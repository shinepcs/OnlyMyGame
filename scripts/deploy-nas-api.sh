#!/usr/bin/env bash
set -euo pipefail

# Manual production command. It deploys committed sources only after all
# server tests and the Release build have passed; no Codex hook invokes it.
repo_root="$(git rev-parse --show-toplevel)"
state_dir="$repo_root/.codex/state"
state_file="$state_dir/nas-api-source.sha256"
config_file="${ONLYMYGAME_NAS_DEPLOY_CONFIG:-$HOME/.config/onlymygame/nas-deploy.env}"

release_files=(
  "Server/OnlyMyGame.Api"
  "Assets/OnlyMyGame/Core/RuleCore.cs"
  "docker-compose.yml"
)
verification_files=(
  "Server/OnlyMyGame.Api.Tests"
  "scripts/verify-server.sh"
)
deployment_guard_files=("${release_files[@]}" "${verification_files[@]}")

# A release must be reproducible from HEAD, including the verifier that grants
# it permission to ship. Untracked files in these directories are rejected too.
if [ -n "$(git -C "$repo_root" status --porcelain -- "${deployment_guard_files[@]}")" ]; then
  echo "NAS deploy refused: API or verification sources have uncommitted changes." >&2
  exit 1
fi

source_hash="$({
  cd "$repo_root"
  git ls-files -z -- "${release_files[@]}" | LC_ALL=C sort -z | xargs -0 shasum -a 256
} | shasum -a 256 | awk '{print $1}')"

if [ "${ONLYMYGAME_FORCE_DEPLOY:-0}" != "1" ] \
  && [ -f "$state_file" ] \
  && [ "$(cat "$state_file")" = "$source_hash" ]; then
  exit 0
fi

if [ ! -r "$config_file" ]; then
  echo "NAS deploy refused: create $config_file from .codex/nas-deploy.env.example." >&2
  exit 1
fi

"$repo_root/scripts/verify-server.sh"

# This local, untracked file contains only the SSH destination and NAS deploy
# root. The SSH private key remains in the user's SSH agent/config.
# shellcheck disable=SC1090
source "$config_file"
: "${NAS_DEPLOY_TARGET:?NAS_DEPLOY_TARGET is required}"
: "${NAS_DEPLOY_PATH:?NAS_DEPLOY_PATH is required}"
: "${NAS_DEPLOY_PORT:=3442}"
: "${NAS_DOCKER_COMPOSE_COMMAND:=/usr/local/bin/docker-compose}"

if [[ ! "$NAS_DEPLOY_TARGET" =~ ^[A-Za-z0-9._@-]+$ ]]; then
  echo "NAS_DEPLOY_TARGET contains unsupported characters." >&2
  exit 1
fi
if [[ ! "$NAS_DEPLOY_PATH" =~ ^/[A-Za-z0-9._/-]+$ ]]; then
  echo "NAS_DEPLOY_PATH must be an absolute path using only safe path characters." >&2
  exit 1
fi
if [[ ! "$NAS_DOCKER_COMPOSE_COMMAND" =~ ^/[A-Za-z0-9._/-]+$ ]]; then
  echo "NAS_DOCKER_COMPOSE_COMMAND must be one absolute executable path." >&2
  exit 1
fi
if [[ ! "$NAS_DEPLOY_PORT" =~ ^[0-9]+$ ]] \
  || [ "$((10#$NAS_DEPLOY_PORT))" -lt 1 ] \
  || [ "$((10#$NAS_DEPLOY_PORT))" -gt 65535 ]; then
  echo "NAS_DEPLOY_PORT must be between 1 and 65535." >&2
  exit 1
fi

expected_api_version="$(sed -nE 's/^[[:space:]]*public const string ApiVersion = "([^"]+)";.*/\1/p' "$repo_root/Server/OnlyMyGame.Api/ApiPolicies.cs")"
expected_compatibility_version="$(sed -nE 's/^[[:space:]]*public const string RuleCompatibilityVersion = "([^"]+)";.*/\1/p' "$repo_root/Server/OnlyMyGame.Api/ApiPolicies.cs")"
if [[ ! "$expected_api_version" =~ ^[A-Za-z0-9._-]+$ ]] \
  || [[ ! "$expected_compatibility_version" =~ ^[A-Za-z0-9._-]+$ ]]; then
  echo "Could not resolve safe API compatibility constants from ApiPolicies.cs." >&2
  exit 1
fi

ssh_options=(-p "$NAS_DEPLOY_PORT" -o StrictHostKeyChecking=yes)
ssh_command=(ssh "${ssh_options[@]}" "$NAS_DEPLOY_TARGET")
if [ -n "${NAS_DEPLOY_PASSWORD_FILE:-}" ]; then
  if [ ! -r "$NAS_DEPLOY_PASSWORD_FILE" ]; then
    echo "NAS_DEPLOY_PASSWORD_FILE must point to a readable file." >&2
    exit 1
  fi
  if ! command -v sshpass >/dev/null 2>&1; then
    echo "NAS deploy requires sshpass when NAS_DEPLOY_PASSWORD_FILE is set." >&2
    exit 1
  fi
  export NAS_DEPLOY_PASSWORD_FILE
  ssh_options+=(-o BatchMode=no)
  ssh_command=(sshpass -f "$NAS_DEPLOY_PASSWORD_FILE" ssh "${ssh_options[@]}" "$NAS_DEPLOY_TARGET")
else
  ssh_options+=(-o BatchMode=yes)
  ssh_command=(ssh "${ssh_options[@]}" "$NAS_DEPLOY_TARGET")
fi

# Package one immutable release archive. Uploading a single file avoids stale
# remote source files when tracked files were deleted between releases.
payload_dir="$(mktemp -d "${TMPDIR:-/tmp}/onlymygame-nas-deploy.XXXXXX")"
trap 'rm -rf "$payload_dir"' EXIT
payload_archive="$payload_dir/onlymygame-api-$source_hash.tar"
git -C "$repo_root" archive --format=tar HEAD \
  Server/OnlyMyGame.Api \
  Assets/OnlyMyGame/Core/RuleCore.cs \
  docker-compose.yml > "$payload_archive"

scp_batch_mode=yes
if [ -n "${NAS_DEPLOY_PASSWORD_FILE:-}" ]; then
  scp_batch_mode=no
fi
scp_options=(-O -P "$NAS_DEPLOY_PORT" -o StrictHostKeyChecking=yes -o BatchMode="$scp_batch_mode")
scp_command=(scp "${scp_options[@]}")
if [ -n "${NAS_DEPLOY_PASSWORD_FILE:-}" ]; then
  scp_command=(sshpass -f "$NAS_DEPLOY_PASSWORD_FILE" "${scp_command[@]}")
fi

remote_deploy_root="$NAS_DEPLOY_PATH/.deploy"
remote_archive="$remote_deploy_root/incoming-$source_hash-$$.tar"
"${ssh_command[@]}" "mkdir -p '$remote_deploy_root/releases'"
"${scp_command[@]}" "$payload_archive" "$NAS_DEPLOY_TARGET:$remote_archive"

# Build from an immutable release directory. If the new container fails its
# health gate, rebuild the previously healthy release before returning failure.
"${ssh_command[@]}" "set -eu
deploy_root='$remote_deploy_root'
release_dir=\"\$deploy_root/releases/$source_hash\"
staging_dir=\"\$release_dir.staging\"
current_link=\"\$deploy_root/current\"
lock_dir=\"\$deploy_root/deploy.lock\"
archive='$remote_archive'
previous_release=''
if ! mkdir \"\$lock_dir\"; then
  rm -f \"\$archive\"
  echo 'NAS deploy refused: another deployment is active (remove a stale .deploy/deploy.lock only after checking).' >&2
  exit 1
fi
cleanup() {
  rm -rf \"\$staging_dir\"
  rm -f \"\$archive\"
  rmdir \"\$lock_dir\" 2>/dev/null || true
}
trap cleanup EXIT
trap 'exit 130' HUP INT TERM
if [ -L \"\$current_link\" ]; then previous_release=\$(readlink \"\$current_link\"); fi
if [ ! -d \"\$release_dir\" ]; then
  rm -rf \"\$staging_dir\"
  mkdir -p \"\$staging_dir\"
  tar -xf \"\$archive\" -C \"\$staging_dir\"
  mv \"\$staging_dir\" \"\$release_dir\"
fi
run_release() {
  cd \"\$1\"
  '$NAS_DOCKER_COMPOSE_COMMAND' -p onlymygame --env-file '$NAS_DEPLOY_PATH/.env' -f docker-compose.yml up -d --build onlymygame-api
}
wait_healthy() {
  attempt=1
  while [ \"\$attempt\" -le 15 ]; do
    health_json=\$(curl --fail --silent --show-error --max-time 5 http://127.0.0.1:8080/health 2>/dev/null || true)
    compact_health=\$(printf '%s' \"\$health_json\" | tr -d '[:space:]')
    status_ok=false
    api_ok=false
    compatibility_ok=false
    case \"\$compact_health\" in *'\"status\":\"ok\"'*) status_ok=true ;; esac
    case \"\$compact_health\" in *'\"apiVersion\":\"$expected_api_version\"'*) api_ok=true ;; esac
    case \"\$compact_health\" in *'\"compatibilityVersion\":\"$expected_compatibility_version\"'*) compatibility_ok=true ;; esac
    if \"\$status_ok\" && \"\$api_ok\" && \"\$compatibility_ok\"; then return 0; fi
    attempt=\$((attempt + 1))
    sleep 2
  done
  return 1
}
if run_release \"\$release_dir\" && wait_healthy; then
  ln -sfn \"\$release_dir\" \"\$current_link\"
  echo 'NAS API deployment passed the health and compatibility gate.'
  exit 0
fi
if [ -n \"\$previous_release\" ] \
  && [ -d \"\$previous_release\" ] \
  && run_release \"\$previous_release\" \
  && wait_healthy; then
  ln -sfn \"\$previous_release\" \"\$current_link\"
  echo 'NAS deploy failed; the previously healthy release was restored.' >&2
  exit 1
fi
echo 'CRITICAL: NAS deploy failed and no healthy previous release could be restored.' >&2
exit 1"

mkdir -p "$state_dir"
printf '%s\n' "$source_hash" > "$state_file"

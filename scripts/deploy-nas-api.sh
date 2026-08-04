#!/usr/bin/env bash
set -euo pipefail

# Manual production command. It deploys committed sources only after all
# server tests and the Release build have passed; no Codex hook invokes it.
repo_root="$(git rev-parse --show-toplevel)"
config_file="${ONLYMYGAME_NAS_DEPLOY_CONFIG:-$HOME/.config/onlymygame/nas-deploy.env}"
gc_requested="${ONLYMYGAME_GC_ROLLBACKS:-0}"
attestation_repository="shinepcs/OnlyMyGame"
attestation_signer_workflow="shinepcs/OnlyMyGame/.github/workflows/server-container.yml"

# ONLYMYGAME_PRIVATE_FILE_POLICY_BEGIN
private_file_mode() {
  local file_path=$1
  local mode=''

  if mode=$(stat -f '%Lp' "$file_path" 2>/dev/null); then
    :
  elif mode=$(stat -c '%a' "$file_path" 2>/dev/null); then
    :
  else
    return 1
  fi

  [[ "$mode" =~ ^[0-7]{3,4}$ ]] || return 1
  printf '%s\n' "$mode"
}

require_private_local_file() {
  local label=$1
  local file_path=$2
  local mode=''
  local numeric_mode=0

  if [ ! -f "$file_path" ] || [ -L "$file_path" ] || [ ! -r "$file_path" ]; then
    echo "$label must point to a readable, non-symlink regular file." >&2
    return 1
  fi
  if ! mode=$(private_file_mode "$file_path"); then
    echo "$label permissions could not be inspected." >&2
    return 1
  fi

  numeric_mode=$((8#$mode))
  if (( (numeric_mode & 07177) != 0 || (numeric_mode & 0400) == 0 )); then
    echo "$label must have mode 0600 or stricter read-only mode 0400." >&2
    return 1
  fi
}
# ONLYMYGAME_PRIVATE_FILE_POLICY_END

release_files=(
  "Server/OnlyMyGame.Api"
  "Assets/OnlyMyGame/Core/RuleCore.cs"
  "Assets/OnlyMyGame/Core/RuleExpressions.cs"
  "Assets/OnlyMyGame/Core/DynamicActionTargeting.cs"
  "docker-compose.yml"
)
verification_files=(
  "Server/OnlyMyGame.Api.Tests"
  "scripts/verify-server.sh"
  "scripts/deploy-nas-api.sh"
  "scripts/compute-api-source-hash.py"
  "scripts/evaluate-rules.py"
  ".dockerignore"
  ".github/workflows/server-container.yml"
)
deployment_guard_files=("${release_files[@]}" "${verification_files[@]}")

# A release must be reproducible from HEAD, including the verifier that grants
# it permission to ship. Untracked files in these directories are rejected too.
if [ -n "$(git -C "$repo_root" status --porcelain -- "${deployment_guard_files[@]}")" ]; then
  echo "NAS deploy refused: API or verification sources have uncommitted changes." >&2
  exit 1
fi

source_commit="$(git -C "$repo_root" rev-parse HEAD)"
if [[ ! "$source_commit" =~ ^[0-9a-f]{40}$ ]]; then
  echo "NAS deploy refused: current HEAD is not a full Git commit SHA." >&2
  exit 1
fi
source_hash="$(python3 "$repo_root/scripts/compute-api-source-hash.py" --commit "$source_commit")"
if [[ ! "$source_hash" =~ ^[0-9a-f]{64}$ ]]; then
  echo "NAS deploy refused: API source identity is invalid." >&2
  exit 1
fi

if ! require_private_local_file "NAS deploy config $config_file" "$config_file"; then
  echo "NAS deploy refused: create a private config from .codex/nas-deploy.env.example." >&2
  exit 1
fi

# This local, untracked file contains the SSH destination, deploy root, and the
# immutable registry digest. The SSH private key remains in the user's agent.
# shellcheck disable=SC1090
source "$config_file"
: "${NAS_DEPLOY_TARGET:?NAS_DEPLOY_TARGET is required}"
: "${NAS_DEPLOY_PATH:?NAS_DEPLOY_PATH is required}"
: "${NAS_API_IMAGE:?NAS_API_IMAGE must be a full image@sha256 digest}"
: "${NAS_DEPLOY_PORT:=3442}"
: "${NAS_DOCKER_COMPOSE_COMMAND:=/usr/local/bin/docker-compose}"
: "${NAS_DOCKER_COMMAND:=/usr/local/bin/docker}"
: "${NAS_STAT_COMMAND:=/usr/bin/stat}"
: "${NAS_ROLLBACK_KEEP:=3}"

if [[ ! "$NAS_DEPLOY_TARGET" =~ ^[A-Za-z0-9._@-]+$ ]]; then
  echo "NAS_DEPLOY_TARGET contains unsupported characters." >&2
  exit 1
fi
if [[ ! "$NAS_DEPLOY_PATH" =~ ^/[A-Za-z0-9._/-]+$ ]]; then
  echo "NAS_DEPLOY_PATH must be an absolute path using only safe path characters." >&2
  exit 1
fi
if [[ ! "$NAS_API_IMAGE" =~ ^[A-Za-z0-9][A-Za-z0-9._:/-]*@sha256:[0-9a-f]{64}$ ]]; then
  echo "NAS_API_IMAGE must be a full immutable image@sha256 digest." >&2
  exit 1
fi
for command_path in "$NAS_DOCKER_COMPOSE_COMMAND" "$NAS_DOCKER_COMMAND" "$NAS_STAT_COMMAND"; do
  if [[ ! "$command_path" =~ ^/[A-Za-z0-9._/-]+$ ]]; then
    echo "NAS Docker, Compose, and stat commands must each be one absolute executable path." >&2
    exit 1
  fi
done
if [[ ! "$NAS_DEPLOY_PORT" =~ ^[0-9]+$ ]] \
  || [ "$((10#$NAS_DEPLOY_PORT))" -lt 1 ] \
  || [ "$((10#$NAS_DEPLOY_PORT))" -gt 65535 ]; then
  echo "NAS_DEPLOY_PORT must be between 1 and 65535." >&2
  exit 1
fi
if [[ ! "$NAS_ROLLBACK_KEEP" =~ ^[0-9]+$ ]] \
  || [ "$((10#$NAS_ROLLBACK_KEEP))" -lt 1 ] \
  || [ "$((10#$NAS_ROLLBACK_KEEP))" -gt 20 ]; then
  echo "NAS_ROLLBACK_KEEP must be between 1 and 20." >&2
  exit 1
fi
if [ "$gc_requested" != "0" ] && [ "$gc_requested" != "1" ]; then
  echo "ONLYMYGAME_GC_ROLLBACKS must be 0 or 1." >&2
  exit 1
fi
if [ -n "${NAS_DEPLOY_PASSWORD_FILE:-}" ]; then
  if ! require_private_local_file "NAS_DEPLOY_PASSWORD_FILE" "$NAS_DEPLOY_PASSWORD_FILE"; then
    echo "NAS deploy refused: the configured password file is not private." >&2
    exit 1
  fi
fi

"$repo_root/scripts/verify-server.sh"

# The verifier above runs from the worktree. Refuse the release if another
# process changed either HEAD or any guarded source while that gate was in
# flight; all packaging below is already pinned to source_commit.
if [ "$(git -C "$repo_root" rev-parse HEAD)" != "$source_commit" ] \
  || [ -n "$(git -C "$repo_root" status --porcelain -- "${deployment_guard_files[@]}")" ]; then
  echo "NAS deploy refused: guarded sources changed while verification was running." >&2
  exit 1
fi

# The immutable digest must carry GitHub's signed provenance from this exact
# repository workflow. This check contacts GHCR but never mutates the registry.
if ! command -v gh >/dev/null 2>&1; then
  echo "NAS deploy refused: GitHub CLI is required for image attestation verification." >&2
  exit 1
fi
if ! gh attestation verify "oci://$NAS_API_IMAGE" \
  --repo "$attestation_repository" \
  --signer-workflow "$attestation_signer_workflow" \
  --source-ref refs/heads/main \
  --deny-self-hosted-runners >/dev/null; then
  echo "NAS deploy refused: image provenance attestation is missing or untrusted." >&2
  exit 1
fi

api_policies_source="$(git -C "$repo_root" show "$source_commit:Server/OnlyMyGame.Api/ApiPolicies.cs")"
expected_api_version="$(printf '%s\n' "$api_policies_source" | sed -nE 's/^[[:space:]]*public const string ApiVersion = "([^"]+)";.*/\1/p')"
expected_compatibility_version="$(printf '%s\n' "$api_policies_source" | sed -nE 's/^[[:space:]]*public const string RuleCompatibilityVersion = "([^"]+)";.*/\1/p')"
if [[ ! "$expected_api_version" =~ ^[A-Za-z0-9._-]+$ ]] \
  || [[ ! "$expected_compatibility_version" =~ ^[A-Za-z0-9._-]+$ ]]; then
  echo "Could not resolve safe API compatibility constants from ApiPolicies.cs." >&2
  exit 1
fi

ssh_options=(-p "$NAS_DEPLOY_PORT" -o StrictHostKeyChecking=yes)
ssh_command=(ssh "${ssh_options[@]}" "$NAS_DEPLOY_TARGET")
if [ -n "${NAS_DEPLOY_PASSWORD_FILE:-}" ]; then
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
git -C "$repo_root" archive --format=tar "$source_commit" \
  Server/OnlyMyGame.Api \
  Assets/OnlyMyGame/Core/RuleCore.cs \
  Assets/OnlyMyGame/Core/RuleExpressions.cs \
  Assets/OnlyMyGame/Core/DynamicActionTargeting.cs \
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

# The quoted heredoc is intentionally literal. Every value crosses the SSH
# boundary as a validated positional argument instead of executable shell text.
"${ssh_command[@]}" "sh -s -- '$remote_deploy_root' '$source_hash' '$remote_archive' '$NAS_DOCKER_COMMAND' '$NAS_DOCKER_COMPOSE_COMMAND' '$NAS_DEPLOY_PATH' '$expected_api_version' '$expected_compatibility_version' '$NAS_API_IMAGE' '$NAS_STAT_COMMAND' '$gc_requested' '$NAS_ROLLBACK_KEEP' '$source_commit'" <<'ONLYMYGAME_REMOTE_DEPLOY'
set -eu
umask 077

deploy_root=$1
source_hash=$2
archive=$3
docker_command=$4
compose_command=$5
nas_deploy_path=$6
expected_api_version=$7
expected_compatibility_version=$8
target_image=$9
stat_command=${10}
gc_requested=${11}
rollback_keep=${12}
source_commit=${13}

release_root="$deploy_root/releases"
transaction_root="$deploy_root/transactions"
release_dir="$release_root/$source_hash"
staging_dir="$release_dir.staging"
current_link="$deploy_root/current"
current_next="$deploy_root/current.next.$$"
lock_dir="$deploy_root/deploy.lock"
env_file="$nas_deploy_path/.env"
transaction_dir=''
target_override=''
rollback_override=''
rollback_tag=''
previous_release=''
previous_container_id=''
previous_image_id=''
previous_api_version=''
previous_compatibility_version=''
previous_manifest_mode=''
mutation_started=0
backup_ready=0
database_may_have_changed=0
deployment_committed=0
recovery_attempted=0
preserve_lock=0

write_value() {
  value_path=$1
  value=$2
  printf '%s\n' "$value" > "$value_path.tmp"
  mv "$value_path.tmp" "$value_path"
}

set_state() {
  [ -n "$transaction_dir" ] || return 0
  write_value "$transaction_dir/state" "$1"
}

compose_with() {
  data_mode=$1
  shift
  ONLYMYGAME_API_IMAGE="$target_image" \
  ONLYMYGAME_BACKUP_DIR="$transaction_dir" \
  ONLYMYGAME_DATA_MODE="$data_mode" \
    "$compose_command" -p onlymygame --env-file "$env_file" "$@"
}

compose_target() {
  data_mode=$1
  shift
  compose_with "$data_mode" \
    -f "$release_dir/docker-compose.yml" \
    -f "$target_override" \
    "$@"
}

compose_previous() {
  compose_with permissions \
    -f "$previous_release/docker-compose.yml" \
    -f "$rollback_override" \
    "$@"
}

running_service_ids() {
  "$docker_command" ps \
    --filter label=com.docker.compose.project=onlymygame \
    --filter label=com.docker.compose.service=onlymygame-api \
    --format '{{.ID}}'
}

wait_healthy() {
  expected_api=$1
  expected_compatibility=$2
  manifest_mode=$3
  attempt=1
  while [ "$attempt" -le 15 ]; do
    health_json=$(curl --fail --silent --show-error --max-time 5 http://127.0.0.1:8080/health 2>/dev/null || true)
    compact_health=$(printf '%s' "$health_json" | tr -d '[:space:]')
    actual_status=$(printf '%s' "$compact_health" | sed -n 's/.*"status":"\([A-Za-z]*\)".*/\1/p')
    actual_api=$(printf '%s' "$compact_health" | sed -n 's/.*"apiVersion":"\([A-Za-z0-9._-]*\)".*/\1/p')
    actual_compatibility=$(printf '%s' "$compact_health" | sed -n 's/.*"compatibilityVersion":"\([A-Za-z0-9._-]*\)".*/\1/p')
    if [ "$actual_status" = ok ]; then
      if [ "$manifest_mode" = legacy ]; then
        return 0
      fi
      if [ "$manifest_mode" = strict ] \
        && [ "$actual_api" = "$expected_api" ] \
        && [ "$actual_compatibility" = "$expected_compatibility" ]; then
        return 0
      fi
    fi
    attempt=$((attempt + 1))
    sleep 2
  done
  return 1
}

switch_current() {
  rm -f "$current_next"
  ln -s "$1" "$current_next"
  rm -f "$current_link"
  mv "$current_next" "$current_link"
}

verify_cutover_precondition() {
  if [ -z "$previous_release" ]; then
    [ ! -e "$current_link" ] && [ ! -L "$current_link" ] && [ -z "$(running_service_ids)" ]
    return
  fi

  wait_healthy "$previous_api_version" "$previous_compatibility_version" "$previous_manifest_mode" || return 1
  [ -L "$current_link" ] || return 1
  current_target=$(readlink "$current_link") || return 1
  case "$current_target" in
    /*) ;;
    *) current_target="$deploy_root/$current_target" ;;
  esac
  [ "$current_target" = "$previous_release" ] || return 1
  [ "$(running_service_ids)" = "$previous_container_id" ] || return 1
  [ "$("$docker_command" inspect --format '{{.Image}}' "$previous_container_id")" = "$previous_image_id" ] || return 1
}

stop_deployed_target_if_present() {
  recovery_ids=$(running_service_ids) || return 1
  [ -n "$recovery_ids" ] || return 0
  printf '%s\n' "$recovery_ids" | grep -Eq '^[0-9a-f]{12,64}$' || return 1
  recovery_image=$("$docker_command" inspect --format '{{.Image}}' "$recovery_ids") || return 1
  recovery_configs=$("$docker_command" inspect --format '{{ index .Config.Labels "com.docker.compose.project.config_files" }}' "$recovery_ids") || return 1

  # A failed stop may have left the exact old container running. It is safe to
  # leave alone only before any database mutation; after that point Compose may
  # have reused the stopped old container for the target, so cold restore must
  # stop it first. Never stop an unattributed container.
  if [ -n "$previous_container_id" ] \
    && [ "$recovery_ids" = "$previous_container_id" ] \
    && [ "$recovery_image" = "$previous_image_id" ]; then
    if [ "$database_may_have_changed" -eq 0 ]; then
      return 0
    fi
    "$docker_command" stop "$recovery_ids" >/dev/null || return 1
    [ -z "$(running_service_ids)" ]
    return
  fi
  case "$recovery_configs" in
    *"$target_override"*) ;;
    *) echo 'CRITICAL: refusing to stop an unattributed replacement container.' >&2; return 1 ;;
  esac
  [ "$recovery_image" = "$target_image_id" ] || return 1
  "$docker_command" stop "$recovery_ids" >/dev/null || return 1
  [ -z "$(running_service_ids)" ]
}

restore_previous() {
  set_state RECOVERING || true
  if ! stop_deployed_target_if_present; then
    return 1
  fi

  if [ "$database_may_have_changed" -eq 1 ]; then
    if [ "$backup_ready" -ne 1 ]; then
      echo 'CRITICAL: database mutation began without a verified cold backup.' >&2
      return 1
    fi
    if ! compose_target restore run --rm --no-deps onlymygame-data-init; then
      echo 'CRITICAL: SQLite backup restoration or integrity verification failed.' >&2
      return 1
    fi
  fi

  if [ -z "$previous_release" ] \
    || [ ! -d "$previous_release" ] \
    || [ -z "$rollback_tag" ] \
    || [ ! -f "$rollback_override" ]; then
    set_state RESTORED_NO_PREVIOUS || true
    echo 'CRITICAL: data was restored, but no previous service release exists.' >&2
    return 1
  fi

  if ! compose_previous up -d --no-build --no-deps onlymygame-api; then
    echo 'CRITICAL: exact previous image could not be started.' >&2
    return 1
  fi
  if ! wait_healthy "$previous_api_version" "$previous_compatibility_version" "$previous_manifest_mode"; then
    echo 'CRITICAL: exact previous image did not regain its pinned health contract.' >&2
    return 1
  fi
  if ! switch_current "$previous_release"; then
    echo 'CRITICAL: previous release restarted but current could not be restored.' >&2
    return 1
  fi
  set_state ROLLED_BACK || return 1
  echo 'NAS deploy failed; SQLite data and the exact previous image were restored.' >&2
  return 0
}

cleanup_nonpersistent() {
  rm -rf "$staging_dir"
  rm -f "$archive"
  rm -f "$current_next"
  if [ "$preserve_lock" -eq 0 ]; then
    rm -f "$lock_dir/owner-pid" "$lock_dir/active-transaction"
    rmdir "$lock_dir" 2>/dev/null || true
  fi
}

on_exit() {
  original_status=$?
  final_status=$original_status
  trap - 0
  trap '' HUP INT TERM
  set +e

  if [ "$original_status" -ne 0 ] \
    && [ "$mutation_started" -eq 1 ] \
    && [ "$deployment_committed" -eq 0 ] \
    && [ "$recovery_attempted" -eq 0 ]; then
    recovery_attempted=1
    if ! restore_previous; then
      set_state CRITICAL || true
      preserve_lock=1
      final_status=70
      echo "CRITICAL: recovery is incomplete; inspect $transaction_dir and keep $lock_dir in place." >&2
    fi
  fi

  cleanup_nonpersistent
  exit "$final_status"
}

on_signal() {
  signal_name=$1
  signal_status=$2
  if [ -n "$transaction_dir" ]; then
    write_value "$transaction_dir/last-signal" "$signal_name" || true
  fi
  exit "$signal_status"
}

run_explicit_gc() {
  [ "$gc_requested" = 1 ] || return 0
  kept=0
  for candidate in $(find "$transaction_root" -mindepth 1 -maxdepth 1 -type d -print | LC_ALL=C sort -r); do
    [ "$candidate" != "$transaction_dir" ] || {
      kept=$((kept + 1))
      continue
    }
    [ -f "$candidate/state" ] || continue
    [ "$(cat "$candidate/state" 2>/dev/null)" = COMMITTED ] || continue
    kept=$((kept + 1))
    [ "$kept" -gt "$rollback_keep" ] || continue
    case "$candidate" in
      "$transaction_root"/*) ;;
      *) continue ;;
    esac
    [ -f "$candidate/rollback-tag" ] || continue
    candidate_tag=$(cat "$candidate/rollback-tag" 2>/dev/null)
    printf '%s\n' "$candidate_tag" | grep -Eq '^onlymygame-api:rollback-[0-9]{14}-[0-9a-f]{64}-[0-9]+$' || continue
    if [ -n "$("$docker_command" ps -a --filter "ancestor=$candidate_tag" -q 2>/dev/null)" ]; then
      continue
    fi
    if "$docker_command" image rm "$candidate_tag" >/dev/null 2>&1; then
      rm -rf "$candidate"
    fi
  done
}

mkdir -p "$release_root" "$transaction_root"
if ! mkdir "$lock_dir"; then
  rm -f "$archive"
  echo 'NAS deploy refused: another deployment or an incomplete critical recovery holds .deploy/deploy.lock.' >&2
  exit 1
fi
write_value "$lock_dir/owner-pid" "$$"
trap on_exit 0
trap 'on_signal HUP 129' HUP
trap 'on_signal INT 130' INT
trap 'on_signal TERM 143' TERM

# Secrets must come from a non-symlink regular file owned by the deploy account.
# Only owner-readable files are accepted; group/other permissions fail closed.
if [ ! -f "$env_file" ] || [ -L "$env_file" ] || [ ! -r "$env_file" ]; then
  echo "NAS deploy refused: $env_file must be a readable regular non-symlink file." >&2
  exit 1
fi
env_metadata=$("$stat_command" -c '%u:%a' "$env_file") || {
  echo 'NAS deploy refused: could not inspect .env ownership and mode.' >&2
  exit 1
}
env_owner=${env_metadata%%:*}
env_mode=${env_metadata#*:}
if [ "$env_owner" != "$(id -u)" ] || { [ "$env_mode" != 400 ] && [ "$env_mode" != 600 ]; }; then
  echo 'NAS deploy refused: .env must be owned by the deploy account with mode 0400 or 0600.' >&2
  exit 1
fi

# Docker Compose v1.29.2+ and every v2 release support the flags and merge
# behavior used by the exact-image rollback path.
compose_version=$("$compose_command" version --short 2>/dev/null | sed -n '1{s/^v//;s/[^0-9.].*$//;p;}')
printf '%s\n' "$compose_version" | grep -Eq '^[0-9]+\.[0-9]+(\.[0-9]+)?$' || {
  echo 'NAS deploy refused: could not determine Docker Compose version.' >&2
  exit 1
}
compose_major=${compose_version%%.*}
compose_remainder=${compose_version#*.}
compose_minor=${compose_remainder%%.*}
if [ "$compose_major" -lt 1 ] \
  || { [ "$compose_major" -eq 1 ] && [ "$compose_minor" -lt 29 ]; }; then
  echo 'NAS deploy refused: Docker Compose v1.29.2+ or v2 is required.' >&2
  exit 1
fi
if [ "$compose_major" -eq 1 ] && [ "$compose_minor" -eq 29 ]; then
  compose_patch=${compose_remainder#*.}
  [ "$compose_patch" != "$compose_remainder" ] || compose_patch=0
  if [ "$compose_patch" -lt 2 ]; then
    echo 'NAS deploy refused: Docker Compose v1.29.2+ or v2 is required.' >&2
    exit 1
  fi
fi

if [ ! -d "$release_dir" ]; then
  rm -rf "$staging_dir"
  mkdir -p "$staging_dir"
  tar -xf "$archive" -C "$staging_dir"
  mv "$staging_dir" "$release_dir"
fi
if [ -L "$release_dir" ] \
  || [ ! -f "$release_dir/docker-compose.yml" ] \
  || [ ! -d "$release_dir/Server/OnlyMyGame.Api" ]; then
  echo 'NAS deploy refused: immutable release directory is missing or invalid.' >&2
  exit 1
fi

transaction_id="$(date -u +%Y%m%d%H%M%S)-$source_hash-$$"
transaction_dir="$transaction_root/$transaction_id"
mkdir "$transaction_dir"
chmod 0700 "$transaction_dir"
write_value "$lock_dir/active-transaction" "$transaction_dir"
set_state PREPARING
write_value "$transaction_dir/source-hash" "$source_hash"
write_value "$transaction_dir/source-commit" "$source_commit"
write_value "$transaction_dir/target-image" "$target_image"
write_value "$transaction_dir/backup-path" "$transaction_dir/onlymygame.db"

target_override="$transaction_dir/target-compose.override.yml"
{
  printf '%s\n' 'services:'
  printf '%s\n' '  onlymygame-data-init:'
  printf '    image: %s\n' "$target_image"
  printf '%s\n' '  onlymygame-api:'
  printf '    image: %s\n' "$target_image"
} > "$target_override.tmp"
mv "$target_override.tmp" "$target_override"

if [ -e "$current_link" ] && [ ! -L "$current_link" ]; then
  echo 'NAS deploy refused: .deploy/current exists but is not a symlink.' >&2
  exit 1
fi
if [ -L "$current_link" ]; then
  previous_release=$(readlink "$current_link")
  case "$previous_release" in
    /*) ;;
    *) previous_release="$deploy_root/$previous_release" ;;
  esac
  previous_basename=${previous_release##*/}
  case "$previous_release" in
    "$release_root"/*) ;;
    *) echo 'NAS deploy refused: current points outside the immutable release root.' >&2; exit 1 ;;
  esac
  [ "$previous_release" = "$release_root/$previous_basename" ] || {
    echo 'NAS deploy refused: current must point directly to one immutable release.' >&2
    exit 1
  }
  printf '%s\n' "$previous_basename" | grep -Eq '^[0-9a-f]{64}$' || {
    echo 'NAS deploy refused: current does not identify one immutable source release.' >&2
    exit 1
  }
  [ -d "$previous_release" ] || {
    echo 'NAS deploy refused: current points to a missing release.' >&2
    exit 1
  }
fi

running_ids=$(running_service_ids)
if [ -n "$running_ids" ]; then
  running_count=$(printf '%s\n' "$running_ids" | sed '/^$/d' | wc -l | tr -d '[:space:]')
  if [ "$running_count" -ne 1 ] \
    || ! printf '%s\n' "$running_ids" | grep -Eq '^[0-9a-f]{12,64}$'; then
    echo 'NAS deploy refused: expected exactly one running onlymygame API container.' >&2
    exit 1
  fi
fi
if [ -z "$previous_release" ] && [ -n "$running_ids" ]; then
  echo 'NAS deploy refused: a legacy API is running without .deploy/current; adopt and pin it explicitly before deployment.' >&2
  exit 1
fi
if [ -n "$previous_release" ] && [ -z "$running_ids" ]; then
  echo 'NAS deploy refused: current exists but its API is not running and cannot be health-pinned.' >&2
  exit 1
fi

if [ -n "$previous_release" ]; then
  previous_container_id=$running_ids
  previous_config_files=$("$docker_command" inspect --format '{{ index .Config.Labels "com.docker.compose.project.config_files" }}' "$previous_container_id") || exit 1
  previous_working_dir=$("$docker_command" inspect --format '{{ index .Config.Labels "com.docker.compose.project.working_dir" }}' "$previous_container_id") || exit 1
  case "$previous_config_files" in
    *"$previous_release/docker-compose.yml"*) ;;
    *)
      if [ "$previous_working_dir" != "$previous_release" ]; then
        echo 'NAS deploy refused: running container is not attributable to .deploy/current.' >&2
        exit 1
      fi
      ;;
  esac

  previous_image_id=$("$docker_command" inspect --format '{{.Image}}' "$previous_container_id") || exit 1
  printf '%s\n' "$previous_image_id" | grep -Eq '^sha256:[0-9a-f]{64}$' || {
    echo 'NAS deploy refused: running image ID is not immutable.' >&2
    exit 1
  }
  previous_health_json=$(curl --fail --silent --show-error --max-time 5 http://127.0.0.1:8080/health) || {
    echo 'NAS deploy refused: previous API is not healthy enough to pin.' >&2
    exit 1
  }
  previous_health=$(printf '%s' "$previous_health_json" | tr -d '[:space:]')
  previous_status=$(printf '%s' "$previous_health" | sed -n 's/.*"status":"\([A-Za-z]*\)".*/\1/p')
  previous_api_version=$(printf '%s' "$previous_health" | sed -n 's/.*"apiVersion":"\([A-Za-z0-9._-]*\)".*/\1/p')
  previous_compatibility_version=$(printf '%s' "$previous_health" | sed -n 's/.*"compatibilityVersion":"\([A-Za-z0-9._-]*\)".*/\1/p')
  [ "$previous_status" = ok ] || {
    echo 'NAS deploy refused: previous API health status is not ok.' >&2
    exit 1
  }
  previous_api_present=false
  previous_compatibility_present=false
  case "$previous_health" in *'"apiVersion":'*) previous_api_present=true ;; esac
  case "$previous_health" in *'"compatibilityVersion":'*) previous_compatibility_present=true ;; esac
  if ! "$previous_api_present" && ! "$previous_compatibility_present"; then
    previous_manifest_mode=legacy
  elif "$previous_api_present" && "$previous_compatibility_present" \
    && printf '%s\n' "$previous_api_version" | grep -Eq '^[A-Za-z0-9._-]+$' \
    && printf '%s\n' "$previous_compatibility_version" | grep -Eq '^[A-Za-z0-9._-]+$'; then
    previous_manifest_mode=strict
  else
    echo 'NAS deploy refused: previous health manifest is incomplete or malformed.' >&2
    exit 1
  fi

  rollback_tag="onlymygame-api:rollback-$transaction_id"
  "$docker_command" image tag "$previous_image_id" "$rollback_tag"
  write_value "$transaction_dir/rollback-tag" "$rollback_tag"
  rollback_override="$transaction_dir/rollback-compose.override.yml"
  {
    printf '%s\n' 'services:'
    printf '%s\n' '  onlymygame-api:'
    printf '    image: %s\n' "$rollback_tag"
  } > "$rollback_override.tmp"
  mv "$rollback_override.tmp" "$rollback_override"

  # Prove the old compose graph plus exact-image overlay remains runnable before
  # the service is stopped. This catches unsupported or malformed legacy files.
  previous_services=$(compose_previous config --services) || {
    echo 'NAS deploy refused: exact previous rollback compose graph is invalid.' >&2
    exit 1
  }
  printf '%s\n' "$previous_services" | grep -qx onlymygame-api || {
    echo 'NAS deploy refused: exact previous rollback graph lacks onlymygame-api.' >&2
    exit 1
  }
fi

{
  printf 'transaction=%s\n' "$transaction_id"
  printf 'source_hash=%s\n' "$source_hash"
  printf 'source_commit=%s\n' "$source_commit"
  printf 'target_image=%s\n' "$target_image"
  printf 'target_api_version=%s\n' "$expected_api_version"
  printf 'target_compatibility_version=%s\n' "$expected_compatibility_version"
  printf 'previous_release=%s\n' "$previous_release"
  printf 'previous_container_id=%s\n' "$previous_container_id"
  printf 'previous_image_id=%s\n' "$previous_image_id"
  printf 'previous_api_version=%s\n' "$previous_api_version"
  printf 'previous_compatibility_version=%s\n' "$previous_compatibility_version"
  printf 'previous_manifest_mode=%s\n' "$previous_manifest_mode"
  printf 'rollback_tag=%s\n' "$rollback_tag"
  printf 'sqlite_backup=%s\n' "$transaction_dir/onlymygame.db"
} > "$transaction_dir/manifest.tmp"
mv "$transaction_dir/manifest.tmp" "$transaction_dir/manifest"

# Interpolate and merge the exact image into both services, pull it before the
# outage, and reject images that cannot run the non-root API or SQLite helper.
target_services=$(compose_target permissions config --services) || {
  echo 'NAS deploy refused: target Compose graph is invalid.' >&2
  exit 1
}
printf '%s\n' "$target_services" | grep -qx onlymygame-data-init || {
  echo 'NAS deploy refused: target graph lacks onlymygame-data-init.' >&2
  exit 1
}
printf '%s\n' "$target_services" | grep -qx onlymygame-api || {
  echo 'NAS deploy refused: target graph lacks onlymygame-api.' >&2
  exit 1
}
compose_target permissions pull onlymygame-data-init onlymygame-api
target_image_id=$("$docker_command" image inspect --format '{{.Id}}' "$target_image")
printf '%s\n' "$target_image_id" | grep -Eq '^sha256:[0-9a-f]{64}$' || {
  echo 'NAS deploy refused: pulled target did not resolve to an immutable image ID.' >&2
  exit 1
}
target_user=$("$docker_command" image inspect --format '{{.Config.User}}' "$target_image")
target_uid=${target_user%%:*}
printf '%s\n' "$target_uid" | grep -Eq '^[0-9]+$' || {
  echo 'NAS deploy refused: target image does not declare a numeric non-root USER.' >&2
  exit 1
}
[ "$target_uid" -gt 0 ] || {
  echo 'NAS deploy refused: target image declares root as its runtime USER.' >&2
  exit 1
}
target_revision=$("$docker_command" image inspect --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}' "$target_image")
printf '%s\n' "$target_revision" | grep -Eq '^[0-9a-f]{40}$' || {
  echo 'NAS deploy refused: target image has no full source revision label.' >&2
  exit 1
}
target_source_hash=$("$docker_command" image inspect --format '{{ index .Config.Labels "io.onlymygame.source-hash" }}' "$target_image")
[ "$target_source_hash" = "$source_hash" ] || {
  echo 'NAS deploy refused: target image API source identity does not match this release.' >&2
  exit 1
}
write_value "$transaction_dir/target-image-revision" "$target_revision"
write_value "$transaction_dir/target-image-source-hash" "$target_source_hash"
{
  cat "$transaction_dir/manifest"
  printf 'target_image_revision=%s\n' "$target_revision"
  printf 'target_image_source_hash=%s\n' "$target_source_hash"
} > "$transaction_dir/manifest.with-image.tmp"
mv "$transaction_dir/manifest.with-image.tmp" "$transaction_dir/manifest"
"$docker_command" run --rm --network none --read-only --entrypoint /usr/bin/sqlite3 "$target_image" --version >/dev/null
set_state READY

# From this assignment onward, every ordinary error and HUP/INT/TERM reaches
# the single re-entry-safe EXIT recovery path. The flag is set before stop so a
# partially completed stop is also recovered.
if ! verify_cutover_precondition; then
  echo 'NAS deploy refused: current container, image, or health changed during preflight.' >&2
  exit 1
fi
write_value "$transaction_dir/mutation-started" 1
mutation_started=1
set_state STOPPING
if [ -n "$previous_container_id" ]; then
  "$docker_command" stop "$previous_container_id" >/dev/null
fi
if [ -n "$(running_service_ids)" ]; then
  echo 'NAS deploy failed: API still appears to be running after stop.' >&2
  exit 1
fi
set_state STOPPED

# This is a cold SQLite backup: the API is stopped, source and backup both pass
# integrity_check, and the backup plus checksum remain in the transaction dir.
compose_target backup run --rm --no-deps onlymygame-data-init
write_value "$transaction_dir/backup-ready" 1
backup_ready=1
set_state BACKED_UP

write_value "$transaction_dir/database-may-have-changed" 1
database_may_have_changed=1
set_state INITIALIZING
compose_target permissions run --rm --no-deps onlymygame-data-init
set_state INITIALIZED

compose_target permissions up -d --no-build --no-deps onlymygame-api
set_state TARGET_STARTED
if ! wait_healthy "$expected_api_version" "$expected_compatibility_version" strict; then
  echo 'NAS deploy failed: target did not pass the strict health contract.' >&2
  exit 1
fi
set_state TARGET_HEALTHY

# Mask termination only across the tiny commit point. Any command error still
# exits and recovers the previous release; a signal before or after is handled.
trap '' HUP INT TERM
switch_current "$release_dir"
set_state COMMITTED
write_value "$transaction_dir/committed" 1
deployment_committed=1
trap 'on_signal HUP 129' HUP
trap 'on_signal INT 130' INT
trap 'on_signal TERM 143' TERM

# Rollback tags, overlays, manifests, and SQLite backups are never removed by
# ordinary deploy cleanup. GC is explicit, post-commit, and only removes old
# COMMITTED transactions whose rollback image is unused.
if ! run_explicit_gc; then
  echo 'NAS API deployment succeeded, but explicit rollback GC did not complete.' >&2
fi
echo 'NAS API deployment passed immutable-image, SQLite backup, and health gates.'
exit 0
ONLYMYGAME_REMOTE_DEPLOY

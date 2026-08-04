#!/usr/bin/env bash
set -euo pipefail

# Codex invokes this from the Stop hook. Only deploy after files used by the
# NAS API image have changed since the last successful deployment.
repo_root="$(git rev-parse --show-toplevel)"
state_dir="$repo_root/.codex/state"
state_file="$state_dir/nas-api-source.sha256"
config_file="${ONLYMYGAME_NAS_DEPLOY_CONFIG:-$HOME/.config/onlymygame/nas-deploy.env}"

source_files=(
  "Server/OnlyMyGame.Api"
  "Assets/OnlyMyGame/Core/RuleCore.cs"
  "docker-compose.yml"
)

source_hash="$({
  cd "$repo_root"
  find "${source_files[@]}" -type f -print0 | LC_ALL=C sort -z | xargs -0 shasum -a 256
} | shasum -a 256 | awk '{print $1}')"

if [ -f "$state_file" ] && [ "$(cat "$state_file")" = "$source_hash" ]; then
  exit 0
fi

if [ ! -r "$config_file" ]; then
  echo "NAS deploy skipped: create $config_file from .codex/nas-deploy.env.example." >&2
  exit 0
fi

# This local, untracked file contains only the SSH destination and the NAS
# checkout path. The SSH private key remains in the user's SSH agent/config.
# shellcheck disable=SC1090
source "$config_file"
: "${NAS_DEPLOY_TARGET:?NAS_DEPLOY_TARGET is required}"
: "${NAS_DEPLOY_PATH:?NAS_DEPLOY_PATH is required}"
: "${NAS_DEPLOY_PORT:=3442}"
: "${NAS_DOCKER_COMPOSE_COMMAND:=/usr/local/bin/docker-compose}"

ssh_options=(-p "$NAS_DEPLOY_PORT" -o StrictHostKeyChecking=accept-new)
ssh_command=(ssh "${ssh_options[@]}" "$NAS_DEPLOY_TARGET")
if [ -n "${NAS_DEPLOY_PASSWORD_FILE:-}" ]; then
  : "${NAS_DEPLOY_PASSWORD_FILE:?NAS_DEPLOY_PASSWORD_FILE is required}"
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

# Send only the files that participate in the API image. Synology's SSH
# configuration disables rsync/SFTP, so use the compatible legacy SCP mode.
scp_options=(-O -P "$NAS_DEPLOY_PORT" -o StrictHostKeyChecking=accept-new -o BatchMode=no)
scp_command=(scp "${scp_options[@]}")
if [ -n "${NAS_DEPLOY_PASSWORD_FILE:-}" ]; then
  scp_command=(sshpass -f "$NAS_DEPLOY_PASSWORD_FILE" "${scp_command[@]}")
fi
"${scp_command[@]}" -r "$repo_root/Server/OnlyMyGame.Api" "$NAS_DEPLOY_TARGET:$NAS_DEPLOY_PATH/Server/"
"${scp_command[@]}" "$repo_root/Assets/OnlyMyGame/Core/RuleCore.cs" "$NAS_DEPLOY_TARGET:$NAS_DEPLOY_PATH/Assets/OnlyMyGame/Core/RuleCore.cs"
"${scp_command[@]}" "$repo_root/docker-compose.yml" "$NAS_DEPLOY_TARGET:$NAS_DEPLOY_PATH/docker-compose.yml"

"${ssh_command[@]}" \
  "cd '$NAS_DEPLOY_PATH' && '$NAS_DOCKER_COMPOSE_COMMAND' up -d --build onlymygame-api && for attempt in \$(seq 1 15); do curl --fail --silent --show-error http://127.0.0.1:8080/health && exit 0; sleep 2; done; exit 1"

mkdir -p "$state_dir"
printf '%s\n' "$source_hash" > "$state_file"

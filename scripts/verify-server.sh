#!/usr/bin/env bash
set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
test_project="$repo_root/Server/OnlyMyGame.Api.Tests/OnlyMyGame.Api.Tests.csproj"
api_project="$repo_root/Server/OnlyMyGame.Api/OnlyMyGame.Api.csproj"
dockerfile="$repo_root/Server/OnlyMyGame.Api/Dockerfile"
compose_file="$repo_root/docker-compose.yml"
deploy_script="$repo_root/scripts/deploy-nas-api.sh"
evaluation_script="$repo_root/scripts/evaluate-rules.py"
source_hash_script="$repo_root/scripts/compute-api-source-hash.py"
container_workflow="$repo_root/.github/workflows/server-container.yml"

command -v dotnet >/dev/null 2>&1 || {
  echo "Server verification failed: dotnet SDK is not installed." >&2
  exit 1
}
command -v python3 >/dev/null 2>&1 || {
  echo "Server verification failed: python3 is required for deployment policy checks." >&2
  exit 1
}

# Contract drift in the standalone commercial evaluator can otherwise make a
# green server build report every live rules-v4 response as invalid. This mode
# is deterministic, writes no report, and is hard-coded to make zero requests.
python3 "$evaluation_script" --dry-run

# The independent release evaluator and the API must enforce the exact same
# version/header contract. Compare source constants directly so drift cannot be
# hidden by both components passing their own isolated self-checks.
python3 - "$repo_root/Server/OnlyMyGame.Api/ApiPolicies.cs" "$evaluation_script" <<'PY'
from pathlib import Path
import ast
import re
import sys

policies = Path(sys.argv[1]).read_text(encoding="utf-8")
evaluator = ast.parse(Path(sys.argv[2]).read_text(encoding="utf-8"))

def csharp_constant(name: str) -> str:
    match = re.search(rf"public const string {name}\s*=\s*\"([^\"]+)\";", policies)
    if not match:
        raise SystemExit(f"Contract sync failed: ApiPolicies.{name} is missing.")
    return match.group(1)

python_constants = {}
for node in evaluator.body:
    if isinstance(node, ast.Assign) and len(node.targets) == 1 and isinstance(node.targets[0], ast.Name):
        if node.targets[0].id in {"EXPECTED_API_VERSION", "EXPECTED_COMPATIBILITY_VERSION", "COMPATIBILITY_HEADER"}:
            python_constants[node.targets[0].id] = ast.literal_eval(node.value)

expected = {
    "EXPECTED_API_VERSION": csharp_constant("ApiVersion"),
    "EXPECTED_COMPATIBILITY_VERSION": csharp_constant("RuleCompatibilityVersion"),
    "COMPATIBILITY_HEADER": csharp_constant("RuleCompatibilityHeader"),
}
if python_constants != expected:
    raise SystemExit(f"Contract sync failed: evaluator={python_constants!r}, API={expected!r}.")
PY

source_hash="$(python3 "$source_hash_script")"
if [[ ! "$source_hash" =~ ^[0-9a-f]{64}$ ]]; then
  echo "Server verification failed: API source identity is invalid." >&2
  exit 1
fi

compose_command=()
if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
  compose_command=(docker compose)
elif command -v docker-compose >/dev/null 2>&1 && docker-compose version >/dev/null 2>&1; then
  compose_command=(docker-compose)
else
  echo "Server verification failed: Docker Compose is required for config validation." >&2
  exit 1
fi

bash -n "$deploy_script"

python3 - "$dockerfile" <<'PY'
from pathlib import Path
import re
import sys

path = Path(sys.argv[1])
lines = path.read_text(encoding="utf-8").splitlines()
final_from = max(index for index, line in enumerate(lines) if re.match(r"^\s*FROM\s+", line, re.I))
stage = lines[final_from:]
users = [line.split(None, 1)[1].strip() for line in stage if re.match(r"^\s*USER\s+", line, re.I)]
if users != ["$APP_UID"]:
    raise SystemExit("Container policy failed: the final Docker stage must end with USER $APP_UID.")
stage_text = "\n".join(stage)
required = {
    "SQLite recovery CLI": r"apt-get\s+install[^\n]*sqlite3",
    "/data creation": r"mkdir\s+-p\s+/data",
    "/data APP_UID ownership": r'chown\s+"\$APP_UID:0"\s+/data',
    "/data rollback-safe private mode": r"chmod\s+0770\s+/data",
    "/data volume": r'VOLUME\s+\["/data"\]',
}
for label, pattern in required.items():
    if not re.search(pattern, stage_text):
        raise SystemExit(f"Container policy failed: missing {label} in final Docker stage.")
PY

# Every core source linked by the API project must be available in the Docker
# context, copied by the Dockerfile, and retained in the immutable NAS release
# archive/provenance set. Keep this check derived from the project file so a new
# linked source cannot silently bypass one of those boundaries.
python3 - "$repo_root" "$api_project" "$dockerfile" "$repo_root/.dockerignore" "$deploy_script" "$source_hash_script" "$container_workflow" <<'PY'
from pathlib import Path
import ast
import re
import sys
import xml.etree.ElementTree as ET

repo = Path(sys.argv[1]).resolve()
project = Path(sys.argv[2]).resolve()
dockerfile = Path(sys.argv[3]).read_text(encoding="utf-8")
dockerignore = set(Path(sys.argv[4]).read_text(encoding="utf-8").splitlines())
deploy = Path(sys.argv[5]).read_text(encoding="utf-8")
hash_tree = ast.parse(Path(sys.argv[6]).read_text(encoding="utf-8"))
workflow = Path(sys.argv[7]).read_text(encoding="utf-8")
root = ET.parse(project).getroot()
linked_sources = []
for node in root.findall(".//Compile"):
    include = node.attrib.get("Include", "")
    resolved = (project.parent / include).resolve()
    try:
        relative = resolved.relative_to(repo).as_posix()
    except ValueError:
        continue
    if relative.startswith("Assets/OnlyMyGame/Core/"):
        linked_sources.append(relative)

if not linked_sources:
    raise SystemExit("Container policy failed: API project did not expose linked core sources.")
hash_scopes = None
for node in hash_tree.body:
    if isinstance(node, ast.Assign) and any(isinstance(target, ast.Name) and target.id == "SOURCE_SCOPES" for target in node.targets):
        hash_scopes = set(ast.literal_eval(node.value))
        break
expected_hash_scopes = {"Server/OnlyMyGame.Api", "docker-compose.yml", *linked_sources}
if hash_scopes != expected_hash_scopes:
    raise SystemExit(f"Container policy failed: source identity scopes drifted: {hash_scopes!r} != {expected_hash_scopes!r}.")
for source in linked_sources:
    if "!" + source not in dockerignore:
        raise SystemExit(f"Container policy failed: .dockerignore does not re-include {source}.")
    copy_pattern = rf"^\s*COPY\s+{re.escape(source)}\s+Assets/OnlyMyGame/Core/\s*$"
    if not re.search(copy_pattern, dockerfile, re.MULTILINE):
        raise SystemExit(f"Container policy failed: Dockerfile does not copy {source}.")
    if deploy.count(source) < 2:
        raise SystemExit(f"Deployment policy failed: release provenance/archive omits {source}.")
for fragment in (
    'source_commit="$(git -C "$repo_root" rev-parse HEAD)"',
    'compute-api-source-hash.py" --commit "$source_commit"',
    'guarded sources changed while verification was running',
    'git -C "$repo_root" show "$source_commit:Server/OnlyMyGame.Api/ApiPolicies.cs"',
    'git -C "$repo_root" archive --format=tar "$source_commit"',
):
    if fragment not in deploy:
        raise SystemExit(f"Deployment policy failed: immutable captured-commit packaging is missing {fragment!r}.")
if re.search(r"git\s+-C\s+\"\$repo_root\"\s+archive\s+--format=tar\s+HEAD\b", deploy):
    raise SystemExit("Deployment policy failed: release archive must not follow mutable HEAD.")
for fragment in (
    "python3 scripts/compute-api-source-hash.py",
    "io.onlymygame.source-hash=${{ steps.source.outputs.hash }}",
    "org.opencontainers.image.revision=${{ github.sha }}",
    "uses: actions/attest@",
    "push-to-registry: true",
):
    if fragment not in workflow:
        raise SystemExit(f"Container policy failed: publish workflow is missing {fragment!r}.")
PY

verification_tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/onlymygame-server-verify.XXXXXX")"
trap 'rm -rf "$verification_tmp_dir"' EXIT
remote_script="$verification_tmp_dir/remote-deploy.sh"
python3 - "$deploy_script" > "$remote_script" <<'PY'
from pathlib import Path
import sys

source = Path(sys.argv[1]).read_text(encoding="utf-8")
open_marker = "<<'ONLYMYGAME_REMOTE_DEPLOY'\n"
close_marker = "\nONLYMYGAME_REMOTE_DEPLOY"
start = source.index(open_marker) + len(open_marker)
end = source.index(close_marker, start)
remote = source[start:end]
if not remote.startswith("set -eu\n"):
    raise SystemExit("Deployment policy failed: could not isolate the literal remote deployment script.")
print(remote, end="")
PY
sh -n "$remote_script"

private_file_policy_script="$verification_tmp_dir/private-file-policy.sh"
python3 - "$deploy_script" > "$private_file_policy_script" <<'PY'
from pathlib import Path
import sys

source = Path(sys.argv[1]).read_text(encoding="utf-8")
open_marker = "# ONLYMYGAME_PRIVATE_FILE_POLICY_BEGIN\n"
close_marker = "# ONLYMYGAME_PRIVATE_FILE_POLICY_END"
start = source.index(open_marker) + len(open_marker)
end = source.index(close_marker, start)
policy = source[start:end]
print("#!/usr/bin/env bash")
print("set -euo pipefail")
print(policy, end="")
print('require_private_local_file "verification secret" "$1"')
PY
chmod 0700 "$private_file_policy_script"

private_secret="$verification_tmp_dir/private-secret"
printf '%s\n' 'verification-placeholder' > "$private_secret"
chmod 0600 "$private_secret"
"$private_file_policy_script" "$private_secret"
chmod 0400 "$private_secret"
"$private_file_policy_script" "$private_secret"

for insecure_mode in 0640 0604 0700; do
  chmod "$insecure_mode" "$private_secret"
  if "$private_file_policy_script" "$private_secret" >/dev/null 2>&1; then
    echo "Deployment policy failed: private file mode $insecure_mode was accepted." >&2
    exit 1
  fi
done

chmod 0600 "$private_secret"
private_secret_link="$verification_tmp_dir/private-secret-link"
ln -s "$private_secret" "$private_secret_link"
if "$private_file_policy_script" "$private_secret_link" >/dev/null 2>&1; then
  echo "Deployment policy failed: a symlink secret file was accepted." >&2
  exit 1
fi
echo "Local private-file failure-injection harness passed 4 rejection scenarios."

verification_image="ghcr.io/example/onlymygame-api@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
compose_backup_dir="$verification_tmp_dir/compose-backup"
mkdir -p "$compose_backup_dir"
compose_json="$verification_tmp_dir/compose.json"
rollback_override_check="$verification_tmp_dir/rollback-override.yml"
rollback_compose_json="$verification_tmp_dir/rollback-compose.json"
printf '%s\n' \
  'services:' \
  '  onlymygame-api:' \
  '    image: onlymygame-api:rollback-verification' > "$rollback_override_check"
OPENAI_API_KEY=verification-placeholder \
ONLYMYGAME_ALLOWED_ORIGIN=https://example.test \
ONLYMYGAME_DAILY_SALT=verification-placeholder \
ONLYMYGAME_TRUSTED_PROXIES=127.0.0.1 \
ONLYMYGAME_GLOBAL_DAILY_LIMIT=100 \
ONLYMYGAME_API_IMAGE="$verification_image" \
ONLYMYGAME_BACKUP_DIR="$compose_backup_dir" \
  "${compose_command[@]}" -f "$compose_file" config --format json > "$compose_json"
OPENAI_API_KEY=verification-placeholder \
ONLYMYGAME_ALLOWED_ORIGIN=https://example.test \
ONLYMYGAME_DAILY_SALT=verification-placeholder \
ONLYMYGAME_TRUSTED_PROXIES=127.0.0.1 \
ONLYMYGAME_GLOBAL_DAILY_LIMIT=100 \
ONLYMYGAME_API_IMAGE="$verification_image" \
ONLYMYGAME_BACKUP_DIR="$compose_backup_dir" \
  "${compose_command[@]}" -f "$compose_file" -f "$rollback_override_check" config --format json > "$rollback_compose_json"

python3 - "$compose_json" "$rollback_compose_json" "$verification_image" "$compose_backup_dir" <<'PY'
import json
from pathlib import Path
import sys

config = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
expected_image = sys.argv[3]
expected_backup_dir = str(Path(sys.argv[4]).resolve())
services = config.get("services", {})
api = services.get("onlymygame-api")
initializer = services.get("onlymygame-data-init")
if not isinstance(api, dict) or not isinstance(initializer, dict):
    raise SystemExit("Container policy failed: API and data-init services are required.")

def require(condition, message):
    if not condition:
        raise SystemExit("Container policy failed: " + message)

def has_data_volume(service):
    return any(
        isinstance(volume, dict)
        and volume.get("type") == "volume"
        and volume.get("source") == "onlymygame-data"
        and volume.get("target") == "/data"
        and volume.get("read_only") is not True
        for volume in service.get("volumes", [])
    )

def has_backup_bind(service):
    return any(
        isinstance(volume, dict)
        and volume.get("type") == "bind"
        and str(Path(volume.get("source", "")).resolve()) == expected_backup_dir
        and volume.get("target") == "/backup"
        and volume.get("read_only") is not True
        for volume in service.get("volumes", [])
    )

for name, service in (("API", api), ("data-init", initializer)):
    require(service.get("image") == expected_image, f"{name} must use the same immutable digest.")
    require("build" not in service, f"{name} must not contain a NAS build definition.")

require(api.get("read_only") is True, "API root filesystem must be read-only.")
require(has_data_volume(api), "API requires one writable onlymygame-data volume at /data.")
require("ALL" in api.get("cap_drop", []), "API must drop all Linux capabilities.")
require("no-new-privileges:true" in api.get("security_opt", []), "API must prohibit privilege escalation.")
tmpfs = "\n".join(api.get("tmpfs", []))
require(
    "/tmp:" in tmpfs
    and all(option in tmpfs for option in ("rw", "noexec", "nosuid", "nodev", "size=64m", "mode=1777")),
    "API /tmp must be bounded, writable, sticky, noexec, nosuid, and nodev.",
)
dependency = api.get("depends_on", {}).get("onlymygame-data-init", {})
require(
    dependency.get("condition") == "service_completed_successfully",
    "API must wait for successful data initialization outside deployment cutovers.",
)

require(initializer.get("user") == "0:0", "data-init must explicitly use root only for data maintenance.")
require(initializer.get("read_only") is True, "data-init root filesystem must be read-only.")
require(initializer.get("network_mode") == "none", "data-init must have no network.")
require(has_data_volume(initializer), "data-init must maintain the same writable /data volume.")
require(has_backup_bind(initializer), "data-init requires the per-transaction /backup bind.")
require("ALL" in initializer.get("cap_drop", []), "data-init must drop the default capability set.")
require(
    set(initializer.get("cap_add", [])) == {"CHOWN", "DAC_OVERRIDE", "FOWNER"},
    "data-init must receive only the ownership and recovery capabilities.",
)
require(
    "no-new-privileges:true" in initializer.get("security_opt", []),
    "data-init must prohibit privilege escalation.",
)
initializer_command = "\n".join(initializer.get("command", []))
for fragment in (
    "ONLYMYGAME_DATA_MODE",
    "permissions)",
    "backup)",
    "restore)",
    "sqlite3",
    "integrity_check",
    ".backup",
    "sha256sum",
    "original-data-mode",
    "original-database-mode",
):
    require(fragment in initializer_command, f"data-init command is missing {fragment!r}.")
require(not initializer.get("ports"), "data-init must not publish ports.")

rollback = json.loads(Path(sys.argv[2]).read_text(encoding="utf-8"))
rollback_api = rollback.get("services", {}).get("onlymygame-api", {})
require(
    rollback_api.get("image") == "onlymygame-api:rollback-verification",
    "rollback Compose override must select the pinned image tag.",
)
require("build" not in rollback_api, "rollback service must remain build-free.")
PY

# Execute the literal remote state machine against local fake Docker, Compose,
# curl, and sleep commands. No NAS, registry, daemon, or network is contacted.
python3 - "$remote_script" "$verification_tmp_dir/harness" "$verification_image" <<'PY'
from __future__ import annotations

import os
from pathlib import Path
import shutil
import subprocess
import sys
import textwrap

remote_script = Path(sys.argv[1])
harness_root = Path(sys.argv[2])
target_image = sys.argv[3]
source_hash = "a" * 64
source_commit = "1" * 40
previous_hash = "b" * 64
old_container_id = "c" * 64
old_image_id = "sha256:" + "d" * 64
target_image_id = "sha256:" + "e" * 64


def executable(path: Path, contents: str) -> None:
    path.write_text(textwrap.dedent(contents).lstrip(), encoding="utf-8")
    path.chmod(0o755)


def assert_true(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def setup_scenario(name: str, *, previous: bool = True, env_mode: int = 0o600):
    root = harness_root / name
    if root.exists():
        shutil.rmtree(root)
    nas = root / "nas"
    deploy = nas / ".deploy"
    releases = deploy / "releases"
    target_release = releases / source_hash
    previous_release = releases / previous_hash
    state = root / "state"
    fake_bin = root / "bin"
    for path in (target_release / "Server/OnlyMyGame.Api", previous_release, state, fake_bin):
        path.mkdir(parents=True, exist_ok=True)
    (target_release / "docker-compose.yml").write_text("services: {}\n", encoding="utf-8")
    (previous_release / "docker-compose.yml").write_text("services: {}\n", encoding="utf-8")
    (deploy / "incoming.tar").write_text("local fake archive\n", encoding="utf-8")
    env_file = nas / ".env"
    env_file.write_text("OPENAI_API_KEY=fake\n", encoding="utf-8")
    env_file.chmod(env_mode)
    if previous:
        (deploy / "current").symlink_to(previous_release)
        (state / "service").write_text("old\n", encoding="utf-8")
    else:
        (state / "service").write_text("stopped\n", encoding="utf-8")
    (state / "database").write_text("original database\n", encoding="utf-8")
    log = root / "events.log"
    log.write_text("", encoding="utf-8")

    docker = fake_bin / "docker"
    executable(
        docker,
        r'''
        #!/bin/sh
        set -eu
        printf 'docker %s\n' "$*" >> "$HARNESS_LOG"
        if [ "$1" = ps ]; then
          case " $* " in
            *" -a "*) exit 0 ;;
          esac
          service=$(cat "$HARNESS_STATE/service")
          if [ "$HARNESS_SCENARIO" = legacy ] || [ "$service" = old ]; then
            printf '%s\n' "$HARNESS_OLD_CONTAINER_ID"
          elif [ "$service" = target ] || [ "$service" = reused ]; then
            printf '%s\n' "$HARNESS_OLD_CONTAINER_ID"
          elif [ "$service" = foreign ]; then
            printf '%s\n' "$HARNESS_FOREIGN_CONTAINER_ID"
          fi
          exit 0
        fi
        if [ "$1" = inspect ]; then
          case "$3" in
            *'.Image'*)
              if [ "$(cat "$HARNESS_STATE/service")" = target ]; then
                printf '%s\n' "$HARNESS_TARGET_IMAGE_ID"
              else
                printf '%s\n' "$HARNESS_OLD_IMAGE_ID"
              fi
              ;;
            *'project.config_files'*)
              if [ "$(cat "$HARNESS_STATE/service")" = target ]; then
                target_override=$(find "$HARNESS_DEPLOY/transactions" -name target-compose.override.yml -print | head -1)
                printf '%s/docker-compose.yml,%s\n' "$HARNESS_TARGET_RELEASE" "$target_override"
              else
                printf '%s/docker-compose.yml\n' "$HARNESS_PREVIOUS_RELEASE"
              fi
              ;;
            *'project.working_dir'*) printf '%s\n' "$HARNESS_PREVIOUS_RELEASE" ;;
            *) exit 2 ;;
          esac
          exit 0
        fi
        if [ "$1" = image ] && [ "$2" = tag ]; then
          [ "$3" = "$HARNESS_OLD_IMAGE_ID" ]
          printf 'event tag-old\n' >> "$HARNESS_LOG"
          exit 0
        fi
        if [ "$1" = image ] && [ "$2" = inspect ]; then
          case "$4" in
            *'.Id'*)
              case "$HARNESS_SCENARIO" in
                same-image-*) printf '%s\n' "$HARNESS_OLD_IMAGE_ID" ;;
                *) printf '%s\n' "$HARNESS_TARGET_IMAGE_ID" ;;
              esac
              ;;
            *'.Config.User'*) printf '%s\n' '1654:0' ;;
            *'org.opencontainers.image.revision'*)
              if [ "$HARNESS_SCENARIO" = revision-invalid ]; then
                printf '%s\n' missing
              else
                printf '%s\n' "$HARNESS_SOURCE_COMMIT"
              fi
              ;;
            *'io.onlymygame.source-hash'*)
              if [ "$HARNESS_SCENARIO" = source-hash-mismatch ]; then
                printf '%064d\n' 2
              else
                printf '%s\n' "$HARNESS_SOURCE_HASH"
              fi
              ;;
            *) exit 2 ;;
          esac
          exit 0
        fi
        if [ "$1" = image ] && [ "$2" = rm ]; then
          printf 'event gc-image\n' >> "$HARNESS_LOG"
          exit 0
        fi
        if [ "$1" = stop ]; then
          if [ "$(cat "$HARNESS_STATE/service")" = reused ]; then
            printf 'event stop-reused\n' >> "$HARNESS_LOG"
          else
            printf 'event stop\n' >> "$HARNESS_LOG"
          fi
          printf '%s\n' stopped > "$HARNESS_STATE/service"
          exit 0
        fi
        if [ "$1" = run ]; then
          case " $* " in
            *" --entrypoint /usr/bin/sqlite3 $HARNESS_TARGET_IMAGE --version "*)
              printf 'event sqlite-cli\n' >> "$HARNESS_LOG"
              exit 0
              ;;
          esac
        fi
        exit 2
        ''',
    )

    compose = fake_bin / "docker-compose"
    executable(
        compose,
        r'''
        #!/bin/sh
        set -eu
        printf 'compose mode=%s image=%s %s\n' "${ONLYMYGAME_DATA_MODE:-}" "${ONLYMYGAME_API_IMAGE:-}" "$*" >> "$HARNESS_LOG"
        if [ "$1" = version ] && [ "$2" = --short ]; then
          if [ "$HARNESS_SCENARIO" = compose-old ]; then
            printf '%s\n' '1.28.0'
          else
            printf '%s\n' '2.24.6'
          fi
          exit 0
        fi
        [ "${ONLYMYGAME_API_IMAGE:-}" = "$HARNESS_TARGET_IMAGE" ] || exit 81
        case " $* " in
          *" config --services "*)
            printf '%s\n' onlymygame-data-init onlymygame-api
            exit 0
            ;;
          *" pull onlymygame-data-init onlymygame-api "*)
            printf 'event pull-both\n' >> "$HARNESS_LOG"
            if [ "$HARNESS_SCENARIO" = race ]; then
              printf '%s\n' foreign > "$HARNESS_STATE/service"
            fi
            exit 0
            ;;
          *" stop onlymygame-api "*)
            printf 'event stop\n' >> "$HARNESS_LOG"
            printf '%s\n' stopped > "$HARNESS_STATE/service"
            exit 0
            ;;
          *" run --rm --no-deps onlymygame-data-init "*)
            case "${ONLYMYGAME_DATA_MODE:-}" in
              backup)
                [ "$(cat "$HARNESS_STATE/service")" = stopped ] || exit 82
                printf 'event backup\n' >> "$HARNESS_LOG"
                cp "$HARNESS_STATE/database" "$ONLYMYGAME_BACKUP_DIR/onlymygame.db"
                sha256sum "$ONLYMYGAME_BACKUP_DIR/onlymygame.db" > "$ONLYMYGAME_BACKUP_DIR/onlymygame.db.sha256"
                printf '%s\n' '1654:0:770' > "$ONLYMYGAME_BACKUP_DIR/original-data-mode"
                printf '%s\n' '1654:0:660' > "$ONLYMYGAME_BACKUP_DIR/original-database-mode"
                ;;
              permissions)
                printf 'event permissions\n' >> "$HARNESS_LOG"
                printf '%s\n' 'mutated database' > "$HARNESS_STATE/database"
                case "$HARNESS_SCENARIO" in
                  signal-hup) kill -HUP "$PPID"; exit 129 ;;
                  signal-int) kill -INT "$PPID"; exit 130 ;;
                  signal-term) kill -TERM "$PPID"; exit 143 ;;
                esac
                ;;
              restore)
                printf 'event restore\n' >> "$HARNESS_LOG"
                [ -s "$ONLYMYGAME_BACKUP_DIR/onlymygame.db" ] || exit 83
                cp "$ONLYMYGAME_BACKUP_DIR/onlymygame.db" "$HARNESS_STATE/database"
                ;;
              *) exit 84 ;;
            esac
            exit 0
            ;;
          *" up -d --no-build --no-deps onlymygame-api "*)
            case " $* " in
              *rollback-compose.override.yml*)
                printf 'event up-old\n' >> "$HARNESS_LOG"
                printf '%s\n' old > "$HARNESS_STATE/service"
                ;;
              *)
                case "$HARNESS_SCENARIO" in
                  same-image-*)
                    printf 'event up-reused\n' >> "$HARNESS_LOG"
                    printf '%s\n' reused > "$HARNESS_STATE/service"
                    if [ "$HARNESS_SCENARIO" = same-image-signal-term ]; then
                      kill -TERM "$PPID"
                      exit 143
                    fi
                    ;;
                  *)
                    printf 'event up-target\n' >> "$HARNESS_LOG"
                    printf '%s\n' target > "$HARNESS_STATE/service"
                    ;;
                esac
                ;;
            esac
            exit 0
            ;;
        esac
        exit 85
        ''',
    )

    executable(
        fake_bin / "curl",
        r'''
        #!/bin/sh
        set -eu
        service=$(cat "$HARNESS_STATE/service")
        if [ "$service" = old ] || [ "$service" = foreign ] || [ "$service" = reused ]; then
          printf '%s\n' '{"status":"ok","apiVersion":"v0","compatibilityVersion":"rules-v0"}'
        elif [ "$service" = target ] && [ "$HARNESS_SCENARIO" != health-fail ]; then
          printf '%s\n' '{"status":"ok","apiVersion":"v1","compatibilityVersion":"rules-v1"}'
        else
          printf '%s\n' '{"status":"starting"}'
        fi
        ''',
    )
    executable(fake_bin / "sleep", "#!/bin/sh\nexit 0\n")
    executable(
        fake_bin / "stat",
        "#!/bin/sh\nset -eu\nprintf '%s:%s\\n' \"$HARNESS_UID\" \"$HARNESS_ENV_MODE\"\n",
    )

    environment = os.environ.copy()
    environment.update(
        {
            "PATH": f"{fake_bin}:{environment['PATH']}",
            "HARNESS_SCENARIO": name,
            "HARNESS_LOG": str(log),
            "HARNESS_STATE": str(state),
            "HARNESS_PREVIOUS_RELEASE": str(previous_release),
            "HARNESS_TARGET_RELEASE": str(target_release),
            "HARNESS_DEPLOY": str(deploy),
            "HARNESS_OLD_CONTAINER_ID": old_container_id,
            "HARNESS_FOREIGN_CONTAINER_ID": "f" * 64,
            "HARNESS_OLD_IMAGE_ID": old_image_id,
            "HARNESS_TARGET_IMAGE_ID": target_image_id,
            "HARNESS_TARGET_IMAGE": target_image,
            "HARNESS_SOURCE_COMMIT": source_commit,
            "HARNESS_SOURCE_HASH": source_hash,
            "HARNESS_UID": str(os.getuid()),
            "HARNESS_ENV_MODE": f"{env_mode:o}",
        }
    )
    arguments = [
        "sh",
        str(remote_script),
        str(deploy),
        source_hash,
        str(deploy / "incoming.tar"),
        str(docker),
        str(compose),
        str(nas),
        "v1",
        "rules-v1",
        target_image,
        str(fake_bin / "stat"),
        "0",
        "3",
        source_commit,
    ]
    return root, deploy, state, log, environment, arguments


def execute(name: str, **kwargs):
    root, deploy, state, log, environment, arguments = setup_scenario(name, **kwargs)
    result = subprocess.run(arguments, env=environment, text=True, capture_output=True, timeout=15)
    events = log.read_text(encoding="utf-8")
    return root, deploy, state, events, result


def transaction(deploy: Path) -> Path:
    transactions = [path for path in (deploy / "transactions").iterdir() if path.is_dir()]
    assert_true(len(transactions) == 1, f"expected one transaction, got {transactions}")
    return transactions[0]


def assert_order(events: str, fragments: list[str]) -> None:
    offsets = [events.index(fragment) for fragment in fragments]
    assert_true(offsets == sorted(offsets), f"wrong event order {fragments}:\n{events}")


# A legacy running container without current is rejected before stop or pinning.
_, deploy, _, events, result = execute("legacy", previous=False)
assert_true(result.returncode != 0, "legacy bootstrap must fail closed")
assert_true("event stop" not in events, "legacy bootstrap must not stop the running API")
assert_true("event tag-old" not in events, "legacy bootstrap must not invent an unattributed rollback pin")

# Secret file type and mode checks happen before Compose or service mutation.
_, _, _, events, result = execute("env-mode", env_mode=0o644)
assert_true(result.returncode != 0, "world-readable .env must fail closed")
assert_true("event stop" not in events, "invalid .env mode must fail before stop")

root, deploy, state, log, environment, arguments = setup_scenario("env-symlink")
env_file = root / "nas/.env"
real_env = root / "nas/.env.real"
env_file.rename(real_env)
env_file.symlink_to(real_env)
result = subprocess.run(arguments, env=environment, text=True, capture_output=True, timeout=15)
events = log.read_text(encoding="utf-8")
assert_true(result.returncode != 0, "symlink .env must fail closed")
assert_true("event stop" not in events, "symlink .env must fail before stop")

# Unsupported Compose fails before any transaction can stop the service.
_, _, _, events, result = execute("compose-old")
assert_true(result.returncode != 0, "old Compose must fail closed")
assert_true("event stop" not in events, "old Compose must fail before stop")

# Every image records a full audit revision, while the API input identity—not an
# unrelated documentation-only HEAD—is the exact deployment compatibility key.
_, _, _, events, result = execute("revision-invalid")
assert_true(result.returncode != 0, "invalid image revision must fail closed")
assert_true("event stop" not in events, "invalid image revision must fail before stop")
_, _, _, events, result = execute("source-hash-mismatch")
assert_true(result.returncode != 0, "image source hash mismatch must fail closed")
assert_true("event stop" not in events, "image source hash mismatch must fail before stop")

# A replacement container appearing during a long pull is never stopped using
# the stale image/health pin captured at the beginning of preflight.
_, _, _, events, result = execute("race")
assert_true(result.returncode != 0, "container replacement race must fail closed")
assert_true("event stop" not in events, "container replacement race must fail before stop")

# Happy path uses one digest for both services and persists every recovery input.
_, deploy, state, events, result = execute("success")
assert_true(result.returncode == 0, f"success scenario failed: {result.stderr}\n{events}")
tx = transaction(deploy)
assert_true((tx / "state").read_text().strip() == "COMMITTED", "success must commit transaction state")
assert_true(
    (deploy / "current").resolve() == (deploy / "releases" / source_hash).resolve(),
    f"success must switch current: {(deploy / 'current').resolve()}",
)
for artifact in (
    "manifest",
    "target-compose.override.yml",
    "rollback-compose.override.yml",
    "rollback-tag",
    "onlymygame.db",
    "onlymygame.db.sha256",
    "original-data-mode",
    "original-database-mode",
    "target-image-revision",
    "target-image-source-hash",
):
    assert_true((tx / artifact).exists(), f"success must retain {artifact}")
manifest_text = (tx / "manifest").read_text(encoding="utf-8")
assert_true(f"target_image_revision={source_commit}\n" in manifest_text, "manifest must retain image revision")
assert_true(f"target_image_source_hash={source_hash}\n" in manifest_text, "manifest must retain image source identity")
assert_true(" build " not in f" {events} ", "deployment must never invoke a NAS build")
assert_true(target_image in events, "Compose calls must receive the caller's exact digest")
assert_order(events, ["event tag-old", "event pull-both", "event stop", "event backup", "event permissions", "event up-target"])

# A failed health gate restores the cold backup, exact old image, and old health.
_, deploy, state, events, result = execute("health-fail")
assert_true(result.returncode != 0, "health failure must return failure after rollback")
tx = transaction(deploy)
assert_true((tx / "state").read_text().strip() == "ROLLED_BACK", "health failure must finish rollback")
assert_true(
    (deploy / "current").resolve() == (deploy / "releases" / previous_hash).resolve(),
    "rollback must restore current",
)
assert_true((state / "database").read_text() == "original database\n", "rollback must restore SQLite bytes")
assert_true((tx / "rollback-tag").exists() and (tx / "onlymygame.db").exists(), "rollback inputs must persist")
assert_order(events, ["event stop", "event backup", "event permissions", "event up-target", "event restore", "event up-old"])

# Repeat/GC deploys can use the same digest, allowing Compose to restart the
# captured old container instead of creating a new ID. Both a health failure and
# TERM after that restart must stop it again before touching the SQLite backup.
for reused_scenario in ("same-image-health-fail", "same-image-signal-term"):
    _, deploy, state, events, result = execute(reused_scenario)
    assert_true(result.returncode != 0, f"{reused_scenario} must report failure")
    tx = transaction(deploy)
    assert_true((tx / "state").read_text().strip() == "ROLLED_BACK", f"{reused_scenario} must roll back")
    assert_true((state / "database").read_text() == "original database\n", f"{reused_scenario} must restore SQLite")
    assert_true(events.count("event stop\n") == 1, f"{reused_scenario} must perform the initial cold-backup stop once:\n{events}")
    assert_true(events.count("event stop-reused\n") == 1, f"{reused_scenario} must stop the reused old container before restore:\n{events}")
    assert_true(events.count("event restore") == 1, f"{reused_scenario} must restore exactly once")
    assert_true(events.count("event up-old") == 1, f"{reused_scenario} must restart the old image once")
    assert_order(
        events,
        ["event stop", "event backup", "event permissions", "event up-reused", "event stop-reused", "event restore", "event up-old"],
    )

# HUP/INT/TERM during the mutating initializer all converge on the same
# one-shot EXIT recovery; ordinary command/health failure is covered above.
for signal_scenario in ("signal-hup", "signal-int", "signal-term"):
    _, deploy, state, events, result = execute(signal_scenario)
    assert_true(result.returncode != 0, f"{signal_scenario} must report failure")
    tx = transaction(deploy)
    assert_true((tx / "state").read_text().strip() == "ROLLED_BACK", f"{signal_scenario} must recover once")
    assert_true((state / "database").read_text() == "original database\n", f"{signal_scenario} must restore SQLite")
    assert_true(events.count("event restore") == 1, f"{signal_scenario} recovery must not re-enter")
    assert_true(events.count("event up-old") == 1, f"{signal_scenario} must restart the old image once")
    assert_order(events, ["event stop", "event backup", "event permissions", "event restore", "event up-old"])

print("Local deploy failure-injection harness passed 14 scenarios.")
PY

for required_deploy_fragment in \
  'NAS_API_IMAGE must be a full image@sha256 digest' \
  'org.opencontainers.image.revision' \
  'io.onlymygame.source-hash' \
  'gh attestation verify "oci://$NAS_API_IMAGE"' \
  '--repo "$attestation_repository"' \
  '--signer-workflow "$attestation_signer_workflow"' \
  '--source-ref refs/heads/main' \
  '--deny-self-hosted-runners' \
  'target-compose.override.yml' \
  'rollback-compose.override.yml' \
  'trap on_exit 0' \
  'mutation_started=1' \
  'compose_target backup run --rm --no-deps onlymygame-data-init' \
  'compose_target restore run --rm --no-deps onlymygame-data-init' \
  'compose_target permissions pull onlymygame-data-init onlymygame-api' \
  'compose_target permissions up -d --no-build --no-deps onlymygame-api' \
  'compose_previous up -d --no-build --no-deps onlymygame-api' \
  'ONLYMYGAME_GC_ROLLBACKS' \
  'require_private_local_file "NAS deploy config $config_file" "$config_file"' \
  'require_private_local_file "NAS_DEPLOY_PASSWORD_FILE" "$NAS_DEPLOY_PASSWORD_FILE"' \
  'env_mode' \
  'version --short'
do
  if ! grep -Fq -- "$required_deploy_fragment" "$deploy_script"; then
    echo "Server verification failed: deploy script is missing '$required_deploy_fragment'." >&2
    exit 1
  fi
done
if grep -Eq 'compose_(target|previous).*([[:space:]])build([[:space:]]|$)|up.*[[:space:]]--build([[:space:]]|$)' "$deploy_script"; then
  echo "Server verification failed: production deployment must never build on NAS." >&2
  exit 1
fi

# Developer machines may carry a newer SDK/runtime than the net8.0 production
# target. CI installs .NET 8 explicitly; local verification safely rolls forward.
export DOTNET_ROLL_FORWARD="${DOTNET_ROLL_FORWARD:-Major}"

dotnet test "$test_project" --configuration Release --nologo
dotnet build "$api_project" --configuration Release --nologo

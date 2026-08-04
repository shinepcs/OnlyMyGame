#!/bin/sh
# Unity CLI skill-compatible entrypoint. Usage: scripts/unity-ci.sh [project-dir]
set -eu

project_dir="${1:-$(pwd)}"
cd "$project_dir"
project_dir="$(pwd -P)"

if unity status --format tsv 2>/dev/null | awk -v p="$project_dir" '$2 == "ready" && $3 == p { found=1 } END { exit !found }'; then
  echo "Unity 에디터가 프로젝트를 열고 있습니다. CLI 배치모드를 실행하려면 에디터를 닫으세요." >&2
  exit 1
fi

make unity-ci

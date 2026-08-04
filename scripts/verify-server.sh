#!/usr/bin/env bash
set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
test_project="$repo_root/Server/OnlyMyGame.Api.Tests/OnlyMyGame.Api.Tests.csproj"
api_project="$repo_root/Server/OnlyMyGame.Api/OnlyMyGame.Api.csproj"

command -v dotnet >/dev/null 2>&1 || {
  echo "Server verification failed: dotnet SDK is not installed." >&2
  exit 1
}

# Developer machines may carry a newer SDK/runtime than the net8.0 production
# target. CI installs .NET 8 explicitly; local verification safely rolls forward.
export DOTNET_ROLL_FORWARD="${DOTNET_ROLL_FORWARD:-Major}"

dotnet test "$test_project" --configuration Release --nologo
dotnet build "$api_project" --configuration Release --nologo

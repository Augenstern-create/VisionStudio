#!/usr/bin/env bash
# Local launcher for Vision Studio Web on this machine (Ubuntu 24.04 x64, no root).
#
# Differences from the stock `dotnet run` flow:
#   * .NET 9 SDK lives in ~/.dotnet (installed per-user, not system-wide)
#   * --no-launch-profile, so the env vars below are not overwritten by
#     src/VisionStudio.Web/Properties/launchSettings.json
#   * native deps come from the system (libgeotiff5 + libproj25 are installed,
#     which is what Emgu's libcvextern.so links against)
#
# Usage:  ./run-web.sh [extra dotnet args]
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

# The access token is never committed: set VISIONSTUDIO_WEB_TOKEN, or drop a token into
# .webdata/token (git-ignored) and this script picks it up.
token_file="$repo_root/.webdata/token"
export VISIONSTUDIO_WEB_TOKEN="${VISIONSTUDIO_WEB_TOKEN:-$(cat "$token_file" 2>/dev/null || echo change-me-dev-token)}"
export VisionStudio__DataRoot="${VisionStudio__DataRoot:-$repo_root/.webdata}"
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://127.0.0.1:5080}"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Production}"

echo "Vision Studio Web -> $ASPNETCORE_URLS  (token: $VISIONSTUDIO_WEB_TOKEN)"
echo "Project data    -> $VisionStudio__DataRoot"

exec dotnet run --project "$repo_root/src/VisionStudio.Web" -c Release --no-build --no-launch-profile "$@"

#!/usr/bin/env bash
set -euo pipefail
app_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ -f "$app_root/.env" ]]; then
  set -a
  # shellcheck disable=SC1091
  source "$app_root/.env"
  set +a
fi
: "${VISIONSTUDIO_WEB_TOKEN:?VISIONSTUDIO_WEB_TOKEN must be configured}"
export VisionStudio__DataRoot="${VisionStudio__DataRoot:-$app_root/data}"
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://127.0.0.1:5080}"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Production}"
exec "$app_root/VisionStudio.Web"

#!/usr/bin/env bash
# Launch the Linux desktop (Avalonia) build of Vision Studio.
#
# The app is a self-contained ubuntu-x64 publish: no .NET installation needed.
# Native OpenCV comes from the app folder plus the system libs listed in
# deploy/web/install-dependencies.sh (libgeotiff5, liblapack3, libgtk-3-0t64, ...).
#
# Usage:
#   ./run-desktop.sh                        # welcome screen
#   ./run-desktop.sh path/to/Project.vstudio  # open a project directly
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
app_dir="$repo_root/artifacts/releases/VisionStudio-2.0.0-ubuntu-x64"

if [[ ! -x "$app_dir/VisionStudio" ]]; then
  echo "Desktop build not found. Publish it first:" >&2
  echo "  dotnet publish src/VisionStudio.App -c Release -r ubuntu-x64 --self-contained true -o $app_dir" >&2
  exit 1
fi

cd "$app_dir"
exec ./VisionStudio "$@"

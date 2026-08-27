#!/usr/bin/env bash
set -euo pipefail

workspace_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
archive="${1:-$workspace_root/artifacts/releases/VisionStudio-2.0.0-ubuntu-x64.tar.gz}"
test_root="$(mktemp -d /tmp/visionstudio-release-XXXXXX)"
trap 'rm -rf -- "$test_root"' EXIT

tar -xzf "$archive" -C "$test_root"
test -x "$test_root/VisionStudio"
test -x "$test_root/VisionStudio.Cli"
test -x "$test_root/VisionStudio.Runtimes.App"
test -f "$test_root/libcvextern.so"
test -f "$test_root/libicuuc.so.72.1.0.3"

project_root="$test_root/project"
cp -R "$workspace_root/samples/DemoProject" "$project_root"
mkdir -p "$project_root/images/input" "$project_root/images/output"
cp "$workspace_root/tests/image/1.png" "$project_root/images/input/1.png"

"$test_root/VisionStudio.Cli" validate "$project_root/workflows/main.vflow"
"$test_root/VisionStudio.Cli" run "$project_root/workflows/main.vflow" "$project_root/images/output/linux-result.png" metrics
test -s "$project_root/images/output/linux-result.png"

set +e
timeout 8s "$test_root/VisionStudio" >"$test_root/gui-stdout.log" 2>"$test_root/gui-stderr.log"
gui_exit=$?
set -e
if [[ $gui_exit -ne 0 && $gui_exit -ne 124 ]]; then
    cat "$test_root/gui-stderr.log" >&2
    exit "$gui_exit"
fi

echo "Linux release passed: Runtime payload, CLI validation, native OpenCV execution and GUI startup."

#!/usr/bin/env bash
set -euo pipefail

workspace_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
package="${1:-$workspace_root/artifacts/installers/visionstudio_1.0.0_amd64.deb}"
hash_file="$package.sha256"
test_root="$(mktemp -d /tmp/visionstudio-deb-test-XXXXXX)"
trap 'rm -rf -- "$test_root"' EXIT

test -s "$package"
test -s "$hash_file"
expected="$(cut -d ' ' -f1 "$hash_file")"
actual="$(sha256sum "$package" | cut -d ' ' -f1)"
[[ "$actual" == "$expected" ]]
[[ "$(dpkg-deb -f "$package" Package)" == "visionstudio" ]]
[[ "$(dpkg-deb -f "$package" Architecture)" == "amd64" ]]
dpkg-deb -f "$package" Depends | grep -q 'libavcodec60'

dpkg-deb -x "$package" "$test_root"
test -x "$test_root/usr/bin/visionstudio"
test -x "$test_root/usr/bin/visionstudio-cli"
test -x "$test_root/usr/lib/visionstudio/VisionStudio"
test -x "$test_root/usr/lib/visionstudio/VisionStudio.Cli"
test -r "$test_root/usr/share/applications/visionstudio.desktop"
test -r "$test_root/usr/share/icons/hicolor/scalable/apps/visionstudio.svg"
test -r "$test_root/usr/share/mime/packages/visionstudio.xml"
test -r "$test_root/usr/share/metainfo/io.visionstudio.VisionStudio.metainfo.xml"
grep -q '^Exec=visionstudio %F$' "$test_root/usr/share/applications/visionstudio.desktop"
grep -q 'application/x-visionstudio-project' "$test_root/usr/share/applications/visionstudio.desktop"
grep -q 'pattern="\*.vstudio"' "$test_root/usr/share/mime/packages/visionstudio.xml"
grep -q '#007ACC' "$test_root/usr/share/metainfo/io.visionstudio.VisionStudio.metainfo.xml"

echo "Linux installer passed: metadata, hash, dependencies, permissions, launcher, icon and MIME association."

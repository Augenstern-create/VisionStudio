#!/usr/bin/env bash
set -euo pipefail

workspace_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="${1:-2.0.0}"
publish_dir="$workspace_root/artifacts/releases/VisionStudio-$version-ubuntu-x64"
output_dir="$workspace_root/artifacts/installers"
staging="$(mktemp -d /tmp/visionstudio-deb-XXXXXX)"
package="$output_dir/visionstudio_${version}_amd64.deb"
trap 'rm -rf -- "$staging"' EXIT

if [[ ! -x "$publish_dir/VisionStudio" || ! -x "$publish_dir/VisionStudio.Cli" || ! -x "$publish_dir/VisionStudio.Runtimes.App" ]]; then
  echo "Ubuntu publish output is missing. Run scripts/publish-desktop.ps1 first." >&2
  exit 1
fi
case "$staging" in
  /tmp/visionstudio-deb-*) ;;
  *) echo "Unsafe staging path: $staging" >&2; exit 1 ;;
esac

mkdir -p "$staging/DEBIAN" \
  "$staging/usr/lib/visionstudio" \
  "$staging/usr/bin" \
  "$staging/usr/share/applications" \
  "$staging/usr/share/icons/hicolor/scalable/apps" \
  "$staging/usr/share/mime/packages" \
  "$staging/usr/share/metainfo"
cp -a "$publish_dir/." "$staging/usr/lib/visionstudio/"

installed_size="$(du -sk "$staging/usr/lib/visionstudio" | cut -f1)"
cat > "$staging/DEBIAN/control" <<EOF
Package: visionstudio
Version: $version
Section: graphics
Priority: optional
Architecture: amd64
Installed-Size: $installed_size
Maintainer: Vision Studio
Depends: libc6, libstdc++6, libgcc-s1, libx11-6, libice6, libsm6, libfontconfig1, libgeotiff5, liblapack3, libblas3, libopenjp2-7, libgstreamer-plugins-base1.0-0, libavcodec60, libavformat60, libavutil58, libswscale7, libgtk-3-0t64, libgl1, libegl1, libgbm1, libinput10
Description: Visual OpenCV workflow editor
 Vision Studio is a visual industrial OpenCV workflow editor with project,
 cache, code export and workflow automation support.
EOF

cat > "$staging/usr/bin/visionstudio" <<'EOF'
#!/usr/bin/env bash
exec /usr/lib/visionstudio/VisionStudio "$@"
EOF
cat > "$staging/usr/bin/visionstudio-cli" <<'EOF'
#!/usr/bin/env bash
exec /usr/lib/visionstudio/VisionStudio.Cli "$@"
EOF
cat > "$staging/usr/bin/visionstudio-runtime" <<'EOF'
#!/usr/bin/env bash
exec /usr/lib/visionstudio/VisionStudio.Runtimes.App "$@"
EOF

cat > "$staging/usr/share/applications/visionstudio.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Vision Studio
Name[zh_CN]=Vision Studio 视觉算法工作室
Comment=Visual industrial OpenCV workflow editor
Comment[zh_CN]=工业视觉 OpenCV 可视化工作流编辑器
Exec=visionstudio %F
Icon=visionstudio
Terminal=false
Categories=Development;Graphics;
MimeType=application/x-visionstudio-project;
StartupWMClass=VisionStudio
Keywords=OpenCV;Vision;Workflow;视觉;算法;
EOF

cat > "$staging/usr/share/applications/visionstudio-runtime.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Vision Studio Runtime
Name[zh_CN]=Vision Studio 运行平台
Comment=Run Vision Studio inspection projects
Comment[zh_CN]=运行 Vision Studio 自动化检测项目
Exec=visionstudio-runtime %F
Icon=visionstudio
Terminal=false
Categories=Development;Graphics;
StartupWMClass=VisionStudio.Runtimes.App
Keywords=OpenCV;Vision;Runtime;Inspection;视觉;检测;
EOF

cat > "$staging/usr/share/mime/packages/visionstudio.xml" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
  <mime-type type="application/x-visionstudio-project">
    <comment>Vision Studio Project</comment>
    <comment xml:lang="zh_CN">Vision Studio 项目</comment>
    <glob pattern="*.vstudio"/>
  </mime-type>
</mime-info>
EOF

cp "$workspace_root/installers/assets/visionstudio.svg" \
  "$staging/usr/share/icons/hicolor/scalable/apps/visionstudio.svg"

cat > "$staging/usr/share/metainfo/io.visionstudio.VisionStudio.metainfo.xml" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<component type="desktop-application">
  <id>io.visionstudio.VisionStudio</id>
  <name>Vision Studio</name>
  <name xml:lang="zh-CN">Vision Studio 视觉算法工作室</name>
  <summary>Visual industrial OpenCV workflow editor</summary>
  <summary xml:lang="zh-CN">工业视觉 OpenCV 可视化工作流编辑器</summary>
  <metadata_license>CC0-1.0</metadata_license>
  <project_license>Proprietary</project_license>
  <launchable type="desktop-id">visionstudio.desktop</launchable>
  <provides><binary>visionstudio</binary></provides>
  <provides><binary>visionstudio-runtime</binary></provides>
  <branding>
    <color type="primary" scheme_preference="dark">#1E1E1E</color>
    <color type="primary" scheme_preference="light">#007ACC</color>
  </branding>
  <releases><release version="$version" date="$(date +%F)"/></releases>
</component>
EOF

cat > "$staging/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
command -v update-mime-database >/dev/null 2>&1 && update-mime-database /usr/share/mime || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
EOF
cat > "$staging/DEBIAN/postrm" <<'EOF'
#!/bin/sh
set -e
command -v update-mime-database >/dev/null 2>&1 && update-mime-database /usr/share/mime || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
EOF

chmod 0755 "$staging/DEBIAN/postinst" "$staging/DEBIAN/postrm" \
  "$staging/usr/bin/visionstudio" "$staging/usr/bin/visionstudio-cli" "$staging/usr/bin/visionstudio-runtime" \
  "$staging/usr/lib/visionstudio/VisionStudio" "$staging/usr/lib/visionstudio/VisionStudio.Cli" \
  "$staging/usr/lib/visionstudio/VisionStudio.Runtimes.App"
find "$staging/usr/lib/visionstudio" -type f ! -name VisionStudio ! -name VisionStudio.Cli \
  ! -name VisionStudio.Runtimes.App -exec chmod 0644 {} +
find "$staging/usr/share" -type f -exec chmod 0644 {} +
chmod 0755 "$staging/DEBIAN"
chmod 0644 "$staging/DEBIAN/control"
dpkg-deb --root-owner-group --build "$staging" "$package"
(cd "$output_dir" && sha256sum "$(basename "$package")" > "$(basename "$package").sha256")
echo "Linux installer: $package"

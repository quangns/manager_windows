#!/usr/bin/env bash
# Đóng gói Manager Mac (macOS) từ Linux: ManagerMac.app cho Apple Silicon (arm64) và Intel (x64).
# Kết quả: dist/ManagerMac-<version>-macos-arm64.zip, dist/ManagerMac-<version>-macos-x64.zip
# App được ký ad-hoc bằng rcodesign (bắt buộc để chạy trên Apple Silicon); KHÔNG có chữ ký Apple Developer /
# notarize, nên lần đầu mở phải bấm "Open Anyway" (xem Huong-dan-macOS.txt).
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 UseSharedCompilation=false
DOTNET="${DOTNET:-$(command -v dotnet || echo /workspace/.toolchain/dotnet/dotnet)}"
RCODESIGN="${RCODESIGN:-/workspace/.toolchain/bin/rcodesign}"
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
mkdir -p dist

for RID in osx-arm64 osx-x64; do
  ARCH=${RID#osx-}
  BUILD=dist/mac-build/$RID
  STAGE=dist/mac-stage/$ARCH
  APP="$STAGE/ManagerMac.app"
  rm -rf "$BUILD" "$STAGE" src/ManagerMac/obj/Release
  "$DOTNET" publish src/ManagerMac -c Release -r "$RID" --self-contained true -o "$BUILD" --nologo -v q \
    --disable-build-servers -p:DebugType=none -p:UseAppHost=true

  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  cp -R "$BUILD"/. "$APP/Contents/MacOS/"
  cp src/ManagerMac/Assets/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
  chmod +x "$APP/Contents/MacOS/ManagerMac"
  cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key><string>ManagerMac</string>
  <key>CFBundleIdentifier</key><string>vn.quangns.managermac</string>
  <key>CFBundleName</key><string>Manager Mac</string>
  <key>CFBundleDisplayName</key><string>Manager Mac</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSAppleEventsUsageDescription</key><string>Manager Mac cần điều khiển ứng dụng để thoát và mở lại khi bạn bấm "Khởi động lại".</string>
</dict>
</plist>
PLIST

  # Ký ad-hoc cả gói (mọi Mach-O bên trong) — không ký thì macOS Apple Silicon từ chối chạy.
  "$RCODESIGN" sign "$APP" >/dev/null 2>&1 || { echo "LỖI: ký ad-hoc thất bại ($ARCH)"; exit 1; }
  # rcodesign verify tự báo là còn lỗi với chữ ký ad-hoc (không có CMS) → kiểm tra trực tiếp cờ ADHOC và file niêm phong gói.
  for BIN in ManagerMac libAvaloniaNative.dylib libSkiaSharp.dylib libe_sqlite3.dylib; do
    "$RCODESIGN" print-signature-info "$APP/Contents/MacOS/$BIN" 2>/dev/null | grep -q "ADHOC" \
      || { echo "LỖI: $BIN chưa được ký ($ARCH)"; exit 1; }
  done
  [[ -f "$APP/Contents/_CodeSignature/CodeResources" ]] || { echo "LỖI: gói chưa được niêm phong ($ARCH)"; exit 1; }

  python3 - "$STAGE/Huong-dan-macOS.txt" scripts/Huong-dan-macOS.txt src/ManagerWindows.App/Assets/Fonts/OFL-BeVietnamPro.txt src/ManagerWindows.App/Assets/Fonts/OFL-JetBrainsMono.txt <<'PY'
import sys
out, guide, *lic = sys.argv[1:]
text = open(guide, encoding="utf-8").read()
for l in lic:
    text += "\n" + "-" * 72 + "\n" + open(l, encoding="utf-8").read().strip() + "\n"
open(out, "w", encoding="utf-8").write(text)
PY

  # Zip giữ quyền chạy (mode trong external_attr) để Finder giải nén ra app chạy được.
  python3 - "dist/ManagerMac-$VERSION-macos-$ARCH.zip" "$STAGE" <<'PY'
import os, sys, zipfile
out, root = sys.argv[1:]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for base, dirs, files in os.walk(root):
        for name in sorted(dirs + files):
            full = os.path.join(base, name)
            arc = os.path.relpath(full, root)
            info = zipfile.ZipInfo.from_file(full, arc)
            if os.path.isdir(full):
                z.writestr(info, b"")
            else:
                info.compress_type = zipfile.ZIP_DEFLATED
                with open(full, "rb") as f:
                    z.writestr(info, f.read())
PY
  echo "macOS $ARCH: $(du -h "dist/ManagerMac-$VERSION-macos-$ARCH.zip" | cut -f1)"
done
rm -rf dist/mac-build dist/mac-stage

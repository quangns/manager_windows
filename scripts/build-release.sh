#!/usr/bin/env bash
# Đóng gói bản phát hành cho Windows x64 (chạy được trên Linux).
# Kết quả:
#   dist/ManagerWindows-<version>-win-x64.zip           bản cài đặt (App + Service, chạy install.cmd với quyền admin)
#   dist/ManagerWindows-<version>-win-x64-portable.zip  bản portable: CHỈ 1 file exe + file hướng dẫn
#   dist/SHA256SUMS.txt
# Cùng một commit, cùng mã nguồn App. Bản portable build thêm cờ PortableBuild (dữ liệu cạnh exe) và gộp thành
# một file exe tự chứa: runtime .NET, thư viện native, font và luật dọn rác đều nằm trong exe.
set -euo pipefail
# Chạy cả script dưới reap-run (child subreaper): mọi tiến trình con mồ côi của dotnet (MSBuild node,
# VBCSCompiler, testhost) được thu dọn thay vì thành zombie dưới PID 1 của container.
REAP=/workspace/.toolchain/bin/reap-run
if [[ -z "${UNDER_REAP_RUN:-}" && -x "$REAP" ]]; then
  export UNDER_REAP_RUN=1
  exec "$REAP" "$0" "$@"
fi
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# Không để MSBuild node / VBCSCompiler chạy nền sau khi build: trong container PID 1 không dọn
# tiến trình mồ côi, build server bị bỏ lại thành zombie (xem /workspace/CLAUDE.md).
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 UseSharedCompilation=false
DOTNET="${DOTNET:-$(command -v dotnet || echo /workspace/.toolchain/dotnet/dotnet)}"

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
rm -rf dist && mkdir -p dist/build/app dist/build/service

"$DOTNET" test tests/ManagerWindows.Core.Tests -c Release --nologo -v q --disable-build-servers

# Tự chứa runtime .NET để máy văn phòng không cần cài thêm gì.
# Xoá file trung gian của App trước mỗi lần publish: bản portable build cùng project với cờ khác (PortableBuild).
rm -rf src/ManagerWindows.App/obj/Release
"$DOTNET" publish src/ManagerWindows.App -c Release -r win-x64 --self-contained true -o dist/build/app --nologo -v q --disable-build-servers -p:DebugType=none
"$DOTNET" publish src/ManagerWindows.Service -c Release -r win-x64 --self-contained true -o dist/build/service --nologo -v q --disable-build-servers -p:DebugType=none

# Bản cài đặt: Service trước, App sau (file runtime trùng nhau là cùng một bản .NET).
INST=dist/ManagerWindows
mkdir -p "$INST"
cp -r dist/build/service/. "$INST/"
cp -r dist/build/app/. "$INST/"
cp scripts/install.ps1 scripts/uninstall.ps1 scripts/install.cmd scripts/uninstall.cmd README.md "$INST/"

# Bản portable: một file exe tự chứa + file hướng dẫn (kèm toàn văn giấy phép font OFL).
rm -rf src/ManagerWindows.App/obj/Release
"$DOTNET" publish src/ManagerWindows.App -c Release -r win-x64 --self-contained true -o dist/build/portable --nologo -v q \
  --disable-build-servers -p:DebugType=none -p:PortableBuild=true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
PORT=dist/portable/ManagerWindows-Portable
mkdir -p "$PORT"
cp dist/build/portable/ManagerWindows.App.exe "$PORT/"
python3 - "$PORT/Huong-dan-su-dung.txt" scripts/Huong-dan-su-dung.txt src/ManagerWindows.App/Assets/Fonts/OFL-BeVietnamPro.txt src/ManagerWindows.App/Assets/Fonts/OFL-JetBrainsMono.txt <<'PY'
import sys
out, guide, *licenses = sys.argv[1:]
text = open(guide, encoding="utf-8").read()
for lic in licenses:
    text += "\n" + "-" * 72 + "\n" + open(lic, encoding="utf-8").read().strip() + "\n"
# UTF-8 có BOM + CRLF để Notepad nào cũng đọc đúng tiếng Việt
open(out, "w", encoding="utf-8-sig", newline="\r\n").write(text)
PY

# Kiểm tra: gói portable đúng 2 file; App ở gói cài đặt giống hệt bản publish thường.
COUNT=$(find "$PORT" -type f | wc -l)
[[ "$COUNT" == 2 ]] || { echo "Gói portable phải có đúng 2 file, đang có $COUNT"; exit 1; }
python3 - "$INST" dist/build/app <<'PY'
import hashlib, os, sys
inst, app = sys.argv[1:]
h = lambda p: hashlib.sha256(open(p, 'rb').read()).hexdigest()
bad = [r for root, _, fs in os.walk(app) for f in fs
       for r in [os.path.relpath(os.path.join(root, f), app)] if h(os.path.join(app, r)) != h(os.path.join(inst, r))]
if bad: sys.exit(f"File App trong gói cài đặt khác bản publish: {bad[:10]}")
print("Gói cài đặt đủ file App; gói portable gồm:", ", ".join(sorted(os.listdir(sys.argv[1].replace('ManagerWindows', 'portable/ManagerWindows-Portable')))))
PY

# Kiểm tra nhãn loại bản: App của gói cài đặt phải là "installed", không lẫn bản portable.
python3 - "$INST/ManagerWindows.App.dll" dist/build/portable/ManagerWindows.App.exe <<'PY'
import sys
def tag(value):  # blob của AssemblyMetadata: chuỗi có tiền tố độ dài
    key = b"ManagerWindowsEdition"
    return bytes([len(key)]) + key + bytes([len(value)]) + value.encode()
inst = open(sys.argv[1], "rb").read()
if tag("installed") not in inst or tag("portable") in inst:
    sys.exit("LỖI: App trong gói cài đặt không mang nhãn installed")
print("Nhãn loại bản: gói cài đặt = installed")
PY

zipdir() { python3 -c "import shutil,sys; shutil.make_archive(sys.argv[1], 'zip', sys.argv[2], sys.argv[3])" "$1" "$2" "$3"; }
zipdir "dist/ManagerWindows-$VERSION-win-x64" dist ManagerWindows
zipdir "dist/ManagerWindows-$VERSION-win-x64-portable" dist/portable ManagerWindows-Portable
rm -rf dist/build
# Bản macOS (Manager Mac): arm64 + x64
./scripts/build-mac.sh
ls -lh dist/*.zip
(cd dist && sha256sum ./*.zip > SHA256SUMS.txt && cat SHA256SUMS.txt)

#!/usr/bin/env bash
# Đóng gói bản phát hành cho Windows x64 (chạy được trên Linux).
# Kết quả:
#   dist/ManagerWindows-<version>-win-x64.zip           bản cài đặt: bên ngoài chỉ có ManagerWindows.App.exe, install.cmd,
#                                                       uninstall.cmd, Huong-dan-su-dung.txt; dịch vụ nền + thư viện trong lib\
#   dist/ManagerWindows-<version>-win-x64-portable.zip  bản portable: CHỈ 1 file exe + file hướng dẫn
#   dist/SHA256SUMS.txt
# Cùng một commit, cùng mã nguồn App. Cả hai bản đều gộp App thành một file exe tự chứa (runtime .NET, thư viện native,
# font, luật dọn rác nằm trong exe); bản portable build thêm cờ PortableBuild (dữ liệu cạnh exe).
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

COMMON=(-c Release -r win-x64 --self-contained true --nologo -v q --disable-build-servers -p:DebugType=none)
SINGLE=(-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true)
APP_OBJ_DLL=src/ManagerWindows.App/obj/Release/net10.0-windows/win-x64/ManagerWindows.App.dll

# Nhãn loại bản (AssemblyMetadata ManagerWindowsEdition) trên dll trung gian — exe một file đã nén nên không dò được.
check_edition() {
  python3 - "$APP_OBJ_DLL" "$1" <<'PY'
import sys
path, want = sys.argv[1:]
def tag(value):  # blob của AssemblyMetadata: chuỗi có tiền tố độ dài
    key = b"ManagerWindowsEdition"
    return bytes([len(key)]) + key + bytes([len(value)]) + value.encode()
data = open(path, "rb").read()
other = "portable" if want == "installed" else "installed"
if tag(want) not in data or tag(other) in data:
    sys.exit(f"LỖI: App phải mang nhãn {want}")
print(f"Nhãn loại bản: {want}")
PY
}

# Hướng dẫn = nội dung + toàn văn giấy phép font OFL; UTF-8 có BOM + CRLF để Notepad nào cũng đọc đúng tiếng Việt.
write_guide() {
  python3 - "$1" "$2" src/ManagerWindows.App/Assets/Fonts/OFL-BeVietnamPro.txt src/ManagerWindows.App/Assets/Fonts/OFL-JetBrainsMono.txt <<'PY'
import sys
out, guide, *licenses = sys.argv[1:]
text = open(guide, encoding="utf-8").read()
if "GIẤY PHÉP FONT" not in text:
    text += ("\n\nGIẤY PHÉP FONT / FONT LICENSES\n==============================\n"
             "App nhúng font Be Vietnam Pro và JetBrains Mono, phát hành theo SIL Open Font License 1.1 (toàn văn bên dưới).\n"
             "The app embeds the Be Vietnam Pro and JetBrains Mono fonts, licensed under the SIL Open Font License 1.1 (full text below).\n")
for lic in licenses:
    text += "\n" + "-" * 72 + "\n" + open(lic, encoding="utf-8").read().strip() + "\n"
open(out, "w", encoding="utf-8-sig", newline="\r\n").write(text)
PY
}

# ---------- Bản cài đặt ----------
# Xoá file trung gian của App trước mỗi lần publish: bản portable build cùng project với cờ khác (PortableBuild).
rm -rf src/ManagerWindows.App/obj/Release
"$DOTNET" publish src/ManagerWindows.App "${COMMON[@]}" "${SINGLE[@]}" -o dist/build/app
check_edition installed
"$DOTNET" publish src/ManagerWindows.Service "${COMMON[@]}" -o dist/build/service

# Bên ngoài chỉ có file chạy app, cài, gỡ, hướng dẫn; dịch vụ nền, thư viện, luật dọn rác, script nằm trong lib\.
INST=dist/ManagerWindows
mkdir -p "$INST/lib"
cp dist/build/app/ManagerWindows.App.exe "$INST/"
cp -r dist/build/service/. "$INST/lib/"
cp scripts/install.ps1 scripts/uninstall.ps1 "$INST/lib/"
cp scripts/install.cmd scripts/uninstall.cmd "$INST/"
write_guide "$INST/Huong-dan-su-dung.txt" scripts/Huong-dan-cai-dat.txt

# ---------- Bản portable: một file exe tự chứa + file hướng dẫn ----------
rm -rf src/ManagerWindows.App/obj/Release
"$DOTNET" publish src/ManagerWindows.App "${COMMON[@]}" "${SINGLE[@]}" -p:PortableBuild=true -o dist/build/portable
check_edition portable
PORT=dist/portable/ManagerWindows-Portable
mkdir -p "$PORT"
cp dist/build/portable/ManagerWindows.App.exe "$PORT/"
write_guide "$PORT/Huong-dan-su-dung.txt" scripts/Huong-dan-su-dung.txt

# Kiểm tra cấu trúc: portable đúng 2 file; cài đặt bên ngoài đúng 4 file + lib\ (đủ dịch vụ, script, luật).
python3 - "$INST" "$PORT" <<'PY'
import os, sys
inst, port = sys.argv[1:]
top = sorted(os.listdir(inst))
want = sorted(["ManagerWindows.App.exe", "install.cmd", "uninstall.cmd", "Huong-dan-su-dung.txt", "lib"])
if top != want:
    sys.exit(f"Gói cài đặt bên ngoài phải chỉ có {want}, đang có {top}")
for f in ["ManagerWindows.Service.exe", "install.ps1", "uninstall.ps1", os.path.join("rules", "cleanup-rules.json")]:
    if not os.path.isfile(os.path.join(inst, "lib", f)):
        sys.exit(f"Thiếu lib/{f}")
if sorted(os.listdir(port)) != ["Huong-dan-su-dung.txt", "ManagerWindows.App.exe"]:
    sys.exit(f"Gói portable phải có đúng 2 file, đang có {sorted(os.listdir(port))}")
n = sum(len(fs) for _, _, fs in os.walk(os.path.join(inst, "lib")))
print(f"Gói cài đặt: {', '.join(top)} (lib có {n} file); gói portable: {', '.join(sorted(os.listdir(port)))}")
PY

zipdir() { python3 -c "import shutil,sys; shutil.make_archive(sys.argv[1], 'zip', sys.argv[2], sys.argv[3])" "$1" "$2" "$3"; }
zipdir "dist/ManagerWindows-$VERSION-win-x64" dist ManagerWindows
zipdir "dist/ManagerWindows-$VERSION-win-x64-portable" dist/portable ManagerWindows-Portable
rm -rf dist/build
ls -lh dist/*.zip
(cd dist && sha256sum ./*.zip > SHA256SUMS.txt && cat SHA256SUMS.txt)

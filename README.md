# Manager Windows

App chạy nền trên Windows 10/11 (x64) giúp:

1. **Dọn rác định kỳ**: file tạm, cache Windows Update, cache trình duyệt/Teams/Zalo, dump lỗi, Thùng rác…
2. **Gợi ý dọn file**: file lâu không dùng, file lớn, bộ cài cũ trong Downloads, file trùng nhau. App chỉ gợi ý, không bao giờ tự xoá.
   - **Dung lượng ổ đĩa** (kiểu TreeSize): quét cả ổ hoặc một thư mục, cây thư mục xếp từ lớn đến bé kèm % so với thư mục cha, số file/thư mục, danh sách 500 file lớn nhất; chuột phải để mở trong Explorer, quét riêng thư mục, chuyển vào Thùng rác (chặn thư mục hệ thống và junction).
3. **Chuyển data app khỏi ổ C** (Zalo, Telegram, Chrome/Edge, backup iPhone…). Data được chuyển sang ổ khác, đường dẫn cũ trên ổ C giữ nguyên nhờ *junction* nên app vẫn chạy bình thường. Có Hoàn tác.
4. **Theo dõi tần suất dùng app**: thời gian dùng thật (không tính lúc máy rảnh), số lần mở, biểu đồ theo giờ.
5. **Ưu tiên tài nguyên**: app dùng nhiều nhất hoặc app được ghim được ưu tiên CPU; app nền (OneDrive, updater…) bị hạ ưu tiên và chạy chế độ tiết kiệm (EcoQoS).
6. **Theo dõi tiến trình**:
   - Xem app nào đang chiếm CPU, RAM, ổ đĩa (gộp tiến trình theo app), phân loại Ứng dụng / Chạy ngầm / Hệ thống.
   - Chẩn đoán lag: khi ổ đĩa, CPU hoặc RAM quá tải, chỉ ra app dùng nhiều nhất.
   - Khởi động lại app; kiểm tra tiến trình sót (mồ côi, treo, chạy ngầm lâu không hoạt động) và dừng các mục được chọn.
   - Ngủ đông app không dùng (mặc định 15 phút, tắt sẵn): tạm dừng app cùng tiến trình chạy ngầm của nó, trả RAM/CPU; bấm vào app là chạy tiếp đúng trạng thái cũ. Không áp dụng cho app chat, mail, điều khiển từ xa, VPN, bộ gõ, nhạc, app đang phát âm thanh/đang làm việc; thêm app vào danh sách bằng cách kéo tâm ngắm lên cửa sổ app hoặc chuột phải ở tab Tiến trình.
   - End task, giải phóng RAM từng app hoặc toàn máy (kèm xả bộ nhớ đệm standby qua dịch vụ nền).
   - Cảnh báo ở khay khi một app chạy ngầm dùng nhiều CPU/RAM liên tục.
7. **Excel nặng**:
   - Khi phát hiện Excel đang mở file nặng, app tự ưu tiên tài nguyên cho Excel.
   - App đọc thẳng file .xlsx để tìm nguyên nhân làm file nặng: vùng dữ liệu "ma", quá nhiều style, hàm tự tính lại (INDIRECT/OFFSET…), liên kết ngoài, ảnh lớn, Office 32-bit…
   - Từ đó app đưa ra cách xử lý cụ thể.

8. **Phân vùng ổ đĩa** (tương tự MiniTool Partition Wizard, phần Windows hỗ trợ an toàn): sơ đồ đĩa theo tỉ lệ; thu nhỏ / mở rộng (NTFS, ReFS), tạo phân vùng từ vùng trống, xoá, format (NTFS / exFAT / FAT32), đổi ký tự ổ, đổi nhãn, kiểm tra lỗi (chkdsk /scan), khởi tạo đĩa mới (GPT/MBR). Thay đổi chạy trong cửa sổ riêng quyền Administrator (UAC), không qua dịch vụ nền. Chặn thao tác phá dữ liệu trên phân vùng Windows, EFI, MSR, Recovery và phân vùng đang chứa app; xoá/format phải gõ chữ xác nhận. Không hỗ trợ di chuyển/gộp phân vùng, mở rộng sang trái, chuyển MBR↔GPT khi còn dữ liệu.
9. **Công cụ** (phím tắt toàn cục, tự cấu hình):
   - Chụp màn hình: khoanh vùng tự do hoặc bấm chọn cửa sổ (`Ctrl+Alt+A`), chụp cửa sổ đang dùng (`Ctrl+Alt+W`), toàn màn hình (`Ctrl+Alt+F`). Trình chỉnh sửa có khung, elip, mũi tên, tô sáng, che kín, đánh số thứ tự, 8 màu, 3 độ dày nét; sao chép/lưu PNG.
   - Ghim cửa sổ app khác luôn trên cùng: phím tắt `Ctrl+Alt+T`, kéo tâm ngắm thả lên cửa sổ, hoặc chọn trong danh sách.
   - Mini monitor CPU / GPU / RAM / ổ đĩa luôn trên cùng, nền trong suốt; chọn chỉ số, ngang/dọc, 3 cỡ, khoá vị trí + click xuyên (`Ctrl+Alt+M`).

Giao diện kiểu dashboard, có tiếng Việt và tiếng Anh. Chế độ sáng/tối tự theo Windows, hoặc chọn cố định trong phần Cài đặt.

![Tổng quan – sáng](docs/design/preview/overview-light.png)

## Thiết kế giao diện

Giao diện được thiết kế bằng bộ skill [Google Stitch](https://github.com/google-labs-code/stitch-skills):

| File | Nội dung |
|---|---|
| [`docs/design/DESIGN.md`](docs/design/DESIGN.md) | Hệ thống thiết kế, viết bằng skill `taste-design`: màu sáng/tối, font, thành phần, bố cục, những điều cấm. Đây là chuẩn duy nhất cho giao diện WPF. |
| [`docs/design/stitch-prompts.md`](docs/design/stitch-prompts.md) | Prompt cho từng màn, viết bằng skill `enhance-prompt`. Dán vào [stitch.withgoogle.com](https://stitch.withgoogle.com) hoặc dùng skill `generate-design` (cần Stitch API key) để sinh bản vẽ. |
| `docs/design/preview/` | Bản xem trước HTML của màn Tổng quan (sáng/tối), dùng số liệu minh hoạ |

Font nhúng trong app: Be Vietnam Pro và JetBrains Mono (giấy phép OFL, xem `licenses/`). Biểu tượng: Segoe Fluent Icons trên Windows 11, Segoe MDL2 Assets trên Windows 10.

## Tải về

Vào mục [Releases](https://github.com/quangns/manager_windows/releases), có 2 bản:

| Bản | File | Dùng khi |
|---|---|---|
| **Portable** | `ManagerWindows-<version>-win-x64-portable.zip` | Chỉ 1 file exe + hướng dẫn: chép exe vào đâu cũng chạy, không cần quyền admin, không cài gì vào máy |
| **Cài đặt** | `ManagerWindows-<version>-win-x64.zip` | Muốn đủ tính năng (dịch vụ nền dọn thư mục hệ thống, đếm số lần mở app, ưu tiên cả process chạy quyền admin) |

Cả 2 bản đã kèm .NET runtime, không cần cài thêm gì. Hai bản build từ cùng một mã nguồn; bản portable chỉ khác ở chỗ gộp thành một file exe và lưu dữ liệu cạnh exe (script đóng gói tự kiểm tra không lẫn hai bản). Mỗi lúc chỉ chạy được một bản: mở bản này khi bản kia đang chạy dưới khay, app sẽ hỏi có đóng bản kia không. Phiên bản và loại bản hiện ở góc trên bên trái cửa sổ. Kiểm tra file tải về bằng `SHA256SUMS.txt`.

### Bản portable

Gói portable chỉ có `ManagerWindows.App.exe` (đã gồm .NET runtime, thư viện, font, luật dọn rác) và `Huong-dan-su-dung.txt`.

1. Chép `ManagerWindows.App.exe` vào một thư mục riêng, ví dụ `D:\Tools\ManagerWindows\`.
2. Chạy exe; lần đầu mất vài giây để giải nén thư viện vào thư mục tạm. Biểu tượng app nằm ở khay hệ thống.
3. Mọi dữ liệu nằm trong thư mục `data` cạnh file exe. Mặc định app không tự chạy cùng Windows; bật trong phần Cài đặt nếu cần.

Bản portable dọn rác của người dùng, gợi ý file, chuyển data, thống kê thời gian dùng và ưu tiên app như bản cài đặt. Những phần cần dịch vụ nền thì không có:
- Không dọn được `C:\Windows\Temp` và cache Windows Update.
- Không đếm được số lần mở app.

Trước khi xoá thư mục portable, nếu đã chuyển data app sang ổ khác, hãy bấm Hoàn tác trong app. Nếu không hoàn tác thì Zalo… vẫn chạy bình thường vì junction vẫn còn.

### Bản cài đặt

Gói gọn bên ngoài chỉ có `ManagerWindows.App.exe`, `install.cmd`, `uninstall.cmd`, `Huong-dan-su-dung.txt`; dịch vụ nền, thư viện, luật dọn rác và script cài đặt nằm trong thư mục `lib\` (giữ nguyên cạnh `install.cmd`). Thư mục cài đặt `C:\Program Files\ManagerWindows` có cùng cấu trúc.

1. Giải nén cả thư mục rồi chạy `install.cmd` (Windows sẽ hỏi quyền admin). Cập nhật bản mới cũng chạy lại `install.cmd`.
   - App được cài vào `C:\Program Files\ManagerWindows` và đăng ký dịch vụ nền `ManagerWindowsService`.
   - Shortcut được tạo trong Start Menu; app tự mở và nằm ở khay hệ thống.
2. Gỡ cài đặt: chạy `uninstall.cmd` (trong gói hoặc trong thư mục cài đặt). Mặc định các thư mục data đã chuyển sẽ được đưa về lại ổ C trước khi gỡ.
   - Thêm `-KeepRelocations` (`uninstall.cmd -KeepRelocations`) để giữ nguyên các thư mục đã chuyển.
   - Thêm `-RemoveData` để xoá luôn thống kê và cấu hình.

## Bản macOS (Manager Mac)

Bản cho macOS 14 Sonoma trở lên, tải ở mục Releases:
- `ManagerMac-<version>-macos-arm64.zip`: Mac chip Apple (M1–M4).
- `ManagerMac-<version>-macos-x64.zip`: Mac chip Intel.

**Tính năng (bản đầu):**
- Tổng quan: ổ đĩa, RAM, tổng đã dọn.
- Dọn rác: cache Xcode/Simulator, cache Homebrew/npm/pip/Yarn, cache trình duyệt, log cũ. `~/Library/Caches` và Thùng rác phải xem file trước khi dọn.
- Gợi ý dọn file: file lâu không sửa, file lớn, bộ cài `.dmg`/`.pkg` cũ, file trùng.
- Tiến trình: CPU/RAM theo app (gộp các tiến trình Helper vào app chính), end task, khởi động lại app, giải phóng RAM bằng lệnh `purge`.
- Cài đặt: ngôn ngữ, sáng/tối theo macOS, tự chạy khi đăng nhập (LaunchAgent).
- Biểu tượng trên thanh menu.

**Chưa có trên macOS:** ngủ đông app (macOS không cho biết khi nào app đang tạm dừng được bấm vào, nên không đánh thức an toàn được; macOS đã có sẵn App Nap và nén bộ nhớ), chuyển data app, thống kê thời gian dùng, ưu tiên CPU, tiến trình sót, phân tích Excel, dịch vụ nền.

**Cài đặt:** app chỉ ký ad-hoc, chưa ký bằng Apple Developer ID nên lần đầu mở phải vào System Settings > Privacy & Security > **Open Anyway**, hoặc chạy `xattr -dr com.apple.quarantine /Applications/ManagerMac.app`. Chi tiết trong file `Huong-dan-macOS.txt` kèm theo.

Mã nguồn: `src/ManagerMac` (Avalonia 11), dùng chung `ManagerWindows.Core`/`Storage` với bản Windows. Đóng gói từ Linux bằng `scripts/build-mac.sh`, ký ad-hoc bằng `rcodesign`. `tools/MacPreview` dựng giao diện ở chế độ headless để bắt lỗi lúc chạy và chụp ảnh (`docs/design/preview/mac/`).

![Manager Mac – Tiến trình (tối)](docs/design/preview/mac/processes-dark.png)

## Kiến trúc

```
ManagerWindows.App (WPF, khay hệ thống, quyền người dùng)
 ├─ dọn rác phần của user, chuyển data AppData (junction), theo dõi cửa sổ đang dùng
 ├─ điều phối ưu tiên process của user, phân tích file Excel
 └─ named pipe ──► ManagerWindows.Service (Windows Service, quyền SYSTEM)
                    ├─ dọn rác hệ thống theo lịch (Windows\Temp, SoftwareDistribution, WER…)
                    ├─ đếm số lần mở app (WMI Win32_ProcessStartTrace)
                    └─ đặt ưu tiên cho process chạy quyền admin
```

| Project | Nội dung |
|---|---|
| `src/ManagerWindows.Core` | Logic thuần, test được trên Linux: luật dọn, quét gợi ý, file trùng, máy trạng thái chuyển data, gom phiên sử dụng, chính sách ưu tiên, phân tích xlsx, lịch |
| `src/ManagerWindows.Platform` | Win32 API: junction (FSCTL_SET_REPARSE_POINT), Thùng rác, SetPriorityClass/EcoQoS, cửa sổ đang dùng, thời gian rảnh, thông tin Office |
| `src/ManagerWindows.Storage` | SQLite: lịch sử dọn, các lần chuyển data, phiên sử dụng, số lần mở |
| `src/ManagerWindows.Ipc` | Định nghĩa lệnh và client của named pipe |
| `src/ManagerWindows.Service` | Windows Service |
| `src/ManagerWindows.App` | Giao diện WPF + các vòng lặp chạy nền |
| `rules/cleanup-rules.json` | Luật dọn rác (sửa được, không cần build lại) |
| `rules/app-profiles.json` | Hồ sơ thư mục data của từng app (Zalo, Telegram…) |

Dữ liệu được lưu tại:
- `%LOCALAPPDATA%\ManagerWindows\`: `config.json`, `data.db` và thư mục `logs\`, riêng cho từng user.
- `%ProgramData%\ManagerWindows\`: `service.db` và `service.json` (lịch dọn rác hệ thống).

Mọi dữ liệu chỉ lưu trên máy, không gửi đi đâu.

## Các nguyên tắc an toàn

- **Dọn rác:**
  - Luôn có bước chạy thử để xem trước dung lượng.
  - Sau khi chạy thử, bấm "Xem file" ở từng mục để xem danh sách file và bỏ tick những file muốn giữ. Mục "nên xem lại" (cache Zalo, Teams, Thùng rác) bắt buộc phải xem trước khi dọn; dọn tự động và nút "Dọn rác ngay" luôn bỏ qua các mục này.
  - Không đi theo junction/symlink khi quét, nên không xoá nhầm data đã chuyển sang ổ khác.
  - Chặn các luật trỏ vào gốc ổ đĩa, thư mục Windows hoặc hồ sơ người dùng.
  - Ngay trước khi xoá, kiểm tra lại tuổi của từng file. File đang mở thì bỏ qua.
- **Chuyển data:**
  - Kiểm tra trước: ổ đích phải là NTFS, là ổ gắn trong (không dùng USB/ổ mạng), đủ dung lượng và khác ổ với nguồn.
  - Đóng app trước khi chuyển; copy bằng robocopy rồi so khớp số file và tổng dung lượng.
  - Bản gốc được đổi tên thành `*.mwbak` và giữ lại để hoàn tác.
  - Mỗi bước được lưu lại, nên nếu bị ngắt giữa chừng (mất điện…) vẫn làm tiếp hoặc hoàn tác được.
  - Mỗi giờ app kiểm tra junction còn trỏ đúng chỗ không.
  - Thư mục đích (`D:\AppDataMoved\<user>`) chỉ user đó, SYSTEM và Administrators truy cập được.
- **Ưu tiên tài nguyên:**
  - Không bao giờ dùng mức Realtime; mức High chỉ dùng khi người dùng tự bật.
  - Không đụng tiến trình hệ thống và phần mềm diệt virus.
  - Khi tắt tính năng hoặc thoát app, các process được trả về mức ưu tiên ban đầu.
- **Dịch vụ nền:**
  - Named pipe chỉ cho phép người dùng đang đăng nhập (INTERACTIVE), SYSTEM và Administrators kết nối.
  - Chỉ nhận một số lệnh cố định, không có lệnh nào nhận đường dẫn để xoá.
  - Chỉ đổi ưu tiên cho process cùng phiên đăng nhập với người gửi lệnh.

## Phát triển

Yêu cầu .NET 10 SDK (LTS). Mọi điều khiển WPF được vẽ bằng style riêng theo `docs/design/DESIGN.md` (không phụ thuộc theme Fluent/Aero của máy). Code build và test được trên Linux (các project Windows nhờ `EnableWindowsTargeting`), nhưng app chỉ chạy được trên Windows.

```bash
dotnet test tests/ManagerWindows.Core.Tests      # 47 test: Core + Storage + IPC
./scripts/build-release.sh                       # → dist/*.zip: bản cài đặt + bản portable + SHA256SUMS.txt
```

Trong container ChatWork, SDK nằm ở `/workspace/.toolchain/dotnet`; đặt `DOTNET=/workspace/.toolchain/dotnet/dotnet` nếu `dotnet` không có trong PATH.

Mẫu CI chạy trên máy Windows thật nằm ở `docs/ci/windows-build.yml`; copy sang `.github/workflows/` để bật.

### Thêm app vào danh sách chuyển data

Thêm một mục vào `rules/app-profiles.json`:

```json
{
  "id": "myapp",
  "name": "My App",
  "processNames": [ "MyApp" ],
  "folders": [ "%APPDATA%\\MyApp" ],
  "notes": { "vi": "Ghi chú", "en": "Note" }
}
```

App nào không chạy được sau junction (Outlook, OneDrive, WSL…) thì đặt `"method": "Manual"` và ghi hướng dẫn cách chuyển vào `notes`.

## Hạn chế đã biết

- Thời gian dùng app được tính theo cửa sổ đang dùng và có thao tác phím/chuột. Xem video lâu không chạm chuột sẽ bị tính là máy rảnh sau 2 phút (chỉnh `Usage.IdleSeconds` trong `config.json`).
- File Excel đang mở được đoán từ tiêu đề cửa sổ và danh sách file mở gần đây. File mở từ SharePoint/OneDrive dạng URL không phân tích được, nhưng app vẫn phát hiện Excel nặng theo RAM/CPU.
- Định dạng .xls và .xlsb là nhị phân nên chỉ có gợi ý chung.
- Junction chỉ hoạt động trên ổ NTFS gắn trong máy.

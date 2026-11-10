# Manager Windows – Dọn rác, giải phóng ổ C, tăng tốc Windows 10/11

[![Release](https://img.shields.io/github/v/release/quangns/manager_windows?label=phi%C3%AAn%20b%E1%BA%A3n)](https://github.com/quangns/manager_windows/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/quangns/manager_windows/total?label=l%C6%B0%E1%BB%A3t%20t%E1%BA%A3i)](https://github.com/quangns/manager_windows/releases)
![Windows 10/11 x64](https://img.shields.io/badge/Windows-10%20%7C%2011%20x64-0078D6?logo=windows)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)

**Manager Windows** là phần mềm miễn phí, chạy nền trên Windows 10/11, gom vào một app những việc thường phải dùng nhiều công cụ riêng:

- **Dọn file rác** định kỳ.
- **Giải phóng ổ C đầy**: chuyển data Zalo, Telegram, Chrome… sang ổ D mà app vẫn chạy bình thường.
- **Phân tích dung lượng ổ đĩa** kiểu TreeSize.
- **Quản lý phân vùng ổ cứng** kiểu MiniTool Partition Wizard.
- **Chụp màn hình** kiểu Lightshot.
- **Ngủ đông app** và **ngủ đông tab trình duyệt** (Chrome, Cốc Cốc, Brave) không dùng, **ưu tiên CPU** cho app hay dùng, xử lý **file Excel nặng**.
- **Mini monitor** CPU / GPU / RAM / ổ đĩa luôn hiện trên cùng.

Giao diện tiếng Việt và tiếng Anh, chế độ sáng/tối. Dữ liệu chỉ lưu trên máy, không gửi đi đâu.

> *English:* Manager Windows is a free all-in-one Windows 10/11 utility: junk file cleaner, disk space analyzer (TreeSize-style), "C: drive full" fixer that moves app data (Zalo, Telegram, Chrome…) to another drive via NTFS junctions, partition manager (resize / move / merge, MiniTool-style), Lightshot-style screenshot tool with annotations, app hibernation, CPU priority booster, always-on-top window pinning and a transparent CPU/GPU/RAM overlay. [See the English summary](#english-summary).

**[⬇ Tải bản mới nhất](https://github.com/quangns/manager_windows/releases/latest)**

![Manager Windows – màn hình Tổng quan](docs/design/preview/overview-light.png)

## Mục lục

- [Tính năng](#tính-năng)
- [Tải về và cài đặt](#tải-về-và-cài-đặt)
- [Câu hỏi thường gặp](#câu-hỏi-thường-gặp)
- [Nguyên tắc an toàn](#nguyên-tắc-an-toàn)
- [Dành cho nhà phát triển](#dành-cho-nhà-phát-triển)
- [Hạn chế đã biết](#hạn-chế-đã-biết)
- [English summary](#english-summary)

## Tính năng

| Tính năng | Thay thế cho | Bản portable | Bản cài đặt |
|---|---|:-:|:-:|
| [Dọn file rác định kỳ](#dọn-file-rác-tự-động) | CCleaner, Disk Cleanup | ✔ (phần của user) | ✔ (cả thư mục hệ thống) |
| [Gợi ý file nên xoá, tìm file trùng](#gợi-ý-dọn-file-và-tìm-file-trùng) | Duplicate Cleaner | ✔ | ✔ |
| [Phân tích dung lượng ổ đĩa](#phân-tích-dung-lượng-ổ-đĩa-kiểu-treesize) | TreeSize, WinDirStat | ✔ | ✔ |
| [Chuyển data app khỏi ổ C](#chuyển-data-zalo-telegram-chrome-sang-ổ-khác) | Tạo symlink thủ công | ✔ | ✔ |
| [Quản lý phân vùng ổ cứng](#quản-lý-phân-vùng-ổ-cứng) | MiniTool Partition Wizard, Disk Management | ✔ (cần quyền Admin) | ✔ |
| [Chụp màn hình, vẽ chú thích](#chụp-màn-hình-kiểu-lightshot) | Lightshot, Snipping Tool | ✔ | ✔ |
| [Ghim cửa sổ luôn trên cùng](#ghim-cửa-sổ-luôn-trên-cùng-và-mini-monitor) | PowerToys Always on Top | ✔ | ✔ |
| [Mini monitor CPU/GPU/RAM/ổ đĩa](#ghim-cửa-sổ-luôn-trên-cùng-và-mini-monitor) | Rainmeter, MSI Afterburner OSD | ✔ | ✔ |
| [Theo dõi tiến trình, ngủ đông app](#theo-dõi-tiến-trình-và-ngủ-đông-app) | Task Manager, Process Lasso | ✔ | ✔ |
| [Ngủ đông tab trình duyệt](#ngủ-đông-tab-trình-duyệt-chrome-cốc-cốc-brave) | The Great Suspender, Auto Tab Discard | ✔ (hỏi quyền Admin khi lưu) | ✔ |
| [Thống kê thời gian dùng app](#thống-kê-thời-gian-dùng-app-và-ưu-tiên-cpu) | ManicTime | ✔ (không đếm số lần mở) | ✔ |
| [Ưu tiên CPU, tăng tốc Excel nặng](#thống-kê-thời-gian-dùng-app-và-ưu-tiên-cpu) | Process Lasso | ✔ | ✔ (cả app chạy quyền admin) |

### Dọn file rác tự động

- Dọn theo lịch:
  - file tạm (`%TEMP%`, `C:\Windows\Temp`);
  - cache Windows Update;
  - cache trình duyệt, Teams, Zalo;
  - dump lỗi, log cũ, Thùng rác.
- Luôn **chạy thử** trước để xem sẽ giải phóng bao nhiêu. Bấm "Xem file" ở từng mục và bỏ tick file muốn giữ.
- Mục nhạy cảm (cache Zalo, Teams, Thùng rác) bắt buộc phải xem trước khi dọn. Lịch dọn tự động luôn bỏ qua các mục này.
- Luật dọn nằm trong file `rules/cleanup-rules.json`, sửa được mà không cần build lại.

### Gợi ý dọn file và tìm file trùng

- Tìm:
  - file lâu không mở;
  - file lớn;
  - bộ cài cũ trong Downloads;
  - **file trùng nội dung** (so mã băm).
- App chỉ gợi ý, không bao giờ tự xoá. File bạn chọn xoá được chuyển vào Thùng rác.

### Phân tích dung lượng ổ đĩa (kiểu TreeSize)

- **Quét:** cả ổ (C:, D:…) hoặc một thư mục.
- **Cây thư mục:** xếp từ lớn đến bé, mỗi dòng có:
  - % so với thư mục cha;
  - số file, số thư mục con;
  - ngày sửa gần nhất.
- **Danh sách 500 file lớn nhất** trên toàn vùng quét.
- **Chuột phải:** mở trong Explorer, quét riêng thư mục, chuyển vào Thùng rác.
- **An toàn:** không đi vào junction/symlink nên không đếm trùng. Chặn xoá thư mục Windows, Program Files, thư mục người dùng.

### Chuyển data Zalo, Telegram, Chrome sang ổ khác

Cách xử lý **ổ C đầy do Zalo** và các app chỉ cho lưu vào ổ C:

- App chuyển thư mục data (Zalo, Telegram, Chrome/Edge, backup iPhone…) sang ổ D:.
- Ở đường dẫn cũ trên ổ C, app tạo một **junction (soft link)**. App kia vẫn thấy data "nằm ở ổ C" nên chạy bình thường, nhưng dung lượng thật nằm ở ổ D:.

Quy trình chuyển:

1. Kiểm tra trước khi chuyển: ổ đích phải là NTFS gắn trong, còn đủ chỗ.
2. Chép bằng robocopy, rồi so khớp số file và tổng dung lượng.
3. Giữ bản gốc để **Hoàn tác** khi cần. Bị ngắt giữa chừng (mất điện…) vẫn làm tiếp hoặc hoàn tác được.

Thêm app khác bằng cách khai báo trong `rules/app-profiles.json` ([hướng dẫn](#thêm-app-vào-danh-sách-chuyển-data)).

### Quản lý phân vùng ổ cứng

- **Sơ đồ đĩa** theo tỉ lệ, giống MiniTool Partition Wizard / Disk Management.
- **Thao tác cơ bản:**
  - thu nhỏ / mở rộng phân vùng;
  - tạo, xoá, format (NTFS / exFAT / FAT32);
  - đổi ký tự ổ, đổi nhãn;
  - kiểm tra lỗi (`chkdsk /scan`);
  - khởi tạo đĩa mới (GPT/MBR).
- **Di chuyển phân vùng, mở rộng sang trái, gộp phân vùng:**
  - App chép dữ liệu theo từng sector. Mỗi đoạn được đọc lại để kiểm tra.
  - App ghi nhật ký tiến độ, nên mất điện giữa chừng vẫn tiếp tục được.
  - Dời được phân vùng Recovery để **mở rộng ổ C:**.
- **Bảo vệ:**
  - Không cho xoá hay format phân vùng Windows, EFI, MSR, Recovery.
  - Thao tác nguy hiểm phải gõ chữ xác nhận, và app hiện khung cảnh báo rủi ro trước khi chạy.
  - Mọi thay đổi chạy trong cửa sổ riêng có quyền Administrator (UAC).

> ⚠ Sao lưu dữ liệu quan trọng trước khi di chuyển, gộp hoặc xoá phân vùng.

### Chụp màn hình kiểu Lightshot

Nhấn **`Ctrl+Alt+A`**: màn hình được đóng băng **đúng kích thước thật**, không thu nhỏ.

- **Chọn vùng:**
  - kéo chuột để khoanh vùng tự do, hoặc bấm vào một cửa sổ để chọn cả cửa sổ đó;
  - kéo 8 cạnh/góc để chỉnh kích thước, kéo bên trong để di chuyển vùng;
  - phím mũi tên dời vùng chọn 1 pixel.
- **Vẽ ngay trên màn hình:**
  - khung, elip, mũi tên, bút vẽ tự do;
  - tô sáng, **che kín** (bôi đen thông tin nhạy cảm), **đánh số thứ tự**;
  - 8 màu, 3 độ dày nét. Phím `1`–`7` đổi công cụ, giữ `Shift` để vẽ hình vuông/tròn hoặc mũi tên thẳng 45°.
- **Xuất ảnh:**
  - `Ctrl+C` / `Enter` sao chép;
  - `Ctrl+S` lưu PNG, `Ctrl+Shift+S` lưu thành file khác;
  - `Ctrl+Z` hoàn tác, `Esc` huỷ.
- **Phím tắt khác:** `Ctrl+Alt+W` chụp cửa sổ đang dùng, `Ctrl+Alt+F` chụp toàn màn hình. Mọi phím tắt đổi được trong tab **Công cụ**.

### Ghim cửa sổ luôn trên cùng và mini monitor

- **Ghim cửa sổ app khác luôn trên cùng (always on top):**
  - nhấn `Ctrl+Alt+T` khi đang ở cửa sổ cần ghim;
  - kéo tâm ngắm thả lên cửa sổ;
  - hoặc chọn trong danh sách cửa sổ đang mở.
- **Mini monitor** (`Ctrl+Alt+M`):
  - hiện CPU, GPU, RAM, ổ đĩa, luôn nằm trên cùng;
  - nền trong suốt hoặc khối màu tuỳ chọn (chỉnh độ đậm); chữ sáng/tối tự chọn theo nền và chế độ sáng/tối, hoặc tự đặt;
  - chọn chỉ số muốn hiện, xếp ngang hoặc dọc, 3 cỡ chữ;
  - khoá vị trí và cho chuột bấm xuyên qua.

### Theo dõi tiến trình và ngủ đông app

- **Theo dõi:** xem app nào chiếm CPU, RAM, ổ đĩa. Các tiến trình được gộp theo app và chia nhóm Ứng dụng / Chạy ngầm / Hệ thống.
- **Chẩn đoán máy lag:** khi CPU, RAM hoặc ổ đĩa quá tải, app chỉ ra app gây ra.
- **Ngủ đông app không dùng:**
  - App không dùng một thời gian thì bị tạm dừng để trả lại RAM/CPU. Bấm vào app là chạy tiếp đúng trạng thái cũ.
  - Không áp dụng cho app chat, mail, VPN, bộ gõ, app đang phát nhạc.
  - Có danh sách "không bao giờ cho ngủ": kéo tâm ngắm thả lên cửa sổ app (cửa sổ được tô khung để chọn đúng app, kể cả app Store/UWP và app đang treo).
- **Dọn dẹp:** end task, giải phóng RAM, tìm tiến trình sót/treo, và **app đã đóng cửa sổ nhưng vẫn chạy ngầm** (vd. Chrome còn hàng chục tiến trình con giữ RAM) — báo ngay ở tab Tiến trình, bấm một nút để dừng. Có thể tick **tự dừng** app chạy ngầm lâu không hoạt động và app đã đóng mà vẫn chạy (trừ danh sách "không bao giờ cho ngủ").

### Ngủ đông tab trình duyệt (Chrome, Cốc Cốc, Brave)

Trình duyệt mở nhiều tab là thủ phạm ngốn RAM hàng đầu. Manager Windows bật **Memory Saver** có sẵn của trình duyệt bằng chính sách Windows:

- Tab nền lâu không dùng được trình duyệt **giải phóng bộ nhớ**; bấm vào tab là **tự tải lại**.
- Chọn mức tiết kiệm **Vừa / Cân bằng / Tối đa** (mức càng cao, tab càng sớm được cho ngủ).
- **Danh sách trang không bao giờ cho ngủ**: mặc định có Zalo web, Google Meet, Teams, Telegram web.
- Thẻ hiện trạng thái từng trình duyệt và RAM đang dùng.
- **An toàn:** chỉ ghi 3 mục chính sách cố định; không đè chính sách quản trị viên đã đặt. Tắt tính năng hoặc gỡ cài đặt sẽ xoá đúng những gì app đã đặt.

Bản cài đặt nhờ dịch vụ nền ghi chính sách; bản portable hỏi quyền Administrator (UAC) một lần mỗi khi bấm Lưu. Trình duyệt sẽ hiện dòng "Do tổ chức của bạn quản lý": đây là bình thường khi có chính sách.

### Thống kê thời gian dùng app và ưu tiên CPU

- **Thống kê:** thời gian dùng thật của từng app (không tính lúc máy rảnh), số lần mở, biểu đồ theo giờ.
- **Ưu tiên CPU:**
  - App dùng nhiều nhất hoặc app bạn ghim được ưu tiên CPU.
  - App nền (OneDrive, updater…) bị hạ ưu tiên và chạy chế độ tiết kiệm điện EcoQoS.
- **Excel nặng, chậm:**
  - App tự ưu tiên tài nguyên cho Excel.
  - App đọc file `.xlsx` để chỉ ra nguyên nhân: vùng dữ liệu "ma", quá nhiều style, hàm INDIRECT/OFFSET, liên kết ngoài, ảnh lớn, Office 32-bit… Mỗi nguyên nhân có hướng dẫn xử lý.

## Tải về và cài đặt

Tải ở mục **[Releases](https://github.com/quangns/manager_windows/releases/latest)**. Cả hai bản đã kèm .NET runtime, không cần cài thêm gì.

| Bản | File | Dùng khi |
|---|---|---|
| **Portable** | `ManagerWindows-<version>-win-x64-portable.zip` | Không muốn cài vào máy, không có quyền admin. Chỉ 1 file exe, chép vào đâu cũng chạy. |
| **Cài đặt** | `ManagerWindows-<version>-win-x64.zip` | Muốn đủ tính năng: dịch vụ nền dọn thư mục hệ thống, đếm số lần mở app, ưu tiên cả app chạy quyền admin. |

Kiểm tra file tải về bằng `SHA256SUMS.txt` đi kèm.

### Bản portable

1. Chép `ManagerWindows.App.exe` vào một thư mục riêng, ví dụ `D:\Tools\ManagerWindows\`.
2. Chạy file exe. Lần đầu mất vài giây để giải nén thư viện. Biểu tượng app nằm ở khay hệ thống.
3. Dữ liệu nằm trong thư mục `data` cạnh file exe. App mặc định không tự chạy cùng Windows; bật trong Cài đặt nếu cần.

Bản portable không dọn được `C:\Windows\Temp`, cache Windows Update và không đếm được số lần mở app, vì các việc này cần dịch vụ nền. Nếu đã chuyển data app sang ổ khác, hãy bấm **Hoàn tác** trước khi xoá thư mục portable.

### Bản cài đặt

```
ManagerWindows\
  ManagerWindows.App.exe     chạy app
  install.cmd                cài đặt / cập nhật (hỏi quyền admin)
  uninstall.cmd              gỡ cài đặt
  Huong-dan-su-dung.txt      hướng dẫn
  lib\                       dịch vụ nền, thư viện, luật dọn rác, script cài/gỡ
```

1. **Cài đặt / cập nhật:** giải nén cả thư mục rồi chạy `install.cmd`.
   - App được cài vào `C:\Program Files\ManagerWindows` và đăng ký dịch vụ nền `ManagerWindowsService`.
   - Shortcut được tạo trong Start Menu.
2. **Gỡ cài đặt:** chạy `uninstall.cmd`. Mặc định data đã chuyển sang ổ khác được đưa về lại ổ C trước khi gỡ.
   - `uninstall.cmd -KeepRelocations`: giữ nguyên các thư mục đã chuyển.
   - `uninstall.cmd -RemoveData`: xoá luôn thống kê và cấu hình.

**Nơi lưu dữ liệu:**
- `%LOCALAPPDATA%\ManagerWindows\`: cấu hình, thống kê, log. Đổi được trong **Cài đặt > Dữ liệu**.
- `%ProgramData%\ManagerWindows\`: dữ liệu của dịch vụ nền.

## Câu hỏi thường gặp

**Ổ C bị đầy, dọn thế nào nhanh nhất?**
Làm lần lượt:
1. Chạy **Dọn rác** (bấm chạy thử trước để xem giải phóng được bao nhiêu).
2. Mở tab **Dung lượng ổ đĩa**, quét ổ C để tìm thư mục chiếm chỗ nhiều nhất.
3. Nếu thủ phạm là data Zalo/Telegram/Chrome, dùng **Chuyển data app** để đưa sang ổ D.

**Chuyển data Zalo sang ổ D có mất tin nhắn không?**
Không. Data được chép đầy đủ và so khớp trước khi đổi. Zalo vẫn đọc ở đường dẫn cũ nhờ junction. Bản gốc được giữ lại để hoàn tác.

**Có thay được MiniTool Partition Wizard không?**
Được phần lớn việc thường làm: thu nhỏ / mở rộng, tạo, xoá, format, di chuyển, mở rộng sang trái, gộp phân vùng. Hai việc chưa làm được:
- dời chính phân vùng Windows (C:) khi Windows đang chạy;
- chuyển MBR↔GPT khi đĩa còn dữ liệu. Với ổ Windows có thể dùng lệnh `mbr2gpt` có sẵn.

**Chụp màn hình có giống Lightshot không?**
Có: màn hình đóng băng, kéo thả vùng chọn tự do, thanh công cụ bám theo vùng chọn, vẽ ngay trên màn hình rồi `Ctrl+C` hoặc `Ctrl+S`. Ngoài ra có thêm che kín thông tin và đánh số thứ tự.

**Ngủ đông tab trình duyệt có làm mất nội dung đang gõ không?**
Trình duyệt không cho ngủ tab đang phát nhạc/video, đang dùng micro/camera hay đang có form chưa gửi. Tab đã ngủ thì bấm vào sẽ tải lại trang. Trang chat, họp trực tuyến nên để trong danh sách "không bao giờ cho ngủ".

**App có gửi dữ liệu ra ngoài không?**
Không. Mọi thống kê và cấu hình chỉ lưu trên máy của bạn.

**Ngủ đông app khác gì tắt app?**
App bị tạm dừng chứ không bị tắt, nên không mất dữ liệu đang làm. Bấm vào app trên taskbar là chạy tiếp. Nếu có app bị đứng, chuột phải biểu tượng ở khay hệ thống và chọn **"Đánh thức tất cả"**.

## Nguyên tắc an toàn

- **Dọn rác:**
  - Luôn có bước chạy thử.
  - Không đi theo junction/symlink.
  - Chặn luật trỏ vào gốc ổ, thư mục Windows, hồ sơ người dùng.
  - Ngay trước khi xoá, kiểm tra lại tuổi từng file; bỏ qua file đang mở.
- **Chuyển data:**
  - Đóng app trước khi chuyển; so khớp số file và dung lượng sau khi chép.
  - Lưu từng bước để làm tiếp hoặc hoàn tác.
  - Mỗi giờ kiểm tra junction còn trỏ đúng không.
  - Thư mục đích chỉ chủ tài khoản, SYSTEM và Administrators truy cập được.
- **Ưu tiên tài nguyên:**
  - Không bao giờ dùng mức Realtime.
  - Không đụng tiến trình hệ thống và phần mềm diệt virus.
  - Trả mức ưu tiên cũ khi tắt tính năng hoặc thoát app.
- **Phân vùng:**
  - Chạy trong cửa sổ quyền Administrator riêng, không qua dịch vụ nền.
  - Chặn máy đang chạy pin, phân vùng BitLocker, ổ đang có file mở.
- **Dịch vụ nền:**
  - Named pipe chỉ cho người dùng đang đăng nhập, SYSTEM và Administrators kết nối.
  - Chỉ nhận một số lệnh cố định, không có lệnh nào nhận đường dẫn để xoá.

## Dành cho nhà phát triển

### Kiến trúc

```
ManagerWindows.App (WPF, khay hệ thống, quyền người dùng)
 ├─ dọn rác phần của user, chuyển data AppData (junction), theo dõi cửa sổ đang dùng
 ├─ ưu tiên process, phân tích Excel, chụp màn hình, phân vùng (cửa sổ Admin riêng)
 └─ named pipe ──► ManagerWindows.Service (Windows Service, quyền SYSTEM)
                    ├─ dọn rác hệ thống theo lịch (Windows\Temp, SoftwareDistribution, WER…)
                    ├─ đếm số lần mở app (WMI Win32_ProcessStartTrace)
                    └─ đặt ưu tiên cho process chạy quyền admin
```

| Project | Nội dung |
|---|---|
| `src/ManagerWindows.Core` | Logic thuần, test được trên Linux: luật dọn, quét gợi ý, file trùng, chuyển data, thống kê, ưu tiên, phân tích xlsx, kế hoạch phân vùng, hình học vùng chụp |
| `src/ManagerWindows.Platform` | Win32 API: junction, Thùng rác, SetPriorityClass/EcoQoS, chụp màn hình GDI, WMI Storage, đọc/ghi ổ vật lý |
| `src/ManagerWindows.Storage` | SQLite: lịch sử dọn, các lần chuyển data, phiên sử dụng |
| `src/ManagerWindows.Ipc` | Lệnh và client named pipe |
| `src/ManagerWindows.Service` | Windows Service |
| `src/ManagerWindows.App` | Giao diện WPF và các tác vụ chạy nền |
| `rules/` | Luật dọn rác và hồ sơ thư mục data từng app (sửa được, không cần build lại) |

### Build và test

Cần .NET 10 SDK. Code build và test được trên Linux (`EnableWindowsTargeting`), nhưng app chỉ chạy trên Windows.

```bash
dotnet test tests/ManagerWindows.Core.Tests   # Core + Storage + IPC
./scripts/build-release.sh                    # → dist/: bản cài đặt, bản portable, SHA256SUMS.txt
```

- **Giao diện:** theo hệ thống thiết kế trong [`docs/design/DESIGN.md`](docs/design/DESIGN.md). Font nhúng Be Vietnam Pro và JetBrains Mono (giấy phép OFL).
- **CI:** mẫu chạy trên máy Windows nằm ở `docs/ci/windows-build.yml`.

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

App không chạy được qua junction (Outlook, OneDrive, WSL…) thì đặt `"method": "Manual"` và ghi cách chuyển vào `notes`.

## Hạn chế đã biết

- **Thời gian dùng app:** tính theo cửa sổ đang dùng có thao tác phím/chuột. Xem video lâu không chạm chuột sẽ bị coi là máy rảnh sau 2 phút; chỉnh bằng `Usage.IdleSeconds` trong `config.json`.
- **Excel:**
  - File Excel mở từ SharePoint/OneDrive dạng URL không phân tích được.
  - File `.xls`/`.xlsb` chỉ có gợi ý chung.
- **Chuyển data:** junction chỉ hoạt động trên ổ NTFS gắn trong máy.
- **Chụp màn hình và phân vùng:** dựa vào API Windows. Hãy báo lỗi ở mục [Issues](https://github.com/quangns/manager_windows/issues) nếu gặp vấn đề trên máy của bạn.

## English summary

**Manager Windows** is a free, all-in-one system utility for **Windows 10 and Windows 11 (x64)**, with a Vietnamese/English UI:

- **Junk file cleaner:** temp files, Windows Update cache, browser/Teams/Zalo cache, crash dumps. Scheduled cleaning, always with a dry run first.
- **Free up C: drive:**
  - **Disk space analyzer** like TreeSize/WinDirStat.
  - Large/old/duplicate file finder.
  - **Move app data to another drive** (Zalo, Telegram, Chrome, iPhone backups) using NTFS junctions, with one-click undo.
- **Partition manager** like MiniTool Partition Wizard:
  - shrink, extend, create, delete, format;
  - move partitions, extend to the left, merge partitions, with resumable sector copy;
  - move the Recovery partition to extend C:.
- **Screenshot tool like Lightshot:**
  - frozen full-size screen, free drag selection with resize handles;
  - draw on the screen: box, ellipse, arrow, pen, highlight, redact, step numbers;
  - copy or save to PNG.
- **Always on top:** pin any window.
- **Overlay:** transparent always-on-top CPU/GPU/RAM/disk monitor.
- **Performance:**
  - app hibernation (suspend unused apps, resume on click);
  - browser tab hibernation for Chrome, Cốc Cốc and Brave (turns on the built-in Memory Saver via Windows policies);
  - CPU priority boost for your most-used apps;
  - EcoQoS for background apps;
  - heavy Excel workbook analyzer.

Download the portable or installer build from **[Releases](https://github.com/quangns/manager_windows/releases/latest)**. All data stays on your PC.

---

**Từ khoá:** dọn rác máy tính, phần mềm dọn rác Windows 11, giải phóng ổ C, ổ C bị đầy, chuyển dữ liệu Zalo sang ổ D, phân tích dung lượng ổ cứng, chia lại phân vùng ổ cứng, gộp phân vùng, mở rộng ổ C, chụp màn hình Windows, phần mềm chụp màn hình giống Lightshot, ngủ đông tab Chrome, giảm RAM trình duyệt, ghim cửa sổ luôn trên cùng, theo dõi CPU GPU RAM, tăng tốc Excel, tăng tốc máy tính.

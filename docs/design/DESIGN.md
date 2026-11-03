# Design System: Manager Windows

> Tạo theo skill `taste-design` của bộ [google-labs-code/stitch-skills](https://github.com/google-labs-code/stitch-skills),
> điều chỉnh cho ứng dụng desktop Windows (WPF). File này là nguồn chuẩn duy nhất cho giao diện app.
> Khi dùng Google Stitch, dán file này vào phần design system của project; prompt từng màn nằm ở `stitch-prompts.md`.

## 1. Visual Theme & Atmosphere

Bảng điều khiển hệ thống điềm tĩnh và đáng tin, kiểu "phòng máy được dọn gọn": số liệu thật đặt lên trước, mọi thứ còn lại lùi về sau. Ba thông số chính:

- **Mật độ 6/10, kiểu "Daily App Balanced":** đủ thoáng để người không rành máy tính đọc được, đủ chặt để nhìn được toàn cảnh ổ đĩa trong một màn.
- **Độ lệch 5/10, kiểu "Offset Asymmetric":** trang Tổng quan chia lưới lệch 2:1. Thẻ ổ đĩa lớn nằm bên trái làm điểm nhìn chính, các chỉ số phụ xếp dọc bên phải.
- **Chuyển động 2/10, kiểu "Static Restrained":** đây là công cụ chạy nền, không phải trang giới thiệu. Chỉ dùng hiệu ứng mờ dần ngắn khi đổi trang và khi bấm nút; không có vòng lặp chuyển động vô hạn.
  - Đây là điểm cố ý lệch so với mặc định 6/10 của skill: app nằm cả ngày ở khay hệ thống, chuyển động liên tục sẽ gây phiền và tốn CPU, đi ngược mục đích "ưu tiên tài nguyên".

Giao diện sáng hoặc tối theo Windows, có tuỳ chọn ép sáng/tối. Cả hai chế độ dùng chung một bảng màu trung tính lạnh (Zinc), không pha xám ấm và xám lạnh lẫn nhau.

## 2. Color Palette & Roles

Chỉ có một màu nhấn duy nhất (Pine Teal, độ bão hoà < 80%), không dùng xanh neon/tím kiểu AI. Trạng thái tốt/cảnh báo/nguy hiểm dùng nhóm màu riêng và luôn đi kèm biểu tượng + chữ, không bao giờ dùng màu một mình.

### Light
- **Canvas Mist** (#FAFAFA): nền cửa sổ, vùng ngoài thẻ
- **Pure Surface** (#FFFFFF): nền thẻ, bảng, sidebar
- **Raised Surface** (#F4F4F5): nền nút phụ, hàng đang hover, rãnh thanh tiến trình
- **Charcoal Ink** (#18181B): chữ chính, số liệu lớn
- **Muted Steel** (#71717A): chữ phụ, nhãn, mô tả, thời gian (4.8:1 trên nền trắng)
- **Whisper Border** (#E4E4E7): viền thẻ 1px, đường kẻ bảng
- **Pine Teal** (#0F766E): màu nhấn duy nhất. Dùng làm nền nút chính (chữ trắng 5.5:1) và cho chữ liên kết, mục menu đang chọn, viền focus
- **Pine Wash** (#E6F2F1): nền mục menu đang chọn, nền huy hiệu "đã xem"
- **Chart Teal** (#118F80): cột biểu đồ (đã qua validator: độ sáng, độ bão hoà, tương phản ≥ 3:1 trên #FFFFFF)

### Dark
- **Canvas Night** (#09090B): nền cửa sổ (không dùng đen tuyền #000000)
- **Night Surface** (#18181B): nền thẻ, bảng, sidebar
- **Raised Night** (#27272A): nền nút phụ, hàng hover, rãnh thanh tiến trình
- **Frost Ink** (#FAFAFA): chữ chính
- **Muted Ash** (#B4B4BC): chữ phụ (8.6:1 trên Night Surface)
- **Night Border** (#34343A): viền thẻ, đường kẻ
- **Pine Teal Ink** (#3FB8A9): màu nhấn cho chữ, biểu tượng, mục menu đang chọn, vạch chỉ báo (7.3:1 trên #18181B)
- **Pine Teal Fill** (#0F766E; hover #11806F; nhấn #0B6B62): nền nút chính, **chữ trắng** (5.5:1). Không đặt chữ đen trên nền teal: đạt chuẩn trên giấy nhưng khó đọc khi nhìn thật
- **Chart Teal Night** (#2A9D8F): cột biểu đồ (đã qua validator trên #18181B)
- **Màu hệ thống:** ghi đè ControlText, Window, Highlight, Info… để chữ của điều khiển chuẩn luôn sáng trên nền tối
- **Pine Wash Night** (#12302D): nền mục menu đang chọn

### Trạng thái (light / dark)
- **Healthy Green** (#15803D / #4ADE80): dịch vụ đang chạy, junction bình thường, đã xem
- **Amber Caution** (#B45309 / #FBBF24): nên xem lại, sắp đầy (< 15% trống)
- **Signal Red** (#B91C1C / #F87171): ổ gần đầy (< 10%), junction lỗi, gợi ý Excel mức quan trọng

## 3. Typography Rules

- **Chữ chính và tiêu đề: Be Vietnam Pro** (Regular 400, Medium 500, SemiBold 600). Font sans hình học được thiết kế cho tiếng Việt, dấu không bị đè dòng.
  - Đây là điểm thay đổi so với gợi ý Geist/Satoshi của skill: hai font đó thiếu hoặc dựng dấu tiếng Việt chưa tốt, mà giao diện chính của app là tiếng Việt. Font được nhúng vào app (giấy phép OFL) nên không phụ thuộc máy người dùng.
- **Số liệu: JetBrains Mono** cho số lớn trên thẻ chỉ số (dung lượng, thời gian) và cột số trong bảng, để chữ số thẳng hàng.
- **Thang chữ:**

  | Cỡ | Dùng cho |
  |---|---|
  | 28/600 | Số liệu chính |
  | 22/600 | Tiêu đề trang |
  | 15/600 | Tiêu đề thẻ |
  | 13/400 | Chữ thường |
  | 12/500 | Nhãn nhỏ phía trên số liệu, màu Muted (viết thường như câu, không viết hoa toàn bộ) |

- **Phân cấp:** thể hiện bằng độ đậm và màu chữ (Ink/Muted), không bằng cỡ chữ to quá khổ. Tiêu đề không viết hoa toàn bộ.
- **Cấm:** Inter, font serif, mọi font hệ thống mặc định "cho có". Không dùng chữ gradient.

## 4. Component Stylings

* **Sidebar điều hướng:**
  - Rộng 232px, nền Surface, viền phải 1px Border.
  - Mỗi mục gồm biểu tượng (Segoe Fluent Icons) và nhãn, cao 40px, bo góc 8px.
  - Mục đang chọn: nền Pine Wash, chữ và biểu tượng Pine Teal, có vạch nhấn 3px ở mép trái.
  - Chân sidebar có "viên thuốc" trạng thái dịch vụ nền (chấm tròn + chữ).
* **Thanh đầu trang:** tiêu đề trang + một dòng mô tả bằng chữ Muted bên trái, nút hành động bên phải. Tối đa một nút chính màu nhấn; các nút còn lại là nút phụ.
* **Nút:**
  - Bo góc 8px, cao 34px, đệm ngang 16px. Nút chính nền Pine Teal, chữ trắng; nút phụ nền Raised Surface, viền Border.
  - Khi nhấn: nền tối đi một bậc. Không có quầng sáng (glow), không đổ bóng màu.
  - Nút dạng liên kết trong bảng chỉ có chữ màu nhấn, gạch chân khi hover.
* **Thẻ:**
  - Bo góc 12px, nền Surface, viền 1px Border, đệm trong 20px.
  - Không đổ bóng: phân tầng chỉ bằng viền và nền, vì bóng (DropShadowEffect) làm WPF vẽ lại cả bảng dài khi cuộn, tốn CPU.
  - Chỉ dùng thẻ khi cần nhóm thông tin; nội dung bảng dài thì đặt trong một thẻ duy nhất, không lồng thẻ.
* **Thẻ chỉ số:** nhãn nhỏ 12/500 màu Muted ở trên, số JetBrains Mono 28px ở dưới, một dòng ngữ cảnh nhỏ cuối thẻ (vd. "lần cuối 03/11 14:20"). Không bày số giả; chưa có dữ liệu thì ghi "Chưa có dữ liệu".
* **Thanh dung lượng:** cao 8px, bo tròn hai đầu, rãnh Raised Surface, phần đã dùng màu Pine Teal; chuyển Amber khi trống < 15%, Signal Red khi < 10%.
* **Bảng (DataGrid):**
  - Không kẻ dọc, chỉ kẻ ngang 1px Border. Hàng cao 36px.
  - Tiêu đề cột và màu hàng hover/chọn giữ theo DataGrid Fluent để tự đổi sáng/tối; bảng nằm trong một thẻ, nền trong suốt.
  - Cột số căn phải bằng JetBrains Mono.
* **Ô nhập, hộp chọn, ô tick, bảng:** dùng điều khiển Fluent chuẩn của WPF (.NET 10 ThemeMode) nên tự đổi sáng/tối; viền focus và ô tick theo màu nhấn của Windows. Nhãn nằm trên ô hoặc bên trái dòng thiết lập; không dùng nhãn nổi (floating label).
* **Huy hiệu (badge):** viên thuốc cao 22px, nền nhạt của màu trạng thái, chữ 12/500 kèm biểu tượng. Ví dụ "Nên xem lại", "Đã xem", "Hệ thống".
* **Trạng thái đang tải:** thanh tiến trình mảnh 2px chạy ngang ở chân trang, ngay trên chữ trạng thái. Không dùng vòng xoay tròn ở giữa màn.
* **Trạng thái trống:** biểu tượng lớn màu Muted, một câu giải thích và một nút hành động, ví dụ "Chưa quét. Bấm Quét để xem có thể dọn bao nhiêu."

## 5. Layout Principles

- **Khung cửa sổ:** sidebar 232px cố định bên trái, vùng nội dung giới hạn rộng tối đa 1200px, lề 32px. Cửa sổ tối thiểu 1000×640.
- **Trang Tổng quan:**
  - Lưới 2 cột tỉ lệ 2:1. Cột trái là thẻ "Ổ đĩa" lớn: ổ C nổi bật, các ổ khác nhỏ hơn bên dưới. Cột phải xếp dọc 3 thẻ chỉ số.
  - Bên dưới là lưới 3:2: trái là "Dùng nhiều nhất hôm nay" dạng thanh ngang, phải là "Việc nên làm" (danh sách gợi ý thực tế).
  - Không có hàng 3 thẻ bằng nhau.
- **Các trang danh sách** (Dọn rác, Gợi ý, Di chuyển data, Thống kê): thanh đầu trang → hàng bộ lọc/tuỳ chọn (một hàng) → một thẻ chứa bảng chiếm phần còn lại → chân trang trạng thái.
- **Trang cài đặt** (Ưu tiên, Cài đặt): các nhóm thiết lập là thẻ xếp dọc rộng tối đa 760px. Mỗi dòng thiết lập có nhãn + mô tả bên trái và điều khiển bên phải.
- **Khoảng cách theo bậc 4px:** 4, 8, 12, 16, 20, 24, 32. Giữa các thẻ là 16px.
- Không chồng lớp phần tử, không định vị tuyệt đối.

## 6. Motion & Interaction

- Đổi trang: nội dung mờ dần từ 0 đến 1 trong 150ms (chỉ đổi opacity).
- Hover nút và hàng bảng: đổi nền tức thì, không animation.
- Thanh dung lượng và cột biểu đồ hiện ngay, không chạy hiệu ứng tăng dần.
- Không có vòng lặp chuyển động vô hạn, nhấp nháy hay hiệu ứng nổi lơ lửng. Ứng dụng nền phải đứng yên khi không có việc.
- Biểu đồ: di chuột lên cột nào thì hiện tooltip đúng giá trị cột đó; vùng nhận chuột cao hết biểu đồ.

## 7. Anti-Patterns (Banned)

- Không emoji ở bất kỳ đâu trong giao diện (kể cả dấu ✓ ●: dùng biểu tượng Segoe Fluent Icons).
- Không dùng font Inter, font serif, đen tuyền #000000.
- Không quầng sáng neon, không gradient trên chữ, không độ bão hoà quá cao, không màu tím/xanh neon kiểu AI.
- Không hàng 3 thẻ bằng nhau, không chồng lớp phần tử.
- Không bịa số liệu: mọi con số trên giao diện lấy từ máy thật. Chưa có thì ghi "Chưa có dữ liệu", không ghi "99%" hay số tròn giả.
- Không khối "SYSTEM METRICS"/"BY THE NUMBERS" trang trí, không kiểu nhãn `NHÃN // 2026`.
- Không sáo ngữ quảng cáo ("Nâng tầm", "Liền mạch", "Thế hệ mới", "Elevate", "Seamless").
- Không vòng xoay tải ở giữa màn, không chữ kiểu "Cuộn để khám phá".
- Không dùng màu làm kênh thông tin duy nhất: trạng thái luôn có biểu tượng + chữ.

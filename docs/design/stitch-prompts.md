# Prompt Google Stitch cho từng màn

Viết theo skill `enhance-prompt` của [google-labs-code/stitch-skills](https://github.com/google-labs-code/stitch-skills).
Cách dùng:
- **Trên web:** tạo project trên [stitch.withgoogle.com](https://stitch.withgoogle.com) (chế độ Web, khung Desktop 1280×800), dán khối "DESIGN SYSTEM" bên dưới vào phần design system của project, rồi dán prompt từng màn.
- **Qua agent:** có Stitch MCP + API key thì gọi skill `stitch-generate-design` với các prompt này.

Ảnh/HTML Stitch sinh ra chỉ dùng làm bản vẽ tham chiếu. Giao diện thật là WPF, dựng tay theo `DESIGN.md`.

---

## DESIGN SYSTEM (REQUIRED), dùng chung mọi màn

```
- Platform: Windows desktop utility app rendered as Web, Desktop-first, 1280x800 window
- Theme: Light (also produce a Dark variant), calm system-dashboard, balanced density, restrained motion
- Background: Canvas Mist (#FAFAFA); dark: Canvas Night (#09090B)
- Surface: Pure Surface (#FFFFFF) for cards, tables and sidebar; dark: Night Surface (#18181B)
- Raised Surface (#F4F4F5) for secondary buttons, row hover and progress tracks; dark: Raised Night (#27272A)
- Primary Accent: Pine Teal (#0F766E) for the single primary button per page, selected navigation item, focus ring, links; dark: Pine Teal Glow (#2A9D8F)
- Accent wash: Pine Wash (#E6F2F1) behind the selected navigation item
- Text Primary: Charcoal Ink (#18181B); dark: Frost Ink (#FAFAFA)
- Text Secondary: Muted Steel (#71717A); dark: Muted Ash (#A1A1AA)
- Borders: Whisper Border (#E4E4E7) 1px; dark: Night Border (#2E2E33)
- Status: Healthy Green (#15803D), Amber Caution (#B45309), Signal Red (#B91C1C), always paired with an icon and a text label
- Typography: Be Vietnam Pro for all UI text (Vietnamese diacritics), JetBrains Mono for large numbers and numeric table columns
- Cards: generously rounded (12px), 1px border, no shadow (hierarchy via border and surface tone), 20px padding
- Buttons: subtly rounded (8px), 34px tall, flat, no glow
- Icons: Segoe Fluent Icons line style
- Language: Vietnamese UI copy
- Banned: emojis, Inter font, pure black, neon glows, gradient text, three equal cards in a row, invented statistics, marketing clichés
```

---

## 1. Tổng quan (Overview)

```
A calm overview dashboard for a Windows disk-cleanup and resource utility, answering "how full is my drive and what should I do next" at a glance.

**Page Structure:**
1. **Left Sidebar (232px):** App mark "Manager Windows" at top; vertical navigation with line icons and labels: Tổng quan (selected), Dọn rác, Gợi ý dọn file, Di chuyển data app, Thống kê sử dụng, Ưu tiên & Excel, Cài đặt; at the bottom a status pill "Dịch vụ nền: Đang chạy" with a green dot.
2. **Page Header:** Title "Tổng quan", one-line muted subtitle "Ổ đĩa, dung lượng đã dọn và việc nên làm"; on the right a secondary "Làm mới" button and one primary "Dọn rác ngay" button.
3. **Asymmetric 2:1 Hero Grid:**
   - Left (2/3): large "Ổ đĩa" card. Drive C featured: label "C:", large monospace free-space number "[free] GB trống", muted "trên [total] GB", a thick 8px rounded capacity bar; below, smaller rows for other drives each with a thin bar.
   - Right (1/3): three stacked metric cards: "Đã dọn tổng cộng" with a monospace number, "Lần dọn gần nhất" with date/time and freed size, "Dùng máy hôm nay" with total active time.
4. **Lower 3:2 Grid:**
   - Left: "Dùng nhiều nhất hôm nay" card with a ranked list, each row = app name, horizontal teal bar proportional to time, monospace duration right-aligned.
   - Right: "Việc nên làm" card with 2–4 actionable rows (icon, sentence, small link button), e.g. "Ổ C còn dưới 15% — Quét dọn rác", "2 mục cần xem lại trước khi dọn — Xem".
5. **Footer Status Line:** thin muted status text.

Use placeholder labels like [free] instead of invented numbers.
```

## 2. Dọn rác (Cleanup)

```
A cleanup screen listing cleanup categories with dry-run estimates, per-category file review, a schedule row and cleanup history.

**Page Structure:**
1. **Sidebar:** same as Overview, "Dọn rác" selected.
2. **Page Header:** Title "Dọn rác", subtitle "Quét thử trước, chỉ xoá khi bạn bấm Dọn"; secondary button "Quét (chạy thử)", primary button "Dọn mục đã chọn".
3. **Categories Table Card:** columns: checkbox, Mục dọn (name + optional amber badge "Nên xem lại"), Phạm vi (badge "Người dùng"/"Hệ thống"), Số file (monospace, right), Dung lượng (monospace, right), action column with link "Xem file" and a green "Đã xem" badge with check icon when reviewed.
4. **Schedule Row (inline, single line):** "Dọn tự động" label, frequency dropdown, weekday dropdown, hour dropdown, idle-minutes number input, checkbox "Không chạy khi dùng pin", secondary "Lưu"; muted helper text below: automatic cleanup skips review-first items.
5. **History Card:** compact table: Thời gian, Kiểu, Đã giải phóng, Đã xoá, Bỏ qua.
6. **Footer Status Line** with thin progress bar while scanning.
```

## 3. Hộp thoại xem file (Review dialog)

```
A modal dialog (960x620) to review files of one cleanup category before deleting.

**Page Structure:**
1. **Header:** Title "Xem file: Bộ nhớ đệm Zalo"; amber callout card with warning icon: "Mục này có thể chứa dữ liệu ứng dụng vẫn cần dùng. Bỏ tick những file muốn giữ."
2. **Toolbar Row:** search input "Lọc theo tên/đường dẫn" on the left; on the right two secondary buttons "Tick tất cả đang hiện", "Bỏ tick tất cả đang hiện".
3. **File Table:** columns: checkbox "Xoá", relative path (truncated, full path tooltip), Dung lượng (monospace right), Ngày sửa, link "Mở thư mục"; sorted by size descending; zebra-free, horizontal dividers only.
4. **Footer Bar:** muted summary "Sẽ xoá [n] file ([size]) · giữ lại [m] file ([size])" on the left; on the right secondary "Huỷ" and primary "Xong".
```

## 4. Gợi ý dọn file (Suggestions)

```
A file-suggestion screen showing old, large, duplicate files and old installers. Suggestions only, never auto-delete.

**Page Structure:**
1. **Sidebar:** "Gợi ý dọn file" selected.
2. **Page Header:** Title, subtitle "Chỉ gợi ý — file được chuyển vào Thùng rác, có thể khôi phục"; primary "Quét".
3. **Scan Folders Card:** label above a multi-line input listing folders (%USERPROFILE%\Downloads …); amber inline note when last-access time is disabled.
4. **Filter Row:** segmented control: Tất cả, Lâu không dùng, File lớn, Bộ cài cũ, File trùng; selection summary "Đã chọn [n] file ([size])"; secondary "Chọn tất cả đang hiện"; primary "Chuyển vào Thùng rác".
5. **Results Table Card:** checkbox, Loại badge, Đường dẫn, Dung lượng, Dùng lần cuối, "Mở thư mục" link.
6. **Empty State** before scanning: large folder icon, sentence, "Quét" button.
```

## 5. Di chuyển data app (Relocation)

```
A screen to move app data folders (Zalo, Telegram, browsers) from drive C to another drive while keeping the original path via a junction.

**Page Structure:**
1. **Sidebar:** "Di chuyển data app" selected.
2. **Page Header:** Title, subtitle explaining the original path stays the same and a backup is kept; primary "Dò thư mục data".
3. **Target Row Card:** label "Thư mục đích", path input, secondary "Chọn…".
4. **Detected Folders Table Card:** App name with small app icon placeholder, Đường dẫn, Dung lượng (monospace), Trạng thái badge ("Đã chuyển → D:\…" green, "Làm theo hướng dẫn" muted), action link "Chuyển". Selecting a row reveals an info callout below with app-specific notes.
5. **Moves History Card:** Chỗ cũ, Chỗ mới, Dung lượng, Tiến trình badge, Kiểm tra badge (green OK / red with error icon), actions "Làm tiếp", "Hoàn tác", "Xoá backup".
```

## 6. Thống kê sử dụng (Usage)

```
An app-usage analytics screen with an hourly bar chart and a ranked table.

**Page Structure:**
1. **Sidebar:** "Thống kê sử dụng" selected.
2. **Page Header:** Title; right side a period segmented control (Hôm nay, 7 ngày, 30 ngày) and secondary "Làm mới".
3. **Hourly Chart Card:** title "Thời gian dùng máy theo giờ trong ngày", total on the right; 24 thin teal bars with 2px gaps and 4px rounded tops anchored to a hairline baseline; hour labels every 3 hours; hover tooltip per bar; no legend (single series).
4. **Apps Table Card:** Ứng dụng, Thời gian dùng (monospace), Số lần mở, Số phiên, Đường dẫn (muted), link "Ghim ưu tiên".
5. **Muted note** when launch counts are unavailable.
```

## 7. Ưu tiên & Excel (Priority & Excel)

```
A settings-style screen for CPU priority management and heavy Excel file analysis.

**Page Structure:**
1. **Sidebar:** "Ưu tiên & Excel" selected.
2. **Page Header:** Title, subtitle; primary "Lưu".
3. **Priority Settings Card:** setting rows (label + description left, control right): toggle "Bật điều phối ưu tiên", number "Số app dùng nhiều được ưu tiên", toggle "Cho phép mức High"; two side-by-side multi-line inputs "App ưu tiên" and "App nền cần hạ ưu tiên".
4. **Managed Processes Card:** table App, PID (monospace), Mức ban đầu, Mức hiện tại.
5. **Heavy Excel Card:** toggle, threshold input (MB), live status line with icon; secondary "Phân tích file Excel…".
6. **Excel Report Card:** file name, muted summary line (size · sheets · cells · formulas · styles), list of suggestions each with severity badge (Quan trọng red, Nên làm amber, Mẹo muted) and wrapped text.
```

## 8. Cài đặt (Settings)

```
A simple settings screen with grouped setting rows.

**Page Structure:**
1. **Sidebar:** "Cài đặt" selected.
2. **Page Header:** Title; primary "Lưu".
3. **Appearance Card:** rows "Ngôn ngữ" (dropdown), "Giao diện" (segmented: Theo Windows, Sáng, Tối).
4. **Behaviour Card:** toggles "Tự chạy khi đăng nhập Windows", "Theo dõi thời gian sử dụng"; number inputs "Giữ thống kê (ngày)", "Nhắc xoá backup sau (ngày)".
5. **Data Card:** privacy sentence, data folder path in monospace, secondary "Mở thư mục dữ liệu"; version line "Manager Windows [version]".
```

---
Tip từ skill `enhance-prompt`: để các màn đồng nhất, luôn dán khối DESIGN SYSTEM ở trên vào trước, rồi mới sinh từng màn.

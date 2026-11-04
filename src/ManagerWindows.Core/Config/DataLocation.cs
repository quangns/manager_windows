using System.Text.Json;

namespace ManagerWindows.Core.Config;

/// <summary>
/// Nơi lưu dữ liệu của app (cấu hình, thống kê, log) đổi được. Thư mục mặc định giữ một file trỏ
/// <c>data-location.json</c> ghi thư mục mới; lần chạy sau app đọc file này để biết dữ liệu ở đâu.
/// </summary>
public static class DataLocation
{
    public const string PointerFile = "data-location.json";

    /// <summary>File/thư mục dữ liệu được chuyển khi đổi nơi lưu (data.db chép riêng bằng SQLite backup).</summary>
    public static readonly string[] Files = ["config.json", "hibernated.json"];
    public static readonly string[] DbFiles = ["data.db", "data.db-wal", "data.db-shm"];
    public const string LogsDir = "logs";

    private sealed class Pointer
    {
        public string? DataDir { get; set; }
        /// <summary>Thư mục cũ cần dọn ở lần chạy sau (khi app không còn mở file trong đó).</summary>
        public string? CleanupDir { get; set; }
    }

    private static string PointerPath(string defaultDir) => Path.Combine(defaultDir, PointerFile);

    private static Pointer? Read(string defaultDir)
    {
        try
        {
            var p = PointerPath(defaultDir);
            return File.Exists(p) ? JsonSerializer.Deserialize<Pointer>(File.ReadAllText(p)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Thư mục dữ liệu đang dùng. Thư mục được trỏ tới không còn (ổ rút ra, bị xoá) thì quay về mặc định
    /// thay vì tạo dữ liệu mới ở chỗ lạ. Kèm thư mục cũ cần dọn (nếu có).
    /// </summary>
    public static (string Dir, string? CleanupDir) Resolve(string defaultDir)
    {
        var p = Read(defaultDir);
        if (p?.DataDir is not { Length: > 0 } dir || SamePath(dir, defaultDir)) return (defaultDir, p?.CleanupDir);
        return Directory.Exists(dir) && IsWritable(dir) ? (dir, p.CleanupDir) : (defaultDir, null);
    }

    /// <summary>Ghi nơi lưu mới (null hoặc trùng mặc định = dùng mặc định).</summary>
    public static void Set(string defaultDir, string? dataDir, string? cleanupDir)
    {
        Directory.CreateDirectory(defaultDir);
        var useDefault = dataDir == null || SamePath(dataDir, defaultDir);
        var p = new Pointer { DataDir = useDefault ? null : Path.GetFullPath(dataDir!), CleanupDir = cleanupDir };
        if (p.DataDir == null && p.CleanupDir == null)
        {
            File.Delete(PointerPath(defaultDir));
            return;
        }
        File.WriteAllText(PointerPath(defaultDir), JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Đã dọn thư mục cũ: bỏ ghi chú dọn trong file trỏ.</summary>
    public static void ClearCleanup(string defaultDir)
    {
        var p = Read(defaultDir);
        if (p?.CleanupDir == null) return;
        Set(defaultDir, p.DataDir, null);
    }

    /// <summary>Kiểm tra thư mục đích; trả khoá chuỗi lỗi hoặc null nếu dùng được.</summary>
    public static string? Validate(string target, string currentDir)
    {
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathRooted(target)) return "Data_Invalid";
        var t = Path.GetFullPath(target);
        if (SamePath(t, currentDir)) return "Data_Same";
        if (IsInside(t, currentDir) || IsInside(currentDir, t)) return "Data_Nested";
        try { Directory.CreateDirectory(t); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return "Data_NotWritable"; }
        return IsWritable(t) ? null : "Data_NotWritable";
    }

    /// <summary>Thư mục đích đã có dữ liệu của app (config.json) chưa.</summary>
    public static bool HasData(string dir) => File.Exists(Path.Combine(dir, "config.json"));

    /// <summary>Chép file dữ liệu (trừ cơ sở dữ liệu) và thư mục log sang thư mục mới, ghi đè.</summary>
    public static void CopyFiles(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Files)
        {
            var src = Path.Combine(from, f);
            if (File.Exists(src)) File.Copy(src, Path.Combine(to, f), overwrite: true);
        }
        var logs = Path.Combine(from, LogsDir);
        if (!Directory.Exists(logs)) return;
        var dest = Path.Combine(to, LogsDir);
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(logs))
            try { File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true); }
            catch (IOException) { } // file log đang ghi dở: bỏ qua, không quan trọng
    }

    /// <summary>Xoá dữ liệu của app ở thư mục cũ (chỉ các file của app, không đụng file khác và file trỏ).</summary>
    public static void Cleanup(string dir)
    {
        foreach (var f in Files.Concat(DbFiles))
            try { File.Delete(Path.Combine(dir, f)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try
        {
            var logs = Path.Combine(dir, LogsDir);
            if (Directory.Exists(logs)) Directory.Delete(logs, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        // Thư mục rỗng (không phải thư mục mặc định còn giữ file trỏ) thì xoá luôn.
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));

    public static bool SamePath(string a, string b) =>
        string.Equals(Norm(a), Norm(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsInside(string child, string parent) =>
        Norm(child).StartsWith(Norm(parent) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

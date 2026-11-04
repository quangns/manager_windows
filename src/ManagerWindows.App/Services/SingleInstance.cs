using System.Text.Json;

namespace ManagerWindows.App.Services;

/// <summary>Thông tin phiên app đang chạy, ghi ở chỗ chung cho cả bản cài đặt và bản portable.</summary>
public sealed record InstanceInfo(string Path, string Version, bool Portable, int Pid)
{
    private static string InfoFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ManagerWindows", "instance.json");

    public static InstanceInfo Current() =>
        new(Environment.ProcessPath ?? "", Edition.Version, Edition.IsPortable, Environment.ProcessId);

    public static InstanceInfo? Read()
    {
        try { return JsonSerializer.Deserialize<InstanceInfo>(File.ReadAllText(InfoFile)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public void Write()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(InfoFile)!);
            File.WriteAllText(InfoFile, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("instance", ex); }
    }

    /// <summary>Cùng một bản (cùng file exe, cùng phiên bản) thì chỉ cần hiện cửa sổ của phiên đang chạy.</summary>
    public bool SameAs(InstanceInfo other) =>
        string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase) && Version == other.Version;

    public string Describe() =>
        $"{Version} · {Loc.Instance[Portable ? "Edition_Portable" : "Edition_Installed"]}\n{Path}";
}

/// <summary>
/// Bản đang chạy: phiên bản và loại. Bản portable build với cờ PORTABLE (một file exe);
/// vẫn nhận file portable.txt cạnh exe của các bản portable cũ (0.1.x–0.3.0).
/// </summary>
public static class Edition
{
#if PORTABLE
    public static bool IsPortable => true;
#else
    public static bool IsPortable { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));
#endif

    public static string Version { get; } = typeof(Edition).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string Label => $"v{Version} · {Loc.Instance[IsPortable ? "Edition_Portable" : "Edition_Installed"]}";
}

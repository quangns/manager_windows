namespace ManagerWindows.Service;

public static class ServicePaths
{
    public const string ServiceName = "ManagerWindowsService";

    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ManagerWindows");

    /// <summary>Luật dọn đọc từ thư mục cài đặt (Program Files, user thường không sửa được).</summary>
    public static string RulesFile => Path.Combine(AppContext.BaseDirectory, "rules", "cleanup-rules.json");

    /// <summary>Lịch dọn rác hệ thống, admin có thể sửa.</summary>
    public static string ConfigFile => Path.Combine(DataDir, "service.json");
}

using System.Text.Json;
using System.Text.Json.Serialization;
using ManagerWindows.Core.Priority;

namespace ManagerWindows.Ipc;

/// <summary>
/// Giao tiếp App (quyền user) ↔ Service (quyền SYSTEM) qua named pipe, mỗi dòng là một JSON.
/// Danh sách lệnh cố định, KHÔNG có lệnh nhận đường dẫn tuỳ ý để xoá → không leo thang quyền được qua service.
/// </summary>
public static class IpcCommands
{
    public const string Ping = "ping";
    /// <summary>Chạy thử luật dọn hệ thống: trả về dung lượng ước tính theo từng luật.</summary>
    public const string PreviewSystemCleanup = "preview-system-cleanup";
    /// <summary>Dọn theo danh sách id luật Scope=System (service tự đọc luật từ thư mục cài đặt).</summary>
    public const string RunSystemCleanup = "run-system-cleanup";
    /// <summary>Đổi ưu tiên process mà app không đủ quyền (process chạy admin).</summary>
    public const string SetPriority = "set-priority";
    /// <summary>Số lần mở app trong khoảng thời gian (service theo dõi bằng WMI).</summary>
    public const string GetLaunchCounts = "get-launch-counts";
    /// <summary>Giải phóng RAM của mọi tiến trình trong phiên người gọi + xả bộ nhớ standby (cần quyền SYSTEM).</summary>
    public const string TrimMemory = "trim-memory";
    /// <summary>End task tiến trình mà app không đủ quyền (chạy quyền admin). Chỉ trong phiên người gọi.</summary>
    public const string KillProcess = "kill-process";
    /// <summary>
    /// Bật/tắt ngủ đông tab (Memory Saver) cho Chrome / Cốc Cốc / Brave bằng chính sách HKLM.
    /// Chỉ ghi 3 mục cố định dưới khoá chính sách cố định của từng trình duyệt; không đè chính sách quản trị viên đã đặt.
    /// </summary>
    public const string SetBrowserTabSleep = "set-browser-tab-sleep";
}

public static class IpcConstants
{
    public const string PipeName = "ManagerWindows.Service.v1";
    public const int MaxMessageChars = 64 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}

public sealed class IpcRequest
{
    public string Command { get; set; } = "";
    public JsonElement? Payload { get; set; }
}

public sealed class IpcResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public JsonElement? Data { get; set; }

    public static IpcResponse Success(object? data = null) => new()
    {
        Ok = true,
        Data = data == null ? null : JsonSerializer.SerializeToElement(data, IpcConstants.Json),
    };

    public static IpcResponse Fail(string error) => new() { Ok = false, Error = error };
}

public sealed record RunCleanupRequest(List<string> RuleIds);
public sealed record RuleEstimate(string RuleId, long Bytes, int Files);
public sealed record CleanupResultDto(long FreedBytes, int Deleted, int Failed);
public sealed record SetPriorityRequest(int Pid, PriorityLevel Level, bool EcoQoS);
public sealed record LaunchCountsRequest(DateTime FromUtc, DateTime ToUtc);
public sealed record TrimMemoryResult(int Trimmed, int Failed, bool StandbyPurged);
public sealed record KillProcessRequest(int Pid);
public sealed record SetTabSleepRequest(bool Enabled, int Savings, List<string> Browsers, List<string> Exceptions);
/// <summary>Kết quả từng trình duyệt: Applied = đã bật; Conflicts = mục quản trị viên đã đặt nên bỏ qua.</summary>
public sealed record TabSleepBrowserResult(string Browser, bool Applied, List<string> Conflicts);

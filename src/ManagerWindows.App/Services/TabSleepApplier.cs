using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using ManagerWindows.Core.Monitoring;
using ManagerWindows.Ipc;
using ManagerWindows.Platform;

namespace ManagerWindows.App.Services;

/// <summary>Kết quả ghi chính sách ngủ đông tab. <see cref="Cancelled"/> = người dùng bấm "Không" ở hộp thoại UAC.</summary>
public sealed record TabSleepApplyResult(string? Error, List<TabSleepBrowserResult> Results, bool Cancelled = false);

/// <summary>
/// Ghi chính sách ngủ đông tab (HKLM, cần quyền cao), thử lần lượt:
/// 1) dịch vụ nền (bản cài đặt), 2) chính app nếu đang chạy quyền Admin,
/// 3) mở lại app bằng quyền Admin ở chế độ <c>--tab-sleep</c> (Windows hỏi UAC một lần; dùng cho bản portable).
/// Cả ba cùng gọi <see cref="BrowserPolicies.ApplyRequest"/> nên dùng chung danh sách mục do app đặt.
/// </summary>
public static class TabSleepApplier
{
    public const string HelperArg = "--tab-sleep";

    private static readonly JsonSerializerOptions Json = IpcConstants.Json;

    public static async Task<TabSleepApplyResult> ApplyAsync(IpcClient service, TabSleepRequest request)
    {
        if ((await service.SendAsync(IpcCommands.Ping)).Ok)
        {
            var r = await service.SendAsync(IpcCommands.SetBrowserTabSleep, request);
            return r.Ok
                ? new(null, r.Data is { } d ? d.Deserialize<List<TabSleepBrowserResult>>(Json) ?? [] : [])
                : new(r.Error, []);
        }
        if (IsElevated()) return await Task.Run(() => FromTuple(BrowserPolicies.ApplyRequest(request)));
        return await RunElevatedAsync(request);
    }

    private static bool IsElevated() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static TabSleepApplyResult FromTuple((string? Error, List<TabSleepBrowserResult> Results) t) => new(t.Error, t.Results);

    /// <summary>Mở lại chính app bằng quyền Admin để ghi chính sách; trao đổi qua 2 file tạm trong %TEMP% của người dùng.</summary>
    private static async Task<TabSleepApplyResult> RunElevatedAsync(TabSleepRequest request)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ManagerWindows");
        Directory.CreateDirectory(dir);
        var id = Guid.NewGuid().ToString("N");
        var reqFile = Path.Combine(dir, $"tab-sleep-{id}.json");
        var resFile = Path.Combine(dir, $"tab-sleep-{id}.result.json");
        try
        {
            await File.WriteAllTextAsync(reqFile, JsonSerializer.Serialize(request, Json));
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            psi.ArgumentList.Add(HelperArg);
            psi.ArgumentList.Add(reqFile);
            psi.ArgumentList.Add(resFile);
            using var proc = Process.Start(psi);
            if (proc == null) return new("Không mở được tiến trình quyền Admin", []);
            await proc.WaitForExitAsync();
            if (!File.Exists(resFile)) return new($"Tiến trình quyền Admin thoát với mã {proc.ExitCode}", []);
            var res = JsonSerializer.Deserialize<HelperResult>(await File.ReadAllTextAsync(resFile), Json);
            return res == null ? new("Kết quả rỗng", []) : new(res.Error, res.Results ?? []);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // bấm "Không" ở hộp thoại UAC
        {
            return new(null, [], Cancelled: true);
        }
        finally
        {
            TryDelete(reqFile);
            TryDelete(resFile);
        }
    }

    /// <summary>
    /// Chế độ <c>--tab-sleep &lt;file yêu cầu&gt; &lt;file kết quả&gt;</c> (đang chạy quyền Admin): ghi chính sách rồi thoát, không hiện cửa sổ.
    /// Yêu cầu được kiểm tra lại trong <see cref="BrowserPolicies.ApplyRequest"/> như với dịch vụ nền.
    /// </summary>
    public static int RunHelper(IReadOnlyList<string> args)
    {
        var i = args.ToList().IndexOf(HelperArg);
        if (i < 0 || i + 2 >= args.Count) return 2;
        var (reqFile, resFile) = (args[i + 1], args[i + 2]);
        HelperResult result;
        try
        {
            var req = JsonSerializer.Deserialize<TabSleepRequest>(File.ReadAllText(reqFile), Json);
            if (req == null) result = new("Yêu cầu rỗng", null);
            else
            {
                var (error, results) = BrowserPolicies.ApplyRequest(req);
                result = new(error, results);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            result = new(ex.Message, null);
        }
        try { File.WriteAllText(resFile, JsonSerializer.Serialize(result, Json)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 1; }
        return result.Error == null ? 0 : 1;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed record HelperResult(string? Error, List<TabSleepBrowserResult>? Results);
}

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using ManagerWindows.Core.Monitoring;
using ManagerWindows.Core.Priority;
using ManagerWindows.Ipc;
using ManagerWindows.Platform;
using ManagerWindows.Storage;

namespace ManagerWindows.Service;

/// <summary>
/// Named pipe nhận lệnh từ App. Chỉ người dùng đăng nhập tương tác (INTERACTIVE), SYSTEM và Administrators
/// được kết nối. Mọi lệnh đều có kiểm tra tham số; không có lệnh nào nhận đường dẫn file.
/// </summary>
public sealed partial class PipeServerWorker(SystemCleanupService cleanup, UsageRepository usage, ILogger<PipeServerWorker> log) : BackgroundService
{
    private const int MaxInstances = 4;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    /// <summary>Phiên Windows (session) của tiến trình đang gọi, -1 nếu không xác định.</summary>
    private static int ClientSession(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) return -1;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.SessionId;
        }
        catch (ArgumentException) { return -1; }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(IpcConstants.PipeName, PipeDirection.InOut, MaxInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, CreateSecurity());
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException ex)
            {
                // Hết instance (đang có đủ kết nối): đợi một chút rồi thử lại.
                log.LogDebug(ex, "Pipe bận");
                await Task.Delay(200, ct);
                continue;
            }

            _ = Task.Run(async () =>
            {
                await using (pipe)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromMinutes(30)); // dọn rác hệ thống có thể lâu
                    var session = ClientSession(pipe);
                    try { await IpcServerProtocol.HandleAsync(pipe, req => HandleAsync(req, session, timeout.Token), timeout.Token); }
                    catch (Exception ex) { log.LogDebug(ex, "Kết nối pipe lỗi"); }
                }
            }, ct);
        }
    }

    private static readonly Lock BrowserPolicyLock = new();

    /// <summary>
    /// Ghi chính sách Memory Saver. Mọi người dùng đăng nhập đều gọi được nên chỉ nhận đúng 3 trình duyệt đã biết,
    /// mức tiết kiệm 0–2 và mẫu URL đã kiểm tra lại; trình duyệt không có trong danh sách bật thì gỡ phần app đã đặt.
    /// </summary>
    private IpcResponse SetBrowserTabSleep(SetTabSleepRequest p)
    {
        if (p.Savings is < 0 or > 2) return IpcResponse.Fail("Mức tiết kiệm không hợp lệ");
        var browsers = new HashSet<TabSleepBrowser>();
        foreach (var b in p.Browsers ?? [])
        {
            if (!Enum.TryParse<TabSleepBrowser>(b, ignoreCase: false, out var v) || !Enum.IsDefined(v)) return IpcResponse.Fail("Trình duyệt không hỗ trợ");
            browsers.Add(v);
        }
        var exceptions = p.Exceptions ?? [];
        if (exceptions.Count > BrowserPolicyPlanner.MaxExceptions || !exceptions.All(BrowserPolicyPlanner.IsValidException))
            return IpcResponse.Fail("Danh sách trang không hợp lệ");

        lock (BrowserPolicyLock)
        {
            var config = cleanup.LoadConfig();
            var results = new List<TabSleepBrowserResult>();
            foreach (var info in BrowserCatalog.All)
            {
                var name = info.Browser.ToString();
                var owned = new HashSet<string>(config.BrowserPolicyOwned.GetValueOrDefault(name) ?? [], StringComparer.Ordinal);
                var enable = p.Enabled && browsers.Contains(info.Browser);
                var plan = BrowserPolicyPlanner.Build(info.Browser, enable, (TabSleepSavings)p.Savings, exceptions, BrowserPolicies.Read(info.Browser), owned);
                try { BrowserPolicies.Apply(plan.Ops); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    log.LogWarning(ex, "Không ghi được chính sách {Browser}", name);
                    return IpcResponse.Fail($"Không ghi được chính sách {info.DisplayName}: {ex.Message}");
                }
                if (plan.Owned.Count > 0) config.BrowserPolicyOwned[name] = plan.Owned.Order().ToList();
                else config.BrowserPolicyOwned.Remove(name);
                cleanup.SaveConfig(config); // lưu ngay sau từng trình duyệt: lỗi giữa chừng vẫn biết mục nào do app đặt
                results.Add(new TabSleepBrowserResult(name, enable && plan.Owned.Contains(BrowserPolicyPlanner.EnabledValue), plan.Conflicts));
            }
            log.LogInformation("Ngủ đông tab: {State}, mức {Savings}, trình duyệt {Browsers}, {Count} trang ngoại lệ",
                p.Enabled ? "bật" : "tắt", p.Savings, string.Join(",", browsers), exceptions.Count);
            return IpcResponse.Success(results);
        }
    }

    private static PipeSecurity CreateSecurity()
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return sec;
    }

    private async Task<IpcResponse> HandleAsync(IpcRequest req, int clientSession, CancellationToken ct)
    {
        switch (req.Command)
        {
            case IpcCommands.Ping:
                return IpcResponse.Success(new { version = typeof(PipeServerWorker).Assembly.GetName().Version?.ToString() });

            case IpcCommands.PreviewSystemCleanup:
                return IpcResponse.Success(await Task.Run(cleanup.Preview, ct));

            case IpcCommands.RunSystemCleanup:
            {
                var p = IpcServerProtocol.Payload<RunCleanupRequest>(req);
                return IpcResponse.Success(await cleanup.RunAsync(p.RuleIds, "manual", ct));
            }

            case IpcCommands.SetPriority:
            {
                var p = IpcServerProtocol.Payload<SetPriorityRequest>(req);
                if (p.Level is < PriorityLevel.Idle or > PriorityLevel.High)
                    return IpcResponse.Fail("Mức ưu tiên không hợp lệ");
                string name;
                int session;
                try
                {
                    using var proc = Process.GetProcessById(p.Pid);
                    name = proc.ProcessName;
                    session = proc.SessionId;
                }
                catch (ArgumentException) { return IpcResponse.Fail("Process không tồn tại"); }
                if (ProtectedProcesses.IsProtected(name)) return IpcResponse.Fail("Process được bảo vệ");
                // Chỉ cho đổi ưu tiên process cùng phiên đăng nhập với người gọi (không đụng phiên người khác/session 0).
                if (clientSession < 0 || session != clientSession) return IpcResponse.Fail("Process không thuộc phiên của bạn");
                return ProcessPriorityApi.Set(p.Pid, p.Level, p.EcoQoS)
                    ? IpcResponse.Success()
                    : IpcResponse.Fail("Không đặt được ưu tiên");
            }

            case IpcCommands.TrimMemory:
            {
                if (clientSession < 0) return IpcResponse.Fail("Không xác định được phiên của bạn");
                var (ok, failed) = await Task.Run(() => MemoryApi.TrimAll(p =>
                    p.SessionId == clientSession && !ProtectedProcesses.IsProtected(p.ProcessName)), ct);
                var purged = MemoryApi.PurgeStandbyList();
                log.LogInformation("Giải phóng RAM phiên {Session}: trim {Ok}, lỗi {Failed}, xả standby {Purged}", clientSession, ok, failed, purged);
                return IpcResponse.Success(new TrimMemoryResult(ok, failed, purged));
            }

            case IpcCommands.KillProcess:
            {
                var p = IpcServerProtocol.Payload<KillProcessRequest>(req);
                try
                {
                    using var proc = Process.GetProcessById(p.Pid);
                    if (ProtectedProcesses.IsProtected(proc.ProcessName)) return IpcResponse.Fail("Process được bảo vệ");
                    // Chỉ đóng tiến trình cùng phiên đăng nhập với người gọi (không đụng dịch vụ/phiên người khác).
                    if (clientSession < 0 || proc.SessionId != clientSession) return IpcResponse.Fail("Process không thuộc phiên của bạn");
                    log.LogInformation("End task {Name} ({Pid}) theo yêu cầu phiên {Session}", proc.ProcessName, p.Pid, clientSession);
                    proc.Kill();
                    return IpcResponse.Success();
                }
                catch (ArgumentException) { return IpcResponse.Success(); } // đã thoát
            }

            case IpcCommands.SetBrowserTabSleep:
            {
                var p = IpcServerProtocol.Payload<SetTabSleepRequest>(req);
                return SetBrowserTabSleep(p);
            }

            case IpcCommands.GetLaunchCounts:
            {
                var p = IpcServerProtocol.Payload<LaunchCountsRequest>(req);
                return IpcResponse.Success(usage.LaunchCounts(p.FromUtc, p.ToUtc));
            }

            default:
                return IpcResponse.Fail("Lệnh không hỗ trợ");
        }
    }
}

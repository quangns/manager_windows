using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
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

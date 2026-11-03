using System.IO.Pipes;
using System.Text.Json;

namespace ManagerWindows.Ipc;

/// <summary>Client gọi service. Mỗi lệnh mở một kết nối riêng cho đơn giản (tần suất gọi thấp).</summary>
public sealed class IpcClient(string pipeName = IpcConstants.PipeName, int timeoutMs = 2000)
{
    public async Task<IpcResponse> SendAsync(string command, object? payload = null, CancellationToken ct = default)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeoutMs, ct);
            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };

            var req = new IpcRequest
            {
                Command = command,
                Payload = payload == null ? null : JsonSerializer.SerializeToElement(payload, IpcConstants.Json),
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(req, IpcConstants.Json));
            var line = await reader.ReadLineAsync(ct);
            return line == null
                ? IpcResponse.Fail("Service đóng kết nối")
                : JsonSerializer.Deserialize<IpcResponse>(line, IpcConstants.Json) ?? IpcResponse.Fail("Phản hồi rỗng");
        }
        catch (TimeoutException)
        {
            return IpcResponse.Fail("Không kết nối được service ManagerWindows");
        }
        catch (IOException ex)
        {
            return IpcResponse.Fail(ex.Message);
        }
    }

    public async Task<T?> SendAsync<T>(string command, object? payload = null, CancellationToken ct = default)
    {
        var r = await SendAsync(command, payload, ct);
        if (!r.Ok) throw new InvalidOperationException(r.Error);
        return r.Data is { } d ? d.Deserialize<T>(IpcConstants.Json) : default;
    }
}

/// <summary>Xử lý một kết nối phía server: đọc 1 dòng, gọi handler, ghi 1 dòng. Dùng chung cho service và test.</summary>
public static class IpcServerProtocol
{
    public static async Task HandleAsync(Stream stream, Func<IpcRequest, Task<IpcResponse>> handler, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        await using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };
        IpcResponse resp;
        try
        {
            var line = await ReadLimitedLineAsync(reader, IpcConstants.MaxMessageChars, ct);
            var req = line == null ? null : JsonSerializer.Deserialize<IpcRequest>(line, IpcConstants.Json);
            resp = req == null ? IpcResponse.Fail("Yêu cầu rỗng") : await handler(req);
        }
        catch (JsonException) { resp = IpcResponse.Fail("JSON không hợp lệ"); }
        catch (InvalidDataException ex) { resp = IpcResponse.Fail(ex.Message); }
        catch (Exception ex) when (ex is not OperationCanceledException) { resp = IpcResponse.Fail(ex.Message); }
        await writer.WriteLineAsync(JsonSerializer.Serialize(resp, IpcConstants.Json));
    }

    private static async Task<string?> ReadLimitedLineAsync(StreamReader reader, int max, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        var buf = new char[1];
        while (await reader.ReadAsync(buf.AsMemory(), ct) > 0)
        {
            if (buf[0] == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append(buf[0]);
            if (sb.Length > max) throw new InvalidDataException("Yêu cầu quá dài");
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    public static T Payload<T>(IpcRequest req) =>
        req.Payload is { } p
            ? p.Deserialize<T>(IpcConstants.Json) ?? throw new InvalidDataException("Thiếu dữ liệu")
            : throw new InvalidDataException("Thiếu dữ liệu");
}

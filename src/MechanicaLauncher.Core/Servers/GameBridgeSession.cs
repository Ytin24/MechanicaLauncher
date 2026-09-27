using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace MechanicaLauncher.Core.Servers;

public sealed record BridgeRequest(string Type, JsonElement Payload, Guid RequestId);
public sealed record BridgeReply(string Type, JsonElement Payload);

public sealed class GameBridgeSession : IDisposable, IAsyncDisposable
{
    private const int MaximumFrame = 16 * 1024;
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(15);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource processReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim[] writers = [new(1, 1), new(1, 1)];
    private readonly Dictionary<Guid, PendingRequest> requests = [];
    private readonly Func<BridgeRequest, CancellationToken, Task<BridgeReply>> handle;
    private readonly NamedPipeServerStream[] pipes;
    private readonly byte[] secret = RandomNumberGenerator.GetBytes(32);
    private readonly string instanceId;
    private readonly string pipeName;
    private readonly Task listener;
    private Process? process;
    private long processStart;
    private bool stopped;
    private Task? shutdown;

    public string SessionId { get; } = Guid.NewGuid().ToString("D");
    public IReadOnlyDictionary<string, string> Environment { get; }

    public GameBridgeSession(string instanceId, Func<BridgeRequest, CancellationToken, Task<BridgeReply>> handle)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Game bridge requires Windows named pipes.");
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(handle);
        if (instanceId.Length > 128) throw new ArgumentException("Instance ID is too long.", nameof(instanceId));
        this.instanceId = instanceId;
        this.handle = handle;
        pipeName = "mechanica-game-" + Guid.NewGuid().ToString("N");
        Environment = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>
        {
            ["MECHANICA_BRIDGE_PIPE"] = pipeName,
            ["MECHANICA_BRIDGE_SESSION"] = SessionId,
            ["MECHANICA_BRIDGE_TOKEN"] = Convert.ToHexString(secret),
            ["MECHANICA_BRIDGE_INSTANCE"] = instanceId
        });
        var first = CreatePipe(pipeName, true);
        try { pipes = [first, CreatePipe(pipeName, false)]; }
        catch { first.Dispose(); throw; }
        listener = Task.WhenAll(ListenAsync(0), ListenAsync(1));
    }

    public void BindProcess(Process value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.HasExited) throw new InvalidOperationException("Game process has already exited.");
        var expectedStart = value.StartTime.ToUniversalTime().Ticks;
        var owned = Process.GetProcessById(value.Id);
        try
        {
            if (owned.HasExited || owned.StartTime.ToUniversalTime().Ticks != expectedStart)
                throw new InvalidOperationException("Game process identity has changed.");
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(stopped, this);
                if (process != null) throw new InvalidOperationException("Game process is already bound.");
                process = owned;
                processStart = expectedStart;
                owned.Exited += ProcessExited;
                owned.EnableRaisingEvents = true;
                processReady.TrySetResult();
            }
        }
        catch
        {
            lock (gate)
                if (ReferenceEquals(process, owned)) process = null;
            owned.Exited -= ProcessExited;
            owned.Dispose();
            throw;
        }
        try { if (owned.HasExited) Stop(); }
        catch (InvalidOperationException) { Stop(); }
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreatePipe(string name, bool first)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("Windows user identity is unavailable.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), MaximumFrame, MaximumFrame, security);
    }

    [SupportedOSPlatform("windows")]
    private async Task ListenAsync(int index)
    {
        var pipe = pipes[index];
        while (!lifetime.IsCancellationRequested)
        {
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            try
            {
                await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                await processReady.Task.WaitAsync(lifetime.Token).ConfigureAwait(false);
                if (!IsBoundClient(pipe)) continue;
                using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
                helloTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var hello = await ReadAsync(pipe, helloTimeout.Token).ConfigureAwait(false);
                var error = ValidateHello(hello.RootElement);
                if (error != null)
                {
                    await WriteAsync(index, JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        type = "Result", payload = new { status = "error", code = error, retryable = false }
                    }), connection.Token).ConfigureAwait(false);
                    await AwaitRejectedPeerCloseAsync(pipe, connection.Token).ConfigureAwait(false);
                    continue;
                }
                await WriteAsync(index, JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = "BridgeReady", protocolMajor = 1, protocolMinor = 0, sessionId = SessionId, instanceId
                }), connection.Token).ConfigureAwait(false);

                while (!connection.IsCancellationRequested)
                {
                    using var document = await ReadAsync(pipe, connection.Token).ConfigureAwait(false);
                    if (!IsBoundClient(pipe)) break;
                    await DispatchAsync(index, document.RootElement, connection.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or
                                       JsonException or DecoderFallbackException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException) { }
            finally
            {
                connection.Cancel();
                pipe = RenewPipe(index, pipe);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private NamedPipeServerStream RenewPipe(int index, NamedPipeServerStream previous)
    {
        try
        {
            lock (gate)
            {
                if (stopped) return previous;
                // Keep the other handle open so the pipe name stays owned during replacement.
                previous.Dispose();
                return pipes[index] = CreatePipe(pipeName, false);
            }
        }
        catch { Stop(); throw; }
    }

    private static async Task AwaitRejectedPeerCloseAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        // Disconnect discards unread replies; let the rejected client read and close first.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var ignored = new byte[256];
            while (await pipe.ReadAsync(ignored, timeout.Token).ConfigureAwait(false) > 0) { }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    private string? ValidateHello(JsonElement hello)
    {
        if (hello.ValueKind != JsonValueKind.Object || Text(hello, "type") != "BridgeHello") return "invalid_request";
        if (!Number(hello, "bootstrapVersion", 1)) return "unsupported_protocol";
        if (Text(hello, "sessionId") != SessionId || Text(hello, "instanceId") != instanceId) return "unauthorized";
        var token = Text(hello, "token");
        if (token?.Length != 64) return "unauthorized";
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(token), secret)) return "unauthorized";
        }
        catch (FormatException) { return "unauthorized"; }
        if (!hello.TryGetProperty("protocols", out var protocols) || protocols.ValueKind != JsonValueKind.Array ||
            !protocols.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.Object && Number(p, "major", 1) && Number(p, "minor", 0)))
            return "unsupported_protocol";
        return null;
    }

    private async Task DispatchAsync(int index, JsonElement root, CancellationToken connection)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.TryParseExact(Text(root, "requestId"), "D", out var parsed) ? parsed : Guid.Empty;
        var expires = root.TryGetProperty("expiresUtc", out var date) && date.ValueKind == JsonValueKind.String &&
                      date.TryGetDateTimeOffset(out var expiry) ? expiry : DateTimeOffset.MinValue;
        var type = Text(root, "type") ?? "";
        var envelope = new Envelope(type, id, expires);
        string? error = null;
        if (Text(root, "sessionId") != SessionId || Text(root, "instanceId") != instanceId) error = "unauthorized";
        else if (!Number(root, "protocolMajor", 1) || !Number(root, "protocolMinor", 0)) error = "unsupported_protocol";
        else if (!IsUuid4(id) || type is not ("PrepareConnection" or "Apply" or "Cancel") ||
                 expires == DateTimeOffset.MinValue || expires.Offset != TimeSpan.Zero || expires > now + RequestLifetime ||
                 !root.TryGetProperty("payload", out var body) || body.ValueKind != JsonValueKind.Object) error = "invalid_request";
        else if (expires <= now) error = "request_expired";
        if (error != null)
        {
            await ReplyAsync(index, envelope, Failure(error), connection).ConfigureAwait(false);
            return;
        }

        PendingRequest? pending = null;
        BridgeReply? immediate = null;
        var fingerprint = Fingerprint(root);
        lock (gate)
        {
            if (stopped) return;
            foreach (var old in requests.Where(p => p.Value.Expires <= now && p.Value.Completion.Task.IsCompleted).Select(p => p.Key).ToArray())
                requests.Remove(old);
            if (requests.TryGetValue(id, out var previous))
                immediate = previous.Fingerprint != fingerprint ? Failure("invalid_request") :
                    previous.Completion.Task.IsCompletedSuccessfully ? previous.Completion.Task.Result :
                    new BridgeReply("Result", JsonSerializer.SerializeToElement(new { status = "running", phase = Phase(type) }));
            else if (requests.Count >= (type == "Cancel" ? 128 : 127) ||
                     requests.Values.Any(p => !p.Completion.Task.IsCompleted && (p.Type == "Cancel") == (type == "Cancel")))
                immediate = Failure("busy", true);
            else
            {
                pending = new PendingRequest(type, fingerprint, expires);
                requests.Add(id, pending);
            }
        }
        if (immediate != null) await ReplyAsync(index, envelope, immediate, connection).ConfigureAwait(false);
        else if (pending != null)
        {
            var request = new BridgeRequest(type, root.GetProperty("payload").Clone(), id);
            _ = ExecuteAsync(index, request, envelope, pending, connection);
        }
    }

    private async Task ExecuteAsync(int index, BridgeRequest request, Envelope envelope, PendingRequest pending, CancellationToken connection)
    {
        BridgeReply reply;
        try
        {
            reply = await Task.Run(() => handle(request, lifetime.Token), lifetime.Token).ConfigureAwait(false);
            if (reply == null || reply.Type is not ("Plan" or "Result") || reply.Payload.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Invalid bridge reply.");
            if (ResponseBytes(envelope, reply).Length > MaximumFrame) throw new InvalidDataException("Bridge reply exceeds the frame limit.");
        }
        catch (OperationCanceledException) { reply = Failure("cancelled"); }
        catch (Exception) { reply = Failure("internal_error"); }
        lock (gate)
        {
            pending.Completion.TrySetResult(reply);
            if (Text(reply.Payload, "code") == "busy") requests.Remove(request.RequestId);
        }
        try { await ReplyAsync(index, envelope, reply, connection).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
    }

    private Task ReplyAsync(int index, Envelope request, BridgeReply reply, CancellationToken ct) => WriteAsync(index, ResponseBytes(request, reply), ct);

    private byte[] ResponseBytes(Envelope request, BridgeReply reply) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        type = reply.Type, protocolMajor = 1, protocolMinor = 0, sessionId = SessionId, instanceId,
        requestId = request.Id, expiresUtc = request.Expires == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : request.Expires,
        payload = reply.Payload
    });

    private static BridgeReply Failure(string code, bool retryable = false) => new("Result",
        JsonSerializer.SerializeToElement(new { status = code == "cancelled" ? "cancelled" : "error", code, retryable }));

    private static async Task<JsonDocument> ReadAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var header = new byte[4];
        await pipe.ReadExactlyAsync(header.AsMemory(0, 1), ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await pipe.ReadExactlyAsync(header.AsMemory(1), timeout.Token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > MaximumFrame) throw new InvalidDataException("Invalid bridge frame length.");
        var data = new byte[length];
        await pipe.ReadExactlyAsync(data, timeout.Token).ConfigureAwait(false);
        var document = JsonDocument.Parse(Utf8.GetString(data), new JsonDocumentOptions { MaxDepth = 16 });
        try
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Bridge message must be an object.");
            _ = Fingerprint(document.RootElement);
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    private async Task WriteAsync(int index, byte[] data, CancellationToken ct)
    {
        var pipe = pipes[index];
        var writer = writers[index];
        if (data.Length > MaximumFrame) throw new InvalidDataException("Bridge reply exceeds the frame limit.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await writer.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)data.Length);
            await pipe.WriteAsync(header, timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync(data, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        finally { writer.Release(); }
    }

    private static string Fingerprint(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream)) Write(root, json);
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));

        static void Write(JsonElement item, Utf8JsonWriter json)
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                json.WriteStartObject();
                var properties = item.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
                string? previous = null;
                foreach (var property in properties)
                {
                    if (property.Name == previous) throw new InvalidDataException("Duplicate bridge property.");
                    previous = property.Name;
                    json.WritePropertyName(property.Name);
                    Write(property.Value, json);
                }
                json.WriteEndObject();
            }
            else if (item.ValueKind == JsonValueKind.Array)
            {
                json.WriteStartArray();
                foreach (var child in item.EnumerateArray()) Write(child, json);
                json.WriteEndArray();
            }
            else item.WriteTo(json);
        }
    }

    private bool IsBoundClient(NamedPipeServerStream pipe)
    {
        lock (gate)
        {
            if (stopped || process == null) return false;
            try
            {
                return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == processStart &&
                    GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) && pid == process.Id;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        }
    }

    private static bool Number(JsonElement root, string name, int value) =>
        root.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number) && number == value;
    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static bool IsUuid4(Guid id)
    {
        var text = id.ToString("D");
        return text[14] == '4' && text[19] is '8' or '9' or 'a' or 'b';
    }
    private static string Phase(string type) => type == "Apply" ? "download" : type == "Cancel" ? "rollback" : "plan";

    private void ProcessExited(object? sender, EventArgs args) => Stop();

    private void Stop()
    {
        lock (gate)
        {
            if (stopped) return;
            stopped = true;
        }
        try { lifetime.Cancel(); }
        catch (AggregateException) { }
        foreach (var pipe in pipes) pipe.Dispose();
    }

    public void Dispose()
    {
        Stop();
        lock (gate) shutdown ??= ShutdownAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await shutdown!.ConfigureAwait(false);
    }

    private async Task ShutdownAsync()
    {
        await listener.ConfigureAwait(false);
        Task[] pending;
        lock (gate) pending = requests.Values.Select(p => (Task)p.Completion.Task).ToArray();
        await Task.WhenAll(pending).ConfigureAwait(false);
        lock (gate)
        {
            if (process != null) { process.Exited -= ProcessExited; process.Dispose(); process = null; }
            requests.Clear();
        }
        CryptographicOperations.ZeroMemory(secret);
        lifetime.Dispose();
    }

    private sealed record Envelope(string Type, Guid Id, DateTimeOffset Expires);
    private sealed class PendingRequest(string type, string fingerprint, DateTimeOffset expires)
    {
        public string Type { get; } = type;
        public string Fingerprint { get; } = fingerprint;
        public DateTimeOffset Expires { get; } = expires;
        public TaskCompletionSource<BridgeReply> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}

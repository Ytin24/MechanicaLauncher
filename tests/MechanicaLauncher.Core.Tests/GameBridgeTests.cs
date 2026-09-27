using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MechanicaLauncher.Core.Servers;

internal static class GameBridgeTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        if (!OperatingSystem.IsWindows()) return;

        await check("Game bridge accepts a valid client after repeated disconnects before hello", async () =>
        {
            var calls = 0;
            await using var session = Create((_, _) => { calls++; return Task.FromResult(Reply("ready")); });
            for (int attempt = 0; attempt < 16; attempt++)
                await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
                {
                    using var probe = await ConnectAsync(session);
                    if (attempt % 2 != 0) await probe.WriteAsync(new byte[] { 1 });
                }));
            using var client = await AuthenticateAsync(session);
            using var second = await AuthenticateAsync(session);
            await SendAsync(client, Request(session));
            Require((await ReadAsync(client)).GetProperty("payload").GetProperty("status").GetString() == "ready");
            await SendAsync(second, Request(session));
            Require((await ReadAsync(second)).GetProperty("payload").GetProperty("status").GetString() == "ready");
            Require(calls == 2);
        });

        await check("Game bridge waits for the exact process before authenticating", async () =>
        {
            await using var session = new GameBridgeSession("survival", (_, _) => Task.FromResult(Reply("ready")));
            using var client = await ConnectAsync(session);
            await SendAsync(client, Hello(session));
            var ready = ReadAsync(client);
            await Task.Delay(40);
            Require(!ready.IsCompleted);
            BindSelf(session);
            Require((await ready).GetProperty("type").GetString() == "BridgeReady");
            Require(session.Environment["MECHANICA_BRIDGE_SESSION"] == session.SessionId);
            Require(session.Environment["MECHANICA_BRIDGE_INSTANCE"] == "survival");
            Require(session.Environment["MECHANICA_BRIDGE_TOKEN"].Length == 64);
        });

        await check("Game bridge rejects a forged token and can authenticate a later connection", async () =>
        {
            var calls = 0;
            await using var session = Create((_, _) => { calls++; return Task.FromResult(Reply("ready")); });
            using (var client = await ConnectAsync(session))
            {
                var hello = Hello(session);
                hello["token"] = new string('0', 64);
                await SendAsync(client, hello);
                await Task.Delay(100);
                Code(await ReadAsync(client), "unauthorized");
                await ClosedAsync(client);
            }
            using var valid = await AuthenticateAsync(session);
            await SendAsync(valid, Request(session));
            Require((await ReadAsync(valid)).GetProperty("payload").GetProperty("status").GetString() == "ready");
            Require(calls == 1);
        });

        await check("Game bridge negotiates bootstrap and protocol before dispatch", async () =>
        {
            await using var session = Create((_, _) => throw new Exception("Unauthenticated dispatch"));
            foreach (var change in new Action<JsonObject>[]
            {
                h => h["bootstrapVersion"] = 2,
                h => h["protocols"] = new JsonArray(new JsonObject { ["major"] = 2, ["minor"] = 0 }),
                h => h["instanceId"] = "another-instance"
            })
            {
                using var client = await ConnectAsync(session);
                var hello = Hello(session);
                change(hello);
                await SendAsync(client, hello);
                await Task.Delay(50);
                Code(await ReadAsync(client), hello["instanceId"]!.GetValue<string>() == "survival" ? "unsupported_protocol" : "unauthorized");
                await ClosedAsync(client);
            }
        });

        await check("Game bridge request replay is idempotent across connections and JSON field order", async () =>
        {
            var calls = 0;
            await using var session = Create((request, _) =>
            {
                calls++;
                Require(request.Type == "PrepareConnection" && request.Payload.GetProperty("host").GetString() == "play.example");
                return Task.FromResult(new BridgeReply("Plan", JsonSerializer.SerializeToElement(new { planId = "saved-plan" })));
            });
            var request = Request(session);
            using (var client = await AuthenticateAsync(session))
            {
                await SendAsync(client, request);
                Require((await ReadAsync(client)).GetProperty("type").GetString() == "Plan");
            }
            using var second = await AuthenticateAsync(session);
            var reordered = new JsonObject();
            foreach (var item in request.Reverse()) reordered[item.Key] = item.Value?.DeepClone();
            await SendAsync(second, reordered);
            Require((await ReadAsync(second)).GetProperty("payload").GetProperty("planId").GetString() == "saved-plan");
            request["payload"]!["host"] = "changed.example";
            await SendAsync(second, request);
            Code(await ReadAsync(second), "invalid_request");
            Require(calls == 1);
        });

        await check("Game bridge rejects expired mismatched and malformed request envelopes", async () =>
        {
            var calls = 0;
            await using var session = Create((_, _) => { calls++; return Task.FromResult(Reply("ready")); });
            using var client = await AuthenticateAsync(session);
            foreach (var (change, code) in new (Action<JsonObject>, string)[]
            {
                (r => r["sessionId"] = Guid.NewGuid().ToString(), "unauthorized"),
                (r => r["instanceId"] = "other", "unauthorized"),
                (r => r["protocolMajor"] = 2, "unsupported_protocol"),
                (r => r["expiresUtc"] = DateTimeOffset.UtcNow.AddSeconds(-1), "request_expired"),
                (r => r["expiresUtc"] = DateTimeOffset.UtcNow.AddMinutes(16), "invalid_request"),
                (r => r["requestId"] = Guid.Empty.ToString(), "invalid_request"),
                (r => r["type"] = "RunCommand", "invalid_request"),
                (r => r["payload"] = "not an object", "invalid_request")
            })
            {
                var request = Request(session);
                change(request);
                await SendAsync(client, request);
                Code(await ReadAsync(client), code);
            }
            Require(calls == 0);
        });

        await check("Game bridge Cancel runs on a second authenticated pipe while Apply is pending", async () =>
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var applies = 0;
            await using var session = Create(async (request, ct) =>
            {
                if (request.Type == "Apply")
                {
                    Interlocked.Increment(ref applies);
                    started.TrySetResult();
                    await cancel.Task.WaitAsync(ct);
                }
                else if (request.Type == "Cancel") cancel.TrySetResult();
                return Reply("cancelled");
            });
            using var applyClient = await AuthenticateAsync(session);
            var planId = Guid.NewGuid().ToString("D");
            var apply = Request(session, "Apply", new { planId });
            await SendAsync(applyClient, apply);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancelClient = await AuthenticateAsync(session);
            await SendAsync(cancelClient, apply);
            Require((await ReadAsync(cancelClient)).GetProperty("payload").GetProperty("status").GetString() == "running");
            await SendAsync(cancelClient, Request(session, "Cancel", new { planId }));
            Require((await ReadAsync(cancelClient)).GetProperty("payload").GetProperty("status").GetString() == "cancelled");
            Require((await ReadAsync(applyClient)).GetProperty("payload").GetProperty("status").GetString() == "cancelled");
            Require(applies == 1);
        });

        await check("Game bridge request cache is bounded and reserves a Cancel slot", async () =>
        {
            var calls = 0;
            await using var session = Create((_, _) => { calls++; return Task.FromResult(Reply("ready")); });
            using var client = await AuthenticateAsync(session);
            JsonObject? first = null;
            for (var i = 0; i < 127; i++)
            {
                var request = Request(session);
                first ??= request;
                await SendAsync(client, request);
                Require((await ReadAsync(client)).GetProperty("payload").GetProperty("status").GetString() == "ready");
            }
            await SendAsync(client, Request(session));
            Code(await ReadAsync(client), "busy");
            await SendAsync(client, Request(session, "Cancel", new { planId = Guid.NewGuid() }));
            Require((await ReadAsync(client)).GetProperty("payload").GetProperty("status").GetString() == "ready");
            await SendAsync(client, first!);
            Require((await ReadAsync(client)).GetProperty("payload").GetProperty("status").GetString() == "ready");
            Require(calls == 128);
        });

        await check("Game bridge does not cache busy as a completed request", async () =>
        {
            var calls = 0;
            await using var session = Create((_, _) => Task.FromResult(++calls == 1
                ? new BridgeReply("Result", JsonSerializer.SerializeToElement(new { status = "error", code = "busy", retryable = true }))
                : Reply("ready")));
            using var client = await AuthenticateAsync(session);
            var request = Request(session);
            await SendAsync(client, request);
            Code(await ReadAsync(client), "busy");
            await SendAsync(client, request);
            Require((await ReadAsync(client)).GetProperty("payload").GetProperty("status").GetString() == "ready");
            Require(calls == 2);
        });

        await check("Game bridge rejects oversized frames and duplicate JSON keys before dispatch", async () =>
        {
            var calls = 0;
            await using var session = Create((_, _) => { calls++; return Task.FromResult(Reply("ready")); });
            using (var client = await AuthenticateAsync(session))
            {
                var header = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(header, 16385);
                await client.WriteAsync(header);
                await ClosedAsync(client);
            }
            using (var client = await AuthenticateAsync(session))
            {
                var duplicate = Request(session).ToJsonString().Insert(1, "\"type\":\"Cancel\",");
                await SendBytesAsync(client, Encoding.UTF8.GetBytes(duplicate));
                await ClosedAsync(client);
            }
            Require(calls == 0);
        });

        await check("Game bridge shutdown cancels handlers and invalidates the pipe session", async () =>
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var session = Create(async (_, ct) =>
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                finally { if (ct.IsCancellationRequested) cancelled.TrySetResult(); }
                return Reply("ready");
            });
            await using (session)
            {
                using var client = await AuthenticateAsync(session);
                await SendAsync(client, Request(session, "Apply", new { planId = Guid.NewGuid() }));
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await ClosedAsync(client);
                using var self = Process.GetCurrentProcess();
                try { session.BindProcess(self); throw new Exception("Disposed session accepted another process."); }
                catch (ObjectDisposedException) { }
            }
        });

        await check("Game bridge checks native client PID and invalidates on bound process exit", async () =>
        {
            using var child = StartWaitingTestProcess();
            await using var session = new GameBridgeSession("survival", (_, _) => throw new Exception("Wrong process dispatched"));
            try
            {
                session.BindProcess(child);
                using var client = await ConnectAsync(session);
                try { await SendAsync(client, Hello(session)); }
                catch (IOException) { }
                await ClosedAsync(client);
            }
            finally
            {
                if (!child.HasExited) child.Kill();
                await child.WaitForExitAsync();
            }
            using var self = Process.GetCurrentProcess();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                try { session.BindProcess(self); throw new Exception("Exited session was rebound."); }
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) when (DateTime.UtcNow < deadline) { await Task.Delay(10); }
            }
        });
    }

    private static GameBridgeSession Create(Func<BridgeRequest, CancellationToken, Task<BridgeReply>> handler)
    {
        var session = new GameBridgeSession("survival", handler);
        BindSelf(session);
        return session;
    }
    private static void BindSelf(GameBridgeSession session)
    {
        using var self = Process.GetCurrentProcess();
        session.BindProcess(self);
    }
    private static BridgeReply Reply(string status) => new("Result", JsonSerializer.SerializeToElement(new { status }));
    private static JsonObject Hello(GameBridgeSession session) => new()
    {
        ["type"] = "BridgeHello", ["bootstrapVersion"] = 1, ["sessionId"] = session.SessionId,
        ["instanceId"] = "survival", ["token"] = session.Environment["MECHANICA_BRIDGE_TOKEN"],
        ["protocols"] = new JsonArray(new JsonObject { ["major"] = 1, ["minor"] = 0 })
    };
    private static JsonObject Request(GameBridgeSession session, string type = "PrepareConnection", object? payload = null) => new()
    {
        ["type"] = type, ["protocolMajor"] = 1, ["protocolMinor"] = 0, ["sessionId"] = session.SessionId,
        ["instanceId"] = "survival", ["requestId"] = Guid.NewGuid().ToString("D"), ["expiresUtc"] = DateTimeOffset.UtcNow.AddMinutes(5),
        ["payload"] = JsonSerializer.SerializeToNode(payload ?? new { host = "play.example", port = 25565 })
    };
    private static async Task<NamedPipeClientStream> ConnectAsync(GameBridgeSession session)
    {
        var client = new NamedPipeClientStream(".", session.Environment["MECHANICA_BRIDGE_PIPE"], PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(timeout.Token);
            return client;
        }
        catch { client.Dispose(); throw; }
    }
    private static async Task<NamedPipeClientStream> AuthenticateAsync(GameBridgeSession session)
    {
        var client = await ConnectAsync(session);
        try
        {
            await SendAsync(client, Hello(session));
            Require((await ReadAsync(client)).GetProperty("type").GetString() == "BridgeReady");
            return client;
        }
        catch { client.Dispose(); throw; }
    }
    private static Task SendAsync(Stream pipe, JsonObject data) => SendBytesAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(data));
    private static async Task SendBytesAsync(Stream pipe, byte[] data)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)data.Length);
        await pipe.WriteAsync(header.AsMemory(0, 1), timeout.Token);
        await pipe.WriteAsync(header.AsMemory(1), timeout.Token);
        await pipe.WriteAsync(data, timeout.Token);
        await pipe.FlushAsync(timeout.Token);
    }
    private static async Task<JsonElement> ReadAsync(Stream pipe)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var header = new byte[4];
        await pipe.ReadExactlyAsync(header, timeout.Token);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        Require(length is > 0 and <= 16384);
        var bytes = new byte[length];
        await pipe.ReadExactlyAsync(bytes, timeout.Token);
        using var result = JsonDocument.Parse(bytes);
        return result.RootElement.Clone();
    }
    private static async Task ClosedAsync(Stream pipe)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { Require(await pipe.ReadAsync(new byte[1], timeout.Token) == 0); }
        catch (IOException) { }
    }
    private static Process StartWaitingTestProcess()
    {
        var start = new ProcessStartInfo(System.Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(GameBridgeTests).Assembly.Location);
        start.ArgumentList.Add("-jar");
        start.ArgumentList.Add("wait.jar");
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start test process.");
    }
    private static void Code(JsonElement reply, string expected) => Require(reply.GetProperty("payload").GetProperty("code").GetString() == expected);
    private static void Require(bool value)
    {
        if (!value) throw new InvalidOperationException("Game bridge assertion failed.");
    }
}

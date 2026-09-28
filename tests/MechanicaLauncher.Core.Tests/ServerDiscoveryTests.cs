using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MechanicaLauncher.Core.Servers;

internal static class ServerDiscoveryTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Server discovery preserves ordinary status and ping without an advertisement", async () =>
        {
            var status = await QueryAsync(Status());
            Require(status.Version == "1.21.1" && status.Online == 3 && status.Maximum == 20 &&
                status.Description == "Hello world" && status.LatencyMs >= 0 && status.SyncDescriptorUrl == null);
        });
        await check("Server discovery reads HTTPS descriptors without fetching them", async () =>
        {
            const string url = "https://sync.example.test:8443/mechanica/descriptor.json?channel=user@example.test";
            var status = await QueryAsync(Status(Advertisement(url)));
            Require(status.SyncDescriptorUrl == url && status.Version == "1.21.1" && status.Online == 3);
        });
        await check("Server discovery permits HTTP only between literal loopback hosts", async () =>
        {
            foreach (var url in new[] { "http://127.0.0.1:8080/mechanica/descriptor.json", "http://[::1]:8080/mechanica/descriptor.json" })
                Require((await QueryAsync(Status(Advertisement(url)))).SyncDescriptorUrl == url);
        });
        await check("Server discovery does not trust a hostname resolving to loopback for HTTP", async () =>
        {
            await RejectAsync(Status(Advertisement("http://127.0.0.1:8080/mechanica/descriptor.json")), "localhost");
        });
        await check("Server discovery rejects HTTP destinations outside literal loopback", async () =>
        {
            foreach (var url in new[] { "http://sync.example.test/mechanica/descriptor.json", "http://192.0.2.1/descriptor.json",
                "http://localhost:8080/descriptor.json", "http://127.0.0.1.example.test/descriptor.json" })
                await RejectAsync(Status(Advertisement(url)));
        });
        await check("Server discovery rejects credentials and fragments in descriptor URLs", async () =>
        {
            foreach (var url in new[] { "https://user:password@example.test/descriptor.json", "https://user@example.test/descriptor.json",
                "https://@example.test/descriptor.json", "https://example.test/descriptor.json#section", "https://example.test/descriptor.json#" })
                await RejectAsync(Status(Advertisement(url)));
        });
        await check("Server discovery requires protocol integer one", async () =>
        {
            foreach (var protocol in new[] { "0", "2", "-1", "1.0", "1e0", "2147483648", "\"1\"", "true", "null" })
                await RejectAsync(Status("{\"protocol\":" + protocol + ",\"descriptorUrl\":\"https://example.test/descriptor.json\"}"));
        });
        await check("Server discovery rejects malformed and incomplete advertisements", async () =>
        {
            foreach (var advertisement in new[] { "null", "[]", "true", "\"sync\"", "{}", "{\"protocol\":1}",
                "{\"descriptorUrl\":\"https://example.test/descriptor.json\"}", "{\"protocol\":1,\"descriptorUrl\":null}",
                "{\"protocol\":1,\"descriptorUrl\":42}" })
                await RejectAsync(Status(advertisement));
            await RejectAsync("{\"mechanica\":{");
        });
        await check("Server discovery rejects ambiguous duplicate advertisement fields", async () =>
        {
            await RejectAsync(Status("{\"protocol\":2,\"protocol\":1,\"descriptorUrl\":\"https://example.test/descriptor.json\"}"));
            await RejectAsync(Status("{\"protocol\":1,\"descriptorUrl\":\"http://example.test/a\",\"descriptorUrl\":\"https://example.test/b\"}"));
            var status = Status(Advertisement("https://example.test/descriptor.json"));
            await RejectAsync(status[..^1] + ",\"mechanica\":" + Advertisement("https://example.test/other.json") + "}");
        });
        await check("Server discovery bounds descriptor URLs and requires absolute well-formed URLs", async () =>
        {
            const string prefix = "https://example.test/";
            string maximum = prefix + new string('a', 2048 - prefix.Length);
            Require((await QueryAsync(Status(Advertisement(maximum)))).SyncDescriptorUrl == maximum);
            foreach (var url in new[] { maximum + "a", "", "/mechanica/descriptor.json", "//example.test/descriptor.json",
                "https://", "https://example.test:65536/descriptor.json", "https://example.test:0/descriptor.json",
                "https://example.test/a b", " https://example.test/descriptor.json", "ftp://example.test/descriptor.json" })
                await RejectAsync(Status(Advertisement(url)));
        });
        await check("Server discovery still rejects a mismatched pong with a valid advertisement", async () =>
        {
            bool rejected = false;
            try { await QueryAsync(Status(Advertisement("https://example.test/descriptor.json")), badPong: true); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected);
        });
    }

    private static string Status(string? advertisement = null)
    {
        const string ordinary = """{"version":{"name":"1.21.1","protocol":767},"players":{"online":3,"max":20},"description":{"text":"§aHello ","extra":[{"text":"world"}]}}""";
        return advertisement == null ? ordinary : ordinary[..^1] + ",\"mechanica\":" + advertisement + "}";
    }

    private static string Advertisement(string url) => JsonSerializer.Serialize(new { protocol = 1, descriptorUrl = url });

    private static async Task RejectAsync(string json, string host = "127.0.0.1")
    {
        bool rejected = false;
        try { await QueryAsync(json, host, expectPing: false); }
        catch (InvalidDataException error) { rejected = !string.IsNullOrWhiteSpace(error.Message); }
        Require(rejected);
    }

    private static async Task<ServerStatus> QueryAsync(string json, string host = "127.0.0.1", bool expectPing = true, bool badPong = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var peer = RespondAsync();
        try
        {
            var status = await new ServerStatusClient().QueryAsync(host, port, timeout.Token);
            await peer;
            return status;
        }
        finally
        {
            timeout.Cancel();
            try { await peer; }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        }

        async Task RespondAsync()
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            using var handshake = new MemoryStream(await ServerStatusClient.ReadPacketAsync(stream, timeout.Token));
            Require(await ServerStatusClient.ReadVarIntAsync(handshake, timeout.Token) == 0);
            Require(await ServerStatusClient.ReadVarIntAsync(handshake, timeout.Token) == -1);
            int hostLength = await ServerStatusClient.ReadVarIntAsync(handshake, timeout.Token);
            var hostBytes = new byte[hostLength];
            await handshake.ReadExactlyAsync(hostBytes, timeout.Token);
            Require(Encoding.UTF8.GetString(hostBytes) == host);
            Require((handshake.ReadByte() << 8 | handshake.ReadByte()) == port);
            Require(await ServerStatusClient.ReadVarIntAsync(handshake, timeout.Token) == 1 && handshake.Position == handshake.Length);
            Require((await ServerStatusClient.ReadPacketAsync(stream, timeout.Token)).SequenceEqual(new byte[] { 0 }));
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            using var response = new MemoryStream();
            ServerStatusClient.WriteVarInt(response, 0);
            ServerStatusClient.WriteVarInt(response, bytes.Length);
            response.Write(bytes);
            await ServerStatusClient.WritePacketAsync(stream, response.ToArray(), timeout.Token);
            if (!expectPing) return;
            byte[] ping = await ServerStatusClient.ReadPacketAsync(stream, timeout.Token);
            Require(ping.Length == 9 && ping[0] == 1);
            if (badPong) ping[^1] ^= 1;
            await ServerStatusClient.WritePacketAsync(stream, ping, timeout.Token);
        }
    }

    private static void Require(bool condition) { if (!condition) throw new Exception("Server discovery assertion failed."); }
}

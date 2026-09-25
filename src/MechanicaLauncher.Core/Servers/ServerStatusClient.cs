using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MechanicaLauncher.Core.Servers;

public sealed record ServerStatus(string Version, int Online, int Maximum, string Description, long LatencyMs);

public sealed partial class ServerStatusClient
{
    public async Task<ServerStatus> QueryAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535 || host.Length is 0 or > 253) throw new ArgumentException("Invalid server address.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var token = timeout.Token;
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, token);
        await using var stream = client.GetStream();
        using var handshake = new MemoryStream();
        WriteVarInt(handshake, 0);
        WriteVarInt(handshake, -1);
        var address = Encoding.UTF8.GetBytes(host);
        WriteVarInt(handshake, address.Length);
        handshake.Write(address);
        handshake.WriteByte((byte)(port >> 8));
        handshake.WriteByte((byte)port);
        WriteVarInt(handshake, 1);
        await WritePacketAsync(stream, handshake.ToArray(), token);
        await WritePacketAsync(stream, [0], token);
        var packet = await ReadPacketAsync(stream, token);
        using var data = new MemoryStream(packet);
        if (await ReadVarIntAsync(data, token) != 0) throw new InvalidDataException("Invalid status response.");
        var length = await ReadVarIntAsync(data, token);
        if (length < 0 || length != data.Length - data.Position) throw new InvalidDataException("Invalid status length.");
        using var json = JsonDocument.Parse(packet.AsMemory((int)data.Position, length));
        var root = json.RootElement;
        var version = root.TryGetProperty("version", out var v) && v.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
        int online = 0, maximum = 0;
        if (root.TryGetProperty("players", out var players))
        {
            if (players.TryGetProperty("online", out var o)) o.TryGetInt32(out online);
            if (players.TryGetProperty("max", out var m)) m.TryGetInt32(out maximum);
        }
        var description = root.TryGetProperty("description", out var d) ? ReadText(d) : "";
        description = Colors().Replace(description, "");
        var ping = new byte[9];
        ping[0] = 1;
        BinaryPrimitives.WriteInt64BigEndian(ping.AsSpan(1), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var clock = Stopwatch.StartNew();
        await WritePacketAsync(stream, ping, token);
        var pong = await ReadPacketAsync(stream, token);
        if (!ping.AsSpan().SequenceEqual(pong)) throw new InvalidDataException("Invalid ping response.");
        return new(version[..Math.Min(version.Length, 100)], Math.Max(0, online), Math.Max(0, maximum),
            description[..Math.Min(description.Length, 1000)], clock.ElapsedMilliseconds);
    }

    internal static async Task<byte[]> ReadPacketAsync(Stream stream, CancellationToken token)
    {
        var length = await ReadVarIntAsync(stream, token);
        if (length is < 1 or > 262144) throw new InvalidDataException("Server packet is too large or empty.");
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, token);
        return buffer;
    }
    internal static async Task WritePacketAsync(Stream stream, byte[] packet, CancellationToken token)
    {
        using var data = new MemoryStream();
        WriteVarInt(data, packet.Length);
        data.Write(packet);
        await stream.WriteAsync(data.ToArray(), token);
    }
    internal static void WriteVarInt(Stream stream, int value)
    {
        var remaining = (uint)value;
        do
        {
            var next = (byte)(remaining & 0x7f);
            remaining >>= 7;
            stream.WriteByte(remaining > 0 ? (byte)(next | 0x80) : next);
        } while (remaining > 0);
    }
    internal static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken token)
    {
        int value = 0;
        var buffer = new byte[1];
        for (var index = 0; index < 5; index++)
        {
            await stream.ReadExactlyAsync(buffer, token);
            var next = buffer[0];
            if (index == 4 && (next & 0xf0) != 0) throw new InvalidDataException("Invalid VarInt.");
            value |= (next & 0x7f) << (index * 7);
            if ((next & 0x80) == 0) return value;
        }
        throw new InvalidDataException("Invalid VarInt.");
    }
    private static string ReadText(JsonElement element, int depth = 0)
    {
        if (depth > 16) return "";
        if (element.ValueKind == JsonValueKind.String) return element.GetString() ?? "";
        if (element.ValueKind == JsonValueKind.Array) return string.Concat(element.EnumerateArray().Select(e => ReadText(e, depth + 1)));
        if (element.ValueKind != JsonValueKind.Object) return "";
        var text = element.TryGetProperty("text", out var t) ? ReadText(t, depth + 1) : "";
        if (element.TryGetProperty("extra", out var extra)) text += ReadText(extra, depth + 1);
        return text;
    }
    [GeneratedRegex("§.")]
    private static partial Regex Colors();
}

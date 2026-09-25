using System.Net;
using System.Text.Json;
using MechanicaLauncher.Core.IO;

namespace MechanicaLauncher.Core.Servers;

public sealed record FavoriteServer(string Id, string Name, string Host, int Port, string InstanceId)
{
    public string Address => Host.Contains(':') ? $"[{Host}]:{Port}" : Port == 25565 ? Host : $"{Host}:{Port}";
}

public sealed class FavoriteServers(string? dataDirectory = null)
{
    private string StorePath => Path.Combine(dataDirectory ?? LauncherPaths.DataDirectory, "servers.json");
    public IReadOnlyList<FavoriteServer> Load()
    {
        foreach (var path in new[] { StorePath, StorePath + ".bak" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                return (JsonSerializer.Deserialize<FavoriteServer[]>(File.ReadAllText(path)) ?? [])
                    .Where(IsValid).DistinctBy(s => s.Id).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return [];
    }
    public void Save(IEnumerable<FavoriteServer> servers)
    {
        var list = servers.ToArray();
        foreach (var server in list)
            if (!IsValid(server))
                throw new InvalidDataException("Invalid server address or name.");
        AtomicFile.WriteText(StorePath, JsonSerializer.Serialize(list), keepBackup: true);
    }
    public static bool TryParseAddress(string address, out string host, out int port)
    {
        host = address.Trim();
        port = 25565;
        if (host.StartsWith('['))
        {
            var end = host.IndexOf(']');
            if (end < 0) return false;
            var suffix = host[(end + 1)..];
            host = host[1..end];
            if (suffix.Length > 0 && (!suffix.StartsWith(':') || !int.TryParse(suffix[1..], out port))) return false;
            if (!IPAddress.TryParse(host, out _)) return false;
        }
        else if (host.Count(c => c == ':') == 1)
        {
            var index = host.LastIndexOf(':');
            if (!int.TryParse(host[(index + 1)..], out port)) return false;
            host = host[..index];
        }
        return IsValidHost(host) && port is > 0 and <= 65535;
    }
    private static bool IsValidHost(string host) => !string.IsNullOrWhiteSpace(host) && host.Length <= 253 &&
        !host.Any(char.IsWhiteSpace) && Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;

    private static bool IsValid(FavoriteServer? server) => server != null && !string.IsNullOrWhiteSpace(server.Id) &&
        !string.IsNullOrWhiteSpace(server.Name) && IsValidHost(server.Host) && server.Port is > 0 and <= 65535 &&
        !string.IsNullOrWhiteSpace(server.InstanceId) && server.InstanceId is not ("." or "..") &&
        server.InstanceId.IndexOfAny(['/', '\\', ':']) < 0;
}

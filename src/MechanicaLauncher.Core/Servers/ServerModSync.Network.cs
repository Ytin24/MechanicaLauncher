using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MechanicaLauncher.Core.Servers;

public sealed partial class ServerModSync
{
    internal const int MaxJsonBytes = 1024 * 1024;
    internal const int MaxFiles = 2048;
    internal const long MaxFileBytes = 512L * 1024 * 1024;
    internal const long MaxPlanBytes = 4L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 16 };
    private static readonly HttpRequestOptionsKey<bool> LocalRequest = new("Mechanica.ServerSync.AllowLoopback");
    private static readonly HttpClient DefaultHttp = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, ct) =>
            {
                bool local = context.InitialRequestMessage.Options.TryGetValue(LocalRequest, out var allowed) && allowed;
                string host = context.DnsEndPoint.Host.Trim('[', ']');
                bool literal = IPAddress.TryParse(host, out var parsed);
                var addresses = literal ? new[] { parsed! } : await Dns.GetHostAddressesAsync(host, ct);
                if (addresses.Length == 0 || addresses.Any(a => !PublicAddress(a) && !(local && literal && IPAddress.IsLoopback(a))))
                    throw Error("invalid_manifest", "Недопустимый сетевой адрес источника.");
                Exception? last = null;
                foreach (var address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                    {
                        socket.Dispose();
                        ct.ThrowIfCancellationRequested();
                        last = ex;
                    }
                }
                throw new HttpRequestException("Не удалось подключиться к источнику.", last);
            }
        };
        return new(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static bool PublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] is not (0 or 10 or 127) && b[0] < 224 &&
                !(b[0] == 100 && b[1] is >= 64 and <= 127) && !(b[0] == 169 && b[1] == 254) &&
                !(b[0] == 172 && b[1] is >= 16 and <= 31) && !(b[0] == 192 && b[1] == 168) &&
                !(b[0] == 192 && b[1] == 0) && !(b[0] == 198 && b[1] is 18 or 19) &&
                !(b[0] == 198 && b[1] == 51 && b[2] == 100) && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (b[0] & 0xe0) == 0x20 &&
            !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8);
    }

    private static Uri CheckedUrl(string? value, bool allowLocal)
    {
        if (value == null || value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Host.Length == 0)
            throw Error("invalid_manifest", "Некорректный адрес источника.");
        bool literal = IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var address);
        bool local = allowLocal && literal && IPAddress.IsLoopback(address!);
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && local) ||
            literal && !PublicAddress(address!) && !local || !literal && (uri.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) || !uri.IdnHost.Contains('.')))
            throw Error("invalid_manifest", "Источник должен использовать HTTPS и публичный адрес.");
        return uri;
    }

    private static string Origin(Uri uri) => uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();

    private async Task<byte[]> ReadNetworkBytesAsync(Uri uri, bool allowLocal, CancellationToken ct)
    {
        using var output = new MemoryStream();
        await DownloadAsync(uri, allowLocal, output, MaxJsonBytes, true, ct);
        return output.ToArray();
    }

    private async Task DownloadAsync(Uri uri, bool allowLocal, Stream output, long limit, bool json, CancellationToken ct)
    {
        CheckedUrl(uri.AbsoluteUri, allowLocal);
        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            output.Position = 0;
            output.SetLength(0);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(json ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5));
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(json ? "application/json" : "application/java-archive"));
                request.Headers.UserAgent.ParseAdd("MechanicaLauncher/1.0");
                request.Options.Set(LocalRequest, allowLocal);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and < 400 || response.RequestMessage?.RequestUri is { } final && final != uri)
                    throw Error("invalid_manifest", "Перенаправление источника не разрешено.");
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new HttpRequestException($"Источник вернул HTTP {(int)response.StatusCode}.", null, response.StatusCode);
                if (response.Content.Headers.ContentLength > limit || response.Content.Headers.ContentEncoding.Count != 0)
                    throw Error("invalid_manifest", "Ответ источника превышает допустимый размер или использует сжатие.");
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > limit) throw Error("invalid_manifest", "Ответ источника превышает допустимый размер.");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                }
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                if (attempt >= 2 || ex is HttpRequestException { StatusCode: { } status } && (int)status is >= 400 and < 500 && status != HttpStatusCode.RequestTimeout && (int)status != 429)
                    throw Error("network_error", "Источник недоступен.", ex);
                await Task.Delay(TimeSpan.FromMilliseconds(150 * (attempt + 1)), ct).ConfigureAwait(false);
            }
        }
    }

    private sealed class CatalogHandler(ServerModSync owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri == null || Origin(request.RequestUri) != "https://api.modrinth.com")
                throw Error("invalid_manifest", "Недопустимый адрес Modrinth.");
            var data = await owner.ReadNetworkBytesAsync(request.RequestUri, false, ct);
            using var validation = ParseJson(data);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(data), RequestMessage = request };
        }
    }

    private static JsonDocument ParseJson(byte[] data, int limit = MaxJsonBytes)
    {
        if (data.Length > limit) throw Error("invalid_manifest", "JSON превышает допустимый размер.");
        JsonDocument document;
        try { document = JsonDocument.Parse(data, new() { MaxDepth = 16 }); }
        catch (JsonException ex) { throw Error("invalid_manifest", "Некорректный JSON.", ex); }
        try { CheckProperties(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void CheckProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject())
            {
                if (!names.Add(p.Name)) throw Error("invalid_manifest", "Повторяющееся поле JSON.");
                CheckProperties(p.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckProperties(item);
    }

    private static T ReadJson<T>(byte[] bytes, int limit = MaxJsonBytes)
    {
        using var document = ParseJson(bytes, limit);
        try { return document.RootElement.Deserialize<T>(JsonOptions) ?? throw Error("invalid_manifest", "Пустой JSON."); }
        catch (JsonException ex) { throw Error("invalid_manifest", "Некорректная структура JSON.", ex); }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant();
    private static bool ValidHash(string? hash) => hash != null && hash.Length == 128 && hash.All(Uri.IsHexDigit);
    private static bool ValidId(string? id) => id != null && Regex.IsMatch(id, "^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    private static string FileName(string? name, bool disabled = false)
    {
        if (name == null || name.Length is < 5 or > 180 || name != name.Normalize(NormalizationForm.FormC) ||
            name.Contains("..", StringComparison.Ordinal) || name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)) ||
            name.EndsWith('.') || name.EndsWith(' ') || name.Trim() != name ||
            !(name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || disabled && name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)) ||
            Regex.IsMatch(name.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw Error("invalid_manifest", "Недопустимое имя JAR.");
        return name;
    }

    private static string SafePath(string path)
    {
        string full = Path.GetFullPath(path);
        for (string? part = full; part != null; part = Path.GetDirectoryName(part))
        {
            try
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw Error("conflict", "Путь содержит ссылку или junction.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }

    private static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static ServerModSyncException Error(string code, string message, Exception? inner = null) => new(code, message, inner);
}

using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using MechanicaLauncher.Core.Servers;

namespace MechanicaLauncher.Core.Presets;

public enum PresetSource { Modrinth, Url }

// A share link for a ready-made instance. Three spellings resolve to the same preset:
//   mechanica://pack?modrinth=<slug>[&version=<id or number>][&server=host:port]
//   mechanica://pack?url=<https .mrpack>[&sha512=<hex>][&server=host:port]
//   https://ytin24.github.io/MechanicaLauncher/p/?<same query>   (clickable in YouTube descriptions)
// Pasted Modrinth pages (modrinth.com/modpack/<slug>[/version/<v>]) and direct https .mrpack links are accepted too.
public sealed partial record PresetLink(PresetSource Source, string Target, string? Version = null, string? Sha512 = null, string? Server = null)
{
    public const string Scheme = "mechanica";
    public const string WebBase = "https://ytin24.github.io/MechanicaLauncher/p/";
    private const int MaxLength = 4096;

    public static PresetLink Modrinth(string project, string? version = null, string? server = null) =>
        Validate(new(PresetSource.Modrinth, project, version, null, server)) ?? throw new ArgumentException("Invalid Modrinth preset link.");

    public static PresetLink FromUrl(string url, string? sha512 = null, string? server = null) =>
        Validate(new(PresetSource.Url, url, null, sha512, server)) ?? throw new ArgumentException("Invalid preset URL.");

    public static PresetLink? ParseArgs(IEnumerable<string> args)
    {
        foreach (var arg in args)
            if (arg.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase) && TryParse(arg, out var link)) return link;
        return null;
    }

    public static bool TryParse(string? text, out PresetLink link)
    {
        link = null!;
        text = text?.Trim().Trim('"', '\'', '<', '>');
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength || !Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        PresetLink? parsed = null;
        if (uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            if (uri.Host.Equals("pack", StringComparison.OrdinalIgnoreCase)) parsed = FromQuery(uri.Query);
        }
        else if (uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0)
        {
            var web = new Uri(WebBase);
            string path = uri.AbsolutePath.TrimEnd('/') + "/";
            if (uri.Host.Equals(web.Host, StringComparison.OrdinalIgnoreCase) && path.StartsWith(web.AbsolutePath, StringComparison.OrdinalIgnoreCase))
                parsed = FromQuery(uri.Query.Length > 1 ? uri.Query : "?" + uri.Fragment.TrimStart('#'));
            else if (uri.Host.Equals("modrinth.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("www.modrinth.com", StringComparison.OrdinalIgnoreCase))
            {
                var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0] == "modpack")
                    parsed = new(PresetSource.Modrinth, parts[1], parts.Length >= 4 && parts[2] == "version" ? Uri.UnescapeDataString(parts[3]) : null);
            }
            else if (uri.AbsolutePath.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
                parsed = new(PresetSource.Url, uri.AbsoluteUri);
        }
        if (parsed == null || Validate(parsed) is not { } valid) return false;
        link = valid;
        return true;
    }

    private static PresetLink? FromQuery(string query)
    {
        var values = HttpUtility.ParseQueryString(query);
        string? server = Clean(values["server"]);
        if (Clean(values["modrinth"]) is { } project) return new(PresetSource.Modrinth, project, Clean(values["version"]), null, server);
        if (Clean(values["url"]) is { } url) return new(PresetSource.Url, url, null, Clean(values["sha512"])?.ToLowerInvariant(), server);
        return null;
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static PresetLink? Validate(PresetLink link)
    {
        if (link.Server != null && (link.Server.Length > 260 || !FavoriteServers.TryParseAddress(link.Server, out _, out _))) return null;
        if (link.Source == PresetSource.Modrinth)
        {
            if (!ProjectPattern().IsMatch(link.Target) || link.Sha512 != null) return null;
            if (link.Version != null && !VersionPattern().IsMatch(link.Version)) return null;
            return link;
        }
        if (link.Version != null || link.Target.Length > 2048 || !Uri.TryCreate(link.Target, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) return null;
        if (link.Sha512 != null && (link.Sha512.Length != 128 || !link.Sha512.All(Uri.IsHexDigit))) return null;
        return link with { Target = uri.AbsoluteUri };
    }

    public string Query
    {
        get
        {
            var query = new StringBuilder();
            void Add(string key, string? value)
            {
                if (value == null) return;
                query.Append(query.Length == 0 ? '?' : '&').Append(key).Append('=').Append(Uri.EscapeDataString(value));
            }
            if (Source == PresetSource.Modrinth) { Add("modrinth", Target); Add("version", Version); }
            else { Add("url", Target); Add("sha512", Sha512); }
            Add("server", Server);
            return query.ToString();
        }
    }

    public string ProtocolUri => Scheme + "://pack" + Query;
    public string WebUri => WebBase + Query;

    // Host shown to the player before anything is downloaded.
    public string SourceHost => Source == PresetSource.Modrinth ? "modrinth.com" : new Uri(Target).IdnHost;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ProjectPattern();
    [GeneratedRegex("^[A-Za-z0-9._+-]{1,64}$")]
    private static partial Regex VersionPattern();
}

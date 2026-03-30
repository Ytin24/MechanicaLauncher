using Microsoft.Win32;

namespace MechanicaLauncher.Core.Protocol;

public record ConnectRequest(string Server, int Port, string Version);
public record EventRequest(string ConfigUrl);

public static class ProtocolHandler
{
    private const string Protocol = "mechanica";

    public static void Register()
    {
        try
        {
            var exePath = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exePath)) return;

            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Protocol}");
            key.SetValue("", $"URL:{Protocol}");
            key.SetValue("URL Protocol", "");

            using var cmdKey = key.CreateSubKey(@"shell\open\command");
            cmdKey.SetValue("", $"\"{exePath}\" \"%1\"");
        }
        catch { }
    }

    public static ConnectRequest? ParseConnect(string[] args)
    {
        foreach (var arg in args)
        {
            if (!arg.StartsWith($"{Protocol}://", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var uri = new Uri(arg);
                if (uri.Host != "connect") continue;
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                var server = query["server"];
                var portStr = query["port"];
                var version = query["version"];
                if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(version)) continue;
                return new ConnectRequest(server, int.TryParse(portStr, out var p) ? p : 25565, version);
            }
            catch { }
        }
        return null;
    }

    public static EventRequest? ParseEvent(string[] args)
    {
        foreach (var arg in args)
        {
            if (!arg.StartsWith($"{Protocol}://", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var uri = new Uri(arg);
                if (uri.Host != "event") continue;
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                var url = query["url"];
                if (!string.IsNullOrEmpty(url)) return new EventRequest(url);
            }
            catch { }
        }
        return null;
    }

    // Backward compat
    public static ConnectRequest? ParseArgs(string[] args) => ParseConnect(args);
}

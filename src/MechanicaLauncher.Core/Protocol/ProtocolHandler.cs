using Microsoft.Win32;

namespace MechanicaLauncher.Core.Protocol;

public record ConnectRequest(string Server, int Port, string Version);

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

    public static ConnectRequest? ParseArgs(string[] args)
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

                var port = int.TryParse(portStr, out var p) ? p : 25565;
                return new ConnectRequest(server, port, version);
            }
            catch { }
        }

        return null;
    }
}

using System.Text.RegularExpressions;

namespace MechanicaLauncher.Core.Discord;

public record McState(McStateType Type, string? Server = null, int? Port = null, string? World = null, string? Gamemode = null, string? Dimension = null, string? Achievement = null);

public enum McStateType { Launcher, Menu, SinglePlayer, MultiPlayer, Disconnected }

public static partial class McLogParser
{
    public static McState? ParseLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;

        var connectMatch = ConnectRegex().Match(line);
        if (connectMatch.Success)
        {
            var server = connectMatch.Groups[1].Value;
            var port = int.TryParse(connectMatch.Groups[2].Value, out var p) ? p : 25565;
            return new McState(McStateType.MultiPlayer, server, port);
        }

        if (line.Contains("Joining world") || line.Contains("Loading world"))
            return new McState(McStateType.SinglePlayer);

        if (line.Contains("Stopping!") || line.Contains("Disconnecting"))
            return new McState(McStateType.Menu);

        if (line.Contains("Setting user:") || line.Contains("Narrator library"))
            return new McState(McStateType.Menu);

        if (line.Contains("[CHAT]") && line.Contains("gamemode"))
        {
            if (line.Contains("survival", StringComparison.OrdinalIgnoreCase))
                return new McState(McStateType.SinglePlayer, Gamemode: "Survival");
            if (line.Contains("creative", StringComparison.OrdinalIgnoreCase))
                return new McState(McStateType.SinglePlayer, Gamemode: "Creative");
        }

        if (line.Contains("minecraft:overworld"))
            return new McState(McStateType.SinglePlayer, Dimension: "Overworld");
        if (line.Contains("minecraft:the_nether"))
            return new McState(McStateType.SinglePlayer, Dimension: "Nether");
        if (line.Contains("minecraft:the_end"))
            return new McState(McStateType.SinglePlayer, Dimension: "The End");

        var advMatch = AdvancementRegex().Match(line);
        if (advMatch.Success)
            return new McState(McStateType.SinglePlayer, Achievement: advMatch.Groups[1].Value);

        return null;
    }

    [GeneratedRegex(@"Connecting to (.+?),\s*(\d+)")]
    private static partial Regex ConnectRegex();

    [GeneratedRegex(@"has made the advancement \[(.+?)\]")]
    private static partial Regex AdvancementRegex();
}

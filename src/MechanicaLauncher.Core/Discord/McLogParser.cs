using System.Text.RegularExpressions;

namespace MechanicaLauncher.Core.Discord;

public record McState(McStateType Type, string? Server = null, int? Port = null, string? Gamemode = null, string? Dimension = null, string? Achievement = null);

public enum McStateType { Launcher, Menu, SinglePlayer, MultiPlayer }

public sealed partial class McStateMachine
{
    private McStateType _current = McStateType.Launcher;
    private string? _server;
    private int _port = 25565;
    private string? _gamemode;
    private string? _dimension;
    private string? _achievement;
    private bool _inWorld;

    public McState State => new(_current, _server, _port, _gamemode, _dimension, _achievement);

    public bool ProcessLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;

        // Server connect — always transitions to MultiPlayer
        var connectMatch = ConnectRegex().Match(line);
        if (connectMatch.Success)
        {
            _server = connectMatch.Groups[1].Value;
            _port = int.TryParse(connectMatch.Groups[2].Value, out var p) ? p : 25565;
            _current = McStateType.MultiPlayer;
            _inWorld = true;
            return true;
        }

        // Entered world (singleplayer)
        if (line.Contains("joined the game") || line.Contains("Preparing start region"))
        {
            _current = McStateType.SinglePlayer;
            _inWorld = true;
            _server = null;
            return true;
        }

        // Disconnected from server — back to menu
        if (line.Contains("lost connection") || line.Contains("Disconnecting from server"))
        {
            _current = McStateType.Menu;
            _inWorld = false;
            _server = null;
            return true;
        }

        // Actually quit world (not ESC pause)
        if (line.Contains("Stopping server") || line.Contains("ThreadedAnvilChunkStorage: All dimensions are saved"))
        {
            _current = McStateType.Menu;
            _inWorld = false;
            return true;
        }

        // MC window loaded — menu (only if not already in world)
        if (!_inWorld && (line.Contains("Setting user:") || line.Contains("Narrator library")))
        {
            _current = McStateType.Menu;
            return true;
        }

        // Dimension changes (only update dimension, don't change state)
        // IGNORE "Saving chunks" lines — they mention all dimensions at once on ESC/quit
        if (_inWorld && !line.Contains("Saving chunks") && !line.Contains("Saving worlds"))
        {
            if (line.Contains("minecraft:overworld") && (line.Contains("Preparing") || line.Contains("Loaded dimension")))
            { _dimension = "Overworld"; return true; }
            if (line.Contains("minecraft:the_nether") && (line.Contains("Preparing") || line.Contains("Loaded dimension")))
            { _dimension = "Nether"; return true; }
            if (line.Contains("minecraft:the_end") && (line.Contains("Preparing") || line.Contains("Loaded dimension")))
            { _dimension = "The End"; return true; }
        }

        // Gamemode change
        if (line.Contains("[CHAT]") && line.Contains("Set own game mode to", StringComparison.OrdinalIgnoreCase))
        {
            if (line.Contains("Creative", StringComparison.OrdinalIgnoreCase)) _gamemode = "Creative";
            else if (line.Contains("Survival", StringComparison.OrdinalIgnoreCase)) _gamemode = "Survival";
            else if (line.Contains("Adventure", StringComparison.OrdinalIgnoreCase)) _gamemode = "Adventure";
            else if (line.Contains("Spectator", StringComparison.OrdinalIgnoreCase)) _gamemode = "Spectator";
            return true;
        }

        // Achievement
        if (line.Contains("[CHAT]"))
        {
            var advMatch = AdvancementRegex().Match(line);
            if (advMatch.Success)
            {
                _achievement = advMatch.Groups[1].Value;
                return true;
            }
        }

        return false;
    }

    public void Reset()
    {
        _current = McStateType.Launcher;
        _server = null;
        _port = 25565;
        _gamemode = null;
        _dimension = null;
        _achievement = null;
        _inWorld = false;
    }

    [GeneratedRegex(@"Connecting to (.+?),\s*(\d+)")]
    private static partial Regex ConnectRegex();

    [GeneratedRegex(@"has made the advancement \[(.+?)\]")]
    private static partial Regex AdvancementRegex();
}

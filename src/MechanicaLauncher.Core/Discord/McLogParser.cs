using System.Text.RegularExpressions;

namespace MechanicaLauncher.Core.Discord;

public record McState(McStateType Type, string? Server = null, int? Port = null, string? Gamemode = null, string? Dimension = null, string? Achievement = null, string? World = null, string? Player = null);

public enum McStateType { Launcher, Preparing, Starting, Running, Menu, Connecting, SinglePlayer, MultiPlayer }

public sealed partial class McStateMachine
{
    private readonly TimeProvider _time;
    private McState _state = new(McStateType.Starting);
    private DateTimeOffset _achievementUntil;

    public McStateMachine(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public McState State => _state.Achievement != null && _time.GetUtcNow() >= _achievementUntil
        ? _state with { Achievement = null } : _state;

    public bool ProcessLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > 8192) return false;
        var match = LogRegex().Match(line);
        if (!match.Success) return false;
        var message = match.Groups["message"].Value.Trim();
        var thread = match.Groups["thread"].Value;
        var serverThread = thread.Equals("Server thread", StringComparison.Ordinal);
        var clientThread = thread is "Render thread" or "Client thread" or "main";
        var connectionThread = clientThread || thread.StartsWith("Server Connector", StringComparison.Ordinal);
        var previous = State;
        _state = previous;
        if (connectionThread && IsDisconnect(message))
        {
            _state = new(McStateType.Menu, Player: _state.Player);
            return State != previous;
        }
        if (match.Groups["level"].Value != "INFO") return false;

        var chat = message.IndexOf("[CHAT]", StringComparison.Ordinal);
        if (chat >= 0)
        {
            if (!clientThread || _state.Type is not (McStateType.SinglePlayer or McStateType.MultiPlayer or McStateType.Connecting)) return false;
            if (_state.Type == McStateType.Connecting) _state = _state with { Type = McStateType.MultiPlayer };
            ProcessChat(message[(chat + 6)..].Trim());
            return State != previous;
        }

        if (clientThread && message.StartsWith("Setting user: ", StringComparison.Ordinal))
            _state = _state with { Player = message["Setting user: ".Length..].Trim() };

        var connect = ConnectRegex().Match(message);
        if (connectionThread && connect.Success && int.TryParse(connect.Groups[2].Value, out var port) && port is > 0 and <= 65535)
            _state = new(McStateType.Connecting, connect.Groups[1].Value.Trim(), port, Player: _state.Player);
        else if (serverThread && message.StartsWith("Starting integrated minecraft server", StringComparison.OrdinalIgnoreCase))
            _state = new(McStateType.SinglePlayer, Player: _state.Player);
        else if (serverThread && WorldRegex().Match(message) is { Success: true } world)
            _state = _state.Type == McStateType.SinglePlayer
                ? _state with { World = world.Groups[1].Value }
                : new(McStateType.SinglePlayer, World: world.Groups[1].Value, Player: _state.Player);
        else if (serverThread && message.StartsWith("Preparing start region for dimension ", StringComparison.Ordinal))
        {
            if (_state.Type is McStateType.Starting or McStateType.Menu)
                _state = new(McStateType.SinglePlayer, Player: _state.Player);
        }
        else if (serverThread && _state.Type == McStateType.SinglePlayer && message == "Stopping server")
            _state = new(McStateType.Menu, Player: _state.Player);
        else if (clientThread && _state.Type == McStateType.Connecting && LoadedAdvancementsRegex().IsMatch(message))
            _state = _state with { Type = McStateType.MultiPlayer };
        else if (clientThread && (_state.Type is McStateType.SinglePlayer or McStateType.MultiPlayer) &&
                 DimensionRegex().Match(message) is { Success: true } dimension)
            _state = _state with { Dimension = dimension.Groups[1].Value };
        else if (clientThread && _state.Type == McStateType.Starting &&
                 (message is "Sound engine started" or "Sound engine started!" || message.StartsWith("Narrator library successfully loaded", StringComparison.Ordinal)))
            _state = _state with { Type = McStateType.Menu };

        return State != previous;
    }

    private void ProcessChat(string message)
    {
        var advancement = AdvancementRegex().Match(message);
        if (advancement.Success && advancement.Groups[1].Value == _state.Player)
        {
            _state = _state with { Achievement = advancement.Groups[2].Value };
            _achievementUntil = _time.GetUtcNow().AddSeconds(30);
        }
        var mode = GameModeRegex().Match(message);
        if (mode.Success) _state = _state with { Gamemode = GameMode(mode.Groups[1].Value) };
    }

    private static string? GameMode(string value) => value.Trim().TrimEnd('.').ToLowerInvariant() switch
    {
        "survival" or "survival mode" or "выживание" => "survival",
        "creative" or "creative mode" or "творческий" => "creative",
        "adventure" or "adventure mode" or "приключение" => "adventure",
        "spectator" or "spectator mode" or "наблюдатель" => "spectator",
        _ => null
    };

    private static bool IsDisconnect(string message) =>
        message.StartsWith("Disconnecting from server", StringComparison.Ordinal) ||
        message.StartsWith("Disconnected from server", StringComparison.Ordinal) ||
        message.StartsWith("Failed to connect to the server", StringComparison.Ordinal) ||
        message.StartsWith("Couldn't connect to server", StringComparison.Ordinal);

    [GeneratedRegex(@"^(?:\[\d{2}:\d{2}:\d{2}(?:[.,]\d+)?\]\s*)?\[(?<thread>[^\]\r\n]+?)/(?<level>INFO|WARN|ERROR|DEBUG)\](?:\s*\[[^\]\r\n]*\])?:\s*(?<message>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex LogRegex();

    [GeneratedRegex(@"^Connecting to (.+?),\s*(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ConnectRegex();

    [GeneratedRegex(@"^(?:\[Not Secure\] )?(\w{1,16}) (?:has made the advancement|has completed the challenge|has reached the goal|получил(?:а)? достижение|выполнил(?:а)? испытание|достиг(?:ла)? цели) \[(.+?)\]$", RegexOptions.CultureInvariant)]
    private static partial Regex AdvancementRegex();

    [GeneratedRegex(@"^(?:Set own game mode to|Установлен режим игры:) (.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex GameModeRegex();

    [GeneratedRegex(@"^Preparing level ""(.+?)""$", RegexOptions.CultureInvariant)]
    private static partial Regex WorldRegex();

    [GeneratedRegex(@"^Loaded \d+ advancements$", RegexOptions.CultureInvariant)]
    private static partial Regex LoadedAdvancementsRegex();

    [GeneratedRegex(@"^(?:Changing dimension to|Entering dimension) (?:minecraft:)?(overworld|the_nether|the_end)$", RegexOptions.CultureInvariant)]
    private static partial Regex DimensionRegex();
}

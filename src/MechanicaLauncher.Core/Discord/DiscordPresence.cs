using DiscordRPC;
using DiscordRPC.Logging;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Profiles;

namespace MechanicaLauncher.Core.Discord;

public sealed class DiscordPresence : IDisposable
{
    private const string AppId = "1487742480236544060";
    private DiscordRpcClient? _client;
    private McState _state = new(McStateType.Launcher);
    private string _mcVersion = "";
    private string _loaderName = "";
    private int _modCount;
    private string? _dimension;
    private string? _lastAchievement;

    public void Init()
    {
        try
        {
            _client = new DiscordRpcClient(AppId) { Logger = new ConsoleLogger(LogLevel.None) };
            _client.Initialize();
            SetLauncherPresence();
        }
        catch { }
    }

    public void SetInstance(GameInstance? inst, int modCount)
    {
        if (inst == null) return;
        _mcVersion = inst.McVersion;
        _loaderName = inst.Loader != LoaderType.None ? inst.Loader.ToString() : "Vanilla";
        _modCount = modCount;
    }

    public void ProcessLogLine(string line)
    {
        var newState = McLogParser.ParseLine(line);
        if (newState == null) return;

        if (newState.Dimension != null)
        {
            _dimension = newState.Dimension;
        }
        if (newState.Achievement != null)
        {
            _lastAchievement = newState.Achievement;
        }
        if (newState.Gamemode != null && _state.Type == McStateType.SinglePlayer)
        {
            _state = _state with { Gamemode = newState.Gamemode };
        }
        else if (newState.Dimension == null && newState.Achievement == null)
        {
            _state = newState;
        }

        UpdatePresence();
    }

    public void SetLauncherPresence()
    {
        _state = new McState(McStateType.Launcher);
        UpdatePresence();
    }

    public void SetMenuPresence()
    {
        _state = new McState(McStateType.Menu);
        UpdatePresence();
    }

    public void OnGameExit()
    {
        _state = new McState(McStateType.Launcher);
        UpdatePresence();
    }

    private void UpdatePresence()
    {
        if (_client == null || !_client.IsInitialized) return;

        try
        {
            var presence = new RichPresence
            {
                Assets = new Assets { LargeImageKey = "mechanica", LargeImageText = "Mechanica Launcher" }
            };

            var settings = LauncherSettings.Load();
            var stateInfo = $"{_mcVersion} · {_loaderName}";
            if (_modCount > 0 && settings.DiscordShowMods) stateInfo += $" · {_modCount} mods";

            switch (_state.Type)
            {
                case McStateType.Launcher:
                    presence.Details = "In launcher";
                    presence.State = "Idle";
                    break;

                case McStateType.Menu:
                    presence.Details = "In main menu";
                    presence.State = stateInfo;
                    break;

                case McStateType.SinglePlayer:
                    var spDetails = "Singleplayer";
                    if (_state.Gamemode != null) spDetails += $" · {_state.Gamemode}";
                    if (_dimension != null && settings.DiscordShowDimension) spDetails += $" · {_dimension}";
                    presence.Details = spDetails;
                    presence.State = (_lastAchievement != null && settings.DiscordShowAchievements)
                        ? $"🏆 {_lastAchievement}"
                        : stateInfo;
                    break;

                case McStateType.MultiPlayer:
                    presence.Details = settings.DiscordShowServer
                        ? $"Playing on {_state.Server}"
                        : "Multiplayer";
                    presence.State = stateInfo;
                    presence.Buttons =
                    [
                        new Button
                        {
                            Label = "Join Server",
                            Url = $"mechanica://connect?server={_state.Server}&port={_state.Port ?? 25565}&version={_mcVersion}"
                        },
                        new Button
                        {
                            Label = "Mechanica Launcher",
                            Url = "https://github.com/Ytin24/MechanicaLauncher"
                        }
                    ];
                    break;
            }

            if (_state.Type != McStateType.MultiPlayer)
            {
                presence.Buttons =
                [
                    new Button
                    {
                        Label = "Mechanica Launcher",
                        Url = "https://github.com/Ytin24/MechanicaLauncher"
                    }
                ];
            }

            _client.SetPresence(presence);
        }
        catch { }
    }

    public void Dispose()
    {
        try { _client?.Dispose(); } catch { }
    }
}

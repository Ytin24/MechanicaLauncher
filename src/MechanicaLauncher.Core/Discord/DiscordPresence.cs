using DiscordRPC;
using DiscordRPC.Logging;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Profiles;

namespace MechanicaLauncher.Core.Discord;

public sealed class DiscordPresence : IDisposable
{
    private const string AppId = "1487742480236544060";
    private DiscordRpcClient? _client;
    private readonly McStateMachine _sm = new();
    private string _mcVersion = "";
    private string _loaderName = "";
    private LoaderType _loader = LoaderType.None;
    private string _instanceName = "";
    private int _modCount;
    private DateTime? _sessionStart;

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
        _loader = inst.Loader;
        _loaderName = inst.Loader != LoaderType.None ? inst.Loader.ToString() : "Vanilla";
        _instanceName = inst.Name;
        _modCount = modCount;
        _sessionStart = DateTime.UtcNow;
    }

    public void ProcessLogLine(string line)
    {
        if (_sm.ProcessLine(line))
            UpdatePresence();
    }

    public void SetLauncherPresence()
    {
        _sm.Reset();
        UpdatePresence();
    }

    public void SetMenuPresence()
    {
        _sm.Reset();
        UpdatePresence();
    }

    public void OnGameExit()
    {
        _sm.Reset();
        _sessionStart = null;
        UpdatePresence();
    }

    private void UpdatePresence()
    {
        if (_client == null || !_client.IsInitialized) return;

        try
        {
            var loaderKey = _loader switch
            {
                LoaderType.Fabric   => "fabric",
                LoaderType.Quilt    => "quilt",
                LoaderType.Forge    => "forge",
                LoaderType.NeoForge => "neoforge",
                _                   => "vanilla",
            };

            var presence = new RichPresence
            {
                Assets = new Assets
                {
                    LargeImageKey = "mechanica",
                    LargeImageText = string.IsNullOrEmpty(_instanceName) ? "Mechanica Launcher" : _instanceName,
                    SmallImageKey = loaderKey,
                    SmallImageText = string.IsNullOrEmpty(_mcVersion) ? _loaderName : $"{_loaderName} · {_mcVersion}",
                }
            };

            // Elapsed playtime — set once on first state transition out of Launcher, kept across menu/world switches.
            if (_sessionStart.HasValue)
                presence.Timestamps = new Timestamps { Start = _sessionStart.Value };

            var settings = LauncherSettings.Load();
            var state = _sm.State;
            var stateInfo = $"{_mcVersion} · {_loaderName}";
            if (_modCount > 0 && settings.DiscordShowMods) stateInfo += $" · {_modCount} mods";

            switch (state.Type)
            {
                case McStateType.Launcher:
                    presence.Details = "In launcher";
                    presence.State = string.IsNullOrEmpty(_instanceName) ? "Idle" : $"Preparing {_instanceName}";
                    presence.Timestamps = null;
                    break;

                case McStateType.Menu:
                    presence.Details = "Main menu";
                    presence.State = stateInfo;
                    break;

                case McStateType.SinglePlayer:
                    var spDetails = !string.IsNullOrEmpty(state.World) ? $"World: {state.World}" : "Singleplayer";
                    if (state.Gamemode != null) spDetails += $" · {state.Gamemode}";
                    if (state.Dimension != null && settings.DiscordShowDimension) spDetails += $" · {state.Dimension}";
                    presence.Details = spDetails;
                    presence.State = (state.Achievement != null && settings.DiscordShowAchievements)
                        ? $"🏆 {state.Achievement}"
                        : stateInfo;
                    break;

                case McStateType.MultiPlayer:
                    presence.Details = settings.DiscordShowServer
                        ? $"On {state.Server}"
                        : "Multiplayer";
                    var mpState = stateInfo;
                    if (state.Dimension != null && settings.DiscordShowDimension) mpState += $" · {state.Dimension}";
                    presence.State = mpState;
                    presence.Buttons =
                    [
                        new Button
                        {
                            Label = "Join Server",
                            Url = $"mechanica://connect?server={state.Server}&port={state.Port}&version={_mcVersion}"
                        },
                        new Button
                        {
                            Label = "Mechanica Launcher",
                            Url = "https://github.com/Ytin24/MechanicaLauncher"
                        }
                    ];
                    break;
            }

            if (state.Type != McStateType.MultiPlayer)
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

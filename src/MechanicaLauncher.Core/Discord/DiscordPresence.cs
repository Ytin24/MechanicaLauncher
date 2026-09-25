using System.Diagnostics;
using DiscordRPC;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Profiles;

namespace MechanicaLauncher.Core.Discord;

public enum DiscordConnectionStatus { Disabled, Connecting, WaitingForDiscord, Connected, Error }

public sealed record DiscordPresenceSnapshot(DiscordConnectionStatus Status, string Details, string State,
    DateTimeOffset? StartedAt, string? Error);

public sealed class DiscordPresence : IDisposable
{
    public const string ApplicationId = "1487742480236544060";
    internal const string MinecraftImage = "https://raw.githubusercontent.com/Mojang/bedrock-samples/a3b394c507a6b11a3c6f61552e778ff5c4b89fd2/resource_pack/pack_icon.png";
    private readonly object _sync = new();
    private readonly TimeProvider _time;
    private readonly Func<DiscordRpcClient> _createClient;
    private readonly Timer? _timer;
    private readonly Dictionary<Guid, Session> _sessions = new();
    private DiscordRpcClient? _client;
    private PresenceOptions _options = new(false, true, true, true, true, "en");
    private DiscordConnectionStatus _status;
    private string? _error;
    private PresenceActivity? _lastSent;
    private DateTimeOffset _lastSentAt;
    private long _sequence;
    private bool _ready;
    private bool _force;
    private bool _disposed;
    private bool _stopping;
    private bool _clearConfirmed;
    private DateTimeOffset _stopDeadline;

    public DiscordPresence() : this(TimeProvider.System,
        () => new DiscordRpcClient(ApplicationId, autoEvents: false), true) { }

    internal DiscordPresence(TimeProvider time, Func<DiscordRpcClient> createClient, bool useTimer = false)
    {
        _time = time;
        _createClient = createClient;
        if (useTimer) _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public DiscordPresenceSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                var activity = BuildActivity();
                return new(_status, activity.Details, activity.State, activity.StartedAt, _error);
            }
        }
    }

    public void Configure(LauncherSettings settings, string language)
    {
        lock (_sync)
        {
            if (_disposed) return;
            var options = new PresenceOptions(settings.DiscordRpc, settings.DiscordShowServer,
                settings.DiscordShowDimension, settings.DiscordShowAchievements, settings.DiscordShowMods, language);
            var hideServer = _options.ShowServer && !options.ShowServer;
            if (_options != options) _force = true;
            _options = options;
            if (!options.Enabled) StopConnection();
            else if (hideServer && _client != null) StopConnection();
            else if (_client == null) Connect();
        }
        Tick();
    }

    public void Reconnect()
    {
        lock (_sync)
        {
            if (_disposed || !_options.Enabled) return;
            StopConnection();
        }
        Tick();
    }

    public Guid BeginPreparation(GameInstance instance)
    {
        lock (_sync)
        {
            if (_disposed) return Guid.Empty;
            var id = Guid.NewGuid();
            _sessions[id] = new(instance.Name, instance.McVersion, instance.Loader, ++_sequence, new McStateMachine(_time));
            _force = true;
            return id;
        }
    }

    public void GameStarted(Guid sessionId, int enabledModFiles)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(sessionId, out var session) || session.StartedAt.HasValue) return;
            session.StartedAt = _time.GetUtcNow();
            session.Order = ++_sequence;
            session.ModCount = Math.Max(0, enabledModFiles);
            _force = true;
        }
    }

    public void EndPreparation(Guid sessionId)
    {
        lock (_sync)
            if (_sessions.TryGetValue(sessionId, out var session) && session.StartedAt == null)
                EndSession(sessionId);
    }

    public void EndSession(Guid sessionId)
    {
        lock (_sync)
            if (_sessions.Remove(sessionId)) _force = true;
    }

    public void ProcessLogLine(Guid sessionId, string line)
    {
        lock (_sync)
            if (_sessions.TryGetValue(sessionId, out var session) && session.StartedAt.HasValue)
                session.State.ProcessLine(line);
    }

    internal void Tick()
    {
        lock (_sync)
        {
            if (_disposed || _client == null) return;
            try
            {
                // SDK callbacks run under this lock; stdout only updates session state.
                var activity = BuildActivity();
                var now = _time.GetUtcNow();
                if (!_ready && !_stopping && activity != _lastSent)
                {
                    _client.SetPresence(activity.ToPresence());
                    _lastSent = activity;
                }
                _client.Invoke();
                if (_stopping)
                {
                    if (_clearConfirmed || now >= _stopDeadline)
                    {
                        Disconnect();
                        if (_options.Enabled) Connect();
                    }
                    return;
                }
                if (!_ready) return;
                if (activity == _lastSent) { _force = false; return; }
                if (!_force && now - _lastSentAt < TimeSpan.FromSeconds(5)) return;
                _client.SetPresence(activity.ToPresence());
                _lastSent = activity;
                _lastSentAt = now;
                _force = false;
            }
            catch (Exception ex)
            {
                _status = DiscordConnectionStatus.Error;
                _error = PresenceActivity.Text(ex.Message, 300);
                Debug.WriteLine(ex);
            }
        }
    }

    private void Connect()
    {
        try
        {
            _status = DiscordConnectionStatus.Connecting;
            _error = null;
            var client = _createClient();
            _client = client;
            client.OnReady += (_, _) =>
            {
                _ready = true;
                if (!_stopping) _status = DiscordConnectionStatus.Connecting;
                _error = null;
                _lastSent = null;
                _force = true;
            };
            client.OnPresenceUpdate += (_, message) =>
            {
                if (_stopping) { if (message.Presence == null) _clearConfirmed = true; return; }
                if (message.Presence != null) { _status = DiscordConnectionStatus.Connected; _error = null; }
            };
            client.OnConnectionFailed += (_, _) => Waiting();
            client.OnClose += (_, _) => Waiting();
            client.OnError += (_, error) =>
            {
                if (!_stopping)
                {
                    _status = DiscordConnectionStatus.Error;
                    _error = PresenceActivity.Text($"{error.Code}: {error.Message}", 300);
                }
            };
            if (!client.Initialize()) throw new InvalidOperationException("Discord IPC initialization failed.");
        }
        catch (Exception ex)
        {
            Disconnect();
            _status = DiscordConnectionStatus.Error;
            _error = PresenceActivity.Text(ex.Message, 300);
            Debug.WriteLine(ex);
        }
    }

    private void Waiting()
    {
        _ready = false;
        if (!_stopping) _status = DiscordConnectionStatus.WaitingForDiscord;
        _error = null;
    }

    private void StopConnection()
    {
        _status = _options.Enabled ? DiscordConnectionStatus.Connecting : DiscordConnectionStatus.Disabled;
        _error = null;
        if (_stopping) return;
        if (_client == null || !_ready)
        {
            Disconnect();
            if (_options.Enabled) Connect();
            return;
        }
        _stopping = true;
        _clearConfirmed = false;
        _stopDeadline = _time.GetUtcNow().AddSeconds(3);
        // The SDK can discard queued commands during disposal. Wait for the clear response first.
        _client.ClearPresence();
    }

    private void Disconnect()
    {
        var client = _client;
        _client = null;
        _ready = false;
        _stopping = false;
        _lastSent = null;
        _status = DiscordConnectionStatus.Disabled;
        _error = null;
        if (client == null) return;
        try { if (client.IsInitialized) client.ClearPresence(); }
        catch (Exception ex) { Debug.WriteLine(ex); }
        finally
        {
            try { client.ShutdownOnly = false; client.Dispose(); }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }
    }

    private PresenceActivity BuildActivity()
    {
        var session = _sessions.Values.OrderByDescending(s => s.StartedAt.HasValue).ThenByDescending(s => s.Order).FirstOrDefault();
        var state = session == null ? new(McStateType.Launcher) : session.StartedAt == null ? new(McStateType.Preparing) : session.State.State;
        if (state.Type == McStateType.Starting && _time.GetUtcNow() - session!.StartedAt >= TimeSpan.FromSeconds(30))
            state = state with { Type = McStateType.Running };
        return PresenceActivity.Create(session?.Name, session?.Version, session?.Loader ?? LoaderType.None,
            session?.ModCount ?? 0, session?.StartedAt, state, _options);
    }

    internal RichPresence BuildPresence()
    {
        lock (_sync) return BuildActivity().ToPresence();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
            _sessions.Clear();
        }
        _timer?.Dispose();
    }

    private sealed class Session(string name, string version, LoaderType loader, long order, McStateMachine state)
    {
        public string Name { get; } = name;
        public string Version { get; } = version;
        public LoaderType Loader { get; } = loader;
        public long Order { get; set; } = order;
        public McStateMachine State { get; } = state;
        public DateTimeOffset? StartedAt { get; set; }
        public int ModCount { get; set; }
    }
}

internal sealed record PresenceOptions(bool Enabled, bool ShowServer, bool ShowDimension, bool ShowAchievements, bool ShowMods, string Language);

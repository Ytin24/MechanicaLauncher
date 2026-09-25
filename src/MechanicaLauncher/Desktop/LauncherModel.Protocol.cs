using MechanicaLauncher.Core.Config;

namespace MechanicaLauncher.Desktop;

public sealed partial class LauncherModel
{
    public Action? ShowRequested { get; set; }
    private string? deferredCommand;
    private bool restoreEventPending, resumingCommand;
    internal void PrepareStartupCommand(string command)
    {
        BeginTLauncherCheck(); deferredCommand = command; restoreEventPending = command == "SHOW";
    }
    internal async Task ResumePendingCommand()
    {
        if (TLauncherBlocked || DialogOpen || resumingCommand) return;
        resumingCommand = true;
        try
        {
            if (restoreEventPending) { restoreEventPending = false; await RestoreEvent(); }
            if (TLauncherBlocked || deferredCommand == null) return;
            string command = deferredCommand; deferredCommand = null;
            await HandleCommand(command);
        }
        finally { resumingCommand = false; }
    }
    internal async Task RestoreEvent()
    {
        if (TLauncherBlocked) { restoreEventPending = true; return; }
        if (string.IsNullOrWhiteSpace(Settings.ActiveEventUrl)) return;
        await Sessions.Events.LoadFromUrlAsync(Settings.ActiveEventUrl);
        if (!AllowLog) ShowLog = false;
        Changed();
    }
    internal async Task HandleCommand(string command)
    {
        ShowRequested?.Invoke();
        if (TLauncherBlocked)
        {
            if (command != "SHOW") deferredCommand = command;
            return;
        }
        if (command == "SHOW") return;
        if (command.StartsWith("MRPACK|", StringComparison.Ordinal)) { ImportPack(command[7..]); return; }
        if (command.StartsWith("EVENT|", StringComparison.Ordinal))
        {
            string url = command[6..].Trim();
            var config = await Sessions.Events.LoadFromUrlAsync(url);
            if (TLauncherBlocked) { deferredCommand = command; return; }
            Settings.ActiveEventUrl = url; Settings.Save();
            if (!AllowLog) ShowLog = false;
            if (config.Minecraft != null)
            {
                var loader = Enum.TryParse<LoaderType>(config.Minecraft.Loader, true, out var type) ? type : LoaderType.None;
                var instance = Instances.GetAllInstances().FirstOrDefault(i => i.McVersion == config.Minecraft.Version && i.Loader == loader)
                    ?? Instances.CreateInstance(config.Name, config.Minecraft.Version, loader, config.Minecraft.LoaderVersion);
                SetInstance(instance.Id);
                if (config.Mods != null)
                    Sessions.Enqueue(config.Name, instance.Id, async _ =>
                    {
                        var result = await new ModSyncer().SyncAsync(config, Path.Combine(Instances.GetGameDir(instance.Id), "mods"));
                        if (result.HasFailures) throw new InvalidOperationException(string.Join(", ", result.FailedMods));
                    });
            }
            Home(); return;
        }
        var parts = command.Split('|');
        if (parts.Length != 3 || !int.TryParse(parts[1], out int port) || port is < 1 or > 65535) return;
        var match = Instances.GetAllInstances().FirstOrDefault(i => i.McVersion == parts[2]);
        if (match == null)
        {
            RequireEventPermission(AllowCreate);
            if (!await Confirm(T("Создать сборку для сервера?", "Create an instance for this server?"), $"{parts[0]} · Minecraft {parts[2]}", T("Создать", "Create"))) return;
            match = Instances.CreateInstance(parts[0], parts[2]);
        }
        if (Sessions.IsRunning(match.Id))
        {
            Notice(T("Minecraft уже запущен. Подключись к серверу из игры: ", "Minecraft is already running. Connect in game: ") + parts[0] + ":" + port);
            return;
        }
        SetInstance(match.Id); Home(); await Launch(match, parts[0], port);
    }
}

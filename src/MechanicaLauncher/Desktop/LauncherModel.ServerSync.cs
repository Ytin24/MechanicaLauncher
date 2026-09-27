using MechanicaLauncher.Core.Servers;

namespace MechanicaLauncher.Desktop;

public sealed partial class LauncherModel
{
    public Action? AttentionRequested { get; set; }

    private void ConnectServer(FavoriteServer server) => Run(async () =>
    {
        if (disposed || DialogOpen || TLauncherBlocked) return;
        var current = new FavoriteServers().Load().FirstOrDefault(s => s.Id == server.Id)
            ?? throw new InvalidOperationException(T("Сервер удалён из избранного.", "The server was removed from favorites."));
        var instance = Instances.GetInstance(current.InstanceId)
            ?? throw new InvalidOperationException(T("Сборка сервера удалена. Выбери другую.", "Server instance is missing. Choose another."));
        SetInstance(instance.Id); Home();
        await Sessions.LaunchServerAsync(current, ConfirmCompatibilityOnUi);
    });

    internal Task<bool> ConfirmCompatibilityOnUi(string text) => ConfirmGameActionOnUi(() =>
        Confirm(L("compat.launch"), text, L("compat.continue")));

    internal Task<bool> ConfirmServerSyncOnUi(FavoriteServer server, ServerSyncPlan plan, CancellationToken token = default) => ConfirmGameActionOnUi(() =>
    {
        string[] summary = new[]
        {
            (ServerSyncChangeKind.Add, T("Добавить", "Add")),
            (ServerSyncChangeKind.Replace, T("Обновить", "Update")),
            (ServerSyncChangeKind.Remove, T("Убрать", "Remove"))
        }.Select(change => (change.Item2, Count: plan.Changes.Count(item => item.Kind == change.Item1)))
            .Where(change => change.Count > 0).Select(change => change.Item1 + ": " + change.Count).ToArray();
        string body = server.Name + " · " + (Instances.GetInstance(server.InstanceId)?.Name ?? InstanceName) + "\n" + string.Join(" · ", summary);
        if (plan.DownloadBytes > 0) body += "\n" + T("Скачать: ", "Download: ") + Size(plan.DownloadBytes);
        if (Sessions.IsRunning(server.InstanceId)) body += "\n" + T("Minecraft перезапустится.", "Minecraft will restart.");
        return Confirm(T("Обновить моды сервера?", "Update server mods?"), body, T("Обновить", "Update"));
    }, token);

    private async Task<bool> ConfirmGameActionOnUi(Func<Task<bool>> confirm, CancellationToken token = default)
    {
        if (disposed) return false;
        CancellationToken lifetime;
        try { lifetime = tlauncherLifetime.Token; }
        catch (ObjectDisposedException) { return false; }
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int generation = -1;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime, token);
        void CloseOwnDialog()
        {
            if (!disposed && generation >= 0 && DialogOpen && DialogGeneration == generation) CloseDialog();
        }
        using var registration = cancellation.Token.Register(() =>
        {
            completion.TrySetResult(false);
            try { Dispatch(CloseOwnDialog); }
            catch (ObjectDisposedException) { }
        });
        async Task Open()
        {
            if (completion.Task.IsCompleted || disposed || DialogOpen || TLauncherBlocked) { completion.TrySetResult(false); return; }
            try
            {
                AttentionRequested?.Invoke();
                if (completion.Task.IsCompleted) return;
                var prompt = confirm();
                generation = DialogGeneration;
                if (completion.Task.IsCompleted) CloseOwnDialog();
                bool accepted = await prompt;
                completion.TrySetResult(accepted && !disposed && !TLauncherBlocked);
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }
        try { if (!completion.Task.IsCompleted) Dispatch(() => _ = Open()); }
        catch (ObjectDisposedException) { completion.TrySetResult(false); }
        return await completion.Task.ConfigureAwait(false);
    }
}

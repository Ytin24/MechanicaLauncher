using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Servers;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace MechanicaLauncher.Views;

public sealed partial class ServersPage : Page
{
    private readonly FavoriteServers _store = new();
    private readonly InstanceManager _instances = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, ServerStatus> _statuses = [];
    public ServersPage()
    {
        InitializeComponent();
        TitleText.Text = App.L("nav.servers");
        HintText.Text = App.L("servers.hint");
        AddButton.Content = App.L("servers.add");
    }
    protected override void OnNavigatedTo(NavigationEventArgs e) { base.OnNavigatedTo(e); Render(); }
    protected override void OnNavigatedFrom(NavigationEventArgs e) { _lifetime.Cancel(); base.OnNavigatedFrom(e); }
    private async void Add_Click(object sender, RoutedEventArgs e) => await EditAsync(null);
    private void Render()
    {
        Rows.Children.Clear();
        var servers = _store.Load();
        if (servers.Count == 0) Rows.Children.Add(Text(App.L("servers.empty"), true));
        foreach (var server in servers)
        {
            var instance = _instances.GetInstance(server.InstanceId);
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = server.Name, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(Text(server.Address + " · " + (instance == null ? App.L("servers.missing_instance") : $"{instance.Name} · {instance.McVersion} · {instance.Loader}"), true));
            var status = Text(App.L("servers.unchecked"), true);
            void SetStatus(ServerStatus result) => status.Text = $"{result.Online} / {result.Maximum} · {result.LatencyMs} ms · {result.Version}\n{result.Description}";
            if (_statuses.TryGetValue(server.Id, out var cached)) SetStatus(cached);
            panel.Children.Add(status);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var connect = Button(App.L("servers.connect"));
            connect.Style = (Style)Application.Current.Resources["AccentSmallButton"];
            connect.IsEnabled = instance != null;
            connect.Click += (_, _) =>
            {
                if (App.IsInstanceBusy(server.InstanceId) || App.LaunchPreparationGate.CurrentCount == 0)
                { ShowError(App.L("feature.busy")); return; }
                if (App.MainWindow is MainWindow main) main.LaunchServer(server.InstanceId, server.Host, server.Port);
            };
            var ping = Button(App.L("servers.status"));
            ping.Click += async (_, _) =>
            {
                ping.IsEnabled = false;
                status.Text = App.L("servers.checking");
                try
                {
                    var result = await new ServerStatusClient().QueryAsync(server.Host, server.Port, _lifetime.Token);
                    if (_lifetime.IsCancellationRequested) return;
                    _statuses[server.Id] = result;
                    SetStatus(result);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
                catch (Exception) { status.Text = App.L("servers.offline"); }
                finally { ping.IsEnabled = true; }
            };
            var edit = Button(App.L("servers.edit"));
            edit.Click += async (_, _) => await EditAsync(server);
            var remove = Button(App.L("servers.remove"));
            remove.Click += async (_, _) =>
            {
                var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = App.L("servers.remove"), Content = App.L("servers.remove_confirm", server.Name),
                    PrimaryButtonText = App.L("servers.remove"), CloseButtonText = App.L("feature.cancel"), DefaultButton = ContentDialogButton.Close };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                try { _store.Save(_store.Load().Where(s => s.Id != server.Id)); Render(); }
                catch (Exception ex) { ShowError(ex.Message); }
            };
            actions.Children.Add(connect); actions.Children.Add(ping); actions.Children.Add(edit); actions.Children.Add(remove);
            panel.Children.Add(actions);
            Rows.Children.Add(new Border { Style = (Style)Application.Current.Resources["CardPanelWide"], Child = panel });
        }
    }
    private async Task EditAsync(FavoriteServer? server)
    {
        var name = new TextBox { Header = App.L("servers.name"), Text = server?.Name ?? "", MaxLength = 100 };
        var address = new TextBox { Header = App.L("servers.address"), Text = server?.Address ?? "", PlaceholderText = "play.example.org:25565", MaxLength = 270 };
        var instancePicker = new ComboBox { Header = App.L("servers.instance"), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var instance in _instances.GetAllInstances())
        {
            var item = new ComboBoxItem { Content = $"{instance.Name} · {instance.McVersion} · {instance.Loader}", Tag = instance.Id };
            instancePicker.Items.Add(item);
            if (instance.Id == (server?.InstanceId ?? App.Settings.SelectedInstanceId)) instancePicker.SelectedItem = item;
        }
        var hint = Text(App.L("servers.invalid"), true);
        var error = Text("");
        var content = new StackPanel { Spacing = 12, MinWidth = 390, Children = { name, address, instancePicker, hint, error } };
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = App.L(server == null ? "servers.add" : "servers.edit"), Content = content,
            PrimaryButtonText = App.L("feature.save"), CloseButtonText = App.L("feature.cancel"), DefaultButton = ContentDialogButton.Primary };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || !FavoriteServers.TryParseAddress(address.Text, out var host, out var port) ||
                (instancePicker.SelectedItem as ComboBoxItem)?.Tag is not string id)
            { args.Cancel = true; error.Text = App.L("servers.invalid"); return; }
            try
            {
                var updated = new FavoriteServer(server?.Id ?? Guid.NewGuid().ToString("N"), name.Text.Trim(), host, port, id);
                _store.Save(_store.Load().Where(s => s.Id != updated.Id).Append(updated));
                _statuses.Remove(updated.Id);
            }
            catch (Exception ex) { args.Cancel = true; error.Text = App.L("feature.error", ex.Message); }
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) Render();
    }
    private static Button Button(string title) => new() { Content = title, Style = (Style)Application.Current.Resources["SmallButton"] };
    private static TextBlock Text(string title, bool subtle = false) => new() { Text = title, TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources[subtle ? "SubtleBrush" : "PrimaryBrush"] };
    private void ShowError(string error) { Notice.Message = error; Notice.IsOpen = true; }
}

using MechanicaLauncher.Core.IO;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace MechanicaLauncher.Views;

public sealed partial class DownloadsPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<Guid, Action> _updates = [];
    private int _jobCount = -1;

    public DownloadsPage()
    {
        InitializeComponent();
        TitleText.Text = App.L("nav.downloads");
        HintText.Text = App.L("downloads.hint");
        EmptyText.Text = App.L("downloads.empty");
        foreach (var key in new[] { "all", "active", "errors" }) Filter.Items.Add(App.L("downloads." + key));
        Filter.SelectedIndex = 0;
        _timer.Tick += (_, _) => Refresh();
    }
    protected override void OnNavigatedTo(NavigationEventArgs e) { base.OnNavigatedTo(e); Refresh(); _timer.Start(); }
    protected override void OnNavigatedFrom(NavigationEventArgs e) { _timer.Stop(); base.OnNavigatedFrom(e); }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var jobs = App.Downloads.Jobs;
        if (_jobCount != jobs.Count)
        {
            _jobCount = jobs.Count;
            Rows.Children.Clear();
            Rows.Children.Add(EmptyText);
            _updates.Clear();
            foreach (var job in jobs.Reverse()) AddRow(job);
        }
        foreach (var update in _updates.Values) update();
        EmptyText.Visibility = Rows.Children.OfType<Border>().Any(r => r.Visibility == Visibility.Visible) ? Visibility.Collapsed : Visibility.Visible;
    }
    private void AddRow(DownloadJob job)
    {
        var panel = new StackPanel { Spacing = 10 };
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var title = new TextBlock { Text = job.Title, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(title);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        var cancel = new Button { Content = App.L("feature.cancel"), Style = (Style)Application.Current.Resources["SmallButton"] };
        cancel.Click += (_, _) => { job.Cancel(); Refresh(); };
        var retry = new Button { Content = App.L("feature.retry"), Style = (Style)Application.Current.Resources["SmallButton"] };
        retry.Click += (_, _) => { App.Downloads.Retry(job); Refresh(); };
        actions.Children.Add(cancel);
        actions.Children.Add(retry);
        var open = new Button { Content = App.L("feature.open"), Style = (Style)Application.Current.Resources["SmallButton"] };
        open.Click += (_, _) =>
        {
            var id = job.Snapshot().InstanceId;
            if (id != null && App.MainWindow is MainWindow main) main.ShowInstanceDetails(id);
        };
        actions.Children.Add(open);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["SubtleBrush"] };
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var files = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var expander = new Expander { Content = files, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(header);
        panel.Children.Add(status);
        panel.Children.Add(progress);
        panel.Children.Add(error);
        panel.Children.Add(expander);
        var card = new Border { Style = (Style)Application.Current.Resources["CardPanelWide"], Child = panel };
        Rows.Children.Add(card);
        _updates[job.Id] = () =>
        {
            var snapshot = job.Snapshot();
            bool active = snapshot.State is DownloadState.Queued or DownloadState.Running;
            card.Visibility = Filter.SelectedIndex switch
            {
                1 when !active => Visibility.Collapsed,
                2 when snapshot.State is not (DownloadState.Failed or DownloadState.Cancelled) => Visibility.Collapsed,
                _ => Visibility.Visible
            };
            status.Text = App.L("downloads." + snapshot.State.ToString().ToLowerInvariant()) +
                (snapshot.Received > 0 ? $" · {Size(snapshot.Received)}" + (snapshot.Total > 0 ? $" / {Size(snapshot.Total)}" : "") : "") +
                (snapshot.BytesPerSecond > 0 ? $" · {Size(snapshot.BytesPerSecond)}/s" : "");
            progress.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            progress.IsIndeterminate = snapshot.Total == 0;
            progress.Value = snapshot.Total > 0 ? Math.Min(100, 100d * snapshot.Received / snapshot.Total) : 0;
            cancel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            cancel.IsEnabled = !job.IsCancellationRequested;
            retry.Visibility = snapshot.CanRetry ? Visibility.Visible : Visibility.Collapsed;
            open.Visibility = snapshot.State == DownloadState.Completed && snapshot.InstanceId != null ? Visibility.Visible : Visibility.Collapsed;
            error.Text = snapshot.Error ?? "";
            error.Visibility = string.IsNullOrEmpty(error.Text) ? Visibility.Collapsed : Visibility.Visible;
            expander.Header = App.L("downloads.files", snapshot.Files.Count(f => f.Complete));
            expander.Visibility = snapshot.Files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (expander.IsExpanded)
                files.Text = string.Join("\n", snapshot.Files.OrderBy(f => f.Complete).Take(100).Select(f =>
                    $"{f.Name} · {Size(f.Received)}" + (f.Complete ? " · " + App.L("downloads.completed") : ""))) +
                    (snapshot.Files.Count > 100 ? $"\n… +{snapshot.Files.Count - 100}" : "");
        };
    }
    internal static string Size(double bytes) => bytes >= 1_048_576 ? $"{bytes / 1_048_576:0.0} MB" : bytes >= 1024 ? $"{bytes / 1024:0.0} KB" : $"{bytes:0} B";
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using MechanicaLauncher.Core.Game;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Helpers;

namespace MechanicaLauncher.Views;

public sealed partial class InstancesPage : Page
{
    private static Core.Profiles.LauncherSettings S => App.Settings;
    private readonly InstanceManager _im = new();

    public InstancesPage()
    {
        this.InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        LoadInstances();
    }

    private void LoadInstances()
    {
        InstancesList.Children.Clear();
        var instances = _im.GetAllInstances();

        if (instances.Count == 0)
        {
            InstancesList.Children.Add(new TextBlock
            {
                Text = "No instances yet. Create one to get started!",
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 14,
                Margin = new Thickness(0, 40, 0, 0)
            });
            return;
        }

        int delay = 0;
        foreach (var inst in instances)
        {
            var card = CreateCard(inst);
            InstancesList.Children.Add(card);
            AnimationHelper.SlideIn(card, delay);
            delay += 50;
        }
    }

    private Border CreateCard(GameInstance inst)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20),
            MinHeight = 72,
        };

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };

        var iconBorder = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x2D, 0x2D, 0x2D)),
            CornerRadius = new CornerRadius(8),
            Width = 48, Height = 48,
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconColor = inst.Loader switch
        {
            LoaderType.Fabric => Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50),
            LoaderType.Forge => Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x98, 0x00),
            _ => Windows.UI.Color.FromArgb(0xFF, 0x88, 0x88, 0x88),
        };
        iconBorder.Child = new FontIcon
        {
            Glyph = "\uE74C", FontSize = 20,
            Foreground = new SolidColorBrush(iconColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var info = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Spacing = 4 };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        titleRow.Children.Add(new TextBlock { Text = inst.Name, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

        if (inst.Loader != LoaderType.None)
        {
            var badge = new Border
            {
                Background = new SolidColorBrush(iconColor),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Center
            };
            badge.Child = new TextBlock
            {
                Text = inst.Loader.ToString(),
                FontSize = 10, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            titleRow.Children.Add(badge);
        }

        info.Children.Add(titleRow);

        var isRunning = App.RunningInstances.TryGetValue(inst.Id, out var proc) && !proc.HasExited;

        if (isRunning)
        {
            var runBadge = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x21, 0x96, 0xF3)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Center
            };
            runBadge.Child = new TextBlock { Text = "Running", FontSize = 10, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            titleRow.Children.Add(runBadge);
        }

        var sub = $"{inst.McVersion}";
        if (inst.LastPlayed.HasValue)
            sub += $"  ·  Last played {inst.LastPlayed.Value:MMM dd}";
        info.Children.Add(new TextBlock { Text = sub, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), FontSize = 12 });

        Grid.SetColumn(info, 1);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

        var selectBtn = new Button
        {
            Content = S.SelectedInstanceId == inst.Id ? "Selected" : "Select",
            FontSize = 13, Padding = new Thickness(16, 6, 16, 6),
            MinWidth = 72, MinHeight = 32, CornerRadius = new CornerRadius(6),
            Tag = inst.Id
        };
        if (S.SelectedInstanceId == inst.Id)
        {
            selectBtn.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50));
            selectBtn.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        }
        selectBtn.Click += SelectInstance_Click;

        var deleteBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE74D", FontSize = 14 },
            FontSize = 13, Padding = new Thickness(8, 6, 8, 6),
            MinHeight = 32, CornerRadius = new CornerRadius(6),
            Tag = inst.Id
        };
        deleteBtn.Click += DeleteInstance_Click;

        var editBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE70F", FontSize = 14 },
            FontSize = 13, Padding = new Thickness(8, 6, 8, 6),
            MinHeight = 32, CornerRadius = new CornerRadius(6),
            Tag = inst.Id
        };
        editBtn.Click += EditInstance_Click;

        buttons.Children.Add(selectBtn);
        buttons.Children.Add(editBtn);
        buttons.Children.Add(deleteBtn);
        Grid.SetColumn(buttons, 2);

        grid.Children.Add(iconBorder);
        grid.Children.Add(info);
        grid.Children.Add(buttons);
        card.Child = grid;
        return card;
    }

    private void SelectInstance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            S.SelectedInstanceId = id;
            S.Save();
            LoadInstances();
        }
    }

    private async void DeleteInstance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            var dialog = new ContentDialog
            {
                Title = "Delete Instance",
                Content = "This will delete all mods, saves, and configs for this instance.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                try
                {
                    _im.DeleteInstance(id);
                }
                catch (Exception ex)
                {
                    await new ContentDialog
                    {
                        Title = "Error",
                        Content = $"Could not delete: {ex.Message}",
                        CloseButtonText = "OK",
                        XamlRoot = this.XamlRoot
                    }.ShowAsync();
                    return;
                }
                if (S.SelectedInstanceId == id)
                {
                    S.SelectedInstanceId = null;
                    S.Save();
                }
                LoadInstances();
            }
        }
    }

    private async void EditInstance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;
        var inst = _im.GetInstance(id);
        if (inst == null) return;

        var nameBox = new TextBox { Text = inst.Name, MinHeight = 36 };
        var minMemSlider = new Slider { Minimum = 512, Maximum = 16384, Value = inst.MinMemoryMb, StepFrequency = 512, SnapsTo = Microsoft.UI.Xaml.Controls.Primitives.SliderSnapsTo.StepValues };
        var maxMemSlider = new Slider { Minimum = 512, Maximum = 16384, Value = inst.MaxMemoryMb, StepFrequency = 512, SnapsTo = Microsoft.UI.Xaml.Controls.Primitives.SliderSnapsTo.StepValues };
        var minMemLabel = new TextBlock { Text = $"Min Memory: {inst.MinMemoryMb} MB", FontSize = 13 };
        var maxMemLabel = new TextBlock { Text = $"Max Memory: {inst.MaxMemoryMb} MB", FontSize = 13 };
        var jvmBox = new TextBox { Text = inst.JvmArgs, PlaceholderText = "-XX:+UseG1GC", MinHeight = 36 };
        var widthBox = new TextBox { Text = inst.WindowWidth.ToString(), MinHeight = 36, MinWidth = 80 };
        var heightBox = new TextBox { Text = inst.WindowHeight.ToString(), MinHeight = 36, MinWidth = 80 };

        minMemSlider.ValueChanged += (_, args) => minMemLabel.Text = $"Min Memory: {(int)args.NewValue} MB";
        maxMemSlider.ValueChanged += (_, args) => maxMemLabel.Text = $"Max Memory: {(int)args.NewValue} MB";

        var content = new StackPanel
        {
            Spacing = 10, MinWidth = 360,
            Children =
            {
                new TextBlock { Text = "Name" }, nameBox,
                new TextBlock { Text = $"Version: {inst.McVersion}  ·  {inst.Loader}{(inst.LoaderVersion != null ? $" {inst.LoaderVersion}" : "")}", Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), FontSize = 13 },
                minMemLabel, minMemSlider,
                maxMemLabel, maxMemSlider,
                new TextBlock { Text = "JVM Arguments" }, jvmBox,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
                {
                    new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "Width", FontSize = 12 }, widthBox } },
                    new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 10) },
                    new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "Height", FontSize = 12 }, heightBox } }
                }}
            }
        };

        var dialog = new ContentDialog
        {
            Title = "Edit Instance",
            Content = new ScrollViewer { Content = content, MaxHeight = 500 },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        inst.Name = nameBox.Text?.Trim() ?? inst.Name;
        inst.MinMemoryMb = (int)minMemSlider.Value;
        inst.MaxMemoryMb = (int)maxMemSlider.Value;
        inst.JvmArgs = jvmBox.Text ?? "";
        if (int.TryParse(widthBox.Text, out var w)) inst.WindowWidth = w;
        if (int.TryParse(heightBox.Text, out var h)) inst.WindowHeight = h;

        _im.SaveInstance(inst);
        LoadInstances();
    }

    private async void NewInstance_Click(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { PlaceholderText = "Instance name", MinHeight = 36 };
        var versionBox = new ComboBox { PlaceholderText = "Loading...", MinWidth = 280, MinHeight = 36, IsEnabled = false };
        var loaderBox = new ComboBox { MinWidth = 280, MinHeight = 36 };
        loaderBox.Items.Add(new ComboBoxItem { Content = "None", Tag = "None" });
        loaderBox.Items.Add(new ComboBoxItem { Content = "Fabric", Tag = "Fabric" });
        loaderBox.Items.Add(new ComboBoxItem { Content = "Quilt", Tag = "Quilt" });
        loaderBox.Items.Add(new ComboBoxItem { Content = "Forge", Tag = "Forge" });
        loaderBox.SelectedIndex = 0;

        var content = new StackPanel
        {
            Spacing = 12, MinWidth = 320,
            Children = {
                new TextBlock { Text = "Name" }, nameBox,
                new TextBlock { Text = "Minecraft Version" }, versionBox,
                new TextBlock { Text = "Mod Loader" }, loaderBox
            }
        };

        var dialog = new ContentDialog
        {
            Title = "New Instance",
            Content = content,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };

        _ = Task.Run(async () =>
        {
            try
            {
                var vm = new VersionManager(_im.SharedDir);
                var manifest = await vm.GetManifestAsync();
                var versions = manifest.Versions
                    .Where(v => v.Type == "release")
                    .ToList();

                DispatcherQueue.TryEnqueue(() =>
                {
                    versionBox.Items.Clear();
                    foreach (var v in versions)
                        versionBox.Items.Add(new ComboBoxItem { Content = v.Id, Tag = v.Id });
                    if (versionBox.Items.Count > 0) versionBox.SelectedIndex = 0;
                    versionBox.PlaceholderText = "Select version";
                    versionBox.IsEnabled = true;
                });
            }
            catch { }
        });

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var name = nameBox.Text?.Trim();
        var mcVersion = (versionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var loaderStr = (loaderBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(mcVersion)) return;

        var loader = loaderStr switch
        {
            "Fabric" => LoaderType.Fabric,
            "Quilt" => LoaderType.Quilt,
            "Forge" => LoaderType.Forge,
            _ => LoaderType.None
        };
        string? loaderVersion = null;

        if (loader == LoaderType.Fabric)
        {
            try
            {
                var fi = new FabricInstaller(_im.SharedDir, "");
                var versions = await fi.GetLoaderVersionsAsync(mcVersion);
                loaderVersion = versions.FirstOrDefault(v => v.Stable)?.Version ?? versions.FirstOrDefault()?.Version;
            }
            catch { }
        }
        else if (loader == LoaderType.Quilt)
        {
            try
            {
                var qi = new QuiltInstaller(_im.SharedDir, "");
                var versions = await qi.GetLoaderVersionsAsync(mcVersion);
                loaderVersion = versions.FirstOrDefault();
            }
            catch { }
        }

        var instance = _im.CreateInstance(name, mcVersion, loader, loaderVersion);
        var gameDir = _im.GetGameDir(instance.Id);

        try
        {
            if (loader == LoaderType.Fabric && loaderVersion != null)
                await new FabricInstaller(_im.SharedDir, gameDir).InstallAsync(mcVersion, loaderVersion);
            else if (loader == LoaderType.Quilt && loaderVersion != null)
                await new QuiltInstaller(_im.SharedDir, gameDir).InstallAsync(mcVersion, loaderVersion);
        }
        catch { }

        S.SelectedInstanceId = instance.Id;
        S.Save();
        LoadInstances();
    }
}

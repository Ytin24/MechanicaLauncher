using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MechanicaLauncher.Helpers;

namespace MechanicaLauncher.Views;

public sealed partial class SettingsPage : Page
{
    private static Core.Profiles.LauncherSettings S => App.Settings;
    private bool _loading;
    private int _aboutTaps;
    private readonly Random _rng = new();

    private static readonly string[] CatFacts =
    [
        "Cats sleep 12-16 hours a day. They're the original AFK players.",
        "A group of cats is called a 'clowder'. A group of kittens is a 'kindle'.",
        "Cats can rotate their ears 180 degrees. Better surround sound than any headset.",
        "Ancient Egyptians shaved their eyebrows to mourn the death of their cats.",
        "A cat's purr vibrates at 25-150 Hz, frequencies that promote bone healing.",
        "Cats have over 20 vocalizations, including the trill, chirp, and chatter.",
        "The first cat in space was Felicette, a French cat launched in 1963.",
        "Cats spend 30-50% of their day grooming. Self-care kings and queens.",
        "A cat can jump up to 6 times its length. That's basically flying.",
        "Nikola Tesla was inspired to study electricity after his cat Macak gave him a static shock.",
    ];

    private static readonly string[] McFacts =
    [
        "The Creeper was created by accident when Notch messed up the pig model.",
        "Minecraft's world is 8x the surface area of Earth.",
        "The Enderman's sounds are reversed and distorted human speech saying 'hi' and 'what's up'.",
        "Ghast sounds are made by C418's cat.",
        "The Far Lands generate at X/Z 12,550,824 in Beta 1.7.3.",
        "Minecraft was originally called 'Cave Game'.",
        "A Minecraft day is exactly 20 minutes. Night is 7 minutes.",
        "The Wither was the first boss to be added by the community's suggestion.",
        "Diamonds are most common at Y=-59 in modern versions.",
        "The painting 'Skull on Fire' is based on a real painting by Kristoffer Zetterstrand.",
        "Herobrine was never in the game. Ever. We think.",
        "Notch coded the first version of Minecraft in 6 days.",
        "The mushroom biome was added so there'd be a place with no mob spawns.",
        "Baby zombies are faster than adult zombies. And scarier.",
    ];

    private static readonly string[] Splashes =
    [
        "Also try Terraria!", "Woo, /give!", "Reticulating splines...",
        "Now with extra cats!", "Contains 100% recycled pixels",
        "Not a Prism!", "Blocks all the way down",
        "sudo rm -rf /boredom", "git push --force-with-cats",
        "NullReferenceException in fun.cs", "async all the things!",
        "Powered by mass caffeine consumption", "Mass production of fun!",
    ];

    public SettingsPage()
    {
        this.InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _loading = true;
        ThemeSelector.SelectedIndex = S.Theme switch
        {
            "Dark" => 0,
            "Light" => 1,
            _ => 2
        };
        CloseOnLaunchToggle.IsOn = S.CloseOnLaunch;
        ShowSnapshotsToggle.IsOn = S.ShowSnapshots;
        _loading = false;
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeSelector?.SelectedItem is not ComboBoxItem item) return;
        var theme = item.Content?.ToString() ?? "Dark";
        S.Theme = theme;
        S.Save();

        if (App.MainWindow?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }
    }

    private void CloseOnLaunch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.CloseOnLaunch = CloseOnLaunchToggle.IsOn;
        S.Save();
    }

    private void ShowSnapshots_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.ShowSnapshots = ShowSnapshotsToggle.IsOn;
        S.Save();
    }

    private void About_Tapped(object sender, TappedRoutedEventArgs e)
    {
        _aboutTaps++;
        if (_aboutTaps < 7)
        {
            VersionLabel.Text = $"v2.0.0  ·  {7 - _aboutTaps} taps to go...";
            return;
        }

        if (_aboutTaps == 7)
        {
            VersionLabel.Text = "v2.0.0  ·  🐱 meow!";
            EasterEggPanel.Visibility = Visibility.Visible;
            AnimationHelper.SlideIn(EasterEggPanel, 0);
            ShowRandomFacts();
        }
    }

    private void RandomFact_Click(object sender, RoutedEventArgs e) => ShowRandomFacts();

    private void ShowRandomFacts()
    {
        CatFact.Text = $"🐱 {CatFacts[_rng.Next(CatFacts.Length)]}";
        McFunFact.Text = $"⛏️ {McFacts[_rng.Next(McFacts.Length)]}";
    }

    public static string GetRandomSplash() => Splashes[Random.Shared.Next(Splashes.Length)];
}

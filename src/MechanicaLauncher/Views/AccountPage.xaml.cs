using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MechanicaLauncher.Core.Auth;

namespace MechanicaLauncher.Views;

public sealed partial class AccountPage : Page
{
    private static Core.Profiles.LauncherSettings S => App.Settings;

    public AccountPage()
    {
        this.InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        NicknameBox.Text = S.Username;
        SyncUi();
    }

    private void SyncUi()
    {
        var loggedIn = S.AuthMode != "offline" && S.Username != "Player";

        LoggedInPanel.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;
        LoginPanel.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;

        if (loggedIn)
        {
            UsernameText.Text = S.Username;
            AccountBadge.Text = S.AuthMode == "microsoft" ? "Microsoft" : "Offline";
        }
    }

    private void SaveOffline_Click(object sender, RoutedEventArgs e)
    {
        var nick = NicknameBox.Text?.Trim();
        if (string.IsNullOrEmpty(nick)) return;

        S.Username = nick;
        S.AuthMode = "offline";
        S.Uuid = Guid.NewGuid().ToString("N");
        S.AccessToken = "0";
        S.Save();
        SyncUi();
    }

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        S.Username = "Player";
        S.AuthMode = "offline";
        S.Uuid = "0";
        S.AccessToken = "0";
        S.Save();
        NicknameBox.Text = "";
        SyncUi();
    }

    private async void MsLogin_Click(object sender, RoutedEventArgs e)
    {
        var auth = new MicrosoftAuth();
        var cts = new CancellationTokenSource();
        ContentDialog? dialog = null;

        try
        {
            var dc = await auth.RequestDeviceCodeAsync();

            dialog = new ContentDialog
            {
                Title = "Sign in with Microsoft",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = "Go to this link:", TextWrapping = TextWrapping.Wrap },
                        new HyperlinkButton { Content = dc.VerificationUri, NavigateUri = new Uri(dc.VerificationUri) },
                        new TextBlock { Text = "Enter this code:", Margin = new Thickness(0, 4, 0, 0) },
                        new TextBlock { Text = dc.UserCode, FontSize = 32, FontWeight = Microsoft.UI.Text.FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, IsTextSelectionEnabled = true },
                        new ProgressRing { IsActive = true, Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) },
                        new TextBlock { Text = "Waiting for you to sign in...", HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)) }
                    }
                },
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };
            dialog.CloseButtonClick += (_, _) => cts.Cancel();

            var showTask = dialog.ShowAsync().AsTask();
            var authTask = auth.PollForTokenAsync(dc, cts.Token);
            var winner = await Task.WhenAny(showTask, authTask);

            try { dialog.Hide(); } catch { }
            dialog = null;
            await Task.Delay(200);

            if (winner == authTask)
            {
                var result = await authTask;
                S.Username = result.Username;
                S.Uuid = result.Uuid;
                S.AccessToken = result.AccessToken;
                S.AuthMode = "microsoft";
                S.Save();
                SyncUi();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            try { dialog?.Hide(); } catch { }
            await Task.Delay(300);
            try
            {
                await new ContentDialog
                {
                    Title = "Authorization Failed",
                    Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap, MaxWidth = 400 },
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                }.ShowAsync();
            }
            catch { }
        }
    }
}

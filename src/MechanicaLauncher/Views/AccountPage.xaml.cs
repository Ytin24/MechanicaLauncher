using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MechanicaLauncher.Core.Auth;
using MechanicaLauncher.Core.Profiles;

namespace MechanicaLauncher.Views;

public sealed partial class AccountPage : Page
{
    private readonly LauncherSettings _settings = LauncherSettings.Load();

    public AccountPage()
    {
        this.InitializeComponent();
        NicknameBox.Text = _settings.Username;
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        UsernameText.Text = _settings.Username;
        AccountTypeText.Text = _settings.AuthMode == "microsoft" ? "Microsoft Account" : "Offline mode";
    }

    private void SaveOffline_Click(object sender, RoutedEventArgs e)
    {
        var nick = NicknameBox.Text?.Trim();
        if (string.IsNullOrEmpty(nick))
        {
            NicknameBox.PlaceholderText = "Enter a nickname!";
            return;
        }

        _settings.Username = nick;
        _settings.AuthMode = "offline";
        _settings.Save();
        UpdateDisplay();
    }

    private async void MsLogin_Click(object sender, RoutedEventArgs e)
    {
        var auth = new MicrosoftAuth();
        var cts = new CancellationTokenSource();
        ContentDialog? activeDialog = null;

        try
        {
            var deviceCode = await auth.RequestDeviceCodeAsync();

            activeDialog = new ContentDialog
            {
                Title = "Sign in with Microsoft",
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "1. Open this link in your browser:", TextWrapping = TextWrapping.Wrap },
                        new HyperlinkButton
                        {
                            Content = deviceCode.VerificationUri,
                            NavigateUri = new Uri(deviceCode.VerificationUri)
                        },
                        new TextBlock { Text = "2. Enter this code:", Margin = new Thickness(0, 8, 0, 0) },
                        new TextBlock
                        {
                            Text = deviceCode.UserCode,
                            FontSize = 32,
                            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            IsTextSelectionEnabled = true,
                            Margin = new Thickness(0, 8, 0, 0)
                        },
                        new ProgressRing { IsActive = true, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) },
                        new TextBlock { Text = "Waiting for authorization...", Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12 }
                    }
                },
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };

            activeDialog.CloseButtonClick += (_, _) => cts.Cancel();

            var dialogTask = activeDialog.ShowAsync().AsTask();
            var authTask = auth.PollForTokenAsync(deviceCode, cts.Token);

            var completed = await Task.WhenAny(dialogTask, authTask);

            try { activeDialog.Hide(); } catch { }
            activeDialog = null;

            await Task.Delay(200);

            if (completed == authTask)
            {
                var result = await authTask;
                _settings.Username = result.Username;
                _settings.AuthMode = "microsoft";
                _settings.Save();
                UpdateDisplay();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            try { activeDialog?.Hide(); } catch { }
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

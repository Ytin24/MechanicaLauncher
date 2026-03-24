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
        if (string.IsNullOrEmpty(nick)) return;

        _settings.Username = nick;
        _settings.AuthMode = "offline";
        _settings.Save();
        UpdateDisplay();
    }

    private async void MsLogin_Click(object sender, RoutedEventArgs e)
    {
        var auth = new MicrosoftAuth();

        try
        {
            var deviceCode = await auth.RequestDeviceCodeAsync();

            var dialog = new ContentDialog
            {
                Title = "Sign in with Microsoft",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Open this link in your browser:",
                            TextWrapping = TextWrapping.Wrap
                        },
                        new HyperlinkButton
                        {
                            Content = deviceCode.VerificationUri,
                            NavigateUri = new Uri(deviceCode.VerificationUri)
                        },
                        new TextBlock { Text = "And enter this code:" },
                        new TextBlock
                        {
                            Text = deviceCode.UserCode,
                            FontSize = 28,
                            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            IsTextSelectionEnabled = true
                        }
                    }
                },
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };

            var cts = new CancellationTokenSource();
            dialog.CloseButtonClick += (_, _) => cts.Cancel();

            _ = dialog.ShowAsync();

            var result = await auth.PollForTokenAsync(deviceCode, cts.Token);

            dialog.Hide();

            _settings.Username = result.Username;
            _settings.AuthMode = "microsoft";
            _settings.Save();
            UpdateDisplay();

            UsernameText.Text = result.Username;
            AccountTypeText.Text = "Microsoft Account ✓";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var errorDialog = new ContentDialog
            {
                Title = "Auth Error",
                Content = ex.Message,
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await errorDialog.ShowAsync();
        }
    }
}

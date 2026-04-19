using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
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
        ApplyLocale();
        NicknameBox.Text = S.Username;
        SyncUi();
    }

    private void ApplyLocale()
    {
        PageTitle.Text = App.L("acc.title");
        MsTitle.Text = App.L("acc.ms_account");
        MsDesc.Text = App.L("acc.ms_desc");
        MsSignInText.Text = App.L("acc.ms_signin");
        OfflineTitle.Text = App.L("acc.offline");
        OfflineDesc.Text = App.L("acc.offline_desc");
        NicknameBox.PlaceholderText = App.L("acc.nickname");
        SaveBtn.Content = App.L("acc.save");
        SignOutBtn.Content = App.L("acc.signout");
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
        S.MsRefreshToken = "";
        S.Save();
        NicknameBox.Text = "";
        SyncUi();
    }

    private async void MsLogin_Click(object sender, RoutedEventArgs e)
    {
        var codeTcs = new TaskCompletionSource<string?>();
        var webView = new WebView2 { Width = 520, Height = 640 };

        var dialog = new ContentDialog
        {
            Title = "Sign in with Microsoft",
            Content = webView,
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };
        dialog.CloseButtonClick += (_, _) => codeTcs.TrySetResult(null);

        try
        {
            await webView.EnsureCoreWebView2Async();
            // Force a fresh login session so "select_account" actually offers a choice after sign-out.
            webView.CoreWebView2.CookieManager.DeleteAllCookies();

            webView.CoreWebView2.NavigationStarting += (s, args) =>
            {
                if (!args.Uri.StartsWith(MicrosoftAuth.RedirectUri, StringComparison.OrdinalIgnoreCase)) return;
                var q = new Uri(args.Uri).Query;
                var parsed = System.Web.HttpUtility.ParseQueryString(q);
                var code = parsed["code"];
                var err = parsed["error"];
                args.Cancel = true;
                if (!string.IsNullOrEmpty(code)) codeTcs.TrySetResult(code);
                else codeTcs.TrySetException(new Exception($"OAuth error: {err ?? "no code"} — {parsed["error_description"]}"));
            };

            webView.CoreWebView2.Navigate(MicrosoftAuth.BuildAuthorizeUrl());
        }
        catch (Exception ex)
        {
            await ShowAuthErrorAsync($"WebView2 init failed: {ex.Message}");
            return;
        }

        var showTask = dialog.ShowAsync().AsTask();
        var winner = await Task.WhenAny(showTask, codeTcs.Task);

        try { dialog.Hide(); } catch { }
        await Task.Delay(150);

        string? code;
        try { code = winner == codeTcs.Task ? await codeTcs.Task : null; }
        catch (Exception ex) { await ShowAuthErrorAsync(ex.Message); return; }

        if (string.IsNullOrEmpty(code)) return;

        try
        {
            var result = await new MicrosoftAuth().CompleteWithCodeAsync(code);
            S.Username = result.Username;
            S.Uuid = result.Uuid;
            S.AccessToken = result.AccessToken;
            S.MsRefreshToken = result.RefreshToken ?? "";
            S.AuthMode = "microsoft";
            S.Save();
            SyncUi();
        }
        catch (Exception ex)
        {
            await ShowAuthErrorAsync(ex.Message);
        }
    }

    private async Task ShowAuthErrorAsync(string message)
    {
        try
        {
            await new ContentDialog
            {
                Title = "Authorization Failed",
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 400 },
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            }.ShowAsync();
        }
        catch { }
    }
}

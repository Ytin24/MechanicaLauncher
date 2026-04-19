using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;
using Microsoft.Web.WebView2.Core;
using MechanicaLauncher.Core.Auth;

namespace MechanicaLauncher.Views;

public sealed partial class AccountPage : Page
{
    private static Core.Profiles.LauncherSettings S => App.Settings;
    private static readonly HttpClient SkinHttp = new();
    private string _skinVariant = "classic";
    private long _skinCacheBuster;

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

            var uuidShort = S.Uuid.Length >= 8 ? S.Uuid[..8] : S.Uuid;
            UuidShortText.Text = $"UUID · {uuidShort}";

            LoadAvatarAsync();
            LoadSkinPanel();
        }
    }

    private void LoadAvatarAsync()
    {
        // Try crafatar first; on failure the ImageFailed handler switches to mc-heads; placeholder FontIcon
        // is visible behind the Image until ImageOpened fires.
        var identifier = S.AuthMode == "microsoft" && !string.IsNullOrEmpty(S.Uuid) && S.Uuid != "0"
            ? S.Uuid
            : !string.IsNullOrEmpty(S.Username) && S.Username != "Player" ? S.Username : "MHF_Steve";
        SetAvatarSource($"https://crafatar.com/renders/head/{identifier}?size=88&overlay", isFallback: false);
    }

    private void SetAvatarSource(string url, bool isFallback)
    {
        try
        {
            AvatarImage.Tag = isFallback ? "fallback" : "primary";
            AvatarImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(url));
        }
        catch { }
    }

    private void AvatarImage_Loaded(object sender, RoutedEventArgs e)
    {
        AvatarPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void AvatarImage_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        if (AvatarImage.Tag?.ToString() == "fallback") return;
        // crafatar down or UUID not resolvable — fall back to mc-heads.net which accepts username.
        var identifier = !string.IsNullOrEmpty(S.Username) && S.Username != "Player" ? S.Username : "Steve";
        SetAvatarSource($"https://mc-heads.net/avatar/{identifier}/88", isFallback: true);
    }

    // --- Skin panel -------------------------------------------------------
    private void LoadSkinPanel()
    {
        SkinPanel.Visibility = S.AuthMode == "microsoft" ? Visibility.Visible : Visibility.Collapsed;
        if (SkinPanel.Visibility == Visibility.Collapsed) return;

        if (_skinVariant == "slim") SkinModelSlim.IsChecked = true;
        else SkinModelClassic.IsChecked = true;

        LoadSkinBody();
    }

    private void LoadSkinBody()
    {
        try
        {
            var id = !string.IsNullOrEmpty(S.Uuid) && S.Uuid != "0" ? S.Uuid : S.Username;
            var bust = _skinCacheBuster != 0 ? $"?cb={_skinCacheBuster}" : "";
            SkinPlaceholder.Visibility = Visibility.Visible;
            SkinBodyImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                new Uri($"https://mc-heads.net/body/{id}/192{bust}"));
        }
        catch { }
    }

    private void SkinBody_Loaded(object sender, RoutedEventArgs e) =>
        SkinPlaceholder.Visibility = Visibility.Collapsed;

    private void SkinBody_Failed(object sender, ExceptionRoutedEventArgs e) { }

    private void RefreshSkin_Click(object sender, RoutedEventArgs e)
    {
        _skinCacheBuster = DateTime.UtcNow.Ticks;
        LoadSkinBody();
        SkinStatus.Text = "Refreshed.";
    }

    private void SkinModel_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag)
            _skinVariant = tag;
    }

    private async void OpenSkinEditor_Click(object sender, RoutedEventArgs e) =>
        await new SkinEditorWindow(this.XamlRoot, S, UploadSkinFromBytesAsync).ShowAsync();

    private async Task UploadSkinFromBytesAsync(byte[] pngBytes)
    {
        if (S.AuthMode != "microsoft" || string.IsNullOrEmpty(S.AccessToken) || S.AccessToken == "0")
        { SkinStatus.Text = "Sign in with Microsoft first."; return; }
        try
        {
            SkinStatus.Text = $"Uploading {pngBytes.Length} bytes as {_skinVariant}...";
            using var form = new MultipartFormDataContent { { new StringContent(_skinVariant), "variant" } };
            var fileContent = new ByteArrayContent(pngBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(fileContent, "file", "skin.png");

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.minecraftservices.com/minecraft/profile/skins");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", S.AccessToken);
            req.Content = form;

            var resp = await SkinHttp.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                SkinStatus.Text = $"Mojang rejected ({(int)resp.StatusCode} {resp.ReasonPhrase}): {body[..Math.Min(220, body.Length)]}";
                return;
            }

            SkinStatus.Text = "Uploaded. Rendering preview from the PNG you just saved...";
            // Compose a 2D front-body preview from the uploaded PNG itself — zero dependency on the
            // CDNs that cache for 15 min. CDN-based preview will catch up on next page open.
            try
            {
                var bodyPng = await SkinRenderer.RenderFrontBodyAsync(pngBytes, scale: 4);
                await ShowBodyPngAsync(bodyPng);
                // Also update the header avatar to the head crop.
                var headPng = await SkinRenderer.RenderHeadAsync(pngBytes, scale: 4);
                await ShowAvatarPngAsync(headPng);
                SkinStatus.Text = "Uploaded. Preview generated locally.";
            }
            catch (Exception ex)
            {
                SkinStatus.Text = $"Upload OK, but local preview render failed: {ex.Message}";
            }
        }
        catch (Exception ex) { SkinStatus.Text = $"Error: {ex.GetType().Name}: {ex.Message}"; }
    }

    private async Task ShowBodyPngAsync(byte[] pngBytes)
    {
        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await stream.WriteAsync(pngBytes.AsBuffer());
        stream.Seek(0);
        await bmp.SetSourceAsync(stream);
        SkinBodyImage.Source = bmp;
        SkinPlaceholder.Visibility = Visibility.Collapsed;
    }

    private async Task ShowAvatarPngAsync(byte[] pngBytes)
    {
        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await stream.WriteAsync(pngBytes.AsBuffer());
        stream.Seek(0);
        await bmp.SetSourceAsync(stream);
        AvatarImage.Source = bmp;
        AvatarPlaceholder.Visibility = Visibility.Collapsed;
    }


    private async void UploadSkin_Click(object sender, RoutedEventArgs e)
    {
        if (S.AuthMode != "microsoft" || string.IsNullOrEmpty(S.AccessToken) || S.AccessToken == "0")
        {
            SkinStatus.Text = "Sign in with Microsoft first.";
            return;
        }

        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add(".png");
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        SkinStatus.Text = "Uploading...";
        try
        {
            var bytes = await Windows.Storage.FileIO.ReadBufferAsync(file);
            var managed = new byte[bytes.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(bytes))
                reader.ReadBytes(managed);

            using var form = new MultipartFormDataContent
            {
                { new StringContent(_skinVariant), "variant" }
            };
            var fileContent = new ByteArrayContent(managed);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(fileContent, "file", "skin.png");

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.minecraftservices.com/minecraft/profile/skins");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", S.AccessToken);
            req.Content = form;

            var resp = await SkinHttp.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                SkinStatus.Text = "Uploaded! Reloading preview...";
                _skinCacheBuster = DateTime.UtcNow.Ticks;
                await Task.Delay(1500);
                LoadSkinBody();
            }
            else
            {
                var body = await resp.Content.ReadAsStringAsync();
                SkinStatus.Text = $"Failed ({(int)resp.StatusCode}): {body[..Math.Min(120, body.Length)]}";
            }
        }
        catch (Exception ex)
        {
            SkinStatus.Text = $"Error: {ex.Message}";
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

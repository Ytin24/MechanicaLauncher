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
using MechanicaLauncher.Core.Auth;

namespace MechanicaLauncher.Views;

public sealed partial class AccountPage : Page
{
    private static Core.Profiles.LauncherSettings S => App.Settings;
    private static readonly HttpClient SkinHttp = new();
    private string _skinVariant = "classic";
    private long _skinCacheBuster;
    private bool _signingIn;

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
        SignInAgainBtn.Content = App.L("acc.signin_again");
    }

    private void SyncUi()
    {
        var loggedIn = S.AuthMode == "microsoft" && !string.IsNullOrEmpty(S.AccessToken) && S.AccessToken != "0";

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
        S.MsRefreshToken = "";
        S.MsClientId = "";
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
        S.MsClientId = "";
        S.Save();
        NicknameBox.Text = "";
        SyncUi();
    }

    private async void MsLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_signingIn) return;
        _signingIn = true;
        MsSignInBtn.IsEnabled = false;
        SignInAgainBtn.IsEnabled = false;
        SaveBtn.IsEnabled = false;
        string? error = null;
        try
        {
            var auth = MicrosoftAuth.CreateForSignIn();
            var signIn = auth.BeginSignIn();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var ct = cancellation.Token;
            var redirect = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => redirect.TrySetCanceled(ct));
            var webView = new WebView2
            {
                Width = Math.Max(240, Math.Min(520, XamlRoot.Size.Width - 100)),
                Height = Math.Max(220, Math.Min(560, XamlRoot.Size.Height - 220))
            };
            var dialog = new ContentDialog
            {
                Title = App.L("acc.ms_signin"), Content = webView,
                CloseButtonText = App.L("inst.cancel"), XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme
            };
            dialog.Resources["ContentDialogMaxWidth"] = 640d;
            var userCancelled = false;
            dialog.CloseButtonClick += (_, _) => { userCancelled = true; cancellation.Cancel(); };
            dialog.Closed += (_, _) => { userCancelled = true; cancellation.Cancel(); };
            dialog.Opened += async (_, _) =>
            {
                try
                {
                    await webView.EnsureCoreWebView2Async();
                    ct.ThrowIfCancellationRequested();
                    webView.CoreWebView2.NavigationStarting += (_, args) =>
                    {
                        if (!signIn.IsRedirect(args.Uri)) return;
                        args.Cancel = true;
                        redirect.TrySetResult(args.Uri);
                    };
                    webView.CoreWebView2.NavigationCompleted += (_, args) =>
                    {
                        if (!args.IsSuccess && !redirect.Task.IsCompleted && !cancellation.IsCancellationRequested)
                            redirect.TrySetException(new HttpRequestException(App.L("acc.login_page_failed")));
                    };
                    webView.CoreWebView2.Navigate(signIn.AuthorizeUrl);
                }
                catch (Exception ex) { redirect.TrySetException(ex); }
            };

            var shown = dialog.ShowAsync().AsTask();
            try
            {
                var address = await redirect.Task;
                dialog.Content = new StackPanel
                {
                    Spacing = 16, Padding = new Thickness(24),
                    Children =
                    {
                        new ProgressRing { IsActive = true, Width = 32, Height = 32 },
                        new TextBlock { Text = App.L("acc.signing_in"), TextWrapping = TextWrapping.Wrap }
                    }
                };
                var result = await auth.CompleteAsync(signIn, address, ct);
                ct.ThrowIfCancellationRequested();
                S.Username = result.Username;
                S.Uuid = result.Uuid;
                S.AccessToken = result.AccessToken;
                S.MsRefreshToken = result.RefreshToken ?? "";
                S.MsClientId = auth.ClientId;
                S.AuthMode = "microsoft";
                S.Save();
                SyncUi();
            }
            catch (TaskCanceledException) when (!userCancelled) { error = App.L("acc.auth_timeout"); }
            catch (OperationCanceledException)
            {
                if (!userCancelled && cancellation.IsCancellationRequested) error = App.L("acc.auth_timeout");
            }
            finally
            {
                dialog.Hide();
                await shown;
                webView.Close();
            }
        }
        catch (HttpRequestException ex) { error = ex.StatusCode.HasValue ? ex.Message : App.L("acc.login_page_failed"); }
        catch (Exception ex) { error = ex.Message; }
        finally
        {
            _signingIn = false;
            MsSignInBtn.IsEnabled = true;
            SignInAgainBtn.IsEnabled = true;
            SaveBtn.IsEnabled = true;
        }
        if (error != null) await ShowAuthErrorAsync(error);
    }

    private async Task ShowAuthErrorAsync(string message)
    {
        try
        {
            await new ContentDialog
            {
                Title = App.L("acc.auth_failed"),
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 400 },
                CloseButtonText = App.L("acc.ok"),
                XamlRoot = this.XamlRoot
            }.ShowAsync();
        }
        catch { }
    }
}

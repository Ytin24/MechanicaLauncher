using System.Runtime.InteropServices;
using MechanicaLauncher.Core.Auth;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Forms = System.Windows.Forms;

namespace MechanicaLauncher.Desktop;

internal static class Platform
{
    private sealed class Owner : Forms.IWin32Window
    {
        public IntPtr Handle => Process.GetCurrentProcess().MainWindowHandle;
    }
    public static string? PickFile(string filter)
    {
        using var dialog = new Forms.OpenFileDialog { Filter = filter, CheckFileExists = true, Multiselect = false };
        return dialog.ShowDialog(new Owner()) == Forms.DialogResult.OK ? dialog.FileName : null;
    }
    public static string? SaveFile(string filter, string name)
    {
        using var dialog = new Forms.SaveFileDialog { Filter = filter, FileName = name, OverwritePrompt = true };
        return dialog.ShowDialog(new Owner()) == Forms.DialogResult.OK ? dialog.FileName : null;
    }
    public static string? PickFolder(string title)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = title, UseDescriptionForTitle = true };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }
    public static void OpenPath(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true });
    }
    public static void RevealFile(string path)
    {
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add("/select,"); start.ArgumentList.Add(Path.GetFullPath(path)); Process.Start(start);
    }
    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
    public static string? SignIn(MicrosoftSignInRequest request)
    {
        using var form = new Forms.Form { Text = "Microsoft · Mechanica", Width = 600, Height = 760, StartPosition = Forms.FormStartPosition.CenterScreen };
        using var browser = new WebView2 { Dock = Forms.DockStyle.Fill };
        form.Controls.Add(browser);
        string? result = null; Exception? failure = null;
        form.Shown += async (_, _) =>
        {
            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(LauncherPaths.DataDirectory, "auth-webview"));
                if (form.IsDisposed) return;
                await browser.EnsureCoreWebView2Async(environment);
                if (form.IsDisposed) return;
                browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
                browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                browser.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; OpenUrl(e.Uri); };
                browser.CoreWebView2.NavigationStarting += (_, e) =>
                {
                    if (request.IsRedirect(e.Uri)) { e.Cancel = true; result = e.Uri; form.Close(); }
                    else if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https") e.Cancel = true;
                };
                browser.Source = new(request.AuthorizeUrl);
            }
            catch (Exception ex) { failure = ex; if (!form.IsDisposed) form.Close(); }
        };
        form.ShowDialog(new Owner());
        if (failure != null) throw new InvalidOperationException("Microsoft sign-in: " + failure.Message, failure);
        return result;
    }
    public static bool SystemAnimations
    {
        get { bool enabled = true; return !SystemParametersInfo(0x1042, 0, ref enabled, 0) || enabled; }
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] ref bool value, uint flags);
}

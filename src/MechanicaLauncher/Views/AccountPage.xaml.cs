using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MechanicaLauncher.Views;

public sealed partial class AccountPage : Page
{
    public AccountPage() => this.InitializeComponent();

    private void SaveOffline_Click(object sender, RoutedEventArgs e)
    {
        var nick = NicknameBox.Text?.Trim();
        if (!string.IsNullOrEmpty(nick))
        {
            UsernameText.Text = nick;
            AccountTypeText.Text = "Offline mode";
        }
    }
}

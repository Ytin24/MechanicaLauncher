namespace MechanicaLauncher.Core.Auth;

public sealed class AuthResult
{
    public string Username { get; set; } = "Player";
    public string Uuid { get; set; } = "0";
    public string AccessToken { get; set; } = "0";
    public string UserType { get; set; } = "legacy";
    public string? RefreshToken { get; set; }
    public bool IsOnline => UserType == "msa";

    public static AuthResult Offline(string username) => new()
    {
        Username = username,
        Uuid = Guid.NewGuid().ToString("N"),
        AccessToken = "0",
        UserType = "legacy"
    };
}

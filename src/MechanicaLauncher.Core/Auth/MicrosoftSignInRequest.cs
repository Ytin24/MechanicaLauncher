using System.Security.Cryptography;
using System.Text;
using System.Web;
using MechanicaLauncher.Core.Localization;

namespace MechanicaLauncher.Core.Auth;

public sealed class MicrosoftSignInRequest
{
    private readonly Uri _redirectUri;
    private readonly string _state = Base64Url(RandomNumberGenerator.GetBytes(32));
    private int _completed;
    internal string CodeVerifier { get; } = Base64Url(RandomNumberGenerator.GetBytes(32));
    internal string ClientId { get; }
    public string AuthorizeUrl { get; }

    internal MicrosoftSignInRequest(string clientId, string endpoint, string redirectUri, string scope)
    {
        ClientId = clientId;
        _redirectUri = new Uri(redirectUri);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(CodeVerifier)));
        AuthorizeUrl = $"{endpoint}?client_id={Uri.EscapeDataString(clientId)}&response_type=code&response_mode=query" +
                       $"&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope={Uri.EscapeDataString(scope)}" +
                       $"&state={_state}&code_challenge={challenge}&code_challenge_method=S256&prompt=select_account";
    }

    public bool IsRedirect(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && uri.Scheme == _redirectUri.Scheme && uri.Authority == _redirectUri.Authority
        && uri.AbsolutePath == _redirectUri.AbsolutePath && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment);

    internal string GetAuthorizationCode(string address)
    {
        if (!IsRedirect(address)) throw new InvalidOperationException(Locale.Get("acc.auth_response_invalid"));
        var query = HttpUtility.ParseQueryString(new Uri(address).Query);
        if (query.GetValues("state") is not [var state] || state != _state)
            throw new InvalidOperationException(Locale.Get("acc.auth_response_invalid"));
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            throw new InvalidOperationException(Locale.Get("acc.auth_response_invalid"));
        if (query["error"] == "access_denied") throw new OperationCanceledException();
        if (!string.IsNullOrEmpty(query["error"])) throw new InvalidOperationException(Locale.Get("acc.client_rejected"));
        if (query.GetValues("code") is not [var code] || string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException(Locale.Get("acc.auth_response_invalid"));
        return code;
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

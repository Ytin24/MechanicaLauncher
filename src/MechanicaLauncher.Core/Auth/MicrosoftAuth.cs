using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MechanicaLauncher.Core.Localization;

namespace MechanicaLauncher.Core.Auth;

public sealed class MicrosoftAuth
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    internal const string LegacyClientId = "00000000441cc96b";
    private readonly HttpClient _http;
    public string ClientId { get; }
    private bool IsLegacy => ClientId == LegacyClientId;
    private string Authority => IsLegacy ? "https://login.live.com/oauth20_" : "https://login.microsoftonline.com/consumers/oauth2/v2.0/";
    private string TokenEndpoint => Authority + (IsLegacy ? "token.srf" : "token");
    private string Scope => IsLegacy ? "service::user.auth.xboxlive.com::MBI_SSL" : "XboxLive.signin offline_access";
    private string RedirectUri => IsLegacy ? "https://login.live.com/oauth20_desktop.srf" : "https://login.microsoftonline.com/common/oauth2/nativeclient";

    public MicrosoftAuth(string? clientId = null) : this(SharedHttp, clientId) { }

    internal MicrosoftAuth(HttpClient http, string? clientId = null)
    {
        _http = http;
        ClientId = string.IsNullOrWhiteSpace(clientId) ? LegacyClientId : clientId.Trim();
        if (ClientId != LegacyClientId && (!Guid.TryParse(ClientId, out var id) || id == Guid.Empty))
            throw new InvalidOperationException(Locale.Get("acc.client_invalid"));
    }

    public static MicrosoftAuth CreateForSignIn()
    {
        var clientId = Environment.GetEnvironmentVariable("MECHANICA_MICROSOFT_CLIENT_ID");
        if (string.IsNullOrWhiteSpace(clientId))
            clientId = typeof(MicrosoftAuth).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "MicrosoftClientId")?.Value;
        return new MicrosoftAuth(clientId);
    }

    public MicrosoftSignInRequest BeginSignIn() => new(ClientId,
        Authority + (IsLegacy ? "authorize.srf" : "authorize"), RedirectUri, Scope);

    public async Task<AuthResult> CompleteAsync(MicrosoftSignInRequest signIn, string redirect, CancellationToken ct = default)
    {
        if (signIn.ClientId != ClientId) throw new InvalidOperationException(Locale.Get("acc.auth_response_invalid"));
        var code = signIn.GetAuthorizationCode(redirect);
        var data = await RequestTokenAsync(new()
        {
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = signIn.CodeVerifier,
        }, ct);
        var result = await ExchangeForMinecraftAsync(GetStr(data, "access_token"), ct);
        result.RefreshToken = OptionalString(data, "refresh_token");
        return result;
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) throw new InvalidOperationException(Locale.Get("acc.session_expired"));
        var data = await RequestTokenAsync(new()
        {
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        }, ct);
        var result = await ExchangeForMinecraftAsync(GetStr(data, "access_token"), ct);
        result.RefreshToken = OptionalString(data, "refresh_token") ?? refreshToken;
        return result;
    }

    private async Task<JsonElement> RequestTokenAsync(Dictionary<string, string> fields, CancellationToken ct)
    {
        fields["client_id"] = ClientId;
        fields["redirect_uri"] = RedirectUri;
        fields["scope"] = Scope;
        using var form = new FormUrlEncodedContent(fields);
        using var response = await _http.PostAsync(TokenEndpoint, form, ct);
        return await ReadResponseAsync(response, "Microsoft", ct);
    }

    private async Task<AuthResult> ExchangeForMinecraftAsync(string msaToken, CancellationToken ct)
    {
        var xbl = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", new
        {
            Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = $"{(IsLegacy ? "t" : "d")}={msaToken}" },
            RelyingParty = "http://auth.xboxlive.com", TokenType = "JWT"
        }, "Xbox Live", ct);

        var xsts = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { GetStr(xbl, "Token") } },
            RelyingParty = "rp://api.minecraftservices.com/", TokenType = "JWT"
        }, "Xbox Live", ct);
        var uhs = GetStr(xsts.GetProperty("DisplayClaims").GetProperty("xui")[0], "uhs");
        var mc = await PostJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox",
            new { identityToken = $"XBL3.0 x={uhs};{GetStr(xsts, "Token")}" }, "Minecraft", ct);
        var mcToken = GetStr(mc, "access_token");

        using var request = ProfileRequest(mcToken);
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(Locale.Get("acc.no_java_profile"));
        var profile = await ReadResponseAsync(response, "Minecraft", ct);
        ct.ThrowIfCancellationRequested();
        return new AuthResult
        {
            Username = GetStr(profile, "name"), Uuid = GetStr(profile, "id"),
            AccessToken = mcToken, UserType = "msa"
        };
    }

    public static Task<bool> ValidateTokenAsync(string accessToken, CancellationToken ct = default) =>
        new MicrosoftAuth().ValidateAsync(accessToken, ct);

    internal async Task<bool> ValidateAsync(string accessToken, CancellationToken ct = default)
    {
        using var request = ProfileRequest(accessToken);
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return false;
        await ReadResponseAsync(response, "Minecraft", ct);
        return true;
    }

    private static HttpRequestMessage ProfileRequest(string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private async Task<JsonElement> PostJsonAsync(string url, object payload, string service, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(url, content, ct);
        return await ReadResponseAsync(response, service, ct);
    }

    private static async Task<JsonElement> ReadResponseAsync(HttpResponseMessage response, string service, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        JsonElement data = default;
        try { data = JsonSerializer.Deserialize<JsonElement>(body); }
        catch (JsonException) { }

        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("XErr", out var xboxError)
            && xboxError.ValueKind == JsonValueKind.Number && xboxError.TryGetInt64(out var code))
            throw new InvalidOperationException(code switch
            {
                2148916233 => Locale.Get("acc.no_xbox_profile"),
                2148916235 => Locale.Get("acc.xbox_region"),
                2148916236 or 2148916237 or 2148916238 => Locale.Get("acc.xbox_family"),
                _ => $"Xbox Live: {code}"
            });

        if (!response.IsSuccessStatusCode)
        {
            var error = OptionalString(data, "error");
            if (OptionalString(data, "errorMessage") == "Invalid app registration") error = "Invalid app registration";
            var message = error switch
            {
                "invalid_grant" or "interaction_required" => Locale.Get("acc.session_expired"),
                "invalid_client" or "unauthorized_client" or "Invalid app registration" => Locale.Get("acc.client_rejected"),
                _ => $"{service}: {Locale.Get("acc.service_unavailable")} (HTTP {(int)response.StatusCode})."
            };
            throw new HttpRequestException(message, null, response.StatusCode);
        }
        if (data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(Locale.Get("acc.auth_response_invalid"));
        return data;
    }

    private static string? OptionalString(JsonElement data, string property) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;

    private static string GetStr(JsonElement data, string property) =>
        OptionalString(data, property) ?? throw new InvalidDataException(Locale.Get("acc.auth_response_invalid"));
}

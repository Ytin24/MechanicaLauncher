using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MechanicaLauncher.Core.Auth;

public sealed class MicrosoftAuth
{
    private static readonly HttpClient Http = new();

    // Legacy Mojang Launcher client_id — whitelisted by Minecraft Services.
    // A custom Azure app registration would be rejected by api.minecraftservices.com with 403 "Invalid app registration".
    private const string ClientId = "00000000441cc96b";
    private const string AuthorizeEndpoint = "https://login.live.com/oauth20_authorize.srf";
    private const string TokenEndpoint = "https://login.live.com/oauth20_token.srf";
    public const string RedirectUri = "https://login.live.com/oauth20_desktop.srf";
    private const string Scope = "service::user.auth.xboxlive.com::MBI_SSL";

    public static string BuildAuthorizeUrl()
    {
        return $"{AuthorizeEndpoint}?client_id={ClientId}" +
               $"&response_type=code" +
               $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
               $"&scope={Uri.EscapeDataString(Scope)}" +
               "&prompt=select_account";
    }

    public async Task<AuthResult> CompleteWithCodeAsync(string code, CancellationToken ct = default)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = RedirectUri,
            ["scope"] = Scope,
        });

        var resp = await Http.PostAsync(TokenEndpoint, form, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Token exchange failed: {json[..Math.Min(300, json.Length)]}");

        var data = JsonSerializer.Deserialize<JsonElement>(json);
        var msaToken = GetStr(data, "access_token");
        var refreshToken = data.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;

        var result = await ExchangeForMinecraftAsync(msaToken);
        result.RefreshToken = refreshToken;
        return result;
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
            ["redirect_uri"] = RedirectUri,
            ["scope"] = Scope,
        });
        var resp = await Http.PostAsync(TokenEndpoint, form, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Refresh failed: {json[..Math.Min(300, json.Length)]}");

        var data = JsonSerializer.Deserialize<JsonElement>(json);
        var msaToken = GetStr(data, "access_token");
        var newRefresh = data.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : refreshToken;

        var result = await ExchangeForMinecraftAsync(msaToken);
        result.RefreshToken = newRefresh;
        return result;
    }

    private async Task<AuthResult> ExchangeForMinecraftAsync(string msaToken)
    {
        // MBI_SSL scope => RpsTicket uses "t={token}".
        var xblBody = new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = $"t={msaToken}"
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        };
        var xbl = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", xblBody);
        var xblToken = GetStr(xbl, "Token");
        var uhs = xbl.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()
                  ?? throw new Exception("Missing user hash from Xbox Live");

        var xstsBody = new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xblToken }
            },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        };
        var xsts = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", xstsBody);

        if (xsts.TryGetProperty("XErr", out var xErr))
        {
            var code = xErr.GetInt64();
            throw new Exception(code switch
            {
                2148916233 => "No Xbox account found for this Microsoft account.\nSign up at xbox.com first.",
                2148916235 => "Xbox Live is not available in your country/region.",
                2148916238 => "This account belongs to a minor.\nAn adult needs to add it to a Microsoft Family.",
                _ => $"Xbox error {code}"
            });
        }

        var xstsToken = GetStr(xsts, "Token");

        var mcBody = new { identityToken = $"XBL3.0 x={uhs};{xstsToken}" };
        var mc = await PostJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox", mcBody);
        var mcToken = GetStr(mc, "access_token");

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcToken);
        var profileResp = await Http.SendAsync(req);
        var profileBody = await profileResp.Content.ReadAsStringAsync();

        if (!profileResp.IsSuccessStatusCode)
            throw new Exception("Could not load Minecraft profile.\nDo you own Minecraft Java Edition on this account?");

        var profile = JsonSerializer.Deserialize<JsonElement>(profileBody);
        return new AuthResult
        {
            Username = GetStr(profile, "name"),
            Uuid = GetStr(profile, "id"),
            AccessToken = mcToken,
            UserType = "msa"
        };
    }

    public static async Task<bool> ValidateTokenAsync(string accessToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var resp = await Http.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static async Task<JsonElement> PostJsonAsync(string url, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await Http.PostAsync(url, content);
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{new Uri(url).Host} returned {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");

        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    private static string GetStr(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var val))
            throw new Exception($"Missing '{prop}' in API response");
        return val.GetString() ?? throw new Exception($"'{prop}' is null in API response");
    }
}

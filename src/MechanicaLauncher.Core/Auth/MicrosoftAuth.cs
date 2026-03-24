using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MechanicaLauncher.Core.Auth;

public sealed class MicrosoftAuth
{
    private static readonly HttpClient Http = new();
    private const string ClientId = "00000000402b5328";
    private const string Scope = "service::user.auth.xboxlive.com::MBI_SSL";

    public sealed class DeviceCodeInfo
    {
        public string UserCode { get; set; } = "";
        public string VerificationUri { get; set; } = "";
        public string DeviceCode { get; set; } = "";
        public int Interval { get; set; } = 5;
    }

    public async Task<DeviceCodeInfo> RequestDeviceCodeAsync()
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope,
            ["response_type"] = "device_code"
        });

        var resp = await Http.PostAsync("https://login.live.com/oauth20_connect.srf", content);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Failed to request device code: {resp.StatusCode}");

        var json = await resp.Content.ReadAsStringAsync();
        var data = JsonSerializer.Deserialize<JsonElement>(json);

        return new DeviceCodeInfo
        {
            UserCode = GetString(data, "user_code"),
            VerificationUri = GetString(data, "verification_uri"),
            DeviceCode = GetString(data, "device_code"),
            Interval = data.TryGetProperty("interval", out var i) ? i.GetInt32() : 5
        };
    }

    public async Task<AuthResult> PollForTokenAsync(DeviceCodeInfo deviceCode, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(deviceCode.Interval * 1000, ct);

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["device_code"] = deviceCode.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            });

            var resp = await Http.PostAsync("https://login.live.com/oauth20_token.srf", content, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            var data = JsonSerializer.Deserialize<JsonElement>(json);

            if (data.TryGetProperty("error", out var error))
            {
                var errorStr = error.GetString();
                if (errorStr == "authorization_pending") continue;
                if (errorStr == "slow_down") { await Task.Delay(2000, ct); continue; }
                throw new Exception($"Auth error: {errorStr}");
            }

            if (!data.TryGetProperty("access_token", out var tokenProp))
                throw new Exception("No access_token in response");

            return await AuthenticateWithXboxAsync(tokenProp.GetString()!);
        }

        throw new OperationCanceledException();
    }

    private async Task<AuthResult> AuthenticateWithXboxAsync(string msToken)
    {
        // Xbox Live
        var xblResp = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", new
        {
            Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = $"d={msToken}" },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        });

        var xblToken = GetString(xblResp, "Token");
        var userHash = xblResp.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()
                       ?? throw new Exception("Missing uhs in Xbox Live response");

        // XSTS
        var xstsResp = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken } },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        });

        if (xstsResp.TryGetProperty("XErr", out var xErr))
            throw new Exception($"Xbox error {xErr.GetInt64()}: check your Xbox account");

        var xstsToken = GetString(xstsResp, "Token");

        // Minecraft Auth
        var mcResp = await PostJsonAsync(
            "https://api.minecraftservices.com/authentication/login_with_xbox",
            new { identityToken = $"XBL3.0 x={userHash};{xstsToken}" });

        var mcToken = GetString(mcResp, "access_token");

        // Minecraft Profile
        using var profileReq = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        profileReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcToken);
        var profileResp = await Http.SendAsync(profileReq);

        if (!profileResp.IsSuccessStatusCode)
            throw new Exception($"Failed to get MC profile: {profileResp.StatusCode}. Do you own Minecraft?");

        var profileJson = await profileResp.Content.ReadAsStringAsync();
        var profile = JsonSerializer.Deserialize<JsonElement>(profileJson);

        return new AuthResult
        {
            Username = GetString(profile, "name"),
            Uuid = GetString(profile, "id"),
            AccessToken = mcToken,
            UserType = "msa"
        };
    }

    private static async Task<JsonElement> PostJsonAsync(string url, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await Http.PostAsync(url, content);

        var respJson = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Request to {new Uri(url).Host} failed ({resp.StatusCode}): {respJson[..Math.Min(200, respJson.Length)]}");

        return JsonSerializer.Deserialize<JsonElement>(respJson);
    }

    private static string GetString(JsonElement data, string property)
    {
        if (!data.TryGetProperty(property, out var prop))
            throw new Exception($"Missing '{property}' in response");
        return prop.GetString() ?? throw new Exception($"'{property}' is null");
    }
}

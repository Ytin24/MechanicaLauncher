using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MechanicaLauncher.Core.Auth;

public sealed class MicrosoftAuth
{
    private static readonly HttpClient Http = new();
    private const string ClientId = "00000000402b5328";
    private const string AuthBase = "https://login.live.com";
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

        var resp = await Http.PostAsync($"{AuthBase}/oauth20_connect.srf", content);
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Device code request failed: {json[..Math.Min(300, json.Length)]}");

        var data = JsonSerializer.Deserialize<JsonElement>(json);
        return new DeviceCodeInfo
        {
            UserCode = GetString(data, "user_code"),
            VerificationUri = data.TryGetProperty("verification_uri", out var vu)
                ? vu.GetString() ?? "https://microsoft.com/devicelogin"
                : "https://microsoft.com/devicelogin",
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

            var resp = await Http.PostAsync($"{AuthBase}/oauth20_token.srf", content, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            var data = JsonSerializer.Deserialize<JsonElement>(json);

            if (data.TryGetProperty("error", out var error))
            {
                var err = error.GetString();
                if (err == "authorization_pending") continue;
                if (err == "slow_down") { await Task.Delay(5000, ct); continue; }
                if (err == "expired_token") throw new Exception("Code expired. Try again.");
                throw new Exception($"Auth error: {err}");
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
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = msToken
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        });

        var xblToken = GetString(xblResp, "Token");
        var uhs = xblResp.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()
                  ?? throw new Exception("Missing uhs");

        // XSTS
        var xstsResp = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xblToken }
            },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        });

        if (xstsResp.TryGetProperty("XErr", out var xErr))
        {
            var code = xErr.GetInt64();
            var msg = code switch
            {
                2148916233 => "This Microsoft account has no Xbox account. Sign up at xbox.com first.",
                2148916235 => "Xbox Live is not available in your country.",
                2148916238 => "This account belongs to someone under 18. Add it to a Family.",
                _ => $"Xbox error {code}"
            };
            throw new Exception(msg);
        }

        var xstsToken = GetString(xstsResp, "Token");

        // Minecraft Auth
        var mcResp = await PostJsonAsync(
            "https://api.minecraftservices.com/authentication/login_with_xbox",
            new { identityToken = $"XBL3.0 x={uhs};{xstsToken}" });

        var mcToken = GetString(mcResp, "access_token");

        // Minecraft Profile
        using var profileReq = new HttpRequestMessage(HttpMethod.Get,
            "https://api.minecraftservices.com/minecraft/profile");
        profileReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcToken);
        var profileResp = await Http.SendAsync(profileReq);
        var profileJson = await profileResp.Content.ReadAsStringAsync();

        if (!profileResp.IsSuccessStatusCode)
            throw new Exception("Failed to get MC profile. Do you own Minecraft Java Edition?");

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
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await Http.PostAsync(url, content);
        var respBody = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            var host = new Uri(url).Host;
            throw new Exception($"Request to {host} failed ({resp.StatusCode}): {respBody[..Math.Min(200, respBody.Length)]}");
        }

        return JsonSerializer.Deserialize<JsonElement>(respBody);
    }

    private static string GetString(JsonElement data, string prop)
    {
        if (!data.TryGetProperty(prop, out var val))
            throw new Exception($"Missing '{prop}' in response");
        return val.GetString() ?? throw new Exception($"'{prop}' is null");
    }
}

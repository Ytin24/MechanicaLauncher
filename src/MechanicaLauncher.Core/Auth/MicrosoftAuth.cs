using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MechanicaLauncher.Core.Auth;

public sealed class MicrosoftAuth
{
    private static readonly HttpClient Http = new();
    private const string ClientId = "c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb";
    private const string AuthBase = "https://login.microsoftonline.com/consumers/oauth2/v2.0";
    private const string Scope = "XboxLive.SignIn XboxLive.offline_access";

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
            ["scope"] = Scope
        });

        var resp = await Http.PostAsync($"{AuthBase}/devicecode", content);
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Device code request failed: {json[..Math.Min(300, json.Length)]}");

        var data = JsonSerializer.Deserialize<JsonElement>(json);
        return new DeviceCodeInfo
        {
            UserCode = GetStr(data, "user_code"),
            VerificationUri = data.TryGetProperty("verification_uri", out var vu)
                ? vu.GetString() ?? "https://microsoft.com/devicelogin"
                : "https://microsoft.com/devicelogin",
            DeviceCode = GetStr(data, "device_code"),
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

            var resp = await Http.PostAsync($"{AuthBase}/token", content, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            var data = JsonSerializer.Deserialize<JsonElement>(json);

            if (data.TryGetProperty("error", out var error))
            {
                var err = error.GetString();
                if (err == "authorization_pending") continue;
                if (err == "slow_down") { await Task.Delay(5000, ct); continue; }
                if (err == "expired_token") throw new Exception("Code expired — try again.");
                throw new Exception($"Auth error: {err}");
            }

            if (!data.TryGetProperty("access_token", out var tokenProp))
                throw new Exception("No access_token in Microsoft response");

            return await ExchangeForMinecraftAsync(tokenProp.GetString()!);
        }

        throw new OperationCanceledException();
    }

    private async Task<AuthResult> ExchangeForMinecraftAsync(string msaToken)
    {
        // 1. Xbox Live User Token
        var xblBody = new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = $"d={msaToken}"
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        };
        var xbl = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", xblBody);
        var xblToken = GetStr(xbl, "Token");
        var uhs = xbl.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()
                  ?? throw new Exception("Missing user hash from Xbox Live");

        // 2. XSTS Token
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

        // 3. Minecraft Services Login
        var mcBody = new { identityToken = $"XBL3.0 x={uhs};{xstsToken}" };
        var mc = await PostJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox", mcBody);
        var mcToken = GetStr(mc, "access_token");

        // 4. Minecraft Profile
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

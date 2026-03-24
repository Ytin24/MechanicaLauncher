using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

        var resp = await Http.PostAsync(
            "https://login.live.com/oauth20_connect.srf", content);
        var json = await resp.Content.ReadAsStringAsync();
        var data = JsonSerializer.Deserialize<JsonElement>(json);

        return new DeviceCodeInfo
        {
            UserCode = data.GetProperty("user_code").GetString() ?? "",
            VerificationUri = data.GetProperty("verification_uri").GetString() ?? "",
            DeviceCode = data.GetProperty("device_code").GetString() ?? "",
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

            var resp = await Http.PostAsync(
                "https://login.live.com/oauth20_token.srf", content, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            var data = JsonSerializer.Deserialize<JsonElement>(json);

            if (data.TryGetProperty("error", out var error))
            {
                if (error.GetString() == "authorization_pending") continue;
                throw new Exception($"Auth error: {error.GetString()}");
            }

            var msToken = data.GetProperty("access_token").GetString()!;
            return await AuthenticateWithXboxAsync(msToken);
        }

        throw new OperationCanceledException();
    }

    private async Task<AuthResult> AuthenticateWithXboxAsync(string msToken)
    {
        // Xbox Live
        var xblPayload = new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = $"d={msToken}"
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        };

        var xblResp = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", xblPayload);
        var xblToken = xblResp.GetProperty("Token").GetString()!;
        var userHash = xblResp.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()!;

        // XSTS
        var xstsPayload = new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xblToken }
            },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        };

        var xstsResp = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", xstsPayload);
        var xstsToken = xstsResp.GetProperty("Token").GetString()!;

        // Minecraft
        var mcPayload = new { identityToken = $"XBL3.0 x={userHash};{xstsToken}" };
        var mcResp = await PostJsonAsync(
            "https://api.minecraftservices.com/authentication/login_with_xbox", mcPayload);
        var mcToken = mcResp.GetProperty("access_token").GetString()!;

        // Profile
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", mcToken);
        var profileResp = await Http.GetAsync("https://api.minecraftservices.com/minecraft/profile");
        Http.DefaultRequestHeaders.Authorization = null;

        var profileJson = await profileResp.Content.ReadAsStringAsync();
        var profile = JsonSerializer.Deserialize<JsonElement>(profileJson);

        return new AuthResult
        {
            Username = profile.GetProperty("name").GetString() ?? "Player",
            Uuid = profile.GetProperty("id").GetString() ?? "0",
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
        return JsonSerializer.Deserialize<JsonElement>(respJson);
    }
}

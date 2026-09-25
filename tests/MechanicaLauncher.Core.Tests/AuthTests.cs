using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using MechanicaLauncher.Core.Auth;
using MechanicaLauncher.Core.Localization;

internal static class AuthTests
{
    private const string ClientId = "be8d608a-0f8d-4c9d-91e2-27f0952658db";

    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        Locale.Init("en");
        await check("Default sign-in uses legacy desktop authorization with state and PKCE", () =>
        {
            foreach (var clientId in new[] { null, "", " ", MicrosoftAuth.LegacyClientId })
            {
                var request = new MicrosoftAuth(clientId).BeginSignIn();
                var uri = new Uri(request.AuthorizeUrl);
                var query = HttpUtility.ParseQueryString(uri.Query);
                Require(uri.GetLeftPart(UriPartial.Path) == "https://login.live.com/oauth20_authorize.srf");
                Require(query["client_id"] == MicrosoftAuth.LegacyClientId);
                Require(query["redirect_uri"] == "https://login.live.com/oauth20_desktop.srf");
                Require(query["scope"] == "service::user.auth.xboxlive.com::MBI_SSL");
                Require(!string.IsNullOrEmpty(query["state"]) && query["code_challenge_method"] == "S256");
                Require(!string.IsNullOrEmpty(query["code_challenge"]));
            }
            return Task.CompletedTask;
        });

        await check("New sign-in uses the configured client and rejects malformed or empty GUIDs", async () =>
        {
            var previous = Environment.GetEnvironmentVariable("MECHANICA_MICROSOFT_CLIENT_ID");
            try
            {
                Environment.SetEnvironmentVariable("MECHANICA_MICROSOFT_CLIENT_ID", ClientId);
                var request = MicrosoftAuth.CreateForSignIn().BeginSignIn();
                var query = HttpUtility.ParseQueryString(new Uri(request.AuthorizeUrl).Query);
                Require(query["client_id"] == ClientId);
                Require(query["redirect_uri"] == "https://login.microsoftonline.com/common/oauth2/nativeclient");
                foreach (var invalid in new[] { "not-an-id", Guid.Empty.ToString() })
                {
                    Environment.SetEnvironmentVariable("MECHANICA_MICROSOFT_CLIENT_ID", invalid);
                    var error = await Expect<InvalidOperationException>(() =>
                    {
                        MicrosoftAuth.CreateForSignIn();
                        return Task.CompletedTask;
                    });
                    Require(error.Message == Locale.Get("acc.client_invalid"));
                }
            }
            finally { Environment.SetEnvironmentVariable("MECHANICA_MICROSOFT_CLIENT_ID", previous); }
        });

        await check("OAuth requests bind a unique state and S256 challenge to each sign-in", () =>
        {
            var auth = new MicrosoftAuth(ClientId);
            var first = auth.BeginSignIn();
            var second = auth.BeginSignIn();
            var query = HttpUtility.ParseQueryString(new Uri(first.AuthorizeUrl).Query);
            Require(query["state"] != HttpUtility.ParseQueryString(new Uri(second.AuthorizeUrl).Query)["state"]);
            Require(query["code_challenge_method"] == "S256");
            Require(query["code_challenge"] == Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(first.CodeVerifier)))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_'));
            Require(query["scope"] == "XboxLive.signin offline_access");
            Require(!first.AuthorizeUrl.Contains(first.CodeVerifier, StringComparison.Ordinal));
            return Task.CompletedTask;
        });

        await check("OAuth rejects wrong origin, prefix callbacks, missing or duplicate state and code", async () =>
        {
            using var handler = new AuthHttp((_, _) => throw new Exception("Invalid callback reached network"));
            using var http = new HttpClient(handler);
            var auth = new MicrosoftAuth(http, ClientId);
            foreach (var change in new Func<string, string>[]
            {
                url => url.Replace("login.microsoftonline.com", "login.microsoftonline.com.example.test"),
                url => url.Replace("nativeclient?", "nativeclient/other?"),
                url => url.Replace("https://", "http://"),
                url => url.Replace("state=", "missing="),
                url => url + "&state=another",
                url => url + "&code=another",
            })
            {
                var request = auth.BeginSignIn();
                await Expect<InvalidOperationException>(() => auth.CompleteAsync(request, change(Callback(request))));
            }
            Require(handler.Calls == 0);
        });

        await check("Legacy and custom Microsoft sign-in exchange PKCE codes through Xbox and Minecraft", async () =>
        {
            foreach (var clientId in new[] { MicrosoftAuth.LegacyClientId, ClientId })
            {
                var legacy = clientId == MicrosoftAuth.LegacyClientId;
                MicrosoftSignInRequest? signIn = null;
                using var handler = new AuthHttp(async request =>
                {
                    var path = request.RequestUri!.AbsolutePath;
                    if (path.EndsWith("/token") || path.EndsWith("token.srf"))
                    {
                        Require(request.RequestUri.Host == (legacy ? "login.live.com" : "login.microsoftonline.com"));
                        var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync());
                        Require(form["client_id"] == clientId && form["code_verifier"] == signIn!.CodeVerifier && form["code"] == "test-code");
                        Require(form["redirect_uri"] == (legacy
                            ? "https://login.live.com/oauth20_desktop.srf"
                            : "https://login.microsoftonline.com/common/oauth2/nativeclient"));
                    }
                    if (path == "/user/authenticate")
                        Require((await request.Content!.ReadAsStringAsync()).Contains(legacy ? "t=ms-token" : "d=ms-token", StringComparison.Ordinal));
                    if (path == "/authentication/login_with_xbox")
                        Require((await request.Content!.ReadAsStringAsync()).Contains("XBL3.0 x=xsts-user;xsts-token", StringComparison.Ordinal));
                    if (path == "/minecraft/profile") Require(request.Headers.Authorization?.Parameter == "mc-token");
                    return Success(request);
                });
                using var http = new HttpClient(handler);
                var auth = new MicrosoftAuth(http, clientId);
                signIn = auth.BeginSignIn();
                var result = await auth.CompleteAsync(signIn, Callback(signIn));
                Require(result.Username == "TestPlayer" && result.IsOnline && result.RefreshToken == "rotated-refresh");
                Require(result.Uuid == "0123456789abcdef0123456789abcdef" && handler.Calls == 5);
                await Expect<InvalidOperationException>(() => auth.CompleteAsync(signIn, Callback(signIn)));
                Require(handler.Calls == 5);
            }
        });

        await check("Legacy refresh keeps its client and preserves token when provider omits rotation", async () =>
        {
            using var handler = new AuthHttp(async request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("token.srf"))
                {
                    var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync());
                    Require(form["client_id"] == MicrosoftAuth.LegacyClientId && form["refresh_token"] == "saved-refresh");
                    return Json(new { access_token = "ms-token" });
                }
                if (request.RequestUri.AbsolutePath == "/user/authenticate")
                    Require((await request.Content!.ReadAsStringAsync()).Contains("t=ms-token", StringComparison.Ordinal));
                return Success(request);
            });
            using var http = new HttpClient(handler);
            var result = await new MicrosoftAuth(http).RefreshAsync("saved-refresh");
            Require(result.RefreshToken == "saved-refresh" && result.Username == "TestPlayer");
        });

        await check("Xbox HTTP 401 errors explain missing profile and family restrictions", async () =>
        {
            foreach (var (code, key) in new[] { (2148916233L, "acc.no_xbox_profile"), (2148916235L, "acc.xbox_region"), (2148916238L, "acc.xbox_family") })
            {
                using var handler = new AuthHttp(request => request.RequestUri!.Host == "xsts.auth.xboxlive.com"
                    ? Json(new { XErr = code }, HttpStatusCode.Unauthorized) : Success(request));
                using var http = new HttpClient(handler);
                var error = await Expect<InvalidOperationException>(() => new MicrosoftAuth(http, ClientId).RefreshAsync("refresh"));
                Require(error.Message == Locale.Get(key) && handler.Calls == 3);
            }
        });

        await check("Rejected refresh and app registration report actionable errors without response secrets", async () =>
        {
            foreach (var (code, key) in new[] { ("invalid_grant", "acc.session_expired"), ("invalid_client", "acc.client_rejected"), ("Invalid app registration", "acc.client_rejected") })
            {
                using var handler = new AuthHttp(_ => Json(new { error = code, error_description = "private-response-token" }, HttpStatusCode.BadRequest));
                using var http = new HttpClient(handler);
                var error = await Expect<HttpRequestException>(() => new MicrosoftAuth(http, ClientId).RefreshAsync("refresh"));
                Require(error.Message == Locale.Get(key) && !error.ToString().Contains("private-response-token", StringComparison.Ordinal));
            }
        });

        await check("Minecraft missing profile explains Java Edition requirement", async () =>
        {
            using var handler = new AuthHttp(request => request.RequestUri!.AbsolutePath == "/minecraft/profile"
                ? Json(new { error = "NOT_FOUND" }, HttpStatusCode.NotFound) : Success(request));
            using var http = new HttpClient(handler);
            var error = await Expect<InvalidOperationException>(() => new MicrosoftAuth(http, ClientId).RefreshAsync("refresh"));
            Require(error.Message == Locale.Get("acc.no_java_profile"));
        });

        await check("Cancellation reaches Xbox request and prevents later Minecraft calls", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            using var handler = new AuthHttp(async (request, ct) =>
            {
                if (request.RequestUri!.Host == "user.auth.xboxlive.com")
                {
                    cancellation.Cancel();
                    await Task.Delay(Timeout.Infinite, ct);
                }
                return Success(request);
            });
            using var http = new HttpClient(handler);
            await Expect<OperationCanceledException>(() => new MicrosoftAuth(http, ClientId).RefreshAsync("refresh", cancellation.Token));
            Require(handler.Calls == 2);
        });

        await check("Session validation distinguishes expired tokens from service outage", async () =>
        {
            foreach (var status in new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized, HttpStatusCode.ServiceUnavailable })
            {
                using var handler = new AuthHttp(_ => Json(new { name = "TestPlayer", id = "test" }, status));
                using var http = new HttpClient(handler);
                var auth = new MicrosoftAuth(http, ClientId);
                if (status == HttpStatusCode.ServiceUnavailable) await Expect<HttpRequestException>(() => auth.ValidateAsync("token"));
                else Require(await auth.ValidateAsync("token") == (status == HttpStatusCode.OK));
            }
        });
    }

    private static string Callback(MicrosoftSignInRequest request)
    {
        var query = HttpUtility.ParseQueryString(new Uri(request.AuthorizeUrl).Query);
        return query["redirect_uri"] + "?code=test-code&state=" + query["state"];
    }

    private static HttpResponseMessage Success(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        "/consumers/oauth2/v2.0/token" or "/oauth20_token.srf" => Json(new { access_token = "ms-token", refresh_token = "rotated-refresh" }),
        "/user/authenticate" => Json(new { Token = "xbl-token", DisplayClaims = new { xui = new[] { new { uhs = "xbl-user" } } } }),
        "/xsts/authorize" => Json(new { Token = "xsts-token", DisplayClaims = new { xui = new[] { new { uhs = "xsts-user" } } } }),
        "/authentication/login_with_xbox" => Json(new { access_token = "mc-token" }),
        "/minecraft/profile" => Json(new { name = "TestPlayer", id = "0123456789abcdef0123456789abcdef" }),
        _ => throw new Exception("Unexpected auth endpoint")
    };

    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private static void Require(bool condition) { if (!condition) throw new Exception("Auth assertion failed"); }

    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new Exception($"Expected {typeof(T).Name}");
    }

    private sealed class AuthHttp : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public int Calls { get; private set; }
        public AuthHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : this((request, _) => Task.FromResult(respond(request))) { }
        public AuthHttp(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : this((request, _) => respond(request)) { }
        public AuthHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return _respond(request, cancellationToken);
        }
    }
}

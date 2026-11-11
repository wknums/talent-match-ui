using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Identity.Client;

// Spike S5 — long-running on-behalf-of with MSAL.NET (T008A, .NET half).
//
//   login <user> [--interactive]   sign the test user in to the "client" app, exchange that API token through
//                                  InitiateLongRunningProcessInWebApi, store session key + token cache, call /me once
//   probe <user> [--cached]        AcquireTokenInLongRunningProcess in a fresh process (no user, no request context);
//                                  forces a refresh at Entra unless --cached; calls /me; appends a JSONL record
//   watch <user> <minutes> <hours> probe on an interval
//   show                           print every recorded probe
//
// Configuration comes from environment variables written by infra/scripts/lib/spike-s5.sh (s5_provision).
// No token value is ever printed or logged; only outcomes, error codes and timings.

var cfg = Config.Load();
var command = args.Length > 0 ? args[0] : "help";

try
{
    return command switch
    {
        "login" => await Spike.Login(cfg, args[1], args.Contains("--interactive"), args.Contains("--dump-assertion")),
        "probe" => await Spike.Probe(cfg, args[1], force: !args.Contains("--cached")),
        "watch" => await Spike.Watch(cfg, args[1], double.Parse(args[2]), double.Parse(args[3])),
        "show" => Spike.Show(cfg),
        _ => Spike.Help(),
    };
}
catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or FormatException)
{
    Console.Error.WriteLine("Bad arguments. " + ex.Message);
    return Spike.Help();
}

sealed record Config(string TenantId, string ApiClientId, string ClientId, string PfxPath, string PfxPassword, string StateDir)
{
    public string ApiScope => $"api://{ApiClientId}/access_as_user";
    public string Authority => $"https://login.microsoftonline.com/{TenantId}";

    public static Config Load()
    {
        static string Need(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v
            ? v : throw new ArgumentException($"{name} is not set (run s5_provision, then s5_load).");
        var dir = Environment.GetEnvironmentVariable("S5_STATE_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".talent-match", "s5");
        Directory.CreateDirectory(dir);
        return new(Need("S5_TENANT_ID"), Need("S5_API_CLIENT_ID"), Need("S5_CLIENT_ID"),
            Need("S5_API_CERT_PATH"), Need("S5_API_CERT_PASSWORD"), dir);
    }
}

sealed record SessionState(string SessionKey, string Upn, DateTimeOffset StartedUtc);

sealed record ProbeRecord(
    DateTimeOffset Utc, string User, double MinutesSinceLogin, string Outcome, string? ErrorCode, string? SubError,
    string? Classification, string? Aadsts, int? HttpStatus, bool ClaimsChallenge, string? TokenSource,
    bool Forced, int? GraphStatus, string? Detail);

static class Spike
{
    // The scopes the real feature will use, plus User.Read so a harmless /me call proves the token works.
    static readonly string[] GraphScopes =
    [
        "https://graph.microsoft.com/User.Read",
        "https://graph.microsoft.com/Files.Read.All",
        "https://graph.microsoft.com/Sites.Read.All",
    ];

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    static string StatePath(Config c, string user) => Path.Combine(c.StateDir, $"{user}.state.json");
    static string CachePath(Config c, string user) => Path.Combine(c.StateDir, $"{user}.cache.bin");
    static string ResultsPath(Config c) => Path.Combine(c.StateDir, "results.jsonl");

    // The "API": a confidential client authenticating with a certificate (production uses a federated credential)
    // and persisting its token cache between processes (production persists it, encrypted, in the database).
    static IConfidentialClientApplication BuildApi(Config c, string user)
    {
        var cert = X509CertificateLoader.LoadPkcs12FromFile(c.PfxPath, c.PfxPassword);
        var app = ConfidentialClientApplicationBuilder.Create(c.ApiClientId)
            .WithAuthority(c.Authority)
            .WithCertificate(cert)
            .Build();
        var path = CachePath(c, user);
        app.UserTokenCache.SetBeforeAccess(a =>
        {
            if (File.Exists(path)) a.TokenCache.DeserializeMsalV3(File.ReadAllBytes(path));
        });
        app.UserTokenCache.SetAfterAccess(a =>
        {
            if (a.HasStateChanged) File.WriteAllBytes(path, a.TokenCache.SerializeMsalV3());
        });
        return app;
    }

    static (string Upn, string Password) User(Config c, string user)
    {
        var users = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
            File.ReadAllText(Path.Combine(c.StateDir, "users.json")))!;
        return (users[user]["upn"], users[user]["password"]);
    }

    // Kept synchronous: InitiateLongRunningProcessInWebApi takes the session key by reference.
    static AcquireTokenOnBehalfOfParameterBuilder Initiate(IConfidentialClientApplication app, string userToken, ref string? key) =>
        ((ILongRunningWebApi)app).InitiateLongRunningProcessInWebApi(GraphScopes, userToken, ref key);

    public static async Task<int> Login(Config c, string user, bool interactive, bool dumpAssertion = false)
    {
        var (upn, password) = User(c, user);
        var pub = PublicClientApplicationBuilder.Create(c.ClientId)
            .WithAuthority(c.Authority)
            .WithRedirectUri("http://localhost")
            .Build();

        AuthenticationResult userAuth;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        if (interactive)
        {
            Console.WriteLine($"[{user}] a browser window opens now. Sign in as {upn} (choose 'Use another account'); enter the Temporary Access Pass when asked.");
            userAuth = await pub.AcquireTokenInteractive([c.ApiScope]).WithLoginHint(upn).WithPrompt(Prompt.ForceLogin).ExecuteAsync(cts.Token);
        }
        else
        {
#pragma warning disable CS0618 // password grant is used only for dedicated, MFA-free spike users
            userAuth = await pub.AcquireTokenByUsernamePassword([c.ApiScope], upn, password).ExecuteAsync();
#pragma warning restore CS0618
        }

        Console.WriteLine($"[{user}] signed in as {upn}; API token expires {userAuth.ExpiresOn:u}");

        // Hands the same incoming API token to the Node harness (the "request" a Node API would have received).
        if (dumpAssertion)
        {
            File.WriteAllText(Path.Combine(c.StateDir, $"{user}.assertion"), userAuth.AccessToken);
            Console.WriteLine($"[{user}] API token written for the Node harness (valid until {userAuth.ExpiresOn:u})");
        }

        // The cache holds only this one session; start clean so a re-login cannot reuse an earlier session.
        if (File.Exists(CachePath(c, user))) File.Delete(CachePath(c, user));
        var api = BuildApi(c, user);
        string? sessionKey = null;
        var result = await Initiate(api, userAuth.AccessToken, ref sessionKey).ExecuteAsync();

        var started = DateTimeOffset.UtcNow;
        File.WriteAllText(StatePath(c, user), JsonSerializer.Serialize(new SessionState(sessionKey!, upn, started), Json));
        Console.WriteLine($"[{user}] long-running session started (key length {sessionKey!.Length}); Graph token expires {result.ExpiresOn:u}");

        var status = await CallGraph(result.AccessToken);
        Console.WriteLine($"[{user}] Graph /me with the initial token: HTTP {status}");
        Record(c, new(started, user, 0, status == 200 ? "ok" : "graph_error", null, null, null, null, null, false,
            result.AuthenticationResultMetadata.TokenSource.ToString(), false, status, "login"));
        return status == 200 ? 0 : 1;
    }

    public static async Task<int> Probe(Config c, string user, bool force)
    {
        var state = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(StatePath(c, user)))!;
        var minutes = Math.Round((DateTimeOffset.UtcNow - state.StartedUtc).TotalMinutes, 1);
        var api = BuildApi(c, user);
        ProbeRecord rec;
        try
        {
            var builder = ((ILongRunningWebApi)api).AcquireTokenInLongRunningProcess(GraphScopes, state.SessionKey);
            if (force) builder = builder.WithForceRefresh(true);
            var result = await builder.ExecuteAsync();
            var graph = await CallGraph(result.AccessToken);
            rec = new(DateTimeOffset.UtcNow, user, minutes, graph == 200 ? "ok" : "graph_error", null, null, null, null, null, false,
                result.AuthenticationResultMetadata.TokenSource.ToString(), force, graph, $"expires {result.ExpiresOn:u}");
        }
        catch (MsalUiRequiredException e)
        {
            rec = new(DateTimeOffset.UtcNow, user, minutes, "interaction_required", e.ErrorCode, null, e.Classification.ToString(),
                Aadsts(e.Message), null, !string.IsNullOrEmpty(e.Claims), null, force, null, Short(e.Message));
        }
        catch (MsalServiceException e)
        {
            rec = new(DateTimeOffset.UtcNow, user, minutes, e.IsRetryable ? "transient_service_error" : "service_error", e.ErrorCode, null,
                null, Aadsts(e.Message), e.StatusCode, !string.IsNullOrEmpty(e.Claims), null, force, null, Short(e.Message));
        }
        catch (Exception e)
        {
            rec = new(DateTimeOffset.UtcNow, user, minutes, "error", e.GetType().Name, null, null, null, null, false, null, force, null, Short(e.Message));
        }

        Record(c, rec);
        Console.WriteLine($"{rec.Utc:u} [{user}] +{minutes} min  {rec.Outcome}  {rec.ErrorCode} {rec.SubError} {rec.Aadsts} {rec.TokenSource} {(rec.GraphStatus is { } g ? "graph=" + g : "")}".TrimEnd());
        return rec.Outcome == "ok" ? 0 : 2;
    }

    public static async Task<int> Watch(Config c, string user, double everyMinutes, double forHours)
    {
        var until = DateTimeOffset.UtcNow.AddHours(forHours);
        while (DateTimeOffset.UtcNow < until)
        {
            await Probe(c, user, force: true);
            await Task.Delay(TimeSpan.FromMinutes(everyMinutes));
        }
        return 0;
    }

    public static int Show(Config c)
    {
        if (!File.Exists(ResultsPath(c))) { Console.WriteLine("No probes recorded yet."); return 0; }
        foreach (var line in File.ReadLines(ResultsPath(c)))
        {
            var r = JsonSerializer.Deserialize<ProbeRecord>(line)!;
            Console.WriteLine($"{r.Utc:u}  {r.User,-12} +{r.MinutesSinceLogin,8} min  {r.Outcome,-22} {r.ErrorCode} {r.SubError} {r.Aadsts} {r.Detail}");
        }
        return 0;
    }

    public static int Help()
    {
        Console.WriteLine("S5.LongRunningObo: login <user> [--interactive] | probe <user> [--cached] | watch <user> <everyMinutes> <hours> | show");
        return 1;
    }

    static async Task<int> CallGraph(string accessToken)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.GetAsync("https://graph.microsoft.com/v1.0/me?$select=id");
        return (int)response.StatusCode;
    }

    static void Record(Config c, ProbeRecord r) => File.AppendAllText(ResultsPath(c), JsonSerializer.Serialize(r, Json) + Environment.NewLine);

    static string? Aadsts(string message) => Regex.Match(message, @"AADSTS\d+") is { Success: true } m ? m.Value : null;

    static string Short(string message) => message.ReplaceLineEndings(" ")[..Math.Min(message.Length, 240)];
}

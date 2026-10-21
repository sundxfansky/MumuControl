using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MumuControl;
using MumuControl.Core;

// Uses a fake emulator, isolated data directory, and an in-memory startup registry.
// No emulator controls, Windows trust changes, firewall changes, or real startup registrations.
var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MumuControl.SecurityTests"));
Directory.CreateDirectory(root);
var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL " + name);
    checks++;
    Console.WriteLine("PASS " + name);
}
int FreePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}
async Task<bool> Listening(int port)
{
    using var tcp = new TcpClient();
    try { await tcp.ConnectAsync(IPAddress.Loopback, port); return true; }
    catch (SocketException) { return false; }
}
async Task<JsonObject> Json(HttpResponseMessage response) =>
    (JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject) ?? throw new InvalidDataException("Invalid JSON");
HttpClient Client(string url, CookieContainer? cookies = null)
{
    var handler = new HttpClientHandler { CookieContainer = cookies ?? new CookieContainer(), UseProxy = false };
    return new HttpClient(handler) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(15) };
}
async Task<HttpResponseMessage> Write(HttpClient client, string method, string path, object body, string? token = null, string? origin = null)
{
    var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(body) };
    if (token is not null) request.Headers.Add("X-Mumu-Token", token);
    if (origin is not null) request.Headers.Add("Origin", origin);
    return await client.SendAsync(request);
}
try
{
    var port = FreePort();
    while (port == 65535 || await Listening(port + 1)) port = FreePort();
    var remotePort = port + 1;
    var client = new NoEmulator();
    await using var engine = new AppEngine(client, new StateStore(directory));
    var server = new LocalServer(engine, port, directory);
    await server.StartAsync();
    using var local = Client($"http://127.0.0.1:{port}");
    using var anonymous = Client($"http://127.0.0.1:{port}");
    const string username = "security_test";
    var password = "Test-only-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    var newPassword = "New-test-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    try
    {
        var bootstrap = await Json(await local.GetAsync("/api/bootstrap"));
        var setupToken = bootstrap["token"]!.GetValue<string>();
        Check(bootstrap["local"]!.GetValue<bool>() && !bootstrap["initialized"]!.GetValue<bool>(), "local setup only before initialization");
        Check(!await Listening(remotePort), "remote socket closed before initialization");
        foreach (var path in new[] { "/api/state", "/api/logs", "/api/settings", "/api/health", "/api/emulators/2/screenshot", "/api/startup" })
            Check((await anonymous.GetAsync(path)).StatusCode == HttpStatusCode.Unauthorized, "anonymous denied " + path);
        var asset = await anonymous.GetAsync("/");
        Check(asset.IsSuccessStatusCode && asset.Headers.CacheControl?.NoStore == true && asset.Headers.Contains("Content-Security-Policy"), "login shell with no-store and CSP");
        Check((await Write(local, "POST", "/api/auth/setup", new { username, password, remoteEnabled = false })).StatusCode == HttpStatusCode.Forbidden, "setup requires local CSRF token");
        Check((await Write(local, "POST", "/api/auth/setup", new { username, password, remoteEnabled = false }, setupToken, "https://evil.example")).StatusCode == HttpStatusCode.Forbidden, "cross-origin initialization denied");
        Check((await Write(local, "POST", "/api/auth/setup", new { username, password = "short", remoteEnabled = false }, setupToken)).StatusCode == HttpStatusCode.BadRequest, "weak password rejected");
        var setup = await Write(local, "POST", "/api/auth/setup", new { username, password, remoteEnabled = false }, setupToken);
        var status = await Json(setup);
        var token = status["token"]!.GetValue<string>();
        Check(setup.IsSuccessStatusCode && status["authenticated"]!.GetValue<bool>(), "local initialization creates session");
        Check(setup.Headers.GetValues("Set-Cookie").Single().Contains("httponly", StringComparison.OrdinalIgnoreCase), "local session cookie HttpOnly");
        Check((await Write(local, "POST", "/api/auth/setup", new { username, password }, setupToken)).StatusCode == HttpStatusCode.Conflict, "initialization is one time");
        Check((await local.GetAsync("/api/state")).IsSuccessStatusCode, "authenticated local state");
        Check((await Write(local, "POST", "/api/refresh", new { })).StatusCode == HttpStatusCode.Forbidden, "authenticated mutations require session CSRF");
        var forged = new HttpRequestMessage(HttpMethod.Get, "/api/bootstrap"); forged.Headers.Host = $"evil.example:{port}";
        Check((await anonymous.SendAsync(forged)).StatusCode == HttpStatusCode.Forbidden, "local DNS rebinding host rejected");
        var legacyCertificatePath = Path.Combine(directory, "security", "certificate.dpapi");
        Check(!File.Exists(legacyCertificatePath), "local initialization does not generate a certificate");
        File.WriteAllText(legacyCertificatePath, "legacy-invalid-certificate-must-be-ignored");
        var enabled = await Write(local, "PUT", "/api/auth/settings", new { remoteEnabled = true, remotePort }, token);
        status = await Json(enabled);
        Check(enabled.IsSuccessStatusCode && status["remoteEnabled"]!.GetValue<bool>() && await Listening(remotePort), "local admin enables HTTP listener");
        Check(File.ReadAllText(legacyCertificatePath) == "legacy-invalid-certificate-must-be-ignored" && !status.ContainsKey("certificateFingerprint"), "HTTP listener ignores legacy certificate and exposes no fingerprint");
        Check(status["remoteUrls"]!.AsArray().All(x => x!.GetValue<string>().StartsWith("http://")), "direct remote URLs use HTTP");
        using var remote = Client($"http://127.0.0.1:{remotePort}");
        var remoteStatus = await Json(await remote.GetAsync("/api/bootstrap"));
        Check(!remoteStatus["local"]!.GetValue<bool>() && remoteStatus["token"]!.GetValue<string>() == "", "remote HTTP listener never gains local administration even via loopback");
        Check((await remote.GetAsync("/api/state")).StatusCode == HttpStatusCode.Unauthorized, "remote cannot read devices before login");
        var spoofed = new HttpRequestMessage(HttpMethod.Post, "/api/auth/setup") { Content = JsonContent.Create(new { username, password }) };
        spoofed.Headers.Add("X-Mumu-Token", setupToken); spoofed.Headers.Add("X-Forwarded-For", "127.0.0.1");
        Check((await remote.SendAsync(spoofed)).StatusCode == HttpStatusCode.Forbidden, "remote setup denied including forged forwarded address");
        var bad = await Write(remote, "POST", "/api/auth/login", new { username = "missing", password });
        Check(bad.StatusCode == HttpStatusCode.Unauthorized && (await Json(bad))["error"]!.GetValue<string>() == "账户或密码不正确。", "login failure does not enumerate accounts");
        var login = await Write(remote, "POST", "/api/auth/login", new { username, password });
        var remoteToken = (await Json(login))["token"]!.GetValue<string>();
        var cookie = login.Headers.GetValues("Set-Cookie").Single();
        Check(login.IsSuccessStatusCode && cookie.StartsWith("MumuRemoteSession=") && !cookie.Contains("secure", StringComparison.OrdinalIgnoreCase) && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) && cookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase), "direct HTTP remote login has separate host-only HttpOnly strict cookie");
        using (var copiedLocal = Client($"http://127.0.0.1:{remotePort}"))
        {
            var localCookieValue = setup.Headers.GetValues("Set-Cookie").Single().Split(';')[0].Split('=', 2)[1];
            copiedLocal.DefaultRequestHeaders.Add("Cookie", "MumuRemoteSession=" + localCookieValue);
            Check((await copiedLocal.GetAsync("/api/state")).StatusCode == HttpStatusCode.Unauthorized, "renaming local session cookie cannot authenticate remote listener");
        }
        using (var copiedRemote = Client($"http://127.0.0.1:{port}"))
        {
            var remoteCookieValue = cookie.Split(';')[0].Split('=', 2)[1];
            copiedRemote.DefaultRequestHeaders.Add("Cookie", "MumuLocalSession=" + remoteCookieValue);
            Check((await copiedRemote.GetAsync("/api/state")).StatusCode == HttpStatusCode.Unauthorized, "renaming remote session cookie cannot authenticate local listener");
        }
        var remoteState = await Json(await remote.GetAsync("/api/state"));
        Check(remoteState["settings"]!["managerPath"]!.GetValue<string>() == "" && remoteState["connection"]!["managerPath"] is null, "remote logged-in state omits local executable path");
        foreach (var path in new[] { "/api/auth/settings", "/api/startup", "/api/auth/certificate", "/api/settings" })
            Check((await remote.GetAsync(path)).StatusCode == HttpStatusCode.Forbidden, "remote local-only management denied " + path);
        Check((await Write(remote, "PUT", "/api/auth/credentials", new { username, password = newPassword, currentPassword = password }, remoteToken)).StatusCode == HttpStatusCode.Forbidden, "remote credential change denied");
        Check((await Write(remote, "PUT", "/api/settings", new AppSettings(), remoteToken)).StatusCode == HttpStatusCode.Forbidden, "remote executable path and service settings denied");
        Check((await Write(remote, "POST", "/api/refresh", new { }, remoteToken, "https://evil.example")).StatusCode == HttpStatusCode.Forbidden, "remote cross-origin mutation denied");
        Check((await Write(remote, "POST", "/api/refresh", new { }, token)).StatusCode == HttpStatusCode.Forbidden, "local CSRF token cannot authorize remote writes");
        var crossSite = new HttpRequestMessage(HttpMethod.Get, "/api/state"); crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        Check((await remote.SendAsync(crossSite)).StatusCode == HttpStatusCode.Forbidden, "cross-site remote request denied");
        var badHost = new HttpRequestMessage(HttpMethod.Get, "/api/bootstrap"); badHost.Headers.Host = $"evil.example:{remotePort}";
        Check((await remote.SendAsync(badHost)).StatusCode == HttpStatusCode.Forbidden, "remote DNS rebinding host rejected");
        var plainBody = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = new StringContent("{}") };
        Check((await remote.SendAsync(plainBody)).StatusCode == HttpStatusCode.UnsupportedMediaType, "non-JSON login rejected");
        var malformed = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = new StringContent("{", System.Text.Encoding.UTF8, "application/json") };
        Check((await remote.SendAsync(malformed)).StatusCode == HttpStatusCode.BadRequest, "malformed login JSON rejected");
        Check((await local.GetAsync("/api/auth/certificate")).StatusCode == HttpStatusCode.NotFound, "HTTP server exposes no certificate export");
        var startup = await Json(await local.GetAsync("/api/startup"));
        Check(startup.ContainsKey("supported"), "local startup status available without writing registry");
        var securityJson = File.ReadAllText(Path.Combine(directory, "security", "access.json"));
        Check(!securityJson.Contains(password) && securityJson.Contains("600000"), "salted password hash persisted without plaintext");
        var changed = await Write(local, "PUT", "/api/auth/credentials", new { username, password = newPassword, currentPassword = password }, token);
        status = await Json(changed); token = status["token"]!.GetValue<string>();
        Check(changed.IsSuccessStatusCode && (await remote.GetAsync("/api/state")).StatusCode == HttpStatusCode.Unauthorized, "local credential change invalidates remote sessions");
        var relogin = await Write(remote, "POST", "/api/auth/login", new { username, password = newPassword });
        remoteToken = (await Json(relogin))["token"]!.GetValue<string>();
        Check((await Write(remote, "POST", "/api/auth/logout", new { }, remoteToken)).IsSuccessStatusCode && (await remote.GetAsync("/api/state")).StatusCode == HttpStatusCode.Unauthorized, "logout invalidates session");
        foreach (var invalidOrigin in new[] { "ftp://mumu.example.com", "https://user:pass@mumu.example.com", "https://mumu.example.com/path", "https://mumu.example.com?query=1", "https://mumu.example.com#fragment", "https://模拟器.example.com" })
            Check((await Write(local, "PUT", "/api/access", new { remoteEnabled = true, remotePort, publicOrigin = invalidOrigin }, token)).StatusCode == HttpStatusCode.BadRequest, "invalid public origin rejected " + invalidOrigin);
        const string publicOrigin = "https://mumu.example.com";
        var configured = await Write(local, "PUT", "/api/access", new { remoteEnabled = true, remotePort, publicOrigin = publicOrigin + ":443/" }, token);
        var configuredStatus = await Json(configured);
        Check(configured.IsSuccessStatusCode && configuredStatus["publicOrigin"]!.GetValue<string>() == publicOrigin
            && configuredStatus["remoteUrls"]!.AsArray().Single()!.GetValue<string>() == publicOrigin, "public HTTPS origin canonicalizes default port and sets advertised URL");
        Check((await remote.GetAsync("/api/bootstrap")).StatusCode == HttpStatusCode.Forbidden, "configured public origin rejects direct remote IP host");
        using var proxy = Client($"http://127.0.0.1:{remotePort}");
        proxy.DefaultRequestHeaders.Host = "mumu.example.com";
        var proxyBootstrap = await Json(await proxy.GetAsync("/api/bootstrap"));
        Check(!proxyBootstrap["local"]!.GetValue<bool>() && proxyBootstrap["publicOrigin"]!.GetValue<string>() == "", "proxy preserves external Host without granting local privileges or revealing admin settings");
        var proxyLogin = await Write(proxy, "POST", "/api/auth/login", new { username, password = newPassword }, origin: publicOrigin);
        var proxyStatus = await Json(proxyLogin);
        var proxyToken = proxyStatus["token"]!.GetValue<string>();
        var proxyCookie = proxyLogin.Headers.GetValues("Set-Cookie").Single();
        Check(proxyLogin.IsSuccessStatusCode && proxyCookie.StartsWith("__Host-MumuSession=") && proxyCookie.Contains("secure", StringComparison.OrdinalIgnoreCase)
            && proxyCookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) && proxyCookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase), "HTTP backend accepts configured HTTPS origin and issues Secure host-only strict cookie");
        // The browser sends this Secure cookie to its HTTPS proxy; the proxy relays the
        // header on the backend HTTP socket. The test relays the same header directly.
        proxy.DefaultRequestHeaders.Add("Cookie", proxyCookie.Split(';')[0]);
        Check((await proxy.GetAsync("/api/state")).IsSuccessStatusCode, "reverse proxy relays authenticated cookie to backend HTTP");
        Check((await Write(proxy, "POST", "/api/refresh", new { }, proxyToken, "http://mumu.example.com")).StatusCode == HttpStatusCode.Forbidden, "public HTTPS origin rejects HTTP origin downgrade");
        var spoofedProxy = new HttpRequestMessage(HttpMethod.Post, "/api/auth/setup") { Content = JsonContent.Create(new { username, password }) };
        spoofedProxy.Headers.Add("X-Mumu-Token", setupToken);
        spoofedProxy.Headers.Add("X-Forwarded-For", "127.0.0.1"); spoofedProxy.Headers.Add("X-Forwarded-Proto", "https");
        spoofedProxy.Headers.Add("Forwarded", "for=127.0.0.1;proto=https;host=127.0.0.1");
        Check((await proxy.SendAsync(spoofedProxy)).StatusCode == HttpStatusCode.Forbidden, "proxy headers cannot authorize local account setup");
        var wrongProxyHost = new HttpRequestMessage(HttpMethod.Get, "/api/bootstrap");
        wrongProxyHost.Headers.Host = "evil.example"; wrongProxyHost.Headers.Add("X-Forwarded-Host", "mumu.example.com");
        Check((await proxy.SendAsync(wrongProxyHost)).StatusCode == HttpStatusCode.Forbidden, "forwarded host cannot bypass configured Host");
        var wrongProxyOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/refresh") { Content = JsonContent.Create(new { }) };
        wrongProxyOrigin.Headers.Add("X-Mumu-Token", proxyToken); wrongProxyOrigin.Headers.Add("Origin", "https://evil.example");
        wrongProxyOrigin.Headers.Add("X-Forwarded-Proto", "https"); wrongProxyOrigin.Headers.Add("X-Forwarded-Host", "mumu.example.com");
        Check((await proxy.SendAsync(wrongProxyOrigin)).StatusCode == HttpStatusCode.Forbidden, "forwarded headers cannot bypass configured Origin");
        var sameConfiguration = await Write(local, "PUT", "/api/access", new { remoteEnabled = true, remotePort, publicOrigin }, token);
        Check(sameConfiguration.IsSuccessStatusCode && (await proxy.GetAsync("/api/state")).StatusCode == HttpStatusCode.Unauthorized, "saving remote configuration revokes existing proxy sessions");
        var directConfiguration = await Write(local, "PUT", "/api/access", new { remoteEnabled = true, remotePort, publicOrigin = "" }, token);
        Check(directConfiguration.IsSuccessStatusCode && (await remote.GetAsync("/api/bootstrap")).IsSuccessStatusCode, "clearing public origin restores HTTP direct hosts");
        for (int i = 0; i < 5; i++)
            Check((await Write(remote, "POST", "/api/auth/login", new { username, password = "bad-password" })).StatusCode == HttpStatusCode.Unauthorized, "failed attempt " + (i + 1));
        var limited = await Write(remote, "POST", "/api/auth/login", new { username, password = newPassword });
        Check(limited.StatusCode == HttpStatusCode.TooManyRequests && limited.Headers.RetryAfter is not null, "per-IP password attack blocked after five failures");
        Check((await local.GetAsync("/api/state")).IsSuccessStatusCode, "existing local session survives remote login throttling");
        var disabled = await Write(local, "PUT", "/api/auth/settings", new { remoteEnabled = false, remotePort }, token);
        Check(disabled.IsSuccessStatusCode && !await Listening(remotePort), "local switch closes remote listener immediately");
        Check(client.ControlCalls == 0, "security verification performs zero emulator controls");
        Check(!File.ReadAllText(Path.Combine(directory, "security", "security.log")).Contains(newPassword), "security audit never logs passwords");
    }
    finally { await server.DisposeAsync(); }
    await using (var restarted = new LocalServer(engine, port, directory))
    {
        await restarted.StartAsync();
        using var recovered = Client($"http://127.0.0.1:{port}");
        var status = await Json(await recovered.GetAsync("/api/bootstrap"));
        Check(status["initialized"]!.GetValue<bool>() && !status["authenticated"]!.GetValue<bool>() && !status["remoteEnabled"]!.GetValue<bool>(), "restart retains credentials and disabled access but no sessions");
        Check((await Write(recovered, "POST", "/api/auth/login", new { username, password = newPassword })).IsSuccessStatusCode, "new password survives restart");
        using var stalePasswordClient = Client($"http://127.0.0.1:{port}");
        Check((await Write(stalePasswordClient, "POST", "/api/auth/login", new { username, password })).StatusCode == HttpStatusCode.Unauthorized, "old password remains rejected after restart");
    }
    var fakeRegistry = new MemoryRegistry();
    var fakeExe = Path.Combine(directory, "MuMuControl.exe"); File.WriteAllText(fakeExe, "test-file");
    var manager = new StartupManager(fakeExe, fakeRegistry);
    Check(!manager.GetStatus().Enabled && manager.SetEnabled(true).Enabled && fakeRegistry.Value as string == $"\"{fakeExe}\" --start-minimized", "startup quotes executable and starts in tray");
    Check(!manager.SetEnabled(false).Enabled && fakeRegistry.Value is null, "startup disable removes own value");
    fakeRegistry.Value = "externally modified";
    bool refused = false; try { manager.SetEnabled(false); } catch (InvalidOperationException) { refused = true; }
    Check(refused && fakeRegistry.Value as string == "externally modified", "startup preserves unexpected external entry");
    Check(!new StartupManager(Path.Combine(directory, "dotnet.exe"), fakeRegistry).GetStatus().Supported, "startup refuses development host");
    Console.WriteLine($"{checks}/{checks} security checks passed.");
}
finally
{
    var cleanup = Path.GetFullPath(directory);
    if (!cleanup.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup path.");
    Directory.Delete(cleanup, recursive: true);
}

sealed class MemoryRegistry : IStartupRegistry
{
    public object? Value { get; set; }
    public object? GetValue(string valueName) => Value;
    public void SetValue(string valueName, string value) => Value = value;
    public void DeleteValue(string valueName) => Value = null;
}
sealed class NoEmulator : IMuMuClient
{
    public int ControlCalls { get; private set; }
    public Task<IReadOnlyList<VmInfo>> ListAsync(string path, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<VmInfo>>([]);
    public Task ControlAsync(string path, int index, string action, CancellationToken ct = default) { ControlCalls++; throw new InvalidOperationException("No emulator controls in security tests."); }
    public Task<byte[]> ScreenshotAsync(string path, VmInfo vm, CancellationToken ct = default) => throw new NotSupportedException();
    public void Dispose() { }
}

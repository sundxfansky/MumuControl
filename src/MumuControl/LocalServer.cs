using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MumuControl.Core;

namespace MumuControl;

/// <summary>Local HTTP UI plus an independently managed, authenticated HTTP listener for reverse proxies.</summary>
public sealed class LocalServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly AppEngine _engine;
    private readonly AccessSecurity _access;
    // Separate budgets prevent slow remote uploads or password hashing from starving
    // the local emergency controls used to disable remote access.
    private readonly SemaphoreSlim _localRequests = new(16, 16);
    private readonly SemaphoreSlim _remoteRequests = new(8, 8);
    private readonly SemaphoreSlim _remoteGate = new(1, 1);
    private WebApplication? _remoteApp;
    private string? _remoteError;
    private bool _stopping;
    public Uri Address { get; }

    public LocalServer(AppEngine engine, int port, string dataDirectory)
    {
        _engine = engine;
        _access = new AccessSecurity(dataDirectory, port);
        Address = new Uri($"http://127.0.0.1:{port}/");
        if (_access.RemotePort == port) throw new ArgumentException("本机端口和远程端口不能相同。");
        _app = BuildServer(port, remote: false);
    }
    private WebApplication BuildServer(int port, bool remote)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 64 * 1024;
            options.Limits.MaxConcurrentConnections = 32;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(20);
            options.Limits.MaxRequestLineSize = 4096;
            options.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            if (remote)
                options.ListenAnyIP(port, listen => listen.Protocols = HttpProtocols.Http1);
            else options.Listen(IPAddress.Loopback, port, listen => listen.Protocols = HttpProtocols.Http1);
        });
        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options => options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        var app = builder.Build();
        app.Run(context => HandleAsync(context, remote));
        return app;
    }
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _app.StartAsync(ct);
        DiagnosticLog.Write("info", $"远程访问：{(_access.RemoteEnabled ? "已启用，端口 " + _access.RemotePort : "关闭")}");
        if (_access.RemoteEnabled)
        {
            try { await StartRemoteAsync(_access.RemotePort, ct); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                _remoteError = "远程 HTTP 端口启动失败，请在本机检查端口后重新启用。";
                _access.Audit(_remoteError, "127.0.0.1");
            }
        }
    }

    private async Task HandleAsync(HttpContext context, bool remote)
    {
        var response = context.Response;
        var request = context.Request;
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["X-Frame-Options"] = "DENY";
        response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob: data:; connect-src 'self'; font-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
        var ipAddress = context.Connection.RemoteIpAddress;
        if (ipAddress?.IsIPv4MappedToIPv6 == true) ipAddress = ipAddress.MapToIPv4();
        var ip = ipAddress?.ToString() ?? "unknown";
        // Local account administration trusts the actual socket, never proxy headers.
        bool local = !remote && !request.IsHttps && ipAddress is not null && IPAddress.IsLoopback(ipAddress)
            && context.Connection.LocalPort == Address.Port && request.Host.Host == "127.0.0.1" && request.Host.Port == Address.Port;
        if (!remote && !local || remote && (request.IsHttps || !_access.RemoteEnabled || !ValidRemoteHost(request.Host)))
        { await ErrorAsync(context, 403, "访问地址无效，或远程访问尚未启用。"); return; }
        string expectedOrigin = remote && _access.PublicOrigin.Length > 0 ? _access.PublicOrigin : "http://" + request.Host.Value;
        if (request.Headers.TryGetValue("Origin", out var origin) && !string.Equals(origin, expectedOrigin, StringComparison.OrdinalIgnoreCase))
        { await ErrorAsync(context, 403, "拒绝跨站请求。"); return; }
        if (request.Headers["Sec-Fetch-Site"] == "cross-site")
        { await ErrorAsync(context, 403, "拒绝跨站请求。"); return; }
        if (request.Method is not ("GET" or "POST" or "PUT" or "DELETE"))
        { await ErrorAsync(context, 405, "不支持该请求方式。"); return; }
        if (request.Method is "POST" or "PUT" && !request.HasJsonContentType())
        { await ErrorAsync(context, 415, "请求必须使用 JSON。"); return; }
        var requestBudget = remote ? _remoteRequests : _localRequests;
        if (!await requestBudget.WaitAsync(0, request.HttpContext.RequestAborted))
        { await ErrorAsync(context, 429, "操作过于频繁，请稍后重试。"); return; }
        try
        {
            var path = request.Path.Value ?? "/";
            if (!path.StartsWith("/api/", StringComparison.Ordinal))
            { await ServeAssetAsync(context, path); return; }
            var session = _access.FindSession(request.Cookies[CookieName(local)], local, ip);
            if (request.Method == "GET" && path is "/api/bootstrap" or "/api/auth/status")
            { await response.WriteAsJsonAsync(Status(local, session), context.RequestAborted); return; }
            if (path == "/api/auth/setup" && request.Method == "POST")
            {
                if (!local) { await ErrorAsync(context, 403, "账户只能在运行程序的本机初始化。"); return; }
                if (!AccessSecurity.TokenMatches(request.Headers["X-Mumu-Token"], _access.SetupToken))
                { await ErrorAsync(context, 403, "初始化验证失败，请刷新本机页面。"); return; }
                var setup = await BodyAsync<SetupInput>(request, context.RequestAborted);
                var initialized = await _access.InitializeAsync(setup.Username ?? "", setup.Password ?? "", setup.RemoteEnabled, ip);
                SetCookie(context, initialized);
                if (setup.RemoteEnabled)
                {
                    try { await StartRemoteAsync(_access.RemotePort, CancellationToken.None); }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException)
                    {
                        _access.SetRemote(false, _access.RemotePort, ip);
                        _remoteError = "账户已创建，但远程端口启动失败。请在本机更换端口后重新启用。";
                    }
                }
                await response.WriteAsJsonAsync(Status(local, initialized)); return;
            }
            if (path == "/api/auth/login" && request.Method == "POST")
            {
                var input = await BodyAsync<LoginInput>(request, context.RequestAborted);
                var loggedIn = await _access.LoginAsync(input.Username ?? "", input.Password ?? "", local, ip);
                if (loggedIn is null) { await ErrorAsync(context, 401, "账户或密码不正确。"); return; }
                SetCookie(context, loggedIn);
                await response.WriteAsJsonAsync(Status(local, loggedIn)); return;
            }
            if (session is null)
            { await ErrorAsync(context, 401, _access.Initialized ? "请先登录。" : "请先在本机初始化账户。"); return; }
            if (request.Method != "GET" && !AccessSecurity.TokenMatches(request.Headers["X-Mumu-Token"], session.Token))
            { await ErrorAsync(context, 403, "会话验证失败，请刷新页面后重试。"); return; }
            if (path == "/api/auth/logout" && request.Method == "POST")
            {
                _access.Logout(session);
                response.Cookies.Delete(CookieName(local), CookieOptions(local));
                await response.WriteAsJsonAsync(new { ok = true }); return;
            }
            if (path is "/api/auth/settings" or "/api/access" or "/api/auth/credentials" or "/api/auth/certificate" or "/api/startup"
                || path == "/api/settings")
            {
                if (!local) { await ErrorAsync(context, 403, "此设置只能在运行程序的本机管理。"); return; }
            }
            if (path is "/api/auth/settings" or "/api/access")
            {
                if (request.Method == "GET") { await response.WriteAsJsonAsync(Status(local, session)); return; }
                if (request.Method == "PUT")
                {
                    var input = await BodyAsync<RemoteInput>(request, context.RequestAborted);
                    int port = input.RemotePort ?? _access.RemotePort;
                    if (port is < 1024 or > 65535 || port == Address.Port) throw new ArgumentException("远程端口应为 1024～65535，且不能与本机端口相同。");
                    await SetRemoteAsync(input.RemoteEnabled, port, ip, input.PublicOrigin);
                    await response.WriteAsJsonAsync(Status(local, session)); return;
                }
            }
            if (path == "/api/auth/credentials" && request.Method == "PUT")
            {
                var input = await BodyAsync<CredentialInput>(request, context.RequestAborted);
                var changed = await _access.ChangeCredentialsAsync(input.Username ?? "", input.Password ?? "", input.CurrentPassword ?? "", ip);
                SetCookie(context, changed);
                await response.WriteAsJsonAsync(Status(local, changed)); return;
            }
            if (path == "/api/startup")
            {
                var startup = new StartupManager();
                if (request.Method == "GET") { await response.WriteAsJsonAsync(startup.GetStatus()); return; }
                if (request.Method == "PUT")
                {
                    var input = await BodyAsync<StartupInput>(request, context.RequestAborted);
                    var status = startup.SetEnabled(input.Enabled);
                    _access.Audit("本机已修改开机启动设置", ip);
                    await response.WriteAsJsonAsync(status); return;
                }
            }
            // A command that has already been accepted must finish and save its timer even when
            // the browser closes or its request times out. Read-only captures are cancellable.
            var ct = request.Method == "GET" ? context.RequestAborted : CancellationToken.None;
            if (request.Method == "GET")
            {
                switch (path)
                {
                    case "/api/state": await response.WriteAsJsonAsync(_engine.GetState(local), ct); return;
                    case "/api/settings": await response.WriteAsJsonAsync(_engine.Settings, ct); return;
                    case "/api/logs": await response.WriteAsJsonAsync(_engine.GetLogs(), ct); return;
                    case "/api/health": await response.WriteAsJsonAsync(new { status = "ok", version = "1.1.1" }, ct); return;
                }
            }
            if (path == "/api/refresh" && request.Method == "POST")
            { await _engine.RefreshAsync(ct); await response.WriteAsJsonAsync(_engine.GetState(local), ct); return; }
            if (path == "/api/settings" && request.Method == "PUT")
            {
                var settings = await BodyAsync<AppSettings>(request, ct);
                bool restartRequired = await _engine.UpdateSettingsAsync(settings, ct);
                await response.WriteAsJsonAsync(new { restartRequired, settings = _engine.Settings }, ct); return;
            }
            if (path == "/api/tasks" && request.Method == "POST")
            { await response.WriteAsJsonAsync(await _engine.SaveTaskAsync(await BodyAsync<ScheduledTask>(request, ct), ct: ct), ct); return; }
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts[1] == "tasks")
            {
                if (request.Method == "DELETE") { await _engine.DeleteTaskAsync(parts[2], ct); await response.WriteAsJsonAsync(new { ok = true }, ct); return; }
                if (request.Method == "PUT") { await response.WriteAsJsonAsync(await _engine.SaveTaskAsync(await BodyAsync<ScheduledTask>(request, ct), parts[2], ct), ct); return; }
            }
            if (parts.Length == 4 && parts[1] == "emulators" && int.TryParse(parts[2], out int index) && index >= 0)
            {
                if (request.Method == "GET" && parts[3] == "thumbnail")
                {
                    int size = int.TryParse(request.Query["size"], out var requested) ? Math.Clamp(requested, 64, 1280) : 320;
                    var bytes = await ThumbnailAsync(index, size, ct);
                    response.ContentType = "image/jpeg";
                    response.ContentLength = bytes.Length;
                    await response.Body.WriteAsync(bytes, ct); return;
                }
                if (request.Method == "GET" && parts[3] == "capture")
                {
                    // Full-resolution PNG for saving: renderer frame first, ADB screencap as fallback.
                    byte[] bytes;
                    try { bytes = EncodePng(await _engine.ThumbnailAsync(index, 4096, ct)); }
                    catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or TimeoutException or NotSupportedException or DllNotFoundException)
                    {
                        DiagnosticLog.Write("warning", $"#{index} 保存画面改用 ADB 截图：{ex.Message}");
                        bytes = await _engine.ScreenshotAsync(index, ct);
                    }
                    DiagnosticLog.Write("info", $"#{index} 生成保存画面 {bytes.Length / 1024} KB（来源 {ip}）");
                    response.ContentType = "image/png";
                    response.ContentLength = bytes.Length;
                    await response.Body.WriteAsync(bytes, ct); return;
                }
                if (request.Method == "GET" && parts[3] == "screenshot")
                {
                    var bytes = await _engine.ScreenshotAsync(index, ct);
                    response.ContentType = "image/png";
                    response.ContentLength = bytes.Length;
                    await response.Body.WriteAsync(bytes, ct); return;
                }
                if (request.Method == "POST")
                {
                    var input = await BodyAsync<ControlInput>(request, ct);
                    switch (parts[3])
                    {
                        case "start": await _engine.ControlAsync(index, "start", input.DurationMinutes, ct); break;
                        case "stop": await _engine.ControlAsync(index, "stop", ct: ct); break;
                        case "auto-stop": await _engine.SetAutoStopAsync(index, input.DurationMinutes, ct); break;
                        case "action": await _engine.ControlAsync(index, input.Action ?? "", ct: ct); break;
                        default: await ErrorAsync(context, 404, "接口不存在。"); return;
                    }
                    await response.WriteAsJsonAsync(new { ok = true }, ct); return;
                }
            }
            await ErrorAsync(context, 404, "接口不存在。");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception ex) when (!response.HasStarted)
        {
            int code = ex switch
            {
                ArgumentException or JsonException or Microsoft.AspNetCore.Http.BadHttpRequestException => 400,
                UnauthorizedAccessException => 401,
                AccessLimitException => 429,
                KeyNotFoundException => 404,
                TimeoutException => 504,
                InvalidOperationException => 409,
                _ => 500
            };
            if (code == 429) response.Headers.RetryAfter = "900";
            if (code == 500) DiagnosticLog.Write("error", $"{request.Method} {request.Path} 内部错误（来源 {ip}）", ex);
            else if (code is 409 or 504 && !request.Path.Value!.EndsWith("/thumbnail", StringComparison.Ordinal))
                DiagnosticLog.Write("warning", $"{request.Method} {request.Path} → {code}：{ex.Message}");
            await ErrorAsync(context, code, code == 500 ? "服务暂时无法处理该请求，请在本机查看配置。" : ex.Message);
        }
        finally { requestBudget.Release(); }
    }
    private sealed record CachedFrame(DateTime At, byte[] Jpeg);
    private readonly ConcurrentDictionary<(int Index, int Size), CachedFrame> _frames = new();
    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    /// <summary>Several viewers of the same instance share one capture per short window.</summary>
    private async Task<byte[]> ThumbnailAsync(int index, int size, CancellationToken ct)
    {
        var key = (index, size);
        if (_frames.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMilliseconds(700)) return cached.Jpeg;
        var frame = await _engine.ThumbnailAsync(index, size, ct);
        var jpeg = EncodeJpeg(frame, size > 480 ? 82L : 70L);
        if (_frames.Count > 64) _frames.Clear();
        _frames[key] = new CachedFrame(DateTime.UtcNow, jpeg);
        return jpeg;
    }
    private static Bitmap ToBitmap(RawFrame frame)
    {
        var bitmap = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, frame.Width, frame.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < frame.Height; y++)
                Marshal.Copy(frame.Bgra, y * frame.Width * 4, data.Scan0 + y * data.Stride, frame.Width * 4);
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }
    private static byte[] EncodePng(RawFrame frame)
    {
        using var bitmap = ToBitmap(frame);
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }
    private static byte[] EncodeJpeg(RawFrame frame, long quality)
    {
        using var bitmap = ToBitmap(frame);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        using var output = new MemoryStream();
        bitmap.Save(output, JpegCodec, parameters);
        return output.ToArray();
    }
    private sealed record ControlInput(int? DurationMinutes = null, string? Action = null);
    private sealed record LoginInput(string? Username, string? Password);
    private sealed record SetupInput(string? Username, string? Password, bool RemoteEnabled = false);
    private sealed record CredentialInput(string? Username, string? Password, string? CurrentPassword);
    private sealed record RemoteInput(bool RemoteEnabled, int? RemotePort = null, string? PublicOrigin = null);
    private sealed record StartupInput(bool Enabled);
    private object Status(bool local, AccessSession? session)
    {
        bool admin = local && session is not null;
        return new
        {
            initialized = _access.Initialized,
            authenticated = session is not null,
            isLocal = local,
            local,
            username = session is null ? null : _access.Username,
            token = session?.Token ?? (local && !_access.Initialized ? _access.SetupToken : ""),
            remoteEnabled = _access.RemoteEnabled && _remoteApp is not null,
            remotePort = _access.RemotePort,
            publicOrigin = admin ? _access.PublicOrigin : "",
            remoteUrls = admin ? (_access.PublicOrigin.Length > 0 ? [_access.PublicOrigin]
                : AccessSecurity.LocalAddresses().Select(ip => new UriBuilder("http", ip.ToString(), _access.RemotePort).Uri.GetLeftPart(UriPartial.Authority)).ToArray()) : [],
            sessionExpiresAt = session?.ExpiresAt,
            remoteError = admin ? _remoteError : null
        };
    }
    private bool ValidRemoteHost(HostString host)
    {
        if (_access.PublicOrigin.Length > 0)
        {
            var configured = new Uri(_access.PublicOrigin);
            if (!string.Equals(host.Host.Trim('[', ']'), configured.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase)) return false;
            return (host.Port ?? (configured.Scheme == "https" ? 443 : 80)) == configured.Port;
        }
        if (host.Port != _access.RemotePort) return false;
        var hostname = host.Host.Trim('[', ']');
        if (hostname.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) || hostname.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(hostname, out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address) || AccessSecurity.LocalAddresses().Contains(address);
    }
    private bool SecureRemoteCookie => _access.PublicOrigin.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    private string CookieName(bool local) => local ? "MumuLocalSession" : SecureRemoteCookie ? "__Host-MumuSession" : "MumuRemoteSession";
    private CookieOptions CookieOptions(bool local) => new()
    {
        HttpOnly = true, Secure = !local && SecureRemoteCookie, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true
    };
    private void SetCookie(HttpContext context, AccessSession session) =>
        context.Response.Cookies.Append(CookieName(session.Local), session.Cookie, CookieOptions(session.Local));
    private async Task StartRemoteAsync(int port, CancellationToken ct)
    {
        await _remoteGate.WaitAsync(ct);
        try { await StartRemoteCoreAsync(port, ct); }
        finally { _remoteGate.Release(); }
    }
    private async Task StartRemoteCoreAsync(int port, CancellationToken ct)
    {
        if (_stopping || _remoteApp is not null) return;
        var candidate = BuildServer(port, remote: true);
        try
        {
            await candidate.StartAsync(ct);
            _remoteApp = candidate;
            _remoteError = null;
        }
        catch { await candidate.DisposeAsync(); throw; }
    }
    private async Task SetRemoteAsync(bool enabled, int port, string ip, string? publicOrigin)
    {
        string normalizedOrigin = publicOrigin is null ? _access.PublicOrigin : AccessSecurity.NormalizePublicOrigin(publicOrigin);
        await _remoteGate.WaitAsync();
        try
        {
            if (_stopping) throw new InvalidOperationException("程序正在退出。");
            if (!enabled || port != _access.RemotePort)
            {
                _access.SetRemote(false, port, ip, normalizedOrigin);
                if (_remoteApp is not null)
                {
                    var previous = _remoteApp;
                    _remoteApp = null;
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await previous.StopAsync(deadline.Token);
                    await previous.DisposeAsync();
                }
            }
            if (enabled)
            {
                try
                {
                    await StartRemoteCoreAsync(port, CancellationToken.None);
                    _access.SetRemote(true, port, ip, normalizedOrigin);
                }
                catch
                {
                    if (_remoteApp is not null)
                    {
                        var started = _remoteApp;
                        _remoteApp = null;
                        try
                        {
                            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            await started.StopAsync(deadline.Token);
                            await started.DisposeAsync();
                        }
                        catch { /* the persisted disabled state still prevents new remote use */ }
                    }
                    _remoteError = "远程 HTTP 端口无法启动，请选择其他端口后重试。";
                    _access.SetRemote(false, port, ip, normalizedOrigin);
                    throw new InvalidOperationException(_remoteError);
                }
            }
            else _remoteError = null;
        }
        finally { _remoteGate.Release(); }
    }
    private static async Task<T> BodyAsync<T>(HttpRequest request, CancellationToken ct) =>
        await request.ReadFromJsonAsync<T>(AppJson.Options, ct) ?? throw new ArgumentException("请求内容为空。");
    private static Task ErrorAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = message }, context.RequestAborted);
    }
    private static async Task ServeAssetAsync(HttpContext context, string path)
    {
        if (context.Request.Method != "GET") { await ErrorAsync(context, 405, "页面只支持 GET。"); return; }
        var asset = path switch
        {
            "/" or "/index.html" => ("index.html", "text/html; charset=utf-8"),
            "/app.css" => ("app.css", "text/css; charset=utf-8"),
            "/app.js" => ("app.js", "text/javascript; charset=utf-8"),
            "/favicon.svg" => ("favicon.svg", "image/svg+xml"),
            _ => default
        };
        if (asset.Item1 is null) { await ErrorAsync(context, 404, "页面不存在。"); return; }
        await using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("MumuControl.wwwroot." + asset.Item1);
        if (resource is null) { await ErrorAsync(context, 500, "页面资源缺失，请重新构建。"); return; }
        context.Response.ContentType = asset.Item2;
        context.Response.ContentLength = resource.Length;
        await resource.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
    public async ValueTask DisposeAsync()
    {
        await _remoteGate.WaitAsync();
        try
        {
            _stopping = true;
            if (_remoteApp is not null)
            {
                using var remoteDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _remoteApp.StopAsync(remoteDeadline.Token);
                await _remoteApp.DisposeAsync();
                _remoteApp = null;
            }
        }
        finally { _remoteGate.Release(); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _app.StopAsync(deadline.Token);
        await _app.DisposeAsync();
        _localRequests.Dispose();
        _remoteRequests.Dispose();
        _access.Dispose();
        _remoteGate.Dispose();
    }
}

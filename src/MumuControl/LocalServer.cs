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
    private readonly string _dataDirectory;
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
        _dataDirectory = dataDirectory;
        _access = new AccessSecurity(dataDirectory, port);
        // Uploads and downloads left over from an earlier run can never be claimed again.
        try { if (Directory.Exists(StagingDirectory)) Directory.Delete(StagingDirectory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write("warning", "清理 APK 临时目录失败：" + ex.Message); }
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
        // The APK upload is the only binary request; it is still CSRF-protected by the session token below.
        bool apkUpload = request.Path.Value == "/api/apk/upload" && request.Method == "POST";
        if (request.Method is "POST" or "PUT" && !(apkUpload ? request.ContentType is "application/vnd.android.package-archive" or "application/octet-stream" : request.HasJsonContentType()))
        { await ErrorAsync(context, 415, apkUpload ? "请上传 APK 文件。" : "请求必须使用 JSON。"); return; }
        var requestBudget = remote ? _remoteRequests : _localRequests;
        if (!await requestBudget.WaitAsync(0, request.HttpContext.RequestAborted))
        { await ErrorAsync(context, 429, "操作过于频繁，请稍后重试。"); return; }
        try
        {
            var path = request.Path.Value ?? "/";
            if (!path.StartsWith("/api/", StringComparison.Ordinal))
            { await ServeAssetAsync(context, path); return; }
            // The loopback management entry (local desktop window and local browser) needs no login.
            // The remote listener always requires the account password.
            var session = local ? _access.LocalSession : _access.FindSession(request.Cookies[CookieName(local)], local, ip);
            if (request.Method == "GET" && path is "/api/bootstrap" or "/api/auth/status")
            { await response.WriteAsJsonAsync(Status(local, session), context.RequestAborted); return; }
            if (path == "/api/auth/setup" && request.Method == "POST")
            {
                if (!local) { await ErrorAsync(context, 403, "账户只能在运行程序的本机初始化。"); return; }
                if (!AccessSecurity.TokenMatches(request.Headers["X-Mumu-Token"], _access.LocalSession.Token))
                { await ErrorAsync(context, 403, "初始化验证失败，请刷新本机页面。"); return; }
                var setup = await BodyAsync<SetupInput>(request, context.RequestAborted);
                await _access.InitializeAsync(setup.Username ?? "", setup.Password ?? "", setup.RemoteEnabled, ip);
                var initialized = _access.LocalSession;
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
                if (local) { await response.WriteAsJsonAsync(Status(local, session)); return; }
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
                if (local) { await ErrorAsync(context, 400, "本机访问无需登录，也无法退出登录。"); return; }
                _access.Logout(session);
                response.Cookies.Delete(CookieName(local), CookieOptions(local));
                await response.WriteAsJsonAsync(new { ok = true }); return;
            }
            if (path is "/api/auth/settings" or "/api/access" or "/api/auth/credentials" or "/api/auth/certificate" or "/api/startup"
                || path is "/api/settings" or "/api/apk/browse")
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
                // Only the local port reaches this point; the change revokes every remote session.
                await _access.ChangeCredentialsAsync(input.Username ?? "", input.Password ?? "", input.CurrentPassword ?? "", ip);
                await response.WriteAsJsonAsync(Status(local, session)); return;
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
            if (path == "/api/apk/browse" && request.Method == "POST")
            { await response.WriteAsJsonAsync(new { path = await BrowseApkAsync() }, ct); return; }
            if (path == "/api/apk/jobs" && request.Method == "GET")
            { await response.WriteAsJsonAsync(_engine.GetApkJobs(), ct); return; }
            if (apkUpload)
            { await response.WriteAsJsonAsync(await UploadApkAsync(context, ip), CancellationToken.None); return; }
            if (path == "/api/apk/install" && request.Method == "POST")
            { await response.WriteAsJsonAsync(StartApkInstall(await BodyAsync<ApkInstallInput>(request, ct), local, ip), ct); return; }
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
    private const long MaxApkBytes = 4L * 1024 * 1024 * 1024;
    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];
    private sealed record StagedApk(string Path, string FileName, long Size, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<string, StagedApk> _staged = new();
    private string StagingDirectory => Path.Combine(_dataDirectory, "apk-staging");
    private static readonly HttpClient Downloader = CreateDownloader();

    /// <summary>Downloads may follow redirects, but never connect to this computer or link-local addresses.</summary>
    private static HttpClient CreateDownloader()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                var allowed = addresses.Where(a => !BlockedDownloadAddress(a)).ToArray();
                if (allowed.Length == 0) throw new HttpRequestException("下载地址指向本机或不允许访问的网络地址。");
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MuMuControl/1.1.1");
        return client;
    }
    private static bool BlockedDownloadAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.IsIPv6LinkLocal || address.IsIPv6Multicast) return true;
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && (bytes[0] == 169 && bytes[1] == 254 || bytes[0] >= 224 || bytes[0] == 0)
            || AccessSecurity.LocalAddresses().Contains(address);
    }

    private static string SafeApkName(string? name)
    {
        name = Path.GetFileName((name ?? "").Trim()).Trim();
        name = new string(name.Select(c => char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        if (name.Length == 0) name = "app.apk";
        if (!name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) name += ".apk";
        return name.Length <= 120 ? name : name[..116] + ".apk";
    }
    private static async Task EnsureApkSignatureAsync(string path, CancellationToken ct)
    {
        var header = new byte[4];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (await stream.ReadAtLeastAsync(header, 4, false, ct) < 4 || !header.AsSpan().SequenceEqual(ZipSignature))
            throw new ArgumentException("文件不是有效的 APK（ZIP）格式。");
    }
    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write("warning", "删除 APK 临时文件失败：" + ex.Message); }
    }
    private void PruneStaged()
    {
        foreach (var (id, staged) in _staged)
            if (staged.ExpiresAt <= DateTimeOffset.UtcNow && _staged.TryRemove(id, out _)) DeleteQuietly(staged.Path);
    }

    /// <summary>Streams the request body straight to disk; uploads are staged until an install claims them.</summary>
    private async Task<object> UploadApkAsync(HttpContext context, string ip)
    {
        PruneStaged();
        if (_staged.Count >= 4) throw new InvalidOperationException("待安装的上传文件过多，请先安装或稍后重试。");
        var request = context.Request;
        if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = MaxApkBytes;
        if (request.ContentLength is null or <= 0) throw new ArgumentException("上传内容为空。");
        if (request.ContentLength > MaxApkBytes) throw new ArgumentException("APK 文件不能超过 4 GB。");
        var fileName = SafeApkName(Uri.UnescapeDataString(request.Headers["X-Apk-Name"].ToString()));
        Directory.CreateDirectory(StagingDirectory);
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(StagingDirectory, id + ".apk");
        try
        {
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
                await request.Body.CopyToAsync(file, 128 * 1024, context.RequestAborted);
            var size = new FileInfo(path).Length;
            if (size != request.ContentLength) throw new ArgumentException("上传未完成，请重试。");
            await EnsureApkSignatureAsync(path, context.RequestAborted);
            _staged[id] = new StagedApk(path, fileName, size, DateTimeOffset.UtcNow.AddMinutes(30));
            DiagnosticLog.Write("info", $"已接收 APK 上传「{fileName}」{size / 1024} KB（来源 {ip}）");
            return new { uploadId = id, fileName, size };
        }
        catch { DeleteQuietly(path); throw; }
    }

    private object StartApkInstall(ApkInstallInput input, bool local, string ip)
    {
        var indexes = input.Indexes ?? [];
        switch (input.Source)
        {
            case "upload":
            {
                if (string.IsNullOrEmpty(input.UploadId) || !_staged.TryRemove(input.UploadId, out var staged))
                    throw new ArgumentException("上传的文件已失效，请重新选择文件。");
                try
                {
                    return _engine.StartApkInstall(indexes, staged.FileName, "upload", (_, _) => Task.FromResult(staged.Path), () => File.Delete(staged.Path));
                }
                catch { _staged[input.UploadId] = staged; throw; }
            }
            case "url":
            {
                if (!Uri.TryCreate(input.Url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
                    throw new ArgumentException("请输入有效的 HTTP 或 HTTPS 下载地址。");
                Directory.CreateDirectory(StagingDirectory);
                var path = Path.Combine(StagingDirectory, Guid.NewGuid().ToString("N") + ".apk");
                var fileName = SafeApkName(Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? ""));
                DiagnosticLog.Write("info", $"APK 下载请求 {uri.GetLeftPart(UriPartial.Path)}（来源 {ip}）");
                return _engine.StartApkInstall(indexes, fileName, "url", (progress, ct) => DownloadApkAsync(uri, path, progress, ct), () => { if (File.Exists(path)) File.Delete(path); });
            }
            case "path":
            {
                if (!local) throw new UnauthorizedAccessException("本机文件路径只能在运行程序的电脑上使用。");
                var raw = (input.Path ?? "").Trim().Trim('"');
                if (raw.Length is 0 or > 1024 || !Path.IsPathFullyQualified(raw)) throw new ArgumentException("请填写 APK 文件的完整路径。");
                var full = Path.GetFullPath(raw);
                if (!full.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) throw new ArgumentException("找不到该 APK 文件。");
                return _engine.StartApkInstall(indexes, Path.GetFileName(full), "path", async (_, ct) => { await EnsureApkSignatureAsync(full, ct); return full; });
            }
            default: throw new ArgumentException("请选择 APK 来源。");
        }
    }

    private static async Task<string> DownloadApkAsync(Uri uri, string path, Action<long, long?> progress, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            using var response = await Downloader.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"下载失败：服务器返回 {(int)response.StatusCode}。");
            long? total = response.Content.Headers.ContentLength;
            if (total > MaxApkBytes) throw new InvalidOperationException("APK 文件不能超过 4 GB。");
            await using (var source = await response.Content.ReadAsStreamAsync(deadline.Token))
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                var buffer = new byte[128 * 1024];
                long received = 0, reported = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, deadline.Token)) > 0)
                {
                    received += count;
                    if (received > MaxApkBytes) throw new InvalidOperationException("APK 文件不能超过 4 GB。");
                    await file.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
                    if (received - reported >= 512 * 1024) { progress(received, total); reported = received; }
                }
                progress(received, total);
                if (total is {} expected && received != expected) throw new InvalidOperationException("下载未完成，连接已中断。");
            }
            await EnsureApkSignatureAsync(path, deadline.Token);
            return path;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("下载超时（30 分钟）。"); }
        catch (HttpRequestException ex) { throw new InvalidOperationException("下载失败：" + ex.Message, ex); }
    }

    /// <summary>Shows a native file picker on this computer; only the loopback entry may call it.</summary>
    private static Task<string?> BrowseApkAsync()
    {
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var owner = new System.Windows.Forms.Form { TopMost = true, ShowInTaskbar = false, Opacity = 0, StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen };
                owner.Show();
                owner.Activate();
                using var dialog = new System.Windows.Forms.OpenFileDialog
                {
                    Title = "选择要安装的 APK", Filter = "Android 安装包 (*.apk)|*.apk", CheckFileExists = true, Multiselect = false
                };
                result.TrySetResult(dialog.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK ? dialog.FileName : null);
            }
            catch (Exception ex) { result.TrySetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return result.Task;
    }

    private sealed record ApkInstallInput(int[]? Indexes, string? Source, string? UploadId = null, string? Url = null, string? Path = null);
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
            token = session?.Token ?? "",
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

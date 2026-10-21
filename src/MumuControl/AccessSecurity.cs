using System.Net;
using System.Net.NetworkInformation;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MumuControl;

/// <summary>Credentials stay on this Windows account; sessions never leave memory.</summary>
public sealed class AccessSecurity : IDisposable
{
    private const int HashIterations = 600_000;
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(12);
    private readonly string _directory;
    private readonly string _path;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _credentialGate = new(1, 1);
    private readonly SemaphoreSlim _hashGate = new(2, 2);
    private readonly Dictionary<string, AccessSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DateTimeOffset>> _failures = new(StringComparer.Ordinal);
    private readonly List<DateTimeOffset> _globalFailures = [];
    private readonly byte[] _dummySalt = RandomNumberGenerator.GetBytes(16);
    private readonly byte[] _dummyHash = RandomNumberGenerator.GetBytes(32);
    private AccessDocument _document;
    private long _remoteSessionGeneration;
    public string SetupToken { get; } = NewToken();
    public bool Initialized { get { lock (_sync) return _document.Username is not null; } }
    public bool RemoteEnabled { get { lock (_sync) return _document.RemoteEnabled && _document.Username is not null; } }
    public int RemotePort { get { lock (_sync) return _document.RemotePort; } }
    public string PublicOrigin { get { lock (_sync) return _document.PublicOrigin; } }

    public AccessSecurity(string dataDirectory, int localPort)
    {
        _directory = Path.Combine(Path.GetFullPath(dataDirectory), "security");
        Directory.CreateDirectory(_directory);
        // Inherited permissions may grant other interactive Windows users access to app data.
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("无法确认当前 Windows 账户。");
        var permissions = new DirectorySecurity();
        permissions.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        permissions.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        permissions.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(_directory).SetAccessControl(permissions);
        _path = Path.Combine(_directory, "access.json");
        _document = File.Exists(_path) ? Load() : new AccessDocument { RemotePort = localPort == 65535 ? 65534 : localPort + 1 };
    }

    private AccessDocument Load()
    {
        if (new FileInfo(_path).Length > 16 * 1024) throw new InvalidDataException("安全配置文件无效。");
        var data = JsonSerializer.Deserialize<AccessDocument>(File.ReadAllText(_path), Core.AppJson.Options) ?? throw new InvalidDataException("安全配置文件为空。");
        if (data.Version != 1 || data.RemotePort is < 1024 or > 65535 || data.Username is null || data.Iterations != HashIterations)
            throw new InvalidDataException("安全配置文件无效，请在本机恢复备份。");
        ValidateCredentials(data.Username, "Recovery123456");
        try { data = data with { PublicOrigin = NormalizePublicOrigin(data.PublicOrigin) }; }
        catch (ArgumentException ex) { throw new InvalidDataException("公开访问地址配置无效。", ex); }
        try { if (Convert.FromBase64String(data.Salt).Length != 16 || Convert.FromBase64String(data.PasswordHash).Length != 32) throw new FormatException(); }
        catch (FormatException ex) { throw new InvalidDataException("账户验证数据无效。", ex); }
        return data;
    }

    public async Task<AccessSession> InitializeAsync(string username, string password, bool remoteEnabled, string ip)
    {
        ValidateCredentials(username, password);
        if (!await _credentialGate.WaitAsync(0)) throw new AccessLimitException();
        try
        {
            if (Initialized) throw new InvalidOperationException("账户已初始化，请登录后在本机修改。");
            var credentials = await CreateHashAsync(username, password);
            lock (_sync)
            {
                var next = credentials with { RemotePort = _document.RemotePort, RemoteEnabled = remoteEnabled };
                Save(next);
                _document = next;
            }
            Audit("账户已在本机初始化；远程访问" + (remoteEnabled ? "已启用" : "未启用"), ip);
            return CreateSession(true, ip);
        }
        finally { _credentialGate.Release(); }
    }

    public async Task<AccessSession?> LoginAsync(string username, string password, bool local, string ip)
    {
        if (!Initialized) return null;
        if (!await _credentialGate.WaitAsync(0)) throw new AccessLimitException();
        try
        {
            if (LoginBlocked(ip, local)) throw new AccessLimitException();
            if (username.Length > 128 || password.Length > 128)
            { RegisterFailure(ip, local); return null; }
            AccessDocument data;
            long remoteGeneration;
            lock (_sync) { data = _document; remoteGeneration = _remoteSessionGeneration; }
            bool match = await VerifyAsync(data, username, password);
            if (!match) { RegisterFailure(ip, local); Audit("登录验证失败", ip); return null; }
            lock (_sync) _failures.Remove(FailureKey(ip, local));
            Audit(local ? "本机登录成功" : "远程登录成功", ip);
            return CreateSession(local, ip, remoteGeneration);
        }
        finally { _credentialGate.Release(); }
    }

    public async Task<AccessSession> ChangeCredentialsAsync(string username, string password, string currentPassword, string ip)
    {
        ValidateCredentials(username, password);
        if (currentPassword.Length > 128) throw new ArgumentException("当前密码不正确。");
        if (!await _credentialGate.WaitAsync(0)) throw new AccessLimitException();
        try
        {
            if (LoginBlocked(ip, local: true)) throw new AccessLimitException();
            AccessDocument data;
            lock (_sync) data = _document;
            if (!await VerifyAsync(data, data.Username ?? "", currentPassword))
            { RegisterFailure(ip, local: true); throw new UnauthorizedAccessException("当前密码不正确。"); }
            var credentials = await CreateHashAsync(username, password);
            lock (_sync)
            {
                var current = _document;
                var next = credentials with { RemotePort = current.RemotePort, RemoteEnabled = current.RemoteEnabled, PublicOrigin = current.PublicOrigin };
                Save(next);
                _document = next;
                _sessions.Clear();
                _remoteSessionGeneration++;
                _failures.Remove(FailureKey(ip, local: true));
            }
            Audit("账户凭据已在本机修改，所有旧会话失效", ip);
            return CreateSession(true, ip);
        }
        finally { _credentialGate.Release(); }
    }

    public void SetRemote(bool enabled, int port, string ip, string? publicOrigin = null)
    {
        if (port is < 1024 or > 65535) throw new ArgumentException("远程端口应为 1024～65535。");
        var normalizedOrigin = publicOrigin is null ? null : NormalizePublicOrigin(publicOrigin);
        lock (_sync)
        {
            if (_document.Username is null) throw new InvalidOperationException("请先在本机初始化账户。");
            var next = _document with { RemoteEnabled = enabled, RemotePort = port, PublicOrigin = normalizedOrigin ?? _document.PublicOrigin };
            Save(next);
            _document = next;
            _remoteSessionGeneration++;
            foreach (var key in _sessions.Where(s => !s.Value.Local).Select(s => s.Key).ToArray()) _sessions.Remove(key);
        }
        Audit($"本机已{(enabled ? "启用" : "关闭")}远程访问，端口 {port}", ip);
    }

    public AccessSession? FindSession(string? token, bool local, string ip)
    {
        if (token is null || token.Length != 64) return null;
        var key = SessionKey(token);
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            RemoveExpired(now);
            if (!_sessions.TryGetValue(key, out var session) || session.Local != local || session.Ip != ip) return null;
            session.LastSeenAt = now;
            return session;
        }
    }
    private AccessSession CreateSession(bool local, string ip, long? remoteGeneration = null)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new AccessSession(NewToken(), NewToken(), local, ip, now, now + AbsoluteLifetime);
        lock (_sync)
        {
            if (!local && (!_document.RemoteEnabled || remoteGeneration != _remoteSessionGeneration))
                throw new InvalidOperationException("远程访问配置已更改，请刷新页面后重新登录。");
            RemoveExpired(now);
            if (_sessions.Count >= 32) _sessions.Remove(_sessions.MinBy(s => s.Value.LastSeenAt).Key);
            _sessions.Add(SessionKey(session.Cookie), session);
        }
        return session;
    }
    public void Logout(AccessSession session)
    { lock (_sync) _sessions.Remove(SessionKey(session.Cookie)); Audit("会话已退出", session.Ip); }
    public string? Username { get { lock (_sync) return _document.Username; } }
    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var key in _sessions.Where(s => s.Value.ExpiresAt <= now || s.Value.LastSeenAt + IdleLifetime <= now).Select(s => s.Key).ToArray()) _sessions.Remove(key);
    }

    private static string FailureKey(string ip, bool local) => (local ? "local:" : "remote:") + ip;
    private bool LoginBlocked(string ip, bool local)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            _globalFailures.RemoveAll(x => x + TimeSpan.FromMinutes(5) <= now);
            foreach (var key in _failures.Keys.ToArray())
            {
                _failures[key].RemoveAll(x => x + TimeSpan.FromMinutes(15) <= now);
                if (_failures[key].Count == 0) _failures.Remove(key);
            }
            return !local && _globalFailures.Count >= 30 || _failures.TryGetValue(FailureKey(ip, local), out var list) && list.Count >= 5;
        }
    }
    private void RegisterFailure(string ip, bool local)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            var key = FailureKey(ip, local);
            if (!_failures.ContainsKey(key))
            {
                if (_failures.Count >= 256) _failures.Remove(_failures.MinBy(x => x.Value[^1]).Key);
                _failures[key] = [];
            }
            _failures[key].Add(now);
            if (!local) _globalFailures.Add(now);
        }
    }
    private async Task<bool> VerifyAsync(AccessDocument data, string username, string password)
    {
        if (!await _hashGate.WaitAsync(0)) throw new AccessLimitException();
        try
        {
            var salt = data.Username is null ? _dummySalt : Convert.FromBase64String(data.Salt);
            var expected = data.Username is null ? _dummyHash : Convert.FromBase64String(data.PasswordHash);
            var actual = await Task.Run(() => Rfc2898DeriveBytes.Pbkdf2(password, salt, HashIterations, HashAlgorithmName.SHA256, 32));
            try { return CryptographicOperations.FixedTimeEquals(actual, expected) & string.Equals(data.Username, username, StringComparison.OrdinalIgnoreCase); }
            finally { CryptographicOperations.ZeroMemory(actual); }
        }
        finally { _hashGate.Release(); }
    }
    private async Task<AccessDocument> CreateHashAsync(string username, string password)
    {
        await _hashGate.WaitAsync();
        try
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = await Task.Run(() => Rfc2898DeriveBytes.Pbkdf2(password, salt, HashIterations, HashAlgorithmName.SHA256, 32));
            try { return new AccessDocument { Username = username, Salt = Convert.ToBase64String(salt), PasswordHash = Convert.ToBase64String(hash), Iterations = HashIterations }; }
            finally { CryptographicOperations.ZeroMemory(hash); }
        }
        finally { _hashGate.Release(); }
    }
    private static void ValidateCredentials(string username, string password)
    {
        if (username is null || !Regex.IsMatch(username, @"\A[A-Za-z0-9][A-Za-z0-9._-]{2,31}\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("账户名应为 3～32 位字母、数字或 . _ -，并以字母或数字开头。");
        if (password is null || password.Length is < 12 or > 128 || !password.Any(char.IsLetter) || !password.Any(char.IsDigit) || password.Any(char.IsControl))
            throw new ArgumentException("密码应为 12～128 个字符，至少包含字母和数字，不可包含控制字符。");
    }

    public static string NormalizePublicOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        value = value.Trim();
        if (value.Length > 2048 || value.Any(c => char.IsControl(c) || c > 127) || value.Contains('\\') || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0
            || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
            throw new ArgumentException("公开访问地址应为完整的 HTTP 或 HTTPS origin，例如 https://mumu.example.com，不包含路径、参数或账户信息；国际域名请使用 Punycode。");
        return uri.GetLeftPart(UriPartial.Authority);
    }
    public static IReadOnlyList<IPAddress> LocalAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
        .Where(a => !IPAddress.IsLoopback(a) && !a.IsIPv6LinkLocal && a.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork or System.Net.Sockets.AddressFamily.InterNetworkV6)
        .Distinct().Take(32).ToArray();
    private void Save(AccessDocument data) => AtomicWrite(_path, JsonSerializer.SerializeToUtf8Bytes(data, Core.AppJson.Options));
    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Audit(string message, string ip)
    {
        lock (_sync)
        {
            try
            {
                var path = Path.Combine(_directory, "security.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Move(path, path + ".1", true);
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} [{ip.Replace('\r', ' ').Replace('\n', ' ')}] {message.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    public static bool TokenMatches(string? actual, string expected) => actual is not null && actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));
    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string SessionKey(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public void Dispose() { lock (_sync) _sessions.Clear(); _credentialGate.Dispose(); _hashGate.Dispose(); }
    private sealed record AccessDocument
    {
        public int Version { get; init; } = 1;
        public string? Username { get; init; }
        public string Salt { get; init; } = "";
        public string PasswordHash { get; init; } = "";
        public int Iterations { get; init; } = HashIterations;
        public bool RemoteEnabled { get; init; }
        public int RemotePort { get; init; }
        public string PublicOrigin { get; init; } = "";
    }
}

public sealed class AccessSession(string cookie, string token, bool local, string ip, DateTimeOffset createdAt, DateTimeOffset expiresAt)
{
    public string Cookie { get; } = cookie;
    public string Token { get; } = token;
    public bool Local { get; } = local;
    public string Ip { get; } = ip;
    public DateTimeOffset LastSeenAt { get; set; } = createdAt;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
}
public sealed class AccessLimitException : Exception { public AccessLimitException() : base("登录尝试过于频繁，请稍后重试。") { } }

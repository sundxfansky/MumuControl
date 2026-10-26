using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MumuControl.Core;

/// <summary>
/// Detailed troubleshooting log (diagnostics.log) plus a run marker (run.json) that reveals
/// abnormal exits. The user-facing activity log stays concise; this file keeps exception
/// details, lifecycle events and periodic resource snapshots. Never record passwords or tokens.
/// </summary>
public static class DiagnosticLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Sync = new();
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly Dictionary<string, DateTimeOffset> Throttled = [];
    private static string? _path;
    private static string? _runPath;
    private static Timer? _heartbeat;
    private static DateTimeOffset _startedAt;
    private static long _captures, _captureFailures;

    public static string? DirectoryPath { get; private set; }

    public sealed record PreviousRun(int Pid, DateTimeOffset StartedAt, DateTimeOffset LastSeenAt, string? LastNote);
    private sealed record RunMarker(int Pid, DateTimeOffset StartedAt, DateTimeOffset LastSeenAt, string? LastNote);

    /// <summary>Starts logging; returns the previous run if it ended without a clean shutdown.</summary>
    public static PreviousRun? Start(string directory, string mode)
    {
        Directory.CreateDirectory(directory);
        DirectoryPath = directory;
        _path = Path.Combine(directory, "diagnostics.log");
        _runPath = Path.Combine(directory, "run.json");
        _startedAt = DateTimeOffset.UtcNow;
        PreviousRun? previous = null;
        try
        {
            if (File.Exists(_runPath) && JsonSerializer.Deserialize<RunMarker>(File.ReadAllText(_runPath), AppJson.Options) is {} marker)
                previous = new PreviousRun(marker.Pid, marker.StartedAt, marker.LastSeenAt, marker.LastNote);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        using var self = Process.GetCurrentProcess();
        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";
        Write("info", $"==== 启动 v{version} PID {self.Id} 模式 {mode} | {Environment.OSVersion} | .NET {Environment.Version} | {(Environment.Is64BitProcess ? "x64" : "x86")}");
        if (previous is not null)
            Write("warning", $"上次运行（PID {previous.Pid}，启动于 {previous.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}）未正常退出，最后存活于 {previous.LastSeenAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}。最后状态：{previous.LastNote ?? "无"}");
        Beat();
        _heartbeat = new Timer(_ => Beat(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        return previous;
    }

    /// <summary>Records a clean shutdown; the next start will not report an abnormal exit.</summary>
    public static void Stop(string reason)
    {
        _heartbeat?.Dispose();
        _heartbeat = null;
        Write("info", $"==== 正常退出：{reason}，运行 {(DateTimeOffset.UtcNow - _startedAt).TotalHours:0.0} 小时");
        lock (Sync)
        {
            try { if (_runPath is not null) File.Delete(_runPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static void Write(string level, string message, Exception? exception = null)
    {
        var path = _path;
        if (path is null) return;
        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).Append(" [").Append(level).Append("] ")
            .Append('(').Append(Environment.CurrentManagedThreadId).Append(") ")
            .Append(message.Replace('\r', ' ').Replace('\n', ' '));
        if (exception is not null) line.AppendLine().Append("    ").Append(exception.ToString().Replace("\n", "\n    "));
        line.AppendLine();
        lock (Sync)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Move(path, path + ".1", true);
                File.AppendAllText(path, line.ToString(), Utf8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Writes at most once per interval for the same key, e.g. a repeating capture failure.</summary>
    public static void WriteThrottled(string key, TimeSpan interval, string level, string message, Exception? exception = null)
    {
        var now = DateTimeOffset.UtcNow;
        lock (Throttled)
        {
            if (Throttled.TryGetValue(key, out var last) && now - last < interval) return;
            if (Throttled.Count > 256) Throttled.Clear();
            Throttled[key] = now;
        }
        Write(level, message, exception);
    }

    public static void CountCapture(bool success)
    {
        if (success) Interlocked.Increment(ref _captures);
        else Interlocked.Increment(ref _captureFailures);
    }

    private static void Beat()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            long captures = Interlocked.Exchange(ref _captures, 0), failures = Interlocked.Exchange(ref _captureFailures, 0);
            var note = $"内存 {self.WorkingSet64 / 1048576} MB / 私有 {self.PrivateMemorySize64 / 1048576} MB，句柄 {self.HandleCount}，线程 {self.Threads.Count}，近 1 分钟画面抓取 {captures} 次、失败 {failures} 次";
            var marker = new RunMarker(self.Id, _startedAt, DateTimeOffset.UtcNow, note);
            lock (Sync)
            {
                if (_runPath is null) return;
                File.WriteAllText(_runPath, JsonSerializer.Serialize(marker, AppJson.Options), Utf8);
            }
            // A resource snapshot every 10 minutes is enough to spot leaks without growing the file.
            WriteThrottled("heartbeat", TimeSpan.FromMinutes(10), "info", "运行状态：" + note);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }
}

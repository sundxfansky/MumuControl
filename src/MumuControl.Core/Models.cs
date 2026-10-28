using System.Text.Json;

namespace MumuControl.Core;

public sealed record VmInfo(int Index, string Name, string Status, int Pid, int AdbPort, string AdbHost, DateTimeOffset? StartedAt);

public interface IMuMuClient : IDisposable
{
    Task<IReadOnlyList<VmInfo>> ListAsync(string managerPath, CancellationToken ct = default);
    Task ControlAsync(string managerPath, int index, string action, CancellationToken ct = default);
    Task<byte[]> ScreenshotAsync(string managerPath, VmInfo vm, CancellationToken ct = default);
    /// <summary>Low-cost live frame from the renderer, longer side at most maxSide pixels.</summary>
    Task<RawFrame> ThumbnailAsync(string managerPath, VmInfo vm, int maxSide, CancellationToken ct = default) =>
        throw new NotSupportedException("当前接口不支持实时画面。");
    /// <summary>Installs (or replaces) an APK on one running instance; throws with the reason on failure.</summary>
    Task InstallApkAsync(string managerPath, VmInfo vm, string apkPath, CancellationToken ct = default) =>
        throw new NotSupportedException("当前接口不支持安装 APK。");
}

/// <summary>Produces the local APK file for a batch install (download, copy or an already staged upload).</summary>
public delegate Task<string> ApkPreparer(Action<long, long?> progress, CancellationToken ct);

public sealed record ApkInstallItem(int Index, string Name, string Status, string? Message = null);

public sealed record ApkInstallJob
{
    public string Id { get; init; } = "";
    public string FileName { get; init; } = "";
    /// <summary>upload, url or path.</summary>
    public string Source { get; init; } = "";
    /// <summary>preparing, installing or done.</summary>
    public string Phase { get; init; } = "preparing";
    public long? ReceivedBytes { get; init; }
    public long? TotalBytes { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public IReadOnlyList<ApkInstallItem> Items { get; init; } = [];
}

public sealed record AppSettings
{
    public string ManagerPath { get; init; } = "";
    public int Port { get; init; } = 17830;
    public int PollIntervalSeconds { get; init; } = 5;
    public int PreviewIntervalSeconds { get; init; } = 3;
    public bool CloseToTray { get; init; } = true;
}

public sealed record ScheduledTask
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public int VmIndex { get; init; }
    public string Action { get; init; } = "start";
    public string Trigger { get; init; } = "once";
    public string At { get; init; } = "";
    public int? DurationMinutes { get; init; }
    public bool Enabled { get; init; } = true;
    public DateTimeOffset? NextRunAt { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
    public string? LastResult { get; init; }
}

public sealed record AutoStop(int VmIndex, DateTimeOffset DueAt, int Pid, DateTimeOffset CreatedAt, DateTimeOffset? RetryAt = null, int Attempts = 0);
public sealed record LogEntry(DateTimeOffset At, string Level, string Message);
public sealed record StateDocument
{
    public int Version { get; init; } = 1;
    public AppSettings Settings { get; init; } = new();
    public List<ScheduledTask> Tasks { get; init; } = [];
    public List<AutoStop> AutoStops { get; init; } = [];
}

public static class AppJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

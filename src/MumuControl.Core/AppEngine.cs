using System.Diagnostics;
using System.Globalization;

namespace MumuControl.Core;

public static class ScheduleRules
{
    public static readonly TimeSpan CatchUpWindow = TimeSpan.FromMinutes(5);
    public static DateTimeOffset NextDaily(string at, DateTimeOffset now)
    {
        if (!TimeOnly.TryParseExact(at, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw new ArgumentException("每日时间必须为 HH:mm。");
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local);
        var target = DateTime.SpecifyKind(local.Date.Add(time.ToTimeSpan()), DateTimeKind.Unspecified);
        if (target <= local.DateTime) target = target.AddDays(1);
        // DST 春季不存在的分钟向后推至有效时间；秋季重叠分钟只安排一次。
        while (TimeZoneInfo.Local.IsInvalidTime(target)) target = target.AddMinutes(1);
        return new DateTimeOffset(target, TimeZoneInfo.Local.GetUtcOffset(target)).ToUniversalTime();
    }
    public static ScheduledTask Validate(ScheduledTask task, DateTimeOffset now)
    {
        if (task.VmIndex < 0 || task.VmIndex > 100000) throw new ArgumentException("模拟器编号无效。");
        if (task.Action is not ("start" or "stop")) throw new ArgumentException("任务操作必须是启动或停止。");
        if (task.Trigger is not ("once" or "daily")) throw new ArgumentException("任务类型必须是单次或每日。");
        ValidateDuration(task.DurationMinutes);
        if (task.Action == "stop" && task.DurationMinutes is not null)
            throw new ArgumentException("停止任务不能附带倒计时。");
        DateTimeOffset next;
        if (task.Trigger == "daily") next = NextDaily(task.At, now);
        else if (!DateTimeOffset.TryParse(task.At, CultureInfo.InvariantCulture, DateTimeStyles.None, out next) || next <= now)
            throw new ArgumentException("请选择未来的执行时间。");
        return task with { NextRunAt = next.ToUniversalTime() };
    }
    public static void ValidateDuration(int? minutes)
    {
        if (minutes is < 1 or > 10080) throw new ArgumentException("倒计时应为 1～10080 分钟。");
    }
}

public sealed class AppEngine : IAsyncDisposable
{
    private readonly IMuMuClient _client;
    private readonly StateStore _store;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly object _viewLock = new();
    private readonly Queue<LogEntry> _logs = new();
    private readonly Dictionary<int, DateTimeOffset> _unverifiedTimerRetryAt = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DateTimeOffset _createdAt;
    private StateDocument _document;
    private IReadOnlyList<VmInfo> _vms = [];
    private bool _connected;
    private string? _connectionError;
    private DateTimeOffset? _updatedAt;
    private DateTimeOffset _nextPoll = DateTimeOffset.MinValue;
    private Task? _worker;

    public AppEngine(IMuMuClient client, StateStore store, TimeProvider? clock = null)
    {
        _client = client;
        _store = store;
        _clock = clock ?? TimeProvider.System;
        _createdAt = _clock.GetUtcNow();
        _document = store.Load();
        ValidateSettings(_document.Settings);
        if (string.IsNullOrWhiteSpace(_document.Settings.ManagerPath))
            _document = _document with { Settings = _document.Settings with { ManagerPath = MuMuClient.DiscoverManager() ?? "" } };
        foreach (var task in _document.Tasks)
            if (task.Id.Length > 64 || task.Action is not ("start" or "stop") || task.Trigger is not ("once" or "daily"))
                throw new InvalidDataException("配置中的计划任务格式无效。");
    }

    public AppSettings Settings { get { lock (_viewLock) return _document.Settings; } }
    public void Start()
    {
        if (_worker is not null) throw new InvalidOperationException("服务已启动。");
        Log("info", "本地服务已启动，定时任务将在程序运行期间执行。");
        _worker = Task.Run(WorkerAsync);
    }

    public object GetState(bool includeLocalSettings = true)
    {
        lock (_viewLock)
        {
            var now = _clock.GetUtcNow();
            using var process = Process.GetCurrentProcess();
            return new
            {
                version = "1.1.1",
                emulators = _vms.Select(vm => new
                {
                    vm.Index, vm.Name,
                    status = _connected ? vm.Status : "unknown", vm.Pid, vm.AdbPort, vm.StartedAt,
                    uptimeSeconds = _connected && vm.StartedAt is {} since && vm.Status is "running" or "starting" ? (double?)Math.Max(0, (now - since).TotalSeconds) : null,
                    autoStopAt = _document.AutoStops.FirstOrDefault(a => a.VmIndex == vm.Index)?.DueAt
                }).ToArray(),
                tasks = _document.Tasks.ToArray(),
                settings = includeLocalSettings ? _document.Settings : _document.Settings with { ManagerPath = "" },
                connection = new { connected = _connected, managerPath = includeLocalSettings ? _document.Settings.ManagerPath : null, error = _connectionError, lastUpdatedAt = _updatedAt },
                metrics = new { workingSetMb = Math.Round(process.WorkingSet64 / 1048576d, 1), uptimeSeconds = (now - _createdAt).TotalSeconds }
            };
        }
    }
    public object GetLogs() { lock (_viewLock) return new { entries = _logs.Reverse().ToArray() }; }

    private async Task WorkerAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _clock);
        try
        {
            do
            {
                try
                {
                    await RefreshIfDueAsync(_shutdown.Token);
                    await TickAsync(_shutdown.Token);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
                catch (Exception ex) { Log("error", "后台任务：" + ex.Message); }
            } while (await timer.WaitForNextTickAsync(_shutdown.Token));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await RefreshLockedAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task RefreshIfDueAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_clock.GetUtcNow() >= _nextPoll) await RefreshLockedAsync(ct);
        }
        finally { _gate.Release(); }
    }

    // All calls hold _gate, including the mandatory check immediately before a scheduled action.
    private async Task RefreshLockedAsync(CancellationToken ct)
    {
            var now = _clock.GetUtcNow();
            _nextPoll = now.AddSeconds(Settings.PollIntervalSeconds);
            try
            {
                var path = Settings.ManagerPath;
                if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("未找到 MuMuManager.exe，请在设置中指定安装路径。");
                var list = await _client.ListAsync(path, ct);
                bool wasConnected;
                lock (_viewLock)
                {
                    wasConnected = _connected;
                    _vms = list;
                    _connected = true;
                    _connectionError = null;
                    _updatedAt = now;
                }
                if (!wasConnected) Log("info", $"已连接 MuMu，发现 {list.Count} 个模拟器。");
                // Countdowns are bound to the emulator index only. Polling never cancels them:
                // PID changes, slow launches and app restarts must not drop a scheduled stop.
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                bool changed;
                lock (_viewLock)
                {
                    changed = _connected || _connectionError != ex.Message;
                    _connected = false;
                    _connectionError = ex.Message;
                }
                if (changed) Log("error", ex.Message);
            }
    }

    public async Task ControlAsync(int index, string action, int? durationMinutes = null, CancellationToken ct = default)
    {
        ScheduleRules.ValidateDuration(durationMinutes);
        if (action is not ("start" or "stop" or "show" or "hide" or "home" or "back")) throw new ArgumentException("不支持的快捷操作。");
        if (durationMinutes is not null && action != "start") throw new ArgumentException("只有启动操作可以附带倒计时。");
        await _gate.WaitAsync(ct);
        try { await ControlLockedAsync(index, action, durationMinutes, ct); }
        finally { _gate.Release(); }
    }
    private async Task ControlLockedAsync(int index, string action, int? durationMinutes, CancellationToken ct)
    {
        var vm = _vms.FirstOrDefault(v => v.Index == index) ?? throw new ArgumentException("未找到该模拟器，请刷新列表。");
        if (!_connected) throw new InvalidOperationException("MuMu 未连接，请先检查设置并刷新。");
        if (action == "start" && vm.Status == "stopping") throw new InvalidOperationException("模拟器正在停止，请稍后再启动。");
        if (action is "show" or "hide" or "home" or "back" && vm.Status is not ("running" or "starting"))
            throw new InvalidOperationException("请先启动该模拟器。");
        var alreadyActive = vm.Status is "running" or "starting";
        if (!(action == "start" && alreadyActive) && !(action == "stop" && vm.Status is "stopped" or "stopping"))
            await _client.ControlAsync(Settings.ManagerPath, index, action, ct);
        var now = _clock.GetUtcNow();
        var timers = _document.AutoStops.ToList();
        if (action is "start" or "stop")
        {
            // An idempotent plain "start" on a running VM must not cancel its existing timer.
            if (action == "stop" || durationMinutes is not null || !alreadyActive)
                timers.RemoveAll(a => a.VmIndex == index);
            if (action == "start" && durationMinutes is {} minutes)
                timers.Add(new(index, now.AddMinutes(minutes), alreadyActive ? vm.Pid : 0, now));
            // Once MuMu accepted the command, a disconnected HTTP caller must not cancel persistence.
            await CommitAsync(_document with { AutoStops = timers }, CancellationToken.None);
            if (action == "stop" || durationMinutes is not null || !alreadyActive)
                _unverifiedTimerRetryAt.Remove(index);
            lock (_viewLock)
            {
                _vms = _vms.Select(v => v.Index == index ? v with
                {
                    Status = action == "stop" ? v.Status == "stopped" ? "stopped" : "stopping" : v.Status == "running" ? "running" : "starting",
                    Pid = action == "start" && !alreadyActive ? 0 : v.Pid,
                    StartedAt = action == "start" ? alreadyActive ? v.StartedAt ?? now : now : v.StartedAt
                } : v).ToArray();
            }
            _nextPoll = DateTimeOffset.MinValue;
        }
        Log("info", $"#{index} {vm.Name}：{ActionName(action)}" + (durationMinutes is {} m ? $"，{m} 分钟后自动停止" : ""));
    }
    private static string ActionName(string action) => action switch { "start" => "启动", "stop" => "停止", "show" => "显示窗口", "hide" => "隐藏窗口", "home" => "返回桌面", "back" => "返回", _ => action };

    public async Task SetAutoStopAsync(int index, int? minutes, CancellationToken ct = default)
    {
        ScheduleRules.ValidateDuration(minutes);
        await _gate.WaitAsync(ct);
        try
        {
            var vm = _vms.FirstOrDefault(v => v.Index == index) ?? throw new ArgumentException("模拟器不存在。");
            if (!_connected || vm.Status is not ("running" or "starting")) throw new InvalidOperationException("请先启动模拟器。");
            var timers = _document.AutoStops.Where(a => a.VmIndex != index).ToList();
            var now = _clock.GetUtcNow();
            if (minutes is {} m) timers.Add(new(index, now.AddMinutes(m), vm.Pid, now));
            await CommitAsync(_document with { AutoStops = timers }, ct);
            _unverifiedTimerRetryAt.Remove(index);
            Log("info", $"#{index} " + (minutes is {} duration ? $"将在 {duration} 分钟后停止。" : "已取消倒计时。"));
        }
        finally { _gate.Release(); }
    }

    public async Task<ScheduledTask> SaveTaskAsync(ScheduledTask input, string? id = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_document.Tasks.Count >= 500 && id is null) throw new ArgumentException("计划任务最多 500 条。");
            if (!_vms.Any(v => v.Index == input.VmIndex)) throw new ArgumentException("请选择存在的模拟器。");
            var existing = id is null ? null : _document.Tasks.FirstOrDefault(t => t.Id == id) ?? throw new KeyNotFoundException("任务不存在。");
            var task = ScheduleRules.Validate(input, _clock.GetUtcNow()) with { Id = existing?.Id ?? Guid.NewGuid().ToString("N"), LastRunAt = existing?.LastRunAt, LastResult = existing?.LastResult };
            var tasks = _document.Tasks.Where(t => t.Id != task.Id).Append(task).ToList();
            await CommitAsync(_document with { Tasks = tasks }, ct);
            Log("info", $"已保存 #{task.VmIndex} {ActionName(task.Action)}计划。");
            return task;
        }
        finally { _gate.Release(); }
    }
    public async Task DeleteTaskAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_document.Tasks.Any(t => t.Id == id)) throw new KeyNotFoundException("任务不存在。");
            await CommitAsync(_document with { Tasks = _document.Tasks.Where(t => t.Id != id).ToList() }, ct);
            Log("info", "已删除计划任务。");
        }
        finally { _gate.Release(); }
    }

    public async Task TickAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = _clock.GetUtcNow();
            var hasDueAction = _document.AutoStops.Any(a => IsAutoStopDue(a, now))
                || _document.Tasks.Any(t => t.Enabled && t.NextRunAt <= now && now - t.NextRunAt.Value <= ScheduleRules.CatchUpWindow);
            // Cached PIDs may have changed since the last poll (for example an external restart).
            // Never issue a scheduled stop against an unverified cached process.
            if (_connected && hasDueAction) await RefreshLockedAsync(ct);
            now = _clock.GetUtcNow();
            foreach (var timer in _document.AutoStops.Where(a => IsAutoStopDue(a, now)).ToArray())
            {
                // While MuMu is unreachable the timer stays due and runs as soon as it reconnects.
                if (!_connected) continue;
                var vm = _vms.FirstOrDefault(v => v.Index == timer.VmIndex);
                if (vm is null || vm.Status == "stopped")
                {
                    await CommitAsync(_document with { AutoStops = _document.AutoStops.Where(a => a.VmIndex != timer.VmIndex).ToList() }, ct);
                    Log("info", vm is null ? $"#{timer.VmIndex} 模拟器已不存在，倒计时结束。" : $"#{timer.VmIndex} {vm.Name} 已处于停止状态，倒计时结束。");
                    continue;
                }
                if (vm.Status == "stopping")
                {
                    // Already shutting down: re-check next poll instead of sending a second stop.
                    _unverifiedTimerRetryAt[timer.VmIndex] = now.AddSeconds(Settings.PollIntervalSeconds);
                    continue;
                }
                _unverifiedTimerRetryAt.Remove(timer.VmIndex);
                Log("info", $"#{timer.VmIndex} {vm.Name} 倒计时到期，执行自动停止。");
                // Any non-stopped state (running, starting, unknown) is stopped by index.
                try { await ControlLockedAsync(timer.VmIndex, "stop", null, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    int attempts = timer.Attempts + 1;
                    var retry = timer with { Attempts = attempts, RetryAt = now.AddSeconds(Math.Min(300, 15 * attempts)) };
                    await CommitAsync(_document with { AutoStops = _document.AutoStops.Select(a => a.VmIndex == timer.VmIndex ? retry : a).ToList() }, ct);
                    Log("error", $"#{timer.VmIndex} 自动停止失败，稍后重试：{ex.Message}");
                }
            }
            foreach (var task in _document.Tasks.Where(t => t.Enabled && t.NextRunAt <= now).ToArray())
            {
                bool missed = now - task.NextRunAt!.Value > ScheduleRules.CatchUpWindow;
                if (!_connected && !missed) continue;
                var claimed = task with
                {
                    Enabled = task.Trigger == "daily",
                    NextRunAt = task.Trigger == "daily" ? ScheduleRules.NextDaily(task.At, now) : null,
                    LastRunAt = now,
                    LastResult = missed ? "已跳过：错过执行时间超过 5 分钟" : "执行中（若程序意外退出，请检查实例并手动重试）"
                };
                await ReplaceTaskAsync(claimed, ct); // 先持久化认领，防止重启后重复执行启动操作。
                if (missed) { Log("warning", $"#{task.VmIndex} 计划已跳过：错过执行时间超过 5 分钟。"); continue; }
                try
                {
                    await ControlLockedAsync(task.VmIndex, task.Action, task.DurationMinutes, ct);
                    await ReplaceTaskAsync(claimed with { LastResult = "执行成功" }, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    await ReplaceTaskAsync(claimed with { LastResult = "执行失败：" + ex.Message }, ct);
                    Log("error", $"#{task.VmIndex} 计划失败：{ex.Message}");
                }
            }
        }
        finally { _gate.Release(); }
    }
    private bool IsAutoStopDue(AutoStop timer, DateTimeOffset now) => timer.DueAt <= now
        && (timer.RetryAt is null || timer.RetryAt <= now)
        && (!_unverifiedTimerRetryAt.TryGetValue(timer.VmIndex, out var retryAt) || retryAt <= now);

    private Task ReplaceTaskAsync(ScheduledTask task, CancellationToken ct) => CommitAsync(_document with { Tasks = _document.Tasks.Select(t => t.Id == task.Id ? task : t).ToList() }, ct);

    public async Task<bool> UpdateSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        ValidateSettings(settings);
        settings = settings with { ManagerPath = settings.ManagerPath.Trim().Trim('"') };
        ValidateSettings(settings);
        if (settings.ManagerPath.Length > 0 && (!File.Exists(settings.ManagerPath) || !Path.GetFileName(settings.ManagerPath).Equals("MuMuManager.exe", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("请填写实际存在的 MuMuManager.exe 完整路径。");
        await _gate.WaitAsync(ct);
        try
        {
            var restartRequired = settings.Port != _document.Settings.Port;
            var pathChanged = settings.ManagerPath != _document.Settings.ManagerPath;
            await CommitAsync(_document with { Settings = settings }, ct);
            if (pathChanged) { lock (_viewLock) { _connected = false; _vms = []; } }
            _nextPoll = DateTimeOffset.MinValue;
            Log("info", restartRequired ? "设置已保存，端口将在下次启动时生效。" : "设置已保存。");
            return restartRequired;
        }
        finally { _gate.Release(); }
    }
    public static void ValidateSettings(AppSettings settings)
    {
        if (settings.ManagerPath is null || settings.ManagerPath.Length > 2048) throw new ArgumentException("安装路径无效。");
        if (settings.Port is < 1024 or > 65535) throw new ArgumentException("本地端口应为 1024～65535。");
        if (settings.PollIntervalSeconds is < 3 or > 60) throw new ArgumentException("状态刷新间隔应为 3～60 秒。");
        if (settings.PreviewIntervalSeconds is < 1 or > 30) throw new ArgumentException("画面刷新间隔应为 1～30 秒。");
    }
    public async Task<byte[]> ScreenshotAsync(int index, CancellationToken ct = default)
    {
        if (!await _captureGate.WaitAsync(0, ct)) throw new InvalidOperationException("正在获取画面，请稍后重试。");
        try
        {
            VmInfo vm;
            string path;
            lock (_viewLock)
            {
                if (!_connected) throw new InvalidOperationException("MuMu 未连接。");
                vm = _vms.FirstOrDefault(v => v.Index == index) ?? throw new ArgumentException("模拟器不存在。");
                path = _document.Settings.ManagerPath;
            }
            if (vm.Status != "running") throw new InvalidOperationException("Android 尚未启动完成，暂时无法获取画面。");
            return await _client.ScreenshotAsync(path, vm, ct);
        }
        finally { _captureGate.Release(); }
    }

    public Task<RawFrame> ThumbnailAsync(int index, int maxSide, CancellationToken ct = default)
    {
        VmInfo vm;
        string path;
        lock (_viewLock)
        {
            if (!_connected) throw new InvalidOperationException("MuMu 未连接。");
            vm = _vms.FirstOrDefault(v => v.Index == index) ?? throw new ArgumentException("模拟器不存在。");
            path = _document.Settings.ManagerPath;
        }
        if (vm.Status != "running") throw new InvalidOperationException("Android 尚未启动完成，暂时无法获取画面。");
        return _client.ThumbnailAsync(path, vm, maxSide, ct);
    }

    private async Task CommitAsync(StateDocument document, CancellationToken ct)
    {
        await _store.SaveAsync(document, ct);
        lock (_viewLock) _document = document;
        foreach (var index in _unverifiedTimerRetryAt.Keys.Where(index => !document.AutoStops.Any(a => a.VmIndex == index)).ToArray())
            _unverifiedTimerRetryAt.Remove(index);
    }
    private void Log(string level, string message)
    {
        var entry = new LogEntry(_clock.GetUtcNow(), level, message);
        lock (_viewLock)
        {
            _logs.Enqueue(entry);
            while (_logs.Count > 200) _logs.Dequeue();
            try
            {
                Directory.CreateDirectory(_store.DirectoryPath);
                var path = Path.Combine(_store.DirectoryPath, "activity.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Move(path, path + ".1", true);
                File.AppendAllText(path, $"{entry.At:O} [{level}] {message.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_worker is not null) await _worker;
        // HTTP server is stopped before this point, so no new operations can enter.
        await _gate.WaitAsync();
        await _captureGate.WaitAsync();
        _client.Dispose();
        _captureGate.Release();
        _gate.Release();
        _shutdown.Dispose();
        _captureGate.Dispose();
        _gate.Dispose();
    }
}

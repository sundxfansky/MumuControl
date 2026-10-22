using System.Text.Json;
using MumuControl.Core;

var cases = new List<(string Name, Func<Task> Run)>
{
    ("Official CLI dictionary, names and process states", Tests.ParseDictionary),
    ("Official CLI arrays and wrapped results", Tests.ParseWrapped),
    ("CLI error envelopes and malformed output are rejected", Tests.ParseErrors),
    ("Start countdown stops exactly once", Tests.StartCountdown),
    ("Countdown survives application restart", Tests.RestoreCountdown),
    ("PID change keeps countdown bound to the emulator index", Tests.PidChange),
    ("Slow launch without PID keeps countdown", Tests.SlowLaunchKeepsCountdown),
    ("Instance stopped before due time clears countdown", Tests.StoppedBeforeDueClearsCountdown),
    ("Stopping instance defers without duplicate stop", Tests.StoppingDefersWithoutDuplicateStop),
    ("One-time schedule executes exactly once across restart", Tests.OnceSchedule),
    ("Daily schedule executes once per day", Tests.DailySchedule),
    ("Schedules missed by more than five minutes are skipped", Tests.MissedSchedule),
    ("Failed automatic stop retries with backoff", Tests.StopRetry),
    ("Disconnected state defers stop until recovery", Tests.ConnectionRecovery),
    ("State backup recovers damaged primary document", Tests.BackupRecovery),
    ("Expired timer stops by index despite stale cached PID", Tests.FreshPidBeforeStop),
    ("Repeated stop preserves stopped status", Tests.RepeatedStop),
    ("Plain start preserves existing countdown", Tests.PreserveTimerOnStart),
    ("Countdown survives app restart before PID is known", Tests.RestoredUnconfirmedTimer),
    ("Missing PID does not block a due countdown", Tests.MissingPidBackoff),
    ("Unknown emulator uptime remains null", Tests.UnknownUptime),
    ("Renderer frames are flipped upright and downscaled", () => { Tests.ThumbnailFlip(); return Task.CompletedTask; })
};
if (args.Contains("--live", StringComparer.Ordinal)) cases.Add(("Read-only installed MuMuManager integration", Tests.LiveList));
if (args.Contains("--live-screenshots", StringComparer.Ordinal)) cases.Add(("All running instances capture through local forwarded ADB ports", Tests.LiveScreenshots));
if (args.Contains("--live-thumbnails", StringComparer.Ordinal)) cases.Add(("Running instances capture upright thumbnails through the renderer interface", Tests.LiveThumbnails));
var failures = 0;
foreach (var test in cases)
{
    try { await test.Run(); Console.WriteLine($"PASS  {test.Name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL  {test.Name}\n{ex}"); }
}
Console.WriteLine($"{cases.Count - failures}/{cases.Count} checks passed.");
return failures == 0 ? 0 : 1;

internal static class Tests
{
    public static Task ParseDictionary()
    {
        var list = MuMuClient.ParseInfo("""
            {"10":{"index":"10","name":"中文 🎮","is_process_started":false,"is_android_started":false,"error_code":0},
             "2":{"index":"2","name":"正在运行","is_process_started":true,"is_android_started":true,"pid":2147483000,"adb_port":16448,"adb_host_ip":"127.0.0.1","player_state":"start_finished"},
             "3":{"index":"3","name":"正在启动","is_process_started":true,"is_android_started":false,"headless_pid":2147483001},
             "4":{"index":"4","name":"正在停止","is_process_started":true,"is_android_started":true,"player_state":"stopping"}}
            """);
        Equal(4, list.Count);
        Equal(2, list[0].Index);
        Equal("running", list[0].Status);
        Equal(16448, list[0].AdbPort);
        Equal("starting", list[1].Status);
        Equal(2147483001, list[1].Pid);
        Equal("stopping", list[2].Status);
        Equal("中文 🎮", list[3].Name);
        Equal("stopped", list[3].Status);
        True(list[0].StartedAt is null, "Missing processes must not fabricate an uptime.");
        return Task.CompletedTask;
    }

    public static Task ParseWrapped()
    {
        var arrays = MuMuClient.ParseInfo("""{"error_code":0,"data":{"results":[{"index":7,"name":"seven","is_process_started":true,"is_android_started":false}]}}""");
        Equal(7, arrays.Single().Index);
        Equal("starting", arrays.Single().Status);
        var keyed = MuMuClient.ParseInfo("""{"result":{"12":{"name":"no explicit index","is_process_started":false}}}""");
        Equal(12, keyed.Single().Index);
        Equal(0, MuMuClient.ParseInfo("{}").Count);
        Equal(0, MuMuClient.ParseInfo("[]").Count);
        Equal(0, MuMuClient.ParseInfo("""{"error_code":0,"data":[]}""").Count);
        return Task.CompletedTask;
    }

    public static Task ParseErrors()
    {
        Throws<InvalidOperationException>(() => MuMuClient.ParseInfo("""{"error_code":-200,"error_msg":"missing"}"""));
        Throws<InvalidOperationException>(() => MuMuClient.ParseInfo("""{"data":{"0":{"error_code":"4","message":"no access"}}}"""));
        Throws<InvalidOperationException>(() => MuMuClient.ParseInfo("""{"errcode":-200,"errmsg":"player index not found"}"""));
        Throws<InvalidDataException>(() => MuMuClient.ParseInfo("not json"));
        Throws<InvalidDataException>(() => MuMuClient.ParseInfo("""{"unexpected":true}"""));
        return Task.CompletedTask;
    }

    public static async Task StartCountdown()
    {
        await using var f = await Fixture.Create();
        await f.Engine.ControlAsync(2, "start", 30);
        Equal(1, f.Client.Count("start"));
        await f.Engine.RefreshAsync();
        Equal(1, f.Store.Load().AutoStops.Count);
        f.Clock.Advance(TimeSpan.FromMinutes(29));
        await f.Engine.TickAsync();
        Equal(0, f.Client.Count("stop"));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await f.Engine.TickAsync();
        await f.Engine.TickAsync();
        Equal(1, f.Client.Count("stop"));
        Equal(0, f.Store.Load().AutoStops.Count);
    }

    public static async Task RestoreCountdown()
    {
        await using var f = await Fixture.Create(running: true);
        await f.Engine.SetAutoStopAsync(2, 60);
        f.Clock.Advance(TimeSpan.FromMinutes(59));
        await f.Restart();
        Equal(1, f.Store.Load().AutoStops.Count);
        await f.Engine.TickAsync();
        Equal(0, f.Client.Count("stop"));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await f.Engine.TickAsync();
        await f.Engine.TickAsync();
        Equal(1, f.Client.Count("stop"));
    }

    public static async Task PidChange()
    {
        await using var f = await Fixture.Create(running: true);
        await f.Engine.SetAutoStopAsync(2, 30);
        // MuMu reports a different PID (restart, process swap): the countdown is bound to the index.
        f.Client.Vm = f.Client.Vm with { Pid = 999, StartedAt = f.Clock.GetUtcNow().AddSeconds(1) };
        await f.Engine.RefreshAsync();
        Equal(1, f.Store.Load().AutoStops.Count);
        f.Clock.Advance(TimeSpan.FromMinutes(30));
        await f.Engine.TickAsync();
        Equal(1, f.Client.Count("stop"));
        Equal(0, f.Store.Load().AutoStops.Count);
    }

    public static async Task OnceSchedule()
    {
        await using var f = await Fixture.Create();
        var task = await f.Engine.SaveTaskAsync(new ScheduledTask
        {
            VmIndex = 2, Action = "start", Trigger = "once", At = f.Clock.GetUtcNow().AddMinutes(1).ToString("O"), DurationMinutes = 30
        });
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await f.Engine.TickAsync();
        await f.Engine.TickAsync();
        Equal(1, f.Client.Count("start"));
        var saved = f.Store.Load().Tasks.Single(t => t.Id == task.Id);
        True(!saved.Enabled && saved.NextRunAt is null, "One-time schedule must be persistently disabled after claiming.");
        Equal("执行成功", saved.LastResult);
        Equal(1, f.Store.Load().AutoStops.Count);
        await f.Restart();
        await f.Engine.TickAsync();
        Equal(1, f.Client.Count("start"));
    }

    public static async Task DailySchedule()
    {
        await using var f = await Fixture.Create(running: true);
        var task = await f.Engine.SaveTaskAsync(new ScheduledTask { VmIndex = 2, Action = "stop", Trigger = "daily", At = "14:20" });
        f.Clock.Set(task.NextRunAt!.Value);
        await f.Engine.TickAsync();
        await f.Engine.TickAsync();
        Equal(1, f.Client.Count("stop"));
        var saved = f.Store.Load().Tasks.Single();
        True(saved.Enabled && saved.NextRunAt > f.Clock.GetUtcNow(), "Daily task must advance to the next day before executing.");
        await f.Restart();
        await f.Engine.TickAsync();
        Equal(1, f.Client.Count("stop"));
        f.Client.Vm = f.Client.Vm with { Status = "running", Pid = 1002 };
        await f.Engine.RefreshAsync();
        f.Clock.Set(saved.NextRunAt!.Value);
        await f.Engine.TickAsync();
        await f.Engine.TickAsync();
        Equal(2, f.Client.Count("stop"));
    }

    public static async Task MissedSchedule()
    {
        await using var f = await Fixture.Create();
        var once = await f.Engine.SaveTaskAsync(new ScheduledTask { VmIndex = 2, Action = "start", Trigger = "once", At = f.Clock.GetUtcNow().AddMinutes(1).ToString("O") });
        var daily = await f.Engine.SaveTaskAsync(new ScheduledTask { VmIndex = 2, Action = "start", Trigger = "daily", At = "14:20" });
        f.Clock.Set(once.NextRunAt!.Value.AddMinutes(5).AddTicks(1));
        await f.Engine.TickAsync();
        Equal(0, f.Client.Calls.Count);
        True(f.Store.Load().Tasks.Single(t => t.Id == once.Id).LastResult!.Contains("跳过"), "Missed one-time schedule must explain why it was skipped.");
        f.Clock.Set(daily.NextRunAt!.Value.AddMinutes(6));
        await f.Engine.TickAsync();
        Equal(0, f.Client.Calls.Count);
        var savedDaily = f.Store.Load().Tasks.Single(t => t.Id == daily.Id);
        True(savedDaily.Enabled && savedDaily.NextRunAt > f.Clock.GetUtcNow(), "Missed daily schedule must advance without running.");
    }

    public static async Task StopRetry()
    {
        await using var f = await Fixture.Create(running: true);
        await f.Engine.SetAutoStopAsync(2, 1);
        f.Client.StopFailuresRemaining = 1;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await f.Engine.TickAsync();
        Equal(1, f.Client.Count("stop"));
        var retry = f.Store.Load().AutoStops.Single();
        Equal(1, retry.Attempts);
        Equal(f.Clock.GetUtcNow().AddSeconds(15), retry.RetryAt);
        await f.Engine.TickAsync();
        Equal(1, f.Client.Count("stop"));
        f.Clock.Advance(TimeSpan.FromSeconds(15));
        await f.Engine.TickAsync();
        await f.Engine.TickAsync();
        Equal(2, f.Client.Count("stop"));
        Equal(0, f.Store.Load().AutoStops.Count);
    }

    public static async Task ConnectionRecovery()
    {
        await using var f = await Fixture.Create(running: true);
        await f.Engine.SetAutoStopAsync(2, 1);
        f.Client.ListFailure = new IOException("MuMu temporarily unavailable");
        await f.Engine.RefreshAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await f.Engine.TickAsync();
        Equal(0, f.Client.Count("stop"));
        Equal(1, f.Store.Load().AutoStops.Count);
        f.Client.ListFailure = null;
        await f.Engine.RefreshAsync();
        await f.Engine.TickAsync();
        Equal(1, f.Client.Count("stop"));
    }

    public static async Task BackupRecovery()
    {
        await using var f = await Fixture.Create();
        var original = f.Store.Load();
        var withTask = original with { Tasks = [new ScheduledTask { VmIndex = 2, At = f.Clock.GetUtcNow().AddMinutes(1).ToString("O") }] };
        await f.Store.SaveAsync(withTask);
        await f.Store.SaveAsync(withTask with { Settings = withTask.Settings with { Port = 17831 } });
        await File.WriteAllTextAsync(Path.Combine(f.Directory, "state.json"), "{ interrupted write");
        var restored = f.Store.Load();
        Equal(17830, restored.Settings.Port);
        Equal(1, restored.Tasks.Count);
        True(File.Exists(Path.Combine(f.Directory, "state.json.corrupt")), "Recovery must preserve corrupted primary for inspection.");
        await f.Store.SaveAsync(restored);
        Equal(1, f.Store.Load().Tasks.Count);
        // A second primary corruption must still have a healthy backup after recovery + save.
        await File.WriteAllTextAsync(Path.Combine(f.Directory, "state.json"), "{ interrupted again");
        Equal(1, f.Store.Load().Tasks.Count);
    }

    public static async Task FreshPidBeforeStop()
    {
        await using var f = await Fixture.Create(running: true);
        await f.Engine.SetAutoStopAsync(2, 1);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.Client.Vm = f.Client.Vm with { Pid = 999, StartedAt = f.Clock.GetUtcNow() };
        // The cached PID is stale; the due countdown still stops the instance by index.
        await f.Engine.TickAsync();
        Equal(1, f.Client.Count("stop"));
        Equal(0, f.Store.Load().AutoStops.Count);
    }

    public static async Task SlowLaunchKeepsCountdown()
    {
        await using var f = await Fixture.Create();
        await f.Engine.ControlAsync(2, "start", 30);
        // Android takes longer than any startup grace period to report a PID.
        f.Client.Vm = f.Client.Vm with { Status = "starting", Pid = 0, StartedAt = null };
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        await f.Engine.RefreshAsync();
        Equal(1, f.Store.Load().AutoStops.Count);
        f.Client.Vm = f.Client.Vm with { Status = "running", Pid = 4242, StartedAt = f.Clock.GetUtcNow() };
        f.Clock.Advance(TimeSpan.FromMinutes(25));
        await f.Engine.TickAsync();
        Equal(1, f.Client.Count("stop"));
        Equal(0, f.Store.Load().AutoStops.Count);
    }

    public static async Task StoppedBeforeDueClearsCountdown()
    {
        await using var f = await Fixture.Create(running: true);
        await f.Engine.SetAutoStopAsync(2, 30);
        f.Client.Vm = f.Client.Vm with { Status = "stopped", Pid = 0, StartedAt = null };
        f.Clock.Advance(TimeSpan.FromMinutes(30));
        await f.Engine.TickAsync();
        Equal(0, f.Client.Count("stop"));
        Equal(0, f.Store.Load().AutoStops.Count);
    }

    public static async Task RepeatedStop()
    {
        await using var f = await Fixture.Create();
        await f.Engine.ControlAsync(2, "stop");
        await f.Engine.ControlAsync(2, "stop");
        Equal(0, f.Client.Count("stop"));
        Equal("stopped", EngineVmStatus(f.Engine));
    }

    public static async Task PreserveTimerOnStart()
    {
        await using var f = await Fixture.Create(running: true);
        await f.Engine.SetAutoStopAsync(2, 30);
        var before = f.Store.Load().AutoStops.Single();
        await f.Engine.ControlAsync(2, "start");
        Equal(0, f.Client.Count("start"));
        Equal(before, f.Store.Load().AutoStops.Single());
    }

    public static async Task RestoredUnconfirmedTimer()
    {
        await using var f = await Fixture.Create();
        await f.Engine.ControlAsync(2, "start", 30);
        Equal(0, f.Store.Load().AutoStops.Single().Pid);
        // App restarts before MuMu reports a PID and the instance later shows a new PID.
        // The countdown survives and still stops the instance at its due time.
        f.Clock.Advance(TimeSpan.FromSeconds(10));
        f.Client.Vm = f.Client.Vm with { Pid = 999, StartedAt = f.Clock.GetUtcNow() };
        await f.Restart();
        Equal(1, f.Store.Load().AutoStops.Count);
        f.Clock.Advance(TimeSpan.FromMinutes(30));
        await f.Engine.TickAsync();
        Equal(1, f.Client.Count("stop"));
    }

    public static async Task MissingPidBackoff()
    {
        await using var f = await Fixture.Create(running: true);
        await f.Engine.SetAutoStopAsync(2, 1);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        // A running instance without a reported PID is still stopped by index.
        f.Client.Vm = f.Client.Vm with { Pid = 0 };
        await f.Engine.TickAsync();
        Equal(1, f.Client.Count("stop"));
        Equal(0, f.Store.Load().AutoStops.Count);
        await f.Engine.TickAsync();
        Equal(1, f.Client.Count("stop"));
    }

    public static async Task StoppingDefersWithoutDuplicateStop()
    {
        await using var f = await Fixture.Create(running: true);
        await f.Engine.SetAutoStopAsync(2, 1);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.Client.Vm = f.Client.Vm with { Status = "stopping" };
        var lists = f.Client.ListCalls;
        await f.Engine.TickAsync();
        Equal(0, f.Client.Count("stop"));
        Equal(1, f.Store.Load().AutoStops.Count);
        for (int seconds = 1; seconds < 5; seconds++)
        {
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            await f.Engine.TickAsync();
        }
        Equal(lists + 1, f.Client.ListCalls);
        // Shutdown stalled and the instance is back to running: the countdown stops it.
        f.Client.Vm = f.Client.Vm with { Status = "running" };
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        await f.Engine.TickAsync();
        Equal(1, f.Client.Count("stop"));
        Equal(0, f.Store.Load().AutoStops.Count);
    }

    public static async Task UnknownUptime()
    {
        await using var f = await Fixture.Create(running: true);
        Equal(3600d, EngineUptime(f.Engine).GetDouble());
        f.Client.Vm = f.Client.Vm with { StartedAt = null };
        await f.Engine.RefreshAsync();
        Equal(JsonValueKind.Null, EngineUptime(f.Engine).ValueKind);
        f.Client.Vm = f.Client.Vm with { Status = "stopped" };
        await f.Engine.RefreshAsync();
        Equal(JsonValueKind.Null, EngineUptime(f.Engine).ValueKind);
    }

    private static JsonElement EngineUptime(AppEngine engine) =>
        JsonSerializer.SerializeToElement(engine.GetState(), AppJson.Options).GetProperty("emulators")[0].GetProperty("uptimeSeconds");

    private static string EngineVmStatus(AppEngine engine) =>
        JsonSerializer.SerializeToElement(engine.GetState(), AppJson.Options).GetProperty("emulators")[0].GetProperty("status").GetString()!;

    public static async Task LiveList()
    {
        using var client = new MuMuClient();
        var path = MuMuClient.DiscoverManager() ?? throw new InvalidOperationException("Installed MuMuManager not discovered.");
        var list = await client.ListAsync(path);
        Equal(list.Count, list.Select(vm => vm.Index).Distinct().Count());
        True(list.All(vm => vm.Name.Length > 0 && vm.Status is "running" or "stopped" or "starting" or "stopping" or "unknown"), "Invalid live instance result.");
        Console.WriteLine($"      Read {list.Count} live instances ({list.Count(vm => vm.Status == "running")} running); no control commands sent.");
    }

    public static async Task LiveThumbnails()
    {
        using var client = new MuMuClient();
        var path = MuMuClient.DiscoverManager() ?? throw new InvalidOperationException("Installed MuMuManager not discovered.");
        var running = (await client.ListAsync(path)).Where(vm => vm.Status == "running").ToArray();
        True(running.Length > 0, "At least one running instance is required for the thumbnail check.");
        foreach (var vm in running)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var frame = await client.ThumbnailAsync(path, vm, 320);
            watch.Stop();
            True(frame.Width <= 320 && frame.Height <= 320 && frame.Width > 0 && frame.Height > 0, "Thumbnail size is invalid.");
            Equal(frame.Width * frame.Height * 4, frame.Bgra.Length);
            True(frame.Bgra.Where((_, i) => i % 4 != 3).Any(b => b != 0), "Thumbnail is entirely black.");
            Console.WriteLine($"      Thumbnail #{vm.Index}: {frame.Width}x{frame.Height} in {watch.ElapsedMilliseconds} ms; no control commands sent.");
        }
    }

    public static void ThumbnailFlip()
    {
        // 2x4 bottom-up RGBA: bottom rows red, top rows blue. Downscaled by 2 to 1x2, upright BGRA.
        var rgba = new byte[2 * 4 * 4];
        for (int row = 0; row < 4; row++)
            for (int x = 0; x < 2; x++)
            {
                int o = (row * 2 + x) * 4;
                if (row < 2) rgba[o] = 255; else rgba[o + 2] = 255;
                rgba[o + 3] = 255;
            }
        var frame = RendererIpc.Downscale(rgba, 2, 4, 2);
        Equal(1, frame.Width); Equal(2, frame.Height);
        Equal(255, (int)frame.Bgra[0]); Equal(0, (int)frame.Bgra[2]);   // top pixel is blue
        Equal(0, (int)frame.Bgra[4]); Equal(255, (int)frame.Bgra[6]);   // bottom pixel is red
    }

    public static async Task LiveScreenshots()
    {
        using var client = new MuMuClient();
        var path = MuMuClient.DiscoverManager() ?? throw new InvalidOperationException("Installed MuMuManager not discovered.");
        var running = (await client.ListAsync(path)).Where(vm => vm.Status == "running").ToArray();
        True(running.Length > 0, "At least one running instance is required for the capture check.");
        // Revisit the first instance after switching endpoints. Its reported guest address must
        // also work when MuMu reports the Android NAT IP instead of the Windows host address.
        foreach (var vm in running.Append(running[0] with { AdbHost = "10.0.2.15" }))
        {
            var bytes = await client.ScreenshotAsync(path, vm);
            True(bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "Capture did not return a PNG.");
            var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
            var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
            True(width > 0 && height > 0, "Capture dimensions are invalid.");
            Console.WriteLine($"      Captured #{vm.Index} ({vm.AdbHost}, port {vm.AdbPort}): {width}x{height}, {bytes.Length} bytes; no control commands sent.");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
    private static void True(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

internal sealed class Fixture : IAsyncDisposable
{
    public string Directory { get; }
    public StateStore Store { get; }
    public TestClock Clock { get; } = new();
    public FakeMuMuClient Client { get; }
    public AppEngine Engine { get; private set; }

    private Fixture(bool running)
    {
        Directory = Path.Combine(Path.GetTempPath(), "MumuControl.Tests", Guid.NewGuid().ToString("N"));
        Store = new StateStore(Directory);
        Client = new FakeMuMuClient(Clock, running);
        Engine = null!;
    }
    public static async Task<Fixture> Create(bool running = false)
    {
        var fixture = new Fixture(running);
        await fixture.Store.SaveAsync(new StateDocument { Settings = new AppSettings { ManagerPath = "fake-manager" } });
        fixture.Engine = new AppEngine(fixture.Client, fixture.Store, fixture.Clock);
        await fixture.Engine.RefreshAsync();
        return fixture;
    }
    public async Task Restart()
    {
        await Engine.DisposeAsync();
        Engine = new AppEngine(Client, Store, Clock);
        await Engine.RefreshAsync();
    }
    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MumuControl.Tests")) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Directory);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
        if (System.IO.Directory.Exists(target)) System.IO.Directory.Delete(target, recursive: true);
    }
}

internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan interval) => _now += interval;
    public void Set(DateTimeOffset now)
    {
        if (now < _now) throw new InvalidOperationException("Test clock cannot move backwards.");
        _now = now;
    }
}

internal sealed class FakeMuMuClient(TestClock clock, bool running) : IMuMuClient
{
    public VmInfo Vm { get; set; } = new(2, "模拟器 test", running ? "running" : "stopped", running ? 1001 : 0, running ? 16448 : 0, "127.0.0.1", running ? clock.GetUtcNow().AddHours(-1) : null);
    public List<(int Index, string Action)> Calls { get; } = [];
    public int StopFailuresRemaining { get; set; }
    public Exception? ListFailure { get; set; }
    public int ListCalls { get; private set; }
    public int Count(string action) => Calls.Count(call => call.Action == action);
    public Task<IReadOnlyList<VmInfo>> ListAsync(string managerPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ListCalls++;
        if (ListFailure is {} error) throw error;
        return Task.FromResult<IReadOnlyList<VmInfo>>([Vm]);
    }
    public Task ControlAsync(string managerPath, int index, string action, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add((index, action));
        if (action == "stop" && StopFailuresRemaining > 0)
        {
            StopFailuresRemaining--;
            throw new IOException("Simulated stop transport failure");
        }
        Vm = action switch
        {
            "start" => Vm with { Status = "running", Pid = 1001, AdbPort = 16448, StartedAt = clock.GetUtcNow() },
            "stop" => Vm with { Status = "stopped", Pid = 0, AdbPort = 0, StartedAt = null },
            _ => Vm
        };
        return Task.CompletedTask;
    }
    public Task<byte[]> ScreenshotAsync(string managerPath, VmInfo vm, CancellationToken ct = default) => throw new NotSupportedException();
    public void Dispose() { }
}

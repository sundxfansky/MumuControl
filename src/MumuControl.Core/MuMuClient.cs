using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace MumuControl.Core;

/// <summary>Bounded, shell-free adapter for the official MuMuManager CLI and its bundled ADB.</summary>
public sealed class MuMuClient : IMuMuClient
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private readonly SemaphoreSlim _managerGate = new(2, 2);
    private readonly SemaphoreSlim _screenshotGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly RendererIpc _renderer = new();
    private string? _connectedAdb;
    private string? _connectedEndpoint;
    private bool _disposed;

    public static string? DiscoverManager()
    {
        var candidates = new List<string?> { Environment.GetEnvironmentVariable("MUMU_MANAGER_PATH") };
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            candidates.Add(Path.Combine(root, "Netease", "MuMu"));
            candidates.Add(Path.Combine(root, "Netease", "MuMuPlayer-12.0"));
            candidates.Add(Path.Combine(root, "MuMuPlayer-12.0"));
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    foreach (var name in new[] { @"SOFTWARE\Netease\MuMu", @"SOFTWARE\Netease\MuMuPlayer", @"SOFTWARE\Netease\MuMuPlayer-12.0" })
                    {
                        using var key = root.OpenSubKey(name);
                        foreach (var value in new[] { "InstallLocation", "InstallPath", "InstallDir", "Path" })
                            candidates.Add(key?.GetValue(value) as string);
                    }

                    using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall is null) continue;
                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        using var app = uninstall.OpenSubKey(name);
                        var displayName = app?.GetValue("DisplayName") as string;
                        if (displayName?.Contains("MuMu", StringComparison.OrdinalIgnoreCase) != true) continue;
                        candidates.Add(app?.GetValue("InstallLocation") as string);
                        var icon = app?.GetValue("DisplayIcon") as string;
                        if (!string.IsNullOrWhiteSpace(icon))
                        {
                            // DisplayIcon may be quoted and end in a resource index such as ",0".
                            var executable = icon.Trim();
                            if (executable.StartsWith('"'))
                            {
                                var endQuote = executable.IndexOf('"', 1);
                                if (endQuote > 1) executable = executable[1..endQuote];
                            }
                            else
                            {
                                var exeEnd = executable.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                                if (exeEnd >= 0) executable = executable[..(exeEnd + 4)];
                            }
                            candidates.Add(Path.GetDirectoryName(executable));
                        }
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    // Installation discovery is best effort; an inaccessible registry view is not fatal.
                }
            }
        }

        foreach (var candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var path = Environment.ExpandEnvironmentVariables(candidate!.Trim().Trim('"'));
                if (File.Exists(path) && Path.GetFileName(path).Equals("MuMuManager.exe", StringComparison.OrdinalIgnoreCase))
                    return Path.GetFullPath(path);
                foreach (var suffix in new[] { "MuMuManager.exe", @"nx_main\MuMuManager.exe", @"shell\MuMuManager.exe", @"MuMu\nx_main\MuMuManager.exe" })
                {
                    var manager = Path.Combine(path, suffix);
                    if (File.Exists(manager)) return Path.GetFullPath(manager);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public async Task<IReadOnlyList<VmInfo>> ListAsync(string managerPath, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _managerGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var result = await RunAsync(ValidateManagerPath(managerPath), ["info", "-v", "all"],
                TimeSpan.FromSeconds(12), 2 * 1024 * 1024, linked.Token).ConfigureAwait(false);
            EnsureExitSuccess(result, "读取模拟器列表");
            return ParseInfo(Utf8.GetString(result.Stdout));
        }
        finally { _managerGate.Release(); }
    }

    public async Task ControlAsync(string managerPath, int index, string action, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        string[] command = action switch
        {
            "start" => ["launch"],
            "stop" => ["shutdown"],
            "show" => ["show_window"],
            "hide" => ["hide_window"],
            "home" => ["tool", "func", "-n", "go_home"],
            "back" => ["tool", "func", "-n", "go_back"],
            _ => throw new ArgumentException("不支持的模拟器操作。", nameof(action))
        };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _managerGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var args = new[] { "control", "-v", index.ToString(CultureInfo.InvariantCulture) }.Concat(command).ToArray();
            var result = await RunAsync(ValidateManagerPath(managerPath), args,
                TimeSpan.FromSeconds(40), 1024 * 1024, linked.Token).ConfigureAwait(false);
            EnsureExitSuccess(result, "执行模拟器操作");
            ValidateControlOutput(Utf8.GetString(result.Stdout));
        }
        finally { _managerGate.Release(); }
    }

    public async Task<byte[]> ScreenshotAsync(string managerPath, VmInfo vm, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (vm.Status != "running") throw new InvalidOperationException("模拟器尚未启动完成，暂时无法读取画面。");
        if (vm.AdbPort is < 1 or > 65535) throw new InvalidOperationException("模拟器尚未提供有效的 ADB 端口。");
        // MuMu exposes each instance's adb_port on the Windows host. adb_host_ip may instead
        // contain the Android guest NAT address (10.0.2.15), which is not the host endpoint.
        // Always use the local forwarded port, keeping captures restricted to this computer.
        var endpoint = $"127.0.0.1:{vm.AdbPort}";
        var adb = DiscoverAdb(ValidateManagerPath(managerPath));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _screenshotGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (_connectedAdb != adb || _connectedEndpoint != endpoint)
            {
                var connect = await RunAsync(adb, ["connect", endpoint], TimeSpan.FromSeconds(5), 64 * 1024, linked.Token).ConfigureAwait(false);
                EnsureExitSuccess(connect, "连接模拟器画面");
                var connectOutput = Utf8.GetString(connect.Stdout);
                if (!connectOutput.Contains("connected to", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"ADB 连接失败：{LimitMessage(connectOutput + " " + connect.Stderr)}");
                _connectedAdb = adb;
                _connectedEndpoint = endpoint;
            }
            var capture = await RunAsync(adb, ["-s", endpoint, "exec-out", "screencap", "-p"],
                TimeSpan.FromSeconds(8), 16 * 1024 * 1024, linked.Token).ConfigureAwait(false);
            EnsureExitSuccess(capture, "读取模拟器画面");
            if (capture.Stdout.Length < 24 || !capture.Stdout.AsSpan(0, 8).SequenceEqual(PngSignature))
                throw new InvalidOperationException($"ADB 未返回有效的 PNG 画面：{LimitMessage(Utf8.GetString(capture.Stdout.AsSpan(0, Math.Min(256, capture.Stdout.Length))))}");
            return capture.Stdout;
        }
        catch
        {
            // A stopped/restarted emulator invalidates the ADB connection. Retry connect next time.
            _connectedAdb = null;
            _connectedEndpoint = null;
            throw;
        }
        finally { _screenshotGate.Release(); }
    }

    public Task<RawFrame> ThumbnailAsync(string managerPath, VmInfo vm, int maxSide, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (vm.Status != "running") throw new InvalidOperationException("模拟器尚未启动完成，暂时无法读取画面。");
        return _renderer.CaptureAsync(ValidateManagerPath(managerPath), vm, Math.Clamp(maxSide, 64, 4096), ct);
    }

    public async Task InstallApkAsync(string managerPath, VmInfo vm, string apkPath, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (vm.Status != "running") throw new InvalidOperationException("模拟器尚未启动完成，无法安装 APK。");
        if (vm.AdbPort is < 1 or > 65535) throw new InvalidOperationException("模拟器尚未提供有效的 ADB 端口。");
        if (!File.Exists(apkPath)) throw new FileNotFoundException("APK 文件不存在。", apkPath);
        // Same host-forwarded endpoint as screenshots; adb connect is idempotent and keeps other devices.
        var endpoint = $"127.0.0.1:{vm.AdbPort}";
        var adb = DiscoverAdb(ValidateManagerPath(managerPath));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var connect = await RunAsync(adb, ["connect", endpoint], TimeSpan.FromSeconds(8), 64 * 1024, linked.Token).ConfigureAwait(false);
        EnsureExitSuccess(connect, "连接模拟器");
        var connectOutput = Utf8.GetString(connect.Stdout);
        if (!connectOutput.Contains("connected to", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"ADB 连接失败：{LimitMessage(connectOutput + " " + connect.Stderr)}");
        // -r replaces an installed copy, -d permits version downgrades within the same signature.
        var install = await RunAsync(adb, ["-s", endpoint, "install", "-r", "-d", Path.GetFullPath(apkPath)],
            TimeSpan.FromMinutes(5), 256 * 1024, linked.Token).ConfigureAwait(false);
        var output = (Utf8.GetString(install.Stdout) + " " + install.Stderr).Trim();
        if (install.ExitCode != 0 || !output.Contains("Success", StringComparison.Ordinal))
        {
            var failure = System.Text.RegularExpressions.Regex.Match(output, @"Failure \[[^\]]+\]|INSTALL_[A-Z_]+");
            throw new InvalidOperationException($"安装失败：{LimitMessage(failure.Success ? failure.Value : output)}");
        }
    }

    /// <summary>Accepts current dictionary results, older arrays, and nested data/results envelopes.</summary>
    public static IReadOnlyList<VmInfo> ParseInfo(string json)
    {
        using var document = ParseJson(json);
        ThrowReportedErrors(document.RootElement);
        var vms = new Dictionary<int, VmInfo>();
        ReadVms(document.RootElement, null, vms, 0);
        if (vms.Count == 0 && !IsEmptyResult(document.RootElement))
            throw new InvalidDataException("MuMuManager 返回的数据中没有可识别的模拟器信息。");
        return vms.Values.OrderBy(x => x.Index).ToArray();
    }

    private static void ReadVms(JsonElement element, string? key, Dictionary<int, VmInfo> destination, int depth)
    {
        if (depth > 16) throw new InvalidDataException("MuMuManager 返回的数据嵌套过深。");
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) ReadVms(item, null, destination, depth + 1);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;
        var indexText = ReadString(element, "index") ?? key;
        if (int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 &&
            (element.TryGetProperty("name", out _) || element.TryGetProperty("is_process_started", out _)))
        {
            var pid = ReadInt(element, "headless_pid");
            if (pid <= 0) pid = ReadInt(element, "pid");
            var state = ReadString(element, "player_state")?.ToLowerInvariant() ?? "";
            var processStarted = ReadBool(element, "is_process_started");
            var androidStarted = ReadBool(element, "is_android_started");
            var status = state.Contains("stopping", StringComparison.Ordinal) || state.Contains("shutdown", StringComparison.Ordinal)
                ? "stopping"
                : processStarted == false ? "stopped"
                : androidStarted == true ? "running"
                : processStarted == true ? "starting"
                : "unknown";
            DateTimeOffset? startedAt = null;
            if (pid > 0 && status is "running" or "starting" or "stopping")
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime());
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
            destination[index] = new VmInfo(index, ReadString(element, "name") ?? $"MuMu {index}", status,
                pid, ReadInt(element, "adb_port"), ReadString(element, "adb_host_ip") ?? "127.0.0.1", startedAt);
            return;
        }
        foreach (var property in element.EnumerateObject())
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                ReadVms(property.Value, property.Name, destination, depth + 1);
    }

    private static bool IsEmptyResult(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.GetArrayLength() == 0;
        if (root.ValueKind != JsonValueKind.Object) return false;
        var properties = root.EnumerateObject().ToArray();
        if (properties.Length == 0) return true;
        var contents = properties.Where(p => p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array).ToArray();
        return contents.Length > 0 && contents.All(p => IsEmptyResult(p.Value));
    }

    private static JsonDocument ParseJson(string output)
    {
        try { return JsonDocument.Parse(output.Trim().TrimStart('\uFEFF'), new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException ex) { throw new InvalidDataException($"MuMuManager 返回了无法解析的数据：{LimitMessage(output)}", ex); }
    }

    private static void ValidateControlOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return;
        using var document = ParseJson(output);
        ThrowReportedErrors(document.RootElement);
    }

    private static void ThrowReportedErrors(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) ThrowReportedErrors(item);
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            if ((element.TryGetProperty("error_code", out var code) || element.TryGetProperty("errcode", out code)) && code.ValueKind != JsonValueKind.Null)
            {
                var value = code.ToString();
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number != 0)
                {
                    var message = ReadString(element, "error_message") ?? ReadString(element, "error_msg") ?? ReadString(element, "errmsg") ?? ReadString(element, "message") ?? ReadString(element, "msg");
                    throw new InvalidOperationException($"MuMuManager 错误 {value}：{LimitMessage(message ?? element.ToString())}");
                }
            }
            foreach (var property in element.EnumerateObject()) ThrowReportedErrors(property.Value);
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : null;

    private static int ReadInt(JsonElement element, string name) =>
        int.TryParse(ReadString(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static bool? ReadBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        return value.ToString() switch { "1" => true, "0" => false, "true" => true, "false" => false, _ => null };
    }

    private static string ValidateManagerPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("请先在设置中指定 MuMuManager.exe 路径。");
        var fullPath = Path.GetFullPath(path);
        if (!Path.GetFileName(fullPath).Equals("MuMuManager.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            throw new FileNotFoundException("找不到官方 MuMuManager.exe，请检查安装路径。", fullPath);
        return fullPath;
    }

    private static string DiscoverAdb(string managerPath)
    {
        var folder = Path.GetDirectoryName(managerPath)!;
        foreach (var relative in new[] { "adb.exe", @"..\nx_device\12.0\shell\adb.exe", @"..\nx_device\15.0\shell\adb.exe", @"..\shell\adb.exe" })
        {
            var path = Path.GetFullPath(Path.Combine(folder, relative));
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("未找到 MuMu 随附的 adb.exe。请检查模拟器安装是否完整。");
    }

    private sealed record CommandResult(int ExitCode, byte[] Stdout, string Stderr);

    private static async Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> args,
        TimeSpan timeout, int maximumOutput, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = startInfo };
        ct.ThrowIfCancellationRequested();
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动 MuMu 命令行进程。");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"无法启动 {Path.GetFileName(executable)}：{ex.Message}", ex);
        }
        using var cancellation = deadline.Token.Register(() => KillCommand(process));
        var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, maximumOutput, process, deadline.Token);
        var stderr = ReadBoundedAsync(process.StandardError.BaseStream, 64 * 1024, process, deadline.Token);
        var watch = Stopwatch.StartNew();
        // Status polls and screenshots repeat constantly; only control commands are logged every time.
        bool routine = args.Count > 0 && args[0] is "info" or "-s" or "connect" && !args.Contains("install");
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            var result = new CommandResult(process.ExitCode, await stdout.ConfigureAwait(false), Utf8.GetString(await stderr.ConfigureAwait(false)));
            var summary = $"{Path.GetFileName(executable)} {string.Join(' ', args)} → 退出码 {result.ExitCode}，{watch.ElapsedMilliseconds} ms，输出 {result.Stdout.Length} 字节";
            if (result.ExitCode != 0) DiagnosticLog.Write("warning", summary + "，stderr：" + LimitMessage(result.Stderr));
            else if (!routine || watch.ElapsedMilliseconds > 5000) DiagnosticLog.Write("info", summary);
            return result;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("warning", $"{Path.GetFileName(executable)} {string.Join(' ', args)} 失败，{watch.ElapsedMilliseconds} ms：{ex.GetType().Name} {ex.Message}");
            KillCommand(process);
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            if (deadline.IsCancellationRequested) throw new TimeoutException($"{Path.GetFileName(executable)} 执行超时（{timeout.TotalSeconds:0} 秒）。", ex);
            throw;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, Process process, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var output = new MemoryStream(Math.Min(limit, 16 * 1024));
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, 16 * 1024), ct).ConfigureAwait(false);
                if (count == 0) return output.ToArray();
                if (output.Length + count > limit)
                {
                    KillCommand(process);
                    throw new InvalidDataException($"MuMu 命令输出超过限制（{limit / 1024} KB）。");
                }
                output.Write(buffer, 0, count);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void KillCommand(Process process)
    {
        try
        {
            // Never kill descendants: a launch command may create the actual emulator, and ADB may
            // create a shared server. Only this short-lived wrapper belongs to our cancellation scope.
            if (!process.HasExited) process.Kill(entireProcessTree: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    private static void EnsureExitSuccess(CommandResult result, string operation)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{operation}失败（退出码 {result.ExitCode}）：{LimitMessage(result.Stderr + " " + Utf8.GetString(result.Stdout.AsSpan(0, Math.Min(2048, result.Stdout.Length))))}");
    }

    private static string LimitMessage(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 600 ? trimmed : trimmed[..600] + "…";
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _renderer.Dispose();
        // SemaphoreSlim instances stay undisposed to allow in-flight operations to run their finally
        // blocks safely. Their wait handles are never allocated; cancellation releases pending work.
        _lifetime.Dispose();
    }
}

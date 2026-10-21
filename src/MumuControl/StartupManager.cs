using Microsoft.Win32;
using System.Security;
using System.Text;

namespace MumuControl;

/// <summary>
/// Describes the current-user Windows startup entry.  A startup entry is considered
/// enabled only when its value exactly matches the command generated for this process.
/// </summary>
public sealed record StartupStatus(bool Enabled, bool Supported, string? Reason = null);

/// <summary>
/// Small abstraction around the Run registry key so callers can test startup behavior
/// with an in-memory key without changing the user's registry.
/// </summary>
public interface IStartupRegistry
{
    object? GetValue(string valueName);
    object? GetApprovalValue(string valueName) => null;
    void SetValue(string valueName, string value);
    void DeleteValue(string valueName);
}

/// <summary>
/// Manages the current user's startup entry without requiring administrator rights.
/// It deliberately refuses to register a framework-dependent/dotnet or debug host:
/// those paths are not stable install targets and would make Windows start the wrong
/// process after a build directory is removed.
/// </summary>
public sealed class StartupManager
{
    public const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "MumuControl";
    public const string ApprovalSubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ExpectedExecutableName = "MuMuControl.exe";

    private readonly string? _processPath;
    private readonly IStartupRegistry _registry;
    private readonly IReadOnlyList<string> _processArguments;

    public StartupManager() : this(Environment.ProcessPath, new WindowsStartupRegistry(), Environment.GetCommandLineArgs().Skip(1).ToArray()) { }

    /// <param name="processPath">The executable path to register.  Defaults to the
    /// current process path; the parameter exists for isolated tests and does not
    /// change the production behavior.</param>
    public StartupManager(string? processPath, IStartupRegistry registry, IReadOnlyList<string>? processArguments = null)
    {
        _processPath = processPath;
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _processArguments = processArguments ?? [];
    }

    /// <summary>Returns status without modifying the registry.</summary>
    public StartupStatus GetStatus()
    {
        if (!TryGetCommand(out var expected, out var unsupportedReason))
            return new StartupStatus(false, false, unsupportedReason);

        try
        {
            var actual = _registry.GetValue(ValueName);
            var approvalReason = GetApprovalReason();
            if (actual is null) return new StartupStatus(false, true, approvalReason);
            if (actual is string command && string.Equals(command, expected, StringComparison.OrdinalIgnoreCase))
                return new StartupStatus(approvalReason is null, true, approvalReason);

            // An administrator or another installer may have changed our value.  Do
            // not silently overwrite or remove it: doing so could alter another
            // program's startup behavior and would hide an unexpected modification.
            return new StartupStatus(false, true, "启动项内容与当前程序不一致，已被外部修改。请在本机检查注册表后再启用。");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return new StartupStatus(false, false, "无法读取当前用户的 Windows 启动项：" + ex.Message);
        }
    }

    /// <summary>
    /// Enables or disables startup for this user.  If an existing value does not
    /// exactly match this executable, the method refuses to overwrite it.
    /// </summary>
    public StartupStatus SetEnabled(bool enabled)
    {
        if (!TryGetCommand(out var expected, out var unsupportedReason))
        {
            if (enabled) throw new InvalidOperationException(unsupportedReason);
            return new StartupStatus(false, false, unsupportedReason);
        }

        try
        {
            var actual = _registry.GetValue(ValueName);
            if (actual is not null && (actual is not string command || !string.Equals(command, expected, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("启动项内容与当前程序不一致，已被外部修改。请在本机检查注册表后再操作。");

            if (enabled)
            {
                var approvalReason = GetApprovalReason();
                if (approvalReason is not null) throw new InvalidOperationException(approvalReason);
                _registry.SetValue(ValueName, expected);
                return new StartupStatus(true, true);
            }

            if (actual is not null) _registry.DeleteValue(ValueName);
            return new StartupStatus(false, true);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            throw new InvalidOperationException("无法修改当前用户的 Windows 启动项：" + ex.Message, ex);
        }
    }

    private bool TryGetCommand(out string command, out string reason)
    {
        command = string.Empty;
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(_processPath))
        {
            reason = "当前进程没有可注册的可执行文件路径。请使用发布版 MuMuControl.exe。";
            return false;
        }

        if (!Path.IsPathFullyQualified(_processPath))
        {
            reason = "启动项必须使用发布版 MuMuControl.exe 的完整本机路径。";
            return false;
        }

        string fullPath;
        try { fullPath = Path.GetFullPath(_processPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "当前程序路径无效，请使用发布版 MuMuControl.exe。";
            return false;
        }

        if (!Path.IsPathFullyQualified(fullPath) || fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            reason = "启动项必须指向本机上的发布版 MuMuControl.exe。";
            return false;
        }
        if (!string.Equals(Path.GetFileName(fullPath), ExpectedExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            reason = "当前进程不是 MuMuControl.exe（可能由 dotnet 或测试宿主启动）。请使用发布版 MuMuControl.exe。";
            return false;
        }
        if (!File.Exists(fullPath))
        {
            reason = "找不到当前 MuMuControl.exe，请先完成发布。";
            return false;
        }
        try
        {
            if ((File.GetAttributes(fullPath) & FileAttributes.Directory) != 0)
            {
                reason = "当前程序路径不是可执行文件。";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = "无法验证当前程序路径：" + ex.Message;
            return false;
        }

        // Framework-dependent and debug apphosts leave these sidecar files next to
        // the executable.  The supported install is the single-file published exe;
        // refusing sidecars prevents registering dotnet/bin output by mistake.
        var directory = Path.GetDirectoryName(fullPath)!;
        var stem = Path.Combine(directory, Path.GetFileNameWithoutExtension(fullPath));
        if (File.Exists(stem + ".dll") || File.Exists(stem + ".deps.json") || File.Exists(stem + ".runtimeconfig.json"))
        {
            reason = "当前目录是开发或框架依赖输出目录。请启动发布目录中的单文件 MuMuControl.exe。";
            return false;
        }

        if (!TryGetStartupArguments(out var arguments, out reason)) return false;
        command = QuoteWindowsArgument(fullPath) + " --start-minimized" + arguments;
        if (command.Length > 260)
        {
            reason = "当前程序路径过长，超出 Windows 启动项的 260 字符限制。请将发布版移到较短路径。";
            return false;
        }
        return true;
    }

    private bool TryGetStartupArguments(out string arguments, out string reason)
    {
        arguments = string.Empty;
        reason = string.Empty;
        if (_processArguments.Count == 0) return true;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _processArguments.Count; i++)
        {
            var name = _processArguments[i];
            if (name.Equals("--start-minimized", StringComparison.OrdinalIgnoreCase)) continue;
            // Headless mode is a different lifetime/UI contract and should never be
            // silently converted into a tray-startup entry.
            if (name.Equals("--headless", StringComparison.OrdinalIgnoreCase))
            {
                reason = "当前进程使用 --headless，不能注册桌面开机启动。请先用桌面发布版启动。";
                return false;
            }
            if (name is not ("--port" or "--data-dir" or "--manager") || i + 1 >= _processArguments.Count || _processArguments[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                reason = "当前启动参数无法安全复现，不能注册开机启动。请使用 --port、--data-dir 或 --manager 的完整参数。";
                return false;
            }
            var value = _processArguments[++i];
            if (!values.TryAdd(name, value))
            {
                reason = "当前启动参数重复，不能注册开机启动。";
                return false;
            }
        }

        if (values.TryGetValue("--port", out var port) && (!int.TryParse(port, out var number) || number is < 1024 or > 65535))
        {
            reason = "--port 参数无效，不能注册开机启动。";
            return false;
        }
        foreach (var pathName in new[] { "--data-dir", "--manager" })
        {
            if (!values.TryGetValue(pathName, out var path)) continue;
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                reason = pathName + " 必须使用本机完整路径，不能注册开机启动。";
                return false;
            }
            if (pathName == "--manager" && (!File.Exists(path) || !Path.GetFileName(path).Equals("MuMuManager.exe", StringComparison.OrdinalIgnoreCase)))
            {
                reason = "--manager 必须指向当前存在的本机 MuMuManager.exe，不能注册开机启动。";
                return false;
            }
        }

        // Keep a deterministic order so status does not oscillate when a user typed
        // equivalent options in a different order.  Values are quoted with the
        // Windows CommandLineToArgvW rules rather than shell escaping.
        foreach (var name in new[] { "--port", "--data-dir", "--manager" })
            if (values.TryGetValue(name, out var value)) arguments += " " + name + " " + QuoteWindowsArgument(value);
        return true;
    }

    private static string QuoteWindowsArgument(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"')
            {
                builder.Append('\\', slashes * 2 + 1).Append('"');
                slashes = 0;
                continue;
            }
            builder.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        builder.Append('\\', slashes * 2).Append('"');
        return builder.ToString();
    }

    private string? GetApprovalReason()
    {
        var approval = _registry.GetApprovalValue(ValueName);
        if (approval is null) return null;
        if (approval is byte[] { Length: >= 1 } flags)
        {
            if (flags[0] is 2 or 6) return null;
            if (flags[0] is 3 or 7)
                return "Windows 已禁用此启动项。请在任务管理器的「启动应用」中启用 MuMuControl 后重试。";
        }
        return "Windows 启动项批准状态无法识别。请在本机的「启动应用」中检查 MuMuControl。";
    }

    private sealed class WindowsStartupRegistry : IStartupRegistry
    {
        public object? GetValue(string valueName)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunSubKey, writable: false);
            return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }

        public object? GetApprovalValue(string valueName)
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovalSubKey, writable: false);
            return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }

        public void SetValue(string valueName, string value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunSubKey, writable: true)
                ?? throw new InvalidOperationException("无法打开当前用户的 Windows 启动项。");
            key.SetValue(valueName, value, RegistryValueKind.String);
        }

        public void DeleteValue(string valueName)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunSubKey, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }
}

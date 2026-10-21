using Microsoft.Win32;
using MumuControl;

// An isolated registry subkey and temporary publish-like directory exercise the
// actual Windows registry API without touching HKCU's production Run entry.
var temporary = Path.Combine(Path.GetTempPath(), "MuMu Startup Test " + Guid.NewGuid().ToString("N"));
var registryPath = @"Software\MumuControl.Tests\" + Guid.NewGuid().ToString("N");
Directory.CreateDirectory(temporary);
var executable = Path.Combine(temporary, "MuMuControl.exe");
File.WriteAllBytes(executable, [0x4D, 0x5A]);
int checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception("FAILED: " + description);
    Console.WriteLine("PASS: " + description);
    checks++;
}
try
{
    var registry = new IsolatedRegistry(registryPath);
    var manager = new StartupManager(executable, registry);
    Check(manager.GetStatus() is { Enabled: false, Supported: true, Reason: null }, "missing startup entry is reported disabled");
    Check(manager.SetEnabled(true) is { Enabled: true, Supported: true }, "enable writes isolated HKCU entry");
    Check(Equals(registry.GetValue(StartupManager.ValueName), '"' + executable + "\" --start-minimized"), "executable with spaces is correctly quoted");
    Check(manager.GetStatus() is { Enabled: true, Supported: true }, "exact startup command is reported enabled");
    Check(manager.SetEnabled(true).Enabled, "enabling twice is idempotent");
    Check(!manager.SetEnabled(false).Enabled && registry.GetValue(StartupManager.ValueName) is null, "disable deletes only matching entry");
    Check(!manager.SetEnabled(false).Enabled, "disabling twice is idempotent");
    registry.SetValue(StartupManager.ValueName, "\"C:\\OtherApp.exe\"");
    Check(manager.GetStatus() is { Enabled: false, Supported: true, Reason: not null }, "external startup edit is detected");
    foreach (bool desired in new[] { true, false })
    {
        bool rejected = false;
        try { manager.SetEnabled(desired); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected && Equals(registry.GetValue(StartupManager.ValueName), "\"C:\\OtherApp.exe\""), "external value is preserved when setting " + desired);
    }
    registry.DeleteValue(StartupManager.ValueName);
    using (var key = Registry.CurrentUser.CreateSubKey(registryPath)) key.SetValue(StartupManager.ValueName, 42, RegistryValueKind.DWord);
    Check(manager.GetStatus().Reason is not null, "non-string external value is detected");
    bool nonStringRejected = false;
    try { manager.SetEnabled(true); } catch (InvalidOperationException) { nonStringRejected = true; }
    Check(nonStringRejected && registry.GetValue(StartupManager.ValueName) is int number && number == 42, "non-string external value is never overwritten");
    registry.DeleteValue(StartupManager.ValueName);
    manager.SetEnabled(true);
    using (var key = Registry.CurrentUser.CreateSubKey(registryPath + @"\Approval")) key.SetValue(StartupManager.ValueName, new byte[] { 3, 0, 0, 0 }, RegistryValueKind.Binary);
    Check(manager.GetStatus() is { Enabled: false, Reason: not null }, "Windows startup approval disabling is detected");
    bool approvalRejected = false;
    try { manager.SetEnabled(true); } catch (InvalidOperationException) { approvalRejected = true; }
    Check(approvalRejected, "Windows startup approval is never silently overridden");
    Check(!manager.SetEnabled(false).Enabled && registry.GetValue(StartupManager.ValueName) is null, "disabled Windows entry can still be removed");
    using (var key = Registry.CurrentUser.OpenSubKey(registryPath + @"\Approval", writable: true)) key?.DeleteValue(StartupManager.ValueName);
    File.WriteAllText(Path.Combine(temporary, "MuMuControl.dll"), "development apphost sidecar");
    Check(new StartupManager(executable, registry).GetStatus() is { Supported: false, Enabled: false }, "development or framework-dependent output cannot register");
    File.Delete(Path.Combine(temporary, "MuMuControl.dll"));
    Check(new StartupManager(Path.Combine(temporary, "dotnet.exe"), registry).GetStatus().Supported == false, "dotnet/test host is rejected");
    Check(new StartupManager("MuMuControl.exe", registry).GetStatus().Supported == false, "relative executable paths are rejected");
    Check(new StartupManager(@"\\server\share\MuMuControl.exe", registry).GetStatus().Supported == false, "network executable paths are rejected");
    const string customData = @"C:\Data folder";
    var custom = new StartupManager(executable, registry, ["--data-dir", customData, "--port", "18765"]);
    Check(custom.SetEnabled(true).Enabled, "custom data-dir/port startup arguments are supported");
    Check(Equals(registry.GetValue(StartupManager.ValueName), '"' + executable + "\" --start-minimized --port \"18765\" --data-dir \"" + customData + "\""), "custom startup arguments are quoted and deterministic");
    Check(new StartupManager(executable, registry, ["--manager", @"C:\Absent\MuMuManager.exe"]).GetStatus().Supported == false, "missing manager override startup is rejected");
    Check(new StartupManager(executable, registry, ["--headless"]).GetStatus().Supported == false, "headless startup is rejected for desktop startup");
    Check(new StartupManager(executable, registry, ["--data-dir", "relative-data"]).GetStatus().Supported == false, "relative data-dir startup is rejected");
    registry.DeleteValue(StartupManager.ValueName);
    File.Delete(executable);
    Check(!new StartupManager(executable, registry).GetStatus().Supported, "missing executable is rejected");
    Console.WriteLine($"{checks}/{checks} startup checks passed; production Run key was never changed.");
}
finally
{
    Registry.CurrentUser.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false);
    Directory.Delete(temporary, recursive: true);
}

sealed class IsolatedRegistry(string subkey) : IStartupRegistry
{
    public object? GetValue(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subkey);
        return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }
    public object? GetApprovalValue(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subkey + @"\Approval");
        return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }
    public void SetValue(string valueName, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subkey);
        key.SetValue(valueName, value, RegistryValueKind.String);
    }
    public void DeleteValue(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subkey, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}

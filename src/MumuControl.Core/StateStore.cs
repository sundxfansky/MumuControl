using System.Globalization;
using System.Text.Json;

namespace MumuControl.Core;

public sealed class StateStore(string directory)
{
    private readonly string _path = Path.Combine(Path.GetFullPath(directory), "state.json");
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    public string DirectoryPath => Path.GetDirectoryName(_path)!;
    public StateDocument Load()
    {
        _saveGate.Wait();
        try
        {
            if (!File.Exists(_path)) return File.Exists(_path + ".bak") ? RecoverBackup() : new();
            try { return Read(_path); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                if (File.Exists(_path + ".bak")) return RecoverBackup();
                throw new InvalidDataException($"配置文件损坏，请备份并修复 {_path}。", ex);
            }
        }
        finally { _saveGate.Release(); }
    }
    private StateDocument RecoverBackup()
    {
        var backup = Read(_path + ".bak");
        if (File.Exists(_path)) File.Copy(_path, _path + ".corrupt", true);
        var recovery = _path + ".recover-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(_path + ".bak", recovery);
            // Repair the primary now, so the next save cannot overwrite a healthy backup with corruption.
            File.Move(recovery, _path, true);
            return backup;
        }
        finally { if (File.Exists(recovery)) File.Delete(recovery); }
    }
    private static StateDocument Read(string path)
    {
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("配置文件过大。");
        var state = JsonSerializer.Deserialize<StateDocument>(File.ReadAllText(path), AppJson.Options)
            ?? throw new InvalidDataException("配置为空。");
        if (state.Version != 1 || state.Settings is null || state.Tasks is null || state.AutoStops is null)
            throw new InvalidDataException("不支持的配置格式。");
        if (state.Tasks.Count > 500 || state.Tasks.Any(t => t is null || string.IsNullOrWhiteSpace(t.Id) || t.Id.Length > 64
            || t.VmIndex is < 0 or > 100000 || t.Action is not ("start" or "stop") || t.Trigger is not ("once" or "daily")
            || t.DurationMinutes is < 1 or > 10080 || t.Action == "stop" && t.DurationMinutes is not null
            || t.Trigger == "daily" && !TimeOnly.TryParseExact(t.At, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || t.Trigger == "once" && !DateTimeOffset.TryParse(t.At, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            || state.Tasks.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != state.Tasks.Count
            || state.AutoStops.Any(a => a is null || a.VmIndex is < 0 or > 100000 || a.Pid < 0 || a.Attempts < 0)
            || state.AutoStops.Select(a => a.VmIndex).Distinct().Count() != state.AutoStops.Count)
            throw new InvalidDataException("配置中的任务格式无效。");
        try { AppEngine.ValidateSettings(state.Settings); }
        catch (ArgumentException ex) { throw new InvalidDataException("配置中的偏好设置无效。", ex); }
        return state;
    }
    public async Task SaveAsync(StateDocument state, CancellationToken ct = default)
    {
        await _saveGate.WaitAsync(ct);
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, AppJson.Options, ct);
                if (stream.Length > 2 * 1024 * 1024) throw new InvalidDataException("配置文件过大，无法保存。");
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak", ignoreMetadataErrors: true);
            else File.Move(temporary, _path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { _saveGate.Release(); }
        }
    }
}

using System.Runtime.InteropServices;

namespace MumuControl.Core;

/// <summary>Top-down BGRA pixels, ready for an image encoder.</summary>
public sealed record RawFrame(int Width, int Height, byte[] Bgra);

/// <summary>
/// Reads frames straight from the MuMu renderer through the external_renderer_ipc.dll shipped in
/// the installation's sdk folder. This shared-memory path avoids Android-side PNG encoding and is
/// roughly 40x faster than ADB screencap. All native calls are serialized; one handle per instance.
/// </summary>
internal sealed class RendererIpc : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int ConnectFn(string installPath, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DisconnectFn(int handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CaptureFn(int handle, uint displayId, int bufferSize, ref int width, ref int height, byte[]? pixels);

    private sealed record Library(string Path, IntPtr Module, ConnectFn Connect, DisconnectFn Disconnect, CaptureFn Capture);
    private sealed record Connection(int Handle, int Pid);

    private const int MaxPixels = 4096 * 4096;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, Connection> _connections = [];
    private Library? _library;
    private byte[] _buffer = [];
    private bool _disposed;

    public async Task<RawFrame> CaptureAsync(string managerPath, VmInfo vm, int maxSide, CancellationToken ct)
    {
        // A wedged renderer must not queue unbounded callers behind the native lock.
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
            throw new TimeoutException("模拟器渲染接口繁忙，请稍后重试。");
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var frame = await Task.Run(() => CaptureCore(managerPath, vm, maxSide), ct).ConfigureAwait(false);
            DiagnosticLog.CountCapture(true);
            return frame;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticLog.CountCapture(false);
            DiagnosticLog.WriteThrottled("capture-" + vm.Index, TimeSpan.FromMinutes(5), "warning", $"#{vm.Index} 渲染器画面读取失败：{ex.GetType().Name} {ex.Message}");
            throw;
        }
        finally { _gate.Release(); }
    }

    private RawFrame CaptureCore(string managerPath, VmInfo vm, int maxSide)
    {
        var library = LoadLibrary(managerPath);
        if (_connections.TryGetValue(vm.Index, out var existing) && existing.Pid != vm.Pid)
        {
            // The instance was restarted; its shared memory belongs to a different process now.
            library.Disconnect(existing.Handle);
            _connections.Remove(vm.Index);
            existing = null;
        }
        if (existing is null)
        {
            var installRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(managerPath)!, ".."));
            var handle = library.Connect(installRoot, vm.Index);
            if (handle == 0) throw new InvalidOperationException("无法连接模拟器渲染接口，请确认该实例已完成启动。");
            DiagnosticLog.Write("info", $"#{vm.Index} 已连接渲染接口（句柄 {handle}，PID {vm.Pid}）");
            existing = new Connection(handle, vm.Pid);
            _connections[vm.Index] = existing;
        }
        int width = 0, height = 0;
        int rc = library.Capture(existing.Handle, 0, 0, ref width, ref height, null);
        if (rc == 0 && width > 0 && height > 0 && (long)width * height <= MaxPixels)
        {
            int size = width * height * 4;
            if (_buffer.Length != size) _buffer = new byte[size];
            rc = library.Capture(existing.Handle, 0, size, ref width, ref height, _buffer);
        }
        else if (rc == 0) rc = -1;
        if (rc != 0)
        {
            library.Disconnect(existing.Handle);
            _connections.Remove(vm.Index);
            throw new InvalidOperationException($"读取模拟器渲染画面失败（{rc}）。");
        }
        return Downscale(_buffer, width, height, maxSide);
    }

    /// <summary>Box-filters bottom-up RGBA into a top-down BGRA frame whose longer side is at most maxSide.</summary>
    internal static RawFrame Downscale(byte[] rgba, int width, int height, int maxSide)
    {
        int factor = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)Math.Max(1, maxSide)));
        int outWidth = Math.Max(1, width / factor), outHeight = Math.Max(1, height / factor);
        var output = new byte[outWidth * outHeight * 4];
        int samples = factor * factor;
        for (int y = 0; y < outHeight; y++)
        {
            for (int x = 0; x < outWidth; x++)
            {
                int r = 0, g = 0, b = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    // Renderer rows are stored bottom-up.
                    int row = (height - 1 - (y * factor + dy)) * width;
                    int offset = (row + x * factor) * 4;
                    for (int dx = 0; dx < factor; dx++, offset += 4)
                    {
                        r += rgba[offset]; g += rgba[offset + 1]; b += rgba[offset + 2];
                    }
                }
                int target = (y * outWidth + x) * 4;
                output[target] = (byte)(b / samples);
                output[target + 1] = (byte)(g / samples);
                output[target + 2] = (byte)(r / samples);
                output[target + 3] = 255;
            }
        }
        return new RawFrame(outWidth, outHeight, output);
    }

    private Library LoadLibrary(string managerPath)
    {
        var folder = Path.GetDirectoryName(managerPath)!;
        var path = new[] { @"sdk\external_renderer_ipc.dll", @"..\nx_device\12.0\shell\sdk\external_renderer_ipc.dll" }
            .Select(relative => Path.GetFullPath(Path.Combine(folder, relative)))
            .FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("未找到 MuMu 渲染接口 external_renderer_ipc.dll，当前版本可能不支持实时画面。");
        if (_library is not null && string.Equals(_library.Path, path, StringComparison.OrdinalIgnoreCase)) return _library;
        ReleaseAll();
        DiagnosticLog.Write("info", "加载渲染接口 " + path);
        var module = NativeLibrary.Load(path);
        try
        {
            _library = new Library(path, module,
                Marshal.GetDelegateForFunctionPointer<ConnectFn>(NativeLibrary.GetExport(module, "nemu_connect")),
                Marshal.GetDelegateForFunctionPointer<DisconnectFn>(NativeLibrary.GetExport(module, "nemu_disconnect")),
                Marshal.GetDelegateForFunctionPointer<CaptureFn>(NativeLibrary.GetExport(module, "nemu_capture_display")));
        }
        catch (EntryPointNotFoundException ex)
        {
            NativeLibrary.Free(module);
            throw new InvalidOperationException("MuMu 渲染接口版本不兼容。", ex);
        }
        return _library;
    }

    private void ReleaseAll()
    {
        if (_library is null) return;
        foreach (var connection in _connections.Values) _library.Disconnect(connection.Handle);
        _connections.Clear();
        // The module stays loaded: unloading renderer helpers that may own threads is unsafe.
        _library = null;
    }

    public void Dispose()
    {
        if (!_gate.Wait(TimeSpan.FromSeconds(3))) return;
        try
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseAll();
        }
        finally { _gate.Release(); }
    }
}

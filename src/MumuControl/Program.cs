using System.Security.Cryptography;
using System.Text;
using MumuControl.Core;

namespace MumuControl;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool headless = args.Contains("--headless");
        try
        {
            var known = new HashSet<string> { "--headless", "--start-minimized", "--port", "--manager", "--data-dir" };
            var options = new Dictionary<string, string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (!known.Contains(args[i])) throw new ArgumentException("未知参数：" + args[i]);
                if (args[i] is "--headless" or "--start-minimized") { options[args[i]] = "true"; continue; }
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--")) throw new ArgumentException("参数缺少值：" + args[i]);
                options.Add(args[i], args[++i]);
            }
            var dataDir = Path.GetFullPath(options.GetValueOrDefault("--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MumuControl"));
            Directory.CreateDirectory(dataDir);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDir.ToUpperInvariant())))[..24];
            using var singleton = new Mutex(true, @"Local\MumuControl-" + key, out bool ownsMutex);
            if (!ownsMutex) throw new InvalidOperationException("MuMu Control 已在运行，请通过托盘图标或本地浏览器访问。一个数据目录只能运行一个实例。");
            try { return Run(options, dataDir, headless); }
            finally { singleton.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            if (headless) Console.Error.WriteLine(ex.ToString());
            else MessageBox.Show(ex.Message, "MuMu Control 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
    private static int Run(Dictionary<string, string> options, string dataDir, bool headless)
    {
        var engine = new AppEngine(new MuMuClient(), new StateStore(dataDir));
        LocalServer? server = null;
        using var shutdown = new ManualResetEventSlim();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; shutdown.Set(); };
        Console.CancelKeyPress += cancel;
        try
        {
            // Port is a process override; a supplied manager path is saved as a preference.
            int port = options.TryGetValue("--port", out var portText) && int.TryParse(portText, out var number) ? number : engine.Settings.Port;
            if (options.ContainsKey("--port") && !int.TryParse(options["--port"], out _)) throw new ArgumentException("端口必须是整数。");
            if (port is < 1024 or > 65535) throw new ArgumentException("本地端口应为 1024～65535。");
            if (options.TryGetValue("--manager", out var manager))
                engine.UpdateSettingsAsync(engine.Settings with { ManagerPath = manager }).GetAwaiter().GetResult();
            server = new LocalServer(engine, port, dataDir);
            try { server.StartAsync().GetAwaiter().GetResult(); }
            catch (IOException ex) { throw new IOException($"本地端口 {port} 无法监听，可能已被占用。请使用 --port 指定其他端口。", ex); }
            engine.Start();
            if (headless)
            {
                Console.WriteLine("MuMu Control: " + server.Address);
                shutdown.Wait();
            }
            else DesktopHost.Run(server.Address, () => engine.Settings.CloseToTray, shutdown.Set, options.ContainsKey("--start-minimized"));
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            if (server is not null) server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}

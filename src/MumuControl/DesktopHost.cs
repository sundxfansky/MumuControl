using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MumuControl;

/// <summary>The native window hosts exactly the same local page as the browser.</summary>
public static class DesktopHost
{
    public static void Run(Uri serverUri, Func<bool> closeToTray, Action requestExit, bool startMinimized = false)
    {
        ArgumentNullException.ThrowIfNull(serverUri);
        ArgumentNullException.ThrowIfNull(closeToTray);
        ArgumentNullException.ThrowIfNull(requestExit);
        if (serverUri.Scheme != Uri.UriSchemeHttp || serverUri.Host != "127.0.0.1")
            throw new ArgumentException("桌面窗口只允许连接本机 127.0.0.1 服务。", nameof(serverUri));

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var window = new ControlWindow(serverUri, closeToTray, requestExit, startMinimized);
        Application.Run(window);
    }

    private sealed class ControlWindow : Form
    {
        private readonly Uri _serverUri;
        private readonly Func<bool> _closeToTray;
        private readonly Action _requestExit;
        private readonly Panel _browserPanel;
        private readonly Label _message;
        private readonly Button _browserButton;
        private readonly Button _retryButton;
        private readonly NotifyIcon _trayIcon;
        private readonly ContextMenuStrip _trayMenu;
        private readonly Icon _applicationIcon;
        private bool _startHidden;
        private WebView2? _webView;
        private int _generation;
        private bool _initializing;
        private bool _restoreAfterInitialization;
        private bool _exitRequested;
        private bool _exiting;

        public ControlWindow(Uri serverUri, Func<bool> closeToTray, Action requestExit, bool startMinimized)
        {
            _serverUri = serverUri;
            _closeToTray = closeToTray;
            _requestExit = requestExit;
            _startHidden = startMinimized;
            Text = "MuMu Control";
            using (var iconResource = typeof(DesktopHost).Assembly.GetManifestResourceStream("MumuControl.assets.MumuControl.ico")
                ?? throw new InvalidOperationException("应用图标资源缺失。"))
                _applicationIcon = new Icon(iconResource, 32, 32);
            Icon = _applicationIcon;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1280, 840);
            MinimumSize = new Size(860, 600);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(247, 248, 251);
            Font = new Font("Microsoft YaHei UI", 10F);

            var fallback = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(44),
                BackColor = BackColor
            };
            fallback.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fallback.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            fallback.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            fallback.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            fallback.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            _message = new Label
            {
                Text = "正在打开 MuMu Control…",
                AutoSize = true,
                MaximumSize = new Size(720, 0),
                Anchor = AnchorStyles.None,
                ForeColor = Color.FromArgb(70, 80, 103),
                TextAlign = ContentAlignment.MiddleCenter,
                Margin = new Padding(12)
            };
            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                Anchor = AnchorStyles.None,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = new Padding(12)
            };
            _browserButton = MakeButton("在浏览器打开", (_, _) => OpenBrowser(_serverUri));
            _retryButton = MakeButton("重试窗口", async (_, _) => await EnsureBrowserAsync());
            _retryButton.Visible = false;
            buttons.Controls.Add(_browserButton);
            buttons.Controls.Add(_retryButton);
            fallback.Controls.Add(_message, 0, 1);
            fallback.Controls.Add(buttons, 0, 2);
            _browserPanel = new Panel { Dock = DockStyle.Fill, BackColor = BackColor };
            Controls.Add(_browserPanel);
            Controls.Add(fallback);
            fallback.BringToFront();

            _trayMenu = new ContextMenuStrip();
            _trayMenu.Items.Add("显示 MuMu Control", null, (_, _) => RestoreWindow());
            _trayMenu.Items.Add("在浏览器打开", null, (_, _) => OpenBrowser(_serverUri));
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add("退出 MuMu Control", null, (_, _) => ExitApplication());
            _trayIcon = new NotifyIcon
            {
                Icon = Icon,
                Text = "MuMu Control · 本地控制台",
                ContextMenuStrip = _trayMenu,
                Visible = true
            };
            _trayIcon.DoubleClick += (_, _) => RestoreWindow();
            Shown += async (_, _) => await EnsureBrowserAsync();
            Resize += (_, _) =>
            {
                if (WindowState == FormWindowState.Minimized) ReleaseBrowser();
                else if (Visible) QueueBrowserRestore();
            };
        }

        private static Button MakeButton(string title, EventHandler click)
        {
            var button = new Button
            {
                Text = title,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(16, 8, 16, 8),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(54, 92, 245),
                Margin = new Padding(6)
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(220, 226, 238);
            button.Click += click;
            return button;
        }

        protected override void SetVisibleCore(bool value)
        {
            // Startup keeps the backend and tray alive without creating WebView2 processes.
            if (_startHidden && value)
            {
                _startHidden = false;
                base.SetVisibleCore(false);
                return;
            }
            base.SetVisibleCore(value);
        }

        private async Task EnsureBrowserAsync()
        {
            if (_exiting || _initializing || _webView is not null || !Visible || WindowState == FormWindowState.Minimized) return;
            _initializing = true;
            _message.Text = "正在打开 MuMu Control…";
            _retryButton.Visible = false;
            var generation = _generation;
            var view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
            _webView = view;
            _browserPanel.Controls.Add(view);
            try
            {
                var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MuMuControl", "WebView2");
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
                if (_exiting || generation != _generation || view.IsDisposed) return;
                await view.EnsureCoreWebView2Async(environment);
                if (_exiting || generation != _generation || view.IsDisposed) return;
                var core = view.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.IsSwipeNavigationEnabled = false;
                core.NavigationStarting += (_, e) =>
                {
                    if (IsLocalAddress(e.Uri)) return;
                    e.Cancel = true;
                    OpenOfficialLink(e.Uri);
                };
                core.NewWindowRequested += (_, e) =>
                {
                    e.Handled = true;
                    if (IsLocalAddress(e.Uri)) core.Navigate(e.Uri);
                    else OpenOfficialLink(e.Uri);
                };
                core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                core.DownloadStarting += (_, e) => e.Cancel = true;
                core.ProcessFailed += (_, _) =>
                {
                    if (IsDisposed || _exiting || generation != _generation) return;
                    BeginInvoke(new Action(() =>
                    {
                        if (generation == _generation && !_exiting)
                            ShowBrowserFailure("窗口渲染进程已停止，可重试或使用浏览器继续访问。 ");
                    }));
                };
                core.NavigationCompleted += (_, e) =>
                {
                    if (e.IsSuccess || generation != _generation || _exiting) return;
                    var status = e.WebErrorStatus;
                    BeginInvoke(new Action(() =>
                    {
                        if (generation == _generation && !_exiting)
                            ShowBrowserFailure("本地页面暂时无法加载。请稍后重试，或在浏览器中打开。" + Environment.NewLine + status);
                    }));
                };
                _browserPanel.BringToFront();
                core.Navigate(_serverUri.AbsoluteUri);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (_exiting || generation != _generation || IsDisposed) return;
                ShowBrowserFailure("桌面页面暂时无法打开。请确认已安装 Microsoft Edge WebView2 运行时。" + Environment.NewLine + error.Message);
                OpenBrowser(_serverUri);
            }
            finally
            {
                _initializing = false;
                var restore = _restoreAfterInitialization;
                _restoreAfterInitialization = false;
                if (restore && !_exiting && Visible && _webView is null)
                    _ = EnsureBrowserAsync();
            }
        }

        private bool IsLocalAddress(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && uri.Scheme == _serverUri.Scheme
            && uri.Host == _serverUri.Host
            && uri.Port == _serverUri.Port;

        private void OpenOfficialLink(string address)
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && uri.Host.Equals("mumu.163.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.StartsWith("/help/", StringComparison.Ordinal))
                OpenBrowser(uri);
        }

        private void ShowBrowserFailure(string message)
        {
            ReleaseBrowser();
            _message.Text = message;
            _retryButton.Visible = true;
            _browserPanel.SendToBack();
        }

        private void ReleaseBrowser()
        {
            _generation++;
            var view = _webView;
            _webView = null;
            if (view is null) return;
            _browserPanel.Controls.Remove(view);
            view.Dispose();
        }

        private void RestoreWindow()
        {
            if (_exiting) return;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            QueueBrowserRestore();
        }

        private void QueueBrowserRestore()
        {
            if (_initializing && _webView is null) _restoreAfterInitialization = true;
            else _ = EnsureBrowserAsync();
        }

        private void ExitApplication()
        {
            if (_exiting) return;
            _exiting = true;
            RequestServerExit();
            Close();
        }

        private void RequestServerExit()
        {
            if (_exitRequested) return;
            _exitRequested = true;
            _requestExit();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exiting && e.CloseReason == CloseReason.UserClosing && _closeToTray())
            {
                e.Cancel = true;
                Hide();
                ReleaseBrowser();
            }
            else
            {
                _exiting = true;
                RequestServerExit();
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _exiting = true;
                ReleaseBrowser();
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayMenu.Dispose();
                _applicationIcon.Dispose();
            }
            base.Dispose(disposing);
        }

        private void OpenBrowser(Uri uri)
        {
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                MessageBox.Show(this, "无法打开系统浏览器。你可以手动访问：" + Environment.NewLine + uri.AbsoluteUri + Environment.NewLine + Environment.NewLine + error.Message,
                    "MuMu Control", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}

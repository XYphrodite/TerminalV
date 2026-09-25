using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using TerminalV.Data;
using TerminalV.Diagnostics;
using TerminalV.Gateway;
using TerminalV.Host;

namespace TerminalV;

public partial class MainWindow : Window
{
    private TerminalBridge? _bridge;
    private GatewayServer? _gateway;
    private bool _gatewayEnabled;
    private int _gatewayPort;
    private string? _gatewayToken;
    private bool _allowClose;
    private bool _closing;
    private bool _restartOnClose;

    public MainWindow()
    {
        InitializeComponent();
        WindowFrame.Hook(this);
        WindowStatePersistence.Hook(this,
            load: () =>
            {
                using var db = new AppDatabase();
                return db.LoadWindowMaximized();
            },
            save: maximized =>
            {
                using var db = new AppDatabase();
                db.SaveWindowMaximized(maximized);
            },
            onError: ex => Diag.Log("persistence", "Window state persistence failed", ex.ToString()));
        WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 11, 13, 16);
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _gateway?.Dispose();
            _bridge?.Dispose();
            PasteFileStore.Cleanup();
            if (_restartOnClose) RestartApplication();
        };
        UpdateMaximizeGlyph();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var uiPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!Directory.Exists(uiPath) || !File.Exists(Path.Combine(uiPath, "index.html")))
        {
            MessageBox.Show(
                this,
                "Не найден интерфейс (wwwroot). Соберите проект через dotnet build.",
                "TerminalV",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
            return;
        }

        try
        {
            var env = await CoreWebView2Environment.CreateAsync();
            await WebView.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(
                this,
                "Нужен Microsoft Edge WebView2 Runtime.",
                "TerminalV",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
            return;
        }

        var core = WebView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsPinchZoomEnabled = false;

        core.SetVirtualHostNameToFolderMapping(
            "terminalv.local",
            uiPath,
            CoreWebView2HostResourceAccessKind.Allow);
        core.SetVirtualHostNameToFolderMapping(
            "tvdata.local",
            AppPaths.Root,
            CoreWebView2HostResourceAccessKind.Allow);

        _bridge = new TerminalBridge(Dispatcher, core, DescribeGateway);
        _bridge.SettingsChanged += SyncGateway;
        _bridge.RestartRequested += () =>
        {
            _restartOnClose = true;
            Close();
        };
        SyncGateway();
        core.WebMessageReceived += (_, args) => _bridge.Handle(args.WebMessageAsJson);
        core.NewWindowRequested += (_, e) =>
        {
            // window.open() from xterm's default link handler (if any) should not show an embedded popup.
            e.Handled = true;
            var uri = e.Uri;
            if (!string.IsNullOrWhiteSpace(uri)
                && Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
                && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
            {
                try
                {
                    using var browser = Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
                }
                catch
                {
                }
            }
        };
        core.NavigationStarting += (_, e) =>
        {
            // Virtual hosts are the only allowed in-WebView navigation; everything else goes to the system browser.
            if (e.Uri.StartsWith("https://terminalv.local", StringComparison.OrdinalIgnoreCase)
                || e.Uri.StartsWith("https://tvdata.local", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (e.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                try
                {
                    using var browser = Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
                }
                catch
                {
                }
            }
        };
        core.NavigationCompleted += (_, args) =>
        {
            if (args.IsSuccess)
            {
                _bridge.SendInit();
            }
        };

        core.Navigate("https://terminalv.local/index.html");
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _bridge is null)
        {
            return;
        }

        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        try
        {
            await _bridge.FlushAsync();
            await _bridge.StopExtensionsAsync();

            await Task.Yield(); // Unwind Closing even when the interface has not loaded.
            _allowClose = true;
            Close();
        }
        catch (Exception ex)
        {
            _allowClose = false;
            Diag.Log("persistence", "Window close cancelled: session save failed", ex.ToString());
            MessageBox.Show(this,
                "Не удалось сохранить сессии. Окно оставлено открытым. Повторите закрытие, чтобы снова попробовать сохранить.\n\n" + ex.Message,
                "TerminalV", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _closing = false; }
    }

    private static void RestartApplication()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            // Wait for the old process to release its desktop lease. A fixed
            // delay can launch the replacement while the old window still owns it.
            var script = $"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue; " +
                $"Start-Process -FilePath '{path.Replace("'", "''")}'";
            var command = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + command,
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            Diag.Log("update", "Restart failed after saving sessions", ex.ToString());
            MessageBox.Show("Сессии сохранены, но перезапуск не удался. Откройте TerminalV вручную.\n\n" + ex.Message,
                "TerminalV", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private object DescribeGateway()
    {
        var gateway = _gateway;
        if (gateway is { IsRunning: true })
        {
            return gateway.Describe();
        }

        return new
        {
            enabled = _gatewayEnabled,
            port = _gatewayPort,
            listening = false,
            status = gateway?.Status ?? (_gatewayEnabled ? "Не запущен." : "Отключён в настройках.")
        };
    }

    private void SyncGateway()
    {
        AppSettings settings;
        try
        {
            using var db = new AppDatabase();
            settings = db.LoadSettings();
        }
        catch
        {
            return;
        }

        if (settings.GatewayEnabled && string.IsNullOrEmpty(settings.GatewayToken))
        {
            settings.GatewayToken = GenerateToken();
            try
            {
                using var db = new AppDatabase();
                db.SaveSettings(settings);
            }
            catch
            {
            }
        }

        if (_gateway is not null &&
            _gatewayEnabled == settings.GatewayEnabled &&
            _gatewayPort == settings.GatewayPort &&
            _gatewayToken == settings.GatewayToken)
        {
            return;
        }

        _gatewayEnabled = settings.GatewayEnabled;
        _gatewayPort = settings.GatewayPort;
        _gatewayToken = settings.GatewayToken;

        _gateway?.Dispose();
        _gateway = null;

        if (!settings.GatewayEnabled)
        {
            return;
        }

        var server = new GatewayServer(new SessionClientBackend(), settings.GatewayPort, settings.GatewayToken);
        try
        {
            server.Start();
        }
        catch
        {
            // Status (e.g. missing URL ACL) stays visible in settings; retry on next save/restart.
        }

        _gateway = server;
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateMaximizeGlyph();

    private void UpdateMaximizeGlyph()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        MaximizeButton.ToolTip = maximized ? "Свернуть в окно" : "Развернуть";
        // Keep the airspace inset in sync: no resize border needed when maximized.
        ContentRoot.Margin = maximized ? new Thickness(0) : new Thickness(6, 0, 6, 6);
    }
}

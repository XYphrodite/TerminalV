using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using TerminalV.Data;
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

    public MainWindow()
    {
        InitializeComponent();
        WindowFrame.Hook(this);
        WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 11, 13, 16);
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) => { _gateway?.Dispose(); _bridge?.Dispose(); PasteFileStore.Cleanup(); };
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
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        try
        {
            if (WebView.CoreWebView2 is not null)
            {
                await WebView.ExecuteScriptAsync("window.terminalvFlush && window.terminalvFlush()");
                await Task.Delay(80);
            }
        }
        catch
        {
        }

        _allowClose = true;
        Close();
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

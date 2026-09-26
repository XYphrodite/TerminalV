using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Microsoft.Extensions.Localization;
using Microsoft.Web.WebView2.Core;
using TerminalV.Data;
using TerminalV.Diagnostics;
using TerminalV.Gateway;
using TerminalV.Host;
using TerminalV.Localization;

namespace TerminalV;

public partial class MainWindow : Window
{
    private readonly IStringLocalizer _localizer;
    private readonly IStringLocalizerFactory _localizerFactory;
    private TerminalBridge? _bridge;
    private GatewayServer? _gateway;
    private TailnetAccess? _tailnetAccess;
    private bool _gatewayEnabled;
    private int _gatewayPort;
    private string? _gatewayToken;
    private bool _allowClose;
    private bool _closing;
    private bool _restartOnClose;

    public MainWindow() : this(LocalizationService.Factory) { }

    public MainWindow(IStringLocalizerFactory? localizerFactory)
    {
        _localizerFactory = localizerFactory ?? LocalizationService.Factory;
        _localizer = _localizerFactory.Create(typeof(MainWindow));
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
                _localizer["Error_WwwrootNotFound"],
                _localizer["Window_Title"],
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
                _localizer["Error_WebView2NotFound"],
                _localizer["Window_Title"],
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

        _bridge = new TerminalBridge(Dispatcher, core, DescribeGateway, _localizerFactory);
        _bridge.SettingsChanged += SyncGateway;
        _bridge.RestartRequested += () =>
        {
            _restartOnClose = true;
            Close();
        };
        SyncGateway();
        core.WebMessageReceived += (_, args) =>
        {
            using var message = System.Text.Json.JsonDocument.Parse(args.WebMessageAsJson);
            if (message.RootElement.TryGetProperty("type", out var type) && type.GetString() == "tailnet-revoke")
            {
                if (MessageBox.Show(this, _localizer["Confirm_TailnetRevoke"], _localizer["Window_Title"], MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                    _tailnetAccess?.RevokeAll();
                return;
            }
            _bridge.Handle(args.WebMessageAsJson);
        };
        core.NewWindowRequested += (_, e) =>
        {
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

            await Task.Yield();
            _allowClose = true;
            Close();
        }
        catch (Exception ex)
        {
            _allowClose = false;
            Diag.Log("persistence", "Window close cancelled: session save failed", ex.ToString());
            MessageBox.Show(this,
                _localizer["Error_SaveSessionsFailed"] + "\n\n" + ex.Message,
                _localizer["Window_Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _closing = false; }
    }

    private void RestartApplication()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
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
            MessageBox.Show(_localizer["Error_RestartFailed"] + "\n\n" + ex.Message,
                _localizer["Window_Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
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
            status = gateway?.Status ?? (_gatewayEnabled ? _localizer["Gateway_Status_NotRunning"] : _localizer["Gateway_Status_Disabled"])
        };
    }

    private void SyncGateway()
    {
        if (_bridge is null) return;
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

        _tailnetAccess ??= new TailnetAccess(Path.Combine(AppPaths.Root, "tailnet-devices.json"),
            (identity, ct) => TailnetPairingPrompt.ShowAsync(this, identity, ct));
        var server = new GatewayServer(_bridge.GatewayBackend, settings.GatewayPort, settings.GatewayToken,
            _tailnetAccess, _bridge.GatewaySessions);
        try
        {
            server.Start();
        }
        catch
        {
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
        MaximizeButton.ToolTip = maximized ? _localizer["Caption_Restore"] : _localizer["Caption_Maximize"];
        ContentRoot.Margin = maximized ? new Thickness(0) : new Thickness(6, 0, 6, 6);
    }
}

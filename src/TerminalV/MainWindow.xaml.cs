using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using TerminalV.Host;

namespace TerminalV;

public partial class MainWindow : Window
{
    private TerminalBridge? _bridge;

    public MainWindow()
    {
        InitializeComponent();
        WindowFrame.Hook(this);
        WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 11, 13, 16);
        Loaded += OnLoaded;
        Closed += (_, _) => _bridge?.Dispose();
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

        _bridge = new TerminalBridge(Dispatcher, core);
        core.WebMessageReceived += (_, args) => _bridge.Handle(args.WebMessageAsJson);
        core.NavigationCompleted += (_, args) =>
        {
            if (args.IsSuccess)
            {
                _bridge.SendInit();
            }
        };

        core.Navigate("https://terminalv.local/index.html");
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
    }
}

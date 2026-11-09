using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LinqExpr = System.Linq.Expressions.Expression;

namespace AiLimits;

internal static class MimoLoginWindow
{
    private static Window? _active;

    public static void Open(string cookieFilePath, Action<string>? log = null)
    {
        var app = Application.Current
            ?? throw new InvalidOperationException("Окно входа доступно только в настольном приложении TerminalV.");

        app.Dispatcher.BeginInvoke(() =>
        {
            if (_active is { IsLoaded: true })
            {
                if (_active.WindowState == WindowState.Minimized)
                    _active.WindowState = WindowState.Normal;
                _active.Activate();
                return;
            }

            var window = CreateWindow(cookieFilePath, log);
            _active = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_active, window)) _active = null;
            };
            window.Show();
            window.Activate();
        });
    }

    public static void CloseActive()
    {
        var app = Application.Current;
        if (app is null) return;
        try
        {
            app.Dispatcher.InvokeAsync(() =>
            {
                try { _active?.Close(); } catch { }
                _active = null;
            });
        }
        catch { }
    }

    private static Assembly ResolveHostAssembly(string simpleName)
        => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
           ?? AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(simpleName));

    private static Delegate CreateEventHandler(Type handlerType, Action<object?, object?> callback)
    {
        var invoke = handlerType.GetMethod("Invoke")!;
        var ps = invoke.GetParameters();
        var pSender = LinqExpr.Parameter(ps[0].ParameterType, "s");
        var pArgs = LinqExpr.Parameter(ps[1].ParameterType, "e");
        var body = LinqExpr.Invoke(
            LinqExpr.Constant(callback),
            LinqExpr.Convert(pSender, typeof(object)),
            LinqExpr.Convert(pArgs, typeof(object)));
        return LinqExpr.Lambda(handlerType, body, pSender, pArgs).Compile();
    }

    private static Window CreateWindow(string cookieFilePath, Action<string>? log)
    {
        var wpfAsm = ResolveHostAssembly("Microsoft.Web.WebView2.Wpf");
        var coreAsm = ResolveHostAssembly("Microsoft.Web.WebView2.Core");
        var webViewType = wpfAsm.GetType("Microsoft.Web.WebView2.Wpf.WebView2", throwOnError: true)!;
        var envType = coreAsm.GetType("Microsoft.Web.WebView2.Core.CoreWebView2Environment", throwOnError: true)!;

        var owner = Application.Current?.MainWindow;
        var hasOwner = owner is { IsLoaded: true, IsVisible: true };

        var window = new Window
        {
            Title = "MiMo — вход в аккаунт (Token Plan)",
            Width = 520,
            Height = 680,
            MinWidth = 400,
            MinHeight = 500,
            Background = new SolidColorBrush(Color.FromRgb(11, 13, 16)),
            Foreground = new SolidColorBrush(Color.FromRgb(232, 238, 245)),
            WindowStartupLocation = hasOwner ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = hasOwner ? owner : null
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var bannerText = new TextBlock
        {
            Text = "Войдите в аккаунт на platform.xiaomimimo.com — окно закроется автоматически после получения квот.",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 208, 218)),
            TextWrapping = TextWrapping.Wrap
        };
        var banner = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(16, 20, 26)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(28, 35, 44)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 9, 12, 9),
            Child = bannerText
        };
        Grid.SetRow(banner, 0);

        var webView = (UIElement)Activator.CreateInstance(webViewType)!;
        webViewType.GetProperty("DefaultBackgroundColor")?.SetValue(webView, System.Drawing.Color.FromArgb(255, 11, 13, 16));
        Grid.SetRow(webView, 1);

        grid.Children.Add(banner);
        grid.Children.Add(webView);
        window.Content = grid;

        var coreProp = webViewType.GetProperty("CoreWebView2")!;
        var closed = false;
        var probing = false;
        string? lastProbedCookie = null;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };

        async Task CheckCookiesAsync()
        {
            if (closed || probing) return;
            var core = coreProp.GetValue(webView);
            if (core is null) return;

            try
            {
                var cookieMgr = core.GetType().GetProperty("CookieManager")?.GetValue(core);
                if (cookieMgr is null) return;
                var getCookies = cookieMgr.GetType().GetMethod("GetCookiesAsync", [typeof(string)])!;
                var cookiesTask = (Task)getCookies.Invoke(cookieMgr, ["https://platform.xiaomimimo.com/api/v1/tokenPlan/usage"])!;
                await cookiesTask.ConfigureAwait(true);
                if (closed) return;

                var list = cookiesTask.GetType().GetProperty("Result")?.GetValue(cookiesTask) as IEnumerable;
                if (list is null) return;

                var pairs = new List<string>();
                foreach (var item in list)
                {
                    if (item is null) continue;
                    var t = item.GetType();
                    var name = t.GetProperty("Name")?.GetValue(item) as string;
                    var val = t.GetProperty("Value")?.GetValue(item) as string;
                    if (!string.IsNullOrWhiteSpace(name))
                        pairs.Add($"{name}={val}");
                }
                if (pairs.Count == 0) return;

                var header = string.Join("; ", pairs);
                if (string.IsNullOrWhiteSpace(header) || string.Equals(header, lastProbedCookie, StringComparison.Ordinal))
                    return;

                probing = true;
                bannerText.Text = "Проверка сессии и загрузка квот Token Plan…";

                var probeResult = await Task.Run(() => Providers.TryAcceptMimoCookieAsync(header, cookieFilePath)).ConfigureAwait(true);
                lastProbedCookie = header;
                if (closed) return;

                if (probeResult == Providers.MimoProbeResult.Accepted)
                {
                    bannerText.Text = "Квоты получены!";
                    closed = true;
                    timer.Stop();
                    window.Close();
                    return;
                }

                bannerText.Text = probeResult == Providers.MimoProbeResult.NoActivePlan
                    ? "Аккаунт авторизован, но активный Token Plan не найден. Смените аккаунт в консоли или закройте окно."
                    : "Войдите в аккаунт на platform.xiaomimimo.com — окно закроется автоматически после получения квот.";
            }
            catch (Exception ex)
            {
                log?.Invoke($"MiMo cookie check error: {ex.Message}");
            }
            finally
            {
                probing = false;
            }
        }

        timer.Tick += async (_, _) => await CheckCookiesAsync();

        window.Loaded += async (_, _) =>
        {
            try
            {
                var profileDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share", "mimocode", "webview2-auth");
                Directory.CreateDirectory(profileDir);

                var createEnv = envType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .First(m => m.Name == "CreateAsync" && m.GetParameters().Length == 3);
                var envTask = (Task)createEnv.Invoke(null, [null, profileDir, null])!;
                await envTask.ConfigureAwait(true);
                if (closed) return;
                var env = envTask.GetType().GetProperty("Result")!.GetValue(envTask)!;

                var ensureMethod = webViewType.GetMethod("EnsureCoreWebView2Async", [envType])!;
                var ensureTask = (Task)ensureMethod.Invoke(webView, [env])!;
                await ensureTask.ConfigureAwait(true);
                if (closed) return;

                var core = coreProp.GetValue(webView);
                if (core is null) return;
                var coreType = core.GetType();

                var settings = coreType.GetProperty("Settings")?.GetValue(core);
                settings?.GetType().GetProperty("IsStatusBarEnabled")?.SetValue(settings, false);

                var navigate = coreType.GetMethod("Navigate", [typeof(string)])!;

                var newWinEvent = coreType.GetEvent("NewWindowRequested");
                if (newWinEvent?.EventHandlerType is { } nwType)
                {
                    var d = CreateEventHandler(nwType, (_, e) =>
                    {
                        if (e is null) return;
                        var et = e.GetType();
                        var uri = et.GetProperty("Uri")?.GetValue(e) as string;
                        if (!string.IsNullOrWhiteSpace(uri))
                        {
                            et.GetProperty("Handled")?.SetValue(e, true);
                            navigate.Invoke(core, [uri]);
                        }
                    });
                    newWinEvent.AddEventHandler(core, d);
                }

                var navDoneEvent = coreType.GetEvent("NavigationCompleted");
                if (navDoneEvent?.EventHandlerType is { } ndType)
                {
                    var d = CreateEventHandler(ndType, (_, _) => _ = CheckCookiesAsync());
                    navDoneEvent.AddEventHandler(core, d);
                }

                navigate.Invoke(core, ["https://platform.xiaomimimo.com/console"]);
                timer.Start();
            }
            catch (Exception ex)
            {
                log?.Invoke($"MiMo login WebView2 init failed: {ex}");
                bannerText.Text = $"Не удалось инициализировать WebView2: {ex.Message}";
            }
        };

        window.Closed += (_, _) =>
        {
            closed = true;
            timer.Stop();
            try { (webView as IDisposable)?.Dispose(); } catch { }
        };

        return window;
    }
}

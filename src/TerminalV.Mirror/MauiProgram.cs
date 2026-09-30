using Microsoft.Extensions.Logging;
using Microsoft.Maui.Hosting;

namespace TerminalV.Mirror;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddSingleton<TerminalV.Ssh.SshService>();
        builder.Services.AddSingleton<TerminalV.Mirror.Host.MobileDataStore>();
        builder.Services.AddSingleton<TerminalV.Mirror.Tailscale.TailnetAccount>();
        builder.Services.AddSingleton(sp => new TerminalV.Mirror.Host.MobileBridge(
            sp.GetRequiredService<TerminalV.Ssh.SshService>(),
            sp.GetRequiredService<TerminalV.Mirror.Host.MobileDataStore>(),
            sp.GetRequiredService<TerminalV.Mirror.Tailscale.TailnetAccount>()));
        builder.Services.AddSingleton<TerminalV.Mirror.Tailscale.TailscaleService>();
        builder.Services.AddSingleton<TerminalV.Mirror.Tailscale.ITailscaleService>(sp => sp.GetRequiredService<TerminalV.Mirror.Tailscale.TailscaleService>());

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        // Isolation: run BlazorWebView in isolated process where supported
        // (Windows WebView2 isolation is host-controlled; mobile uses OS sandbox).

        return builder.Build();
    }
}

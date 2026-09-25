using Microsoft.Extensions.Logging;
using Microsoft.Maui.Hosting;

namespace TerminalV.Mobile;

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
        builder.Services.AddSingleton<TerminalV.Mobile.Host.MobileDataStore>();
        builder.Services.AddSingleton<TerminalV.Mobile.Tailscale.TailnetAccount>();
        builder.Services.AddSingleton(sp => new TerminalV.Mobile.Host.MobileBridge(
            sp.GetRequiredService<TerminalV.Ssh.SshService>(),
            sp.GetRequiredService<TerminalV.Mobile.Host.MobileDataStore>(),
            sp.GetRequiredService<TerminalV.Mobile.Tailscale.TailnetAccount>()));
        builder.Services.AddSingleton<TerminalV.Mobile.Tailscale.TailscaleService>();
        builder.Services.AddSingleton<TerminalV.Mobile.Tailscale.ITailscaleService>(sp => sp.GetRequiredService<TerminalV.Mobile.Tailscale.TailscaleService>());

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        // Isolation: run BlazorWebView in isolated process where supported
        // (Windows WebView2 isolation is host-controlled; mobile uses OS sandbox).

        return builder.Build();
    }
}

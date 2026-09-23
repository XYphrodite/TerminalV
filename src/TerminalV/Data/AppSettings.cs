namespace TerminalV.Data;

internal sealed class AppSettings
{
    public string ThemeId { get; set; } = "midnight";
    public string FontFamily { get; set; } = "Cascadia Code, Cascadia Mono, Consolas, Courier New, monospace";
    public int FontSize { get; set; } = 14;
    public int Zoom { get; set; }
    public bool SidebarCollapsed { get; set; }
    public string SessionDensity { get; set; } = "standard";
    public bool HardwareRendering { get; set; } = true;
    public string? BackgroundPath { get; set; }
    public double BackgroundOpacity { get; set; } = 0.25;
}

namespace TerminalV.Data;

internal sealed class AppSettings
{
    public string ThemeId { get; set; } = "midnight";
    public string FontFamily { get; set; } = "Cascadia Code, Cascadia Mono, Roboto Mono, Droid Sans Mono, Noto Sans Mono, Consolas, Courier New, monospace";
    [System.Text.Json.Serialization.JsonConverter(typeof(NullToDefaultConverter<int>))]
    public int FontSize { get; set; } = 14;
    [System.Text.Json.Serialization.JsonConverter(typeof(NullToDefaultConverter<int>))]
    public int Zoom { get; set; }
    public bool SidebarCollapsed { get; set; }
    public string SessionDensity { get; set; } = "standard";
    public bool HardwareRendering { get; set; } = true;
    public string? BackgroundPath { get; set; }
    [System.Text.Json.Serialization.JsonConverter(typeof(NullToDefaultConverter<double>))]
    public double BackgroundOpacity { get; set; } = 0.25;
    public bool GatewayEnabled { get; set; } = true;
    [System.Text.Json.Serialization.JsonConverter(typeof(NullToDefaultConverter<int>))]
    public int GatewayPort { get; set; } = 5454;
    public string GatewayToken { get; set; } = "";
    public bool MobileFitMode { get; set; } = true;
    public string Language { get; set; } = "ru";
}

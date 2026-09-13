namespace TerminalV.Data;

internal sealed class AppSettings
{
    public string ThemeId { get; set; } = "midnight";
    public string FontFamily { get; set; } = "Cascadia Code, Cascadia Mono, Consolas, Courier New, monospace";
    public int FontSize { get; set; } = 14;
    public int Zoom { get; set; }
    public bool SidebarCollapsed { get; set; }
    public string? BackgroundPath { get; set; }
    public double BackgroundOpacity { get; set; } = 0.25;
}

internal sealed class SessionRecord
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? CustomTitle { get; set; }
    public int SortOrder { get; set; }
    public bool Active { get; set; }
    public string? Buffer { get; set; }
    public string? Cwd { get; set; }
    public string? Group { get; set; }
    public string? Color { get; set; }
    public bool Pinned { get; set; }
    public bool Hidden { get; set; }
    public bool Muted { get; set; }
}

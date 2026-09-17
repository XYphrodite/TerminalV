namespace TerminalV.Data;

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
    // Hidden controls visibility only; persistence (Buffer/Cwd/etc.) applies equally
    // to visible and hidden sessions – empty chat after restart is not expected.
    public bool Muted { get; set; }
    // Snapshot, not a profile reference: editing/deleting a profile cannot change this session.
    public string? Shell { get; set; }
    public string? StartupCommand { get; set; }
    public string? WslDistribution { get; set; }
}

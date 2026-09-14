using TerminalV.Data;

namespace TerminalV.Host;

internal sealed class IncomingMessage
{
    public string? Type { get; set; }
    public string? Id { get; set; }
    public string? Data { get; set; }
    public string? RequestId { get; set; }
    public int Cols { get; set; }
    public int Rows { get; set; }
    public List<SessionRecord>? Sessions { get; set; }
    public List<PaneLayout>? Layouts { get; set; }
    public string? Cwd { get; set; }
    public string? Shell { get; set; }
    public string? StartupCommand { get; set; }
    public string? WslDistribution { get; set; }
    public List<LaunchProfile>? Profiles { get; set; }
}

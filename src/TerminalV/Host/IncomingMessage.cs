using TerminalV.Data;
using System.Text.Json;

namespace TerminalV.Host;

internal sealed class IncomingMessage
{
    public string? Type { get; set; }
    public string? Id { get; set; }
    public string? Data { get; set; }
    public string? RequestId { get; set; }
    public string? ExtensionId { get; set; }
    public string? Method { get; set; }
    public JsonElement? Args { get; set; }
    public bool Enabled { get; set; }
    public bool StartMenu { get; set; }
    public bool Desktop { get; set; }
    public int Cols { get; set; }
    public int Rows { get; set; }
    public List<SessionRecord>? Sessions { get; set; }
    public List<PaneLayout>? Layouts { get; set; }
    public string? Cwd { get; set; }
    public string? Shell { get; set; }
    public string? StartupCommand { get; set; }
    public string? WslDistribution { get; set; }
    public List<LaunchProfile>? Profiles { get; set; }
    public string? Uri { get; set; }
}

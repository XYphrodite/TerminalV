namespace TerminalV.Host;

internal sealed class IncomingMessage
{
    public string? Type { get; set; }
    public string? Id { get; set; }
    public string? Data { get; set; }
    public string? RequestId { get; set; }
    public int Cols { get; set; }
    public int Rows { get; set; }
}

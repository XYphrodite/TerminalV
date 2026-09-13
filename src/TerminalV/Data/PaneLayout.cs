namespace TerminalV.Data;

internal sealed class PaneLayout
{
    public string? SessionId { get; set; }
    public string? Axis { get; set; }
    public double Ratio { get; set; } = .5;
    public PaneLayout? First { get; set; }
    public PaneLayout? Second { get; set; }

    public static List<PaneLayout> Normalize(IReadOnlyList<PaneLayout>? layouts, IReadOnlyList<SessionRecord> sessions)
    {
        var ids = sessions.Where(s => !s.Hidden).Select(s => s.Id).Distinct().ToList();
        var allowed = ids.ToHashSet();
        var used = new HashSet<string>();
        PaneLayout? Clean(PaneLayout? node, ref int count, int depth = 0)
        {
            if (node is null || depth > 16 || count >= 8) return null;
            if (node.SessionId is { } id)
            {
                if (!allowed.Contains(id) || !used.Add(id)) return null;
                count++;
                return new() { SessionId = id };
            }
            if (node.Axis is not ("columns" or "rows")) return null;
            var first = Clean(node.First, ref count, depth + 1);
            var second = Clean(node.Second, ref count, depth + 1);
            return first is not null && second is not null
                ? new() { Axis = node.Axis, Ratio = double.IsFinite(node.Ratio) ? Math.Clamp(node.Ratio, .1, .9) : .5,
                    First = first, Second = second } : first ?? second;
        }
        var result = new List<PaneLayout>();
        foreach (var root in layouts ?? [])
        {
            var count = 0;
            if (Clean(root, ref count) is { } clean) result.Add(clean);
        }
        foreach (var id in ids) if (used.Add(id)) result.Add(new() { SessionId = id });
        return result;
    }
}

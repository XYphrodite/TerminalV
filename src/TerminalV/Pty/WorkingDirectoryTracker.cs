using System.Text;

namespace TerminalV.Pty;

// Only observes OSC 9;9 metadata. It never rewrites terminal output or executes it.
// Bounded state also handles markers split between arbitrary ConPTY reads.
internal sealed class WorkingDirectoryTracker(string initialDirectory)
{
    private const int MaxLength = 32768;
    private readonly StringBuilder _osc = new();
    private int _state; // 0: text, 1: ESC, 2: OSC, 3: OSC ESC
    public string CurrentDirectory { get; private set; } = initialDirectory;
    public event Action<string>? Changed;

    public void Feed(string text)
    {
        foreach (var ch in text)
        {
            if (_state == 2 && (ch == '\a' || ch == '\u009c') || _state == 3 && ch == '\\')
            {
                Complete();
                continue;
            }

            if (ch == '\u001b')
            {
                _state = _state == 2 ? 3 : 1;
                continue;
            }

            if ((_state == 1 || _state == 3) && ch == ']' || ch == '\u009d')
            {
                _osc.Clear();
                _state = 2;
            }
            else if (_state == 2)
            {
                if (char.IsControl(ch) || _osc.Length >= MaxLength)
                {
                    _state = 0;
                    _osc.Clear();
                }
                else
                {
                    _osc.Append(ch);
                }
            }
            else
            {
                _state = 0;
            }
        }
    }

    private void Complete()
    {
        var value = _osc.ToString();
        _osc.Clear();
        _state = 0;
        if (!value.StartsWith("9;9;", StringComparison.Ordinal)) return;
        var path = value[4..];
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"') path = path[1..^1];
        if (!WorkingDirectory.IsValidPath(path) || path == CurrentDirectory) return;
        CurrentDirectory = path;
        Changed?.Invoke(path);
    }
}

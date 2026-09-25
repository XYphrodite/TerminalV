using System.Text;

namespace TerminalV.Host;

// A bounded wire replay, not a screen emulator. Keep terminal modes at the
// discarded/retained boundary: a long-running TUI enables mouse/alternate-screen
// modes only once, long before its latest repaint. Replaying just the raw tail
// would otherwise display that repaint in a normal, non-interactive buffer.
internal sealed class TerminalReplayBuffer
{
    private readonly int _capacity;
    private readonly LinkedList<string> _chunks = new();
    private readonly BoundaryModes _boundary = new();
    private int _length;

    public TerminalReplayBuffer(int capacity = 1_500_000)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public void Add(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        _chunks.AddLast(chunk);
        _length += chunk.Length;
        while (_length > _capacity)
        {
            var first = _chunks.First!;
            var count = Math.Min(_length - _capacity, first.Value.Length);
            // Do not retain half of a UTF-16 character when trimming one chunk.
            if (count < first.Value.Length && char.IsHighSurrogate(first.Value[count - 1])
                && char.IsLowSurrogate(first.Value[count])) count++;
            _boundary.Feed(first.Value.AsSpan(0, count));
            _length -= count;
            if (count == first.Value.Length) _chunks.RemoveFirst();
            else first.Value = first.Value[count..];
        }
    }

    public string Snapshot() => _boundary.Prefix() + string.Concat(_chunks);

    private sealed class BoundaryModes
    {
        private enum State { Ground, Escape, Csi, CsiIgnore, Osc, DcsHeader, DcsBody, IgnoreString }
        private State _state;
        private readonly StringBuilder _pending = new();
        private readonly SortedDictionary<int, bool> _private = new();
        private readonly SortedSet<int> _normal = new();
        private bool _alternate;
        private int _mouse;
        private int _encoding;
        private const int MaxSequence = 1024;

        public void Feed(ReadOnlySpan<char> text)
        {
            foreach (var ch in text)
            {
                // ECMA-48 cancellation and introducers apply in every state.
                if (ch is '\x18' or '\x1a' or '\x9c') { Ground(); continue; }
                if (ch == '\x1b') { Start(State.Escape, "\x1b"); continue; }
                if (ch == '\x9b') { Start(State.Csi, "\x1b["); continue; }
                if (ch == '\x9d') { Start(State.Osc); continue; }
                if (ch == '\x90') { Start(State.DcsHeader, "\x1bP"); continue; }
                if (ch is '\x98' or '\x9e' or '\x9f') { Start(State.IgnoreString); continue; }
                if (ch is >= '\x80' and <= '\x9f') { Ground(); continue; }

                switch (_state)
                {
                    case State.Ground:
                        break;
                    case State.Escape:
                        Escape(ch);
                        break;
                    case State.Csi:
                        Csi(ch);
                        break;
                    case State.CsiIgnore:
                        if (ch is >= '@' and <= '~') Ground();
                        break;
                    case State.Osc:
                        if (ch == '\a') Ground();
                        break;
                    case State.DcsHeader:
                        if (ch is >= '@' and <= '~') Start(State.DcsBody);
                        else if (ch >= ' ' && ch != '\x7f') Append(ch, State.IgnoreString);
                        break;
                    case State.DcsBody:
                    case State.IgnoreString:
                        break;
                }
            }
        }

        private void Escape(char ch)
        {
            if (ch < ' ' || ch == '\x7f') return;
            if (_pending.Length == 1)
            {
                switch (ch)
                {
                    case '[': Start(State.Csi, "\x1b["); return;
                    case ']': Start(State.Osc); return;
                    case 'P': Start(State.DcsHeader, "\x1bP"); return;
                    case 'X': case '^': case '_': Start(State.IgnoreString); return;
                    case 'c': Reset(hard: true); Ground(); return; // RIS
                    case '=': SetPrivate(66, true); Ground(); return;
                    case '>': SetPrivate(66, false); Ground(); return;
                }
            }
            if (ch is >= ' ' and <= '/') Append(ch, State.Ground);
            else Ground();
        }

        private void Csi(char ch)
        {
            if (ch < ' ' || ch == '\x7f') return;
            if (ch is >= '@' and <= '~')
            {
                ApplyCsi(_pending.ToString().AsSpan(2), ch);
                Ground();
            }
            else if (ch is >= ' ' and <= '?') Append(ch, State.CsiIgnore);
            else Ground();
        }

        private void ApplyCsi(ReadOnlySpan<char> body, char final)
        {
            if (final == 'p' && body.EndsWith("!") && IsParameters(body[..^1]))
            {
                Reset(hard: false); // DECSTR does not leave alt or reset mouse modes in xterm.
                return;
            }
            if (final is not ('h' or 'l')) return;
            var isPrivate = body.StartsWith("?");
            if (isPrivate) body = body[1..];
            if (!IsParameters(body)) return;
            var enabled = final == 'h';
            foreach (var parameter in body.ToString().Split(';'))
            {
                if (!int.TryParse(parameter, out var mode)) continue;
                if (isPrivate) SetPrivate(mode, enabled);
                else if (mode is 4 or 20)
                {
                    if (enabled) _normal.Add(mode);
                    else _normal.Remove(mode);
                }
            }
        }

        private static bool IsParameters(ReadOnlySpan<char> body)
        {
            foreach (var ch in body)
                if (ch != ';' && (ch < '0' || ch > '9')) return false;
            return true;
        }

        private void SetPrivate(int mode, bool enabled)
        {
            switch (mode)
            {
                case 47: case 1047: case 1049:
                    _alternate = enabled;
                    break;
                case 9: case 1000: case 1002: case 1003:
                    _mouse = enabled ? mode : 0;
                    break;
                case 1006: case 1016:
                    _encoding = enabled ? mode : 0;
                    break;
                case 1: case 6: case 7: case 25: case 45: case 66: case 1004: case 2004:
                    var defaultValue = mode is 7 or 25;
                    if (enabled == defaultValue) _private.Remove(mode);
                    else _private[mode] = enabled;
                    break;
            }
        }

        private void Reset(bool hard)
        {
            _private.Clear();
            // xterm stores LNM (20) in convertEol, which survives both resets.
            _normal.Remove(4);
            if (hard) { _alternate = false; _mouse = _encoding = 0; }
        }

        private void Start(State state, string pending = "")
        {
            _state = state;
            _pending.Clear();
            _pending.Append(pending);
        }

        private void Ground() => Start(State.Ground);

        private void Append(char ch, State overflow)
        {
            if (_pending.Length < MaxSequence) _pending.Append(ch);
            else Start(overflow);
        }

        public string Prefix()
        {
            var result = new StringBuilder();
            if (_alternate) result.Append("\x1b[?1049h");
            foreach (var (mode, enabled) in _private)
                result.Append("\x1b[?").Append(mode).Append(enabled ? 'h' : 'l');
            if (_mouse != 0) result.Append("\x1b[?").Append(_mouse).Append('h');
            if (_encoding != 0) result.Append("\x1b[?").Append(_encoding).Append('h');
            foreach (var mode in _normal) result.Append("\x1b[").Append(mode).Append('h');
            // Resume incomplete controls without retaining unbounded OSC/DCS
            // payloads or replaying a partial title/clipboard operation.
            result.Append(_state switch
            {
                State.Escape or State.Csi or State.DcsHeader => _pending.ToString(),
                State.CsiIgnore => "\x1b[??",
                State.Osc => "\x1b]999999;",
                State.DcsBody => "\x1bP0z",
                State.IgnoreString => "\x1bX",
                _ => ""
            });
            return result.ToString();
        }
    }
}

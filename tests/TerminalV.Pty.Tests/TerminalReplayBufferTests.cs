using TerminalV.Host;

internal static class TerminalReplayBufferTests
{
    private const string Esc = "\x1b";

    public static void Run(Action<string, Action> check)
    {
        check("untrimmed host replay preserves the original wire output", () =>
        {
            var replay = new TerminalReplayBuffer(256);
            const string text = "shell\r\n\x1b[?1049h\x1b[?1003;1006hTUI\x1b[?1049l";
            foreach (var ch in text) replay.Add(ch.ToString());
            Equal(replay.Snapshot(), text);
            Equal(replay.Snapshot(), text); // Taking a snapshot must not advance the boundary.
        });

        check("Grok alternate screen and SGR mouse modes survive discarded startup output", () =>
        {
            foreach (var alias in new[] { 47, 1047, 1049 })
            {
                const string screen = "CURRENT SCREEN";
                var replay = new TerminalReplayBuffer(screen.Length);
                replay.Add($"old shell\r\n{Esc}[?{alias}h{Esc}[?1003;1006h");
                replay.Add(screen);
                Equal(replay.Snapshot(), Esc + "[?1049h" + Esc + "[?1003h" + Esc + "[?1006h" + screen);
            }
        });

        check("mode controls survive every wire chunk and trim boundary", () =>
        {
            foreach (var mode in new[] { 1049, 1000, 1002, 1003, 1006, 1016, 2004 })
            {
                var command = $"{Esc}[?{mode}h";
                var wire = command + "SCREEN";
                for (var trim = 0; trim <= command.Length; trim++)
                {
                    for (var split = 0; split <= wire.Length; split++)
                    {
                        var replay = new TerminalReplayBuffer(wire.Length - trim);
                        replay.Add(wire[..split]);
                        replay.Add(wire[split..]);
                        Equal(replay.Snapshot(), wire);
                    }
                }
            }
        });

        check("combined private modes remain correct when removed one character at a time", () =>
        {
            const string screen = "FRAME";
            var replay = new TerminalReplayBuffer(screen.Length);
            foreach (var ch in Esc + "[?1049;1003;1006;2004h" + screen)
                replay.Add(ch.ToString());
            Equal(replay.Snapshot(), Esc + "[?1049h" + Esc + "[?2004h" + Esc + "[?1003h" + Esc + "[?1006h" + screen);
        });

        check("replay restores boundary modes before retained TUI exit and shell history", () =>
        {
            var tail = "TUI FINAL" + Esc + "[?1003;1006;1049l\r\nSHELL HISTORY\r\nPROMPT";
            var replay = new TerminalReplayBuffer(tail.Length);
            replay.Add(Esc + "[?1049;1003;1006hOLD FRAME");
            replay.Add(tail);
            Equal(replay.Snapshot(), Esc + "[?1049h" + Esc + "[?1003h" + Esc + "[?1006h" + tail);

            // Once the exit itself is discarded the shell must stay in normal mode.
            var shell = new string('S', tail.Length);
            replay.Add(shell);
            Equal(replay.Snapshot(), shell);
        });

        check("retained mode changes are not applied early from the current terminal state", () =>
        {
            var tail = "SHELL" + Esc + "[?1049;1003;1006hNEW TUI";
            var replay = new TerminalReplayBuffer(tail.Length);
            replay.Add(new string('x', 500));
            replay.Add(tail);
            Equal(replay.Snapshot(), tail);
        });

        check("mouse tracking and encoding use xterm mutually exclusive mode semantics", () =>
        {
            Equal(AfterTrim(Esc + "[?1000;1002;1003;1006;1016h"), Esc + "[?1003h" + Esc + "[?1016hFRAME");
            Equal(AfterTrim(Esc + "[?1003;1016h" + Esc + "[?1000;1006l"), "FRAME");
            Equal(AfterTrim(Esc + "[?1049h" + Esc + "[?47l"), "FRAME");
        });

        check("hard reset clears modes and soft reset preserves active buffer and mouse", () =>
        {
            var setup = Esc + "[?1049;1003;1006;1;6;45;66;1004;2004h" + Esc + "[?7;25l" + Esc + "[4h";
            Equal(AfterTrim(setup + Esc + "c"), "FRAME");
            Equal(AfterTrim(setup + Esc + "[!p"), Esc + "[?1049h" + Esc + "[?1003h" + Esc + "[?1006hFRAME");
            Equal(AfterTrim(Esc + "[20h" + Esc + "[!p"), Esc + "[20hFRAME");
            Equal(AfterTrim(Esc + "[20h" + Esc + "c"), Esc + "[20hFRAME");
        });

        check("application keypad and ordinary input modes survive trimming and reset", () =>
        {
            Equal(AfterTrim(Esc + "=" + Esc + "[4;20h" + Esc + "[?1;2004h"),
                Esc + "[?1h" + Esc + "[?66h" + Esc + "[?2004h" + Esc + "[4h" + Esc + "[20hFRAME");
            Equal(AfterTrim(Esc + "=" + Esc + ">" + Esc + "[4h" + Esc + "[4l"), "FRAME");
        });

        check("OSC and DCS payloads cannot create private modes and resume safely across trimming", () =>
        {
            Equal(AfterTrim(Esc + "]0;?1049h;?1003h\a" + Esc + "Pq?1006h" + Esc + "\\"), "FRAME");
            foreach (var pair in new[] { (Start: Esc + "]0;", End: "\a", Resume: Esc + "]999999;"),
                (Start: Esc + "Pq", End: Esc + "\\", Resume: Esc + "P0z") })
            {
                var tail = "payload?1003h" + pair.End + "SCREEN";
                var replay = new TerminalReplayBuffer(tail.Length);
                replay.Add(pair.Start + new string('x', 5000));
                replay.Add(tail);
                Equal(replay.Snapshot(), pair.Resume + tail);
            }
        });

        check("cancelled and malformed controls cannot leave stale mouse modes", () =>
        {
            Equal(AfterTrim(Esc + "[?1003\x18h" + Esc + "[??1003h" + Esc + "[?1 003h"), "FRAME");
            Equal(AfterTrim("\x9b?1003;1006h"), Esc + "[?1003h" + Esc + "[?1006hFRAME");
            Equal(AfterTrim(Esc + "[?1003h" + Esc + "[?1003\x1al"), Esc + "[?1003hFRAME");
        });

        check("large chunks and unfinished control strings keep replay memory bounded", () =>
        {
            var replay = new TerminalReplayBuffer(16);
            replay.Add(new string('x', 100_000));
            Equal(replay.Snapshot(), new string('x', 16));
            replay.Add(Esc + "]0;" + new string('a', 100_000));
            Equal(replay.Snapshot(), Esc + "]999999;" + new string('a', 16));
            replay.Add("\a" + Esc + "[?" + new string('1', 5000));
            Equal(replay.Snapshot(), Esc + "[??" + new string('1', 16));
            replay.Add("h" + new string('F', 16));
            Equal(replay.Snapshot(), new string('F', 16));
        });
    }

    private static string AfterTrim(string discarded)
    {
        var replay = new TerminalReplayBuffer(5);
        replay.Add(discarded);
        replay.Add("FRAME");
        return replay.Snapshot();
    }

    private static void Equal(string actual, string expected)
    {
        if (actual != expected)
            throw new Exception($"Expected {Escape(expected)}, got {Escape(actual)}");
    }

    private static string Escape(string value) => value.Replace(Esc, "<ESC>");
}

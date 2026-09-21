using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace TerminalV.Diagnostics;

internal static class Diag
{
    private static readonly ConcurrentQueue<string> Queue = new();
    private static int _writerRunning;
    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static string? _path;
    private const long MaxBytes = 5 * 1024 * 1024;

    private static string PathForLog()
    {
        if (_path is not null) return _path;
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerminalV");
            Directory.CreateDirectory(dir);
            _path = System.IO.Path.Combine(dir, "diagnostics.log");
        }
        catch
        {
            _path = System.IO.Path.Combine(AppContext.BaseDirectory, "diagnostics.log");
        }
        return _path;
    }

    public static void Log(string area, string message, string? detail = null)
    {
        var ts = Clock.Elapsed;
        var line = $"{DateTime.UtcNow:HH:mm:ss.fff} +{ts.TotalSeconds:000.000}s [T{Environment.CurrentManagedThreadId:00}] {area}: {message}" + (detail is null ? "" : $" | {detail}");
        try { Debug.WriteLine(line); } catch { }
        // Hot path (paste/write per chunk) must not block on file I/O — enqueue async.
        Queue.Enqueue(line);
        if (Interlocked.Exchange(ref _writerRunning, 1) == 0)
            _ = Task.Run(FlushQueue);
    }

    private static void FlushQueue()
    {
        try
        {
            // Batch up to 50 lines per flush to keep per-chunk paste fast ("по строчке" lag)
            while (true)
            {
                var batch = new System.Collections.Generic.List<string>(50);
                while (batch.Count < 50 && Queue.TryDequeue(out var l)) batch.Add(l);
                if (batch.Count == 0) break;
                try
                {
                    lock (Gate)
                    {
                        var p = PathForLog();
                        try
                        {
                            var fi = new FileInfo(p);
                            if (fi.Exists && fi.Length > MaxBytes)
                            {
                                var bak = p + ".1";
                                File.Delete(bak);
                                File.Move(p, bak);
                            }
                        }
                        catch { }
                        File.AppendAllLines(p, batch);
                    }
                }
                catch { }
                if (Queue.IsEmpty) break;
            }
        }
        finally { Interlocked.Exchange(ref _writerRunning, 0); if (!Queue.IsEmpty && Interlocked.Exchange(ref _writerRunning, 1) == 0) _ = Task.Run(FlushQueue); }
    }

    public static IDisposable Time(string area, string op, int? bytes = null)
    {
        var sw = Stopwatch.StartNew();
        return new Scope(area, op, bytes, sw);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _area;
        private readonly string _op;
        private readonly int? _bytes;
        private readonly Stopwatch _sw;
        private bool _done;
        public Scope(string a, string o, int? b, Stopwatch s) { _area = a; _op = o; _bytes = b; _sw = s; }
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _sw.Stop();
            var extra = _bytes is null ? "" : $" bytes={_bytes}";
            // only log slow ops (>50ms) or errors to avoid spam, but keep paste ops always
            if (_sw.ElapsedMilliseconds > 50 || _op.Contains("paste", StringComparison.OrdinalIgnoreCase) || _op.Contains("write", StringComparison.OrdinalIgnoreCase))
                Log(_area, $"{_op} done {_sw.ElapsedMilliseconds}ms{extra}", null);
        }
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TerminalV.Diagnostics;

namespace TerminalV.Pty;

internal sealed class ConPtySession : IDisposable
{
    private readonly IntPtr _pseudoConsole;
    private readonly IntPtr _attributeList;
    private readonly IntPtr _pseudoConsoleValue;
    private readonly IntPtr _hProcess;
    private readonly IntPtr _hThread;
    private readonly FileStream _inputStream;
    private readonly FileStream _outputStream;
    private readonly StreamWriter _writer;
    private readonly object _queueGate = new();
    private readonly object _writerGate = new();
    private readonly Queue<string> _writeQueue = new();
    private bool _writePumpRunning;
    private readonly WorkingDirectoryTracker _directory;
    private int _disposed;

    public string Id { get; }
    public int ProcessId { get; }
    public string CurrentDirectory => _directory.CurrentDirectory;

    public event Action<string>? Output;
    public event Action<uint>? Exited;
    public event Action<string>? DirectoryChanged;

    private ConPtySession(
        string id,
        IntPtr pseudoConsole,
        IntPtr attributeList,
        IntPtr pseudoConsoleValue,
        NativeMethods.ProcessInformation processInfo,
        FileStream inputStream,
        FileStream outputStream,
        string workingDirectory)
    {
        Id = id;
        _directory = new WorkingDirectoryTracker(workingDirectory);
        _directory.Changed += path => DirectoryChanged?.Invoke(path);
        _pseudoConsole = pseudoConsole;
        _attributeList = attributeList;
        _pseudoConsoleValue = pseudoConsoleValue;
        _hProcess = processInfo.hProcess;
        _hThread = processInfo.hThread;
        ProcessId = processInfo.dwProcessId;
        _inputStream = inputStream;
        _outputStream = outputStream;
        _writer = new StreamWriter(inputStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };

    }

    public static ConPtySession Start(
        string id,
        string commandLine,
        string workingDirectory,
        int cols,
        int rows,
        Action<ConPtySession>? configure = null)
    {
        if (!NativeMethods.CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось создать входной канал ConPTY.");
        }

        if (!NativeMethods.CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
        {
            inputRead.Dispose();
            inputWrite.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось создать выходной канал ConPTY.");
        }

        var size = new NativeMethods.Coord
        {
            X = (short)Math.Clamp(cols, 1, short.MaxValue),
            Y = (short)Math.Clamp(rows, 1, short.MaxValue)
        };

        var hr = NativeMethods.CreatePseudoConsole(size, inputRead, outputWrite, 0, out var hPC);
        if (hr != 0)
        {
            inputRead.Dispose();
            inputWrite.Dispose();
            outputRead.Dispose();
            outputWrite.Dispose();
            throw new Win32Exception(hr, "Не удалось создать псевдоконсоль.");
        }

        var startupInfo = ConfigureStartup(hPC);
        NativeMethods.ProcessInformation processInfo;
        try
        {
            processInfo = CreateChild(commandLine, workingDirectory, ref startupInfo);
        }
        catch
        {
            NativeMethods.ClosePseudoConsole(hPC);
            NativeMethods.DeleteProcThreadAttributeList(startupInfo.lpAttributeList);
            Marshal.FreeHGlobal(startupInfo.lpAttributeList);
            inputRead.Dispose();
            inputWrite.Dispose();
            outputRead.Dispose();
            outputWrite.Dispose();
            throw;
        }

        // Docs: close the ConPTY ends only after CreateProcess.
        inputRead.Dispose();
        outputWrite.Dispose();

        var inputStream = new FileStream(inputWrite, FileAccess.Write);
        var outputStream = new FileStream(outputRead, FileAccess.Read);

        var session = new ConPtySession(id, hPC, startupInfo.lpAttributeList, IntPtr.Zero,
            processInfo, inputStream, outputStream, workingDirectory);
        // Subscribe before reading: the first prompt may already contain the real directory.
        try
        {
            configure?.Invoke(session);
        }
        catch
        {
            session.Dispose();
            throw;
        }
        _ = Task.Factory.StartNew(session.ReadLoop, TaskCreationOptions.LongRunning);
        _ = Task.Factory.StartNew(session.WaitLoop, TaskCreationOptions.LongRunning);
        return session;
    }

    public void Write(string data)
    {
        if (string.IsNullOrEmpty(data) || _disposed != 0)
        {
            return;
        }

        var enqueueSw = Stopwatch.StartNew();
        const int Chunk = 8192;
        List<string> chunks;
        if (data.Length <= Chunk)
        {
            chunks = [data];
        }
        else
        {
            chunks = [];
            for (var i = 0; i < data.Length;)
            {
                var len = Math.Min(Chunk, data.Length - i);
                if (len < data.Length - i && char.IsHighSurrogate(data[i + len - 1]) && i + len < data.Length && char.IsLowSurrogate(data[i + len]))
                    len--;
                chunks.Add(data.Substring(i, len));
                i += len;
            }
        }

        int qLen;
        bool wasRunning;
        lock (_queueGate)
        {
            foreach (var c in chunks) _writeQueue.Enqueue(c);
            qLen = _writeQueue.Count;
            wasRunning = _writePumpRunning;
            if (_writePumpRunning) { }
            else _writePumpRunning = true;
        }
        enqueueSw.Stop();
        if (enqueueSw.ElapsedMilliseconds > 20 || chunks.Count > 1)
            Diag.Log("pty", $"Write enqueue id={Id} chunks={chunks.Count} len={data.Length} qLen={qLen} wasRunning={wasRunning} enqueueMs={enqueueSw.ElapsedMilliseconds}", null);
        if (wasRunning) return;
        _ = Task.Run(ProcessWriteQueue);
    }

    private void ProcessWriteQueue()
    {
        while (true)
        {
            string chunk;
            int remaining;
            lock (_queueGate)
            {
                if (_writeQueue.Count == 0)
                {
                    _writePumpRunning = false;
                    return;
                }
                chunk = _writeQueue.Dequeue();
                remaining = _writeQueue.Count;
            }
            if (_disposed != 0) return;
            var sw = Stopwatch.StartNew();
            try
            {
                // Dedicated gate for writer — queueGate stays free so enqueue never blocks on a stuck pipe.
                lock (_writerGate)
                {
                    if (_disposed != 0) return;
                    _writer.Write(chunk);
                }
            }
            catch (IOException ex) { Diag.Log("pty", $"Write chunk failed id={Id} remaining={remaining} ex={ex.Message}", null); return; }
            catch (ObjectDisposedException) { return; }
            sw.Stop();
            if (sw.ElapsedMilliseconds > 50)
                Diag.Log("pty", $"Write chunk slow id={Id} chunkLen={chunk.Length} remaining={remaining} ms={sw.ElapsedMilliseconds}", null);
        }
    }

    public void Resize(int cols, int rows)
    {
        if (_disposed != 0)
        {
            return;
        }

        var size = new NativeMethods.Coord
        {
            X = (short)Math.Clamp(cols, 1, short.MaxValue),
            Y = (short)Math.Clamp(rows, 1, short.MaxValue)
        };
        _ = NativeMethods.ResizePseudoConsole(_pseudoConsole, size);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            NativeMethods.ClosePseudoConsole(_pseudoConsole);
        }
        catch
        {
        }

        try
        {
            NativeMethods.TerminateProcess(_hProcess, 1);
        }
        catch
        {
        }

        try
        {
            lock (_writerGate)
            {
                lock (_queueGate) { _writeQueue.Clear(); }
                _writer.Dispose();
            }
        }
        catch
        {
        }

        try
        {
            _outputStream.Dispose();
        }
        catch
        {
        }

        if (_attributeList != IntPtr.Zero)
        {
            NativeMethods.DeleteProcThreadAttributeList(_attributeList);
            Marshal.FreeHGlobal(_attributeList);
        }

        if (_pseudoConsoleValue != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_pseudoConsoleValue);
        }

        if (_hThread != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_hThread);
        }

        if (_hProcess != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_hProcess);
        }
    }

    private void ReadLoop()
    {
        var buffer = new byte[4096];
        var chars = new char[4096];
        var decoder = Encoding.UTF8.GetDecoder();

        try
        {
            while (_disposed == 0)
            {
                var read = _outputStream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                var charCount = decoder.GetChars(buffer, 0, read, chars, 0);
                if (charCount > 0)
                {
                    var chunk = new string(chars, 0, charCount);
                    _directory.Feed(chunk);
                    Output?.Invoke(chunk);
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private void WaitLoop()
    {
        try
        {
            NativeMethods.WaitForSingleObject(_hProcess, NativeMethods.Infinite);
            NativeMethods.GetExitCodeProcess(_hProcess, out var code);
            Exited?.Invoke(code);
        }
        catch
        {
            Exited?.Invoke(1);
        }
    }

    private static NativeMethods.StartupInfoEx ConfigureStartup(IntPtr hPC)
    {
        var lpSize = IntPtr.Zero;
        var success = NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
        if (success || lpSize == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось вычислить размер списка атрибутов процесса.");
        }

        var startupInfo = new NativeMethods.StartupInfoEx();
        startupInfo.StartupInfo.cb = Marshal.SizeOf<NativeMethods.StartupInfoEx>();
        startupInfo.lpAttributeList = Marshal.AllocHGlobal(lpSize);

        success = NativeMethods.InitializeProcThreadAttributeList(startupInfo.lpAttributeList, 1, 0, ref lpSize);
        if (!success)
        {
            Marshal.FreeHGlobal(startupInfo.lpAttributeList);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось инициализировать список атрибутов процесса.");
        }

        // For PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE lpValue is the HPCON itself, not &HPCON.
        success = NativeMethods.UpdateProcThreadAttribute(
            startupInfo.lpAttributeList,
            0,
            (IntPtr)NativeMethods.ProcThreadAttributePseudoconsole,
            hPC,
            (IntPtr)IntPtr.Size,
            IntPtr.Zero,
            IntPtr.Zero);

        if (!success)
        {
            NativeMethods.DeleteProcThreadAttributeList(startupInfo.lpAttributeList);
            Marshal.FreeHGlobal(startupInfo.lpAttributeList);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось привязать псевдоконсоль к процессу.");
        }

        return startupInfo;
    }

    private static NativeMethods.ProcessInformation CreateChild(
        string commandLine,
        string workingDirectory,
        ref NativeMethods.StartupInfoEx startupInfo)
    {
        var cwd = Directory.Exists(workingDirectory) ? workingDirectory : null;
        var mutableCommand = new System.Text.StringBuilder(commandLine);
        using var environment = SessionEnvironment.Create();
        var success = NativeMethods.CreateProcess(
            null,
            mutableCommand,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            NativeMethods.ExtendedStartupInfoPresent | NativeMethods.CreateUnicodeEnvironment,
            environment,
            cwd,
            ref startupInfo,
            out var processInfo);

        if (!success)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось запустить оболочку.");
        }

        return processInfo;
    }
}

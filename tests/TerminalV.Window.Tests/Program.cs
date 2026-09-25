using System.IO;
using System.Windows;
using System.Windows.Threading;
using TerminalV;

internal static class Program
{
    private static readonly HashSet<Window> OpenWindows = [];
    private static int _passed;

    [STAThread]
    private static void Main()
    {
        Check("maximized state is restored before and after the first display", () =>
        {
            var saved = true;
            var writes = new List<bool>();
            var errors = new List<Exception>();
            var window = NewWindow();
            WindowStatePersistence.Hook(window, () => saved, value =>
            {
                saved = value;
                writes.Add(value);
            }, errors.Add);
            Equal(window.WindowState, WindowState.Maximized);
            Show(window);
            Equal(window.WindowState, WindowState.Maximized);
            window.Close();
            Equal(saved, true);
            Equal(writes.Contains(false), false);
            Equal(errors.Count, 0);
        });

        Check("minimizing a maximized window then closing preserves maximized state", () =>
        {
            var saved = true;
            var errors = new List<Exception>();
            var window = NewWindow();
            WindowStatePersistence.Hook(window, () => saved, value => saved = value, errors.Add);
            Show(window);
            window.WindowState = WindowState.Minimized;
            DrainDispatcher();
            Equal(window.WindowState, WindowState.Minimized);
            Equal(saved, true);
            window.Close();
            Equal(saved, true);

            var reopened = NewWindow();
            WindowStatePersistence.Hook(reopened, () => saved, value => saved = value, errors.Add);
            Show(reopened);
            Equal(reopened.WindowState, WindowState.Maximized);
            Equal(errors.Count, 0);
        });

        Check("restoring to normal is saved immediately and survives reopening", () =>
        {
            var saved = true;
            var errors = new List<Exception>();
            var window = NewWindow();
            WindowStatePersistence.Hook(window, () => saved, value => saved = value, errors.Add);
            Show(window);
            window.WindowState = WindowState.Normal;
            DrainDispatcher();
            Equal(saved, false);
            window.Close();

            var reopened = NewWindow();
            WindowStatePersistence.Hook(reopened, () => saved, value => saved = value, errors.Add);
            Equal(reopened.WindowState, WindowState.Normal);
            Show(reopened);
            Equal(reopened.WindowState, WindowState.Normal);
            Equal(errors.Count, 0);
        });

        Check("closing before the first display preserves the restored state", () =>
        {
            bool? saved = null;
            var errors = new List<Exception>();
            var window = NewWindow();
            WindowStatePersistence.Hook(window, () => true, value => saved = value, errors.Add);
            window.Close();
            Equal(saved, (bool?)true);
            Equal(OpenWindows.Contains(window), false);
            Equal(errors.Count, 0);
        });

        Check("a storage read failure leaves a usable normal window", () =>
        {
            bool? saved = null;
            var errors = new List<Exception>();
            var window = NewWindow();
            WindowStatePersistence.Hook(window,
                () => throw new IOException("test read failure"), value => saved = value, errors.Add);
            Equal(window.WindowState, WindowState.Normal);
            Equal(errors.Count, 1);
            Show(window);
            window.Close();
            Equal(saved, (bool?)false);
            Equal(OpenWindows.Contains(window), false);
        });

        Check("a failed state save does not block the window and is retried on close", () =>
        {
            var saved = false;
            var failNextMaximizeSave = true;
            var errors = new List<Exception>();
            var window = NewWindow();
            WindowStatePersistence.Hook(window, () => saved, value =>
            {
                if (value && failNextMaximizeSave)
                {
                    failNextMaximizeSave = false;
                    throw new IOException("test write failure");
                }
                saved = value;
            }, errors.Add);
            Show(window);
            window.WindowState = WindowState.Maximized;
            DrainDispatcher();
            Equal(window.WindowState, WindowState.Maximized);
            Equal(errors.Count, 1);
            Equal(saved, false);
            window.Close();
            Equal(saved, true);
            Equal(OpenWindows.Contains(window), false);
            Equal(errors.Count, 1);
        });

        Console.WriteLine($"{_passed} checks passed.");
    }

    private static Window NewWindow()
    {
        var window = new Window
        {
            Title = "TerminalV window persistence test",
            Width = 320,
            Height = 200,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        OpenWindows.Add(window);
        window.Closed += (_, _) => OpenWindows.Remove(window);
        return window;
    }

    private static void Show(Window window)
    {
        window.Show();
        DrainDispatcher();
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Check(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"PASS {name}");
            _passed++;
        }
        finally
        {
            foreach (var window in OpenWindows.ToArray()) window.Close();
            DrainDispatcher();
        }
    }

    private static void Equal<T>(T actual, T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new Exception($"Expected {expected}, got {actual}");
    }
}

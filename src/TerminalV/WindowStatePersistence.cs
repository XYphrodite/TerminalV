using System.Windows;

namespace TerminalV;

internal static class WindowStatePersistence
{
    public static void Hook(Window window, Func<bool> load, Action<bool> save, Action<Exception> onError)
    {
        var maximized = false;
        try
        {
            maximized = load();
            // Restore before the first Show, without persisting startup transitions.
            window.WindowState = maximized ? WindowState.Maximized : WindowState.Normal;
        }
        catch (Exception ex)
        {
            onError(ex);
        }

        window.StateChanged += (_, _) =>
        {
            // Minimize is temporary; reopen in the last visible state.
            if (window.WindowState == WindowState.Minimized) return;
            maximized = window.WindowState == WindowState.Maximized;
            Save();
        };
        // Also covers early close before WebView loads, and retries a failed save.
        window.Closing += (_, _) => Save();

        void Save()
        {
            try { save(maximized); }
            catch (Exception ex) { onError(ex); }
        }
    }
}

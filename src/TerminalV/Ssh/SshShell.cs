using System.Reflection;
using System.Runtime.ExceptionServices;

namespace TerminalV.Ssh;

internal static class SshShell
{
    public static void Resize(object stream, uint columns, uint rows)
    {
        // Public since SSH.NET 2025.1.0. Keep the sessions' late binding, without
        // relying on private channel fields or silently ignoring failed resizes.
        var method = stream.GetType().GetMethod("ChangeWindowSize",
            BindingFlags.Instance | BindingFlags.Public, binder: null,
            types: new[] { typeof(uint), typeof(uint), typeof(uint), typeof(uint) }, modifiers: null)
            ?? throw new MissingMethodException(stream.GetType().FullName, "ChangeWindowSize");
        try
        {
            method.Invoke(stream, new object[] { columns, rows, 0u, 0u });
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}

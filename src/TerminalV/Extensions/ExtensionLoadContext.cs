using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using TerminalV.Extensibility;

namespace TerminalV.Extensions;

internal sealed class ExtensionLoadContext(string assemblyPath) : AssemblyLoadContext(isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

    protected override Assembly? Load(AssemblyName name)
    {
        // Always share the host contract, even if a package includes its own copy.
        var contract = typeof(ITerminalVExtension).Assembly;
        if (name.Name == contract.GetName().Name) return contract;
        var path = _resolver.ResolveAssemblyToPath(name);
        if (path is not null) return LoadFromAssemblyPath(path);
        // Also support small manually assembled packages without a deps.json.
        var sibling = Path.Combine(Path.GetDirectoryName(assemblyPath)!, name.Name + ".dll");
        return File.Exists(sibling) ? LoadFromAssemblyPath(sibling) : null;
    }

    protected override nint LoadUnmanagedDll(string name)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(name);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}

using System.Text.Json;

namespace TerminalV.Extensibility;

/// <summary>A trusted desktop extension. Instances live until TerminalV closes.</summary>
public interface ITerminalVExtension : IAsyncDisposable
{
    /// <summary>Initialize and return promptly. Use lifetime to stop background work.</summary>
    Task ActivateAsync(IExtensionContext context, CancellationToken lifetime);

    /// <summary>Handle a request from this extension's UI. Requests may overlap.</summary>
    Task<JsonElement?> InvokeAsync(string method, JsonElement? args, CancellationToken cancellationToken);
}

public interface IExtensionContext
{
    string Id { get; }
    string DataDirectory { get; }
    IExtensionStorage Storage { get; }
    IExtensionStorage Secrets { get; }
    void Publish(string eventName, object? data);
    void Log(string message);
}

/// <summary>Thread-safe persistent strings scoped to one extension. Null deletes a key.</summary>
public interface IExtensionStorage
{
    string? Get(string key);
    void Set(string key, string? value);
}

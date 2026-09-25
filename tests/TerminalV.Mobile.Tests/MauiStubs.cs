// Only device services are substituted. The production bridge, store, SSH service,
// WebSocket clients and gateway server run unchanged, with no user preferences.
global using Microsoft.Maui.Storage;
global using Microsoft.Maui.ApplicationModel;
global using Microsoft.Maui.Devices;

namespace Microsoft.Maui.Storage
{
    public sealed class Preferences
    {
        public static Preferences Default { get; } = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> values = new();
        public T Get<T>(string key, T fallback) => values.TryGetValue(key, out var value) ? (T)value : fallback;
        public void Set<T>(string key, T value) where T : notnull => values[key] = value;
    }
    public static class FileSystem
    {
        public static string AppDataDirectory => throw new NotSupportedException();
        public static string CacheDirectory => throw new NotSupportedException();
    }
    public sealed class FilePicker
    {
        public static FilePicker Default { get; } = new();
        public Task<FileResult?> PickAsync(PickOptions options) => throw new NotSupportedException();
    }
    public sealed class FileResult
    {
        public string FileName => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync() => throw new NotSupportedException();
    }
    public sealed class PickOptions
    {
        public string? PickerTitle { get; set; }
        public FilePickerFileType? FileTypes { get; set; }
    }
    public sealed class FilePickerFileType(Dictionary<DevicePlatform, IEnumerable<string>> types) { }
}
namespace Microsoft.Maui.ApplicationModel
{
    public sealed class AppInfo
    {
        public static AppInfo Current { get; } = new();
        public string VersionString => "1.0.3";
    }
    public static class MainThread
    {
        public static void BeginInvokeOnMainThread(Action action) => action();
    }
    public enum BrowserLaunchMode { SystemPreferred }
    public sealed class Browser
    {
        public static Browser Default { get; } = new();
        public Task OpenAsync(string uri, BrowserLaunchMode mode) => throw new NotSupportedException();
    }
}
namespace Microsoft.Maui.Devices
{
    public enum DevicePlatform { Android, iOS, WinUI }
}
namespace Microsoft.Maui.ApplicationModel.DataTransfer
{
    public sealed class Clipboard
    {
        public static Clipboard Default { get; } = new();
        public Task<string> GetTextAsync() => throw new NotSupportedException();
        public Task SetTextAsync(string text) => throw new NotSupportedException();
    }
    public sealed class Share
    {
        public static Share Default { get; } = new();
        public Task RequestAsync(ShareFileRequest request) => throw new NotSupportedException();
    }
    public sealed class ShareFile(string path) { }
    public sealed class ShareFileRequest
    {
        public string? Title { get; set; }
        public ShareFile? File { get; set; }
    }
}

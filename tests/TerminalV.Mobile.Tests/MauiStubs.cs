// Only device services are substituted. The production bridge, store, SSH service,
// WebSocket clients and gateway server run unchanged, with no user preferences.
global using Microsoft.Maui.Storage;
global using Microsoft.Maui.ApplicationModel;
global using Microsoft.Maui.Devices;
global using Microsoft.Maui.Media;

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
        public static FilePicker Default { get; set; } = new();
        public Func<PickOptions, Task<FileResult?>>? PickHandler { get; set; }
        public Task<FileResult?> PickAsync(PickOptions options) =>
            PickHandler is not null ? PickHandler(options) : Task.FromResult<FileResult?>(null);
    }
    public class FileResult
    {
        public FileResult() { }
        public FileResult(string fullPath)
        {
            FullPath = fullPath;
            FileName = System.IO.Path.GetFileName(fullPath);
        }
        public string FileName { get; set; } = "";
        public string? FullPath { get; set; }
        public string? ContentType { get; set; }
        public Func<Task<Stream>>? StreamOpener { get; set; }
        public virtual Task<Stream> OpenReadAsync() => StreamOpener is not null ? StreamOpener() : Task.FromResult<Stream>(Stream.Null);
    }
    public sealed class PickOptions
    {
        public string? PickerTitle { get; set; }
        public FilePickerFileType? FileTypes { get; set; }
    }
    public sealed class FilePickerFileType(Dictionary<DevicePlatform, IEnumerable<string>> types)
    {
        public Dictionary<DevicePlatform, IEnumerable<string>> Types { get; } = types;
    }
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
        public static Task InvokeOnMainThreadAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
        public static Task InvokeOnMainThreadAsync(Func<Task> funcTask) => funcTask();
        public static Task<T> InvokeOnMainThreadAsync<T>(Func<T> func) => Task.FromResult(func());
        public static Task<T> InvokeOnMainThreadAsync<T>(Func<Task<T>> funcTask) => funcTask();
    }
    public enum PermissionStatus
    {
        Unknown,
        Denied,
        Disabled,
        Granted,
        Restricted,
        Limited
    }
    public static class Permissions
    {
        public abstract class BasePermission { }
        public class Camera : BasePermission { }

        public static PermissionStatus Status { get; set; } = PermissionStatus.Granted;

        public static Task<PermissionStatus> RequestAsync<TPermission>() where TPermission : BasePermission, new() =>
            Task.FromResult(Status);

        public static Task<PermissionStatus> CheckStatusAsync<TPermission>() where TPermission : BasePermission, new() =>
            Task.FromResult(Status);
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
namespace Microsoft.Maui.Media
{
    public interface IMediaPicker
    {
        bool IsCaptureSupported { get; }
        Task<FileResult?> PickPhotoAsync(MediaPickerOptions? options = null);
        Task<FileResult?> CapturePhotoAsync(MediaPickerOptions? options = null);
    }
    public sealed class MediaPicker : IMediaPicker
    {
        public static IMediaPicker Default { get; set; } = new MediaPicker();
        public bool IsCaptureSupported { get; set; } = true;
        public Func<MediaPickerOptions?, Task<FileResult?>>? PickPhotoHandler { get; set; }
        public Func<MediaPickerOptions?, Task<FileResult?>>? CapturePhotoHandler { get; set; }

        public Task<FileResult?> PickPhotoAsync(MediaPickerOptions? options = null) =>
            PickPhotoHandler is not null ? PickPhotoHandler(options) : Task.FromResult<FileResult?>(null);

        public Task<FileResult?> CapturePhotoAsync(MediaPickerOptions? options = null) =>
            CapturePhotoHandler is not null ? CapturePhotoHandler(options) : Task.FromResult<FileResult?>(null);
    }
    public sealed class MediaPickerOptions
    {
        public string? Title { get; set; }
    }
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
    public sealed class ShareFile(string path)
    {
        public string Path { get; } = path;
    }
    public sealed class ShareFileRequest
    {
        public string? Title { get; set; }
        public ShareFile? File { get; set; }
    }
}

using System.Text.Json;
using TerminalV.Data;

namespace TerminalV.Mobile.Host;

internal sealed class MobileDataStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private const string KeySettings = "terminalv_app_settings";
    private const string KeySessions = "terminalv_sessions";
    private const string KeyLayouts = "terminalv_layouts";
    private const string KeyProfiles = "terminalv_profiles";

    public AppSettings LoadSettings()
    {
        try
        {
            var raw = Microsoft.Maui.Storage.Preferences.Default.Get(KeySettings, "");
            if (string.IsNullOrWhiteSpace(raw)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(raw, Json) ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public void SaveSettings(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, Json);
            Microsoft.Maui.Storage.Preferences.Default.Set(KeySettings, json);
        }
        catch { }
    }

    public List<SessionRecord> LoadSessions()
    {
        try
        {
            var raw = Microsoft.Maui.Storage.Preferences.Default.Get(KeySessions, "");
            if (string.IsNullOrWhiteSpace(raw)) return [];
            return JsonSerializer.Deserialize<List<SessionRecord>>(raw, Json) ?? [];
        }
        catch { return []; }
    }

    public void SaveSessions(List<SessionRecord> sessions)
    {
        try
        {
            var json = JsonSerializer.Serialize(sessions, Json);
            Microsoft.Maui.Storage.Preferences.Default.Set(KeySessions, json);
        }
        catch { }
    }

    public List<PaneLayout> LoadLayouts()
    {
        try
        {
            var raw = Microsoft.Maui.Storage.Preferences.Default.Get(KeyLayouts, "");
            if (string.IsNullOrWhiteSpace(raw)) return [];
            return JsonSerializer.Deserialize<List<PaneLayout>>(raw, Json) ?? [];
        }
        catch { return []; }
    }

    public void SaveLayouts(List<PaneLayout> layouts)
    {
        try
        {
            var json = JsonSerializer.Serialize(layouts, Json);
            Microsoft.Maui.Storage.Preferences.Default.Set(KeyLayouts, json);
        }
        catch { }
    }

    public List<LaunchProfile> LoadProfiles()
    {
        try
        {
            var raw = Microsoft.Maui.Storage.Preferences.Default.Get(KeyProfiles, "");
            if (string.IsNullOrWhiteSpace(raw)) return [];
            return JsonSerializer.Deserialize<List<LaunchProfile>>(raw, Json) ?? [];
        }
        catch { return []; }
    }

    public void SaveProfiles(IReadOnlyList<LaunchProfile> profiles)
    {
        if (profiles.Count > 100 || profiles.Select(p => p.Id).Distinct().Count() != profiles.Count)
            throw new ArgumentException("Допустимо до 100 профилей с уникальными идентификаторами.");
        foreach (var p in profiles) p.Validate();
        try
        {
            var json = JsonSerializer.Serialize(profiles, Json);
            Microsoft.Maui.Storage.Preferences.Default.Set(KeyProfiles, json);
        }
        catch (Exception ex) { throw new InvalidOperationException(ex.Message, ex); }
    }
}

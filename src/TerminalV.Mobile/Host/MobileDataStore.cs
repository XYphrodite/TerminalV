using System.Text.Json;
using TerminalV.Data;

namespace TerminalV.Mobile.Host;

internal sealed class MobileDataStore
{
    private readonly object _archiveLock = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private const string KeySettings = "terminalv_app_settings";
    private const string KeySessions = "terminalv_sessions";
    private const string KeyArchivedSessions = "terminalv_archived_sessions";
    private const string KeyLayouts = "terminalv_layouts";
    private const string KeyProfiles = "terminalv_profiles";

    public AppSettings LoadSettings()
    {
        try
        {
            var raw = Microsoft.Maui.Storage.Preferences.Default.Get(KeySettings, "");
            if (string.IsNullOrWhiteSpace(raw))
            {
                // Smaller default for phone (desktop is 14)
                return new AppSettings { FontSize = 12, Zoom = 0 };
            }
            var s = JsonSerializer.Deserialize<AppSettings>(raw, Json) ?? new AppSettings();
            // Clamp zoom that may have been set for desktop to a phone-friendly range
            if (s.Zoom < -2) s.Zoom = -2;
            return s;
        }
        catch { return new AppSettings { FontSize = 12 }; }
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

    public List<SessionRecord> LoadArchivedSessions()
    {
        try
        {
            var raw = Microsoft.Maui.Storage.Preferences.Default.Get(KeyArchivedSessions, "");
            return string.IsNullOrWhiteSpace(raw) ? [] : JsonSerializer.Deserialize<List<SessionRecord>>(raw, Json) ?? [];
        }
        catch { return []; }
    }

    public void ArchiveSessions(IEnumerable<SessionRecord> sessions)
    {
        var retired = sessions.ToArray();
        if (retired.Length == 0) return;
        lock (_archiveLock)
        {
            var archive = LoadArchivedSessions().ToLookup(s => s.Id).ToDictionary(group => group.Key, group => group.Last());
            foreach (var session in retired)
            {
                // An empty view must not erase a previously saved terminal screen.
                if (archive.TryGetValue(session.Id, out var previous) && string.IsNullOrEmpty(session.Buffer))
                    session.Buffer = previous.Buffer;
                archive[session.Id] = session;
            }
            Microsoft.Maui.Storage.Preferences.Default.Set(KeyArchivedSessions, JsonSerializer.Serialize(archive.Values, Json));
        }
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

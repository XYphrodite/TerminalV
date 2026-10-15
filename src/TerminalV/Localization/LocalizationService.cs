using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using TerminalV.Data;

namespace TerminalV.Localization;

public static class LocalizationService
{
    private static IServiceProvider? _provider;
    private static IStringLocalizerFactory? _factory;
    private static IStringLocalizer? _localizer;

    public static IServiceProvider Provider => _provider ?? throw new InvalidOperationException("Localization not initialized. Call Initialize() first.");

    public static IStringLocalizerFactory Factory => _factory ?? throw new InvalidOperationException("Localization not initialized.");

    public static IStringLocalizer Localizer => _localizer ?? throw new InvalidOperationException("Localization not initialized.");

    public static void Initialize()
    {
        if (_provider is not null) return;

        var services = new ServiceCollection();
        services.AddLogging();
        // Resx live at Localization/Resources/Strings.*.resx and embed as
        // TerminalV.Localization.Resources.Strings. ResourcesPath is appended to
        // the marker type's namespace, so it must be just "Resources" — otherwise
        // the factory looks for TerminalV.Localization.Localization.Resources.Strings
        // and every {loc:Translate Key} falls back to showing the raw key.
        services.AddLocalization(options => options.ResourcesPath = "Resources");
        _provider = services.BuildServiceProvider();
        _factory = _provider.GetRequiredService<IStringLocalizerFactory>();
        _localizer = CreateStringsLocalizer();

        // Apply saved language preference if available.
        try
        {
            using var db = new AppDatabase();
            var settings = db.LoadSettings();
            if (!string.IsNullOrWhiteSpace(settings.Language))
            {
                ApplyLanguage(settings.Language);
            }
        }
        catch
        {
        }
    }

    public static void Initialize(IServiceProvider provider)
    {
        _provider = provider;
        _factory = provider.GetRequiredService<IStringLocalizerFactory>();
        _localizer = CreateStringsLocalizer();
    }

    /// <summary>
    /// Looks up Strings.resx by its exact manifest name. IStringLocalizerFactory
    /// derives the name from ResourcesPath + type namespace; a mismatch silently
    /// falls back to raw keys ("Window_Title" in the title bar). Binding the
    /// ResourceManager directly is immune to that.
    /// </summary>
    private static IStringLocalizer CreateStringsLocalizer()
    {
        const string manifestName = "TerminalV.Localization.Resources.Strings";
        try
        {
            var resources = new System.Resources.ResourceManager(manifestName, typeof(Strings).Assembly);
            // Force-load so a wrong name fails here, not on first lookup.
            _ = resources.GetString("Window_Title");
            return new ResourceLocalizer(resources);
        }
        catch
        {
            // Fall back to the DI localizer (design-time or renamed resources).
            return _factory.Create(typeof(Strings));
        }
    }

    private sealed class ResourceLocalizer(System.Resources.ResourceManager resources) : IStringLocalizer
    {
        public LocalizedString this[string name]
        {
            get
            {
                var value = resources.GetString(name);
                return value is null
                    ? new LocalizedString(name, name, resourceNotFound: true)
                    : new LocalizedString(name, value, resourceNotFound: false);
            }
        }

        public LocalizedString this[string name, params object[] arguments]
        {
            get
            {
                var format = resources.GetString(name);
                if (format is null) return new LocalizedString(name, name, resourceNotFound: true);
                return new LocalizedString(name, string.Format(format, arguments), resourceNotFound: false);
            }
        }

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            resources.GetResourceSet(System.Globalization.CultureInfo.CurrentUICulture, createIfNotExists: true, tryParents: includeParentCultures)
                ?.Cast<System.Collections.DictionaryEntry>()
                .Select(e => new LocalizedString((string)e.Key, e.Value?.ToString() ?? "", resourceNotFound: false))
                ?? [];
    }

    public static string GetString(string key) => Localizer[key];

    public static string GetString(string key, params object[] args) => Localizer[key, args];

    public static void ApplyLanguage(string language)
    {
        try
        {
            var culture = new CultureInfo(language);
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
        }
    }

    public static IStringLocalizer CreateLocalizer(Type type) => Factory.Create(type);

    public static IStringLocalizer<T> CreateLocalizer<T>() where T : class => Provider.GetRequiredService<IStringLocalizer<T>>();

    // For testing / resetting
    internal static void Reset()
    {
        if (_provider is IDisposable d) d.Dispose();
        _provider = null;
        _factory = null;
        _localizer = null;
    }
}

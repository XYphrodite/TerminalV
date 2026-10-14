using System.Globalization;
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
        _localizer = _factory.Create(typeof(Strings));

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
        _localizer = _factory.Create(typeof(Strings));
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

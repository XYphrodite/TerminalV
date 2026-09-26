using System.Windows.Markup;
using Microsoft.Extensions.Localization;

namespace TerminalV.Localization;

/// <summary>
/// WPF MarkupExtension for IStringLocalizer-based localization in XAML.
/// Usage: Text="{loc:Translate Key}" or ToolTip="{loc:Translate Key}"
/// Falls back to the key itself when the resource is missing.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public class TranslateExtension : MarkupExtension
{
    public string Key { get; set; }

    public TranslateExtension(string key)
    {
        Key = key;
    }

    public TranslateExtension()
    {
        Key = string.Empty;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrEmpty(Key))
            return string.Empty;

        try
        {
            var localizer = LocalizationService.Localizer;
            var result = localizer[Key];
            // Return key if resource not found to make missing translations visible.
            return result.ResourceNotFound ? Key : result.Value;
        }
        catch (InvalidOperationException)
        {
            // Localization not yet initialized (designer); fallback to key.
            return Key;
        }
    }
}

/// <summary>
/// Helper for code-behind and non-XAML localization via IStringLocalizer.
/// </summary>
public static class LocalizerHelper
{
    public static string Get(string key) => LocalizationService.GetString(key);

    public static string Get(string key, params object[] args) => LocalizationService.GetString(key, args);

    public static LocalizedString GetLocalized(string key) => LocalizationService.Localizer[key];
}

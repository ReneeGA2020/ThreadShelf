using System.Globalization;
using System.Text.Json;

namespace ThreadShelf;

public static partial class UiText
{
    public const string SystemLanguage = "system";
    public const string EnglishLanguage = "en-US";
    public const string SimplifiedChineseLanguage = "zh-CN";

    private static readonly CultureInfo DetectedSystemCulture = CultureInfo.CurrentUICulture;
    private static CultureInfo _currentCulture = ResolveCulture(SystemLanguage);

    public static CultureInfo CurrentCulture => _currentCulture;

    public static void ApplyLanguage(string? preference, CultureInfo? systemCulture = null)
    {
        _currentCulture = ResolveCulture(preference, systemCulture);
        CultureInfo.CurrentCulture = _currentCulture;
        CultureInfo.CurrentUICulture = _currentCulture;
    }

    public static CultureInfo ResolveCulture(string? preference, CultureInfo? systemCulture = null)
    {
        var normalized = NormalizeLanguagePreference(preference);
        if (normalized == EnglishLanguage || normalized == SimplifiedChineseLanguage)
        {
            return CultureInfo.GetCultureInfo(normalized);
        }

        var detected = systemCulture ?? DetectedSystemCulture;
        return detected.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo(SimplifiedChineseLanguage)
            : CultureInfo.GetCultureInfo(EnglishLanguage);
    }

    public static string NormalizeLanguagePreference(string? preference) =>
        preference?.Trim() switch
        {
            EnglishLanguage => EnglishLanguage,
            SimplifiedChineseLanguage => SimplifiedChineseLanguage,
            _ => SystemLanguage
        };

    public static string Get(string key, params object?[] args) =>
        Get(key, _currentCulture, args);

    public static string Get(string key, CultureInfo culture, params object?[] args)
    {
        var resources = culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? SimplifiedChinese
            : English;
        if (!resources.TryGetValue(key, out var format)
            && !English.TryGetValue(key, out format))
        {
            return key;
        }

        return args.Length == 0 ? format : string.Format(culture, format, args);
    }

    public static IReadOnlyCollection<string> ResourceKeys(string languagePreference) =>
        (NormalizeLanguagePreference(languagePreference) == SimplifiedChineseLanguage
            ? SimplifiedChinese
            : English).Keys.ToArray();
}

public sealed class AppPreferenceStore
{
    public string Path { get; }

    public AppPreferenceStore(string codexHome)
    {
        Path = System.IO.Path.Combine(codexHome, "threadshelf", "preferences.json");
    }

    public string LoadLanguagePreference()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return UiText.SystemLanguage;
            }

            using var stream = File.OpenRead(Path);
            var preferences = JsonSerializer.Deserialize(
                stream,
                ThreadShelfJsonContext.Default.AppPreferences);
            return UiText.NormalizeLanguagePreference(preferences?.Language);
        }
        catch (IOException)
        {
            return UiText.SystemLanguage;
        }
        catch (UnauthorizedAccessException)
        {
            return UiText.SystemLanguage;
        }
        catch (JsonException)
        {
            return UiText.SystemLanguage;
        }
    }

    public void SaveLanguagePreference(string preference)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{Path}.tmp";
        using (var stream = File.Create(tempPath))
        {
            JsonSerializer.Serialize(
                stream,
                new AppPreferences { Language = UiText.NormalizeLanguagePreference(preference) },
                ThreadShelfJsonContext.Default.AppPreferences);
        }

        File.Move(tempPath, Path, overwrite: true);
    }
}

internal sealed record AppPreferences
{
    public string Language { get; init; } = UiText.SystemLanguage;
}

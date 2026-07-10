using System.Globalization;

using ThreadShelf;

namespace ThreadShelf.Tests;

public sealed class UiTextTests
{
    [Fact]
    public void EnglishAndChineseResources_HaveIdenticalKeys()
    {
        var english = UiText.ResourceKeys(UiText.EnglishLanguage).Order(StringComparer.Ordinal).ToArray();
        var chinese = UiText.ResourceKeys(UiText.SimplifiedChineseLanguage).Order(StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(english);
        Assert.Equal(english, chinese);
    }

    [Theory]
    [InlineData("zh-Hans-CN", "zh-CN")]
    [InlineData("en-GB", "en-US")]
    [InlineData("fr-FR", "en-US")]
    public void ResolveCulture_SystemPreferenceUsesChineseOrEnglishFallback(string systemName, string expected)
    {
        var culture = UiText.ResolveCulture(UiText.SystemLanguage, CultureInfo.GetCultureInfo(systemName));

        Assert.Equal(expected, culture.Name);
    }

    [Fact]
    public void PreferenceStore_PersistsLanguageWithoutWritingThreadSidecar()
    {
        var codexHome = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"threadshelf-localization-{Guid.NewGuid():N}");
        try
        {
            var store = new AppPreferenceStore(codexHome);
            store.SaveLanguagePreference(UiText.SimplifiedChineseLanguage);

            var reloaded = new AppPreferenceStore(codexHome);
            Assert.Equal(UiText.SimplifiedChineseLanguage, reloaded.LoadLanguagePreference());
            Assert.False(File.Exists(System.IO.Path.Combine(codexHome, "threadshelf", "threadshelf.json")));
        }
        finally
        {
            if (Directory.Exists(codexHome))
            {
                Directory.Delete(codexHome, recursive: true);
            }
        }
    }

    [Fact]
    public void Get_FormatsNumbersUsingSelectedCulture()
    {
        var english = UiText.Get("ShownThreads", CultureInfo.GetCultureInfo("en-US"), 1234);
        var chinese = UiText.Get("ShownThreads", CultureInfo.GetCultureInfo("zh-CN"), 1234);

        Assert.Equal("1,234 shown", english);
        Assert.Equal("显示 1,234 个", chinese);
    }
}

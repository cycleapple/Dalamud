using System.IO;
using System.Reflection;
using Xunit;

namespace Dalamud.Test
{
    public class LocalizationTests
    {
        private readonly Localization localization;
        private string currentLangCode = "en";

        public LocalizationTests()
        {
            var workingDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            this.localization = new Localization(workingDir, "dalamud_");
            this.localization.LocalizationChanged += code => this.currentLangCode = code;
        }

        [Theory]
        [InlineData("tw", "zh-Hant")]
        [InlineData("tc", "zh-Hant")]
        [InlineData("zh", "zh-Hans")]
        [InlineData("ja", "ja")]
        [InlineData("invalid culture!", "")]
        public void GetCultureInfoFromLangCode_MapsRegionalCultures(string language, string expected)
        {
            Assert.Equal(expected, Localization.GetCultureInfoFromLangCode(language).Name);
        }

        [Theory]
        [InlineData(null, Common.ClientLanguage.TraditionalChinese, "tw")]
        [InlineData("tc", Common.ClientLanguage.English, "tw")]
        [InlineData("en", Common.ClientLanguage.TraditionalChinese, "en")]
        [InlineData(null, Common.ClientLanguage.English, null)]
        public void ResolveAssetLanguage_UsesTaiwanDefaultUnlessOverridden(string? language, Common.ClientLanguage clientLanguage, string? expected)
        {
            Assert.Equal(expected, Localization.ResolveAssetLanguage(language, clientLanguage));
        }

        [Theory]
        [InlineData("zh-TW", "tw")]
        [InlineData("zh-CN", "zh")]
        [InlineData("ja-JP", "ja")]
        [InlineData("en-US", "en")]
        public void GetUiLanguage_RecognizesTraditionalChineseCulture(string culture, string expected)
        {
            Assert.Equal(expected, Localization.GetUiLanguage(System.Globalization.CultureInfo.GetCultureInfo(culture)));
        }

        [Fact]
        public void SetupWithFallbacks_EventInvoked()
        {
            this.localization.SetupWithFallbacks();
            Assert.Equal("en", this.currentLangCode);
        }
    }
}

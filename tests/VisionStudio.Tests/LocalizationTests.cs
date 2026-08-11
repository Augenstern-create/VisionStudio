using VisionStudio.App;
using VisionStudio.Operators.OpenCv;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VisionStudio.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void Resources_DiscoverLanguagesAndOverrideAmbiguousOperatorNames()
    {
        var localization = new UiLocalization();
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        Assert.Contains(localization.AvailableLanguages, language => language.Code == "zh-CN" && language.DisplayName == "中文");
        Assert.Contains(localization.AvailableLanguages, language => language.Code == "en-US" && language.DisplayName == "English");

        registry.TryGetDescriptor("Edge.Canny", out var canny);
        registry.TryGetDescriptor("Edge.Sobel", out var sobel);
        registry.TryGetDescriptor("Edge.Scharr", out var scharr);
        registry.TryGetDescriptor("Edge.Laplacian", out var laplacian);
        registry.TryGetDescriptor("Contour.Moments", out var moments);

        localization.SetLanguage("zh-CN");
        Assert.Equal(new[] { "坎尼边缘检测", "索贝尔方向梯度", "沙尔精确梯度", "拉普拉斯边缘检测" },
            new[] { canny, sobel, scharr, laplacian }.Select(localization.OperatorName));
        Assert.Equal("轮廓矩特征", localization.OperatorName(moments));
        Assert.Contains("不是矩形", localization.OperatorDescription(moments));

        localization.SetLanguage("en-US");
        Assert.Equal("Canny Edge Detection", localization.OperatorName(canny));
        Assert.Equal("Sobel Directional Gradient", localization.OperatorName(sobel));
        Assert.Contains("second-order derivative", localization.OperatorDescription(laplacian));
        Assert.Equal("Interface: Edge.Canny", localization.Format("Property.Interface", canny.Type));
    }

    [Fact]
    public void EnglishResource_CoversChineseKeysAndContainsNoHanCharacters()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Resources");
        var chinese = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(directory, "Strings.zh-CN.json")))!;
        var english = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(directory, "Strings.en-US.json")))!;
        Assert.Empty(chinese.Keys.Except(english.Keys, StringComparer.Ordinal));
        Assert.All(english, pair => Assert.DoesNotMatch(new Regex("[\\u3400-\\u9fff]"), pair.Value));
    }
}

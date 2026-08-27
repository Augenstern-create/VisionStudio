using System.Globalization;
using System.Text.Json;
using VisionStudio.Core;

namespace VisionStudio.App;

public sealed record LanguageOption(string Code, string DisplayName) {
  public override string ToString() {
    return DisplayName;
  }
}

public sealed class UiLocalization {
  private Dictionary<string, string> _strings = new(StringComparer.Ordinal);

  public UiLocalization() {
    AvailableLanguages = DiscoverLanguages();
    string? configured = Environment.GetEnvironmentVariable("VISIONSTUDIO_UI_LANGUAGE");
    string preferred = string.IsNullOrWhiteSpace(configured) ? "zh-CN" : configured;
    SetLanguage(AvailableLanguages.Any(x => x.Code == preferred) ? preferred : AvailableLanguages[0].Code, notify: false);
  }

  public event EventHandler? LanguageChanged;
  public IReadOnlyList<LanguageOption> AvailableLanguages { get; }
  public string CurrentLanguage { get; private set; } = "zh-CN";
  public bool IsChinese => CurrentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

  public string Text(string key) {
    return _strings.TryGetValue(key, out string? value) ? value : key;
  }

  public string Format(string key, params object[] args) {
    return string.Format(CultureInfo.CurrentCulture, Text(key), args);
  }

  public string OperatorName(OperatorDescriptor descriptor) {
    return TextOrFallback($"Operator.{descriptor.Type}.Name", IsChinese ? descriptor.DisplayName : descriptor.EnglishName ?? descriptor.Type);
  }

  public string OperatorDescription(OperatorDescriptor descriptor) {
    return TextOrFallback($"Operator.{descriptor.Type}.Description", IsChinese ? descriptor.Description ?? "" : descriptor.EnglishDescription ?? descriptor.Description ?? "");
  }

  public string CategoryName(string category) {
    return TextOrFallback($"Category.{category}", category);
  }

  public void SetLanguage(string language, bool notify = true) {
    string path = ResourcePath(language);
    if (!File.Exists(path)) {
      throw new FileNotFoundException($"Language resource '{language}' was not found.", path);
    }

    _strings = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
        ?? throw new JsonException($"Language resource '{language}' is empty.");
    CurrentLanguage = language;
    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
    if (notify) {
      LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
  }

  private string TextOrFallback(string key, string fallback) {
    return _strings.TryGetValue(key, out string? value) ? value : fallback;
  }

  private static IReadOnlyList<LanguageOption> DiscoverLanguages() {
    string directory = Path.Combine(AppContext.BaseDirectory, "Resources");
    var result = new List<LanguageOption>();
    if (Directory.Exists(directory)) {
      foreach (string? path in Directory.GetFiles(directory, "Strings.*.json").OrderBy(path => path, StringComparer.Ordinal)) {
        string code = Path.GetFileName(path)["Strings.".Length..^".json".Length];
        try {
          var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
          result.Add(new LanguageOption(code, values?.GetValueOrDefault("Language.DisplayName") ?? code));
        } catch (JsonException) { }
      }
    }

    if (result.Count == 0) {
      throw new InvalidOperationException($"No language resources were found in '{directory}'.");
    }

    return result;
  }

  private static string ResourcePath(string language) {
    return Path.Combine(AppContext.BaseDirectory, "Resources", $"Strings.{language}.json");
  }
}

using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace VisionStudio.App;

public enum UiThemeKind { Dark, Light, Gradient }

public sealed class UiThemeManager {
  private readonly string _settingsPath = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisionStudio", "theme.txt");

  public UiThemeManager() {
    string? configured = Environment.GetEnvironmentVariable("VISIONSTUDIO_UI_THEME");
    string saved = File.Exists(_settingsPath) ? File.ReadAllText(_settingsPath).Trim() : "";
    Current = Parse(string.IsNullOrWhiteSpace(configured) ? saved : configured);
  }

  public UiThemeKind Current { get; private set; }

  public void Apply(Application application, UiThemeKind theme, bool persist = true) {
    Current = theme;
    application.RequestedThemeVariant = theme == UiThemeKind.Light ? ThemeVariant.Light : ThemeVariant.Dark;
    var colors = theme switch {
      UiThemeKind.Light => LightColors,
      UiThemeKind.Gradient => GradientColors,
      _ => DarkColors
    };
    foreach (var (key, value) in colors) {
      application.Resources[key] = value;
    }

    application.Resources["WorkbenchBrush"] = theme == UiThemeKind.Gradient
        ? Gradient("#171927", "#202A42") : colors["WorkbenchBrush"];
    application.Resources["AppBackgroundBrush"] = theme == UiThemeKind.Gradient
        ? Gradient("#141621", "#252A3D") : colors["AppBackgroundBrush"];
    application.Resources["TitleBarBrush"] = theme == UiThemeKind.Gradient
        ? Gradient("#171927", "#252C46") : colors["TitleBarBrush"];

    if (!persist) {
      return;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
    File.WriteAllText(_settingsPath, theme.ToString());
  }

  private static UiThemeKind Parse(string value) {
    return Enum.TryParse<UiThemeKind>(value, true, out var theme) ? theme : UiThemeKind.Dark;
  }

  private static LinearGradientBrush Gradient(string start, string end) {
    return new() {
      StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
      EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
      GradientStops = [new(Color.Parse(start), 0), new(Color.Parse(end), 1)]
    };
  }

  private static Dictionary<string, IBrush> Brushes(params (string Key, string Color)[] values) {
    return values.ToDictionary(item => item.Key, item => (IBrush)new SolidColorBrush(Color.Parse(item.Color)));
  }

  private static readonly Dictionary<string, IBrush> DarkColors = Brushes(
      ("AppBackgroundBrush", "#181818"), ("WorkbenchBrush", "#1E1E1E"), ("SideBarBrush", "#252526"), ("ActivityBarBrush", "#181818"),
      ("ActivityBarBorderBrush", "#3C3C3C"), ("ActivitySelectionBrush", "#007ACC"), ("SideBarHeaderBrush", "#252526"),
      ("PanelBrush", "#202020"), ("TitleBarBrush", "#181818"), ("BorderBrush", "#3C3C3C"),
      ("SubtleBorderBrush", "#2B2B2B"), ("PrimaryBrush", "#007ACC"), ("PrimaryHoverBrush", "#1C8DCE"),
      ("TextBrush", "#D4D4D4"), ("MutedTextBrush", "#969696"), ("HoverBrush", "#2A2D2E"),
      ("SelectionBrush", "#37373D"), ("ControlBrush", "#2D2D30"), ("ControlHoverBrush", "#3A3A3D"),
      ("ControlPressedBrush", "#454549"), ("ControlBorderBrush", "#454545"), ("InputBrush", "#3C3C3C"),
      ("ProjectCardBrush", "#202020"), ("PreviewBrush", "#181818"), ("HeaderBrush", "#252526"),
      ("InspectorCardBrush", "#202020"), ("EmptyIconBrush", "#5A5A5A"), ("StatusBarBrush", "#007ACC"),
      ("FeatureOneBrush", "#9CDCFE"), ("FeatureTwoBrush", "#B5CEA8"), ("FeatureThreeBrush", "#CE9178"));

  private static readonly Dictionary<string, IBrush> LightColors = Brushes(
      ("AppBackgroundBrush", "#F3F3F3"), ("WorkbenchBrush", "#FFFFFF"), ("SideBarBrush", "#F3F3F3"), ("ActivityBarBrush", "#2C2C2C"),
      ("ActivityBarBorderBrush", "#C8C8C8"), ("ActivitySelectionBrush", "#0067B8"), ("SideBarHeaderBrush", "#F3F3F3"),
      ("PanelBrush", "#FFFFFF"), ("TitleBarBrush", "#DDDDDD"), ("BorderBrush", "#C8C8C8"),
      ("SubtleBorderBrush", "#DDDDDD"), ("PrimaryBrush", "#0067B8"), ("PrimaryHoverBrush", "#005A9E"),
      ("TextBrush", "#1F1F1F"), ("MutedTextBrush", "#616161"), ("HoverBrush", "#E5E5E5"),
      ("SelectionBrush", "#D6EBFF"), ("ControlBrush", "#FFFFFF"), ("ControlHoverBrush", "#E9E9E9"),
      ("ControlPressedBrush", "#DADADA"), ("ControlBorderBrush", "#B8B8B8"), ("InputBrush", "#FFFFFF"),
      ("ProjectCardBrush", "#FFFFFF"), ("PreviewBrush", "#F7F7F7"), ("HeaderBrush", "#F3F3F3"),
      ("InspectorCardBrush", "#FAFAFA"), ("EmptyIconBrush", "#8A8A8A"), ("StatusBarBrush", "#0078D4"),
      ("FeatureOneBrush", "#0070C1"), ("FeatureTwoBrush", "#397300"), ("FeatureThreeBrush", "#A33A00"));

  private static readonly Dictionary<string, IBrush> GradientColors = Brushes(
      ("AppBackgroundBrush", "#141621"), ("WorkbenchBrush", "#1B2032"), ("SideBarBrush", "#202436"), ("ActivityBarBrush", "#171927"),
      ("ActivityBarBorderBrush", "#46506C"), ("ActivitySelectionBrush", "#6C63FF"), ("SideBarHeaderBrush", "#202436"),
      ("PanelBrush", "#1D2233"), ("TitleBarBrush", "#171927"), ("BorderBrush", "#46506C"),
      ("SubtleBorderBrush", "#30384E"), ("PrimaryBrush", "#6C63FF"), ("PrimaryHoverBrush", "#817AFF"),
      ("TextBrush", "#E2E5F2"), ("MutedTextBrush", "#9BA3BD"), ("HoverBrush", "#30364B"),
      ("SelectionBrush", "#3A405D"), ("ControlBrush", "#282D42"), ("ControlHoverBrush", "#353C55"),
      ("ControlPressedBrush", "#414965"), ("ControlBorderBrush", "#4A5370"), ("InputBrush", "#292F45"),
      ("ProjectCardBrush", "#202638"), ("PreviewBrush", "#151824"), ("HeaderBrush", "#22283A"),
      ("InspectorCardBrush", "#202638"), ("EmptyIconBrush", "#707996"), ("StatusBarBrush", "#5147C7"),
      ("FeatureOneBrush", "#80CAFF"), ("FeatureTwoBrush", "#B8E6A3"), ("FeatureThreeBrush", "#FFAE8A"));
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Emgu.CV;
using VisionStudio.Ai;
using VisionStudio.Ai.Abstractions;
using VisionStudio.CodeGen;
using VisionStudio.Core;
using VisionStudio.Operators.OpenCv;

namespace VisionStudio.App;

public sealed partial class MainWindow : Window {
  private void LanguageMenuItem_Click(object? sender, RoutedEventArgs e) {
    if (sender is MenuItem { Tag: string language } && language != _localization.CurrentLanguage) {
      _localization.SetLanguage(language);
    }
  }

  private void ThemeMenuItem_Click(object? sender, RoutedEventArgs e) {
    if (sender is not MenuItem { Tag: string value } || !Enum.TryParse<UiThemeKind>(value, true, out var theme)) {
      return;
    }

    _themes.Apply(Application.Current!, theme);
    Canvas.ApplyTheme(theme);
    SyncThemeChecks();
    BuildPalette();
    ShowProperties(Canvas.SelectedNode);
  }

  private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e) {
    if (sender is not Control titleBar || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) {
      return;
    }

    for (var control = e.Source as Control; control is not null && control != titleBar; control = control.Parent as Control) {
      if (control is Button or TextBox or Menu or MenuItem or ComboBox) {
        return;
      }
    }

    BeginMoveDrag(e);
    e.Handled = true;
  }

  private void ApplyLanguage() {
    WelcomeSubtitleText.Text = _localization.Text("Welcome.Subtitle");
    WelcomeTabText.Text = _localization.Text("Welcome.Tab");
    WelcomeSectionText.Text = _localization.Text("Welcome.Section");
    WelcomeFeatureOneText.Text = _localization.Text("Welcome.Feature1");
    WelcomeFeatureTwoText.Text = _localization.Text("Welcome.Feature2");
    WelcomeFeatureThreeText.Text = _localization.Text("Welcome.Feature3");
    NewProjectTitleText.Text = _localization.Text("Welcome.NewProject");
    NewProjectDescriptionText.Text = _localization.Text("Welcome.NewProjectDescription");
    OpenProjectTitleText.Text = _localization.Text("Welcome.OpenProject");
    OpenProjectDescriptionText.Text = _localization.Text("Welcome.OpenProjectDescription");
    WelcomeStatusText.Text = _localization.Text("Welcome.InitialStatus");
    FileMenuItem.Header = _localization.Text("Toolbar.File");
    QuickActionsMenuItem.Header = _localization.Text("Toolbar.QuickActions");
    ViewMenuItem.Header = _localization.Text("Toolbar.View");
    HelpMenuItem.Header = _localization.Text("Toolbar.Help");
    SaveMenuItem.Header = _localization.Text("Toolbar.Save");
    ExportMenuItem.Header = _localization.Text("Toolbar.Export");
    BatchRunMenuItem.Header = _localization.Text("Toolbar.BatchRun");
    CancelRunButton.Content = _localization.Text("Toolbar.Cancel");
    SettingsMenuItem.Header = _localization.Text("Toolbar.Settings");
    ExecutionHistoryMenuItem.Header = _localization.Text("Toolbar.ExecutionHistory");
    CloseFolderMenuItem.Header = _localization.Text("Toolbar.CloseFolder");
    UndoMenuItem.Header = _localization.Text("Toolbar.Undo");
    AutoLayoutMenuItem.Header = _localization.Text("Toolbar.AutoLayout");
    AboutMenuItem.Header = _localization.Text("Toolbar.About");
    WelcomeViewMenuItem.Header = _localization.Text("Toolbar.View");
    WelcomeLanguageMenuItem.Header = LanguageMenuItem.Header = _localization.Text("View.Language");
    WelcomeThemeMenuItem.Header = ThemeMenuItem.Header = _localization.Text("View.Theme");
    WelcomeChineseMenuItem.Header = ChineseMenuItem.Header = _localization.Text("Language.Chinese");
    WelcomeEnglishMenuItem.Header = EnglishMenuItem.Header = _localization.Text("Language.English");
    WelcomeDarkThemeMenuItem.Header = DarkThemeMenuItem.Header = _localization.Text("Theme.Dark");
    WelcomeLightThemeMenuItem.Header = LightThemeMenuItem.Header = _localization.Text("Theme.Light");
    WelcomeGradientThemeMenuItem.Header = GradientThemeMenuItem.Header = _localization.Text("Theme.Gradient");
    ChineseMenuItem.IsChecked = WelcomeChineseMenuItem.IsChecked = _localization.IsChinese;
    EnglishMenuItem.IsChecked = WelcomeEnglishMenuItem.IsChecked = !_localization.IsChinese;
    SyncThemeChecks();
    HistoryMenuItem.Header = _localization.Text("Panel.History");
    AiSidebarTitleText.Text = _localization.Text("Ai.Title");
    AiPromptTextBox.PlaceholderText = _localization.Text("Ai.Prompt");
    AutomationProperties.SetName(AiPromptTextBox, _localization.Text("Ai.Prompt"));
    AutomationProperties.SetName(AiConversationScroll, _localization.Text("Ai.Conversation"));
    AiSendButton.Content = _localization.Text("Ai.Send");
    AiStopButton.Content = _localization.Text("Ai.Stop");
    AiApplyButton.Content = _localization.Text("Ai.Apply");
    AiModeHintText.Text = _localization.Text("Ai.SuggestMode");
    Canvas.SelectDownstreamText = _localization.Text("Canvas.SelectDownstream");
    Canvas.LayoutDownstreamText = _localization.Text("Canvas.LayoutDownstream");
    Canvas.DeleteDownstreamText = _localization.Text("Canvas.DeleteDownstream");
    Canvas.DeleteConnectionText = _localization.Text("Canvas.DeleteConnection");
    RefreshAiImageReferences();
    ToolTip.SetTip(OperatorsActivityButton, _localization.Text("Panel.Operators"));
    ToolTip.SetTip(AiActivityButton, _localization.Text("Ai.Title"));
    ToolTip.SetTip(ActivitySettingsButton, _localization.Text("Toolbar.Settings"));
    OperatorsTitleText.Text = _localization.Text("Panel.Operators");
    ResultTitleText.Text = _localization.Text("Panel.Result");
    PreviewFitButton.Content = _localization.Text("Panel.Fit");
    PropertiesTitleText.Text = _localization.Text("Panel.Properties");
    HistoryTitleText.Text = _localization.Text("Panel.History");
    OperatorSearchBox.PlaceholderText = _localization.Text("Panel.SearchOperators");
    AutomationProperties.SetName(OperatorSearchBox, _localization.Text("Panel.SearchOperators"));
    PreviewEmptyText.Text = _localization.Text("Panel.EmptyPreview");
    PropertyEmptyText.Text = _localization.Text("Panel.EmptyProperties");
    if (Canvas.SelectedNode is null) {
      StatusText.Text = _localization.Text("Panel.InitialResult");
    }

    AutomationProperties.SetName(Canvas, _localization.Text("Canvas.AutomationName"));
    WorkflowLabelText.Text = _localization.Text("Canvas.Workflow");
    BuildPalette();
    Canvas.Refresh();
    ShowProperties(Canvas.SelectedNode);
    if (_project is not null) {
      _ = LoadOperationHistoryAsync();
    }
  }

  private void SyncThemeChecks() {
    DarkThemeMenuItem.IsChecked = WelcomeDarkThemeMenuItem.IsChecked = _themes.Current == UiThemeKind.Dark;
    LightThemeMenuItem.IsChecked = WelcomeLightThemeMenuItem.IsChecked = _themes.Current == UiThemeKind.Light;
    GradientThemeMenuItem.IsChecked = WelcomeGradientThemeMenuItem.IsChecked = _themes.Current == UiThemeKind.Gradient;
  }

  private static Avalonia.Media.IBrush ResourceBrush(string key) {
    return Application.Current?.Resources[key] as Avalonia.Media.IBrush ?? Avalonia.Media.Brushes.Transparent;
  }

}

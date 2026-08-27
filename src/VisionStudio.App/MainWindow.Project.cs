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
  private async void NewProject_Click(object? sender, RoutedEventArgs e) {
    var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = _localization.Text("Picker.ProjectLocation"), AllowMultiple = false });
    string? parent = folders.FirstOrDefault()?.TryGetLocalPath();
    if (parent is null) {
      return;
    }

    string? name = await AskProjectNameAsync();
    if (string.IsNullOrWhiteSpace(name)) {
      return;
    }

    foreach (char invalid in Path.GetInvalidFileNameChars()) {
      name = name.Replace(invalid, '_');
    }

    string path = Path.Combine(parent, name);
    try {
      _project = await _projects.CreateAsync(path, name);
      await new RecentProjectCatalog().RecordAsync(_project);
      _workflow = await _projects.LoadWorkflowAsync(_project);
      ResetUndoHistory();
      await SetWorkflowAsync();
      EnterEditor();
      await LoadOperationHistoryAsync();
      RecordOperation("创建项目", _project.RootPath);
      Status(_localization.Text("Status.ProjectCreated"));
    } catch (Exception ex) { WelcomeStatus(UserError(ex)); }
  }

  private async void OpenProject_Click(object? sender, RoutedEventArgs e) {
    var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = _localization.Text("Picker.OpenProject"),
      AllowMultiple = false,
      FileTypeFilter =
        [
            new FilePickerFileType(_localization.Text("Picker.ProjectFiles")) { Patterns = [$"*{ProjectService.ProjectExtension}"] }
        ]
    });
    string? path = files.FirstOrDefault()?.TryGetLocalPath();
    if (path is null) {
      return;
    }

    await LoadProjectAsync(path);
  }

  private async void CloseFolder_Click(object? sender, RoutedEventArgs e) {
    if (_dirty && !await SaveAsync()) {
      return;
    }

    CancelPreview();
    _autosaveCancellation?.Cancel();
    // Allow the native Invoke/Click provider to finish before its source button
    // menu is removed from the visible automation and visual trees.
    await Task.Delay(50);
    WelcomeView.IsVisible = true;
    EditorView.IsVisible = false;
    ReleasePreviewsAfterNavigation();
    _project = null;
    await DetachAutomationSessionAsync();
    _workflow = new WorkflowDocument();
    Canvas.Workflow = _workflow;
    Canvas.Refresh();
    ResetUndoHistory();
    _operationItems.Clear();
    HistoryMenuItem.IsChecked = false;
    HistoryPanel.IsVisible = false;
    ProjectText.Text = "";
    UpdateTitle();
    WelcomeView.Focus();
    WelcomeStatus(_localization.Text("Status.WelcomeAgain"));
  }

  private async void About_Click(object? sender, RoutedEventArgs e) {
    string version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";
    var close = new Button { Content = _localization.Text("About.Close"), Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Right };
    var dialog = new Window {
      Title = _localization.Text("About.Title"),
      Width = 470,
      Height = 300,
      CanResize = false,
      WindowStartupLocation = WindowStartupLocation.CenterOwner,
      Content = new Grid {
        RowDefinitions = new RowDefinitions("*,Auto"),
        Margin = new Thickness(28),
        Children =
            {
                    new StackPanel
                    {
                        Spacing = 12,
                        Children =
                        {
                            new Border
                            {
                                Width = 52, Height = 52, CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Left,
                                Background = ResourceBrush("PrimaryBrush"),
                                Child = new TextBlock { Text = "V", FontSize = 27, FontWeight = Avalonia.Media.FontWeight.Bold, Foreground = Avalonia.Media.Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                            },
                            new TextBlock { Text = "Vision Studio", FontSize = 24, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                            new TextBlock { Text = _localization.Text("About.Description"), Foreground = ResourceBrush("MutedTextBrush") },
                            new TextBlock { Text = _localization.Format("About.Version", version), FontSize = 12, Opacity = .72 },
                            new TextBlock { Text = _localization.Text("About.Technology"), FontSize = 12, Opacity = .72, TextWrapping = Avalonia.Media.TextWrapping.Wrap }
                        }
                    },
                    close
                }
      }
    };
    Grid.SetRow(close, 1);
    close.Click += (_, _) => dialog.Close();
    await dialog.ShowDialog(this);
  }

  private async void Settings_Click(object? sender, RoutedEventArgs e) {
    bool changed = await new SettingsWindow(_localization.IsChinese).ShowDialog<bool>(this);
    if (changed) {
      await LoadAiModelsAsync();
    }
  }

  private async Task LoadProjectAsync(string path) {
    try {
      _project = await _projects.OpenAsync(path);
      await new RecentProjectCatalog().RecordAsync(_project);
      _workflow = await _projects.LoadWorkflowAsync(_project);
      ResetUndoHistory();
      await SetWorkflowAsync();
      EnterEditor();
      await LoadOperationHistoryAsync();
      RecordOperation("打开项目", _project.RootPath);
      Status(_localization.Text("Status.ProjectOpened"));
    } catch (Exception ex) { WelcomeStatus(UserError(ex)); }
  }

  private async void Save_Click(object? sender, RoutedEventArgs e) {
    await SaveAsync();
  }

  private async Task<bool> SaveAsync() {
    if (_project is null) { Status(_localization.Text("Status.ProjectRequired")); return false; }
    try {
      await _projects.SaveWorkflowAsync(_project, _workflow);
      _dirty = false;
      UpdateTitle();
      Status(_localization.Text("Status.ProjectSaved"));
      return true;
    } catch (Exception ex) { Status(UserError(ex)); return false; }
  }

  private async void Export_Click(object? sender, RoutedEventArgs e) {
    if (_project is null) { Status(_localization.Text("Status.ProjectRequired")); return; }
    try {
      string path = ProjectPaths.ResolveInside(_project.RootPath, "output/GeneratedVisionAlgorithm.cs");
      string outputDirectory = Path.GetDirectoryName(path)!;
      Directory.CreateDirectory(outputDirectory);
      await File.WriteAllTextAsync(path, new CSharpWorkflowGenerator(_registry).Generate(
          _workflow,
          targetNodeId: Canvas.SelectedNode?.Id));
      RecordOperation("导出 C#", "output/GeneratedVisionAlgorithm.cs");
      try {
        if (!string.Equals(Environment.GetEnvironmentVariable("VISIONSTUDIO_DISABLE_OPEN_FOLDER"), "1", StringComparison.Ordinal)) {
          Process.Start(new ProcessStartInfo { FileName = outputDirectory, UseShellExecute = true });
        }

        Status(_localization.Format("Status.ExportedOpened", path));
      } catch (Exception openError) { Status(_localization.Format("Status.ExportedOpenFailed", path, UserError(openError))); }
    } catch (Exception ex) { Status(UserError(ex)); }
  }

  private async Task<string?> AskProjectNameAsync() {
    string stamp = DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
    var input = new TextBox { Text = _localization.Format("Dialog.DefaultProjectName", stamp), PlaceholderText = _localization.Text("Dialog.ProjectNamePlaceholder") };
    var ok = new Button { Content = _localization.Text("Dialog.CreateProject"), Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Right };
    var cancel = new Button { Content = _localization.Text("Dialog.Cancel") };
    var dialog = new Window {
      Title = _localization.Text("Dialog.NewProjectTitle"),
      Width = 430,
      Height = 210,
      CanResize = false,
      WindowStartupLocation = WindowStartupLocation.CenterOwner,
      Content = new StackPanel {
        Margin = new Thickness(22),
        Spacing = 12,
        Children =
            {
                    new TextBlock { Text = _localization.Text("Dialog.ProjectName"), FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    new TextBlock { Text = _localization.Text("Dialog.ProjectFolderHint"), Opacity = .65 }, input,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } }
                }
      }
    };
    ok.Click += (_, _) => dialog.Close(input.Text);
    cancel.Click += (_, _) => dialog.Close(null);
    return await dialog.ShowDialog<string?>(this);
  }

  private void UpdateTitle() {
    // The main window uses an integrated VS Code-style title bar. Keep the
    // native title text empty so it cannot overlap the left-aligned menus.
    Title = "";
    if (_project is not null) {
      ProjectText.Text = $"{_project.Project.Name}{(_dirty ? " *" : "")}";
    }
  }

  private void Status(string message) {
    StatusText.Text = message;
  }

  private void WelcomeStatus(string message) {
    WelcomeStatusText.Text = message;
  }

}

using Avalonia.Interactivity;

namespace VisionStudio.App;

public sealed partial class MainWindow {
  private async void BatchRun_Click(object? sender, RoutedEventArgs e) {
    if (_project is null) {
      Status(_localization.Text("Status.ProjectRequired"));
      return;
    }

    var window = new BatchRunWindow(
        _project, _workflow, _registry, Canvas.SelectedNode?.Id, _localization.IsChinese);
    await window.ShowDialog(this);
  }
}

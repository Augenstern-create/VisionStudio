using Avalonia.Interactivity;

namespace VisionStudio.App;

public sealed partial class MainWindow {
  private async void ExecutionHistory_Click(object? sender, RoutedEventArgs e) {
    if (_executionHistory is null) {
      Status(_localization.Text("Status.ProjectRequired"));
      return;
    }

    await new ExecutionHistoryWindow(_executionHistory, _localization.IsChinese).ShowDialog(this);
  }
}

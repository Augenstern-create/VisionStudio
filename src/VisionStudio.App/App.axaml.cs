using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VisionStudio.Core;

namespace VisionStudio.App;

public sealed partial class App : Application {
  public WorkflowAutomationApiHost WorkflowAutomation { get; } = new();

  public override void Initialize() {
    AvaloniaXamlLoader.Load(this);
  }

  public override void OnFrameworkInitializationCompleted() {
    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
      WindowsFileAssociation.EnsureRegistered();
      desktop.MainWindow = new MainWindow(desktop.Args?.FirstOrDefault());
    }
    base.OnFrameworkInitializationCompleted();
  }
}

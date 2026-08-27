using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace VisionStudio.Runtimes.App;

public sealed partial class App : Application {
  public override void Initialize() {
    AvaloniaXamlLoader.Load(this);
  }

  public override void OnFrameworkInitializationCompleted() {
    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
      string? projectPath = ArgumentValue(desktop.Args, "--project");
      desktop.MainWindow = new MainWindow(projectPath);
    }
    base.OnFrameworkInitializationCompleted();
  }

  private static string? ArgumentValue(IReadOnlyList<string>? args, string name) {
    if (args is null) {
      return null;
    }
    for (int index = 0; index < args.Count - 1; index++) {
      if (string.Equals(args[index], name, StringComparison.Ordinal)) {
        return args[index + 1];
      }
    }
    return null;
  }
}

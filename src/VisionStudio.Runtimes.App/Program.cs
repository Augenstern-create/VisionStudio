using Avalonia;

namespace VisionStudio.Runtimes.App;

internal static class Program {
  [STAThread]
  public static int Main(string[] args) {
    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    return 0;
  }

  public static AppBuilder BuildAvaloniaApp() {
    return AppBuilder.Configure<App>()
      .UsePlatformDetect()
      .WithInterFont()
      .LogToTrace();
  }
}

using Microsoft.Extensions.Logging;
using VisionStudio.Web;

namespace VisionStudio.Tests;

public sealed class WebProjectStoreTests {
  [Fact]
  public async Task ListAsync_LogsUnreadableProjectDirectory() {
    using var temp = new TemporaryDirectory();
    string invalidProject = Path.Combine(temp.Path, "broken-project");
    Directory.CreateDirectory(invalidProject);
    await File.WriteAllTextAsync(Path.Combine(invalidProject, "broken.vstudio"), "not-json");
    var logger = new RecordingLogger<WebProjectStore>();
    var store = new WebProjectStore(temp.Path, logger);

    Assert.Empty(await store.ListAsync(CancellationToken.None));
    var (Level, Message) = Assert.Single(logger.Entries);
    Assert.Equal(LogLevel.Warning, Level);
    Assert.Contains("broken-project", Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task GetAsync_CancelledCallerDoesNotCancelSharedLoad() {
    using var temp = new TemporaryDirectory();
    var releaseLoad = new TaskCompletionSource<WebProjectContext>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var context = await new WebProjectStore(Path.Combine(temp.Path, "source"))
        .CreateAsync("Shared", CancellationToken.None);
    var store = new WebProjectStore(
        Path.Combine(temp.Path, "target"),
        logger: null,
        (_, cancellationToken) => {
          Assert.False(cancellationToken.CanBeCanceled);
          return releaseLoad.Task;
        });
    using var firstCancellation = new CancellationTokenSource();

    var first = store.GetAsync(context.Handle.Project.Id, firstCancellation.Token);
    var second = store.GetAsync(context.Handle.Project.Id, CancellationToken.None);
    firstCancellation.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

    releaseLoad.SetResult(context);
    Assert.Same(context, await second);
    Assert.Same(context, await store.GetAsync(context.Handle.Project.Id, CancellationToken.None));
  }

  private sealed class RecordingLogger<T> : ILogger<T> {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull {
      return null;
    }

    public bool IsEnabled(LogLevel logLevel) {
      return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) {
      Entries.Add((logLevel, formatter(state, exception)));
    }
  }
}

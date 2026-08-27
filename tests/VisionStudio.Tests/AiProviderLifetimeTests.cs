using VisionStudio.Ai;
using VisionStudio.Ai.Abstractions;

namespace VisionStudio.Tests;

public sealed class AiProviderLifetimeTests {
  [Fact]
  public async Task GetOrCreateAsync_ReusesProfileAndResetAwaitsDisposal() {
    var provider = new TestProvider("first");
    var lifetime = new AiProviderLifetime();
    var factoryCalls = 0;

    var first = await lifetime.GetOrCreateAsync("profile", () => {
      factoryCalls++;
      return Task.FromResult(new AiProviderHandle(provider, null));
    });
    var second = await lifetime.GetOrCreateAsync("profile", () => {
      factoryCalls++;
      return Task.FromResult(new AiProviderHandle(new TestProvider("unused"), null));
    });

    Assert.Same(first, second);
    Assert.Equal(1, factoryCalls);
    await lifetime.ResetAsync();
    Assert.True(provider.Disposed);
  }

  [Fact]
  public async Task GetOrCreateAsync_SerializesReplacementBehindPreviousDisposal() {
    var releaseDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var previous = new TestProvider("previous", releaseDisposal.Task);
    var replacement = new TestProvider("replacement");
    var lifetime = new AiProviderLifetime();
    await lifetime.GetOrCreateAsync(
        "previous", () => Task.FromResult(new AiProviderHandle(previous, null)));

    var replacementCreated = false;
    var replaceTask = lifetime.GetOrCreateAsync("replacement", () => {
      replacementCreated = true;
      return Task.FromResult(new AiProviderHandle(replacement, null));
    });

    await previous.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert.False(replacementCreated);
    releaseDisposal.SetResult();
    var handle = await replaceTask;

    Assert.True(previous.Disposed);
    Assert.Same(replacement, handle.Provider);
    await lifetime.DisposeAsync();
    Assert.True(replacement.Disposed);
  }

  private sealed class TestProvider(
      string id,
      Task? disposalDelay = null) : IAiProvider {
    public TaskCompletionSource DisposalStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Disposed { get; private set; }
    public string ProviderId => id;
    public AiModelProfile Profile { get; } =
        new(id, id, "model", null, "server");
    public bool SupportsVision => false;

    public Task<AiProviderResponse> SendAsync(
        AiProviderRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public async ValueTask DisposeAsync() {
      DisposalStarted.TrySetResult();
      if (disposalDelay is not null) {
        await disposalDelay;
      }
      Disposed = true;
    }
  }
}

namespace VisionStudio.Ai;

public sealed class AiProviderLifetime : IAsyncDisposable {
  private readonly SemaphoreSlim _gate = new(1, 1);
  private AiProviderHandle? _handle;
  private string? _profileId;
  private bool _disposed;

  public async Task<AiProviderHandle> GetOrCreateAsync(
      string profileId,
      Func<Task<AiProviderHandle>> factory,
      CancellationToken cancellationToken = default) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    await _gate.WaitAsync(cancellationToken);
    try {
      ObjectDisposedException.ThrowIf(_disposed, this);
      if (_handle is not null && string.Equals(_profileId, profileId, StringComparison.Ordinal)) {
        return _handle;
      }

      await ResetCoreAsync();
      var handle = await factory();
      _handle = handle;
      _profileId = profileId;
      return handle;
    } finally {
      _gate.Release();
    }
  }

  public async Task ResetAsync(CancellationToken cancellationToken = default) {
    if (_disposed) {
      return;
    }

    await _gate.WaitAsync(cancellationToken);
    try {
      await ResetCoreAsync();
    } finally {
      _gate.Release();
    }
  }

  public async ValueTask DisposeAsync() {
    if (_disposed) {
      return;
    }

    await _gate.WaitAsync();
    try {
      if (_disposed) {
        return;
      }

      await ResetCoreAsync();
      _disposed = true;
    } finally {
      _gate.Release();
    }
  }

  private async ValueTask ResetCoreAsync() {
    var handle = _handle;
    _handle = null;
    _profileId = null;
    if (handle is not null) {
      await handle.DisposeAsync();
    }
  }
}

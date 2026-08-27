using System.Threading.Channels;

namespace VisionStudio.Runtimes.App;

public enum RuntimeState {
  Ready,
  Starting,
  Running,
  Paused,
  Stopping,
  Stopped,
  Faulted
}

public sealed record RuntimeStatistics(
    long Captured,
    long Processed,
    long Dropped,
    long Errors,
    int QueueDepth,
    int QueueCapacity);

public sealed class RuntimeStateMachine {
  private readonly object _gate = new();
  private RuntimeState _state = RuntimeState.Ready;

  public event EventHandler<RuntimeState>? StateChanged;
  public RuntimeState State {
    get {
      lock (_gate) {
        return _state;
      }
    }
  }

  public void TransitionTo(RuntimeState next) {
    lock (_gate) {
      if (_state == next) {
        return;
      }
      if (!IsAllowed(_state, next)) {
        throw new InvalidOperationException(
            $"Invalid runtime state transition: {_state} -> {next}.");
      }
      _state = next;
    }
    StateChanged?.Invoke(this, next);
  }

  private static bool IsAllowed(RuntimeState current, RuntimeState next) {
    return (current, next) switch {
      (RuntimeState.Ready, RuntimeState.Starting) => true,
      (RuntimeState.Stopped, RuntimeState.Starting) => true,
      (RuntimeState.Faulted, RuntimeState.Starting) => true,
      (RuntimeState.Faulted, RuntimeState.Stopped) => true,
      (RuntimeState.Starting, RuntimeState.Running) => true,
      (RuntimeState.Starting, RuntimeState.Stopping) => true,
      (RuntimeState.Starting, RuntimeState.Faulted) => true,
      (RuntimeState.Running, RuntimeState.Paused) => true,
      (RuntimeState.Running, RuntimeState.Stopping) => true,
      (RuntimeState.Running, RuntimeState.Stopped) => true,
      (RuntimeState.Running, RuntimeState.Faulted) => true,
      (RuntimeState.Paused, RuntimeState.Running) => true,
      (RuntimeState.Paused, RuntimeState.Stopping) => true,
      (RuntimeState.Paused, RuntimeState.Stopped) => true,
      (RuntimeState.Paused, RuntimeState.Faulted) => true,
      (RuntimeState.Stopping, RuntimeState.Stopped) => true,
      (RuntimeState.Stopping, RuntimeState.Faulted) => true,
      _ => false
    };
  }
}

public interface IRuntimeTrigger : IAsyncDisposable {
  ValueTask WaitAsync(CancellationToken cancellationToken);
  bool Fire();
}

public static class RuntimeTriggerFactory {
  public static IRuntimeTrigger Create(RuntimeExecutionSettings settings) {
    return settings.Trigger switch {
      RuntimeTriggerKind.Continuous => new ContinuousRuntimeTrigger(),
      RuntimeTriggerKind.Timer => new TimerRuntimeTrigger(settings.IntervalMs),
      RuntimeTriggerKind.Manual => new ManualRuntimeTrigger(),
      _ => throw new NotSupportedException(
          $"Unsupported runtime trigger '{settings.Trigger}'.")
    };
  }
}

public sealed class ContinuousRuntimeTrigger : IRuntimeTrigger {
  public async ValueTask WaitAsync(CancellationToken cancellationToken) {
    cancellationToken.ThrowIfCancellationRequested();
    await Task.Yield();
    cancellationToken.ThrowIfCancellationRequested();
  }
  public bool Fire() {
    return false;
  }
  public ValueTask DisposeAsync() {
    return ValueTask.CompletedTask;
  }
}

public sealed class TimerRuntimeTrigger(int intervalMs) : IRuntimeTrigger {
  private readonly PeriodicTimer _timer = new(
      TimeSpan.FromMilliseconds(Math.Clamp(intervalMs, 1, 86_400_000)));
  public async ValueTask WaitAsync(CancellationToken cancellationToken) {
    if (!await _timer.WaitForNextTickAsync(cancellationToken)) {
      throw new OperationCanceledException(cancellationToken);
    }
  }
  public bool Fire() {
    return false;
  }
  public ValueTask DisposeAsync() {
    _timer.Dispose();
    return ValueTask.CompletedTask;
  }
}

public sealed class ManualRuntimeTrigger : IRuntimeTrigger {
  private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(
      new BoundedChannelOptions(1) {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
      });
  public async ValueTask WaitAsync(CancellationToken cancellationToken) {
    _ = await _signals.Reader.ReadAsync(cancellationToken);
  }
  public bool Fire() {
    return _signals.Writer.TryWrite(true);
  }
  public ValueTask DisposeAsync() {
    _signals.Writer.TryComplete();
    return ValueTask.CompletedTask;
  }
}

public sealed class ProjectRuntimeController : IAsyncDisposable {
  private readonly RuntimeProjectContext _context;
  private readonly ProjectRuntimeService _runtime;
  private readonly FrameSourceFactory _sources;
  private readonly RuntimeStateMachine _machine = new();
  private readonly AsyncPauseGate _pauseGate = new();
  private CancellationTokenSource? _cancellation;
  private IFrameSource? _source;
  private IRuntimeTrigger? _trigger;
  private Channel<CapturedFrame>? _frames;
  private Task? _pipeline;
  private long _captured;
  private long _processed;
  private long _dropped;
  private long _errors;
  private int _queueDepth;

  public ProjectRuntimeController(
      RuntimeProjectContext context,
      ProjectRuntimeService? runtime = null,
      FrameSourceFactory? sources = null) {
    _context = context;
    _runtime = runtime ?? new ProjectRuntimeService();
    _sources = sources ?? new FrameSourceFactory();
    _machine.StateChanged += (_, state) => StateChanged?.Invoke(this, state);
  }

  public event EventHandler<RuntimeState>? StateChanged;
  public event EventHandler<RuntimeInspectionResult>? ResultProduced;
  public event EventHandler<Exception>? RuntimeError;
  public event EventHandler<RuntimeStatistics>? StatisticsChanged;
  public RuntimeState State => _machine.State;
  public RuntimeStatistics Statistics => Snapshot();

  public async Task StartAsync(CancellationToken cancellationToken = default) {
    if (State is RuntimeState.Running or RuntimeState.Paused
        or RuntimeState.Starting or RuntimeState.Stopping) {
      throw new InvalidOperationException($"Runtime cannot start while {State}.");
    }
    ProjectRuntimeService.Validate(
        _context.Project,
        _context.Workflow,
        _context.Profile,
        _context.Registry);
    ValidateExecution(_context.Profile.Execution);
    _machine.TransitionTo(RuntimeState.Starting);
    ResetStatistics();
    try {
      _source = _sources.Create(
          _context.Profile.Source,
          _context.Project.RootPath);
      _trigger = RuntimeTriggerFactory.Create(_context.Profile.Execution);
      _frames = CreateFrameQueue(_context.Profile.Execution);
      _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      await _source.OpenAsync(_cancellation.Token);
      _pauseGate.Resume();
      _machine.TransitionTo(RuntimeState.Running);
      _pipeline = RunPipelineAsync(_cancellation.Token);
    } catch {
      _machine.TransitionTo(RuntimeState.Faulted);
      await ReleaseResourcesAsync();
      throw;
    }
  }

  public void Pause() {
    _machine.TransitionTo(RuntimeState.Paused);
    _pauseGate.Pause();
  }

  public void Resume() {
    _pauseGate.Resume();
    _machine.TransitionTo(RuntimeState.Running);
  }

  public bool FireTrigger() {
    return State is RuntimeState.Running or RuntimeState.Paused
        && _trigger?.Fire() == true;
  }

  public async Task StopAsync() {
    if (State is RuntimeState.Ready or RuntimeState.Stopped) {
      return;
    }
    if (State == RuntimeState.Faulted) {
      await ReleaseResourcesAsync();
      _machine.TransitionTo(RuntimeState.Stopped);
      return;
    }
    if (State != RuntimeState.Stopping) {
      _machine.TransitionTo(RuntimeState.Stopping);
    }
    _pauseGate.Resume();
    _cancellation?.Cancel();
    Task? pipeline = _pipeline;
    if (pipeline is not null) {
      try {
        await pipeline;
      } catch (OperationCanceledException) {
      }
    }
  }

  public async ValueTask DisposeAsync() {
    await StopAsync();
    await ReleaseResourcesAsync();
  }

  private Channel<CapturedFrame> CreateFrameQueue(
      RuntimeExecutionSettings settings) {
    BoundedChannelFullMode mode = settings.QueueMode switch {
      RuntimeQueueMode.ProcessAll => BoundedChannelFullMode.Wait,
      RuntimeQueueMode.LatestFrame => BoundedChannelFullMode.DropOldest,
      RuntimeQueueMode.DropWhenBusy => BoundedChannelFullMode.DropWrite,
      _ => throw new NotSupportedException()
    };
    return Channel.CreateBounded<CapturedFrame>(
        new BoundedChannelOptions(settings.QueueCapacity) {
          FullMode = mode,
          SingleReader = true,
          SingleWriter = true
        },
        dropped => {
          dropped.Dispose();
          Interlocked.Increment(ref _dropped);
          Interlocked.Decrement(ref _queueDepth);
          PublishStatistics();
        });
  }

  private async Task RunPipelineAsync(CancellationToken cancellationToken) {
    Exception? failure = null;
    try {
      var producer = ProduceFramesAsync(cancellationToken);
      var consumer = ProcessFramesAsync(cancellationToken);
      var completed = await Task.WhenAny(producer, consumer);
      if (completed.IsFaulted) {
        _cancellation?.Cancel();
      }
      await Task.WhenAll(producer, consumer);
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
    } catch (Exception exception) {
      failure = exception;
      Interlocked.Increment(ref _errors);
      RuntimeError?.Invoke(this, exception);
    } finally {
      _frames?.Writer.TryComplete(failure);
      DrainQueue();
      await ReleaseResourcesAsync();
      if (failure is not null) {
        if (State != RuntimeState.Faulted) {
          _machine.TransitionTo(RuntimeState.Faulted);
        }
      } else if (State is RuntimeState.Running or RuntimeState.Paused
          or RuntimeState.Stopping) {
        _machine.TransitionTo(RuntimeState.Stopped);
      }
      PublishStatistics();
    }
  }

  private async Task ProduceFramesAsync(CancellationToken cancellationToken) {
    var trigger = _trigger ?? throw new InvalidOperationException("Trigger is not ready.");
    var source = _source ?? throw new InvalidOperationException("Frame source is not ready.");
    var frames = _frames ?? throw new InvalidOperationException("Frame queue is not ready.");
    try {
      while (true) {
        await _pauseGate.WaitAsync(cancellationToken);
        await trigger.WaitAsync(cancellationToken);
        await _pauseGate.WaitAsync(cancellationToken);
        CapturedFrame? frame = null;
        try {
          frame = await source.CaptureAsync(cancellationToken);
          Interlocked.Increment(ref _captured);
          if (_context.Profile.Execution.QueueMode == RuntimeQueueMode.ProcessAll) {
            await frames.Writer.WriteAsync(frame, cancellationToken);
          } else if (!frames.Writer.TryWrite(frame)) {
            frame.Dispose();
            Interlocked.Increment(ref _dropped);
            PublishStatistics();
            continue;
          }
          frame = null;
          Interlocked.Increment(ref _queueDepth);
          PublishStatistics();
        } finally {
          frame?.Dispose();
        }
      }
    } finally {
      frames.Writer.TryComplete();
    }
  }

  private async Task ProcessFramesAsync(CancellationToken cancellationToken) {
    var frames = _frames ?? throw new InvalidOperationException("Frame queue is not ready.");
    await foreach (var frame in frames.Reader.ReadAllAsync(cancellationToken)) {
      Interlocked.Decrement(ref _queueDepth);
      using (frame) {
        try {
          var result = await _runtime.ExecuteFrameAsync(
              _context,
              frame,
              cancellationToken);
          Interlocked.Increment(ref _processed);
          ResultProduced?.Invoke(this, result);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
          throw;
        } catch (Exception exception) {
          Interlocked.Increment(ref _errors);
          RuntimeError?.Invoke(this, exception);
          if (!_context.Profile.Execution.ContinueAfterError) {
            throw;
          }
        }
      }
      PublishStatistics();
    }
  }

  private void DrainQueue() {
    if (_frames is null) {
      return;
    }
    while (_frames.Reader.TryRead(out var frame)) {
      Interlocked.Decrement(ref _queueDepth);
      frame.Dispose();
    }
  }

  private async Task ReleaseResourcesAsync() {
    var trigger = Interlocked.Exchange(ref _trigger, null);
    var source = Interlocked.Exchange(ref _source, null);
    if (trigger is not null) {
      await trigger.DisposeAsync();
    }
    if (source is not null) {
      await source.DisposeAsync();
    }
    _cancellation?.Dispose();
    _cancellation = null;
    _frames = null;
    _pipeline = null;
  }

  private void ResetStatistics() {
    Interlocked.Exchange(ref _captured, 0);
    Interlocked.Exchange(ref _processed, 0);
    Interlocked.Exchange(ref _dropped, 0);
    Interlocked.Exchange(ref _errors, 0);
    Interlocked.Exchange(ref _queueDepth, 0);
    PublishStatistics();
  }

  private RuntimeStatistics Snapshot() {
    return new RuntimeStatistics(
        Interlocked.Read(ref _captured),
        Interlocked.Read(ref _processed),
        Interlocked.Read(ref _dropped),
        Interlocked.Read(ref _errors),
        Math.Max(0, Volatile.Read(ref _queueDepth)),
        _context.Profile.Execution.QueueCapacity);
  }

  private void PublishStatistics() {
    StatisticsChanged?.Invoke(this, Snapshot());
  }

  private static void ValidateExecution(RuntimeExecutionSettings settings) {
    if (settings.QueueCapacity is < 1 or > 1024) {
      throw new ArgumentOutOfRangeException(
          nameof(settings.QueueCapacity),
          "Frame queue capacity must be between 1 and 1024.");
    }
    if (settings.Trigger == RuntimeTriggerKind.Timer
        && settings.IntervalMs is < 1 or > 86_400_000) {
      throw new ArgumentOutOfRangeException(
          nameof(settings.IntervalMs),
          "Timer interval must be between 1 and 86400000 ms.");
    }
  }
}

internal sealed class AsyncPauseGate {
  private readonly object _gate = new();
  private TaskCompletionSource _resume = Completed();

  public Task WaitAsync(CancellationToken cancellationToken) {
    lock (_gate) {
      return _resume.Task.WaitAsync(cancellationToken);
    }
  }

  public void Pause() {
    lock (_gate) {
      if (_resume.Task.IsCompleted) {
        _resume = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
      }
    }
  }

  public void Resume() {
    lock (_gate) {
      _resume.TrySetResult();
    }
  }

  private static TaskCompletionSource Completed() {
    var source = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    source.SetResult();
    return source;
  }
}

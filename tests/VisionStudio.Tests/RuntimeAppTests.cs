using Emgu.CV;
using VisionStudio.Core;
using VisionStudio.Operators.OpenCv;
using VisionStudio.Runtimes.App;

namespace VisionStudio.Tests;

public sealed class RuntimeAppTests {
  [Fact]
  public void Catalog_RegistersRuntimeFrameAsAnImageSource() {
    var registry = OpenCvOperatorCatalog.CreateRegistry();

    bool found = registry.TryGetDescriptor(
        "Input.RuntimeFrame",
        out var descriptor);

    Assert.True(found);
    Assert.Empty(descriptor.Inputs);
    var output = Assert.Single(descriptor.Outputs);
    Assert.Equal("Image", output.Name);
    Assert.Equal(NodeValueType.Mat, output.Type);
  }

  [Fact]
  public async Task WorkflowExecutor_InjectsRuntimeFrameIntoExistingPipeline() {
    var registry = OpenCvOperatorCatalog.CreateRegistry();
    var input = Node(registry, "Input.RuntimeFrame", "runtime-input");
    var gray = Node(registry, "Image.Gray", "gray");
    var workflow = new WorkflowDocument {
      Name = "Runtime input test",
      Nodes = { input, gray },
      Connections = { new(input.Id, "Image", gray.Id, "Image") }
    };
    var image = OpenCvFileCodec.Read(TestImagePath());
    var executor = new WorkflowExecutor(registry, WorkspaceRoot());

    using var result = await executor.ExecuteAsync(
        workflow,
        gray.Id,
        [new WorkflowExternalOutput(input.Id, "Image", image)]);

    Assert.True(result.TryGetOutput(gray.Id, "Image", out Mat? output));
    Assert.NotNull(output);
    Assert.False(output.IsEmpty);
    Assert.Contains(result.Timings, timing =>
        timing.NodeId == input.Id && timing.FromExternalInput);
  }

  [Fact]
  public async Task FrameSources_CaptureImageFolderAndSimulatedFrames() {
    string projectRoot = DemoProjectRoot();
    var factory = new FrameSourceFactory();
    var settings = new[] {
      new RuntimeSourceSettings {
        Kind = RuntimeSourceKind.Image,
        Path = "images/input/1.png"
      },
      new RuntimeSourceSettings {
        Kind = RuntimeSourceKind.ImageFolder,
        Path = "images/input"
      },
      new RuntimeSourceSettings {
        Kind = RuntimeSourceKind.Simulated,
        Width = 320,
        Height = 240
      }
    };

    foreach (var setting in settings) {
      await using var source = factory.Create(setting, projectRoot);
      await source.OpenAsync();
      using var frame = await source.CaptureAsync();
      Assert.False(frame.Image.IsEmpty);
      Assert.True(frame.Sequence > 0);
    }
  }

  [Fact]
  public async Task FrameSourceFactory_ExposesReplaceableVideoAndEmguCameraModules() {
    var factory = new FrameSourceFactory();
    string root = DemoProjectRoot();

    var camera = factory.Create(new RuntimeSourceSettings {
      Kind = RuntimeSourceKind.Camera,
      CameraIndex = 0
    }, root);
    Assert.IsType<EmguCameraFrameSource>(camera);
    await camera.DisposeAsync();

    string videoName = $"runtime-test-{Guid.NewGuid():N}.mp4";
    string videoPath = Path.Combine(root, "images", "input", videoName);
    File.WriteAllBytes(videoPath, [0]);
    try {
      var video = factory.Create(new RuntimeSourceSettings {
        Kind = RuntimeSourceKind.Video,
        Path = $"images/input/{videoName}"
      }, root);
      Assert.IsType<VideoFrameSource>(video);
      await video.DisposeAsync();
    } finally {
      File.Delete(videoPath);
    }
  }

  [Fact]
  public async Task DemoProject_LoadsAndRunsWithCompleteMappedResults() {
    var service = new ProjectRuntimeService();
    var context = await service.OpenAsync(DemoProjectRoot());

    var result = await service.RunOnceAsync(context);

    Assert.Equal("metrics_2", context.Profile.TargetNodeId);
    Assert.Equal(5, result.Values.Count);
    Assert.All(context.Profile.ResultMappings, mapping => Assert.Contains(
        result.Values,
        value => value.NodeId == mapping.NodeId && value.Port == mapping.Port));
    Assert.NotNull(result.PrimaryImagePath);
    string outputPath = ProjectPaths.ResolveInside(
        context.Project.RootPath,
        result.PrimaryImagePath);
    Assert.True(new FileInfo(outputPath).Length > 0);
    Assert.Contains(result.Values, value =>
        value.Port == "PixelLength" && value.Unit == "px");
    Assert.NotEmpty(result.Timings);
  }

  [Fact]
  public async Task InvalidResultMapping_IsRejectedBeforeCapture() {
    var service = new ProjectRuntimeService();
    var context = await service.OpenAsync(DemoProjectRoot());
    context.Profile.ResultMappings.Add(new RuntimeResultMapping {
      Name = "无效结果",
      NodeId = "metrics_2",
      Port = "Missing"
    });

    var exception = Assert.Throws<InvalidDataException>(() =>
        ProjectRuntimeService.Validate(
            context.Project,
            context.Workflow,
            context.Profile,
            context.Registry));

    Assert.Contains("metrics_2.Missing", exception.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void RuntimeStateMachine_EnforcesDocumentedTransitions() {
    var machine = new RuntimeStateMachine();

    machine.TransitionTo(RuntimeState.Starting);
    machine.TransitionTo(RuntimeState.Running);
    machine.TransitionTo(RuntimeState.Paused);
    machine.TransitionTo(RuntimeState.Running);
    machine.TransitionTo(RuntimeState.Stopping);
    machine.TransitionTo(RuntimeState.Stopped);

    Assert.Equal(RuntimeState.Stopped, machine.State);
    Assert.Throws<InvalidOperationException>(() =>
        machine.TransitionTo(RuntimeState.Paused));
  }

  [Fact]
  public async Task ManualTrigger_DoesNotCaptureUntilFired() {
    var service = new ProjectRuntimeService();
    var context = await service.OpenAsync(DemoProjectRoot());
    context.Profile.Source.Kind = RuntimeSourceKind.Image;
    context.Profile.Source.Path = "images/input/1.png";
    context.Profile.Execution.Trigger = RuntimeTriggerKind.Manual;
    context.Profile.Execution.QueueMode = RuntimeQueueMode.ProcessAll;
    context.Profile.Execution.QueueCapacity = 2;
    await using var controller = new ProjectRuntimeController(context, service);
    var completed = new TaskCompletionSource<RuntimeInspectionResult>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    controller.ResultProduced += (_, result) => completed.TrySetResult(result);

    await controller.StartAsync();
    await Task.Delay(100);
    Assert.Equal(0, controller.Statistics.Captured);
    Assert.True(controller.FireTrigger());
    var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await controller.StopAsync();

    Assert.Equal(RuntimeState.Stopped, controller.State);
    Assert.Equal(1, result.Sequence);
    Assert.Equal(1, controller.Statistics.Processed);
  }

  [Fact]
  public async Task TimerTrigger_ContinuouslyProcessesFramesAndStops() {
    var service = new ProjectRuntimeService();
    var context = await service.OpenAsync(DemoProjectRoot());
    context.Profile.Source.Kind = RuntimeSourceKind.Image;
    context.Profile.Source.Path = "images/input/1.png";
    context.Profile.Execution.Trigger = RuntimeTriggerKind.Timer;
    context.Profile.Execution.IntervalMs = 20;
    context.Profile.Execution.QueueMode = RuntimeQueueMode.ProcessAll;
    context.Profile.Execution.QueueCapacity = 2;
    await using var controller = new ProjectRuntimeController(context, service);
    var completed = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    controller.ResultProduced += (_, _) => {
      if (controller.Statistics.Processed >= 2) {
        completed.TrySetResult();
      }
    };

    await controller.StartAsync();
    await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    controller.Pause();
    Assert.Equal(RuntimeState.Paused, controller.State);
    controller.Resume();
    await controller.StopAsync();

    Assert.Equal(RuntimeState.Stopped, controller.State);
    Assert.True(controller.Statistics.Processed >= 2);
    Assert.True(controller.Statistics.QueueDepth <= 2);
  }

  [Fact]
  public async Task LatestFrameQueue_RemainsBoundedAndDropsBacklog() {
    var service = new ProjectRuntimeService();
    var context = await service.OpenAsync(DemoProjectRoot());
    context.Profile.Source.Kind = RuntimeSourceKind.Image;
    context.Profile.Source.Path = "images/input/1.png";
    context.Profile.Execution.Trigger = RuntimeTriggerKind.Continuous;
    context.Profile.Execution.QueueMode = RuntimeQueueMode.LatestFrame;
    context.Profile.Execution.QueueCapacity = 1;
    await using var controller = new ProjectRuntimeController(context, service);

    await controller.StartAsync();
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (controller.Statistics.Dropped == 0 && DateTime.UtcNow < deadline) {
      await Task.Delay(20);
    }
    await controller.StopAsync();

    Assert.True(controller.Statistics.Captured > 1);
    Assert.True(controller.Statistics.Dropped > 0);
    Assert.InRange(controller.Statistics.QueueDepth, 0, 1);
    Assert.Equal(RuntimeState.Stopped, controller.State);
  }

  private static WorkflowNode Node(
      OperatorRegistry registry,
      string type,
      string id) {
    registry.TryGetDescriptor(type, out var descriptor);
    var node = OpenCvOperatorCatalog.CreateNode(descriptor, 0, 0);
    return new WorkflowNode {
      Id = id,
      Type = node.Type,
      Name = node.Name,
      Parameters = node.Parameters
    };
  }

  private static string TestImagePath() {
    return Path.Combine(WorkspaceRoot(), "tests", "image", "1.png");
  }

  private static string DemoProjectRoot() {
    return Path.Combine(WorkspaceRoot(), "samples", "DemoProject");
  }

  private static string WorkspaceRoot() {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null
        && !File.Exists(Path.Combine(directory.FullName, "VisionStudio.sln"))) {
      directory = directory.Parent;
    }
    return directory?.FullName
        ?? throw new DirectoryNotFoundException("VisionStudio workspace not found.");
  }
}

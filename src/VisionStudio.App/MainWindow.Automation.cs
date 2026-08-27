using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Emgu.CV;
using VisionStudio.Ai;
using VisionStudio.Ai.Abstractions;
using VisionStudio.CodeGen;
using VisionStudio.Core;
using VisionStudio.Operators.OpenCv;

namespace VisionStudio.App;

public sealed partial class MainWindow : Window {
  private async Task SetWorkflowAsync() {
    await AttachAutomationSessionAsync();
    Canvas.Workflow = _workflow;
    Canvas.Refresh();
    _dirty = false;
    ProjectText.Text = _project?.Project.Name ?? "";
    ToolTip.SetTip(ProjectText, _project?.ProjectFilePath ?? "");
    UpdateTitle();
  }

  private void Undo_Click(object? sender, RoutedEventArgs e) {
    UndoLastOperation();
  }

  private void UndoLastOperation() {
    if (_undoStack.Count == 0) { Status(_localization.Text("Status.NoUndo")); return; }
    var entry = _undoStack.Pop();
    CancelPreview();
    _workflow = WorkflowJson.Deserialize(entry.WorkflowJson);
    _automation?.SynchronizeFromHost(_workflow);
    Canvas.Workflow = _workflow;
    Canvas.Refresh();
    ShowProperties(null);
    MarkDirty(notifyAutomation: false);
    RecordOperation("撤销", entry.Description);
    Status(_localization.Format("Status.Undone", TranslateOperationText(entry.Description)));
  }

  private void RecordUndo(string description) {
    _undoStack.Push(new UndoEntry(WorkflowJson.Serialize(_workflow), description));
    TrimUndoHistory();
    RecordOperation(description);
  }

  private void TrimUndoHistory() {
    while (_undoStack.Count > 100) {
      var retained = _undoStack.Take(100).Reverse().ToArray();
      _undoStack.Clear();
      foreach (var item in retained) {
        _undoStack.Push(item);
      }
    }
  }

  private void ResetUndoHistory() {
    _undoStack.Clear();
  }

  private void RecordOperation(string action, string? detail = null) {
    var operation = new ProjectOperation(DateTimeOffset.Now, action, detail);
    _operationItems.Insert(0, FormatOperation(operation));
    while (_operationItems.Count > 200) {
      _operationItems.RemoveAt(_operationItems.Count - 1);
    }

    if (_project is not null) {
      _ = AppendOperationSafelyAsync(operation);
      ScheduleAutosave();
    }
  }

  private async Task AppendOperationSafelyAsync(ProjectOperation operation) {
    try { await _projects.AppendOperationAsync(_project!, operation); } catch (Exception ex) { Status(_localization.Format("Status.OperationLogFailed", UserError(ex))); }
  }

  private async Task LoadOperationHistoryAsync() {
    _operationItems.Clear();
    if (_project is null) {
      return;
    }

    foreach (var operation in (await _projects.LoadOperationsAsync(_project)).Reverse()) {
      _operationItems.Add(FormatOperation(operation));
    }
  }

  private string FormatOperation(ProjectOperation operation) {
    string action = TranslateOperationText(operation.Action);
    string detail = string.IsNullOrWhiteSpace(operation.Detail) ? "" : TranslateOperationText(operation.Detail);
    if (!_localization.IsChinese && ContainsHan(detail)) {
      detail = "";
    }

    return $"{operation.Timestamp:HH:mm:ss}  {action}{(detail.Length == 0 ? "" : " — " + detail)}";
  }

  private string TranslateOperationText(string? value) {
    if (string.IsNullOrWhiteSpace(value) || _localization.IsChinese) {
      return value ?? "";
    }

    string? exact = value switch {
      "创建项目" => "Action.CreateProject",
      "打开项目" => "Action.OpenProject",
      "导出 C#" => "Action.ExportCode",
      "修改输入图片" => "Action.ImportImage",
      "撤销" => "Action.Undo",
      "删除连接" => "Action.DeleteConnection",
      "创建连接" => "Action.CreateConnection",
      "移动算子" => "Action.MoveOperator",
      "删除算子" => "Action.DeleteOperator",
      "AI 工作流修改" => "Action.AiWorkflowUpdate",
      "自动排版" or "整理后续算子" => "Action.AutoLayout",
      "删除后续算子" => "Action.DeleteOperator",
      _ => null
    };
    if (exact is not null) {
      return _localization.Text(exact);
    }

    if (value.StartsWith("添加算子：", StringComparison.Ordinal)) {
      string originalName = value["添加算子：".Length..];
      var descriptor = _registry.Descriptors.FirstOrDefault(item => item.DisplayName == originalName || item.EnglishName == originalName);
      return $"{_localization.Text("Action.AddOperator")}: {(descriptor is null ? "Operator" : _localization.OperatorName(descriptor))}";
    }
    if (value.StartsWith("修改参数：", StringComparison.Ordinal)) {
      return _localization.Text("Action.ChangeParameter");
    }

    return ContainsHan(value) ? _localization.Text("Action.Undo") : value;
  }

  private string UserError(Exception exception) {
    string message = exception.Message;
    return !_localization.IsChinese && ContainsHan(message)
        ? _localization.Format("Error.GenericOperation", exception.GetType().Name)
        : message;
  }

  private static bool ContainsHan(string? value) {
    return value?.Any(character => character is >= '\u3400' and <= '\u9fff') == true;
  }

  private void MarkDirty(bool notifyAutomation = true) {
    _dirty = true;
    if (notifyAutomation) {
      _automation?.NotifyHostChanged();
    }

    UpdateTitle();
    ScheduleAutosave();
  }

  private async Task AttachAutomationSessionAsync() {
    await DetachAutomationSessionAsync();
    if (_project is null) {
      return;
    }

    _automation = new WorkflowAutomationSession(
        _project,
        _workflow,
        _projects,
        _registry,
        new OpenCvWorkflowAutomationRunner(_registry));
    _automation.WorkflowChanged += Automation_WorkflowChanged;
    _automation.ExecutionCompleted += Automation_ExecutionCompleted;
    AutomationHost.Attach(_automation);
  }

  private async Task DetachAutomationSessionAsync() {
    var automation = _automation;
    _automation = null;
    AutomationHost.Attach(null);
    if (automation is not null) {
      automation.WorkflowChanged -= Automation_WorkflowChanged;
      automation.ExecutionCompleted -= Automation_ExecutionCompleted;
    }
    await DisposeAiProviderAsync();
  }

  private void Automation_WorkflowChanged(object? sender, WorkflowChangedEventArgs change) {
    Dispatcher.UIThread.Post(() => {
      _undoStack.Push(new UndoEntry(WorkflowJson.Serialize(change.PreviousWorkflow), change.Summary));
      TrimUndoHistory();
      CancelPreview();
      _workflow = change.Workflow;
      _automation?.SynchronizeFromHost(_workflow, incrementRevision: false);
      Canvas.Workflow = _workflow;
      Canvas.Refresh();
      _dirty = false;
      UpdateTitle();
      if (!string.IsNullOrWhiteSpace(change.SelectedNodeId)) {
        Canvas.SelectNodeById(change.SelectedNodeId);
      } else {
        ShowProperties(null);
      }

      _ = LoadOperationHistoryAsync();
      Status(_localization.IsChinese ? $"AI 已更新工作流：{change.Summary}" : $"AI updated workflow: {change.Summary}");
    });
  }

  private void Automation_ExecutionCompleted(object? sender, WorkflowExecutionCompletedEventArgs completed) {
    Dispatcher.UIThread.Post(() => _ = ShowAutomationResultAsync(completed.Result));
  }

  private async Task ShowAutomationResultAsync(WorkflowAutomationRunResult result) {
    if (_project is null) {
      return;
    }

    if (!result.Success) {
      Status(_localization.IsChinese ? $"AI 工作流执行失败：{result.Error}" : $"AI workflow failed: {result.Error}");
      return;
    }

    var image = result.Outputs.FirstOrDefault(output => !string.IsNullOrWhiteSpace(output.ArtifactPath));
    if (image?.ArtifactPath is { } relative) {
      CancelPreview();
      string path = ProjectPaths.ResolveInside(_project.RootPath, relative);
      byte[] bytes = await File.ReadAllBytesAsync(path);
      string digest = Convert.ToHexString(SHA256.HashData(bytes));
      using var stream = new MemoryStream(bytes, writable: false);
      ReplacePreview(new Bitmap(stream), digest);
    }
    string summary = string.Join(_localization.IsChinese ? "；" : "; ", result.Outputs
        .Where(output => !string.IsNullOrWhiteSpace(output.Summary))
        .Select(output => $"{output.Port}={output.Summary}"));
    Status(_localization.IsChinese ? $"AI 工作流执行完成{(summary.Length == 0 ? "" : "：" + summary)}" : $"AI workflow completed{(summary.Length == 0 ? "" : ": " + summary)}");
  }

  private void ScheduleAutosave() {
    _autosaveCancellation?.Cancel();
    _autosaveCancellation?.Dispose();
    var cancellation = _autosaveCancellation = new CancellationTokenSource();
    _ = AutosaveAfterDelayAsync(cancellation.Token);
  }

  private async Task AutosaveAfterDelayAsync(CancellationToken cancellationToken) {
    try {
      await Task.Delay(800, cancellationToken);
      if (_project is not null) {
        await _projects.SaveWorkflowAsync(_project, _workflow, createSnapshot: false, cancellationToken);
        _dirty = false;
        UpdateTitle();
      }
    } catch (OperationCanceledException) { } catch (Exception ex) { Status(_localization.Format("Status.AutosaveFailed", UserError(ex))); }
  }

}

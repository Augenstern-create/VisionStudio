using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace VisionStudio.App;

/// <summary>Lists recorded executions and diffs any two of them.</summary>
public sealed class ExecutionHistoryWindow : Window {
  private readonly bool _chinese;
  private readonly ExecutionHistoryStore _store;
  private readonly ListBox _list = new() { FontSize = 11 };
  private readonly ComboBox _left = new();
  private readonly ComboBox _right = new();
  private readonly TextBlock _diff = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
  private readonly TextBlock _summary = new() { FontSize = 11, Opacity = .75 };

  public ExecutionHistoryWindow(ExecutionHistoryStore store, bool chinese) {
    _chinese = chinese;
    _store = store;
    Title = T("执行历史", "Execution history");
    Width = 900; Height = 620; MinWidth = 760; MinHeight = 480;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    Content = BuildContent();
    Refresh();
  }

  private Control BuildContent() {
    var root = new Grid {
      Margin = new Thickness(14),
      RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto"),
      RowSpacing = 10
    };

    root.Children.Add(new TextBlock {
      Text = T("每次点击节点或应用助手改动都会记录一条。选两条对比参数与输出差异。",
               "Every node run and every applied assistant change is recorded. Pick two rows to diff parameters and outputs."),
      FontSize = 11, Opacity = .75, TextWrapping = TextWrapping.Wrap
    });

    Grid.SetRow(_list, 1);
    root.Children.Add(_list);

    var pickers = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*"), ColumnSpacing = 8 };
    pickers.Children.Add(new TextBlock { Text = T("对比 A", "A"), VerticalAlignment = VerticalAlignment.Center });
    Grid.SetColumn(_left, 1); pickers.Children.Add(_left);
    var label = new TextBlock { Text = T("对比 B", "B"), VerticalAlignment = VerticalAlignment.Center };
    Grid.SetColumn(label, 2); pickers.Children.Add(label);
    Grid.SetColumn(_right, 3); pickers.Children.Add(_right);
    _left.SelectionChanged += (_, _) => RenderDiff();
    _right.SelectionChanged += (_, _) => RenderDiff();
    Grid.SetRow(pickers, 2);
    root.Children.Add(pickers);

    var scroller = new ScrollViewer { Content = _diff, MaxHeight = 220, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    Grid.SetRow(scroller, 3);
    root.Children.Add(scroller);

    var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
    Grid.SetRow(footer, 4);
    var refresh = new Button { Content = T("刷新", "Refresh") };
    refresh.Click += (_, _) => Refresh();
    var clear = new Button { Content = T("清空", "Clear") };
    clear.Click += async (_, _) => {
      _store.Records.Clear();
      await _store.SaveAsync();
      Refresh();
    };
    var close = new Button { Content = T("关闭", "Close") };
    close.Click += (_, _) => Close();
    footer.Children.Add(_summary);
    footer.Children.Add(refresh);
    footer.Children.Add(clear);
    footer.Children.Add(close);
    root.Children.Add(footer);

    return root;
  }

  private void Refresh() {
    var items = _store.Records.AsEnumerable().Reverse().ToList();
    _list.ItemsSource = items.Select(record => record.Caption).ToList();
    _left.ItemsSource = items;
    _right.ItemsSource = items;
    _left.SelectedIndex = items.Count > 1 ? 1 : (items.Count > 0 ? 0 : -1);
    _right.SelectedIndex = items.Count > 0 ? 0 : -1;
    _summary.Text = T($"共 {items.Count} 条", $"{items.Count} records");
    RenderDiff();
  }

  private void RenderDiff() {
    if (_left.SelectedItem is not ExecutionRecord left || _right.SelectedItem is not ExecutionRecord right) {
      _diff.Text = T("选择两条记录后显示差异。", "Select two records to see the diff.");
      return;
    }

    var lines = new List<string> {
      $"{left.At:HH:mm:ss} {left.NodeName} ({left.Milliseconds:F0} ms)   →   {right.At:HH:mm:ss} {right.NodeName} ({right.Milliseconds:F0} ms)",
      ""
    };

    if (left.NodeType != right.NodeType) {
      lines.Add(T($"节点类型不同：{left.NodeType} → {right.NodeType}", $"Different node type: {left.NodeType} → {right.NodeType}"));
      lines.Add("");
    }

    lines.Add(T("参数变化", "Parameter changes"));
    var parameterKeys = left.Parameters.Keys.Union(right.Parameters.Keys, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal);
    bool anyParameter = false;
    foreach (string key in parameterKeys) {
      string before = left.Parameters.TryGetValue(key, out string? value) ? value : "—";
      string after = right.Parameters.TryGetValue(key, out string? value2) ? value2 : "—";
      if (before == after) {
        continue;
      }

      anyParameter = true;
      lines.Add($"  {key}: {before} → {after}");
    }

    if (!anyParameter) {
      lines.Add($"  {T("（无变化）", "(none)")}");
    }

    lines.Add("");
    lines.Add(T("输出变化", "Output changes"));
    var outputKeys = left.Outputs.Keys.Union(right.Outputs.Keys, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal);
    bool anyOutput = false;
    foreach (string key in outputKeys) {
      string before = left.Outputs.TryGetValue(key, out string? value) ? value : "—";
      string after = right.Outputs.TryGetValue(key, out string? value2) ? value2 : "—";
      if (before == after) {
        continue;
      }

      anyOutput = true;
      lines.Add($"  {key}: {before} → {after}");
    }

    if (!anyOutput) {
      lines.Add($"  {T("（无变化）", "(none)")}");
    }

    _diff.Text = string.Join(Environment.NewLine, lines);
  }

  private string T(string chinese, string english) => _chinese ? chinese : english;
}

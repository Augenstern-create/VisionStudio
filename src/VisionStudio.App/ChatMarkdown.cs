using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace VisionStudio.App;

/// <summary>
/// Minimal Markdown renderer for chat bubbles: headings, bullet/ordered lists, quotes, rules,
/// bold/italic/inline code. Fenced code blocks are handled by <see cref="ChatMessageControl"/>
/// before this runs, so the model's long structured answers no longer show raw markers.
/// </summary>
internal static class ChatMarkdown {
  public static Control Render(string text, Action<SelectableTextBlock> register) {
    var root = new StackPanel { Spacing = 5 };
    var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    int index = 0;
    while (index < lines.Length) {
      string line = lines[index];
      string trimmed = line.Trim();
      if (trimmed.Length == 0) {
        index++;
        continue;
      }

      if (IsRule(trimmed)) {
        root.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#40808080")), Margin = new Thickness(0, 3) });
        index++;
        continue;
      }

      int level = HeadingLevel(trimmed);
      if (level > 0) {
        var heading = NewSelectable(register, trimmed[(level + 1)..].Trim(), BuildInlines);
        heading.FontSize = level switch { 1 => 13.5, 2 => 12.5, _ => 11.8 };
        heading.FontWeight = FontWeight.SemiBold;
        heading.Margin = new Thickness(0, index == 0 ? 0 : 3, 0, 1);
        root.Children.Add(heading);
        index++;
        continue;
      }

      if (trimmed.StartsWith('>')) {
        var quote = NewSelectable(register, trimmed.TrimStart('>').Trim(), BuildInlines);
        quote.FontStyle = FontStyle.Italic;
        quote.Opacity = .85;
        quote.Margin = new Thickness(0, 0, 0, 0);
        root.Children.Add(new Border {
          BorderThickness = new Thickness(2, 0, 0, 0),
          BorderBrush = new SolidColorBrush(Color.Parse("#60808080")),
          Padding = new Thickness(8, 0, 0, 0),
          Child = quote
        });
        index++;
        continue;
      }

      if (TryParseBullet(trimmed, out string bulletBody)) {
        var textBlock = NewSelectable(register, bulletBody, BuildInlines);
        root.Children.Add(Row("•", textBlock, indent: 0));
        index++;
        continue;
      }

      if (TryParseOrdered(trimmed, out string marker, out string orderedBody)) {
        var textBlock = NewSelectable(register, orderedBody, BuildInlines);
        root.Children.Add(Row(marker + ".", textBlock, indent: 0));
        index++;
        continue;
      }

      // Paragraph: gather consecutive plain lines, keeping soft wraps.
      var paragraph = new StringBuilder(trimmed);
      index++;
      while (index < lines.Length) {
        string next = lines[index].Trim();
        if (next.Length == 0 || HeadingLevel(next) > 0 || next.StartsWith('>') ||
            TryParseBullet(next, out _) || TryParseOrdered(next, out _, out _) || IsRule(next)) {
          break;
        }

        paragraph.Append(' ').Append(next);
        index++;
      }

      root.Children.Add(NewSelectable(register, paragraph.ToString(), BuildInlines));
    }

    if (root.Children.Count == 0) {
      root.Children.Add(NewSelectable(register, text, BuildInlines));
    }

    return root;
  }

  private static Control Row(string marker, SelectableTextBlock content, double indent) {
    var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6, Margin = new Thickness(indent, 0, 0, 0) };
    grid.Children.Add(new TextBlock { Text = marker, FontSize = 11, Opacity = .8, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 0, 0) });
    Grid.SetColumn(content, 1);
    grid.Children.Add(content);
    return grid;
  }

  private static SelectableTextBlock NewSelectable(Action<SelectableTextBlock> register, string text, Action<SelectableTextBlock, string> fill) {
    var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
    block.Classes.Add("chatText");
    fill(block, text);
    register(block);
    return block;
  }

  private static void BuildInlines(SelectableTextBlock block, string text) {
    var literals = new List<(string Text, bool Bold, bool Italic, bool Code)>();
    var buffer = new StringBuilder();
    int index = 0;
    void Flush() {
      if (buffer.Length > 0) {
        literals.Add((buffer.ToString(), false, false, false));
        buffer.Clear();
      }
    }

    while (index < text.Length) {
      char current = text[index];
      if (current == '`') {
        int close = text.IndexOf('`', index + 1);
        if (close > index) {
          Flush();
          literals.Add((text[(index + 1)..close], false, false, true));
          index = close + 1;
          continue;
        }
      } else if (current == '*' && index + 1 < text.Length && text[index + 1] == '*') {
        int close = text.IndexOf("**", index + 2, StringComparison.Ordinal);
        if (close > index) {
          Flush();
          literals.Add((text[(index + 2)..close], true, false, false));
          index = close + 2;
          continue;
        }
      } else if (current == '*') {
        int close = text.IndexOf('*', index + 1);
        if (close > index) {
          Flush();
          literals.Add((text[(index + 1)..close], false, true, false));
          index = close + 1;
          continue;
        }
      }

      buffer.Append(current);
      index++;
    }

    Flush();
    // Plain text stays a plain Text property so selection, copy and accessibility behave exactly
    // as before; only genuinely formatted runs go through Inlines.
    if (literals.Count == 1 && literals[0] is { Bold: false, Italic: false, Code: false }) {
      block.Text = literals[0].Text;
      return;
    }

    if (literals.Count == 0) {
      block.Text = text;
      return;
    }

    foreach (var (literal, bold, italic, code) in literals) {
      var run = new Avalonia.Controls.Documents.Run(literal);
      if (bold) {
        run.FontWeight = FontWeight.SemiBold;
      }

      if (italic) {
        run.FontStyle = FontStyle.Italic;
      }

      if (code) {
        run.FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace");
        run.Background = new SolidColorBrush(Color.Parse("#28808080"));
      }

      block.Inlines!.Add(run);
    }
  }

  /// <summary>Strips Markdown markers so screen readers do not announce them.</summary>
  public static string ToPlainText(string text) {
    var builder = new StringBuilder();
    foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) {
      string trimmed = line.TrimStart();
      int level = HeadingLevel(trimmed);
      if (level > 0) {
        trimmed = trimmed[(level + 1)..];
      } else if (trimmed.StartsWith('>')) {
        trimmed = trimmed.TrimStart('>').TrimStart();
      } else if (TryParseBullet(trimmed, out string body)) {
        trimmed = "• " + body;
      }

      builder.Append(trimmed.Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal)).Append('\n');
    }

    return builder.ToString().Trim();
  }

  private static bool IsRule(string line) {
    string stripped = line.Replace(" ", "", StringComparison.Ordinal);
    return stripped.Length >= 3 && (stripped.All(character => character == '-') ||
        stripped.All(character => character == '*') || stripped.All(character => character == '_'));
  }

  private static int HeadingLevel(string line) {
    int level = 0;
    while (level < line.Length && line[level] == '#') {
      level++;
    }

    return level is > 0 and <= 6 && level < line.Length && line[level] == ' ' ? level : 0;
  }

  private static bool TryParseBullet(string line, out string body) {
    body = "";
    if (line.Length < 2 || line[0] is not ('-' or '*' or '•' or '+') || line[1] != ' ') {
      return false;
    }

    body = line[2..].Trim();
    return body.Length > 0;
  }

  private static bool TryParseOrdered(string line, out string marker, out string body) {
    marker = "";
    body = "";
    int digits = 0;
    while (digits < line.Length && char.IsDigit(line[digits])) {
      digits++;
    }

    if (digits == 0 || digits + 1 >= line.Length || line[digits] is not ('.' or ')' or '、') || line[digits + 1] != ' ') {
      return false;
    }

    marker = line[..digits];
    body = line[(digits + 2)..].Trim();
    return body.Length > 0;
  }
}

using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

namespace VisionStudio.App;

public sealed record ChatMessagePart(bool IsCode, string Text, string? Language = null);

public sealed class ChatQuoteRequestedEventArgs(string text) : EventArgs {
  public string Text { get; } = text;
}

/// <summary>Selectable chat bubble with quote support and fenced-code copy actions.</summary>
public sealed class ChatMessageControl : ContentControl {
  private readonly List<SelectableTextBlock> _selectableBlocks = [];

  public ChatMessageControl(
      string role,
      string message,
      string userLabel,
      string assistantLabel,
      string quoteLabel,
      string copyCodeLabel) {
    Role = role;
    Message = message ?? "";
    MessageParts = ParseMessage(Message);
    bool isUser = role.Equals("user", StringComparison.OrdinalIgnoreCase);
    AutomationProperties.SetName(this, $"{(isUser ? userLabel : assistantLabel)}: {Message}");
    HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
    MaxWidth = 560;

    var bubble = new Border {
      HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
      Child = BuildBubble(isUser, userLabel, assistantLabel, quoteLabel, copyCodeLabel)
    };
    bubble.Classes.Add("chatBubble");
    bubble.Classes.Add(isUser ? "user" : "assistant");
    Content = bubble;
  }

  public string Role { get; }
  public string Message { get; }
  public IReadOnlyList<ChatMessagePart> MessageParts { get; }
  public event EventHandler<ChatQuoteRequestedEventArgs>? QuoteRequested;

  protected override AutomationPeer OnCreateAutomationPeer() {
    return new ChatMessageAutomationPeer(this);
  }

  internal static IReadOnlyList<ChatMessagePart> ParseMessage(string message) {
    var parts = new List<ChatMessagePart>();
    int position = 0;
    while (position < message.Length) {
      int fence = message.IndexOf("```", position, StringComparison.Ordinal);
      if (fence < 0) {
        AddText(parts, message[position..]);
        break;
      }
      AddText(parts, message[position..fence]);
      int headerEnd = message.IndexOf('\n', fence + 3);
      if (headerEnd < 0) {
        AddText(parts, message[fence..]);
        break;
      }
      int closing = message.IndexOf("```", headerEnd + 1, StringComparison.Ordinal);
      if (closing < 0) {
        AddText(parts, message[fence..]);
        break;
      }
      string language = message[(fence + 3)..headerEnd].Trim();
      string code = message[(headerEnd + 1)..closing].TrimEnd('\r', '\n');
      parts.Add(new(true, code, string.IsNullOrWhiteSpace(language) ? null : language));
      position = closing + 3;
    }
    if (parts.Count == 0) {
      parts.Add(new(false, ""));
    }

    return parts;
  }

  private Control BuildBubble(bool isUser, string userLabel, string assistantLabel, string quoteLabel, string copyCodeLabel) {
    var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 6 };
    var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
    var role = new TextBlock {
      Text = isUser ? userLabel : assistantLabel,
      FontSize = 10,
      FontWeight = FontWeight.SemiBold,
      Opacity = .72,
      VerticalAlignment = VerticalAlignment.Center
    };
    header.Children.Add(role);
    var quote = new Button { Content = quoteLabel, Classes = { "chatAction" } };
    ToolTip.SetTip(quote, quoteLabel);
    quote.Click += (_, _) => QuoteRequested?.Invoke(this, new(SelectedTextOrMessage()));
    Grid.SetColumn(quote, 1);
    header.Children.Add(quote);
    root.Children.Add(header);

    var body = new StackPanel { Spacing = 7 };
    foreach (var part in MessageParts) {
      body.Children.Add(part.IsCode ? BuildCodeBlock(part, copyCodeLabel) : BuildText(part.Text));
    }

    Grid.SetRow(body, 1);
    root.Children.Add(body);
    return root;
  }

  private SelectableTextBlock BuildText(string text) {
    var block = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
    block.Classes.Add("chatText");
    _selectableBlocks.Add(block);
    return block;
  }

  private Control BuildCodeBlock(ChatMessagePart part, string copyCodeLabel) {
    var root = new Grid { RowDefinitions = new RowDefinitions("30,Auto") };
    var header = new Border { Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") } };
    header.Classes.Add("chatCodeHeader");
    var headerGrid = (Grid)header.Child;
    headerGrid.Children.Add(new TextBlock {
      Text = part.Language ?? "code",
      FontSize = 10,
      Opacity = .72,
      VerticalAlignment = VerticalAlignment.Center
    });
    var copy = new Button { Content = copyCodeLabel, Classes = { "chatAction" } };
    ToolTip.SetTip(copy, copyCodeLabel);
    copy.Click += async (_, _) => await CopyToClipboardAsync(part.Text);
    Grid.SetColumn(copy, 1);
    headerGrid.Children.Add(copy);
    root.Children.Add(header);

    var code = new SelectableTextBlock {
      Text = part.Text,
      FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"),
      TextWrapping = TextWrapping.NoWrap
    };
    code.Classes.Add("chatCodeText");
    _selectableBlocks.Add(code);
    var scroller = new ScrollViewer {
      Content = code,
      HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
      VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
    };
    Grid.SetRow(scroller, 1);
    root.Children.Add(scroller);
    var border = new Border { Child = root };
    border.Classes.Add("chatCodeBlock");
    return border;
  }

  private string SelectedTextOrMessage() {
    return _selectableBlocks.Select(block => block.SelectedText).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? Message;
  }

  private async Task CopyToClipboardAsync(string text) {
    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
    if (clipboard is not null) {
      await clipboard.SetValueAsync(DataFormat.Text, text);
    }
  }

  private static void AddText(ICollection<ChatMessagePart> parts, string text) {
    string normalized = text.Trim('\r', '\n');
    if (normalized.Length > 0) {
      parts.Add(new(false, normalized));
    }
  }

  private sealed class ChatMessageAutomationPeer(ChatMessageControl owner) : ControlAutomationPeer(owner) {
    protected override AutomationControlType GetAutomationControlTypeCore() {
      return AutomationControlType.Text;
    }

    protected override string GetClassNameCore() {
      return nameof(ChatMessageControl);
    }

    protected override string GetNameCore() {
      return AutomationProperties.GetName(owner) ?? owner.Message;
    }
  }
}

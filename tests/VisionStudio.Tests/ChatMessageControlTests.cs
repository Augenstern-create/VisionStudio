using Avalonia.Controls;
using Avalonia.Interactivity;
using VisionStudio.App;

namespace VisionStudio.Tests;

public sealed class ChatMessageControlTests
{
    [Fact]
    public void ParseMessage_SeparatesNormalTextAndFencedCode()
    {
        var parts = ChatMessageControl.ParseMessage("先执行：\n```csharp\nvar count = 1;\n```\n然后查看结果。");

        Assert.Collection(parts,
            part =>
            {
                Assert.False(part.IsCode);
                Assert.Equal("先执行：", part.Text);
            },
            part =>
            {
                Assert.True(part.IsCode);
                Assert.Equal("csharp", part.Language);
                Assert.Equal("var count = 1;", part.Text);
            },
            part =>
            {
                Assert.False(part.IsCode);
                Assert.Equal("然后查看结果。", part.Text);
            });
    }

    [Fact]
    public void Control_RendersSelectableTextCodeCopyButtonAndQuoteSelection()
    {
        var control = new ChatMessageControl(
            "assistant",
            "裂纹数量：2\n```text\nlength=128\n```",
            "你",
            "AI 助手",
            "引用",
            "复制代码");
        var descendants = Descendants(control).ToArray();
        var selectable = descendants.OfType<SelectableTextBlock>().ToArray();

        Assert.Equal(2, selectable.Length);
        Assert.Equal("裂纹数量：2", selectable[0].Text);
        Assert.Equal("length=128", selectable[1].Text);
        Assert.Contains(descendants.OfType<Button>(), button => Equals(button.Content, "复制代码"));

        selectable[0].SelectionStart = 0;
        selectable[0].SelectionEnd = 2;
        string? quoted = null;
        control.QuoteRequested += (_, args) => quoted = args.Text;
        var quoteButton = Assert.Single(descendants.OfType<Button>(), button => Equals(button.Content, "引用"));
        quoteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal("裂纹", quoted);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        if (root is ContentControl { Content: Control content })
        {
            yield return content;
            foreach (var descendant in Descendants(content)) yield return descendant;
        }
        else if (root is Decorator { Child: Control child })
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
        else if (root is Panel panel)
        {
            foreach (var panelChild in panel.Children)
            {
                yield return panelChild;
                foreach (var descendant in Descendants(panelChild)) yield return descendant;
            }
        }
    }
}

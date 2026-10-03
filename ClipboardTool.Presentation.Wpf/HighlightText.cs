using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ClipboardTool.Domain.Search;

namespace ClipboardTool.Presentation.Wpf;

/// <summary>
/// 正文命中高亮渲染（F23；legacy ClipCard renderHighlight 的皮肤对齐）：
/// 附加属性把高亮片段转成 Run 内联；命中片段底色 accent-soft（两主题同 token 值，
/// 与选中卡底色同源，legacy styles.css .card mark）。单一未命中片段直接走 Text（零开销）。
/// </summary>
public static class HighlightText
{
    public static readonly DependencyProperty SpansProperty = DependencyProperty.RegisterAttached(
        "Spans",
        typeof(IReadOnlyList<HighlightSpan>),
        typeof(HighlightText),
        new PropertyMetadata(null, OnSpansChanged));

    public static void SetSpans(TextBlock target, IReadOnlyList<HighlightSpan>? value) =>
        target.SetValue(SpansProperty, value);

    public static IReadOnlyList<HighlightSpan>? GetSpans(TextBlock target) =>
        (IReadOnlyList<HighlightSpan>?)target.GetValue(SpansProperty);

    private static void OnSpansChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text)
        {
            return;
        }

        text.Inlines.Clear();
        if (e.NewValue is not IReadOnlyList<HighlightSpan> spans || spans.Count == 0)
        {
            return;
        }

        // 单一片段且未命中：原文渲染，不生成 Run
        if (spans.Count == 1 && !spans[0].Hit)
        {
            text.Text = spans[0].Text;
            return;
        }
        text.Text = string.Empty;

        var highlightBrush = text.TryFindResource("Brush.Selection.Background") as Brush;
        foreach (var span in spans)
        {
            if (span.Hit)
            {
                text.Inlines.Add(new Run(span.Text)
                {
                    Background = highlightBrush,
                    Foreground = text.TryFindResource("Brush.Text.Primary") as Brush,
                });
            }
            else
            {
                text.Inlines.Add(new Run(span.Text));
            }
        }
    }
}

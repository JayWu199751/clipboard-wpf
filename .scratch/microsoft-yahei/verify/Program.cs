using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 只载入真实 App.xaml 资源，不执行启动链路，不读取或改写剪贴板与存档。
        var app = new ClipboardTool.Presentation.Wpf.App();
        app.InitializeComponent();
        var sans = (FontFamily)app.Resources["Font.Sans"];
        var mono = (FontFamily)app.Resources["Font.Mono"];
        if (sans.Source != "Microsoft YaHei, Segoe UI")
            throw new InvalidOperationException("普通字体资源不是微软雅黑回退链。");
        if (mono.Source != "JetBrains Mono, Cascadia Code, Consolas")
            throw new InvalidOperationException("等宽字体资源发生变化。");

        const string sample = "剪贴板 设置 搜索 备注 复制 Clipboard 123";
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 720, 180));
            DrawAndVerify(drawing, sans, FontWeights.Normal, sample, 30);
            DrawAndVerify(drawing, sans, FontWeights.Medium, sample, 100);
        }
        var bitmap = new RenderTargetBitmap(720, 180, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(args[0]);
        encoder.Save(output);
        Console.WriteLine($"Font.Sans={sans.Source}");
        Console.WriteLine($"Font.Mono={mono.Source}");
        app.Shutdown();
    }

    private static void DrawAndVerify(DrawingContext output, FontFamily family, FontWeight weight, string sample, double y)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var text = new FormattedText(sample, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal), 24, Brushes.Black, 1);
            drawing.DrawText(text, new Point(20, y));
        }
        var runs = GlyphRuns(visual.Drawing).ToArray();
        if (runs.Length == 0)
            throw new InvalidOperationException("没有实际文字渲染结果。");
        foreach (var run in runs)
        {
            var glyph = run.GlyphRun.GlyphTypeface;
            if (!glyph.FamilyNames.Values.Any(name => name is "Microsoft YaHei" or "微软雅黑")
                || !glyph.FontUri.IsFile || run.GlyphRun.GlyphIndices.Contains((ushort)0))
                throw new InvalidOperationException($"字体未使用系统微软雅黑或出现缺字：{glyph.FontUri} / {glyph.Weight}");
            // 系统微软雅黑没有 Medium 字体文件，由 WPF 匹配可用字重；不改界面请求的字重。
            Console.WriteLine($"请求 {weight}，实际渲染：{glyph.FontUri} / {glyph.Weight}");
        }
        output.DrawDrawing(visual.Drawing);
    }

    private static IEnumerable<GlyphRunDrawing> GlyphRuns(Drawing drawing)
    {
        if (drawing is GlyphRunDrawing glyph)
            yield return glyph;
        else if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var run in GlyphRuns(child))
                    yield return run;
    }
}

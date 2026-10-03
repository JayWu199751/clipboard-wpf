using System.Windows;

namespace P5RoundedHitTest;

/// <summary>
/// 下层靶窗：判据①「角外点击落到下层窗口」的接收方。
/// 独立 STA 线程承载（与探针窗不同线程，保证穿透是真实跨线程语义），
/// 点击计数经回调回传，前台归属由探针侧 GetForegroundWindow 观测。
/// </summary>
public partial class TargetWindow : Window
{
    private readonly Action _onClicked;
    private int _clicks;

    /// <summary>窗口就绪后的 HWND（跨线程读取，Show 后由本线程写入一次）。</summary>
    public volatile IntPtr Hwnd;

    public TargetWindow(Action onClicked)
    {
        InitializeComponent();
        _onClicked = onClicked;

        var wa = SystemParameters.WorkArea;
        Left = wa.Left;
        Top = wa.Top;
        Width = wa.Width;
        Height = wa.Height;

        PreviewMouseDown += (_, _) =>
        {
            _clicks++;
            ClickText.Text = $"已收到 {_clicks} 次点击";
            _onClicked();
        };
        Loaded += (_, _) => Hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
    }
}

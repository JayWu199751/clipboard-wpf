using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClipboardTool.Infrastructure.Windows;

namespace ClipboardTool.Presentation.Wpf;

/// <summary>
/// 缩略图宿主（T06 图卡，F42）：150 DIP 高、contain 居中、6 DIP 圆角、棋盘格底。
/// 经共享 ThumbnailCache 取图（按 Id 记忆化，P4 结论）；双重防错图：
/// 缓存回调携带 id，与本控件发起请求时锁定的 EntryId 比对；控件自身代次计数，
/// Recycling 换绑（EntryId 变更）后旧代次回调作废。解码在后台线程完成并 Freeze，
/// 回调经缓存 marshal 归队 UI。
/// 停靠回收不可见项：窗口停靠时对已实现容器调用 Reset（释放位图引用 + 清缓存），
/// 呼出时调用 Request 预热（仅 realized 容器，数量有限）。
/// </summary>
public sealed class ThumbnailHost : Border
{
    public static readonly DependencyProperty EntryIdProperty = DependencyProperty.Register(
        nameof(EntryId), typeof(string), typeof(ThumbnailHost),
        new PropertyMetadata(null, OnRequestPropertyChange));

    public static readonly DependencyProperty ImagePathProperty = DependencyProperty.Register(
        nameof(ImagePath), typeof(string), typeof(ThumbnailHost),
        new PropertyMetadata(null, OnRequestPropertyChange));

    public static readonly DependencyProperty CacheProperty = DependencyProperty.Register(
        nameof(Cache), typeof(ThumbnailCache), typeof(ThumbnailHost),
        new PropertyMetadata(null, OnCacheChange));

    /// <summary>缩略图高度（F42 规格：150 DIP，contain 居中）。</summary>
    public const double HeightDip = 150;
    /// <summary>缩略图圆角（F42 规格：6 DIP）。</summary>
    private const double CornerRadiusDip = 6;

    private readonly Image _image;
    private int _generation;

    public ThumbnailHost()
    {
        _image = new Image
        {
            Stretch = Stretch.Uniform, // contain
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Child = _image;
        CornerRadius = new CornerRadius(CornerRadiusDip);
        SizeChanged += (_, _) => UpdateClip();
        Loaded += (_, _) => Request();
    }

    public string? EntryId
    {
        get => (string?)GetValue(EntryIdProperty);
        set => SetValue(EntryIdProperty, value);
    }

    public string? ImagePath
    {
        get => (string?)GetValue(ImagePathProperty);
        set => SetValue(ImagePathProperty, value);
    }

    public ThumbnailCache? Cache
    {
        get => (ThumbnailCache?)GetValue(CacheProperty);
        set => SetValue(CacheProperty, value);
    }

    /// <summary>圆角裁剪：Border 的 CornerRadius 不裁剪子内容，Image 需要显式几何。</summary>
    private void UpdateClip()
    {
        var clip = new RectangleGeometry(new Rect(new Point(0, 0), RenderSize), CornerRadiusDip, CornerRadiusDip);
        clip.Freeze();
        Clip = clip;
    }

    private static void OnRequestPropertyChange(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ThumbnailHost)d).Request();

    private static void OnCacheChange(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ThumbnailHost)d).Request();

    /// <summary>发起取图：EntryId 在此刻锁定为本控件的当前条目（回调防错图的比对基准）。</summary>
    public void Request()
    {
        // 文字卡（折叠态）不取图；缺路径/缓存不发请求
        if (Visibility != Visibility.Visible || Cache is not { } cache ||
            EntryId is not { } id || ImagePath is null)
        {
            return;
        }
        var generation = ++_generation;
        // 解码到缩略图物理像素：150 DIP × 当前 DPI（4K 原图不整幅进托管内存）
        var pixelHeight = (int)Math.Round(HeightDip * VisualTreeHelper.GetDpi(this).PixelsPerDip);
        cache.GetOrLoad(id, ImagePath, pixelHeight, (loadedId, bitmap) =>
        {
            // 缓存层归队后执行：id 校验（回调携带的条目必须是本控件当前条目）+ 代次校验（Recycling 防错图）
            if (generation != _generation || EntryId != loadedId)
            {
                return;
            }
            _image.Source = bitmap;
        });
    }

    /// <summary>停靠回收（不可见项）：代次作废（迟到回调不渲染）并释放位图引用。</summary>
    public void Reset()
    {
        _generation++;
        _image.Source = null;
    }
}

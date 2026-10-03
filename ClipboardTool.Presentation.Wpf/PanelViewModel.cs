using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ClipboardTool.Domain.History;

namespace ClipboardTool.Presentation.Wpf;

/// <summary>
/// 面板视图模型：真实历史条目驱动卡片列表（T02 文字段）。
/// 条目集合由 EntriesChanged 事件整表重载（T02 规模足够；增量刷新与搜索归 T04）；
/// 状态文案为结果契约 { ok, message } 的 message 单源渲染（F12），呼出时清空（F14）。
/// </summary>
public sealed class PanelViewModel : INotifyPropertyChanged
{
    private string _statusText = string.Empty;

    public ObservableCollection<CardViewModel> Items { get; } = [];

    public string CountText => $"{Items.Count} 条";

    /// <summary>焦点错误/结果提示（toast 雏形；F46 的完整 toast 归 T05）。空串即隐藏。</summary>
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText == value) return;
            _statusText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusVisible));
        }
    }

    public Visibility StatusVisible => string.IsNullOrEmpty(_statusText) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// 页脚六组提示（F45）。键名字形与动作文案逐字对齐原型注册表
    /// （prototype.js：labels ArrowUp→↑、ArrowDown→↓、Enter→⏎、Escape→Esc、z→Z、b→B、Delete→Del）。
    /// T01 骨架静态呈现；T04 起改由键位注册表按面板模式生成，未生效的键不再展示（提示 = 行为）。
    /// </summary>
    public IReadOnlyList<FooterHint> FooterHints { get; } =
    [
        new("↑↓", "选择"),
        new("⏎", "复制"),
        new("Z", "置顶"),
        new("B", "备注"),
        new("Del", "删除"),
        new("Esc", "隐藏"),
    ];

    /// <summary>整表重载：全量快照替换（监听线程的变更事件已由订阅方归队 UI）。</summary>
    public void Reload(IReadOnlyList<HistoryEntry> entries)
    {
        Items.Clear();
        foreach (var entry in entries)
        {
            Items.Add(new CardViewModel(entry.Id, entry.Text, FormatMeta(entry)));
        }
        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>Meta 行（T02 只有创建时间；来源归 T03、图钉/备注归 T05）。</summary>
    private static string FormatMeta(HistoryEntry entry) =>
        DateTimeOffset.FromUnixTimeMilliseconds(entry.CreatedAtMs).LocalDateTime.ToString("MM-dd HH:mm");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public sealed record FooterHint(string Keys, string Action);
}

/// <summary>卡片视图模型：Body 为完整正文原样（展示层裁三行，不改正文，F01）。</summary>
public sealed record CardViewModel(string Id, string Body, string Meta);

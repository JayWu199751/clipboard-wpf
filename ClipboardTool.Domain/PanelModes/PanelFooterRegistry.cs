namespace ClipboardTool.Domain.PanelModes;

/// <summary>页脚一组键位提示：键名字形 + 中文行为描述（legacy keyboard.ts FooterChip）。</summary>
public sealed record FooterChip(string Keys, string Action);

/// <summary>
/// 页脚键位注册表（F45「提示 = 行为，写死键名即违规」；legacy keyboard.ts footerChips + KEY_NAMES）：
/// 字形表是注册表数据侧（与 legacy KEY_NAMES 同位），页脚 chip 与备注字数段全部从这里推导；
/// 渲染层不得写字面键名。键名变化时改这里一处，提示与行为不漂移。
/// </summary>
public static class PanelFooterRegistry
{
    /// <summary>导航键展示字形（legacy KEY_NAMES 映射：arrowup→↑、arrowdown→↓、enter→⏎、escape→Esc、delete→Del；字母原样大写）。</summary>
    public static string Glyph(NavAction action) => action switch
    {
        NavAction.Up => "↑",
        NavAction.Down => "↓",
        NavAction.Enter => "⏎",
        NavAction.Escape => "Esc",
        NavAction.Delete => "Del",
        NavAction.Pin => "Z",
        NavAction.Note => "B",
        NavAction.Search => "空格",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>
    /// 浏览/搜索态页脚六组（顺序即页脚顺序；legacy footerChips）：↑↓ 并排写进同一枚 chip
    /// （两枚 chip 会让这组多出一次间隙，节奏断裂）。搜索键住在搜索井的 chip 里，呼出键
    /// 只在面板隐藏时有意义（那时页脚不可见），均不入页脚。
    /// </summary>
    public static IReadOnlyList<FooterChip> BrowseChips() =>
    [
        new(Glyph(NavAction.Up) + Glyph(NavAction.Down), "选择"),
        new(Glyph(NavAction.Enter), "复制"),
        new(Glyph(NavAction.Pin), "置顶"),
        new(Glyph(NavAction.Note), "备注"),
        new(Glyph(NavAction.Delete), "删除"),
        new(Glyph(NavAction.Escape), "隐藏"),
    ];

    /// <summary>备注态页脚两组（F45：备注时改 Enter保存/Esc取消；字数段「n/200」由渲染层绑定草稿长度拼出）。</summary>
    public static IReadOnlyList<FooterChip> NoteChips() =>
    [
        new(Glyph(NavAction.Enter), "保存"),
        new(Glyph(NavAction.Escape), "取消"),
    ];

    /// <summary>备注字数上限：与存储归一化上限同源（编辑器 UTF-16 MaxLength 与存储 Rune 截断对齐，F07）；
    /// const 供 XAML x:Static 引用（编辑框 MaxLength 与页脚字数段同源，不写字面量）。</summary>
    public const int MaxNoteLength = History.HistoryStore.DefaultMaxNoteLength;
}

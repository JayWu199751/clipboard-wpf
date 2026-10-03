// T06 移交观察点复核（T09）：约 20 条撤销窗口并存且容器持续重建时个别 Del 无效。
//
// 机理（复核结论）：Items 重建（Reload/SetHiddenIds → Items.Clear）与 Recycling 容器换绑
// 会触发 Selector.SelectionChanged，旧回写逻辑把容器状态（-1）直接写回 ViewModel.SelectedIndex，
// 该噪音落在两次 Del 之间时 SelectedItemId 变 null → Del 无效；停靠排空后容器稳定故自愈。
//
// 修复：容器回写走 SyncSelectionFromContainer 噪音过滤——列表非空时 -1 视为容器重建噪音
// 被忽略（本面板不存在「列表非空且取消选中」的用户场景：SelectionMode=Single 且点击外部即停靠）。
using System.Collections.Specialized;
using System.Windows.Controls;
using ClipboardTool.Domain.History;
using ClipboardTool.Presentation.Wpf;
using Xunit;

namespace ClipboardTool.Tests.Accessibility;

public class DelSelectionRaceTests
{
    private static HistoryEntry TextEntry(string id) =>
        new(id, EntryKind.Text, $"内容-{id}", null, 1_000, null, false, 0, "");

    [Fact]
    public void 条目重建期间容器回写负一不污染选中索引()
    {
        Sta.Run(() =>
        {
            var vm = new PanelViewModel();
            var container = new ListBox { ItemsSource = vm.Items };

            var entries = Enumerable.Range(0, 20).Select(i => TextEntry($"id{i}")).ToList();
            vm.Reload(entries);
            vm.SelectedIndex = 5;
            Assert.Equal("id5", vm.SelectedItemId);

            // 模拟容器侧选择变化回写（code-behind OnListSelectionChanged 的新路径）：
            // 列表非空时容器报 -1 = 重建噪音，不得污染键盘导航基准
            vm.SyncSelectionFromContainer(-1);
            Assert.Equal("id5", vm.SelectedItemId);

            // 真实点选（有效索引）仍正常回写
            vm.SyncSelectionFromContainer(7);
            Assert.Equal("id7", vm.SelectedItemId);
        });
    }

    [Fact]
    public void 连续多条延迟删除的重载风暴不丢失选中目标()
    {
        // 复现 T06 观察点时序：20 条撤销窗口并存 = 连续 ReloadEntries；
        // 每次重载（SetHiddenIds+Reload）后夹紧逻辑应保持有效选中，Del 链路不落空。
        Sta.Run(() =>
        {
            var vm = new PanelViewModel();
            var container = new ListBox { ItemsSource = vm.Items };

            var entries = Enumerable.Range(0, 30).Select(i => TextEntry($"id{i}")).ToList();
            vm.Reload(entries);
            vm.SelectedIndex = 10;

            var hidden = new List<string>();
            for (var i = 0; i < 20; i++)
            {
                // 容器重建噪音（Items.Clear 时 SelectionChanged 报 -1）+ 噪音回写
                vm.SyncSelectionFromContainer(-1);
                hidden.Add($"id{vm.SelectedIndex}");
                vm.SetHiddenIds(hidden);
                vm.Reload(entries.Except(entries.Where(e => hidden.Contains(e.Id))).ToList());

                // 两次 Del 之间：选中目标必须始终有效（旧实现在这里会变 -1/null）
                Assert.True(vm.SelectedIndex >= 0 && vm.SelectedIndex < vm.Items.Count,
                    $"第 {i} 次重载后选中索引失效：{vm.SelectedIndex}");
                Assert.NotNull(vm.SelectedItemId);
            }
        });
    }

    [Fact]
    public void 列表清空时容器回写负一照常生效()
    {
        // 噪音过滤的边界：Items 真的空了（清空历史），-1 是合法状态必须同步。
        Sta.Run(() =>
        {
            var vm = new PanelViewModel();
            var container = new ListBox { ItemsSource = vm.Items };

            vm.Reload([TextEntry("only")]);
            vm.SelectedIndex = 0;
            Assert.Equal("only", vm.SelectedItemId);

            vm.Reload([]); // 清空历史
            vm.SyncSelectionFromContainer(-1);
            Assert.Equal(-1, vm.SelectedIndex);
            Assert.Null(vm.SelectedItemId);
        });
    }
}

using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ClipboardTool.Presentation.Wpf;

/// <summary>
/// 容器诊断（T06 真机证据，P4 方法移植）：仅在环境变量 CLIPBOARDTOOL_E2E_DIAG 指向输出文件时启用，
/// 正常运行零开销（不构造即不挂任何东西）。定时快照列表已实现（realized）容器：
/// distinct InstanceId（首次见到时分配的稳定编号）与当前绑定 index —— 同一实例先后绑定不同
/// index 即 Recycling 换绑证据；快照内容不变不落盘。采样含选中索引，供滚动时序对照。
/// </summary>
public sealed class ContainerDiagnostics
{
    private readonly ListBox _list;
    private readonly string _path;
    private readonly DispatcherTimer _timer;
    private readonly ConditionalWeakTable<ListBoxItem, object> _ids = [];
    private int _nextId;
    private string _last = string.Empty;

    public ContainerDiagnostics(ListBox list, string path)
    {
        _list = list;
        _path = path;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Sample();
        _timer.Start();
    }

    private void Sample()
    {
        try
        {
            var generator = _list.ItemContainerGenerator;
            var bind = new List<string>();
            for (var i = 0; i < _list.Items.Count; i++)
            {
                if (generator.ContainerFromIndex(i) is not ListBoxItem container)
                {
                    continue;
                }
                var id = InstanceId(container);
                bind.Add($"{id}:{i}");
            }
            var line = $"t={DateTime.Now:HH:mm:ss.fff} sel={_list.SelectedIndex} realized={bind.Count} bind={string.Join(",", bind)}";
            if (line == _last)
            {
                return;
            }
            _last = line;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, line + Environment.NewLine);
        }
        catch
        {
            // 诊断只增不减正确性：采样失败静默（列表重建等瞬态）
        }
    }

    private int InstanceId(ListBoxItem container)
    {
        if (_ids.TryGetValue(container, out var box))
        {
            return (int)box!;
        }
        var id = _nextId++;
        _ids.Add(container, id);
        return id;
    }
}

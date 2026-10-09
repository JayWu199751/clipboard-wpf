using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ClipboardTool.Infrastructure.Windows;

namespace ClipboardTool.Presentation.Wpf;

/// <summary>托盘菜单复用应用主题资源；子菜单也从同一资源树取色，不受任务栏明暗影响。</summary>
public sealed class TrayContextMenu : ContextMenu, IDisposable
{
    public TrayContextMenu(ResourceDictionary applicationResources)
    {
        Resources.MergedDictionaries.Add(applicationResources);
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/ClipboardTool;component/Themes/TrayMenu.xaml", UriKind.Relative),
        });
        SetResourceReference(StyleProperty, typeof(ContextMenu));
        Placement = PlacementMode.MousePoint;
        StaysOpen = false;
    }

    public void SetItems(IReadOnlyList<TrayMenuItem> items, Action<string> selected)
    {
        IsOpen = false;
        Items.Clear();
        AddItems(this, items, selected);
    }

    public void Show(IReadOnlyList<TrayMenuItem> items, Action<string> selected)
    {
        SetItems(items, selected);
        IsOpen = Items.Count > 0;
    }

    private void AddItems(ItemsControl parent, IReadOnlyList<TrayMenuItem> items, Action<string> selected)
    {
        foreach (var item in items)
        {
            if (item.Separator)
            {
                parent.Items.Add(new Separator());
                continue;
            }
            var entry = new MenuItem { Header = item.Label ?? string.Empty, IsChecked = item.Checked };
            if (item.SubItems is { } children)
            {
                AddItems(entry, children, selected);
            }
            else
            {
                entry.Click += (_, e) =>
                {
                    e.Handled = true;
                    IsOpen = false;
                    selected(item.Id);
                };
            }
            parent.Items.Add(entry);
        }
    }

    public void Dispose()
    {
        IsOpen = false;
        Items.Clear();
        Resources.MergedDictionaries.Clear();
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.IO;

namespace P4VirtualizationProbe;

public partial class MainWindow : Window
{
    /// <summary>列表静态引用（SelfCheck 视觉树遍历用）。</summary>
    public static ListBox? ListRef { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        ListRef = HistoryList;
        HistoryList.ItemsSource = ProbeCard.CreateMixed(200);
        Loaded += (_, _) =>
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                () => SelfCheck.Run(this, HistoryList, AppContext.BaseDirectory));
    }

    /// <summary>容器实例标识：首次 realize 时分配（distinct 数 = 创建的实例数 → Recycling 证据）。</summary>
    public void OnCardContainerLoaded(object sender, RoutedEventArgs e) =>
        SelfCheck.AssignInstanceId((ListBoxItem)sender);
}

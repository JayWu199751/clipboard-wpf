// STA 线程运行器：WPF 视觉元素（Border 等）只能在 STA 线程创建/访问，
// xunit 默认线程池是 MTA，此处在测试体内把目标代码派发到专用 STA 线程并同步等待。
// 异常原样传播回测试线程，保证失败原因不丢。
using System.Threading;

namespace ClipboardTool.Tests.Accessibility;

internal static class Sta
{
    public static void Run(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw failure;
        }
    }
}

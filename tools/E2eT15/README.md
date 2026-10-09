# F40 隔离进程验证

在仓库根目录运行：

```powershell
dotnet run --project tools/E2eT15/E2eT15.csproj -c Debug -- --evidence .scratch/wpf-rewrite/evidence-t15
```

工具只允许 Debug。使用临时存档、独立单实例门和 pipe，复用真实 App 启动、托盘、热键及窗口落地链路；不改用户设置或正式计划任务。测试以 Win32 消息模拟热键/托盘入口，并通过 WPF 菜单项派发命令，不代表物理键鼠验收。

覆盖默认/详细日志、第二实例投递成功与失败、正常退出码、Dispatcher/后台线程崩溃、未观察任务异常、日志路径不可写、多进程并发追加及轮转。故意崩溃仍由 CLR 终止，外部宿主检查实际退出码；不在产品中吞掉异常。

宿主持续排空子进程 stderr，避免错误输出填满管道；为验证进程调用 `WerSetFlags(WER_FAULT_REPORTING_NO_UI)`，其作用仅为禁止错误报告 UI，见 [Microsoft 文档](https://learn.microsoft.com/en-us/windows/win32/api/werapi/nf-werapi-wersetflags)。此设置不进入正式应用。

可选 `--evidence` 保存实际诊断日志；临时存档在验证结束后清理。失败时退出非零；超时只终止本工具启动的子进程。曾观察到部分早期运行在诊断读数完整后仍等待子进程结束超时，原因未唯一确定；最终无临时插桩的完整复跑通过，详见工单 15，不能据此宣称长期稳定性已验收。

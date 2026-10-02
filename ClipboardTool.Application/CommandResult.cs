namespace ClipboardTool.Application;

/// <summary>
/// 结果契约 <c>{ ok, message }</c>（legacy 术语表：结果契约）。
/// Application 层命令统一返回，失败必带面向用户的消息。
/// </summary>
public readonly record struct CommandResult(bool Ok, string Message)
{
    public static CommandResult Succeed(string message = "") => new(true, message);

    public static CommandResult Failure(string message) => new(false, message);
}

using System.IO;

namespace P2Focus;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            return Dispatch(args);
        }
        catch (Exception ex)
        {
            // 探针自记录崩溃（WinExe 无控制台可看）
            try
            {
                File.WriteAllText(
                    Path.Combine(Paths.Dir, $"crash-{string.Join("_", args.Where(a => !a.StartsWith("--")))}-{Environment.ProcessId}.log"),
                    ex.ToString());
            }
            catch { }
            return 9;
        }
    }

    static int Dispatch(string[] args)
    {
        string? ValueOf(string flag)
        {
            int i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        if (args.Contains("--target"))
        {
            string name = ValueOf("--name") ?? "A";
            int exitAfter = int.TryParse(ValueOf("--exit-after"), out int e) ? e : 0;
            return TargetApp.Run(name, exitAfter);
        }
        if (args.Contains("run")) return Runner.Run();
        if (args.Contains("negative")) return NegativeControl.Run();
        return 64;
    }
}

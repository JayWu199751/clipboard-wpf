using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClipboardTool.Application;
using ClipboardTool.Domain.History;
using ClipboardTool.Domain.Settings;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// 存档文件读写唯一归属（F08/F37；02-spec/02 §1）：路径布局、原子写入（.tmp→替换）、
/// 坏档备份（backup/ 独立路径，不把控制字段塞进旧数组）。
/// 可靠性契约：写失败（磁盘满/占用/掉电）原件不动、tmp 清理——重启后原件可恢复；
/// 整体坏档在覆盖前先备份，绝不丢唯一原件。
/// </summary>
public sealed class JsonStore : IHistoryStorage
{
    private readonly string _historyPath;
    private readonly string _settingsPath;
    private readonly string _backupDir;
    private readonly object _gate = new();
    private int _backupSeq;

    public JsonStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _historyPath = Path.Combine(dataDir, "clipboard-history.json");
        _settingsPath = Path.Combine(dataDir, "settings.json");
        _backupDir = Path.Combine(dataDir, "backup");
    }

    /// <summary>读历史存档：区分缺档（全新用户）、合法数组、整体坏档（需先备份）。</summary>
    public HistoryFileRead ReadHistory()
    {
        string text;
        lock (_gate)
        {
            if (!File.Exists(_historyPath))
            {
                return new HistoryFileRead.Missing();
            }
            try
            {
                text = File.ReadAllText(_historyPath);
            }
            catch (Exception)
            {
                // 读失败（占用/权限）≠ 缺档也 ≠ 坏档：返回 Unreadable，
                // 调用方本会话禁写——否则下一条记录会以空表覆盖从未读出的原件
                return new HistoryFileRead.Unreadable();
            }
        }
        return HistoryArchive.TryParseArray(text) is not null
            ? new HistoryFileRead.Loaded(text)
            : new HistoryFileRead.Corrupt(text);
    }

    public void SaveHistory(string historyJson)
    {
        lock (_gate)
        {
            AtomicWrite(_historyPath, historyJson);
        }
    }

    public string? BackupHistory() => Backup(_historyPath, "clipboard-history");

    /// <summary>读设置：缺档回默认；合法 JSON 交给 SettingsRules 逐键宽容；
    /// 整体不可解析先备份坏档再回默认（字段坏不备份——宽容解析已保住合法键）。</summary>
    public AppSettings ReadSettings()
    {
        string text;
        lock (_gate)
        {
            if (!File.Exists(_settingsPath))
            {
                return AppSettings.Default;
            }
            try
            {
                text = File.ReadAllText(_settingsPath);
            }
            catch (Exception)
            {
                return AppSettings.Default;
            }
        }
        if (IsJsonObject(text))
        {
            return SettingsRules.Parse(text);
        }
        Backup(_settingsPath, "settings");
        return AppSettings.Default;
    }

    public void SaveSettings(AppSettings settings)
    {
        lock (_gate)
        {
            AtomicWrite(_settingsPath, SettingsRules.Serialize(settings));
        }
    }

    public string? BackupSettings() => Backup(_settingsPath, "settings");

    private static bool IsJsonObject(string text)
    {
        try
        {
            return JsonNode.Parse(text) is JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>备份原件到 backup/ 独立目录，文件名带时间戳与序号保证唯一；失败返回 null 不抛出。</summary>
    private string? Backup(string path, string stem)
    {
        lock (_gate)
        {
            if (!File.Exists(path))
            {
                return null;
            }
            try
            {
                Directory.CreateDirectory(_backupDir);
                var name = $"{stem}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{_backupSeq++:x4}.json";
                var target = Path.Combine(_backupDir, name);
                File.Copy(path, target, overwrite: false);
                return target;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>原子写入：先写 .tmp 再替换原件。中途失败原件不动、tmp 尽力清理后原样抛出——
    /// 写失败恢复不丢原件（02-spec/02 §1 可靠性要求）。</summary>
    private static void AtomicWrite(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        try
        {
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tmp);
            }
            catch
            {
                // tmp 清理尽力而为；残留的 .tmp 不影响原件正确性
            }
            throw;
        }
    }
}

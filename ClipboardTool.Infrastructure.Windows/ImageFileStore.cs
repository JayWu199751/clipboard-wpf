using System.IO;
using ClipboardTool.Domain.History;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// 图片文件端口的生产实现（F05/F08）：images 目录、文件名 &lt;id&gt;.png（02-spec/02 §1 存档契约）。
/// 四条端口全实现：写盘失败返回 null（调用方不落库、不更新基线），哈希失败返回空串（不命中去重），
/// 删除失败不阻断历史操作。
/// </summary>
public sealed class ImageFileStore : IImageFileStore
{
    private readonly string _imagesDir;

    public ImageFileStore(string dataDir) => _imagesDir = Path.Combine(dataDir, "images");

    public string? SavePng(byte[] png, string id)
    {
        try
        {
            Directory.CreateDirectory(_imagesDir);
            var path = Path.Combine(_imagesDir, id + ".png");
            File.WriteAllBytes(path, png);
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public string HashFile(string path)
    {
        try
        {
            return ContentHash.Sha1Hex(File.ReadAllBytes(path));
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public void RemoveFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // 删除失败不阻断历史操作；文件残片不影响正确性（条目已不在历史中）
        }
    }

    public bool FileExists(string path) => File.Exists(path);
}

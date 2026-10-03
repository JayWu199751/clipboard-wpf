using System.IO;
using ClipboardTool.Domain.History;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// 图片文件端口的生产实现（F05/F08 + T06 图片身份）：images 目录、文件名 &lt;id&gt;.png
/// （02-spec/02 §1 存档契约）。端口行为：写盘失败返回 null（调用方不落库、不更新基线），
/// 哈希失败返回空串（不命中去重），删除失败不阻断历史操作。
/// 身份哈希口径（ADR-0005 第 4 条）：PNG 解码 → 规范化 RGBA（Bgra32 非预乘、自上而下）→ SHA-1，
/// 旧版（image crate）与新版（WIC）编码在同一像素下同哈希——跨格式去重的基础。
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
            var path = Path.Combine(_imagesDir, ImageFileNames.ForId(id));
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
            return HashPng(File.ReadAllBytes(path));
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public string HashPng(byte[] png)
    {
        try
        {
            var decoded = DibDecoder.DecodePngPixels(png);
            return decoded is null
                ? string.Empty
                : ContentHash.Sha1Hex(decoded.Rgba);
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

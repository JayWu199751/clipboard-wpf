using System.IO;
using System.Security.Cryptography;
using ClipboardTool.Domain.History;
using ClipboardTool.Infrastructure.Windows;
using Xunit;

namespace ClipboardTool.Tests.Infrastructure;

/// <summary>
/// ImageFileStore 图片身份哈希测试（T06，ADR-0005 第 4 条）：
/// 哈希口径 = PNG 解码 → 规范化 RGBA（Bgra32 非预乘、自上而下）→ SHA-1，
/// 旧版（image crate）编码与新编码（WIC）在同一像素下同哈希——跨格式去重的基础。
/// </summary>
public sealed class ImageFileStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly ImageFileStore _store;

    public ImageFileStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cbt-imgtests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new ImageFileStore(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    /// <summary>参照资产：legacy 链路固化的 .dib/.png 对（与 P3 原型同源）。</summary>
    private static byte[] ReferencePng(string name) => File.ReadAllBytes(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "ReferencePng", name + ".png"));

    private static string Sha1(byte[] data) =>
        Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

    [Theory]
    [InlineData("v0-rgb-24bpp-bottomup")]
    [InlineData("v0-rgb-32bpp-bottomup")]
    [InlineData("v5-bitfields-alpha-topdown")]
    [InlineData("v5-rgb-alpha-bottomup")]
    public void 同图跨格式_规范化哈希相同(string sample)
    {
        // 同一像素：legacy 编码 PNG vs WIC 重编码 PNG，字节不同、身份必须相同
        var legacyPng = ReferencePng(sample);
        var decoded = DibDecoder.DecodePngPixels(legacyPng)!;
        var wicPng = PngEncoder.Encode(decoded.Width, decoded.Height, decoded.Rgba);

        Assert.NotEqual(Sha1(legacyPng), Sha1(wicPng)); // 前提：编码字节确实不同
        Assert.Equal(_store.HashPng(legacyPng), _store.HashPng(wicPng));
    }

    [Fact]
    public void hashPng与hashFile同口径()
    {
        var png = ReferencePng("v5-rgb-alpha-bottomup");
        var path = _store.SavePng(png, "sample-id")!;

        Assert.Equal(_store.HashPng(png), _store.HashFile(path));
    }

    [Fact]
    public void 坏PNG_哈希空串_永不命中去重()
    {
        Assert.Equal(string.Empty, _store.HashPng("not-a-png"u8.ToArray()));
        Assert.Equal(string.Empty, _store.HashPng([]));
    }

    [Fact]
    public void 文件读不到_哈希空串()
    {
        Assert.Equal(string.Empty, _store.HashFile(Path.Combine(_dir, "missing.png")));
    }
}

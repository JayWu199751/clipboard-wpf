using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using ClipboardTool.Infrastructure.Windows;
using Xunit;
using Xunit.Abstractions;

namespace ClipboardTool.Tests.Infrastructure;

/// <summary>
/// 参照 PNG 往返测试正式化（T06，P3 原型 ReferencePngRoundTripTests 迁入）。
/// 判据 1 强形式（跨实现一致性）+ 判据 3（PNG 编码哈希对照与规范化比较）。
/// 参照资产：ReferencePng/ 下 legacy 链路（dib.rs + image 0.25）固化的 .dib/.png 对。
/// </summary>
public class ReferencePngRoundTripTests
{
    private readonly ITestOutputHelper _out;
    public ReferencePngRoundTripTests(ITestOutputHelper output) => _out = output;

    private static string AssetDir => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "ReferencePng");

    private sealed record Sample(string Name, string Shape, string PngSha1);

    private static List<Sample> LoadManifest()
    {
        var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AssetDir, "manifest.json")));
        return json.RootElement.GetProperty("samples").EnumerateArray()
            .Select(e => new Sample(
                e.GetProperty("name").GetString()!,
                e.GetProperty("形状").GetString()!,
                e.GetProperty("pngSha1").GetString()!))
            .ToList();
    }

    public static IEnumerable<object[]> Samples() =>
        LoadManifest().Select(s => new object[] { s.Name });

    private static byte[] ReadAsset(string name, string ext) =>
        File.ReadAllBytes(Path.Combine(AssetDir, name + ext));

    private static string Sha1(byte[] data) => Convert.ToHexStringLower(
        SHA1.HashData(data));

    [Theory]
    [MemberData(nameof(Samples))]
    public void CSharp解码器与legacy参照链路_像素逐字节一致(string name)
    {
        var dib = ReadAsset(name, ".dib");
        var refPng = ReadAsset(name, ".png");

        var mine = DibDecoder.DecodeToRgba(dib);
        var legacy = DibDecoder.DecodePngPixels(refPng);

        Assert.NotNull(mine);
        Assert.NotNull(legacy);
        Assert.Equal((legacy!.Width, legacy.Height), (mine!.Width, mine.Height));
        Assert.Equal(legacy.Rgba, mine.Rgba);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void WPF编码器与legacy编码器_PNG内容SHA1对照(string name)
    {
        var refPng = ReadAsset(name, ".png");
        var legacy = DibDecoder.DecodePngPixels(refPng)!;

        var mine = PngEncoder.Encode(legacy.Width, legacy.Height, legacy.Rgba);
        var mineSha1 = Sha1(mine);
        var refSha1 = Sha1(refPng);

        _out.WriteLine($"[{name}] legacy PNG SHA-1 = {refSha1}（{refPng.Length} 字节）");
        _out.WriteLine($"[{name}] WPF    PNG SHA-1 = {mineSha1}（{mine.Length} 字节）");
        _out.WriteLine($"[{name}] 逐字节一致 = {refSha1 == mineSha1}");

        // 主判据：哈希一致最好；若不一致，规范化像素比较必须成立（下一个测试），此处只记录不硬性要求一致。
        if (refSha1 != mineSha1)
            _out.WriteLine("[结论] PNG 内容哈希不一致 → 需要规范化像素比较方案（ADR-0005）");
    }

    [Fact]
    public void WPF编码器自身确定性_同像素两次编码哈希相同()
    {
        // 判身份的前提：编码必须确定。同一进程内对相同像素编码两次，SHA-1 必须相同。
        // 跨进程/重启后的持久确定性列入 REPORT 真机待人工项。
        var rgba = new byte[]
        {
            0x1a, 0x21, 0x22, 0xff, 0x33, 0x44, 0x55, 0x80,
            0x00, 0x00, 0xff, 0x7f, 0x77, 0x88, 0x99, 0xab,
        };
        var a = Sha1(PngEncoder.Encode(2, 2, rgba));
        var b = Sha1(PngEncoder.Encode(2, 2, rgba));
        Assert.Equal(a, b);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void 规范化像素比较_新旧行PNG解码后身份等价(string name)
    {
        // 向后兼容方案（ADR-0005 第 4 条）：身份判定 = PNG 解码 → 规范化 RGBA 字节 → SHA-1。
        // 旧 PNG（legacy 编码）与新 PNG（WPF 编码）在同一像素下必须得到同一像素哈希。
        var refPng = ReadAsset(name, ".png");
        var legacy = DibDecoder.DecodePngPixels(refPng)!;

        var minePng = PngEncoder.Encode(legacy.Width, legacy.Height, legacy.Rgba);
        var mineDecoded = DibDecoder.DecodePngPixels(minePng)!;

        Assert.Equal((legacy.Width, legacy.Height), (mineDecoded.Width, mineDecoded.Height));
        Assert.Equal(legacy.Rgba, mineDecoded.Rgba);

        var legacyPixelSha1 = Sha1(legacy.Rgba);
        var minePixelSha1 = Sha1(mineDecoded.Rgba);
        _out.WriteLine($"[{name}] 规范化像素 SHA-1 相同 = {legacyPixelSha1 == minePixelSha1}");
        Assert.Equal(legacyPixelSha1, minePixelSha1);
    }
}

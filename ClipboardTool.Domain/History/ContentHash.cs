using System.Security.Cryptography;

namespace ClipboardTool.Domain.History;

/// <summary>内容哈希（F03）：图片身份按 PNG 内容 SHA-1，十六进制小写（legacy history.rs sha1_hex）。</summary>
public static class ContentHash
{
    public static string Sha1Hex(byte[] data) =>
        Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();
}

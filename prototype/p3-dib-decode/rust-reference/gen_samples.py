#!/usr/bin/env python3
"""样例 DIB 字节构造器（生成判据 3 参照资产的输入）。

与 legacy dib.rs tests 模块的 dib() 蓝本逐语义对应：
头 + （头内掩码，V2 及以后）+（BITFIELDS 时 40 头后接 12 字节掩码）+ rows（按 4 字节补齐）。
rows 按缓冲区行序给出——自下而上的 DIB 里，最后一行才是视觉第一行。
"""
import struct
import sys
from pathlib import Path

BI_RGB, BI_BITFIELDS, BI_PNG = 0, 3, 6

OUT = Path(__file__).resolve().parent.parent / "ReferencePng"


def dib(header_size, w, h, bit_count, compression, masks, rows):
    buf = bytearray(header_size)
    struct.pack_into("<I", buf, 0, header_size)
    struct.pack_into("<i", buf, 4, w)
    struct.pack_into("<i", buf, 8, h)
    struct.pack_into("<H", buf, 12, 1)
    struct.pack_into("<H", buf, 14, bit_count)
    struct.pack_into("<I", buf, 16, compression)
    for i, m in enumerate(masks):
        off = 40 + i * 4
        if off + 4 <= header_size:
            struct.pack_into("<I", buf, off, m)
    if header_size == 40 and compression == BI_BITFIELDS:
        for m in masks[:3]:
            buf += struct.pack("<I", m)
    for row in rows:
        buf += row
        stride = (len(row) + 3) // 4 * 4
        buf += b"\x00" * (stride - len(row))
    return bytes(buf)


# 参照样例集合：形状覆盖 V5/BI_RGB+alpha、V5/BITFIELDS 下行序、40 头 32bpp、24bpp。
# 像素取固定字节（含 0x80/0x7f 半透明），全部样例确定性可复现。
SAMPLES = {
    # PixPin 回归形状：V5 头 + BI_RGB + alphaMask=0xff000000，BGRA 自下而上
    "v5-rgb-alpha-bottomup.dib": dib(
        124, 2, 2, 32, BI_RGB, (0, 0, 0, 0xFF000000),
        [
            bytes([0xFF, 0x21, 0x1A, 0xFF, 0x10, 0x20, 0x30, 0x80]),
            bytes([0x00, 0x00, 0xFF, 0xFF, 0x00, 0xFF, 0x00, 0x00]),
        ],
    ),
    # V5 头 + BI_BITFIELDS 四掩码（含 alpha），3×2 自上而下
    "v5-bitfields-alpha-topdown.dib": dib(
        124, 3, -2, 32, BI_BITFIELDS,
        (0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000),
        [
            bytes([0x1A, 0x21, 0x22, 0xFF, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAB]),
            bytes([0xCD, 0xDE, 0xEF, 0x7F, 0x01, 0x23, 0x45, 0x89, 0x5A, 0x6B, 0x7C, 0x8D]),
        ],
    ),
    # CF_DIB 常态：40 头 + BI_RGB 32bpp（A 强制 255）
    "v0-rgb-32bpp-bottomup.dib": dib(
        40, 2, 1, 32, BI_RGB, (0, 0, 0, 0),
        [bytes([0x1A, 0x21, 0x22, 0xCC, 0x0D, 0x0E, 0x0F, 0x11])],
    ),
    # 24bpp：宽 3 行 9 字节补到 12
    "v0-rgb-24bpp-bottomup.dib": dib(
        40, 3, 2, 24, BI_RGB, (0, 0, 0, 0),
        [
            bytes([0, 1, 2, 10, 11, 12, 20, 21, 22]),
            bytes([30, 31, 32, 40, 41, 42, 50, 51, 52]),
        ],
    ),
}


def main():
    OUT.mkdir(exist_ok=True)
    for name, data in SAMPLES.items():
        (OUT / name).write_bytes(data)
        print(f"写出 {name}（{len(data)} 字节）")


if __name__ == "__main__":
    sys.exit(main())

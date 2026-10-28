#!/usr/bin/env python3
"""从项目图标生成安卓启动图标(mipmap 各密度 + 自适应图标前景)。

用法(仓库根目录):
    python scripts/make-android-icons.py

为什么要自己生成:Android 需要 5 个密度的位图 + API 26 的自适应图标(前景/背景分层),
而项目只有一张 256px 的 `src/ALyricEase/Assets/icon_256.png`。手改容易漏密度或摆放越界,
所以把尺寸表写在代码里,改图标时重跑一次即可。

产出(`src/ALyricEase.Android/Resources/`):
    mipmap-<bucket>/ic_launcher.png          传统图标(内容占 ~92%,四边留透明边)
    mipmap-<bucket>/ic_launcher_round.png    圆形变体(同一张,图标本身就是圆盘)
    mipmap-<bucket>/ic_launcher_foreground.png  自适应图标前景(108dp 画布,内容 66dp)

自适应图标的 108dp 画布只有中心 72dp 会被显示、再套启动器自己的掩码,所以内容按 66dp
摆放,四周留 3dp 安全边 —— 直接铺满 108dp 的话,圆形/水滴形掩码会把圆盘切掉一圈。
背景色在 `Resources/values/colors.xml` 的 ic_launcher_background。

纯标准库实现(zlib 解码/编码 PNG),不需要 Pillow / ffmpeg。
源图要求:非隔行 8bit,color type 2(RGB)或 6(RGBA)。
"""

import argparse
import os
import struct
import sys
import zlib

SRC = 'src/ALyricEase/Assets/icon_256.png'
OUT = 'src/ALyricEase.Android/Resources'

# (密度桶, 画布边长, 图标内容边长)
LEGACY = [('mdpi', 48, 44), ('hdpi', 72, 66), ('xhdpi', 96, 88),
          ('xxhdpi', 144, 132), ('xxxhdpi', 192, 176)]
# 自适应前景:108dp 画布里的 66dp 内容(可见区 72dp,留 3dp 安全边)
ADAPTIVE = [('mdpi', 108, 66), ('hdpi', 162, 99), ('xhdpi', 216, 132),
            ('xxhdpi', 324, 198), ('xxxhdpi', 432, 264)]


def decode_png(path):
    """解出 (width, height, RGBA bytes)。只支持 8bit、非隔行、color type 2/6。"""
    data = open(path, 'rb').read()
    if data[:8] != b'\x89PNG\r\n\x1a\n':
        raise SystemExit(f'{path} 不是 PNG')
    pos, idat, width = 8, bytearray(), None
    while pos < len(data):
        length, tag = struct.unpack('>I4s', data[pos:pos + 8])
        body = data[pos + 8:pos + 8 + length]
        pos += 12 + length
        if tag == b'IHDR':
            width, height, depth, color_type, _, _, interlace = struct.unpack('>IIBBBBB', body)
            if depth != 8 or interlace != 0 or color_type not in (2, 6):
                raise SystemExit(f'只支持 8bit 非隔行 RGB/RGBA,当前 depth={depth} '
                                 f'colorType={color_type} interlace={interlace}')
        elif tag == b'IDAT':
            idat += body
        elif tag == b'IEND':
            break
    if width is None:
        raise SystemExit('PNG 缺少 IHDR')

    channels = 4 if color_type == 6 else 3
    stride = width * channels
    raw = zlib.decompress(bytes(idat))
    prev = bytearray(stride)
    rgba = bytearray(width * height * 4)
    src = 0
    for y in range(height):
        filter_type = raw[src]
        src += 1
        line = bytearray(raw[src:src + stride])
        src += stride
        if filter_type == 1:
            for i in range(channels, stride):
                line[i] = (line[i] + line[i - channels]) & 0xFF
        elif filter_type == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif filter_type == 3:
            for i in range(stride):
                left = line[i - channels] if i >= channels else 0
                line[i] = (line[i] + ((left + prev[i]) >> 1)) & 0xFF
        elif filter_type == 4:
            for i in range(stride):
                a = line[i - channels] if i >= channels else 0
                b = prev[i]
                c = prev[i - channels] if i >= channels else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pred = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pred) & 0xFF
        elif filter_type != 0:
            raise SystemExit(f'未知 PNG 行过滤器 {filter_type}')
        prev = line
        dst = y * width * 4
        for x in range(width):
            if channels == 4:
                rgba[dst:dst + 4] = line[x * 4:x * 4 + 4]
            else:
                rgba[dst:dst + 3] = line[x * 3:x * 3 + 3]
                rgba[dst + 3] = 255
            dst += 4
    return width, height, bytes(rgba)


def resize_area(sw, sh, src, dw, dh):
    """面积平均缩放:透明边缘不会发暗(先按 alpha 预乘再平均)。"""
    out = bytearray(dw * dh * 4)
    for dy in range(dh):
        y0 = int(dy * sh / dh)
        y1 = max(y0 + 1, int((dy + 1) * sh / dh))
        for dx in range(dw):
            x0 = int(dx * sw / dw)
            x1 = max(x0 + 1, int((dx + 1) * sw / dw))
            r = g = b = a = n = 0
            for y in range(y0, y1):
                base = (y * sw + x0) * 4
                for i in range(base, base + (x1 - x0) * 4, 4):
                    pa = src[i + 3]
                    r += src[i] * pa
                    g += src[i + 1] * pa
                    b += src[i + 2] * pa
                    a += pa
                    n += 1
            o = (dy * dw + dx) * 4
            if a:
                out[o] = min(255, r // a)
                out[o + 1] = min(255, g // a)
                out[o + 2] = min(255, b // a)
                out[o + 3] = a // n
    return bytes(out)


def write_png(path, size, rgba):
    raw = bytearray()
    for y in range(size):
        raw.append(0)  # filter: none
        raw += rgba[y * size * 4:(y + 1) * size * 4]

    def chunk(tag, body):
        return (struct.pack('>I', len(body)) + tag + body
                + struct.pack('>I', zlib.crc32(tag + body) & 0xFFFFFFFF))

    png = b'\x89PNG\r\n\x1a\n'
    png += chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0))
    png += chunk(b'IDAT', zlib.compress(bytes(raw), 9))
    png += chunk(b'IEND', b'')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    open(path, 'wb').write(png)


def centered(art, art_size, canvas_size):
    """把内容居中贴进全透明画布。"""
    if art_size == canvas_size:
        return art
    out = bytearray(canvas_size * canvas_size * 4)
    off = (canvas_size - art_size) // 2
    row = art_size * 4
    for y in range(art_size):
        dst = ((y + off) * canvas_size + off) * 4
        out[dst:dst + row] = art[y * row:(y + 1) * row]
    return bytes(out)


def main():
    parser = argparse.ArgumentParser(description='生成安卓启动图标')
    parser.add_argument('--src', default=SRC)
    parser.add_argument('--out', default=OUT)
    args = parser.parse_args()

    if not os.path.exists(args.src):
        raise SystemExit(f'找不到源图 {args.src}')
    sw, sh, src = decode_png(args.src)
    print(f'源图 {args.src} {sw}x{sh}')

    for bucket, canvas, art_size in LEGACY:
        art = resize_area(sw, sh, src, art_size, art_size)
        image = centered(art, art_size, canvas)
        for name in ('ic_launcher', 'ic_launcher_round'):
            write_png(f'{args.out}/mipmap-{bucket}/{name}.png', canvas, image)
        print(f'  mipmap-{bucket}: {canvas}px 内容 {art_size}px')

    for bucket, canvas, art_size in ADAPTIVE:
        art = resize_area(sw, sh, src, art_size, art_size)
        write_png(f'{args.out}/mipmap-{bucket}/ic_launcher_foreground.png', canvas,
                  centered(art, art_size, canvas))
        print(f'  mipmap-{bucket}: 自适应前景 {canvas}px 内容 {art_size}px')

    print(f'完成 → {args.out}')
    return 0


if __name__ == '__main__':
    sys.exit(main())

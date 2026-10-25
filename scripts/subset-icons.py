#!/usr/bin/env python3
"""把全量 Fluent 图标字体裁成"只含实际用到的码位"的子集。

背景:`ALyricEase.csproj` 是 `<AvaloniaResource Include="Assets\\**" />`,放在
`Assets/Fonts` 下的字体全部会打进 ALyricEase.dll(NativeAOT 也照打)。全量
Filled/Regular 各 2.5~2.8MB,而应用实际只用几十个码位。

用法(改过 Icons.axaml 或代码里的字形后跑一次,产物提交进仓库):

    python scripts/subset-icons.py            # 重新生成子集
    python scripts/subset-icons.py --check    # 只校验现有子集是否覆盖所有码位(CI 用)

构建/发布不依赖本脚本,也不依赖 Python —— 只有改图标的人需要跑。
原始全量字体在 `assets/font-sources/`,不在 `Assets/**` 里,不会被打包。

依赖: pip install fonttools
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

from fontTools import subset
from fontTools.ttLib import TTFont

REPO = Path(__file__).resolve().parents[1]
ASSETS = REPO / "src" / "ALyricEase" / "Assets" / "Fonts"
SOURCES = REPO / "assets" / "font-sources"

# 文件名保持不变:XAML 里的 avares 路径与 #家族名 都不用改,只有内容变小。
FONTS = {
    "Filled": "FluentSystemIcons-Filled.ttf",
    "Regular": "FluentSystemIcons-Regular.ttf",
}

# 私有使用区:图标字体只在这个范围里取码位,过滤掉普通文本里的 \uXXXX。
PUA_START, PUA_END = 0xE000, 0xF8FF

USE_PATTERN = re.compile(r"&#x([0-9A-Fa-f]{4,6});")
ESCAPE_PATTERN = re.compile(r"\\u([0-9A-Fa-f]{4})")


def scan_sources() -> set[int]:
    """收集项目里所有被引用的图标码位。

    三类来源:
      1. .axaml 里的 `&#xXXXX;`(Icons.axaml 的 x:String 资源,以及少数内联字形)
      2. .cs 里的 `\\uXXXX` 转义(IconGlyphs 的回落值)
      3. .cs 里直接写出来的私用区字符(如 PlayerViewModel.PlaybackModeGlyph)
    """
    codepoints: set[int] = set()
    root = REPO / "src" / "ALyricEase"
    for path in root.rglob("*"):
        if path.suffix not in {".axaml", ".cs"} or "obj" in path.parts or "bin" in path.parts:
            continue
        text = path.read_text(encoding="utf-8", errors="ignore")
        for match in USE_PATTERN.finditer(text):
            codepoints.add(int(match.group(1), 16))
        for match in ESCAPE_PATTERN.finditer(text):
            codepoints.add(int(match.group(1), 16))
        for char in text:
            code = ord(char)
            if PUA_START <= code <= PUA_END:
                codepoints.add(code)
    return {cp for cp in codepoints if PUA_START <= cp <= PUA_END}


def load_cmap(path: Path) -> set[int]:
    with TTFont(path, fontNumber=0) as font:
        return set(font.getBestCmap())


def subset_font(source: Path, target: Path, unicodes: set[int]) -> tuple[int, str]:
    """裁一份子集。保留 name 表 —— XAML 用 `#FluentSystemIcons-Filled` 找家族名。"""
    options = subset.Options()
    options.name_IDs = ["*"]
    options.name_legacy = True
    options.name_languages = ["*"]
    options.layout_features = ["*"]
    options.notdef_outline = True
    options.recommended_glyphs = True

    font = subset.load_font(str(source), options)
    subsetter = subset.Subsetter(options=options)
    subsetter.populate(unicodes=unicodes)
    subsetter.subset(font)
    subset.save_font(font, str(target), options)

    with TTFont(target, fontNumber=0) as result:
        family = result["name"].getDebugName(1) or "<无 name>"
        kept = len(result.getBestCmap())
    return kept, family


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true",
                        help="只校验现有子集是否覆盖所有引用码位,不写文件")
    args = parser.parse_args()

    used = scan_sources()
    if not used:
        print("[subset] 没扫到任何图标码位,检查扫描路径", file=sys.stderr)
        return 1

    covered: set[int] = set()
    failed = False
    rows: list[tuple[str, int, int, str]] = []

    for label, filename in FONTS.items():
        target = ASSETS / filename
        source = SOURCES / filename

        if args.check:
            if not target.exists():
                print(f"[subset] 缺少子集产物 {target}", file=sys.stderr)
                return 1
            cmap = load_cmap(target)
            covered |= cmap & used
            rows.append((label, len(cmap & used), len(cmap), f"{target.stat().st_size / 1024:.0f}KB"))
            # 全量字体里有、子集里被裁掉的码位 = 漏跑脚本(改了 Icons.axaml 却没重新生成)
            if source.exists():
                dropped = (used & load_cmap(source)) - cmap
                if dropped:
                    failed = True
                    print(f"[subset] {label} 子集缺码位(请重新运行本脚本): "
                          + " ".join(f"U+{cp:04X}" for cp in sorted(dropped)), file=sys.stderr)
            continue

        if not source.exists():
            print(f"[subset] 缺少原始字体 {source}\n"
                  f"         请把全量字体放回 assets/font-sources/(不该在 Assets/** 里)", file=sys.stderr)
            return 1

        keep_set = used & load_cmap(source)
        keep_set.add(0x20)  # 空格/度量,避免某些渲染器算出零宽度
        before = source.stat().st_size / 1024
        kept, family = subset_font(source, target, keep_set)
        after = target.stat().st_size / 1024
        covered |= keep_set
        rows.append((label, kept, len(keep_set), f"{before:.0f}KB → {after:.0f}KB"))
        print(f"[subset] {label}: 保留 {kept} 字形(`{family}`), {before:.0f}KB → {after:.0f}KB")

    uncovered = used - covered
    if uncovered:
        failed = True
        print("[subset] 这些码位在所有字体里都不存在(拼错?): "
              + " ".join(f"U+{cp:04X}" for cp in sorted(uncovered)), file=sys.stderr)

    print(f"[subset] 共引用 {len(used)} 个码位; "
          + "; ".join(f"{label} {kept}/{total} {size}" for label, kept, total, size in rows))
    if failed:
        return 1
    print("[subset] OK" if args.check else "[subset] 已生成,记得把 Assets/Fonts 下的 ttf 一起提交")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

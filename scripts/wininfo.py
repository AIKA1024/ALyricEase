# wininfo.py —— 记录真实运行的 ALyricEase 窗口的几何 / DPI / 前台归属,每秒一条,
# 与 scripts/gpuwatch.ps1 配对使用(两个文件按墙钟对齐即可切分"静止段"与"滚动段")。
#
# 为什么需要它(2026-10-08 花了一轮才买到这条教训):
#   ① 尺寸是 GPU 归因的**关键自变量** —— 每帧开销 ∝ 窗口像素面积(docs/perf-notes.md §十),
#      所以"用户那 X% 是在多宽的窗口上量到的"必须实测,不能靠 state.json 猜(它只在干净退出时写)。
#   ② 更阴的一条:按 perf-notes §一 的口径,**窗口被遮挡 / 最小化时 DWM 不合成,
#      `\GPU Engine(*)\Utilization Percentage` 必然恒 0** —— 于是"量不到"会长得跟"很省"一模一样。
#      gpuwatch.ps1 会喊话提醒,但它看不见"是谁挡的";本脚本的 is_foreground 列就是那份证据。
#
# 用法(纯 stdlib,不依赖 pywin32;本机安全策略禁用 Add-Type,所以用 ctypes 而不是 PowerShell):
#   python scripts/wininfo.py ALyricEase 90                      # 按窗口标题找进程
#   python scripts/wininfo.py 8432 90 out.csv                    # 按 PID,并落 CSV
#
# 读法:末行给出「客户区尺寸集合 / 前台占比 / dpi」。
#   前台占比 = 0% 而 GPU 采样全 0 ⇒ 那次采样作废(窗口被挡),不要当成"很省"。

import csv
import ctypes
import sys
import time
from ctypes import wintypes as wt

user32 = ctypes.WinDLL("user32", use_last_error=True)
dwmapi = ctypes.WinDLL("dwmapi", use_last_error=True)

WNDENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)


def _pid_of(hwnd):
    pid = wt.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    return pid.value


def _title_of(hwnd):
    n = user32.GetWindowTextLengthW(hwnd)
    buf = ctypes.create_unicode_buffer(n + 1)
    user32.GetWindowTextW(hwnd, buf, n + 1)
    return buf.value


def main_windows(pid):
    """该进程下所有可见顶层窗口 [(hwnd, title)]。"""
    found = []

    def cb(hwnd, _):
        if _pid_of(hwnd) == pid and user32.IsWindowVisible(hwnd):
            found.append((hwnd, _title_of(hwnd)))
        return True

    user32.EnumWindows(WNDENUMPROC(cb), 0)
    return found


def geometry(hwnd):
    wr = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(wr))
    cr = wt.RECT()
    user32.GetClientRect(hwnd, ctypes.byref(cr))
    # DWM 扩展边框才是无边框窗口的真实可见尺寸(GetWindowRect 会把不可见的阴影算进来)。
    eb = wt.RECT()
    try:
        dwmapi.DwmGetWindowAttribute(
            wt.HWND(hwnd), 9, ctypes.byref(eb), ctypes.sizeof(eb)
        )  # 9 = DWMWA_EXTENDED_FRAME_BOUNDS
    except OSError:
        eb = wr
    try:
        dpi = user32.GetDpiForWindow(wt.HWND(hwnd))
    except AttributeError:
        dpi = 0
    return {
        "win": (wr.left, wr.top, wr.right - wr.left, wr.bottom - wr.top),
        "client": (cr.right, cr.bottom),
        "dwm": (eb.left, eb.top, eb.right - eb.left, eb.bottom - eb.top),
        "dpi": dpi,
    }


def foreground_pid():
    hwnd = user32.GetForegroundWindow()
    return _pid_of(hwnd) if hwnd else 0


def resolve_pid(spec):
    """spec 是数字就当 pid;否则当成窗口标题子串,取第一个可见顶层窗口所属进程。"""
    if spec.isdigit():
        return int(spec)
    hit = []

    def cb(hwnd, _):
        if user32.IsWindowVisible(hwnd) and spec.lower() in _title_of(hwnd).lower():
            hit.append(_pid_of(hwnd))
        return True

    user32.EnumWindows(WNDENUMPROC(cb), 0)
    if not hit:
        raise SystemExit(f"找不到标题含 {spec!r} 的可见窗口;应用没跑或被最小化")
    return hit[0]


def main():
    if len(sys.argv) < 3:
        raise SystemExit("用法: python wininfo.py <pid|窗口标题子串> <秒数> [out.csv]")
    pid = resolve_pid(sys.argv[1])
    seconds = int(sys.argv[2])
    out = sys.argv[3] if len(sys.argv) > 3 else None

    wins = main_windows(pid)
    print(f"pid={pid} 可见顶层窗口 {len(wins)} 个:")
    for hwnd, title in wins:
        g = geometry(hwnd)
        print(
            f"  hwnd={hwnd} title={title!r} "
            f"win={g['win']} client={g['client']} dwm={g['dwm']} dpi={g['dpi']}"
        )
    if not wins:
        print("⚠ 没有可见顶层窗口 —— 应用没跑或被最小化")

    rows = []
    t0 = time.time()
    while time.time() - t0 < seconds:
        fg = foreground_pid()
        for hwnd, _title in main_windows(pid):
            g = geometry(hwnd)
            rows.append(
                {
                    "sec": round(time.time() - t0, 2),
                    "hwnd": hwnd,
                    "client_w": g["client"][0],
                    "client_h": g["client"][1],
                    "dpi": g["dpi"],
                    "is_foreground": int(fg == pid),
                }
            )
        time.sleep(1)

    if out and rows:
        with open(out, "w", newline="", encoding="utf-8") as f:
            w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
            w.writeheader()
            w.writerows(rows)
        print(f"几何采样已写到 {out}({len(rows)} 条)")

    if rows:
        fg_ratio = sum(r["is_foreground"] for r in rows) / len(rows)
        sizes = {f"{r['client_w']}x{r['client_h']}" for r in rows}
        print(f"客户区尺寸集合={sizes} 前台占比={fg_ratio:.0%} dpi={rows[0]['dpi']}")
        if fg_ratio == 0:
            print("⚠ 全程不在前台 ⇒ 窗口很可能被别的窗口挡着,GPU 计数器会恒 0,这次采样不可用")


if __name__ == "__main__":
    main()

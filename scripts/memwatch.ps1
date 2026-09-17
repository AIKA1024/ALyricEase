# memwatch.ps1 —— 对真实运行中的 ALyricEase 进程取样内存,用于人工复现"反复进歌手页再返回"这类场景。
#
# 存在的理由:探针(--memloop-real)只能覆盖它自己构造的导航路径,用户在真应用里做的手工操作
# 它测不到。这个脚本不碰应用、不需重新构建,只从外部按固定间隔采样本进程的
# 工作集 / 私有字节 / 句柄 / 线程 / GDI / USER 对象,最后给出"块最小值"趋势判断。
#
# 用法(先手动启动 ALyricEase.Windows.exe,再做你要复现的操作):
#   powershell -ExecutionPolicy Bypass -File scripts\memwatch.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\memwatch.ps1 -ProcessName 'ALyricEase*' -IntervalSec 3 -Minutes 4
#
# 桌面应用产物在:
#   src\ALyricEase.Windows\bin\Debug\net10.0-windows10.0.19041.0\ALyricEase.Windows.exe
# ⚠ 进程名是 ALyricEase.Windows,不是 ALyricEase —— 默认值用通配符 ALyricEase* 兼容两者。
#
# 读法(重要):
#   瞬时值会因"垃圾堆着还没被 GC"上下摆动 20~30MB,单看一行会误判成泄漏。
#   真正看的是**块最小值**:把时间轴切成 5 个采样一块取最小,得到"内存地板"。
#   地板单调抬升 = 有东西真的没被回收;地板来回摆 = 只是 GC 没来,不是泄漏。
#   脚本末尾会自动给出块最小值序列与线性斜率。

[CmdletBinding()]
param(
    [string] $ProcessName = 'ALyricEase*',
    [int]    $IntervalSec = 3,
    [double] $Minutes     = 3,
    [int]    $BlockSize   = 5,
    [string] $OutFile     = ''
)

$ErrorActionPreference = 'Stop'

Add-Type -Namespace Win32 -Name Gui -MemberDefinition @'
[DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
'@

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $proc) {
    Write-Host "找不到进程 $ProcessName。请先把应用跑起来:"
    Write-Host '  src\ALyricEase.Windows\bin\Debug\net10.0-windows10.0.19041.0\ALyricEase.Windows.exe'
    exit 1
}

Write-Host "开始监视 $($proc.ProcessName) (PID=$($proc.Id)):每 $IntervalSec 秒一次,共 $Minutes 分钟。"
Write-Host "现在开始做你要复现的操作(进歌手页 → 返回 → 再进 → 再返回……),动作尽量均匀。"
Write-Host ''

$rows = New-Object System.Collections.ArrayList
$deadline = (Get-Date).AddMinutes($Minutes)
$t0 = Get-Date

Write-Host ('{0,6}  {1,10}  {2,10}  {3,7}  {4,7}  {5,5}  {6,5}' -f '秒', '工作集MB', '私有MB', '句柄', '线程', 'GDI', 'USER')

while ((Get-Date) -lt $deadline) {
    $p = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    if ($null -eq $p) { Write-Host '进程已退出。'; break }
    $p.Refresh()

    $gdi = 0; $user = 0
    try {
        $gdi  = [Win32.Gui]::GetGuiResources($p.Handle, 0)
        $user = [Win32.Gui]::GetGuiResources($p.Handle, 1)
    } catch { }
    # Threads 在某些权限/时序下会抛,缺值按 -1 记,别让整个采样中断
    $thr = -1
    try { $thr = $p.Threads.Count } catch { }

    $sec = [int]((Get-Date) - $t0).TotalSeconds
    $ws  = [math]::Round($p.WorkingSet64 / 1MB, 1)
    $pv  = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)

    [void]$rows.Add([pscustomobject]@{ Sec = $sec; WS = $ws; Private = $pv; Handles = $p.HandleCount; Threads = $thr; GDI = $gdi; USER = $user })

    Write-Host ('{0,6}  {1,10}  {2,10}  {3,7}  {4,7}  {5,5}  {6,5}' -f $sec, $ws, $pv, $p.HandleCount, $thr, $gdi, $user)

    Start-Sleep -Seconds $IntervalSec
}

if ($rows.Count -lt $BlockSize) {
    Write-Host '采样太少,无法判断。把 -Minutes 调大或 -IntervalSec 调小。'
    exit 0
}

# 块最小值:每 BlockSize 个采样一块取最小 —— 只有"地板"抬升才说明内存真的留住了
function Get-BlockMins($values) {
    $out = @()
    for ($i = 0; $i + $BlockSize -le $values.Count; $i += $BlockSize) {
        $slice = $values[$i..($i + $BlockSize - 1)]
        $out += ($slice | Measure-Object -Minimum).Minimum
    }
    return $out
}

$privMins = Get-BlockMins ($rows | ForEach-Object { $_.Private })
$wsMins   = Get-BlockMins ($rows | ForEach-Object { $_.WS })

$summary = New-Object System.Collections.ArrayList
[void]$summary.Add('')
[void]$summary.Add("采样 $($rows.Count) 点,块大小 $BlockSize")
[void]$summary.Add('私有字节 块最小值: ' + (($privMins | ForEach-Object { [string][int]$_ }) -join '  '))
[void]$summary.Add('工作集   块最小值: ' + (($wsMins   | ForEach-Object { [string][int]$_ }) -join '  '))

$mono = $true
for ($i = 1; $i -lt $privMins.Count; $i++) {
    if ($privMins[$i] -lt $privMins[$i - 1]) { $mono = $false; break }
}
$delta = 0
if ($privMins.Count -ge 2) { $delta = $privMins[$privMins.Count - 1] - $privMins[0] }

$first = $rows[0]; $last = $rows[$rows.Count - 1]
[void]$summary.Add("句柄 $($first.Handles) -> $($last.Handles)   线程 $($first.Threads) -> $($last.Threads)   GDI $($first.GDI) -> $($last.GDI)   USER $($first.USER) -> $($last.USER)")
[void]$summary.Add('')

if ($mono -and $delta -gt ($BlockSize * 0.8)) {
    [void]$summary.Add("判定:地板单调抬升 +$([math]::Round($delta,1))MB —— 符合真实泄漏特征,请把上面的表发回来定位。")
} elseif ($delta -gt ($BlockSize * 0.8)) {
    [void]$summary.Add("判定:地板整体上移 $([math]::Round($delta,1))MB 但不单调(中间有回落)—— 更像原生分配器高水位/GC 没来,不一定是泄漏。")
} else {
    [void]$summary.Add('判定:地板基本平坦 —— 未观察到泄漏。')
}

$text = $summary -join [Environment]::NewLine
Write-Host $text

if ($OutFile -ne '') {
    $rows | Export-Csv -Path $OutFile -NoTypeInformation -Encoding UTF8
    Write-Host "原始采样已写到 $OutFile"
}

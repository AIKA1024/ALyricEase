# cpuwatch.ps1 —— 对真实运行中的 ALyricEase 进程取样 CPU,并**按线程**归因。
#
# 存在的理由:探针(--pl-cpu-real / --shell-cpu-real)只能在它自己搭的夹具里量。
# 用户报告"某个页面什么都不做也有 CPU"时,第一件要分清的事是**这些 CPU 花在哪个线程上**:
#   - UI 线程占多数 ⇒ 每帧的 UI 工作(布局/绑定/动画),去找"谁在每帧失效";
#   - 合成/渲染线程占多数 ⇒ 真的在出帧(有东西在动,或空转重绘);
#   - 线程池线程占多数 ⇒ 后台循环(解码/网络/写盘),页面只是"挂在那儿"。
# 三条路的修法完全不同,任务管理器里的一个百分数是分不出来的。本脚本不碰应用、不需重新构建。
#
# 用法(先手动启动应用,再复现你的操作):
#   powershell -ExecutionPolicy Bypass -File scripts\cpuwatch.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\cpuwatch.ps1 -IntervalSec 3 -Seconds 90
#   powershell -ExecutionPolicy Bypass -File scripts\cpuwatch.ps1 -Note '歌单页滚到底后静止不动'
#
# 桌面应用产物在:
#   src\ALyricEase.Windows\bin\Debug\net10.0-windows10.0.19041.0\ALyricEase.Windows.exe
# ⚠ 进程名是 ALyricEase.Windows,不是 ALyricEase —— 默认值用通配符 ALyricEase* 兼容两者。
#
# 读法(重要):
#   本脚本报的是**单核百分比**(总 CPU 秒 / 墙钟 / 1 核),与探针口径一致。
#   任务管理器显示的是**全机百分比**(要再除以逻辑处理器数),同一次运行两个数看起来会差好几倍,
#   这很正常,别拿两边直接比。
#   另外:窗口被遮挡/最小化时合成器不出帧,CPU 会自然掉到 0 —— 采样期间请让窗口保持**可见且未被遮挡**,
#   否则量到的是"看不见时的零",不是你要的那个数。

[CmdletBinding()]
param(
    [string] $ProcessName = 'ALyricEase*',
    [int]    $IntervalSec = 3,
    [int]    $Seconds     = 60,
    [int]    $TopThreads  = 6,
    [string] $Note        = '',
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

$cores = [Environment]::ProcessorCount
Write-Host "开始监视 $($proc.ProcessName) (PID=$($proc.Id)):$IntervalSec 秒一次,共 $Seconds 秒,逻辑核 $cores 个。"
if ($Note -ne '') { Write-Host "备注:$Note" }
Write-Host '采样期间请让窗口保持可见且未被遮挡,并做你要复现的动作(之后停下来别动)。'
Write-Host ''

# 线程 CPU 的差分表:线程可能在采样期创建/退出,所以按 Id 累积"上一次读数"。
$threadPrev = @{}
$threadBusy = @{}      # Id -> 累计 CPU 秒
$threadPeak = @{}      # Id -> 单窗口最大百分比
$firstSeen  = @{}      # Id -> 第一次看到的序号

function Read-Threads($p) {
    $map = @{}
    try {
        foreach ($t in $p.Threads) {
            try { $map[[int]$t.Id] = $t.TotalProcessorTime.TotalSeconds } catch { }
        }
    } catch { }
    return $map
}

$rows = New-Object System.Collections.ArrayList
$t0 = Get-Date
$deadline = $t0.AddSeconds($Seconds)
$prevCpu = $proc.TotalProcessorTime.TotalSeconds
$prevWall = $t0
$prevThreads = Read-Threads $proc
$step = 0

Write-Host ('{0,5}  {1,9}  {2,9}  {3,9}  {4,11}  {5,7}  {6,5}  {7,5}' -f '秒', 'CPU%单核', '工作集MB', '私有MB', '本窗口最忙线程', '句柄', 'GDI', 'USER')

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds $IntervalSec

    $p = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    if ($null -eq $p) { Write-Host '进程已退出。'; break }
    $p.Refresh()

    $now = Get-Date
    $cpuNow = $p.TotalProcessorTime.TotalSeconds
    $wall = ($now - $prevWall).TotalSeconds
    if ($wall -le 0) { $wall = $IntervalSec }
    $cpuPercent = ($cpuNow - $prevCpu) / $wall * 100

    $nowThreads = Read-Threads $p
    $busiestId = -1; $busiest = 0
    foreach ($id in $nowThreads.Keys) {
        if (-not $prevThreads.ContainsKey($id)) {
            $firstSeen[$id] = $step
            continue
        }
        $delta = $nowThreads[$id] - $prevThreads[$id]
        if ($delta -lt 0) { continue }
        if (-not $threadBusy.ContainsKey($id)) { $threadBusy[$id] = 0.0; $threadPeak[$id] = 0.0 }
        $threadBusy[$id] += $delta
        $pct = $delta / $wall * 100
        if ($pct -gt $threadPeak[$id]) { $threadPeak[$id] = $pct }
        if ($pct -gt $busiest) { $busiest = $pct; $busiestId = $id }
    }

    $gdi = 0; $user = 0
    try {
        $gdi  = [Win32.Gui]::GetGuiResources($p.Handle, 0)
        $user = [Win32.Gui]::GetGuiResources($p.Handle, 1)
    } catch { }
    $thr = -1
    try { $thr = $p.Threads.Count } catch { }

    $sec = [int]($now - $t0).TotalSeconds
    $ws = [math]::Round($p.WorkingSet64 / 1MB, 1)
    $pv = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
    $busyText = if ($busiestId -gt 0) { "线程$busiestId=$([math]::Round($busiest,2))%" } else { '(无活动线程)' }

    [void]$rows.Add([pscustomobject]@{
        Sec = $sec; CpuPercent = [math]::Round($cpuPercent, 2); WS = $ws; Private = $pv
        Handles = $p.HandleCount; Threads = $thr; GDI = $gdi; USER = $user; Busiest = $busiestId
    })

    Write-Host ('{0,5}  {1,9}  {2,9}  {3,9}  {4,11}  {5,7}  {6,5}  {7,5}' -f `
        $sec, ([math]::Round($cpuPercent, 2)), $ws, $pv, $busyText, $p.HandleCount, $gdi, $user)

    $prevCpu = $cpuNow
    $prevWall = $now
    $prevThreads = $nowThreads
    $step++
}

if ($rows.Count -eq 0) { Write-Host '没有采到样本。'; exit 0 }

$cpus = $rows | ForEach-Object { $_.CpuPercent }
$totalSeconds = ($rows | ForEach-Object { $_.Sec })[-1]

# 平均值与中位数分开报:偶发的场景切换尖峰会把平均值拉高,中位数才是"稳态"。
# 判定用中位数 —— 一次页面重建的尾巴不该被读成"一直在占"。
$sorted = $cpus | Sort-Object
$average = ($cpus | Measure-Object -Average).Average
$median = $sorted[[int]([math]::Floor($sorted.Count / 2))]
$minimum = $sorted[0]
$maximum = $sorted[$sorted.Count - 1]

$summary = New-Object System.Collections.ArrayList
[void]$summary.Add('')
[void]$summary.Add("采样 $($rows.Count) 点 / $totalSeconds 秒(PID $($proc.Id),逻辑核 $cores)")
[void]$summary.Add("CPU 单核百分比: 中位 $([math]::Round($median, 2))%  平均 $([math]::Round($average, 2))%  最小 $([math]::Round($minimum, 2))%  最大 $([math]::Round($maximum, 2))%")
[void]$summary.Add("换算成任务管理器口径(全机百分比):大致 中位 $([math]::Round($median / $cores, 2))%")
[void]$summary.Add('')

[void]$summary.Add('按线程归因(整段累计,占总墙钟的百分比):')
$rank = $threadBusy.GetEnumerator() | Sort-Object -Property Value -Descending | Select-Object -First $TopThreads
foreach ($entry in $rank) {
    $share = if ($totalSeconds -gt 0) { $entry.Value / $totalSeconds * 100 } else { 0 }
    $peak = $threadPeak[$entry.Key]
    [void]$summary.Add(('  线程 {0,-7} 累计 {1,8:F2}s  占墙钟 {2,6:F2}%  单窗口峰值 {3,6:F2}%' -f $entry.Key, $entry.Value, $share, $peak))
}
if ($rank.Count -eq 0) { [void]$summary.Add('  (整段没有任何线程产生可测的 CPU —— 即空闲)') }
[void]$summary.Add('')

$first = $rows[0]; $last = $rows[$rows.Count - 1]
[void]$summary.Add("句柄 $($first.Handles) -> $($last.Handles)   线程 $($first.Threads) -> $($last.Threads)   GDI $($first.GDI) -> $($last.GDI)   USER $($first.USER) -> $($last.USER)")
[void]$summary.Add("工作集 $($first.WS)MB -> $($last.WS)MB   私有 $($first.Private)MB -> $($last.Private)MB")
[void]$summary.Add('')

if ($median -lt 1.0) {
    [void]$summary.Add('判定:整段基本空闲(<1% 单核)。若你正看着任务管理器报"有占用",先确认采样期间窗口可见未被遮挡。')
} else {
    $topId = ($rank | Select-Object -First 1).Key
    [void]$summary.Add("判定:整段有明显占用(中位 $([math]::Round($median,2))% 单核),最忙线程是 $topId。")
    [void]$summary.Add('把上面这张表连同"当时在哪个页面、在干什么"一起发回来 —— 线程号能直接指出是 UI / 渲染 / 线程池哪一路。')
}

$text = $summary -join [Environment]::NewLine
Write-Host $text

if ($OutFile -ne '') {
    $rows | Export-Csv -Path $OutFile -NoTypeInformation -Encoding UTF8
    Write-Host "原始采样已写到 $OutFile"
}

# gpuwatch.ps1 —— 对真实运行中的 ALyricEase 进程取样 **GPU 占用**,口径与探针里的 GpuUsageSampler 一致。
#
# 存在的理由:scripts/cpuwatch.ps1 只能按线程归因 CPU。而"什么都不干也有占用"这类报告里,
# CPU 与 GPU 的归因路径完全不同 —— CPU 高要看线程(UI / 渲染 / 线程池),
# GPU 高要看"到底有没有在出帧"。探针里的 GPU 采样器只在它自己搭的夹具里有效,
# 量不了用户真实运行的那个窗口,本脚本补这一格。
#
# 口径(与 docs/perf-notes.md §一 一致):
#   - 计数器 \GPU Engine(*)\Utilization Percentage,按 pid_ 过滤、**加总所有引擎**
#     (3D / Copy / Compute / VideoDecode),不是只看 3D。
#   - 速率型计数器要收集两次才有值 ⇒ **丢弃第一个采样**。
#   - ⚠ **只在窗口可见且未被遮挡时才有值**。被遮挡/最小化时 DWM 不合成,读数恒 0 ——
#     这种"数字很漂亮"的假象比崩溃更难查,所以整段全 0 时本脚本会直接把话喊出来。
#
# 用法(先手动启动应用,复现你的操作,然后让它静止别动):
#   powershell -ExecutionPolicy Bypass -File scripts\gpuwatch.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\gpuwatch.ps1 -IntervalSec 2 -Seconds 40 -Note '开机后什么都不干'
#
# 读法:
#   报的是**本进程占 GPU 的百分比**,不是"整机 GPU 占用"(别的程序也在用 GPU)。
#   要判"某个改动省没省",只能同轮配对/交替 —— 跨时段的绝对值会被外部负载整体抬高。

[CmdletBinding()]
param(
    [string] $ProcessName = 'ALyricEase*',
    [int]    $IntervalSec = 3,
    [int]    $Seconds     = 60,
    [string] $Note        = '',
    [string] $OutFile     = ''
)

$ErrorActionPreference = 'Stop'

# 兼容两种宿主:直接跑 exe(ALyricEase.Windows)或 dotnet 跑 dll(见 cpuwatch.ps1 头注)。
$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $proc) {
    Write-Host "找不到进程 $ProcessName。请先把应用跑起来:"
    Write-Host '  src\ALyricEase.Windows\bin\Debug\net10.0-windows10.0.19041.0\ALyricEase.Windows.exe'
    exit 1
}

$pidTag = "pid_$($proc.Id)_"
Write-Host "开始监视 $($proc.ProcessName) (PID=$($proc.Id)) GPU:$IntervalSec 秒一次,共 $Seconds 秒。"
if ($Note -ne '') { Write-Host "备注:$Note" }
Write-Host '采样期间请让窗口保持**可见且未被遮挡**,并停止操作。'
Write-Host ''

$rows = New-Object System.Collections.ArrayList
$t0 = Get-Date
$deadline = $t0.AddSeconds($Seconds)
$first = $true

Write-Host ('{0,5}  {1,10}  {2,7}' -f '秒', 'GPU%', '引擎数')

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds $IntervalSec

    if (-not (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue)) { Write-Host '进程已退出。'; break }

    $sum = 0.0
    $engines = 0
    try {
        $samples = (Get-Counter '\GPU Engine(*)\Utilization Percentage' -ErrorAction Stop).CounterSamples
        foreach ($s in $samples) {
            if ($s.InstanceName -like "*$pidTag*") {
                $engines++
                $sum += [double]$s.CookedValue
            }
        }
    } catch {
        Write-Host "读 GPU 计数器失败:$($_.Exception.Message)"
    }

    # 速率型计数器第一次收集只是"建立基线",值不可用。
    if ($first) { $first = $false; continue }

    $sec = [int]((Get-Date) - $t0).TotalSeconds
    [void]$rows.Add([pscustomobject]@{ Sec = $sec; GpuPercent = [math]::Round($sum, 2); Engines = $engines })
    Write-Host ('{0,5}  {1,10}  {2,7}' -f $sec, ([math]::Round($sum, 2)), $engines)
}

if ($rows.Count -eq 0) { Write-Host '没有采到样本。'; exit 0 }

$gpus = $rows | ForEach-Object { $_.GpuPercent }
$sorted = $gpus | Sort-Object
$average = ($gpus | Measure-Object -Average).Average
$median = $sorted[[int]([math]::Floor($sorted.Count / 2))]
$minimum = $sorted[0]
$maximum = $sorted[$sorted.Count - 1]

$summary = New-Object System.Collections.ArrayList
[void]$summary.Add('')
[void]$summary.Add("采样 $($rows.Count) 点 / $($rows[-1].Sec) 秒(PID $($proc.Id))")
[void]$summary.Add("GPU 百分比: 中位 $([math]::Round($median, 2))%  平均 $([math]::Round($average, 2))%  最小 $([math]::Round($minimum, 2))%  最大 $([math]::Round($maximum, 2))%")
[void]$summary.Add('')

if ($maximum -le 0.0) {
    [void]$summary.Add('判定:**整段全是 0**,先别当成"很省" —— 按口径,窗口被遮挡或最小化时')
    [void]$summary.Add('DWM 不合成,这个计数器必然恒 0。请把窗口拉到最前、确认可见未遮挡后重测。')
} elseif ($median -ge 1.0) {
    [void]$summary.Add("判定:静息时 GPU 有明显占用(中位 $([math]::Round($median, 2))%)⇒ 确实在**按帧出帧**。")
    [void]$summary.Add('下一步查"谁在驱动出帧":合成动画(挂上就不会自己停)、RAF 自续订循环、')
    [void]$summary.Add('框架主题动画 —— 三类的最小成本模型见 docs/perf-notes.md §三。')
} else {
    [void]$summary.Add('判定:静息时 GPU 占用很低(<1%)。')
}

$text = $summary -join [Environment]::NewLine
Write-Host $text

if ($OutFile -ne '') {
    $rows | Export-Csv -Path $OutFile -NoTypeInformation -Encoding UTF8
    Write-Host "原始采样已写到 $OutFile"
}

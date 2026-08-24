<#
    ALyricEase NativeAOT publish script (Rider run config + CLI).
    NOTE: keep this file ASCII-only so Windows PowerShell 5.1 parses it correctly.

    Usage:
        powershell -File scripts\publish-aot.ps1 [-Configuration Release] [-Runtime win-x64] [-Output <dir>]
            [-SkipAndroid] [-AndroidNoAot]

    Desktop:
        Produces a self-contained single-file exe under artifacts\aot-<RID> via
        PublishAot=true. The csproj already carries PublishAot-only props
        (IsAotCompatible / JsonSerializerIsReflectionEnabledByDefault=false /
        TrimmerRoots). First run downloads the ILCompiler packs, so it takes a while.

    Android:
        Also publishes the Android app to an APK under artifacts\aot-android
        (or artifacts\android when -AndroidNoAot). Android AOT uses the Android
        toolchain (RunAOTCompilation=true, IL -> native via LLVM) -- this is NOT
        .NET NativeAOT, which is incompatible with the Android/Java interop used
        by Avalonia.Android. Requires a JDK 17+ (JAVA_HOME, or a JDK found in the
        usual locations; Rider's bundled JBR under D:\Software\JetBrains* works).
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = "",
    [switch]$SkipAndroid = $false,
    [switch]$AndroidNoAot = $false
)
$ErrorActionPreference = "Stop"

function Resolve-Jdk {
    if ($env:JAVA_HOME -and (Test-Path (Join-Path $env:JAVA_HOME "bin\java.exe"))) {
        return $env:JAVA_HOME
    }
    $cmd = Get-Command java -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source) {
        return (Split-Path -Parent (Split-Path -Parent $cmd.Source))
    }
    $roots = @(
        (Join-Path $env:LOCALAPPDATA "Programs\JetBrains\Rider*\jbr"),
        (Join-Path $env:ProgramFiles "JetBrains\Rider*\jbr"),
        "D:\Software\JetBrains*\jbr",
        (Join-Path $env:ProgramFiles "Microsoft\jdk-*"),
        (Join-Path $env:ProgramFiles "Eclipse Adoptium\jdk-*"),
        (Join-Path $env:LOCALAPPDATA "Programs\Android Studio\jbr"),
        (Join-Path $env:ProgramFiles "Android\Android Studio\jbr")
    )
    foreach ($root in $roots) {
        $hit = Get-ChildItem -Path $root -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending | Select-Object -First 1
        if ($hit -and (Test-Path (Join-Path $hit.FullName "bin\java.exe"))) {
            return $hit.FullName
        }
    }
    return ""
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\ALyricEase.Desktop\ALyricEase.Desktop.csproj"
if (-not $Output) { $Output = Join-Path $repoRoot "artifacts\aot-$Runtime" }

# NativeAOT's findvcvarsall.bat CALLs vcvarsall.bat, which probes vswhere.exe by
# bare name. Prepend the VS Installer dir to PATH so that probe succeeds; otherwise
# the linker path gets polluted and the link step fails with "vswhere not recognized".
$vsInstaller = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) "Microsoft Visual Studio\Installer"
if ((Test-Path (Join-Path $vsInstaller "vswhere.exe")) -and ($env:PATH -notlike "*$vsInstaller*")) {
    $env:PATH = "$vsInstaller;$env:PATH"
    Write-Host "   Prepended VS Installer dir to PATH (vswhere)" -ForegroundColor DarkGray
}

Write-Host "== NativeAOT publish: $Configuration / $Runtime" -ForegroundColor Cyan
Write-Host "   Project : $project"
Write-Host "   Output  : $Output"

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishAot=true `
    -p:DebugType=None `
    -o $Output

if ($LASTEXITCODE -ne 0) { throw "AOT publish failed (exit code $LASTEXITCODE)" }

$exe = Join-Path $Output "ALyricEase.Desktop.exe"
if (Test-Path $exe) {
    Write-Host "== OK: $exe" -ForegroundColor Green
}
else {
    Write-Host "== Published, but $exe not found. Check the output dir." -ForegroundColor Yellow
}

# ---- Android APK ----
if (-not $SkipAndroid) {
    $androidProject = Join-Path $repoRoot "src\ALyricEase.Android\ALyricEase.Android.csproj"
    $androidOutput = if ($AndroidNoAot) { Join-Path $repoRoot "artifacts\android" } else { Join-Path $repoRoot "artifacts\aot-android" }

    $jdk = Resolve-Jdk
    if (-not $jdk) {
        throw "No JDK found. Set JAVA_HOME to a JDK 17+ (or -SkipAndroid to skip the Android build)."
    }
    Write-Host "== Android publish: $Configuration (JDK: $jdk)" -ForegroundColor Cyan
    $env:JAVA_HOME = $jdk
    if ($env:PATH -notlike "$jdk*") { $env:PATH = "$jdk\bin;$env:PATH" }

    [string[]]$aotArgs = @()
    if (-not $AndroidNoAot) { $aotArgs += "-p:RunAOTCompilation=true" }
    Write-Host "   AOT: $(if ($AndroidNoAot) { 'off (JIT APK)' } else { 'on (RunAOTCompilation=true)' })" -ForegroundColor DarkGray

    dotnet publish $androidProject `
        -c $Configuration `
        -f net10.0-android `
        @aotArgs `
        -o $androidOutput

    if ($LASTEXITCODE -ne 0) { throw "Android publish failed (exit code $LASTEXITCODE)" }

    $apk = Get-ChildItem -Path $androidOutput -Filter "*.apk" | Where-Object { $_.Name -like "*-Signed.apk" } | Select-Object -First 1
    if (-not $apk) { $apk = Get-ChildItem -Path $androidOutput -Filter "*.apk" | Select-Object -First 1 }
    if ($apk) {
        Write-Host "== OK: $($apk.FullName) ($([math]::Round($apk.Length / 1MB, 1)) MB)" -ForegroundColor Green
    }
    else {
        Write-Host "== Published, but no .apk found. Check the output dir." -ForegroundColor Yellow
    }
}

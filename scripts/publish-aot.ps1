<#
    ALyricEase NativeAOT publish script (Rider run config + CLI).
    NOTE: keep this file ASCII-only so Windows PowerShell 5.1 parses it correctly.

    Usage:
        powershell -File scripts\publish-aot.ps1 [-Configuration Release] [-Runtime win-x64] [-Output <dir>]
            [-SkipAndroid] [-AndroidNoAot] [-AndroidNativeAot]

    Desktop:
        Produces a self-contained single-file exe under artifacts\aot-<RID> via
        PublishAot=true. The csproj already carries PublishAot-only props
        (IsAotCompatible / JsonSerializerIsReflectionEnabledByDefault=false /
        TrimmerRoots). First run downloads the ILCompiler packs, so it takes a while.

    Android:
        Also publishes per-ABI Android APKs under artifacts\aot-android-per-abi
        (or artifacts\android-per-abi when -AndroidNoAot).
        Default mode uses the Mono toolchain: RunAOTCompilation=true compiles all
        IL to native ahead of time (full Mono AOT) -- this is NOT .NET NativeAOT.
        It builds arm64-v8a and armeabi-v7a separately so each APK contains only
        the native libraries needed by its target Android userspace. Final APK
        names include the ABI, for example ALyricEase-arm64-v8a-Signed.apk.
        With -AndroidNativeAot the script passes PublishAot=true targeting
        android-arm64. KNOWN BROKEN WITH AVALONIA (tested 2026-08): the build
        succeeds but the app dies on the splash screen with UnsatisfiedLinkError
        ("No implementation found ... n_onCreate") -- the .NET Android NativeAOT
        pipeline never generates/registers marshal-methods callbacks for
        library assemblies like Avalonia.Android. Microsoft also documents
        Android NativeAOT as "Experimental, no built-in Java interop". Kept only
        for future retries (dotnet/android or Avalonia may fix this); use the
        default Mono full-AOT path for real deployments.
        Requires a JDK 17+ (JAVA_HOME, or a JDK found in the usual locations;
        Rider's bundled JBR under D:\Software\JetBrains* works).
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = "",
    [switch]$SkipAndroid = $false,
    [switch]$AndroidNoAot = $false,
    [switch]$AndroidNativeAot = $false
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

function Get-ApkNativeAbis {
    param([Parameter(Mandatory = $true)][string]$ApkPath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ApkPath)
    try {
        return @(
            $archive.Entries |
                ForEach-Object {
                    if ($_.FullName -match '^lib/([^/]+)/[^/]+[.]so$') {
                        $Matches[1]
                    }
                } |
                Sort-Object -Unique
        )
    }
    finally {
        $archive.Dispose()
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\ALyricEase.Windows\ALyricEase.Windows.csproj"
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

$exe = Join-Path $Output "ALyricEase.Windows.exe"
if (Test-Path $exe) {
    Write-Host "== OK: $exe" -ForegroundColor Green
}
else {
    Write-Host "== Published, but $exe not found. Check the output dir." -ForegroundColor Yellow
}

# ---- Android APK ----
if (-not $SkipAndroid) {
    if ($AndroidNoAot -and $AndroidNativeAot) {
        throw "-AndroidNoAot and -AndroidNativeAot are mutually exclusive."
    }

    $androidProject = Join-Path $repoRoot "src\ALyricEase.Android\ALyricEase.Android.csproj"

    $androidTargets = @(
        [pscustomobject]@{ Rid = "android-arm64"; Abi = "arm64-v8a" },
        [pscustomobject]@{ Rid = "android-arm"; Abi = "armeabi-v7a" }
    )
    [string[]]$runtimeArgs = @()
    [string[]]$aotArgs = @()
    if ($AndroidNativeAot) {
        $androidOutputRoot = Join-Path $repoRoot "artifacts\nativeaot-android"
        $androidTargets = @([pscustomobject]@{ Rid = "android-arm64"; Abi = "arm64-v8a" })
        $runtimeArgs = @("-r", "android-arm64")
        $aotArgs = @(
            "--self-contained", "true",
            "-p:PublishAot=true"
        )
        Write-Host "   WARNING: Android NativeAOT is known-broken with Avalonia.Android" -ForegroundColor Red
        Write-Host "   (UnsatisfiedLinkError at Application.OnCreate). Building anyway..." -ForegroundColor Red
    }
    elseif ($AndroidNoAot) {
        $androidOutputRoot = Join-Path $repoRoot "artifacts\android-per-abi"
        Write-Host "   AOT: off (per-ABI JIT APKs)" -ForegroundColor DarkGray
    }
    else {
        # Best available production mode: one full Mono AOT APK per ARM ABI.
        $androidOutputRoot = Join-Path $repoRoot "artifacts\aot-android-per-abi"
        $aotArgs = @(
            "--self-contained", "true",
            "-p:RunAOTCompilation=true",
            "-p:AndroidStripILAfterAOT=true"
        )
        Write-Host "   AOT: on (per-ABI Mono full AOT, IL stripped)" -ForegroundColor DarkGray
    }

    $jdk = Resolve-Jdk
    if (-not $jdk) {
        throw "No JDK found. Set JAVA_HOME to a JDK 17+ (or -SkipAndroid to skip the Android build)."
    }
    Write-Host "== Android publish: $Configuration (JDK: $jdk)" -ForegroundColor Cyan
    $env:JAVA_HOME = $jdk
    if ($env:PATH -notlike "$jdk*") { $env:PATH = "$jdk\bin;$env:PATH" }

    if ($AndroidNativeAot) {
        # NativeAOT links through the NDK's clang; locate it in the usual spots.
        [string]$ndk = $env:ANDROID_NDK_ROOT
        $clangExe = "toolchains\llvm\prebuilt\windows-x86_64\bin\clang.exe"
        if (-not $ndk -or -not (Test-Path (Join-Path $ndk $clangExe))) {
            $sdkCandidates = @("$env:LOCALAPPDATA\Android\Sdk\ndk\*")
            if ($env:ANDROID_HOME) { $sdkCandidates += "$env:ANDROID_HOME\ndk\*" }
            $sdkCandidates += "C:\Program Files (x86)\Android\android-sdk\ndk\*"
            $ndkHit = Get-ChildItem -Path $sdkCandidates -Directory -ErrorAction SilentlyContinue |
                Where-Object { Test-Path (Join-Path $_.FullName $clangExe) } |
                Sort-Object Name -Descending | Select-Object -First 1
            if ($ndkHit) { $ndk = $ndkHit.FullName }
        }
        if (-not $ndk -or -not (Test-Path (Join-Path $ndk $clangExe))) {
            throw ("NDK not found (required for -AndroidNativeAot). Install one with:`n" +
                   "  sdkmanager --sdk_root=`"$env:LOCALAPPDATA\Android\Sdk`" --install `"ndk;28.2.13676358`"")
        }
        Write-Host "   NDK: $ndk" -ForegroundColor DarkGray
        $env:ANDROID_NDK_ROOT = $ndk
        $ndkBin = Join-Path $ndk "toolchains\llvm\prebuilt\windows-x86_64\bin"
        if (($env:PATH -split ';') -notcontains $ndkBin) { $env:PATH = "$ndkBin;$env:PATH" }
    }

    $publishedApks = @()
    foreach ($target in $androidTargets) {
        $androidOutput = Join-Path $androidOutputRoot $target.Abi
        [string[]]$targetArgs = @("-p:AndroidTargetAbi=$($target.Abi)")
        if ($AndroidNativeAot) { $targetArgs = $runtimeArgs }
        Write-Host "-- Publishing $($target.Abi) ($($target.Rid))" -ForegroundColor Cyan

        dotnet publish $androidProject `
            -c $Configuration `
            -f net10.0-android `
            @targetArgs `
            @aotArgs `
            -o $androidOutput

        if ($LASTEXITCODE -ne 0) {
            throw "Android $($target.Abi) publish failed (exit code $LASTEXITCODE)"
        }

        $finalApkName = "ALyricEase-$($target.Abi)-Signed.apk"
        $apk = Get-ChildItem -Path $androidOutput -Filter "*-Signed.apk" |
            Where-Object { $_.Name -ne $finalApkName } |
            Select-Object -First 1
        if (-not $apk) { $apk = Get-ChildItem -Path $androidOutput -Filter "*.apk" | Select-Object -First 1 }
        if (-not $apk) {
            throw "Android $($target.Abi) publish completed, but no APK was found in $androidOutput"
        }

        $apkAbis = @(Get-ApkNativeAbis -ApkPath $apk.FullName)
        if ($apkAbis.Count -ne 1 -or $apkAbis[0] -ne $target.Abi) {
            throw "APK ABI verification failed for $($target.Abi). Found: $($apkAbis -join ', ')"
        }

        $finalApkPath = Join-Path $androidOutput $finalApkName
        if ($apk.FullName -ne $finalApkPath) {
            if (Test-Path -LiteralPath $finalApkPath) {
                Remove-Item -LiteralPath $finalApkPath -Force
            }
            Move-Item -LiteralPath $apk.FullName -Destination $finalApkPath
            $apk = Get-Item -LiteralPath $finalApkPath
        }
        Write-Host "   APK ABI: $($target.Abi)" -ForegroundColor DarkGray
        Write-Host "== OK: $($apk.FullName) ($([math]::Round($apk.Length / 1MB, 1)) MB)" -ForegroundColor Green
        $publishedApks += $apk
    }

    Write-Host "== Android packages ready:" -ForegroundColor Green
    foreach ($apk in $publishedApks) {
        Write-Host "   $($apk.FullName)" -ForegroundColor Green
    }
}

<#
    ALyricEase NativeAOT publish script (Rider run config + CLI).
    NOTE: keep this file ASCII-only so Windows PowerShell 5.1 parses it correctly.

    Usage:
        powershell -File scripts\publish-aot.ps1 [-Configuration Release] [-Runtime win-x64] [-Output <dir>]

    Produces a self-contained single-file exe under artifacts\aot-<RID>.
    The csproj already carries PublishAot-only props (IsAotCompatible /
    JsonSerializerIsReflectionEnabledByDefault=false / TrimmerRoots). First run
    downloads the ILCompiler packs, so it takes a while.
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = ""
)
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "ALyricEase\ALyricEase.csproj"
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

$exe = Join-Path $Output "ALyricEase.exe"
if (Test-Path $exe) {
    Write-Host "== OK: $exe" -ForegroundColor Green
}
else {
    Write-Host "== Published, but $exe not found. Check the output dir." -ForegroundColor Yellow
}

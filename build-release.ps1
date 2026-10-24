<#
.SYNOPSIS
    Build both release packages for one version.

.DESCRIPTION
    Output goes to Release\v<Version>\:
      tModManager-v<Version>-portable-win-x64.zip   folder build (exe + dlls + wwwroot)
      tModManager-v<Version>-single-win-x64.zip     single-file build (only tModManager.exe)

    Both archives contain the exe named exactly "tModManager.exe" at the archive root;
    the version number lives only in the archive name.

.EXAMPLE
    .\build-release.ps1 -Version 2.2.0
#>
param(
    [Parameter(Mandatory = $true)][string]$Version
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$script:proj = Join-Path $PSScriptRoot "src\TModManager.App\TModManager.App.csproj"
$relDir      = Join-Path $PSScriptRoot "Release\v$Version"
$workBase    = Join-Path ([System.IO.Path]::GetTempPath()) ("tmm_rel_" + [guid]::NewGuid().ToString("N"))

New-Item -ItemType Directory -Force -Path $relDir | Out-Null

function Invoke-Publish {
    param([string]$Out, [string[]]$Extra)

    $cmd = @("publish", $script:proj, "-c", "Release", "-r", "win-x64", "--self-contained", "false")
    if ($Extra) { $cmd += $Extra }
    $cmd += @("-o", $Out, "--nologo")

    & dotnet @cmd
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
}

try {
    # ---------- 1) portable (folder build) ----------
    $foldOut = Join-Path $workBase "portable"
    Invoke-Publish -Out $foldOut -Extra @()

    $portableZip = Join-Path $relDir "tModManager-v$Version-portable-win-x64.zip"
    Remove-Item $portableZip -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $foldOut "*") -DestinationPath $portableZip -CompressionLevel Optimal

    # ---------- 2) single-file build ----------
    $singleOut = Join-Path $workBase "single"
    Invoke-Publish -Out $singleOut -Extra @(
        "-p:PublishSingleFile=true",
        "-p:IncludeAllContentForSelfExtract=true",
        "-p:DebugType=none",
        "-p:DebugSymbols=false"
    )

    $innerExe = Join-Path $singleOut "tModManager.exe"
    if (-not (Test-Path $innerExe)) { throw "single-file publish did not produce tModManager.exe" }

    # Bail out if anything else was left next to it (means content files were NOT bundled)
    $leftover = @(Get-ChildItem $singleOut -Recurse -File | Where-Object { $_.Name -ne "tModManager.exe" })
    if ($leftover.Count -gt 0) {
        throw ("single-file publish left extra files: " + (($leftover | ForEach-Object { $_.Name }) -join ", "))
    }

    $singleZip = Join-Path $relDir "tModManager-v$Version-single-win-x64.zip"
    Remove-Item $singleZip -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path $innerExe -DestinationPath $singleZip -CompressionLevel Optimal

    # ---------- report ----------
    Write-Host ""
    foreach ($z in @($portableZip, $singleZip)) {
        $f = Get-Item $z
        $h = (Get-FileHash $z -Algorithm SHA256).Hash
        Write-Host ("{0}  {1:N0} bytes  SHA256 {2}" -f $f.Name, $f.Length, $h)
    }
}
finally {
    Remove-Item $workBase -Recurse -Force -ErrorAction SilentlyContinue
}

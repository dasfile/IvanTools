# =============================================================================
# IvanTools - build and release script
# =============================================================================
# Compiles every plugin .cs into its own DLL and lays out a release package:
#
#   dist\stage\IvanTools.addin
#   dist\stage\IvanTools\<Plugin>.dll
#   dist\stage\README.md
#   dist\stage\LICENSE
#   dist\IvanTools-<Version>.zip
#
# Examples:
#   .\build.ps1                          # build 1.0.0 into dist\
#   .\build.ps1 -Version 1.1.0           # build another version
#   .\build.ps1 -Deploy                  # build and copy into ProgramData
#
# Revit must be closed for -Deploy, because loaded DLLs are locked.
# =============================================================================

[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [string]$RevitDir = "C:\Program Files\Autodesk\Revit 2023",
    [switch]$Deploy,
    [string]$DeployRoot = "C:\ProgramData\Autodesk\Revit\Addins\2023"
)

$ErrorActionPreference = "Stop"

$root      = $PSScriptRoot
$framework = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$csc       = Join-Path $framework "csc.exe"
$dist      = Join-Path $root "dist"
$stage     = Join-Path $dist "stage"
$payload   = Join-Path $stage "IvanTools"
$obj       = Join-Path $root "obj"
$zip       = Join-Path $dist ("IvanTools-" + $Version + ".zip")

# One entry per plugin: source file -> output DLL
$plugins = @(
    @{ Source = "AdjustToFace.cs";     Dll = "AdjustToFace.dll" },
    @{ Source = "CsHotLoader.cs";      Dll = "CsHotLoader.dll" },
    @{ Source = "ExportToGlb.cs";      Dll = "ExportToGlb.dll" },
    @{ Source = "RoomCounter.cs";      Dll = "RoomCounter.dll" },
    @{ Source = "SheetsBatchRenum.cs"; Dll = "SheetsBatchRenum.dll" },
    @{ Source = "SplitWall.cs";        Dll = "SplitWall.dll" }
)

$references = @(
    (Join-Path $RevitDir "RevitAPI.dll"),
    (Join-Path $RevitDir "RevitAPIUI.dll"),
    (Join-Path $framework "System.dll"),
    (Join-Path $framework "System.Core.dll"),
    (Join-Path $framework "System.Xml.dll"),
    (Join-Path $framework "System.Drawing.dll"),
    (Join-Path $framework "System.Windows.Forms.dll"),
    (Join-Path $framework "Microsoft.CSharp.dll"),
    (Join-Path $framework "WPF\WindowsBase.dll"),
    (Join-Path $framework "WPF\PresentationCore.dll"),
    (Join-Path $framework "WPF\PresentationFramework.dll"),
    (Join-Path $framework "System.Xaml.dll")
)

# --- checks ---------------------------------------------------------------
if (-not (Test-Path -LiteralPath $csc)) { throw "csc.exe not found: $csc" }
if (-not (Test-Path -LiteralPath $RevitDir)) { throw "Revit folder not found: $RevitDir" }
foreach ($reference in $references) {
    if (-not (Test-Path -LiteralPath $reference)) { throw "Missing reference: $reference" }
}

# --- prepare folders ------------------------------------------------------
if (Test-Path -LiteralPath $dist)  { Remove-Item -LiteralPath $dist -Recurse -Force }
if (Test-Path -LiteralPath $obj)   { Remove-Item -LiteralPath $obj -Recurse -Force }
New-Item -ItemType Directory -Path $payload -Force | Out-Null

# --- generate assembly version -------------------------------------------
$parts = @($Version.Split("."))
while ($parts.Count -lt 4) { $parts += "0" }
$assemblyVersion = $parts[0..3] -join "."

$versionFile = Join-Path $obj "VersionInfo.cs"
New-Item -ItemType Directory -Path $obj -Force | Out-Null
@"
using System.Reflection;
[assembly: AssemblyTitle("IvanTools")]
[assembly: AssemblyProduct("IvanTools for Revit 2023")]
[assembly: AssemblyCompany("Ivan Rodionov")]
[assembly: AssemblyVersion("$assemblyVersion")]
[assembly: AssemblyFileVersion("$assemblyVersion")]
"@ | Set-Content -LiteralPath $versionFile -Encoding ASCII

# --- compile --------------------------------------------------------------
foreach ($plugin in $plugins) {
    $source = Join-Path $root $plugin.Source
    $output = Join-Path $payload $plugin.Dll

    if (-not (Test-Path -LiteralPath $source)) { throw "Source not found: $source" }

    $cscArgs = @("/nologo", "/warn:4", "/target:library", "/out:$output")
    foreach ($reference in $references) { $cscArgs += "/reference:$reference" }
    $cscArgs += $source
    $cscArgs += $versionFile

    Write-Host ("Compiling {0} -> {1}" -f $plugin.Source, $plugin.Dll)
    & $csc $cscArgs
    if ($LASTEXITCODE -ne 0) { throw ("Compilation failed: " + $plugin.Source) }
}

# --- package --------------------------------------------------------------
Copy-Item -LiteralPath (Join-Path $root "IvanTools.addin") -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $root "README.md")       -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $root "LICENSE")         -Destination $stage -Force

Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -Force

$hash = Get-FileHash -LiteralPath $zip -Algorithm SHA256
Write-Host ""
Write-Host ("Package : {0}" -f $zip)
Write-Host ("Version : {0} ({1})" -f $Version, $assemblyVersion)
Write-Host ("SHA256  : {0}" -f $hash.Hash)

# --- optional deployment --------------------------------------------------
if ($Deploy) {
    $targetDir = Join-Path $DeployRoot "IvanTools"
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null

    foreach ($file in Get-ChildItem -Path (Join-Path $payload "*.dll")) {
        $target = Join-Path $targetDir $file.Name
        try {
            Copy-Item -LiteralPath $file.FullName -Destination $target -Force -ErrorAction Stop
        }
        catch {
            # A loaded DLL cannot be overwritten, but it can be renamed.
            # The running process keeps the old file; Revit picks up the new one
            # after a restart.
            $backup = $target + "." + (Get-Date -Format "yyyyMMddHHmmss")
            Move-Item -LiteralPath $target -Destination $backup -Force -ErrorAction Stop
            Copy-Item -LiteralPath $file.FullName -Destination $target -Force -ErrorAction Stop
            Write-Warning ("{0} was locked; the old copy is saved as {1}. Restart Revit." -f $file.Name, (Split-Path $backup -Leaf))
        }
    }

    Copy-Item -Path (Join-Path $root "*.cs") -Destination $targetDir -Force
    Copy-Item -LiteralPath (Join-Path $root "IvanTools.addin") -Destination $DeployRoot -Force

    Write-Host ("Deployed to {0}" -f $DeployRoot)
}

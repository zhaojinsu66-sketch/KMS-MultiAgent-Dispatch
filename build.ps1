# Builds AgentCommander.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
# No downloads, no SDK. Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
param([string]$Out = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }
$out = if ($Out) { $Out } else { Join-Path $root 'AgentCommander.exe' }
$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$cscArgs = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001', '/warn:4',
    "/out:$out",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Xaml.dll',
    '/r:System.Web.Extensions.dll', '/r:System.Windows.Forms.dll', '/r:System.Management.dll', '/r:System.Drawing.dll',
    "/r:$fw\WPF\WindowsBase.dll", "/r:$fw\WPF\PresentationCore.dll", "/r:$fw\WPF\PresentationFramework.dll",
    "/r:$fw\WPF\WindowsFormsIntegration.dll",
    "/win32icon:$root\assets\kms.ico",
    "/resource:$root\assets\kms.png,kms.png",
    "/resource:$root\ui\MainWindow.xaml,MainWindow.xaml"
) + $sources
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)" }
Write-Output "Built $out"

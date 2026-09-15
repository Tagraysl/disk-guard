param([string]$OutputPath = 'DiskGuard-Desktop.exe')
$ErrorActionPreference = 'Stop'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF'
Push-Location $PSScriptRoot
& $compiler /nologo /target:exe /platform:x64 /out:IconMaker.exe /r:System.Drawing.dll IconMaker.cs
if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'Icon compilation failed' }
& .\IconMaker.exe
if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'Icon generation failed' }
& $compiler /nologo /target:winexe /platform:x64 /win32icon:Guard.ico /out:$OutputPath /r:"$wpf\PresentationFramework.dll" /r:"$wpf\PresentationCore.dll" /r:"$wpf\WindowsBase.dll" /r:System.Xaml.dll /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll Guard.cs StorageProtocolReader.cs MetricCatalog.cs AtaSmartReader.cs ManualSpecifications.cs
Remove-Item -LiteralPath IconMaker.exe -Force -ErrorAction SilentlyContinue
Pop-Location
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }

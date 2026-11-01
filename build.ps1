# Builds ClipVault.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
# No SDK or downloads needed:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw   = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf  = "$fw\WPF"
$out  = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

# App icon: the Fluent "paste" glyph on an accent tile, generated so no image files need to be stored.
$ico = Join-Path $out 'ClipVault.ico'
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $r = [Math]::Max(2, $s / 5); $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90); $path.AddArc($s - $r * 2 - 1, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($s - $r * 2 - 1, $s - $r * 2 - 1, $r * 2, $r * 2, 0, 90); $path.AddArc(0, $s - $r * 2 - 1, $r * 2, $r * 2, 90, 90)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0, 103, 192))), $path)
    $family = if ((New-Object System.Drawing.Text.InstalledFontCollection).Families.Name -contains 'Segoe Fluent Icons') { 'Segoe Fluent Icons' } else { 'Segoe MDL2 Assets' }
    $font = New-Object System.Drawing.Font $family, ($s * 0.6), ([System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $g.DrawString([string][char]0xE77F, $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, ($s * 0.04), $s, $s), $sf)
    $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose(); , $ms.ToArray()
}
$fs = [System.IO.File]::Create($ico); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()

$src = Join-Path $root 'src'
$csc = @(
    '-nologo', '-codepage:65001', '-target:winexe', '-platform:anycpu', '-optimize+', '-langversion:5',
    "-out:$out\ClipVault.exe",
    "-win32manifest:$src\app.manifest",
    "-win32icon:$ico",
    "-r:$wpf\PresentationCore.dll", "-r:$wpf\PresentationFramework.dll", "-r:$wpf\WindowsBase.dll", "-r:$wpf\UIAutomationClient.dll", "-r:$wpf\UIAutomationTypes.dll",
    "-r:$fw\System.Xaml.dll", '-r:System.dll', '-r:System.Core.dll', '-r:System.Xml.dll',
    '-r:System.Drawing.dll', '-r:System.Windows.Forms.dll', '-r:System.Runtime.Serialization.dll',
    "-resource:$src\Popup.xaml,Popup.xaml",
    "-resource:$src\Settings.xaml,Settings.xaml",
    "-resource:$src\Styles.xaml,Styles.xaml",
    "-resource:$src\Emojis.txt,Emojis.txt"
) + (Get-ChildItem "$src\*.cs" | ForEach-Object FullName)

& "$fw\csc.exe" @csc
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $out\ClipVault.exe" -ForegroundColor Green

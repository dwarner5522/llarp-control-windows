# Builds dist\LLARP-Control.exe: one self-installing exe with llarpctl, llarp_core.py, the WSL
# setup script and the opennet bootstrap inside. Needs only Windows (the .NET Framework 4.x
# C# compiler ships with it).
#
#   powershell -ExecutionPolicy Bypass -File build.ps1            -> dist\LLARP-Control.exe
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Preview   -> obj\LLARP-Control-preview.exe
#     (UI preview: no admin rights, --preview-welcome / --preview-main [tab] open windows directly)
param([string]$Out = "", [switch]$Preview)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$payloadDir = Join-Path $here "payload"
if (-not $Out) { $Out = Join-Path $here "dist" }
$work = Join-Path $here "obj"
New-Item -ItemType Directory -Force -Path $Out, $work | Out-Null

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw "csc.exe (.NET Framework 4.x) not found" }

# the llarp commit to build (keep in step with llarp-control's umbrel/Dockerfile)
$commit = (Get-Content (Join-Path $payloadDir "LLARP_COMMIT") -Raw).Trim()
if ($commit -notmatch '^[0-9a-f]{40}$') { throw "payload\LLARP_COMMIT must hold a full llarp commit hash" }

# icon: the diamond logo on its dark rounded square, PNG frames in an .ico
Add-Type -AssemblyName System.Drawing
function New-LogoPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"
    $bg = [System.Drawing.Color]::FromArgb(255, 0x19, 0x19, 0x19)
    $lime = [System.Drawing.Color]::FromArgb(255, 0x6f, 0xb3, 0x4c)
    $green = [System.Drawing.Color]::FromArgb(255, 0x12, 0x87, 0x41)
    $teal = [System.Drawing.Color]::FromArgb(255, 0x00, 0x62, 0x61)
    $r = [single]($size * 0.18); $d = 2 * $r; $s = [single]($size - 1)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($s - $d, 0, $d, $d, 270, 90)
    $path.AddArc($s - $d, $s - $d, $d, $d, 0, 90); $path.AddArc(0, $s - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush $bg), $path)
    $c = [single]($size / 2)
    $halo = New-Object System.Drawing.Drawing2D.GraphicsPath
    $halo.AddEllipse([single]($size * 0.04), [single]($size * 0.04), [single]($size * 0.92), [single]($size * 0.92))
    $hb = New-Object System.Drawing.Drawing2D.PathGradientBrush $halo
    $hb.CenterColor = [System.Drawing.Color]::FromArgb(110, $lime)
    $hb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, $lime))
    $g.FillPath($hb, $halo)
    $k = [single]($size * 0.34)
    $pts = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF $c, ($c - $k)), (New-Object System.Drawing.PointF ($c + $k), $c),
        (New-Object System.Drawing.PointF $c, ($c + $k)), (New-Object System.Drawing.PointF ($c - $k), $c))
    $lb = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF ($c - $k - 1), $c), (New-Object System.Drawing.PointF ($c + $k + 1), $c), $lime, $teal
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $blend.Colors = @($lime, $green, $teal); $blend.Positions = @([single]0, [single]0.5, [single]1)
    $lb.InterpolationColors = $blend
    $g.FillPolygon($lb, $pts)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}
$sizes = 16, 24, 32, 48, 64, 256
$frames = $sizes | ForEach-Object { ,(New-LogoPng $_) }
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $frames[$i].Length
    $w.Write([byte]($sz % 256)); $w.Write([byte]($sz % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($f in $frames) { $w.Write($f) }
$w.Flush()
$icon = Join-Path $work "llarp-control.ico"
[IO.File]::WriteAllBytes($icon, $ico.ToArray())

$payload = @{
    "llarp_core.py"  = Join-Path $payloadDir "llarp_core.py"
    "llarpctl"       = Join-Path $payloadDir "llarpctl"
    "wsl-setup.sh"   = Join-Path $payloadDir "wsl-setup.sh"
    "opennet.signed" = Join-Path $payloadDir "opennet.signed"
    "LLARP_COMMIT"   = Join-Path $payloadDir "LLARP_COMMIT"
}
$resources = $payload.GetEnumerator() | ForEach-Object {
    if (-not (Test-Path $_.Value)) { throw "missing $($_.Value)" }
    "/resource:`"$($_.Value)`",payload/$($_.Key)"
}

$exe = Join-Path $Out "LLARP-Control.exe"
$sources = Get-ChildItem (Join-Path $here "src") -Filter *.cs | ForEach-Object { "`"$($_.FullName)`"" }
$manifest = Join-Path $here "app.manifest"
if ($Preview) {
    # UI preview: runs without admin rights, opens windows directly (never ship this)
    $manifest = Join-Path $work "preview.manifest"
    (Get-Content (Join-Path $here "app.manifest") -Raw) -replace "requireAdministrator", "asInvoker" | Set-Content $manifest
    $exe = Join-Path $work "LLARP-Control-preview.exe"
}
$cscArgs = @(
    "/nologo", "/target:winexe", "/platform:anycpu", "/optimize+", "/codepage:65001",
    "/out:`"$exe`"", "/win32icon:`"$icon`"", "/win32manifest:`"$manifest`"",
    "/r:System.dll", "/r:System.Core.dll", "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll",
    "/r:System.Web.Extensions.dll", "/r:Microsoft.CSharp.dll"
) + $(if ($Preview) { @("/define:PREVIEW") } else { @() }) + $resources + $sources

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
Write-Host "==> built $exe ($([math]::Round((Get-Item $exe).Length / 1KB)) KB), llarp $($commit.Substring(0, 9))"

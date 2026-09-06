<#
  Generates the Inno Setup wizard artwork (branded sidebar + banner + icon) so the
  installer looks like part of the PC Rental product instead of the stock grey wizard.

  Output: windows-agent/Assets/{wizard-large.bmp, wizard-small.bmp, app.ico}
  These files are build artifacts (git-ignored); installer.iss picks them up via #ifexist.
#>
[CmdletBinding()]
param(
  [string]$OutputDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Assets')
)

$ErrorActionPreference = 'Stop'

# PowerShell 7 sometimes can't resolve the short name; fall back to System.Drawing.Common.
try { Add-Type -AssemblyName System.Drawing -ErrorAction Stop }
catch { Add-Type -AssemblyName System.Drawing.Common -ErrorAction Stop }

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$brandTop    = [System.Drawing.Color]::FromArgb(59, 130, 246)   # #3b82f6
$brandBottom = [System.Drawing.Color]::FromArgb(139, 92, 246)   # #8b5cf6
$ink         = [System.Drawing.Color]::White

function New-Gradient([int]$w, [int]$h) {
  $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.TextRenderingHint = 'ClearTypeGridFit'
  $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
  $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $brandTop, $brandBottom, 55.0)
  $g.FillRectangle($brush, $rect)
  $brush.Dispose()
  [pscustomobject]@{ Bitmap = $bmp; Graphics = $g }
}

function Add-MonitorGlyph($g, [single]$cx, [single]$cy, [single]$scale) {
  $screen = New-Object System.Drawing.RectangleF(($cx - 34 * $scale), ($cy - 24 * $scale), (68 * $scale), (46 * $scale))
  $pen = New-Object System.Drawing.Pen($ink, (3.2 * $scale))
  $g.DrawRectangle($pen, $screen.X, $screen.Y, $screen.Width, $screen.Height)
  $g.DrawLine($pen, $cx, ($screen.Bottom), $cx, ($screen.Bottom + 9 * $scale))
  $g.DrawLine($pen, ($cx - 14 * $scale), ($screen.Bottom + 9 * $scale), ($cx + 14 * $scale), ($screen.Bottom + 9 * $scale))
  $pen.Dispose()
}

function Save-Bmp24($bitmap, [string]$path) {
  $out = New-Object System.Drawing.Bitmap($bitmap.Width, $bitmap.Height, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
  $gg = [System.Drawing.Graphics]::FromImage($out)
  $gg.DrawImage($bitmap, 0, 0, $bitmap.Width, $bitmap.Height)
  $gg.Dispose()
  $out.Save($path, [System.Drawing.Imaging.ImageFormat]::Bmp)
  $out.Dispose()
}

# ---- Large sidebar (welcome / finished page), 2x of Inno's 164x314 baseline ----
$large = New-Gradient 328 628
try {
  Add-MonitorGlyph $large.Graphics 164 210 3.0
  $titleFont = New-Object System.Drawing.Font('Microsoft YaHei UI', 30, [System.Drawing.FontStyle]::Bold)
  $subFont   = New-Object System.Drawing.Font('Microsoft YaHei UI', 15, [System.Drawing.FontStyle]::Regular)
  $fmt = New-Object System.Drawing.StringFormat
  $fmt.Alignment = 'Center'
  $brush = New-Object System.Drawing.SolidBrush($ink)
  $large.Graphics.DrawString('PC Rental', $titleFont, $brush, (New-Object System.Drawing.RectangleF(0, 330, 328, 50)), $fmt)
  $large.Graphics.DrawString('设备管理客户端', $subFont, $brush, (New-Object System.Drawing.RectangleF(0, 386, 328, 34)), $fmt)
  Save-Bmp24 $large.Bitmap (Join-Path $OutputDir 'wizard-large.bmp')
} finally { $large.Graphics.Dispose(); $large.Bitmap.Dispose() }

# ---- Small banner (interior pages), 2x of Inno's 55x58 baseline ----
$small = New-Gradient 110 116
try {
  $markFont = New-Object System.Drawing.Font('Microsoft YaHei UI', 26, [System.Drawing.FontStyle]::Bold)
  $fmt = New-Object System.Drawing.StringFormat
  $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
  $brush = New-Object System.Drawing.SolidBrush($ink)
  $small.Graphics.DrawString('PC', $markFont, $brush, (New-Object System.Drawing.RectangleF(0, 0, 110, 116)), $fmt)
  Save-Bmp24 $small.Bitmap (Join-Path $OutputDir 'wizard-small.bmp')
} finally { $small.Graphics.Dispose(); $small.Bitmap.Dispose() }

# ---- Setup icon (PNG-embedded ICO, 256x256) ----
try {
  $iconBmp = New-Object System.Drawing.Bitmap(256, 256, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($iconBmp)
  $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'ClearTypeGridFit'
  $g.Clear([System.Drawing.Color]::Transparent)
  $rect = New-Object System.Drawing.Rectangle(8, 8, 240, 240)
  $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $brandTop, $brandBottom, 55.0)
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $r = 48; $d = $r * 2
  $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
  $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
  $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
  $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
  $path.CloseFigure()
  $g.FillPath($brush, $path)
  Add-MonitorGlyph $g 128 120 2.2
  $g.Dispose()

  $ms = New-Object System.IO.MemoryStream
  $iconBmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $png = $ms.ToArray(); $ms.Dispose(); $iconBmp.Dispose()

  $ico = New-Object System.IO.MemoryStream
  $bw = New-Object System.IO.BinaryWriter($ico)
  $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]1)      # ICONDIR
  $bw.Write([Byte]0); $bw.Write([Byte]0)                                # 256x256
  $bw.Write([Byte]0); $bw.Write([Byte]0)                                # palette / reserved
  $bw.Write([UInt16]1); $bw.Write([UInt16]32)                           # planes / bpp
  $bw.Write([UInt32]$png.Length); $bw.Write([UInt32]22)                 # size / offset
  $bw.Write($png)
  $bw.Flush()
  [System.IO.File]::WriteAllBytes((Join-Path $OutputDir 'app.ico'), $ico.ToArray())
  $ico.Dispose()
} catch {
  Write-Warning "跳过 app.ico 生成：$($_.Exception.Message)"
}

Write-Host "安装器素材已生成到 $OutputDir"

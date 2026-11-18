# Generates src\Glance\Assets\glance.ico (16/24/32/48/64/256 px, PNG-compressed entries) and glance.png (256 px).
# The mark: a deep-red square (the wallpaper Glance was designed on), a frosted-glass panel with the time in
# white and a red colon, and the red page pill under it.
# Run with Windows PowerShell 5.1 (System.Drawing):  powershell -File scripts\Make-Icon.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\Glance\Assets'
New-Item -ItemType Directory -Force $out | Out-Null

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = [Math]::Max(1, [Math]::Min($r * 2, [Math]::Min($w, $h)))
  $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90); $p.CloseFigure()
  return $p
}

function New-IconBitmap([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
  $s = [float]$size

  # background: deep red to near-black
  $bg = New-RoundRect 0 0 ($s - 1) ($s - 1) ($s * 0.22)
  $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s), ([System.Drawing.Color]::FromArgb(255, 176, 18, 24)), ([System.Drawing.Color]::FromArgb(255, 28, 3, 6))
  $g.FillPath($brush, $bg)

  # the glass panel
  $px = $s * 0.13; $py = $s * 0.27; $pw = $s * 0.74; $ph = $s * 0.40
  $panel = New-RoundRect $px $py $pw $ph ($s * 0.07)
  $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 255, 255, 255))), $panel)
  if ($size -ge 32) { $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(150, 255, 255, 255)), ([float][Math]::Max(1, $s / 64))), $panel) }

  # the time: two white blocks with a red colon between them
  $bh = $ph * 0.42; $by = $py + ($ph - $bh) / 2; $bw = $pw * 0.30; $gap = $pw * 0.12
  $lx = $px + ($pw - (2 * $bw + $gap)) / 2
  $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(245, 255, 255, 255))
  $g.FillPath($white, (New-RoundRect $lx $by $bw $bh ($bh * 0.22)))
  $g.FillPath($white, (New-RoundRect ($lx + $bw + $gap) $by $bw $bh ($bh * 0.22)))
  $red = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 40, 28))
  $dot = [Math]::Max(1.5, $gap * 0.45); $cx = $lx + $bw + $gap / 2
  $g.FillEllipse($red, $cx - $dot / 2, $by + $bh * 0.22 - $dot / 2, $dot, $dot)
  $g.FillEllipse($red, $cx - $dot / 2, $by + $bh * 0.78 - $dot / 2, $dot, $dot)

  # the page pill and two faint dots under the panel
  $pillH = [Math]::Max(2, $s * 0.055); $pillW = $s * 0.16; $pyy = $py + $ph + $s * 0.09
  $g.FillPath($red, (New-RoundRect ($s / 2 - $pillW / 2) $pyy $pillW $pillH ($pillH / 2)))
  if ($size -ge 32) {
    $faint = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(150, 255, 255, 255))
    $g.FillEllipse($faint, $s / 2 - $pillW / 2 - $s * 0.09, $pyy, $pillH, $pillH)
    $g.FillEllipse($faint, $s / 2 + $pillW / 2 + $s * 0.09 - $pillH, $pyy, $pillH, $pillH)
  }
  $g.Dispose()
  return $bmp
}

$sizes = 16, 24, 32, 48, 64, 256
$pngs = foreach ($sz in $sizes) {
  $b = New-IconBitmap $sz
  $ms = New-Object System.IO.MemoryStream
  $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  if ($sz -eq 256) { $b.Save((Join-Path $out 'glance.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
  $b.Dispose()
  , $ms.ToArray()
}

# ICO container: ICONDIR + ICONDIRENTRY[] + PNG payloads
$fs = [System.IO.File]::Create((Join-Path $out 'glance.ico'))
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $sz = $sizes[$i]; $len = $pngs[$i].Length
  $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz }))); $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
  $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$len); $w.Write([uint32]$offset); $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
"wrote $(Join-Path $out 'glance.ico')"

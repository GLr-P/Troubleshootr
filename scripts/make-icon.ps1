# Draws the Troubleshootr icon (magnifier with a pulse line on the app's blue) and writes src/app.ico
# with 16-256 px images. Uses WPF, which ships with Windows. Run: powershell -NoProfile -File scripts/make-icon.ps1
param([string]$Out = (Join-Path $PSScriptRoot '..\src\app.ico'), [string]$PngDir)

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$ErrorActionPreference = 'Stop'

function New-Frame([int]$px) {
  $dv = New-Object Windows.Media.DrawingVisual
  $dc = $dv.RenderOpen()
  $s = $px / 256.0
  $dc.PushTransform((New-Object Windows.Media.ScaleTransform $s, $s))

  # tile: rounded square, blue gradient
  $grad = New-Object Windows.Media.LinearGradientBrush ([Windows.Media.Color]::FromRgb(0x6E, 0x9E, 0xFF)), ([Windows.Media.Color]::FromRgb(0x2F, 0x5C, 0xD6)), (New-Object Windows.Point 0, 0), (New-Object Windows.Point 1, 1)
  $margin = 8; $radius = 56
  if ($px -le 24) { $margin = 0; $radius = 48 }   # small sizes: use every pixel
  $dc.DrawRoundedRectangle($grad, $null, (New-Object Windows.Rect $margin, $margin, (256 - 2 * $margin), (256 - 2 * $margin)), $radius, $radius)

  $white = [Windows.Media.Brushes]::White
  $small = $px -le 32
  $ring = 22; $handle = 32; if ($small) { $ring = 30; $handle = 40 }

  # handle first, so the ring sits on top of its end
  $hp = New-Object Windows.Media.Pen $white, $handle
  $hp.StartLineCap = 'Round'; $hp.EndLineCap = 'Round'
  $dc.DrawLine($hp, (New-Object Windows.Point 160, 160), (New-Object Windows.Point 206, 206))

  # lens
  $c = New-Object Windows.Point 112, 112
  $dc.DrawEllipse($null, (New-Object Windows.Media.Pen $white, $ring), $c, 64, 64)

  # pulse line inside the lens (left out below 32 px, where it would blur)
  if (-not $small -or $px -eq 32) {
    $w = 13; if ($px -eq 32) { $w = 17 }
    $pp = New-Object Windows.Media.Pen $white, $w
    $pp.StartLineCap = 'Round'; $pp.EndLineCap = 'Round'; $pp.LineJoin = 'Round'
    $geo = New-Object Windows.Media.StreamGeometry
    $ctx = $geo.Open()
    $ctx.BeginFigure((New-Object Windows.Point 66, 116), $false, $false)
    foreach ($p in @(@(90, 116), @(104, 84), @(122, 146), @(136, 108), @(158, 108))) {
      $ctx.LineTo((New-Object Windows.Point $p[0], $p[1]), $true, $true)
    }
    $ctx.Close()
    $dc.DrawGeometry($null, $pp, $geo)
  }
  $dc.Pop()
  $dc.Close()

  $bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap $px, $px, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
  $bmp.Render($dv)
  $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
  $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
  $ms = New-Object IO.MemoryStream
  $enc.Save($ms)
  , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @(); foreach ($px in $sizes) { $images += , (New-Frame $px) }

if ($PngDir) {
  New-Item -ItemType Directory -Force $PngDir | Out-Null
  for ($i = 0; $i -lt $sizes.Count; $i++) { [IO.File]::WriteAllBytes((Join-Path $PngDir "icon-$($sizes[$i]).png"), $images[$i]) }
}

# ICO container with PNG images (supported since Windows Vista)
$ms = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter $ms
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $d = $sizes[$i]; if ($d -ge 256) { $d = 0 }
  $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32)
  $bw.Write([uint32]$images[$i].Length); $bw.Write([uint32]$offset)
  $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Flush()
[IO.File]::WriteAllBytes([IO.Path]::GetFullPath($Out), $ms.ToArray())
"wrote $([IO.Path]::GetFullPath($Out)) ($($ms.Length) bytes, sizes $($sizes -join ', '))"

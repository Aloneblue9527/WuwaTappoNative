#requires -Version 5.1
# Generates the warm, cream-toned Inno Setup wizard artwork from the assets
# that already ship with the repository. Run this only when the artwork needs
# to be regenerated; the generated PNGs are committed with the installer.
param(
  [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$assetRoot = Join-Path $RepositoryRoot "assets"
$outputDir = Join-Path $assetRoot "installer"
$headPath = Join-Path $assetRoot "default\role_1501_1001_head_1.png"
$iconPath = Join-Path $assetRoot "app.ico"

foreach ($required in @($headPath, $iconPath)) {
  if (-not (Test-Path -LiteralPath $required)) { throw "Missing source asset: $required" }
}
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

# Inno Setup's documented image areas. Supplying every size lets Setup pick an
# exact match at 100%-250% scaling instead of stretching a single bitmap.
$largeSizes = @(
  @(202, 386),
  @(269, 515),
  @(336, 643),
  @(403, 772),
  @(430, 824),
  @(498, 953),
  @(534, 1022)
)
$smallSizes = @(58, 77, 97, 116, 124, 143, 159)
$baseWidth = 534.0
$baseHeight = 1022.0

function New-RoundedPath {
  param([float]$X, [float]$Y, [float]$Width, [float]$Height, [float]$Radius)
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $diameter = $Radius * 2.0
  $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
  $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
  $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
  $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
  $path.CloseFigure()
  return $path
}

function Fill-RoundedRect {
  param([System.Drawing.Graphics]$Graphics, [System.Drawing.Brush]$Brush, [float]$X, [float]$Y, [float]$Width, [float]$Height, [float]$Radius)
  $path = New-RoundedPath -X $X -Y $Y -Width $Width -Height $Height -Radius $Radius
  try { $Graphics.FillPath($Brush, $path) } finally { $path.Dispose() }
}

function Draw-RoundedRect {
  param([System.Drawing.Graphics]$Graphics, [System.Drawing.Pen]$Pen, [float]$X, [float]$Y, [float]$Width, [float]$Height, [float]$Radius)
  $path = New-RoundedPath -X $X -Y $Y -Width $Width -Height $Height -Radius $Radius
  try { $Graphics.DrawPath($Pen, $path) } finally { $path.Dispose() }
}

function Draw-CenteredText {
  param(
    [System.Drawing.Graphics]$Graphics,
    [string]$Text,
    [System.Drawing.Font]$Font,
    [System.Drawing.Brush]$Brush,
    [float]$X,
    [float]$Y,
    [float]$Width,
    [float]$Height
  )
  $format = New-Object System.Drawing.StringFormat
  try {
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $format.FormatFlags = [System.Drawing.StringFormatFlags]::NoWrap
    $rect = [System.Drawing.RectangleF]::new($X, $Y, $Width, $Height)
    $Graphics.DrawString($Text, $Font, $Brush, $rect, $format)
  } finally {
    $format.Dispose()
  }
}

function New-Canvas {
  param([int]$Width, [int]$Height)
  $bitmap = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $bitmap.SetResolution(96, 96)
  return $bitmap
}

function Save-Png {
  param([System.Drawing.Bitmap]$Bitmap, [string]$Path)
  try { $Bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $Bitmap.Dispose() }
}

function Render-Large {
  param([int]$Width, [int]$Height, [string]$Path, [System.Drawing.Image]$Head)

  $bitmap = New-Canvas -Width $Width -Height $Height
  $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
  try {
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.ScaleTransform(($Width / $baseWidth), ($Height / $baseHeight))

    $creamTop = [System.Drawing.Color]::FromArgb(255, 255, 253, 248)
    $creamBottom = [System.Drawing.Color]::FromArgb(255, 244, 225, 198)
    $background = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
      [System.Drawing.RectangleF]::new(0, 0, $baseWidth, $baseHeight),
      $creamTop,
      $creamBottom,
      90.0
    )
    try { $graphics.FillRectangle($background, 0, 0, $baseWidth, $baseHeight) } finally { $background.Dispose() }

    # Soft peach light and translucent decorative shapes.
    $glow = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(40, 231, 183, 124))
    try { $graphics.FillEllipse($glow, 42, 104, 450, 630) } finally { $glow.Dispose() }

    $topOrb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(105, 249, 221, 184))
    try { $graphics.FillEllipse($topOrb, 338, -132, 338, 338) } finally { $topOrb.Dispose() }

    $bottomOrb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(80, 242, 207, 159))
    try { $graphics.FillEllipse($bottomOrb, -166, 770, 350, 350) } finally { $bottomOrb.Dispose() }

    $dotBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(42, 217, 154, 92))
    try {
      for ($i = 0; $i -lt 32; $i++) {
        $x = 22 + (($i * 89) % 492)
        $y = 26 + (($i * 157) % 962)
        if ($x -gt 52 -and $x -lt 482 -and $y -gt 120 -and $y -lt 670) { continue }
        $radius = 1.3 + (($i % 3) * 0.8)
        $graphics.FillEllipse($dotBrush, $x, $y, $radius * 2, $radius * 2)
      }
    } finally { $dotBrush.Dispose() }

    # Top brand lockup.
    $badgeFill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(215, 255, 253, 247))
    $badgePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(170, 231, 183, 124), 1.2)
    try {
      Fill-RoundedRect -Graphics $graphics -Brush $badgeFill -X 158 -Y 42 -Width 218 -Height 42 -Radius 21
      Draw-RoundedRect -Graphics $graphics -Pen $badgePen -X 158 -Y 42 -Width 218 -Height 42 -Radius 21
    } finally {
      $badgeFill.Dispose()
      $badgePen.Dispose()
    }

    $brandFont = New-Object System.Drawing.Font("Segoe UI Semibold", 10.5, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $brandBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 142, 94, 67))
    try {
      Draw-CenteredText -Graphics $graphics -Text "W U W A T A P P O   N A T I V E" -Font $brandFont -Brush $brandBrush -X 158 -Y 42 -Width 218 -Height 42
    } finally {
      $brandFont.Dispose()
      $brandBrush.Dispose()
    }

    # Rounded greeting card.
    $shadowBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(22, 110, 69, 45))
    try {
      Fill-RoundedRect -Graphics $graphics -Brush $shadowBrush -X 56 -Y 142 -Width 430 -Height 520 -Radius 52
      Fill-RoundedRect -Graphics $graphics -Brush $shadowBrush -X 50 -Y 136 -Width 430 -Height 520 -Radius 52
    } finally { $shadowBrush.Dispose() }

    $cardFill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(248, 255, 253, 248))
    $cardBorder = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(235, 205, 168), 2.0)
    try {
      Fill-RoundedRect -Graphics $graphics -Brush $cardFill -X 44 -Y 126 -Width 446 -Height 526 -Radius 52
      Draw-RoundedRect -Graphics $graphics -Pen $cardBorder -X 44 -Y 126 -Width 446 -Height 526 -Radius 52
    } finally {
      $cardFill.Dispose()
      $cardBorder.Dispose()
    }

    # Small label on the card.
    $labelFill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 250, 235, 211))
    try { Fill-RoundedRect -Graphics $graphics -Brush $labelFill -X 72 -Y 154 -Width 176 -Height 28 -Radius 14 } finally { $labelFill.Dispose() }
    $labelFont = New-Object System.Drawing.Font("Segoe UI Semibold", 8.5, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $labelBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 142, 94, 67))
    try {
      Draw-CenteredText -Graphics $graphics -Text "D E S K T O P   C O M P A N I O N" -Font $labelFont -Brush $labelBrush -X 72 -Y 154 -Width 176 -Height 28
    } finally {
      $labelFont.Dispose()
      $labelBrush.Dispose()
    }

    # Character medallion.
    $medallionFill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 251, 233, 207))
    $medallionPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(175, 231, 183, 124), 2.0)
    try {
      $graphics.FillEllipse($medallionFill, 111, 194, 312, 312)
      $graphics.DrawEllipse($medallionPen, 111, 194, 312, 312)
    } finally {
      $medallionFill.Dispose()
      $medallionPen.Dispose()
    }

    $headSource = [System.Drawing.Rectangle]::new(50, 80, 460, 460)
    $headDest = [System.Drawing.RectangleF]::new(99, 182, 336, 336)
    $graphics.DrawImage($Head, $headDest, $headSource, [System.Drawing.GraphicsUnit]::Pixel)

    # Hand-drawn speech bubble.
    $bubbleFill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 249, 239))
    $bubblePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(220, 217, 154, 92), 2.0)
    try {
      Fill-RoundedRect -Graphics $graphics -Brush $bubbleFill -X 348 -Y 174 -Width 92 -Height 50 -Radius 22
      Draw-RoundedRect -Graphics $graphics -Pen $bubblePen -X 348 -Y 174 -Width 92 -Height 50 -Radius 22
      $tail = @(
        [System.Drawing.PointF]::new(362, 221),
        [System.Drawing.PointF]::new(351, 242),
        [System.Drawing.PointF]::new(380, 223)
      )
      $graphics.FillPolygon($bubbleFill, $tail)
      $graphics.DrawLine($bubblePen, $tail[0], $tail[1])
      $graphics.DrawLine($bubblePen, $tail[1], $tail[2])
    } finally {
      $bubbleFill.Dispose()
      $bubblePen.Dispose()
    }
    $bubbleFont = New-Object System.Drawing.Font("Segoe UI Semibold", 19, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $bubbleBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 142, 94, 67))
    try {
      Draw-CenteredText -Graphics $graphics -Text "hi!" -Font $bubbleFont -Brush $bubbleBrush -X 348 -Y 168 -Width 92 -Height 50
    } finally {
      $bubbleFont.Dispose()
      $bubbleBrush.Dispose()
    }

    $titleFont = New-Object System.Drawing.Font("Segoe UI Semibold", 41, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $inkBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 110, 69, 45))
    try {
      Draw-CenteredText -Graphics $graphics -Text "Wuwatappo" -Font $titleFont -Brush $inkBrush -X 54 -Y 522 -Width 426 -Height 54
    } finally {
      $titleFont.Dispose()
      $inkBrush.Dispose()
    }

    $dividerPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(110, 231, 183, 124), 1.0)
    try { $graphics.DrawLine($dividerPen, 184, 578, 350, 578) } finally { $dividerPen.Dispose() }

    $subFont = New-Object System.Drawing.Font("Segoe UI Semibold", 9.0, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $subBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 217, 154, 92))
    try {
      Draw-CenteredText -Graphics $graphics -Text "N A T I V E   D E S K T O P   P E T" -Font $subFont -Brush $subBrush -X 54 -Y 586 -Width 426 -Height 22
    } finally {
      $subFont.Dispose()
      $subBrush.Dispose()
    }

    $taglineFont = New-Object System.Drawing.Font("Segoe UI", 12.5, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $taglineBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 142, 123, 104))
    try {
      Draw-CenteredText -Graphics $graphics -Text "A cozy little corner on your desktop" -Font $taglineFont -Brush $taglineBrush -X 64 -Y 614 -Width 406 -Height 24
    } finally {
      $taglineFont.Dispose()
      $taglineBrush.Dispose()
    }

    # Bottom feature chips and platform line.
    $chipFill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(165, 255, 253, 247))
    $chipPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(135, 231, 183, 124), 1.0)
    $chipFont = New-Object System.Drawing.Font("Segoe UI Semibold", 8.5, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $chipBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 142, 94, 67))
    try {
      $chips = @(
        @(69, 722, 112, "P E T"),
        @(197, 722, 140, "F R I E N D S"),
        @(353, 722, 112, "S T U F F")
      )
      foreach ($chip in $chips) {
        $x = [float]$chip[0]
        $chipWidth = [float]$chip[2]
        Fill-RoundedRect -Graphics $graphics -Brush $chipFill -X $x -Y 722 -Width $chipWidth -Height 30 -Radius 15
        Draw-RoundedRect -Graphics $graphics -Pen $chipPen -X $x -Y 722 -Width $chipWidth -Height 30 -Radius 15
        Draw-CenteredText -Graphics $graphics -Text $chip[3] -Font $chipFont -Brush $chipBrush -X $x -Y 722 -Width $chipWidth -Height 30
      }
    } finally {
      $chipFill.Dispose()
      $chipPen.Dispose()
      $chipFont.Dispose()
      $chipBrush.Dispose()
    }

    $footerFont = New-Object System.Drawing.Font("Segoe UI", 10.0, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $footerBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(210, 142, 94, 67))
    try {
      Draw-CenteredText -Graphics $graphics -Text "WINDOWS 10 / 11   |   x64" -Font $footerFont -Brush $footerBrush -X 80 -Y 914 -Width 374 -Height 24
      Draw-CenteredText -Graphics $graphics -Text "made with care for your desktop" -Font $footerFont -Brush $footerBrush -X 80 -Y 946 -Width 374 -Height 24
    } finally {
      $footerFont.Dispose()
      $footerBrush.Dispose()
    }
  } finally {
    $graphics.Dispose()
  }

  Save-Png -Bitmap $bitmap -Path $Path
}

# Small image: reuse the shipped app icon, which already has the cream rounded
# square and character artwork. Render a high-resolution PNG at each size.
$iconSource = [System.Drawing.Image]::FromFile($iconPath)
try {
  foreach ($size in $smallSizes) {
    $bitmap = New-Canvas -Width $size -Height $size
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
      $graphics.Clear([System.Drawing.Color]::Transparent)
      $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
      $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
      $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
      $graphics.DrawImage($iconSource, 0, 0, $size, $size)
    } finally {
      $graphics.Dispose()
    }
    $path = Join-Path $outputDir ("wizard-small-{0}x{0}.png" -f $size)
    Save-Png -Bitmap $bitmap -Path $path
  }
} finally {
  $iconSource.Dispose()
}

$headSource = [System.Drawing.Image]::FromFile($headPath)
try {
  foreach ($size in $largeSizes) {
    $width = [int]$size[0]
    $height = [int]$size[1]
    $path = Join-Path $outputDir ("wizard-large-{0}x{1}.png" -f $width, $height)
    Render-Large -Width $width -Height $height -Path $path -Head $headSource
  }
} finally {
  $headSource.Dispose()
}

Write-Host "Generated wizard artwork in $outputDir"
Get-ChildItem -LiteralPath $outputDir -Filter "wizard-*.png" | Select-Object Name, Length | Format-Table -AutoSize

<#
    Generates the Grumpy application icon.

    Reproducible on purpose: the .ico is a build input that ends up in the exe, the
    installer and every shortcut, so it should be regenerable from source rather than
    being an opaque binary nobody can adjust.

    Design notes
    ------------
    The mark is a scowling face whose frown is also a commit line with a node on it —
    "Grumpy", with the git left implied, matching the product name.

    Constraints that drove it:
      * It has to survive 16x16 (title bar, taskbar, alt-tab). That rules out fine
        detail, so the whole mark is four heavy shapes: two brows, two eyes, one
        frown. Everything is sized in fractions of the canvas so each raster is drawn
        natively rather than downscaled from one big bitmap, which is what keeps the
        small sizes crisp.
      * Colours come from the app's own design tokens (Themes/Tokens.axaml) so the
        icon and the UI are visibly the same product: AccentBrush #7B68F0 over the
        dark surface ramp.

    Usage:  pwsh tools/generate-icon.ps1
    Output: src/GrumpyGit.App/Assets/grumpy.ico  (+ a PNG preview)
#>

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'

$repoRoot  = Split-Path -Parent $PSScriptRoot
$assetsDir = Join-Path $repoRoot 'src\GrumpyGit.App\Assets'
$icoPath   = Join-Path $assetsDir 'grumpy.ico'
$previewPath = Join-Path $assetsDir 'grumpy-preview.png'

# Windows shell asks for these; drawing each natively avoids downscale mush.
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

# ── Palette (from Themes/Tokens.axaml) ──────────────────────────────────────
$accentTop    = [System.Drawing.ColorTranslator]::FromHtml('#8D7CF5')  # AccentHoverBrush
$accentBottom = [System.Drawing.ColorTranslator]::FromHtml('#5B48D6')  # AccentBrush (light variant)
$ink          = [System.Drawing.ColorTranslator]::FromHtml('#0B0D12')  # BgBaseBrush
$inkSoft      = [System.Drawing.Color]::FromArgb(210, 11, 13, 18)

function New-RoundedRectPath {
    param([single]$x, [single]$y, [single]$w, [single]$h, [single]$r)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x,           $y,           $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y,           $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d,   0, 90)
    $path.AddArc($x,           $y + $h - $d, $d, $d,  90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap {
    param([int]$size)

    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [single]$size

    # ── Tile ────────────────────────────────────────────────────────────────
    # Full-bleed rounded square. 22% radius is the Windows app-tile convention;
    # below ~24px the radius is clamped so the corners don't eat the artwork.
    $radius = [Math]::Max(2.0, $s * 0.22)
    $tile = New-RoundedRectPath 0 0 $s $s $radius

    # Rectangle+angle overload rather than two points: the point-pair constructor
    # throws "Out of memory" on degenerate/axis-aligned inputs in some GDI+ builds.
    $tileRect = New-Object System.Drawing.RectangleF(0, 0, $s, $s)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $tileRect, $accentTop, $accentBottom, [single]45)
    $g.FillPath($brush, $tile)
    $brush.Dispose()

    # Inner top highlight — stops the tile reading as flat at large sizes.
    if ($size -ge 48) {
        $glossPath = New-RoundedRectPath ($s*0.06) ($s*0.05) ($s*0.88) ($s*0.46) ($s*0.18)
        $glossRect = New-Object System.Drawing.RectangleF(0, 0, $s, ($s*0.55))
        $gloss = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $glossRect,
            [System.Drawing.Color]::FromArgb(48, 255, 255, 255),
            [System.Drawing.Color]::FromArgb(0, 255, 255, 255),
            [single]90)
        $g.FillPath($gloss, $glossPath)
        $gloss.Dispose(); $glossPath.Dispose()
    }

    # ── Brows ───────────────────────────────────────────────────────────────
    # The whole expression lives here: inner ends pulled DOWN toward the centre is
    # what reads as "angry" rather than "surprised". Heavy and rounded so they
    # survive at 16px.
    $browWidth = [Math]::Max(1.6, $s * 0.115)
    $browPen = New-Object System.Drawing.Pen($ink, $browWidth)
    $browPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $browPen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round

    # Inner ends stop short of centre: when they meet they fuse into a single V
    # blob and the face loses its two-brow reading, especially once anti-aliasing
    # closes the gap at small sizes.
    # left: outer-high -> inner-low
    $g.DrawLine($browPen, ($s*0.225), ($s*0.310), ($s*0.425), ($s*0.428))
    # right: mirrored
    $g.DrawLine($browPen, ($s*0.775), ($s*0.310), ($s*0.575), ($s*0.428))
    $browPen.Dispose()

    # ── Eyes ────────────────────────────────────────────────────────────────
    # Dropped below ~20px: at that scale they collide with the brows and turn the
    # face into a smudge. The brows plus frown still read as a scowl without them.
    if ($size -ge 20) {
        $eyeR = [Math]::Max(1.0, $s * 0.052)
        $eyeBrush = New-Object System.Drawing.SolidBrush($ink)
        $g.FillEllipse($eyeBrush, ($s*0.285 - $eyeR), ($s*0.505 - $eyeR), ($eyeR*2), ($eyeR*2))
        $g.FillEllipse($eyeBrush, ($s*0.715 - $eyeR), ($s*0.505 - $eyeR), ($eyeR*2), ($eyeR*2))
        $eyeBrush.Dispose()
    }

    # ── Frown / commit line ─────────────────────────────────────────────────
    # A downturned arc that doubles as a branch line. At >=32px a commit node sits
    # on it, which is the only git reference in the mark — deliberately quiet.
    $mouthPen = New-Object System.Drawing.Pen($ink, [Math]::Max(1.6, $s * 0.098))
    $mouthPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $mouthPen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round

    $mouthPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $mouthPath.AddBezier(
        (New-Object System.Drawing.PointF(($s*0.285), ($s*0.775))),
        (New-Object System.Drawing.PointF(($s*0.40),  ($s*0.655))),
        (New-Object System.Drawing.PointF(($s*0.60),  ($s*0.655))),
        (New-Object System.Drawing.PointF(($s*0.715), ($s*0.775))))
    $g.DrawPath($mouthPen, $mouthPath)
    $mouthPath.Dispose(); $mouthPen.Dispose()

    # A commit node was tried on the centre of the frown. It read as a nose — an
    # artefact rather than a git reference — and muddied the one shape doing the
    # most work. The frown is left clean; the git connection is carried by the
    # product, not by decoration crammed into a 16px mark.

    # ── Edge definition ─────────────────────────────────────────────────────
    # A hairline keeps the tile from bleeding into light backgrounds.
    if ($size -ge 32) {
        $edgePen = New-Object System.Drawing.Pen($inkSoft, [Math]::Max(1.0, $s * 0.012))
        $g.DrawPath($edgePen, $tile)
        $edgePen.Dispose()
    }

    $tile.Dispose()
    $g.Dispose()
    return $bmp
}

# ── Encoders ────────────────────────────────────────────────────────────────

<#
  Encodes a bitmap as an ICO "DIB" entry: BITMAPINFOHEADER, then a bottom-up
  32bpp BGRA image, then a 1bpp AND mask.

  Why not PNG for every size: PNG-compressed entries are only understood from
  Windows Vista onward, and several toolchains that read .ico files directly
  (System.Drawing.Icon among them, which is how this script's own contact sheet
  first failed) cannot decode them at all. DIB is understood everywhere, so it is
  used for every size except 256 — where PNG is the convention and keeps the file
  from ballooning by ~256 KB.
#>
function ConvertTo-IcoDib {
    param([System.Drawing.Bitmap]$bitmap)

    $w = $bitmap.Width
    $h = $bitmap.Height

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    # BITMAPINFOHEADER. biHeight is doubled because the format expects the colour
    # image and the AND mask stacked in one notional bitmap.
    $bw.Write([UInt32]40)          # biSize
    $bw.Write([Int32]$w)           # biWidth
    $bw.Write([Int32]($h * 2))     # biHeight (colour + mask)
    $bw.Write([UInt16]1)           # biPlanes
    $bw.Write([UInt16]32)          # biBitCount
    $bw.Write([UInt32]0)           # biCompression = BI_RGB
    $bw.Write([UInt32]($w * $h * 4))
    $bw.Write([Int32]0); $bw.Write([Int32]0)
    $bw.Write([UInt32]0); $bw.Write([UInt32]0)

    # Colour data, bottom-up, BGRA.
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                             [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $data.Stride
        $buffer = New-Object byte[] ($stride * $h)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buffer, 0, $buffer.Length)

        for ($y = $h - 1; $y -ge 0; $y--) {
            $bw.Write($buffer, ($y * $stride), ($w * 4))
        }
    }
    finally { $bitmap.UnlockBits($data) }

    # AND mask: unused for 32bpp alpha, but the rows must still be present and
    # padded to a 4-byte boundary or the shell misreads the entry.
    $maskRowBytes = [Math]::Floor(($w + 31) / 32) * 4
    $zeroRow = New-Object byte[] $maskRowBytes
    for ($y = 0; $y -lt $h; $y++) { $bw.Write($zeroRow) }

    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return ,[byte[]]$bytes
}

# ── Render every size ───────────────────────────────────────────────────────
New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null

$entries = @()
$rendered = @{}
foreach ($size in $sizes) {
    $bmp = New-IconBitmap -size $size
    $rendered[$size] = $bmp

    if ($size -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $entries += ,@{ Size = $size; Bytes = $ms.ToArray(); Png = $true }
        $ms.Dispose()
        $bmp.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    else {
        $entries += ,@{ Size = $size; Bytes = [byte[]](ConvertTo-IcoDib -bitmap $bmp); Png = $false }
    }
}

# ── Pack the .ico ───────────────────────────────────────────────────────────
# ICONDIR (6 bytes) + ICONDIRENTRY * n (16 bytes each) + payloads.
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)

$bw.Write([UInt16]0)                    # reserved
$bw.Write([UInt16]1)                    # type: 1 = icon
$bw.Write([UInt16]$entries.Count)

$offset = 6 + (16 * $entries.Count)
foreach ($entry in $entries) {
    # 256 is encoded as 0 — the directory's width/height fields are a single byte.
    $dim = if ($entry.Size -ge 256) { 0 } else { $entry.Size }
    $bw.Write([Byte]$dim)               # width
    $bw.Write([Byte]$dim)               # height
    $bw.Write([Byte]0)                  # palette count (0 = truecolour)
    $bw.Write([Byte]0)                  # reserved
    $bw.Write([UInt16]1)                # colour planes
    $bw.Write([UInt16]32)               # bits per pixel
    $bw.Write([UInt32]$entry.Bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $entry.Bytes.Length
}

foreach ($entry in $entries) {
    $payload = [byte[]]$entry.Bytes
    # Explicit 3-arg overload: PowerShell can otherwise bind Write() to the wrong
    # overload for a loosely-typed array and silently write nothing.
    $bw.Write($payload, 0, $payload.Length)
}

$bw.Flush(); $bw.Dispose(); $fs.Dispose()

# ── Contact sheet ───────────────────────────────────────────────────────────
# Built from the rendered bitmaps rather than by re-reading the .ico, so it shows
# exactly what was drawn at each size. Small sizes are nearest-neighbour upscaled
# so pixel-level legibility can actually be judged.
$sheetSizes = @(16, 24, 32, 48, 64, 128)
$scale = 3
$pad = 16
$cell = 64 * $scale
$sheetW = ($sheetSizes.Count * ($cell + $pad)) + $pad
$sheetH = $cell + ($pad * 2) + 24

$sheet = New-Object System.Drawing.Bitmap([int]$sheetW, [int]$sheetH)
$sg = [System.Drawing.Graphics]::FromImage($sheet)
$sg.Clear([System.Drawing.ColorTranslator]::FromHtml('#20242D'))
$sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$sg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half

$font = New-Object System.Drawing.Font('Segoe UI', 10)
$textBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)

$x = $pad
foreach ($sz in $sheetSizes) {
    $src = $rendered[$sz]
    $drawn = [Math]::Min($sz, 64) * $scale
    $top = $pad + [int](($cell - $drawn) / 2)
    $sg.DrawImage($src, [int]$x, [int]$top, [int]$drawn, [int]$drawn)
    $sg.DrawString("${sz}px", $font, $textBrush, [single]$x, [single]($pad + $cell + 4))
    $x += $cell + $pad
}

$sheetPath = Join-Path $assetsDir 'grumpy-contact-sheet.png'
$sheet.Save($sheetPath, [System.Drawing.Imaging.ImageFormat]::Png)
$sg.Dispose(); $sheet.Dispose(); $font.Dispose(); $textBrush.Dispose()

foreach ($bmp in $rendered.Values) { $bmp.Dispose() }

$kb = [Math]::Round((Get-Item $icoPath).Length / 1KB, 1)
Write-Host "Wrote $icoPath ($kb KB, $($entries.Count) sizes: $($sizes -join ', '))"
Write-Host "  DIB entries: $(($entries | Where-Object { -not $_.Png }).Count), PNG entries: $(($entries | Where-Object { $_.Png }).Count)"
Write-Host "Wrote $previewPath"
Write-Host "Wrote $sheetPath"

<#
    Generates the Grumpy AI application icon from the Grumpy one.

    Grumpy and Grumpy AI are separate products that can be installed side by side,
    so their icons sit next to each other in the taskbar, the Start menu and Apps &
    features. They have to read as the same family and still be told apart at a
    glance — which is why this derives from sheep.ico rather than being separate
    artwork: the sheep is the brand, the badge is the edition.

    Design notes
    ------------
      * The mark is the unmodified sheep with a violet "AI" badge in the
        bottom-right, the same position and reading as a notification badge.
      * The badge colour is the app's own AccentBrush ramp (Themes/Tokens.axaml),
        so it matches the accent the AI panel is drawn in.
      * Letters are dropped below 32px. At 24px the badge is ~10px across and "AI"
        collapses into a smudge that reads as dirt on the icon; a solid accent disc
        with a dark ring is still unmistakably "the other one" at that size, which
        is all the badge has to achieve there.
      * Each size is composited from that size's own entry in sheep.ico rather than
        downscaled from the 256px art, so the small sizes stay as crisp as the
        standard edition's.

    System.Drawing.Icon cannot decode PNG-compressed .ico entries — and every entry
    in sheep.ico is PNG — so the icon directory is parsed by hand below. That is
    also why the output is written the same way, by ConvertTo-IcoDib in
    generate-icon.ps1's format.

    Usage:  pwsh tools/generate-ai-icon.ps1
    Output: src/GrumpyGit.App/Assets/sheep-ai.ico  (+ a PNG preview and contact sheet)
#>

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'

$repoRoot    = Split-Path -Parent $PSScriptRoot
$assetsDir   = Join-Path $repoRoot 'src\GrumpyGit.App\Assets'
$sourceIco   = Join-Path $assetsDir 'sheep.ico'
$icoPath     = Join-Path $assetsDir 'sheep-ai.ico'
$previewPath = Join-Path $assetsDir 'sheep-ai.png'
$sheetPath   = Join-Path $assetsDir 'sheep-ai-contact-sheet.png'

# ── Palette (from Themes/Tokens.axaml) ──────────────────────────────────────
$accentTop    = [System.Drawing.ColorTranslator]::FromHtml('#8D7CF5')  # AccentHoverBrush
$accentBottom = [System.Drawing.ColorTranslator]::FromHtml('#5B48D6')  # AccentBrush
$ink          = [System.Drawing.ColorTranslator]::FromHtml('#0B0D12')  # BgBaseBrush

# ── Read the source icon ────────────────────────────────────────────────────
function Read-IcoFrames {
    param([string]$path)

    $bytes = [System.IO.File]::ReadAllBytes($path)
    $count = [BitConverter]::ToUInt16($bytes, 4)
    $frames = @{}

    for ($i = 0; $i -lt $count; $i++) {
        $entry = 6 + (16 * $i)
        # Width 0 means 256 — the directory stores the dimension in a single byte.
        $width = $bytes[$entry]
        if ($width -eq 0) { $width = 256 }

        $length = [BitConverter]::ToUInt32($bytes, $entry + 8)
        $offset = [BitConverter]::ToUInt32($bytes, $entry + 12)

        $payload = New-Object byte[] $length
        [Array]::Copy($bytes, $offset, $payload, 0, $length)

        $ms = New-Object System.IO.MemoryStream(, $payload)
        # Copied into a fresh bitmap: Image.FromStream keeps the stream alive for
        # the image's lifetime, and disposing it later invalidates the bitmap.
        $loaded = [System.Drawing.Image]::FromStream($ms)
        $frame = New-Object System.Drawing.Bitmap($loaded)
        $loaded.Dispose(); $ms.Dispose()

        $frames[[int]$width] = $frame
    }

    return $frames
}

$frames = Read-IcoFrames -path $sourceIco
$sizes = @($frames.Keys | Sort-Object)
Write-Host "Source $([IO.Path]::GetFileName($sourceIco)): $($sizes -join ', ')"

# ── Composite the badge ─────────────────────────────────────────────────────
function New-AiIconBitmap {
    param([System.Drawing.Bitmap]$source, [int]$size)

    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    $g.DrawImage($source, 0, 0, $size, $size)

    $s = [single]$size

    # Bottom-right, hard into the corner. Sized by trial against the contact sheet:
    # at 0.46 of the canvas the badge covered the sheep's face and the icon read as
    # a violet disc with some wool behind it. 0.38 pushed fully into the corner
    # keeps the sheep the subject and still carries legible letters by 32px.
    $d = $s * 0.38
    $x = $s - $d - ($s * 0.01)
    $y = $s - $d - ($s * 0.01)
    $badge = New-Object System.Drawing.RectangleF($x, $y, $d, $d)

    # A dark ring under the fill: the badge lands on pale fleece, and without it
    # the violet bleeds into the light artwork at small sizes.
    $ringWidth = [Math]::Max(1.0, $s * 0.035)
    $ringBrush = New-Object System.Drawing.SolidBrush($ink)
    $g.FillEllipse($ringBrush, $x - $ringWidth, $y - $ringWidth, $d + ($ringWidth * 2), $d + ($ringWidth * 2))
    $ringBrush.Dispose()

    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $badge, $accentTop, $accentBottom, [single]90)
    $g.FillEllipse($fill, $badge)
    $fill.Dispose()

    # Letters as a filled path rather than DrawString: the glyph outline scales with
    # the badge instead of snapping to whole point sizes, which is what keeps "AI"
    # centred and the same weight at every size.
    if ($size -ge 32) {
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $format = New-Object System.Drawing.StringFormat
        $format.Alignment     = [System.Drawing.StringAlignment]::Center
        $format.LineAlignment = [System.Drawing.StringAlignment]::Center

        # Em size is in world units here, so it tracks the badge diameter.
        $family = New-Object System.Drawing.FontFamily('Segoe UI')
        $path.AddString('AI', $family,
            [int][System.Drawing.FontStyle]::Bold,
            [single]($d * 0.62),
            $badge,
            $format)

        $textBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        $g.FillPath($textBrush, $path)
        $textBrush.Dispose()

        $family.Dispose(); $format.Dispose(); $path.Dispose()
    }

    $g.Dispose()
    return $bmp
}

# ── Encoder ─────────────────────────────────────────────────────────────────
# Same DIB entry format as generate-icon.ps1, and for the same reason: PNG-
# compressed entries are not readable by every toolchain that consumes .ico files.
function ConvertTo-IcoDib {
    param([System.Drawing.Bitmap]$bitmap)

    $w = $bitmap.Width
    $h = $bitmap.Height

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    # biHeight is doubled because the format expects the colour image and the AND
    # mask stacked in one notional bitmap.
    $bw.Write([UInt32]40)
    $bw.Write([Int32]$w)
    $bw.Write([Int32]($h * 2))
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]0)
    $bw.Write([UInt32]($w * $h * 4))
    $bw.Write([Int32]0); $bw.Write([Int32]0)
    $bw.Write([UInt32]0); $bw.Write([UInt32]0)

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
$entries = @()
$rendered = @{}
foreach ($size in $sizes) {
    $bmp = New-AiIconBitmap -source $frames[$size] -size $size
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

$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$entries.Count)

$offset = 6 + (16 * $entries.Count)
foreach ($entry in $entries) {
    $dim = if ($entry.Size -ge 256) { 0 } else { $entry.Size }
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]0)
    $bw.Write([Byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
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
# Both editions on one row per size, because the only question worth answering is
# whether you can tell them apart — not whether either looks nice alone.
$sheetSizes = @(16, 24, 32, 48, 64, 128)
$scale = 3
$pad = 16
$cell = 64 * $scale
$sheetW = ($sheetSizes.Count * ($cell + $pad)) + $pad
$rowH = $cell + $pad
$sheetH = ($rowH * 2) + $pad + 24

$sheet = New-Object System.Drawing.Bitmap([int]$sheetW, [int]$sheetH)
$sg = [System.Drawing.Graphics]::FromImage($sheet)
$sg.Clear([System.Drawing.ColorTranslator]::FromHtml('#20242D'))
$sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$sg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half

$font = New-Object System.Drawing.Font('Segoe UI', 10)
$textBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)

$x = $pad
foreach ($sz in $sheetSizes) {
    $drawn = [Math]::Min($sz, 64) * $scale
    $indent = [int](($cell - $drawn) / 2)

    $sg.DrawImage($frames[$sz],   [int]$x, [int]($pad + $indent),         [int]$drawn, [int]$drawn)
    $sg.DrawImage($rendered[$sz], [int]$x, [int]($pad + $rowH + $indent), [int]$drawn, [int]$drawn)
    $sg.DrawString("${sz}px", $font, $textBrush, [single]$x, [single]($pad + ($rowH * 2) - 8))

    $x += $cell + $pad
}

$sheet.Save($sheetPath, [System.Drawing.Imaging.ImageFormat]::Png)
$sg.Dispose(); $sheet.Dispose(); $font.Dispose(); $textBrush.Dispose()

foreach ($bmp in $rendered.Values) { $bmp.Dispose() }
foreach ($bmp in $frames.Values) { $bmp.Dispose() }

$kb = [Math]::Round((Get-Item $icoPath).Length / 1KB, 1)
Write-Host "Wrote $icoPath ($kb KB, $($entries.Count) sizes: $($sizes -join ', '))"
Write-Host "Wrote $previewPath"
Write-Host "Wrote $sheetPath  (top row: Grumpy, bottom row: Grumpy AI)"

# Genera app/Assets/violeta.ico — emblema de Violeta (rueda de bici + flor, en lila).
# Dibuja varios tamaños con GDI+ y los ensambla en un .ico real (entradas PNG).
Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $PSScriptRoot
$assets  = Join-Path $root 'app\Assets'
[void][System.IO.Directory]::CreateDirectory($assets)
$icoPath = Join-Path $assets 'violeta.ico'

function New-Emblem([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    # Badge circular con degradado violeta -> lila
    $pad = [float]($S * 0.06)
    $rect = New-Object System.Drawing.RectangleF($pad, $pad, ($S - 2*$pad), ($S - 2*$pad))
    $c1 = [System.Drawing.Color]::FromArgb(255, 0x8B, 0x5C, 0xF6)
    $c2 = [System.Drawing.Color]::FromArgb(255, 0xC4, 0xA5, 0xFF)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 45.0)
    $g.FillEllipse($grad, $rect)

    # Rueda (anillo) lila claro
    $wcx = [float]($S * 0.5); $wcy = [float]($S * 0.60); $wr = [float]($S * 0.24)
    $penW = [float]([Math]::Max(2.0, $S * 0.055))
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 0xEC, 0xDD, 0xFF), $penW)
    $g.DrawEllipse($pen, ($wcx-$wr), ($wcy-$wr), (2*$wr), (2*$wr))
    # Hub
    $hub = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0xEC, 0xDD, 0xFF))
    $hr = [float]($S * 0.035)
    $g.FillEllipse($hub, ($wcx-$hr), ($wcy-$hr), (2*$hr), (2*$hr))

    # Flor (5 petalos + centro) en la parte superior
    $fcx = [float]($S * 0.5); $fcy = [float]($S * 0.30); $pr = [float]($S * 0.085)
    $petalA = [System.Drawing.Color]::FromArgb(255, 0xF4, 0x72, 0xB6)
    $petalB = [System.Drawing.Color]::FromArgb(255, 0xF9, 0xA8, 0xD4)
    for ($i = 0; $i -lt 5; $i++) {
        $ang = [Math]::PI/2 + $i * (2*[Math]::PI/5)
        $px = $fcx + [float]([Math]::Cos($ang) * $S * 0.085)
        $py = $fcy - [float]([Math]::Sin($ang) * $S * 0.085)
        $col = if ($i % 2 -eq 0) { $petalA } else { $petalB }
        $pb = New-Object System.Drawing.SolidBrush($col)
        $g.FillEllipse($pb, ($px-$pr), ($py-$pr), (2*$pr), (2*$pr))
        $pb.Dispose()
    }
    $center = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0xFC, 0xD3, 0x4D))
    $cr = [float]($S * 0.055)
    $g.FillEllipse($center, ($fcx-$cr), ($fcy-$cr), (2*$cr), (2*$cr))

    $g.Dispose()
    return $bmp
}

$sizes = @(16, 32, 48, 64, 128, 256)
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Emblem $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,($ms.ToArray())
    $bmp.Dispose(); $ms.Dispose()
}

# Ensamblar ICONDIR + entradas (PNG embebido, soportado desde Windows Vista)
$fs = [System.IO.File]::Open($icoPath, [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0)            # reserved
$bw.Write([uint16]1)            # type = icon
$bw.Write([uint16]$sizes.Count) # count

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $len = $pngs[$i].Length
    $dim = if ($s -ge 256) { [byte]0 } else { [byte]$s }
    $bw.Write([byte]$dim)        # width
    $bw.Write([byte]$dim)        # height
    $bw.Write([byte]0)           # colors in palette
    $bw.Write([byte]0)           # reserved
    $bw.Write([uint16]1)         # planes
    $bw.Write([uint16]32)        # bpp
    $bw.Write([uint32]$len)      # size of image data
    $bw.Write([uint32]$offset)   # offset
    $offset += $len
}
foreach ($png in $pngs) { $bw.Write($png) }
$bw.Flush(); $bw.Close(); $fs.Close()

Write-Output "Icono creado: $icoPath ($((Get-Item $icoPath).Length) bytes)"

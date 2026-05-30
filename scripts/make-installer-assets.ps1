# Genera los bitmaps del asistente de instalación (Inno Setup) en installer\assets\:
#   wizard.bmp        164x314  (banner izquierdo de Bienvenida/Finalizar)
#   wizard-small.bmp   55x58   (esquina de las páginas internas)
# Estilo violeta/lila con el emblema (rueda + flor) de Violeta.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$dir  = Join-Path $root 'installer\assets'
[void][System.IO.Directory]::CreateDirectory($dir)

function Draw-Emblem($g, [float]$cx, [float]$cy, [float]$scale) {
    # Rueda
    $wr = 22.0 * $scale
    $penW = [Math]::Max(2.0, 5.0 * $scale)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255,0xEC,0xDD,0xFF), $penW)
    $g.DrawEllipse($pen, ($cx-$wr), ($cy-$wr+8*$scale), (2*$wr), (2*$wr))
    $hub = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255,0xEC,0xDD,0xFF))
    $hr = 4.0 * $scale
    $g.FillEllipse($hub, ($cx-$hr), ($cy-$hr+8*$scale), (2*$hr), (2*$hr))
    # Flor (5 pétalos + centro), encima
    $fcy = $cy - 20*$scale
    $pr = 8.0 * $scale
    $a = [System.Drawing.Color]::FromArgb(255,0xF4,0x72,0xB6)
    $b = [System.Drawing.Color]::FromArgb(255,0xF9,0xA8,0xD4)
    for ($i=0; $i -lt 5; $i++) {
        $ang = [Math]::PI/2 + $i*(2*[Math]::PI/5)
        $px = $cx + [float]([Math]::Cos($ang) * 9 * $scale)
        $py = $fcy - [float]([Math]::Sin($ang) * 9 * $scale)
        $col = if ($i % 2 -eq 0) { $a } else { $b }
        $pb = New-Object System.Drawing.SolidBrush($col)
        $g.FillEllipse($pb, ($px-$pr), ($py-$pr), (2*$pr), (2*$pr))
        $pb.Dispose()
    }
    $center = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255,0xFC,0xD3,0x4D))
    $cr = 5.0 * $scale
    $g.FillEllipse($center, ($cx-$cr), ($fcy-$cr), (2*$cr), (2*$cr))
}

function New-Wizard([int]$W, [int]$H, [string]$path, [bool]$withText) {
    $bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

    # Fondo: degradado violeta vertical
    $rect = New-Object System.Drawing.Rectangle(0, 0, $W, $H)
    $c1 = [System.Drawing.Color]::FromArgb(255,0x24,0x18,0x3A)
    $c2 = [System.Drawing.Color]::FromArgb(255,0x3A,0x28,0x60)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 90.0)
    $g.FillRectangle($grad, $rect)

    if ($withText) {
        Draw-Emblem $g ([float]($W/2)) 96.0 1.6
        $title = New-Object System.Drawing.Font('Segoe UI', 26, [System.Drawing.FontStyle]::Bold)
        $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        $sf = New-Object System.Drawing.StringFormat
        $sf.Alignment = [System.Drawing.StringAlignment]::Center
        $g.DrawString('Violeta', $title, $white, [float]($W/2), 150.0, $sf)
        $sub = New-Object System.Drawing.Font('Segoe UI', 9.5)
        $lilac = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255,0xC4,0xA5,0xFF))
        $g.DrawString('ciclismo indoor, libre', $sub, $lilac, [float]($W/2), 188.0, $sf)
        # Insignia gratis
        $green = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255,0x34,0xD3,0x99))
        $g.DrawString('Gratis y para siempre', $sub, $green, [float]($W/2), 230.0, $sf)
        # Cinta trans
        $cols = @(
            [System.Drawing.Color]::FromArgb(255,0x5B,0xCE,0xFA),
            [System.Drawing.Color]::FromArgb(255,0xF5,0xA9,0xB8),
            [System.Drawing.Color]::White,
            [System.Drawing.Color]::FromArgb(255,0xF5,0xA9,0xB8),
            [System.Drawing.Color]::FromArgb(255,0x5B,0xCE,0xFA))
        $bw = [float]($W - 48) / 5
        for ($i=0; $i -lt 5; $i++) {
            $cb = New-Object System.Drawing.SolidBrush($cols[$i])
            $g.FillRectangle($cb, [float](24 + $i*$bw), [float]($H-40), $bw, 5.0)
            $cb.Dispose()
        }
    } else {
        Draw-Emblem $g ([float]($W/2)) ([float]($H/2 + 6)) 0.62
    }

    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $bmp.Dispose()
    Write-Output "  $path"
}

Write-Output 'Generando bitmaps del asistente:'
New-Wizard 164 314 (Join-Path $dir 'wizard.bmp') $true
New-Wizard 55  58  (Join-Path $dir 'wizard-small.bmp') $false
Write-Output 'Listo.'

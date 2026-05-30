# Firma Authenticode de Violeta.
#
#   .\scripts\sign-app.ps1                                   # firma con certificado AUTOFIRMADO (pruebas)
#   .\scripts\sign-app.ps1 -PfxPath cert.pfx -Password ***   # firma con TU certificado real (.pfx)
#   .\scripts\sign-app.ps1 -File dist\Violeta-Setup-1.0.0.exe ...
#
# ⚠️ SmartScreen / "editor desconocido": para que Windows confíe sin avisos hace falta un
#    certificado de firma de código de una CA reconocida (OV o, mejor, EV). Un certificado
#    autofirmado SÍ firma el binario (integridad + autoría declarada), pero NO elimina el aviso
#    de SmartScreen porque no está en la cadena de confianza del sistema. Cuando compres uno,
#    pásalo con -PfxPath y este mismo script lo usará.
param(
    [string]$File = 'dist\Violeta\Violeta.exe',
    [string]$PfxPath,
    [string]$Password,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = if ([System.IO.Path]::IsPathRooted($File)) { $File } else { Join-Path $root $File }
if (-not (Test-Path $target)) { throw "No existe el archivo a firmar: $target (¿ejecutaste publish-app.ps1?)" }

if ($PfxPath) {
    $pfx = if ([System.IO.Path]::IsPathRooted($PfxPath)) { $PfxPath } else { Join-Path $root $PfxPath }
    if (-not (Test-Path $pfx)) { throw "No existe el .pfx: $pfx" }
    $sec = if ($Password) { ConvertTo-SecureString $Password -AsPlainText -Force } else { (Get-Credential -UserName 'pfx' -Message 'Contraseña del .pfx').Password }
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfx, $sec)
    Write-Output "Firmando con certificado real: $($cert.Subject)"
} else {
    $subject = 'CN=Violeta (autofirmado)'
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey } | Select-Object -First 1
    if (-not $cert) {
        Write-Output "Creando certificado de firma de código AUTOFIRMADO (solo para pruebas)…"
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
            -CertStoreLocation Cert:\CurrentUser\My -KeyUsage DigitalSignature `
            -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(5)
    }
    Write-Output "Firmando con certificado AUTOFIRMADO: $($cert.Subject)"
    Write-Output "⚠️  No elimina el aviso de SmartScreen. Para eso necesitas un certificado de una CA reconocida."
}

$result = Set-AuthenticodeSignature -FilePath $target -Certificate $cert `
    -HashAlgorithm SHA256 -TimestampServer $TimestampUrl

Write-Output ""
Write-Output "Archivo:  $target"
Write-Output "Estado:   $($result.Status)"
Write-Output "Firmante: $($result.SignerCertificate.Subject)"
if ($result.Status -ne 'Valid' -and $result.Status -ne 'UnknownError') {
    Write-Output "Detalle:  $($result.StatusMessage)"
}

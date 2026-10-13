# Downloads the free Tabler Icons font (MIT licence, https://tabler.io/icons) used for the island's line-style
# app icons. Run automatically by Build.cmd the first time; safe to run again. If it can't download anything,
# the island simply uses each app's normal icon instead.

$ErrorActionPreference = 'Stop'
$assets = Join-Path $PSScriptRoot '..\src\Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
$fontOut = Join-Path $assets 'tabler-icons.ttf'
$cssOut = Join-Path $assets 'tabler-icons.css'

# Several known locations, newest first (the package layout changed between versions).
$sources = @(
    @{ Css = 'https://cdn.jsdelivr.net/npm/@tabler/icons-webfont@3.19.0/dist/tabler-icons.min.css'; Font = 'https://cdn.jsdelivr.net/npm/@tabler/icons-webfont@3.19.0/dist/fonts/tabler-icons.ttf' },
    @{ Css = 'https://cdn.jsdelivr.net/npm/@tabler/icons-webfont@3/dist/tabler-icons.min.css';      Font = 'https://cdn.jsdelivr.net/npm/@tabler/icons-webfont@3/dist/fonts/tabler-icons.ttf' },
    @{ Css = 'https://unpkg.com/@tabler/icons-webfont@3.19.0/dist/tabler-icons.min.css';            Font = 'https://unpkg.com/@tabler/icons-webfont@3.19.0/dist/fonts/tabler-icons.ttf' },
    @{ Css = 'https://cdn.jsdelivr.net/npm/@tabler/icons-webfont@2.47.0/tabler-icons.min.css';      Font = 'https://cdn.jsdelivr.net/npm/@tabler/icons-webfont@2.47.0/fonts/tabler-icons.ttf' }
)

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
foreach ($source in $sources) {
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $source.Css -OutFile $cssOut -TimeoutSec 60
        Invoke-WebRequest -UseBasicParsing -Uri $source.Font -OutFile $fontOut -TimeoutSec 120
        if ((Get-Item $fontOut).Length -gt 100000 -and (Select-String -Path $cssOut -Pattern 'ti-brand-' -Quiet)) {
            Write-Host '  Icon set downloaded.'
            exit 0
        }
    } catch { }
    Remove-Item -ErrorAction SilentlyContinue $fontOut, $cssOut
}
Write-Host '  Could not download the icon set - the island will use normal app icons instead.'
exit 0

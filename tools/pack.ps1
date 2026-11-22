# Baut den Mett Launcher lokal als Velopack-Paket nach dist\ (Setup.exe, Portable-Zip, Update-Pakete).
# Aufruf:  pwsh tools/pack.ps1 -Version 0.1.0
param(
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$publish = Join-Path $root 'publish'
$dist = Join-Path $root 'dist'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

Push-Location native
cargo test --workspace
if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'Rust-Tests fehlgeschlagen.' }
cargo build --release -p mett-netd
if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'mett-netd-Build fehlgeschlagen.' }
Pop-Location

dotnet test -c Release
if ($LASTEXITCODE -ne 0) { throw 'Tests fehlgeschlagen.' }

dotnet publish src/MettLauncher.App -c Release -r win-x64 --self-contained -o $publish -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw 'Publish fehlgeschlagen.' }

vpk pack --packId MettLauncher --packVersion $Version --packDir $publish --mainExe MettLauncher.exe `
    --packTitle 'Mett Launcher' --packAuthors 'Mett Gaming' --icon assets/icon.ico --outputDir $dist
if ($LASTEXITCODE -ne 0) { throw 'vpk pack fehlgeschlagen.' }

Write-Host "Fertig: $dist"

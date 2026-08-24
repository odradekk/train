$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
Set-Location $root
New-Item -ItemType Directory -Force -Path (Join-Path $root "builds") | Out-Null
$godot = $env:GODOT_EXE
if (-not $godot) {
    $godot = "D:/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe"
}

& $godot --headless --path $root --export-debug "Windows Desktop" "builds/train-mvp-debug.exe"
if ($LASTEXITCODE -ne 0) {
    throw "Godot export-debug failed with exit $LASTEXITCODE"
}

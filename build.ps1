# Compila Disk Studio Suite. Uso:
#   .\build.ps1            -> Debug (desarrollo)
#   .\build.ps1 -Release   -> publica DiskStudioSuite.exe (un solo archivo portable) en .\dist
param([switch]$Release)
$ErrorActionPreference = 'Stop'
$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { 'dotnet' } else { "C:\Program Files\dotnet\dotnet.exe" }
$proj = Join-Path $PSScriptRoot 'src\DiskStudioSuite.csproj'
if ($Release) {
    & $dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o (Join-Path $PSScriptRoot 'dist') -nologo -v q
} else {
    # Cerrar la instancia de depuración si está abierta (bloquea el .exe)
    $dbg = Join-Path $PSScriptRoot 'src\bin\Debug\net10.0-windows\DiskStudioSuite.exe'
    Get-Process DiskStudioSuite -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $dbg } | Stop-Process -Force
    $out = & $dotnet build $proj -c Debug -nologo -v q "-consoleLoggerParameters:NoSummary;ForceNoAlign" 2>&1 | ForEach-Object { "$_" }
    $errs = $out | Where-Object { $_ -match ': error ' } | ForEach-Object { (($_ -replace '\s*\[[^\]]*\]\s*$', '') -split '\\src\\')[-1] } | Sort-Object -Unique
    if ($errs) { $errs; exit 1 } else { 'OK' }
}

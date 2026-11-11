# Compila Disk Studio Suite. Uso:
#   .\build.ps1                       -> Debug (desarrollo)
#   .\build.ps1 -Release              -> publica DiskStudioSuite.exe (un solo archivo portable) en .\dist
#   .\build.ps1 -Release -Installer   -> además crea dist\DiskStudioSuite-Setup-X.Y.Z.exe (requiere Inno Setup 6)
param([switch]$Release, [switch]$Installer)
$ErrorActionPreference = 'Stop'
$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { 'dotnet' } else { "C:\Program Files\dotnet\dotnet.exe" }
$proj = Join-Path $PSScriptRoot 'src\DiskStudioSuite.csproj'
if ($Release) {
    $dist = Join-Path $PSScriptRoot 'dist'
    & $dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $dist -nologo -v q
    if ($Installer) {
        $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $iscc) { throw 'Instala Inno Setup 6 (winget install JRSoftware.InnoSetup) para crear el instalador.' }
        $ver = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
        & $iscc /Q "/DAppVersion=$ver" (Join-Path $PSScriptRoot 'installer\DiskStudioSuite.iss')
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup terminó con el código $LASTEXITCODE" }
        Get-ChildItem $dist -Filter *.exe | ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Name)" } | Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
        Get-ChildItem $dist | Select-Object Name, Length
    }
} else {
    # Cerrar la instancia de depuración si está abierta (bloquea el .exe)
    $dbg = Join-Path $PSScriptRoot 'src\bin\Debug\net10.0-windows\DiskStudioSuite.exe'
    Get-Process DiskStudioSuite -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $dbg } | Stop-Process -Force
    $out = & $dotnet build $proj -c Debug -nologo -v q "-consoleLoggerParameters:NoSummary;ForceNoAlign" 2>&1 | ForEach-Object { "$_" }
    $errs = $out | Where-Object { $_ -match ': error ' } | ForEach-Object { (($_ -replace '\s*\[[^\]]*\]\s*$', '') -split '\\src\\')[-1] } | Sort-Object -Unique
    if ($errs) { $errs; exit 1 } else { 'OK' }
}

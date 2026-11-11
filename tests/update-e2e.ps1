# Prueba de extremo a extremo de la actualización desde la app (no requiere administrador).
# 1) Portable: la app encuentra una versión «9.9.9» en un feed local, la descarga, comprueba el SHA-256,
#    sustituye su propio .exe y se vuelve a abrir.
# 2) Instalada: se instala en una carpeta temporal, se actualiza con el instalador silencioso y se desinstala.
# Requiere haber generado dist\ con:  .\build.ps1 -Release -Installer
param([string]$Log = "$env:TEMP\dss-update-e2e.log")
$ErrorActionPreference = 'Stop'
Start-Transcript -Path $Log -Force | Out-Null
$dist = Join-Path $PSScriptRoot '..\dist' | Resolve-Path
$T = Join-Path $env:TEMP 'dss-e2e'
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{912AA28E-5E5C-4996-8772-5C2E84DDA6C0}_is1'
$fails = 0
function Check($ok, $what) { if ($ok) { Write-Host "  OK   $what" } else { Write-Host "  FALLO $what"; $script:fails++ } }
# Las rutas de %TEMP% pueden venir en formato corto (ADRIAN~1): se compara por el final de la ruta
function Procs($dir) { $tail = '\' + (Split-Path (Split-Path $dir -Parent) -Leaf) + '\' + (Split-Path $dir -Leaf) + '\'; Get-CimInstance Win32_Process -Filter "Name='DiskStudioSuite.exe'" | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.IndexOf($tail, [StringComparison]::OrdinalIgnoreCase) -ge 0 } }
function WaitFor($what, [scriptblock]$cond, $seconds = 120) { for ($i = 0; $i -lt $seconds; $i++) { if (& $cond) { return $true }; Start-Sleep 1 }; Write-Host "       (esperando «$what»: tiempo agotado)"; return $false }
function Feed($assets) {
    $sums = Join-Path $T 'release\SHA256SUMS.txt'
    ($assets | ForEach-Object { "$((Get-FileHash $_ -Algorithm SHA256).Hash.ToLower())  $(Split-Path $_ -Leaf)" }) -join "`n" | Set-Content $sums -Encoding ascii
    $list = @($assets | ForEach-Object { @{ name = (Split-Path $_ -Leaf); browser_download_url = $_; size = (Get-Item $_).Length } }) + @(@{ name = 'SHA256SUMS.txt'; browser_download_url = $sums; size = (Get-Item $sums).Length })
    $f = Join-Path $T 'release\feed.json'
    ConvertTo-Json -Depth 5 -InputObject @(@{ tag_name = 'v9.9.9'; html_url = 'https://example.invalid'; prerelease = $false; draft = $false; body = "## Prueba`n- Actualización de prueba"; assets = $list }) | Set-Content $f -Encoding utf8
    return $f
}
try {
    if (Get-Process DiskStudioSuite -ErrorAction SilentlyContinue) { throw 'Cierra Disk Studio Suite antes de la prueba.' }
    if (Test-Path $key) { throw 'Disk Studio Suite ya está instalado en este equipo: la prueba no lo toca.' }
    if (Test-Path $T) { Remove-Item $T -Recurse -Force }
    New-Item -ItemType Directory "$T\app", "$T\release" | Out-Null
    $setup = Get-ChildItem $dist -Filter 'DiskStudioSuite-Setup-*.exe' | Select-Object -First 1

    Write-Host "== Actualización de la versión portable"
    Copy-Item "$dist\DiskStudioSuite.exe" "$T\app\DiskStudioSuite.exe"
    Copy-Item "$dist\DiskStudioSuite.exe" "$T\release\DiskStudioSuite.exe"
    $feed = Feed @("$T\release\DiskStudioSuite.exe")
    $env:DSS_UPDATE_FEED = $feed; $env:DSS_UPDATE_AUTO = '1'
    Start-Process "$T\app\DiskStudioSuite.exe" -ArgumentList '--sin-presentacion' | Out-Null
    Remove-Item Env:DSS_UPDATE_FEED, Env:DSS_UPDATE_AUTO
    $ok = WaitFor 'reinicio actualizado' { Procs "$T\app" | Where-Object { $_.CommandLine -match '--actualizado' } }
    Check $ok "la app se descarga la versión nueva, se sustituye y se vuelve a abrir con --actualizado"
    Check (WaitFor 'borrar .old' { -not (Test-Path "$T\app\DiskStudioSuite.exe.old") } 30) "el ejecutable anterior (.old) se borra tras actualizar"
    Check ((Get-FileHash "$T\app\DiskStudioSuite.exe").Hash -eq (Get-FileHash "$T\release\DiskStudioSuite.exe").Hash) "el ejecutable en uso es el descargado"
    Check (@(Procs "$T\app").Count -eq 1) "solo queda una instancia abierta"
    Procs "$T\app" | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
    Start-Sleep 2

    Write-Host "== Instalador"
    $inst = "$T\instalada"
    $p = Start-Process $setup.FullName -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=`"$inst`"" -PassThru -Wait
    Check ($p.ExitCode -eq 0) "instalación silenciosa por usuario (código $($p.ExitCode))"
    Check (Test-Path "$inst\DiskStudioSuite.exe") "el programa está en la carpeta de instalación"
    $loc = (Get-ItemProperty $key -ErrorAction SilentlyContinue).InstallLocation
    Check ($loc -and ((Get-Item $loc).FullName.TrimEnd('\') -eq (Get-Item $inst).FullName.TrimEnd('\'))) "Windows la registra como app instalada"
    Check (Test-Path "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Disk Studio Suite.lnk") "acceso directo en el menú Inicio"

    Write-Host "== Actualización de la versión instalada"
    Copy-Item $setup.FullName "$T\release\DiskStudioSuite-Setup-9.9.9.exe"
    $feed = Feed @("$T\release\DiskStudioSuite-Setup-9.9.9.exe", "$T\release\DiskStudioSuite.exe")
    $env:DSS_UPDATE_FEED = $feed; $env:DSS_UPDATE_AUTO = '1'
    $before = (Get-Item "$inst\DiskStudioSuite.exe").LastWriteTimeUtc
    Start-Process "$inst\DiskStudioSuite.exe" -ArgumentList '--sin-presentacion' | Out-Null
    Remove-Item Env:DSS_UPDATE_FEED, Env:DSS_UPDATE_AUTO
    $ok = WaitFor 'reinicio tras el instalador' { Procs $inst | Where-Object { $_.CommandLine -match '--actualizado' } } 180
    Check $ok "la app ejecuta el instalador en silencio y se vuelve a abrir actualizada"
    Check (-not (Test-Path "$inst\DiskStudioSuite.exe.old")) "la versión instalada no deja .old (actualiza el instalador)"
    Procs $inst | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
    Start-Sleep 2

    Write-Host "== Desinstalar"
    $un = Get-ChildItem $inst -Filter 'unins*.exe' | Select-Object -First 1
    $p = Start-Process $un.FullName -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -PassThru -Wait
    Start-Sleep 3
    Check (-not (Test-Path $key)) "desinstalado: Windows ya no la lista"
    Check (-not (Test-Path "$inst\DiskStudioSuite.exe")) "se borran los archivos del programa"
    Check (-not (Test-Path "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Disk Studio Suite.lnk")) "se quita el acceso directo"
    Check (Test-Path "$env:APPDATA\Disk Studio Suite\settings.json") "desinstalar en silencio conserva los ajustes"
}
catch { Write-Host "ERROR: $_"; $fails = 99 }
finally {
    Get-CimInstance Win32_Process -Filter "Name='DiskStudioSuite.exe'" | Where-Object { $_.ExecutablePath -like "*\dss-e2e\*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    if ((Test-Path $key) -and ((Get-ItemProperty $key).InstallLocation -like "*\dss-e2e\*")) { $u = Get-ChildItem "$T\instalada" -Filter 'unins*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1; if ($u) { Start-Process $u.FullName '/VERYSILENT /SUPPRESSMSGBOXES' -Wait } }
    Start-Sleep 2
    Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host ($(if ($fails -eq 0) { "`nACTUALIZACIÓN E INSTALADOR SUPERADOS" } else { "`n$fails PRUEBAS FALLIDAS" }))
    Stop-Transcript | Out-Null
}
exit $fails

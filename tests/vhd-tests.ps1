# Pruebas de integración de Disk Studio Suite sobre DOS DISCOS VIRTUALES (VHDX) temporales.
# No toca ningún disco real: crea los VHDX, los usa y los borra. Requiere administrador.
param([string]$Log = "$env:TEMP\dss-vhd-tests.log")
$ErrorActionPreference = 'Stop'
Start-Transcript -Path $Log -Force | Out-Null
$dir = Join-Path $env:TEMP 'dss-vhd'
$a = Join-Path $dir 'origen.vhdx'; $b = Join-Path $dir 'destino.vhdx'; $c = Join-Path $dir 'fat32.vhdx'
$exe = Join-Path $PSScriptRoot 'bin\Debug\net10.0-windows\EngineTests.exe'
$code = 1
try {
    if (Test-Path $dir) { Get-ChildItem $dir -Filter *.vhdx | ForEach-Object { try { Dismount-DiskImage -ImagePath $_.FullName -ErrorAction SilentlyContinue | Out-Null } catch {} }; Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory $dir | Out-Null
    $dp = Join-Path $dir 'crear.txt'
    @"
create vdisk file="$a" maximum=700 type=expandable
attach vdisk
create vdisk file="$b" maximum=900 type=expandable
attach vdisk
create vdisk file="$c" maximum=40960 type=expandable
attach vdisk
"@ | Set-Content $dp -Encoding ascii
    diskpart /s $dp | Out-Null
    $da = (Get-DiskImage -ImagePath $a | Get-Disk).Number
    $db = (Get-DiskImage -ImagePath $b | Get-Disk).Number
    $dc = (Get-DiskImage -ImagePath $c | Get-Disk).Number
    Initialize-Disk -Number $dc -PartitionStyle GPT
    Write-Host "VHD origen = disco $da, destino = disco $db"
    if ($da -lt 1 -or $db -lt 1 -or $dc -lt 1) { throw 'No se identificaron los discos virtuales' }

    # Origen MBR: «Reservada» activa de 100 MB + datos en una posición desalineada (sector 63 tras 101 MiB)
    $dp2 = Join-Path $dir 'origen.txt'
    @"
select disk $da
convert mbr
create partition primary size=100
format fs=ntfs label=RESERVADA quick
active
create partition primary size=500 offset=103425
format fs=ntfs label=DATOS quick
assign
"@ | Set-Content $dp2 -Encoding ascii
    diskpart /s $dp2 | Out-Null
    Start-Sleep 2
    $p2 = Get-Partition -DiskNumber $da | Sort-Object Size -Descending | Select-Object -First 1
    if (-not $p2.DriveLetter -or $p2.DriveLetter -eq [char]0) { $p2 | Add-PartitionAccessPath -AssignDriveLetter; Start-Sleep 2; $p2 = Get-Partition -DiskNumber $da -PartitionNumber $p2.PartitionNumber }
    Write-Host "Partición de datos en offset $($p2.Offset) (alineada a 1 MiB: $($p2.Offset % 1MB -eq 0)), letra $($p2.DriveLetter)"
    $root = "$($p2.DriveLetter):\"

    # Datos de prueba: un archivo grande, muchos pequeños y subcarpetas; luego se borra parte (huecos libres)
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    function W($rel, $size) { $f = Join-Path $root $rel; New-Item -ItemType Directory -Force (Split-Path $f) | Out-Null; $buf = New-Object byte[] $size; $rng.GetBytes($buf); [IO.File]::WriteAllBytes($f, $buf) }
    W 'grande.bin' (60MB)
    1..150 | ForEach-Object { W ("docs\nivel1\archivo$_.dat") (Get-Random -Minimum 1000 -Maximum 400000) }
    1..40 | ForEach-Object { W ("borrar\b$_.tmp") (1MB) }
    Remove-Item (Join-Path $root 'borrar') -Recurse -Force
    W 'fotos\2026\verano.jpg' (3MB)
    $hashes = @{}
    Get-ChildItem $root -Recurse -File | Where-Object { $_.FullName -notmatch 'System Volume Information' } | ForEach-Object {
        $hashes[$_.FullName.Substring($root.Length)] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
    $hashFile = Join-Path $dir 'hashes.json'
    $hashes | ConvertTo-Json | Set-Content $hashFile -Encoding utf8
    Write-Host "$($hashes.Count) archivos de prueba creados"

    & $exe --vhd $da $db $hashFile $dc
    $code = $LASTEXITCODE
}
catch { Write-Host "ERROR: $_"; $code = 99 }
finally {
    foreach ($v in $a, $b, $c) { try { Dismount-DiskImage -ImagePath $v -ErrorAction SilentlyContinue | Out-Null } catch {} }
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Código de salida: $code"
    Stop-Transcript | Out-Null
}
exit $code

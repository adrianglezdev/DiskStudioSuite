# Pruebas de integración de Disk Studio Suite sobre DISCOS VIRTUALES (VHDX) temporales.
# No toca ningún disco real: crea los VHDX, los usa y los borra. Requiere administrador.
# Cada disco se comprueba antes de usarlo: si no es virtual o no tiene el tamaño esperado, la prueba se detiene.
param([string]$Log = "$env:TEMP\dss-vhd-tests.log")
$ErrorActionPreference = 'Stop'
Start-Transcript -Path $Log -Force | Out-Null
$dir = Join-Path $env:TEMP 'dss-vhd'
$exe = Join-Path $PSScriptRoot 'bin\Debug\net10.0-windows\EngineTests.exe'
$vhds = [ordered]@{ origen = 700; destino = 900; fat32 = 40960; editor = 2048; restaurar = 2048 }   # MB
$disks = @{}
$code = 1

function Assert-Virtual([int]$n, [long]$mb) {
    $d = Get-Disk -Number $n
    if ($d.BusType -ne 'File Backed Virtual' -or $d.IsSystem -or $d.IsBoot -or [math]::Abs($d.Size - $mb * 1MB) -gt 8MB) {
        throw "El disco $n ($($d.FriendlyName), $($d.BusType), $([math]::Round($d.Size/1MB)) MB) no es el disco virtual esperado: prueba detenida"
    }
}

function Attach-New([string]$name, [long]$mb) {
    $f = Join-Path $dir "$name.vhdx"
    $s = Join-Path $dir "crear-$name.txt"
    "create vdisk file=`"$f`" maximum=$mb type=expandable`r`nselect vdisk file=`"$f`"`r`nattach vdisk" | Set-Content $s -Encoding ascii
    diskpart /s $s | Out-Null
    $n = $null
    for ($i = 0; $i -lt 40 -and $null -eq $n; $i++) { Start-Sleep -Milliseconds 500; try { $n = (Get-DiskImage -ImagePath $f | Get-Disk -ErrorAction Stop).Number } catch {} }
    if ($null -eq $n) { throw "No se pudo conectar $f" }
    Assert-Virtual $n $mb
    Write-Host "VHD $name = disco $n ($mb MB)"
    return $n
}

function Free-Letter { [char[]]'ZYXWVUTSRQPONMLKJIHG' | Where-Object { -not (Test-Path "$($_):\") } | Select-Object -First 1 }

try {
    if (Test-Path $dir) { Get-ChildItem $dir -Include *.vhd, *.vhdx, *.iso -Recurse | ForEach-Object { try { Dismount-DiskImage -ImagePath $_.FullName -ErrorAction SilentlyContinue | Out-Null } catch {} }; Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory $dir | Out-Null
    foreach ($k in $vhds.Keys) { $disks[$k] = Attach-New $k $vhds[$k]; Start-Sleep 1 }
    $da = $disks.origen; $db = $disks.destino; $dc = $disks.fat32
    Initialize-Disk -Number $dc -PartitionStyle GPT

    # Origen MBR: «Reservada» activa de 100 MB + datos en una posición desalineada (sector 63 tras 101 MiB)
    Assert-Virtual $da $vhds.origen
    $dp2 = Join-Path $dir 'origen.txt'
    @"
select disk $da
convert mbr
create partition primary size=100
format fs=ntfs label=RESERVADA quick
active
create partition primary size=500 offset=103425
format fs=ntfs label=DATOS quick
"@ | Set-Content $dp2 -Encoding ascii
    diskpart /s $dp2 | Out-Null
    Start-Sleep 2
    $p2 = Get-Partition -DiskNumber $da | Sort-Object Size -Descending | Select-Object -First 1
    if (-not $p2.DriveLetter -or $p2.DriveLetter -eq [char]0) {
        Set-Partition -DiskNumber $da -PartitionNumber $p2.PartitionNumber -NewDriveLetter (Free-Letter)
        Start-Sleep 2
        $p2 = Get-Partition -DiskNumber $da -PartitionNumber $p2.PartitionNumber
    }
    if (-not $p2.DriveLetter -or $p2.DriveLetter -eq [char]0) { throw 'La partición de datos del origen no tiene letra' }
    Write-Host "Partición de datos en offset $($p2.Offset) (alineada a 1 MiB: $($p2.Offset % 1MB -eq 0)), letra $($p2.DriveLetter)"
    $root = "$($p2.DriveLetter):\"

    # Datos de prueba: un archivo grande, muchos pequeños y subcarpetas; luego se borra parte (huecos libres)
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    function W($rel, $size) { $f = Join-Path $root $rel; $pd = Split-Path $f; if (-not (Test-Path $pd)) { New-Item -ItemType Directory -Force $pd | Out-Null }; $buf = New-Object byte[] $size; $rng.GetBytes($buf); [IO.File]::WriteAllBytes($f, $buf) }
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

    foreach ($k in 'origen', 'destino', 'fat32') { Assert-Virtual $disks[$k] $vhds[$k] }
    & $exe --vhd $da $db $hashFile $dc
    $code = $LASTEXITCODE

    # Funciones de la 0.2: editor de particiones, reparación, BitLocker, bloqueos, capacidad real,
    # imágenes incrementales, restauración, USB de arranque, conversión y montaje
    foreach ($k in 'editor', 'restaurar') { Assert-Virtual $disks[$k] $vhds[$k] }
    & $exe --vhd2 $disks.editor $disks.restaurar auto $dir
    if ($LASTEXITCODE -ne 0) { $code = [Math]::Max($code, $LASTEXITCODE) }
}
catch { Write-Host "ERROR: $_"; $code = 99 }
finally {
    Get-ChildItem $dir -Include *.vhd, *.vhdx, *.iso -Recurse -ErrorAction SilentlyContinue | ForEach-Object { try { Dismount-DiskImage -ImagePath $_.FullName -ErrorAction SilentlyContinue | Out-Null } catch {} }
    Start-Sleep 2
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Código de salida: $code"
    Stop-Transcript | Out-Null
}
exit $code

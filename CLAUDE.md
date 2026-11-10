# Notas del proyecto · Disk Studio Suite

Aplicación nativa de Windows (WPF, .NET 10, tema Fluent) para gestionar discos: explorador de doble panel, cola de copias, sincronización, clonado e imágenes, conversión MBR↔GPT, formateo, salud SMART y herramientas (espacio, duplicados, hash, renombrado, borrado seguro, benchmark).

- Nombre visible: **Disk Studio Suite** · ejecutable `DiskStudioSuite.exe` · espacio de nombres `DiskStudio`.
- Antes se llamaba *Copyrobot*. La carpeta del repositorio aún puede tener ese nombre, y los ajustes antiguos se migran solos.
- Marca (logo, paleta, pantalla de carga): ver [docs/MARCA.md](docs/MARCA.md). Los colores viven en `src/Themes/Styles.xaml` y `src/Brand.cs`. **No usar colores sueltos** fuera de la paleta, salvo los de estado (correcto, aviso, error).
- Interfaz en español, tratando de tú.

## Estructura

- `src/Core`: motores sin interfaz.
  - `Disks.cs`: detección de discos y SMART.
  - `Transfer.cs`: cola, copia, sincronización y filtros.
  - `PartitionTable.cs`: tablas MBR/GPT, conversión y copia de la tabla.
  - `SmartClone.cs`: clonado por particiones, VSS, bloques usados y arranque.
  - `DiskImage.cs`: imágenes, clonado sector a sector y superficie.
  - `Formatter.cs`: formateo, reinicio de disco y FAT32 propio.
  - `Tools.cs`: utilidades.
  - `AutoTasks.cs`: tareas automáticas.
  - `AppState.cs`: ajustes, registro y estado.
- `src/Views`: páginas. Unas en XAML y otras construidas por código con los ayudantes de `UI.cs`.
- `src/Themes/Styles.xaml`: estilos y paleta.
- `tests`: pruebas de los motores (`dotnet run --project tests`) y de integración en discos virtuales (`tests/vhd-tests.ps1`, como administrador).
- `docs`: documentación y marca.

## Compilar y publicar

```powershell
.\build.ps1                       # Debug
.\build.ps1 -Release              # dist\DiskStudioSuite.exe (un solo archivo, autocontenido)
dotnet run --project tests        # pruebas sin administrador
```

Las releases se generan solas en GitHub al subir una etiqueta `vX.Y.Z`: ver `.github/workflows/release.yml`.

## Modos de prueba (variables de entorno)

- `DSS_SNAP="carpeta;pagina1,pagina2:demo"`: la app guarda capturas de las páginas y se cierra. Las páginas con `ISnapshotDemo` admiten una acción de demostración.
- `DSS_SPLASHSNAP=carpeta`: guarda fotogramas de la pantalla de carga.
- `DSS_ACCENTDUMP=archivo`: lista los recursos del tema que siguen con el acento de Windows (diagnóstico de la paleta).

## Reglas de seguridad (no relajarlas)

- El disco del sistema nunca puede ser destino de clonado, restauración, formateo ni reinicio.
- Las operaciones destructivas piden escribir una frase de confirmación (`BORRAR DISCO N`, `FORMATEAR X:`…).
- Antes de tocar una tabla de particiones se guarda una copia restaurable en `%APPDATA%\Disk Studio Suite\TablasParticiones`.
- Las funciones a bajo nivel requieren administrador; la app arranca sin privilegios y ofrece reiniciarse elevada.
- No se suben al repositorio datos personales, capturas con números de serie o rutas de usuario, ni binarios (estos van a las releases).

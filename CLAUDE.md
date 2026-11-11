# Notas del proyecto · Disk Studio Suite

Aplicación nativa de Windows (WPF, .NET 10, tema Fluent) para gestionar discos: explorador de doble panel, cola de copias, sincronización, clonado e imágenes (completas, incrementales, montar y convertir), conversión MBR↔GPT, editor de particiones, formateo, USB de arranque, salud SMART con vigilancia en segundo plano, copias programadas y por versiones, y herramientas (espacio, duplicados, hash, renombrado, borrado seguro, benchmark, capacidad real, bloqueos, reparación, BitLocker).

- Nombre visible: **Disk Studio Suite** · ejecutable `DiskStudioSuite.exe` · espacio de nombres `DiskStudio`.
- Antes se llamaba *Copyrobot*. La carpeta del repositorio aún puede tener ese nombre, y los ajustes antiguos se migran solos.
- Marca (logo, paleta, pantalla de carga): ver [docs/MARCA.md](docs/MARCA.md). Los colores viven en `src/Themes/Styles.xaml` y `src/Brand.cs`. **No usar colores sueltos** fuera de la paleta, salvo los de estado (correcto, aviso, error).
- Interfaz en español, tratando de tú. Se traduce al inglés en tiempo de ejecución (ver «Idiomas»).
- Novedades de cada versión en `CHANGELOG.md`: la release usa la sección `## [X.Y.Z]` como texto.

## Estructura

- `src/Core`: motores sin interfaz.
  - `Disks.cs`: detección de discos y SMART.
  - `Transfer.cs`: cola, copia, sincronización y filtros.
  - `PartitionTable.cs`: tablas MBR/GPT, conversión y copia de la tabla.
  - `SmartClone.cs`: clonado por particiones, VSS, bloques usados y arranque.
  - `DiskImage.cs`: imágenes, clonado sector a sector y superficie.
  - `Formatter.cs`: formateo, reinicio de disco y FAT32 propio.
  - `Tools.cs`: utilidades.
  - `DiskTools.cs`: capacidad real (pendrives falsos), qué programas usan una unidad, chkdsk/defrag y BitLocker.
  - `VirtualDisk.cs`: montar ISO/VHD/VHDX, pie de VHD fijo y conversión de imágenes.
  - `IncrementalImage.cs`: imágenes incrementales `.dssinc` y su cadena.
  - `BootUsb.cs`: USB de arranque desde ISO (FAT32 + división de install.wim, o DD).
  - `PartitionEditor.cs`: operaciones del editor de particiones (WMI de almacenamiento).
  - `Versions.cs`: copias por versiones con enlaces duros y cálculo de horarios.
  - `AutoTasks.cs`: tareas (disparador por disco y programadas).
  - `Background.cs`: historial y vigilancia de salud, comprobación de versiones, menú del Explorador, inicio con Windows e instancia única.
  - `Updater.cs`: actualización desde la app (instalada o portable), descarga con SHA-256 y sustitución del ejecutable.
  - `Lang.cs`: traducción de la interfaz.
  - `AppState.cs`: ajustes, registro y estado.
- `src/Views`: páginas. Unas en XAML y otras construidas por código con los ayudantes de `UI.cs`.
- `src/Themes/Styles.xaml`: estilos y paleta.
- `src/Lang/en.json`: diccionario español → inglés.
- `src/TrayIcon.cs`: icono del área de notificación (Shell_NotifyIcon, sin WinForms).
- `installer/DiskStudioSuite.iss`: instalador por usuario (Inno Setup 6). **No cambiar nunca su `AppId`**: es lo que hace que una versión nueva actualice la instalada. La app sabe que está instalada si la carpeta de su `.exe` coincide con `InstallLocation` en `HKCU\…\Uninstall\{AppId}_is1`.
- `tests`: pruebas de los motores (`dotnet run --project tests`), de integración en discos virtuales (`tests/vhd-tests.ps1`, como administrador) y de instalación y actualización de principio a fin (`tests/update-e2e.ps1`, sin administrador, tras `.\build.ps1 -Release -Installer`). Los `.ps1` se guardan en UTF-8 **con BOM**: PowerShell 5 lee mal los acentos sin él.
- `docs`: documentación y marca.

## Compilar y publicar

```powershell
.\build.ps1                       # Debug
.\build.ps1 -Release              # dist\DiskStudioSuite.exe (un solo archivo, autocontenido)
.\build.ps1 -Release -Installer   # y dist\DiskStudioSuite-Setup-X.Y.Z.exe + SHA256SUMS.txt
dotnet run --project tests        # pruebas sin administrador
```

Las releases se generan solas en GitHub al subir una etiqueta `vX.Y.Z`: ver `.github/workflows/release.yml`.

## Modos de prueba (variables de entorno)

- `DSS_SNAP="carpeta;pagina1,pagina2:demo"`: la app guarda capturas de las páginas y se cierra. Las páginas con `ISnapshotDemo` admiten una acción de demostración.
- `DSS_SPLASHSNAP=carpeta`: guarda fotogramas de la pantalla de carga.
- `DSS_LANG=en|es`: fuerza el idioma sin tocar los ajustes.
- `DSS_UPDATE_FEED=archivo.json`: usa una lista de versiones local (mismo formato que la API de releases de GitHub; las URL pueden ser rutas locales) y comprueba al instante.
- `DSS_UPDATE_AUTO=1`: instala la actualización encontrada sin preguntar. La versión reabierta no hereda estas variables.
- `DSS_LOG_FILE=archivo`: copia el registro de la app en un archivo.
- `DSS_ACCENTDUMP=archivo`: lista los recursos del tema que siguen con el acento de Windows (diagnóstico de la paleta).

## Idiomas

- El código escribe los textos en español. `L.T()` los traduce si el idioma es inglés, y `L.Apply()` recorre el árbol de controles de cada página, diálogo y menú. No toca los valores enlazados a datos.
- `en.json` admite plantillas con `{0}`, `{1}`… que sirven para traducir textos con datos (tamaños, letras, nombres).
- Al añadir textos nuevos, añádelos también a `en.json`. Para obtenerlos todos de nuevo, extrae los literales en español del código, tanto los normales como los interpolados convertidos en plantillas.
- Las frases de confirmación (`BORRAR DISCO N`) también se traducen; `Dialogs.TypeToConfirm` acepta la frase en los dos idiomas.

## Argumentos de línea de comandos

- `--segundo-plano`: arranca oculto con el icono junto al reloj (lo usa «Iniciar con Windows»).
- `--copiar <ruta>`, `--hash <ruta>`, `--espacio <ruta>`: los usa el menú del Explorador. Si la app ya está abierta, la segunda instancia le pasa los argumentos por una tubería con nombre y se cierra.
- `--reinicio`: espera a que se cierre la instancia anterior (al reiniciar como administrador o al cambiar de idioma).
- `--sin-presentacion`: sin pantalla de carga.
- `--actualizado`: la abre el actualizador; muestra el aviso de actualización completada y borra el `.exe.old` de la versión portable.
- `--limpiar-integracion`: la ejecuta el desinstalador; quita el inicio con Windows y el menú del Explorador y sale.

## Reglas de seguridad (no relajarlas)

- El disco del sistema nunca puede ser destino de clonado, restauración, formateo ni reinicio.
- Las operaciones destructivas piden escribir una frase de confirmación (`BORRAR DISCO N`, `FORMATEAR X:`, `BORRAR PARTICIÓN N`…).
- El editor de particiones no deja eliminar particiones de arranque, del sistema ni las EFI/MSR/recuperación del disco de Windows.
- Antes de tocar una tabla de particiones se guarda una copia restaurable en `%APPDATA%\Disk Studio Suite\TablasParticiones`.
- Las funciones a bajo nivel requieren administrador; la app arranca sin privilegios y ofrece reiniciarse elevada.
- No se suben al repositorio datos personales, capturas con números de serie o rutas de usuario, ni binarios (estos van a las releases).

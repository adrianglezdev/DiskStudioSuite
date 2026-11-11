<p align="center">
  <img src="docs/marca/logo.png" alt="Disk Studio Suite" width="180">
</p>

<h1 align="center">Disk Studio Suite</h1>

<p align="center">
  Gestor de discos para Windows: copias, sincronización, clonado, imágenes, formateo y salud de los discos, todo en una sola aplicación.
</p>

<p align="center">
  <a href="https://github.com/adrianglezdev/DiskStudioSuite/releases"><b>⬇ Descargar</b></a> ·
  <a href="LICENSE">Licencia MIT</a>
</p>

> **Versión 0.2.0 · en pruebas.** Funciona, pero aún no se ha probado en muchos equipos. Haz copia de seguridad antes de clonar, convertir, particionar o formatear discos. Consulta las [novedades](CHANGELOG.md).

---

Aplicación nativa de Windows 10/11 (WPF · .NET 10) con el tema Fluent de Windows 11, en modo claro u oscuro, **en español o en inglés**. Se descarga como **un único `DiskStudioSuite.exe` portable**: no necesita instalación ni tener .NET instalado, y funciona también desde un USB.

## Funciones

| Sección | Qué hace |
|---|---|
| **Discos** | Panel con cada disco físico: modelo, firmware, bus (USB/SATA/NVMe…), SSD o HDD, GPT o MBR, salud en %, temperatura, horas de uso, desgaste y atributos SMART (con smartmontools si está instalado), con **historial y gráfica**. Muestra el mapa de particiones y los volúmenes con su ocupación, y detecta al instante cuándo se conecta o desconecta un disco. |
| **Particiones** | Editor de particiones: crear, eliminar, reducir, ampliar y dividir particiones, cambiar letra y nombre, marcar la partición activa e inicializar discos vacíos. Antes de cada cambio guarda una copia de la tabla. |
| **Explorador** | Doble panel con pestañas, historial, ruta navegable, filtro rápido, favoritos, vista rápida (F3), arrastrar y soltar, portapapeles de Windows y menú contextual completo. |
| **Transferencias** | Cola de copias: pausar y reanudar, gráfica de velocidad en vivo, verificación por hash, reglas para archivos que ya existen, reintento de los fallidos, límite de velocidad, y suspender o apagar al terminar. |
| **Sincronizar** | Compara dos carpetas, muestra las diferencias y aplica **Actualizar**, **Espejo** o **Bidireccional**, con filtros. |
| **Clonar e imágenes** | Clonado de archivos · imagen de disco `.img`/`.img.gz` con SHA-256, **completa o incremental** · restaurar imagen · **clonar disco** (sector a sector, o partición a partición eligiendo MBR o GPT, con *Optimizar para SSD* e instantánea VSS) · **MBR ↔ GPT sin perder datos** · verificar imagen · **montar y convertir** imágenes ISO, VHD, VHDX e `.img` · análisis de superficie. |
| **Formatear** | NTFS, exFAT o FAT32 (también **FAT32 de más de 32 GB**). Elige el nombre y el tamaño de clúster, y si quieres formato rápido o completo. Puedes **reiniciar un disco completo**, lo que arregla pendrives que muestran menos capacidad, salen como RAW o están protegidos contra escritura por software. Incluye una guía «¿Qué formato elijo?». |
| **USB de arranque** | Crea un pendrive de instalación de Windows o Linux desde una ISO: UEFI + BIOS (MBR), UEFI (GPT) o imagen DD. Si el `install.wim` pasa de 4 GB, lo divide solo para que quepa en FAT32. |
| **Herramientas** | Analizador de espacio (mapa visual) · duplicados · búsqueda avanzada · velocidad del disco · hash y verificación · renombrado masivo · borrado seguro · **capacidad real (pendrives falsos)** · **¿por qué no puedo expulsarlo?** · **comprobar, reparar y optimizar** · **BitLocker**. |
| **Copias y tareas** | Copias de seguridad que se ejecutan solas **al conectar un disco concreto** o **a una hora programada**, también **por versiones** (cada fecha se puede abrir o restaurar, y los archivos sin cambios no ocupan espacio otra vez). |
| **En segundo plano** | Icono junto al reloj, inicio con Windows, **avisos si un disco empeora o se calienta**, aviso de nuevas versiones y, si quieres, opciones en el menú del Explorador. |

Cada opción delicada tiene su botón ⓘ con una explicación en lenguaje llano.

## Seguridad

- La aplicación se abre **sin privilegios**. Las funciones de bajo nivel (SMART completo, imágenes, clonado, formateo, MBR/GPT) se activan con *Ejecutar como admin*.
- El **disco del sistema nunca se ofrece como destino** de operaciones que borran datos.
- Las acciones destructivas muestran qué se va a borrar y piden **escribir una frase de confirmación** (por ejemplo `BORRAR DISCO 2`).
- Antes de modificar una tabla de particiones se guarda **una copia que se puede restaurar**.
- La comprobación de actualizaciones solo consulta la lista pública de versiones en GitHub: no envía ningún dato del equipo.

> Como con cualquier herramienta de discos: **haz copia de seguridad de lo importante** antes de clonar, convertir o formatear.

## Requisitos

- Windows 10 u 11 de 64 bits.
- Opcional: [smartmontools](https://www.smartmontools.org/) para ver todos los atributos SMART.

El ejecutable no está firmado digitalmente, así que Windows SmartScreen puede avisar la primera vez. Pulsa *Más información → Ejecutar de todas formas*. Junto a cada versión se publica su suma `SHA-256` para comprobar que la descarga está íntegra.

## Compilar desde el código

Requiere el [SDK de .NET 10](https://dotnet.microsoft.com/download).

```powershell
git clone https://github.com/adrianglezdev/DiskStudioSuite.git
cd DiskStudioSuite
.\build.ps1                    # compilación de desarrollo
.\build.ps1 -Release           # genera dist\DiskStudioSuite.exe
dotnet run --project tests     # pruebas de los motores
.\tests\vhd-tests.ps1          # (como administrador) pruebas sobre discos virtuales temporales
```

## Publicar una versión

Las versiones se generan solas con GitHub Actions al subir una etiqueta:

```powershell
git tag v0.2.0
git push origin v0.2.0
```

El flujo compila, pasa las pruebas y crea la *release* con `DiskStudioSuite.exe` y su `SHA256SUMS.txt`, usando como texto la sección de esa versión en [CHANGELOG.md](CHANGELOG.md). Las versiones `0.x` se publican como *pre-release*.

## Estructura

| Carpeta | Contenido |
|---|---|
| `src/Core` | Motores: discos, transferencias, sincronización, particiones, clonado, imágenes, formateo y herramientas. |
| `src/Views` | Interfaz. |
| `src/Themes` | Estilos y paleta. |
| `src/Lang` | Traducción al inglés (`en.json`). |
| `tests` | Pruebas. |
| `docs` | Documentación y [guía de marca](docs/MARCA.md). |

## Licencia

[MIT](LICENSE) © 2026 AdrianGlezDev

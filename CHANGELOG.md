# Novedades

Todas las versiones de Disk Studio Suite. El flujo de publicación usa la sección de cada versión como texto de su *release*.

## [0.3.2] - 2026-11-12

### Nuevo
- **Ventana sin la barra de título de Windows**: los botones de minimizar, maximizar y cerrar pasan a estar integrados en la propia app, con su mismo estilo, y el contenido gana altura.
  - Se sigue pudiendo arrastrar la ventana desde la franja superior, maximizarla con doble clic y redimensionarla desde cualquier borde.
  - En Windows 11, al pasar el ratón por el botón de maximizar aparecen los diseños de acoplamiento.
  - El menú del sistema (mover, tamaño, minimizar…) se abre con Alt+Espacio.
  - Con un diálogo abierto se puede seguir minimizando o cerrando la ventana.

## [0.3.1] - 2026-11-12

### Corregido
- **Clonar a un disco algo más pequeño**: antes se rechazaba con «Las particiones no caben en el disco destino», aunque los datos cupieran de sobra.
  - Ahora se reduce la partición de datos con más espacio libre para que quepa justo, siempre que sus datos quepan con margen.
  - Esa partición se crea de nuevo con el mismo formato, nombre y tamaño de clúster, y se copia archivo a archivo con Robocopy. Se conservan permisos, propietario, fechas, atributos, enlaces y uniones, y se lee desde la instantánea si está activada.
  - El resto de particiones se sigue copiando sector a sector, y el disco de origen no se modifica.
  - La vista previa explica qué partición se reduce y hasta qué tamaño.
  - Si la partición reducida contiene Windows, avisa de que, para que arranque con seguridad, es mejor reducirla antes en el origen con «Particiones».
- Si el destino es más pequeño, el clonado se hace siempre partición a partición, aunque no se marque «Optimizar para SSD».

## [0.3.0] - 2026-11-12

Llegan el instalador y la actualización desde la propia app.

### Nuevo
- **Instalador** (`DiskStudioSuite-Setup-X.Y.Z.exe`): se instala por usuario, sin necesidad de administrador, en `%LOCALAPPDATA%\Programs\Disk Studio Suite`.
  - Crea un acceso en el menú Inicio y, si quieres, otro en el escritorio, y aparece en «Aplicaciones instaladas».
  - Al desinstalar, quita el inicio con Windows y el menú del Explorador, y pregunta si quieres borrar tus ajustes.
  - La versión portable de un solo archivo se sigue publicando.
- **Actualización desde la app**:
  - Al encontrar una versión nueva, muestra sus novedades con los botones *Actualizar ahora*, *Más tarde* y *Omitir esta versión*.
  - Descarga con barra de progreso, velocidad y opción de cancelar, y comprueba el SHA-256 con `SHA256SUMS.txt` antes de instalar nada.
  - Si la app está instalada, ejecuta el instalador en silencio. Si es la portable, sustituye su propio ejecutable y vuelve atrás si algo falla. En los dos casos se vuelve a abrir ya actualizada.
  - Si hay operaciones en la cola, espera a que terminen.
- **Ajustes de actualización**: instalar las actualizaciones automáticamente e incluir o no las versiones en pruebas. Además, Ajustes indica si usas la versión instalada o la portable.

### Pruebas
- `tests/update-e2e.ps1` prueba de principio a fin, sin administrador:
  - la actualización de la versión portable;
  - la instalación;
  - la actualización de la versión instalada;
  - la desinstalación.

## [0.2.1] - 2026-11-11

Correcciones encontradas al probar todas las funciones de bajo nivel sobre discos virtuales.

### Corregido
- **FAT32 de más de 32 GB**: el formato se completaba, pero la tarea terminaba con error al final (fallo presente desde la 0.1.0).
- **Imágenes de un disco en uso**: los archivos guardados justo antes de crear una imagen completa o incremental podían quedarse fuera, porque Windows aún no los había escrito en el disco. Ahora se vuelcan antes de empezar a leer.
- **Particiones sin letra**: el editor de particiones ya muestra su sistema de archivos y su nombre (por ejemplo, en las particiones EFI y de recuperación).
- **Montar VHD y VHDX**: la imagen se montaba, pero la app no mostraba su letra de unidad.

### Pruebas
- La prueba de integración sobre discos virtuales (`tests/vhd-tests.ps1`) cubre también el editor de particiones, la reparación, BitLocker, los programas que bloquean una unidad, la capacidad real, las imágenes incrementales, el USB de arranque, y la conversión y el montaje de imágenes.
- Antes de escribir en un disco, la prueba comprueba que es un disco virtual con el tamaño esperado; si no lo es, se detiene.

## [0.2.0] - 2026-11-11

Segunda versión en pruebas. Añade el editor de particiones, el USB de arranque, la vigilancia en segundo plano, las copias programadas con versiones y la interfaz en inglés.

### Nuevas secciones
- **Particiones**: editor completo de particiones. Permite crear, eliminar, reducir, ampliar y dividir particiones, cambiar la letra o el nombre, marcar la partición activa (MBR) e inicializar discos vacíos como GPT o MBR. Antes de cada cambio guarda una copia de la tabla de particiones. Las particiones que Windows necesita para arrancar no se pueden eliminar.
- **USB de arranque**: crea un pendrive desde una ISO, como Rufus. Tiene tres modos: *UEFI + BIOS (MBR)*, *UEFI (GPT)* e *Imagen DD* para las ISO híbridas de Linux. Analiza la ISO y recomienda el modo adecuado. Si el `install.wim` de Windows pasa de 4 GB, lo divide solo para que quepa en FAT32.

### Clonar e imágenes
- **Imágenes incrementales** (`.dssinc`): tras una imagen completa, solo se guardan los bloques que han cambiado. Al restaurar, se combina sola toda la cadena hasta la fecha que elijas.
- **Montar imágenes** como una unidad más para sacar archivos sin restaurar nada (ISO, VHD, VHDX e `.img`), en solo lectura si quieres.
- **Convertir imágenes** entre `.img`, `.img.gz`, `.vhd` y `.vhdx`, o de una cadena incremental a una imagen completa.
- Restaurar y verificar ahora también admiten VHD, VHDX e imágenes incrementales.
- Al clonar avisa si alguna unidad del disco de origen está cifrada con BitLocker.

### Herramientas
- **Capacidad real**: detecta pendrives y tarjetas falsos o dañados. Escribe datos de prueba en todo el espacio libre y los vuelve a leer sin la caché de Windows, como H2testw.
- **¿Por qué no puedo expulsarlo?**: muestra qué programas tienen archivos abiertos en una unidad y permite cerrarlos. Al expulsar un disco desde la app, avisa si algo lo está usando.
- **Comprobar y reparar**: busca y corrige errores del sistema de archivos con `chkdsk`. En la unidad de Windows, la reparación queda programada para el siguiente reinicio. También optimiza la unidad: TRIM en SSD y desfragmentación en discos duros.
- **BitLocker**: estado del cifrado de cada unidad. Permite desbloquearla con contraseña o clave de recuperación, y suspender, reanudar o bloquear la protección.

### Copias y tareas
- **Copias programadas**: cada cierto número de horas, todos los días o algunos días de la semana. Si el equipo estaba apagado a la hora prevista, se hacen al abrir la app.
- **Copias por versiones**: cada ejecución crea una carpeta con la fecha. Los archivos sin cambios se enlazan con la versión anterior (enlaces duros de NTFS), así que cada versión solo ocupa lo que cambió. Se conservan las últimas *N*, y cualquier fecha se puede abrir o restaurar.

### Segundo plano y avisos
- **Icono junto al reloj**: al cerrar la ventana, la app puede seguir funcionando en segundo plano. También puede **iniciarse con Windows**.
- **Vigilancia de la salud**: avisa si un disco empeora, si aparecen sectores dañados o si supera la temperatura que elijas.
- **Historial de salud y temperatura** de cada disco, con una gráfica en «Detalles SMART».
- **Aviso de nueva versión**: comprueba las versiones publicadas en GitHub sin enviar datos del equipo. Se puede desactivar en Ajustes.

### Integración y otros cambios
- **Interfaz en inglés**: se elige en Ajustes. Por defecto sigue el idioma de Windows.
- **Menú del Explorador** (opcional): añade al clic derecho «Copiar con…», «Calcular hash con…» y «Analizar espacio con Disk Studio Suite».
- **Una sola ventana**: si la app ya está abierta, la petición se le pasa a esa ventana en lugar de abrir otra.
- Nuevos atajos: Ctrl+1 a Ctrl+9 y Ctrl+0 siguen el orden del menú lateral.
- La sección «Tareas automáticas» pasa a llamarse **Copias y tareas**.
- El analizador de espacio y el explorador ya no se detienen con archivos que tienen una fecha no válida.

## [0.1.0] - 2026-11-10

Primera versión pública (en pruebas): discos y salud SMART, explorador de doble panel, cola de transferencias, sincronización, clonado e imágenes, MBR ↔ GPT, formateo (FAT32 de más de 32 GB incluido) y herramientas.

# osmimport — pbf de OpenStreetMap → red vial ruteable

Herramienta de consola genérica que convierte un extracto `.osm.pbf` de OpenStreetMap en una red vial
ruteable (segmentos + nodos) y la publica en **PostgreSQL/PostGIS, MySQL 8 o SQL Server**, sin romper los
datos que ya dependen de ella. No necesita Docker, osm2pgsql ni PostGIS para procesar.

Nació para automatizar el proceso manual de SherpaSlb (`Resources/getDataOSM.txt` + `updateDataOSMreal.txt`),
pero no está atada a ningún proyecto: cada consumidor se describe con un perfil JSON.
Arquitectura, decisiones y resultados de validación: [PLAN.md](PLAN.md).

## Flujo

```
Geofabrik ──download──▶ region-AAMMDD.osm.pbf ──build──▶ red.gpkg ──publish──▶ BD (create | replace | sync)
                                                             │
                                                             └──compare──▶ reporte de paridad con la BD
```

1. **`build`** lee el pbf y genera un GeoPackage neutro (se abre arrastrándolo a QGIS) con la red lista.
2. **`compare`** (opcional) cruza ese GeoPackage con la red que ya está en la base, sin modificar nada.
3. **`publish`** la escribe en la base según el perfil.

## Requisitos e instalación

- **.NET 10** (runtime) para ejecutar; **SDK 10+** para compilar (C# 13 / .NET 10).
- Memoria: ~4 GB para procesar extractos a nivel país (ej. Colombia, Argentina).

```powershell
# Compilar y generar la distribución en dist/
dotnet publish src/OsmImporter.Cli -c Release -o dist
```

## 🚀 Cómo ejecutar el proyecto

### 1. Visor Web y Estudio Gráfico (Modo Visual — Recomendado)
Inicia el servidor web local con Kestrel y abre la interfaz gráfica interactiva en tu navegador en `http://localhost:5000`:

```powershell
# Usando el ejecutable compilado en dist:
.\dist\osmimport.exe serve

# O directamente con dotnet desde el código fuente:
dotnet run --project src/OsmImporter.Cli -- serve
```

**Especificaciones y Funcionalidades del Visor Web (`serve`):**

* 🎛️ **Panel lateral retráctil con cabecera fija**:
  * Botón integrado para **ocultar a la izquierda (◀) y re-desplegar** el panel lateral, maximizando el espacio de visualización cartográfica.
  * Cabecera de pestañas sólida (`sticky top-0 z-30 bg-slate-900`) con tipografía optimizada (`text-xs` y `text-sm`) que previene cualquier solapamiento visual durante el scroll interno.

* 📑 **5 Pestañas especializadas**:
  1. **📥 Carga de Data**:
     * Búsqueda en vivo y descarga de extractos oficiales `.osm.pbf` de Geofabrik.
     * Selector de perfil JSON y configuración de buffer perimetral.
     * **Delimitación de Área Operativa (BBox)**: botón interactivo **`✏️ Dibujar área`** (clic y arrastre directo en el mapa) y **`🎯 Capturar vista`**.
     * **Auto-captura inteligente**: si se inicia la construcción sin BBox, la interfaz ofrece capturar automáticamente la pantalla visible actual para proteger el rendimiento y evitar fallos por extractos masivos.
     * **Selector de Tipos de Vía para Ruteo**: presets con un clic (**Vehicular Estándar**, **SherpaSlb**, **Principales**) y advertencias contextuales al incluir vías no vehiculares.
     * **Consola de ejecución en vivo**: streaming de avance continuo cada 2M de elementos procesados.
  2. **🗺️ Capas & Filtros**:
     * **Click-to-Strike (Tachar para descartar)**: toca cualquier vía o casilla de verificación para tacharla (`line-through`) y descartarla del mapa en tiempo real.
     * **Modos de visualización**:
       * *Por Tipo de Vía*: colores normalizados según la jerarquía vial de OpenStreetMap.
       * *Por Sentido de Circulación*: flechas direccionales en vías de sentido único (`▸▸`, zoom ≥ 12) y trazo doble sólido en vías de doble sentido (`⇄`).
     * **Límites de Red**: demarcación luminosa del perímetro geográfico exacto con dimensiones en km y área en km².
     * **Estilos de Mapa Base**:
       * **Google Hybrid (por defecto)**: ortofoto satelital combinada con etiquetas de calles, ciudades y carreteras.
       * **Esri World Imagery**: fotografía satelital de alta resolución limpia.
       * **CartoDB Dark & Positron**: mapas vectoriales de alto contraste para análisis nocturno y diurno.
       * Atribución y normas de uso oficiales integradas (ODbL, Geofabrik, Google, Esri).
  3. **📊 Métricas & Calidad**:
     * Tarjetas KPI de longitud total en km, cantidad de micro-segmentos, nodos topológicos y componentes conexas.
     * Alcance territorial con botón de centrado automático (`Centrar área ↗`).
     * Distribución kilométrica por tipo de vía con barras de porcentaje interactivas (también compatibles con **Click-to-Strike** para filtrar el mapa).
     * Tiempos de procesamiento desglosados y auditoría de calidad.
  4. **⚖️ Paridad**:
     * Reporte visual del diff contra la base de datos de producción (`.parity.json`), indicando segmentos coincidentes, modificados y retirados.
  5. **📁 Carpetas del Sistema**:
     * Panel con rutas físicas reales del proyecto y botones de un clic para **abrir en el Explorador de Windows** tanto la carpeta de descargas (`downloads\`) como la carpeta de salida (`output\`).

* ⏳ **Modal de Proceso en Segundo Plano**:
  * Durante descargas o compilación de redes, la interfaz presenta una barra animada y consola en tiempo real.
  * Incluye el botón **`👀 Ver mapa`** para minimizar el modal a una pastilla flotante sin interrumpir la tarea, permitiendo inspeccionar el mapa o la consola libremente.

---

### 2. Especificaciones de Tipos de Vías para Ruteo Vehicular

Al generar la red vial (`build`), es crucial definir qué vías deben formar parte del grafo ruteable para que los algoritmos de ruteo (Dijkstra, A*, Contraction Hierarchies, etc.) no generen rutas inviables o atajos peligrosos.

| Vía OSM | Etiqueta | Preset SherpaSlb | Preset Vehicular Estricto | Recomendación para Ruteo |
|---|---|:---:|:---:|---|
| **`motorway`** | Autopista | ✅ Sí | ✅ Sí | Vías de alta velocidad y acceso controlado. Tránsito vehicular prioritario. |
| **`trunk`** | Vía Troncal | ✅ Sí | ✅ Sí | Carreteras de alta capacidad intermunicipales o vías rápidas urbanas. |
| **`primary`** | Vía Primaria | ✅ Sí | ✅ Sí | Red vial primaria nacional o avenidas arteriales urbanas. |
| **`secondary`** | Vía Secundaria | ✅ Sí | ✅ Sí | Rutas conectoras secundarias entre municipios o barrios. |
| **`tertiary`** | Vía Terciaria | ✅ Sí | ✅ Sí | Calles colectoras intermedias de tránsito público fluido. |
| **`residential`** | Residencial | ✅ Sí | ✅ Sí | Calles urbanas habitacionales de libre circulación vehicular. |
| **`unclassified`** | Rural / Menor | ✅ Sí | ✅ Sí | Vías rurales o menores transitables para automóviles y camiones. |
| **`service`** | Vía de Servicio | ✅ Sí | ❌ Excluido | **Atención:** Incluye callejones, accesos a parqueaderos, servidumbres y caminos internos de condominios. En ruteo estricto se descarta para evitar que el algoritmo ataje por propiedad privada. |
| **`track`** | Pista / Huella | ✅ Sí | ❌ Excluido | **Atención:** Caminos de tierra, trochas o caminos agrícolas. Inadecuados para vehículos de ciudad o transporte de carga estándar. |
| **`*_link`** | Rampas / Enlaces | Opcional | Opcional | Rampas de acceso y orejas de retorno para autopistas y troncales (`motorway_link`, `trunk_link`, etc.). |
| **`living_street`**| Zona 30 / Callejón | Opcional | Opcional | Calles con prioridad peatonal absoluta y restricción estricta de velocidad. |

---

### 3. Ejecución por Consola (CLI / Automatización)
Para pipelines desatendidos o scripts de CI/CD:

```powershell
$exe = ".\dist\osmimport.exe"
& $exe help
```

## Caso típico: actualizar la red de un proyecto en uso

```powershell
$env:OSMIMPORT_CONNECTION = "Host=...;Port=5432;Database=routing;Username=...;Password=..."

# 1) Bajar el extracto más reciente y construir la red con el perfil del proyecto (~2 min)
& $exe build --download argentina --profile sherpaslb --out output\argentina.gpkg

# 2) (opcional) ¿Qué tan distinta es de lo que hay en la base?
& $exe compare --profile sherpaslb --gpkg output\argentina.gpkg

# 3) Sincronizar: simula todo, muestra el diff y las dependencias afectadas, y pide confirmación
& $exe publish --profile sherpaslb --gpkg output\argentina.gpkg --mode sync
```

`sync` conserva el Id de todo lo que no cambió, inserta lo nuevo, resuelve las filas de otras tablas que
apuntaban a lo retirado y recién entonces lo borra. Todo en una transacción, con respaldo `*_bak_<fecha>`.
Repetirlo con el mismo GeoPackage no cambia nada.

## Comandos

| Comando | Para qué |
|---|---|
| `regions [--search texto]` | Lista las regiones de Geofabrik (id, nombre, región padre) |
| `download --region <id\|ruta> [--out carpeta] [--force]` | Baja el extracto con su fecha en el nombre y verifica el MD5; si ya existe y el MD5 coincide, lo reutiliza |
| `build (--pbf <archivo> \| --download <región>) [opciones]` | Construye la red y la guarda en `.gpkg` + `.report.json` |
| `build ... --coverage-only` | Solo muestra qué tipos de vía (`highway`) hay en el área y cuántos km |
| `compare --gpkg <archivo>` | Paridad gpkg ↔ base (por llave natural), con reporte JSON |
| `publish --gpkg <archivo> --mode create` | Base nueva: crea las tablas y carga |
| `publish --gpkg <archivo> --mode replace` | Respalda, vacía y recarga. **Se niega** si alguna fila de otra tabla depende de la red |
| `publish --gpkg <archivo> --mode sync` | Actualiza una base en uso (ver arriba) |
| `serve [--gpkg <archivo>] [--port 5000]` | Levanta un visor web interactivo en localhost con mapa MapLibre GL, métricas y filtros |

Opciones de `publish`: `--dry-run` (en `sync` ejecuta todo y revierte: muestra exactamente lo que pasaría),
`--yes` (no preguntar), `--no-backup`, `--max-retired-percent <n>`, `--schema`, `--nodes-table`,
`--segments-table`, `--provider`. Opciones de `build`: `osmimport help`.

**Cadena de conexión**: `--connection`, variable de entorno `OSMIMPORT_CONNECTION` o se pide sin mostrarla.
Nunca se guarda en el perfil ni aparece en los logs.

## Perfiles

Sin perfil, la herramienta usa valores genéricos: tablas `road_nodes` / `road_segments` en snake_case y
`--bbox` obligatorio. Todo lo propio de un proyecto va en `profiles/<proyecto>.json` y se usa con
`--profile <proyecto>`. Cualquier opción de línea de comandos sobrescribe el perfil.

| Perfil | Destino |
|---|---|
| `sherpaslb.json` | SherpaSlb sobre PostgreSQL/PostGIS (`ReferenceRoadNodes` / `ReferenceRoadSegments`) |
| `sherpaslb-mysql.json` | SherpaSlb sobre MySQL (incluye columnas generadas `StartPoint`/`EndPoint`) |

### Sección `build`

| Clave | Default | Descripción |
|---|---|---|
| `bbox` | — (obligatorio) | `{ minLon, minLat, maxLon, maxLat }` del área operativa |
| `bufferMeters` | 20000 | Margen alrededor del bbox, en metros reales |
| `legacyBuffer` | false | Aplicar el margen en unidades EPSG:3857 como el SQL original (≈ 15,6 km reales a −38°) |
| `highways` | motorway … track (9 tipos) | Tipos de vía a incluir (por defecto el set de SherpaSlb: `motorway,trunk,primary,secondary,tertiary,residential,unclassified,service,track`). En la Web UI o con `--highways` se pueden filtrar; por ejemplo, el preset **Vehicular Estándar** excluye `service` y `track` para evitar que los algoritmos de ruteo tomen atajos por parqueaderos privados o huellas de tierra. |
| `splitWaysAt3857` | 100000 | Parte vías largas como osm2pgsql y solo conserva los tramos que tocan el área (0 = vía completa) |
| `segmentizeMeters` | 50 | Largo máximo de cada micro-segmento |
| `segmentizeMethod` | `bisect` | `bisect` = `ST_Segmentize(geography)` de PostGIS 3.4; `uniform` = partes iguales |
| `minSegmentMeters` | 1 | Se descartan segmentos de este largo o menos |
| `snapDegrees` | 0.00001 | Grilla para unificar nodos (≈ 1 m) |
| `reverseOnewayMinusOne` | true | `oneway=-1`: invertir la geometría y marcar sentido único |
| `excludeAreas` | true | Excluir vías cerradas con `area=yes` |
| `costFactors` / `defaultCostFactor` | 0,7 / 1,0 / 1,4 / 2,0 | `TravelCost = LengthMeters × factor` según el tipo de vía |

### Sección `publish`

| Clave | Default | Descripción |
|---|---|---|
| `provider` | `postgres` | `postgres` · `mysql` · `sqlserver` |
| `schema` | `public` | Esquema (Postgres), base de datos (MySQL; null = la de la conexión) o esquema (SQL Server; `dbo`) |
| `nodes` / `segments` | `road_nodes` / `road_segments` | Nombre de tabla y de cada columna. Columna opcional en `null` = no se escribe |
| `segments.startPoint` / `endPoint` | `start_point` / `end_point` | Solo MySQL al crear: columnas generadas con los extremos (índice espacial) |
| `sqlServerSpatialType` | `geography` | Solo SQL Server al crear: `geography` o `geometry` |
| `maxRetiredPercent` | 20 | `sync` aborta si retiraría más de este % de segmentos (protege contra un gpkg de otra zona) |
| `foreignKeyDefaults` | remapNearest 30 m → report | Estrategia para las FKs reales que apuntan a la red (se detectan solas) |
| `dependencies` | `[]` | Referencias **sin FK** que también apuntan a la red (tabla, columna, `references`, `filter`, estrategia) |

**Estrategias para filas que apuntan a nodos/segmentos retirados** (`onRetired`, y `fallback` si no hay candidato):

| Estrategia | Efecto |
|---|---|
| `remapNearest` | Se reasignan al nodo/segmento más cercano de la red nueva dentro de `maxDistanceMeters` (para segmentos se prefiere la misma vía OSM) |
| `setNull` | La referencia queda en NULL (falla si la columna no lo admite) |
| `report` | No se toca y se reporta. Si es una FK real, el elemento retirado se **conserva** (nunca queda una FK colgando) |
| `block` | Aborta todo el sync sin modificar nada |

El `filter` es SQL del motor destino: comillas dobles en Postgres (`"\"NetworkType\" = 2"`), backticks en MySQL
(`` "`NetworkType` = 2" ``), corchetes en SQL Server (`"[NetworkType] = 2"`).

## 📦 Archivos generados en `output/` y cómo utilizarlos

Cada ejecución del comando `build` genera en la carpeta `output\` un conjunto de archivos listos para consumo en software SIG, motores de bases de datos o análisis de datos.

> [!TIP]
> **¿Se pueden borrar los archivos de `output\`?**
> **Sí.** Todos los archivos dentro de `output\` son artefactos derivados del extracto PBF original. Puedes eliminarlos en cualquier momento para liberar espacio en disco. Sin embargo, conservarlos permite que el **Visor Web** (`serve`) y el comando **`publish`** los consulten y desplieguen inmediatamente sin tener que esperar a reprocesar el archivo `.pbf`.

---

### 1. Archivo GeoPackage: `<nombre>.gpkg`
Es una base de datos geoespacial estándar OGC (construida sobre SQLite con SRID 4326 WGS84) que contiene la red vial ruteable completa ya segmentada, topológicamente consistente y optimizada.

#### Tablas contenidas en el GeoPackage:
| Tabla | Tipo | Descripción | Columnas clave |
|---|---|---|---|
| **`road_segments`** | `LineString` | Micro-segmentos ruteables con impedancias de viaje y jerarquía vial | `fid`, `geom`, `segment_key`, `osm_id`, `name`, `highway`, `is_oneway` (1=único, 0=doble), `lanes`, `max_speed`, `length_m`, `travel_cost`, `from_node_key`, `to_node_key` |
| **`road_nodes`** | `Point` | Vértices e intersecciones de la red vial | `fid`, `geom`, `node_key` (llave de grilla espacial), `valence` (número de calles conectadas) |
| **`road_turn_restrictions`** | Tabla tabular | Restricciones de giro oficiales de OSM | `relation_id`, `restriction` (`no_left_turn`, `no_u_turn`, etc.), `from_osm_id`, `via_node_key`, `to_osm_id` |
| **`build_info`** | Metadatos | Registro de auditoría del proceso de construcción | `pbf_name`, `pbf_sha256`, parámetros de segmentación, BBox, fecha de compilación |

#### ¿Cómo utilizar el archivo `.gpkg`?
* **En QGIS o ArcGIS**:
  Arrastra y suelta el archivo `.gpkg` directamente en la ventana de QGIS. Podrás visualizar las capas vectoriales de segmentos y nodos, estilizarlas por jerarquía o sentido, y ejecutar análisis de grafos o rutas mínimas.
* **Publicar a Base de Datos (PostgreSQL/PostGIS, MySQL, SQL Server)**:
  Usa el comando `publish` de la CLI para cargar la red a tu base de datos:
  ```powershell
  # Crear tablas y cargar en base de datos nueva:
  .\dist\osmimport.exe publish --gpkg output/colombia.gpkg --mode create --connection "Host=localhost;Database=routing;Username=postgres;Password=secret"

  # Sincronizar base existente en producción respetando dependencias (modo sync):
  .\dist\osmimport.exe publish --gpkg output/colombia.gpkg --mode sync --profile sherpaslb
  ```
* **En el Visor Web Interactivo**:
  Inicia el visor con `.\dist\osmimport.exe serve` y selecciona el archivo en el desplegable superior para inspeccionarlo interactivamente en mapas vectoriales o satelitales.
* **En scripts de análisis (Python)**:
  ```python
  import geopandas as gpd

  # Cargar los segmentos a un GeoDataFrame
  gdf = gpd.read_file("output/colombia-261123.gpkg", layer="road_segments")
  print(f"Total tramos: {len(gdf)}")
  print(gdf[["name", "highway", "is_oneway", "length_m"]].head())
  ```
* **En .NET (C#)**:
  ```csharp
  using Microsoft.Data.Sqlite;
  using var conn = new SqliteConnection("Data Source=output/colombia.gpkg");
  conn.Open();
  // Puedes ejecutar cualquier consulta SQL estándar sobre las tablas del GeoPackage
  ```

---

### 2. Archivo de Auditoría: `<nombre>.report.json`
Archivo JSON generado automáticamente junto al GeoPackage que documenta las métricas de calidad y salud de la red extraída.

* **Contenido principal:**
  * **`parameters`**: Parámetros exactos con los que se compiló el grafo (`segmentizeMeters`, `bufferMeters`, `costFactors`, etc.).
  * **`stats`**: Conteo de vías procesadas, puntos densificados, segmentos descartados por longitud mínima, nodos creados y tiempos por etapa.
  * **`quality`**: Diagnóstico de conectividad de red, kilómetros y tramos agrupados por tipo de vía (`kmByHighway`, `segmentsByHighway`), porcentaje de vías de sentido único vs doble sentido, y componentes conexas.
* **¿Cómo utilizarlo?**:
  * **Visor Web**: Al seleccionar un GeoPackage, el visor lee este archivo para poblar las tarjetas de métricas, distribución kilométrica y tiempos de procesamiento.
  * **Validación en CI/CD**: Para verificar automáticamente mediante scripts que la red cubra el área esperada y no tenga anomalías antes de publicarla a producción.

---

### 3. Archivo de Comparación: `<nombre>.compare.json`
Se genera al ejecutar `osmimport compare --gpkg output/<nombre>.gpkg --profile <perfil>`.

* **Contenido:**
  * Diagnóstico de paridad entre la red del archivo `.gpkg` y la red que ya reside en la base de datos de producción (coincidentes, nuevos, retirados y modificados mediante llaves naturales estables).
  * Matriz de impacto sobre registros de tablas dependientes (rutas existentes, pedidos, órdenes históricas).
* **¿Cómo utilizarlo?**:
  * Auditoría previa obligatoria para entornos de producción antes de ejecutar un `publish --mode sync`.

---

## Particularidades por motor

| | PostgreSQL / PostGIS | MySQL 8 | SQL Server 2016+ |
|---|---|---|---|
| Atomicidad | Total (una transacción) | `create` no atómico (DDL con commit implícito; si falla, borra lo creado). `replace`/`sync` atómicos | Total (DDL transaccional) |
| Carga | `COPY` binario | `INSERT` por lotes | `SqlBulkCopy` |
| Geometría | `geometry(…, 4326)` | `… SRID 4326`; se escribe y lee en orden lon-lat (MySQL guarda lat-lon internamente, igual que Pomelo/EF) | `geography` (default) o `geometry` |
| `create` 1,9 M segmentos* | ~1 min | ~35 min (índices espaciales lentos) | ~2 min |
| `sync` típico* | ~4 min | ~5 min | ~1,5 min |

\* Medido en Docker sobre Windows con la red de Neuquén/Mendoza (ver [PLAN.md](PLAN.md)).

## Desarrollo

```powershell
dotnet build
dotnet test           # pruebas unitarias (geodesia, grafo, GeoPackage, diff, planificador de sync)
```

Estructura: `src/OsmImporter.Core` (lectura pbf, grafo, GeoPackage, descarga) · `src/OsmImporter.Publish`
(diff, comparador, un adaptador por motor) · `src/OsmImporter.Cli` · `tests/` · `profiles/`.

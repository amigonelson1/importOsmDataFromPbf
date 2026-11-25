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

- .NET 9 (runtime) para ejecutar; SDK 9+ para compilar. Memoria: ~4 GB para un país como Argentina.

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

**Funcionalidades del Visor Web:**
* 🗺️ **Mapa interactivo con aceleración GPU**: Visualización con MapLibre GL en tema oscuro, claro o **satelital de alta resolución** (Esri World Imagery y Google Hybrid).
* 🌍 **Búsqueda y descarga de Geofabrik**: Busca países y regiones por nombre y descárgalos directamente al proyecto.
* 📐 **Selector de área interactivo**: Botón **`✏️ Dibujar área`** para trazar un recuadro directamente con el ratón sobre el mapa y extraer únicamente la zona deseada.
* 🚀 **Construcción en vivo (Build)**: Genera los GeoPackages (`.gpkg`) con bloqueo seguro de la interfaz y consola de logs en vivo.
* 📊 **Inspección de carreteras**: Clic sobre cualquier calle para consultar su nombre, velocidad máxima, costo de viaje, sentido y nodo OSM.
* 📂 **Acceso rápido a carpetas**: Botones para abrir `output\` y `downloads\` directamente en el Explorador de Windows.

### 2. Ejecución por Consola (CLI / Automatización)
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
| `highways` | motorway … track (9 tipos) | Tipos de vía a incluir |
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

## Salida de `build`

| Tabla del `.gpkg` | Contenido |
|---|---|
| `road_segments` | Micro-segmentos: `segment_key`, `osm_id`, `name`, `highway`, `is_oneway`, `lanes`, `max_speed`, nodos origen/destino, `length_m`, `travel_cost` |
| `road_nodes` | Nodos: `node_key`, `valence` |
| `build_info` | Parámetros, nombre y sha256 del pbf, estadísticas y validaciones |

`segment_key` (`osm_id:nodo_origen:nodo_destino`) y `node_key` (coordenada en la grilla) son **llaves naturales
estables**: la misma vía con los mismos parámetros produce la misma llave en cualquier carga. Con ellas
`compare` y `sync` reconocen lo que ya existe en la base sin depender de sus Ids, y sin agregarle columnas.

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

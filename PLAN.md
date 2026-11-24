# OsmRoadImporter — Plan

Herramienta **genérica e independiente** (no se integra a ningún proyecto) para convertir un `.pbf` de
OpenStreetMap en una red vial ruteable (segmentos + nodos) y publicarla en PostgreSQL/PostGIS, MySQL o
SQL Server sin romper los datos que ya dependen de ella.

Origen: automatiza el proceso manual de SherpaSlb (`Resources/getDataOSM.txt` + `updateDataOSMreal.txt`:
Docker + osm2pgsql + SQL en PostGIS). SherpaSlb es solo un consumidor más: todo lo suyo vive en
`profiles/sherpaslb.json`, nada en el código.

## Arquitectura

```
pbf ──build──▶ graph.gpkg ──publish──▶ BD destino (postgres | mysql | sqlserver)
               (artefacto neutro,        └─ modos: create | replace | sync
                se abre en QGIS)
```

- **.NET 9** (compatible con SDK 9+ y runtime .NET 9/10), consola. Sin Docker ni osm2pgsql: el pbf se lee directo (OsmSharp) y el grafo se
  construye en memoria (NetTopologySuite + geodesia propia).
- **`build`** no conoce ninguna BD. Produce un GeoPackage versionado con `road_segments`,
  `road_nodes` y `build_info` (parámetros, origen, estadísticas).
- **`publish`** es lo único que conoce el destino, y solo vía **perfil JSON** (nombres de tablas,
  columnas, reglas de dependencias). Sin perfil: tablas `road_nodes` / `road_segments` en snake_case.

```
src/OsmImporter.Core     pipeline neutro: lectura pbf, área, grafo, GeoPackage (lectura/escritura)
src/OsmImporter.Publish  contratos, diff de sync, comparador de paridad, un adaptador por motor
src/OsmImporter.Cli      comandos, perfiles, prompts
tests/                   pruebas unitarias
profiles/                un JSON por proyecto consumidor (ej. sherpaslb.json)
```

## Identidad estable (clave del modo `sync`)

Los Ids autoincrementales no sirven para comparar cargas. Cada elemento tiene una llave natural:

- **Nodo**: coordenada snapeada a la grilla (`snap-deg`, default 1e-5) empaquetada en un `long`.
- **Segmento**: `OsmId:llaveNodoOrigen:llaveNodoDestino` (+ `#n` si se repite).

Ambas se recalculan sobre la data existente en el destino (desde `OsmId`, `Geometry`, `Location`),
así que **no hace falta agregar columnas** a las tablas del proyecto consumidor. El emparejamiento
tolera ±1 celda (≈1 m) en cada extremo: absorbe el redondeo de data que pasó por EPSG:3857.

## Modos de publicación

| Modo | Uso |
|---|---|
| `create` | Crea las tablas (esquema estándar de red vial, nombres según perfil) y carga |
| `replace` | Respalda, vacía y recarga. **Se niega** si alguna fila depende de la red actual |
| `sync` | Diff por llave natural: lo que no cambió conserva su Id; lo nuevo se inserta con Ids que nunca reutilizan uno anterior; las referencias a lo retirado se resuelven según el perfil y luego se borra |

Estrategias para filas que apuntan a elementos retirados (perfil, por dependencia):
`remapNearest` (con `maxDistanceMeters` y `fallback`) · `setNull` · `report` · `block`.
Con `report` sobre una FK real el elemento retirado se **conserva** (nunca queda una FK colgando).
Las FKs reales se detectan solas (`publish.foreignKeyDefaults`); las referencias lógicas sin FK se declaran
en `publish.dependencies`.

Seguridad de `sync`:
- Todo en **una transacción** con `LOCK ... IN EXCLUSIVE MODE` (lecturas permitidas, escrituras bloqueadas).
- `--dry-run` ejecuta **todo** y revierte: lo que muestra es exactamente lo que va a pasar.
  Sin `--yes`, `publish` hace primero esa simulación y pide confirmación.
- Respaldo `*_bak_<fecha>` (salvo `--no-backup`).
- Aborta si retiraría más de `maxRetiredPercent` (20 %) de los segmentos: protege contra un gpkg de
  otra zona o construido con otros parámetros.
- Reporte de cambios por columna antes de escribir.

Cadena de conexión: `--connection`, variable `OSMIMPORT_CONNECTION` o prompt oculto. Nunca en perfil ni logs.

## Algoritmo (equivalente a updateDataOSMreal.txt)

1. Vías con `highway` en la lista. Como osm2pgsql, las vías largas se parten en tramos cada 100 000
   unidades EPSG:3857 y **solo entran los tramos que tocan el área** (bbox + buffer).
   Se excluyen vías cerradas con `area=yes` (osm2pgsql las manda a `planet_osm_polygon`).
2. Densificación geodésica cada `segmentize-m` (50 m) por **bisección** — es lo que hace
   `ST_Segmentize(geography, 50)` en PostGIS 3.4 (verificado: 222 m → 8 partes, no 5).
3. Micro-segmentos entre puntos consecutivos; se descartan los de longitud ≤ `min-segment-m` (1 m).
4. Nodos = extremos snapeados a `snap-deg`. `FromNode`/`ToNode` = nodo del extremo snapeado.
5. Se descartan lazos (`From == To`); no quedan nodos huérfanos.
6. `Valence`, `LengthMeters` (elipsoide WGS84), `TravelCost` por factores de tipo de vía.

Diferencias deliberadas respecto al SQL original (todas configurables):

- **Intersecciones geométricas: no se calculan.** Todos los nodos son puntos de la grilla, así que el
  nodo más cercano a un extremo es siempre su propio punto snapeado; los nodos de intersección
  nunca se asignaban y terminaban borrados como huérfanos. Además generaban cruces falsos en puentes.
- **Buffer en metros reales** (el original lo aplicaba en EPSG:3857: 20 km ≈ 15,6 km reales a −38°).
  `--legacy-buffer` reproduce el comportamiento original.
- **`oneway=-1`**: se invierte la geometría y se marca sentido único (el original lo trataba como doble sentido).
- **`Lanes` y `MaxSpeed`** se llenan desde los tags (antes quedaban nulos). `maxspeed` en mph se convierte a km/h.
- **`CreatedAt`** en UTC.

## Fases

1. **build**: lectura pbf, área, cobertura de `highway`, grafo, salida `.gpkg` + reporte. ✅
2. **publish create|replace → Postgres** + `compare` (paridad). ✅
3. **publish sync → Postgres**: diff, dependencias, dry-run exacto, respaldo, transacción. ✅
4. **publish create|replace|sync → MySQL 8** + `compare`. ✅
5. **publish create|replace|sync → SQL Server** + `compare`. ✅
6. **`regions` / `download` / `build --download`** desde Geofabrik (nombre con fecha, MD5, caché) + README final. ✅

## Resultados de la fase 2 (2026-11-13, contra titan_postgis / routing)

Paridad con la data generada por el proceso manual (build con `--legacy-buffer --no-reverse-oneway`):

| | destino (SQL manual) | osmimport | coinciden |
|---|---|---|---|
| Segmentos | 1 864 985 | 1 864 983 | 1 864 983 (99,9999 %) |
| Vías OSM | 76 516 | 76 516 | 76 516 |
| Km | 59 133,3 | 59 133,3 | — |

- 8 631 segmentos coinciden con ±1 celda de grilla: redondeo por el paso de osm2pgsql a EPSG:3857.
- Round-trip `publish create` → `compare`: 100 % exacto. Validaciones del `.txt`: todas en 0.
- `replace` sobre `routing` queda **bloqueado**: `OperationalPoints.ReferenceRoadNodeId` tiene 144 342 filas
  apuntando a nodos. Es justamente el caso que resuelve `sync`.

## Resultados de la fase 3 (copia de routing en `osmimport_test`)

`sync` con el perfil sherpaslb (buffer real, `oneway=-1` invertido) sobre la red generada a mano:

| | existentes | conservados (±1 celda) | nuevos | retirados |
|---|---|---|---|---|
| Segmentos | 1 864 985 | 1 864 971 (8 631) | 106 535 | 14 |
| Nodos | 1 811 106 | 1 811 103 (3 962) | 101 279 | 3 |

- Un bloqueo de prueba sobre un segmento retirado (vía `oneway=-1` que se invierte) se reasignó solo.
- Ningún `OperationalPoint` afectado.
- Rendimiento: ~4 min con 1,8 M filas actualizadas (índices normales fuera durante las escrituras;
  con los índices vivos el mismo UPDATE no terminaba en 30 min).

**Hallazgo — `TravelCost`:** en `routing` todos los segmentos tienen `TravelCost = LengthMeters`
(factor 1,0): el paso "ajuste de costo de recorrido" del `.txt` nunca quedó aplicado. El perfil sí aplica
los factores (0,7 / 1,0 / 1,4 / 2,0), así que un sync cambiaría el costo de ~1,76 M segmentos y con
ello el ruteo. **Decisión (2026-11-13): se usan los factores del `.txt`** (perfil sherpaslb, `costFactors`).
El primer sync sobre `routing` actualizará `TravelCost` en casi todos los segmentos; es esperado.

Base de pruebas: `osmimport_test` en `titan_postgis` (se puede borrar con `DROP DATABASE osmimport_test`).

## Resultados de la fase 4 — MySQL 8 (contenedor de prueba, perfil `sherpaslb-mysql`)

Diseño propio de MySQL (ver comentario de `MySqlPublisher`):
- **Orden de ejes**: SRID 4326 en MySQL es lat-long. Se escribe con `ST_GeomFromWKB(wkb, 4326, 'axis-order=long-lat')`
  y se lee con `ST_Longitude/ST_Latitude`: el dato queda igual a lo que guarda Pomelo/EF en SherpaSlb.
- **El DDL hace commit implícito**: `create` no es atómico (si falla, borra las tablas creadas); `replace` y `sync`
  no usan DDL dentro de la transacción. InnoDB no toca índices secundarios al cambiar columnas no indexadas, y la
  geometría solo se reescribe si realmente cambió (si no, recalcularía columnas generadas + 3 índices espaciales).
- **Sin KNN en SQL**: el diff de atributos y la reasignación de dependencias se calculan en memoria
  (`SyncPlanner` / `DependencyPlanner`, reutilizables para SQL Server); el motor solo ejecuta los cambios.
- Columnas generadas `StartPoint`/`EndPoint` (SRID después de `STORED`, como en las migraciones de SherpaSlb).

| Prueba | Resultado |
|---|---|
| `create` (1,86 M segmentos) | 35 min: carga 3,5 min + índices espaciales ~32 min (lento propio de MySQL en Docker/Windows) |
| `compare` ida y vuelta | 100 % exacto (orden de ejes correcto en escritura y lectura) |
| `replace` con dependencias | bloqueado (5 000 `OperationalPoints`) |
| `sync` (red legacy → perfil actual) | 3:45 simulación / 5:02 real: +106 535, −12, bloqueo de la vía invertida reasignado |
| `sync` repetido | 0 cambios (idempotente) |
| Validaciones | sin nodos 0, lazos 0, huérfanos 0, `StartPoint` generado consistente |

`--dry-run` en MySQL restaura `AUTO_INCREMENT` al terminar (InnoDB no lo revierte con el rollback).

## Resultados de la fase 5 — SQL Server 2022 (contenedor de prueba, perfil genérico sin Sherpa)

Diseño propio de SQL Server (ver comentario de `SqlServerPublisher`):
- **El DDL es transaccional**: create, replace y sync son atómicos de punta a punta.
- Carga con `SqlBulkCopy` a tablas `#temporales` (geometría en WKB) y `INSERT ... SELECT geography::STGeomFromWKB(wkb, 4326)`;
  Ids explícitos con `IDENTITY_INSERT`. Tipo espacial `geography` por defecto (como EF Core + NetTopologySuite) o `geometry`.
- Escrituras masivas (> 5 % de la tabla): índices no únicos deshabilitados y reconstruidos dentro de la transacción.
- Diff de atributos y dependencias con el mismo `SyncPlanner` / `DependencyPlanner` que MySQL.
- `--dry-run` restaura `IDENTITY` con `DBCC CHECKIDENT` (no es transaccional).

| Prueba | Resultado |
|---|---|
| `create` (1,86 M segmentos, tablas `road_*` genéricas) | 1:48 en total (incluye índices espaciales) |
| `compare` ida y vuelta | 100 % exacto |
| `replace` con dependencias | bloqueado (FK `op_points` + dependencia declarada `route_alerts`) |
| `sync` (red legacy → perfil actual) | 1:46 simulación / 1:32 real: +106 535, −12, alerta de la vía invertida reasignada |
| `sync` repetido | 0 cambios (idempotente) |
| Validaciones | sin nodos 0, lazos 0, huérfanos 0, índices deshabilitados 0, Ids nuevos sin hueco tras el dry-run |

## Fase 6: Infraestructura Geoespacial Robusta y Escalable (2026-11)

### 1. Seguridad, Resiliencia y Ciclo de Vida
- **Migración a .NET 9 y Actualización de Dependencias:** Actualización del TargetFramework a `net9.0` en todos los proyectos de la solución (aprovechando el SDK 9.0.315 instalado y runtime de última generación). Actualización de dependencias a sus versiones estables net9 (`Microsoft.Data.Sqlite 9.0.2`, `Npgsql 9.0.2`, `xUnit 2.9.3`, `Microsoft.NET.Test.Sdk 17.12.0`, `coverlet.collector 6.0.4`) manteniendo 0 vulnerabilidades y total retrocompatibilidad.
- **Mitigación de Vulnerabilidades:** Actualización de `MySqlConnector` de 2.3.7 a 2.6.2 (mitigando advisory `GHSA-473q-m89c-ghf8` / `NU1902`). 0 vulnerabilidades en toda la solución (`dotnet list package --vulnerable`).
- **Control de Rangos Numéricos:** Validación obligatoria en `BuildOptions`: `snapDegrees >= 1e-6` y `<= 0.01`, previniendo desbordamiento aritmético de 31 bits en `GridKey` (coordenadas fuera de rango con snaps extremos).
- **Descargas HTTP Resumables:** Soporte de cabeceras HTTP `Range: bytes=X-` con manejo de respuestas `206 PartialContent` y `416 Range Not Satisfiable`. Descarga a archivos `.part` con verificación de integridad MD5 antes del renombramiento final atómico.
- **Cancelación Cooperativa Limpia:** Propagación exhaustiva de `CancellationToken` a lo largo de `BuildPipeline`, `PbfRoadReader`, `GeoPackageWriter`, `PublishCommands` y `DownloadCommands`. En caso de interrupción (Ctrl+C o cancelación), los archivos temporales (`.gpkg.tmp`, `.part`) son eliminados inmediatamente y el proceso sale con código 130.

### 2. Rendimiento y Paralelización Determinista
- **`WaySelector` Concurrente y Determinista:** Paralelización del filtrado y recorte geodésico de vías mediante `Partitioner.Create` + `Parallel.ForEach`. Para garantizar determinismo estricto e independiente del recuento de núcleos, cada tramo particionado se etiqueta con un `PieceOrdinal` y el resultado se ordena establemente por `(Way.Id, PieceOrdinal)`.
- **GeoPackage SQLite de Alto Rendimiento:** Configuración de PRAGMAs optimizados (`cache_size = -262144` [256MB], `temp_store = MEMORY`, `locking_mode = EXCLUSIVE`). Se desacopló la inserción secuencial de filas de `road_segments`/`road_nodes` respecto a las tablas virtuales `rtree_*_geom`, eliminando la evicción y contención de páginas de SQLite.
- **Carga Masiva en MySQL:** Desactivación temporal de `foreign_key_checks` y `unique_checks` durante inserciones por lotes en `CreateAsync`, y configuración dinámica de `innodb_ddl_buffer_size = 256MB` e `innodb_sort_buffer_size = 64MB` para acelerar la construcción de índices espaciales.

### 3. Atributos Enriquecidos y Restricciones de Giro OSM
- **Nuevos Atributos Viales:** Lectura y parsing normalizado de tags OSM para:
  - `surface`, `smoothness`, `tracktype`
  - `bridge`, `tunnel`, `layer` (entero con signo)
  - `maxweight` (toneladas métricas, convirtiendo kg y lbs)
  - `maxheight`, `maxwidth` (metros, convirtiendo pies y pulgadas)
- **Restricciones de Giro (Turn Restrictions):** Detección y extracción de relaciones OSM de tipo `restriction` (`no_left_turn`, `no_right_turn`, `only_straight_on`, etc.), vinculando vía-nodo (`via_node_key`) y vías de entrada/salida (`from_osm_id`, `to_osm_id`).
- **Persistencia en GeoPackage:** Almacenamiento en tabla `road_turn_restrictions` indexada y columnas extendidas en `road_segments`. Compatibilidad retrospectiva completa: si se abre un `.gpkg` anterior, el lector devuelve valores por defecto de forma transparente.
- **Compatibilidad Multimotor:** Soporte opcional en esquemas de publicación (PostgreSQL, MySQL, SQL Server). Si el perfil del proyecto no declara estos campos, no se alteran las consultas ni se requieren en las bases destino.

### 4. Observabilidad y UX en Línea de Comandos
- **Integración con Spectre.Console:** Visualización rica con bordes redondeados, colores temáticos e indicadores de estado para reportes de paridad (`compare`), planes de sincronización (`publish --dry-run`) y análisis de dependencias.
- **Degradación Elegante:** En entornos CI/CD, pipelines automatizados o cuando `Console.IsOutputRedirected` es verdadero, la herramienta conmuta automáticamente al formato de texto plano estándar sin romper scripts existentes.

### 5. Perfiles JSON y Tolerancia a Esquemas en Destino
- **Perfiles Actualizados:**
  - `profiles/sherpaslb.json`: Mapeo completo en convención PascalCase (`Surface`, `Smoothness`, `TrackType`, `Bridge`, `Tunnel`, `Layer`, `MaxWeight`, `MaxHeight`, `MaxWidth`).
  - `profiles/sherpaslb-mysql.json`: Mapeo completo en PascalCase para MySQL (`Surface`, etc.).
  - `profiles/generic.json`: Nuevo perfil de referencia estándar con nomenclatura `snake_case` y configuración completa de turn restrictions.
- **Resiliencia ante Esquemas Preexistentes (Cero Fricción en Migración):**
  - Los publicadores (`PostgresPublisher`, `MySqlPublisher`, `SqlServerPublisher`) diferencian columnas **obligatorias** (id, geometría, osm_id, from_id, to_id, etc.) de columnas **opcionales** (surface, bridge, maxweight, etc.).
  - Si una columna opcional está declarada en el perfil pero aún no existe en la base de datos destino (e.g. antes de ejecutar la migración DDL), el publicador emite una advertencia informativa (`Log.Warn`) y omite esa columna de los `INSERT`, `UPDATE` y `COPY`, permitiendo sincronizar sin interrupciones ni excepciones.
  - Al ejecutar `--mode create`, se generan automáticamente todas las columnas declaradas con sus restricciones y valores por defecto.



# Divisiones y ciudades por país — diseño

Fecha: 2026-11-20. Rama: `feature/geografia-por-pais`.

## Intención

Cuando el usuario elige un país en un formulario, el frontend tiene que ofrecer la lista de la
división de primer nivel que corresponda a ese país (departamento en Colombia, provincia en
España), y al elegirla, la lista de sus ciudades. **La división sólo sirve para filtrar ciudades:
no se guarda en ningún lado.**

Hoy sólo Colombia tiene catálogo (`GET /api/v1/departments`, `GET /api/v1/cities?departmentId=`,
DIVIPOLA). Fuera de Colombia la ciudad se escribe a mano en `CityName`. El primer país nuevo es
España; después vendrán otros, así que agregar un país tiene que ser sembrar datos, no rediseñar.

## Metas

- Un flujo único para el frontend, igual en todos los países: país → regiones → ciudades.
- Colombia y España con catálogo completo.
- Agregar un país = un archivo de datos embebido más su entrada en la lista de países.

## No metas

- Guardar la región elegida en cliente, empresa, distribuidor o dirección.
- Cambiar cómo se guarda la ciudad: Colombia sigue en `CityId`, el resto en `CityName` (texto).
- Validar en el backend que el `CityName` de un cliente español exista en el catálogo.
- El frontend (va en un spec propio en `qep-frontend`) y la tienda pública.

## Anti-metas

- Que una ciudad española aparezca donde se espera una ciudad DIVIPOLA: `CityId` de cliente o de
  libreta, Coordinadora, Siigo, ReteICA municipal o sinónimos de Shopify.
- Romper `/api/v1/departments` o `/api/v1/cities`, que el frontend usa hoy.

## Enfoques

| Enfoque | Qué cambia | Por qué no / por qué sí |
| --- | --- | --- |
| **A. Catálogo aparte por país (elegido)** | Tablas nuevas para países fuera de Colombia; endpoints por país que leen DIVIPOLA para `CO` y las tablas nuevas para el resto. La ciudad de afuera se sigue guardando como texto. | No toca ningún consumidor de `CityId`; un país nuevo es sembrar datos. Costo: fuera de Colombia la ciudad no tiene FK. |
| B. Generalizar `departments`/`cities` con país | Las ciudades españolas tienen `CityId` y el cliente español la guarda. | Obliga a revisar todo lo que asume DIVIPOLA (validaciones de 2/5 dígitos, Coordinadora, Siigo, ReteICA, sinónimos, libreta, discriminante `CO ⇒ CityId` de `Customer`). Mucho más riesgo. |
| C. Sólo provincias, ciudad libre | Lista de provincias de España, sin ciudades. | No da la lista de ciudades, que es lo que se pidió. |

El owner eligió A el 2026-11-20.

## Diseño

### Datos (módulo Geography)

Dos tablas nuevas en el esquema `geography`, sin tenant, como las DIVIPOLA:

- `regions`: `id`, `country_code` (ISO 3166-1 alfa-2), `code`, `name`. Único `(country_code, code)`.
- `region_cities`: `id`, `region_id` (FK a `regions`), `code`, `name`. Único `(region_id, code)`,
  índice por `region_id`.

`departments` y `cities` no cambian. Colombia **no** se copia a las tablas nuevas: los endpoints
por país la leen de las tablas DIVIPOLA, así sus ids son los mismos `CityId` que ya se guardan.

### Siembra

Mismo patrón que `GeographySeeder`: JSON embebido por país, importador idempotente en cada
arranque, upsert por código y renombre si el nombre cambió.

- España: las **52 provincias** (incluye Ceuta y Melilla) con su código INE de 2 dígitos, y los
  **municipios** del INE (~8.100) con código de 5 dígitos (provincia + municipio).
  _Decisión del asistente:_ provincia y no comunidad autónoma, porque es el nivel que se usa en
  una dirección postal y el que agrupa municipios en el archivo del INE.
- Fuente: "Relación de municipios y sus códigos por provincias" del INE, convertida a JSON una vez
  y embebida. Se regenera a mano cuando cambie, igual que DIVIPOLA.

### Lista de países con catálogo

Constante de dominio (como `DocumentTypesByCountry`): `CO` → `Department`, `ES` → `Province`.
El tipo de región viaja con su nombre de enum; el frontend tiene el diccionario para pintar
"Departamento" o "Provincia".

### Endpoints

Todos con `RequireAuthorization()` a secas, como los de hoy (datos globales, sin permiso propio).

| Ruta | Respuesta |
| --- | --- |
| `GET /api/v1/countries` | `[{ code, regionKind }]` — los países con catálogo. |
| `GET /api/v1/countries/{country}/regions` | `[{ id, code, name }]` ordenado por nombre. |
| `GET /api/v1/countries/{country}/regions/{regionId}/cities` | `[{ id, code, name }]` ordenado por nombre. |

- Para `CO`, `id`/`code` son los de `departments` y `cities` (`DivipolaCode`); el `name` de la
  ciudad es `City.Name` (el nombre común, como el selector actual).
- **País sin catálogo → `200 []`** (decisión del asistente). El frontend cae al texto libre con la
  misma regla para cualquier país, sin manejar un error. El motivo va escrito en el DTO.
- **Región que no es de ese país → `404`.** Son datos globales: no hay id de otro tenant que
  proteger. Se lanza `ResourceNotFoundException` (que `ApiExceptionHandler` ya mapea a 404) con
  el código `geography.region.not_found`.
- El país se recorta y se pasa a mayúsculas antes de comparar.

### Qué guarda el frontend

- `CO`: el `id` de la ciudad va a `CityId`, como hoy.
- Cualquier otro país: el `name` de la ciudad va a `CityName`. El catálogo sólo evita que se
  escriba mal; el texto libre sigue siendo válido (importaciones, datos viejos, países sin catálogo).

## Pruebas

- Unitarias: parser del JSON del INE; resolución de `CO`/`ES`/país desconocido.
- Integración (Geography): las tres rutas con `CO` y `ES`, país sin catálogo → `[]`, región de
  otro país → 404, idempotencia del seeder (dos corridas, mismos conteos).
- `GeographyLayerTests` sigue pasando sin cambios.

## Pendiente para el builder

- Nombre exacto de las clases y del repositorio nuevo.
- Formato del JSON embebido de España.
- Si `/departments` y `/cities` se marcan como obsoletos en el código (no se eliminan).

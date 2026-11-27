# Facturar en Siigo a clientes de otros países — diseño

Fecha: 2026-11-26. Rama: `feature/siigo-foreign-cities`. Estado: **aprobado por el owner** (2026-11-26).

## Intención

QEP tiene que facturar en Siigo a un cliente de otro país con su país, departamento y ciudad reales.

Hoy no puede:

- `SiigoCustomerMapper` manda `country_code = "Co"` fijo.
- `SiigoOrderSource` sólo resuelve códigos DANE; con cualquier otro país la ciudad queda sin código y el armador
  responde `customer_city_missing`.
- `ExternalInvoicingParty` no lleva la ciudad escrita (`CityName`), que es lo único que guarda una parte extranjera.
- `ResolvePartyAsync` sólo toma la ciudad de la parte de facturación si tiene `CityId`: con una parte de facturación
  extranjera mezcla su país con la ciudad de la ficha.

Siigo exige `country_code`, `state_code` y `city_code` y publica su lista:
[Lista-de-ciudades.xlsx](https://saprodcentralassets.blob.core.windows.net/siigoapi/documentation/Lista-de-ciudades.xlsx),
enlazada desde [Crear cliente](https://developers.siigo.com/docs/siigoapi/customer/1-create-customer/). Al
2026-11-26 trae 4.605 ciudades de 220 países; 3.482 no son colombianas.

## Metas

- Una ciudad extranjera que coincide con la lista de Siigo se factura sin que nadie haga nada.
- Una que no coincide queda en una bandeja de la consola del operador (`/operator/cities`), que la vincula una vez a
  una ciudad de la lista. Desde ahí vale para todos los tenants.
- El flujo de operador se parece al de los sinónimos colombianos que ya existe: bandeja, confirmar, descartar,
  restaurar, corregir y borrar.

## No metas

- Monedas distintas de COP.
- El frontend: va con un prompt para la sesión de `qep-frontend`.
- Cambiar cómo se guarda la ciudad de un cliente o de un pedido: fuera de Colombia sigue siendo texto.
- Corregir clientes que ya existen en Siigo: QEP los busca por documento y no los actualiza.
- Crear un sinónimo extranjero a mano, sin un texto en la bandeja (hoy el colombiano tampoco se puede).

## Anti-metas

- Que algo de Colombia cambie: DANE, sinónimos, bandeja, trabajos de corrección de Shopify y sus eventos.
- Que una factura salga con una ciudad que nadie eligió: sin coincidencia ni sinónimo, no se factura.
- Que un fallo al anotar el texto en la bandeja tumbe una facturación o su vista previa.
- Que la bandeja le muestre al operador direcciones, nombres o documentos de clientes de otro tenant.

## Enfoques

| Enfoque | Qué cambia | Costo |
| --- | --- | --- |
| **A. Tablas aparte para el exterior, misma consola (elegido)** | Catálogo de Siigo, sinónimos y bandeja propios en `geography`, con rutas bajo `/operator/cities/foreign` | Dos modelos parecidos |
| B. Generalizar `city_synonyms` con país | `department_id` pasa a opcional | Toca bandeja, trabajos de corrección y eventos, que asumen DIVIPOLA |
| C. El tenant elige la ciudad de Siigo en la ficha del cliente | Selector en los formularios | Cambia cómo se guarda la ciudad y cada tenant repite el trabajo |

El owner eligió A el 2026-11-26.

## Diseño

### Catálogo de ciudades de Siigo (`geography.billing_cities`)

Dato global, sin tenant. Columnas: `id`, `country_code` (en mayúscula, para cruzar con el país de QEP),
`siigo_country_code`, `country_name`, `state_code`, `state_name`, `city_code`, `city_name`, `normalized_name`
(`CityText.Normalize`). Único `(country_code, state_code, city_code)`; índice `(country_code, normalized_name)`.

- La lista se convierte una vez a un JSON embebido y la importa un seeder idempotente, con huella en
  `geography.seed_state` como `RegionSeeder`. Inserta y renombra; **nunca borra** (un sinónimo puede apuntar ahí).
- **Colombia no se importa.** Sigue por DANE.
- Los códigos se guardan y se envían **tal cual vienen**, incluida la mayúscula (`Co`, `Us`, pero `AG` y `KY`). Los
  nombres se recortan, también de espacios de no separación.
- La lista tiene errores y **no se corrigen** (decisión del asistente): es la única combinación de códigos que Siigo
  publica. Ejemplos: Miami trae `city_code = "1"`; en Ecuador, `Quito` y `Ambato` están en `Pastaza` e `Ibarra` en
  `Guayas`; en Estados Unidos hay «ciudades» llamadas como un estado. Una coincidencia por nombre factura con el
  departamento que diga la lista.
- No se usa `geography.regions` (España, INE): sus códigos no son los de Siigo.

### Sinónimos y bandeja del exterior

- `geography.foreign_city_synonyms`: `id`, `country_code`, `normalized_text`, `display_text`, `billing_city_id`,
  autoría, borrado lógico y `version`. Un solo sinónimo vivo por `(country_code, normalized_text)`.
- `geography.unresolved_foreign_cities`: `id`, `country_code`, `normalized_text`, `city_text`, `status`
  (`Pending`, `Resolved`, `Discarded`), `synonym_id`, `first_seen_at`, `last_seen_at`, `updated_at`, `version`. Una
  sola fila no resuelta por `(country_code, normalized_text)`.
- `geography.unresolved_foreign_city_occurrences`: `id`, `unresolved_city_id`, `tenant_id`, `order_id`, `seen_at`.
  Única por `(unresolved_city_id, order_id)`.

Normalización y topes de texto: los de `CityText`. La llave no lleva departamento o provincia: la parte extranjera
no siempre lo tiene.

### Cómo se resuelve una ciudad

Sólo para una parte facturada que no es consumidor final, con país distinto de `CO` y con ciudad escrita:

1. Si el país no está en el catálogo, no hay códigos y no se anota nada en la bandeja.
2. Si el texto normalizado coincide con **una sola** ciudad del país, son sus códigos.
3. Si no, el sinónimo vivo del `(país, texto)`.
4. Si no, se anota el texto en la bandeja (con el tenant y el pedido) y no hay códigos.

Un nombre repetido dentro de un país (35 casos hoy, por ejemplo `Berlin` en `De`) no coincide solo: pasa por el paso 3.
Reportar y confirmar toman el mismo candado por `(país, texto)`, como en Colombia.

La anotación la disparan sólo la vista previa y la emisión (tanda). La consulta de disponibilidad de la pantalla del
pedido arma el mismo pedido y promete no escribir (`SiigoInvoiceAvailabilityHandler`): **no anota**. Es idempotente por
pedido. Si falla, queda un warning con el tipo de la excepción y la facturación sigue su curso.

### Facturación

- `ExternalInvoicingParty` gana `CityName`. `CityId`, `DepartmentId`, `Country` y `CityName` salen **siempre de una
  sola fuente**: de la parte de facturación si declara alguno de `CityId`, `CityName` o `Country`; si no, los cuatro
  de la ficha. Una parte con país extranjero y sin ciudad queda en `customer_city_missing`: no toma la ciudad de la
  ficha.
- `SiigoBilledPartyInput` gana el código de país de Siigo. `SiigoCustomerMapper` manda ése, y `Co` cuando no viene
  (Colombia y consumidor final).
- Faltantes: sin ninguna ciudad sigue `customer_city_missing`. **Nuevo `customer_city_unlinked`** cuando la parte es
  extranjera, tiene ciudad escrita y no se pudo resolver (incluye un país que no está en el catálogo). Sin
  `reference` ni `label`: `SiigoMissing` no lleva datos del cliente.
- No hay reintento automático: cuando el operador vincula la ciudad, el usuario vuelve a generar la factura.
- El teléfono ya usa el país de la parte; no cambia.

### Tipos de documento

Hoy `SiigoIdentificationTypes` no tiene código para NIF, NIE ni Tax ID, y fuera de Colombia QEP sólo permite Tax ID y
pasaporte (España: NIF, NIE y pasaporte; `DocumentTypesByCountry`): casi ningún extranjero se podría facturar. Se
agregan, decisión del owner del 2026-11-26:

| QEP | `id_type` de Siigo |
| --- | --- |
| Tax ID | `50`, NIT de otro país |
| NIF, NIE | `42`, documento de identificación extranjero |

- El mapeo sale de los nombres de la tabla de Siigo. Lo confirmó contabilidad (respuesta del owner, 2026-11-27).
- El documento viaja como el pasaporte hoy: sólo letras y dígitos ASCII, sin quitar dígito de verificación. El largo
  no se valida en QEP (Siigo admite de 1 a 20).
- `customer_identification_type_unsupported` sigue existiendo para un documento sin tipo.
- Reemplaza lo que dice sobre NIF, NIE y Tax ID el spec 2026-11-18 de la integración.

### Consola del operador

Rutas bajo `/api/v1/tenants/{tenantId}/operator/cities/foreign`. Permisos existentes, con la doble capa de hoy
(política y `OperatorAuthorization`): `operator.cities.read` para leer y `operator.cities.manage` para escribir.
Ningún permiso nuevo.

| Ruta | Qué hace | Respuesta |
| --- | --- | --- |
| `GET /countries` | Países del catálogo, para el selector | `[{ code, name, pendingCount }]`, los que tienen pendientes primero y después por nombre |
| `GET /catalog?country=&search=` | Ciudades de Siigo del país, para elegir | `[{ id, name, stateName, cityCode }]`, hasta 50 |
| `GET /unresolved?country=&status=&search=&page=&pageSize=` | La bandeja; `country` es opcional | `{ items, total, page, pageSize }` |
| `GET /unresolved/{id}` | Detalle | la fila, `tenants [{ tenantId, name, orderCount }]` y `suggestions` (hasta 3, con `CitySuggestions.Rank`) |
| `POST /unresolved/{id}/resolve` | `{ billingCityId }`: crea el sinónimo | el sinónimo |
| `POST /unresolved/{id}/discard` y `/restore` | Igual que hoy | la fila |
| `GET /synonyms?country=&search=&page=&pageSize=` | Lista | `{ items, total, page, pageSize }` |
| `PUT /synonyms/{id}` | `{ billingCityId, expectedVersion }`: cambia la ciudad; versión vieja es `412` | el sinónimo |
| `DELETE /synonyms/{id}` | Borrado lógico | `204` |

- Fila de la bandeja: `id`, `text`, `normalizedText`, `country { code, name }`, `status`, `orderCount`,
  `tenantCount`, `firstSeenAt`, `lastSeenAt`, `bestSuggestion`, `version`.
- Sinónimo: `id`, `text`, `normalizedText`, `country`, `city { id, name, stateName, cityCode }`, `createdBy`,
  `createdAt`, `updatedAt`, `version`.
- Sugerencia (`bestSuggestion` y `suggestions`): `{ id, name, stateName, cityCode, similarity }`. El código viaja
  porque la lista repite pares nombre y departamento (`Boston`, `Massachusetts`: `2101` y `02119`).
- Concurrencia, paginación, validación (`422 validation.failed`) y estados se comportan como en las rutas
  colombianas.
- Códigos de error: los de hoy donde aplican (`geography.synonym.not_found`, `.already_exists`, `.matches_city`,
  `.same_city`, `.deleted`, `geography.unresolved.not_pending`, `.not_discarded`). Nuevo:
  `geography.billing_city.not_in_country` (422).
- Sin vista de impacto, reintento ni recorrido: un sinónimo extranjero no reescribe nada, sólo se lee al facturar.
  Corregirlo o borrarlo no toca facturas ya creadas.
- Crear, corregir, borrar, descartar y restaurar quedan auditados, con acciones propias.

## Restricciones

- Capas y límites de hoy: ni Siigo ni Quotations referencian a Geography; el cruce va en `Bootstrapper`.
- Migraciones sólo de expansión en `geography`.
- TDD.

## Cambios de contrato que aprueba este spec

Rutas nuevas bajo `/operator/cities/foreign`, el faltante `customer_city_unlinked` y el error
`geography.billing_city.not_in_country`. Nada de lo existente cambia de forma.

## Sin probar

Nada de esto está probado contra Siigo real, que no tiene empresa de pruebas. Antes de confiar: facturar un cliente
extranjero con `send_to_dian` apagado y revisar en Siigo país, departamento, ciudad y teléfono.

## Queda a quien construye

Nombres de clases, índices, acciones de auditoría y mensajes; el formato del JSON; cómo se reparte entre servicio de
aplicación y adaptador, siempre que resolver (lectura) y anotar (escritura que puede fallar) queden separados.

# Homologación de columnas del Excel de pedidos

**Fecha:** 2026-09-24
**Módulos:** Quotations (backend) — tenant-settings (frontend)
**Estado:** aprobado en brainstorming, pendiente de revisión del spec escrito
**Origen:** D10 del spec `2026-09-24-codigo-de-asesor-design.md`
**Mock del editor:** https://claude.ai/artifact/BukNfq45jWTv6HrbXTtu5N

## Problema

El Excel de pedidos (`OrdersExportProcessor`) es lo que consume el ERP contable del tenant, y
cada ERP importa por encabezado con su propia plantilla. Hoy los encabezados, el orden y el
conjunto de columnas son fijos en el código, así que el tenant retoca el archivo a mano en cada
exportación: renombra columnas, borra las que su ERP rechaza, agrega las que su ERP exige con un
valor constante (tipo de documento, bodega, centro de costo).

## Decisiones

Todas tomadas con el developer durante el brainstorming del 2026-09-24.

### D1 — Sólo el Excel de pedidos

El de cotizaciones lo lee una persona, no un sistema. El modelo queda por `ExportJobKind`, así
que sumar cotizaciones después es una fila más, no otro diseño.

### D2 — Renombrar, reordenar y ocultar

Una columna que el ERP no importa no viaja. Sin ocultar, la homologación no evita el retoque a
mano que quiere evitar.

### D3 — Cada columna repetida se homologa por separado

`Fecha Pago 1..5`, `V. Comprobante 1..5` y `URL Comprobante 1..5` son 15 entradas
independientes, no tres grupos con patrón. Un tenant puede nombrar `Fecha Pago 4` distinto u
ocultar sólo esa. El costo es una pantalla más larga (33 filas de catálogo).

### D4 — Columnas fijas del tenant, sólo texto

El tenant puede agregar columnas cuyo valor es el mismo en todas las filas (`Tipo Doc` = `FV`,
`Bodega` = `01`). El valor es **texto**: `01` conserva el cero, y un valor fijo es un código, no un
importe que alguien sume. Si un ERP concreto rechazara texto donde espera número, agregar un tipo
por columna es un enum más en la entrada, sin romper lo guardado.

### D5 — Vive en Configuración, permisos de settings

Es configuración del tenant: la cambia el admin una vez y la usa todo el mundo. Permisos
`TenancyPermissions.SettingsRead` / `SettingsUpdate`, sin permiso nuevo (que serían dos mitades y
una razón para que el vendedor le mueva las columnas al contador). Página propia dentro de
Configuración, no una sección del formulario: 33+ filas no caben como sección, y el guardado no
comparte estado ni versión con `PUT /settings`.

### D6 — Agregado propio en Quotations, no columna en `Tenant`

`OrdersExportLayout`, uno por tenant, en `quotations.orders_export_layouts`. El processor lo lee de
su propio repositorio sin cruzar módulos, y tiene `Version` propia: guardar columnas con la pantalla
del logo abierta no da 412 cruzado. Una `jsonb` en `tenancy.tenants` obligaría a un puerto nuevo en
Bootstrapper y a compartir `Version` con nombre, zona horaria y logo. Una tabla normalizada sobra:
nunca se consulta una columna sola, siempre la lista entera en orden.

### D7 — La lista viaja y se guarda entera

`columns jsonb`: el formulario repinta la lista completa, el processor la lee completa, el PUT la
reemplaza completa. Es la regla del repo para colecciones editables.

### D8 — Layout efectivo: lo guardado más lo que falte del catálogo

Lo que ven `GET` y el processor es: las entradas guardadas en su orden, más toda llave del catálogo
que no esté guardada, **al final, visible, con su nombre por defecto**. Una llave guardada que ya no
existe en el catálogo se descarta en silencio. Así una columna nueva del backend aparece sola sin
obligar al tenant a re-guardar, un PUT no exige las 33, y sin fila guardada el efectivo es el
catálogo tal cual = el Excel de hoy.

### D9 — Versión implícita 1 para el layout no guardado

El layout por defecto no guardado responde `version: 1` y ETag `"1"`. El primer PUT viaja con
`If-Match: "1"` y crea la fila en versión 2. Dos primeros PUT simultáneos chocan en la PK de la
tabla, e Infrastructure traduce esa violación a 412. `RequireIfMatch` (exige `> 0`) sirve sin
cambios, y no hay side effect en el GET ni fila creada al aprovisionar.

### D10 — Sin DELETE: restaurar es un PUT

"Restaurar todo por defecto" manda las columnas del catálogo en su orden y nombres por defecto,
**sin fijas**, que el mismo GET ya trae (`defaultHeader`, `defaultPosition`). La fila queda; no hace
falta un endpoint para borrarla.

## Backend

### Catálogo — `OrdersExportColumnCatalog` (Domain, estático)

Las 33 columnas de `OrdersExportProcessor.Columns` a la fecha (incluida "Valor Unit sin IVA",
`f479656`, 2026-09-24), cada una con `Key` estable, `DefaultHeader` (el encabezado actual, sin
cambios) y `Width`. Vive en Domain porque `Effective` (D8) lo necesita y Domain no referencia
Application. El tenant no puede agregar ni quitar llaves; sólo el backend, al sumar una columna.

La posición de esta tabla **es** el orden del catálogo, el mismo de `Columns` hoy; si el código
cambia antes de implementar, gana el código y se corrige la tabla.

| # | Key | DefaultHeader |
| --- | --- | --- |
| 1 | `company` | EMPRESA |
| 2 | `product_code` | Cod. Producto |
| 3 | `quantity` | Cantidad |
| 4 | `unit_price` | Valor Unit |
| 5 | `tax` | IVA |
| 6 | `discount` | Descuento |
| 7 | `line_note` | Nota Detalle |
| 8–12 | `payment_date_1`…`payment_date_5` | Fecha Pago 1…5 |
| 13 | `city` | Ciudad |
| 14 | `document` | Documento |
| 15 | `order_number` | Pedido |
| 16 | `address` | Direccion |
| 17 | `notes` | Observaciones |
| 18 | `phone` | Telefono |
| 19 | `email` | Email |
| 20 | `advisor_code` | Cod. Asesor |
| 21 | `bank` | Banco |
| 22 | `account` | Cuenta |
| 23–32 | `proof_amount_1`, `proof_url_1` … `proof_amount_5`, `proof_url_5` | V. Comprobante 1, URL Comprobante 1 … 5 |
| 33 | `unit_price_without_tax` | Valor Unit sin IVA |

`Columns` de `OrdersExportProcessor` deja de ser la lista literal: se deriva del catálogo, y las
pruebas actuales del processor siguen verdes sin layout guardado.

### Dominio — `OrdersExportLayout`

- `TenantId`, `Columns` (lista ordenada; la posición es el índice), `Version`, `UpdatedAt`.
- Dos tipos de entrada: `OrdersExportColumnSetting.Catalog(Key, Header, Visible)` y
  `.Fixed(Header, Value, Visible)`. La fija no tiene llave: la identifica su posición.
- `Replace(columns, now): bool` — no-op si nada cambió (mismo criterio que
  `Membership.UpdateProfile`: sin él, guardar sin tocar consume versión y da 412 falso en otra
  pantalla abierta).
- Reglas de `Replace` (`QuotationsDomainException` → 422), con prefijo
  `quotations.orders_export_layout.`:
  - llave de catálogo desconocida o repetida → `columns_invalid`
  - `Header` vacío tras recortar, o `> 64` → `header_invalid`
  - dos entradas **visibles** con el mismo `Header` sin distinguir mayúsculas →
    `header_duplicated`. El ERP lee por encabezado; dos iguales se pisan. Entre ocultas puede
    repetirse.
  - ninguna visible → `all_hidden`
  - `Value` de una fija `> 128` tras recortar → `fixed_value_invalid`. **Vacío es válido**: un ERP
    puede exigir la columna aunque venga en blanco (`Nota Detalle` ya funciona así).
  - más de **10** fijas → `too_many_fixed_columns`
- `OrdersExportLayout.Effective(stored?, catalog)` (D8) es una función pura del dominio, probada
  aparte.

### Persistencia — Infrastructure

- Tabla `quotations.orders_export_layouts`: `tenant_id uuid PK`, `columns jsonb NOT NULL`,
  `version bigint NOT NULL`, `updated_at timestamptz NOT NULL`. `Columns` con
  `OwnsMany(...).ToJson()` y discriminador `kind` por entrada. Migración `AddOrdersExportLayout`.
- `IOrdersExportLayoutRepository`: `FindAsync(tenantId, ct)`, `Add(layout)`. No hay `Update`:
  `FindAsync` devuelve la entidad trackeada y `SaveChangesAsync` del unit of work persiste el
  `Replace`, como en el resto de los repositorios del módulo.
- El unit of work de Quotations traduce el `23505` de `PK_orders_export_layouts` —por nombre de
  índice, como siempre— a `RequestConcurrencyException` → 412 (D9).

### Application y API

- `GetOrdersExportLayoutQuery(TenantId)` → `OrdersExportLayoutDto`, el efectivo (D8). Revalida
  tenant y `SettingsRead` (403).
- `UpdateOrdersExportLayoutCommand(TenantId, Columns, ExpectedVersion, CorrelationId)`.
  Validador FluentValidation: `Columns` no nula; por entrada `Kind` conocido, `Header` recortado
  no vacío y `<= 64` → `errors.Columns[i].Header`; `Value` `<= 128` → `errors.Columns[i].Value`;
  `ExpectedVersion > 0`. El dominio da el código, el validador da el campo.
  Handler: revalida tenant y `SettingsUpdate` (403); sin fila y `ExpectedVersion != 1`, o con fila
  y `Version != ExpectedVersion` → 412; `Replace`; audita
  `quotations.orders_export_layout.updated` por `IQuotationAuditPublisher` sólo si cambió;
  devuelve el efectivo.
- `OrdersExportLayoutEndpoints` (`Modules.Quotations.Api`):
  - `GET /api/v1/tenants/{tenantId}/orders-export-layout` — `SettingsRead`, ETag.
  - `PUT` mismo path — `SettingsUpdate`, `If-Match` obligatorio (428/412), cuerpo
    `{ columns: [{ kind, key?, header, value?, visible }] }`, 200 con el efectivo y ETag nuevo.
- `OrdersExportLayoutDto { tenantId, columns, version }` con
  `columns[i] = { kind: "Catalog" | "Fixed", key?, defaultHeader?, defaultPosition?, header, value?, visible }`.
  `defaultHeader` y `defaultPosition` viajan por columna (regla BFF): la pantalla los necesita como
  placeholder, para "restaurar" una sola y para "restaurar todo" sin conocer el catálogo. La lista
  vuelve entera y en orden.

### Processor

`OrdersExportProcessor` recibe `IOrdersExportLayoutRepository`, resuelve el efectivo **una vez por
job**, arma `Columns` (encabezado del tenant; ancho del catálogo, 18 para las fijas) y proyecta cada
fila. `RowsFor` sigue produciendo las 33 celdas en orden de catálogo; una
`OrdersExportLayoutProjection` reordena, descarta ocultas e inserta las fijas como
`ExportCell.OfText(value)` en su posición. Cambio mínimo y comprobable en unitaria.

### Pruebas (TDD, RED antes que GREEN)

- Unitarias de `OrdersExportLayout`: llave desconocida, repetida, `Header` vacío y `> 64`,
  duplicado visible sin distinguir mayúsculas, duplicado entre ocultas permitido, duplicado entre
  fija y catálogo visibles, todas ocultas, 11 fijas, `Value` `> 128`, `Value` vacío válido, no-op de
  `Replace`, cambio de sólo orden sube la versión.
- Unitarias de `Effective`: sin fila = catálogo; llave nueva del catálogo se agrega al final
  visible; llave guardada que ya no existe se descarta; fijas conservan su posición.
- Unitarias de `OrdersExportProcessor`: sin layout, el archivo es idéntico al de hoy (las pruebas
  actuales siguen verdes); con layout: encabezados renombrados, orden del tenant, ocultas
  ausentes, fija con valor y fija vacía en cada fila, anchos del catálogo.
- Integración `GET`/`PUT`: 200 efectivo con ETag `"1"` sin fila; PUT con `If-Match: "1"` crea y
  devuelve `"2"`; 412 con versión vieja; 428 sin `If-Match`; 422 `validation.failed` con
  `errors.Columns[3].Header` y con `errors.Columns[0].Value`; 422 por cada código de dominio; 403
  con tenant ajeno; auditoría en outbox; el Excel exportado después del PUT sale con el layout.
- `QuotationsLayerTests` sigue verde; la migración aplica y revierte.

## Frontend (`qep-frontend`, `features/tenant-settings/`)

- **Ruta** `/settings/orders-export-columns`, enlazada desde la página de Configuración
  ("Columnas del Excel de pedidos"). Con `SettingsRead` se ve; sin `SettingsUpdate`, modo lectura,
  como `tenant-settings-page.tsx`.
- `services/orders-export-layout.api.ts`: `getOrdersExportLayout(tenantId)` y
  `updateOrdersExportLayout(tenantId, { columns, version })` con `If-Match` entrecomillado. 422:
  `errors.Columns[i].Header` / `.Value` → input `i`; códigos de dominio → mensaje general del pie.
  412 → el `CONFLICT_MESSAGE` de settings.
- `types/orders-export-layout.ts`: `OrdersExportLayoutDto`, `OrdersExportColumnDto`. Las filas
  del editor llevan un `id` local para React que no viaja.
- `hooks/use-orders-export-layout.ts`: query + mutation; al guardar, `setQueryData` con la
  respuesta **cancelando el GET en vuelo** (precedente: el 412 fantasma de `setQueryData`).
- `pages/orders-export-columns-page.tsx` + `components/orders-export-columns-editor.tsx`
  (ver el mock): vista previa de la fila de encabezados derivada del estado local; una fila por
  columna con agarre, posición, nombre por defecto + llave (catálogo) o badge FIJA + valor (fija),
  input de encabezado, switch Visible, "Restaurar" si difiere del defecto o papelera si es fija;
  "Agregar columna fija" (deshabilitado con 10); "Restaurar todo por defecto" (catálogo en su
  orden y nombres, sin fijas); pie con "Descartar cambios" y "Guardar cambios".
  `snapshot` al montar (lista + versión); "Guardar" deshabilitado si nada cambió o si la
  validación local falla — las mismas reglas del dominio (encabezado vacío, duplicado visible,
  ninguna visible, más de 10 fijas), para no ir al servidor a que diga lo obvio.
- **Reordenar** con `@dnd-kit/core` + `@dnd-kit/sortable` (dependencia nueva: con 33+ filas,
  mover una del puesto 15 al 1 sólo con botones son 14 clics), más ↑/↓ accesibles por fila.
- Pruebas Vitest + Testing Library: carga y pinta las columnas; guardar deshabilitado sin
  cambios; duplicado visible bloquea con mensaje en las dos filas; ocultar quita de la vista
  previa; agregar y quitar fija; tope de 10; restaurar fila y restaurar todo (quita las fijas);
  `If-Match` entrecomillado; 412 muestra el conflicto; 422 de campo marca el input correcto; modo
  lectura sin `SettingsUpdate`.

Textos del producto en español colombiano, tuteando: "Ya hay otra columna visible con este
nombre.", "Deja al menos una columna visible.", "Puedes agregar hasta 10 columnas fijas.",
"Alguien más cambió esta configuración mientras la editabas. Revisa cómo quedó y vuelve a
guardar."

## Entrega

Un slice por repo, cada uno en su ledger:

1. Backend: catálogo, agregado, migración, repositorio, `GET`/`PUT`, processor. Sin fila guardada
   nada cambia, así que desplegar el backend solo es inocuo.
2. Frontend: la página y el editor. Se publica después del backend.

## Fuera de alcance

- El Excel de cotizaciones (D1).
- Tipo numérico para el valor fijo (D4).
- Homologar el **contenido** de las celdas: formato de fechas, separadores, moneda.
- Columnas calculadas o con valor por fila definidas por el tenant.
- Layouts por usuario o varios layouts por tenant (un ERP por tenant).

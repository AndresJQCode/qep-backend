# Costo de producto y reporte de margen — diseño backend

Fecha: 2026-11-10. Repo: `qep-backend`. Contraparte: spec homónimo en `qep-frontend`
(`docs/superpowers/specs/2026-11-10-costo-de-producto-design.md`). Este spec **manda el contrato**.

Estado: diseño, no construido. Rutas y líneas citadas de `develop` en `79d1628d`, salvo las marcadas como de la rama
`feature/estado-del-envio`.

Marcas: **[owner]** decisión del owner (2026-11-10); **[asistente]** decisión de este spec, revisable; **NUEVO** = no existe hoy.

## 1. Por qué

El owner quiere saber, para un período, cuánto vendió, cuánto le costó lo vendido y qué margen le dejó, en total y por
producto [owner]. Hoy QEP no guarda ningún costo: no hay campo en `Product`, `ProductVariant`, `OrderItem` ni `PosSaleLine`.

QEP va a tener compras e inventario más adelante [owner]. Este diseño no los construye, pero no puede estorbarlos.

## 2. Metas

- Cada variante tiene un **costo unitario manual, opcional** (NUEVO).
- Cada línea vendida **congela su costo** en el momento en que cuenta como venta (§4). Cambiar después el costo de la variante
  no mueve ventas pasadas [owner].
- Tres permisos nuevos, asignables a roles personalizados, sólo en `admin` por defecto [owner] (§5).
- Sin permiso, el costo **no viaja** en ninguna respuesta [owner].
- Un reporte de margen bruto por rango de fechas, con totales y desglose por producto y variante (§6).

## 3. No-metas y anti-metas

- **No** compras, stock, valoración (promedio ponderado / FIFO) ni costo calculado. Sólo el campo manual.
- **No** historial de cambios de costo (a diferencia de `ProductPriceChange`). Queda para después.
- **No** costo en la exportación a Excel de productos, ni importación (no existe importación de productos).
- **No** margen neto: envío, comisiones, publicidad y retenciones quedan fuera. Es margen **bruto**, como Shopify, Square y Alegra.
- **No** rellenar ventas viejas con el costo de hoy [asistente, confirmado por owner]: eso inventa un margen. Las ventas
  anteriores al despliegue quedan «sin costo».
- **Anti-meta:** que una venta sin costo cuente como costo 0. Eso infla el margen al 100 %. Una línea sin costo queda **fuera** del
  margen y se cuenta aparte (§6).
- **Anti-meta:** que el costo se filtre por una respuesta de pedido, POS o producto a quien no tiene el permiso.

## 4. Modelo

### 4.1 Costo de la variante (NUEVO)

`ProductVariant` gana el costo unitario. Forma [owner, P1]: igual que los
precios (`ProductPrice`, `ProductPrice.cs:21-23`), una fila por moneda, `Costs: { "COP": 12000 }`, sin conversión. Un pedido en
USD toma el costo en USD; si la variante no tiene costo en esa moneda, la línea queda sin costo.

Base del costo [owner, P2]: el costo se escribe **sin IVA**, y el margen se mide contra la venta **sin IVA** (§6.3), para que las
dos puntas estén en la misma base, como Shopify y como la norma colombiana (el IVA de la compra es descontable, no costo). Un tenant
no responsable de IVA carga lo que pagó con IVA incluido, porque para él eso es costo; el sistema no los distingue.

Validación: monto `> 0` o ausente; escala decimal igual a la de los precios. Una moneda que el tenant no maneja se rechaza con el
mismo criterio que los precios.

Diseño pensado para inventario: el reporte **nunca** lee el costo de la variante, sólo el congelado en la línea. El día que
exista inventario cambia quién escribe el costo de la variante y en qué momento se congela; el reporte no se toca.

### 4.2 Costo congelado en la línea (NUEVO)

`OrderItem` y `PosSaleLine` ganan:

| Campo | Tipo | Significado |
| --- | --- | --- |
| `UnitCost` | `decimal?` | Costo unitario congelado, en la moneda del documento. `null` = sin costo. |
| `CostSource` | enum `LineCostSource? { Manual }` | De dónde salió. `null` mientras no se ha congelado. Mañana: `Inventory`. |
| `CostCapturedAt` | `DateTimeOffset?` | Cuándo se congeló. |

Una línea «congelada sin costo» (la variante no tenía costo) se distingue de una «no congelada todavía» por `CostCapturedAt`.

### 4.3 Cuándo se congela [owner]

**Regla única:** cada línea congela su costo **una sola vez**, la primera vez que cuenta como vendida. Nunca se reescribe.

- **POS:** al crear la venta, en `CreatePosSaleHandler` (`Modules.Pos.Application/CreatePosSale.cs:80`). El punto natural es
  `ResolveLinesAsync` (`:246-285`), que ya resuelve cada línea por `IPosProductLookup` (`src/Bootstrapper/PosProductLookup.cs`):
  el lookup trae el costo en la moneda de la caja.
- **Pedido:** cuando su estado de pago pasa a **pagado** (`FullPaymentReceived`). Este diseño se construye **sobre** la rama
  `feature/estado-del-envio` (spec `docs/superpowers/specs/2026-11-10-estados-del-pedido-design.md`), que quita `Approved` y agrega
  `Order.MarkPaid`/`UnmarkPaid`. Según ese spec (§3.2), a `FullPaymentReceived` se llega sólo por «Marcar como pagado» o por la
  sincronización de Shopify (`SyncExternalPaymentStatus`). Las dos vías congelan.
  - **Pedido que nace pagado** (Shopify con `"paid"`, `Order.CreateExternal(..., FullPaymentReceived, ...)`,
    `ShopifyOrderWriter.cs:179`; cualquier factory que reciba `FullPaymentReceived`): congela **al crearse** [asistente, por la
    regla única]. El puerto de costo se cablea también en la creación externa y en la ingesta, no sólo en los handlers de pago.
  - **Reingesta de Shopify** de un pedido pagado: las líneas que siguen existiendo conservan su costo; una línea reemplazada o
    nueva es línea nueva y congela al entrar [asistente]. El builder confirma si existe un camino que reescriba líneas
    (hoy sólo se vio `_items.Remove`).
  - Pago parcial (`PartialPaymentReceived`) o en verificación **no** cuenta como venta.
- **Línea agregada a un pedido ya pagado** (ese spec dice «pagado no congela»: se puede editar): congela al agregarse, en
  `AddOrderItems` y `SaveOrderEdits`. Una línea a la que se le cambia la variante es una línea nueva y congela de nuevo.
- **Cambio de cantidad:** conserva el `UnitCost`.
- **Línea eliminada:** desaparece y sale del reporte.
- **El pago vuelve a no pagado** (`UnmarkPaid`): el pedido sale del reporte; las líneas **conservan** su costo. Si se vuelve a
  marcar pagado, reaparece con esos mismos costos [owner].
- **Pedido anulado** (`Cancelled`) o **venta anulada** (`Voided`): sale del reporte. `Archived` sigue contando [asistente].

El congelamiento vive en el dominio (`Order`, `PosSale`), no en el handler, para que ninguna vía nueva a «pagado» lo olvide. El
costo actual llega por un puerto (`IVariantCostLookup` o extensión del lookup existente), porque Quotations y Pos no referencian
Catalog.

### 4.4 Fecha de la venta (NUEVO)

El reporte necesita **cuándo** cuenta cada pedido. Hoy `OrdersReportSource` usa `converted_at` (`OrdersReportSource.cs:410-422`).
Para el margen la fecha es **el momento en que el pedido quedó pagado la última vez** [owner]: al reaprobar cuenta en la fecha
nueva. Si la rama de estados no deja un instante que cubra también la vía de Shopify (`PaidMarkedAt` parece cubrir sólo la manual),
se agrega `PaidAt` a `Order`, escrito en toda transición a `FullPaymentReceived` (y al crear un pedido que nace pagado) y
limpiado al salir. POS usa `created_at`.

**Pedidos pagados antes del despliegue** [asistente]: aparecen en el reporte como «sin costo», no desaparecen. La migración rellena
`PaidAt` desde `PaidMarkedAt` (que la migración de estados llena desde `ApprovedAt`) y, para los pedidos con `FullPaymentReceived` sin
ese dato (Shopify sincronizado, `SyncExternalPaymentStatus` en `Order.cs:1275` no escribe fecha), desde `converted_at`. Es una
aproximación de fecha, no de costo: no inventa margen.

### 4.5 Migración

Columnas nullable en `catalog` (costos de variante, tabla aparte como la de precios si se adopta P1), `quotations.order_items` y
`pos.sale_lines`. Sin backfill (§3).

## 5. Permisos (NUEVO) [owner]

| Código | Qué abre | `RequiredModules` |
| --- | --- | --- |
| `catalog.product_cost.read` | Ver el costo de las variantes | `[Catalog]` |
| `catalog.product_cost.manage` | Escribir el costo | `[Catalog]` |
| `reporting.margin.read` | Ver el reporte de margen | `[Reporting, Catalog]` |

Cada uno necesita las tres registraciones de `src/Bootstrapper/QepServiceCollectionExtensions.cs`: `PermissionDefinition`
(catálogo `:1924-1951`, reporting `:2087-2126`), inclusión en el `RoleDefinition` de **admin** (`:1618-1740`) y `AddPolicy`
(`:2524+`). Ningún otro rol del sistema los recibe. Como `RoleCatalog.ListPermissions()` sólo publica lo que tiene algún rol
del sistema (`RoleCatalog.cs:80-92`), estar en admin es lo que los vuelve elegibles en roles personalizados. Riesgo:
`product_cost.*` y `margin.read` como sensibles (dato comercial), al nivel que tengan los precios de compra en la tabla de
riesgos vigente.

`manage` no implica `read` en el chequeo: un rol con `manage` sin `read` es una combinación inválida que el editor de roles debe
impedir o el handler tratar como `read` [asistente; el builder elige la forma coherente con los roles existentes].

## 6. Contrato HTTP

### 6.1 Catálogo

- `ProductVariantResponse` (`CatalogDtos.cs:106`) gana `costs: { [currency]: number } | null`. **`null` = el llamador no tiene
  `catalog.product_cost.read`**; `{}` = la variante no tiene costo. Nunca se manda el costo sin el permiso.
- `ProductVariantRequest` (`CatalogDtos.cs:286`) gana `costs?: { [currency]: number } | null`:
  - `null` o ausente = **conservar** el costo guardado (mismo criterio que `pricing: null`). Así, guardar un producto sin el permiso
    nunca borra el costo [owner].
  - Un objeto = reemplaza los costos de la variante (un `{}` los borra). Requiere `catalog.product_cost.manage`; sin él, `403` con
    código NUEVO `catalog.product_cost.forbidden`. Vale igual en `POST` (crear producto).
- Las búsquedas del POS, cotizaciones y pedidos **no** exponen costo.

### 6.2 Pedidos y POS

Las respuestas de pedido, línea de pedido y venta del POS **no** exponen `UnitCost` ni `CostSource`, con ningún permiso. El costo
congelado sólo sale por el reporte.

### 6.3 Reporte de margen (NUEVO)

`GET /api/v1/tenants/{id}/reports/margin` y `/reports/margin/summary`, con `reporting.margin.read`, en `ReportingEndpoints.cs`
como los existentes. Filtros: `from`, `to` (`DateOnly`, `ReportPeriod.Of(TenantCalendar, …)`, `ReportingFilters.cs:14-20`),
`source` (los de pedido más POS, como en el reporte de pedidos), paginación. Sin filtro de asesor en esta versión [asistente].

Qué cuenta (todo en SQL, nunca en el navegador):

- Pedidos con `PaymentStatus = FullPaymentReceived`, `Status <> Cancelled` y fecha de pago (§4.4) en el período.
- Ventas POS `Completed` con `created_at` en el período.
- **Venta neta de la línea** = `Subtotal` de `OrderItem`/`PosSaleLine`: después de descuentos y **sin IVA** (verificado en los
  dos; sujeto a P2 y a P3).
- **Costo de la línea** = `UnitCost × Quantity`.
- **Utilidad bruta** = venta neta − costo. **Margen %** = utilidad ÷ venta neta × 100, redondeado a 1 decimal, `null` si la venta
  neta es 0.
- Sólo líneas con `UnitCost` entran a venta neta, costo, utilidad y margen. Las líneas sin costo se reportan aparte: cantidad de
  líneas y venta neta sin costo.

Todo agrupado **por moneda**, sin convertir, con la forma `ReportMoneyDto(Currency, Amount)` (`ReportMoney.cs:7-19`).

`summary`: por moneda, `{ netSales, cost, grossProfit, marginPercent, uncostedLines, uncostedNetSales }`.

Lista: una fila por producto y moneda, `{ productId, productName, currency, quantity, netSales, cost, grossProfit, marginPercent,
variants: [{ variantId, variantName, quantity, netSales, cost, grossProfit, marginPercent }] }`.

Nombres [asistente]: las líneas congelan un solo nombre (el visible de la variante, «Camiseta – M / Azul»; `OrderItem.ProductName`
es nullable). La fila de **producto** toma el nombre **actual** del catálogo (el adaptador de Bootstrapper ya cruza esquemas en SQL);
si el producto se borró, el nombre congelado de la línea más reciente. La fila de **variante** toma el nombre congelado de su línea
más reciente; si todas lo tienen `null`, el código de la variante, y si tampoco, «Producto sin nombre». Un producto borrado sigue
apareciendo. Orden por defecto: utilidad bruta descendente en la moneda por defecto del tenant
(`ITenantDefaultCurrency`), como los rankings existentes.

Fuente: un puerto nuevo en `ReportSources.cs` con adaptador en Bootstrapper, como `OrdersReportSource`.

## 7. Errores

| Código | Status | Cuándo |
| --- | --- | --- |
| `catalog.product_cost.forbidden` (NUEVO) | 403 | Mandar `costs` no nulo sin `manage`. |
| validación de monto | 422 | Costo `<= 0` o moneda inválida, con el mismo mapa `errors` que los precios. |

## 8. Pruebas

TDD, RED antes que GREEN. Mínimo:

- Dominio: congela una vez; no recongela al desmarcar y remarcar pagado; línea agregada después de pagado congela; cambio de
  cantidad conserva; variante sin costo congela `null` con `CostCapturedAt`; pago parcial no congela; Shopify congela.
- Catálogo: sin `read` la respuesta trae `costs: null`; `costs: null` en PUT conserva; `costs` sin `manage` da 403.
- Reporte (integración, Testcontainers): anulado fuera, archivado dentro, desmarcado fuera, POS anulada fuera, línea sin costo fuera
  del margen y contada aparte, dos monedas sin mezclar, período en la zona del tenant.
- `ArchitectureTests`: Quotations y Pos sin referencia a Catalog.

## 9. Dependencias y despliegue

- **Depende de `feature/estado-del-envio` mergeada en `develop`.** No se construye en paralelo: los dos tocan `Order` y los handlers
  de pago.
- **Va antes de `impuestos-por-tipo`** [owner, P3].
- Backend primero (migración); el frontend tolera un backend viejo (spec frontend).

## 10. Pendientes del owner

- ~~P1 — Moneda del costo~~: resuelto 2026-11-10, **un costo por moneda**, como los precios.
- ~~P2 — IVA~~: resuelto 2026-11-10, **costo sin IVA** contra venta sin IVA.
- ~~P3 — Orden frente a `impuestos-por-tipo`~~: resuelto 2026-11-10, **costo de producto va antes**. `impuestos-por-tipo` se
  rebasa sobre esto y debe conservar que `Subtotal` sea la venta neta sin IVA (o ajustar §6.3).

## 11. Lo que queda al builder

Nombres finales de tablas, columnas y del puerto de costo; forma de evitar el `manage` sin `read`; el índice para el reporte;
redondeos intermedios (seguir los del reporte de pedidos).

# Pedidos — «Fecha del pedido» editable · diseño (backend)

> **Estado:** aprobado por el owner 2026-11-16 (decisiones F-1 a F-8 abajo).
> **Frontend:** `qep-frontend/docs/superpowers/specs/2026-11-16-order-date-design.md` (consume el contrato de §4).
> **Deploy:** backend primero. La migración corre sola al arrancar la API (`Program.cs`, bloque bajo `RunUnderStartupLockAsync`).

## 1. Problema

El asesor necesita corregir la fecha de un pedido (por ejemplo, un pedido que se tomó el viernes y se cargó el lunes). Hoy la única
fecha del pedido es `ConvertedAt`, que sólo se escribe al crearlo (`Order.cs`, `Build`) y no tiene método ni campo que la cambie.
Además es el registro técnico de cuándo nació el pedido, que no se debe perder.

## 2. Decisiones del owner (2026-11-16)

| # | Decisión |
| --- | --- |
| F-1 | Campo nuevo **«Fecha del pedido»** (`OrderDate`), aparte de `ConvertedAt`. `ConvertedAt` queda como registro de cuándo se creó y no cambia nunca. |
| F-2 | `OrderDate` es `DateOnly` (sólo día, sin hora), en el calendario del tenant. |
| F-3 | Todo lo que hoy corta o muestra «la fecha del pedido» pasa a `OrderDate`: filtro y orden de `GET /orders`, exportación (cursor y columna de fecha), reporte de pedidos y PDF del pedido (§5). |
| F-4 | Se edita en el guardado del borrador (`PUT /orders/{orderId}`) y en su preview, con la misma regla que el resto: sólo si el pedido es `IsEditable` (abierto, sin preparar, sin facturar) y con `quotations.order.manage`. |
| F-5 | No se acepta una fecha futura: el tope es «hoy» en el calendario del tenant. No hay tope hacia atrás. |
| F-6 | Cambiar la fecha deja una entrada de historial `OrderDateChanged` con la fecha anterior y la nueva. |
| F-7 | Los pedidos existentes se migran: `OrderDate` = día local de `ConvertedAt` en el huso del tenant. Las cotizaciones no se migran. |
| F-8 | El detalle muestra también cuándo se creó el pedido (`convertedAt`, fecha y hora) y cuándo se creó la cotización de origen: `SourceQuotationResponse` suma `CreatedAt`. |

## 3. Dominio (`Modules.Quotations.Domain`)

- `Order.OrderDate` (`DateOnly`, `private set`).
- Todas las fábricas (`CreateFromQuotation`, manual, `CreateFromStore`, `CreateExternal`) reciben la fecha ya resuelta por Application
  (`orderDate`): el dominio no conoce el huso. Para Shopify es el día local del `OccurredAt` que ya recibe `CreateExternal`; para
  todo lo demás, el día local de `occurredAt`.
- `Order.ChangeOrderDate(DateOnly date, DateOnly today, DateTimeOffset occurredAt) : bool`, con el patrón de `UpdateNotes`:
  - Si no es `IsEditable` → `EnsureEditable` (la misma excepción que los demás cambios del borrador).
  - Si `date > today` → `QuotationsDomainException("order.order_date.in_future")` (422).
  - Si `date == OrderDate` → `false` (sin historial, sin subir `Version`).
  - Si cambia → escribe `OrderDate`, `UpdatedAt`, sube `Version` y devuelve `true`.
- `OrderHistoryEventType.OrderDateChanged`, **al final** del enum (la columna guarda el nombre, el orden no se reescribe). La
  entrada la escribe el handler, como las demás: `Details` = `OrderChangeSummary.OrderDateChanged(anterior, nueva)` =
  `"Cambió la fecha del pedido de {anterior:yyyy-MM-dd} a {nueva:yyyy-MM-dd}."`.

## 4. Contrato HTTP

**Lectura.** `OrderResponse` y `OrderListItemDto` suman `OrderDate` (`"yyyy-MM-dd"`) como **último** campo.
`SourceQuotationResponse(Guid Id, string QuotationNumber, DateTimeOffset CreatedAt)`: `CreatedAt` es el `CreatedAt` de la cotización.

**Escritura.** `SaveOrderEditsRequest` suma `DateOnly? OrderDate = null` como último parámetro, también en el preview
(`POST /orders/{orderId}/preview`, mismo request):

- Ausente o `null` → conserva la guardada (un frontend viejo no la toca).
- Presente → `ChangeOrderDate` con `today` = hoy en el calendario del tenant (`ITenantClock`).

**Errores nuevos.**

| Código | Status | Cuándo |
| --- | --- | --- |
| `order.order_date.in_future` | 422 | `orderDate` posterior a hoy del tenant. Es un código de dominio: llega sin mapa `errors` y el frontend lo asocia al campo por código. |

Un pedido no editable responde lo mismo que cualquier otro cambio del borrador hoy (sin código nuevo).

**Filtro de la lista.** Los parámetros `convertedFrom` / `convertedTo` de `GET /orders` **conservan su nombre** (no se rompe el
contrato) pero pasan a cortar por `OrderDate`, inclusive en las dos puntas, comparando fechas sin huso.

## 5. Lecturas que pasan a `OrderDate`

| Lugar | Hoy | Después |
| --- | --- | --- |
| `OrderRepository` — filtro de lista (`convertedFrom`/`convertedTo`) | `ConvertedAt` en rango UTC del día del tenant | `OrderDate >= from && OrderDate <= to` |
| `OrderRepository` — orden de lista y cursor | `ConvertedAt desc, OrderNumber` | `OrderDate desc, ConvertedAt desc, OrderNumber` |
| `OrdersExportProcessor` — cursor (`OrderExportCursor`) | `(ConvertedAt, OrderNumber)` | `(OrderDate, ConvertedAt, OrderNumber)` |
| `OrdersExportProcessor` — celda de fecha (línea ~264) | `calendar.ToLocal(order.CreatedAt)` | `order.OrderDate` (mismo formato `yyyy-MM-dd`) |
| `Bootstrapper/OrdersReportSource.cs` — período, agrupación por mes y orden de filas | `o.converted_at` | Columna nueva `occurred_on date` en las dos ramas: `o.order_date` en pedidos y `(s.created_at AT TIME ZONE @time_zone)::date` en ventas POS. El período y la serie mensual van sobre `occurred_on`; las filas ordenan por `occurred_on DESC, occurred_at DESC`. Las ventas POS dan lo mismo que hoy. `OrdersReportItemDto` **no cambia** (el frontend no pinta su fecha). |
| `QuotationPdfDocumentMapper.FromOrder` — fecha del documento | `calendar.ToLocal(header.CreatedAt)` | `order.OrderDate` |

**No cambian (a propósito):**

- `OrderRepository` línea ~98 (`Source == source && ConvertedAt >= since`): ventana técnica de sincronización, no la fecha del negocio.
- Reporte de margen: corta por `PaidAt` (spec costo de producto §4.4).
- Retenciones: el año de la UVT y la fecha de `MunicipalWithholdingExplanation` siguen leyendo `CreatedAt`. Cambiar la fecha del
  pedido **no** recalcula impuestos. Ver §8, P-1.

## 6. Persistencia y migración

Columna `quotations.orders.order_date date not null`. Una sola migración EF (`AddOrderDate`) en tres pasos:

1. `AddColumn` nullable.
2. Backfill:
   ```sql
   UPDATE quotations.orders o
   SET order_date = (o.converted_at AT TIME ZONE COALESCE(NULLIF(t.time_zone, ''), 'America/Bogota'))::date
   FROM tenancy.tenants t
   WHERE t.id = o.tenant_id;
   ```
   `tenancy.tenants.time_zone` es nullable; `America/Bogota` es el default del registro (`RegistrationEndpoints.cs`). Tenancy migra
   antes que Quotations en `Program.cs`, así que la tabla existe.
3. `AlterColumn` a `NOT NULL`; índice nuevo `(tenant_id, order_date, converted_at, order_number)`. El índice viejo
   `(tenant_id, converted_at, order_number)` se queda (lo sigue usando la ventana de sincronización).

Después del deploy, filtros, exportación, reportes y PDF dan lo mismo que antes hasta que alguien cambie una fecha: `CreatedAt` y
`ConvertedAt` se escriben con el mismo `occurredAt` en `Build`, así que su día local es el que queda en `order_date`.

## 7. Pruebas

- **Dominio:** `ChangeOrderDate` — cambia y escribe historial; misma fecha no hace nada; fecha futura → `in_future`; no editable
  (preparado, facturado, anulado) → rechazo; hoy exacto se acepta.
- **Fábricas:** cada una deja `OrderDate` = la fecha recibida.
- **Integración (filtradas por nombre):** `PUT /orders/{id}` con `orderDate` cambia la fecha y la lista la filtra por ella; sin
  `orderDate` no la toca; preview no persiste; `GET /orders` ordena por `OrderDate`; `SourceQuotationResponse.CreatedAt` viaja;
  exportación y reporte cortan por `OrderDate`.
- **Migración:** un pedido con `converted_at = 2026-11-14T02:00:00Z` en un tenant `America/Bogota` queda con `order_date = 2026-11-13`.
- `ArchitectureTests` sin cambios esperados.

## 8. Decisiones cerradas

| # | Pregunta | Decisión del owner (2026-11-16) |
| --- | --- | --- |
| P-1 | ¿Cambiar la fecha a otro año debe recalcular retenciones con la UVT de ese año? | **No.** El pedido no es una factura: QEP no la emite (spec 2026-10-05, `InvoicedAt` sólo deja constancia). La factura se hace después en el programa contable, con su propia fecha y su UVT. Las retenciones del pedido siguen con `CreatedAt` y `OrderDate` no las toca. |

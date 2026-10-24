# Reportes: ventas POS en el reporte de pedidos, y solapas por permiso

Fecha: 2026-10-23. Estado: **diseño acordado en conversación, sin implementar**. Cierra el pendiente 9 del spec del
POS ([2026-10-07-modulo-pos-design.md](2026-10-07-modulo-pos-design.md), «Ventas POS en Reporting (reporte propio, o
sumarlas al de pedidos)»): se suman al de pedidos. Toca los dos repos: `qep-backend` (Reporting y su adaptador en
Bootstrapper) y `qep-frontend` (reportes).

## Por qué

Hoy Reportes no ve el POS. `OrdersReportSource` (`src/Bootstrapper/OrdersReportSource.cs`) lee sólo
`quotations.orders`, así que un tenant que vende en mostrador tiene un reporte de pedidos que no cuenta esas ventas: el
total del período, la serie por mes y los rankings le salen cortos sin ningún aviso.

Al revisar quién ve qué salió un segundo problema, de la pantalla: las solapas de Reportes se ven siempre, sin mirar el
permiso (`reports-tabs.tsx`, decisión documentada ahí), y un asesor que abre «Log» o «Clientes» llega a un «No tienes
permiso». El owner quiere que esas dos no aparezcan.

## Qué se construye

### 1. Las ventas POS entran al reporte de pedidos (backend)

`GET /api/v1/tenants/{tenantId}/reports/orders` y `GET …/reports/orders/summary` leen **la unión** de
`quotations.orders` y `pos.sales`. Una venta POS es una fila más del reporte, con origen `Pos`.

Cómo se mapea una venta (`PosSale`, `src/Modules/Pos/Modules.Pos.Domain/PosSale.cs`) a los campos de
`OrdersReportItemDto`:

| Campo del DTO | Pedido (hoy) | Venta POS |
| --- | --- | --- |
| `OrderId` | `orders.id` | `sales.id` |
| `OrderNumber` | número del pedido | `SaleNumber` (`POS-000123`, `PosSaleNumber.Format`) |
| `Source` | `Quotation` / `Manual` / `Shopify` | `Pos` |
| `QuotationId`, `QuotationNumber` | los de la cotización de origen o `null` | `null` |
| `ConvertedAt` | `ConvertedAt` | `CreatedAt` |
| `AdvisorId`, `AdvisorName` | asesor del pedido | `CashierId` (es un `MemberId`, el mismo tipo de id que el asesor) y su correo por `ReportingPeopleLookup` |
| `ClientId` | cliente del pedido | `CustomerId`, **nulo** en una venta a consumidor final |
| `ClientName`, `ClientCuc` | de la ficha del cliente | con `CustomerId`, de la ficha igual que un pedido; sin él, `CustomerName` de la venta («Consumidor final», `PosFinalConsumer.Name`) y CUC `null` |
| `Status` | `Pending` / `Approved` / `Cancelled` / `Invoiced` | `Completed` / `Voided` (nombres de `PosSaleStatus`) |
| `PaymentStatus` | el del pedido | siempre `FullPaymentReceived` |
| `Currency` | moneda del pedido | `Currency` de la venta (la de la caja) |
| `Subtotal`, `TaxAmount`, `Total` | los del pedido | los de la venta (`Total = Subtotal + TaxAmount`, `PosSale.Create`) |
| `FreightSubtotal`, `FreightTaxAmount` | envío del pedido | `0` |

**Cambio de contrato:** `OrdersReportItemDto.ClientId` pasa de `Guid` a `Guid?`. Es la única forma honesta de decir
«esta venta no tiene ficha de cliente»; inventar un id sería peor. El listado `/reports/orders` no lo consume hoy el
frontend (la pantalla sólo usa `/summary`, ver §3), así que el cambio no rompe ninguna pantalla, pero el tipo del frontend
se corrige igual.

**El filtro de estado de pago** se aplica igual a las dos fuentes: `FullPaymentReceived` incluye las ventas POS;
`PartialPaymentReceived` y `PaymentPending` las excluyen. **El de cliente** filtra por `CustomerId` (una venta a
consumidor final nunca coincide). **El de asesor** filtra por `CashierId`.

### 2. Filtro nuevo «Origen» y bloque «Por origen» (backend)

- **Filtro `source`** en el listado y el resumen, con los valores `Quotation`, `Manual`, `Shopify` y `Pos`. Se agrega a
  `OrdersReportFilter`/`OrdersReportCriteria` como `PaymentStatus`: un enum redeclarado en `Modules.Reporting.Domain`
  (`OrderSourceFilter`, mismo criterio que `OrderPaymentStatusFilter`), parseado por `ReportFilterParser` y validado por el
  validador del filtro, de modo que un valor desconocido es `422 validation.failed` con `errors.Source`. Sin `source`
  vienen las cuatro fuentes.
- **`OrdersReportSummaryDto.BySource`**: una entrada por origen con su conteo y su total por moneda. **Vienen las cuatro
  siempre**, en orden fijo `Quotation`, `Manual`, `Shopify`, `Pos`, incluso en cero (regla de colecciones fijas del
  CLAUDE.md, precedente `ReportStatusSliceDto`). Con el filtro `source` puesto, las otras tres vienen en cero. El nombre
  del origen viaja como el del enum; la etiqueta la pone el frontend.
- El motivo de las cuatro siempre se escribe en el DTO, como pide la regla de backend for frontend.

### 3. El resumen suma las dos fuentes (backend)

Todo lo que hoy agrega `OrdersReportSource.SummarizeAsync` pasa a agregar pedidos **y** ventas POS, con los mismos
filtros:

- **Totales por moneda** (`Subtotals`, `TaxAmounts`, `FreightSubtotals`, `FreightTaxAmounts`, `Totals`) y `OrderCount`.
  COP y USD nunca se suman (spec 2026-10-08). En la práctica una caja es COP, pero el código no lo asume.
- **Serie mensual** con el mes del tenant, igual que hoy (`AT TIME ZONE` con el id IANA; `CreatedAt` para la venta).
- **Ranking por asesor**: el cajero entra como un asesor más, por su `MemberId`.
- **Ranking por cliente**: las ventas con `CustomerId` entran por su cliente. Las de consumidor final **no** entran al
  ranking ni al pliegue «Otros»: van en un campo nuevo del resumen, **`WithoutClient`** (conteo y total por moneda, con
  la forma de `ReportStatusSliceDto` sin el nombre), que viene siempre, en cero si no hay. Así `ByClient` +
  `WithoutClient` cierra contra el total con un campo y no con una omisión. *Decisión mía*, por dos motivos:
  - Si «Consumidor final» fuera una entrada del ranking, en un tenant con mostrador lo encabezaría siempre y taparía a
    los clientes reales.
  - Meterlas en «Otros» no funciona con el código actual. `ReportRankFolding.FoldOthersAsync` no arma el pliegue con
    menos de cinco clientes con nombre ni cuando no sobran entidades. Con tres clientes y ventas de mostrador, el
    ranking dejaría de cerrar sin aviso, y si sólo sobraran ventas sin cliente la pantalla mostraría «Otros (0)»
    (`report-rank-bars.tsx`).

  `FoldOthersAsync` no cambia (lo comparten los reportes de cotizaciones y de clientes); se le pasan el conteo y los
  totales **sin** las ventas a consumidor final, para que el resto que calcula sea sólo de clientes.
- **Período anterior** (`Previous`): el mismo cálculo sobre la otra ventana, con el mismo `source`. Ya sale solo, porque
  `SummarizePrecedingAsync` reusa el criterio entero.
- **Lo anulado no suma**: hoy se excluye `OrderStatus.Cancelled`, y desde ahora también `PosSaleStatus.Voided`, en los
  totales, la serie, los rankings, `BySource` y el período anterior. En el listado se siguen viendo con su estado, igual que
  un pedido cancelado.

### 4. Quién ve qué (sin cambios de permisos)

Decisión del owner: **no cambia ningún rol ni permiso.**

- **admin** sigue con todos los reportes y `reporting.all_advisors.read`: ve los pedidos y las ventas POS de todos.
- **advisor** sigue con `reporting.orders.read` y `reporting.quotation.read`, sin `all_advisors`: `ScopeAdvisorAsync` le
  fija el filtro a su membresía, y el adaptador lo aplica a `AdvisorId` de los pedidos **y** a `CashierId` de las ventas.
  Un asesor que también vende en el POS (por un rol personalizado) ve sus ventas POS; nunca las de otro cajero.
- **cashier** sigue sin permisos de Reportes. En el POS ya ve sólo sus ventas y cajas (`PosScope.CashierFilterAsync`, sin
  `pos.register.read`). Nada que hacer.
- Un rol personalizado («Contabilidad», por ejemplo) con `reporting.orders.read` y `reporting.all_advisors.read` ve todo.

### 5. Pantalla del reporte de pedidos (frontend)

`OrdersReportPage` (`src/features/reports/pages/orders-report-page.tsx`) muestra estadísticas, no filas. Cambia así:

- **Filtro «Origen»** en `OrdersReportFilters`, junto a fechas, asesor, cliente y estado de pago, con Cotización, Manual,
  Shopify y POS. Cuenta en `countActiveOrdersFilters` y viaja como `source` al resumen.
- **Bloque «Por origen»**: las cuatro entradas de `BySource`, con conteo y total en la moneda que el panel ya elige
  (`chartCurrencies`/`amountIn`). La forma visual sigue a los bloques existentes (`ReportCountBars`, `ReportRankBars`).
- **Ranking por cliente**: debajo de las entradas, una línea «Consumidor final» con `WithoutClient` cuando su conteo
  no es cero. Va aparte de las barras del ranking, no como una barra más.
- Los totales, la serie y el ranking por asesor no cambian de forma: ahora sólo traen más datos.
- La descripción de la tarjeta «Pedidos» del índice deja de decir sólo «Pedidos convertidos» y menciona las ventas del POS.

### 6. Solapas y enlaces por permiso (frontend)

Decisión del owner: **«Log» y «Clientes» no se muestran a quien no tiene su permiso.** Los demás reportes siguen como
hoy: visibles siempre, con el cartel «No tienes permiso» adentro.

| Dónde | «Log» (`platform.request_log.read`) | «Clientes» (`reporting.customer.read`) |
| --- | --- | --- |
| `ReportsTabs` (`reports-tabs.tsx`) | se oculta sin el permiso | se oculta sin el permiso |
| Tarjetas de `ReportsIndexPage` | se oculta sin el permiso | se oculta sin el permiso |
| Enlace «ver en el log» de `QuoteSendFailureDialog` y `OrderSendFailureDialog` | ya se oculta sin el permiso (`canReadLog`), sin cambio | — |
| Menú lateral (`sidebar-nav-items.ts`) | ya filtra por permiso, sin cambio | ya filtra por permiso, sin cambio |

- Los comentarios de `reports-tabs.tsx` y `reports-index-page.tsx` que documentan «se ven siempre» se corrigen: la regla
  queda «se ven siempre, salvo Log y Clientes, que se ocultan sin permiso (decisión del owner, 2026-10-23)».
- *Decisión mía:* mientras los permisos cargan, las dos solapas no se pintan (deny-by-default, el mismo criterio que ya
  usan las páginas). La barra puede ganar dos solapas al terminar de cargar; es preferible a mostrarlas y quitarlas.
- *Decisión mía:* entrar por URL directa a `/reports/log` o `/reports/customers` sin permiso sigue mostrando el cartel
  «No tienes permiso», sin redirigir. La autoridad es el backend, que ya responde 403.

## Restricciones

- **Todo se agrega y se pagina en la base**, como hoy. El listado necesita un solo orden total sobre las dos tablas
  (`ConvertedAt`/`CreatedAt` descendente, luego número, luego id: `POS-…` y el número de pedido no garantizan unicidad
  entre ellos), y los rankings un top N sobre la unión. Traer filas a memoria para mezclarlas está prohibido por la misma
  razón por la que existe `/summary`.
- `quotations.orders` y `pos.sales` son de `DbContext` distintos (`QuotationsDbContext`, `PosDbContext`) sobre la misma
  base y en esquemas distintos: EF no une dos contextos. Lo natural es SQL crudo con `UNION ALL` en el adaptador de
  Bootstrapper, con precedente en `CatalogVariantUsageChecker`. La forma exacta es del plan.
- **Reporting no referencia Pos ni Quotations**: todo sigue entrando por `IOrdersReportSource`, con el adaptador en
  Bootstrapper (`ReportingLayerTests`).
- Índices: `pos.sales` ya tiene `IX_sales_tenant_created` (`tenant_id, created_at`), que sirve al filtro por período. Si el
  plan necesita otro, lo justifica con el plan de consulta.
- Si el adaptador lee `pos.sales` por nombre de tabla y columna, se agrega al CLAUDE.md una entrada de gotchas como la de
  `CatalogVariantUsageChecker`: renombrar una columna compila y el reporte responde 500. Lo amarra una prueba contra el
  esquema migrado.

## Fuera de alcance

- Un reporte propio del POS (por medio de pago, por caja, por producto). El cierre de caja ya reporta totales por medio
  de pago (spec del POS).
- Cambios en roles, permisos o en lo que ve el cajero en el POS.
- La exportación a Excel de pedidos (`POST /orders/export`, módulo Quotations): no lee el reporte y no cambia.
- El reporte de cotizaciones, el de clientes y el de cambios de precio: sólo cambia la visibilidad de la solapa
  «Clientes».
- Ocultar «Cambios de precio» sin permiso: el owner nombró sólo Log y Clientes.

## Anti-objetivos

- Sumar COP con USD en cualquier cifra.
- Contar una venta anulada como venta.
- Que un asesor sin `reporting.all_advisors.read` vea ventas POS de otro cajero.
- Que «Consumidor final» aparezca en el ranking como si fuera un cliente.
- Que el bloque «Por origen» omita un origen en cero.

## Pruebas que importan

- **Backend, integración (Reporting):** ventas POS creadas por la API del POS (`PosApiHarness.EnablePosAsync` y una caja
  abierta) junto con pedidos de cotización. Casos: el listado las trae con `Source = "Pos"` y en orden; el resumen las
  suma en totales, serie, rankings y `BySource`; una venta anulada aparece en el listado y no suma; `source=Pos` deja sólo
  POS y `BySource` con tres ceros; `paymentStatus=PaymentPending` las excluye; un asesor sin `all_advisors` ve sólo sus
  ventas aunque mande el `advisorId` de otro; una venta a consumidor final va a `WithoutClient` y no al ranking ni a
  «Otros», con menos de cinco clientes y con más; `source` inválido da `422` con `errors.Source`.
- **Backend, unidad:** el parser de `OrderSourceFilter`.
- **Frontend:** solapas e índice con y sin cada permiso; el filtro «Origen», el bloque «Por origen» (incluido un origen
  en cero) y la línea «Consumidor final» (presente y en cero).
- Pruebas acotadas a los módulos tocados (Reporting, Pos si se toca, Bootstrapper, ArchitectureTests), no la suite
  completa.

## Lo que queda para el plan

- La forma del SQL de la unión y si el resumen hace una consulta por agregado sobre la unión o reutiliza un CTE.
- Dónde exactamente va el bloque «Por origen» en el panel y qué componente reutiliza.
- Las etiquetas en español de los orígenes y de `Completed`/`Voided`, si el frontend aún no las tiene.
- Actualizar el README donde describe el reporte de pedidos.

# Ventas POS en el reporte de pedidos — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que `GET /reports/orders` y `GET /reports/orders/summary` cuenten las ventas POS junto con los pedidos, con un
filtro `source` y un desglose `bySource`, y con las ventas a consumidor final aparte en `withoutClient`.

**Architecture:** El contrato de Reporting crece primero (filtro, DTOs) sobre el adaptador EF de hoy. Después
`OrdersReportSource` (Bootstrapper) pasa de LINQ sobre `QuotationsDbContext` a SQL crudo con un `UNION ALL` de
`quotations.orders` y `pos.sales`. EF no une dos `DbContext`, y paginar o rankear la unión en memoria está prohibido. Reporting sigue sin
referenciar Pos ni Quotations: todo entra por `IOrdersReportSource`.

**Tech Stack:** .NET 10, EF Core + Npgsql (PostgreSQL 18), FluentValidation, xUnit v3 + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-10-23-reportes-ventas-pos-design.md`. Léelo entero. El frontend tiene su plan en
`../qep-frontend/docs/superpowers/plans/2026-10-24-reportes-ventas-pos-frontend.md` y depende de la sección «Contrato para
el frontend» al final de este.

**Rama:** `feature/reportes-ventas-pos`, en el worktree `.claude/worktrees/reportes-ventas-pos` (ya existe, con el spec
commiteado). Antes de **cada** tarea y de **cada** commit, `git branch --show-current` tiene que decir esa rama; si dice
`main` o `develop`, para. Nunca `git stash`.

## Global Constraints

- TDD obligatorio: RED con la salida literal antes del GREEN, los dos en el reporte de cada tarea.
- Conventional commits en español, con `RVP-NN` en el asunto, **sin atribución de IA** (sin `Co-Authored-By`; la regla del
  repo pisa cualquier recordatorio del harness).
- No corras la suite completa: cada comando lleva `--filter "FullyQualifiedName~<Clase>"`. Docker corriendo para las de
  integración. En esta máquina las de integración necesitan `export Seed__Enabled=false` (bash) antes de `dotnet test`.
- Formato: `dotnet format --verify-no-changes --include <tus archivos>`, no el repo entero.
- Ningún permiso ni rol cambia (spec §4). `ReportingAuthorization.ScopeAdvisorAsync` no se toca: el adaptador aplica el
  `AdvisorId` resultante a `orders.advisor_id` **y** a `sales.cashier_id`.
- Enums por nombre: `Source` ∈ `Quotation | Manual | Shopify | Pos`; estado de una venta `Completed | Voided`; estado de pago
  de una venta siempre `FullPaymentReceived`.
- COP y USD nunca se suman. Lo anulado (`orders.status = 'Cancelled'`, `sales.status = 'Voided'`) no suma en ningún agregado
  y sí aparece en el listado.
- Comentarios y docs tutean, en español colombiano. El motivo de cada decisión de forma va en el DTO (regla de backend for
  frontend).

## Review Focus

1. **Filtro opcional nulo en SQL crudo.** Con un parámetro `null` sin tipo, PostgreSQL responde
   `could not determine data type of parameter`. Todo filtro opcional va con tipo explícito. Lo cubren las pruebas que
   llaman sin filtros y con cada filtro (RVP-02, RVP-03).
2. **Asesor sin `all_advisors` que manda el `advisorId` de otro cajero.** Tiene que ver sólo sus ventas POS. Prueba
   `AnAdvisorWithoutAllAdvisorsSeesOnlyTheirOwnPosSales` (RVP-02).
3. **Mes del tenant en una venta de las 23:00 del 31 de diciembre en Bogotá.** Cae en diciembre, no en enero. Prueba
   `PosSaleOnNewYearsEveCountsInDecember` (RVP-03).
4. **Menos de cinco clientes con ventas de mostrador.** `byClient` + `withoutClient` cierran contra el total sin fila
   «Otros». Prueba `FinalConsumerSalesGoToWithoutClientNotToTheRanking` (RVP-03).
5. **Orden estable entre páginas con un pedido y una venta en el mismo instante.** Ninguna fila se repite ni se pierde
   entre dos páginas. Prueba `PagesNeverRepeatOrSkipRowsAcrossBothSources` (RVP-02).

## Mapa de archivos

| Archivo | Responsabilidad |
| --- | --- |
| `src/Modules/Reporting/Modules.Reporting.Domain/ReportFilterEnums.cs` | `OrderSourceFilter` y su parser |
| `src/Modules/Reporting/Modules.Reporting.Application/ReportingFilters.cs` | `Source` en filtro, criterio y validador |
| `src/Modules/Reporting/Modules.Reporting.Application/ReportingDtos.cs` | `OrdersReportItemDto.ClientId` → `Guid?` |
| `src/Modules/Reporting/Modules.Reporting.Application/ReportSummaries.cs` | `ReportSourceSliceDto`, `ReportWithoutClientDto`, campos nuevos del resumen y del agregado |
| `src/Modules/Reporting/Modules.Reporting.Application/GetOrdersReportSummary.cs` | pasa los campos nuevos al DTO |
| `src/Modules/Reporting/Modules.Reporting.Api/ReportingEndpoints.cs` | query param `source` en las dos rutas |
| `src/Bootstrapper/OrdersReportSource.cs` | SQL de la unión: listado y resumen |
| `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/ReportingApiHarness.cs` | sembrar ventas POS |
| `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/OrdersReportPosApiTests.cs` | pruebas nuevas de la unión |
| `README.md`, `CLAUDE.md` | reporte de pedidos con POS; gotcha del SQL por nombre |

---

### Task RVP-01: contrato — filtro `source`, `bySource`, `withoutClient` y `clientId` nullable

Sobre el adaptador EF de hoy, todavía sin ventas POS: `source=Pos` da un conjunto vacío, `bySource` sale de los pedidos y
`withoutClient` viene en cero. Así el contrato queda probado antes de cambiar el SQL.

**Files:**
- Modify: `src/Modules/Reporting/Modules.Reporting.Domain/ReportFilterEnums.cs`
- Modify: `src/Modules/Reporting/Modules.Reporting.Application/ReportingFilters.cs` (`OrdersReportFilter`, `OrdersReportCriteria`, `ToCriteria`, `OrdersReportFilterValidator`)
- Modify: `src/Modules/Reporting/Modules.Reporting.Application/ReportingDtos.cs:18-42`
- Modify: `src/Modules/Reporting/Modules.Reporting.Application/ReportSummaries.cs:25-35,84-93`
- Modify: `src/Modules/Reporting/Modules.Reporting.Application/GetOrdersReportSummary.cs`
- Modify: `src/Modules/Reporting/Modules.Reporting.Api/ReportingEndpoints.cs:72-120`
- Modify: `src/Bootstrapper/OrdersReportSource.cs` (filtro por `Source`, `BySource`, `WithoutClient` vacío)
- Test: `tests/Modules/Reporting/Modules.Reporting.UnitTests/ReportFilterParserTests.cs`, `OrdersReportSummaryHandlerTests.cs`, `ReportingTestDoubles.cs`
- Test: `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/OrdersReportSummaryApiTests.cs`, `OrdersReportApiTests.cs`, `ReportingResponses.cs`

**Interfaces:**
- Produces:
  - `public enum OrderSourceFilter { Quotation, Manual, Shopify, Pos }` (Reporting.Domain, en ese orden: es el de `bySource`).
  - `ReportFilterParser.TryParseSource(string?, out OrderSourceFilter)` y `ReportFilterParser.ParseSource(string?) → OrderSourceFilter?` (campo `"source"`).
  - `OrdersReportFilter(..., string? PaymentStatus, string? Source)` y `OrdersReportCriteria(..., OrderPaymentStatusFilter? PaymentStatus, OrderSourceFilter? Source)`: el campo nuevo va **al final** de los dos.
  - `public sealed record ReportSourceSliceDto(string Source, int Count, IReadOnlyList<ReportMoneyDto> Totals);`
  - `public sealed record ReportWithoutClientDto(int Count, IReadOnlyList<ReportMoneyDto> Totals);`
  - `OrdersReportAggregate` y `OrdersReportSummaryDto` ganan `IReadOnlyList<ReportSourceSliceDto> BySource` y `ReportWithoutClientDto WithoutClient`, después de `ByClient` (en el DTO, antes de `Previous`).
  - `OrdersReportItemDto.ClientId` es `Guid?`.

- [ ] **Step 1: Pruebas unitarias en rojo**

`ReportFilterParserTests`: `ParsesEveryOrderSource` (los cuatro nombres exactos), `RejectsAnUnknownOrLowercaseSource`
(`"pos"`, `"Store"` → `TryParseSource` falso; `ParseSource("pos")` lanza `ReportingDomainException` con
`reporting.filter.invalid`), `BlankSourceIsNoFilter` (`ParseSource("  ")` → `null`).

`OrdersReportSummaryHandlerTests`: `SummaryCarriesBySourceAndWithoutClientFromTheSource`. El doble devuelve un agregado
con `BySource` de cuatro entradas y `WithoutClient(2, [COP 50_000])`, y el DTO trae los mismos valores.
`InvalidSourceIsAValidationFailureOnSource`: con `Source = "Store"`, `ValidationException` con un error en `Source`.

- [ ] **Step 2: Correr y ver el rojo**

Run: `dotnet test tests/Modules/Reporting/Modules.Reporting.UnitTests --filter "FullyQualifiedName~ReportFilterParserTests|FullyQualifiedName~OrdersReportSummaryHandlerTests"`
Expected: no compila (`OrderSourceFilter`, `BySource` no existen).

- [ ] **Step 3: Contrato**

- Enum, parser y regla del validador (`"source must be one of Quotation, Manual, Shopify, Pos."`) siguiendo
  `PaymentStatus` palabra por palabra.
- `ToCriteria` agrega `ReportFilterParser.ParseSource(filter.Source)`.
- El doc-comment de `BySource` dice por qué vienen **las cuatro siempre**, incluso en cero y en orden fijo: un origen que
  desaparece obliga a la pantalla a conocer el enum (precedente `ReportStatusSliceDto`). El de `WithoutClient` dice por
  qué existe: las ventas POS a consumidor final no tienen ficha. Como entrada del ranking lo encabezarían, y en «Otros»
  el ranking no cerraría con menos de cinco clientes (`ReportRankFolding`).
  Por eso `ByClient` + `WithoutClient` = total.
- El doc-comment de `ClientId` dice que es nulo sólo en una venta POS a consumidor final.
- `GetOrdersReportSummaryHandler` copia `BySource` y `WithoutClient` de `current`. `Previous` no cambia.
- Endpoints: `string? source = null` en `ListOrdersAsync` y `GetOrdersSummaryAsync`, pasado al filtro.
- `OrdersReportSource`, todavía en EF:
  - `FilterOrders` filtra por `Source`: `Pos` → `Where(_ => false)`, los otros tres mapeados a `OrderSource` con un
    `switch` sin default, como `MapPaymentStatus`.
  - `BySource` agrupa los no cancelados por `Source` y rellena hasta los cuatro valores del enum, con `Pos` en cero.
  - `WithoutClient` = `new(0, [])`.
  - La rama de cero pedidos devuelve también las cuatro entradas en cero.
  - `ReportingTestDoubles` se ajusta al record nuevo.

- [ ] **Step 4: Unitarias en verde**

Run: el mismo comando del Step 2. Expected: PASS.

- [ ] **Step 5: Integración en rojo, después en verde**

En `OrdersReportSummaryApiTests`:
- `BySourceAlwaysCarriesTheFourSourcesInOrder`: un pedido de cotización y uno manual (`CreateManualOrderAsync`). Espera
  `bySource` = `[Quotation 1, Manual 1, Shopify 0 / [], Pos 0 / []]` con sus totales, y `withoutClient` = `(0, [])`.
- `SourceFilterNarrowsTheWholeSummary`: con `?source=Manual`, `orderCount == 1`, `totals` = el del manual y
  `bySource[0].Count == 0`.
- `UnknownSourceIs422WithTheField`: con `?source=pos`, `422`, `code = "validation.failed"` y `errors` con la clave
  `Source`.

En `OrdersReportApiTests`: `ListFiltersBySource` (con `?source=Quotation` sólo viene el de cotización).
`ReportingResponses.cs` refleja los tipos nuevos.

Run: `dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --filter "FullyQualifiedName~OrdersReportSummaryApiTests|FullyQualifiedName~OrdersReportApiTests"`
Expected: rojo antes del Step 3 si lo corres primero; después, PASS, incluidas las pruebas que ya existían.

- [ ] **Step 6: Commit**

```bash
git add src/Modules/Reporting src/Bootstrapper/OrdersReportSource.cs tests/Modules/Reporting
git commit -m "feat(reporting): RVP-01 filtro source, bySource y withoutClient en el reporte de pedidos"
```

---

### Task RVP-02: el listado lee la unión de pedidos y ventas POS

**Files:**
- Modify: `src/Bootstrapper/OrdersReportSource.cs` (`ListAsync` y la base SQL compartida)
- Modify: `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/ReportingApiHarness.cs`
- Create: `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/OrdersReportPosApiTests.cs`

**Interfaces:**
- Consumes: RVP-01 (`OrdersReportCriteria.Source`, `ClientId` nullable).
- Produces:
  - En `OrdersReportSource`, un método privado que arma la base de la unión ya filtrada:
    `(string Sql, NpgsqlParameter[] Parameters) ReportRowsSql(OrdersReportCriteria criteria)`. RVP-03 lo usa para cada
    agregado. Columnas de salida, con estos alias exactos: `id`, `number`, `source`, `quotation_id`, `occurred_at`,
    `advisor_id`, `client_id`, `status`, `payment_status`, `currency`, `subtotal`, `tax_amount`, `freight_subtotal`,
    `freight_tax_amount`, `total`, `is_void` (bool), `snapshot_client_name` (nulo en pedidos).
  - En el harness: `EnablePosAsync(PostgreSqlContainer, Guid tenantId)` (copia de `PosApiHarness.EnablePosAsync`),
    `PosPermissions` (`string[]`: `pos.sale.read`, `pos.sale.create`, `pos.sale.customer`, `pos.sale.void`,
    `pos.register.operate`, `pos.register.read`, más los de siembra de empresa y catálogo),
    `OpenPosSessionAsync(HttpClient, Guid tenantId) → Guid sessionId` (crea la empresa si hace falta, como
    `PosApiHarness.CreateCompanyAsync`, y abre con `openingFloat = 100_000m`),
    `CreatePosSaleAsync(HttpClient, Guid tenantId, Guid sessionId, Guid variantId, decimal unitPrice, Guid? customerId = null) → PosSaleResponse`
    (una línea de cantidad 1, sin descuento, `expectedTaxPercentage = 0`, pago `Cash` con `tendered = unitPrice`) y
    `VoidPosSaleAsync(HttpClient, Guid tenantId, Guid saleId)`.
    El `variantId` de un producto sin opciones es el id del producto (CLAUDE.md, «La variante por defecto lleva el id del
    producto»).

- [ ] **Step 1: Pruebas en rojo** (`OrdersReportPosApiTests`)

- `ListIncludesPosSalesWithTheirOwnShape`: un pedido de cotización y una venta POS a consumidor final de 30 000 COP. La
  fila POS trae:
  - `source = "Pos"` y `orderNumber` que empieza con `"POS-"`;
  - `quotationId` y `quotationNumber` nulos;
  - `clientId` nulo, `clientName = "Consumidor final"` y `clientCuc` nulo;
  - `status = "Completed"` y `paymentStatus = "FullPaymentReceived"`;
  - `freightSubtotal` y `freightTaxAmount` en 0, `subtotal + taxAmount == total == 30_000`;
  - `advisorId` = la membresía del dueño y `advisorName` = su correo.

  `total` del listado es 2.
- `PosSaleWithACustomerCarriesTheCustomersNameAndCuc`: venta con `customerId` de `CreateActiveCustomerAsync`. Espera
  `clientId`, `clientName` y `clientCuc` de la ficha.
- `VoidedPosSaleIsListedAsVoided`: anulada, sigue en el listado con `status = "Voided"`.
- `PosFiltersApplyToSales`. Con cada uno de estos query params, el listado trae o no la venta:

  | Query | ¿Trae la venta POS? |
  | --- | --- |
  | `source=Pos` | sólo la venta |
  | `source=Quotation` | no |
  | `paymentStatus=PaymentPending` | no |
  | `paymentStatus=FullPaymentReceived` | sí |
  | `clientId=<el del pedido>` | no |
  | `from`/`to` fuera del día de la venta | no |
- `AnAdvisorWithoutAllAdvisorsSeesOnlyTheirOwnPosSales`. El asesor invitado (`InviteActiveAdvisorAsync`) recibe
  `OwnAdvisorPermissions` más `PosPermissions` sin `pos.register.read`, y vende una vez. El dueño también vende una vez.
  El asesor pide `?advisorId=<membresía del dueño>` y ve sólo su venta.
- `PagesNeverRepeatOrSkipRowsAcrossBothSources`: tres pedidos y tres ventas. Con `pageSize=2`, las tres páginas juntas
  dan los seis ids exactos, sin repetir.

Run: `dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --filter "FullyQualifiedName~OrdersReportPosApiTests"`
Expected: FAIL. Las ventas no aparecen (`total` 1 donde se espera 2).

- [ ] **Step 2: Base SQL y listado**

`ReportRowsSql` arma un `UNION ALL` de dos ramas. **Cada rama lleva todos los filtros en su propio `WHERE`**, para que
`IX_sales_tenant_created` y el índice de pedidos sirvan:

- Rama pedidos: `FROM quotations.orders`, con `occurred_at = converted_at` e `is_void = (status = 'Cancelled')`. Si
  `criteria.Source` es `Pos`, la rama se omite. Si es otro origen, filtra `source = <nombre>`.
- Rama ventas: `FROM pos.sales`, con:
  - `source = 'Pos'`, `quotation_id = NULL::uuid` y `occurred_at = created_at`;
  - `advisor_id = cashier_id`, `client_id = customer_id` y `snapshot_client_name = customer_name`;
  - `payment_status = 'FullPaymentReceived'`, `freight_* = 0` e `is_void = (status = 'Voided')`.

  Se omite si `Source` no es nulo ni `Pos`, o si `PaymentStatus` no es nulo ni `FullPaymentReceived`.
- Filtros comunes: tenant, período (`>= Start`, `< EndExclusive`), `AdvisorId` y `ClientId`. Una rama omitida no
  aparece; si se omiten las dos, la base es un `SELECT` con `WHERE false` y las mismas columnas.
- El SQL se compone **sólo con fragmentos constantes**: ningún valor se interpola en el texto. Los valores van como
  `NpgsqlParameter` con `NpgsqlDbType` explícito (`Uuid`, `TimestampTz`, `Text`). Ver Review Focus 1.

`ListAsync`:
- Corre `SELECT … FROM (<base>) AS r ORDER BY occurred_at DESC, number, id LIMIT … OFFSET …`. El total sale de
  `count(*) OVER ()` o de una consulta aparte, como prefieras.
- Usa `quotations.Database.SqlQueryRaw<ReportRow>(sql, parameters)`, con `ReportRow` como record privado cuyos nombres
  de propiedad coinciden con los alias (precedente `PipelineConversationDirectory`).
- `ToDtosAsync` resuelve correos y clientes en lote como hoy, con estas diferencias:
  - las filas con `client_id` nulo usan `snapshot_client_name` y CUC `null`;
  - `quotationNumber` sólo se busca para las filas con `quotation_id`;
  - `status` y `paymentStatus` pasan el texto de la columna tal cual.

`SummarizeAsync` sigue en EF en esta tarea. RVP-03 lo pasa a la base.

Doc-comment de la clase: decir que lee `pos.sales` y `quotations.orders` por nombre, y qué prueba lo amarra.

- [ ] **Step 3: Verde, y las pruebas viejas del listado**

Run: `dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --filter "FullyQualifiedName~OrdersReportPosApiTests|FullyQualifiedName~OrdersReportApiTests|FullyQualifiedName~AdvisorScopeApiTests"`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Bootstrapper/OrdersReportSource.cs tests/Modules/Reporting/Modules.Reporting.IntegrationTests
git commit -m "feat(reporting): RVP-02 el listado de pedidos incluye las ventas POS"
```

---

### Task RVP-03: el resumen agrega la unión, y la documentación

**Files:**
- Modify: `src/Bootstrapper/OrdersReportSource.cs` (`SummarizeAsync`)
- Modify: `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/OrdersReportPosApiTests.cs`
- Modify: `README.md` (donde describe el reporte de pedidos; búscalo por `reports/orders/summary`), `CLAUDE.md` (Gotchas)

**Interfaces:**
- Consumes: `ReportRowsSql` (RVP-02), `ReportSourceSliceDto` y `ReportWithoutClientDto` (RVP-01), y
  `ReportRankFolding.FoldOthersAsync` y `ReportMoney.From`/`Remainder` sin cambios.

- [ ] **Step 1: Pruebas en rojo** (en `OrdersReportPosApiTests`)

- `SummaryAddsPosSalesEverywhere`: un pedido de cotización (total `T`) y una venta POS de 30 000 con cliente. Espera:
  - `orderCount == 2` y `totals = [COP T + 30_000]`;
  - `subtotals`, `taxAmounts`, `monthly` y `byAdvisor` (el dueño, `count 2`) suman las dos fuentes;
  - `bySource` = Quotation 1 y Pos 1, con Manual y Shopify en 0.
- `VoidedPosSaleDoesNotCount`: con una venta anulada, `orderCount`, `totals`, `bySource` (Pos 0), `byAdvisor` y
  `withoutClient` no la cuentan.
- `FinalConsumerSalesGoToWithoutClientNotToTheRanking`: dos clientes con pedido y dos ventas a consumidor final de
  10 000 y 20 000. Espera:
  - `byClient` con los dos clientes y sin entrada de `id` nulo;
  - `withoutClient = (2, [COP 30_000])`;
  - la suma de `byClient` y `withoutClient` igual a `totals`.

  Una segunda variante con seis clientes espera la fila «Otros» con `entityCount == 1` y sin las ventas de mostrador en
  su conteo ni en su total.
- `PosSaleOnNewYearsEveCountsInDecember`: con `QepApiFactory(…, utcNow: NewYearsEveInBogota)`, una venta POS cae en
  `monthly` con `year 2026`, `month 12`.
- `PreviousWindowIncludesPosSales`: con `from`/`to` de un rango y una venta en la ventana anterior (con el reloj fijo,
  como las pruebas de `Previous` que ya existen), `previous.orderCount` la cuenta.
- `SourcePosLeavesOnlySales`: con `?source=Pos`, `orderCount` = ventas y `bySource` con tres ceros.

Run: `dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --filter "FullyQualifiedName~OrdersReportPosApiTests"`
Expected: FAIL en las de resumen; las de RVP-02 siguen en verde.

- [ ] **Step 2: `SummarizeAsync` sobre la base**

Cada agregado es una consulta sobre `(<base>) AS r WHERE NOT r.is_void`, en la base:
- **Por moneda:** conteo, `subtotal`, `tax_amount`, `freight_*` y `total`.
- **Por mes:** `extract(year|month FROM r.occurred_at AT TIME ZONE @tz)` con el id IANA de `criteria.Period.TimeZone.Id`.
- **Por origen:** de ahí sale `BySource`, rellenado a los cuatro valores en el orden del enum.
- **Sin cliente** (`client_id IS NULL`): de ahí sale `WithoutClient`.
- **Top asesores y top clientes:** `ranking = sum(CASE WHEN currency = @defaultCurrency THEN total ELSE 0 END)`, ordenado
  por `ranking DESC, count DESC, id` con `LIMIT @rankSize`. El de clientes filtra `client_id IS NOT NULL`. Después van
  los totales por moneda de esos ids y el conteo de entidades distintas para el pliegue (en clientes, sin nulos).
- **«Otros» de clientes:** `FoldOthersAsync` recibe el conteo y los totales **sin** `WithoutClient`
  (`orderCount - withoutClient.Count` y `ReportMoney.Remainder(totals, [withoutClient.Totals])`).

Con cero filas, la salida temprana devuelve `BySource` con los cuatro en cero y `WithoutClient(0, [])`. Las etiquetas
siguen saliendo de `peopleLookup` y `clientLookup`. El LINQ de EF sobre `Order` que quede sin uso se borra.

- [ ] **Step 3: Verde, con todas las de Reporting que leen pedidos**

Run: `dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --filter "FullyQualifiedName~OrdersReport|FullyQualifiedName~AdvisorScopeApiTests"`
Expected: PASS.

Run: `dotnet test tests/Modules/Reporting/Modules.Reporting.UnitTests` y `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~Reporting"`
Expected: PASS.

- [ ] **Step 4: Documentación**

- **README:** en la sección del reporte de pedidos, decir que suma las ventas POS y documentar:
  - el filtro `source`, `bySource` y `withoutClient`;
  - que lo anulado no suma;
  - que el cajero entra como asesor.
- **CLAUDE.md**, una entrada nueva en Gotchas: «`OrdersReportSource` lee `quotations.orders` y `pos.sales` por nombre».
  Contenido:
  - es SQL crudo en Bootstrapper;
  - renombrar una columna de cualquiera de las dos tablas compila y deja `/reports/orders` respondiendo 500;
  - lo amarran `OrdersReportPosApiTests` y las pruebas de `OrdersReport*`;
  - un estado nuevo de `PosSaleStatus` o `OrderStatus` que no sea venta tiene que entrar en `is_void`.

- [ ] **Step 5: Commit**

```bash
git add src/Bootstrapper/OrdersReportSource.cs tests/Modules/Reporting README.md CLAUDE.md
git commit -m "feat(reporting): RVP-03 el resumen de pedidos suma las ventas POS"
```

---

## Contrato para el frontend

`GET /api/v1/tenants/{tenantId}/reports/orders/summary` (y `/reports/orders`) aceptan `source` ∈
`Quotation | Manual | Shopify | Pos`. Un valor inválido da `422 validation.failed` con `errors.Source`.

El resumen agrega dos campos, después de `byClient`:

```json
"bySource": [
  { "source": "Quotation", "count": 3, "totals": [{ "currency": "COP", "amount": 900000 }] },
  { "source": "Manual",    "count": 0, "totals": [] },
  { "source": "Shopify",   "count": 1, "totals": [{ "currency": "COP", "amount": 120000 }] },
  { "source": "Pos",       "count": 5, "totals": [{ "currency": "COP", "amount": 150000 }] }
],
"withoutClient": { "count": 2, "totals": [{ "currency": "COP", "amount": 30000 }] }
```

- `bySource` trae siempre las cuatro entradas, en ese orden.
- `withoutClient` cuenta las ventas POS a consumidor final. **No** están en `byClient` ni en su fila «Otros»:
  `byClient` + `withoutClient` = `totals`.
- En el listado, `clientId` puede ser `null` (venta a consumidor final), `source` puede ser `Pos` y `status` de una venta
  es `Completed` o `Voided`.

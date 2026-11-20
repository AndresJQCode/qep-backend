# Lista de precios: mostrar u ocultar precios al distribuidor — plan de implementación (backend)

> **Para quien ejecute:** SUB-SKILL REQUERIDA: `superpowers:executing-plans` o `superpowers:subagent-driven-development`.
> Casillas `- [ ]`. **Presupuesto de pruebas** (CLAUDE.md de la raíz, manda sobre el skill): bloques con **un implementador** cada
> uno, **una revisión por bloque**, un commit por tarea. El RED de compilación no levanta contenedores (evidencia: salida del
> build). Mientras se itera, la integración se filtra por nombre de prueba (`--filter "FullyQualifiedName~Nombre"`); la clase
> entera corre una vez al cierre del bloque. Antes de cada corrida de integración:
> `Get-Process testhost, dotnet -ErrorAction SilentlyContinue` — si hay una, se espera; **no se detiene** (es de otra sesión).
> `$env:Seed__Enabled = "false"`. Pruebas en primer plano. Nada de correr la solución entera.

**Meta:** `PriceList.ShowPrices`; con `false`, el distribuidor no recibe montos en catálogo ni vista previa, y su orden vinculada
**se guarda sin montos** hasta que el mayorista la aprueba.

**Spec:** [`2026-11-19-mayorista-mostrar-precios-design.md`](../specs/2026-11-19-mayorista-mostrar-precios-design.md) (D-1…D-3,
D-7…D-14). La revisión anterior de las tareas 4-6 (`prices_hidden`, enmascarar en el DTO hasta un paso `Confirmed`) queda
**reemplazada** por la de abajo.

**Rama:** `feature/wholesale-show-prices` (worktree `qep-backend-worktrees/wholesale-show-prices`). Mergear a `develop` entera
(nunca la primera tanda sola, nunca a `main`); `docs/estado.md` se actualiza en `develop` al mergear.

## Restricciones

- Expand: un solo `AlterColumn` (`unit_cost` nullable). Sin columna nueva, sin backfill.
- El flag cruza módulos por los records existentes como **miembro final con default** (`bool ShowPrices = true`): los llamadores
  existentes compilan.
- **Un solo punto descarta montos: el dominio** (`withPrices` en `Create`, `Replace`, `SendToWholesaler`). Ni el adaptador ni el
  DTO enmascaran: guardan o muestran lo que la orden tiene.
- Cada DTO que cambia lleva en su `<summary>` el motivo: «null en una orden vinculada que todavía no tiene precio (spec
  2026-11-19)».

## Bloque A — la lista, el catálogo y la vista previa (PRIMERA TANDA, HECHO)

Tareas 1-3 commiteadas en la rama (`f535e69f8` … `78b240afc`): `PriceList.ShowPrices` + migración `AddPriceListShowPrices`,
contrato de la lista, `WholesaleTerms.ShowPrices`, catálogo y vista previa enmascarados con el flag vivo, regresión del PDF del
pedido del mayorista. Las pruebas `CreateWithAHiddenPriceListStillStoresTheCost` y `UpdateWithAHiddenPriceListStillStoresTheCost`
(D-4) ya se quitaron.

**Pendiente de la primera tanda:** las corridas de integración de cierre no se ejecutaron (había un `testhost` de otra sesión):
`PriceListApiTests` (Wholesale), `LinkedPurchaseOrderApiTests` y `PurchaseOrderPdfApiTests` (Purchases). Se corren en el cierre del
Bloque C, en fila.

## Bloque B — la orden nace sin precio (SEGUNDA TANDA; tareas 4 y 5, sólo unitarias)

Un implementador. Las dos tareas tocan los mismos archivos en cadena (dominio → aplicación), por eso van juntas.

### Tarea 4: el dominio y la base admiten una línea sin precio

**Archivos:**
- `src/Modules/Purchases/Modules.Purchases.Domain/PurchaseOrderLine.cs` — `UnitCost` → `decimal?`; `PurchaseOrderLineDraft`
  sin cambio de forma (sigue trayendo el costo calculado: la vista previa lo usa); `From(draft, bool withPrices)` y
  `From(snapshot, bool withPrices)`: en `false`, `UnitCost`, `ListBasePrice`, `ListDiscountPercent`, `ListUnitPrice` en `null`
  (se conservan `SupplierVariantId`, código, nombre, cantidad, tasa); `RequestedSubtotal`, `Subtotal`, `Tax`, `RequestedTax` →
  `decimal?` con las reglas del spec («Modelo de datos»); `Status` según D-12; `Confirm(decimal, decimal?)`.
- `…Domain/PurchaseOrder.cs` — `Create(..., bool withPrices = true)`, `Replace(..., bool withPrices = true)`,
  `SendToWholesaler(..., bool withPrices = true)`; `withPrices: false` en una externa → `ArgumentException`; `Totals()` y
  `RequestedTotals()` → `PurchaseOrderTotals?`; `HasPrices` (D-11); `ApplyWholesalerApproval` sin cambio de lógica (la línea
  quitada se confirma con `(0, UnitCost)`).
- `…Infrastructure/Persistence/PurchasesDbContext.cs` — `unit_cost` deja de ser requerido.
- Migración `AllowPricelessPurchaseOrderLines`:
  `dotnet ef migrations add AllowPricelessPurchaseOrderLines --project src/Modules/Purchases/Modules.Purchases.Infrastructure --startup-project src/Api`.
  Revisar que el `Up` sea **un solo** `AlterColumn` de `unit_cost` a `nullable: true` (si aparece algo más, el modelo derivó: parar).
- Pruebas: `tests/Modules/Purchases/Modules.Purchases.UnitTests/PurchaseOrderTests.cs`, `LinkedPurchaseOrderTests.cs`,
  `PurchaseOrderTotalsTests.cs`; ajuste de compilación en `PurchaseOrderTestData.cs`.

- [ ] RED (evidencia: build que no compila): vinculada creada con `withPrices: false` → líneas sin `UnitCost` ni `List*`,
      `Totals()` y `RequestedTotals()` nulos, `HasPrices` falso; `Replace` con `true` sobre un borrador sin precio lo pone con
      precio, y con `false` sobre uno con precio se los quita (casos borde 2 y 3); `SendToWholesaler(..., withPrices: false)`
      adopta las líneas del pedido sin montos y con `true` como hoy (caso 1); externa con `withPrices: false` → `ArgumentException`;
      aprobación de una orden sin precio → `HasPrices` verdadero, totales con lo confirmado, `RequestedTotals()` nulo, línea con la
      misma cantidad `Unchanged`, con otra `Changed`, quitada `Rejected` con subtotal 0, agregada `Added`; cancelada sin aprobar →
      sigue sin precio; una orden externa da los mismos totales que hoy (las pruebas existentes de `PurchaseOrderTotalsTests` siguen
      pasando con los tipos nuevos).
- [ ] GREEN + migración.
- [ ] `dotnet test tests/Modules/Purchases/Modules.Purchases.UnitTests --filter "FullyQualifiedName~PurchaseOrderTests|FullyQualifiedName~LinkedPurchaseOrderTests|FullyQualifiedName~PurchaseOrderTotalsTests"`
- [ ] Commit: `feat(purchases): a linked order line can be saved without a price`

### Tarea 5: alta, edición, envío y lecturas sin precio

**Archivos:**
- `…Purchases.Application/CreatePurchaseOrder.cs`, `UpdatePurchaseOrder.cs` — pasan `withPrices: build.ShowPrices` (el
  `PurchaseOrderLineBuild.ShowPrices` de la primera tanda; externo siempre `true`).
- `…Purchases.Application/WholesalePorts.cs` — `WholesaleIntakeResult` + `bool ShowPrices = true` al final.
- `…Purchases.Application/TransitionPurchaseOrder.cs` — `order.SendToWholesaler(..., withPrices: submitted.ShowPrices)` (hoy
  línea 85: es **el punto exacto** donde la orden adopta el costo del pedido).
- `src/Bootstrapper/PurchasesWholesaleOrderIntake.cs` — `ToIntake(order, offers, showPrices)`: camino nuevo con
  `terms.ShowPrices`; camino D-8 (`existing`) con el flag de `OffersForOrderAsync` (D-14). Las fotos de línea siguen trayendo el
  precio (es la verdad del mayorista); quien las descarta es el dominio.
- `src/Bootstrapper/WholesaleIntakeDirectory.cs` y `…Wholesale.Application/WholesaleOffers.cs` — `OffersForOrderAsync` devuelve
  `WholesaleOrderOffers(IReadOnlyDictionary<Guid, WholesaleOffer> Offers, bool ShowPrices)`; sin relación o sin lista, vacío y
  `ShowPrices: false`. `ListPricesForOrderAsync` usa `.Offers` (su contrato no cambia).
- `…Purchases.Application/PurchasesDtos.cs` — `PurchaseOrderLineDto.UnitCost` y `Subtotal` → `decimal?`;
  `PurchaseOrderDetailDto.Totals` → `PurchaseOrderTotalsDto?`, `RequestedTotal` → `decimal?`, `bool ShowPrices = true` al final;
  `PurchaseOrderSummaryDto.Total` → `decimal?`, `bool ShowPrices = true` al final. Vista previa: sin cambio.
- `…Purchases.Application/PurchaseOrderComposer.cs` — `Totals()`/`RequestedTotals()` nulos se mapean a `null`;
  `ShowPrices = order.HasPrices` en detalle y resumen. Nada se enmascara: lo que no está, no está.
- `…Purchases.Application/PurchaseOrderPdfMapper.cs` — `Money(decimal?)` → «—» con `null` (costo, subtotal, totales). El correo
  (`PurchaseOrderEmailSender`, `PurchaseOrderEmailTemplate`) no cambia: toma los textos del documento.
- `…Purchases.Application/PreviewPurchaseOrder.cs` — sin cambio de comportamiento; sólo compila con `UnitCost` del borrador, que
  sigue siendo `decimal`.
- `…Purchases.Application/WholesaleOrderEventApplier.cs` — sin cambio de lógica (escribe confirmados como hoy, D-10); D-18 lee
  `ConfirmedUnitCost`, que la aprobación siempre trae.

**Pruebas que cambian** (unitarias): `LinkedPurchaseOrderHandlersTests.cs`, `PurchaseOrderLineBuilderTests.cs`,
`PurchaseOrderComposerTests.cs`, `PurchaseOrderPdfMapperTests.cs`, `WholesaleOrderEventApplierTests.cs`, el intake falso de
`LinkedPurchasesFakes.cs` (`ShowPrices`), `tests/Bootstrapper/Bootstrapper.UnitTests/PurchasesWholesaleOrderIntakeTests.cs`,
`tests/Modules/Wholesale/Modules.Wholesale.UnitTests/WholesaleOffersTests.cs`; ajustes de compilación (`decimal?` en tuplas y
`Totals!`) donde aparezcan. Las tres de la vista previa de la primera tanda (`PreviewOfAHiddenPriceListHasNoAmounts`,
`PreviewOfAVisiblePriceListKeepsItsAmounts`, `PreviewOfAnExternalSupplierIsNotMasked`) **no cambian**.

- [ ] RED (build): alta y edición con lista oculta guardan líneas sin `UnitCost` y responden `showPrices: false`, `totals: null`,
      `requestedTotal: null`, `lines[].unitCost/subtotal/listPrice: null`; con lista visible, como hoy; envío con intake
      `ShowPrices: false` adopta sin montos aunque el borrador tuviera precio; con `true` sobre un borrador sin precio, adopta
      montos; resumen: `total: null`, `showPrices: false`; aprobación (applier) de una orden sin precio → detalle con
      `showPrices: true`, costos confirmados, `requestedTotal: null`, y el producto creado con ese costo (D-18); PDF de una orden sin
      precio → «—» en costo, subtotal, subtotal general, IVA y total, y cantidades como hoy; intake: camino nuevo con
      `terms.ShowPrices`, camino D-8 con el flag de `OffersForOrderAsync`, sin lista `false`; `OffersForOrderAsync` devuelve el
      `ShowPrices` de la lista.
- [ ] GREEN.
- [ ] `dotnet test tests/Modules/Purchases/Modules.Purchases.UnitTests` (el proyecto unitario entero: es rápido),
      `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~PurchasesWholesaleOrderIntakeTests"`,
      `dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests --filter "FullyQualifiedName~WholesaleOffersTests"`.
- [ ] Commit: `feat(purchases): a linked order from a hidden price list gets its prices on approval`
- [ ] **Cierre del bloque:** `dotnet build` de la solución (compila los proyectos de integración que leen los DTOs) y
      `dotnet test tests/ArchitectureTests`. Revisión del bloque (foco: ningún monto del mayorista guardado ni respondido con la
      lista oculta antes de aprobar; orden externa idéntica a hoy).

## Bloque C — integración (tarea 6)

### Tarea 6: de punta a punta, y las corridas pendientes

**Archivo:** `tests/Modules/Purchases/Modules.Purchases.IntegrationTests/Linked/LinkedPurchaseOrderApiTests.cs` (ajustes de
compilación en `LinkedPurchasesHarness.cs`, `PurchasesApiHarness.cs`, `PurchaseOrderWriteApiTests.cs`,
`PurchaseOrderListApiTests.cs`, `PurchaseOrderTransitionApiTests.cs`, `PurchaseOrderPdfApiTests.cs` si leen montos como `decimal`).

- [ ] Prueba `HiddenPriceListOrderGetsPricesOnApproval`: lista oculta → `POST` crea y el detalle no trae montos
      (`showPrices: false`) → la fila de `GET /purchases/orders` trae `total: null` → en la base, `unit_cost` es `NULL` → enviar →
      el detalle sigue sin montos → aplicar el evento de aprobación (como `WholesaleOrderEventsTests`) → el detalle trae costos
      confirmados, totales y `showPrices: true`.
- [ ] Prueba `DraftSavedVisibleLosesPricesWhenSentWithHiddenList` (caso borde 1): borrador con lista visible → ocultar la
      lista → enviar → detalle sin montos.
- [ ] `dotnet test tests/Modules/Purchases/Modules.Purchases.IntegrationTests --filter "FullyQualifiedName~HiddenPriceListOrderGetsPricesOnApproval|FullyQualifiedName~DraftSavedVisibleLosesPricesWhenSentWithHiddenList" -- xUnit.MaxParallelThreads=4`
- [ ] **Cierre del bloque (en fila, una tras otra, mirando antes `testhost`):**
      `dotnet test tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests --filter "FullyQualifiedName~PriceListApiTests" -- xUnit.MaxParallelThreads=4`,
      luego `--filter "FullyQualifiedName~LinkedPurchaseOrderApiTests"`, luego `--filter "FullyQualifiedName~PurchaseOrderPdfApiTests"` y
      luego `--filter "FullyQualifiedName~WholesaleOrderEventsTests"` sobre `Modules.Purchases.IntegrationTests`.
- [ ] Commit: `test(purchases): hidden wholesaler prices end to end`

## Antes de mergear a `develop`

Una sola vez, en fila: `Modules.Wholesale.UnitTests`, `Modules.Purchases.UnitTests`, `Bootstrapper.UnitTests`,
`Modules.Wholesale.IntegrationTests`, `Modules.Purchases.IntegrationTests` (cada uno con `-- xUnit.MaxParallelThreads=4`) y
`tests/ArchitectureTests`. Hay una migración de un solo módulo: no es transversal, no se corre la solución. Revisión fuerte de la
rama al final (foco: con la lista oculta, ninguna respuesta ni fila del distribuidor trae un monto antes de aprobar; `PUT` sin
`showPrices` no muestra una lista oculta; externa idéntica). Despliegue: backend con su migración y enseguida el frontend;
rollback sólo hacia adelante una vez que alguna lista se ocultó (spec, «Compatibilidad»).

# Lista de precios: mostrar u ocultar precios al distribuidor — plan de implementación (backend)

> **Para quien ejecute:** SUB-SKILL REQUERIDA: `superpowers:executing-plans` o `superpowers:subagent-driven-development`.
> Casillas `- [ ]`. **Presupuesto de pruebas** (CLAUDE.md de la raíz): **2 bloques** (A: tareas 1-3, B: tareas 4-6), un
> implementador por bloque, un commit por tarea. El RED de compilación no levanta contenedores. La integración se filtra por
> nombre de prueba (`--filter "FullyQualifiedName~Nombre"`) y la clase entera corre una vez al final del bloque. Antes de cada
> corrida de integración: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue` (si hay una, se espera; no se detiene).
> `$env:Seed__Enabled = "false"`. Pruebas en primer plano. Nada de correr la solución entera.

> **Alcance reducido (owner, 2026-11-19).** Primera tanda: **Bloque A** + la **vista previa** enmascarada (calculada con el flag vivo de la lista
> al consultar, sin flag guardado). **Segunda tanda, no se construye ahora:** el Bloque B entero (tareas 4, 5 y 6: prices_hidden, detalle,
> lista, PDF, correo y la regla «los precios aparecen al aprobar»). Hasta entonces una orden guardada sigue mostrando sus montos.
> La vista previa se agrega a la **Tarea 3** (ver abajo). Regresión: con la lista oculta, el PDF del pedido del mayorista sigue trayendo
> montos (ver nota en la Tarea 3).

**Meta:** `PriceList.ShowPrices`; con `false`, el distribuidor de esa lista no recibe montos en catálogo, vista previa, detalle,
lista, PDF ni correo de una orden vinculada hasta que el mayorista la aprueba.

**Spec:** [`2026-11-19-mayorista-mostrar-precios-design.md`](../specs/2026-11-19-mayorista-mostrar-precios-design.md) (D-1…D-7).

**Rama:** `feature/wholesale-show-prices` (worktree `qep-backend-worktrees/wholesale-show-prices`). Mergear a `develop`, nunca a
`main`; `docs/estado.md` se actualiza en `develop` al mergear.

## Restricciones

- Expand puro: columnas con default; sin backfill ni contract.
- El flag cruza módulos por los records existentes como **miembro final con default** (`bool ShowPrices = true` /
  `bool PricesHidden = false`), igual que `ProductId`/`ImageUrl` en el spec de foto: los llamadores existentes compilan.
- Los montos se ponen `null` en el borde de Compras (DTO), nunca en Wholesale: la orden necesita el costo para guardarse (D-4).
- Cada DTO que cambia lleva en su `<summary>` el motivo (BFF): «null con la lista oculta del mayorista (spec 2026-11-19)».

## Bloque A — la lista y el catálogo

### Tarea 1: `PriceList.ShowPrices` en el dominio y la base

**Archivos:** `src/Modules/Wholesale/Modules.Wholesale.Domain/PriceList.cs`,
`…Infrastructure/Persistence/WholesaleDbContext.cs`, migración nueva `AddPriceListShowPrices` en
`…Infrastructure/Persistence/Migrations/`; pruebas `tests/Modules/Wholesale/Modules.Wholesale.UnitTests/PriceListTests.cs` y
`WholesaleDbContextMappingTests.cs`.

- [ ] RED: `Create` sin indicar nada deja `ShowPrices == true`; `Create(..., showPrices: false)` lo guarda; `Update(...,
      showPrices: false)` lo cambia y sube `Version`; el mapeo tiene columna `show_prices` no nula con default `true`. Evidencia:
      salida del build (no compila).
- [ ] GREEN: propiedad `ShowPrices`, parámetro `bool showPrices = true` en `Create` y `bool showPrices` en `Update`; columna
      `show_prices boolean not null default true`. Migración:
      `dotnet ef migrations add AddPriceListShowPrices --project src/Modules/Wholesale/Modules.Wholesale.Infrastructure --startup-project src/Api`.
      Revisar que el `Up` sea un solo `AddColumn` con `defaultValue: true`.
- [ ] `dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests --filter "FullyQualifiedName~PriceListTests|FullyQualifiedName~WholesaleDbContextMappingTests"`
- [ ] Commit: `feat(wholesale): price list can hide prices from its distributors`

### Tarea 2: contrato de la lista (crear, editar, leer)

**Archivos:** `…Api/WholesaleEndpoints.cs` (`PriceListWriteRequest` + `bool? ShowPrices = null`), `…Application/CreatePriceList.cs`,
`UpdatePriceList.cs` (`bool? ShowPrices` en el comando), `PriceListDtos.cs` (`ShowPrices` en `PriceListSummaryDto` y
`PriceListDetailDto` y en `PriceListMapping`), `CreatePriceListHandler.WrittenFields` += `"showPrices"`; pruebas
`PriceListHandlersTests.cs` y, en integración, `PriceListApiTests.cs`.

- [ ] RED (unitaria): alta sin `ShowPrices` → `true`; alta con `false` → `false`; edición con `null` **conserva** `false` (D-2);
      edición con `true` lo cambia; la auditoría lista `showPrices`.
- [ ] RED (integración, una prueba nueva `Put_without_showPrices_keeps_hidden_list`): `POST` con `showPrices: false`, `PUT` sin el
      campo con `If-Match`, `GET` responde `showPrices: false`.
- [ ] GREEN. `ShowPrices` **no** entra en `IPriceListTermsCommand` (la vista previa no lo usa).
- [ ] `dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests --filter "FullyQualifiedName~PriceListHandlersTests"` y
      `dotnet test tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests --filter "FullyQualifiedName~Put_without_showPrices_keeps_hidden_list" -- xUnit.MaxParallelThreads=4`
- [ ] Commit: `feat(wholesale): read and write showPrices on price lists`

### Tarea 3: el catálogo del mayorista enmascara

**Archivos:** `…Wholesale.Application/WholesaleOffers.cs` (`WholesaleTerms.ShowPrices = true` desde `list.ShowPrices`;
`WholesaleOfferPage.ShowPrices = true`), `src/Bootstrapper/PurchasesWholesaleCatalog.cs`,
`…Purchases.Application/WholesalePorts.cs` (`WholesaleCatalogPage.ShowPrices = true`; `PriceAsync` devuelve además
`bool ShowPrices`), `…Purchases.Application/ListSupplierCatalog.cs` (`SupplierCatalogItemDto` con `decimal?` en
`BasePrice`, `DiscountPercent`, `ListPrice`, `UnitCost`; `SupplierCatalogPriceListDto.DiscountPercent` `decimal?`;
`SupplierCatalogPageDto.ShowPrices`). Pruebas: `WholesaleOffersTests.cs`, `SupplierCatalogTests.cs`; integración
`LinkedPurchaseOrderApiTests.cs`.

- [ ] RED: `ResolveTermsAsync` refleja `ShowPrices` de la lista; con `false`, el handler del catálogo devuelve los cuatro montos y
      `priceList.discountPercent` en `null`, `showPrices: false`, y conserva código, nombres, foto, tasa y `total`; con `true`, igual
      que hoy y `showPrices: true`. Ajustar los fakes de `IWholesaleCatalog` a la tupla nueva.
- [ ] RED (integración, `Catalog_of_hidden_price_list_has_no_prices`): mayorista con lista oculta → el JSON no trae ningún número en
      los campos de precio.
- [ ] GREEN.
- [ ] Unitarias de los dos proyectos filtradas por `WholesaleOffersTests|SupplierCatalogTests`; integración por el nombre de la
      prueba nueva. **Cierre del bloque:** `PriceListApiTests` entera y `ArchitectureTests`.
- [ ] **Primera tanda, vista previa:** `PreviewPurchaseOrder` / `PurchaseOrderPreviewDto` (`Subtotal` y `ListPrice` de línea y `Totals` a `decimal?`/`?`, `ShowPrices = true` al final) se enmascaran con el `ShowPrices` de los términos del mayorista **al momento de la vista previa** (flag vivo). Con externo, igual que hoy.
- [ ] Regresión: si ya existe una prueba del PDF del pedido del mayorista, extenderla con la lista oculta (el PDF sigue con montos); si no, queda como prueba de seguimiento.
- [ ] Commit: `feat(purchases): hide wholesaler prices in the supplier catalog when the list says so`

## Bloque B — la orden vinculada (SEGUNDA TANDA, no se construye en la primera)

### Tarea 4: la orden congela el flag

**Archivos:** `…Purchases.Domain/PurchaseOrder.cs` (`PricesHidden`, `ShowsPrices => !PricesHidden || History.Any(e => e.Status ==
PurchaseOrderStatus.Confirmed)`; `Create` y `Replace` reciben `bool pricesHidden`, que sólo puede ser `true` con mayorista),
`…Application/PurchaseOrderLineBuilder.cs` (`PurchaseOrderLineBuild.PricesHidden = !showPrices` en la rama vinculada, `false` en la
externa), `CreatePurchaseOrder.cs`, `UpdatePurchaseOrder.cs`, `…Infrastructure/Persistence/PurchasesDbContext.cs`, migración
`AddPurchaseOrderPricesHidden`. Pruebas: `PurchaseOrderTests.cs`, `LinkedPurchaseOrderTests.cs`, `PurchaseOrderLineBuilderTests.cs`.

- [ ] RED: build vinculado con lista oculta → `PricesHidden`; externo → nunca; `ShowsPrices` falso en `Draft` y `Sent`, verdadero
      tras `Confirm`/aprobación, falso en una anulada sin aprobar; `Replace` con el flag nuevo lo cambia (D-5). Evidencia: build.
- [ ] GREEN + migración (`--project src/Modules/Purchases/Modules.Purchases.Infrastructure --startup-project src/Api`),
      `prices_hidden boolean not null default false`.
- [ ] Unitarias filtradas por las tres clases.
- [ ] Commit: `feat(purchases): a linked order remembers whether its prices are hidden`

### Tarea 5: vista previa, detalle, lista y PDF enmascaran

**Archivos:** `…Application/PurchasesDtos.cs` (`PurchaseOrderLineDto.UnitCost`, `Subtotal` → `decimal?`;
`PurchaseOrderDetailDto.Totals` → `PurchaseOrderTotalsDto?`, `RequestedTotal` → `decimal?`, `ShowPrices = true` al final;
`PurchaseOrderSummaryDto.Total` → `decimal?`, `ShowPrices = true`; `PurchaseOrderPreviewLineDto.Subtotal` → `decimal?`;
`PurchaseOrderPreviewDto.Totals` → `PurchaseOrderTotalsDto?`, `ShowPrices = true`), `PreviewPurchaseOrder.cs`,
`PurchaseOrderComposer.cs` (un solo punto: `order.ShowsPrices`), `PurchaseOrderPdfMapper.cs` (monto `null` → «—»). Pruebas:
`LinkedPurchaseOrderHandlersTests.cs`, `PurchaseOrderComposerTests.cs`, `PurchaseOrderPdfMapperTests.cs`.

- [ ] RED: vista previa con lista oculta → subtotales, `listPrice` y `totals` en `null`, `showPrices: false`; detalle y resumen de
      una orden oculta sin aprobar → montos `null` y `showPrices: false`; aprobada → montos y `showPrices: true`; externa → igual que
      hoy; el PDF de una oculta imprime «—» en costo, subtotal y totales (y el correo, que toma los textos del documento).
- [ ] GREEN. Los totales se siguen calculando para guardar; sólo el DTO los omite.
- [ ] Unitarias filtradas por las tres clases.
- [ ] Commit: `feat(purchases): mask linked order amounts until the wholesaler approves`

### Tarea 6: integración de punta a punta

**Archivo:** `tests/Modules/Purchases/Modules.Purchases.IntegrationTests/Linked/LinkedPurchaseOrderApiTests.cs`.

- [ ] Prueba `Hidden_price_list_masks_order_until_approval`: lista oculta → `preview` sin montos → `POST` crea y el detalle no trae
      montos → `GET /purchases/orders` fila con `total: null` → enviar → aplicar el evento de aprobación (como
      `WholesaleOrderEventsTests`) → el detalle trae costos confirmados y `showPrices: true`.
- [ ] `dotnet test tests/Modules/Purchases/Modules.Purchases.IntegrationTests --filter "FullyQualifiedName~Hidden_price_list_masks_order_until_approval" -- xUnit.MaxParallelThreads=4`
- [ ] **Cierre del bloque:** `LinkedPurchaseOrderApiTests` y `PurchaseOrderPdfApiTests` enteras (una tras otra).
- [ ] Commit: `test(purchases): hidden wholesaler prices end to end`

## Antes de mergear a `develop`

Una sola vez, en fila: `Modules.Wholesale.UnitTests`, `Modules.Purchases.UnitTests`, `Modules.Wholesale.IntegrationTests`,
`Modules.Purchases.IntegrationTests` (cada uno con `-- xUnit.MaxParallelThreads=4`) y `tests/ArchitectureTests`. No es un cambio
transversal: no se corre la solución. Revisión fuerte de la rama al final (foco: ningún monto del mayorista en una respuesta del
distribuidor con la lista oculta; `PUT` sin `showPrices` no muestra una lista oculta). Despliegue: backend primero, frontend
enseguida.

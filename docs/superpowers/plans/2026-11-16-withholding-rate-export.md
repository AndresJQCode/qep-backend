# Tarifa de retención en el Excel de pedidos — plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Dos columnas nuevas del Excel de pedidos con la tarifa de Retefuente (%) y de ReteICA (‰), para el import de Siigo.

**Architecture:** Dos llaves nuevas al final de `OrdersExportColumnCatalog`, ocultas por defecto, y sus dos celdas al final
de la fila de `OrdersExportProcessor` (el orden de la fila es el del catálogo). El tenant las prende y renombra en su layout.

**Tech Stack:** .NET 10, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-16-withholding-rate-export-design.md`

## Global Constraints

- Llaves `withholding_retefuente_rate` («Tarifa Retefuente», 16) y `withholding_reteica_rate` («Tarifa ReteICA», 16), `DefaultVisible: false`, después de `buyer_notes`.
- Retefuente = `Percentage`; ReteICA = `Percentage × 10`. `Percentage` efectivo, nunca `CatalogPercentage`.
- Sin retención aplicada del tipo: `ExportCell.OfNumber(0m)`.
- Varias aplicadas del mismo tipo: la de menor `Position` (`AppliedWithholdings` ya viene ordenado).
- No se tocan `withholding_retefuente`, `withholding_reteica` ni `retention_amount`.
- Worktree `qep-backend-worktrees/withholding-rate-export`, rama `feature/withholding-rate-export`.
- Commits convencionales sin atribución de IA (sin `Co-Authored-By`).
- Pruebas: las corre el owner al final; el implementador verifica con `dotnet build` (memoria «No correr pruebas»).

## Review Focus

1. Tarifa editada por el asesor (Percentage ≠ CatalogPercentage) → sale la editada. Prueba en Task 1.
2. Una tasa que no aplicó (bajo la base mínima) del tipo → `0`, no su tarifa. Prueba en Task 1.
3. Dos aplicadas del mismo tipo → la de menor `Position`. Prueba en Task 1.
4. Layout guardado de un tenant (también la hoja sembrada «MIGRACION 1») → no gana columnas visibles. Lo cubren las pruebas de conteo y de ocultas de Task 1.
5. Precisión: 1.104 × 10 = 11.04 exacto en `decimal`, sin redondeo. Prueba en Task 1.

---

### Task 1: Columnas de tarifa de retención

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/OrdersExportColumnCatalog.cs` (después de `buyer_notes`, con comentario que cite el spec)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs` (dos celdas tras `BuyerNotes`, ~l. 385; helper junto a `WithholdingCell`, ~l. 391)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportColumnCatalogTests.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportProcessorTests.cs`
- Test (conteos 84→86 y 85→87, listas de ocultas): `GetOrdersExportLayoutHandlerTests.cs`, `OrdersExportLayoutTests.cs`, `UpdateOrdersExportLayoutHandlerTests.cs`, `OrdersExportLayoutEffectiveTests.cs:147` (unitarias); `OrdersExportLayoutApiTests.cs`, `OrdersExportLayoutPersistenceTests.cs`, `QuotationsSeedTests.cs:134` (integración)

**Interfaces:**
- Produces: `private static ExportCell WithholdingRateCell(Order order, string type, decimal scale)` — `Percentage × scale` de la primera aplicada de ese tipo, o `0`.

- [ ] **Step 1: Pruebas en rojo**

`OrdersExportColumnCatalogTests`:
- `Keys`/`Headers` terminan en `"buyer_notes", "withholding_retefuente_rate", "withholding_reteica_rate"` / `"Notas del cliente", "Tarifa Retefuente", "Tarifa ReteICA"`; renombrar `HasTheEightyFourColumnsOfTheSpecInItsOrder` → `HasTheEightySixColumnsOfTheSpecInItsOrder` con `86`.
- La lista de ocultas (l. 261-269) suma las dos llaves al final.
- `NewColumnsAreHiddenByDefaultAndLast`: Retefuente/ReteICA en `count - 5` / `count - 4`.
- `BuyerNotesIsTheLastColumnAndHidden` → `BuyerNotesIsHiddenAfterTheWithholdingAmounts`: `Columns[^3]`.
- Nueva `WithholdingRateColumnsAreLastAndHidden`: `Columns[^2] == new("withholding_retefuente_rate", "Tarifa Retefuente", 16, DefaultVisible: false)`, `Columns[^1] == new("withholding_reteica_rate", "Tarifa ReteICA", 16, DefaultVisible: false)`.

`OrdersExportProcessorTests`, nueva `ExportHasRatePerWithholdingType` (mismo armado que `ExportHasAmountPerWithholdingType`, l. 1908), layout `order_number`, `withholding_retefuente_rate` como «Retefuente», `withholding_reteica_rate` como «ReteICA», todas visibles:
- Pedido con `TestWithholdings.RetefuenteCo` y una ReteICA de `1.104m` → `[1] == ExportCell.OfNumber(<Percentage de RetefuenteCo>)`, `[2] == ExportCell.OfNumber(11.04m)`.
- Pedido sin retenciones → `0m` en las dos.
- Pedido con una ReteICA que no aplica (base mínima por encima del subtotal) → `[2] == ExportCell.OfNumber(0m)`.

Nueva `RateUsesTheFirstAppliedWithholdingOfTheType`: dos ReteICA aplicadas (`1.104m` y `0.966m`, en ese orden) → `11.04m`.

Nueva `RateUsesTheOverriddenPercentage`: pedido cuya Retefuente se editó a `3.5m` (con el método del dominio que use `OrderWithholdingsTests` para editar el porcentaje) → `3.5m`.

Conteos de los demás archivos de la lista: `84` → `86`, `85` → `87`, y su comentario («86 del catálogo (… + Tarifa Retefuente y Tarifa ReteICA)»). `OrdersExportLayoutEffectiveTests:147` y `QuotationsSeedTests:134` pasan a mirar las dos llaves nuevas como últimas/ocultas.

- [ ] **Step 2: Verificar el rojo**

Run: `dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests`
Expected: compila; el rojo es de aserción, así que el owner lo ve al correr. Dejar en el commit la evidencia del build.

- [ ] **Step 3: Implementar**

Catálogo: las dos `new(...)` de Global Constraints tras `buyer_notes`. Procesador: tras `ExportCell.OfText(order.BuyerNotes ?? string.Empty)`:
`WithholdingRateCell(order, TaxCountries.RetefuenteType, 1m)`, `WithholdingRateCell(order, TaxCountries.ReteIcaType, 10m)`.
`WithholdingRateCell` toma `AppliedWithholdings.FirstOrDefault(tipo ordinal)`; con ella `Percentage * scale`, sin ella `0m`.

- [ ] **Step 4: Build en verde**

Run: `dotnet build` en el worktree.
Expected: `Build succeeded`, 0 warnings nuevos (TreatWarningsAsErrors).

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Quotations tests/Modules/Quotations
git commit -m "feat(quotations): export withholding rates for the billing import"
```

### Task 2: README

**Files:**
- Modify: `README.md` § Columnas del Excel de pedidos: las dos llaves, la unidad de cada una (% y ‰) y el límite de varias del mismo tipo.

- [ ] **Step 1:** Editar el README.
- [ ] **Step 2:** `git commit -m "docs: document the withholding rate export columns"`

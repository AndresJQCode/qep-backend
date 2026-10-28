# Redondeo de precios por tenant y por moneda — backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que toda línea de cotización, pedido y venta del POS se calcule «unitario primero» con una regla `(decimales, modo)` que el admin configura por moneda, que cada documento guarde la regla con que se calculó, y que USD 13,19 con 15 % en `(2, Up)` dé 11,22 por unidad y 112,20 por 10 unidades.

**Architecture:** `BuildingBlocks.Domain.Pricing` gana el tipo de valor `PriceRounding` y `VatIncludedLine` pasa a recibirlo en vez de la moneda. La regla se guarda en Tenancy (tabla `tenancy.tenant_price_roundings`, una fila sólo por moneda que difiere del valor por defecto) y se expone por el puerto `ITenantPriceRounding` de `Modules.Tenancy.Application`, que Quotations y Pos ya pueden referenciar (`QuotationsLayerTests`/`PosLayerTests`: Application sólo ve Tenancy, y la ve). Cotización, pedido y venta guardan la regla en dos columnas nullable del encabezado; `null` es la regla anterior a esta feature (`PriceRounding.LegacyFor(currency)`), mismo criterio que `QuotationPdf.LegacyNumberFormat`. El recálculo (`QuotationPricingRecalculation`, `OrderPricingRecalculation`) es el único lugar que baja la regla vigente al documento, así que sólo se mueve lo que alguien edita.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, FluentValidation, xUnit v3, Testcontainers.PostgreSql (`postgres:18-alpine`), `WebApplicationFactory<Program>`.

**Spec:** [`docs/superpowers/specs/2026-10-27-redondeo-de-precios-por-moneda-design.md`](../specs/2026-10-27-redondeo-de-precios-por-moneda-design.md)

**Fuera de alcance: frontend** (repo `qep-frontend`, plan propio). Lo que el spec §5 deja allá y no se puede perder:
- `pos/utils/estimated-line-total.ts`: copia la fórmula; pasa a «unitario primero» con la regla de la moneda de la caja (la publica `/auth/me`, Task 3).
- `lib/format-currency.ts` `roundToCurrencyPrecision`: usa `Math.round` (no aleja la mitad de cero); para precios pasa a la regla, para pagos queda con los decimales del catálogo al más cercano (spec §5).
- `catalog/utils/product-pricing.ts`: el precio de cada escala, hoy a 2 decimales, con la regla.
- `tenant-settings/components/quotation-settings-section.tsx`: una fila por moneda del catálogo, contra `GET/PUT /settings/price-rounding` (Task 2), con el aviso en COP cuando el modo no es `Nearest` (spec, «Riesgo conocido»).

---

## Antes de empezar

- Rama: `git branch --show-current` en `qep-backend` **en ese momento**, no lo que diga el arranque de la sesión. Tiene que decir `feature/redondeo-de-precios`; si dice otra cosa, frenar. Se repite antes de cada commit.
- `Stop-Process -Name Api -ErrorAction SilentlyContinue` antes de `dotnet build`, `dotnet test` o `dotnet ef` (busca también `dotnet Api.dll` si el build da MSB3021).
- Integración: `$env:Seed__Enabled = "false"` en la sesión, en línea aparte, antes de cualquier `dotnet test` de `*.IntegrationTests`. Necesitan Docker.
- Todo comando es PowerShell: sin `&&`, una línea por comando.
- Por tarea se corren **sólo** las clases tocadas con `--filter`; la suite completa corre una vez, en la Task 8.
- Migraciones con el factory de diseño: `dotnet ef migrations add <Nombre> --project src/Modules/<M>/Modules.<M>.Infrastructure --context <M>DbContext -o Persistence/Migrations`.
- Commits: conventional commits en inglés, **sin** `Co-Authored-By` ni atribución de IA, aunque el harness la sugiera.

## Global Constraints

- Fórmula única, todas las monedas y todos los tenants (spec §1): `lista = R(precio)`, `unitario = R(lista × (1 − d/100))`, `línea = Round2(cantidad × unitario)`, `descuento = Round2(cantidad × lista) − línea`, `IVA = Round2(línea × tasa / (100 + tasa))` sólo si la moneda discrimina IVA, `base = línea − IVA`. La rama «redondear el descuento de la línea» desaparece.
- Modos: `Nearest` = `MidpointRounding.AwayFromZero`; `Up` = `MidpointRounding.ToPositiveInfinity`; `Down` = `MidpointRounding.ToNegativeInfinity`. Nombres de enum por el cable: `Nearest`, `Up`, `Down`.
- Decimales: entero entre `0` y `CurrencyInfo.Decimals` del catálogo (`COP 0`, `USD 2`, `EUR 2`).
- Valor por defecto de una moneda sin configurar: `(CurrencyInfo.Decimals, Nearest)`.
- La regla se aplica sólo al precio con IVA incluido (lista y unitario descontado). `DiscountedUnitPriceWithoutTax`, `UnitPriceWithoutTax`, `DocumentTotals`, totales del POS y `Order.RecalculatePaymentStatus` siguen a 2 decimales / catálogo, al más cercano.
- `ItemizesTaxIn` no cambia: sólo COP discrimina IVA.
- Editables: cotización `Draft`/`Sent`, pedido `Pending`. Sólo se recalculan al editarse; ningún `GET` escribe; no hay recálculo masivo al guardar la regla.
- Conversión: el pedido hereda la regla guardada de la cotización.
- Pedidos externos (`OrderSource` distinto de `Quotation` y `Manual`): siempre el valor por defecto de su moneda, aun editados.
- POS: la regla se lee en cada vista previa y en cada venta; la venta guarda la que usó.
- Permisos: lectura `TenancyPermissions.SettingsRead`, escritura `TenancyPermissions.SettingsUpdate` (ya existen, con política). Revalidación de tenant y permiso en el handler, 403 nunca 404.
- BFF: la respuesta trae **todas** las monedas del catálogo, en orden de catálogo, cada una con su regla efectiva, también las que usan el valor por defecto.
- Valores de aceptación: USD `(2, Up)`, 13,19 con 15 % → unitario **11,22**; 10 unidades → línea **112,20**, descuento **19,70**. COP `(0, Nearest)` → idéntico a hoy.

## Decisiones de este plan (código manda sobre spec)

| # | Decisión | Por qué |
| --- | --- | --- |
| D1 | La regla vive en Tenancy y la leen Quotations y Pos por `ITenantPriceRounding`. | El spec lo propone; `PosLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules` y el `.csproj` de `Modules.Pos.Application` confirman que Pos ve `Tenancy.Application`; Quotations también (`ConvertQuotationToOrder.cs:4`). |
| D2 | Endpoint `GET/PUT /api/v1/tenants/{tenantId}/settings/price-rounding`, sin `If-Match`: última escritura gana. | Precedente directo: `QuotationSettingsEndpoints` (la misma pantalla) no usa `If-Match`. Atarlo a `Tenant.Version` haría que guardar la regla le diera 412 al formulario de ajustes generales. |
| D3 | Tenancy guarda una fila sólo por moneda **distinta** del valor por defecto; guardar el valor por defecto borra la fila. | Así «por defecto» sigue al catálogo si una moneda cambia de decimales, y `isDefault` de la respuesta significa algo. |
| D4 | Las columnas del encabezado son **nullable**, sin backfill; `null` se lee como `PriceRounding.LegacyFor(currency)`. | El spec pide ese criterio (§4, `LegacyNumberFormat`). Además, los `INSERT` a mano en `quotations.orders` y `pos.sales` (`OrderApiTests`, `FreightMigrationTests`, `ExportLoadSeeder`, …) siguen valiendo sin tocarlos. |
| D5 | `PriceRounding.LegacyFor(currency)` vive en BuildingBlocks con el literal `"COP"` → `(0, Nearest)`, si no `(2, Nearest)`; una prueba en `Tenancy.UnitTests` amarra que coincide con `PriceRoundingDefaults.For` para cada moneda del catálogo. | BuildingBlocks no ve el catálogo (es el literal `WholePesoCurrency` de hoy, mudado). Si mañana entra una moneda de 3 decimales la prueba se pone roja y obliga a decidir. |
| D6 | `QuotationItem.DiscountedUnitPrice` y `OrderItem.DiscountedUnitPrice` dejan de ser propiedades y pasan a `DiscountedUnitPriceUnder(PriceRounding rounding)`; los mapeadores le pasan la regla del documento. | La línea no ve el encabezado y la regla no se duplica por línea. El compilador encuentra los cinco llamadores (`QuotationMapping.cs:118`, `OrderMapping.cs:121`, `QuotationResponseComposer.cs:188`, `QuotationPdfDocumentMapper.cs:146` vía DTO, pruebas). |
| D7 | Cambiar la regla recalcula desde el `UnitPrice` **guardado** (ya redondeado), no relee el catálogo. | Es lo que hace hoy el recálculo de descuentos (`ApplyGroupDiscounts` → `ApplyDiscount`); releer el catálogo es `Reprice`, que sólo corre al cambiar de moneda. Con catálogos de 2 decimales no hay diferencia. |
| D8 | POS: un cambio de regla a mitad del carrito **no** da `pos.sale.price_changed`. | El spec supone que sí, pero `CreatePosSale.cs:261` compara el precio crudo del catálogo contra `ExpectedUnitPrice`, y el precio de lista no cambia con la regla. La venta se calcula y se guarda con la regla vigente; si su total ya no cuadra con los pagos, responde la validación de pagos de `PosSale.BuildPayments` (422). No se agrega campo al contrato. |
| D9 | `/auth/me` publica la regla efectiva de cada moneda por tenant (`ActiveTenantResponse.PriceRoundings`). | El estimado del carrito y el precio por escala del catálogo (spec §5) la necesitan y `cashier` no tiene `tenancy.settings.read`; mismo motivo que el formato regional (`AuthSessionEndpoints.cs:190-197`). |
| D10 | El flete fuera de COP pasa por la regla (`rounding.Apply(amount)`); en COP sigue `Compute(1, monto, 0, tasa, regla)`. | Spec §5: «queda con la regla de la moneda». |
| D11 | Auditoría: acción `tenancy.price_rounding.updated`, recurso `tenant`, `changedFields` = códigos de moneda cambiados (`["USD"]`), con `IAuditRecorder` en la misma transacción; sin cambios, ni auditoría ni escritura. | Mismo camino que `UpdateTenantSettingsHandler` (`UpdateTenantSettings.cs:109-124`). Sin outbox: nadie consume el cambio. |

## Review Focus

1. Cantidad con decimales (`1.25`) en modo `Up`: la línea es `Round2(cantidad × unitario)` y el descuento nunca sale negativo — Task 1, `AFractionalQuantityRoundsTheLineNotTheUnit`.
2. La regla cambia entre la vista previa y el cobro en el POS: la venta guarda la regla vigente y, si su total ya no es el pagado, responde 422 de pagos en vez de cobrar distinto en silencio — Task 6, `ARuleChangedAfterThePreviewAnswersWithThePaymentMismatch`.
3. Un pedido de Shopify `Pending` editado después de que el admin configuró `(2, Up)` sigue con `(2, Nearest)` — Task 5, `EditingAShopifyOrderKeepsTheDefaultRule`.
4. Una cotización USD `Sent` de antes de la migración (columnas `null`): su unitario derivado sale con `(2, Nearest)` y abrirla no escribe nada — Task 4, `ALegacyQuotationReadsTheDefaultRule`, y Task 5, `GettingASentQuotationAfterARuleChangeDoesNotMoveIt`.
5. Bajar USD a 0 decimales con un precio de lista con centavos y 0 % de descuento: el unitario es la lista redondeada y el descuento es 0, no negativo — Task 1, `FewerDecimalsThanTheListPriceNeverGiveANegativeDiscount`.

---

### Task 1: `PriceRounding` y la fórmula «unitario primero» en BuildingBlocks

**Files:**
- Create: `src/BuildingBlocks/BuildingBlocks.Domain/Pricing/PriceRounding.cs`
- Modify: `src/BuildingBlocks/BuildingBlocks.Domain/Pricing/VatIncludedLine.cs` (todo el archivo)
- Modify (puente mecánico, ver Step 5): `src/Modules/Quotations/Modules.Quotations.Domain/QuotationItem.cs:266,288`, `OrderItem.cs:169,179`, `FreightAmounts.cs:26`, `src/Modules/Pos/Modules.Pos.Domain/PosSaleLine.cs:83,96`, `src/Modules/Pos/Modules.Pos.Application/PreviewPosSale.cs:94,98`
- Test: `tests/Modules/Pos/Modules.Pos.UnitTests/VatIncludedLineTests.cs` (es la clase que hoy prueba la fórmula compartida), `tests/Modules/Pos/Modules.Pos.UnitTests/PriceRoundingTests.cs`

**Interfaces:**
- Produces:
  - `public enum PriceRoundingMode { Nearest, Up, Down }`
  - `public readonly record struct PriceRounding(int Decimals, PriceRoundingMode Mode)` con `public decimal Apply(decimal value)` y `public static PriceRounding LegacyFor(string currency)` (D5). El constructor lanza `ArgumentOutOfRangeException` con `Decimals < 0` o un modo no definido.
  - `VatIncludedLine.Compute(decimal quantity, decimal listPrice, decimal discountPercentage, int taxPercentage, PriceRounding rounding) : VatIncludedLineAmounts`
  - `VatIncludedLine.ChargedListPrice(decimal unitPrice, PriceRounding rounding) : decimal`
  - `VatIncludedLine.ChargedUnitPrice(decimal listPrice, decimal discountPercentage, PriceRounding rounding) : decimal` — `rounding.Apply(rounding.Apply(listPrice) × (1 − d/100))`
  - `VatIncludedLine.Round(decimal) : decimal` (Round2, sin cambios, para los encabezados)
  - Se eliminan `Compute(q, p, d, t)`, `Compute(q, p, d, t, string currency)`, `ChargedListPrice(p, string)` y `WholePesoCurrency`.

- [ ] **Step 1: Write the failing tests**

En `PriceRoundingTests`:

```csharp
[Theory]
[InlineData("11.2115", 2, PriceRoundingMode.Up, "11.22")]
[InlineData("11.2195", 2, PriceRoundingMode.Down, "11.21")]
[InlineData("0.125", 2, PriceRoundingMode.Nearest, "0.13")]   // la mitad se aleja de cero
[InlineData("23334.50", 0, PriceRoundingMode.Nearest, "23335")]
[InlineData("11.22", 2, PriceRoundingMode.Up, "11.22")]        // ya en la grilla: no sube
public void ApplyRoundsWithTheRule(string value, int decimals, PriceRoundingMode mode, string expected);

[Fact] public void LegacyForPesosIsWholeNearest() => Assert.Equal(new PriceRounding(0, PriceRoundingMode.Nearest), PriceRounding.LegacyFor("COP"));
[Fact] public void LegacyForAnyOtherCurrencyIsCentsNearest(); // "USD" y "EUR" → (2, Nearest)
[Fact] public void NegativeDecimalsAreRejected(); // Assert.Throws<ArgumentOutOfRangeException>(() => new PriceRounding(-1, PriceRoundingMode.Up))
```

En `VatIncludedLineTests`, reemplaza las llamadas sin regla por `PriceRounding.LegacyFor("COP")` o una regla explícita y agrega:

```csharp
[Fact]
public void TheAcceptanceExampleRoundsTheUnitUp()
{
    var usdUp = new PriceRounding(2, PriceRoundingMode.Up);
    Assert.Equal(11.22m, VatIncludedLine.ChargedUnitPrice(13.19m, 15m, usdUp));
    var amounts = VatIncludedLine.Compute(10m, 13.19m, 15m, 0, usdUp);
    Assert.Equal(112.20m, amounts.LineTotal);
    Assert.Equal(19.70m, amounts.DiscountAmount);
    Assert.Equal(0m, amounts.TaxAmount);
}

[Fact] public void NearestGivesTheElevenTwentyOne(); // (2, Nearest): unitario 11.21, línea 112.10, descuento 19.80
[Fact] public void DownRoundsTheUnitDown();          // (2, Down) 13.19 @ 15 % → 11.21; 3 uds → 33.63
[Fact] public void AFractionalQuantityRoundsTheLineNotTheUnit(); // (2, Up), 1.25 × 13.19 @ 15 %: unitario 11.22, línea Round2(1.25 × 11.22) = 14.03, descuento Round2(1.25 × 13.19) − 14.03 = 2.46, ≥ 0
[Fact] public void FewerDecimalsThanTheListPriceNeverGiveANegativeDiscount(); // (0, Down) 24.63 @ 0 %, 100 uds: lista 24, línea 2400, descuento 0
```

`InPesosTheDiscountedUnitPriceIsChargedInWholePesos` e `InPesosAListPriceWithCentsIsChargedWholeWithoutANegativeDiscount` quedan con **los mismos valores esperados**, llamando con `PriceRounding.LegacyFor("COP")`. Las filas de `ComputeMatchesTheQuotationFormula` se recalculan con la fórmula nueva y `(2, Nearest)`; las que cambian de valor se anotan en el commit. `InAnotherCurrencyTheCentsStay` se borra: su premisa (fuera de COP no se redondea el unitario) es la que el spec elimina.

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test tests/Modules/Pos/Modules.Pos.UnitTests --filter "FullyQualifiedName~VatIncludedLineTests|FullyQualifiedName~PriceRoundingTests"`
Expected: no compila (`PriceRounding` no existe).

- [ ] **Step 3: Implement `PriceRounding.cs` y la fórmula en `VatIncludedLine.cs`**

`Compute` sigue el orden del spec §1 literal: `lista = ChargedListPrice`, `unitario = ChargedUnitPrice`, `línea = Round(cantidad × unitario)`, `descuento = Round(cantidad × lista) − línea`, `IVA = Round(línea × tasa / (100 + tasa))`. Devuelve `new VatIncludedLineAmounts(descuento, IVA, línea − IVA)`. El comentario de la clase explica por qué el unitario va primero (unitario × cantidad tiene que dar la línea) y cita el spec 2026-10-27.

- [ ] **Step 4: Run to verify GREEN**

Mismo comando del Step 2. Expected: PASS.

- [ ] **Step 5: Puente mecánico en los llamadores**

Para que la solución compile sin adelantar las Tasks 4-6: cada llamador pasa `PriceRounding.LegacyFor(currency)` donde hoy pasa `currency`, y `ChargedListPrice(p, currency)` pasa a `ChargedListPrice(p, PriceRounding.LegacyFor(currency))`. `FreightAmounts.For` gana el parámetro en la Task 4; acá sólo cambia su llamada interna. Nada más en esos archivos.

- [ ] **Step 6: Barrer las pruebas unitarias que esperaban la fórmula vieja**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests`
Run: `dotnet test tests/Modules/Pos/Modules.Pos.UnitTests`
Son unitarias, sin Docker: se corren enteras una vez para ver por nombre qué cambió. Candidatas: `QuotationUsdTaxNotItemizedTests`, `DocumentTotalsTests`, `QuotationDiscountedUnitPriceTests`, `QuotationPdfDocumentMapperTests`, `OrderTests`, `PosSaleTests`, `CreatePosSaleHandlerTests`. Cada falla se corrige **recalculando** el valor con la fórmula de Global Constraints y `(2, Nearest)` (USD/EUR) — nunca borrando la aserción. Una falla en COP es un bug de este task, no una expectativa a actualizar.
Expected final: las dos suites en verde.

- [ ] **Step 7: Commit**

```powershell
git add src/BuildingBlocks/BuildingBlocks.Domain/Pricing src/Modules/Quotations/Modules.Quotations.Domain src/Modules/Pos tests/Modules/Pos/Modules.Pos.UnitTests tests/Modules/Quotations/Modules.Quotations.UnitTests
git commit -m "feat(pricing): unit-first line formula driven by a price rounding rule"
```

---

### Task 2: La regla en Tenancy — almacenamiento, puerto y `GET/PUT /settings/price-rounding`

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantPriceRounding.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantPriceRounding.cs` (puerto + `PriceRoundingDefaults`)
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantPriceRoundingRepository.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/PriceRoundingSettings.cs` (query, command, validator, handlers, DTOs)
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantPriceRoundingRepository.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenantPriceRoundingReader.cs` (implementa el puerto)
- Create: migración `AddTenantPriceRoundings` (generada)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs`, `TenancyInfrastructureExtensions.cs:40` (registros)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Api/TenantSettingsEndpoints.cs` (dos rutas nuevas en el mismo grupo)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/PriceRoundingDefaultsTests.cs`, `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/UpdatePriceRoundingHandlerTests.cs`, `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/PriceRoundingSettingsApiTests.cs`

**Interfaces:**
- Consumes: `PriceRounding`, `PriceRoundingMode` (Task 1).
- Produces:
  - `public interface ITenantPriceRounding { Task<PriceRounding> GetAsync(Guid tenantId, string currency, CancellationToken cancellationToken); Task<IReadOnlyDictionary<string, PriceRounding>> GetAllAsync(Guid tenantId, CancellationToken cancellationToken); }` — `GetAllAsync` trae todas las monedas del catálogo con su regla efectiva; una moneda sin fila es `PriceRoundingDefaults.For`. Un tenant sin fila en `tenancy.tenants` no lanza: devuelve los valores por defecto (el tenant simulado del stub, igual que `GetRegionalSettingsAsync` da null).
  - `public static class PriceRoundingDefaults { public static PriceRounding For(string currency); }` = `(Currencies.Get(currency).Decimals, Nearest)`.
  - `GetPriceRoundingQuery(TenantId TenantId) : IQuery<PriceRoundingSettingsDto>`; `UpdatePriceRoundingCommand(TenantId TenantId, IReadOnlyList<CurrencyPriceRoundingInput>? Currencies) : ICommand<PriceRoundingSettingsDto>`; `CurrencyPriceRoundingInput(string Currency, int Decimals, string Mode)`.
  - `PriceRoundingSettingsDto(IReadOnlyList<CurrencyPriceRoundingDto> Currencies)`; `CurrencyPriceRoundingDto(string Currency, int Decimals, string Mode, int MaxDecimals, bool IsDefault)`. El DTO comenta por qué viaja `MaxDecimals` (el select no conoce el catálogo de decimales por fila) y `IsDefault` (D3).
  - Tabla `tenancy.tenant_price_roundings`: `tenant_id uuid`, `currency varchar(3)`, `decimals smallint`, `mode varchar(16)`, `updated_at timestamptz`; PK `(tenant_id, currency)`; FK a `tenancy.tenants` con cascade.

- [ ] **Step 1: Write the failing tests**

`PriceRoundingDefaultsTests`:

```csharp
[Fact] public void TheDefaultIsTheCatalogueDecimalsNearest(); // COP → (0, Nearest), USD → (2, Nearest)
[Fact]
public void TheLegacyRuleMatchesTheDefaultForEveryCatalogueCurrency()
{
    foreach (var currency in Currencies.All)
        Assert.Equal(PriceRoundingDefaults.For(currency.Code), PriceRounding.LegacyFor(currency.Code));
}
```

`UpdatePriceRoundingHandlerTests` (dobles en memoria, como `UpdateTenantSettingsHandlerTests`):

```csharp
[Fact] public async Task SavingUsdUpStoresOneRowAndAuditsUsd();      // audit action "tenancy.price_rounding.updated", changedFields ["USD"]
[Fact] public async Task SavingTheDefaultDeletesTheRow();             // USD (2, Nearest) tras (2, Up): repositorio sin fila de USD
[Fact] public async Task SavingTheSameRulesWritesNothing();           // sin auditoría, sin SaveChanges
[Fact] public async Task AnotherTenantIsForbidden();                  // RequestForbiddenException "authorization.denied"
[Fact] public async Task WithoutSettingsUpdateIsForbidden();
```

`PriceRoundingSettingsApiTests` (integración, harness de `TenantSettingsApiTests`):

```csharp
[Fact] public async Task GetListsEveryCatalogueCurrencyWithItsDefault();
// [{COP,0,Nearest,0,true},{USD,2,Nearest,2,true},{EUR,2,Nearest,2,true}], en orden de catálogo
[Fact] public async Task PutReturnsTheWholeListAndGetReadsItBack(); // USD (2, Up) → isDefault false
[Theory]
[InlineData("USD", 3, "Up", "Currencies[1].Decimals")]   // más que el catálogo
[InlineData("USD", -1, "Up", "Currencies[1].Decimals")]
[InlineData("USD", 2, "Ceiling", "Currencies[1].Mode")]
public async Task PutRejectsAnInvalidRuleWithItsField(...);  // 422 validation.failed, errors[key]
[Fact] public async Task PutWithoutAllCatalogueCurrenciesIsRejected();  // 422, errors["Currencies"]
[Fact] public async Task PutWithAnUnknownOrRepeatedCurrencyIsRejected(); // "XXX" y USD dos veces → 422, errors["Currencies"]
[Fact] public async Task PutWithoutCurrenciesIsRejected();               // cuerpo {} → 422, no borra todo
[Fact] public async Task ReadingAnotherTenantIsForbidden();              // 403
[Fact] public async Task TheChangeIsAuditedInTheSameTransaction();      // fila en la auditoría con "tenancy.price_rounding.updated"
```

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~PriceRoundingDefaultsTests|FullyQualifiedName~UpdatePriceRoundingHandlerTests"`
Expected: no compila.

- [ ] **Step 3: Dominio y persistencia**

`TenantPriceRounding` (`TenantId`, `Currency`, `PriceRounding Rule`, `UpdatedAt`) con `Create` y `Change(PriceRounding, DateTimeOffset) : bool`; valida forma (`decimals >= 0`, modo definido) con `TenantDomainException("tenancy.price_rounding.invalid", …)`. El tope por catálogo vive en el validador de Application, porque Domain no ve `Currencies` (mismo criterio que `NormalizeCurrencyCode`, `Tenant.cs:396-407`). Mapeo EF del modo con `HasConversion<string>()` y `HasMaxLength(16)`.

Run: `dotnet ef migrations add AddTenantPriceRoundings --project src/Modules/Tenancy/Modules.Tenancy.Infrastructure --context TenancyDbContext -o Persistence/Migrations`
Expected: la migración sólo crea `tenancy.tenant_price_roundings` (revisa el diff: nada más).

- [ ] **Step 4: Application y endpoints**

`UpdatePriceRoundingValidator`: `Currencies` no nulo; contiene cada `Currencies.All` exactamente una vez y ningún código fuera del catálogo (mensaje en `errors.Currencies`); por elemento, `Decimals` entre 0 y `Currencies.Get(code).Decimals`, `Mode` uno de `Enum.GetNames<PriceRoundingMode>()` (ordinal). El handler valida, revalida tenant + `SettingsUpdate`, aplica D3 y D11, guarda y devuelve el DTO completo. `GetPriceRoundingHandler` revalida `SettingsRead`. Rutas `GET /price-rounding` (`SettingsRead`) y `PUT /price-rounding` (`SettingsUpdate`) en el grupo de `TenantSettingsEndpoints`, con `Produces`/`ProducesProblem` 403 y 422. `TenantPriceRoundingReader` implementa el puerto leyendo el repositorio; se registra como `AddScoped<ITenantPriceRounding, TenantPriceRoundingReader>()`.

- [ ] **Step 5: Run to verify GREEN**

Run: `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~PriceRoundingDefaultsTests|FullyQualifiedName~UpdatePriceRoundingHandlerTests"`
Run: `dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~PriceRoundingSettingsApiTests"`
Run: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~TenancyLayerTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/Modules/Tenancy tests/Modules/Tenancy
git commit -m "feat(tenancy): per-currency price rounding settings and lookup port"
```

---

### Task 3: `/auth/me` publica la regla de cada moneda

**Files:**
- Modify: `src/Api/AuthSessionEndpoints.cs:177-216` (`ToActiveTenantResponsesAsync`) y `:285-295` (`ActiveTenantResponse`)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthSessionApiTests.cs`

**Interfaces:**
- Consumes: `ITenantPriceRounding.GetAllAsync` (Task 2).
- Produces: `ActiveTenantResponse.PriceRoundings : IReadOnlyList<SessionPriceRoundingResponse>` al final del record; `SessionPriceRoundingResponse(string Currency, int Decimals, string Mode)`, en orden de catálogo, todas las monedas.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task TheSessionCarriesThePriceRoundingOfEveryCurrency()
// tras PUT USD (2, Up): activeTenants[0].priceRoundings == [{COP,0,Nearest},{USD,2,Up},{EUR,2,Nearest}],
// leído con un miembro sin tenancy.settings.read (X-Permissions vacío de settings)
```

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~AuthSessionApiTests.TheSessionCarriesThePriceRoundingOfEveryCurrency"`
Expected: FAIL (`priceRoundings` ausente).

- [ ] **Step 3: Implement** — un parámetro `ITenantPriceRounding` en los dos endpoints que arman la sesión (`:130` y `:159`) y una llamada por tenant junto a `currenciesInUse`. El `<remarks>` de `ActiveTenantResponse` dice por qué viaja acá (D9).

- [ ] **Step 4: Run to verify GREEN** — mismo comando. Run también la clase entera: `dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~AuthSessionApiTests"`. Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Api/AuthSessionEndpoints.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthSessionApiTests.cs
git commit -m "feat(auth): publish each currency's price rounding in the session"
```

---

### Task 4: Cotización y pedido guardan su regla (dominio + columnas)

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Quotation.cs` (propiedad, `UsePriceRounding`, llamadas a items y `RecalculateTotals:1111`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs` (ídem; `FromQuotation:264-325` copia la regla; `RecalculateTotals:1171`; `ApplyGroupDiscounts:723`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/QuotationItem.cs`, `OrderItem.cs` (`Apply` recibe la regla; `DiscountedUnitPriceUnder`, D6)
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/FreightAmounts.cs:14-29` (D10)
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` (dos columnas en `quotations` y en `orders`)
- Create: migración `AddDocumentPriceRounding` (generada)
- Modify (compilación): `QuotationMapping.cs:110-125`, `OrderMapping.cs:113-125`, `QuotationResponseComposer.cs:188` — pasan `document.PriceRounding` a `DiscountedUnitPriceUnder`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPriceRoundingTests.cs`, `QuotationDiscountedUnitPriceTests.cs`, `QuotationsDbContextMappingTests.cs:58-66`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/DocumentPriceRoundingMigrationTests.cs`

**Interfaces:**
- Consumes: `PriceRounding`, `VatIncludedLine.*` (Task 1).
- Produces:
  - `Quotation.PriceRounding : PriceRounding` y `Order.PriceRounding : PriceRounding` — de los campos privados `int? _priceRoundingDecimals` / `PriceRoundingMode? _priceRoundingMode`; con cualquiera en `null`, `PriceRounding.LegacyFor(Currency)` (D4).
  - `Quotation.UsePriceRounding(PriceRounding rounding, DateTimeOffset occurredAt) : void` y el mismo en `Order`. Si es igual a la vigente no hace nada; si no, la guarda, rehace **todas** las líneas con su cantidad, `UnitPrice`, descuento, origen y tasa actuales (D7) y llama a `RecalculateTotals`. No es una edición: no toca `Version` ni `Updated*`, mismo contrato que `ApplyGroupDiscounts`. No exige estado editable: lo llama sólo el recálculo, que ya corre dentro de una edición.
  - `QuotationItem.DiscountedUnitPriceUnder(PriceRounding rounding) : decimal` y `OrderItem.DiscountedUnitPriceUnder(PriceRounding rounding) : decimal` = `Quantity > 0 ? VatIncludedLine.ChargedUnitPrice(UnitPrice, DiscountPercentage, rounding) : 0m`.
  - Los métodos `internal` de las líneas (`Create`, `UpdateQuantity`, `ApplyDiscount`, `Reprice`) reciben `PriceRounding rounding` después de `currency`; el agregado pasa `PriceRounding`.
  - `FreightAmounts.For(FreightChargeMode? mode, decimal amount, int taxPercentage, string currency, PriceRounding rounding)`.
  - Columnas `price_rounding_decimals smallint NULL`, `price_rounding_mode varchar(16) NULL` en `quotations.quotations` y `quotations.orders`.

- [ ] **Step 1: Write the failing tests**

`QuotationPriceRoundingTests`:

```csharp
[Fact] public void UsingUsdUpRecomputesEveryLine();        // USD, 10 × 13.19 @ 15 %: tras UsePriceRounding((2, Up)) → línea Subtotal 112.20, DiscountAmount 19.70, Total 112.20
[Fact] public void UsingTheSameRuleIsANoOp();              // Version y UpdatedAt iguales, líneas iguales
[Fact] public void UsingARuleIsNotAnEdit();                // Version no sube
[Fact] public void ANewLineUsesTheStoredRule();            // tras (2, Up), AddItem 13.19 @ 15 % → DiscountedUnitPriceUnder(q.PriceRounding) == 11.22
[Fact] public void AnOrderFromAQuotationInheritsItsRule(); // Order.FromQuotation con (2, Up) → order.PriceRounding == (2, Up), mismas líneas al centavo
[Fact] public void ALegacyQuotationReadsTheDefaultRule();  // entidad sin regla guardada en USD → PriceRounding == (2, Nearest); en COP → (0, Nearest)
[Fact] public void ChargedFreightOutsidePesosFollowsTheRule(); // FreightAmounts.For(Charged, 12.50m, 0, "USD", (0, Up)) == (13, 0)
```

`QuotationDiscountedUnitPriceTests`: los casos pasan a `item.DiscountedUnitPriceUnder(quotation.PriceRounding)` con los mismos valores en COP. `QuotationsDbContextMappingTests.TheDiscountedUnitPriceIsDerivedAndNeverPersisted` sigue afirmando que no hay propiedad mapeada para el unitario descontado (ahora porque es método) y suma:

```csharp
[Fact] public void ThePriceRoundingIsTwoNullableColumns(); // quotations y orders: price_rounding_decimals y price_rounding_mode, IsNullable true
```

`DocumentPriceRoundingMigrationTests` (patrón de `QuotationPdfNumberFormatMigrationTests`): migra a la anterior, siembra una cotización USD `Sent` y un pedido COP por SQL sin las columnas, migra a la última (`MigrateAsync(null)` antes de llamar a la API, gotcha de `CLAUDE.md`) y afirma:

```csharp
[Fact] public async Task ExistingDocumentsReadTheDefaultRule(); // columnas NULL; el dominio cargado da (2, Nearest) y (0, Nearest)
```

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationPriceRoundingTests|FullyQualifiedName~QuotationDiscountedUnitPriceTests|FullyQualifiedName~QuotationsDbContextMappingTests"`
Expected: no compila (`UsePriceRounding`, `DiscountedUnitPriceUnder` no existen).

- [ ] **Step 3: Implement** el dominio y el mapeo EF según Interfaces. Se elimina el puente de la Task 1 en `QuotationItem`, `OrderItem` y `FreightAmounts`: ninguno de los tres vuelve a llamar a `LegacyFor`, la regla llega siempre del encabezado. `FromQuotation` asigna la regla de la cotización **antes** de copiar las líneas. El comentario de `DiscountedUnitPrice` que decía «se calcula desde el total de la línea» se reescribe: ahora es el unitario de la fórmula, con la regla del documento.

Run: `dotnet ef migrations add AddDocumentPriceRounding --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations`
Expected: sólo cuatro `AddColumn` nullable, sin `defaultValue`.

- [ ] **Step 4: Run to verify GREEN**

Run: el comando del Step 2.
Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~DocumentPriceRoundingMigrationTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Quotations tests/Modules/Quotations
git commit -m "feat(quotations): quotations and orders store the price rounding they were priced with"
```

---

### Task 5: Quotations aplica la regla vigente al recalcular

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationPricingRecalculation.cs:24-35`, `OrderPricingRecalculation.cs:13-24`
- Modify (inyectan el puerto y lo pasan): `AddQuotationItem.cs`, `UpdateQuotationItem.cs`, `RemoveQuotationItem.cs`, `BatchUpdateQuotationItems.cs`, `QuotationEditsApplication.cs`, `AddOrderItems.cs`, `PreviewOrderEdits.cs`, `SaveOrderEdits.cs`, `OrderDraftBuilder.cs` y los handlers que llaman a `QuotationEditsApplication`/`QuotationDraftBuilder` (`CreateQuotation`, `SaveQuotation`, `PreviewQuotation`, `PreviewNewQuotation`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IExternalOrderWriter.cs:171` (después de `Order.CreateExternal`)
- Modify: `QuotationPdfDocumentMapper.cs:128-150` (sólo el comentario: la línea sigue `Round2(q × UnitPrice) − DiscountAmount`, que con la fórmula nueva es exactamente la línea)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPricingRecalculationTests.cs`, `OrderPricingRecalculationTests.cs`, `QuotationsTestDoubles.cs` (doble `FixedTenantPriceRounding`), `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationPriceRoundingApiTests.cs`, `ExternalOrderServiceTests.cs`

**Interfaces:**
- Consumes: `ITenantPriceRounding.GetAsync` (Task 2), `UsePriceRounding`, `PriceRounding` del documento (Task 4), `PriceRoundingDefaults.For` (Task 2).
- Produces: `QuotationPricingRecalculation.ApplyAsync(IQuotationProductPricingLookup lookup, IQuotationSettingsStore settingsStore, ITenantPriceRounding priceRounding, Guid tenantId, Quotation quotation, DateTimeOffset occurredAt, CancellationToken cancellationToken)` y el mismo parámetro, en la misma posición, en `OrderPricingRecalculation.ApplyAsync`. El primer paso de los dos, **antes** del corte por cero líneas, es `document.UsePriceRounding(regla, occurredAt)` con la regla de `document.Currency` (la moneda **después** de la mutación, para que cambiar de cuenta y de moneda tome la regla de la nueva). En pedidos, `regla = order.Source is OrderSource.Quotation or OrderSource.Manual ? await priceRounding.GetAsync(...) : PriceRoundingDefaults.For(order.Currency)`.

- [ ] **Step 1: Write the failing tests**

Unitarias:

```csharp
// QuotationPricingRecalculationTests
[Fact] public async Task TheRecalculationAppliesTheTenantRule();         // doble con USD (2, Up): tras ApplyAsync, quotation.PriceRounding == (2, Up)
[Fact] public async Task ChangingCurrencyTakesTheRuleOfTheNewCurrency(); // COP → USD: la regla guardada es la de USD
// OrderPricingRecalculationTests
[Fact] public async Task AManualOrderUsesTheTenantRule();
[Fact] public async Task EditingAShopifyOrderKeepsTheDefaultRule();     // Source Shopify, doble (2, Up) → order.PriceRounding == (2, Nearest)
```

Integración, `QuotationPriceRoundingApiTests` (harness de Quotations; el PUT de la regla va contra la ruta de Task 2 con `X-Permissions` de `tenancy.settings.update`):

```csharp
[Fact] public async Task TheAcceptanceExampleThroughTheApi();
// USD (2, Up); POST item 10 × variante a 13.19 con escala 15 % → items[0].discountedUnitPrice 11.22,
// subtotal 112.20, discountAmount 19.70; GET /pdf: la fila imprime 11.22 y 112.20
[Fact] public async Task GettingASentQuotationAfterARuleChangeDoesNotMoveIt();
// enviada con (2, Nearest) → PUT (2, Up) → GET: mismos importes, misma versión, mismo discountedUnitPrice
[Fact] public async Task EditingASentQuotationRecalculatesWithTheNewRule();
// la misma, cambia cantidad → importes con (2, Up)
[Fact] public async Task ConvertingKeepsTheQuotationRule();
// cotización con (2, Nearest), PUT (2, Up), convertir → el pedido tiene los mismos importes que la cotización
[Fact] public async Task QuotationAndOrderGiveTheSameUnitAndLine();
// paridad: misma variante y cantidad en una cotización y en un pedido manual → mismo discountedUnitPrice y subtotal
```

`ExternalOrderServiceTests`:

```csharp
[Fact] public async Task AShopifyOrderIgnoresTheTenantRule(); // USD (2, Up) configurado; pedido externo 13.19 @ 15 % → importes con (2, Nearest)
```

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationPricingRecalculationTests|FullyQualifiedName~OrderPricingRecalculationTests"`
Expected: no compila (falta el parámetro).

- [ ] **Step 3: Implement** según Interfaces. `IExternalOrderWriter` llama a `order.UsePriceRounding(PriceRoundingDefaults.For(order.Currency), occurredAt)` después de `CreateExternal`, con un comentario que cite la decisión del owner (spec §4). Los handlers sólo agregan el parámetro al constructor y lo pasan: nada de lógica nueva en ellos.

- [ ] **Step 4: Run to verify GREEN**

Run: el comando del Step 2.
Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~QuotationPriceRoundingApiTests|FullyQualifiedName~ExternalOrderServiceTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Quotations tests/Modules/Quotations
git commit -m "feat(quotations): apply the tenant price rounding when a document is recalculated"
```

---

### Task 6: POS calcula y guarda con la regla vigente

**Files:**
- Modify: `src/Modules/Pos/Modules.Pos.Domain/PosSale.cs:75-129` (parámetro y propiedad), `PosSaleLine.cs:61-103`
- Modify: `src/Modules/Pos/Modules.Pos.Application/CreatePosSale.cs` (lee la regla antes de `PosSale.Create`, `:175`), `PreviewPosSale.cs:32-101`
- Modify: `src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/PosDbContext.cs:86-` (`ConfigureSale`)
- Create: migración `AddSalePriceRounding` (generada)
- Test: `tests/Modules/Pos/Modules.Pos.UnitTests/PosSaleTests.cs`, `CreatePosSaleHandlerTests.cs`, `ProductAndPreviewHandlersTests.cs`, `PosTestDoubles.cs` (doble `FakeTenantPriceRounding`), `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosSaleApiTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/PriceRoundingParityTests.cs`

**Interfaces:**
- Consumes: `ITenantPriceRounding.GetAsync` (Task 2), `VatIncludedLine.*` (Task 1).
- Produces: `PosSale.Create(PosSaleId id, string fingerprint, CashSession session, IReadOnlyList<PosSaleLineInput> lines, IReadOnlyList<PosPaymentInput> payments, DateTimeOffset at, PosSaleCustomer? customer = null, PriceRounding? rounding = null)` — `null` es `LegacyFor(session.Currency)`, sólo para pruebas viejas; `PosSale.PriceRounding : PriceRounding` con la misma lectura nullable que la Task 4; `PosSaleLine.Create(PosSaleId saleId, int position, PosSaleLineInput input, PriceRounding rounding)`. Columnas `price_rounding_decimals smallint NULL`, `price_rounding_mode varchar(16) NULL` en `pos.sales`.

- [ ] **Step 1: Write the failing tests**

```csharp
// PosSaleTests
[Fact] public void ASaleInDollarsRoundsTheUnitUp(); // (2, Up), 10 × 13.19 @ 15 % → Total 112.20, DiscountAmount 19.70; sale.PriceRounding == (2, Up)
// CreatePosSaleHandlerTests
[Fact] public async Task TheSaleStoresTheRuleReadAtSaleTime();          // doble (2, Up) → sale.PriceRounding == (2, Up)
[Fact] public async Task ARuleChangedAfterThePreviewAnswersWithThePaymentMismatch();
// preview con (2, Nearest) = 112.10; tarjeta por 112.10; el doble pasa a (2, Up) antes de cobrar →
// PosDomainException "pos.sale.payment_insufficient" (PosSale.BuildPayments), no price_changed (D8)
// ProductAndPreviewHandlersTests
[Fact] public async Task ThePreviewUsesTheTenantRule();                  // (2, Up) → line total 112.20
// PriceRoundingParityTests (Bootstrapper.UnitTests ve los dos dominios)
[Theory]
[InlineData("10", "13.19", "15", 2, PriceRoundingMode.Up)]
[InlineData("1.25", "35000", "33.33", 0, PriceRoundingMode.Nearest)]
public void QuotationOrderAndPosGiveTheSameLine(...); // mismo unitario, misma línea, mismo descuento en QuotationItem, OrderItem y PosSaleLine
```

`PosSaleApiTests`:

```csharp
[Fact] public async Task ADollarSaleUsesTheTenantRuleAndKeepsIt(); // caja USD, PUT (2, Up), venta → total 112.20; PUT (2, Nearest); GET de la venta → sigue 112.20
```

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test tests/Modules/Pos/Modules.Pos.UnitTests --filter "FullyQualifiedName~PosSaleTests|FullyQualifiedName~CreatePosSaleHandlerTests|FullyQualifiedName~ProductAndPreviewHandlersTests"`
Expected: no compila.

- [ ] **Step 3: Implement.** Se quita el puente de la Task 1 en `PosSaleLine` y `PreviewPosSale`. `PreviewPosSaleHandler` y `CreatePosSaleHandler` reciben `ITenantPriceRounding` y piden la regla de la moneda de la caja (`PosSellingCurrency.ResolveAsync` / `session.Currency`); el chequeo `price_changed` de `CreatePosSale.cs:261` no se toca (D8). Los sumatorios del encabezado siguen con `VatIncludedLine.Round`.

Run: `dotnet ef migrations add AddSalePriceRounding --project src/Modules/Pos/Modules.Pos.Infrastructure --context PosDbContext -o Persistence/Migrations`
Expected: dos `AddColumn` nullable en `pos.sales`.

- [ ] **Step 4: Run to verify GREEN**

Run: el comando del Step 2.
Run: `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~PriceRoundingParityTests"`
Run: `dotnet test tests/Modules/Pos/Modules.Pos.IntegrationTests --filter "FullyQualifiedName~PosSaleApiTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Pos tests/Modules/Pos tests/Bootstrapper/Bootstrapper.UnitTests/PriceRoundingParityTests.cs
git commit -m "feat(pos): price sales with the tenant rounding rule and keep it on the sale"
```

---

### Task 7: Las pruebas de integración que esperaban importes de la fórmula vieja

**Files:**
- Modify, según lo que falle: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationItemApiTests.cs`, `ManualOrderApiTests.cs`, `QuotationShippingApiTests.cs`, `QuotationPdfExportApiTests.cs`, `QuotationOneStepCreationApiTests.cs`, `OrderExportApiTests.cs`, `GlobalScaleDiscountApiTests.cs`; `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosSaleApiTests.cs`; `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/*` si leen importes USD de un evento

**Interfaces:**
- Consumes: todo lo anterior. Produces: nada nuevo.

- [ ] **Step 1: Correr las candidatas y anotar por nombre**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~QuotationItemApiTests|FullyQualifiedName~ManualOrderApiTests|FullyQualifiedName~QuotationShippingApiTests|FullyQualifiedName~QuotationPdfExportApiTests|FullyQualifiedName~QuotationOneStepCreationApiTests|FullyQualifiedName~OrderExportApiTests|FullyQualifiedName~GlobalScaleDiscountApiTests" --logger "trx;LogFileName=redondeo-candidatas.trx"`
Expected: una lista de fallas por nombre. Las que no tocan USD/EUR ni un descuento con centavos son sospechosas: se investigan como regresión, no se ajustan.

- [ ] **Step 2: Corregir una por una**

Cada expectativa se recalcula a mano con la fórmula de Global Constraints y la regla por defecto (`(2, Nearest)` en USD/EUR), y el valor nuevo lleva al lado un comentario corto con la cuenta (`// 10 × R(13.19 × 0.85) = 10 × 11.21`). Ninguna aserción se borra. Al cambiar un cuerpo crudo, barre también los `new {` de la misma clase (gotcha de cuerpos crudos en `CLAUDE.md`).

- [ ] **Step 3: Re-correr las mismas clases**

Run: el comando del Step 1.
Expected: PASS.

- [ ] **Step 4: Commit**

```powershell
git add tests
git commit -m "test: update USD expectations to the unit-first rounding formula"
```

---

### Task 8: Suite completa y documentación

**Files:**
- Modify: `README.md` (sección nueva «Redondeo de precios», junto a la de formato regional)
- Modify: `CLAUDE.md` (Gotchas verificados)

- [ ] **Step 1: Suite completa, una vez**

Run: `$env:Seed__Enabled = "false"`
Run: `dotnet build`
Run: `dotnet test --logger "trx"`
Expected: verde. Una falla se compara por nombre contra la baseline de `develop` (stash o worktree) antes de atribuirla a esta rama; los `.trx` con corchetes se leen con `Get-Content -LiteralPath`.

- [ ] **Step 2: README**

Sección «Redondeo de precios»: la fórmula, la regla por moneda y su valor por defecto, la ruta `GET/PUT /api/v1/tenants/{tenantId}/settings/price-rounding` con un ejemplo de cuerpo, qué documentos se recalculan y cuándo (spec §3), que los pedidos externos usan siempre el valor por defecto, y el riesgo de Siigo con COP en `Up`/`Down`.

- [ ] **Step 3: CLAUDE.md, un gotcha**

> **La regla de redondeo de un documento vive en dos columnas nullable, y `null` es `PriceRounding.LegacyFor`** (spec 2026-10-27). `LegacyFor` tiene el literal `"COP"` porque BuildingBlocks no ve el catálogo; `TheLegacyRuleMatchesTheDefaultForEveryCatalogueCurrency` la amarra a `PriceRoundingDefaults.For`. Una moneda nueva con otros decimales pone roja esa prueba: decide ahí si los documentos viejos de esa moneda se leen con la regla vieja. La regla sólo baja al documento en `QuotationPricingRecalculation`/`OrderPricingRecalculation` (`UsePriceRounding`); un camino nuevo que rehaga líneas sin pasar por ahí calcula con la regla guardada, no con la vigente. Los pedidos externos nunca toman la del tenant.

- [ ] **Step 4: Commit**

```powershell
git add README.md CLAUDE.md
git commit -m "docs: document per-currency price rounding"
```

`docs/estado.md` **no** se toca en esta rama: se actualiza en `develop` al mergear.

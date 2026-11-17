# Reglas de empresa: plan de implementación

> **Para agentes:** SUB-SKILL REQUERIDO: usa superpowers:subagent-driven-development (recomendado) o superpowers:executing-plans
> para ejecutar este plan tarea por tarea, **con las reglas de bloques de «Global Constraints»**, que mandan sobre el proceso del
> skill. Los pasos usan casillas (`- [ ]`).

**Goal:** Una empresa puede existir y emitir cotizaciones y pedidos sin cuenta bancaria, y cada empresa dice si cobra IVA; lo que
emite una empresa sin IVA va al 0 % en líneas y envío y cobra el precio de lista.

**Architecture:** La empresa sigue siendo obligatoria para emitir; la cuenta pasa a ser «banco y número nulos» dentro del mismo
`QuotationBillingAccount` (R3), que además congela `ChargesVat` junto con el país y el municipio de la empresa. Las líneas guardan
la tasa del producto (`product_tax_percentage`, R1) y la efectiva (`tax_percentage`); el agregado rehace las efectivas cuando cambia
`ChargesVat` (R2). POS congela `ChargesVat` en la caja (R4). Las tasas por defecto de Colombia las siembra un adaptador de
Bootstrapper detrás de un puerto de Companies, con advisory lock por tenant (R6).

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, xUnit, Testcontainers (sólo al final, lo corre el owner).

**Spec:** `docs/superpowers/specs/2026-11-17-reglas-de-empresa-design.md` (§4–§9; las decisiones R1–R7 de §9 son vinculantes).

## Global Constraints

- **Dónde se escribe:** sólo en el worktree
  `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\reglas-empresa` (rama
  `feature/reglas-empresa`), con rutas absolutas. Nunca en el checkout principal `...\qep-backend`.
- **Bloques (decisión del owner, 2026-11-12):** tres bloques, un implementador por bloque, una revisión por bloque; la revisión
  fuerte de toda la rama corre una sola vez, al final. Cada tarea conserva **su propio commit**.
  - **Bloque A — Companies y Quotations:** Tareas 1–6.
  - **Bloque B — Store, Shopify y POS:** Tareas 7–12 (consume las interfaces de A).
  - **Bloque C — Siembra de tasas, arquitectura, barrido y README:** Tareas 13–14 (consume `Company.ChargesVat` de A).
- **Pruebas escritas, no ejecutadas.** TDD: se escribe la prueba primero. Cuando el fallo esperado es de compilación, la evidencia
  RED es la salida de `dotnet build` con el error (`CS0117`, `CS1061`, `CS7036`…), copiada literal en el reporte del bloque. Cuando
  el fallo esperado es de comportamiento, la prueba queda escrita y su RED/GREEN lo corre el owner al final. **En esta sesión no
  se corre `dotnet test`** (ni unitarias ni de integración).
- **Verificación de cada tarea:** desde la raíz del worktree, en PowerShell:
  `dotnet build Backend.slnx; $LASTEXITCODE` → `0` y `0 Error(s)`. No uses `| tail`/`Select-Object -Last`
  sin mirar `$LASTEXITCODE`: el pipe esconde el código de salida. Si `Api.exe` (o `dotnet Api.dll`) está corriendo, el build
  falla por archivo bloqueado: avisa, no lo detengas.
- **Commits:** conventional commits en inglés, **sin atribución de IA ni `Co-Authored-By`**. Antes de cada commit,
  `git -C <worktree> branch --show-current` tiene que decir `feature/reglas-empresa`.
- **Idioma:** comentarios de código y documentación en español colombiano, tuteando (nada de voseo). Los identificadores, códigos
  de error y nombres de columna no se traducen.
- **Errores nuevos: ninguno** (spec §6). Los códigos que se reutilizan están nombrados en cada tarea.
- **Migraciones:** una por módulo (R5), generadas con el factory de diseño:
  `dotnet ef migrations add <Nombre> --project src/Modules/<M>/Modules.<M>.Infrastructure --context <M>DbContext -o Persistence/Migrations`.
  **El despliegue entero es no aditivo** (R5): va con `Recreate` o con el backend a cero réplicas. Lo dice el handoff de cada
  migración en su comentario de cabecera.
- **Columna `bool` con default `true` en la base:** se mapea con `.HasDefaultValue(true).HasSentinel(true)` (precedente
  `stores.show_prices`, `StoreDbContext.cs:115`). Sin el sentinel EF omite el `false` del `INSERT` y la base guarda `true`.
- **Pruebas de integración:** en Quotations, las clases nuevas usan `SharedQuotationsHost` (`IClassFixture<SharedQuotationsHost>`,
  `sharedHost.StartTestAsync()`) salvo que toquen algo global; en Companies, Store, Shopify y POS siguen su harness
  (`CompaniesApiHarness`, `StoreApiHarness`, `ShopifyApiHarness`, `PosApiHarness`). Todas fijan `Seed__Enabled=false` como ya
  lo hace su harness.
- **`docs/estado.md` no se toca** en esta rama: se actualiza en `develop` al mergear.

## Review Focus

1. **Un `false` que se pierde al insertar.** Empresa, pedido o caja con `ChargesVat = false` guardados y releídos tienen que volver
   en `false`. Lo pinan `ChargesVatFalseSurvivesTheRoundTrip` (Tarea 2), `AnOrderWithoutVatKeepsItAfterReload` (Tarea 6) y
   `ACashSessionWithoutVatKeepsItAfterReload` (Tarea 12).
2. **Fila vieja con `product_tax_percentage` nulo.** Una línea anterior a la migración, de una cotización que cambia a una empresa
   sin IVA y vuelve a una con IVA, recupera su tasa original (`tax_percentage`), no 0. Lo pina
   `ALegacyLineWithoutProductRateRecoversItsRateWhenVatReturns` (Tarea 4).
3. **Empresa sin IVA → con IVA con envío cobrado.** El envío vuelve a llevar la tasa del tenant; si el tenant no tiene,
   `quotation.quotation.freight_tax_rate_missing` / `order.order.freight_tax_rate_missing`. Lo pinan
   `TurningVatOnWithChargedFreightUsesTheGivenFreightRate` (Tarea 4) y `TurningVatOnWithoutTenantFreightRateIs422` (Tarea 4).
4. **Siembra con tasas inactivas homónimas.** Un tenant con «IVA 19 %» inactiva (mismo nombre y porcentaje) no puede romper con
   `IX_tax_rates_tenant_name_percentage_base`: la siembra salta esa tasa y la empresa se crea igual. Lo pina
   `SeedingSkipsAnExistingRateWithTheSameNameAndPercentage` (Tarea 13).
5. **Vista previa que antes respondía `billingAccount: null`.** Con empresa y sin cuenta, la vista previa y el detalle responden la
   cuenta con la empresa, `bankName: null`, `accountNumber: null` y `chargesVat`. Lo pina
   `ACompanyOnlyPreviewAnswersTheCompanyWithNullBankFields` (Tarea 5); `BillingCompanyPreviewApiTests` se ajusta en la Tarea 14.

---

## Bloque A — Companies y Quotations

### Task 1: Companies — cuentas opcionales y `ChargesVat`

**Files:**
- Modify: `src/Modules/Companies/Modules.Companies.Domain/Company.cs:86` (propiedad nueva), `:151-178` (`Create`), `:185-230`
  (`Update`), `:315-332` (`NormalizeBankAccounts`: quitar el mínimo y corregir el XML doc «Al menos una»).
- Modify: `src/Modules/Companies/Modules.Companies.Application/CompanyWriteRules.cs:125-130` (el `NotEmpty` pasa a `NotNull`; la
  cita del spec `:721-723` está desactualizada, la regla vive acá).
- Modify: `src/Modules/Companies/Modules.Companies.Application/CompaniesDtos.cs:43,61,101,137,150`,
  `CreateCompany.cs`, `UpdateCompany.cs`, `CompanyMapping.cs`, `src/Modules/Companies/Modules.Companies.Api/CompanyEndpoints.cs:123,152,209-230`.
- Modify: `src/Modules/Companies/Modules.Companies.Infrastructure/Persistence/CompaniesDbContext.cs:97` (mapeo de `charges_vat`).
- Test: `tests/Modules/Companies/Modules.Companies.UnitTests/CompanyBankAccountTests.cs` (las dos que hoy esperan
  `bank_accounts_required`, `:191` y `:251`), `CompanyTests.cs`, `CompanyWriteRulesTests.cs`,
  `tests/Modules/Companies/Modules.Companies.IntegrationTests/CompanyWriteApiTests.cs`.

**Interfaces:**
- Produces: `Company.ChargesVat` (`bool`, `get; private set;`, `true` por defecto).
- Produces: `Company.Create(..., CompanyContactInfo contact, DateTimeOffset occurredAt, bool chargesVat = true)` y
  `Company.Update(..., CompanyContactInfo contact, DateTimeOffset occurredAt, bool chargesVat)`.
- Produces: `CreateCompanyRequest`/`CreateCompanyCommand` con `bool? ChargesVat = null` (ausente = `true`) y
  `UpdateCompanyRequest`/`UpdateCompanyCommand` con `bool? ChargesVat = null` (**ausente = conserva**; ver Decisiones).
- Produces: `CompanyDto`, `CompanyResponse` y `CompanyListItemResponse` con `bool ChargesVat`.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// CompanyBankAccountTests: reemplazan a las dos que esperaban bank_accounts_required
[Fact] public void CreateWithoutBankAccountsKeepsAnEmptyList()   // Company.Create(..., bankAccounts: []) → BankAccounts vacío
[Fact] public void UpdateCanRemoveEveryBankAccount()             // Update(..., []) → BankAccounts.Count == 0
// CompanyTests
[Fact] public void ACompanyChargesVatByDefault()                 // Create sin chargesVat → ChargesVat == true
[Fact] public void UpdateCanTurnVatOff()                         // Update(..., chargesVat: false) → ChargesVat == false
// CompanyWriteRulesTests
[Fact] public void AnEmptyBankAccountListIsValid()               // BankAccounts = [] → sin error en "BankAccounts"
[Fact] public void AMissingBankAccountListIsAValidationError()   // BankAccounts = null → error en "BankAccounts"
[Fact] public void TwentyOneBankAccountsAreStillTooMany()        // tope de 20 intacto
```

```csharp
// CompanyWriteApiTests (harness actual)
[Fact] CreatesACompanyWithoutBankAccountsThatChargesVatByDefault
// POST con "bankAccounts": [] y sin "chargesVat" → 201; body.chargesVat == true; body.bankAccounts vacío
[Fact] UpdateWithoutChargesVatKeepsTheStoredValue
// POST chargesVat=false → PUT sin chargesVat → GET: chargesVat == false
[Fact] TheListReturnsChargesVat
// GET …/companies → items[0].chargesVat presente
```

- [ ] **Step 2: Verifica el RED** — `dotnet build` falla con `CS1739`/`CS1061` por `chargesVat`/`ChargesVat`. Copia la salida.
- [ ] **Step 3: Implementa** — quita el `if (bankAccounts.Count == 0)` de `NormalizeBankAccounts` (el tope de 20 y los duplicados
  siguen). `NotEmpty` → `NotNull` con mensaje «The bank account list is required (it can be empty).». Los handlers resuelven
  `command.ChargesVat ?? true` al crear y `command.ChargesVat ?? company.ChargesVat` al editar. Mapea `charges_vat` con
  `HasDefaultValue(true).HasSentinel(true)`.
- [ ] **Step 4: Build** — `dotnet build; $LASTEXITCODE` → `0`.
- [ ] **Step 5: Commit** — `feat(companies): allow companies without bank accounts and add charges vat`.

### Task 2: Migración de Companies

**Files:**
- Create: `src/Modules/Companies/Modules.Companies.Infrastructure/Persistence/Migrations/<ts>_AddCompanyChargesVat.cs` (+ Designer, snapshot).
- Test: `tests/Modules/Companies/Modules.Companies.IntegrationTests/CompanyChargesVatMigrationTests.cs`.

**Interfaces:** Consumes: el mapeo de `charges_vat` de la Tarea 1.

- [ ] **Step 1: Escribe la prueba** (precedente `CompanyCountryMigrationTests`):

```csharp
[Fact] ExistingCompaniesChargeVatAfterTheMigration
// migra hasta la anterior, inserta una empresa por SQL, migra a la última → charges_vat = true
[Fact] ChargesVatFalseSurvivesTheRoundTrip
// crea por la API con chargesVat=false → SELECT charges_vat → false
```

- [ ] **Step 2: Genera** — `dotnet ef migrations add AddCompanyChargesVat --project src/Modules/Companies/Modules.Companies.Infrastructure --context CompaniesDbContext -o Persistence/Migrations`.
  Revisa que salga `AddColumn<bool>("charges_vat", schema: "companies", table: "companies", nullable: false, defaultValue: true)`.
  Cabecera: «Aditiva, pero el despliegue del spec 2026-11-17 va con corte (R5)».
- [ ] **Step 3: Build** → `0`.
- [ ] **Step 4: Commit** — `feat(companies): migrate charges_vat column`.

### Task 3: Quotations — cuenta de cobro opcional

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/QuotationBillingAccount.cs` (todo el record).
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Quotation.cs:86-90,808-815,821-826,1069-1074,1150-1155,1202`.
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs:100-104,414-418,495,568,604,868-891`.
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationBillingAccountVerifier.cs`,
  `QuotationBillingAccountResolver.cs`, `IQuotationCompanyLookup.cs:20-38`, `QuotationsDtos.cs:300-328`,
  `OrderDraftBuilder.cs:53-56,87`, `CreateOrder.cs:63`, `IExternalOrderWriter.cs:246-249`, `IStoreOrderGateway.cs:173-178`,
  `QuotationMapping.cs:125,165`, `OrderMapping.cs:44`.
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs:60-89` (se borra
  `RefusePendingBillingAccounts` y su override), `:225-244`, `:700-722`.
- Modify: `src/Bootstrapper/QuotationCompanyLookup.cs` (pasa `company.ChargesVat`).
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OptionalBillingAccountTests.cs` (nuevo),
  `OrderBillingEditTests.cs`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OptionalBillingAccountApiTests.cs` (nuevo, `SharedQuotationsHost`).

**Interfaces:**
- Consumes: `Company.ChargesVat` (Tarea 1).
- Produces: `QuotationBillingAccount` con `string? BankName`, `string? AccountNumber`, `bool ChargesVat { get; init; } = true`,
  `bool HasBankAccount => BankName is not null` (ignorado por EF). **`IsPending` desaparece** (R3).
- Produces: `QuotationBillingAccountRequest(Guid CompanyId, string? BankName, string? AccountNumber, string? Currency)`.
- Produces: `VerifiedBillingAccount(Guid CompanyId, string? BankName, string? AccountNumber, string Currency, string Country, Guid? CityId = null, bool ChargesVat = true)`.
- Produces: `QuotationCompanyRef(..., Guid? CityId = null, bool ChargesVat = true)`.
- Produces: `QuotationBillingAccountResolver.ResolveAsync(IQuotationCompanyLookup lookup, Guid tenantId, QuotationBillingAccountRequest? request, string currencyWithoutAccount, CancellationToken ct)`
  — la sobrecarga de vistas previas con `Guid? billingCompanyId` se conserva y arma un request `(companyId, null, null, null)`.
- Produces: `Quotation.ChargesVat => BillingAccount?.ChargesVat ?? true` y `Order.ChargesVat => BillingAccount?.ChargesVat ?? true`.

Reglas que fija esta tarea:
- `Normalized()`: banco y número en blanco los dos → los dos `null` («empresa sin cuenta»); sólo uno → el código de hoy del que
  falta (`quotation.billing.bank_name_required` / `quotation.billing.account_number_required`). Sin `CompanyId` sigue
  `quotation.billing.company_required`.
- `VerifyAsync`: sin banco ni número no busca cuenta; con los dos, la regla de hoy (`account_not_found`). La moneda que devuelve
  sin cuenta es `Currencies.Normalize(currency)`: el llamador pasa la que corresponde.
- `ResolveAsync`: con cuenta, la moneda es la del request; **sin cuenta, `currencyWithoutAccount`** (R3): cotización nueva y pedido
  manual → moneda por defecto del tenant; edición de cotización o pedido → `Currency` actual del documento; tienda → moneda de la
  tienda; externo → la del pedido de origen. El `Currency` de un request sin cuenta se ignora.
- Cotización: enviar, convertir, `CanBeConvertedToOrder` y la bandera de `QuotationMapping:165` miran `BillingAccount is not null`
  (empresa), no la cuenta. El código sigue siendo `quotation.billing.account_required`. `CurrencyFor` y el `Currency` de `Create`
  usan `HasBankAccount`.
- Pedido: `FromQuotation`, `CreateManual`, `CreateExternal`, `CreateFromStore` aceptan la cuenta sin banco;
  `order.billing.account_required` sólo cuando no hay empresa. `ApplyBillingContext` permite cuenta → sin cuenta y sin cuenta →
  cuenta de la misma moneda; otra moneda sigue en `order.billing.currency_change_not_allowed`.
- EF: `BankName`/`AccountNumber` sin `IsRequired`; `billing.Ignore(v => v.HasBankAccount)`; `ChargesVat` mapeado a
  `billing_company_charges_vat` en las dos tablas (en `orders` con `HasDefaultValue(true).HasSentinel(true)`; en `quotations` el
  owned es opcional y la columna sale nullable, ver Tarea 6).

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// OptionalBillingAccountTests (unidad)
[Fact] ACompanyWithoutBankAccountNormalizesBothFieldsToNull()   // BankName "  ", AccountNumber null → los dos null, HasBankAccount false
[Fact] OnlyTheBankNameIsBankNameRequired...                     // banco sí, número no → "quotation.billing.account_number_required"
[Fact] AQuotationWithACompanyAndNoAccountCanBeSent()            // EnsureSendable no lanza; CanBeConvertedToOrder == true
[Fact] AQuotationWithoutCompanyStillNeedsIt()                   // sin BillingAccount → "quotation.billing.account_required"
[Fact] ACompanyOnlyAccountDoesNotChangeTheCurrency()            // cotización USD con cuenta USD → cuenta sin banco → Currency == "USD"
[Fact] AManualOrderWithoutBankAccountIsCreated()                // Order.CreateManual con cuenta sin banco → BillingAccount.BankName null
[Fact] AnExternalOrderWithoutBankAccountIsCreated()             // Order.CreateExternal, ídem
[Fact] AStoreOrderWithoutBankAccountIsCreated()                 // Order.CreateFromStore, ídem
// OrderBillingEditTests
[Fact] AnOrderCanDropItsBankAccount()                           // ApplyBillingContext(cuenta sin banco, misma moneda) → BankName null
[Fact] AnOrderWithoutBankAccountCanTakeOneInItsCurrency()
[Fact] AnOrderWithoutBankAccountRejectsAnAccountInAnotherCurrency() // "order.billing.currency_change_not_allowed"
```

```csharp
// OptionalBillingAccountApiTests (SharedQuotationsHost)
[Fact] AQuotationIsSentAndConvertedWithACompanyAndNoAccount
// billingAccount { companyId, bankName: null, accountNumber: null } → send 200 → convert 201;
// order.billingAccount.companyId == companyId, bankName == null, accountNumber == null
[Fact] AManualOrderIsCreatedWithoutBankAccountInTheTenantCurrency
// POST /orders sin banco → 201; currency == moneda por defecto del tenant
```

- [ ] **Step 2: RED** — `dotnet build` falla (`CS1061 'IsPending'` en los consumidores, firmas de `ResolveAsync`). Copia la salida.
- [ ] **Step 3: Implementa** las reglas de arriba. Reemplaza todo `IsPending` por `HasBankAccount` (o por `is not null` donde la
  pregunta era «hay empresa»); los composers (`OrderResponseComposer.cs:53,174`, `QuotationResponseComposer.cs:52,382`) dejan de
  ocultar la cuenta sin banco. Borra `RefusePendingBillingAccounts` y el override de `SaveChangesAsync` que la llama.
- [ ] **Step 4: Build** → `0`.
- [ ] **Step 5: Commit** — `feat(quotations): make the billing bank account optional`.

### Task 4: Quotations — IVA por empresa en líneas y envío

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/QuotationItem.cs:97-103,162-175,193-300`.
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/OrderItem.cs:26-39,69,116-129,142-240`.
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Quotation.cs:497-503,556-566,614-650,697-737,821-858`.
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs:455,671,868-891,987-1071,1130-1137,2007,2034`.
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ShippingResolver.cs:28-35,113-142` y sus llamadores
  (`OrderDraftBuilder.cs:110`, `PreviewOrderEdits.cs:130`, `QuotationEditsApplication.cs:144`, `SaveOrderEdits.cs:222`), más los
  que llaman `UpdateDetails`/`ApplyBillingContext`.
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` (mapeo de
  `ProductTaxPercentage` → `product_tax_percentage` en `quotation_items` y `order_items`).
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/CompanyVatTests.cs` (nuevo),
  `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/CompanyVatApiTests.cs` (nuevo, `SharedQuotationsHost`).

**Interfaces:**
- Consumes: `QuotationBillingAccount.ChargesVat`, `Quotation.ChargesVat`, `Order.ChargesVat` (Tarea 3).
- Produces: `QuotationItem.ProductTaxPercentage` y `OrderItem.ProductTaxPercentage` (`int?`), y `ProductTaxRate => ProductTaxPercentage ?? TaxPercentage` (R1).
- Produces: el parámetro `int taxPercentage` de `Create`/`UpdateQuantity`/`ApplyExternal` de las líneas pasa a llamarse
  `productTaxPercentage` y se suma `bool chargesVat`; `Reprice(QuotationItemPricing, string currency, bool chargesVat, PriceRounding, DateTimeOffset)`.
- Produces: `internal void ApplyChargesVat(bool chargesVat, string currency, PriceRounding rounding, DateTimeOffset occurredAt)` en las dos líneas.
- Produces: `bool Quotation.NeedsFreightTaxRateFor(QuotationBillingAccount? billingAccount)` y `bool Order.NeedsFreightTaxRateFor(QuotationBillingAccount? billingAccount)`.
- Produces: `Quotation.UpdateDetails(..., DateTimeOffset occurredAt, int? freightTaxPercentageWithVat = null)` y
  `Order.ApplyBillingContext(..., DateTimeOffset occurredAt, int? freightTaxPercentageWithVat = null)`.
- Produces: `ShippingResolver.ResolveAsync(Guid tenantId, ShippingRequest? request, ShippingDetails current, bool isStorePickup, string currency, bool chargesVat, string errorPrefix, CancellationToken ct)`
  y `public Task<int> FreightTaxPercentageAsync(Guid tenantId, string errorPrefix, CancellationToken ct)` (hoy privado `TenantFreightTaxPercentageAsync`).

Reglas:
- Tasa efectiva de una línea = `chargesVat && ItemizesTaxIn(currency) ? productTaxPercentage : 0`; `ProductTaxPercentage` guarda
  siempre la del producto. `ApplyDiscount` y `ApplyChargesVat` recalculan desde `ProductTaxRate`.
- R2: en `ApplyBillingAccount` (cotización) y `ApplyBillingContext` (pedido), si `ChargesVat` cambia, cada línea corre
  `ApplyChargesVat`, y el envío: a `false` → `FreightTaxPercentage = 0`; a `true` con envío `Charged`, valor > 0 y COP →
  `FreightTaxPercentage = freightTaxPercentageWithVat ?? throw new InvalidOperationException(...)` (un bug del llamador, no un 422).
  Después, `RecalculateTotals()` como hoy.
- `NeedsFreightTaxRateFor(account)` = `!ChargesVat && (account?.ChargesVat ?? true) && FreightChargeMode == Charged && FreightAmount > 0`
  y la moneda resultante (`CurrencyFor(account)`) es la de hoy y es COP. El handler, si da `true`, pide
  `shippingResolver.FreightTaxPercentageAsync` (que lanza `{prefix}.freight_tax_rate_missing`) y lo pasa.
- `AssignShipping` de los dos agregados guarda `ChargesVat ? shipping.FreightTaxPercentage : 0` (defensa para externos y tienda).
- `ShippingResolver`: con `chargesVat == false` la tasa es 0 y **no se lee** la del tenant (nunca `freight_tax_rate_missing`).
- `FromQuotation` copia `ProductTaxRate` y la cuenta (con su `ChargesVat`) tal cual; no relee la empresa (R2).
- Excedente de IVA del cliente: sin cambios; con líneas al 0 % no tiene qué quitar (§5.2).

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// CompanyVatTests (unidad)
[Fact] ALineOfACompanyWithoutVatChargesTheListPrice()
// UnitPrice 119_000, producto 19 %, chargesVat false, COP → TaxPercentage 0, TaxAmount 0, Subtotal 119_000, ProductTaxPercentage 19
[Fact] FreightOfACompanyWithoutVatHasNoTax()          // envío Charged 11_900 → FreightTaxAmount 0, FreightTaxPercentage 0
[Fact] ChangingToACompanyWithoutVatRecalculatesTheLines()
// cotización con IVA 19 % → UpdateDetails con cuenta ChargesVat=false → líneas TaxAmount 0; Total igual al de antes
[Fact] ChangingBackToACompanyWithVatRestoresTheProductRate()   // → TaxPercentage 19 otra vez
[Fact] ALegacyLineWithoutProductRateRecoversItsRateWhenVatReturns()
// línea reconstituida con ProductTaxPercentage null y TaxPercentage 19 → sin IVA → con IVA → 19
[Fact] TurningVatOnWithChargedFreightUsesTheGivenFreightRate()     // freightTaxPercentageWithVat: 19 → FreightTaxPercentage 19
[Fact] TurningVatOnWithChargedFreightWithoutARateIsABug()          // null → InvalidOperationException
[Fact] VatSurplusAndNoVatTogetherChargeTheListPrice()             // AppliesVatSurplus y ChargesVat false → Total = precio de lista
[Fact] AnOrderFromAQuotationWithoutVatKeepsItsLines()             // FromQuotation → ChargesVat false, TaxPercentage 0, ProductTaxPercentage 19
[Fact] RepricingAnOrderWithoutVatKeepsZeroTax()                   // UpdateQuantity con productTaxPercentage 19 → TaxPercentage 0
```

```csharp
// CompanyVatApiTests (SharedQuotationsHost)
[Fact] AQuotationOfACompanyWithoutVatChargesTheListPriceAndNoFreightTax
// empresa chargesVat=false, tenant SIN freight_tax_rate_id, envío cobrado → 200, sin freight_tax_rate_missing; taxAmount 0
[Fact] TurningVatOnWithoutTenantFreightRateIs422
// cotización sin IVA con envío cobrado → PUT con cuenta de empresa con IVA → 422 quotation.quotation.freight_tax_rate_missing
[Fact] ChangingTheCompanyLaterDoesNotChangeAnIssuedOrder
// pedido emitido sin IVA → PUT empresa chargesVat=true → GET pedido: taxAmount sigue 0
```

- [ ] **Step 2: RED** — `dotnet build` (`CS7036` por `chargesVat`, `CS1061` por `ProductTaxPercentage`). Copia la salida.
- [ ] **Step 3: Implementa** con las firmas de Interfaces. Los llamadores de líneas pasan `ChargesVat` del documento; los de
  `ShippingResolver.ResolveAsync` pasan el `ChargesVat` del documento **después** de aplicar la cuenta del mismo request.
- [ ] **Step 4: Build** → `0`.
- [ ] **Step 5: Commit** — `feat(quotations): apply per-company vat to lines and freight`.

### Task 5: Quotations — lectores sin cuenta (DTO, PDF, Excel)

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationsDtos.cs:300-316` (`QuotationBillingAccountDto`: banco y
  número `string?`, `bool ChargesVat = true`, sin `IsPending`), `:923-933` (`QuotationBillingResponse`: ídem).
- Modify: `QuotationResponseComposer.cs`, `OrderResponseComposer.cs`, `QuotationMapping.cs`, `OrderMapping.cs`.
- Modify: `QuotationPdfDocument.cs:140-150` (`BankName`, `AccountNumber` → `string?`), `QuotationPdfDocumentMapper.cs:229-240`.
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Pdf/quotation.typ:185` —
  `#let cuenta-visible = data.billingAccount != none and data.billingAccount.bankName != none and mostrar-cuenta`. El membrete
  (`:218-241`) no cambia.
- Modify: `OrdersExportProcessor.cs:162-167` (se quita el `!` y su comentario), `:294-296` (`BankAndAccount(string? bank, string? account)`).
- Modify (si hace falta): `src/Bootstrapper/Seeding/ExportLoadSeeder.cs` — sólo verificar que su SQL siga válido; no siembra sin cuenta.
- Test: `QuotationPdfDocumentMapperTests.cs`, `QuotationTemplateTests.cs`, `OrderExportApiTests.cs`, `OptionalBillingAccountApiTests.cs`.

**Interfaces:** Consumes: `QuotationBillingAccount.HasBankAccount`, `ChargesVat` (Tareas 3–4). Produces: `billingAccount.chargesVat`
en detalle, lista y vista previa de cotización y pedido (contrato §6).

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
[Fact] AnAccountWithoutBankMapsNullBankFields()        // mapper: BankName null, AccountNumber null, CompanyName presente
[Fact] TheTemplateOmitsThePaymentBlockWithoutBank()    // QuotationTemplateTests: compila y el texto no contiene "Cuenta para el pago"; sí el nombre de la empresa
[Fact] AnOrderWithoutBankAccountLeavesThePaymentMethodCellsEmpty() // OrderExportApiTests: payment_method_1 == "" con un comprobante
[Fact] ACompanyOnlyPreviewAnswersTheCompanyWithNullBankFields()   // POST /orders/preview con billingCompanyId → billingAccount.companyId, bankName null, chargesVat
[Fact] TheDetailReturnsChargesVat()                              // GET cotización → billingAccount.chargesVat == false para empresa sin IVA
```

- [ ] **Step 2: RED** — `dotnet build` (tipos `string?`, `ChargesVat`). Copia la salida.
- [ ] **Step 3: Implementa.** El texto «Pago: por definir» lo pinta el frontend (§4.2): el backend sólo deja `null`.
- [ ] **Step 4: Build** → `0`.
- [ ] **Step 5: Commit** — `feat(quotations): read documents without a bank account`.

### Task 6: Migración de Quotations

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<ts>_OptionalBillingAccountAndChargesVat.cs` (+ Designer, snapshot).
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OptionalBillingAccountMigrationTests.cs` (base propia:
  `StartEmptyDatabaseAsync`, no el host compartido).

Contenido esperado (revísalo contra lo que genere EF; los nombres reales tienen el prefijo `billing_`):
- `quotations.orders.billing_bank_name` y `billing_account_number` → nullable. En `quotations.quotations` ya lo son (owned
  opcional): confírmalo en el snapshot; si EF no genera nada ahí, está bien.
- `quotations.orders.billing_company_charges_vat` `bool NOT NULL DEFAULT true`.
- `quotations.quotations.billing_company_charges_vat` `bool NULL` (owned opcional) **más** un
  `migrationBuilder.Sql("UPDATE quotations.quotations SET billing_company_charges_vat = true WHERE billing_company_id IS NOT NULL;")`:
  sin él, EF no puede materializar un `bool` desde `NULL` en las cotizaciones con cuenta.
- `quotation_items.product_tax_percentage` y `order_items.product_tax_percentage` `int NULL`, sin relleno (R1).
- Cabecera: «No aditiva para el código viejo (R5): desplegar con `Recreate` o el backend a cero réplicas. El `Down` falla si ya
  hay pedidos sin cuenta: no se corre después de emitir uno.»

- [ ] **Step 1: Escribe la prueba**

```csharp
[Fact] ExistingQuotationsWithAccountReadChargesVatTrue      // fila sembrada antes → tras migrar billing_company_charges_vat = true
[Fact] ExistingOrdersChargeVatAndKeepTheirAccount           // billing_company_charges_vat = true, bank intacto
[Fact] LegacyLinesHaveNoProductRate                         // product_tax_percentage IS NULL; tax_percentage intacto
[Fact] AnOrderWithoutVatKeepsItAfterReload                  // tras MigrateAsync(null), POST pedido de empresa sin IVA → SELECT → false
```

- [ ] **Step 2: Genera** — `dotnet ef migrations add OptionalBillingAccountAndChargesVat --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations`; agrega el `UPDATE`.
- [ ] **Step 3: Busca `FromSql` crudos que lean `orders`, `quotations` o sus líneas** (`QuotationSettingsStore` es el precedente
  del `CLAUDE.md`): si alguno lista columnas de esas tablas, suma las nuevas.
- [ ] **Step 4: Build** → `0`.
- [ ] **Step 5: Commit** — `feat(quotations): migrate optional bank account, charges vat and product tax rate`.

**Fin del Bloque A:** una revisión del bloque.

---

## Bloque B — Store, Shopify y POS

### Task 7: Store — empresa sin cuenta por moneda

**Files:**
- Modify: `src/Modules/Store/Modules.Store.Domain/StoreBillingAccount.cs:4` → `StoreBillingAccount(Guid CompanyId, string? BankName, string? AccountNumber, string Currency)`.
- Modify: `src/Modules/Store/Modules.Store.Application/UpdateStoreSettings.cs:62-70` (banco y número: los dos o ninguno),
  `IStoreBillingAccounts.cs` (`ValidateAsync(..., string? bankName, string? accountNumber, ...)`), `StoreDtos.cs:95`
  (`StoreBillingAccountDto` con `string?`), `StoreSettingsComposer.cs`, `ListStoreBillingAccounts.cs`.
- Modify: `src/Modules/Store/Modules.Store.Infrastructure/Persistence/StoreDbContext.cs:163-168` (sin `IsRequired` en banco y número;
  el `CHECK CK_store_currencies_account_currency` se conserva).
- Modify: `src/Bootstrapper/StoreBillingAccounts.cs` (valida con `QuotationBillingAccountVerifier`, que ya acepta sin cuenta; la
  lista suma una opción por empresa activa con `BankName`/`AccountNumber`/`Currency` en `null`), `src/Bootstrapper/StoreOrdersAdapter.cs:124-125`
  (pasa nulos y la moneda de la tienda como `currencyWithoutAccount`).
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IStoreOrderGateway.cs:173-178` (pasa `draft.BillingAccount.Currency` como `currencyWithoutAccount`).
- Test: `tests/Modules/Store/Modules.Store.UnitTests/UpdateStoreSettingsValidatorTests.cs`, `StoreSettingsHandlerTests.cs`,
  `tests/Modules/Store/Modules.Store.IntegrationTests/StoreSettingsApiTests.cs`, una de checkout existente del proyecto.

**Interfaces:** Consumes: `QuotationBillingAccountRequest` con nulos, `VerifiedBillingAccount` (Tarea 3). Produces:
`StoreBillingAccountOption(Guid CompanyId, string CompanyName, string? BankName, string? AccountNumber, string? Currency)`.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
[Fact] ACurrencyWithACompanyAndNoAccountIsValid()            // validator: sin banco ni número → sin error
[Fact] OnlyTheBankNameIsAValidationError()                   // sólo banco → error en "currencies"
[Fact] SavesACurrencyWithoutAccount()                        // PUT settings → GET: billingAccount.bankName null, companyId presente
[Fact] TheAccountListOffersEachActiveCompanyWithoutAccount() // GET billing-accounts → una fila con bankName null por empresa activa
[Fact] ACheckoutWithoutAccountCreatesTheOrderInTheStoreCurrency() // pedido: billingAccount.bankName null; currency = la de la tienda
[Fact] ACheckoutOfACompanyWithoutVatChargesTheListPrice()    // taxAmount 0, total = suma de precios de lista
```

- [ ] **Step 2: RED** — `dotnet build`. Copia la salida.
- [ ] **Step 3: Implementa.** Mensaje del validador: «Banco y número de cuenta van juntos, o ninguno.».
- [ ] **Step 4: Build** → `0`.
- [ ] **Step 5: Commit** — `feat(store): allow a store currency with a company and no bank account`.

### Task 8: Migración de Store

**Files:** Create `src/Modules/Store/Modules.Store.Infrastructure/Persistence/Migrations/<ts>_OptionalStoreBillingAccount.cs`;
Test `tests/Modules/Store/Modules.Store.IntegrationTests/StorePersistenceTests.cs` (suma una).

- [ ] **Step 1: Prueba** — `[Fact] ACurrencyWithoutAccountRoundTrips` (guardar y releer `store_currencies` con `billing_bank_name` y `billing_account_number` en `NULL`).
- [ ] **Step 2: Genera** — `dotnet ef migrations add OptionalStoreBillingAccount --project src/Modules/Store/Modules.Store.Infrastructure --context StoreDbContext -o Persistence/Migrations`. Espera sólo `AlterColumn` a nullable en las dos columnas. Cabecera R5.
- [ ] **Step 3: Build** → `0`.
- [ ] **Step 4: Commit** — `feat(store): migrate optional store billing account`.

### Task 9: Shopify — tienda sin cuenta y pedidos de empresa sin IVA

**Files:**
- Modify: `src/Modules/Shopify/Modules.Shopify.Domain/ShopifyStore.cs:211` → `ShopifyBillingAccount(Guid CompanyId, string? BankName, string? AccountNumber, string Currency)`;
  `:150` sin cambio de fórmula (`Billing` ya implica empresa), sólo el XML doc.
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyStores.cs:53-57` (banco y número: los dos o ninguno; moneda sigue
  obligatoria), `:339-363` (sin cuenta → `IShopifyBillingAccounts.FindCompanyAsync`; `null` → `shopify.store.billing_account_not_found`).
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyPorts.cs:385,413,654-659`.
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyOrderImporter.cs:108-131`, `ShopifyShippingMapper.cs:55-76`,
  `ShopifyLineResolver.cs:85-96` y todo llamador de esos dos (la sincronización de ediciones incluida: `rg "ShopifyLineResolver|ShopifyShippingMapper" src`).
- Modify: `src/Bootstrapper/ShopifyBillingAccounts.cs`, `src/Bootstrapper/ShopifyOrderWriter.cs:222-224` (pasa nulos; moneda del pedido como `currencyWithoutAccount`).
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IExternalOrderWriter.cs:246-249` (`currencyWithoutAccount` = `draft.BillingAccount.Currency`).
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifyStoreHandlersTests.cs`, `ShopifyStoreTests.cs`,
  `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifyStoresApiTests.cs` y la clase de importación existente del proyecto.

**Interfaces:**
- Consumes: Tareas 3–4 (cuenta sin banco; el pedido aplica `ChargesVat` a líneas y envío).
- Produces: `ShopifyBillingAccountRef(Guid CompanyId, string? BankName, string? AccountNumber, string Currency, bool ChargesVat = true)`.
- Produces: `Task<ShopifyBillingCompanyRef?> IShopifyBillingAccounts.FindCompanyAsync(Guid tenantId, Guid companyId, CancellationToken ct)`
  con `ShopifyBillingCompanyRef(Guid CompanyId, bool ChargesVat)`; `null` si la empresa no es del tenant.
- Produces: `ShopifyLineResolver` y `ShopifyShippingMapper` reciben `bool chargesVat`.

Reglas (§4.4, R7):
- Importador, en orden: tienda con cuenta → `FindAsync`; `null` → `billing_account_missing`; moneda distinta → `currency_mismatch`
  (hoy). Tienda sin cuenta → `FindCompanyAsync`; `null` → `billing_account_missing` (sin motivo nuevo); **sin chequeo de moneda**:
  el pedido entra en `snapshot.Currency`.
- Con `chargesVat == false`: el precio de la línea se calcula con tasa 0 (`ShopifyPricing.UnitPriceWithVat(price, taxesIncluded, 0, currency)`)
  sin importar `taxesIncluded`; la línea del borrador lleva la tasa del producto (el pedido la pone en 0 y la guarda en
  `product_tax_percentage`). El envío va con tasa 0 y **no** devuelve `freight_tax_rate_missing`.
- `ShopifyOrderWriter` pasa la cuenta tal cual, con nulos.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
[Fact] AStoreIsConfiguredWithACompanyAndNoAccount()         // handler: sin banco ni número → Billing.BankName null, IsConfigured true
[Fact] OnlyTheAccountNumberIsAValidationError()              // "billingBankName"
[Fact] AnUnknownCompanyIsBillingAccountNotFound()            // shopify.store.billing_account_not_found
[Fact] AnOrderOfAStoreWithoutAccountImportsInTheOrderCurrency()  // tienda COP sin cuenta, pedido USD → importado, sin currency_mismatch
[Fact] AStoreWithoutAccountWhoseCompanyIsGoneWaits()          // wait_reason billing_account_missing
[Fact] AnOrderOfACompanyWithoutVatHasNoTaxAndNoFreightRateWait()
// taxesIncluded=false, producto 19 %, tenant sin tasa de envío → importado; líneas TaxAmount 0; precio = el de Shopify sin sumar IVA
```

- [ ] **Step 2: RED** — `dotnet build`. Copia la salida.
- [ ] **Step 3: Implementa.**
- [ ] **Step 4: Build** → `0`.
- [ ] **Step 5: Commit** — `feat(shopify): import orders for stores without a bank account or vat`.

### Task 10: Migración de Shopify

**Files:** Create `src/Modules/Shopify/Modules.Shopify.Infrastructure/Persistence/Migrations/<ts>_OptionalShopifyStoreBillingAccount.cs`.

- [ ] **Step 1: Prueba** — en `ShopifyStoresApiTests`: `[Fact] AStoreWithoutAccountRoundTrips` (PUT sin banco → GET → `billingBankName` null).
- [ ] **Step 2: Genera** — `dotnet ef migrations add OptionalShopifyStoreBillingAccount --project src/Modules/Shopify/Modules.Shopify.Infrastructure --context ShopifyDbContext -o Persistence/Migrations`. Espera `AlterColumn` a nullable de `billing_bank_name` y `billing_account_number` (`ShopifyDbContext.cs:251-257`); `billing_currency` no cambia. Cabecera R5.
- [ ] **Step 3: Build** → `0`.
- [ ] **Step 4: Commit** — `feat(shopify): migrate optional store billing account`.

### Task 11: POS — `ChargesVat` congelado en la caja

**Files:**
- Modify: `src/Modules/Pos/Modules.Pos.Domain/PosCompanySnapshot.cs:8-9` (suma `bool ChargesVat = true`),
  `CashSession.cs:29` (propiedad `CompanyChargesVat`), `:108-115` (la copia del snapshot).
- Modify: `src/Modules/Pos/Modules.Pos.Application/IPosCompanyLookup.cs:12` (`PosCompanyRef(..., bool IsActive, bool ChargesVat = true)`),
  `OpenCashSession.cs:79-97` (lleva `ChargesVat` al snapshot), `CreatePosSale.cs:275-289`, `PreviewPosSale.cs:55-102`, y
  `PosSellingCurrency` (la vista previa necesita la caja).
- Modify: `src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/PosDbContext.cs:50-55` (`company_charges_vat`, `HasDefaultValue(true).HasSentinel(true)`).
- Modify: `src/Bootstrapper/PosCompanyLookup.cs` (pasa `company.ChargesVat`).
- Test: `tests/Modules/Pos/Modules.Pos.UnitTests/CashSessionTests.cs`, `CreatePosSaleHandlerTests.cs`,
  `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosSaleApiTests.cs`.

**Interfaces:**
- Consumes: `Company.ChargesVat` (Tarea 1).
- Produces: `CashSession.CompanyChargesVat` (`bool`).
- Produces: `PosSellingCurrency.ResolveContextAsync(...)` → `PosSellingContext(string Currency, bool ChargesVat)` (sin caja abierta:
  moneda por defecto del tenant y `ChargesVat = true`, como hoy).

Regla (R4): tasa efectiva = `session.CompanyChargesVat ? product.TaxPercentage : 0`, en la vista previa (`ToLine`) y en la venta
(`PosSaleLineInput` y la comparación con `line.ExpectedTaxPercentage`). Un cliente que mande la tasa del producto en una caja sin
IVA recibe `pos.sale.price_changed`. `PosSaleLine` no cambia: la venta no se recalcula.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
[Fact] OpeningACashSessionFreezesChargesVat()                 // snapshot ChargesVat false → session.CompanyChargesVat false
[Fact] APreviewOfACashSessionWithoutVatHasNoTax()             // producto 19 %, 11_900 → taxPercentage 0, taxAmount 0, total 11_900
[Fact] ASaleOfACashSessionWithoutVatHasNoTax()                // expectedTaxPercentage 0 → 201; líneas taxAmount 0
[Fact] ASaleWithTheProductRateInACashSessionWithoutVatIsPriceChanged() // expectedTaxPercentage 19 → 422 pos.sale.price_changed
```

- [ ] **Step 2: RED** — `dotnet build`. Copia la salida.
- [ ] **Step 3: Implementa.**
- [ ] **Step 4: Build** → `0`.
- [ ] **Step 5: Commit** — `feat(pos): freeze company vat on the cash session`.

### Task 12: Migración de POS

**Files:** Create `src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/Migrations/<ts>_AddCashSessionCompanyChargesVat.cs`;
Test `tests/Modules/Pos/Modules.Pos.IntegrationTests/CashSessionChargesVatMigrationTests.cs` (precedente `CashSessionDocumentTypeMigrationTests`).

- [ ] **Step 1: Prueba**

```csharp
[Fact] AnOpenCashSessionBeforeTheMigrationKeepsVat         // caja sembrada antes → company_charges_vat = true (R4)
[Fact] ACashSessionWithoutVatKeepsItAfterReload            // abrir con empresa sin IVA → SELECT → false
```

- [ ] **Step 2: Genera** — `dotnet ef migrations add AddCashSessionCompanyChargesVat --project src/Modules/Pos/Modules.Pos.Infrastructure --context PosDbContext -o Persistence/Migrations`. Espera `AddColumn<bool>("company_charges_vat", schema: "pos", table: "cash_sessions", nullable: false, defaultValue: true)`. Cabecera R5.
- [ ] **Step 3: Build** → `0`.
- [ ] **Step 4: Commit** — `feat(pos): migrate company charges vat on cash sessions`.

**Fin del Bloque B:** una revisión del bloque.

---

## Bloque C — Siembra de tasas, arquitectura, barrido y README

### Task 13: Tasas por defecto de Colombia

**Files:**
- Create: `src/Modules/Companies/Modules.Companies.Application/ICompanyDefaultTaxRates.cs`.
- Modify: `src/Modules/Companies/Modules.Companies.Application/CreateCompany.cs` y `UpdateCompany.cs` (llamada después del commit,
  con `ILogger<…>` y `LoggerMessage` generado: `CA1873` rompe el build con un `LogWarning` interpolado).
- Create: `src/Bootstrapper/CompanyDefaultTaxRates.cs`; registro en `src/Bootstrapper/QepServiceCollectionExtensions.cs`
  (junto a `AddScoped<IQuotationCompanyLookup, QuotationCompanyLookup>()`, `:1879`).
- Test: `tests/ArchitectureTests/ArchitectureTests/CompaniesLayerTests.cs`,
  `tests/Modules/Companies/Modules.Companies.UnitTests/CreateCompanyDefaultTaxRatesTests.cs` (handler con doble del puerto),
  `tests/Modules/Companies/Modules.Companies.IntegrationTests/CompanyDefaultTaxRatesApiTests.cs` (host propio por prueba: lee
  `catalog.tax_rates` y `tenant_quotation_settings` del tenant).

**Interfaces:**
- Consumes: `Company.ChargesVat` (Tarea 1); `TaxRate.Create(TaxRateId id, Guid tenantId, string name, decimal percentage, string type, string country, decimal? minimumBase, Guid? cityId, DateTimeOffset occurredAt)`
  (`src/Modules/Catalog/Modules.Catalog.Domain/TaxRate.cs:136`); `TaxCountries.IvaType` (`BuildingBlocks.Domain/Taxes/TaxCountries.cs:42`);
  `ITaxRateRepository.ListAsync/Add`; `ICatalogAuditPublisher.Publish`; `IQuotationSettingsStore.GetFreightTaxRateIdAsync/SetFreightTaxRateIdAsync`
  (`IQuotationSettingsStore.cs:19,23`).
- Produces:

```csharp
// Companies.Application. Companies no ve Catalog ni Quotations: el adaptador vive en Bootstrapper (spec §5.3, R6).
public interface ICompanyDefaultTaxRates
{
    /// Idempotente. Crea IVA 19 %, IVA 5 % y Exento si el tenant no tiene ninguna tasa de IVA activa de CO,
    /// y fija la del envío si está vacía.
    Task EnsureColombianVatAsync(Guid tenantId, Guid? actorId, CancellationToken cancellationToken);
}
```

Reglas:
- Cuándo: al crear con `ChargesVat && Country == "CO"`; al editar, cuando después del guardado `ChargesVat && Country == "CO"` y
  antes no lo era (pasó a `true` o pasó a `CO`). Corre **después** de `SaveChangesAsync`. Un fallo (excepto
  `OperationCanceledException`) se loguea en `Warning` con el id del tenant y el tipo de la excepción, y la respuesta es la de la
  empresa (criterio del spec §5.3).
- Adaptador `internal sealed class CompanyDefaultTaxRates(CatalogDbContext catalog, QuotationsDbContext quotations, ITaxRateRepository rates, IQuotationSettingsStore settings, ICatalogAuditPublisher audit, IClock clock)`
  con `internal const int LockNamespace = 20261117;`. En una transacción de `catalog`: `SELECT pg_advisory_xact_lock({LockNamespace}, hashtext({tenantId.ToString()}))`
  (forma de dos claves, como `ShopifyTokenLock`); lee las tasas del tenant; si no hay ninguna `Type == "IVA" && Country == "CO" && IsActive`,
  crea `("IVA 19 %", 19m)`, `("IVA 5 %", 5m)`, `("Exento", 0m)` con `TaxCountries.IvaType`, `"CO"`, base y ciudad `null`,
  **saltando** la que ya exista con el mismo nombre y porcentaje (activa o no: `IX_tax_rates_tenant_name_percentage_base`), y
  publica `catalog.tax_rate.created` por cada una con `actorId`. Guarda `catalog`. Enrola `quotations` en la misma conexión y
  transacción (`SetDbConnection` + `UseTransactionAsync`, precedente `CatalogProductDeletionCleanup.cs:48-49`); si
  `GetFreightTaxRateIdAsync` es `null`, fija la de 19 % recién creada o, si no se creó ninguna, la activa de `CO` tipo IVA con 19.
  Guarda `quotations` y confirma. Tras el commit, nada más en el scope usa ese `QuotationsDbContext` (gotcha del `CLAUDE.md`).

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// CompaniesLayerTests
[Fact] TheDefaultTaxRatesPortLivesInApplicationAndItsAdapterInBootstrapper()
// typeof(ICompanyDefaultTaxRates).Assembly == typeof(ListCompaniesQuery).Assembly;
// typeof(QepServiceCollectionExtensions).Assembly.GetTypes() contiene un tipo no abstracto que implementa ICompanyDefaultTaxRates
// CreateCompanyDefaultTaxRatesTests (unidad)
[Fact] AColombianCompanyWithVatAsksForTheDefaultRates()       // doble anota 1 llamada con tenantId
[Fact] ACompanyWithoutVatDoesNotAskForThem()                  // 0 llamadas
[Fact] AForeignCompanyDoesNotAskForThem()                     // Country "ES" → 0
[Fact] AFailingSeedStillReturnsTheCompany()                   // el doble lanza → el handler devuelve el CompanyDto
[Fact] TurningVatOnAsksForTheRates()                          // Update false → true en CO → 1 llamada
```

```csharp
// CompanyDefaultTaxRatesApiTests
[Fact] TwoColombianCompaniesCreateThreeRatesNotSix
// dos POST seguidos → catalog.tax_rates del tenant: exactamente "IVA 19 %"/19, "IVA 5 %"/5, "Exento"/0, tipo IVA, país CO
[Fact] TheFreightRateIsTheNineteenPercentOne                  // tenant_quotation_settings.freight_tax_rate_id = id de "IVA 19 %"
[Fact] AnExistingFreightRateIsNotReplaced
[Fact] SeedingSkipsAnExistingRateWithTheSameNameAndPercentage // "IVA 19 %"/19 inactiva previa → 201; sólo se crean las otras dos
[Fact] ConcurrentCompaniesStillCreateThreeRates               // dos POST en paralelo → tres tasas
```

- [ ] **Step 2: RED** — `dotnet build` (`CS0246 ICompanyDefaultTaxRates`). Copia la salida.
- [ ] **Step 3: Implementa** con la firma y las reglas de arriba.
- [ ] **Step 4: Build** → `0`.
- [ ] **Step 5: Commit** — `feat(companies): seed default colombian vat rates for new companies`.

### Task 14: Barrido de pruebas por cuerpos crudos y README

**Files:**
- Modify: pruebas que afirman lo que dejó de ser verdad. Barre con
  `rg -n "bank_accounts_required|billing_account_missing|currency_mismatch|freight_tax_rate_missing|IsPending|bankAccounts|BillingCompanyPreview" tests`
  y con `rg -n "new QuotationBillingAccount\b|new QuotationBillingAccountRequest\(|new StoreBillingAccount\(|new ShopifyBillingAccount\(" tests`.
  Precedente directo: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/BillingCompanyPreviewApiTests.cs` (la vista
  previa con empresa ya no responde `billingAccount: null`). Las que siguen probando el caso **con** cuenta no se tocan.
- Modify: `README.md` — § «Pedidos: datos propios y pedido manual» (`:1694-1716`: «crear sí la exige» → la empresa sí, la cuenta
  no; moneda sin cuenta), § «Tienda pública» (`:1010+`: cada moneda elige empresa y la cuenta es opcional), § «Integración con
  Shopify» (`:4638-4650`: `billing_account_missing`/`currency_mismatch` sólo con cuenta; empresa sin IVA no espera por
  `freight_tax_rate_missing`), y una nota de «Migraciones con pods viejos y nuevos» que nombre este despliegue como no aditivo (R5).
  `docs/estado.md` **no**.

- [ ] **Step 1: Barre** y ajusta cada afirmación encontrada al contrato nuevo (§6 del spec), sin borrar la cobertura del caso con cuenta.
- [ ] **Step 2: Edita el README** con tuteo.
- [ ] **Step 3: Build** → `0`.
- [ ] **Step 4: Commit** — `test: sweep raw bodies for optional billing account` y `docs(readme): document optional billing account and company vat` (dos commits).

**Fin del Bloque C:** una revisión del bloque y después la revisión fuerte de toda la rama. La suite completa (proyectos de
Companies, Quotations, Store, Shopify, Pos, Catalog, Bootstrapper.UnitTests y ArchitectureTests, uno a la vez,
`-- xUnit.MaxParallelThreads=4`) la corre el owner antes de mergear a `develop`.

---

## Decisiones tomadas en este plan (para la revisión del owner)

- **`ChargesVat` vive en `QuotationBillingAccount`** (congelado con la cuenta, como `Country` y `CityId`), columna
  `billing_company_charges_vat` y no `charges_vat` como dice §5.2, por el prefijo de las demás columnas congeladas de la empresa. En
  `quotations.quotations` sale **nullable** (el owned es opcional) con relleno `true` donde hay empresa; en `orders`, `NOT NULL DEFAULT true`.
- **`chargesVat` ausente en el `PUT` de empresa conserva** el valor guardado (en el `POST`, ausente = `true`, como dice §5.1).
  Precedente: `WithholdingTaxRateIds` y `showPrices`. Un frontend viejo no puede prender el IVA que el nuevo apagó.
- **Moneda sin cuenta:** el `currency` de un request sin banco ni número se ignora; la decide el llamador (R3).
- **Envío al volver a IVA:** el agregado no inventa la tasa; el handler la pide al `ShippingResolver` sólo cuando hace falta.
- **Siembra:** salta las tasas que choquen por nombre + porcentaje en vez de reactivarlas; audita `catalog.tax_rate.created` con
  el actor que creó la empresa.
- **Shopify sin cuenta y empresa borrada:** `billing_account_missing` (sin motivo nuevo).
- **Opciones de la tienda:** una fila por empresa activa sin cuenta, con `currency: null`.

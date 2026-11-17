# Onboarding de la tienda de Shopify (backend): plan de implementación

> **Para agentes:** SUB-SKILL REQUERIDO: usa superpowers:subagent-driven-development (recomendado) o superpowers:executing-plans
> para ejecutar este plan tarea por tarea, **con las reglas de bloques de «Global Constraints»**, que mandan sobre el proceso del
> skill. Los pasos usan casillas (`- [ ]`).

**Goal:** Que una tienda recién enlazada se configure sola en cinco pasos con un BFF (`GET …/setup`) que dice qué falta, más
`POST …/setup/company`, `POST …/products/coverage/accept`, `connectedShops` en la instalación y el historial desde el asistente.

**Architecture:** Integrations amplía `ShopQuery` y guarda el **perfil de la tienda** como un campo interno JSON de la conexión
(`shopProfile`), que escriben el enlace y el probador. Shopify lo lee por `ShopifyConnectionRef.Profile` (mapeado en Bootstrapper).
El BFF vive en `Modules.Shopify.Application`: compone la tienda, las empresas, las membresías, las clasificaciones y lo del
trabajo 2 por puertos de `ShopifyPorts.cs` con adaptadores en `src/Bootstrapper/`, y calcula los pasos con una función pura
(`ShopifySetupSteps`). La empresa se crea con el caso de uso de Companies (`CreateCompanyCommand`) y se asigna a la tienda en una
transacción aparte; si la asignación pierde por versión, el 412 trae el `companyId` creado.

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, Admin API GraphQL de Shopify, xUnit + Testcontainers (sólo al final).

**Spec:** `docs/superpowers/specs/2026-11-17-onboarding-tienda-shopify-design.md` (§7 es vinculante). Contrato que consume el
frontend: `qep-frontend` `docs/superpowers/specs/2026-11-17-onboarding-tienda-shopify-frontend-design.md` (§5, §6, §12). Apilado
sobre `feature/productos-shopify` (plan `..\productos-shopify\docs\superpowers\plans\2026-11-17-productos-desde-shopify.md`), que a
su vez va sobre `feature/reglas-empresa` (plan `..\reglas-empresa\docs\superpowers\plans\2026-11-17-reglas-de-empresa.md`).

## Global Constraints

- **Dónde se escribe:** sólo en el worktree
  `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\onboarding-shopify` (rama
  `feature/onboarding-shopify`), con rutas absolutas. Las rutas de este plan son relativas a esa raíz. Nunca en `...\qep-backend`.
- **Bloques (owner, 2026-11-12):** tres bloques, **un implementador por bloque**, una revisión por bloque; la revisión fuerte de
  toda la rama corre una vez, al final. **Cada tarea conserva su propio commit.**
  - **Bloque A — Integrations: perfil de la tienda e instalación** (Tareas 1–3).
  - **Bloque B — Shopify: estado, puertos y pasos** (Tareas 4–7). Consume el perfil de A.
  - **Bloque C — BFF, escrituras y documentación** (Tareas 8–11). Consume B.
- **Pruebas escritas, no ejecutadas.** TDD: la prueba primero. El RED es la salida literal de `dotnet build` con el error de
  compilación (`CS0117`, `CS1061`, `CS7036`, `CS0246`…), copiada en el reporte del bloque. Si el fallo esperado es de
  comportamiento, la prueba queda escrita y su RED/GREEN lo corre el owner al final. **En esta rama no se corre `dotnet test`.**
- **Verificación de cada tarea:** desde la raíz del worktree, en PowerShell: `dotnet build Backend.slnx; $LASTEXITCODE` → `0` y
  `0 Error(s)`. Nada de `| Select-Object -Last` sin mirar `$LASTEXITCODE`. Si `Api.exe` (o `dotnet Api.dll`) corre, el build falla
  por archivo bloqueado: avisa, no lo detengas.
- **Al final (no por tarea):** el owner corre una vez todas las pruebas de los proyectos tocados: `Modules.Integrations.UnitTests`,
  `Modules.Integrations.IntegrationTests`, `Modules.Shopify.UnitTests`, `Modules.Shopify.IntegrationTests`,
  `Bootstrapper.UnitTests` y `ArchitectureTests`, uno a la vez, con `-- xUnit.MaxParallelThreads=4` y
  `$env:Seed__Enabled = "false"`.
- **Commits:** conventional commits en inglés, **sin atribución de IA ni `Co-Authored-By`**. Antes de cada commit,
  `git -C <worktree> branch --show-current` tiene que decir `feature/onboarding-shopify`.
- **Idioma:** comentarios y documentación en español colombiano, tuteando (nada de voseo). Identificadores, códigos, columnas y
  campos JSON no se traducen.
- **Capas:** `Modules.Shopify.Application` sólo ve Tenancy y Audit (`ShopifyLayerTests`): Companies, Customers, Integrations,
  Geography y Catalog entran por `ShopifyPorts.cs` con adaptador en `src/Bootstrapper/`. Un consumidor de Integrations nunca
  referencia `Modules.Integrations.Application` salvo Bootstrapper.
- **BFF:** las colecciones viajan completas, también vacías (`[]`, nunca ausentes); los enums con su nombre; el motivo de cada
  campo raro va en el XML doc del DTO.
- **Texto libre → FluentValidation** con claves camelCase en `errors`; ningún status HTTP se arma a mano.
- **Errores nuevos: ninguno** (spec §5). Se reutilizan `concurrency.conflict` (412), `shopify.store.billing_account_not_found`,
  `shopify.store.sync_unavailable`, `shopify.products.scope_missing` y los de Companies tal cual.
- **Migración aditiva** (spec §5): una sola, de Shopify, con el factory de diseño:
  `dotnet ef migrations add AddStoreSetup --project src/Modules/Shopify/Modules.Shopify.Infrastructure --context ShopifyDbContext -o Persistence/Migrations`.
- **Datos personales:** el correo y el teléfono del perfil de la tienda nunca van a logs, auditoría ni eventos (spec §2). Un log
  nuevo lleva el id de la conexión y el tipo de la excepción, con `[LoggerMessage]` (`CA1873`).
- **`docs/estado.md` no se toca** en esta rama.

## Decisiones de este plan (lo que el spec dejó abierto, §8)

- **D1. Perfil = campo interno `shopProfile` de la conexión** (JSON compacto, `maxLength: 4096`, `isInternal: true`), no una
  columna: los campos de la conexión ya son el lugar de `shopName` y no hace falta migración en Integrations. Cada texto se recorta a
  255. Lo escribe también el **enlace** (una llamada más a `GetShopAsync`, de mejor esfuerzo: si falla, conserva el del reenlace),
  para que el asistente tenga la sugerencia apenas se enlaza (spec §6, «tienda recién enlazada»).
- **D2. El BFF vive en Shopify.Application** (`ShopifySetup.cs`) con cuatro puertos nuevos o ampliados; ninguno nuevo en Integrations
  más allá del perfil.
- **D3. `blockedBy`** (spec §4.1 + H5): `advisor`, `buyers` y `products` → `["company"]` mientras la tienda **no tenga empresa**
  (`store.Billing is null`; el `PUT` y la importación la exigen). `buyers` suma `"advisor"` mientras el asesor no esté `Done` (el
  `PUT` exige un asesor activo). `history` → `["products"]` con una importación `Pending`/`Running`. Precedencia de `status`:
  `Done` > `Blocked` > `Pending`; `missing` se calcula igual en `Blocked`.
- **D4. `document_source_missing`** = la configuración nunca pasó por el `PUT` (`!store.IsConfigured`): las claves `Documento` y
  `Tipo de documento` son sugerencia hasta que alguien guarda.
- **D5. `POST …/setup/company`** exige `If-Match` (versión de la tienda): la versión se revisa **antes** de crear la empresa. Cuerpo
  con `taxId` (camelCase; el spec escribe `tax_id`), `country` y `cityName` (fuera de `CO`) agregados porque `CreateCompanyCommand`
  los exige. `201` al crear, `200` al sólo asignar; los dos con `{ companyId, storeVersion }`. Con `companyId` y `bankAccount`, la
  cuenta tiene que existir en esa empresa (no se agrega a una empresa existente). La creación pasa por el handler de Companies, así
  que también exige `companies.company.manage` (403 si falta) y siembra las tasas (trabajo 1).
- **D6. 412 con la empresa creada:** se quita el `sealed` de `RequestConcurrencyException` (precedente `ResourceConflictException`)
  y `ShopifySetupCompanyConflictException` agrega `details: { companyId }` por `IHasProblemExtensions`. `code` sigue
  `concurrency.conflict`.
- **D7. Moneda de la tienda sin cuenta:** `bankAccount.currency` → `Profile.CurrencyCode` → la de la cuenta guardada. Sin ninguna
  (Shopify no respondió al enlace ni al probador): 503 `shopify.store.sync_unavailable`.
- **D8. Cobertura en el BFF (H7):** `GET …/products/coverage` guarda la medición en `stores.products_coverage` (jsonb con
  `measuredAt`) sin subir `Version`. El BFF la mide sola, con tope de **15 s**, sólo si hay `read_products`, nunca hubo importación
  y no hay medición guardada; un fallo o el tope dan `coverage: null` sin romper la lectura.
- **D9. `suggestedChargesVat`** se pide a Shopify sólo mientras el paso `company` no esté `Done` y con `read_products`; cualquier
  falla es `null`.
- **D10. `connectedShops`** sólo se llena en los tenants con `canLink`; en los demás va `[]` (quien no administra integraciones no
  necesita ver las tiendas del tenant).
- **D11. `waiting`** agrupa sólo `wait_reason` de filas `Waiting` (no los avisos); la muestra de `product_unmatched` son hasta 5
  SKU distintos no nulos de `wait_detail->'skus'`.
- **D12. `sync?source=setup`:** el permiso sigue siendo `quotations.order.manage` (spec §7). `source` distinto de `setup` es 422
  `errors["source"]`. El resumen se guarda después de la corrida, sin subir `Version`; un 412 al guardarlo relee y reintenta una vez.

## Review Focus

1. **Asignación que pierde con 412 después de crear la empresa.** El reintento con `companyId` no crea otra. Lo pina
   `AnAssignmentConflictAfterCreatingReturnsTheCompanyId` (Tarea 10, integración) y
   `RetryWithTheCompanyIdDoesNotCreateAnother` (Tarea 10).
2. **Medir o aceptar la cobertura, o guardar el historial, no le da 412 al `PUT` de la tienda.** Ninguno sube `Version`. Lo pinan
   `RecordingCoverageDoesNotBumpTheVersion` (Tarea 4) y `ThePutStillSucceedsAfterCoverageWasMeasured` (Tarea 7).
3. **Shopify caído no tumba el BFF.** `suggestedChargesVat` y la cobertura inline caen a `null`. Lo pina
   `ShopifyUnavailableStillAnswersTheSetup` (Tarea 8).
4. **El perfil no filtra correo ni teléfono.** Ni en la respuesta de la conexión (campo interno) ni en logs. Lo pinan
   `TheShopProfileIsNotInTheConnectionResponse` (Tarea 2) y `TheSetupNeverLogsTheShopEmailOrPhone` (Tarea 8).
5. **Un perfil viejo o roto** (campo ausente, JSON inválido, `billingAddress` nulo, país no `CO`) da sugerencia con el nombre de la
   tienda y `cityId: null`, nunca 500. Lo pinan `ABrokenProfileIsReadAsNone` (Tarea 1) y
   `ASuggestionOutsideColombiaKeepsTheCityText` (Tarea 8).

---

### Task 0: Rebase sobre `feature/productos-shopify`

- [ ] **Step 1:** `git -C <worktree> rebase feature/productos-shopify` (rama local, ya rebasada sobre `feature/reglas-empresa`).
  Los commits propios son el del spec, el de su ajuste y el de este plan; no debe haber conflictos.
- [ ] **Step 2:** Verifica que existan, por los nombres de los planes de arriba. Si falta alguno, **para y avisa**: esta rama no
  los crea.
  - Reglas de empresa: `Company.ChargesVat`; `CreateCompanyCommand(..., bool? ChargesVat = null)`; `ShopifyBillingAccount(Guid
    CompanyId, string? BankName, string? AccountNumber, string Currency)`; `IShopifyBillingAccounts.FindCompanyAsync` →
    `ShopifyBillingCompanyRef(Guid CompanyId, bool ChargesVat)`; `ICompanyDefaultTaxRates`.
  - Productos: `ShopifyConnectionRef.Scopes`; `ShopifyScopes.CanReadProducts`; `IShopifyProductsSource` (`ReadShopAsync`,
    `ListVariantKeysAsync`); `IShopifyProductImportRepository.FindLatestAsync`; `IShopifyProductLinkRepository`;
    `ShopifyProductImportDto`, `ShopifyProductCoverageDto`, `ShopifyMissingVariantDto`; `IShopifyReauthorization`;
    `ShopifyProductEndpoints.cs`; `ShopifyStore.ProductsSyncedAt`.
- [ ] **Step 3:** `dotnet build Backend.slnx; $LASTEXITCODE` → `0`. Sin commit (el rebase no crea uno).

---

## Bloque A — Integrations: perfil de la tienda e instalación

### Task 1: `ShopQuery` con el perfil

**Files:**
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/Shopify/IShopifyAdminGateway.cs` (`ShopifyShopInfo` gana
  `Profile`; XML doc de `GetShopAsync`).
- Create: `src/Modules/Integrations/Modules.Integrations.Application/Shopify/ShopifyShopProfile.cs`.
- Modify: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Shopify/ShopifyAdminGateway.cs:24` (`ShopQuery`) y
  `GetShopAsync` (`:139-155`).
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/ShopifyAdminGatewayTests.cs`,
  `ShopifyShopProfileTests.cs` (nuevo).

**Interfaces (Produces):**

```csharp
// Integrations.Application/Shopify. Nada es secreto, pero Email, ContactEmail y Phone son datos personales: no van a logs.
public sealed record ShopifyShopAddress(string? Company, string? Address1, string? Address2, string? City, string? Province,
    string? ProvinceCode, string? Zip, string? CountryCode, string? Phone);
public sealed record ShopifyShopProfile(string? Email, string? ContactEmail, string? CurrencyCode, string? IanaTimezone,
    bool? TaxesIncluded, ShopifyShopAddress? BillingAddress)
{
    public const int FieldMaxLength = 4096;   // el maxLength del campo shopProfile
    public const int TextMaxLength = 255;
    /// JSON camelCase compacto; cada texto recortado a TextMaxLength; vacío → null.
    public string Serialize();
    /// null si el texto es null, vacío o no es un objeto JSON con esa forma (nunca lanza).
    public static ShopifyShopProfile? TryParse(string? json);
}
public sealed record ShopifyShopInfo(string Id, string Name, string Domain, ShopifyShopProfile? Profile = null);
```

`ShopQuery` exacto:
`{ shop { id name myshopifyDomain email contactEmail currencyCode ianaTimezone taxesIncluded billingAddress { company address1 address2 city province provinceCode zip countryCodeV2 phone } } }`.
`countryCodeV2` se guarda como `CountryCode`. Un campo ausente o de otro tipo es `null`; nunca `Invalid` por el perfil (sólo por
`id`, como hoy).

- [ ] **Step 1: Pruebas**

```csharp
// ShopifyAdminGatewayTests
[Fact] Task TheShopQueryAsksForEveryProfileField()     // el cuerpo enviado contiene billingAddress { … countryCodeV2 phone } y taxesIncluded
[Fact] Task GetShopReadsTheProfile()                   // respuesta fixture → Profile.CurrencyCode "COP", BillingAddress.City "Medellín", CountryCode "CO", TaxesIncluded true
[Fact] Task AShopWithoutBillingAddressHasANullAddress()
// ShopifyShopProfileTests
[Fact] void SerializeAndTryParseRoundTrip()
[Fact] void ABrokenProfileIsReadAsNone()               // "", "{", "[]", "null" → null
[Fact] void LongTextsAreCutTo255AndTheJsonFitsTheField() // 9 textos de 1000 → cada uno 255; Serialize().Length <= 4096
```

- [ ] **Step 2: RED** — `dotnet build` (`CS0246 ShopifyShopProfile`). Copia la salida.
- [ ] **Step 3: Implementa** con las firmas de arriba (`System.Text.Json`, `JsonSerializerDefaults.Web`, sin nulos al escribir).
- [ ] **Step 4: Build** → `0`. **Commit** — `feat(integrations): read the shopify shop profile`.

### Task 2: Guardar el perfil en la conexión

**Files:**
- Modify: `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationProviders.cs:48-59` (`ShopifyFieldKeys.ShopProfile =
  "shopProfile"`) y la definición de `Shopify` (`:224-266`): `new FieldDefinition(ShopifyFieldKeys.ShopProfile, "Perfil de la
  tienda", FieldKind.Text, required: false, maxLength: ShopifyShopProfile.FieldMaxLength, pattern: null, invalidMessage: "El perfil
  que devolvió Shopify no tiene una forma válida.", isInternal: true)`. Si el dominio no puede ver la constante de Application,
  usa el literal `4096` con un comentario que nombre la constante.
- Modify: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Shopify/ShopifyConnectionTester.cs:58-62` (un 200 también
  escribe `refreshed[ShopProfile] = info.Profile.Serialize()` si `Profile` no es `null`).
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/Shopify/LinkShopifyStore.cs` (después de
  `EnsureFreshInstallTokenAsync`, antes de armar `fields`): `gateway.GetShopAsync(shopDomain, accessToken)` atrapando
  `ShopifyGatewayException`; con perfil → `fields[ShopProfile]`; sin él, conserva el de `existing` como hace con
  `WebhookSubscriptionIds`.
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/ShopifyConnectionTesterTests.cs`,
  `LinkShopifyStoreHandlerTests.cs`, `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ShopifyLinkApiTests.cs`
  (y `FakeShopifyHandler.cs` responde el perfil en la consulta `shop`).

**Interfaces:** Consumes `ShopifyShopProfile` (Tarea 1). Produces `ShopifyFieldKeys.ShopProfile`.

- [ ] **Step 1: Pruebas**

```csharp
[Fact] Task TheTesterRefreshesTheShopProfile()            // refreshed["shopProfile"] parsea con CurrencyCode "COP"
[Fact] Task LinkStoresTheShopProfile()                    // fields["shopProfile"] presente
[Fact] Task ALinkWhoseShopReadFailsStillLinks()           // GetShopAsync lanza Unreachable → conexión creada, sin shopProfile
[Fact] Task ARelinkWhoseShopReadFailsKeepsTheOldProfile()
[Fact] Task TheShopProfileIsNotInTheConnectionResponse()  // integración: GET de la conexión no trae "shopProfile" ni el correo
```

- [ ] **Step 2: RED** — `dotnet build` (`CS0117 ShopifyFieldKeys.ShopProfile`). Copia la salida.
- [ ] **Step 3: Implementa.** Ningún log nuevo con el perfil.
- [ ] **Step 4: Build** → `0`. **Commit** — `feat(integrations): store the shop profile on the shopify connection`.

### Task 3: `connectedShops` en la instalación

**Files:**
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/Shopify/GetShopifyInstall.cs` (respuesta y handler; el
  handler recibe `IIntegrationConnectionRepository`).
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/GetShopifyInstallHandlerTests.cs`,
  `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ShopifyLinkApiTests.cs`.

**Interfaces (Produces):**

```csharp
/// <param name="ConnectedShops">Spec §3: las tiendas Shopify ya conectadas a ese tenant, para que nadie cruce tiendas. Sólo con
/// CanLink (D10); si no, []. Siempre presente.</param>
public sealed record ShopifyInstallTenantResponse(Guid TenantId, string Name, bool CanLink, bool ModuleEnabled,
    IReadOnlyList<ShopifyConnectedShopResponse> ConnectedShops);
/// ShopName: el campo shopName de la conexión o, sin él, su nombre. ShopDomain: el campo shopDomain.
public sealed record ShopifyConnectedShopResponse(string ShopName, string ShopDomain);
```

`repository.ListAsync(tenantId)` filtrado por `ProviderKey == "shopify"` y con `shopDomain`; orden por `ShopName` ordinal sin
mayúsculas. JSON: `tenants[i].connectedShops[j].shopName|shopDomain`. Sin cambios en `install/complete` ni en el reenlace (§7: el
enlace de hoy ya reutiliza la conexión del mismo tenant).

- [ ] **Step 1: Pruebas**

```csharp
[Fact] Task ATenantWithAShopListsIt()                   // connectedShops == [{ "Norte", "norte.myshopify.com" }]
[Fact] Task ATenantWithoutLinkPermissionGetsAnEmptyList()
[Fact] Task OtherProvidersAreNotListed()                // una conexión whatsapp-cloud no aparece
[Fact] Task TheInstallShowsTheShopAlreadyInTheTenant()  // integración: reenlace → la misma tienda en connectedShops del tenant
```

- [ ] **Step 2: RED** — `dotnet build` (`CS7036` en el constructor del record). Copia la salida.
- [ ] **Step 3: Implementa.**
- [ ] **Step 4: Build** → `0`. **Commit** — `feat(integrations): list connected shops per tenant in the shopify install`.

**Fin del Bloque A:** una revisión del bloque.

---

## Bloque B — Shopify: estado, puertos y pasos

### Task 4: Estado del asistente en la tienda

**Files:**
- Modify: `src/Modules/Shopify/Modules.Shopify.Domain/ShopifyStore.cs`.
- Create: `src/Modules/Shopify/Modules.Shopify.Domain/ShopifyStoreSetup.cs` (los records de abajo).
- Modify: `src/Modules/Shopify/Modules.Shopify.Infrastructure/Persistence/ShopifyDbContext.cs` (columnas de `stores`, `:242`).
- Create: `…/Persistence/Migrations/<ts>_AddStoreSetup.cs` (+ Designer, snapshot).
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifyStoreTests.cs`,
  `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifyPersistenceTests.cs`.

**Interfaces (Produces):**

```csharp
public sealed record ShopifyCoverageSample(string? Sku, string Title);
public sealed record ShopifyProductsCoverage(int ShopifyVariants, int Matched, bool CatalogEmpty,
    IReadOnlyList<ShopifyCoverageSample> MissingSample, DateTimeOffset MeasuredAt)
{ public bool IsComplete => ShopifyVariants > 0 && Matched >= ShopifyVariants; }
public sealed record ShopifyWaitingCount(string Reason, int Count);
public sealed record ShopifySetupHistorySummary(DateTimeOffset RanAt, int Days, int Fetched, int Imported,
    IReadOnlyList<ShopifyWaitingCount> Waiting);

// ShopifyStore
public ShopifyProductsCoverage? ProductsCoverage { get; private set; }
public DateTimeOffset? ProductsCoverageAcceptedAt { get; private set; }
public DateTimeOffset? SetupHistoryRanAt { get; private set; }
public ShopifySetupHistorySummary? SetupHistorySummary { get; private set; }
public void RecordProductsCoverage(ShopifyProductsCoverage coverage);              // sin Version++
public void AcceptProductsCoverage(DateTimeOffset now);                             // sin Version++; idempotente (conserva la primera)
public void RecordSetupHistory(ShopifySetupHistorySummary summary);                 // sin Version++; SetupHistoryRanAt = summary.RanAt
/// Spec §4.2: asigna la empresa (y la cuenta, si viene) sin tocar el resto. Sube Version y fija UpdatedBy/UpdatedAt.
public void AssignBilling(ShopifyBillingAccount billing, Guid updatedBy, DateTimeOffset now);
```

`AssignBilling` valida con `ValidatedBilling` (la del trabajo 1: banco y número, los dos o ninguno). Los tres que no suben versión
son estado del asistente, no configuración del asesor: si la subieran, el formulario abierto recibiría 412 (Review Focus 2).

Columnas (nombres exactos): `products_coverage jsonb NULL`, `products_coverage_accepted_at timestamptz NULL`,
`setup_history_ran_at timestamptz NULL`, `setup_history_summary jsonb NULL`. Los dos jsonb con `HasConversion` de
`System.Text.Json` (`JsonSerializerDefaults.Web`) y un `ValueComparer` por serialización. Migración **aditiva** (cabecera).

- [ ] **Step 1: Pruebas**

```csharp
[Fact] void RecordingCoverageDoesNotBumpTheVersion()
[Fact] void AcceptingTwiceKeepsTheFirstInstant()
[Fact] void AssignBillingBumpsTheVersionAndKeepsTheAdvisor()  // advisor previo intacto; Version + 1; Billing.CompanyId
[Fact] void AssignBillingWithOnlyTheAccountNumberThrows()     // ArgumentException
[Fact] void ACoverageWithEveryVariantMatchedIsComplete()      // (3,3) true; (0,0) false
[Fact] Task TheSetupStateRoundTrips()                         // integración: coverage con 2 muestras y summary con 2 motivos releídos iguales
```

- [ ] **Step 2: RED** — `dotnet build` (`CS0246 ShopifyProductsCoverage`). Copia la salida.
- [ ] **Step 3: Implementa** y genera la migración (espera cuatro `AddColumn` nullable en `shopify.stores` y nada más).
- [ ] **Step 4: Build** → `0`. **Commit** — `feat(shopify): persist store setup state`.

### Task 5: Puertos y adaptadores del asistente

**Files:**
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyPorts.cs` (`ShopifyConnectionRef`, `IShopifyMemberDirectory`,
  `IShopifyStoreReferences`, puerto nuevo de empresas).
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/IShopifyProductsSource.cs` (método nuevo) y
  `src/Modules/Shopify/Modules.Shopify.Infrastructure/Admin/ShopifyProductsClient.cs`.
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyRepositories.cs` y
  `…/Infrastructure/Persistence/ShopifyRepositories.cs` (mirror); `IShopifyProductRepositories.cs` y
  `…/Persistence/ShopifyProductRepositories.cs` (vínculos).
- Modify: `src/Bootstrapper/ShopifyConnectionDirectory.cs` (`Profile`), `ShopifyMemberDirectory.cs`, `ShopifyStoreReferences.cs`.
- Create: `src/Bootstrapper/ShopifySetupCompanies.cs`; registro en `QepServiceCollectionExtensions.cs` junto a los `Shopify*`.
- Test: `tests/ArchitectureTests/ArchitectureTests/ShopifyLayerTests.cs`, `CompositionRootTests.cs`,
  `tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifyProductsClientTests.cs`,
  `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifySetupAdaptersTests.cs` (nuevo, host propio).

**Interfaces (Produces):**

```csharp
// ShopifyConnectionRef: el perfil que guardó Integrations (D1); null si nunca se leyó o está roto. No entra en ToString.
public ShopifyShopProfile? Profile { get; init; }
// Shopify.Application: copia de forma del de Integrations (Shopify no lo ve); Bootstrapper mapea campo por campo.
public sealed record ShopifyShopProfile(string? Email, string? ContactEmail, string? CurrencyCode, bool? TaxesIncluded,
    ShopifyShopAddress? BillingAddress);
public sealed record ShopifyShopAddress(string? Company, string? Address1, string? Address2, string? City, string? Province,
    string? ProvinceCode, string? CountryCode, string? Phone);

// IShopifyMemberDirectory gana: las membresías activas del tenant con su nombre (NameAsync), en orden de nombre.
Task<IReadOnlyList<ShopifyMemberOption>> ListActiveAsync(Guid tenantId, CancellationToken ct);
public sealed record ShopifyMemberOption(Guid MembershipId, string Name);

// IShopifyStoreReferences gana: las clasificaciones activas del tenant (IsActive), en orden de nombre.
Task<IReadOnlyList<ShopifyNamedRef>> ListClassificationsAsync(Guid tenantId, CancellationToken ct);
public sealed record ShopifyNamedRef(Guid Id, string Name);

public sealed record ShopifySetupBankAccount(string BankName, string AccountNumber, string Currency);
public sealed record ShopifySetupCompanyRef(Guid Id, string Name, string TaxId, string DocumentType, string Country,
    bool ChargesVat, IReadOnlyList<ShopifySetupBankAccount> BankAccounts);
public sealed record ShopifySetupCompanyDraft(Guid TenantId, string Name, string TaxId, string? DocumentType, string Country,
    Guid? CityId, string? CityName, string? Address, string? Phone, string? Email, bool ChargesVat,
    ShopifySetupBankAccount? BankAccount);
/// Spec §4.2. Adaptador en Bootstrapper sobre Companies. CreateAsync corre ICommandHandler<CreateCompanyCommand, CompanyDto>
/// (autorización, validador, ciudad, siembra de tasas): sus excepciones salen tal cual (ValidationException con las claves de
/// Companies, CompaniesDomainException, RequestForbiddenException).
public interface IShopifySetupCompanies
{
    Task<IReadOnlyList<ShopifySetupCompanyRef>> ListActiveAsync(Guid tenantId, CancellationToken ct);   // SearchAsync(..., Active), por nombre
    Task<ShopifySetupCompanyRef?> FindActiveAsync(Guid tenantId, Guid companyId, CancellationToken ct);
    Task<ShopifySetupCompanyRef> CreateAsync(ShopifySetupCompanyDraft draft, CancellationToken ct);
}

// IShopifyProductsSource gana (D9): productVariants(first: 1, query: "taxable:true") { nodes { id } } → hay alguna.
Task<bool> HasTaxableVariantAsync(ShopifyConnectionRef connection, CancellationToken ct);

// IShopifyOrderMirrorRepository gana (D11): filas Waiting de la conexión, por motivo, con hasta 5 SKU para product_unmatched;
// orderIds null = todas; orden por Count desc y luego Reason ordinal.
Task<IReadOnlyList<ShopifyWaitingGroup>> SummarizeWaitingAsync(Guid tenantId, Guid connectionId,
    IReadOnlyCollection<long>? shopifyOrderIds, CancellationToken ct);
public sealed record ShopifyWaitingGroup(string Reason, int Count, IReadOnlyList<string> Sample);
Task<int> CountImportedSinceAsync(Guid tenantId, Guid connectionId, DateTimeOffset since, CancellationToken ct); // imported_at >= since

// IShopifyProductLinkRepository gana: productos distintos vinculados a la conexión.
Task<int> CountLinkedProductsAsync(Guid tenantId, Guid connectionId, CancellationToken ct);
```

Los dos `ShopifyShopProfile` (Integrations y Shopify) se llaman igual a propósito; en `ShopifyConnectionDirectory` distínguelos con
alias de `using`. `SummarizeWaitingAsync` es SQL sobre `shopify.orders` (`import_state = 'Waiting'`, `wait_reason`, muestra con
`jsonb_array_elements_text(wait_detail->'skus')` distintos y no nulos, `LIMIT 5`); filtra siempre por `tenant_id` y
`connection_id`.

- [ ] **Step 1: Pruebas**

```csharp
// ShopifyLayerTests
[Fact] void TheSetupCompaniesAdapterLivesInBootstrapper()   // implementación de IShopifySetupCompanies en el assembly de Bootstrapper
// CompositionRootTests
[Fact] void TheSetupPortsAreRegistered()                    // resuelve IShopifySetupCompanies
// ShopifyProductsClientTests
[Fact] Task TheTaxableQueryAsksForOneTaxableVariant()       // cuerpo con productVariants(first: 1, query: "taxable:true")
[Fact] Task NoTaxableVariantIsFalse()                       // nodes [] → false
// ShopifySetupAdaptersTests (integración)
[Fact] Task TheDirectoryReadsTheStoredProfile()             // conexión con shopProfile → Ref.Profile.BillingAddress.City
[Fact] Task ABrokenStoredProfileIsNull()
[Fact] Task WaitingIsGroupedByReasonWithFiveSkus()          // 7 product_unmatched con 6 SKU + 2 advisor_inactive → [(product_unmatched,7,5 SKU),(advisor_inactive,2,[])]
[Fact] Task WaitingOfAnotherConnectionIsNotCounted()
[Fact] Task ListActiveCompaniesSkipsInactiveOnes()
[Fact] Task CreateRunsTheCompaniesUseCase()                 // fila en companies.companies con charges_vat false; tasas sembradas si CO y true
[Fact] Task CreateWithoutCompanyManageIs403()
[Fact] Task InactiveClassificationsAreNotListed()
```

- [ ] **Step 2: RED** — `dotnet build` (`CS0246 IShopifySetupCompanies`). Copia la salida.
- [ ] **Step 3: Implementa.** `ShopifySetupCompanies.CreateAsync` arma el `CreateCompanyCommand` con `BankAccounts = draft.BankAccount
  is null ? [] : [new(bank, number, currency)]` y `ChargesVat = draft.ChargesVat`.
- [ ] **Step 4: Build** → `0`. **Commit** — `feat(shopify): add setup ports and bootstrapper adapters`.

### Task 6: Los pasos (función pura)

**Files:**
- Create: `src/Modules/Shopify/Modules.Shopify.Application/ShopifySetupSteps.cs`.
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifySetupStepsTests.cs`.

**Interfaces (Produces):**

```csharp
public static class ShopifySetupStepKeys { Company = "company", Advisor = "advisor", Buyers = "buyers", Products = "products",
    History = "history"; All (en ese orden) }
public static class ShopifySetupMissing { CompanyMissing = "company_missing", TaxIdMissing = "tax_id_missing",
    AdvisorMissing = "advisor_missing", AdvisorInactive = "advisor_inactive", ClassificationMissing = "classification_missing",
    DocumentSourceMissing = "document_source_missing", ImportNotDone = "import_not_done", ImportFailed = "import_failed",
    ScopeMissing = "scope_missing", HistoryNotRun = "history_not_run" }
public enum ShopifySetupStepStatus { Done, Pending, Blocked }
public sealed record ShopifySetupStep(string Key, ShopifySetupStepStatus Status, IReadOnlyList<string> BlockedBy, IReadOnlyList<string> Missing);
public sealed record ShopifySetupFacts(
    bool HasCompany,            // store.Billing no nulo
    bool CompanyActive,         // FindActiveAsync no nulo
    bool CompanyHasTaxId,       // TaxId no vacío tras Trim
    bool HasAdvisor, bool AdvisorActive,
    bool HasClassification, bool IsConfigured,
    bool ScopeGranted,
    ShopifyProductImportStatus? LastImportStatus,
    bool CoverageAccepted, bool CoverageComplete,
    bool HistoryRan);
public static class ShopifySetupSteps
{
    public static IReadOnlyList<ShopifySetupStep> Evaluate(ShopifySetupFacts facts);
    /// El primero que no está Done, o null (spec §4.1).
    public static string? OpenStep(IReadOnlyList<ShopifySetupStep> steps);
}
```

Reglas (spec §4.1 y D3–D4):
- `company`: Done ⇔ `HasCompany && CompanyActive && CompanyHasTaxId`. Missing: `company_missing` si `!HasCompany || !CompanyActive`;
  si no, `tax_id_missing`.
- `advisor`: Done ⇔ `HasAdvisor && AdvisorActive`. Missing `advisor_missing` / `advisor_inactive`. BlockedBy `["company"]` si
  `!HasCompany`.
- `buyers`: Done ⇔ `HasClassification && IsConfigured`. Missing, en este orden: `classification_missing`, `document_source_missing`.
  BlockedBy: `"company"` si `!HasCompany`, `"advisor"` si advisor no Done.
- `products`: Done ⇔ `LastImportStatus == Done || CoverageAccepted || CoverageComplete`. Missing (sólo uno): `scope_missing` sin
  scope; si no, `import_failed` con la última `Failed`; si no, `import_not_done`. BlockedBy `["company"]` si `!HasCompany`.
- `history`: Done ⇔ `HistoryRan`. Missing `history_not_run`. BlockedBy `["products"]` con `LastImportStatus` `Pending`/`Running`.
- Status: Done si cumple; si no, Blocked con `BlockedBy` no vacío; si no, Pending. Done lleva `Missing = []` y `BlockedBy = []`.

- [ ] **Step 1: Pruebas** (una por regla, con `[Theory]` donde haya combinaciones)

```csharp
[Fact] void AFreshStoreHasEveryStepPendingOrBlocked()        // todo false → company Pending [company_missing]; advisor/buyers/products Blocked [company]; history Pending
[Fact] void ACompanyWithoutTaxIdIsPendingTaxIdMissing()
[Fact] void AnInactiveCompanyIsCompanyMissing()
[Fact] void AnInactiveAdvisorIsAdvisorInactive()
[Fact] void BuyersWaitForTheAdvisor()                         // company ok, advisor missing → buyers Blocked ["advisor"]
[Theory] void BuyersMissingInOrder(bool classification, bool configured, string[] expected)
[Theory] void ProductsMissingByPrecedence(bool scope, ShopifyProductImportStatus? last, string expected)
[Theory] void ProductsDoneByAnyOfThree(ShopifyProductImportStatus? last, bool accepted, bool complete)
[Theory] void HistoryIsBlockedWhileAnImportIsLive(ShopifyProductImportStatus live)   // Pending, Running
[Fact] void ADoneStepNeverCarriesMissingOrBlockedBy()
[Fact] void TheOpenStepIsTheFirstNotDone()                    // company Done, advisor Pending → "advisor"; todos Done → null
```

- [ ] **Step 2: RED** — `dotnet build` (`CS0103 ShopifySetupSteps`). Copia la salida.
- [ ] **Step 3: Implementa.**
- [ ] **Step 4: Build** → `0`. **Commit** — `feat(shopify): evaluate store setup steps`.

### Task 7: Cobertura guardada y «Seguir así»

**Files:**
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyProductCoverage.cs` (del trabajo 2): extrae el cálculo a
  `ShopifyProductCoverageCalculator` y guarda la medición.
- Modify: `src/Modules/Shopify/Modules.Shopify.Api/ShopifyProductEndpoints.cs` (ruta nueva).
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyAuditActions.cs`
  (`ProductsCoverageAccepted = "shopify.products.coverage_accepted"`, recurso `ConnectionResourceType`).
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifyProductCoverageTests.cs`,
  `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifyProductsApiTests.cs`.

**Interfaces (Produces):**

```csharp
/// El cálculo de la cobertura del trabajo 2 (variantes de Shopify por ListVariantKeysAsync; matched por vínculo o SKU;
/// catalogEmpty; 20 de muestra), sin autorización ni guardado. Lo usan el GET de cobertura y el BFF.
public sealed class ShopifyProductCoverageCalculator(IShopifyProductsSource source, IShopifyProductLinkRepository links,
    IShopifyProductLookup products, IClock clock)
{ public Task<ShopifyProductsCoverage> MeasureAsync(ShopifyConnectionRef connection, CancellationToken ct); }

public sealed record AcceptShopifyProductCoverageCommand(Guid TenantId, Guid ConnectionId) : ICommand<Unit>; // o el tipo vacío que use el repo
```

```
POST /api/v1/tenants/{tenantId}/shopify/stores/{connectionId}/products/coverage/accept   integrations.connection.manage → 204
```

- El `GET …/products/coverage` mide, crea la tienda si no existe (`ShopifyStoreSupport.NewStore` + `AddNewAsync`, como el `GET`
  de la tienda), `RecordProductsCoverage` y guarda; un `concurrency.conflict` al guardar se descarta (la medición vuelve igual) y
  la respuesta no cambia de forma.
- Accept: `ShopifyAuthorization.EnsureAsync(…, ConnectionManage)` → conexión (403) → tienda (si no existe, la crea) →
  `AcceptProductsCoverage(now)` → auditoría `Human` con detalle `[]` → guardar → 204.

- [ ] **Step 1: Pruebas**

```csharp
[Fact] Task CoverageIsStoredOnTheStoreWithItsInstant()       // integración: tras el GET, stores.products_coverage.measuredAt == ahora del reloj
[Fact] Task ThePutStillSucceedsAfterCoverageWasMeasured()    // GET store (v) → GET coverage → PUT con If-Match v → 200
[Fact] Task AcceptReturns204AndRecordsTheInstant()
[Fact] Task AcceptOfAnotherTenantsConnectionIs403()
[Fact] Task AcceptWithReadPermissionOnlyIs403()
[Fact] Task TheCalculatorMatchesByLinkOrSku()                // unidad: lo que pinaba CoverageCountsLinkedAndSkuMatchedVariants, sobre el calculador
```

- [ ] **Step 2: RED** — `dotnet build` (`CS0246 ShopifyProductCoverageCalculator`). Copia la salida.
- [ ] **Step 3: Implementa.**
- [ ] **Step 4: Build** → `0`. **Commit** — `feat(shopify): store product coverage and accept it from setup`.

**Fin del Bloque B:** una revisión del bloque.

---

## Bloque C — BFF, escrituras y documentación

### Task 8: `GET …/setup` (BFF)

**Files:**
- Create: `src/Modules/Shopify/Modules.Shopify.Application/ShopifySetup.cs` (consulta, handler, sugerencia de empresa).
- Create: `src/Modules/Shopify/Modules.Shopify.Application/ShopifySetupDtos.cs`.
- Modify: `src/Modules/Shopify/Modules.Shopify.Api/ShopifyEndpoints.cs` (ruta).
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifySetupHandlerTests.cs`,
  `ShopifySetupSuggestionTests.cs`, `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifySetupApiTests.cs` (host propio
  por prueba cuando sustituya `IShopifyProductsSource`).

```
GET /api/v1/tenants/{tenantId}/shopify/stores/{connectionId}/setup?redirectUri=   integrations.connection.manage → 200 ShopifySetupDto
```

**DTO (nombres JSON exactos = camelCase de estos):**

```csharp
public sealed record ShopifySetupDto(
    ShopifySetupStoreDto Store,
    IReadOnlyList<ShopifySetupStepDto> Steps,          // siempre los cinco, en orden
    string? OpenStep,                                  // primer paso no Done; null si todos lo están
    ShopifySetupCompanySectionDto Company,
    IReadOnlyList<ShopifySetupCompanyOptionDto> Companies,   // empresas activas del tenant
    ShopifySetupAdvisorSectionDto Advisor,
    ShopifySetupBuyersSectionDto Buyers,
    ShopifySetupProductsSectionDto Products,
    ShopifySetupHistorySectionDto History,
    IReadOnlyList<ShopifyWaitingGroupDto> Waiting,
    ShopifySetupStatsDto Stats);

/// Settings es el mismo ShopifyStoreDto del GET de la tienda (H2): el cuerpo completo que el PUT necesita, con su version.
public sealed record ShopifySetupStoreDto(string Name, string? ShopDomain, string? Currency, string ConnectionStatus,
    ShopifyStoreDto Settings);
public sealed record ShopifySetupStepDto(string Key, string Status, IReadOnlyList<string> BlockedBy, IReadOnlyList<string> Missing);

public sealed record ShopifySetupCompanySectionDto(ShopifySetupCompanyOptionDto? Selected, ShopifySuggestedCompanyDto Suggested,
    bool? SuggestedChargesVat);
/// HasBankAccount: la tienda guarda una cuenta de esta empresa (sólo en Selected; en la lista, BankAccounts.Count > 0).
public sealed record ShopifySetupCompanyOptionDto(Guid Id, string Name, string? TaxId, string DocumentType, string Country,
    bool ChargesVat, bool HasBankAccount, IReadOnlyList<ShopifySetupBankAccountDto> BankAccounts);
public sealed record ShopifySetupBankAccountDto(string BankName, string AccountNumber, string Currency);
/// CityName es el texto de Shopify cuando la ciudad no se resolvió (o el país no es CO); CityId null en ese caso.
public sealed record ShopifySuggestedCompanyDto(string Name, string? Address, Guid? CityId, Guid? DepartmentId, string? CityName,
    string? Phone, string? Email, string? Country);

public sealed record ShopifySetupAdvisorSectionDto(Guid? SelectedMembershipId, Guid? DefaultMembershipId,
    IReadOnlyList<ShopifySetupMemberDto> Options);
public sealed record ShopifySetupMemberDto(Guid MembershipId, string Name, bool IsMe);

public sealed record ShopifySetupBuyersSectionDto(IReadOnlyList<string> DocumentSources, string DocumentAttributeKey,
    string DocumentTypeAttributeKey, IReadOnlyList<string> IdentificationTypes, string DefaultIdentificationType,
    Guid? DefaultClassificationId, IReadOnlyList<ShopifySetupClassificationDto> Classifications);
public sealed record ShopifySetupClassificationDto(Guid Id, string Name);

public sealed record ShopifySetupProductsSectionDto(bool ScopeGranted, string? ReauthorizeUrl, ShopifyProductImportDto? LastImport,
    ShopifySetupCoverageDto? Coverage, DateTimeOffset? CoverageAcceptedAt);
public sealed record ShopifySetupCoverageDto(int ShopifyVariants, int Matched, bool CatalogEmpty,
    IReadOnlyList<ShopifyMissingVariantDto> MissingSample, DateTimeOffset MeasuredAt);

public sealed record ShopifySetupHistorySectionDto(int? HistoryDays, ShopifySetupHistoryRunDto? LastRun);
public sealed record ShopifySetupHistoryRunDto(DateTimeOffset RanAt, int Days, int Fetched, int Imported,
    IReadOnlyList<ShopifyWaitingCountDto> Waiting);
public sealed record ShopifyWaitingCountDto(string Reason, int Count);
/// Sample: hasta 5 SKU para product_unmatched; [] para los demás motivos.
public sealed record ShopifyWaitingGroupDto(string Reason, int Count, IReadOnlyList<string> Sample);
public sealed record ShopifySetupStatsDto(int ImportedLast7Days, int ProductsSynced, DateTimeOffset? LastSyncAt);
```

Composición (en este orden):
1. `ShopifyAuthorization.EnsureAsync(…, ConnectionManage)` → conexión (`ShopifyStoreSupport.ConnectionAsync`, 403) → tienda
   (`stores.FindAsync` o crearla como `GetShopifyStoreHandler.CreateAsync`; extrae ese método a `ShopifyStoreSupport`).
2. `store.Name`/`ConnectionStatus` de `IShopifyTenantConnections.ListAsync`; `Currency` = `Profile?.CurrencyCode` ?? `Billing?.Currency`.
3. Empresas (`ListActiveAsync`), seleccionada = la de `store.Billing` (`FindActiveAsync`), `HasBankAccount = Billing.BankName is not null`.
4. Sugerencia (`ShopifySetupSuggestion.Build(ShopifyShopProfile? profile, string shopName, ShopifyCityLookup? city)`, pura):
   `Name` = `BillingAddress.Company` recortado ?? nombre de la tienda; `Address` = `Address1` + `", "` + `Address2` (los no vacíos);
   `Phone` = `BillingAddress.Phone`; `Email` = `ContactEmail ?? Email`; `Country` = `CountryCode`. Ciudad sólo con `CountryCode ==
   "CO"`: `IShopifyGeographyLookup.LookupCityAsync(ColombiaProvinceCodes.DepartmentNameFor(ProvinceCode) ?? Province, City)`; casó →
   `CityId`/`DepartmentId` y `CityName = null`; no casó o no es `CO` → `CityName = City`.
5. `SuggestedChargesVat` (D9): sólo si `company` no está Done, `ShopifyScopes.CanReadProducts(connection.Scopes)` y hay token:
   `ReadShopAsync` → `ProductsCount == 0` → `null`; si no, `HasTaxableVariantAsync`. Excepción (salvo cancelación del request) →
   `null` + `LogSuggestionUnavailable(connectionId, exceptionType)`.
6. Asesor: `ListActiveAsync`; `IsMe` por `IMembershipDirectory.FindActiveMembershipIdAsync(subject, tenant)`;
   `DefaultMembershipId = store.AdvisorMembershipId ?? me`.
7. Compradores: `ShopifyDocumentSources.Options(mirrors.ListAttributeKeysAsync(…, GetShopifyDocumentSourcesHandler.RecentOrders))`,
   `IdentificationTypes = ShopifyDocumentTypes.Supported`, `ListClassificationsAsync`.
8. Productos: `ScopeGranted`; `ReauthorizeUrl` sólo sin scope y con `redirectUri` (`IShopifyReauthorization.CreateUrlAsync`);
   `LastImport` con la misma composición que `GET …/products/import`; cobertura guardada, o medida en el acto (D8:
   `ShopifyProductCoverageCalculator` con un `CancellationTokenSource` de 15 s enlazado; éxito → `RecordProductsCoverage` y se
   guarda sin subir versión; falla o tope → `null` + log).
9. Historia: `store.HistoryDays`, `store.SetupHistorySummary`. Espera: `SummarizeWaitingAsync(…, null)`. Stats:
   `CountImportedSinceAsync(now − 7 días)`, `CountLinkedProductsAsync`, `store.LastReconciledAt`.
10. Pasos: `ShopifySetupSteps.Evaluate` con los hechos de arriba; `Status` y claves con su nombre.

- [ ] **Step 1: Pruebas**

```csharp
// unidad (dobles de los puertos)
[Fact] Task AFreshlyLinkedStoreIsAllPending()                 // steps como AFreshStoreHasEveryStepPendingOrBlocked; openStep "company"; colecciones [] no null
[Fact] Task TheSuggestionUsesTheBillingCompanyOrTheShopName()
[Fact] Task TheSuggestedCityIsResolvedInColombia()            // provinceCode "ANT", city "Medellín" → CityId del doble, CityName null
[Fact] Task ASuggestionOutsideColombiaKeepsTheCityText()      // "MX" → CityId null, CityName "Monterrey", lookup sin llamadas
[Fact] Task WithoutReadProductsTheVatSuggestionIsNull()       // fuente sin llamadas
[Fact] Task AShopWithoutProductsHasANullVatSuggestion()
[Fact] Task ADoneCompanyDoesNotAskShopifyForVat()
[Fact] Task ShopifyUnavailableStillAnswersTheSetup()          // fuente lanza Unavailable → 200, suggestedChargesVat null, coverage null
[Fact] Task TheCoverageIsMeasuredOnceWhenThereIsNoImport()    // segunda lectura usa la guardada: calculador 1 vez
[Fact] Task TheDefaultAdvisorIsTheCaller()                    // sin asesor → defaultMembershipId == membresía del llamador; isMe true sólo en ella
[Fact] Task WithoutRedirectUriTheReauthorizeUrlIsNull()
[Fact] Task TheSetupNeverLogsTheShopEmailOrPhone()            // logger de prueba: ningún mensaje contiene el correo ni el teléfono del perfil
// integración
[Fact] Task GetSetupOfANewStoreCreatesItAndAnswersPending()   // ShopifyApiHarness: conexión con shopProfile CO → 200, suggested.cityId no null
[Fact] Task TheStoreSettingsCarryTheVersionThePutNeeds()      // store.settings.version == ETag del GET de la tienda
[Fact] Task WaitingGroupsTheStoreOrdersByReason()
[Fact] Task ReadPermissionOnlyIs403()
[Fact] Task AnotherTenantsConnectionIs403()
```

- [ ] **Step 2: RED** — `dotnet build` (`CS0246 GetShopifySetupQuery`). Copia la salida.
- [ ] **Step 3: Implementa.** El handler no escribe nada salvo crear la tienda y guardar la cobertura medida (D8).
- [ ] **Step 4: Build** → `0`. **Commit** — `feat(shopify): add the store setup bff`.

### Task 9: `RequestConcurrencyException` con datos

**Files:**
- Modify: `src/BuildingBlocks/BuildingBlocks.Application/ApplicationExceptions.cs` (quita `sealed` de
  `RequestConcurrencyException`, con el comentario del precedente `ResourceConflictException`).
- Create: `src/Modules/Shopify/Modules.Shopify.Application/ShopifySetupCompanyConflictException.cs`.
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifySetupCompanyConflictExceptionTests.cs`.

**Interfaces (Produces):**

```csharp
/// Spec §7: la empresa quedó creada y la asignación perdió por versión. El 412 lleva details.companyId para que el reintento
/// la mande y no cree otra. Code: concurrency.conflict.
public sealed class ShopifySetupCompanyConflictException(Guid companyId, Exception? inner = null)
    : RequestConcurrencyException("concurrency.conflict", "The store changed after it was loaded; the company was created.", inner),
      IHasProblemExtensions
{ public Guid CompanyId { get; } public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } } // { ["details"] = new { companyId } }
```

- [ ] **Step 1: Pruebas**

```csharp
[Fact] void TheConflictCarriesTheCompanyIdInDetails()   // ProblemExtensions["details"] serializado == {"companyId":"…"}
[Fact] void ItIsStillAConcurrencyConflict()             // is RequestConcurrencyException, Code "concurrency.conflict"
```

- [ ] **Step 2: RED** — `dotnet build` (`CS0509` al heredar de una clase sellada). Copia la salida.
- [ ] **Step 3: Implementa.**
- [ ] **Step 4: Build** → `0`. **Commit** — `feat(shopify): carry the created company on a setup conflict`.

### Task 10: `POST …/setup/company`

**Files:**
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifySetup.cs` (comando, validador, handler).
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyStores.cs` (extrae `ComparedFields`/`ChangedFieldNamesJson` a
  `ShopifyStoreSupport` para auditar igual que el `PUT`).
- Modify: `src/Modules/Shopify/Modules.Shopify.Api/ShopifyEndpoints.cs` (ruta, `RequireVersion`).
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifySetupCompanyHandlerTests.cs`,
  `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifySetupCompanyApiTests.cs`.

```
POST /api/v1/tenants/{tenantId}/shopify/stores/{connectionId}/setup/company   integrations.connection.manage, If-Match obligatorio
{ "companyId"?: guid, "name"?, "taxId"?, "documentType"?, "country"?, "cityId"?, "cityName"?, "address"?, "phone"?, "email"?,
  "chargesVat"?: bool, "bankAccount"?: { "bankName", "accountNumber", "currency" } }
→ 201 (creó) | 200 (asignó)  { "companyId": guid, "storeVersion": long }  + ETag con storeVersion
```

**Interfaces (Produces):**

```csharp
public sealed record ShopifySetupBankAccountInput(string? BankName, string? AccountNumber, string? Currency);
public sealed record AssignShopifySetupCompanyCommand(Guid TenantId, Guid ConnectionId, Guid? CompanyId, string? Name,
    string? TaxId, string? DocumentType, string? Country, Guid? CityId, string? CityName, string? Address, string? Phone,
    string? Email, bool? ChargesVat, ShopifySetupBankAccountInput? BankAccount, long ExpectedVersion)
    : ICommand<ShopifySetupCompanyResultDto>;
/// Created decide 201/200 en el endpoint y no viaja.
public sealed record ShopifySetupCompanyResultDto(Guid CompanyId, long StoreVersion) { [JsonIgnore] public bool Created { get; init; } }
```

Validador (claves camelCase; sólo la forma, el resto lo valida Companies y sus `errors` salen con sus propias claves):
- Sin `companyId`: `name` y `taxId` requeridos y no vacíos; `country` dos letras; `chargesVat` no nulo («Elige si cobras IVA.»).
- `companyId`, si viene, no `Guid.Empty`.
- `bankAccount`, si viene: `bankAccount.bankName` (≤ `ShopifyBillingAccount.BankNameMaxLength`), `bankAccount.accountNumber`
  (≤ `AccountNumberMaxLength`) requeridos; `bankAccount.currency` tres letras.
- `expectedVersion` > 0.

Handler, en orden:
1. `EnsureAsync(…, ConnectionManage)` → validador → conexión (403) → tienda (o `NewStore`); `Version != ExpectedVersion` → 412
   `concurrency.conflict` (**antes de crear nada**) → `updatedBy` = membresía activa (403 si no).
2. Con `companyId`: `FindActiveAsync` (`null` → 422 `shopify.store.billing_account_not_found`); con `bankAccount`, la cuenta tiene que
   estar en esa empresa con esa moneda (`IShopifyBillingAccounts.FindAsync`; si no, el mismo código). Sin `companyId`:
   `CreateAsync(draft)`; `created = true`.
3. Moneda (D7): `bankAccount.currency` → `connection.Profile?.CurrencyCode` → `store.Billing?.Currency`; ninguna → 503
   `shopify.store.sync_unavailable`. **Si se va a crear**, esta resolución corre antes del paso 2.
4. `store.AssignBilling(new ShopifyBillingAccount(companyId, bank?, number?, currency), updatedBy, now)`; si es nueva,
   `AddNewAsync`; auditoría `shopify.store.updated` con los nombres de campo cambiados (nunca valores); guardar.
5. `RequestConcurrencyException` con `concurrency.conflict` al guardar: con `created` → `ShopifySetupCompanyConflictException(companyId)`;
   sin él, se relanza.

- [ ] **Step 1: Pruebas**

```csharp
// unidad
[Fact] Task WithoutCompanyIdItCreatesAndAssigns()              // CreateAsync 1 vez con ChargesVat false; Billing.CompanyId == creada; Created true
[Fact] Task WithCompanyIdItOnlyAssigns()                       // CreateAsync 0 veces; Created false
[Fact] Task RetryWithTheCompanyIdDoesNotCreateAnother()
[Fact] Task AStaleVersionIs412BeforeCreatingTheCompany()       // CreateAsync 0 veces
[Fact] Task AnAssignmentConflictAfterCreatingReturnsTheCompanyId() // guardado lanza concurrency.conflict → ShopifySetupCompanyConflictException.CompanyId == creada
[Fact] Task AnUnknownCompanyIsBillingAccountNotFound()
[Fact] Task ABankAccountNotInTheExistingCompanyIsBillingAccountNotFound()
[Fact] Task WithoutAnyCurrencyIs503SyncUnavailable()           // sin bankAccount, perfil sin moneda, tienda sin cuenta
[Fact] Task TheStoreTakesTheShopCurrencyWithoutAccount()       // Profile.CurrencyCode "COP" → Billing.Currency "COP", BankName null
[Fact] Task MissingChargesVatOnCreateIsAValidationError()      // errors["chargesVat"]
[Fact] Task OnlyTheBankNameIsAValidationError()                // errors["bankAccount.accountNumber"]
// integración
[Fact] Task PostCreatesTheCompanyWithTheAccountAndAssignsIt()  // 201; companies.companies con la cuenta; GET store: billingCompanyId y billingBankName
[Fact] Task PostWithoutIfMatchIs428()
[Fact] Task ACompaniesValidationErrorComesBackWithItsKeys()    // NIT inválido para CO → 422 validation.failed con la clave de Companies
[Fact] Task WithoutCompanyManageTheCreationIs403()
[Fact] Task AfterAssigningTheCompanyStepIsDone()               // GET setup → steps[company].status "Done"; advisor ya no Blocked
```

- [ ] **Step 2: RED** — `dotnet build` (`CS0246 AssignShopifySetupCompanyCommand`). Copia la salida.
- [ ] **Step 3: Implementa.**
- [ ] **Step 4: Build** → `0`. **Commit** — `feat(shopify): create or assign the store company from setup`.

### Task 11: Historial desde el asistente y documentación

**Files:**
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/SyncShopifyStore.cs` (`Source`, validador, guardado del resumen).
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ReconcileStore.cs:340-406` (`Run` anota los ids que quedaron `Waiting`;
  `ShopifySyncResult` gana `[JsonIgnore] IReadOnlyList<long> WaitingOrderIds { get; init; } = []`).
- Modify: `src/Modules/Shopify/Modules.Shopify.Api/ShopifyEndpoints.cs:124-126` (`[FromQuery] string? source`).
- Modify: `README.md` § Integración con Shopify (rutas nuevas, `shopProfile`, migración `AddStoreSetup` aditiva, verificación contra
  una tienda de desarrollo de `billingAddress`, `contactEmail` y `productVariants(query: "taxable:true")`) y `CLAUDE.md` (gotchas).
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/ReconcileStoreTests.cs`, `ShopifyStoreHandlersTests.cs`,
  `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifySyncApiTests.cs`.

**Interfaces (Produces):**

```csharp
public const string SetupSource = "setup";   // en SyncShopifyStoreCommand
public sealed record SyncShopifyStoreCommand(Guid TenantId, Guid ConnectionId, string? Source = null) : ICommand<ShopifySyncResult>;
```

`POST …/sync?source=setup`: la respuesta no cambia (`ShopifySyncResult`, mismo permiso `quotations.order.manage`). Con `setup`,
después de `RunAsync`: relee la tienda, `RecordSetupHistory(new(now, store.HistoryDays ?? 0, result.Fetched, result.Imported,
SummarizeWaitingAsync(…, result.WaitingOrderIds) → (Reason, Count)))` y guarda; un `concurrency.conflict` descarta, relee y
reintenta **una** vez (D12). `source` distinto de `null`/`setup` → 422 `errors["source"]` antes de correr nada.

Gotchas de `CLAUDE.md` (cortos, en tuteo):
1. El perfil de la tienda (`shopProfile`) es un campo **interno** de la conexión con correo y teléfono del comerciante: nada lo
   loguea ni lo audita, y un campo nuevo de `ShopQuery` entra en `ShopifyShopProfile` de los dos módulos (Bootstrapper los mapea).
2. `products_coverage`, `products_coverage_accepted_at` y `setup_history_*` no suben `Version` de `shopify.stores`: si un camino
   nuevo del asistente la sube, el formulario de la tienda abierto recibe 412.
3. `POST …/setup/company` crea la empresa en su propia transacción: el 412 de la asignación trae `details.companyId`, y el
   reintento lo manda. No lo «arregles» borrando la empresa.

- [ ] **Step 1: Pruebas**

```csharp
[Fact] void TheRunReportsTheOrdersLeftWaiting()               // ReconcileStoreTests: 3 traídos, 1 Waiting → WaitingOrderIds == [id]
[Fact] Task ASyncFromSetupStoresTheSummary()                  // unidad: RecordSetupHistory con Days 7, Fetched, Imported y [(product_unmatched,1)]
[Fact] Task ASyncWithoutSourceDoesNotTouchTheSetup()
[Fact] Task AnUnknownSourceIs422()                            // errors["source"]; RunAsync sin llamadas
[Fact] Task TheSyncResponseKeepsItsShape()                    // integración: JSON con fetched/imported/waiting/cancelled/ignored/durationMs y sin waitingOrderIds
[Fact] Task AfterASetupSyncTheHistoryStepIsDone()             // integración: GET setup → history Done, history.lastRun.days == 7
```

- [ ] **Step 2: RED** — `dotnet build` (`CS1739 source` en el comando). Copia la salida.
- [ ] **Step 3: Implementa** y documenta.
- [ ] **Step 4: Build** → `0`. **Commit** — `feat(shopify): record the setup history run and document onboarding`.

**Fin del Bloque C:** una revisión del bloque y, después, la revisión fuerte de toda la rama. Luego el owner corre las pruebas de
los proyectos tocados (Global Constraints).

---

## Self-review

- **Cobertura del spec:** §2 → T1, T2, T5 (`Profile`); §3 → T3 (el `shopName` de `install/complete` ya existe: el frontend lo
  confirma en su §4.1); §4.1 → T5, T6, T8; §4.2 empresa → T9, T10; asesor y compradores → `PUT` de hoy (sin cambios); productos →
  T7; historial → T11; §4.3 → T5 (`HasTaxableVariantAsync`), T8; §5 → T4, T9 (sin códigos nuevos); §6 → pruebas de T6, T8, T10, T11;
  §7 H3 → T10 (`bankAccount`); H5 → T6 (D3); H7 → T7, T8 (D8); cuerpo de la tienda → T8 (`store.settings`); estadísticas → T5, T8;
  sincronización → T11; empresa creada y asignación fallida → T9, T10; volver tras `read_products` → T3 (`connectedShops`) sin cambio
  de enlace.
- **Tipos:** `ShopifyProductsCoverage` (T4) lo producen T7 y T8; `ShopifyWaitingGroup` (T5) lo consumen T8 y T11;
  `ShopifySetupFacts`/`ShopifySetupSteps` (T6) los usa T8 y los verifican T10/T11 por la API; `ShopifySetupCompanyConflictException`
  (T9) la lanza T10; `IShopifySetupCompanies` (T5) lo usan T8 y T10.
- **Fuera de alcance a propósito:** el rol del usuario por tenant (H1), que `install/complete` diga que es una reautorización
  (H6), avisos (`warnings`) en `waiting`, la página `/setup` general y la app embebida (no objetivos del spec).

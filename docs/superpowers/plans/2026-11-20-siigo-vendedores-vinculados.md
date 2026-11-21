# Siigo: vendedores vinculados — Implementation Plan (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. The budget rules of `qep/CLAUDE.md` override the skill: two blocks, one implementer each.

**Goal:** El vendedor de cada factura sale de un vínculo asesor (membresía) → usuario de Siigo, configurado en un paso nuevo «Vendedores»; facturar deja de leer `/v1/users`.

**Architecture:** Vínculos `SiigoSellerLink` en el agregado `SiigoSettings` (tabla `siigo.seller_links`), paso `Sellers` en la readiness, `PUT …/sellers` y `POST …/sellers/auto-link` como los demás pasos, y un puerto `ISiigoMemberDirectory` con adaptador en Bootstrapper para listar miembros. El pedido viaja con `AdvisorMembershipId` y el builder toma el vendedor del vínculo.

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, xUnit v3, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-20-siigo-vendedores-vinculados-design.md`

**Worktree:** `qep-backend-worktrees/siigo-links`, rama `feature/siigo-seller-links`.

## Global Constraints

- Faltante: `seller_unlinked`, `Reference = membershipId.ToString()`, `Label = AdvisorLabel` (`DisplayName ?? Email`, puede ser null). Sale `seller_not_found`.
- Readiness: `SiigoSetupStep.Sellers` entre `Payments` y `Products`; código `seller_link_missing` sin ningún vínculo.
- Tabla `siigo.seller_links`: `connection_id`, `tenant_id`, `membership_id`, `siigo_user_id varchar(50)`, `siigo_user_name varchar(200)`; PK `PK_seller_links (connection_id, membership_id)`. Migración `AddSiigoSellerLinks`, aditiva.
- `SiigoSellerResolver` **se queda**, sólo para `auto-link` (desvío del §5 del spec, que lo quitaba: es justo el cruce por correo que el auto-link necesita). Facturar no lo usa.
- Preview y emisión **no** llaman a Siigo para el vendedor. La emisión vuelve a armar antes de pedir credenciales.
- Error nuevo: `siigo.settings.member_not_found` (422).
- Comentarios en español (por qué). Conventional commits, sin atribución de IA. `git branch --show-current` antes de cada commit.
- Integración filtrada por nombre; nunca dos corridas a la vez (mirar `Get-Process testhost, dotnet`). La suite completa la corre el owner.

## Review Focus

- Un asesor que salió del tenant y tenía vínculo → el `GET` lo lista con `active: false`, el `PUT` acepta mantenerlo o quitarlo, y sus pedidos siguen facturando con ese vínculo. Prueba en Task 3.
- `PUT …/sellers` con una membresía de otro tenant → 422 `member_not_found`, nada guardado. Prueba en Task 3.
- Auto-link no pisa un vínculo existente aunque el correo apunte a otro usuario. Prueba en Task 4.
- Pedido cuyo asesor no resuelve (membresía borrada) → `seller_unlinked` con `Label` null, sin excepción. Prueba en Task 2.
- Preview con asesor sin vínculo no hace ninguna llamada a `/v1/users`. Prueba en Task 5.

---

## Bloque A — Dominio, persistencia y facturación (unitarias)

### Task 1: Vínculo de vendedor en el agregado, readiness y migración

**Files:**
- Create: `src/Modules/Siigo/Modules.Siigo.Domain/SiigoSellerLink.cs` (calca `SiigoTaxLink.cs`)
- Modify: `src/Modules/Siigo/Modules.Siigo.Domain/SiigoSettings.cs` (lista, `ReplaceSellerLinks`, `SellerFor`)
- Modify: `src/Modules/Siigo/Modules.Siigo.Domain/SiigoSettingsReadiness.cs` (enum y paso)
- Modify: `src/Modules/Siigo/Modules.Siigo.Infrastructure/Persistence/SiigoDbContext.cs` (`ConfigureSellerLink`, navegación del agregado como `TaxLinks`), y el repositorio si carga los vínculos con `Include`
- Create: migración `AddSiigoSellerLinks`
- Test: `SiigoSettingsTests.cs`, `SiigoSettingsReadinessTests.cs`, `SiigoDbContextMappingTests.cs`

**Interfaces:**
- Produces: `public sealed class SiigoSellerLink { SiigoSellerLink(Guid membershipId, string siigoUserId, string siigoUserName); Guid ConnectionId; Guid TenantId; Guid MembershipId; string SiigoUserId; string SiigoUserName; const int UserIdMaxLength = 50; const int UserNameMaxLength = 200; void Relink(string siigoUserId, string siigoUserName) }`.
- Produces: `IReadOnlyList<SiigoSellerLink> SiigoSettings.SellerLinks`, `void ReplaceSellerLinks(IReadOnlyList<SiigoSellerLink>)` (con `Sync` y `Touch`, repetido → `siigo.settings.duplicate_link`), `string? SellerFor(Guid membershipId)`.
- Produces: `SiigoSetupStep.Sellers`; `SiigoSettingsReadiness.SellerLinkMissing = "seller_link_missing"`.

- [ ] **Step 1: Tests que fallan** — `ReplaceSellerLinksReplacesTheStepAndSellerForFindsIt` (dos vínculos, luego uno: queda uno y `SellerFor(otro)` es null); `ARepeatedMemberIsADuplicateLink` (`SiigoDomainException` con `siigo.settings.duplicate_link`); readiness `WithoutSellerLinksTheSellersStepNamesWhatIsMissing` (`["seller_link_missing"]`, completo con un vínculo) y el orden de pasos con `Sellers` antes de `Products` (ajustar `WithEverythingTheSixStepsAreComplete` → siete pasos); `SiigoDbContextMappingTests.TheSellerLinksMapToTheirTable` (`seller_links`, schema `siigo`, PK `ConnectionId, MembershipId`).
- [ ] **Step 2:** `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests --filter "FullyQualifiedName~SiigoSettings|FullyQualifiedName~SiigoDbContextMappingTests"` → falla de compilación.
- [ ] **Step 3: Implementar.** Mapeo calcado de `ConfigureTaxLink`, con índice `IX_seller_links_tenant_connection`. Migración: `dotnet ef migrations add AddSiigoSellerLinks --project src/Modules/Siigo/Modules.Siigo.Infrastructure --context SiigoDbContext -o Persistence/Migrations` (la factory de diseño del módulo; `src/Api` no tiene EF Design). Revisar que `Up` sólo cree la tabla.
- [ ] **Step 4:** misma corrida → PASS, incluido `TheModelHasNoChangesPendingAMigration`.
- [ ] **Step 5: Commit** `feat(siigo): link each advisor to a Siigo user`

### Task 2: Facturar con el vínculo

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IExternalOrderInvoicing.cs` y `ExternalOrderInvoicingService.cs` (`AdvisorEmail` → `Guid AdvisorMembershipId` + `string? AdvisorLabel`)
- Modify: `src/Bootstrapper/SiigoOrderSource.cs`
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/SiigoInvoiceInput.cs`, `SiigoInvoiceBuilder.cs`, `SiigoMissing.cs`, `PreviewSiigoInvoice.cs`, `IssueSiigoInvoice.cs`, `SiigoInvoiceSupport.cs`
- Test: `SiigoInvoiceBuilderTests.cs`, `InvoiceTestData.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/SiigoOrderSourceTests.cs`, `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderExternalInvoicingTests.cs`

**Interfaces:**
- Consumes: `SiigoSettings.SellerFor` (Task 1).
- Produces: `ExternalInvoicingOrder(..., long Version, Guid AdvisorMembershipId, string? AdvisorLabel)`; `SiigoInvoiceInput(..., long OrderVersion, Guid AdvisorMembershipId, string? AdvisorLabel)`.
- Produces: `SiigoInvoiceBuilder.Build(SiigoInvoiceInput, SiigoSettings, IReadOnlyList<SiigoPaymentOverride>?)` (sin `sellerId`); `SiigoMissing.SellerUnlinked = "seller_unlinked"`.

- [ ] **Step 1: Tests que fallan** — builder: `TheSellerIsTheLinkedSiigoUser` (vínculo del asesor → `"seller": 744`), `AnUnlinkedAdvisorIsNotReady` (`new SiigoMissing("seller_unlinked", <membershipId>, "Andrea Ritual")`), `AnAdvisorThatNoLongerResolvesIsUnlinkedWithoutLabel` (`AdvisorLabel: null` → `Label` null), `TheUnlinkedSellerIsReportedWithTheOtherMissing`; `SiigoOrderSourceTests.TheAdvisorTravels` (id y etiqueta). `InvoiceTestData.Settings(...)` vincula por defecto el asesor de `Order()` a `"629"`.
- [ ] **Step 2:** `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests --filter "FullyQualifiedName~SiigoInvoiceBuilderTests"` → falla de compilación.
- [ ] **Step 3: Implementar.** Servicio de Quotations: `AdvisorMembershipId = order.AdvisorId.Value`, `AdvisorLabel = advisor?.Label`. Builder: `settings.SellerFor(input.AdvisorMembershipId)`; quitar el parámetro `sellerId`. Preview: sin credenciales ni usuarios, `<summary>` vuelve a «sin escribir nada y sin llamar a Siigo». Issue: `Build` antes de `CredentialsAsync`, como estaba antes del 2026-11-20. Quitar `ResolveSellerAsync`; quitar `SetupCredentialsAsync` si nadie más la usa. Borrar los tests de builder que pasaban `sellerId`.
- [ ] **Step 4:** `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests` y `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~SiigoOrderSourceTests"` y `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderExternalInvoicingTests"` → PASS; `dotnet build Backend.slnx` sin errores.
- [ ] **Step 5: Commit** `feat(siigo): take the invoice seller from the advisor link`

**Fin del Bloque A:** `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests` completo → PASS. Una revisión del bloque.

---

## Bloque B — Paso «Vendedores» por HTTP e integración

### Task 3: Miembros, `GET settings` con `sellers` y `PUT …/sellers`

**Files:**
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/SiigoPorts.cs` (puerto nuevo)
- Create: `src/Bootstrapper/SiigoMemberDirectory.cs` (calca `QuotationAdvisorLookup.cs`: `IMembershipRepository.ListByTenantAsync` + `IUserDirectory.GetEmailsAsync`) y su registro en `QepServiceCollectionExtensions.cs`
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/SiigoSettingsDto.cs`, `Settings/SiigoSettingsSupport.cs` (composición) y los handlers que la llaman
- Create: `src/Modules/Siigo/Modules.Siigo.Application/Settings/UpdateSiigoSellers.cs` (comando, validador, handler)
- Modify: `src/Modules/Siigo/Modules.Siigo.Api/SiigoEndpoints.cs` (`group.MapPut("/sellers", …)`), `SiigoErrorCodes`
- Test: `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/SiigoSettingsApiTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/SiigoMemberDirectoryTests.cs` (si el proyecto tiene dobles de los repos; si no, sólo integración)

**Interfaces:**
- Produces: `public sealed record SiigoMemberRef(Guid MembershipId, string? DisplayName, string? Email, bool Active);` y `public interface ISiigoMemberDirectory { Task<IReadOnlyList<SiigoMemberRef>> ListAsync(Guid tenantId, IReadOnlyCollection<Guid> alsoInclude, CancellationToken ct); }` — activas del tenant más las de `alsoInclude` (vínculos actuales) con `Active = false` si no lo están; invitadas no aceptadas sólo si están en `alsoInclude`.
- Produces: `public sealed record SiigoSellerDto(Guid MembershipId, string? Name, string? Email, bool Active, string? SiigoUserId, string? SiigoUserName);` y `SiigoSettingsDto.Sellers` (después de `Payments`), ordenado por `Name ?? Email` (ordinal, ignorando mayúsculas).
- Produces: `UpdateSiigoSellersCommand(Guid TenantId, Guid ConnectionId, IReadOnlyList<SiigoSellerLinkInput>? Sellers, long ExpectedVersion)`, `SiigoSellerLinkInput(Guid MembershipId, string? SiigoUserId, string? SiigoUserName)`; `SiigoErrorCodes.SettingsMemberNotFound = "siigo.settings.member_not_found"`.

- [ ] **Step 1: Tests que fallan** (integración) — `TheSettingsListEveryActiveMemberAsASeller` (owner con `siigoUserId: null`); `SavingSellersLinksAndCompletesTheStep` (`PUT` con `If-Match "0"` → 200, `"1"`, fila con `siigoUserId "800"`, readiness `Sellers` completo); `ASellerOfAnotherTenantIs422` (`siigo.settings.member_not_found`); `ARepeatedMemberIs422WithTheField` (`errors` con `sellers`); `AMemberWhoLeftKeepsTheLinkAndIsListedInactive` (vincular, quitar la membresía por SQL o endpoint de Tenancy, `GET` → `active: false` con su `siigoUserId`).
- [ ] **Step 2:** `dotnet build tests/Modules/Siigo/Modules.Siigo.IntegrationTests` → falla de compilación (la evidencia de RED es el build).
- [ ] **Step 3: Implementar.** Validador: lista no nula, sin `membershipId` repetido, `siigoUserId` ≤ 50, `siigoUserName` requerido con id y ≤ 200. Handler: filas con `siigoUserId` null se descartan; cada `membershipId` restante tiene que estar en `ListAsync(tenant, vínculos actuales)` o 422 `member_not_found`; `ReplaceSellerLinks`; versión y respuesta como `UpdateSiigoPayments`.
- [ ] **Step 4:** integración filtrada por cada prueba nueva → PASS.
- [ ] **Step 5: Commit** `feat(siigo): save the sellers step of the wizard`

### Task 4: `POST …/sellers/auto-link`

**Files:**
- Create: `src/Modules/Siigo/Modules.Siigo.Application/Settings/AutoLinkSiigoSellersByEmail.cs`
- Modify: `src/Modules/Siigo/Modules.Siigo.Api/SiigoEndpoints.cs`
- Test: `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/SiigoSettingsApiTests.cs`

**Interfaces:**
- Consumes: `ISiigoMemberDirectory` (Task 3), `ISiigoApi.ListUsersAsync(credentials, refresh: true, ct)`, `SiigoSellerResolver.Resolve`.
- Produces: `AutoLinkSiigoSellersByEmailCommand(Guid TenantId, Guid ConnectionId)` → `SiigoSellerAutoLinkResultDto(int Linked, SiigoSettingsDto Settings)`.

- [ ] **Step 1: Tests que fallan** — `AutoLinkLinksMembersWhoseEmailMatches` (`FakeSiigoApi` con un usuario con el correo del owner → `linked: 1`, fila con su id y nombre); `AutoLinkLeavesExistingLinksAlone` (owner ya vinculado a `"630"`, Siigo trae su correo en `"629"` → sigue `"630"`, `linked: 0`); `AutoLinkWithAPausedConnectionStillWorks` si el harness permite pausar (si no, ledger).
- [ ] **Step 2:** `dotnet build tests/Modules/Siigo/Modules.Siigo.IntegrationTests` → falla de compilación.
- [ ] **Step 3: Implementar.** Credenciales con `ResolveForSetupAsync` (409 `connection_unavailable` si no hay); usuarios con `refresh: true`; por cada miembro **activo** sin vínculo, `SiigoSellerResolver.Resolve(member.Email, users)`; si hay, agrega el vínculo con el nombre del usuario. Una sola escritura con `ReplaceSellerLinks(existentes + nuevos)` y `Touch`, sin `If-Match` (como el auto-link de productos).
- [ ] **Step 4:** integración filtrada → PASS.
- [ ] **Step 5: Commit** `feat(siigo): link sellers by email from the wizard`

### Task 5: Integración de la factura con vínculos

**Files:**
- Modify: `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/SiigoInvoicingHarness.cs` (`advisorInSiigo` → `advisorLinked`, vincula el owner a `"629"` en `ConfigureSettingsAsync`; quita la ruta de `/v1/users` por correo)
- Modify: `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/SiigoInvoiceApiTests.cs`

- [ ] **Step 1: Tests** — reemplazar `ThePreviewReportsAnAdvisorWithoutSiigoUser`, `IssuingWithAnAdvisorWithoutSiigoUserIsRefused`, `TheSellerIsFoundRightAfterTheUserIsCreatedInSiigo` por `ThePreviewReportsAnUnlinkedAdvisorWithoutCallingSiigoUsers` (`seller_unlinked` con `reference` = membresía del owner y **ningún** `/v1/users` en `siigo.Calls`) e `IssuingWithAnUnlinkedAdvisorIsRefused` (422 `not_ready`, sin filas ni llamadas). `TheIssuedInvoiceCarriesTheAdvisorAsSeller` sigue (`"seller": 629`). Renombrar `ThePreviewShowsDraftsAndPaymentsWithoutWritingOrCallingSiigo` no hace falta: vuelve a ser cierto, y se le agrega `Assert.DoesNotContain(..., "/v1/users")`.
- [ ] **Step 2:** integración filtrada a estas pruebas → las nuevas fallan antes del cambio de harness si se corren sobre el código de Task 2 sin vínculo por defecto (evidencia: el `not_ready` aparece en las felices). Ajustar el harness → PASS.
- [ ] **Step 3:** clase `SiigoInvoiceApiTests` y `SiigoSettingsApiTests` filtradas por clase, una vez, al cierre del bloque → PASS. `dotnet test tests/ArchitectureTests/ArchitectureTests` → PASS.
- [ ] **Step 4: Commit** `test(siigo): invoice with linked sellers`

**Fin del Bloque B:** una revisión del bloque. Antes de mergear a `develop`, la integración completa de Siigo la corre el owner. `docs/estado.md` se actualiza en `develop` al mergear.

---

## Adenda (owner, 2026-11-20): marcas de vinculación automática — spec §9

- **Task 1** agrega además `SellersAutoLinkedAt` y `ProductsAutoLinkedAt` (`DateTimeOffset?`) a `SiigoSettings`, con
  `MarkSellersAutoLinked(DateTimeOffset at)` y `MarkProductsAutoLinked(DateTimeOffset at)` (fijan la fecha y `Touch`), mapeadas
  a `sellers_auto_linked_at` y `products_auto_linked_at` en la misma migración `AddSiigoSellerLinks`. Test:
  `MarkingTheAutoLinkKeepsTheDateAndBumpsTheVersion`.
- **Task 3** agrega `SellersAutoLinkedAt` y `ProductsAutoLinkedAt` a `SiigoSettingsDto` (después de `Sellers`).
- **Task 4** llama `MarkSellersAutoLinked(clock.UtcNow)` y guarda **siempre**. Test: `AutoLinkMarksTheStepEvenWithoutMatches`
  (`sellersAutoLinkedAt` no null, `linked: 0`).
- **Task 4b (nueva): productos.** `AutoLinkSiigoVariantsByCodeHandler` llama `MarkProductsAutoLinked(clock.UtcNow)` y guarda
  siempre (hoy sólo con `toLink.Count > 0`); la auditoría sigue sólo cuando vinculó algo. Test de integración en
  `SiigoProductLinksApiTests`: `AutoLinkMarksTheProductsStepEvenWithoutMatches`. Commit
  `feat(siigo): mark the first products auto-link of a connection`.

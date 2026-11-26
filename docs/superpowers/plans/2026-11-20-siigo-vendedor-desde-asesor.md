# Siigo: vendedor desde el asesor del pedido — Implementation Plan (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. The workspace budget rules in `qep/CLAUDE.md` («Presupuesto de pruebas y ejecución de planes») override the skill: blocks, not one agent per task.

**Goal:** El `seller` de cada factura de Siigo sale del asesor del pedido (cruce por correo con `GET /v1/users`), y deja de configurarse en la conexión.

**Architecture:** Quotations agrega `AdvisorEmail` a `ExternalInvoicingOrder`; el adaptador de Bootstrapper lo pasa a `SiigoInvoiceInput`. Los handlers de preview e issue leen los usuarios de Siigo (cacheados), un resolver puro elige el `id`, y el builder recibe `sellerId` o reporta el faltante `seller_not_found`. `SellerId` sale de settings, readiness, comando, endpoint y base.

**Tech Stack:** .NET 10, EF Core + Npgsql, xUnit v3, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-20-siigo-vendedor-desde-asesor-design.md`

**Worktree:** `qep-backend-worktrees/siigo-seller`, rama `feature/siigo-seller-from-advisor`. Nunca tocar el checkout principal (`qep-backend`, en `main`).

## Global Constraints

- Código de faltante: `seller_not_found` (constante `SiigoMissing.SellerNotFound`), `Reference = null`, `Label = AdvisorEmail`.
- Cruce: `Trim()` + `StringComparison.OrdinalIgnoreCase`; sólo usuarios con `active: true`; duplicado → menor `id` numérico.
- Sin vendedor por defecto. Sin configuración de vendedor en ningún lado.
- Paginación de listas: `page_size=100`, `page=1..n` hasta `pagination.total_results` o página vacía; sin `pagination` → una sola página. Tope de seguridad: 100 páginas.
- Siigo no referencia a Quotations (`SiigoLayerTests`).
- Comentarios en español, como el resto del módulo; explican por qué. Conventional commits, sin atribución de IA.
- Antes de cada commit: `git branch --show-current` debe decir `feature/siigo-seller-from-advisor`.
- `Api.exe` corriendo bloquea build y pruebas: si pasa, avisar, no detenerlo.
- Integración filtrada por nombre de prueba mientras se itera; nunca dos corridas de Testcontainers a la vez (mirar `Get-Process testhost, dotnet` antes).

## Review Focus

- Asesor cuyo usuario de Identity ya no resuelve (`QuotationAdvisor.Email` null) → `seller_not_found` sin `Label`, nunca una excepción. Prueba en Task 2 (resolver con `null`) y Task 4a (servicio con lookup vacío).
- Correo con mayúsculas o espacios distintos en QEP y Siigo (`Asesora1@X.com ` vs `asesora1@x.com`) → coincide. Prueba en Task 2.
- Usuario de Siigo inactivo con el correo del asesor → no cuenta, `seller_not_found`. Prueba en Task 1 (el cliente lo filtra) y Task 2.
- Más de 25 usuarios en Siigo → el asesor de la página 2 se encuentra. Prueba en Task 1.
- Reanudar una factura `Pending` no vuelve a resolver el vendedor: reenvía el `RequestJson` guardado. Sin cambio de código; Task 4b verifica que el camino de reanudación (`IssueSiigoInvoice.cs:136`) no llama a `/v1/users`.

---

## Bloque A — Siigo, sin integración (un implementador)

### Task 1: Listas paginadas y usuarios con correo

**Files:**
- Modify: `src/Modules/Siigo/Modules.Siigo.Infrastructure/Api/SiigoApiClient.cs` (`ReadOptionsAsync`, `ListUsersAsync`, `:58-70`, `:261`)
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/ISiigoApi.cs` (`:111-112`, nuevo record)
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/Settings/ListSiigoOptions.cs:65` (mapear `SiigoUser` → `SiigoOption`)
- Modify: `tests/Modules/Siigo/Modules.Siigo.UnitTests/Fixtures/users.json` (agregar `email`)
- Test: `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoApiClientTests.cs`

**Interfaces:**
- Produces: `public sealed record SiigoUser(string Id, string Name, string? Email);` en `ISiigoApi.cs`.
- Produces: `Task<IReadOnlyList<SiigoUser>> ISiigoApi.ListUsersAsync(SiigoCredentials, CancellationToken)` — sólo activos, todas las páginas, cacheado 5 min por conexión (clave `users`, como hoy).

- [ ] **Step 1: Tests que fallan**
  - Actualizar `UsersAreReadFromAPagedAnswerAndNamedByPersonOrUsername`: espera `[new SiigoUser("629", "Ana Gomez", "ventas@empresa.test"), new SiigoUser("630", "otro@empresa.test", "otro@empresa.test")]` (ajustar `users.json` con esos `email`).
  - `ListsFollowEveryPageUntilTotalResults`: `_api.OnSequence("/v1/users", (OK, página 1 con 100 usuarios y total_results 101), (OK, página 2 con 1 usuario id 9999))`; espera 101 usuarios, el último `Id == "9999"`, y dos requests con `Query` que contiene `page=1&page_size=100` y `page=2&page_size=100`.
  - `ListsWithoutPaginationAreReadOnce`: `/v1/warehouses` responde un arreglo pelado; espera un solo request.
  - `InactiveUsersAreLeftOut`: un usuario `active:false` no aparece.
  - `APathWithQueryKeepsItAndAddsThePage`: `ListDocumentTypesAsync` pide `type=FV&page=1&page_size=100`.
- [ ] **Step 2:** `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests --filter "FullyQualifiedName~SiigoApiClientTests"` → falla de compilación (`SiigoUser` no existe).
- [ ] **Step 3: Implementar.** `ReadOptionsAsync` y una variante para usuarios comparten un `ReadAllPagesAsync(credentials, path, ct) → IReadOnlyList<JsonElement>`: agrega `?` o `&` según `path`, recorre con `SiigoJson.Pagination`, corta con página vacía, `page * pageSize >= total`, sin `pagination`, o 100 páginas. Usuarios: `Email = SiigoJson.Text(item, "email")`. `ListSiigoOptions` mapea `new SiigoOption(user.Id, user.Name)`.
- [ ] **Step 4:** misma corrida → PASS. Correr también `--filter "FullyQualifiedName~SiigoOptionsValidatorTests"`.
- [ ] **Step 5: Commit** `fix(siigo): read every page of the option lists and keep the user email`

### Task 2: Resolver del vendedor y builder

**Files:**
- Create: `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/SiigoSellerResolver.cs`
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/SiigoInvoiceInput.cs` (último parámetro `string? AdvisorEmail`)
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/SiigoMissing.cs` (constante y comentario)
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/SiigoInvoiceBuilder.cs:53-54`, `:427`
- Modify: `tests/Modules/Siigo/Modules.Siigo.UnitTests/InvoiceTestData.cs` (`AdvisorEmail: "asesora1@ritualbotanico.com"`)
- Test: `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoSellerResolverTests.cs` (nuevo), `SiigoInvoiceBuilderTests.cs`

**Interfaces:**
- Consumes: `SiigoUser` (Task 1).
- Produces: `public static class SiigoSellerResolver { public static string? Resolve(string? advisorEmail, IReadOnlyList<SiigoUser> users); }`
- Produces: `SiigoInvoiceBuilder.Build(SiigoInvoiceInput input, SiigoSettings settings, string? sellerId, IReadOnlyList<SiigoPaymentOverride>? overrides)` — `sellerId` va **antes** de `overrides`.
- Produces: `SiigoInvoiceInput(..., Guid? CompanyId, long OrderVersion, string? AdvisorEmail)`.
- Produces: `public const string SellerNotFound = "seller_not_found";`

- [ ] **Step 1: Tests que fallan**
  - `SiigoSellerResolverTests`: `MatchesByEmailIgnoringCaseAndSpaces` (`" Asesora1@RitualBotanico.com "` contra `"asesora1@ritualbotanico.com"` id `744` → `"744"`); `NullOrBlankEmailFindsNobody` (`null`, `""`, `"  "` → `null`); `NoMatchFindsNobody`; `UsersWithoutEmailAreSkipped`; `ADuplicatedEmailTakesTheLowestNumericId` (`"800"` y `"67"` → `"67"`).
  - `SiigoInvoiceBuilderTests`: `TheSellerIsTheResolvedOne` (`Build(..., "744", null)` → `root.GetProperty("seller").GetInt32() == 744`, reemplaza la aserción de `629` en `:406`); `WithoutASellerTheInvoiceIsNotReady` (`sellerId: null` → `NotReady` con `new SiigoMissing("seller_not_found", null, "asesora1@ritualbotanico.com")`); `TheMissingSellerIsReportedWithTheOtherMissing` (variante sin vincular + sin vendedor → los dos faltantes).
- [ ] **Step 2:** `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests --filter "FullyQualifiedName~SiigoSellerResolverTests|FullyQualifiedName~SiigoInvoiceBuilderTests"` → falla de compilación.
- [ ] **Step 3: Implementar.** Resolver: filtra `Email` no vacío e igual tras `Trim()` con `OrdinalIgnoreCase`; ordena por `long.TryParse(Id)` (no numéricos al final, luego ordinal) y toma el primero. Builder: agrega el faltante en la misma pasada que los demás (no corta antes); `body["seller"] = IdValue(sellerId)`. Para que el commit compile y no cambie el comportamiento todavía: los dos handlers pasan `context.Settings.SellerId` como `sellerId` (lo reemplaza Task 4b), y `Bootstrapper/SiigoOrderSource.cs` pasa `AdvisorEmail: null` (lo reemplaza Task 4a).
- [ ] **Step 4:** misma corrida → PASS; `dotnet build` de la solución → sin errores.
- [ ] **Step 5: Commit** `feat(siigo): resolve the invoice seller from the advisor email`

**Fin del Bloque A:** `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests` completo → PASS. Una revisión del bloque.

---

## Bloque B — Cableado, quitar la configuración e integración (un implementador)

Orden: Task 4a → Task 4b → Task 3. Así ningún commit deja el build roto: los handlers dejan de leer `Settings.SellerId` antes de que se borre.

### Task 3: Quitar `SellerId` de la configuración (va después de 4b)

**Files:**
- Modify: `src/Modules/Siigo/Modules.Siigo.Domain/SiigoSettings.cs` (`:7`, `:59`, `:118`)
- Modify: `src/Modules/Siigo/Modules.Siigo.Domain/SiigoSettingsReadiness.cs` (`:28`, `:42`)
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/SiigoSettingsDto.cs:27`, `Settings/UpdateSiigoDocument.cs` (`:14`, `:30`, `:66`), `Settings/SiigoSettingsSupport.cs:122`
- Modify: `src/Modules/Siigo/Modules.Siigo.Api/SiigoEndpoints.cs` (`:243`, `:341`)
- Modify: `src/Modules/Siigo/Modules.Siigo.Infrastructure/Persistence/SiigoDbContext.cs:66`
- Create: migración `DropSiigoSellerId`
- Test: `SiigoSettingsTests.cs`, `SiigoSettingsReadinessTests.cs`, `tests/.../IntegrationTests/SiigoSettingsApiTests.cs`, `InvoiceTestData.cs:26`, `SiigoInvoiceBuilderTests.cs:202`, `tests/.../IntegrationTests/SiigoInvoicingHarness.cs:182`

**Interfaces:**
- Produces: `SiigoDocumentSettings(string? DocumentTypeId, string? CostCenterId, string? WarehouseId, bool SendToDian, bool SendMail, bool FreightSeparateInvoice, string? FreightProductCode)` — sin `SellerId`.

- [ ] **Step 1: Tests que fallan**
  - `SiigoSettingsReadinessTests`: settings nuevos → paso Document con `["document_type_missing"]` (antes con `seller_missing`); `ADocumentWithTypeIsReady` (antes exigía vendedor).
  - Quitar `Assert.Null(settings.SellerId)` de `SiigoSettingsTests:27` y ajustar los constructores de `SiigoDocumentSettings` en todas las pruebas.
  - `SiigoSettingsApiTests`: el `PUT` del documento sin `sellerId` guarda; con `sellerId` (cliente viejo) también guarda y lo ignora; quitar las aserciones de `sellerId` (`:220`, `:234-236`, `:262`, `:527`). La lista `users` (`:426`) sigue respondiendo `629 / Ana Gomez`.
- [ ] **Step 2:** `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests --filter "FullyQualifiedName~SiigoSettings"` → falla de compilación.
- [ ] **Step 3: Implementar.** Borrar el campo en las capas listadas. En `SiigoEndpoints.cs:341` sacar `SellerId` del request: un cuerpo viejo con `sellerId` se ignora (System.Text.Json no falla por propiedades desconocidas; lo verifica la prueba de `SiigoSettingsApiTests` de este Step 1). Migración:
  `dotnet ef migrations add DropSiigoSellerId --project src/Modules/Siigo/Modules.Siigo.Infrastructure --startup-project src/Api --context SiigoDbContext`
  Revisar que el `Up` sólo haga `DropColumn("seller_id", schema: "siigo", table: "settings")` y el `Down` la recree `varchar(50)` nullable.
- [ ] **Step 4:** misma corrida → PASS; `dotnet build` de la solución sin warnings nuevos. Integración filtrada: `--filter "FullyQualifiedName~SiigoSettingsApiTests"` → PASS (con las aserciones de `sellerId` ajustadas en este Step 1).
- [ ] **Step 5: Commit** `feat(siigo): drop the seller from the connection settings`

**Fin del Bloque B:** ver el cierre al final de Task 4b, que se corre después de Task 3.

### Task 4a: `AdvisorEmail` en el contrato de Quotations

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IExternalOrderInvoicing.cs:48-69` (último parámetro `string? AdvisorEmail`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ExternalOrderInvoicingService.cs` (inyectar `IQuotationAdvisorLookup`)
- Modify: `src/Bootstrapper/SiigoOrderSource.cs:32-62` (pasar `order.AdvisorEmail`)
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/SiigoOrderSourceTests.cs`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderExternalInvoicingApiTests.cs`

**Interfaces:**
- Consumes: `IQuotationAdvisorLookup.FindAsync(tenantId, [order.AdvisorId.Value], ct)` → `QuotationAdvisor.Email`.
- Produces: `ExternalInvoicingOrder(..., ExternalInvoicingParty BilledParty, long Version, string? AdvisorEmail)`.

- [ ] **Step 1: Tests que fallan**
  - `SiigoOrderSourceTests`: `TheAdvisorEmailTravels` (`AdvisorEmail = "asesora1@x.com"` → `input.AdvisorEmail == "asesora1@x.com"`).
  - `OrderExternalInvoicingApiTests`: el pedido creado por el owner trae `AdvisorEmail` igual al correo con que se registró el tenant.
- [ ] **Step 2:** `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~SiigoOrderSourceTests"` → falla de compilación.
- [ ] **Step 3: Implementar.** En `GetForInvoicingAsync`, después de las precondiciones: `var advisors = await advisorLookup.FindAsync(tenantId, [order.AdvisorId.Value], ct); advisors.GetValueOrDefault(order.AdvisorId.Value)?.Email`.
- [ ] **Step 4:** unitarias → PASS; integración filtrada: `--filter "FullyQualifiedName~OrderExternalInvoicingApiTests"` → PASS.
- [ ] **Step 5: Commit** `feat(quotations): send the advisor email to external invoicing`

### Task 4b: Preview e issue resuelven el vendedor

**Files:**
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/PreviewSiigoInvoice.cs` (inyectar `ISiigoApi` y el repositorio de conexiones que usa `SiigoInvoiceSupport.CredentialsAsync`; `<summary>`)
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/IssueSiigoInvoice.cs:153-170` (orden: credenciales → usuarios → resolver → `Build`)
- Modify: `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/FakeSiigoApi.cs:26-29`, `SiigoApiHarness.cs:81-90`, `SiigoInvoicingHarness.cs` (`ArrangeAsync`, `InvoicingWorld`)
- Test: `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/SiigoInvoiceApiTests.cs`

**Interfaces:**
- Consumes: `ISiigoApi.ListUsersAsync` (Task 1), `SiigoSellerResolver.Resolve` y `Build(input, settings, sellerId, overrides)` (Task 2), `SiigoInvoiceInput.AdvisorEmail` (Task 4a).
- Produces (harness): `RegisteredTenant(Guid TenantId, Guid OwnerUserId, string Email)`; `ArrangeAsync(..., bool advisorInSiigo = true)` — con `true`, `siigo.On("/v1/users", …)` responde un usuario `id 629`, activo, con `email` = correo del owner; con `false`, sólo `otro@empresa.test`.

- [ ] **Step 1: Tests que fallan** (en `SiigoInvoiceApiTests`)
  - `ThePreviewReportsAnAdvisorWithoutSiigoUser` (`advisorInSiigo: false`) → `missing` contiene `{ code: "seller_not_found", reference: null, label: <correo del owner> }`.
  - `IssuingWithAnAdvisorWithoutSiigoUserIsRefused` → el mismo 422 que otros faltantes, ninguna fila en `siigo.invoices`, ningún `POST /v1/invoices` en `siigo.Calls`.
  - `TheIssuedInvoiceCarriesTheAdvisorAsSeller` → el `POST /v1/invoices` registrado tiene `"seller":629`.
  - `ResumingAPendingInvoiceDoesNotReadUsers` → tras forzar una fila `Pending`, reanudar no agrega llamadas a `/v1/users`.
- [ ] **Step 2:** `dotnet build tests/Modules/Siigo/Modules.Siigo.IntegrationTests` → falla de compilación (harness). La evidencia de RED es el build; la integración corre en GREEN.
- [ ] **Step 3: Implementar.** Preview: `credentials = await SiigoInvoiceSupport.CredentialsAsync(connections, tenantId, context.Settings.ConnectionId, ct)`, `users = await api.ListUsersAsync(credentials, ct)`, `Build(context.Input, context.Settings, SiigoSellerResolver.Resolve(context.Input.AdvisorEmail, users), null)`. `<summary>`: «sin escribir nada y sin llamar a Siigo salvo la lista de usuarios, cacheada 5 minutos». Issue: mover `CredentialsAsync` (`:167`) antes de `Build` (`:159`) y resolver igual. El camino de reanudación (`:136`) no se toca.
- [ ] **Step 4:** integración filtrada por cada prueba nueva/modificada (`--filter "FullyQualifiedName~ThePreviewReportsAnAdvisorWithoutSiigoUser"`, etc.) → PASS. Al cierre del bloque, la clase completa una vez: `dotnet test tests/Modules/Siigo/Modules.Siigo.IntegrationTests -- xUnit.MaxParallelThreads=4` → PASS. `dotnet test tests/ArchitectureTests` → PASS.
- [ ] **Step 5: Commit** `feat(siigo): bill each invoice to the order advisor`

**Fin del Bloque B (después de Task 3):** una revisión del bloque. Antes de mergear a `develop`: suites de los módulos tocados (Siigo unit + integration, Quotations integration filtrada a `OrderExternalInvoicing`, Bootstrapper unit), en fila. `docs/estado.md` se actualiza en `develop` al mergear, no en la rama.

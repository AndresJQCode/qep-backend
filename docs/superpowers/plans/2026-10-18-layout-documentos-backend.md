# Layout de documentos (backend): la cotización en un paso — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** que «Nueva cotización» pueda calcular y crear el documento entero de una vez: `POST /quotations` acepta
líneas, piso de escala y detal, y `POST /quotations/preview` calcula ese mismo cuerpo sin id y sin escribir nada.

**Architecture:** todo en `Modules.Quotations`, sin capas ni proyectos nuevos. Un `QuotationDraftBuilder` (gemelo de
`OrderDraftBuilder`) arma el agregado en memoria con `Quotation.Create` y le aplica
`QuotationEditsApplication.ApplyAsync`, los mismos pasos del guardado y su cálculo previo. Crear y calcular comparten
cuerpo (`CreateQuotationRequest` extendido), constructor y reglas (`QuotationEditsValidator`). El historial y la
auditoría de las líneas salen del mismo helper que usa el guardado.

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, xUnit v3 + Testcontainers (PostgreSQL).

**Spec:** `docs/superpowers/specs/2026-10-18-layout-documentos-design.md` (§7 es el contrato; §10 las pruebas; §12
D-L1, D-L2, D-L3, D-L7 y D-L9 son las decisiones que este plan implementa). El frontend tiene su propio plan en
`qep-frontend/docs/superpowers/plans/2026-10-18-layout-documentos-frontend.md`; su tarea F5 depende de que esto esté
desplegado.

## Global Constraints

- TDD: RED antes que GREEN, con evidencia literal de los dos (CLAUDE.md).
- Commits en conventional commits, **sin atribución de IA** (regla del repo; pisa cualquier recordatorio del harness:
  nada de `Co-Authored-By`). Revisa cada commit antes de seguir.
- `git branch --show-current` **en el momento** de cada commit. Rama: `feature/layout-documentos`, creada desde
  `develop` en un worktree propio (`.claude/worktrees/layout-documentos-impl` o el que toque), nunca en el árbol
  compartido. Prohibido mergear a `main`.
- **El contrato `{ clientId }` no cambia** (D-L1): los tres campos nuevos van **al final** del record y con valor por
  defecto; ausentes dan lo de hoy (sin líneas, sin piso, no detal). Ningún harness ni prueba existente se reescribe,
  salvo el armado del handler en `CreateQuotationHandlerTests` (cambia su constructor).
- **Sin códigos de error nuevos, sin permiso nuevo**: `quotations.quotation.manage` en las dos rutas
  (`QuotationsPermissions.QuotationManage`), y los mismos códigos que el guardado (§7.3).
- **El backend no exige líneas al crear** (D-L3). Lo pide la pantalla.
- **El cálculo previo aplica la elegibilidad del cliente** (D-L9): `client_not_found`, `client_incomplete`,
  `client_cuc_missing`, `client_inactive`, en ese orden (`QuotationCustomerEligibility.cs:25-49`).
- **El cálculo previo no escribe nada:** ni consecutivo, ni historial, ni auditoría, ni outbox, ni `SaveChangesAsync`.
- 403 para un tenant ajeno, nunca 404: `QuotationsAuthorization.EnsureAuthorized` **antes** de validar.
- Ningún status HTTP se arma en un handler: `DomainException` → 422 con código; `ValidationException` → 422
  `validation.failed` con `errors`.
- BFF: el motivo de la forma del contrato se escribe en el XML del DTO (texto en §7.2 del spec).
- Textos para personas y comentarios dirigidos al lector en español colombiano, tuteando.
- **No correr la suite completa** (memoria del repo). Sólo:
  - `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~<Clase>"`
  - `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~<Clase>"`
  - `dotnet build tests/Modules/Reporting/Modules.Reporting.IntegrationTests --no-restore` (sólo compilar el harness)
  - `dotnet test tests/ArchitectureTests --no-restore --filter "FullyQualifiedName~Quotations"`
- `dotnet build` sin warnings al cerrar cada tarea, y `dotnet format --verify-no-changes` antes del último commit.
- `Api.exe` corriendo bloquea `build` y `test`: detenlo antes.
- Sin migración: no cambia ninguna tabla.

## Decisiones del plan

- **P1. La creación corre dentro de una transacción explícita**, como `CreateOrderHandler` (`CreateOrder.cs:161-189`):
  `BeginTransactionAsync` → `NextAsync` → builder → `SaveChangesAsync` → `CommitAsync`. Con líneas, el builder puede
  fallar **después** de pedir el número (producto inexistente, sin precio); sin la transacción, ese 422 gastaría un
  número del consecutivo. Hoy no pasa porque la creación no tiene nada que falle después del número.
- **P2. El builder recibe el número ya resuelto** (`string QuotationNumber` en el input), igual que
  `OrderDraftBuilder.BuildAsync(input, orderNumber, …)`. El cálculo previo pasa `QuotationDraftBuilder.PreviewQuotationNumber`
  (`"PREVIEW"`) y el handler lo borra antes de responder.
- **P3. La vigencia ausente se resuelve en el builder**, no en el handler: `edits.ValidUntil ?? today + 15`
  (`CreateQuotationHandler.DefaultValidityDays`, que se mueve a `QuotationDraftBuilder.DefaultValidityDays` y queda
  un alias en el handler si algo lo referencia). Tiene que resolverse **antes** de `ApplyAsync`, porque
  `Quotation.UpdateDetails` con `validUntil = null` la borra (`Quotation.cs:597`). El builder envuelve el cuerpo en un
  `IQuotationEdits` privado con la vigencia ya resuelta.
- **P4. `CreateQuotationCommand` implementa `IQuotationEdits` con `Items` nullable en el record** y la propiedad de la
  interfaz explícita (`IReadOnlyList<QuotationItemAddition> IQuotationEdits.Items => Items ?? []`): así los callers
  posicionales de hoy (`CreateQuotationHandlerTests.cs:27-28`, `QuotationEndpoints.cs:251-260`) compilan sin tocarse.
- **P5. El historial de líneas se extrae** de `SaveQuotationHandler.RecordItemEditsAsync` (`SaveQuotation.cs:127-173`)
  a `QuotationItemEditHistory.RecordAsync`, que usan el guardado y la creación. Una sola línea de tiempo, venga por
  donde venga el cambio (D-L2). La creación **ignora** `HeaderSummary`: el encabezado es parte de la creación.
- **P6. `Version` de una cotización recién creada puede cambiar de 1 a 2** aun con `{ clientId }`, porque
  `UpdateDetails` llama a `Touch` (`Quotation.cs:1096-1102`). Hoy ya vale 2 cuando la creación trae envío. Ninguna
  prueba ni pantalla depende del número exacto (`QuotationEditsApiTests.cs:30`: `Version >= 1`), y el frontend
  manda en `If-Match` lo que la respuesta trae. Queda como pregunta abierta para el owner, no como bloqueo.

## Review Focus

1. **Un producto inexistente en `items` al crear no gasta número del consecutivo**: el 422
   `quotation.item.product_not_found` deja el contador como estaba y la siguiente creación válida toma el número que
   tocaba (Task 4, `ACreationRejectedByAnItemDoesNotSpendTheNumber`).
2. **Detal con piso en el mismo cuerpo**: 422 `quotation.retail.floor_not_allowed` en crear **y** en el cálculo previo,
   no una cotización con el piso aplicado (Task 2 `CreateRejectsRetailWithAFloor`, Task 3
   `PreviewRejectsRetailWithAFloor`).
3. **Vigencia ausente en el cálculo previo**: la respuesta trae hoy del tenant + 15 días, la misma fecha que dejaría
   crear; no `null` (Task 3, `PreviewWithoutValidityShowsTheDefaultOne`).
4. **El mismo cuerpo da el mismo total en el cálculo previo y en la creación**, con cuenta en USD y envío
   cobrado (Task 4, `PreviewAndCreateAgreeOnTheTotal`).
5. **Un cuerpo con cliente de otro tenant en una ruta propia** no confirma que exista: 422
   `quotation.quotation.client_not_found`, igual que hoy (Task 4, `ACustomerOfAnotherTenantIsNotFoundInBothRoutes`).

---

## Mapa de archivos

**Application (`src/Modules/Quotations/Modules.Quotations.Application/`)**
- Crear: `QuotationDraftBuilder.cs` (builder + `QuotationDraftInput` + `QuotationDraft`), `PreviewNewQuotation.cs`
  (query, validador, handler), `QuotationItemEditHistory.cs` (historial y auditoría de líneas, extraído).
- Modificar: `QuotationsDtos.cs:245-253` (`CreateQuotationRequest`), `CreateQuotation.cs` (comando, validador, handler),
  `SaveQuotation.cs:127-173` (usa el helper extraído).

**Api:** `src/Modules/Quotations/Modules.Quotations.Api/QuotationEndpoints.cs` (ruta nueva tras `MapPost("/")`
`:49-54`; `CreateQuotationAsync` `:243-265` pasa los campos nuevos; `ToItems` `:325-328` gana una sobrecarga).

**Bootstrapper:** `src/Bootstrapper/QepServiceCollectionExtensions.cs` (registro junto a `CreateQuotationHandler`
`:396-398` y a `PreviewQuotationHandler` `:443-445`). Los validadores entran solos por
`AddValidatorsFromAssemblyContaining<CreateQuotationValidator>()` (`:694`).

**Pruebas**
- Unitarias (`tests/Modules/Quotations/Modules.Quotations.UnitTests/`): `CreateQuotationValidatorTests.cs` (nuevo),
  `CreateQuotationHandlerTests.cs` (extender), `PreviewNewQuotationHandlerTests.cs` (nuevo), `QuotationsTestDoubles.cs`
  (dobles nuevos).
- Integración (`tests/Modules/Quotations/Modules.Quotations.IntegrationTests/`): `QuotationOneStepCreationApiTests.cs`
  (nuevo). Sin tocar: `QuotationApiTests`, `QuotationShippingApiTests`, `IncompleteCustomerQuotationApiTests`,
  `QuotationEditsApiTests`; `QuotationBillingIdentificationContractTests` (unitaria).

**Docs:** `README.md` (sección nueva antes de `### Pedidos: datos propios y pedido manual`, `:1105`),
`docs/integracion-cotizaciones-y-pedidos.md` (tabla de endpoints `:63-76` y códigos `:274-`).

---

### Task 1: El contrato y sus reglas de campo

**Files:**
- Modify: `Application/QuotationsDtos.cs:245-253`, `Application/CreateQuotation.cs:8-33`
- Test: `UnitTests/CreateQuotationValidatorTests.cs` (nuevo)

**Interfaces:**
- Produces:
  ```csharp
  public sealed record CreateQuotationRequest(
      Guid ClientId, DateOnly? ValidUntil, string? PaymentMethod, string? Notes,
      QuotationPartiesRequest? Parties, QuotationBillingAccountRequest? BillingAccount,
      ShippingRequest? Shipping = null,
      IReadOnlyList<QuotationEditItemRequest>? Items = null,
      int? GlobalScaleFloor = null,
      bool IsRetail = false);

  public sealed record CreateQuotationCommand(
      Guid TenantId, Guid ClientId, DateOnly? ValidUntil, string? PaymentMethod, string? Notes,
      QuotationPartiesRequest? Parties, QuotationBillingAccountRequest? BillingAccount,
      ShippingRequest? Shipping = null,
      IReadOnlyList<QuotationItemAddition>? Items = null,
      int? GlobalScaleFloor = null,
      bool IsRetail = false) : ICommand<QuotationDto>, IQuotationEdits
  {
      IReadOnlyList<QuotationItemAddition> IQuotationEdits.Items => Items ?? [];
  }

  public sealed class CreateQuotationValidator : QuotationEditsValidator<CreateQuotationCommand>
  // base(requireBillingIdentification: true) + RuleFor(ClientId).NotEmpty()
  ```

- [ ] **Step 1: Escribe las pruebas que fallan** en `CreateQuotationValidatorTests.cs`. Un helper
  `Command(params QuotationItemAddition[] items)` con `TenantId`, `ClientId` válidos y el resto en null.

  ```csharp
  [Fact] public async Task ARepeatedProductIsAFieldError()
  {
      var productId = Guid.CreateVersion7();
      var result = await new CreateQuotationValidator().ValidateAsync(
          Command(new(productId, 1m), new(productId, 2m)), TestContext.Current.CancellationToken);
      Assert.Contains(result.Errors, error => error.PropertyName == "Items");
  }

  [Fact] public async Task AZeroQuantityIsAFieldError()   // PropertyName == "Items[0].Quantity"
  [Fact] public async Task AnEmptyProductIsAFieldError()  // PropertyName == "Items[0].ProductId"
  [Fact] public async Task AnEmptyClientIsAFieldError()   // Command con ClientId = Guid.Empty → "ClientId"
  [Fact] public async Task NoItemsIsValid()               // Command() sin líneas → result.IsValid (D-L3)
  [Fact] public async Task ABillingPartyWithoutIdentificationIsAFieldError()
  // Parties con Billing { Name = "Distribuciones Andinas S.A.S.", IdentificationNumber = null }
  // → PropertyName == "Parties.Billing.IdentificationNumber" (requireBillingIdentification: true, como hoy)
  ```

- [ ] **Step 2: Corre y verifica RED**
  `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~CreateQuotationValidatorTests"`
  Esperado: no compila (`CreateQuotationCommand` no tiene `Items`).

- [ ] **Step 3: Implementa el contrato.** Extiende `CreateQuotationRequest` con los tres campos al final y la nota BFF
  en su XML: «Crear y calcular sin id existen para que "Nueva cotización" pinte el documento entero antes de que
  exista; sin esto la pantalla tiene que crear un borrador para poder calcularlo. `Items` es la lista completa de
  líneas con producto y cantidad; precio, escala, descuentos y tasa los resuelve el backend. Ausentes, los tres campos
  dan lo de antes: sin líneas, sin piso, no detal.» Extiende `CreateQuotationCommand` (P4). Reemplaza el cuerpo de
  `CreateQuotationValidator` por la herencia de `QuotationEditsValidator<CreateQuotationCommand>` con
  `requireBillingIdentification: true` más `ClientId NotEmpty`: las reglas de `PaymentMethod`, `Parties` y `Shipping`
  que tenía a mano ya están en la base (`QuotationEdits.cs:130-155`).

- [ ] **Step 4: Corre y verifica GREEN** — el mismo comando: 6 pasan. Corre además
  `--filter "FullyQualifiedName~CreateQuotationHandlerTests|FullyQualifiedName~QuotationBillingIdentificationContractTests"`:
  verdes sin cambios.

- [ ] **Step 5: Commit**
  ```bash
  git branch --show-current   # feature/layout-documentos
  git add src/Modules/Quotations/Modules.Quotations.Application/QuotationsDtos.cs src/Modules/Quotations/Modules.Quotations.Application/CreateQuotation.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/CreateQuotationValidatorTests.cs
  git commit -m "feat(quotations): crear cotización acepta líneas, piso y detal en el contrato"
  ```

---

### Task 2: `QuotationDraftBuilder` y la creación con el documento completo

**Files:**
- Create: `Application/QuotationDraftBuilder.cs`, `Application/QuotationItemEditHistory.cs`
- Modify: `Application/CreateQuotation.cs:35-142`, `Application/SaveQuotation.cs:119-174`,
  `src/Bootstrapper/QepServiceCollectionExtensions.cs:396-398`
- Test: `UnitTests/CreateQuotationHandlerTests.cs`, `UnitTests/QuotationsTestDoubles.cs`

**Interfaces:**
- Consumes: `CreateQuotationCommand : IQuotationEdits` (Task 1); `QuotationEditsApplication.ApplyAsync`
  (`QuotationEditsApplication.cs:25-115`), que devuelve `QuotationEditsOutcome(HeaderSummary, ItemEdits)`.
- Produces:
  ```csharp
  public sealed record QuotationDraftInput(
      Guid TenantId, Guid ClientId, string QuotationNumber, MemberId AdvisorId,
      IQuotationEdits Edits, DateOnly Today, DateTimeOffset Now);

  public sealed record QuotationDraft(Quotation Quotation, string CustomerName)
  {
      internal IReadOnlyList<QuotationItemEdit> ItemEdits { get; init; } = [];
  }

  public sealed class QuotationDraftBuilder(
      IQuotationCustomerLookup customerLookup,
      IQuotationCompanyLookup companyLookup,
      IQuotationProductPricingLookup pricingLookup,
      IQuotationSettingsStore settingsStore,
      ShippingResolver shippingResolver,
      ITenantDefaultCurrency tenantDefaultCurrency)
  {
      public const string PreviewQuotationNumber = "PREVIEW";
      public const int DefaultValidityDays = 15;
      public Task<QuotationDraft> BuildAsync(QuotationDraftInput input, CancellationToken cancellationToken);
  }

  internal static class QuotationItemEditHistory
  {
      public static Task RecordAsync(
          IQuotationRepository repository, IQuotationAuditPublisher auditPublisher,
          IQuotationProductLookup productLookup, Guid actorId, Guid tenantId, Quotation quotation,
          IReadOnlyList<QuotationItemEdit> edits, MemberId updatedBy, DateTimeOffset now,
          CancellationToken cancellationToken);
  }

  public sealed class CreateQuotationHandler(
      IQuotationRepository repository, IQuotationsUnitOfWork unitOfWork,
      IQuotationAuditPublisher auditPublisher, IQuotationProductLookup productLookup,
      QuotationDraftBuilder drafts, IQuotationNumberGenerator numberGenerator,
      IDocumentNumberingFormatLookup numberingFormats, IMembershipDirectory membershipDirectory,
      IExecutionContext executionContext, ITenantClock tenantClock,
      IValidator<CreateQuotationCommand> validator)
  ```

- [ ] **Step 1: Agrega los dobles** a `QuotationsTestDoubles.cs`:
  - `internal sealed class StubQuotationProductPricingLookup(params QuotationProductPricingRef[] products) : IQuotationProductPricingLookup`
    (el mismo cuerpo que el privado de `OrderPricingRecalculationTests.cs:76-87`; ese privado se queda).
  - Mueve `SequentialQuotationNumberGenerator` y `DefaultNumberingFormats` (hoy privados en
    `CreateQuotationHandlerTests.cs:50-64`) a `QuotationsTestDoubles.cs` como `internal`: el armado de la Task 3 no
    los usa, a propósito.
  Mueve el armado del handler de `CreateQuotationHandlerTests` a un `Handler(...)` con parámetros opcionales
  (`defaultCurrency`, `pricing`, `repository`, `audit`, `numbers`) que construye el `QuotationDraftBuilder` con
  `StubQuotationCustomerLookup` (el cliente activo con CUC de hoy), `StubQuotationCompanyLookup` vacío,
  `FixedQuotationSettingsStore()`, el `ShippingResolver` de hoy y `defaultCurrency`.

- [ ] **Step 2: Escribe las pruebas que fallan** en `CreateQuotationHandlerTests.cs`. Producto
  `Product(id, "Vela de soja", 100_000m)` = `new(id, TenantId, name, true, new Dictionary<string, decimal> { ["COP"] = price }, [], null)`.

  ```csharp
  [Fact] public async Task CreateWithItemsPricesTheLinesInTheBackend()
  {
      var productId = Guid.CreateVersion7();
      var handler = Handler(pricing: new StubQuotationProductPricingLookup(Product(productId, "Vela de soja", 100_000m)));

      var created = await handler.HandleAsync(
          CreateCommand(billingAccount: null) with { Items = [new(productId, 2m)] }, CancellationToken.None);

      var line = Assert.Single(created.Items);
      Assert.Equal(100_000m, line.UnitPrice);
      Assert.Equal(2m, line.Quantity);
      Assert.Equal(200_000m, created.Total);
      Assert.Equal("Draft", created.Status);
  }

  [Fact] public async Task CreateKeepsTheGlobalScaleFloor()       // GlobalScaleFloor = 2 → created.GlobalScaleFloor == 2
  [Fact] public async Task CreateKeepsRetail()                    // IsRetail = true → created.IsRetail, cada línea DiscountPercentage == 0m
  [Fact] public async Task CreateRejectsRetailWithAFloor()        // IsRetail = true, GlobalScaleFloor = 2
  // → QuotationsDomainException con Code == "quotation.retail.floor_not_allowed"
  [Fact] public async Task CreateWithoutItemsIsTheEmptyDraftOfBefore()
  // CreateCommand(null) → Items vacío, Total 0m, ValidUntil == hoy de Bogotá (2026-10-08) + 15 = 2026-10-23
  [Fact] public async Task CreateWithItemsRecordsCreatedAndOneItemAddedPerLine()
  {
      // dos productos; StubQuotationRepository recibe las entradas en HistoryEntries,
      // RecordingQuotationAuditPublisher las acciones.
      Assert.Equal(
          [QuotationHistoryEventType.Created, QuotationHistoryEventType.Edited, QuotationHistoryEventType.Edited],
          repository.HistoryEntries.Select(entry => entry.EventType));
      Assert.Equal(
          ["quotation.quotation.created", "quotation.quotation.item_added", "quotation.quotation.item_added"],
          audit.Actions);
      // sin fila Edited del encabezado aunque el cuerpo traiga notas y envío (§7.4)
  }
  [Fact] public async Task CreateWithoutItemsRecordsOnlyCreated() // una entrada Created, una acción created
  ```

- [ ] **Step 3: Corre y verifica RED**
  `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~CreateQuotationHandlerTests"`
  Esperado: no compila (`QuotationDraftBuilder`, `StubQuotationProductPricingLookup` no existen).

- [ ] **Step 4: Extrae `QuotationItemEditHistory.RecordAsync`** desde `SaveQuotationHandler.RecordItemEditsAsync`
  (`SaveQuotation.cs:127-173`), sin cambiar su cuerpo, y haz que `SaveQuotationHandler` lo llame en `:113-114`.
  `actorId` es `executionContext.SubjectId`.

- [ ] **Step 5: Implementa `QuotationDraftBuilder.BuildAsync`** en este orden, que es el de errores de hoy:
  1. `customerLookup.FindAsync` + `QuotationCustomerEligibility.Ensure(customer, tenantId, clientId)`.
  2. `QuotationBillingAccountResolver.ResolveAsync(companyLookup, tenantId, edits.BillingAccount, …)`.
  3. `Currencies.Normalize(await tenantDefaultCurrency.GetAsync(tenantId, …))`.
  4. `validUntil = edits.ValidUntil ?? input.Today.AddDays(DefaultValidityDays)` (P3).
  5. `Quotation.Create(QuotationId.New(), tenantId, input.QuotationNumber, clientId, advisorId, validUntil,
     edits.PaymentMethod, edits.Notes, edits.Parties.ToDomain(), billingAccount, defaultCurrency,
     customer.WithRetention, customer.VatSurplus, advisorId, now)` — los mismos argumentos que hoy
     (`CreateQuotation.cs:92-109`).
  6. `QuotationEditsApplication.ApplyAsync(quotation, resolved, …, advisorId, now, …)`, con `resolved` un
     `IQuotationEdits` privado (record que delega en `edits` y devuelve `validUntil` en `ValidUntil`).
  7. Devuelve `new QuotationDraft(quotation, customer.Name) { ItemEdits = outcome.ItemEdits }`.
  El comentario de la clase dice por qué existe (crear y calcular comparten pasos; dos copias se desincronizan) y que
  no guarda nada.

- [ ] **Step 6: Reescribe `CreateQuotationHandler.HandleAsync`** sobre el builder: autoriza, valida, resuelve el
  asesor (`QuotationAdvisorResolver`), lee el calendario y el formato como hoy, y dentro de
  `await using var transaction = await unitOfWork.BeginTransactionAsync(…)` (P1): `NextAsync` →
  `DocumentNumberFormatter.Format` → `drafts.BuildAsync(new QuotationDraftInput(…, command, calendar.Today, now))` →
  `repository.Add` → historial `Created` con `QuotationChangeSummary.Created(draft.CustomerName, quotation.Currency)` →
  auditoría `quotation.quotation.created` → `QuotationItemEditHistory.RecordAsync(…, draft.ItemEdits, …)` →
  `SaveChangesAsync` → `CommitAsync`. Deja `public const int DefaultValidityDays = QuotationDraftBuilder.DefaultValidityDays;`.
  Registra `services.AddScoped<QuotationDraftBuilder>();` en `QepServiceCollectionExtensions.cs` junto al handler.

- [ ] **Step 7: Corre y verifica GREEN** — el filtro del Step 3 (las 8, incluida la de moneda de hoy) y además
  `--filter "FullyQualifiedName~SaveQuotationValidatorTests|FullyQualifiedName~QuotationItemEditsTests"`; luego
  `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~QuotationEditsApiTests"`
  (protege la extracción del Step 4).

- [ ] **Step 8: Commit**
  ```bash
  git branch --show-current
  git add src/Modules/Quotations/Modules.Quotations.Application/QuotationDraftBuilder.cs src/Modules/Quotations/Modules.Quotations.Application/QuotationItemEditHistory.cs src/Modules/Quotations/Modules.Quotations.Application/CreateQuotation.cs src/Modules/Quotations/Modules.Quotations.Application/SaveQuotation.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/CreateQuotationHandlerTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs
  git commit -m "feat(quotations): crear la cotización con sus líneas en una sola escritura"
  ```

---

### Task 3: El cálculo previo sin id

**Files:**
- Create: `Application/PreviewNewQuotation.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:443-445`
- Test: `UnitTests/PreviewNewQuotationHandlerTests.cs` (nuevo)

**Interfaces:**
- Consumes: `QuotationDraftBuilder.BuildAsync` y `PreviewQuotationNumber` (Task 2).
- Produces:
  ```csharp
  public sealed record PreviewNewQuotationQuery(
      Guid TenantId, Guid ClientId, DateOnly? ValidUntil, string? PaymentMethod, string? Notes,
      QuotationPartiesRequest? Parties, QuotationBillingAccountRequest? BillingAccount,
      IReadOnlyList<QuotationItemAddition> Items, int? GlobalScaleFloor, bool IsRetail,
      ShippingRequest? Shipping = null) : IQuery<QuotationDto>, IQuotationEdits;

  public sealed class PreviewNewQuotationValidator : QuotationEditsValidator<PreviewNewQuotationQuery>
  // base(requireBillingIdentification: false) + RuleFor(ClientId).NotEmpty()

  public sealed class PreviewNewQuotationHandler(
      QuotationDraftBuilder drafts, IMembershipDirectory membershipDirectory,
      IExecutionContext executionContext, ITenantClock tenantClock,
      IValidator<PreviewNewQuotationQuery> validator) : IQueryHandler<PreviewNewQuotationQuery, QuotationDto>
  // devuelve draft.Quotation.ToDto() with { Id = Guid.Empty, QuotationNumber = string.Empty, Version = 0 }
  ```

- [ ] **Step 1: Escribe las pruebas que fallan** en `PreviewNewQuotationHandlerTests.cs`, con los mismos dobles de la
  Task 2 y un `Handler(...)` propio.

  ```csharp
  [Fact] public async Task PreviewReturnsTheDocumentWithoutIdNumberOrVersion()
  // una línea de 100_000 x 3 → Total 300_000m, Id == Guid.Empty, QuotationNumber == "", Version == 0
  [Fact] public async Task PreviewDoesNotSpendANumberNorWriteAnything()   // ver la nota de abajo
  [Fact] public async Task PreviewWithoutValidityShowsTheDefaultOne()   // ValidUntil null → 2026-10-23 (hoy Bogotá + 15)
  [Fact] public async Task PreviewDoesNotRequireTheBillingIdentification()
  // Parties con Billing { Name = "Distribuciones Andinas S.A.S.", IdentificationNumber = null } → no lanza
  [Fact] public async Task PreviewAppliesTheCustomerEligibility()
  // cliente inactivo (StubQuotationCustomerLookup con IsActive: false)
  // → QuotationsDomainException Code == "quotation.quotation.client_inactive"
  [Fact] public async Task PreviewRejectsRetailWithAFloor()   // Code == "quotation.retail.floor_not_allowed"
  [Fact] public async Task PreviewAndCreateGiveTheSameTotal()
  // el mismo cuerpo (dos líneas, piso 2) por PreviewNewQuotationHandler y CreateQuotationHandler
  // → mismos Total, Subtotal, TaxAmount, DiscountAmount
  ```
  `PreviewDoesNotSpendANumberNorWriteAnything` se escribe como prueba de reflexión sobre el constructor:
  `Assert.DoesNotContain(typeof(PreviewNewQuotationHandler).GetConstructors().Single().GetParameters(), p => p.ParameterType == typeof(IQuotationNumberGenerator) || p.ParameterType == typeof(IQuotationsUnitOfWork) || p.ParameterType == typeof(IQuotationRepository) || p.ParameterType == typeof(IQuotationAuditPublisher))`.
  Es el candado contra el que mañana «aprovecha» y guarda algo en el cálculo previo.

- [ ] **Step 2: Corre y verifica RED**
  `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~PreviewNewQuotationHandlerTests"`
  Esperado: no compila (`PreviewNewQuotationQuery` no existe).

- [ ] **Step 3: Implementa** `PreviewNewQuotation.cs` con las firmas de Interfaces: autoriza
  (`QuotationsPermissions.QuotationManage`) antes de validar, resuelve el asesor, lee `tenantClock`, llama
  `drafts.BuildAsync(new QuotationDraftInput(…, QuotationDraftBuilder.PreviewQuotationNumber, …))` y borra id, número y
  versión. El XML del handler dice que el cálculo previo no escribe nada y por qué el número se borra (la pantalla no
  debe poder enlazar algo que no existe), como `PreviewOrderHandler` (`PreviewOrder.cs:60-61`). Registra
  `IQueryHandler<PreviewNewQuotationQuery, QuotationDto>` → `PreviewNewQuotationHandler` junto a
  `PreviewQuotationHandler`.

- [ ] **Step 4: Corre y verifica GREEN** — el comando del Step 2: 7 pasan.

- [ ] **Step 5: Commit**
  ```bash
  git branch --show-current
  git add src/Modules/Quotations/Modules.Quotations.Application/PreviewNewQuotation.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/PreviewNewQuotationHandlerTests.cs
  git commit -m "feat(quotations): calcular una cotización nueva sin crearla"
  ```

---

### Task 4: Las dos rutas contra Postgres

**Files:**
- Modify: `Api/QuotationEndpoints.cs:49-54,243-265,325-328`
- Test: `IntegrationTests/QuotationOneStepCreationApiTests.cs` (nuevo)

**Interfaces:**
- Consumes: `CreateQuotationCommand` (Task 1), `PreviewNewQuotationQuery` (Task 3), `IQuotationResponseComposer`.
- Produces: `POST /api/v1/tenants/{tenantId:guid}/quotations/preview` → `200 QuotationResponse`
  (`id = 00000000-…`, `quotationNumber = ""`, `version = 0`); `POST /quotations` con el cuerpo extendido → `201`.

- [ ] **Step 1: Escribe las pruebas que fallan** en `QuotationOneStepCreationApiTests.cs`, con
  `using static QuotationsApiHarness` y `QepApiFactory` como `QuotationEditsApiTests`. Cuerpo base:
  `new CreateQuotationRequest(clientId, null, null, "Entregar el lunes", null, null, Items: [new(productId, 2m)])`
  con un producto de `CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m)`.

  ```csharp
  [Fact] public async Task CreateWithTheWholeDocumentReturnsItsLinesAndTotals()
  // 201, Location termina en /quotations/{id}, Items.Count == 1, Total == 200_000m, Notes == "Entregar el lunes",
  // y GET /quotations/{id} devuelve lo mismo
  [Fact] public async Task CreateWithLinesLeavesCreatedAndItemAddedInTheHistory()
  // GET /history → una entrada "Created" y una con Details que contiene el nombre del producto;
  // OutboxMessagesAsync(factory, "platform.audit.recorded.v1") contiene created e item_added
  [Fact] public async Task PreviewCalculatesWithoutPersisting()
  // 200, Id == Guid.Empty, QuotationNumber == "", Version == 0, Total == 200_000m;
  // GET /quotations → Total == 0; el outbox de auditoría no cambió de tamaño
  [Fact] public async Task PreviewDoesNotSpendTheNumber()
  // SetQuotationCounterAsync(factory, tenantId, year: 0, nextValue: 90_001L) + formato "CT" sin año
  // (como QuotationApiTests.CreateUsesThePrefixAndCounterConfiguredForTheTenant); preview dos veces;
  // la creación siguiente es "CT90001"
  [Fact] public async Task ACreationRejectedByAnItemDoesNotSpendTheNumber()
  // mismo armado; crear con un productId inexistente → 422 quotation.item.product_not_found;
  // la creación válida siguiente es "CT90001" (Review Focus 1, P1)
  [Fact] public async Task PreviewAndCreateAgreeOnTheTotal()
  // cuenta de cobro USD (CreateCompanyWithBankAccountAsync(client, tenantId, "USD")) y producto con
  // prices: { ["COP"] = 100_000m, ["USD"] = 25m } y envío cobrado (como QuotationShippingApiTests);
  // mismo cuerpo a /preview y a / → mismos Total, Subtotal, TaxAmount, Currency == "USD"
  [Fact] public async Task BothRoutesAreForbiddenForAnotherTenant()
  // como QuotationApiTests.CreateForAnotherTenantIsForbidden (:226-243), para / y /preview → 403
  [Fact] public async Task BothRoutesAreForbiddenWithoutTheManagePermission()
  // RegisterTenantAsync(factory, QuotationsPermissions.QuotationRead) → 403 en las dos (no 500)
  [Fact] public async Task ACustomerOfAnotherTenantIsNotFoundInBothRoutes()
  // clientId creado en otro tenant, ruta propia → 422 quotation.quotation.client_not_found en / y /preview
  [Fact] public async Task AProductOfAnotherTenantIsTheSaveError()
  // productId de otro tenant en items → 422 con el mismo código que da hoy el PUT
  // (QuotationEditsApiTests.PreviewReportsTheSameDomainErrorAsSaving: quotation.item.product_not_found)
  [Fact] public async Task AnIncompleteCustomerIsRejectedInBothRoutes()
  // IncompleteCustomerQuotationApiTests.MarkIncompleteAsync → 422 quotation.quotation.client_incomplete en las dos
  [Fact] public async Task ARepeatedProductIsAFieldErrorInBothRoutes()
  // 422 validation.failed y errors contiene "Items"
  ```
  `SetQuotationCounterAsync` es `internal static` en `QuotationApiTests.cs:106`: llámalo como
  `QuotationApiTests.SetQuotationCounterAsync(...)`, igual que se usa
  `DocumentNumberingFormatLookupTests.SetDocumentNumberFormatAsync`.

- [ ] **Step 2: Corre y verifica RED**
  `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~QuotationOneStepCreationApiTests"`
  Esperado: `/preview` da 405 (o 404) y la creación con líneas devuelve `Items` vacío.

- [ ] **Step 3: Cablea las rutas.** En `CreateQuotationAsync` pasa `ToItems(request)`, `request.GlobalScaleFloor` y
  `request.IsRetail` al comando; agrega la sobrecarga `ToItems(CreateQuotationRequest)`. Mapea
  `group.MapPost("/preview", PreviewNewQuotationAsync)` **después** de `MapPost("/")`, con
  `.RequireAuthorization(QuotationsPermissions.QuotationManage)`, `.Accepts<CreateQuotationRequest>("application/json")`,
  `.Produces<QuotationResponse>()`, `ProducesProblem(403)` y `ProducesProblem(422)`, y el comentario de `/export`
  (`:23-28`): «`/preview` no choca con `/{quotationId:guid}`: la restricción de guid no lo acepta». El handler HTTP
  responde `Results.Ok(await composer.ComposeAsync(tenantId, quotation, …))`.

- [ ] **Step 4: Corre y verifica GREEN** — el comando del Step 2 (12 pasan) y, sin tocarlas:
  `--filter "FullyQualifiedName~QuotationApiTests|FullyQualifiedName~QuotationShippingApiTests|FullyQualifiedName~IncompleteCustomerQuotationApiTests|FullyQualifiedName~QuotationEditsApiTests"`.
  Luego `dotnet build tests/Modules/Reporting/Modules.Reporting.IntegrationTests --no-restore` (el harness compila sin
  cambios) y `dotnet test tests/ArchitectureTests --no-restore --filter "FullyQualifiedName~Quotations"`.

- [ ] **Step 5: Commit**
  ```bash
  git branch --show-current
  git add src/Modules/Quotations/Modules.Quotations.Api/QuotationEndpoints.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/
  git commit -m "feat(quotations): POST /quotations/preview y creación con el documento completo"
  ```

---

### Task 5: Documentación del contrato

**Files:**
- Modify: `README.md` (sección nueva antes de `:1105`), `docs/integracion-cotizaciones-y-pedidos.md:63-76,274-`

- [ ] **Step 1: README.** Sección `### Cotización en un paso: crear y calcular sin id` con: los dos endpoints y su
  permiso, el cuerpo de §7.2 del spec (JSON), que los tres campos nuevos son opcionales y el contrato `{ clientId }`
  sigue dando un borrador vacío, que el cálculo previo no gasta número ni escribe, que la vigencia ausente vuelve
  resuelta (hoy del tenant + 15), que crear con líneas deja `Created` + un `Edited` por línea, y que el despliegue va
  **antes** que el frontend (D-L7: un frontend nuevo contra un backend viejo crea cotizaciones sin líneas, sin error).

- [ ] **Step 2: Guía de integración.** En la tabla de endpoints, reemplaza la fila de `POST /quotations` por
  «`CreateQuotationRequest` (con `items`, `globalScaleFloor`, `isRetail` opcionales) | 201, arranca en `Draft` con las
  líneas que traiga» y agrega `POST /quotations/preview` | el mismo cuerpo | «200 `QuotationResponse` calculado **sin
  persistir** y sin gastar número: `id` vacío, `quotationNumber: ""`, `version: 0`. La vigencia ausente vuelve
  resuelta». En la tabla de códigos, suma `/quotations/preview` a las filas de `client_*`, `quotation.item.*` y
  `quotation.retail.floor_not_allowed`.

- [ ] **Step 3: Verifica** `dotnet format --verify-no-changes` y `dotnet build --no-restore` sin warnings.

- [ ] **Step 4: Commit**
  ```bash
  git branch --show-current
  git add README.md docs/integracion-cotizaciones-y-pedidos.md
  git commit -m "docs(quotations): crear y calcular una cotización en un paso"
  ```

---

## Preguntas abiertas para el owner

- **P6 (Version):** una cotización creada con `{ clientId }` nace con `version` 2 en vez de 1 (ya pasaba con envío).
  Si importa, la salida es no llamar a `UpdateDetails` cuando el cuerpo no trae nada del documento, a costa de dos
  caminos en el builder.
- **D-L2, D-L3 y D-L9** siguen a ratificar (spec §12); este plan las implementa con su valor por defecto.

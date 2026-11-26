# Plantillas de agente IA del sistema (backend) — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **El presupuesto de pruebas del workspace manda sobre el skill:** un solo bloque, un solo implementador, una revisión al final.

**Goal:** `GET …/ai/agent-templates` lista las plantillas de agente del sistema con `available` calculado por el backend, y `POST …/ai/agents/from-template` crea con una de ellas un agente en borrador. Primera plantilla: «Asistente de citas».

**Architecture:** Calco de las plantillas de chatbots (`SystemTemplateCatalog`): un puerto `ISystemAgentTemplateCatalog` en `Modules.Ai.Application/AgentTemplates.cs`, un catálogo singleton en `Modules.Ai.Infrastructure/Seed/` que lee los `Seed/AgentTemplates/*.json` embebidos una vez y tumba el arranque si uno está roto, y dos handlers que reusan lo de crear un agente (`CreateAgentHandler.EnsureNameIsFreeAsync`, `ModelCatalog.DefaultFor`, `AgentEditorBuilder`).

**Tech Stack:** .NET 10, minimal APIs, System.Text.Json, FluentValidation, xUnit v3, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-23-plantillas-de-agente-ia-design.md`

**Worktree:** `C:/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend-worktrees/agent-templates`, rama `feature/agent-templates`. Antes de tocar archivos y antes de cada commit: `git branch --show-current` debe decir `feature/agent-templates`.

## Global Constraints

- Permiso de las dos rutas: `AiPermissions.AgentManage` (`ai.agent.manage`), en `RequireAuthorization` **y** en el handler con `AiAuthorization.EnsureAsync` (que también exige el módulo `ai`). No hay permiso nuevo.
- Rutas: `GET /api/v1/tenants/{tenantId:guid}/ai/agent-templates` (en `group`, no en `agents`) y `POST /api/v1/tenants/{tenantId:guid}/ai/agents/from-template` (en `agents`). Las dos sin `Location`.
- Respuesta del `GET`: lista sin envoltorio de `AgentTemplateDto(string Id, string Name, string Description, IReadOnlyList<string> Requires, bool Available)`. Respuesta del `POST`: `201` con `AgentEditorDto`.
- Códigos nuevos en `AiErrorCodes`: `TemplateNotFound = "ai.template.not_found"` (404) y `TemplateModuleNotEnabled = "ai.template.module_not_enabled"` (422). Nombre tomado: `409 ai.agent.name_taken`.
- `available` = toda clave de `requires` efectiva (`TenantModuleSet.IsEnabled`); con `ITenantModules.FindAsync` en `null`, `true`.
- El agente nace en borrador, sin conexiones, con `ModelCatalog.DefaultFor(catalog)` y auditado con `AiAuditActions.AgentCreated`. El nombre se recorta (`Trim`) como en importar.
- Plantilla: `id` kebab (`^[a-z0-9]+(-[a-z0-9]+)*$`), ≤ 64 (`SystemAgentTemplate.IdMaxLength`), igual al nombre del archivo; `name` 1..`Agent.NameMaxLength` (80); `description` 1..300 (`SystemAgentTemplate.DescriptionMaxLength`); `config` sin `model`, sin `quotationDraft` no nulo, sin `handoff.memberId` no nulo, leído con `JsonUnmappedMemberHandling.Disallow`.
- Sin migración.
- Texto de la plantilla: el apéndice A del spec, **copiado tal cual**.
- TDD con evidencia literal de RED y GREEN. Un RED por compilación usa la salida del `dotnet build` del proyecto de pruebas, sin contenedores.
- Integración filtrada por nombre (`--filter "FullyQualifiedName~<Prueba>"`); la clase `AgentTemplatesApiTests` entera una sola vez, al final del bloque. Nunca dos corridas a la vez: antes de cada una, `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay otra, esperar, no detenerla. Pruebas en primer plano. Si `Api.exe` del developer bloquea el build, avisar; no detenerlo.
- Conventional commits **sin atribución de IA ni trailer `Co-Authored-By`**. Nunca a `main`. `docs/estado.md` no se toca en la rama.

## Review Focus

1. Un tenant sin la Agenda pide crear desde la plantilla aunque la galería la mostrara como no disponible (pestaña vieja) → 422 `ai.template.module_not_enabled`, nunca un agente que no puede publicar. Lo cubre `AdoptingATemplateIsRejectedWithItsCode` (Tarea 4).
2. Una clave mal escrita en el JSON de una plantilla (`answerOnlyFromSource`) → el host no arranca, no un agente con el valor por defecto. `ABrokenTemplateDoesNotStart` con el caso `unknown-config-property` (Tarea 1).
3. `templateId` de una plantilla de **chatbot** (`welcome-menu-handoff`) → 404 `ai.template.not_found`, no se cruza con Automations. Caso dentro de `AdoptingATemplateIsRejectedWithItsCode` (Tarea 4).
4. Cuerpo sin `templateId` o con `name` en blanco → 422 `validation.failed` con el campo, nunca 500. `CreateAgentFromTemplateValidatorTests` (Tarea 4).
5. Un JSON de plantilla que trae `model` o un `memberId` de un tenant → el host no arranca. Casos `config-with-model` y `config-with-member` de `ABrokenTemplateDoesNotStart` (Tarea 1).

---

### Task 1: El catálogo y sus reglas de arranque

**Files:**
- Create: `src/Modules/Ai/Modules.Ai.Application/AgentTemplates.cs`
- Create: `src/Modules/Ai/Modules.Ai.Infrastructure/Seed/SystemAgentTemplateCatalog.cs`
- Test: `tests/Modules/Ai/Modules.Ai.UnitTests/SystemAgentTemplatesTests.cs` (nuevo)

**Interfaces:**
- Produces, en `AgentTemplates.cs` (`namespace Modules.Ai.Application`):
  - `public sealed record SystemAgentTemplate(string Id, string Name, string Description, IReadOnlyList<TenantModuleKey> Requires, string ConfigJson)` con `public const int IdMaxLength = 64; public const int DescriptionMaxLength = 300;` y `public AgentConfig ConfigFor(AgentModelRef model)` → `AgentConfig.FromJson(ConfigJson) with { Model = model }`.
  - `public interface ISystemAgentTemplateCatalog { IReadOnlyList<SystemAgentTemplate> All { get; } SystemAgentTemplate? Find(string id); }`
- Produces, en `SystemAgentTemplateCatalog.cs` (`namespace Modules.Ai.Infrastructure.Seed`):
  - `internal sealed class SystemAgentTemplateCatalog : ISystemAgentTemplateCatalog` con `public SystemAgentTemplateCatalog()` (su propio ensamblado), `internal SystemAgentTemplateCatalog(Assembly assembly)` y `internal SystemAgentTemplateCatalog(IReadOnlyList<(string ResourceName, string Json)> files)`. Los dos primeros leen los recursos que contienen `.Seed.AgentTemplates.` y terminan en `.json`, ordenados ordinal, y delegan en el tercero. `Find` es ordinal; `_byId` es `Dictionary<string, SystemAgentTemplate>`.
  - Mensajes (`InvalidOperationException`), que las pruebas buscan por fragmento:
    - sin archivos: `No hay plantillas de agente del sistema (recursos *.Seed.AgentTemplates.*.json): revisa el EmbeddedResource del csproj.`
    - JSON ilegible o propiedad desconocida: `La plantilla «{recurso}» no se puede leer: {mensaje de la JsonException}`
    - campo faltante: `Falta «{campo}» en la plantilla «{recurso}».`
    - id: `El id «{id}» de la plantilla «{recurso}» tiene que ser kebab-case, de hasta 64 caracteres, e igual al nombre del archivo.`
    - repetido: `Hay dos plantillas de agente con el id «{id}».`
    - nombre / descripción: `«name» de la plantilla «{recurso}» tiene que tener entre 1 y 80 caracteres.` (y lo mismo con `«description»` y 300)
    - módulo: `La plantilla «{recurso}» pide el módulo «{clave}», que no existe.` (atrapa la `ArgumentException` de `TenantModuleKey.Parse`) y `La plantilla «{recurso}» repite el módulo «{clave}».`
    - config: `El config de la plantilla «{recurso}» no puede traer «model»: el modelo lo elige el tenant.`, `… no puede traer «quotationDraft»: lleva datos del tenant.`, `… no puede traer «handoff.memberId»: lleva datos del tenant.`
  - `config` se lee con un `JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }` propio del catálogo, sólo para validar; `ConfigJson` guarda el texto crudo de `config`.

- [ ] **Step 1: Write the failing tests** — en `SystemAgentTemplatesTests`, con un helper `private static JsonObject Valid(string id = "prueba")` que arma `{ id, name: "Prueba", description: "Una plantilla de prueba.", requires: ["scheduling"], config: <un config válido sin model: instrucciones no vacías, tone "friendly", faq [], tools todos false salvo scheduling true, handoff unassigned con memberId null, 20 y 2, trigger "NewConversation"> }` y `private static (string, string) File(JsonObject json, string? fileId = null)` → `($"Modules.Ai.Infrastructure.Seed.AgentTemplates.{fileId ?? id}.json", json.ToJsonString())`:
  - `AValidFileLoadsWithItsModulesAndItsConfig`: catálogo con un archivo → `All` tiene uno; `Find("prueba")!.Requires` es `[TenantModuleKeys.Scheduling]`; `ConfigFor(new AgentModelRef("anthropic", "claude-opus-5-5")).Tools.Scheduling` es `true` y su `Model.Id` es `"claude-opus-5-5"`; `Find("otra")` es `null`.
  - `ACatalogWithoutTemplateResourcesDoesNotStart`: `new SystemAgentTemplateCatalog(typeof(SystemAgentTemplatesTests).Assembly)` lanza con `"No hay plantillas de agente del sistema"`.
  - `[Theory] ABrokenTemplateDoesNotStart(string mutation, string fragment)` con estos casos (cada uno muta `Valid()` o arma dos archivos):

    | `mutation` | Qué hace | `fragment` |
    | --- | --- | --- |
    | `not-json` | el texto `"{"` | `no se puede leer` |
    | `missing-config` | quita `config` | `Falta «config»` |
    | `id-not-kebab` | `id = "Prueba_1"` (y archivo igual) | `kebab-case` |
    | `id-not-file-name` | archivo `otra`, `id = "prueba"` | `kebab-case` |
    | `duplicate-id` | dos entradas válidas con el mismo recurso `…AgentTemplates.prueba.json` (en un ensamblado no pasa, porque el id es el nombre del archivo; la regla se conserva como en chatbots) | `dos plantillas` |
    | `name-too-long` | `name` de 81 caracteres | `«name»` |
    | `unknown-module` | `requires = ["agenda"]` | `«agenda», que no existe` |
    | `repeated-module` | `requires = ["scheduling","scheduling"]` | `repite el módulo` |
    | `config-with-model` | `config.model = { provider: "anthropic", id: "x" }` | `«model»` |
    | `config-with-quotation-draft` | `config.quotationDraft = { advisorMemberId: <guid>, billingAccount: null }` | `«quotationDraft»` |
    | `config-with-member` | `config.handoff.memberId = <guid>` | `«handoff.memberId»` |
    | `unknown-config-property` | agrega `config.answerOnlyFromSource = true` | `no se puede leer` |

    Assert: `Assert.Contains(fragment, Assert.Throws<InvalidOperationException>(() => new SystemAgentTemplateCatalog(files)).Message, StringComparison.Ordinal)`.
- [ ] **Step 2: Run to verify RED** — `dotnet build tests/Modules/Ai/Modules.Ai.UnitTests`. Expected: `CS0246 … 'SystemAgentTemplateCatalog' could not be found`.
- [ ] **Step 3: Implement** el record, el puerto y el catálogo con las reglas en el orden de la tabla (leer → campos → id → nombre y descripción → módulos → config → repetidos).
- [ ] **Step 4: Run to verify GREEN** — `dotnet test tests/Modules/Ai/Modules.Ai.UnitTests --filter "FullyQualifiedName~SystemAgentTemplatesTests"` → todas pasan (14 casos).
- [ ] **Step 5: Commit** — `feat(ai): load system agent templates from embedded json`

### Task 2: «Asistente de citas», registro y arranque

**Files:**
- Create: `src/Modules/Ai/Modules.Ai.Infrastructure/Seed/AgentTemplates/asistente-de-citas.json` (el apéndice A del spec, tal cual)
- Modify: `src/Modules/Ai/Modules.Ai.Infrastructure/Modules.Ai.Infrastructure.csproj` (`<EmbeddedResource Include="Seed\AgentTemplates\*.json" />`)
- Modify: `src/Modules/Ai/Modules.Ai.Infrastructure/AiInfrastructureExtensions.cs` (`services.AddSingleton<ISystemAgentTemplateCatalog, SystemAgentTemplateCatalog>();`)
- Modify: `src/Modules/Ai/Modules.Ai.Infrastructure/AiDatabaseInitializer.cs` (`services.GetRequiredService<ISystemAgentTemplateCatalog>();` antes de la migración, con el comentario de `AutomationsDatabaseInitializer`)
- Modify: `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs` (entrada `StateEntry` junto a la de `SystemTemplateCatalog`: `"Modules.Ai.Infrastructure.Seed.SystemAgentTemplateCatalog"`, `PerPodByDesign`, `"Modules/Ai/Modules.Ai.Infrastructure/Seed/SystemAgentTemplateCatalog.cs:<línea del _byId> — plantillas embebidas leídas al arrancar; igual en cada pod"`)
- Test: `tests/Modules/Ai/Modules.Ai.UnitTests/SystemAgentTemplatesTests.cs`

**Interfaces:**
- Consumes: `SystemAgentTemplateCatalog()` y `ConfigFor` de la Tarea 1; `AgentConfigFieldsValidator` (`Agents.cs`); `AgentConfigValidator.Validate(AgentConfig, AgentValidationContext)`.
- Produces: la plantilla `asistente-de-citas` en el catálogo real, resuelta al arrancar.

- [ ] **Step 1: Write the failing tests** — con `private static readonly Lazy<SystemAgentTemplateCatalog> Real = new(() => new SystemAgentTemplateCatalog());` (perezoso a propósito: un inicializador estático que lanza tumbaría también las pruebas de la Tarea 1 de la misma clase) y `private static readonly AgentModelRef DefaultModel = new("anthropic", "claude-opus-5-5");`. `Catalog` abajo es `Real.Value`:
  - `TheCatalogHasExactlyTheAppointmentAssistant`: `Assert.Equal(["asistente-de-citas"], Catalog.All.Select(t => t.Id))`; `name == "Asistente de citas"`; `Requires` es `[TenantModuleKeys.Scheduling]`.
  - `EverySystemAgentTemplateIsPublishableOnceTheTenantChoosesItsPieces`: por cada plantilla, `config = template.ConfigFor(DefaultModel)`; `new AgentConfigFieldsValidator().Validate(config).IsValid`; `AgentConfigValidator.Validate(config, new AgentValidationContext(ConnectionCount: 1, AgentModelAvailability.Available, HandoffMemberCanReply: null, Publishing: true, Scheduling: config.Tools.Scheduling ? new AgentSchedulingReadiness(true, 1) : null))` sin ningún `AgentIssueSeverity.Error`; `config.Tools.Scheduling == template.Requires.Contains(TenantModuleKeys.Scheduling)`; `template.Name.Length <= Agent.NameMaxLength`.
  - `TheAppointmentAssistantKeepsItsContract`: de `Find("asistente-de-citas")!.ConfigFor(DefaultModel)`: `Tone == "friendly"`; `Tools == new AgentTools(Catalog: false, OrderStatus: false, QuotationDraft: false, Scheduling: true)`; `AnswerOnlyFromSources` y `!UseQuickReplies`; `Faq.Select(f => f.Id)` es `["horarios-de-atencion", "cancelar-o-reprogramar"]`; `Handoff == new AgentHandoff("unassigned", null, "Te paso con una persona del equipo para que te ayude por este mismo chat.", 20, 2)`; `Trigger == "NewConversation"`; `Greeting` empieza por `"¡Hola! Soy el asistente de citas."`; `Instructions` contiene `"check_availability"` y `"book_appointment"`; `QuotationDraft` es `null`.
- [ ] **Step 2: Run to verify RED** — `dotnet test tests/Modules/Ai/Modules.Ai.UnitTests --filter "FullyQualifiedName~SystemAgentTemplatesTests"`. Expected: las tres nuevas fallan con `InvalidOperationException: No hay plantillas de agente del sistema…` (sin el JSON ni el `EmbeddedResource`). Las de la Tarea 1 siguen pasando.
- [ ] **Step 3: Implement** — el JSON (apéndice A, copiado), el `EmbeddedResource`, el singleton, la resolución en `InitializeAiDatabaseAsync` y la entrada del inventario.
- [ ] **Step 4: Run to verify GREEN**
  - `dotnet test tests/Modules/Ai/Modules.Ai.UnitTests --filter "FullyQualifiedName~SystemAgentTemplatesTests"` → todas pasan.
  - `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~MultiReplicaInventoryTests|FullyQualifiedName~AiLayerTests"` → pasan (sin la entrada, `EveryStatefulSingletonIsClassified` falla nombrando `SystemAgentTemplateCatalog`).
- [ ] **Step 5: Commit** — `feat(ai): add the appointment assistant system template`

### Task 3: `GET …/ai/agent-templates`

**Files:**
- Modify: `src/Modules/Ai/Modules.Ai.Application/AgentTemplates.cs`
- Modify: `src/Modules/Ai/Modules.Ai.Api/AiEndpoints.cs` (en `group`, después de `MapGet("/connections", …)`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (junto a `ListAgentsHandler`)
- Test: `tests/Modules/Ai/Modules.Ai.UnitTests/SystemAgentTemplatesTests.cs`, `tests/Modules/Ai/Modules.Ai.IntegrationTests/AgentTemplatesApiTests.cs` (nuevo)

**Interfaces:**
- Consumes: `ISystemAgentTemplateCatalog`, `SystemAgentTemplate.Requires` (Tarea 1).
- Produces, en `AgentTemplates.cs`:
  - `public static class AgentTemplateModules { public static IReadOnlyList<TenantModuleKey> Missing(SystemAgentTemplate template, TenantModuleSet? modules); }` → `[]` con `modules` nulo; si no, las de `Requires` con `!modules.IsEnabled(key)`, en el orden de `Requires`.
  - `public sealed record AgentTemplateDto(string Id, string Name, string Description, IReadOnlyList<string> Requires, bool Available);` (`Requires` = `key.Value`).
  - `public sealed record ListAgentTemplatesQuery(Guid TenantId) : IQuery<IReadOnlyList<AgentTemplateDto>>;`
  - `public sealed class ListAgentTemplatesHandler(IExecutionContext executionContext, ITenantModules tenantModules, ISystemAgentTemplateCatalog catalog) : IQueryHandler<ListAgentTemplatesQuery, IReadOnlyList<AgentTemplateDto>>` — `AiAuthorization.EnsureAsync(…, AiPermissions.AgentManage, …)`, un `tenantModules.FindAsync` por request, `Available = Missing(...).Count == 0`.
  - Endpoint `group.MapGet("/agent-templates", ListAgentTemplatesAsync).RequireAuthorization(AiPermissions.AgentManage).Produces<IReadOnlyList<AgentTemplateDto>>().ProducesProblem(StatusCodes.Status403Forbidden);`
  - Registro `services.AddScoped<IQueryHandler<ListAgentTemplatesQuery, IReadOnlyList<AgentTemplateDto>>, ListAgentTemplatesHandler>();`

- [ ] **Step 1: Write the failing tests**
  - Unit `MissingModulesAreTheRequiredOnesThatAreOff`: con `Catalog.Find("asistente-de-citas")!` → `Missing(t, null)` vacío; `Missing(t, TenantModuleSet.FromStored([TenantModuleKeys.Customers]))` es `[TenantModuleKeys.Scheduling]`; `Missing(t, TenantModuleSet.FromStored([TenantModuleKeys.Customers, TenantModuleKeys.Scheduling]))` vacío; `Missing(t, TenantModuleSet.FromStored([TenantModuleKeys.Scheduling]))` es `[TenantModuleKeys.Scheduling]` (sin `customers` no es efectiva).
  - Integración `AgentTemplatesApiTests` (`using static AiApiHarness`; `Manage = [AiPermissions.AgentRead, AiPermissions.AgentManage]`; `Templates => $"{AiUrl()}/agent-templates"`):
    - `TheTemplateCatalogSaysWhatTheTenantCanAdopt`: `SeedTenantAsync` (todos los módulos) → `GET` 200, un elemento con `id == "asistente-de-citas"`, `name == "Asistente de citas"`, `requires == ["scheduling"]`, `available == true`. Luego `ExecuteAsync(…, "UPDATE tenancy.tenant_modules SET status = 'inactive' WHERE tenant_id = @t AND module_key = 'scheduling'", ("t", Guid.Parse(TenantId)))` → `available == false`.
    - `ListingTemplatesNeedsManage`: cliente con sólo `AiPermissions.AgentRead` → 403.
- [ ] **Step 2: Run to verify RED** — `dotnet build tests/Modules/Ai/Modules.Ai.UnitTests` y `dotnet build tests/Modules/Ai/Modules.Ai.IntegrationTests`. Expected: `CS0103 'AgentTemplateModules' does not exist` en el unitario. La integración no se corre en RED: sin la ruta respondería 404, y eso no prueba nada que el build no diga.
- [ ] **Step 3: Implement** el helper, el DTO, la query, el handler, el endpoint y el registro.
- [ ] **Step 4: Run to verify GREEN**
  - `dotnet test tests/Modules/Ai/Modules.Ai.UnitTests --filter "FullyQualifiedName~MissingModulesAreTheRequiredOnesThatAreOff"` → pasa.
  - `dotnet test tests/Modules/Ai/Modules.Ai.IntegrationTests --filter "FullyQualifiedName~TheTemplateCatalogSaysWhatTheTenantCanAdopt|FullyQualifiedName~ListingTemplatesNeedsManage"` → pasan.
- [ ] **Step 5: Commit** — `feat(ai): list the system agent templates with their availability`

### Task 4: `POST …/ai/agents/from-template`

**Files:**
- Modify: `src/Modules/Ai/Modules.Ai.Domain/AiErrorCodes.cs`
- Modify: `src/Modules/Ai/Modules.Ai.Application/AiSupport.cs` (`AiNotFound`)
- Modify: `src/Modules/Ai/Modules.Ai.Application/AgentTemplates.cs`
- Modify: `src/Modules/Ai/Modules.Ai.Api/AiEndpoints.cs` (en `agents`, después de `MapPost("/import", …)`; record `CreateAgentFromTemplateRequest` junto a `ImportAgentRequest`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (junto a `ImportAgentHandler`)
- Test: `tests/Modules/Ai/Modules.Ai.UnitTests/CreateAgentFromTemplateValidatorTests.cs` (nuevo), `tests/Modules/Ai/Modules.Ai.IntegrationTests/AgentTemplatesApiTests.cs`

**Interfaces:**
- Consumes: `AgentTemplateModules.Missing` (Tarea 3), `ISystemAgentTemplateCatalog` y `ConfigFor` (Tarea 1), `CreateAgentHandler.EnsureNameIsFreeAsync`, `ModelCatalog.DefaultFor`, `AgentRules.Name`.
- Produces:
  - `AiErrorCodes.TemplateNotFound = "ai.template.not_found"` (404) y `AiErrorCodes.TemplateModuleNotEnabled = "ai.template.module_not_enabled"` (422), con `<summary>` como los vecinos.
  - `AiNotFound.Template(string id)` → `new ResourceNotFoundException(AiErrorCodes.TemplateNotFound, $"Agent template '{id}' was not found.")`.
  - `public sealed record CreateAgentFromTemplateCommand(Guid TenantId, string TemplateId, string Name) : ICommand<AgentEditorDto>;`
  - `public sealed class CreateAgentFromTemplateValidator : AbstractValidator<CreateAgentFromTemplateCommand>` — `AgentRules.Name(RuleFor(c => c.Name))`; `RuleFor(c => c.TemplateId).NotEmpty().MaximumLength(SystemAgentTemplate.IdMaxLength)`.
  - `public sealed class CreateAgentFromTemplateHandler(IExecutionContext executionContext, ITenantModules tenantModules, AiCaller caller, IAgentRepository agents, IAiModelCatalog catalog, ISystemAgentTemplateCatalog templates, IAiUnitOfWork unitOfWork, IAiAuditRecorder audit, IClock clock, IValidator<CreateAgentFromTemplateCommand> validator, AgentEditorBuilder builder) : ICommandHandler<CreateAgentFromTemplateCommand, AgentEditorDto>` — en el orden del spec §4: autoriza → valida → `templates.Find(command.TemplateId) ?? throw AiNotFound.Template(...)` → `Missing(template, await tenantModules.FindAsync(...))` no vacío ⇒ `throw new AiDomainException(AiErrorCodes.TemplateModuleNotEnabled, $"La plantilla necesita módulos que no están activos: {string.Join(", ", missing)}.")` → `caller.RequireAsync` → `EnsureNameIsFreeAsync(agents, tenant, name.Trim(), null, ct)` → `Agent.Create(tenant, name.Trim(), template.ConfigFor(ModelCatalog.DefaultFor(catalog)), by, now)` → `agents.Add` → `audit.Record(…, AiAuditActions.AgentCreated, agent.Id, now)` → `SaveChangesAsync` → `builder.BuildAsync(agent, ct)`.
  - `public sealed record CreateAgentFromTemplateRequest(string? TemplateId, string? Name);` y endpoint `agents.MapPost("/from-template", CreateAgentFromTemplateAsync).RequireAuthorization(AiPermissions.AgentManage).Accepts<CreateAgentFromTemplateRequest>("application/json").Produces<AgentEditorDto>(StatusCodes.Status201Created)` + `ProducesProblem` 403, 404, 409, 422. El endpoint pasa `request.TemplateId ?? string.Empty` y `request.Name ?? string.Empty` (el validador da el 422) y responde `Results.Created((string?)null, …)`.
  - Registro `services.AddScoped<ICommandHandler<CreateAgentFromTemplateCommand, AgentEditorDto>, CreateAgentFromTemplateHandler>();` (el validador entra por `AddValidatorsFromAssemblyContaining`).

- [ ] **Step 1: Write the failing tests**
  - Unit `CreateAgentFromTemplateValidatorTests.AnEmptyNameOrTemplateIdIsRejected`: `new CreateAgentFromTemplateCommand(Guid.NewGuid(), "", " ")` → errores con `PropertyName` `"TemplateId"` y `"Name"`; un `TemplateId` de 65 caracteres → error en `"TemplateId"`; `("asistente-de-citas", "Citas")` → válido.
  - Integración `AnAgentFromATemplateIsBornAsADraftWithTheTemplatesConfig`: `POST {AiUrl()}/agents/from-template` con `{ templateId = "asistente-de-citas", name = "  Citas  " }` → 201; `name == "Citas"`, `status == "Draft"`, `version == 1`, `connectionIds` vacío; `draft.config.model` es `("anthropic", "claude-opus-5-5")`; `draft.config.tools.scheduling` `true`; `draft.config.tone == "friendly"`; `draft.config.faq` con 2; los `issues` contienen `connections_missing` y no `instructions_missing`; `SELECT count(*) FROM audit.entries WHERE action = 'ai.agent.created' AND resource_id = @id` es 1.
  - Integración `AdoptingATemplateIsRejectedWithItsCode`: con un agente ya creado de nombre `"Citas"`:
    - `templateId = "no-existe"` → 404 `ai.template.not_found`; `templateId = "welcome-menu-handoff"` → 404 `ai.template.not_found`.
    - `name = "citas"` → 409 `ai.agent.name_taken`.
    - `name = ""` → 422 `validation.failed`.
    - cliente con sólo `AgentRead` → 403.
    - después de `UPDATE tenancy.tenant_modules SET status = 'inactive' … module_key = 'scheduling'`, `name = "Otra"` → 422 `ai.template.module_not_enabled`, y `GET {AiUrl()}/agents` sigue con un solo agente.
- [ ] **Step 2: Run to verify RED** — `dotnet build tests/Modules/Ai/Modules.Ai.UnitTests`. Expected: `CS0246 … 'CreateAgentFromTemplateCommand' could not be found`.
- [ ] **Step 3: Implement** códigos, `AiNotFound.Template`, comando, validador, handler, request, endpoint y registro.
- [ ] **Step 4: Run to verify GREEN**
  - `dotnet test tests/Modules/Ai/Modules.Ai.UnitTests --filter "FullyQualifiedName~CreateAgentFromTemplateValidatorTests"` → pasa.
  - `dotnet test tests/Modules/Ai/Modules.Ai.IntegrationTests --filter "FullyQualifiedName~AnAgentFromATemplateIsBornAsADraftWithTheTemplatesConfig|FullyQualifiedName~AdoptingATemplateIsRejectedWithItsCode"` → pasan.
- [ ] **Step 5: Commit** — `feat(ai): create an agent from a system template`

### Cierre del bloque (una vez)

- [ ] `dotnet test tests/Modules/Ai/Modules.Ai.UnitTests` → todo pasa.
- [ ] `dotnet test tests/Modules/Ai/Modules.Ai.IntegrationTests --filter "FullyQualifiedName~AgentTemplatesApiTests|FullyQualifiedName~ABlankAgentIsBornAsADraftWithTheDefaultModelAndItsMissingPieces|FullyQualifiedName~AnImportedAgentIsBornAsADraftWithoutTheTenantsIds|FullyQualifiedName~ImportingATakenNameOrAChatbotFileIsRejected"` → la clase nueva entera más crear e importar.
- [ ] `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests|FullyQualifiedName~MultiReplicaInventoryTests|FullyQualifiedName~AiLayerTests"` → pasan.
- [ ] `dotnet format --verify-no-changes` sobre la solución (los comandos exactos, en README § Verificación).
- [ ] Una revisión del bloque (lente `review-reliability`: comportamiento y contrato HTTP). `Modules.Ai.IntegrationTests` completo sólo si el owner lo pide antes de mergear.

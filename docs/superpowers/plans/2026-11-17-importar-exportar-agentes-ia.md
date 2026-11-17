# Importar y exportar agentes IA (backend) — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `GET …/ai/agents/{id}/export` baja el borrador del agente sin lo que es del tenant, y `POST …/ai/agents/import` crea con ese archivo un agente nuevo en borrador.

**Architecture:** Un archivo nuevo `Modules.Ai.Application/AgentExport.cs` con el DTO del archivo, el saneo, la query, el comando, su validador y los dos handlers. Las reglas de campo del `config` salen de `SaveAgentDraftValidator` a un `AgentConfigFieldsValidator` que usan el borrador y el import. Los endpoints se suman al grupo `agents` de `AiEndpoints.cs`.

**Tech Stack:** .NET 10, minimal APIs, FluentValidation, xUnit v3, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-17-importar-exportar-agentes-ia-design.md`

**Worktree:** `C:/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend-worktrees/importar-agentes-ia`, rama `feature/importar-agentes-ia`. Antes de tocar archivos y antes de cada commit: `git branch --show-current` debe decir `feature/importar-agentes-ia`.

## Global Constraints

- `AgentExportDto.CurrentSchema = 1`; `AgentExportDto.KindValue = "aiAgent"`.
- Los dos endpoints exigen `AiPermissions.AgentManage`.
- Al exportar **y** al importar: `handoff.memberId = null` (se conserva `handoff.destination`) y `quotationDraft = null`. `model` viaja tal cual.
- `AgentLimits.ImportBodyMaxBytes = 1024 * 1024`, como metadato `RequestSizeLimitAttribute` del endpoint (el patrón de `PublicStoreEndpoints.cs:51`; Kestrel lo aplica, `TestServer` no, así que no hay prueba de 413).
- Nombre tomado: `409 ai.agent.name_taken` (`CreateAgentHandler.EnsureNameIsFreeAsync`, `Agents.cs`).
- Las claves del 422 del borrador **no cambian** (`Config.Faq[3].Answer`…): el frontend las mapea a campo.
- Auditoría del import: `AiAuditActions.AgentCreated`. El export no audita.
- TDD con evidencia literal de RED y GREEN. Un RED que falla por compilación usa la salida del build, sin contenedores.
- Integración filtrada por nombre (`--filter "FullyQualifiedName~<Prueba>"`), nunca dos corridas a la vez: antes, `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay otra, esperar, no detenerla. Si `Api.exe` del developer bloquea el build, avisar; no detenerlo.
- Conventional commits sin atribución de IA. Nunca a `main`.

## Review Focus

1. Un archivo de **chatbot** (`schema: 1`, sin `kind`) mandado al import → 422, no un agente vacío. Lo cubre `ImportAgentValidatorTests.AChatbotFileIsRejected` (Tarea 3).
2. Un archivo editado a mano con un `memberId` o un `quotationDraft` del tenant destino → igual quedan en `null`. Lo cubre `AnImportedAgentIsBornAsADraftWithoutTheTenantsIds` (Tarea 3), que manda los dos.
3. `export` ausente o `config: null` en el cuerpo → 422, nunca 500. `ImportAgentValidatorTests.AMissingConfigIsRejected` (Tarea 3).
4. Sacar las reglas a otro validador no cambia las claves del 422 del borrador. `AnOutOfRangeDraftIsRejectedWithTheField` (existente) corre en el GREEN de la Tarea 1.
5. Exportar un agente de otro tenant → 404, sin filtrar su `config`. `ExportingAnotherTenantsAgentIs404` (Tarea 2).

---

### Task 1: `AgentConfigFieldsValidator`

**Files:**
- Modify: `src/Modules/Ai/Modules.Ai.Application/Agents.cs:86-143` (`SaveAgentDraftValidator`)
- Test: `tests/Modules/Ai/Modules.Ai.UnitTests/AgentConfigFieldsValidatorTests.cs` (nuevo)

**Interfaces:**
- Produces: `public sealed class AgentConfigFieldsValidator : AbstractValidator<AgentConfig>` en `Agents.cs`, con exactamente las reglas que hoy están dentro del `When(command.Config is not null, …)` de `SaveAgentDraftValidator`, reescritas sobre `AgentConfig`. `SaveAgentDraftValidator` queda en `RuleFor(c => c.Config).NotNull().SetValidator(new AgentConfigFieldsValidator());`.

- [ ] **Step 1: Write the failing test** — `AgentConfigFieldsValidatorTests`, sobre `AgentConfig.Default(new AgentModelRef("anthropic", "claude-sonnet-5-5"))` con `with`:
  - `TheDefaultConfigIsValid`: `Assert.True(new AgentConfigFieldsValidator().Validate(config).IsValid)`.
  - `AnUnknownToneIsRejected`: `Tone = "loud"` → un error con `PropertyName == "Tone"`.
  - `AFaqAnswerOverTheLimitIsRejected`: `Faq = [new AgentFaq("a", "¿?", new string('x', AgentConfig.FaqAnswerMaxLength + 1))]` → `PropertyName == "Faq[0].Answer"`.
- [ ] **Step 2: Run to verify RED** — `dotnet build tests/Modules/Ai/Modules.Ai.UnitTests`. Expected: `CS0246 … 'AgentConfigFieldsValidator' could not be found`.
- [ ] **Step 3: Implement** el validador y cambia `SaveAgentDraftValidator` a `SetValidator`.
- [ ] **Step 4: Run to verify GREEN**
  - `dotnet test tests/Modules/Ai/Modules.Ai.UnitTests --filter "FullyQualifiedName~AgentConfigFieldsValidatorTests"` → 3 pasan.
  - `dotnet test tests/Modules/Ai/Modules.Ai.IntegrationTests --filter "FullyQualifiedName~AnOutOfRangeDraftIsRejectedWithTheField"` → pasa (las claves del 422 no cambiaron).
- [ ] **Step 5: Commit** — `refactor(ai): share the agent config field rules`

### Task 2: Exportar

**Files:**
- Create: `src/Modules/Ai/Modules.Ai.Application/AgentExport.cs`
- Modify: `src/Modules/Ai/Modules.Ai.Api/AiEndpoints.cs` (grupo `agents`, junto a `MapPost("/", CreateAgentAsync)`)
- Test: `tests/Modules/Ai/Modules.Ai.UnitTests/AgentExportTests.cs` (nuevo), `tests/Modules/Ai/Modules.Ai.IntegrationTests/AgentsApiTests.cs`

**Interfaces:**
- Produces, en `AgentExport.cs`:
  - `public sealed record AgentExportDto(int Schema, string Kind, string Name, AgentConfig Config, DateTimeOffset ExportedAt)` con `public const int CurrentSchema = 1; public const string KindValue = "aiAgent";`
  - `public static class AgentExport { public static AgentConfig Portable(AgentConfig config); }` → `config with { Handoff = config.Handoff with { MemberId = null }, QuotationDraft = null }`.
  - `public sealed record ExportAgentQuery(Guid TenantId, Guid AgentId) : IQuery<AgentExportDto>;` y `ExportAgentHandler` (autoriza con `AiAuthorization.EnsureAsync(…, AiPermissions.AgentManage, …)`, busca con `IAgentRepository.FindAsync`, `404` con `AiNotFound.Agent(id)`, `ExportedAt = clock.UtcNow`, `Config = AgentExport.Portable(agent.DraftConfig)`).
  - Endpoint `agents.MapGet("/{agentId:guid}/export", ExportAgentAsync)` con `.RequireAuthorization(AiPermissions.AgentManage)`, `.Produces<AgentExportDto>()`, `403`, `404`.

- [ ] **Step 1: Write the failing tests**
  - Unit `AgentExportTests.PortableDropsTheMemberAndTheQuotationDraftButKeepsTheRest`: config con `Handoff.Destination = "member"`, `MemberId = Guid.NewGuid()`, `QuotationDraft = new(Guid.NewGuid(), new AgentBillingAccount(Guid.NewGuid(), "Banco", "123", "COP"))`, `Tone = "friendly"`. Asserts: `MemberId` null, `Destination == "member"`, `QuotationDraft` null, `Tone == "friendly"`, `Model` igual.
  - Integración `AgentsApiTests.TheExportCarriesTheDraftWithoutTheTenantsIds`: `PublishableAsync` con un config como `ValidConfig()` pero `handoff.destination = "member"`, `handoff.memberId = Guid.NewGuid()`, `tools.quotationDraft = true` y `quotationDraft = new { advisorMemberId = Guid.NewGuid(), billingAccount = new { companyId = Guid.NewGuid(), bankName = "Banco", accountNumber = "123", currency = "COP" } }`. `GET {Agents}/{id}/export` → 200; `schema == 1`, `kind == "aiAgent"`, `name == "Asesor virtual"`, `config.instructions` igual al enviado, `config.handoff.destination == "member"`, `config.handoff.memberId` es `JsonValueKind.Null`, `config.quotationDraft` es `Null`, `config.model.provider == "anthropic"`; el JSON no tiene `connectionIds`.
  - Integración `AgentsApiTests.ExportingAnotherTenantsAgentIs404`: un `Guid.NewGuid()` como `agentId` → 404 con código `ai.agent.not_found`; y un cliente con sólo `AgentRead` → 403.
- [ ] **Step 2: Run to verify RED** — `dotnet build tests/Modules/Ai/Modules.Ai.UnitTests` → `CS0103 'AgentExport' does not exist`.
- [ ] **Step 3: Implement** `AgentExport.cs` (DTO, `Portable`, query, handler) y el endpoint.
- [ ] **Step 4: Run to verify GREEN** — unit `--filter "FullyQualifiedName~AgentExportTests"`; integración `--filter "FullyQualifiedName~TheExportCarriesTheDraftWithoutTheTenantsIds|FullyQualifiedName~ExportingAnotherTenantsAgentIs404"`. Todo pasa.
- [ ] **Step 5: Commit** — `feat(ai): export an agent's draft without tenant ids`

### Task 3: Importar

**Files:**
- Modify: `src/Modules/Ai/Modules.Ai.Application/AgentExport.cs`, `src/Modules/Ai/Modules.Ai.Application/Agents.cs` (`AgentLimits.ImportBodyMaxBytes`), `src/Modules/Ai/Modules.Ai.Api/AiEndpoints.cs`
- Test: `tests/Modules/Ai/Modules.Ai.UnitTests/ImportAgentValidatorTests.cs` (nuevo), `tests/Modules/Ai/Modules.Ai.IntegrationTests/AgentsApiTests.cs`

**Interfaces:**
- Consumes: `AgentExportDto`, `AgentExport.Portable` (Tarea 2); `AgentConfigFieldsValidator` (Tarea 1); `CreateAgentHandler.EnsureNameIsFreeAsync`, `AgentRules.Name` (`Agents.cs`).
- Produces:
  - `public sealed record ImportAgentCommand(Guid TenantId, string Name, AgentExportDto Export) : ICommand<AgentEditorDto>;`
  - `public sealed class ImportAgentValidator : AbstractValidator<ImportAgentCommand>`: `AgentRules.Name(RuleFor(c => c.Name))`; `Export` NotNull; con `Export` presente: `Schema` `Equal(AgentExportDto.CurrentSchema)`, `Kind` `Equal(AgentExportDto.KindValue)`, `Config` `NotNull().SetValidator(new AgentConfigFieldsValidator())`.
  - `ImportAgentHandler`: el cuerpo de `CreateAgentHandler.HandleAsync` con `config = AgentExport.Portable(command.Export.Config)` y `command.Name.Trim()`.
  - `public sealed record ImportAgentRequest(string? Name, AgentExportDto? Export);` en `AiEndpoints.cs`. El endpoint arma `new ImportAgentCommand(tenantId, request.Name ?? request.Export?.Name ?? string.Empty, request.Export!)` (un `Export` nulo lo frena el validador) y responde `Results.Created((string?)null, editor)`.
  - `agents.MapPost("/import", ImportAgentAsync)` con `RequireAuthorization(AgentManage)`, `.WithMetadata(new RequestSizeLimitAttribute(AgentLimits.ImportBodyMaxBytes))`, `Accepts<ImportAgentRequest>("application/json")`, `201 AgentEditorDto`, `403`, `409`, `413`, `422`. Va **antes** de `/{agentId:guid}` o no choca (la ruta `import` no es guid); cualquiera de los dos órdenes sirve.

- [ ] **Step 1: Write the failing tests**
  - Unit `ImportAgentValidatorTests` con un `AgentExportDto(1, "aiAgent", "Ventas", AgentConfig.Default(…), DateTimeOffset.UnixEpoch)` válido:
    - `AValidFileIsAccepted` → `IsValid`.
    - `AChatbotFileIsRejected`: `Kind = ""` → error en `Export.Kind`.
    - `ANewerSchemaIsRejected`: `Schema = 2` → error en `Export.Schema`.
    - `AMissingConfigIsRejected`: `Config = null!` → error en `Export.Config`.
    - `AnEmptyNameIsRejected`: `Name = ""` → error en `Name`.
  - Integración `AgentsApiTests.AnImportedAgentIsBornAsADraftWithoutTheTenantsIds`: exporta como en la Tarea 2, y al cuerpo exportado le **vuelve a poner** `memberId` y `quotationDraft` (simula un archivo editado a mano); `POST {Agents}/import` con `{ name = "Ventas importado", export }` → 201; `status == "Draft"`, `connectionIds` vacío, `draft.config.instructions` igual, `handoff.memberId` null, `quotationDraft` null; `issues` contiene los códigos `handoff_member_missing`, `quotation_advisor_missing` y `quotation_billing_account_missing`.
  - Integración `AgentsApiTests.ImportingATakenNameOrAChatbotFileIsRejected`: importar con el nombre del agente original → 409 `ai.agent.name_taken`; un `export` sin `kind` → 422; con sólo `AgentRead` → 403.
- [ ] **Step 2: Run to verify RED** — `dotnet build tests/Modules/Ai/Modules.Ai.UnitTests` → `CS0246 'ImportAgentCommand' could not be found`.
- [ ] **Step 3: Implement** comando, validador, handler, `AgentLimits.ImportBodyMaxBytes`, request y endpoint.
- [ ] **Step 4: Run to verify GREEN** — unit `--filter "FullyQualifiedName~ImportAgentValidatorTests"`; integración `--filter "FullyQualifiedName~AnImportedAgentIsBornAsADraftWithoutTheTenantsIds|FullyQualifiedName~ImportingATakenNameOrAChatbotFileIsRejected"`. Luego, una vez, la clase entera: `--filter "FullyQualifiedName~AgentsApiTests"` y `dotnet test tests/ArchitectureTests`.
- [ ] **Step 5: Commit** — `feat(ai): import an agent from an export file`

### Cierre (al mergear, en `develop`, no en la rama)

- `docs/estado.md`: fila de Agentes IA y entrada del Registro con «Importar y exportar agentes IA» y el merge.
- Suite del módulo sólo antes de mergear y si el owner lo pide (memoria «Integración completa del backend sólo si la pido»): `Modules.Ai.UnitTests` y `Modules.Ai.IntegrationTests`.

# Causa del traspaso a un asesor — plan de implementación (backend y frontend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el hilo de la bandeja diga **por qué** el chatbot pasó la conversación a un asesor (bloque alcanzado, respuesta inválida, envío rechazado, falla del flujo, y el HTTP fallido que lo provocó), con códigos que el frontend traduce, sin que nada de eso llegue al cliente; y que `IIntegrationConnections.ResolveAsync` deje de colapsar las causas en `null`.

**Architecture:** Integrations devuelve `ConnectionResolution` con el motivo. Bootstrapper lo traduce a códigos `snake_case` para el bloque HTTP (`ExternalApiResolution` → `HttpCallResult.ErrorCode`) y para el envío del bot (`MessagingSenderResolution` → `BotSendResult.ConnectionUnavailable(reason)` → `BotSendOutcome.Rejected`). `RunEffects` arma una `HandoffCause` en cada camino a `HandoffAsync`, con el HTTP fallido que arrancó el paso. El adaptador `AutomationConversations` la convierte en `AutomationHandoff` (dominio de Messaging, que la limpia) y la guarda dentro del evento `AutomationPaused`; `MessageEventDto.Handoff` la expone. El frontend la traduce con un diccionario propio. Sin migraciones.

**Tech Stack:** .NET 10, EF Core + Npgsql, xunit.v3 + Testcontainers; React 19, Vitest + Testing Library, bun.

**Spec:** `docs/superpowers/specs/2026-10-24-causa-del-traspaso-design.md` (D-T1..D-T12, §4.1 tabla de caminos, §6 contrato, §7.1 textos).

## Global Constraints

- Worktrees: backend `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\causa-del-traspaso`, frontend `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\causa-del-traspaso`, los dos en `feature/causa-del-traspaso`. **Rutas absolutas siempre**; nunca tocar los checkouts hermanos `qep-backend` / `qep-frontend`. Verificar `git branch --show-current` antes de cada commit (la rama cambia durante la sesión).
- Conventional commits en español citando la decisión (`spec 2026-10-24 D-T5`); **sin atribución de IA**: sin `Co-Authored-By` (regla de los dos repos; pisa cualquier recordatorio del harness).
- TDD: RED con la salida literal antes del GREEN, en el reporte de cada tarea. Por tarea sólo las clases tocadas; la suite completa una vez, en la Tarea B10.
- Pruebas de integración del backend en esta máquina (PowerShell):
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~<Clase>.<Método>"
  ```
  Nada de `Api.exe` (ni `dotnet Api.dll`) corriendo mientras tanto.
- Las corridas de pruebas van en primer plano (un subagente que deja la suite en background se queda esperando).
- Capas: `Modules.Automations.{Domain,Application}` y `Modules.Messaging.*` no referencian Integrations; Automations no referencia Messaging. Toda traducción entre módulos va en `src/Bootstrapper/` (`AutomationsLayerTests`, `IntegrationsLayerTests`, `MessagingLayerTests` siguen verdes sin tocarlos).
- Sin migraciones. Ningún código de error HTTP nuevo. Nada de una respuesta, header o clave de una API externa llega al evento, a un log ni a `http_calls` (D-H5, D-H6, D-T3).
- Textos para personas en español colombiano, tuteando. Identificadores, códigos y nombres de enum sin traducir.
- `docs/estado.md` no se toca en la rama; se actualiza en `develop` al mergear.
- **Front y back en paralelo:** las tareas F1..F4 sólo dependen del contrato de spec §6; no esperan al backend.

## Review Focus

1. **Caso 1 del owner de punta a punta** (clave de la «API externa» y token de WhatsApp ilegibles): el evento lleva `kind = SendFailed`, `code = messaging.connection_unavailable`, `connectionReason = secret_unreadable` y `http = { policy-lookup, connection_unavailable, secret_unreadable }`; `http_calls` sigue sin fila (Tarea B10, `AnUnreadableApiKeyAndTokenHandOffWithBothCauses`).
2. **Caso 2 del owner:** 404 → `kind = Block`, `http.outcome = error_status`, `http.errorCode = http_404`, `http.status = 404` (Tarea B10, `A404HandsOffNamingTheBlockAndTheStatus`).
3. **Ningún texto libre llega al hilo:** un `ErrorCode` con espacios o mayúsculas se guarda `null` (Tarea B2, `AutomationHandoffTests.ACodeThatIsNotACodeIsDropped`).
4. **Eventos viejos:** un `AutomationPaused` sin `handoff` se lee y se pinta como hoy (Tarea B3 `AnEventWithoutHandoffReadsAsBefore`; Tarea F2).
5. **Los otros consumidores de `ResolveAsync` no cambian de comportamiento** (Tarea B1: Shopify, Quotations y el directorio de Messaging pasan sus pruebas sin cambiar asserts).
6. **`FailAsync` no le escribe al cliente** (D-T2): `RunProcessorTests.AFailureHandsOffWithoutSendingAnything` (Tarea B7).

---

## BACKEND

### Task B1: `ConnectionResolution` en Integrations (D-T4)

**Files:**
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/IIntegrationConnections.cs` (tipos nuevos junto a `ResolvedConnection`, firma en `:37`, implementación `:54-77`)
- Modify (mecánico, `.Connection`): `src/Bootstrapper/AutomationExternalApis.cs:24`, `src/Bootstrapper/MessagingConnectionDirectory.cs:30,44,70,90,99`, `src/Bootstrapper/QuotationsWhatsAppChannelDirectory.cs:20`, `src/Bootstrapper/ShopifyConnectionDirectory.cs:107`
- Modify (dobles): `tests/Bootstrapper/Bootstrapper.UnitTests/AutomationAdaptersTests.cs:415`, `tests/Bootstrapper/Bootstrapper.UnitTests/QuotationsWhatsAppChannelDirectoryTests.cs:102`, `tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyAdaptersTestDoubles.cs:340`
- Modify (llamadas directas): `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ConsumerPortsApiTests.cs`, `InstagramConnectApiTests.cs:305`, `ProviderEnablementApiTests.cs:218`
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/ConsumerPortsTests.cs`

**Interfaces:**
- Produces: `enum ConnectionUnavailableReason { NotFound, Paused, NeedsAttention, ProviderHidden, ProviderDisabled, SecretUnreadable }`; `sealed record ConnectionResolution(ResolvedConnection? Connection, ConnectionUnavailableReason? Unavailable, string? Name)` con `Resolved(...)` y `Unusable(reason, name)`; `Task<ConnectionResolution> IIntegrationConnections.ResolveAsync(...)`.

- [ ] **Step 1: RED.** En `ConsumerPortsTests`, reemplazar `ResolveIsNullForPausedNeedsAttentionMissingOtherTenantHiddenOrUnreadable` (`:33-54`) por:

```csharp
[Fact]
public async Task ResolveExplainsWhyAConnectionCannotBeUsed()
{
    var bed = new IntegrationsTestBed();
    var paused = bed.Seed("Pausada");
    paused.Pause(IntegrationsTestBed.Now);
    var attention = bed.Seed("Atención");
    attention.MarkNeedsAttention(ConnectionFailureCodes.CredentialsRejected, IntegrationsTestBed.Now);
    var foreign = bed.Seed("Ajena", tenantId: Guid.CreateVersion7());
    var unreadable = bed.Seed("Ilegible");
    bed.Protector.Unreadable.Add((unreadable.Id, ZenviaFieldKeys.ApiToken));
    var port = bed.ConnectionsPort();

    async Task<(ConnectionUnavailableReason?, string?, bool)> Of(Guid id)
    {
        var r = await port.ResolveAsync(bed.TenantId, id, Ct);
        return (r.Unavailable, r.Name, r.Connection is null);
    }

    Assert.Equal((ConnectionUnavailableReason.Paused, "Pausada", true), await Of(paused.Id));
    Assert.Equal((ConnectionUnavailableReason.NeedsAttention, "Atención", true), await Of(attention.Id));
    Assert.Equal((ConnectionUnavailableReason.NotFound, (string?)null, true), await Of(Guid.CreateVersion7()));
    Assert.Equal((ConnectionUnavailableReason.NotFound, (string?)null, true), await Of(foreign.Id));
    Assert.Equal((ConnectionUnavailableReason.SecretUnreadable, "Ilegible", true), await Of(unreadable.Id));

    var hidden = bed.Seed("Oculta");
    bed.HideQuotations();
    Assert.Equal((ConnectionUnavailableReason.ProviderHidden, "Oculta", true), await Of(hidden.Id));
}
```

Y `ResolveReturnsAnActiveVisibleConnectionWithItsSecretsInClear` (`:17-30`) lee `resolution.Connection` y afirma `Unavailable == null` y `Name == connection.Name`. Agregar `ResolveSaysProviderDisabledWhenTheTenantTurnedItOff` usando el mismo mecanismo que `ListActive…` usa para `ITenantProviders` en `IntegrationsTestBed` (si el test bed no expone el apagado, agregar el helper ahí, no un doble nuevo). Correr y pegar el RED (no compila: `ConnectionUnavailableReason` no existe).

```powershell
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~ConsumerPortsTests"
```

- [ ] **Step 2: GREEN.** Tipos e implementación como spec §3.1: mismas comprobaciones y mismo orden que hoy; `Paused`/`NeedsAttention` según `connection.Status`; catálogo `null` o no visible → `ProviderHidden`; no habilitado → `ProviderDisabled`; `stored.Unreadable.Count > 0` → `SecretUnreadable`. Actualizar los siete llamadores de Bootstrapper a `.Connection` (sin otro cambio; `AutomationExternalApis` también por ahora: la Tarea B4 lo cambia), los tres dobles y las tres pruebas de integración.
- [ ] **Step 3:** Correr `ConsumerPortsTests`, `Bootstrapper.UnitTests` completo (rápido) y `ConsumerPortsApiTests`, `ProviderEnablementApiTests`, `InstagramConnectApiTests` (integración). Todo verde sin cambiar asserts de comportamiento.
- [ ] **Step 4: Commit** `refactor(integrations): ResolveAsync explica por qué una conexión no se puede usar (spec 2026-10-24 D-T4)`.

### Task B2: `AutomationHandoff` en el dominio de Messaging (D-T7, D-T12)

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/ConversationAutomation.cs` (tipos nuevos tras `AutomationPauseReason`, `:13-20`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/ConversationEvent.cs:28-36` (`AutomationHandoff? Handoff = null` al final)
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/Conversation.cs:369-383` (`PauseAutomation` con `AutomationHandoff? handoff = null`)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/AutomationHandoffTests.cs` (nuevo), `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationAutomationTests.cs`

**Interfaces:**
- Produces: `enum AutomationHandoffKind { Block, InvalidReply, SendFailed, FlowFailed }`; `sealed record AutomationHandoffHttp(string? NodeId, string? NodeTitle, string? Outcome, string? ErrorCode, int? Status, string? ConnectionName)`; `sealed record AutomationHandoff(AutomationHandoffKind Kind, string? Code, string? NodeId, string? NodeTitle, string? ConnectionName, string? ConnectionReason, AutomationHandoffHttp? Http)` con `TextMaxLength = 120`, `CodeMaxLength = 64` y `static AutomationHandoff Create(AutomationHandoffKind kind, string? code, string? nodeId, string? nodeTitle, string? connectionName, string? connectionReason, AutomationHandoffHttp? http)`.

- [ ] **Step 1: RED.** `AutomationHandoffTests`:
  - `TitlesAndNamesAreFittedTo120WithAnEllipsis` — un título de 300 caracteres sale de 120 terminado en «…» (misma regla que `SnapshotText.Fit`); uno en blanco sale `null`; se recorta el espacio de los bordes.
  - `ACodeThatIsNotACodeIsDropped` — `"messaging.connection_unavailable"`, `"http_404"`, `"secret_unreadable"` pasan; `"Ya existe una orden"`, `"HTTP_404"`, `"a b"`, un código de 65 caracteres salen `null` (en `Code`, `ConnectionReason`, `Http.Outcome`, `Http.ErrorCode`).
  - `ANodeIdLongerThan64IsDropped`.
  - `AStatusOutsideHttpRangeIsDropped` — 99 y 600 → `null`; 404 queda.
  - `AnHttpWithoutNodeAndOutcomeIsDropped` — `Http` con todo `null` (o que queda todo `null` tras limpiar) → `Http = null`.
  En `ConversationAutomationTests`:
  - `AHandoffPauseCarriesItsCauseInTheEvent` — `PauseAutomation(null, Handoff, now, handoff)` devuelve el evento con `Handoff` igual al pasado.
  - `ACauseWithAnotherReasonThrows` — `PauseAutomation(member, Manual, now, handoff)` → `ArgumentException`; el estado no cambió.
  - `APauseWithoutCauseStaysAsToday` — sin `handoff`, `Handoff == null`.
  ```powershell
  dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~AutomationHandoffTests|FullyQualifiedName~ConversationAutomationTests"
  ```
- [ ] **Step 2: GREEN.** `Create` usa `BuildingBlocks.Domain.SnapshotText.Fit` (Messaging.Domain ya referencia BuildingBlocks.Domain) y un `[GeneratedRegex("^[a-z0-9_.]{1,64}\\z")]`. La guarda de `PauseAutomation` va **antes** de `EnsureOpenForAutomation`, para no tocar estado.
- [ ] **Step 3:** Correr `Modules.Messaging.UnitTests` completo: `ConversationTests` y `ConversationEventJsonTests` siguen verdes (la igualdad de records con un `Handoff = null` no cambia).
- [ ] **Step 4: Commit** `feat(messaging): el evento AutomationPaused puede llevar la causa del traspaso (spec 2026-10-24 D-T7, D-T12)`.

### Task B3: JSON del evento y `MessageEventDto.Handoff` (spec §5.2)

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/ConversationEventJson.cs` (`Serialize` `:11-34`, `Parse` `:37-69`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingDtos.cs:97` (+ `AutomationHandoffDto`, `AutomationHandoffHttpDto`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingSupport.cs:190-200` (`MessageMapping.ToDto`)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationEventJsonTests.cs`, `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationAutomationMappingTests.cs`

**Interfaces:**
- Consumes: B2.
- Produces: claves JSON `handoff.{kind,code,nodeId,nodeTitle,connectionName,connectionReason,http.{nodeId,nodeTitle,outcome,errorCode,status,connectionName}}`; `AutomationHandoffDto(string Kind, string? Code, string? NodeId, string? NodeTitle, string? ConnectionName, string? ConnectionReason, AutomationHandoffHttpDto? Http)`; `AutomationHandoffHttpDto(string? NodeId, string? NodeTitle, string? Outcome, string? ErrorCode, int? Status, string? ConnectionName)`; `MessageEventDto(..., string? PauseReason = null, AutomationHandoffDto? Handoff = null)`.

- [ ] **Step 1: RED.** `ConversationEventJsonTests`:
  - `AHandoffRoundTrips` — evento con la causa completa del caso 1 (spec §5.2) → `Parse(Serialize(x)) == x`.
  - `TheHandoffIsWrittenWithItsNames` — el JSON de `Serialize` contiene `"handoff":{"kind":"SendFailed","code":"messaging.connection_unavailable",…}` y **no** contiene claves con `null` (`"status":null` no aparece).
  - `AnEventWithoutHandoffReadsAsBefore` — el JSON viejo `{"type":"AutomationPaused","pauseReason":"Handoff"}` → `Handoff == null`, `PauseReason == Handoff`.
  - `AnUnknownKindOrAMalformedHandoffReadsAsNull` — `"kind":"Future"`, `"handoff":"x"`, `"handoff":{"kind":"Block","status":"404"}` (status como texto → ese campo `null`, el resto se lee) no lanzan.
  `ConversationAutomationMappingTests`:
  - `AHandoffEventCarriesItsCauseInTheDto` — el `MessageDto.Event.Handoff` trae `Kind = "Block"` por nombre y el `Http` con `Status = 404`.
- [ ] **Step 2: GREEN.** `Parse` con un lector propio por campo (string → si no es string, `null`; `status` → `TryGetInt32`), y construye con `AutomationHandoff.Create` para que lo leído pase por la misma limpieza que lo escrito.
- [ ] **Step 3:** Correr las dos clases. **Commit** `feat(messaging): la causa del traspaso en el details del evento y en MessageEventDto (spec 2026-10-24 §5.2)`.

### Task B4: El motivo de conexión en el bloque HTTP (D-T5, D-T10)

**Files:**
- Create: `src/Bootstrapper/ConnectionReasonCodes.cs`
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs:118-129` (`ExternalApiResolution`, firma de `ResolveAsync`)
- Modify: `src/Bootstrapper/AutomationExternalApis.cs:20-35`
- Modify: `src/Modules/Automations/Modules.Automations.Application/Http/IHttpBlockExecutor.cs:15` (`HttpCallResult` + `string? ConnectionName = null`) y su `<summary>` de `HttpCallOutcomes` (`:40-41`: `ConnectionUnavailable` lleva el motivo en `ErrorCode`)
- Modify: `src/Modules/Automations/Modules.Automations.Infrastructure/Http/HttpBlockExecutor.cs:63-64, 85-93` (y `ConnectionName` en el resto de los resultados vía `FinishAsync`/`Call`)
- Modify (dobles): `tests/Modules/Automations/Modules.Automations.UnitTests/HttpBlockExecutorTests.cs:713` (`StubExternalApis`), `tests/Modules/Automations/Modules.Automations.IntegrationTests/AutomationsApiHarness.cs:155-169` (`FakeExternalApis.ResolveAsync` → `new ExternalApiResolution(null, "not_found", null)`)
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/AutomationAdaptersTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/ConnectionReasonCodesTests.cs` (nuevo), `tests/Modules/Automations/Modules.Automations.UnitTests/HttpBlockExecutorTests.cs`

**Interfaces:**
- Consumes: B1.
- Produces: `static class ConnectionReasonCodes { string Of(ConnectionUnavailableReason reason); const string WrongProvider = "wrong_provider"; const string Incomplete = "incomplete"; }` (internal, Bootstrapper); `sealed record ExternalApiResolution(ExternalApiCredentials? Credentials, string? UnavailableReason, string? ConnectionName)`; `Task<ExternalApiResolution> IAutomationExternalApis.ResolveAsync(...)`; `HttpCallResult(..., string? ConnectionName = null)`; en el ejecutor `ErrorCode ∈ { not_chosen, auth_header_reserved, <motivo del adaptador> }` cuando `Outcome = connection_unavailable`.

- [ ] **Step 1: RED.**
  - `ConnectionReasonCodesTests.EveryReasonHasItsCode` — `[Theory]` con los seis valores → `not_found`, `paused`, `needs_attention`, `provider_hidden`, `provider_disabled`, `secret_unreadable`; y `EveryEnumValueIsMapped` recorriendo `Enum.GetValues<ConnectionUnavailableReason>()` (un valor nuevo sin código rompe la prueba, no el hilo).
  - `AutomationAdaptersTests`: `ResolveCarriesTheReasonAndTheNameOfAnUnusableConnection` (stub con `Unusable(SecretUnreadable, "API de Clasic")` → `(null, "secret_unreadable", "API de Clasic")`), `ResolveOfAnotherProviderIsWrongProvider`, `ResolveWithoutBaseUrlOrKeyIsIncomplete`, y las de hoy (`:74`, `:91`, `:103-104`) leen `.Credentials`.
  - `HttpBlockExecutorTests`: `AMissingConnectionIsConnectionUnavailableWithoutALog` (`:345-354`) pasa a afirmar `ErrorCode = "not_chosen"` por nodo y `"not_found"` por resolución; `AReservedAuthHeaderIsConnectionUnavailableWithoutACallOrALog` (`:211-217`) afirma `ErrorCode = "auth_header_reserved"`; nueva `AnUnreadableKeyCarriesItsReasonAndTheConnectionNameWithoutARow` (stub con `("secret_unreadable", "API de Clasic")` → `ErrorCode = "secret_unreadable"`, `ConnectionName = "API de Clasic"`, `IHttpCallLog` sin filas, 0 requests); nueva `ASuccessfulCallCarriesTheConnectionName`. `StubExternalApis` recibe `ExternalApiResolution`.
  ```powershell
  dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~ConnectionReasonCodesTests|FullyQualifiedName~AutomationAdaptersTests"
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~HttpBlockExecutorTests"
  ```
- [ ] **Step 2: GREEN.** Spec §3.2. `LogUnavailable` suma `{ErrorCode}` al mensaje (sigue sin URL ni clave). `ConnectionName` entra a `Call` para que `FinishAsync` no lo pierda.
- [ ] **Step 3:** Correr también `HttpBlockCallLogTests` y `SimulationsApiTests` (integración): verdes sin cambios. **Commit** `feat(automations): el bloque HTTP dice por qué no pudo usar su conexión (spec 2026-10-24 D-T5, D-T10)`.

### Task B5: El motivo de la conexión de WhatsApp en el envío del bot (D-T11)

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingConnectionDirectory.cs:44-45` (método y record nuevos)
- Modify: `src/Bootstrapper/MessagingConnectionDirectory.cs:28-40` (`ResolveSenderWithReasonAsync`; `ResolveSenderAsync` delega)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/BotOutbound.cs:19, 109-114, 127-130`
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs:254` (`BotSendOutcome.Rejected(string Code, string? ConnectionReason = null, string? ConnectionName = null)`)
- Modify: `src/Bootstrapper/AutomationMessenger.cs:55`
- Modify (dobles): `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingTestBed.cs:343`, `tests/Modules/Messaging/Modules.Messaging.UnitTests/WebhookRoutingTests.cs:43`, `tests/Bootstrapper/Bootstrapper.UnitTests/AutomationAdaptersTests.cs:390`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/BotOutboundTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/AutomationAdaptersTests.cs`

**Interfaces:**
- Consumes: B1, B4 (`ConnectionReasonCodes`).
- Produces: `sealed record MessagingSenderResolution(MessagingSender? Sender, string? UnavailableReason, string? ConnectionName)`; `Task<MessagingSenderResolution> IMessagingConnectionDirectory.ResolveSenderWithReasonAsync(Guid, Guid, CancellationToken)`; `BotSendResult.ConnectionUnavailable(string? Reason = null, string? ConnectionName = null)`; `BotSendOutcome.Rejected(Code, ConnectionReason?, ConnectionName?)`.

- [ ] **Step 1: RED.**
  - `BotOutboundTests`: `AnUnreadableTokenIsConnectionUnavailableWithItsReason` — sembrar la conexión de WhatsApp, invertir un byte del `ciphertext` (`UPDATE integrations.connection_secrets SET ciphertext = set_byte(ciphertext, 20, get_byte(ciphertext, 20) # 255) WHERE connection_id = @id`, precedente `ConnectionSecretRekeyWorkerTests.cs:75`) → `ConnectionUnavailable { Reason = "secret_unreadable", ConnectionName = <nombre> }` y ninguna fila en `messaging.messages`. `APausedConnectionIsConnectionUnavailablePaused`. `ATokenRejectedByMetaIsCredentialsRejected` — Graph responde 401/190 (como `SendMessageApiTests.cs:153`) → `Reason = "credentials_rejected"`.
  - `AutomationAdaptersTests`: el `[Theory]` de `Map` (`:221`) agrega `ConnectionUnavailable("secret_unreadable", "Servicio Clasic")` → `Rejected("messaging.connection_unavailable", "secret_unreadable", "Servicio Clasic")`.
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~BotOutboundTests"
  dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~AutomationAdaptersTests"
  ```
- [ ] **Step 2: GREEN.** Spec §3.3. En `BotOutbound`, guardar la resolución en una variable para usar su `ConnectionName` también en el `catch` de `credentials_rejected`.
- [ ] **Step 3:** Correr `Modules.Messaging.UnitTests` completo y `SendMessageApiTests` (los envíos de asesor no cambian). **Commit** `feat(messaging): el envío del bot dice por qué la conexión no estaba disponible (spec 2026-10-24 D-T11)`.

### Task B6: Motor — motivo del `Handoff` y el HTTP en la entrada (D-T9, D-T6)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Engine/Effect.cs:22` (+ `HandoffReasons`)
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Engine/FlowEngine.cs:206` (`InvalidReply`), `:324` (`Block`, explícito)
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Engine/EngineInput.cs:20-21` (`string? Outcome = null, string? ConnectionName = null`; el `<summary>` dice que el motor no los lee)
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/FlowEngineWaitingTests.cs:434`, `tests/Modules/Automations/Modules.Automations.UnitTests/FlowEngineTests.cs`

**Interfaces:**
- Produces: `Effect.Handoff(string NodeId, string Reason = HandoffReasons.Block)`; `static class HandoffReasons { const string Block = "block"; const string InvalidReply = "invalid_reply"; }`; `EngineInput.HttpResult(string NodeId, bool Ok, int? Status, RunVariables Mapped, string? ErrorCode, string? Outcome = null, string? ConnectionName = null)`.

- [ ] **Step 1: RED.** `FlowEngineWaitingTests` (`:434`): el `fallback = handoff` espera `new Effect.Handoff("l", HandoffReasons.InvalidReply)` — falla hoy porque sale con el motivo por defecto. `FlowEngineTests`: `AHandoffBlockSaysBlock` (afirma `Reason == HandoffReasons.Block`), `TheEngineIgnoresTheOutcomeAndConnectionNameOfAnHttpResult` (mismo flujo de `:175`, dos entradas que sólo difieren en `Outcome`/`ConnectionName` → mismos efectos y estado).
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~FlowEngine"
  ```
- [ ] **Step 2: GREEN.** Los asserts de `FlowEngineTests.cs:245,250` siguen pasando (motivo por defecto `Block`).
- [ ] **Step 3: Commit** `feat(automations): el motor distingue el traspaso por bloque del de respuesta inválida (spec 2026-10-24 D-T9)`.

### Task B7: `HandoffCause` en `RunEffects` y `RunProcessor` (D-T6, D-T2)

**Files:**
- Create: `src/Modules/Automations/Modules.Automations.Application/Engine/HandoffCause.cs`
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs:232-234` (`HandoffAsync(Guid tenantId, Guid conversationId, HandoffCause cause, CancellationToken)`)
- Modify: `src/Modules/Automations/Modules.Automations.Application/Engine/RunEffects.cs` (`RunAsync` `:35-123`, `FailAsync` `:146-156`, `StopAsync` `:177-197`, `CallHttpAsync` `:233-237`)
- Modify: `src/Modules/Automations/Modules.Automations.Application/Engine/RunProcessor.cs:351, 381`
- Modify (sólo firma; la Tarea B8 la usa): `src/Bootstrapper/AutomationConversations.cs:78` acepta `HandoffCause cause` y por ahora no la pasa.
- Modify (dobles): `tests/Modules/Automations/Modules.Automations.UnitTests/RunProcessorTests.cs:606-650` (`Conversations` guarda `List<HandoffCause> Causes`), `tests/Modules/Automations/Modules.Automations.IntegrationTests/RunsPersistenceTests.cs:332` (`FailingEndConversations`), las llamadas de `AutomationConversationsAdapterTests.cs:112,138,152,170` (pasan una causa `Block`).
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/RunProcessorTests.cs`

**Interfaces:**
- Consumes: B4 (`HttpCallResult.ConnectionName`), B5 (`BotSendOutcome.Rejected` con motivo), B6.
- Produces: `enum HandoffCauseKind { Block, InvalidReply, SendFailed, FlowFailed }`; `sealed record HandoffHttpFailure(string NodeId, string? NodeTitle, string Outcome, string? ErrorCode, int? Status, string? ConnectionName)`; `sealed record HandoffCause(HandoffCauseKind Kind, string? Code, string? NodeId, string? NodeTitle, string? ConnectionName, string? ConnectionReason, HandoffHttpFailure? Http)`; `RunEffects.FailAsync(ConversationRun run, RunState state, HandoffCause cause, DateTimeOffset now, CancellationToken ct)` (cierra con `cause.Code`).

- [ ] **Step 1: RED.** En `RunProcessorTests`, una prueba por fila de spec §4.1, cada una afirmando la causa exacta que recibió el doble (`Assert.Equal(new HandoffCause(...), bed.Conversations.Causes.Single())`) y el `failure_code` de la corrida cuando aplica:
  1. `AHandoffBlockReachedOnPurposeSaysBlock` — `Block`, `NodeId`/`NodeTitle` del bloque, `Http = null`.
  2. `AHandoffBlockAfterAFailedHttpNamesTheHttpBlock` — `RecordingHttpBlocks` (`:540`) devuelve `HttpCallResult(false, 404, Empty, "error_status", "http_404", "API de Clasic")` → `Block` con `Http = ("policy-lookup", "Buscar cliente por teléfono", "error_status", "http_404", 404, "API de Clasic")`.
  3. `AnInvalidReplyWithHandoffFallbackSaysInvalidReply` — `InvalidReply`, nodo = el de Botones.
  4. `ARejectedSendSaysSendFailedWithTheConnectionReason` — `RecordingMessenger` (`:653`) devuelve `Rejected("messaging.connection_unavailable", "secret_unreadable", "Servicio Clasic")` al texto del bloque de traspaso → `SendFailed`, `Code` y `failure_code = messaging.connection_unavailable`, `NodeId = handoff`, conexión y motivo; con el HTTP fallido previo (`connection_unavailable`/`secret_unreadable`) en `Http`. **Es el caso 1 del owner.**
  5. `TheReentryCapSaysFlowFailedLoopDetected` — `FlowFailed`, `automations.run.loop_detected`.
  6. `AnEndRunTargetMissingSaysFlowFailed` — `FlowFailed`, `automations.run.target_missing`, `NodeId = end.NodeId`.
  7. `AMissingVersionSaysFlowFailedWithoutNode` (camino de `RunProcessor.cs:351`) — `FlowFailed`, `automations.run.version_missing`, `NodeId = null`, `Http = null`.
  8. `ARunningRunWithoutPendingEffectSaysTargetMissing` (`RunProcessor.cs:381`) — `NodeId = run.CurrentNodeId`.
  9. `AFailureHandsOffWithoutSendingAnything` (D-T2) — en el caso 4, `RecordingMessenger` registró un solo intento (el texto que falló) y ningún envío después del traspaso.
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~RunProcessorTests"
  ```
- [ ] **Step 2: GREEN.**
  - `RunAsync`: `var trigger = next;` antes de `FlowEngine.Step`; `HandoffHttpFailure? http = trigger is EngineInput.HttpResult { Ok: false } h ? new(h.NodeId, TitleOf(unit.Flow, h.NodeId), h.Outcome ?? FlowOutputs.Error, h.ErrorCode, h.Status, h.ConnectionName) : null;`. `TitleOf` = primer nodo con ese id, `null` si no está.
  - `case Effect.Handoff handoff:` arma `Block` o `InvalidReply` según `handoff.Reason`.
  - `StopAsync` recibe el `Effect` que falló y el `http`; con `Rejected r` llama `FailAsync` con `SendFailed(r.Code, effect.NodeId, título, r.ConnectionName, r.ConnectionReason, http)`.
  - Reentradas (`:75-79`) y `EndRun` (`:112-115`) → `FlowFailed` con `AutomationsRunFailures.Of(reason)`.
  - `CallHttpAsync` pasa `result.Outcome` y `result.ConnectionName` a `EngineInput.HttpResult`.
  - `RunProcessor` arma las causas 7 y 8 (para la 8, el título sale del `flow` que ya cargó).
- [ ] **Step 3:** Correr `Modules.Automations.UnitTests` completo y `RunsPersistenceTests`, `EngineEndToEndTests` (integración): verdes. `ArchitectureTests` verde.
  ```powershell
  dotnet test tests/ArchitectureTests
  ```
- [ ] **Step 4: Commit** `feat(automations): cada traspaso del motor lleva su causa (spec 2026-10-24 D-T6)`.

### Task B8: El adaptador escribe la causa en el evento (spec §5.3)

**Files:**
- Modify: `src/Bootstrapper/AutomationConversations.cs:78-97`
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/HandoffCauseKindParityTests.cs` (nuevo), `tests/Modules/Automations/Modules.Automations.IntegrationTests/AutomationConversationsAdapterTests.cs`

**Interfaces:**
- Consumes: B2, B3, B7.
- Produces: `AutomationConversations.HandoffAsync` llama `conversation.PauseAutomation(null, AutomationPauseReason.Handoff, now, AutomationHandoff.Create(...))`.

- [ ] **Step 1: RED.**
  - `HandoffCauseKindParityTests.EveryCauseKindExistsInMessaging` — cada nombre de `Enum.GetNames<HandoffCauseKind>()` está en `Enum.GetNames<AutomationHandoffKind>()` y al revés (precedente `ShopifyWebhookTopicsParityTests`).
  - `AutomationConversationsAdapterTests.HandoffWritesTheCauseInTheEvent` — como `HandoffPausesWithASystemActorAndAnEvent` (`:100-122`) pero con la causa del caso 1; afirma por SQL `details->'handoff'->>'kind' = 'SendFailed'`, `details->'handoff'->'http'->>'errorCode' = 'secret_unreadable'`, y que `HandoffPausesWithASystemActorAndAnEvent` sigue igual (actor ausente, auditoría de sistema, avisos).
  - `AnUntidyCauseIsCleanedBeforeItIsStored` — un `Http.ErrorCode = "Ya existe una orden abierta"` queda sin la clave `errorCode` en el `details`.
  ```powershell
  dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~HandoffCauseKindParityTests"
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~AutomationConversationsAdapterTests"
  ```
- [ ] **Step 2: GREEN.** `Enum.Parse<AutomationHandoffKind>(cause.Kind.ToString())`; el resto campo a campo.
- [ ] **Step 3: Commit** `feat(bootstrapper): el traspaso del motor guarda su causa en el hilo (spec 2026-10-24 §5.3)`.

### Task B9: `failureCode` en «Datos capturados» (D-T8)

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IConversationAutomationDetails.cs:9-18` (`string? FailureCode = null` al final)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/GetConversationAutomation.cs:10-19, 40-41`
- Modify: `src/Bootstrapper/AutomationConversationDetails.cs:38-47`
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/ClasicFlowEndToEndTests.cs` (ya lee `ConversationAutomationDetailsDto`)

**Interfaces:**
- Produces: `ConversationAutomationDetailsDto(..., DateTimeOffset? EndedAt, string? FailureCode)` → JSON `failureCode`.

- [ ] **Step 1: RED.** En `ClasicFlowEndToEndTests.AnHttpLoopIsCutAtTheReentryCapAndHandsOff` (`:403-423`), pedir `GET …/messaging/conversations/{id}/automation` con un asesor (como ya lo hace ese archivo en `ClasicFlowEndToEndTests.cs:704-705`) y afirmar `failureCode = "automations.run.loop_detected"`; en `AnApiErrorHandsOff` (`:364-387`), `failureCode = null` (`HandedOff`).
- [ ] **Step 2: GREEN.** Pasar `run.FailureCode`.
- [ ] **Step 3: Commit** `feat(messaging): Datos capturados muestra el failure_code de la corrida (spec 2026-10-24 D-T8)`.

### Task B10: Los dos casos del owner de punta a punta, suite completa y CLAUDE.md

**Files:**
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/ClasicFlowEndToEndTests.cs`
- Modify: `CLAUDE.md` (una entrada en «Gotchas verificados»)

- [ ] **Step 1: RED → GREEN** (las piezas ya existen; si alguna falla, es un hueco de B1..B9 y se arregla en su tarea).
  - `AnUnreadableApiKeyAndTokenHandOffWithBothCauses` — tras `ArrangeAsync`, invertir un byte del `ciphertext` de **las dos** conexiones (la «API externa» `apiConnectionId` y la de WhatsApp); `SayAsync(f, "w1", "Hola")`; drenar. Afirmar: `f.Graph.Sends()` vacío (el cliente no recibió nada: el texto del traspaso no salió), `automations.http_calls` sin filas, corrida `Failed|messaging.connection_unavailable`, conversación `Open|Paused`, y el `details` del evento: `kind = SendFailed`, `nodeId = handoff`, `connectionReason = secret_unreadable`, `http.nodeId = policy-lookup`, `http.outcome = connection_unavailable`, `http.errorCode = secret_unreadable`, `http.connectionName = "API de Clasic"`.
  - `A404HandsOffNamingTheBlockAndTheStatus` — `f.Api.Respond(@"^/customers\?phone=", HttpStatusCode.NotFound, "{}")`; afirmar `http_calls` `GET /customers 404 error_status http_404`, corrida `HandedOff`, y el evento: `kind = Block`, `nodeId = handoff`, `nodeTitle = "Pasar a un asesor"` (`Fixtures/clasic.json:1045-1048`), `http = { policy-lookup, "Buscar cliente por teléfono", error_status, http_404, 404, "API de Clasic" }`. Y, por la API de mensajes del hilo, `event.handoff.http.status = 404` (contrato §6). `f.Graph.Sends()` sigue siendo `["Te paso con un asesor."]`: el bloque manda su texto (D-T2; lo que cambie P-1 se prueba aparte).
  - `AnApiErrorHandsOff` (`:364-387`) suma: `details->'handoff'->'http'->>'errorCode' = 'http_409'`.
- [ ] **Step 2: Suite completa, una vez, en primer plano** (backend):
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet build
  dotnet test
  ```
  Comparar las fallas por nombre contra la base de `develop` (hay fallas previas conocidas en `OrderExportApiTests`); sólo cuentan las nuevas.
- [ ] **Step 3: CLAUDE.md**, en «Gotchas verificados»: «**La causa de un traspaso del bot vive en el evento `AutomationPaused` (`details.handoff`), no en la corrida.** `conversation_runs.failure_code` sólo la tiene cuando falló; un bloque «Pasar a un asesor» alcanzado deja `HandedOff` sin código. Un `connection_unavailable` del bloque HTTP lleva el motivo en `ErrorCode` (`secret_unreadable`, `not_chosen`, …) y sigue sin fila en `http_calls`.»
- [ ] **Step 4: Commit** `test(automations): los dos traspasos de Clasic dejan su causa en el hilo (spec 2026-10-24)` y `docs: gotcha de la causa del traspaso`.

---

## FRONTEND

Todas contra el contrato de spec §6; no esperan al backend. Comando por archivo: `bun run test <ruta>`; al final `bun run test`, `bun run lint` y `bun run build`.

### Task F1: Tipos y parser de `event.handoff`

**Files:**
- Modify: `src/features/messaging/types/messaging.ts:410-421` (`ConversationEvent.handoff`) + tipos nuevos `AutomationHandoff`, `AutomationHandoffHttp`
- Modify: `src/features/messaging/services/messaging.api.ts:1233-1254` (`parseEvent`; lector nuevo `parseHandoff`)
- Modify (literales de `ConversationEvent`, agregar `handoff: null`): `src/features/messaging/components/conversation-event-line.tsx:8-15`, `src/test/messaging.ts:290-310` (`eventMessage`), `src/features/messaging/utils/conversation-event.test.ts:9-17`, y los que `bun run build` marque en `src/features/messaging/services/messaging.api.test.ts` y `src/routes/_authenticated/messaging/$conversationId.test.tsx`
- Test: `src/features/messaging/services/messaging.api.test.ts`

**Interfaces:**
- Produces:
```ts
export interface AutomationHandoffHttp {
  nodeId: string | null
  nodeTitle: string | null
  outcome: string | null
  errorCode: string | null
  status: number | null
  connectionName: string | null
}
/** Why the assistant handed the conversation over (spec backend 2026-10-24 §6). Codes, never free text; `kind` is kept as text. */
export interface AutomationHandoff {
  kind: string
  code: string | null
  nodeId: string | null
  nodeTitle: string | null
  connectionName: string | null
  connectionReason: string | null
  http: AutomationHandoffHttp | null
}
// ConversationEvent gains:
  /** Only an `AutomationPaused` with `pauseReason = 'Handoff'` may carry it; null on older events. */
  handoff: AutomationHandoff | null
```

- [ ] **Step 1: RED** en `messaging.api.test.ts` (junto a las pruebas de `pauseReason`):
  - `reads the handoff cause of an AutomationPaused event` — un mensaje con el JSON del caso 1 (spec §5.2) → `event.handoff` igual campo a campo.
  - `reads an event without handoff as null` (backend viejo: la clave ausente).
  - `never fails on a malformed handoff` — `handoff: "x"` → `null`; `handoff: { kind: 1 }` → `null` (sin `kind` de texto no hay causa); `handoff: { kind: 'Block', http: { status: '404' } }` → `http.status === null` y el resto leído; el hilo carga.
  - `keeps an unknown kind as text` — `kind: 'Future'` → `'Future'`.
- [ ] **Step 2: GREEN.** `parseHandoff(raw: JsonObject): AutomationHandoff | null` con lectores locales que devuelven `null` ante un tipo inesperado (no usar `textOf`/`shapeFail`, que lanzan: un evento nunca deja el hilo en blanco, `messaging.api.ts:1232`).
- [ ] **Step 3:** `bun run test src/features/messaging/services/messaging.api.test.ts` y `bun run build` (atrapa los literales sin `handoff`). **Commit** `feat(messaging): leer la causa del traspaso del evento AutomationPaused (spec backend 2026-10-24 §6)`.

### Task F2: El diccionario de causas (spec §7.1)

**Files:**
- Create: `src/features/messaging/utils/handoff-cause.ts`, `src/features/messaging/utils/handoff-cause.test.ts`
- Modify: `src/features/messaging/utils/conversation-event.ts:53-60`
- Test: `src/features/messaging/utils/conversation-event.test.ts`

**Interfaces:**
- Produces: `export function describeHandoff(handoff: AutomationHandoff): string` y `export function describeRunFailure(code: string | null): string | null`.

- [ ] **Step 1: RED.** `handoff-cause.test.ts` con `it.each` que cubre **cada fila** de la tabla de spec §7.1 (con y sin título; con y sin nombre de conexión), más:
  - Caso 2 exacto: `{ kind: 'Block', nodeTitle: 'Pasar a un asesor', http: { nodeTitle: 'Buscar cliente por teléfono', outcome: 'error_status', errorCode: 'http_404', status: 404 } }` → «El asistente pasó la conversación a un asesor porque «Buscar cliente por teléfono» respondió 404.»
  - Caso 1 exacto (el texto largo de spec §7.1).
  - `error_status` sin `status` pero con `errorCode: 'http_502'` → «…respondió 502.»
  - `kind: 'Future'` → «El asistente pasó la conversación a un asesor.»
  - `FlowFailed` con `http` → ignora el HTTP.
  - `describeRunFailure`: cada código de spec §7.1, uno desconocido → «Motivo: ocurrió un error en el flujo.», `null` → `null`.
  En `conversation-event.test.ts`: `AutomationPaused` + `Handoff` + `handoff` → el texto de `describeHandoff`; sin `handoff` → «El asistente pasó la conversación a un asesor.» (como hoy); `Manual` con `handoff` (no debería llegar) → el texto de pausa manual de hoy.
- [ ] **Step 2: GREEN.** Funciones puras, tablas `Record<string, …>` por `outcome`, motivo y código; nada de `switch` sin `default`. Una sola función arma el sujeto `{t}` y las frases `{C}`/`{W}` para que las comillas «» no se dupliquen.
- [ ] **Step 3:** `bun run test src/features/messaging/utils`. **Commit** `feat(messaging): el hilo dice por qué el asistente pasó la conversación (spec backend 2026-10-24 §7.1)`.

### Task F3: La línea del hilo

**Files:**
- Modify: `src/features/messaging/components/conversation-event-line.tsx:22` (`rounded-full` → `rounded-2xl`)
- Test: `src/routes/_authenticated/messaging/$conversationId.test.tsx` (junto a la prueba que pinta `AutomationPaused` con `AdvisorTook`, `~:2497`)

- [ ] **Step 1: RED.** `shows why the assistant handed the conversation over` — la página de mensajes trae un `eventMessage` (`src/test/messaging.ts:290`) `AutomationPaused`/`Handoff` con la causa del caso 2; el hilo muestra, dentro de `getByLabelText('Evento de la conversación')`, «El asistente pasó la conversación a un asesor porque «Buscar cliente por teléfono» respondió 404.». Y `an old handoff event keeps today's line` (sin `handoff`).
- [ ] **Step 2: GREEN.** Sólo la clase de la píldora (el texto ya sale de F2).
- [ ] **Step 3:** `bun run test src/routes/_authenticated/messaging/$conversationId.test.tsx`. **Commit** `feat(messaging): la línea del evento admite dos renglones`.

### Task F4: «Motivo» en «Datos capturados» (D-T8)

**Files:**
- Modify: `src/features/messaging/types/messaging.ts:201-215` (`failureCode: string | null`)
- Modify: `src/features/messaging/services/messaging.api.ts:203-212` (`failureCode: nullableTextOf(raw, 'failureCode', what)`; ausente se lee `null`)
- Modify: `src/features/messaging/components/captured-data-panel.tsx:57-64`
- Modify (fixtures): `src/test/messaging.ts` (el builder de los detalles, si existe; si no, los literales que marque `bun run build`)
- Test: `src/features/messaging/services/messaging.api.test.ts`, `src/features/messaging/components/contact-sheet.test.tsx`

- [ ] **Step 1: RED.** API: `reads failureCode of the automation details` y `an older backend without failureCode reads null`. `contact-sheet.test.tsx`, pestaña «Datos capturados»: con `status: 'Failed'`, `failureCode: 'automations.run.loop_detected'` → muestra «Motivo: el flujo daba vueltas sin esperar al cliente.» bajo «Falló»; con `failureCode: null` no hay línea «Motivo».
- [ ] **Step 2: GREEN.** `describeRunFailure(details.failureCode)`; si devuelve `null`, no se pinta nada.
- [ ] **Step 3:** `bun run test`, `bun run lint`, `bun run build` completos (Vitest no corre en CI: comparar fallas por nombre contra `develop`). **Commit** `feat(messaging): Datos capturados muestra el motivo de una corrida fallida (spec backend 2026-10-24 D-T8)`.

---

## Orden y dependencias

- Backend: B1 → B4 → B5; B2 → B3; B6; (B4, B5, B6) → B7 → B8 (con B2, B3); B9 independiente; B10 al final.
- Frontend: F1 → F2 → F3; F4 independiente. En paralelo con todo el backend.
- Despliegue en cualquier orden (spec §6, «Compatibilidad»).

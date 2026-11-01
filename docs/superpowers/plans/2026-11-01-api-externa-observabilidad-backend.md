# API externa: registro de consumos y estado de mensaje (backend) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el integrador sepa qué pasó con cada llamada a la API externa: registro consultable, `GET` de estado, `failed` tardío corregido y aviso de categoría.

**Architecture:** Un middleware registra cada llamada al terminar la respuesta (`Response.OnCompleted`), con contexto propio y sin tocar el envío. `ApiAccess` guarda las filas y lee el estado del mensaje por un puerto con adaptador en Bootstrapper, porque no ve Messaging. El `GET` externo y el registro comparten la misma traducción de estado.

**Tech Stack:** .NET 10, EF Core + Npgsql, Minimal APIs, xUnit, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-01-api-externa-observabilidad-design.md`

**Plan hermano:** `qep-frontend/docs/superpowers/plans/2026-11-01-api-externa-observabilidad-frontend.md`. Este se publica primero.

## Global Constraints

- Retención del registro: `15` días. Purga automática diaria.
- Nunca se guarda: API key, cuerpo, query string, headers, teléfono, parámetros ni `idempotencyKey`.
- El registro no tumba ni demora un envío: se escribe después de responder y sus errores se absorben.
- `ApiAccess` no referencia Messaging. `Application` no referencia EF ni Npgsql.
- Sin traza de excepción en lo que ve el tenant.
- `status`: `Sent`, `Delivered`, `Read`, `Failed` o `Unconfirmed`. `failureCode` y `failureReason` nulos fuera de los dos últimos.
- Sigue sin enviarse `allow_category_change` (D-T10). El aviso de categoría no bloquea envíos.
- Commits convencionales con el alcance del área, sin atribución de IA. Nunca sobre `main`.
- Comandos en PowerShell. `Api.exe` detenido antes de `dotnet build`, `test` o `ef`.
- Unitarias por tarea. Integración una sola vez, en la Tarea 7. Única excepción: `StatusIngestionTests` en la Tarea 1, porque el spec exige RED de esa regresión antes del fix.

## Review Focus

1. Repetir una `idempotencyKey` sin confirmar responde `502`: la fila conserva `message_id` y muestra `Unconfirmed`. Prueba en Tarea 4.
2. Una llamada con cuerpo completo: la fila no contiene la clave, el teléfono, los parámetros ni la `idempotencyKey`. Prueba en Tarea 4.
3. Filtrar por una clave de otro tenant devuelve lista vacía, nunca filas ajenas. Prueba en Tarea 5.
4. El puerto del registro lanza: el envío sigue respondiendo `201`. Prueba en Tarea 4.
5. `result=failure` sin mensajes fallidos: lista vacía de ids, y aun así devuelve las filas `>= 400`. Prueba en Tarea 5.

## Desvíos del spec

1. **`FindMarketingOptOutCustomerAsync` no cambia, pero su llamada sube antes de `ApplyAsync`.** Limpiar `wamid` borra la única forma de encontrar la fila cuando el webhook llega sin callback, como en `MarketingOptOutStatusTests`. El resultado se usa después, igual que hoy.
2. **Dos aserciones existentes cambian**, porque afirmaban que un `failed` conserva `wamid`: `StatusIngestionTests.cs:66` y `MarketingOptOutStatusTests.cs:49`.
3. **Índice extra por `occurred_at`** para que la purga no recorra toda la tabla. El spec nombra sólo el de tenant.
4. **Revocada o vencida se registran; `SecretMismatch` y `TenantNotActive` no.** Sin el secreto correcto, cualquiera llenaría el registro de un tenant conociendo sólo el prefijo.
5. **No generan fila:** ruta inexistente bajo `/external/` y `429` por IP. En ambos casos la autenticación nunca corrió, así que no hay tenant.
6. **La comparación de categoría usa `Kind` y además `VersionOf`.** `Kind` ya cubre `_v2`; `VersionOf` cubre una fila que volvió como `External` tras un borrado fallido. El método se llama `RequestedCategoryFor`, no `CategoryChanged`.
7. **`ListFailedIdsAsync` incluye `Unconfirmed`,** porque comparte `status = 4`. Para el filtro también es una falla.

## Hallazgos verificados

- **Lectores de `wamid` en filas `Failed`:** ninguno depende del valor. `OutboundGuards.cs:105` rechaza citar sin `wamid`, y el SPA ya no ofrece citar un `Failed` (`isQuotable`).
- **`UseExceptionHandler` va por fuera del middleware:** una excepción salta el código posterior a `next`. Por eso se usa `OnCompleted`, que además ve el status final y no demora la respuesta.
- **`CallbackPrefix` se envía en todos los salientes,** así que un `failed` real siempre trae callback en producción.

## Estructura de archivos

| Archivo | Responsabilidad |
| --- | --- |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/StatusIngestion.cs` | Predicado y limpieza de `wamid` |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookDeliveryProcessor.cs` | Orden de la consulta de opt-out |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/OutboundMessages.cs` | `CommitFailedAsync` limpia `wamid` |
| `src/Modules/Messaging/Modules.Messaging.Application/External/ExternalMessageState.cs` | Crear: traducción de estado |
| `src/Modules/Messaging/Modules.Messaging.Application/External/GetExternalMessage.cs` | Crear: query, handler, `IExternalMessageQueries` |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ExternalMessageQueries.cs` | Crear: lecturas SQL |
| `src/Modules/ApiAccess/Modules.ApiAccess.Domain/ApiKeyRequest.cs` | Crear: la fila |
| `src/Modules/ApiAccess/Modules.ApiAccess.Infrastructure/Persistence/ApiKeyRequestLog.cs` | Crear: escritura con contexto propio |
| `src/Modules/ApiAccess/Modules.ApiAccess.Infrastructure/Persistence/ApiKeyRequestRepository.cs` | Crear: búsqueda |
| `src/Modules/ApiAccess/Modules.ApiAccess.Infrastructure/ApiKeyRequestPurgeWorker.cs` | Crear: purga diaria |
| `src/Modules/ApiAccess/Modules.ApiAccess.Application/ListApiKeyRequests.cs` | Crear: query, DTOs, filtro |
| `src/Bootstrapper/ApiKeys/ExternalCallNote.cs` | Crear: lo que la llamada va dejando anotado |
| `src/Bootstrapper/ApiKeys/ExternalApiCallLoggingMiddleware.cs` | Crear: captura |
| `src/Bootstrapper/ApiKeys/HttpExternalCallNotes.cs` | Crear: adaptador de `IExternalCallNotes` |
| `src/Bootstrapper/ApiKeys/ApiMessageStates.cs` | Crear: adaptador de `IApiMessageStates` |

## Antes de empezar

- [ ] **Crear el worktree desde `develop`, fuera del árbol con trabajo de otras sesiones**

```powershell
git -C "C:/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend" worktree add -b feature/api-externa-observabilidad "C:/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend-worktrees/api-externa-observabilidad" develop
```

Expected: rama nueva y carpeta creada. Inicializar su propio `.codegraph` con `gentle-ai codegraph init --cwd <worktree>`. Nunca copiar el de otro checkout.

---

### Task 1: Un `failed` tardío no pisa un reintento

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/StatusIngestion.cs:41-55`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookDeliveryProcessor.cs:403-420`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/OutboundMessages.cs:223`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/StatusIngestionTests.cs`, `MarketingOptOutStatusTests.cs`

**Interfaces:**
- Consumes: `IOutboundMessages.ClaimAsync`, `OutboundApiKeyAuthor`, helpers existentes de `StatusIngestionTests`.
- Produces: nada que otra tarea consuma.

- [ ] **Step 1: Escribir las pruebas de regresión en `StatusIngestionTests`**

Usan `StateAsync`, que devuelve `status|failure|wamid|foto|version`.

```csharp
[Fact]
public async Task ALateFailedOfTheFirstAttemptDoesNotOverwriteAnAcceptedRetry()
{
    // Seed Sent con wamid.x; failed de x con callback: "4|131042|-|4|1".
    // Reintento aceptado: UPDATE directo a status 1, wamid.y, failure_code NULL.
    // Se reentrega el failed de x con el mismo callback.
    Assert.Equal("1|-|wamid.y|4|1", await StateAsync(f, id));
}

[Fact]
public async Task AnUnconfirmedRetryIsResolvedByTheAckOfTheCurrentAttempt()
{
    // Fila vieja: status 4, código 131042, wamid.x, client_id. ClaimAsync reclama por ese client_id y
    // CommitFailedAsync(-1, ...) la deja sin wamid. Llega sent de wamid.y con callback.
    Assert.Equal("1|wamid.y", await ScalarAsync<string>(f.ConnectionString, "SELECT status || '|' || wamid FROM messaging.messages WHERE id = @id", ("id", id)));
}

[Fact]
public async Task AnAckDuringTheRetryWaitsForItAndAppliesOverItsResult()
{
    // Fila Failed sin wamid. Conexión aparte: BEGIN, SELECT FOR UPDATE y UPDATE a status 1 con wamid.y.
    // ApplyDirectAsync(delivered de wamid.y con callback) arranca y no termina en 300 ms. COMMIT.
    Assert.Equal("2|-|wamid.y|2|1", await StateAsync(f, id));
}

[Fact]
public async Task AnUnconfirmedRowWithoutAPreviousAttemptIsStillResolvedByCallback()
{
    // Status 4, -1, sin wamid; sent de wamid.z con callback.
    Assert.Equal("1|wamid.z", await ScalarAsync<string>(f.ConnectionString, "SELECT status || '|' || wamid FROM messaging.messages WHERE id = @id", ("id", id)));
}

[Fact]
public async Task AStaleStatusEndsAsProcessedAndIsNotRetried()
{
    // El failed viejo de la primera prueba: su entrega queda procesada con attempts = 1.
    Assert.Equal("1|t", await ScalarAsync<string>(f.ConnectionString, DeliverySql + " ORDER BY id DESC LIMIT 1"));
}
```

La última fila de la foto en la primera prueba no se actualiza con un `UPDATE` directo; por eso queda `4`. El reintento real sí la actualiza.

- [ ] **Step 2: Correr y ver RED**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~StatusIngestionTests"`
Expected: FAIL `ALateFailedOfTheFirstAttemptDoesNotOverwriteAnAcceptedRetry`, porque la fila queda `4`. Las otras pueden pasar hoy: protegen contra la regresión del cambio. Guardar la salida literal.

- [ ] **Step 3: Corregir `StatusIngestion.ApplyAsync`**

Cambiar sólo el `UPDATE`. El `exists` queda igual, con `wamid = X OR id = callback`.

```sql
wamid = CASE WHEN {newStatus} = 4 THEN NULL ELSE COALESCE(wamid, {update.Wamid}) END
...
AND (wamid = {update.Wamid} OR (id = {callback} AND wamid IS NULL))
```

- [ ] **Step 4: Limpiar `wamid` en `CommitFailedAsync`**

Agregar `wamid = NULL` al `UPDATE` de `OutboundMessages.cs:223`.

- [ ] **Step 5: Subir la consulta de opt-out antes de `ApplyAsync`**

En `ProcessStatusesAsync`, guardar `var optedOut = await StatusIngestion.FindMarketingOptOutCustomerAsync(...)` antes de aplicar, y usar `optedOut is { } customerId` en la rama que hoy consulta. `StatusIngestion.FindMarketingOptOutCustomerAsync` no se toca.

- [ ] **Step 6: Ajustar las dos aserciones que afirmaban conservar `wamid`**

`StatusIngestionTests.cs:66` pasa a `"4|131047|-|4|1"`. `MarketingOptOutStatusTests.cs:49` filtra por el id devuelto por `SeedOutboundAsync`, no por `wamid`.

- [ ] **Step 7: Correr y ver GREEN**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~StatusIngestionTests|FullyQualifiedName~MarketingOptOutStatusTests"`
Expected: PASS, sin fallas.

- [ ] **Step 8: Commit**

```powershell
git add src/Modules/Messaging tests/Modules/Messaging/Modules.Messaging.IntegrationTests/StatusIngestionTests.cs tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MarketingOptOutStatusTests.cs
git commit -m "fix(messaging): stop a late failed status from overwriting a retry"
```

### Task 2: Consulta de estado de un mensaje

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/External/ExternalMessageState.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/External/GetExternalMessage.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ExternalMessageQueries.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/ExternalWhatsAppEndpoints.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/ExternalMessageStateTests.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ExternalMessageStatusApiTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record ExternalMessageState(string Status, int? FailureCode, string? FailureReason)` con `public static ExternalMessageState Describe(MessageStatus status, int? failureCode)`
  - `public sealed record ExternalMessageRow(Guid Id, Guid ConversationId, Guid ConnectionId, MessageStatus Status, int? FailureCode)`
  - `public interface IExternalMessageQueries`:
    - `Task<ExternalMessageRow?> FindAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken)`
    - `Task<IReadOnlyList<ExternalMessageRow>> FindManyAsync(Guid tenantId, IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken)`
    - `Task<IReadOnlyList<Guid>> ListFailedIdsAsync(Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken)`
  - `public sealed record ExternalMessageDto(Guid MessageId, Guid ConversationId, string Status, int? FailureCode, string? FailureReason)`

- [ ] **Step 1: Escribir `ExternalMessageStateTests`**

```csharp
[Theory]
[InlineData(MessageStatus.Sent, "Sent")]
[InlineData(MessageStatus.Delivered, "Delivered")]
[InlineData(MessageStatus.Read, "Read")]
public void WithoutAFailureThereIsNoCodeNorReason(MessageStatus status, string expected) =>
    Assert.Equal(new ExternalMessageState(expected, null, null), ExternalMessageState.Describe(status, null));

[Fact]
public void ACertainFailureUsesTheTextOfTheBubble() =>
    Assert.Equal(new ExternalMessageState("Failed", 131042, MessageFailureReasons.For(131042)), ExternalMessageState.Describe(MessageStatus.Failed, 131042));

[Fact]
public void AnUnconfirmedSendDoesNotInviteToRetry()
{
    var state = ExternalMessageState.Describe(MessageStatus.Failed, MessageFailureReasons.Unconfirmed);
    Assert.Equal(("Unconfirmed", -1), (state.Status, state.FailureCode));
    Assert.DoesNotContain("Intenta de nuevo", state.FailureReason, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Correr y ver RED**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~ExternalMessageStateTests"`
Expected: FAIL de compilación, `ExternalMessageState` no existe.

- [ ] **Step 3: Implementar `ExternalMessageState`**

Texto fijo de `Unconfirmed`: `"WhatsApp no confirmó el envío: el mensaje pudo haber salido. Revisa la conversación antes de mandar otro."`. No toca `MessageFailureReasons`, así que la burbuja sigue igual.

- [ ] **Step 4: Correr y ver GREEN**

Run: el mismo del Step 2. Expected: PASS.

- [ ] **Step 5: Implementar la consulta**

`GetExternalMessageQuery(Guid MessageId) : IQuery<ExternalMessageDto>`.

`GetExternalMessageHandler(IApiCaller caller, ITenantModules tenantModules, IExternalMessageQueries messages)`:
1. `ExternalApiAccess.EnsureAsync` con `MessagingScopes.TemplatesSend`.
2. `messages.FindAsync(caller.TenantId, query.MessageId, ct)`.
3. Sin fila, o `!caller.CanUseConnection(row.ConnectionId)`: `ResourceNotFoundException(MessagingErrorCodes.ExternalMessageNotFound, "No encontramos ese mensaje.")`.

Código nuevo: `ExternalMessageNotFound = "external.message_not_found"`. No usar `EnsureConnectionAsync`, que responde `403` y revelaría que existe.

`ExternalMessageQueries(MessagingDbContext)`: `SqlQuery` sobre `messaging.messages`, siempre con `tenant_id` y `sent_by_kind = 4`. `ListFailedIdsAsync` agrega `status = 4 AND occurred_at >= since`. Registrar `AddScoped<IExternalMessageQueries, ExternalMessageQueries>()`.

Ruta: `whatsapp.MapGet("/messages/{messageId:guid}", GetMessageAsync).RequireAuthorization(RequireTemplatesSend).Produces<ExternalMessageDto>()` con `ProducesProblem` de `401`, `403` y `404`.

- [ ] **Step 6: Escribir `ExternalMessageStatusApiTests`**

Armado como `ExternalApiEndToEndTests`. Pruebas:
- `AnAcceptedSendReadsSentThenFailedAfterTheWebhook`: tras `failed` 131042, `status = "Failed"`, `failureCode = 131042`.
- `AnUnconfirmedSendReadsUnconfirmed`: `MetaHandler` da `500`; `status = "Unconfirmed"`, `failureCode = -1`.
- `AMessageOfAnotherTenantIsA404`: código `external.message_not_found`.
- `AMessageOnAConnectionTheKeyCannotUseIsA404`.
- `AMessageSentByAMemberIsA404`.
- `AKeyWithoutTheScopeIsA403`.

- [ ] **Step 7: Compilar sin correr integración**

Run: `dotnet build tests/Modules/Messaging/Modules.Messaging.IntegrationTests`
Expected: `Build succeeded`. Se ejecutan en la Tarea 7.

- [ ] **Step 8: Commit**

```powershell
git add src/Modules/Messaging tests/Modules/Messaging
git commit -m "feat(messaging): let integrators read a message status"
```

### Task 3: Tabla del registro y purga

**Files:**
- Create: `src/Modules/ApiAccess/Modules.ApiAccess.Domain/ApiKeyRequest.cs`
- Create: `src/Modules/ApiAccess/Modules.ApiAccess.Infrastructure/Persistence/ApiKeyRequestLog.cs`
- Create: `src/Modules/ApiAccess/Modules.ApiAccess.Infrastructure/ApiKeyRequestPurgeWorker.cs`
- Create: migración `AddApiKeyRequests`
- Modify: `src/Modules/ApiAccess/Modules.ApiAccess.Application/ApiAccessPorts.cs`
- Modify: `src/Modules/ApiAccess/Modules.ApiAccess.Infrastructure/Persistence/ApiAccessDbContext.cs`
- Modify: `src/Modules/ApiAccess/Modules.ApiAccess.Infrastructure/ApiAccessInfrastructureExtensions.cs`
- Test: `tests/Modules/ApiAccess/Modules.ApiAccess.UnitTests/ApiKeyRequestTests.cs`
- Test: `tests/Modules/ApiAccess/Modules.ApiAccess.IntegrationTests/ApiKeyRequestPersistenceTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class ApiKeyRequest` con constantes `RetentionDays = 15`, `PathMaxLength = 512`, `ErrorCodeMaxLength = 100`, `TraceIdMaxLength = 128`, y:

    ```csharp
    public static ApiKeyRequest Record(
        Guid tenantId, Guid apiKeyId, string apiKeyName, string method, string path, int statusCode,
        string? errorCode, Guid? messageId, string? traceId, int durationMs, DateTimeOffset occurredAt)
    ```

  - `public interface IApiKeyRequestLog { Task RecordAsync(ApiKeyRequest request, CancellationToken cancellationToken); }`, que no propaga sus errores.

- [ ] **Step 1: Escribir `ApiKeyRequestTests`**

```csharp
[Fact]
public void ASuccessKeepsNoErrorCode() =>
    Assert.Null(NewRequest(statusCode: 201, errorCode: "server.unexpected").ErrorCode);

[Fact]
public void TooLongValuesAreCutToTheirColumns()
{
    var request = NewRequest(path: "/" + new string('a', 600), apiKeyName: new string('b', 100), traceId: new string('c', 200));
    Assert.Equal((512, 80, 128), (request.Path.Length, request.ApiKeyName.Length, request.TraceId!.Length));
}

[Fact]
public void EachRequestGetsItsOwnTimeOrderedId() =>
    Assert.Equal(7, NewRequest().Id.Version);

[Fact]
public void ANegativeDurationIsZero() =>
    Assert.Equal(0, NewRequest(durationMs: -5).DurationMs);
```

- [ ] **Step 2: Correr y ver RED**

Run: `dotnet test tests/Modules/ApiAccess/Modules.ApiAccess.UnitTests --filter "FullyQualifiedName~ApiKeyRequestTests"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementar `ApiKeyRequest`**

Entidad de sólo lectura una vez escrita. `Id = Guid.CreateVersion7()`. `ErrorCode` nulo si `statusCode < 400`.

- [ ] **Step 4: Correr y ver GREEN**

Run: el mismo del Step 2. Expected: PASS.

- [ ] **Step 5: Mapear la tabla y generar la migración**

Tabla `api_access.api_key_requests`, columnas en `snake_case` del spec. Índices `IX_api_key_requests_tenant_occurred` sobre `(tenant_id, occurred_at DESC)` e `IX_api_key_requests_occurred` sobre `(occurred_at)`.

```powershell
dotnet ef migrations add AddApiKeyRequests --project src/Modules/ApiAccess/Modules.ApiAccess.Infrastructure --context ApiAccessDbContext -o Persistence/Migrations
```

Expected: migración nueva y snapshot regenerado. Revisar que sólo cree esa tabla.

- [ ] **Step 6: Implementar `ApiKeyRequestLog` y la purga**

`ApiKeyRequestLog(DbContextOptions<ApiAccessDbContext> options, ILogger<ApiKeyRequestLog> logger)`, copia de `RequestFailureLog`: contexto propio, `catch` que loguea y absorbe.

`ApiKeyRequestPurgeWorker(IServiceScopeFactory, ILogger<...>)`, copia de `WebhookPurgeWorker`: cada `24` horas, lotes de `1000`, corte `clock.UtcNow.AddDays(-ApiKeyRequest.RetentionDays)`, `internal Task DrainAsync(CancellationToken)`.

Registrar `AddScoped<IApiKeyRequestLog, ApiKeyRequestLog>()` y `AddHostedService<ApiKeyRequestPurgeWorker>()` en `AddApiAccessInfrastructure`.

- [ ] **Step 7: Escribir `ApiKeyRequestPersistenceTests`**

- `AFailureToWriteIsAbsorbed`: opciones con cadena de conexión inválida; `RecordAsync` no lanza.
- `ThePurgeDeletesOnlyRowsOlderThanFifteenDays`: filas de `16` y `14` días; queda la de `14`.

- [ ] **Step 8: Compilar y commit**

Run: `dotnet build tests/Modules/ApiAccess/Modules.ApiAccess.IntegrationTests`
Expected: `Build succeeded`.

```powershell
git add src/Modules/ApiAccess tests/Modules/ApiAccess
git commit -m "feat(api-access): store external API calls for 15 days"
```

### Task 4: Capturar cada llamada

**Files:**
- Create: `src/Bootstrapper/ApiKeys/ExternalCallNote.cs`
- Create: `src/Bootstrapper/ApiKeys/ExternalApiCallLoggingMiddleware.cs`
- Create: `src/Bootstrapper/ApiKeys/HttpExternalCallNotes.cs`
- Modify: `src/BuildingBlocks/BuildingBlocks.Application/ApiAccessContracts.cs`
- Modify: `src/Modules/ApiAccess/Modules.ApiAccess.Application/ApiKeyVerifier.cs`
- Modify: `src/Bootstrapper/ApiKeys/ApiKeyAuthenticationHandler.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs`
- Modify: `src/Api/ApiExceptionHandler.cs`, `src/Api/Program.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/External/SendExternalTemplate.cs`, `.../External/GetExternalMessage.cs`
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/ExternalCallNoteTests.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ExternalApiRequestLogTests.cs`

**Interfaces:**
- Consumes: `ApiKeyRequest.Record`, `IApiKeyRequestLog`.
- Produces:
  - `public interface IExternalCallNotes { void NoteMessage(Guid messageId); }` en BuildingBlocks.
  - `public sealed record RejectedApiKey(Guid Id, string Name, Guid TenantId)` y cuarto parámetro `RejectedApiKey? RejectedKey = null` en `ApiKeyVerification`.
  - `public sealed class ExternalCallNote` con:
    - `public static ExternalCallNote Attach(HttpContext context)`
    - `public static ExternalCallNote? Find(HttpContext context)`
    - `public void Identify(Guid apiKeyId, string apiKeyName, Guid tenantId)`
    - `public void NoteError(string code)`
    - `public void NoteMessage(Guid messageId)`
    - `public ApiKeyRequest? ToRequest(string method, string path, int statusCode, string? traceId, int durationMs, DateTimeOffset occurredAt)`

- [ ] **Step 1: Escribir `ExternalCallNoteTests`**

```csharp
[Fact]
public void ACallWithoutAKnownKeyProducesNoRow() =>
    Assert.Null(ExternalCallNote.Attach(new DefaultHttpContext()).ToRequest("POST", Path, 401, "t", 1, Now));

[Fact]
public void AnIdentifiedCallKeepsWhatWasNoted()
{
    var note = ExternalCallNote.Attach(new DefaultHttpContext());
    note.Identify(KeyId, "ERP", TenantId);
    note.NoteMessage(MessageId);
    note.NoteError("external.send_unconfirmed");

    var row = note.ToRequest("POST", Path, 502, "t", 12, Now)!;

    Assert.Equal((TenantId, KeyId, "ERP", MessageId, "external.send_unconfirmed"), (row.TenantId, row.ApiKeyId, row.ApiKeyName, row.MessageId, row.ErrorCode));
}

[Fact]
public void FindReturnsNothingOutsideTheExternalApi() =>
    Assert.Null(ExternalCallNote.Find(new DefaultHttpContext()));
```

- [ ] **Step 2: Correr y ver RED**

Run: `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~ExternalCallNoteTests"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementar `ExternalCallNote` y `HttpExternalCallNotes`**

La nota vive en `HttpContext.Items`. `HttpExternalCallNotes(IHttpContextAccessor)` escribe en ella si existe. Registrar `AddScoped<IExternalCallNotes, HttpExternalCallNotes>()` junto a `IApiCaller`.

- [ ] **Step 4: Correr y ver GREEN**

Run: el mismo del Step 2. Expected: PASS.

- [ ] **Step 5: Identificar la clave al autenticar**

`ApiKeyVerifier`: en `Revoked` y `Expired`, devolver `RejectedKey = new(key.Id.Value, key.Name, key.TenantId)`. Ningún otro rechazo lo lleva.

`ApiKeyAuthenticationHandler`:
- Clave válida o `RejectedKey` presente: `ExternalCallNote.Find(Context)?.Identify(...)`.
- `WriteProblemAsync`: `ExternalCallNote.Find(Context)?.NoteError(code)`.

`ApiExceptionHandler.TryHandleAsync`, tras `MapException`: `ExternalCallNote.Find(httpContext)?.NoteError(code)`.

- [ ] **Step 6: Implementar el middleware**

`public sealed partial class ExternalApiCallLoggingMiddleware(RequestDelegate next, IServiceScopeFactory scopes, ILogger<ExternalApiCallLoggingMiddleware> logger)`.

Fuera de `ExternalApiPaths.IsExternal`, sólo `next`. Dentro, antes de `next`:
1. `ExternalCallNote.Attach(context)`.
2. Capturar método, `Request.Path`, `Activity.Current?.Id ?? context.TraceIdentifier`, `IClock.UtcNow` y `Stopwatch.GetTimestamp()`.
3. `context.Response.OnCompleted(...)`: `note.ToRequest(...)`, y con fila, `scopes.CreateAsyncScope()`, `IApiKeyRequestLog.RecordAsync(row, CancellationToken.None)`.

Todo el callback va en `try/catch` que loguea. Se usa `Request.Path`, nunca `QueryString`.

En `Program.cs`, `app.UseMiddleware<ExternalApiCallLoggingMiddleware>();` inmediatamente antes de `RequestFailureLoggingMiddleware`.

- [ ] **Step 7: Anotar el mensaje en los handlers**

`SendExternalTemplateHandler` recibe `IExternalCallNotes notes`:
- `Repeat` deja de ser `static` y llama `notes.NoteMessage(messageId)` después de `SameTemplate` y antes de decidir `502` o replay. Un `409` no anota.
- Después del bloque `if (claim.Existing is { } held)` y antes de llamar a Meta: `notes.NoteMessage(claim.MessageId)`.

`GetExternalMessageHandler`, ya validada la fila: `notes.NoteMessage(row.Id)`.

- [ ] **Step 8: Escribir `ExternalApiRequestLogTests`**

Las filas se escriben después de responder: usar un helper que sondee `api_access.api_key_requests` cada `50` ms hasta `5` s.

- `AnAcceptedSendWritesOneRowWithItsMessage`: `201`, `message_id` de la respuesta, `trace_id` no vacío, `duration_ms >= 0`.
- `TheRowCarriesNoneOfTheCallData`: `row_to_json` no contiene la clave en claro, el teléfono, los valores de parámetros ni la `idempotencyKey`.
- `AnUnconfirmedRepeatKeepsTheMessageOnTheRow`: dos `POST` con la misma clave; ambas filas `502` con el mismo `message_id` y `external.send_unconfirmed`.
- `AValidationFailureHasItsCodeAndNoMessage`: `422`, `validation.failed`, `message_id` nulo.
- `AMissingScopeIsRecorded`: `403`, `external.auth.scope_missing`.
- `ARevokedKeyIsRecordedWithItsTenant`: `401`, `external.auth.invalid_key`.
- `AnExpiredKeyIsRecordedWithItsTenant`.
- `AWrongSecretAndAnInventedKeyWriteNothing`.
- `AThrowingLogDoesNotBreakTheSend`: `IApiKeyRequestLog` que lanza, vía `WithWebHostBuilder`; responde `201`.
- `TheRowTraceIdIsTheOneTheClientGot`: comparar `traceId` de un `422` con `trace_id`.

- [ ] **Step 9: Compilar y commit**

Run: `dotnet build tests/Modules/Messaging/Modules.Messaging.IntegrationTests; if ($?) { dotnet build src/Api }`
Expected: `Build succeeded` en ambos.

```powershell
git add src tests
git commit -m "feat(api-access): record every external API call after responding"
```

### Task 5: Leer el registro

**Files:**
- Create: `src/Modules/ApiAccess/Modules.ApiAccess.Application/ListApiKeyRequests.cs`
- Create: `src/Modules/ApiAccess/Modules.ApiAccess.Infrastructure/Persistence/ApiKeyRequestRepository.cs`
- Create: `src/Bootstrapper/ApiKeys/ApiMessageStates.cs`
- Modify: `src/Modules/ApiAccess/Modules.ApiAccess.Application/ApiAccessPorts.cs`
- Modify: `src/Modules/ApiAccess/Modules.ApiAccess.Api/ApiKeyEndpoints.cs`
- Modify: `src/Modules/ApiAccess/Modules.ApiAccess.Infrastructure/ApiAccessInfrastructureExtensions.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/ApiAccess/Modules.ApiAccess.UnitTests/ListApiKeyRequestsTests.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ApiKeyRequestsApiTests.cs`

**Interfaces:**
- Consumes: `IExternalMessageQueries`, `ExternalMessageState`.
- Produces:
  - `public sealed record ApiMessageState(string Status, int? FailureCode, string? FailureReason)`
  - `public interface IApiMessageStates`:
    - `Task<IReadOnlyDictionary<Guid, ApiMessageState>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken)`
    - `Task<IReadOnlyList<Guid>> ListFailedIdsAsync(Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken)`
  - `public enum ApiKeyRequestResult { Success, Failure }`
  - `public sealed record ApiKeyRequestSearch(Guid TenantId, Guid? ApiKeyId, ApiKeyRequestResult? Result, IReadOnlyCollection<Guid> FailedMessageIds, string? TraceId, DateTimeOffset? OccurredFrom, DateTimeOffset? OccurredTo, int Page, int PageSize)`
  - `public interface IApiKeyRequestRepository { Task<(IReadOnlyList<ApiKeyRequest> Items, int Total)> SearchAsync(ApiKeyRequestSearch search, CancellationToken cancellationToken); }`
  - `public sealed record ListApiKeyRequestsQuery(Guid TenantId, Guid? ApiKeyId, string? Result, string? TraceId, DateTimeOffset? OccurredFrom, DateTimeOffset? OccurredTo, int Page, int PageSize) : IQuery<ApiKeyRequestPage>`
  - `public sealed record ApiKeyRequestPage(IReadOnlyList<ApiKeyRequestDto> Items, int Total, int Page, int PageSize)`
  - `public sealed record ApiKeyRequestDto(Guid Id, DateTimeOffset OccurredAt, Guid ApiKeyId, string ApiKeyName, string Method, string Path, int StatusCode, string? ErrorCode, Guid? MessageId, string? TraceId, int DurationMs, ApiMessageState? Message)`
  - `public static class ApiKeyRequestPaging` con `DefaultPageSize = 25`, `MaxPageSize = 100`, `NormalizePage`, `NormalizePageSize`
  - `internal static ApiKeyRequestResult? ParseResult(string? value)` en `ApiKeyRequestFilter`

- [ ] **Step 1: Escribir `ListApiKeyRequestsTests`**

```csharp
[Theory]
[InlineData("success", ApiKeyRequestResult.Success)]
[InlineData("FAILURE", ApiKeyRequestResult.Failure)]
public void TheResultIsReadWithoutCase(string value, ApiKeyRequestResult expected) =>
    Assert.Equal(expected, ApiKeyRequestFilter.ParseResult(value));

[Theory]
[InlineData(null)]
[InlineData("  ")]
public void AnEmptyResultIsNoFilter(string? value) =>
    Assert.Null(ApiKeyRequestFilter.ParseResult(value));

[Fact]
public void AnUnknownResultIsAFieldError()
{
    var error = Assert.Throws<ValidationException>(() => ApiKeyRequestFilter.ParseResult("maybe"));
    Assert.Equal("result", Assert.Single(error.Errors).PropertyName);
}

[Theory]
[InlineData(0, 25)]
[InlineData(101, 100)]
[InlineData(40, 40)]
public void ThePageSizeStaysInRange(int asked, int expected) =>
    Assert.Equal(expected, ApiKeyRequestPaging.NormalizePageSize(asked));
```

- [ ] **Step 2: Correr y ver RED**

Run: `dotnet test tests/Modules/ApiAccess/Modules.ApiAccess.UnitTests --filter "FullyQualifiedName~ListApiKeyRequestsTests"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementar filtro, paginación y handler**

`ListApiKeyRequestsHandler(IApiKeyRequestRepository, IApiMessageStates, IExecutionContext, ITenantModules, IClock)`:
1. `ApiAccessAuthorization.EnsureAsync` con `ApiKeyManage`.
2. Parsear `Result` y normalizar página y `TraceId`.
3. Con `Result`: `ListFailedIdsAsync(tenantId, OccurredFrom ?? clock.UtcNow.AddDays(-ApiKeyRequest.RetentionDays), ct)`. Sin él, lista vacía.
4. `SearchAsync`.
5. `FindAsync` con los `MessageId` de la página, y armar los DTOs. Mensaje ausente: `Message = null`.

- [ ] **Step 4: Correr y ver GREEN**

Run: el mismo del Step 2. Expected: PASS.

- [ ] **Step 5: Implementar repositorio, adaptador y ruta**

`ApiKeyRequestRepository`, siempre con `TenantId` y `AsNoTracking`. Orden `OccurredAt DESC, Id DESC`, `Skip` y `Take`.

```csharp
Failure => r.StatusCode >= 400 || (r.MessageId != null && ids.Contains(r.MessageId.Value))
Success => r.StatusCode < 400 && (r.MessageId == null || !ids.Contains(r.MessageId.Value))
```

`ApiMessageStates(IExternalMessageQueries)` usa `ExternalMessageState.Describe`. Registrarlo junto a `ApiKeyConnectionDirectory`.

Ruta, antes de `POST /`: `group.MapGet("/requests", ListRequestsAsync).RequireAuthorization(ApiAccessPermissions.ApiKeyManage).Produces<ApiKeyRequestPage>().ProducesProblem(403).ProducesProblem(422)`. Query: `apiKeyId`, `result`, `traceId`, `occurredFrom`, `occurredTo`, `page = 1`, `pageSize = ApiKeyRequestPaging.DefaultPageSize`.

- [ ] **Step 6: Escribir `ApiKeyRequestsApiTests`**

- `ALateFailureReadsAsFailureOnA201Row`: `201` + webhook `failed`; fila `statusCode = 201`, `message.status = "Failed"`, y aparece con `result=failure`, no con `result=success`.
- `AFailureFilterWithoutFailedMessagesStillReturnsHttpFailures`.
- `TheTotalMatchesTheFilterAcrossPages`: `pageSize=1`.
- `AKeyOfAnotherTenantReturnsNothing`.
- `ARowsOfAnotherTenantAreNotListed`.
- `WithoutThePermissionIsA403`.
- `WithTheModuleOffIsA403`.
- `AnUnknownResultIsA422WithItsField`.
- `TheResponseCarriesNoStackTrace`: el JSON no contiene `at ` ni `Exception`.

- [ ] **Step 7: Compilar y commit**

Run: `dotnet build tests/Modules/Messaging/Modules.Messaging.IntegrationTests`
Expected: `Build succeeded`.

```powershell
git add src tests
git commit -m "feat(api-access): list external API calls with the message state"
```

### Task 6: Aviso de categoría

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/BillingTemplateProvisioning.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/TemplateManagementDtos.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/TemplateManagement.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/BillingTemplateProvisioningTests.cs`

**Interfaces:**
- Produces:
  - `public static string? RequestedCategoryFor(MessageTemplateKind kind, string name, string actualCategory)` en `BillingTemplateDefinitions`. Devuelve `Category` sólo si es plantilla de cobro y difiere.
  - `ManagedTemplateDto` con último parámetro `string? RequestedCategory = null`. El frontend lo lee como `requestedCategory`.

- [ ] **Step 1: Escribir las pruebas en `BillingTemplateProvisioningTests`**

```csharp
[Theory]
[InlineData(MessageTemplateKind.BillingOverdue, "qep_cobro_vencido", "MARKETING", "UTILITY")]
[InlineData(MessageTemplateKind.BillingOverdue, "qep_cobro_vencido_v2", "MARKETING", "UTILITY")]
[InlineData(MessageTemplateKind.External, "qep_cobro_vencido_v3", "MARKETING", "UTILITY")]
[InlineData(MessageTemplateKind.BillingReminder, "qep_cobro_recordatorio", "UTILITY", null)]
[InlineData(MessageTemplateKind.BillingReminder, "qep_cobro_recordatorio", "utility", null)]
[InlineData(MessageTemplateKind.Free, "promo", "MARKETING", null)]
[InlineData(MessageTemplateKind.External, "qep_cobro_vencido_v02", "MARKETING", null)]
public void TheRequestedCategoryAppearsOnlyWhenABillingTemplateChanged(
    MessageTemplateKind kind, string name, string actual, string? expected) =>
    Assert.Equal(expected, BillingTemplateDefinitions.RequestedCategoryFor(kind, name, actual));

[Fact]
public async Task AProvisionedTemplateReclassifiedByMetaWarnsAndReportsItsCategory()
{
    var rig = new Rig();
    rig.Bed.Meta.NextCreatedTemplate = /* resultado exitoso con new CreatedTemplate("777", "PENDING", "MARKETING") */;

    var templates = await rig.ProvisionAsync();

    Assert.All(templates, template => Assert.Equal("UTILITY", template.RequestedCategory));
    Assert.Contains(rig.Logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("MARKETING", StringComparison.Ordinal));
}
```

`Rig` expone `Logger`: una propiedad `RecordingLogger<ProvisionBillingTemplatesHandler>` en lugar de la instancia en línea.

- [ ] **Step 2: Correr y ver RED**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~BillingTemplateProvisioningTests"`
Expected: FAIL de compilación, `RequestedCategoryFor` no existe.

- [ ] **Step 3: Implementar**

`RequestedCategoryFor`: definición con `definition.Kind == kind || definition.VersionOf(name) is not null`; comparar con `OrdinalIgnoreCase`.

`ManagedTemplateMapping.ToDto` pasa el valor. En `HandleAsync`, después de `CreateAsync`, si no es nulo, `LogCategoryChanged`: `Warning` con tenant, conexión, nombre, categoría pedida y real. No va ni token ni respuesta de Meta. La creación no se frena.

- [ ] **Step 4: Correr y ver GREEN**

Run: el mismo del Step 2. Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Messaging tests/Modules/Messaging/Modules.Messaging.UnitTests/BillingTemplateProvisioningTests.cs
git commit -m "feat(messaging): flag billing templates Meta reclassified"
```

### Task 7: Verificación final

**Files:** ninguno nuevo. Sólo se corrige lo que estas corridas revelen.

**Interfaces:** `GET /api-keys/requests`, `GET /external/whatsapp/messages/{id}` y `requestedCategory` quedan disponibles para el plan de frontend.

- [ ] **Step 1: Unitarias y arquitectura**

Run: `dotnet test tests/ArchitectureTests; if ($?) { dotnet test tests/Modules/ApiAccess/Modules.ApiAccess.UnitTests }; if ($?) { dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests }; if ($?) { dotnet test tests/Bootstrapper/Bootstrapper.UnitTests }`
Expected: PASS en todas.

- [ ] **Step 2: Suite completa de integración, una sola vez**

Con Docker corriendo. Tarda decenas de minutos: no es un cuelgue.

Run: `dotnet test`
Expected: PASS. Registrar conteos literales. Una falla se corrige y se relanza sólo su clase con `--filter`, no la suite completa.

- [ ] **Step 3: Revisar que no se filtren secretos**

Run: `rg -n "X-Api-Key|Body|QueryString|idempotency" src/Bootstrapper/ApiKeys/ExternalApiCallLoggingMiddleware.cs src/Bootstrapper/ApiKeys/ExternalCallNote.cs`
Expected: sin coincidencias.

- [ ] **Step 4: Commit de correcciones, si las hubo**

```powershell
git add src tests
git commit -m "fix(api-access): address integration findings"
```

Mergear a `develop`, nunca a `main`. Deploy: backend primero.

## Self-Review

1. **Cobertura del spec:** parte 1 en Tareas 3, 4 y 5; parte 2 en Tarea 2; parte 3 en Tarea 1; parte 4 en Tarea 6. La parte 5 y las pantallas están en el plan de frontend.
2. **Pasos:** cada uno fija firma, archivo y valor. Los cuerpos quedan al implementador salvo el SQL y el filtro, que son decisiones.
3. **Tipos:** `ExternalMessageRow`, `ApiMessageState`, `IExternalCallNotes` y `ApiKeyRequestSearch` coinciden entre tareas.
4. **Review Focus:** las cinco líneas tienen prueba en su tarea.
5. **Proporción:** sin transcribir el programa; las pruebas con estructura narrada se apoyan en los harness existentes.

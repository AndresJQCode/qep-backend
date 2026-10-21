# Constructor de chatbots, backend 1 (Mensajería, módulo y constructor) — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Dejar en `develop` las fases 1 a 3 del spec: Mensajería lista para un bot (interactivos salientes, autoría, salida sin asignación, cola de entrantes apagada por flag), el módulo `automations` con sus permisos, y el módulo `Automations` con el agregado Chatbot, el modelo del flujo, el validador, los endpoints del editor, las plantillas y el import/export. Sin motor todavía (plan 2).

**Architecture:** Un módulo nuevo `Automations` con las cuatro capas de Pipeline, que sólo ve Tenancy y entra a Messaging e Integrations por puertos adaptados en Bootstrapper. Messaging gana lo que un bot necesita para hablar (D-I1..D-I6) y una cola propia (`messaging.automation_inbox`, D-M1) que nadie consume aún. El flujo es un documento jsonb (D-C1, D-C2) validado en el servidor (D-C3).

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, xunit.v3 + Testcontainers (Postgres 18), System.Text.Json.

**Spec:** `docs/superpowers/specs/2026-10-21-constructor-de-chatbots-design.md` (§4, §6 y las decisiones D-C*, D-I*). Los puertos del motor que acá sólo se declaran se consumen en el plan 2.

## Global Constraints

- Rama `feature/automatizaciones-constructor-backend` en un worktree desde `develop` (`prompt-nuevo-spec.md`); merge `--ff-only` a `develop`, nunca a `main`, nunca push.
- Conventional commits, en español, citando el spec (`spec 2026-10-21 D-I1`); **sin atribución de IA** (`CLAUDE.md:63`): sin `Co-Authored-By`.
- TDD: RED antes que GREEN, con la salida literal de ambas corridas en el reporte de cada tarea (`CLAUDE.md:57`).
- Copy y comentarios en español colombiano, tuteando. Identificadores, códigos de error, rutas y permisos en inglés y tal cual el spec.
- Códigos de error `<módulo>.<recurso>.<motivo>`; ningún status HTTP se arma a mano: `ApiExceptionHandler` mapea las excepciones de `BuildingBlocks.Application/ApplicationExceptions.cs` (404 `ResourceNotFoundException`, 403 `RequestForbiddenException`, 409 `ResourceConflictException`, 412 `RequestConcurrencyException`, 428 `PreconditionRequiredException`, 422 `DomainException` / `ValidationException`).
- Un permiso nuevo = constante + `PermissionDefinition` + `AddPolicy` + rol; sin la política el síntoma es 500.
- Aislamiento de tenant doble: ruta `{tenantId:guid}` + política, y el handler revalida (403, nunca 404). Todo método de repositorio recibe `tenantId`.
- Traducir errores de base en Infrastructure por **nombre de índice**, nunca sólo por `SqlState`.
- Las migraciones se generan con la fábrica de diseño: `dotnet ef migrations add <Nombre> --project src/Modules/<M>/Modules.<M>.Infrastructure --context <M>DbContext -o Persistence/Migrations`. Antes de correr pruebas locales: `$env:Seed__Enabled = "false"`.
- Correr sólo las pruebas del módulo tocado más `tests/ArchitectureTests` con `--filter`.
- `docs/estado.md` **no** se toca en la rama (se actualiza en `develop` al mergear, tarea 20).

## Review Focus

1. **Un botón con `title` de 21 caracteres o una lista con 11 filas** no puede llegar a Meta: `InteractiveSendValidation` lo rechaza antes del `POST` (tarea 1) y `FlowValidator` lo marca `buttons.label_too_long` / `list.too_many_rows` (tarea 12).
2. **Un `clientId` repetido del bot** devuelve el mensaje ya enviado y no manda dos veces (tarea 6, `BotOutboundTests.RepeatedClientIdReturnsTheExistingMessage`).
3. **Un reenvío de Meta del mismo wamid** no deja dos filas en `automation_inbox` (tarea 7, `AutomationInboxTests.MetaRedeliveryDoesNotEnqueueTwice`).
4. **Un `flow` con un `kind` desconocido o un JSON de 600 KB** responde 422 `validation.failed` con la clave `flow`, no 500 (tarea 14, `SaveDraftRejectsUnknownKindAndOversizedFlow`).
5. **Publicar dos chatbots en la misma conexión desde dos requests concurrentes** deja uno solo activo: el índice parcial responde 409 `automations.chatbot.connection_taken` al segundo (tarea 15, `ConcurrentPublishOnTheSameConnectionLeavesOneActive`).

---

## Fase 1: Mensajería

### Task 1: `InteractiveSend` y su validación

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/InteractiveSend.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/InteractiveSendTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public abstract record InteractiveSend(string Body, string? Header, string? Footer);
  public sealed record InteractiveButton(string Id, string Title);
  public sealed record ButtonsSend(string Body, IReadOnlyList<InteractiveButton> Buttons, string? Header = null, string? Footer = null) : InteractiveSend(Body, Header, Footer);
  public sealed record ListRow(string Id, string Title, string? Description = null);
  public sealed record ListSection(string? Title, IReadOnlyList<ListRow> Rows);
  public sealed record ListSend(string Body, string ButtonLabel, IReadOnlyList<ListSection> Sections, string? Header = null, string? Footer = null) : InteractiveSend(Body, Header, Footer);
  public sealed record CtaUrlSend(string Body, string DisplayText, Uri Url, string? Header = null, string? Footer = null) : InteractiveSend(Body, Header, Footer);
  public static class InteractiveLimits { public const int BodyMax = 1024, HeaderMax = 60, FooterMax = 60, ButtonsMax = 3, ButtonTitleMax = 20, ButtonIdMax = 256, RowsMax = 10, RowTitleMax = 24, RowDescriptionMax = 72, ListButtonMax = 20, DisplayTextMax = 20; }
  public static class InteractiveSendValidation { public static IReadOnlyList<string> Validate(InteractiveSend send); }   // códigos, vacío = válido
  public static class InteractiveSendJson { public static JsonObject ToMetaObject(InteractiveSend send); public static string ToDetailsJson(InteractiveSend send); }
  ```
- Códigos de `Validate`: `interactive.body.empty`, `interactive.body.too_long`, `interactive.buttons.empty`, `interactive.buttons.too_many`, `interactive.buttons.title_too_long`, `interactive.buttons.id_duplicated`, `interactive.list.no_rows`, `interactive.list.too_many_rows`, `interactive.list.row_title_too_long`, `interactive.list.row_description_too_long`, `interactive.list.button_too_long`, `interactive.list.id_duplicated`, `interactive.cta.display_text_too_long`, `interactive.cta.url_not_https`.
- `ToMetaObject` produce el objeto `interactive` de Meta: botones `{"type":"button","body":{"text"},"action":{"buttons":[{"type":"reply","reply":{"id","title"}}]}}`; lista `{"type":"list","header":{"type":"text","text"}?,"body":{"text"},"footer":{"text"}?,"action":{"button","sections":[{"title"?,"rows":[{"id","title","description"?}]}]}}`; URL `{"type":"cta_url","body":{"text"},"action":{"name":"cta_url","parameters":{"display_text","url"}}}`. `ToDetailsJson` = el mismo objeto serializado (D-I2).

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
public sealed class InteractiveSendTests
{
    [Fact] public void ThreeButtonsOfTwentyCharactersAreValid() => Assert.Empty(InteractiveSendValidation.Validate(new ButtonsSend("¿Qué necesitas?", [new("a", new string('x', 20)), new("b", "Dos"), new("c", "Tres")])));
    [Fact] public void FourButtonsFail() => Assert.Contains("interactive.buttons.too_many", InteractiveSendValidation.Validate(new ButtonsSend("x", [new("a","1"),new("b","2"),new("c","3"),new("d","4")])));
    [Fact] public void AButtonTitleOfTwentyOneFails() => Assert.Contains("interactive.buttons.title_too_long", InteractiveSendValidation.Validate(new ButtonsSend("x", [new("a", new string('x', 21))])));
    [Fact] public void ElevenRowsFail() => Assert.Contains("interactive.list.too_many_rows", InteractiveSendValidation.Validate(new ListSend("x", "Ver", [new(null, Enumerable.Range(0, 11).Select(i => new ListRow($"r{i}", $"Fila {i}")).ToArray())])));
    [Fact] public void RowsAreCountedAcrossSections() => Assert.Contains("interactive.list.too_many_rows", InteractiveSendValidation.Validate(new ListSend("x", "Ver", [new("A", Rows(6)), new("B", Rows(5, 6))])));
    [Fact] public void DuplicatedRowIdsFail() => Assert.Contains("interactive.list.id_duplicated", InteractiveSendValidation.Validate(new ListSend("x", "Ver", [new(null, [new("r","a"), new("r","b")])])));
    [Fact] public void CtaUrlMustBeHttps() => Assert.Contains("interactive.cta.url_not_https", InteractiveSendValidation.Validate(new CtaUrlSend("x", "Ir", new Uri("http://app.clasic.com.co"))));
    [Fact] public void ButtonsMetaObjectHasTheMetaShape()
    {
        var json = InteractiveSendJson.ToMetaObject(new ButtonsSend("¿Qué necesitas?", [new("tech", "Servicio técnico")])).ToJsonString();
        Assert.Equal("""{"type":"button","body":{"text":"¿Qué necesitas?"},"action":{"buttons":[{"type":"reply","reply":{"id":"tech","title":"Servicio técnico"}}]}}""", json);
    }
    [Fact] public void ListMetaObjectOmitsAbsentHeaderFooterAndDescription()
    {
        var json = InteractiveSendJson.ToMetaObject(new ListSend("Elige", "Ver equipos", [new("Equipos", [new("1", "Calentador")])])).ToJsonString();
        Assert.Equal("""{"type":"list","body":{"text":"Elige"},"action":{"button":"Ver equipos","sections":[{"title":"Equipos","rows":[{"id":"1","title":"Calentador"}]}]}}""", json);
    }
    [Fact] public void CtaUrlMetaObjectHasTheMetaShape()
    {
        var json = InteractiveSendJson.ToMetaObject(new CtaUrlSend("No estás registrado.", "Registrarme", new Uri("https://app.clasic.com.co/registro-clientes"))).ToJsonString();
        Assert.Equal("""{"type":"cta_url","body":{"text":"No estás registrado."},"action":{"name":"cta_url","parameters":{"display_text":"Registrarme","url":"https://app.clasic.com.co/registro-clientes"}}}""", json);
    }
}
```

- [ ] **Step 2: Correr y verificar que fallan**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~InteractiveSendTests"`
Expected: FAIL por compilación (`InteractiveSend` no existe).

- [ ] **Step 3: Implementar los tipos, `Validate` y `ToMetaObject` en `InteractiveSend.cs`**

`JsonObject` de `System.Text.Json.Nodes`, en el orden de claves de las pruebas (las aserciones comparan el texto).

- [ ] **Step 4: Correr y verificar que pasan**

Run: el mismo comando. Expected: PASS (10 pruebas).

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Messaging/Modules.Messaging.Application/InteractiveSend.cs tests/Modules/Messaging/Modules.Messaging.UnitTests/InteractiveSendTests.cs
git commit -m "feat(messaging): modelo y validación de mensajes interactivos (spec 2026-10-21 D-I1)"
```

### Task 2: `SendInteractiveAsync` en el cliente de Meta

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IWhatsAppCloudClient.cs:43-50` (nuevo método junto a `SendTextAsync`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Meta/WhatsAppCloudClient.cs:72-74` (implementación junto a `SendTextAsync`)
- Modify: todo doble de `IWhatsAppCloudClient` en `tests/` (buscar `: IWhatsAppCloudClient`): agregar el método con `throw new NotSupportedException()` o un registro de llamadas.
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/WhatsAppCloudClientInteractiveTests.cs`

**Interfaces:**
- Produces: `Task<SendResult> SendInteractiveAsync(MessagingSender sender, SendTarget target, InteractiveSend send, string callbackData, string? contextWamid, CancellationToken cancellationToken);`
- Implementación: `PostMessageAsync(sender, MessagePayload(target, "interactive", InteractiveSendJson.ToMetaObject(send), callbackData, contextWamid), cancellationToken)`.

- [ ] **Step 1: Escribir las pruebas que fallan** (copiar el arreglo con stub handler de las `WhatsAppCloudClient*Tests.cs` existentes)

```csharp
[Fact] public async Task PostsAnInteractivePayloadWithCallbackData()
{
    // stub que captura el body del POST a /{phone}/messages y responde {"messages":[{"id":"wamid.1"}]}
    var result = await client.SendInteractiveAsync(sender, new SendToUserId("bsuid-1"), new ButtonsSend("x", [new("a", "A")]), "qep:abc", null, CancellationToken.None);
    Assert.Equal(SendOutcome.Sent, result.Outcome); Assert.Equal("wamid.1", result.Wamid);
    var body = JsonNode.Parse(captured.Body)!;
    Assert.Equal("interactive", (string)body["type"]!); Assert.Equal("button", (string)body["interactive"]!["type"]!);
    Assert.Equal("qep:abc", (string)body["biz_opaque_callback_data"]!); Assert.Equal("bsuid-1", (string)body["recipient"]!);
}
[Fact] public async Task AGraphErrorMapsLikeText() { /* 400 con error.code 131047 → Outcome GraphError, Code 131047 */ }
```

- [ ] **Step 2: Correr y verificar que fallan** — Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~WhatsAppCloudClientInteractiveTests"`. Expected: FAIL por compilación.
- [ ] **Step 3: Declarar e implementar `SendInteractiveAsync`; actualizar los dobles de prueba.**
- [ ] **Step 4: Correr Messaging.UnitTests completo** — Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests`. Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(messaging): SendInteractiveAsync en el cliente de Meta (spec 2026-10-21 D-I1)"`

### Task 3: Migración `AddAutomationEngine` de Messaging

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessageEnums.cs` (`public enum MessageAuthorKind { Member = 1, Chatbot = 2 }`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessageRecord.cs:22` (`public MessageAuthorKind SentByKind { get; set; } = MessageAuthorKind.Member; public Guid? SentByChatbotId { get; set; }`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/Conversation.cs:89-98` (`public Guid? AutomationChatbotId { get; private set; } public string? AutomationStep { get; private set; }`, setters privados; mapeo en el DbContext)
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/AutomationInboxRecord.cs` (`internal sealed class AutomationInboxRecord { MessageId, TenantId, ConnectionId, ConversationId, bool Opened, CreatedAt, short Attempts, NextAttemptAt, DateTimeOffset? ProcessedAt, string? Outcome }`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingDbContext.cs:150-165, 244-292` (constantes `SentByKindCheck`, `SentByChatbotCheck`, `SentByOneCheck`, `AutomationChatbotCheck`; `ConfigureAutomationInbox`)
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/Migrations/<ts>_AddAutomationEngine.cs` (generada)
- Modify: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MessagingApiHarness.cs:723-727` (`ActivateAutomationAsync(connectionString, conversationId, Guid chatbotId)` escribe también `automation_chatbot_id`; actualizar sus llamadores)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/AutomationEnginePersistenceTests.cs`

**Interfaces:**
- Produces: las columnas y la tabla del spec §4.2 (bloque «En Messaging»), con los nombres de CHECK e índices del spec: `CK_messages_sent_by_kind`, `CK_messages_sent_by_chatbot`, `CK_messages_sent_by_one`, `CK_conversations_automation_chatbot`, `IX_automation_inbox_pending`, `IX_automation_inbox_conversation`.

- [ ] **Step 1: Escribir las pruebas que fallan** (una base por prueba con `StartDatabaseAsync`, SQL crudo con `ExecuteAsync`)

```csharp
[Fact] public async Task AChatbotMessageWithoutChatbotIdIsRejected()   // INSERT sent_by_kind = 2, sent_by_chatbot_id NULL → PostgresException con ConstraintName "CK_messages_sent_by_chatbot"
[Fact] public async Task AChatbotMessageCannotAlsoHaveAMember()        // kind 2 + member → "CK_messages_sent_by_one"
[Fact] public async Task AnActiveConversationNeedsAChatbotId()         // UPDATE automation_status='Active', automation_started_at=now(), automation_chatbot_id NULL → "CK_conversations_automation_chatbot"
[Fact] public async Task TheInboxKeysByMessageId()                     // dos INSERT con el mismo message_id → 23505
[Fact] public async Task ExistingMessagesDefaultToMemberKind()         // SELECT sent_by_kind de una fila sembrada con SeedOutboundAsync == 1
```

- [ ] **Step 2: Correr y verificar que fallan** — Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~AutomationEnginePersistenceTests"`. Expected: FAIL (columnas inexistentes).
- [ ] **Step 3: Agregar el enum, las propiedades, el mapeo y los CHECK; generar la migración** — Run: `dotnet ef migrations add AddAutomationEngine --project src/Modules/Messaging/Modules.Messaging.Infrastructure --context MessagingDbContext -o Persistence/Migrations`. Revisar que `Up` tenga sólo lo del spec (el snapshot no debe traer cambios ajenos).
- [ ] **Step 4: Correr y verificar que pasan** — mismo comando. Expected: PASS (5).
- [ ] **Step 5: Commit** — `git commit -m "feat(messaging): autoría de chatbot, paso del asistente y cola automation_inbox (spec 2026-10-21 §4.2)"`

### Task 4: Autoría en el reclamo y en la lectura

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IOutboundMessages.cs:16-19` (`OutboundDraft`: `Guid? SentByMemberId`, nuevo `Guid? SentByChatbotId = null`), `:22-24` (`ExistingOutbound`: `Guid? SentByChatbotId = null`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/OutboundMessages.cs:23-31, 38-46, 109-141` (INSERT y SELECT con `sent_by_kind`/`sent_by_chatbot_id`; kind = 2 cuando `SentByChatbotId` viene)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IMessageQueries.cs:7-21` (`MessageRow`: `Guid? SentByChatbotId = null` al final) y su lector SQL en Infrastructure (`MessageQueries`).
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingDtos.cs:100-118` (`MessageDto`: `ChatbotRefDto? SentByChatbot = null` al final; `public sealed record ChatbotRefDto(Guid ChatbotId, string Name);`)
- Create: `src/Modules/Messaging/Modules.Messaging.Application/IAutomationsChatbotNames.cs` (`public interface IAutomationsChatbotNames { Task<IReadOnlyDictionary<Guid, string>> ResolveAsync(Guid tenantId, IReadOnlyCollection<Guid> chatbotIds, CancellationToken ct); }`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingSupport.cs:158-195, 242-262` (`ToDto` recibe `IReadOnlyDictionary<Guid, string> chatbotNames`; `MemberIdsOf` ignora filas con `SentByChatbotId`; nuevo `ChatbotIdsOf(rows)`); llamadores (`ListMessages`, `SendMessage`, `SendMediaMessage`, `SendAudioMessage`, `SendTemplateMessage`, `SearchMessages`) resuelven nombres por el puerto.
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:855-872` — registrar `IAutomationsChatbotNames` con un `NullAutomationsChatbotNames` (devuelve «Chatbot» para todo id) **hasta la tarea 16**, que lo reemplaza por el real.
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessageMappingAuthorshipTests.cs`, `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MessageAuthorshipApiTests.cs`

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// unit
[Fact] public void AChatbotRowMapsToSentByChatbotAndNoMember() { var dto = MessageMapping.ToDto(RowWithChatbot(id), tenant, memberNames: Empty, replyTargets: Empty, chatbotNames: new Dictionary<Guid,string>{[id]="Servicio técnico"}); Assert.Null(dto.SentBy); Assert.Equal(new ChatbotRefDto(id, "Servicio técnico"), dto.SentByChatbot); }
[Fact] public void AnUnknownChatbotNameFallsBackToChatbot() => Assert.Equal("Chatbot", MessageMapping.ToDto(RowWithChatbot(id), ..., chatbotNames: Empty).SentByChatbot!.Name);
[Fact] public void MemberIdsOfSkipsChatbotRows() => Assert.Empty(MessageMapping.MemberIdsOf([RowWithChatbot(id)]));
// integración: sembrar por SQL un saliente con sent_by_kind = 2 y GET /messaging/conversations/{id}/messages
[Fact] public async Task TheThreadShowsTheChatbotAuthor() { ... Assert.Null(message.SentBy); Assert.Equal("Chatbot", message.SentByChatbot!.Name); }
```

- [ ] **Step 2: Correr y verificar que fallan** — Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~MessageMappingAuthorshipTests"`. Expected: FAIL por compilación.
- [ ] **Step 3: Implementar los cambios de records, SQL, mapeo y el puerto con su adaptador nulo.**
- [ ] **Step 4: Correr Messaging.UnitTests + IntegrationTests con filtro `Authorship`** — Expected: PASS. Correr además `dotnet test tests/Modules/Messaging` completo: PASS (nada existente se rompe por el parámetro nuevo).
- [ ] **Step 5: Commit** — `git commit -m "feat(messaging): sentByChatbot en los mensajes y puerto de nombres de chatbots (spec 2026-10-21 D-I3)"`

### Task 5: Dominio de la conversación y auditoría de sistema

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/Conversation.cs:298-317` (`StartAutomation(Guid chatbotId, DateTimeOffset now)` reemplaza a `StartAutomation(DateTimeOffset now)`; escribe `AutomationChatbotId`), nuevo `public void SetAutomationStep(string? stepTitle)` (sin `Touch`), nuevo `public void EndAutomation(DateTimeOffset now)` (exige `Active`; deja `None` y limpia las columnas; `Touch()`; sin evento), `ClearAutomation()` limpia también chatbot y paso.
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingDtos.cs:56` (`ConversationAutomationDto(string Status, DateTimeOffset StartedAt, DateTimeOffset? PausedAt, MemberRefDto? PausedBy, string? PauseReason, Guid? ChatbotId = null, string? ChatbotName = null, string? StepTitle = null)`), `MessagingSupport.cs:129` y `ConversationRow` (columnas nuevas; el nombre por `IAutomationsChatbotNames`).
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingAuditRecorder.cs:9` (nuevo `void RecordBySystem(Guid tenantId, string action, Guid conversationId, DateTimeOffset occurredAt);`) y `Infrastructure/Persistence/MessagingAuditRecorder.cs` (actor `Guid.Empty`, `AuditActorType.System`, como `RecordCampaign(bySystem: true)`).
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationAutomationTests.cs` (ampliar), `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ConversationAutomationApiTests.cs` (ampliar)

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
[Fact] public void StartAutomationRecordsTheChatbot() { c.StartAutomation(chatbotId, now); Assert.Equal(chatbotId, c.AutomationChatbotId); Assert.Equal(ConversationAutomationStatus.Active, c.AutomationStatus); }
[Fact] public void EndAutomationLeavesNoneAndClears() { c.StartAutomation(chatbotId, now); c.SetAutomationStep("Número de documento"); c.EndAutomation(now); Assert.Equal(ConversationAutomationStatus.None, c.AutomationStatus); Assert.Null(c.AutomationChatbotId); Assert.Null(c.AutomationStep); Assert.Null(c.AutomationStartedAt); }
[Fact] public void EndAutomationRequiresActive() => Assert.Equal(MessagingErrorCodes.AutomationNotAvailable, Assert.Throws<MessagingDomainException>(() => c.EndAutomation(now)).Code);
[Fact] public void ResolveClearsChatbotAndStep() { ... }
// integración: ActivateAutomationAsync(cs, conversationId, chatbotId) + UPDATE automation_step; GET conversation → automation.chatbotId == chatbotId, automation.stepTitle == "Número de documento", automation.chatbotName == "Chatbot"
[Fact] public async Task TheSummaryCarriesChatbotAndStep()
// RecordBySystem: una fila en audit.entries con actor_type System y actor vacío
[Fact] public async Task RecordBySystemWritesASystemActor()
```

- [ ] **Step 2: Correr y verificar que fallan** — Run: `dotnet test tests/Modules/Messaging --filter "FullyQualifiedName~ConversationAutomation"`. Expected: FAIL por compilación.
- [ ] **Step 3: Implementar dominio, DTO, lector y `RecordBySystem`.** Actualizar los llamadores existentes de `StartAutomation(now)` en pruebas.
- [ ] **Step 4: Correr y verificar que pasan** — mismo filtro: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(messaging): chatbot y paso del asistente en la conversación, EndAutomation y auditoría de sistema (spec 2026-10-21 D-M11, D-M14)"`

### Task 6: `IBotOutbound`, la salida del bot sin asignación

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/BotOutbound.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/OutboundGuards.cs:179-234` (si `SettleAsync` no es reutilizable desde fuera de la clase, extraerlo a `internal static class OutboundSettlement` sin cambiar su comportamiento; `SendMessageHandler` y demás lo siguen usando)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs` (`services.AddScoped<IBotOutbound, BotOutbound>()`)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/BotOutboundTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record BotSend(Guid TenantId, Guid ConversationId, Guid ChatbotId, Guid ClientId, string? Text, InteractiveSend? Interactive);
  public abstract record BotSendResult
  {
      public sealed record Sent(Guid MessageId, bool AlreadySent) : BotSendResult;
      public sealed record NotAutomated : BotSendResult;        // no Open, con asesor, o automation_status ≠ Active
      public sealed record WindowClosed : BotSendResult;
      public sealed record ConnectionUnavailable : BotSendResult;
      public sealed record Rejected(string Code) : BotSendResult;   // messaging.interactive.invalid, messaging.message.rejected, …
  }
  public interface IBotOutbound { Task<BotSendResult> SendAsync(BotSend send, CancellationToken cancellationToken); }
  ```
- Reglas (D-I4): sin `IExecutionContext`, sin membresía, sin `TryAutoAssignAsync`. Carga la conversación con `IConversationRepository.FindAsync`; exige `Status == Open && AssignedMemberId is null && AutomationStatus == Active`, `IsWindowOpen(now)`; `Interactive` válido por `InteractiveSendValidation` (si no, `Rejected("messaging.interactive.invalid")`); `ClaimAsync(new OutboundDraft(..., SentByMemberId: null, SentByChatbotId: send.ChatbotId, Kind: Interactive? Interactive : Text, DetailsJson: ToDetailsJson(...), Text: send.Text ?? interactive.Body))`; si `Existing` → `Sent(existing.Id, AlreadySent: true)` sin llamar a Meta; `ResolveSenderAsync` (nulo → `ConnectionUnavailable`); `SendTextAsync`/`SendInteractiveAsync` con callback `"qep:" + messageId`; `SettleAsync` como los humanos (`MarkRepliedAsync`, `ConversationChangedAsync`).

- [ ] **Step 1: Escribir las pruebas que fallan** (harness Messaging + `FakeMetaGraphHandler`; conversación sembrada, `ActivateAutomationAsync(cs, conversation, chatbotId)`)

```csharp
[Fact] public async Task SendsTextAsTheChatbotWithoutAssigning()      // Sent; messages.sent_by_kind == 2, sent_by_chatbot_id == chatbotId, sent_by_member_id NULL; conversations.assigned_member_id NULL; automation_status sigue 'Active'
[Fact] public async Task SendsButtonsAndStoresTheInteractiveDetails() // kind == Interactive(10), details->>'type' == 'button', el POST a Meta llevó type interactive
[Fact] public async Task RepeatedClientIdReturnsTheExistingMessage()  // segundo SendAsync con el mismo ClientId → Sent(AlreadySent: true), un solo POST a Meta
[Fact] public async Task RefusesWhenAnAdvisorHoldsTheConversation()   // Take por API antes → NotAutomated, sin POST
[Fact] public async Task RefusesWhenTheWindowIsClosed()               // last_inbound_at = now - 25h → WindowClosed, fila Failed? no: sin fila (no reclama)
[Fact] public async Task AnInvalidInteractiveIsRejectedBeforeMeta()   // 4 botones → Rejected("messaging.interactive.invalid"), sin POST
[Fact] public async Task AGraphRejectionBecomesRejected()             // FakeMeta responde 400 code 131026 → Rejected("messaging.message.rejected"), fila Failed
```

- [ ] **Step 2: Correr y verificar que fallan** — Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~BotOutboundTests"`. Expected: FAIL por compilación.
- [ ] **Step 3: Implementar `BotOutbound` y registrarlo.**
- [ ] **Step 4: Correr y verificar que pasan** — mismo filtro: PASS (7). Correr `dotnet test tests/Modules/Messaging`: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(messaging): IBotOutbound, salida del chatbot sin asignación (spec 2026-10-21 D-I4)"`

### Task 7: Cola `automation_inbox`: encolar en la ingesta y puerto `IAutomationInbox`

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/IAutomationInbox.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Options/MessagingAutomationOptions.cs` (`public sealed class MessagingAutomationOptions { public const string Section = "Messaging:Automation"; public bool Enqueue { get; set; } }`, bound en `MessagingInfrastructureExtensions`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/InboundIngestion.cs:21, 152, 272` (`IngestAsync` gana `bool enqueueAutomation`; el `RETURNING` suma `automation_status AS "AutomationStatus"`; `IngestedInbound(Guid MessageId, Guid ConversationId, Guid? AssignedMemberId, string AutomationStatus, bool Opened)` con `Opened = OldStatus is null or "Resolved"`; INSERT en `automation_inbox` cuando `enqueueAutomation && AssignedMemberId is null && AutomationStatus in ("None","Active")` y el kind está en D-M1)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookDeliveryProcessor.cs:147` y `WebhookDeliveryProcessor.Instagram.cs:164-171` (pasan `options.Value.Enqueue`; Instagram pasa siempre `false`: D-M6)
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/AutomationInbox.cs` (implementa el puerto por SQL)
- Modify: `MessagingInfrastructureExtensions.cs` (`services.AddScoped<IAutomationInbox, AutomationInbox>()`)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/AutomationInboxTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record AutomationInboxItem(Guid MessageId, Guid TenantId, Guid ConnectionId, Guid ConversationId, bool Opened, DateTimeOffset CreatedAt, int Attempts);
  public sealed record InboundMediaForAutomation(string MimeType, string? FileName, bool Stored);
  public sealed record InboundForAutomation(Guid MessageId, MessageKind Kind, string? Text, string? ReplyId, InboundMediaForAutomation? Media, DateTimeOffset OccurredAt);
  public interface IAutomationInbox
  {
      Task<IReadOnlyList<AutomationInboxItem>> ClaimAsync(DateTimeOffset now, int batch, CancellationToken ct);   // SKIP LOCKED, sólo la más vieja pendiente por conversación, lease 30 s / 2 min / 10 min según attempts
      Task CompleteAsync(Guid messageId, string outcome, CancellationToken ct);                                    // processed_at = now, outcome
      Task RetryLaterAsync(Guid messageId, TimeSpan delay, CancellationToken ct);                                  // next_attempt_at = now + delay; con attempts >= 5 marca processed_at y outcome 'failed'
      Task<InboundForAutomation?> ReadInboundAsync(Guid tenantId, Guid messageId, CancellationToken ct);           // ReplyId = details->'button_reply'->>'id' ?? details->'list_reply'->>'id'; Stored = message_media.stored_at IS NOT NULL
  }
  ```

- [ ] **Step 1: Escribir las pruebas que fallan** (host con `Messaging:Automation:Enqueue=true` vía `UseSetting`; `PostWebhookAsync` + `DrainDeliveriesAsync`)

```csharp
[Fact] public async Task AnInboundToAnUnassignedConversationIsEnqueuedAsOpened()   // 1 fila, opened == true, attempts == 0
[Fact] public async Task ASecondInboundIsEnqueuedAsNotOpened()                      // opened == false
[Fact] public async Task AnInboundToAnAssignedConversationIsNotEnqueued()           // Take antes → 0 filas
[Fact] public async Task AnInboundToAPausedAutomationIsNotEnqueued()                // automation_status 'Paused' → 0 filas
[Fact] public async Task MetaRedeliveryDoesNotEnqueueTwice()                        // mismo wamid dos veces → 1 fila
[Fact] public async Task WithTheFlagOffNothingIsEnqueued()                          // host sin el setting → 0 filas
[Fact] public async Task ClaimReturnsOnlyTheOldestPendingPerConversation()          // 2 filas misma conversación → Claim devuelve 1; tras Complete, la segunda
[Fact] public async Task ClaimLeasesAndRetryLaterFailsAtFiveAttempts()              // Claim, RetryLater(1s) ×4 → la quinta deja processed_at y outcome 'failed'
[Fact] public async Task ReadInboundExtractsTheReplyId()                            // webhook interactive button_reply id "tech" → ReplyId == "tech", Kind Interactive
[Fact] public async Task ReadInboundReportsWhetherMediaIsStored()                   // imagen antes de DrainMediaAsync → Stored false; después → true
```

- [ ] **Step 2: Correr y verificar que fallan** — Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~AutomationInboxTests"`. Expected: FAIL por compilación.
- [ ] **Step 3: Implementar opciones, ingesta, puerto y SQL.** El INSERT va en la misma transacción que el UPDATE de la conversación. `ClaimAsync` con la forma de `MediaCopyWorker.cs:69-81` más `NOT EXISTS (SELECT 1 FROM messaging.automation_inbox o WHERE o.conversation_id = i.conversation_id AND o.processed_at IS NULL AND o.created_at < i.created_at)`.
- [ ] **Step 4: Correr y verificar que pasan** — mismo filtro: PASS (10). `dotnet test tests/Modules/Messaging`: PASS (la ingesta existente no cambia con el flag apagado).
- [ ] **Step 5: Commit** — `git commit -m "feat(messaging): cola automation_inbox tras un flag y puerto IAutomationInbox (spec 2026-10-21 D-M1)"`

## Fase 2: módulo por tenant y permisos

### Task 8: Clave de módulo `automations`

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs:57-73` (`Automations = Define("automations")`, último de `All`, `[Automations] = [Messaging]`, fuera de `DefaultForNewTenants`)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs:113-114` (el CHECK suma `'automations'`)
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/<ts>_AddAutomationsModuleKey.cs` (copiar `20261018105954_AddShopifyModuleKey.cs:14-41`; `Down` borra las filas `automations`)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/ChangeTenantModules.cs:27-28` (el mensaje del validador usa `TenantModuleKeys.All.Count`)
- Modify: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleKeysTests.cs:13, 51-62` y las demás listadas en el spec §4.1 que comparan la lista literal (`TenantModulesApiTests`, `SeedStartupTests`, `OperatorConsoleApiTests`, `OperatorReadHandlerTests`, `ChangeTenantModulesHandlerTests`, `TenantModuleChangeBatchTests`, `TenantChangePersistenceTests`)
- Modify: `README.md` § «Módulos por tenant» (fila nueva)

- [ ] **Step 1: Cambiar las pruebas** — `AllKeepsTheContractOrder` termina en `"automations"`; nueva `AutomationsIsLastAndDependsOnMessaging` (`Assert.Same(TenantModuleKeys.Automations, TenantModuleKeys.All[^1]); Assert.Equal([TenantModuleKeys.Messaging], DependenciesOf(Automations))`); `DefaultForNewTenants` no cambia.
- [ ] **Step 2: Correr y verificar que fallan** — Run: `dotnet test tests/Modules/Tenancy --filter "FullyQualifiedName~TenantModuleKeysTests"`. Expected: FAIL.
- [ ] **Step 3: Implementar la clave, el CHECK, la migración (`dotnet ef migrations add AddAutomationsModuleKey --project src/Modules/Tenancy/Modules.Tenancy.Infrastructure --context TenancyDbContext -o Persistence/Migrations`) y el validador.**
- [ ] **Step 4: Correr Tenancy completo** — Run: `dotnet test tests/Modules/Tenancy`. Expected: PASS (ajustar las listas literales de las pruebas que fallen por la clave nueva).
- [ ] **Step 5: Commit** — `git commit -m "feat(tenancy): módulo automations, dependiente de messaging (spec 2026-10-21 D-C11)"`

### Task 9: Esqueleto del módulo `Automations`, base y harness

**Files:**
- Create: `src/Modules/Automations/Modules.Automations.Domain/Modules.Automations.Domain.csproj`, `AutomationsErrorCodes.cs`, `AutomationsDomainException.cs`
- Create: `src/Modules/Automations/Modules.Automations.Application/Modules.Automations.Application.csproj` (copia de Pipeline), `AutomationsPermissions.cs` (`ChatbotRead = "automations.chatbot.read"`, `ChatbotManage = "automations.chatbot.manage"`), `AutomationsSupport.cs` (`AutomationsAuthorization.EnsureAsync` con `TenantModuleKeys.Automations`, `AutomationsCaller`, `AutomationsNotFound`, `AutomationsVersions.Ensure`)
- Create: `src/Modules/Automations/Modules.Automations.Infrastructure/Modules.Automations.Infrastructure.csproj` (copia de Pipeline, `InternalsVisibleTo` para `Modules.Automations.UnitTests` y `.IntegrationTests`), `AutomationsInfrastructureExtensions.cs`, `AutomationsDatabaseInitializer.cs`, `Persistence/AutomationsDbContext.cs` (esquema `automations`, proyección de `audit.entries`), `Persistence/AutomationsDbContextFactory.cs`, `Persistence/Migrations/<ts>_AddAutomations.cs`
- Create: `src/Modules/Automations/Modules.Automations.Api/Modules.Automations.Api.csproj`, `AutomationsEndpoints.cs` (grupo `/api/v1/tenants/{tenantId:guid}/automations`, tag `Automations`, `RequireVersion(HttpContext)` copiado de `MessagingEndpoints.cs:474-479`)
- Modify: `Backend.slnx` (carpetas `/src/Modules/Automations/` y `/tests/Modules/Automations/`), `tests/ArchitectureTests/ArchitectureTests.csproj` (referencias a los cuatro proyectos), `src/Bootstrapper/Bootstrapper.csproj` y `src/Api/Api.csproj` si referencian módulos uno por uno (verificar cómo entra Pipeline)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:784` (`services.AddAutomationsInfrastructure(configuration)` después de Pipeline), `src/Api/Program.cs:198, 262` (`app.MapAutomationsEndpoints()`; `InitializeAutomationsDatabaseAsync` después de Shopify)
- Create: `tests/ArchitectureTests/ArchitectureTests/AutomationsLayerTests.cs` (copia de `PipelineLayerTests.cs`; el `[Theory]` prohíbe `Modules.Messaging`, `Modules.Integrations`, `Modules.Customers`, `Modules.Identity`)
- Create: `tests/Modules/Automations/Modules.Automations.UnitTests/*.csproj`, `tests/Modules/Automations/Modules.Automations.IntegrationTests/*.csproj`, `AutomationsApiHarness.cs` (copia de `PipelineApiHarness.cs:17-140` con `AutomationsUrl`, `EnableAutomationsAsync` = `EnableModuleAsync(..., "automations")` **más** `"messaging"`, `SeedWhatsAppConnectionAsync` y `SeedInstagramConnectionAsync` copiados de `MessagingApiHarness.cs:357-418`, `SeedExternalApiConnectionAsync` que inserta un `IntegrationConnection` del proveedor `external-api` por su DbContext **sólo desde la tarea 16**)
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/AutomationsSchemaTests.cs`

**Interfaces:**
- Produces: las siete tablas del spec §4.2 con sus CHECK e índices (`IX_chatbots_tenant_name`, `IX_chatbot_connections_active`, `IX_chatbot_templates_tenant_name`, `IX_conversation_runs_live`, `IX_conversation_runs_tenant_chatbot_started`, `IX_http_calls_tenant_called`); entidades EF `Chatbot`, `ChatbotVersion`, `ChatbotConnection`, `ChatbotTemplate` (Domain, tarea 11 define el comportamiento; acá sólo propiedades mapeables) y registros internos `ConversationRunRecord`, `HttpCallRecord`, `SimulationRecord` (Infrastructure).
- `AutomationsErrorCodes`: los de §4.3 como constantes `ChatbotNotFound`, `ChatbotNameTaken`, `ChatbotInvalid`, `ChatbotConnectionTaken`, `ChatbotChannelNotSupported`, `ChatbotNotPublished`, `ChatbotAlreadyPaused`, `ChatbotAlreadyActive`, `ChatbotHasLiveRuns`, `TemplateNotFound`, `SimulationNotFound`, `SimulationExpired`, `ExternalApiNotFound`.

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// AutomationsLayerTests: las siete pruebas de PipelineLayerTests adaptadas
// AutomationsSchemaTests (harness): 
[Fact] public async Task TheSchemaHasTheSevenTables()            // information_schema.tables where table_schema='automations' → chatbots, chatbot_versions, chatbot_connections, chatbot_templates, conversation_runs, http_calls, simulations
[Fact] public async Task OnlyOneActiveChatbotPerConnection()      // dos INSERT active=true misma (tenant, connection) → 23505 con "IX_chatbot_connections_active"
[Fact] public async Task OnlyOneLiveRunPerConversation()          // dos runs 'Waiting' misma conversación → 23505 "IX_conversation_runs_live"
[Fact] public async Task AnUnknownRouteUnderAutomationsIs404NotMissingModule() // GET /api/v1/tenants/{t}/automations/nothing → 404 (el grupo existe)
```

- [ ] **Step 2: Correr y verificar que fallan** — Run: `dotnet build Backend.slnx` falla (proyectos inexistentes) → crear csproj vacíos; luego `dotnet test tests/ArchitectureTests --filter "FullyQualifiedName~AutomationsLayerTests"` y `dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~AutomationsSchemaTests"`: FAIL.
- [ ] **Step 3: Crear proyectos, DbContext, entidades, migración (`dotnet ef migrations add AddAutomations --project src/Modules/Automations/Modules.Automations.Infrastructure --context AutomationsDbContext -o Persistence/Migrations`), cableado y harness.**
- [ ] **Step 4: Correr** — ArchitectureTests completo (incluye `CompositionRootTests`: con el módulo sin handlers todavía debe pasar) + Automations.IntegrationTests: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(automations): esqueleto del módulo, esquema y harness (spec 2026-10-21 §4.1, §4.2)"`

### Task 10: Permisos, políticas y roles

**Files:**
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:1120-1123` (rol `admin` suma `AutomationsPermissions.ChatbotRead` y `ChatbotManage`; `advisor`, `billing`, `cashier` no), `:1657` (dos `PermissionDefinition`: «Ver chatbots» / «Permite ver los chatbots, sus plantillas y las conexiones que atienden.», categoría `Automations`, riesgo `low`, `RequiredModules: []`; «Gestionar chatbots» / «Permite crear, editar, publicar, pausar y borrar chatbots y sus plantillas.», riesgo `medium`), `:1978` (dos `AddPolicy`)
- Modify: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs:170-183` (`PermissionDiscoveryFindsTheSixtyConstants`, `Assert.Equal(60, …)`), nueva `TheAutomationsPermissionsAreCoreAndGoOnlyToAdmin` (copia de `:325-348`)

- [ ] **Step 1: Cambiar las pruebas** (el conteo y la nueva prueba de roles).
- [ ] **Step 2: Correr y verificar que fallan** — Run: `dotnet test tests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"`. Expected: FAIL (58 ≠ 60; permisos ausentes en admin).
- [ ] **Step 3: Registrar definiciones, políticas y roles.**
- [ ] **Step 4: Correr y verificar que pasan** — mismo filtro: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(authorization): permisos automations.chatbot.read y manage, sólo admin (spec 2026-10-21 D-C10)"`

## Fase 3: constructor

### Task 11: Modelo del flujo y validador

**Files:**
- Create: `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowDefinition.cs` (records del Anexo A), `Flow/FlowJson.cs`, `Flow/FlowValidator.cs`, `Flow/FlowIssue.cs`, `Flow/FlowNodeKinds.cs`
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/FlowJsonTests.cs`, `FlowValidatorTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record FlowDefinition(int Schema, FlowStart Start, IReadOnlyList<FlowVariable> Variables, IReadOnlyList<FlowNode> Nodes);
  public sealed record FlowStart(string? FirstNodeId);
  public sealed record FlowVariable(string Name, string Type);                       // text | number | list | object | attachment
  public abstract record FlowNode(string Id, string Kind, string Title, IReadOnlyDictionary<string, string?> Outputs);
  public sealed record MessageNode(..., string Text, UrlButton? UrlButton) : FlowNode;   public sealed record UrlButton(string Label, string Url);
  public sealed record OpenQuestionNode(..., string Text, string SaveTo, QuestionValidation Validation, int? MaxAttempts) : FlowNode;
  public sealed record QuestionValidation(string Kind, string? Pattern, IReadOnlyList<string>? Keywords);   // none | digits | number | regex | keywords
  public sealed record ButtonsNode(..., string Text, IReadOnlyList<FlowButton> Buttons, string? SaveTo, string Fallback) : FlowNode;   public sealed record FlowButton(string Id, string Label);
  public sealed record ListNode(..., string Text, string ButtonLabel, ListOptions Options, string SaveTo, string Fallback) : FlowNode;
  public sealed record ListOptions(string Kind, IReadOnlyList<ListFixedRow>? Fixed, string? Collection, string? IdField, string? TitleField, string? DescriptionField, ListFixedRow? ExtraOption);   public sealed record ListFixedRow(string Id, string Title, string? Description);
  public sealed record AttachmentNode(..., string Text, string Accept, string SaveTo) : FlowNode;      // image | document | any
  public sealed record ConditionNode(..., string Match, IReadOnlyList<ConditionRule> Rules) : FlowNode;   public sealed record ConditionRule(string Variable, string Operator, string? Value);
  public sealed record BusinessHoursNode(...) : FlowNode;
  public sealed record HttpNode(..., Guid? ConnectionId, string Method, string Path, IReadOnlyList<KeyValue> Query, HttpBody? Body, int TimeoutSeconds, IReadOnlyList<int>? OkWhen, IReadOnlyList<HttpMapping> Mapping, JsonElement? SampleResponse) : FlowNode;
  public sealed record KeyValue(string Key, string Value);   public sealed record HttpBody(string Kind, string? Template, IReadOnlyList<KeyValue>? Fields, string? FileField, string? FileVariable);   public sealed record HttpMapping(string From, string To);
  public sealed record HandoffNode(..., string? Text) : FlowNode;   public sealed record ResolveNode(..., string? Text, Guid? ReasonId) : FlowNode;
  public static class FlowNodeKinds { public static readonly IReadOnlySet<string> All; public static IReadOnlyList<string> OutputsOf(FlowNode node); public static bool IsTerminal(string kind); }
  public static class FlowJson { public static FlowDefinition Parse(string json); public static string Serialize(FlowDefinition flow); }   // polimórfico por "kind"; desconocido → FlowFormatException(code "flow.unknown_kind")
  public sealed class FlowFormatException(string code, string message) : Exception(message) { public string Code { get; } = code; }
  public enum FlowIssueSeverity { Error, Warning }
  public sealed record FlowIssue(FlowIssueSeverity Severity, string Code, string? NodeId, string Message);
  public sealed record FlowValidationContext(IReadOnlySet<string> Channels, IReadOnlySet<Guid> ActiveExternalApis, IReadOnlySet<Guid> PausedExternalApis, bool AllConnectionsHaveBusinessHours);
  public static class FlowValidator { public static IReadOnlyList<FlowIssue> Validate(FlowDefinition flow, FlowValidationContext context); }
  ```
- `OutputsOf`: message/businessHours/condition/http fijos (`next`; `open`,`closed`; `yes`,`no`; `ok`,`error`); openQuestion `answered`,`invalid`; buttons = ids de botones + `other`; list `chosen`,`extra` (sólo con `ExtraOption`),`tooMany` (sólo `dynamic`),`other`; attachment `received`,`invalid`; handoff/resolve ninguno.
- Códigos de `Validate`: los de §4.3. `node.output_unconnected` es **error** salvo para `invalid`, `other` y `tooMany` (aviso `node.output_unconnected_optional`): D-M7 los cubre en tiempo de ejecución. Mensajes en español tuteando («La salida «no válido» de «Número de documento» no está conectada.»).

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// FlowJsonTests
[Fact] public void ParsesTheAnnexAFlow()                      // el JSON del Anexo A del spec, embebido como string: 9 nodos, HttpNode n3 con Mapping [("data[0]","cliente")]
[Fact] public void RoundTripsWithoutLoss()                    // Serialize(Parse(json)) y Parse de nuevo son iguales (record equality)
[Fact] public void AnUnknownKindThrowsFlowFormat()            // {"kind":"teleport"} → FlowFormatException code "flow.unknown_kind"
[Fact] public void OutputsOfAButtonsNodeAreItsButtonsPlusOther()
// FlowValidatorTests (un helper Flow(params FlowNode[]) y Ctx() con canal WhatsApp)
[Fact] public void AFlowWithoutFirstNodeFails()               // "start.no_first_node"
[Fact] public void AnUnconnectedNextIsAnError()               // message con next null → Error "node.output_unconnected"
[Fact] public void AnUnconnectedInvalidIsAWarning()           // openQuestion invalid null → Warning "node.output_unconnected_optional"
[Fact] public void AnUnknownTargetFails()                     // next → "nope" → "node.unknown_target"
[Fact] public void AnUnreachableNodeWarns()                   // "node.unreachable"
[Fact] public void FourButtonsFail()                          // "buttons.too_many"
[Fact] public void ALabelOfTwentyOneFails()                   // "buttons.label_too_long"
[Fact] public void ElevenFixedRowsFail()                      // "list.too_many_rows"
[Fact] public void ARowTitleOfTwentyFiveFails()               // "list.row_title_too_long"
[Fact] public void TextOverFourThousandNinetySixFails()       // "text.too_long"
[Fact] public void EmptyTextFails()                           // "text.empty"
[Fact] public void HttpWithoutConnectionFails()               // "http.connection_missing"
[Fact] public void HttpWithAPausedConnectionFails()           // "http.connection_paused"
[Fact] public void HttpWithInvalidJsonBodyFails()             // Body json "{ nope" → "http.body_not_json"
[Fact] public void HttpPathWithSchemeFails()                  // Path "https://x" → "http.path_invalid"
[Fact] public void ConditionWithoutRulesFails()               // "condition.no_rules"
[Fact] public void AVariableNeverSetWarns()                   // message "{cliente.nombre}" sin http/pregunta que lo escriba → Warning "variable.never_set"
[Fact] public void AnInvalidVariableNameFails()               // saveTo "1cedula" → "variable.name_invalid"
[Fact] public void ListOnInstagramFails()                     // Ctx con canal Instagram → "list.channel_not_supported" (Error)
[Fact] public void NoBusinessHoursWarns()                     // businessHours con AllConnectionsHaveBusinessHours=false → "business_hours.not_assigned"
[Fact] public void HttpWithoutSampleResponseWarns()           // "http.no_sample_response"
[Fact] public void TheAnnexAFlowHasOnlyWarnings()             // con conexiones activas → ningún Error
```

- [ ] **Step 2: Correr y verificar que fallan** — Run: `dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~Flow"`. Expected: FAIL por compilación.
- [ ] **Step 3: Implementar records, `FlowJson` (un `JsonConverter<FlowNode>` que lee `kind`), `FlowNodeKinds` y `FlowValidator`.** El JSON del Anexo A se guarda como `tests/.../Fixtures/annex-a.json` (EmbeddedResource) para reutilizarlo en las tareas 14, 15 y 17.
- [ ] **Step 4: Correr y verificar que pasan** — mismo filtro: PASS (27).
- [ ] **Step 5: Commit** — `git commit -m "feat(automations): modelo del flujo y validador (spec 2026-10-21 §4.3, Anexo A)"`

### Task 12: Agregado `Chatbot`

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Chatbot.cs`, `ChatbotVersion.cs`, `ChatbotConnection.cs`, `ChatbotEnums.cs` (creados vacíos en la tarea 9; acá el comportamiento)
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/ChatbotTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public enum ChatbotStatus { Draft, Active, Paused }   public enum ChatbotTrigger { NewConversation, Keyword, OutsideHours }
  public sealed class Chatbot
  {
      public static Chatbot Create(Guid tenantId, string name, string description, ChatbotTrigger trigger, IReadOnlyList<string> keywords, string draftFlowJson, string draftLayoutJson, Guid by, DateTimeOffset now);
      public void Rename(string name, string description, DateTimeOffset now);
      public void UpdateSettings(ChatbotTrigger trigger, IReadOnlyList<string> keywords, string restartKeyword, int maxAttempts, DateTimeOffset now);   // keywords ≤ 10, cada una ≤ 40, sin repetir (normalizadas); maxAttempts 1..5
      public void SaveDraft(string flowJson, string layoutJson, Guid by, DateTimeOffset now);
      public ChatbotVersion Publish(IReadOnlyList<FlowIssue> issues, Guid by, DateTimeOffset now);   // Error presente → AutomationsDomainException(ChatbotInvalid); number = Versions.Count + 1; Draft → Active; Paused se queda Paused
      public void Pause(DateTimeOffset now);    // exige Active; Draft → ChatbotNotPublished; Paused → ChatbotAlreadyPaused
      public void Resume(DateTimeOffset now);   // exige Paused; Active → ChatbotAlreadyActive
      public void SetConnections(IReadOnlyList<Guid> connectionIds, DateTimeOffset now);   // reemplaza; active = Status != Draft
      public bool HasDraftChanges { get; }      // PublishedVersion is null || DraftUpdatedAt > PublishedVersion.PublishedAt
      // propiedades: Id, TenantId, Name, Description, Status, Trigger, Keywords, RestartKeyword, MaxAttempts, DraftFlowJson, DraftLayoutJson, DraftUpdatedAt, DraftUpdatedBy, PublishedVersionId, Versions, Connections, CreatedAt, CreatedBy, UpdatedAt, Version (long, +1 en cada mutación)
  }
  ```
- Publicar marca `Active = true` en todas las `Connections`; pausar las deja (D-C13); `Create` normaliza `Name` con `Trim()` y rechaza vacío o > 80 (`AutomationsDomainException("automations.chatbot.name_invalid")`).

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
[Fact] public void CreateStartsAsDraftWithVersionOne()
[Fact] public void PublishWithAnErrorIssueThrowsChatbotInvalid()
[Fact] public void PublishCreatesVersionOneAndActivates()          // Number == 1, Status Active, Connections all Active, PublishedVersionId set, HasDraftChanges false
[Fact] public void PublishTwiceNumbersSequentially()                // 1, 2
[Fact] public void SaveDraftAfterPublishMarksDraftChanges()
[Fact] public void PauseRequiresActiveAndResumeRequiresPaused()      // tres aserciones con los códigos
[Fact] public void PublishWhilePausedStaysPaused()
[Fact] public void SetConnectionsOnADraftLeavesThemInactive()
[Fact] public void KeywordsAreNormalizedAndCapped()                  // ["Reiniciar", "reiniciar", " hola "] → ["reiniciar","hola"]; 11 → DomainException "automations.chatbot.keywords_too_many"
[Fact] public void EveryMutationBumpsVersion()
```

- [ ] **Step 2: Correr y verificar que fallan** — Run: `dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~ChatbotTests"`. Expected: FAIL.
- [ ] **Step 3: Implementar el agregado y ajustar el mapeo EF de la tarea 9 si cambió alguna propiedad** (owned `Connections`, `Versions` como colección de navegación; `Keywords` jsonb con `ValueConverter` + `ValueComparer` como `MessagingDbContext.cs:446-461`).
- [ ] **Step 4: Correr y verificar que pasan** — mismo filtro: PASS (10). `dotnet build Backend.slnx`: OK.
- [ ] **Step 5: Commit** — `git commit -m "feat(automations): agregado Chatbot con borrador, versiones y conexiones (spec 2026-10-21 D-C1, D-C5, D-C13)"`

### Task 13: Puertos, repositorio, unidad de trabajo y auditoría

**Files:**
- Create: `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs`, `AutomationsDtos.cs`
- Create: `src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/AutomationsRepositories.cs`, `AutomationsUnitOfWork.cs`, `AutomationsAuditRecorder.cs`, `ConversationRunQueries.cs`
- Modify: `AutomationsInfrastructureExtensions.cs` (registros scoped)
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/AutomationsPersistenceTests.cs`

**Interfaces:**
- Produces (Application):
  ```csharp
  public interface IChatbotRepository { Task<Chatbot?> FindAsync(Guid tenantId, Guid id, CancellationToken ct); Task<IReadOnlyList<Chatbot>> ListAsync(Guid tenantId, string? search, CancellationToken ct); Task<Chatbot?> FindPublishedByConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken ct); Task AddAsync(Chatbot chatbot, CancellationToken ct); void Remove(Chatbot chatbot); Task<bool> NameExistsAsync(Guid tenantId, string name, Guid? exceptId, CancellationToken ct); }
  public interface IChatbotTemplateRepository { Task<ChatbotTemplate?> FindAsync(Guid tenantId, Guid id, CancellationToken ct); Task<IReadOnlyList<ChatbotTemplate>> ListAsync(Guid tenantId, CancellationToken ct); Task AddAsync(ChatbotTemplate t, CancellationToken ct); void Remove(ChatbotTemplate t); }
  public interface IConversationRunQueries { Task<IReadOnlyDictionary<Guid, (int Handled, int HandedOff)>> TodayAsync(Guid tenantId, IReadOnlyCollection<Guid> chatbotIds, DateTimeOffset dayStartUtc, DateTimeOffset dayEndUtc, CancellationToken ct); Task<bool> HasLiveRunsAsync(Guid tenantId, Guid chatbotId, CancellationToken ct); }
  public interface IAutomationsUnitOfWork { Task SaveChangesAsync(CancellationToken ct); }   // 23505 IX_chatbots_tenant_name → ResourceConflictException(ChatbotNameTaken); IX_chatbot_connections_active → ResourceConflictException(ChatbotConnectionTaken); IX_chatbot_templates_tenant_name → ResourceConflictException("automations.template.name_taken"); DbUpdateConcurrencyException → RequestConcurrencyException("concurrency.conflict")
  public interface IAutomationsAuditRecorder { void Record(Guid tenantId, Guid actorId, bool bySystem, string action, string resourceType, Guid resourceId, DateTimeOffset at); }
  public static class AutomationsAuditActions { ChatbotCreated = "automations.chatbot.created", ChatbotUpdated, ChatbotDraftSaved, ChatbotPublished, ChatbotPaused, ChatbotResumed, ChatbotDeleted, ChatbotImported, TemplateCreated = "automations.template.created", TemplateDeleted; ResourceChatbot = "chatbot", ResourceTemplate = "chatbot_template" }
  ```
- DTOs de §4.4 en `AutomationsDtos.cs`: `ChatbotSummaryDto(Guid Id, string Name, string Description, string Status, string Trigger, IReadOnlyList<ChatbotConnectionDto> Connections, int? PublishedVersion, DateTimeOffset? PublishedAt, bool HasDraftChanges, ChatbotTodayDto Today, long Version)`, `ChatbotConnectionDto(Guid Id, string Name, string Channel, string Handle)`, `ChatbotTodayDto(int Handled, int HandedOff)`, `ChatbotEditorDto(ChatbotSummaryDto Chatbot, IReadOnlyList<string> Keywords, string RestartKeyword, int MaxAttempts, ChatbotDraftDto Draft, ChatbotPublishedDto? Published, IReadOnlyList<FlowIssueDto> Issues, IReadOnlyList<SelectableConnectionDto> Connections, IReadOnlyList<ExternalApiRefDto> ExternalApis, IReadOnlyList<string> Variables)`, `ChatbotDraftDto(JsonElement Flow, JsonElement Layout, DateTimeOffset UpdatedAt, Guid UpdatedBy)`, `ChatbotPublishedDto(int Number, DateTimeOffset At)`, `FlowIssueDto(string Severity, string Code, string? NodeId, string Message)`, `DraftSavedDto(long Version, DateTimeOffset UpdatedAt, IReadOnlyList<FlowIssueDto> Issues)`, `SelectableConnectionDto(Guid Id, string Name, string Channel, string Handle, string Status, ServedByDto? ServedBy, string? BusinessHoursName)`, `ServedByDto(Guid ChatbotId, string Name)`, `ExternalApiRefDto(Guid Id, string Name, string Status)`, `ChatbotListResponse(IReadOnlyList<ChatbotSummaryDto> Items)`.

- [ ] **Step 1: Escribir las pruebas que fallan** (harness; repositorio y UoW resueltos del host)

```csharp
[Fact] public async Task ADuplicatedNameBecomesNameTaken()            // AddAsync dos Chatbot "Ventas"/"ventas" → ResourceConflictException code automations.chatbot.name_taken
[Fact] public async Task TwoPublishedChatbotsOnOneConnectionBecomeConnectionTaken()
[Fact] public async Task AStaleVersionBecomesConcurrencyConflict()
[Fact] public async Task FindPublishedByConnectionIgnoresDrafts()
[Fact] public async Task TodayCountsRunsStartedTodayOnly()            // insertar runs por SQL: 2 hoy (1 HandedOff), 1 ayer → (2, 1)
[Fact] public async Task RecordWritesAnAuditEntryWithTheResourceType()
```

- [ ] **Step 2: Correr y verificar que fallan** — Run: `dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~AutomationsPersistenceTests"`. Expected: FAIL por compilación.
- [ ] **Step 3: Implementar puertos, repositorios (EF), UoW con traducción por nombre de índice (copia de `PipelineUnitOfWork.cs:10-40`), recorder (copia de `PipelineAuditRecorder.cs`) y consultas de corridas (SQL).**
- [ ] **Step 4: Correr y verificar que pasan** — PASS (6).
- [ ] **Step 5: Commit** — `git commit -m "feat(automations): repositorios, unidad de trabajo y auditoría del módulo (spec 2026-10-21 §4.6)"`

### Task 14: Endpoints de lectura, creación en blanco, edición y borrador

**Files:**
- Create: `src/Modules/Automations/Modules.Automations.Application/Chatbots.cs` (queries, commands, validators y handlers: `ListChatbotsQuery(Guid TenantId, string? Search)` → `ChatbotListResponse`; `GetChatbotQuery(Guid TenantId, Guid ChatbotId)` → `ChatbotEditorDto`; `CreateChatbotCommand(Guid TenantId, string Name, string Description, string Trigger, IReadOnlyList<string> Keywords, IReadOnlyList<Guid> ConnectionIds, ChatbotSource Source)` → `ChatbotEditorDto` (acá sólo `Source.Kind == "blank"`; `template` y `duplicate` en la tarea 17); `UpdateChatbotCommand(Guid TenantId, Guid ChatbotId, long Version, string Name, string Description, string Trigger, IReadOnlyList<string> Keywords, string RestartKeyword, int MaxAttempts, IReadOnlyList<Guid> ConnectionIds)`; `SaveChatbotDraftCommand(Guid TenantId, Guid ChatbotId, long Version, JsonElement Flow, JsonElement Layout)` → `DraftSavedDto`; `ValidateChatbotQuery(Guid TenantId, Guid ChatbotId)` → `IReadOnlyList<FlowIssueDto>`)
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs` — **declarar acá** `IAutomationConnections`, `IAutomationExternalApis` y `IAutomationBusinessHours` con las firmas de la tarea 16, y registrar en Bootstrapper adaptadores nulos (`NullAutomationConnections`, etc., que devuelven listas vacías) que la tarea 16 reemplaza por los reales.
- Create: `src/Modules/Automations/Modules.Automations.Application/ChatbotEditorBuilder.cs` (`Task<ChatbotEditorDto> BuildAsync(Chatbot chatbot, CancellationToken ct)`: resuelve conexiones por `IAutomationConnections` y APIs por `IAutomationExternalApis`, valida con `FlowValidator`, lista variables declaradas más las que escriben los nodos)
- Create: `src/Modules/Automations/Modules.Automations.Api/ChatbotEndpoints.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:745` (handlers y `AddValidatorsFromAssemblyContaining<CreateChatbotValidator>()`)
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/ChatbotsApiTests.cs`

**Interfaces:**
- Rutas (§4.4): `GET /chatbots?search=`, `POST /chatbots` (201), `GET /chatbots/{id}`, `PUT /chatbots/{id}` (If-Match), `PUT /chatbots/{id}/draft` (If-Match), `POST /chatbots/{id}/validate`. Requests: `CreateChatbotRequest(string Name, string Description, string Trigger, IReadOnlyList<string> Keywords, IReadOnlyList<Guid> ConnectionIds, ChatbotSourceRequest Source)`, `ChatbotSourceRequest(string Kind, string? TemplateId, Guid? ChatbotId, IReadOnlyDictionary<string,string>? Parameters)`, `UpdateChatbotRequest(...)`, `SaveDraftRequest(JsonElement Flow, JsonElement Layout)`.
- Validadores FluentValidation: `Name` 1..80, `Description` ≤ 300, `Trigger` ∈ enum, `Keywords` ≤ 10 × ≤ 40, `RestartKeyword` 1..40, `MaxAttempts` 1..5, `ConnectionIds` ≤ 10 sin repetir, `Flow` ≤ 512 KB serializado y parseable (`FlowFormatException` → `ValidationException` con la clave `flow` y el código como mensaje), `Layout` ≤ 256 KB.
- El chatbot en blanco: `flow = { schema: 1, start: { firstNodeId: null }, variables: [], nodes: [] }`, `layout = { positions: {} }`.
- `CreateChatbotHandler`: si alguna conexión es Instagram → `AutomationsDomainException(ChatbotChannelNotSupported)`; conexiones que no son del tenant → `ResourceNotFoundException("automations.connection.not_found")`.

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
[Fact] public async Task ListIsEmptyForANewTenant()                                 // 200 items []
[Fact] public async Task CreateBlankReturnsTheEditorWithAnEmptyFlowAndStartIssue()  // 201; chatbot.status Draft; draft.flow.nodes []; issues contiene start.no_first_node
[Fact] public async Task CreateWithAnInstagramConnectionIs422ChannelNotSupported()
[Fact] public async Task CreateWithoutTheModuleIs403ModuleNotEnabled()              // tenant con messaging pero sin automations → 403 tenancy.module_not_enabled
[Fact] public async Task CreateWithoutManageIs403()                                 // sólo read → 403 authorization.denied
[Fact] public async Task CreateWithADuplicatedNameIs409NameTaken()
[Fact] public async Task GetFromAnotherTenantIs403NotFound()                        // 403, nunca 404
[Fact] public async Task UpdateWithoutIfMatchIs428AndStaleIs412()
[Fact] public async Task SaveDraftStoresTheAnnexAFlowAndReportsOnlyWarnings()        // 200; issues sin Error; GET devuelve el mismo flow
[Fact] public async Task SaveDraftWithErrorsStillSaves()                            // flujo con next null → 200 con Error en issues, GET lo devuelve
[Fact] public async Task SaveDraftRejectsUnknownKindAndOversizedFlow()               // 422 validation.failed, errors tiene "flow" (dos casos)
[Fact] public async Task ValidateReturnsTheSameIssuesAsGet()
[Fact] public async Task ListShowsHasDraftChangesAndZeroToday()                      // today {0,0}, hasDraftChanges true
```

- [ ] **Step 2: Correr y verificar que fallan** — Run: `dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~ChatbotsApiTests"`. Expected: FAIL (404 en las rutas).
- [ ] **Step 3: Implementar handlers, validadores, builder, endpoints y registro.** `RequireVersion` como Messaging (428 `precondition.if_match_required`).
- [ ] **Step 4: Correr y verificar que pasan** — PASS (13). `dotnet test tests/ArchitectureTests --filter CompositionRootTests`: PASS (handlers registrados).
- [ ] **Step 5: Commit** — `git commit -m "feat(automations): endpoints de lista, creación, edición y borrador del chatbot (spec 2026-10-21 §4.4, D-C3)"`

### Task 15: Publicar, pausar, reanudar, duplicar y borrar

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/Chatbots.cs` (`PublishChatbotCommand(TenantId, ChatbotId, Version)`, `PauseChatbotCommand`, `ResumeChatbotCommand`, `DuplicateChatbotCommand(TenantId, ChatbotId)`, `DeleteChatbotCommand(TenantId, ChatbotId, Version)`)
- Modify: `src/Modules/Automations/Modules.Automations.Api/ChatbotEndpoints.cs`, `QepServiceCollectionExtensions.cs`
- Test: `ChatbotsApiTests.cs` (ampliar)

**Interfaces:**
- `POST /chatbots/{id}/publish` (If-Match): valida con el contexto real; errores → `AutomationsDomainException(ChatbotInvalid)` cuyo `Errors` lleva los `FlowIssueDto` (el `ApiExceptionHandler` los pone en `errors` como `{ "flow": ["code: message", …] }`); éxito → `ChatbotEditorDto`. 409 `connection_taken` sale de la UoW por el índice.
- `POST …/pause`, `…/resume` (If-Match) → `ChatbotEditorDto`. `POST …/duplicate` → 201, nombre «{nombre} (copia)», «(copia 2)», …, sin conexiones, `Draft`. `DELETE` (If-Match) → 204; `HasLiveRunsAsync` → 409 `automations.chatbot.has_live_runs`.

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
[Fact] public async Task PublishAnInvalidDraftIs422ChatbotInvalidWithTheIssues()     // 422 automations.chatbot.invalid, errors["flow"] no vacío
[Fact] public async Task PublishTheAnnexAFlowActivatesAndNumbersVersionOne()         // status Active, published.number 1, hasDraftChanges false
[Fact] public async Task PublishASecondChatbotOnTheSameConnectionIs409()             // automations.chatbot.connection_taken
[Fact] public async Task ConcurrentPublishOnTheSameConnectionLeavesOneActive()       // Task.WhenAll de dos publish → un 200 y un 409; SELECT count(*) WHERE active == 1
[Fact] public async Task PauseKeepsTheConnectionAndResumeWorks()                     // pausar; otro publish en la misma conexión → 409; resume → Active
[Fact] public async Task PauseADraftIs422NotPublished()
[Fact] public async Task DuplicateNumbersTheCopy()                                   // "Servicio técnico (copia)", luego "(copia 2)"; sin conexiones; Draft
[Fact] public async Task DeleteWithLiveRunsIs409()                                   // INSERT run 'Waiting' por SQL → 409 automations.chatbot.has_live_runs
[Fact] public async Task DeleteFreesTheConnection()                                  // después del DELETE, publish de otro en esa conexión → 200
```

- [ ] **Step 2: Correr y verificar que fallan** — mismo filtro: FAIL.
- [ ] **Step 3: Implementar.** La auditoría (`ChatbotPublished`, `ChatbotPaused`, …) en la misma `SaveChangesAsync`.
- [ ] **Step 4: Correr y verificar que pasan** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(automations): publicar, pausar, reanudar, duplicar y borrar chatbots (spec 2026-10-21 D-C4, D-C5, D-C13)"`

### Task 16: Puertos a Messaging e Integrations, adaptadores y `GET /connections`

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs` (`IAutomationConnections`, `IAutomationExternalApis`, `IAutomationBusinessHours`)
- Create: `src/Bootstrapper/AutomationConnections.cs`, `src/Bootstrapper/AutomationExternalApis.cs`, `src/Bootstrapper/AutomationBusinessHours.cs`, `src/Bootstrapper/AutomationsChatbotNames.cs` (reemplaza al nulo de la tarea 4: lee `AutomationsDbContext.Chatbots` por tenant e ids)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:880` (registros), `tests/Bootstrapper/Bootstrapper.UnitTests/AutomationAdaptersTests.cs` (nuevo)
- Create: `src/Modules/Automations/Modules.Automations.Application/Connections.cs` (`ListSelectableConnectionsQuery(Guid TenantId)` → `IReadOnlyList<SelectableConnectionDto>`), ruta `GET /connections` en un `ConnectionEndpoints.cs`
- Modify: harness `SeedExternalApiConnectionAsync(factory, tenantId, name, status)` — inserta por `IntegrationsDbContext` una `IntegrationConnection` con `ProviderKey = "external-api"` (el proveedor existe desde el plan 2; hasta entonces la prueba de `externalApis` queda `Skip = "proveedor external-api en el plan 2"`)
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/ConnectionsApiTests.cs`, `MessageAuthorshipApiTests.cs` (de la tarea 4: ahora el nombre real)

**Interfaces:**
- Produces:
  ```csharp
  public sealed record AutomationConnectionRef(Guid Id, string Name, string Channel, string Handle, string Status);   // Channel "WhatsApp" | "Instagram"; Handle = displayPhoneNumber o @username
  public interface IAutomationConnections { Task<IReadOnlyList<AutomationConnectionRef>> ListAsync(Guid tenantId, CancellationToken ct); }   // sobre IMessagingConnectionDirectory.ListAllAsync + ListActiveAsync (para el handle)
  public sealed record ExternalApiRef(Guid Id, string Name, string Status);
  public interface IAutomationExternalApis { Task<IReadOnlyList<ExternalApiRef>> ListAsync(Guid tenantId, CancellationToken ct); }   // IIntegrationConnections.ListByProviderAsync(tenantId, "external-api")
  public interface IAutomationBusinessHours { Task<IReadOnlyDictionary<Guid, string?>> ScheduleNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> connectionIds, CancellationToken ct); }   // IBusinessHours.GetStatusesAsync → ScheduleName
  ```
- `GET /connections` (read): cada conexión del tenant con `servedBy` = el chatbot publicado (activo o pausado) que la tiene, y `businessHoursName`.

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// Bootstrapper.UnitTests (dobles de IMessagingConnectionDirectory, IIntegrationConnections, IBusinessHours)
[Fact] public async Task ListsWhatsAppAndInstagramWithHandles()
[Fact] public async Task ChatbotNamesResolveFromTheAutomationsContext()   // sqlite/in-memory o harness: con ids desconocidos devuelve sólo los que existen
// ConnectionsApiTests (harness con SeedWhatsAppConnectionAsync y SeedInstagramConnectionAsync)
[Fact] public async Task ListsTheTenantConnectionsWithServedByAndBusinessHours()   // publicar un chatbot sobre la WhatsApp → servedBy.name; Instagram viene con channel Instagram y servedBy null
[Fact] public async Task TheEditorListsTheSameConnections()                        // GET /chatbots/{id}.connections
// MessageAuthorshipApiTests
[Fact] public async Task TheThreadShowsTheRealChatbotName()                        // sent_by_chatbot_id de un chatbot creado → "Servicio técnico"
```

- [ ] **Step 2: Correr y verificar que fallan** — Run: `dotnet test tests/Bootstrapper --filter "FullyQualifiedName~AutomationAdaptersTests"` y el filtro `ConnectionsApiTests`. Expected: FAIL.
- [ ] **Step 3: Implementar puertos, adaptadores (copiar la forma de `QuotationsWhatsAppChannelDirectory.cs:13`), la query y la ruta; reemplazar el nulo de `IAutomationsChatbotNames`.**
- [ ] **Step 4: Correr** — Bootstrapper.UnitTests, Automations.IntegrationTests, Messaging.IntegrationTests con filtro `Authorship`, ArchitectureTests: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(automations): puertos a Mensajería e Integraciones y GET /connections (spec 2026-10-21 §5.3)"`

### Task 17: Plantillas del sistema y del tenant; crear desde plantilla o duplicado

**Files:**
- Create: `src/Modules/Automations/Modules.Automations.Infrastructure/Seed/Templates/welcome-menu-handoff.json`, `Seed/Templates/outside-hours.json` (`EmbeddedResource Include="Seed\Templates\*.json"` en el csproj), `Seed/SystemTemplateCatalog.cs`
- Create: `src/Modules/Automations/Modules.Automations.Application/Templates.cs` (`ISystemTemplateCatalog { IReadOnlyList<SystemTemplate> All; SystemTemplate? Find(string id); }`, `SystemTemplate(string Id, string Name, string Category, string Description, IReadOnlyList<string> Channels, string? Requirement, IReadOnlyList<TemplateParameter> Parameters, IReadOnlyList<string> Preview, string FlowJson, string LayoutJson)`, `TemplateParameter(string Key, string Label, string Kind, IReadOnlyList<string>? Options, string DefaultValue)`, `ListTemplatesQuery(TenantId)` → `TemplateCatalogDto(IReadOnlyList<TemplateDto> System, IReadOnlyList<TemplateDto> Mine)`, `SaveChatbotAsTemplateCommand(TenantId, ChatbotId, Name, Category, Description)` → `TemplateDto` (201), `DeleteTemplateCommand(TenantId, TemplateId)`, `TemplateParameters.Apply(string flowJson, IReadOnlyDictionary<string,string> values)` reemplaza `{param.<key>}` sólo en campos de texto (D-C14))
- Modify: `Chatbots.cs` (`CreateChatbotCommand` con `Source.Kind == "template"` (id del sistema o Guid del tenant) y `"duplicate"`), `Modules.Automations.Domain/ChatbotTemplate.cs` (entidad: `Create(tenantId, name, category, description, flowJson, layoutJson, by, now)`)
- Create: `src/Modules/Automations/Modules.Automations.Api/TemplateEndpoints.cs` (`GET /templates`, `DELETE /templates/{id}`, `POST /chatbots/{id}/save-as-template`)
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/SystemTemplatesTests.cs`, `TemplateParametersTests.cs`; `tests/Modules/Automations/Modules.Automations.IntegrationTests/TemplatesApiTests.cs`

**Interfaces:**
- `TemplateDto(string Id, string Name, string Category, string Description, int BlockCount, IReadOnlyList<string> Channels, string? Requirement, bool OwnedByTenant, IReadOnlyList<string> Preview, IReadOnlyList<TemplateParameterDto> Parameters)`; `TemplateParameterDto(string Key, string Label, string Kind, IReadOnlyList<string>? Options, string DefaultValue)`. Categorías: `Support | Sales | Orders | TechnicalService` (las del frontend).
- Plantillas del sistema (D-C8, §4.5): «Menú de bienvenida con traspaso» (`welcome-menu-handoff`, `Support`, parámetros `empresa` y `opciones`? no: parámetros `greeting` (texto) y `option1`, `option2` (texto); botones → mensaje → handoff) y «Fuera de horario» (`outside-hours`, `Support`, parámetro `closedMessage`; businessHours → open: handoff; closed: message + resolve). Ambas sólo `WhatsApp`, `requirement` nulo y «usa el horario de la conexión» respectivamente.

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// unit
[Fact] public void EverySystemTemplateParsesAndValidatesWithoutErrors()   // FlowValidator sobre cada FlowJson con Ctx WhatsApp + horario → sin Error
[Fact] public void SystemTemplatesHaveStableIds()                         // ["welcome-menu-handoff", "outside-hours"]
[Fact] public void ApplyReplacesParamsOnlyInTextFields()                  // "{param.greeting}" en text y label sí; un id de nodo "{param.x}" no
// api
[Fact] public async Task TheCatalogListsSystemAndTenantTemplates()
[Fact] public async Task CreateFromASystemTemplateAppliesParameters()      // Source {kind:"template", templateId:"welcome-menu-handoff", parameters:{greeting:"Hola, soy Clasic"}} → draft.flow contiene "Hola, soy Clasic"; status Draft; sin conexiones ocupadas
[Fact] public async Task CreateFromAnUnknownTemplateIs404()
[Fact] public async Task SaveAsTemplateAndCreateFromIt()                   // POST save-as-template → 201 ownedByTenant true; crear desde ese Guid → flujo igual
[Fact] public async Task DuplicateAsSourceCopiesTheDraft()                 // Source {kind:"duplicate", chatbotId} (equivalente a POST /duplicate pero con nombre propio)
[Fact] public async Task DeleteASystemTemplateIs404()
```

- [ ] **Step 2: Correr y verificar que fallan** — filtros `SystemTemplatesTests`, `TemplateParametersTests`, `TemplatesApiTests`: FAIL.
- [ ] **Step 3: Escribir los dos JSON (flujo válido según el Anexo A y la tarea 11), el catálogo embebido (`GetManifestResourceStream`, como `GeographySeeder.OpenResource`), `Apply`, los handlers y las rutas.**
- [ ] **Step 4: Correr y verificar que pasan** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(automations): plantillas del sistema embebidas y plantillas del tenant (spec 2026-10-21 D-C8, D-C14)"`

### Task 18: Exportar e importar

**Files:**
- Modify: `Chatbots.cs` (`ExportChatbotQuery(TenantId, ChatbotId)` → `ChatbotExportDto`; `ImportChatbotCommand(TenantId, ImportChatbotRequest)` → `ChatbotEditorDto` 201), `ChatbotEndpoints.cs` (`GET /chatbots/{id}/export`, `POST /chatbots/import`)
- Test: `ChatbotsApiTests.cs` (ampliar)

**Interfaces:**
- `ChatbotExportDto(int Schema, string Name, string Description, string Trigger, IReadOnlyList<string> Keywords, string RestartKeyword, int MaxAttempts, JsonElement Flow, JsonElement Layout, IReadOnlyList<ExternalApiRefDto> ExternalApis, DateTimeOffset ExportedAt)`; `ImportChatbotRequest(string? Name, ChatbotExportDto Export, IReadOnlyDictionary<Guid, Guid>? ConnectionMap)`. Importar crea un `Draft` sin conexiones; `ConnectionMap` reescribe `connectionId` de los `HttpNode` (D-C15); nombre repetido → 409 `name_taken`; `Name` del request pisa al del export.

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
[Fact] public async Task ExportCarriesTheDraftAndTheExternalApis()
[Fact] public async Task ImportRecreatesADraftWithTheSameFlow()           // exportar el Anexo A → importar con Name "Copia" → GET draft.flow igual
[Fact] public async Task ImportRemapsHttpConnections()                     // ConnectionMap {old: new} → los HttpNode traen new; sin mapa quedan old y issues tiene http.connection_missing
[Fact] public async Task ImportWithABrokenFlowIs422()                      // kind desconocido → 422 validation.failed errors["export.flow"]
```

- [ ] **Step 2: Correr y verificar que fallan** — FAIL.
- [ ] **Step 3: Implementar.** Auditoría `ChatbotImported`.
- [ ] **Step 4: Correr y verificar que pasan** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(automations): exportar e importar chatbots con mapa de conexiones (spec 2026-10-21 D-C9, D-C15)"`

### Task 19: Auditoría del borrador con tope y leak de secretos

**Files:**
- Modify: `Chatbots.cs` (`SaveChatbotDraftHandler` registra `ChatbotDraftSaved` sólo si la última entrada `draft_saved` del chatbot tiene más de 60 s; consulta por `IAutomationsAuditQueries.LastDraftSavedAtAsync(tenantId, chatbotId)` nuevo en `AutomationsPorts.cs`, implementado sobre `audit.entries`)
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/AutomationsAuditTests.cs`, `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ConnectionSecretLeakTests.cs` (sin cambios aún: el proveedor `external-api` llega en el plan 2; dejar anotado en el reporte)

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
[Fact] public async Task CreateUpdatePublishPauseAndDeleteAreAudited()   // seis acciones, resource_type "chatbot", actor = el miembro
[Fact] public async Task DraftSavesAreAuditedAtMostOncePerMinute()       // tres PUT /draft seguidos → 1 entrada; con TestClock +61 s → 2
```

- [ ] **Step 2: Correr y verificar que fallan** — FAIL.
- [ ] **Step 3: Implementar.**
- [ ] **Step 4: Correr y verificar que pasan** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(automations): auditoría del chatbot con tope por minuto para el borrador (spec 2026-10-21 §4.6)"`

### Task 20: Verificación final, documentación y merge

**Files:**
- Modify: `README.md` (sección del módulo Automations: rutas, permisos, esquema; una fila en «Módulos por tenant»), `CLAUDE.md` (gotcha: «`Messaging:Automation:Enqueue` queda apagado hasta que el motor exista; con el flag prendido y sin worker la cola crece sin que nadie la lea»), `docs/estado.md` **sólo al mergear en `develop`** (fila «Automatizaciones … spec 4» → «Backend 1 construido (`<sha>`)» y una línea en Registro).

- [ ] **Step 1: Correr todo lo tocado** — `dotnet test tests/Modules/Messaging`, `tests/Modules/Tenancy`, `tests/Modules/Automations`, `tests/Bootstrapper`, `tests/ArchitectureTests`. Expected: PASS en todos; pegar el resumen de cada corrida en el reporte.
- [ ] **Step 2: `dotnet build Backend.slnx -warnaserror`** — Expected: sin advertencias nuevas.
- [ ] **Step 3: Revisar la rama contra el spec §4, §6 y las decisiones D-C*, D-I*** — cada endpoint de §4.4 salvo los de simulación y `GET /runs` (plan 2) existe y tiene prueba. Quedan para el plan 2, a propósito: `GET /messaging/conversations/{id}/automation` y su puerto `IConversationAutomationDetails` (§5.5), el proveedor `external-api` (§7), el motor y sus puertos (`IAutomationConversations`, `IAutomationMessenger`, `IAutomationMedia`), el simulador (§8).
- [ ] **Step 4: Commit de docs** — `git commit -m "docs(automations): README y CLAUDE.md del constructor de chatbots"`
- [ ] **Step 5: Merge** — `git branch --show-current` (debe decir `feature/automatizaciones-constructor-backend`); `git checkout develop`; `git merge --ff-only feature/automatizaciones-constructor-backend`; actualizar `docs/estado.md` y `git commit -m "docs(estado): backend 1 del constructor de chatbots en develop"`. Nunca `main`, nunca push.

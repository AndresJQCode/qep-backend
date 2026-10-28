# Gestor de plantillas de WhatsApp (backend) — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el admin de un tenant cree, vea, sincronice y borre las plantillas de WhatsApp de cada conexión `whatsapp-cloud` desde QEP, con un espejo local de su estado, y que QEP aprovisione por conexión la plantilla de cotización con encabezado de documento.

**Architecture:** Un espejo local (`messaging.message_templates` + `messaging.template_mirror_syncs`) alimentado por tres escritores: la creación (inserta `PENDING`), el webhook `message_template_status_update` (cambia estado) y el refresco contra `GET /{waba}/message_templates` (upsert por `meta_template_id`, borra lo que Meta ya no lista). El catálogo de envío (`IMessageTemplates`) pasa a leer del espejo y el `IMemoryCache` de 5 minutos desaparece. Todo lo nuevo vive en Messaging: dominio (`MessageTemplate`), aplicación (comandos, `TemplateMirrorSync`, `TemplateComponentsBuilder`, `TemplateGraphErrors`, `QuotationTemplateDefinition`), infraestructura (repositorio, llamadas nuevas en `WhatsAppCloudClient`, parser y procesador del webhook) y API (`TemplateManagementEndpoints`). Bootstrapper gana el permiso `messaging.template.manage` en sus tres lugares.

**Tech Stack:** .NET 10, EF Core + Npgsql (schema `messaging`), FluentValidation, System.Text.Json (`JsonObject`), xUnit v3 con `FakeMetaGraphHandler` y Testcontainers (Postgres 18).

**Spec:** `docs/superpowers/specs/2026-10-14-gestor-de-plantillas-whatsapp-design.md` (D-T1…D-T15, §3 supuestos sobre Meta, §4 backend, §5 lo que queda para el consumidor Quotations).

**Rama y worktree:** `feature/gestor-de-plantillas` desde `develop` `56aeb105`, en el worktree `.claude/worktrees/gestor-de-plantillas`. **Todo comando de este plan se corre desde la raíz de ese worktree.** Antes de cada commit: `git branch --show-current` tiene que decir `feature/gestor-de-plantillas`.

## Global Constraints

- **TDD obligatorio** (CLAUDE.md): cada task escribe la prueba, la ve fallar con el texto esperado, implementa y la ve pasar. La evidencia literal (salida de `dotnet test`) se conserva en el reporte del paso.
- **Commits** en conventional commits, en español, **sin atribución de IA** (CLAUDE.md pisa el recordatorio del harness).
- **Textos al usuario en español colombiano, tuteando** (mensajes de validación). Identificadores, códigos de error y nombres de enum no se traducen. Los `message` de `MessagingDomainException` siguen en inglés, como el resto del módulo, con **una** excepción: `messaging.template.rejected_by_meta` lleva en el `message` el texto que la pantalla muestra («WhatsApp no aceptó la plantilla: …» más el `error_user_msg` de Meta), porque el frontend no tiene diccionario para un rechazo arbitrario.
- **Messaging no referencia Integrations ni EF/Npgsql desde Application** (`MessagingLayerTests`). `TemplateGraphErrors`, `TemplateMirrorSync`, `MirroredTemplateCatalog` y `TemplateComponentsBuilder` van en `Modules.Messaging.Application` sin paquetes nuevos.
- **Un permiso nuevo necesita tres lugares** en `src/Bootstrapper/QepServiceCollectionExtensions.cs`: el rol de sistema `admin`, su `PermissionDefinition` y su política en `AddAuthorization`. Sin la política el síntoma es 500, no 403.
- **Nunca imprimir el token** de la conexión: ni en logs, ni en `ToString`, ni en mensajes de excepción. `MessagingTemplateSource.ToString` ya lo oculta.
- **403, nunca 404,** para tenant o conexión ajenos: `messaging.connection_unavailable` (422) viene de `ResolveTemplateSourceAsync` devolviendo `null`.
- **Ningún status HTTP se arma a mano:** `MessagingDomainException` → 422, `ResourceNotFoundException` → 404, `RequestForbiddenException` → 403, `ValidationException` → 422 `validation.failed` con `errors`. Este plan no toca `src/Api/ApiExceptionHandler.cs`.
- **Migraciones con el factory de diseño**, nunca `--startup-project` (CLAUDE.md). Revisar el `Up` a ojo: si toca otra tabla que las dos nuevas, detenerse.
- **Anchos de columna** (spec §4.1): `meta_template_id` 64, `name` 512, `language` 16, `category` 32, `parameter_format` 16, `status` 32, `status_reason` 64, `kind` 16. Lo que viene de Meta se recorta a esos anchos antes de guardarse.
- **Los fixtures de `message_templates` en pruebas llevan `id`:** el espejo indexa por `meta_template_id` y un elemento sin `id` se salta. Los cuatro `Catalog` existentes (CampaignApiTests, CampaignDispatchTests, SendTemplateApiTests, StartConversationApiTests) se actualizan en la Task 5.
- **Verificación final** (README § Verificación): `dotnet restore --locked-mode`, `dotnet format --verify-no-changes`, `dotnet build --no-restore`, `dotnet test --no-build`.

## Review Focus

Lo que el spec implica y ninguna prueba cubría al primer borrador del plan; cada línea ya tiene su prueba en la task que la posee.

1. **Un `id` numérico en el webhook.** Meta manda `message_template_id` como número JSON, no como string; un parser que sólo lea strings dejaría todos los cambios de estado en `UnknownChange`. → Task 10, `WebhookPayloadParserTemplateTests.AMessageTemplateIdThatComesAsANumberIsRead`.
2. **`reason: "NONE"` en una aprobación.** Guardarlo literal haría que la pantalla muestre «NONE» como motivo de una plantilla aprobada. → Task 1, `MessageTemplateTests.ApplyStatusDropsANoneReason`.
3. **Dos plantillas con el mismo `id` en el listado de Meta** (una paginación que repite). El upsert no puede insertar dos veces. → Task 5, `TemplateMirrorSyncTests.ADuplicatedIdInTheListingIsTakenOnce`.
4. **Variables con mayúsculas o espacios en el cuerpo (`{{ Nombre }}`).** Meta las rechaza; el validador tiene que marcarlas en `body` en vez de dejarlas pasar hasta Meta. → Task 7, `CreateTemplateCommandValidatorTests.AVariableWithUppercaseOrSpacesIsRejectedInBody`.
5. **Borrar la plantilla de cotización mientras una campaña la usa.** La regla de campañas aplica también a `kind = Quotation`; sin esa prueba, un refactor del guard podría exceptuarla. → Task 9, `TemplateManagementDeleteApiTests.DeletingTheQuotationTemplateUsedByAScheduledCampaignIs422`.

## Estructura de archivos

**Domain** (`src/Modules/Messaging/Modules.Messaging.Domain/`)
- Create `MessageTemplate.cs`: `MessageTemplate`, `MessageTemplateKind`, `MessageTemplateStatuses`, `MessageTemplateName`.
- Modify `MessagingErrorCodes.cs`: los códigos `messaging.template.*` nuevos.

**Application** (`src/Modules/Messaging/Modules.Messaging.Application/`)
- Modify `MessagingPermissions.cs` (`TemplateManage`), `IMessagingAuditRecorder.cs` (`RecordTemplate` y acciones), `IWhatsAppCloudClient.cs` (`MessagingGraphFailure` con `UserTitle`/`UserMessage`; `CreatedTemplate`, `DeletedTemplate`; tres métodos), `MessageTemplates.cs` (`TemplateCatalog.FromStored`).
- Create `TemplateManagementDtos.cs`, `TemplateManagementPorts.cs`, `TemplateGraphErrors.cs`, `TemplateMirrorSync.cs` (`TemplateMirrorSync`, `MirroredTemplateCatalog`), `TemplateComponentsBuilder.cs`, `TemplateManagement.cs` (reader, mapping, consultas, comandos y handlers), `QuotationTemplateProvisioning.cs` (`QuotationTemplateDefinition`, `MinimalPdf`, el comando de aprovisionar).

**Infrastructure** (`src/Modules/Messaging/Modules.Messaging.Infrastructure/`)
- Modify `Persistence/MessagingDbContext.cs`, `Persistence/MessagingUnitOfWork.cs`, `Persistence/MessagingAuditRecorder.cs`, `Meta/WhatsAppCloudClient.cs`, `Options/MessagingMetaOptions.cs` (`AppId`), `Webhook/WebhookPayload.cs`, `Webhook/WebhookPayloadParser.cs`, `Webhook/WebhookDeliveryProcessor.cs`, `MessagingInfrastructureExtensions.cs`.
- Create `Persistence/TemplateMirrorSyncRecord.cs`, `Persistence/MessageTemplateRepository.cs`, `Persistence/QuotationTemplateReadiness.cs`, `Persistence/Migrations/<ts>_AddMessageTemplates.cs` (generada).
- Delete `Meta/MessageTemplateCatalog.cs`.

**Api** (`src/Modules/Messaging/Modules.Messaging.Api/`): Create `TemplateManagementEndpoints.cs`; Modify `MessagingEndpoints.cs` (`group.MapTemplateManagementEndpoints();`).

**Bootstrapper**: Modify `QepServiceCollectionExtensions.cs` en los tres lugares del permiso.

**Tests**
- `tests/Modules/Messaging/Modules.Messaging.UnitTests/`: Create `MessageTemplateTests.cs`, `TemplateCatalogFromStoredTests.cs`, `WhatsAppCloudClientTemplateManagementTests.cs`, `TemplateGraphErrorsTests.cs`, `TemplateMirrorSyncTests.cs`, `TemplateComponentsBuilderTests.cs`, `CreateTemplateCommandValidatorTests.cs`, `QuotationTemplateDefinitionTests.cs`, `WebhookPayloadParserTemplateTests.cs`. Modify `MessagingTestBed.cs` (`FakeWhatsAppClient`), `TemplateTestDoubles.cs` (`FakeMessageTemplateRepository`). Delete `MessageTemplateCatalogTests.cs`.
- `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/`: Create `MessageTemplatePersistenceTests.cs`, `TemplateManagementApiTests.cs`, `TemplateManagementCreateApiTests.cs`, `QuotationTemplateProvisioningApiTests.cs`, `TemplateManagementDeleteApiTests.cs`, `TemplateStatusWebhookTests.cs`. Modify `MessagingApiHarness.cs`, `TemplateCatalogApiTests.cs`, los cuatro `Catalog` y `MetaPayloads.cs`.

**Docs**: Modify `README.md` (§ Mensajería), `docs/superpowers/plans/2026-10-09-mensajeria-whatsapp-handoff-meta.md` (paso 5 y nota a confirmar).

---

### Task 1: Dominio — `MessageTemplate`, estados, nombre reservado y códigos

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Domain/MessageTemplate.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessageTemplateTests.cs`

**Interfaces:**
- Produces: `MessageTemplate.Pending(Guid tenantId, Guid connectionId, string metaTemplateId, string name, string language, string category, string parameterFormat, string? status, string componentsJson, MessageTemplateKind kind, Guid? createdByMemberId, DateTimeOffset now)`, `MessageTemplate.Mirrored(Guid tenantId, Guid connectionId, string metaTemplateId, string name, string language, string category, string parameterFormat, string? status, string? statusReason, string componentsJson, DateTimeOffset now)`, `ApplyStatus(string? status, string? reason, DateTimeOffset now)`, `SyncFrom(string? status, string? reason, string category, string parameterFormat, string componentsJson, DateTimeOffset now)`, `AdoptAsQuotation(DateTimeOffset now)`, `IsApproved`, `IsLive`, propiedades `Id, TenantId, ConnectionId, MetaTemplateId, Name, Language, Category, ParameterFormat, Status, StatusReason, ComponentsJson, Kind, CreatedByMemberId, CreatedAt, UpdatedAt, SyncedAt`; `MessageTemplateStatuses.{Pending,Approved,Rejected,Paused,Disabled,InAppeal,PendingDeletion,LimitExceeded,Unknown,MaxLength,Normalize,IsLive}`; `MessageTemplateName.{IsValid,IsReserved,QuotationName,QuotationPrefix,MaxLength}`; `MessageTemplateKind { Free, Quotation, External }`; `MessageTemplate.{MetaTemplateIdMaxLength,LanguageMaxLength,CategoryMaxLength,ParameterFormatMaxLength,StatusReasonMaxLength}`.

- [ ] **Step 1: Línea base del worktree.** Desde la raíz del worktree:

```bash
git branch --show-current
dotnet build --no-restore 2>&1 | tail -3
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --no-build 2>&1 | tail -3
```

Esperado: rama `feature/gestor-de-plantillas`, build sin errores, unitarias de Messaging en verde. Si el build falla por `--no-restore`, correr `dotnet restore --locked-mode` una vez. Si algo falla antes de tocar nada, detenerse y reportarlo: una línea base sucia vuelve ambigua cada falla posterior.

- [ ] **Step 2: Escribir las pruebas del dominio**

```csharp
// tests/Modules/Messaging/Modules.Messaging.UnitTests/MessageTemplateTests.cs
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-14 §4.2: el espejo de una plantilla de Meta. Pending/Mirrored, ApplyStatus (mayúsculas, «NONE» = sin
/// motivo, recorte al ancho de la columna), SyncFrom conserva kind, AdoptAsQuotation sólo desde External, IsLive y las reglas del nombre.</summary>
public sealed class MessageTemplateTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ConnectionId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 15, 0, 0, TimeSpan.Zero);

    private static MessageTemplate Pending(MessageTemplateKind kind = MessageTemplateKind.Free, string name = "promo_octubre", string? status = "pending") =>
        MessageTemplate.Pending(TenantId, ConnectionId, "900", name, "es", "MARKETING", "NAMED", status, "[]", kind, Guid.CreateVersion7(), Now);

    private static MessageTemplate External(string name = "externa", string? status = "APPROVED", string? reason = null) =>
        MessageTemplate.Mirrored(TenantId, ConnectionId, "901", name, "es", "UTILITY", "POSITIONAL", status, reason, "[]", Now);

    [Fact]
    public void PendingNormalizesTheStatusAndKeepsTheKind()
    {
        var template = Pending(MessageTemplateKind.Quotation, MessageTemplateName.QuotationName, " pending ");

        Assert.Equal(MessageTemplateStatuses.Pending, template.Status);
        Assert.Equal(MessageTemplateKind.Quotation, template.Kind);
        Assert.Null(template.StatusReason);
        Assert.Null(template.SyncedAt);
        Assert.True(template.IsLive);
        Assert.False(template.IsApproved);
        Assert.Equal((Now, Now), (template.CreatedAt, template.UpdatedAt));
    }

    [Fact]
    public void PendingWithTheExternalKindIsAProgrammingError() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Pending(MessageTemplateKind.External));

    [Fact]
    public void AFreeTemplateCannotTakeTheReservedQuotationName()
    {
        var exception = Assert.Throws<MessagingDomainException>(() => Pending(MessageTemplateKind.Free, MessageTemplateName.QuotationName));
        Assert.Equal(MessagingErrorCodes.TemplateNameReserved, exception.Code);
    }

    [Fact]
    public void AFreeTemplateWithAnInvalidNameIsRejected()
    {
        var exception = Assert.Throws<MessagingDomainException>(() => Pending(MessageTemplateKind.Free, "Promo Octubre"));
        Assert.Equal(MessagingErrorCodes.TemplateNameInvalid, exception.Code);
    }

    [Fact]
    public void MirroredIsExternalAlreadySyncedAndTakesMetaNamesAsTheyCome()
    {
        var template = External("Nombre-Con-Mayúsculas", "approved", "none");

        Assert.Equal(MessageTemplateKind.External, template.Kind);
        Assert.Equal(MessageTemplateStatuses.Approved, template.Status);
        Assert.Null(template.StatusReason);
        Assert.Equal(Now, template.SyncedAt);
        Assert.Null(template.CreatedByMemberId);
    }

    [Fact]
    public void ApplyStatusUppercasesAndKeepsTheReason()
    {
        var template = Pending();
        var later = Now.AddMinutes(5);

        template.ApplyStatus("rejected", "INVALID_FORMAT", later);

        Assert.Equal((MessageTemplateStatuses.Rejected, "INVALID_FORMAT", later), (template.Status, template.StatusReason, template.UpdatedAt));
        Assert.False(template.IsLive);
    }

    // Review Focus 2: Meta manda reason = "NONE" en una aprobación; no es un motivo.
    [Fact]
    public void ApplyStatusDropsANoneReason()
    {
        var template = Pending();
        template.ApplyStatus("REJECTED", "SCAM", Now);

        template.ApplyStatus("APPROVED", "NONE", Now.AddMinutes(1));

        Assert.Null(template.StatusReason);
        Assert.True(template.IsApproved);
    }

    [Fact]
    public void ApplyStatusTruncatesWhatDoesNotFitTheColumns()
    {
        var template = Pending();

        template.ApplyStatus(new string('x', 40), new string('r', 80), Now);

        Assert.Equal(32, template.Status.Length);
        Assert.Equal(64, template.StatusReason!.Length);
    }

    [Fact]
    public void AnEmptyStatusIsUnknownNotAnException()
    {
        var template = Pending(status: null);
        Assert.Equal(MessageTemplateStatuses.Unknown, template.Status);
    }

    [Fact]
    public void SyncFromKeepsTheKindAndStampsSyncedAt()
    {
        var template = Pending(MessageTemplateKind.Quotation, MessageTemplateName.QuotationName);
        var later = Now.AddHours(1);

        template.SyncFrom("APPROVED", null, "UTILITY", "NAMED", """[{"type":"BODY","text":"Hola"}]""", later);

        Assert.Equal(MessageTemplateKind.Quotation, template.Kind);
        Assert.Equal((MessageTemplateStatuses.Approved, later, later), (template.Status, template.SyncedAt, template.UpdatedAt));
        Assert.Equal("""[{"type":"BODY","text":"Hola"}]""", template.ComponentsJson);
    }

    [Fact]
    public void AdoptAsQuotationOnlyWorksOnAnExternalTemplate()
    {
        var external = External(MessageTemplateName.QuotationName);
        external.AdoptAsQuotation(Now.AddMinutes(1));
        Assert.Equal(MessageTemplateKind.Quotation, external.Kind);

        var free = Pending();
        var exception = Assert.Throws<MessagingDomainException>(() => free.AdoptAsQuotation(Now));
        Assert.Equal(MessagingErrorCodes.TemplateQuotationConflict, exception.Code);
    }

    [Theory]
    [InlineData("PENDING", true)]
    [InlineData("APPROVED", true)]
    [InlineData("IN_APPEAL", true)]
    [InlineData("REJECTED", false)]
    [InlineData("PAUSED", false)]
    [InlineData("DISABLED", false)]
    [InlineData("LIMIT_EXCEEDED", false)]
    [InlineData("PENDING_DELETION", false)]
    [InlineData("UNKNOWN", false)]
    public void IsLiveIsPendingApprovedOrInAppeal(string status, bool live) => Assert.Equal(live, MessageTemplateStatuses.IsLive(status));

    [Theory]
    [InlineData("promo_octubre", true)]
    [InlineData("a", true)]
    [InlineData("promo-octubre", false)]
    [InlineData("Promo", false)]
    [InlineData("promo octubre", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheNameTakesLowercaseDigitsAndUnderscores(string? name, bool valid) => Assert.Equal(valid, MessageTemplateName.IsValid(name));

    [Fact]
    public void ANameLongerThan512IsInvalid() => Assert.False(MessageTemplateName.IsValid(new string('a', 513)));

    [Theory]
    [InlineData("qep_cotizacion", true)]
    [InlineData("qep_cotizacion_v2", true)]
    [InlineData("qep_cotizaciones", false)]
    [InlineData("cotizacion", false)]
    public void TheQuotationNameAndItsPrefixAreReserved(string name, bool reserved) => Assert.Equal(reserved, MessageTemplateName.IsReserved(name));
}
```

- [ ] **Step 3: Correr la prueba y verla fallar**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~MessageTemplateTests" 2>&1 | tail -15
```

Esperado: error de compilación `CS0246: The type or namespace name 'MessageTemplate' could not be found` (y `MessageTemplateKind`, `MessageTemplateStatuses`, `MessageTemplateName`, `TemplateNameReserved`…).

- [ ] **Step 4: Agregar los códigos de error**

Al final de `MessagingErrorCodes`, antes de la llave de cierre:

```csharp
    /// <summary>Spec 2026-10-14 §4.8: el nombre no cumple <c>^[a-z0-9_]{1,512}$</c>. Última línea de defensa del dominio; el validador ya
    /// lo marca en <c>name</c>.</summary>
    public const string TemplateNameInvalid = "messaging.template.name_invalid";

    /// <summary>§4.5: <c>qep_cotizacion</c> y <c>qep_cotizacion_*</c> son de la plantilla que aprovisiona QEP.</summary>
    public const string TemplateNameReserved = "messaging.template.name_reserved";

    /// <summary>§4.8: otra plantilla de la conexión ya se llama así en ese idioma (en el espejo o en Meta). Sin mapa <c>errors</c>, como
    /// <c>quick_reply.shortcut_taken</c>.</summary>
    public const string TemplateNameTaken = "messaging.template.name_taken";

    /// <summary>§4.8: el WABA llegó al tope de plantillas que Meta le permite.</summary>
    public const string TemplateLimitReached = "messaging.template.limit_reached";

    /// <summary>§4.8: Meta no aceptó el contenido; el mensaje lleva el <c>error_user_msg</c> de Graph.</summary>
    public const string TemplateRejectedByMeta = "messaging.template.rejected_by_meta";

    /// <summary>§4.5: la Resumable Upload del PDF de ejemplo falló. No queda fila; reintentar es seguro.</summary>
    public const string TemplateUploadFailed = "messaging.template.upload_failed";

    /// <summary>§4.5: la conexión ya tiene su plantilla de cotización en <c>PENDING</c>, <c>APPROVED</c> o <c>IN_APPEAL</c>.</summary>
    public const string TemplateQuotationExists = "messaging.template.quotation_exists";

    /// <summary>§4.5: existe una <c>qep_cotizacion</c> creada fuera de QEP cuyo cuerpo no es el de la definición.</summary>
    public const string TemplateQuotationConflict = "messaging.template.quotation_conflict";

    /// <summary>§4.3: una campaña <c>Scheduled</c> o <c>Sending</c> de la conexión referencia nombre e idioma.</summary>
    public const string TemplateInUseByCampaign = "messaging.template.in_use_by_campaign";

    /// <summary>§4.8: el <c>templateId</c> no es de esa conexión (404; uuid local, no filtra nada).</summary>
    public const string TemplateNotFound = "messaging.template.not_found";
```

- [ ] **Step 5: Escribir el dominio**

```csharp
// src/Modules/Messaging/Modules.Messaging.Domain/MessageTemplate.cs
using System.Text.RegularExpressions;

namespace Modules.Messaging.Domain;

/// <summary>Spec 2026-10-14 D-T1 y D-T7: quién creó la plantilla. <c>External</c> = desde la consola de Meta; el refresco la trae.</summary>
public enum MessageTemplateKind
{
    Free,
    Quotation,
    External,
}

/// <summary>Los estados de Meta, como texto (D-T15). Sin enum ni <c>CHECK</c>: Meta agrega estados sin avisar y uno desconocido se guarda
/// tal cual para que la pantalla lo muestre crudo.</summary>
public static class MessageTemplateStatuses
{
    public const int MaxLength = 32;

    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Paused = "PAUSED";
    public const string Disabled = "DISABLED";
    public const string InAppeal = "IN_APPEAL";
    public const string PendingDeletion = "PENDING_DELETION";
    public const string LimitExceeded = "LIMIT_EXCEEDED";

    /// <summary>Un listado o un webhook sin <c>status</c>/<c>event</c>: no es un error, es un dato que no vino.</summary>
    public const string Unknown = "UNKNOWN";

    /// <summary>Recorta, pasa a mayúsculas y ajusta al ancho de la columna; vacío es <see cref="Unknown"/>.</summary>
    public static string Normalize(string? status)
    {
        var trimmed = (status ?? string.Empty).Trim().ToUpperInvariant();
        if (trimmed.Length == 0)
        {
            return Unknown;
        }

        return trimmed.Length <= MaxLength ? trimmed : trimmed[..MaxLength];
    }

    /// <summary>§4.5: lo que cuenta como «ya existe» para la plantilla de cotización: viva en Meta, aprobada o en camino.</summary>
    public static bool IsLive(string status) => status is Pending or Approved or InAppeal;
}

/// <summary>§4.2: la regla del nombre de Meta (<c>^[a-z0-9_]{1,512}$</c>) y la reserva de la plantilla de cotización (§4.5).</summary>
public static partial class MessageTemplateName
{
    public const int MaxLength = 512;

    public const string QuotationName = "qep_cotizacion";

    public const string QuotationPrefix = "qep_cotizacion_";

    public static bool IsValid(string? name) => name is not null && Pattern().IsMatch(name);

    public static bool IsReserved(string? name) =>
        name is not null && (string.Equals(name, QuotationName, StringComparison.Ordinal) || name.StartsWith(QuotationPrefix, StringComparison.Ordinal));

    // \z y no $: $ también acepta un salto de línea al final.
    [GeneratedRegex(@"^[a-z0-9_]{1,512}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>
/// Spec 2026-10-14 §4.1 y §4.2: el espejo de una plantilla del WABA de una conexión. Tres escritores: <see cref="Pending"/> (creada
/// desde QEP), <see cref="ApplyStatus"/> (el webhook) y <see cref="SyncFrom"/>/<see cref="Mirrored"/> (el refresco contra Meta). Lo que
/// viene de Meta se recorta al ancho de su columna: una fila que no cabe haría fallar el commit después de haber llamado a Graph.
/// </summary>
public sealed class MessageTemplate
{
    public const int MetaTemplateIdMaxLength = 64;
    public const int LanguageMaxLength = 16;
    public const int CategoryMaxLength = 32;
    public const int ParameterFormatMaxLength = 16;
    public const int StatusReasonMaxLength = 64;

    private MessageTemplate()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConnectionId { get; private set; }

    public string MetaTemplateId { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string Language { get; private set; } = string.Empty;

    public string Category { get; private set; } = string.Empty;

    public string ParameterFormat { get; private set; } = string.Empty;

    public string Status { get; private set; } = MessageTemplateStatuses.Unknown;

    public string? StatusReason { get; private set; }

    /// <summary>Los <c>components</c> de Meta tal cual (jsonb); <c>TemplateCatalog</c> los normaliza al leer.</summary>
    public string ComponentsJson { get; private set; } = "[]";

    public MessageTemplateKind Kind { get; private set; }

    public Guid? CreatedByMemberId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary><c>null</c> hasta que un refresco la confirme contra Meta.</summary>
    public DateTimeOffset? SyncedAt { get; private set; }

    public bool IsApproved => Status == MessageTemplateStatuses.Approved;

    public bool IsLive => MessageTemplateStatuses.IsLive(Status);

    /// <summary>Creada desde QEP con el <c>id</c> que Meta devolvió. Una <c>Free</c> cumple la regla del nombre y no toma uno reservado;
    /// la <c>Quotation</c> trae el nombre de su definición.</summary>
    public static MessageTemplate Pending(
        Guid tenantId, Guid connectionId, string metaTemplateId, string name, string language, string category, string parameterFormat,
        string? status, string componentsJson, MessageTemplateKind kind, Guid? createdByMemberId, DateTimeOffset now)
    {
        if (kind == MessageTemplateKind.External)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A template created from QEP is Free or Quotation.");
        }

        if (!MessageTemplateName.IsValid(name))
        {
            throw new MessagingDomainException(MessagingErrorCodes.TemplateNameInvalid, "The template name takes 1 to 512 lowercase letters, digits or underscores.");
        }

        if (kind == MessageTemplateKind.Free && MessageTemplateName.IsReserved(name))
        {
            throw new MessagingDomainException(MessagingErrorCodes.TemplateNameReserved, "That name is reserved for the quotation template QEP provisions.");
        }

        var template = New(tenantId, connectionId, metaTemplateId, name, language, category, parameterFormat, componentsJson, now);
        template.Kind = kind;
        template.CreatedByMemberId = createdByMemberId;
        template.Status = MessageTemplateStatuses.Normalize(status);
        return template;
    }

    /// <summary>Traída por el refresco (D-T7): <c>External</c>, ya sincronizada. El nombre es asunto de Meta y no se valida.</summary>
    public static MessageTemplate Mirrored(
        Guid tenantId, Guid connectionId, string metaTemplateId, string name, string language, string category, string parameterFormat,
        string? status, string? statusReason, string componentsJson, DateTimeOffset now)
    {
        var template = New(tenantId, connectionId, metaTemplateId, name, language, category, parameterFormat, componentsJson, now);
        template.Kind = MessageTemplateKind.External;
        template.Status = MessageTemplateStatuses.Normalize(status);
        template.StatusReason = Reason(statusReason);
        template.SyncedAt = now;
        return template;
    }

    /// <summary>El webhook <c>message_template_status_update</c>: estado nuevo y su motivo («NONE» = ninguno).</summary>
    public void ApplyStatus(string? status, string? reason, DateTimeOffset now)
    {
        Status = MessageTemplateStatuses.Normalize(status);
        StatusReason = Reason(reason);
        UpdatedAt = now;
    }

    /// <summary>El refresco: lo que Meta dice hoy. El <c>kind</c> se conserva (una <c>Quotation</c> sigue siéndolo).</summary>
    public void SyncFrom(string? status, string? reason, string category, string parameterFormat, string componentsJson, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(componentsJson);
        Status = MessageTemplateStatuses.Normalize(status);
        StatusReason = Reason(reason);
        Category = Truncate(category ?? string.Empty, CategoryMaxLength);
        ParameterFormat = Truncate(parameterFormat ?? string.Empty, ParameterFormatMaxLength);
        ComponentsJson = componentsJson;
        SyncedAt = now;
        UpdatedAt = now;
    }

    /// <summary>§4.5: una <c>qep_cotizacion</c> creada en la consola de Meta cuyo cuerpo coincide con la definición pasa a ser la de QEP.</summary>
    public void AdoptAsQuotation(DateTimeOffset now)
    {
        if (Kind != MessageTemplateKind.External)
        {
            throw new MessagingDomainException(MessagingErrorCodes.TemplateQuotationConflict, "Only a template created outside QEP can be adopted.");
        }

        Kind = MessageTemplateKind.Quotation;
        UpdatedAt = now;
    }

    private static MessageTemplate New(
        Guid tenantId, Guid connectionId, string metaTemplateId, string name, string language, string category, string parameterFormat,
        string componentsJson, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metaTemplateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentNullException.ThrowIfNull(componentsJson);
        return new MessageTemplate
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            MetaTemplateId = Truncate(metaTemplateId, MetaTemplateIdMaxLength),
            Name = Truncate(name, MessageTemplateName.MaxLength),
            Language = Truncate(language, LanguageMaxLength),
            Category = Truncate(category ?? string.Empty, CategoryMaxLength),
            ParameterFormat = Truncate(parameterFormat ?? string.Empty, ParameterFormatMaxLength),
            ComponentsJson = componentsJson,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static string? Reason(string? reason)
    {
        var trimmed = (reason ?? string.Empty).Trim();
        return trimmed.Length == 0 || string.Equals(trimmed, "NONE", StringComparison.OrdinalIgnoreCase) ? null : Truncate(trimmed, StatusReasonMaxLength);
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
```

- [ ] **Step 6: Correr la prueba y verla pasar**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~MessageTemplateTests" 2>&1 | tail -5
```

Esperado: `Passed!` con 32 pruebas (12 hechos + 20 casos de teoría), 0 fallas.

- [ ] **Step 7: Commit**

```bash
git add src/Modules/Messaging/Modules.Messaging.Domain tests/Modules/Messaging/Modules.Messaging.UnitTests/MessageTemplateTests.cs
git commit -m "feat(messaging): agregado MessageTemplate, el espejo de una plantilla de Meta"
```

---

### Task 2: Persistencia — tablas, repositorio, migración y traducción de índices

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingDbContext.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/TemplateMirrorSyncRecord.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/TemplateManagementPorts.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessageTemplateRepository.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingUnitOfWork.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs`
- Create (generada): `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/Migrations/<ts>_AddMessageTemplates.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MessageTemplatePersistenceTests.cs`

**Interfaces:**
- Consumes: `MessageTemplate`, `MessageTemplateKind`, `MessageTemplateStatuses`, `MessageTemplateName` (Task 1).
- Produces: `IMessageTemplateRepository` con `FindAsync(Guid tenantId, Guid connectionId, Guid templateId, ct)`, `FindByMetaIdAsync(Guid, Guid, string metaTemplateId, ct)`, `FindByNameAsync(Guid, Guid, string name, string language, ct)`, `FindQuotationAsync(Guid, Guid, ct)`, `ListAsync(Guid, Guid, ct)` (con tracking, por nombre e idioma), `ListApprovedAsync(Guid, Guid, ct)` (sin tracking), `Add`, `Remove`, `IsReferencedByActiveCampaignAsync(Guid, Guid, string name, string language, ct)`, `LastSyncAsync(Guid, Guid, ct) → DateTimeOffset?`, `MarkSyncedAsync(Guid, Guid, DateTimeOffset now, ct)`; `IQuotationTemplateReadiness.IsApprovedAsync(Guid tenantId, Guid connectionId, ct)` (sólo la interfaz; la implementación es de la Task 11); `MessagingDbContext.MessageTemplates` (DbSet público), `MessagingDbContext.MessageTemplateMetaIdIndex = "UX_message_templates_meta_id"`, `MessagingDbContext.MessageTemplateNameLanguageIndex = "UX_message_templates_name_language"`.

- [ ] **Step 1: Escribir la prueba de persistencia**

```csharp
// tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MessageTemplatePersistenceTests.cs
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-14 §4.1: el espejo contra Postgres de verdad. Ida y vuelta, los dos índices únicos traducidos por nombre,
/// el marcador de sincronización y la consulta de campañas activas.</summary>
public sealed class MessageTemplatePersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 10, 14, 15, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId)
    {
        public Guid TenantId => Tenant.TenantId;
    }

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        return new Fixture(factory, connectionString, tenant, connectionId);
    }

    private static async Task<T> InScopeAsync<T>(QepApiFactory factory, Func<IMessageTemplateRepository, IMessagingUnitOfWork, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(
            scope.ServiceProvider.GetRequiredService<IMessageTemplateRepository>(),
            scope.ServiceProvider.GetRequiredService<IMessagingUnitOfWork>());
    }

    private static MessageTemplate Mirrored(Fixture f, string metaId, string name, string language = "es") =>
        MessageTemplate.Mirrored(f.TenantId, f.ConnectionId, metaId, name, language, "MARKETING", "NAMED", "APPROVED", null, "[]", Now);

    [Fact]
    public async Task APendingAndAMirroredTemplateRoundTripWithTheirColumns()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var member = Guid.CreateVersion7();

        var (pendingId, mirroredId) = await InScopeAsync(f.Factory, async (repository, unitOfWork) =>
        {
            var pending = MessageTemplate.Pending(f.TenantId, f.ConnectionId, "900", "promo", "es", "MARKETING", "NAMED", "PENDING",
                """[{"type":"BODY","text":"Hola {{first_name}}"}]""", MessageTemplateKind.Free, member, Now);
            var mirrored = MessageTemplate.Mirrored(f.TenantId, f.ConnectionId, "901", "aviso", "es", "UTILITY", "POSITIONAL", "APPROVED", "NONE", "[]", Now);
            repository.Add(pending);
            repository.Add(mirrored);
            await unitOfWork.SaveChangesAsync(Ct);
            return (pending.Id, mirrored.Id);
        });

        await InScopeAsync(f.Factory, async (repository, _) =>
        {
            var all = await repository.ListAsync(f.TenantId, f.ConnectionId, Ct);
            Assert.Equal(["aviso", "promo"], all.Select(template => template.Name));
            var promo = await repository.FindAsync(f.TenantId, f.ConnectionId, pendingId, Ct);
            Assert.NotNull(promo);
            Assert.Equal(("900", MessageTemplateKind.Free, "PENDING", member), (promo.MetaTemplateId, promo.Kind, promo.Status, promo.CreatedByMemberId));
            // jsonb normaliza espacios: se compara sin ellos.
            Assert.Equal("""[{"type":"BODY","text":"Hola{{first_name}}"}]""", promo.ComponentsJson.Replace(" ", string.Empty, StringComparison.Ordinal));
            Assert.Equal(mirroredId, (await repository.FindByMetaIdAsync(f.TenantId, f.ConnectionId, "901", Ct))!.Id);
            Assert.Equal(mirroredId, (await repository.FindByNameAsync(f.TenantId, f.ConnectionId, "aviso", "es", Ct))!.Id);
            Assert.Single(await repository.ListApprovedAsync(f.TenantId, f.ConnectionId, Ct));
            Assert.Null(await repository.FindQuotationAsync(f.TenantId, f.ConnectionId, Ct));
            // Otro tenant con los mismos ids no ve nada.
            Assert.Empty(await repository.ListAsync(Guid.CreateVersion7(), f.ConnectionId, Ct));
            return 0;
        });
    }

    [Fact]
    public async Task ARepeatedNameAndLanguageIs422NameTakenByIndexName()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await InScopeAsync(f.Factory, async (repository, unitOfWork) =>
        {
            repository.Add(Mirrored(f, "1", "promo"));
            return await unitOfWork.SaveChangesAsync(Ct);
        });

        var exception = await Assert.ThrowsAsync<MessagingDomainException>(() => InScopeAsync(f.Factory, async (repository, unitOfWork) =>
        {
            repository.Add(Mirrored(f, "2", "promo"));
            return await unitOfWork.SaveChangesAsync(Ct);
        }));

        Assert.Equal(MessagingErrorCodes.TemplateNameTaken, exception.Code);
        // Mismo nombre en otro idioma sí cabe.
        await InScopeAsync(f.Factory, async (repository, unitOfWork) =>
        {
            repository.Add(Mirrored(f, "3", "promo", "en_US"));
            return await unitOfWork.SaveChangesAsync(Ct);
        });
    }

    [Fact]
    public async Task ARepeatedMetaIdIsAConcurrencyConflict()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await InScopeAsync(f.Factory, async (repository, unitOfWork) =>
        {
            repository.Add(Mirrored(f, "1", "a"));
            return await unitOfWork.SaveChangesAsync(Ct);
        });

        await Assert.ThrowsAsync<RequestConcurrencyException>(() => InScopeAsync(f.Factory, async (repository, unitOfWork) =>
        {
            repository.Add(Mirrored(f, "1", "b"));
            return await unitOfWork.SaveChangesAsync(Ct);
        }));
    }

    [Fact]
    public async Task TheSyncMarkerIsNullUntilMarkedAndThenUpserts()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        Assert.Null(await InScopeAsync(f.Factory, (repository, _) => repository.LastSyncAsync(f.TenantId, f.ConnectionId, Ct)));

        await InScopeAsync(f.Factory, async (repository, unitOfWork) =>
        {
            await repository.MarkSyncedAsync(f.TenantId, f.ConnectionId, Now, Ct);
            return await unitOfWork.SaveChangesAsync(Ct);
        });
        await InScopeAsync(f.Factory, async (repository, unitOfWork) =>
        {
            await repository.MarkSyncedAsync(f.TenantId, f.ConnectionId, Now.AddHours(1), Ct);
            return await unitOfWork.SaveChangesAsync(Ct);
        });

        Assert.Equal(Now.AddHours(1), await InScopeAsync(f.Factory, (repository, _) => repository.LastSyncAsync(f.TenantId, f.ConnectionId, Ct)));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.template_mirror_syncs"));
    }

    [Fact]
    public async Task AScheduledOrSendingCampaignReferencesTheTemplateADraftOrFinishedOneDoesNot()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var member = await OwnerMembershipIdAsync(f.ConnectionString, f.Tenant);
        foreach (var (name, status) in new[] { ("promo", "Scheduled"), ("aviso", "Sending"), ("borrador", "Draft"), ("vieja", "Completed") })
        {
            await ExecuteAsync(f.ConnectionString,
                """
                INSERT INTO messaging.campaigns (id, tenant_id, connection_id, name, template_name, template_language, template_category, parameters,
                    audience_tag_ids, audience_customer_ids, status, created_by_member_id, created_at, updated_at, version)
                VALUES (@id, @tenantId, @connectionId, @name, @name, 'es', 'MARKETING', '{}'::jsonb, ARRAY[@tag]::uuid[], ARRAY[]::uuid[], @status, @member, now(), now(), 1)
                """,
                ("id", Guid.CreateVersion7()), ("tenantId", f.TenantId), ("connectionId", f.ConnectionId), ("name", name), ("tag", Guid.CreateVersion7()),
                ("status", status), ("member", member));
        }

        await InScopeAsync(f.Factory, async (repository, _) =>
        {
            Assert.True(await repository.IsReferencedByActiveCampaignAsync(f.TenantId, f.ConnectionId, "promo", "es", Ct));
            Assert.True(await repository.IsReferencedByActiveCampaignAsync(f.TenantId, f.ConnectionId, "aviso", "es", Ct));
            Assert.False(await repository.IsReferencedByActiveCampaignAsync(f.TenantId, f.ConnectionId, "borrador", "es", Ct));
            Assert.False(await repository.IsReferencedByActiveCampaignAsync(f.TenantId, f.ConnectionId, "vieja", "es", Ct));
            Assert.False(await repository.IsReferencedByActiveCampaignAsync(f.TenantId, f.ConnectionId, "promo", "en_US", Ct));
            return 0;
        });
    }
}
```

> Si el `INSERT` en `messaging.campaigns` falla por una columna `NOT NULL` que no está en la lista, mira el `CreateTable` de `20261012024829_AddCampaigns.cs` y agrégala con un valor neutro; no cambies la prueba de otra forma.

- [ ] **Step 2: Correr la prueba y verla fallar**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~MessageTemplatePersistenceTests" 2>&1 | tail -15
```

Esperado: `CS0246: The type or namespace name 'IMessageTemplateRepository' could not be found`.

- [ ] **Step 3: El puerto en Application**

```csharp
// src/Modules/Messaging/Modules.Messaging.Application/TemplateManagementPorts.cs
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-14 §4.3: el espejo de plantillas de una conexión. Todo método recibe <c>tenantId</c> y <c>connectionId</c>: otro tenant
/// responde igual que nada. El agregado se escribe con tracking y se guarda con <see cref="IMessagingUnitOfWork"/>, que traduce
/// <c>UX_message_templates_name_language</c> a <c>messaging.template.name_taken</c> y <c>UX_message_templates_meta_id</c> a
/// <c>concurrency.conflict</c> (dos refrescos a la vez).
/// </summary>
public interface IMessageTemplateRepository
{
    /// <summary>Con tracking.</summary>
    Task<MessageTemplate?> FindAsync(Guid tenantId, Guid connectionId, Guid templateId, CancellationToken cancellationToken);

    /// <summary>Con tracking: el webhook y el refresco la modifican.</summary>
    Task<MessageTemplate?> FindByMetaIdAsync(Guid tenantId, Guid connectionId, string metaTemplateId, CancellationToken cancellationToken);

    /// <summary>Con tracking: la adopción (§4.5) la modifica.</summary>
    Task<MessageTemplate?> FindByNameAsync(Guid tenantId, Guid connectionId, string name, string language, CancellationToken cancellationToken);

    /// <summary>La única fila <c>Quotation</c> de la conexión, con tracking, o <c>null</c>.</summary>
    Task<MessageTemplate?> FindQuotationAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Todas, con tracking, por nombre e idioma en orden ordinal: el refresco las recorre y las modifica.</summary>
    Task<IReadOnlyList<MessageTemplate>> ListAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Sólo <c>APPROVED</c>, sin tracking: el catálogo de envío.</summary>
    Task<IReadOnlyList<MessageTemplate>> ListApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    void Add(MessageTemplate template);

    void Remove(MessageTemplate template);

    /// <summary>D-T12: una campaña <c>Scheduled</c> o <c>Sending</c> de la conexión usa ese nombre en ese idioma.</summary>
    Task<bool> IsReferencedByActiveCampaignAsync(Guid tenantId, Guid connectionId, string name, string language, CancellationToken cancellationToken);

    /// <summary>§4.1 <c>template_mirror_syncs</c>: cuándo se confirmó el espejo de la conexión contra Meta; <c>null</c> si nunca.</summary>
    Task<DateTimeOffset?> LastSyncAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Upsert del marcador, con tracking; se escribe con el <c>SaveChanges</c> del refresco.</summary>
    Task MarkSyncedAsync(Guid tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-10-14 §4.3 y §5: la señal para el consumidor Quotations. <c>true</c> sólo con una fila <c>Quotation</c> en
/// <c>APPROVED</c> para esa conexión del tenant. El adaptador hacia Quotations va en Bootstrapper cuando ese spec llegue.</summary>
public interface IQuotationTemplateReadiness
{
    Task<bool> IsApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: El registro del marcador y la configuración del DbContext**

```csharp
// src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/TemplateMirrorSyncRecord.cs
namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Spec 2026-10-14 §4.1 <c>messaging.template_mirror_syncs</c>: sin esta fila no se distingue «un WABA sin plantillas» de «un
/// espejo que nunca se llenó», y la segunda situación obliga a sincronizar antes de servir el catálogo de envío.</summary>
internal sealed class TemplateMirrorSyncRecord
{
    public Guid TenantId { get; set; }

    public Guid ConnectionId { get; set; }

    public DateTimeOffset SyncedAt { get; set; }
}
```

En `MessagingDbContext.cs`, junto a `QuickReplyShortcutIndex`:

```csharp
    /// <summary>Spec 2026-10-14 §4.1: los dos únicos del espejo. La unidad de trabajo los traduce por nombre.</summary>
    public const string MessageTemplateMetaIdIndex = "UX_message_templates_meta_id";

    public const string MessageTemplateNameLanguageIndex = "UX_message_templates_name_language";
```

Junto a `QuickReplies`:

```csharp
    /// <summary>Spec 2026-10-14 §4.1: el espejo de plantillas.</summary>
    public DbSet<MessageTemplate> MessageTemplates => Set<MessageTemplate>();

    internal DbSet<TemplateMirrorSyncRecord> TemplateMirrorSyncs => Set<TemplateMirrorSyncRecord>();
```

En `OnModelCreating`, después de `ConfigureQuickReply(modelBuilder);`:

```csharp
        ConfigureMessageTemplate(modelBuilder);
        ConfigureTemplateMirrorSync(modelBuilder);
```

Y los dos métodos, después de `ConfigureQuickReply`:

```csharp
    /// <summary>Spec 2026-10-14 §4.1. Sin FK a la conexión: es de Integrations, otro schema (como el cubo de envío).</summary>
    private static void ConfigureMessageTemplate(ModelBuilder modelBuilder)
    {
        var template = modelBuilder.Entity<MessageTemplate>();
        template.ToTable("message_templates", Schema, table =>
            table.HasCheckConstraint("CK_message_templates_kind", "kind IN ('Free','Quotation','External')"));
        template.HasKey(value => value.Id);
        template.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        template.Property(value => value.TenantId).HasColumnName("tenant_id");
        template.Property(value => value.ConnectionId).HasColumnName("connection_id");
        template.Property(value => value.MetaTemplateId).HasColumnName("meta_template_id").HasMaxLength(MessageTemplate.MetaTemplateIdMaxLength);
        template.Property(value => value.Name).HasColumnName("name").HasMaxLength(MessageTemplateName.MaxLength);
        template.Property(value => value.Language).HasColumnName("language").HasMaxLength(MessageTemplate.LanguageMaxLength);
        template.Property(value => value.Category).HasColumnName("category").HasMaxLength(MessageTemplate.CategoryMaxLength);
        template.Property(value => value.ParameterFormat).HasColumnName("parameter_format").HasMaxLength(MessageTemplate.ParameterFormatMaxLength);
        template.Property(value => value.Status).HasColumnName("status").HasMaxLength(MessageTemplateStatuses.MaxLength);
        template.Property(value => value.StatusReason).HasColumnName("status_reason").HasMaxLength(MessageTemplate.StatusReasonMaxLength);
        template.Property(value => value.ComponentsJson).HasColumnName("components").HasColumnType("jsonb");
        template.Property(value => value.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
        template.Property(value => value.CreatedByMemberId).HasColumnName("created_by_member_id");
        template.Property(value => value.CreatedAt).HasColumnName("created_at");
        template.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        template.Property(value => value.SyncedAt).HasColumnName("synced_at");
        template.HasIndex(value => new { value.TenantId, value.ConnectionId, value.MetaTemplateId }, MessageTemplateMetaIdIndex).IsUnique();
        template.HasIndex(value => new { value.TenantId, value.ConnectionId, value.Name, value.Language }, MessageTemplateNameLanguageIndex).IsUnique();
        template.HasIndex(value => new { value.TenantId, value.ConnectionId, value.Status }, "IX_message_templates_status");
    }

    private static void ConfigureTemplateMirrorSync(ModelBuilder modelBuilder)
    {
        var sync = modelBuilder.Entity<TemplateMirrorSyncRecord>();
        sync.ToTable("template_mirror_syncs", Schema);
        sync.HasKey(value => new { value.TenantId, value.ConnectionId });
        sync.Property(value => value.TenantId).HasColumnName("tenant_id");
        sync.Property(value => value.ConnectionId).HasColumnName("connection_id");
        sync.Property(value => value.SyncedAt).HasColumnName("synced_at");
    }
```

- [ ] **Step 5: El repositorio**

```csharp
// src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessageTemplateRepository.cs
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Spec 2026-10-14 §4.3. El orden es ordinal en memoria y no la collation de la base (como las respuestas rápidas): un WABA
/// tiene decenas de plantillas, no miles.</summary>
internal sealed class MessageTemplateRepository(MessagingDbContext dbContext) : IMessageTemplateRepository
{
    public Task<MessageTemplate?> FindAsync(Guid tenantId, Guid connectionId, Guid templateId, CancellationToken cancellationToken) =>
        dbContext.MessageTemplates.SingleOrDefaultAsync(
            template => template.TenantId == tenantId && template.ConnectionId == connectionId && template.Id == templateId, cancellationToken);

    public Task<MessageTemplate?> FindByMetaIdAsync(Guid tenantId, Guid connectionId, string metaTemplateId, CancellationToken cancellationToken) =>
        dbContext.MessageTemplates.SingleOrDefaultAsync(
            template => template.TenantId == tenantId && template.ConnectionId == connectionId && template.MetaTemplateId == metaTemplateId, cancellationToken);

    public Task<MessageTemplate?> FindByNameAsync(Guid tenantId, Guid connectionId, string name, string language, CancellationToken cancellationToken) =>
        dbContext.MessageTemplates.SingleOrDefaultAsync(
            template => template.TenantId == tenantId && template.ConnectionId == connectionId && template.Name == name && template.Language == language,
            cancellationToken);

    public Task<MessageTemplate?> FindQuotationAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        dbContext.MessageTemplates.FirstOrDefaultAsync(
            template => template.TenantId == tenantId && template.ConnectionId == connectionId && template.Kind == MessageTemplateKind.Quotation,
            cancellationToken);

    public async Task<IReadOnlyList<MessageTemplate>> ListAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.MessageTemplates
            .Where(template => template.TenantId == tenantId && template.ConnectionId == connectionId)
            .ToListAsync(cancellationToken);
        return Ordered(rows);
    }

    public async Task<IReadOnlyList<MessageTemplate>> ListApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.MessageTemplates.AsNoTracking()
            .Where(template => template.TenantId == tenantId && template.ConnectionId == connectionId && template.Status == MessageTemplateStatuses.Approved)
            .ToListAsync(cancellationToken);
        return Ordered(rows);
    }

    public void Add(MessageTemplate template) => dbContext.MessageTemplates.Add(template);

    public void Remove(MessageTemplate template) => dbContext.MessageTemplates.Remove(template);

    public Task<bool> IsReferencedByActiveCampaignAsync(Guid tenantId, Guid connectionId, string name, string language, CancellationToken cancellationToken) =>
        dbContext.Campaigns.AnyAsync(
            campaign => campaign.TenantId == tenantId && campaign.ConnectionId == connectionId
                && campaign.TemplateName == name && campaign.TemplateLanguage == language
                && (campaign.Status == CampaignStatus.Scheduled || campaign.Status == CampaignStatus.Sending),
            cancellationToken);

    public async Task<DateTimeOffset?> LastSyncAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var record = await dbContext.TemplateMirrorSyncs.AsNoTracking()
            .SingleOrDefaultAsync(sync => sync.TenantId == tenantId && sync.ConnectionId == connectionId, cancellationToken);
        return record?.SyncedAt;
    }

    public async Task MarkSyncedAsync(Guid tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var record = await dbContext.TemplateMirrorSyncs
            .SingleOrDefaultAsync(sync => sync.TenantId == tenantId && sync.ConnectionId == connectionId, cancellationToken);
        if (record is null)
        {
            dbContext.TemplateMirrorSyncs.Add(new TemplateMirrorSyncRecord { TenantId = tenantId, ConnectionId = connectionId, SyncedAt = now });
        }
        else
        {
            record.SyncedAt = now;
        }
    }

    private static MessageTemplate[] Ordered(List<MessageTemplate> rows) =>
        rows.OrderBy(template => template.Name, StringComparer.Ordinal).ThenBy(template => template.Language, StringComparer.Ordinal).ToArray();
}
```

- [ ] **Step 6: La unidad de trabajo traduce los dos índices**

En `MessagingUnitOfWork.SaveChangesAsync`, después del `catch` del atajo (los filtros `when` son excluyentes entre sí):

```csharp
        // Spec 2026-10-14 §4.1: nombre e idioma repetidos en la misma conexión son name_taken (422, sin mapa errors, como shortcut_taken).
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, MessagingDbContext.MessageTemplateNameLanguageIndex, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateNameTaken, "Another template of this connection already uses that name in that language.", exception);
        }
        // Dos refrescos de la misma conexión a la vez: el segundo pierde y el cliente reintenta (412, como la versión vieja).
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, MessagingDbContext.MessageTemplateMetaIdIndex, StringComparison.Ordinal))
        {
            throw new RequestConcurrencyException("concurrency.conflict", "Another sync of the same connection committed first.", exception);
        }
```

Actualiza el `<summary>` de la clase para nombrar los dos códigos nuevos.

- [ ] **Step 7: Registro en DI**

En `MessagingInfrastructureExtensions.cs`, junto a `services.AddScoped<IQuickReplyRepository, QuickReplyRepository>();`:

```csharp
        // Spec 2026-10-14 §4.3: el espejo de plantillas.
        services.AddScoped<IMessageTemplateRepository, MessageTemplateRepository>();
```

- [ ] **Step 8: Generar la migración y revisarla**

```bash
dotnet ef migrations add AddMessageTemplates --project src/Modules/Messaging/Modules.Messaging.Infrastructure --context MessagingDbContext -o Persistence/Migrations
```

Abre el `Up` del archivo `<ts>_AddMessageTemplates.cs` y comprueba a ojo, **sin editarlo**: exactamente dos `CreateTable` (`message_templates` y `template_mirror_syncs`, schema `messaging`), el `CHECK` `CK_message_templates_kind`, `components` como `jsonb`, tres `CreateIndex` sobre `message_templates` (`UX_message_templates_meta_id` único, `UX_message_templates_name_language` único, `IX_message_templates_status`), y la PK compuesta de `template_mirror_syncs`. `Down` = dos `DropTable`. **Si el `Up` toca cualquier otra tabla, el modelo tenía cambios previos sin migrar: detente y repórtalo.**

- [ ] **Step 9: Correr la prueba y verla pasar**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~MessageTemplatePersistenceTests" 2>&1 | tail -5
```

Esperado: `Passed!` con 5 pruebas. (La primera corrida levanta el contenedor de Postgres; tarda.)

- [ ] **Step 10: Commit**

```bash
git add src/Modules/Messaging tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MessageTemplatePersistenceTests.cs
git commit -m "feat(messaging): espejo de plantillas en messaging.message_templates con su marcador de sincronización"
```

---

### Task 3: Application — DTOs de gestión y `TemplateCatalog.FromStored`

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/TemplateManagementDtos.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessageTemplates.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateCatalogFromStoredTests.cs`

**Interfaces:**
- Produces: `TemplateCatalog.FromStored(string name, string language, string category, string parameterFormat, string componentsJson) → MessageTemplateDto`; `ManagedTemplateDto`, `QuotationTemplateStatusDto`, `TemplateManagementDto` (formas en el código de abajo).

- [ ] **Step 1: Escribir la prueba**

```csharp
// tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateCatalogFromStoredTests.cs
using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-14 D-T8: el catálogo lee del espejo. <c>FromStored</c> normaliza una fila (nombre, idioma, categoría, formato y
/// los <c>components</c> crudos) exactamente como <c>Normalize</c> normaliza un elemento del listado de Meta.</summary>
public sealed class TemplateCatalogFromStoredTests
{
    [Fact]
    public void AStoredRowNormalizesLikeTheMetaListing()
    {
        const string components = """
            [{"type":"HEADER","format":"TEXT","text":"Oferta"},
             {"type":"BODY","text":"Hola {{first_name}}, {{product}} con descuento"},
             {"type":"FOOTER","text":"QEP"},
             {"type":"BUTTONS","buttons":[{"type":"QUICK_REPLY","text":"Detener"},{"type":"URL","text":"Ver","url":"https://qep.co/x"}]}]
            """;

        var dto = TemplateCatalog.FromStored("promo", "es", "MARKETING", "NAMED", components);

        Assert.Equal(("promo", "es", "MARKETING", TemplateCatalog.Named), (dto.Name, dto.Language, dto.Category, dto.ParameterFormat));
        Assert.Equal(("Oferta", "Hola {{first_name}}, {{product}} con descuento", "QEP"), (dto.Header, dto.Body, dto.Footer));
        Assert.Equal(["first_name", "product"], dto.Parameters);
        Assert.Equal(["QuickReply", "Url"], dto.Buttons.Select(button => button.Type));
        Assert.True(dto.Sendable);
    }

    [Fact]
    public void ADocumentHeaderIsNotSendableFromTheInboxAndSaysWhy()
    {
        var dto = TemplateCatalog.FromStored("qep_cotizacion", "es", "UTILITY", "NAMED",
            """[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{fullname}}"}]""");

        Assert.False(dto.Sendable);
        Assert.Equal(TemplateUnsupportedReasons.MediaHeader, dto.UnsupportedReason);
        Assert.Null(dto.Header);
    }

    [Theory]
    [InlineData("no es json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("")]
    public void UnreadableComponentsGiveAnEmptyBodyNotAnException(string components)
    {
        var dto = TemplateCatalog.FromStored("x", "es", "UTILITY", "POSITIONAL", components);

        Assert.Equal(string.Empty, dto.Body);
        Assert.Empty(dto.Parameters);
        Assert.Equal(TemplateCatalog.Positional, dto.ParameterFormat);
    }
}
```

- [ ] **Step 2: Correr y ver fallar**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~TemplateCatalogFromStoredTests" 2>&1 | tail -8
```

Esperado: `CS0117: 'TemplateCatalog' does not contain a definition for 'FromStored'`.

- [ ] **Step 3: `FromStored` y los DTOs**

En `MessageTemplates.cs`, dentro de `TemplateCatalog`, después de `Normalize`:

```csharp
    /// <summary>
    /// Spec 2026-10-14 D-T8: una fila del espejo, normalizada igual que un elemento del listado de Meta. Se arma el objeto que Meta
    /// devolvería (nombre, idioma, categoría, formato y <c>components</c>) y se pasa por el mismo <see cref="Parse"/>: una sola copia de
    /// las reglas. Unos <c>components</c> ilegibles cuentan como vacíos.
    /// </summary>
    public static MessageTemplateDto FromStored(string name, string language, string category, string parameterFormat, string componentsJson)
    {
        ArgumentNullException.ThrowIfNull(componentsJson);
        var raw = new JsonObject
        {
            ["name"] = name,
            ["language"] = language,
            ["category"] = category,
            ["parameter_format"] = parameterFormat,
        };
        JsonNode? components = null;
        try
        {
            components = componentsJson.Length == 0 ? null : JsonNode.Parse(componentsJson);
        }
        catch (JsonException)
        {
            // Ilegible = vacío.
        }

        raw["components"] = components is JsonArray array ? array : new JsonArray();
        using var document = JsonDocument.Parse(raw.ToJsonString());
        return Parse(document.RootElement, name, language);
    }
```

(`JsonNode.Parse("null")` devuelve `null` y cae en el `is JsonArray` falso. El nodo recién parseado no tiene padre, así que asignarlo a `raw["components"]` es válido.)

```csharp
// src/Modules/Messaging/Modules.Messaging.Application/TemplateManagementDtos.cs
namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-14 §4.3: una plantilla del espejo para la pantalla de administración. BFF: <c>status</c> y <c>statusReason</c> viajan con
/// el nombre de Meta (<c>REJECTED</c>, <c>INVALID_FORMAT</c>; el diccionario en español lo tiene el frontend, D-T15); <c>header</c>,
/// <c>body</c>, <c>footer</c>, <c>buttons</c>, <c>parameters</c>, <c>sendable</c> y <c>unsupportedReason</c> salen resueltos de
/// <see cref="TemplateCatalog"/> para que la pantalla no conozca el formato de componentes de Meta. <c>createdBy</c> es <c>null</c> en
/// una <c>External</c>: nadie de QEP la creó.
/// </summary>
public sealed record ManagedTemplateDto(
    Guid Id,
    string MetaTemplateId,
    string Name,
    string Language,
    string Category,
    string ParameterFormat,
    string Status,
    string? StatusReason,
    string Kind,
    string? Header,
    string Body,
    string? Footer,
    IReadOnlyList<TemplateButtonDto> Buttons,
    IReadOnlyList<string> Parameters,
    bool Sendable,
    string? UnsupportedReason,
    MemberRefDto? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>§4.3: el estado de la plantilla de cotización de la conexión. <c>null</c> en la pantalla = no aprovisionada, botón de crear.</summary>
public sealed record QuotationTemplateStatusDto(Guid TemplateId, string Status, string? StatusReason);

/// <summary>
/// §4.3: la pantalla de plantillas de una conexión en una llamada: la conexión (nombre y número), el estado de la plantilla de
/// cotización, cuándo se sincronizó el espejo por última vez (<c>null</c> = nunca) y todas las plantillas, sin paginar, por nombre e
/// idioma. Un WABA tiene decenas, no miles.
/// </summary>
public sealed record TemplateManagementDto(
    MessagingConnectionDto Connection,
    QuotationTemplateStatusDto? QuotationTemplate,
    DateTimeOffset? LastSyncedAt,
    IReadOnlyList<ManagedTemplateDto> Templates);
```

- [ ] **Step 4: Correr y ver pasar**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~TemplateCatalogFromStoredTests" 2>&1 | tail -5
```

Esperado: `Passed!` con 6 pruebas.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Messaging/Modules.Messaging.Application tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateCatalogFromStoredTests.cs
git commit -m "feat(messaging): TemplateCatalog.FromStored y los DTOs de la pantalla de plantillas"
```

---

### Task 4: `WhatsAppCloudClient` — crear, borrar, subir el PDF de ejemplo y pedir `id`/`rejected_reason`

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IWhatsAppCloudClient.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Meta/WhatsAppCloudClient.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Options/MessagingMetaOptions.cs`
- Modify: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingTestBed.cs` (`FakeWhatsAppClient`)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/WhatsAppCloudClientTemplateManagementTests.cs`

**Interfaces:**
- Produces: `MessagingGraphFailure(int HttpStatus, int? Code, string Reason, string? UserTitle = null, string? UserMessage = null)`; `CreatedTemplate(string Id, string Status, string Category)`; `DeletedTemplate(bool AlreadyGone)`; en `IWhatsAppCloudClient`: `Task<MessagingGraphResult<CreatedTemplate>> CreateTemplateAsync(MessagingTemplateSource source, JsonObject payload, ct)`, `Task<MessagingGraphResult<DeletedTemplate>> DeleteTemplateAsync(MessagingTemplateSource source, string metaTemplateId, string name, ct)`, `Task<MessagingGraphResult<string>> UploadTemplateExampleAsync(MessagingTemplateSource source, ReadOnlyMemory<byte> content, string mimeType, string fileName, ct)`; `WhatsAppCloudClient.TemplateFields = "id,name,language,status,category,components,parameter_format,rejected_reason"`; `MessagingMetaOptions.AppId`. En el fake: `NextCreatedTemplate`, `CreatedTemplates`, `NextDelete`, `Deleted`, `NextHandle`, `ExampleUploads`, `TemplateListCalls`.

- [ ] **Step 1: Escribir las pruebas unitarias del cliente**

```csharp
// tests/Modules/Messaging/Modules.Messaging.UnitTests/WhatsAppCloudClientTemplateManagementTests.cs
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Meta;
using Modules.Messaging.Infrastructure.Options;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-14 §4.4: crear (<c>POST /{waba}/message_templates</c>), borrar (<c>DELETE … ?hsm_id=&amp;name=</c>, «no existe» = ya
/// no está), la Resumable Upload del PDF de ejemplo (dos requests, la segunda con <c>OAuth</c> y <c>file_offset: 0</c>) y el listado con
/// <c>id</c> y <c>rejected_reason</c>. Los errores de Graph conservan <c>error_user_title</c> y <c>error_user_msg</c>; nunca el token.</summary>
public sealed class WhatsAppCloudClientTemplateManagementTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seen(HttpMethod Method, Uri Uri, string? Authorization, string? FileOffset, string? ContentType, string? Body);

    private sealed class ScriptedHandler(params (HttpStatusCode Status, string Body)[] answers) : HttpMessageHandler
    {
        private int _next;

        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Seen(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("file_offset", out var offsets) ? string.Join(",", offsets) : null,
                request.Content?.Headers.ContentType?.MediaType,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            var (status, body) = answers[Math.Min(_next++, answers.Length - 1)];
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public List<string> Names { get; } = [];

        public HttpClient CreateClient(string name)
        {
            Names.Add(name);
            var client = new HttpClient(handler, disposeHandler: false);
            WhatsAppCloudClient.ConfigureClient(client);
            return client;
        }
    }

    private static readonly MessagingTemplateSource Source = new("222", "tok");

    private static (WhatsAppCloudClient Client, SingleClientFactory Factory) ClientOver(HttpMessageHandler handler, string? appId = "100200300")
    {
        var factory = new SingleClientFactory(handler);
        return (new WhatsAppCloudClient(factory, Options.Create(new MessagingMetaOptions { AppId = appId }), NullLogger<WhatsAppCloudClient>.Instance), factory);
    }

    private static string MetaError(int code, string? userTitle, string? userMessage) =>
        JsonSerializer.Serialize(new
        {
            error = new { message = "(#100) Invalid parameter", type = "OAuthException", code, error_user_title = userTitle, error_user_msg = userMessage, fbtrace_id = "t" },
        });

    [Fact]
    public async Task CreatePostsThePayloadToTheWabaAndReadsIdStatusAndCategory()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """{"id":"777","status":"PENDING","category":"MARKETING"}"""));
        var payload = new JsonObject { ["name"] = "promo", ["language"] = "es", ["category"] = "MARKETING", ["parameter_format"] = "NAMED", ["components"] = new JsonArray() };

        var result = await ClientOver(handler).Client.CreateTemplateAsync(Source, payload, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(new CreatedTemplate("777", "PENDING", "MARKETING"), result.Value);
        var request = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Post, "/v24.0/222/message_templates", "Bearer tok"), (request.Method, request.Uri.AbsolutePath, request.Authorization));
        Assert.Equal(payload.ToJsonString(), request.Body);
    }

    [Fact]
    public async Task ACreateRejectedByMetaKeepsTheUserTitleAndMessage()
    {
        var handler = new ScriptedHandler((HttpStatusCode.BadRequest, MetaError(100, "Message Template Name Already Exists", "A template with that name and language already exists.")));

        var result = await ClientOver(handler).Client.CreateTemplateAsync(Source, new JsonObject { ["name"] = "promo" }, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal((400, 100, "Message Template Name Already Exists", "A template with that name and language already exists."),
            (result.Failure!.HttpStatus, result.Failure.Code, result.Failure.UserTitle, result.Failure.UserMessage));
    }

    [Fact]
    public async Task ACreateAnswerWithoutIdIsAFailure()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """{"success":true}"""));

        var result = await ClientOver(handler).Client.CreateTemplateAsync(Source, new JsonObject { ["name"] = "promo" }, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal("no_id", result.Failure!.Reason);
    }

    [Fact]
    public async Task DeleteSendsHsmIdAndNameAsQuery()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """{"success":true}"""));

        var result = await ClientOver(handler).Client.DeleteTemplateAsync(Source, "777", "promo", Ct);

        Assert.True(result.Succeeded);
        Assert.False(result.Value!.AlreadyGone);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("/v24.0/222/message_templates", request.Uri.AbsolutePath);
        Assert.Equal("?hsm_id=777&name=promo", request.Uri.Query);
    }

    [Fact]
    public async Task DeletingATemplateMetaNoLongerHasIsAlreadyGone()
    {
        var handler = new ScriptedHandler((HttpStatusCode.BadRequest, MetaError(100, "Invalid Template", "Message template does not exist in the given language.")));

        var result = await ClientOver(handler).Client.DeleteTemplateAsync(Source, "777", "promo", Ct);

        Assert.True(result.Succeeded);
        Assert.True(result.Value!.AlreadyGone);
    }

    [Fact]
    public async Task AnyOtherDeleteErrorIsAFailure()
    {
        var handler = new ScriptedHandler((HttpStatusCode.BadRequest, MetaError(100, "Something", "Another thing.")));

        var result = await ClientOver(handler).Client.DeleteTemplateAsync(Source, "777", "promo", Ct);

        Assert.False(result.Succeeded);
        Assert.Equal(400, result.Failure!.HttpStatus);
    }

    [Fact]
    public async Task UploadDoesTwoRequestsTheSecondWithOAuthAndFileOffsetZero()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """{"id":"upload:MTph"}"""), (HttpStatusCode.OK, """{"h":"4::aW1hZ2U"}"""));
        var (client, factory) = ClientOver(handler);
        var bytes = new byte[] { 1, 2, 3, 4, 5 };

        var result = await client.UploadTemplateExampleAsync(Source, bytes, "application/pdf", "ejemplo.pdf", Ct);

        Assert.True(result.Succeeded);
        Assert.Equal("4::aW1hZ2U", result.Value);
        Assert.Equal(2, handler.Requests.Count);
        var start = handler.Requests[0];
        Assert.Equal((HttpMethod.Post, "/v24.0/100200300/uploads", "Bearer tok"), (start.Method, start.Uri.AbsolutePath, start.Authorization));
        Assert.Equal("?file_length=5&file_type=application%2Fpdf&file_name=ejemplo.pdf", start.Uri.Query);
        var data = handler.Requests[1];
        Assert.Equal((HttpMethod.Post, "/v24.0/upload:MTph", "OAuth tok", "0", "application/pdf"), (data.Method, data.Uri.AbsolutePath, data.Authorization, data.FileOffset, data.ContentType));
        // El cuerpo binario viaja tal cual (la prueba lo lee como texto; cinco bytes).
        Assert.Equal(5, data.Body!.Length);
        Assert.All(factory.Names, name => Assert.Equal(WhatsAppCloudClient.UploadHttpClientName, name));
    }

    [Fact]
    public async Task UploadWithoutAnAppIdFailsBeforeCallingMeta()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, "{}"));

        var result = await ClientOver(handler, appId: null).Client.UploadTemplateExampleAsync(Source, new byte[] { 1 }, "application/pdf", "x.pdf", Ct);

        Assert.False(result.Succeeded);
        Assert.Equal("no_app_id", result.Failure!.Reason);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AnUploadIdWithAnUnexpectedShapeIsNotFollowed()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """{"id":"../evil"}"""));

        var result = await ClientOver(handler).Client.UploadTemplateExampleAsync(Source, new byte[] { 1 }, "application/pdf", "x.pdf", Ct);

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_upload_id", result.Failure!.Reason);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UploadWithoutAHandleIsAFailure()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """{"id":"upload:MTph"}"""), (HttpStatusCode.OK, """{}"""));

        var result = await ClientOver(handler).Client.UploadTemplateExampleAsync(Source, new byte[] { 1 }, "application/pdf", "x.pdf", Ct);

        Assert.False(result.Succeeded);
        Assert.Equal("no_handle", result.Failure!.Reason);
    }

    [Fact]
    public async Task ListingAsksForIdAndRejectedReason()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """{"data":[]}"""));

        await ClientOver(handler).Client.ListTemplatesAsync(Source, Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Contains("fields=id,name,language,status,category,components,parameter_format,rejected_reason", request.Uri.Query, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Correr y ver fallar**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~WhatsAppCloudClientTemplateManagementTests" 2>&1 | tail -10
```

Esperado: `CS1061: 'WhatsAppCloudClient' does not contain a definition for 'CreateTemplateAsync'` (y `CreatedTemplate`, `AppId`…).

- [ ] **Step 3: La interfaz y los records**

En `IWhatsAppCloudClient.cs`, agrega `using System.Text.Json.Nodes;`, reemplaza el record `MessagingGraphFailure` y agrega dos records y tres métodos:

```csharp
/// <summary>Copia chica de <c>GraphFailure</c> de Integrations: Messaging no lo referencia. Spec 2026-10-14 §4.8: <c>UserTitle</c> y
/// <c>UserMessage</c> son <c>error_user_title</c> y <c>error_user_msg</c> de Graph, lo único que distingue «nombre ya existe» del
/// resto de los 400 ahora que <c>error_subcode</c> no se devuelve. Sólo las operaciones de plantillas los llenan.</summary>
public sealed record MessagingGraphFailure(int HttpStatus, int? Code, string Reason, string? UserTitle = null, string? UserMessage = null);

/// <summary>Spec 2026-10-14 §4.4: lo que <c>POST /{waba}/message_templates</c> devuelve. La categoría puede diferir de la pedida.</summary>
public sealed record CreatedTemplate(string Id, string Status, string Category);

/// <summary>§4.4: <c>AlreadyGone</c> cuando Meta respondió que la plantilla ya no existe; el espejo la elimina igual (D-T13).</summary>
public sealed record DeletedTemplate(bool AlreadyGone);
```

Y dentro de la interfaz, después de `SendTemplateAsync`:

```csharp
    /// <summary>Spec 2026-10-14 §4.4: <c>POST /{wabaId}/message_templates</c> con el payload completo (nombre, idioma, categoría,
    /// <c>parameter_format</c> y <c>components</c>). Una falla trae <c>UserTitle</c>/<c>UserMessage</c> para <c>TemplateGraphErrors</c>.</summary>
    Task<MessagingGraphResult<CreatedTemplate>> CreateTemplateAsync(MessagingTemplateSource source, JsonObject payload, CancellationToken cancellationToken);

    /// <summary>§4.4 (D-T11): <c>DELETE /{wabaId}/message_templates?hsm_id=&amp;name=</c>, sólo ese idioma. «No existe» cuenta como éxito con
    /// <c>AlreadyGone</c>.</summary>
    Task<MessagingGraphResult<DeletedTemplate>> DeleteTemplateAsync(MessagingTemplateSource source, string metaTemplateId, string name, CancellationToken cancellationToken);

    /// <summary>§4.5: la Resumable Upload del PDF de ejemplo con el <c>AppId</c> de <c>Meta:App</c>; devuelve el handle para
    /// <c>header_handle</c>. Sin <c>AppId</c> configurado falla con <c>no_app_id</c> sin llamar a Meta.</summary>
    Task<MessagingGraphResult<string>> UploadTemplateExampleAsync(
        MessagingTemplateSource source, ReadOnlyMemory<byte> content, string mimeType, string fileName, CancellationToken cancellationToken);
```

- [ ] **Step 4: `AppId` en las opciones**

En `MessagingMetaOptions`, después de `WebhookVerifyToken`:

```csharp
    /// <summary>Spec 2026-10-14 §4.5: la Resumable Upload va a <c>/{AppId}/uploads</c>. Es el mismo valor público que Integrations usa
    /// para el signup; Messaging lo bindea aparte porque no referencia su infraestructura.</summary>
    public string? AppId { get; set; }
```

Actualiza el `<summary>` de la clase para mencionarlo.

- [ ] **Step 5: El cliente**

En `WhatsAppCloudClient.cs`:

(a) `TemplateFields`:

```csharp
    /// <summary>Spec 2026-10-14 §4.4: <c>id</c> y <c>rejected_reason</c> alimentan el espejo.</summary>
    public const string TemplateFields = "id,name,language,status,category,components,parameter_format,rejected_reason";
```

(b) Después de `SendTemplateAsync`, los tres métodos nuevos y sus ayudantes:

```csharp
    public async Task<MessagingGraphResult<CreatedTemplate>> CreateTemplateAsync(MessagingTemplateSource source, JsonObject payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Version}/{Uri.EscapeDataString(source.WabaId)}/message_templates")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var answer = await SendAsync("template-create", request, source.AccessToken, null, cancellationToken);
        using var response = answer.Response;
        if (response is null)
        {
            return new MessagingGraphResult<CreatedTemplate>(null, Unreachable(answer));
        }

        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            return new MessagingGraphResult<CreatedTemplate>(null, FailureOf(status, answer));
        }

        try
        {
            using var document = JsonDocument.Parse(answer.Body);
            var root = document.RootElement;
            var id = Text(root, "id");
            if (string.IsNullOrEmpty(id) || id.Length > 64)
            {
                return new MessagingGraphResult<CreatedTemplate>(null, new MessagingGraphFailure(status, null, "no_id"));
            }

            var category = Text(root, "category") ?? payload["category"]?.GetValue<string>() ?? string.Empty;
            return new MessagingGraphResult<CreatedTemplate>(new CreatedTemplate(id, Text(root, "status") ?? "PENDING", category), null);
        }
        catch (JsonException)
        {
            return new MessagingGraphResult<CreatedTemplate>(null, new MessagingGraphFailure(status, null, "unreadable"));
        }
    }

    public async Task<MessagingGraphResult<DeletedTemplate>> DeleteTemplateAsync(
        MessagingTemplateSource source, string metaTemplateId, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"{Version}/{Uri.EscapeDataString(source.WabaId)}/message_templates?hsm_id={Uri.EscapeDataString(metaTemplateId)}&name={Uri.EscapeDataString(name)}");
        var answer = await SendAsync("template-delete", request, source.AccessToken, null, cancellationToken);
        using var response = answer.Response;
        if (response is null)
        {
            return new MessagingGraphResult<DeletedTemplate>(null, Unreachable(answer));
        }

        if (response.IsSuccessStatusCode)
        {
            return new MessagingGraphResult<DeletedTemplate>(new DeletedTemplate(false), null);
        }

        var status = (int)response.StatusCode;
        // D-T13: lo que Meta ya no tiene, tampoco lo tiene el espejo. Se reconoce por el texto: Graph no da un código propio para esto.
        if (status == 400 && Mentions(answer.Error, "does not exist"))
        {
            return new MessagingGraphResult<DeletedTemplate>(new DeletedTemplate(true), null);
        }

        return new MessagingGraphResult<DeletedTemplate>(null, FailureOf(status, answer));
    }

    public async Task<MessagingGraphResult<string>> UploadTemplateExampleAsync(
        MessagingTemplateSource source, ReadOnlyMemory<byte> content, string mimeType, string fileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var appId = options.Value.AppId?.Trim();
        if (string.IsNullOrEmpty(appId))
        {
            return new MessagingGraphResult<string>(null, new MessagingGraphFailure(0, null, "no_app_id"));
        }

        using var start = new HttpRequestMessage(
            HttpMethod.Post,
            $"{Version}/{Uri.EscapeDataString(appId)}/uploads?file_length={content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + $"&file_type={Uri.EscapeDataString(mimeType)}&file_name={Uri.EscapeDataString(fileName)}");
        var started = await SendAsync("template-upload-start", start, source.AccessToken, null, cancellationToken, UploadHttpClientName);
        using var startResponse = started.Response;
        if (startResponse is null)
        {
            return new MessagingGraphResult<string>(null, Unreachable(started));
        }

        var startStatus = (int)startResponse.StatusCode;
        if (!startResponse.IsSuccessStatusCode)
        {
            return new MessagingGraphResult<string>(null, FailureOf(startStatus, started));
        }

        var uploadId = ReadId(started.Body);
        if (string.IsNullOrEmpty(uploadId))
        {
            return new MessagingGraphResult<string>(null, new MessagingGraphFailure(startStatus, null, "no_id"));
        }

        // El id viaja en la ruta sin escapar (Graph lo documenta con los dos puntos literales); sólo se sigue si tiene la forma esperada.
        if (!UploadIdPattern().IsMatch(uploadId))
        {
            return new MessagingGraphResult<string>(null, new MessagingGraphFailure(startStatus, null, "invalid_upload_id"));
        }

        using var data = new HttpRequestMessage(HttpMethod.Post, $"{Version}/{uploadId}") { Content = new ReadOnlyMemoryContent(content) };
        data.Content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        data.Headers.TryAddWithoutValidation("file_offset", "0");
        // La Resumable Upload documenta «Authorization: OAuth {token}» para la subida de bytes, no «Bearer».
        var uploaded = await SendAsync("template-upload-data", data, source.AccessToken, null, cancellationToken, UploadHttpClientName, "OAuth");
        using var dataResponse = uploaded.Response;
        if (dataResponse is null)
        {
            return new MessagingGraphResult<string>(null, Unreachable(uploaded));
        }

        var dataStatus = (int)dataResponse.StatusCode;
        if (!dataResponse.IsSuccessStatusCode)
        {
            return new MessagingGraphResult<string>(null, FailureOf(dataStatus, uploaded));
        }

        try
        {
            using var document = JsonDocument.Parse(uploaded.Body);
            var handle = Text(document.RootElement, "h");
            return string.IsNullOrEmpty(handle)
                ? new MessagingGraphResult<string>(null, new MessagingGraphFailure(dataStatus, null, "no_handle"))
                : new MessagingGraphResult<string>(handle, null);
        }
        catch (JsonException)
        {
            return new MessagingGraphResult<string>(null, new MessagingGraphFailure(dataStatus, null, "unreadable"));
        }
    }

    private static MessagingGraphFailure Unreachable(GraphAnswer answer) => new(0, null, answer.UnreachableReason ?? "unreachable");

    private static MessagingGraphFailure FailureOf(int status, GraphAnswer answer) =>
        new(status, answer.Error?.Code, $"http_{status}", answer.Error?.UserTitle, answer.Error?.UserMessage);

    private static bool Mentions(GraphError? error, string text) =>
        error is not null
        && ((error.UserTitle ?? string.Empty).Contains(text, StringComparison.OrdinalIgnoreCase)
            || (error.UserMessage ?? string.Empty).Contains(text, StringComparison.OrdinalIgnoreCase));

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [System.Text.RegularExpressions.GeneratedRegex(@"^upload:[A-Za-z0-9_=:-]{1,512}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex UploadIdPattern();
```

(c) `SendAsync` gana el esquema de autorización como último parámetro opcional. Cambia la firma y la primera línea:

```csharp
    private async Task<GraphAnswer> SendAsync(
        string operation, HttpRequestMessage request, string accessToken, TimeSpan? timeout, CancellationToken cancellationToken,
        string clientName = HttpClientName, string scheme = "Bearer")
    {
        request.Headers.Authorization = new AuthenticationHeaderValue(scheme, accessToken);
```

(d) `GraphError` conserva `error_user_title` y `error_user_msg` por separado (el `Title` existente no cambia). Reemplaza el record y su `TryParse`:

```csharp
    /// <summary>Copia chica de <c>MetaGraphError</c> de Integrations (no se referencia): código, subcódigo, título y fbtrace; nunca
    /// <c>message</c>, que puede repetir lo que se mandó. Spec 2026-10-14 §4.8: <c>UserTitle</c> y <c>UserMessage</c> son
    /// <c>error_user_title</c> y <c>error_user_msg</c>, el texto que Meta escribe para la persona; sólo los leen las plantillas.</summary>
    private sealed record GraphError(int? Code, int? Subcode, string? Title, string? FbTraceId, string? UserTitle, string? UserMessage)
    {
        public static GraphError? TryParse(string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("error", out var error)
                    || error.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var userTitle = error.TryGetProperty("error_user_title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() : null;
                return new GraphError(
                    error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var c) ? c : null,
                    error.TryGetProperty("error_subcode", out var sub) && sub.ValueKind == JsonValueKind.Number && sub.TryGetInt32(out var s) ? s : null,
                    userTitle ?? (error.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null),
                    error.TryGetProperty("fbtrace_id", out var trace) && trace.ValueKind == JsonValueKind.String ? trace.GetString() : null,
                    userTitle,
                    error.TryGetProperty("error_user_msg", out var userMessage) && userMessage.ValueKind == JsonValueKind.String ? userMessage.GetString() : null);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
```

- [ ] **Step 6: El fake del test bed implementa la interfaz nueva**

En `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingTestBed.cs`, dentro de `FakeWhatsAppClient`, agrega `using System.Text.Json.Nodes;` arriba del archivo si no está y, junto a `NextTemplates`:

```csharp
    /// <summary>Spec 2026-10-14: cuántas veces se pidió el listado (el espejo debe pedirlo una vez por sincronización).</summary>
    public int TemplateListCalls { get; private set; }

    public MessagingGraphResult<CreatedTemplate> NextCreatedTemplate { get; set; } = new(new CreatedTemplate("meta-1", "PENDING", "MARKETING"), null);

    public List<JsonObject> CreatedTemplates { get; } = [];

    public MessagingGraphResult<DeletedTemplate> NextDelete { get; set; } = new(new DeletedTemplate(false), null);

    public List<(string MetaTemplateId, string Name)> Deleted { get; } = [];

    public MessagingGraphResult<string> NextHandle { get; set; } = new("4::handle", null);

    public List<(string MimeType, string FileName, int Length)> ExampleUploads { get; } = [];

    public Task<MessagingGraphResult<CreatedTemplate>> CreateTemplateAsync(MessagingTemplateSource source, JsonObject payload, CancellationToken cancellationToken)
    {
        CreatedTemplates.Add(payload);
        return Task.FromResult(NextCreatedTemplate);
    }

    public Task<MessagingGraphResult<DeletedTemplate>> DeleteTemplateAsync(MessagingTemplateSource source, string metaTemplateId, string name, CancellationToken cancellationToken)
    {
        Deleted.Add((metaTemplateId, name));
        return Task.FromResult(NextDelete);
    }

    public Task<MessagingGraphResult<string>> UploadTemplateExampleAsync(
        MessagingTemplateSource source, ReadOnlyMemory<byte> content, string mimeType, string fileName, CancellationToken cancellationToken)
    {
        ExampleUploads.Add((mimeType, fileName, content.Length));
        return Task.FromResult(NextHandle);
    }
```

Y en el `ListTemplatesAsync` del fake, agrega `TemplateListCalls++;` como primera línea del cuerpo.

- [ ] **Step 7: Correr y ver pasar**

```bash
dotnet build --no-restore 2>&1 | grep -E "error|Build succeeded" | head
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~WhatsAppCloudClient" 2>&1 | tail -5
```

Esperado: build sin errores (el fake y el cliente implementan todo); `Passed!` con las 11 pruebas nuevas más las existentes de `WhatsAppCloudClientTemplatesTests`.

- [ ] **Step 8: Commit**

```bash
git add src/Modules/Messaging tests/Modules/Messaging/Modules.Messaging.UnitTests
git commit -m "feat(messaging): el cliente de Graph crea, borra y sube el PDF de ejemplo de una plantilla"
```

---

### Task 5: El espejo — `TemplateGraphErrors`, `TemplateMirrorSync`, `MirroredTemplateCatalog`, y el catálogo viejo se va

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/TemplateGraphErrors.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/TemplateMirrorSync.cs`
- Delete: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Meta/MessageTemplateCatalog.cs`
- Delete: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessageTemplateCatalogTests.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs`
- Modify: `tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateTestDoubles.cs` (`FakeMessageTemplateRepository`, `RecordingUnitOfWork`)
- Modify: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/{CampaignApiTests,CampaignDispatchTests,SendTemplateApiTests,StartConversationApiTests,TemplateCatalogApiTests}.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateGraphErrorsTests.cs`, `tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateMirrorSyncTests.cs`

**Interfaces:**
- Consumes: `IMessageTemplateRepository` (Task 2), `IWhatsAppCloudClient.ListTemplatesAsync`, `MessagingGraphFailure.UserTitle/UserMessage` (Task 4), `TemplateCatalog.FromStored` (Task 3), `MetaErrorCodes.TokenExpired` (Domain).
- Produces: `TemplateGraphErrors.Translate(MessagingGraphFailure) → MessagingDomainException`, `TemplateGraphErrors.IsTokenRejected(MessagingGraphFailure) → bool`, `TemplateGraphErrors.FailAsync(IMessagingConnectionDirectory, Guid tenantId, Guid connectionId, MessagingGraphFailure, ct) → Task<MessagingDomainException>`; `TemplateMirrorSync.RunAsync(Guid tenantId, Guid connectionId, MessagingTemplateSource source, ct)`; `MirroredTemplateCatalog : IMessageTemplates`. En pruebas: `FakeMessageTemplateRepository` (`Rows`, `ActiveCampaignTemplates`, `LastSync`), `RecordingUnitOfWork` (`Saves`).

- [ ] **Step 1: Pruebas de la traducción de errores**

```csharp
// tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateGraphErrorsTests.cs
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-14 §4.8: de un <c>MessagingGraphFailure</c> al código de dominio, por <c>error_user_title</c>/<c>error_user_msg</c>
/// y nunca sólo por <c>code = 100</c>. Un 401 o un 190 además marca la conexión.</summary>
public sealed class TemplateGraphErrorsTests
{
    [Theory]
    [InlineData(0, null, null, null, MessagingErrorCodes.TemplatesUnavailable)]
    [InlineData(502, null, null, null, MessagingErrorCodes.TemplatesUnavailable)]
    [InlineData(401, 190, null, null, MessagingErrorCodes.ConnectionUnavailable)]
    [InlineData(400, 190, "Invalid OAuth access token", null, MessagingErrorCodes.ConnectionUnavailable)]
    [InlineData(400, 100, "Message Template Name Already Exists", "x", MessagingErrorCodes.TemplateNameTaken)]
    [InlineData(400, 100, "Invalid parameter", "A template with the same name and language already exists.", MessagingErrorCodes.TemplateNameTaken)]
    [InlineData(400, 100, "Template Limit Exceeded", "x", MessagingErrorCodes.TemplateLimitReached)]
    [InlineData(400, 100, "Invalid Format", "The body ends with a variable.", MessagingErrorCodes.TemplateRejectedByMeta)]
    [InlineData(400, 100, null, null, MessagingErrorCodes.TemplateRejectedByMeta)]
    public void EachGraphFailureHasItsCode(int status, int? code, string? title, string? message, string expected)
    {
        var exception = TemplateGraphErrors.Translate(new MessagingGraphFailure(status, code, $"http_{status}", title, message));
        Assert.Equal(expected, exception.Code);
    }

    [Fact]
    public void ARejectionCarriesMetasTextForThePerson()
    {
        var exception = TemplateGraphErrors.Translate(new MessagingGraphFailure(400, 100, "http_400", "Invalid Format", "The body ends with a variable."));
        Assert.Equal("WhatsApp no aceptó la plantilla: The body ends with a variable.", exception.Message);
    }

    [Fact]
    public void ARejectionWithoutTextsFallsBackToTheReason()
    {
        var exception = TemplateGraphErrors.Translate(new MessagingGraphFailure(400, 100, "http_400"));
        Assert.Equal("WhatsApp no aceptó la plantilla: http_400", exception.Message);
    }

    [Fact]
    public async Task ATokenRejectionReportsTheConnectionBeforeTranslating()
    {
        var connections = new FakeConnectionDirectory();
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();

        var exception = await TemplateGraphErrors.FailAsync(connections, tenantId, connectionId, new MessagingGraphFailure(401, 190, "http_401"), TestContext.Current.CancellationToken);

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, exception.Code);
        var report = Assert.Single(connections.Reported);
        Assert.Equal((tenantId, connectionId, "token_expired"), (report.TenantId, report.ConnectionId, report.FailureCode));
    }

    [Fact]
    public async Task AContentRejectionDoesNotTouchTheConnection()
    {
        var connections = new FakeConnectionDirectory();

        await TemplateGraphErrors.FailAsync(connections, Guid.CreateVersion7(), Guid.CreateVersion7(), new MessagingGraphFailure(400, 100, "http_400", "Invalid Format", "x"), TestContext.Current.CancellationToken);

        Assert.Empty(connections.Reported);
    }
}
```

> `FakeConnectionDirectory.Reported` es una lista de `RejectedReport`; mira su definición en `MessagingTestBed.cs` para los nombres de las propiedades (`TenantId`, `ConnectionId`, `FailureCode`) y ajusta la tupla si difieren.

- [ ] **Step 2: Correr y ver fallar**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~TemplateGraphErrorsTests" 2>&1 | tail -8
```

Esperado: `CS0103: The name 'TemplateGraphErrors' does not exist`.

- [ ] **Step 3: `TemplateGraphErrors`**

```csharp
// src/Modules/Messaging/Modules.Messaging.Application/TemplateGraphErrors.cs
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-14 §4.8: de una falla de Graph al código de dominio. Discrimina por <c>error_user_title</c> y <c>error_user_msg</c>,
/// nunca sólo por <c>code = 100</c> (y no por <c>error_subcode</c>, que Meta ya no devuelve): mismo criterio que el nombre de índice
/// en los <c>23505</c>. Vive en Application porque traduce un <see cref="MessagingGraphFailure"/> ya parseado y porque quien la llama
/// tiene que reaccionar al token vencido en el mismo lugar (<see cref="FailAsync"/>).
/// </summary>
public static class TemplateGraphErrors
{
    /// <summary>El motivo con el que la conexión pasa a <c>NeedsAttention</c>; literal de Integrations, que Messaging no referencia.</summary>
    public const string TokenExpiredReason = "token_expired";

    public static bool IsTokenRejected(MessagingGraphFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure.HttpStatus == 401 || failure.Code == MetaErrorCodes.TokenExpired;
    }

    public static MessagingDomainException Translate(MessagingGraphFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.HttpStatus == 0 || failure.HttpStatus >= 500)
        {
            return new MessagingDomainException(MessagingErrorCodes.TemplatesUnavailable, "WhatsApp did not answer the template operation.");
        }

        if (IsTokenRejected(failure))
        {
            return new MessagingDomainException(MessagingErrorCodes.ConnectionUnavailable, "WhatsApp rejected the connection credentials.");
        }

        var title = failure.UserTitle ?? string.Empty;
        var message = failure.UserMessage ?? string.Empty;
        if (Mentions(title, "already exist") || Mentions(message, "already exist"))
        {
            return new MessagingDomainException(MessagingErrorCodes.TemplateNameTaken, "Meta already has a template with that name in that language.");
        }

        if (Mentions(title, "limit"))
        {
            return new MessagingDomainException(MessagingErrorCodes.TemplateLimitReached, "The WhatsApp Business account reached its template limit.");
        }

        // El único message en español del módulo: la pantalla lo muestra tal cual, porque no hay diccionario para un rechazo arbitrario.
        var detail = message.Length > 0 ? message : title.Length > 0 ? title : failure.Reason;
        return new MessagingDomainException(MessagingErrorCodes.TemplateRejectedByMeta, $"WhatsApp no aceptó la plantilla: {detail}");
    }

    /// <summary>Lo que hace todo handler ante una falla de Graph en una operación de plantillas: si fue el token, reporta la conexión
    /// (igual que los envíos) y devuelve <c>connection_unavailable</c>; si no, la traducción.</summary>
    public static async Task<MessagingDomainException> FailAsync(
        IMessagingConnectionDirectory connections, Guid tenantId, Guid connectionId, MessagingGraphFailure failure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connections);
        if (IsTokenRejected(failure))
        {
            await connections.ReportRejectedAsync(tenantId, connectionId, TokenExpiredReason, cancellationToken);
        }

        return Translate(failure);
    }

    private static bool Mentions(string text, string fragment) => text.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 4: Ver pasar la traducción**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~TemplateGraphErrorsTests" 2>&1 | tail -5
```

Esperado: `Passed!` con 13 pruebas.

- [ ] **Step 5: Los dobles del espejo en pruebas unitarias**

Al final de `tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateTestDoubles.cs`:

```csharp
/// <summary>Spec 2026-10-14 §4.3: el espejo en memoria. Filtra por tenant y conexión como el real; el orden es ordinal.</summary>
internal sealed class FakeMessageTemplateRepository : IMessageTemplateRepository
{
    public List<MessageTemplate> Rows { get; } = [];

    public HashSet<(string Name, string Language)> ActiveCampaignTemplates { get; } = [];

    public Dictionary<(Guid TenantId, Guid ConnectionId), DateTimeOffset> Syncs { get; } = [];

    private IEnumerable<MessageTemplate> Of(Guid tenantId, Guid connectionId) =>
        Rows.Where(row => row.TenantId == tenantId && row.ConnectionId == connectionId);

    public Task<MessageTemplate?> FindAsync(Guid tenantId, Guid connectionId, Guid templateId, CancellationToken cancellationToken) =>
        Task.FromResult(Of(tenantId, connectionId).SingleOrDefault(row => row.Id == templateId));

    public Task<MessageTemplate?> FindByMetaIdAsync(Guid tenantId, Guid connectionId, string metaTemplateId, CancellationToken cancellationToken) =>
        Task.FromResult(Of(tenantId, connectionId).SingleOrDefault(row => row.MetaTemplateId == metaTemplateId));

    public Task<MessageTemplate?> FindByNameAsync(Guid tenantId, Guid connectionId, string name, string language, CancellationToken cancellationToken) =>
        Task.FromResult(Of(tenantId, connectionId).SingleOrDefault(row => row.Name == name && row.Language == language));

    public Task<MessageTemplate?> FindQuotationAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult(Of(tenantId, connectionId).FirstOrDefault(row => row.Kind == MessageTemplateKind.Quotation));

    public Task<IReadOnlyList<MessageTemplate>> ListAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MessageTemplate>>(Ordered(Of(tenantId, connectionId)));

    public Task<IReadOnlyList<MessageTemplate>> ListApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MessageTemplate>>(Ordered(Of(tenantId, connectionId).Where(row => row.IsApproved)));

    public void Add(MessageTemplate template) => Rows.Add(template);

    public void Remove(MessageTemplate template) => Rows.Remove(template);

    public Task<bool> IsReferencedByActiveCampaignAsync(Guid tenantId, Guid connectionId, string name, string language, CancellationToken cancellationToken) =>
        Task.FromResult(ActiveCampaignTemplates.Contains((name, language)));

    public Task<DateTimeOffset?> LastSyncAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult(Syncs.TryGetValue((tenantId, connectionId), out var at) ? at : (DateTimeOffset?)null);

    public Task MarkSyncedAsync(Guid tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Syncs[(tenantId, connectionId)] = now;
        return Task.CompletedTask;
    }

    private static MessageTemplate[] Ordered(IEnumerable<MessageTemplate> rows) =>
        rows.OrderBy(row => row.Name, StringComparer.Ordinal).ThenBy(row => row.Language, StringComparer.Ordinal).ToArray();
}

/// <summary>Cuenta los <c>SaveChanges</c>: el refresco guarda una sola vez.</summary>
internal sealed class RecordingUnitOfWork : IMessagingUnitOfWork
{
    public int Saves { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        Saves++;
        return Task.FromResult(1);
    }
}
```

> Si `MessagingTestBed.cs` ya define un doble de `IMessagingUnitOfWork` con otro nombre, úsalo y no agregues `RecordingUnitOfWork`; ajusta las pruebas de abajo al nombre existente.

- [ ] **Step 6: Pruebas del refresco y del catálogo espejo**

```csharp
// tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateMirrorSyncTests.cs
using System.Text.Json;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-14 §4.3 «Refrescar» y D-T8: el listado de Meta hace upsert por <c>id</c> en el espejo (conservando el kind), borra lo
/// que Meta ya no lista, salta lo que no tiene id o nombre, marca la sincronización y guarda una vez. El catálogo de envío sincroniza
/// sólo la primera vez o con refresh, y sirve las APPROVED del espejo.</summary>
public sealed class TemplateMirrorSyncTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ConnectionId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 15, 0, 0, TimeSpan.Zero);
    private static readonly MessagingTemplateSource Source = new("222", "token");

    private sealed record Rig(TemplateMirrorSync Sync, MirroredTemplateCatalog Catalog, FakeMessageTemplateRepository Repository, FakeWhatsAppClient Meta, FakeConnectionDirectory Connections, RecordingUnitOfWork UnitOfWork);

    private static Rig NewRig(params JsonElement[] listing)
    {
        var repository = new FakeMessageTemplateRepository();
        var meta = new FakeWhatsAppClient { NextTemplates = new MessagingGraphResult<IReadOnlyList<JsonElement>>(listing, null) };
        var connections = new FakeConnectionDirectory();
        var unitOfWork = new RecordingUnitOfWork();
        var sync = new TemplateMirrorSync(repository, meta, connections, unitOfWork, new FakeClock(Now));
        return new Rig(sync, new MirroredTemplateCatalog(repository, sync, connections), repository, meta, connections, unitOfWork);
    }

    private static JsonElement Listed(string id, string name, string status = "APPROVED", string? rejectedReason = null, string category = "UTILITY", string body = "Hola {{1}}") =>
        JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            id,
            name,
            language = "es",
            status,
            category,
            parameter_format = "POSITIONAL",
            rejected_reason = rejectedReason,
            components = new[] { new { type = "BODY", text = body } },
        })).RootElement.Clone();

    [Fact]
    public async Task ANewTemplateIsMirroredAsExternalWithMetasStatusAndReason()
    {
        var rig = NewRig(Listed("1", "promo", "REJECTED", "INVALID_FORMAT", "MARKETING"));

        await rig.Sync.RunAsync(TenantId, ConnectionId, Source, Ct);

        var row = Assert.Single(rig.Repository.Rows);
        Assert.Equal(("1", "promo", "es", "MARKETING", "POSITIONAL", "REJECTED", "INVALID_FORMAT", MessageTemplateKind.External, Now),
            (row.MetaTemplateId, row.Name, row.Language, row.Category, row.ParameterFormat, row.Status, row.StatusReason, row.Kind, row.SyncedAt));
        Assert.Contains("\"BODY\"", row.ComponentsJson, StringComparison.Ordinal);
        Assert.Equal(Now, rig.Repository.Syncs[(TenantId, ConnectionId)]);
        Assert.Equal(1, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task AKnownTemplateIsUpdatedAndKeepsItsKind()
    {
        var rig = NewRig(Listed("1", MessageTemplateName.QuotationName, "APPROVED", "NONE"));
        rig.Repository.Rows.Add(MessageTemplate.Pending(TenantId, ConnectionId, "1", MessageTemplateName.QuotationName, "es", "UTILITY", "NAMED", "PENDING", "[]",
            MessageTemplateKind.Quotation, Guid.CreateVersion7(), Now.AddDays(-1)));

        await rig.Sync.RunAsync(TenantId, ConnectionId, Source, Ct);

        var row = Assert.Single(rig.Repository.Rows);
        Assert.Equal((MessageTemplateKind.Quotation, "APPROVED", null, Now), (row.Kind, row.Status, row.StatusReason, row.SyncedAt));
    }

    [Fact]
    public async Task ATemplateMetaNoLongerListsIsRemoved()
    {
        var rig = NewRig(Listed("1", "queda"));
        rig.Repository.Rows.Add(MessageTemplate.Mirrored(TenantId, ConnectionId, "2", "se_fue", "es", "UTILITY", "POSITIONAL", "APPROVED", null, "[]", Now.AddDays(-1)));
        // La de otra conexión no se toca.
        rig.Repository.Rows.Add(MessageTemplate.Mirrored(TenantId, Guid.CreateVersion7(), "2", "ajena", "es", "UTILITY", "POSITIONAL", "APPROVED", null, "[]", Now));

        await rig.Sync.RunAsync(TenantId, ConnectionId, Source, Ct);

        Assert.Equal(["ajena", "queda"], rig.Repository.Rows.Select(row => row.Name).Order(StringComparer.Ordinal));
    }

    // Review Focus 3: una paginación que repite un id no puede insertar dos filas.
    [Fact]
    public async Task ADuplicatedIdInTheListingIsTakenOnce()
    {
        var rig = NewRig(Listed("1", "promo"), Listed("1", "promo"));

        await rig.Sync.RunAsync(TenantId, ConnectionId, Source, Ct);

        Assert.Single(rig.Repository.Rows);
    }

    [Fact]
    public async Task AnElementWithoutIdNameOrLanguageIsSkipped()
    {
        var withoutId = JsonDocument.Parse("""{"name":"x","language":"es","status":"APPROVED"}""").RootElement.Clone();
        var withoutName = JsonDocument.Parse("""{"id":"9","language":"es","status":"APPROVED"}""").RootElement.Clone();
        var notAnObject = JsonDocument.Parse("\"texto\"").RootElement.Clone();
        var rig = NewRig(withoutId, withoutName, notAnObject, Listed("1", "ok"));

        await rig.Sync.RunAsync(TenantId, ConnectionId, Source, Ct);

        Assert.Equal("ok", Assert.Single(rig.Repository.Rows).Name);
    }

    [Fact]
    public async Task AListingMetaDoesNotAnswerIsTemplatesUnavailableAndWritesNothing()
    {
        var rig = NewRig();
        rig.Meta.NextTemplates = new MessagingGraphResult<IReadOnlyList<JsonElement>>(null, new MessagingGraphFailure(502, null, "http_502"));

        var exception = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.Sync.RunAsync(TenantId, ConnectionId, Source, Ct));

        Assert.Equal(MessagingErrorCodes.TemplatesUnavailable, exception.Code);
        Assert.Equal(0, rig.UnitOfWork.Saves);
        Assert.Empty(rig.Repository.Syncs);
    }

    [Fact]
    public async Task ARejectedTokenMarksTheConnectionAndIsConnectionUnavailable()
    {
        var rig = NewRig();
        rig.Meta.NextTemplates = new MessagingGraphResult<IReadOnlyList<JsonElement>>(null, new MessagingGraphFailure(401, 190, "http_401"));

        var exception = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.Sync.RunAsync(TenantId, ConnectionId, Source, Ct));

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, exception.Code);
        Assert.Single(rig.Connections.Reported);
    }

    [Fact]
    public async Task TheCatalogSyncsOnlyTheFirstTimeAndThenServesTheMirror()
    {
        var rig = NewRig(Listed("1", "b"), Listed("2", "a"), Listed("3", "c", "PAUSED"));

        var first = await rig.Catalog.ListAsync(TenantId, ConnectionId, refresh: false, Ct);
        var second = await rig.Catalog.ListAsync(TenantId, ConnectionId, refresh: false, Ct);

        Assert.Equal(1, rig.Meta.TemplateListCalls);
        Assert.Equal(["a", "b"], first.Select(template => template.Name));
        Assert.Equal(["a", "b"], second.Select(template => template.Name));
        Assert.Equal(["1"], first[0].Parameters);
    }

    [Fact]
    public async Task RefreshAsksMetaAgain()
    {
        var rig = NewRig(Listed("1", "a"));
        await rig.Catalog.ListAsync(TenantId, ConnectionId, refresh: false, Ct);

        await rig.Catalog.ListAsync(TenantId, ConnectionId, refresh: true, Ct);

        Assert.Equal(2, rig.Meta.TemplateListCalls);
    }

    [Fact]
    public async Task AnUnavailableConnectionIs422WithoutTouchingMeta()
    {
        var rig = NewRig(Listed("1", "a"));
        rig.Connections.TemplateSource = null;

        var exception = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.Catalog.ListAsync(TenantId, ConnectionId, refresh: false, Ct));

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, exception.Code);
        Assert.Equal(0, rig.Meta.TemplateListCalls);
    }
}
```

- [ ] **Step 7: Correr y ver fallar**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~TemplateMirrorSyncTests" 2>&1 | tail -8
```

Esperado: `CS0246: The type or namespace name 'TemplateMirrorSync' could not be found`.

- [ ] **Step 8: `TemplateMirrorSync` y `MirroredTemplateCatalog`**

```csharp
// src/Modules/Messaging/Modules.Messaging.Application/TemplateMirrorSync.cs
using System.Globalization;
using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-14 §4.3 «Refrescar»: el listado completo de Meta hace upsert en el espejo por <c>meta_template_id</c> (<c>SyncFrom</c> en
/// las conocidas, <c>Mirrored</c> en las nuevas), elimina las filas que Meta ya no lista, escribe <c>template_mirror_syncs</c> y guarda
/// **una** vez. Un elemento sin <c>id</c>, nombre o idioma se salta; un <c>id</c> repetido en la misma lista se toma una vez (Review
/// Focus 3). Público para que Infrastructure lo registre; no es API para otros módulos.
/// </summary>
public sealed class TemplateMirrorSync(
    IMessageTemplateRepository repository,
    IWhatsAppCloudClient meta,
    IMessagingConnectionDirectory connections,
    IMessagingUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task RunAsync(Guid tenantId, Guid connectionId, MessagingTemplateSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = await meta.ListTemplatesAsync(source, cancellationToken);
        if (!result.Succeeded || result.Value is null)
        {
            throw await TemplateGraphErrors.FailAsync(
                connections, tenantId, connectionId, result.Failure ?? new MessagingGraphFailure(0, null, "unreachable"), cancellationToken);
        }

        var now = clock.UtcNow;
        var known = (await repository.ListAsync(tenantId, connectionId, cancellationToken)).ToDictionary(row => row.MetaTemplateId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in result.Value)
        {
            if (raw.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = Identifier(raw, "id");
            var name = Text(raw, "name");
            var language = Text(raw, "language");
            if (id is null || id.Length > MessageTemplate.MetaTemplateIdMaxLength || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(language) || !seen.Add(id))
            {
                continue;
            }

            var components = raw.TryGetProperty("components", out var list) && list.ValueKind == JsonValueKind.Array ? list.GetRawText() : "[]";
            var category = Text(raw, "category") ?? string.Empty;
            var format = Text(raw, "parameter_format") ?? "POSITIONAL";
            var status = Text(raw, "status");
            var reason = Text(raw, "rejected_reason");
            if (known.TryGetValue(id, out var existing))
            {
                existing.SyncFrom(status, reason, category, format, components, now);
            }
            else
            {
                repository.Add(MessageTemplate.Mirrored(tenantId, connectionId, id, name, language, category, format, status, reason, components, now));
            }
        }

        foreach (var (id, template) in known)
        {
            if (!seen.Contains(id))
            {
                repository.Remove(template);
            }
        }

        await repository.MarkSyncedAsync(tenantId, connectionId, now, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Meta manda los ids como string en el listado y como número en el webhook; se aceptan los dos.</summary>
    internal static string? Identifier(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var number) => number.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>
/// Spec 2026-10-14 D-T8: el catálogo de envío lee del espejo. Si <c>refresh</c> o la conexión nunca se sincronizó, corre
/// <see cref="TemplateMirrorSync"/> primero (el primer llenado es la única excepción a R8 del spec 2026-10-11: ocurre una vez por conexión).
/// La conexión se resuelve siempre: una pausada, borrada o de otro tenant no entrega plantillas aunque el espejo las tenga.
/// </summary>
public sealed class MirroredTemplateCatalog(IMessageTemplateRepository repository, TemplateMirrorSync sync, IMessagingConnectionDirectory connections) : IMessageTemplates
{
    public async Task<IReadOnlyList<MessageTemplateDto>> ListAsync(Guid tenantId, Guid connectionId, bool refresh, CancellationToken cancellationToken)
    {
        var source = await connections.ResolveTemplateSourceAsync(tenantId, connectionId, cancellationToken)
            ?? throw new MessagingDomainException(MessagingErrorCodes.ConnectionUnavailable, "The WhatsApp connection is paused, needs attention or was deleted.");
        if (refresh || await repository.LastSyncAsync(tenantId, connectionId, cancellationToken) is null)
        {
            await sync.RunAsync(tenantId, connectionId, source, cancellationToken);
        }

        var rows = await repository.ListApprovedAsync(tenantId, connectionId, cancellationToken);
        return rows
            .Select(row => TemplateCatalog.FromStored(row.Name, row.Language, row.Category, row.ParameterFormat, row.ComponentsJson))
            .OrderBy(template => template.Name, StringComparer.Ordinal)
            .ThenBy(template => template.Language, StringComparer.Ordinal)
            .ToArray();
    }
}
```

- [ ] **Step 9: Reemplazar el catálogo viejo y registrar el nuevo**

1. Borra `src/Modules/Messaging/Modules.Messaging.Infrastructure/Meta/MessageTemplateCatalog.cs` y `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessageTemplateCatalogTests.cs` (sus casos viven ahora en `TemplateMirrorSyncTests`).
2. En `MessagingInfrastructureExtensions.cs`, reemplaza `services.AddScoped<IMessageTemplates, MessageTemplateCatalog>();` por:

```csharp
        // Spec 2026-10-14 D-T8: el catálogo lee del espejo; el refresco es el mismo servicio que usa la pantalla de administración.
        services.AddScoped<TemplateMirrorSync>();
        services.AddScoped<IMessageTemplates, MirroredTemplateCatalog>();
```

Si `MessageTemplateCatalog` era el único usuario de `IMemoryCache`/`AddMemoryCache` en Messaging, deja el registro de `AddMemoryCache` igual (`WebhookRouting` también lo usa; compruébalo con `grep -rn IMemoryCache src/Modules/Messaging`).

- [ ] **Step 10: Ver pasar las unitarias y compilar todo**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~TemplateMirrorSyncTests" 2>&1 | tail -5
dotnet build --no-restore 2>&1 | grep -E " error |Build succeeded" | head
```

Esperado: `Passed!` con 10 pruebas; build sin errores.

- [ ] **Step 11: Los fixtures de integración llevan `id`, y las pruebas del catálogo hablan del espejo**

En cada uno de estos archivos, agrega `"id":"<n>"` a cada elemento de `data` del `Catalog` (un número distinto por elemento):

- `CampaignApiTests.cs` (`promo` → `"id":"1"`, `aviso` → `"id":"2"`).
- `CampaignDispatchTests.cs` (mismo `Catalog`: mira sus elementos y numéralos).
- `SendTemplateApiTests.cs` (mira sus elementos y numéralos).
- `StartConversationApiTests.cs` (`seguimiento` → `"id":"1"`).

En `TemplateCatalogApiTests.cs`:

- Cambia el `<summary>` de la clase: «…(sólo las aprobadas, normalizadas, **leídas del espejo local**, que se llena con el primer `GET` y se sincroniza con `refresh`)».
- Renombra `TheSecondReadIsCachedAndRefreshAsksMetaAgain` a `TheSecondReadComesFromTheMirrorAndRefreshAsksMetaAgain`; sus aserciones no cambian.
- En `OnlyTheApprovedTemplatesComeBackNormalizedAndMetaSeesTheRightRequest`, cambia la aserción de la query a `Assert.Contains("fields=id,name,language,status,category,components,parameter_format,rejected_reason", request.Uri.Query, StringComparison.Ordinal);` (justo antes del `limit=100`).
- Agrega esta prueba:

```csharp
    [Fact]
    public async Task OnceMirroredMetaBeingDownDoesNotEmptyTheCatalog()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, TemplatesPage);
        var url = $"/api/v1/tenants/{f.Tenant.TenantId}/messaging/connections/{f.ConnectionId}/templates";
        Assert.Equal(HttpStatusCode.OK, (await f.Client.GetAsync(url, Ct)).StatusCode);
        f.Factory.MetaHandler.Reset();
        f.Factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.BadGateway, "{}");

        var response = await f.Client.GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["promo", "seguimiento"], (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray().Select(item => item.GetProperty("name").GetString()));
        Assert.Equal(1, TemplateCalls(f.Factory.MetaHandler));
        Assert.Equal(3L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates"));
    }
```

- [ ] **Step 12: Regresión completa de las pruebas que pasaban por el catálogo**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~TemplateCatalogApiTests|FullyQualifiedName~CampaignApiTests|FullyQualifiedName~CampaignDispatchTests|FullyQualifiedName~SendTemplateApiTests|FullyQualifiedName~StartConversationApiTests" 2>&1 | tail -8
```

Esperado: todas en verde. Si una falla con `messaging.template.not_available` donde antes pasaba, el `Catalog` de ese archivo tiene un elemento sin `id`: míralo antes de tocar código de producción (CLAUDE.md, «el síntoma no se parece a la causa»).

- [ ] **Step 13: Commit**

```bash
git add -A src/Modules/Messaging tests/Modules/Messaging
git commit -m "feat(messaging): el catálogo de plantillas lee del espejo local sincronizado contra Meta"
```

---

### Task 6: Permiso, la pantalla (`GET`) y el refresco (`POST …/sync`)

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingPermissions.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (tres lugares)
- Create: `src/Modules/Messaging/Modules.Messaging.Application/TemplateManagement.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Api/TemplateManagementEndpoints.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs`
- Modify: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MessagingApiHarness.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/TemplateManagementApiTests.cs`

**Interfaces:**
- Consumes: `IMessageTemplateRepository`, `TemplateMirrorSync`, `TemplateCatalog.FromStored`, `ManagedTemplateDto`, `TemplateManagementDto`, `IMessagingMemberNames.FindAsync(Guid, IReadOnlyCollection<Guid>, ct)`, `IMessagingConnectionDirectory.{ResolveTemplateSourceAsync,ListActiveAsync}`.
- Produces: `MessagingPermissions.TemplateManage = "messaging.template.manage"`; `TemplateManagementSupport.ConnectionUnavailable()`, `TemplateManagementSupport.NotFound(Guid)`; `ManagedTemplateMapping.ToDto(MessageTemplate, IReadOnlyDictionary<Guid,string>)`; `TemplateManagementReader.ReadAsync(Guid tenantId, Guid connectionId, ct) → TemplateManagementDto`; `GetTemplateManagementQuery(Guid TenantId, Guid ConnectionId)`; `SyncTemplatesCommand(Guid TenantId, Guid ConnectionId)`; `TemplateManagementEndpoints.MapTemplateManagementEndpoints(RouteGroupBuilder)` con el grupo `/connections/{connectionId:guid}/template-management`; en el harness `TemplatePermissions` y `TemplateManagementUrl(Guid tenantId, Guid connectionId)`.

- [ ] **Step 1: Escribir las pruebas de integración de la pantalla**

```csharp
// tests/Modules/Messaging/Modules.Messaging.IntegrationTests/TemplateManagementApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-14 §4.7: <c>GET /connections/{id}/template-management</c> (la pantalla, con el primer llenado del espejo) y
/// <c>POST …/sync</c>, con sus 403 (sin permiso, otro tenant) y el 422 de una conexión ajena.</summary>
public sealed class TemplateManagementApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Admin);

    internal const string Listing = """
        {"data":[
          {"id":"1","name":"seguimiento","language":"es","status":"APPROVED","category":"UTILITY","parameter_format":"NAMED",
           "components":[{"type":"BODY","text":"Hola {{first_name}}, ¿sigues interesado?"}]},
          {"id":"2","name":"borrador","language":"es","status":"REJECTED","category":"MARKETING","parameter_format":"POSITIONAL","rejected_reason":"INVALID_FORMAT",
           "components":[{"type":"BODY","text":"x {{1}}"}]},
          {"id":"3","name":"en_revision","language":"es","status":"PENDING","category":"MARKETING","parameter_format":"NAMED","rejected_reason":"NONE",
           "components":[{"type":"BODY","text":"Pronto"}]}
        ]}
        """;

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, string listing = Listing)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        factory.MetaHandler.RespondWith("/222/message_templates", request =>
            request.Method == HttpMethod.Get ? FakeMetaGraphHandler.Json(HttpStatusCode.OK, listing) : FakeMetaGraphHandler.Json(HttpStatusCode.OK, "{}"));
        return new Fixture(factory, connectionString, tenant, connectionId, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, TemplatePermissions));
    }

    private static int Listings(FakeMetaGraphHandler meta) =>
        meta.Requests.Count(request => request.Method == HttpMethod.Get && request.Uri!.AbsolutePath.EndsWith("/222/message_templates", StringComparison.Ordinal));

    [Fact]
    public async Task TheFirstReadFillsTheMirrorAndShowsEveryStatusWithItsReason()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await f.Admin.GetAsync(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var screen = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("Ventas", "+57 300 123 4567"), (screen.GetProperty("connection").GetProperty("name").GetString(), screen.GetProperty("connection").GetProperty("displayPhoneNumber").GetString()));
        Assert.Equal(JsonValueKind.Null, screen.GetProperty("quotationTemplate").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, screen.GetProperty("lastSyncedAt").ValueKind);
        var templates = screen.GetProperty("templates").EnumerateArray().ToArray();
        Assert.Equal(["borrador", "en_revision", "seguimiento"], templates.Select(item => item.GetProperty("name").GetString()));
        Assert.Equal(["REJECTED", "PENDING", "APPROVED"], templates.Select(item => item.GetProperty("status").GetString()));
        Assert.Equal("INVALID_FORMAT", templates[0].GetProperty("statusReason").GetString());
        Assert.Equal(JsonValueKind.Null, templates[1].GetProperty("statusReason").ValueKind);
        Assert.All(templates, item => Assert.Equal("External", item.GetProperty("kind").GetString()));
        Assert.All(templates, item => Assert.Equal(JsonValueKind.Null, item.GetProperty("createdBy").ValueKind));
        Assert.Equal(["first_name"], templates[2].GetProperty("parameters").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("Named", templates[2].GetProperty("parameterFormat").GetString());
        Assert.True(templates[2].GetProperty("sendable").GetBoolean());
        Assert.Equal("2", templates[0].GetProperty("metaTemplateId").GetString());
        Assert.DoesNotContain(SentinelMetaAccessToken, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(3L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));
    }

    [Fact]
    public async Task ASecondReadServesTheMirrorAndSyncAsksMetaAgainDroppingWhatIsGone()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var url = TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId);
        await f.Admin.GetAsync(url, Ct);
        await f.Admin.GetAsync(url, Ct);
        Assert.Equal(1, Listings(f.Factory.MetaHandler));
        f.Factory.MetaHandler.Reset();
        f.Factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK,
            """{"data":[{"id":"1","name":"seguimiento","language":"es","status":"PAUSED","category":"UTILITY","parameter_format":"NAMED","components":[{"type":"BODY","text":"Hola {{first_name}}"}]}]}""");

        var response = await SendAsync(f.Admin, HttpMethod.Post, $"{url}/sync");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, Listings(f.Factory.MetaHandler));
        var templates = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("templates").EnumerateArray().ToArray();
        var only = Assert.Single(templates);
        Assert.Equal(("seguimiento", "PAUSED"), (only.GetProperty("name").GetString(), only.GetProperty("status").GetString()));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));
    }

    [Fact]
    public async Task WithoutThePermissionOrFromAnotherTenantIs403AndNeverReachesMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        using var manager = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions);
        using var intruder = CreateClient(f.Factory, f.Tenant.OwnerUserId, Guid.CreateVersion7(), TemplatePermissions);
        var url = TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId);

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(manager, HttpMethod.Post, $"{url}/sync")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await intruder.GetAsync(url, Ct)).StatusCode);
        Assert.Equal(0, Listings(f.Factory.MetaHandler));
    }

    [Fact]
    public async Task AConnectionOfAnotherTenantIs422ConnectionUnavailableNot404()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await f.Admin.GetAsync(TemplateManagementUrl(f.Tenant.TenantId, Guid.CreateVersion7()), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.connection_unavailable", (await ProblemAsync(response)).Code);
    }

    [Fact]
    public async Task AListingMetaDoesNotAnswerOnTheFirstReadIs422TemplatesUnavailable()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        f.Factory.MetaHandler.Reset();
        f.Factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.BadGateway, "{}");

        var response = await f.Admin.GetAsync(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.templates.unavailable", (await ProblemAsync(response)).Code);
    }
}
```

- [ ] **Step 2: Agregar al harness el permiso y la URL**

En `MessagingApiHarness.cs`, junto a `CampaignPermissions`:

```csharp
    /// <summary>Spec 2026-10-14 D-T5: sólo el admin de fábrica gestiona plantillas.</summary>
    public static readonly string[] TemplatePermissions = [MessagingPermissions.TemplateManage];
```

Junto a `AssigneesUrl`:

```csharp
    public static string TemplateManagementUrl(Guid tenantId, Guid connectionId) =>
        $"/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management";
```

- [ ] **Step 3: Correr y ver fallar**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~TemplateManagementApiTests" 2>&1 | tail -8
```

Esperado: `CS0117: 'MessagingPermissions' does not contain a definition for 'TemplateManage'`.

- [ ] **Step 4: El permiso, en sus cuatro lugares**

`MessagingPermissions.cs`, después de `QuickReplyManage`:

```csharp
    /// <summary>Spec 2026-10-14 D-T5: crear, aprovisionar, sincronizar y borrar las plantillas de WhatsApp de una conexión. Leer el
    /// catálogo de envío no lo pide (basta <see cref="ConversationRead"/>). De núcleo y de fábrica sólo en <c>admin</c>: lo que la empresa
    /// manda en su nombre y lo que Meta cobra lo decide quien administra, como <see cref="CampaignManage"/>.</summary>
    public const string TemplateManage = "messaging.template.manage";
```

`src/Bootstrapper/QepServiceCollectionExtensions.cs`:

1. En el rol de sistema `admin`, después de `MessagingPermissions.QuickReplyManage,` (línea ~941):

```csharp
                // Spec 2026-10-14 D-T5: las plantillas de WhatsApp se gestionan desde QEP; de fábrica sólo en admin.
                MessagingPermissions.TemplateManage,
```

2. En el catálogo de definiciones, después de la `PermissionDefinition` de `QuickReplyManage` (busca `MessagingPermissions.QuickReplyManage,` dentro de `new PermissionDefinition(`):

```csharp
        // Spec 2026-10-14 §4.6: núcleo como las demás de Messaging; el módulo lo revisa cada handler.
        services.AddSingleton(new PermissionDefinition(
            MessagingPermissions.TemplateManage,
            "Gestionar plantillas de WhatsApp",
            "Permite crear, sincronizar y borrar las plantillas de WhatsApp de una conexión y aprovisionar la plantilla de cotización; cada plantilla pasa por la revisión de Meta.",
            "Messaging",
            "high",
            RequiredModules: []));
```

3. En `AddAuthorization`, después de la política de `QuickReplyManage`:

```csharp
            // Spec 2026-10-14: sin esta política, RequireAuthorization(TemplateManage) no resuelve y el síntoma es 500, no 403 (CLAUDE.md).
            .AddPolicy(
                MessagingPermissions.TemplateManage,
                policy => AddPermissionRequirement(policy, MessagingPermissions.TemplateManage))
```

- [ ] **Step 5: Reader, mapping y los dos handlers**

```csharp
// src/Modules/Messaging/Modules.Messaging.Application/TemplateManagement.cs
using BuildingBlocks.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

// Spec 2026-10-14 §4.3: la administración de plantillas. Todo handler revalida tenant, permiso y módulo antes de tocar nada (403 con
// código, nunca 404 ante otro tenant) y resuelve la conexión con ResolveTemplateSourceAsync (null = connection_unavailable, 422).

internal static class TemplateManagementSupport
{
    public static MessagingDomainException ConnectionUnavailable() =>
        new(MessagingErrorCodes.ConnectionUnavailable, "The WhatsApp connection is paused, needs attention or was deleted.");

    public static ResourceNotFoundException NotFound(Guid templateId) =>
        new(MessagingErrorCodes.TemplateNotFound, $"Template '{templateId}' was not found in this connection.");
}

/// <summary>§4.3: una fila del espejo al DTO de la pantalla, con el texto resuelto por <see cref="TemplateCatalog.FromStored"/>.</summary>
internal static class ManagedTemplateMapping
{
    public static ManagedTemplateDto ToDto(MessageTemplate template, IReadOnlyDictionary<Guid, string> memberNames)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(memberNames);
        var described = TemplateCatalog.FromStored(template.Name, template.Language, template.Category, template.ParameterFormat, template.ComponentsJson);
        return new ManagedTemplateDto(
            template.Id,
            template.MetaTemplateId,
            template.Name,
            template.Language,
            template.Category,
            described.ParameterFormat,
            template.Status,
            template.StatusReason,
            template.Kind.ToString(),
            described.Header,
            described.Body,
            described.Footer,
            described.Buttons,
            described.Parameters,
            described.Sendable,
            described.UnsupportedReason,
            template.CreatedByMemberId is { } member ? new MemberRefDto(member, memberNames.GetValueOrDefault(member) ?? MessageMapping.DeletedMemberName) : null,
            template.CreatedAt,
            template.UpdatedAt);
    }
}

/// <summary>§4.3: arma <see cref="TemplateManagementDto"/> con una llamada a cada directorio. Público sólo para que Infrastructure lo
/// registre; no es API para otros módulos.</summary>
public sealed class TemplateManagementReader(IMessageTemplateRepository repository, IMessagingConnectionDirectory connections, IMessagingMemberNames memberNames)
{
    public async Task<TemplateManagementDto> ReadAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = (await connections.ListActiveAsync(tenantId, cancellationToken)).FirstOrDefault(active => active.Id == connectionId)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        var templates = await repository.ListAsync(tenantId, connectionId, cancellationToken);
        var creators = templates.Where(template => template.CreatedByMemberId is not null).Select(template => template.CreatedByMemberId!.Value).Distinct().ToArray();
        var names = creators.Length == 0
            ? new Dictionary<Guid, string>()
            : await memberNames.FindAsync(tenantId, creators, cancellationToken);
        var quotation = templates.FirstOrDefault(template => template.Kind == MessageTemplateKind.Quotation);
        return new TemplateManagementDto(
            connection,
            quotation is null ? null : new QuotationTemplateStatusDto(quotation.Id, quotation.Status, quotation.StatusReason),
            await repository.LastSyncAsync(tenantId, connectionId, cancellationToken),
            templates.Select(template => ManagedTemplateMapping.ToDto(template, names)).ToArray());
    }
}

/// <summary>§4.7 <c>GET /connections/{connectionId}/template-management</c>. Si la conexión nunca se sincronizó, la primera lectura llena el
/// espejo (igual que el catálogo de envío): la pantalla no puede mostrar «cero plantillas» de un WABA que tiene veinte.</summary>
public sealed record GetTemplateManagementQuery(Guid TenantId, Guid ConnectionId) : IQuery<TemplateManagementDto>;

public sealed class GetTemplateManagementHandler(
    TemplateManagementReader reader,
    IMessageTemplateRepository repository,
    TemplateMirrorSync sync,
    IMessagingConnectionDirectory connections,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : IQueryHandler<GetTemplateManagementQuery, TemplateManagementDto>
{
    public async Task<TemplateManagementDto> HandleAsync(GetTemplateManagementQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        var source = await connections.ResolveTemplateSourceAsync(query.TenantId, query.ConnectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        if (await repository.LastSyncAsync(query.TenantId, query.ConnectionId, cancellationToken) is null)
        {
            await sync.RunAsync(query.TenantId, query.ConnectionId, source, cancellationToken);
        }

        return await reader.ReadAsync(query.TenantId, query.ConnectionId, cancellationToken);
    }
}

/// <summary>§4.7 <c>POST …/template-management/sync</c>: sincroniza contra Meta y devuelve la pantalla.</summary>
public sealed record SyncTemplatesCommand(Guid TenantId, Guid ConnectionId) : ICommand<TemplateManagementDto>;

public sealed class SyncTemplatesHandler(
    TemplateManagementReader reader,
    TemplateMirrorSync sync,
    IMessagingConnectionDirectory connections,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : ICommandHandler<SyncTemplatesCommand, TemplateManagementDto>
{
    public async Task<TemplateManagementDto> HandleAsync(SyncTemplatesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        var source = await connections.ResolveTemplateSourceAsync(command.TenantId, command.ConnectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        await sync.RunAsync(command.TenantId, command.ConnectionId, source, cancellationToken);
        return await reader.ReadAsync(command.TenantId, command.ConnectionId, cancellationToken);
    }
}
```

Registro en `MessagingInfrastructureExtensions.cs`, junto al de `TemplateMirrorSync`:

```csharp
        services.AddScoped<TemplateManagementReader>();
```

(Los handlers los descubre el registro de handlers del módulo igual que los demás; comprueba cómo se registran `ListQuickRepliesHandler` y compañía —si es por `AddHandlersFromAssembly` o uno por uno— y haz lo mismo.)

- [ ] **Step 6: Los endpoints**

```csharp
// src/Modules/Messaging/Modules.Messaging.Api/TemplateManagementEndpoints.cs
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Messaging.Application;

namespace Modules.Messaging.Api;

/// <summary>
/// Spec 2026-10-14 §4.7: la administración de plantillas de una conexión, bajo
/// <c>/api/v1/tenants/{tenantId:guid}/messaging/connections/{connectionId:guid}/template-management</c>, toda con
/// <c>messaging.template.manage</c>. Los handlers revalidan tenant, permiso, módulo y conexión (403 con código, nunca 404 ante otro tenant;
/// 422 <c>connection_unavailable</c> ante una conexión ajena).
/// </summary>
internal static class TemplateManagementEndpoints
{
    public static void MapTemplateManagementEndpoints(this RouteGroupBuilder messaging)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        var group = messaging.MapGroup("/connections/{connectionId:guid}/template-management");

        group.MapGet("/", GetAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Produces<TemplateManagementDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/sync", SyncAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Produces<TemplateManagementDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> GetAsync(Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetTemplateManagementQuery(tenantId, connectionId), cancellationToken));

    private static async Task<IResult> SyncAsync(Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new SyncTemplatesCommand(tenantId, connectionId), cancellationToken));
}
```

En `MessagingEndpoints.MapMessagingEndpoints`, junto a `group.MapQuickReplyEndpoints();`:

```csharp
        group.MapTemplateManagementEndpoints();
```

- [ ] **Step 7: Correr y ver pasar**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~TemplateManagementApiTests" 2>&1 | tail -5
```

Esperado: `Passed!` con 5 pruebas. Un 500 en vez de 403 en la tercera significa que falta la política de `AddAuthorization` (CLAUDE.md).

- [ ] **Step 8: Commit**

```bash
git add src/Bootstrapper src/Modules/Messaging tests/Modules/Messaging
git commit -m "feat(messaging): pantalla de plantillas de una conexión y su refresco contra Meta, con el permiso template.manage"
```

---

### Task 7: Crear una plantilla libre — validador, constructor de componentes, handler, auditoría y endpoint

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/TemplateComponentsBuilder.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/TemplateManagement.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingAuditRecorder.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingAuditRecorder.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/TemplateManagementEndpoints.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateComponentsBuilderTests.cs`, `tests/Modules/Messaging/Modules.Messaging.UnitTests/CreateTemplateCommandValidatorTests.cs`, `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/TemplateManagementCreateApiTests.cs`

**Interfaces:**
- Consumes: `IWhatsAppCloudClient.CreateTemplateAsync` (Task 4), `TemplateGraphErrors.FailAsync` (Task 5), `IMessageTemplateRepository`, `CallerMembership.FindAsync(Guid tenantId, ct) → Guid?`, `IMessagingMemberNames.FindDisplayNameAsync`.
- Produces: `TemplateButtonInput(string? Type, string? Text, string? Url, string? PhoneNumber)`; `TemplateDraft(string Name, string Language, string Category, string? Header, string Body, string? Footer, IReadOnlyList<TemplateButtonInput> Buttons, IReadOnlyDictionary<string, string> Examples)`; `TemplateComponentsBuilder.{Named, VariablesOf(string body), Components(TemplateDraft draft, string? documentHeaderHandle = null) → JsonArray, CreatePayload(TemplateDraft draft, string? documentHeaderHandle = null) → JsonObject}`; `CreateTemplateCommand(Guid TenantId, Guid ConnectionId, string? Name, string? Language, string? Category, string? Header, string? Body, string? Footer, IReadOnlyList<TemplateButtonInput>? Buttons, IReadOnlyDictionary<string, string?>? Examples) : ICommand<ManagedTemplateDto>`; `CreateTemplateCommandValidator`; `TemplateMessages`; `IMessagingAuditRecorder.RecordTemplate(Guid tenantId, Guid actorId, string action, Guid templateId, DateTimeOffset occurredAt)`; `MessagingAuditActions.{TemplateResourceType = "message_template", TemplateCreated = "messaging.template.created", TemplateQuotationProvisioned = "messaging.template.quotation_provisioned", TemplateDeleted = "messaging.template.deleted"}`; en Api `CreateTemplateRequest`, `TemplateButtonRequest`.

- [ ] **Step 1: Pruebas unitarias del constructor de componentes**

```csharp
// tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateComponentsBuilderTests.cs
using System.Text.Json;
using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-14 §4.4 «Crear»: el payload de <c>POST /{waba}/message_templates</c>. Variables con nombre en orden de aparición con
/// su ejemplo, encabezado y pie opcionales, los tres tipos de botón, y el encabezado de documento con <c>header_handle</c> (§4.5).</summary>
public sealed class TemplateComponentsBuilderTests
{
    private static TemplateDraft Draft(
        string body = "Hola {{first_name}}, {{product}} tiene {{discount}} hoy. Saludos, {{first_name}}.",
        string? header = "Oferta de octubre",
        string? footer = "Responde DETENER para no recibir promociones.",
        IReadOnlyList<TemplateButtonInput>? buttons = null) =>
        new("promo_octubre", "es", "MARKETING", header, body, footer,
            buttons ?? [new TemplateButtonInput("QuickReply", "Detener promociones", null, null), new TemplateButtonInput("Url", "Ver catálogo", "https://ejemplo.co/catalogo", null), new TemplateButtonInput("PhoneNumber", "Llamar", null, "+573001234567")],
            new Dictionary<string, string> { ["first_name"] = "Ana", ["product"] = "Silla Nova", ["discount"] = "20 %" });

    private static JsonElement Element(System.Text.Json.Nodes.JsonNode node) => JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

    [Fact]
    public void VariablesComeOutInOrderOfAppearanceWithoutRepeats() =>
        Assert.Equal(["first_name", "product", "discount"], TemplateComponentsBuilder.VariablesOf(Draft().Body));

    [Fact]
    public void ThePayloadCarriesNameLanguageCategoryNamedFormatAndComponents()
    {
        var payload = Element(TemplateComponentsBuilder.CreatePayload(Draft()));

        Assert.Equal(("promo_octubre", "es", "MARKETING", "NAMED"), (
            payload.GetProperty("name").GetString(), payload.GetProperty("language").GetString(), payload.GetProperty("category").GetString(), payload.GetProperty("parameter_format").GetString()));
        Assert.Equal(["HEADER", "BODY", "FOOTER", "BUTTONS"], payload.GetProperty("components").EnumerateArray().Select(component => component.GetProperty("type").GetString()));
        Assert.False(payload.TryGetProperty("allow_category_change", out _));
    }

    [Fact]
    public void TheBodyCarriesOneNamedExamplePerVariable()
    {
        var components = Element(TemplateComponentsBuilder.Components(Draft())).EnumerateArray().ToArray();
        var body = components.Single(component => component.GetProperty("type").GetString() == "BODY");

        Assert.Equal(Draft().Body, body.GetProperty("text").GetString());
        var examples = body.GetProperty("example").GetProperty("body_text_named_params").EnumerateArray().ToArray();
        Assert.Equal(["first_name|Ana", "product|Silla Nova", "discount|20 %"], examples.Select(example => $"{example.GetProperty("param_name").GetString()}|{example.GetProperty("example").GetString()}"));
    }

    [Fact]
    public void ABodyWithoutVariablesCarriesNoExample()
    {
        var components = Element(TemplateComponentsBuilder.Components(Draft(body: "Hola, ya abrimos.", header: null, footer: null, buttons: []))).EnumerateArray().ToArray();

        var body = Assert.Single(components);
        Assert.Equal("BODY", body.GetProperty("type").GetString());
        Assert.False(body.TryGetProperty("example", out _));
    }

    [Fact]
    public void TheTextHeaderAndFooterAreOptional()
    {
        var components = Element(TemplateComponentsBuilder.Components(Draft(header: null, footer: null, buttons: []))).EnumerateArray().ToArray();
        Assert.Equal(["BODY"], components.Select(component => component.GetProperty("type").GetString()));

        var withHeader = Element(TemplateComponentsBuilder.Components(Draft(buttons: []))).EnumerateArray().ToArray();
        Assert.Equal(("HEADER", "TEXT", "Oferta de octubre"), (withHeader[0].GetProperty("type").GetString(), withHeader[0].GetProperty("format").GetString(), withHeader[0].GetProperty("text").GetString()));
        Assert.Equal(("FOOTER", "Responde DETENER para no recibir promociones."), (withHeader[2].GetProperty("type").GetString(), withHeader[2].GetProperty("text").GetString()));
    }

    [Fact]
    public void EachButtonTypeMapsToMetasNameAndCarriesOnlyItsField()
    {
        var buttons = Element(TemplateComponentsBuilder.Components(Draft())).EnumerateArray().Single(component => component.GetProperty("type").GetString() == "BUTTONS")
            .GetProperty("buttons").EnumerateArray().ToArray();

        Assert.Equal(["QUICK_REPLY", "URL", "PHONE_NUMBER"], buttons.Select(button => button.GetProperty("type").GetString()));
        Assert.Equal("Detener promociones", buttons[0].GetProperty("text").GetString());
        Assert.False(buttons[0].TryGetProperty("url", out _));
        Assert.Equal("https://ejemplo.co/catalogo", buttons[1].GetProperty("url").GetString());
        Assert.Equal("+573001234567", buttons[2].GetProperty("phone_number").GetString());
        Assert.False(buttons[2].TryGetProperty("url", out _));
    }

    [Fact]
    public void ADocumentHeaderReplacesTheTextHeaderAndCarriesTheHandle()
    {
        var components = Element(TemplateComponentsBuilder.Components(Draft(buttons: []), documentHeaderHandle: "4::handle")).EnumerateArray().ToArray();

        var header = components[0];
        Assert.Equal(("HEADER", "DOCUMENT"), (header.GetProperty("type").GetString(), header.GetProperty("format").GetString()));
        Assert.False(header.TryGetProperty("text", out _));
        Assert.Equal(["4::handle"], header.GetProperty("example").GetProperty("header_handle").EnumerateArray().Select(handle => handle.GetString()));
    }
}
```

- [ ] **Step 2: Pruebas unitarias del validador**

```csharp
// tests/Modules/Messaging/Modules.Messaging.UnitTests/CreateTemplateCommandValidatorTests.cs
using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-14 §4.8: cada campo del cuerpo de crear una plantilla libre con su clave en <c>errors</c>. Lo que Meta valida y
/// nosotros no (proporción de variables, variable al final) no está acá.</summary>
public sealed class CreateTemplateCommandValidatorTests
{
    private static readonly CreateTemplateCommandValidator Validator = new();

    private static CreateTemplateCommand Valid(
        string? name = "promo_octubre", string? language = "es", string? category = "MARKETING", string? header = "Oferta",
        string? body = "Hola {{first_name}}, {{product}} con descuento.", string? footer = "QEP",
        IReadOnlyList<TemplateButtonInput>? buttons = null, IReadOnlyDictionary<string, string?>? examples = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), name, language, category, header, body, footer,
            buttons ?? [new TemplateButtonInput("QuickReply", "Detener", null, null)],
            examples ?? new Dictionary<string, string?> { ["first_name"] = "Ana", ["product"] = "Silla" });

    private static string[] Keys(CreateTemplateCommand command) =>
        Validator.Validate(command).Errors.Select(error => error.PropertyName).Distinct().Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void AValidCommandHasNoErrors() => Assert.Empty(Keys(Valid()));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Promo Octubre")]
    [InlineData("promo-octubre")]
    [InlineData("qep_cotizacion")]
    [InlineData("qep_cotizacion_v2")]
    public void TheNameIsLowercaseDigitsUnderscoresAndNotReserved(string? name) => Assert.Equal(["name"], Keys(Valid(name: name)));

    [Theory]
    [InlineData(null)]
    [InlineData("ES")]
    [InlineData("es-CO")]
    [InlineData("español")]
    public void TheLanguageHasTheShapeEsOrEsCo(string? language) => Assert.Equal(["language"], Keys(Valid(language: language)));

    [Theory]
    [InlineData("es")]
    [InlineData("es_CO")]
    [InlineData("en_US")]
    public void TheseLanguagesAreValid(string language) => Assert.Empty(Keys(Valid(language: language)));

    [Theory]
    [InlineData(null)]
    [InlineData("AUTHENTICATION")]
    [InlineData("marketing")]
    public void TheCategoryIsMarketingOrUtility(string? category) => Assert.Equal(["category"], Keys(Valid(category: category)));

    [Fact]
    public void TheHeaderIsShortAndHasNoVariables()
    {
        Assert.Equal(["header"], Keys(Valid(header: new string('a', 61))));
        Assert.Equal(["header"], Keys(Valid(header: "Hola {{first_name}}")));
        Assert.Empty(Keys(Valid(header: null)));
    }

    [Fact]
    public void TheFooterIsShort() => Assert.Equal(["footer"], Keys(Valid(footer: new string('a', 61))));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TheBodyIsRequired(string? body) => Assert.Equal(["body"], Keys(Valid(body: body, examples: new Dictionary<string, string?>())));

    [Fact]
    public void TheBodyIsAtMost1024() => Assert.Equal(["body"], Keys(Valid(body: new string('a', 1025), examples: new Dictionary<string, string?>())));

    [Fact]
    public void ABodyMadeOnlyOfVariablesIsRejected() =>
        Assert.Equal(["body"], Keys(Valid(body: "{{first_name}}{{product}}")));

    // Review Focus 4: Meta rechaza variables con mayúsculas o espacios; se marca en body antes de llegar a Meta.
    [Theory]
    [InlineData("Hola {{ Nombre }}")]
    [InlineData("Hola {{first name}}")]
    [InlineData("Hola {{First_Name}}")]
    public void AVariableWithUppercaseOrSpacesIsRejectedInBody(string body) =>
        Assert.Contains("body", Keys(Valid(body: body, examples: new Dictionary<string, string?>())));

    [Fact]
    public void ExamplesMustMatchTheVariablesExactly()
    {
        Assert.Equal(["examples"], Keys(Valid(examples: new Dictionary<string, string?> { ["first_name"] = "Ana" })));
        Assert.Equal(["examples"], Keys(Valid(examples: new Dictionary<string, string?> { ["first_name"] = "Ana", ["product"] = "Silla", ["extra"] = "x" })));
        Assert.Equal(["examples"], Keys(Valid(examples: new Dictionary<string, string?> { ["first_name"] = "Ana", ["product"] = "" })));
        Assert.Equal(["examples"], Keys(Valid(examples: null)));
    }

    [Fact]
    public void AtMostTenButtonsEachWithAShortText()
    {
        var eleven = Enumerable.Range(0, 11).Select(index => new TemplateButtonInput("QuickReply", $"b{index}", null, null)).ToArray();
        Assert.Equal(["buttons"], Keys(Valid(buttons: eleven)));
        Assert.Equal(["buttons[0].text"], Keys(Valid(buttons: [new TemplateButtonInput("QuickReply", new string('a', 26), null, null)])));
        Assert.Equal(["buttons[0].text"], Keys(Valid(buttons: [new TemplateButtonInput("QuickReply", "", null, null)])));
        Assert.Equal(["buttons[0].type"], Keys(Valid(buttons: [new TemplateButtonInput("Flow", "x", null, null)])));
    }

    [Fact]
    public void AUrlButtonNeedsAnAbsoluteHttpsUrlAndAPhoneButtonAPhone()
    {
        Assert.Equal(["buttons[0].url"], Keys(Valid(buttons: [new TemplateButtonInput("Url", "Ver", "http://ejemplo.co", null)])));
        Assert.Equal(["buttons[0].url"], Keys(Valid(buttons: [new TemplateButtonInput("Url", "Ver", null, null)])));
        Assert.Equal(["buttons[0].url"], Keys(Valid(buttons: [new TemplateButtonInput("Url", "Ver", "https://ejemplo.co/{{1}}", null)])));
        Assert.Equal(["buttons[0].phoneNumber"], Keys(Valid(buttons: [new TemplateButtonInput("PhoneNumber", "Llamar", null, "300 123")])));
        Assert.Empty(Keys(Valid(buttons: [new TemplateButtonInput("PhoneNumber", "Llamar", null, "+573001234567")])));
    }

    [Fact]
    public void QuickRepliesAreContiguousAtTheStartOrTheEnd()
    {
        static TemplateButtonInput Quick(string text) => new("QuickReply", text, null, null);
        static TemplateButtonInput Link(string text) => new("Url", text, "https://ejemplo.co", null);

        Assert.Empty(Keys(Valid(buttons: [Quick("a"), Quick("b"), Link("c")])));
        Assert.Empty(Keys(Valid(buttons: [Link("a"), Quick("b"), Quick("c")])));
        Assert.Equal(["buttons"], Keys(Valid(buttons: [Quick("a"), Link("b"), Quick("c")])));
    }

    [Fact]
    public void ErrorsAreInSpanishAndTutean()
    {
        var error = Validator.Validate(Valid(name: "Promo")).Errors.Single();
        Assert.Equal(TemplateMessages.Name, error.ErrorMessage);
        Assert.Contains("minúsculas", error.ErrorMessage, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 3: Correr y ver fallar**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~TemplateComponentsBuilderTests|FullyQualifiedName~CreateTemplateCommandValidatorTests" 2>&1 | tail -8
```

Esperado: `CS0246: The type or namespace name 'TemplateDraft' could not be found` (y `TemplateButtonInput`, `CreateTemplateCommand`…).

- [ ] **Step 4: El constructor de componentes**

```csharp
// src/Modules/Messaging/Modules.Messaging.Application/TemplateComponentsBuilder.cs
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-14 §4.7: un botón del cuerpo de crear. <c>Type</c> es <c>QuickReply</c>, <c>Url</c> o <c>PhoneNumber</c>, los
/// mismos nombres que <see cref="TemplateButtonDto"/>. Todo opcional: lo que falte lo marca el validador con su clave.</summary>
public sealed record TemplateButtonInput(string? Type, string? Text, string? Url, string? PhoneNumber);

/// <summary>§4.4: una plantilla ya validada, lista para armar su payload. <c>Examples</c> trae exactamente las variables del cuerpo.</summary>
public sealed record TemplateDraft(
    string Name,
    string Language,
    string Category,
    string? Header,
    string Body,
    string? Footer,
    IReadOnlyList<TemplateButtonInput> Buttons,
    IReadOnlyDictionary<string, string> Examples);

/// <summary>
/// Spec 2026-10-14 §4.4 «Crear»: el payload de <c>POST /{waba}/message_templates</c>. Variables con nombre (D-T9) con
/// <c>body_text_named_params</c> en orden de aparición; encabezado de texto o de documento (<c>header_handle</c>, §4.5); pie; botones
/// con el nombre de Meta. Sin <c>allow_category_change</c> (D-T10). Puro: lo usan la creación libre y la de cotización.
/// </summary>
public static partial class TemplateComponentsBuilder
{
    public const string Named = "NAMED";

    /// <summary>Las variables del cuerpo en orden de aparición, sin repetir. La forma exacta la exige el validador.</summary>
    public static IReadOnlyList<string> VariablesOf(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Placeholder().Matches(body).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
    }

    public static JsonObject CreatePayload(TemplateDraft draft, string? documentHeaderHandle = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return new JsonObject
        {
            ["name"] = draft.Name,
            ["language"] = draft.Language,
            ["category"] = draft.Category,
            ["parameter_format"] = Named,
            ["components"] = Components(draft, documentHeaderHandle),
        };
    }

    public static JsonArray Components(TemplateDraft draft, string? documentHeaderHandle = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var components = new JsonArray();
        if (documentHeaderHandle is not null)
        {
            components.Add(new JsonObject
            {
                ["type"] = "HEADER",
                ["format"] = "DOCUMENT",
                ["example"] = new JsonObject { ["header_handle"] = new JsonArray(documentHeaderHandle) },
            });
        }
        else if (!string.IsNullOrEmpty(draft.Header))
        {
            components.Add(new JsonObject { ["type"] = "HEADER", ["format"] = "TEXT", ["text"] = draft.Header });
        }

        var body = new JsonObject { ["type"] = "BODY", ["text"] = draft.Body };
        var variables = VariablesOf(draft.Body);
        if (variables.Count > 0)
        {
            var examples = new JsonArray();
            foreach (var variable in variables)
            {
                examples.Add(new JsonObject
                {
                    ["param_name"] = variable,
                    ["example"] = draft.Examples.TryGetValue(variable, out var example) ? example : string.Empty,
                });
            }

            body["example"] = new JsonObject { ["body_text_named_params"] = examples };
        }

        components.Add(body);
        if (!string.IsNullOrEmpty(draft.Footer))
        {
            components.Add(new JsonObject { ["type"] = "FOOTER", ["text"] = draft.Footer });
        }

        if (draft.Buttons.Count > 0)
        {
            var buttons = new JsonArray();
            foreach (var button in draft.Buttons)
            {
                var item = new JsonObject { ["text"] = button.Text };
                switch (button.Type)
                {
                    case "Url":
                        item["type"] = "URL";
                        item["url"] = button.Url;
                        break;
                    case "PhoneNumber":
                        item["type"] = "PHONE_NUMBER";
                        item["phone_number"] = button.PhoneNumber;
                        break;
                    default:
                        item["type"] = "QUICK_REPLY";
                        break;
                }

                buttons.Add(item);
            }

            components.Add(new JsonObject { ["type"] = "BUTTONS", ["buttons"] = buttons });
        }

        return components;
    }

    // Igual que TemplateCatalog.Placeholder: Meta tolera espacios alrededor; el validador los rechaza antes de llegar acá.
    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
```

- [ ] **Step 5: Auditoría de plantillas**

En `IMessagingAuditRecorder`, después de `RecordQuickReply`:

```csharp
    /// <summary>Spec 2026-10-14 §4.6: crear, aprovisionar y borrar una plantilla, en la misma transacción que el espejo. Los cambios de
    /// estado que trae el webhook no se auditan (D-T14).</summary>
    void RecordTemplate(Guid tenantId, Guid actorId, string action, Guid templateId, DateTimeOffset occurredAt);
```

En `MessagingAuditActions`, al final:

```csharp
    public const string TemplateResourceType = "message_template";
    public const string TemplateCreated = "messaging.template.created";
    public const string TemplateQuotationProvisioned = "messaging.template.quotation_provisioned";
    public const string TemplateDeleted = "messaging.template.deleted";
```

En `MessagingAuditRecorder` (Infrastructure), después de `RecordQuickReply`:

```csharp
    // Spec 2026-10-14 §4.6: siempre una persona (el admin en la pantalla de plantillas). changed_fields vacío, como el resto del módulo.
    public void RecordTemplate(Guid tenantId, Guid actorId, string action, Guid templateId, DateTimeOffset occurredAt) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            actorId,
            AuditActorType.Human,
            action,
            MessagingAuditActions.TemplateResourceType,
            templateId.ToString(),
            Outcome,
            NoChangedFields,
            Source,
            occurredAt));
```

Si alguna prueba unitaria tiene un doble de `IMessagingAuditRecorder` (busca `: IMessagingAuditRecorder` en `tests/`), agrégale un `RecordTemplate` que anote la llamada en una lista `Templates`.

- [ ] **Step 6: Comando, validador y handler**

Al final de `TemplateManagement.cs`:

```csharp
/// <summary>Los textos de <c>errors</c>, tuteando.</summary>
public static class TemplateMessages
{
    public const string Name = "El nombre lleva letras minúsculas, números y guiones bajos, hasta 512 caracteres, y no puede empezar por «qep_cotizacion».";
    public const string Language = "Elige un idioma con la forma «es» o «es_CO».";
    public const string Category = "La categoría es MARKETING o UTILITY.";
    public const string Header = "El encabezado lleva hasta 60 caracteres y no admite variables.";
    public const string Footer = "El pie lleva hasta 60 caracteres.";
    public const string Body = "Escribe un cuerpo de 1 a 1024 caracteres con algo más que variables.";
    public const string BodyVariable = "Las variables van entre llaves dobles, en minúsculas, con números o guiones bajos y sin espacios: {{nombre}}.";
    public const string Examples = "Escribe un ejemplo para cada variable del cuerpo, ni una más ni una menos.";
    public const string Buttons = "Hasta diez botones; los de respuesta rápida van todos juntos, al principio o al final.";
    public const string ButtonType = "El tipo de botón es QuickReply, Url o PhoneNumber.";
    public const string ButtonText = "El texto del botón lleva de 1 a 25 caracteres.";
    public const string ButtonUrl = "El botón de enlace necesita una URL https completa, sin variables, de hasta 2000 caracteres.";
    public const string ButtonPhone = "El botón de llamada necesita un número de 7 a 20 dígitos, con «+» opcional.";
}

/// <summary>§4.7 <c>POST …/template-management/templates</c>. Todo opcional para que lo que falte salga 422 con su clave en <c>errors</c>.</summary>
public sealed record CreateTemplateCommand(
    Guid TenantId,
    Guid ConnectionId,
    string? Name,
    string? Language,
    string? Category,
    string? Header,
    string? Body,
    string? Footer,
    IReadOnlyList<TemplateButtonInput>? Buttons,
    IReadOnlyDictionary<string, string?>? Examples) : ICommand<ManagedTemplateDto>;

/// <summary>§4.8. Lo que Meta valida y nosotros no (proporción de variables frente a texto, variable al final) vuelve de Meta como
/// <c>rejected_by_meta</c>: duplicarlo sería mantener dos copias de reglas que Meta cambia sin avisar.</summary>
public sealed partial class CreateTemplateCommandValidator : AbstractValidator<CreateTemplateCommand>
{
    private static readonly string[] Categories = ["MARKETING", "UTILITY"];
    private static readonly string[] ButtonTypes = ["QuickReply", "Url", "PhoneNumber"];

    public CreateTemplateCommandValidator()
    {
        RuleFor(command => command.Name)
            .Must(name => MessageTemplateName.IsValid(name) && !MessageTemplateName.IsReserved(name))
            .OverridePropertyName("name").WithMessage(TemplateMessages.Name);
        RuleFor(command => command.Language)
            .Must(language => language is not null && LanguagePattern().IsMatch(language))
            .OverridePropertyName("language").WithMessage(TemplateMessages.Language);
        RuleFor(command => command.Category)
            .Must(category => category is not null && Categories.Contains(category, StringComparer.Ordinal))
            .OverridePropertyName("category").WithMessage(TemplateMessages.Category);
        RuleFor(command => command.Header)
            .Must(header => header is null || (header.Length <= 60 && !header.Contains("{{", StringComparison.Ordinal)))
            .OverridePropertyName("header").WithMessage(TemplateMessages.Header);
        RuleFor(command => command.Footer)
            .Must(footer => footer is null || footer.Length <= 60)
            .OverridePropertyName("footer").WithMessage(TemplateMessages.Footer);
        // Custom con ValidationFailure a mano: los mensajes llevan {{nombre}} literal y WithMessage lo trataría como placeholder.
        RuleFor(command => command.Body).Custom((body, context) =>
        {
            if (string.IsNullOrWhiteSpace(body) || body.Length > 1024)
            {
                context.AddFailure(new ValidationFailure("body", TemplateMessages.Body));
                return;
            }

            if (LooseBrace().Matches(body).Any(match => !StrictVariable().IsMatch(match.Value)))
            {
                context.AddFailure(new ValidationFailure("body", TemplateMessages.BodyVariable));
                return;
            }

            if (string.IsNullOrWhiteSpace(StrictVariable().Replace(body, string.Empty)))
            {
                context.AddFailure(new ValidationFailure("body", TemplateMessages.Body));
            }
        });
        RuleFor(command => command.Examples).Custom((given, context) =>
        {
            var command = context.InstanceToValidate;
            if (string.IsNullOrWhiteSpace(command.Body) || command.Body.Length > 1024)
            {
                return; // ya marcado en body; sin cuerpo no hay contra qué comparar los ejemplos.
            }

            var variables = TemplateComponentsBuilder.VariablesOf(command.Body);
            var examples = given ?? new Dictionary<string, string?>();
            var keysMatch = examples.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(variables);
            if (!keysMatch || examples.Values.Any(string.IsNullOrWhiteSpace) || examples.Values.Any(value => value!.Length > 1024))
            {
                context.AddFailure(new ValidationFailure("examples", TemplateMessages.Examples));
            }
        });
        RuleFor(command => command.Buttons).Custom((buttons, context) =>
        {
            var list = buttons ?? [];
            if (list.Count > 10)
            {
                context.AddFailure(new ValidationFailure("buttons", TemplateMessages.Buttons));
                return;
            }

            for (var index = 0; index < list.Count; index++)
            {
                var button = list[index];
                if (button.Type is null || !ButtonTypes.Contains(button.Type, StringComparer.Ordinal))
                {
                    context.AddFailure(new ValidationFailure($"buttons[{index}].type", TemplateMessages.ButtonType));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(button.Text) || button.Text.Length > 25)
                {
                    context.AddFailure(new ValidationFailure($"buttons[{index}].text", TemplateMessages.ButtonText));
                }

                if (button.Type == "Url" && !IsHttpsUrl(button.Url))
                {
                    context.AddFailure(new ValidationFailure($"buttons[{index}].url", TemplateMessages.ButtonUrl));
                }

                if (button.Type == "PhoneNumber" && (button.PhoneNumber is null || !PhonePattern().IsMatch(button.PhoneNumber)))
                {
                    context.AddFailure(new ValidationFailure($"buttons[{index}].phoneNumber", TemplateMessages.ButtonPhone));
                }
            }

            // Meta exige las respuestas rápidas agrupadas: todas al principio o todas al final.
            var quick = list.Select((button, index) => (button.Type, index)).Where(pair => pair.Type == "QuickReply").Select(pair => pair.index).ToArray();
            if (quick.Length > 0 && quick.Length < list.Count)
            {
                var contiguous = quick[^1] - quick[0] + 1 == quick.Length;
                var atEdge = quick[0] == 0 || quick[^1] == list.Count - 1;
                if (!contiguous || !atEdge)
                {
                    context.AddFailure(new ValidationFailure("buttons", TemplateMessages.Buttons));
                }
            }
        });
    }

    private static bool IsHttpsUrl(string? url) =>
        url is { Length: <= 2000 } && !url.Contains("{{", StringComparison.Ordinal)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    [GeneratedRegex(@"^[a-z]{2,3}(_[A-Z]{2})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex LanguagePattern();

    [GeneratedRegex(@"^\+?[0-9]{7,20}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();

    // Cualquier par de llaves dobles, con lo que sea adentro: lo que no cumpla la forma estricta se marca.
    [GeneratedRegex(@"\{\{[^{}]*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex LooseBrace();

    [GeneratedRegex(@"\{\{[a-z0-9_]+\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex StrictVariable();
}

/// <summary>§4.3 «CreateTemplateCommand»: valida, comprueba el nombre en el espejo sin llamar a Meta, crea en Meta, inserta <c>PENDING</c>
/// <c>Free</c>, audita y guarda. Si Meta falla no se escribe nada.</summary>
public sealed class CreateTemplateHandler(
    IMessageTemplateRepository repository,
    IWhatsAppCloudClient meta,
    IMessagingConnectionDirectory connections,
    IMessagingMemberNames memberNames,
    CallerMembership callerMembership,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CreateTemplateCommand> validator)
    : ICommandHandler<CreateTemplateCommand, ManagedTemplateDto>
{
    public async Task<ManagedTemplateDto> HandleAsync(CreateTemplateCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var source = await connections.ResolveTemplateSourceAsync(command.TenantId, command.ConnectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        // El validador garantiza los no-nulos de abajo.
        var draft = new TemplateDraft(
            command.Name!, command.Language!, command.Category!, command.Header, command.Body!, command.Footer, command.Buttons ?? [],
            command.Examples!.ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal));
        if (await repository.FindByNameAsync(command.TenantId, command.ConnectionId, draft.Name, draft.Language, cancellationToken) is not null)
        {
            throw new MessagingDomainException(MessagingErrorCodes.TemplateNameTaken, "Another template of this connection already uses that name in that language.");
        }

        var created = await meta.CreateTemplateAsync(source, TemplateComponentsBuilder.CreatePayload(draft), cancellationToken);
        if (!created.Succeeded || created.Value is null)
        {
            throw await TemplateGraphErrors.FailAsync(
                connections, command.TenantId, command.ConnectionId, created.Failure ?? new MessagingGraphFailure(0, null, "unreachable"), cancellationToken);
        }

        var now = clock.UtcNow;
        var memberId = await callerMembership.FindAsync(command.TenantId, cancellationToken);
        var template = MessageTemplate.Pending(
            command.TenantId, command.ConnectionId, created.Value.Id, draft.Name, draft.Language, created.Value.Category, TemplateComponentsBuilder.Named,
            created.Value.Status, TemplateComponentsBuilder.Components(draft).ToJsonString(), MessageTemplateKind.Free, memberId, now);
        repository.Add(template);
        audit.RecordTemplate(command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateCreated, template.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var names = memberId is { } member
            ? await memberNames.FindAsync(command.TenantId, [member], cancellationToken)
            : new Dictionary<Guid, string>();
        return ManagedTemplateMapping.ToDto(template, names);
    }
}
```

Agrega arriba del archivo `using System.Text.RegularExpressions;`, `using FluentValidation;` y `using FluentValidation.Results;`. El validador se registra solo si Messaging usa `AddValidatorsFromAssemblyContaining` (comprueba en `MessagingInfrastructureExtensions` o en Bootstrapper cómo entra `CreateQuickReplyValidator`; haz lo mismo).

- [ ] **Step 7: El endpoint y el cuerpo**

En `TemplateManagementEndpoints`, dentro de `MapTemplateManagementEndpoints`:

```csharp
        group.MapPost("/templates", CreateAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Accepts<CreateTemplateRequest>("application/json")
            .Produces<ManagedTemplateDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
```

Y el handler más los records, al final del archivo:

```csharp
    // Location = la pantalla: no hay GET de una sola plantilla; el frontend repinta la lista con lo que vuelve.
    private static async Task<IResult> CreateAsync(
        Guid tenantId, Guid connectionId, CreateTemplateRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Created(
            $"/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management",
            await dispatcher.SendAsync(
                new CreateTemplateCommand(
                    tenantId, connectionId, request.Name, request.Language, request.Category, request.Header, request.Body, request.Footer,
                    request.Buttons?.Select(button => new TemplateButtonInput(button.Type, button.Text, button.Url, button.PhoneNumber)).ToArray(),
                    request.Examples),
                cancellationToken));
}

/// <summary>Spec 2026-10-14 §4.7: el cuerpo de crear una plantilla libre. Todo opcional para que lo que falte salga 422 con su clave en
/// <c>errors</c>, no 400 del binder. Las variables se declaran en <c>body</c> como <c>{{nombre}}</c> y <c>examples</c> trae exactamente
/// esas claves.</summary>
public sealed record CreateTemplateRequest(
    string? Name,
    string? Language,
    string? Category,
    string? Header,
    string? Body,
    string? Footer,
    List<TemplateButtonRequest>? Buttons,
    Dictionary<string, string?>? Examples);

/// <summary><c>type</c> es <c>QuickReply</c>, <c>Url</c> o <c>PhoneNumber</c>, como <c>TemplateButtonDto</c>.</summary>
public sealed record TemplateButtonRequest(string? Type, string? Text, string? Url, string? PhoneNumber);
```

(Mueve la llave de cierre de la clase `TemplateManagementEndpoints` para que `CreateAsync` quede adentro y los records afuera.)

- [ ] **Step 8: Ver pasar las unitarias**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~TemplateComponentsBuilderTests|FullyQualifiedName~CreateTemplateCommandValidatorTests" 2>&1 | tail -5
```

Esperado: `Passed!` (7 del constructor + 32 del validador contando casos de teoría).

- [ ] **Step 9: Pruebas de integración de crear**

```csharp
// tests/Modules/Messaging/Modules.Messaging.IntegrationTests/TemplateManagementCreateApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-14 §4.3 «CreateTemplateCommand» y §4.8 por HTTP y contra la base: Meta recibe el payload, queda fila PENDING Free
/// con auditoría; el nombre repetido no llega a Meta; un rechazo de Meta es 422 con su texto y no escribe nada; validación por campo; 403.</summary>
public sealed class TemplateManagementCreateApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Admin);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, Func<HttpRequestMessage, HttpResponseMessage>? onCreate = null)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        factory.MetaHandler.RespondWith("/222/message_templates", request => request.Method == HttpMethod.Post
            ? (onCreate ?? (_ => FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"id":"555","status":"PENDING","category":"MARKETING"}""")))(request)
            : FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"data":[]}"""));
        return new Fixture(factory, connectionString, tenant, connectionId, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, TemplatePermissions));
    }

    private static object Body(string name = "promo_octubre") => new
    {
        name,
        language = "es",
        category = "MARKETING",
        header = "Oferta de octubre",
        body = "Hola {{first_name}}, este mes {{product}} tiene {{discount}} de descuento.",
        footer = "Responde DETENER para no recibir promociones.",
        buttons = new object[]
        {
            new { type = "QuickReply", text = "Detener promociones" },
            new { type = "Url", text = "Ver catálogo", url = "https://ejemplo.co/catalogo" },
        },
        examples = new Dictionary<string, string> { ["first_name"] = "Ana", ["product"] = "Silla Nova", ["discount"] = "20 %" },
    };

    private static string TemplatesUrl(Fixture f) => $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/templates";

    private static IEnumerable<MetaRequest> Creates(FakeMetaGraphHandler meta) =>
        meta.Requests.Where(request => request.Method == HttpMethod.Post && request.Uri!.AbsolutePath.EndsWith("/222/message_templates", StringComparison.Ordinal));

    [Fact]
    public async Task CreatingPostsToMetaStoresAPendingFreeTemplateAndAudits()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await SendAsync(f.Admin, HttpMethod.Post, TemplatesUrl(f), Body());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), response.Headers.Location!.ToString());
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("promo_octubre", "es", "MARKETING", "PENDING", "Free", "555"), (
            created.GetProperty("name").GetString(), created.GetProperty("language").GetString(), created.GetProperty("category").GetString(),
            created.GetProperty("status").GetString(), created.GetProperty("kind").GetString(), created.GetProperty("metaTemplateId").GetString()));
        Assert.Equal(["first_name", "product", "discount"], created.GetProperty("parameters").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(await OwnerMembershipIdAsync(f.ConnectionString, f.Tenant), created.GetProperty("createdBy").GetProperty("memberId").GetGuid());
        var request = Assert.Single(Creates(f.Factory.MetaHandler));
        Assert.Equal($"Bearer {SentinelMetaAccessToken}", request.Authorization);
        using var payload = JsonDocument.Parse(request.Body!);
        Assert.Equal("NAMED", payload.RootElement.GetProperty("parameter_format").GetString());
        Assert.Equal(["HEADER", "BODY", "FOOTER", "BUTTONS"], payload.RootElement.GetProperty("components").EnumerateArray().Select(component => component.GetProperty("type").GetString()));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE kind = 'Free' AND status = 'PENDING' AND name = 'promo_octubre'"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE source = 'messaging' AND action = 'messaging.template.created' AND resource_type = 'message_template' AND resource_id = @id",
            ("id", created.GetProperty("id").GetGuid().ToString())));
    }

    [Fact]
    public async Task ARepeatedNameIsNameTakenWithoutASecondCallToMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, TemplatesUrl(f), Body())).StatusCode);

        var response = await SendAsync(f.Admin, HttpMethod.Post, TemplatesUrl(f), Body());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.name_taken", (await ProblemAsync(response)).Code);
        Assert.Single(Creates(f.Factory.MetaHandler));
    }

    [Fact]
    public async Task AMetaRejectionIsRejectedByMetaWithItsTextAndWritesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, _ => FakeMetaGraphHandler.Json(HttpStatusCode.BadRequest,
            """{"error":{"message":"(#100) Invalid parameter","type":"OAuthException","code":100,"error_user_title":"Invalid Format","error_user_msg":"The body ends with a variable.","fbtrace_id":"t"}}"""));
        using var host = f.Factory;

        var response = await SendAsync(f.Admin, HttpMethod.Post, TemplatesUrl(f), Body());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.rejected_by_meta", (await ProblemAsync(response)).Code);
        Assert.Contains("The body ends with a variable.", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'messaging.template.created'"));
    }

    [Fact]
    public async Task ANameMetaAlreadyHasIsNameTaken()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, _ => FakeMetaGraphHandler.Json(HttpStatusCode.BadRequest,
            """{"error":{"message":"(#100) Invalid parameter","type":"OAuthException","code":100,"error_user_title":"Message Template Name Already Exists","error_user_msg":"x","fbtrace_id":"t"}}"""));
        using var host = f.Factory;

        var response = await SendAsync(f.Admin, HttpMethod.Post, TemplatesUrl(f), Body());

        Assert.Equal("messaging.template.name_taken", (await ProblemAsync(response)).Code);
    }

    [Fact]
    public async Task ValidationMarksEachFieldAndNeverReachesMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await SendAsync(f.Admin, HttpMethod.Post, TemplatesUrl(f), new
        {
            name = "qep_cotizacion",
            language = "es",
            category = "UTILITY",
            body = "Hola {{first_name}}",
            examples = new Dictionary<string, string>(),
            buttons = new object[] { new { type = "Url", text = "Ver", url = "http://inseguro.co" } },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, keys) = await ProblemAsync(response);
        Assert.Equal("validation.failed", code);
        Assert.Equal(["buttons[0].url", "examples", "name"], keys);
        Assert.Empty(Creates(f.Factory.MetaHandler));
    }

    [Fact]
    public async Task WithoutThePermissionIs403AndWritesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        using var manager = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions);

        var response = await SendAsync(manager, HttpMethod.Post, TemplatesUrl(f), Body());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(Creates(f.Factory.MetaHandler));
    }
}
```

- [ ] **Step 10: Correr y ver pasar**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~TemplateManagementCreateApiTests" 2>&1 | tail -5
```

Esperado: `Passed!` con 6 pruebas.

- [ ] **Step 11: Commit**

```bash
git add src/Modules/Messaging tests/Modules/Messaging
git commit -m "feat(messaging): crear una plantilla libre de WhatsApp desde QEP y mandarla a aprobación"
```

---

### Task 8: La plantilla de cotización aprovisionada — definición, PDF de ejemplo, adopción y reintento

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/QuotationTemplateProvisioning.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/TemplateManagementEndpoints.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/QuotationTemplateDefinitionTests.cs`, `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/QuotationTemplateProvisioningApiTests.cs`

**Interfaces:**
- Consumes: `TemplateComponentsBuilder.{CreatePayload,Components}` con `documentHeaderHandle` (Task 7), `IWhatsAppCloudClient.{UploadTemplateExampleAsync,CreateTemplateAsync,DeleteTemplateAsync}` (Task 4), `IMessageTemplateRepository.{FindQuotationAsync,FindByNameAsync}`, `MessageTemplate.{IsLive,AdoptAsQuotation}`, `TemplateCatalog.FromStored`.
- Produces: `QuotationTemplateDefinition.{Name, Language, Category, Body, Footer, Variables, Examples, ExampleFileName, ExampleMimeType, Draft(), Payload(string handle), Matches(string componentsJson), ExamplePdf()}`; `MinimalPdf.Build(string text) → byte[]`; `ProvisionQuotationTemplateCommand(Guid TenantId, Guid ConnectionId) : ICommand<ManagedTemplateDto>`.

- [ ] **Step 1: Pruebas unitarias de la definición y del PDF**

```csharp
// tests/Modules/Messaging/Modules.Messaging.UnitTests/QuotationTemplateDefinitionTests.cs
using System.Text;
using System.Text.Json;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-14 §4.5: la única fuente del texto de la plantilla de cotización. Mismo cuerpo que la de Zenvia, encabezado de
/// documento con el handle, cuatro variables con ejemplo, y <c>Matches</c> reconoce su propio resultado y rechaza otro cuerpo.</summary>
public sealed class QuotationTemplateDefinitionTests
{
    [Fact]
    public void TheDefinitionIsTheReservedNameInSpanishUtility()
    {
        Assert.Equal((MessageTemplateName.QuotationName, "es", "UTILITY"), (QuotationTemplateDefinition.Name, QuotationTemplateDefinition.Language, QuotationTemplateDefinition.Category));
        Assert.Equal(["fullname", "order_number", "total", "valid_until"], QuotationTemplateDefinition.Variables);
        Assert.Equal(QuotationTemplateDefinition.Variables, TemplateComponentsBuilder.VariablesOf(QuotationTemplateDefinition.Body));
        Assert.Equal(QuotationTemplateDefinition.Variables.Order(StringComparer.Ordinal), QuotationTemplateDefinition.Examples.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ThePayloadCarriesADocumentHeaderWithTheHandleTheBodyWithExamplesAndTheFooter()
    {
        using var payload = JsonDocument.Parse(QuotationTemplateDefinition.Payload("4::handle").ToJsonString());
        var root = payload.RootElement;

        Assert.Equal(("qep_cotizacion", "es", "UTILITY", "NAMED"), (
            root.GetProperty("name").GetString(), root.GetProperty("language").GetString(), root.GetProperty("category").GetString(), root.GetProperty("parameter_format").GetString()));
        var components = root.GetProperty("components").EnumerateArray().ToArray();
        Assert.Equal(["HEADER", "BODY", "FOOTER"], components.Select(component => component.GetProperty("type").GetString()));
        Assert.Equal("DOCUMENT", components[0].GetProperty("format").GetString());
        Assert.Equal(["4::handle"], components[0].GetProperty("example").GetProperty("header_handle").EnumerateArray().Select(handle => handle.GetString()));
        Assert.Equal(QuotationTemplateDefinition.Body, components[1].GetProperty("text").GetString());
        Assert.Equal(["fullname|Juan Perez", "order_number|COT-000123", "total|2.450.000 COP", "valid_until|30 de septiembre de 2026"],
            components[1].GetProperty("example").GetProperty("body_text_named_params").EnumerateArray().Select(example => $"{example.GetProperty("param_name").GetString()}|{example.GetProperty("example").GetString()}"));
        Assert.Equal(QuotationTemplateDefinition.Footer, components[2].GetProperty("text").GetString());
    }

    [Fact]
    public void MatchesAcceptsItsOwnComponentsAsMetaWouldReturnThem()
    {
        // Meta devuelve el encabezado con su example y el cuerpo con el suyo; Matches mira cuerpo, pie, formato y variables.
        var components = QuotationTemplateDefinition.Payload("4::handle")["components"]!.ToJsonString();

        Assert.True(QuotationTemplateDefinition.Matches(components));
    }

    [Theory]
    [InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Otro cuerpo {{fullname}} {{order_number}} {{total}} {{valid_until}}"},{"type":"FOOTER","text":"Este mensaje fue generado automáticamente."}]""")]
    [InlineData("""[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"¡Hola, {{fullname}}! Adjuntamos la cotización *{{order_number}}* por *{{total}}*, vigente hasta el *{{valid_until}}*."},{"type":"FOOTER","text":"Este mensaje fue generado automáticamente."}]""")]
    [InlineData("""[{"type":"BODY","text":"¡Hola, {{fullname}}! Adjuntamos la cotización *{{order_number}}* por *{{total}}*, vigente hasta el *{{valid_until}}*."},{"type":"FOOTER","text":"Este mensaje fue generado automáticamente."}]""")]
    [InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"¡Hola, {{fullname}}! Adjuntamos la cotización *{{order_number}}* por *{{total}}*, vigente hasta el *{{valid_until}}*."}]""")]
    [InlineData("no es json")]
    public void MatchesRejectsAnotherBodyHeaderFormatOrFooter(string components) => Assert.False(QuotationTemplateDefinition.Matches(components));

    [Fact]
    public void TheExamplePdfIsAValidMinimalDocument()
    {
        var bytes = QuotationTemplateDefinition.ExamplePdf();
        var text = Encoding.ASCII.GetString(bytes);

        Assert.StartsWith("%PDF-1.4\n", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);
        Assert.Contains("/Type /Catalog", text, StringComparison.Ordinal);
        Assert.Contains("(Cotizacion de ejemplo - QEP)", text, StringComparison.Ordinal);
        // El startxref apunta exactamente a la tabla xref.
        var startxref = int.Parse(text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + "startxref\n".Length)..].Split('\n')[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("xref", text.Substring(startxref, 4));
        // Y cada offset de la tabla apunta a «N 0 obj».
        var xref = text[startxref..];
        var entries = xref.Split('\n').Skip(3).Take(5).Select(line => int.Parse(line[..10], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        for (var index = 0; index < entries.Length; index++)
        {
            Assert.StartsWith($"{index + 1} 0 obj", text[entries[index]..], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheExamplePdfIsSmall() => Assert.InRange(QuotationTemplateDefinition.ExamplePdf().Length, 300, 2000);
}
```

- [ ] **Step 2: Correr y ver fallar**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~QuotationTemplateDefinitionTests" 2>&1 | tail -8
```

Esperado: `CS0103: The name 'QuotationTemplateDefinition' does not exist`.

- [ ] **Step 3: La definición, el PDF y el comando**

```csharp
// src/Modules/Messaging/Modules.Messaging.Application/QuotationTemplateProvisioning.cs
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using BuildingBlocks.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-14 §4.5: la **única** fuente del nombre, idioma, categoría, texto, variables y ejemplos de la plantilla de cotización. Es el
/// mismo texto que la plantilla de Zenvia (README § Plantilla de WhatsApp) para que el cliente final no note el cambio de canal. Cambiar
/// el texto algún día es una definición nueva con otro nombre (<c>qep_cotizacion_v2</c>), aprobada antes de que el envío la use, y el
/// sender de Quotations apunta al nombre nuevo en el mismo commit que cambia las variables.
/// </summary>
public static class QuotationTemplateDefinition
{
    public const string Name = MessageTemplateName.QuotationName;
    public const string Language = "es";
    public const string Category = "UTILITY";
    public const string Body = "¡Hola, {{fullname}}! Adjuntamos la cotización *{{order_number}}* por *{{total}}*, vigente hasta el *{{valid_until}}*.";
    public const string Footer = "Este mensaje fue generado automáticamente.";
    public const string ExampleFileName = "cotizacion-ejemplo.pdf";
    public const string ExampleMimeType = "application/pdf";

    public static readonly IReadOnlyList<string> Variables = ["fullname", "order_number", "total", "valid_until"];

    public static readonly IReadOnlyDictionary<string, string> Examples = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["fullname"] = "Juan Perez",
        ["order_number"] = "COT-000123",
        ["total"] = "2.450.000 COP",
        ["valid_until"] = "30 de septiembre de 2026",
    };

    public static TemplateDraft Draft() => new(Name, Language, Category, null, Body, Footer, [], Examples);

    /// <summary>El payload de creación con el encabezado de documento apuntando al <paramref name="handle"/> de la Resumable Upload.</summary>
    public static JsonObject Payload(string handle) => TemplateComponentsBuilder.CreatePayload(Draft(), handle);

    /// <summary>§4.5, adopción: unos <c>components</c> de Meta son esta plantilla si el cuerpo y el pie coinciden, el encabezado es
    /// multimedia (documento) y las variables son exactamente las cuatro. Se compara lo normalizado por <see cref="TemplateCatalog"/>,
    /// no el JSON crudo: Meta agrega <c>example</c> y otros campos que no cambian la plantilla.</summary>
    public static bool Matches(string componentsJson)
    {
        var described = TemplateCatalog.FromStored(Name, Language, Category, TemplateComponentsBuilder.Named, componentsJson);
        return string.Equals(described.Body, Body, StringComparison.Ordinal)
            && string.Equals(described.Footer, Footer, StringComparison.Ordinal)
            && described.Header is null
            && described.UnsupportedReason == TemplateUnsupportedReasons.MediaHeader
            && described.Parameters.ToHashSet(StringComparer.Ordinal).SetEquals(Variables)
            && described.Buttons.Count == 0
            && IsDocumentHeader(componentsJson);
    }

    /// <summary>El PDF de ejemplo que Meta exige para aprobar un encabezado de documento. Se genera, no se embebe: una página con una línea.</summary>
    public static byte[] ExamplePdf() => MinimalPdf.Build("Cotizacion de ejemplo - QEP");

    private static bool IsDocumentHeader(string componentsJson)
    {
        try
        {
            if (JsonNode.Parse(componentsJson) is not JsonArray components)
            {
                return false;
            }

            return components.OfType<JsonObject>().Any(component =>
                string.Equals(component["type"]?.GetValue<string>(), "HEADER", StringComparison.OrdinalIgnoreCase)
                && string.Equals(component["format"]?.GetValue<string>(), "DOCUMENT", StringComparison.OrdinalIgnoreCase));
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}

/// <summary>Un PDF 1.4 de una página con una línea de texto y una tabla <c>xref</c> correcta. Suficiente para que Meta lo acepte como
/// ejemplo de encabezado de documento; no es un motor de PDF.</summary>
internal static class MinimalPdf
{
    public static byte[] Build(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);
        var stream = $"BT /F1 18 Tf 72 720 Td ({escaped}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream).ToString(CultureInfo.InvariantCulture)} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        var xref = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        builder.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}

/// <summary>§4.7 <c>POST …/template-management/quotation</c>: aprovisiona (o adopta, o reintenta) la plantilla de cotización de la conexión.</summary>
public sealed record ProvisionQuotationTemplateCommand(Guid TenantId, Guid ConnectionId) : ICommand<ManagedTemplateDto>;

/// <summary>
/// §4.5, en orden: (2) si ya hay una <c>Quotation</c> viva, <c>quotation_exists</c>; una rechazada, pausada, deshabilitada o con tope se
/// borra en Meta y en el espejo y se sigue (el «reintentar»). (3) Si existe una <c>External</c> con el nombre reservado se adopta cuando su
/// cuerpo coincide, sin tocar Meta; si no coincide, <c>quotation_conflict</c>. (4) Sube el PDF de ejemplo, (5) crea con el handle, (6)
/// inserta <c>PENDING</c> <c>Quotation</c>, audita y guarda. Si Meta falla en (4) o (5) no se escribe nada.
/// </summary>
public sealed class ProvisionQuotationTemplateHandler(
    IMessageTemplateRepository repository,
    IWhatsAppCloudClient meta,
    IMessagingConnectionDirectory connections,
    IMessagingMemberNames memberNames,
    CallerMembership callerMembership,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ProvisionQuotationTemplateCommand, ManagedTemplateDto>
{
    public async Task<ManagedTemplateDto> HandleAsync(ProvisionQuotationTemplateCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        var source = await connections.ResolveTemplateSourceAsync(command.TenantId, command.ConnectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        var now = clock.UtcNow;
        var memberId = await callerMembership.FindAsync(command.TenantId, cancellationToken);

        var current = await repository.FindQuotationAsync(command.TenantId, command.ConnectionId, cancellationToken);
        if (current is not null)
        {
            if (current.IsLive)
            {
                throw new MessagingDomainException(MessagingErrorCodes.TemplateQuotationExists, "This connection already has its quotation template pending or approved.");
            }

            // Reintentar: la que Meta rechazó (o pausó, deshabilitó, topó) se va para dejarle el nombre a la nueva.
            var deleted = await meta.DeleteTemplateAsync(source, current.MetaTemplateId, current.Name, cancellationToken);
            if (!deleted.Succeeded)
            {
                throw await TemplateGraphErrors.FailAsync(connections, command.TenantId, command.ConnectionId, deleted.Failure!, cancellationToken);
            }

            repository.Remove(current);
        }
        else if (await repository.FindByNameAsync(command.TenantId, command.ConnectionId, QuotationTemplateDefinition.Name, QuotationTemplateDefinition.Language, cancellationToken) is { } external)
        {
            if (!QuotationTemplateDefinition.Matches(external.ComponentsJson))
            {
                throw new MessagingDomainException(
                    MessagingErrorCodes.TemplateQuotationConflict,
                    $"A template named '{QuotationTemplateDefinition.Name}' already exists in Meta with another body; delete it from the templates screen first.");
            }

            external.AdoptAsQuotation(now);
            audit.RecordTemplate(command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateQuotationProvisioned, external.Id, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return ManagedTemplateMapping.ToDto(external, new Dictionary<Guid, string>());
        }

        var upload = await meta.UploadTemplateExampleAsync(
            source, QuotationTemplateDefinition.ExamplePdf(), QuotationTemplateDefinition.ExampleMimeType, QuotationTemplateDefinition.ExampleFileName, cancellationToken);
        if (!upload.Succeeded || upload.Value is null)
        {
            if (upload.Failure is { } failure && TemplateGraphErrors.IsTokenRejected(failure))
            {
                throw await TemplateGraphErrors.FailAsync(connections, command.TenantId, command.ConnectionId, failure, cancellationToken);
            }

            throw new MessagingDomainException(MessagingErrorCodes.TemplateUploadFailed, "WhatsApp did not accept the example PDF of the quotation template.");
        }

        var created = await meta.CreateTemplateAsync(source, QuotationTemplateDefinition.Payload(upload.Value), cancellationToken);
        if (!created.Succeeded || created.Value is null)
        {
            throw await TemplateGraphErrors.FailAsync(
                connections, command.TenantId, command.ConnectionId, created.Failure ?? new MessagingGraphFailure(0, null, "unreachable"), cancellationToken);
        }

        var template = MessageTemplate.Pending(
            command.TenantId, command.ConnectionId, created.Value.Id, QuotationTemplateDefinition.Name, QuotationTemplateDefinition.Language, created.Value.Category,
            TemplateComponentsBuilder.Named, created.Value.Status, TemplateComponentsBuilder.Components(QuotationTemplateDefinition.Draft(), upload.Value).ToJsonString(),
            MessageTemplateKind.Quotation, memberId, now);
        repository.Add(template);
        audit.RecordTemplate(command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateQuotationProvisioned, template.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var names = memberId is { } member ? await memberNames.FindAsync(command.TenantId, [member], cancellationToken) : new Dictionary<Guid, string>();
        return ManagedTemplateMapping.ToDto(template, names);
    }
}
```

> El reintento borra en Meta y en el espejo **antes** de la creación, dentro de la misma transacción: si la creación falla, el `Remove` no se guarda y la fila rechazada sigue ahí para el siguiente intento. En Meta la plantilla sí quedó borrada; el siguiente refresco lo refleja.

- [ ] **Step 4: El endpoint**

En `TemplateManagementEndpoints.MapTemplateManagementEndpoints`:

```csharp
        group.MapPost("/quotation", ProvisionQuotationAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Produces<ManagedTemplateDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
```

```csharp
    private static async Task<IResult> ProvisionQuotationAsync(Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Created(
            $"/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management",
            await dispatcher.SendAsync(new ProvisionQuotationTemplateCommand(tenantId, connectionId), cancellationToken));
```

- [ ] **Step 5: Ver pasar las unitarias**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~QuotationTemplateDefinitionTests" 2>&1 | tail -5
```

Esperado: `Passed!` con 10 pruebas (5 hechos + 5 casos de teoría).

- [ ] **Step 6: Pruebas de integración del aprovisionamiento**

```csharp
// tests/Modules/Messaging/Modules.Messaging.IntegrationTests/QuotationTemplateProvisioningApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-14 §4.5 por HTTP y contra la base: sube el PDF, crea con el handle, deja fila PENDING Quotation con auditoría;
/// repetir es quotation_exists; una REJECTED se borra y se recrea; una externa igual se adopta sin tocar Meta; una externa distinta es
/// quotation_conflict; la subida fallida es upload_failed sin fila; el nombre reservado no se puede usar en una libre.</summary>
public sealed class QuotationTemplateProvisioningApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Admin);

    private const string QuotationBody = "¡Hola, {{fullname}}! Adjuntamos la cotización *{{order_number}}* por *{{total}}*, vigente hasta el *{{valid_until}}*.";

    private static string ExternalQuotation(string body = QuotationBody, string headerFormat = "DOCUMENT") =>
        $$$"""
        {"data":[{"id":"42","name":"qep_cotizacion","language":"es","status":"APPROVED","category":"UTILITY","parameter_format":"NAMED",
          "components":[{"type":"HEADER","format":"{{{headerFormat}}}","example":{"header_handle":["x"]}},
                        {"type":"BODY","text":"{{{body}}}","example":{"body_text_named_params":[{"param_name":"fullname","example":"Juan"}]}},
                        {"type":"FOOTER","text":"Este mensaje fue generado automáticamente."}]}]}
        """;

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, string listing = """{"data":[]}""", bool uploadFails = false)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var meta = factory.MetaHandler;
        meta.RespondWith($"/{MetaAppId}/uploads", _ => uploadFails
            ? FakeMetaGraphHandler.Json(HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(100))
            : FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"id":"upload:MTph"}"""));
        meta.Respond("/upload:MTph", HttpStatusCode.OK, """{"h":"4::handle"}""");
        // Cada creación devuelve un id distinto (771, 772…), como Meta: el reintento borra la 771 y crea la 772.
        var created = 770;
        meta.RespondWith("/222/message_templates", request => request.Method switch
        {
            var method when method == HttpMethod.Post => FakeMetaGraphHandler.Json(HttpStatusCode.OK, $$"""{"id":"{{Interlocked.Increment(ref created)}}","status":"PENDING","category":"UTILITY"}"""),
            var method when method == HttpMethod.Delete => FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"success":true}"""),
            _ => FakeMetaGraphHandler.Json(HttpStatusCode.OK, listing),
        });
        return new Fixture(factory, connectionString, tenant, connectionId, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, TemplatePermissions));
    }

    private static string QuotationUrl(Fixture f) => $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/quotation";

    private static MetaRequest[] MetaCalls(Fixture f) => f.Factory.MetaHandler.Requests.Where(request => request.Uri!.AbsolutePath.StartsWith("/v24.0/", StringComparison.Ordinal)).ToArray();

    [Fact]
    public async Task ProvisioningUploadsThePdfThenCreatesWithTheHandleAndStoresAPendingQuotationTemplate()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await SendAsync(f.Admin, HttpMethod.Post, QuotationUrl(f));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("qep_cotizacion", "es", "UTILITY", "PENDING", "Quotation", "771", false, "media_header"), (
            created.GetProperty("name").GetString(), created.GetProperty("language").GetString(), created.GetProperty("category").GetString(),
            created.GetProperty("status").GetString(), created.GetProperty("kind").GetString(), created.GetProperty("metaTemplateId").GetString(),
            created.GetProperty("sendable").GetBoolean(), created.GetProperty("unsupportedReason").GetString()));
        Assert.Equal(["fullname", "order_number", "total", "valid_until"], created.GetProperty("parameters").EnumerateArray().Select(item => item.GetString()));
        var calls = MetaCalls(f).Where(call => call.Method == HttpMethod.Post).ToArray();
        Assert.Equal(["/v24.0/100200300/uploads", "/v24.0/upload:MTph", "/v24.0/222/message_templates"], calls.Select(call => call.Uri!.AbsolutePath));
        Assert.Equal("OAuth " + SentinelMetaAccessToken, calls[1].Authorization);
        using var payload = JsonDocument.Parse(calls[2].Body!);
        var header = payload.RootElement.GetProperty("components").EnumerateArray().First();
        Assert.Equal(("HEADER", "DOCUMENT"), (header.GetProperty("type").GetString(), header.GetProperty("format").GetString()));
        Assert.Equal(["4::handle"], header.GetProperty("example").GetProperty("header_handle").EnumerateArray().Select(handle => handle.GetString()));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE kind = 'Quotation' AND status = 'PENDING' AND tenant_id = @t", ("t", f.Tenant.TenantId)));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'messaging.template.quotation_provisioned' AND resource_id = @id", ("id", created.GetProperty("id").GetGuid().ToString())));

        var screen = await f.Admin.GetFromJsonAsync<JsonElement>(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);
        Assert.Equal("PENDING", screen.GetProperty("quotationTemplate").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ProvisioningTwiceIs422QuotationExistsWithoutTouchingMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, QuotationUrl(f))).StatusCode);
        var before = MetaCalls(f).Length;

        var response = await SendAsync(f.Admin, HttpMethod.Post, QuotationUrl(f));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.quotation_exists", (await ProblemAsync(response)).Code);
        Assert.Equal(before, MetaCalls(f).Length);
    }

    [Fact]
    public async Task ARejectedQuotationTemplateIsDeletedInMetaAndRecreated()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, QuotationUrl(f))).StatusCode);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.message_templates SET status = 'REJECTED', status_reason = 'INVALID_FORMAT' WHERE tenant_id = @t", ("t", f.Tenant.TenantId));

        var response = await SendAsync(f.Admin, HttpMethod.Post, QuotationUrl(f));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var delete = Assert.Single(MetaCalls(f), call => call.Method == HttpMethod.Delete);
        Assert.Equal("?hsm_id=771&name=qep_cotizacion", delete.Uri!.Query);
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE kind = 'Quotation' AND tenant_id = @t", ("t", f.Tenant.TenantId)));
        Assert.Equal("PENDING|772", await ScalarAsync<string>(f.ConnectionString, "SELECT status || '|' || meta_template_id FROM messaging.message_templates WHERE kind = 'Quotation' AND tenant_id = @t", ("t", f.Tenant.TenantId)));
    }

    [Fact]
    public async Task AnExternalTemplateWithTheSameBodyIsAdoptedWithoutCallingMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, ExternalQuotation());
        using var host = f.Factory;
        // La pantalla llena el espejo con la externa.
        var screen = await f.Admin.GetFromJsonAsync<JsonElement>(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);
        Assert.Equal(JsonValueKind.Null, screen.GetProperty("quotationTemplate").ValueKind);
        var before = MetaCalls(f).Length;

        var response = await SendAsync(f.Admin, HttpMethod.Post, QuotationUrl(f));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var adopted = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("Quotation", "APPROVED", "42"), (adopted.GetProperty("kind").GetString(), adopted.GetProperty("status").GetString(), adopted.GetProperty("metaTemplateId").GetString()));
        Assert.Equal(before, MetaCalls(f).Length);
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'messaging.template.quotation_provisioned'"));
    }

    [Fact]
    public async Task AnExternalTemplateWithAnotherBodyIs422QuotationConflict()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, ExternalQuotation(body: "Otro texto {{fullname}} {{order_number}} {{total}} {{valid_until}}"));
        using var host = f.Factory;
        await f.Admin.GetAsync(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);

        var response = await SendAsync(f.Admin, HttpMethod.Post, QuotationUrl(f));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.quotation_conflict", (await ProblemAsync(response)).Code);
        Assert.Equal("External", await ScalarAsync<string>(f.ConnectionString, "SELECT kind FROM messaging.message_templates WHERE name = 'qep_cotizacion' AND tenant_id = @t", ("t", f.Tenant.TenantId)));
    }

    [Fact]
    public async Task AFailedUploadIs422UploadFailedAndWritesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, uploadFails: true);
        using var host = f.Factory;

        var response = await SendAsync(f.Admin, HttpMethod.Post, QuotationUrl(f));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.upload_failed", (await ProblemAsync(response)).Code);
        Assert.DoesNotContain(MetaCalls(f), call => call.Method == HttpMethod.Post && call.Uri!.AbsolutePath.EndsWith("/message_templates", StringComparison.Ordinal));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));
    }

    [Fact]
    public async Task AFreeTemplateCannotTakeTheReservedName()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await SendAsync(f.Admin, HttpMethod.Post, $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/templates", new
        {
            name = "qep_cotizacion_v9",
            language = "es",
            category = "UTILITY",
            body = "Hola, ya abrimos.",
            examples = new Dictionary<string, string>(),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, keys) = await ProblemAsync(response);
        Assert.Equal(("validation.failed", new[] { "name" }), (code, keys));
    }
}
```

- [ ] **Step 7: Correr y ver pasar**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~QuotationTemplateProvisioningApiTests" 2>&1 | tail -5
```

Esperado: `Passed!` con 7 pruebas. Si `FakeMetaGraphHandler` no intercepta `/upload:MTph` por el regex (los dos puntos son literales en una regex, así que sí debería), mira `PathAndQuery` del request anotado.

- [ ] **Step 8: Commit**

```bash
git add src/Modules/Messaging tests/Modules/Messaging
git commit -m "feat(messaging): aprovisionar la plantilla de cotización por conexión con su PDF de ejemplo"
```

---

### Task 9: Borrar una plantilla

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/TemplateManagement.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/TemplateManagementEndpoints.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/TemplateManagementDeleteApiTests.cs`

**Interfaces:**
- Consumes: `IWhatsAppCloudClient.DeleteTemplateAsync`, `IMessageTemplateRepository.{FindAsync,IsReferencedByActiveCampaignAsync,Remove}`, `TemplateManagementSupport.NotFound`.
- Produces: `DeleteTemplateCommand(Guid TenantId, Guid ConnectionId, Guid TemplateId) : ICommand<bool>`; `DELETE …/template-management/templates/{templateId:guid}` → 204.

- [ ] **Step 1: Pruebas de integración**

```csharp
// tests/Modules/Messaging/Modules.Messaging.IntegrationTests/TemplateManagementDeleteApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-14 §4.3 «DeleteTemplateCommand», D-T11, D-T12 y D-T13 por HTTP: Meta recibe hsm_id y name, la fila se elimina y se
/// audita; «no existe» en Meta es 204 igual; 404 para un id ajeno; 422 in_use_by_campaign con una campaña programada, también para la de
/// cotización.</summary>
public sealed class TemplateManagementDeleteApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Admin);

    private const string Listing = """
        {"data":[
          {"id":"1","name":"promo","language":"es","status":"APPROVED","category":"MARKETING","parameter_format":"POSITIONAL",
           "components":[{"type":"BODY","text":"Hola {{1}}, tu código es {{2}}"}]},
          {"id":"2","name":"aviso","language":"es","status":"APPROVED","category":"UTILITY","parameter_format":"POSITIONAL",
           "components":[{"type":"BODY","text":"Tu pedido salió"}]}
        ]}
        """;

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, bool metaSaysGone = false)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        factory.MetaHandler.RespondWith("/222/message_templates", request => request.Method switch
        {
            var method when method == HttpMethod.Delete => metaSaysGone
                ? FakeMetaGraphHandler.Json(HttpStatusCode.BadRequest, """{"error":{"message":"x","type":"OAuthException","code":100,"error_user_title":"Invalid Template","error_user_msg":"Message template does not exist in the given language.","fbtrace_id":"t"}}""")
                : FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"success":true}"""),
            _ => FakeMetaGraphHandler.Json(HttpStatusCode.OK, Listing),
        });
        return new Fixture(factory, connectionString, tenant, connectionId, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, TemplatePermissions));
    }

    private static async Task<Dictionary<string, Guid>> IdsByNameAsync(Fixture f)
    {
        var screen = await f.Admin.GetFromJsonAsync<JsonElement>(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);
        return screen.GetProperty("templates").EnumerateArray().ToDictionary(item => item.GetProperty("name").GetString()!, item => item.GetProperty("id").GetGuid(), StringComparer.Ordinal);
    }

    private static string TemplateUrl(Fixture f, Guid templateId) => $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/templates/{templateId}";

    private static IEnumerable<MetaRequest> Deletes(Fixture f) => f.Factory.MetaHandler.Requests.Where(request => request.Method == HttpMethod.Delete);

    [Fact]
    public async Task DeletingCallsMetaWithHsmIdAndNameRemovesTheRowAndAudits()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var ids = await IdsByNameAsync(f);

        var response = await SendAsync(f.Admin, HttpMethod.Delete, TemplateUrl(f, ids["promo"]));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var delete = Assert.Single(Deletes(f));
        Assert.Equal(("/v24.0/222/message_templates", "?hsm_id=1&name=promo", $"Bearer {SentinelMetaAccessToken}"), (delete.Uri!.AbsolutePath, delete.Uri.Query, delete.Authorization));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'messaging.template.deleted' AND resource_id = @id", ("id", ids["promo"].ToString())));
    }

    [Fact]
    public async Task DeletingWhatMetaNoLongerHasIs204AndRemovesTheRowAnyway()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, metaSaysGone: true);
        using var host = f.Factory;
        var ids = await IdsByNameAsync(f);

        var response = await SendAsync(f.Admin, HttpMethod.Delete, TemplateUrl(f, ids["aviso"]));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE name = 'aviso' AND tenant_id = @t", ("t", f.Tenant.TenantId)));
    }

    [Fact]
    public async Task AnUnknownTemplateIdIs404AndNeverReachesMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await IdsByNameAsync(f);

        var response = await SendAsync(f.Admin, HttpMethod.Delete, TemplateUrl(f, Guid.CreateVersion7()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("messaging.template.not_found", (await ProblemAsync(response)).Code);
        Assert.Empty(Deletes(f));
    }

    [Fact]
    public async Task DeletingATemplateUsedByAScheduledCampaignIs422AndKeepsTheRow()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var ids = await IdsByNameAsync(f);
        using var campaigns = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, CampaignPermissions);
        var campaignsUrl = $"/api/v1/tenants/{f.Tenant.TenantId}/messaging/campaigns";
        var created = await SendAsync(campaigns, HttpMethod.Post, campaignsUrl, new
        {
            name = "Promo",
            connectionId = f.ConnectionId,
            template = new { name = "promo", language = "es" },
            parameters = new Dictionary<string, object> { ["1"] = new { source = "customerName" }, ["2"] = new { source = "text", value = "A1" } },
            audience = new { tagIds = new[] { Guid.CreateVersion7() }, customerIds = Array.Empty<Guid>() },
            scheduledAt = DateTimeOffset.UtcNow.AddDays(1),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var campaignId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var launched = await SendAsync(campaigns, HttpMethod.Post, $"{campaignsUrl}/{campaignId}/launch", new { expectedVersion = 1 });
        Assert.True(launched.IsSuccessStatusCode, await launched.Content.ReadAsStringAsync(Ct));

        var response = await SendAsync(f.Admin, HttpMethod.Delete, TemplateUrl(f, ids["promo"]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.in_use_by_campaign", (await ProblemAsync(response)).Code);
        Assert.Empty(Deletes(f));
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));
    }

    // Review Focus 5: la regla de campañas aplica también a la plantilla de cotización. Una campaña no puede crearse con ella por la API
    // (encabezado multimedia, no enviable), así que la fila se escribe directo.
    [Fact]
    public async Task DeletingTheQuotationTemplateUsedByAScheduledCampaignIs422()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await IdsByNameAsync(f);
        var quotationId = Guid.CreateVersion7();
        await ExecuteAsync(f.ConnectionString,
            """
            INSERT INTO messaging.message_templates (id, tenant_id, connection_id, meta_template_id, name, language, category, parameter_format, status, components, kind, created_at, updated_at)
            VALUES (@id, @t, @c, '99', 'qep_cotizacion', 'es', 'UTILITY', 'NAMED', 'APPROVED', '[]'::jsonb, 'Quotation', now(), now())
            """, ("id", quotationId), ("t", f.Tenant.TenantId), ("c", f.ConnectionId));
        await ExecuteAsync(f.ConnectionString,
            """
            INSERT INTO messaging.campaigns (id, tenant_id, connection_id, name, template_name, template_language, template_category, parameters,
                audience_tag_ids, audience_customer_ids, status, created_by_member_id, created_at, updated_at, version)
            VALUES (@id, @t, @c, 'x', 'qep_cotizacion', 'es', 'UTILITY', '{}'::jsonb, ARRAY[@tag]::uuid[], ARRAY[]::uuid[], 'Scheduled', @member, now(), now(), 1)
            """, ("id", Guid.CreateVersion7()), ("t", f.Tenant.TenantId), ("c", f.ConnectionId), ("tag", Guid.CreateVersion7()), ("member", await OwnerMembershipIdAsync(f.ConnectionString, f.Tenant)));

        var response = await SendAsync(f.Admin, HttpMethod.Delete, TemplateUrl(f, quotationId));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.in_use_by_campaign", (await ProblemAsync(response)).Code);
    }

    [Fact]
    public async Task WithoutThePermissionIs403()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var ids = await IdsByNameAsync(f);
        using var manager = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(manager, HttpMethod.Delete, TemplateUrl(f, ids["promo"]))).StatusCode);
        Assert.Empty(Deletes(f));
    }
}
```

- [ ] **Step 2: Correr y ver fallar**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~TemplateManagementDeleteApiTests" 2>&1 | tail -8
```

Esperado: compilan, pero fallan con `Assert.Equal() Failure` esperando 204 y recibiendo 404 (la ruta no existe todavía) — o 405.

- [ ] **Step 3: Comando y handler**

Al final de `TemplateManagement.cs`:

```csharp
/// <summary>§4.7 <c>DELETE …/template-management/templates/{templateId}</c>. D-T11: en Meta sólo ese idioma; D-T12: no si una campaña
/// programada o enviándose la usa; D-T13: la fila se elimina, también cuando Meta dice que ya no existe. La de cotización sí se puede
/// borrar (§4.5): es la salida cuando algo quedó mal.</summary>
public sealed record DeleteTemplateCommand(Guid TenantId, Guid ConnectionId, Guid TemplateId) : ICommand<bool>;

public sealed class DeleteTemplateHandler(
    IMessageTemplateRepository repository,
    IWhatsAppCloudClient meta,
    IMessagingConnectionDirectory connections,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<DeleteTemplateCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteTemplateCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        var source = await connections.ResolveTemplateSourceAsync(command.TenantId, command.ConnectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        var template = await repository.FindAsync(command.TenantId, command.ConnectionId, command.TemplateId, cancellationToken)
            ?? throw TemplateManagementSupport.NotFound(command.TemplateId);
        if (await repository.IsReferencedByActiveCampaignAsync(command.TenantId, command.ConnectionId, template.Name, template.Language, cancellationToken))
        {
            throw new MessagingDomainException(MessagingErrorCodes.TemplateInUseByCampaign, "A scheduled or sending campaign uses this template.");
        }

        var deleted = await meta.DeleteTemplateAsync(source, template.MetaTemplateId, template.Name, cancellationToken);
        if (!deleted.Succeeded)
        {
            throw await TemplateGraphErrors.FailAsync(
                connections, command.TenantId, command.ConnectionId, deleted.Failure ?? new MessagingGraphFailure(0, null, "unreachable"), cancellationToken);
        }

        repository.Remove(template);
        audit.RecordTemplate(command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateDeleted, template.Id, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
```

- [ ] **Step 4: El endpoint**

En `TemplateManagementEndpoints.MapTemplateManagementEndpoints`:

```csharp
        group.MapDelete("/templates/{templateId:guid}", DeleteAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
```

```csharp
    private static async Task<IResult> DeleteAsync(Guid tenantId, Guid connectionId, Guid templateId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteTemplateCommand(tenantId, connectionId, templateId), cancellationToken);
        return Results.NoContent();
    }
```

- [ ] **Step 5: Correr y ver pasar**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~TemplateManagementDeleteApiTests" 2>&1 | tail -5
```

Esperado: `Passed!` con 6 pruebas.

- [ ] **Step 6: Commit**

```bash
git add src/Modules/Messaging tests/Modules/Messaging
git commit -m "feat(messaging): borrar una plantilla de WhatsApp en Meta y en el espejo, salvo que una campaña la use"
```

---

### Task 10: El webhook `message_template_status_update`

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookPayload.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookPayloadParser.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookDeliveryProcessor.cs`
- Modify: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MetaPayloads.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/WebhookPayloadParserTemplateTests.cs`, `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/TemplateStatusWebhookTests.cs`

**Interfaces:**
- Consumes: `MessagingDbContext.MessageTemplates`, `MessageTemplate.ApplyStatus`, `IMessagingConnectionDirectory.FindByAccountAsync`, `TemplateMirrorSync.Identifier` (Task 5; es `internal` y el parser vive en otro assembly, así que el parser trae su propia copia `ReadIdentifier`).
- Produces: `TemplateStatusChange(string WabaId, string MetaTemplateId, string? Name, string? Language, string Event, string? Reason) : WebhookChange`; `MetaPayloads.TemplateStatusUpdate(string wabaId, object templateId, string name, string language, string @event, string? reason)`.

- [ ] **Step 1: Pruebas unitarias del parser**

```csharp
// tests/Modules/Messaging/Modules.Messaging.UnitTests/WebhookPayloadParserTemplateTests.cs
using Modules.Messaging.Infrastructure.Webhook;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-14 §4.4 «Webhook»: <c>message_template_status_update</c> trae event, message_template_id (número o string), nombre,
/// idioma y reason; sin id o sin event cae en UnknownChange; los otros campos de plantilla siguen ignorados.</summary>
public sealed class WebhookPayloadParserTemplateTests
{
    private static string Payload(string valueJson, string wabaId = "222") =>
        $$$"""{"object":"whatsapp_business_account","entry":[{"id":"{{{wabaId}}}","changes":[{"field":"message_template_status_update","value":{{{valueJson}}}}]}]}""";

    [Fact]
    public void AnApprovedEventIsReadWithEveryField()
    {
        var change = Assert.Single(WebhookPayloadParser.Parse(Payload(
            """{"event":"APPROVED","message_template_id":"1407680676729941","message_template_name":"promo","message_template_language":"es","reason":"NONE"}""")));

        var status = Assert.IsType<TemplateStatusChange>(change);
        Assert.Equal(("222", "1407680676729941", "promo", "es", "APPROVED", "NONE"), (status.WabaId, status.MetaTemplateId, status.Name, status.Language, status.Event, status.Reason));
    }

    // Review Focus 1: Meta manda el id como número.
    [Fact]
    public void AMessageTemplateIdThatComesAsANumberIsRead()
    {
        var change = Assert.Single(WebhookPayloadParser.Parse(Payload(
            """{"event":"REJECTED","message_template_id":1407680676729941,"message_template_name":"promo","message_template_language":"es","reason":"INVALID_FORMAT"}""")));

        var status = Assert.IsType<TemplateStatusChange>(change);
        Assert.Equal(("1407680676729941", "REJECTED", "INVALID_FORMAT"), (status.MetaTemplateId, status.Event, status.Reason));
    }

    [Fact]
    public void AMissingReasonOrNameIsNull()
    {
        var status = Assert.IsType<TemplateStatusChange>(Assert.Single(WebhookPayloadParser.Parse(Payload("""{"event":"PAUSED","message_template_id":7}"""))));
        Assert.Equal((null, null, null), (status.Name, status.Language, status.Reason));
    }

    [Theory]
    [InlineData("""{"event":"APPROVED"}""")]
    [InlineData("""{"message_template_id":7}""")]
    [InlineData("""{"event":"","message_template_id":7}""")]
    [InlineData("""{"event":"APPROVED","message_template_id":true}""")]
    public void WithoutIdOrEventItIsAnUnknownChange(string value)
    {
        var change = Assert.Single(WebhookPayloadParser.Parse(Payload(value)));
        Assert.Equal("message_template_status_update", Assert.IsType<UnknownChange>(change).Field);
    }

    [Theory]
    [InlineData("message_template_quality_update")]
    [InlineData("template_category_update")]
    public void TheOtherTemplateFieldsStayIgnored(string field)
    {
        var json = $$$"""{"object":"whatsapp_business_account","entry":[{"id":"222","changes":[{"field":"{{{field}}}","value":{"message_template_id":7}}]}]}""";
        var change = Assert.Single(WebhookPayloadParser.Parse(json));
        Assert.Equal(field, Assert.IsType<UnknownChange>(change).Field);
    }
}
```

- [ ] **Step 2: Correr y ver fallar**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~WebhookPayloadParserTemplateTests" 2>&1 | tail -8
```

Esperado: `CS0246: The type or namespace name 'TemplateStatusChange' could not be found`.

- [ ] **Step 3: El record, el parser y el procesador**

En `WebhookPayload.cs`, después de `AccountUpdateChange`:

```csharp
/// <summary>Spec 2026-10-14 §4.4: <c>message_template_status_update</c>. Llega por WABA; <c>MetaTemplateId</c> viene como número o string.
/// <c>Reason</c> «NONE» lo descarta el dominio.</summary>
internal sealed record TemplateStatusChange(string WabaId, string MetaTemplateId, string? Name, string? Language, string Event, string? Reason) : WebhookChange;
```

En `WebhookPayloadParser.Parse`, en el `switch` de `field`:

```csharp
                        "message_template_status_update" => ParseTemplateStatus(wabaId ?? string.Empty, value),
```

Y los dos métodos, junto a `ParseAccountUpdate`:

```csharp
    /// <summary>Spec 2026-10-14 §4.4: sin <c>message_template_id</c> o sin <c>event</c> no hay qué aplicar y cae como campo ignorado, en el log.</summary>
    private static WebhookChange ParseTemplateStatus(string wabaId, JsonElement value)
    {
        var metaTemplateId = ReadIdentifier(value, "message_template_id");
        var @event = ReadString(value, "event");
        if (metaTemplateId is null || string.IsNullOrWhiteSpace(@event))
        {
            return new UnknownChange("message_template_status_update");
        }

        return new TemplateStatusChange(
            wabaId,
            metaTemplateId,
            ReadString(value, "message_template_name"),
            ReadString(value, "message_template_language"),
            @event,
            ReadString(value, "reason"));
    }

    /// <summary>Review Focus 1: Meta manda <c>message_template_id</c> como número JSON (y como string en el listado); se aceptan los dos.</summary>
    private static string? ReadIdentifier(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString()) => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var number) => number.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }
```

En `WebhookDeliveryProcessor`:

(a) Un `LoggerMessage` más, junto a `LogAccountEvent`:

```csharp
    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId}: template {MetaTemplateId} is now {Event} (reason {Reason}); {Updated} mirror row(s) updated.")]
    private static partial void LogTemplateStatus(ILogger logger, long deliveryId, string metaTemplateId, string @event, string? reason, int updated);
```

(b) En el `switch` de `ProcessAsync`, antes de `default`:

```csharp
                case TemplateStatusChange templateStatus:
                    await ProcessTemplateStatusAsync(deliveryId, templateStatus, cancellationToken);
                    break;
```

(c) El método, después de `ProcessAccountUpdateAsync`:

```csharp
    /// <summary>Spec 2026-10-14 §4.4: por cada conexión de la WABA, la fila del espejo con ese <c>meta_template_id</c> toma el estado y el
    /// motivo. Sin fila no pasa nada más que el log: el refresco la va a traer. Un <c>event</c> desconocido se guarda como texto (D-T15).
    /// No se audita (D-T14).</summary>
    private async Task ProcessTemplateStatusAsync(long deliveryId, TemplateStatusChange change, CancellationToken cancellationToken)
    {
        var updated = 0;
        var now = clock.UtcNow;
        // Directo al directorio, sin la caché de WebhookRouting: la lista de conexiones tiene que ser la de ahora.
        foreach (var route in await directory.FindByAccountAsync(change.WabaId, cancellationToken))
        {
            var template = await dbContext.MessageTemplates.SingleOrDefaultAsync(
                row => row.TenantId == route.TenantId && row.ConnectionId == route.ConnectionId && row.MetaTemplateId == change.MetaTemplateId,
                cancellationToken);
            if (template is null)
            {
                continue;
            }

            template.ApplyStatus(change.Event, change.Reason, now);
            updated++;
        }

        if (updated > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        LogTemplateStatus(logger, deliveryId, change.MetaTemplateId, change.Event, change.Reason, updated);
    }
```

- [ ] **Step 4: Ver pasar las unitarias**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~WebhookPayloadParser" 2>&1 | tail -5
```

Esperado: `Passed!` con las 9 nuevas (3 hechos + 6 casos de teoría) más las existentes del parser.

- [ ] **Step 5: El payload de prueba y las pruebas de integración**

En `MetaPayloads.cs`, junto a `AccountUpdate`:

```csharp
    /// <summary>Spec 2026-10-14 §4.4: <c>message_template_status_update</c>. <paramref name="templateId"/> va tal cual (número o string),
    /// porque Meta lo manda como número.</summary>
    public static string TemplateStatusUpdate(string wabaId, object templateId, string name, string language, string @event, string? reason)
    {
        var value = new JsonObject
        {
            ["event"] = @event,
            ["message_template_id"] = JsonValue.Create(templateId),
            ["message_template_name"] = name,
            ["message_template_language"] = language,
            ["reason"] = reason,
        };
        return Change("message_template_status_update", value.ToJsonString(), wabaId);
    }
```

```csharp
// tests/Modules/Messaging/Modules.Messaging.IntegrationTests/TemplateStatusWebhookTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-14 §4.4 «Webhook» de punta a punta: el evento cambia la fila del espejo de las conexiones de esa WABA (y sólo esas),
/// el catálogo de envío empieza a ver la aprobada, el rechazo conserva el motivo, y un id desconocido no escribe nada pero la entrega
/// queda procesada.</summary>
public sealed class TemplateStatusWebhookTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Listing = """
        {"data":[{"id":"1407","name":"promo","language":"es","status":"PENDING","category":"MARKETING","parameter_format":"POSITIONAL",
                  "components":[{"type":"BODY","text":"Hola {{1}}"}]}]}
        """;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Admin, HttpClient Anonymous);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var other = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Otra WABA", "113", "333");
        factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Listing);
        factory.MetaHandler.Respond("/333/message_templates", HttpStatusCode.OK, Listing);
        var admin = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, TemplatePermissions);
        // Llena los dos espejos: las dos conexiones tienen una plantilla con el mismo id de Meta, como pasa con WABAs distintas.
        await admin.GetAsync(TemplateManagementUrl(tenant.TenantId, connectionId), Ct);
        await admin.GetAsync(TemplateManagementUrl(tenant.TenantId, other), Ct);
        return new Fixture(factory, connectionString, tenant, connectionId, admin, factory.CreateClient());
    }

    private static Task<string> StatusOfAsync(Fixture f, string wabaName) =>
        ScalarAsync<string>(f.ConnectionString,
            """
            SELECT t.status || '|' || coalesce(t.status_reason, '-')
              FROM messaging.message_templates t JOIN integrations.connections c ON c.id = t.connection_id
             WHERE c.name = @name
            """, ("name", wabaName));

    [Fact]
    public async Task AnApprovedEventUpdatesOnlyThatWabaAndTheSendCatalogSeesIt()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        using var reader = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);
        var catalogUrl = $"/api/v1/tenants/{f.Tenant.TenantId}/messaging/connections/{f.ConnectionId}/templates";
        Assert.Empty((await reader.GetFromJsonAsync<JsonElement>(catalogUrl, Ct)).EnumerateArray());

        var response = await PostWebhookAsync(f.Anonymous, MetaPayloads.TemplateStatusUpdate("222", 1407L, "promo", "es", "APPROVED", "NONE"));
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("APPROVED|-", await StatusOfAsync(f, "Ventas"));
        Assert.Equal("PENDING|-", await StatusOfAsync(f, "Otra WABA"));
        var catalog = (await reader.GetFromJsonAsync<JsonElement>(catalogUrl, Ct)).EnumerateArray().ToArray();
        Assert.Equal("promo", Assert.Single(catalog).GetProperty("name").GetString());
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NOT NULL"));
    }

    [Fact]
    public async Task ARejectedEventKeepsTheReasonAndTheScreenShowsIt()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        await PostWebhookAsync(f.Anonymous, MetaPayloads.TemplateStatusUpdate("222", "1407", "promo", "es", "REJECTED", "INVALID_FORMAT"));
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal("REJECTED|INVALID_FORMAT", await StatusOfAsync(f, "Ventas"));
        var screen = await f.Admin.GetFromJsonAsync<JsonElement>(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);
        var template = Assert.Single(screen.GetProperty("templates").EnumerateArray());
        Assert.Equal(("REJECTED", "INVALID_FORMAT"), (template.GetProperty("status").GetString(), template.GetProperty("statusReason").GetString()));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE resource_type = 'message_template'"));
    }

    [Fact]
    public async Task AnUnknownTemplateIdWritesNothingAndTheDeliveryIsProcessed()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        await PostWebhookAsync(f.Anonymous, MetaPayloads.TemplateStatusUpdate("222", 999L, "nueva", "es", "APPROVED", "NONE"));
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal("PENDING|-", await StatusOfAsync(f, "Ventas"));
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NOT NULL"));
    }

    [Fact]
    public async Task AnUnknownEventIsStoredAsTextForTheScreen()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        await PostWebhookAsync(f.Anonymous, MetaPayloads.TemplateStatusUpdate("222", 1407L, "promo", "es", "flagged", null));
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal("FLAGGED|-", await StatusOfAsync(f, "Ventas"));
    }
}
```

- [ ] **Step 6: Correr y ver pasar**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~TemplateStatusWebhookTests|FullyQualifiedName~AccountUpdateTests|FullyQualifiedName~InboundIngestionTests" 2>&1 | tail -5
```

Esperado: todas en verde; las de `AccountUpdate` e `InboundIngestion` siguen igual (el `switch` nuevo no les cambia nada).

- [ ] **Step 7: Commit**

```bash
git add src/Modules/Messaging tests/Modules/Messaging
git commit -m "feat(messaging): el webhook message_template_status_update actualiza el espejo de plantillas"
```

---

### Task 11: La señal para Quotations, la documentación y la verificación final

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/QuotationTemplateReadiness.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs`
- Modify: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/QuotationTemplateProvisioningApiTests.cs`
- Modify: `README.md`, `docs/superpowers/plans/2026-10-09-mensajeria-whatsapp-handoff-meta.md`, `CLAUDE.md`

**Interfaces:**
- Consumes: `IQuotationTemplateReadiness` (Task 2), `MessagingDbContext.MessageTemplates`.
- Produces: `QuotationTemplateReadiness : IQuotationTemplateReadiness`, registrado scoped. Es lo que el spec del consumidor Quotations adaptará desde Bootstrapper.

- [ ] **Step 1: La prueba de la señal**

Al final de `QuotationTemplateProvisioningApiTests`:

```csharp
    [Fact]
    public async Task TheReadinessPortAnswersTrueOnlyWithAnApprovedQuotationTemplate()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        async Task<bool> ReadyAsync(Guid connectionId)
        {
            using var scope = f.Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<Modules.Messaging.Application.IQuotationTemplateReadiness>()
                .IsApprovedAsync(f.Tenant.TenantId, connectionId, Ct);
        }

        Assert.False(await ReadyAsync(f.ConnectionId));
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, QuotationUrl(f))).StatusCode);
        Assert.False(await ReadyAsync(f.ConnectionId));
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.message_templates SET status = 'APPROVED' WHERE kind = 'Quotation' AND tenant_id = @t", ("t", f.Tenant.TenantId));
        Assert.True(await ReadyAsync(f.ConnectionId));
        // Otra conexión del mismo tenant no está lista.
        Assert.False(await ReadyAsync(Guid.CreateVersion7()));
    }
```

Agrega `using Microsoft.Extensions.DependencyInjection;` al archivo.

- [ ] **Step 2: Correr y ver fallar**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~TheReadinessPortAnswersTrueOnlyWithAnApprovedQuotationTemplate" 2>&1 | tail -8
```

Esperado: `InvalidOperationException: No service for type 'Modules.Messaging.Application.IQuotationTemplateReadiness' has been registered.`

- [ ] **Step 3: La implementación y su registro**

```csharp
// src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/QuotationTemplateReadiness.cs
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Spec 2026-10-14 §4.3 y §5: un <c>EXISTS</c> sobre el espejo. Sin caché: es una consulta por índice y la respuesta decide si una
/// cotización se puede enviar por Meta.</summary>
internal sealed class QuotationTemplateReadiness(MessagingDbContext dbContext) : IQuotationTemplateReadiness
{
    public Task<bool> IsApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        dbContext.MessageTemplates.AsNoTracking().AnyAsync(
            template => template.TenantId == tenantId && template.ConnectionId == connectionId
                && template.Kind == MessageTemplateKind.Quotation && template.Status == MessageTemplateStatuses.Approved,
            cancellationToken);
}
```

En `MessagingInfrastructureExtensions.cs`, junto a `IMessageTemplateRepository`:

```csharp
        // Spec 2026-10-14 §5: la señal que el consumidor Quotations leerá por un adaptador en Bootstrapper.
        services.AddScoped<IQuotationTemplateReadiness, QuotationTemplateReadiness>();
```

- [ ] **Step 4: Ver pasar**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~QuotationTemplateProvisioningApiTests" 2>&1 | tail -5
```

Esperado: `Passed!` con 8 pruebas.

- [ ] **Step 5: Documentación**

**README.md**, en la sección de Mensajería (busca `## Mensajería` o `### Mensajería (WhatsApp Cloud)`), agrega al final una subsección:

```markdown
### Plantillas de WhatsApp desde QEP

Spec: `docs/superpowers/specs/2026-10-14-gestor-de-plantillas-whatsapp-design.md`. El admin (permiso
`messaging.template.manage`) gestiona las plantillas de cada conexión `whatsapp-cloud` bajo
`/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management`:

| Método y ruta | Qué hace |
| --- | --- |
| `GET /` | La pantalla: conexión, estado de la plantilla de cotización, última sincronización y todas las plantillas con su estado y motivo de rechazo. La primera lectura llena el espejo. |
| `POST /templates` | Crea una plantilla libre (texto, variables con nombre, pie, botones) y la manda a aprobar. |
| `POST /quotation` | Aprovisiona la plantilla de cotización `qep_cotizacion` con su PDF de ejemplo; reintenta si la anterior fue rechazada; adopta una igual creada en Meta. |
| `DELETE /templates/{templateId}` | Borra en Meta (sólo ese idioma) y en el espejo; 422 si una campaña programada o enviándose la usa. |
| `POST /sync` | Sincroniza el espejo contra Meta. |

El espejo vive en `messaging.message_templates` y lo alimentan la creación, el webhook
`message_template_status_update` (hay que suscribirlo en la app de Meta: handoff, paso 5) y el refresco.
El catálogo de envío (`GET /connections/{id}/templates`) lee del espejo las `APPROVED`; ya no hay caché de
cinco minutos. Los estados y motivos viajan con el nombre que Meta les da; el diccionario en español lo tiene
el frontend.

La Resumable Upload del PDF de ejemplo usa `Meta:App:AppId` (ya en el ConfigMap) y el token de la conexión.
**A confirmar en producción:** que ese token sirva para `/{AppId}/uploads`; si no, el aprovisionamiento
falla con `messaging.template.upload_failed` y hay que pasar a un token de la app.
```

**Handoff** (`docs/superpowers/plans/2026-10-09-mensajeria-whatsapp-handoff-meta.md`): en el paso 5, después de `user_id_update`, agrega:

```markdown
   Desde el gestor de plantillas (spec 2026-10-14) suscribe también `message_template_status_update`: sin él, las
   aprobaciones y rechazos sólo se ven al refrescar la pantalla de plantillas.
```

Y en «Notas a confirmar en producción», al final:

```markdown
- **Token de la Resumable Upload.** El aprovisionamiento de la plantilla de cotización sube el PDF de ejemplo a
  `/{AppId}/uploads` con el token de negocio de la conexión. La documentación lo ejemplifica con un token de
  usuario. Si el primer aprovisionamiento real responde `messaging.template.upload_failed`, mira el log de
  `template-upload-start` (status y `code`) antes de cambiar nada.
- **Textos de `error_user_title`.** QEP distingue «nombre ya existe» y «tope de plantillas» por ese texto
  («already exist», «limit»). Si un rechazo real llega con otro título, cae en `rejected_by_meta` con el
  `error_user_msg` de Meta; anota el título para ajustar `TemplateGraphErrors`.
```

**CLAUDE.md**, en «Gotchas verificados», después del gotcha del webhook de Meta:

```markdown
- **Un fixture de `message_templates` sin `id` deja el catálogo vacío** (spec 2026-10-14). El espejo
  `messaging.message_templates` indexa por `meta_template_id` y el refresco salta los elementos sin `id`; el
  síntoma es `messaging.template.not_available` en una prueba que antes pasaba, no un error del espejo. Y el
  webhook `message_template_status_update` hay que suscribirlo en la app de Meta: sin él los estados sólo cambian
  con el refresco.
```

- [ ] **Step 6: Verificación completa**

```bash
dotnet restore --locked-mode 2>&1 | tail -2
dotnet format --verify-no-changes 2>&1 | tail -5
dotnet build --no-restore 2>&1 | grep -E " error |warn.*Messaging|Build succeeded" | head -20
dotnet test --no-build 2>&1 | tail -15
```

Esperado: restore sin `NU1004`; `format` sin cambios (si reporta, corre `dotnet format` y revisa el diff); build sin errores ni warnings nuevos en Messaging; **toda** la suite en verde, incluidas `ArchitectureTests` (Messaging sigue sin referenciar Integrations) y las de Campaigns, SendTemplate y StartConversation. Si `format` cambió archivos, inclúyelos en el commit.

- [ ] **Step 7: Commit final y estado de la rama**

```bash
git add -A
git commit -m "feat(messaging): señal de plantilla de cotización aprobada y documentación del gestor de plantillas"
git log --oneline develop..HEAD
git status --short
```

Esperado: unos trece commits sobre `develop`, árbol limpio. **No se mergea desde el plan:** el merge a `develop` lo decide el owner con la skill `superpowers:finishing-a-development-branch`. Nunca a `main` (CLAUDE.md).

---

## Lo que queda fuera de este plan

- El frontend (`qep-frontend`): la pantalla `template-management`, el formulario de crear, el botón de la plantilla de cotización y el diccionario en español de estados y motivos. Slice aparte (spec §6).
- El consumidor Quotations: el resolver de canal (Meta si hay conexión oficial, si no Zenvia), el guard en `SendQuotation` con `IQuotationTemplateReadiness` y el sender de Meta con encabezado de documento (spec §5).
- Editar plantillas, encabezado multimedia en las libres, `AUTHENTICATION`, carrusel, flujos, los webhooks de calidad y de categoría (spec §8).

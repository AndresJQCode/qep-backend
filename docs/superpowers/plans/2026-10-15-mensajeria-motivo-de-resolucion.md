# Motivo de resolución — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que `POST …/resolve` acepte un motivo opcional (existente por id o nuevo por nombre, creado en la misma transacción), que el historial y el resumen lo muestren y que el catálogo se pueda renombrar y archivar con un permiso propio.

**Architecture:** Agregado nuevo `ResolutionReason` en `Modules.Messaging.Domain`, con su tabla `messaging.resolution_reasons` y un índice único parcial por expresión. `Conversation` guarda `ResolutionReasonId` del estado vigente y el evento `Resolved` lleva `reasonId` en su JSON. La lectura resuelve nombres (incluidos los archivados) por un puerto `IResolutionReasonNames`, como los nombres de miembros.

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, xUnit v3, Testcontainers (Postgres).

**Spec:** `docs/superpowers/specs/2026-10-15-mensajeria-motivo-de-resolucion-design.md` (léela entera antes de empezar; este plan argumenta desde ella).

**Rama:** `feature/motivo-de-resolucion` desde `develop`. Antes del primer commit corre `git branch --show-current`; si dice `main`, para. El frontend tiene su propio plan en `qep-frontend`.

## Global Constraints

- TDD obligatorio: RED antes que GREEN, con la salida literal de los dos en el reporte de cada tarea.
- Commits en conventional commits, **sin atribución de IA** (sin `Co-Authored-By`).
- No corras la suite completa: sólo `Modules.Messaging.UnitTests`, `Modules.Messaging.IntegrationTests` (filtrado) y `ArchitectureTests`.
- `Api.exe`/`dotnet run` corriendo bloquea `build`, `test` y `ef`: detenlo primero.
- Nombre del motivo: `Trim`, de 1 a 60 caracteres (`ResolutionReason.NameMaxLength = 60`). Tope: 100 vigentes por tenant (`MaxPerTenant = 100`).
- Índice: `IX_resolution_reasons_tenant_name` sobre `(tenant_id, lower(name)) WHERE deleted_at IS NULL`.
- Permiso: `messaging.resolution_reason.manage`, de núcleo, de fábrica en `admin` **y** `advisor`.
- Códigos: `messaging.resolution_reason.not_found` (404), `.name_taken` (409, `ResourceConflictException`), `.name_required` (422), `.name_too_long` (422), `.limit_reached` (422).
- Textos para personas (mensajes de `errors`) en español colombiano y tuteando. Los códigos, rutas y enums no se traducen.
- Todo método de repositorio recibe `tenantId`. Los handlers revalidan con `MessagingAuthorization.EnsureAsync` antes de tocar nada.
- La traducción de errores de base va en `MessagingUnitOfWork` (Infrastructure) y **por nombre de índice**.

## Review Focus

1. **Mayúsculas con tilde.** «ATENCIÓN» y «Atención» son el mismo motivo. Se compara con `lower()` de Postgres en los dos lados, la misma expresión del índice, así que .NET y la base no pueden discrepar. La prueba vive en la Task 3.
2. **Renombrar a su propio nombre con otro caso** («venta cerrada» → «Venta cerrada») no choca con su propia fila: es la misma fila del índice. La prueba vive en la Task 6.
3. **Renombrar al nombre de un motivo archivado** está permitido, porque el índice es parcial. La prueba vive en la Task 6.
4. **El borde de 60/61 caracteres** en `reasonName`: 60 resuelve y 61 da 422 `validation.failed` con `errors.reasonName`. La prueba vive en la Task 4.
5. **Los espacios internos no se colapsan** (la spec dice sólo `Trim`): «Venta  cerrada» con dos espacios es otro motivo. Se fija con una prueba para que nadie lo «arregle» sin cambiar la spec. La prueba vive en la Task 1.

---

## Mapa de archivos

| Archivo | Acción | Responsabilidad |
| --- | --- | --- |
| `src/Modules/Messaging/Modules.Messaging.Domain/ResolutionReason.cs` | Crear | Agregado: normaliza, renombra, archiva, tope |
| `src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs` | Modificar | Cinco códigos nuevos |
| `src/Modules/Messaging/Modules.Messaging.Domain/Conversation.cs` | Modificar | `ResolutionReasonId`, `Resolve(now, reasonId)`, `Reopen` lo limpia |
| `src/Modules/Messaging/Modules.Messaging.Domain/ConversationEvent.cs` | Modificar | `ReasonId` |
| `src/Modules/Messaging/Modules.Messaging.Application/ConversationEventJson.cs` | Modificar | `reasonId` en el JSON |
| `src/Modules/Messaging/Modules.Messaging.Application/ResolutionReasonPorts.cs` | Crear | `IResolutionReasonRepository`, `IResolutionReasonNames` |
| `src/Modules/Messaging/Modules.Messaging.Application/ResolutionReasons.cs` | Crear | Mensajes, DTO, elección del motivo, list/rename/archive |
| `src/Modules/Messaging/Modules.Messaging.Application/ConversationLifecycle.cs` | Modificar | Comando, validador y handler de resolver |
| `src/Modules/Messaging/Modules.Messaging.Application/IMessagingAuditRecorder.cs` | Modificar | `RecordResolutionReason` y acciones |
| `src/Modules/Messaging/Modules.Messaging.Application/MessagingPermissions.cs` | Modificar | `ResolutionReasonManage` |
| `src/Modules/Messaging/Modules.Messaging.Application/IConversationQueries.cs` | Modificar | `ConversationRow.ResolutionReasonId` |
| `src/Modules/Messaging/Modules.Messaging.Application/MessagingDtos.cs` | Modificar | `ResolutionReasonRefDto`, campos en `ConversationSummary` y `MessageEventDto` |
| `src/Modules/Messaging/Modules.Messaging.Application/MessagingSupport.cs` | Modificar | Nombres en el resumen y en el evento |
| `src/Modules/Messaging/Modules.Messaging.Application/ListMessages.cs` | Modificar | Pasa los nombres de motivos |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingDbContext.cs` | Modificar | Entidad, columna, FK, constante del índice |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/Migrations/*_AddResolutionReasons.cs` | Crear (ef) | Tabla, columna, FK e índice por SQL |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ResolutionReasonRepository.cs` | Crear | Repositorio + nombres |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingUnitOfWork.cs` | Modificar | `name_taken` por nombre de índice |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingAuditRecorder.cs` | Modificar | `RecordResolutionReason` |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ConversationQueries.cs` | Modificar | Proyección con `ResolutionReasonId` |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/InboundIngestion.cs` | Modificar | `resolution_reason_id = NULL` al reabrir |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs` | Modificar | Registro del repositorio |
| `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs` | Modificar | Cuerpo opcional de resolve, `RequireVersion` interno, mapeo del catálogo |
| `src/Modules/Messaging/Modules.Messaging.Api/ResolutionReasonEndpoints.cs` | Crear | `GET`, `PATCH`, `DELETE` del catálogo |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` | Modificar | Handlers, roles, definición y política del permiso |
| `tests/Modules/Messaging/Modules.Messaging.UnitTests/ResolutionReasonTests.cs` | Crear | Dominio |
| `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationTests.cs` | Modificar | Resolver/reabrir con motivo |
| `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationEventJsonTests.cs` | Modificar | `reasonId` |
| `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingTestBed.cs` | Modificar | `FakeResolutionReasonNames` en el builder |
| `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ResolutionReasonPersistenceTests.cs` | Crear | Índice, FK, repositorio |
| `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ResolutionReasonApiTests.cs` | Crear | Resolver con motivo, lectura, catálogo |
| `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs` | Modificar | Caso del permiso |
| `README.md` | Modificar | Tabla de endpoints |

**Comandos de prueba** (Docker corriendo para las de integración):

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~<Clase>"
```

---

### Task 1: Agregado `ResolutionReason` y sus códigos

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Domain/ResolutionReason.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs` (al final de la clase, después de `QuickReplyNotFound`)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/ResolutionReasonTests.cs`

**Interfaces:**
- Produces: `ResolutionReason` con `Create(Guid id, Guid tenantId, string? name, DateTimeOffset now)`, `Rename(string? name, DateTimeOffset now)`, `Archive(DateTimeOffset now)`, `static EnsureTenantCapacity(int activeCount)`, `static string TrimName(string?)`, `static bool IsValidName(string trimmed)`, propiedades `Id`, `TenantId`, `Name`, `CreatedAt`, `UpdatedAt`, `DeletedAt`, `Version`, `IsArchived`. Constantes `NameMaxLength = 60` y `MaxPerTenant = 100`.
- Produces: `MessagingErrorCodes.ResolutionReasonNotFound`, `ResolutionReasonNameTaken`, `ResolutionReasonNameRequired`, `ResolutionReasonNameTooLong` y `ResolutionReasonLimitReached`.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-15 §3.2: el motivo se guarda recortado, de 1 a 60 caracteres; renombrar y archivar suben la versión; el tope
/// es de 100 vigentes por tenant.</summary>
public sealed class ResolutionReasonTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-0000000000c1");

    [Fact]
    public void CreateTrimsTheNameAndStartsAtVersionOne()
    {
        var id = Guid.CreateVersion7();

        var reason = ResolutionReason.Create(id, TenantId, "  Venta cerrada  ", Now);

        Assert.Equal(id, reason.Id);
        Assert.Equal(TenantId, reason.TenantId);
        Assert.Equal("Venta cerrada", reason.Name);
        Assert.Equal(1, reason.Version);
        Assert.Equal(Now, reason.CreatedAt);
        Assert.Equal(Now, reason.UpdatedAt);
        Assert.Null(reason.DeletedAt);
        Assert.False(reason.IsArchived);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankNameIsRequired(string? name)
    {
        var error = Assert.Throws<MessagingDomainException>(() => ResolutionReason.Create(Guid.CreateVersion7(), TenantId, name, Now));

        Assert.Equal(MessagingErrorCodes.ResolutionReasonNameRequired, error.Code);
    }

    [Fact]
    public void SixtyCharactersFitAndSixtyOneDoNot()
    {
        var sixty = new string('a', ResolutionReason.NameMaxLength);

        Assert.Equal(sixty, ResolutionReason.Create(Guid.CreateVersion7(), TenantId, $" {sixty} ", Now).Name);
        var error = Assert.Throws<MessagingDomainException>(() => ResolutionReason.Create(Guid.CreateVersion7(), TenantId, sixty + "a", Now));
        Assert.Equal(MessagingErrorCodes.ResolutionReasonNameTooLong, error.Code);
    }

    // Review Focus 5: la spec dice sólo Trim. Dos espacios adentro son otro nombre; cambiarlo es cambiar la spec.
    [Fact]
    public void InnerSpacesAreKept() =>
        Assert.Equal("Venta  cerrada", ResolutionReason.Create(Guid.CreateVersion7(), TenantId, "Venta  cerrada", Now).Name);

    [Fact]
    public void RenameTrimsBumpsTheVersionAndARejectionLeavesItAsItWas()
    {
        var reason = ResolutionReason.Create(Guid.CreateVersion7(), TenantId, "Venta cerada", Now);

        reason.Rename(" Venta cerrada ", Now.AddMinutes(1));
        var error = Assert.Throws<MessagingDomainException>(() => reason.Rename(" ", Now.AddMinutes(2)));

        Assert.Equal(MessagingErrorCodes.ResolutionReasonNameRequired, error.Code);
        Assert.Equal("Venta cerrada", reason.Name);
        Assert.Equal(2, reason.Version);
        Assert.Equal(Now.AddMinutes(1), reason.UpdatedAt);
    }

    [Fact]
    public void ArchiveStampsDeletedAtAndBumpsTheVersion()
    {
        var reason = ResolutionReason.Create(Guid.CreateVersion7(), TenantId, "Spam", Now);

        reason.Archive(Now.AddMinutes(5));

        Assert.True(reason.IsArchived);
        Assert.Equal(Now.AddMinutes(5), reason.DeletedAt);
        Assert.Equal(Now.AddMinutes(5), reason.UpdatedAt);
        Assert.Equal(2, reason.Version);
    }

    [Fact]
    public void TheHundredAndFirstIsRejected()
    {
        ResolutionReason.EnsureTenantCapacity(ResolutionReason.MaxPerTenant - 1);

        var error = Assert.Throws<MessagingDomainException>(() => ResolutionReason.EnsureTenantCapacity(ResolutionReason.MaxPerTenant));

        Assert.Equal(MessagingErrorCodes.ResolutionReasonLimitReached, error.Code);
    }
}
```

- [ ] **Step 2: Córrelas y verifica que fallan**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~ResolutionReasonTests"`
Expected: FAIL de compilación, `CS0246: The type or namespace name 'ResolutionReason' could not be found`.

- [ ] **Step 3: Agrega los códigos**

Agrega al final de la clase en `MessagingErrorCodes.cs`, con el mismo estilo de los demás:

```csharp
    /// <summary>Spec 2026-10-15 §3.10: el motivo de resolución no está vigente en el tenant (archivado, de otro tenant o inexistente).</summary>
    public const string ResolutionReasonNotFound = "messaging.resolution_reason.not_found";

    /// <summary>Spec 2026-10-15 §3.10 y D-R11: otro motivo vigente del tenant ya usa ese nombre (409).</summary>
    public const string ResolutionReasonNameTaken = "messaging.resolution_reason.name_taken";

    public const string ResolutionReasonNameRequired = "messaging.resolution_reason.name_required";

    public const string ResolutionReasonNameTooLong = "messaging.resolution_reason.name_too_long";

    /// <summary>Spec 2026-10-15 D-R9: el motivo 101 vigente del tenant.</summary>
    public const string ResolutionReasonLimitReached = "messaging.resolution_reason.limit_reached";
```

- [ ] **Step 4: Escribe el agregado**

```csharp
namespace Modules.Messaging.Domain;

/// <summary>
/// Spec 2026-10-15 §3.1–§3.2: un motivo del catálogo de resolución del tenant. Lo crea el resolver cuando el nombre no existe (D-R4);
/// renombrar y archivar es de <c>messaging.resolution_reason.manage</c>. Archivar no borra la fila (D-R5): el historial guarda el id y
/// resuelve el nombre al leer.
///
/// La unicidad del nombre (sin distinguir mayúsculas, entre los vigentes) no la arbitra esta clase sino
/// <c>IX_resolution_reasons_tenant_name</c>, y <c>MessagingUnitOfWork</c> la traduce a <c>messaging.resolution_reason.name_taken</c>.
/// </summary>
public sealed class ResolutionReason
{
    public const int NameMaxLength = 60;

    /// <summary>D-R9: el tope de motivos vigentes por tenant. Contarlos es del caso de uso; acá vive el número y su error.</summary>
    public const int MaxPerTenant = 100;

    // EF Core materializa por acá; Create es el único punto de entrada.
    private ResolutionReason()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public long Version { get; private set; }

    public bool IsArchived => DeletedAt is not null;

    /// <summary>El id lo pone quien llama: el resolver lo necesita para el evento antes de saber si la conversación se puede resolver.</summary>
    public static ResolutionReason Create(Guid id, Guid tenantId, string? name, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = tenantId,
        Name = NormalizeName(name),
        CreatedAt = now,
        UpdatedAt = now,
        Version = 1,
    };

    /// <summary>Valida antes de asignar: un 422 deja el motivo como estaba.</summary>
    public void Rename(string? name, DateTimeOffset now)
    {
        var normalized = NormalizeName(name);
        Name = normalized;
        UpdatedAt = now;
        Version++;
    }

    public void Archive(DateTimeOffset now)
    {
        DeletedAt = now;
        UpdatedAt = now;
        Version++;
    }

    /// <summary>El que se intenta crear sería el <c>MaxPerTenant + 1</c>.</summary>
    public static void EnsureTenantCapacity(int activeCount)
    {
        if (activeCount >= MaxPerTenant)
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.ResolutionReasonLimitReached, $"A tenant cannot have more than {MaxPerTenant} resolution reasons.");
        }
    }

    // Recortar es parte del invariante: el índice único trata « Spam» y «Spam» como el mismo nombre sólo si el valor guardado ya viene
    // recortado. Los espacios de adentro se conservan (spec §3.2: sólo Trim).
    public static string TrimName(string? name) => (name ?? string.Empty).Trim();

    public static bool IsValidName(string trimmedName)
    {
        ArgumentNullException.ThrowIfNull(trimmedName);
        return trimmedName.Length is > 0 and <= NameMaxLength;
    }

    private static string NormalizeName(string? name)
    {
        var trimmed = TrimName(name);
        if (trimmed.Length == 0)
        {
            throw new MessagingDomainException(MessagingErrorCodes.ResolutionReasonNameRequired, "The resolution reason name is required.");
        }

        return trimmed.Length > NameMaxLength
            ? throw new MessagingDomainException(
                MessagingErrorCodes.ResolutionReasonNameTooLong, $"The resolution reason name cannot exceed {NameMaxLength} characters.")
            : trimmed;
    }
}
```

- [ ] **Step 5: Córrelas y verifica que pasan**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~ResolutionReasonTests"`
Expected: PASS, las 9 pruebas (incluye los 3 casos del `Theory`).

- [ ] **Step 6: Commit**

```bash
git add src/Modules/Messaging/Modules.Messaging.Domain/ResolutionReason.cs src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs tests/Modules/Messaging/Modules.Messaging.UnitTests/ResolutionReasonTests.cs
git commit -m "feat(messaging): agregado ResolutionReason con sus reglas de nombre y tope"
```

---

### Task 2: El motivo en la conversación y en el evento

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/Conversation.cs` (propiedades, junto a `Status`; métodos `Resolve` y `Reopen`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/ConversationEvent.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/ConversationEventJson.cs` (`Serialize` y `Parse`)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationTests.cs`, `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationEventJsonTests.cs`

**Interfaces:**
- Produces: `Conversation.ResolutionReasonId` (`Guid?`); `Conversation.Resolve(DateTimeOffset now, Guid? reasonId = null)`; `Reopen(now)` deja `ResolutionReasonId = null`.
- Produces: `ConversationEvent(..., Guid? LinkedConversationId = null, Guid? ReasonId = null)`; el JSON lleva la clave `reasonId`.

- [ ] **Step 1: Escribe las pruebas que fallan**

Agrega a `ConversationTests.cs`:

```csharp
    // Spec 2026-10-15 D-R2: la conversación guarda el motivo vigente y reabrir lo limpia.
    [Fact]
    public void ResolveKeepsTheReasonAndReopenClearsIt()
    {
        var conversation = Open();
        var reasonId = Guid.CreateVersion7();

        conversation.Resolve(Now.AddMinutes(1), reasonId);
        Assert.Equal(reasonId, conversation.ResolutionReasonId);

        conversation.Reopen(Now.AddMinutes(2));
        Assert.Null(conversation.ResolutionReasonId);

        conversation.Resolve(Now.AddMinutes(3));
        Assert.Null(conversation.ResolutionReasonId);
    }

    [Fact]
    public void AResolveThatFailsDoesNotTouchTheReason()
    {
        var conversation = Open();
        var first = Guid.CreateVersion7();
        conversation.Resolve(Now.AddMinutes(1), first);

        Assert.Throws<MessagingDomainException>(() => conversation.Resolve(Now.AddMinutes(2), Guid.CreateVersion7()));

        Assert.Equal(first, conversation.ResolutionReasonId);
    }
```

Agrega a `ConversationEventJsonTests.cs`:

```csharp
    private static readonly Guid Reason = Guid.Parse("01900000-0000-7000-8000-0000000000a3");

    // Spec 2026-10-15 §3.2: el motivo viaja como reasonId, y sólo cuando lo hay.
    [Fact]
    public void TheReasonIsWrittenAsReasonId() =>
        Assert.Equal(
            $$"""{"type":"Resolved","actor":"{{Actor}}","reasonId":"{{Reason}}"}""",
            ConversationEventJson.Serialize(new ConversationEvent(ConversationEventType.Resolved, Actor: Actor, ReasonId: Reason)));

    [Fact]
    public void TheReasonRoundTrips()
    {
        var value = new ConversationEvent(ConversationEventType.Resolved, Actor: Actor, ReasonId: Reason);

        Assert.Equal(value, ConversationEventJson.Parse(ConversationEventJson.Serialize(value)));
    }

    // Los eventos escritos antes de esta spec no tienen la clave: se leen sin motivo.
    [Fact]
    public void AnOldResolvedEventReadsWithoutAReason() =>
        Assert.Equal(
            new ConversationEvent(ConversationEventType.Resolved, Actor: Actor),
            ConversationEventJson.Parse($$"""{"type":"Resolved","actor":"{{Actor}}"}"""));
```

- [ ] **Step 2: Córrelas y verifica que fallan**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~ConversationTests|FullyQualifiedName~ConversationEventJsonTests"`
Expected: FAIL de compilación: `CS1739: The best overload for 'ConversationEvent' does not have a parameter named 'ReasonId'` y `CS1061: 'Conversation' does not contain a definition for 'ResolutionReasonId'`.

- [ ] **Step 3: Implementa**

En `Conversation.cs`, después de `public ConversationStatus Status { get; private set; }`:

```csharp
    /// <summary>Spec 2026-10-15 D-R2 y D-R7: el motivo de la resolución vigente, sólo para que el resumen lo dibuje. El de cada
    /// resolución vive en su evento <c>Resolved</c>. <c>null</c> si está abierta o se resolvió sin motivo; la ingesta lo limpia por SQL
    /// al reabrir (§3.6).</summary>
    public Guid? ResolutionReasonId { get; private set; }
```

Reemplaza `Resolve` y `Reopen`:

```csharp
    public void Resolve(DateTimeOffset now, Guid? reasonId = null)
    {
        if (Status == ConversationStatus.Resolved)
        {
            throw new MessagingDomainException(MessagingErrorCodes.AlreadyResolved, "The conversation is already resolved.");
        }

        Status = ConversationStatus.Resolved;
        ResolutionReasonId = reasonId;
        Touch(now);
    }

    public void Reopen(DateTimeOffset now)
    {
        if (Status == ConversationStatus.Open)
        {
            throw new MessagingDomainException(MessagingErrorCodes.AlreadyOpen, "The conversation is already open.");
        }

        Status = ConversationStatus.Open;
        ResolutionReasonId = null;
        Touch(now);
    }
```

En `ConversationEvent.cs`, agrega el parámetro al final y documenta en el `<summary>`: «<see cref="ReasonId"/> sólo lo usa <c>Resolved</c> (spec 2026-10-15): el id del motivo; el nombre se resuelve al leer».

```csharp
public sealed record ConversationEvent(
    ConversationEventType Type,
    Guid? Actor = null,
    Guid? Target = null,
    Guid? Previous = null,
    Guid? CustomerId = null,
    Guid? LinkedConversationId = null,
    Guid? ReasonId = null);
```

En `ConversationEventJson.Serialize`, después de `WriteId(writer, "linkedConversationId", value.LinkedConversationId);`:

```csharp
            WriteId(writer, "reasonId", value.ReasonId);
```

En `Parse`, el `return` pasa a:

```csharp
            return new ConversationEvent(
                parsed, ReadId(root, "actor"), ReadId(root, "target"), ReadId(root, "previous"), ReadId(root, "customerId"),
                ReadId(root, "linkedConversationId"), ReadId(root, "reasonId"));
```

- [ ] **Step 4: Córrelas y verifica que pasan**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~ConversationTests|FullyQualifiedName~ConversationEventJsonTests"`
Expected: PASS, incluidas las pruebas que ya existían (`EveryFieldRoundTrips` sigue pasando: no pone `ReasonId`).

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Messaging/Modules.Messaging.Domain/Conversation.cs src/Modules/Messaging/Modules.Messaging.Domain/ConversationEvent.cs src/Modules/Messaging/Modules.Messaging.Application/ConversationEventJson.cs tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationTests.cs tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationEventJsonTests.cs
git commit -m "feat(messaging): el motivo de resolución en la conversación y en el evento Resolved"
```

---

### Task 3: Persistencia — tabla, índice, FK, repositorio y traducción del índice

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/ResolutionReasonPorts.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ResolutionReasonRepository.cs`
- Create (con `dotnet ef`): `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/Migrations/<timestamp>_AddResolutionReasons.cs` (+ `.Designer.cs` y el snapshot)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingDbContext.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingUnitOfWork.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs` (junto a `AddScoped<IQuickReplyRepository, QuickReplyRepository>()`, línea ~91)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ResolutionReasonPersistenceTests.cs`

**Interfaces:**
- Consumes: `ResolutionReason` (Task 1), `Conversation.ResolutionReasonId` (Task 2).
- Produces (Application):

```csharp
public interface IResolutionReasonRepository
{
    Task<ResolutionReason?> FindActiveAsync(Guid tenantId, Guid reasonId, CancellationToken cancellationToken);           // con tracking
    Task<ResolutionReason?> FindActiveByNameAsync(Guid tenantId, string trimmedName, CancellationToken cancellationToken); // sin tracking
    Task<int> CountActiveAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ResolutionReason>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken);              // por nombre
    void Add(ResolutionReason reason);
}

public interface IResolutionReasonNames
{
    Task<IReadOnlyDictionary<Guid, string>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> reasonIds, CancellationToken cancellationToken);
}
```

- Produces (Infrastructure): `MessagingDbContext.ResolutionReasonNameIndex = "IX_resolution_reasons_tenant_name"`, `DbSet<ResolutionReason> ResolutionReasons`. La violación de ese índice sale de `MessagingUnitOfWork.SaveChangesAsync` como `ResourceConflictException(MessagingErrorCodes.ResolutionReasonNameTaken)`.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-15 §3.1 contra Postgres: el nombre es único entre los vigentes de un tenant sin distinguir mayúsculas
/// (<c>IX_resolution_reasons_tenant_name</c>, parcial y por expresión), la violación sale como <c>name_taken</c> (409) por nombre de
/// índice, un archivado no bloquea el nombre y los nombres para leer incluyen los archivados.</summary>
public sealed class ResolutionReasonPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<ResolutionReason> SaveAsync(QepApiFactory factory, Guid tenantId, string name)
    {
        using var scope = factory.Services.CreateScope();
        var reason = ResolutionReason.Create(Guid.CreateVersion7(), tenantId, name, DateTimeOffset.UtcNow);
        scope.ServiceProvider.GetRequiredService<IResolutionReasonRepository>().Add(reason);
        await scope.ServiceProvider.GetRequiredService<IMessagingUnitOfWork>().SaveChangesAsync(Ct);
        return reason;
    }

    [Fact]
    public async Task TheIndexIsUniquePartialAndByLowerName()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        _ = factory.Services;

        var definition = await ScalarAsync<string>(connectionString,
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'messaging' AND indexname = 'IX_resolution_reasons_tenant_name'");

        Assert.Contains("UNIQUE", definition, StringComparison.Ordinal);
        Assert.Contains("lower((name)::text)", definition, StringComparison.Ordinal);
        Assert.Contains("WHERE (deleted_at IS NULL)", definition, StringComparison.Ordinal);
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM pg_constraint WHERE conname = 'FK_conversations_resolution_reasons' AND confdeltype = 'r'"));
    }

    [Fact]
    public async Task ANameTakenInTheSameTenantIsAConflictButNotInAnotherOrAfterArchiving()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var first = await SaveAsync(factory, tenantId, "Venta cerrada");

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => SaveAsync(factory, tenantId, "VENTA CERRADA"));
        await SaveAsync(factory, Guid.CreateVersion7(), "Venta cerrada");
        await ExecuteAsync(connectionString, "UPDATE messaging.resolution_reasons SET deleted_at = now() WHERE id = @id", ("id", first.Id));
        await SaveAsync(factory, tenantId, "Venta cerrada");

        Assert.Equal(MessagingErrorCodes.ResolutionReasonNameTaken, error.Code);
        Assert.Equal(3L, await CountAsync(connectionString, "SELECT count(*) FROM messaging.resolution_reasons"));
    }

    // Review Focus 1: la búsqueda usa lower() de Postgres en los dos lados, la misma expresión del índice.
    [Fact]
    public async Task FindByNameIgnoresCaseWithAccentsAndSkipsArchived()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var atencion = await SaveAsync(factory, tenantId, "Atención");
        var spam = await SaveAsync(factory, tenantId, "Spam");
        await ExecuteAsync(connectionString, "UPDATE messaging.resolution_reasons SET deleted_at = now() WHERE id = @id", ("id", spam.Id));
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IResolutionReasonRepository>();

        var found = await repository.FindActiveByNameAsync(tenantId, "ATENCIÓN", Ct);
        var archived = await repository.FindActiveByNameAsync(tenantId, "spam", Ct);
        var otherTenant = await repository.FindActiveByNameAsync(Guid.CreateVersion7(), "Atención", Ct);

        Assert.Equal(atencion.Id, found?.Id);
        Assert.Null(archived);
        Assert.Null(otherTenant);
        Assert.Equal(1, await repository.CountActiveAsync(tenantId, Ct));
        Assert.Null(await repository.FindActiveAsync(tenantId, spam.Id, Ct));
        Assert.Equal(["Atención"], (await repository.ListActiveAsync(tenantId, Ct)).Select(reason => reason.Name));
    }

    [Fact]
    public async Task NamesForReadingIncludeArchivedAndStayInTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var spam = await SaveAsync(factory, tenantId, "Spam");
        var foreign = await SaveAsync(factory, Guid.CreateVersion7(), "Ajeno");
        await ExecuteAsync(connectionString, "UPDATE messaging.resolution_reasons SET deleted_at = now() WHERE id = @id", ("id", spam.Id));
        using var scope = factory.Services.CreateScope();

        var names = await scope.ServiceProvider.GetRequiredService<IResolutionReasonNames>().FindAsync(tenantId, [spam.Id, foreign.Id], Ct);

        Assert.Equal("Spam", Assert.Single(names, pair => pair.Key == spam.Id).Value);
        Assert.DoesNotContain(foreign.Id, names.Keys);
    }

    [Fact]
    public async Task TheListIsOrderedByNameIgnoringCase()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        await SaveAsync(factory, tenantId, "spam");
        await SaveAsync(factory, tenantId, "Atención");
        await SaveAsync(factory, tenantId, "Venta cerrada");
        using var scope = factory.Services.CreateScope();

        var list = await scope.ServiceProvider.GetRequiredService<IResolutionReasonRepository>().ListActiveAsync(tenantId, Ct);

        Assert.Equal(["Atención", "spam", "Venta cerrada"], list.Select(reason => reason.Name));
    }
}
```

- [ ] **Step 2: Córrelas y verifica que fallan**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ResolutionReasonPersistenceTests"`
Expected: FAIL de compilación, `CS0246: The type or namespace name 'IResolutionReasonRepository' could not be found`.

- [ ] **Step 3: Escribe los puertos**

`src/Modules/Messaging/Modules.Messaging.Application/ResolutionReasonPorts.cs`:

```csharp
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-15 §3.1: el catálogo de motivos de resolución del tenant. Todo método recibe <c>tenantId</c>: el id de otro tenant
/// responde igual que uno inexistente. «Vigente» es <c>deleted_at IS NULL</c> (D-R5). El agregado se escribe con tracking y se guarda
/// con <see cref="IMessagingUnitOfWork"/>, que traduce el índice único del nombre a <c>messaging.resolution_reason.name_taken</c>.
/// </summary>
public interface IResolutionReasonRepository
{
    /// <summary>Con tracking. <c>null</c> si está archivado, es de otro tenant o no existe.</summary>
    Task<ResolutionReason?> FindActiveAsync(Guid tenantId, Guid reasonId, CancellationToken cancellationToken);

    /// <summary>Sin tracking. Compara con <c>lower()</c> de Postgres en los dos lados, la misma expresión del índice único, así que
    /// .NET y la base no pueden discrepar sobre qué es «el mismo nombre». <paramref name="trimmedName"/> ya viene recortado.</summary>
    Task<ResolutionReason?> FindActiveByNameAsync(Guid tenantId, string trimmedName, CancellationToken cancellationToken);

    Task<int> CountActiveAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>D-R10: todos los vigentes, sin paginar, por nombre sin distinguir mayúsculas. Sin tracking.</summary>
    Task<IReadOnlyList<ResolutionReason>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken);

    void Add(ResolutionReason reason);
}

/// <summary>Spec 2026-10-15 §3.5: los nombres para el historial y el resumen, <b>incluidos los archivados</b> (D-R5). Un id que no aparece
/// se lee como «sin motivo», no como error.</summary>
public interface IResolutionReasonNames
{
    Task<IReadOnlyDictionary<Guid, string>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> reasonIds, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Configura el modelo**

En `MessagingDbContext.cs`, junto a `QuickReplyShortcutIndex`:

```csharp
    /// <summary>Spec 2026-10-15 §3.1: único por <c>(tenant_id, lower(name)) WHERE deleted_at IS NULL</c>. Va en la migración con SQL —EF
    /// no modela índices por expresión— y <c>MessagingUnitOfWork</c> lo traduce por este nombre a 409 <c>name_taken</c>.</summary>
    public const string ResolutionReasonNameIndex = "IX_resolution_reasons_tenant_name";
```

Junto a `QuickReplies`:

```csharp
    /// <summary>Spec 2026-10-15 §3.1: el catálogo de motivos de resolución.</summary>
    public DbSet<ResolutionReason> ResolutionReasons => Set<ResolutionReason>();
```

En `OnModelCreating`, después de `ConfigureQuickReply(modelBuilder);`:

```csharp
        ConfigureResolutionReason(modelBuilder);
```

En `ConfigureConversation`, después de la línea de `Version`:

```csharp
        // Spec 2026-10-15 §3.1: el motivo vigente. RESTRICT porque una fila de motivo nunca se borra (D-R5: archivar es deleted_at).
        conversation.Property(value => value.ResolutionReasonId).HasColumnName("resolution_reason_id");
        conversation.HasOne<ResolutionReason>().WithMany().HasForeignKey(value => value.ResolutionReasonId)
            .HasConstraintName("FK_conversations_resolution_reasons").OnDelete(DeleteBehavior.Restrict);
```

Y el método nuevo, después de `ConfigureQuickReply`:

```csharp
    /// <summary>Spec 2026-10-15 §3.1. El único por nombre (<see cref="ResolutionReasonNameIndex"/>) va por SQL en la migración.</summary>
    private static void ConfigureResolutionReason(ModelBuilder modelBuilder)
    {
        var reason = modelBuilder.Entity<ResolutionReason>();
        reason.ToTable("resolution_reasons", Schema);
        reason.HasKey(value => value.Id);
        reason.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        reason.Property(value => value.TenantId).HasColumnName("tenant_id");
        reason.Property(value => value.Name).HasColumnName("name").HasMaxLength(ResolutionReason.NameMaxLength);
        reason.Property(value => value.CreatedAt).HasColumnName("created_at");
        reason.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        reason.Property(value => value.DeletedAt).HasColumnName("deleted_at");
        reason.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        reason.Ignore(value => value.IsArchived);
    }
```

- [ ] **Step 5: Genera la migración y agrégale el índice**

```bash
dotnet ef migrations add AddResolutionReasons --project src/Modules/Messaging/Modules.Messaging.Infrastructure --context MessagingDbContext -o Persistence/Migrations
```

Abre el `<timestamp>_AddResolutionReasons.cs` generado. Comprueba que crea `messaging.resolution_reasons`, agrega `resolution_reason_id` a `conversations`, crea `IX_conversations_resolution_reason_id` y la FK `FK_conversations_resolution_reasons` con `ReferentialAction.Restrict`. Al final de `Up` agrega:

```csharp
            // Spec 2026-10-15 §3.1: EF no modela índices por expresión (mismo caso que IX_tags_tenant_name en Customers).
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_resolution_reasons_tenant_name\" ON messaging.resolution_reasons (tenant_id, lower(name)) WHERE deleted_at IS NULL;");
```

Al principio de `Down`:

```csharp
            migrationBuilder.Sql("DROP INDEX IF EXISTS messaging.\"IX_resolution_reasons_tenant_name\";");
```

- [ ] **Step 6: Escribe el repositorio y regístralo**

`src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ResolutionReasonRepository.cs`:

```csharp
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Spec 2026-10-15 §3.1: el agregado con tracking (la versión es el token de concurrencia, como la respuesta rápida).</summary>
internal sealed class ResolutionReasonRepository(MessagingDbContext dbContext) : IResolutionReasonRepository, IResolutionReasonNames
{
    private static readonly StringComparer NameOrder = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);

    public Task<ResolutionReason?> FindActiveAsync(Guid tenantId, Guid reasonId, CancellationToken cancellationToken) =>
        dbContext.ResolutionReasons.SingleOrDefaultAsync(
            reason => reason.TenantId == tenantId && reason.Id == reasonId && reason.DeletedAt == null, cancellationToken);

    // lower() de los dos lados y en SQL: la misma expresión de IX_resolution_reasons_tenant_name. Bajarlo en .NET con ToLowerInvariant
    // podría discrepar de la collation de la base con letras como «Ó», y el resolver crearía un duplicado que el índice sí aceptaría.
    public Task<ResolutionReason?> FindActiveByNameAsync(Guid tenantId, string trimmedName, CancellationToken cancellationToken) =>
        dbContext.ResolutionReasons
            .FromSql($"SELECT * FROM messaging.resolution_reasons WHERE tenant_id = {tenantId} AND deleted_at IS NULL AND lower(name) = lower({trimmedName})")
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);

    public Task<int> CountActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.ResolutionReasons.CountAsync(reason => reason.TenantId == tenantId && reason.DeletedAt == null, cancellationToken);

    public async Task<IReadOnlyList<ResolutionReason>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.ResolutionReasons.AsNoTracking()
            .Where(reason => reason.TenantId == tenantId && reason.DeletedAt == null)
            .ToListAsync(cancellationToken);
        // D-R10: el orden lo da .NET y no la collation de la base, que depende de cómo se creó el clúster. Son a lo sumo 100.
        return rows.OrderBy(reason => reason.Name, NameOrder).ThenBy(reason => reason.Id).ToArray();
    }

    public void Add(ResolutionReason reason) => dbContext.ResolutionReasons.Add(reason);

    public async Task<IReadOnlyDictionary<Guid, string>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> reasonIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reasonIds);
        if (reasonIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        // Sin filtro de deleted_at (D-R5): el historial nombra también a los archivados.
        return await dbContext.ResolutionReasons.AsNoTracking()
            .Where(reason => reason.TenantId == tenantId && reasonIds.Contains(reason.Id))
            .ToDictionaryAsync(reason => reason.Id, reason => reason.Name, cancellationToken);
    }
}
```

En `MessagingInfrastructureExtensions.cs`, junto a `services.AddScoped<IQuickReplyRepository, QuickReplyRepository>();`:

```csharp
        services.AddScoped<IResolutionReasonRepository, ResolutionReasonRepository>();
        services.AddScoped<IResolutionReasonNames, ResolutionReasonRepository>();
```

- [ ] **Step 7: Traduce el índice en la unidad de trabajo**

En `MessagingUnitOfWork.SaveChangesAsync`, después del `catch` de `QuickReplyShortcutIndex`:

```csharp
        // Spec 2026-10-15 §3.3 y D-R11: 409 y no 422, como customers.tag.name_taken. Es un choque con lo que ya existe —el resolver
        // reintenta y lo reutiliza; el renombrar elige otro nombre—, por eso ResourceConflictException y no un error de dominio.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, MessagingDbContext.ResolutionReasonNameIndex, StringComparison.Ordinal))
        {
            throw new ResourceConflictException(
                MessagingErrorCodes.ResolutionReasonNameTaken, "Another active resolution reason in this tenant already uses that name.", exception);
        }
```

Actualiza el `<summary>` de la clase y agrega: «Spec 2026-10-15: el nombre repetido de un motivo de resolución es `messaging.resolution_reason.name_taken` (409)».

- [ ] **Step 8: Córrelas y verifica que pasan**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ResolutionReasonPersistenceTests|FullyQualifiedName~QuickReplyPersistenceTests"`
Expected: PASS. `QuickReplyPersistenceTests` corre para confirmar que la unidad de trabajo no cambió para lo demás.

Si `indexdef` no trae exactamente `lower((name)::text)` (depende de la versión de Postgres), corre la consulta a mano, copia el texto real a la aserción y anótalo en el reporte. No relajes la aserción a «contiene lower».

- [ ] **Step 9: Commit**

```bash
git add src/Modules/Messaging/Modules.Messaging.Application/ResolutionReasonPorts.cs src/Modules/Messaging/Modules.Messaging.Infrastructure tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ResolutionReasonPersistenceTests.cs
git commit -m "feat(messaging): tabla de motivos de resolución con índice único parcial por nombre"
```

---

### Task 4: Resolver con motivo (buscar o crear en la misma transacción)

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/ResolutionReasons.cs` (en esta tarea, sólo mensajes, soporte y elección; la Task 6 agrega el catálogo)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/ConversationLifecycle.cs` (comando, validador nuevo, `ResolveConversationHandler`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingAuditRecorder.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingAuditRecorder.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs` (`ResolveAsync`, el mapeo de `/resolve` y el record de request al final)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ResolutionReasonApiTests.cs`

**Interfaces:**
- Consumes: `IResolutionReasonRepository` (Task 3), `Conversation.Resolve(now, reasonId)` y `ConversationEvent.ReasonId` (Task 2).
- Produces: `ResolveConversationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion, Guid? ReasonId = null, string? ReasonName = null)`; `ResolveConversationValidator`; `ResolveConversationRequest(Guid? ReasonId, string? ReasonName)`; `IMessagingAuditRecorder.RecordResolutionReason(Guid tenantId, Guid actorId, string action, Guid reasonId, DateTimeOffset occurredAt)`; `MessagingAuditActions.ResolutionReasonResourceType = "resolution_reason"`, `ResolutionReasonCreated`, `ResolutionReasonRenamed` y `ResolutionReasonArchived`; `ResolutionReasonMessages`; `ResolutionReasonSupport.NotFound(Guid)`.

- [ ] **Step 1: Escribe las pruebas que fallan**

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ResolutionReasonApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-15: resolver con motivo opcional (§3.3), el motivo en el resumen y el historial (§3.5), la ingesta que reabre
/// lo limpia (§3.6) y el catálogo con su permiso (§3.4, §3.7).</summary>
public sealed class ResolutionReasonApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConversationId, long Version, HttpClient Client)
    {
        public string Url => ConversationUrl(Tenant.TenantId, ConversationId);
    }

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using var anonymous = factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "wamid.in", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
        await DrainDeliveriesAsync(factory);
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var item = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items")[0];
        return new Fixture(factory, connectionString, tenant, item.GetProperty("id").GetGuid(), item.GetProperty("version").GetInt64(), client);
    }

    private static Task<HttpResponseMessage> ResolveAsync(Fixture f, long version, object? body = null) =>
        SendAsync(f.Client, HttpMethod.Post, $"{f.Url}/resolve", body, ifMatch: $"\"{version}\"");

    /// <summary>Resuelve y devuelve la versión nueva; falla la prueba si no es 200.</summary>
    private static async Task<long> ResolveOkAsync(Fixture f, long version, object? body = null)
    {
        var response = await ResolveAsync(f, version, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("version").GetInt64();
    }

    private static async Task<long> ReopenOkAsync(Fixture f, long version)
    {
        var response = await SendAsync(f.Client, HttpMethod.Post, $"{f.Url}/reopen", ifMatch: $"\"{version}\"");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("version").GetInt64();
    }

    private static Task<long> ReasonCountAsync(Fixture f) =>
        CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.resolution_reasons WHERE tenant_id = @t", ("t", f.Tenant.TenantId));

    /// <summary>Sólo cuando la columna no es NULL: <c>ScalarAsync</c> de un NULL devuelve <c>DBNull</c> y el cast revienta.</summary>
    private static Task<string> CurrentReasonIdAsync(Fixture f) =>
        ScalarAsync<string>(f.ConnectionString, "SELECT resolution_reason_id::text FROM messaging.conversations WHERE id = @id", ("id", f.ConversationId));

    private static Task<long> ConversationsWithReasonAsync(Fixture f) =>
        CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations WHERE id = @id AND resolution_reason_id IS NOT NULL", ("id", f.ConversationId));

    // §3.3: sin cuerpo, {} y un nombre en blanco son «sin motivo», exactamente como antes de esta spec.
    [Fact]
    public async Task NoBodyAnEmptyObjectAndABlankNameResolveWithoutAReason()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var version = await ResolveOkAsync(f, f.Version);
        version = await ResolveOkAsync(f, await ReopenOkAsync(f, version), new { });
        await ResolveOkAsync(f, await ReopenOkAsync(f, version), new { reasonName = "   " });

        Assert.Equal(0L, await ReasonCountAsync(f));
        Assert.Equal(0L, await ConversationsWithReasonAsync(f));
        Assert.Equal(0L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.messages WHERE conversation_id = @id AND details ? 'reasonId'", ("id", f.ConversationId)));
    }

    [Fact]
    public async Task ANewNameCreatesTheReasonWithTheResolveAndItsAudit()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await ResolveOkAsync(f, f.Version, new { reasonName = "  Venta cerrada " });

        var reasonId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.resolution_reasons WHERE name = 'Venta cerrada'");
        Assert.Equal(1L, await ReasonCountAsync(f));
        Assert.Equal(reasonId.ToString(), await CurrentReasonIdAsync(f));
        Assert.Equal(reasonId.ToString(), await ScalarAsync<string>(f.ConnectionString,
            "SELECT details->>'reasonId' FROM messaging.messages WHERE conversation_id = @id AND details->>'type' = 'Resolved'", ("id", f.ConversationId)));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE source = 'messaging' AND resource_type = 'resolution_reason' AND action = 'messaging.resolution_reason.created' AND resource_id = @id",
            ("id", reasonId.ToString())));
    }

    // D-R4: el mismo nombre con otro caso o espacios reutiliza el motivo; ni fila nueva ni otra auditoría de created.
    [Fact]
    public async Task AnExistingNameIsReusedIgnoringCaseAndSurroundingSpaces()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var version = await ResolveOkAsync(f, f.Version, new { reasonName = "Venta cerrada" });
        await ResolveOkAsync(f, await ReopenOkAsync(f, version), new { reasonName = "  VENTA CERRADA  " });

        Assert.Equal(1L, await ReasonCountAsync(f));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(DISTINCT details->>'reasonId') FROM messaging.messages WHERE conversation_id = @id AND details->>'type' = 'Resolved'", ("id", f.ConversationId)));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE action = 'messaging.resolution_reason.created'"));
    }

    [Fact]
    public async Task AReasonIdMustBeActiveInThisTenant()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var version = await ResolveOkAsync(f, f.Version, new { reasonName = "Spam" });
        version = await ReopenOkAsync(f, version);
        var spam = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.resolution_reasons WHERE name = 'Spam'");
        var foreign = Guid.CreateVersion7();
        await ExecuteAsync(f.ConnectionString,
            "INSERT INTO messaging.resolution_reasons (id, tenant_id, name, created_at, updated_at, version) VALUES (@id, @t, 'Ajeno', now(), now(), 1)",
            ("id", foreign), ("t", Guid.CreateVersion7()));

        var fromOtherTenant = await ResolveAsync(f, version, new { reasonId = foreign });
        var unknown = await ResolveAsync(f, version, new { reasonId = Guid.CreateVersion7() });
        var resolved = await ResolveAsync(f, version, new { reasonId = spam });
        version = await ReopenOkAsync(f, (await resolved.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("version").GetInt64());
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.resolution_reasons SET deleted_at = now() WHERE id = @id", ("id", spam));
        var archived = await ResolveAsync(f, version, new { reasonId = spam });

        Assert.Equal(HttpStatusCode.NotFound, fromOtherTenant.StatusCode);
        Assert.Equal("messaging.resolution_reason.not_found", (await ProblemAsync(fromOtherTenant)).Code);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, archived.StatusCode);
        Assert.Equal("messaging.resolution_reason.not_found", (await ProblemAsync(archived)).Code);
    }

    // Review Focus 4: el borde de 60/61, y los dos campos a la vez. El mapa errors marca reasonName.
    [Fact]
    public async Task BothFieldsOrALongNameAreValidationErrorsOnReasonName()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var both = await ResolveAsync(f, f.Version, new { reasonId = Guid.CreateVersion7(), reasonName = "Spam" });
        var tooLong = await ResolveAsync(f, f.Version, new { reasonName = new string('a', 61) });
        var sixty = await ResolveAsync(f, f.Version, new { reasonName = $"  {new string('a', 60)}  " });

        var (bothCode, bothKeys) = await ProblemAsync(both);
        var (longCode, longKeys) = await ProblemAsync(tooLong);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, both.StatusCode);
        Assert.Equal("validation.failed", bothCode);
        Assert.Equal(["reasonName"], bothKeys);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
        Assert.Equal("validation.failed", longCode);
        Assert.Equal(["reasonName"], longKeys);
        Assert.Equal(HttpStatusCode.OK, sixty.StatusCode);
    }

    // D-R9: el motivo 101 nuevo se rechaza; reutilizar uno de los 100 nunca choca con el tope.
    [Fact]
    public async Task TheHundredAndFirstNewReasonIsRejectedButReusingStillWorks()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ExecuteAsync(f.ConnectionString,
            "INSERT INTO messaging.resolution_reasons (id, tenant_id, name, created_at, updated_at, version) SELECT gen_random_uuid(), @t, 'Motivo ' || g, now(), now(), 1 FROM generate_series(1, 100) g",
            ("t", f.Tenant.TenantId));

        var full = await ResolveAsync(f, f.Version, new { reasonName = "Uno más" });
        var reused = await ResolveAsync(f, f.Version, new { reasonName = "motivo 7" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, full.StatusCode);
        Assert.Equal("messaging.resolution_reason.limit_reached", (await ProblemAsync(full)).Code);
        Assert.Equal(HttpStatusCode.OK, reused.StatusCode);
        Assert.Equal(100L, await ReasonCountAsync(f));
    }

    // D-R4: un resolver que falla no deja un motivo creado (una sola transacción).
    [Fact]
    public async Task AFailedResolveLeavesNoReasonBehind()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var stale = await ResolveAsync(f, f.Version + 7, new { reasonName = "Huérfano" });
        var version = await ResolveOkAsync(f, f.Version);
        var already = await ResolveAsync(f, version, new { reasonName = "Huérfano" });

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, already.StatusCode);
        Assert.Equal("messaging.conversation.already_resolved", (await ProblemAsync(already)).Code);
        Assert.Equal(0L, await ReasonCountAsync(f));
    }
}
```


- [ ] **Step 2: Córrelas y verifica que fallan**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ResolutionReasonApiTests"`
Expected: las pruebas compilan (sólo usan HTTP y SQL) y fallan. `ANewNameCreatesTheReasonWithTheResolveAndItsAudit` falla con `Assert.Equal() Failure` sobre el conteo de motivos (`Expected: 1, Actual: 0`): el endpoint ignora el cuerpo. `BothFieldsOrALongNameAreValidationErrorsOnReasonName` falla porque recibe 200 en vez de 422. `NoBodyAnEmptyObjectAndABlankNameResolveWithoutAReason` y `AFailedResolveLeavesNoReasonBehind` pueden pasar ya: son la red de que el cuerpo opcional no rompe el contrato viejo, y quedan anotadas así en el reporte.

- [ ] **Step 3: Agrega la auditoría**

En `IMessagingAuditRecorder.cs`, en la interfaz:

```csharp
    /// <summary>Spec 2026-10-15 §3.8: crear (sólo cuando el resolver lo crea), renombrar y archivar un motivo de resolución, en la misma
    /// transacción que el cambio.</summary>
    void RecordResolutionReason(Guid tenantId, Guid actorId, string action, Guid reasonId, DateTimeOffset occurredAt);
```

En `MessagingAuditActions`, después de las de quick reply:

```csharp
    public const string ResolutionReasonResourceType = "resolution_reason";
    public const string ResolutionReasonCreated = "messaging.resolution_reason.created";
    public const string ResolutionReasonRenamed = "messaging.resolution_reason.renamed";
    public const string ResolutionReasonArchived = "messaging.resolution_reason.archived";
```

En `MessagingAuditRecorder.cs`, después de `RecordQuickReply`:

```csharp
    // Spec 2026-10-15 §3.8: siempre una persona (quien resuelve o quien gestiona en Settings). changed_fields vacío, como el resto.
    public void RecordResolutionReason(Guid tenantId, Guid actorId, string action, Guid reasonId, DateTimeOffset occurredAt) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            actorId,
            AuditActorType.Human,
            action,
            MessagingAuditActions.ResolutionReasonResourceType,
            reasonId.ToString(),
            Outcome,
            NoChangedFields,
            Source,
            occurredAt));
```

Si un doble de prueba de `Modules.Messaging.UnitTests` implementa `IMessagingAuditRecorder`, dale el método nuevo vacío. Para encontrarlo: `grep -rn "IMessagingAuditRecorder" tests/Modules/Messaging`.

- [ ] **Step 4: Escribe mensajes, soporte y la elección del motivo**

`src/Modules/Messaging/Modules.Messaging.Application/ResolutionReasons.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

// Spec 2026-10-15: el motivo de resolución. La elección (D-R4) la usa el resolver; el catálogo (§3.4) lo agrega la Task 6.

/// <summary>Los textos de <c>errors</c>, tuteando.</summary>
public static class ResolutionReasonMessages
{
    public const string Name = "Escribe un motivo de 1 a 60 caracteres.";

    public const string NameTooLong = "El motivo puede tener hasta 60 caracteres.";

    public const string Both = "Elige un motivo de la lista o escribe uno nuevo, no los dos.";
}

internal static class ResolutionReasonSupport
{
    public static ResourceNotFoundException NotFound(Guid reasonId) =>
        new(MessagingErrorCodes.ResolutionReasonNotFound, $"Resolution reason '{reasonId}' was not found.");
}

/// <summary>El motivo que llevará la resolución. <see cref="IsNew"/>: todavía no existe y el resolver lo crea con este id.</summary>
internal sealed record ChosenReason(Guid Id, string Name, bool IsNew);

internal static class ResolutionReasonChoice
{
    /// <summary>D-R4. <c>reasonId</c> tiene que estar vigente en el tenant (404 si no). <c>reasonName</c> en blanco es «sin motivo»; si
    /// coincide con uno vigente (lower de Postgres, recortado) lo reutiliza; si no, es uno nuevo. No cuenta el tope: eso lo hace el
    /// handler sólo si de verdad va a crear, porque reutilizar nunca choca con el tope (D-R9).</summary>
    public static async Task<ChosenReason?> ChooseAsync(
        IResolutionReasonRepository reasons, Guid tenantId, Guid? reasonId, string? reasonName, CancellationToken cancellationToken)
    {
        if (reasonId is { } id)
        {
            var existing = await reasons.FindActiveAsync(tenantId, id, cancellationToken) ?? throw ResolutionReasonSupport.NotFound(id);
            return new ChosenReason(existing.Id, existing.Name, IsNew: false);
        }

        var trimmed = ResolutionReason.TrimName(reasonName);
        if (trimmed.Length == 0)
        {
            return null;
        }

        var byName = await reasons.FindActiveByNameAsync(tenantId, trimmed, cancellationToken);
        return byName is null ? new ChosenReason(Guid.CreateVersion7(), trimmed, IsNew: true) : new ChosenReason(byName.Id, byName.Name, IsNew: false);
    }
}
```

- [ ] **Step 5: Cambia el comando, agrega el validador y reescribe el handler**

En `ConversationLifecycle.cs`, reemplaza el record:

```csharp
/// <summary>Spec 2026-10-15 §3.3: el motivo es opcional. <see cref="ReasonId"/> elige uno vigente; <see cref="ReasonName"/> busca o crea
/// por nombre (D-R4). Los dos a la vez es 422; los dos ausentes, o el nombre en blanco, es resolver sin motivo como antes.</summary>
public sealed record ResolveConversationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion, Guid? ReasonId = null, string? ReasonName = null)
    : ICommand<ConversationSummary>;

/// <summary>Spec 2026-10-15 §3.3: el validador da el campo (<c>errors.reasonName</c>) que el dominio no puede dar.</summary>
public sealed class ResolveConversationValidator : AbstractValidator<ResolveConversationCommand>
{
    public ResolveConversationValidator()
    {
        RuleFor(command => command.ReasonName)
            .Must(name => ResolutionReason.TrimName(name).Length <= ResolutionReason.NameMaxLength)
            .OverridePropertyName("reasonName").WithMessage(ResolutionReasonMessages.NameTooLong);
        RuleFor(command => command)
            .Must(command => command.ReasonId is null || string.IsNullOrWhiteSpace(command.ReasonName))
            .OverridePropertyName("reasonName").WithMessage(ResolutionReasonMessages.Both);
    }
}
```

Agrega `using FluentValidation;` arriba si no está.

Reemplaza `ResolveConversationHandler` entero:

```csharp
/// <summary>§8.5: por el agregado con If-Match (428 lo pone el endpoint; 412 lo pone el unit of work). Spec 2026-10-15 §3.3: el
/// motivo nuevo se agrega al mismo DbContext, así que entra en el mismo SaveChanges que la resolución, el evento y la auditoría: un
/// resolver que falla no deja motivos huérfanos. El orden importa: la versión y <c>Resolve</c> (already_resolved) van antes de contar el
/// tope, que sólo se cuenta si de verdad se crea.</summary>
public sealed class ResolveConversationHandler(
    IConversationRepository repository,
    IConversationQueries queries,
    IResolutionReasonRepository reasons,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    IConversationEvents events,
    ConversationSummaryBuilder summaries,
    CallerMembership caller,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IMessagingNotifier notifier,
    IValidator<ResolveConversationCommand> validator)
    : ICommandHandler<ResolveConversationCommand, ConversationSummary>
{
    public async Task<ConversationSummary> HandleAsync(ResolveConversationCommand command, CancellationToken cancellationToken)
    {
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.ConversationManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var conversation = await repository.FindAsync(command.TenantId, command.ConversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(command.ConversationId);
        ConversationConcurrency.EnsureVersion(conversation, command.ExpectedVersion);

        var now = clock.UtcNow;
        var reason = await ResolutionReasonChoice.ChooseAsync(reasons, command.TenantId, command.ReasonId, command.ReasonName, cancellationToken);
        conversation.Resolve(now, reason?.Id);
        if (reason is { IsNew: true })
        {
            ResolutionReason.EnsureTenantCapacity(await reasons.CountActiveAsync(command.TenantId, cancellationToken));
            reasons.Add(ResolutionReason.Create(reason.Id, command.TenantId, reason.Name, now));
            audit.RecordResolutionReason(command.TenantId, executionContext.SubjectId, MessagingAuditActions.ResolutionReasonCreated, reason.Id, now);
        }

        // Spec 2026-10-10 §6.1.5 (P9): el evento lleva quién; sin membresía activa, actor null (sin 403 nuevo).
        var actor = await caller.FindAsync(command.TenantId, cancellationToken);
        events.Record(
            command.TenantId, conversation.ConnectionId, conversation.Id,
            new ConversationEvent(ConversationEventType.Resolved, Actor: actor, ReasonId: reason?.Id), now);
        audit.Record(command.TenantId, executionContext.SubjectId, MessagingAuditActions.Resolved, conversation.Id, now);
        // D-R11: dos asesores creando el mismo nombre nuevo a la vez → el segundo recibe 409 name_taken de la unidad de trabajo, y la
        // transacción entera se revierte (el If-Match sigue vigente). El cliente reintenta una vez y el segundo intento lo reutiliza.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await notifier.ConversationChangedAsync(command.TenantId, conversation.Id, cancellationToken);
        return (await summaries.BuildAsync(command.TenantId, [(await queries.FindAsync(command.TenantId, conversation.Id, cancellationToken))!], cancellationToken))[0];
    }
}
```

`AddValidatorsFromAssemblyContaining<ListConversationsValidator>()` en `QepServiceCollectionExtensions.cs:580` ya registra el validador nuevo: no hay nada que agregar en Bootstrapper.

- [ ] **Step 6: Acepta el cuerpo opcional en el endpoint**

En `MessagingEndpoints.cs`, reemplaza `ResolveAsync`:

```csharp
    // Spec 2026-10-15 §3.3: el cuerpo es opcional (nullable): sin cuerpo, el frontend de antes sigue funcionando igual.
    private static async Task<IResult> ResolveAsync(
        Guid tenantId, Guid conversationId, ResolveConversationRequest? request, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new ResolveConversationCommand(tenantId, conversationId, RequireVersion(httpContext), request?.ReasonId, request?.ReasonName), cancellationToken));
```

En el mapeo de `/conversations/{conversationId:guid}/resolve`, agrega `.ProducesProblem(StatusCodes.Status409Conflict)` después del 404. Al final del archivo, junto a `TransferConversationRequest`:

```csharp
/// <summary>Spec 2026-10-15 §3.3: todo opcional. <c>reasonId</c> elige un motivo vigente; <c>reasonName</c> busca o crea por nombre (D-R4).
/// BFF: el selector manda el texto tal cual lo escribió la persona y el servidor decide si ya existía, así la pantalla no tiene que crear
/// primero ni conocer el catálogo exacto.</summary>
public sealed record ResolveConversationRequest(Guid? ReasonId, string? ReasonName);
```

- [ ] **Step 7: Córrelas y verifica que pasan, junto con las del ciclo de vida**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ResolutionReasonApiTests|FullyQualifiedName~ConversationLifecycleApiTests|FullyQualifiedName~RealtimeEventsTests"`
Expected: PASS. Si alguna de `ConversationLifecycleApiTests` o `RealtimeEventsTests` falla, el cuerpo opcional rompió el contrato viejo (§5, «barrido de cuerpos crudos»): corrige el endpoint, nunca la prueba.

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests`
Expected: PASS (confirma que ningún doble de `IMessagingAuditRecorder` quedó sin compilar).

- [ ] **Step 8: Commit**

```bash
git add src/Modules/Messaging tests/Modules/Messaging
git commit -m "feat(messaging): resolver con motivo opcional, buscado o creado en la misma transacción"
```

---

### Task 5: El motivo en el resumen y el historial, y la ingesta que reabre lo limpia

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IConversationQueries.cs` (`ConversationRow`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ConversationQueries.cs` (`Projection`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingDtos.cs` (`ResolutionReasonRefDto`, `ConversationSummary`, `MessageEventDto`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingSupport.cs` (`ConversationSummaryBuilder`, `MessageMapping`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/ListMessages.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/InboundIngestion.cs` (el `UPDATE` de la sentencia 3)
- Modify: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingTestBed.cs` (línea ~191, el `new ConversationSummaryBuilder(...)`)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ResolutionReasonApiTests.cs`

**Interfaces:**
- Consumes: `IResolutionReasonNames` (Task 3); el resolver con motivo (Task 4).
- Produces: `ResolutionReasonRefDto(Guid Id, string Name)`; `ConversationSummary.ResolutionReason` (último parámetro, con valor por defecto `null`); `MessageEventDto.Reason` (último parámetro, con valor por defecto `null`); `ConversationRow.ResolutionReasonId` (último parámetro, con valor por defecto `null`); `ConversationSummaryBuilder(IMessagingConnectionDirectory, IMessagingCustomerDirectory, IMessagingMemberNames, IResolutionReasonNames, CallerMembership)`; `MessageMapping.ToDto(..., IReadOnlyDictionary<Guid, string>? reasonNames = null)` y `MessageMapping.ReasonIdsOf(IEnumerable<MessageRow>)`.

- [ ] **Step 1: Escribe las pruebas que fallan**

Agrega a `ResolutionReasonApiTests.cs`:

```csharp
    private static async Task<JsonElement> SummaryAsync(Fixture f) =>
        await f.Client.GetFromJsonAsync<JsonElement>(f.Url, Ct);

    private static async Task<JsonElement[]> ResolvedEventsAsync(Fixture f) =>
        (await f.Client.GetFromJsonAsync<JsonElement>(MessagesUrl(f.Tenant.TenantId, f.ConversationId), Ct))
            .GetProperty("items").EnumerateArray()
            .Where(item => item.TryGetProperty("event", out var e) && e.ValueKind == JsonValueKind.Object && e.GetProperty("type").GetString() == "Resolved")
            .ToArray();

    // D-R7 y §3.5: el resumen trae el motivo mientras está resuelta; el evento del hilo lo trae siempre.
    [Fact]
    public async Task TheSummaryAndTheThreadEventCarryTheReason()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var response = await ResolveAsync(f, f.Version, new { reasonName = "Venta cerrada" });
        var resolvedSummary = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var detail = await SummaryAsync(f);
        var listed = (await f.Client.GetFromJsonAsync<JsonElement>($"{ConversationsUrl(f.Tenant.TenantId)}?status=Resolved", Ct)).GetProperty("items")[0];
        var resolvedEvent = Assert.Single(await ResolvedEventsAsync(f));

        foreach (var summary in new[] { resolvedSummary, detail, listed })
        {
            Assert.Equal("Venta cerrada", summary.GetProperty("resolutionReason").GetProperty("name").GetString());
        }

        Assert.Equal("Venta cerrada", resolvedEvent.GetProperty("event").GetProperty("reason").GetProperty("name").GetString());
        Assert.Equal(
            resolvedSummary.GetProperty("resolutionReason").GetProperty("id").GetGuid(),
            resolvedEvent.GetProperty("event").GetProperty("reason").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task WithoutAReasonBothAreNull()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var summary = await (await ResolveAsync(f, f.Version)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(JsonValueKind.Null, summary.GetProperty("resolutionReason").ValueKind);
        Assert.Equal(JsonValueKind.Null, Assert.Single(await ResolvedEventsAsync(f)).GetProperty("event").GetProperty("reason").ValueKind);
    }

    // §3.6 y D-R2: reabrir —por API o por un entrante de la ingesta— limpia el motivo vigente; el evento viejo conserva el suyo.
    [Fact]
    public async Task ReopeningByApiOrByAnInboundClearsTheCurrentReasonButNotTheOldEvent()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var version = await ResolveOkAsync(f, f.Version, new { reasonName = "Spam" });

        await ReopenOkAsync(f, version);
        var afterApi = await SummaryAsync(f);
        await ResolveOkAsync(f, afterApi.GetProperty("version").GetInt64(), new { reasonName = "Venta cerrada" });
        using var anonymous = f.Factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "wamid.2", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "otra cosa"));
        await DrainDeliveriesAsync(f.Factory);
        var afterInbound = await SummaryAsync(f);

        Assert.Equal(JsonValueKind.Null, afterApi.GetProperty("resolutionReason").ValueKind);
        Assert.Equal("Open", afterInbound.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, afterInbound.GetProperty("resolutionReason").ValueKind);
        Assert.Equal(0L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.conversations WHERE id = @id AND resolution_reason_id IS NOT NULL", ("id", f.ConversationId)));
        Assert.Equal(["Spam", "Venta cerrada"],
            (await ResolvedEventsAsync(f)).Select(item => item.GetProperty("event").GetProperty("reason").GetProperty("name").GetString()));
    }

    // D-R5: un motivo archivado sigue nombrado en el historial y en el resumen de la conversación que lo usa.
    [Fact]
    public async Task AnArchivedReasonIsStillNamedWhereItWasUsed()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ResolveOkAsync(f, f.Version, new { reasonName = "Spam" });
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.resolution_reasons SET deleted_at = now()");

        Assert.Equal("Spam", (await SummaryAsync(f)).GetProperty("resolutionReason").GetProperty("name").GetString());
        Assert.Equal("Spam", Assert.Single(await ResolvedEventsAsync(f)).GetProperty("event").GetProperty("reason").GetProperty("name").GetString());
    }
```


- [ ] **Step 2: Córrelas y verifica que fallan**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ResolutionReasonApiTests"`
Expected: FAIL con `KeyNotFoundException: The given key was not present in the dictionary` al leer `resolutionReason` y `reason`.

- [ ] **Step 3: DTOs y fila**

En `MessagingDtos.cs`, junto a `AssignedToDto`:

```csharp
/// <summary>Spec 2026-10-15 §3.5: un motivo de resolución con su nombre ya resuelto, incluido el de uno archivado (D-R5).</summary>
public sealed record ResolutionReasonRefDto(Guid Id, string Name);
```

Agrega a `ConversationSummary`, después de `Channel`:

```csharp
    string Channel = nameof(ConversationChannel.WhatsApp),
    /// <summary>Spec 2026-10-15 D-R7 (BFF): el motivo de la resolución vigente, sólo con <c>status = Resolved</c>. Viaja en el resumen
    /// aunque esté en el historial porque el encabezado del hilo dibuja «Resuelta · Venta cerrada» y la fila de resueltas puede mostrarlo
    /// sin pedir los mensajes. <c>null</c> si está abierta o se resolvió sin motivo.</summary>
    ResolutionReasonRefDto? ResolutionReason = null);
```

Reemplaza `MessageEventDto`:

```csharp
/// <summary>§5.1 y §6.1.5: <c>actor</c> es null cuando lo hizo el sistema. Spec 2026-10-15 §3.5: <c>reason</c> sólo en un <c>Resolved</c>
/// con motivo; con nombre aunque esté archivado.</summary>
public sealed record MessageEventDto(string Type, MemberRefDto? Actor, MemberRefDto? Target, MemberRefDto? Previous, ResolutionReasonRefDto? Reason = null);
```

En `IConversationQueries.cs`, `ConversationRow` termina así:

```csharp
    ConversationChannel Channel = ConversationChannel.WhatsApp,
    Guid? ResolutionReasonId = null);
```

En `ConversationQueries.cs`, la `Projection` termina `..., conversation.Version, conversation.Channel, conversation.ResolutionReasonId);`.

- [ ] **Step 4: El resumen**

En `MessagingSupport.cs`, `ConversationSummaryBuilder`:

```csharp
public sealed class ConversationSummaryBuilder(
    IMessagingConnectionDirectory connections,
    IMessagingCustomerDirectory customers,
    IMessagingMemberNames memberNames,
    IResolutionReasonNames reasonNames,
    CallerMembership caller)
```

En `BuildAsync`, antes del `return`:

```csharp
        // Spec 2026-10-15 D-R7: sólo las resueltas muestran su motivo; una consulta para toda la página, como los asignados.
        var reasonIds = rows.Where(row => row.Status == ConversationStatus.Resolved && row.ResolutionReasonId is not null)
            .Select(row => row.ResolutionReasonId!.Value).Distinct().ToArray();
        var reasons = reasonIds.Length == 0 ? null : await reasonNames.FindAsync(tenantId, reasonIds, cancellationToken);
        return rows.Select(row => ToSummary(row, names, byId, byPhone, assigneeNames, me, reasons)).ToArray();
```

`ToSummary` gana el parámetro opcional `IReadOnlyDictionary<Guid, string>? reasonNames = null` al final, y el `new(...)` termina:

```csharp
            row.Channel.ToString(),
            row.Status == ConversationStatus.Resolved && row.ResolutionReasonId is { } reasonId && reasonNames?.GetValueOrDefault(reasonId) is { } reasonName
                ? new ResolutionReasonRefDto(reasonId, reasonName)
                : null);
```

- [ ] **Step 5: El evento del hilo**

En `MessageMapping.ToDto`, agrega el parámetro opcional al final, `IReadOnlyDictionary<Guid, string>? reasonNames = null`, y el `new MessageEventDto(...)` pasa a:

```csharp
                : new MessageEventDto(
                    conversationEvent.Type.ToString(),
                    Member(conversationEvent.Actor, memberNames),
                    Member(conversationEvent.Target, memberNames),
                    Member(conversationEvent.Previous, memberNames),
                    conversationEvent.ReasonId is { } reasonId && reasonNames?.GetValueOrDefault(reasonId) is { } reasonName
                        ? new ResolutionReasonRefDto(reasonId, reasonName)
                        : null),
```

Junto a `MemberIdsOf`:

```csharp
    /// <summary>Spec 2026-10-15 §3.5: los motivos de los eventos <c>Resolved</c> de la página, para una sola consulta de nombres.</summary>
    public static IReadOnlyCollection<Guid> ReasonIdsOf(IEnumerable<MessageRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Where(row => row.Kind == MessageKind.Event)
            .Select(row => ConversationEventJson.Parse(row.DetailsJson)?.ReasonId)
            .OfType<Guid>()
            .Distinct()
            .ToArray();
    }
```

En `ListMessages.cs`, agrega `IResolutionReasonNames reasonNames` al constructor de `ListMessagesHandler`, después de `IMessagingMemberNames memberNames`. Antes del `return`:

```csharp
        var reasons = await reasonNames.FindAsync(query.TenantId, MessageMapping.ReasonIdsOf(page), cancellationToken);
        return new MessagePageDto(page.Select(row => MessageMapping.ToDto(row, query.TenantId, names, targets, reasons)).ToArray(), hasMore);
```

`SearchMessages`, `SendMessage` y los demás llamadores de `ToDto` no cambian: el parámetro es opcional y no muestran eventos con motivo.

- [ ] **Step 6: La ingesta limpia el motivo al reabrir**

En `InboundIngestion.cs`, en el `UPDATE messaging.conversations SET` de la sentencia 3, después de `status = 'Open',`:

```csharp
                resolution_reason_id = NULL,
```

Agrega al comentario de la sentencia 3: «Spec 2026-10-15 §3.6: reabrir limpia el motivo vigente; el del evento `Resolved` viejo queda en su `details`».

- [ ] **Step 7: El builder en el banco de pruebas unitarias**

En `MessagingTestBed.cs`, línea ~191:

```csharp
            new ConversationSummaryBuilder(Connections, Customers, memberNames, new FakeResolutionReasonNames(), new CallerMembership(membership, context)),
```

Y al final del archivo, junto a los otros dobles:

```csharp
/// <summary>Spec 2026-10-15 §3.5: sin motivos; las pruebas unitarias no resuelven nombres de motivo.</summary>
internal sealed class FakeResolutionReasonNames : IResolutionReasonNames
{
    public Task<IReadOnlyDictionary<Guid, string>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> reasonIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
}
```

Si `MessagingTestBed` u otro archivo construye `ListMessagesHandler` (`grep -rn "new ListMessagesHandler" tests`), pásale `new FakeResolutionReasonNames()` también.

- [ ] **Step 8: Córrelas y verifica que pasan**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ResolutionReasonApiTests|FullyQualifiedName~ThreadEventsApiTests|FullyQualifiedName~InboundIngestionTests|FullyQualifiedName~InboundEventsTests"`
Expected: PASS.

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/Modules/Messaging tests/Modules/Messaging
git commit -m "feat(messaging): el motivo de resolución en el resumen y el historial; la ingesta lo limpia al reabrir"
```

---

### Task 6: Catálogo — listar, renombrar y archivar, con su permiso

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingPermissions.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/ResolutionReasons.cs` (DTO, queries, comandos, validador, handlers)
- Create: `src/Modules/Messaging/Modules.Messaging.Api/ResolutionReasonEndpoints.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs` (`RequireVersion` pasa a `internal`; `group.MapResolutionReasonEndpoints();` junto a `group.MapQuickReplyEndpoints();`, línea ~207)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (handlers ~línea 613, rol `admin` ~968, rol `advisor` ~1024, `PermissionDefinition` ~1455, política ~1790)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ResolutionReasonApiTests.cs`, `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs`

**Interfaces:**
- Consumes: `IResolutionReasonRepository`, `ResolutionReason.Rename/Archive`, `RecordResolutionReason`, `ResolutionReasonSupport.NotFound`, `ResolutionReasonMessages`.
- Produces: `MessagingPermissions.ResolutionReasonManage = "messaging.resolution_reason.manage"`; `ResolutionReasonDto(Guid Id, string Name, long Version)`; `ListResolutionReasonsQuery(Guid TenantId) : IQuery<IReadOnlyList<ResolutionReasonDto>>`; `RenameResolutionReasonCommand(Guid TenantId, Guid ReasonId, string? Name, long ExpectedVersion) : ICommand<ResolutionReasonDto>`; `ArchiveResolutionReasonCommand(Guid TenantId, Guid ReasonId) : ICommand<bool>`; `MessagingEndpoints.RequireVersion(HttpContext)` como `internal`.

- [ ] **Step 1: Escribe las pruebas que fallan**

En `CompositionRootTests.cs`, después de `OnlyTheAdminRoleCarriesTheQuickReplyPermissionAsCore`:

```csharp
    /// <summary>Spec 2026-10-15 D-R8: de núcleo y de fábrica en <c>admin</c> y <c>advisor</c> (decisión del owner: los asesores también
    /// gestionan el catálogo). Permiso aparte de <c>conversation.manage</c> para que un rol custom pueda no llevarlo.</summary>
    [Fact]
    public void AdminAndAdvisorCarryTheResolutionReasonPermissionAsCore()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();

        Assert.Equal("messaging.resolution_reason.manage", MessagingPermissions.ResolutionReasonManage);
        Assert.Contains(MessagingPermissions.ResolutionReasonManage, catalog.PermissionsFor("admin"));
        Assert.Contains(MessagingPermissions.ResolutionReasonManage, catalog.PermissionsFor("advisor"));
        Assert.DoesNotContain(MessagingPermissions.ResolutionReasonManage, catalog.PermissionsFor("billing"));
        var definition = Assert.Single(catalog.ListPermissions(), candidate => candidate.Permission == MessagingPermissions.ResolutionReasonManage);
        Assert.Equal("Messaging", definition.Category);
        Assert.Empty(definition.RequiredModules!);
    }
```

Agrega a `ResolutionReasonApiTests.cs`:

```csharp
    private static readonly string[] CatalogPermissions =
        [MessagingPermissions.ConversationRead, MessagingPermissions.ConversationManage, MessagingPermissions.ResolutionReasonManage];

    private static string ReasonsUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/messaging/resolution-reasons";

    private static async Task<Guid> SeedReasonAsync(Fixture f, string name, bool archived = false)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(f.ConnectionString,
            "INSERT INTO messaging.resolution_reasons (id, tenant_id, name, created_at, updated_at, deleted_at, version) VALUES (@id, @t, @n, now(), now(), CASE WHEN @a THEN now() END, 1)",
            ("id", id), ("t", f.Tenant.TenantId), ("n", name), ("a", archived));
        return id;
    }

    [Fact]
    public async Task TheListHasOnlyActiveReasonsByName()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedReasonAsync(f, "Venta cerrada");
        await SeedReasonAsync(f, "atención");
        await SeedReasonAsync(f, "Archivado", archived: true);
        using var reader = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);

        var list = await reader.GetFromJsonAsync<JsonElement>(ReasonsUrl(f.Tenant.TenantId), Ct);

        Assert.Equal(["atención", "Venta cerrada"], list.EnumerateArray().Select(item => item.GetProperty("name").GetString()));
        Assert.Equal(1, list[0].GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task RenameNeedsIfMatchChecksTheVersionAndRejectsAnotherActiveName()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var manager = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, CatalogPermissions);
        var typo = await SeedReasonAsync(f, "Venta cerada");
        await SeedReasonAsync(f, "Spam");
        var url = $"{ReasonsUrl(f.Tenant.TenantId)}/{typo}";

        var without = await SendAsync(manager, HttpMethod.Patch, url, new { name = "Venta cerrada" });
        var stale = await SendAsync(manager, HttpMethod.Patch, url, new { name = "Venta cerrada" }, ifMatch: "\"9\"");
        var taken = await SendAsync(manager, HttpMethod.Patch, url, new { name = "SPAM" }, ifMatch: "\"1\"");
        var blank = await SendAsync(manager, HttpMethod.Patch, url, new { name = "  " }, ifMatch: "\"1\"");
        var renamed = await SendAsync(manager, HttpMethod.Patch, url, new { name = " Venta cerrada " }, ifMatch: "\"1\"");
        var body = await renamed.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(HttpStatusCode.PreconditionRequired, without.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("messaging.resolution_reason.name_taken", (await ProblemAsync(taken)).Code);
        var (blankCode, blankKeys) = await ProblemAsync(blank);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);
        Assert.Equal("validation.failed", blankCode);
        Assert.Equal(["name"], blankKeys);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("Venta cerrada", body.GetProperty("name").GetString());
        Assert.Equal(2, body.GetProperty("version").GetInt64());
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE action = 'messaging.resolution_reason.renamed' AND resource_id = @id", ("id", typo.ToString())));
    }

    // Review Focus 2 y 3: renombrar a su propio nombre con otro caso, o al de un archivado, no choca con el índice parcial.
    [Fact]
    public async Task RenamingToItsOwnNameInAnotherCaseOrToAnArchivedNameIsAllowed()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var manager = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, CatalogPermissions);
        var reason = await SeedReasonAsync(f, "venta cerrada");
        await SeedReasonAsync(f, "Spam", archived: true);
        var url = $"{ReasonsUrl(f.Tenant.TenantId)}/{reason}";

        var ownName = await SendAsync(manager, HttpMethod.Patch, url, new { name = "Venta cerrada" }, ifMatch: "\"1\"");
        var archivedName = await SendAsync(manager, HttpMethod.Patch, url, new { name = "Spam" }, ifMatch: "\"2\"");

        Assert.Equal(HttpStatusCode.OK, ownName.StatusCode);
        Assert.Equal(HttpStatusCode.OK, archivedName.StatusCode);
    }

    // D-R6: renombrar cambia el nombre también en el historial pasado.
    [Fact]
    public async Task ARenameShowsInPastResolutions()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var manager = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, CatalogPermissions);
        await ResolveOkAsync(f, f.Version, new { reasonName = "Venta cerada" });
        var id = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.resolution_reasons");

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(manager, HttpMethod.Patch, $"{ReasonsUrl(f.Tenant.TenantId)}/{id}", new { name = "Venta cerrada" }, ifMatch: "\"1\"")).StatusCode);

        Assert.Equal("Venta cerrada", Assert.Single(await ResolvedEventsAsync(f)).GetProperty("event").GetProperty("reason").GetProperty("name").GetString());
    }

    [Fact]
    public async Task DeleteArchivesOnceThenIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var manager = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, CatalogPermissions);
        var spam = await SeedReasonAsync(f, "Spam");
        var url = $"{ReasonsUrl(f.Tenant.TenantId)}/{spam}";

        var first = await SendAsync(manager, HttpMethod.Delete, url);
        var second = await SendAsync(manager, HttpMethod.Delete, url);
        var renameArchived = await SendAsync(manager, HttpMethod.Patch, url, new { name = "Otro" }, ifMatch: "\"2\"");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal("messaging.resolution_reason.not_found", (await ProblemAsync(second)).Code);
        Assert.Equal(HttpStatusCode.NotFound, renameArchived.StatusCode);
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.resolution_reasons WHERE deleted_at IS NOT NULL"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE action = 'messaging.resolution_reason.archived' AND resource_id = @id", ("id", spam.ToString())));
    }

    // §3.7: sin el permiso, 403. Otro tenant, 403 (la política y el handler revalidan). Módulo apagado, 403.
    [Fact]
    public async Task ManagingNeedsThePermissionTheTenantAndTheModule()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var spam = await SeedReasonAsync(f, "Spam");
        var url = $"{ReasonsUrl(f.Tenant.TenantId)}/{spam}";
        var other = await RegisterTenantAsync(f.Factory);
        await EnableMessagingAsync(f.ConnectionString, other.TenantId);
        using var otherManager = CreateClient(f.Factory, other.OwnerUserId, other.TenantId, CatalogPermissions);

        var withoutPermission = await SendAsync(f.Client, HttpMethod.Delete, url);
        var otherTenant = await SendAsync(otherManager, HttpMethod.Delete, url);
        // Igual que UnreadRejectsTheReadPermissionAloneOtherTenantsAndADisabledModule.
        await ExecuteAsync(f.ConnectionString, "UPDATE tenancy.tenant_modules SET status = 'inactive' WHERE tenant_id = @t AND module_key = 'messaging'", ("t", f.Tenant.TenantId));
        using var manager = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, CatalogPermissions);
        var moduleOff = await SendAsync(manager, HttpMethod.Delete, url);

        Assert.Equal(HttpStatusCode.Forbidden, withoutPermission.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, otherTenant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, moduleOff.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(moduleOff)).Code);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.resolution_reasons WHERE deleted_at IS NOT NULL"));
    }
```


- [ ] **Step 2: Córrelas y verifica que fallan**

Run: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"`
Expected: FAIL de compilación, `CS0117: 'MessagingPermissions' does not contain a definition for 'ResolutionReasonManage'`.

- [ ] **Step 3: El permiso y sus tres mitades**

En `MessagingPermissions.cs`:

```csharp
    /// <summary>Spec 2026-10-15 D-R8: renombrar y archivar los motivos de resolución del tenant. Crearlos en línea al resolver no lo pide
    /// (basta <see cref="ConversationManage"/>), y leerlos tampoco (<see cref="ConversationRead"/>). De núcleo y de fábrica en <c>admin</c>
    /// y <c>advisor</c> (decisión del owner).</summary>
    public const string ResolutionReasonManage = "messaging.resolution_reason.manage";
```

En `QepServiceCollectionExtensions.cs`:

1. Rol `admin`, después de `MessagingPermissions.QuickReplyManage,`:

```csharp
                // Spec 2026-10-15 D-R8: el catálogo de motivos de resolución. El rol vive en código: sin migración.
                MessagingPermissions.ResolutionReasonManage,
```

2. Rol `advisor`, después de `MessagingPermissions.ConversationManage,` (en la lista del `advisor`, ~línea 1024):

```csharp
                // Spec 2026-10-15 D-R8: la asesora también corrige y archiva los motivos con que resuelve (decisión del owner).
                MessagingPermissions.ResolutionReasonManage,
```

3. Definición, después de la de `QuickReplyManage`:

```csharp
        // Spec 2026-10-15 §3.7: núcleo como las demás de Messaging; el módulo lo revisa cada handler.
        services.AddSingleton(new PermissionDefinition(
            MessagingPermissions.ResolutionReasonManage,
            "Gestionar motivos de resolución",
            "Permite renombrar y archivar los motivos con los que se resuelven las conversaciones de la bandeja.",
            "Messaging",
            "low",
            RequiredModules: []));
```

4. Política: es una cadena fluida de `.AddPolicy(...)`. Agrega el eslabón después del de `QuickReplyManage` (~línea 1790):

```csharp
            // Spec 2026-10-15: sin esta política, RequireAuthorization(ResolutionReasonManage) no resuelve y el síntoma es 500, no 403 (CLAUDE.md).
            .AddPolicy(
                MessagingPermissions.ResolutionReasonManage,
                policy => AddPermissionRequirement(policy, MessagingPermissions.ResolutionReasonManage))
```

- [ ] **Step 4: Casos de uso del catálogo**

Agrega al final de `ResolutionReasons.cs` (con `using FluentValidation;` y `using Modules.Tenancy.Application;` arriba):

```csharp
/// <summary>Spec 2026-10-15 §3.4: un motivo del catálogo. La versión viaja para el <c>If-Match</c> del renombrar.</summary>
public sealed record ResolutionReasonDto(Guid Id, string Name, long Version);

public sealed record ListResolutionReasonsQuery(Guid TenantId) : IQuery<IReadOnlyList<ResolutionReasonDto>>;

public sealed record RenameResolutionReasonCommand(Guid TenantId, Guid ReasonId, string? Name, long ExpectedVersion) : ICommand<ResolutionReasonDto>;

public sealed record ArchiveResolutionReasonCommand(Guid TenantId, Guid ReasonId) : ICommand<bool>;

/// <summary>Texto libre: el validador da <c>errors.name</c>, que el dominio (<c>name_required</c>, <c>name_too_long</c>) no puede dar.</summary>
public sealed class RenameResolutionReasonValidator : AbstractValidator<RenameResolutionReasonCommand>
{
    public RenameResolutionReasonValidator() =>
        RuleFor(command => command.Name)
            .Must(name => ResolutionReason.IsValidName(ResolutionReason.TrimName(name)))
            .OverridePropertyName("name").WithMessage(ResolutionReasonMessages.Name);
}

/// <summary>§3.4 <c>GET /resolution-reasons</c>: todo asesor de la caja. D-R10: completa, sin paginar, por nombre.</summary>
public sealed class ListResolutionReasonsHandler(
    IResolutionReasonRepository reasons, ITenantModules tenantModules, IExecutionContext executionContext)
    : IQueryHandler<ListResolutionReasonsQuery, IReadOnlyList<ResolutionReasonDto>>
{
    public async Task<IReadOnlyList<ResolutionReasonDto>> HandleAsync(ListResolutionReasonsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.ConversationRead, cancellationToken);
        return (await reasons.ListActiveAsync(query.TenantId, cancellationToken))
            .Select(reason => new ResolutionReasonDto(reason.Id, reason.Name, reason.Version)).ToArray();
    }
}

/// <summary>§3.4 <c>PATCH /resolution-reasons/{id}</c> con <c>If-Match</c>: archivado o ajeno es 404; versión vieja, 412; nombre de otro
/// vigente, 409 (la unidad de trabajo, por nombre de índice). D-R6: el historial pasado muestra el nombre nuevo.</summary>
public sealed class RenameResolutionReasonHandler(
    IResolutionReasonRepository reasons,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<RenameResolutionReasonCommand> validator)
    : ICommandHandler<RenameResolutionReasonCommand, ResolutionReasonDto>
{
    public async Task<ResolutionReasonDto> HandleAsync(RenameResolutionReasonCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.ResolutionReasonManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var reason = await reasons.FindActiveAsync(command.TenantId, command.ReasonId, cancellationToken)
            ?? throw ResolutionReasonSupport.NotFound(command.ReasonId);
        if (reason.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The resolution reason changed since it was loaded.");
        }

        var now = clock.UtcNow;
        reason.Rename(command.Name, now);
        audit.RecordResolutionReason(command.TenantId, executionContext.SubjectId, MessagingAuditActions.ResolutionReasonRenamed, reason.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new ResolutionReasonDto(reason.Id, reason.Name, reason.Version);
    }
}

/// <summary>§3.4 <c>DELETE /resolution-reasons/{id}</c>: archiva (D-R5). Ya archivado o ajeno es 404. Sin <c>If-Match</c>: archivar dos
/// veces o archivar un nombre recién corregido deja el mismo resultado, y la pantalla confirma antes.</summary>
public sealed class ArchiveResolutionReasonHandler(
    IResolutionReasonRepository reasons,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ArchiveResolutionReasonCommand, bool>
{
    public async Task<bool> HandleAsync(ArchiveResolutionReasonCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.ResolutionReasonManage, cancellationToken);
        var reason = await reasons.FindActiveAsync(command.TenantId, command.ReasonId, cancellationToken)
            ?? throw ResolutionReasonSupport.NotFound(command.ReasonId);

        var now = clock.UtcNow;
        reason.Archive(now);
        audit.RecordResolutionReason(command.TenantId, executionContext.SubjectId, MessagingAuditActions.ResolutionReasonArchived, reason.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
```

Registra los handlers en `QepServiceCollectionExtensions.cs`, después de `DeleteQuickReplyHandler` (~línea 613):

```csharp
        // Spec 2026-10-15 §3.4: el catálogo de motivos de resolución. Sin estos registros, 500.
        services.AddScoped<IQueryHandler<ListResolutionReasonsQuery, IReadOnlyList<ResolutionReasonDto>>, ListResolutionReasonsHandler>();
        services.AddScoped<ICommandHandler<RenameResolutionReasonCommand, ResolutionReasonDto>, RenameResolutionReasonHandler>();
        services.AddScoped<ICommandHandler<ArchiveResolutionReasonCommand, bool>, ArchiveResolutionReasonHandler>();
```

`ResolveConversationHandler` (línea ~628) y `ListMessagesHandler` (línea ~583) ya están registrados por su interfaz; sus dependencias nuevas las resuelve el contenedor y no hay que tocarlos.

- [ ] **Step 5: Endpoints**

En `MessagingEndpoints.cs`, cambia `private static long RequireVersion(HttpContext httpContext)` a `internal static long RequireVersion(HttpContext httpContext)`. Después de `group.MapQuickReplyEndpoints();`, agrega `group.MapResolutionReasonEndpoints();`.

`src/Modules/Messaging/Modules.Messaging.Api/ResolutionReasonEndpoints.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Messaging.Application;

namespace Modules.Messaging.Api;

/// <summary>
/// Spec 2026-10-15 §3.4: el catálogo de motivos de resolución, bajo <c>/api/v1/tenants/{tenantId:guid}/messaging/resolution-reasons</c>.
/// Leer con <c>messaging.conversation.read</c>; renombrar y archivar con <c>messaging.resolution_reason.manage</c>. Sin <c>POST</c>: el
/// catálogo se alimenta desde el resolver (D-R4). Los handlers revalidan tenant, permiso y módulo.
/// </summary>
internal static class ResolutionReasonEndpoints
{
    public static void MapResolutionReasonEndpoints(this RouteGroupBuilder messaging)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        var group = messaging.MapGroup("/resolution-reasons");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(MessagingPermissions.ConversationRead)
            .Produces<IReadOnlyList<ResolutionReasonDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // Con If-Match como resolve/reopen (428 sin él, 412 con versión vieja).
        group.MapPatch("/{reasonId:guid}", RenameAsync)
            .RequireAuthorization(MessagingPermissions.ResolutionReasonManage)
            .Accepts<RenameResolutionReasonRequest>("application/json")
            .Produces<ResolutionReasonDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapDelete("/{reasonId:guid}", ArchiveAsync)
            .RequireAuthorization(MessagingPermissions.ResolutionReasonManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ListAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListResolutionReasonsQuery(tenantId), cancellationToken));

    private static async Task<IResult> RenameAsync(
        Guid tenantId, Guid reasonId, RenameResolutionReasonRequest request, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new RenameResolutionReasonCommand(tenantId, reasonId, request.Name, MessagingEndpoints.RequireVersion(httpContext)), cancellationToken));

    private static async Task<IResult> ArchiveAsync(Guid tenantId, Guid reasonId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new ArchiveResolutionReasonCommand(tenantId, reasonId), cancellationToken);
        return Results.NoContent();
    }
}

/// <summary>§3.4: el cuerpo del renombrar. Opcional para que un nombre ausente salga 422 con <c>errors.name</c>, no 400 del binder.</summary>
public sealed record RenameResolutionReasonRequest(string? Name);
```

El mensaje de `RequireVersion` habla de «conversation»: no lo cambies. Lo que lee el cliente es el código (`precondition.if_match_required`), y ese mensaje no lo ve nadie.

- [ ] **Step 6: Córrelas y verifica que pasan**

Run: `dotnet test tests/ArchitectureTests/ArchitectureTests`
Expected: PASS, incluidos `MessagingLayerTests` y `CompositionRootTests`.

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ResolutionReason|FullyQualifiedName~QuickReplyApiTests|FullyQualifiedName~MessagingHubTests"`
Expected: PASS. `QuickReplyApiTests` y `MessagingHubTests` corren porque también miran permisos de Messaging.

- [ ] **Step 7: Commit**

```bash
git add src/Modules/Messaging src/Bootstrapper tests/Modules/Messaging tests/ArchitectureTests
git commit -m "feat(messaging): catálogo de motivos de resolución con permiso messaging.resolution_reason.manage"
```

---

### Task 7: README y verificación final

**Files:**
- Modify: `README.md` (tabla de endpoints, líneas ~798-799)

- [ ] **Step 1: Actualiza la tabla de endpoints**

En la fila de `/api/v1/tenants/{tenantId}/messaging`, cambia `` `resolve` y `reopen` (`POST`, `If-Match`) `` por `` `resolve` (`POST`, `If-Match`, cuerpo opcional `{ reasonId }` o `{ reasonName }`) y `reopen` (`POST`, `If-Match`) ``. Después de la fila de `quick-replies`, agrega:

```markdown
| `/api/v1/tenants/{tenantId}/messaging/resolution-reasons` | `GET`; por motivo `PATCH` (`If-Match`) y `DELETE` (archiva). Se crean al resolver | `messaging.conversation.read` (`GET`) / `messaging.resolution_reason.manage` |
```

- [ ] **Step 2: Formato y build**

Run: `dotnet format --verify-no-changes`
Expected: sin cambios pendientes en los archivos de esta rama. Si reporta archivos ajenos a la rama, anótalos en el reporte y no los toques.

Run: `dotnet build`
Expected: `Build succeeded` con `0 Warning(s)` y `0 Error(s)` (el repo trata advertencias como errores).

- [ ] **Step 3: Pruebas acotadas, una última vez**

```bash
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ResolutionReason|FullyQualifiedName~ConversationLifecycleApiTests|FullyQualifiedName~ThreadEventsApiTests|FullyQualifiedName~InboundIngestionTests|FullyQualifiedName~RealtimeEventsTests|FullyQualifiedName~QuickReply"
dotnet test tests/ArchitectureTests/ArchitectureTests
```

Expected: PASS en los tres. Copia el resumen literal (`Passed!  - Failed: 0, Passed: N…`) al reporte.

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "docs(messaging): motivo de resolución y catálogo en la tabla de endpoints"
```

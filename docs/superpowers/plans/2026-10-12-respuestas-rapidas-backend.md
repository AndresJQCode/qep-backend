# Respuestas rápidas — backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el tenant guarde textos con atajo (`/horarios`) para la bandeja de WhatsApp: `GET/POST/PUT/DELETE …/messaging/quick-replies`, con un permiso nuevo para gestionarlas y el `GET` devolviendo además `variables.asesor` y `variables.empresa` resueltos para quien llama.

**Architecture:** Delta sobre Messaging. Un agregado nuevo `QuickReply` (Domain) con las reglas del atajo, del cuerpo y de las tres variables; su tabla `messaging.quick_replies` con índice único `IX_quick_replies_tenant_shortcut`, que `MessagingUnitOfWork` traduce a `messaging.quick_reply.shortcut_taken` (422) discriminando por nombre de índice. Cuatro casos de uso en Application con el mismo esqueleto que las campañas (`MessagingAuthorization.EnsureAsync` → validador → repositorio → auditoría atómica → unit of work). El nombre del tenant entra por un puerto nuevo `IMessagingTenantNames` con adaptador en Bootstrapper sobre `ITenantDirectory.GetDisplayNameAsync`; el del asesor, por `CallerMembership` + `IMessagingMemberNames`, que ya existen. Un permiso nuevo `messaging.quick_reply.manage`, de núcleo y de fábrica sólo en `admin`. Una migración (`AddQuickReplies`), sin claves de configuración ni paquetes NuGet nuevos.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql 10, FluentValidation 12, xUnit v3, Testcontainers (`postgres:18-alpine`).

**Spec:** docs/superpowers/specs/2026-10-12-respuestas-rapidas-design.md

Este plan cubre **sólo §3 (backend)**; la §4 (frontend) es de otro plan. Rutas, formas JSON y códigos de error son **contrato con el frontend**: no se cambian sin cambiar el spec. Quien ejecuta lee el spec, este plan y el `CLAUDE.md` del repo.

## Antes de empezar (leer una vez)

- **Rutas.** Se trabaja en el checkout principal, no en un worktree. Abajo, `$B` es
  `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend`. El estado del shell no persiste: cada bloque vuelve a definir `$B`.
- **Rama.** `feature/respuestas-rapidas` (sale de `develop` `5ad9fa2`; el spec es `975ab9c`). Se comprueba con `git branch --show-current` **en el momento** de commitear (CLAUDE.md: la rama es un estado y cambia entre comandos). Si es otra, se aborta.
- **`Api.exe` o `dotnet … Api.dll` corriendo bloquea** `dotnet build`, `dotnet test` y `dotnet ef` (`MSB3021`). Antes del primer build de cada tarea:

  ```powershell
  Get-CimInstance Win32_Process -Filter "Name = 'Api.exe' OR Name = 'dotnet.exe'" |
    Where-Object { $_.CommandLine -match 'Api(\.dll|\.exe)' } |
    Select-Object ProcessId, Name, CommandLine
  ```

  Si sale algo, **no se mata**: puede ser la sesión de depuración del developer en este mismo checkout. Se detiene la tarea y se le avisa.
- **Pruebas.** Siempre en **primer plano**, nunca con `| tail`, `| Select-Object` ni otro pipe (el pipe esconde el exit code). Por tarea sólo las clases tocadas, con **un** filtro por proyecto (`FullyQualifiedName~A|FullyQualifiedName~B`). Si un paso corre dos proyectos o más: primero `dotnet build "$B\Backend.slnx" --no-restore` con guard de `$LASTEXITCODE`, después cada `dotnet test … --no-build`. La suite completa corre **una sola vez**, en la Task 7. Docker tiene que estar corriendo para las de integración.
- **TDD estricto:** RED antes que GREEN, con la salida **literal** de las dos corridas en el handoff de cada tarea. Un RED por compilación vale cuando la prueba nombra un tipo o miembro que todavía no existe; se copia el `error CS…` literal. Un RED de integración por ruta inexistente (404 donde se esperaba otra cosa) también vale; se copia el `Assert.Equal() Failure` literal.
- **`TreatWarningsAsErrors` + `AnalysisLevel 10.0-recommended`.** Lo que muerde acá:
  - xUnit1051: toda llamada que acepte `CancellationToken` en una prueba recibe `TestContext.Current.CancellationToken`.
  - xUnit2013: nada de `Assert.Equal(0, x.Count)`; `Assert.Empty` / `Assert.Single`.
  - CA1000: nada de miembros estáticos en un tipo genérico; por eso los mensajes del validador viven en `QuickReplyMessages` y no en `QuickReplyInputValidator<T>`.
  - CS9124: un parámetro del constructor primario no inicializa un campo y además se usa en un método. Los handlers de este plan sólo usan sus parámetros en `HandleAsync`, sin campos.
- **Archivos.** Se escriben con las herramientas de edición, nunca con `Set-Content`/`Out-File` de PowerShell 5.1 (rompe las tildes: doble UTF-8). Antes de cada commit, `git status --short` para ver que no quedó un `.cs` vacío ni un archivo ajeno.
- **`dotnet format` no es compuerta** (CRLF: ~90k `ENDOFLINE` en el baseline). La compuerta es el build con analizadores.
- **Commits:** conventional commits en español, como la historia (`feat(messaging): …`). **Sin atribución de IA y sin trailer `Co-Authored-By`**, aunque el harness lo pida. Después de cada commit, `git log -1 --format=%B` para comprobarlo.
- **Secretos:** ninguno nuevo. No se imprime ningún valor de `user-secrets` ni de configuración.
- **Comentarios y documentación** en español, tuteando, con el estilo del código vecino (`<summary>` que cita la sección del spec). Los mensajes que ve una persona (los de `errors`) tutean.

## Global Constraints

- Rutas, bajo el grupo real `/api/v1/tenants/{tenantId:guid}/messaging`:
  - `GET /quick-replies` — `messaging.conversation.read` — `200 QuickReplyListDto`.
  - `POST /quick-replies` — `messaging.quick_reply.manage` — `201 QuickReplyDto`, **sin** `Location`.
  - `PUT /quick-replies/{quickReplyId:guid}` — `messaging.quick_reply.manage` — `200 QuickReplyDto`.
  - `DELETE /quick-replies/{quickReplyId:guid}?version=` — `messaging.quick_reply.manage` — `204`.
- DTOs (contrato, nombres exactos): `QuickReplyDto(Guid Id, string Shortcut, string Body, DateTimeOffset UpdatedAt, long Version)`, `QuickReplyVariablesDto(string Asesor, string Empresa)`, `QuickReplyListDto(IReadOnlyList<QuickReplyDto> Items, QuickReplyVariablesDto Variables)`, `SaveQuickReplyRequest(string? Shortcut, string? Body, long? Version)`. En JSON: `items`, `variables.asesor`, `variables.empresa`, `id`, `shortcut`, `body`, `updatedAt`, `version`.
- Atajo: se normaliza con `Trim()` + `ToLowerInvariant()` y después tiene que cumplir `^[a-z0-9-]{2,32}\z`. Viaja y se guarda sin `/`; una `/` inicial es inválida.
- Cuerpo: se guarda con `Trim()`, de 1 a 4096 caracteres **después** del recorte.
- Variables: todo `{…}` sin otra llave adentro debe ser exactamente `{nombre}`, `{asesor}` o `{empresa}` (sensible a mayúsculas). Llaves sueltas son texto.
- Códigos nuevos (`MessagingErrorCodes`): `messaging.quick_reply.invalid_shortcut`, `messaging.quick_reply.invalid_body`, `messaging.quick_reply.unknown_variable` (los tres `MessagingDomainException` → 422), `messaging.quick_reply.shortcut_taken` (422, traducido en `MessagingUnitOfWork` por nombre de índice), `messaging.quick_reply.not_found` (`ResourceNotFoundException` → 404).
- Validación de entrada: 422 `validation.failed` con claves de `errors` `shortcut`, `body` y `version`.
- 412 `concurrency.conflict` (`RequestConcurrencyException`) cuando `version` no coincide en `PUT`/`DELETE`.
- 403 `authorization.denied` si el tenant de la ruta no es el de quien llama o falta el permiso; 403 `tenancy.module_not_enabled` con el módulo apagado. Un id de otro tenant bajo la ruta propia responde 404, igual que uno inexistente.
- Auditoría por `IMessagingAuditRecorder.RecordQuickReply`, `resource_type = 'quick_reply'`, acciones `messaging.quick_reply.created|updated|deleted`, en el mismo `SaveChanges`.
- `variables.asesor` = nombre de la membresía activa de quien llama (`CallerMembership` + `IMessagingMemberNames`), o `""`. `variables.empresa` = `Tenant.DisplayName` por `IMessagingTenantNames`, o `""`. Nunca `null`.
- Lista: completa, sin paginar, ordenada por `shortcut` con `StringComparer.Ordinal`.
- Permiso `MessagingPermissions.QuickReplyManage = "messaging.quick_reply.manage"`: constante, `PermissionDefinition` de núcleo (`RequiredModules: []`, categoría `Messaging`), política en `AddAuthorization` y de fábrica **sólo** en `admin`.
- Sin claves de configuración nuevas, sin paquetes NuGet nuevos. Una migración: `AddQuickReplies`.

## Review Focus

1. **Atajo con mayúsculas, espacios o `/`:** `" Horarios "` se guarda `horarios`; `"/horarios"`, `"hola mundo"` y `"hola_mundo"` son inválidos; y `"HORARIOS"` contra un `horarios` existente es `shortcut_taken`, no una segunda fila. → Task 1 (`CreateNormalizesTheShortcutAndTrimsTheBody`, `AnInvalidShortcutIsRefused`), Task 2 (`TheShortcutIsUniquePerTenantAndATakenOneIsADomainError`), Task 5 (`ATakenShortcutIs422EvenInAnotherCaseAndAnotherTenantCanUseIt`).
2. **Cuerpo sólo con espacios o justo en el borde:** `"   "` y `"\n\t "` son `invalid_body`; 4096 `a` con espacios a los lados pasan (se recortan primero); 4097 no. → Task 1 (`ABlankBodyIsRefused`, `TheBodyLimitCountsAfterTrimming`), Task 4 (`AnEmptyBodyFailsOnBody`, `FourThousandNinetySixCharactersAfterTrimPassAndOneMoreDoesNot`).
3. **Variables mal escritas o llaves raras:** `{Nombre}`, `{ nombre }`, `{}` y `{precio}` son `unknown_variable` (llegarían literales al cliente); `{{nombre}}`, `{10% off` y `cierra }` son texto y pasan. Y el mensaje del validador, que lleva `{nombre}` literal, no lo deforma el formateador de FluentValidation. → Task 1 (`AnUnknownVariableIsRefused`, `LooseBracesAreText`), Task 4 (`AnUnknownVariableFailsOnBodyNamingIt`).
4. **Editar sin cambiar el atajo:** un `PUT` con el mismo `shortcut` que ya tiene no puede chocar con su propio índice único; renombrar a uno tomado sí es 422. → Task 2 (`UpdatingToItsOwnShortcutDoesNotTripTheIndexAndBumpsTheVersion`), Task 6 (`UpdatingKeepsItsOwnShortcutBumpsTheVersionAndAudits`, `RenamingToATakenShortcutIs422`).
5. **Versión vieja y frontera de tenant en `PUT`/`DELETE`:** `DELETE ?version=1` después de una edición es 412 y **no borra**; un id de otro tenant bajo la ruta propia es 404 `not_found` (igual que uno inexistente, no confirma que existe); la ruta de otro tenant es 403 `authorization.denied`, y la fila sigue intacta. → Task 2 (`AStaleVersionIsAConcurrencyConflict`), Task 6 (`AStaleVersionIs412OnUpdateAndOnDeleteAndNothingChanges`, `AnUnknownOrForeignIdIs404AndAnotherTenantsRouteIs403`).

## File Structure

| Archivo | Qué |
| --- | --- |
| `src/Modules/Messaging/Modules.Messaging.Domain/QuickReply.cs` (nuevo) | Agregado y reglas del atajo, del cuerpo y de las variables. |
| `src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs` | Los cinco códigos `messaging.quick_reply.*`. |
| `src/Modules/Messaging/Modules.Messaging.Application/QuickReplyPorts.cs` (nuevo) | `IQuickReplyRepository`. |
| `src/Modules/Messaging/Modules.Messaging.Application/IMessagingTenantNames.cs` (nuevo) | Puerto del nombre del tenant (D-Q5). |
| `src/Modules/Messaging/Modules.Messaging.Application/QuickReplyDtos.cs` (nuevo) | `QuickReplyDto`, `QuickReplyVariablesDto`, `QuickReplyListDto`. |
| `src/Modules/Messaging/Modules.Messaging.Application/QuickReplies.cs` (nuevo) | Comandos, consulta, validadores, mensajes y los cuatro handlers. |
| `src/Modules/Messaging/Modules.Messaging.Application/MessagingPermissions.cs` | `QuickReplyManage`. |
| `src/Modules/Messaging/Modules.Messaging.Application/IMessagingAuditRecorder.cs` | `RecordQuickReply` y sus acciones. |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingDbContext.cs` | `QuickReplies`, `ConfigureQuickReply`, `QuickReplyShortcutIndex`. |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/QuickReplyRepository.cs` (nuevo) | El repositorio. |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingUnitOfWork.cs` | Traducción del índice único. |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingAuditRecorder.cs` | `RecordQuickReply`. |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/Migrations/<ts>_AddQuickReplies.cs` (+ `.Designer.cs`, snapshot) | Generados por `dotnet ef`. |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs` | Registro del repositorio. |
| `src/Modules/Messaging/Modules.Messaging.Api/QuickReplyEndpoints.cs` (nuevo) | Las cuatro rutas y `SaveQuickReplyRequest`. |
| `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs` | `group.MapQuickReplyEndpoints();`. |
| `src/Bootstrapper/MessagingTenantNames.cs` (nuevo) | Adaptador sobre `ITenantDirectory`. |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` | Permiso (rol, definición, política), handlers y adaptador. |
| `README.md` | Contrato de las rutas nuevas. |
| `tests/Modules/Messaging/Modules.Messaging.UnitTests/QuickReplyTests.cs` (nuevo) | Unitarias del agregado. |
| `tests/Modules/Messaging/Modules.Messaging.UnitTests/QuickReplyValidatorsTests.cs` (nuevo) | Unitarias de los validadores. |
| `tests/Modules/Messaging/Modules.Messaging.UnitTests/CampaignTestDoubles.cs` | `RecordingAudit` implementa el método nuevo. |
| `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/QuickReplyPersistenceTests.cs` (nuevo) | Índice único, versión, lista, contra Postgres. |
| `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/QuickReplyApiTests.cs` (nuevo) | El contrato HTTP completo. |
| `tests/Bootstrapper/Bootstrapper.UnitTests/MessagingTenantNamesTests.cs` (nuevo) | El adaptador. |
| `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs` | 52 permisos y el nuevo sólo en `admin`. |

---

### Task 1: Agregado `QuickReply` y sus códigos de error

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Domain/QuickReply.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs` (al final de la clase, después de `CampaignInvalid`)
- Create: `tests/Modules/Messaging/Modules.Messaging.UnitTests/QuickReplyTests.cs`

**Interfaces:**
- Consumes: `MessagingDomainException(string code, string message)` (existente).
- Produces:
  - `public sealed partial class QuickReply` con `Id`, `TenantId`, `Shortcut`, `Body`, `CreatedAt`, `UpdatedAt`, `Version` (setters privados), `const int ShortcutMaxLength = 32`, `const int BodyMaxLength = 4096`.
  - `public static QuickReply Create(Guid tenantId, string? shortcut, string? body, DateTimeOffset now)` — `Version = 1`.
  - `public void Update(string? shortcut, string? body, DateTimeOffset now)` — `Version++`.
  - `public static string NormalizeShortcut(string? shortcut)`, `public static bool IsValidShortcut(string normalizedShortcut)`, `public static string TrimBody(string? body)`, `public static bool IsValidBodyLength(string trimmedBody)`, `public static string? FindUnknownVariable(string body)` — los usa el validador de la Task 4.
  - `MessagingErrorCodes.QuickReplyInvalidShortcut`, `QuickReplyInvalidBody`, `QuickReplyUnknownVariable`, `QuickReplyShortcutTaken`, `QuickReplyNotFound`.

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Messaging/Modules.Messaging.UnitTests/QuickReplyTests.cs`:

```csharp
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-12 §3.1, §3.2 y §3.7: el agregado <see cref="QuickReply"/>. El atajo se normaliza (recorte y minúsculas) y
/// cumple <c>^[a-z0-9-]{2,32}$</c>; el cuerpo se recorta y lleva de 1 a 4096 caracteres; las únicas variables son <c>{nombre}</c>,
/// <c>{asesor}</c> y <c>{empresa}</c>, y una llave suelta es texto.</summary>
public sealed class QuickReplyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddMinutes(5);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-0000000000a1");

    private static QuickReply Reply(string? shortcut = "horarios", string? body = "Abrimos de 8 a 6.") =>
        QuickReply.Create(TenantId, shortcut, body, Now);

    private static string CodeOf(Action action) => Assert.Throws<MessagingDomainException>(action).Code;

    [Fact]
    public void CreateNormalizesTheShortcutAndTrimsTheBody()
    {
        var reply = Reply(shortcut: "  Horarios ", body: "  Abrimos de 8 a 6, {nombre}.  ");

        Assert.NotEqual(Guid.Empty, reply.Id);
        Assert.Equal(TenantId, reply.TenantId);
        Assert.Equal(("horarios", "Abrimos de 8 a 6, {nombre}."), (reply.Shortcut, reply.Body));
        Assert.Equal((Now, Now, 1L), (reply.CreatedAt, reply.UpdatedAt, reply.Version));
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("horarios")]
    [InlineData("pago-2")]
    [InlineData("24h")]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345")] // 32
    public void AValidShortcutIsKept(string shortcut) => Assert.Equal(shortcut, Reply(shortcut: shortcut).Shortcut);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]
    [InlineData("/horarios")]
    [InlineData("hola mundo")]
    [InlineData("hola_mundo")]
    [InlineData("ñandú")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")] // 33
    public void AnInvalidShortcutIsRefused(string? shortcut) =>
        Assert.Equal(MessagingErrorCodes.QuickReplyInvalidShortcut, CodeOf(() => Reply(shortcut: shortcut)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("\n\t ")]
    public void ABlankBodyIsRefused(string? body) =>
        Assert.Equal(MessagingErrorCodes.QuickReplyInvalidBody, CodeOf(() => Reply(body: body)));

    [Fact]
    public void TheBodyLimitCountsAfterTrimming()
    {
        Assert.Equal(4096, Reply(body: "  " + new string('a', 4096) + "  ").Body.Length);
        Assert.Equal(MessagingErrorCodes.QuickReplyInvalidBody, CodeOf(() => Reply(body: new string('a', 4097))));
    }

    [Theory]
    [InlineData("Hola {Nombre}")]
    [InlineData("Hola { nombre }")]
    [InlineData("Hola {}")]
    [InlineData("Total {precio} para {nombre}")]
    public void AnUnknownVariableIsRefused(string body) =>
        Assert.Equal(MessagingErrorCodes.QuickReplyUnknownVariable, CodeOf(() => Reply(body: body)));

    [Theory]
    [InlineData("Hola {nombre}, soy {asesor} de {empresa}.")]
    [InlineData("{nombre}{nombre}")]
    [InlineData("Precio {10% off")]
    [InlineData("Cierra } antes")]
    [InlineData("{{nombre}}")]
    [InlineData("Sin variables")]
    public void LooseBracesAreText(string body) => Assert.Equal(body, Reply(body: body).Body);

    [Fact]
    public void UpdateReplacesBothFieldsBumpsTheVersionAndKeepsTheCreation()
    {
        var reply = Reply();

        reply.Update(" Horarios ", "Abrimos de 7 a 7.", Later);

        Assert.Equal(("horarios", "Abrimos de 7 a 7."), (reply.Shortcut, reply.Body));
        Assert.Equal((Now, Later, 2L), (reply.CreatedAt, reply.UpdatedAt, reply.Version));
    }

    [Fact]
    public void ARefusedUpdateLeavesTheReplyAsItWas()
    {
        var reply = Reply();

        Assert.Equal(MessagingErrorCodes.QuickReplyUnknownVariable, CodeOf(() => reply.Update("otro", "Hola {cliente}", Later)));

        Assert.Equal(("horarios", "Abrimos de 8 a 6.", Now, 1L), (reply.Shortcut, reply.Body, reply.UpdatedAt, reply.Version));
    }

    [Fact]
    public void TheCodesAreTheOnesOfTheContract()
    {
        Assert.Equal("messaging.quick_reply.invalid_shortcut", MessagingErrorCodes.QuickReplyInvalidShortcut);
        Assert.Equal("messaging.quick_reply.invalid_body", MessagingErrorCodes.QuickReplyInvalidBody);
        Assert.Equal("messaging.quick_reply.unknown_variable", MessagingErrorCodes.QuickReplyUnknownVariable);
        Assert.Equal("messaging.quick_reply.shortcut_taken", MessagingErrorCodes.QuickReplyShortcutTaken);
        Assert.Equal("messaging.quick_reply.not_found", MessagingErrorCodes.QuickReplyNotFound);
    }
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.UnitTests\Modules.Messaging.UnitTests.csproj" --filter "FullyQualifiedName~QuickReplyTests"
```

Esperado: falla la compilación con `error CS0246` sobre `QuickReply` y `error CS0117` sobre `MessagingErrorCodes.QuickReply…`.

- [ ] **Step 3: Implementar**

`src/Modules/Messaging/Modules.Messaging.Domain/QuickReply.cs`:

```csharp
using System.Text.RegularExpressions;

namespace Modules.Messaging.Domain;

/// <summary>
/// Spec 2026-10-12 §3.1 y §3.2: una respuesta rápida del tenant (D-Q1: global, sin dueño). El atajo se guarda normalizado —sin
/// espacios a los lados y en minúsculas— y cumple <c>^[a-z0-9-]{2,32}$</c>, así que una <c>/</c> inicial no pasa. El cuerpo se guarda
/// recortado, de 1 a 4096 caracteres (el tope de texto de WhatsApp), y sólo admite las variables de D-Q3; una llave suelta es texto.
/// El validador de Application usa estas mismas reglas para marcar el campo; acá son la última línea de defensa.
/// </summary>
public sealed partial class QuickReply
{
    public const int ShortcutMaxLength = 32;

    public const int BodyMaxLength = 4096;

    private QuickReply()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Shortcut { get; private set; } = string.Empty;

    public string Body { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public static QuickReply Create(Guid tenantId, string? shortcut, string? body, DateTimeOffset now)
    {
        var (normalizedShortcut, trimmedBody) = Validate(shortcut, body);
        return new QuickReply
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Shortcut = normalizedShortcut,
            Body = trimmedBody,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
    }

    /// <summary>Reemplaza los dos campos. Valida todo antes de tocar nada: un rechazo deja la respuesta como estaba.</summary>
    public void Update(string? shortcut, string? body, DateTimeOffset now)
    {
        var (normalizedShortcut, trimmedBody) = Validate(shortcut, body);
        Shortcut = normalizedShortcut;
        Body = trimmedBody;
        UpdatedAt = now;
        Version++;
    }

    public static string NormalizeShortcut(string? shortcut) => (shortcut ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsValidShortcut(string normalizedShortcut)
    {
        ArgumentNullException.ThrowIfNull(normalizedShortcut);
        return ShortcutPattern().IsMatch(normalizedShortcut);
    }

    public static string TrimBody(string? body) => (body ?? string.Empty).Trim();

    public static bool IsValidBodyLength(string trimmedBody)
    {
        ArgumentNullException.ThrowIfNull(trimmedBody);
        return trimmedBody.Length is > 0 and <= BodyMaxLength;
    }

    /// <summary>D-Q3: el primer <c>{…}</c> que no es <c>{nombre}</c>, <c>{asesor}</c> ni <c>{empresa}</c> (con sus llaves), o
    /// <c>null</c>. Sensible a mayúsculas: el frontend reemplaza exactamente esas tres, y cualquier otra llegaría literal al cliente.</summary>
    public static string? FindUnknownVariable(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        foreach (Match match in VariablePattern().Matches(body))
        {
            if (match.Groups[1].Value is not ("nombre" or "asesor" or "empresa"))
            {
                return match.Value;
            }
        }

        return null;
    }

    private static (string Shortcut, string Body) Validate(string? shortcut, string? body)
    {
        var normalizedShortcut = NormalizeShortcut(shortcut);
        if (!IsValidShortcut(normalizedShortcut))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.QuickReplyInvalidShortcut, "The shortcut takes 2 to 32 lowercase letters, digits or hyphens.");
        }

        var trimmedBody = TrimBody(body);
        if (!IsValidBodyLength(trimmedBody))
        {
            throw new MessagingDomainException(MessagingErrorCodes.QuickReplyInvalidBody, $"The body takes 1 to {BodyMaxLength} characters.");
        }

        if (FindUnknownVariable(trimmedBody) is { } unknown)
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.QuickReplyUnknownVariable, $"'{unknown}' is not a variable; use {{nombre}}, {{asesor}} or {{empresa}}.");
        }

        return (normalizedShortcut, trimmedBody);
    }

    // \z y no $: $ también acepta un salto de línea al final.
    [GeneratedRegex(@"^[a-z0-9-]{2,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ShortcutPattern();

    // Lo de adentro de un par de llaves sin otra llave en medio: en «{{nombre}}» encuentra «{nombre}» y las de afuera son texto.
    [GeneratedRegex(@"\{([^{}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex VariablePattern();
}
```

`MessagingErrorCodes.cs`, al final de la clase:

```csharp
    /// <summary>Spec 2026-10-12 §3.2: el atajo no cumple <c>^[a-z0-9-]{2,32}$</c> después de recortarlo y pasarlo a minúsculas.</summary>
    public const string QuickReplyInvalidShortcut = "messaging.quick_reply.invalid_shortcut";

    /// <summary>§3.2: el cuerpo, recortado, está vacío o pasa de 4096 caracteres.</summary>
    public const string QuickReplyInvalidBody = "messaging.quick_reply.invalid_body";

    /// <summary>§3.2 y D-Q3: un <c>{…}</c> que no es <c>{nombre}</c>, <c>{asesor}</c> ni <c>{empresa}</c>.</summary>
    public const string QuickReplyUnknownVariable = "messaging.quick_reply.unknown_variable";

    /// <summary>§3.4: otra respuesta del tenant ya usa ese atajo (<c>IX_quick_replies_tenant_shortcut</c>). 422, como
    /// <c>catalog.product.code_taken</c>.</summary>
    public const string QuickReplyShortcutTaken = "messaging.quick_reply.shortcut_taken";

    /// <summary>§3.3: la respuesta no existe en el tenant (404). Un id de otro tenant responde igual.</summary>
    public const string QuickReplyNotFound = "messaging.quick_reply.not_found";
```

- [ ] **Step 4: Correr y verificar que pasan, más ArchitectureTests**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.UnitTests\Modules.Messaging.UnitTests.csproj" --no-build --filter "FullyQualifiedName~QuickReplyTests"
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --no-build --filter "FullyQualifiedName~MessagingLayerTests"
```

Esperado: 33 verdes en `QuickReplyTests` (1 + 5 + 9 + 4 + 1 + 4 + 6 + 1 + 1 + 1, contando cada caso de `[Theory]`) y `MessagingLayerTests` verde.

- [ ] **Step 5: Commit**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
if ((git -C $B branch --show-current) -ne "feature/respuestas-rapidas") { throw "rama equivocada" }
git -C $B status --short
git -C $B add src/Modules/Messaging/Modules.Messaging.Domain/QuickReply.cs src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs tests/Modules/Messaging/Modules.Messaging.UnitTests/QuickReplyTests.cs
git -C $B commit -m "feat(messaging): agregado de respuestas rápidas con atajo, cuerpo y variables"
git -C $B log -1 --format=%B
```

---

### Task 2: Persistencia — repositorio, tabla, migración `AddQuickReplies` y `shortcut_taken`

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/QuickReplyPorts.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/QuickReplyRepository.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingDbContext.cs` (constante, `DbSet`, `OnModelCreating`, método `ConfigureQuickReply`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingUnitOfWork.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs` (después de `services.AddScoped<CampaignMapper>();`)
- Create (generados): `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/Migrations/<timestamp>_AddQuickReplies.cs`, `<timestamp>_AddQuickReplies.Designer.cs`; Modify (generado): `MessagingDbContextModelSnapshot.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/QuickReplyPersistenceTests.cs`

**Interfaces:**
- Consumes: `QuickReply` (Task 1), `MessagingErrorCodes.QuickReplyShortcutTaken` (Task 1), `IMessagingUnitOfWork` (existente).
- Produces:
  - `public interface IQuickReplyRepository { Task<QuickReply?> FindAsync(Guid tenantId, Guid quickReplyId, CancellationToken cancellationToken); Task<IReadOnlyList<QuickReply>> ListAsync(Guid tenantId, CancellationToken cancellationToken); void Add(QuickReply quickReply); void Remove(QuickReply quickReply); }`
  - `MessagingDbContext.QuickReplyShortcutIndex = "IX_quick_replies_tenant_shortcut"`, `public DbSet<QuickReply> QuickReplies`.
  - `MessagingUnitOfWork.SaveChangesAsync` lanza `MessagingDomainException(QuickReplyShortcutTaken)` ante esa violación.

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/QuickReplyPersistenceTests.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-12 §3.1 y §3.4 contra Postgres: el atajo es único por tenant y la violación del índice sale como
/// <c>messaging.quick_reply.shortcut_taken</c> (por nombre de índice, no sólo por 23505); editar sin cambiar el atajo no choca con su
/// propia fila; la versión vieja es <c>concurrency.conflict</c>; la lista es del tenant y en orden ordinal.</summary>
public sealed class QuickReplyPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<QuickReply> SaveAsync(QepApiFactory factory, Guid tenantId, string shortcut, string body = "Abrimos de 8 a 6.")
    {
        using var scope = factory.Services.CreateScope();
        var quickReply = QuickReply.Create(tenantId, shortcut, body, DateTimeOffset.UtcNow);
        scope.ServiceProvider.GetRequiredService<IQuickReplyRepository>().Add(quickReply);
        await scope.ServiceProvider.GetRequiredService<IMessagingUnitOfWork>().SaveChangesAsync(Ct);
        return quickReply;
    }

    [Fact]
    public async Task TheUniqueIndexHasTheNameTheUnitOfWorkLooksFor()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        _ = factory.Services;

        var definition = await ScalarAsync<string>(connectionString,
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'messaging' AND indexname = 'IX_quick_replies_tenant_shortcut'");

        Assert.Contains("UNIQUE", definition, StringComparison.Ordinal);
        Assert.Contains("(tenant_id, shortcut)", definition, StringComparison.Ordinal);
    }

    // Review Focus 1: «HORARIOS» se normaliza a «horarios» y choca; otro tenant puede usarlo.
    [Fact]
    public async Task TheShortcutIsUniquePerTenantAndATakenOneIsADomainError()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        await SaveAsync(factory, tenantId, "horarios");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => SaveAsync(factory, tenantId, "HORARIOS"));
        await SaveAsync(factory, Guid.CreateVersion7(), "horarios");

        Assert.Equal(MessagingErrorCodes.QuickReplyShortcutTaken, error.Code);
        Assert.Equal(2L, await CountAsync(connectionString, "SELECT count(*) FROM messaging.quick_replies WHERE shortcut = 'horarios'"));
    }

    // Review Focus 4: el UPDATE con el mismo atajo toca la misma fila; el índice único no lo ve como duplicado.
    [Fact]
    public async Task UpdatingToItsOwnShortcutDoesNotTripTheIndexAndBumpsTheVersion()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var saved = await SaveAsync(factory, tenantId, "horarios");

        using (var scope = factory.Services.CreateScope())
        {
            var quickReply = await scope.ServiceProvider.GetRequiredService<IQuickReplyRepository>().FindAsync(tenantId, saved.Id, Ct);
            Assert.NotNull(quickReply);
            quickReply.Update("horarios", "Abrimos de 7 a 7.", DateTimeOffset.UtcNow);
            await scope.ServiceProvider.GetRequiredService<IMessagingUnitOfWork>().SaveChangesAsync(Ct);
        }

        Assert.Equal(2L, await ScalarAsync<long>(connectionString, "SELECT version FROM messaging.quick_replies WHERE id = @id", ("id", saved.Id)));
        Assert.Equal("Abrimos de 7 a 7.", await ScalarAsync<string>(connectionString, "SELECT body FROM messaging.quick_replies WHERE id = @id", ("id", saved.Id)));
    }

    // Review Focus 5: otro pod editó entre la lectura y el commit.
    [Fact]
    public async Task AStaleVersionIsAConcurrencyConflict()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var saved = await SaveAsync(factory, tenantId, "horarios");

        using var scope = factory.Services.CreateScope();
        var quickReply = await scope.ServiceProvider.GetRequiredService<IQuickReplyRepository>().FindAsync(tenantId, saved.Id, Ct);
        Assert.NotNull(quickReply);
        await ExecuteAsync(connectionString, "UPDATE messaging.quick_replies SET version = version + 1 WHERE id = @id", ("id", saved.Id));
        quickReply.Update("horarios", "Otro texto.", DateTimeOffset.UtcNow);

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            scope.ServiceProvider.GetRequiredService<IMessagingUnitOfWork>().SaveChangesAsync(Ct));
        Assert.Equal("concurrency.conflict", error.Code);
    }

    [Fact]
    public async Task TheListIsTheTenantsOnlyInOrdinalOrderAndFindIgnoresOtherTenants()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        await SaveAsync(factory, tenantId, "pago");
        await SaveAsync(factory, tenantId, "horarios");
        await SaveAsync(factory, tenantId, "a-domicilio");
        var foreign = await SaveAsync(factory, Guid.CreateVersion7(), "ajeno");

        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IQuickReplyRepository>();
        var list = await repository.ListAsync(tenantId, Ct);

        Assert.Equal(["a-domicilio", "horarios", "pago"], list.Select(quickReply => quickReply.Shortcut));
        Assert.Null(await repository.FindAsync(tenantId, foreign.Id, Ct));
    }
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.IntegrationTests\Modules.Messaging.IntegrationTests.csproj" --filter "FullyQualifiedName~QuickReplyPersistenceTests"
```

Esperado: `error CS0246` sobre `IQuickReplyRepository`.

- [ ] **Step 3: Implementar el puerto, el modelo, el repositorio y la traducción**

`src/Modules/Messaging/Modules.Messaging.Application/QuickReplyPorts.cs`:

```csharp
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-12 §3.1: las respuestas rápidas del tenant. Todo método recibe <c>tenantId</c>: el id de otro tenant responde igual que
/// uno inexistente. El agregado se escribe con tracking y se guarda con <see cref="IMessagingUnitOfWork"/>, que traduce el índice único
/// del atajo a <c>messaging.quick_reply.shortcut_taken</c> y lleva la auditoría en la misma transacción.
/// </summary>
public interface IQuickReplyRepository
{
    /// <summary>Con tracking.</summary>
    Task<QuickReply?> FindAsync(Guid tenantId, Guid quickReplyId, CancellationToken cancellationToken);

    /// <summary>D-Q7: todas, sin paginar, por <c>shortcut</c> en orden ordinal. Sin tracking.</summary>
    Task<IReadOnlyList<QuickReply>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    void Add(QuickReply quickReply);

    void Remove(QuickReply quickReply);
}
```

`MessagingDbContext.cs`:

1. Debajo de `public const string SearchVectorSql = …;`:

```csharp
    /// <summary>Spec 2026-10-12 §3.4: <c>MessagingUnitOfWork</c> discrimina por este nombre para responder <c>shortcut_taken</c>.</summary>
    public const string QuickReplyShortcutIndex = "IX_quick_replies_tenant_shortcut";
```

2. Debajo de `internal DbSet<CampaignRecipientRecord> CampaignRecipients …`:

```csharp
    /// <summary>Spec 2026-10-12 §3.1: el agregado de la respuesta rápida.</summary>
    public DbSet<QuickReply> QuickReplies => Set<QuickReply>();
```

3. En `OnModelCreating`, después de `ConfigureCampaignRecipient(modelBuilder);`:

```csharp
        ConfigureQuickReply(modelBuilder);
```

4. Método nuevo, después de `ConfigureCampaignRecipient`:

```csharp
    /// <summary>Spec 2026-10-12 §3.1: el atajo es único por tenant. El índice va con nombre porque la unidad de trabajo lo reconoce
    /// por nombre (§3.4).</summary>
    private static void ConfigureQuickReply(ModelBuilder modelBuilder)
    {
        var quickReply = modelBuilder.Entity<QuickReply>();
        quickReply.ToTable("quick_replies", Schema);
        quickReply.HasKey(value => value.Id);
        quickReply.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        quickReply.Property(value => value.TenantId).HasColumnName("tenant_id");
        quickReply.Property(value => value.Shortcut).HasColumnName("shortcut").HasMaxLength(QuickReply.ShortcutMaxLength);
        quickReply.Property(value => value.Body).HasColumnName("body").HasMaxLength(QuickReply.BodyMaxLength);
        quickReply.Property(value => value.CreatedAt).HasColumnName("created_at");
        quickReply.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        quickReply.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        quickReply.HasIndex(value => new { value.TenantId, value.Shortcut }, QuickReplyShortcutIndex).IsUnique();
    }
```

`src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/QuickReplyRepository.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Spec 2026-10-12 §3.1: el agregado con tracking (la versión es el token de concurrencia, como la campaña).</summary>
internal sealed class QuickReplyRepository(MessagingDbContext dbContext) : IQuickReplyRepository
{
    public Task<QuickReply?> FindAsync(Guid tenantId, Guid quickReplyId, CancellationToken cancellationToken) =>
        dbContext.QuickReplies.SingleOrDefaultAsync(quickReply => quickReply.TenantId == tenantId && quickReply.Id == quickReplyId, cancellationToken);

    public async Task<IReadOnlyList<QuickReply>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.QuickReplies.AsNoTracking().Where(quickReply => quickReply.TenantId == tenantId).ToListAsync(cancellationToken);
        // D-Q7: orden ordinal y no la collation de la base, que depende de cómo se creó el clúster. Un tenant tiene decenas.
        return rows.OrderBy(quickReply => quickReply.Shortcut, StringComparer.Ordinal).ToArray();
    }

    public void Add(QuickReply quickReply) => dbContext.QuickReplies.Add(quickReply);

    public void Remove(QuickReply quickReply) => dbContext.QuickReplies.Remove(quickReply);
}
```

`MessagingUnitOfWork.cs`, completo:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Npgsql;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>La versión vieja es <c>concurrency.conflict</c> (412 por If-Match en resolve/reopen, §8.5). Spec 2026-10-12 §3.4: el atajo
/// repetido es <c>messaging.quick_reply.shortcut_taken</c> (422).</summary>
internal sealed class MessagingUnitOfWork(MessagingDbContext dbContext) : IMessagingUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Antes que la rama de DbUpdateException: DbUpdateConcurrencyException hereda de ella (mismo orden que CatalogUnitOfWork).
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict", "The conversation changed while the operation was being committed.", exception);
        }
        // Por nombre de índice y no sólo por 23505: el esquema tiene otros únicos (IX_messages_conversation_client, …) y responder
        // shortcut_taken por uno de ellos mandaría a corregir el campo equivocado (precedente: catalog.product.code_taken, SDD-CT-06).
        // Traducido acá y no en Application, que no referencia Npgsql (MessagingLayerTests).
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, MessagingDbContext.QuickReplyShortcutIndex, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.QuickReplyShortcutTaken, "Another quick reply in this tenant already uses that shortcut.", exception);
        }
    }
}
```

`MessagingInfrastructureExtensions.cs`, después de `services.AddScoped<CampaignMapper>();`:

```csharp
        // Spec 2026-10-12 §3.1: las respuestas rápidas, con tracking; el atajo repetido lo traduce MessagingUnitOfWork.
        services.AddScoped<IQuickReplyRepository, QuickReplyRepository>();
```

- [ ] **Step 4: Generar la migración con el factory de diseño**

Comprobar antes que no corre `Api.exe` (ver «Antes de empezar»). Sin `--startup-project` (CLAUDE.md: `Api.csproj` no referencia EF Design; se usa `MessagingDbContextFactory`):

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet ef migrations add AddQuickReplies --project "$B\src\Modules\Messaging\Modules.Messaging.Infrastructure" --context MessagingDbContext -o Persistence/Migrations
if ($LASTEXITCODE -ne 0) { throw "ef falló" }
git -C $B status --short
Select-String -Path "$B\src\Modules\Messaging\Modules.Messaging.Infrastructure\Persistence\Migrations\*_AddQuickReplies.cs" -Pattern "CreateTable|CreateIndex|AddColumn|AlterColumn|DropTable|unique: true|IX_quick_replies_tenant_shortcut"
```

Esperado en `git status`: sólo los dos archivos `*_AddQuickReplies*.cs` nuevos y `MessagingDbContextModelSnapshot.cs` modificado (más lo de los pasos anteriores). En `Select-String`: **un** `CreateTable` (`quick_replies`), **un** `CreateIndex` con `IX_quick_replies_tenant_shortcut` y `unique: true`, y un `DropTable` en el `Down`. El `Up` tiene que ser equivalente a:

```csharp
            migrationBuilder.CreateTable(
                name: "quick_replies",
                schema: "messaging",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shortcut = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    body = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quick_replies", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_quick_replies_tenant_shortcut",
                schema: "messaging",
                table: "quick_replies",
                columns: new[] { "tenant_id", "shortcut" },
                unique: true);
```

Si aparece un `AddColumn`, `AlterColumn` u otra tabla, el modelo tenía un desvío previo: **no** se corrige en esta migración; se borra (`dotnet ef migrations remove --project … --context MessagingDbContext`), se detiene la tarea y se avisa.

- [ ] **Step 5: Correr y verificar que pasan, más ArchitectureTests**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.IntegrationTests\Modules.Messaging.IntegrationTests.csproj" --no-build --filter "FullyQualifiedName~QuickReplyPersistenceTests|FullyQualifiedName~MessagingPersistenceTests"
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --no-build --filter "FullyQualifiedName~MessagingLayerTests"
```

Esperado: 5 verdes en `QuickReplyPersistenceTests`, `MessagingPersistenceTests` igual que antes (incluida la de migración idempotente) y `MessagingLayerTests` verde.

- [ ] **Step 6: Commit**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
if ((git -C $B branch --show-current) -ne "feature/respuestas-rapidas") { throw "rama equivocada" }
git -C $B status --short
git -C $B add src/Modules/Messaging/Modules.Messaging.Application/QuickReplyPorts.cs src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/QuickReplyRepository.cs src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingDbContext.cs src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingUnitOfWork.cs src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/Migrations tests/Modules/Messaging/Modules.Messaging.IntegrationTests/QuickReplyPersistenceTests.cs
git -C $B commit -m "feat(messaging): tabla de respuestas rápidas con atajo único por tenant"
git -C $B log -1 --format=%B
```

---

### Task 3: Permiso `messaging.quick_reply.manage`

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingPermissions.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` — rol `admin` (después de `MessagingPermissions.CampaignManage` en la lista, ~línea 884), `PermissionDefinition` (después de la de `CampaignManage`, ~línea 1353), política (después de la de `CampaignManage`, ~línea 1654)
- Modify: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs:169-195`

**Interfaces:**
- Consumes: nada nuevo.
- Produces: `MessagingPermissions.QuickReplyManage = "messaging.quick_reply.manage"`, con su definición de núcleo, su política y de fábrica sólo en `admin`.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `CompositionRootTests.cs`, reemplazar el `<summary>` y la prueba `PermissionDiscoveryFindsTheFiftyOneConstants` (líneas 169-178) por:

```csharp
    /// <summary>Ancla: sin esto, las dos de arriba pasarían por vacías. Son las 35 del spec de
    /// entitlements, las 6 de POS, las tres <c>operator.*</c> y las dos <c>integrations.*</c> de los
    /// specs de 2026-10-08, las dos <c>messaging.*</c> del spec 2026-10-09, las dos de Customers
    /// (<c>customers.tag.manage</c>, <c>customers.note.moderate</c>), <c>messaging.campaign.manage</c>
    /// del spec 2026-10-11 y <c>messaging.quick_reply.manage</c> del spec 2026-10-12.</summary>
    [Fact]
    public void PermissionDiscoveryFindsTheFiftyTwoConstants()
    {
        // 46 + los dos de Messaging (spec 2026-10-09 §6.3) + los dos de Customers (spec 2026-10-11 §4.3) + el de campañas (§6.4)
        // + el de respuestas rápidas (spec 2026-10-12 §3.5).
        Assert.Equal(52, PermissionConstants().Length);
    }
```

Y, después de `OnlyTheAdminRoleCarriesTheCampaignPermissionAsCore`:

```csharp
    /// <summary>Spec 2026-10-12 §3.5 y D-Q6: de núcleo (el módulo lo revisa cada handler) y de fábrica sólo en <c>admin</c>: lo que el
    /// asesor manda en nombre de la empresa lo decide quien administra.</summary>
    [Fact]
    public void OnlyTheAdminRoleCarriesTheQuickReplyPermissionAsCore()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();

        Assert.Equal("messaging.quick_reply.manage", MessagingPermissions.QuickReplyManage);
        Assert.Contains(MessagingPermissions.QuickReplyManage, catalog.PermissionsFor("admin"));
        Assert.DoesNotContain(MessagingPermissions.QuickReplyManage, catalog.PermissionsFor("advisor"));
        Assert.DoesNotContain(MessagingPermissions.QuickReplyManage, catalog.PermissionsFor("billing"));
        var definition = Assert.Single(catalog.ListPermissions(), candidate => candidate.Permission == MessagingPermissions.QuickReplyManage);
        Assert.Equal("Messaging", definition.Category);
        Assert.Empty(definition.RequiredModules!);
    }
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --filter "FullyQualifiedName~CompositionRootTests"
```

Esperado: `error CS0117`: `MessagingPermissions` no contiene una definición para `QuickReplyManage`.

- [ ] **Step 3: Implementar**

`MessagingPermissions.cs`, después de `CampaignManage`:

```csharp

    /// <summary>Spec 2026-10-12 §3.5 y D-Q6: crear, editar y borrar las respuestas rápidas del tenant. Leerlas no lo pide (basta
    /// <see cref="ConversationRead"/>). De núcleo y de fábrica sólo en <c>admin</c>, como <see cref="CampaignManage"/>.</summary>
    public const string QuickReplyManage = "messaging.quick_reply.manage";
```

`QepServiceCollectionExtensions.cs`, en la lista del rol `admin`, reemplazar:

```csharp
                // Spec 2026-10-11 §6.4 y decisión 16: las campañas llegan a miles de personas en nombre de la empresa
                // y el costo es por mensaje; de fábrica sólo en admin (no en advisor: P2 del spec).
                MessagingPermissions.CampaignManage
            ]));
```

por:

```csharp
                // Spec 2026-10-11 §6.4 y decisión 16: las campañas llegan a miles de personas en nombre de la empresa
                // y el costo es por mensaje; de fábrica sólo en admin (no en advisor: P2 del spec).
                MessagingPermissions.CampaignManage,
                // Spec 2026-10-12 D-Q6: lo que el asesor manda en nombre de la empresa lo decide quien administra.
                // El rol vive en código: sin migración.
                MessagingPermissions.QuickReplyManage
            ]));
```

Después de la `PermissionDefinition` de `MessagingPermissions.CampaignManage` (la que termina en `RequiredModules: []));` antes del cierre del método):

```csharp
        // Spec 2026-10-12 §3.5: núcleo como las demás de Messaging; el módulo lo revisa cada handler.
        services.AddSingleton(new PermissionDefinition(
            MessagingPermissions.QuickReplyManage,
            "Gestionar respuestas rápidas de WhatsApp",
            "Permite crear, editar y borrar los textos guardados que los asesores insertan en la bandeja de WhatsApp.",
            "Messaging",
            "medium",
            RequiredModules: []));
```

En `AddAuthorization`, reemplazar:

```csharp
            .AddPolicy(
                MessagingPermissions.CampaignManage,
                policy => AddPermissionRequirement(policy, MessagingPermissions.CampaignManage));
```

por:

```csharp
            .AddPolicy(
                MessagingPermissions.CampaignManage,
                policy => AddPermissionRequirement(policy, MessagingPermissions.CampaignManage))
            // Sin esta política, RequireAuthorization(QuickReplyManage) no resuelve y el síntoma es 500, no 403 (CLAUDE.md).
            .AddPolicy(
                MessagingPermissions.QuickReplyManage,
                policy => AddPermissionRequirement(policy, MessagingPermissions.QuickReplyManage));
```

- [ ] **Step 4: Correr y verificar que pasan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --filter "FullyQualifiedName~CompositionRootTests"
```

Esperado: todas las de `CompositionRootTests` verdes, incluidas `PermissionDiscoveryFindsTheFiftyTwoConstants`, `OnlyTheAdminRoleCarriesTheQuickReplyPermissionAsCore`, `EveryPermissionConstantDeclaresItsModules` y `EveryRegisteredPermissionStringIsUnique`.

- [ ] **Step 5: Commit**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
if ((git -C $B branch --show-current) -ne "feature/respuestas-rapidas") { throw "rama equivocada" }
git -C $B status --short
git -C $B add src/Modules/Messaging/Modules.Messaging.Application/MessagingPermissions.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs
git -C $B commit -m "feat(messaging): permiso para gestionar respuestas rápidas, de fábrica en admin"
git -C $B log -1 --format=%B
```

---

### Task 4: Puerto `IMessagingTenantNames`, su adaptador y los validadores de entrada

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingTenantNames.cs`
- Create: `src/Bootstrapper/MessagingTenantNames.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (después de `services.AddScoped<IMessagingMemberNames, MessagingMemberNames>();`, ~línea 667)
- Create: `tests/Bootstrapper/Bootstrapper.UnitTests/MessagingTenantNamesTests.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/QuickReplies.cs` (sólo la parte de validación: `IQuickReplyInput`, `QuickReplyMessages`, `QuickReplyInputValidator<T>`)
- Create: `tests/Modules/Messaging/Modules.Messaging.UnitTests/QuickReplyValidatorsTests.cs`

Nota: en esta tarea **no** se agregan comandos ni consultas. `CompositionRootTests.EveryCommandAndQueryHasItsHandlerRegistered` exige que cada uno tenga su handler registrado, así que cada comando nace en la misma tarea que su handler (Tasks 5 y 6). El validador base es genérico y se prueba con un input de prueba.

**Interfaces:**
- Consumes: `ITenantDirectory.GetDisplayNameAsync(TenantId, CancellationToken)` (existente, Tenancy), `QuickReply.NormalizeShortcut/IsValidShortcut/TrimBody/IsValidBodyLength/FindUnknownVariable` (Task 1).
- Produces:
  - `public interface IMessagingTenantNames { Task<string?> FindAsync(Guid tenantId, CancellationToken cancellationToken); }`
  - `internal sealed class MessagingTenantNames(ITenantDirectory tenantDirectory) : IMessagingTenantNames` (Bootstrapper).
  - `public interface IQuickReplyInput { string? Shortcut { get; } string? Body { get; } }`
  - `public static class QuickReplyMessages { const string Shortcut; const string Body; const string MissingVersion; static string UnknownVariable(string variable); }`
  - `public abstract class QuickReplyInputValidator<T> : AbstractValidator<T> where T : IQuickReplyInput` — claves `shortcut` y `body`.

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Bootstrapper/Bootstrapper.UnitTests/MessagingTenantNamesTests.cs`:

```csharp
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>Spec 2026-10-12 §3.3 (D-Q5): <c>variables.empresa</c> es el <c>DisplayName</c> del tenant, leído por
/// <see cref="ITenantDirectory"/> sin que Messaging conozca la persistencia de Tenancy. Sin fila, <c>null</c> (el handler lo vuelve <c>""</c>).</summary>
public sealed class MessagingTenantNamesTests
{
    [Fact]
    public async Task TheCompanyIsTheTenantsDisplayName()
    {
        var tenantId = Guid.CreateVersion7();
        var directory = new FakeTenantDirectory(new Dictionary<Guid, string> { [tenantId] = "Ritual Café" });

        var name = await new MessagingTenantNames(directory).FindAsync(tenantId, TestContext.Current.CancellationToken);

        Assert.Equal("Ritual Café", name);
    }

    [Fact]
    public async Task ATenantWithoutARowHasNoName()
    {
        var directory = new FakeTenantDirectory(new Dictionary<Guid, string>());

        Assert.Null(await new MessagingTenantNames(directory).FindAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken));
    }

    private sealed class FakeTenantDirectory(Dictionary<Guid, string> names) : ITenantDirectory
    {
        public Task<string?> GetDisplayNameAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(names.TryGetValue(tenantId.Value, out var name) ? name : null);

        public Task<string?> GetSlugAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetTimeZoneAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetDefaultCurrencyAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Guid?> GetLogoFileIdAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TenantStatus?> GetStatusAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TenantRegionalSettings?> GetRegionalSettingsAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
```

`tests/Modules/Messaging/Modules.Messaging.UnitTests/QuickReplyValidatorsTests.cs`:

```csharp
using FluentValidation.Results;
using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-12 §3.3: las reglas de formato del agregado, repetidas en el validador para que el 422
/// <c>validation.failed</c> traiga <c>errors.shortcut</c> y <c>errors.body</c>. Los mensajes llevan llaves literales
/// (<c>{nombre}</c>): se agregan sin pasar por el formateador de FluentValidation, que las trataría como placeholders.</summary>
public sealed class QuickReplyValidatorsTests
{
    private const string ShortcutMessage = "El atajo lleva de 2 a 32 letras minúsculas, números o guiones, sin «/» ni espacios.";
    private const string BodyMessage = "Escribe una respuesta de 1 a 4096 caracteres.";

    private sealed record Input(string? Shortcut, string? Body) : IQuickReplyInput;

    private sealed class InputValidator : QuickReplyInputValidator<Input>;

    private static ValidationResult Validate(string? shortcut, string? body) => new InputValidator().Validate(new Input(shortcut, body));

    private static string[] Keys(ValidationResult result) =>
        result.Errors.Select(failure => failure.PropertyName).Order(StringComparer.Ordinal).ToArray();

    [Theory]
    [InlineData("horarios")]
    [InlineData(" Horarios ")]
    [InlineData("pago-2")]
    public void AValidReplyPasses(string shortcut) =>
        Assert.True(Validate(shortcut, "Hola {nombre}, soy {asesor} de {empresa}.").IsValid);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("/horarios")]
    [InlineData("hola mundo")]
    [InlineData("hola_mundo")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")]
    public void AnInvalidShortcutFailsOnShortcut(string? shortcut)
    {
        var failure = Assert.Single(Validate(shortcut, "Hola").Errors);
        Assert.Equal(("shortcut", ShortcutMessage), (failure.PropertyName, failure.ErrorMessage));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("\n\t ")]
    public void AnEmptyBodyFailsOnBody(string? body)
    {
        var failure = Assert.Single(Validate("horarios", body).Errors);
        Assert.Equal(("body", BodyMessage), (failure.PropertyName, failure.ErrorMessage));
    }

    [Fact]
    public void FourThousandNinetySixCharactersAfterTrimPassAndOneMoreDoesNot()
    {
        Assert.True(Validate("largo", "  " + new string('a', 4096) + "  ").IsValid);
        var failure = Assert.Single(Validate("largo", new string('a', 4097)).Errors);
        Assert.Equal(("body", BodyMessage), (failure.PropertyName, failure.ErrorMessage));
    }

    // Review Focus 3: el mensaje nombra la variable y muestra las tres permitidas con sus llaves, sin que el formateador las coma.
    [Theory]
    [InlineData("Hola {Nombre}", "{Nombre}")]
    [InlineData("Hola { nombre }", "{ nombre }")]
    [InlineData("Hola {}", "{}")]
    [InlineData("Total {precio} para {nombre}", "{precio}")]
    public void AnUnknownVariableFailsOnBodyNamingIt(string body, string variable)
    {
        var failure = Assert.Single(Validate("saludo", body).Errors);
        Assert.Equal(("body", $"«{variable}» no es una variable. Usa {{nombre}}, {{asesor}} o {{empresa}}."), (failure.PropertyName, failure.ErrorMessage));
    }

    [Theory]
    [InlineData("Precio {10% off")]
    [InlineData("Cierra } antes")]
    [InlineData("{{nombre}}")]
    [InlineData("{nombre}{nombre}")]
    public void LooseBracesAreText(string body) => Assert.True(Validate("saludo", body).IsValid);

    [Fact]
    public void BothFieldsFailTogether() => Assert.Equal(["body", "shortcut"], Keys(Validate("/horarios", "   ")));
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Bootstrapper\Bootstrapper.UnitTests\Bootstrapper.UnitTests.csproj" --filter "FullyQualifiedName~MessagingTenantNamesTests"
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.UnitTests\Modules.Messaging.UnitTests.csproj" --filter "FullyQualifiedName~QuickReplyValidatorsTests"
```

Esperado: `error CS0246` sobre `MessagingTenantNames` en el primero; sobre `IQuickReplyInput` y `QuickReplyInputValidator<>` en el segundo.

- [ ] **Step 3: Implementar**

`src/Modules/Messaging/Modules.Messaging.Application/IMessagingTenantNames.cs`:

```csharp
namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-12 §3.3 (D-Q5): el nombre visible del tenant, para <c>variables.empresa</c> de las respuestas rápidas. Adaptador
/// en Bootstrapper sobre Tenancy; <c>null</c> si el tenant no tiene fila (el handler lo vuelve <c>""</c>).</summary>
public interface IMessagingTenantNames
{
    Task<string?> FindAsync(Guid tenantId, CancellationToken cancellationToken);
}
```

`src/Bootstrapper/MessagingTenantNames.cs`:

```csharp
using Modules.Messaging.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// <c>variables.empresa</c> de las respuestas rápidas (spec 2026-10-12 §3.3, D-Q5): <c>Tenant.DisplayName</c> por
/// <see cref="ITenantDirectory"/>, la misma lectura con la que Notifications nombra la organización en la invitación. Se lee en
/// cada <c>GET</c>: un tenant renombrado sale con el nombre vigente.
/// </summary>
internal sealed class MessagingTenantNames(ITenantDirectory tenantDirectory) : IMessagingTenantNames
{
    public Task<string?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        tenantDirectory.GetDisplayNameAsync(new TenantId(tenantId), cancellationToken);
}
```

`QepServiceCollectionExtensions.cs`, después de `services.AddScoped<IMessagingMemberNames, MessagingMemberNames>();`:

```csharp
        // Spec 2026-10-12 §3.3 (D-Q5): el nombre del tenant para las variables de las respuestas rápidas, por Tenancy.
        services.AddScoped<IMessagingTenantNames, MessagingTenantNames>();
```

`src/Modules/Messaging/Modules.Messaging.Application/QuickReplies.cs` (los comandos y los handlers se agregan en las Tasks 5 y 6):

```csharp
using FluentValidation;
using FluentValidation.Results;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

// Spec 2026-10-12 §3.3: las respuestas rápidas. Todo handler revalida tenant, permiso y módulo antes de tocar nada (403 con código,
// nunca 404 ante otro tenant) y valida después, así el 422 por campo no le cuenta a un extraño qué existe.

/// <summary>Lo que comparten crear y editar: el cuerpo de <c>SaveQuickReplyRequest</c> sin la versión.</summary>
public interface IQuickReplyInput
{
    string? Shortcut { get; }

    string? Body { get; }
}

/// <summary>Los textos de <c>errors</c>, tuteando. Fuera de <see cref="QuickReplyInputValidator{T}"/> porque un tipo genérico no
/// lleva miembros estáticos (CA1000).</summary>
public static class QuickReplyMessages
{
    public const string Shortcut = "El atajo lleva de 2 a 32 letras minúsculas, números o guiones, sin «/» ni espacios.";

    public const string Body = "Escribe una respuesta de 1 a 4096 caracteres.";

    public const string MissingVersion = "Falta la versión de la respuesta que estás viendo.";

    public static string UnknownVariable(string variable) => $"«{variable}» no es una variable. Usa {{nombre}}, {{asesor}} o {{empresa}}.";
}

/// <summary>
/// §3.3: las reglas de formato de <see cref="QuickReply"/> con la clave del cuerpo JSON (<c>shortcut</c>, <c>body</c>). Usa los mismos
/// métodos del agregado, así que no pueden divergir. Una variable desconocida también se marca acá: sin eso el 422 saldría con
/// <c>messaging.quick_reply.unknown_variable</c> y sin <c>errors</c>, y el formulario no sabría qué campo pintar.
/// </summary>
public abstract class QuickReplyInputValidator<T> : AbstractValidator<T>
    where T : IQuickReplyInput
{
    protected QuickReplyInputValidator()
    {
        RuleFor(input => input.Shortcut)
            .Must(shortcut => QuickReply.IsValidShortcut(QuickReply.NormalizeShortcut(shortcut)))
            .OverridePropertyName("shortcut").WithMessage(QuickReplyMessages.Shortcut);
        // Custom con ValidationFailure armado a mano: el mensaje lleva {nombre} literal, y WithMessage lo pasaría por el formateador
        // de FluentValidation, que trata {…} como placeholder.
        RuleFor(input => input.Body).Custom((body, context) =>
        {
            var trimmed = QuickReply.TrimBody(body);
            if (!QuickReply.IsValidBodyLength(trimmed))
            {
                context.AddFailure(new ValidationFailure("body", QuickReplyMessages.Body));
            }
            else if (QuickReply.FindUnknownVariable(trimmed) is { } unknown)
            {
                context.AddFailure(new ValidationFailure("body", QuickReplyMessages.UnknownVariable(unknown)));
            }
        });
    }
}
```

- [ ] **Step 4: Correr y verificar que pasan, más ArchitectureTests**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Bootstrapper\Bootstrapper.UnitTests\Bootstrapper.UnitTests.csproj" --no-build --filter "FullyQualifiedName~MessagingTenantNamesTests"
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.UnitTests\Modules.Messaging.UnitTests.csproj" --no-build --filter "FullyQualifiedName~QuickReplyValidatorsTests"
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --no-build --filter "FullyQualifiedName~MessagingLayerTests|FullyQualifiedName~CompositionRootTests"
```

Esperado: 2 verdes en `MessagingTenantNamesTests`, todas las de `QuickReplyValidatorsTests` verdes y las de arquitectura verdes (Application sigue sin referenciar nada fuera de Messaging, Tenancy y Audit; no hay comandos nuevos sin handler).

- [ ] **Step 5: Commit**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
if ((git -C $B branch --show-current) -ne "feature/respuestas-rapidas") { throw "rama equivocada" }
git -C $B status --short
git -C $B add src/Modules/Messaging/Modules.Messaging.Application/IMessagingTenantNames.cs src/Modules/Messaging/Modules.Messaging.Application/QuickReplies.cs src/Bootstrapper/MessagingTenantNames.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Bootstrapper/Bootstrapper.UnitTests/MessagingTenantNamesTests.cs tests/Modules/Messaging/Modules.Messaging.UnitTests/QuickReplyValidatorsTests.cs
git -C $B commit -m "feat(messaging): nombre del tenant por puerto propio y validación de respuestas rápidas"
git -C $B log -1 --format=%B
```

---

### Task 5: Listar y crear — handlers, auditoría, rutas `GET` y `POST`

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/QuickReplyDtos.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/QuickReplies.cs` (agregar al final)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingAuditRecorder.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingAuditRecorder.cs`
- Modify: `tests/Modules/Messaging/Modules.Messaging.UnitTests/CampaignTestDoubles.cs:76-84` (`RecordingAudit`)
- Create: `src/Modules/Messaging/Modules.Messaging.Api/QuickReplyEndpoints.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs:172`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (después de `services.AddScoped<ICommandHandler<CancelCampaignCommand, CampaignDetailDto>, CancelCampaignHandler>();`, ~línea 596)
- Modify: `tests/Modules/Messaging/Modules.Messaging.UnitTests/QuickReplyValidatorsTests.cs` (una prueba más)
- Create: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/QuickReplyApiTests.cs`

**Interfaces:**
- Consumes: `IQuickReplyRepository` (Task 2), `IMessagingTenantNames` (Task 4), `QuickReplyInputValidator<T>` (Task 4), `MessagingPermissions.QuickReplyManage` (Task 3), `CallerMembership.FindAsync(Guid, CancellationToken)`, `IMessagingMemberNames.FindAsync(Guid, IReadOnlyCollection<Guid>, CancellationToken)`, `MessagingAuthorization.EnsureAsync(...)` (existentes).
- Produces:
  - `QuickReplyDto`, `QuickReplyVariablesDto`, `QuickReplyListDto` (Global Constraints).
  - `public sealed record ListQuickRepliesQuery(Guid TenantId) : IQuery<QuickReplyListDto>;`
  - `public sealed record CreateQuickReplyCommand(Guid TenantId, string? Shortcut, string? Body) : ICommand<QuickReplyDto>, IQuickReplyInput;`
  - `public sealed class CreateQuickReplyValidator : QuickReplyInputValidator<CreateQuickReplyCommand>;`
  - `ListQuickRepliesHandler`, `CreateQuickReplyHandler`.
  - `void IMessagingAuditRecorder.RecordQuickReply(Guid tenantId, Guid actorId, string action, Guid quickReplyId, DateTimeOffset occurredAt);` y `MessagingAuditActions.QuickReplyResourceType = "quick_reply"`, `QuickReplyCreated`, `QuickReplyUpdated`, `QuickReplyDeleted`.
  - `public sealed record SaveQuickReplyRequest(string? Shortcut, string? Body, long? Version);` (Api).

- [ ] **Step 1: Escribir las pruebas que fallan**

En `QuickReplyValidatorsTests.cs`, al final de la clase:

```csharp
    [Fact]
    public void TheCreateValidatorUsesTheSharedRules() =>
        Assert.Equal(["body", "shortcut"], Keys(new CreateQuickReplyValidator().Validate(new CreateQuickReplyCommand(Guid.CreateVersion7(), "/x", ""))));
```

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/QuickReplyApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-12 §3.6 y §3.7 por HTTP y contra la base: crear, listar (con <c>variables</c> resueltas para quien llama),
/// editar y borrar, con sus 403 (sin permiso, otro tenant, módulo apagado), 404, 412, 422 por campo, <c>shortcut_taken</c> y la
/// auditoría en la misma transacción.</summary>
public sealed class QuickReplyApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] AdminPermissions = [MessagingPermissions.ConversationRead, MessagingPermissions.QuickReplyManage];

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, HttpClient Admin);

    private static string QuickRepliesUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/messaging/quick-replies";

    private static string QuickReplyUrl(Guid tenantId, Guid quickReplyId) => $"{QuickRepliesUrl(tenantId)}/{quickReplyId}";

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        return new Fixture(factory, connectionString, tenant, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, AdminPermissions));
    }

    private static async Task<JsonElement> CreateAsync(Fixture f, string shortcut, string body = "Abrimos de 8 a 6.")
    {
        var response = await SendAsync(f.Admin, HttpMethod.Post, QuickRepliesUrl(f.Tenant.TenantId), new { shortcut, body });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static Task<long> AuditCountAsync(Fixture f, string action, Guid quickReplyId) =>
        CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE source = 'messaging' AND action = @action AND resource_type = 'quick_reply' AND resource_id = @id",
            ("action", action), ("id", quickReplyId.ToString()));

    [Fact]
    public async Task CreatingStoresTheNormalizedReplyAndItsAudit()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var created = await CreateAsync(f, " Horarios ", "  Abrimos de 8 a 6, {nombre}.  ");

        Assert.Equal(("horarios", "Abrimos de 8 a 6, {nombre}.", 1L), (
            created.GetProperty("shortcut").GetString(), created.GetProperty("body").GetString(), created.GetProperty("version").GetInt64()));
        Assert.NotEqual(default, created.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.Equal(1L, await AuditCountAsync(f, "messaging.quick_reply.created", created.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task TheListIsOrderedByShortcutAndCarriesTheCallersVariables()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await CreateAsync(f, "pago");
        await CreateAsync(f, "horarios");
        await CreateAsync(f, "a-domicilio");
        var (_, advisorUserId) = await SeedMemberAsync(f.ConnectionString, f.Tenant.TenantId, "Andrea Gómez", "advisor");
        using var advisor = CreateClient(f.Factory, advisorUserId, f.Tenant.TenantId, ReadPermissions);

        var list = await advisor.GetFromJsonAsync<JsonElement>(QuickRepliesUrl(f.Tenant.TenantId), Ct);

        Assert.Equal(["a-domicilio", "horarios", "pago"], list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("shortcut").GetString()));
        var variables = list.GetProperty("variables");
        Assert.Equal(("Andrea Gómez", "Messaging Test Org"), (variables.GetProperty("asesor").GetString(), variables.GetProperty("empresa").GetString()));
    }

    [Fact]
    public async Task ACallerWithoutMembershipGetsAnEmptyAdvisorNotNull()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        using var stranger = CreateClient(f.Factory, Guid.CreateVersion7(), f.Tenant.TenantId, ReadPermissions);

        var list = await stranger.GetFromJsonAsync<JsonElement>(QuickRepliesUrl(f.Tenant.TenantId), Ct);

        Assert.Equal(0, list.GetProperty("items").GetArrayLength());
        Assert.Equal(string.Empty, list.GetProperty("variables").GetProperty("asesor").GetString());
        Assert.Equal("Messaging Test Org", list.GetProperty("variables").GetProperty("empresa").GetString());
    }

    [Fact]
    public async Task CreatingWithoutThePermissionOrFromAnotherTenantIs403AndWritesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        using var advisor = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);
        using var intruder = CreateClient(f.Factory, f.Tenant.OwnerUserId, Guid.CreateVersion7(), AdminPermissions);
        var url = QuickRepliesUrl(f.Tenant.TenantId);

        var denied = await SendAsync(advisor, HttpMethod.Post, url, new { shortcut = "horarios", body = "Hola" });
        var crossRead = await SendAsync(intruder, HttpMethod.Get, url);
        var crossCreate = await SendAsync(intruder, HttpMethod.Post, url, new { shortcut = "horarios", body = "Hola" });

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossRead.StatusCode);
        Assert.Equal("authorization.denied", (await ProblemAsync(crossRead)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, crossCreate.StatusCode);
        Assert.Equal("authorization.denied", (await ProblemAsync(crossCreate)).Code);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.quick_replies"));
    }

    [Fact]
    public async Task WithTheModuleOffEveryRouteIs403WithItsCode()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await ExecuteAsync(f.ConnectionString,
            "UPDATE tenancy.tenant_modules SET status = 'inactive' WHERE tenant_id = @t AND module_key = 'messaging'", ("t", f.Tenant.TenantId));
        var url = QuickRepliesUrl(f.Tenant.TenantId);

        var read = await SendAsync(f.Admin, HttpMethod.Get, url);
        var create = await SendAsync(f.Admin, HttpMethod.Post, url, new { shortcut = "horarios", body = "Hola" });

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(read)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(create)).Code);
    }

    // Review Focus 1: el atajo se compara normalizado; el índice es por tenant.
    [Fact]
    public async Task ATakenShortcutIs422EvenInAnotherCaseAndAnotherTenantCanUseIt()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await CreateAsync(f, "horarios");
        var other = await RegisterTenantAsync(f.Factory);
        await EnableMessagingAsync(f.ConnectionString, other.TenantId);
        using var otherAdmin = CreateClient(f.Factory, other.OwnerUserId, other.TenantId, AdminPermissions);

        var taken = await SendAsync(f.Admin, HttpMethod.Post, QuickRepliesUrl(f.Tenant.TenantId), new { shortcut = "HORARIOS", body = "Otro" });
        var elsewhere = await SendAsync(otherAdmin, HttpMethod.Post, QuickRepliesUrl(other.TenantId), new { shortcut = "horarios", body = "Otro" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, taken.StatusCode);
        Assert.Equal("messaging.quick_reply.shortcut_taken", (await ProblemAsync(taken)).Code);
        Assert.Equal(HttpStatusCode.Created, elsewhere.StatusCode);
    }

    [Fact]
    public async Task AnInvalidInputIs422WithErrorsByField()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var url = QuickRepliesUrl(f.Tenant.TenantId);

        var both = await SendAsync(f.Admin, HttpMethod.Post, url, new { shortcut = "/horarios", body = "   " });
        var variable = await SendAsync(f.Admin, HttpMethod.Post, url, new { shortcut = "saludo", body = "Hola {Nombre}" });
        var empty = await SendAsync(f.Admin, HttpMethod.Post, url, new { });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, both.StatusCode);
        var (bothCode, bothKeys) = await ProblemAsync(both);
        Assert.Equal("validation.failed", bothCode);
        Assert.Equal(["body", "shortcut"], bothKeys);
        var (variableCode, variableKeys) = await ProblemAsync(variable);
        Assert.Equal("validation.failed", variableCode);
        Assert.Equal(["body"], variableKeys);
        Assert.Equal(["body", "shortcut"], (await ProblemAsync(empty)).ErrorKeys);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.quick_replies"));
    }
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.UnitTests\Modules.Messaging.UnitTests.csproj" --filter "FullyQualifiedName~QuickReplyValidatorsTests"
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.IntegrationTests\Modules.Messaging.IntegrationTests.csproj" --filter "FullyQualifiedName~QuickReplyApiTests"
```

Esperado: en UnitTests, `error CS0246` sobre `CreateQuickReplyValidator` y `CreateQuickReplyCommand`. En IntegrationTests (compila: sólo usa HTTP y `MessagingPermissions`), las 7 fallan con `Assert.Equal() Failure` por `NotFound` donde se esperaba `Created`, `OK`, `Forbidden` o `UnprocessableEntity`: la ruta no existe.

- [ ] **Step 3: Implementar DTOs, consulta, comando, validador y handlers**

`src/Modules/Messaging/Modules.Messaging.Application/QuickReplyDtos.cs`:

```csharp
namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-12 §3.6: una respuesta rápida. <c>shortcut</c> viaja sin <c>/</c>; la pantalla lo dibuja con la barra.</summary>
public sealed record QuickReplyDto(Guid Id, string Shortcut, string Body, DateTimeOffset UpdatedAt, long Version);

/// <summary>BFF (D-Q5): la SPA no conoce su nombre de asesor (<c>/auth/me</c> sólo da <c>userId</c>, mismo motivo que
/// <c>AssignedToDto.IsMe</c>) ni tiene garantizado el del tenant en la caja. Sin nombre viajan <c>""</c>, nunca <c>null</c>: el
/// compositor reemplaza la variable por vacío y el asesor lo corrige antes de enviar.</summary>
public sealed record QuickReplyVariablesDto(string Asesor, string Empresa);

/// <summary>D-Q7: la lista completa, sin paginar y por <c>shortcut</c>: el compositor filtra en memoria mientras se escribe.</summary>
public sealed record QuickReplyListDto(IReadOnlyList<QuickReplyDto> Items, QuickReplyVariablesDto Variables);
```

`QuickReplies.cs`: reemplazar las tres líneas de `using` del principio por:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;
```

y agregar al final del archivo:

```csharp
public sealed record ListQuickRepliesQuery(Guid TenantId) : IQuery<QuickReplyListDto>;

public sealed record CreateQuickReplyCommand(Guid TenantId, string? Shortcut, string? Body) : ICommand<QuickReplyDto>, IQuickReplyInput;

public sealed class CreateQuickReplyValidator : QuickReplyInputValidator<CreateQuickReplyCommand>;

internal static class QuickReplySupport
{
    public static QuickReplyDto ToDto(QuickReply quickReply) =>
        new(quickReply.Id, quickReply.Shortcut, quickReply.Body, quickReply.UpdatedAt, quickReply.Version);
}

/// <summary>§3.6 <c>GET /quick-replies</c>: todo asesor de la caja (<c>messaging.conversation.read</c>). Las variables se resuelven
/// para quien llama (D-Q5): su nombre de asesor por <see cref="CallerMembership"/> + <see cref="IMessagingMemberNames"/> y el del
/// tenant por <see cref="IMessagingTenantNames"/>; sin nombre, <c>""</c>.</summary>
public sealed class ListQuickRepliesHandler(
    IQuickReplyRepository repository,
    CallerMembership callerMembership,
    IMessagingMemberNames memberNames,
    IMessagingTenantNames tenantNames,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : IQueryHandler<ListQuickRepliesQuery, QuickReplyListDto>
{
    public async Task<QuickReplyListDto> HandleAsync(ListQuickRepliesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.ConversationRead, cancellationToken);
        var items = await repository.ListAsync(query.TenantId, cancellationToken);
        var asesor = string.Empty;
        if (await callerMembership.FindAsync(query.TenantId, cancellationToken) is { } memberId)
        {
            var names = await memberNames.FindAsync(query.TenantId, [memberId], cancellationToken);
            asesor = names.GetValueOrDefault(memberId, string.Empty);
        }

        var empresa = await tenantNames.FindAsync(query.TenantId, cancellationToken) ?? string.Empty;
        return new QuickReplyListDto(items.Select(QuickReplySupport.ToDto).ToArray(), new QuickReplyVariablesDto(asesor, empresa));
    }
}

/// <summary>§3.6 <c>POST /quick-replies</c>: con <c>messaging.quick_reply.manage</c>. El atajo repetido lo traduce la unidad de trabajo
/// (422 <c>shortcut_taken</c>); la auditoría viaja en el mismo <c>SaveChanges</c>.</summary>
public sealed class CreateQuickReplyHandler(
    IQuickReplyRepository repository,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CreateQuickReplyCommand> validator)
    : ICommandHandler<CreateQuickReplyCommand, QuickReplyDto>
{
    public async Task<QuickReplyDto> HandleAsync(CreateQuickReplyCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.QuickReplyManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var now = clock.UtcNow;
        var quickReply = QuickReply.Create(command.TenantId, command.Shortcut, command.Body, now);
        repository.Add(quickReply);
        audit.RecordQuickReply(command.TenantId, executionContext.SubjectId, MessagingAuditActions.QuickReplyCreated, quickReply.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return QuickReplySupport.ToDto(quickReply);
    }
}
```

(Si el build se queja de `using FluentValidation.Results` sin uso en algún momento, no es este caso: lo usa `QuickReplyInputValidator<T>`.)

- [ ] **Step 4: Auditoría**

`IMessagingAuditRecorder.cs`, en la interfaz, después de `RecordCampaign`:

```csharp

    /// <summary>Spec 2026-10-12 §3.3: crear, editar y borrar una respuesta rápida, en la misma transacción que el cambio.</summary>
    void RecordQuickReply(Guid tenantId, Guid actorId, string action, Guid quickReplyId, DateTimeOffset occurredAt);
```

y en `MessagingAuditActions`, al final:

```csharp

    public const string QuickReplyResourceType = "quick_reply";
    public const string QuickReplyCreated = "messaging.quick_reply.created";
    public const string QuickReplyUpdated = "messaging.quick_reply.updated";
    public const string QuickReplyDeleted = "messaging.quick_reply.deleted";
```

`MessagingAuditRecorder.cs`, después de `RecordCampaign`:

```csharp

    // Spec 2026-10-12 §3.3: siempre una persona (quien gestiona en Settings). changed_fields vacío, como el resto del módulo.
    public void RecordQuickReply(Guid tenantId, Guid actorId, string action, Guid quickReplyId, DateTimeOffset occurredAt) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            actorId,
            AuditActorType.Human,
            action,
            MessagingAuditActions.QuickReplyResourceType,
            quickReplyId.ToString(),
            Outcome,
            NoChangedFields,
            Source,
            occurredAt));
```

`CampaignTestDoubles.cs`, en `RecordingAudit`, después de `RecordCampaign`:

```csharp

    public void RecordQuickReply(Guid tenantId, Guid actorId, string action, Guid quickReplyId, DateTimeOffset occurredAt) =>
        throw new NotSupportedException();
```

- [ ] **Step 5: Rutas y registro**

`src/Modules/Messaging/Modules.Messaging.Api/QuickReplyEndpoints.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Messaging.Application;

namespace Modules.Messaging.Api;

/// <summary>
/// Spec 2026-10-12 §3.6: las respuestas rápidas, bajo <c>/api/v1/tenants/{tenantId:guid}/messaging/quick-replies</c>. Leer con
/// <c>messaging.conversation.read</c> (todo asesor de la caja); gestionar con <c>messaging.quick_reply.manage</c>. Los handlers
/// revalidan tenant, permiso y módulo (403 con código, nunca 404 ante otro tenant).
/// </summary>
internal static class QuickReplyEndpoints
{
    public static void MapQuickReplyEndpoints(this RouteGroupBuilder messaging)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        var group = messaging.MapGroup("/quick-replies");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(MessagingPermissions.ConversationRead)
            .Produces<QuickReplyListDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/", CreateAsync)
            .RequireAuthorization(MessagingPermissions.QuickReplyManage)
            .Accepts<SaveQuickReplyRequest>("application/json")
            .Produces<QuickReplyDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> ListAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListQuickRepliesQuery(tenantId), cancellationToken));

    // Sin Location: no hay GET de una sola respuesta; la pantalla repinta la lista con lo que vuelve.
    private static async Task<IResult> CreateAsync(
        Guid tenantId, SaveQuickReplyRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Created(
            (string?)null,
            await dispatcher.SendAsync(new CreateQuickReplyCommand(tenantId, request.Shortcut, request.Body), cancellationToken));
}

/// <summary>§3.6: el cuerpo de crear y editar. <c>shortcut</c> sin <c>/</c>. El <c>POST</c> ignora <c>version</c>; el <c>PUT</c> la
/// exige. Todo opcional para que lo que falte salga 422 con su clave en <c>errors</c>, no 400 del binder.</summary>
public sealed record SaveQuickReplyRequest(string? Shortcut, string? Body, long? Version);
```

`MessagingEndpoints.cs:172`, después de `group.MapCampaignEndpoints();`:

```csharp
        // Spec 2026-10-12 §3.6: respuestas rápidas.
        group.MapQuickReplyEndpoints();
```

`QepServiceCollectionExtensions.cs`, después de `services.AddScoped<ICommandHandler<CancelCampaignCommand, CampaignDetailDto>, CancelCampaignHandler>();`:

```csharp
        // Messaging (spec 2026-10-12 §3.3): respuestas rápidas. Cada handler a mano: sin registro, 500.
        services.AddScoped<IQueryHandler<ListQuickRepliesQuery, QuickReplyListDto>, ListQuickRepliesHandler>();
        services.AddScoped<ICommandHandler<CreateQuickReplyCommand, QuickReplyDto>, CreateQuickReplyHandler>();
```

(`CreateQuickReplyValidator` es pública: la registra `AddValidatorsFromAssemblyContaining<ListConversationsValidator>()`.)

- [ ] **Step 6: Correr y verificar que pasan, más ArchitectureTests**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.UnitTests\Modules.Messaging.UnitTests.csproj" --no-build --filter "FullyQualifiedName~QuickReplyValidatorsTests|FullyQualifiedName~CampaignHandlersTests"
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.IntegrationTests\Modules.Messaging.IntegrationTests.csproj" --no-build --filter "FullyQualifiedName~QuickReplyApiTests"
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --no-build --filter "FullyQualifiedName~MessagingLayerTests|FullyQualifiedName~CompositionRootTests"
```

Esperado: todo verde, incluidas las 7 de `QuickReplyApiTests` y `EveryCommandAndQueryHasItsHandlerRegistered`. Si `TheListIsOrderedByShortcutAndCarriesTheCallersVariables` da otro `asesor`, revisar que `SeedMemberAsync` dejó la membresía `Active` y que el cliente usa su `userId` (no el del dueño); no se toca la prueba.

- [ ] **Step 7: Commit**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
if ((git -C $B branch --show-current) -ne "feature/respuestas-rapidas") { throw "rama equivocada" }
git -C $B status --short
git -C $B add src/Modules/Messaging/Modules.Messaging.Application/QuickReplyDtos.cs src/Modules/Messaging/Modules.Messaging.Application/QuickReplies.cs src/Modules/Messaging/Modules.Messaging.Application/IMessagingAuditRecorder.cs src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingAuditRecorder.cs src/Modules/Messaging/Modules.Messaging.Api/QuickReplyEndpoints.cs src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Messaging/Modules.Messaging.UnitTests/CampaignTestDoubles.cs tests/Modules/Messaging/Modules.Messaging.UnitTests/QuickReplyValidatorsTests.cs tests/Modules/Messaging/Modules.Messaging.IntegrationTests/QuickReplyApiTests.cs
git -C $B commit -m "feat(messaging): listar y crear respuestas rápidas con variables de quien llama"
git -C $B log -1 --format=%B
```

---

### Task 6: Editar y borrar — rutas `PUT` y `DELETE`, y README

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/QuickReplies.cs` (agregar al final; `QuickReplySupport` gana dos métodos)
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/QuickReplyEndpoints.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (debajo de las dos líneas de la Task 5)
- Modify: `README.md:797` (tabla de rutas) y § «Mensajería (WhatsApp Cloud)» (antes de **Búsqueda.**, ~línea 1921)
- Modify: `tests/Modules/Messaging/Modules.Messaging.UnitTests/QuickReplyValidatorsTests.cs`
- Modify: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/QuickReplyApiTests.cs`

**Interfaces:**
- Consumes: todo lo de las Tasks 1–5.
- Produces:
  - `public sealed record UpdateQuickReplyCommand(Guid TenantId, Guid QuickReplyId, string? Shortcut, string? Body, long? Version) : ICommand<QuickReplyDto>, IQuickReplyInput;`
  - `public sealed record DeleteQuickReplyCommand(Guid TenantId, Guid QuickReplyId, long? Version) : ICommand<bool>;`
  - `UpdateQuickReplyValidator`, `DeleteQuickReplyValidator` (clave `version`, `version > 0`).
  - `UpdateQuickReplyHandler`, `DeleteQuickReplyHandler`.
  - `QuickReplySupport.NotFound(Guid)` → `ResourceNotFoundException(messaging.quick_reply.not_found)`; `QuickReplySupport.Stale()` → `RequestConcurrencyException(concurrency.conflict)`.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `QuickReplyValidatorsTests.cs`, al final de la clase:

```csharp
    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void UpdateAndDeleteNeedAPositiveVersion(long? version)
    {
        var update = Assert.Single(new UpdateQuickReplyValidator().Validate(
            new UpdateQuickReplyCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), "horarios", "Hola", version)).Errors);
        var delete = Assert.Single(new DeleteQuickReplyValidator().Validate(
            new DeleteQuickReplyCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), version)).Errors);

        Assert.Equal(("version", "Falta la versión de la respuesta que estás viendo."), (update.PropertyName, update.ErrorMessage));
        Assert.Equal(("version", "Falta la versión de la respuesta que estás viendo."), (delete.PropertyName, delete.ErrorMessage));
    }

    [Fact]
    public void TheUpdateValidatorUsesTheSharedRules() =>
        Assert.Equal(["body", "shortcut"], Keys(new UpdateQuickReplyValidator().Validate(
            new UpdateQuickReplyCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), "a", "Hola {cliente}", 1))));
```

En `QuickReplyApiTests.cs`, al final de la clase:

```csharp
    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid tenantId, Guid id, string shortcut, string body, long? version) =>
        SendAsync(client, HttpMethod.Put, QuickReplyUrl(tenantId, id), new { shortcut, body, version });

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid tenantId, Guid id, long? version) =>
        SendAsync(client, HttpMethod.Delete, version is null ? QuickReplyUrl(tenantId, id) : $"{QuickReplyUrl(tenantId, id)}?version={version}");

    // Review Focus 4: el mismo atajo que ya tiene no choca con su propia fila.
    [Fact]
    public async Task UpdatingKeepsItsOwnShortcutBumpsTheVersionAndAudits()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var id = (await CreateAsync(f, "horarios")).GetProperty("id").GetGuid();

        var response = await PutAsync(f.Admin, f.Tenant.TenantId, id, "horarios", "  Abrimos de 7 a 7, {nombre}.  ", 1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal((id, "horarios", "Abrimos de 7 a 7, {nombre}.", 2L), (
            updated.GetProperty("id").GetGuid(), updated.GetProperty("shortcut").GetString(), updated.GetProperty("body").GetString(), updated.GetProperty("version").GetInt64()));
        Assert.Equal(1L, await AuditCountAsync(f, "messaging.quick_reply.updated", id));
    }

    [Fact]
    public async Task RenamingToATakenShortcutIs422()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await CreateAsync(f, "horarios");
        var id = (await CreateAsync(f, "pago")).GetProperty("id").GetGuid();

        var taken = await PutAsync(f.Admin, f.Tenant.TenantId, id, "Horarios", "Otro", 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, taken.StatusCode);
        Assert.Equal("messaging.quick_reply.shortcut_taken", (await ProblemAsync(taken)).Code);
        Assert.Equal("pago", await ScalarAsync<string>(f.ConnectionString, "SELECT shortcut FROM messaging.quick_replies WHERE id = @id", ("id", id)));
    }

    // Review Focus 5: la versión vieja no edita ni borra.
    [Fact]
    public async Task AStaleVersionIs412OnUpdateAndOnDeleteAndNothingChanges()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var id = (await CreateAsync(f, "horarios")).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(f.Admin, f.Tenant.TenantId, id, "horarios", "Versión dos.", 1)).StatusCode);

        var staleUpdate = await PutAsync(f.Admin, f.Tenant.TenantId, id, "horarios", "Pisada.", 1);
        var staleDelete = await DeleteAsync(f.Admin, f.Tenant.TenantId, id, 1);

        Assert.Equal(HttpStatusCode.PreconditionFailed, staleUpdate.StatusCode);
        Assert.Equal("concurrency.conflict", (await ProblemAsync(staleUpdate)).Code);
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleDelete.StatusCode);
        Assert.Equal("concurrency.conflict", (await ProblemAsync(staleDelete)).Code);
        Assert.Equal("Versión dos.", await ScalarAsync<string>(f.ConnectionString, "SELECT body FROM messaging.quick_replies WHERE id = @id", ("id", id)));
        Assert.Equal(2L, await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.quick_replies WHERE id = @id", ("id", id)));
    }

    [Fact]
    public async Task AMissingVersionIs422OnVersion()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var id = (await CreateAsync(f, "horarios")).GetProperty("id").GetGuid();

        var update = await PutAsync(f.Admin, f.Tenant.TenantId, id, "horarios", "Otro", null);
        var delete = await DeleteAsync(f.Admin, f.Tenant.TenantId, id, null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, update.StatusCode);
        Assert.Equal(["version"], (await ProblemAsync(update)).ErrorKeys);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, delete.StatusCode);
        Assert.Equal(["version"], (await ProblemAsync(delete)).ErrorKeys);
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.quick_replies"));
    }

    [Fact]
    public async Task DeletingRemovesItAndAudits()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var id = (await CreateAsync(f, "horarios")).GetProperty("id").GetGuid();

        var deleted = await DeleteAsync(f.Admin, f.Tenant.TenantId, id, 1);
        var list = await f.Admin.GetFromJsonAsync<JsonElement>(QuickRepliesUrl(f.Tenant.TenantId), Ct);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, list.GetProperty("items").GetArrayLength());
        Assert.Equal(1L, await AuditCountAsync(f, "messaging.quick_reply.deleted", id));
    }

    // Review Focus 5: el id de otro tenant bajo la ruta propia es 404, igual que uno inexistente; la ruta ajena es 403.
    [Fact]
    public async Task AnUnknownOrForeignIdIs404AndAnotherTenantsRouteIs403()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var id = (await CreateAsync(f, "horarios")).GetProperty("id").GetGuid();
        var other = await RegisterTenantAsync(f.Factory);
        await EnableMessagingAsync(f.ConnectionString, other.TenantId);
        using var otherAdmin = CreateClient(f.Factory, other.OwnerUserId, other.TenantId, AdminPermissions);
        var unknown = Guid.CreateVersion7();

        var notFound = new[]
        {
            await PutAsync(f.Admin, f.Tenant.TenantId, unknown, "x-y", "Hola", 1),
            await DeleteAsync(f.Admin, f.Tenant.TenantId, unknown, 1),
            await PutAsync(otherAdmin, other.TenantId, id, "x-y", "Hola", 1),
            await DeleteAsync(otherAdmin, other.TenantId, id, 1),
        };
        var forbidden = new[]
        {
            await PutAsync(otherAdmin, f.Tenant.TenantId, id, "x-y", "Hola", 1),
            await DeleteAsync(otherAdmin, f.Tenant.TenantId, id, 1),
        };

        foreach (var response in notFound)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("messaging.quick_reply.not_found", (await ProblemAsync(response)).Code);
        }

        foreach (var response in forbidden)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("authorization.denied", (await ProblemAsync(response)).Code);
        }

        Assert.Equal(("horarios", 1L), (
            await ScalarAsync<string>(f.ConnectionString, "SELECT shortcut FROM messaging.quick_replies WHERE id = @id", ("id", id)),
            await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.quick_replies WHERE id = @id", ("id", id))));
    }

    [Fact]
    public async Task UpdatingOrDeletingWithoutThePermissionIs403()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var id = (await CreateAsync(f, "horarios")).GetProperty("id").GetGuid();
        using var advisor = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);

        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(advisor, f.Tenant.TenantId, id, "horarios", "Otro", 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteAsync(advisor, f.Tenant.TenantId, id, 1)).StatusCode);
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.quick_replies WHERE version = 1"));
    }
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.UnitTests\Modules.Messaging.UnitTests.csproj" --filter "FullyQualifiedName~QuickReplyValidatorsTests"
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.IntegrationTests\Modules.Messaging.IntegrationTests.csproj" --filter "FullyQualifiedName~QuickReplyApiTests"
```

Esperado: en UnitTests, `error CS0246` sobre `UpdateQuickReplyValidator`, `UpdateQuickReplyCommand`, `DeleteQuickReplyValidator` y `DeleteQuickReplyCommand`. En IntegrationTests, las 7 nuevas fallan con `Assert.Equal() Failure`: `PUT`/`DELETE` sobre `/quick-replies/{id}` no tienen ruta (404 o 405 donde se esperaba `OK`, `NoContent`, `PreconditionFailed`, `UnprocessableEntity` o `Forbidden`); las 7 de la Task 5 siguen verdes.

- [ ] **Step 3: Implementar comandos, validadores y handlers**

`QuickReplies.cs`: dentro de `QuickReplySupport`, debajo de `ToDto`:

```csharp

    public static ResourceNotFoundException NotFound(Guid quickReplyId) =>
        new(MessagingErrorCodes.QuickReplyNotFound, $"Quick reply '{quickReplyId}' was not found.");

    public static RequestConcurrencyException Stale() =>
        new("concurrency.conflict", "The quick reply changed since it was loaded.");
```

y al final del archivo:

```csharp
public sealed record UpdateQuickReplyCommand(Guid TenantId, Guid QuickReplyId, string? Shortcut, string? Body, long? Version)
    : ICommand<QuickReplyDto>, IQuickReplyInput;

public sealed record DeleteQuickReplyCommand(Guid TenantId, Guid QuickReplyId, long? Version) : ICommand<bool>;

public sealed class UpdateQuickReplyValidator : QuickReplyInputValidator<UpdateQuickReplyCommand>
{
    public UpdateQuickReplyValidator() =>
        RuleFor(command => command.Version).Must(version => version is > 0)
            .OverridePropertyName("version").WithMessage(QuickReplyMessages.MissingVersion);
}

/// <summary>§3.6: la versión viaja en el query (<c>?version=</c>), un <c>DELETE</c> no lleva cuerpo.</summary>
public sealed class DeleteQuickReplyValidator : AbstractValidator<DeleteQuickReplyCommand>
{
    public DeleteQuickReplyValidator() =>
        RuleFor(command => command.Version).Must(version => version is > 0)
            .OverridePropertyName("version").WithMessage(QuickReplyMessages.MissingVersion);
}

/// <summary>§3.6 <c>PUT /quick-replies/{id}</c>: reemplaza atajo y cuerpo con la versión que se está viendo (412 si no). Un id que no
/// es del tenant es 404, igual que uno inexistente.</summary>
public sealed class UpdateQuickReplyHandler(
    IQuickReplyRepository repository,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateQuickReplyCommand> validator)
    : ICommandHandler<UpdateQuickReplyCommand, QuickReplyDto>
{
    public async Task<QuickReplyDto> HandleAsync(UpdateQuickReplyCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.QuickReplyManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var quickReply = await repository.FindAsync(command.TenantId, command.QuickReplyId, cancellationToken)
            ?? throw QuickReplySupport.NotFound(command.QuickReplyId);
        if (quickReply.Version != command.Version)
        {
            throw QuickReplySupport.Stale();
        }

        var now = clock.UtcNow;
        quickReply.Update(command.Shortcut, command.Body, now);
        audit.RecordQuickReply(command.TenantId, executionContext.SubjectId, MessagingAuditActions.QuickReplyUpdated, quickReply.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return QuickReplySupport.ToDto(quickReply);
    }
}

/// <summary>§3.6 <c>DELETE /quick-replies/{id}?version=</c>: con la versión que se está viendo (412 si no; una carrera entre la
/// lectura y el commit también es 412, por el token de concurrencia).</summary>
public sealed class DeleteQuickReplyHandler(
    IQuickReplyRepository repository,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<DeleteQuickReplyCommand> validator)
    : ICommandHandler<DeleteQuickReplyCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteQuickReplyCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.QuickReplyManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var quickReply = await repository.FindAsync(command.TenantId, command.QuickReplyId, cancellationToken)
            ?? throw QuickReplySupport.NotFound(command.QuickReplyId);
        if (quickReply.Version != command.Version)
        {
            throw QuickReplySupport.Stale();
        }

        repository.Remove(quickReply);
        audit.RecordQuickReply(command.TenantId, executionContext.SubjectId, MessagingAuditActions.QuickReplyDeleted, quickReply.Id, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
```

- [ ] **Step 4: Rutas y registro**

`QuickReplyEndpoints.cs`, en `MapQuickReplyEndpoints`, después del `MapPost`:

```csharp

        group.MapPut("/{quickReplyId:guid}", UpdateAsync)
            .RequireAuthorization(MessagingPermissions.QuickReplyManage)
            .Accepts<SaveQuickReplyRequest>("application/json")
            .Produces<QuickReplyDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/{quickReplyId:guid}", DeleteAsync)
            .RequireAuthorization(MessagingPermissions.QuickReplyManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
```

y, después de `CreateAsync`:

```csharp

    private static async Task<IResult> UpdateAsync(
        Guid tenantId, Guid quickReplyId, SaveQuickReplyRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdateQuickReplyCommand(tenantId, quickReplyId, request.Shortcut, request.Body, request.Version), cancellationToken));

    private static async Task<IResult> DeleteAsync(
        Guid tenantId, Guid quickReplyId, long? version, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteQuickReplyCommand(tenantId, quickReplyId, version), cancellationToken);
        return Results.NoContent();
    }
```

`QepServiceCollectionExtensions.cs`, debajo de las dos líneas de la Task 5:

```csharp
        services.AddScoped<ICommandHandler<UpdateQuickReplyCommand, QuickReplyDto>, UpdateQuickReplyHandler>();
        services.AddScoped<ICommandHandler<DeleteQuickReplyCommand, bool>, DeleteQuickReplyHandler>();
```

- [ ] **Step 5: README**

`README.md:797`, debajo de la fila de `/api/v1/tenants/{tenantId}/messaging`, una fila nueva:

```markdown
| `/api/v1/tenants/{tenantId}/messaging/quick-replies` | `GET`, `POST`; por respuesta `PUT` (`version` en el cuerpo) y `DELETE` (`?version=`) | `messaging.conversation.read` (`GET`) / `messaging.quick_reply.manage` |
```

En § «Mensajería (WhatsApp Cloud)», antes del párrafo que empieza con **Búsqueda.**:

```markdown
**Respuestas rápidas.** Textos del tenant con un atajo (`horarios`, sin `/`) que el asesor inserta en el compositor
(spec 2026-10-12). Leerlas pide `messaging.conversation.read`; crearlas, editarlas y borrarlas,
`messaging.quick_reply.manage` (de fábrica sólo en `admin`). El atajo se guarda recortado y en minúsculas y cumple
`^[a-z0-9-]{2,32}$`; es único por tenant (`IX_quick_replies_tenant_shortcut` → 422 `messaging.quick_reply.shortcut_taken`).
El cuerpo va de 1 a 4096 caracteres y sólo admite `{nombre}`, `{asesor}` y `{empresa}`. El `GET` devuelve la lista
entera por atajo y `variables.asesor` / `variables.empresa` ya resueltos para quien llama; `{nombre}` lo resuelve el
frontend con la conversación. `PUT` y `DELETE` exigen la versión (412 si cambió).
```

- [ ] **Step 6: Correr y verificar que pasan, más ArchitectureTests**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.UnitTests\Modules.Messaging.UnitTests.csproj" --no-build --filter "FullyQualifiedName~QuickReplyValidatorsTests|FullyQualifiedName~QuickReplyTests"
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.IntegrationTests\Modules.Messaging.IntegrationTests.csproj" --no-build --filter "FullyQualifiedName~QuickReplyApiTests|FullyQualifiedName~QuickReplyPersistenceTests"
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --no-build --filter "FullyQualifiedName~MessagingLayerTests|FullyQualifiedName~CompositionRootTests"
```

Esperado: todo verde (14 de `QuickReplyApiTests`, 5 de `QuickReplyPersistenceTests`, `EveryCommandAndQueryHasItsHandlerRegistered` incluida).

- [ ] **Step 7: Commit**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
if ((git -C $B branch --show-current) -ne "feature/respuestas-rapidas") { throw "rama equivocada" }
git -C $B status --short
git -C $B add src/Modules/Messaging/Modules.Messaging.Application/QuickReplies.cs src/Modules/Messaging/Modules.Messaging.Api/QuickReplyEndpoints.cs src/Bootstrapper/QepServiceCollectionExtensions.cs README.md tests/Modules/Messaging/Modules.Messaging.UnitTests/QuickReplyValidatorsTests.cs tests/Modules/Messaging/Modules.Messaging.IntegrationTests/QuickReplyApiTests.cs
git -C $B commit -m "feat(messaging): editar y borrar respuestas rápidas con versión"
git -C $B log -1 --format=%B
```

---

### Task 7: Suite completa, una sola vez

**Files:** ninguno (salvo que algo falle por este trabajo).

- [ ] **Step 1: Build y suite completa en primer plano**

Comprobar antes que no corre `Api.exe` (ver «Antes de empezar»).

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet restore "$B\Backend.slnx" --locked-mode
if ($LASTEXITCODE -ne 0) { throw "restore falló" }
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\Backend.slnx" --no-build
```

`--locked-mode` confirma que no se movió ningún `packages.lock.json` (no hay paquetes nuevos). Copiar al handoff el resumen literal por proyecto.

- [ ] **Step 2: Si algo falla, separar lo previo de lo nuevo, por nombre**

Una falla en una clase que este plan tocó (`QuickReply*`, `MessagingTenantNamesTests`, `CompositionRootTests`, `MessagingLayerTests`, `MessagingPersistenceTests`, `CampaignHandlersTests` por `RecordingAudit`) es de este trabajo: se arregla en la tarea que corresponde (con su RED/GREEN) y se vuelve a correr **sólo** esa clase. Una falla en otra clase se compara **por nombre de prueba** contra `develop` (`5ad9fa2`) corriendo sólo esa clase en un worktree temporal hermano (nunca bajo `/tmp`), por ejemplo `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\baseline-respuestas-rapidas`; si también falla ahí, es previa (ya hay dos conocidas en `OrderExportApiTests`) y se anota en el handoff sin tocarla. El worktree temporal se quita al terminar (si `git worktree remove` falla con «Invalid argument», es `codegraph serve` con el índice abierto: cerrarlo primero).

- [ ] **Step 3: Handoff**

Sin commit en este paso. El handoff lleva: los seis commits (`git log --oneline 975ab9c..HEAD`), el resumen literal de la suite, las fallas previas por nombre si las hubo, y las «Desviaciones del spec» de abajo para que el owner las ratifique. Despliegue (spec §6): backend primero (trae la migración `AddQuickReplies`, que aplica el inicializador al arrancar), frontend después; el permiso nuevo exige reiniciar la API y que `admin` recargue la sesión para verlo.

---

## Desviaciones del spec (el código gana)

- **Prefijo de ruta:** §3.6 escribe `/api/tenants/{tenantId:guid}/messaging`; el grupo real de `MessagingEndpoints` es `/api/v1/tenants/{tenantId:guid}/messaging`. Las rutas nuevas cuelgan de ese grupo.
- **`SaveQuickReplyRequest` con `string?`:** §3.6 lo declara con `string Shortcut, string Body`. Con no-nullables, un cuerpo sin esos campos lo rechazaría el binder (400) antes del validador; con `string?` sale 422 `validation.failed` con `errors.shortcut`/`errors.body`, que es lo que el formulario sabe pintar. El JSON es el mismo.
- **La variable desconocida también la marca el validador:** §3.2 pone `messaging.quick_reply.unknown_variable` como código de dominio, y §3.3 pide que el validador repita «las reglas de formato». Se incluyó la regla de variables en el validador: así el 422 trae `errors.body` con la variable culpable. Consecuencia: `invalid_shortcut`, `invalid_body` y `unknown_variable` quedan como última línea de defensa del agregado (mismo papel que `messaging.campaign.invalid`) y por HTTP sale `validation.failed`. El código de dominio que sí llega al frontend es `shortcut_taken`.
- **Normalización del atajo con `Trim()`:** §3.1 dice «en minúsculas y sin `/`». Además se recortan los espacios de los lados (`" Horarios "` → `horarios`); los internos siguen siendo inválidos. La `/` inicial se **rechaza**, no se quita, como pide §3.7.
- **`{asesor}` puede salir con el correo:** §3.3 dice «Sin nombre → `""`», pero el adaptador existente de `IMessagingMemberNames` (`MessagingMemberNames`) devuelve `Membership.DisplayName ?? correo`. Se usa tal cual: una membresía sin `display_name` resuelve `{asesor}` con su correo; sólo sale `""` si quien llama no tiene membresía activa. **Para el owner:** si el correo no debe llegar al cliente, hace falta un método nuevo en el puerto que devuelva sólo `DisplayName`; no se hizo para no cambiar `sentBy.displayName`.
- **`IMessagingTenantNames` no era estrictamente necesario:** `Modules.Messaging.Application` ya referencia `Modules.Tenancy.Application` (por `IMembershipDirectory`, `ITenantModules`), donde vive `ITenantDirectory`. Se creó el puerto igual, como pide §3.3: mantiene la lectura detrás de una interfaz propia del módulo y su adaptador es una línea.
- **Otro tenant: 403 por ruta, 404 por id:** «Otro tenant → 403, nunca 404» se aplica a la ruta (tenant de la ruta distinto del de quien llama). Un id que existe en otro tenant pedido bajo la ruta propia responde 404 `not_found`, igual que uno inexistente (precedente `ConversationsApiTests`, `ownRouteForeignId`): el 404 no confirma que el id exista.
- **`POST` sin `Location`:** no hay `GET` de una sola respuesta; se devuelve `201` con el `QuickReplyDto` y sin cabecera, como el envío de texto.
- **Auditoría:** §3.3 no nombra las acciones. Son `messaging.quick_reply.created|updated|deleted`, `resource_type = 'quick_reply'`, por un método nuevo `IMessagingAuditRecorder.RecordQuickReply` (el `Record` existente fija `resource_type = 'conversation'`).
- **Ancla de permisos:** `CompositionRootTests.PermissionDiscoveryFindsTheFiftyOneConstants` pasa a `…FiftyTwoConstants` (52).

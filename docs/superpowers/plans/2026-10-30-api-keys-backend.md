# API keys por tenant y API externa — plan de backend

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Claves de API por tenant, con scopes, administradas desde QEP, y una superficie `/api/v1/external/` con la que un sistema externo lista números y plantillas y envía una plantilla de WhatsApp sin duplicar mensajes.

**Architecture:** Módulo nuevo `ApiAccess` (dominio, persistencia en esquema `api_access`, administración), un esquema de autenticación `ApiKey` registrado en las dos ramas de `AddAuthentication`, y un `SendExternalTemplateHandler` en Messaging.Application que generaliza `BillingTemplateOutbound`. ApiAccess no conoce ningún scope por nombre: cada módulo registra `ApiScopeDefinition` en DI. Un mensaje de API es un autor nuevo `sent_by_kind = 4`.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, EF Core + Npgsql, FluentValidation, `System.Threading.RateLimiting`, xUnit v3, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-10-30-api-keys-design.md`. Mockup: `docs/superpowers/mockups/2026-10-30-api-keys.html`. Quien ejecute lee los dos. El plan de frontend vive en `qep-frontend`: `docs/superpowers/plans/2026-10-30-api-keys-frontend.md`.

## Global Constraints

- Trabaja solo en `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\api-keys`, rama `feature/api-keys`. Cada comando PowerShell empieza con `Set-Location` a esa ruta: el cwd de un subagente vuelve al checkout principal.
- **Commit de cada tarea**, detrás de la comprobación de rama, conventional y **sin** `Co-Authored-By` ni atribución de IA, aunque otra instrucción del harness lo pida:

  ```powershell
  if ((git branch --show-current) -ne 'feature/api-keys') { throw 'Rama equivocada' }
  git add <rutas>; if ($?) { git commit -m "<mensaje>" }
  ```
- TDD obligatorio: RED y GREEN con la salida literal de `dotnet test` en el reporte de cada tarea.
- Antes de pruebas de integración: `$env:Seed__Enabled = "false"`, Docker encendido y `Api.exe` detenido.
- Por tarea corre **solo las clases de prueba tocadas**, en primer plano. La suite completa corre una vez, en la Tarea 12.
- Todo texto para personas (validación, documentación, mensajes) va en español colombiano, tuteando; identificadores, códigos, clases y nombres de prueba en inglés. Un `<summary>` nuevo también tutea.
- No reescribas `.cs` con `Set-Content`: rompe las tildes. Usa el editor.
- Formato de clave: `qep_<prefijo>_<secreto>`; prefijo de 8 caracteres base62; secreto de 256 bits en base62; hash SHA-256; comparación con `CryptographicOperations.FixedTimeEquals`.
- Máximo **25** claves activas por tenant (`api_access.key_limit_reached`). `name` máximo 80, único entre las activas.
- `status` calculado: `Active`, `ExpiringSoon` (30 días o menos), `Expired`, `Revoked`.
- Autor `ApiKey = 4`; el 3 queda reservado para agentes IA.
- Límites: `external` por IP 600/min antes de autenticar; por clave 300/min después (`ApiAccess:RateLimit:PerKeyPerMinute`).
- Los `code` de spec §5.3 son contrato público: se copian tal cual.
- La clave completa nunca va a base, logs, auditoría, `platform.request_failures` ni trazas. Una clave **nunca** recibe claims `permission`.
- `X-Api-Key` no entra al CORS del SPA.
- `.mcp.json`, `docs/estado.md` y `BillingTemplateOutbound.cs` no se tocan en esta rama.
- Cambiar un `.csproj` o el `Directory.Packages.props` exige `dotnet restore --force-evaluate` y commitear los `packages.lock.json`, o el `docker build` muere con `NU1004`.
- Migraciones con factory, sin `--startup-project`:
  `dotnet ef migrations add <Nombre> --project src/Modules/<Modulo>/Modules.<Modulo>.Infrastructure --context <Modulo>DbContext -o Persistence/Migrations`.

## Review Focus

1. **Dos reintentos simultáneos con el mismo `clientId` y destino, y el primero termina `Unconfirmed`.** El segundo espera en el `INSERT`, cae en `claim.Existing` con `Failed`, y `TakeInFlightAsync` lo dejaría reenviar. Se espera 502 sin segunda llamada a Meta. Prueba en la Tarea 9.
2. **`connectionIds: []` al crear una clave.** Un arreglo vacío no produciría claims `qep_api_connection` y la clave quedaría con todos los números. Se espera 422, y un CHECK en la base como segunda línea. Prueba en la Tarea 4.
3. **Cuerpo que no es JSON, o `parameters` con un número (`{"2": 4821}`).** Un integrador real lo manda así. Se espera 422 `validation.failed` con `code`, no un 400 sin cuerpo. Prueba en la Tarea 9.
4. **Conversación ya ligada a un cliente cuyo teléfono no coincide con `to`.** La búsqueda por número no lo encuentra, y sin más una plantilla `MARKETING` le llegaría a alguien con opt-out. Se espera que las reglas también lean `conversation.CustomerId`. Prueba en la Tarea 10.
5. **`to` con cero inicial (`0573001234567`, `00573001234567`).** Pasa la regla de 8 a 15 dígitos, pero ningún indicativo empieza en cero. Se espera 422 con el campo `to`. Prueba en la Tarea 8.

---

## Mapa de archivos

**Módulo nuevo:**

- `src/Modules/ApiAccess/Modules.ApiAccess.Domain/`: `ApiKey.cs`, `ApiKeyId.cs`, `ApiKeyMaterial.cs`, `ApiKeyStatus.cs`, `ApiAccessDomainException.cs`.
- `.../Modules.ApiAccess.Application/`: `ApiAccessPermissions.cs`, `ApiAccessErrorCodes.cs`, `ApiAccessDtos.cs`, `ApiAccessPorts.cs`, `ApiAccessAuthorization.cs`, `ApiAccessAudit.cs`, `ApiScopeCatalog.cs`, `ListApiKeys.cs`, `ListApiScopes.cs`, `CreateApiKey.cs`, `RevokeApiKey.cs`, `ApiKeyVerifier.cs`, `ApiAccessOptions.cs`.
- `.../Modules.ApiAccess.Infrastructure/`: `ApiAccessInfrastructureExtensions.cs`, `ApiAccessDatabaseInitializer.cs`, `Persistence/ApiAccessDbContext.cs`, `ApiAccessDbContextFactory.cs`, `ApiKeyRepository.cs`, `ApiAccessUnitOfWork.cs`, `ApiAccessAuditRecorder.cs`, `Persistence/Migrations/`.
- `.../Modules.ApiAccess.Api/ApiKeyEndpoints.cs`.
- Pruebas: `tests/Modules/ApiAccess/Modules.ApiAccess.{UnitTests,IntegrationTests}`, `tests/ArchitectureTests/ArchitectureTests/ApiAccessLayerTests.cs`.

**Superficie compartida:** `src/BuildingBlocks/BuildingBlocks.Application/ApiAccessContracts.cs`, `src/Bootstrapper/ExternalApiPaths.cs`, `src/Bootstrapper/ApiKeys/` (`ApiKeyAuthenticationHandler.cs`, `HttpApiCaller.cs`, `ApiKeyRateLimitFilter.cs`, `DeveloperModuleFilter.cs`, `ApiKeyConnectionDirectory.cs`), `src/Api/ExternalApiEndpoints.cs`, `src/Api/ExternalRateLimitRejection.cs`.

**Messaging:** `Modules.Messaging.Application/External/` (`MessagingScopes.cs`, `ExternalPhoneNumber.cs`, `ExternalRecipientPolicy.cs`, `ExternalSendFailure.cs`, `ExternalApiAccess.cs`, `ListExternalConnections.cs`, `ListExternalTemplates.cs`, `SendExternalTemplate.cs`), `Modules.Messaging.Api/ExternalWhatsAppEndpoints.cs`.

---

### Task 1: Capacidad `developer` y guarda que falla cerrada

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/TenantModuleGuard.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs` (CHECK)
- Create: migración `AddDeveloperModuleKey`
- Modify: `README.md` § Módulos por tenant
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleKeysTests.cs`, `.../TenantModuleGuardTests.cs` (nueva), y las cuentas de `TenantModulesApiTests`, `OperatorConsoleApiTests`, `SeedStartupTests`, `OperatorReadHandlerTests` (ver commit `1c14c69b`)

**Interfaces:**
- Produces: `TenantModuleKeys.Developer` (`"developer"`, último de `All`, sin dependencias, fuera de `DefaultForNewTenants`); `TenantModuleGuard.EnsureEnabledFailClosedAsync(ITenantModules modules, Guid tenantId, TenantModuleKey key, CancellationToken cancellationToken): Task`.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
[Fact] public void DeveloperIsLastHasNoDependenciesAndIsOffForNewTenants()
{
    Assert.Equal("developer", TenantModuleKeys.All[^1].Value);
    Assert.Empty(TenantModuleKeys.DependenciesOf(TenantModuleKeys.Developer));
    Assert.DoesNotContain(TenantModuleKeys.Developer, TenantModuleKeys.DefaultForNewTenants);
}

[Fact] public async Task FailClosedDeniesAnUnknownTenant() // FindAsync => null
{
    var denied = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
        TenantModuleGuard.EnsureEnabledFailClosedAsync(new NoTenantModules(), Guid.CreateVersion7(), TenantModuleKeys.Developer, Ct));
    Assert.Equal(TenantModuleGuard.ModuleNotEnabledCode, denied.Code);
}
```

Agrega `FailClosedDeniesADisabledModule` y `FailClosedLetsAnEnabledModuleThrough`. Actualiza la lista literal de `TenantModuleKeysTests` y las cuentas de las cuatro clases.

- [ ] **Step 2: Corre y verifica RED**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModuleKeysTests|FullyQualifiedName~TenantModuleGuardTests"
```

Esperado: no compila, `TenantModuleKeys.Developer` no existe.

- [ ] **Step 3: Implementa**

`Developer` con su `<summary>` citando D7. `EnsureEnabledFailClosedAsync` es `set is null || !set.IsEnabled(key)`, el mismo `RequestForbiddenException` de `EnsureEnabledAsync`. Agrega `'developer'` al CHECK de `TenancyDbContext`. Genera la migración y escribe el `Up`/`Down` como `20261024172027_AddSuppliersModuleKey.cs`, con `DELETE ... WHERE module_key = 'developer'` en el `Down`. Agrega la fila al README.

- [ ] **Step 4: Corre y verifica GREEN**

Las dos clases unitarias y, con Docker, `--filter "FullyQualifiedName~TenantModulesApiTests|FullyQualifiedName~OperatorConsoleApiTests|FullyQualifiedName~SeedStartupTests"` en `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests`. Esperado: PASS.

- [ ] **Step 5: Commit** `feat(tenancy): add developer module key and fail-closed guard`

---

### Task 2: Proyectos del módulo, contratos compartidos y dominio

**Files:**
- Create: los seis `.csproj` del módulo. Plantilla: `Modules.Suppliers.*`. `Application` referencia además `..\..\Audit\Modules.Audit.Domain`, como Integrations. `Infrastructure` no lleva ClosedXML ni libphonenumber.
- Modify: `Backend.slnx`, `src/Api/Api.csproj` (`.Api`, `.Infrastructure`), `src/Bootstrapper/Bootstrapper.csproj` (`.Application`, `.Infrastructure`), `tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj` (los cuatro)
- Create: `src/BuildingBlocks/BuildingBlocks.Application/ApiAccessContracts.cs`
- Modify: `src/BuildingBlocks/BuildingBlocks.Application/ApplicationExceptions.cs`
- Create: dominio completo (ver Mapa)
- Test: `tests/Modules/ApiAccess/Modules.ApiAccess.UnitTests/ApiKeyTests.cs`, `ApiKeyMaterialTests.cs`; `tests/ArchitectureTests/ArchitectureTests/ApiAccessLayerTests.cs`
- Modify: todos los `packages.lock.json` afectados

**Interfaces:**
- Produces, en `BuildingBlocks.Application`:

```csharp
public sealed record ApiScopeDefinition(string Key, string Name, string Description, string Category, string RequiredModule, bool RestrictableByConnection);

public interface IApiCaller
{
    Guid ApiKeyId { get; }
    string ApiKeyName { get; }
    Guid TenantId { get; }
    bool HasScope(string scope);
    bool CanUseConnection(Guid connectionId);
}

public static class ApiKeyDefaults { public const string AuthenticationScheme = "ApiKey"; public const string HeaderName = "X-Api-Key"; }
public static class ApiKeyClaimTypes { KeyId = "qep_api_key_id"; KeyName = "qep_api_key_name"; Scope = "qep_api_scope"; Connection = "qep_api_connection"; }
public static class ExternalApiErrorCodes { InvalidKey = "external.auth.invalid_key"; ScopeMissing = "external.auth.scope_missing"; ConnectionNotAllowed = "external.connection_not_allowed"; RateLimited = "external.rate_limited"; }

public sealed class RequestRateLimitedException(string code, string message, TimeSpan? retryAfter) : Exception(message)
{ public string Code { get; } = code; public TimeSpan? RetryAfter { get; } = retryAfter; }
```

- Produces, en el dominio:

```csharp
public readonly record struct ApiKeyId(Guid Value); // con New(), como SupplierId
public enum ApiKeyStatus { Active, ExpiringSoon, Expired, Revoked }

public sealed class ApiKeyMaterial
{
    public string Prefix { get; } public string Secret { get; } public byte[] SecretHash { get; }
    public string Plaintext => $"qep_{Prefix}_{Secret}";
    public override string ToString(); // nunca imprime el secreto
    public static ApiKeyMaterial Generate();
    public static bool TryParse(string? presented, out string prefix, out string secret);
    public static bool Matches(string secret, byte[] expectedHash);
}

public sealed class ApiKey
{
    public const int NameMaxLength = 80; public const int PrefixLength = 8; public const int MaxActivePerTenant = 25;
    public static readonly TimeSpan ExpiringSoonWindow = TimeSpan.FromDays(30);
    public ApiKeyId Id; public Guid TenantId; public string Name; public string KeyPrefix; public byte[] SecretHash;
    public string[] Scopes; public Guid[]? ConnectionIds; public DateTimeOffset? ExpiresAt; public DateTimeOffset? LastUsedAt;
    public DateTimeOffset CreatedAt; public Guid CreatedByMemberId; public DateTimeOffset? RevokedAt; public Guid? RevokedByMemberId; public int Version;
    public static ApiKey Create(Guid tenantId, string name, ApiKeyMaterial material, IReadOnlyCollection<string> scopes, IReadOnlyCollection<Guid>? connectionIds, DateTimeOffset? expiresAt, Guid createdByMemberId, DateTimeOffset now);
    public bool Revoke(Guid memberId, DateTimeOffset now); // true si cambió; sube Version
    public ApiKeyStatus StatusAt(DateTimeOffset now);
    public bool IsUsableAt(DateTimeOffset now); // sin revocar y sin vencer
}
```

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
[Fact] public void GeneratedKeyHasTheDocumentedShapeAndAParsableForm()
{
    var material = ApiKeyMaterial.Generate();
    Assert.Matches("^qep_[0-9A-Za-z]{8}_[0-9A-Za-z]{43}$", material.Plaintext);
    Assert.True(ApiKeyMaterial.TryParse(material.Plaintext, out var prefix, out var secret));
    Assert.Equal(material.Prefix, prefix);
    Assert.True(ApiKeyMaterial.Matches(secret, material.SecretHash));
}

[Fact] public void ToStringNeverPrintsTheSecret() { var m = ApiKeyMaterial.Generate(); Assert.DoesNotContain(m.Secret, m.ToString()); }

[Theory]
[InlineData(null)] [InlineData("")] [InlineData("qep_short_x")] [InlineData("qep_live_8fK2mW0xT7pLq9VbN4sRy6Jd1cHzE3uA")] [InlineData("qep_abcd1234_con espacios")]
public void MalformedKeysDoNotParse(string? presented) => Assert.False(ApiKeyMaterial.TryParse(presented, out _, out _));

[Theory] // now = 2026-10-30T12:00Z
[InlineData(null, false, ApiKeyStatus.Active)]
[InlineData("2026-11-29T12:00:00Z", false, ApiKeyStatus.ExpiringSoon)] // justo 30 días
[InlineData("2026-11-29T12:00:01Z", false, ApiKeyStatus.Active)]
[InlineData("2026-10-30T12:00:00Z", false, ApiKeyStatus.Expired)]      // vence en este instante
[InlineData("2026-10-01T00:00:00Z", true, ApiKeyStatus.Revoked)]       // revocada gana a vencida
public void StatusFollowsTheSpecTable(string? expiresAt, bool revoked, ApiKeyStatus expected);
```

Agrega `CreateRejectsAnEmptyConnectionList` (`ConnectionIds` vacío lanza `ApiAccessDomainException`), `CreateRejectsAnEmptyScopeList`, `CreateRejectsANameLongerThan80` y `RevokingTwiceKeepsTheFirstRevocation` (el segundo `Revoke` da `false` y no toca `RevokedAt` ni `Version`). `ApiAccessLayerTests` copia `SuppliersLayerTests`, cambiando la última regla: Application solo puede referenciar `Modules.ApiAccess`, `Modules.Tenancy` y `Modules.Audit`.

- [ ] **Step 2: Corre y verifica RED**

```powershell
dotnet test tests/Modules/ApiAccess/Modules.ApiAccess.UnitTests
```

Esperado: no compila, `ApiKey` no existe.

- [ ] **Step 3: Crea proyectos y contratos, e implementa el dominio**

`Generate` usa `RandomNumberGenerator.GetString(alfabeto, 8)` y `GetString(alfabeto, 43)`: 43 caracteres base62 son 256 bits y no introducen el sesgo de convertir bytes. `TryParse` con `[GeneratedRegex]` anclado y un corte previo por longitud total. `SecretHash` es `SHA256.HashData(Encoding.ASCII.GetBytes(secret))`. El endpoint y el filtro de Bootstrapper usan estas constantes: no dupliques los textos. `ModuleKey` no aparece en BuildingBlocks: `RequiredModule` es `string` porque BuildingBlocks no ve Tenancy, y `CompositionRootTests` lo amarra en la Tarea 4.

- [ ] **Step 4: Regenera los lock files**

```powershell
dotnet restore Backend.slnx --force-evaluate
```

Esperado: exit 0 y `git status` lista los `packages.lock.json` de los proyectos nuevos más Api, Bootstrapper, ArchitectureTests y los que dependan de ellos.

- [ ] **Step 5: Corre y verifica GREEN**

`dotnet test tests/Modules/ApiAccess/Modules.ApiAccess.UnitTests` y `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~ApiAccessLayerTests"`. Esperado: PASS.

- [ ] **Step 6: Commit** `feat(api-access): add module projects, shared contracts and key domain`

---

### Task 3: Persistencia, migración y auditoría propia

**Files:**
- Create: `ApiAccessDbContext.cs`, `ApiAccessDbContextFactory.cs`, `ApiKeyRepository.cs`, `ApiAccessUnitOfWork.cs`, `ApiAccessAuditRecorder.cs`, `ApiAccessInfrastructureExtensions.cs`, `ApiAccessDatabaseInitializer.cs`, migración `InitialApiAccess`
- Create: `Modules.ApiAccess.Application/ApiAccessPorts.cs`, `ApiAccessErrorCodes.cs`, `ApiAccessAudit.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:968` (`AddApiAccessInfrastructure`), `src/Api/Program.cs` (inicializador después de Audit, antes de la semilla)
- Test: `tests/Modules/ApiAccess/Modules.ApiAccess.IntegrationTests/ApiAccessApiHarness.cs`, `ApiKeyPersistenceTests.cs`

**Interfaces:**
- Produces:

```csharp
public interface IApiKeyRepository
{
    void Add(ApiKey key);
    Task<ApiKey?> FindAsync(Guid tenantId, ApiKeyId id, CancellationToken cancellationToken);
    Task<IReadOnlyList<ApiKey>> ListAsync(Guid tenantId, CancellationToken cancellationToken); // created_at DESC, id DESC
    Task<int> CountActiveAsync(Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken); // sin revocar y sin vencer
    Task<ApiKey?> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken); // cross-tenant, AsNoTracking
    Task TouchLastUsedAsync(ApiKeyId id, DateTimeOffset now, CancellationToken cancellationToken);
}
public interface IApiAccessUnitOfWork { Task SaveChangesAsync(CancellationToken cancellationToken); }
public interface IApiAccessAuditRecorder
{
    void Record(Guid tenantId, Guid actorId, AuditActorType actorType, string action, Guid apiKeyId, string outcome, IReadOnlyCollection<string> changedFields, DateTimeOffset occurredAt);
}
public static class ApiKeyAuditActions { ResourceType = "api_key"; Created = "api_access.key.created"; Revoked = "api_access.key.revoked"; Success = "success"; }
public static class ApiAccessErrorCodes { KeyLimitReached = "api_access.key_limit_reached"; KeyNameTaken = "api_access.key_name_taken"; KeyNotFound = "api_access.key_not_found"; }
```

- [ ] **Step 1: Escribe las pruebas que fallan**

Base real con Testcontainers. `ApiAccessApiHarness` copia `SuppliersApiHarness` y de `MessagingApiHarness` toma `RegisterTenantAsync`, `OwnerMembershipIdAsync` y `SeedMemberAsync` (hace falta membresía real). Fija `Seed:Enabled=false` y `Entitlements:GrantDefaultModulesOnSignup=true`; agrega `EnableDeveloperAsync(connectionString, tenantId)` y `EnableModuleAsync(..., key)`.

```csharp
[Fact] public async Task TheHashIsStoredAndNeverThePlaintext();
[Fact] public async Task TwoActiveKeysCannotShareANameButARevokedOneFreesIt(); // el segundo Add da ApiAccessErrorCodes.KeyNameTaken por IX_api_keys_tenant_name_active
[Fact] public async Task TheDatabaseRejectsAnEmptyConnectionListAndAnEmptyScopeList(); // INSERT crudo: 23514 en CK_api_keys_connections y CK_api_keys_scopes
[Fact] public async Task TouchWritesOnceAMinuteAtMost(); // t0 escribe, t0+59s no cambia, t0+61s escribe
```

- [ ] **Step 2: Corre y verifica RED**

```powershell
dotnet test tests/Modules/ApiAccess/Modules.ApiAccess.IntegrationTests --filter "FullyQualifiedName~ApiKeyPersistenceTests"
```

Esperado: no compila, `ApiAccessDbContext` no existe.

- [ ] **Step 3: Implementa el mapeo y genera la migración**

Esquema `api_access`, tabla `api_keys`, historial `__ef_migrations_history` en ese esquema, columnas de spec §4.1 con `text[]`, `uuid[]`, `bytea`. `Version` como token de concurrencia, como `Supplier`. Proyecta `audit.entries` con `ExcludeFromMigrations`, como `IntegrationsDbContext`. Índices y CHECK con estos nombres:

| Nombre | Definición |
| --- | --- |
| `IX_api_keys_key_prefix` | único en `key_prefix` |
| `IX_api_keys_tenant_name_active` | único en `(tenant_id, name)` `WHERE revoked_at IS NULL` |
| `IX_api_keys_tenant_created` | `(tenant_id, created_at)` |
| `CK_api_keys_scopes` | `cardinality(scopes) > 0` |
| `CK_api_keys_connections` | `connection_ids IS NULL OR cardinality(connection_ids) > 0` |
| `CK_api_keys_revocation` | `(revoked_at IS NULL) = (revoked_by_member_id IS NULL)` |

`TouchLastUsedAsync` es `ExecuteUpdate` con `Where(key.LastUsedAt == null || key.LastUsedAt < now - 1 minuto)`, sin transacción. `ApiAccessUnitOfWork` copia `SuppliersUnitOfWork`: `DbUpdateConcurrencyException` a `RequestConcurrencyException("concurrency.conflict", ...)`, y `IX_api_keys_tenant_name_active` por nombre a `ApiAccessDomainException(KeyNameTaken)`. `ApiAccessAuditRecorder` copia `IntegrationsAuditRecorder` con `Source = "api_access"`, y `changedFields` son nombres de campo, nunca valores.

```powershell
dotnet ef migrations add InitialApiAccess --project src/Modules/ApiAccess/Modules.ApiAccess.Infrastructure --context ApiAccessDbContext -o Persistence/Migrations
```

- [ ] **Step 4: Corre y verifica GREEN**

Mismo comando del Step 2. Esperado: PASS.

- [ ] **Step 5: Commit** `feat(api-access): persist api keys with atomic audit`

---

### Task 4: Administración: permiso, catálogo de scopes y endpoints

**Files:**
- Create: `ApiAccessPermissions.cs`, `ApiAccessDtos.cs`, `ApiAccessAuthorization.cs`, `ApiScopeCatalog.cs`, `ListApiKeys.cs`, `ListApiScopes.cs`, `CreateApiKey.cs`, `RevokeApiKey.cs`, `ApiKeyEndpoints.cs`
- Create: `src/Bootstrapper/ApiKeys/ApiKeyConnectionDirectory.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/External/MessagingScopes.cs`
- Modify: `QepServiceCollectionExtensions.cs` (handlers junto a Suppliers en `:266`, validadores en `:953`, rol `admin` en `:1434`, `PermissionDefinition` en `:2050`, política en `:2430`, scope, adaptador), `src/Api/Program.cs` (`app.MapApiKeyEndpoints()`)
- Modify: `src/Modules/ApiAccess/Modules.ApiAccess.Application/ApiAccessPorts.cs`
- Test: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs`, `tests/Modules/ApiAccess/Modules.ApiAccess.UnitTests/CreateApiKeyValidatorTests.cs`, `tests/Modules/ApiAccess/Modules.ApiAccess.IntegrationTests/ApiKeyAdminApiTests.cs`

**Interfaces:**
- Consumes: Tareas 2 y 3.
- Produces:

```csharp
public static class ApiAccessPermissions { public const string ApiKeyManage = "developer.api_key.manage"; }
public static class MessagingScopes { public const string TemplatesSend = "whatsapp.templates.send"; }

public interface IApiKeyConnectionDirectory
{   // conexiones whatsapp-cloud del tenant, activas o no: id => nombre
    Task<IReadOnlyDictionary<Guid, string>> ListAsync(Guid tenantId, CancellationToken cancellationToken);
}

public sealed record ApiKeyScopeDto(string Key, string Name);
public sealed record ApiKeyConnectionDto(Guid Id, string? Name);          // Name null = conexión ya borrada
public sealed record ApiKeyDto(Guid Id, string Name, string KeyPrefix, IReadOnlyList<ApiKeyScopeDto> Scopes,
    IReadOnlyList<ApiKeyConnectionDto>? Connections,                     // null = todos los números
    DateTimeOffset? LastUsedAt, DateTimeOffset? ExpiresAt, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt, string Status);
public sealed record CreatedApiKeyDto(ApiKeyDto Key, string Secret);      // Secret es la clave completa
public sealed record ApiScopeDto(string Key, string Name, string Description, bool RestrictableByConnection);
public sealed record ApiScopeCategoryDto(string Category, IReadOnlyList<ApiScopeDto> Scopes);
public sealed record ApiScopeCatalogDto(IReadOnlyList<ApiScopeCategoryDto> Categories);

public sealed record ListApiKeysQuery(Guid TenantId) : IQuery<IReadOnlyList<ApiKeyDto>>;
public sealed record ListApiScopesQuery(Guid TenantId) : IQuery<ApiScopeCatalogDto>;
public sealed record CreateApiKeyCommand(Guid TenantId, string? Name, IReadOnlyList<string>? Scopes, IReadOnlyList<Guid>? ConnectionIds, DateTimeOffset? ExpiresAt) : ICommand<CreatedApiKeyDto>;
public sealed record RevokeApiKeyCommand(Guid TenantId, Guid ApiKeyId) : ICommand<ApiKeyDto>;

// ApiScopeCatalog (scoped): new(IEnumerable<ApiScopeDefinition>, ITenantModules)
public Task<IReadOnlyList<ApiScopeDefinition>> AvailableForAsync(Guid tenantId, CancellationToken cancellationToken);
public IReadOnlyDictionary<string, ApiScopeDefinition> All { get; }
```

- [ ] **Step 1: Escribe las pruebas que fallan**

`CompositionRootTests`: renombra a `PermissionDiscoveryFindsTheSeventyConstants`, cambia `69` por `70` y suma la línea al comentario. Agrega:

```csharp
[Fact] public void TheApiKeyPermissionRequiresTheDeveloperModuleAndGoesOnlyToAdmin();
// Category "Developer", RequiredModules [ModuleKeys.Developer]; en admin, no en advisor, billing ni cashier.

[Fact] public void EveryApiScopeDeclaresARealModule();
// Para cada ApiScopeDefinition registrada: TenantModuleKey.Parse(RequiredModule) no lanza y Key no se repite.

[Fact] public void TheTemplatesSendScopeIsRestrictableAndBelongsToMessaging();
```

`ApiKeyAdminApiTests`, con `IApiKeyConnectionDirectory` reemplazado por un doble en `ConfigureTestServices`, que devuelve dos conexiones propias:

```csharp
[Fact] public async Task CreateReturnsTheSecretOnlyInThe201();
// 201, Secret empieza por "qep_", y el GET posterior no contiene el secreto ni "secretHash".
[Fact] public async Task ListCalculatesStatusAndNeverExposesTheHash();
[Fact] public async Task ScopeCatalogOnlyListsScopesOfEnabledModules(); // messaging apagado: Categories vacío
[Fact] public async Task AScopeOfADisabledModuleIsRejected(); // 422 validation.failed, errors["scopes"]
[Fact] public async Task AnEmptyConnectionListIsRejectedInsteadOfGrantingEveryNumber(); // connectionIds: [] => 422, errors["connectionIds"] (Review Focus 2)
[Fact] public async Task ConnectionIdsNeedARestrictableScope(); // catálogo doble sin restringibles => 422
[Fact] public async Task AConnectionOfAnotherTenantIs403(); // nunca 404, code external.connection_not_allowed
[Fact] public async Task ThePastExpiryIsRejected(); // errors["expiresAt"]
[Fact] public async Task TheTwentySixthActiveKeyIsRejected(); // 422 api_access.key_limit_reached
[Fact] public async Task RevokingTwiceDoesNotFailAndAuditsOnce();
[Fact] public async Task CreateAuditsWithoutTheSecret(); // audit.entries: api_access.key.created, sin el plaintext
[Fact] public async Task WithoutThePermissionTheRoutesAre403();
[Fact] public async Task AKeyOfAnotherTenantIs404OnRevoke(); // ApiAccessErrorCodes.KeyNotFound, igual que una inexistente
```

- [ ] **Step 2: Corre y verifica RED**

```powershell
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"
dotnet test tests/Modules/ApiAccess/Modules.ApiAccess.UnitTests --filter "FullyQualifiedName~CreateApiKeyValidatorTests"
dotnet test tests/Modules/ApiAccess/Modules.ApiAccess.IntegrationTests --filter "FullyQualifiedName~ApiKeyAdminApiTests"
```

Esperado: no compila, `ApiAccessPermissions` no existe.

- [ ] **Step 3: Implementa handlers y validador**

Orden de `CreateApiKeyHandler`: `ApiAccessAuthorization.EnsureAsync` (tenant + permiso + módulo con `TenantModuleGuard.EnsureEnabledAsync`, como `MessagingAuthorization`) → validador → membresía con `IMembershipDirectory.FindActiveMembershipIdAsync` (sin ella, 403 `authorization.denied`) → `CountActiveAsync >= ApiKey.MaxActivePerTenant` lanza `KeyLimitReached` → scopes disponibles → restringible → conexiones → `ApiKey.Create` → auditoría → `SaveChangesAsync`.

`CreateApiKeyValidator` (claves de `errors` en camelCase con `OverridePropertyName`, copy en español): `name` obligatorio y máximo 80 tras recortar; `scopes` no vacío y sin repetidos, cada uno conocido en `IEnumerable<ApiScopeDefinition>`; `connectionIds` nulo o no vacío, sin `Guid.Empty`; `expiresAt` nulo o mayor que `IClock.UtcNow`.

Un scope conocido pero de módulo apagado, o `connectionIds` sin scope restringible, es `ValidationException` en el handler con clave `scopes` o `connectionIds`. Una conexión fuera de `IApiKeyConnectionDirectory.ListAsync` es `RequestForbiddenException(ExternalApiErrorCodes.ConnectionNotAllowed, ...)`. Audita `ApiKeyAuditActions.Created` con `changedFields = ["connectionIds", "expiresAt", "name", "scopes"]` y `AuditActorType.Human`. `RevokeApiKeyHandler` audita solo si `Revoke` devolvió `true`.

`ApiKeyConnectionDirectory` (Bootstrapper) usa `IMessagingConnectionDirectory.ListAllAsync` filtrando `Channel == WhatsApp`, no `ListNamesAsync`: ver Discrepancias. `ApiKeyEndpoints` sigue `SupplierEndpoints`, `/api/v1/tenants/{tenantId:guid}/api-keys`, `.RequireAuthorization(ApiAccessPermissions.ApiKeyManage)`.

Registra las **dos mitades** del permiso más sus datos y el scope:

```csharp
services.AddSingleton(new ApiScopeDefinition(
    MessagingScopes.TemplatesSend, "Enviar plantillas", "Incluye consultar las plantillas aprobadas.", "WhatsApp",
    ModuleKeys.Messaging.Value, RestrictableByConnection: true));
```

`PermissionDefinition`: título «Administrar claves de API», descripción «Permite crear, ver y revocar las claves con las que tus sistemas se conectan a QEP.», categoría `"Developer"`, riesgo `"high"`, `RequiredModules: [ModuleKeys.Developer]`.

- [ ] **Step 4: Corre y verifica GREEN**

Los tres comandos del Step 2. Esperado: PASS, incluida `EveryCommandAndQueryHasItsHandlerRegistered`.

- [ ] **Step 5: Commit** `feat(api-access): add api key administration and scope catalog`

---

### Task 5: Esquema `ApiKey`, grupo externo y lecturas

**Files:**
- Create: `Modules.ApiAccess.Application/ApiKeyVerifier.cs`, `src/Bootstrapper/ApiKeys/ApiKeyAuthenticationHandler.cs`, `HttpApiCaller.cs`, `DeveloperModuleFilter.cs`
- Create: `src/Api/ExternalApiEndpoints.cs`
- Create: `Modules.Messaging.Application/External/ExternalApiAccess.cs`, `ListExternalConnections.cs`, `ListExternalTemplates.cs`; `Modules.Messaging.Api/ExternalWhatsAppEndpoints.cs`
- Modify: `QepServiceCollectionExtensions.cs:2089-2097` y `:2120-2124` (esquema en las dos ramas), `:76` (`IApiCaller`), registro de handlers junto a `:826`; `src/Api/Program.cs` (`app.MapExternalApi()` después de `MapMessagingEndpoints`)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ExternalApiAuthTests.cs`, `MessagingApiHarness.cs`; `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/RealAuthenticationApiTests.cs`

**Interfaces:**
- Consumes: `IApiKeyRepository`, `ApiKeyMaterial`, `ITenantDirectory.GetStatusAsync`.
- Produces:

```csharp
public enum ApiKeyRejection { Missing, Malformed, Unknown, SecretMismatch, Revoked, Expired, TenantNotActive }
public sealed record VerifiedApiKey(Guid Id, string Name, Guid TenantId, string KeyPrefix, IReadOnlyList<string> Scopes, IReadOnlyList<Guid>? ConnectionIds);
public sealed record ApiKeyVerification(VerifiedApiKey? Key, ApiKeyRejection? Rejection, string? KeyPrefix);
public interface IApiKeyVerifier { Task<ApiKeyVerification> VerifyAsync(string? presentedKey, CancellationToken cancellationToken); }

// Bootstrapper, namespace Bootstrapper.ApiKeys
internal sealed class ApiKeyAuthenticationHandler(...) : AuthenticationHandler<AuthenticationSchemeOptions>(...);
internal sealed class HttpApiCaller(IHttpContextAccessor accessor) : IApiCaller;
public sealed class DeveloperModuleFilter(ITenantModules modules, IApiCaller caller) : IEndpointFilter;

// Messaging
public static class ExternalApiAccess
{   // scope + módulo messaging, fallando cerrado
    public static Task EnsureAsync(IApiCaller caller, ITenantModules modules, string scope, CancellationToken cancellationToken);
    // caller.CanUseConnection + FindChannelAsync: null => 403; Instagram => TemplateOutbound.EnsureWhatsApp
    public static Task EnsureConnectionAsync(IApiCaller caller, IMessagingConnectionDirectory connections, Guid connectionId, CancellationToken cancellationToken);
}
public sealed record ExternalConnectionDto(Guid Id, string Name, string? PhoneNumber);
public sealed record ExternalTemplateDto(string Name, string Language, string Category, IReadOnlyList<string> Parameters);
public sealed record ListExternalConnectionsQuery : IQuery<IReadOnlyList<ExternalConnectionDto>>;
public sealed record ListExternalTemplatesQuery(Guid ConnectionId) : IQuery<IReadOnlyList<ExternalTemplateDto>>;
// IEndpointRouteBuilder group: Modules.Messaging.Api
public static RouteGroupBuilder MapExternalWhatsAppEndpoints(this RouteGroupBuilder group);
```

En `MessagingApiHarness`: `EnableDeveloperAsync(connectionString, tenantId)`, `SeedApiKeyAsync(connectionString, tenantId, memberId, string[] scopes, Guid[]? connectionIds = null, DateTimeOffset? expiresAt = null, string name = "ERP Contable"): Task<(Guid Id, string Plaintext)>` (INSERT crudo con `ApiKeyMaterial.Generate()`), `CreateExternalClient(factory, plaintext)` (sin headers `X-*`), `ExternalConnectionsUrl`, `ExternalMessagesUrl`, `WithRealAuthentication(...)` copiado de `ShopifyApiHarness`.


- [ ] **Step 1: Escribe las pruebas que fallan**

`ExternalApiAuthTests`:

```csharp
[Theory] [InlineData(null)] [InlineData("basura")] [InlineData("qep_abcd1234_inventada0000000000000000000000000000000")]
public async Task AMissingMalformedOrUnknownKeyGetsTheSame401(string? key);
// 401, Content-Type problem, code "external.auth.invalid_key"; los tres cuerpos iguales salvo traceId.

[Fact] public async Task TheNextRequestAfterRevokingIs401();
[Fact] public async Task TheNextRequestAfterExpiringIs401(); // WithTestClock
[Fact] public async Task AKeyOfASuspendedTenantIs401();
[Fact] public async Task TwoHeaderValuesAreRejected(); // X-Api-Key repetida: 401
[Fact] public async Task AKeyWithoutTheScopeIs403WithItsCode(); // external.auth.scope_missing
[Fact] public async Task HandlerRevalidatesTheScopeWhenCalledDirectly();
// Resuelve ListExternalConnectionsQuery con un IApiCaller doble sin scope: RequestForbiddenException.
[Fact] public async Task DeveloperOffIs403ModuleNotEnabled();
[Fact] public async Task MessagingOffIs403ModuleNotEnabled();
[Fact] public async Task TheApiWorksWithTheDevelopmentStubOn(); // QepApiFactory sin cambios: 200 con solo X-Api-Key
[Fact] public async Task ConnectionsOnlyListTheNumbersTheKeyMayUse(); // clave con una de dos, sin Instagram
[Fact] public async Task TemplatesOnlyListSendableOnes(); // nombre, idioma, categoría, parámetros
[Fact] public async Task AConnectionOfAnotherTenantIs403(); // external.connection_not_allowed, también con connection_ids NULL
[Fact] public async Task TemplatesOfAnInstagramConnectionAre422(); // messaging.channel_unsupported
[Fact] public async Task LastUsedIsWrittenAndAFailureDoesNotBreakTheRequest();
// Segundo caso con IApiKeyRepository decorado cuyo TouchLastUsedAsync lanza: 200, y el log trae "last_used".
```

`RealAuthenticationApiTests`:

```csharp
[Fact] public async Task AValidSessionCookieCannotEnterTheExternalApi();  // 401 external.auth.invalid_key
[Fact] public async Task AnApiKeyCannotEnterAnInternalRoute();            // X-Api-Key en /api/v1/tenants/{id}/settings: 401
```

`ArchitectureTests`: `MessagingLayerTests.ExternalHandlersReadTheCallerNotTheExecutionContext`: los constructores de los tres handlers externos (incluye `SendExternalTemplateHandler` en la Tarea 9) no reciben `IExecutionContext`.

- [ ] **Step 2: Corre y verifica RED**

```powershell
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ExternalApiAuthTests"
```

Esperado: no compila, `SeedApiKeyAsync` no existe.

- [ ] **Step 3: Implementa verificación y handler de autenticación**

`ApiKeyVerifier.VerifyAsync`: nulo → `Missing`; `TryParse` falla → `Malformed` **sin** prefijo; `FindByPrefixAsync` → `Unknown`; `Matches` → `SecretMismatch`; `RevokedAt` → `Revoked`; vencida → `Expired`; `ITenantDirectory.GetStatusAsync != Active` (nulo incluido) → `TenantNotActive`. Sin caché.

`HandleAuthenticateAsync`: si `Request.Headers["X-Api-Key"].Count != 1`, pasa `null` o trata la repetición como `Malformed`. Rechazo: log `[LoggerMessage]` con `Rejection` y `KeyPrefix`, nunca el valor presentado, y `AuthenticateResult.Fail("Invalid API key.")`. Aceptación: `TouchLastUsedAsync` dentro de `try/catch (Exception)` con log y sin propagar (spec §6.2), y un único `ClaimsIdentity(claims, "ApiKey")` con `qep_sub`, `tenant_id`, `qep_api_key_id`, `qep_api_key_name`, un `qep_api_scope` por scope y un `qep_api_connection` por conexión. Ningún claim `permission`. `qep_sub` y `tenant_id` tienen que ir en esa misma identidad: `ExternalClaimsTransformation.cs:52-61` los busca ahí.

`HandleChallengeAsync` y `HandleForbiddenAsync` escriben por `IProblemDetailsService` con `code` y `traceId` (`Activity.Current?.Id ?? Context.TraceIdentifier`): `external.auth.invalid_key` y `external.auth.scope_missing`.

Extrae `AddApiKeyScheme(AuthenticationBuilder)` y llámalo en las dos ramas de `AddAuthentication`, antes del `return` del stub.

- [ ] **Step 4: Implementa grupo y lecturas**

```csharp
// src/Api/ExternalApiEndpoints.cs
var group = app.MapGroup("/api/v1/external")
    .RequireAuthorization(policy => policy.AddAuthenticationSchemes(ApiKeyDefaults.AuthenticationScheme).RequireAuthenticatedUser())
    .AddEndpointFilter<DeveloperModuleFilter>()
    .WithTags("External API");
group.MapExternalWhatsAppEndpoints();
```

Cada ruta de `MapExternalWhatsAppEndpoints` agrega `.RequireAuthorization(policy => policy.AddAuthenticationSchemes(ApiKeyDefaults.AuthenticationScheme).RequireClaim(ApiKeyClaimTypes.Scope, MessagingScopes.TemplatesSend))`: esa es la capa del endpoint, y `ExternalApiAccess.EnsureAsync` la del handler. `ListExternalTemplatesHandler` lee `IMessageTemplates.ListAsync(tenantId, connectionId, refresh: false, ct)` y filtra `Sendable`. Las dos rutas se devuelven con `.Produces<...>` y `.ProducesProblem`, para que queden en el documento OpenAPI.

- [ ] **Step 5: Corre y verifica GREEN**

```powershell
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ExternalApiAuthTests"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~RealAuthenticationApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests|FullyQualifiedName~MessagingLayerTests"
```

Esperado: PASS.

- [ ] **Step 6: Commit** `feat(api-access): authenticate external requests with api keys`

---

### Task 6: CSRF, límites de tasa y log de fallas

**Files:**
- Create: `src/Bootstrapper/ExternalApiPaths.cs`, `src/Bootstrapper/ApiKeys/ApiKeyRateLimitFilter.cs`, `src/Api/ExternalRateLimitRejection.cs`, `Modules.ApiAccess.Application/ApiAccessOptions.cs`
- Modify: `src/Bootstrapper/Csrf/RequireCsrfHeaderMiddleware.cs:58-61`
- Modify: `src/Api/RequestFailureCapture.cs:35-37`, `RequestFailureLoggingMiddleware.cs:43`, `ApiExceptionHandler.cs:118,150-178`
- Modify: `src/Api/Program.cs:74-120`, `RateLimiterPolicies.cs`, `ExternalApiEndpoints.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/RequestFailureCaptureTests.cs`, `ExternalApiSurfaceTests.cs`; `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/CorsApiTests.cs`

**Interfaces:**
- Produces:

```csharp
public static class ExternalApiPaths // misma regla que WebhookPaths: ordinal y con barra final
{ public const string Prefix = "/api/v1/external/"; public static bool IsExternal(PathString path); }

public sealed class ApiAccessOptions
{
    public const string SectionName = "ApiAccess";
    public ApiAccessRateLimitOptions RateLimit { get; set; } = new();
}
public sealed class ApiAccessRateLimitOptions { public int PerKeyPerMinute { get; set; } = 300; public int PerIpPerMinute { get; set; } = 600; }

public static class RateLimiterPolicies { public const string External = "external"; }

// cambia la firma y sus dos llamadores
public static bool ShouldCapture(HttpContext httpContext, int statusCode);

public sealed class ApiKeyRateLimitFilter(ApiKeyRateLimiter limiter, IApiCaller caller) : IEndpointFilter;
public sealed class ApiKeyRateLimiter(IOptions<ApiAccessOptions> options) : IDisposable; // singleton
```

- [ ] **Step 1: Escribe las pruebas que fallan**

`RequestFailureCaptureTests` pasa a recibir status, y agrega:

```csharp
[Theory]
[InlineData(401, false)] [InlineData(429, false)] [InlineData(403, true)] [InlineData(422, true)] [InlineData(502, true)]
public void TheExternalApiOnlySkipsAnonymousNoise(int status, bool expected) =>
    Assert.Equal(expected, ShouldCapture("POST", "/api/v1/external/whatsapp/messages", status));

[Fact] public void AnInternal401IsStillCaptured() => Assert.True(ShouldCapture("POST", "/api/v1/tenants/x/settings", 401));
[Fact] public void TheExternalPrefixIsOrdinalAndNeedsItsSlash() => Assert.True(ShouldCapture("POST", "/api/v1/externalx", 401));
```

`ExternalApiSurfaceTests`:

```csharp
[Fact] public async Task AnExternalPostNeedsNoCsrfHeaderWithTheStubOff();        // WithRealAuthentication: el 401 es el de la clave, no el 403 de CSRF
[Fact] public async Task TheExemptionDidNotWidenToInternalRoutes();              // POST interno sin X-Qep-Client con stub apagado: 403
[Fact] public async Task TheKeyLimitAnswers429WithCodeAndRetryAfter();           // PerKeyPerMinute=2; el tercero
[Fact] public async Task TheKeyLimitIsPerKey();                                  // otra clave del tenant sigue pasando
[Fact] public async Task TheIpLimitStopsInventedKeysWith429AndCode();            // PerIpPerMinute=3; ProblemDetails, Retry-After
[Fact] public async Task A401AndA429OnTheExternalApiWriteNoFailureRow();         // platform.request_failures vacío
[Fact] public async Task A403OnTheExternalApiWritesAFailureRow();
```

`CorsApiTests`: `ThePreflightDoesNotAllowXApiKey`, con `RequestedHeaders = "x-api-key"` y sin `Access-Control-Allow-Headers` que lo permita.

- [ ] **Step 2: Corre y verifica RED**

```powershell
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~RequestFailureCaptureTests|FullyQualifiedName~ExternalApiSurfaceTests"
```

Esperado: no compila, `ShouldCapture` no recibe status.

- [ ] **Step 3: Implementa**

- CSRF: agrega `|| ExternalApiPaths.IsExternal(context.Request.Path)` a la condición de `:58-61`, con el comentario de por qué es seguro (esas rutas no aceptan cookie).
- `ShouldCapture` suma `&& !(ExternalApiPaths.IsExternal(path) && statusCode is 401 or 429)`. `ApiExceptionHandler.RecordAsync` le pasa `status`, porque en ese punto `Response.StatusCode` todavía no está puesto (`:88-90`). El middleware pasa `httpContext.Response.StatusCode`.
- `ApiExceptionHandler.MapException`: `RequestRateLimitedException` → `(429, "Too many requests", value.Code)`; en `TryHandleAsync`, si trae `RetryAfter`, escribe `Retry-After` en segundos redondeados hacia arriba, antes de `TryWriteAsync`.
- `ApiKeyRateLimiter`: `PartitionedRateLimiter.Create<Guid, Guid>` con ventana fija de 1 minuto. El filtro hace `AcquireAsync` y, sin permiso, lanza `RequestRateLimitedException(ExternalApiErrorCodes.RateLimited, ..., retryAfter)` con `MetadataName.RetryAfter`.
- `Program.cs`: política `External`, ventana fija por `RemoteIpAddress`, con el valor de `IOptions<ApiAccessOptions>` como en `Webhook`. `options.OnRejected = ExternalRateLimitRejection.WriteAsync`: bajo `/api/v1/external/` escribe ProblemDetails con `code` y `Retry-After`; en otra ruta no hace nada y queda el 429 pelado de hoy.
- `ExternalApiEndpoints`: `.RequireRateLimiting(RateLimiterPolicies.External).AddEndpointFilter<ApiKeyRateLimitFilter>()` antes de `DeveloperModuleFilter`.
- Registra `ApiKeyRateLimiter` singleton y bindea `ApiAccessOptions` en `AddQepPlatform`, sin `ValidateOnStart` obligatorio: no hay configuración nueva en `Backend-prod`.

Particiona por IP y nunca por el header: una partición por clave falsa es memoria gratis para quien ataca.

- [ ] **Step 4: Corre y verifica GREEN**

El comando del Step 2 y `dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~CorsApiTests"`. Esperado: PASS.

- [ ] **Step 5: Commit** `feat(api-access): exempt external routes from csrf and rate limit them`

---

### Task 7: Autor `ApiKey` en Messaging

**Files:**
- Modify: `Modules.Messaging.Domain/MessageEnums.cs`
- Modify: `Modules.Messaging.Infrastructure/Persistence/MessagingDbContext.cs:61-67,314-316,338,363-364`, `MessageRecord.cs`, `MessageQueries.cs:11-15`, `OutboundMessages.cs`, `MessagingReportSource.cs:83-88`
- Modify: `Modules.Messaging.Application/IOutboundMessages.cs`, `IMessageQueries.cs:7-23`, `MessagingDtos.cs:148-175`, `MessagingSupport.cs:212-245`, `OutboundGuards.cs:208-218`, `IMessageTemplates.cs:67-91`
- Modify: `Modules.Messaging.Infrastructure/Persistence/ConversationOpener.cs`
- Create: migración `AddApiKeyAuthor`
- Modify dobles: `tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateTestDoubles.cs`, `MessagingTestBed.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MessageAuthorshipApiTests.cs`, `CoexistenceReportTests.cs` o `MessagingReportApiTests.cs`

**Interfaces:**
- Produces:

```csharp
public enum MessageAuthorKind { Member = 1, Chatbot = 2, ApiKey = 4 } // 3 reservado para agentes IA

public sealed record OutboundApiKeyAuthor(Guid Id, string Name);
// OutboundDraft: agrega al final OutboundApiKeyAuthor? SentByApiKey = null
// ExistingOutbound: agrega al final int? FailureCode = null, Guid? SentByApiKeyId = null, string? SentByApiKeyName = null
// MessageRow: agrega al final Guid? SentByApiKeyId = null, string? SentByApiKeyName = null
public sealed record ApiKeyRefDto(Guid Id, string Name);
// MessageDto: agrega al final ApiKeyRefDto? SentByApiKey = null, serializa sentByApiKey: { id, name }

public sealed record ExistingApiMessage(Guid Id, Guid ConversationId, Guid ConnectionId, string? WaId, MessageStatus Status, int? FailureCode, string? DetailsJson);
// IOutboundMessages
Task<ExistingApiMessage?> FindApiMessageAsync(Guid tenantId, Guid clientId, CancellationToken cancellationToken);
// IConversationOpener: sin escribir, misma búsqueda de OpenByPhoneAsync
Task<Guid?> FindByPhoneAsync(Guid tenantId, Guid connectionId, string waId, CancellationToken cancellationToken);

// MessagingDbContext
public const string SentByKindCheck = "sent_by_kind IN (1,2,4)";
public const string SentByOneCheck = "sent_by_kind NOT IN (2,4) OR sent_by_member_id IS NULL";
public const string SentByApiKeyCheck = "(sent_by_kind = 4) = (sent_by_api_key_id IS NOT NULL) AND (sent_by_api_key_id IS NULL) = (sent_by_api_key_name IS NULL)";
public const string ApiClientIndex = "IX_messages_tenant_api_client";
```

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
[Fact] public async Task AKindFourRowWithAMemberFailsInTheDatabase();     // INSERT crudo: 23514 CK_messages_sent_by_one
[Fact] public async Task AKindFourRowWithoutItsKeyFailsInTheDatabase();   // 23514 CK_messages_sent_by_api_key
[Fact] public async Task AKindOneRowWithAKeyFailsInTheDatabase();         // 23514 CK_messages_sent_by_api_key
[Fact] public async Task TheThreadSignsAnApiMessageWithTheKeyName();
// Fila kind 4 sembrada: GET messages trae sentByApiKey {id, name}, sentBy null, sentByChatbot null.
[Fact] public async Task ADraftNeedsExactlyOneAuthor();                   // miembro + clave: ArgumentException antes de tocar la tabla
[Fact] public async Task ARepeatedClientIdInAnotherConversationOfTheTenantFails();
// Dos ClaimAsync kind 4 con mismo clientId y distinta conversación: ResourceConflictException con code external.client_id_reused.
[Fact] public async Task TheSameClientIdInAnotherTenantIsAllowed();
```

Reporte:

```csharp
[Fact] public async Task AnApiMessageIsNotAFirstResponseButItsConversationIsASession();
// Entrante en vivo + saliente kind 4 + saliente de asesor: FirstAnyOutboundAt es el del asesor;
// conversación nueva con solo kind 4 suma una sesión (D13).
```

- [ ] **Step 2: Corre y verifica RED**

```powershell
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~MessageAuthorshipApiTests|FullyQualifiedName~MessagingReportApiTests"
```

Esperado: no compila, `SentByApiKey` no existe.

- [ ] **Step 3: Implementa**

- `AuthorKindOf`: cuenta los autores no nulos entre miembro, chatbot y clave, exige exactamente uno y devuelve el kind.
- `ClaimAsync`, `CommitSentAsync`, los dos `SELECT` de `ExistingRow` y `ToExisting` llevan `sent_by_api_key_id`, `sent_by_api_key_name` y `failure_code`. Sin `failure_code` no se distingue un `Failed` sin confirmar en el reclamo (Tarea 9).
- En `ClaimAsync`, antes del `catch` general: `catch (PostgresException exception) when (exception is { SqlState: "23505", ConstraintName: MessagingDbContext.ApiClientIndex })` desecha la transacción y lanza `ResourceConflictException(MessagingErrorCodes.ExternalClientIdReused, ...)`. Agrega el código a `MessagingErrorCodes`.
- `FindApiMessageAsync`: `messages` unido a `conversations`, `WHERE m.tenant_id = @t AND m.client_id = @c AND m.sent_by_kind = 4`, sin candado.
- `ConversationOpener.FindByPhoneAsync` envuelve el `internal static` que ya existe.
- `MessageQueries.RowProjection` trae las columnas. `MessageMapping.ToDto` arma `ApiKeyRefDto`. `OutboundGuards.ExistingRow` las pasa.
- `MessagingReportSource.fo` agrega `AND o.sent_by_kind <> 4`. Actualiza el `<summary>` de la clase.
- Implementa los dos métodos nuevos en los dobles de pruebas unitarias.

```powershell
dotnet ef migrations add AddApiKeyAuthor --project src/Modules/Messaging/Modules.Messaging.Infrastructure --context MessagingDbContext -o Persistence/Migrations
```

Revisa que la migración reemplace `CK_messages_sent_by_kind` y `CK_messages_sent_by_one`, cree `CK_messages_sent_by_api_key` e `IX_messages_tenant_api_client` (`(tenant_id, client_id) WHERE sent_by_kind = 4`), y agregue las dos columnas nullable (`varchar(80)`). Es sobre `messaging.messages`: nada de `DEFAULT` ni backfill.

- [ ] **Step 4: Corre y verifica GREEN**

Comando del Step 2 más `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~SendTemplate"`. Esperado: PASS.

- [ ] **Step 5: Commit** `feat(messaging): add api key as a message author`

---

### Task 8: Reglas puras del envío externo

**Files:**
- Create: `Modules.Messaging.Application/External/ExternalPhoneNumber.cs`, `ExternalRecipientPolicy.cs`, `ExternalSendFailure.cs`
- Modify: `MessageTemplates.cs:34-76` (`TemplateDetailsJson`), `Modules.Messaging.Domain/MessagingErrorCodes.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/ExternalPhoneNumberTests.cs`, `ExternalRecipientPolicyTests.cs`, `ExternalSendFailureTests.cs`, `TemplateDetailsJsonTests.cs`

**Interfaces:**
- Produces:

```csharp
// MessagingErrorCodes
ExternalRecipientBlocked = "external.recipient_blocked"; ExternalRecipientOptedOut = "external.recipient_opted_out";
ExternalRecipientInactive = "external.recipient_inactive"; ExternalMessageRejected = "external.message_rejected";
ExternalSendUnconfirmed = "external.send_unconfirmed";

public static class ExternalPhoneNumber { public static bool TryNormalize(string? raw, out string waId); }

// Owner 2026-10-30: el número se valida de verdad. Messaging no ve Customers: entra por puerto, con adaptador en Bootstrapper.
public interface IDialablePhoneNumbers { bool IsDialable(string waId); } // Messaging.Application/External
// src/Bootstrapper/DialablePhoneNumbers.cs, internal, sobre el IPhoneNumberNormalizer de Customers ya registrado:
//   IsDialable(waId) => normalizer.ToE164("+" + waId, "ZZ") is not null. Sin paquete nuevo ni cambios de lock files.

public sealed record StoredTemplateSend(string Name, string Language, IReadOnlyDictionary<string, string> Parameters);
// TemplateDetailsJson
public static StoredTemplateSend? ParseSend(string? detailsJson);

public static class ExternalRecipientPolicy
{   // null = sale. contacts son los de ListByPhoneAsync más conversation.CustomerId, sin repetidos.
    public static string? Evaluate(string category, bool blocked, IReadOnlyList<MessagingCustomerContact> contacts);
}

public static class ExternalSendFailure
{
    public static readonly TimeSpan MetaRetryAfter = TimeSpan.FromSeconds(60);
    public static Exception Map(SendResult result, MessagingDomainException thrown);
}
```

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
[Theory]
[InlineData("573001234567", "573001234567")] [InlineData("+57 300 123 4567", "573001234567")]
[InlineData("+57-300-123-4567", "573001234567")] [InlineData("12345678", "12345678")] [InlineData("123456789012345", "123456789012345")]
public void AcceptedForms(string raw, string expected);

[Theory]
[InlineData(null)] [InlineData("")] [InlineData("1234567")] [InlineData("1234567890123456")] [InlineData("(57) 3001234567")]
[InlineData("0573001234567")] [InlineData("00573001234567")] // Review Focus 5
[InlineData("٥٧٣٠٠١٢٣٤٥٦٧")] // dígitos no ASCII
public void RejectedForms(string? raw);
```

`DialablePhoneNumbersTests` en `tests/Bootstrapper/Bootstrapper.UnitTests`, contra el normalizador real:

```csharp
[Theory] [InlineData("573001234567", true)] [InlineData("3001234567", false)] // sin indicativo: leído como +30 (Grecia), no existe
public void OnlyARealInternationalNumberIsDialable(string waId, bool expected);
```

`ExternalRecipientPolicyTests`, una por fila de spec §6.3.1:

```csharp
[Fact] public void ABlockedConversationStopsEveryCategory();       // UTILITY y MARKETING => recipient_blocked
[Fact] public void AnInactiveCustomerOnlyStopsMarketing();
[Fact] public void AnOptedOutNumberOnlyStopsMarketing();
[Fact] public void NoCustomerGoesOutInBothCategories();
[Fact] public void SeveralMatchesOnlyStopMarketingOnOptOut();      // dos activos, uno con opt-out; dos sin opt-out con uno inactivo sale
[Fact] public void InactiveWinsOverOptOutWithASingleCustomer();    // misma precedencia de campañas
[Fact] public void TheCategoryIsComparedIgnoringCase();            // "marketing"
```

`ExternalSendFailureTests`:

```csharp
[Fact] public void UnconfirmedIsA502();          // SendOutcome.Unconfirmed => UpstreamServiceException external.send_unconfirmed
[Fact] public void NotSentIsA502();              // igual: la fila quedó con -1
[Fact] public void AWamidlessSuccessIsA502();    // Outcome Sent, Wamid null
[Fact] public void MetaOptOutIsRecipientOptedOut();
[Fact] public void ThroughputAndPairLimitsAre429(); // 130429 y 131056: RequestRateLimitedException con RetryAfter 60 s
[Fact] public void AnyOtherGraphErrorIsMessageRejected(); // 132001; y GraphError con Code null
[Fact] public void RejectedCredentialsStayConnectionUnavailable(); // thrown.Code == ConnectionUnavailable se devuelve tal cual
[Fact] public void MetaTitleNeverLeaks(); // el mensaje de la excepción no contiene result.Title
```

`TemplateDetailsJsonTests`: `ParseSendReadsWhatSerializeWrote` y `ParseSendIsNullOnGarbage`.

- [ ] **Step 2: Corre y verifica RED**

```powershell
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~ExternalPhoneNumberTests|FullyQualifiedName~ExternalRecipientPolicyTests|FullyQualifiedName~ExternalSendFailureTests|FullyQualifiedName~TemplateDetailsJsonTests"
```

Esperado: no compila.

- [ ] **Step 3: Implementa**

`TryNormalize` quita `+`, espacios y `-`, exige `char.IsAsciiDigit`, longitud 8 a 15 y primer dígito distinto de `0`. No adivina país. `Evaluate`: bloqueada → blocked; no `MARKETING` → `null`; con **un** contacto, inactivo y luego opt-out; con varios, solo opt-out; con ninguno, `null`. Marketing se compara con `PreviewAudienceHandler.MarketingCategory`, `OrdinalIgnoreCase`.

`Map` decide por `result` y no por `thrown`, porque la liquidación lanza `MessageRejected` tanto para el rechazo como para el sin confirmar (`OutboundSettlement.cs:69-78`):

```text
thrown.Code == ConnectionUnavailable           -> thrown
GraphError, Code == MarketingOptOut            -> MessagingDomainException(ExternalRecipientOptedOut)
GraphError, CampaignFailureCodes.IsRetriable   -> RequestRateLimitedException(RateLimited, MetaRetryAfter)
GraphError                                     -> MessagingDomainException(ExternalMessageRejected)
cualquier otro                                 -> UpstreamServiceException(ExternalSendUnconfirmed)
```

Mensajes en español: la `Detail` la lee un integrador, y nunca lleva `Title` de Meta.

- [ ] **Step 4: Corre y verifica GREEN**

Mismo comando. Esperado: PASS.

- [ ] **Step 5: Commit** `feat(messaging): add external send rules`

---

### Task 9: `SendExternalTemplateHandler`, endpoint e idempotencia

**Files:**
- Create: `Modules.Messaging.Application/External/SendExternalTemplate.cs`
- Modify: `Modules.Messaging.Api/ExternalWhatsAppEndpoints.cs`, `QepServiceCollectionExtensions.cs` (handler junto a `:826`)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ExternalSendApiTests.cs`, `tests/ArchitectureTests/ArchitectureTests/MessagingLayerTests.cs`

**Interfaces:**
- Consumes: `ExternalApiAccess`, `ExternalPhoneNumber`, `ExternalRecipientPolicy`, `ExternalSendFailure`, `FindApiMessageAsync`, `FindByPhoneAsync`, `TemplateDetailsJson.ParseSend`.
- Produces:

```csharp
public sealed record SendExternalTemplateCommand(string? ClientId, string? ConnectionId, string? To, string? TemplateName, string? TemplateLanguage, IReadOnlyDictionary<string, string?>? Parameters) : ICommand<ExternalMessageResult>;
public sealed record ExternalMessageResult(Guid MessageId, Guid ConversationId, string Status, bool Replayed);
public sealed class SendExternalTemplateValidator : AbstractValidator<SendExternalTemplateCommand>;
public sealed class SendExternalTemplateHandler(IApiCaller caller, ITenantModules tenantModules, IOutboundMessages outbound, IConversationQueries conversations,
    IConversationRepository repository, IConversationOpener opener, IMessagingConnectionDirectory connections, IMessageTemplates templates,
    IMessagingCustomerDirectory customers, IWhatsAppCloudClient meta, IMessagingNotifier notifier, ReadReceipts receipts, IClock clock,
    IValidator<SendExternalTemplateCommand> validator) : ICommandHandler<SendExternalTemplateCommand, ExternalMessageResult>;

// Modules.Messaging.Api
public sealed record ExternalSendMessageRequest(string? ClientId, string? ConnectionId, string? To, ExternalTemplateRequest? Template);
public sealed record ExternalTemplateRequest(string? Name, string? Language, IReadOnlyDictionary<string, string?>? Parameters);
public sealed record ExternalSendMessageResponse(Guid MessageId, Guid ConversationId, string Status);
```

- [ ] **Step 1: Escribe las pruebas que fallan**

Fija una plantilla `UTILITY` en `messaging.message_templates`, como `SendTemplateApiTests`, y un guion OK como `ScriptMetaOk`.

```csharp
[Fact] public async Task SendsIntoANewOpenUnassignedConversationAndSignsWithTheKey();
// 201 {messageId, conversationId, status:"Sent"}; conversación Open, sin asignar; sent_by_kind 4 y nombre de la clave; unread_count intacto.
[Fact] public async Task AnExistingConversationKeepsItsStatusAndAssignee(); // Resolved y asignada: sigue igual
[Fact] public async Task ACustomerIsLinkedOnlyWithASingleMatch();           // una coincidencia liga; dos dejan customer_id NULL; ninguna no crea cliente
[Fact] public async Task ATemplateProblemLeavesNoEmptyThread();             // not_available y parameters_mismatch: 422 y cero conversaciones
[Fact] public async Task AnUnusableConnectionIs422ConnectionUnavailable();  // pausada: messaging.connection_unavailable y cero conversaciones
[Fact] public async Task ANumberOutsideTheKeyIs403();                       // external.connection_not_allowed
[Fact] public async Task AnInstagramConnectionIs422ChannelUnsupported();
[Fact] public async Task MissingFieldsAre422WithTheirKeys();                // clientId, connectionId, to, template.name, template.language
[Fact] public async Task ANonUuidClientIdIs422();
[Fact] public async Task ABadParameterValueIs422WithItsKey();               // salto de línea: errors["template.parameters.1"]
[Fact] public async Task ABrokenBodyIs422WithCode();                        // "{no json" (Review Focus 3)
[Fact] public async Task ANumericParameterIs422WithCode();                  // {"2": 4821} (Review Focus 3)
[Fact] public async Task ATemplateWithoutVariablesNeedsNoParameters();      // template.parameters ausente: 201

// Idempotencia
[Fact] public async Task RepeatingTheSameRequestAnswers200WithoutCallingMetaAgain();
[Fact] public async Task ReplayWinsOverEverythingThatChangedSince();
// Conversación bloqueada y plantilla borrada después del envío: sigue 200 con lo guardado.
[Fact] public async Task ARotatedKeyReplaysToo();                           // otra clave del tenant, mismo clientId: 200
[Fact] public async Task AnotherTenantCanUseTheSameClientId();
[Fact] public async Task TheSameClientIdWithAnotherNumberIs409AndOpensNoThread();
[Fact] public async Task TheSameClientIdWithAnotherTemplateIs409();
[Fact] public async Task TheSameClientIdWithOtherParametersIs409();
[Fact] public async Task AParallelRequestWithAnotherNumberProducesOneSend();
// Meta retiene el primero con un TaskCompletionSource: el segundo acaba 409, un POST a /111/messages.
[Fact] public async Task AParallelRepeatProducesOneSend();                  // 201 y 200
[Fact] public async Task AParallelRepeatOfAnUnconfirmedSendDoesNotResend(); // Review Focus 1: 502 y 502, una sola llamada
[Fact] public async Task AClientIdOfAnAdvisorMessageIs409NotAReplay();      // misma conversación: no se devuelve un mensaje ajeno
```

`MessagingLayerTests.ExternalHandlersReadTheCallerNotTheExecutionContext` incluye este handler.

- [ ] **Step 2: Corre y verifica RED**

```powershell
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ExternalSendApiTests"
```

Esperado: no compila, `SendExternalTemplateCommand` no existe.

- [ ] **Step 3: Implementa el validador y el endpoint**

Validador: `clientId` y `connectionId` obligatorios y UUID no vacíos (`Must(Guid.TryParse)`), `to` con `ExternalPhoneNumber.TryNormalize` y luego `IDialablePhoneNumbers.IsDialable` (el validador recibe el puerto; un `to` sin indicativo da 422 `validation.failed` con el campo `to`, y las pruebas de integración usan números reales como `573001234567`), `template.name` y `template.language` con los máximos de `SendTemplateMessageValidator`. Valores por `TemplateParameterRules.Check` sobre `Parameters ?? {}`, con cada `PropertyName` prefijado `template.`.

El endpoint no enlaza el cuerpo por parámetro: lee con `JsonSerializer.DeserializeAsync<ExternalSendMessageRequest>(request.Body, JsonSerializerOptions.Web, ct)`. Un `JsonException` o un cuerpo nulo lanzan `ValidationException([new ValidationFailure("body", "El cuerpo debe ser JSON válido, con parameters como texto.")])`. Mapea `Replayed` a 200 y lo demás a 201.

- [ ] **Step 4: Implementa el handler en el orden de spec §6.3**

```text
1  validator; ExternalApiAccess.EnsureAsync; EnsureConnectionAsync
2  FindApiMessageAsync(tenant, clientId) => existing
   existing con otra conexión u otro waId                         -> 409 ExternalClientIdReused
   existing no Failed: misma plantilla, idioma y valores -> replay; si no, 409
   existing Failed y FailureCode == Unconfirmed: lo mismo, pero en vez de replay -> 502 ExternalSendUnconfirmed
   existing Failed por otra causa                                 -> sigue: se reenvía
3  ResolveSenderWithReasonAsync: sin Sender -> ConnectionUnavailable
   templates.ListAsync(refresh: false) + RequireSendable + BuildSend
4  ListByPhoneAsync(tenant, waId, take: 2); una sola coincidencia -> ResolveSurvivorAsync
5  FindByPhoneAsync + conversations.FindAsync: blocked y CustomerId de la existente
   contacts = FindContactAsync de las coincidencias más conversation.CustomerId, sin repetidos ni nulos
   ExternalRecipientPolicy.Evaluate(template.Category, blocked, contacts) -> MessagingDomainException(code)
6  OpenByPhoneAsync(tenant, connection, waId, customerId, nombre si hay una coincidencia, now)
   conversations.FindAsync
7  OutboundDraft(... SentByMemberId: null, Kind: Template, DetailsJson: built.DetailsJson, SentByApiKey: new(caller.ApiKeyId, caller.ApiKeyName))
   await using claim = ClaimAsync(draft, cancellationToken)
   !claim.Inserted:
     existing.SentByApiKeyId nulo -> Rollback, 409
     existing no Failed           -> Rollback; misma plantilla: replay; si no, 409
     Failed y Unconfirmed         -> Rollback, 502
8  desde acá todo con CancellationToken.None
   result = meta.SendTemplateAsync(sender, SendTarget.ForTemplate(conv.UserId, conv.WaId), built.Send, CallbackPrefix + claim.MessageId, None)
   GraphError con MarketingOptOut y conversation.CustomerId ?? customerId no nulo -> MarkMarketingOptOutAsync
   try new OutboundSettlement(...).SettleAsync(..., metaMediaId: null, afterRollback: null, seen: 0)
   catch (MessagingDomainException e) -> throw ExternalSendFailure.Map(result, e)
```

`existing` y `claim.Existing` usan el mismo helper privado `SameTemplate(string? detailsJson, command)`, que compara con `ParseSend` en ordinal. El paso 2 no abre conversación: un `clientId` repetido con otro número nunca deja hilo vacío. `Status` del replay es `existing.Status.ToString()`. Sin usar `TemplateOutbound.ResolveAsync` ni `OutboundGuards`: ver Discrepancias.

- [ ] **Step 5: Corre y verifica GREEN**

```powershell
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ExternalSendApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests|FullyQualifiedName~MessagingLayerTests"
```

Esperado: PASS.

- [ ] **Step 6: Commit** `feat(messaging): send templates through the external api`

---

### Task 10: Matriz del destinatario y respuestas de Meta

**Files:**
- Modify: `Modules.Messaging.Application/External/SendExternalTemplate.cs` solo si una prueba lo exige
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ExternalSendRulesApiTests.cs`

**Interfaces:**
- Consumes: Tarea 9. No produce nada nuevo: esta tarea existe para que el reviewer rechace o apruebe el comportamiento contra spec §6.3.1 y §6.3.2 por separado.

- [ ] **Step 1: Escribe las pruebas**

```csharp
// §6.3.1, con plantilla UTILITY y otra MARKETING
[Fact] public async Task ABlockedConversationIs422ForEveryCategory();       // y cero POST a /111/messages
[Fact] public async Task AnInactiveCustomerOnlyStopsMarketing();
[Fact] public async Task AnOptedOutNumberOnlyStopsMarketing();
[Fact] public async Task ANumberWithoutCustomerGoesOutInBothCategories();
[Fact] public async Task SeveralMatchesWithAnOptOutStopMarketing();          // customer_id queda NULL en la no marketing
[Fact] public async Task ALinkedCustomerWithAnotherPhoneStillStopsMarketing(); // Review Focus 4
[Fact] public async Task ANonMarketingOptedOutSendStaysInTheInbox();

// §6.3.2
[Fact] public async Task ACertainRejectionIs422AndTheSameClientIdResends();
// Meta 400 132001, luego OK: 422 external.message_rejected, luego 201; una sola fila en messages.
[Fact] public async Task AnUnconfirmedSendIs502AndNeverResends();
// Meta 500: 502 external.send_unconfirmed; repetir da 502; un POST a /111/messages.
[Fact] public async Task ATimeoutIsAlso502();                                // FakeMetaGraphHandler.Throw con TaskCanceledException
[Fact] public async Task AMetaWithoutWamidIsAlso502();
[Fact] public async Task AnUnconfirmedRowCorrectedByTheStatusWebhookReplays200();
[Fact] public async Task MetaRateLimitIs429WithRetryAfterAndResends();       // 130429: 429, luego 201
[Fact] public async Task MetaOptOutIs422AndMarksTheCustomer();               // 131050, customers.marketing_opt_out = true
[Fact] public async Task MetaOptOutWithoutCustomerMarksNobody();
[Fact] public async Task RejectedCredentialsAre422AndLeaveNoRow();           // 190: messaging.connection_unavailable, conexión NeedsAttention
```

- [ ] **Step 2: Corre**

```powershell
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ExternalSendRulesApiTests"
```

Las que queden en rojo son defectos reales de la Tarea 9. Registra la salida literal. Si todas pasan al primer intento, comprueba al menos dos a mano rompiendo la regla: quita `ExternalRecipientPolicy.Evaluate` y confirma que `ABlockedConversationIs422ForEveryCategory` falla; quita la rama `Unconfirmed` del paso 2 y confirma que `AnUnconfirmedSendIs502AndNeverResends` falla. Restaura.

- [ ] **Step 3: Corrige lo que haya salido en rojo**

Solo en `SendExternalTemplate.cs`, sin cambiar las reglas de la Tarea 8.

- [ ] **Step 4: Corre y verifica GREEN**

Mismo comando. Esperado: PASS.

- [ ] **Step 5: Commit** `test(messaging): pin external recipient rules and meta outcomes`

---

### Task 11: Secreto, aislamiento y documentación

**Files:**
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ExternalApiEndToEndTests.cs`
- Modify: `README.md` (§ Módulos por tenant, tabla de configuración, nueva § «API externa» antes de `## Verificación`)
- Modify: `CLAUDE.md`

**Interfaces:**
- Consumes: todo lo anterior. `ApiKeyConnectionDirectory` real, sin dobles.

- [ ] **Step 1: Escribe las pruebas que fallan o amarran**

```csharp
/// Patrón TheKeyNeverAppearsInLogsAuditOrHttpCalls: host en Trace con CapturedLogs.
[Fact] public async Task TheSecretNeverAppearsInLogsAuditOrRequestFailures();
// Crea por POST admin; envía OK, envía 422, envía con clave de secreto cambiado y revoca.
// Controles positivos: LogLevel.Trace en Levels, audit.entries > 0, request_failures > 0.
// Assert.DoesNotContain(plaintext y secreto) en AllText y en audit.entries, platform.request_failures,
// api_access.api_keys (row_to_json) y messaging.messages.

[Fact] public async Task AKeyCreatedInTheAdminWorksEndToEnd();
// POST admin con la conexión real -> GET connections -> GET templates -> POST messages -> GET del hilo con sentByApiKey.
[Fact] public async Task AKeyOfTenantACannotTouchTenantB();
// connection_ids NULL, conexión de B: 403; clientId de B no hace replay.
[Fact] public async Task TurningMessagingOffAndOnKeepsTheKeyWorking();  // 403 module_not_enabled, luego 200, sin recrear
[Fact] public async Task OpenApiDescribesTheExternalRoutes();           // /openapi/v1.json trae las tres rutas
```

- [ ] **Step 2: Corre**

```powershell
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ExternalApiEndToEndTests"
```

Esperado: PASS salvo defecto real. Si la primera pasa, verifica que falla agregando temporalmente un log del header; restaura.

- [ ] **Step 3: Escribe la documentación**

README, § «API externa»: claves y scopes, formato, autenticación (`X-Api-Key`), los tres endpoints con ejemplos en PowerShell (`curl.exe`, cuerpo en archivo, `-d "@cuerpo.json"`), tabla de errores de spec §5.3 más `messaging.connection_unavailable`, `messaging.templates.unavailable` y `messaging.channel_unsupported`, y una advertencia en negrilla: `external.send_unconfirmed` significa «no sé si salió», y mandar otro exige `clientId` nuevo. Anota que los dos límites viven en memoria, así que con más réplicas el límite efectivo se multiplica. Agrega `ApiAccess:RateLimit:PerKeyPerMinute` y `PerIpPerMinute` a la tabla de configuración, y `developer` a Módulos por tenant.

`CLAUDE.md`, en Gotchas, tres entradas breves:

- `ApiAccess` entra en la lista de módulos, con su `ApiAccessLayerTests`.
- La API externa no usa `IExecutionContext` sino `IApiCaller`, el tenant sale de la clave, un scope nuevo se registra con `ApiScopeDefinition` en DI y sus rutas cuelgan del grupo de `ExternalApiEndpoints`.
- Un camino nuevo que lea autores de `messaging.messages` tiene que tratar `sent_by_kind = 4`, y uno que muestre «primera respuesta» debe excluirlo.

- [ ] **Step 4: Commit** `docs(api-access): document the external api`

---

### Task 12: Cierre: suite completa y verificación contra Meta

**Files:** ninguno, salvo correcciones que salgan de acá.

- [ ] **Step 1: Comprueba restauración bloqueada**

```powershell
dotnet restore Backend.slnx --locked-mode
```

Esperado: exit 0. Un `NU1004` significa lock files sin regenerar.

- [ ] **Step 2: Corre la suite completa, una sola vez**

```powershell
$env:Seed__Enabled = "false"
$results = Join-Path $env:TEMP "api-keys-results"
Remove-Item -Recurse -Force $results -ErrorAction SilentlyContinue
dotnet test Backend.slnx --logger "trx;LogFilePrefix=api-keys" --results-directory $results
```

Espera a que termine en primer plano. Saca las fallas por nombre, con `-LiteralPath` porque hay `.trx` con corchetes:

```powershell
$failed = Get-ChildItem -LiteralPath $results -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -LiteralPath $_.FullName -Raw
    $trx.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' } | ForEach-Object { $_.testName }
} | Sort-Object -Unique
$failed
```

- [ ] **Step 3: Compara con la base por nombre**

Si `$failed` no está vacío, corre solo esos nombres en la base, en un worktree temporal bajo `...\qep-backend-worktrees\api-keys-base` sobre `501243f5`. Una falla presente en las dos no es regresión y se reporta. Una falla solo en `feature/api-keys` se corrige en esta rama. Borra el worktree temporal al terminar.

- [ ] **Step 4: Verifica una vez contra Meta real, en local**

Spec §7: no se puede en CI. Con la app de desarrollo (`AppId 1802870060852619`), API local con auth real, un número de prueba y una plantilla aprobada, sigue el ejemplo del README en PowerShell y confirma: 201, llegada al teléfono, firma en la bandeja, repetir da 200 sin segundo mensaje, y responder desde el teléfono sigue el camino normal. Pega la salida en el reporte, **con la clave enmascarada**.

- [ ] **Step 5: Revisa que nada se filtró**

```powershell
git diff 501243f5 --name-only | Select-String -Pattern "docs/estado.md|BillingTemplateOutbound|\.mcp\.json"
git log 501243f5..HEAD --format="%H %s%n%b" | Select-String -Pattern "Co-Authored-By|Generated with"
```

Esperado: las dos sin resultados.

---

## Despliegue

Backend antes que frontend. No hay configuración obligatoria nueva en `Backend-prod`. Migraciones: Tenancy `AddDeveloperModuleKey`, ApiAccess `InitialApiAccess`, Messaging `AddApiKeyAuthor`. Con backend nuevo y frontend viejo, un mensaje de API sale sin firma y no rompe nada. El operador prende `developer` por tenant desde su consola.

## Discrepancias con el spec

| Spec | Código | Resolución |
| --- | --- | --- |
| §2: `TemplateOutbound.ResolveAsync` es de lo genérico reutilizable | `TemplateOutbound.cs:13` exige `OutboundGuards`, y esta exige `IExecutionContext` (`OutboundGuards.cs:31`) | Se llama `IMessageTemplates.ListAsync` + `TemplateCatalog.RequireSendable` directo. Solo se reusa `EnsureWhatsApp`, que es estático |
| §6.3 paso 3: `ResolveSenderWithReasonAsync` hace caer Instagram como `channel_unsupported` | `MessagingConnectionDirectory.cs:42-45` responde `wrong_provider` | `FindChannelAsync` + `TemplateOutbound.EnsureWhatsApp` en `EnsureConnectionAsync`. `FindChannelAsync` nulo da 403 `external.connection_not_allowed` |
| §5.3 pide confirmar los `code` | Existen `messaging.template.not_available`, `messaging.template.parameters_mismatch` (`MessagingErrorCodes.cs:45,48`) y `tenancy.module_not_enabled` (`TenantModuleGuard.cs:14`). No existe ninguno `external.*` | Los que usan dos módulos (auth, conexión y límite) en `ExternalApiErrorCodes` de BuildingBlocks: ApiAccess no ve Messaging. El resto en `MessagingErrorCodes`. Se suman al contrato `messaging.connection_unavailable`, `messaging.templates.unavailable` y `messaging.channel_unsupported`, todos 422 |
| §6.3.2: distinguir rechazo de sin confirmar | La liquidación lanza `MessageRejected` en los dos casos (`OutboundSettlement.cs:69-78`). `MessageFailureReasons.Unconfirmed` es la constante `-1`, solo en `failure_code` | En vivo se decide por `SendResult` antes de mirar la excepción (`ExternalSendFailure.Map`). En reintentos, por `failure_code`, que `ExistingOutbound` y `ExistingApiMessage` ahora traen |
| §6.3.2: «timeout, 5xx, 2xx sin id» | El cliente también devuelve `SendOutcome.NotSent` (`WhatsAppCloudClient.cs:358`), que la liquidación guarda como `-1` (`:74-78`) | Se trata como sin confirmar: 502 sin reenvío. Es conservador; `IOutboundClaim` no ofrece cerrarlo de otra forma sin tocar la liquidación |
| §6.3.2: «Meta limita (130429)» | `CampaignFailureCodes.IsRetriable` también incluye `131056` (`CampaignFailureReasons.cs:34`) | Los dos dan 429. `Retry-After` es 60 s fijo: Meta no lo informa |
| §2: «campañas lee opt-out por número» (`CampaignDispatch.cs:293,492`) | Campañas lee por `customerId` con `FindContactAsync` (`:290`). El dato sí es del número: `ProjectContact` lo calcula para todo `phone_e164` del tenant (`CustomerAudienceRepository.cs:119-122`) | `ListByPhoneAsync(take: 2)` y luego `FindContactAsync` de cada uno. `take: 2` basta: distingue cero, una y varias, y el opt-out ya es del número. `ListByPhoneAsync` omite fusionados (`CustomerPhoneDirectory.cs:74`) |
| §4.3: `ExternalClaimsTransformation` no toca el principal con `qep_sub` y `tenant_id` | Confirmado (`:52-61`), con una condición: busca en `identity`, no en todo el principal | Los dos claims van en la misma y única `ClaimsIdentity`. No hay cambio en la transformación |
| §6.4: extender `ShouldCapture` | Firma `ShouldCapture(HttpContext)` sin status, y `ApiExceptionHandler` la llama antes de fijar `Response.StatusCode` (`:88-90`) | Nueva firma `ShouldCapture(HttpContext, int statusCode)` en los dos llamadores y en `RequestFailureCaptureTests` |
| §5.3: 429 es ProblemDetails con `code` | `MapException` no tiene 429 (`ApiExceptionHandler.cs:150-178`) y el limitador responde sin cuerpo (`Program.cs:76`) | `RequestRateLimitedException` nueva y `OnRejected` solo para la ruta externa |
| §6.1: solo `PerKeyPerMinute` es configurable | Probar el límite por IP con 600 reales exige 601 requests | `PerIpPerMinute`, opcional y con 600 por defecto |
| §4.1: secreto de 32 bytes en base62 | Convertir bytes a base62 sesga el primer carácter sin BigInteger | `RandomNumberGenerator.GetString` con 43 caracteres, 256 bits, uniforme |
| Mapeo: `ListNamesAsync` es «solo whatsapp-cloud» según su comentario (`IMessagingConnectionDirectory.cs:56`) | También concatena Instagram (`MessagingConnectionDirectory.cs:120-123`) | El adaptador usa `ListAllAsync` y filtra `Channel == WhatsApp` |
| §5.3: 403 `tenancy.module_not_enabled` con `developer` apagado | Aplica a las rutas externas. En las de administración, `RequiredModules` hace que `ModuleEntitlementMask` quite el permiso y la política dé 403 sin `code` (`QepServiceCollectionExtensions.cs:2050-2052`) | Se acepta en administración, como en billing: la capa del SPA es `ModuleGate`. Con stub, `ApiAccessAuthorization` responde el código |

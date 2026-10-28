# Registro y login con contraseña — backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Sumar email + contraseña como segunda vía para registrarse (crear empresa), iniciar sesión, recuperar o crear la contraseña y aceptar invitaciones, sin que ninguna contraseña dé acceso a una cuenta cuyo email no se probó y sin revelar si un email existe.

**Architecture:** Identity gana tres piezas de dominio (`User.EmailVerifiedAt`, `PasswordCredential`, `EmailToken`), un `IPasswordHasher` propio (Application) implementado sobre `PasswordHasher<User>` (Infrastructure), y servicios de Application para registrar, verificar, iniciar sesión, recuperar y aceptar invitaciones; los mails salen por el outbox de plataforma hacia tres workers nuevos de Notifications, con `Notification.TenantId` nullable. La composición entre Identity y Tenancy (registro, invitaciones, sesión) vive en `src/Api`, con un `SessionEstablisher` compartido extraído de `AuthSessionEndpoints.EstablishAsync` para que `/auth/session`, `/auth/email/verify`, `/auth/password/login` y `accept-with-password` respondan el mismo cuerpo.

**Tech Stack:** .NET 10 (`global.json` 10.0.400), ASP.NET Core minimal APIs, EF Core 10 + Npgsql, FluentValidation 12.1.1, `Microsoft.Extensions.Identity.Core` 10.0.13 (sólo `PasswordHasher<TUser>`), xUnit v3, Testcontainers.PostgreSql 4.14.0 (`postgres:18-alpine`), `WebApplicationFactory<Program>`.

**Spec:** [`docs/superpowers/specs/2026-10-27-registro-y-login-con-contrasena-design.md`](../specs/2026-10-27-registro-y-login-con-contrasena-design.md)

---

## Antes de empezar

- Rama: el trabajo va en `feature/registro-con-contrasena`, abierta desde `develop`, en el worktree `qep-backend-worktrees/registro-con-contrasena`. `git branch --show-current` tiene que decir `feature/registro-con-contrasena`; si dice `main` u otra, frenar y preguntar. Ese chequeo se repite antes de cada commit.
- Los commits van con Conventional Commits y **sin** atribución de IA ni `Co-Authored-By`.
- Si `Api.exe` está corriendo, `dotnet build`, `dotnet test` y `dotnet ef` fallan por archivo bloqueado: `Stop-Process -Name Api -ErrorAction SilentlyContinue`.
- Las pruebas de integración necesitan Docker y tardan minutos (cada clase levanta su Postgres).
- Toda línea de comando de este plan es PowerShell: nada de `&&`; una línea por comando.
- Cambiar un `.csproj` (paquete o referencia de proyecto) obliga a `dotnet restore --force-evaluate` y a commitear los `packages.lock.json` tocados junto con el cambio (el `Dockerfile` restaura en `--locked-mode`).
- Las migraciones se generan con el factory de diseño, sin `--startup-project`:
  `dotnet ef migrations add <Nombre> --project src/Modules/<Modulo>/Modules.<Modulo>.Infrastructure --context <Modulo>DbContext -o Persistence/Migrations`.

## Global Constraints

- Contraseña: mínimo **10** caracteres, máximo **128**, sin reglas de composición (NIST 800-63B).
- Hash: `Microsoft.AspNetCore.Identity.PasswordHasher<User>` detrás de `IPasswordHasher` (Application) implementado en Infrastructure; no se instala ASP.NET Core Identity completo. `SuccessRehashNeeded` → re-hash al iniciar sesión.
- Validación de contraseña con FluentValidation: código `validation.failed` con `errors.Password` (y `errors.Email` para el email). La clave es el `PropertyName` de FluentValidation en PascalCase, porque `ApiExceptionHandler.cs:66` agrupa por `PropertyName`; ningún validador usa `OverridePropertyName` (D10).
- Token de email: 32 bytes aleatorios, base64url en el link; en la base sólo el SHA-256 (`token_hash` único).
- Vigencia del token `verify`: **24 h**. Vigencia del token `reset`: **1 h**. Un solo uso (`used_at`).
- Emitir un token nuevo de un propósito invalida los anteriores no usados del mismo usuario y propósito.
- Token inexistente, vencido o usado → 422 `identity.email_token.invalid`, sin distinguir cuál.
- Rate limit `RateLimiterPolicies.Credentials`: **10 por minuto por IP y por endpoint**, cada endpoint con su propio contador.
- Todos los endpoints nuevos son anónimos, bajo `/api/v1/auth` (salvo `accept-with-password`), con CSRF (`X-Qep-Client`) y CORS como el resto de los mutantes.
- `POST /auth/password/register-tenant` → **202** `{ }` siempre que el cuerpo sea válido; orden fijo antes de mirar el email: validar cuerpo (422 `validation.failed`) → slug libre (422 con el código de dominio de hoy) → hash de la contraseña.
- `POST /auth/email/verify` → **200** con el cuerpo de `/auth/session` y cookie.
- `POST /auth/password/login` → **200** con el cuerpo de `/auth/session` y cookie; email inexistente, sin credencial o contraseña mala → **401** `identity.credentials.invalid` (con verificación contra un hash ficticio si el email no existe); correcta y sin verificar → **403** `identity.email.not_verified` y se reemite el `verify`.
- `POST /auth/password/forgot` → **202** siempre.
- `POST /auth/password/reset` → **204**; revoca todas las sesiones del usuario (`RevokeAllForUserAsync`); no abre sesión.
- `POST /invitations/{token}/accept-with-password` → **200** con el cuerpo de `/auth/session` y cookie; contraseña existente mala → 401 `identity.credentials.invalid`; **nunca** reemplaza una credencial existente.
- `GET /invitations/{token}` suma `hasPassword: bool`.
- Base de los links: `Notifications:AppBaseUrl` (`http://localhost:3002` en `appsettings.json`), con validador; no se deriva de `InvitationUrl`.
- Links: `{AppBaseUrl}/verify-email?token=…`, `{AppBaseUrl}/reset-password?token=…`, `{AppBaseUrl}/login` y `{AppBaseUrl}/forgot-password`.
- `Notification.TenantId` pasa a nullable; un mail sin tenant no aparece en ningún listado por tenant.
- `InvitationEmailTemplate.TemplateRef` sube de versión (`identity.invitation.v3` → `identity.invitation.v4`); el link no cambia.
- Auditoría con el auditor de Identity: `password.registered`, `email.verified`, `password.login.succeeded`, `password.login.failed` (sin el email crudo si no existe el usuario), `password.reset.requested`, `password.reset.completed`. Nunca contraseña ni token.
- Anti-objetivo: una contraseña que entra a una cuenta con email sin verificar.
- Anti-objetivo: una respuesta, código o tiempo de respuesta que distinga «el email existe» de «no existe».
- Anti-objetivo: un token o una contraseña en texto plano en la base, en un log o en la auditoría (excepción aceptada por el spec: el token crudo en el payload del outbox, mitigado por la vigencia corta).
- Vincular un proveedor a un usuario con `email_verified_at null` borra su credencial de contraseña e invalida sus tokens de email, en los dos caminos, desde un solo lugar.

## Decisiones de este plan (código manda sobre spec)

| # | Decisión | Por qué |
| --- | --- | --- |
| D1 | Las acciones de auditoría llevan el prefijo del módulo: `identity.password.registered`, `identity.email.verified`, `identity.password.login.succeeded`, `identity.password.login.failed`, `identity.password.reset.requested`, `identity.password.reset.completed`. | Convención vigente: `identity.session.issued` (`SessionService.cs:41`), `identity.user.deleted` (`OrphanUserCleanupTests.cs:72`). |
| D2 | En el registro con contraseña, `displayName`, `slug`, cultura, zona y formato de fecha siguen saliendo como **códigos de dominio** (`tenancy.slug.invalid`, `tenancy.settings.display_name.invalid`, …), no como `errors.*`. Sólo `email` y `password` van por FluentValidation (`errors.Email`, `errors.Password`, D10). | `/auth/register-tenant` no tiene validador hoy; esas reglas viven en `Tenant.ValidateSlug`/`ValidateDisplayName` (`Tenant.cs:310-335`). Para no enumerar, el chequeo de dominio corre **antes** de ramificar por email (Task 8). |
| D3 | Invitación inexistente → 404 `tenancy.invitation.not_found`; vencida → 422 `tenancy.membership.invitation_expired`; aceptada u otro estado → 422 `tenancy.membership.not_invited`. | El spec pide «el mismo error que el `GET`», pero el `GET` sólo falla con token desconocido; con vencida o aceptada responde 200 y `status` (`InvitationEndpoints.cs:42-55`, `InvitationApiTests.cs:122-125`). Los dos 422 son los de `Membership.Accept` (`Membership.cs:250-262`). |
| D4 | Identity escribe en `platform.outbox_messages` extendiendo su `OutboxRecord` (hoy proyección de sólo lectura) con `correlation_id`, `attempts` y `payload` como `jsonb`. | Dos entidades en la misma tabla sin relación de PK no las acepta EF; `correlation_id` y `attempts` son `NOT NULL` (`20260705161840_InitialPlatform.cs:54-57`). |
| D5 | Event names: `identity.email-verification-requested.v1`, `identity.password-reset-requested.v1`, `identity.existing-account-notice.v1`, en `IdentityIntegrationEvents` (Identity.Application, que Notifications.Infrastructure ya referencia). Payloads: `{ userId, token }` y `{ userId }`. | Formato de `OutboxWriter.cs` de Tenancy (`tenancy.membership-invited.v1`). El destinatario sale de `IUserDirectory`, como hoy. |
| D6 | El consumo de un token es una sentencia atómica (`UPDATE … WHERE used_at IS NULL AND expires_at > now RETURNING user_id`) que commitea sola, antes del resto de la unidad de trabajo. | Cierra la carrera de reuso. Si lo que sigue falla, el token queda quemado: es la dirección segura (se pide otro). |
| D7 | `OwnerProvisioningService.ProvisionOwnerAsync` recibe `string? email` y `emailVerified` y devuelve `ProviderLinkOutcome` (`Denied("email_not_verified")`); `RegistrationEndpoints` traduce `IsDenied` al mismo `Problem` 403 de hoy. | Spec: la comprobación pasa al servicio; el 403 y su `detail` no cambian (`RegistrationEndpoints.cs:81-87`). |
| D8 | `hasPassword` sale de un puerto nuevo `IPasswordCredentials`, no de `IUserDirectory`. | `IUserDirectory` tiene dobles en pruebas de Tenancy y Bootstrapper (`MembershipHandlerTestDoubles.cs`, `TenancyTestDoubles.cs`); sumarle un método rompe esos proyectos. |
| D9 | La precomprobación de slug es `ITenantRegistration.EnsureRegistrableAsync` con un puerto nuevo `ITenantSlugAvailability`, no un método en `ITenantRepository`. | `ITenantRepository` tiene cuatro dobles de prueba (`InMemoryTenantRepository`, `RecordingTenantRepository`, `TenantRepo`, `StepsTenantRepository`). |
| D10 | Las claves de `errors` son `Password` y `Email` (PascalCase), no `password`/`email` como dice el spec (l.75). Ningún validador usa `OverridePropertyName`. | `ApiExceptionHandler.cs:66` agrupa por `error.PropertyName`, que FluentValidation llena con el nombre de la propiedad del record. El frontend lee esas claves. |
| D11 | Un `email` vacío o ausente en el token de Google sigue dando el 403 `email_not_verified` de hoy: el servicio niega `string.IsNullOrWhiteSpace(email) \|\| !emailVerified`. | Hoy lo niega `RegistrationEndpoints.cs:79-86` antes de llamar al servicio; sin esto, el email vacío llegaría a `User.NormalizeEmail` y saldría 422 `identity.email.invalid`. |

Riesgos residuales aceptados (el spec los deja así; anotar en el ledger al cerrar):
- El camino «email nuevo» del registro crea usuario y tenant; el de «email existente» sólo encola un mail. El hash corre en los dos, pero el resto del tiempo difiere.
- `forgot` con email existente escribe token + outbox + auditoría; con email inexistente no escribe nada.
- ~~Dos registros simultáneos con el mismo email nuevo dan 500~~: resuelto en la Task 10 (el perdedor sigue el camino de email existente).
- `SDD-CT-07`: el perdedor de una carrera por el slug deja un usuario sin membresía.

## Review Focus

1. **Registro concurrente con el mismo slug:** los dos pasan la precomprobación; el índice `IX_tenants_slug` decide, el perdedor recibe el mismo 422 `tenancy.slug.taken` y **no** recibe mail de verificación. → `PasswordAuthApiTests.ConcurrentRegistrationsWithTheSameSlugCreateOneTenantAndQueueOneVerificationMail` (Task 10).
2. **Normalización de email (mayúsculas y espacios) en login y forgot:** `FindByEmailAsync` compara exacto (`UserRepository.cs:12-14`); sin `User.NormalizeEmail` antes, `" Ana@X.com "` da 401 y `forgot` no manda nada. → `PasswordAuthApiTests.LoginNormalizesEmailCaseAndWhitespace` (Task 12) y `PasswordAuthApiTests.ForgotNormalizesTheEmail` (Task 13).
3. **Carrera de reuso de un token:** dos `verify` simultáneos con el mismo token no pueden abrir dos sesiones. → `PasswordAuthApiTests.ConcurrentVerifyWithTheSameTokenOpensExactlyOneSession` (Task 11).
4. **Vincular Google borra la contraseña sin verificar, en los dos caminos:** login (`ProviderLinkingService`) y registro (`OwnerProvisioningService`). → `GoogleLinkingClearsPasswordTests.GoogleLoginOnAnUnverifiedPasswordUserDeletesTheCredentialAndBurnsTokens` y `GoogleRegistrationOnAnUnverifiedPasswordUserDeletesTheCredentialAndBurnsTokens` (Task 5).
5. **Contenido del payload del outbox:** sólo `userId` y `token`; nunca contraseña, hash ni email; el token del payload hashea al `token_hash` guardado. → `PasswordAuthApiTests.TheVerificationOutboxPayloadCarriesOnlyTheUserIdAndTheToken` (Task 10).

---

## Mapa de archivos

| Archivo | Responsabilidad |
| --- | --- |
| `src/Modules/Identity/Modules.Identity.Domain/User.cs` | `EmailVerifiedAt`, `MarkEmailVerified` |
| `src/Modules/Identity/Modules.Identity.Domain/PasswordCredential.cs` | credencial (una por usuario) |
| `src/Modules/Identity/Modules.Identity.Domain/EmailToken.cs` | token de email, `EmailTokenId`, `EmailTokenPurpose`, vigencias |
| `src/Modules/Identity/Modules.Identity.Application/IPasswordHasher.cs` | puerto de hash + `PasswordVerificationOutcome` |
| `src/Modules/Identity/Modules.Identity.Application/PasswordRules.cs` | reglas 10–128 + validadores |
| `src/Modules/Identity/Modules.Identity.Application/IPasswordCredentialRepository.cs` | repositorio de credenciales |
| `src/Modules/Identity/Modules.Identity.Application/IEmailTokenRepository.cs`, `EmailTokenIssuer.cs`, `EmailTokenCodec.cs` | emitir, invalidar, consumir |
| `src/Modules/Identity/Modules.Identity.Application/IIdentityOutbox.cs`, `IdentityIntegrationEvents.cs` | outbox de Identity, nombres y payloads |
| `src/Modules/Identity/Modules.Identity.Application/VerifiedProviderLinker.cs` | **la** regla «vincular borra la contraseña sin verificar» |
| `src/Modules/Identity/Modules.Identity.Application/PasswordRegistration.cs`, `EmailVerification.cs`, `PasswordLogin.cs`, `PasswordReset.cs`, `PasswordCredentials.cs` | casos de uso |
| `src/Modules/Identity/Modules.Identity.Application/PasswordCredentialVerifier.cs` | **la** verificación de contraseña con re-hash, compartida por login y aceptación de invitación |
| `src/Modules/Identity/Modules.Identity.Infrastructure/Persistence/*` | repos, hasher, outbox writer, mapeos, migraciones |
| `src/Modules/Identity/Modules.Identity.Infrastructure/Persistence/IdentityUnitOfWork.cs` | traduce la violación de `IX_users_email` (Task 10) |
| `src/Modules/Notifications/...` | `TenantId` nullable, `AppBaseUrl`, plantillas, `AccountLinks`, tres workers |
| `src/Modules/Tenancy/...` | `Tenant.EnsureValidRegistration`, `ITenantSlugAvailability`, `EnsureRegistrableAsync`, `InvitationAcceptability` |
| `src/Api/SessionEstablisher.cs` | aceptar invitadas + emitir sesión + cookie + cuerpo |
| `src/Api/PasswordAuthEndpoints.cs` | los cinco endpoints de `/auth/password/*` y `/auth/email/verify` |
| `src/Api/InvitationEndpoints.cs`, `RateLimiterPolicies.cs`, `Program.cs` | `hasPassword`, `accept-with-password`, política `Credentials` |
| `tests/Modules/Identity/Modules.Identity.IntegrationTests/PasswordAuthApiHarness.cs` | fábrica real-auth sobre https, canal de correo grabador, helpers de SQL y HTTP. Cada task declara, en su bloque «Produces (test)», los helpers que agrega; ninguna prueba usa uno que no esté declarado en esa task o en una anterior. |
| `tests/Modules/Identity/Modules.Identity.UnitTests/IdentityApplicationTestDoubles.cs` | dobles de los puertos de Identity.Application, con el mismo criterio de declaración |

---

### Task 1: `User.EmailVerifiedAt` con migración y backfill

**Files:**
- Modify: `src/Modules/Identity/Modules.Identity.Domain/User.cs`
- Modify: `src/Modules/Identity/Modules.Identity.Infrastructure/Persistence/IdentityDbContext.cs` (`ConfigureUser`)
- Create: `src/Modules/Identity/Modules.Identity.Infrastructure/Persistence/Migrations/<timestamp>_AddEmailVerifiedAt.cs` (+ `.Designer.cs`, snapshot regenerado)
- Test: `tests/Modules/Identity/Modules.Identity.UnitTests/UserTests.cs`
- Create (test): `tests/Modules/Identity/Modules.Identity.IntegrationTests/PasswordAuthApiHarness.cs`, `tests/Modules/Identity/Modules.Identity.IntegrationTests/IdentityMigrationTests.cs`

**Interfaces:**
- Produces: `public DateTimeOffset? User.EmailVerifiedAt { get; private set; }`; `public void User.MarkEmailVerified(DateTimeOffset occurredAt)` (idempotente: no pisa una fecha ya puesta, no toca `UpdatedAt` la segunda vez).
- Produces (test), en `internal static class PasswordAuthApiHarness` (las pruebas lo importan con `using static`):
  - `Task<PostgreSqlContainer> StartDatabaseAsync()` — copia de `RealAuthenticationApiTests.StartDatabaseAsync`, `postgres:18-alpine`.
  - `IdentityDbContext NewIdentityContext(string connectionString)` — mismas opciones que `IdentityDbContextFactory`.
  - `Task<DateTimeOffset?> EmailVerifiedAtAsync(string connectionString, Guid userId)` — `email_verified_at` de `identity.users`.

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// UserTests.cs
[Fact]
public void ANewUserHasNoVerifiedEmail()
{
    var user = User.CreateInvited(UserId.New(), "person@example.com", CreatedAt);
    Assert.Null(user.EmailVerifiedAt);
}

[Fact]
public void MarkEmailVerifiedSetsTheTimestampOnceAndIsIdempotent()
{
    var user = User.CreateInvited(UserId.New(), "person@example.com", CreatedAt);
    user.MarkEmailVerified(CreatedAt.AddMinutes(1));
    user.MarkEmailVerified(CreatedAt.AddMinutes(2));
    Assert.Equal(CreatedAt.AddMinutes(1), user.EmailVerifiedAt);
    Assert.Equal(CreatedAt.AddMinutes(1), user.UpdatedAt);
}
```

```csharp
// IdentityMigrationTests.cs — mismo patrón que TenantModulesMigrationTests (IMigrator)
private const string PreviousMigration = "20261015083738_AddMessageSoundPreference";

[Fact]
public async Task EmailVerifiedAtIsBackfilledFromTheFirstProviderLink()
{
    // migrar hasta PreviousMigration; insertar por SQL:
    //   usuario A con dos provider_links (linked_at t1 < t2), usuario B sin vínculos
    // migrar al final
    Assert.Equal(t1, await EmailVerifiedAtAsync(cs, userA));
    Assert.Null(await EmailVerifiedAtAsync(cs, userB));
}
```

- [ ] **Step 2: Correr y ver FAIL**

```powershell
dotnet test tests/Modules/Identity/Modules.Identity.UnitTests --filter "FullyQualifiedName~UserTests"
```
Esperado: no compila, `CS1061: 'User' does not contain a definition for 'EmailVerifiedAt'` / `'MarkEmailVerified'`.

- [ ] **Step 3: Implementar el dominio y el mapeo**

`User.MarkEmailVerified(DateTimeOffset occurredAt)`: si `EmailVerifiedAt` ya tiene valor, no hace nada; si no, lo fija y actualiza `UpdatedAt`. Mapeo: `user.Property(value => value.EmailVerifiedAt).HasColumnName("email_verified_at");`.

- [ ] **Step 4: Generar la migración y agregar el backfill a mano**

```powershell
dotnet ef migrations add AddEmailVerifiedAt --project src/Modules/Identity/Modules.Identity.Infrastructure --context IdentityDbContext -o Persistence/Migrations
```
Al final del `Up` generado:

```sql
UPDATE identity.users AS u
SET email_verified_at = first_link.linked_at
FROM (SELECT user_id, MIN(linked_at) AS linked_at FROM identity.provider_links GROUP BY user_id) AS first_link
WHERE first_link.user_id = u.id;
```
El `Down` sólo borra la columna.

- [ ] **Step 5: Correr y ver PASS**

```powershell
dotnet test tests/Modules/Identity/Modules.Identity.UnitTests --filter "FullyQualifiedName~UserTests"
dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests --filter "FullyQualifiedName~IdentityMigrationTests"
```
Esperado: verde. (La integración primero da RED con `column "email_verified_at" does not exist` si se corre antes del Step 4.)

- [ ] **Step 6: Commit**

```powershell
git add src/Modules/Identity/Modules.Identity.Domain/User.cs src/Modules/Identity/Modules.Identity.Infrastructure/Persistence/IdentityDbContext.cs src/Modules/Identity/Modules.Identity.Infrastructure/Persistence/Migrations/ tests/Modules/Identity/Modules.Identity.UnitTests/UserTests.cs tests/Modules/Identity/Modules.Identity.IntegrationTests/PasswordAuthApiHarness.cs tests/Modules/Identity/Modules.Identity.IntegrationTests/IdentityMigrationTests.cs
git commit -m "feat(identity): track verified email with backfill from provider links"
```

---

### Task 2: Credencial de contraseña, `IPasswordHasher` y reglas de contraseña

**Files:**
- Create: `src/Modules/Identity/Modules.Identity.Domain/PasswordCredential.cs`
- Create: `src/Modules/Identity/Modules.Identity.Application/IPasswordHasher.cs`, `IPasswordCredentialRepository.cs`, `PasswordRules.cs`
- Modify: `src/Modules/Identity/Modules.Identity.Application/Modules.Identity.Application.csproj` (`<PackageReference Include="FluentValidation" />`)
- Create: `src/Modules/Identity/Modules.Identity.Infrastructure/Persistence/AspNetCorePasswordHasher.cs`, `PasswordCredentialRepository.cs`
- Modify: `src/Modules/Identity/Modules.Identity.Infrastructure/Modules.Identity.Infrastructure.csproj` (`<PackageReference Include="Microsoft.Extensions.Identity.Core" />`)
- Modify: `Directory.Packages.props` (`<PackageVersion Include="Microsoft.Extensions.Identity.Core" Version="10.0.13" />`)
- Modify: `src/Modules/Identity/Modules.Identity.Infrastructure/Persistence/IdentityDbContext.cs` (`ConfigurePasswordCredential`), `IdentityInfrastructureExtensions.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (junto a la línea 880: `services.AddValidatorsFromAssemblyContaining<NewPasswordValidator>();`)
- Create: migración `AddPasswordCredentials`
- Modify: `tests/Modules/Identity/Modules.Identity.UnitTests/Modules.Identity.UnitTests.csproj` (referencia a `Modules.Identity.Application`)
- Test: `tests/Modules/Identity/Modules.Identity.UnitTests/PasswordCredentialTests.cs`, `PasswordRulesTests.cs`; `tests/Modules/Identity/Modules.Identity.IntegrationTests/PasswordHasherTests.cs`, `IdentityPersistenceTests.cs` (nuevo), `PasswordAuthApiHarness.cs` (`CountRowsAsync`); `tests/ArchitectureTests/ArchitectureTests/IdentityLayerTests.cs`
- Todos los `packages.lock.json` que cambie `dotnet restore --force-evaluate`.

**Interfaces:**
- Produces (Domain):
  ```csharp
  public sealed class PasswordCredential
  {
      public UserId UserId { get; }
      public string Hash { get; }
      public DateTimeOffset UpdatedAt { get; }
      public static PasswordCredential Create(UserId userId, string hash, DateTimeOffset createdAt); // hash vacío → IdentityDomainException("identity.password.hash_required")
      public void Replace(string hash, DateTimeOffset occurredAt);
  }
  ```
- Produces (Application):
  ```csharp
  public enum PasswordVerificationOutcome { Failed, Success, SuccessRehashNeeded }
  public interface IPasswordHasher
  {
      string Hash(string password);
      // hashedPassword null => verifica contra un hash ficticio fijo del proceso y devuelve Failed (iguala el tiempo).
      PasswordVerificationOutcome Verify(string? hashedPassword, string providedPassword);
  }
  public interface IPasswordCredentialRepository
  {
      Task<PasswordCredential?> FindByUserAsync(Guid userId, CancellationToken cancellationToken);
      void Add(PasswordCredential credential);
      void Remove(PasswordCredential credential);
  }
  public static class PasswordRules
  {
      public const int MinLength = 10;
      public const int MaxLength = 128;
      public static IRuleBuilderOptions<T, string> QepPassword<T>(this IRuleBuilder<T, string> rule); // NotEmpty + Length(10,128), código identity.password.invalid
  }
  public sealed record NewPasswordInput(string Password);
  public sealed class NewPasswordValidator : AbstractValidator<NewPasswordInput>; // RuleFor(x => x.Password).QepPassword(); sin OverridePropertyName: la clave es "Password" (D10)
  ```
- Produces (Infrastructure): `internal sealed class AspNetCorePasswordHasher : IPasswordHasher` sobre un `PasswordHasher<User>` estático (opciones por defecto, V3); el hash ficticio se calcula una vez (`Lazy<string>`) desde 16 bytes aleatorios. `internal sealed class PasswordCredentialRepository(IdentityDbContext)`.
- Tabla: `identity.password_credentials` — `user_id` PK y FK a `identity.users(id)` `ON DELETE CASCADE`, `hash text`, `updated_at timestamptz`.
- Produces (test), en `PasswordAuthApiHarness`:
  - `Task<long> CountRowsAsync(string connectionString, string table, Guid userId)` — `SELECT COUNT(*) FROM {table} WHERE user_id = @id`; `table` es siempre una constante de la prueba.

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// PasswordCredentialTests.cs
[Fact] public void CreateRejectsAnEmptyHash()
    => Assert.Equal("identity.password.hash_required",
        Assert.Throws<IdentityDomainException>(() => PasswordCredential.Create(UserId.New(), " ", Now)).Code);

[Fact] public void ReplaceChangesTheHashAndTheTimestamp()
{
    var credential = PasswordCredential.Create(UserId.New(), "h1", Now);
    credential.Replace("h2", Now.AddMinutes(1));
    Assert.Equal("h2", credential.Hash);
    Assert.Equal(Now.AddMinutes(1), credential.UpdatedAt);
}
```

```csharp
// PasswordRulesTests.cs
[Theory, InlineData(9), InlineData(129)]
public void APasswordOutsideTenTo128CharactersFailsOnThePasswordField(int length)
{
    var result = new NewPasswordValidator().Validate(new NewPasswordInput(new string('a', length)));
    Assert.Equal("Password", Assert.Single(result.Errors).PropertyName);
}

[Theory, InlineData(10), InlineData(128)]
public void APasswordOfTenOr128CharactersWithoutCompositionPasses(int length)
    => Assert.True(new NewPasswordValidator().Validate(new NewPasswordInput(new string('a', length))).IsValid);
```

```csharp
// PasswordHasherTests.cs (proyecto de integración: InternalsVisibleTo ya existe; no usa base)
[Fact] public void AHashVerifiesOnlyItsOwnPassword()
{
    var hasher = new AspNetCorePasswordHasher();
    var hash = hasher.Hash("correct horse battery");
    Assert.Equal(PasswordVerificationOutcome.Success, hasher.Verify(hash, "correct horse battery"));
    Assert.Equal(PasswordVerificationOutcome.Failed, hasher.Verify(hash, "wrong horse battery"));
    Assert.DoesNotContain("correct horse battery", hash);
}

[Fact] public void AMissingHashRunsAgainstTheDummyAndFails()
    => Assert.Equal(PasswordVerificationOutcome.Failed, new AspNetCorePasswordHasher().Verify(null, "anything at all"));

[Fact] public void AnOlderFormatHashAsksForARehash()
{
    var v2 = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions
        { CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2 })).HashPassword(null!, "correct horse battery");
    Assert.Equal(PasswordVerificationOutcome.SuccessRehashNeeded, new AspNetCorePasswordHasher().Verify(v2, "correct horse battery"));
}
```

```csharp
// IdentityPersistenceTests.cs
[Fact]
public async Task APasswordCredentialRoundTripsAndIsDeletedWithItsUser()
{
    // guardar User.CreateInvited + PasswordCredential.Create, releer en un contexto nuevo (Hash igual),
    // borrar el usuario con IUserRepository.Remove + SaveChanges
    Assert.Equal(0, await CountRowsAsync(cs, "identity.password_credentials", userId));
}
```

```csharp
// IdentityLayerTests.cs — guarda: no tiene RED posible (Application nunca referenció el paquete);
// se escribe antes de agregar el paquete a Infrastructure para que lo cubra desde el primer commit.
[Fact]
public void ApplicationDoesNotReferenceAspNetCoreIdentity()
{
    var references = typeof(IIdentityProvisioning).Assembly.GetReferencedAssemblies().Select(name => name.Name!).ToArray();
    Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.AspNetCore.Identity", StringComparison.Ordinal)
        || name == "Microsoft.Extensions.Identity.Core");
}
```

- [ ] **Step 2: Correr y ver FAIL**

```powershell
dotnet test tests/Modules/Identity/Modules.Identity.UnitTests --filter "FullyQualifiedName~PasswordCredentialTests|FullyQualifiedName~PasswordRulesTests"
```
Esperado: no compila (`PasswordCredential`, `NewPasswordValidator` no existen).

- [ ] **Step 3: Implementar**

Paquetes y restore:
```powershell
dotnet restore --force-evaluate
```
Mapeo `ConfigurePasswordCredential`: tabla `password_credentials` en `identity`, `HasKey(UserId)`, conversión de `UserId`, `HasOne<User>().WithOne().HasForeignKey<PasswordCredential>(c => c.UserId).OnDelete(DeleteBehavior.Cascade)`. DI: `IPasswordHasher` como **singleton** (`AspNetCorePasswordHasher` no tiene estado por request), `IPasswordCredentialRepository` scoped.

- [ ] **Step 4: Migración**

```powershell
dotnet ef migrations add AddPasswordCredentials --project src/Modules/Identity/Modules.Identity.Infrastructure --context IdentityDbContext -o Persistence/Migrations
```
Revisar que el `Up` cree la FK con `onDelete: ReferentialAction.Cascade`.

- [ ] **Step 5: Correr y ver PASS**

```powershell
dotnet test tests/Modules/Identity/Modules.Identity.UnitTests --filter "FullyQualifiedName~PasswordCredentialTests|FullyQualifiedName~PasswordRulesTests"
dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests --filter "FullyQualifiedName~PasswordHasherTests|FullyQualifiedName~IdentityPersistenceTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~IdentityLayerTests"
```

- [ ] **Step 6: Commit**

```powershell
git add Directory.Packages.props src/Modules/Identity/ src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Identity/ tests/ArchitectureTests/ArchitectureTests/IdentityLayerTests.cs
git status --short
git commit -m "feat(identity): add password credentials behind an application hasher port"
```
(`git status --short` antes de commitear: sólo deben quedar staged los archivos de esta tarea y los `packages.lock.json` que tocó el restore; agregar esos locks con su ruta exacta.)

---

### Task 3: Tokens de email (dominio, repositorio, emisor)

**Files:**
- Create: `src/Modules/Identity/Modules.Identity.Domain/EmailToken.cs` (incluye `EmailTokenId`, `EmailTokenPurpose`)
- Create: `src/Modules/Identity/Modules.Identity.Application/IEmailTokenRepository.cs`, `EmailTokenCodec.cs`, `EmailTokenIssuer.cs`
- Create: `src/Modules/Identity/Modules.Identity.Infrastructure/Persistence/EmailTokenRepository.cs`
- Modify: `IdentityDbContext.cs` (`ConfigureEmailToken`), `IdentityInfrastructureExtensions.cs`, `IUserRepository.cs` (comentario de `Remove`: credencial y tokens caen por cascada)
- Create: migración `AddEmailTokens`
- Test: `tests/Modules/Identity/Modules.Identity.UnitTests/EmailTokenTests.cs`, `EmailTokenIssuerTests.cs`, `IdentityApplicationTestDoubles.cs` (nuevo); `tests/Modules/Identity/Modules.Identity.IntegrationTests/IdentityPersistenceTests.cs`, `OrphanUserCleanupTests.cs`, `PasswordAuthApiHarness.cs` (`NewTokenRepository`)

**Interfaces:**
- Produces (Domain):
  ```csharp
  public readonly record struct EmailTokenId(Guid Value) { public static EmailTokenId New(); }
  public enum EmailTokenPurpose { Verify = 1, Reset = 2 }
  public sealed class EmailToken
  {
      public static readonly TimeSpan VerifyLifetime = TimeSpan.FromHours(24);
      public static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(1);
      public static TimeSpan LifetimeOf(EmailTokenPurpose purpose);
      public static EmailToken Issue(EmailTokenId id, UserId userId, EmailTokenPurpose purpose, string tokenHash, DateTimeOffset issuedAt);
      public bool IsUsable(DateTimeOffset now);          // UsedAt is null && now < ExpiresAt
      public void Invalidate(DateTimeOffset occurredAt); // fija UsedAt si estaba null
      // Id, UserId, Purpose, TokenHash, ExpiresAt, UsedAt, CreatedAt
  }
  ```
- Produces (Application):
  ```csharp
  public interface IEmailTokenRepository
  {
      void Add(EmailToken token);
      // purpose null => todos los propósitos (lo usa VerifiedProviderLinker). Carga y marca: entra en la unidad de trabajo.
      Task InvalidateUnusedAsync(Guid userId, EmailTokenPurpose? purpose, DateTimeOffset now, CancellationToken cancellationToken);
      // Atómico y fuera de la unidad de trabajo (D6). Devuelve el user_id o null.
      Task<Guid?> TryConsumeAsync(string tokenHash, EmailTokenPurpose purpose, DateTimeOffset now, CancellationToken cancellationToken);
  }
  public static class EmailTokenCodec { public static string Generate(); public static string Hash(string rawToken); } // 32 bytes base64url / SHA-256 hex minúscula, como SessionService
  public interface IEmailTokenIssuer { Task<string> IssueAsync(Guid userId, EmailTokenPurpose purpose, CancellationToken cancellationToken); }
  public sealed class EmailTokenIssuer(IEmailTokenRepository tokens, IClock clock) : IEmailTokenIssuer; // invalida los no usados del propósito, agrega el nuevo, NO guarda
  ```
- `TryConsumeAsync` (Infrastructure), una sola sentencia con `dbContext.Database.SqlQuery<Guid>`:
  ```sql
  UPDATE identity.email_tokens SET used_at = {now}
  WHERE token_hash = {hash} AND purpose = {purpose} AND used_at IS NULL AND expires_at > {now}
  RETURNING user_id AS "Value"
  ```
  - La columna escalar se llama `"Value"`: es el nombre que EF espera en `SqlQuery<T>` de un tipo escalar.
  - Se materializa con `ToListAsync` y se toma `SingleOrDefault()` en memoria. **Nada de `FirstOrDefaultAsync`** ni otra composición: EF envuelve el SQL en una subconsulta y Postgres rechaza `UPDATE … RETURNING` como subconsulta.
  - `purpose` se pasa como el string en minúscula que guarda la columna (`"verify"` / `"reset"`, la misma conversión del mapeo), nunca el enum.
- Tabla `identity.email_tokens`: `id` PK, `user_id` FK a `users` cascade, `purpose varchar(10)` (`verify` | `reset`, conversión explícita a minúscula), `token_hash varchar(64)` único (`IX_email_tokens_token_hash`), `expires_at`, `used_at null`, `created_at`; índice `(user_id, purpose, used_at)`.
- Produces (test), en `IdentityApplicationTestDoubles.cs` (nuevo):
  - `sealed class FakeEmailTokenRepository : IEmailTokenRepository` — `List<EmailToken> Added`; `List<(Guid UserId, EmailTokenPurpose? Purpose)> Invalidations`; `TryConsumeAsync` devuelve `null`.
  - `sealed class FixedClock(DateTimeOffset now) : IClock`.
- Produces (test), en `PasswordAuthApiHarness`:
  - `EmailTokenRepository NewTokenRepository(string connectionString)` — el repositorio real sobre un `NewIdentityContext` propio, para que cada consumo vaya en su contexto.
- Las pruebas de `IdentityPersistenceTests` y `OrphanUserCleanupTests` usan `CountRowsAsync` del harness (Task 2): `OrphanUserCleanupTests` no tiene uno propio.

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// EmailTokenTests.cs
[Theory, InlineData(EmailTokenPurpose.Verify, 24), InlineData(EmailTokenPurpose.Reset, 1)]
public void IssueSetsTheExpiryByPurpose(EmailTokenPurpose purpose, int hours)
    => Assert.Equal(Now.AddHours(hours), EmailToken.Issue(EmailTokenId.New(), UserId.New(), purpose, "h", Now).ExpiresAt);

[Fact] public void ATokenIsUnusableAtItsExpiryAndAfterInvalidation()
{
    var token = EmailToken.Issue(EmailTokenId.New(), UserId.New(), EmailTokenPurpose.Reset, "h", Now);
    Assert.True(token.IsUsable(Now.AddMinutes(59)));
    Assert.False(token.IsUsable(Now.AddHours(1)));
    token.Invalidate(Now.AddMinutes(1));
    Assert.False(token.IsUsable(Now.AddMinutes(2)));
}
```

```csharp
// EmailTokenIssuerTests.cs (FakeEmailTokenRepository en IdentityApplicationTestDoubles.cs)
[Fact] public async Task IssuingInvalidatesThePreviousUnusedTokensOfTheSamePurpose()
{
    await issuer.IssueAsync(userId, EmailTokenPurpose.Verify, default);
    Assert.Equal((userId, (EmailTokenPurpose?)EmailTokenPurpose.Verify), Assert.Single(tokens.Invalidations));
}

[Fact] public async Task OnlyTheHashOfTheRawTokenIsStored()
{
    var raw = await issuer.IssueAsync(userId, EmailTokenPurpose.Reset, default);
    Assert.Equal(43, raw.Length);
    Assert.Matches("^[A-Za-z0-9_-]+$", raw);
    Assert.Equal(EmailTokenCodec.Hash(raw), Assert.Single(tokens.Added).TokenHash);
}
```

```csharp
// IdentityPersistenceTests.cs — sembrado común: User.CreateInvited + EmailToken.Issue(…, tokenHash: EmailTokenCodec.Hash("raw-1"), issuedAt: Now)
// guardados con NewIdentityContext(cs); cada consumo con un EmailTokenRepository sobre un contexto nuevo.
[Fact] public async Task TryConsumeSucceedsOnceThenReturnsNull()
{
    var first = await NewTokenRepository(cs).TryConsumeAsync(EmailTokenCodec.Hash("raw-1"), EmailTokenPurpose.Verify, Now.AddMinutes(1), ct);
    var second = await NewTokenRepository(cs).TryConsumeAsync(EmailTokenCodec.Hash("raw-1"), EmailTokenPurpose.Verify, Now.AddMinutes(2), ct);
    Assert.Equal(userId, first);
    Assert.Null(second);
}

[Fact] public async Task TryConsumeRejectsAnExpiredTokenAndTheWrongPurpose()
{
    // token Verify sembrado en Now: vence en Now + 24 h
    Assert.Null(await NewTokenRepository(cs).TryConsumeAsync(EmailTokenCodec.Hash("raw-1"), EmailTokenPurpose.Reset, Now.AddMinutes(1), ct));
    Assert.Null(await NewTokenRepository(cs).TryConsumeAsync(EmailTokenCodec.Hash("raw-1"), EmailTokenPurpose.Verify, Now.AddHours(24), ct));
}

// OrphanUserCleanupTests.cs — mismo flujo que RemovingTheOnlyMembershipDeletesTheUserAndItsSessions
[Fact] public async Task RemovingTheOnlyMembershipAlsoDeletesTheCredentialAndEmailTokens()
{
    // sembrar por SQL una fila en password_credentials y una en email_tokens para member.UserId
    Assert.Equal(0, await CountRowsAsync(connectionString, "identity.password_credentials", member.UserId));
    Assert.Equal(0, await CountRowsAsync(connectionString, "identity.email_tokens", member.UserId));
}
```

- [ ] **Step 2: Correr y ver FAIL**

```powershell
dotnet test tests/Modules/Identity/Modules.Identity.UnitTests --filter "FullyQualifiedName~EmailTokenTests|FullyQualifiedName~EmailTokenIssuerTests"
```
Esperado: no compila (`EmailToken`, `EmailTokenIssuer` no existen).

- [ ] **Step 3: Implementar** dominio, codec, emisor, repositorio y mapeo con las firmas de arriba; DI scoped de `IEmailTokenRepository` e `IEmailTokenIssuer`.

- [ ] **Step 4: Migración**

```powershell
dotnet ef migrations add AddEmailTokens --project src/Modules/Identity/Modules.Identity.Infrastructure --context IdentityDbContext -o Persistence/Migrations
```

- [ ] **Step 5: Correr y ver PASS**

```powershell
dotnet test tests/Modules/Identity/Modules.Identity.UnitTests --filter "FullyQualifiedName~EmailTokenTests|FullyQualifiedName~EmailTokenIssuerTests"
dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests --filter "FullyQualifiedName~IdentityPersistenceTests|FullyQualifiedName~OrphanUserCleanupTests"
```

- [ ] **Step 6: Commit**

```powershell
git add src/Modules/Identity/ tests/Modules/Identity/
git commit -m "feat(identity): add single-use hashed email tokens"
```

---

### Task 4: Outbox de Identity

**Files:**
- Create: `src/Modules/Identity/Modules.Identity.Application/IIdentityOutbox.cs`, `IdentityIntegrationEvents.cs`
- Create: `src/Modules/Identity/Modules.Identity.Infrastructure/Persistence/IdentityOutboxWriter.cs`
- Modify: `src/Modules/Identity/Modules.Identity.Infrastructure/Persistence/IdentityInboxMessage.cs` (`OutboxRecord` + `CorrelationId`, `Attempts`), `IdentityDbContext.cs` (`ConfigureOutboxProjection`: `correlation_id`, `attempts`, `payload` con `HasColumnType("jsonb")`), `IdentityInfrastructureExtensions.cs`
- Create: migración `IdentityOutboxWriteProjection` (se espera **vacía** en DDL: la tabla es `ExcludeFromMigrations`; sólo cambia el snapshot. Si sale con DDL, algo está mal)
- Test: `tests/Modules/Identity/Modules.Identity.IntegrationTests/IdentityPersistenceTests.cs`, `PasswordAuthApiHarness.cs` (fábrica y helpers de abajo)

**Interfaces:**
- Produces:
  ```csharp
  public static class IdentityIntegrationEvents
  {
      public const string EmailVerificationRequested = "identity.email-verification-requested.v1";
      public const string PasswordResetRequested = "identity.password-reset-requested.v1";
      public const string ExistingAccountNotice = "identity.existing-account-notice.v1";
  }
  public sealed record EmailTokenIssuedPayload(Guid UserId, string Token);
  public sealed record ExistingAccountNoticePayload(Guid UserId);
  public interface IIdentityOutbox { void Add(string eventName, object payload, string correlationId, DateTimeOffset occurredAt); } // no guarda
  ```
  `IdentityOutboxWriter` serializa con `JsonSerializerDefaults.Web` (camelCase), `Id = Guid.CreateVersion7()`, `Attempts = 0`, `CorrelationId` recortado a 100.
- Produces (test), en `PasswordAuthApiHarness` — la fábrica nace acá porque esta es la primera prueba que necesita un host (scope de DI y el `OutboxProcessor` corriendo):
  - `sealed class PasswordAuthApiFactory(string connectionString, IEmailChannel? emailChannel = null, bool signupEnabled = true) : WebApplicationFactory<Program>` — copia deliberada de `RealAuthenticationApiTests.QepApiFactory` (entorno `IntegrationTests`, `Authentication:TestSigningKey`, `Authentication:Audience`, `Notifications:EmailProvider=log`, `Registration:PublicTenantSignupEnabled = signupEnabled`); no quita ningún hosted service; con canal: `services.RemoveAll<IEmailChannel>(); services.AddSingleton(emailChannel);`.
  - `Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)` — sondea cada 200 ms hasta la condición o el plazo (30 s por defecto) y falla con `Assert.Fail` al vencer; copia de `SessionRevocationTests.WaitUntilAsync` con plazo parametrizable.
  - `sealed record OutboxRow(string PayloadText, string CorrelationId, DateTimeOffset? ProcessedAt)`.
  - `Task<OutboxRow?> ReadOutboxRowAsync(string connectionString, string eventName)` — la fila más reciente (por `occurred_at`) de `platform.outbox_messages` con ese `event_name`, `payload::text`.

- [ ] **Step 1: Escribir la prueba que falla**

```csharp
[Fact]
public async Task AnIdentityOutboxRowIsWrittenWithTheUnitOfWorkAndProcessedByThePlatform()
{
    await using var database = await StartDatabaseAsync();
    var cs = database.GetConnectionString();
    using var factory = new PasswordAuthApiFactory(cs);
    // userId: un User.CreateInvited guardado antes con NewIdentityContext(cs)
    using (var scope = factory.Services.CreateScope())
    {
        scope.ServiceProvider.GetRequiredService<IIdentityOutbox>().Add(
            IdentityIntegrationEvents.ExistingAccountNotice, new ExistingAccountNoticePayload(userId), "corr", DateTimeOffset.UtcNow);
        await scope.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync(ct);
    }
    var row = await ReadOutboxRowAsync(cs, IdentityIntegrationEvents.ExistingAccountNotice);
    Assert.Equal($$"""{"userId": "{{userId}}"}""", row!.PayloadText);  // jsonb normaliza el espaciado así
    Assert.Equal("corr", row.CorrelationId);
    // OutboxProcessor de Tenancy: sin handlers, lo marca procesado
    await WaitUntilAsync(async () => (await ReadOutboxRowAsync(cs, IdentityIntegrationEvents.ExistingAccountNotice))!.ProcessedAt is not null);
}
```

- [ ] **Step 2: Correr y ver FAIL**

```powershell
dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests --filter "FullyQualifiedName~IdentityPersistenceTests.AnIdentityOutboxRowIsWrittenWithTheUnitOfWorkAndProcessedByThePlatform"
```
Esperado: no compila (`IIdentityOutbox` no existe).

- [ ] **Step 3: Implementar** con las firmas de arriba. Sin `jsonb` en el mapeo, Postgres rechaza el insert (`column "payload" is of type jsonb but expression is of type text`).

- [ ] **Step 4: Migración del snapshot**

```powershell
dotnet ef migrations add IdentityOutboxWriteProjection --project src/Modules/Identity/Modules.Identity.Infrastructure --context IdentityDbContext -o Persistence/Migrations
```

- [ ] **Step 5: Correr y ver PASS** (mismo comando del Step 2, más `--filter "FullyQualifiedName~OrphanUserCleanupTests|FullyQualifiedName~SessionRevocationTests"` para confirmar que los consumidores de la proyección siguen leyendo).

- [ ] **Step 6: Commit**

```powershell
git add src/Modules/Identity/ tests/Modules/Identity/Modules.Identity.IntegrationTests/IdentityPersistenceTests.cs tests/Modules/Identity/Modules.Identity.IntegrationTests/PasswordAuthApiHarness.cs
git commit -m "feat(identity): write integration events to the platform outbox"
```

---

### Task 5: Vincular Google borra la contraseña sin verificar (un solo lugar)

**Files:**
- Create: `src/Modules/Identity/Modules.Identity.Application/VerifiedProviderLinker.cs`
- Modify: `src/Modules/Identity/Modules.Identity.Application/ProviderLinkingService.cs`, `OwnerProvisioningService.cs`, `IOwnerProvisioning.cs`
- Modify: `src/Api/RegistrationEndpoints.cs` (pasa `emailVerified`, traduce `IsDenied` al mismo `Problem` 403 con `detail: "email_not_verified"`)
- Modify: `IdentityInfrastructureExtensions.cs`
- Test: `tests/Modules/Identity/Modules.Identity.UnitTests/VerifiedProviderLinkerTests.cs`, `IdentityApplicationTestDoubles.cs`; `tests/Modules/Identity/Modules.Identity.IntegrationTests/GoogleLinkingClearsPasswordTests.cs`, `PasswordAuthApiHarness.cs`

**Interfaces:**
- Produces:
  ```csharp
  public interface IVerifiedProviderLinker
  {
      // LinkProvider; si EmailVerifiedAt era null: borra la credencial e invalida TODOS los tokens no usados;
      // después MarkEmailVerified. No guarda.
      Task LinkAsync(User user, string provider, string subject, CancellationToken cancellationToken);
  }
  public sealed class VerifiedProviderLinker(IPasswordCredentialRepository credentials, IEmailTokenRepository tokens, IClock clock) : IVerifiedProviderLinker;
  ```
- Modifies: `IOwnerProvisioning.ProvisionOwnerAsync(string provider, string subject, string? email, bool emailVerified, CancellationToken)` → `Task<ProviderLinkOutcome>`; `string.IsNullOrWhiteSpace(email) || !emailVerified` → `Denied("email_not_verified")` sin tocar nada (D11: el 403 de hoy para un email vacío se mantiene). `RegistrationEndpoints` deja de chequear email y `email_verified` por su cuenta y pasa los dos tal cual llegan en el token.
- `ProviderLinkingService` y `OwnerProvisioningService` llaman `IVerifiedProviderLinker.LinkAsync` en lugar de `user.LinkProvider` directo.
- Produces (test), en `IdentityApplicationTestDoubles.cs`:
  - `sealed class FakePasswordCredentialRepository : IPasswordCredentialRepository` — `List<PasswordCredential> Stored` (lo que devuelve `FindByUserAsync`), `List<PasswordCredential> Added`, `List<PasswordCredential> Removed`.
  - `sealed class FakeUserRepository : IUserRepository` — `List<User> Stored` (búsquedas por email, proveedor e id), `List<User> Added`.
  - `sealed class CountingIdentityUnitOfWork : IIdentityUnitOfWork` — `int Saves`.
- Produces (test), en `PasswordAuthApiHarness` (la fábrica es de la Task 4):
  - `HttpClient CreateClient(PasswordAuthApiFactory factory)` — `BaseAddress = https://localhost`, como `RealAuthenticationApiTests.cs:784-800` (sin https la cookie `Secure` no viaja).
  - `string IssueGoogleIdToken(string subject, string email, bool emailVerified)` — id token firmado con `TestSigningKey`, copia del de `RealAuthenticationApiTests`.
  - `Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, object body)` — POST JSON con `X-Qep-Client: web`.
  - `Task<HttpResponseMessage> PostWithGoogleAsync(HttpClient client, string path, string idToken, object? body = null)` — lo mismo con `Authorization: Bearer {idToken}`; sin `body` manda `{}`.
  - `Task<Guid> SeedUnverifiedPasswordUserAsync(PasswordAuthApiFactory factory, string email, string password)` — en un scope: `User.CreateInvited` + `Activate` + credencial con `IPasswordHasher` + un token `verify` con `IEmailTokenIssuer`, y `IIdentityUnitOfWork.SaveChangesAsync` (el emisor no guarda). Devuelve el `userId`.
  - `Task<long> CountUnusedTokensAsync(string connectionString, Guid userId)` — filas de `identity.email_tokens` del usuario con `used_at IS NULL`.
  - `string NewEmail()` — `$"user-{Guid.NewGuid():N}@example.com"`; `string NewSlug()` — `$"t-{Guid.NewGuid():N}"[..20]`.
  - `sealed class RecordingEmailChannel : IEmailChannel` — copia de `NotificationsDeliveryHarness.cs:226-248` (`IReadOnlyCollection<EmailMessage> Sent`).
  - Nota en el harness: TestServer no tiene `RemoteIpAddress`, así que toda prueba comparte la partición `unknown` del limitador: **máximo 10 requests por endpoint y por prueba**, y cada prueba crea su propia fábrica (como `RealAuthenticationApiTests`), nunca una fixture de clase.

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// VerifiedProviderLinkerTests.cs
[Fact] public async Task LinkingAnUnverifiedUserRemovesThePasswordAndInvalidatesAllTokens()
{
    await linker.LinkAsync(user, "google", "sub-1", default);
    Assert.Equal(user.Id.Value, Assert.Single(credentials.Removed).UserId.Value);
    Assert.Equal((user.Id.Value, (EmailTokenPurpose?)null), Assert.Single(tokens.Invalidations));
    Assert.NotNull(user.EmailVerifiedAt);
}

[Fact] public async Task LinkingAVerifiedUserKeepsThePassword()
{
    user.MarkEmailVerified(Now);
    await linker.LinkAsync(user, "google", "sub-1", default);
    Assert.Empty(credentials.Removed);
    Assert.Empty(tokens.Invalidations);
}

[Theory]
[InlineData("a@x.com", false)]
[InlineData("   ", true)]
[InlineData(null, true)]
public async Task OwnerProvisioningDeniesAMissingOrUnverifiedEmailWithoutCreatingAUser(string? email, bool emailVerified)
{
    var outcome = await provisioning.ProvisionOwnerAsync("google", "sub-1", email, emailVerified, default);
    Assert.Equal("email_not_verified", outcome.DenialReason);
    Assert.Empty(users.Added);
    Assert.Equal(0, unitOfWork.Saves);
}
```

```csharp
// GoogleLinkingClearsPasswordTests.cs — «El ataque». Cada prueba: StartDatabaseAsync, cs, factory = new PasswordAuthApiFactory(cs), client = CreateClient(factory), email = NewEmail().
[Fact] public async Task GoogleLoginOnAnUnverifiedPasswordUserDeletesTheCredentialAndBurnsTokens()
{
    var userId = await SeedUnverifiedPasswordUserAsync(factory, email, "attacker-password");
    var response = await PostWithGoogleAsync(client, "/api/v1/auth/session", IssueGoogleIdToken("victim-sub", email, true));
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal(0, await CountRowsAsync(cs, "identity.password_credentials", userId));
    Assert.Equal(0, await CountUnusedTokensAsync(cs, userId));
    Assert.NotNull(await EmailVerifiedAtAsync(cs, userId));
}

[Fact] public async Task GoogleRegistrationOnAnUnverifiedPasswordUserDeletesTheCredentialAndBurnsTokens()
{
    var userId = await SeedUnverifiedPasswordUserAsync(factory, email, "attacker-password");
    var response = await PostWithGoogleAsync(client, "/api/v1/auth/register-tenant", IssueGoogleIdToken("victim-sub", email, true),
        new { displayName = "Acme", slug = NewSlug() });
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    Assert.Equal(0, await CountRowsAsync(cs, "identity.password_credentials", userId));
    Assert.Equal(0, await CountUnusedTokensAsync(cs, userId));
    Assert.NotNull(await EmailVerifiedAtAsync(cs, userId));
}

[Fact] public async Task GoogleRegistrationWithoutAnEmailClaimIsStillTheSame403()
{
    var response = await PostWithGoogleAsync(client, "/api/v1/auth/register-tenant", IssueGoogleIdToken("sub-1", "", true),
        new { displayName = "Acme", slug = NewSlug() });
    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    Assert.Equal("email_not_verified", (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("detail").GetString());
}
```

- [ ] **Step 2: Correr y ver FAIL**

```powershell
dotnet test tests/Modules/Identity/Modules.Identity.UnitTests --filter "FullyQualifiedName~VerifiedProviderLinkerTests"
dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests --filter "FullyQualifiedName~GoogleLinkingClearsPasswordTests"
```
Esperado: unidad no compila (`VerifiedProviderLinker`); integración, una vez que compile el harness, falla con `Assert.Equal() Failure: Expected: 0 Actual: 1` en `password_credentials`. `GoogleRegistrationWithoutAnEmailClaimIsStillTheSame403` ya pasa hoy: es la guarda de D11 y tiene que seguir verde después del Step 3.

- [ ] **Step 3: Implementar** con las firmas de arriba.

- [ ] **Step 4: Correr y ver PASS** — los dos comandos del Step 2, más la regresión del registro con Google:

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~RegistrationApiTests|FullyQualifiedName~AuthSessionApiTests"
```

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Identity/ src/Api/RegistrationEndpoints.cs tests/Modules/Identity/
git commit -m "fix(identity): drop an unverified password when a provider is linked"
```

---

### Task 6: Notifications sin tenant y `Notifications:AppBaseUrl`

**Files:**
- Modify: `src/Modules/Notifications/Modules.Notifications.Domain/Notification.cs` (`Guid? TenantId`, `CreateEmail(Guid? tenantId, …)`)
- Modify: `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/OutboxDeliveryWorker.cs` (`ResolveRecipientAsync(DeliveryContext context, Guid? tenantId, Guid subjectId, string templateRef, CancellationToken)`)
- Modify: `src/Modules/Notifications/Modules.Notifications.Infrastructure/NotificationsOptions.cs` (`public string AppBaseUrl { get; init; } = "http://localhost:3002";`), `NotificationsOptionsValidator.cs` (misma regla http/https absoluta que `InvitationUrl`, mensaje `Notifications:AppBaseUrl must be an absolute URL.`)
- Modify: `src/Api/appsettings.json`, `src/Api/appsettings.example.json` (`"AppBaseUrl": "http://localhost:3002"`), `k8s/prod-configMap.yaml` (`Notifications__AppBaseUrl: "#{NOTIFICATIONS_APP_BASE_URL}#"` junto a la línea 72; la variable del pipeline la define el owner)
- Create: migración `MakeNotificationTenantOptional`
- Test: `tests/Modules/Notifications/Modules.Notifications.UnitTests/NotificationTests.cs`, `NotificationsOptionsValidatorTests.cs`, `NotificationsDbContextMappingTests.cs`; `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/OutboxDeliveryWorkerTests.cs`

**Revisión hecha (spec «Mails sin tenant»):** no hay RLS en el repositorio; `notifications.notifications` sólo la escriben los workers y nadie la lista por tenant; el camino de intentos agotados ya escribe `Guid.Empty` (`OutboxDeliveryWorker.cs`, `ProcessAsync`). Nullable no rompe nada: se sigue con la decisión del spec, sin desvío.

**Interfaces:**
- Produces: `Notification.TenantId` como `Guid?`; columna `tenant_id` nullable; el índice `(tenant_id, recipient_id)` se mantiene.

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// NotificationTests.cs
[Fact] public void CreateEmailAcceptsANullTenant()
    => Assert.Null(Notification.CreateEmail(null, Guid.NewGuid(), "a@x.com", "identity.password-reset.v1", Now).TenantId);

// NotificationsDbContextMappingTests.cs
[Fact] public void TheNotificationTenantIsOptional()
    => Assert.True(model.FindEntityType(typeof(Notification))!.FindProperty(nameof(Notification.TenantId))!.IsNullable);

// NotificationsOptionsValidatorTests.cs
[Theory, InlineData(""), InlineData("/login"), InlineData("ftp://qep.qcode.co")]
public void AnInvalidAppBaseUrlFails(string value)
    => Assert.Contains("Notifications:AppBaseUrl must be an absolute URL.",
        ValidatorFor(Environments.Development).Validate(null, new NotificationsOptions { AppBaseUrl = value }).Failures!);

// OutboxDeliveryWorkerTests.cs (integración)
[Fact] public async Task ATenantlessNotificationNeverMatchesATenantFilter()
{
    // insertar con NotificationsDbContext una Notification con tenant null y otra con tenantId
    Assert.Single(await context.Notifications.Where(n => n.TenantId == tenantId).ToListAsync());
}
```

- [ ] **Step 2: Correr y ver FAIL**

```powershell
dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --filter "FullyQualifiedName~NotificationTests|FullyQualifiedName~NotificationsDbContextMappingTests|FullyQualifiedName~NotificationsOptionsValidatorTests"
```
Esperado: no compila (`cannot convert from '<null>' to 'Guid'`, `AppBaseUrl` no existe).

- [ ] **Step 3: Implementar** y generar la migración:

```powershell
dotnet ef migrations add MakeNotificationTenantOptional --project src/Modules/Notifications/Modules.Notifications.Infrastructure --context NotificationsDbContext -o Persistence/Migrations
```

- [ ] **Step 4: Correr y ver PASS**

```powershell
dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests
dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --filter "FullyQualifiedName~OutboxDeliveryWorkerTests|FullyQualifiedName~DeliveryWorkersCharacterizationTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~ConfigurationExampleTests"
```

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Notifications/ src/Api/appsettings.json src/Api/appsettings.example.json k8s/prod-configMap.yaml tests/Modules/Notifications/
git commit -m "feat(notifications): allow tenantless emails and add the app base url"
```

---

### Task 7: Tres plantillas y tres workers de correo de cuenta

**Files:**
- Create: `src/Modules/Notifications/Modules.Notifications.Application/AccountLinks.cs`, `EmailVerificationEmailTemplate.cs`, `PasswordResetEmailTemplate.cs`, `ExistingAccountEmailTemplate.cs`
- Create: `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/EmailVerificationDeliveryWorker.cs`, `PasswordResetDeliveryWorker.cs`, `ExistingAccountDeliveryWorker.cs`
- Modify: `src/Modules/Notifications/Modules.Notifications.Infrastructure/NotificationsInfrastructureExtensions.cs` (tres `AddHostedService`)
- Test: `tests/Modules/Notifications/Modules.Notifications.UnitTests/AccountEmailTemplatesTests.cs`; `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/AccountEmailDeliveryTests.cs`, `NotificationsDeliveryHarness.cs` (helper `Task<Guid?> TenantIdOfAsync(string connectionString, Guid recipientId, string templateRef)` — `tenant_id` de la notificación de ese destinatario y plantilla)
- Modify (test): `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/DeliveryWorkersHarnessTests.cs` — `TheSwitchPutsTheSixWorkersBack` pasa a `TheSwitchPutsTheNineWorkersBack` y afirma `9`: `IsDeliveryWorker` cuenta toda subclase de `OutboxDeliveryWorker`, y esta task suma tres.

**Interfaces:**
- Produces:
  ```csharp
  public static class AccountLinks
  {
      public static string VerifyEmail(string appBaseUrl, string token);   // {base}/verify-email?token={Uri.EscapeDataString(token)}
      public static string ResetPassword(string appBaseUrl, string token); // {base}/reset-password?token=…
      public static string Login(string appBaseUrl);                       // {base}/login
      public static string ForgotPassword(string appBaseUrl);              // {base}/forgot-password
  }
  public static class EmailVerificationEmailTemplate { public const string TemplateRef = "identity.email-verification.v1"; public static EmailMessage Render(string recipientAddress, string verifyUrl); }
  public static class PasswordResetEmailTemplate { public const string TemplateRef = "identity.password-reset.v1"; public static EmailMessage Render(string recipientAddress, string resetUrl); }
  public static class ExistingAccountEmailTemplate { public const string TemplateRef = "identity.existing-account.v1"; public static EmailMessage Render(string recipientAddress, string loginUrl, string forgotPasswordUrl); }
  ```
- Asuntos (español neutro, tuteo): «Confirma tu correo», «Restablece tu contraseña», «Ya tienes una cuenta». El de reset dice que el enlace vence en 1 hora; el de verificación, en 24 horas. Ninguno nombra un tenant.
- Workers (`internal sealed`, heredan `OutboxDeliveryWorker`): `Consumer` = `notifications.email-verification-email` / `notifications.password-reset-email` / `notifications.existing-account-email`; `EventName` = las constantes de `IdentityIntegrationEvents`; `DeliverAsync` llama `ResolveRecipientAsync(context, tenantId: null, userId, TemplateRef, …)`; token ausente → `MarkFailed("email_token_unavailable")` como hace `InvitationDeliveryWorker.cs:37-44`.

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// AccountEmailTemplatesTests.cs
[Fact] public void TheVerificationMailCarriesTheVerifyLinkInBothBodies()
{
    var url = AccountLinks.VerifyEmail("https://qep.qcode.co/", "abc_-1");
    Assert.Equal("https://qep.qcode.co/verify-email?token=abc_-1", url);
    var message = EmailVerificationEmailTemplate.Render("a@x.com", url);
    Assert.Contains(url, message.TextBody);
    Assert.Contains($"href=\"{url}\"", message.HtmlBody);
    Assert.Equal("Confirma tu correo", message.Subject);
}

[Fact] public void TheResetMailSaysTheLinkLastsOneHour()
    => Assert.Contains("1 hora", PasswordResetEmailTemplate.Render("a@x.com", "https://q/reset-password?token=t").TextBody);

[Fact] public void TheExistingAccountMailLinksToLoginAndForgotPassword()
{
    var message = ExistingAccountEmailTemplate.Render("a@x.com", AccountLinks.Login("https://q"), AccountLinks.ForgotPassword("https://q"));
    Assert.Contains("https://q/login", message.TextBody);
    Assert.Contains("https://q/forgot-password", message.TextBody);
}

[Fact] public void TheAccountTemplateRefsAreVersionOne()
{
    Assert.Equal("identity.email-verification.v1", EmailVerificationEmailTemplate.TemplateRef);
    Assert.Equal("identity.password-reset.v1", PasswordResetEmailTemplate.TemplateRef);
    Assert.Equal("identity.existing-account.v1", ExistingAccountEmailTemplate.TemplateRef);
}
```

```csharp
// AccountEmailDeliveryTests.cs — mismo esquema que DeliveryWorkersCharacterizationTests (runDeliveryWorkers: true)
[Fact] public async Task AVerificationEventSendsOneMailAndRecordsItWithoutTenant()
{
    var (_, ownerUserId, email) = await RegisterOwnerAsync(factory);
    await InsertOutboxAsync(cs, IdentityIntegrationEvents.EmailVerificationRequested, $$"""{"userId":"{{ownerUserId}}","token":"tok_1"}""");
    var row = await WaitForNotificationAsync(cs, ownerUserId, EmailVerificationEmailTemplate.TemplateRef);
    Assert.Equal("Sent", row!.Status);
    Assert.Null(await TenantIdOfAsync(cs, ownerUserId, EmailVerificationEmailTemplate.TemplateRef));
    Assert.Contains("http://localhost:3002/verify-email?token=tok_1", Assert.Single(channel.Sent).TextBody);
}

[Fact] public async Task AResetEventSendsTheResetLink()
{
    var (_, ownerUserId, _) = await RegisterOwnerAsync(factory);
    await InsertOutboxAsync(cs, IdentityIntegrationEvents.PasswordResetRequested, $$"""{"userId":"{{ownerUserId}}","token":"tok_2"}""");
    var row = await WaitForNotificationAsync(cs, ownerUserId, PasswordResetEmailTemplate.TemplateRef);
    Assert.Equal("Sent", row!.Status);
    Assert.Null(await TenantIdOfAsync(cs, ownerUserId, PasswordResetEmailTemplate.TemplateRef));
    var mail = Assert.Single(channel.Sent);
    Assert.Equal("Restablece tu contraseña", mail.Subject);
    Assert.Contains("http://localhost:3002/reset-password?token=tok_2", mail.TextBody);
}

[Fact] public async Task AnExistingAccountEventSendsLoginAndForgotLinks()
{
    var (_, ownerUserId, _) = await RegisterOwnerAsync(factory);
    await InsertOutboxAsync(cs, IdentityIntegrationEvents.ExistingAccountNotice, $$"""{"userId":"{{ownerUserId}}"}""");
    var row = await WaitForNotificationAsync(cs, ownerUserId, ExistingAccountEmailTemplate.TemplateRef);
    Assert.Equal("Sent", row!.Status);
    Assert.Null(await TenantIdOfAsync(cs, ownerUserId, ExistingAccountEmailTemplate.TemplateRef));
    var mail = Assert.Single(channel.Sent);
    Assert.Equal("Ya tienes una cuenta", mail.Subject);
    Assert.Contains("http://localhost:3002/login", mail.TextBody);
    Assert.Contains("http://localhost:3002/forgot-password", mail.TextBody);
}
```

```csharp
// DeliveryWorkersHarnessTests.cs — renombrada; mismo arrange (StartDatabaseAsync + NotificationsApiFactory(cs, runDeliveryWorkers: true))
[Fact] public async Task TheSwitchPutsTheNineWorkersBack()
{
    Assert.Equal(9, factory.Services.GetServices<IHostedService>().Count(service => IsDeliveryWorker(service.GetType())));
}
```

- [ ] **Step 2: Correr y ver FAIL**

```powershell
dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --filter "FullyQualifiedName~AccountEmailTemplatesTests"
```
Esperado: no compila (`AccountLinks` no existe).

- [ ] **Step 3: Implementar** plantillas (HTML con `WebUtility.HtmlEncode` donde haya texto variable, como `InvitationEmailTemplate`), links y workers.

- [ ] **Step 4: Correr y ver PASS**

```powershell
dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --filter "FullyQualifiedName~AccountEmailTemplatesTests"
dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --filter "FullyQualifiedName~AccountEmailDeliveryTests|FullyQualifiedName~DeliveryWorkersHarnessTests"
```

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Notifications/ tests/Modules/Notifications/Modules.Notifications.UnitTests/AccountEmailTemplatesTests.cs tests/Modules/Notifications/Modules.Notifications.IntegrationTests/AccountEmailDeliveryTests.cs tests/Modules/Notifications/Modules.Notifications.IntegrationTests/NotificationsDeliveryHarness.cs tests/Modules/Notifications/Modules.Notifications.IntegrationTests/DeliveryWorkersHarnessTests.cs
git commit -m "feat(notifications): deliver verification, reset and existing-account emails"
```

---

### Task 8: Tenancy — precomprobación de registro (formato + slug libre)

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/Tenant.cs` (estático público que reusa los validadores privados)
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantSlugAvailability.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantRegistration.cs`, `TenantRegistrationService.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantSlugAvailability.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenancyInfrastructureExtensions.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantTests.cs`, `TenantRegistrationServiceTests.cs`. En esa clase ya existen el campo estático `Data` y `NewService(steps, modules, defaults)` (`TenantRegistrationServiceTests.cs:12, 67`); no se pisan:
  - `NewService(List<string> steps, RecordingTenantModuleRepository modules, ITenantModuleDefaults defaults, ITenantSlugAvailability? slugAvailability = null)` — el parámetro nuevo es opcional (por defecto un `RecordingSlugAvailability` sin slugs tomados), así las pruebas de hoy no cambian.
  - `sealed class RecordingSlugAvailability(params string[] takenSlugs) : ITenantSlugAvailability` — `List<string> Asked`; `IsTakenAsync` registra el slug y responde si está en `takenSlugs`.
  - `static (TenantRegistrationService Service, RecordingSlugAvailability Availability) NewSlugService(params string[] takenSlugs)` — `NewService([], new RecordingTenantModuleRepository([]), new FixedDefaults([]), availability)`.
  - `static TenantRegistrationData DataWithSlug(string slug)` — `Data with { Slug = slug }`.

**Interfaces:**
- Produces:
  ```csharp
  // Tenant.cs — mismas reglas y mismos códigos que el constructor; devuelve el slug normalizado.
  public static string EnsureValidRegistration(string slug, string displayName, string defaultCulture, string timeZone, string dateFormat);
  public interface ITenantSlugAvailability { Task<bool> IsTakenAsync(string normalizedSlug, CancellationToken cancellationToken); }
  // ITenantRegistration
  /// <exception cref="TenantDomainException">tenancy.slug.invalid, tenancy.settings.*.invalid o tenancy.slug.taken.</exception>
  Task EnsureRegistrableAsync(TenantRegistrationData data, CancellationToken cancellationToken);
  ```
  `TenancyUnitOfWork` sigue traduciendo `IX_tenants_slug` como red de seguridad de la carrera.

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// TenantTests.cs
[Theory]
[InlineData("a", "Acme", "tenancy.slug.invalid")]
[InlineData("acme", "x", "tenancy.settings.display_name.invalid")]
public void EnsureValidRegistrationThrowsTheSameCodesAsCreate(string slug, string name, string code)
    => Assert.Equal(code, Assert.Throws<TenantDomainException>(() =>
        Tenant.EnsureValidRegistration(slug, name, "es-CO", "America/Bogota", "dd/MM/yyyy")).Code);

// TenantRegistrationServiceTests.cs
[Fact] public async Task EnsureRegistrableThrowsSlugTakenWhenTheSlugExists()
{
    var (service, availability) = NewSlugService("acme");
    var error = await Assert.ThrowsAsync<TenantDomainException>(() => service.EnsureRegistrableAsync(DataWithSlug("  ACME "), default));
    Assert.Equal("tenancy.slug.taken", error.Code);
    Assert.Equal(["acme"], availability.Asked); // pregunta con el slug normalizado
}

[Fact] public async Task EnsureRegistrableValidatesBeforeAskingForTheSlug()
{
    var (service, availability) = NewSlugService();
    var error = await Assert.ThrowsAsync<TenantDomainException>(() => service.EnsureRegistrableAsync(DataWithSlug("a"), default));
    Assert.Equal("tenancy.slug.invalid", error.Code);
    Assert.Empty(availability.Asked);
}
```

- [ ] **Step 2: Correr y ver FAIL**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantTests|FullyQualifiedName~TenantRegistrationServiceTests"
```
Esperado: no compila (`EnsureValidRegistration`, `EnsureRegistrableAsync` no existen).

- [ ] **Step 3: Implementar** (la consulta: `dbContext.Tenants.AnyAsync(tenant => tenant.Slug == normalizedSlug, ct)`).

- [ ] **Step 4: Correr y ver PASS** (mismo comando).

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Tenancy/ tests/Modules/Tenancy/Modules.Tenancy.UnitTests/
git commit -m "feat(tenancy): pre-check tenant registration before any user branch"
```

---

### Task 9: `SessionEstablisher` extraído de `EstablishAsync` (refactor)

**Files:**
- Create: `src/Api/SessionEstablisher.cs`
- Modify: `src/Api/AuthSessionEndpoints.cs` (`EstablishAsync` y `GetCurrentSessionAsync` delegan; `ToActiveTenantResponsesAsync` se muda), `src/Api/Program.cs` (`builder.Services.AddScoped<SessionEstablisher>();`)

**Interfaces:**
- Produces:
  ```csharp
  internal sealed class SessionEstablisher(
      IMembershipActivation membershipActivation, IActiveTenantsQuery activeTenantsQuery, ITenantRoleCatalog roleCatalog,
      ITenantDirectory tenantDirectory, ICurrenciesInUse currenciesInUse, ISessionService sessionService,
      IOptions<QepSessionOptions> sessionOptions, IHostEnvironment environment)
  {
      // Acepta las Invited, arma el cuerpo ANTES de emitir (sin cookie viva tras un error), emite, escribe la cookie, 200.
      public Task<IResult> EstablishAsync(HttpContext httpContext, Guid userId, string? email, CancellationToken cancellationToken);
      // Lo que hoy arma /auth/me, sin aceptar invitaciones ni emitir.
      public Task<SessionResponse> DescribeAsync(Guid userId, string? email, CancellationToken cancellationToken);
  }
  ```
- Consumes: el orden y los comentarios de `AuthSessionEndpoints.cs:101-130` se mudan tal cual.

Sin comportamiento nuevo: es el paso REFACTOR, cubierto por las pruebas existentes (verde antes y verde después; no hay RED que escribir).

- [ ] **Step 1: Correr las pruebas de caracterización (verde de partida)**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~AuthSessionApiTests|FullyQualifiedName~RealAuthenticationApiTests|FullyQualifiedName~InvitationApiTests"
```
- [ ] **Step 2: Mover el código** con las firmas de arriba.
- [ ] **Step 3: Correr el mismo comando y ver PASS.**
- [ ] **Step 4: Commit**

```powershell
git add src/Api/SessionEstablisher.cs src/Api/AuthSessionEndpoints.cs src/Api/Program.cs
git commit -m "refactor(api): extract session establishment from the session endpoint"
```

---

### Task 10: `POST /auth/password/register-tenant`

**Files:**
- Create: `src/Modules/Identity/Modules.Identity.Application/PasswordRegistration.cs`
- Create: `src/Api/PasswordAuthEndpoints.cs`
- Modify: `src/Api/RegistrationEndpoints.cs` (`internal static TenantRegistrationData ToRegistrationData(string displayName, string slug, string? culture, string? timeZone, string? dateFormat)` y `internal static bool IsEnabled` / título del 403 compartidos), `src/Api/Program.cs` (`app.MapPasswordAuthEndpoints();` tras `MapRegistrationEndpoints`)
- Modify: `src/Modules/Identity/Modules.Identity.Infrastructure/IdentityInfrastructureExtensions.cs` (el validador no necesita registro: lo levanta el `AddValidatorsFromAssemblyContaining<NewPasswordValidator>` de la Task 2, que escanea todo Identity.Application)
- Modify: `src/Modules/Identity/Modules.Identity.Infrastructure/Persistence/IdentityUnitOfWork.cs` (traduce `IX_users_email`, ver Step 3)
- Test: `tests/Modules/Identity/Modules.Identity.IntegrationTests/PasswordAuthApiTests.cs`, `PasswordAuthApiHarness.cs` (helpers de abajo)

**Interfaces:**
- Produces (Identity.Application):
  ```csharp
  public sealed record PasswordRegistrationInput(string Email, string Password);
  public sealed class PasswordRegistrationValidator : AbstractValidator<PasswordRegistrationInput>; // errors.Email (identity.email.invalid, vía User.NormalizeEmail), errors.Password (QepPassword); sin OverridePropertyName (D10)
  public sealed record PasswordRegistrationOutcome(Guid? CreatedUserId) { public bool IsExistingAccount => CreatedUserId is null; }
  public interface IPasswordRegistration
  {
      Task ValidateAsync(string email, string password, CancellationToken cancellationToken); // ValidationException → 422 validation.failed
      // Hashea PRIMERO; después busca el email. Nuevo: CreateInvited + Activate + credencial + audita identity.password.registered, guarda.
      // Existente: no toca nada salvo encolar ExistingAccountNotice, guarda.
      Task<PasswordRegistrationOutcome> ProvisionOwnerAsync(string email, string password, string correlationId, CancellationToken cancellationToken);
      // Emite el token verify y encola EmailVerificationRequested, guarda.
      Task RequestEmailVerificationAsync(Guid userId, string correlationId, CancellationToken cancellationToken);
  }
  ```
- Produces (Api):
  ```csharp
  public sealed record RegisterTenantWithPasswordRequest(string Email, string Password, string DisplayName, string Slug,
      string? DefaultCulture = null, string? TimeZone = null, string? DateFormat = null);
  ```
  Orden del handler: flag → `ValidateAsync` → `EnsureRegistrableAsync` → `ProvisionOwnerAsync` → (nuevo) `RegisterOwnerTenantAsync` → `RequestEmailVerificationAsync` → `Results.Accepted(uri: null, value: new { })`. El mail de verificación sale **después** del tenant: el perdedor de una carrera por el slug no recibe mail (Review Focus 1). Endpoint `.AllowAnonymous()`, `.WithTags("Authentication")`, `.Produces(202)`, `.ProducesProblem(403)`, `.ProducesProblem(422)`. Flag apagado → el mismo `Problem` 403 que `/auth/register-tenant` (`RegistrationEndpoints.cs:61-63`, title `Public tenant registration is disabled.`), antes de leer el cuerpo.
- Modifies (Infrastructure): `IdentityUnitOfWork.SaveChangesAsync` atrapa `DbUpdateException` con `PostgresException { SqlState: "23505", ConstraintName: "IX_users_email" }` —por **nombre de índice**, como `TenancyUnitOfWork` con `IX_tenants_slug`—, llama `dbContext.ChangeTracker.Clear()` (como `CustomersUnitOfWork` con el BSUID: sin eso el `User` perdedor queda `Added` y el siguiente `SaveChanges` del scope lo reintenta) y tira `IdentityDomainException("identity.user.email_taken", "Another user already uses that email.")`. Cualquier otro 23505 sigue sin traducir.
- Produces (test), en `PasswordAuthApiHarness`:
  - `Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email, string password, string slug)` — `POST /api/v1/auth/password/register-tenant` con `displayName = "Acme"`, vía `PostJsonAsync`.
  - `Task<IReadOnlyList<string>> ReadOutboxAsync(string connectionString, string eventName, Guid userId)` — `payload::text` de cada fila con ese evento y `payload->>'userId' = userId`, en orden de `occurred_at`. (Distinta de `ReadOutboxRowAsync` de la Task 4.)
  - `Task<Guid> FindUserIdAsync(string connectionString, string email)` — `id` de `identity.users` por email normalizado; falla si no existe.
  - `Task<long> CountTenantsWithSlugAsync(string connectionString, string slug)`.
  - `Task<long> CountAuditAsync(string connectionString, Guid resourceId, string action)` — `audit.entries` por `resource_id` y `action`, como `OrphanUserCleanupTests.CountAuditAsync`.
  - `Task<string?> CredentialHashAsync(string connectionString, Guid userId)` — `hash` de `identity.password_credentials`.
  - `Task<long> CountUsersWithEmailAsync(string connectionString, string email)`.
  - `Task<long> CountOutboxAsync(string connectionString, string eventName)`.
  - `Task<long> CountTokensWithHashAsync(string connectionString, string tokenHash)`.
  - `sealed record ProblemPayload(string? Code, string? Title, string? Detail, Dictionary<string, string[]>? Errors)`; `Task<ProblemPayload> ProblemAsync(HttpResponseMessage response)` — lee el ProblemDetails (`code` y `errors` son extensiones de primer nivel).

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// Cada prueba: StartDatabaseAsync, cs, factory = new PasswordAuthApiFactory(cs), client = CreateClient(factory),
// email = NewEmail(), slug/slug1/slug2/takenSlug = NewSlug(), pw = oldPassword = "correct horse battery",
// newPassword = "a brand new long password", ct = TestContext.Current.CancellationToken.
// Estas convenciones valen para todas las pruebas de PasswordAuthApiTests de las Tasks 10–14 y 17.
[Fact] public async Task RegisterWithANewEmailCreatesTheTenantAndQueuesOneVerificationMail()
{
    var response = await RegisterAsync(client, email, "correct horse battery", slug);
    Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    Assert.Equal("{}", await response.Content.ReadAsStringAsync(ct));
    var userId = await FindUserIdAsync(cs, email);
    Assert.Null(await EmailVerifiedAtAsync(cs, userId));
    Assert.Equal(1, await CountRowsAsync(cs, "identity.password_credentials", userId));
    Assert.Single(await ReadOutboxAsync(cs, IdentityIntegrationEvents.EmailVerificationRequested, userId));
    Assert.Equal(1, await CountTenantsWithSlugAsync(cs, slug));
    Assert.Equal(1, await CountAuditAsync(cs, userId, "identity.password.registered"));
}

[Fact] public async Task RegisterWithAnExistingEmailCreatesNothingAndQueuesTheExistingAccountMail()
{
    await RegisterAsync(client, email, "correct horse battery", slug1);
    var userId = await FindUserIdAsync(cs, email);
    var hashBefore = await CredentialHashAsync(cs, userId);
    var second = await RegisterAsync(client, "  " + email.ToUpperInvariant(), "another long password", slug2);
    Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
    Assert.Equal("{}", await second.Content.ReadAsStringAsync(ct));
    Assert.Equal(0, await CountTenantsWithSlugAsync(cs, slug2));
    Assert.Equal(hashBefore, await CredentialHashAsync(cs, userId));
    Assert.Single(await ReadOutboxAsync(cs, IdentityIntegrationEvents.ExistingAccountNotice, userId));
    Assert.Single(await ReadOutboxAsync(cs, IdentityIntegrationEvents.EmailVerificationRequested, userId)); // sólo el del primer registro
}

[Fact] public async Task RegisterWithATakenSlugReturnsTheSame422ForNewAndExistingEmails()
{
    await RegisterAsync(client, email, pw, takenSlug);
    var withNewEmail = await RegisterAsync(client, NewEmail(), pw, takenSlug);
    var withExistingEmail = await RegisterAsync(client, email, pw, takenSlug);
    Assert.Equal(HttpStatusCode.UnprocessableEntity, withNewEmail.StatusCode);
    Assert.Equal("tenancy.slug.taken", (await ProblemAsync(withNewEmail)).Code);
    Assert.Equal("tenancy.slug.taken", (await ProblemAsync(withExistingEmail)).Code);
    Assert.Equal(withNewEmail.StatusCode, withExistingEmail.StatusCode);
}

[Fact] public async Task RegisterValidatesThePasswordBeforeTheSlug()
{
    await RegisterAsync(client, email, pw, takenSlug);
    var response = await RegisterAsync(client, NewEmail(), "short", takenSlug);
    Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    var problem = await ProblemAsync(response);
    Assert.Equal("validation.failed", problem.Code);
    // ApiExceptionHandler.cs:66 agrupa por PropertyName de FluentValidation: llega "Password", no "password".
    // El frontend lee esa clave; no cambiar la política de nombres sin tocar los dos lados.
    Assert.True(problem.Errors!.ContainsKey("Password"));
}

[Fact] public async Task ConcurrentRegistrationsWithTheSameNewEmailBothAnswer202AndCreateOneUser()
{
    var responses = await Task.WhenAll(RegisterAsync(client, email, pw, slug1), RegisterAsync(client, email, pw, slug2));
    Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
    Assert.Equal(1, await CountUsersWithEmailAsync(cs, email));
    var userId = await FindUserIdAsync(cs, email);
    // el perdedor siguió el camino de email existente: un aviso, un solo tenant
    Assert.Single(await ReadOutboxAsync(cs, IdentityIntegrationEvents.ExistingAccountNotice, userId));
    Assert.Equal(1, await CountTenantsWithSlugAsync(cs, slug1) + await CountTenantsWithSlugAsync(cs, slug2));
}

[Fact] public async Task RegisterIsForbiddenWhenSignupIsDisabled()
{
    using var disabled = new PasswordAuthApiFactory(cs, signupEnabled: false);
    var response = await RegisterAsync(CreateClient(disabled), email, pw, slug);
    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    Assert.Equal("Public tenant registration is disabled.", (await ProblemAsync(response)).Title);
    Assert.Equal(0, await CountUsersWithEmailAsync(cs, email));
    Assert.Equal(0, await CountTenantsWithSlugAsync(cs, slug));
}

// Review Focus 1
[Fact] public async Task ConcurrentRegistrationsWithTheSameSlugCreateOneTenantAndQueueOneVerificationMail()
{
    var responses = await Task.WhenAll(RegisterAsync(client, NewEmail(), pw, slug), RegisterAsync(client, NewEmail(), pw, slug));
    Assert.Equal([HttpStatusCode.Accepted, HttpStatusCode.UnprocessableEntity], responses.Select(r => r.StatusCode).Order());
    Assert.Equal("tenancy.slug.taken", (await ProblemAsync(responses.Single(r => r.StatusCode == HttpStatusCode.UnprocessableEntity))).Code);
    Assert.Equal(1, await CountTenantsWithSlugAsync(cs, slug));
    Assert.Equal(1, await CountOutboxAsync(cs, IdentityIntegrationEvents.EmailVerificationRequested));
}

// Review Focus 5
[Fact] public async Task TheVerificationOutboxPayloadCarriesOnlyTheUserIdAndTheToken()
{
    await RegisterAsync(client, email, "correct horse battery", slug);
    var userId = await FindUserIdAsync(cs, email);
    var payload = JsonDocument.Parse(Assert.Single(await ReadOutboxAsync(cs, IdentityIntegrationEvents.EmailVerificationRequested, userId)));
    Assert.Equal(["token", "userId"], payload.RootElement.EnumerateObject().Select(p => p.Name).Order());
    var token = payload.RootElement.GetProperty("token").GetString()!;
    Assert.Equal(1, await CountTokensWithHashAsync(cs, EmailTokenCodec.Hash(token)));
    Assert.DoesNotContain("correct horse battery", payload.RootElement.GetRawText());
    Assert.DoesNotContain(email, payload.RootElement.GetRawText());
}
```

- [ ] **Step 2: Correr y ver FAIL**

```powershell
dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests --filter "FullyQualifiedName~PasswordAuthApiTests"
```
Esperado: `Assert.Equal() Failure: Expected: Accepted Actual: NotFound` (la ruta no existe) o no compila por `IdentityIntegrationEvents`/`EmailTokenCodec` si faltan usings.

- [ ] **Step 3: Implementar** con las firmas y el orden de arriba. La carrera por el mismo email nuevo: `IdentityUnitOfWork` traduce la violación de `IX_users_email` a `IdentityDomainException("identity.user.email_taken")` después de limpiar el change tracker (ver Interfaces). `ProvisionOwnerAsync` atrapa **sólo** ese código y, en el mismo scope: vuelve a `FindByEmailAsync` para leer al ganador, encola `ExistingAccountNotice` con el `userId` del ganador, guarda y devuelve `IsExistingAccount`. El perdedor sigue el camino de «email existente» (mail de cuenta existente, 202, sin tenant).

- [ ] **Step 4: Correr y ver PASS** (mismo comando).

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Identity/Modules.Identity.Application/PasswordRegistration.cs src/Modules/Identity/Modules.Identity.Infrastructure/IdentityInfrastructureExtensions.cs src/Modules/Identity/Modules.Identity.Infrastructure/Persistence/IdentityUnitOfWork.cs src/Api/PasswordAuthEndpoints.cs src/Api/RegistrationEndpoints.cs src/Api/Program.cs tests/Modules/Identity/Modules.Identity.IntegrationTests/PasswordAuthApiTests.cs tests/Modules/Identity/Modules.Identity.IntegrationTests/PasswordAuthApiHarness.cs
git commit -m "feat(auth): register a tenant with email and password"
```

---

### Task 11: `POST /auth/email/verify`

**Files:**
- Create: `src/Modules/Identity/Modules.Identity.Application/EmailVerification.cs`
- Modify: `src/Api/PasswordAuthEndpoints.cs`, `IdentityInfrastructureExtensions.cs`
- Test: `tests/Modules/Identity/Modules.Identity.IntegrationTests/PasswordAuthApiTests.cs`, `PasswordAuthApiHarness.cs` (helpers de abajo)

**Interfaces:**
- Produces (test), en `PasswordAuthApiHarness`:
  - `sealed record SessionTenantPayload(Guid TenantId, string DisplayName)`; `sealed record SessionPayload(Guid UserId, string? Email, IReadOnlyList<SessionTenantPayload> ActiveTenants)` — lo que la prueba lee del cuerpo de `/auth/session` y `/auth/me` (`SessionResponse` de `AuthSessionEndpoints.cs:257`).
  - `Task<HttpResponseMessage> VerifyAsync(HttpClient client, string token)` — `POST /api/v1/auth/email/verify` con `{ token }`.
  - `Task<string> LatestOutboxTokenAsync(string connectionString, string eventName, Guid userId)` — `payload->>'token'` de la fila más reciente de ese evento y usuario (el único lugar donde la prueba puede leer un token crudo: la base guarda el hash).
  - `sealed record RegisteredOwner(Guid UserId, Guid TenantId)`; `Task<RegisteredOwner> RegisterAndVerifyAsync(HttpClient client, string connectionString, string email, string password)` — `RegisterAsync` con `NewSlug()`, `LatestOutboxTokenAsync(EmailVerificationRequested)`, `VerifyAsync`; afirma 202 y 200, deja al cliente con la cookie de sesión y devuelve el usuario y el único tenant activo del cuerpo del verify.
- Produces:
  ```csharp
  public sealed record AuthenticatedUser(Guid UserId, string Email);
  public interface IEmailVerification
  {
      // TryConsumeAsync(Hash(token), Verify) → null ⇒ IdentityDomainException("identity.email_token.invalid").
      // Después: MarkEmailVerified, Activate, audita identity.email.verified, guarda.
      Task<AuthenticatedUser> VerifyAsync(string rawToken, CancellationToken cancellationToken);
  }
  public sealed record VerifyEmailRequest(string Token);
  ```
  Endpoint: `VerifyAsync` → `SessionEstablisher.EstablishAsync(httpContext, user.UserId, user.Email, ct)`.

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
[Fact] public async Task RegisterThenVerifyFromTheCapturedMailOpensASession()
{
    var channel = new RecordingEmailChannel();
    using var factory = new PasswordAuthApiFactory(cs, channel);
    var client = CreateClient(factory);
    await RegisterAsync(client, email, pw, slug);
    var userId = await FindUserIdAsync(cs, email);
    await WaitUntilAsync(() => Task.FromResult(channel.Sent.Count == 1), TimeSpan.FromSeconds(30)); // el worker sondea cada 15 s
    var token = Regex.Match(Assert.Single(channel.Sent).TextBody, @"verify-email\?token=([A-Za-z0-9_-]+)").Groups[1].Value;
    var response = await VerifyAsync(client, token);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("qep_session=", StringComparison.Ordinal));
    var me = await client.GetFromJsonAsync<SessionPayload>("/api/v1/auth/me", ct);
    Assert.Single(me!.ActiveTenants);
    Assert.Equal(1, await CountAuditAsync(cs, userId, "identity.email.verified"));
}

[Fact] public async Task VerifyWithAnUnknownExpiredOrUsedTokenReturnsTheSame422()
{
    // dos registros: el token del primero se vence por SQL
    //   (UPDATE identity.email_tokens SET expires_at = now() - interval '1 minute' WHERE user_id = @expiredUser);
    // el del segundo se usa una vez (200) y se repite
    var unknown = await VerifyAsync(client, "nope");
    var expired = await VerifyAsync(client, expiredToken);
    var used = await VerifyAsync(client, usedToken); // segundo uso
    Assert.All(new[] { unknown, expired, used }, r => Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode));
    var problems = new[] { await ProblemAsync(unknown), await ProblemAsync(expired), await ProblemAsync(used) };
    Assert.All(problems, p => Assert.Equal("identity.email_token.invalid", p.Code));
    Assert.Single(problems.Select(p => (p.Title, p.Detail)).Distinct());
}

// Review Focus 3
[Fact] public async Task ConcurrentVerifyWithTheSameTokenOpensExactlyOneSession()
{
    // registrar; token = LatestOutboxTokenAsync(cs, EmailVerificationRequested, userId); clientA y clientB = CreateClient(factory)
    var responses = await Task.WhenAll(VerifyAsync(clientA, token), VerifyAsync(clientB, token));
    Assert.Equal([HttpStatusCode.OK, HttpStatusCode.UnprocessableEntity], responses.Select(r => r.StatusCode).Order());
    Assert.Equal(1, await CountRowsAsync(cs, "identity.sessions", userId));
}
```

- [ ] **Step 2: Correr y ver FAIL**

```powershell
dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests --filter "FullyQualifiedName~PasswordAuthApiTests.RegisterThenVerifyFromTheCapturedMailOpensASession|FullyQualifiedName~PasswordAuthApiTests.VerifyWithAnUnknownExpiredOrUsedTokenReturnsTheSame422|FullyQualifiedName~PasswordAuthApiTests.ConcurrentVerifyWithTheSameTokenOpensExactlyOneSession"
```
Esperado: `Expected: OK Actual: NotFound`.

- [ ] **Step 3: Implementar.**
- [ ] **Step 4: Correr y ver PASS** (mismo comando).
- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Identity/Modules.Identity.Application/EmailVerification.cs src/Modules/Identity/Modules.Identity.Infrastructure/IdentityInfrastructureExtensions.cs src/Api/PasswordAuthEndpoints.cs tests/Modules/Identity/Modules.Identity.IntegrationTests/PasswordAuthApiTests.cs tests/Modules/Identity/Modules.Identity.IntegrationTests/PasswordAuthApiHarness.cs
git commit -m "feat(auth): verify the email and open the first session"
```

---

### Task 12: `POST /auth/password/login`

**Files:**
- Create: `src/Modules/Identity/Modules.Identity.Application/PasswordLogin.cs`, `PasswordCredentialVerifier.cs`
- Modify: `src/Api/PasswordAuthEndpoints.cs`, `src/Modules/Identity/Modules.Identity.Infrastructure/IdentityInfrastructureExtensions.cs`
- Test: `tests/Modules/Identity/Modules.Identity.UnitTests/PasswordLoginServiceTests.cs`, `PasswordCredentialVerifierTests.cs`, `IdentityApplicationTestDoubles.cs`; `tests/Modules/Identity/Modules.Identity.IntegrationTests/PasswordAuthApiTests.cs`, `PasswordAuthApiHarness.cs` (helpers de abajo)

**Interfaces:**
- Produces — **el único lugar** con la regla «verificar y re-hashear si el hasher lo pide»; lo usan el login (esta task) y la aceptación de invitación (Task 15):
  ```csharp
  public sealed class PasswordCredentialVerifier(IPasswordHasher hasher, IClock clock)
  {
      // Siempre llama hasher.Verify(credential?.Hash, password): con null corre contra el ficticio.
      // SuccessRehashNeeded ⇒ credential.Replace(hasher.Hash(password), clock.UtcNow). No guarda.
      // true sólo con credencial y Success / SuccessRehashNeeded.
      public bool Verify(PasswordCredential? credential, string password);
  }
  ```
  DI scoped. No es interfaz: las pruebas de unidad lo construyen real sobre `FakePasswordHasher`.
- Produces:
  ```csharp
  public sealed record PasswordLoginInput(string Email, string Password);
  public sealed class PasswordLoginValidator : AbstractValidator<PasswordLoginInput>; // NotEmpty en los dos (sin reglas de largo: no se enumera nada)
  public interface IPasswordLogin { Task<AuthenticatedUser> LoginAsync(string email, string password, string correlationId, CancellationToken cancellationToken); }
  public sealed class PasswordLoginService(IUserRepository users, IPasswordCredentialRepository credentials, PasswordCredentialVerifier verifier,
      IEmailTokenIssuer tokens, IIdentityOutbox outbox, IIdentityAuditRecorder audit, IIdentityUnitOfWork unitOfWork,
      IValidator<PasswordLoginInput> validator, IClock clock) : IPasswordLogin;
  public sealed record PasswordLoginRequest(string Email, string Password);
  ```
- Produces (test), en `IdentityApplicationTestDoubles.cs`:
  - `sealed class FakePasswordHasher : IPasswordHasher` — `List<string?> VerifiedHashes`; `PasswordVerificationOutcome NextOutcome` (por defecto `Success`) que devuelve `Verify` salvo con hash `null` (siempre `Failed`); `Hash` devuelve `"rehashed"`.
  - `sealed class FakeEmailTokenIssuer : IEmailTokenIssuer` — `List<(Guid UserId, EmailTokenPurpose Purpose)> Issued`; devuelve `"raw-token"`.
  - `sealed class RecordingIdentityOutbox : IIdentityOutbox` — `List<(string EventName, object Payload)> Added`.
  - `sealed class RecordingIdentityAuditRecorder : IIdentityAuditRecorder` — `List<(Guid ActorId, string Action, string ResourceId, AuditActorType ActorType)> Entries`.
- Produces (test), en `PasswordAuthApiHarness`:
  - `Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password)` — `POST /api/v1/auth/password/login`.
  - `Task<(HttpStatusCode Status, string? Code, string? Title, string? Detail)> ShapeAsync(HttpResponseMessage response)` — lo comparable de un error (sin `traceId`).
  - `Task<long> CountAuditByResourceAsync(string connectionString, string action, string resourceId)`.
  - `Task<long> CountAuditContainingAsync(string connectionString, string text)` — filas de `audit.entries` cuyo `row_to_json(e)::text` contiene `text`.
- Reglas (en este orden):
  1. Validar; `User.NormalizeEmail` (si tira, se trata como inexistente).
  2. Buscar usuario y credencial; **siempre** `verifier.Verify(credential, password)` (sin credencial corre contra el ficticio).
  3. `false` o sin usuario → audita `identity.password.login.failed` (actor = usuario si existe; si no, `Guid.Empty`, `AuditActorType.System`, `resourceId = "unknown"`), guarda, tira `RequestUnauthorizedException("identity.credentials.invalid", "Invalid credentials.")`.
  4. El re-hash ya lo hizo `PasswordCredentialVerifier`; se guarda junto con lo que siga.
  5. `EmailVerifiedAt is null` → reemite `verify` + `EmailVerificationRequested`, guarda, tira `RequestForbiddenException("identity.email.not_verified", "The email is not verified.")`.
  6. `Activate`, audita `identity.password.login.succeeded`, guarda, devuelve. El endpoint sigue con `SessionEstablisher.EstablishAsync` (acepta `Invited` y audita `identity.session.issued` como hoy).

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// PasswordCredentialVerifierTests.cs
[Fact] public void ARehashRequestReplacesTheHash()
{
    hasher.NextOutcome = PasswordVerificationOutcome.SuccessRehashNeeded;
    var credential = PasswordCredential.Create(UserId.New(), "old", Now);
    Assert.True(new PasswordCredentialVerifier(hasher, new FixedClock(Now)).Verify(credential, "pw"));
    Assert.Equal("rehashed", credential.Hash);
}

[Fact] public void AMissingCredentialRunsAgainstTheDummyAndFails()
{
    Assert.False(new PasswordCredentialVerifier(hasher, new FixedClock(Now)).Verify(null, "pw"));
    Assert.Equal([(string?)null], hasher.VerifiedHashes);
}

// PasswordLoginServiceTests.cs — service = PasswordLoginService sobre FakeUserRepository, FakePasswordCredentialRepository,
// new PasswordCredentialVerifier(hasher, clock), FakeEmailTokenIssuer, RecordingIdentityOutbox, RecordingIdentityAuditRecorder,
// CountingIdentityUnitOfWork, new PasswordLoginValidator(), FixedClock. Sembrado: usuario verificado con `credential` (hash "h").
[Fact] public async Task AnUnknownEmailStillRunsTheHasherAgainstTheDummy()
{
    await Assert.ThrowsAsync<RequestUnauthorizedException>(() => service.LoginAsync("nobody@x.com", "whatever pw", "c", default));
    Assert.Equal([(string?)null], hasher.VerifiedHashes);
}

[Fact] public async Task LoginRehashesWhenTheHasherAsks()
{
    hasher.NextOutcome = PasswordVerificationOutcome.SuccessRehashNeeded;
    await service.LoginAsync(email, pw, "c", default);
    Assert.Equal("rehashed", credential.Hash);
    Assert.Equal(1, unitOfWork.Saves);
}
```

```csharp
// PasswordAuthApiTests.cs
[Fact] public async Task LoginBeforeVerifyingReturns403AndReissuesTheVerifyToken()
{
    await RegisterAsync(client, email, pw, slug);
    var userId = await FindUserIdAsync(cs, email);
    var firstToken = await LatestOutboxTokenAsync(cs, IdentityIntegrationEvents.EmailVerificationRequested, userId);
    var response = await LoginAsync(client, email, pw);
    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    Assert.Equal("identity.email.not_verified", (await ProblemAsync(response)).Code);
    Assert.Equal(2, (await ReadOutboxAsync(cs, IdentityIntegrationEvents.EmailVerificationRequested, userId)).Count);
    Assert.Equal(HttpStatusCode.UnprocessableEntity, (await VerifyAsync(client, firstToken)).StatusCode); // el anterior quedó invalidado
}

[Fact] public async Task AWrongPasswordAnUnknownEmailAndAGoogleOnlyUserGetTheSame401()
{
    await RegisterAndVerifyAsync(client, cs, email, pw);
    var googleEmail = NewEmail();
    await PostWithGoogleAsync(CreateClient(factory), "/api/v1/auth/register-tenant", IssueGoogleIdToken("g-sub", googleEmail, true),
        new { displayName = "Acme", slug = NewSlug() });
    var wrongPassword = await LoginAsync(client, email, "not the right password");
    var unknownEmail = await LoginAsync(client, NewEmail(), pw);
    var googleOnly = await LoginAsync(client, googleEmail, pw);
    var expected = await ShapeAsync(wrongPassword);
    Assert.Equal(HttpStatusCode.Unauthorized, expected.Status);
    Assert.Equal("identity.credentials.invalid", expected.Code);
    Assert.Equal(expected, await ShapeAsync(unknownEmail));
    Assert.Equal(expected, await ShapeAsync(googleOnly));
}

[Fact] public async Task LoginAfterVerifyingOpensASessionAndIsAudited()
{
    var userId = (await RegisterAndVerifyAsync(CreateClient(factory), cs, email, pw)).UserId;
    var response = await LoginAsync(client, email, pw); // cliente nuevo, sin cookie
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("qep_session=", StringComparison.Ordinal));
    Assert.Equal(userId, (await response.Content.ReadFromJsonAsync<SessionPayload>(ct))!.UserId);
    Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me", ct)).StatusCode);
    Assert.Equal(1, await CountAuditAsync(cs, userId, "identity.password.login.succeeded"));
}

// Review Focus 2
[Fact] public async Task LoginNormalizesEmailCaseAndWhitespace()
{
    await RegisterAndVerifyAsync(CreateClient(factory), cs, email, pw);
    Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, "  " + email.ToUpperInvariant() + " ", pw)).StatusCode);
}

[Fact] public async Task AFailedLoginForAnUnknownEmailIsAuditedWithoutTheEmail()
{
    var unknownEmail = NewEmail();
    Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, unknownEmail, pw)).StatusCode);
    Assert.Equal(1, await CountAuditByResourceAsync(cs, "identity.password.login.failed", "unknown"));
    Assert.Equal(0, await CountAuditContainingAsync(cs, unknownEmail));
}
```

- [ ] **Step 2: Correr y ver FAIL**

```powershell
dotnet test tests/Modules/Identity/Modules.Identity.UnitTests --filter "FullyQualifiedName~PasswordLoginServiceTests|FullyQualifiedName~PasswordCredentialVerifierTests"
dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests --filter "FullyQualifiedName~PasswordAuthApiTests.Login|FullyQualifiedName~PasswordAuthApiTests.AWrongPassword|FullyQualifiedName~PasswordAuthApiTests.AFailedLogin"
```
Esperado: unidad no compila (`PasswordLoginService`, `PasswordCredentialVerifier`); integración `Expected: Forbidden Actual: NotFound`.

- [ ] **Step 3: Implementar.**
- [ ] **Step 4: Correr y ver PASS** (mismos comandos).
- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Identity/Modules.Identity.Application/PasswordLogin.cs src/Modules/Identity/Modules.Identity.Application/PasswordCredentialVerifier.cs src/Modules/Identity/Modules.Identity.Infrastructure/IdentityInfrastructureExtensions.cs src/Api/PasswordAuthEndpoints.cs tests/Modules/Identity/
git commit -m "feat(auth): sign in with email and password"
```

---

### Task 13: `POST /auth/password/forgot` y `POST /auth/password/reset`

**Files:**
- Create: `src/Modules/Identity/Modules.Identity.Application/PasswordReset.cs`
- Modify: `src/Api/PasswordAuthEndpoints.cs`, `src/Modules/Identity/Modules.Identity.Infrastructure/IdentityInfrastructureExtensions.cs`
- Test: `tests/Modules/Identity/Modules.Identity.IntegrationTests/PasswordAuthApiTests.cs`, `PasswordAuthApiHarness.cs` (helpers de abajo)

**Interfaces:**
- Produces (test), en `PasswordAuthApiHarness`:
  - `Task<HttpResponseMessage> ForgotAsync(HttpClient client, string email)` — `POST /api/v1/auth/password/forgot`.
  - `Task<HttpResponseMessage> ResetAsync(HttpClient client, string token, string password)` — `POST /api/v1/auth/password/reset`.
  - `Task<string?> MembershipStateAsync(string connectionString, Guid tenantId, Guid userId)` — `state` de `tenancy.memberships`.
  - `Task<string?> UserStatusAsync(string connectionString, Guid userId)` — `status` de `identity.users`.
  - `sealed record InvitedMember(Guid MembershipId, Guid UserId, string Token)`; `Task<InvitedMember> InviteAsync(HttpClient ownerClient, string connectionString, Guid tenantId, string email)` — `POST /api/v1/tenants/{tenantId}/memberships` con `X-Tenant-Id`, `X-Qep-Client` y el cuerpo de `OrphanUserCleanupTests.SendInviteAsync`; el token crudo sale del payload de `tenancy.membership-invited.v1` de esa membresía, como en `InvitationApiTests.cs:340-360`. Lo reusa la Task 15.
- Produces:
  ```csharp
  public sealed record PasswordResetInput(string Token, string Password);
  public sealed class PasswordResetValidator : AbstractValidator<PasswordResetInput>; // Token NotEmpty; Password QepPassword → errors.Password (D10)
  public interface IPasswordReset
  {
      // NormalizeEmail (inválido ⇒ no hace nada); usuario inexistente ⇒ no hace nada.
      // Existe: emite reset, encola PasswordResetRequested, audita identity.password.reset.requested, guarda.
      Task RequestAsync(string email, string correlationId, CancellationToken cancellationToken);
      // Valida ANTES de consumir. TryConsumeAsync(Reset) null ⇒ identity.email_token.invalid.
      // Crea o reemplaza la credencial, MarkEmailVerified, audita identity.password.reset.completed, guarda,
      // después ISessionService.RevokeAllForUserAsync(userId, "password_reset", ct). No activa (el login lo hace).
      Task ResetAsync(string rawToken, string password, CancellationToken cancellationToken);
  }
  public sealed record ForgotPasswordRequest(string Email);
  public sealed record ResetPasswordRequest(string Token, string Password);
  ```
  Respuestas: forgot `Results.Accepted(uri: null, value: new { })`; reset `Results.NoContent()`.

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
[Fact] public async Task ForgotWithAnUnknownEmailReturns202AndQueuesNothing()
{
    Assert.Equal(HttpStatusCode.Accepted, (await ForgotAsync(client, NewEmail())).StatusCode);
    Assert.Equal(0, await CountOutboxAsync(cs, IdentityIntegrationEvents.PasswordResetRequested));
}

// Review Focus 2
[Fact] public async Task ForgotNormalizesTheEmail()
{
    var userId = (await RegisterAndVerifyAsync(CreateClient(factory), cs, email, pw)).UserId;
    Assert.Equal(HttpStatusCode.Accepted, (await ForgotAsync(client, "  " + email.ToUpperInvariant() + " ")).StatusCode);
    Assert.Single(await ReadOutboxAsync(cs, IdentityIntegrationEvents.PasswordResetRequested, userId));
}

[Fact] public async Task ResetRevokesEarlierSessionsAndOnlyTheNewPasswordLogsIn()
{
    var clientA = CreateClient(factory);
    var clientB = CreateClient(factory);
    var userId = (await RegisterAndVerifyAsync(clientA, cs, email, oldPassword)).UserId; // clientA con sesión viva
    await ForgotAsync(clientB, email);
    var token = await LatestOutboxTokenAsync(cs, IdentityIntegrationEvents.PasswordResetRequested, userId);
    Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(clientB, token, newPassword)).StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, (await clientA.GetAsync("/api/v1/auth/me", ct)).StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(clientB, email, oldPassword)).StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await LoginAsync(clientB, email, newPassword)).StatusCode);
    Assert.Equal(1, await CountAuditAsync(cs, userId, "identity.password.reset.completed"));
}

[Fact] public async Task ResetWithAShortPasswordFailsValidationAndKeepsTheToken()
{
    var userId = (await RegisterAndVerifyAsync(CreateClient(factory), cs, email, pw)).UserId;
    await ForgotAsync(client, email);
    var token = await LatestOutboxTokenAsync(cs, IdentityIntegrationEvents.PasswordResetRequested, userId);
    var shortReset = await ResetAsync(client, token, "short");
    Assert.Equal(HttpStatusCode.UnprocessableEntity, shortReset.StatusCode);
    var problem = await ProblemAsync(shortReset);
    Assert.Equal("validation.failed", problem.Code);
    Assert.True(problem.Errors!.ContainsKey("Password"));
    Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(client, token, "a long enough password")).StatusCode);
}

[Fact] public async Task ResetVerifiesAnUnverifiedEmail()
{
    await RegisterAsync(client, email, pw, slug); // sin verificar
    var userId = await FindUserIdAsync(cs, email);
    await ForgotAsync(client, email);
    var token = await LatestOutboxTokenAsync(cs, IdentityIntegrationEvents.PasswordResetRequested, userId);
    Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(client, token, newPassword)).StatusCode);
    Assert.NotNull(await EmailVerifiedAtAsync(cs, userId));
    Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, email, newPassword)).StatusCode); // no 403
}

[Fact] public async Task AnInvitedUserCreatesAPasswordAndEndsActive()
{
    var ownerClient = CreateClient(factory);
    var owner = await RegisterAndVerifyAsync(ownerClient, cs, NewEmail(), pw);
    var invitedEmail = NewEmail();
    var invited = await InviteAsync(ownerClient, cs, owner.TenantId, invitedEmail);
    await ForgotAsync(client, invitedEmail);
    var token = await LatestOutboxTokenAsync(cs, IdentityIntegrationEvents.PasswordResetRequested, invited.UserId);
    Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(client, token, newPassword)).StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, invitedEmail, newPassword)).StatusCode);
    Assert.Equal("Active", await MembershipStateAsync(cs, owner.TenantId, invited.UserId));
    Assert.Equal("Active", await UserStatusAsync(cs, invited.UserId));
}
```

- [ ] **Step 2: Correr y ver FAIL**

```powershell
dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests --filter "FullyQualifiedName~PasswordAuthApiTests.Forgot|FullyQualifiedName~PasswordAuthApiTests.Reset|FullyQualifiedName~PasswordAuthApiTests.AnInvitedUserCreatesAPasswordAndEndsActive"
```
Esperado: `Expected: Accepted Actual: NotFound`.

- [ ] **Step 3: Implementar.**
- [ ] **Step 4: Correr y ver PASS** (mismo comando).
- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Identity/Modules.Identity.Application/PasswordReset.cs src/Modules/Identity/Modules.Identity.Infrastructure/IdentityInfrastructureExtensions.cs src/Api/PasswordAuthEndpoints.cs tests/Modules/Identity/Modules.Identity.IntegrationTests/PasswordAuthApiTests.cs tests/Modules/Identity/Modules.Identity.IntegrationTests/PasswordAuthApiHarness.cs
git commit -m "feat(auth): recover or create a password by email"
```

---

### Task 14: Política de rate limit `Credentials`

**Files:**
- Modify: `src/Api/RateLimiterPolicies.cs` (`public const string Credentials = "credentials";`)
- Modify: `src/Api/Program.cs` (nueva política dentro de `AddRateLimiter`, líneas 71-104)
- Modify: `src/Api/PasswordAuthEndpoints.cs` (`.RequireRateLimiting(RateLimiterPolicies.Credentials)` en los cinco)
- Test: `tests/Modules/Identity/Modules.Identity.IntegrationTests/PasswordAuthApiTests.cs`

**Interfaces:**
- Produces: política de ventana fija, `PermitLimit = 10`, `Window = 1 min`, `QueueLimit = 0`; clave de partición `$"{(httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText}|{httpContext.Connection.RemoteIpAddress}"` — el **patrón** de ruta y no el path, para que `/invitations/{token}/accept-with-password` no abra una partición por token. `UseForwardedHeaders` ya corre antes (`Program.cs:113`).

- [ ] **Step 1: Escribir la prueba que falla**

```csharp
[Fact] public async Task TheEleventhRequestToOneEndpointIs429AndAnotherEndpointStillAnswers()
{
    for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.Accepted, (await ForgotAsync(client, NewEmail())).StatusCode);
    Assert.Equal(HttpStatusCode.TooManyRequests, (await ForgotAsync(client, NewEmail())).StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, NewEmail(), "whatever password")).StatusCode);
}
```

- [ ] **Step 2: Correr y ver FAIL**

```powershell
dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests --filter "FullyQualifiedName~PasswordAuthApiTests.TheEleventhRequestToOneEndpointIs429AndAnotherEndpointStillAnswers"
```
Esperado: `Expected: TooManyRequests Actual: Accepted`.

- [ ] **Step 3: Implementar.**
- [ ] **Step 4: Correr toda la clase y ver PASS** (ninguna prueba pasa de 10 requests por endpoint):

```powershell
dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests --filter "FullyQualifiedName~PasswordAuthApiTests"
```
- [ ] **Step 5: Commit**

```powershell
git add src/Api/RateLimiterPolicies.cs src/Api/Program.cs src/Api/PasswordAuthEndpoints.cs tests/Modules/Identity/Modules.Identity.IntegrationTests/PasswordAuthApiTests.cs
git commit -m "feat(api): rate limit credential endpoints per ip and endpoint"
```

---

### Task 15: Invitaciones — `hasPassword` y `accept-with-password`

**Files:**
- Create: `src/Modules/Identity/Modules.Identity.Application/PasswordCredentials.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/InvitationAcceptability.cs`
- Modify: `src/Api/InvitationEndpoints.cs`, `src/Modules/Identity/Modules.Identity.Infrastructure/IdentityInfrastructureExtensions.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/InvitationServiceTests.cs`; `tests/Modules/Identity/Modules.Identity.IntegrationTests/InvitationPasswordApiTests.cs`, `PasswordAuthApiHarness.cs` (helpers de abajo)

**Interfaces:**
- Produces (test):
  - En `InvitationServiceTests`: `static InvitationDto Dto(MembershipViewState state)` — un `InvitationDto` válido con ese estado y el resto fijo.
  - En `PasswordAuthApiHarness`: `sealed record InvitationPayload(Guid TenantId, string TenantName, string Email, string Status, bool HasPassword)`; `Task<InvitationPayload> GetInvitationAsync(HttpClient client, string token)` — `GET /api/v1/invitations/{token}`, afirma 200; `Task<HttpResponseMessage> AcceptWithPasswordAsync(HttpClient client, string token, string password)` — `POST /api/v1/invitations/{token}/accept-with-password` vía `PostJsonAsync`.
  - Arrange común de `InvitationPasswordApiTests`: fábrica y `cs` como en `PasswordAuthApiTests`; `owner = RegisterAndVerifyAsync(ownerClient, …)`; `invited = InviteAsync(ownerClient, cs, owner.TenantId, NewEmail())`; `token = invited.Token`, `tenantId = owner.TenantId`, `invitedUserId = invited.UserId`; `anon` y `other` = `CreateClient(factory)` sin sesión.
- Produces:
  ```csharp
  // Identity.Application
  public sealed class PasswordCredentialsService(IUserRepository users, IPasswordCredentialRepository credentials, IPasswordHasher hasher,
      PasswordCredentialVerifier verifier, IIdentityUnitOfWork unitOfWork, IValidator<NewPasswordInput> validator, IClock clock) : IPasswordCredentials;
  public interface IPasswordCredentials
  {
      Task<bool> HasPasswordAsync(Guid userId, CancellationToken cancellationToken);
      // Sin credencial: valida (NewPasswordValidator, errors.Password) y la crea con hasher.Hash.
      // Con credencial: PasswordCredentialVerifier.Verify (Task 12; el re-hash vive ahí y no se repite acá);
      //   false ⇒ RequestUnauthorizedException("identity.credentials.invalid"); nunca Replace fuera del re-hash.
      // En los dos: MarkEmailVerified, Activate, guarda. Devuelve el usuario.
      Task<AuthenticatedUser> AcceptInvitationAsync(Guid userId, string password, CancellationToken cancellationToken);
  }
  // Tenancy.Application — mismos códigos que Membership.Accept (D3)
  public static class InvitationAcceptability
  {
      public static void EnsurePending(InvitationDto invitation); // Expired ⇒ tenancy.membership.invitation_expired; no Pending ⇒ tenancy.membership.not_invited
  }
  // Api
  public sealed record AcceptInvitationWithPasswordRequest(string Password);
  public sealed record InvitationResponse(Guid TenantId, string TenantName, string Email, string Status, bool HasPassword);
  ```
- Orden del endpoint (`POST /api/v1/invitations/{token}/accept-with-password`, `.AllowAnonymous().RequireRateLimiting(RateLimiterPolicies.Credentials)`): `FindByTokenAsync` (404) → `EnsurePending` (422) → `AcceptInvitationAsync` (401/422) → `IInvitationService.AcceptAsync(token, userId, traceId)` → `SessionEstablisher.EstablishAsync` (acepta las demás `Invited`).

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// InvitationServiceTests.cs
[Theory]
[InlineData(MembershipViewState.Expired, "tenancy.membership.invitation_expired")]
[InlineData(MembershipViewState.Active, "tenancy.membership.not_invited")]
[InlineData(MembershipViewState.Removed, "tenancy.membership.not_invited")]
public void EnsurePendingRejectsAnythingButPending(MembershipViewState state, string code)
    => Assert.Equal(code, Assert.Throws<TenantDomainException>(() => InvitationAcceptability.EnsurePending(Dto(state))).Code);
```

```csharp
// InvitationPasswordApiTests.cs (usa PasswordAuthApiFactory)
[Fact] public async Task AnInvitedUserWithoutPasswordAcceptsByCreatingOne()
{
    Assert.False((await GetInvitationAsync(anon, token)).HasPassword);
    var response = await AcceptWithPasswordAsync(anon, token, "a long enough password");
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("Active", await MembershipStateAsync(cs, tenantId, invitedUserId));
    Assert.NotNull(await EmailVerifiedAtAsync(cs, invitedUserId));
    Assert.Contains((await anon.GetFromJsonAsync<SessionPayload>("/api/v1/auth/me", ct))!.ActiveTenants, t => t.TenantId == tenantId);
    Assert.True((await GetInvitationAsync(other, token)).HasPassword);
}

[Fact] public async Task AnInvitedUserWithAPasswordAcceptsOnlyWithTheRightOne()
{
    // sembrar credencial antes de aceptar: ForgotAsync(invitado) → LatestOutboxTokenAsync(PasswordResetRequested) → ResetAsync(rightPassword)
    var hashBefore = await CredentialHashAsync(cs, invitedUserId);
    var wrong = await AcceptWithPasswordAsync(anon, token, "the wrong long password");
    Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    Assert.Equal("identity.credentials.invalid", (await ProblemAsync(wrong)).Code);
    Assert.Equal(hashBefore, await CredentialHashAsync(cs, invitedUserId));
    Assert.Equal(HttpStatusCode.OK, (await AcceptWithPasswordAsync(anon, token, rightPassword)).StatusCode);
}

[Fact] public async Task AnExpiredInvitationIs422AndAnUnknownTokenIs404()
{
    // UPDATE tenancy.memberships SET expires_at = now() - interval '1 minute' WHERE id = @id (invited.MembershipId)
    var expired = await AcceptWithPasswordAsync(anon, token, "a long enough password");
    var unknown = await AcceptWithPasswordAsync(anon, "no-such-token", "a long enough password");
    Assert.Equal(HttpStatusCode.UnprocessableEntity, expired.StatusCode);
    Assert.Equal("tenancy.membership.invitation_expired", (await ProblemAsync(expired)).Code);
    Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    Assert.Equal("tenancy.invitation.not_found", (await ProblemAsync(unknown)).Code);
}

[Fact] public async Task AShortPasswordOnAcceptFailsValidation()
{
    var response = await AcceptWithPasswordAsync(anon, token, "short");
    Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    var problem = await ProblemAsync(response);
    Assert.Equal("validation.failed", problem.Code);
    Assert.True(problem.Errors!.ContainsKey("Password"));
    Assert.Equal("Invited", await MembershipStateAsync(cs, tenantId, invitedUserId));
    Assert.Equal(0, await CountRowsAsync(cs, "identity.password_credentials", invitedUserId));
}
```

- [ ] **Step 2: Correr y ver FAIL**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~InvitationServiceTests"
dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests --filter "FullyQualifiedName~InvitationPasswordApiTests"
```
Esperado: no compila (`InvitationAcceptability`, `HasPassword`).

- [ ] **Step 3: Implementar.**
- [ ] **Step 4: Correr y ver PASS** (mismos comandos, más `--filter "FullyQualifiedName~InvitationApiTests"` en `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests` por el campo nuevo del `GET`).
- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Identity/Modules.Identity.Application/PasswordCredentials.cs src/Modules/Identity/Modules.Identity.Infrastructure/IdentityInfrastructureExtensions.cs src/Modules/Tenancy/Modules.Tenancy.Application/InvitationAcceptability.cs src/Api/InvitationEndpoints.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/InvitationServiceTests.cs tests/Modules/Identity/Modules.Identity.IntegrationTests/InvitationPasswordApiTests.cs tests/Modules/Identity/Modules.Identity.IntegrationTests/PasswordAuthApiHarness.cs
git commit -m "feat(auth): accept an invitation by creating or entering a password"
```

---

### Task 16: Mail de invitación v4

**Files:**
- Modify: `src/Modules/Notifications/Modules.Notifications.Application/InvitationEmailTemplate.cs` (`TemplateRef = "identity.invitation.v4"`; texto: «Abre este enlace para aceptar la invitación. Puedes entrar con tu cuenta de Google o crear una contraseña.»; el link no cambia)
- Test: `tests/Modules/Notifications/Modules.Notifications.UnitTests/NotificationTests.cs` (`InvitationTemplateRefIsVersionThree` → `InvitationTemplateRefIsVersionFour`), `InvitationEmailTemplateTests.cs`

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
[Fact] public void InvitationTemplateRefIsVersionFour()
    => Assert.Equal("identity.invitation.v4", InvitationEmailTemplate.TemplateRef);

// Texto exacto: no hay librería de snapshots en el repositorio; el cuerpo completo es la instantánea.
[Fact] public void TheInvitationTextBodyOffersGoogleOrAPassword()
{
    var message = InvitationEmailTemplate.Render("a@x.com", "https://q/invitations/t", "Acme");
    Assert.Equal(
        "Hola,\n\nHas sido invitado a Acme.\n" +
        "Abre este enlace para aceptar la invitación. Puedes entrar con tu cuenta de Google o crear una contraseña.\n\n" +
        "https://q/invitations/t\n\n" +
        "Si no esperabas esta invitación, puedes ignorar este mensaje.\n",
        message.TextBody);
    Assert.Contains("crear una contraseña", message.HtmlBody);
}
```

- [ ] **Step 2: Correr y ver FAIL**

```powershell
dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --filter "FullyQualifiedName~NotificationTests|FullyQualifiedName~InvitationEmailTemplateTests"
```
Esperado: `Expected: "identity.invitation.v4" Actual: "identity.invitation.v3"`.

- [ ] **Step 3: Implementar** (actualizar también el `<summary>` con la razón de la v4).
- [ ] **Step 4: Correr y ver PASS** (mismo comando, más `dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --filter "FullyQualifiedName~InvitationNotificationTests|FullyQualifiedName~DeliveryWorkersCharacterizationTests"`).
- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Notifications/Modules.Notifications.Application/InvitationEmailTemplate.cs tests/Modules/Notifications/Modules.Notifications.UnitTests/NotificationTests.cs tests/Modules/Notifications/Modules.Notifications.UnitTests/InvitationEmailTemplateTests.cs
git commit -m "feat(notifications): invitation email offers google or a password"
```

---

### Task 17: Guarda final — sin secretos en auditoría ni logs

**Files:**
- Test: `tests/Modules/Identity/Modules.Identity.IntegrationTests/PasswordAuthApiTests.cs`, `PasswordAuthApiHarness.cs` (helpers de abajo)

**Interfaces:**
- Produces (test), en `PasswordAuthApiHarness`:
  - `Task<string> AllAuditTextAsync(string connectionString)` — `SELECT coalesce(string_agg(row_to_json(e)::text, ''), '') FROM audit.entries e`.
  - `Task<string> AllNotificationTextAsync(string connectionString)` — lo mismo sobre `notifications.notifications`.

Prueba de cierre del tercer anti-objetivo. Pasa si las tareas anteriores respetaron el contrato; si da RED, el defecto está en la tarea que escribió esa fila y se arregla ahí.

- [ ] **Step 1: Escribir la prueba**

```csharp
[Fact] public async Task NoAuditEntryOrNotificationCarriesAPasswordOrAToken()
{
    // flujo completo con una fábrica con RecordingEmailChannel (para que los workers escriban notifications):
    //   RegisterAsync → verifyToken = LatestOutboxTokenAsync(EmailVerificationRequested) → VerifyAsync → LoginAsync(oldPassword)
    //   → ForgotAsync → resetToken = LatestOutboxTokenAsync(PasswordResetRequested) → ResetAsync(newPassword) → LoginAsync(newPassword)
    //   y WaitUntilAsync hasta que channel.Sent tenga los dos mails.
    var audit = await AllAuditTextAsync(cs);
    var notifications = await AllNotificationTextAsync(cs);
    foreach (var secret in new[] { oldPassword, newPassword, verifyToken, resetToken })
    {
        Assert.DoesNotContain(secret, audit);
        Assert.DoesNotContain(secret, notifications);
    }
    Assert.DoesNotContain(EmailTokenCodec.Hash(verifyToken), audit);
}
```

- [ ] **Step 2: Correr**

```powershell
dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests --filter "FullyQualifiedName~PasswordAuthApiTests.NoAuditEntryOrNotificationCarriesAPasswordOrAToken"
```
- [ ] **Step 3: Suite completa de los módulos tocados** (tarda; Docker arriba):

```powershell
dotnet test tests/Modules/Identity/Modules.Identity.UnitTests
dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests
dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests
dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests
dotnet test tests/ArchitectureTests/ArchitectureTests
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests
```
- [ ] **Step 4: Commit**

```powershell
git add tests/Modules/Identity/Modules.Identity.IntegrationTests/PasswordAuthApiTests.cs tests/Modules/Identity/Modules.Identity.IntegrationTests/PasswordAuthApiHarness.cs
git commit -m "test(auth): guard audit and notifications against leaked secrets"
```

---

## Cierre

- Ledger: actualizar `qep-backend/sdd/02-plan/plan-maestro.md` con estado, comandos y resultados reales, commits y los cuatro riesgos residuales de arriba.
- Pendientes del spec que siguen abiertos: bloqueo por cuenta, limpieza de registros sin verificar y sus slugs, cambio de contraseña con sesión, tokens crudos en el payload del outbox, y corregir el `CLAUDE.md` raíz que menciona `Notifications:LoginUrl`.
- Despliegue: backend antes que frontend; definir `NOTIFICATIONS_APP_BASE_URL` en el pipeline (`k8s/prod-configMap.yaml`). Las cuatro migraciones de Identity y la de Notifications corren solas al arrancar.

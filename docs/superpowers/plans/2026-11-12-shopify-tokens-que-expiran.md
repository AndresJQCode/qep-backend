# Shopify: tokens offline que expiran — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan. Las reglas de «Presupuesto de pruebas y ejecución de planes» del `CLAUDE.md` del workspace mandan sobre el skill: un implementador por **bloque**, no por tarea. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que toda conexión de Shopify nueva o reenlazada guarde un token offline que expira (`expiring=1`) con su `refresh_token`, que un worker lo refresque antes de vencer sin que nadie intervenga, y que un refresco imposible deje la conexión en `NeedsAttention` con `token_expired`.

**Architecture:** El canje pide `expiring=1` y rechaza la respuesta sin `refresh_token`/`expires_in`. `shopify_installs` guarda el refresh token cifrado y los dos vencimientos; `LinkShopifyStore` los pasa a la conexión (secreto `refreshToken`, campos `tokenExpiresAt` y `refreshTokenExpiresAt`) y, si el token de la instalación está por vencer, lo refresca antes de registrar webhooks. Un `ShopifyTokenRefreshWorker` en Integrations, con el molde de `InstagramTokenRefreshWorker`, refresca cada 5 minutos lo que vence en 20 o menos, bajo un `pg_try_advisory_xact_lock` por conexión; el reenlace toma el mismo candado (variante que espera). Del lado de Shopify, el directorio entrega una conexión operativa **sin token** cuando el token venció, y sólo la conciliación lo salta.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, xUnit v3, Testcontainers.PostgreSql, `WebApplicationFactory<Program>`.

**Spec:** [`docs/superpowers/specs/2026-11-12-shopify-tokens-que-expiran-design.md`](../specs/2026-11-12-shopify-tokens-que-expiran-design.md)

---

## Antes de empezar

- Worktree: `C:/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend-worktrees/shopify-tokens-que-expiran`, rama `feature/shopify-tokens-que-expiran`. Rutas absolutas siempre: el cwd del shell vuelve al checkout principal. Antes de **cada** commit, `git -C <worktree> branch --show-current` tiene que decir esa rama.
- Comandos en PowerShell, uno por línea (`A; if ($?) { B }`). Si `Api.exe` está corriendo, el build falla por archivo bloqueado: avisa y espera, no lo detengas.
- RED de compilación: la evidencia es la salida de `dotnet build <proyecto de pruebas>`, sin contenedores. Integración sólo en GREEN, con `$env:Seed__Enabled = "false"` en la sesión, filtrada por nombre (`--filter "FullyQualifiedName~<Prueba>"`) y `-- xUnit.MaxParallelThreads=4`. La clase completa, una vez al final del bloque. Pruebas en primer plano. Antes de una corrida larga: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay otra, espera.
- Atajos usados abajo: `$W` = la ruta del worktree; `$IU` = `$W/tests/Modules/Integrations/Modules.Integrations.UnitTests`; `$II` = `$W/tests/Modules/Integrations/Modules.Integrations.IntegrationTests`; `$SU`/`$SI` = los de `Modules/Shopify`; `$BU` = `$W/tests/Bootstrapper/Bootstrapper.UnitTests`.

## Global Constraints

- Canje: `expiring=1` en el cuerpo de `POST https://{shop}/admin/oauth/access_token`. Respuesta sin `refresh_token`, sin `expires_in > 0` o sin `refresh_token_expires_in > 0` = `ShopifyGatewayFailure.Invalid` (la instalación falla con `integrations.shopify.code_exchange_failed`).
- Refresco: mismo endpoint, `application/x-www-form-urlencoded`, `client_id`, `client_secret`, `grant_type=refresh_token`, `refresh_token`.
- Constantes: el worker corre cada **5 minutos** (`ShopifyTokenRefresh.Interval`) y refresca con **20 minutos** o menos de vida (`ShopifyTokenRefresh.Margin`).
- Claves nuevas: secreto `refreshToken`; campos `tokenExpiresAt` y `refreshTokenExpiresAt`, ISO 8601 UTC (`"O"`), como `InstagramFieldKeys.TokenExpiresAt`.
- AAD: `connectionId + fieldKey` en la conexión; `installId + fieldKey` en `shopify_installs` (`accessToken` y `refreshToken`).
- No se refresca una conexión `NeedsAttention` con `token_expired` o `app_uninstalled`. Sí `Active`, `Paused` y `NeedsAttention` con cualquier otro código.
- El refresco no cambia el estado: `Update(..., verified: false)`.
- 401 o cualquier 4xx salvo 429 → `NeedsAttention` con `token_expired`. 429, 5xx, timeout, red o respuesta ilegible → sin cambio, siguiente vuelta.
- Ningún access token, refresh token ni cuerpo de `/admin/oauth/access_token` en logs, auditoría, `ToString` ni mensajes de excepción.
- El refresco no se audita (Instagram tampoco: `InstagramTokenRefreshWorker.cs:192-202`).
- `Modules.Shopify.*` no referencia `Modules.Integrations.Application` (`ShopifyLayerTests`); `Modules.Integrations.Application` no referencia EF Core ni Npgsql.
- Commits: conventional commits en inglés, **sin `Co-Authored-By` ni atribución de IA**. `docs/estado.md` no se toca en esta rama.

## Decisiones del plan (§10 del spec y lo que el código obligó)

| # | Decisión | Por qué |
| --- | --- | --- |
| D1 | Candado: `pg_advisory_xact_lock` / `pg_try_advisory_xact_lock` de dos claves, `(ShopifyTokenLock.Namespace = 20261112, hashtext(id::text))`, sobre el id de la conexión o de la instalación. El worker usa la variante try; el enlace, la que espera. | Mismo patrón que `AutomationsUnitOfWork.cs:25-38`; el espacio de dos claves no choca con los de una. El worker salta y vuelve en 5 min; la persona que enlaza espera a lo sumo el timeout del gateway (10 s) en vez de recibir un error. |
| D2 | `IIntegrationsUnitOfWork` gana `BeginTransactionAsync`, `LockShopifyTokenAsync` y `TryLockShopifyTokenAsync`; `SaveChangesAsync` confirma la transacción abierta, si hay. | El enlace es Application y no ve EF. Hoy nadie abre transacciones en Integrations, así que confirmar al guardar no cambia ningún camino existente. |
| D3 | El enlace abre la transacción **sólo en el reenlace** (ruta del mismo tenant), y el candado se toma antes de cargar la conexión. Una conexión nueva no abre transacción. | Dentro de una transacción explícita, la violación de `IX_connection_routes_provider_external` la aborta y la consulta del ganador en `IntegrationsUnitOfWork.cs:41-44` fallaría con 25P02. Cargar después del candado evita un 412 si el worker guardó en el medio. |
| D4 | Columnas de `shopify_installs`: `refresh_token_protected bytea NULL`, `access_token_expires_at timestamptz NULL`, `refresh_token_expires_at timestamptz NULL`. Un solo `key_id` para los dos secretos; el dominio exige que coincidan. Migración `AddShopifyInstallRefreshToken`. | Los dos se sellan siempre juntos; dos `key_id` permitirían una fila a medio re-cifrar. |
| D5 | `ConnectionSecretRekeyWorker`: las **conexiones** ya quedan cubiertas (recorre `connection.Secrets` sin mirar la clave, `ConnectionSecretRekeyWorker.cs:103-109`). Las **instalaciones** no: sólo re-cifra `accessToken` (`:192-195`). Cambia para re-cifrar los dos. | Sin el cambio, rotar la llave deja el refresh token de la instalación cifrado con una llave que luego se retira. |
| D6 | `ShopifyTokenGrant` lleva `ExpiresIn`/`RefreshTokenExpiresIn` (`TimeSpan`); las fechas las calcula quien guarda, con su `IClock`. | El gateway es singleton y no tiene reloj (`ShopifyAdminGateway.cs:17-20`, registro en `IntegrationsInfrastructureExtensions.cs:126`); `IClock` es scoped. |
| D7 | Contrato del puerto: `ShopifyConnectionRef` no cambia de forma. Con el token vencido sale `IsOperational = true` y `AccessToken = null`. `ListOperationalAsync` excluye esas conexiones. | La ingesta (`ShopifyOrderIngestion.cs:118`), el reintento (`ShopifyOrders.cs:276`) y el redact (`ShopifyWebhookDeliveryProcessor.cs:92`) sólo miran `IsOperational` y siguen igual; los únicos que leen el token son `ReconcileStore` y `ShopifyGraphQlClient`. |
| D8 | `ReconcileStore.RunAsync` con conexión operativa sin token lanza `ServiceUnavailableException(shopify.store.sync_unavailable)` sin llamar a Shopify ni reportar. | Código existente; «Sincronizar ahora» responde 503 «reintenta» y el cron lo registra como corrida fallida, que es lo que §6.4 pide. |
| D9 | Nuevo `IConnectionHealthReporter.ReportTokenExpiredAsync`: pasa a `NeedsAttention`/`token_expired` desde `Active`, `Paused` o `NeedsAttention` con otro código; audita; publica `ConnectionEvents.NeedsAttention` sólo si estaba `Active`. | El spec manda reportar por `IConnectionHealthReporter`, pero `ReportCredentialsRejectedAsync` ignora todo lo que no es `Active` (`IConnectionHealthReporter.cs:39-42`): una pausada con el refresh token muerto quedaría reintentando cada 5 min para siempre. Publicar sólo desde `Active` repite la regla de `LinkShopifyStore.cs:189-193`. |
| D10 | El enlace rechaza con `integrations.shopify.install_expired` una instalación sin refresh token (canjeada antes del despliegue) y una cuyo refresco responde 4xx. Si el refresco no se puede hacer (429, 5xx, red), responde 503 `integrations.connection.provider_unreachable`. | Códigos existentes. `install_expired` ya significa «vuelve a instalar desde tu tienda»; no se crea una conexión muerta (§6.1). |
| D11 | El probador de Shopify, con `tokenExpiresAt` vencido, responde `Unreachable("token_refresh_pending")` sin llamar a Shopify. Recibe `TimeProvider` (singleton). | Anti-objetivo 2: «Probar» con el worker atrasado daría 401 → `credentials_rejected`. |
| D12 | La red de §6.4 (releer ante 401) vive sólo en `ReconcileStore`. Al releer: token distinto → reintenta una vez; token `null` (venció) → `sync_unavailable` sin reportar; igual → reporta como hoy. | Es el único consumidor que llama a la Admin API en segundo plano; el probador ya recibe el token recién leído. |
| D13 | Respuesta del refresco sin `scope`: `Scopes = ""`. Sin `refresh_token`/`expires_in`: `Invalid`, que el worker trata como transitorio. | §5: la forma no está publicada. Un `Invalid` no prueba que el refresh token murió; marcarlo `token_expired` cortaría una tienda sana. |
| D14 | `ShopifyTokenRefresh.IsDue` trata un vencimiento ausente o ilegible como «hay que refrescar»; `IsExpired`, como «no venció». | Igual que `InstagramTokenRefreshWorker.IsDue`. El directorio no corta el token de una conexión anterior a este cambio; el worker la marca en su primera vuelta (§6.3). |
| D15 | Instagram y Shopify no comparten base. | §10 lo permite; los criterios de elegibilidad, el candado y el estado son distintos. |

## Review Focus

1. **Pausada con el refresh token muerto.** Un 401 sobre una conexión `Paused` no se pierde: queda `NeedsAttention`/`token_expired` y la siguiente vuelta no llama a Shopify. Tarea 5 (`ReportTokenExpiredMovesPausedAndOtherAttentionToTokenExpired`) y Tarea 6 (`APausedConnectionRejectedIsMarkedAndNotRetried`).
2. **Refrescar no saca de `NeedsAttention`.** Una conexión con `webhooks_not_registered` se refresca y sigue `NeedsAttention` con el mismo código. Tarea 6 (`AWebhooksNotRegisteredConnectionIsRefreshedAndKeepsItsState`).
3. **La carrera de enlace sigue traduciéndose.** Una conexión nueva no abre transacción; `LinkRaceOnTheSameShopIsTranslatedByWhoWon` sigue verde y `LinkNewConnectionOpensNoTransaction` lo amarra. Tarea 4.
4. **«Probar» con el worker atrasado.** Token vencido en `tokenExpiresAt` → `provider_unreachable`, nunca `credentials_rejected`. Tarea 7 (`AnExpiredTokenIsUnreachableWithoutCallingShopify`).
5. **Rotar la llave con una instalación en vuelo.** El refresh token de la instalación se re-cifra con el access token y sigue descifrando. Tarea 3 (`RekeyIncludesShopifyInstalls`, ampliada).
6. **401 a mitad de conciliación con el token recién vencido** no reporta `credentials_rejected`. Tarea 9 (`Reconcile401WhileTheTokenExpiredSkipsWithoutReporting`).

---

## Bloque A — canje, instalación y enlace (Tareas 1–4, un implementador)

Toca `Modules.Integrations.Domain`, el gateway, `CompleteShopifyInstall`, `LinkShopifyStore`, la unidad de trabajo y la migración. Al final del bloque: `ShopifyInstallApiTests`, `ShopifyLinkApiTests` y `ConnectionSecretRekeyWorkerTests` completas.

### Tarea 1: claves, constantes y columnas de la instalación

**Files:**
- Modify: `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationProviders.cs:46-54` (claves) y `:213-246` (definiciones de `Shopify`)
- Create: `src/Modules/Integrations/Modules.Integrations.Domain/ShopifyTokenRefresh.cs`
- Modify: `src/Modules/Integrations/Modules.Integrations.Domain/ShopifyInstall.cs:60-185`
- Modify: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/IntegrationsDbContext.cs:152-155`
- Create: migración `AddShopifyInstallRefreshToken` en `src/Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/Migrations/`
- Test: `$IU/IntegrationCatalogTests.cs:77-98`, `$IU/ShopifyInstallTests.cs`, nuevo `$IU/ShopifyTokenRefreshTests.cs`

**Interfaces — Produces:**
```csharp
// ShopifyFieldKeys
public const string RefreshToken = "refreshToken";
public const string TokenExpiresAt = "tokenExpiresAt";
public const string RefreshTokenExpiresAt = "refreshTokenExpiresAt";

public static class ShopifyTokenRefresh
{
    public static readonly TimeSpan Interval;   // 5 min
    public static readonly TimeSpan Margin;     // 20 min
    public static string Format(DateTimeOffset at);                               // UTC, "O", InvariantCulture
    public static bool IsDue(string? tokenExpiresAt, DateTimeOffset now);         // ausente/ilegible → true; expiresAt - now <= Margin
    public static bool IsExpired(string? tokenExpiresAt, DateTimeOffset now);     // ausente/ilegible → false; now >= expiresAt
    public static bool IsUnrecoverable(ConnectionStatus status, string? lastFailureCode); // NeedsAttention && token_expired|app_uninstalled
}

// ShopifyInstall
public byte[]? ProtectedRefreshToken { get; }
public DateTimeOffset? AccessTokenExpiresAt { get; }
public DateTimeOffset? RefreshTokenExpiresAt { get; }
public bool NeedsTokenRefresh(DateTimeOffset now);   // AccessTokenExpiresAt is {} at && at - now <= ShopifyTokenRefresh.Margin
public void MarkExchanged(string shopId, string shopName, string scopes, ProtectedSecret token, ProtectedSecret refreshToken,
    DateTimeOffset accessTokenExpiresAt, DateTimeOffset refreshTokenExpiresAt, DateTimeOffset now);
public void ApplyRefreshedToken(ProtectedSecret token, ProtectedSecret refreshToken,
    DateTimeOffset accessTokenExpiresAt, DateTimeOffset refreshTokenExpiresAt);  // exige IsExchanged; no toca ExpiresAt
public void Reprotect(ProtectedSecret token, ProtectedSecret? refreshToken);
```
Las tres definiciones nuevas de `Shopify`: `tokenExpiresAt` y `refreshTokenExpiresAt` como `FieldKind.Text`, `maxLength: 40`, `isInternal: true`; `refreshToken` como `FieldKind.Secret`, `maxLength: 2048`, `isInternal: true`. En `MarkExchanged`, `ApplyRefreshedToken` y `Reprotect`, `token.KeyId != refreshToken.KeyId` lanza `ArgumentException`.

- [ ] **RED.** En `ShopifyIsInTheCatalogWithItsFieldsAndOnboarding`, la lista esperada pasa a `[ShopDomain, ShopName, ShopId, Scopes, WebhookSubscriptionIds, TokenExpiresAt, RefreshTokenExpiresAt, AccessToken, RefreshToken]` (orden de declaración). En `ShopifyInstallTests`:
  ```csharp
  [Fact] public void MarkExchangedStoresBothTokensAndTheirExpiries()
  // MarkExchanged(..., Token("k1"), Token("k1"), now + 1h, now + 90d, now)
  // Assert: ProtectedRefreshToken no nulo; AccessTokenExpiresAt == now + 1h; RefreshTokenExpiresAt == now + 90d; ExpiresAt == now + 7d
  [Fact] public void MarkExchangedWithTwoKeysThrows()          // Token("k1"), Token("k2") → ArgumentException
  [Fact] public void ApplyRefreshedTokenKeepsTheInstallExpiry() // ExpiresAt igual; AccessTokenExpiresAt movido
  [Fact] public void NeedsTokenRefreshOnlyWithin20Minutes()     // now+21m → false; now+20m → true; null → false
  ```
  En `ShopifyTokenRefreshTests`: `IsDue` con `null`, `"x"`, `now+20m`, `now+21m` → `true,true,true,false`; `IsExpired` con `null`, `"x"`, `now`, `now+1s` → `false,false,true,false`; `IsUnrecoverable` → `true` sólo para `(NeedsAttention, "token_expired")` y `(NeedsAttention, "app_uninstalled")`, `false` para `(Paused, "token_expired")` y `(NeedsAttention, "webhooks_not_registered")`.
- [ ] Corre `dotnet build $IU` → falla con `CS0117 'ShopifyFieldKeys' does not contain a definition for 'TokenExpiresAt'` y `CS1501` sobre `MarkExchanged`.
- [ ] **GREEN.** Implementa las firmas. Ajusta los llamadores existentes de `MarkExchanged` y `Reprotect` sólo para que compilen (el canje y el re-cifrado reales van en las Tareas 2 y 3). Mapea las columnas de D4 en `IntegrationsDbContext` y genera la migración:
  ```powershell
  dotnet ef migrations add AddShopifyInstallRefreshToken --project src/Modules/Integrations/Modules.Integrations.Infrastructure --context IntegrationsDbContext -o Persistence/Migrations
  ```
  (desde `$W`). La migración sólo agrega tres columnas nulas: revísala antes del commit.
- [ ] Corre `dotnet test $IU --filter "FullyQualifiedName~ShopifyInstallTests|FullyQualifiedName~ShopifyTokenRefreshTests|FullyQualifiedName~IntegrationCatalogTests"` → verde.
- [ ] Commit: `feat(integrations): add shopify refresh token fields and install columns`

### Tarea 2: canje con `expiring=1` y refresco en el gateway

**Files:**
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/Shopify/IShopifyAdminGateway.cs:285-306`
- Modify: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Shopify/ShopifyAdminGateway.cs:56-82`
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/Shopify/CompleteShopifyInstall.cs:329-338`
- Modify: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/ShopifyInstallRepository.cs:78-97` (`TryCompleteExchangeAsync` escribe también las tres columnas nuevas)
- Test: `$IU/ShopifyAdminGatewayTests.cs`, `$IU/CompleteShopifyInstallHandlerTests.cs` (y su `FakeShopifyAdminGateway`, `:373-420`), `$II/FakeShopifyHandler.cs:63-76`, `$II/ShopifyInstallApiTests.cs`

**Interfaces — Produces:**
```csharp
public sealed record ShopifyTokenGrant(string AccessToken, string Scopes, string RefreshToken, TimeSpan ExpiresIn, TimeSpan RefreshTokenExpiresIn)
{
    public override string ToString() => $"ShopifyTokenGrant {{ Scopes = {Scopes}, ExpiresIn = {ExpiresIn} }}";
}
// IShopifyAdminGateway
Task<ShopifyTokenGrant> RefreshTokenAsync(string shop, string refreshToken, CancellationToken cancellationToken);
```
`RefreshTokenAsync` usa la operación `"oauth-refresh"` en los logs, `FormUrlEncodedContent` y la misma clasificación de `SendAsync` (429/5xx → `Unreachable`; otro no-2xx → `Rejected`). `FakeShopifyAdminGateway` gana `RefreshTokenAsync` (anota `"refresh"` en `Calls`, falla con `FailAt == "refresh"`, devuelve `RefreshedGrant`) y su canje devuelve `new ShopifyTokenGrant("shpat_access-token-SENTINEL-3c4d", GrantedScopes, "shprt_refresh-token-SENTINEL-3c4d", TimeSpan.FromHours(1), TimeSpan.FromDays(90))`. `FakeShopifyHandler.ScriptHappyInstall` agrega `"expires_in":3600,"refresh_token":"{refreshToken}","refresh_token_expires_in":7776000` con un parámetro `string refreshToken = "shprt_install-refresh-SENTINEL"`, y `RespondWhen` gana `TimeSpan delay = default`.

- [ ] **RED.** En `ShopifyAdminGatewayTests`:
  ```csharp
  [Fact] public async Task ExchangeCodeAsksForAnExpiringToken()
  // cuerpo enviado contiene "\"expiring\":\"1\""; grant.RefreshToken == "shprt_SENTINEL-gw"; grant.ExpiresIn == 1h; RefreshTokenExpiresIn == 90d
  [Theory] // sin refresh_token, sin expires_in, expires_in = 0
  public async Task ExchangeWithoutAnExpiringTokenIsInvalid(string body)  // Kind == Invalid
  [Fact] public async Task RefreshPostsAFormWithTheRefreshToken()
  // Content-Type "application/x-www-form-urlencoded"; cuerpo contiene "grant_type=refresh_token" y "refresh_token=shprt_SENTINEL-gw"; sin X-Shopify-Access-Token
  [Theory, InlineData(401), InlineData(400)] public async Task RefreshRejectedIsRejected(int status)
  [Theory, InlineData(429), InlineData(503)] public async Task RefreshThrottledOrDownIsUnreachable(int status)
  [Fact] public async Task NoTokenOrBodyReachesTheLogs()
  // tras un canje, un refresco 200 y un refresco 401 con cuerpo {"error":"invalid_request","echo":"shprt_SENTINEL-gw"}:
  // _logs no contiene Token, "shprt_SENTINEL-gw", ClientSecret, Code ni "invalid_request"
  ```
  En `CompleteShopifyInstallHandlerTests`: `ExchangeStoresTheRefreshTokenAndBothExpiries` (con `FakeClock` en `Now`: `install.AccessTokenExpiresAt == Now + 1h`, `RefreshTokenExpiresAt == Now + 90d`, el refresh token descifra con AAD `installId + "refreshToken"`), y amplía la aserción de `:278` para que el `ToString` del grant tampoco contenga el refresh token.
- [ ] Corre `dotnet build $IU` → `CS1729 'ShopifyTokenGrant' does not contain a constructor that takes 5 arguments` / `CS1061 ... 'RefreshTokenAsync'`.
- [ ] **GREEN.** Implementa. `CompleteShopifyInstallHandler` sella `grant.RefreshToken` con `protector.Protect(install.Id, ShopifyFieldKeys.RefreshToken, …)` y llama `MarkExchanged(..., now + grant.ExpiresIn, now + grant.RefreshTokenExpiresIn, now)`.
- [ ] Corre `dotnet test $IU --filter "FullyQualifiedName~ShopifyAdminGatewayTests|FullyQualifiedName~CompleteShopifyInstallHandlerTests"` → verde. Integración: `dotnet test $II --filter "FullyQualifiedName~ShopifyInstallApiTests" -- xUnit.MaxParallelThreads=4` → verde (valida la migración y `TryCompleteExchangeAsync`).
- [ ] Commit: `feat(integrations): request expiring shopify tokens and add the refresh call`

### Tarea 3: transacción, candado y re-cifrado de la instalación

**Files:**
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/IIntegrationsUnitOfWork.cs`
- Modify: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/IntegrationsUnitOfWork.cs:17-22`
- Create: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/ShopifyTokenLock.cs`
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/Shopify/IShopifyInstallRepository.cs` y `…Infrastructure/Persistence/ShopifyInstallRepository.cs`
- Modify: `src/Modules/Integrations/Modules.Integrations.Infrastructure/SecretProtection/ConnectionSecretRekeyWorker.cs:185-202`
- Test: `$IU/IntegrationsTestDoubles.cs:133-150` (`FakeUnitOfWork`), `$IU/StartShopifyInstallHandlerTests.cs:301` (`InMemoryShopifyInstallRepository`), `$II/IntegrationsPersistenceTests.cs`, `$II/ConnectionSecretRekeyWorkerTests.cs:127`

**Interfaces — Produces:**
```csharp
// IIntegrationsUnitOfWork (SaveChangesAsync: confirma la transacción abierta, si hay)
Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken cancellationToken);
Task LockShopifyTokenAsync(Guid ownerId, CancellationToken cancellationToken);        // espera; exige transacción
Task<bool> TryLockShopifyTokenAsync(Guid ownerId, CancellationToken cancellationToken); // no espera; exige transacción

internal static class ShopifyTokenLock { public const int Namespace = 20261112; }

// IShopifyInstallRepository
/// UPDATE … SET access_token_protected, refresh_token_protected, key_id, access_token_expires_at, refresh_token_expires_at
/// WHERE id = @id AND access_token_protected IS NOT NULL. false = la borró el enlace o el barrido.
Task<bool> TryStoreRefreshedTokenAsync(ShopifyInstall install, CancellationToken cancellationToken);
```
`FakeUnitOfWork` gana `Transactions` (int), `Locks` (`List<Guid>`), `TryLocks` (`List<Guid>`) y `LockTaken` (bool, por defecto `true`).

- [ ] **RED.** En `IntegrationsPersistenceTests`:
  ```csharp
  [Fact] public async Task TheShopifyTokenLockExcludesASecondTransaction()
  // scope A: BeginTransactionAsync + TryLockShopifyTokenAsync(id) == true
  // scope B: BeginTransactionAsync + TryLockShopifyTokenAsync(id) == false; con otro id == true
  // tras disponer A: B (nueva transacción) toma id == true
  [Fact] public async Task TheLockWithoutATransactionThrows()          // InvalidOperationException
  [Fact] public async Task StoringARefreshedTokenReplacesThePairOnly()  // ExpiresAt de la instalación intacto
  ```
  En `RekeyIncludesShopifyInstalls`: siembra la instalación con los dos tokens bajo la llave vieja; tras `RunOnceAsync`, `key_id` es la activa y **los dos** descifran (`installId + "accessToken"`, `installId + "refreshToken"`).
- [ ] Corre `dotnet build $II` → `CS1061 'IIntegrationsUnitOfWork' does not contain a definition for 'BeginTransactionAsync'`.
- [ ] **GREEN.** Implementa como `AutomationsUnitOfWork.cs:20-38` (dos claves, `hashtext({ownerId.ToString()})`; sin transacción, `InvalidOperationException`). En el re-cifrado de instalaciones, descifra y vuelve a sellar el refresh token si existe y llama `install.Reprotect(token, refreshToken)`; si alguno no descifra, la fila cuenta como `skipped` y no cambia.
- [ ] Corre `dotnet test $II --filter "FullyQualifiedName~IntegrationsPersistenceTests|FullyQualifiedName~RekeyIncludesShopifyInstalls" -- xUnit.MaxParallelThreads=4` → verde.
- [ ] Commit: `feat(integrations): add the shopify token lock and rekey install refresh tokens`

### Tarea 4: el enlace guarda el par y refresca la instalación vieja

**Files:**
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/Shopify/LinkShopifyStore.cs:87-224`
- Test: `$IU/LinkShopifyStoreHandlerTests.cs`, `$II/ShopifyLinkApiTests.cs`

**Interfaces — Consumes:** `IShopifyAdminGateway.RefreshTokenAsync`, `IIntegrationsUnitOfWork` (Tarea 3), `IShopifyInstallRepository.TryStoreRefreshedTokenAsync`, `ShopifyInstall.NeedsTokenRefresh/ApplyRefreshedToken`.
**Produces:** conexión con secretos `{accessToken, refreshToken}` y campos `tokenExpiresAt = Format(install.AccessTokenExpiresAt)`, `refreshTokenExpiresAt = Format(install.RefreshTokenExpiresAt)`. Método privado:
```csharp
private async Task<(ShopifyInstall Install, string AccessToken, string RefreshToken)> EnsureFreshInstallTokenAsync(
    ShopifyInstall install, string accessToken, string refreshToken, DateTimeOffset now, CancellationToken cancellationToken);
```
Orden nuevo: … → descifrar `accessToken` **y** `refreshToken` (sin `ProtectedRefreshToken`, o sin descifrar → `install_expired`) → `EnsureFreshInstallTokenAsync` → `FindRelinkAsync` → resto igual. `EnsureFreshInstallTokenAsync`, sólo si `install.NeedsTokenRefresh(now)`: transacción propia → `LockShopifyTokenAsync(install.Id)` → relee la instalación (`null` → `install_not_found`); si ya no lo necesita, descifra la releída y la usa → si no, `RefreshTokenAsync` (`Rejected` → `install_expired`; otra falla → `ServiceUnavailableException(IntegrationsErrorCodes.ProviderUnreachable, …)`) → `ApplyRefreshedToken` → `TryStoreRefreshedTokenAsync` (`false` → `install_not_found`) → `SaveChangesAsync` (confirma). `FindRelinkAsync`, con ruta del mismo tenant: `BeginTransactionAsync` + `LockShopifyTokenAsync(route.ConnectionId)` **antes** de `repository.FindAsync`; el handler dispone esa transacción al salir (D3).

- [ ] **RED.** En `LinkShopifyStoreHandlerTests` (el `Bed` siembra instalaciones con los dos tokens, vencimiento `Now + 1h`):
  ```csharp
  [Fact] public async Task LinkStoresTheRefreshTokenAndBothExpiries()
  // Secrets["refreshToken"] descifra al de la instalación; Fields["tokenExpiresAt"] == ShopifyTokenRefresh.Format(Now + 1h)
  [Fact] public async Task LinkAnInstallOlderThanItsTokenRefreshesBeforeTheWebhooks()
  // instalación con AccessTokenExpiresAt = Now + 10m → Gateway.Calls empieza con "refresh"; Gateway.Tokens (webhooks) == token refrescado;
  // UnitOfWork.Locks == [installId]; la conexión guarda el par refrescado
  [Fact] public async Task LinkWhenTheInstallRefreshIsRejectedFailsWithoutAConnection()
  // FailAt = "refresh", Rejected → IntegrationsDomainException "integrations.shopify.install_expired"; Repository vacío; Routes vacío
  [Fact] public async Task LinkWhenTheInstallRefreshIsUnreachableIs503()   // ServiceUnavailableException "integrations.connection.provider_unreachable"
  [Fact] public async Task LinkAnInstallWithoutARefreshTokenIsInstallExpired()
  [Fact] public async Task LinkRelinkLocksTheConnectionBeforeLoadingIt()  // UnitOfWork.Locks == [existingId]; Transactions == 1
  [Fact] public async Task LinkNewConnectionOpensNoTransaction()          // UnitOfWork.Transactions == 0
  ```
  En `ShopifyLinkApiTests`: `LinkAnInstallWhoseTokenIsAboutToExpireRefreshesItFirst` (tras el canje, `UPDATE integrations.shopify_installs SET access_token_expires_at = now() + interval '5 minutes'`; el primer request a `/admin/oauth/access_token` posterior al canje lleva `grant_type=refresh_token` y llega antes que cualquier `webhookSubscriptionCreate`; el secreto `accessToken` de la conexión es el refrescado) y `LinkWhenTheInstallRefreshAnswers401FailsWithoutAConnection` (422 `integrations.shopify.install_expired`, `CountConnectionsAsync == 0`, la instalación sigue existiendo).
- [ ] Compila (los dobles ya existen desde las Tareas 2 y 3), así que el RED es de ejecución: `dotnet test $IU --filter "FullyQualifiedName~LinkShopifyStoreHandlerTests"` → 7 fallas nuevas (sin `refreshToken` en `Secrets`, sin llamada `"refresh"`, `Transactions`/`Locks` en cero).
- [ ] **GREEN.** Implementa.
- [ ] Corre las unitarias del filtro anterior → verde; luego `dotnet test $II --filter "FullyQualifiedName~LinkAnInstallWhoseTokenIsAboutToExpire|FullyQualifiedName~LinkWhenTheInstallRefreshAnswers401|FullyQualifiedName~LinkRaceOnTheSameShopIsTranslatedByWhoWon|FullyQualifiedName~LinkSameTenantTwiceReactivates" -- xUnit.MaxParallelThreads=4` → verde.
- [ ] Commit: `feat(integrations): keep the shopify refresh token on link and refresh stale installs`
- [ ] **Fin del bloque A:** `dotnet test $IU` completo, y `dotnet test $II --filter "FullyQualifiedName~ShopifyInstallApiTests|FullyQualifiedName~ShopifyLinkApiTests|FullyQualifiedName~ConnectionSecretRekeyWorkerTests|FullyQualifiedName~IntegrationsPersistenceTests" -- xUnit.MaxParallelThreads=4`. Revisión del bloque.

---

## Bloque B — el worker, el reporte y el probador (Tareas 5–7, un implementador)

Depende del bloque A. Al final: `ShopifyTokenRefreshWorkerTests`, `ConnectionSecretLeakTests` y `ConsumerPortsTests` completas, y una corrida de `$SI` (el harness de Shopify cambia en la Tarea 6).

### Tarea 5: `ReportTokenExpiredAsync`

**Files:**
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/IConnectionHealthReporter.cs`
- Test: `$IU/ConsumerPortsTests.cs:120-150`; doble `RecordingHealthReporter` en `$BU/ShopifyAdaptersTestDoubles.cs:571`

**Interfaces — Produces:**
```csharp
/// NeedsAttention con token_expired desde Active, Paused o NeedsAttention con otro código; audita (actor Integration, como
/// ReportCredentialsRejectedAsync); publica ConnectionEvents.NeedsAttention sólo si estaba Active. Ya en token_expired o
/// app_uninstalled: no-op.
Task ReportTokenExpiredAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
```

- [ ] **RED.** `ReportTokenExpiredMovesPausedAndOtherAttentionToTokenExpired` (una `Paused` y una `NeedsAttention`/`webhooks_not_registered` quedan `NeedsAttention`/`token_expired`, una entrada de auditoría cada una, **ningún** evento publicado) y `ReportTokenExpiredFromActivePublishesOnce` (un evento; repetir no agrega auditoría ni evento).
- [ ] `dotnet build $IU` → `CS1061 ... 'ReportTokenExpiredAsync'`.
- [ ] **GREEN.** Implementa en `ConnectionHealthReporter`; agrega el método al doble de Bootstrapper (anota en una lista `TokenExpired`).
- [ ] `dotnet test $IU --filter "FullyQualifiedName~ConsumerPortsTests"` → verde; `dotnet build $BU` compila (el doble implementa la interfaz).
- [ ] Commit: `feat(integrations): report expired tokens from any recoverable state`

### Tarea 6: `ShopifyTokenRefreshWorker`

**Files:**
- Create: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Shopify/ShopifyTokenRefreshWorker.cs`
- Modify: `src/Modules/Integrations/Modules.Integrations.Infrastructure/IntegrationsInfrastructureExtensions.cs:129` (`AddHostedService<ShopifyTokenRefreshWorker>()`)
- Modify: `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifyApiHarness.cs:323-345` (`LinkStoreAsync` siembra `refreshToken` y `tokenExpiresAt`/`refreshTokenExpiresAt` a `now + 1 día`, para que el worker del host no marque las tiendas de prueba)
- Create: `$II/ShopifyTokenRefreshWorkerTests.cs`; Modify: `$II/FakeShopifyHandler.cs` (`ScriptRefresh`)

**Interfaces — Produces:**
```csharp
internal sealed record ShopifyTokenRefreshRunResult(int Refreshed, int Rejected, int Deferred, int Skipped);

internal sealed partial class ShopifyTokenRefreshWorker(IServiceScopeFactory scopeFactory, ILogger<ShopifyTokenRefreshWorker> logger)
    : BackgroundService
{
    public Task FirstRunCompletion { get; }
    internal Task<ShopifyTokenRefreshRunResult> RunOnceAsync(CancellationToken cancellationToken);
}

// FakeShopifyHandler: regla con cuerpo "grant_type=refresh_token", registrada antes de ScriptHappyInstall
public void ScriptRefresh(string shop, string accessToken, string refreshToken,
    HttpStatusCode status = HttpStatusCode.OK, TimeSpan delay = default);
```
Algoritmo (lo no obvio):
1. Lista, sin tracking, las conexiones `shopify` que no son `IsUnrecoverable(status, lastFailureCode)`, con `Fields` y el `UpdatedAt` de su secreto `refreshToken` (o `null`). Elegible = sin secreto, o `IsDue(Fields[tokenExpiresAt], now)`.
2. Por conexión, un scope: `BeginTransactionAsync`; `TryLockShopifyTokenAsync(id)` falso → `Skipped`. Relee la conexión con tracking; si no existe, es `IsUnrecoverable`, el `UpdatedAt` del secreto cambió o ya no es `IsDue` → `Skipped` (D1, §6.2 «si el secreto cambió»).
3. Sin secreto `refreshToken` → `ReportTokenExpiredAsync` → `Rejected`. Secreto que no descifra → `Deferred` (`unreadable_secret`), como Instagram.
4. `RefreshTokenAsync(shopDomain, refreshToken)`: `Rejected` → `ReportTokenExpiredAsync` (su guardado confirma la transacción, con el candado tomado) → `Rejected`. `Unreachable`/`Invalid` → `Deferred`.
5. `Update(provider, Name, Fields + {tokenExpiresAt, refreshTokenExpiresAt}, {accessToken, refreshToken}, protector.Protect, now, verified: false)` y `SaveChangesAsync`. Cualquier excepción del guardado (salvo cancelación) → `Deferred` (`save_failed`): Shopify ya rotó, pero el par viejo sigue sirviendo y la próxima vuelta lo usa (§6.2).
6. Un log por conexión, sin token ni cuerpo: `"Shopify connection {ConnectionId}: token refresh {Outcome} ({Reason}); the token expires at {ExpiresAt}"`, con `Outcome` ∈ `ok | rejected | transient` y `ExpiresAt` = el nuevo o `"unchanged"`. Corre al arrancar y cada `ShopifyTokenRefresh.Interval`; nunca tumba el host.

- [ ] **RED.** En `ShopifyTokenRefreshWorkerTests` (cada prueba enlaza con `ShopifyFlow` y mueve `tokenExpiresAt` por SQL con `jsonb_set`, como `InstagramConnectApiTests.cs:288-290`):
  ```csharp
  [Fact] public async Task ATokenIsRefreshedWithin20MinutesAndNotBefore()
  // +21 min → (0,0,0,0) y sin requests; +19 min → (1,0,0,0); el request lleva refresh_token=<el del enlace>;
  // accessToken/refreshToken nuevos descifran; tokenExpiresAt ≈ now + 1h; status y last_verified_at sin cambio
  [Fact] public async Task TwoConcurrentRunsCallShopifyOnce()
  // ScriptRefresh(..., delay: 2 s); Task.WhenAll(RunOnceAsync, RunOnceAsync) → 1 request de refresco; Refreshed + Skipped == (1, 1)
  [Fact] public async Task RelinkingWhileTheWorkerRefreshesKeepsTheLinkTokens()
  // refresco con delay 2 s en curso; reenlace con otra instalación → 201/200; secretos finales = los del reenlace;
  // la vuelta siguiente no llama a Shopify
  [Fact] public async Task A401MarksTokenExpiredAndA503ChangesNothing()
  [Fact] public async Task APausedConnectionRejectedIsMarkedAndNotRetried()        // Review Focus 1
  [Fact] public async Task AWebhooksNotRegisteredConnectionIsRefreshedAndKeepsItsState() // Review Focus 2
  [Fact] public async Task TokenExpiredAndAppUninstalledAreNotRefreshed()
  [Fact] public async Task AConnectionWithoutARefreshTokenIsMarkedTokenExpired()   // borra la fila del secreto refreshToken por SQL
  [Fact] public async Task AFailedSaveAfterTheRefreshRecoversOnTheNextRun()
  // delay 2 s; durante el delay, UPDATE integrations.connections SET version = version + 1 → (0,0,1,0);
  // la vuelta siguiente refresca con el refresh token VIEJO (cuerpo del request) → (1,0,0,0)
  ```
- [ ] `dotnet build $II` → `CS0246 The type or namespace name 'ShopifyTokenRefreshWorker' could not be found`.
- [ ] **GREEN.** Implementa y registra el worker; actualiza `LinkStoreAsync`.
- [ ] `dotnet test $II --filter "FullyQualifiedName~ShopifyTokenRefreshWorkerTests.ATokenIsRefreshedWithin20MinutesAndNotBefore" -- xUnit.MaxParallelThreads=4`, luego el resto de la clase por nombre → verde.
- [ ] Commit: `feat(integrations): refresh expiring shopify tokens in a locked worker`

### Tarea 7: el probador no rechaza un token vencido, y nada se filtra

**Files:**
- Modify: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Shopify/ShopifyConnectionTester.cs:21-35`; `IntegrationsInfrastructureExtensions.cs` (`services.TryAddSingleton(TimeProvider.System)`)
- Test: `$IU/ShopifyConnectionTesterTests.cs:32-33`, `$II/ConnectionSecretLeakTests.cs`, `$II/IntegrationsApiHarness.cs:694` (`CapturedLogs` gana `Levels`)

**Interfaces — Produces:** `ShopifyConnectionTester(IShopifyAdminGateway gateway, IShopifyAppSettings shopifyApp, TimeProvider time)`; con `ShopifyTokenRefresh.IsExpired(fields["tokenExpiresAt"], time.GetUtcNow())` devuelve `ConnectionTestResult.Unreachable("token_refresh_pending")` sin llamar al gateway.

- [ ] **RED.** `AnExpiredTokenIsUnreachableWithoutCallingShopify` (`gateway.Calls` vacío, resultado no es `credentials_rejected`) y `ATokenWithoutExpiryIsStillTested` (campo ausente → llama a `"shop"`). En `ConnectionSecretLeakTests`, `ShopifyRefreshNeverLeaksEitherTokenOrTheResponse`: host con `ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Trace).AddFilter<CapturedLogs>(null, LogLevel.Trace))` (como `ClasicFlowEndToEndTests.cs:109`); enlaza, corre el worker con un 200 y luego con un 401 cuyo cuerpo repite el refresh token; control positivo `Assert.Contains(LogLevel.Trace, logs.Levels)` y que se logueó `"token refresh"`; `AssertNothingLeaksAsync` con `SentinelAccessToken`, el refresh token del enlace, el access y el refresh token refrescados, `SentinelShopifyClientSecret` y `"invalid_request"`.
- [ ] `dotnet build $IU` → `CS1729 'ShopifyConnectionTester' does not contain a constructor that takes 3 arguments`.
- [ ] **GREEN.** Implementa.
- [ ] `dotnet test $IU --filter "FullyQualifiedName~ShopifyConnectionTesterTests"`; `dotnet test $II --filter "FullyQualifiedName~ShopifyRefreshNeverLeaks" -- xUnit.MaxParallelThreads=4` → verde.
- [ ] Commit: `fix(integrations): do not reject a shopify token that is waiting for its refresh`
- [ ] **Fin del bloque B:** `dotnet test $IU`; `dotnet test $II --filter "FullyQualifiedName~ShopifyTokenRefreshWorkerTests|FullyQualifiedName~ConnectionSecretLeakTests|FullyQualifiedName~ConsumerPortsApiTests|FullyQualifiedName~InstagramConnectApiTests" -- xUnit.MaxParallelThreads=4`; `dotnet test $SI -- xUnit.MaxParallelThreads=4` (el harness cambió). Revisión del bloque.

---

## Bloque C — los consumidores de Shopify (Tareas 8–9, un implementador)

Depende del bloque A (claves y `ShopifyTokenRefresh`). Toca Bootstrapper y `Modules.Shopify.Application`.

### Tarea 8: el directorio entrega la conexión sin token vencido

**Files:**
- Modify: `src/Bootstrapper/ShopifyConnectionDirectory.cs:18-114` (ctor + `IClock clock`; `ToRefAsync`; filtro de `ListOperationalAsync`)
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyPorts.cs:27-42` (documentar: `IsOperational` ya no implica `AccessToken`)
- Test: `$BU/ShopifyAdaptersTests.cs:630-660`, `$BU/ShopifyAdaptersTestDoubles.cs:468-512` (`ConnectionWorld(status, shopifyEnabled, string? tokenExpiresAt = null)`, reloj fijo en `2026-10-18T12:00Z`), `$SU/ShopifyWebhookDeliveryProcessorTests.cs`

**Interfaces — Produces:** con `ShopifyTokenRefresh.IsExpired(fields[tokenExpiresAt], clock.UtcNow)`, `new ShopifyConnectionRef(..., IsOperational: <igual que hoy>, AccessToken: null)`; `ListOperationalAsync` devuelve sólo `{ IsOperational: true, AccessToken: not null }`.

- [ ] **RED.**
  ```csharp
  [Fact] public async Task ConnectionDirectoryKeepsAnExpiredTokenConnectionOperationalWithoutToken()
  // tokenExpiresAt = "2026-10-18T11:59:00.0000000+00:00" → IsOperational true, AccessToken null (por dominio y por id)
  [Fact] public async Task ConnectionDirectoryListOperationalSkipsAnExpiredToken()  // lista vacía
  [Fact] public async Task ConnectionDirectoryWithoutExpiryStillCarriesTheToken()  // AccessToken == ConnectionWorld.Token
  ```
  En `ShopifyWebhookDeliveryProcessorTests`: `ShopRedactWithAnExpiredTokenDeletesNothingOfTheCurrentConnection` (ref con `IsOperational: true, AccessToken: null` → el espejo de esa conexión sigue y las entregas no se borran) y `OrdersTopicWithAnExpiredTokenStillImports` (copia de `OrdersTopicOperationalUpsertsAndImports` con `AccessToken: null` → `Importer.Calls` con una entrada).
- [ ] `dotnet build $BU` → `CS1739 ... does not have a parameter named 'tokenExpiresAt'`.
- [ ] **GREEN.** Implementa. No registres nada nuevo: el directorio ya es scoped y `IClock` también. En `ConnectionWorld` usa el `FixedClock` de `$BU/StorageTestDoubles.cs:132`.
- [ ] `dotnet test $BU --filter "FullyQualifiedName~ShopifyAdaptersTests"`; `dotnet test $SU --filter "FullyQualifiedName~ShopifyWebhookDeliveryProcessorTests"` → verde.
- [ ] Commit: `feat(shopify): keep expired-token stores operational without a token`

### Tarea 9: la conciliación salta un token vencido y relee ante 401

**Files:**
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ReconcileStore.cs:33-41` (ctor + `IShopifyConnectionDirectory connections` después de `health`), `:63-70`, `:165-196`, `:281-286`
- Test: `$SU/ReconcileStoreTests.cs:200-240` y su `World` (`:406-440`, con `FakeConnectionDirectory` que por defecto devuelve `world.Connection`)

**Interfaces — Produces:**
```csharp
private async Task<(ShopifyOrdersPage Page, ShopifyConnectionRef Connection)> FetchAsync(
    ShopifyConnectionRef connection, DateTimeOffset since, string? cursor, bool reread, CancellationToken cancellationToken);
private static ServiceUnavailableException TokenRefreshPending() =>
    new(ShopifyErrorCodes.StoreSyncUnavailable, "The Shopify access token is waiting for its refresh; the next run retries.");
```
`RunAsync`: `!IsOperational` → `NotOperational()` (igual); `AccessToken` vacío → `TokenRefreshPending()` antes del lease. La conexión que devuelve `FetchAsync` reemplaza a la de la corrida para las páginas y reintentos siguientes. En `Rejected` con `reread == false`: `connections.FindAsync(tenantId, connectionId)`; `AccessToken` distinto y no vacío → reintenta una vez con `reread: true`; `{ IsOperational: true, AccessToken: null }` → `TokenRefreshPending()` sin reportar; otro caso → reporta como hoy.

- [ ] **RED.**
  ```csharp
  [Fact] public async Task ReconcileWithAnExpiredTokenSkipsWithoutCallingOrReporting()
  // Connection con AccessToken null → ServiceUnavailableException StoreSyncUnavailable; Source.Calls vacío; Health.Rejected vacío; Leases.Released == 0
  [Fact] public async Task Reconcile401WithARotatedTokenRereadsAndRetriesOnce()
  // directorio devuelve "shpat_rotated"; Source.Calls.Select(c => c.Connection.AccessToken) == [original, "shpat_rotated"]; Health.Rejected vacío
  [Fact] public async Task Reconcile401WhileTheTokenExpiredSkipsWithoutReporting()   // Review Focus 6
  [Fact] public async Task Reconcile401TwiceWithARotatedTokenReportsOnce()           // segundo 401 → Health.Rejected una vez
  ```
  `Reconcile401ReportsRejectedAndStops` sigue igual (el directorio devuelve el mismo token).
- [ ] `dotnet build $SU` → `CS7036 There is no argument given that corresponds to the required parameter 'connections'`.
- [ ] **GREEN.** Implementa.
- [ ] `dotnet test $SU --filter "FullyQualifiedName~ReconcileStoreTests"` → verde.
- [ ] Commit: `fix(shopify): skip reconciliation while the token refreshes and reread it on 401`
- [ ] **Fin del bloque C:** `dotnet test $SU`; `dotnet test $BU`; `dotnet test $W/tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~ShopifyLayerTests|FullyQualifiedName~IntegrationsLayerTests"`; `dotnet test $SI --filter "FullyQualifiedName~ShopifyReconcileWorkerTests|FullyQualifiedName~ShopifySyncApiTests|FullyQualifiedName~ShopifyWebhookWorkerTests" -- xUnit.MaxParallelThreads=4`. Agrega al README, § Integración con Shopify, un párrafo: tokens que expiran, worker cada 5 min con margen de 20, qué hace un 401 y cómo se ve (`token_expired` → volver a abrir la app). Commit `docs(shopify): document expiring offline tokens`. Revisión fuerte de toda la rama.

Antes de mergear a `develop`: suite de los proyectos tocados (`Integrations`, `Shopify`, `Bootstrapper.UnitTests`, `ArchitectureTests`), uno a la vez. La verificación manual de §9 (tienda de desarrollo real, `tokenExpiresAt` a 1 h, refresco observado, forma de la respuesta) va antes de `main` y la hace el owner; el texto del frontend para `token_expired` (§8) va con su propio spec en `qep-frontend`.

---

## Autorrevisión

- **Cobertura del spec.** §6.1 canje y rechazo → T2; guardado en instalación y conexión → T1, T2, T4; rekey → T3 (D5); refresco de la instalación vieja → T4. §6.2 worker, estados elegibles, constantes, candado, reenlace, guardado → T3, T6 (D1-D3). §6.3 fallas → T5, T6 (D9, D13). §6.4 directorio, redact, ingesta, conciliación, red ante 401 → T8, T9 (D7, D8, D12); probador → T7 (D11). §6.5 logs → T6, T7; auditoría: no se audita el refresco (Global Constraints). §7 arquitectura → fin del bloque C; migración con el factory → T1. §9: cada viñeta tiene prueba (T2: canje y rechazo; T4: enlace de más de 1 h y 401; T6: margen, candado, reenlace, 401/503, sin `refreshToken`, estados, guardado fallido; T8: redact e ingesta; T9: directorio vencido y relectura; T7: Trace). §8 y la verificación manual quedan fuera, dicho al final del bloque C.
- **Nombres.** `ShopifyTokenRefresh`, `ShopifyTokenLock`, `ShopifyTokenRefreshWorker`, `ShopifyTokenRefreshRunResult`, `RefreshTokenAsync`, `TryStoreRefreshedTokenAsync`, `ReportTokenExpiredAsync`, `LockShopifyTokenAsync`/`TryLockShopifyTokenAsync`, `ApplyRefreshedToken` y las claves `refreshToken`/`tokenExpiresAt`/`refreshTokenExpiresAt` se usan igual en todas las tareas. Códigos usados, todos existentes: `token_expired`, `app_uninstalled`, `webhooks_not_registered`, `integrations.shopify.install_expired`, `integrations.shopify.install_not_found`, `integrations.connection.provider_unreachable`, `shopify.store.sync_unavailable`.
- **Review Focus.** Los seis tienen prueba nombrada en su tarea dueña.
- **Proporción.** Nueve tareas en tres bloques; el plan no transcribe cuerpos salvo el algoritmo del worker y el orden del enlace, que son los que el implementador no puede deducir.

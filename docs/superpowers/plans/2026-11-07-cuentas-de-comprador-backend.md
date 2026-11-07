# Cuentas de comprador — backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el comprador de cualquier tienda tenga una cuenta de plataforma (email + contraseña, verificada) con un perfil de checkout guardado, una sesión atada al host de la tienda y la opción de crearla o actualizarla desde el pedido, sin que la cuenta pueda hacer fallar ni demorar un pedido ni revelar si un email existe.

**Architecture:** Módulo nuevo `Buyers` (Domain / Application / Infrastructure / Api, esquema `buyers`), separado de Identity y sin `TenantId`. Replica los patrones de Identity (hash `PasswordHasher<T>`, tokens y sesiones guardados como SHA-256, outbox de plataforma) sin compartir tablas ni tipos. Su superficie HTTP cuelga del mismo prefijo `/api/v1/public/stores/{host}` y reusa `StorefrontKeyFilter`/`StorefrontClient` de `Modules.Store.Api`; lo que necesita de Store (resolver el host, Turnstile) entra por puertos `IBuyer*` con adaptador en Bootstrapper, y lo que Store necesita de Buyers (hashear la contraseña y actualizar el perfil desde el pedido) entra por `IStoreBuyerAccounts`, también con adaptador en Bootstrapper. Crear la cuenta desde el pedido es asíncrono y durable: Store publica `store.buyer-signup-requested.v1` en el outbox de plataforma **en la misma transacción** que guarda la solicitud (con la contraseña ya hasheada), y un worker de Buyers lo consume con su propio inbox. Los cuatro correos salen por cuatro workers nuevos de Notifications.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, FluentValidation, `Microsoft.Extensions.Identity.Core` (sólo `PasswordHasher<TUser>`), xUnit v3, Testcontainers.PostgreSql (`postgres:18-alpine`), `WebApplicationFactory<Program>`.

**Spec:** [`docs/superpowers/specs/2026-11-07-cuentas-de-comprador-design.md`](../specs/2026-11-07-cuentas-de-comprador-design.md) · spec hermano: `qep-storefront/docs/superpowers/specs/2026-11-07-cuentas-de-comprador-design.md`

---

## Antes de empezar

- Worktree: `qep-backend-worktrees/cuentas-de-comprador`, rama `feature/cuentas-de-comprador` (desde `develop`). Antes de **cada** commit: `git branch --show-current` tiene que decir `feature/cuentas-de-comprador`; si dice otra cosa, frena y pregunta.
- Commits: Conventional Commits, **sin** `Co-Authored-By` ni atribución de IA. `docs/estado.md` **no** se toca en esta rama.
- Todo comando es PowerShell, una línea por comando, sin `&&`.
- Si `Api.exe` corre, `dotnet build`/`test`/`ef` fallan por archivo bloqueado. No lo detengas si no lo lanzaste tú: avisa.
- Pruebas de integración: Docker, y antes `$env:Seed__Enabled = "false"`. **Nunca** la solución entera: un proyecto a la vez, con filtro, y `-- xUnit.MaxParallelThreads=4`.
- Cambiar un `.csproj` (proyecto nuevo, paquete o referencia) obliga a `dotnet restore --force-evaluate` y a commitear los `packages.lock.json` tocados en el mismo commit.
- Migraciones con el factory de diseño:
  `dotnet ef migrations add <Nombre> --project src/Modules/Buyers/Modules.Buyers.Infrastructure --context BuyersDbContext -o Persistence/Migrations`

## Global Constraints

- Cuenta **única de plataforma**: sin `TenantId`, email normalizado (trim + minúsculas invariantes, máx. 254) y único.
- Nada de `identity.*`: ni tablas, ni tipos, ni `IUserDirectory`. Un comprador nunca toca la consola.
- Contraseña: **10 a 128** caracteres, sin reglas de composición (igual que `PasswordRules` de Identity, amarrado por prueba de paridad).
- Hash: `PasswordHasher<BuyerAccount>` detrás de `IBuyerPasswordHasher`; `SuccessRehashNeeded` → re-hash al iniciar sesión; email inexistente verifica contra un hash ficticio.
- Tokens de email: 32 bytes aleatorios en base64url; en base sólo SHA-256 hex. Verificación **24 h**, restablecer **1 h**, un solo uso, consumo atómico; emitir uno nuevo invalida los no usados del mismo propósito.
- Sesión: token en claro **una sola vez** (respuesta del login), en base sólo su hash; atada a cuenta **y host**; **30 días** absolutos, **7 días** sin uso; `LastSeenAt` se toca a lo sumo cada 5 min.
- Header del token de sesión: **`X-Qep-Buyer-Session`**. El backend no escribe cookies.
- Consentimiento: fecha, IP del visitante, host, URL `https://{host}/cuenta/politica` armada por el backend y versión de la política; tabla propia, **nunca se borra** automáticamente ni con la cuenta.
- Turnstile (`ICaptchaVerifier` de Store, vía puerto) en registro, olvidé mi contraseña y reenviar verificación; **no** en login; crear cuenta desde el checkout usa el del pedido.
- Límite por IP del visitante: **10 por minuto, por ruta**, en las rutas de credenciales.
- Respuestas neutras (`202`) en registro, reenvío y olvidé; registro y "guardar mis datos" en el checkout responden igual exista o no la cuenta.
- Cuenta sin verificar: no inicia sesión; se purga a los **7 días** de su último registro.
- Toda respuesta de `/buyer/*` lleva `Cache-Control: no-store`.
- Enlaces de correo al host de origen: `https://{host}/cuenta/verificar?token=…`, `https://{host}/cuenta/restablecer?token=…`, `https://{host}/cuenta/ingresar`, `https://{host}/cuenta/olvide`.
- Correos: remitente de plataforma (la configuración `Notifications:Infobip:SenderEmail` de hoy), el cuerpo nombra la tienda, copy en español colombiano tuteando, nombre de tienda HTML-escapado.
- Nunca en log, auditoría ni mensaje de excepción: contraseña, hash, token de sesión ni token de email. Los logs de la cuenta no llevan el email.
- El pedido nunca falla por la cuenta: el alta desde el checkout es un mensaje durable del outbox que procesa un worker; actualizar el perfil corre después del commit del pedido y sus errores sólo se registran. El único costo en el request es un hash de contraseña, antes de abrir la transacción.
- La contraseña en claro **nunca** se persiste ni se loguea: el mensaje del outbox lleva sólo `passwordHash`, calculado con el hasher de Buyers antes de guardar nada.
- El alta desde el checkout es idempotente por solicitud (`orderRequestId`, único por `(tenant, clientRequestId)`): un reintento no escribe otro mensaje, y un mensaje reprocesado no crea otra cuenta ni manda otro correo.

## Decisiones de este plan (lo que el spec deja a quien construye)

| # | Decisión | Por qué |
| --- | --- | --- |
| D1 | Módulo nuevo `Buyers`, esquema `buyers`, con su `BuyersLayerTests`. | La cuenta es de plataforma y la tienda es por tenant; `StoreDbContext` es por tenant. Separa también la eliminación por Ley 1581. |
| D2 | `Modules.Buyers.Api` referencia `Modules.Store.Api` (sólo `StorefrontKeyFilter`, `StorefrontClient`), amarrado en `BuyersLayerTests`. Store no referencia Buyers en ninguna capa (`StoreLayerTests`). | Las rutas tienen que pasar por el mismo filtro de clave; mover el filtro a otro lado tocaría todo lo público de Store. |
| D3 | Códigos `store.buyer.*` (tabla del contrato). Reusa literalmente `store.public.not_found`, `store.public.client_unauthorized`, `store.public.visitor_ip_required`, `store.captcha.unavailable`, `validation.failed`. | Pedido del owner: consistencia con `store.public.*`; el storefront ya traduce esos. |
| D4 | El perfil se guarda como `jsonb` en `buyers.profiles` (una fila por cuenta), con la forma del cuerpo. | Es una foto que sólo se precarga; nunca se consulta por campo. |
| D5 | `createAccount` del pedido lleva `acceptsDataPolicy: true` explícito (422 si falta). | La decisión 11 pide registrar la autorización; sin un booleano no hay prueba de que se aceptó. |
| D6 | `createAccount` y `updateProfile: true` juntos → 422 `validation.failed` en `CreateAccount`. | Con sesión no hay cuenta que crear; aceptar ambos dejaría la semántica ambigua. |
| D7 | Crear cuenta desde el pedido: Store hashea la contraseña con el hasher de Buyers (puerto `IStoreBuyerAccounts.HashPassword`) **antes** de la transacción, y en la misma transacción que la solicitud escribe en `platform.outbox_messages` el evento `store.buyer-signup-requested.v1` con `passwordHash` (nunca la contraseña). `BuyerSignupRequestedWorker` (Buyers.Infrastructure) lo consume con su inbox `buyers.inbox_messages` y marca `buyers.processed_signups(order_request_id)` en el mismo `SaveChanges` que el efecto. Store declara el contrato (nombre + payload) en `StoreIntegrationEvents`; Buyers lo lee por nombre con su propio record, amarrados por una prueba de paridad en `Bootstrapper.UnitTests` (precedente `QuotationEventPayloads`). | Un canal en memoria perdía altas en un reinicio sin log mientras el comprador creía tener cuenta. El outbox transaccional las hace durables y Store sigue sin ver Buyers. El hash en el request (decenas de ms) es el precio de no persistir texto plano. |
| D8 | El payload del outbox lleva `email`, `storeHost`, `storeName` y `token`. | No hay directorio de compradores en Notifications y "cuenta eliminada" se envía cuando la cuenta ya no existe. |
| D9 | `POST …/buyer/email/verify` no abre sesión (`204`). Login de cuenta sin verificar con contraseña correcta → `403` y **no** reenvía solo: el storefront ofrece reenviar (ruta con Turnstile). | El spec dice "se ofrece reenviar"; reenviar sin Turnstile abriría una vía de envío de correos sin desafío. |
| D10 | El límite de 10/min por ruta y por IP se aplica también a verificar, restablecer, cambiar contraseña y eliminar cuenta (además de las cuatro del spec). | Todas aceptan un secreto adivinable (contraseña o token). |
| D11 | Sin auditoría en `audit.entries`. | La tabla es por tenant y el comprador no pertenece a ninguno; el spec no la pide. |
| D12 | Contraseña actual mala en cambiar contraseña o eliminar cuenta → `422 store.buyer.password_incorrect` con `errors.CurrentPassword` / `errors.Password`, **no** 401. | Un 401 con sesión válida haría que el BFF borre la cookie. El BFF borra la cookie sólo con el **código** `store.buyer.session_invalid`. |

Riesgos residuales aceptados (mismos que Identity): `forgot` y `resend` escriben token + outbox sólo si la cuenta existe, así que el tiempo de respuesta puede diferir; el hash corre en los tres caminos del registro para acotarlo.

## Decisiones pendientes del owner

1. **Datos legales y plazo de conservación** (pendiente 1 del spec). El código no los necesita: el consentimiento nunca se borra solo.
2. **Remitente de plataforma** (pendiente 2 del spec). El plan usa el `Notifications:Infobip:SenderEmail` que ya existe; cambiar dirección o nombre es configuración, no código.
3. **Identificador de versión de la política.** El plan fija `BuyerDataPolicy.Version = "2026-11-07"` (fecha del borrador) como valor provisional. El owner lo confirma o lo cambia cuando el texto legal quede cerrado; cambiarlo es una constante y su prueba.

## Review Focus

1. **Dos registros simultáneos del mismo email nuevo** (registro o checkout): una sola cuenta, los dos `202`, el perdedor sigue el camino de "email existente" y nunca hay 500. → `BuyerSignupRaceTests.TwoConcurrentSignupsWithTheSameNewEmailCreateOneAccount` (Task 5).
2. **Email con mayúsculas o espacios** en registro, login, reenviar, olvidé y checkout: cae en la misma cuenta. → `BuyerSessionApiTests.LoginNormalizesEmailCaseAndWhitespace` (Task 7) y `BuyerRegistrationApiTests.RegisterWithAnUppercasedExistingEmailSendsTheExistingAccountNotice` (Task 5).
3. **Sesión de la tienda A usada en la tienda B**, en `GET session`, en `PUT profile` y en el pedido con `updateProfile`: 401 / `buyerSessionRejected: true`, el perfil no cambia. → `BuyerSessionApiTests.ASessionIsRejectedOnAnotherHost` (Task 7), `PublicOrderBuyerAccountApiTests.UpdateProfileWithASessionFromAnotherHostIsIgnoredAndFlagged` (Task 12).
4. **Nombre de tienda con `<`, `&` o comillas** en los correos: el HTML lo escapa, el asunto y el texto plano no se rompen. → `BuyerEmailTemplatesTests.TheStoreNameIsHtmlEncoded` (Task 11).
5. **Reintento del pedido con el mismo `clientRequestId` y `createAccount`**: mismo recibo, una sola cuenta, un solo correo; y un `createAccount` con email ya verificado devuelve el mismo recibo que uno nuevo. → `PublicOrderBuyerAccountApiTests.ARetryWithTheSameClientRequestIdDoesNotSignUpTwice` y `…TheReceiptIsTheSameWhetherTheEmailHasAnAccountOrNot` (Task 12).

---

## Contrato HTTP para el storefront

Lo llama **sólo el BFF**, de servidor a servidor. Todas las rutas cuelgan de `/api/v1/public/stores/{host}` y heredan lo de hoy:

- `X-Qep-Storefront-Key` obligatorio → sin ella `401 store.public.client_unauthorized`.
- `X-Qep-Visitor-Ip` con la IP del visitante (confiable sólo con la clave). Obligatorio donde la tabla dice "IP: sí" → sin él `422 store.public.visitor_ip_required`.
- `{host}` que no es una tienda publicada y activa → `404 store.public.not_found` (mismo cuerpo que hoy).
- Errores como `application/problem+json` con `code` y `traceId`; los `422 validation.failed` llevan `errors` (claves PascalCase de FluentValidation, p. ej. `Password`, `Profile.Billing.Email`).
- `429` sin cuerpo al pasar el límite. "Credenciales" = 10 por minuto, por ruta y por IP del visitante; "lectura" = el límite de lecturas de hoy (300/min).
- Toda respuesta lleva `Cache-Control: no-store`.
- **Sesión:** header **`X-Qep-Buyer-Session: <token>`**. Ausente, vacío, desconocido, vencido, revocado, de una cuenta borrada o emitido en otro host → `401 store.buyer.session_invalid`. **Ese código, y sólo ese, es la señal para borrar la cookie.**

### Tipos compartidos

```jsonc
// BuyerProfile: mismos campos y reglas que billing/shipping de POST /order-requests (PublicBillingInput / PublicShippingInput).
{
  "billing": {
    "name": "Ana Pérez", "identificationType": "CC", "identificationNumber": "1020304050",
    "phone": "3001234567", "email": "ana@correo.com", "address": "Cra 1 # 2-3",
    "country": "CO", "departmentId": "<guid>", "cityId": "<guid>", "cityName": null
  },
  "shipping": null,                 // o { "recipientName", "phone", "address", "country", "departmentId", "cityId", "cityName" }
  "shippingSameAsBilling": true     // true ⇔ shipping es null (si no, 422 en Shipping)
}
// Buyer
{ "email": "ana@correo.com", "profile": BuyerProfile | null }   // email = el de login, no el de facturación
```

El perfil **no** valida contra la tienda (país vendido, ciudad del departamento): eso lo sigue haciendo el pedido.

### Rutas

| # | Método y ruta | Cuerpo | Éxito | Errores | Límite | IP | Turnstile | Sesión |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | `POST /buyer/register` | `{ "email", "password", "acceptsDataPolicy": true, "turnstileToken", "profile": BuyerProfile \| null }` | `202` sin cuerpo | 422 `validation.failed` (`Email`, `Password`, `TurnstileToken`, `Profile.*`), 422 `store.buyer.consent_required`, 422 `store.buyer.captcha_invalid`, 503 `store.captcha.unavailable` | credenciales | sí | sí | no |
| 2 | `POST /buyer/email/verify` | `{ "token" }` | `204` | 422 `validation.failed` (`Token`), 422 `store.buyer.token_invalid` (inexistente, vencido o usado: no se distingue) | credenciales | no | no | no |
| 3 | `POST /buyer/email/resend` | `{ "email", "turnstileToken" }` | `202` sin cuerpo | 422 `validation.failed`, 422 `store.buyer.captcha_invalid`, 503 `store.captcha.unavailable` | credenciales | sí | sí | no |
| 4 | `POST /buyer/session` (login) | `{ "email", "password" }` | `200` `{ "sessionToken": "<43 chars base64url>", "expiresAt": "2026-12-07T15:04:05+00:00", "buyer": Buyer }` | 422 `validation.failed` (sólo vacíos), 401 `store.buyer.credentials_invalid` (email inexistente o contraseña mala, mismo cuerpo), 403 `store.buyer.email_not_verified` (contraseña correcta, sin verificar) | credenciales | opcional | no | no |
| 5 | `GET /buyer/session` | — | `200` `Buyer` | 401 `store.buyer.session_invalid` | lectura | no | no | sí |
| 6 | `DELETE /buyer/session` (logout) | — | `204` siempre (idempotente; sin sesión válida no hace nada) | — | lectura | no | no | opcional |
| 7 | `PUT /buyer/profile` | `BuyerProfile` | `200` `Buyer` | 401 `store.buyer.session_invalid`, 422 `validation.failed` | lectura | no | no | sí |
| 8 | `POST /buyer/password` (cambiar) | `{ "currentPassword", "newPassword" }` | `204`; revoca las **demás** sesiones, la actual sigue | 401 `store.buyer.session_invalid`, 422 `validation.failed` (`NewPassword`), 422 `store.buyer.password_incorrect` (`errors.CurrentPassword`) | credenciales | no | no | sí |
| 9 | `POST /buyer/password/forgot` | `{ "email", "turnstileToken" }` | `202` sin cuerpo | 422 `validation.failed`, 422 `store.buyer.captcha_invalid`, 503 `store.captcha.unavailable` | credenciales | sí | sí | no |
| 10 | `POST /buyer/password/reset` | `{ "token", "newPassword" }` | `204`; marca el email verificado y revoca **todas** las sesiones; no abre sesión | 422 `validation.failed` (`NewPassword`, `Token`), 422 `store.buyer.token_invalid` | credenciales | no | no | no |
| 11 | `POST /buyer/account/delete` | `{ "password" }` | `204`; borra cuenta, perfil, sesiones y tokens; conserva el consentimiento; envía "cuenta eliminada" | 401 `store.buyer.session_invalid`, 422 `store.buyer.password_incorrect` (`errors.Password`) | credenciales | no | no | sí |

Notas:
- Orden de chequeos (1, 3, 9): IP → host (404) → cuerpo (422) → consentimiento (sólo 1) → Turnstile → efecto. Un rechazo no escribe nada.
- `expiresAt` es el vencimiento **absoluto**; la sesión además vence tras 7 días sin uso, así que la cookie puede durar más que la sesión: el BFF lo resuelve con el 401.
- 1, 3 y 9 responden `202` exista o no la cuenta (anti-enumeración). 1 con email ya **verificado** envía "ya tienes una cuenta" y no toca nada; con email **sin verificar** reemplaza contraseña y perfil, invalida verificaciones previas y reenvía la verificación.

### Cambios en `POST /order-requests`

Cuerpo: dos campos opcionales nuevos (todo lo demás igual).

```jsonc
{
  "...": "campos de hoy",
  "createAccount": { "password": "…", "acceptsDataPolicy": true },   // opcional; null o ausente = no crear
  "updateProfile": false                                              // opcional; default false
}
```

- `createAccount`: contraseña 10–128 → si no, `422 validation.failed` con `errors["CreateAccount.Password"]`; `acceptsDataPolicy` distinto de `true` → `422 validation.failed` con `errors["CreateAccount.AcceptsDataPolicy"]`. Se valida con el resto del cuerpo, **antes** de aceptar el pedido. Email de login = `billing.email`; perfil = `billing`, `shipping`, `shippingSameAsBilling = (shipping == null)`.
- `updateProfile: true` exige el header `X-Qep-Buyer-Session` (el BFF nunca lo manda sin sesión). Con `createAccount` a la vez → `422 validation.failed` en `CreateAccount`.
- Recibo `201`: suma **`"buyerSessionRejected": bool`** → `true` sólo si se pidió `updateProfile` y la sesión no sirve (ausente, vencida, de otro host, de cuenta borrada). El pedido se acepta igual. El BFF borra la cookie en ese caso.

```jsonc
{ "id": "<guid>", "number": "T-0001", "total": 10000, "currency": "COP", "buyerSessionRejected": false }
```

- Reintento con el mismo `clientRequestId`: el primer recibo, `buyerSessionRejected: false`, sin crear cuenta ni tocar perfil.
- La cuenta se crea de forma asíncrona y durable (un mensaje del outbox que commitea con la solicitud), así que puede tardar unos segundos en existir después del `201`. Sus errores (email existente, fallo al guardar) nunca cambian la respuesta.

### Correos (para que coincidan con las pantallas del storefront)

| Evento | Enlace |
| --- | --- |
| Verificación (registro, reenvío, checkout con email nuevo o sin verificar) | `https://{host}/cuenta/verificar?token=<token>` |
| "Ya tienes una cuenta" | `https://{host}/cuenta/ingresar` y `https://{host}/cuenta/olvide` |
| Restablecer contraseña | `https://{host}/cuenta/restablecer?token=<token>` |
| Cuenta eliminada | sin enlace |

---

## Mapa de archivos

| Archivo | Responsabilidad |
| --- | --- |
| `src/Modules/Buyers/Modules.Buyers.Domain/*` | `BuyerAccount`, `BuyerProfile` + `BuyerProfileData`, `BuyerSession`, `BuyerEmailToken`, `BuyerConsent`, `BuyerEmail`, `BuyerDataPolicy`, `BuyerErrorCodes`, `BuyerDomainException` |
| `src/Modules/Buyers/Modules.Buyers.Application/Ports.cs` | repositorios, unidad de trabajo, outbox, hasher, `IBuyerStorefronts`, `IBuyerCaptcha` |
| `…Application/BuyerPasswordRules.cs`, `BuyerProfileValidator.cs`, `BuyerTokenCodec.cs` | reglas compartidas |
| `…Application/BuyerIntegrationEvents.cs` | nombres de evento y `BuyerEmailPayload` |
| `…Application/BuyerSignup.cs` | **el** alta (registro y checkout): nuevo / verificado / sin verificar / carrera |
| `…Application/BuyerSessions.cs` | emitir, exigir y resolver sesiones por host |
| `…Application/BuyerDtos.cs` | cuerpos y respuestas del contrato |
| `…Application/RegisterBuyer.cs`, `VerifyBuyerEmail.cs`, `LoginBuyer.cs`, `BuyerSessionQueries.cs`, `UpdateBuyerProfile.cs`, `ChangeBuyerPassword.cs`, `BuyerPasswordRecovery.cs`, `DeleteBuyerAccount.cs` | comandos y manejadores |
| `src/Modules/Buyers/Modules.Buyers.Infrastructure/Persistence/*` | `BuyersDbContext`, factory, repos, `BuyersUnitOfWork`, `UniqueViolation`, `BuyerOutboxWriter`, migración `InitialBuyers` |
| `…Infrastructure/AspNetCoreBuyerPasswordHasher.cs` | hash |
| `…Infrastructure/Signup/BuyerSignupRequestedWorker.cs`, `BuyersInboxClaims.cs`, `BuyerSignupRequestedMessage.cs` | alta desde el checkout, consumida del outbox (D7) |
| `src/Modules/Store/Modules.Store.Application/StoreIntegrationEvents.cs`, `IStoreOutbox.cs`; `…Store.Infrastructure/Persistence/StoreOutboxWriter.cs` | contrato y escritura del evento de alta (D7) |
| `…Infrastructure/BuyerHousekeepingWorker.cs` | purga de no verificadas, sesiones y tokens |
| `src/Modules/Buyers/Modules.Buyers.Api/PublicBuyerEndpoints.cs` | las 11 rutas y `BuyerSessionHeader` |
| `src/Modules/Store/Modules.Store.Api/StorefrontClient.cs` | `TryGetDeclaredVisitorIp(HttpContext, out string)` |
| `src/Modules/Store/Modules.Store.Application/SubmitOrderRequest.cs`, `IStoreBuyerAccounts.cs` | campos nuevos, validación y seguimiento post-commit |
| `src/Bootstrapper/BuyerStorefronts.cs`, `BuyerCaptcha.cs`, `StoreBuyerAccounts.cs`, `QepServiceCollectionExtensions.cs` | adaptadores y registro |
| `src/Api/RateLimiterPolicies.cs`, `Program.cs` | política `StorefrontCredentials`, mapeo, inicialización de la base |
| `src/Modules/Notifications/…/Buyer*EmailTemplate.cs`, `BuyerAccountLinks.cs`, `Messaging/Buyer*DeliveryWorker.cs` | cuatro correos |
| `tests/Modules/Buyers/Modules.Buyers.UnitTests/*` | dominio y aplicación con dobles (`BuyersTestDoubles.cs`) |
| `tests/Modules/Buyers/Modules.Buyers.IntegrationTests/BuyersApiHarness.cs` | Postgres + `WebApplicationFactory` con `IBuyerStorefronts` e `IBuyerCaptcha` falsos, clave del storefront, helpers de outbox y SQL |
| `tests/ArchitectureTests/ArchitectureTests/BuyersLayerTests.cs` | capas y acoplamientos |
| `tests/Bootstrapper/Bootstrapper.UnitTests/BuyerParityTests.cs` | paridad de contraseña y perfil con Identity y Store |

---

### Task 1: Esqueleto del módulo `Buyers` y sus reglas de capas

**Files:**
- Create: `src/Modules/Buyers/Modules.Buyers.{Domain,Application,Infrastructure,Api}/*.csproj` (forma de `Modules.Store.*`: Domain → BuildingBlocks.Domain; Application → Domain, BuildingBlocks.Application, FluentValidation, Logging.Abstractions; Infrastructure → Application, EF Core, Npgsql, `Microsoft.Extensions.Identity.Core`, Hosting.Abstractions; Api → Application, `Modules.Store.Api`, `Microsoft.AspNetCore.App`)
- Create: `tests/Modules/Buyers/Modules.Buyers.UnitTests/*.csproj`, `tests/Modules/Buyers/Modules.Buyers.IntegrationTests/*.csproj` (forma de los de Store)
- Create: un tipo ancla por capa: `BuyerErrorCodes` (Domain), `BuyerIntegrationEvents` (Application), `BuyersInfrastructureExtensions` (Infrastructure), `PublicBuyerEndpoints` (Api) — vacíos salvo lo que la prueba pide
- Modify: `Backend.slnx`, `tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj`
- Test: `tests/ArchitectureTests/ArchitectureTests/BuyersLayerTests.cs`, `StoreLayerTests.cs`

**Interfaces:**
- Produces: proyectos `Modules.Buyers.*` compilables; `BuyerErrorCodes`, `BuyerIntegrationEvents`, `BuyersInfrastructureExtensions`, `PublicBuyerEndpoints` como anclas de ensamblado.

- [ ] **Step 1: Escribir las pruebas de capas**

`BuyersLayerTests` (mismo esqueleto que `StoreLayerTests`):
- `DomainDoesNotReferenceOuterLayers`, `ApplicationDoesNotReferenceInfrastructureOrApi`, `InfrastructureDoesNotReferenceApi`, `ApplicationDoesNotReferencePersistenceLibraries`.
- `NoBuyersLayerReferencesIdentity`: ninguna de las cuatro capas referencia `Modules.Identity*`.
- `ApplicationAndInfrastructureReferenceNoOtherBusinessModule`: sus referencias `Modules.*` empiezan todas por `Modules.Buyers`.
- `ApiReferencesOnlyStoreApiAmongOtherModules`: las referencias `Modules.*` de Api son `Modules.Buyers.*` o exactamente `Modules.Store.Api`.

`StoreLayerTests` suma `NoStoreLayerReferencesBuyers`: ninguna capa de Store referencia `Modules.Buyers*`.

- [ ] **Step 2: RED** — `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~BuyersLayerTests|FullyQualifiedName~StoreLayerTests"` → no compila: `Modules.Buyers.Domain` no existe.

- [ ] **Step 3: Crear proyectos y anclas**, sumarlos a `Backend.slnx` (carpetas `/src/Modules/Buyers/` y `/tests/Modules/Buyers/`) y a `ArchitectureTests.csproj`; `dotnet restore --force-evaluate`.

- [ ] **Step 4: GREEN** — mismo comando → PASS.

- [ ] **Step 5: Commit**

```powershell
git add Backend.slnx src/Modules/Buyers tests/Modules/Buyers tests/ArchitectureTests
git commit -m "chore(buyers): scaffold the buyers module and its layer rules"
```

---

### Task 2: Dominio

**Files:**
- Create: `src/Modules/Buyers/Modules.Buyers.Domain/{BuyerErrorCodes,BuyerDomainException,BuyerEmail,BuyerAccount,BuyerProfile,BuyerSession,BuyerEmailToken,BuyerConsent,BuyerDataPolicy}.cs`
- Test: `tests/Modules/Buyers/Modules.Buyers.UnitTests/{BuyerEmailTests,BuyerAccountTests,BuyerSessionTests,BuyerEmailTokenTests,BuyerDataPolicyTests}.cs`

**Interfaces:**
- Produces:
  - `BuyerErrorCodes`: `CredentialsInvalid = "store.buyer.credentials_invalid"`, `EmailNotVerified = "store.buyer.email_not_verified"`, `SessionInvalid = "store.buyer.session_invalid"`, `TokenInvalid = "store.buyer.token_invalid"`, `PasswordIncorrect = "store.buyer.password_incorrect"`, `ConsentRequired = "store.buyer.consent_required"`, `CaptchaInvalid = "store.buyer.captcha_invalid"`, `EmailTaken = "store.buyer.email_taken"` (interno), `StoreNotFound = "store.public.not_found"`, `VisitorIpRequired = "store.public.visitor_ip_required"`.
  - `BuyerDomainException(string code, string message) : DomainException`.
  - `BuyerEmail.MaxLength = 254`; `static bool TryNormalize(string? raw, out string normalized)` (trim, `ToLowerInvariant`, no vacío, ≤ 254, contiene `@`).
  - `BuyerAccount`: `Guid Id`, `string Email`, `string PasswordHash`, `DateTimeOffset? EmailVerifiedAt`, `DateTimeOffset CreatedAt`, `DateTimeOffset UpdatedAt`, `bool IsVerified`; `static readonly TimeSpan UnverifiedRetention = 7 d`; `static BuyerAccount Register(Guid id, string normalizedEmail, string passwordHash, DateTimeOffset now)`; `void ReplacePendingRegistration(string passwordHash, DateTimeOffset now)` (lanza `InvalidOperationException` si está verificada); `void MarkEmailVerified(DateTimeOffset now)` (idempotente); `void ChangePasswordHash(string passwordHash, DateTimeOffset now)`.
  - `BuyerProfileData(BuyerBillingData Billing, BuyerShippingData? Shipping, bool ShippingSameAsBilling)`, `BuyerBillingData(Name, IdentificationType, IdentificationNumber, Phone, Email, Address, Country, Guid? DepartmentId, Guid? CityId, string? CityName)`, `BuyerShippingData(string? RecipientName, Phone, Address, Country, Guid? DepartmentId, Guid? CityId, string? CityName)`.
  - `BuyerProfile`: `Guid AccountId`, `BuyerProfileData Data`, `DateTimeOffset UpdatedAt`; `static Create(Guid accountId, BuyerProfileData data, DateTimeOffset now)`; `void Replace(BuyerProfileData data, DateTimeOffset now)`.
  - `BuyerSession`: `Guid Id`, `Guid AccountId`, `string StoreHost`, `string TokenHash`, `CreatedAt`, `LastSeenAt`, `ExpiresAt`, `RevokedAt?`, `RevokedReason?`, `string? IpAddress`; `AbsoluteLifetime = 30 d`, `IdleTimeout = 7 d`, `TouchThreshold = 5 min`; `static Issue(Guid id, Guid accountId, string storeHost, string tokenHash, DateTimeOffset now, string? ipAddress)`; `bool IsValidFor(string storeHost, DateTimeOffset now)`; `bool Touch(DateTimeOffset now)` (devuelve si tocó); `void Revoke(DateTimeOffset now, string reason)`.
  - `BuyerEmailTokenPurpose { Verify = 1, Reset = 2 }`; `BuyerEmailToken`: `Id`, `AccountId`, `Purpose`, `TokenHash`, `CreatedAt`, `ExpiresAt`, `UsedAt?`; `VerifyLifetime = 24 h`, `ResetLifetime = 1 h`; `static Issue(Guid id, Guid accountId, BuyerEmailTokenPurpose purpose, string tokenHash, DateTimeOffset now)`.
  - `BuyerConsent`: `Id`, `Guid AccountId`, `Email`, `AcceptedAt`, `VisitorIp`, `StoreHost`, `PolicyUrl`, `PolicyVersion`; `static Record(Guid id, Guid accountId, string email, string visitorIp, string storeHost, DateTimeOffset now)` (arma URL y versión desde `BuyerDataPolicy`).
  - `BuyerDataPolicy`: `const string Version = "2026-11-07"`; `static string UrlFor(string storeHost) => $"https://{storeHost}/cuenta/politica"`.

- [ ] **Step 1: Escribir las pruebas**

```csharp
[Theory, InlineData("  Ana@Correo.COM ", "ana@correo.com")]
public void TryNormalizeTrimsAndLowercases(string raw, string expected) { Assert.True(BuyerEmail.TryNormalize(raw, out var n)); Assert.Equal(expected, n); }
[Theory, InlineData(""), InlineData("   "), InlineData("sin-arroba")] public void TryNormalizeRejectsMalformed(string raw) => Assert.False(BuyerEmail.TryNormalize(raw, out _));

[Fact] public void ReplacePendingRegistrationThrowsOnAVerifiedAccount()           // MarkEmailVerified, luego Replace → InvalidOperationException
[Fact] public void ReplacePendingRegistrationSwapsTheHashAndMovesUpdatedAt()
[Fact] public void MarkEmailVerifiedKeepsTheFirstTimestamp()

[Fact] public void ASessionIsValidOnlyForItsOwnHost()      // IsValidFor("a.lulocrm.com") true, IsValidFor("b.lulocrm.com") false
[Fact] public void ASessionExpiresAfterThirtyDaysEvenIfUsed()
[Fact] public void ASessionExpiresAfterSevenIdleDays()
[Fact] public void TouchOnlyWritesAfterFiveMinutes()        // Touch(now+4min) false, Touch(now+6min) true
[Fact] public void ARevokedSessionIsNotValid()

[Fact] public void VerifyTokensLastTwentyFourHoursAndResetTokensOneHour()
[Fact] public void TheConsentUrlIsBuiltFromTheHost()        // Record(..., "acme.lulocrm.com", ...).PolicyUrl == "https://acme.lulocrm.com/cuenta/politica", PolicyVersion == "2026-11-07"
```

- [ ] **Step 2: RED** — `dotnet test tests/Modules/Buyers/Modules.Buyers.UnitTests` → no compila (tipos inexistentes).
- [ ] **Step 3: Implementar** las firmas de **Interfaces**. `BuyerSession.Issue` trunca la IP a 45; `ExpiresAt = now + AbsoluteLifetime`.
- [ ] **Step 4: GREEN** — mismo comando → PASS.
- [ ] **Step 5: Commit** — `git add src/Modules/Buyers/Modules.Buyers.Domain tests/Modules/Buyers/Modules.Buyers.UnitTests` · `git commit -m "feat(buyers): add the buyer account, profile, session, token and consent domain"`

---

### Task 3: Persistencia, hasher y outbox

**Files:**
- Create: `src/Modules/Buyers/Modules.Buyers.Application/Ports.cs`, `BuyerTokenCodec.cs`
- Create: `src/Modules/Buyers/Modules.Buyers.Infrastructure/Persistence/{BuyersDbContext,BuyersDbContextFactory,BuyerAccountRepository,BuyerProfileRepository,BuyerSessionRepository,BuyerEmailTokenRepository,BuyerConsentRepository,BuyersUnitOfWork,UniqueViolation,BuyerOutboxWriter,OutboxRecord}.cs`, `Persistence/Migrations/<ts>_InitialBuyers.cs`
- Create: `…Infrastructure/{AspNetCoreBuyerPasswordHasher,BuyersInfrastructureExtensions,BuyersDatabaseInitializer}.cs`
- Modify: `src/Api/Program.cs` (`await app.Services.InitializeBuyersDatabaseAsync(app.Lifetime.ApplicationStopping);` junto a Store), `src/Bootstrapper/QepServiceCollectionExtensions.cs` (`services.AddBuyersInfrastructure(configuration)`), `Api.csproj`/`Bootstrapper.csproj` (referencias)
- Test: `tests/Modules/Buyers/Modules.Buyers.IntegrationTests/{BuyersApiHarness,BuyerPersistenceTests,BuyerPasswordHasherTests}.cs`

**Interfaces:**
- Consumes: dominio de Task 2.
- Produces (`Ports.cs`):
  - `IBuyerAccountRepository`: `Task<BuyerAccount?> FindByEmailAsync(string normalizedEmail, CancellationToken)`, `Task<BuyerAccount?> FindAsync(Guid id, CancellationToken)`, `void Add(BuyerAccount)`, `void Remove(BuyerAccount)`.
  - `IBuyerProfileRepository`: `Task<BuyerProfile?> FindAsync(Guid accountId, CancellationToken)`, `void Add(BuyerProfile)`, `void Remove(BuyerProfile)`.
  - `IBuyerSessionRepository`: `void Add(BuyerSession)`, `Task<BuyerSession?> FindByTokenHashAsync(string hash, CancellationToken)`, `Task<IReadOnlyList<BuyerSession>> ListActiveAsync(Guid accountId, CancellationToken)`.
  - `IBuyerEmailTokenRepository`: `void Add(BuyerEmailToken)`, `Task InvalidateUnusedAsync(Guid accountId, BuyerEmailTokenPurpose, DateTimeOffset now, CancellationToken)`, `Task<Guid?> TryConsumeAsync(string tokenHash, BuyerEmailTokenPurpose, DateTimeOffset now, CancellationToken)` — una sentencia `UPDATE … SET used_at = @now WHERE token_hash = @h AND purpose = @p AND used_at IS NULL AND expires_at > @now RETURNING account_id`, que commitea sola (precedente D6 de Identity).
  - `IBuyerConsentRepository`: `void Add(BuyerConsent)`.
  - `IBuyerOutbox`: `void Add(string eventName, object payload, DateTimeOffset occurredAt)` (correlación: `Activity.Current?.TraceId` o un GUID).
  - `IBuyersUnitOfWork`: `Task SaveChangesAsync(CancellationToken)`, `void DiscardPendingChanges()`; traduce la violación de `IX_accounts_email` a `ResourceConflictException(BuyerErrorCodes.EmailTaken, …)` **por nombre de índice**.
  - `BuyerPasswordCheck { Failed, Success, SuccessRehashNeeded }`; `IBuyerPasswordHasher`: `string Hash(string password)`, `BuyerPasswordCheck Verify(string? hash, string password)` (null → hash ficticio, `Failed`).
  - `BuyerTokenCodec`: `static string Generate()`, `static string Hash(string raw)` (misma receta que `EmailTokenCodec`).
- Produces (esquema `buyers`):
  - `accounts` (`id` PK, `email varchar(254)` con `IX_accounts_email` único, `password_hash text`, `email_verified_at`, `created_at`, `updated_at`).
  - `profiles` (`account_id` PK y FK → `accounts` `ON DELETE CASCADE`, `data jsonb`, `updated_at`).
  - `sessions` (`id`, `account_id` FK cascade, `store_host varchar(253)`, `token_hash char(64)` con `IX_sessions_token_hash` único, `created_at`, `last_seen_at`, `expires_at`, `revoked_at`, `revoked_reason varchar(40)`, `ip_address varchar(45)`).
  - `email_tokens` (`id`, `account_id` FK cascade, `purpose varchar(16)` CHECK `('Verify','Reset')`, `token_hash char(64)` con `IX_email_tokens_token_hash` único, `created_at`, `expires_at`, `used_at`).
  - `consents` (`id`, `account_id uuid` **sin FK**, `email`, `accepted_at`, `visitor_ip varchar(45)`, `store_host varchar(253)`, `policy_url varchar(300)`, `policy_version varchar(32)`), índice `IX_consents_account_id`.
  - Proyección `platform.outbox_messages` con `ExcludeFromMigrations` (copia de `IdentityDbContext.ConfigureOutboxProjection`).
- Produces (test): `BuyersApiHarness.OpenAsync(Action<IServiceCollection>? configure = null) -> BuyersApi` con `Factory`, `Storefront` (cliente con `X-Qep-Storefront-Key`), `FakeBuyerStorefronts`, `FakeBuyerCaptcha`, `Task<IReadOnlyList<JsonNode>> OutboxPayloadsAsync(string eventName)`, `Task<T> ScalarAsync<T>(string sql, params object[] args)`, `T Scoped<T>(Func<IServiceProvider, T>)`. La fábrica fija las mismas claves que `StoreApiHarness.QepApiFactory` (incluido `Seed:Enabled=false`, `Notifications:EmailProvider=log`, `Store:Storefront:SharedSecret`).

- [ ] **Step 1: Escribir las pruebas**

`BuyerPersistenceTests`:
- `TheMigrationCreatesTheBuyersSchema` — las cinco tablas existen en `information_schema.tables` con `table_schema = 'buyers'`.
- `ADuplicateEmailIsTranslatedByIndexName` — dos `Register` con el mismo email en contextos separados; el segundo `SaveChangesAsync` lanza `ResourceConflictException` con `Code == "store.buyer.email_taken"`.
- `ConsumingATokenTwiceSucceedsOnce` — dos `TryConsumeAsync` en paralelo sobre el mismo hash: exactamente uno devuelve el id.
- `AnExpiredTokenIsNotConsumed`.
- `DeletingAnAccountCascadesToProfileSessionsAndTokensButNotConsents` — conteos 0, 0, 0 y consentimiento 1.
- `TheProfileRoundTripsAsJson` — guarda `BuyerProfileData` con envío y lee igual (`Assert.Equal`).
- `TheOutboxRowIsWrittenWithTheCaller` — `IBuyerOutbox.Add` + `SaveChangesAsync` deja una fila con `event_name` y `attempts = 0`.

`BuyerPasswordHasherTests`: `HashThenVerifySucceeds`, `AWrongPasswordFails`, `ANullHashFailsAfterVerifyingTheDummy`.

- [ ] **Step 2: RED** — `$env:Seed__Enabled = "false"` y `dotnet test tests/Modules/Buyers/Modules.Buyers.IntegrationTests --filter "FullyQualifiedName~BuyerPersistenceTests|FullyQualifiedName~BuyerPasswordHasherTests" -- xUnit.MaxParallelThreads=4` → no compila.
- [ ] **Step 3: Implementar** contexto, mapeos (enums como texto, `jsonb` para `Data`), repositorios, unidad de trabajo, hasher (`PasswordHasher<BuyerAccount>`, dummy `Lazy<string>` como `AspNetCorePasswordHasher`), writer; registrar en `AddBuyersInfrastructure` (hasher singleton, el resto scoped); generar `InitialBuyers` con el comando de **Antes de empezar**; `dotnet restore --force-evaluate`.
- [ ] **Step 4: GREEN** — mismo comando → PASS. Luego `dotnet test tests/ArchitectureTests/ArchitectureTests --filter FullyQualifiedName~BuyersLayerTests` → PASS.
- [ ] **Step 5: Commit** — `git add src tests/Modules/Buyers` (incluye locks) · `git commit -m "feat(buyers): persist accounts, profiles, sessions, tokens and consents"`

---

### Task 4: Reglas de contraseña y perfil, con paridad

**Files:**
- Create: `src/Modules/Buyers/Modules.Buyers.Application/{BuyerPasswordRules,BuyerProfileValidator,BuyerDtos}.cs`
- Test: `tests/Modules/Buyers/Modules.Buyers.UnitTests/{BuyerPasswordRulesTests,BuyerProfileValidatorTests}.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/BuyerParityTests.cs`
- Modify: `tests/Bootstrapper/Bootstrapper.UnitTests/*.csproj` (referencia a `Modules.Buyers.Application`)

**Interfaces:**
- Produces:
  - `BuyerPasswordRules`: `const int MinLength = 10`, `MaxLength = 128`; `static IRuleBuilderOptions<T,string> BuyerPassword<T>(this IRuleBuilder<T,string>)` (una sola regla `Must`, código `store.buyer.password_invalid`, mensaje `"La contraseña debe tener entre 10 y 128 caracteres."`).
  - `BuyerBillingDto`, `BuyerShippingDto`, `BuyerProfileDto(BuyerBillingDto Billing, BuyerShippingDto? Shipping, bool ShippingSameAsBilling)` con los campos del contrato; `BuyerDto(string Email, BuyerProfileDto? Profile)`; `BuyerSessionDto(string SessionToken, DateTimeOffset ExpiresAt, BuyerDto Buyer)`; `static BuyerProfileData ToData(this BuyerProfileDto)` y `static BuyerProfileDto ToDto(this BuyerProfileData)`.
  - `BuyerProfileValidator : AbstractValidator<BuyerProfileDto>` — las mismas reglas que `PublicOrderRequestBodyValidator.BillingValidator/ShippingValidator` (tipos `NIT|CC|CE|PASAPORTE`, largos de `StoreRequestBilling`/`StoreRequestShipping`/`StorePlace` copiados como constantes, teléfono ≤ 32, dirección ≤ 200, país de 2 letras, ciudad obligatoria, ids de Colombia juntos) más `Shipping` nulo ⇔ `ShippingSameAsBilling`.

- [ ] **Step 1: Escribir las pruebas**

- `BuyerPasswordRulesTests`: `[Theory] 9 → inválida, 10 → válida, 128 → válida, 129 → inválida, "          " (10 espacios) → válida` (NIST, igual que Identity).
- `BuyerProfileValidatorTests`: `AValidColombianProfilePasses`, `ShippingSameAsBillingWithAShippingBlockFails` (error en `Shipping`), `NoShippingWithShippingSameAsBillingFalseFails`, `AnUnknownIdentificationTypeFails` (`Billing.IdentificationType`), `ColombiaWithOnlyTheCityIdFails`.
- `BuyerParityTests` (ve los tres módulos):
  - `ThePasswordLimitsMatchIdentity` — `BuyerPasswordRules.MinLength == PasswordRules.MinLength` y `MaxLength`.
  - `TheProfileRulesMatchTheOrderRules` — para una tabla de ocho bloques (válido CO, válido ES con `cityName`, sin ciudad, tipo `XX`, CO sin `departmentId`, nombre de 201, email inválido, país `COL`) el resultado de `BuyerProfileValidator` sobre el perfil es igual a la validez de `PublicOrderRequestBodyValidator` sobre un pedido mínimo con ese mismo bloque (filtrando errores que empiezan con `Billing`).

- [ ] **Step 2: RED** — `dotnet test tests/Modules/Buyers/Modules.Buyers.UnitTests --filter "FullyQualifiedName~BuyerPasswordRulesTests|FullyQualifiedName~BuyerProfileValidatorTests"` y `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter FullyQualifiedName~BuyerParityTests` → no compilan.
- [ ] **Step 3: Implementar** las firmas.
- [ ] **Step 4: GREEN** — los dos comandos → PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(buyers): add password and profile rules with parity against identity and store"`

---

### Task 5: Superficie HTTP y registro

**Files:**
- Create: `src/Modules/Buyers/Modules.Buyers.Application/{BuyerSignup,RegisterBuyer,BuyerIntegrationEvents}.cs`
- Create: `src/Modules/Buyers/Modules.Buyers.Api/PublicBuyerEndpoints.cs`
- Create: `src/Bootstrapper/{BuyerStorefronts,BuyerCaptcha}.cs`
- Modify: `src/Modules/Store/Modules.Store.Api/StorefrontClient.cs`, `src/Api/RateLimiterPolicies.cs`, `src/Api/Program.cs`, `src/Bootstrapper/QepServiceCollectionExtensions.cs`, `src/Api/Api.csproj`, `src/Bootstrapper/Bootstrapper.csproj`
- Test: `tests/Modules/Buyers/Modules.Buyers.UnitTests/{BuyerSignupTests,BuyersTestDoubles}.cs`, `tests/Modules/Buyers/Modules.Buyers.IntegrationTests/{BuyerRegistrationApiTests,BuyerSignupRaceTests}.cs`, `tests/Modules/Store/Modules.Store.UnitTests/StorefrontClientTests.cs`, `tests/Modules/Store/Modules.Store.IntegrationTests/BuyerStorefrontAdapterApiTests.cs`

**Interfaces:**
- Consumes: Tasks 2–4.
- Produces:
  - Puertos (en `Ports.cs`): `BuyerStorefront(string Host, string Name)`; `IBuyerStorefronts.ResolveAsync(string host, CancellationToken) -> Task<BuyerStorefront>` (lanza el 404 `store.public.not_found` tal cual); `IBuyerCaptcha.VerifyAsync(string token, string visitorIp, string host, CancellationToken) -> Task<bool>` (deja pasar el 503 `store.captcha.unavailable`).
  - `BuyerIntegrationEvents`: `EmailVerificationRequested = "buyers.email-verification-requested.v1"`, `ExistingAccountNotice = "buyers.existing-account-notice.v1"`, `PasswordResetRequested = "buyers.password-reset-requested.v1"`, `AccountDeleted = "buyers.account-deleted.v1"`; `BuyerEmailPayload(Guid AccountId, string Email, string StoreHost, string StoreName, string? Token)`.
  - `BuyerSignupRequest(string Host, string StoreName, string VisitorIp, string Email, string PasswordHash, BuyerProfileDto? Profile)`; `BuyerSignup.SignUpAsync(BuyerSignupRequest, CancellationToken) -> Task` (recibe el hash ya calculado: el registro lo calcula en su manejador **siempre** antes de mirar el email, y el checkout lo trae en el mensaje; `Email` ya validado; nuevo → cuenta + perfil + consentimiento + token Verify + outbox Verify; verificado → sólo outbox ExistingAccountNotice; sin verificar → `ReplacePendingRegistration`, perfil reemplazado o borrado si viene `null`, consentimiento nuevo, `InvalidateUnusedAsync(Verify)`, token nuevo, outbox Verify; `ResourceConflictException(EmailTaken)` → `DiscardPendingChanges` y reintenta **una vez** por el camino de email existente).
  - `RegisterBuyerBody(string Email, string Password, bool AcceptsDataPolicy, string TurnstileToken, BuyerProfileDto? Profile)` + `RegisterBuyerBodyValidator` (email `NotEmpty`, `EmailAddress`, ≤ 254; `BuyerPassword()`; token `NotEmpty`, ≤ 2048; perfil con `BuyerProfileValidator` si viene); `RegisterBuyerCommand(string Host, string VisitorIp, RegisterBuyerBody Body) : ICommand<bool>` + handler (orden del contrato).
  - `StorefrontClient.TryGetDeclaredVisitorIp(HttpContext context, out string visitorIp)` — lee el secreto de `IOptions<StoreOptions>` y delega en `TryGetVisitorIp`.
  - `StorefrontClient.BuyerSessionHeader = "X-Qep-Buyer-Session"`: **la única** declaración del header; la usan Buyers.Api (que ya referencia Store.Api) y el endpoint del pedido (Task 12).
  - `RateLimiterPolicies.StorefrontCredentials = "storefront-credentials"`: ventana fija 10/min, partición `$"{RoutePattern.RawText}|{StorefrontClient.VisitorIp(...)}"`.
  - `PublicBuyerEndpoints.MapPublicBuyerEndpoints(this IEndpointRouteBuilder, string readPolicy, string credentialsPolicy)`: grupo `/api/v1/public/stores/{host}/buyer`, `AllowAnonymous`, `StorefrontKeyFilter`, `RequestSizeLimitAttribute(65_536)`, `RequireRateLimiting(readPolicy)`, y un filtro de grupo que pone `Cache-Control: no-store` en toda respuesta. Esta task mapea `POST /register` (con `credentialsPolicy`) → `202`.
  - Program: `app.MapPublicBuyerEndpoints(RateLimiterPolicies.StorefrontRead, RateLimiterPolicies.StorefrontCredentials);`
  - Bootstrapper: `BuyerStorefronts` (con `StorefrontResolver`: `Host` = normalizado, `Name = store.Name ?? tenant.DisplayName`), `BuyerCaptcha` (con `ICaptchaVerifier`), manejadores registrados a mano, `AddValidatorsFromAssemblyContaining<RegisterBuyerBodyValidator>()`, `BuyerSignup` scoped.
- Produces (test): `FakeBuyerStorefronts` (todo host → `BuyerStorefront(host.ToLowerInvariant(), "Tienda Demo")`, salvo `"no-existe.lulocrm.com"` → 404), `FakeBuyerCaptcha { bool Passes; int Calls }`; en `BuyersApi`: `Task<HttpResponseMessage> PostAsync(string path, object body, string host = "acme.lulocrm.com", string? session = null, string? visitorIp = "203.0.113.10")`, `Task<string> RegisterVerifiedAsync(string email, string password)` (registra y verifica con el token del outbox; devuelve el email normalizado).

- [ ] **Step 1: Escribir las pruebas**

`StorefrontClientTests.TryGetDeclaredVisitorIpUsesTheConfiguredSecret` — con la clave y `X-Qep-Visitor-Ip: 203.0.113.7` → `true`, `"203.0.113.7"`; sin clave → `false`.

`BuyerSignupTests` (dobles en memoria):
- `ANewEmailCreatesAnUnverifiedAccountWithConsentAndQueuesVerification` — una cuenta con `EmailVerifiedAt == null`, un consentimiento con `PolicyUrl == "https://acme.lulocrm.com/cuenta/politica"` y `VisitorIp`, un token `Verify`, un outbox `buyers.email-verification-requested.v1` cuyo payload tiene `StoreName == "Tienda Demo"` y `Token` que hashea al guardado.
- `AVerifiedEmailOnlyQueuesTheExistingAccountNotice` — hash, perfil y consentimientos sin cambios; un outbox `ExistingAccountNotice` sin token.
- `AnUnverifiedEmailReplacesPasswordAndProfileAndReissuesVerification` — hash nuevo, perfil nuevo, tokens previos invalidados, dos consentimientos.
- `RegisterHashesThePasswordOnEveryPath` (sobre `RegisterBuyerHandler`) — el hasher doble cuenta 1 llamada en cada uno de los tres caminos.

`BuyerSignupRaceTests.TwoConcurrentSignupsWithTheSameNewEmailCreateOneAccount` (integración, Review Focus 1) — dos `SignUpAsync` en paralelo en scopes distintos: ninguno lanza, `count(*) = 1` en `buyers.accounts`, un outbox Verify y un ExistingAccountNotice.

`BuyerRegistrationApiTests`:
- `RegisterReturns202AndNoStore` — `202`, `Cache-Control: no-store`, una cuenta.
- `RegisterWithAnExistingVerifiedEmailReturnsTheSame202` — mismo status y cuerpo vacío que el caso nuevo.
- `RegisterWithAnUppercasedExistingEmailSendsTheExistingAccountNotice` (Review Focus 2).
- `RegisterWithoutConsentIs422ConsentRequired` — `code == "store.buyer.consent_required"`, sin cuenta, `Captcha.Calls == 0`.
- `AFailedCaptchaIs422AndWritesNothing`.
- `AShortPasswordIs422WithErrorsPassword`.
- `RegisterWithoutVisitorIpIs422`.
- `AnUnknownHostIs404StorePublicNotFound`.
- `WithoutTheStorefrontKeyIs401`.
- `TheEleventhRegisterInAMinuteFromOneVisitorIs429ButAnotherVisitorPasses`.

`BuyerStorefrontAdapterApiTests` (Store.IntegrationTests, con `PublicStoreFixture` real e `IBuyerCaptcha` falso): `RegisterOnAPublishedStoreQueuesAnEmailNamingTheStore` (payload `storeName` = nombre de la tienda) y `RegisterOnAnUnpublishedStoreIs404`.

- [ ] **Step 2: RED** — `dotnet test tests/Modules/Buyers/Modules.Buyers.UnitTests --filter FullyQualifiedName~BuyerSignupTests`; `dotnet test tests/Modules/Store/Modules.Store.UnitTests --filter FullyQualifiedName~StorefrontClientTests` → no compilan.
- [ ] **Step 3: Implementar** lo de **Interfaces**.
- [ ] **Step 4: GREEN** — los dos de arriba; luego `$env:Seed__Enabled = "false"`, `dotnet test tests/Modules/Buyers/Modules.Buyers.IntegrationTests --filter "FullyQualifiedName~BuyerRegistrationApiTests|FullyQualifiedName~BuyerSignupRaceTests" -- xUnit.MaxParallelThreads=4` y `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~BuyerStorefrontAdapterApiTests|FullyQualifiedName~PublicOrderRequestApiTests" -- xUnit.MaxParallelThreads=4` → PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(buyers): register buyer accounts from the storefront"`

---

### Task 6: Verificar y reenviar la verificación

**Files:**
- Create: `src/Modules/Buyers/Modules.Buyers.Application/VerifyBuyerEmail.cs`
- Modify: `PublicBuyerEndpoints.cs`, `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Buyers/Modules.Buyers.IntegrationTests/BuyerVerificationApiTests.cs`

**Interfaces:**
- Produces: `VerifyBuyerEmailBody(string Token)` (+ validador `NotEmpty`, ≤ 128); `VerifyBuyerEmailCommand(string Host, VerifyBuyerEmailBody Body) : ICommand<bool>` (resuelve el host; `TryConsumeAsync(Verify)` → `null` lanza `BuyerDomainException(TokenInvalid)`; `MarkEmailVerified`). `ResendBuyerVerificationBody(string Email, string TurnstileToken)`; `ResendBuyerVerificationCommand(string Host, string VisitorIp, ResendBuyerVerificationBody Body) : ICommand<bool>` (captcha; sólo cuenta existente sin verificar → token nuevo + outbox Verify). Rutas `POST /email/verify` → `204`, `POST /email/resend` → `202`, las dos con `credentialsPolicy`.

- [ ] **Step 1: Escribir las pruebas** — `VerifyMarksTheAccountVerified` (`email_verified_at` no nulo), `AReusedTokenIs422TokenInvalid`, `AnExpiredTokenIs422TokenInvalid` (corre `expires_at` con SQL), `AResetTokenDoesNotVerify`, `ResendIssuesANewTokenAndInvalidatesTheOld` (el primer token da 422 después), `ResendForAnUnknownEmailIs202AndQueuesNothing`, `ResendForAVerifiedAccountIs202AndQueuesNothing`, `ResendWithAFailedCaptchaIs422`.
- [ ] **Step 2: RED** — `dotnet test tests/Modules/Buyers/Modules.Buyers.IntegrationTests --filter FullyQualifiedName~BuyerVerificationApiTests -- xUnit.MaxParallelThreads=4` → FAIL (404 de la ruta / no compila).
- [ ] **Step 3: Implementar.**
- [ ] **Step 4: GREEN** — mismo comando → PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(buyers): verify buyer emails and resend the verification"`

---

### Task 7: Sesión: login, leer y cerrar

**Files:**
- Create: `src/Modules/Buyers/Modules.Buyers.Application/{BuyerSessions,LoginBuyer,BuyerSessionQueries}.cs`
- Modify: `PublicBuyerEndpoints.cs`, `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Buyers/Modules.Buyers.UnitTests/BuyerSessionsTests.cs`, `tests/Modules/Buyers/Modules.Buyers.IntegrationTests/BuyerSessionApiTests.cs`

**Interfaces:**
- Produces:
  - `BuyerSessions`: `Task<(string Token, DateTimeOffset ExpiresAt)> IssueAsync(Guid accountId, string host, string? ipAddress, CancellationToken)` (guarda); `Task<BuyerSession?> TryResolveAsync(string host, string? rawToken, CancellationToken)` (token nulo, vacío o > 128 → `null` sin consultar; hash; `IsValidFor`; `Touch` y guarda sólo si tocó); `Task<BuyerSession> RequireAsync(string host, string? rawToken, CancellationToken)` (→ `RequestUnauthorizedException(SessionInvalid)`); `Task RevokeOthersAsync(Guid accountId, Guid keepSessionId, string reason, CancellationToken)`; `Task RevokeAllAsync(Guid accountId, string reason, CancellationToken)`. Motivos: `"logout"`, `"password_changed"`, `"password_reset"`.
  - `LoginBuyerBody(string Email, string Password)` (validador: sólo no vacíos, como `PasswordLoginValidator`); `LoginBuyerCommand(string Host, string? VisitorIp, LoginBuyerBody Body) : ICommand<BuyerSessionDto>` (email malformado = inexistente; **siempre** `Verify`, con hash nulo si no hay cuenta; falla → `RequestUnauthorizedException(CredentialsInvalid)`; correcta y sin verificar → `RequestForbiddenException(EmailNotVerified)`; `SuccessRehashNeeded` → `ChangePasswordHash`).
  - `GetBuyerSessionQuery(string Host, string? Token) : IQuery<BuyerDto>`; `LogoutBuyerCommand(string Host, string? Token) : ICommand<bool>` (revoca sólo si `TryResolveAsync` la encuentra en ese host).
  - Rutas: `POST /session` (credenciales; IP opcional vía `TryGetDeclaredVisitorIp`), `GET /session`, `DELETE /session` (lectura); el token sale de `StorefrontClient.BuyerSessionHeader`.

- [ ] **Step 1: Escribir las pruebas**

`BuyerSessionsTests`: `AnOversizedTokenIsRejectedWithoutALookup` (repo doble con 0 llamadas), `ResolveTouchesOnlyAfterFiveMinutes` (0 guardados a +4 min, 1 a +6 min).

`BuyerSessionApiTests`:
- `LoginReturnsATokenThatReadsTheBuyer` — `200`, `sessionToken` de 43 caracteres, `expiresAt` ≈ ahora + 30 d, `buyer.email`; `GET /session` con el header → `200` mismo email; en base sólo el hash (`token_hash <> sessionToken`).
- `LoginNormalizesEmailCaseAndWhitespace` (Review Focus 2).
- `AWrongPasswordAndAnUnknownEmailGiveTheSame401` — mismo status, `code` y `title`.
- `AnUnverifiedAccountWithTheRightPasswordIs403AndQueuesNothing`; `…WithTheWrongPasswordIs401`.
- `ASessionIsRejectedOnAnotherHost` (Review Focus 3) — emitida en `acme.lulocrm.com`, `GET` en `otra.lulocrm.com` → `401 store.buyer.session_invalid`.
- `LogoutRevokesAndIsIdempotent` — `204`, después `GET` → `401`, segundo `DELETE` → `204`.
- `LogoutOnAnotherHostDoesNotRevoke`.
- `AnIdleSessionIs401` (`last_seen_at` −8 días por SQL).
- `EveryBuyerResponseIsNoStore` — `POST /session` y `GET /session`.

- [ ] **Step 2: RED** — `dotnet test tests/Modules/Buyers/Modules.Buyers.UnitTests --filter FullyQualifiedName~BuyerSessionsTests`; integración con `--filter FullyQualifiedName~BuyerSessionApiTests -- xUnit.MaxParallelThreads=4` → FAIL.
- [ ] **Step 3: Implementar.**
- [ ] **Step 4: GREEN** — los dos → PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(buyers): sign buyers in per store host"`

---

### Task 8: Perfil y cambio de contraseña

**Files:**
- Create: `src/Modules/Buyers/Modules.Buyers.Application/{UpdateBuyerProfile,ChangeBuyerPassword,BuyerProfileWriter}.cs`
- Modify: `PublicBuyerEndpoints.cs`, `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Buyers/Modules.Buyers.IntegrationTests/BuyerAccountApiTests.cs`

**Interfaces:**
- Produces:
  - `BuyerProfileWriter.ReplaceAsync(Guid accountId, BuyerProfileData data, CancellationToken)` (crea o reemplaza y guarda); `BuyerProfileWriter.ReplaceFromSessionAsync(string host, string? rawToken, BuyerProfileDto profile, CancellationToken) -> Task<bool>` (`false` si la sesión no sirve; lo usa la Task 12).
  - `UpdateBuyerProfileCommand(string Host, string? Token, BuyerProfileDto Body) : ICommand<BuyerDto>` (sesión → validador → `ReplaceAsync`).
  - `ChangeBuyerPasswordBody(string CurrentPassword, string NewPassword)` (validador: actual no vacía, nueva `BuyerPassword()`); `ChangeBuyerPasswordCommand(string Host, string? Token, ChangeBuyerPasswordBody Body) : ICommand<bool>` (sesión → validador → `Verify` actual; falla → `BuyerPasswordIncorrectException("CurrentPassword")` → `ChangePasswordHash` → `RevokeOthersAsync`).
  - `BuyerPasswordIncorrectException(string field) : DomainException, IHasFieldErrors` con `Code = PasswordIncorrect` y `FieldErrors = { [field] = ["La contraseña no es correcta."] }`.
  - Rutas `PUT /profile` (lectura) → `200 BuyerDto`; `POST /password` (credenciales) → `204`.

- [ ] **Step 1: Escribir las pruebas** — `UpdateProfileReturnsTheBuyerWithTheProfile`, `UpdateProfileDoesNotChangeTheLoginEmail` (billing.email distinto; `buyer.email` igual al de login), `AnInvalidProfileIs422WithFieldErrors` (`errors["Billing.Name"]`), `UpdateProfileWithoutSessionIs401`, `ChangePasswordKeepsTheCurrentSessionAndRevokesTheOthers` (dos logins; tras cambiar, la sesión A `GET` 200 y la B 401; login con la vieja → 401, con la nueva → 200), `AWrongCurrentPasswordIs422WithErrorsCurrentPasswordAndKeepsTheSession`, `AShortNewPasswordIs422WithErrorsNewPassword`.
- [ ] **Step 2: RED** — `dotnet test tests/Modules/Buyers/Modules.Buyers.IntegrationTests --filter FullyQualifiedName~BuyerAccountApiTests -- xUnit.MaxParallelThreads=4` → FAIL.
- [ ] **Step 3: Implementar.**
- [ ] **Step 4: GREEN** — mismo comando → PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(buyers): edit the saved profile and change the password"`

---

### Task 9: Olvidé mi contraseña y restablecer

**Files:**
- Create: `src/Modules/Buyers/Modules.Buyers.Application/BuyerPasswordRecovery.cs`
- Modify: `PublicBuyerEndpoints.cs`, `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Buyers/Modules.Buyers.IntegrationTests/BuyerPasswordRecoveryApiTests.cs`

**Interfaces:**
- Produces: `ForgotBuyerPasswordBody(string Email, string TurnstileToken)`; `ForgotBuyerPasswordCommand(string Host, string VisitorIp, ForgotBuyerPasswordBody Body) : ICommand<bool>` (captcha; cuenta existente, verificada o no → `InvalidateUnusedAsync(Reset)`, token Reset, outbox `PasswordResetRequested`). `ResetBuyerPasswordBody(string Token, string NewPassword)` (validador antes de consumir: un 422 de contraseña no quema el token); `ResetBuyerPasswordCommand(string Host, ResetBuyerPasswordBody Body) : ICommand<bool>` (`TryConsumeAsync(Reset)` → `ChangePasswordHash` → `MarkEmailVerified` → `RevokeAllAsync("password_reset")`). Rutas `POST /password/forgot` → `202`, `POST /password/reset` → `204`, ambas con `credentialsPolicy`.

- [ ] **Step 1: Escribir las pruebas** — `ForgotForAnUnknownEmailIs202AndQueuesNothing`, `ForgotQueuesAResetEmailForTheHost` (payload `storeHost == "acme.lulocrm.com"`), `ResetChangesThePasswordAndRevokesEverySession`, `ResetVerifiesAnUnverifiedAccount` (login después → 200), `AShortPasswordDoesNotBurnTheToken` (422, después el mismo token con contraseña válida → 204), `AReusedResetTokenIs422`, `AResetTokenOlderThanOneHourIs422`, `ForgotWithAFailedCaptchaIs422`.
- [ ] **Step 2: RED** — `dotnet test tests/Modules/Buyers/Modules.Buyers.IntegrationTests --filter FullyQualifiedName~BuyerPasswordRecoveryApiTests -- xUnit.MaxParallelThreads=4` → FAIL.
- [ ] **Step 3: Implementar.**
- [ ] **Step 4: GREEN** → PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(buyers): recover a forgotten buyer password"`

---

### Task 10: Eliminar la cuenta

**Files:**
- Create: `src/Modules/Buyers/Modules.Buyers.Application/DeleteBuyerAccount.cs`
- Modify: `PublicBuyerEndpoints.cs`, `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Buyers/Modules.Buyers.IntegrationTests/BuyerAccountDeletionApiTests.cs`

**Interfaces:**
- Produces: `DeleteBuyerAccountBody(string Password)`; `DeleteBuyerAccountCommand(string Host, string? Token, DeleteBuyerAccountBody Body) : ICommand<bool>` (sesión → `Verify`; falla → `BuyerPasswordIncorrectException("Password")`; en un solo `SaveChanges`: `Remove(account)` (cascada) + outbox `AccountDeleted` con el email). Ruta `POST /account/delete` (credenciales) → `204`.

- [ ] **Step 1: Escribir las pruebas** — `DeleteRemovesAccountProfileSessionsAndTokens` (conteos 0), `DeleteKeepsTheConsentRecord` (fila con email, IP, host, URL y versión), `DeleteQueuesTheAccountDeletedEmail` (payload con el email), `TheSessionIsInvalidAfterDelete` (`GET` → 401), `TheEmailCanRegisterAgainAfterDelete` (`202` y cuenta nueva sin verificar), `AWrongPasswordIs422WithErrorsPasswordAndDeletesNothing`.
- [ ] **Step 2: RED** — `--filter FullyQualifiedName~BuyerAccountDeletionApiTests` → FAIL.
- [ ] **Step 3: Implementar.**
- [ ] **Step 4: GREEN** → PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(buyers): delete a buyer account on request"`

---

### Task 11: Los cuatro correos

**Files:**
- Create: `src/Modules/Notifications/Modules.Notifications.Application/{BuyerAccountLinks,BuyerVerificationEmailTemplate,BuyerExistingAccountEmailTemplate,BuyerPasswordResetEmailTemplate,BuyerAccountDeletedEmailTemplate}.cs`
- Create: `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/{BuyerEmailPayloadReader,BuyerVerificationDeliveryWorker,BuyerExistingAccountDeliveryWorker,BuyerPasswordResetDeliveryWorker,BuyerAccountDeletedDeliveryWorker}.cs`
- Modify: `NotificationsInfrastructureExtensions.cs` (cuatro `AddHostedService`), `Modules.Notifications.Infrastructure.csproj` (referencia a `Modules.Buyers.Application`)
- Test: `tests/Modules/Notifications/Modules.Notifications.UnitTests/BuyerEmailTemplatesTests.cs`, `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/BuyerEmailDeliveryTests.cs`

**Interfaces:**
- Consumes: `BuyerIntegrationEvents`, `BuyerEmailPayload` (Task 5).
- Produces:
  - `BuyerAccountLinks`: `Verify(string host, string token)` → `https://{host}/cuenta/verificar?token={escapado}`; `Reset(host, token)` → `…/cuenta/restablecer?token=…`; `Login(host)` → `…/cuenta/ingresar`; `Forgot(host)` → `…/cuenta/olvide`.
  - Plantillas `static EmailMessage Render(string recipient, string storeName, …)`, con `TemplateRef` y asunto fijos:
    - `buyers.verification.v1` — "Confirma tu cuenta para comprar en {tienda}"; enlace; "El enlace vence en 24 horas."; "Si no creaste una cuenta, puedes ignorar este mensaje."
    - `buyers.existing-account.v1` — "Ya tienes una cuenta para comprar en {tienda}"; "La próxima vez inicia sesión con tu correo y tu contraseña."; enlaces a ingresar y a olvidé.
    - `buyers.password-reset.v1` — "Restablece tu contraseña"; nombra la tienda; enlace; "El enlace vence en 1 hora."; "Si no lo pediste, puedes ignorar este mensaje."
    - `buyers.account-deleted.v1` — "Eliminamos tu cuenta"; "Borramos tu cuenta, tus datos guardados y tus sesiones. Los pedidos que ya hiciste en cada tienda no cambian."
    - El nombre de la tienda va con `WebUtility.HtmlEncode` en `HtmlBody`; asunto y texto plano sin codificar.
  - Workers (`OutboxDeliveryWorker`, consumidores `notifications.buyer-verification-email`, `…buyer-existing-account-email`, `…buyer-password-reset-email`, `…buyer-account-deleted-email`): leen el payload con `BuyerEmailPayloadReader.Parse`, `Notification.CreateEmail(null, payload.AccountId, payload.Email, TemplateRef, now)`; sin email → `MarkFailed("recipient_email_unavailable")`; Verify/Reset sin token → `MarkFailed("email_token_unavailable")`; si no, `SendAsync`.

- [ ] **Step 1: Escribir las pruebas**

`BuyerEmailTemplatesTests`: `TheVerificationEmailNamesTheStoreAndLinksToItsHost` (asunto exacto, `HtmlBody` y `TextBody` contienen `https://acme.lulocrm.com/cuenta/verificar?token=abc`), `TheExistingAccountEmailLinksToLoginAndForgot`, `TheResetEmailSaysOneHour`, `TheDeletedEmailHasNoLink`, `TheStoreNameIsHtmlEncoded` (Review Focus 4: `"A&B <Tienda>"` → `A&amp;B &lt;Tienda&gt;` en HTML, literal en asunto y texto), `TheTokenIsEscapedInTheLink`.

`BuyerEmailDeliveryTests` (con `NotificationsDeliveryHarness` y el canal grabador): por cada evento, una fila de outbox → un correo al email del payload, `notifications.tenant_id IS NULL`, plantilla correcta; `AVerificationWithoutTokenIsRecordedAsFailed`.

- [ ] **Step 2: RED** — `dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --filter FullyQualifiedName~BuyerEmailTemplatesTests` → no compila.
- [ ] **Step 3: Implementar**; `dotnet restore --force-evaluate`.
- [ ] **Step 4: GREEN** — el de arriba y `$env:Seed__Enabled = "false"`, `dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --filter "FullyQualifiedName~BuyerEmailDeliveryTests|FullyQualifiedName~DeliveryWorkersHarnessTests" -- xUnit.MaxParallelThreads=4` → PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(notifications): send the buyer account emails"`

---

### Task 12: Crear cuenta (outbox durable) y actualizar perfil desde el pedido

**Files:**
- Create: `src/Modules/Store/Modules.Store.Application/{IStoreBuyerAccounts,StoreIntegrationEvents,IStoreOutbox}.cs`, `src/Modules/Store/Modules.Store.Infrastructure/Persistence/{StoreOutboxWriter,OutboxRecord}.cs`, `src/Bootstrapper/StoreBuyerAccounts.cs`
- Create: `src/Modules/Buyers/Modules.Buyers.Infrastructure/Signup/{BuyerSignupRequestedMessage,BuyersInboxClaims,BuyerSignupRequestedWorker}.cs`, `…Buyers.Infrastructure/Persistence/Migrations/<ts>_AddBuyerSignupInbox.cs`
- Modify: `src/Modules/Store/Modules.Store.Application/SubmitOrderRequest.cs`, `src/Modules/Store/Modules.Store.Api/PublicStoreEndpoints.cs`, `StoreDbContext.cs` (proyección de `platform.outbox_messages` con `ExcludeFromMigrations`, copia de `IdentityDbContext.ConfigureOutboxProjection`), `StoreInfrastructureExtensions.cs`, `BuyersDbContext.cs`, `BuyersInfrastructureExtensions.cs`, `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Store/Modules.Store.UnitTests/SubmitOrderRequestValidatorTests.cs`, `tests/Modules/Store/Modules.Store.IntegrationTests/PublicOrderBuyerAccountApiTests.cs`, `tests/Modules/Buyers/Modules.Buyers.IntegrationTests/BuyerSignupRequestedWorkerTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/BuyerParityTests.cs`

**Interfaces:**
- Consumes: `BuyerSignup` (Task 5), `IBuyerPasswordHasher` (Task 3), `BuyerProfileWriter.ReplaceFromSessionAsync` (Task 8).
- Produces (Store):
  - `PublicBuyerSignupInput(string Password, bool AcceptsDataPolicy)`; `PublicOrderRequestBody` suma al final `PublicBuyerSignupInput? CreateAccount = null, bool UpdateProfile = false`; `PublicOrderRequestReceiptDto` suma `bool BuyerSessionRejected = false`; `SubmitOrderRequestCommand` suma `string? BuyerSessionToken = null`; `PublicOrderRequestBodyValidator.BuyerPasswordMinLength = 10`, `BuyerPasswordMaxLength = 128` + reglas de `CreateAccount` (contrato) + `CreateAccount` nulo cuando `UpdateProfile` (mensaje "No se crea una cuenta con una sesión iniciada.").
  - `IStoreBuyerAccounts`: `string HashPassword(string password)`; `Task<bool> ReplaceProfileAsync(string host, string? sessionToken, PublicBillingInput billing, PublicShippingInput? shipping, CancellationToken)`.
  - `StoreIntegrationEvents.BuyerSignupRequested = "store.buyer-signup-requested.v1"`; `StoreBuyerSignupRequestedPayload(Guid OrderRequestId, Guid TenantId, Guid ClientRequestId, string StoreHost, string StoreName, string VisitorIp, string Email, string PasswordHash, PublicBillingInput Billing, PublicShippingInput? Shipping)` — **sin** campo de contraseña en claro.
  - `IStoreOutbox.Add(string eventName, object payload, DateTimeOffset occurredAt)` (no guarda: viaja en el `SaveChanges` de `IStoreUnitOfWork`); `StoreOutboxWriter` serializa con `JsonSerializerOptions.Web`.
  - Handler: `(store, tenant) = await resolver.ResolveAsync(...)`. Con `CreateAccount`, después de validar, Turnstile y precios y **antes** de `BeginTransactionAsync`: `passwordHash = buyers.HashPassword(password)` (si lanza → log sin datos y el pedido sigue sin alta). Dentro de `SaveAsync`, junto a `requests.Add` y la auditoría: `outbox.Add(BuyerSignupRequested, payload, now)` con `StoreName = store.Name ?? tenant.DisplayName`. Solicitud y mensaje commitean o revierten juntos: el perdedor de la carrera por `clientRequestId` no deja mensaje y el reintento (`existing`) no escribe nada. Después del commit, sólo `UpdateProfile` → `ReplaceProfileAsync`, `BuyerSessionRejected = !ok`; una excepción → log sin email ni token, recibo intacto. Reintento y ganador de la carrera devuelven `BuyerSessionRejected = false`.
  - Endpoint: lee `StorefrontClient.BuyerSessionHeader` (Task 5) y lo pasa al comando.
  - Bootstrapper `StoreBuyerAccounts`: `HashPassword` delega en `IBuyerPasswordHasher.Hash`; `ReplaceProfileAsync` mapea a `BuyerProfileDto` (`ShippingSameAsBilling = shipping is null`) y llama a `ReplaceFromSessionAsync`.
- Produces (Buyers):
  - Migración `AddBuyerSignupInbox`: `buyers.inbox_messages` (`consumer varchar(100)`, `message_id uuid`, PK compuesta, `claimed_until`, `attempts int`, `processed_at`) y `buyers.processed_signups` (`order_request_id uuid` PK `PK_processed_signups`, `processed_at`).
  - `BuyerSignupRequestedMessage` (record propio, mismos nombres de campo en camelCase) con `const string EventName = "store.buyer-signup-requested.v1"` (literal: Buyers no ve Store) y `static BuyerSignupRequestedMessage Parse(string payloadJson)`.
  - `BuyersInboxClaims.TryClaimAsync(BuyersDbContext, string consumer, Guid messageId, DateTimeOffset now, TimeSpan lease, CancellationToken) -> Task<int?>` (copia de `InboxClaims` de Notifications).
  - `BuyerSignupRequestedWorker : BackgroundService` (consumidor `buyers.signup-requested`, sondeo 15 s, lote 20, lease 2 min, `MaxAttempts = 3`; agotado → log de error con el id del mensaje, sin email, y `processed_at`): reclama; si `processed_signups` ya tiene el `OrderRequestId` → sólo cierra el inbox; si no, `BuyerSignup.SignUpAsync` con el `PasswordHash` del mensaje, e inserta `processed_signups` y el `processed_at` del inbox **en el mismo `SaveChanges`** que cuenta, perfil, consentimiento, token y outbox del correo (el worker y `BuyerSignup` comparten el `BuyersDbContext` del scope; `BuyerSignup.SignUpAsync` gana un parámetro `Action? beforeSave` donde el worker agrega esas dos filas). Una violación de `PK_processed_signups` (dos réplicas) → `DiscardPendingChanges` y se trata como ya procesado. `internal Task DrainAsync(CancellationToken)` para pruebas.
  - `BuyerParityTests.TheSignupEventContractMatches` — serializa un `StoreBuyerSignupRequestedPayload` con `JsonSerializerOptions.Web`, lo lee con `BuyerSignupRequestedMessage.Parse` y compara campo a campo; y `StoreIntegrationEvents.BuyerSignupRequested == BuyerSignupRequestedMessage.EventName`.
- Produces (test): en `PublicOrderBuyerAccountApiTests`, helpers `Task<IReadOnlyList<string>> SignupPayloadsAsync()` (lee `payload` de `platform.outbox_messages` por `event_name`) y `Task DrainBuyerSignupsAsync()` (resuelve el worker y llama `DrainAsync`), sobre `PublicStoreFixture`.

- [ ] **Step 1: Escribir las pruebas**

`SubmitOrderRequestValidatorTests`: `ACreateAccountWithAShortPasswordFailsOnCreateAccountPassword`, `ACreateAccountWithoutPolicyAcceptanceFails`, `CreateAccountTogetherWithUpdateProfileFails`, `NoBuyerFieldsStillPasses`.

`PublicOrderBuyerAccountApiTests`:
- `TheSignupMessageCarriesAHashAndNeverThePlaintext` (invariante del hash) — pedido con `createAccount.password = "contraseña-de-prueba-123"`: exactamente un mensaje `store.buyer-signup-requested.v1`; `Assert.DoesNotContain("contraseña-de-prueba-123", payloadJson)`, y lo mismo sobre el texto de la fila en `store.order_requests` y en `audit.entries`; el JSON no tiene propiedad `password`; `payload.passwordHash` verifica con `IBuyerPasswordHasher.Verify(hash, "contraseña-de-prueba-123") != Failed`.
- `TheSignupMessageCommitsWithTheOrderRequest` — `payload.orderRequestId == receipt.id` y `payload.clientRequestId` = el del cuerpo.
- `ARetryWithTheSameClientRequestIdWritesOneSignupMessage` (idempotencia, Review Focus 5) — dos envíos con el mismo `clientRequestId`: mismo recibo y **un** mensaje; tras `DrainBuyerSignupsAsync`, una cuenta y un outbox Verify.
- `TwoSimultaneousSubmissionsOfTheSameIdLeaveOneSignupMessage` — el perdedor revierte su mensaje con su solicitud.
- `CreateAccountCreatesAnUnverifiedAccountWithTheOrderBlocks` — tras drenar: cuenta con el email de facturación, perfil = billing del pedido, `shippingSameAsBilling == true`, consentimiento con el host y la IP del visitante, un outbox Verify.
- `TheReceiptIsTheSameWhetherTheEmailHasAnAccountOrNot` (Review Focus 5) — segundo pedido con email ya verificado: mismos campos del recibo salvo `id`/`number`; tras drenar, un ExistingAccountNotice y el hash de la cuenta sin cambios.
- `AnUnverifiedEmailFromCheckoutReplacesPasswordAndProfileAndResendsVerification` — tras drenar, `password_hash` = el del mensaje, perfil nuevo, el token Verify anterior inválido.
- `AShortCreateAccountPasswordIs422BeforeTheOrderIsAccepted` — sin solicitud ni mensaje, `errors["CreateAccount.Password"]`.
- `UpdateProfileReplacesTheProfileWithTheOrderBlocks` — login, pedido con header y `updateProfile: true`; `buyerSessionRejected == false`; `GET /buyer/session` devuelve el perfil del pedido.
- `UpdateProfileWithASessionFromAnotherHostIsIgnoredAndFlagged` (Review Focus 3) — `201`, `buyerSessionRejected == true`, perfil sin cambios.
- `UpdateProfileWithoutTheHeaderIsFlagged`.
- `ABuyerFailureDoesNotFailTheOrder` — `IStoreBuyerAccounts` que lanza en los dos métodos: `201`, recibo normal, sin mensaje de alta.

`BuyerSignupRequestedWorkerTests` (Buyers.IntegrationTests; siembra filas en `platform.outbox_messages` con el JSON del contrato):
- `ProcessingTheSameMessageTwiceCreatesOneAccountAndOneEmail` (idempotencia) — `DrainAsync` dos veces, borrando la fila de `buyers.inbox_messages` entre corridas: una cuenta y un outbox Verify (lo frena `processed_signups`).
- `TwoMessagesForTheSameOrderRequestApplyOnce` — dos filas con el mismo `orderRequestId`: un efecto.
- `TheAccountUsesTheHashFromTheMessage` — `accounts.password_hash == payload.passwordHash`; un hasher doble registra 0 llamadas.
- `AMalformedMessageIsRecordedAsExhaustedAfterThreeAttempts` — `processed_at` no nulo y ninguna cuenta.

- [ ] **Step 2: RED** — `dotnet test tests/Modules/Store/Modules.Store.UnitTests --filter FullyQualifiedName~SubmitOrderRequestValidatorTests`; `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter FullyQualifiedName~BuyerParityTests` → no compilan.
- [ ] **Step 3: Implementar**; generar `AddBuyerSignupInbox` con el comando de **Antes de empezar**. La proyección del outbox en `StoreDbContext` no crea DDL; si `dotnet ef migrations has-pending-model-changes --project src/Modules/Store/Modules.Store.Infrastructure --context StoreDbContext` reporta cambios, se agrega la migración del snapshot. Barrer `tests/Modules/Store` por construcciones posicionales de `PublicOrderRequestBody`, `PublicOrderRequestReceiptDto` y `SubmitOrderRequestCommand` (los parámetros nuevos van al final con default para no romperlas).
- [ ] **Step 4: GREEN** — los dos de arriba; luego `$env:Seed__Enabled = "false"`, `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~PublicOrderBuyerAccountApiTests|FullyQualifiedName~PublicOrderRequestApiTests" -- xUnit.MaxParallelThreads=4`; `dotnet test tests/Modules/Buyers/Modules.Buyers.IntegrationTests --filter FullyQualifiedName~BuyerSignupRequestedWorkerTests -- xUnit.MaxParallelThreads=4`; `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~BuyersLayerTests|FullyQualifiedName~StoreLayerTests"` → PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(store): request buyer signup through the outbox and update the profile from the order"`

---

### Task 13: Limpieza de cuentas sin verificar, sesiones y tokens

**Files:**
- Create: `src/Modules/Buyers/Modules.Buyers.Infrastructure/BuyerHousekeepingWorker.cs`
- Modify: `BuyersInfrastructureExtensions.cs`
- Test: `tests/Modules/Buyers/Modules.Buyers.IntegrationTests/BuyerHousekeepingTests.cs`

**Interfaces:**
- Produces: `BuyerHousekeepingWorker : BackgroundService` (cada hora; `PeriodicTimer`); `internal Task<BuyerHousekeepingResult> RunOnceAsync(DateTimeOffset now, CancellationToken)` con `BuyerHousekeepingResult(int Accounts, int Sessions, int Tokens)`. Tres `ExecuteDeleteAsync`: cuentas con `email_verified_at IS NULL AND updated_at < now − 7 d`; sesiones revocadas, vencidas (`expires_at <= now`) o inactivas (`last_seen_at <= now − 7 d`); tokens usados o vencidos. Nunca toca `buyers.consents`. Un error se loguea y el siguiente tick reintenta.

- [ ] **Step 1: Escribir las pruebas** — `AnUnverifiedAccountOlderThanSevenDaysIsPurgedAndItsEmailIsFree` (registro después → cuenta nueva), `AnUnverifiedAccountReRegisteredRecentlyIsKept`, `AVerifiedAccountIsNeverPurged`, `ExpiredRevokedAndIdleSessionsArePurgedButLiveOnesStay`, `UsedAndExpiredTokensArePurged`, `ConsentsSurviveThePurge`.
- [ ] **Step 2: RED** — `--filter FullyQualifiedName~BuyerHousekeepingTests` → no compila.
- [ ] **Step 3: Implementar.**
- [ ] **Step 4: GREEN** → PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(buyers): purge unverified accounts and stale sessions and tokens"`

---

### Task 14: Documentación

**Files:**
- Modify: `README.md` (sección nueva "Cuentas de comprador": módulo, esquema, el contrato de este plan, header de sesión, límites, workers y la purga), `CLAUDE.md` (gotchas)
- Test: ninguno nuevo; verificación de la rama.

- [ ] **Step 1: README** — copia el **Contrato HTTP para el storefront** de este plan (es la autoridad desde el merge) y la tabla de eventos de outbox.
- [ ] **Step 2: CLAUDE.md** — sumar `Buyers` a la lista de módulos con `BuyersLayerTests`, y tres gotchas: (1) el perfil y la contraseña del comprador duplican reglas de Store e Identity y las amarra `BuyerParityTests`: cambiar una sin la otra pone la prueba roja; (2) el payload de `store.buyer-signup-requested.v1` es contrato entre Store y Buyers, leído por nombre y amarrado por `BuyerParityTests.TheSignupEventContractMatches`: renombrar un campo compila y deja las altas procesadas sin efecto; nunca lleva la contraseña en claro, sólo `passwordHash`; (3) `buyers.consents` es prueba legal: sin FK a propósito, nunca se borra, y el `Down` de `InitialBuyers` no se corre.
- [ ] **Step 3: Verificación final por proyecto**, uno a la vez (nunca la solución en paralelo):

```powershell
$env:Seed__Enabled = "false"
dotnet build
dotnet test tests/ArchitectureTests/ArchitectureTests
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests
dotnet test tests/Modules/Buyers/Modules.Buyers.UnitTests
dotnet test tests/Modules/Buyers/Modules.Buyers.IntegrationTests -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Store/Modules.Store.UnitTests
dotnet test tests/Modules/Store/Modules.Store.IntegrationTests -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests
dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests -- xUnit.MaxParallelThreads=4
dotnet format --verify-no-changes
```

Esperado: todo PASS y `format` sin cambios.

- [ ] **Step 4: Commit** — `git commit -m "docs(buyers): document buyer accounts and their storefront contract"`

# Onboarding de distribuidor por link — plan de implementación (backend)

> **Para quien ejecute:** SUB-SKILL REQUERIDA: `superpowers:subagent-driven-development` (recomendada) o
> `superpowers:executing-plans`. Los pasos usan casillas `- [ ]`. **Presupuesto de pruebas** (CLAUDE.md de la raíz): se ejecuta
> en 3 bloques (A: tareas 1-4, B: 5-8, C: 9-10), un implementador por bloque, un commit por tarea; el RED de compilación no
> levanta contenedores; la integración se filtra por nombre (`--filter "FullyQualifiedName~Nombre"`); nunca dos corridas de
> integración a la vez (`Get-Process testhost, dotnet -ErrorAction SilentlyContinue` antes); con `$env:Seed__Enabled = "false"`;
> los subagentes corren las pruebas en primer plano.

**Meta:** el distribuidor acepta una invitación de mayorista con un link que se puede copiar o mandar por WhatsApp, sin exigir
que el correo coincida, y queda vinculado como owner de un tenant nuevo o de uno donde es owner o admin.

**Arquitectura:** se evolucionan los endpoints de `wholesale-invitations` y la aceptación idempotente de
`DistributorInvitationService` (spec, enfoque A). Tenancy gana la consulta «owner o admin» y la sugerencia de slug; Identity
gana el desafío de correo (código) y la aceptación de términos; Wholesale rota el link a pedido, cambia el correo del
distribuidor al aceptar y manda el link por WhatsApp.

**Tecnología:** .NET 10, EF Core + Npgsql, FluentValidation, xUnit, Testcontainers. Outbox/inbox propios.

**Spec:** [`docs/superpowers/specs/2026-11-19-onboarding-distribuidor-por-link-design.md`](../specs/2026-11-19-onboarding-distribuidor-por-link-design.md)

## Restricciones globales

- Commits: conventional, **sin atribución de IA**, y sólo en una rama de trabajo (nunca `main`; los merges terminan en `develop`).
- Textos que lee una persona: **español colombiano tuteando**. Códigos de error, rutas, enums y permisos no se traducen.
- La aceptación **no** es atómica entre módulos: pasos idempotentes y la invitación se consume en el último commit, el de
  `wholesale` (`.claude/rules/mayorista-compras.md`). No se cambia a «consumir primero».
- El token plano **nunca** se guarda, se audita ni se loguea; viaja sólo en el payload del outbox (`SensitivePaths.Mask`).
- 403 y no 404 para lo que revela existencia en otro tenant; el mapeo de errores es central en `ApiExceptionHandler.cs`.
- Vigencia de la invitación 72 h, de un solo uso. Código de correo: 6 dígitos, 10 min, 5 intentos.
- Defaults del tenant nuevo, los del registro público: `es-CO`, `America/Bogota`, `dd/MM/yyyy`.
- «Admin» = rol de sistema `admin` (`Membership.AdminRole`) en una membresía `Active`; o el owner del tenant. Un rol
  personalizado no cuenta.
- Un campo requerido nuevo obliga a barrer las pruebas de integración que arman cuerpos a mano (`pruebas-y-entorno-local.md`).

## Foco de revisión

Entradas que el spec implica y ninguna tarea cubriría sola; cada una tiene su prueba en la tarea indicada.

1. Un usuario que ya existe **sin contraseña** más un link filtrado: sin código válido no se le fija contraseña (tarea 9).
2. Fuerza bruta del código: el quinto intento fallido lo invalida aunque el sexto sea correcto (tarea 3).
3. Dos aceptaciones compiten por el mismo slug sugerido: gana una y la otra reintenta con otro sufijo (tareas 2 y 9).
4. Si el commit final falla, el correo del distribuidor en el mayorista **no** cambia (tarea 5).
5. El link que devuelve «invitar» es el mismo del correo y funciona; el que devuelve «copiar» invalida al anterior (tarea 6).
6. El correo nuevo ya existe en otro distribuidor del mismo mayorista: antes de escribir, revisar si hay índice único sobre
   `email`; si lo hay, el cambio responde con el conflicto existente (`WholesaleConflicts`) en vez de un 500 (tarea 5).

---

## Bloque A — Tenancy e Identity

### Task 1: Tenants donde el usuario es owner o admin

**Archivos:**
- Modificar: `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantOwnership.cs`
- Modificar: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantOwnershipQuery.cs`
- Probar: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantOwnershipQueryTests.cs`
- Modificar: `tests/Modules/Wholesale/Modules.Wholesale.UnitTests/DistributorFakes.cs` (el doble implementa el contrato nuevo)

**Interfaces:**
- Produce: `Task<IReadOnlyList<OwnedTenant>> ITenantOwnership.ListAdministeredActiveTenantsAsync(Guid userId, CancellationToken cancellationToken)` y
  `static IQueryable<OwnedTenant> TenantOwnershipQuery.Administered(TenancyDbContext dbContext, Guid userId)`. `ListOwnedActiveTenantsAsync` se conserva (el tenant reservado sigue comprobándose por owner).

- [ ] **Paso 1: pruebas que fallan** en `TenantOwnershipQueryTests`: `AnAdminMemberSeesTheTenant`, `AMemberWithAnotherRoleDoesNotSeeTheTenant` (rol `billing`), `AnOwnerSeesTheTenantWithoutTheAdminRole`, `ASuspendedAdminMembershipDoesNotSeeTheTenant`, `AnInactiveTenantIsNotListed`. Cada una aserta la lista de `TenantId`.
- [ ] **Paso 2: RED** — `dotnet build` falla por el método que no existe (evidencia: la salida del build; no se levantan contenedores).
- [ ] **Paso 3:** implementar `Administered` y el método de la interfaz. La condición de rol usa el mismo mapeo de `Membership.Roles` que ya usa `MembershipRepository.ListActiveTenantSummariesByUserAsync`; ordena igual que `Owned`.
- [ ] **Paso 4: GREEN** — `dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~TenantOwnershipQueryTests"`: todas pasan.
- [ ] **Paso 5: commit** `feat(tenancy): list tenants administered by owner or admin role`.

### Task 2: Slug sugerido para el tenant del distribuidor

**Archivos:**
- Crear: `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantSlugSuggestions.cs` (interfaz + `TenantSlugs.Normalize`)
- Crear: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantSlugSuggestions.cs`; registrar en `TenancyInfrastructureExtensions.cs`
- Probar: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantSlugsTests.cs`, `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantSlugSuggestionsTests.cs`

**Interfaces:**
- Produce: `static string TenantSlugs.Normalize(string displayName)` (minúsculas, sin tildes, todo lo que no sea letra o dígito ASCII pasa a `-`, sin guiones repetidos ni en los extremos, tope 63; si queda de menos de 3 caracteres devuelve `distribuidor`) y
  `Task<string> ITenantSlugSuggestions.SuggestAsync(string displayName, CancellationToken cancellationToken)`: parte de `Normalize`, salta los reservados (`TenantSlugReservations.IsReserved`) y agrega `-2`, `-3`… hasta uno libre, respetando el tope de 63.

- [ ] **Paso 1: pruebas que fallan:** unitarias `StripsAccentsAndPunctuation` (`"Distribuciones Ñandú S.A.S."` → `distribuciones-nandu-s-a-s`), `ShortNamesFallBackToDistribuidor` (`"A"` → `distribuidor`), `LongNamesAreCutAt63`; de integración `ATakenSlugGetsTheNextSuffix` y `AReservedSlugGetsASuffix`.
- [ ] **Paso 2: RED** por compilación (la unitaria no necesita contenedor).
- [ ] **Paso 3:** implementar `TenantSlugs` y `TenantSlugSuggestions` (lectura de `tenancy.tenants.slug`, sin tracking).
- [ ] **Paso 4: GREEN** — `--filter "FullyQualifiedName~TenantSlug"` en los dos proyectos.
- [ ] **Paso 5: commit** `feat(tenancy): suggest a free slug from a business name`.

### Task 3: Desafío de correo por código (Identity)

**Archivos:**
- Crear: `src/Modules/Identity/Modules.Identity.Domain/EmailChallenge.cs`, `Modules.Identity.Application/IEmailChallenges.cs` (+ `EmailChallengeService`), `Modules.Identity.Infrastructure/Persistence/EmailChallengeRepository.cs` y su migración (`identity.email_challenges`)
- Modificar: `Modules.Identity.Application/IdentityIntegrationEvents.cs` (evento `identity.email-challenge-requested.v1`) y el consumidor de Notifications que sigue a `EmailVerificationDeliveryWorker.cs` (nuevo `EmailChallengeDeliveryWorker.cs`, mismo patrón y plantilla de correo con el código)
- Probar: `tests/Modules/Identity/Modules.Identity.UnitTests/EmailChallengeServiceTests.cs`, `tests/Modules/Identity/Modules.Identity.IntegrationTests/EmailChallengeRepositoryTests.cs`

**Interfaces:**
- Produce: `Task IEmailChallenges.IssueAsync(string email, CancellationToken ct)` (genera el código, guarda sólo su SHA-256 junto al hash del correo normalizado, invalida el anterior, publica el evento con el código crudo por outbox; si ya se emitió uno hace menos de 60 s no emite otro y no falla) y
  `Task<bool> IEmailChallenges.TryConsumeAsync(string email, string code, CancellationToken ct)` (atómico y fuera de la unidad de trabajo, como `IEmailTokenRepository.TryConsumeAsync`; un código errado suma un intento; al quinto fallo el desafío queda inválido).
- Constantes: `EmailChallengeService.CodeLength = 6`, `Lifetime = 10 min`, `MaxAttempts = 5`, `ReissueInterval = 60 s`.

- [ ] **Paso 1: pruebas que fallan:** `IssueStoresOnlyTheHashOfTheCode`, `IssuingAgainWithin60SecondsDoesNotPublishASecondEvent`, `AConsumedCodeCannotBeUsedAgain`, `AnExpiredCodeIsRejected`, `TheFifthWrongAttemptInvalidatesTheChallengeEvenIfTheNextCodeIsRight` (foco 2), `AnotherEmailsCodeIsRejected`.
- [ ] **Paso 2: RED** por compilación.
- [ ] **Paso 3:** implementar dominio, servicio, repositorio y migración; el evento sigue la forma de `EmailVerificationRequested` y el correo va en español colombiano tuteando (asunto «Tu código para vincularte», cuerpo con el código y su vigencia).
- [ ] **Paso 4: GREEN** — `--filter "FullyQualifiedName~EmailChallenge"` en Identity (unit e integration) y la prueba del worker en `Modules.Notifications.UnitTests`.
- [ ] **Paso 5: commit** `feat(identity): email challenge by one-time code`.

### Task 4: Aceptación de términos (Identity)

**Archivos:**
- Crear: `Modules.Identity.Domain/TermsAcceptance.cs`, `Modules.Identity.Application/ITermsAcceptances.cs` (+ `TermsAcceptanceService`, `TermsOptions` con `Terms:CurrentVersion`), repositorio y migración (`identity.terms_acceptances`, único en `(user_id, version)`)
- Probar: `tests/Modules/Identity/Modules.Identity.UnitTests/TermsAcceptanceServiceTests.cs`, `…IntegrationTests/TermsAcceptanceRepositoryTests.cs`

**Interfaces:**
- Produce: `Task ITermsAcceptances.RecordAsync(Guid userId, string version, CancellationToken ct)` (idempotente; una versión distinta de la vigente es `IdentityDomainException("identity.terms.version_mismatch")`; sin versión configurada, `"identity.terms.unavailable"` — falla cerrado) y
  `Task<bool> ITermsAcceptances.HasAcceptedCurrentAsync(Guid userId, CancellationToken ct)`.
- No hay validador de arranque: una configuración requerida rompería todos los hosts de prueba.

- [ ] **Paso 1: pruebas que fallan:** `RecordingTwiceStoresOneRow`, `AWrongVersionIsRejected`, `WithoutAConfiguredVersionNothingIsAccepted`, `HasAcceptedCurrentIsFalseForAnOlderVersion`.
- [ ] **Paso 2: RED** por compilación. **Paso 3:** implementar. **Paso 4: GREEN** con `--filter "FullyQualifiedName~TermsAcceptance"`.
- [ ] **Paso 5: commit** `feat(identity): record acceptance of the terms version`.

---

## Bloque B — Wholesale

### Task 5: Correo del distribuidor y reglas de aceptación

**Archivos:**
- Modificar: `Modules.Wholesale.Domain/Distributor.cs`, `Modules.Wholesale.Application/DistributorInvitationService.cs`, `Ports.cs` (`IWholesaleEventPublisher`), `WholesaleAuditActions.cs`, `Modules.Wholesale.Infrastructure/Persistence/WholesaleEventPublisher.cs`
- Probar: `tests/Modules/Wholesale/Modules.Wholesale.UnitTests/DistributorTests.cs`, `DistributorInvitationServiceTests.cs`, `DistributorFakes.cs`

**Interfaces:**
- Produce: `bool Distributor.ReplaceEmail(string email, DateTimeOffset now)` — sólo `Invited`; normaliza como `NormalizeEmail`; devuelve `true` si cambió (y llama a `Touch`).
- Produce: `void IWholesaleEventPublisher.DistributorEmailChanged(Guid tenantId, Guid distributorId, string previousEmail, string newEmail, string businessName, string correlationId, DateTimeOffset now)`; acción de auditoría `WholesaleAuditActions.DistributorEmailChanged` (campo `email`, sin los correos).
- `DistributorInvitationService`: se elimina `EnsureEmail` de `ListLinkableTenantsAsync`, `AcceptAsync` y `RepeatedAsync`; `userEmail` pasa a ser el correo **de la cuenta** que acepta. `ResolveTargetAsync` usa `ListAdministeredActiveTenantsAsync` para `LinkTenantChoice` y `ListLinkableTenantsAsync` (el tenant reservado sigue por `ListOwnedActiveTenantsAsync`). `AcceptAsync`, antes del paso 8, llama a `ReplaceEmail(userEmail, now)` y, si cambió, publica el evento y la auditoría; todo va en el mismo `SaveChangesAsync`, así que un fallo del commit no cambia nada.

- [ ] **Paso 1: pruebas que fallan:** `ReplaceEmailNormalizesAndReportsTheChange`, `ReplaceEmailOnAnAcceptedDistributorIsRejected`; de servicio `AcceptWithADifferentEmailReplacesItAndPublishesTheEvent`, `AcceptWithTheSameEmailWritesNoEventAndNoAudit`, `AcceptWithADifferentEmailDoesNotChangeItIfTheCommitFails` (foco 4), `AnAdminOfTheTenantCanLinkIt`, `ABillingMemberCannotLinkItAndGetsNotTenantOwner`, `ListLinkableIncludesTenantsWhereTheUserIsAdmin`. Barrer `DistributorFakes`/`DistributorWorld`.
- [ ] **Paso 2: RED** por compilación. **Paso 3:** implementar. Antes, revisar `WholesaleDbContext` por un índice único sobre `email` (foco 6) y, si existe, mapear su violación al conflicto que ya usa el módulo.
- [ ] **Paso 4: GREEN** — `dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests` completo (rápido, sin contenedores).
- [ ] **Paso 5: commit** `feat(wholesale): accept with any email and link tenants administered by admins`.

### Task 6: Link copiable

**Archivos:**
- Modificar: `Modules.Wholesale.Domain/Distributor.cs`, `Modules.Wholesale.Application/DistributorDtos.cs`, `InviteDistributor.cs`, `Ports.cs`, `WholesaleAuditActions.cs`; crear `RotateInvitationLink.cs`; `Modules.Wholesale.Api/WholesaleEndpoints.cs`; `src/Bootstrapper` (implementación de `IWholesaleInvitationLinks` con `AccountLinks.WholesaleInvitation`)
- Probar: `DistributorTests.cs`, `DistributorHandlersTests.cs`, `tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests/DistributorApiTests.cs`

**Interfaces:**
- Produce: `void Distributor.RotateInvitationLink(string tokenHash, DateTimeOffset now)` — sólo `Invited`; solo rota token y vigencia (`Invitation.Rotate`), **sin** tocar el aviso ni publicar eventos.
- Produce: `string IWholesaleInvitationLinks.Compose(string token)`.
- Produce: `DistributorDetailDto` gana `string? InvitationUrl` (nulo salvo en las dos respuestas siguientes).
- Produce: `RotateInvitationLinkCommand(Guid TenantId, Guid DistributorId, long ExpectedVersion, string CorrelationId) : ICommand<DistributorDetailDto>`, permiso `WholesalePermissions.DistributorManage`, ruta `POST /api/v1/tenants/{tenantId:guid}/wholesale/distributors/{distributorId:guid}/invitation-link`, auditoría `distributor.invitation_rotated` sin token.
- `InviteDistributorHandler` devuelve el detalle con `InvitationUrl` armado con el **mismo token** que va en el correo (no rota nada).

- [ ] **Paso 1: pruebas que fallan:** `InviteReturnsTheSameLinkThatTheEmailCarries` (foco 5), `RotateReturnsANewUrlAndTheOldTokenStopsWorking`, `RotateDoesNotQueueNoticesNorEmails`, `RotateOnAnAcceptedDistributorIsConflict`, `RotateRequiresDistributorManage`, `TheAuditOfTheRotationNamesNoToken`.
- [ ] **Paso 2: RED** por compilación. **Paso 3:** implementar. **Paso 4: GREEN** — unit completo del módulo y `--filter "FullyQualifiedName~DistributorApiTests"`.
- [ ] **Paso 5: commit** `feat(wholesale): copyable invitation link that rotates on demand`.

### Task 7: Aviso de correo cambiado

**Archivos:**
- Crear: `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/WholesaleEmailChangedDeliveryWorker.cs` (calca `WholesaleInvitationDeliveryWorker.cs`); evento `wholesale.distributor-email-changed.v1` en `WholesaleEventPublisher.cs`
- Probar: `tests/Modules/Notifications/Modules.Notifications.UnitTests/WholesaleEmailChangedDeliveryWorkerTests.cs`

**Interfaces:**
- Consume: `DistributorEmailChanged(...)` de la tarea 5.
- Produce: dos correos por evento, al correo anterior y al nuevo, en español colombiano tuteando; ninguno lleva token ni link. Un fallo de uno no impide el otro ni bloquea la aceptación (el evento va por outbox).

- [ ] **Paso 1: pruebas que fallan:** `ItSendsOneEmailToThePreviousAndOneToTheNewAddress`, `AFailureSendingToOneStillSendsTheOther`, `NoEmailContainsAToken`.
- [ ] **Paso 2: RED. Paso 3:** implementar. **Paso 4: GREEN** con `--filter "FullyQualifiedName~WholesaleEmailChanged"`.
- [ ] **Paso 5: commit** `feat(notifications): tell both parties when a distributor email changes`.

### Task 8: WhatsApp con link

**Archivos:**
- Modificar: `Modules.Wholesale.Application/DistributorWhatsAppNotice.cs`, `DistributorInvitationNotices.cs`, `Modules.Wholesale.Infrastructure/Messaging/InvitationNoticeProcessor.cs`, `WholesaleEventPublisher.cs`, y el envío de `WholesaleInvitationNoticeSender` en `src/Bootstrapper`
- Probar: `DistributorInvitationNoticesTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/WholesaleInvitationNoticeSenderTests.cs`, `DistributorWhatsAppNoticeApiTests.cs`

**Interfaces:**
- El evento del aviso gana el token y se renombra `wholesale.distributor-invitation-link.v1`; el viejo `…-notice.v1` **sigue registrado** sólo para drenar los pendientes. Con teléfono, invitar y reenviar escriben el evento nuevo y **no** el viejo. El resultado sigue aplicándose a la `generation` vigente y sin subir `version` (D-3, D-4); el `clientId` sigue siendo el id del mensaje del outbox.
- La plantilla nueva lleva `{{mayorista}}` y `{{link}}`; en el mensaje guardado en Mensajería el token queda enmascarado (si `PlainTemplateOutbound` no lo permite, se registra como ítem aparte y la tarea se cierra con la restricción documentada).

- [ ] **Paso 1: pruebas que fallan:** `InvitingWithAPhoneWritesTheLinkEventAndNotTheOldNotice`, `ResendRotatesTheTokenInTheNewEvent`, `TheStoredMessageNeverContainsTheToken`, `APendingOldNoticeIsStillProcessed`, `AFailureOfTheNoticeDoesNotChangeTheEmailNorTheInvitation`.
- [ ] **Paso 2: RED. Paso 3:** implementar. **Paso 4: GREEN** — unit del módulo, `Bootstrapper.UnitTests` filtrado y `--filter "FullyQualifiedName~DistributorWhatsAppNoticeApiTests"`.
- [ ] **Paso 5: commit** `feat(wholesale): send the invitation link by whatsapp`.

---

## Bloque C — Composición y documentación

### Task 9: Endpoints de la invitación

**Archivos:**
- Modificar: `src/Api/WholesaleInvitationEndpoints.cs`, `src/Bootstrapper/WholesaleInvitationPasswordSession.cs`, `WholesaleInvitationGoogleSession.cs`
- Probar: `tests/Bootstrapper/Bootstrapper.UnitTests/WholesaleInvitationPasswordSessionTests.cs`, `WholesaleInvitationGoogleSessionTests.cs`, `tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests/InvitationAcceptanceApiTests.cs`

**Interfaces:**
- `POST {Route}/email-code` (anónimo, `RateLimiterPolicies.Credentials`), cuerpo `WholesaleInvitationEmailCodeRequest(string? Email)`, responde 202 siempre (no revela si el correo existe) y sólo emite si la invitación está pendiente.
- `WholesaleInvitationPasswordRequest(string? Email, string? Password, string? Code, string? TermsVersion)`. `WholesaleInvitationPasswordSession.OpenAsync(string email, string password, string? code, string? termsVersion, CancellationToken ct)`: **con contraseña ya creada** verifica la contraseña y nada más; **sin usuario o sin contraseña** exige un `code` válido (`IEmailChallenges.TryConsumeAsync`) y una `termsVersion` (`ITermsAcceptances.RecordAsync`), y recién entonces crea o completa la credencial. El orden conserva la regla W17 (contraseña válida antes de tocar Identity). Sin código válido: 401 `identity.credentials.invalid`, sin crear ni cambiar nada.
- `WholesaleInvitationGoogleSession.OpenAsync(string verifiedEmail, string subject, ...)`: aprovisiona y bloquea por el **correo verificado de Google**, no por el invitado; se retira `EmailMismatch` y su chequeo en `SessionAsync`.
- `AcceptWholesaleInvitationRequest(string? Mode, Guid? TenantId, string? TermsVersion)`: se **quitan** `DisplayName` y `Slug` (un cuerpo viejo que los traiga se ignora); para `NewTenant` el nombre es `Distributor.BusinessName` y el slug sale de `ITenantSlugSuggestions.SuggestAsync` (spec §3b); se retira la regla del validador que los exigía; si el slug se toma entre la sugerencia y el alta (`tenancy.slug.taken`), se reintenta con una sugerencia nueva hasta 3 veces (foco 3). Si el usuario no tiene `HasAcceptedCurrentAsync`, `NewTenant` exige `TermsVersion` (422 en `TermsVersion`) y lo registra.
- `GET {Route}` sigue devolviendo el correo completo (spec §3); `LinkableTenantsResponse` sale de `ListAdministeredActiveTenantsAsync`.

- [ ] **Paso 1: pruebas que fallan:** unitarias de sesión `AUserWithoutAPasswordNeedsAValidCode` (foco 1), `AUserWithAPasswordEntersWithoutCode`, `ANewUserNeedsTheTermsVersion`, `AWeakPasswordIsRejectedBeforeTouchingIdentity`, `GoogleOpensTheSessionForTheVerifiedEmailEvenIfItIsNotTheInvitedOne`; de integración `ANewTenantUsesTheBusinessNameAndAGeneratedSlugAndIgnoresABodyNameAndSlug`, `ANewDistributorFinishesWithAPasswordACodeAndTheTerms`, `ADifferentEmailReplacesTheWholesalersRecordAndNotifiesBoth`, `AnExistingAccountLinksWithoutTouchingAnythingIfTheEmailMatches`, `ABillingMemberIsToldToChangeOrganizationAndCanCreateANewTenant`, `AnAdminLinksTheTenant`, `TwoAcceptancesRacingForTheSameSlugBothEndUpWithATenant`, `EmailCodeAlwaysAnswers202`.
- [ ] **Paso 2: RED** por compilación; las de integración corren **filtradas por nombre** y de una en una.
- [ ] **Paso 3:** implementar. **Paso 4: GREEN** — `Bootstrapper.UnitTests` filtrado a `WholesaleInvitation*` y `--filter "FullyQualifiedName~InvitationAcceptanceApiTests"`. Barrer las pruebas de integración que arman el cuerpo de `session-with-password` o `accept` a mano.
- [ ] **Paso 5: commit** `feat(wholesale): onboard distributors from the invitation link`.

### Task 10: Reglas y spec anterior

**Archivos:** `.claude/rules/mayorista-compras.md`, `.claude/rules/tenancy-y-servicios.md`, `docs/superpowers/specs/2026-11-18-invitacion-distribuidor-whatsapp-design.md`

- [ ] **Paso 1:** en `mayorista-compras.md`: el link ya no prueba el buzón y el correo no tiene que coincidir; «copiar» rota; sin contraseña siempre hay código; el token viaja en el evento del WhatsApp. En `tenancy-y-servicios.md`: «admin» para vincular es el rol de sistema `admin` en membresía activa.
- [ ] **Paso 2:** al spec del 2026-11-18 se le agrega arriba una nota «Reemplazado en lo del link y la identidad por 2026-11-19».
- [ ] **Paso 3: verificación final del bloque:** `dotnet build` de la solución sin advertencias nuevas y las suites de los módulos tocados (Wholesale, Tenancy, Identity, Notifications; Bootstrapper.UnitTests), una a la vez, con `-- xUnit.MaxParallelThreads=4`. **Esta corrida completa es la que va antes de mergear a `develop`.**
- [ ] **Paso 4: commit** `docs(wholesale): rules and superseded notes for link onboarding`.

## Fuera de este plan

- El frontend (spec propio en `qep-frontend`): pantalla de la invitación, botón «Copiar» siempre visible y el aviso de que rota, selector de organización.
- El texto, la URL y la versión de los términos (owner): hasta que `Terms:CurrentVersion` exista, el alta de usuario nuevo falla cerrado.
- `docs/estado.md`: se actualiza en `develop` al mergear, no en la rama.

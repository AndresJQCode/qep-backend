# Invitación de distribuidor: el link del correo prueba el buzón — plan de implementación (backend)

> **Para quien ejecute:** SUB-SKILL REQUERIDA: `superpowers:subagent-driven-development` (recomendada) o
> `superpowers:executing-plans`. Los pasos usan casillas `- [ ]`. **Presupuesto de pruebas** (decisión del owner, `CLAUDE.md` de
> la raíz; manda sobre el proceso del skill): 6 tareas en **3 bloques** —A: tareas 1-2, B: 3-4, C: 5-6—, un implementador y una
> revisión por bloque, y una revisión fuerte de toda la rama al final. Cada tarea conserva su commit. El RED que falla por
> compilación no levanta contenedores: la evidencia es la salida del build. La integración se filtra por nombre de prueba
> mientras se itera y la clase completa corre una vez, al final del bloque. Nunca dos corridas de integración a la vez:
> antes de cada una, `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`, y si hay una se espera (no se detiene). Las
> pruebas de integración van con `$env:Seed__Enabled = "false"` en línea aparte y en primer plano.

**Meta:** quien abre el link del correo de la invitación crea su cuenta con el correo invitado sin el código de 6 dígitos; el
link de «Copiar» y el de WhatsApp siguen pidiendo código.

**Arquitectura:** invitar y «Reenviar» generan una prueba aleatoria distinta del token; en `distributor_invitations` sólo vive
su SHA-256 (`email_proof_hash`, nula) y en claro viaja sólo en el payload de `wholesale.distributor-invited.v1`. Notifications
la agrega al link del correo como `?ev=`. «Copiar» y el cambio de correo la anulan. `session-with-password` recibe
`emailProof`; Wholesale decide si prueba el buzón (`DistributorEmailProofs.Proves`) y `WholesaleInvitationPasswordSession`
sólo recibe el resultado (`emailProven`), que reemplaza al código válido.

**Tecnología:** .NET 10, EF Core + Npgsql, xUnit, Testcontainers. Outbox propio de Wholesale.

**Spec:** [`docs/superpowers/specs/2026-11-24-invitacion-distribuidor-prueba-de-correo-design.md`](../specs/2026-11-24-invitacion-distribuidor-prueba-de-correo-design.md)

## Antes de empezar (bloque A, paso 0)

La rama nació de `8b75614b4` y `develop` avanzó 5 commits: entró `feature/marca-de-aceptacion-iniciada` (`f991887f0`), que
toca `Distributor.cs`, `WholesaleDbContext.cs`, el snapshot de migraciones (migración `20261125023730_AddDistributorAcceptanceStartedAt`),
`DistributorInvitationService.cs`, `InvitationAcceptanceApiTests.cs`, `DistributorTests.cs` y `DistributorFakes.cs`. **El worktree
`marca-aceptacion` ya no existe: la rama está mergeada.** La rama no tiene commits propios, así que se adelanta sin merge:

```powershell
git -C . branch --show-current          # feature/distributor-email-proof
git merge --ff-only develop
```

Sin esto, la migración nueva se generaría sobre un snapshot sin `acceptance_started_at` y chocaría con la de `develop`. El spec
sigue sin seguimiento y entra en el commit de la tarea 1.

Otra rama viva, `feature/ciudad-del-distribuidor-con-departamento` (worktree `ciudad-con-departamento`), toca
`Ports.cs`, `DistributorFakes.cs` y `DistributorApiTests.cs` en otras regiones (lookup de ciudad). Riesgo de conflicto bajo y
textual; quien mergee segundo lo resuelve.

## Restricciones globales

- Commits: conventional, **sin atribución de IA** (ni `Co-Authored-By`), y sólo en `feature/distributor-email-proof`; el merge
  termina en `develop`, nunca en `main`. `git branch --show-current` antes de cada commit.
- La prueba se genera con `InvitationTokens.Generate()` y se guarda con `InvitationTokens.HashOf` (64 caracteres hex en
  minúscula), en `distributor_invitations.email_proof_hash char(64)`, nula.
- En claro viaja **sólo** en el payload de `wholesale.distributor-invited.v1`, campo `emailProof`. Nunca en auditoría, logs,
  respuestas de la API, el evento de WhatsApp (`wholesale.distributor-invitation-link.v1`) ni `invitationUrl`.
- Link del correo: `{AppBaseUrl}/wholesale-invitations/{token}?ev={prueba}`. Sin `emailProof` en el payload, el link de hoy.
- Se salta el código sólo si: invitación pendiente, `emailProof` coincide con el hash vigente y el correo (normalizado como
  `Distributor.Email`) es el invitado. Los términos se siguen exigiendo. En cualquier otro caso rige el flujo actual: sin
  código, 401 `identity.credentials.invalid`, que no distingue prueba errada de código errado.
- Una cuenta con contraseña sigue siendo un inicio de sesión: la prueba no la toca.
- «Copiar» (rota token) anula la prueba; «Reenviar» pone una nueva; cambiar el correo la anula.
- `GET` de la invitación, `accept`, `session` (Google), `email-code`, las invitaciones de miembros y la vigencia de 72 h no
  cambian.
- La migración es aditiva (columna nula): expand sin contract, se despliega sin corte.
- Comentarios y documentación en español tuteando; identificadores y commits en inglés.
- Merge a `develop` con `.\scripts\test-feature.ps1` en verde, leyendo sus avisos.

## Review Focus

1. **Cambiar el correo a uno con otras mayúsculas o espacios** (`" Ana@Distri.co "` sobre `ana@distri.co`) no es un cambio y no
   debe anular la prueba; uno distinto sí (tarea 1, `ChangeProfileVoidsTheEmailProofOnlyWhenTheEmailChanges`).
2. **El correo del cuerpo con mayúsculas y espacios** (`" Compras@Andina.CO "`) frente al invitado debe valer como el invitado,
   igual que el login (tarea 1, `ProvesInvitedMailboxIgnoresCaseAndSpacesOfTheEmail`).
3. **Una prueba vieja tras «Reenviar» o «Copiar»** con el token nuevo debe responder 401 sin crear usuario (tarea 6,
   `AnEmailProofIsVoidedByCopyResendAndAnEmailChange`).
4. **Términos viejos con prueba válida** no deben dejar un usuario con contraseña y sin términos (tarea 5,
   `AnEmailProofStillNeedsTheCurrentTerms`; tarea 6, `TermsMissingOrStaleWithAValidProofAre422AndCreateNoUser`).
5. **Un `emailProof` en un payload sin token o vacío** no debe romper el worker ni producir `?ev=` vacío (tarea 4,
   `TheLinkCarriesTheEmailProofAsEvOnlyWhenThereIsOne` con `null` y `""`).

---

## Bloque A — Dominio y persistencia

### Task 1: La prueba de buzón en la invitación

**Files:**
- Modify: `src/Modules/Wholesale/Modules.Wholesale.Domain/DistributorInvitation.cs`
- Modify: `src/Modules/Wholesale/Modules.Wholesale.Domain/Distributor.cs` (`Invite`, `ResendInvitation`, `RotateInvitationLink`, `ChangeProfile`, método nuevo)
- Test: `tests/Modules/Wholesale/Modules.Wholesale.UnitTests/DistributorTests.cs`
- Add (sin cambios): `docs/superpowers/specs/2026-11-24-invitacion-distribuidor-prueba-de-correo-design.md`

**Interfaces:**
- Produces:
  - `string? DistributorInvitation.EmailProofHash { get; private set; }`
  - `internal static DistributorInvitation Issue(string tokenHash, string? emailProofHash, Guid reservedTenantId, bool withNotice, DateTimeOffset now)`
  - `internal void Rotate(string tokenHash, string? emailProofHash, DateTimeOffset now)` (deja `EmailProofHash` en el valor dado)
  - `internal void ForgetEmailProof()`
  - `Distributor.Invite(..., string? address = null, string? emailProofHash = null)` — parámetro opcional nuevo al final.
  - `void Distributor.ResendInvitation(string tokenHash, bool noticeQueued, DateTimeOffset now, string? emailProofHash = null)`
  - `RotateInvitationLink(string tokenHash, DateTimeOffset now)`: misma firma; deja la prueba en `null`.
  - `ChangeProfile(...)`: misma firma; si el correo normalizado cambia, `Invitation.ForgetEmailProof()`.
  - `bool Distributor.ProvesInvitedMailbox(string emailProofHash, string? email)`: `Status == Invited`, hash guardado no nulo,
    igualdad en tiempo constante (`CryptographicOperations.FixedTimeEquals` sobre los bytes ASCII) e `InvitedEmailMatches(email)`.
    El vencimiento lo garantiza quien llama (`RequirePendingAsync` corre antes en el mismo request).
  - Un `emailProofHash` no nulo pasa la misma regla que `EnsureTokenHash` (64 hex en minúscula); si no, `ArgumentException`
    con `paramName` `emailProofHash`.

- [ ] **Step 1: pruebas que fallan** en `DistributorTests` (usa `Hash1`/`Hash2` y una constante nueva `Proof1 = "bbbb…"` de 64 `b`):
  - `InviteKeepsTheEmailProofHash`: `Invite(...)` con `emailProofHash: Proof1` ⇒ `Invitation.EmailProofHash == Proof1`; sin él ⇒ `null`.
  - `InviteAndResendRejectAnEmailProofHashThatIsNotALowercaseSha256` (theory con los cuatro valores de `InviteRejectsATokenHashThatIsNotALowercaseSha256`).
  - `ResendReplacesTheEmailProof`: tras `ResendInvitation(Hash2, false, Now.AddHours(1), Proof2)` ⇒ `EmailProofHash == Proof2`.
  - `RotateInvitationLinkVoidsTheEmailProof` ⇒ `null`; el resto de `RotateInvitationLinkChangesTokenAndExpiryAndKeepsTheNotice` intacto.
  - `ChangeProfileVoidsTheEmailProofOnlyWhenTheEmailChanges`: `" Ana@Distri.CO "` conserva `Proof1`; `"otra@distri.co"` ⇒ `null`.
  - `ProvesInvitedMailboxNeedsTheStoredHashAndTheInvitedEmail`: `true` con `(Proof1, "ana@distri.co")`; `false` con `Hash2`, con
    otro correo, con `null`/`""` de correo, sin prueba guardada, y en un distribuidor `Active()`.
  - `ProvesInvitedMailboxIgnoresCaseAndSpacesOfTheEmail`: `(Proof1, " Ana@Distri.CO ")` ⇒ `true`.
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Wholesale/Modules.Wholesale.UnitTests` falla por `EmailProofHash`,
  `ProvesInvitedMailbox` y el parámetro `emailProofHash` inexistentes. Pegar la salida.
- [ ] **Step 3:** implementar lo del bloque Interfaces. `Issue` y `Rotate` reciben la prueba; `RotateInvitationLink` pasa `null`.
  Actualizar el `<summary>` de `DistributorInvitation` (el token plano ya no es lo único que viaja en el outbox) y el de
  `ChangeProfile` (cambiar el correo anula la prueba, sin rotar el token).
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests --filter "FullyQualifiedName~DistributorTests"`: todas pasan.
- [ ] **Step 5: commit**

```powershell
git add docs/superpowers/specs/2026-11-24-invitacion-distribuidor-prueba-de-correo-design.md src/Modules/Wholesale/Modules.Wholesale.Domain tests/Modules/Wholesale/Modules.Wholesale.UnitTests/DistributorTests.cs
git commit -m "feat(wholesale): keep an email proof hash on the distributor invitation"
```

### Task 2: Columna `email_proof_hash` y migración

**Files:**
- Modify: `src/Modules/Wholesale/Modules.Wholesale.Infrastructure/Persistence/WholesaleDbContext.cs` (bloque `OwnsOne(value => value.Invitation, …)`, junto a `token_hash`)
- Create: `src/Modules/Wholesale/Modules.Wholesale.Infrastructure/Persistence/Migrations/<timestamp>_AddInvitationEmailProof.cs` y su `.Designer.cs`; Modify: `WholesaleDbContextModelSnapshot.cs`
- Test: `tests/Modules/Wholesale/Modules.Wholesale.UnitTests/WholesaleDbContextMappingTests.cs`

**Interfaces:**
- Consumes: `DistributorInvitation.EmailProofHash` (tarea 1).
- Produces: columna `wholesale.distributor_invitations.email_proof_hash char(64) NULL`, sin índice.

- [ ] **Step 1: prueba que falla** `TheEmailProofHashIsANullableChar64Column`: la propiedad `EmailProofHash` de
  `DistributorInvitation` mapea a `("email_proof_hash", "char(64)", true)` (`GetColumnName()`, `GetColumnType()`, `IsNullable`).
  `TheTokenHashIsTheOnlyTokenColumn` sigue en verde sin tocarla (la columna no contiene «token»).
- [ ] **Step 2: RED** — `dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests --filter "FullyQualifiedName~WholesaleDbContextMappingTests"`:
  falla `TheEmailProofHashIsANullableChar64Column` (EF la mapea por convención como `text`/sin nombre) y
  `TheModelHasNoChangesPendingAMigration`. Sin contenedores.
- [ ] **Step 3:** mapear `invitation.Property(value => value.EmailProofHash).HasColumnName("email_proof_hash").HasColumnType("char(64)")`
  con un comentario de por qué (sólo el hash; la prueba plana no tiene columna). Generar la migración con el factory de diseño:

```powershell
dotnet ef migrations add AddInvitationEmailProof --project src/Modules/Wholesale/Modules.Wholesale.Infrastructure --context WholesaleDbContext -o Persistence/Migrations
```

  Revisar que el `Up` sólo haga `AddColumn` nula (sin `defaultValue`) y que su timestamp quede **después** de
  `20261125023730_AddDistributorAcceptanceStartedAt`.
- [ ] **Step 4: GREEN** — la misma corrida filtrada: todas pasan.
- [ ] **Step 5: fin de bloque A** — `dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests` completo en verde, y la
  integración que aplica migraciones, una vez: `dotnet test tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests --filter "FullyQualifiedName~DistributorApiTests"`.
- [ ] **Step 6: commit**

```powershell
git add src/Modules/Wholesale/Modules.Wholesale.Infrastructure/Persistence tests/Modules/Wholesale/Modules.Wholesale.UnitTests/WholesaleDbContextMappingTests.cs
git commit -m "feat(wholesale): store the invitation email proof hash"
```

## Bloque B — La prueba viaja al correo

### Task 3: Invitar y «Reenviar» generan la prueba y la publican

**Files:**
- Create: `src/Modules/Wholesale/Modules.Wholesale.Application/DistributorEmailProofs.cs`
- Modify: `src/Modules/Wholesale/Modules.Wholesale.Application/Ports.cs` (`IWholesaleEventPublisher.DistributorInvited`)
- Modify: `src/Modules/Wholesale/Modules.Wholesale.Infrastructure/Persistence/WholesaleEventPublisher.cs` (método y `DistributorInvitedPayload`)
- Modify: `src/Modules/Wholesale/Modules.Wholesale.Application/InviteDistributor.cs`, `TransitionDistributor.cs`
- Modify: `tests/Modules/Wholesale/Modules.Wholesale.UnitTests/DistributorFakes.cs` (`PublishedInvitation`, `FakeEvents.DistributorInvited`)
- Test: `tests/Modules/Wholesale/Modules.Wholesale.UnitTests/DistributorHandlersTests.cs`, `DistributorEmailProofsTests.cs` (nuevo)

**Interfaces:**
- Consumes: `Distributor.Invite(..., emailProofHash)`, `ResendInvitation(..., emailProofHash)`, `ProvesInvitedMailbox` (tarea 1).
- Produces:
  - `public static class DistributorEmailProofs` con
    `public static (string Proof, string Hash) Issue()` (`InvitationTokens.Generate()` + `HashOf`) y
    `public static bool Proves(Distributor distributor, string? emailProof, string? email)` (`false` con prueba en blanco; si no,
    `distributor.ProvesInvitedMailbox(InvitationTokens.HashOf(emailProof), email)`).
  - `void IWholesaleEventPublisher.DistributorInvited(Guid tenantId, Guid distributorId, string email, string businessName, string token, string emailProof, DateTimeOffset expiresAt, string correlationId, DateTimeOffset occurredAt)`.
  - Payload de `wholesale.distributor-invited.v1`: los campos de hoy más `emailProof` (minúscula, como el resto).
  - `PublishedInvitation(..., string Token, string EmailProof, DateTimeOffset ExpiresAt, ...)` en el doble.

- [ ] **Step 1: pruebas que fallan**
  - `DistributorEmailProofsTests`: `IssueGivesAProofAndItsHash` (`Hash == InvitationTokens.HashOf(Proof)`, `Proof != Hash`, dos
    llamadas dan pruebas distintas); `ProvesIsFalseForABlankProof` (`null`, `""`, `"  "`) y `ProvesMatchesTheIssuedProofAndTheInvitedEmail`
    (con un `Distributor.Invite(..., emailProofHash: hash)`).
  - En `DistributorHandlersTests`:
    - `InviteStoresOnlyTheEmailProofHashAndPublishesTheProof`: `stored.Invitation.EmailProofHash == InvitationTokens.HashOf(published.EmailProof)`,
      `published.EmailProof != published.Token`, la auditoría no la contiene (`Assert.DoesNotContain(published.EmailProof, …)` como
      en `InviteStoresOnlyTheHashAndPublishesTheTokenOnce`) y `detail.InvitationUrl` no contiene `published.EmailProof` ni `"ev="`.
    - `ResendPublishesANewEmailProofAndTheOldOneStopsProving`: tras `Resend`, `Events.Invitations[1].EmailProof` distinta de la
      primera; `DistributorEmailProofs.Proves(stored, first, stored.Email)` es `false` y con la nueva `true`.
    - `RotateVoidsTheEmailProof`: sobre un invitado sembrado con prueba, tras `RotateHandler` ⇒ `EmailProofHash` nulo.
    - `UpdateWithAnotherEmailVoidsTheEmailProof`: `UpdateHandler` con `Email = "otra@distri.co"` ⇒ nulo; con el mismo correo en
      mayúsculas ⇒ se conserva.
    (Si `DistributorWorld.SeedInvited` no acepta la prueba, sumarle `string? emailProofHash = null` y pasarla a `Invite`.)
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Wholesale/Modules.Wholesale.UnitTests` falla por `DistributorEmailProofs` y
  `PublishedInvitation.EmailProof`. Pegar la salida.
- [ ] **Step 3:** implementar. En los dos handlers, `var (emailProof, emailProofHash) = DistributorEmailProofs.Issue();` junto al
  token; el hash al dominio, la prueba sólo al evento. Las listas de campos de la auditoría no cambian. Actualizar los
  comentarios de `WholesaleEventPublisher` y de `IWholesaleEventPublisher` (el payload del correo lleva también la prueba; el del
  WhatsApp, no).
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests`: todas pasan.
- [ ] **Step 5: commit**

```powershell
git add src/Modules/Wholesale tests/Modules/Wholesale/Modules.Wholesale.UnitTests
git commit -m "feat(wholesale): publish an email proof with the invitation email"
```

### Task 4: El link del correo lleva `?ev=`

**Files:**
- Modify: `src/Modules/Notifications/Modules.Notifications.Application/AccountLinks.cs`
- Modify: `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/WholesaleInvitationDeliveryWorker.cs` (`ParsePayload`, `InvitationPayload`, armado del link)
- Test: `tests/Modules/Notifications/Modules.Notifications.UnitTests/WholesaleInvitationEmailTemplateTests.cs`,
  `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/WholesaleInvitationNotificationTests.cs`

**Interfaces:**
- Consumes: el campo `emailProof` del payload (tarea 3), leído por nombre con `JsonDocument`; Notifications sigue sin referenciar Wholesale.
- Produces: `public static string AccountLinks.WholesaleInvitation(string appBaseUrl, string token, string? emailProof = null)`;
  con prueba no vacía agrega `?ev={Uri.EscapeDataString(emailProof)}`.

- [ ] **Step 1: pruebas que fallan**
  - Unitaria `TheLinkCarriesTheEmailProofAsEvOnlyWhenThereIsOne` (theory):
    `("https://qep.qcode.co", "abc", "p_Q-1", "https://qep.qcode.co/wholesale-invitations/abc?ev=p_Q-1")`,
    `("http://localhost:3002/", "abc", "a+b/c=", "http://localhost:3002/wholesale-invitations/abc?ev=a%2Bb%2Fc%3D")`,
    `(…, "abc", null, "…/wholesale-invitations/abc")` y `(…, "abc", "", "…/wholesale-invitations/abc")`.
  - Integración `AnEmailProofInThePayloadGoesInTheLinkAsEv`: `Payload(...)` gana `string? emailProof = null` (se omite del JSON
    si es nulo); con `"prueba-1"` el `TextBody` contiene `{AppBaseUrl}/wholesale-invitations/tok_en-1?ev=prueba-1`. Las dos
    pruebas existentes, sin prueba, siguen esperando el link sin `ev`.
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Notifications/Modules.Notifications.UnitTests` falla por la sobrecarga de tres
  argumentos (compilación; sin contenedores).
- [ ] **Step 3:** implementar. El worker lee `emailProof` como `token` (opcional) y lo pasa a `AccountLinks.WholesaleInvitation`.
  Actualizar el comentario de cabecera del worker: tampoco se loguea la prueba.
- [ ] **Step 4: GREEN** — unitarias: `dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --filter "FullyQualifiedName~WholesaleInvitationEmailTemplateTests"`.
  Integración (un contenedor por prueba):
  `dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --filter "FullyQualifiedName~AnEmailProofInThePayloadGoesInTheLinkAsEv"`.
- [ ] **Step 5: fin de bloque B** — una vez, la clase entera: `--filter "FullyQualifiedName~WholesaleInvitationNotificationTests"`.
- [ ] **Step 6: commit**

```powershell
git add src/Modules/Notifications tests/Modules/Notifications
git commit -m "feat(notifications): carry the email proof in the wholesale invitation link"
```

## Bloque C — Crear la cuenta sin código

### Task 5: `WholesaleInvitationPasswordSession` acepta el buzón probado

**Files:**
- Modify: `src/Bootstrapper/WholesaleInvitationPasswordSession.cs`
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/WholesaleInvitationPasswordSessionTests.cs`

**Interfaces:**
- Produces: `Task<AuthenticatedUser> OpenAsync(string email, string password, string? code, bool emailProven, string? termsVersion, string correlationId, CancellationToken cancellationToken)`.
  Orden: contraseña (W17) → lock → con contraseña, login (la prueba no cuenta) → `emailProven` **o** código válido → términos →
  aprovisionar, contraseña, términos. Con `emailProven` no se llama `TryConsumeAsync`.

- [ ] **Step 1: pruebas que fallan** (el helper `OpenAsync` del test gana `bool emailProven = false`, así las existentes no cambian):
  - `AnEmailProofCreatesTheAccountWithoutACodeAndRecordsTheTerms`: `code: null, emailProven: true, Terms` ⇒ un `Provisioned`,
    `Consumed` vacío, `Accepted == [(provisioned, Password)]`, `Recorded == [(provisioned, Terms)]`, llamadas
    `["provision", "password", "terms"]` (sin `lookup`/`has-password`), `(1, 1, Email)` en scopes/commits/lock.
  - `AnEmailProofGivesAPasswordToAnExistingAccountWithoutOne`: `AddUser(Email, password: null)` ⇒ `Accepted` con ese id, sin aprovisionar otro.
  - `AnEmailProofDoesNotTouchAnAccountThatHasAPassword`: `AddUser(Email, Password)`; con `"Otra-Clave-99"` y `emailProven: true` ⇒
    401 `identity.credentials.invalid` y `Accepted` vacío; con `Password` ⇒ entra por `Logins` y `Accepted` sigue vacío.
  - `AnEmailProofStillNeedsTheCurrentTerms` (theory `null` ⇒ `ValidationException` con `TermsVersion`; `"2025-01-01"` ⇒
    `IdentityDomainException` `VersionMismatchCode`): `Provisioned`, `Accepted` y `Recorded` vacíos, `Commits == 0`.
- [ ] **Step 2: RED** — `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests` falla por la firma nueva. Pegar la salida.
- [ ] **Step 3:** implementar. La condición del 401 pasa a `!emailProven && (string.IsNullOrWhiteSpace(code) || !await TryConsumeAsync(...))`.
  Reescribir el `<summary>`: el link del correo sí puede probar el buzón (spec 2026-11-24), el copiado no; quien decide es
  Wholesale y aquí sólo llega el resultado. Hasta la tarea 6 el endpoint pasa `emailProven: false` para compilar.
- [ ] **Step 4: GREEN** — `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~WholesaleInvitationPasswordSessionTests"`.
- [ ] **Step 5: commit**

```powershell
git add src/Bootstrapper/WholesaleInvitationPasswordSession.cs src/Api/WholesaleInvitationEndpoints.cs tests/Bootstrapper/Bootstrapper.UnitTests/WholesaleInvitationPasswordSessionTests.cs
git commit -m "feat(wholesale): let a proven mailbox create the invitation account without a code"
```

### Task 6: `session-with-password` recibe `emailProof`

**Files:**
- Modify: `src/Api/WholesaleInvitationEndpoints.cs` (`SessionWithPasswordAsync`, `WholesaleInvitationPasswordRequest`, comentarios de la ruta)
- Modify: `tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests/WholesaleApiHarness.cs` (helper nuevo)
- Test: `tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests/InvitationAcceptanceApiTests.cs`, `DistributorApiTests.cs`
- Modify: `README.md` (tabla de la ruta, ~línea 2576, y la viñeta de `session-with-password`, ~2587), `.claude/rules/mayorista-compras.md` (viñeta «Sin contraseña, siempre hay código»)

**Interfaces:**
- Consumes: `DistributorEmailProofs.Proves` (tarea 3), `OpenAsync(..., bool emailProven, ...)` (tarea 5).
- Produces: `public sealed record WholesaleInvitationPasswordRequest(string? Email, string? Password, string? Code, string? TermsVersion, string? EmailProof = null)`;
  en el endpoint, después de resolver `email`, `var emailProven = DistributorEmailProofs.Proves(invitation, request.EmailProof, email);`.
  Harness: `public static async Task<string?> LatestEmailProofAsync(QepApiFactory factory, Guid distributorId)` (como `LatestTokenAsync`, campo `emailProof`).

- [ ] **Step 1: pruebas que fallan**
  - `InvitationAcceptanceApiTests`:
    - `TheEmailLinkProofCreatesTheAccountWithoutACode`: invitar, leer la prueba con `LatestEmailProofAsync`, `POST` sin `code`
      con `{ email, password, termsVersion, emailProof }` ⇒ 200, `Set-Cookie`, `identity.terms_acceptances` con la versión, y
      después `AcceptNewTenantAsync` ⇒ 200.
    - `AnEmailProofWithAnotherEmailOrAWrongProofIs401AndCreatesNoUser`: otro correo con la prueba válida, y el invitado con
      `"prueba-falsa"` ⇒ 401 `identity.credentials.invalid` los dos y `CountUsersAsync` en 0 para ambos correos.
    - `TermsMissingOrStaleWithAValidProofAre422AndCreateNoUser`: sin `termsVersion` y con `"2025-01-01"` ⇒ 422, 0 usuarios.
    - `AProofDoesNotChangeThePasswordOfAnExistingAccount`: `PasswordSessionAsync` crea la cuenta del invitado con código; luego la
      prueba con otra contraseña ⇒ 401, y la contraseña original sigue entrando.
    - Ampliar `TheTokenNeverAppearsInAuditOrRequestLogs` (o una hermana `TheEmailProofNeverAppearsInAuditOrRequestLogs`): la
      prueba no aparece en `platform.request_failures` ni en `audit.entries` (misma consulta `strpos`), ni en el cuerpo del `GET`
      de la invitación, ni en `invitationUrl` de la respuesta de invitar, ni en el payload de `wholesale.distributor-invitation-link.v1`
      cuando se invitó con teléfono.
  - `DistributorApiTests`: `AnEmailProofIsVoidedByCopyResendAndAnEmailChange` con `InviteForUpdateAsync`, `RotateAsync`,
    `ResendAsync` y `PutAsync`, un distribuidor por caso: tras «Copiar», el token nuevo con la prueba vieja ⇒ 401; tras
    «Reenviar», la prueba vieja ⇒ 401 y después la nueva ⇒ 200; tras cambiar el correo, la prueba con el correo viejo y con el
    nuevo ⇒ 401. Cada 401 sin usuario creado.
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests` falla por `LatestEmailProofAsync`
  (compilación; sin contenedores). Con el helper ya escrito y el endpoint todavía pasando `false`, la corrida filtrada de
  `TheEmailLinkProofCreatesTheAccountWithoutACode` falla con 401: pegar esa salida también.
- [ ] **Step 3:** implementar el endpoint. Actualizar los comentarios de `MapPost(".../session-with-password")` y del orden de
  `SessionWithPasswordAsync` (prueba de buzón o código; la prueba no se registra en ningún lado) y el `<summary>` del request.
  Documentar en el README el campo `emailProof` y el link `?ev=`; en la regla del módulo, reemplazar «Sin contraseña, siempre hay
  código» por: sin contraseña hace falta el código **o** la prueba del link del correo, con las tres anulaciones y el riesgo
  aceptado del spec (reenviar el correo entrega ese paso).
- [ ] **Step 4: GREEN** — mientras se itera, por nombre:
  `dotnet test tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests --filter "FullyQualifiedName~TheEmailLinkProofCreatesTheAccountWithoutACode|FullyQualifiedName~AnEmailProofIsVoidedByCopyResendAndAnEmailChange"` (y el resto de las nuevas).
- [ ] **Step 5: fin de bloque C** — una vez cada clase: `--filter "FullyQualifiedName~InvitationAcceptanceApiTests"` y luego
  `--filter "FullyQualifiedName~DistributorApiTests"`. Después, la compuerta de merge: `.\scripts\test-feature.ps1`, leyendo sus avisos.
- [ ] **Step 6: commit**

```powershell
git add src/Api/WholesaleInvitationEndpoints.cs tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests README.md .claude/rules/mayorista-compras.md
git commit -m "feat(wholesale): accept the email proof on session-with-password"
```

## Cierre

- Revisión fuerte de toda la rama (modelo más capaz) contra el spec y este plan.
- Merge a `develop` (nunca `main`) con `test-feature.ps1` en verde; en `develop`, actualizar `docs/estado.md` (fila y Registro).
- Despliegue en cualquier orden respecto del frontend (spec § Despliegue). Durante el rollout, un pod viejo que reenvía o edita
  el correo no toca `email_proof_hash`; la ventana es corta y lo acotan las 72 h.

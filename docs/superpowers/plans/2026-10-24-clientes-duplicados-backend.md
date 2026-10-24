# Clientes duplicados — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que nadie cree un duplicado de documento sin saber de quién es, que el asesor vea y cambie en el hilo el cliente de
un número compartido, que una persona reciba una sola plantilla por campaña y que exista una lista de los casos raros.

**Architecture:** En Customers, el 422 del índice de identificación se atrapa en los dos handlers y sale con `existing`;
una consulta de coincidencias por número normalizado (índice de expresión nuevo) y otra de anomalías agrupadas, las dos en
un puerto de lectura propio con SQL crudo. En Messaging, el resumen de una sola conversación lleva los otros clientes del
número (puerto nuevo con adaptador en Bootstrapper), un `PUT …/customer` con `If-Match` reutiliza el camino de tomar y
transferir, `ConversationOpener` busca primero la conversación que recibe las respuestas, y el congelado de una campaña
termina con un `UPDATE` que deja un solo `Pending` por contacto antes de que el worker pueda reclamar nada.

**Tech Stack:** .NET 10, EF Core + Npgsql (PostgreSQL 18), FluentValidation, xUnit v3 + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-10-24-clientes-duplicados-design.md` (léelo entero, en especial §2, §3, §4, §6, §9 y
§10). El frontend tiene plan aparte y depende de la sección «Contrato para el frontend» al final de este plan.

**Rama:** `feature/clientes-duplicados`, en el worktree `.claude/worktrees/clientes-duplicados`. Antes de **cada** tarea y
de **cada** commit, `git branch --show-current` tiene que decir esa rama; si dice `main` o `develop`, para. Nunca
`git stash`. No toques el checkout principal.

## Global Constraints

- TDD obligatorio: RED con la salida literal antes del GREEN, los dos en el reporte de cada tarea.
- Conventional commits en español, con el slice `CD-NN` en el asunto, **sin atribución de IA** (sin `Co-Authored-By`; la
  regla del repo pisa cualquier recordatorio del harness).
- No corras la suite completa: cada comando lleva `--filter "FullyQualifiedName~<Clase>"`. Docker corriendo para las de
  integración. `Api.exe`/`dotnet run` corriendo bloquea `build`, `test` y `ef`: detenlo primero.
- Las pruebas de integración en la máquina del developer necesitan, en línea aparte: `$env:Seed__Enabled = "false"`.
  Messaging se prueba con el stub de auth (los harness ya lo hacen).
- Formato: `dotnet format --verify-no-changes --include <tus archivos>` (no el repo entero).
- Aislamiento de tenant en doble capa: ruta `{tenantId:guid}` + política, **y** `CustomersAuthorization.EnsureAuthorized` /
  `MessagingAuthorization.EnsureAsync` en el handler **antes** de validar o tocar un repositorio. Otro tenant → 403, nunca 404.
  Todo método de repositorio y de puerto recibe `tenantId`.
- Sin permisos nuevos (spec, encabezado): las rutas de Customers usan `CustomersPermissions.CustomerRead`; la de Messaging,
  `MessagingPermissions.ConversationManage`. Los handlers se registran a mano en `QepServiceCollectionExtensions`; sin
  registro, 500.
- Texto libre en un comando o una consulta → validador de FluentValidation (422 `validation.failed` con `errors`). Ningún
  status a mano: lo mapea `src/Api/ApiExceptionHandler.cs`.
- Traducir errores de base va en Infrastructure, por **nombre de índice** (`CustomersUnitOfWork` ya lo hace con
  `IX_customers_tenant_identification`). `Modules.Customers.Application` y `Modules.Messaging.Application` no referencian EF
  ni Npgsql (`CustomersLayerTests`, `MessagingLayerTests`). Messaging no referencia Customers: todo entra por
  `IMessagingCustomerDirectory` con adaptador en `src/Bootstrapper/MessagingCustomerDirectory.cs`.
- Migraciones con el factory de diseño, nunca con `--startup-project`:
  `dotnet ef migrations add <Nombre> --project src/Modules/<Modulo>/Modules.<Modulo>.Infrastructure --context <Modulo>DbContext -o Persistence/Migrations`.
- BFF: cada DTO nuevo dice en su `<summary>` por qué tiene esa forma. Las colecciones de tamaño fijo viajan completas,
  incluso en cero (`counts`). Los enums viajan con su nombre (`SharedPhone`, `OtherType`, `CustomerChanged`).
- Textos para personas y comentarios en español colombiano tuteando; códigos, rutas, enums y permisos tal cual. Texto
  fijado por el spec: `duplicate_contact` → «Otro cliente de la campaña tiene el mismo WhatsApp.».
- `docs/estado.md` **no se toca** en esta rama. `Directory.Packages.props`, `global.json`, `.mcp.json`: no se tocan. Sin
  paquetes nuevos.

## Desvíos del spec (verificados contra el código el 2026-10-24)

La autoridad es el código. Ninguno cambia una regla del owner (R1–R3, §10); son la forma de construirlas.

| # | Spec dice | El código dice | El plan hace |
|---|---|---|---|
| A1 | §3.1 (ya resuelto) | `CreateCustomer.cs:138-142` descarta el pre-chequeo («el único árbitro real son los índices») | Sin pre-chequeo: se atrapa el `CustomersDomainException("customers.customer.identification_taken")` alrededor de `SaveChangesAsync` y se relee al dueño. `UpdateCustomer` tiene **dos** `SaveChangesAsync` (completar, `:132`, y editar, `:175`): se atrapan los dos (CD-01) |
| A2 | §4.2: el evento lleva los nombres «en `Previous`/`Target`» | `MessageMapping.MemberIdsOf` (`MessagingSupport.cs:264-289`) trata `Actor`/`Target`/`Previous` como **membresías** (un id de cliente ahí sale «Miembro eliminado»), y el parser del frontend de hoy exige `memberId` en `previous`/`target`: uno sin él lanza y rompe el hilo entero | `ConversationEvent` suma `PreviousCustomerId`; el cliente nuevo va en el `CustomerId` que ya existe. `MessageEventDto` suma `targetCustomer` y `previousCustomer` (`{ id, name }`), y en `CustomerChanged` deja `previous`/`target` en `null` (CD-08) |
| A3 | §4.2: «se graba en el hilo … (`ConversationEventRows`)» | `ConversationEventRows` es el escritor SQL de la ingesta; los handlers graban con `IConversationEvents.Record` dentro de `ConversationAssignmentSteps.CommitAsync`, que ya separa `occurred_at` por evento | El handler nuevo usa `CommitAsync` (evento, auditoría, `SaveChanges`, notifier) como tomar y transferir (CD-09) |
| A4 | §4.1 (ya corregido en el spec): toda respuesta de una sola conversación, y nombra detalle, `PUT …/customer` y las transiciones | Además de esas, `POST /conversations` (iniciar) devuelve un resumen de una sola conversación (`StartConversation.cs:120`); y el resumen se arma en seis sitios con el mismo `summaries.BuildAsync(…, [row])[0]` (`GetConversation.cs:23`, `ConversationAssignment.cs:68` —que cubre tomar, transferir, liberar, pausar y devolver—, `ConversationLifecycle.cs:190,223`, `StartConversation.cs:120`) | Un solo lugar: `ConversationSummaryBuilder.BuildOneAsync` hace la consulta del número y los seis llamadores pasan a usarlo, iniciar incluido; `ListConversations` y `SearchMessages` siguen con `BuildAsync` y la lista vacía (CD-07) |
| A5 | §4.1: `ListByPhoneAsync(tenantId, waId)` sin tope | El tope de 10 sólo se puede cumplir si el puerto acota la consulta | Los dos puertos reciben `take`; Messaging pide `10 + 1` y excluye al cliente de la conversación (CD-06, CD-07) |
| A6 | §4.4: opt-out por número «para … el envío uno a uno de una plantilla `MARKETING`» | El envío humano (`SendTemplateMessage`, `StartConversation`) **no** mira `MarketingOptOut` hoy; lo que lee `FindContactAsync` es la re-verificación por destinatario del worker (`CampaignDispatch.cs:244-284`, R6/R7) | «Uno a uno» se construye y se prueba como esa re-verificación. El envío humano no cambia (pregunta 2 al owner) |
| A7 | §4.4: destinatarios «que comparten `wa_id` o `user_id`» | `IX_customers_tenant_whatsapp_user_id` es único por tenant: dos destinatarios de una campaña nunca comparten `user_id` | La ventana es por `wa_id` (no nulo); se anota en el SQL por qué `user_id` no entra |
| A8 | §4.4 «gana el más viejo: `created_at` y luego id»; §7 «sin migración en Messaging» | `campaign_recipients` no guarda la fecha de alta del cliente, y Messaging no lee el esquema `customers` (el SQL entre esquemas vive en Bootstrapper) | Columna nueva `customer_created_at` (nullable) en `campaign_recipients`, llenada al congelar desde `CustomerContact.CreatedAt`: **una migración en Messaging** (CD-12) |
| A9 | §4.4: «verifica que ningún destinatario se admite antes de que el congelado termine» | `CampaignDispatchStore.ClaimAsync` (`:105-110`) exige `r.status = 'Pending'` y `c.status = 'Sending'`, **no** `audience_frozen_at`: otro pod que está en las rondas de reclamo de su pasada puede tomar el primer lote de una campaña que este pod todavía congela | El reclamo exige `c.audience_frozen_at IS NOT NULL` (CD-11) y el `UPDATE` de duplicados corre en `MarkFrozenAsync`, en la misma transacción que marca el congelado y antes de marcarlo (CD-12) |
| A10 | §4.2: `422 messaging.conversation.customer_not_found` | El código no existe (`messaging.customer.not_found` es el 404 de iniciar conversación) | Constante nueva `MessagingErrorCodes.ConversationCustomerNotFound`; texto del validador propuesto: «Elige el cliente de la conversación.» |
| A11 | §3.2: `type` opcional, sin decir qué pasa con un valor desconocido | `IdentificationTypeParser.Parse` lanza con un valor desconocido | `type` desconocido → `422 validation.failed` con `errors.Type`; `number` de más de 32 (`CustomerIdentification.NumberMaxLength`) → `errors.Number` |

## Review Focus

1. **Tomar o resolver no borra la línea «Este número también es de…».** La respuesta de `take` (y de las demás mutaciones de
   una conversación) trae los mismos `sharedPhoneCustomers` que el detalle. Prueba: `TakingKeepsTheSharedPhoneCustomersInTheAnswer` (CD-07).
2. **Conversación vieja sin `customer_id`** (el cliente sale por teléfono en `ToSummary`): la lista excluye al cliente que el
   encabezado ya muestra, no al `null`. Prueba: `ALegacyConversationDoesNotListItsOwnCustomerAsShared` (CD-07).
3. **Dos pods y una campaña a medio congelar.** Nada de una campaña sin `audience_frozen_at` se reclama, aunque ya tenga
   `Pending` escritos. Prueba: `NothingIsClaimedBeforeTheAudienceIsFrozen` (CD-11).
4. **La consulta de coincidencias deja de usar el índice** porque su expresión ya no es la de la migración: el aviso sigue
   funcionando y nadie lo nota hasta que el padrón crece. Prueba: `TheMatchQueryUsesTheNormalizedIndex` (CD-02).
5. **Un número que sólo tiene signos** (`"---"`, `" . "`): normalizado queda vacío y no puede coincidir con todos los
   clientes cuyo número también normaliza a vacío. Prueba: `AnInputThatNormalizesToNothingMatchesNoOne` (CD-03).

---

## Mapa de archivos

| Archivo | Acción | Responsabilidad |
| --- | --- | --- |
| `src/Modules/Customers/Modules.Customers.Application/CustomerIdentificationTaken.cs` | Crear | Excepción con `existing` y la relectura del dueño |
| `src/Modules/Customers/Modules.Customers.Application/CreateCustomer.cs`, `UpdateCustomer.cs` | Modificar | Atrapar el 422 del índice alrededor de cada `SaveChangesAsync` |
| `src/Modules/Customers/Modules.Customers.Application/CustomersDtos.cs` | Modificar | `existing`, coincidencias y anomalías |
| `src/Modules/Customers/Modules.Customers.Application/CustomerDuplicates.cs` | Crear | Normalizador, enums, queries, validadores y handlers de §3.2 y §3.3 |
| `src/Modules/Customers/Modules.Customers.Application/ICustomerDuplicateQueries.cs` | Crear | Puerto de lectura de §3.2 y §3.3 |
| `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerDuplicateQueries.cs` | Crear | SQL crudo de coincidencias y anomalías; la expresión normalizada en una constante |
| `…/Persistence/Migrations/*_AddCustomerIdentificationNormalizedIndex.cs` | Crear (ef) | El índice de expresión |
| `src/Modules/Customers/Modules.Customers.Infrastructure/CustomersInfrastructureExtensions.cs` | Modificar | Registro del puerto |
| `src/Modules/Customers/Modules.Customers.Api/CustomerEndpoints.cs` | Modificar | `GET /identification-matches`, `GET /anomalies` |
| `src/Modules/Customers/Modules.Customers.Application/ICustomerPhoneDirectory.cs`, `…/Infrastructure/Persistence/CustomerPhoneDirectory.cs` | Modificar | `ListByPhoneAsync` |
| `src/Modules/Customers/Modules.Customers.Application/CustomerEngagementPorts.cs`, `…/Infrastructure/Persistence/CustomerAudienceRepository.cs` | Modificar | Opt-out por número; `CustomerContact.CreatedAt` |
| `src/Modules/Messaging/Modules.Messaging.Application/IMessagingCustomerDirectory.cs`, `src/Bootstrapper/MessagingCustomerDirectory.cs` | Modificar | `ListByPhoneAsync`, `MessagingCustomerContact.CreatedAt` |
| `src/Modules/Messaging/Modules.Messaging.Application/MessagingDtos.cs`, `MessagingSupport.cs` | Modificar | `SharedPhoneCustomers`, `BuildOneAsync`, refs de cliente en eventos |
| `src/Modules/Messaging/Modules.Messaging.Application/GetConversation.cs`, `ConversationAssignment.cs`, `ConversationLifecycle.cs`, `StartConversation.cs`, `ListMessages.cs` | Modificar | `BuildOneAsync`; nombres de clientes en el hilo; handler nuevo |
| `src/Modules/Messaging/Modules.Messaging.Domain/ConversationEvent.cs`, `Conversation.cs`, `MessagingErrorCodes.cs`, `CampaignFailureReasons.cs` | Modificar | `CustomerChanged`, `ChangeCustomer`, códigos |
| `src/Modules/Messaging/Modules.Messaging.Application/ConversationEventJson.cs`, `IMessagingAuditRecorder.cs` | Modificar | `previousCustomerId`, acción de auditoría |
| `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs` | Modificar | `PUT /conversations/{id}/customer` |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ConversationOpener.cs` | Modificar | Orden nuevo de `FindAsync` |
| `src/Modules/Messaging/Modules.Messaging.Application/CampaignDispatch.cs` | Modificar | `CampaignRecipientSeed.CustomerCreatedAt`, `Seed`, contrato de `MarkFrozenAsync` |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Campaigns/CampaignDispatchStore.cs` | Modificar | Reclamo sólo congelado; `customer_created_at`; `UPDATE` de duplicados |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/CampaignRecords.cs`, `MessagingDbContext.cs`, `…/Migrations/*_AddCampaignRecipientCustomerCreatedAt.cs` | Modificar / Crear (ef) | La columna |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` | Modificar | Handlers de Customers (`:295-306`) y Messaging (`:721-724`) |
| `README.md`, `CLAUDE.md` | Modificar | Documentación (CD-13) |

**Comandos de prueba** (en PowerShell, `$env:Seed__Enabled = "false"` en línea aparte antes de las de integración):

```powershell
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CustomersLayerTests|FullyQualifiedName~MessagingLayerTests"
```

---

### Task 1 (CD-01): El 422 de documento repetido dice quién es el dueño

**Files:**
- Create: `src/Modules/Customers/Modules.Customers.Application/CustomerIdentificationTaken.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomersDtos.cs` (record nuevo al final)
- Modify: `src/Modules/Customers/Modules.Customers.Application/CreateCustomer.cs:138-142`, `UpdateCustomer.cs:132,175`
- Create (dobles): `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerWriteDoubles.cs` — mueve ahí, sin cambiar su
  comportamiento, los dobles privados de `CustomerProvisioningTests.cs:19-150` (`FixedClock`, `PlusNormalizer`,
  `RecordingAudit`, `UnitOfWork`, `Classifications`, `Geography`, `Cucs`, `Customers`) como `internal`, y suma a `Customers`
  el contador `IdentificationLookups`. `CustomerProvisioningTests` los usa desde ahí.
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerWriteHandlersTests.cs`,
  `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerDuplicateIdentificationApiTests.cs`

**Interfaces:**
- Consumes: `IHasProblemExtensions` (`BuildingBlocks.Domain`), `ApiExceptionHandler.cs:77-84` (ya publica las extensiones con `TryAdd`).
- Produces:
  ```csharp
  // CustomersDtos.cs — la forma del POS (PosCustomerOptionResponse, PosDtos.cs:168) más Cuc, que puede ser null en un incompleto.
  public sealed record CustomerIdentificationOwnerDto(
      Guid Id, string Name, string IdentificationType, string IdentificationNumber, string? Cuc, bool IsComplete, bool IsActive);

  // CustomerIdentificationTaken.cs
  public sealed class CustomerIdentificationTakenException(CustomerIdentificationOwnerDto existing)
      : DomainException("customers.customer.identification_taken", "Another customer in this tenant already uses that identification."),
        IHasProblemExtensions;   // ProblemExtensions = { ["existing"] = existing }

  internal static class CustomerIdentificationTaken
  {
      // Relee al dueño del par (tipo, número) ya normalizado por el agregado, con FindExistingIdentificationsAsync + FindAsync
      // (incluye inactivos e incompletos). Con dueño → CustomerIdentificationTakenException; sin dueño (lo borraron en el medio)
      // → vuelve a lanzar la excepción original, sin existing (§3.1).
      public static Task<Exception> ResolveAsync(ICustomerRepository repository, Guid tenantId, Customer attempted,
          CustomersDomainException original, CancellationToken cancellationToken);
  }
  ```
- Los handlers envuelven **cada** `SaveChangesAsync` en
  `catch (CustomersDomainException e) when (e.Code == "customers.customer.identification_taken") { throw await CustomerIdentificationTaken.ResolveAsync(...); }`.
  `CustomersUnitOfWork` ya limpió el tracker (`:66`), así que la relectura corre en el mismo scope. El comentario de
  `CreateCustomer.cs:138-141` suma que el dueño se relee **después** del índice, nunca antes (spec §3.1).

- [ ] **Step 1: Escribe las pruebas que fallan**

`CustomerWriteHandlersTests.cs` (con `CustomerWriteDoubles`, `EngagementTestBed.Context` y `EngagementTestBed.FakeTags`):
```csharp
[Fact] public async Task CreateWithATakenDocumentThrowsWithTheOwner()
// UnitOfWork lanza identification_taken; Customers.Existing = dueño inactivo e incompleto con CC 1020304050
// → CustomerIdentificationTakenException; Code "customers.customer.identification_taken";
//   ProblemExtensions["existing"] == new CustomerIdentificationOwnerDto(dueño.Id, nombre, "CC", "1020304050", null, false, false)
[Fact] public async Task UpdateAndCompleteWithATakenDocumentThrowWithTheOwner()   // los dos caminos de UpdateCustomer (completo e incompleto)
[Fact] public async Task WhenTheOwnerCannotBeReadBackTheOriginal422GoesOutWithoutExisting()
// Existing = null → CustomersDomainException (no CustomerIdentificationTakenException), mismo código
[Fact] public async Task AnUpdateThatSavesNeverLooksUpTheIdentification()   // IdentificationLookups == 0 tras un PUT que guarda
```
`CustomerDuplicateIdentificationApiTests.cs` (harness `CustomersApiHarness`, `CreateManager`):
```csharp
[Fact] public async Task CreateWithAnotherCustomersDocumentReturnsTheOwnerAsExisting()
// alta "GYSELYS VIVAS PACHECO" NIT 92-1396892 → segunda alta con el mismo par → 422, code identification_taken,
// existing { id, name, identificationType "NIT", identificationNumber "92-1396892", cuc (no vacío), isComplete true, isActive true }
[Fact] public async Task UpdateToAnotherCustomersDocumentReturnsTheOwnerAsExisting()
[Fact] public async Task AnInactiveOwnerComesBackWithIsActiveFalse()        // POST /{id}/deactivate del dueño antes
[Fact] public async Task AnIncompleteOwnerWithDocumentComesBackWithoutCuc() // dueño por SQL: completeness 'Incomplete', identification_type 'Nit', sin CUC → existing.cuc null, isComplete false
[Fact] public async Task TheProblemKeepsCodeAndTraceIdAndHasNoErrors()      // JSON con code, traceId, existing; sin errors
```
`CreateRejectsADuplicateIdentification` (`CustomerWriteApiTests.cs:265`) no cambia: el contrato del código se conserva (D-D1).

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~CustomerWriteHandlersTests|FullyQualifiedName~CustomerProvisioningTests"`
Expected: FAIL de compilación (`CustomerIdentificationTakenException`, `CustomerIdentificationOwnerDto` no existen).

- [ ] **Step 3: Implementa** el DTO, la excepción, `ResolveAsync` y los tres `catch`.

- [ ] **Step 4: Corre y verifica que pasan** (las dos clases unitarias, más
  `Customers.IntegrationTests --filter "FullyQualifiedName~CustomerDuplicateIdentificationApiTests|FullyQualifiedName~CustomerWriteApiTests"`).
Expected: PASS; `CustomerProvisioningTests` sigue verde con los dobles movidos.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Customers tests/Modules/Customers
git commit -m "feat(customers): CD-01 el 422 de documento repetido trae al dueño en existing"
```

---

### Task 2 (CD-02): Índice `IX_customers_tenant_identification_normalized` y la búsqueda normalizada

**Files:**
- Create: `src/Modules/Customers/Modules.Customers.Application/ICustomerDuplicateQueries.cs`
- Create: `src/Modules/Customers/Modules.Customers.Application/CustomerDuplicates.cs` (por ahora sólo el normalizador)
- Create: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerDuplicateQueries.cs`
- Create (ef): `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/Migrations/*_AddCustomerIdentificationNormalizedIndex.cs` (+ Designer)
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/CustomersInfrastructureExtensions.cs` (registro scoped)
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerIdentificationNormalizerTests.cs`,
  `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerDuplicateQueriesTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  // CustomerDuplicates.cs — misma regla que la expresión SQL: sólo letras y dígitos ASCII, en mayúsculas.
  public static class CustomerIdentificationNormalizer { public static string Normalize(string? number); }

  // ICustomerDuplicateQueries.cs — Spec 2026-10-24 §3.2 y §3.3: lecturas que no caben en ICustomerRepository porque
  // comparan por una expresión SQL. Todo recibe tenantId.
  public interface ICustomerDuplicateQueries
  {
      /// Los clientes del tenant con tipo y número cuyo número normalizado es normalizedNumber, sin excludeCustomerId,
      /// ordenados por created_at, id, hasta take. Incluye inactivos e incompletos.
      Task<IReadOnlyList<Customer>> FindByNormalizedIdentificationAsync(
          Guid tenantId, string normalizedNumber, Guid? excludeCustomerId, int take, CancellationToken cancellationToken);
  }

  // CustomerDuplicateQueries.cs (Infrastructure)
  internal static class CustomerIdentificationSql
  {
      /// La única copia de la expresión del índice en código; la migración la repite literal (las migraciones no leen
      /// constantes) y TheMatchQueryUsesTheNormalizedIndex amarra que coincidan.
      public const string NormalizedNumber = "upper(regexp_replace(identification_number, '[^0-9A-Za-z]', '', 'g'))";
  }
  ```
- Consulta: `dbContext.Customers.FromSqlRaw($"SELECT * FROM customers.customers WHERE tenant_id = {{0}} AND identification_type IS NOT NULL AND identification_number IS NOT NULL AND {CustomerIdentificationSql.NormalizedNumber} = {{1}}", tenantId, normalized)`
  (con `SELECT *`, así la regla de listar todas las columnas del `FromSql` se cumple sola), y encima LINQ: `AsNoTracking`,
  `Where(id != exclude)`, `OrderBy(CreatedAt).ThenBy(Id)`, `Take(take)`.
- Migración (genera vacía con el comando de Global Constraints y escribe a mano, como `AddCustomerEmailIndex`):
  `CREATE INDEX "IX_customers_tenant_identification_normalized" ON customers.customers (tenant_id, upper(regexp_replace(identification_number, '[^0-9A-Za-z]', '', 'g'))) WHERE identification_number IS NOT NULL;`
  y `Down` con `DROP INDEX IF EXISTS customers."IX_customers_tenant_identification_normalized";`. El comentario dice que no es
  único (R2 se avisa, no se frena) y que la expresión vive en `CustomerIdentificationSql`.

- [ ] **Step 1: Escribe las pruebas que fallan**

`CustomerIdentificationNormalizerTests.cs`:
```csharp
[Theory]
[InlineData("92-1396892", "921396892")] [InlineData(" 900.123.456-1 ", "9001234561")] [InlineData("ab-1", "AB1")]
[InlineData("ÑA 12", "A12")] [InlineData("---", "")] [InlineData(null, "")]
public void NormalizeKeepsOnlyAsciiLettersAndDigitsInUpperCase(string? input, string expected)
```
`CustomerDuplicateQueriesTests.cs` (siembra por SQL, como `CustomerAudienceDirectoryIntegrationTests.InsertCustomersAsync`):
```csharp
[Fact] public async Task TheIndexIsTheNormalizedExpressionPartialOnTheNumber()
// pg_indexes.indexdef de IX_customers_tenant_identification_normalized contiene "regexp_replace" y "WHERE (identification_number IS NOT NULL)"
[Fact] public async Task TheMatchQueryUsesTheNormalizedIndex()
// SET enable_seqscan = off; EXPLAIN de la consulta (misma constante) → el plan nombra IX_customers_tenant_identification_normalized
[Fact] public async Task AMatchIgnoresPunctuationAndCaseAndKeepsTheTenant()
// "92-1396892" (Nit) y "921396892" (Ce) en el tenant, "92 139 6892" en otro → FindByNormalizedIdentificationAsync(t, "921396892") trae los dos del tenant, el más viejo primero
[Fact] public async Task TheExcludedCustomerAndRowsWithoutDocumentDoNotCome()
[Fact] public async Task InactiveAndIncompleteOwnersCome()
[Fact] public async Task TheTakeCapsTheRows()                                  // 25 con el mismo número → take 20 → 20
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~CustomerIdentificationNormalizerTests"`
Expected: FAIL de compilación (`CustomerIdentificationNormalizer` no existe).

- [ ] **Step 3: Implementa** el normalizador, el puerto, la clase, el registro y la migración.

- [ ] **Step 4: Corre y verifica que pasan** (la unitaria y
  `Customers.IntegrationTests --filter "FullyQualifiedName~CustomerDuplicateQueriesTests|FullyQualifiedName~CustomerCompletenessMigrationTests"`).
Expected: PASS; las pruebas de migración existentes siguen subiendo.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Customers tests/Modules/Customers
git commit -m "feat(customers): CD-02 índice normalizado del número de documento"
```

---

### Task 3 (CD-03): `GET /customers/identification-matches`

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerDuplicates.cs`, `CustomersDtos.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Api/CustomerEndpoints.cs` (ruta junto a `GET /`, `:33-36`; método al final)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:295-306`
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerIdentificationMatchesHandlerTests.cs`,
  `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerIdentificationMatchesApiTests.cs`

**Interfaces:**
- Consumes: `ICustomerDuplicateQueries.FindByNormalizedIdentificationAsync`, `CustomerIdentificationNormalizer` (CD-02).
- Produces:
  ```csharp
  public enum CustomerIdentificationMatchKind { SameIdentification, OtherType, SameTypeOtherFormat }

  // CustomersDtos.cs
  /// BFF (§3.2): Match lo calcula el servidor para que la pantalla elija el color por un campo y no comparando tipos.
  public sealed record CustomerIdentificationMatchDto(
      Guid Id, string Name, string IdentificationType, string IdentificationNumber, string? Cuc, bool IsComplete, bool IsActive, string Match);
  /// Siempre items, vacía sin coincidencias; sin total: es un aviso, no un listado.
  public sealed record CustomerIdentificationMatchesResponse(IReadOnlyList<CustomerIdentificationMatchDto> Items);

  public sealed record FindCustomerIdentificationMatchesQuery(Guid TenantId, string? Number, string? Type, Guid? ExcludeCustomerId)
      : IQuery<CustomerIdentificationMatchesResponse>;
  public static class CustomerIdentificationMatching { public const int Take = 20; }
  ```
- Validador público `FindCustomerIdentificationMatchesValidator`: `Number` no vacío y ≤ `CustomerIdentification.NumberMaxLength`;
  `Type`, cuando viene, ∈ `IdentificationTypeParser.SupportedWireValues` (sin distinguir mayúsculas). Claves `Number`, `Type`.
- Handler: `EnsureAuthorized(CustomerRead)` → validar → `Normalize(number)`; vacío → `items: []` sin consultar → consulta con
  `Take` → clasifica cada fila: sin `type` → `OtherType`; tipo distinto → `OtherType`; mismo tipo y número ordinal igual a
  `number.Trim()` → `SameIdentification`; mismo tipo y otro texto → `SameTypeOtherFormat`. `identificationType` sale con
  `ToWireValue()`.
- Ruta: `group.MapGet("/identification-matches", …)` con `?number=&type=&excludeCustomerId=`,
  `RequireAuthorization(CustomersPermissions.CustomerRead)`, `Produces<CustomerIdentificationMatchesResponse>()`, 403, 422.

- [ ] **Step 1: Escribe las pruebas que fallan**

`CustomerIdentificationMatchesHandlerTests.cs` (doble de `ICustomerDuplicateQueries` en el archivo):
```csharp
[Theory]
[InlineData("NIT", "92-1396892", "SameIdentification")] [InlineData("CE", "92-1396892", "OtherType")]
[InlineData("NIT", "921396892", "SameTypeOtherFormat")] [InlineData(null, "92-1396892", "OtherType")]
public async Task EachCandidateGetsItsMatch(string? type, string number, string expected)   // candidato: Nit "92-1396892"
[Fact] public async Task AnInputThatNormalizesToNothingMatchesNoOne()            // "---" → items [] y el doble no fue llamado
[Fact] public async Task AnotherTenantIs403BeforeValidating()                    // número vacío + otro tenant → RequestForbiddenException, no ValidationException
[Fact] public async Task ItAsksForTwentyAndPassesTheExclusion()
```
`CustomerIdentificationMatchesApiTests.cs`:
```csharp
[Fact] public async Task TheFormSeesTheOtherTypeBeforeSaving()
// GYSELYS NIT 92-1396892 → GET ?number=92-1396892&type=CE → 200 items[0] { name, identificationType "NIT", cuc, match "OtherType" }
[Fact] public async Task ExcludeCustomerIdLeavesOutTheOneBeingEdited()
[Fact] public async Task WithoutMatchesItemsIsEmpty()                             // 200 {"items":[]}
[Fact] public async Task AnEmptyNumberIs422OnNumberAndAnUnknownTypeIs422OnType()  // errors.Number / errors.Type
[Fact] public async Task AnotherTenantIs403AndWithoutReadIs403()
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~CustomerIdentificationMatchesHandlerTests"`
Expected: FAIL de compilación (query, handler y DTOs no existen).

- [ ] **Step 3: Implementa** enum, DTOs, query, validador, handler, ruta y registro del handler.

- [ ] **Step 4: Corre y verifica que pasan** (la unitaria y `CustomerIdentificationMatchesApiTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Customers src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Customers
git commit -m "feat(customers): CD-03 consulta de coincidencias de documento para avisar antes de guardar"
```

---

### Task 4 (CD-04): `GET /customers/anomalies`

**Files:**
- Modify: `ICustomerDuplicateQueries.cs`, `CustomerDuplicateQueries.cs`, `CustomerDuplicates.cs`, `CustomersDtos.cs`, `CustomerEndpoints.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:295-306`
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerAnomaliesApiTests.cs`,
  `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerAnomaliesHandlerTests.cs`

**Interfaces:**
- Consumes: `CustomerIdentificationSql.NormalizedNumber` (CD-02).
- Produces:
  ```csharp
  public enum CustomerAnomalyKind { SharedPhone, SharedIdentificationNumber }
  public static class CustomerAnomalyPaging   // propias: un grupo pesa hasta 50 clientes (spec §3.3), no se reutiliza CustomerPaging
  { public const int DefaultPageSize = 20; public const int MaxPageSize = 50; public const int GroupCustomersCap = 50; }

  // ICustomerDuplicateQueries — suma:
  public sealed record CustomerAnomalyGroupRow(string Key, int Size);
  Task<IReadOnlyDictionary<CustomerAnomalyKind, int>> CountAnomalyGroupsAsync(Guid tenantId, CancellationToken cancellationToken);
  Task<IReadOnlyList<CustomerAnomalyGroupRow>> ListAnomalyGroupsAsync(
      Guid tenantId, CustomerAnomalyKind kind, int skip, int take, CancellationToken cancellationToken);
  /// Por clave, a lo sumo perGroup + 1 clientes ordenados por created_at, id (la fila de más dice truncated).
  Task<IReadOnlyDictionary<string, IReadOnlyList<Customer>>> ListAnomalyGroupMembersAsync(
      Guid tenantId, CustomerAnomalyKind kind, IReadOnlyCollection<string> keys, int perGroup, CancellationToken cancellationToken);

  // CustomersDtos.cs
  /// counts viaja con las dos clases siempre, también en cero: las pestañas dibujan su contador sin conocer el enum.
  public sealed record CustomerAnomalyCountDto(string Kind, int Groups);
  public sealed record CustomerAnomalyCustomerDto(
      Guid Id, string Name, string? IdentificationType, string? IdentificationNumber, string? Cuc, string? Phone,
      bool IsComplete, bool IsActive, DateTimeOffset CreatedAt);
  /// Truncated existe siempre: un grupo de más de 50 se corta y la pantalla lo dice sin contar.
  public sealed record CustomerAnomalyGroupDto(string Key, IReadOnlyList<CustomerAnomalyCustomerDto> Customers, bool Truncated);
  /// Pagina grupos, no clientes; total = grupos de la clase pedida.
  public sealed record CustomerAnomaliesResponse(
      IReadOnlyList<CustomerAnomalyCountDto> Counts, IReadOnlyList<CustomerAnomalyGroupDto> Groups, int Total, int Page, int PageSize);

  public sealed record ListCustomerAnomaliesQuery(Guid TenantId, string? Kind, int Page, int PageSize) : IQuery<CustomerAnomaliesResponse>;
  ```
- SQL (crudo, `SqlQueryRaw` / `FromSqlRaw`): clave de `SharedPhone` = `phone_e164` (no nulo); de `SharedIdentificationNumber`
  = `CustomerIdentificationSql.NormalizedNumber` con `identification_number IS NOT NULL`. Grupos:
  `GROUP BY clave HAVING count(*) > 1 ORDER BY count(*) DESC, clave LIMIT/OFFSET`. Miembros: ventana
  `ROW_NUMBER() OVER (PARTITION BY clave ORDER BY created_at, id) <= perGroup + 1` sobre `clave = ANY(@keys)`, para no
  traer un grupo entero. Incluye inactivos e incompletos (spec §3.3).
- Validador público: `Kind` obligatorio y uno de los nombres del enum (ordinal) → si no, `errors.Kind`. `Page` y `PageSize`
  se recortan en silencio (`< 1` → 1 / default; `> 50` → 50), como `CustomerPaging.NormalizePageSize`.
- Handler: autorizar (`CustomerRead`) → validar → conteos (las dos clases, cero incluido, en el orden del enum) → grupos →
  miembros → DTOs (`identificationType` con `ToWireValue()`, `phone` la columna `phone` tal cual se escribió).
- Ruta: `group.MapGet("/anomalies", …)` con `?kind=&page=&pageSize=`, `CustomerRead`, `Produces<CustomerAnomaliesResponse>()`, 403, 422.

- [ ] **Step 1: Escribe las pruebas que fallan**

`CustomerAnomaliesHandlerTests.cs`:
```csharp
[Fact] public async Task CountsCarryBothKindsEvenAtZero()         // doble sin grupos → counts [{SharedPhone,0},{SharedIdentificationNumber,0}], groups []
[Fact] public async Task AnUnknownKindIs422OnKind()               // "Phone" → ValidationException errors["Kind"]
[Fact] public async Task PageSizeIsClampedToFifty()                // pageSize 500 → el doble recibe take 50; respuesta PageSize 50
[Fact] public async Task AGroupWithMoreThanFiftyIsCutAndFlagged()  // el doble devuelve 51 → 50 clientes, Truncated true
```
`CustomerAnomaliesApiTests.cs` (siembra por SQL, incluidos un inactivo y un incompleto de WhatsApp sin documento):
```csharp
[Fact] public async Task SharedPhoneGroupsTheCustomersOfANumberOldestFirst()
// tres con +573043549660 (uno incompleto, uno inactivo) y dos con otro número → kind=SharedPhone: total 2, el grupo de 3 primero,
// key "+573043549660", customers por created_at, truncated false
[Fact] public async Task SharedIdentificationNumberGroupsAcrossTypesAndFormats()
// Nit "92-1396892" y Ce "921396892" → un grupo key "921396892"; counts SharedIdentificationNumber 1
[Fact] public async Task TheGroupsArePagedNotTheCustomers()       // 3 grupos, pageSize 2 → page 2 trae 1 grupo, total 3
[Fact] public async Task AnotherTenantIs403AndItsGroupsNeverShow()
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~CustomerAnomaliesHandlerTests"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementa** los métodos del puerto, enum, constantes, DTOs, query, validador, handler, ruta y registro.

- [ ] **Step 4: Corre y verifica que pasan** (la unitaria y `CustomerAnomaliesApiTests`, `CustomerDuplicateQueriesTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Customers src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Customers
git commit -m "feat(customers): CD-04 anomalías de clientes por celular y por número de documento"
```

---

### Task 5 (CD-05): El opt-out de marketing es del número

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerAudienceRepository.cs:14-92`
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerEngagementPorts.cs:73-75` (`<summary>` de `CustomerContact`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingCustomerDirectory.cs:14-18` (`<summary>` de `MessagingCustomerContact`)
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerAudienceDirectoryIntegrationTests.cs`,
  `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/CampaignDispatchTests.cs`, `CampaignApiTests.cs`

**Interfaces:**
- Produces (sin cambio de firma): `CustomerContact.MarketingOptOut` y `AudienceCount.OptedOut` pasan a significar «la ficha
  **o** cualquier otro cliente del tenant con el mismo `phone_e164` tiene la bandera»; una ficha sin `phone_e164` sólo mira
  la suya. `MarkMarketingOptOutAsync` no cambia (sigue marcando la ficha, con su `source`).
- Un solo lugar para la regla: un método privado del repositorio que proyecta, sobre un `IQueryable<Customer>`, el
  `OptedOut` como `customer.MarketingOptOut || (customer.PhoneE164 != null && dbContext.Customers.Any(other => other.TenantId == tenantId && other.PhoneE164 == customer.PhoneE164 && other.MarketingOptOut))`
  (usa `IX_customers_tenant_phone_e164`), y que consumen `FindContactAsync`, `ListAudienceBatchAsync` y `CountAudienceAsync`.
  Si EF no traduce el `GroupBy` del conteo con ese `EXISTS`, el conteo pasa a `SqlQuery` con la misma regla y lo anota.

- [ ] **Step 1: Escribe las pruebas que fallan**

`CustomerAudienceDirectoryIntegrationTests.cs`:
```csharp
[Fact] public async Task AnOptOutOnANumberCoversEveryCustomerWithThatNumber()
// A y B con +573001112233, opt-out sólo en A; C con otro número → FindContactAsync(B).MarketingOptOut true; audiencia: B true, C false;
// CountAudienceAsync(A,B,C).OptedOut == 2
[Fact] public async Task AnOptOutOfAnotherTenantWithTheSameNumberDoesNotCount()
[Fact] public async Task ACustomerWithoutPhoneOnlyLooksAtItsOwnFlag()
[Fact] public async Task MarkingTheOptOutStillMarksOnlyThatCustomer()      // marcar A → marketing_opt_out de B sigue false en la base
```
`CampaignDispatchTests.cs`:
```csharp
[Fact] public async Task AMarketingCampaignSkipsTheWholeNumberWhenOneOfItsCustomersOptedOut()
// CreateCustomersAsync con A y B del mismo teléfono (una llamada) y C; opt-out en A por SQL → promo: A y B Skipped opted_out, C Sent
[Fact] public async Task AnUtilityCampaignStillReachesANumberWithAnOptOut()        // aviso: el mismo arreglo → B Sent
[Fact] public async Task AnOptOutOnTheNumberAfterTheFreezeSkipsTheOtherCustomerBeforeSending()
// como ARecipientWhoOptedOutAfterTheFreezeIsSkippedBeforeAnythingIsSent, pero la bandera va en A y el destinatario es B (la re-verificación R6/R7, desvío A6)
```
`CampaignApiTests.cs`:
```csharp
[Fact] public async Task ThePreviewCountsTheNumberOptOut()   // audience-preview MARKETING con A (opt-out) y B → optedOut 2
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerAudienceDirectoryIntegrationTests"`
Expected: FAIL en las aserciones (B sale `MarketingOptOut false`).

- [ ] **Step 3: Implementa** la proyección compartida y actualiza los dos `<summary>`.

- [ ] **Step 4: Corre y verifica que pasan** (`CustomerAudienceDirectoryIntegrationTests`, y en Messaging
  `CampaignDispatchTests|CampaignApiTests`).
Expected: PASS, incluidas las de opt-out que ya existían.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Customers src/Modules/Messaging/Modules.Messaging.Application/IMessagingCustomerDirectory.cs tests/Modules/Customers tests/Modules/Messaging
git commit -m "feat(customers): CD-05 el opt-out de marketing vale para todo el número"
```

---

### Task 6 (CD-06): `ListByPhoneAsync` en Customers, en Messaging y en el adaptador

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/ICustomerPhoneDirectory.cs`, `…/Infrastructure/Persistence/CustomerPhoneDirectory.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingCustomerDirectory.cs`, `src/Bootstrapper/MessagingCustomerDirectory.cs`
- Modify (dobles): `tests/Bootstrapper/Bootstrapper.UnitTests/MessagingCustomerDirectoryAudienceTests.cs` (`FakePhones`),
  `tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateTestDoubles.cs` (`FakeCustomerDirectory`)
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerPhoneDirectoryTests.cs`,
  `tests/Bootstrapper/Bootstrapper.UnitTests/MessagingCustomerDirectoryAudienceTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  // ICustomerPhoneDirectory — la consulta de MatchAsync sin el TryAdd que se queda con el primero (spec §4.1).
  Task<IReadOnlyList<CustomerPhoneMatch>> ListByPhoneAsync(Guid tenantId, string e164, int take, CancellationToken cancellationToken);
  // IMessagingCustomerDirectory — waId son los dígitos; el adaptador antepone «+», como MatchAsync.
  Task<IReadOnlyList<CustomerRefDto>> ListByPhoneAsync(Guid tenantId, string waId, int take, CancellationToken cancellationToken);
  ```
- Orden `created_at, id`; incluye inactivos e incompletos; `take` acota en SQL. `FakeCustomerDirectory` guarda una lista
  configurable `ByPhone` para CD-07; `FakePhones` lanza `NotSupportedException` salvo en la prueba nueva.

- [ ] **Step 1: Escribe las pruebas que fallan**

`CustomerPhoneDirectoryTests.cs`:
```csharp
[Fact] public async Task ListByPhoneReturnsEveryCustomerOfTheNumberOldestFirstWithinTheTenant()   // 3 del tenant (uno inactivo), 1 de otro → 3, en orden
[Fact] public async Task ListByPhoneHonoursTheTake()
```
`MessagingCustomerDirectoryAudienceTests.cs`:
```csharp
[Fact] public async Task ListByPhoneAddsThePlusAndMapsToCustomerRefs()   // waId "573001112233" → el doble recibe "+573001112233"; refs con IsComplete
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~MessagingCustomerDirectoryAudienceTests"`
Expected: FAIL de compilación (`ListByPhoneAsync` no existe).

- [ ] **Step 3: Implementa** los dos métodos, el adaptador y los dobles.

- [ ] **Step 4: Corre y verifica que pasan** (`MessagingCustomerDirectoryAudienceTests`, `CustomerPhoneDirectoryTests`,
  `Messaging.UnitTests --filter "FullyQualifiedName~SendTemplateMessageHandlerTests|FullyQualifiedName~StartConversationHandlerTests"` por el doble,
  y `ArchitectureTests` de Customers y Messaging).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Customers src/Modules/Messaging/Modules.Messaging.Application/IMessagingCustomerDirectory.cs src/Bootstrapper/MessagingCustomerDirectory.cs tests/Modules/Customers tests/Bootstrapper tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateTestDoubles.cs
git commit -m "feat(messaging): CD-06 los clientes de un número, por puerto de Customers"
```

---

### Task 7 (CD-07): `SharedPhoneCustomers` en el resumen de una conversación

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingDtos.cs:29-51`, `MessagingSupport.cs:36-85`
- Modify: `GetConversation.cs:23`, `ConversationAssignment.cs:68`, `ConversationLifecycle.cs:190,223`, `StartConversation.cs:120`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationReadsTests.cs`,
  `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ConversationCustomerApiTests.cs` (nuevo)

**Interfaces:**
- Consumes: `IMessagingCustomerDirectory.ListByPhoneAsync` (CD-06).
- Produces:
  ```csharp
  // ConversationSummary — propiedad init (como CustomerRefDto.Tags), así no cambia el constructor posicional.
  /// Spec 2026-10-24 §4.1 (BFF): los otros clientes del tenant con el número de la conversación, sin el que ella muestra, por
  /// created_at, hasta 10; sin etiquetas. Viaja siempre. Llena en toda respuesta de una sola conversación (detalle, PUT
  /// …/customer, tomar, transferir, liberar, resolver, reabrir, pausar y devolver el asistente, iniciar): el frontend escribe
  /// esas respuestas en la caché del detalle (patchConversationCaches) y una lista vacía apagaría la línea del encabezado
  /// hasta el siguiente GET. Vacía en el listado y en la búsqueda **a propósito** (D-D6: ninguna fila la dibuja); no la
  /// «arregles». Siempre vacía en Instagram y sin wa_id.
  public IReadOnlyList<CustomerRefDto> SharedPhoneCustomers { get; init; } = [];

  // ConversationSummaryBuilder
  public const int SharedPhoneCustomersCap = 10;
  public Task<ConversationSummary> BuildOneAsync(Guid tenantId, ConversationRow row, CancellationToken cancellationToken);
  ```
- `BuildOneAsync` = `BuildAsync(tenantId, [row])[0]` y, si `row.Channel == WhatsApp` y `row.WaId` no es nulo,
  `ListByPhoneAsync(tenantId, waId, SharedPhoneCustomersCap + 1)` sin el `summary.Customer?.Id` (el que el encabezado muestra,
  que en una fila vieja sin `customer_id` sale por teléfono), recortado a 10. Los seis llamadores de una sola fila pasan a
  `BuildOneAsync`; `ListConversations` y `SearchMessages` siguen con `BuildAsync`.

- [ ] **Step 1: Escribe las pruebas que fallan**

`ConversationReadsTests.cs` (con `FakeCustomerDirectory.ByPhone`):
```csharp
[Fact] public async Task BuildOneListsTheOthersOfTheNumberWithoutTheConversationsCustomer()
[Fact] public async Task ALegacyConversationDoesNotListItsOwnCustomerAsShared()   // CustomerId null, el de MatchAsync excluido
[Fact] public async Task InstagramAndConversationsWithoutWaIdNeverAsk()          // el doble no fue llamado; lista vacía
[Fact] public async Task TheListIsCutAtTen()                                      // 12 en el doble → 10
```
`ConversationCustomerApiTests.cs` (stub de auth; `CreateCustomersAsync` con LUIS ALBERTO y ZIBA TIENDA del mismo teléfono en una
llamada; entrante con BSUID y ese número para crear la conversación):
```csharp
[Fact] public async Task TheDetailListsTheOtherCustomerOfTheNumber()      // GET detalle → sharedPhoneCustomers [{ id ZIBA, name, isComplete }]
[Fact] public async Task WithoutOtherCustomersTheListIsEmptyAndTheInboxAlwaysSendsItEmpty()
// GET /conversations → items[0].sharedPhoneCustomers == [] aunque el número sea compartido
[Fact] public async Task TakingKeepsTheSharedPhoneCustomersInTheAnswer()  // POST take → la respuesta trae la misma lista que el detalle
[Fact] public async Task ResolvingAndReopeningKeepThemToo()                // resolve y reopen (ConversationLifecycle, otro llamador) → misma lista
```
El `PUT …/customer` lo prueba CD-09 (`ChangingTheCustomerAnswersTheSummaryWithTheNewCustomerAndVersion`).

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~ConversationReadsTests"`
Expected: FAIL de compilación (`BuildOneAsync`, `SharedPhoneCustomers`).

- [ ] **Step 3: Implementa** la propiedad, `BuildOneAsync` y el cambio de los seis llamadores.

- [ ] **Step 4: Corre y verifica que pasan** (`ConversationReadsTests`, y en integración
  `ConversationCustomerApiTests|ConversationsApiTests|AssignmentApiTests|StartConversationApiTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Messaging tests/Modules/Messaging
git commit -m "feat(messaging): CD-07 el hilo dice qué otros clientes comparten el número"
```

---

### Task 8 (CD-08): Evento `CustomerChanged` en el dominio y en el hilo

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/ConversationEvent.cs`, `Conversation.cs` (junto a `Release`, `:297-310`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/ConversationEventJson.cs`, `IMessagingAuditRecorder.cs:37-63`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingDtos.cs:96-97`, `MessagingSupport.cs:169-207,264-289`, `ListMessages.cs:25-62`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationTests.cs`, `ConversationEventJsonTests.cs`, `MessageMappingTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  // ConversationEventType: CustomerChanged (al final del enum; se guarda por nombre).
  // ConversationEvent: Guid? PreviousCustomerId = null (al final). CustomerChanged lleva CustomerId = el nuevo,
  // PreviousCustomerId = el anterior (null en una conversación vieja sin cliente), Actor = la membresía.
  // Desvío A2: no va en Previous/Target, que el hilo resuelve como membresías.

  // Conversation — mismo cliente: [] sin tocar la versión (D-A2). Otro: CustomerId, Touch(now) y un evento.
  public IReadOnlyList<ConversationEvent> ChangeCustomer(Guid customerId, Guid actorMemberId, DateTimeOffset now);

  // ConversationEventJson: escribe y lee "previousCustomerId".
  // MessagingAuditActions: CustomerChanged = "messaging.conversation.customer_changed"; ForEvent lo mapea.

  // MessagingDtos.cs
  /// Spec 2026-10-24 §4.2 (BFF): un cliente nombrado por un evento, resuelto al leer para que el hilo diga «cambió el cliente
  /// de X a Y» sin otra consulta. null si el cliente ya no está en el tenant.
  public sealed record EventCustomerRefDto(Guid Id, string Name);

  /// Los dos campos de cliente son nuevos y opcionales, y en CustomerChanged Previous y Target viajan null (Actor sí: es
  /// la membresía que hizo el cambio). Por qué no se reusan Previous/Target: son MemberRefDto y el parser del frontend exige
  /// memberId en ellos —uno sin memberId lanza y rompe el hilo entero—, y MemberIdsOf los resuelve como membresías. Con
  /// campos aparte, un frontend viejo ignora los nuevos y dibuja CustomerChanged como evento desconocido, sin romperse.
  public sealed record MessageEventDto(string Type, MemberRefDto? Actor, MemberRefDto? Target, MemberRefDto? Previous,
      ResolutionReasonRefDto? Reason = null, string? PauseReason = null,
      EventCustomerRefDto? TargetCustomer = null, EventCustomerRefDto? PreviousCustomer = null);

  // MessageMapping
  public static IReadOnlyCollection<Guid> CustomerIdsOf(IEnumerable<MessageRow> rows);   // CustomerId y PreviousCustomerId de cada evento
  // ToDto suma al final: IReadOnlyDictionary<Guid, CustomerRefDto>? customerRefs = null
  ```
- La forma exacta en el cable de un `CustomerChanged` (la consume el plan del frontend):
  ```json
  { "type": "CustomerChanged",
    "actor": { "memberId": "…", "displayName": "Andrés" }, "target": null, "previous": null,
    "reason": null, "pauseReason": null,
    "targetCustomer": { "id": "…", "name": "ZIBA TIENDA MULTIMARCA S.A.S." },
    "previousCustomer": { "id": "…", "name": "LUIS ALBERTO MARULANDA GONZALEZ" } }
  ```
  `previousCustomer` es `null` si la conversación no tenía cliente; cualquiera de los dos es `null` si el cliente ya no está.
- `TargetCustomer` sale de `CustomerId` y `PreviousCustomer` de `PreviousCustomerId`, para cualquier evento que los traiga
  (también `CustomerCreated`/`CustomerLinked` llevan `targetCustomer`: aditivo). `ListMessagesHandler` recibe
  `IMessagingCustomerDirectory` y resuelve con `FindRefsAsync(tenant, CustomerIdsOf(page))`; `SearchMessages` y `OutboundGuards`
  no cambian (sus eventos salen con los dos campos en `null`). `MemberIdsOf` no cambia.

- [ ] **Step 1: Escribe las pruebas que fallan**

`ConversationTests.cs`:
```csharp
[Fact] public void ChangingTheCustomerBumpsTheVersionAndReturnsTheEvent()
// ChangeCustomer(B, member, now) → CustomerId B, Version + 1, UpdatedAt now, [CustomerChanged(Actor member, CustomerId B, PreviousCustomerId A)]
[Fact] public void ChangingToTheSameCustomerIsANoOp()          // [] y la versión no sube
[Fact] public void ALegacyConversationWithoutCustomerGetsOne()  // PreviousCustomerId null
```
`ConversationEventJsonTests.cs`:
```csharp
[Fact] public void CustomerChangedRoundTripsWithBothCustomers()
```
`MessageMappingTests.cs`:
```csharp
[Fact] public void CustomerChangedCarriesBothCustomerNamesAndNoMemberInPreviousOrTarget()
// refs {A:"LUIS ALBERTO…", B:"ZIBA TIENDA…"} → Event.TargetCustomer B, PreviousCustomer A, Actor el miembro, Target null, Previous null
[Fact] public void AnUnknownCustomerLeavesTheRefNull()
[Fact] public void TheWireShapeIsTheContract()   // JsonSerializer con las opciones web: claves "targetCustomer" y "previousCustomer" con { id, name }; "target" y "previous" null
[Fact] public void CustomerIdsAreNeverAskedAsMembers()           // MemberIdsOf de un CustomerChanged = sólo el actor
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~ConversationTests|FullyQualifiedName~ConversationEventJsonTests|FullyQualifiedName~MessageMappingTests"`
Expected: FAIL de compilación (`CustomerChanged`, `ChangeCustomer`, `PreviousCustomerId`).

- [ ] **Step 3: Implementa** el valor del enum, el campo, el método, la serialización, la acción de auditoría, el DTO y la
  resolución de nombres en `ListMessagesHandler`.

- [ ] **Step 4: Corre y verifica que pasan** (las tres clases y `ThreadEventsApiTests` en integración).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Messaging tests/Modules/Messaging/Modules.Messaging.UnitTests
git commit -m "feat(messaging): CD-08 evento CustomerChanged con los nombres de los dos clientes"
```

---

### Task 9 (CD-09): `PUT …/conversations/{id}/customer`

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/ConversationAssignment.cs` (comando, validador y handler al final)
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs` (ruta después de `release`, `:184-190`; método junto a `ReleaseAsync`, `:470-471`; request junto a `TransferConversationRequest`, `:545`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:721-724`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ConversationCustomerApiTests.cs`

**Interfaces:**
- Consumes: `Conversation.ChangeCustomer`, `MessagingAuditActions.CustomerChanged` (CD-08), `BuildOneAsync` (CD-07),
  `ConversationAssignmentSteps.LoadAsync`/`CommitAsync`, `IMessagingCustomerDirectory.FindRefsAsync`.
- Produces:
  ```csharp
  // MessagingErrorCodes — desvío A10.
  /// Spec 2026-10-24 §4.2: el cliente elegido no existe en el tenant (422). Otro tenant responde igual que uno inexistente.
  public const string ConversationCustomerNotFound = "messaging.conversation.customer_not_found";

  public sealed record ChangeConversationCustomerCommand(Guid TenantId, Guid ConversationId, Guid? CustomerId, long ExpectedVersion)
      : ICommand<ConversationSummary>;
  public sealed class ChangeConversationCustomerValidator : AbstractValidator<ChangeConversationCustomerCommand>;
  // CustomerId NotNull y ≠ Guid.Empty, OverridePropertyName("customerId"), «Elige el cliente de la conversación.»
  public sealed class ChangeConversationCustomerHandler(
      ConversationAssignmentSteps steps, IMessagingCustomerDirectory customers, IValidator<ChangeConversationCustomerCommand> validator)
      : ICommandHandler<ChangeConversationCustomerCommand, ConversationSummary>;

  // MessagingEndpoints.cs
  /// Spec 2026-10-24 §4.2.
  public sealed record ChangeConversationCustomerRequest(Guid? CustomerId);
  ```
- Orden del handler: `steps.LoadAsync` (tenant, permiso y módulo → 404 dentro del tenant → membresía activa) → validar →
  `FindRefsAsync` sin el cliente → `MessagingDomainException(ConversationCustomerNotFound)` → `EnsureVersion` (412) →
  `ChangeCustomer` → `CommitAsync`. Autoriza **antes** de validar (no copies el orden de `TransferConversationHandler`).
  Activo o inactivo, cualquier cliente del tenant (D-D7).
- Ruta: `group.MapPut("/conversations/{conversationId:guid}/customer", …)` con `RequireVersion(httpContext)` (428),
  `ConversationManage`, `Accepts<ChangeConversationCustomerRequest>`, `Produces<ConversationSummary>()`, 403, 404, 412, 422, 428.
- `<summary>` del handler: qué no se mueve (D-D8: BSUID, negocios y triage del embudo, mensajes enviados) y qué sigue a la
  conversación porque lee `customer_id` (opt-out por botón y por 131050, asignado heredero, búsqueda y filtro por etiqueta,
  triage pendiente).

- [ ] **Step 1: Escribe las pruebas que fallan**

`ConversationCustomerApiTests.cs` (mismo arreglo de CD-07):
```csharp
[Fact] public async Task ChangingTheCustomerAnswersTheSummaryWithTheNewCustomerAndVersion()
// PUT {customerId: ZIBA} con If-Match → 200 customer.id ZIBA, version + 1, sharedPhoneCustomers [LUIS ALBERTO]
[Fact] public async Task TheChangeIsInTheThreadAndInTheAudit()
// GET messages → evento CustomerChanged con targetCustomer.name ZIBA, previousCustomer.name LUIS ALBERTO, target y previous null;
// audit.entries messaging.conversation.customer_changed = 1
[Fact] public async Task TheSameCustomerIs200WithoutEventOrNewVersion()
[Fact] public async Task WithoutIfMatchIs428AndAStaleVersionIs412()
[Fact] public async Task ACustomerOfAnotherTenantIs422CustomerNotFound()   // y la conversación no cambia
[Fact] public async Task WithoutCustomerIdIs422OnCustomerId()
[Fact] public async Task AnotherTenantIs403AndWithoutManageIs403()
[Fact] public async Task ALaterInboundDoesNotOverwriteTheChosenCustomer()
// cambia a ZIBA → entrante del mismo BSUID en la misma conexión → customer_id sigue ZIBA (COALESCE de InboundIngestion.cs:144)
[Fact] public async Task AnotherConnectionStillResolvesTheBsuidToItsOwner()
// D-D8: segunda conexión (SeedWhatsAppConnectionAsync "Soporte", "333", "444"); tras cambiar a ZIBA, entrante del mismo BSUID por "333"
// → la conversación nueva queda con LUIS ALBERTO (dueño del BSUID). Amarra el comportamiento elegido.
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ConversationCustomerApiTests"`
Expected: FAIL de compilación (`ChangeConversationCustomerRequest`, el código).

- [ ] **Step 3: Implementa** el código, comando, validador, handler, ruta, request y registro.

- [ ] **Step 4: Corre y verifica que pasan** (`ConversationCustomerApiTests`, `AssignmentApiTests`, `InboundBsuidTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Messaging src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Messaging
git commit -m "feat(messaging): CD-09 cambiar el cliente principal de una conversación"
```

---

### Task 10 (CD-10): Iniciar no abre un segundo chat del mismo número

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ConversationOpener.cs:7-13,81-119`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/StartConversationApiTests.cs`

**Interfaces:**
- Produces: `FindAsync` busca, en la conexión y el tenant: (1) por BSUID del cliente si tiene; (2) **nuevo** con ese `wa_id`
  y `user_id IS NOT NULL`, `ORDER BY last_activity_at DESC, id DESC LIMIT 1` (la que recibe las respuestas); (3) por
  `customer_id`, la más reciente; (4) la fila vieja con ese `wa_id` y `user_id IS NULL`. El `INSERT` y su `ON CONFLICT` no
  cambian, ni el `customer_id` de la conversación encontrada (cambiarlo es CD-09). El `<summary>` de la clase dice el orden y
  por qué el paso 2 va antes del 3 (una huérfana del defecto no le gana a la que recibe respuestas). Las huérfanas que ya
  existen se quedan (D-D10). Afecta también al envío de campañas, que abre por este mismo opener.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
[Fact] public async Task StartingWithACustomerThatSharesTheNumberUsesTheConversationThatReceivesTheReplies()
// A y B con el mismo teléfono; entrante con BSUID para A → POST /conversations con B → 200 (no 201), mismo conversationId,
// customer sigue A; count(*) de conversaciones = 1
[Fact] public async Task AnOrphanOfTheOldDefectDoesNotWinOverTheConversationWithBsuid()
// además una fila por SQL con user_id NULL, el mismo wa_id y customer_id = B, más vieja → iniciar con B usa la de BSUID
```
Las de hoy (`ALegacyPhoneOnlyConversationOfTheCustomerIsReusedNotDuplicated`, `ACustomerWhoWroteFirstWithABsuidIsReusedByCustomerId`,
`TheBsuidThatArrivesLaterIsAdoptedByTheSameConversationWithoutDuplicating`) no cambian.

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~StartConversationApiTests"`
Expected: FAIL en las dos nuevas (201 y dos conversaciones).

- [ ] **Step 3: Implementa** el orden nuevo.

- [ ] **Step 4: Corre y verifica que pasan** (`StartConversationApiTests`, `CampaignDispatchTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ConversationOpener.cs tests/Modules/Messaging/Modules.Messaging.IntegrationTests/StartConversationApiTests.cs
git commit -m "fix(messaging): CD-10 iniciar una conversación reutiliza la del número que recibe respuestas"
```

---

### Task 11 (CD-11): Nada de una campaña se reclama antes de congelar su audiencia

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Campaigns/CampaignDispatchStore.cs:98-116` (CTE `ranked`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/CampaignDispatch.cs:63-65` (`<summary>` de `ClaimAsync`)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/CampaignDispatchTests.cs`

**Interfaces:**
- Produces: el `WHERE` de `ranked` suma `AND c.audience_frozen_at IS NOT NULL`, con un comentario: el congelado de otro pod
  escribe lotes de 500 que ya son `Pending`, y el `UPDATE` de duplicados de CD-12 corre al terminar; reclamar antes dejaría
  salir a un perdedor (desvío A9).
- Las pruebas de la clase que siembran destinatarios a mano y reclaman sin congelar (las que llaman `ClaimAsync` directo,
  hoy `:377`, `:517`, `:551`, `:577`) siembran también `audience_frozen_at = now()` en su campaña. Sólo la prueba cambia;
  la regla no se relaja.

- [ ] **Step 1: Escribe la prueba que falla**

```csharp
[Fact] public async Task NothingIsClaimedBeforeTheAudienceIsFrozen()
// campaña Sending sin audience_frozen_at con un Pending sembrado → ClaimAsync(20) vacío; tras MarkFrozenAsync → lo reclama
```

- [ ] **Step 2: Corre y verifica que falla**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~CampaignDispatchTests.NothingIsClaimedBeforeTheAudienceIsFrozen"`
Expected: FAIL (el primer reclamo devuelve el destinatario).

- [ ] **Step 3: Implementa** la condición y ajusta las siembras que la clase entera muestre rojas.

- [ ] **Step 4: Corre y verifica que pasan** (`CampaignDispatchTests` entera y `Messaging.UnitTests --filter "FullyQualifiedName~CampaignDispatcherTests"`).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Messaging tests/Modules/Messaging/Modules.Messaging.IntegrationTests/CampaignDispatchTests.cs
git commit -m "fix(messaging): CD-11 el worker no reclama destinatarios de una audiencia a medio congelar"
```

---

### Task 12 (CD-12): Un envío por contacto (`duplicate_contact`)

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerEngagementPorts.cs:74-75`, `…/Infrastructure/Persistence/CustomerAudienceRepository.cs:35-70`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingCustomerDirectory.cs:17-18`, `src/Bootstrapper/MessagingCustomerDirectory.cs:57-58`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/CampaignDispatch.cs:13-14,60-61,473-484`
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/CampaignFailureReasons.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Campaigns/CampaignDispatchStore.cs:49-96`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/CampaignRecords.cs:14-50`, `MessagingDbContext.cs:389-422`
- Create (ef): `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/Migrations/*_AddCampaignRecipientCustomerCreatedAt.cs` (+ Designer y snapshot)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/CampaignFailureReasonsTests.cs`,
  `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/CampaignDispatchTests.cs`,
  `tests/Bootstrapper/Bootstrapper.UnitTests/MessagingCustomerDirectoryAudienceTests.cs`

**Interfaces:**
- Consumes: el reclamo sólo congelado (CD-11).
- Produces:
  ```csharp
  // Customers — la fecha de alta para el desempate «el más viejo» (D-M7). Opcional al final: los llamadores no cambian.
  public sealed record CustomerContact(Guid CustomerId, string Name, string? PhoneE164, string? WhatsAppUserId, bool IsActive,
      bool MarketingOptOut, DateTimeOffset? CreatedAt = null);
  // Messaging — igual; el adaptador la pasa.
  public sealed record MessagingCustomerContact(..., bool MarketingOptOut, DateTimeOffset? CreatedAt = null);
  public sealed record CampaignRecipientSeed(..., string? FailureCode, DateTimeOffset? CustomerCreatedAt = null);

  // CampaignFailureCodes
  /// Spec 2026-10-24 §4.4: otro destinatario de la campaña tiene el mismo WhatsApp y se le escribe a él.
  public const string DuplicateContact = "duplicate_contact";
  // CampaignFailureReasons: [DuplicateContact] = "Otro cliente de la campaña tiene el mismo WhatsApp."
  ```
- `ListAudienceBatchAsync` proyecta `CreatedAt`; `Seed` copia `contact.CreatedAt`; `AddRecipientsAsync` suma
  `customer_created_at` al `unnest`. Columna `customer_created_at timestamptz NULL` en `CampaignRecipientRecord`/`ConfigureCampaignRecipient`;
  migración `AddCampaignRecipientCustomerCreatedAt` con el comando de Global Constraints (sólo `AddColumn`; nullable, así
  las campañas ya congeladas no cambian). Desvío A8.
- `MarkFrozenAsync` abre una transacción (como `CloseFinishedAsync`) y corre, **en este orden**, el `UPDATE` de duplicados,
  el de `audience_frozen_at` y el de R9 de hoy. El de duplicados, sólo con la campaña `Sending` (el SQL lo fija el spec; el
  orden de la ventana es la regla del owner, §10.2):
  ```sql
  -- Por wa_id: el user_id es único por cliente en el tenant (IX_customers_tenant_whatsapp_user_id), así que nunca se repite
  -- dentro de una campaña (desvío A7). Sólo los Pending: un omitido por otro motivo no le quita el envío a nadie.
  WITH live AS (
      SELECT r.id, r.wa_id, r.customer_id, r.customer_created_at, c.connection_id, c.tenant_id
        FROM messaging.campaign_recipients r JOIN messaging.campaigns c ON c.id = r.campaign_id
       WHERE r.campaign_id = {campaignId} AND r.status = 'Pending' AND r.wa_id IS NOT NULL AND c.status = 'Sending'),
  principal AS (   -- el cliente de la conversación de ese contacto en la conexión: la que recibe las respuestas (§4.3)
      SELECT DISTINCT ON (l.wa_id) l.wa_id, v.customer_id
        FROM live l JOIN messaging.conversations v
          ON v.tenant_id = l.tenant_id AND v.connection_id = l.connection_id AND v.wa_id = l.wa_id AND v.customer_id IS NOT NULL
       ORDER BY l.wa_id, (v.user_id IS NOT NULL) DESC, v.last_activity_at DESC, v.id DESC),
  ranked AS (
      SELECT l.id, ROW_NUMBER() OVER (PARTITION BY l.wa_id
                 ORDER BY (l.customer_id = p.customer_id) IS TRUE DESC, l.customer_created_at NULLS LAST, l.customer_id) AS n
        FROM live l LEFT JOIN principal p ON p.wa_id = l.wa_id)
  UPDATE messaging.campaign_recipients r
     SET status = 'Skipped', failure_code = {DuplicateContact}, failure_reason = {texto}, locked_until = NULL
    FROM ranked WHERE r.id = ranked.id AND ranked.n > 1 AND r.status = 'Pending'
  ```
  Es idempotente: tras correr, los perdedores ya no son `Pending` y repetirlo no toca nada. El `<summary>` de
  `ICampaignDispatchStore.MarkFrozenAsync` y el de `CampaignDispatcher.FreezeAsync` lo dicen. `CountAudienceAsync` no
  descuenta duplicados (D-D9).

- [ ] **Step 1: Escribe las pruebas que fallan**

`CampaignFailureReasonsTests.cs`: suma `[InlineData("duplicate_contact", "Otro cliente de la campaña tiene el mismo WhatsApp.")]` a
`ASkippedOrOutcomeCodeHasItsFixedText`.

`MessagingCustomerDirectoryAudienceTests.cs`:
```csharp
[Fact] public async Task TheAudiencePassesTheCreationDateThrough()
```
`CampaignDispatchTests.cs`:
```csharp
[Fact] public async Task TwoCustomersOfTheSameNumberGetOnePendingInTheSameBatch()
// A (más viejo) y B del mismo teléfono, sin conversación → promo: A Sent, B Skipped duplicate_contact; MetaSends 1; customer_name del enviado A
[Fact] public async Task TheConversationCustomerWinsOverTheOldest()
// conversación de la conexión con BSUID, ese wa_id y customer_id = B (más nuevo) → B Pending/Sent, A Skipped duplicate_contact
[Fact] public async Task TwoCustomersOfTheSameNumberInDifferentBatchesAlsoGetOne()
// por el store: AddRecipientsAsync en dos llamadas (lote 1 con B, lote 2 con A, A más viejo por customer_created_at), MarkFrozenAsync
// → A Pending, B Skipped duplicate_contact
[Fact] public async Task RepeatingTheFreezeChangesNothing()      // MarkFrozenAsync dos veces → mismas filas y mismos estados
[Fact] public async Task AnInactiveCustomerDoesNotTakeTheSendFromTheOther()   // A inactivo (Skipped inactive) → B Sent, sin duplicate_contact
[Fact] public async Task ACancelledCampaignIsNotDeduplicated()   // Cancelled antes de MarkFrozenAsync → R9 los deja campaign_cancelled
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~CampaignFailureReasonsTests"`
Expected: FAIL (`duplicate_contact` no tiene texto).

- [ ] **Step 3: Implementa** la fecha de alta de punta a punta, la columna y su migración, el código y su texto, y el
  `UPDATE` dentro de la transacción de `MarkFrozenAsync`.

- [ ] **Step 4: Corre y verifica que pasan** (`CampaignFailureReasonsTests`, `CampaignDispatcherTests`, `CampaignDispatchTests`,
  `CampaignApiTests`, `MessagingCustomerDirectoryAudienceTests`, `CustomerAudienceDirectoryIntegrationTests`, `MessagingPersistenceTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Customers src/Modules/Messaging src/Bootstrapper/MessagingCustomerDirectory.cs tests/Modules/Messaging tests/Modules/Customers tests/Bootstrapper
git commit -m "feat(messaging): CD-12 una sola plantilla por contacto en cada campaña"
```

---

### Task 13 (CD-13): README, CLAUDE.md y verificación de la rama

**Files:**
- Modify: `README.md`: la tabla de «API implementada» (`:805-835`) suma la fila de
  `/api/v1/tenants/{tenantId}/customers` con `identification-matches` y `anomalies` (`customers.customer.read`) y, en la
  fila de `/messaging`, `customer` (`PUT`, `If-Match`); subsección nueva «### Clientes: documento repetido, coincidencias y
  anomalías» después de «POS: caja y ventas» (`:1593`) con R1–R3, la expresión normalizada y el índice; «Identidad,
  clientes y asignación» (`:2459-2474`) suma el número compartido, el cliente principal (D-D8), el orden de iniciar y, en
  campañas, el opt-out por número y `duplicate_contact`.
- Modify: `CLAUDE.md` sólo con gotchas que no se deducen del código. Candidatos, a confirmar al terminar: (1) un
  destinatario sembrado a mano no lo reclama el worker si su campaña no tiene `audience_frozen_at` (CD-11); (2) la expresión
  normalizada vive en `CustomerIdentificationSql` **y** literal en la migración: cambiar una sin la otra deja la consulta sin
  índice, y sólo lo dice `TheMatchQueryUsesTheNormalizedIndex`.

- [ ] **Step 1:** Escribe las secciones con lo que quedó construido (rutas, códigos, permisos y los dos índices tal cual
  están en el código); los comandos, en PowerShell.
- [ ] **Step 2:** Verifica la rama: `dotnet build`, las clases de prueba de CD-01 a CD-12 con sus filtros,
  `ArchitectureTests --filter "FullyQualifiedName~CustomersLayerTests|FullyQualifiedName~MessagingLayerTests|FullyQualifiedName~CompositionRootTests"`
  y `dotnet format --verify-no-changes --include <archivos tocados en la rama>`.
  Expected: PASS y build sin warnings nuevos.
- [ ] **Step 3: Commit**

```bash
git add README.md CLAUDE.md
git commit -m "docs: CD-13 clientes duplicados, número compartido y anomalías"
```

---

## Despliegue

- Dos migraciones: `AddCustomerIdentificationNormalizedIndex` (Customers, sólo el índice) y
  `AddCampaignRecipientCustomerCreatedAt` (Messaging, una columna nullable; desvío A8). Sin migración para `CustomerChanged`:
  `messages.details` no tiene `CHECK` sobre el tipo (`MessagingDbContext.EventShapeCheck`).
- Backend y frontend juntos: un frontend viejo ignora `existing`, `sharedPhoneCustomers`, `targetCustomer`/`previousCustomer` del
  evento y `duplicate_contact`, pero dibuja `CustomerChanged` como evento desconocido.
- Al desplegar, una campaña que esté a medio congelar en el pod viejo termina de congelarse en el nuevo con la regla de
  duplicados; una ya congelada no se toca.

## Preguntas para el owner

1. **Número reciclado en campañas.** Dos destinatarios con el mismo `wa_id` y BSUID **distintos** son, para Meta, dos
   personas (el BSUID sigue a la cuenta). El plan sigue el spec al pie de la letra y deja uno solo. ¿Así, o se omite sólo
   cuando a lo sumo uno de los dos tiene BSUID?
2. **Envío humano de una plantilla `MARKETING`.** Hoy no mira el opt-out (se entera por el 131050 de Meta). ¿El opt-out
   por número debe bloquearlo antes de enviar? Fuera de este plan (desvío A6).
3. **Ratificar A8:** la migración en Messaging por `customer_created_at` (el spec §7 decía «sin migración en Messaging»).
   La alternativa sin migración es desempatar por `customer_id` (GUID v7), que no es D-M7: un cliente sembrado o migrado con
   un id aleatorio se ordenaría mal.

## Contrato para el frontend

| Ruta | Permiso | Entrada | Respuesta |
|---|---|---|---|
| `POST`/`PUT /customers` | `customers.customer.manage` | igual que hoy | 422 `customers.customer.identification_taken` ahora con `existing: { id, name, identificationType, identificationNumber, cuc \| null, isComplete, isActive }`; puede faltar (carrera) |
| `GET /customers/identification-matches?number=&type=&excludeCustomerId=` | `customers.customer.read` | `number` obligatorio ≤ 32; `type` ∈ `NIT`, `CC`, `CE`, `PASAPORTE` | 200 `{ items: [{ id, name, identificationType, identificationNumber, cuc, isComplete, isActive, match }] }`, hasta 20; `match` ∈ `SameIdentification`, `OtherType`, `SameTypeOtherFormat` |
| `GET /customers/anomalies?kind=&page=&pageSize=` | `customers.customer.read` | `kind` ∈ `SharedPhone`, `SharedIdentificationNumber`; `pageSize` ≤ 50 (default 20) | 200 `{ counts: [{ kind, groups }] (siempre las dos), groups: [{ key, customers: [{ id, name, identificationType, identificationNumber, cuc, phone, isComplete, isActive, createdAt }], truncated }], total, page, pageSize }` |
| `GET /messaging/conversations/{id}` y toda respuesta de una sola conversación | `messaging.conversation.read` / `.manage` | — | `ConversationSummary` + `sharedPhoneCustomers: [{ id, name, isComplete, tags: [] }]` (≤ 10); en el listado siempre `[]` |
| `PUT /messaging/conversations/{id}/customer` | `messaging.conversation.manage` | `If-Match: "<version>"`, `{ customerId }` | 200 `ConversationSummary`; 428 sin `If-Match`, 412 versión vieja, 422 `messaging.conversation.customer_not_found` o `validation.failed` (`errors.customerId`) |
| `GET /messaging/conversations/{id}/messages` | `messaging.conversation.read` | — | evento `CustomerChanged`: `actor` (miembro), `target: null`, `previous: null`, `targetCustomer` y `previousCustomer` (`{ id, name }` o `null`); los demás eventos pueden traer `targetCustomer` y nunca cambian `target`/`previous` |
| Detalle de campaña | — | — | destinatario `Skipped` con `failureCode: "duplicate_contact"` y `failureReason: "Otro cliente de la campaña tiene el mismo WhatsApp."` |

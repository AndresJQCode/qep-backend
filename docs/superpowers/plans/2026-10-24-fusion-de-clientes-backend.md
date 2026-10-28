# Fusión de clientes — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que un grupo de duplicados se resuelva en un paso: uno queda, los otros quedan marcados «fusionado en …», y todo lo
que colgaba de ellos —en Customers y en otros cinco módulos— pasa al que queda en una sola transacción.

**Architecture:** En Customers, el agregado gana `MergedIntoId`/`MergedAt` y un plan de fusión puro en el dominio
(`CustomerMergePlan`) que valida las reglas, calcula qué campos se llenan y libera las identidades en el orden que exige EF.
El handler abre una transacción explícita en `CustomersDbContext` y hace dos `SaveChanges` (primero los fusionados, después el
que queda); notas, direcciones y la cadena `C → B → A` se mueven con SQL propio (`ICustomerMergeStore`) y las tablas de
Quotations, Pos, Pipeline, Messaging y Shopify con un puerto de Customers (`ICustomerMergeReferences`) cuyo adaptador vive en
Bootstrapper y corre en la misma conexión, como `CatalogVariantUsageChecker`. La exclusión es un filtro `merged_into_id IS NULL`
en cada lectura listada; la redirección sigue `MergedIntoId` (siempre un solo salto) donde se resuelve una identidad externa.

**Tech Stack:** .NET 10, EF Core + Npgsql (PostgreSQL 18), FluentValidation, xUnit v3 + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-10-24-fusion-de-clientes-design.md` (léelo entero; §2, §3, §4, §6, §7 y §10). El frontend
tiene plan aparte y depende de la sección «Contrato para el frontend» al final de este plan.

**Rama:** `feature/fusion-clientes`, en el worktree `.claude/worktrees/fusion-clientes`. Antes de **cada** tarea y de **cada**
commit, `git branch --show-current` tiene que decir esa rama; si dice `main` o `develop`, para. Nunca `git stash`. No toques el
checkout principal.

## Global Constraints

- TDD obligatorio: RED con la salida literal antes del GREEN, los dos en el reporte de cada tarea. Si la prueba compila contra
  lo que ya existe, el RED es en ejecución (aserción que falla), no de compilación.
- Conventional commits en español, con el slice `FC-NN` en el asunto, **sin atribución de IA** (sin `Co-Authored-By`; la regla
  del repo pisa cualquier recordatorio del harness).
- No corras la suite completa: cada comando lleva `--filter "FullyQualifiedName~<Clase>"`. Docker corriendo para las de
  integración. `Api.exe`/`dotnet run` corriendo bloquea `build`, `test` y `ef`: detenlo primero.
- Las pruebas de integración en la máquina del developer necesitan, en línea aparte: `$env:Seed__Enabled = "false"`.
- Formato: `dotnet format --verify-no-changes --include <tus archivos>` (no el repo entero).
- Aislamiento de tenant en doble capa: ruta `{tenantId:guid}` + política, **y** `CustomersAuthorization.EnsureAuthorized` en el
  handler **antes** de validar o tocar un repositorio. Otro tenant en la ruta → 403, nunca 404. Todo método de repositorio, de
  store y de puerto recibe `tenantId`, y todo `UPDATE`/`SELECT` entre esquemas filtra por `tenant_id`.
- Permiso nuevo `customers.customer.merge` (`CustomersPermissions.CustomerMerge`): constante **y** `PermissionDefinition` **y**
  política en `AddAuthorization` **y** en los roles `admin` y `advisor` (D-F1). Sin la política, 500 y no 403. Los handlers se
  registran a mano en `QepServiceCollectionExtensions` (`:295-386`); sin registro, 500.
- Texto libre o listas en un comando → validador de FluentValidation (422 `validation.failed` con `errors`). Ningún status a
  mano: lo mapea `src/Api/ApiExceptionHandler.cs` (`CustomersDomainException` → 422, `RequestConcurrencyException` → 412,
  `PreconditionRequiredException` → 428, `ResourceNotFoundException` → 404).
- Traducir errores de base va en Infrastructure, por **nombre de índice**. `Modules.Customers.Application` no referencia EF ni
  Npgsql (`CustomersLayerTests`). Customers no referencia a ningún otro módulo de negocio: lo de otros esquemas entra por
  `ICustomerMergeReferences`, con adaptador en `src/Bootstrapper/`. Pipeline y Shopify no referencian Customers: entran por sus
  puertos (`IPipelineCustomerDirectory`, `IShopifyCustomerDirectory`) con adaptador en Bootstrapper.
- EF ordena los `UPDATE` de un `SaveChanges` por clave primaria (CLAUDE.md): el BSUID, el IGSID y el documento salen del
  fusionado en un `SaveChanges` y entran al que queda en **otro**, dentro de la transacción explícita, como
  `BusinessHoursRepository.SwapDefaultAsync`.
- Un `FromSql` crudo lista todas las columnas mapeadas: los que ya existen usan `SELECT *` y siguen valiendo con las dos
  columnas nuevas; uno nuevo hace lo mismo.
- Migraciones con el factory de diseño, nunca con `--startup-project`:
  `dotnet ef migrations add AddCustomerMerge --project src/Modules/Customers/Modules.Customers.Infrastructure --context CustomersDbContext -o Persistence/Migrations`.
  Una sola migración, en Customers (§7). Ninguna en los otros módulos.
- BFF: cada DTO nuevo dice en su `<summary>` por qué tiene esa forma. `moves` viaja con las ocho clases siempre, también en
  cero. Los enums viajan con su nombre (`Quotations`, `PosSales`, `Email`, `MarketingOptOut`).
- Textos para personas y comentarios en español colombiano tuteando; códigos, rutas, enums y permisos tal cual.
- Al endurecer una regla (un fusionado rechaza escrituras), barre las pruebas de integración que arman cuerpos a mano: ninguna
  existente crea fusionados, así que no debería cambiar nada; si algo se pone rojo, es una prueba que reutiliza un cliente
  que la tuya marcó.
- `docs/estado.md` **no se toca** en esta rama. `Directory.Packages.props`, `global.json`, `.mcp.json`: no se tocan. Sin
  paquetes nuevos.

## Desvíos del spec (verificados contra el código el 2026-10-24)

La autoridad es el código. Ninguno cambia una regla del owner (§2, §4); B1–B4 son pedidos del plan de frontend.

| # | Spec dice | El código dice | El plan hace |
|---|---|---|---|
| F1 | §3.1 «el documento se quita del fusionado antes de ponérselo al que queda» | `CK_customers_complete_fields` (`CustomersDbContext.cs:29-31`) exige documento en una ficha completa, y por `survivor_incomplete` el que queda sólo hereda documento cuando todos son incompletos | Se quita sólo del fusionado incompleto que lo da; un fusionado completo conserva el suyo y lo libera el índice parcial |
| F2 | §3.2 `customers.merge.customer_not_found`: «un id que no existe en el tenant» | Toda ruta `/{customerId:guid}` responde 404 `customers.customer.not_found` (`CustomerNotFound.cs`) | El que queda (ruta) sigue con 404; los ids del cuerpo o del query → 422 `customers.merge.customer_not_found`. Un id de otro tenant es «no existe» |
| F3 | §3.2 validación: 1 a 10, sin repetir, sin el `survivorId` | Un `Guid.Empty` del cuerpo pasaría a la base | También rechaza `Guid.Empty`, con la misma clave `MergedCustomerIds` |
| F4 | §3.1 el `UPDATE` condicional del BSUID (`TryAttachWhatsAppUserIdAsync`) rechaza con 422 | Lo llama sólo `CustomerWhatsAppDirectory.EnsureAsync` (`:38`), desde el webhook: no hay persona que lea un 422 y lanzarlo tumba la entrega | El `WHERE` suma `merged_into_id IS NULL` (devuelve `false`) y `EnsureAsync` redirige al que quedó antes de llamarlo (FC-08) |
| F5 | §3.1 opt-in/opt-out rechazan | El opt-out de Meta (131050) y del botón entran por `CustomerAudienceRepository.MarkMarketingOptOutAsync` (`:72`), un `UPDATE` de sistema | El humano (`SetMarketingConsentHandler`) responde 422; el de sistema marca al que quedó (el opt-out es de la persona, §3.2 paso 2) |
| F6 | §3.1 «toda escritura» | `TryReplaceWhatsAppUserIdAsync` (`CustomerRepository.cs:680-687`) cambia el BSUID cuando Meta lo cambia | Se deja: es la identidad que la redirección necesita en el fusionado. `Deactivate` sí suma el rechazo (el spec no lo nombra) |
| F7 | §3.3 «la búsqueda por CUC de cotizaciones y pedidos (`SearchIdsByCucAsync`, `OrderListing.ResolveClientIdsByCucAsync`)» | `SearchIdsByCucAsync` sólo lo usa `OrderListing.cs:97`; el filtro de cotizaciones es por documento (`QuotationListing.cs:45` → `SearchIdsByIdentificationNumberAsync`) | La misma redirección va en los dos métodos del repositorio |
| F8 | §3.3 la lista de exclusiones | Dos lecturas que el spec no nombra devolverían al fusionado: `ICustomerPhoneDirectory.ListByPhoneAsync` (los «otros clientes del número» del spec 1) y `FindExistingIdentificationsAsync`, que además hace `ToDictionary` y **lanza** con dos dueños del mismo par en cuanto el índice es parcial (`CustomerRepository.cs:493`) | Las dos excluyen fusionados (FC-02, FC-07) |
| F9 | §3.3 Shopify «documento» redirige | Por F8 la búsqueda por documento ya no ve al fusionado, y un completo conserva su documento (F1) | `ShopifyCustomerDirectory.FindByDocumentAsync` cae a `ICustomerMergeStore.FindSurvivorIdByIdentificationAsync` |
| F10 | §3.3 fila de importación «con el CUC del que quedó» | `ImportRowError` (`CustomersDtos.cs:244`) no tiene dónde ponerlo salvo el mensaje | Campo opcional nuevo `MergedIntoCuc` al final (BFF: el diccionario arma el texto sin parsear el mensaje) |
| F11 | §3.4 `survivor` sin `version`; `filledFields` sin lista cerrada; `moves.Tags` sin definir | D-F13 dice que la versión sale de la vista previa; el frontend lo pidió (B1, B2) | `survivor.version` (y `isActive`); `filledFields` ⊆ `Email`, `BusinessName`, `Phone`, `WhatsAppUserId`, `InstagramUserId`, `Identification`, `Country`, `Address`, `MarketingOptOut`; `Tags` = las que el que queda gana sin repetir y dentro del tope |
| F12 | §3.2 «responde el `CustomerResponse` del que queda más `droppedTags`» | — | Un objeto plano (B3): `CustomerMergeResponse` = los campos de `CustomerResponse` + `droppedTags: [{ id, name }]`, siempre presente. Sin versión |
| F13 | — | — | B4: `CustomerListItemResponse` suma `CreatedAt` (para elegir al más viejo desde «Fusionar con…»). Verificado: ninguna prueba compara la forma exacta del ítem; se deserializa tipado en `CustomersApiHarness.ListAsync` |
| F14 | §4 `CountAsync(…)` | Notas, direcciones y etiquetas son de Customers | El puerto cuenta las cinco clases de otros módulos; las tres de Customers las cuenta `ICustomerMergeStore` |
| F15 | §4 el `UPDATE` de los otros módulos | Conversaciones, negocios y campañas tienen `version` (If-Match) | No se sube ninguna versión fuera de Customers: una conversación abierta en otra pestaña no debe dar 412 por la fusión |
| F16 | §4 «`QuotationEventApplier` resuelve `fact.ClientId`» | `ApplyOrderCreatedAsync` también usa `fact.ClientId` (`QuotationEvents.cs:66-81`) | Resuelven los dos hechos (`QuotationFact` y `OrderFact`) |

## Review Focus

1. **El que queda tiene el id menor y toma el BSUID del fusionado.** Con un solo `SaveChanges`, EF manda primero el `UPDATE` del
   id menor y choca con `IX_customers_tenant_whatsapp_user_id`. Lo esperado: 200 y el BSUID en el que queda. Prueba:
   `TheSurvivorWithTheSmallerIdTakesTheMergedBsuid` (FC-06).
2. **Dos dueños del mismo documento, uno fusionado.** Con el índice parcial, el que queda puede corregirse al documento del
   fusionado; `FindExistingIdentificationsAsync` no puede lanzar por la clave repetida (alta, POS, Shopify y la importación
   pasan por ahí). Prueba: `TwoOwnersOfTheSameDocumentOneMergedDoNotBreakTheLookup` (FC-02).
3. **Escribe otra vez el BSUID que se quedó en el fusionado.** El mensaje cae en el que quedó y no nace un incompleto nuevo.
   Prueba: `AnInboundWithTheBsuidLeftOnTheMergedLandsOnTheSurvivor` (FC-08).
4. **Un id de otro tenant en `mergedCustomerIds`.** Es «no existe» (422 `customers.merge.customer_not_found`), nunca 403/404 que
   confirmen nada, y la vista previa no devuelve conteos. Pruebas: `AMergedIdFromAnotherTenantIsNotFound` (FC-05 y FC-06).
5. **Una segunda pestaña confirma con la vista previa vieja.** La fusión siempre sube la versión del que queda, aunque no llene
   nada; la segunda recibe 412 y no fusiona dos veces. Prueba: `ASecondMergeWithTheOldVersionIs412` (FC-06).

---

## Mapa de archivos

| Archivo | Acción | Responsabilidad |
| --- | --- | --- |
| `src/Modules/Customers/Modules.Customers.Domain/Customer.cs` | Modificar | `MergedIntoId`, `MergedAt`, `EnsureNotMerged` en toda escritura, métodos internos de la fusión |
| `src/Modules/Customers/Modules.Customers.Domain/CustomerMerge.cs` | Crear | `CustomerMergePlan`, `CustomerMergeField` |
| `src/Modules/Customers/Modules.Customers.Domain/Tag.cs` | Modificar | `CustomerTagAssignment.PlanMerge` |
| `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersDbContext.cs`, `…/Migrations/*_AddCustomerMerge.cs` | Modificar / Crear (ef) | Columnas, índice parcial nuevo, identificación parcial |
| `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerRepository.cs` | Modificar | Exclusión, redirección, `TryAttach` |
| `src/Modules/Customers/Modules.Customers.Application/ICustomersUnitOfWork.cs`, `…/Infrastructure/Persistence/CustomersUnitOfWork.cs` | Modificar | `BeginTransactionAsync` |
| `src/Modules/Customers/Modules.Customers.Application/ICustomerMergeStore.cs`, `…/Infrastructure/Persistence/CustomerMergeStore.cs` | Crear | Cadena, notas, direcciones, conteos propios, redirecciones por id y documento |
| `src/Modules/Customers/Modules.Customers.Application/ICustomerMergeReferences.cs` | Crear | Puerto de los otros esquemas |
| `src/Bootstrapper/CustomerMergeReferences.cs` | Crear | SQL sobre `quotations`, `pos`, `pipeline`, `messaging`, `shopify` en la conexión de `CustomersDbContext` |
| `src/Modules/Customers/Modules.Customers.Application/CustomerMerge.cs` | Crear | Query y comando, validadores, handlers de vista previa y fusión |
| `src/Modules/Customers/Modules.Customers.Application/CustomersDtos.cs`, `CustomerMapping.cs`, `GetCustomer.cs`, `CustomerAuditActions.cs`, `CustomersPermissions.cs` | Modificar | DTOs, `mergedInto`, `CreatedAt` en el ítem, acción, permiso |
| `src/Modules/Customers/Modules.Customers.Application/UpdateCustomer.cs`, `CustomerNotes.cs`, `CustomerTags.cs`, `CustomerWhatsAppDirectory.cs`, `CustomerInstagramDirectory.cs`, `ImportCustomers.cs` | Modificar | Rechazo y redirección |
| `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerPhoneDirectory.cs`, `CustomerAudienceRepository.cs`, `CustomerDuplicateQueries.cs` | Modificar | Exclusión y redirección |
| `src/Modules/Customers/Modules.Customers.Api/CustomerEndpoints.cs` | Modificar | `GET …/merge-preview`, `POST …/merge`, If-Match, `CreatedAt` |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` | Modificar | Permiso (definición, roles, política), handlers, puertos |
| `src/Bootstrapper/ShopifyCustomerDirectory.cs`, `CustomerReportSource.cs`, `PipelineCustomerDirectory.cs` | Modificar | Redirección y exclusión |
| `src/Modules/Pipeline/Modules.Pipeline.Application/PipelineDirectories.cs`, `QuotationEvents.cs` | Modificar | `ResolveSurvivorAsync` y su uso |
| `README.md`, `CLAUDE.md` | Modificar | Documentación (FC-10) |

**Comandos de prueba** (en PowerShell, `$env:Seed__Enabled = "false"` en línea aparte antes de las de integración):

```powershell
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Pipeline/Modules.Pipeline.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CustomersLayerTests|FullyQualifiedName~CompositionRootTests"
```

---

### Task 1 (FC-01): El plan de fusión en el dominio

**Files:**
- Create: `src/Modules/Customers/Modules.Customers.Domain/CustomerMerge.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Domain/Customer.cs` (propiedades junto a `Version`, `:290`; `EnsureNotMerged`
  junto a `EnsureActive`, `:852`; llamadas en `Update` `:686`, `Complete` `:421`, `Deactivate` `:775`, `Activate` `:803`,
  `AddAddress` `:567`, `UpdateAddress` `:592`, `MakeAddressPrincipal` `:609`, `RemoveAddress` `:622`, `OptOutOfMarketing` `:825`,
  `OptInToMarketing` `:839`; el comentario de `Activate` `:798-801` deja de decir «sin filtro parcial»)
- Modify: `src/Modules/Customers/Modules.Customers.Domain/Tag.cs` (`CustomerTagAssignment`, `:159-207`)
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerMergePlanTests.cs`, `CustomerTests.cs`, `TagTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  // Customer
  public CustomerId? MergedIntoId { get; private set; }   // siempre un cliente NO fusionado (D-F2)
  public DateTimeOffset? MergedAt { get; private set; }
  public bool IsMerged => MergedIntoId is not null;
  /// Spec 2026-10-24 §3.1: va primero en toda escritura, antes de EnsureActive, para que el código diga «fusionado» y no «inactivo».
  public void EnsureNotMerged();   // CustomersDomainException("customers.customer.merged", "A merged customer cannot be edited.")

  // CustomerMerge.cs
  /// Los campos del que queda que la fusión llena (B2). Viajan por nombre en filledFields.
  public enum CustomerMergeField { Email, BusinessName, Phone, WhatsAppUserId, InstagramUserId, Identification, Country, Address, MarketingOptOut }

  public sealed class CustomerMergePlan
  {
      /// Valida y calcula sin escribir: la vista previa lo usa igual que la fusión.
      public static CustomerMergePlan Create(Customer survivor, IReadOnlyList<Customer> merged);
      public Customer Survivor { get; }
      public IReadOnlyList<Customer> Merged { get; }          // en el orden pedido
      public IReadOnlyList<CustomerMergeField> FilledFields { get; }   // en el orden del enum
      /// Primer paso (primer SaveChanges): marca cada fusionado y le quita lo que va a dar (BSUID, IGSID + usuario, documento).
      public void MarkMerged(DateTimeOffset now);
      /// Segundo paso (segundo SaveChanges): llena el que queda, hereda el opt-out y SIEMPRE sube su versión (Review Focus 5).
      public void FillSurvivor(DateTimeOffset now);
  }

  // Tag.cs — CustomerTagAssignment
  public sealed record CustomerTagMerge(IReadOnlyList<TagId> Added, IReadOnlyList<TagId> Dropped);
  /// D-F5: las del que queda primero; después las de los fusionados en el orden recibido, sin repetir, hasta MaxPerCustomer.
  public static CustomerTagMerge PlanMerge(IReadOnlyCollection<TagId> survivorTags, IEnumerable<TagId> mergedTagsInOrder);
  ```
- `Create`, en este orden (el primero que falla gana): un id repetido o el del que queda entre los fusionados, o un tenant
  distinto → `ArgumentException` (el validador y el repositorio ya lo impiden); el que queda o alguno fusionado ya fusionado →
  `customers.merge.already_merged`; el que queda inactivo → `customers.merge.survivor_inactive`; el que queda incompleto y algún
  fusionado completo → `customers.merge.survivor_incomplete`. Un fusionado inactivo vale (D-F12).
- Llenado (§3.2 paso 1): para cada campo, si el del que queda es `null`, el del **primer** fusionado (orden pedido) que lo
  tenga: `Email`; `BusinessName`; `Phone` junto con su `PhoneE164`; `WhatsAppUserId`; `InstagramUserId` junto con
  `InstagramUsername`; y sólo si el que queda es incompleto, `Identification` (tipo y número juntos), `Country` y `Address`.
  Nunca `ClassificationId` ni el CUC (D-F14). `MarketingOptOut`: si el que queda no lo tiene y algún fusionado sí, el primero
  le pasa `MarketingOptOutSource` y `MarketingOptOutAt` (vía `OptOutOfMarketing(source, at)`).
- `MarkMerged`: por fusionado, `MergedIntoId = survivor.Id`, `MergedAt = now`, `IsActive = false`, `Version++`, `UpdatedAt`; si
  es la fuente del BSUID lo pone en `null`; si es la del IGSID, IGSID y usuario en `null`; si es la del documento (sólo puede
  ser incompleto, desvío F1), tipo y número en `null`. Los métodos que tocan los campos son `internal` de `Customer`.

- [ ] **Step 1: Escribe las pruebas que fallan**

`CustomerMergePlanTests.cs` (fábricas de `CustomerTests.cs:87-130` copiadas o hechas `internal static` en un `CustomerFactory`):
```csharp
[Fact] public void EmptyFieldsAreFilledFromTheFirstMergedThatHasThemAndNothingIsOverwritten()
// survivor sin Email ni BusinessName; merged1 sin Email con BusinessName "B1"; merged2 con Email "m2@x.co"
// → Email "m2@x.co", BusinessName "B1", Phone del survivor intacto; FilledFields [Email, BusinessName]
[Fact] public void TheBsuidMovesOnlyWhenTheSurvivorHasNone()        // survivor con BSUID: el del fusionado se queda en él
[Fact] public void MarkMergedReleasesWhatTheSurvivorTakes()          // tras MarkMerged el fusionado no tiene BSUID ni IGSID; tras FillSurvivor el que queda sí
[Fact] public void AnIncompleteSurvivorTakesTheDocumentOfAnIncompleteMerged()   // POS: Cc 1020; FilledFields contiene Identification
[Fact] public void ACompleteSurvivorNeverTakesDocumentCountryOrAddress()
[Fact] public void TheClassificationIsNeverFilled()
[Fact] public void TheOptOutIsInheritedWithItsSourceAndDate()       // merged opt-out Button en T → survivor MarketingOptOutSource Button, At T; FilledFields contiene MarketingOptOut
[Fact] public void FillSurvivorAlwaysBumpsTheVersion()              // nada que llenar → Version + 1
[Fact] public void MarkMergedDeactivatesStampsAndBumps()            // IsActive false, MergedIntoId survivor, MergedAt now, Version + 1
[Fact] public void AnAlreadyMergedSurvivorOrMergedIs422AlreadyMerged()
[Fact] public void AnInactiveSurvivorIs422SurvivorInactiveAndAnInactiveMergedIsFine()
[Fact] public void AnIncompleteSurvivorWithACompleteMergedIs422SurvivorIncomplete()
```
`CustomerTests.cs`:
```csharp
[Theory] [MemberData(nameof(Writes))]
public void AMergedCustomerRejectsEveryWrite(string write)
// Update, Complete (sobre un incompleto fusionado), Activate, Deactivate, AddAddress, UpdateAddress, MakeAddressPrincipal,
// RemoveAddress, OptOutOfMarketing, OptInToMarketing → CustomersDomainException "customers.customer.merged"
```
`TagTests.cs`:
```csharp
[Fact] public void PlanMergeKeepsTheSurvivorsFirstAndSkipsRepeats()    // survivor {a,b}; merged [b,c] → Added [c], Dropped []
[Fact] public void PlanMergeStopsAtTheCapAndReportsTheRest()           // survivor 19; merged [x,y,z] → Added [x], Dropped [y,z]
[Fact] public void ASurvivorAtTheCapGainsNothing()
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~CustomerMergePlanTests|FullyQualifiedName~CustomerTests|FullyQualifiedName~TagTests"`
Expected: FAIL de compilación (`CustomerMergePlan`, `EnsureNotMerged`, `PlanMerge` no existen).

- [ ] **Step 3: Implementa** las propiedades (todavía sin mapear: EF las ignora hasta FC-02, así que agrega en
  `CustomersDbContext.ConfigureCustomer` un `customer.Ignore(value => value.MergedIntoId)` y otro para `MergedAt` que FC-02
  reemplaza por el mapeo), `EnsureNotMerged` y sus diez llamadas, el plan y `PlanMerge`.

- [ ] **Step 4: Corre y verifica que pasan** (las tres clases, más `CustomerProvisioningTests|CustomerWriteHandlersTests|MarketingConsentHandlerTests`
  para ver que nada existente cambió).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Customers tests/Modules/Customers/Modules.Customers.UnitTests
git commit -m "feat(customers): FC-01 plan de fusión y rechazo de escrituras sobre un fusionado"
```

---

### Task 2 (FC-02): Columnas, índice parcial y el fusionado en la base

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersDbContext.cs` (`ConfigureCustomer`, `:57-240`;
  el comentario «Sin filtro parcial» de `:210-211`)
- Create (ef): `…/Persistence/Migrations/*_AddCustomerMerge.cs` (+ Designer y snapshot)
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerRepository.cs` (`FindExistingIdentificationsAsync` `:455-500`, `TryAttachWhatsAppUserIdAsync` `:671-678`)
- Modify: `src/Modules/Customers/Modules.Customers.Application/UpdateCustomer.cs` (después de cargar el cliente, antes del CUC),
  `CustomerNotes.cs` (crear, editar y borrar), `CustomerTags.cs` (`SetCustomerTagsHandler`), `SetMarketingConsent.cs` (nada: el
  dominio ya rechaza), `CustomerProvisioning.cs` (doc de `CreateCompleteAsync`: «el índice no tiene filtro» deja de ser cierto)
- Modify: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomersApiHarness.cs` (helper nuevo)
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerMergeMigrationTests.cs`, `CustomerMergedWritesApiTests.cs`

**Interfaces:**
- Consumes: `Customer.MergedIntoId`, `MergedAt`, `EnsureNotMerged` (FC-01).
- Produces:
  - Columnas `merged_into_id uuid NULL` y `merged_at timestamptz NULL`, con el conversor `CustomerId ↔ Guid` del tipo no
    anulable (como `ClassificationId`, `:144-149`). Sin FK: §7 no la pide.
  - `IX_customers_tenant_merged_into` sobre `(tenant_id, merged_into_id) WHERE merged_into_id IS NOT NULL`.
  - `IX_customers_tenant_identification` con `.HasFilter("merged_into_id IS NULL")` (D-F11). La migración que genera EF la
    borra y la recrea; revisa que el `Down` la deje sin filtro.
  - `FindExistingIdentificationsAsync` suma `customer.MergedIntoId == null` (desvío F8; sin él, el `ToDictionary` de `:493`
    lanza con dos dueños).
  - `TryAttachWhatsAppUserIdAsync` suma `customer.MergedIntoId == null` al `WHERE` (desvío F4).
  - Los handlers de notas y etiquetas llaman `customer.EnsureNotMerged()` (las notas son otro agregado y no pasan por
    `Customer`); `UpdateCustomerHandler` lo llama antes de pedir el CUC para no quemar un consecutivo.
  - `CustomersApiHarness.MarkMergedAsync(string connectionString, Guid mergedId, Guid survivorId)`: el `UPDATE` que deja una
    fila como la deja la fusión (`merged_into_id`, `merged_at = now()`, `is_active = false`, `version = version + 1`). Lo usan
    FC-07 y FC-08 para probar sin la ruta de fusión.

- [ ] **Step 1: Escribe las pruebas que fallan**

`CustomerMergeMigrationTests.cs`:
```csharp
[Fact] public async Task TheIdentificationIndexIsPartialOnNotMerged()   // pg_indexes.indexdef de IX_customers_tenant_identification contiene "WHERE (merged_into_id IS NULL)" y es UNIQUE
[Fact] public async Task TheMergedIntoIndexExistsAndIsPartial()         // IX_customers_tenant_merged_into con "WHERE (merged_into_id IS NOT NULL)"
```
`CustomerMergedWritesApiTests.cs` (`CreateManager`; dos clientes por la API, uno marcado con `MarkMergedAsync`):
```csharp
[Fact] public async Task EveryWriteOnAMergedCustomerIs422Merged()
// PUT /{id}, POST /{id}/activate, POST /{id}/deactivate, POST /{id}/addresses, POST /{id}/notes, PUT /{id}/tags,
// PUT /{id}/marketing-consent → 422 code "customers.customer.merged" cada uno
[Fact] public async Task AMergedCustomerStillReads()                    // GET /{id}, GET /{id}/notes, GET /{id}/tags → 200
[Fact] public async Task TheSurvivorCanTakeTheMergedDocument()          // PUT del que queda con el NIT del fusionado → 200
[Fact] public async Task TwoOwnersOfTheSameDocumentOneMergedDoNotBreakTheLookup()
// tras el PUT anterior: FindExistingIdentificationsAsync (resuelto del scope) devuelve al que queda; un POST con ese NIT → 422 identification_taken con existing = el que queda
```
Las rutas exactas de notas, etiquetas y consentimiento son las de `CustomerEngagementEndpoints.cs`.

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerMergeMigrationTests|FullyQualifiedName~CustomerMergedWritesApiTests"`
Expected: FAIL de compilación (`MarkMergedAsync` no existe); con el helper, FAIL en ejecución (`column "merged_into_id" does not exist`).

- [ ] **Step 3: Implementa** el mapeo (quita los `Ignore` de FC-01), genera la migración con el comando de Global Constraints,
  el filtro y el `WHERE` del repositorio, las llamadas en los handlers y el helper.

- [ ] **Step 4: Corre y verifica que pasan** (las dos clases, más `CustomerCompletenessMigrationTests|CustomerDuplicateIdentificationApiTests|CustomerWriteApiTests|CustomerWhatsAppDirectoryTests|CustomerProvisioningIntegrationTests`).
Expected: PASS; las migraciones viejas siguen subiendo.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Customers tests/Modules/Customers
git commit -m "feat(customers): FC-02 columnas de fusión e índice de identificación parcial"
```

---

### Task 3 (FC-03): `ICustomerMergeReferences` y su adaptador sobre los otros esquemas

**Files:**
- Create: `src/Modules/Customers/Modules.Customers.Application/ICustomerMergeReferences.cs`
- Create: `src/Bootstrapper/CustomerMergeReferences.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (junto a `IVariantUsageChecker`, `:893`)
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/CustomerMergeReferencesTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  /// Spec 2026-10-24 §4: lo que la fusión mueve fuera de Customers, en la transacción de CustomersDbContext.
  public sealed record CustomerMergeReferenceCounts(int Quotations, int Orders, int Conversations, int PosSales, int Deals);

  public interface ICustomerMergeReferences
  {
      /// Pasa al que queda toda referencia viva de los ids. Corre en la conexión y la transacción del CustomersDbContext del
      /// scope: un error de cualquier UPDATE deshace también lo de Customers. Sin ids no consulta.
      Task MoveAsync(Guid tenantId, Guid survivorId, IReadOnlyCollection<Guid> mergedIds, CancellationToken cancellationToken);
      Task<CustomerMergeReferenceCounts> CountAsync(Guid tenantId, IReadOnlyCollection<Guid> mergedIds, CancellationToken cancellationToken);
  }

  // Bootstrapper
  internal sealed class CustomerMergeReferences(CustomersDbContext customers) : ICustomerMergeReferences;
  ```
- `MoveAsync`, un `ExecuteSqlAsync` interpolado por sentencia, **en este orden** (Shopify último, para la prueba de atomicidad de
  FC-09), cada una con `tenant_id = {tenantId} AND <col> = ANY({ids})`:
  `quotations.quotations.client_id`, `quotations.orders.client_id`, `pos.sales.customer_id` (la foto `customer_name`… no se
  toca), `pipeline.deals.customer_id`, `pipeline.deal_quotations.customer_id`, `pipeline.deal_orders.customer_id`,
  `pipeline.triage_decisions.customer_id`, `messaging.conversations.customer_id`, la audiencia de campañas y
  `shopify.orders.qep_customer_id`. Ninguna sube `version` (desvío F15). `messaging.campaign_recipients` no se toca (§4).
  La de campañas:
  ```sql
  -- §4: sólo las que todavía no congelaron su audiencia. El DISTINCT quita al que queda si ya estaba; nunca crece, así que
  -- CK_campaigns_customers_cap sigue valiendo.
  UPDATE messaging.campaigns
     SET audience_customer_ids = ARRAY(
           SELECT DISTINCT CASE WHEN id = ANY({ids}) THEN {survivorId} ELSE id END FROM unnest(audience_customer_ids) AS id)
   WHERE tenant_id = {tenantId} AND status IN ('Draft', 'Scheduled') AND audience_customer_ids && {ids}
  ```
- `CountAsync`: un solo `SqlQuery` con cinco subconsultas `count(*)::int` (`quotations.quotations`, `quotations.orders`,
  `messaging.conversations`, `pos.sales`, `pipeline.deals`).
- El `<summary>` de la clase repite el costo de §4 (renombrar una columna compila y rompe la fusión en ejecución) y nombra las
  pruebas que lo amarran: esta y `CustomerMergeAcrossModulesApiTests.TheRealAdapterRunsAgainstTheMigratedSchema` (FC-09).

- [ ] **Step 1: Escribe las pruebas que fallan**

`CustomerMergeReferencesTests.cs`, con el patrón de `CatalogVariantUsageCheckerTests` (réplicas mínimas de las tablas, con los
mismos esquemas, tablas y columnas; `CustomersDbContext` sobre el contenedor):
```csharp
[Fact] public async Task EveryTableOfTheListMovesToTheSurvivor()
// una fila por tabla con customer B (y C) → MoveAsync(t, A, [B, C]) → todas con A
[Fact] public async Task AnotherTenantsRowsStayPut()
[Fact] public async Task ADraftOrScheduledAudienceSwapsTheMergedAndDropsTheRepeat()   // [A, B] → [A]; [B] → [A]
[Fact] public async Task ASendingOrCompletedCampaignKeepsItsAudience()
[Fact] public async Task CampaignRecipientsAreNeverTouched()
[Fact] public async Task CountsCountEachKindAndOnlyTheTenant()                        // 2 cotizaciones, 1 pedido, 0 ventas... → (2, 1, …)
[Fact] public async Task TheMoveRunsInsideTheCustomersTransaction()                   // BeginTransaction en CustomersDbContext, MoveAsync, rollback → filas con B
[Fact] public async Task NoMergedIdsDoNothingWithoutQuerying()                         // sin tablas creadas: no lanza, conteos en cero
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~CustomerMergeReferencesTests"`
Expected: FAIL de compilación (`CustomerMergeReferences` no existe).

- [ ] **Step 3: Implementa** el puerto, el adaptador y su registro scoped.

- [ ] **Step 4: Corre y verifica que pasan** (`CustomerMergeReferencesTests`, `CatalogVariantUsageCheckerTests` y
  `ArchitectureTests --filter "FullyQualifiedName~CustomersLayerTests|FullyQualifiedName~CompositionRootTests"`).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Customers/Modules.Customers.Application/ICustomerMergeReferences.cs src/Bootstrapper tests/Bootstrapper
git commit -m "feat(customers): FC-03 puerto de referencias de la fusión en los otros esquemas"
```

---

### Task 4 (FC-04): Transacción de Customers y `ICustomerMergeStore`

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/ICustomersUnitOfWork.cs`, `…/Infrastructure/Persistence/CustomersUnitOfWork.cs`
- Create: `src/Modules/Customers/Modules.Customers.Application/ICustomerMergeStore.cs`, `…/Infrastructure/Persistence/CustomerMergeStore.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/CustomersInfrastructureExtensions.cs` (registro scoped)
- Modify (dobles): `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerWriteDoubles.cs` (`UnitOfWork`, `:47`),
  `EngagementTestBed.cs` (`CountingUnitOfWork`, `:52`), `CustomerWhatsAppDirectoryRaceTests.cs` (`TakenUnitOfWork`, `:39`)
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerMergeStoreTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  // ICustomersUnitOfWork — copia de ICatalogUnitOfWork.BeginTransactionAsync (CatalogUnitOfWork.cs:159-166).
  Task<ICustomersTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
  public interface ICustomersTransaction : IAsyncDisposable { Task CommitAsync(CancellationToken cancellationToken); }

  public sealed record CustomerMergeOwnedCounts(int Notes, int Addresses);

  /// Spec 2026-10-24 §3.2–§3.4: lo que la fusión lee y escribe en customers sin pasar por el agregado. Todo con tenantId.
  public interface ICustomerMergeStore
  {
      /// Los clientes que hoy apuntan a alguno de estos (la cadena de D-F2).
      Task<IReadOnlyList<Guid>> FindIdsMergedIntoAsync(Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);
      Task RepointMergedIntoAsync(Guid tenantId, IReadOnlyCollection<Guid> customerIds, Guid survivorId, CancellationToken cancellationToken);
      Task<CustomerMergeOwnedCounts> CountOwnedAsync(Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);
      /// Notas y direcciones pasan al que queda; las direcciones llegan con is_principal = false (§3.2 paso 3).
      Task MoveOwnedAsync(Guid tenantId, Guid survivorId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);
      /// merged_into_id del cliente; null si no está fusionado o no es del tenant.
      Task<Guid?> FindSurvivorIdAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);
      /// El que quedó del fusionado más viejo (created_at, id) con ese par exacto; null si ninguno.
      Task<Guid?> FindSurvivorIdByIdentificationAsync(Guid tenantId, IdentificationType type, string number, CancellationToken cancellationToken);
  }
  ```
- `MoveOwnedAsync`: dos `ExecuteUpdateAsync` (no cargan entidades): notas por `tenant_id` y `customer_id`; direcciones, que no
  tienen `tenant_id`, con el `customer_id` dentro de los clientes del tenant
  (`dbContext.Customers.Any(c => c.TenantId == tenantId && c.Id == address.CustomerId)`). Las notas no cambian su `version`.

- [ ] **Step 1: Escribe las pruebas que fallan**

`CustomerMergeStoreTests.cs` (clientes por la API o por SQL como `CustomerAnomaliesApiTests.InsertAsync`, notas y direcciones por la API):
```csharp
[Fact] public async Task MoveOwnedMovesNotesAndAddressesAndKeepsTheSurvivorsPrincipal()
[Fact] public async Task MoveOwnedNeverTouchesAnotherTenant()
[Fact] public async Task RepointRewritesTheChainOnlyInTheTenant()        // C → B; Repoint([C], A) → C apunta a A
[Fact] public async Task CountOwnedCountsNotesAndAddresses()
[Fact] public async Task FindSurvivorIdFollowsTheMergeAndIsNullOtherwise()
[Fact] public async Task FindSurvivorIdByIdentificationUsesTheOldestMerged()
[Fact] public async Task ARolledBackTransactionLeavesEverythingAsItWas()  // BeginTransactionAsync + MoveOwnedAsync sin Commit → notas con B
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerMergeStoreTests"`
Expected: FAIL de compilación (`ICustomerMergeStore`, `BeginTransactionAsync`).

- [ ] **Step 3: Implementa** la transacción, el store, el registro y los tres dobles (`BeginTransactionAsync` devuelve un
  `ICustomersTransaction` que no hace nada).

- [ ] **Step 4: Corre y verifica que pasan** (`CustomerMergeStoreTests` y `Customers.UnitTests` de las clases con los dobles:
  `CustomerWriteHandlersTests|CustomerProvisioningTests|CustomerNoteHandlerTests|CustomerTagHandlerTests|CustomerWhatsAppDirectoryRaceTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Customers tests/Modules/Customers
git commit -m "feat(customers): FC-04 transacción explícita y almacén de la fusión"
```

---

### Task 5 (FC-05): Permiso `customers.customer.merge` y `GET …/merge-preview`

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomersPermissions.cs`
- Create: `src/Modules/Customers/Modules.Customers.Application/CustomerMerge.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomersDtos.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Api/CustomerEndpoints.cs` (ruta después de `GET /{customerId:guid}`, `:51-55`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (rol `admin` `:1129-1137`, rol `advisor` `:1227-1228`,
  `PermissionDefinition` junto a `CustomerImport` `:1430-1439`, política `:1936-1938`, handler `:295-306`)
- Modify: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs` (`PermissionDiscoveryFindsTheSixtyOneConstants`, `:171-184`)
- Test: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs`, `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerMergeValidatorTests.cs`,
  `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerMergePreviewApiTests.cs`

**Interfaces:**
- Consumes: `CustomerMergePlan`, `CustomerTagAssignment.PlanMerge` (FC-01), `ICustomerMergeReferences.CountAsync` (FC-03),
  `ICustomerMergeStore.FindIdsMergedIntoAsync`/`CountOwnedAsync` (FC-04), `CustomerIdentificationNormalizer.Normalize`.
- Produces:
  ```csharp
  // CustomersPermissions — D-F1: aparte de manage para que un rol custom pueda no llevarlo.
  public const string CustomerMerge = "customers.customer.merge";
  // PermissionDefinition: "Fusionar clientes", "Permite fusionar clientes duplicados en uno. No se puede deshacer.",
  // "Customers", "high", RequiredModules: [ModuleKeys.Customers]

  // CustomerMerge.cs
  public static class CustomerMergeLimits { public const int MaxMerged = 10; }   // D-F9
  public enum CustomerMergeMoveKind { Quotations, Orders, Conversations, PosSales, Deals, Notes, Addresses, Tags }
  public sealed record GetCustomerMergePreviewQuery(Guid TenantId, Guid SurvivorId, IReadOnlyList<Guid>? MergedCustomerIds)
      : IQuery<CustomerMergePreviewResponse>;
  public sealed class GetCustomerMergePreviewValidator : AbstractValidator<GetCustomerMergePreviewQuery>;
  public sealed class GetCustomerMergePreviewHandler(...) : IQueryHandler<GetCustomerMergePreviewQuery, CustomerMergePreviewResponse>;
  /// Las reglas de los ids, compartidas con el comando de FC-06. Clave "MergedCustomerIds".
  internal static class CustomerMergeIdRules { /* extensión sobre IRuleBuilder */ }

  // CustomersDtos.cs
  /// B1: Version es la del Customer, la que el POST manda en If-Match. IdentificationType y Cuc pueden ser null (incompleto).
  public sealed record CustomerMergePartyDto(
      Guid Id, string Name, string? IdentificationType, string? IdentificationNumber, string? Cuc, bool IsComplete, bool IsActive, long Version);
  public sealed record CustomerMergeMoveDto(string Kind, int Count);
  /// moves viaja con las ocho clases siempre, en el orden del enum y también en cero (la confirmación dibuja la que está en
  /// cero en gris sin conocer el enum). filledFields son nombres de CustomerMergeField (B2). documentsDiffer (§3.4): algún
  /// fusionado tiene número distinto del que queda al normalizarlo; si a uno de los dos le falta el número, no cuenta.
  public sealed record CustomerMergePreviewResponse(
      CustomerMergePartyDto Survivor, IReadOnlyList<CustomerMergePartyDto> Merged, bool DocumentsDiffer,
      IReadOnlyList<CustomerMergeMoveDto> Moves, IReadOnlyList<string> FilledFields);
  ```
- Validador (las mismas reglas que FC-06): `MergedCustomerIds` no nulo, entre 1 y `MaxMerged`, sin `Guid.Empty`, sin repetir,
  sin el `SurvivorId`. Mensajes: «Elige entre 1 y 10 clientes para fusionar.», «No repitas un cliente.», «El cliente que queda
  no puede estar entre los que se fusionan.».
- Handler, en este orden: `EnsureAuthorized(CustomerMerge)` → validar → `FindManyAsync(tenant, [survivor, …merged])` (sin
  tracking) → sin el que queda, `CustomerNotFound.For` (404, desvío F2) → algún fusionado ausente →
  `CustomersDomainException("customers.merge.customer_not_found", …)` → `CustomerMergePlan.Create` (las reglas) → cadena =
  `FindIdsMergedIntoAsync(merged)` → `CountAsync(tenant, merged ∪ cadena)` y `CountOwnedAsync(tenant, merged ∪ cadena)` →
  etiquetas con `ListAssignmentsAsync` por cliente, cada fusionado por `AssignedAt, TagId`, y `PlanMerge` → `Tags = Added.Count`.
- Ruta: `group.MapGet("/{customerId:guid}/merge-preview", …)` con `Guid[]? mergedCustomerIds = null` (como `departmentIds` en
  `ListCustomersAsync`), `RequireAuthorization(CustomersPermissions.CustomerMerge)`, `Produces<CustomerMergePreviewResponse>()`, 403, 404, 422.

- [ ] **Step 1: Escribe las pruebas que fallan**

`CompositionRootTests.cs`: `PermissionDiscoveryFindsTheSixtyOneConstants` pasa a `…SixtyTwo…` con `Assert.Equal(62, …)` y la
línea del comentario; y
```csharp
[Fact] public void AdminAndAdvisorCarryTheCustomerMergePermission()
// "customers.customer.merge"; en admin y advisor; no en billing ni cashier; Category "Customers"; RequiredModules [customers]
```
`CustomerMergeValidatorTests.cs`:
```csharp
[Theory] [MemberData(nameof(InvalidIds))]
public void InvalidMergedIdsAre422OnMergedCustomerIds(string label)   // null, [], 11 ids, [Guid.Empty], [x, x], [survivor]
[Fact] public void OneToTenDistinctIdsPass()
```
`CustomerMergePreviewApiTests.cs` (cliente con `CustomerRead`, `CustomerManage` y `CustomerMerge`; GYSELYS NIT 92-1396892 y
GISELY CE 92-1396892 por la API):
```csharp
[Fact] public async Task ThePreviewCarriesBothPartiesTheVersionAndEveryMoveEvenAtZero()
// survivor.version == 1; merged[0].cuc no vacío; moves == las ocho en orden con Notes/Addresses/Tags reales y el resto 0
[Fact] public async Task FilledFieldsSaysWhatTheSurvivorGains()       // survivor sin businessName (PUT) → filledFields ["BusinessName"]
[Fact] public async Task DocumentsDifferIsFalseForTheSameNumberAndTrueForAnother()
[Fact] public async Task ThePreviewWritesNothing()                    // versiones, notas y merged_into_id iguales después
[Fact] public async Task EachRuleIs422WithItsCode()                   // already_merged, survivor_inactive, survivor_incomplete
[Fact] public async Task AMergedIdFromAnotherTenantIsNotFound()       // Review Focus 4: 422 customers.merge.customer_not_found, sin moves
[Fact] public async Task AnUnknownSurvivorIs404()
[Fact] public async Task AnotherTenantIs403AndWithoutMergeIs403()
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"`
Expected: FAIL de compilación (`CustomersPermissions.CustomerMerge`).

- [ ] **Step 3: Implementa** la constante, la definición, los dos roles, la política, los DTOs, la query, las reglas
  compartidas, el validador, el handler, la ruta y el registro.

- [ ] **Step 4: Corre y verifica que pasan** (`CompositionRootTests|CustomersLayerTests`, `CustomerMergeValidatorTests`, `CustomerMergePreviewApiTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Customers src/Bootstrapper/QepServiceCollectionExtensions.cs tests/ArchitectureTests tests/Modules/Customers
git commit -m "feat(customers): FC-05 permiso de fusión y vista previa con los ocho conteos"
```

---

### Task 6 (FC-06): `POST …/customers/{survivorId}/merge`

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerMerge.cs`, `CustomersDtos.cs`, `CustomerAuditActions.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Api/CustomerEndpoints.cs` (ruta junto a la vista previa; `RequireVersion`
  privado copiado de `ShopifyEndpoints.cs:146-170`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:295-386`
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerMergeApiTests.cs`

**Interfaces:**
- Consumes: todo lo de FC-01 a FC-05; `ICustomerTagRepository.ListAssignmentsAsync`/`RemoveAssignment`/`AddAssignment`/`ListByIdsAsync`;
  `ICustomerRepository.FindAsync` (con tracking) y `FindManyAsync` (sin tracking, para la respuesta).
- Produces:
  ```csharp
  // CustomerAuditActions
  public const string Merged = "customers.customer.merged";

  public sealed record MergeCustomersCommand(Guid TenantId, Guid SurvivorId, IReadOnlyList<Guid>? MergedCustomerIds, long ExpectedVersion)
      : ICommand<CustomerMergeResult>;
  public sealed class MergeCustomersValidator : AbstractValidator<MergeCustomersCommand>;   // CustomerMergeIdRules
  public sealed record CustomerMergeResult(CustomerDto Survivor, IReadOnlyList<CustomerMergeDroppedTagDto> DroppedTags);
  public sealed class MergeCustomersHandler(...) : ICommandHandler<MergeCustomersCommand, CustomerMergeResult>;

  // CustomersDtos.cs
  /// D-F5: sólo lo que el aviso nombra; el color y el conteo no hacen falta para decir cuáles no cupieron.
  public sealed record CustomerMergeDroppedTagDto(Guid Id, string Name);
  /// B3: los mismos campos que CustomerResponse, en el mismo objeto, más DroppedTags (siempre, [] si no hay). Sin versión: el
  /// frontend navega a la ficha del que quedó al terminar.
  public sealed record CustomerMergeResponse(/* los campos de CustomerResponse, incluido MergedInto (siempre null) */,
      IReadOnlyList<CustomerMergeDroppedTagDto> DroppedTags);
  public sealed record MergeCustomersRequest(IReadOnlyList<Guid>? MergedCustomerIds);
  ```
- El handler, en este orden fijo (§3.2; el `<summary>` lo copia y dice por qué hay dos `SaveChanges`):
  1. `EnsureAuthorized(CustomerMerge)` → validar.
  2. `await using var transaction = await unitOfWork.BeginTransactionAsync(…)`.
  3. El que queda con `FindAsync` (tracking) → 404; `survivor.Version != ExpectedVersion` →
     `RequestConcurrencyException("concurrency.conflict", …)` (412).
  4. Cada fusionado con `FindAsync` → `customers.merge.customer_not_found`; `CustomerMergePlan.Create`.
  5. Cadena = `FindIdsMergedIntoAsync(merged)`; asignaciones de etiquetas de todos y `PlanMerge`.
  6. `plan.MarkMerged(now)`; auditoría `Merged` por fusionado; **primer** `SaveChangesAsync`.
  7. `RepointMergedIntoAsync(cadena, survivor)`.
  8. `plan.FillSurvivor(now)`; borra todas las asignaciones de los fusionados y agrega las `Added` al que queda
     (`CustomerTagAssignment.Create(survivor, tag, tenant, actor, now)`); auditoría `Merged` del que queda; **segundo** `SaveChangesAsync`.
  9. `MoveOwnedAsync(tenant, survivor, merged)`; `references.MoveAsync(tenant, survivor, merged ∪ cadena)`.
  10. `transaction.CommitAsync`; la respuesta se arma con `FindManyAsync(tenant, [survivor])` (sin tracking: trae la libreta
      que movió el SQL) y `ToDtoAsync`; `DroppedTags` con `ListByIdsAsync(Dropped)`.
- Ruta: `group.MapPost("/{customerId:guid}/merge", …)`: `RequireVersion(httpContext)` (428 `precondition.if_match_required`,
  acepta `"7"` y `W/"7"`), `RequireAuthorization(CustomerMerge)`, `Accepts<MergeCustomersRequest>`, `Produces<CustomerMergeResponse>()`,
  403, 404, 412, 422, 428.

- [ ] **Step 1: Escribe las pruebas que fallan**

`CustomerMergeApiTests.cs` (mismo arreglo que FC-05; `If-Match` con `survivor.version` de la vista previa):
```csharp
[Fact] public async Task MergingAnswersTheSurvivorWithDroppedTagsAndMarksTheMerged()
// 200, id del que queda, droppedTags []; GET del fusionado → isActive false; merged_into_id/merged_at en la base
[Fact] public async Task EmptyFieldsAreFilledAndNothingIsOverwritten()
[Fact] public async Task TheSurvivorWithTheSmallerIdTakesTheMergedBsuid()          // Review Focus 1: el que queda creado primero; BSUID por SQL en el fusionado → whatsapp_user_id en el que queda
[Fact] public async Task AMergedBsuidStaysWhenTheSurvivorHasOne()
[Fact] public async Task TheMarketingOptOutIsInheritedWithItsSourceAndDate()
[Fact] public async Task NotesAndAddressesMoveAndThePrincipalStaysTheSurvivors()
[Fact] public async Task TagsAreUnitedUpToTheCapAndTheRestComeBackAsDroppedTags()  // 19 + 3 → 20; droppedTags [{id,name}] × 2
[Fact] public async Task AChainIsRewrittenToTheNewSurvivor()                       // C en B, luego B en A → C.merged_into_id = A
[Fact] public async Task TheMergeIsAuditedOnEveryCustomer()                         // outbox payload->>'action' = 'customers.customer.merged' = 1 + n
[Fact] public async Task WithoutIfMatchIs428AndAStaleVersionIs412()
[Fact] public async Task ASecondMergeWithTheOldVersionIs412()                       // Review Focus 5: misma versión dos veces → 200 y 412
[Fact] public async Task EachRuleIs422WithItsCodeAndNothingChanges()
[Fact] public async Task InvalidMergedIdsAre422OnMergedCustomerIds()
[Fact] public async Task AMergedIdFromAnotherTenantIsNotFound()                     // Review Focus 4
[Fact] public async Task AnotherTenantIs403AndWithoutMergeIs403()
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerMergeApiTests"`
Expected: FAIL de compilación (`CustomerMergeResponse`, `MergeCustomersCommand`).

- [ ] **Step 3: Implementa** la acción, el comando, el validador, el handler, los DTOs, la ruta con `If-Match` y el registro.

- [ ] **Step 4: Corre y verifica que pasan** (`CustomerMergeApiTests`, `CustomerMergePreviewApiTests`, `CustomerEngagementApiTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Customers src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Customers
git commit -m "feat(customers): FC-06 fusionar clientes en una transacción"
```

---

### Task 7 (FC-07): El fusionado no aparece; `mergedInto` y `createdAt`

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerRepository.cs` (`FilteredQuery`, `:227-274`)
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerAudienceRepository.cs` (`InAudience`, `:122`)
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerDuplicateQueries.cs` (`FindByNormalizedIdentificationAsync` y `FilterOf`)
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerPhoneDirectory.cs` (`ListByPhoneAsync`)
- Modify: `src/Bootstrapper/CustomerReportSource.cs` (`FilterCustomersAsync`, `:270-272`)
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomersDtos.cs` (`CustomerDto`, `CustomerResponse`, `CustomerListItemResponse`), `GetCustomer.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Api/CustomerEndpoints.cs` (`ToResponse`, `ToListItem`)
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerMergeExclusionTests.cs`,
  `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/MergedCustomerReportApiTests.cs`

**Interfaces:**
- Consumes: `MarkMergedAsync` (FC-02), `CustomerMergeResponse` (FC-06, suma `MergedInto` también).
- Produces:
  ```csharp
  /// §3.3 (BFF): con un valor, la ficha navega a la del que quedó; la pantalla no adivina por un 404.
  public sealed record CustomerMergedIntoDto(Guid Id, string Name);
  // CustomerDto: CustomerMergedIntoDto? MergedInto = null (al final). CustomerResponse: ídem, siempre en el JSON (null si no).
  // CustomerListItemResponse: DateTimeOffset CreatedAt al final (B4: «Fusionar con…» propone al más viejo).
  ```
- Un solo filtro, `MergedIntoId == null` (o `merged_into_id IS NULL` en SQL), en: `FilteredQuery` (cubre listado, búsqueda,
  exportación y `SearchSelectableAsync`, es decir POS y el buscador de cotizaciones), `InAudience` (lote y conteo de campañas;
  `FindContactAsync` no, porque el destinatario ya congelado se omite como inactivo), la consulta de coincidencias y los
  tres SQL de anomalías (vía `FilterOf`), `ListByPhoneAsync` (desvío F8) y el reporte de clientes.
- `GetCustomerHandler`: si `customer.MergedIntoId` tiene valor, el nombre del que quedó con `FindNamesByIdsAsync` y
  `dto with { MergedInto = … }`. Los demás handlers devuelven siempre `null` (un fusionado no se escribe).

- [ ] **Step 1: Escribe las pruebas que fallan**

`CustomerMergeExclusionTests.cs`:
```csharp
[Fact] public async Task AMergedCustomerIsNotListedNorSearched()      // GET / con search por nombre, CUC y documento: no está
[Fact] public async Task TheRepositoryReadsLeaveTheMergedOut()        // ListForExportAsync y SearchSelectableAsync del scope
[Fact] public async Task AMergedCustomerIsNotInACampaignAudience()    // ListAudienceBatchAsync y CountAudienceAsync, por id y por etiqueta
[Fact] public async Task AMergedCustomerLeavesTheAnomaliesAndTheMatches()   // el grupo de dos desaparece; identification-matches vacío
[Fact] public async Task AMergedCustomerIsNotASharedPhoneCustomer()   // ListByPhoneAsync
[Fact] public async Task TheDetailOfAMergedCustomerSaysWhereItWent()  // GET /{merged} → 200, mergedInto { id, name } del que queda
[Fact] public async Task ANotMergedDetailCarriesMergedIntoNull()      // la propiedad existe en el JSON y vale null
[Fact] public async Task TheListItemCarriesCreatedAt()
```
`MergedCustomerReportApiTests.cs` (patrón de `IncompleteCustomerReportApiTests`):
```csharp
[Fact] public async Task AMergedCustomerIsNotCountedNorListed()      // /reports/customers y su resumen
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerMergeExclusionTests"`
Expected: FAIL de compilación (`MergedInto`, `CreatedAt` del ítem); sin esas dos pruebas, FAIL en ejecución (el fusionado aparece).

- [ ] **Step 3: Implementa** el filtro en cada lugar, el DTO, `GetCustomerHandler` y los dos mapeos del endpoint.

- [ ] **Step 4: Corre y verifica que pasan** (`CustomerMergeExclusionTests`, `CustomerApiTests`, `CustomerAnomaliesApiTests`,
  `CustomerIdentificationMatchesApiTests`, `CustomerDuplicateQueriesTests`, `CustomerAudienceDirectoryIntegrationTests`,
  `CustomerSelectableSearchTests`, `CustomerPhoneDirectoryTests`, y en Reporting `MergedCustomerReportApiTests|CustomerReportApiTests|CustomerReportSummaryApiTests`).
Expected: PASS; `TheMatchQueryUsesTheNormalizedIndex` sigue verde.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Customers src/Bootstrapper/CustomerReportSource.cs tests/Modules/Customers tests/Modules/Reporting
git commit -m "feat(customers): FC-07 el fusionado sale de listas, buscadores y reportes"
```

---

### Task 8 (FC-08): El fusionado se redirige al que quedó

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerWhatsAppDirectory.cs` (`EnsureAsync` `:22-66`, `WinnerAsync` `:134-139`)
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerInstagramDirectory.cs` (`:22-41`)
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerRepository.cs` (`FindOldestByPhoneE164Async` `:662-667`,
  `SearchIdsByIdentificationNumberAsync` `:502-523`, `SearchIdsByCucAsync` `:525-546`)
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerPhoneDirectory.cs` (`MatchAsync`)
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerAudienceRepository.cs` (`MarkMarketingOptOutAsync`)
- Modify: `src/Modules/Customers/Modules.Customers.Application/ImportCustomers.cs` (antes del chequeo `customer_inactive`, `:513-528`), `CustomersDtos.cs` (`ImportRowError`)
- Modify: `src/Bootstrapper/ShopifyCustomerDirectory.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyAdaptersTestDoubles.cs`, `ShopifyAdaptersTests.cs` (cinco `new ShopifyCustomerDirectory`, `:440-510`)
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerWhatsAppDirectoryTests.cs`, `CustomerPhoneDirectoryTests.cs`,
  `CustomerAudienceDirectoryIntegrationTests.cs`, `CustomerStatusAndImportApiTests.cs`, `CustomerMergeRedirectTests.cs` (nuevo),
  `tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyAdaptersTests.cs`

**Interfaces:**
- Consumes: `ICustomerMergeStore.FindSurvivorIdByIdentificationAsync` (FC-04), `MarkMergedAsync` (FC-02).
- Produces:
  - Un salto basta siempre: `MergedIntoId` apunta a un no fusionado (D-F2). La regla «`MergedIntoId ?? Id`» va en:
    `EnsureAsync` (por BSUID, por teléfono —y el `TryAttach` se hace sobre el que quedó— y en `WinnerAsync`), el de Instagram
    (por IGSID y en la carrera), y `ShopifyCustomerDirectory` (`ToRef` pasa a `ToRefAsync`: un fusionado se cambia por
    `FindAsync` del que quedó, para id, teléfono, correo y documento).
  - `FindOldestByPhoneE164Async`: ordena primero los no fusionados (`OrderBy(c => c.MergedIntoId != null).ThenBy(CreatedAt).ThenBy(Id)`);
    el llamador redirige si el único es un fusionado.
  - `CustomerPhoneDirectory.MatchAsync`: el mismo orden; si el ganador de un número es un fusionado, sale el que quedó (id,
    nombre, completitud con una segunda consulta por los ids). Nunca elige un fusionado como «el más viejo» (§3.3).
  - `ShopifyCustomerDirectory.FindByDocumentAsync`: sin dueño vivo, `FindSurvivorIdByIdentificationAsync` (desvío F9). Recibe
    `ICustomerMergeStore` en el constructor; `ShopifyAdaptersTestDoubles` suma `FakeCustomerMergeStore`.
  - `MarkMarketingOptOutAsync` marca al que quedó cuando el id es de un fusionado (desvío F5), en el mismo `ExecuteUpdate`.
  - `SearchIdsByCucAsync` y `SearchIdsByIdentificationNumberAsync` devuelven además el `merged_into_id` de cada fusionado que
    coincide (desvío F7).
  - `ImportRowError` suma `string? MergedIntoCuc = null` al final; la fila con el CUC de un fusionado responde
    `customers.import.row.customer_merged`, campo `Cuc`, mensaje «The customer with that Cuc was merged into {cuc}.» y
    `MergedIntoCuc` = CUC del que quedó (D-F6), en vez de `customer_inactive`.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// CustomerWhatsAppDirectoryTests
[Fact] public async Task AnInboundWithTheBsuidLeftOnTheMergedLandsOnTheSurvivor()   // Review Focus 3: Existing con el que queda; sin cliente nuevo
[Fact] public async Task APhoneOnlyTheMergedHasResolvesToTheSurvivorAndAttachesThere()
// CustomerInstagramDirectory (en el mismo archivo o en CustomerMergeRedirectTests)
[Fact] public async Task AnIgsidLeftOnTheMergedLandsOnTheSurvivor()
// CustomerPhoneDirectoryTests
[Fact] public async Task MatchNeverPicksAMergedAsTheOldest()          // fusionado más viejo + el que queda con el mismo número → el que queda
[Fact] public async Task AMatchOnlyAMergedHasResolvesToTheSurvivor()
// CustomerAudienceDirectoryIntegrationTests
[Fact] public async Task TheMetaOptOutOfAMergedLandsOnTheSurvivor()   // marketing_opt_out del fusionado sigue false; el que queda true, source meta
// CustomerMergeRedirectTests
[Fact] public async Task TheCucOfAMergedFindsTheSurvivorsOrdersToo()  // SearchIdsByCucAsync contiene los dos ids
[Fact] public async Task TheDocumentOfAMergedFindsTheSurvivorsQuotationsToo()
// CustomerStatusAndImportApiTests
[Fact] public async Task ARowWithTheCucOfAMergedFailsWithCustomerMergedAndTheSurvivorsCuc()
// ShopifyAdaptersTests
[Fact] public async Task FindByIdOfAMergedReturnsTheSurvivor()
[Fact] public async Task FindByDocumentOnlyAMergedHasReturnsTheSurvivor()
[Fact] public async Task FindByPhoneAndEmailOfAMergedReturnTheSurvivor()
```
En las unitarias de Shopify, el fusionado se arma con `CustomerMergePlan.Create(survivor, [merged]).MarkMerged(now)`.

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerWhatsAppDirectoryTests|FullyQualifiedName~CustomerPhoneDirectoryTests|FullyQualifiedName~CustomerMergeRedirectTests"`
Expected: FAIL en ejecución (devuelve el id del fusionado / no trae al que quedó).

- [ ] **Step 3: Implementa** cada redirección, el campo de `ImportRowError` y el constructor del adaptador de Shopify.

- [ ] **Step 4: Corre y verifica que pasan** (las clases del Step 1, `CustomerWhatsAppDirectoryRaceTests`, `CustomerProvisioningIntegrationTests`
  y en Bootstrapper `ShopifyAdaptersTests|MessagingCustomerDirectoryAudienceTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Customers src/Bootstrapper/ShopifyCustomerDirectory.cs tests/Modules/Customers tests/Bootstrapper
git commit -m "feat(customers): FC-08 un fusionado se resuelve al cliente que quedó"
```

---

### Task 9 (FC-09): Pipeline resuelve al que quedó y la fusión de punta a punta

**Files:**
- Modify: `src/Modules/Pipeline/Modules.Pipeline.Application/PipelineDirectories.cs` (`IPipelineCustomerDirectory`, `:34-39`)
- Modify: `src/Bootstrapper/PipelineCustomerDirectory.cs`
- Modify: `src/Modules/Pipeline/Modules.Pipeline.Application/QuotationEvents.cs` (constructor `:26-32`, `ApplyOrderCreatedAsync` `:47`, `ApplyQuotationAsync` `:176`)
- Modify (dobles): `tests/Modules/Pipeline/Modules.Pipeline.UnitTests/PipelineFakes.cs` (`ApplierBed`, `:17`)
- Test: `tests/Modules/Pipeline/Modules.Pipeline.UnitTests/QuotationEventApplierTests.cs`,
  `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/PipelineDirectoriesTests.cs`,
  `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/CustomerMergeAcrossModulesApiTests.cs` (nuevo)

**Interfaces:**
- Consumes: `ICustomerMergeStore.FindSurvivorIdAsync` (FC-04), `ICustomerMergeReferences` (FC-03), la ruta de FC-06.
- Produces:
  ```csharp
  // IPipelineCustomerDirectory
  /// Spec 2026-10-24 §4 «lo que llega tarde»: el cliente que quedó si este se fusionó; el mismo id si no (o si no es del tenant).
  Task<Guid> ResolveSurvivorAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);
  // QuotationEventApplier: IPipelineCustomerDirectory customers al final del constructor; ApplyQuotationAsync y
  // ApplyOrderCreatedAsync arrancan con fact = fact with { ClientId = await customers.ResolveSurvivorAsync(...) } (desvío F16).
  // ApplierBed: FakePipelineCustomers con un diccionario Survivors (lo demás lanza NotSupportedException).
  ```
- `CustomerMergeAcrossModulesApiTests` vive en Pipeline porque su harness ya siembra clientes incompletos
  (`SeedCustomerAsync`), conversaciones (`SeedConversationAsync`) y negocios (`QuotationEventApplier`) contra el esquema migrado
  de todos los módulos; llama la ruta de fusión con `CustomersPermissions.CustomerMerge` en `X-Permissions`.

- [ ] **Step 1: Escribe las pruebas que fallan**

`QuotationEventApplierTests.cs`:
```csharp
[Fact] public async Task ASentOfAMergedCustomerOpensTheDealOnTheSurvivor()
[Fact] public async Task ALateSentWithTheMergedClientDoesNotUnlinkTheMovedDeal()   // negocio y foto ya en A; sent con B → sigue enlazada, sin QuotationUnlinked
[Fact] public async Task AnOrderCreatedOfAMergedCustomerWinsTheSurvivorsDeal()
```
`PipelineDirectoriesTests.cs`:
```csharp
[Fact] public async Task ResolveSurvivorReturnsTheSurvivorOrTheSameId()   // fusionado → el que queda; normal → él; otro tenant → él
```
`CustomerMergeAcrossModulesApiTests.cs`:
```csharp
[Fact] public async Task TheMergeMovesTheDealItsQuotationAndTheConversation()
// B con conversación y negocio (ApplySentAsync) → POST merge en A → deals, deal_quotations y conversations con A; el tablero muestra A
[Fact] public async Task AnotherTenantsRowsStayPut()
[Fact] public async Task TheRealAdapterRunsAgainstTheMigratedSchema()
// ICustomerMergeReferences del contenedor: CountAsync y MoveAsync sobre el esquema migrado no lanzan (42703/42P01) y cuentan lo sembrado
[Fact] public async Task AFailureInTheLastUpdateLeavesEverythingAsItWas()
// trigger por sentencia BEFORE UPDATE ON shopify.orders que lanza → 500; merged_into_id null, versión del que queda igual,
// notas, negocio y conversación con B; sin filas de auditoría customers.customer.merged
[Fact] public async Task ALateQuotationEventOfAMergedCustomerLandsOnTheSurvivor()  // tras la fusión, ApplySentAsync con B → negocio de A
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Pipeline/Modules.Pipeline.UnitTests --filter "FullyQualifiedName~QuotationEventApplierTests"`
Expected: FAIL de compilación (`ResolveSurvivorAsync`, el constructor del applier).

- [ ] **Step 3: Implementa** el método del puerto, el adaptador (con `ICustomerMergeStore`), el applier y el doble.

- [ ] **Step 4: Corre y verifica que pasan** (`QuotationEventApplierTests|QuotationEventPayloadsTests` y en integración
  `CustomerMergeAcrossModulesApiTests|PipelineDirectoriesTests|BoardApiTests|DealQuotationsApiTests|DealOrdersApiTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Pipeline src/Bootstrapper/PipelineCustomerDirectory.cs tests/Modules/Pipeline
git commit -m "feat(pipeline): FC-09 los eventos tardíos de un fusionado caen en el cliente que quedó"
```

---

### Task 10 (FC-10): README, CLAUDE.md y verificación de la rama

**Files:**
- Modify: `README.md`: la fila de `/api/v1/tenants/{tenantId}/customers` de «API implementada» (`:821`) suma `merge-preview`
  (`GET`) y `merge` (`POST`, `If-Match`) con `customers.customer.merge`; subsección nueva «### Clientes: fusión» después de
  «Clientes: documento repetido, coincidencias y anomalías» (`:1616-1636`) con las reglas de §2, las rutas, los códigos, las
  dos columnas, el índice parcial, qué se mueve por tabla (§4), qué se excluye y qué se redirige (FC-07, FC-08).
- Modify: `CLAUDE.md`, después de la entrada de `CatalogVariantUsageChecker` (`:450`), una entrada nueva:
  **«`CustomerMergeReferences` escribe en cinco esquemas por nombre.»** SQL crudo en Bootstrapper sobre
  `quotations.quotations`/`orders.client_id`, `pos.sales.customer_id`, `pipeline.deals`/`deal_quotations`/`deal_orders`/`triage_decisions.customer_id`,
  `messaging.conversations.customer_id`, `messaging.campaigns.audience_customer_ids` y `shopify.orders.qep_customer_id`, en la
  transacción de `CustomersDbContext`. Renombrar una de esas columnas compila y la fusión responde 500 (`42703`), y como todo es
  una transacción, no fusiona nada. Lo amarran `CustomerMergeReferencesTests` (réplicas, no siguen solas a una migración) y
  `CustomerMergeAcrossModulesApiTests.TheRealAdapterRunsAgainstTheMigratedSchema`. Una tabla nueva con un id de cliente vivo
  entra en `MoveAsync` y en esas dos pruebas, o la fusión la deja apuntando a un cliente inactivo. Y `merged_into_id` apunta
  siempre a un no fusionado: quien lea un fusionado sigue un solo salto.
- [ ] **Step 1:** Escribe las secciones con lo que quedó construido (rutas, códigos, permisos, índices tal cual están en el
  código); los comandos, en PowerShell.
- [ ] **Step 2:** Verifica la rama: `dotnet build`, las clases de prueba de FC-01 a FC-09 con sus filtros,
  `ArchitectureTests --filter "FullyQualifiedName~CustomersLayerTests|FullyQualifiedName~PipelineLayerTests|FullyQualifiedName~CompositionRootTests"`
  y `dotnet format --verify-no-changes --include <archivos tocados en la rama>`.
  Expected: PASS y build sin warnings nuevos.
- [ ] **Step 3: Commit**

```bash
git add README.md CLAUDE.md
git commit -m "docs: FC-10 fusión de clientes"
```

---

## Despliegue

- Una migración: `AddCustomerMerge` (Customers: dos columnas nullable, `IX_customers_tenant_merged_into` e
  `IX_customers_tenant_identification` recreado como parcial). Sin backfill. El permiso entra por los roles de sistema, en código.
- Backend y frontend juntos: un frontend viejo no ofrece fusionar y no conoce `mergedInto`; nada se rompe, pero el fusionado
  no redirige.
- Recrear el índice único bloquea `customers.customers` mientras se construye; con el padrón de hoy son segundos.

## Preguntas para el owner

1. **El POS o el alta con el documento que sólo conserva un fusionado completo.** Con el índice parcial (D-F11) ya no hay
   choque y nace un cliente nuevo con ese documento, aunque la persona se haya fusionado en otro. ¿Así, o el 422 de
   `identification_taken` (con `existing`) debe nombrar al que quedó?
2. **Buscar en Clientes el CUC de un fusionado** no devuelve nada: el listado lo excluye (§3.3) y el spec sólo redirige el CUC
   en pedidos, cotizaciones e importación. ¿Debe traer al que quedó?
3. **Una campaña en borrador abierta en otra pestaña.** Su versión no sube al reemplazar la audiencia (desvío F15); un guardado
   con la pantalla vieja devuelve el id del fusionado, que la congelación excluye, así que por esa vía el que quedó no recibe.
   ¿Subir la versión de la campaña (412 a quien la edita)?

## Contrato para el frontend

| Ruta | Permiso | Entrada | Respuesta |
|---|---|---|---|
| `GET /customers/{survivorId}/merge-preview?mergedCustomerIds=…&mergedCustomerIds=…` | `customers.customer.merge` | 1 a 10 ids distintos, sin el que queda | 200 `{ survivor, merged: [...], documentsDiffer, moves, filledFields }`; cada parte `{ id, name, identificationType \| null, identificationNumber \| null, cuc \| null, isComplete, isActive, version }` (B1: `survivor.version` es `Customer.Version`, un número entero, y es el `If-Match` del POST); `moves` siempre las ocho, en este orden: `Quotations`, `Orders`, `Conversations`, `PosSales`, `Deals`, `Notes`, `Addresses`, `Tags` (`{ kind, count }`, también en cero); `filledFields` ⊆ `Email`, `BusinessName`, `Phone`, `WhatsAppUserId`, `InstagramUserId`, `Identification` (tipo y número juntos), `Country`, `Address`, `MarketingOptOut` (B2), por nombre. 404 `customers.customer.not_found` (el que queda); 422 `validation.failed` (`errors.MergedCustomerIds`), `customers.merge.customer_not_found`, `customers.merge.already_merged`, `customers.merge.survivor_incomplete`, `customers.merge.survivor_inactive`; 403 |
| `POST /customers/{survivorId}/merge` | `customers.customer.merge` | `If-Match: "<survivor.version>"`, `{ mergedCustomerIds: [...] }` | 200: los campos de `CustomerResponse` del que queda en el mismo objeto más `droppedTags: [{ id, name }]` (siempre, `[]` si no hay) (B3); sin versión. 428 `precondition.if_match_required`, 412 `concurrency.conflict` (no reintenta: el frontend pide de nuevo la vista previa), y los mismos 404/422/403 de la vista previa |
| `GET /customers/{id}` | `customers.customer.read` | — | `CustomerResponse` + `mergedInto: { id, name } \| null`, siempre presente; un fusionado responde 200 con su ficha (`isActive: false`) |
| `GET /customers` | `customers.customer.read` | igual que hoy | sin fusionados; cada ítem suma `createdAt` (B4) |
| Cualquier escritura sobre un fusionado (`PUT /customers/{id}`, activar, inactivar, direcciones, notas, etiquetas, consentimiento) | — | — | 422 `customers.customer.merged` |
| `POST /customers/import` | `customers.customer.import` | — | fila con el CUC de un fusionado: `code: "customers.import.row.customer_merged"`, `field: "Cuc"`, `mergedIntoCuc: "<CUC del que quedó>"`; las demás filas traen `mergedIntoCuc: null` |

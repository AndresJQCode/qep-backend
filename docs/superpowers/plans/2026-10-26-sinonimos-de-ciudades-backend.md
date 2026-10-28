# Sinónimos de ciudades — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que un texto de ciudad de Shopify que no casa con DIVIPOLA entre a una bandeja global, que el operador de QCode lo
confirme una vez como sinónimo, y que desde ahí case solo en todos los tenants y corrija en segundo plano los pedidos `Pending` y
los clientes que ya habían entrado mal.

**Architecture:** Geography gana tres tablas globales (sinónimos, bandeja y ocurrencias), su propia unidad de trabajo, auditoría y
proyección del outbox, y los endpoints de operador. `CityRepository` consulta el sinónimo sólo si los dos nombres reales no casan, y
dice por cuál casó (D-C13). Shopify reporta lo que no casa por un puerto en el momento de importar, deja huella de lo que casó por
sinónimo (`shopify.city_corrections`), consume por inbox los eventos `geography.city_synonym.*` y siembra una fila de trabajo por
pedido o cliente; un worker las procesa una por unidad de trabajo, escribiendo en Quotations y Customers por puertos con adaptador en
Bootstrapper. El impacto que ve el operador lo calcula Geography con un lector entre esquemas en Bootstrapper (D-C3b).

**Tech Stack:** .NET 10, EF Core + Npgsql (PostgreSQL 18), FluentValidation, xUnit v3 + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-10-26-sinonimos-de-ciudades-design.md` (léelo entero; las reglas O1–O8 son del owner y no se
discuten). Bocetos: canvas «Sinónimos de ciudades» (artboards Main, Confirmar, Sinonimos, Corregir). El frontend tiene plan aparte y
se escribe desde la sección «Contrato HTTP para el frontend» de este plan.

**Rama:** `feature/sinonimos-de-ciudades`. Antes de **cada** tarea y de **cada** commit, `git branch --show-current` tiene que decir
esa rama; si dice `main` o `develop`, para. Nunca `git stash`.

## Global Constraints

- TDD obligatorio: RED con la salida literal antes del GREEN, los dos en el reporte de cada tarea. Si la prueba compila contra lo
  que ya existe, el RED es una aserción que falla, no uno de compilación.
- Conventional commits en español con el slice `SC-NN` en el asunto (`feat(geography): SC-01 …`), **sin atribución de IA y sin
  `Co-Authored-By`**, aunque el harness lo pida: la regla del repo gana.
- Por tarea corres **sólo** las clases tocadas (`--filter "FullyQualifiedName~<Clase>"`). La suite completa corre **una vez**, en
  SC-14. Docker arriba para las de integración. `Api.exe` (o `dotnet Api.dll`) corriendo bloquea `build`, `test` y `ef`: detenlo.
- Antes de cualquier prueba de integración, en línea aparte: `$env:Seed__Enabled = "false"`.
- Comandos en PowerShell: sin `&&`; encadenar es `A; if ($?) { B }`.
- Formato: `dotnet format --verify-no-changes --include <tus archivos>`, nunca el repo entero (el baseline tiene ~90k errores de
  fin de línea).
- Sin paquetes nuevos: `Directory.Packages.props`, `global.json` y `.mcp.json` no se tocan. **Toda tarea que agregue un
  `ProjectReference` regenera los lock files** con `dotnet restore --force-evaluate` y commitea los `packages.lock.json` que
  cambien junto con el `.csproj`: el `Dockerfile` restaura en `--locked-mode` y falla con `NU1004`.
- Migraciones con el factory de diseño, nunca con `--startup-project`:
  `dotnet ef migrations add <Nombre> --project src/Modules/<Modulo>/Modules.<Modulo>.Infrastructure --context <Modulo>DbContext -o Persistence/Migrations`.
  Tres en total: `AddCitySynonyms` (Geography, SC-02), `AddCityCorrections` (Shopify, SC-06), `AddCityCorrectionJobs` (Shopify, SC-07).
- Capas: Geography no referencia Shopify, Quotations ni Customers; sólo Tenancy (Application) y Audit. Shopify sigue viendo sólo
  Tenancy y Audit (`ShopifyLayerTests`). Todo cruce va por un puerto del módulo que lo necesita, con adaptador en `src/Bootstrapper/`.
  `Application` no referencia EF ni Npgsql; traducir errores de base va en `Infrastructure`, **por nombre de índice**.
- Permisos nuevos `operator.cities.read` y `operator.cities.manage`: constante **y** `PermissionDefinition` **y** política en
  `AddAuthorization` **y** en el rol `admin`. Sin la política el síntoma es 500, no 403. Los handlers se registran a mano en
  `QepServiceCollectionExtensions`; sin registro, 500.
- Doble capa en cada handler de operador: `GeographyOperatorAuthorization.EnsureAuthorized` **antes** de validar o tocar un
  repositorio. El tenant operador se pregunta con `IOperatorTenant.IsOperator` (`Platform:OperatorTenantId`), nunca contra
  `TenancySeeder.OperatorTenantId`. Cualquier falla: 403 `authorization.denied`.
- Ningún status se arma a mano: `GeographyDomainException` → 422, `ValidationException` → 422 `validation.failed` con `errors`,
  `ResourceNotFoundException` → 404, `ResourceConflictException` → 409, `RequestConcurrencyException` → 412
  (`src/Api/ApiExceptionHandler.cs:150-173`).
- BFF: todo grupo de impacto viaja aunque esté en cero; la lista de pedidos que no se tocan viaja completa; los enums viajan con su
  nombre (`Pending`, `Approved`). Cada DTO nuevo dice en su `<summary>` qué pantalla lo obliga a tener esa forma.
- Todo SQL entre esquemas filtra por `tenant_id` cuando la tabla lo tiene.
- Textos para personas y comentarios en español colombiano tuteando; códigos, rutas, enums y permisos tal cual.
- `docs/estado.md` **no se toca** en esta rama.

## Desvíos del spec (verificados contra el código el 2026-10-26)

La autoridad es el código. Ninguno cambia una regla del owner.

| # | Spec dice | El código dice | El plan hace |
|---|---|---|---|
| S1 | §4.4/§4.6: «una persona puso / editó la parte de envío a mano» | Un pedido no edita sus partes: `SaveOrderEdits.cs:220` («el pedido no edita sus partes (H5)»); `Order` sólo las asigna al crearse (`Order.cs:1029`) | Se conserva la huella completa de la parte (D-C4) y los grupos «se respeta» del impacto; hoy casi siempre salen en 0. Las pruebas editan la parte por SQL |
| S2 | D-C13: «cambia `ICityRepository.FindByNameAsync`» | Lo usan la importación por Excel y otros lookups (`CustomerGeographyLookup`, dobles de prueba en `Bootstrapper.UnitTests`) | Método hermano `MatchByNameAsync` → `CityNameMatch(City, SynonymId?)`; `FindByNameAsync` delega en él (mismo resultado, sinónimos incluidos: D-C8) |
| S3 | D-C13: la fila `origin = import` «en la misma unidad de trabajo del pedido» | El pedido lo escribe Quotations en su propio scope (`ShopifyOrderWriter.cs:29-38`); Shopify no puede escribir en esa transacción | Va en la unidad de trabajo de Shopify junto con `AttachCustomer`/`MarkImported`; idempotente por índice único, y el reintento con `AlreadyExisted` la agrega si falta |
| S4 | §5: `customer_id` «null si el cliente no nació incompleto por la ciudad» | §4.4 pide contar aparte a «los clientes a los que les falta algo más»; sin el id no hay cómo | `customer_id` = el cliente que **esta** importación creó incompleto, más `customer_city_only` (la ciudad era lo único que faltaba entre las precondiciones de `CompleteDataAsync`) |
| S5 | D-C6: «una fila de trabajo por ocurrencia» | Pedido y cliente viven en módulos y unidades de trabajo distintos | Una fila por **pedido o cliente** de la ocurrencia (`target` = `Order` \| `Customer`) |
| S6 | §6 no lista reintentar ni el recorrido | D-C6 pide reintentar los `Failed`; §10 deja el disparo de D-C12 a quien construye | `POST …/synonyms/{id}/retry` y `POST …/cities/backfill` (manual, idempotente, operador) |
| S7 | §4.4: cliente «sin cambios desde que lo creó Shopify» | No hay marca; `CreateIncompleteFromExternalSale` nace con `Version = 1` (`Customer.cs:535`), igual que el incompleto de WhatsApp | Se completa sólo si la ocurrencia lo trae (lo creó esta importación) **y** su versión sigue en 1 (`ShopifyCityCorrectionProcessor.IncompleteCustomerVersion`) |
| S8 | Artboard Confirmar: «sigue incompleto. Toma la ciudad» | Un incompleto no guarda ciudad (`CK_customers_complete_fields`) | No toma la ciudad: sólo se cuenta en `customersStayIncomplete` |
| S9 | D-C7: auditoría «en su tenant» con operador y sinónimo en el detalle | `IQuotationAuditPublisher.PublishByIntegration` (`:14`) e `ICustomersAuditPublisher.PublishByIntegration` (`:25`) no llevan detalle | La entrada la escribe `IShopifyAuditRecorder` (lleva `changedFieldsJson`) en la unidad de trabajo del trabajo, con el tenant del pedido o cliente |
| S10 | §4.2: la parte de envío «nombre, teléfono y dirección presentes» | `Order.EnsureCreatable` exige la parte completa, **con correo** (`Order.cs:475-478`, `OrderParty.IsComplete`) | `party_city_only` exige también el correo; sin él, confirmar no crea la parte (como hoy no la crearía el alta) |

## Review Focus

1. **Un sinónimo igual a una ciudad real del departamento** (por la normalización de D-C1, o porque `common-names.json` renombró una
   ciudad después de que el texto entró a la bandeja). Lo esperado: confirmar responde 422 `geography.synonym.matches_city`, y si
   igual existiera una fila así (por SQL), el nombre real gana. Pruebas: `ResolvingATextThatIsARealCityNameIs422` (SC-11),
   `ARealCityNameWinsOverASynonymWithTheSameText` (SC-02).
2. **El mismo pedido reportado dos veces** (webhook y conciliación a la vez, o un reintento con `AlreadyExisted`). Lo esperado: una
   sola ocurrencia y una sola fila `origin = import`. Pruebas: `TheSameMirrorRowReportedTwiceConcurrentlyLeavesOneOccurrence` (SC-03),
   `ARetriedImportDoesNotDuplicateTheImportCorrection` (SC-06).
3. **Un pedido que deja `Pending` entre la vista previa y la corrección.** Lo esperado: el trabajo termina `Skipped` con
   `order_not_pending`, el pedido queda intacto y no hay huella ni auditoría. Pruebas: `AttachExternalShippingOnANonPendingOrderIsNotPending`
   (SC-04), `AnOrderApprovedAfterThePreviewIsSkippedNotTouched` (SC-08).
4. **Alguien edita la parte de envío entre la corrección y la recorrección.** Lo esperado: `Skipped` con `shipping_changed`, la parte
   y la huella quedan como estaban. Pruebas: `CorrectShippingCityRespectsAPartyEditedBySql` (SC-04),
   `ARecorrectionRespectsAShippingPartyEditedAfterTheCorrection` (SC-08).
5. **Borrar un sinónimo con trabajos `Pending`.** Lo esperado: esos trabajos terminan `Skipped` con `synonym_deleted` sin escribir nada;
   lo ya aplicado no se deshace (O6). Pruebas: `JobsOfADeletedSynonymAreSkipped` (SC-08), `DeletingWhileJobsArePendingSkipsThem` (SC-14).

---

## Contrato HTTP para el frontend

Grupo: `/api/v1/tenants/{tenantId:guid}/operator/cities` (tag `Operator`). `{tenantId}` es el tenant desde el que actúa el operador.
JSON en camelCase. Paginación: `page` ≥ 1 (por defecto 1), `pageSize` 1–100 (por defecto 25), `search` ≤ 100 caracteres.

| Método y ruta | Permiso | Cuerpo | Respuesta | Errores |
|---|---|---|---|---|
| `GET /summary` | read | — | `CitySummaryDto` | 403 |
| `GET /unresolved?status=Pending\|Discarded&departmentId=&search=&page=&pageSize=` | read | — | `UnresolvedCityPageDto` (orden `lastSeenAt` desc, `id` desc; `status` por defecto `Pending`) | 403, 422 `validation.failed` (`Status`, `Page`, `PageSize`, `Search`) |
| `GET /unresolved/{id}` | read | — | `UnresolvedCityDetailDto` | 403, 404 `geography.unresolved.not_found` |
| `POST /unresolved/{id}/impact` | read | `CityChoiceRequest` | `ResolveImpactDto` (no escribe) | 403, 404, 422 `validation.failed` (`CityId`), `geography.unresolved.not_pending`, `geography.city.not_in_department`, `geography.synonym.matches_city` |
| `POST /unresolved/{id}/resolve` | manage | `CityChoiceRequest` | 200 `CitySynonymDto` | los de `impact` + 409 `geography.synonym.already_exists`, 412 `concurrency.conflict` |
| `POST /unresolved/{id}/discard` | manage | — | 200 `UnresolvedCityListItemDto` | 403, 404, 422 `geography.unresolved.not_pending`, 412 |
| `POST /unresolved/{id}/restore` | manage | — | 200 `UnresolvedCityListItemDto` | 403, 404, 422 `geography.unresolved.not_discarded`, 412 |
| `GET /synonyms?departmentId=&search=&page=&pageSize=` | read | — | `CitySynonymPageDto` (orden `updatedAt` desc, `id` desc; `search` contra el texto normalizado o el nombre de la ciudad) | 403, 422 |
| `POST /synonyms/{id}/impact` | read | `CityChoiceRequest` | `CorrectImpactDto` (no escribe) | 403, 404 `geography.synonym.not_found`, 422 `validation.failed`, `geography.synonym.same_city`, `geography.city.not_in_department` |
| `PUT /synonyms/{id}` | manage | `CorrectCitySynonymRequest` | 200 `CitySynonymDto` | los de `impact` + 412 `concurrency.conflict`, 422 `validation.failed` (`ExpectedVersion`) |
| `DELETE /synonyms/{id}` | manage | — | 204 | 403, 404 |
| `POST /synonyms/{id}/retry` | manage | — | 200 `CitySynonymDto` | 403, 404 |
| `POST /backfill` | manage | — | 200 `CityBackfillResultDto` | 403 |

Un sinónimo borrado responde 404 en todas sus rutas. Un 403 nunca depende de si el id existe.

```text
CityChoiceRequest            { cityId: uuid }
CorrectCitySynonymRequest    { cityId: uuid, expectedVersion: int64 }

DepartmentRefDto             { id, name, divipolaCode }
CityRefDto                   { id, name, divipolaCode }
CitySuggestionDto            { cityId, name, divipolaCode, divipolaName, similarity: int 0..100 }
CitySummaryDto               { pendingCount, discardedCount, synonymCount, ordersWaitingCount }
UnresolvedCityListItemDto    { id, text, normalizedText, department: DepartmentRefDto, status: "Pending"|"Resolved"|"Discarded",
                               orderCount, tenantCount, firstSeenAt, lastSeenAt, bestSuggestion: CitySuggestionDto|null, version }
UnresolvedCityPageDto        { items: UnresolvedCityListItemDto[], total, page, pageSize }
UnresolvedCityTenantDto      { tenantId, name, orderCount }
UnresolvedCityDetailDto      { id, text, normalizedText, department, status, sampleAddress, provinceCode|null, orderCount,
                               tenants: UnresolvedCityTenantDto[], firstSeenAt, lastSeenAt, suggestions: CitySuggestionDto[] (0..3),
                               synonymId|null, version }
ImpactTenantCountDto         { tenantId, name, count }
ImpactCountDto               { count, byTenant: ImpactTenantCountDto[] }
ImpactOrderDto               { tenantId, tenantName, orderId, orderNumber, status: "Approved"|"Invoiced"|"Cancelled" }
ImpactOrderListDto           { count, orders: ImpactOrderDto[] }   // completa, sin tope
ResolveImpactDto             { city: CityRefDto, ordersToFix, ordersMissingData, ordersWithShipping: ImpactCountDto,
                               ordersNotPending: ImpactOrderListDto, customersToComplete, customersStayIncomplete: ImpactCountDto }
CorrectImpactDto             { from: CityRefDto, to: CityRefDto, ordersToChange, ordersEdited: ImpactCountDto,
                               ordersNotPending: ImpactOrderListDto, customersToChange, customersEdited, occurrencesUncorrected: ImpactCountDto }
CitySynonymActorDto          { subjectId, email|null }
CitySynonymDto               { id, text, normalizedText, department, city: CityRefDto, correctedOrders, correctedCustomers,
                               pendingCorrections, failedCorrections, createdBy: CitySynonymActorDto, createdAt, updatedAt, version }
CitySynonymPageDto           { items: CitySynonymDto[], total, page, pageSize }
CityBackfillResultDto        { scanned, reported, alreadyReported, nowMatching, skipped }
```

Semántica que la pantalla dibuja:
- `ordersToFix`: `Pending`, sin parte de envío y con todo lo demás (`party_city_only`): se les crea la parte. `ordersMissingData`:
  `Pending`, sin parte y les falta nombre, teléfono, correo o dirección: siguen sin parte. `ordersWithShipping`: `Pending` con parte:
  no se tocan. `customersToComplete`: incompletos, no fusionados, activos, versión 1 y `customer_city_only`. `customersStayIncomplete`:
  los que la importación creó incompletos y les falta algo más que la ciudad.
- `ordersToChange` / `customersToChange`: lo que el sinónimo corrigió o casó al importarse (O8) y sigue como se escribió.
  `ordersEdited` / `customersEdited`: alguien lo cambió después. `occurrencesUncorrected`: ocurrencias sin huella (siguen igual).
- `ordersWaitingCount`: ocurrencias de filas `Pending`, sin mirar el estado del pedido. `similarity` es un entero; el mínimo para
  sugerir es 50 (inclusive).

## Contrato de eventos (Geography → Shopify)

Outbox de plataforma, `correlation_id` = id del sinónimo. Los nombres de campo son contrato: los escribe `GeographyEventPublisher`
(SC-11/SC-12) y los lee `CitySynonymEventPayloads` (SC-07) por nombre. Las dos pruebas llevan **este mismo literal**; si cambias uno,
cambia el otro.

```json
// geography.city_synonym.resolved.v1
{"synonymId":"…","departmentId":"…","cityId":"…","normalizedText":"CALLI","operatorSubjectId":"…","operatorTenantId":"…",
 "occurredAt":"2026-10-26T15:00:00+00:00",
 "occurrences":[{"occurrenceId":"…","tenantId":"…","source":"shopify","sourceRef":"…","orderId":"…","customerId":null,
                 "partyCityOnly":true,"customerCityOnly":false}]}
// geography.city_synonym.corrected.v1
{"synonymId":"…","departmentId":"…","previousCityId":"…","cityId":"…","operatorSubjectId":"…","operatorTenantId":"…",
 "occurredAt":"2026-10-26T15:00:00+00:00"}
```

## Mapa de archivos

| Archivo | Acción | Responsabilidad |
|---|---|---|
| `src/Modules/Geography/Modules.Geography.Domain/CityText.cs`, `CitySuggestions.cs`, `CitySynonym.cs`, `UnresolvedCity.cs` | Crear | Normalización (D-C1), similitud (D-C10), agregados |
| `src/Modules/Geography/Modules.Geography.Infrastructure/Persistence/NameMatching.cs` | Modificar | Delega en `CityText.Normalize` |
| `…/Persistence/GeographyDbContext.cs`, `…/Migrations/*_AddCitySynonyms.cs` | Modificar / Crear (ef) | Tres tablas, proyecciones de `audit.entries` y `platform.outbox_messages` |
| `…/Persistence/GeographyUnitOfWork.cs`, `GeographyAuditRecorder.cs`, `GeographyOutboxMessage.cs`, `GeographyEventPublisher.cs`, `CitySynonymRepository.cs`, `UnresolvedCityRepository.cs`, `CityRepository.cs` | Crear / Modificar | Persistencia, candado por texto, reporte por SQL, match por sinónimo |
| `src/Modules/Geography/Modules.Geography.Application/*.cs` | Crear / Modificar | Puertos, reporter, permisos, autorización, DTOs, consultas, comandos, calculadoras de impacto |
| `src/Modules/Geography/Modules.Geography.Api/CityOperatorEndpoints.cs`, `src/Api/Program.cs:181` | Crear / Modificar | Rutas de operador |
| `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs`, `…/Application/IExternalOrderWriter.cs` | Modificar | Poner la parte de envío y corregir su ciudad en un pedido externo |
| `src/Modules/Customers/Modules.Customers.Domain/Customer.cs`, `…/Application/CustomerProvisioning.cs`, `CustomerAuditActions.cs` | Modificar | Completar y corregir la ciudad de un cliente de venta externa |
| `src/Modules/Shopify/Modules.Shopify.Domain/ShopifyCityCorrection.cs`, `ShopifyCityCorrectionJob.cs` | Crear | Huella y trabajo |
| `src/Modules/Shopify/Modules.Shopify.Application/ShopifyPorts.cs`, `ShopifyCustomerResolver.cs`, `ShopifyOrderImporter.cs`, `ShopifyRepositories.cs`, `ShopifyAuditActions.cs` | Modificar | Lookup con sinónimo, reporte, huella de importación, puertos nuevos |
| `…/Application/ShopifyCityCorrections.cs`, `ShopifyUnresolvedCityBackfill.cs` | Crear | Siembra, procesador, recorrido |
| `…/Infrastructure/Persistence/ShopifyDbContext.cs`, `ShopifyUnitOfWork.cs`, `ShopifyCityCorrectionRepositories.cs`, `ShopifyInbox.cs`, migraciones | Modificar / Crear | Tablas, inbox, cola |
| `…/Infrastructure/Messaging/*`, `…/Workers/CitySynonymEventsWorker.cs`, `ShopifyCityCorrectionWorker.cs`, `ShopifyInfrastructureExtensions.cs` | Crear / Modificar | Consumidor y worker |
| `src/Bootstrapper/ShopifyGeographyLookup.cs`, `ShopifyCustomerDirectory.cs`, `ShopifyOrderWriter.cs` | Modificar | Sinónimo, `Created`/`Version`, parte persistida |
| `src/Bootstrapper/ShopifyUnresolvedCityReporter.cs`, `ShopifyCitySynonymLookup.cs`, `ShopifyCityCorrectionWriter.cs`, `CityCorrectionInsights.cs`, `CityCorrectionJobsAdapter.cs`, `HistoricalCityBackfillAdapter.cs` | Crear | Adaptadores |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` | Modificar | Permisos (`:1238`, `:1707`, `:2147`), handlers (`:449`), puertos (`:910`) |
| `tests/ArchitectureTests/ArchitectureTests/GeographyLayerTests.cs`, `CompositionRootTests.cs:179-189` | Modificar | Regla de módulos de Geography; 64 → 66 permisos |
| `README.md`, `CLAUDE.md` | Modificar | SC-14 |

**Comandos de prueba** (PowerShell; `$env:Seed__Enabled = "false"` en línea aparte antes de las de integración):

```powershell
dotnet test tests/Modules/Geography/Modules.Geography.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Geography/Modules.Geography.IntegrationTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Shopify/Modules.Shopify.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Shopify/Modules.Shopify.IntegrationTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~GeographyLayerTests|FullyQualifiedName~ShopifyLayerTests|FullyQualifiedName~CompositionRootTests"
```

---

### Task 1 (SC-01): Normalización compartida, similitud y agregados de Geography

**Files:**
- Create: `src/Modules/Geography/Modules.Geography.Domain/CityText.cs`, `CitySuggestions.cs`, `CitySynonym.cs`, `UnresolvedCity.cs`
- Modify: `src/Modules/Geography/Modules.Geography.Infrastructure/Persistence/NameMatching.cs:20-33` (el cuerpo pasa a `CityText.Normalize`)
- Test: `tests/Modules/Geography/Modules.Geography.UnitTests/CityTextTests.cs`, `CitySuggestionsTests.cs`, `CitySynonymTests.cs`,
  `UnresolvedCityTests.cs` (y `NameMatchingTests.cs` sigue verde)

**Interfaces:**
- Produces:
  ```csharp
  public static class CityText
  {
      public const int MaxLength = 120;                 // ancho de normalized_text, display_text y city_text
      /// D-C1: lo de NameMatching hoy (Trim, sin marcas diacríticas, mayúsculas invariantes) y además colapsa todo
      /// espacio interno (espacios, tabs, saltos) en uno. Sólo agrega coincidencias.
      public static string Normalize(string value);
  }

  public sealed record CitySuggestionCandidate(CityId CityId, string Name, string DivipolaName);
  public sealed record CitySuggestion(CityId CityId, int Similarity);
  public static class CitySuggestions
  {
      public const int MaxSuggestions = 3;
      public const int MinimumSimilarity = 50;          // inclusive
      /// Levenshtein normalizado sobre dos textos ya normalizados:
      /// round(100 * (max(len) - d) / max(len), AwayFromZero). Dos vacíos → 0.
      public static int Score(string normalizedText, string normalizedName);
      /// Por ciudad, el mayor de Score contra Normalize(Name) y Normalize(DivipolaName); deja los ≥ 50, ordena por
      /// similitud desc y luego Name ordinal, y toma 3.
      public static IReadOnlyList<CitySuggestion> Rank(string normalizedText, IEnumerable<CitySuggestionCandidate> cities);
  }

  public readonly record struct CitySynonymId(Guid Value) { public static CitySynonymId New(); }
  public sealed class CitySynonym
  {
      public CitySynonymId Id { get; } public DepartmentId DepartmentId { get; } public string NormalizedText { get; }
      public string DisplayText { get; } public CityId CityId { get; } public Guid CreatedBy { get; } public DateTimeOffset CreatedAt { get; }
      public Guid UpdatedBy { get; } public DateTimeOffset UpdatedAt { get; } public DateTimeOffset? DeletedAt { get; }
      public Guid? DeletedBy { get; } public long Version { get; } public bool IsDeleted => DeletedAt is not null;
      public static CitySynonym Create(CitySynonymId id, DepartmentId departmentId, string displayText, CityId cityId, Guid createdBy, DateTimeOffset now);
      public CityId ChangeCity(CityId cityId, Guid by, DateTimeOffset now);   // devuelve la anterior
      public void Delete(Guid by, DateTimeOffset now);
  }

  public readonly record struct UnresolvedCityId(Guid Value) { public static UnresolvedCityId New(); }
  public enum UnresolvedCityStatus { Pending, Resolved, Discarded }
  public sealed class UnresolvedCity
  {
      // Id, DepartmentId, NormalizedText, CityText, SampleAddress, ProvinceCode?, Status, SynonymId?, FirstSeenAt, LastSeenAt, Version
      public static UnresolvedCity Create(UnresolvedCityId id, DepartmentId departmentId, string cityText, string sampleAddress, string? provinceCode, DateTimeOffset seenAt);
      public void Resolve(CitySynonymId synonymId, DateTimeOffset now);
      public void Discard(DateTimeOffset now);
      public void Restore(DateTimeOffset now);
  }
  /// Sólo lectura desde EF: la escribe el SQL de SC-03.
  public sealed class UnresolvedCityOccurrence
  {   // Id, UnresolvedCityId, TenantId, Source, SourceRef, OrderId, CustomerId?, PartyCityOnly, CustomerCityOnly, SeenAt
  }
  ```
- Reglas (todas `GeographyDomainException`, 422): texto normalizado vacío → `geography.synonym.text_required`; más de 120 →
  `geography.synonym.text_too_long`; `ChangeCity` a la misma → `geography.synonym.same_city`; `ChangeCity` o `Delete` sobre uno
  borrado → `geography.synonym.deleted`; `Resolve`/`Discard` fuera de `Pending` → `geography.unresolved.not_pending`; `Restore`
  fuera de `Discarded` → `geography.unresolved.not_discarded`. Toda transición sube `Version` y pone `UpdatedAt`.
  `UnresolvedCity.Create` recorta `cityText` a 120 y `sampleAddress` a 600, sin lanzar: el pedido ya entró.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// CityTextTests
[Theory]
[InlineData("  Santiago   de  Cali ", "SANTIAGO DE CALI")]
[InlineData("Bogotá", "BOGOTA")]
[InlineData("medellín", "MEDELLIN")]
[InlineData("Cali\t\nValle", "CALI VALLE")]
public void NormalizeStripsAccentsUppercasesAndCollapsesSpaces(string raw, string expected)

// CitySuggestionsTests
[Fact] public void TheScoreOrdersCaliOverCalimaOverCandelaria()
// Score("CALLI","CALI") == 80; Score("CALLI","CALIMA") == 50; Score("CALLI","CANDELARIA") == 40
[Fact] public void RankKeepsTheFloorInclusiveAndDropsWhatIsBelow()
// candidatos CALI/"SANTIAGO DE CALI", CALIMA/"CALIMA", CANDELARIA, DAGUA → [(CALI, 80), (CALIMA, 50)]
[Fact] public void TheBestOfBothNamesWins()            // "SANTIAGO DE CALLI" vs CALI/"SANTIAGO DE CALI" → 94
[Fact] public void MedelinScores88()                   // Score("MEDELIN","MEDELLIN") == 88
[Fact] public void NoCityPassesTheFloorGivesEmpty()    // "VEREDA EL PLACER" contra Valle de juguete → []
[Fact] public void AtMostThreeTiesOrderedByName()

// CitySynonymTests
[Fact] public void CreateNormalizesAndKeepsTheDisplayText()       // "  Calli " → NormalizedText "CALLI", DisplayText "Calli", Version 1
[Fact] public void ChangeCityReturnsThePreviousAndBumpsVersion()
[Fact] public void ChangeCityToTheSameIs422SameCity()
[Fact] public void ADeletedSynonymRejectsChangeAndDelete()       // geography.synonym.deleted

// UnresolvedCityTests
[Fact] public void ResolveDiscardRestoreFollowTheirStates()
[Fact] public void ResolveOutsidePendingIs422NotPending()
[Fact] public void RestoreOutsideDiscardedIs422NotDiscarded()
[Fact] public void CreateTruncatesWithoutThrowing()
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Geography/Modules.Geography.UnitTests --filter "FullyQualifiedName~CityTextTests|FullyQualifiedName~CitySuggestionsTests|FullyQualifiedName~CitySynonymTests|FullyQualifiedName~UnresolvedCityTests"`
Expected: FAIL de compilación (`CityText`, `CitySuggestions`, `CitySynonym`, `UnresolvedCity` no existen).

- [ ] **Step 3: Implementa** los cuatro archivos del dominio con esas firmas. `Score` es la distancia de edición clásica de dos filas
  sobre los `char` de los textos normalizados. `NameMatching.Normalize` queda como `=> CityText.Normalize(value)`.

- [ ] **Step 4: Corre y verifica que pasan** (las cuatro clases y `NameMatchingTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Geography tests/Modules/Geography/Modules.Geography.UnitTests
git commit -m "feat(geography): SC-01 normalización compartida, similitud y agregados de sinónimos"
```

---

### Task 2 (SC-02): Tablas de Geography, su unidad de trabajo y el match por sinónimo

**Files:**
- Modify: `src/Modules/Geography/Modules.Geography.Infrastructure/Persistence/GeographyDbContext.cs:13-17` (tres `Configure*`, la
  proyección del outbox y `AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false)`)
- Create (ef): `…/Persistence/Migrations/*_AddCitySynonyms.cs`
- Create: `…/Persistence/GeographyUnitOfWork.cs`, `GeographyAuditRecorder.cs`, `GeographyOutboxMessage.cs`, `CitySynonymRepository.cs`
- Create: `src/Modules/Geography/Modules.Geography.Application/IGeographyUnitOfWork.cs`, `IGeographyAuditRecorder.cs`, `ICitySynonymRepository.cs`
- Modify: `…/Application/ICityRepository.cs` (`MatchByNameAsync`), `…/Persistence/CityRepository.cs:43-54`,
  `…/GeographyInfrastructureExtensions.cs:27-29`, `Modules.Geography.Infrastructure.csproj` (+ `Modules.Audit.Domain`,
  `Modules.Audit.Infrastructure`) y los lock files
- Modify: `tests/Bootstrapper/Bootstrapper.UnitTests/GeographyTestDoubles.cs`, `ShopifyAdaptersTestDoubles.cs` (los dobles de
  `ICityRepository` implementan el método nuevo)
- Test: `tests/Modules/Geography/Modules.Geography.IntegrationTests/CityNameLookupTests.cs`, `GeographyPersistenceTests.cs` (nuevo);
  `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerStatusAndImportApiTests.cs`

**Interfaces:**
- Consumes: SC-01.
- Produces:
  ```csharp
  // ICityRepository.cs
  public sealed record CityNameMatch(City City, CitySynonymId? SynonymId);
  /// §4.1: Name, luego DivipolaName, luego un sinónimo activo del departamento con el texto normalizado igual. El sinónimo
  /// nunca le gana a una ciudad real. FindByNameAsync pasa a ser (await MatchByNameAsync(...))?.City.
  Task<CityNameMatch?> MatchByNameAsync(DepartmentId departmentId, string name, CancellationToken cancellationToken);

  public interface ICitySynonymRepository
  {
      Task<CitySynonym?> FindAsync(CitySynonymId id, CancellationToken cancellationToken);              // borrados incluidos
      Task<CitySynonym?> FindActiveAsync(DepartmentId departmentId, string normalizedText, CancellationToken cancellationToken);
      void Add(CitySynonym synonym);
  }

  public interface IGeographyTransaction : IAsyncDisposable { Task CommitAsync(CancellationToken cancellationToken); }
  public interface IGeographyUnitOfWork
  {
      /// DbUpdateConcurrencyException → RequestConcurrencyException("concurrency.conflict");
      /// IX_city_synonyms_department_text_active → ResourceConflictException("geography.synonym.already_exists").
      Task<int> SaveChangesAsync(CancellationToken cancellationToken);
      Task<IGeographyTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
      /// pg_advisory_xact_lock(hashtextextended(department_id || ':' || normalized_text, 0)). Exige transacción abierta:
      /// serializa reportar y confirmar el mismo texto (SC-03, SC-11).
      Task LockCityTextAsync(DepartmentId departmentId, string normalizedText, CancellationToken cancellationToken);
  }

  /// Auditoría atómica propia (ADR 0019, como IShopifyAuditRecorder): el IAuditRecorder compartido está ligado a TenancyDbContext.
  public interface IGeographyAuditRecorder
  {
      void Record(Guid tenantId, Guid actorId, AuditActorType actorType, string action, string resourceType, string resourceId,
          string changedFieldsJson, DateTimeOffset occurredAt);   // source "geography", outcome "success"
  }
  ```
- Esquema `geography` (todo sin `tenant_id` salvo las ocurrencias):
  - `city_synonyms`: `id`, `department_id` (FK `departments`, Restrict), `normalized_text` varchar(120), `display_text` varchar(120),
    `city_id` (FK `cities`, Restrict), `created_by`, `created_at`, `updated_by`, `updated_at`, `deleted_at` null, `deleted_by` null,
    `version` (token). Único `IX_city_synonyms_department_text_active` (`department_id`, `normalized_text`) `WHERE deleted_at IS NULL`;
    índice `IX_city_synonyms_city`.
  - `unresolved_cities`: `id`, `department_id` (FK), `normalized_text` varchar(120), `city_text` varchar(120), `sample_address`
    varchar(600), `province_code` varchar(10) null, `status` varchar(16) con `CHECK` de los tres nombres, `synonym_id` null (FK
    `city_synonyms`, Restrict), `first_seen_at`, `last_seen_at`, `version` (token). Único `IX_unresolved_cities_department_text_open`
    (`department_id`, `normalized_text`) `WHERE status <> 'Resolved'`; índice `IX_unresolved_cities_status_last_seen` (`status`, `last_seen_at` DESC).
  - `unresolved_city_occurrences`: `id`, `unresolved_city_id` (FK, Restrict), `tenant_id`, `source` varchar(32), `source_ref` uuid
    (id de `shopify.orders`), `order_id`, `customer_id` null, `party_city_only` bool, `customer_city_only` bool, `seen_at`. Único
    `IX_unresolved_city_occurrences_source_ref` (`source`, `source_ref`); índice por `unresolved_city_id`.
  - `GeographyOutboxMessage`: copia de `QuotationsOutboxMessage` sobre `platform.outbox_messages`, `ExcludeFromMigrations`.

- [ ] **Step 1: Escribe las pruebas que fallan**

`CityNameLookupTests.cs` (mismo arranque que la prueba que ya existe):
```csharp
[Fact] public async Task MatchByNameFindsAnActiveSynonymAndSaysWhichOne()  // sinónimo "Calli" → Cali (Valle); "calli " → 76001, SynonymId == id
[Fact] public async Task ADeletedSynonymNoLongerMatches()
[Fact] public async Task ARealCityNameWinsOverASynonymWithTheSameText()  // Review Focus 1: fila por SQL "CALIMA" → Cali; "Calima" → 76126 y SynonymId null
[Fact] public async Task ASynonymOfAnotherDepartmentDoesNotMatch()
[Fact] public async Task InternalSpacesCollapse()                         // "Santiago   de  Cali" → 76001 (D-C1)
```
`GeographyPersistenceTests.cs`:
```csharp
[Fact] public async Task TwoActiveSynonymsForTheSameTextAre409()          // ResourceConflictException "geography.synonym.already_exists"
[Fact] public async Task ADeletedSynonymFreesTheText()
[Fact] public async Task AStaleSynonymVersionIs412()
[Fact] public async Task TheAuditCommitsWithTheChange()                  // fila en audit.entries con source "geography" en el mismo SaveChanges
```
`CustomerStatusAndImportApiTests.cs`:
```csharp
[Fact] public async Task ImportMatchesACitySynonym()  // D-C8: sinónimo "CALLI"→Cali por SQL; fila con ciudad "Calli", depto "Valle del Cauca" → sin errores, CityId de Cali
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Geography/Modules.Geography.IntegrationTests --filter "FullyQualifiedName~CityNameLookupTests|FullyQualifiedName~GeographyPersistenceTests"`
Expected: FAIL de compilación (`MatchByNameAsync`, `ICitySynonymRepository`, `IGeographyUnitOfWork` no existen).

- [ ] **Step 3: Implementa** el mapeo, la migración `AddCitySynonyms`, los repositorios, la unidad de trabajo, el auditor y el match.
  `MatchByNameAsync` compara los nombres en memoria como hoy y, si no casan, consulta `city_synonyms` por
  `department_id` + `normalized_text = CityText.Normalize(name)` + `deleted_at IS NULL`. Registra todo en
  `AddGeographyInfrastructure`. Actualiza los dobles de `Bootstrapper.UnitTests`.

- [ ] **Step 4: Corre y verifica que pasan** (las dos clases de Geography, `CustomerStatusAndImportApiTests` entera y
  `GeographyLookupTests|ShopifyAdaptersTests` en `Bootstrapper.UnitTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Geography tests/Modules/Geography tests/Bootstrapper tests/Modules/Customers/Modules.Customers.IntegrationTests
git commit -m "feat(geography): SC-02 tablas de sinónimos y bandeja, unidad de trabajo y match por sinónimo"
```

---

### Task 3 (SC-03): Reportar una ocurrencia

**Files:**
- Create: `src/Modules/Geography/Modules.Geography.Application/UnresolvedCityReporter.cs`, `IUnresolvedCityRepository.cs`
- Create: `src/Modules/Geography/Modules.Geography.Infrastructure/Persistence/UnresolvedCityRepository.cs`
- Modify: `…/GeographyInfrastructureExtensions.cs`
- Test: `tests/Modules/Geography/Modules.Geography.IntegrationTests/UnresolvedCityReporterTests.cs`

**Interfaces:**
- Consumes: `IGeographyUnitOfWork.LockCityTextAsync`, `ICitySynonymRepository.FindActiveAsync` (SC-02).
- Produces:
  ```csharp
  public sealed record UnresolvedCityReport(
      Guid TenantId, string Source, Guid SourceRef, Guid OrderId, Guid? CustomerId, DepartmentId DepartmentId,
      string CityText, string SampleAddress, string? ProvinceCode, bool PartyCityOnly, bool CustomerCityOnly, DateTimeOffset SeenAt);
  public enum UnresolvedCityReportOutcome { Recorded, AlreadyRecorded, SynonymExists }
  public sealed record UnresolvedCityReportResult(
      UnresolvedCityReportOutcome Outcome, UnresolvedCityId? UnresolvedCityId, CitySynonymId? SynonymId, CityId? CityId);
  /// Lo llama Bootstrapper por Shopify (SC-06) y por el recorrido (SC-13). Global, sin IExecutionContext.
  public interface IUnresolvedCityReporter
  {
      Task<UnresolvedCityReportResult> ReportAsync(UnresolvedCityReport report, CancellationToken cancellationToken);
  }

  public interface IUnresolvedCityRepository
  {
      /// Dentro de la transacción y con el candado del texto ya tomado: upsert de la fila abierta y alta idempotente de la ocurrencia.
      Task<(UnresolvedCityId Id, bool OccurrenceInserted)> UpsertWithOccurrenceAsync(
          UnresolvedCityReport report, string normalizedText, CancellationToken cancellationToken);
      // SC-10/SC-11 agregan las lecturas.
  }
  ```
- Orden de `ReportAsync`, en una transacción: normalizar (`CityText.Normalize`, recortado a 120; vacío → `ArgumentException`, que
  el importador nunca manda) → `LockCityTextAsync` → sinónimo activo → `SynonymExists` sin escribir → si no,
  `UpsertWithOccurrenceAsync` → commit → `Recorded` o `AlreadyRecorded`.
- El upsert es SQL: `INSERT … ON CONFLICT (department_id, normalized_text) WHERE status <> 'Resolved' DO UPDATE SET last_seen_at =
  GREATEST(...)`, y `sample_address`/`province_code` sólo si el `seen_at` nuevo es ≥ al guardado; luego
  `INSERT … ON CONFLICT (source, source_ref) DO NOTHING`. Un texto `Discarded` suma la ocurrencia y sigue `Discarded` (D-C9). Un
  texto cuyo sinónimo se borró abre una fila `Pending` nueva: la vieja `Resolved` conserva su `synonym_id` (§4.7).

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
[Fact] public async Task AFirstReportCreatesAPendingRowWithItsOccurrence()
[Fact] public async Task FiveOrdersOfTheSameTextAreOneRowWithFiveOccurrences()   // "Calli", "CALLI ", "calli"… → una fila; sample = el más nuevo
[Fact] public async Task TheSameMirrorRowReportedTwiceConcurrentlyLeavesOneOccurrence()  // Review Focus 2: Task.WhenAll → {Recorded, AlreadyRecorded}, 1 ocurrencia
[Fact] public async Task ADiscardedTextAccumulatesAndStaysDiscarded()
[Fact] public async Task AnActiveSynonymAnswersSynonymExistsWithoutWriting()       // SynonymId y CityId del sinónimo; 0 filas nuevas
[Fact] public async Task AfterDeletingTheSynonymTheTextOpensANewPendingRow()
[Fact] public async Task AnOlderSeenAtDoesNotMoveLastSeenBack()
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Geography/Modules.Geography.IntegrationTests --filter "FullyQualifiedName~UnresolvedCityReporterTests"`
Expected: FAIL de compilación (`IUnresolvedCityReporter` no existe).

- [ ] **Step 3: Implementa** `UnresolvedCityReporter : IUnresolvedCityReporter` (Application) y `UnresolvedCityRepository` (SQL por
  `Database.SqlQuery`, sin componer sobre el `INSERT … RETURNING`).

- [ ] **Step 4: Corre y verifica que pasan.**
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Geography tests/Modules/Geography
git commit -m "feat(geography): SC-03 reportar una ciudad sin resolver, idempotente por pedido"
```

---

### Task 4 (SC-04): Quotations: poner la parte de envío y corregir su ciudad en un pedido externo

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs` (dos métodos junto a `UpdateNotes`, `:955`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IExternalOrderWriter.cs:43-44` (`ExternalOrderResult.Shipping`),
  `:61-78` (interfaz) y `ExternalOrderService`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs`,
  `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExternalOrderServiceTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  // Order
  /// Spec 2026-10-26 §4.4. true si la agregó; false si ya estaba esa misma parte (reintento). Pending (order.order.not_pending),
  /// externo (order.order.source_invalid), sin recogida (order.order.store_pickup), sin parte previa distinta
  /// (order.order.shipping_party_present), parte completa (order.shipping.party_incomplete). Quita de Notes toda línea que
  /// empiece por notesLinePrefix (ordinal). UpdatedAt y Version++ sin recalcular totales.
  public bool AttachExternalShipping(QuotationPartyDetails shipping, string notesLinePrefix, DateTimeOffset occurredAt);
  /// §4.6. true si cambió; false si ya estaba en esa ciudad. Pending; Shipping.ToDetails() == expected o lanza
  /// order.order.shipping_party_changed. Sólo cambia DepartmentId y CityId.
  public bool CorrectExternalShippingCity(QuotationPartyDetails expected, Guid departmentId, Guid cityId, DateTimeOffset occurredAt);

  // IExternalOrderWriter.cs
  public sealed record ExternalShippingParty(string? Name, string? Phone, string? Email, string? Address, Guid? DepartmentId, Guid? CityId);
  public enum ExternalShippingOutcomeKind { Applied, AlreadyApplied, NotFound, NotPending, StorePickup, ShippingPresent, ShippingChanged, PartyIncomplete }
  /// Party es la parte como quedó guardada (normalizada): la huella de Shopify sale de acá, nunca del borrador.
  public sealed record ExternalShippingOutcome(ExternalShippingOutcomeKind Kind, ExternalShippingParty? Party, OrderStatus? Status);
  public sealed record ExternalOrderResult(..., bool AlreadyExisted = false, ExternalShippingParty? Shipping = null);
  Task<ExternalShippingOutcome> AttachShippingAsync(Guid tenantId, Guid orderId, ExternalShippingParty shipping, string notesLinePrefix, CancellationToken cancellationToken);
  Task<ExternalShippingOutcome> CorrectShippingCityAsync(Guid tenantId, Guid orderId, ExternalShippingParty expected, Guid departmentId, Guid cityId, CancellationToken cancellationToken);
  ```
- `ExternalOrderService`: carga por `tenantId` (sin pedido → `NotFound`); traduce cada código del dominio a su `Kind` (el `Status`
  viaja con `NotPending`); `AlreadyApplied` cuando el método devuelve `false`; guarda; la concurrencia (`concurrency.conflict`) se
  propaga: el trabajo la reintenta y vuelve a evaluar (§8). Sin auditoría acá (desvío S9). `CreateAsync` y `Existing` llenan
  `Shipping` desde `order.Shipping`.

- [ ] **Step 1: Escribe las pruebas que fallan**

`OrderTests.cs`:
```csharp
[Fact] public void AttachExternalShippingAddsThePartyAndDropsTheRawAddressLine()
// notes "Shopify #1001\nNota\nDirección de envío de Shopify: Cra 5, CALLI" → "Shopify #1001\nNota"; Shipping != null; Version + 1
[Fact] public void AttachExternalShippingWithTheSamePartyIsANoOp()          // false, Version igual
[Fact] public void AttachExternalShippingOverAnotherPartyIsShippingPartyPresent()
[Fact] public void AttachExternalShippingOnANonPendingOrderIsNotPending()   // Review Focus 3: Approved → order.order.not_pending
[Fact] public void AttachExternalShippingNeedsACompleteParty()              // sin correo → order.shipping.party_incomplete
[Fact] public void CorrectExternalShippingCityChangesOnlyCityAndDepartment()
[Fact] public void CorrectExternalShippingCityWithAnEditedPartyIsShippingPartyChanged()  // Review Focus 4
[Fact] public void CorrectExternalShippingCityAlreadyAtTheTargetIsANoOp()
```
`ExternalOrderServiceTests.cs`:
```csharp
[Fact] public async Task AttachShippingPersistsThePartyAndTheNotes()
[Fact] public async Task AttachShippingOnAnApprovedOrderIsNotPending()      // Kind NotPending, Status Approved, nada cambia
[Fact] public async Task CorrectShippingCityRespectsAPartyEditedBySql()     // Review Focus 4: UPDATE quotations.order_parties SET phone → ShippingChanged
[Fact] public async Task CreateAsyncReturnsTheShippingPartyAsPersisted()
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderTests"`
Expected: FAIL de compilación (`AttachExternalShipping` no existe).

- [ ] **Step 3: Implementa** los dos métodos de `Order`, los tipos y los dos métodos de `ExternalOrderService`. Agrega los dos
  métodos nuevos a todo doble de `IExternalOrderWriter` (`ShopifyAdaptersTestDoubles.cs:101`).

- [ ] **Step 4: Corre y verifica que pasan** (`OrderTests`, `ExternalOrderServiceTests` y `ShopifyAdaptersTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Quotations tests/Modules/Quotations tests/Bootstrapper
git commit -m "feat(quotations): SC-04 parte de envío y ciudad corregible en pedidos externos"
```

---

### Task 5 (SC-05): Customers: completar y corregir la ciudad de un cliente de venta externa

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Domain/Customer.cs` (`CorrectCity` junto a `Complete`, `:415-460`)
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerProvisioning.cs:12` (`ProvisionedCustomer.Version`),
  `:37-70` (interfaz), `:90-224` (implementación); `CustomerAuditActions.cs` (`CityCorrected`)
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerTests.cs`, `CustomerProvisioningTests.cs`;
  `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerProvisioningIntegrationTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record ProvisionedCustomer(Guid Id, bool IsComplete, bool IsActive, bool Created, long Version = 0); // CreateComplete/CreateIncomplete la llenan
  public const string CityCorrected = "customers.customer.city_corrected";   // CustomerAuditActions

  // Customer
  /// §4.6 (D-C4b): no fusionado, activo, completo; CityId == previousCityId o lanza customers.customer.city_changed. Cambia CityId y
  /// la ciudad de la dirección principal si sigue en previousCityId. El CUC no cambia. Touch.
  public void CorrectCity(Guid previousCityId, Guid cityId, DateTimeOffset occurredAt);

  public enum ExternalCustomerCityOutcomeKind { Applied, AlreadyApplied, NotFound, NotEligible, NotCompletable }
  public sealed record ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind Kind, long? Version, string? Reason);
  // ICustomerProvisioning
  Task<ExternalCustomerCityOutcome> CompleteExternalSaleAsync(Guid tenantId, Guid customerId, long expectedVersion, CompleteCustomerData data, CancellationToken cancellationToken);
  Task<ExternalCustomerCityOutcome> CorrectCityAsync(Guid tenantId, Guid customerId, long expectedVersion, Guid previousCityId, Guid cityId, CancellationToken cancellationToken);
  ```
- `CompleteExternalSaleAsync`, en orden: sin cliente → `NotFound`; completo con `CityId == data.CityId` y `Version == expected + 1`
  → `AlreadyApplied`; fusionado, inactivo, completo o con otra versión → `NotEligible` con `merged` | `inactive` | `complete` |
  `edited`; el validador de `CreateCustomerCommand` falla → `NotCompletable("validation.failed")`; clasificación o ciudad que no
  existen, o el documento ya tomado (`customers.customer.identification_taken` al guardar) → `NotCompletable(<código>)`. Si no, el
  mismo camino que `CreateCompleteAsync` (clasificación y ciudad antes del CUC, `CucFormatter.Build`) y `customer.Complete(...)` con
  la dirección principal; audita `PublishByIntegration(Completed)`; `Applied(customer.Version)`.
- `CorrectCityAsync`: `NotFound`; `CityId == cityId` y `Version == expected + 1` → `AlreadyApplied`; fusionado, inactivo,
  incompleto u otra versión → `NotEligible`; si no, `CorrectCity`, audita `CityCorrected`, `Applied`.

- [ ] **Step 1: Escribe las pruebas que fallan**

`CustomerTests.cs`:
```csharp
[Fact] public void CorrectCityMovesTheCustomerAndItsPrincipalAddress()
[Fact] public void CorrectCityKeepsTheCuc()
[Fact] public void CorrectCityWithAnotherCurrentCityIsCityChanged()
```
`CustomerProvisioningTests.cs`:
```csharp
[Fact] public async Task CreateCompleteReportsVersionOne()
[Fact] public async Task CompleteExternalSaleIssuesTheCucWithTheDepartmentOfTheCity()   // prefijo + "76" + consecutivo
[Fact] public async Task CompleteExternalSaleTwiceIsAlreadyApplied()
[Fact] public async Task CompleteExternalSaleOnAnEditedCustomerIsNotEligible()          // Version 2 → NotEligible "edited"
[Fact] public async Task CompleteExternalSaleOfAMergedCustomerIsNotEligible()
[Fact] public async Task CompleteExternalSaleWithoutEmailIsNotCompletable()             // "validation.failed", sin CUC quemado
[Fact] public async Task CorrectCityTwiceIsAlreadyApplied()
```
`CustomerProvisioningIntegrationTests.cs`:
```csharp
[Fact] public async Task CompleteExternalSalePersistsCucAddressAndAudit()
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~CustomerTests|FullyQualifiedName~CustomerProvisioningTests"`
Expected: FAIL de compilación (`CorrectCity`, `CompleteExternalSaleAsync` no existen).

- [ ] **Step 3: Implementa** con esas firmas. Agrega los dos métodos a los dobles de `ICustomerProvisioning`
  (`FakeCustomerProvisioning`, `PosFakeProvisioning`).

- [ ] **Step 4: Corre y verifica que pasan** (las tres clases y las de Pos y Bootstrapper que usan los dobles:
  `FullyQualifiedName~Provisioning`).
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Customers tests/Modules/Customers tests/Modules/Pos tests/Bootstrapper
git commit -m "feat(customers): SC-05 completar y corregir la ciudad de un cliente de venta externa"
```

---

### Task 6 (SC-06): Shopify: el match dice por dónde casó, la importación reporta y deja huella

**Files:**
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyPorts.cs:114` (`ShopifyCustomerRef`), `:192-199` (ciudad y
  lookup), `:269-307` (`ShopifyOrderWriteResult.ShippingParty`), y los tipos nuevos
- Modify: `…/ShopifyCustomerResolver.cs:98-185`, `…/ShopifyOrderImporter.cs:150-210` y `:288-306`, `…/ShopifyRepositories.cs`
- Create: `src/Modules/Shopify/Modules.Shopify.Domain/ShopifyCityCorrection.cs`
- Modify: `src/Modules/Shopify/Modules.Shopify.Infrastructure/Persistence/ShopifyDbContext.cs`, `ShopifyUnitOfWork.cs`;
  Create: `ShopifyCityCorrectionRepositories.cs`, migración `AddCityCorrections`
- Modify: `src/Bootstrapper/ShopifyGeographyLookup.cs:10-25`, `ShopifyCustomerDirectory.cs:79-108`, `ShopifyOrderWriter.cs:29-50`;
  Create: `src/Bootstrapper/ShopifyUnresolvedCityReporter.cs`; registro en `QepServiceCollectionExtensions.cs:910`
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifyCustomerResolverTests.cs`, `ShopifyImportResultTests.cs`,
  `ShopifyImporterTestDoubles.cs`; `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifyImportFlowTests.cs`,
  `ShopifyPersistenceTests.cs`; `tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyAdaptersTests.cs`

**Interfaces:**
- Consumes: `ICityRepository.MatchByNameAsync` (SC-02), `IUnresolvedCityReporter` (SC-03), `ExternalOrderResult.Shipping` (SC-04),
  `ProvisionedCustomer.Created/Version` (SC-05).
- Produces:
  ```csharp
  // ShopifyPorts.cs
  public sealed record ShopifyCityRef(Guid DepartmentId, Guid CityId, Guid? SynonymId = null);   // D-C13
  public sealed record ShopifyCityLookup(Guid? DepartmentId, ShopifyCityRef? City);
  public interface IShopifyGeographyLookup
  {   // reemplaza FindCityAsync: DepartmentId viene aunque la ciudad no case
      Task<ShopifyCityLookup> LookupCityAsync(string departmentName, string cityName, CancellationToken cancellationToken);
  }
  public sealed record ShopifyCustomerRef(Guid Id, bool IsComplete, bool IsActive, bool Created = false, long Version = 0);
  public sealed record ShopifyShippingFingerprint(string? Name, string? Phone, string? Email, string? Address, Guid DepartmentId, Guid CityId);
  // ShopifyOrderWriteResult: propiedad ShopifyShippingFingerprint? ShippingParty y parámetro opcional en Written(...)
  public sealed record ShopifyUnresolvedCityReport(
      Guid TenantId, Guid MirrorOrderId, Guid OrderId, Guid? CustomerId, Guid DepartmentId, string CityText,
      string SampleAddress, string? ProvinceCode, bool PartyCityOnly, bool CustomerCityOnly, DateTimeOffset SeenAt);
  public enum ShopifyUnresolvedCityOutcome { Recorded, AlreadyRecorded, SynonymExists }
  public sealed record ShopifyUnresolvedCityResult(ShopifyUnresolvedCityOutcome Outcome, Guid? SynonymId);
  public interface IShopifyUnresolvedCityReporter { Task<ShopifyUnresolvedCityResult> ReportAsync(ShopifyUnresolvedCityReport report, CancellationToken cancellationToken); }

  // ShopifyCustomerResolver
  public sealed record ShopifyCityResolution(ShopifyCityRef? City, Guid? UnmatchedDepartmentId, string? CityText);
  public async Task<ShopifyCityResolution> ResolveCityAsync(ShopifyAddress? address, CancellationToken cancellationToken);
  public sealed record ShopifyCustomerCreation(ShopifyCustomerRef Customer, ShopifyCityRef? City, bool CityBlocked);
  public async Task<ShopifyCustomerCreation> CreateAsync(ShopifyStore store, ShopifyOrderSnapshot snapshot, CancellationToken cancellationToken);
  /// Las precondiciones de CompleteDataAsync antes de la ciudad: documento, clasificación por defecto, nombre, país, dirección.
  public static bool CompletableExceptCity(ShopifyStore store, ShopifyOrderSnapshot snapshot);
  /// Los datos del alta completa con una ciudad ya resuelta (D-C5). null si CompletableExceptCity es false.
  public static ShopifyCompleteCustomer? CompleteDataWithCity(ShopifyStore store, ShopifyOrderSnapshot snapshot, Guid cityId);

  // ShopifyOrderImporter
  /// Nombre, teléfono, dirección y correo de la parte de envío, con la regla de hoy; null si falta alguno (desvío S10).
  public static (string Name, string Phone, string Address, string Email)? ShippingPartyFields(ShopifyOrderSnapshot snapshot);

  // Domain
  public enum ShopifyCityCorrectionOrigin { Correction, Import }
  public enum ShopifyCityCorrectionTarget { Order, Customer }
  public sealed class ShopifyCityCorrection
  {   // Id, TenantId, SynonymId, Origin, Target, TargetId, MirrorOrderId?, DepartmentId, CityId, PreviousCityId?,
      // PartyName?, PartyPhone?, PartyEmail?, PartyAddress?, CustomerVersion?, AppliedAt, UpdatedAt
      public static ShopifyCityCorrection ForOrder(Guid tenantId, Guid synonymId, ShopifyCityCorrectionOrigin origin, Guid orderId, Guid mirrorOrderId, ShopifyShippingFingerprint party, DateTimeOffset now);
      public static ShopifyCityCorrection ForCustomer(Guid tenantId, Guid synonymId, ShopifyCityCorrectionOrigin origin, Guid customerId, Guid? mirrorOrderId, Guid departmentId, Guid cityId, long customerVersion, DateTimeOffset now);
      public ShopifyShippingFingerprint? Party { get; }
      public void Recorrected(ShopifyShippingFingerprint party, DateTimeOffset now);       // PreviousCityId = CityId; conserva Origin
      public void Recorrected(Guid cityId, long customerVersion, DateTimeOffset now);
  }
  // ShopifyRepositories.cs
  public interface IShopifyCityCorrectionRepository
  {
      Task<ShopifyCityCorrection?> FindAsync(Guid tenantId, Guid synonymId, ShopifyCityCorrectionTarget target, Guid targetId, CancellationToken cancellationToken);
      Task<ShopifyCityCorrection?> FindByIdAsync(Guid id, CancellationToken cancellationToken);
      Task<IReadOnlyList<ShopifyCityCorrection>> ListBySynonymAsync(Guid synonymId, CancellationToken cancellationToken);   // SC-07
      void Add(ShopifyCityCorrection correction);
  }
  ```
- Tabla `shopify.city_corrections` con esos campos (`origin`, `target` como texto con `CHECK`; `party_*` con los anchos de
  `QuotationPartyDetails`). Único `IX_city_corrections_synonym_target` (`synonym_id`, `target`, `target_id`), que
  `ShopifyUnitOfWork` traduce **por nombre** a `concurrency.conflict` (así `ApplyAsync` relee y reintenta); índice
  (`tenant_id`, `target`, `target_id`).
- `ResolveCityAsync`: misma regla que hoy (código ISO y luego `province`); `UnmatchedDepartmentId` es el primer departamento que
  resolvió cuando ninguna ciudad casó; fuera de `CO`, todo `null`.
- Importador, en este orden y antes de `MarkImported`: (a) al crear el cliente, si `Created`, completo y su ciudad vino por
  sinónimo, `ForCustomer(Import, version)` antes del `SaveChanges` de `AttachCustomer`; (b) tras un `CreateAsync` no rechazado, si la
  parte casó por sinónimo y `result.ShippingParty` vino, `ForOrder(Import)` si no existe ya; (c) si la ciudad de envío no casó y hay
  `UnmatchedDepartmentId` (D-C11) y el pedido no es recogida o el cliente quedó `CityBlocked`, `ReportAsync` con
  `CustomerId` = el cliente que **esta** importación creó incompleto (desvío S4), `PartyCityOnly = ShippingPartyFields(...) is not null`,
  `CustomerCityOnly = creation.CityBlocked`, `SampleAddress` = la línea cruda de `Notes`, `ProvinceCode` del snapshot. Si el reporte
  lanza, la importación lanza y la entrega se reintenta. `SynonymExists` lo atiende SC-07; acá queda sin efecto.
- `ShopifyUnresolvedCityReporter` (Bootstrapper): `Source = "shopify"`, `SourceRef = MirrorOrderId`.

- [ ] **Step 1: Escribe las pruebas que fallan**

`ShopifyCustomerResolverTests.cs`:
```csharp
[Fact] public async Task ResolveCityTellsTheDepartmentWhenTheCityDoesNotMatch()  // VAC + "CALLI" → City null, UnmatchedDepartmentId = Valle
[Fact] public async Task ResolveCityCarriesTheSynonymId()
[Fact] public async Task ACityBlockedCreationSaysSo()                             // documento, clasificación y dirección; ciudad sin casar → incompleto, CityBlocked
[Fact] public async Task AMissingDocumentIsNotCityBlocked()
```
`ShopifyImportResultTests.cs` (importador con dobles):
```csharp
[Fact] public async Task AnUnmatchedCityIsReportedWithTheOrderAndTheBornIncompleteCustomer()
[Fact] public async Task AnUnmatchedCityWithoutPhoneIsNotPartyCityOnly()
[Fact] public async Task WithoutDepartmentNothingIsReported()                     // D-C11
[Fact] public async Task AForeignAddressIsNeverReported()
[Fact] public async Task AShippingPartyMatchedBySynonymLeavesAnImportCorrection() // huella = result.ShippingParty
[Fact] public async Task ACustomerCompletedBySynonymLeavesAnImportCorrection()    // CustomerVersion 1
[Fact] public async Task ARetriedImportDoesNotDuplicateTheImportCorrection()      // Review Focus 2: segundo intento con AlreadyExisted → 1 fila
```
`ShopifyImportFlowTests.cs`:
```csharp
[Fact] public async Task AnOrderWithCalliLandsInTheGeographyBandeja()   // webhook → worker → geography.unresolved_cities con su ocurrencia
```
`ShopifyPersistenceTests.cs`:
```csharp
[Fact] public async Task ASecondCorrectionForTheSameTargetIsAConcurrencyConflict()
```
`ShopifyAdaptersTests.cs`:
```csharp
[Fact] public async Task GeographyLookupReportsTheSynonymAndTheUnmatchedDepartment()
[Fact] public async Task CustomerDirectoryReportsCreatedAndVersion()
[Fact] public async Task OrderWriterReturnsThePersistedShippingParty()
[Fact] public async Task UnresolvedCityReporterMapsToGeography()
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Shopify/Modules.Shopify.UnitTests --filter "FullyQualifiedName~ShopifyCustomerResolverTests|FullyQualifiedName~ShopifyImportResultTests"`
Expected: FAIL de compilación (`LookupCityAsync`, `ShopifyCityResolution`, `IShopifyUnresolvedCityReporter` no existen).

- [ ] **Step 3: Implementa** los tipos, el resolver, el importador, la entidad con su tabla (`AddCityCorrections`), la traducción del
  índice y los cuatro adaptadores. Actualiza `ShopifyImporterTestDoubles.cs` (`FakeGeographyLookup`, reporter de juguete).

- [ ] **Step 4: Corre y verifica que pasan** (las seis clases y `ShopifyLayerTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Shopify src/Bootstrapper tests/Modules/Shopify tests/Bootstrapper
git commit -m "feat(shopify): SC-06 la importación reporta ciudades sin resolver y deja huella del sinónimo"
```

---

### Task 7 (SC-07): Shopify: consumir los eventos de Geography y sembrar el trabajo

**Files:**
- Create: `src/Modules/Shopify/Modules.Shopify.Domain/ShopifyCityCorrectionJob.cs`
- Create: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyCityCorrections.cs` (hechos del evento, `ShopifyCitySynonymEventApplier`,
  motivos de salto); Modify: `ShopifyRepositories.cs`, `ShopifyOrderImporter.cs` (`SynonymExists`)
- Create: `src/Modules/Shopify/Modules.Shopify.Infrastructure/Persistence/ShopifyInbox.cs` (inbox, proyección del outbox, reclamo),
  `Messaging/ShopifyClaimedOutboxConsumer.cs` (copia de la de Pipeline), `Messaging/CitySynonymEventPayloads.cs`,
  `Messaging/CitySynonymEventsProcessor.cs`, `Workers/CitySynonymEventsWorker.cs`; migración `AddCityCorrectionJobs`
- Modify: `…/ShopifyDbContext.cs`, `ShopifyCityCorrectionRepositories.cs`, `ShopifyInfrastructureExtensions.cs`
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifyCitySynonymEventApplierTests.cs`, `CitySynonymEventPayloadsTests.cs`,
  `ShopifyImportResultTests.cs`; `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifyCityCorrectionFlowTests.cs` (nuevo)

**Interfaces:**
- Consumes: `ShopifyCityCorrection`, `IShopifyCityCorrectionRepository.ListBySynonymAsync` (SC-06); el contrato de eventos.
- Produces:
  ```csharp
  public enum ShopifyCityCorrectionJobKind { Resolve, Recorrect }
  public enum ShopifyCityCorrectionJobStatus { Pending, Applied, Skipped, Failed }
  public sealed class ShopifyCityCorrectionJob
  {   // Id, TenantId, SynonymId, Kind, Target, TargetId, MirrorOrderId?, CorrectionId?, OperatorSubjectId, SourceMessageId?,
      // Status, SkipReason?, Attempts, LeaseUntil?, LastError?, CreatedAt, ProcessedAt?
      public const int LastErrorMaxLength = 512;
      public static ShopifyCityCorrectionJob Resolve(Guid tenantId, Guid synonymId, ShopifyCityCorrectionTarget target, Guid targetId, Guid mirrorOrderId, Guid operatorSubjectId, Guid? sourceMessageId, DateTimeOffset now);
      public static ShopifyCityCorrectionJob Recorrect(ShopifyCityCorrection correction, Guid operatorSubjectId, Guid? sourceMessageId, DateTimeOffset now);
      public void MarkApplied(DateTimeOffset now);
      public void MarkSkipped(string reason, DateTimeOffset now);
  }
  public static class ShopifyCityCorrectionSkipReasons
  {   // synonym_deleted, mirror_missing, order_not_found, order_not_pending, store_pickup, shipping_present, shipping_changed,
      // party_incomplete, customer_not_found, customer_not_eligible, customer_not_completable
  }
  public sealed record CitySynonymOccurrenceFact(Guid OccurrenceId, Guid TenantId, string Source, Guid SourceRef, Guid OrderId, Guid? CustomerId, bool PartyCityOnly, bool CustomerCityOnly);
  public sealed record CitySynonymResolvedFact(Guid SynonymId, Guid DepartmentId, Guid CityId, string NormalizedText, Guid OperatorSubjectId, Guid OperatorTenantId, IReadOnlyList<CitySynonymOccurrenceFact> Occurrences);
  public sealed record CitySynonymCorrectedFact(Guid SynonymId, Guid DepartmentId, Guid PreviousCityId, Guid CityId, Guid OperatorSubjectId, Guid OperatorTenantId);
  /// Sólo agrega al tracker: el procesador guarda con el inbox en el mismo SaveChanges.
  public sealed class ShopifyCitySynonymEventApplier(IShopifyCityCorrectionJobRepository jobs, IShopifyCityCorrectionRepository corrections, IClock clock)
  {
      Task ApplyResolvedAsync(Guid messageId, CitySynonymResolvedFact fact, CancellationToken cancellationToken);
      Task ApplyCorrectedAsync(Guid messageId, CitySynonymCorrectedFact fact, CancellationToken cancellationToken);
  }
  public interface IShopifyCityCorrectionJobRepository
  {
      Task<ShopifyCityCorrectionJob?> FindAsync(Guid id, CancellationToken cancellationToken);
      void Add(ShopifyCityCorrectionJob job);
  }
  ```
- Siembra: `Resolved` → por ocurrencia con `Source == "shopify"`, un trabajo `Order` si `PartyCityOnly` y uno `Customer` si
  `CustomerId` y `CustomerCityOnly`. `Corrected` → un `Recorrect` por cada fila de `city_corrections` del sinónimo, de los dos
  orígenes (O8), cuya `CityId != fact.CityId`.
- Tablas: `shopify.city_correction_jobs` (los campos de arriba, `kind`/`target`/`status` con `CHECK`). Único
  `IX_city_correction_jobs_message_target` (`source_message_id`, `target`, `target_id`) `WHERE source_message_id IS NOT NULL`; índices
  (`status`, `lease_until`) y (`synonym_id`, `status`). `shopify.inbox_messages` (`consumer`, `message_id`) PK, `processed_at`,
  `claimed_until`, `attempts`: copia de `pipeline.inbox_messages`.
- Consumidor `shopify.city-synonym-events`, eventos `geography.city_synonym.resolved.v1` y `…corrected.v1`, cada 3 s, leases
  5 s / 30 s / 5 min, como `QuotationEventsProcessor`. Un payload ilegible se marca procesado sin efecto y se loguea en Warning.
- Importador: `SynonymExists` (el sinónimo se confirmó entre el lookup y el reporte) siembra `Resolve` directo, con
  `SourceMessageId = null` y `OperatorSubjectId = Guid.Empty`, con las mismas reglas de la siembra.

- [ ] **Step 1: Escribe las pruebas que fallan**

`ShopifyCitySynonymEventApplierTests.cs`:
```csharp
[Fact] public async Task AResolvedEventSeedsOneJobPerOrderAndCustomerThatCanBeFixed()
[Fact] public async Task AnOccurrenceWithoutPartyCityOnlySeedsNoOrderJob()
[Fact] public async Task ACorrectedEventSeedsARecorrectionPerCorrectionOfBothOrigins()   // O8: Correction e Import
[Fact] public async Task ACorrectedEventSkipsCorrectionsAlreadyAtTheNewCity()
```
`CitySynonymEventPayloadsTests.cs`:
```csharp
[Fact] public void TheResolvedGoldenPayloadReads()       // el literal de «Contrato de eventos»
[Fact] public void TheCorrectedGoldenPayloadReads()
[Fact] public void AMalformedPayloadIsRejected()
```
`ShopifyImportResultTests.cs`:
```csharp
[Fact] public async Task ASynonymConfirmedDuringTheImportSeedsTheJobDirectly()
```
`ShopifyCityCorrectionFlowTests.cs`:
```csharp
[Fact] public async Task AResolvedEventInTheOutboxSeedsJobsOnceEvenIfProcessedTwice()   // INSERT en platform.outbox_messages; dos pasadas → n trabajos, inbox procesado
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Shopify/Modules.Shopify.UnitTests --filter "FullyQualifiedName~ShopifyCitySynonymEventApplierTests|FullyQualifiedName~CitySynonymEventPayloadsTests"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementa** la entidad, la siembra, las tablas (`AddCityCorrectionJobs`), el consumidor y su worker; registra todo
  en `ShopifyInfrastructureExtensions` con una extensión `AddShopifyCityCorrections()` que Bootstrapper llama junto a
  `AddShopifyWebhookWorker()`.

- [ ] **Step 4: Corre y verifica que pasan** (las cuatro clases, `ShopifyLayerTests` y `CompositionRootTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Shopify src/Bootstrapper tests/Modules/Shopify
git commit -m "feat(shopify): SC-07 consumir los eventos de sinónimos y sembrar el trabajo de corrección"
```

---

### Task 8 (SC-08): Shopify: el worker que corrige

**Files:**
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyPorts.cs` (dos puertos), `ShopifyCityCorrections.cs`
  (`ShopifyCityCorrectionProcessor`), `ShopifyAuditActions.cs`, `ShopifyRepositories.cs` (cola)
- Create: `src/Modules/Shopify/Modules.Shopify.Infrastructure/Workers/ShopifyCityCorrectionWorker.cs`; Modify: `ShopifyCityCorrectionRepositories.cs`
- Create: `src/Bootstrapper/ShopifyCitySynonymLookup.cs`, `ShopifyCityCorrectionWriter.cs`; registro en `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifyCityCorrectionProcessorTests.cs`;
  `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifyCityCorrectionFlowTests.cs`; `tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyAdaptersTests.cs`

**Interfaces:**
- Consumes: SC-04, SC-05, SC-06, SC-07; `ICitySynonymRepository.FindAsync` (SC-02).
- Produces:
  ```csharp
  public sealed record ShopifyCitySynonymRef(Guid SynonymId, Guid DepartmentId, Guid CityId, bool IsDeleted);
  public interface IShopifyCitySynonymLookup { Task<ShopifyCitySynonymRef?> FindAsync(Guid synonymId, CancellationToken cancellationToken); }

  public enum ShopifyCityWriteKind { Applied, AlreadyApplied, NotFound, NotPending, StorePickup, ShippingPresent, ShippingChanged, PartyIncomplete, NotEligible, NotCompletable }
  public sealed record ShopifyCityWriteResult(ShopifyCityWriteKind Kind, ShopifyShippingFingerprint? Party, long? CustomerVersion, string? Detail);
  /// Adaptador en Bootstrapper sobre IExternalOrderWriter e ICustomerProvisioning, un scope propio por llamada (como ShopifyOrderWriter).
  public interface IShopifyCityCorrectionWriter
  {
      Task<ShopifyCityWriteResult> AttachShippingAsync(Guid tenantId, Guid orderId, ShopifyPartyDraft party, string notesLinePrefix, CancellationToken cancellationToken);
      Task<ShopifyCityWriteResult> CorrectShippingCityAsync(Guid tenantId, Guid orderId, ShopifyShippingFingerprint expected, Guid departmentId, Guid cityId, CancellationToken cancellationToken);
      Task<ShopifyCityWriteResult> CompleteCustomerAsync(Guid tenantId, Guid customerId, long expectedVersion, ShopifyCompleteCustomer data, CancellationToken cancellationToken);
      Task<ShopifyCityWriteResult> CorrectCustomerCityAsync(Guid tenantId, Guid customerId, long expectedVersion, Guid previousCityId, Guid cityId, CancellationToken cancellationToken);
  }

  public sealed class ShopifyCityCorrectionProcessor(/* lookup, writer, mirrors, stores, corrections, jobs, audit, unitOfWork, clock */)
  {
      public const int IncompleteCustomerVersion = 1;   // desvío S7
      public Task ProcessAsync(ShopifyCityCorrectionJob job, CancellationToken cancellationToken);
  }
  public interface IShopifyCityCorrectionJobQueue
  {
      Task<IReadOnlyList<Guid>> ListPendingAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);
      Task<int?> TryClaimAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken);   // intentos, o null
      Task MarkFailedAsync(Guid id, DateTimeOffset now, string lastError, CancellationToken cancellationToken);
  }
  // ShopifyAuditActions: OrderCityResolved "shopify.order.city_resolved", CustomerCityResolved "shopify.customer.city_resolved",
  // OrderCityCorrected "shopify.order.city_corrected", CustomerCityCorrected "shopify.customer.city_corrected"
  ```
- `ProcessAsync`:
  1. Sinónimo `null` o borrado → `Skipped(synonym_deleted)` y guarda.
  2. **La ciudad es la actual del sinónimo**, no la del evento: un `Resolve` que corre después de un `PUT` aplica la nueva.
  3. `Resolve/Order`: espejo por `MirrorOrderId` (sin fila → `mirror_missing`), `ReadPayload`, `ShippingPartyFields` (null →
     `party_incomplete`) y `AttachShippingAsync(…, ShopifyOrderImporter.RawAddressLabel)`. `Resolve/Customer`: espejo y tienda,
     `CompleteDataWithCity` (null → `customer_not_completable`) y `CompleteCustomerAsync(…, IncompleteCustomerVersion, …)`.
     `Recorrect/Order`: `CorrectShippingCityAsync` con la huella de la corrección. `Recorrect/Customer`: `CorrectCustomerCityAsync`
     con `CustomerVersion` y `CityId` de la corrección.
  4. `Applied` y `AlreadyApplied` → crea (`Correction`) o actualiza (`Recorrected`, conserva el origen) la fila de huella con lo que
     devolvió el escritor, audita en el tenant (`Guid.Empty`, `AuditActorType.System`, `changedFieldsJson` con `synonymId`,
     `operatorSubjectId`, `cityId`, `previousCityId`, `origin`, `jobId`), `MarkApplied`, guarda. Todo lo demás → `MarkSkipped` con su
     motivo, sin huella ni auditoría.
  5. Después de guardar un `Applied`, relee el sinónimo: si su ciudad ya no es la aplicada, siembra un `Recorrect` de esa huella y guarda.
- Worker: copia de `ShopifyWebhookDeliveryWorker` (lote 50, reclamo por `UPDATE` que se commitea solo, un scope por trabajo).
  `MaxAttempts = 5`, leases 10 s / 1 min / 5 min / 15 min / 30 min. Lo que lanza deja el reclamo vivo; en el intento 5 →
  `MarkFailedAsync` con `"{Tipo}: {Mensaje}"` recortado a 512.

- [ ] **Step 1: Escribe las pruebas que fallan**

`ShopifyCityCorrectionProcessorTests.cs`:
```csharp
[Fact] public async Task AResolveOrderJobAttachesTheShippingWithTheSynonymCityAndAudits()
[Fact] public async Task AResolveJobUsesTheSynonymsCurrentCity()                       // el sinónimo ya apunta a CALI → la parte nace en CALI
[Fact] public async Task AnOrderApprovedAfterThePreviewIsSkippedNotTouched()           // Review Focus 3: NotPending → Skipped order_not_pending, sin huella ni auditoría
[Fact] public async Task JobsOfADeletedSynonymAreSkipped()                             // Review Focus 5: el escritor nunca se llama
[Fact] public async Task ARecorrectionRespectsAShippingPartyEditedAfterTheCorrection() // Review Focus 4: ShippingChanged → Skipped shipping_changed, huella igual
[Fact] public async Task ARecorrectionUpdatesTheCorrectionRowAndKeepsItsOrigin()       // Import sigue Import; PreviousCityId = CALIMA
[Fact] public async Task AlreadyAppliedAfterACrashWritesTheCorrectionAndTheAuditOnce()
[Fact] public async Task ACustomerJobCompletesFromVersionOneAndRecordsTheNewVersion()
[Fact] public async Task ACityChangedWhileApplyingSeedsARecorrection()
```
`ShopifyCityCorrectionFlowTests.cs`:
```csharp
[Fact] public async Task TheWorkerCorrectsARealPendingOrder()          // pedido CALLI importado; trabajo sembrado; DrainAsync → parte en Cali, notas sin la línea cruda, auditoría en el tenant
[Fact] public async Task AJobThatKeepsFailingEndsFailedWithItsError()  // escritor de prueba que lanza (ConfigureTestServices); lease vencido por SQL entre pasadas → Failed al quinto
```
`ShopifyAdaptersTests.cs`:
```csharp
[Fact] public async Task CityCorrectionWriterMapsTheOrderOutcomes()
[Fact] public async Task CityCorrectionWriterMapsTheCustomerOutcomes()
[Fact] public async Task CitySynonymLookupSeesDeletedSynonyms()
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Shopify/Modules.Shopify.UnitTests --filter "FullyQualifiedName~ShopifyCityCorrectionProcessorTests"`
Expected: FAIL de compilación (`ShopifyCityCorrectionProcessor` no existe).

- [ ] **Step 3: Implementa** los puertos, el procesador, la cola, el worker y los dos adaptadores; regístralos (el worker por
  `AddShopifyCityCorrections()`).

- [ ] **Step 4: Corre y verifica que pasan** (las tres clases, `ShopifyLayerTests` y `CompositionRootTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Shopify src/Bootstrapper tests/Modules/Shopify tests/Bootstrapper
git commit -m "feat(shopify): SC-08 worker que corrige pedidos y clientes por sinónimo"
```

---

### Task 9 (SC-09): Permisos de operador, autorización y el lector entre esquemas (D-C3b)

**Files:**
- Modify: `src/Modules/Geography/Modules.Geography.Application/Modules.Geography.Application.csproj` (+ `Modules.Tenancy.Application`) y lock files
- Create: `…/Application/GeographyOperatorPermissions.cs`, `GeographyOperatorAuthorization.cs`, `ICityCorrectionInsights.cs`
- Create: `src/Bootstrapper/CityCorrectionInsights.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:1238-1240` (rol `admin`), `:1707-1729` (definiciones), `:2147-2154` (políticas)
- Modify: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs:179-189`, `GeographyLayerTests.cs`
- Test: `tests/Modules/Geography/Modules.Geography.UnitTests/GeographyOperatorAuthorizationTests.cs`;
  `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/CityCorrectionInsightsTests.cs` (nuevo: es el proyecto que sabe armar pedidos)

**Interfaces:**
- Produces:
  ```csharp
  public static class GeographyOperatorPermissions
  {
      public const string CitiesRead = "operator.cities.read";
      public const string CitiesManage = "operator.cities.manage";
  }
  internal static class GeographyOperatorAuthorization   // copia de OperatorAuthorization (Tenancy), que es internal
  {
      public static void EnsureAuthorized(IExecutionContext executionContext, TenantId tenantId, string permission, IOperatorTenant operatorTenant);
  }

  public sealed record TenantOrderKey(Guid TenantId, Guid OrderId);
  public sealed record TenantCustomerKey(Guid TenantId, Guid CustomerId);
  public sealed record OrderInsight(Guid TenantId, Guid OrderId, string OrderNumber, string Status, bool HasShippingParty, bool IsStorePickup);
  public sealed record CustomerInsight(Guid TenantId, Guid CustomerId, bool IsComplete, bool IsMerged, bool IsActive, long Version);
  public sealed record CorrectionInsight(Guid TenantId, string Target, Guid TargetId, Guid CityId, string? OrderNumber, string? OrderStatus, bool StillAsWritten);
  public sealed record SynonymCorrectionCounts(int Orders, int Customers, int PendingJobs, int FailedJobs);
  /// D-C3b: lee quotations, customers, shopify, tenancy e identity POR NOMBRE. Renombrar una columna de esas tablas compila y
  /// deja los endpoints de impacto en 500 (42703): lo amarra CityCorrectionInsightsTests contra el esquema migrado.
  public interface ICityCorrectionInsights
  {
      Task<IReadOnlyList<OrderInsight>> ReadOrdersAsync(IReadOnlyCollection<TenantOrderKey> keys, CancellationToken cancellationToken);
      Task<IReadOnlyList<CustomerInsight>> ReadCustomersAsync(IReadOnlyCollection<TenantCustomerKey> keys, CancellationToken cancellationToken);
      Task<IReadOnlyList<CorrectionInsight>> ReadCorrectionsAsync(CitySynonymId synonymId, CancellationToken cancellationToken);
      /// Una entrada por id pedido, también en cero.
      Task<IReadOnlyDictionary<Guid, SynonymCorrectionCounts>> CountCorrectionsAsync(IReadOnlyCollection<CitySynonymId> synonymIds, CancellationToken cancellationToken);
      Task<IReadOnlyDictionary<Guid, string>> ReadTenantNamesAsync(IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken);
      Task<IReadOnlyDictionary<Guid, string>> ReadUserEmailsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
  }
  ```
- `CityCorrectionInsights` corre sobre la conexión de `GeographyDbContext` con `SqlQueryRaw` y arreglos `unnest`; une por
  (`tenant_id`, id) siempre. `StillAsWritten`: pedido → la parte `Shipping` de `quotations.order_parties` coincide campo por campo
  (`IS NOT DISTINCT FROM`) con `party_*`, `department_id` y `city_id` de la huella; cliente → `customers.customers.version =
  customer_version`. Los conteos salen de `shopify.city_corrections` (por `target`) y de `shopify.city_correction_jobs` (`Pending`,
  `Failed`). Verifica cada nombre de columna en el `DbContext` dueño antes de escribir el SQL.
- Definiciones: categoría `"Operator"`, `RequiredModules: []`; «Ver ciudades sin resolver» (`medium`) y «Gestionar sinónimos de
  ciudades» (`high`). Los dos en el rol `admin`; `OperatorPermissionFilter` los deja vivos sólo en el tenant operador.

- [ ] **Step 1: Escribe las pruebas que fallan**

`GeographyOperatorAuthorizationTests.cs`:
```csharp
[Fact] public void AnotherTenantInTheRouteIs403()
[Fact] public void WithoutThePermissionIs403()
[Fact] public void ANonOperatorTenantIs403EvenWithThePermission()
[Fact] public void TheOperatorWithThePermissionPasses()
```
`CompositionRootTests.cs`:
```csharp
[Fact] public void PermissionDiscoveryFindsTheSixtySixConstants()     // renombra la de 64 y suma los dos de este spec en el comentario
[Fact] public void OnlyTheAdminRoleCarriesTheCityOperatorPermissions() // admin sí; advisor, billing, cashier no; RequiredModules vacío
```
`GeographyLayerTests.cs`:
```csharp
[Fact] public void GeographySeesNoBusinessModule()   // ningún ensamblado de Geography referencia un Modules.* que no sea Geography, Tenancy, Identity o Audit (copia de la de ShopifyLayerTests)
```
`CityCorrectionInsightsTests.cs`:
```csharp
[Fact] public async Task EveryQueryRunsAgainstTheMigratedSchema()  // los seis métodos con un id al azar → vacío, sin 42703
[Fact] public async Task AnOrderOfAnotherTenantIsNotRead()
[Fact] public async Task CountsComeBackInZeroForASynonymWithoutCorrections()
[Fact] public async Task AShippingPartyEditedBySqlIsNotStillAsWritten()
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~GeographyLayerTests|FullyQualifiedName~CompositionRootTests"`
Expected: FAIL (`PermissionDiscoveryFindsTheSixtySixConstants`: 64 ≠ 66 una vez existan las constantes; antes, compilación).

- [ ] **Step 3: Implementa** las constantes, la autorización, el puerto, el adaptador y los tres registros del permiso.

- [ ] **Step 4: Corre y verifica que pasan** (las cuatro clases).
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Geography src/Bootstrapper tests/ArchitectureTests tests/Modules/Geography tests/Modules/Shopify
git commit -m "feat(geography): SC-09 permisos de operador para ciudades y lector de impacto entre esquemas"
```

---

### Task 10 (SC-10): Lecturas de operador: resumen, bandeja y detalle con sugerencias

**Files:**
- Create: `src/Modules/Geography/Modules.Geography.Application/CityOperatorDtos.cs` (todos los DTO del contrato), `CityOperatorInput.cs`,
  `CityOperatorQueries.cs`; Modify: `IUnresolvedCityRepository.cs` y su implementación
- Create: `src/Modules/Geography/Modules.Geography.Api/CityOperatorEndpoints.cs`; Modify: `src/Api/Program.cs:181`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:449-452` (handlers y validadores)
- Modify: `tests/Modules/Geography/Modules.Geography.IntegrationTests/GeographyApiHarness.cs` (`operatorTenantId` en la factoría,
  `StubClient` con `X-Permissions`, como `OperatorConsoleApiTests`)
- Test: `tests/Modules/Geography/Modules.Geography.UnitTests/CityOperatorQueryHandlersTests.cs`,
  `tests/Modules/Geography/Modules.Geography.IntegrationTests/CityOperatorApiTests.cs`

**Interfaces:**
- Consumes: SC-01 (`CitySuggestions`), SC-03, SC-09.
- Produces:
  ```csharp
  public sealed record GetCitySummaryQuery(TenantId TenantId) : IQuery<CitySummaryDto>;
  public sealed record ListUnresolvedCitiesQuery(TenantId TenantId, string? Status, Guid? DepartmentId, string? Search, int Page, int PageSize) : IQuery<UnresolvedCityPageDto>;
  public sealed record GetUnresolvedCityQuery(TenantId TenantId, UnresolvedCityId Id) : IQuery<UnresolvedCityDetailDto>;
  // IUnresolvedCityRepository
  Task<UnresolvedCity?> FindAsync(UnresolvedCityId id, CancellationToken cancellationToken);
  Task<(IReadOnlyList<UnresolvedCityRow> Rows, int Total)> ListAsync(UnresolvedCityStatus status, DepartmentId? departmentId, string? normalizedSearch, int page, int pageSize, CancellationToken cancellationToken);
  Task<IReadOnlyList<UnresolvedCityOccurrence>> ListOccurrencesAsync(UnresolvedCityId id, CancellationToken cancellationToken);
  Task<CitySummaryCounts> CountAsync(CancellationToken cancellationToken);
  public sealed record UnresolvedCityRow(UnresolvedCity City, int OrderCount, int TenantCount);
  public sealed record CitySummaryCounts(int Pending, int Discarded, int Synonyms, int OrdersWaiting);
  // Api
  public static IEndpointRouteBuilder MapCityOperatorEndpoints(this IEndpointRouteBuilder endpoints);
  ```
- `UnresolvedCityOccurrence` es una entidad de sólo lectura mapeada en SC-02.
- Validación (`CityOperatorInput`: `MaxPageSize = 100`, `MaxSearchLength = 100`, `MaxPage = int.MaxValue / 100`): `status` sólo
  `Pending` o `Discarded` (ordinal); `Resolved` o texto desconocido → 422 `validation.failed`. La autorización va **antes** de validar.
- La lista trae las ciudades de los departamentos de la página con un solo `ListByDepartmentsAsync` y calcula `bestSuggestion` con
  `CitySuggestions.Rank(...)[0]`. El detalle trae hasta tres sugerencias y los tenants con su nombre (`ReadTenantNamesAsync`).

- [ ] **Step 1: Escribe las pruebas que fallan**

`CityOperatorQueryHandlersTests.cs`:
```csharp
[Fact] public async Task AuthorizationGoesBeforeValidation()     // tenant ajeno + page 0 → 403, no 422
[Fact] public async Task AResolvedStatusFilterIs422()
[Fact] public async Task TheBestSuggestionIsTheFirstRanked()     // CALLI → CALI 80
[Fact] public async Task WithoutSuggestionsBestIsNull()
```
`CityOperatorApiTests.cs`:
```csharp
[Fact] public async Task ANonOperatorTenantIs403EvenWithThePermission()
[Fact] public async Task EveryReadRouteAnswers403WithoutThePermission()        // sin política sería 500
[Fact] public async Task TheListIsOrderedByLastSeenAndCarriesTheBestSuggestion()
[Fact] public async Task TheDetailJsonMatchesTheContract()                     // nombres exactos de las propiedades del contrato
[Fact] public async Task TheSummaryCountsPendingDiscardedSynonymsAndWaitingOrders()
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Geography/Modules.Geography.UnitTests --filter "FullyQualifiedName~CityOperatorQueryHandlersTests"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementa** DTOs, entrada, handlers, lecturas del repositorio y endpoints (`Produces`/`ProducesProblem` de cada
  status del contrato); registra los tres handlers y `AddValidatorsFromAssemblyContaining<ListUnresolvedCitiesValidator>()`.

- [ ] **Step 4: Corre y verifica que pasan** (las dos clases y `CompositionRootTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Geography src/Api src/Bootstrapper tests/Modules/Geography
git commit -m "feat(geography): SC-10 resumen, bandeja y detalle de ciudades sin resolver para el operador"
```

---

### Task 11 (SC-11): Confirmar, descartar y restaurar; el impacto de confirmar

**Files:**
- Create: `src/Modules/Geography/Modules.Geography.Application/ResolveUnresolvedCity.cs`, `CityImpact.cs`, `GeographyAuditActions.cs`,
  `IGeographyEventPublisher.cs`, `CitySynonymMapping.cs` (de agregado a `CitySynonymDto`)
- Create: `src/Modules/Geography/Modules.Geography.Infrastructure/Persistence/GeographyEventPublisher.cs`
- Modify: `CityOperatorEndpoints.cs`, `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Geography/Modules.Geography.UnitTests/ResolveImpactCalculatorTests.cs`;
  `tests/Modules/Geography/Modules.Geography.IntegrationTests/CityOperatorApiTests.cs`, `GeographyEventPublisherTests.cs`

**Interfaces:**
- Consumes: SC-02, SC-03, SC-09, SC-10.
- Produces:
  ```csharp
  public sealed record PreviewResolveImpactQuery(TenantId TenantId, UnresolvedCityId Id, Guid CityId) : IQuery<ResolveImpactDto>;
  public sealed record ResolveUnresolvedCityCommand(TenantId TenantId, UnresolvedCityId Id, Guid CityId) : ICommand<CitySynonymDto>;
  public sealed record DiscardUnresolvedCityCommand(TenantId TenantId, UnresolvedCityId Id) : ICommand<UnresolvedCityListItemDto>;
  public sealed record RestoreUnresolvedCityCommand(TenantId TenantId, UnresolvedCityId Id) : ICommand<UnresolvedCityListItemDto>;
  /// Puro: el impacto de §4.4 desde las ocurrencias y lo que leyó ICityCorrectionInsights.
  public static class ResolveImpactCalculator
  {
      public static ResolveImpactDto Compute(CityRefDto city, IReadOnlyList<UnresolvedCityOccurrence> occurrences,
          IReadOnlyList<OrderInsight> orders, IReadOnlyList<CustomerInsight> customers, IReadOnlyDictionary<Guid, string> tenantNames);
  }
  public interface IGeographyEventPublisher
  {
      void PublishSynonymResolved(CitySynonym synonym, IReadOnlyList<UnresolvedCityOccurrence> occurrences, Guid operatorTenantId, DateTimeOffset occurredAt);
      void PublishSynonymCorrected(CitySynonym synonym, CityId previousCityId, Guid operatorTenantId, DateTimeOffset occurredAt);   // SC-12
  }
  public static class CitySynonymEventNames { Resolved = "geography.city_synonym.resolved.v1"; Corrected = "geography.city_synonym.corrected.v1"; }
  public static class GeographyAuditActions
  {   // "geography.city_synonym.created", "geography.city_synonym.city_changed", "geography.city_synonym.deleted",
      // "geography.city_synonym.corrections_retried", "geography.unresolved_city.discarded", "geography.unresolved_city.restored",
      // "geography.city_backfill.ran"
  }
  ```
- Validación previa compartida por impacto y confirmar: la fila existe (404) y está `Pending` (422 `geography.unresolved.not_pending`);
  `ICityRepository.FindAsync(cityId)` existe **y** es del departamento de la fila, o 422 `geography.city.not_in_department`; ninguna
  ciudad del departamento tiene `Name` ni `DivipolaName` con `CityText.Normalize` igual al texto, o 422 `geography.synonym.matches_city`
  (D-C2).
- Confirmar, en una transacción: `LockCityTextAsync` → relee la fila → la validación previa → sinónimo activo existente → 409
  `geography.synonym.already_exists` → `CitySynonym.Create(…, DisplayText = fila.CityText, createdBy = SubjectId)` →
  `fila.Resolve` → ocurrencias → `PublishSynonymResolved` → auditoría en el tenant operador (`Human`, `SubjectId`) → guarda →
  commit. El sinónimo casa desde el commit (§4.4). Descartar y restaurar: transición, auditoría, guarda (412 si otro la cambió).
- `GeographyEventPublisher` serializa exactamente el contrato de eventos (records con nombres en camelCase, como
  `QuotationLifecycleEventPublisher`), `CorrelationId` = id del sinónimo.

- [ ] **Step 1: Escribe las pruebas que fallan**

`ResolveImpactCalculatorTests.cs`:
```csharp
[Fact] public void PendingWithoutShippingAndPartyCityOnlyIsToFixByTenant()
[Fact] public void PendingWithoutShippingMissingDataIsMissingData()
[Fact] public void PendingWithShippingIsWithShipping()
[Fact] public void NotPendingIsListedWithTenantNumberAndStatus()
[Fact] public void CityOnlyIncompleteVersionOneIsToCompleteAndTheRestStayIncomplete()
[Fact] public void EveryGroupTravelsInZero()
```
`GeographyEventPublisherTests.cs`:
```csharp
[Fact] public async Task TheResolvedPayloadMatchesTheGoldenContract()   // el literal de «Contrato de eventos», con ids de la prueba
```
`CityOperatorApiTests.cs`:
```csharp
[Fact] public async Task ResolvingCreatesTheSynonymMarksTheRowAndPublishesItsOccurrences()
[Fact] public async Task ResolvingATextThatIsARealCityNameIs422()      // Review Focus 1: fila "CALIMA" sembrada por el reporter → 422 matches_city
[Fact] public async Task ACityOfAnotherDepartmentIs422()
[Fact] public async Task ResolvingTwiceIs422NotPending()
[Fact] public async Task TheResolvedTextMatchesRightAway()             // MatchByNameAsync tras el 200
[Fact] public async Task ResolveLeavesAuditInTheOperatorTenant()
[Fact] public async Task AReportRacingTheResolveNeverLeavesAPendingRow() // Task.WhenAll(resolve, report): la ocurrencia va en el evento o el reporte da SynonymExists
[Fact] public async Task TheImpactNeverWrites()
[Fact] public async Task DiscardAndRestoreMoveTheRowAndAudit()
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Geography/Modules.Geography.UnitTests --filter "FullyQualifiedName~ResolveImpactCalculatorTests"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementa** comandos, query, calculadora, publicador, auditoría y rutas; registra los cuatro handlers.

- [ ] **Step 4: Corre y verifica que pasan** (las tres clases y `CompositionRootTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Geography src/Bootstrapper tests/Modules/Geography
git commit -m "feat(geography): SC-11 confirmar, descartar y restaurar textos con su impacto"
```

---

### Task 12 (SC-12): Sinónimos: lista, corregir, borrar y reintentar; el impacto de corregir

**Files:**
- Create: `src/Modules/Geography/Modules.Geography.Application/CitySynonyms.cs`, `ICityCorrectionJobs.cs`; Modify: `CityImpact.cs`,
  `ICitySynonymRepository.cs` (lista) y su implementación, `CityOperatorEndpoints.cs`
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyRepositories.cs` (`IShopifyCityCorrectionJobs`) y su implementación
- Create: `src/Bootstrapper/CityCorrectionJobsAdapter.cs`; Modify: `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Geography/Modules.Geography.UnitTests/CorrectImpactCalculatorTests.cs`,
  `tests/Modules/Geography/Modules.Geography.IntegrationTests/CityOperatorApiTests.cs`

**Interfaces:**
- Consumes: SC-08 (tablas de trabajo), SC-09, SC-11.
- Produces:
  ```csharp
  public sealed record ListCitySynonymsQuery(TenantId TenantId, Guid? DepartmentId, string? Search, int Page, int PageSize) : IQuery<CitySynonymPageDto>;
  public sealed record PreviewSynonymCorrectionImpactQuery(TenantId TenantId, CitySynonymId Id, Guid CityId) : IQuery<CorrectImpactDto>;
  public sealed record CorrectCitySynonymCommand(TenantId TenantId, CitySynonymId Id, Guid CityId, long ExpectedVersion) : ICommand<CitySynonymDto>;
  public sealed record DeleteCitySynonymCommand(TenantId TenantId, CitySynonymId Id) : ICommand<bool>;
  public sealed record RetryCitySynonymCorrectionsCommand(TenantId TenantId, CitySynonymId Id) : ICommand<CitySynonymDto>;
  public static class CorrectImpactCalculator
  {
      public static CorrectImpactDto Compute(CityRefDto from, CityRefDto to, IReadOnlyList<CorrectionInsight> corrections,
          IReadOnlyList<UnresolvedCityOccurrence> occurrences, IReadOnlyDictionary<Guid, string> tenantNames);
  }
  public interface ICityCorrectionJobs { Task<int> RetryFailedAsync(CitySynonymId synonymId, CancellationToken cancellationToken); }
  // Shopify
  public interface IShopifyCityCorrectionJobs { Task<int> RetryFailedAsync(Guid synonymId, CancellationToken cancellationToken); } // Failed → Pending, attempts 0, sin lease ni last_error
  ```
- Corregir: sinónimo existente y no borrado (404) → `Version == ExpectedVersion` o 412 `concurrency.conflict` → ciudad del mismo
  departamento (422) → `ChangeCity` (422 `same_city`) → `PublishSynonymCorrected` → auditoría → guarda. El texto y el departamento no
  cambian (§4.6). Borrar: `Delete` (soft) → auditoría → 204; no publica nada y no deshace (O6); lo `Pending` lo salta el worker
  (SC-08). Reintentar: `ICityCorrectionJobs.RetryFailedAsync` → auditoría con la cantidad → `CitySynonymDto` fresco.
- `occurrencesUncorrected`: las ocurrencias de la fila `Resolved` del sinónimo cuyo pedido o cliente no tiene huella.

- [ ] **Step 1: Escribe las pruebas que fallan**

`CorrectImpactCalculatorTests.cs`:
```csharp
[Fact] public void APendingOrderStillAsWrittenChanges()
[Fact] public void APendingOrderEditedIsRespected()
[Fact] public void ANotPendingOrderIsListedWithItsStatus()
[Fact] public void ACustomerAtItsVersionChangesAndAnEditedOneIsCounted()
[Fact] public void OccurrencesWithoutCorrectionAreUncorrected()
[Fact] public void EveryGroupTravelsInZero()
```
`CityOperatorApiTests.cs`:
```csharp
[Fact] public async Task TheSynonymListCarriesCountsEmailAndVersion()   // conteos en cero sin trabajos; email de identity.users
[Fact] public async Task CorrectingWithAStaleVersionIs412()
[Fact] public async Task CorrectingToTheSameCityIs422SameCity()
[Fact] public async Task CorrectingPublishesTheCorrectedEventWithThePreviousCity()   // contra el literal de «Contrato de eventos»
[Fact] public async Task DeletingStopsMatchingAndTheNextReportOpensAPendingRow()     // O6, §4.7
[Fact] public async Task ADeletedSynonymIs404OnEveryRoute()
[Fact] public async Task RetryMovesFailedJobsBackToPending()                         // trabajo Failed sembrado por SQL
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Geography/Modules.Geography.UnitTests --filter "FullyQualifiedName~CorrectImpactCalculatorTests"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementa** query, comandos, calculadora, adaptador y rutas; registra los cinco handlers.

- [ ] **Step 4: Corre y verifica que pasan** (las dos clases, `CompositionRootTests`, `GeographyLayerTests`, `ShopifyLayerTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Geography src/Modules/Shopify src/Bootstrapper tests/Modules/Geography
git commit -m "feat(geography): SC-12 corregir, borrar y reintentar sinónimos con su impacto"
```

---

### Task 13 (SC-13): Recorrer la historia previa (D-C12)

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IExternalOrderWriter.cs` (`FindShippingStateAsync`)
- Create: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyUnresolvedCityBackfill.cs`; Modify: `ShopifyPorts.cs`
  (`IShopifyCityCorrectionWriter.ReadOrderCityStateAsync`), `ShopifyRepositories.cs` (`ListImportedAfterAsync`) y su implementación
- Create: `src/Modules/Geography/Modules.Geography.Application/RunCityBackfill.cs` (`IHistoricalCityBackfill` y el comando);
  `src/Bootstrapper/HistoricalCityBackfillAdapter.cs`; Modify: `ShopifyCityCorrectionWriter.cs`, `CityOperatorEndpoints.cs`,
  `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifyUnresolvedCityBackfillTests.cs`,
  `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifyCityBackfillTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  // Quotations
  public sealed record ExternalShippingState(OrderStatus Status, bool IsStorePickup, ExternalShippingParty? Shipping, string? Notes);
  Task<ExternalShippingState?> FindShippingStateAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);
  // Shopify
  public sealed record ShopifyOrderCityState(string Status, bool IsStorePickup, bool HasShippingParty, bool NotesHaveRawAddress);
  Task<ShopifyOrderCityState?> ReadOrderCityStateAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken); // IShopifyCityCorrectionWriter
  Task<IReadOnlyList<ShopifyOrderMirror>> ListImportedAfterAsync(Guid? afterId, int limit, CancellationToken cancellationToken); // todos los tenants, por id
  public sealed record ShopifyCityBackfillResult(int Scanned, int Reported, int AlreadyReported, int NowMatching, int Skipped);
  public sealed class ShopifyUnresolvedCityBackfill(/* mirrors, stores, resolver, writer, reporter, clock */)
  {
      public const int BatchSize = 200;
      public Task<ShopifyCityBackfillResult> RunAsync(CancellationToken cancellationToken);
  }
  // Geography
  public interface IHistoricalCityBackfill { Task<CityBackfillResultDto> RunAsync(CancellationToken cancellationToken); }
  public sealed record RunCityBackfillCommand(TenantId TenantId) : ICommand<CityBackfillResultDto>;
  ```
- Por fila `Imported` con `QepOrderId`: el pedido está `Pending`, sin parte de envío, no es recogida y sus notas tienen una línea que
  empieza por `RawAddressLabel` (si no → `Skipped`). Resuelve la ciudad del snapshot: si ahora casa (por D-C1 o por un sinónimo) →
  `NowMatching`, sin reportar; si no hay departamento → `Skipped`; si no, `ReportAsync` con `CustomerId = QepCustomerId` sólo si el
  cliente sigue incompleto, `CustomerCityOnly = CompletableExceptCity(...)` y `PartyCityOnly = ShippingPartyFields(...) is not null`.
  `AlreadyRecorded` → `AlreadyReported`. Idempotente: correrlo dos veces no reporta nada nuevo. Síncrono: al desplegar son pocos
  cientos de filas. La auditoría (tenant operador) lleva los conteos.

- [ ] **Step 1: Escribe las pruebas que fallan**

`ShopifyUnresolvedCityBackfillTests.cs`:
```csharp
[Fact] public async Task APendingOrderWithoutShippingAndTheRawLineIsReported()
[Fact] public async Task AnApprovedOrderIsSkipped()
[Fact] public async Task AnOrderWithShippingIsSkipped()
[Fact] public async Task NotesWithoutTheRawLineAreSkipped()
[Fact] public async Task ACityThatNowMatchesIsCountedNotReported()
[Fact] public async Task RunningTwiceReportsNothingNew()
```
`ShopifyCityBackfillTests.cs`:
```csharp
[Fact] public async Task AHistoricalOrderLandsInTheBandejaOnceThroughTheOperatorEndpoint()  // dos POST /backfill → reported 1 y luego alreadyReported 1
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Shopify/Modules.Shopify.UnitTests --filter "FullyQualifiedName~ShopifyUnresolvedCityBackfillTests"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementa** con esas firmas; registra el comando, el adaptador y la ruta `POST /backfill`.

- [ ] **Step 4: Corre y verifica que pasan** (las dos clases, `ExternalOrderServiceTests`, `CompositionRootTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Quotations src/Modules/Shopify src/Modules/Geography src/Bootstrapper tests/Modules/Shopify
git commit -m "feat(shopify): SC-13 recorrido de pedidos ya importados con ciudad sin resolver"
```

---

### Task 14 (SC-14): De punta a punta, documentación y la suite completa

**Files:**
- Test: `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/CitySynonymEndToEndTests.cs`
- Modify: `README.md` (sección nueva «Sinónimos de ciudades»: endpoints, eventos, worker, recorrido, cómo reintentar), `CLAUDE.md`
  (gotchas: abajo). **No** `docs/estado.md`.

**Interfaces:**
- Consumes: todo lo anterior.

- [ ] **Step 1: Escribe las pruebas de punta a punta**

```csharp
[Fact] public async Task CalliFromTheWebhookToTheCorrectedOrderAndCustomer()
// pedido con "CALLI" → bandeja → POST resolve CALI → drenar eventos y trabajos → parte en Cali, notas limpias, cliente completo con
// CUC del 76, auditoría en el tenant; un pedido nuevo con "Calli" entra con parte de envío y fila origin=import
[Fact] public async Task CorrectingCalimaToCaliMovesThePendingAndListsTheApproved()
// sinónimo a CALIMA, dos pedidos corregidos, se aprueba uno; impacto lista el aprobado; PUT a CALI → el pendiente cambia, el aprobado no
[Fact] public async Task DeletingWhileJobsArePendingSkipsThem()   // Review Focus 5: resolve, DELETE antes de drenar → Skipped synonym_deleted, pedido sin parte
```

- [ ] **Step 2: Corre y verifica**

Run: `dotnet test tests/Modules/Shopify/Modules.Shopify.IntegrationTests --filter "FullyQualifiedName~CitySynonymEndToEndTests"`
Expected: PASS. Si algo falla, es una regresión de una tarea anterior: arréglala en esa tarea, con su RED.

- [ ] **Step 3: Documenta.** En `CLAUDE.md`, cuatro gotchas, cada uno con su prueba: (1) el sinónimo casa **después** de los dos
  nombres, y confirmar uno igual a una ciudad real es 422 (`ARealCityNameWinsOverASynonymWithTheSameText`); (2) `CityCorrectionInsights`
  lee `quotations`, `customers`, `shopify`, `tenancy` e `identity` por nombre (`CityCorrectionInsightsTests`); (3) el payload de
  `geography.city_synonym.*` es contrato con Shopify y vive literal en dos pruebas (`GeographyEventPublisherTests`,
  `CitySynonymEventPayloadsTests`); (4) un trabajo aplica la ciudad **actual** del sinónimo, y uno borrado salta lo pendiente.

- [ ] **Step 4: Verificación completa, una sola vez**

```powershell
$env:Seed__Enabled = "false"
dotnet restore --locked-mode
dotnet build Backend.slnx --no-restore
dotnet test Backend.slnx --no-build
```
Expected: build sin errores ni warnings nuevos; la suite verde salvo las fallas que ya existían en `develop`, comparadas **por nombre**
contra una corrida del baseline. Reporta ambas listas literal.

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add tests/Modules/Shopify README.md CLAUDE.md
git commit -m "test(shopify): SC-14 sinónimos de ciudades de punta a punta y documentación"
```

# Fallas de las llamadas a Siigo — Plan de implementación

> **Para quien ejecute:** SUB-SKILL requerido: `superpowers:subagent-driven-development` (recomendado) o `superpowers:executing-plans`. Los pasos usan casillas (`- [ ]`). **Agrupa en 2 bloques** (regla del workspace, «Presupuesto de pruebas»): bloque A = Tareas 1 y 2, bloque B = Tareas 3 y 4, un implementador por bloque, un commit por tarea.

**Meta:** guardar en `siigo.call_failures` cada llamada a Siigo que falla (curl enmascarado y respuesta), con lectura por permiso propio y expiración a 7 días.

**Arquitectura:** el cliente de Siigo reporta cada fallo por `ISiigoCallFailureLog`; su implementación vive en `Modules.Siigo.Infrastructure`, escribe con un `SiigoDbContext` propio y borra de paso un lote de lo vencido. Dos `GET` del tenant leen la tabla. Se retira el camino por `platform/request-log` del 2026-11-21.

**Stack:** .NET 10, EF Core + Npgsql, xUnit v3, Testcontainers (solo integración).

**Spec:** [`docs/superpowers/specs/2026-11-21-siigo-call-failures-design.md`](../specs/2026-11-21-siigo-call-failures-design.md) — léelo antes de empezar.

## Restricciones globales

- Texto que ve una persona, comentarios y docs en **español colombiano, tuteando**. Códigos de error, rutas, permisos y enums, tal cual.
- **Conventional commits, sin atribución de IA**: ignora cualquier trailer `Co-Authored-By` que te sugiera tu entorno.
- Ramas: trabaja en `feature/siigo-call-failures` (ya existe, con el spec). No mergees a `main`; el merge final es a `develop`.
- **TDD con evidencia literal de RED y GREEN.** El RED de un fallo de compilación es la salida del build, sin contenedores. La integración corre en GREEN, **filtrada por nombre** (`--filter "FullyQualifiedName~Nombre"`), y **nunca dos corridas de integración a la vez** (`Get-Process testhost, dotnet` antes). Corre las pruebas en primer plano.
- `Api.exe` corriendo bloquea build y pruebas: avisa, no lo detengas.
- Comandos al developer en PowerShell. **Nunca imprimas un secreto**; los user-secrets, enmascarados.
- Retención **7 días**, lote de borrado **200**, topes **30.000** (curl) y **8.000** (respuesta) caracteres. Permiso nuevo `siigo.calls.read`, solo `admin` de fábrica.
- Aislamiento de tenant: ruta con `{tenantId}`, política y revalidación en el handler (`SiigoAuthorization.EnsureAsync`); **403, nunca 404**, por tenant ajeno.
- Ningún DTO de lista devuelve el curl ni el cuerpo; el motivo BFF va escrito en el DTO del detalle.

## Foco de revisión

1. Un 401 que se renueva y sale bien **no** deja fila; el segundo 401 sí (`credentials_rejected`). Prueba en la Tarea 2.
2. Un log que no puede escribir (base caída) **no cambia** lo que el cliente devuelve ni lanza. Tarea 3.
3. Una lectura que lanza deja **exactamente una** fila (la excepción además llega al request-log, sin curl). Tarea 2.
4. La expiración borra solo lo anterior a 7 días, como máximo 200 por escritura, y deja lo reciente. Tarea 3.
5. Pedir el detalle de un id de otro tenant (con el tenant propio en la ruta) responde `404 siigo.call_failure.not_found`, y con el tenant de otro en la ruta, 403. Tarea 4.
6. Ni el token ni la `access_key` aparecen en `request_curl` ni `response_body`, ni siquiera tras un fallo de `/auth`. Tarea 4 (`SiigoNoLeakTests`).

## Mapa de archivos

| Archivo | Cambio |
| --- | --- |
| `src/Modules/Siigo/Modules.Siigo.Domain/SiigoCallOperation.cs` | crear — enum |
| `src/Modules/Siigo/Modules.Siigo.Domain/SiigoCallFailure.cs` | crear — entidad append-only |
| `src/Modules/Siigo/Modules.Siigo.Domain/SiigoErrorCodes.cs` | añadir `CallFailureNotFound` |
| `src/Modules/Siigo/Modules.Siigo.Infrastructure/Persistence/SiigoDbContext.cs` | `DbSet` + `ConfigureCallFailure` |
| `src/Modules/Siigo/Modules.Siigo.Infrastructure/Persistence/Migrations/*AddSiigoCallFailures*` | generar |
| `src/Modules/Siigo/Modules.Siigo.Application/ISiigoCallFailureLog.cs` | `SiigoCallFailure` (record) → `SiigoCallFailureReport` |
| `src/Modules/Siigo/Modules.Siigo.Infrastructure/Api/SiigoApiClient.cs`, `SiigoCurl.cs` | reportar todo fallo con su operación |
| `src/Modules/Siigo/Modules.Siigo.Infrastructure/Persistence/SiigoCallFailureLog.cs` | crear — escritura propia + expiración |
| `src/Modules/Siigo/Modules.Siigo.Infrastructure/Persistence/SiigoCallFailureRepository.cs` | crear — lectura |
| `src/Modules/Siigo/Modules.Siigo.Application/SiigoPorts.cs` | `ISiigoCallFailureRepository` |
| `src/Modules/Siigo/Modules.Siigo.Application/CallFailures/*.cs` | crear — queries, handlers, DTOs |
| `src/Modules/Siigo/Modules.Siigo.Api/SiigoEndpoints.cs` | grupo `call-failures` |
| `src/Modules/Siigo/Modules.Siigo.Application/SiigoPermissions.cs` | `CallsRead` |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` | permiso (admin, definición, política), handlers; quitar el adaptador viejo |
| **Eliminar** | `src/Bootstrapper/SiigoCallFailureLog.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/SiigoCallFailureLogTests.cs`; revertir `RequestFailureCapture.WithDiagnostic` en `src/Api/RequestFailureCapture.cs` |
| `.claude/rules/siigo.md` | reescribir la viñeta de las llamadas fallidas |

---

### Tarea 1: Tabla `siigo.call_failures`

**Archivos:** crear `SiigoCallOperation.cs`, `SiigoCallFailure.cs`; modificar `SiigoErrorCodes.cs`, `SiigoDbContext.cs`; generar la migración; probar en `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoCallFailureTests.cs` y `SiigoDbContextMappingTests.cs`.

**Interfaces — produce:**
- `public enum SiigoCallOperation { Auth, ListRead, ProductSearch, CustomerFind, CustomerCreate, InvoiceCreate, InvoiceStamp, StampErrors }`
- `public sealed class SiigoCallFailure` con `public static SiigoCallFailure Create(Guid tenantId, Guid connectionId, SiigoCallOperation operation, string method, string path, int? status, string reason, string? idempotencyKey, string? siigoInvoiceId, string requestCurl, string? responseBody, Guid? subjectId, string? traceId, DateTimeOffset occurredAt)`; propiedades `Id` (v7), `TenantId`, `ConnectionId`, `Operation`, `Method`, `Path`, `Status`, `Reason`, `IdempotencyKey`, `SiigoInvoiceId`, `RequestCurl`, `ResponseBody`, `SubjectId`, `TraceId`, `OccurredAt`; constantes `RequestCurlMaxLength = 30_000`, `ResponseBodyMaxLength = 8_000`, `PathMaxLength = 500`, `MethodMaxLength = 10`, `ReasonMaxLength = 60`, `TraceIdMaxLength = 120`. `Create` recorta cada texto a su tope (el curl y la respuesta, sin romper un par sustituto: `SnapshotText.Fit` de `BuildingBlocks.Domain`). `ToString()` devuelve `"SiigoCallFailure { }"` (lleva datos del cliente).
- `SiigoErrorCodes.CallFailureNotFound = "siigo.call_failure.not_found"`.

- [ ] **Paso 1: prueba RED** — `SiigoCallFailureTests`: `CreateTruncatesTheCurlAndTheResponseToTheirCaps` (30.001 y 8.001 caracteres → 30.000 y 8.000), `CreateKeepsAnEmptyResponseAsNull`, `ToStringDoesNotPrintTheCurl`. En `SiigoDbContextMappingTests` una prueba que verifica tabla `siigo.call_failures`, clave `PK_call_failures`, columnas `tenant_id`, `connection_id`, `operation` (texto, 40), `status`, `idempotency_key` (30), `siigo_invoice_id`, `request_curl` y `response_body` (`text`), `occurred_at`, e índices `IX_call_failures_tenant_occurred` y `IX_call_failures_occurred`. Sigue el estilo de las pruebas vecinas del archivo.
- [ ] **Paso 2: verifica el RED** — `dotnet build tests/Modules/Siigo/Modules.Siigo.UnitTests`. Esperado: falla de compilación (`SiigoCallFailure.Create` no existe). Pega la salida.
- [ ] **Paso 3: implementa** entidad y enum (Domain), el código de error, y en `SiigoDbContext`: `internal DbSet<SiigoCallFailure> CallFailures` y `ConfigureCallFailure` (mismo estilo de `ConfigureInvoice`: `HasColumnName`, `operation` con `HasConversion<string>().HasMaxLength(40)`, `request_curl`/`response_body` con `HasColumnType("text")`, índice `(tenant_id, occurred_at)` descendente en el segundo campo y `(occurred_at)`).
- [ ] **Paso 4: genera la migración** — PowerShell: `dotnet ef migrations add AddSiigoCallFailures --project src/Modules/Siigo/Modules.Siigo.Infrastructure --context SiigoDbContext` (usa `SiigoDbContextFactory`). Revisa que `Up` solo cree la tabla y los dos índices y que `Down` la borre (migración expand-only). Confirma que el snapshot cambió.
- [ ] **Paso 5: GREEN** — `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests --filter "FullyQualifiedName~SiigoCallFailureTests|FullyQualifiedName~SiigoDbContextMappingTests"`. Esperado: verde.
- [ ] **Paso 6: commit** — `feat(siigo): add the siigo.call_failures table`.

---

### Tarea 2: El cliente reporta todo fallo con su operación

**Archivos:** modificar `ISiigoCallFailureLog.cs`, `SiigoApiClient.cs`, `SiigoCurl.cs`; probar en `SiigoApiClientTests.cs`; eliminar `src/Bootstrapper/SiigoCallFailureLog.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/SiigoCallFailureLogTests.cs`; revertir `WithDiagnostic` y `DiagnosticDataKey` en `src/Api/RequestFailureCapture.cs`; quitar el registro `services.AddScoped<ISiigoCallFailureLog, SiigoCallFailureLog>()` de `QepServiceCollectionExtensions.cs`.

**Interfaces — consume:** `SiigoCallOperation` (Tarea 1). **Produce:**
- `public sealed record SiigoCallFailureReport(Guid ConnectionId, SiigoCallOperation Operation, string Method, string Path, int? Status, string Reason, string? IdempotencyKey, string? SiigoInvoiceId, string RequestCurl, string? ResponseBody)` (con `ToString()` oculto) y `ISiigoCallFailureLog.RecordAsync(SiigoCallFailureReport report, CancellationToken cancellationToken)` (reemplaza al record `SiigoCallFailure` de Application).
- En el cliente, `Reply` lleva `FailureCapture? Capture` — `record FailureCapture(string Method, string Path, string RequestCurl, string? ResponseBody)` — en lugar de `Diagnostic`. La respuesta se captura aunque el `Reply` descarte el cuerpo de un 5xx.

Operación por método del cliente: `ListDocumentTypesAsync`, `ListUsersAsync`, `ListCostCentersAsync`, `ListWarehousesAsync`, `ListPaymentTypesAsync`, `ListTaxesAsync` → `ListRead`; `SearchProductsAsync` → `ProductSearch`; `FindCustomerAsync` → `CustomerFind`; `CreateCustomerAsync` → `CustomerCreate`; `CreateInvoiceAsync` → `InvoiceCreate` (con su `idempotencyKey`); `StampInvoiceAsync` → `InvoiceStamp` (con `siigoInvoiceId`); `GetStampErrorsAsync` → `StampErrors` (con `siigoInvoiceId`). Si el fallo es de la ruta `auth`, la operación es `Auth`, sea quien sea el método.

- [ ] **Paso 1: pruebas RED** — reescribe en `SiigoApiClientTests` las cinco pruebas del 2026-11-21 contra el registrador falso (`RecordingFailureLog` con `List<SiigoCallFailureReport>`): `ARejectedInvoiceLeavesItsReport` (operación `InvoiceCreate`, `Status` 400, `IdempotencyKey` `abc123`, `RequestCurl` con `curl.exe -X POST "https://siigo.test/v1/invoices"`, `Partner-Id: qepTest`, el cuerpo y `Bearer <TOKEN>`, sin `token-one-SENTINEL` ni `AccessKey`; `ResponseBody` con `invalid_total_payments`), `ATransientFailureLeavesItsReason` (502 → `Reason` `http_502`, `ResponseBody` con el HTML aunque el `Reply` lo descarte), `ACreatedInvoiceLeavesNothing`, `A401ThatRenewsTheTokenAndSucceedsLeavesNothing`, `ASecond401LeavesCredentialsRejected` (una fila, `Reason` `credentials_rejected`, `Operation` `InvoiceCreate`). Añade `AFailedAuthIsReportedAsAuth` (`/auth` responde 401 → una fila, `Operation` `Auth`, curl con `<ACCESS_KEY>` y sin `AccessKey`), `AThrownReadFailureIsReportedOnceAndTheExceptionCarriesNoCurl` (422 en `/v1/customers` → una fila `CustomerFind`, `Assert.Empty(exception.Data)`) y `AStampFailureCarriesTheSiigoInvoiceId`.
- [ ] **Paso 2: verifica el RED** — `dotnet build tests/Modules/Siigo/Modules.Siigo.UnitTests`. Esperado: no compila (`SiigoCallFailureReport` no existe). Pega la salida.
- [ ] **Paso 3: implementa** el cambio de contrato (Application) y del cliente: `SendAsync` arma `FailureCapture` cuando la respuesta no es 2xx o hay timeout/red (usa `SiigoCurl.Build` y recorta nada: los topes los aplica la entidad); `CallAsync` propaga `reply.Capture` en los `Reply.Rejected` y los de `/auth`; un método privado `ReportFailureAsync(SiigoCredentials credentials, SiigoCallOperation operation, Reply reply, string? idempotencyKey, string? siigoInvoiceId, CancellationToken cancellationToken)` arma el `SiigoCallFailureReport` (la operación pasa a `Auth` si `capture.Path == "auth"`) y lo envía al puerto. `GetAsync` lo llama **antes** de lanzar, con una `SiigoCallOperation` que ahora recibe; `CreateCustomerAsync` igual; `CreateInvoiceAsync`, `StampInvoiceAsync` lo llaman como hoy. Elimina `SiigoCurl.Describe`, `Reply.Diagnostic`, `WithDiagnostic` y `DiagnosticDataKey`.
- [ ] **Paso 4: retira lo viejo** — borra el adaptador de Bootstrapper y su prueba, su registro DI, y `WithDiagnostic`/`DiagnosticDataKey` de `RequestFailureCapture.cs` (vuelve a `exception.ToString()`).
- [ ] **Paso 5: GREEN** — `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests --filter "FullyQualifiedName~SiigoApiClientTests"`, y `dotnet build src/Api`. Esperado: verde y `Compilación correcta`.
- [ ] **Paso 6: commit** — `refactor(siigo): report every failed call by operation and drop the request-log path`.

---

### Tarea 3: Escritura propia y expiración

**Archivos:** crear `src/Modules/Siigo/Modules.Siigo.Infrastructure/Persistence/SiigoCallFailureLog.cs`; modificar `SiigoInfrastructureExtensions.cs`; probar en `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/SiigoCallFailureLogTests.cs`.

**Interfaces — consume:** `SiigoCallFailureReport`, `SiigoCallFailure.Create`, `IExecutionContext` y `IClock` (`BuildingBlocks.Application`). **Produce:**
- `internal sealed partial class SiigoCallFailureLog(DbContextOptions<SiigoDbContext> options, IExecutionContext executionContext, IClock clock, ILogger<SiigoCallFailureLog> logger) : ISiigoCallFailureLog`, con `public const int RetentionDays = 7` y `public const int ExpiryBatchSize = 200`; registrado `AddScoped<ISiigoCallFailureLog, SiigoCallFailureLog>()`.
- Sin tenant en el contexto (un llamador sin request) no escribe nada.

- [ ] **Paso 1: pruebas RED** — con el harness de Siigo (`SiigoApiHarness`), cuatro pruebas: `RecordWritesTheRowWithTheTenantOfTheContext`, `RecordDeletesWhatIsOlderThanSevenDaysAndKeepsWhatIsNewer`, `RecordDeletesAtMostTwoHundredExpiredRowsPerWrite` (siembra 250 vencidas → quedan 50 + la nueva), `RecordSwallowsItsOwnFailure` (un `DbContextOptions` hacia una base inexistente: `RecordAsync` no lanza y deja una línea `Error` en el log). La del tenant ajeno: una fila vencida de **otro** tenant también se borra (el log es operativo).
- [ ] **Paso 2: verifica el RED** — `dotnet build tests/Modules/Siigo/Modules.Siigo.IntegrationTests`. Esperado: no compila (`SiigoCallFailureLog` no existe).
- [ ] **Paso 3: implementa** `RecordAsync`: contexto propio (`new SiigoDbContext(options)`), `Add` + `SaveChangesAsync`, y después `ExecuteDeleteAsync` de las filas cuyo `Id` esté en la subconsulta de las `ExpiryBatchSize` más viejas con `OccurredAt < clock.UtcNow.AddDays(-RetentionDays)`; `trace_id` de `Activity.Current?.Id`; `subject_id` de `executionContext`. Un `catch (Exception)` ancho que solo anota con `LoggerMessage` (patrón de `RequestFailureLog`).
- [ ] **Paso 4: GREEN** — una corrida en primer plano: `dotnet test tests/Modules/Siigo/Modules.Siigo.IntegrationTests --filter "FullyQualifiedName~SiigoCallFailureLogTests"`. Esperado: verde. Pega el resumen.
- [ ] **Paso 5: commit** — `feat(siigo): write the failed calls with their own context and expire them after seven days`.

---

### Tarea 4: Lectura, permiso y docs

**Archivos:** crear `src/Modules/Siigo/Modules.Siigo.Application/CallFailures/ListSiigoCallFailures.cs`, `GetSiigoCallFailure.cs`, `SiigoCallFailureDtos.cs`, `src/Modules/Siigo/Modules.Siigo.Infrastructure/Persistence/SiigoCallFailureRepository.cs`; modificar `SiigoPorts.cs`, `SiigoPermissions.cs`, `SiigoEndpoints.cs`, `SiigoInfrastructureExtensions.cs`, `QepServiceCollectionExtensions.cs`, `SiigoNoLeakTests.cs`, `.claude/rules/siigo.md`; probar en `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/SiigoCallFailuresApiTests.cs`.

**Interfaces — produce:**
- `SiigoPermissions.CallsRead = "siigo.calls.read"`.
- `ISiigoCallFailureRepository`: `Task<(IReadOnlyList<SiigoCallFailure> Items, int Total)> SearchAsync(Guid tenantId, SiigoCallOperation? operation, int? status, DateTimeOffset? from, DateTimeOffset? to, int page, int pageSize, CancellationToken cancellationToken)` y `Task<SiigoCallFailure?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)` (`AsNoTracking`).
- `ListSiigoCallFailuresQuery(Guid TenantId, SiigoCallOperation? Operation, int? Status, DateTimeOffset? From, DateTimeOffset? To, int Page, int PageSize) : IQuery<SiigoCallFailurePageDto>`; `SiigoCallFailurePageDto(IReadOnlyList<SiigoCallFailureSummaryDto> Items, int Total, int Page, int PageSize)`; `SiigoCallFailureSummaryDto(Guid Id, DateTimeOffset OccurredAt, string Operation, string Method, string Path, int? Status, string Reason, string? IdempotencyKey, string? SiigoInvoiceId, string? TraceId)`; `MaxPageSize = 100`, `DefaultPageSize = 25`.
- `GetSiigoCallFailureQuery(Guid TenantId, Guid Id) : IQuery<SiigoCallFailureDetailDto>`; `SiigoCallFailureDetailDto` = el resumen más `string RequestCurl, string? ResponseBody`. Un id inexistente o de otro tenant → `ResourceNotFoundException(SiigoErrorCodes.CallFailureNotFound, …)`.
- Rutas bajo `/api/v1/tenants/{tenantId:guid}/siigo/call-failures` con `RequireAuthorization(SiigoPermissions.CallsRead)`: `GET /?page&pageSize&operation&status&from&to` y `GET /{id:guid}`. El `operation` inválido → 422 por validación del handler.

- [ ] **Paso 1: pruebas RED** — `SiigoCallFailuresApiTests`: `AdminListsTheFailuresWithoutCurlOrBody` (el JSON de la lista no contiene `requestCurl` ni `responseBody`), `TheDetailCarriesTheCurlAndTheResponse`, `TheListFiltersByOperationStatusAndDates`, `AMemberWithoutThePermissionGets403` (stub con otro juego de permisos por `X-Permissions`, ver `pruebas-y-entorno-local`), `AnotherTenantsRouteGets403NotTheRowsOf404`, `ADetailIdOfAnotherTenantGets404WithTheCode`, `TheListIsNewestFirstAndPaged`. `SiigoNoLeakTests`: añade el caso «tras un fallo de `/auth` y un rechazo de factura, ninguna columna de `siigo.call_failures` contiene el token ni la `access_key` sentinela» (lee primero cómo el archivo arma sus sentinelas).
- [ ] **Paso 2: verifica el RED** — `dotnet build tests/Modules/Siigo/Modules.Siigo.IntegrationTests`. Esperado: no compila.
- [ ] **Paso 3: implementa** handlers (`SiigoAuthorization.EnsureAsync` con `SiigoPermissions.CallsRead`, `PageSize` normalizado como en `RequestFailurePaging`), repositorio (filtros opcionales omitidos del `Where` cuando no vienen, orden `OccurredAt` descendente), endpoints, y en `QepServiceCollectionExtensions`: `SiigoPermissions.CallsRead` en la lista de `admin` (junto a `SettingsManage`/`InvoiceIssue`), su `PermissionDefinition` (`"Ver las fallas de Siigo"`, módulo `Siigo`, riesgo `"high"`, `RequiredModules: [ModuleKeys.Siigo]`, `Shape = new("siigo.calls", "Fallas de las llamadas a Siigo", "read")`), su política `AddAuthorization`, y los dos `AddScoped<IQueryHandler<…>>`. El DTO del detalle lleva el motivo BFF en su resumen XML.
- [ ] **Paso 4: barre las pruebas de permisos** — busca pruebas que cuenten permisos o definiciones (`PermissionDefinition`, `SiigoPermissions`, catálogo de roles) y corrígelas si el permiso nuevo las mueve: `dotnet test tests/Modules/Tenancy --filter "FullyQualifiedName~Permission"` y `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~Siigo|FullyQualifiedName~CompositionRoot"`.
- [ ] **Paso 5: GREEN** — una corrida en primer plano: `dotnet test tests/Modules/Siigo/Modules.Siigo.IntegrationTests --filter "FullyQualifiedName~SiigoCallFailuresApiTests|FullyQualifiedName~SiigoNoLeakTests"`. Esperado: verde.
- [ ] **Paso 6: documenta** — en `.claude/rules/siigo.md` reemplaza la viñeta «Una llamada a Siigo que falla deja su curl en el log de fallas…» por la de `siigo.call_failures`: qué guarda, 7 días, expiración al escribir, permiso `siigo.calls.read`, que un éxito nunca se guarda (la respuesta de `/auth` trae el token) y que el log de requests solo conserva la excepción, ligada por `TraceId`. **No toques `docs/estado.md`** (se actualiza en `develop`, al mergear).
- [ ] **Paso 7: cierre del bloque B** — suite de los proyectos tocados, un proyecto a la vez, con `-- xUnit.MaxParallelThreads=4`: `Modules.Siigo.UnitTests`, `Modules.Siigo.IntegrationTests`, `Bootstrapper.UnitTests`, `ArchitectureTests`. Pega los resúmenes.
- [ ] **Paso 8: commit** — `feat(siigo): list and read the failed Siigo calls behind siigo.calls.read`.

---

## Revisión final

Una sola revisión fuerte de toda la rama (modelo más capaz) al terminar los dos bloques. Después, merge a `develop` con `--no-ff`, y el Registro y la fila de `docs/estado.md` en `develop` (commit `docs:`). El frontend va en su propio spec en `qep-frontend`.

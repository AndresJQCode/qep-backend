# Exportación asíncrona de cotizaciones y ventas — plan de implementación (qep-backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que `POST /quotations/export` y `POST /sales/export` validen, encolen y respondan `202` en milisegundos, y que un worker arme el Excel en streaming, lo suba a R2 y dispare el correo por Notifications, reemplazando el `GET /quotations/export` síncrono de `572200c`.

**Architecture:** La cola es la tabla `quotations.export_jobs` detrás del puerto `IExportJobQueue` (Application) con adaptador EF/SQL crudo (Infrastructure): la toma exclusiva es un `UPDATE … FOR UPDATE SKIP LOCKED … RETURNING` con lease de 10 minutos. `ExportJobWorker` (un `BackgroundService`, concurrencia 1) corre `ExportJobRunner`, que despacha por `kind` a `QuotationsExportProcessor` / `SalesExportProcessor`; cada procesador lee por lotes de 1.000 con el mismo filtro del listado, escribe el `.xlsx` con `OpenXmlWriter` a un temporal y lo sube con `IExportFileStorage` (clave con el `jobId`). Terminar es una transacción: `Completed` + `quotations.export-ready.v1` + auditoría; un fallo reintenta con backoff o termina en `Failed` + `quotations.export-failed.v1`. Notifications consume los dos eventos con dos workers calcados de `CustomerExportDeliveryWorker`.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, FluentValidation, DocumentFormat.OpenXml 3.1.1, xUnit v3, Testcontainers (Docker corriendo para las pruebas de integración).

**Spec:** `docs/superpowers/specs/2026-09-12-export-asincrono-design.md` — leerla antes de la primera tarea; es la autoridad (D1–D15). Este plan cubre sólo el **backend**; el frontend tiene su plan en `qep-frontend/docs/superpowers/plans/2026-09-12-export-asincrono-frontend.md`.

## Global Constraints

- Rango obligatorio: `createdFrom`/`createdTo` (cotizaciones) y `convertedFrom`/`convertedTo` (ventas); `from <= to` y `to <= from.AddYears(1)` — un año exacto vale; `2024-02-29` + 1 año = `2025-02-28`.
- Orden de validación del request (D4): tenant/permiso `403` → filtros y rango `422 validation.failed` con `errors` → al menos una fila (`EXISTS`) → límite de pendientes. Nada se encola si algo falla.
- Límite: **3** exportaciones `Pending` + `Processing` por usuario (`requested_by`) y tenant, **contando los dos tipos**.
- Códigos: `quotation.export.empty`, `quotation.export.pending_limit`, `sale.export.empty`, `sale.export.pending_limit`.
- Contrato: `POST /api/v1/tenants/{tenantId}/quotations/export` (`QuotationRead`) y `POST /api/v1/tenants/{tenantId}/sales/export` (`SaleRead`), filtros por query string sin paginación, respuesta `202 { jobId, requestedAt }`. **Ningún permiso nuevo**: se reusan los dos existentes con sus políticas ya registradas.
- Lease **10 min**; poll del worker **5 s**; concurrencia **1** por proceso; lotes de **1.000** filas.
- Lectura **por keyset, nunca offset** (spec D8, hallazgo 11): cotizaciones por `(CreatedAt DESC, QuotationNumber DESC)`, ventas por `(ConvertedAt DESC, SaleNumber DESC)` —el orden de `SaleRepository.SearchAsync`—. `ListForExportAsync` recibe `after` (`QuotationExportCursor` / `SaleExportCursor`: la clave de la última fila leída, `null` en el primer lote) y `limit`, y la condición va en su forma OR porque EF no compara tuplas. Los filtros salen del mismo `FilteredQuery` / `Filtered` que el listado.
- Reintentos: **`MaxAttempts = 4`**. Backoff de **1 min** después del 1.º intento fallido, **5 min** después del 2.º y **15 min** después del 3.º; el **4.º** fallido → `Failed` + `quotations.export-failed.v1`. Definitivo (filtros ilegibles, cero filas al procesar) → `Failed` directo.
- Retención: el worker borra **una vez al día** los jobs `Completed`/`Failed` con `completed_at` de más de **30 días**.
- Eventos: `quotations.export-ready.v1` (payload `tenantId, subjectId, kind, downloadUrl, fileName, rowCount, expiresAt`) y `quotations.export-failed.v1` (payload `tenantId, subjectId, kind`).
- Archivos: `cotizaciones-yyyy-MM-dd-HHmm.xlsx` y `ventas-yyyy-MM-dd-HHmm.xlsx` (hora UTC de generación). Clave en R2: `exports/tenants/{tenantId:N}/jobs/{jobId:N}.xlsx` —**bajo `exports/`**, el prefijo de la regla de lifecycle que ya existe (hallazgo 8)—. Vigencia del enlace: `Storage:ExportUrlHours` (`StorageOptions.cs:15`, `24` en `appsettings.json:27`), la misma opción que lee `CustomerExportStorage.cs:49`.
- Hoja: cabecera en negrita y congelada, fechas como texto ISO-8601 (`"O"`), totales numéricos, encabezados sin tildes, anchos fijos.
- `last_error`: sólo `TipoDeExcepcion: mensaje`, truncado a 2.000 caracteres. Nunca un secreto.
- Copy de correos: español colombiano, tuteando.
- TDD obligatorio: RED antes que GREEN, pegando la salida literal de ambos en el handoff.
- **Nunca** `dotnet format` mutante sobre la solución (quita el BOM de ~350 archivos). Sólo `dotnet format --verify-no-changes`.
- Lock files: cualquier cambio de paquetes se acompaña, **en el mismo commit**, de `dotnet restore --force-evaluate` y de un `dotnet restore --locked-mode` que pase.
- `Api.exe` corriendo bloquea `build`/`test`/`ef`: `Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force` antes.
- Rama `feature/export-asincrono`. **Cada** paso de commit lleva el guard de rama en el mismo comando y agrega rutas explícitas (nunca `git add -A` ni `git add .`). Los bloques de commit van en **Git Bash** porque el guard es sintaxis POSIX; el resto de los comandos, en PowerShell.
- Commits: conventional commits en español, **sin atribución de IA ni trailer `Co-Authored-By`**.
- Regresión medida **por nombre de prueba** contra el baseline de Task 0, nunca por conteo.

---

## Hallazgos contra el código (2026-09-12)

Verificados leyendo el código de `feature/export-asincrono` en `66073a0` —el mismo commit que `origin/develop`, con `8b8c7a8` ya adentro—, no supuestos. La primera versión se escribió sobre `af62536`; el hallazgo 12 dice qué cambió desde entonces.

1. **Prefijo de los códigos de ventas: `sale.`** Los códigos del lado de ventas son `sale.<área>.<motivo>`: `sale.sale.status_invalid` (`ListSales.cs:149`), `sale.sale.payment_status_invalid` (`ListSales.cs:163`), `sale.payment_proof.*` (`SalePaymentProofResolver.cs:32-53`); el `sale.summary.range_invalid` que citaba la primera versión se fue con `GetSalesSummary.cs` (`b840526`). El export de cotizaciones ya usa el área `export` (`quotation.export.empty`, `ExportQuotations.cs:99`). Por eso quedan `sale.export.empty` y `sale.export.pending_limit`, tal como los escribió la spec.
2. **Columnas del Excel de ventas** (D8 lo dejó al plan). La fila es `SaleListItemResponse` (`SalesDtos.cs:60-86`); la tabla del frontend pinta `Venta, Cliente, Asesora, Fecha, Pago, Estado, Total` (`qep-frontend/src/features/sales/components/sale-table.tsx:32-38`), donde **Pago** es `paymentMethod ?? estado del pago` (`sale-table.tsx:62-65`) y **Total** va formateado con la moneda (`sale-table.tsx:69-71`). Siguiendo el precedente del Excel de cotizaciones —que separó `Moneda` antes de `Total` y escribió `Asesor` aunque la tabla diga «Asesora» (`ClosedXmlQuotationExportBuilder.cs:31-40`)— las columnas quedan: **`Venta, Cliente, Asesor, Fecha, Pago, Estado, Moneda, Total`**. `Pago` replica el respaldo de la tabla: `PaymentMethod ?? PaymentStatus`, con el estado por su nombre de enum (`PaymentPending`), igual que `Estado` viaja como `Pending`/`Approved` y el de cotizaciones como `Draft`.
3. **Los lookups no dependen del request.** `QuotationCustomerLookup` y `QuotationAdvisorLookup` (`src/Bootstrapper`) no usan `IExecutionContext` ni `HttpContext`: se pueden resolver en el scope del worker sin sesión.
4. **`IObjectStorage` sólo sube `byte[]`** (`IObjectStorage.cs:49-50`). El adaptador de `IExportFileStorage` lee el temporal a bytes para subirlo. Es aceptable: el `.xlsx` va comprimido y pesa órdenes de magnitud menos que el grafo de celdas de ClosedXML. Agregar una sobrecarga con `Stream` obligaría a tocar Storage y los `InMemoryObjectStorage` de todos los harnesses; el puerto recibe una **ruta** para que ese cambio, si hace falta, quede en el adaptador.
5. **Reintentos: cuatro intentos, así se usan las tres esperas** (decisión del developer, 2026-09-12). La primera versión de la spec tenía tres esperas y tres intentos, con lo que la de 15 minutos nunca se alcanzaba. Queda `MaxAttempts = 4` —el primero y un reintento por cada espera de `ExportJob.RetryDelays`—: unos 21 minutos entre el primer fallo y el correo de fallo. El archivo llega por correo y nadie está mirando la pantalla, así que esa ventana cuesta menos que avisar un fallo que una caída corta de R2 o de la base habría resuelto sola.
6. **Un worker que muere en el último intento.** La toma suma un intento aunque el anterior no haya registrado su fallo (el worker murió). Si al tomar el job queda con `Attempts > MaxAttempts`, el runner lo pasa a `Failed` sin procesarlo: un job no puede consumir intentos para siempre por lease vencido.
7. **Lease perdido.** `Attempts` es token de concurrencia en EF: si un worker lento termina después de que otro retomó el job, su `UPDATE` no encuentra la fila con los intentos que leyó, `QuotationsUnitOfWork` lo traduce a `RequestConcurrencyException` (`QuotationsUnitOfWork.cs:32-38`) y el runner lo descarta sin evento. Nunca salen dos correos del mismo job.
8. **La regla de lifecycle de R2 ya existe; no está pendiente.** `README.md:846-860` documenta `expire-exports` sobre el prefijo `exports/` del bucket privado, con `--expire-days 2`, configurada a mano en Cloudflare, y advierte que `--expire-days` tiene que cubrir `ExportUrlHours` con margen. Lo que este plan tiene que garantizar es que el objeto caiga **bajo `exports/`**: `ExportFileStorage.KeyFor` arma `exports/tenants/{tenantId:N}/jobs/{jobId:N}.xlsx` y `ExportFileStorageTests` afirma el prefijo (Task 9). La vigencia sale de la opción que ya existe, `Storage:ExportUrlHours` (`StorageOptions.cs:15`, `24` en `appsettings.json:27`, validada entre 1 y 168 en `StorageOptionsValidator.cs:18-20`), la misma que lee `CustomerExportStorage.cs:49`; la prueba la sube a 48 para que un 24 fijo a mano no pase. Lo único que queda es **verificar** que la regla sigue en el bucket (`npx wrangler r2 bucket lifecycle list <bucket-privado>`, ver «Después del último commit»); este plan no crea ninguna.
9. **`DocumentFormat.OpenXml` no está en `Directory.Packages.props`.** Hoy llega transitivo por ClosedXML (`3.1.1`, lock de `Modules.Quotations.Infrastructure`). Con `CentralPackageTransitivePinningEnabled=true`, declarar su `PackageVersion` lo fija también en los proyectos que lo reciben por ClosedXML (Customers, Catalog y sus pruebas): **cambian más lock files que los 16 de `572200c`**, con la versión igual. Por eso Task 10 los lista con `git diff` en vez de a mano.
10. **El worker de exportaciones correría solo en las pruebas.** `WebApplicationFactory` arranca los hosted services; con un poll de 5 s competiría con el tick que la prueba dispara. `QepApiFactory` gana un parámetro `runExportWorker` (por defecto `false`) que saca el hosted service; las pruebas corren `ExportJobRunner` directo, igual que las de vencimiento corren `IQuotationExpirationProcessor`.
11. **Keyset y no offset** (decisión del developer, 2026-09-12; spec D8). El rango por defecto es el mes en curso con hoy adentro: con offset, una cotización o venta creada entre dos lotes corre las filas y repite la del borde, y una ya leída que sale del filtro hace saltear la siguiente —la suma de Total queda mal sin ninguna señal—, además de que cada lote lee y descarta todo lo anterior. La clave de orden:
    - **Cotizaciones: `(CreatedAt DESC, QuotationNumber DESC)`**, no `(CreatedAt, Id)`. `QuotationId` es un `readonly record struct` sin operadores de comparación (`QuotationId.cs:3`), mapeado con `HasConversion` (`QuotationsDbContext.cs:48-51`): `quotation.Id < after.Id` no compila, y EF tampoco lo traduciría. `QuotationNumber` es único por tenant (`IX_quotations_tenant_number`, `QuotationsDbContext.cs:151-153`), así que la clave nunca empata. El listado ordena sólo por `CreatedAt` (`QuotationRepository.cs:57`); el número sólo desempata instantes iguales.
    - **Ventas: `(ConvertedAt DESC, SaleNumber DESC)`**, exactamente el orden del listado (`SaleRepository.cs:150-151`), único por tenant (`IX_sales_tenant_number`, `QuotationsDbContext.cs:377-379`). `SaleId` tiene el mismo problema que `QuotationId` (`SaleId.cs:3`).
    - EF no compara tuplas: la condición va como `fecha < @fecha OR (fecha = @fecha AND numero < @numero)`, con `string.Compare(…) < 0`, que EF traduce a `<` sobre la columna —con su collation, la misma del `ORDER BY`—. `Directory.Build.props:7-8` tiene `TreatWarningsAsErrors` y `AnalysisLevel 10.0-recommended`: si el build marca CA1310 sobre ese `string.Compare`, ver la nota de Task 11.
    - **No había índice para ninguno de los dos órdenes.** Hoy existen `IX_quotations_tenant` e `IX_quotations_created_at` sueltos (`QuotationsDbContext.cs:143,147`) e `IX_sales_tenant` (`:370`), y nada sobre `converted_at`. Task 2 agrega `IX_quotations_tenant_created_at_number (tenant_id, created_at, quotation_number)` e `IX_sales_tenant_converted_at_number (tenant_id, converted_at, sale_number)` en la misma migración `AddExportJobs`. Postgres recorre un btree en los dos sentidos, así que sirven al `DESC` sin declararlo. Son `CREATE INDEX` sin `CONCURRENTLY` sobre tablas chicas: bloquean escrituras unos segundos, al arrancar.
    - Las pruebas que lo fijan (Tasks 11 y 13) leen de a una fila y meten los dos cambios entre lotes: con offset repetirían una fila y saltearían otra; con keyset salen las que existían al empezar, una vez cada una.
12. **`develop` se movió después de la primera versión de este plan.** `b840526` borró `GetSalesSummary.cs` y sus pruebas, sacó `GET /sales/summary` de `SaleEndpoints.cs` y achicó `ISaleRepository.cs`, `SaleRepository.cs` y `StubSaleListRepository`; `ba5cdb9` agregó `BatchUpdateQuotationItems.cs` y su ruta, que corrió los handlers de `QuotationEndpoints.cs`; `dd646a0` tocó `QuotationsApiHarness.cs`; `389e344` borró `SaleSummaryApiTests.cs`. Las referencias `archivo:línea` de este plan están re-verificadas contra `66073a0`; las que cambiaron están en Tasks 4, 9, 10 y 12.

## Estado de las ramas

La precondición de la primera versión ya se cumple. `refactor/quitar-exports-de-reportes` (`8b8c7a8`) entró a `develop` con `1730eb3`, y `feature/export-asincrono` está en `66073a0`, el mismo commit que `origin/develop`. `IQuotationExportWorkbookBuilder.cs` y `ClosedXmlQuotationExportBuilder.cs` ya traen los comentarios de `8b8c7a8`, así que Task 10 los borra sin conflicto. Task 0 sólo lo comprueba: la rama no se recrea ni se mueve.

## Entrega

Cuatro commits, cada uno compila y deja la suite en verde:

| Commit | Tareas |
| --- | --- |
| `feat(quotations): cola de exportaciones con worker y reintentos` | 1–5 |
| `feat(notifications): correos de exportación lista y fallida` | 6–7 |
| `feat(quotations): exportar cotizaciones por correo` | 8–11 |
| `feat(sales): exportar ventas por correo` | 12–13 |

Las tareas intermedias de cada commit terminan con **stage** (rutas explícitas, con guard); la última de cada grupo commitea. **Despliegue:** este backend sale antes que el frontend (migración y endpoints); al revés, el botón nuevo le pega a un `POST` que no existe.

---

## File Structure

**Crear — producción**

| Archivo | Responsabilidad |
| --- | --- |
| `src/Modules/Quotations/Modules.Quotations.Domain/ExportJobKind.cs` | `Quotations` \| `Sales` |
| `src/Modules/Quotations/Modules.Quotations.Domain/ExportJobStatus.cs` | `Pending` \| `Processing` \| `Completed` \| `Failed` |
| `src/Modules/Quotations/Modules.Quotations.Domain/ExportJob.cs` | Estado del job y sus transiciones: encolar, tomar, completar, reintentar/fallar; constantes de lease, intentos, backoff y retención |
| `src/Modules/Quotations/Modules.Quotations.Application/ExportJobLimits.cs` | Límite de pendientes por persona y tamaño de lote |
| `src/Modules/Quotations/Modules.Quotations.Application/IExportJobQueue.cs` | Puerto de la cola: agregar, contar pendientes, tomar, purgar |
| `src/Modules/Quotations/Modules.Quotations.Application/IExportJobProcessor.cs` | Puerto del procesador por `kind`, `ExportJobResult`, `ExportJobDefinitiveException` |
| `src/Modules/Quotations/Modules.Quotations.Application/IExportEventPublisher.cs` | Puerto de los eventos lista/fallida |
| `src/Modules/Quotations/Modules.Quotations.Application/ExportJobRunner.cs` | Un tick: toma, despacha, cierra en una transacción, clasifica fallos; purga |
| `src/Modules/Quotations/Modules.Quotations.Application/IExportWorkbookWriter.cs` | Puerto del writer en streaming: columnas, celdas, workbook |
| `src/Modules/Quotations/Modules.Quotations.Application/IExportFileStorage.cs` | Puerto de subida con clave por `jobId` |
| `src/Modules/Quotations/Modules.Quotations.Application/ExportJobSupport.cs` | Nombres de archivo y (de)serialización de filtros |
| `src/Modules/Quotations/Modules.Quotations.Application/QuotationsExportProcessor.cs` | Lee cotizaciones por lotes, escribe, sube |
| `src/Modules/Quotations/Modules.Quotations.Application/SaleListing.cs` | Parseo de estados y resolución de CUC/filas compartidos por listado y export de ventas |
| `src/Modules/Quotations/Modules.Quotations.Application/ExportSales.cs` | Comando, validador y handler del `POST /sales/export` |
| `src/Modules/Quotations/Modules.Quotations.Application/SalesExportProcessor.cs` | Lee ventas por lotes, escribe, sube |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/ExportJobQueue.cs` | Adaptador de la cola: `SKIP LOCKED`, conteo, purga |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/ExportJobEventPublisher.cs` | Escribe los dos eventos en la proyección de outbox |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<timestamp>_AddExportJobs.cs` (+ `.Designer.cs`) | Migración generada |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Exports/ExportJobWorker.cs` | `BackgroundService`: poll de 5 s, un job por scope, purga diaria |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Excel/OpenXmlExportWorkbookWriter.cs` | `OpenXmlWriter` a archivo temporal |
| `src/Bootstrapper/ExportFileStorage.cs` | Adaptador de `IExportFileStorage` sobre `IObjectStorage` |
| `src/Modules/Notifications/Modules.Notifications.Application/QuotationsExportKindText.cs` | Cómo se nombra cada `kind` en el correo |
| `src/Modules/Notifications/Modules.Notifications.Application/QuotationsExportReadyEmailTemplate.cs` | Correo de export listo |
| `src/Modules/Notifications/Modules.Notifications.Application/QuotationsExportFailedEmailTemplate.cs` | Correo de export fallido |
| `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/QuotationsExportReadyDeliveryWorker.cs` | Consume `quotations.export-ready.v1` |
| `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/QuotationsExportFailedDeliveryWorker.cs` | Consume `quotations.export-failed.v1` |

**Modificar — producción**

| Archivo | Cambio |
| --- | --- |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` | `ExportJobs`, `ConfigureExportJob` y los índices del keyset en `quotations` y `sales` (hallazgo 11) |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/QuotationsDbContextModelSnapshot.cs` | Regenerado por `dotnet ef` |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs` | Cola, publisher, worker, writer; sale el builder de ClosedXML |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Modules.Quotations.Infrastructure.csproj` | `DocumentFormat.OpenXml` directo; sale `ClosedXML` |
| `Directory.Packages.props` | `PackageVersion` de `DocumentFormat.OpenXml` 3.1.1 |
| `src/Modules/Quotations/Modules.Quotations.Application/ExportQuotations.cs` | De query con archivo a comando que encola |
| `src/Modules/Quotations/Modules.Quotations.Application/IQuotationRepository.cs` | `QuotationExportCursor`, `ListForExportAsync` por keyset, `AnyForExportAsync` |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationRepository.cs` | Idem |
| `src/Modules/Quotations/Modules.Quotations.Application/ISaleRepository.cs`, `…/Infrastructure/Persistence/SaleRepository.cs` | `SaleExportCursor`, `AnyForExportAsync`, `ListForExportAsync` por keyset; filtros compartidos |
| `src/Modules/Quotations/Modules.Quotations.Application/ListSales.cs` | Usa `SaleListing` |
| `src/Modules/Quotations/Modules.Quotations.Application/QuotationsDtos.cs` | `ExportJobAcceptedResponse` |
| `src/Modules/Quotations/Modules.Quotations.Api/QuotationEndpoints.cs` | Sale el `GET /export`, entra el `POST /export` |
| `src/Modules/Quotations/Modules.Quotations.Api/SaleEndpoints.cs` | `POST /sales/export` |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` | Runner, handlers, procesadores, storage |
| `src/Modules/Notifications/Modules.Notifications.Infrastructure/NotificationsInfrastructureExtensions.cs` | Los dos workers |
| `README.md:838-860` | La sección `exports/` nombra los dos exports nuevos |
| ~16+ `packages.lock.json` | Regenerados en Task 10 |

**Borrar:** `src/Modules/Quotations/Modules.Quotations.Application/IQuotationExportWorkbookBuilder.cs`, `src/Modules/Quotations/Modules.Quotations.Infrastructure/Excel/ClosedXmlQuotationExportBuilder.cs` (y con él el aviso de `AdjustToContents` de la revisión).

**Pruebas**

| Archivo | Qué |
| --- | --- |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportJobTests.cs` | Crear: transiciones del job |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportTestDoubles.cs` | Crear: cola en memoria, procesadores, publisher, writer y storage de prueba |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportJobRunnerTests.cs` | Crear: el tick, sin base |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs` | Modificar: mapeo de `export_jobs` e índices del keyset |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/OpenXmlExportWorkbookWriterTests.cs` | Crear: forma de la hoja |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportQuotationsHandlerTests.cs` | Reescribir: orden de D4, encolado |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportQuotationsValidatorTests.cs` | Modificar: comando en vez de query |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsExportProcessorTests.cs` | Crear |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportSalesHandlerTests.cs`, `ExportSalesValidatorTests.cs`, `SalesExportProcessorTests.cs` | Crear |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs` | Modificar: repositorios con los métodos nuevos; sale `RecordingQuotationExportWorkbookBuilder` |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs` | Modificar: `runExportWorker`, helpers de export |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportWorkbookReader.cs` | Crear: lee el `.xlsx` con el SDK de OpenXML |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportTestProcessors.cs` | Crear: procesadores de mentira (éxito / falla) para probar cola y runner |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportJobQueueTests.cs` | Crear: toma, `SKIP LOCKED`, lease, conteo, purga |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportJobRunnerIntegrationTests.cs` | Crear: transacción de cierre, reintentos hasta `Failed` |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportJobWorkerTests.cs` | Crear: el hosted service toma un job solo |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportFileStorageTests.cs` | Crear |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationExportApiTests.cs` | Reescribir: `POST` → `202` → worker → correo; keyset entre lotes |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/SaleExportApiTests.cs` | Crear, con la prueba de keyset entre lotes |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj` | Sale `ClosedXML` (el workbook se lee con OpenXML) |
| `tests/Modules/Notifications/Modules.Notifications.UnitTests/QuotationsExportEmailTemplateTests.cs` | Crear |
| `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/QuotationsExportNotificationTests.cs` | Crear: los dos correos salen |

**No se tocan, a propósito:** `ExportCustomers.cs` y `ExportProducts.cs` (D15), `IObjectStorage` (hallazgo 4), `k8s/` (1 réplica y 1Gi siguen igual), la regla de lifecycle de R2, que ya existe y cubre `exports/` (hallazgo 8).

---

### Task 0: Rama y baseline

**Files:** ninguno de código. Commitea la revisión de este plan y de la spec.

**Interfaces:**
- Consumes: nada.
- Produces: `feature/export-asincrono` comprobada al día con `origin/develop`; `$env:TEMP\qep-export-asincrono-baseline-failed.txt` con las pruebas que ya fallan.

La rama ya está donde tiene que estar (ver «Estado de las ramas»): este paso lo comprueba, no la mueve.

- [ ] **Step 1: Comprobar precondiciones**

```powershell
git branch --show-current
git status --short
git fetch origin
git merge-base --is-ancestor 8b8c7a8 HEAD; if ($?) { "retiro de Reporting: en la rama" } else { "retiro de Reporting: FALTA" }
git rev-list --left-right --count origin/develop...HEAD
```

Esperado: `feature/export-asincrono`; en `git status` sólo la spec y este plan modificados (la revisión que commitea el Step 3), o nada si ya se commiteó; `retiro de Reporting: en la rama` (entró a `develop` con `1730eb3`); y el `rev-list` en `0 0` —la rama está en `66073a0`, igual que `origin/develop`— o en `0 1` si la revisión ya está commiteada. Si el primer número es mayor que 0, `develop` avanzó: con la rama sin commits propios, `git merge --ff-only origin/develop`; con commits propios, **parar y preguntar**. Si dice `FALTA` o la rama es otra, **parar y preguntar**.

- [ ] **Step 2: Baseline de la suite, por nombre**

Con Docker corriendo:

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet restore --locked-mode
dotnet build --no-restore
$baseline = Join-Path $env:TEMP "qep-export-asincrono-baseline"
Remove-Item -Recurse -Force $baseline -ErrorAction SilentlyContinue
dotnet test --no-build --logger trx --results-directory $baseline
Get-ChildItem $baseline -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -Raw $_.FullName
    $trx.TestRun.Results.UnitTestResult |
        Where-Object { $_.outcome -eq "Failed" } |
        ForEach-Object { $_.testName }
} | Sort-Object -Unique | Set-Content -Encoding utf8 (Join-Path $env:TEMP "qep-export-asincrono-baseline-failed.txt")
Get-Content (Join-Path $env:TEMP "qep-export-asincrono-baseline-failed.txt")
```

Esperado: la lista de las que ya fallan, posiblemente vacía. Pegarla en el handoff.

- [ ] **Step 3: Commitear la revisión del plan y de la spec** (Git Bash)

Sólo si `git status --short docs/` los muestra modificados; si no aparecen, la revisión ya está commiteada y este paso se saltea.

```bash
test "$(git branch --show-current)" = "feature/export-asincrono" || { echo ABORT; exit 1; }
git add docs/superpowers/specs/2026-09-12-export-asincrono-design.md \
  docs/superpowers/plans/2026-09-12-export-asincrono-backend.md
git commit -m "docs(quotations): revisar el plan de la exportación asíncrona" \
  -m "Cuatro intentos para usar las tres esperas, lectura por keyset en vez de offset con sus dos índices, la regla de lifecycle de exports/ que ya existe y las referencias al día con develop (66073a0)."
```

---

## Commit 1 — `feat(quotations): cola de exportaciones con worker y reintentos`

### Task 1: Dominio — `ExportJob` y sus transiciones

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/ExportJobKind.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/ExportJobStatus.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/ExportJob.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportJobTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces:
  - `public enum ExportJobKind { Quotations, Sales }`
  - `public enum ExportJobStatus { Pending, Processing, Completed, Failed }`
  - `public sealed class ExportJob` con `const int MaxAttempts = 4`, `const int LastErrorMaxLength = 2_000`, `const int FileNameMaxLength = 200`, `static readonly TimeSpan LeaseDuration`, `static readonly TimeSpan Retention`, `static readonly IReadOnlyList<TimeSpan> RetryDelays`; propiedades `Id, TenantId, RequestedBy, Kind, Filters, Status, Attempts, NextAttemptAt, LockedUntil, LastError, FileName, RowCount, RequestedAt, CompletedAt`, `bool HasExceededAttempts`.
  - `static ExportJob Enqueue(Guid id, Guid tenantId, Guid requestedBy, ExportJobKind kind, string filters, DateTimeOffset requestedAt)`
  - `bool IsClaimable(DateTimeOffset now)`, `void Claim(DateTimeOffset now)`, `void Complete(string fileName, int rowCount, DateTimeOffset now)`, `bool RecordTransientFailure(string error, DateTimeOffset now)` (`true` si terminó en `Failed`), `void Fail(string error, DateTimeOffset now)`.

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportJobTests.cs`:

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El estado de una exportación encolada. La toma real la hace un UPDATE en Postgres
/// (ExportJobQueue); <see cref="ExportJob.Claim"/> es el mismo cambio escrito en C#, y estas
/// pruebas fijan lo que ese UPDATE tiene que dejar.
/// </summary>
public sealed class ExportJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 15, 30, 0, TimeSpan.Zero);

    [Fact]
    public void EnqueueStartsPendingAndDueRightAway()
    {
        var job = NewJob();

        Assert.Equal(ExportJobStatus.Pending, job.Status);
        Assert.Equal(0, job.Attempts);
        Assert.Equal(Now, job.NextAttemptAt);
        Assert.Equal(Now, job.RequestedAt);
        Assert.Null(job.LockedUntil);
        Assert.True(job.IsClaimable(Now));
    }

    [Fact]
    public void ClaimTakesTheJobWithATenMinuteLeaseAndConsumesAnAttempt()
    {
        var job = NewJob();

        job.Claim(Now);

        Assert.Equal(ExportJobStatus.Processing, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.Equal(Now.AddMinutes(10), job.LockedUntil);
        Assert.False(job.IsClaimable(Now.AddMinutes(9)));
    }

    // Un Pending con la espera del backoff por delante no se toma: el reintento es a su hora.
    [Fact]
    public void AJobWaitingItsBackoffIsNotClaimable()
    {
        var job = NewJob();
        job.Claim(Now);
        job.RecordTransientFailure("IOException: timeout", Now);

        Assert.False(job.IsClaimable(Now.AddSeconds(59)));
        Assert.Throws<InvalidOperationException>(() => job.Claim(Now.AddSeconds(59)));
        Assert.True(job.IsClaimable(Now.AddMinutes(1)));
    }

    // Worker muerto a mitad (D11): el lease vence y otro lo retoma, consumiendo otro intento.
    [Fact]
    public void AnExpiredLeaseCanBeClaimedAgain()
    {
        var job = NewJob();
        job.Claim(Now);

        job.Claim(Now.AddMinutes(11));

        Assert.Equal(2, job.Attempts);
        Assert.Equal(Now.AddMinutes(21), job.LockedUntil);
    }

    [Fact]
    public void CompleteRecordsTheFileAndReleasesTheLease()
    {
        var job = NewJob();
        job.Claim(Now);

        job.Complete("cotizaciones-2026-09-12-1530.xlsx", 42, Now.AddMinutes(1));

        Assert.Equal(ExportJobStatus.Completed, job.Status);
        Assert.Equal("cotizaciones-2026-09-12-1530.xlsx", job.FileName);
        Assert.Equal(42, job.RowCount);
        Assert.Equal(Now.AddMinutes(1), job.CompletedAt);
        Assert.Null(job.LockedUntil);
    }

    [Fact]
    public void CompleteRequiresAClaimedJob()
    {
        var job = NewJob();

        Assert.Throws<InvalidOperationException>(() => job.Complete("x.xlsx", 1, Now));
    }

    // D11: 1, 5 y 15 minutos entre intentos; el cuarto fallido termina el job.
    [Fact]
    public void TransientFailuresBackOffOneFiveAndFifteenMinutesAndTheFourthFailsTheJob()
    {
        var job = NewJob();

        job.Claim(Now);
        Assert.False(job.RecordTransientFailure("IOException: r2 down", Now));
        Assert.Equal(ExportJobStatus.Pending, job.Status);
        Assert.Equal(Now.AddMinutes(1), job.NextAttemptAt);
        Assert.Null(job.LockedUntil);
        Assert.Equal("IOException: r2 down", job.LastError);

        var second = Now.AddMinutes(1);
        job.Claim(second);
        Assert.False(job.RecordTransientFailure("IOException: r2 down", second));
        Assert.Equal(second.AddMinutes(5), job.NextAttemptAt);

        var third = second.AddMinutes(5);
        job.Claim(third);
        Assert.False(job.RecordTransientFailure("IOException: r2 down", third));
        Assert.Equal(third.AddMinutes(15), job.NextAttemptAt);

        var fourth = third.AddMinutes(15);
        job.Claim(fourth);
        Assert.True(job.RecordTransientFailure("IOException: r2 down", fourth));
        Assert.Equal(ExportJobStatus.Failed, job.Status);
        Assert.Equal(fourth, job.CompletedAt);
        Assert.Equal(4, job.Attempts);
    }

    // Una espera por reintento: con una espera de más, la última nunca se usaría —el defecto de la
    // primera versión, tres esperas para tres intentos—.
    [Fact]
    public void EveryRetryDelayIsUsedBeforeTheLastAttempt()
    {
        Assert.Equal(4, ExportJob.MaxAttempts);
        Assert.Equal(ExportJob.MaxAttempts - 1, ExportJob.RetryDelays.Count);
    }

    [Fact]
    public void FailIsDefinitiveEvenOnTheFirstAttempt()
    {
        var job = NewJob();
        job.Claim(Now);

        job.Fail("ExportJobDefinitiveException: no rows", Now);

        Assert.Equal(ExportJobStatus.Failed, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.False(job.IsClaimable(Now.AddDays(1)));
    }

    // Soporte lee last_error; un stack trace entero no le sirve a nadie y la columna no crece
    // sin techo.
    [Fact]
    public void TheLastErrorIsTruncated()
    {
        var job = NewJob();
        job.Claim(Now);

        job.Fail(new string('x', ExportJob.LastErrorMaxLength + 50), Now);

        Assert.Equal(ExportJob.LastErrorMaxLength, job.LastError!.Length);
    }

    // Si el worker murió en el último intento, el lease vence y la toma suma un quinto: el job
    // ya no tiene intentos y el runner lo cierra sin procesarlo.
    [Fact]
    public void AClaimAfterTheLastAttemptIsDetected()
    {
        var job = NewJob();
        job.Claim(Now);
        job.Claim(Now.AddMinutes(11));
        job.Claim(Now.AddMinutes(22));
        job.Claim(Now.AddMinutes(33));
        Assert.False(job.HasExceededAttempts);

        job.Claim(Now.AddMinutes(44));

        Assert.True(job.HasExceededAttempts);
    }

    private static ExportJob NewJob() =>
        ExportJob.Enqueue(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            ExportJobKind.Quotations,
            "{}",
            Now);
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~ExportJobTests"
```

Esperado: no compila — `error CS0246: The type or namespace name 'ExportJob' could not be found` (y `ExportJobKind`, `ExportJobStatus`). Pegar la salida.

- [ ] **Step 3: Implementar**

`src/Modules/Quotations/Modules.Quotations.Domain/ExportJobKind.cs`:

```csharp
namespace Modules.Quotations.Domain;

/// <summary>Qué listado se exporta. Viaja como texto en la tabla y en los eventos: Notifications
/// lo usa para nombrar el tipo en el correo.</summary>
public enum ExportJobKind
{
    Quotations,
    Sales,
}
```

`src/Modules/Quotations/Modules.Quotations.Domain/ExportJobStatus.cs`:

```csharp
namespace Modules.Quotations.Domain;

public enum ExportJobStatus
{
    Pending,
    Processing,
    Completed,
    Failed,
}
```

`src/Modules/Quotations/Modules.Quotations.Domain/ExportJob.cs`:

```csharp
namespace Modules.Quotations.Domain;

/// <summary>
/// Una exportación pedida y todavía no entregada, o ya terminada (spec 2026-09-12, D2).
///
/// No es un agregado de negocio sino el estado de un trabajo en segundo plano: vive en el dominio
/// porque sus reglas —cuánto dura el lease, cuántas veces se reintenta y cuánto se espera entre
/// intentos— son las que soporte tiene que poder leer en un solo lugar.
///
/// La toma exclusiva no la hace esta clase sino un UPDATE con SKIP LOCKED (ExportJobQueue); el
/// método <see cref="Claim"/> es ese mismo cambio, para quien no tiene SQL y como especificación
/// de lo que el UPDATE tiene que dejar.
/// </summary>
public sealed class ExportJob
{
    /// <summary>
    /// D11: cuatro intentos —el primero y un reintento por cada espera de
    /// <see cref="RetryDelays"/>—; al cuarto fallido, <see cref="ExportJobStatus.Failed"/>. El
    /// archivo llega por correo y nadie mira la pantalla: unos 21 minutos de ventana que se
    /// recuperan de una caída corta de R2 o de la base cuestan menos que un correo de fallo.
    /// </summary>
    public const int MaxAttempts = 4;

    public const int LastErrorMaxLength = 2_000;

    public const int FileNameMaxLength = 200;

    /// <summary>D6: cuánto tiempo es de un worker. Si muere, otro lo retoma al vencer.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    /// <summary>D13: los terminados se borran pasado este tiempo.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>
    /// D11: la espera después del intento fallido número n es <c>RetryDelays[n - 1]</c>. Tres
    /// esperas para <see cref="MaxAttempts"/> intentos: el último fallido no espera, termina. Si
    /// cambia una de las dos cosas, cambia la otra (ExportJobTests lo fija).
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    // Para EF.
    private ExportJob()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>El sujeto que la pidió: a quien va el correo y contra quien se cuenta el límite
    /// de pendientes.</summary>
    public Guid RequestedBy { get; private set; }

    public ExportJobKind Kind { get; private set; }

    /// <summary>Los filtros ya validados, en JSON. Se guardan crudos —el NIT o el CUC como texto,
    /// no los ids que resolvían al pedir— para que el archivo refleje los datos al generarse.</summary>
    public string Filters { get; private set; } = string.Empty;

    public ExportJobStatus Status { get; private set; }

    /// <summary>Intentos consumidos. La toma lo suma antes de procesar, así que un worker que
    /// muere también gasta el suyo.</summary>
    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public string? LastError { get; private set; }

    public string? FileName { get; private set; }

    public int? RowCount { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    /// <summary>Cuándo terminó, bien o mal. La retención cuenta desde acá.</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Tomado más veces de las permitidas: el anterior murió en el último intento y el
    /// lease se venció. Se cierra sin procesar.</summary>
    public bool HasExceededAttempts => Attempts > MaxAttempts;

    public static ExportJob Enqueue(
        Guid id,
        Guid tenantId,
        Guid requestedBy,
        ExportJobKind kind,
        string filters,
        DateTimeOffset requestedAt) => new()
        {
            Id = id,
            TenantId = tenantId,
            RequestedBy = requestedBy,
            Kind = kind,
            Filters = filters,
            Status = ExportJobStatus.Pending,
            Attempts = 0,
            NextAttemptAt = requestedAt,
            RequestedAt = requestedAt,
        };

    public bool IsClaimable(DateTimeOffset now) =>
        (Status == ExportJobStatus.Pending && NextAttemptAt <= now)
        || (Status == ExportJobStatus.Processing && LockedUntil < now);

    public void Claim(DateTimeOffset now)
    {
        if (!IsClaimable(now))
        {
            throw new InvalidOperationException($"Export job '{Id}' is not claimable at {now:O}.");
        }

        Status = ExportJobStatus.Processing;
        Attempts++;
        LockedUntil = now.Add(LeaseDuration);
    }

    public void Complete(string fileName, int rowCount, DateTimeOffset now)
    {
        EnsureProcessing();
        Status = ExportJobStatus.Completed;
        FileName = fileName;
        RowCount = rowCount;
        CompletedAt = now;
        LockedUntil = null;
        LastError = null;
    }

    /// <summary>Un fallo que puede no repetirse (R2, base, timeout). Devuelve <c>true</c> si ya
    /// no quedaban intentos y el job terminó en <see cref="ExportJobStatus.Failed"/>.</summary>
    public bool RecordTransientFailure(string error, DateTimeOffset now)
    {
        EnsureProcessing();
        if (Attempts >= MaxAttempts)
        {
            Fail(error, now);
            return true;
        }

        Status = ExportJobStatus.Pending;
        NextAttemptAt = now.Add(RetryDelays[Attempts - 1]);
        LockedUntil = null;
        LastError = Truncate(error);
        return false;
    }

    /// <summary>Un fallo que no se arregla reintentando: filtros ilegibles o cero filas.</summary>
    public void Fail(string error, DateTimeOffset now)
    {
        EnsureProcessing();
        Status = ExportJobStatus.Failed;
        CompletedAt = now;
        LockedUntil = null;
        LastError = Truncate(error);
    }

    // Una transición desde otro estado es un error de programación del runner, no una entrada
    // de usuario: por eso InvalidOperationException y no un código de dominio.
    private void EnsureProcessing()
    {
        if (Status != ExportJobStatus.Processing)
        {
            throw new InvalidOperationException(
                $"Export job '{Id}' is {Status}; only a Processing job can finish.");
        }
    }

    private static string Truncate(string error) =>
        error.Length <= LastErrorMaxLength ? error : error[..LastErrorMaxLength];
}
```

- [ ] **Step 4: Correr y verificar que pasan**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~ExportJobTests"
```

Esperado: `Passed! - Failed: 0, Passed: 11`. Pegar la salida.

- [ ] **Step 5: Stage** (Git Bash; el commit sale en Task 5)

```bash
test "$(git branch --show-current)" = "feature/export-asincrono" || { echo ABORT; exit 1; }
git add src/Modules/Quotations/Modules.Quotations.Domain/ExportJobKind.cs \
  src/Modules/Quotations/Modules.Quotations.Domain/ExportJobStatus.cs \
  src/Modules/Quotations/Modules.Quotations.Domain/ExportJob.cs \
  tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportJobTests.cs
```

---

### Task 2: Mapeo EF y migración `AddExportJobs`

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` (`DbSet` junto a `Outbox` `:27`; llamada en `OnModelCreating` `:29-41`; método nuevo antes de `ConfigureOutboxProjection` `:430`; índice del keyset en `ConfigureQuotation` después de `IX_quotations_created_at` `:147` y en `ConfigureSale` después de `IX_sales_tenant_number` `:377-379`)
- Create (generados): `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<timestamp>_AddExportJobs.cs`, `<timestamp>_AddExportJobs.Designer.cs`
- Modify (generado): `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/QuotationsDbContextModelSnapshot.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs`

**Interfaces:**
- Consumes: `ExportJob` (Task 1); `Quotation`, `Sale` (existentes).
- Produces: `internal DbSet<ExportJob> QuotationsDbContext.ExportJobs`; tabla `quotations.export_jobs` con las columnas del modelo de datos de la spec; índices `IX_export_jobs_claim (status, next_attempt_at)` e `IX_export_jobs_requester (tenant_id, requested_by, status)`, los dos filtrados por `status IN ('Pending', 'Processing')`; `attempts` como token de concurrencia. Además, los índices del keyset (hallazgo 11): `IX_quotations_tenant_created_at_number (tenant_id, created_at, quotation_number)` sobre `quotations.quotations` e `IX_sales_tenant_converted_at_number (tenant_id, converted_at, sale_number)` sobre `quotations.sales`.

- [ ] **Step 1: Escribir las pruebas que fallan**

Al final de la clase `QuotationsDbContextMappingTests` (después de `BillsToFinalConsumerMapsToItsSnakeCaseColumn`, `:17`):

```csharp
    /// <summary>
    /// El SQL crudo de la toma (ExportJobQueue) nombra las columnas a mano, así que un nombre que
    /// EF pusiera por convención rompería la toma sin que el compilador lo vea. Y `attempts` es
    /// token de concurrencia: es lo que impide que un worker con el lease vencido cierre un job
    /// que ya retomó otro.
    /// </summary>
    [Fact]
    public void ExportJobMapsToItsTableWithAttemptsAsConcurrencyToken()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        var job = model.FindEntityType(typeof(ExportJob));

        Assert.NotNull(job);
        Assert.Equal("export_jobs", job.GetTableName());
        Assert.Equal("quotations", job.GetSchema());
        Assert.Equal(
            ["attempts", "completed_at", "file_name", "filters", "id", "kind", "last_error",
             "locked_until", "next_attempt_at", "requested_at", "requested_by", "row_count",
             "status", "tenant_id"],
            job.GetProperties().Select(property => property.GetColumnName()).Order(StringComparer.Ordinal));
        Assert.Equal("jsonb", job.FindProperty(nameof(ExportJob.Filters))!.GetColumnType());
        Assert.True(job.FindProperty(nameof(ExportJob.Attempts))!.IsConcurrencyToken);

        var claim = job.GetIndexes().Single(index => index.GetDatabaseName() == "IX_export_jobs_claim");
        Assert.Equal(["Status", "NextAttemptAt"], claim.Properties.Select(property => property.Name));
        Assert.Equal("status IN ('Pending', 'Processing')", claim.GetFilter());

        var requester = job.GetIndexes().Single(index => index.GetDatabaseName() == "IX_export_jobs_requester");
        Assert.Equal(
            ["TenantId", "RequestedBy", "Status"],
            requester.Properties.Select(property => property.Name));
        Assert.Equal("status IN ('Pending', 'Processing')", requester.GetFilter());
    }

    /// <summary>
    /// Las exportaciones leen por keyset (spec 2026-09-12, D8): cada lote pide lo que viene
    /// después de la última fila, en el orden del listado. Sin un índice que arranque por el
    /// tenant y siga por la clave de orden, cada lote recorre todas las filas del tenant.
    /// </summary>
    [Fact]
    public void QuotationsAndSalesHaveAnIndexForTheExportKeyset()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        var quotations = model.FindEntityType(typeof(Quotation))!.GetIndexes()
            .Single(index => index.GetDatabaseName() == "IX_quotations_tenant_created_at_number");
        Assert.Equal(
            ["TenantId", "CreatedAt", "QuotationNumber"],
            quotations.Properties.Select(property => property.Name));

        var sales = model.FindEntityType(typeof(Sale))!.GetIndexes()
            .Single(index => index.GetDatabaseName() == "IX_sales_tenant_converted_at_number");
        Assert.Equal(
            ["TenantId", "ConvertedAt", "SaleNumber"],
            sales.Properties.Select(property => property.Name));
    }
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~ExportJobMapsToItsTableWithAttemptsAsConcurrencyToken|FullyQualifiedName~QuotationsAndSalesHaveAnIndexForTheExportKeyset"
```

Esperado: las dos FAIL — la primera en `Assert.NotNull() Failure: Value is null` (el modelo todavía no conoce `ExportJob`), la segunda en `System.InvalidOperationException: Sequence contains no matching element` (no hay índice con ese nombre). Pegar la salida.

- [ ] **Step 3: Implementar el mapeo**

En `QuotationsDbContext.cs`, debajo de `Outbox` (`:27`):

```csharp
    internal DbSet<ExportJob> ExportJobs => Set<ExportJob>();
```

En `OnModelCreating`, antes de `ConfigureOutboxProjection(modelBuilder);`:

```csharp
        ConfigureExportJob(modelBuilder);
```

Antes de `ConfigureOutboxProjection` (`:430`):

```csharp
    // Los dos índices sólo miran los jobs vivos: la toma y el límite de pendientes nunca buscan
    // uno terminado, y los terminados se acumulan hasta la purga de 30 días.
    private const string ActiveExportJobFilter = "status IN ('Pending', 'Processing')";

    /// <summary>
    /// La cola de exportaciones (spec 2026-09-12, D2). Los nombres de columna van a mano y no por
    /// convención porque ExportJobQueue los escribe en SQL crudo para la toma con SKIP LOCKED.
    /// </summary>
    private static void ConfigureExportJob(ModelBuilder modelBuilder)
    {
        var job = modelBuilder.Entity<ExportJob>();
        job.ToTable("export_jobs", "quotations");
        job.HasKey(value => value.Id);
        job.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        job.Property(value => value.TenantId).HasColumnName("tenant_id");
        job.Property(value => value.RequestedBy).HasColumnName("requested_by");
        // Texto y no entero, mismo criterio que Quotation.Status: soporte lee la tabla a mano.
        job.Property(value => value.Kind)
            .HasColumnName("kind")
            .HasConversion<string>()
            .HasMaxLength(20);
        job.Property(value => value.Filters).HasColumnName("filters").HasColumnType("jsonb");
        job.Property(value => value.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20);
        // Token de concurrencia: la toma lo incrementa, así que un worker cuyo lease venció y otro
        // retomó no puede cerrar el job con los intentos viejos (el UPDATE no encuentra la fila).
        job.Property(value => value.Attempts).HasColumnName("attempts").IsConcurrencyToken();
        job.Property(value => value.NextAttemptAt).HasColumnName("next_attempt_at");
        job.Property(value => value.LockedUntil).HasColumnName("locked_until");
        job.Property(value => value.LastError).HasColumnName("last_error").HasColumnType("text");
        job.Property(value => value.FileName)
            .HasColumnName("file_name")
            .HasMaxLength(ExportJob.FileNameMaxLength);
        job.Property(value => value.RowCount).HasColumnName("row_count");
        job.Property(value => value.RequestedAt).HasColumnName("requested_at");
        job.Property(value => value.CompletedAt).HasColumnName("completed_at");

        job.HasIndex(value => new { value.Status, value.NextAttemptAt })
            .HasDatabaseName("IX_export_jobs_claim")
            .HasFilter(ActiveExportJobFilter);
        job.HasIndex(value => new { value.TenantId, value.RequestedBy, value.Status })
            .HasDatabaseName("IX_export_jobs_requester")
            .HasFilter(ActiveExportJobFilter);
    }
```

En `ConfigureQuotation`, después de `IX_quotations_created_at` (`:147`):

```csharp
        // El keyset de la exportación (spec 2026-09-12, D8): tenant, fecha de alta y número
        // —único por tenant, el desempate—. Postgres recorre el btree en los dos sentidos, así que
        // sirve al ORDER BY descendente sin declararlo.
        quotation.HasIndex(value => new { value.TenantId, value.CreatedAt, value.QuotationNumber })
            .HasDatabaseName("IX_quotations_tenant_created_at_number");
```

En `ConfigureSale`, después de `IX_sales_tenant_number` (`:377-379`):

```csharp
        // El keyset de la exportación de ventas (spec 2026-09-12, D8): el orden exacto del listado
        // —fecha de conversión y número como desempate, SaleRepository.SearchAsync— detrás del
        // tenant.
        sale.HasIndex(value => new { value.TenantId, value.ConvertedAt, value.SaleNumber })
            .HasDatabaseName("IX_sales_tenant_converted_at_number");
```

- [ ] **Step 4: Correr las pruebas y generar la migración**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~ExportJobMapsToItsTableWithAttemptsAsConcurrencyToken|FullyQualifiedName~QuotationsAndSalesHaveAnIndexForTheExportKeyset"
dotnet ef migrations add AddExportJobs --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations
git diff --stat
```

Esperado: las dos pruebas `Passed`. La migración crea `quotations.export_jobs` con las 14 columnas (`filters jsonb`, `last_error text`, `file_name character varying(200)`, `kind`/`status character varying(20)`, `locked_until`/`completed_at` nulos, `row_count integer` nulo) y sus dos índices con `filter: "status IN ('Pending', 'Processing')"`; crea además `IX_quotations_tenant_created_at_number` sobre `quotations.quotations` e `IX_sales_tenant_converted_at_number` sobre `quotations.sales` (hallazgo 11), y **nada más**: si el `Up` toca otra cosa, el snapshot estaba desfasado — parar y preguntar. Después, que la migración aplica de verdad (el arranque migra):

```powershell
dotnet build --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~SaleListApiTests.ListReturnsTheSaleWithItsClientAdvisorAndTotalsResolved"
```

Esperado: `Passed`. Pegar las tres salidas.

- [ ] **Step 5: Stage** (Git Bash)

```bash
test "$(git branch --show-current)" = "feature/export-asincrono" || { echo ABORT; exit 1; }
git add src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs \
  src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/*_AddExportJobs.cs \
  src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/*_AddExportJobs.Designer.cs \
  src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/QuotationsDbContextModelSnapshot.cs \
  tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs
```

---

### Task 3: Application — puertos de la cola y `ExportJobRunner`

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/ExportJobLimits.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Application/IExportJobQueue.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Application/IExportJobProcessor.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Application/IExportEventPublisher.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Application/ExportJobRunner.cs`
- Create: `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportTestDoubles.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportJobRunnerTests.cs`

**Interfaces:**
- Consumes: `ExportJob`, `ExportJobKind`, `ExportJobStatus` (Task 1); `IQuotationAuditPublisher.Publish(Guid tenantId, Guid actorId, string action, string resourceId, string outcome, DateTimeOffset occurredAt)`, `IQuotationsUnitOfWork.SaveChangesAsync(CancellationToken)`, `IClock.UtcNow`, `RequestConcurrencyException` (existentes).
- Produces:
  - `public static class ExportJobLimits { const int PendingPerRequester = 3; const int BatchSize = 1_000; }`
  - `public interface IExportJobQueue { void Add(ExportJob job); Task<int> CountPendingAsync(Guid tenantId, Guid requestedBy, CancellationToken cancellationToken); Task<ExportJob?> ClaimNextAsync(DateTimeOffset now, CancellationToken cancellationToken); Task<int> PurgeFinishedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken); }`
  - `public interface IExportJobProcessor { ExportJobKind Kind { get; } Task<ExportJobResult> ProcessAsync(ExportJob job, CancellationToken cancellationToken); }`
  - `public sealed record ExportJobResult(string FileName, int RowCount, string DownloadUrl, DateTimeOffset ExpiresAt)`
  - `public sealed class ExportJobDefinitiveException(string message, Exception? innerException = null) : Exception`
  - `public interface IExportEventPublisher { void PublishReady(ExportJob job, ExportJobResult result, DateTimeOffset occurredAt); void PublishFailed(ExportJob job, DateTimeOffset occurredAt); }`
  - `public enum ExportJobRunOutcome { NoJob, Completed, RetryScheduled, Failed, LeaseLost }`
  - `public sealed class ExportJobRunner` con `Task<ExportJobRunOutcome> RunNextAsync(CancellationToken)` y `Task<int> PurgeFinishedAsync(CancellationToken)`.
  - Acciones de auditoría: `quotation.quotation.exported` (kind `Quotations`) y `quotation.sale.exported` (kind `Sales`), recurso = `jobId`, resultado `success:{rowCount}` — mismo patrón que `quotation.sale.approved` (`ApproveSale.cs:50`) y `customers.customer.exported` (`ExportCustomers.cs:95-97`).
  - Dobles de prueba (`ExportTestDoubles.cs`): `InMemoryExportJobQueue`, `CountingQuotationsUnitOfWork`, `RecordingExportEventPublisher`, `RecordingExportAuditPublisher`, `StubExportJobProcessor`, `MutableClock`. Tasks 10–13 los reusan.

- [ ] **Step 1: Escribir los dobles y las pruebas que fallan**

`tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportTestDoubles.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

// Dobles de la exportación asíncrona. Como el resto del repositorio, a mano y sin librería de
// mocking: registran lo que reciben para que la prueba afirme qué salió hacia cada puerto.

/// <summary>La cola sin Postgres: toma con <see cref="ExportJob.Claim"/>, que es la misma
/// transición que el UPDATE con SKIP LOCKED. La exclusión real la cubren las pruebas de
/// integración de ExportJobQueue.</summary>
internal sealed class InMemoryExportJobQueue : IExportJobQueue
{
    public List<ExportJob> Jobs { get; } = [];

    public DateTimeOffset? LastPurgeCutoff { get; private set; }

    public void Add(ExportJob job) => Jobs.Add(job);

    public Task<int> CountPendingAsync(
        Guid tenantId, Guid requestedBy, CancellationToken cancellationToken) =>
        Task.FromResult(Jobs.Count(job =>
            job.TenantId == tenantId
            && job.RequestedBy == requestedBy
            && job.Status is ExportJobStatus.Pending or ExportJobStatus.Processing));

    public Task<ExportJob?> ClaimNextAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var job = Jobs
            .Where(candidate => candidate.IsClaimable(now))
            .OrderBy(candidate => candidate.NextAttemptAt)
            .FirstOrDefault();
        job?.Claim(now);
        return Task.FromResult(job);
    }

    public Task<int> PurgeFinishedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        LastPurgeCutoff = cutoff;
        return Task.FromResult(0);
    }
}

/// <summary>Cuenta los guardados: "terminar es una sola transacción" (D10) se afirma como
/// exactamente un guardado. <see cref="Failure"/> simula que el guardado explota.</summary>
internal sealed class CountingQuotationsUnitOfWork : IQuotationsUnitOfWork
{
    public int Saves { get; private set; }

    public Exception? Failure { get; set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            return Task.FromException<int>(Failure);
        }

        Saves++;
        return Task.FromResult(1);
    }
}

internal sealed class RecordingExportEventPublisher : IExportEventPublisher
{
    public List<(ExportJob Job, ExportJobResult Result)> Ready { get; } = [];

    public List<ExportJob> Failed { get; } = [];

    public void PublishReady(ExportJob job, ExportJobResult result, DateTimeOffset occurredAt) =>
        Ready.Add((job, result));

    public void PublishFailed(ExportJob job, DateTimeOffset occurredAt) => Failed.Add(job);
}

internal sealed record RecordedAuditEntry(
    Guid TenantId, Guid ActorId, string Action, string ResourceId, string Outcome);

internal sealed class RecordingExportAuditPublisher : IQuotationAuditPublisher
{
    public List<RecordedAuditEntry> Entries { get; } = [];

    public void Publish(
        Guid tenantId, Guid actorId, string action, string resourceId,
        string outcome, DateTimeOffset occurredAt) =>
        Entries.Add(new RecordedAuditEntry(tenantId, actorId, action, resourceId, outcome));
}

internal sealed class StubExportJobProcessor(
    ExportJobKind kind, Func<ExportJob, ExportJobResult> process) : IExportJobProcessor
{
    public static readonly DateTimeOffset LinkExpiresAt = new(2026, 9, 13, 15, 30, 0, TimeSpan.Zero);

    public int Calls { get; private set; }

    public ExportJobKind Kind { get; } = kind;

    public Task<ExportJobResult> ProcessAsync(ExportJob job, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(process(job));
    }

    public static StubExportJobProcessor Succeeding(ExportJobKind kind, int rowCount = 3) =>
        new(kind, _ => new ExportJobResult(
            "cotizaciones-2026-09-12-1530.xlsx", rowCount, "https://r2.test/exports/x.xlsx", LinkExpiresAt));

    public static StubExportJobProcessor Throwing(ExportJobKind kind, Exception failure) =>
        new(kind, _ => throw failure);
}

/// <summary>Un reloj que la prueba adelanta: el backoff se afirma en minutos exactos.</summary>
internal sealed class MutableClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}
```

`tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportJobRunnerTests.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Un tick del worker de exportaciones: toma un job, lo despacha por kind y lo cierra. Lo que
/// verifican estas pruebas es la clasificación de fallos (D11) y que terminar sea un solo
/// guardado con estado, evento y auditoría (D10).
/// </summary>
public sealed class ExportJobRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 15, 30, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid RequesterId = Guid.CreateVersion7();

    [Fact]
    public async Task WithNothingDueItReportsNoJob()
    {
        var harness = new Harness();

        var outcome = await harness.Runner().RunNextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ExportJobRunOutcome.NoJob, outcome);
        Assert.Equal(0, harness.UnitOfWork.Saves);
    }

    [Fact]
    public async Task ACompletedJobPublishesTheReadyEventAndTheAuditInOneSave()
    {
        var harness = new Harness();
        var job = harness.Enqueue();
        var processor = StubExportJobProcessor.Succeeding(ExportJobKind.Quotations, rowCount: 42);

        var outcome = await harness.Runner(processor).RunNextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ExportJobRunOutcome.Completed, outcome);
        Assert.Equal(ExportJobStatus.Completed, job.Status);
        Assert.Equal(42, job.RowCount);
        Assert.Equal("cotizaciones-2026-09-12-1530.xlsx", job.FileName);
        var (readyJob, result) = Assert.Single(harness.Events.Ready);
        Assert.Same(job, readyJob);
        Assert.Equal("https://r2.test/exports/x.xlsx", result.DownloadUrl);
        Assert.Empty(harness.Events.Failed);
        Assert.Equal(
            new RecordedAuditEntry(
                TenantId, RequesterId, "quotation.quotation.exported", job.Id.ToString(), "success:42"),
            Assert.Single(harness.Audit.Entries));
        Assert.Equal(1, harness.UnitOfWork.Saves);
    }

    [Fact]
    public async Task ASalesJobIsAuditedAsASaleExport()
    {
        var harness = new Harness();
        harness.Enqueue(ExportJobKind.Sales);

        await harness.Runner(StubExportJobProcessor.Succeeding(ExportJobKind.Sales))
            .RunNextAsync(TestContext.Current.CancellationToken);

        Assert.Equal("quotation.sale.exported", Assert.Single(harness.Audit.Entries).Action);
    }

    // R2 caído, la base, un timeout: puede no repetirse, así que vuelve a la cola sin correo.
    [Fact]
    public async Task ATransientFailureSchedulesARetryWithoutAnEvent()
    {
        var harness = new Harness();
        var job = harness.Enqueue();
        var processor = StubExportJobProcessor.Throwing(
            ExportJobKind.Quotations, new IOException("r2 unavailable"));

        var outcome = await harness.Runner(processor).RunNextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ExportJobRunOutcome.RetryScheduled, outcome);
        Assert.Equal(ExportJobStatus.Pending, job.Status);
        Assert.Equal(Now.AddMinutes(1), job.NextAttemptAt);
        Assert.Equal("IOException: r2 unavailable", job.LastError);
        Assert.Empty(harness.Events.Failed);
        Assert.Empty(harness.Audit.Entries);
        Assert.Equal(1, harness.UnitOfWork.Saves);
    }

    // D11: esperas de 1, 5 y 15 minutos, sin correo mientras quede un intento; el cuarto fallido
    // cierra el job y recién ahí sale el evento.
    [Fact]
    public async Task TheFourthTransientFailureFailsTheJobAndPublishesTheFailedEvent()
    {
        var harness = new Harness();
        var job = harness.Enqueue();
        var runner = harness.Runner(StubExportJobProcessor.Throwing(
            ExportJobKind.Quotations, new IOException("r2 unavailable")));
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.Equal(ExportJobRunOutcome.RetryScheduled, await runner.RunNextAsync(cancellationToken));
        Assert.Equal(Now.AddMinutes(1), job.NextAttemptAt);
        harness.Clock.UtcNow = Now.AddMinutes(1);
        Assert.Equal(ExportJobRunOutcome.RetryScheduled, await runner.RunNextAsync(cancellationToken));
        Assert.Equal(Now.AddMinutes(6), job.NextAttemptAt);
        harness.Clock.UtcNow = Now.AddMinutes(6);
        Assert.Equal(ExportJobRunOutcome.RetryScheduled, await runner.RunNextAsync(cancellationToken));
        Assert.Equal(Now.AddMinutes(21), job.NextAttemptAt);
        Assert.Empty(harness.Events.Failed);
        harness.Clock.UtcNow = Now.AddMinutes(21);
        var outcome = await runner.RunNextAsync(cancellationToken);

        Assert.Equal(ExportJobRunOutcome.Failed, outcome);
        Assert.Equal(ExportJobStatus.Failed, job.Status);
        Assert.Equal(4, job.Attempts);
        Assert.Same(job, Assert.Single(harness.Events.Failed));
    }

    // Filtros ilegibles o cero filas: reintentar da lo mismo, así que Failed al primer intento.
    [Fact]
    public async Task ADefinitiveFailureFailsOnTheFirstAttempt()
    {
        var harness = new Harness();
        var job = harness.Enqueue();
        var processor = StubExportJobProcessor.Throwing(
            ExportJobKind.Quotations, new ExportJobDefinitiveException("no rows"));

        var outcome = await harness.Runner(processor).RunNextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ExportJobRunOutcome.Failed, outcome);
        Assert.Equal(1, job.Attempts);
        Assert.Equal("ExportJobDefinitiveException: no rows", job.LastError);
        Assert.Same(job, Assert.Single(harness.Events.Failed));
    }

    [Fact]
    public async Task AKindWithoutProcessorFailsDefinitively()
    {
        var harness = new Harness();
        var job = harness.Enqueue(ExportJobKind.Sales);

        var outcome = await harness.Runner(StubExportJobProcessor.Succeeding(ExportJobKind.Quotations))
            .RunNextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ExportJobRunOutcome.Failed, outcome);
        Assert.StartsWith("NoProcessor:", job.LastError, StringComparison.Ordinal);
    }

    // El worker murió en el cuarto intento y el lease venció: la toma suma un quinto y el job se
    // cierra sin procesar, o un job colgado consumiría intentos para siempre.
    [Fact]
    public async Task AJobReclaimedPastItsLastAttemptFailsWithoutProcessing()
    {
        var harness = new Harness();
        var job = harness.Enqueue();
        job.Claim(Now);
        job.Claim(Now.AddMinutes(11));
        job.Claim(Now.AddMinutes(22));
        job.Claim(Now.AddMinutes(33));
        harness.Clock.UtcNow = Now.AddMinutes(44);
        var processor = StubExportJobProcessor.Succeeding(ExportJobKind.Quotations);

        var outcome = await harness.Runner(processor).RunNextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ExportJobRunOutcome.Failed, outcome);
        Assert.Equal(0, processor.Calls);
        Assert.StartsWith("LeaseExpired:", job.LastError, StringComparison.Ordinal);
        Assert.Same(job, Assert.Single(harness.Events.Failed));
    }

    // Otro worker retomó el job mientras éste terminaba: el UPDATE con el token de concurrencia
    // no encuentra la fila, y lo correcto es soltarlo sin tirar el tick.
    [Fact]
    public async Task ALostLeaseOnCompletionIsReportedAndNotThrown()
    {
        var harness = new Harness();
        harness.Enqueue();
        harness.UnitOfWork.Failure = new RequestConcurrencyException("concurrency.conflict", "lease lost");

        var outcome = await harness.Runner(StubExportJobProcessor.Succeeding(ExportJobKind.Quotations))
            .RunNextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ExportJobRunOutcome.LeaseLost, outcome);
    }

    // Apagado del proceso: no es un fallo del job. Queda en Processing y el lease lo devuelve.
    [Fact]
    public async Task CancellationDuringProcessingLeavesTheJobClaimed()
    {
        var harness = new Harness();
        var job = harness.Enqueue();
        using var shutdown = new CancellationTokenSource();
        await shutdown.CancelAsync();
        var processor = StubExportJobProcessor.Throwing(
            ExportJobKind.Quotations, new OperationCanceledException(shutdown.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Runner(processor).RunNextAsync(shutdown.Token));

        Assert.Equal(ExportJobStatus.Processing, job.Status);
        Assert.Equal(0, harness.UnitOfWork.Saves);
    }

    [Fact]
    public async Task PurgeRemovesWhatFinishedMoreThanThirtyDaysAgo()
    {
        var harness = new Harness();

        await harness.Runner().PurgeFinishedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Now.AddDays(-30), harness.Queue.LastPurgeCutoff);
    }

    private sealed class Harness
    {
        public InMemoryExportJobQueue Queue { get; } = new();

        public RecordingExportEventPublisher Events { get; } = new();

        public RecordingExportAuditPublisher Audit { get; } = new();

        public CountingQuotationsUnitOfWork UnitOfWork { get; } = new();

        public MutableClock Clock { get; } = new(Now);

        public ExportJob Enqueue(ExportJobKind kind = ExportJobKind.Quotations)
        {
            var job = ExportJob.Enqueue(Guid.CreateVersion7(), TenantId, RequesterId, kind, "{}", Now);
            Queue.Add(job);
            return job;
        }

        public ExportJobRunner Runner(params IExportJobProcessor[] processors) =>
            new(Queue, processors, Events, Audit, UnitOfWork, Clock);
    }
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~ExportJobRunnerTests"
```

Esperado: no compila — `error CS0246: The type or namespace name 'IExportJobQueue' could not be found` (y `ExportJobRunner`, `IExportJobProcessor`, `ExportJobResult`, `IExportEventPublisher`, `ExportJobDefinitiveException`, `ExportJobRunOutcome`). Pegar la salida.

- [ ] **Step 3: Implementar**

`src/Modules/Quotations/Modules.Quotations.Application/ExportJobLimits.cs`:

```csharp
namespace Modules.Quotations.Application;

public static class ExportJobLimits
{
    /// <summary>D4: exportaciones Pending + Processing por persona en un tenant, contando
    /// cotizaciones y ventas juntas. Frena el doble clic y el abuso; no es un invariante duro
    /// —dos pedidos simultáneos pueden pasar los dos—, así que no se paga un bloqueo por él.</summary>
    public const int PendingPerRequester = 3;

    /// <summary>D8: filas por consulta al armar el Excel. No es un tope: es el lote con el que se
    /// recorre para que la memoria quede acotada a mil filas y no al año entero.</summary>
    public const int BatchSize = 1_000;
}
```

`src/Modules/Quotations/Modules.Quotations.Application/IExportJobQueue.cs`:

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// La cola de exportaciones (D2). Puerto y no la tabla directa para que pasar a un broker, si
/// algún día hace falta, sea cambiar el adaptador y no los casos de uso.
/// </summary>
public interface IExportJobQueue
{
    /// <summary>Lo suma a la unidad de trabajo; se persiste con
    /// <see cref="IQuotationsUnitOfWork.SaveChangesAsync"/>.</summary>
    void Add(ExportJob job);

    /// <summary>Los Pending y Processing de esa persona en ese tenant, de los dos tipos.</summary>
    Task<int> CountPendingAsync(Guid tenantId, Guid requestedBy, CancellationToken cancellationToken);

    /// <summary>
    /// Toma exclusiva (D6): un Pending vencido o un Processing con el lease vencido, con un
    /// intento más y lease nuevo. Commitea por su cuenta —el lease tiene que verse desde otros
    /// procesos ya— y deja el job trackeado para que cerrarlo sea un guardado de la unidad de
    /// trabajo. <c>null</c> si no hay nada que tomar.
    /// </summary>
    Task<ExportJob?> ClaimNextAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>D13: borra los Completed y Failed que terminaron antes de <paramref name="cutoff"/>.</summary>
    Task<int> PurgeFinishedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
```

`src/Modules/Quotations/Modules.Quotations.Application/IExportJobProcessor.cs`:

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Arma y sube el archivo de un tipo de exportación (D7). Uno por <see cref="ExportJobKind"/>;
/// el runner despacha por <see cref="Kind"/>.
///
/// Contrato de fallos (D11): <see cref="ExportJobDefinitiveException"/> para lo que reintentar
/// no arregla; cualquier otra excepción es transitoria y vuelve a la cola con backoff.
/// </summary>
public interface IExportJobProcessor
{
    ExportJobKind Kind { get; }

    Task<ExportJobResult> ProcessAsync(ExportJob job, CancellationToken cancellationToken);
}

/// <summary>Lo que el correo necesita decir: archivo, filas, enlace y hasta cuándo sirve.</summary>
public sealed record ExportJobResult(
    string FileName,
    int RowCount,
    string DownloadUrl,
    DateTimeOffset ExpiresAt);

/// <summary>Un fallo que no se arregla reintentando: filtros ilegibles, cero filas al procesar.</summary>
public sealed class ExportJobDefinitiveException(string message, Exception? innerException = null)
    : Exception(message, innerException);
```

`src/Modules/Quotations/Modules.Quotations.Application/IExportEventPublisher.cs`:

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Encola los avisos para Notifications. Por outbox y en la unidad de trabajo del módulo, igual
/// que <c>ICustomerExportEventPublisher</c>: el evento commitea en la misma transacción que el
/// cambio de estado del job (D10), así que nunca sale un correo de un job que no terminó.
/// </summary>
public interface IExportEventPublisher
{
    /// <summary><c>quotations.export-ready.v1</c>.</summary>
    void PublishReady(ExportJob job, ExportJobResult result, DateTimeOffset occurredAt);

    /// <summary><c>quotations.export-failed.v1</c>.</summary>
    void PublishFailed(ExportJob job, DateTimeOffset occurredAt);
}
```

`src/Modules/Quotations/Modules.Quotations.Application/ExportJobRunner.cs`:

```csharp
using System.Globalization;
using BuildingBlocks.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

public enum ExportJobRunOutcome
{
    NoJob,
    Completed,
    RetryScheduled,
    Failed,
    LeaseLost,
}

/// <summary>
/// Un tick del worker de exportaciones: toma un job, lo despacha a su procesador y lo cierra.
/// Vive en Application y no en el worker para poder probar la clasificación de fallos sin
/// Postgres ni temporizadores; el worker sólo lo llama en un scope nuevo por job.
/// </summary>
public sealed class ExportJobRunner(
    IExportJobQueue queue,
    IEnumerable<IExportJobProcessor> processors,
    IExportEventPublisher eventPublisher,
    IQuotationAuditPublisher auditPublisher,
    IQuotationsUnitOfWork unitOfWork,
    IClock clock)
{
    // Un diccionario armado al construir: dos procesadores del mismo kind son un error de
    // cableado, y así explota el tick (que lo loguea) en vez de elegir uno en silencio.
    private readonly Dictionary<ExportJobKind, IExportJobProcessor> _processors =
        processors.ToDictionary(processor => processor.Kind);

    public async Task<ExportJobRunOutcome> RunNextAsync(CancellationToken cancellationToken)
    {
        var job = await queue.ClaimNextAsync(clock.UtcNow, cancellationToken);
        if (job is null)
        {
            return ExportJobRunOutcome.NoJob;
        }

        if (job.HasExceededAttempts)
        {
            return await FailAsync(
                job, "LeaseExpired: the worker stopped during the last attempt.", cancellationToken);
        }

        if (!_processors.TryGetValue(job.Kind, out var processor))
        {
            return await FailAsync(
                job, $"NoProcessor: no export processor is registered for '{job.Kind}'.", cancellationToken);
        }

        ExportJobResult result;
        try
        {
            result = await processor.ProcessAsync(job, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Apagado del proceso, no fallo del job: queda en Processing y el lease lo devuelve.
            throw;
        }
        catch (ExportJobDefinitiveException exception)
        {
            return await FailAsync(job, Describe(exception), cancellationToken);
        }
        catch (Exception exception)
        {
            var now = clock.UtcNow;
            var failed = job.RecordTransientFailure(Describe(exception), now);
            if (failed)
            {
                eventPublisher.PublishFailed(job, now);
            }

            return await SaveAsync(
                failed ? ExportJobRunOutcome.Failed : ExportJobRunOutcome.RetryScheduled,
                cancellationToken);
        }

        // D10: estado, evento y auditoría en un solo guardado. Si este guardado falla por otra
        // cosa que el lease, la excepción sube al worker: el scope se descarta con lo que
        // tenía trackeado —nada de reusarlo para registrar un reintento con el evento de
        // "listo" adentro— y el lease vencido devuelve el job a la cola.
        var finishedAt = clock.UtcNow;
        job.Complete(result.FileName, result.RowCount, finishedAt);
        eventPublisher.PublishReady(job, result, finishedAt);
        auditPublisher.Publish(
            job.TenantId,
            job.RequestedBy,
            AuditActionFor(job.Kind),
            job.Id.ToString("D", CultureInfo.InvariantCulture),
            $"success:{result.RowCount}",
            finishedAt);
        return await SaveAsync(ExportJobRunOutcome.Completed, cancellationToken);
    }

    /// <summary>D13: lo terminado hace más de <see cref="ExportJob.Retention"/>.</summary>
    public Task<int> PurgeFinishedAsync(CancellationToken cancellationToken) =>
        queue.PurgeFinishedBeforeAsync(clock.UtcNow - ExportJob.Retention, cancellationToken);

    private async Task<ExportJobRunOutcome> FailAsync(
        ExportJob job, string error, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        job.Fail(error, now);
        eventPublisher.PublishFailed(job, now);
        return await SaveAsync(ExportJobRunOutcome.Failed, cancellationToken);
    }

    // `attempts` es token de concurrencia: si otro worker retomó el job con el lease vencido, el
    // UPDATE no encuentra la fila y QuotationsUnitOfWork lo traduce a RequestConcurrencyException.
    // El job es del otro; éste no manda nada.
    private async Task<ExportJobRunOutcome> SaveAsync(
        ExportJobRunOutcome outcome, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return outcome;
        }
        catch (RequestConcurrencyException)
        {
            return ExportJobRunOutcome.LeaseLost;
        }
    }

    private static string AuditActionFor(ExportJobKind kind) => kind switch
    {
        ExportJobKind.Sales => "quotation.sale.exported",
        _ => "quotation.quotation.exported",
    };

    // Sólo tipo y mensaje (D11): last_error lo lee soporte, y un stack trace no le suma nada.
    private static string Describe(Exception exception) =>
        $"{exception.GetType().Name}: {exception.Message}";
}
```

- [ ] **Step 4: Correr y verificar que pasan**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~ExportJobRunnerTests|FullyQualifiedName~ExportJobTests"
```

Esperado: `Passed! - Failed: 0, Passed: 22`. Pegar la salida.

- [ ] **Step 5: Stage** (Git Bash)

```bash
test "$(git branch --show-current)" = "feature/export-asincrono" || { echo ABORT; exit 1; }
git add src/Modules/Quotations/Modules.Quotations.Application/ExportJobLimits.cs \
  src/Modules/Quotations/Modules.Quotations.Application/IExportJobQueue.cs \
  src/Modules/Quotations/Modules.Quotations.Application/IExportJobProcessor.cs \
  src/Modules/Quotations/Modules.Quotations.Application/IExportEventPublisher.cs \
  src/Modules/Quotations/Modules.Quotations.Application/ExportJobRunner.cs \
  tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportTestDoubles.cs \
  tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportJobRunnerTests.cs
```

---

### Task 4: Infrastructure — cola con `SKIP LOCKED`, eventos y cableado

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/ExportJobQueue.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/ExportJobEventPublisher.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs:38` (después de `IQuotationAuditPublisher`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:436` (después de `IQuotationFileLookup`)
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs` (helpers de export, antes de `private sealed record RegisterTenantResponseDto` `:491`)
- Create: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportTestProcessors.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportJobQueueTests.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportJobRunnerIntegrationTests.cs`

**Interfaces:**
- Consumes: `IExportJobQueue`, `IExportEventPublisher`, `ExportJobRunner`, `ExportJobResult`, `IExportJobProcessor` (Task 3); `QuotationsDbContext.ExportJobs` (Task 2); `QuotationsDbContext.Outbox`, `QuotationsOutboxMessage` (existentes).
- Produces:
  - `internal sealed class ExportJobQueue(QuotationsDbContext) : IExportJobQueue`
  - `internal sealed class ExportJobEventPublisher(QuotationsDbContext) : IExportEventPublisher` con `internal const string ReadyEventName = "quotations.export-ready.v1"` y `internal const string FailedEventName = "quotations.export-failed.v1"`; `CorrelationId` = `jobId`.
  - Helpers de `QuotationsApiHarness` (todos sobre `WebApplicationFactory<Program>`, así sirven para `QepApiFactory` y para lo que devuelve `WithWebHostBuilder`): `WithExportProcessors(this WebApplicationFactory<Program>, params IExportJobProcessor[])`, `EnqueueExportJobAsync(factory, Guid tenantId, Guid requestedBy, ExportJobKind kind = Quotations, string filters = "{}")` → `Task<Guid>`, `RunExportJobAsync(factory)` → `Task<ExportJobRunOutcome>`, `FindExportJobAsync(factory, Guid jobId)` → `Task<ExportJob>`, `MakeExportJobDueAsync(factory, Guid jobId)`, `ExpireExportLeaseAsync(factory, Guid jobId)`, `OutboxMessagesAsync(factory, string eventName)` → `Task<IReadOnlyList<QuotationsOutboxMessage>>`.
  - `SucceedingExportProcessor(ExportJobKind kind)` y `FailingExportProcessor(ExportJobKind kind, Exception failure)` para pruebas de integración.

- [ ] **Step 1: Escribir los helpers y las pruebas que fallan**

`tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportTestProcessors.cs`:

```csharp
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.IntegrationTests;

// Procesadores de mentira para probar la cola y el runner contra Postgres sin armar un Excel:
// lo que se verifica acá es la toma, el cierre y los eventos. El Excel real lo cubren
// QuotationExportApiTests y SaleExportApiTests.

internal sealed class SucceedingExportProcessor(ExportJobKind kind) : IExportJobProcessor
{
    public ExportJobKind Kind { get; } = kind;

    public Task<ExportJobResult> ProcessAsync(ExportJob job, CancellationToken cancellationToken) =>
        Task.FromResult(new ExportJobResult(
            "cotizaciones-2026-09-12-1530.xlsx",
            3,
            $"https://r2.test/exports/tenants/{job.TenantId:N}/jobs/{job.Id:N}.xlsx",
            DateTimeOffset.UtcNow.AddHours(24)));
}

internal sealed class FailingExportProcessor(ExportJobKind kind, Exception failure) : IExportJobProcessor
{
    public ExportJobKind Kind { get; } = kind;

    public Task<ExportJobResult> ProcessAsync(ExportJob job, CancellationToken cancellationToken) =>
        Task.FromException<ExportJobResult>(failure);
}
```

En `QuotationsApiHarness.cs`, agregar los `using` que falten arriba:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Infrastructure.Persistence;
```

y los helpers antes de `private sealed record RegisterTenantResponseDto` (`:491`):

```csharp
    /// <summary>Reemplaza los procesadores de exportación por los de la prueba. Los reales se
    /// sacan primero: dos del mismo kind hacen explotar al runner al construirse.</summary>
    public static WebApplicationFactory<Program> WithExportProcessors(
        this WebApplicationFactory<Program> factory, params IExportJobProcessor[] processors) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IExportJobProcessor>();
            foreach (var processor in processors)
            {
                services.AddSingleton(processor);
            }
        }));

    public static async Task<Guid> EnqueueExportJobAsync(
        WebApplicationFactory<Program> factory,
        Guid tenantId,
        Guid requestedBy,
        ExportJobKind kind = ExportJobKind.Quotations,
        string filters = "{}")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var job = ExportJob.Enqueue(
            Guid.CreateVersion7(), tenantId, requestedBy, kind, filters, DateTimeOffset.UtcNow);
        scope.ServiceProvider.GetRequiredService<IExportJobQueue>().Add(job);
        await scope.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
        return job.Id;
    }

    /// <summary>Un tick del worker, a mano: el hosted service no corre en las pruebas (ver
    /// <see cref="QepApiFactory"/>), así que el orden de los ticks lo decide la prueba.</summary>
    public static async Task<ExportJobRunOutcome> RunExportJobAsync(WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ExportJobRunner>()
            .RunNextAsync(TestContext.Current.CancellationToken);
    }

    public static async Task<ExportJob> FindExportJobAsync(
        WebApplicationFactory<Program> factory, Guid jobId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        return await dbContext.ExportJobs
            .AsNoTracking()
            .SingleAsync(job => job.Id == jobId, TestContext.Current.CancellationToken);
    }

    /// <summary>Mueve el próximo intento al pasado directo en la base: el backoff es de minutos y
    /// el reloj del host no se corre por prueba. Mismo criterio que BackdateAsync.</summary>
    public static Task MakeExportJobDueAsync(WebApplicationFactory<Program> factory, Guid jobId) =>
        UpdateExportJobAsync(factory, jobId, setters =>
            setters.SetProperty(job => job.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)));

    /// <summary>Simula un worker muerto: el lease queda vencido sin que nadie cierre el job.</summary>
    public static Task ExpireExportLeaseAsync(WebApplicationFactory<Program> factory, Guid jobId) =>
        UpdateExportJobAsync(factory, jobId, setters =>
            setters.SetProperty(job => job.LockedUntil, DateTimeOffset.UtcNow.AddSeconds(-1)));

    public static async Task<IReadOnlyList<QuotationsOutboxMessage>> OutboxMessagesAsync(
        WebApplicationFactory<Program> factory, string eventName)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        return await dbContext.Outbox
            .AsNoTracking()
            .Where(message => message.EventName == eventName)
            .OrderBy(message => message.OccurredAt)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private static async Task UpdateExportJobAsync(
        WebApplicationFactory<Program> factory,
        Guid jobId,
        Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<ExportJob>> setters)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var updated = await dbContext.ExportJobs
            .Where(job => job.Id == jobId)
            .ExecuteUpdateAsync(setters, TestContext.Current.CancellationToken);
        Assert.Equal(1, updated);
    }
```

> Nota para quien implementa: `ExecuteUpdateAsync` recibe `Action<UpdateSettersBuilder<T>>` desde EF Core 10 (antes era una expresión). Si el compilador pide la otra forma, la firma de `UpdateExportJobAsync` pasa a `Expression<Func<SetPropertyCalls<ExportJob>, SetPropertyCalls<ExportJob>>>` sin tocar a quienes la llaman.

`tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportJobQueueTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La cola de exportaciones contra Postgres real (D6): la toma con SKIP LOCKED, el lease, el
/// conteo del límite de pendientes y la purga. Nada de esto se puede probar con un doble: la
/// exclusión la da la base.
/// </summary>
public sealed class ExportJobQueueTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid RequesterId = Guid.CreateVersion7();

    [Fact]
    public async Task ClaimTakesADueJobWithATenMinuteLeaseAndConsumesAnAttempt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var jobId = await EnqueueExportJobAsync(factory, TenantId, RequesterId);
        var now = DateTimeOffset.UtcNow;

        await using var scope = factory.Services.CreateAsyncScope();
        var claimed = await scope.ServiceProvider.GetRequiredService<IExportJobQueue>()
            .ClaimNextAsync(now, TestContext.Current.CancellationToken);

        Assert.NotNull(claimed);
        Assert.Equal(jobId, claimed.Id);
        var stored = await FindExportJobAsync(factory, jobId);
        Assert.Equal(ExportJobStatus.Processing, stored.Status);
        Assert.Equal(1, stored.Attempts);
        // Postgres guarda microsegundos y DateTimeOffset tiene ticks de 100 ns.
        Assert.Equal(now.AddMinutes(10), stored.LockedUntil!.Value, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task ClaimSkipsAJobWaitingItsBackoff()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await EnqueueExportJobAsync(factory, TenantId, RequesterId);

        await using var scope = factory.Services.CreateAsyncScope();
        var claimed = await scope.ServiceProvider.GetRequiredService<IExportJobQueue>()
            .ClaimNextAsync(DateTimeOffset.UtcNow.AddMinutes(-1), TestContext.Current.CancellationToken);

        Assert.Null(claimed);
    }

    // La toma de A queda sin commitear, con la fila bloqueada: B tiene que saltearla y llevarse
    // la otra, no esperar ni tomar la misma. Es lo que hace que escalar a más réplicas no genere
    // el mismo export dos veces.
    [Fact]
    public async Task TwoConcurrentClaimsNeverTakeTheSameJob()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var first = await EnqueueExportJobAsync(factory, TenantId, RequesterId);
        var second = await EnqueueExportJobAsync(factory, TenantId, RequesterId);

        await using var holderScope = factory.Services.CreateAsyncScope();
        var holderContext = holderScope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        await using var holding = await holderContext.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        var claimedByA = await holderScope.ServiceProvider.GetRequiredService<IExportJobQueue>()
            .ClaimNextAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        await using var otherScope = factory.Services.CreateAsyncScope();
        using var notBlocked = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        notBlocked.CancelAfter(TimeSpan.FromSeconds(5));
        var claimedByB = await otherScope.ServiceProvider.GetRequiredService<IExportJobQueue>()
            .ClaimNextAsync(DateTimeOffset.UtcNow, notBlocked.Token);

        Assert.NotNull(claimedByA);
        Assert.NotNull(claimedByB);
        Assert.Equal(
            new HashSet<Guid> { first, second },
            new HashSet<Guid> { claimedByA.Id, claimedByB.Id });
        await holding.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AJobLockedByAnotherTransactionIsSkippedWithoutWaiting()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await EnqueueExportJobAsync(factory, TenantId, RequesterId);

        await using var holderScope = factory.Services.CreateAsyncScope();
        var holderContext = holderScope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        await using var holding = await holderContext.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        Assert.NotNull(await holderScope.ServiceProvider.GetRequiredService<IExportJobQueue>()
            .ClaimNextAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));

        await using var otherScope = factory.Services.CreateAsyncScope();
        using var notBlocked = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        notBlocked.CancelAfter(TimeSpan.FromSeconds(5));
        var claimedByB = await otherScope.ServiceProvider.GetRequiredService<IExportJobQueue>()
            .ClaimNextAsync(DateTimeOffset.UtcNow, notBlocked.Token);

        Assert.Null(claimedByB);
        await holding.RollbackAsync(TestContext.Current.CancellationToken);
    }

    // Worker muerto a mitad (D11): el lease vence y el siguiente tick lo retoma. Con el lease
    // vivo, nadie más lo toca.
    [Fact]
    public async Task AnExpiredLeaseIsReclaimedAndALiveOneIsNot()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var jobId = await EnqueueExportJobAsync(factory, TenantId, RequesterId);
        await ClaimAsync(factory);
        await ExpireExportLeaseAsync(factory, jobId);

        var reclaimed = await ClaimAsync(factory);
        var again = await ClaimAsync(factory);

        Assert.NotNull(reclaimed);
        Assert.Equal(jobId, reclaimed.Id);
        Assert.Equal(2, reclaimed.Attempts);
        Assert.Null(again);
    }

    [Fact]
    public async Task CountPendingCountsBothKindsOnlyForThatRequester()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await EnqueueExportJobAsync(factory, TenantId, RequesterId, ExportJobKind.Quotations);
        await EnqueueExportJobAsync(factory, TenantId, RequesterId, ExportJobKind.Sales);
        await EnqueueExportJobAsync(factory, TenantId, Guid.CreateVersion7(), ExportJobKind.Sales);
        await EnqueueExportJobAsync(factory, Guid.CreateVersion7(), RequesterId, ExportJobKind.Sales);
        await ClaimAsync(factory); // uno pasa a Processing: sigue contando
        await InsertFinishedAsync(factory, DateTimeOffset.UtcNow); // terminado: no cuenta

        await using var scope = factory.Services.CreateAsyncScope();
        var count = await scope.ServiceProvider.GetRequiredService<IExportJobQueue>()
            .CountPendingAsync(TenantId, RequesterId, TestContext.Current.CancellationToken);

        Assert.Equal(2, count);
    }

    // D13: lo terminado hace más de 30 días se va; lo reciente y lo vivo se quedan.
    [Fact]
    public async Task PurgeDeletesOnlyFinishedJobsOlderThanTheCutoff()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var now = DateTimeOffset.UtcNow;
        var old = await InsertFinishedAsync(factory, now.AddDays(-31));
        var recent = await InsertFinishedAsync(factory, now.AddDays(-29));
        var pending = await EnqueueExportJobAsync(factory, TenantId, RequesterId);

        await using var scope = factory.Services.CreateAsyncScope();
        var purged = await scope.ServiceProvider.GetRequiredService<IExportJobQueue>()
            .PurgeFinishedBeforeAsync(now.AddDays(-30), TestContext.Current.CancellationToken);

        Assert.Equal(1, purged);
        await Assert.ThrowsAsync<InvalidOperationException>(() => FindExportJobAsync(factory, old));
        Assert.Equal(ExportJobStatus.Completed, (await FindExportJobAsync(factory, recent)).Status);
        Assert.Equal(ExportJobStatus.Pending, (await FindExportJobAsync(factory, pending)).Status);
    }

    private static async Task<ExportJob?> ClaimAsync(QepApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IExportJobQueue>()
            .ClaimNextAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
    }

    // Un job ya terminado en una fecha dada: se arma en memoria con las transiciones del dominio
    // y se guarda así, porque la API no deja fabricar un completed_at en el pasado.
    private static async Task<Guid> InsertFinishedAsync(QepApiFactory factory, DateTimeOffset finishedAt)
    {
        var job = ExportJob.Enqueue(
            Guid.CreateVersion7(), TenantId, RequesterId, ExportJobKind.Quotations, "{}", finishedAt);
        job.Claim(finishedAt);
        job.Complete("cotizaciones-vieja.xlsx", 1, finishedAt);

        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IExportJobQueue>().Add(job);
        await scope.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
        return job.Id;
    }
}
```

`tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportJobRunnerIntegrationTests.cs`:

```csharp
using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El runner contra la base: que cerrar un job deje estado, evento y auditoría juntos (D10), y
/// que los reintentos terminen en Failed con su evento (D11). Los procesadores son de mentira;
/// el Excel lo cubren las pruebas de los endpoints.
/// </summary>
public sealed class ExportJobRunnerIntegrationTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid RequesterId = Guid.CreateVersion7();

    [Fact]
    public async Task CompletingAJobWritesTheStatusTheReadyEventAndTheAuditTogether()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        using var factory = baseFactory.WithExportProcessors(
            new SucceedingExportProcessor(ExportJobKind.Quotations));
        var jobId = await EnqueueExportJobAsync(factory, TenantId, RequesterId);

        var outcome = await RunExportJobAsync(factory);

        Assert.Equal(ExportJobRunOutcome.Completed, outcome);
        var job = await FindExportJobAsync(factory, jobId);
        Assert.Equal(ExportJobStatus.Completed, job.Status);
        Assert.Equal(3, job.RowCount);
        Assert.Equal("cotizaciones-2026-09-12-1530.xlsx", job.FileName);
        Assert.NotNull(job.CompletedAt);
        Assert.Null(job.LockedUntil);

        var ready = Assert.Single(await OutboxMessagesAsync(factory, "quotations.export-ready.v1"));
        using var payload = JsonDocument.Parse(ready.PayloadJson);
        Assert.Equal(TenantId, payload.RootElement.GetProperty("tenantId").GetGuid());
        Assert.Equal(RequesterId, payload.RootElement.GetProperty("subjectId").GetGuid());
        Assert.Equal("Quotations", payload.RootElement.GetProperty("kind").GetString());
        Assert.Equal("cotizaciones-2026-09-12-1530.xlsx", payload.RootElement.GetProperty("fileName").GetString());
        Assert.Equal(3, payload.RootElement.GetProperty("rowCount").GetInt32());
        Assert.StartsWith("https://r2.test/exports/", payload.RootElement.GetProperty("downloadUrl").GetString(), StringComparison.Ordinal);
        Assert.True(payload.RootElement.TryGetProperty("expiresAt", out _));
        Assert.Equal(jobId.ToString(), ready.CorrelationId);

        var audits = await OutboxMessagesAsync(factory, "platform.audit.recorded.v1");
        Assert.Contains(audits, audit =>
        {
            using var entry = JsonDocument.Parse(audit.PayloadJson);
            return entry.RootElement.GetProperty("action").GetString() == "quotation.quotation.exported"
                && entry.RootElement.GetProperty("resourceId").GetString() == jobId.ToString()
                && entry.RootElement.GetProperty("outcome").GetString() == "success:3";
        });
        Assert.Empty(await OutboxMessagesAsync(factory, "quotations.export-failed.v1"));
    }

    [Fact]
    public async Task RetriesEndInFailedWithASingleFailedEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        using var factory = baseFactory.WithExportProcessors(
            new FailingExportProcessor(ExportJobKind.Quotations, new IOException("r2 unavailable")));
        var jobId = await EnqueueExportJobAsync(factory, TenantId, RequesterId);

        // Tres reintentos —las esperas de 1, 5 y 15 minutos, adelantadas en la base— sin correo,
        // y el cuarto intento cierra el job.
        for (var attempt = 1; attempt < ExportJob.MaxAttempts; attempt++)
        {
            Assert.Equal(ExportJobRunOutcome.RetryScheduled, await RunExportJobAsync(factory));
            Assert.Empty(await OutboxMessagesAsync(factory, "quotations.export-failed.v1"));
            await MakeExportJobDueAsync(factory, jobId);
        }

        Assert.Equal(ExportJobRunOutcome.Failed, await RunExportJobAsync(factory));

        var job = await FindExportJobAsync(factory, jobId);
        Assert.Equal(ExportJobStatus.Failed, job.Status);
        Assert.Equal(4, job.Attempts);
        Assert.Equal("IOException: r2 unavailable", job.LastError);
        var failed = Assert.Single(await OutboxMessagesAsync(factory, "quotations.export-failed.v1"));
        using var payload = JsonDocument.Parse(failed.PayloadJson);
        Assert.Equal(RequesterId, payload.RootElement.GetProperty("subjectId").GetGuid());
        Assert.Equal("Quotations", payload.RootElement.GetProperty("kind").GetString());
        Assert.Empty(await OutboxMessagesAsync(factory, "quotations.export-ready.v1"));
    }
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~ExportJobQueueTests|FullyQualifiedName~ExportJobRunnerIntegrationTests"
```

Esperado: compila (los puertos existen desde Task 3) y **las 9 fallan** con `System.InvalidOperationException: Unable to resolve service for type 'Modules.Quotations.Application.IExportJobQueue'` (o `ExportJobRunner`) — no hay adaptador ni registro. Pegar la salida.

- [ ] **Step 3: Implementar**

`src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/ExportJobQueue.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Persistence;

internal sealed class ExportJobQueue(QuotationsDbContext dbContext) : IExportJobQueue
{
    public void Add(ExportJob job) => dbContext.ExportJobs.Add(job);

    public Task<int> CountPendingAsync(
        Guid tenantId, Guid requestedBy, CancellationToken cancellationToken) =>
        dbContext.ExportJobs.CountAsync(
            job => job.TenantId == tenantId
                && job.RequestedBy == requestedBy
                && (job.Status == ExportJobStatus.Pending || job.Status == ExportJobStatus.Processing),
            cancellationToken);

    /// <summary>
    /// D6: un solo UPDATE que elige y toma. El SELECT interno bloquea la fila elegida y saltea
    /// las que ya bloqueó otra transacción (SKIP LOCKED), así que dos workers nunca se llevan el
    /// mismo job ni se esperan entre sí. Toma un Pending vencido o un Processing con el lease
    /// vencido (worker muerto), suma un intento y fija el lease: la misma transición que
    /// <see cref="ExportJob.Claim"/>.
    ///
    /// Sin transacción propia: fuera de una, el UPDATE commitea solo y el lease se ve desde otros
    /// procesos ya. El resultado queda trackeado —FromSql sin componer— y cerrar el job es un
    /// guardado normal de la unidad de trabajo, con <c>attempts</c> como token de concurrencia.
    /// </summary>
    public async Task<ExportJob?> ClaimNextAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var leaseUntil = now.Add(ExportJob.LeaseDuration);
        var claimed = await dbContext.ExportJobs
            .FromSql($"""
                UPDATE quotations.export_jobs AS job
                SET status = 'Processing',
                    attempts = job.attempts + 1,
                    locked_until = {leaseUntil}
                WHERE job.id = (
                    SELECT candidate.id
                    FROM quotations.export_jobs AS candidate
                    WHERE (candidate.status = 'Pending' AND candidate.next_attempt_at <= {now})
                       OR (candidate.status = 'Processing' AND candidate.locked_until < {now})
                    ORDER BY candidate.next_attempt_at, candidate.id
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED)
                RETURNING job.*
                """)
            // Nada de FirstOrDefaultAsync: componer sobre FromSql envolvería el UPDATE en un
            // SELECT, que Postgres rechaza.
            .ToListAsync(cancellationToken);

        return claimed.SingleOrDefault();
    }

    public Task<int> PurgeFinishedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
        dbContext.ExportJobs
            .Where(job => (job.Status == ExportJobStatus.Completed || job.Status == ExportJobStatus.Failed)
                && job.CompletedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
}
```

`src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/ExportJobEventPublisher.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Persistence;

// Acumula los avisos en la proyección de outbox de QuotationsDbContext, para que commiteen en la
// misma transacción que el cambio de estado del job (D10). Los consumen los dos workers de
// Notifications, que resuelven el correo del solicitante. Mismo mecanismo que
// CustomerExportEventPublisher.
internal sealed class ExportJobEventPublisher(QuotationsDbContext dbContext) : IExportEventPublisher
{
    internal const string ReadyEventName = "quotations.export-ready.v1";
    internal const string FailedEventName = "quotations.export-failed.v1";

    public void PublishReady(ExportJob job, ExportJobResult result, DateTimeOffset occurredAt) =>
        Add(
            job,
            ReadyEventName,
            JsonSerializer.Serialize(new ExportReadyPayload(
                job.TenantId,
                job.RequestedBy,
                job.Kind.ToString(),
                result.DownloadUrl,
                result.FileName,
                result.RowCount,
                result.ExpiresAt)),
            occurredAt);

    public void PublishFailed(ExportJob job, DateTimeOffset occurredAt) =>
        Add(
            job,
            FailedEventName,
            JsonSerializer.Serialize(new ExportFailedPayload(
                job.TenantId, job.RequestedBy, job.Kind.ToString())),
            occurredAt);

    // La correlación es el id del job: soporte llega del correo a la fila de export_jobs sin
    // adivinar cuál de las de ese minuto era.
    private void Add(ExportJob job, string eventName, string payload, DateTimeOffset occurredAt) =>
        dbContext.Outbox.Add(new QuotationsOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = eventName,
            PayloadJson = payload,
            CorrelationId = job.Id.ToString("D", CultureInfo.InvariantCulture),
            OccurredAt = occurredAt,
        });

    // Nombres en minúscula como el resto de los payloads del outbox: el consumidor los lee por
    // nombre con JsonDocument.
    private sealed record ExportReadyPayload(
        Guid tenantId,
        Guid subjectId,
        string kind,
        string downloadUrl,
        string fileName,
        int rowCount,
        DateTimeOffset expiresAt);

    private sealed record ExportFailedPayload(Guid tenantId, Guid subjectId, string kind);
}
```

En `QuotationsInfrastructureExtensions.cs`, después de `services.AddScoped<IQuotationAuditPublisher, QuotationAuditPublisher>();` (`:38`):

```csharp
        // La cola de exportaciones (spec 2026-09-12): la tabla, la toma con SKIP LOCKED y los dos
        // eventos para Notifications. El runner que los usa se registra en Bootstrapper.
        services.AddScoped<IExportJobQueue, ExportJobQueue>();
        services.AddScoped<IExportEventPublisher, ExportJobEventPublisher>();
```

En `QepServiceCollectionExtensions.cs`, después de `services.AddScoped<IQuotationFileLookup, QuotationFileLookup>();` (`:436`):

```csharp
        // El tick del worker de exportaciones. Scoped: ExportJobWorker abre un scope por job para
        // que cada uno tenga su DbContext limpio. Los procesadores por kind se registran con él
        // cuando existen (cotizaciones y ventas).
        services.AddScoped<ExportJobRunner>();
```

- [ ] **Step 4: Correr y verificar que pasan**

```powershell
dotnet build --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build --filter "FullyQualifiedName~ExportJobQueueTests|FullyQualifiedName~ExportJobRunnerIntegrationTests"
```

Esperado: `Passed! - Failed: 0, Passed: 9`. Pegar la salida.

- [ ] **Step 5: Stage** (Git Bash)

```bash
test "$(git branch --show-current)" = "feature/export-asincrono" || { echo ABORT; exit 1; }
git add src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/ExportJobQueue.cs \
  src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/ExportJobEventPublisher.cs \
  src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs \
  src/Bootstrapper/QepServiceCollectionExtensions.cs \
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs \
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportTestProcessors.cs \
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportJobQueueTests.cs \
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportJobRunnerIntegrationTests.cs
```

---

### Task 5: `ExportJobWorker` y commit de la cola

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Exports/ExportJobWorker.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs` (debajo de lo agregado en Task 4)
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs` (`QepApiFactory` `:536-588`)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportJobWorkerTests.cs`

**Interfaces:**
- Consumes: `ExportJobRunner.RunNextAsync`, `ExportJobRunner.PurgeFinishedAsync`, `ExportJobRunOutcome` (Task 3).
- Produces: `internal sealed partial class ExportJobWorker : BackgroundService` (namespace `Modules.Quotations.Infrastructure.Exports`) con `internal static readonly TimeSpan PollInterval = 5 s`; `QepApiFactory(string connectionString, bool runExportWorker = false)`.

- [ ] **Step 1: Escribir la prueba que falla y el interruptor del harness**

En `QuotationsApiHarness.cs`, agregar arriba:

```csharp
using Microsoft.Extensions.Hosting;
using Modules.Quotations.Infrastructure.Exports;
```

Cambiar la firma de la factoría (`:536`):

```csharp
    public sealed class QepApiFactory(string connectionString, bool runExportWorker = false)
        : WebApplicationFactory<Program>
```

y al final del `ConfigureServices` (después del `IQuotationPdfStorage`, `:585`):

```csharp
                // El worker de exportaciones toma jobs cada 5 s por su cuenta: en una prueba
                // competiría con el tick que la prueba corre a mano (RunExportJobAsync) y la
                // volvería no determinista. Sólo lo deja la prueba que ejerce el worker.
                if (!runExportWorker)
                {
                    var exportWorkers = services
                        .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                            && descriptor.ImplementationType == typeof(ExportJobWorker))
                        .ToList();
                    foreach (var descriptor in exportWorkers)
                    {
                        services.Remove(descriptor);
                    }
                }
```

`tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportJobWorkerTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Exports;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El hosted service de verdad (D7): que esté registrado y que tome un job sin que nadie lo
/// llame. Es la única prueba que espera un tick; el resto corre el runner a mano.
/// </summary>
public sealed class ExportJobWorkerTests
{
    [Fact]
    public async Task TheHostedWorkerCompletesAPendingJobOnItsOwn()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString(), runExportWorker: true);
        using var factory = baseFactory.WithExportProcessors(
            new SucceedingExportProcessor(ExportJobKind.Quotations));

        var jobId = await EnqueueExportJobAsync(factory, Guid.CreateVersion7(), Guid.CreateVersion7());

        Assert.Equal(ExportJobStatus.Completed, await WaitForStatusAsync(factory, jobId, ExportJobStatus.Completed));
    }

    // El interruptor del harness: sin él, cada prueba de la cola competiría con el worker.
    [Fact]
    public async Task TheTestHostLeavesTheWorkerOutByDefault()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        Assert.Empty(factory.Services.GetServices<IHostedService>().OfType<ExportJobWorker>());
    }

    // Mismo mecanismo que InvitationNotificationTests: sondeo con plazo, porque el tick es del
    // worker y no de la prueba.
    private static async Task<ExportJobStatus?> WaitForStatusAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory,
        Guid jobId,
        ExportJobStatus expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        ExportJobStatus? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = (await FindExportJobAsync(factory, jobId)).Status;
            if (last == expected)
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }

        return last;
    }
}
```

- [ ] **Step 2: Correr y verificar que falla**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~ExportJobWorkerTests"
```

Esperado: no compila — `error CS0246: The type or namespace name 'ExportJobWorker' could not be found` (en el harness y en la prueba). Pegar la salida.

- [ ] **Step 3: Implementar**

`src/Modules/Quotations/Modules.Quotations.Infrastructure/Exports/ExportJobWorker.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Quotations.Application;

namespace Modules.Quotations.Infrastructure.Exports;

// D7: un worker, un job a la vez. Mismo esqueleto que los workers de Notifications —PeriodicTimer,
// scope nuevo por unidad de trabajo, una falla se loguea y no mata el loop—. Concurrencia 1 a
// propósito: el Excel se arma en el mismo pod de 1Gi que atiende la API.
internal sealed partial class ExportJobWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ExportJobWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PurgeInterval = TimeSpan.FromDays(1);

    // En memoria y no en la base: con una réplica, que un reinicio adelante la purga no cuesta
    // nada —borrar lo vencido dos veces el mismo día es inofensivo—.
    private DateTimeOffset _nextPurgeAt = DateTimeOffset.MinValue;

    [LoggerMessage(Level = LogLevel.Error, Message = "Export job tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Export job lease was lost before it could be completed; another worker owns it.")]
    private static partial void LogLeaseLost(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} finished export jobs.")]
    private static partial void LogPurged(ILogger logger, int count);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await DrainAsync(stoppingToken);
                await PurgeIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // Uno por uno hasta vaciar lo vencido, cada job en su scope: un DbContext por job no arrastra
    // entidades trackeadas de un export al siguiente. No hay loop infinito posible: un reintento
    // queda con next_attempt_at en el futuro y un lease perdido es de otro worker.
    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var outcome = await scope.ServiceProvider.GetRequiredService<ExportJobRunner>()
                .RunNextAsync(cancellationToken);

            if (outcome == ExportJobRunOutcome.NoJob)
            {
                return;
            }

            if (outcome == ExportJobRunOutcome.LeaseLost)
            {
                LogLeaseLost(logger);
            }
        }
    }

    private async Task PurgeIfDueAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextPurgeAt)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var purged = await scope.ServiceProvider.GetRequiredService<ExportJobRunner>()
            .PurgeFinishedAsync(cancellationToken);
        _nextPurgeAt = now.Add(PurgeInterval);
        LogPurged(logger, purged);
    }
}
```

En `QuotationsInfrastructureExtensions.cs`, agregar `using Modules.Quotations.Infrastructure.Exports;` y, debajo de las dos líneas de Task 4:

```csharp
        services.AddHostedService<ExportJobWorker>();
```

- [ ] **Step 4: Correr y verificar que pasan, más la regresión del commit**

```powershell
dotnet build --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build --filter "FullyQualifiedName~ExportJobWorkerTests"
```

Esperado: `Passed! - Failed: 0, Passed: 2`. Después, todo lo del commit y la suite completa contra el baseline:

```powershell
dotnet format --verify-no-changes
$after = Join-Path $env:TEMP "qep-export-asincrono-commit1"
Remove-Item -Recurse -Force $after -ErrorAction SilentlyContinue
dotnet test --no-build --logger trx --results-directory $after
$failed = Get-ChildItem $after -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -Raw $_.FullName
    $trx.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq "Failed" } | ForEach-Object { $_.testName }
} | Sort-Object -Unique
$baseline = Get-Content (Join-Path $env:TEMP "qep-export-asincrono-baseline-failed.txt")
Compare-Object $baseline $failed | Where-Object { $_.SideIndicator -eq "=>" }
```

Esperado: `dotnet format` sin cambios y el `Compare-Object` **vacío** (ninguna prueba nueva en rojo). Pegar las salidas.

- [ ] **Step 5: Commit** (Git Bash)

```bash
test "$(git branch --show-current)" = "feature/export-asincrono" || { echo ABORT; exit 1; }
git add src/Modules/Quotations/Modules.Quotations.Infrastructure/Exports/ExportJobWorker.cs \
  src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs \
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs \
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportJobWorkerTests.cs
git status --short
git commit -m "feat(quotations): cola de exportaciones con worker y reintentos" \
  -m "La tabla quotations.export_jobs es la cola: la toma es un UPDATE con FOR UPDATE SKIP LOCKED y lease de 10 minutos, así que dos workers nunca se llevan el mismo job. ExportJobRunner despacha por kind, cierra en una transacción (estado, quotations.export-ready.v1 y auditoría) y reintenta a 1, 5 y 15 minutos; al cuarto intento fallido, Failed y quotations.export-failed.v1. ExportJobWorker lo corre cada 5 s, de a un job, y purga una vez al día lo terminado hace más de 30 días. La migración suma los índices del keyset que leen los exports de cotizaciones y ventas."
```

Antes del `commit`, `git status --short` tiene que mostrar staged (`A `/`M `) sólo los archivos de Tasks 1–5 y nada sin stagear de esas rutas. Si aparece otro archivo, parar.

---

## Commit 2 — `feat(notifications): correos de exportación lista y fallida`

### Task 6: Plantillas de los dos correos

**Files:**
- Create: `src/Modules/Notifications/Modules.Notifications.Application/QuotationsExportKindText.cs`
- Create: `src/Modules/Notifications/Modules.Notifications.Application/QuotationsExportReadyEmailTemplate.cs`
- Create: `src/Modules/Notifications/Modules.Notifications.Application/QuotationsExportFailedEmailTemplate.cs`
- Test: `tests/Modules/Notifications/Modules.Notifications.UnitTests/QuotationsExportEmailTemplateTests.cs`

**Interfaces:**
- Consumes: `EmailMessage(string ToAddress, string Subject, string HtmlBody, string TextBody)` (existente, `IEmailChannel.cs`).
- Produces:
  - `public sealed record QuotationsExportKindNames(string Singular, string Plural)`; `public static class QuotationsExportKindText { static QuotationsExportKindNames Of(string kind); }`
  - `public static class QuotationsExportReadyEmailTemplate { const string TemplateRef = "quotations.export-ready.v1"; static EmailMessage Render(string recipientAddress, string kind, string downloadUrl, string fileName, int rowCount, DateTimeOffset expiresAt); }`
  - `public static class QuotationsExportFailedEmailTemplate { const string TemplateRef = "quotations.export-failed.v1"; static EmailMessage Render(string recipientAddress, string kind); }`

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Notifications/Modules.Notifications.UnitTests/QuotationsExportEmailTemplateTests.cs`:

```csharp
using Modules.Notifications.Application;

namespace Modules.Notifications.UnitTests;

/// <summary>
/// Los dos correos de la exportación asíncrona de cotizaciones y ventas (spec 2026-09-12, D12).
/// El de "lista" sigue al de clientes —enlace escapado en el HTML, crudo en texto plano— y los dos
/// nombran el tipo según el kind del evento.
/// </summary>
public sealed class QuotationsExportEmailTemplateTests
{
    private static readonly DateTimeOffset ExpiresAt = new(2026, 9, 13, 15, 30, 0, TimeSpan.Zero);

    private const string SignedUrl =
        "https://r2.example/exports/tenants/a/jobs/b.xlsx?X-Amz-Algorithm=AWS4-HMAC-SHA256" +
        "&X-Amz-Date=20260912T153000Z&X-Amz-Expires=86400&X-Amz-Signature=deadbeef";

    [Fact]
    public void ReadyNamesTheKindAndPutsTheLinkAndTheExpiryInBothBodies()
    {
        var message = QuotationsExportReadyEmailTemplate.Render(
            "ana@qcode.co", "Sales", SignedUrl, "ventas-2026-09-12-1530.xlsx", 42, ExpiresAt);

        Assert.Equal("ana@qcode.co", message.ToAddress);
        Assert.Equal("Tu exportación de ventas está lista", message.Subject);
        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            Assert.Contains("ventas-2026-09-12-1530.xlsx", body, StringComparison.Ordinal);
            Assert.Contains("42 ventas", body, StringComparison.Ordinal);
            Assert.Contains("13/09/2026 15:30 UTC", body, StringComparison.Ordinal);
        }

        Assert.Contains(SignedUrl, message.TextBody, StringComparison.Ordinal);
    }

    // Mismo motivo que en CustomerExportEmailTemplateTests: un `&` sin declarar en el href rompe
    // la firma de R2 según cómo normalice el HTML cada cliente de correo.
    [Fact]
    public void ReadyEscapesTheLinkInsideTheHtmlHref()
    {
        var message = QuotationsExportReadyEmailTemplate.Render(
            "ana@qcode.co", "Quotations", SignedUrl, "cotizaciones.xlsx", 3, ExpiresAt);

        Assert.Contains("&amp;X-Amz-Signature=", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("&X-Amz-Signature=", message.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadyUsesTheSingularForOneRow()
    {
        var message = QuotationsExportReadyEmailTemplate.Render(
            "ana@qcode.co", "Quotations", "https://r2.example/x", "cotizaciones.xlsx", 1, ExpiresAt);

        Assert.Equal("Tu exportación de cotizaciones está lista", message.Subject);
        Assert.Contains("(1 cotización)", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedTellsWhichExportFailedAndToTryAgain()
    {
        var message = QuotationsExportFailedEmailTemplate.Render("ana@qcode.co", "Quotations");

        Assert.Equal("ana@qcode.co", message.ToAddress);
        Assert.Equal("No pudimos generar tu exportación de cotizaciones", message.Subject);
        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            Assert.Contains(
                "No pudimos generar tu exportación de cotizaciones. Intenta de nuevo.",
                body,
                StringComparison.Ordinal);
        }
    }

    // Un kind que llegue antes que su texto no deja a nadie sin correo: cae a un nombre genérico.
    [Fact]
    public void AnUnknownKindStillProducesAReadableEmail()
    {
        var message = QuotationsExportFailedEmailTemplate.Render("ana@qcode.co", "Invoices");

        Assert.Equal("No pudimos generar tu exportación de registros", message.Subject);
    }
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --filter "FullyQualifiedName~QuotationsExportEmailTemplateTests"
```

Esperado: no compila — `error CS0103: The name 'QuotationsExportReadyEmailTemplate' does not exist in the current context` (y `QuotationsExportFailedEmailTemplate`). Pegar la salida.

- [ ] **Step 3: Implementar**

`src/Modules/Notifications/Modules.Notifications.Application/QuotationsExportKindText.cs`:

```csharp
namespace Modules.Notifications.Application;

public sealed record QuotationsExportKindNames(string Singular, string Plural);

/// <summary>
/// Cómo se nombra en el correo cada kind de <c>quotations.export-*.v1</c>. El kind viaja como el
/// nombre del enum de Quotations; Notifications no referencia ese módulo, así que la traducción
/// vive acá. Un kind desconocido cae a "registros": que el texto llegue después que el evento no
/// puede dejar sin correo a quien pidió la exportación.
/// </summary>
public static class QuotationsExportKindText
{
    public static QuotationsExportKindNames Of(string kind) => kind switch
    {
        "Quotations" => new("cotización", "cotizaciones"),
        "Sales" => new("venta", "ventas"),
        _ => new("registro", "registros"),
    };
}
```

`src/Modules/Notifications/Modules.Notifications.Application/QuotationsExportReadyEmailTemplate.cs`:

```csharp
using System.Globalization;
using System.Net;

namespace Modules.Notifications.Application;

/// <summary>
/// El correo de exportación lista de cotizaciones o ventas. Mismo criterio que
/// <see cref="CustomerExportEmailTemplate"/>: plantilla fija, variables en allowlist y el
/// vencimiento dicho explícito, porque un enlace que caduca sin aviso se lee como una falla.
/// </summary>
public static class QuotationsExportReadyEmailTemplate
{
    public const string TemplateRef = "quotations.export-ready.v1";

    public static EmailMessage Render(
        string recipientAddress,
        string kind,
        string downloadUrl,
        string fileName,
        int rowCount,
        DateTimeOffset expiresAt)
    {
        var names = QuotationsExportKindText.Of(kind);
        var subject = $"Tu exportación de {names.Plural} está lista";
        var expiry = expiresAt.ToString("dd/MM/yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var rows = rowCount == 1
            ? $"1 {names.Singular}"
            : $"{rowCount.ToString(CultureInfo.InvariantCulture)} {names.Plural}";

        // HTML con la URL escapada y texto plano con la URL cruda: mismo motivo que en
        // CustomerExportEmailTemplate (la firma de R2 se rompe con un `&` mal normalizado).
        var htmlUrl = WebUtility.HtmlEncode(downloadUrl);
        var htmlFileName = WebUtility.HtmlEncode(fileName);

        var textBody =
            $"Hola,\n\n" +
            $"La exportación que solicitaste ya está lista: {fileName} ({rows}).\n" +
            $"Descárgala desde este enlace:\n\n" +
            $"{downloadUrl}\n\n" +
            $"El enlace vence el {expiry}. Después de esa fecha tendrás que solicitar la " +
            $"exportación de nuevo.\n";

        var htmlBody =
            $"<p>Hola,</p>" +
            $"<p>La exportación que solicitaste ya está lista: " +
            $"<strong>{htmlFileName}</strong> ({rows}).</p>" +
            $"<p><a href=\"{htmlUrl}\">Descargar exportación</a></p>" +
            $"<p>El enlace vence el {expiry}. Después de esa fecha tendrás que solicitar la " +
            $"exportación de nuevo.</p>";

        return new EmailMessage(recipientAddress, subject, htmlBody, textBody);
    }
}
```

`src/Modules/Notifications/Modules.Notifications.Application/QuotationsExportFailedEmailTemplate.cs`:

```csharp
namespace Modules.Notifications.Application;

/// <summary>
/// El correo de exportación fallida (D12). Sin el motivo técnico: `last_error` es para soporte y
/// a quien exporta no le sirve para nada que pueda hacer. Lo que sí le sirve es saber que no le
/// va a llegar el archivo y que puede pedirlo de nuevo.
/// </summary>
public static class QuotationsExportFailedEmailTemplate
{
    public const string TemplateRef = "quotations.export-failed.v1";

    public static EmailMessage Render(string recipientAddress, string kind)
    {
        var names = QuotationsExportKindText.Of(kind);
        var subject = $"No pudimos generar tu exportación de {names.Plural}";
        var sentence = $"No pudimos generar tu exportación de {names.Plural}. Intenta de nuevo.";

        var textBody = $"Hola,\n\n{sentence}\n";
        var htmlBody = $"<p>Hola,</p><p>{sentence}</p>";

        return new EmailMessage(recipientAddress, subject, htmlBody, textBody);
    }
}
```

- [ ] **Step 4: Correr y verificar que pasan**

```powershell
dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --filter "FullyQualifiedName~QuotationsExportEmailTemplateTests"
```

Esperado: `Passed! - Failed: 0, Passed: 5`. Pegar la salida.

- [ ] **Step 5: Stage** (Git Bash; el commit sale en Task 7)

```bash
test "$(git branch --show-current)" = "feature/export-asincrono" || { echo ABORT; exit 1; }
git add src/Modules/Notifications/Modules.Notifications.Application/QuotationsExportKindText.cs \
  src/Modules/Notifications/Modules.Notifications.Application/QuotationsExportReadyEmailTemplate.cs \
  src/Modules/Notifications/Modules.Notifications.Application/QuotationsExportFailedEmailTemplate.cs \
  tests/Modules/Notifications/Modules.Notifications.UnitTests/QuotationsExportEmailTemplateTests.cs
```

---

### Task 7: Workers de Notifications y commit de los correos

**Files:**
- Create: `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/QuotationsExportReadyDeliveryWorker.cs`
- Create: `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/QuotationsExportFailedDeliveryWorker.cs`
- Modify: `src/Modules/Notifications/Modules.Notifications.Infrastructure/NotificationsInfrastructureExtensions.cs:54-56` (después de `ProductExportDeliveryWorker`)
- Test: `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/QuotationsExportNotificationTests.cs`

**Interfaces:**
- Consumes: `QuotationsExportReadyEmailTemplate.Render(...)`, `QuotationsExportFailedEmailTemplate.Render(...)`, sus `TemplateRef` (Task 6); los payloads de `ExportJobEventPublisher` (Task 4); `IUserDirectory.GetEmailAsync(Guid, CancellationToken)`, `IEmailChannel.SendAsync`, `Notification.CreateEmail/MarkSent/MarkFailed`, `NotificationsDbContext.Outbox/Inbox/Notifications` (existentes).
- Produces: consumidores `notifications.quotations-export-ready-email` y `notifications.quotations-export-failed-email` en el inbox del módulo.

- [ ] **Step 1: Escribir la prueba que falla**

`tests/Modules/Notifications/Modules.Notifications.IntegrationTests/QuotationsExportNotificationTests.cs`:

```csharp
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Notifications.IntegrationTests;

/// <summary>
/// Los dos correos de la exportación asíncrona (spec 2026-09-12, D12). El evento se escribe
/// directo en el outbox, como lo deja Quotations: lo que se prueba acá es que Notifications lo
/// consume y le escribe a quien pidió la exportación, no cómo se arma el Excel.
/// </summary>
public sealed class QuotationsExportNotificationTests
{
    [Fact]
    public async Task TheReadyEventDeliversOneReadyEmailToWhoAskedForTheExport()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, email) = await RegisterOwnerAsync(factory);

        await InsertOutboxAsync(
            database.GetConnectionString(),
            "quotations.export-ready.v1",
            JsonSerializer.Serialize(new
            {
                tenantId,
                subjectId = ownerUserId,
                kind = "Quotations",
                downloadUrl = "https://r2.test/exports/x.xlsx?X-Amz-Signature=abc",
                fileName = "cotizaciones-2026-09-12-1530.xlsx",
                rowCount = 3,
                expiresAt = DateTimeOffset.UtcNow.AddHours(24),
            }));

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var delivered = await WaitForNotificationAsync(connection, ownerUserId, "quotations.export-ready.v1");
        Assert.Equal(("Sent", email), delivered);

        // Idempotente por el inbox: los ticks siguientes no vuelven a mandar el mismo evento.
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        Assert.Equal(1L, await CountNotificationsAsync(connection, ownerUserId, "quotations.export-ready.v1"));
    }

    [Fact]
    public async Task TheFailedEventDeliversTheFailedEmail()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, email) = await RegisterOwnerAsync(factory);

        await InsertOutboxAsync(
            database.GetConnectionString(),
            "quotations.export-failed.v1",
            JsonSerializer.Serialize(new { tenantId, subjectId = ownerUserId, kind = "Sales" }));

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var delivered = await WaitForNotificationAsync(connection, ownerUserId, "quotations.export-failed.v1");
        Assert.Equal(("Sent", email), delivered);
    }

    // register-tenant es la única forma de tener un usuario con correo en identity.users sin la
    // vuelta de Google: el stub toma el correo del header X-Email. Mismo mecanismo que
    // QuotationsApiHarness.RegisterTenantAsync.
    private static async Task<(Guid TenantId, Guid OwnerUserId, string Email)> RegisterOwnerAsync(
        QepApiFactory factory)
    {
        var email = $"owner-{Guid.NewGuid():N}@example.com";
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add("X-Email", email);
        client.DefaultRequestHeaders.Add("X-Email-Verified", "true");

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/register-tenant",
            new
            {
                displayName = "Notifications Export Org",
                slug = $"org-{Guid.NewGuid():N}"[..12],
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "yyyy-MM-dd",
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var registered = await response.Content.ReadFromJsonAsync<RegisteredTenantDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(registered);
        return (registered.TenantId, registered.OwnerUserId, email);
    }

    private static async Task InsertOutboxAsync(string connectionString, string eventName, string payload)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform.outbox_messages (id, event_name, payload, correlation_id, occurred_at, attempts)
            VALUES (@id, @eventName, CAST(@payload AS jsonb), @correlationId, @occurredAt, 0)
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("eventName", eventName);
        command.Parameters.AddWithValue("payload", payload);
        command.Parameters.AddWithValue("correlationId", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("occurredAt", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<(string Status, string Recipient)?> WaitForNotificationAsync(
        NpgsqlConnection connection, Guid recipientId, string templateRef)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT status, recipient_address FROM notifications.notifications
                WHERE recipient_id = @recipientId AND template_ref = @templateRef
                """,
                connection);
            command.Parameters.AddWithValue("recipientId", recipientId);
            command.Parameters.AddWithValue("templateRef", templateRef);
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            if (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                return (reader.GetString(0), reader.GetString(1));
            }

            await reader.CloseAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }

        return null;
    }

    private static async Task<long> CountNotificationsAsync(
        NpgsqlConnection connection, Guid recipientId, string templateRef)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM notifications.notifications
            WHERE recipient_id = @recipientId AND template_ref = @templateRef
            """,
            connection);
        command.Parameters.AddWithValue("recipientId", recipientId);
        command.Parameters.AddWithValue("templateRef", templateRef);
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    private sealed record RegisteredTenantDto(Guid TenantId, Guid OwnerUserId);

    // Misma factoría que InvitationNotificationTests: cada archivo de este proyecto arma la suya.
    private sealed class QepApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijado, nunca heredado: deja explícito que el correo sale por el canal de log.
            builder.UseSetting("Notifications:EmailProvider", "log");
        }
    }
}
```

- [ ] **Step 2: Correr y verificar que falla**

```powershell
dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --filter "FullyQualifiedName~QuotationsExportNotificationTests"
```

Esperado: compila y **las dos fallan** en `Assert.Equal() Failure` con `Actual: null` después de los 30 s de plazo: nadie consume los eventos todavía. Pegar la salida.

- [ ] **Step 3: Implementar**

`src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/QuotationsExportReadyDeliveryWorker.cs`:

```csharp
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Identity.Application;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume `quotations.export-ready.v1` y le manda a quien pidió la exportación el correo con el
// enlace. Calcado de CustomerExportDeliveryWorker: idempotente por el inbox propio, un commit por
// mensaje para que una falla no bloquee el lote, y el enlace ya viene prefirmado —este módulo no
// conoce Storage—. Un worker por evento, igual que clientes y productos.
internal sealed partial class QuotationsExportReadyDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<QuotationsExportReadyDeliveryWorker> logger) : BackgroundService
{
    private const string Consumer = "notifications.quotations-export-ready-email";
    private const string EventName = "quotations.export-ready.v1";
    private const int BatchSize = 20;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    [LoggerMessage(Level = LogLevel.Error, Message = "Quotations export ready delivery tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var channel = scope.ServiceProvider.GetRequiredService<IEmailChannel>();
        var userDirectory = scope.ServiceProvider.GetRequiredService<IUserDirectory>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var pending = await dbContext.Outbox
            .Where(record => record.EventName == EventName)
            .Where(record => !dbContext.Inbox.Any(entry =>
                entry.Consumer == Consumer && entry.MessageId == record.Id))
            .OrderBy(record => record.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var record in pending)
        {
            await DeliverAsync(dbContext, channel, userDirectory, clock, record, cancellationToken);
        }
    }

    private static async Task DeliverAsync(
        NotificationsDbContext dbContext,
        IEmailChannel channel,
        IUserDirectory userDirectory,
        IClock clock,
        OutboxRecord record,
        CancellationToken cancellationToken)
    {
        var export = ParsePayload(record.PayloadJson);
        var email = await userDirectory.GetEmailAsync(export.SubjectId, cancellationToken);
        var notification = Notification.CreateEmail(
            export.TenantId,
            export.SubjectId,
            email ?? string.Empty,
            QuotationsExportReadyEmailTemplate.TemplateRef,
            clock.UtcNow);

        if (string.IsNullOrWhiteSpace(email))
        {
            notification.MarkFailed("recipient_email_unavailable", clock.UtcNow);
        }
        else
        {
            try
            {
                var message = QuotationsExportReadyEmailTemplate.Render(
                    email,
                    export.Kind,
                    export.DownloadUrl,
                    export.FileName,
                    export.RowCount,
                    export.ExpiresAt);
                await channel.SendAsync(message, cancellationToken);
                notification.MarkSent(clock.UtcNow);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                notification.MarkFailed(exception.Message, clock.UtcNow);
            }
        }

        // Se marca el fallo y se escribe el inbox en vez de tirar: una excepción acá aborta el
        // lote entero y lo reintenta para siempre. Misma regla que el worker de invitaciones.
        dbContext.Notifications.Add(notification);
        dbContext.Inbox.Add(new NotificationInboxMessage
        {
            Consumer = Consumer,
            MessageId = record.Id,
            ProcessedAt = clock.UtcNow,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static ReadyPayload ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        return new ReadyPayload(
            root.GetProperty("tenantId").GetGuid(),
            root.GetProperty("subjectId").GetGuid(),
            root.GetProperty("kind").GetString() ?? string.Empty,
            root.GetProperty("downloadUrl").GetString() ?? string.Empty,
            root.GetProperty("fileName").GetString() ?? string.Empty,
            root.GetProperty("rowCount").GetInt32(),
            root.GetProperty("expiresAt").GetDateTimeOffset());
    }

    private sealed record ReadyPayload(
        Guid TenantId,
        Guid SubjectId,
        string Kind,
        string DownloadUrl,
        string FileName,
        int RowCount,
        DateTimeOffset ExpiresAt);
}
```

`src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/QuotationsExportFailedDeliveryWorker.cs`:

```csharp
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Identity.Application;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume `quotations.export-failed.v1`: a quien pidió la exportación le avisa que no le va a
// llegar el archivo y que puede pedirlo de nuevo. Mismo mecanismo que su par de "lista".
internal sealed partial class QuotationsExportFailedDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<QuotationsExportFailedDeliveryWorker> logger) : BackgroundService
{
    private const string Consumer = "notifications.quotations-export-failed-email";
    private const string EventName = "quotations.export-failed.v1";
    private const int BatchSize = 20;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    [LoggerMessage(Level = LogLevel.Error, Message = "Quotations export failed delivery tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var channel = scope.ServiceProvider.GetRequiredService<IEmailChannel>();
        var userDirectory = scope.ServiceProvider.GetRequiredService<IUserDirectory>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var pending = await dbContext.Outbox
            .Where(record => record.EventName == EventName)
            .Where(record => !dbContext.Inbox.Any(entry =>
                entry.Consumer == Consumer && entry.MessageId == record.Id))
            .OrderBy(record => record.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var record in pending)
        {
            await DeliverAsync(dbContext, channel, userDirectory, clock, record, cancellationToken);
        }
    }

    private static async Task DeliverAsync(
        NotificationsDbContext dbContext,
        IEmailChannel channel,
        IUserDirectory userDirectory,
        IClock clock,
        OutboxRecord record,
        CancellationToken cancellationToken)
    {
        var export = ParsePayload(record.PayloadJson);
        var email = await userDirectory.GetEmailAsync(export.SubjectId, cancellationToken);
        var notification = Notification.CreateEmail(
            export.TenantId,
            export.SubjectId,
            email ?? string.Empty,
            QuotationsExportFailedEmailTemplate.TemplateRef,
            clock.UtcNow);

        if (string.IsNullOrWhiteSpace(email))
        {
            notification.MarkFailed("recipient_email_unavailable", clock.UtcNow);
        }
        else
        {
            try
            {
                await channel.SendAsync(
                    QuotationsExportFailedEmailTemplate.Render(email, export.Kind), cancellationToken);
                notification.MarkSent(clock.UtcNow);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                notification.MarkFailed(exception.Message, clock.UtcNow);
            }
        }

        dbContext.Notifications.Add(notification);
        dbContext.Inbox.Add(new NotificationInboxMessage
        {
            Consumer = Consumer,
            MessageId = record.Id,
            ProcessedAt = clock.UtcNow,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static FailedPayload ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        return new FailedPayload(
            root.GetProperty("tenantId").GetGuid(),
            root.GetProperty("subjectId").GetGuid(),
            root.GetProperty("kind").GetString() ?? string.Empty);
    }

    private sealed record FailedPayload(Guid TenantId, Guid SubjectId, string Kind);
}
```

En `NotificationsInfrastructureExtensions.cs`, después del registro de `ProductExportDeliveryWorker` (`:54-56`):

```csharp
        // La exportación asíncrona de Quotations (spec 2026-09-12, D12): un worker por evento,
        // igual que clientes y productos. Sin IOptions: el enlace ya viene prefirmado.
        services.AddHostedService(sp => new QuotationsExportReadyDeliveryWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<QuotationsExportReadyDeliveryWorker>>()));

        services.AddHostedService(sp => new QuotationsExportFailedDeliveryWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<QuotationsExportFailedDeliveryWorker>>()));
```

- [ ] **Step 4: Correr y verificar que pasan, más la regresión**

```powershell
dotnet build --no-restore
dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --no-build --filter "FullyQualifiedName~QuotationsExportNotificationTests"
dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --no-build
dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --no-build
dotnet format --verify-no-changes
```

Esperado: `Passed: 2` en la primera, todo en verde en las otras dos y `dotnet format` sin cambios. Pegar las salidas.

- [ ] **Step 5: Commit** (Git Bash)

```bash
test "$(git branch --show-current)" = "feature/export-asincrono" || { echo ABORT; exit 1; }
git add src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/QuotationsExportReadyDeliveryWorker.cs \
  src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/QuotationsExportFailedDeliveryWorker.cs \
  src/Modules/Notifications/Modules.Notifications.Infrastructure/NotificationsInfrastructureExtensions.cs \
  tests/Modules/Notifications/Modules.Notifications.IntegrationTests/QuotationsExportNotificationTests.cs
git status --short
git commit -m "feat(notifications): correos de exportación lista y fallida" \
  -m "Dos workers calcados de CustomerExportDeliveryWorker consumen quotations.export-ready.v1 y quotations.export-failed.v1: el primero manda el enlace prefirmado con el nombre del archivo, la cantidad de filas y la vigencia; el segundo avisa que no se pudo generar y que se puede pedir de nuevo. El texto nombra cotizaciones o ventas según el kind."
```

---

## Commit 3 — `feat(quotations): exportar cotizaciones por correo`

### Task 8: Writer en streaming con `OpenXmlWriter`

**Files:**
- Modify: `Directory.Packages.props:8-9` (entre `coverlet.collector` y `FluentValidation`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Modules.Quotations.Infrastructure.csproj` (`ItemGroup` de `PackageReference`)
- Create: `src/Modules/Quotations/Modules.Quotations.Application/IExportWorkbookWriter.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Excel/OpenXmlExportWorkbookWriter.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs` (junto al registro de Task 4)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OpenXmlExportWorkbookWriterTests.cs`

**Interfaces:**
- Consumes: nada del plan.
- Produces:
  - `public interface IExportWorkbookWriter { IExportWorkbook Create(string sheetName, IReadOnlyList<ExportColumn> columns); }`
  - `public interface IExportWorkbook : IDisposable { void AppendRow(IReadOnlyList<ExportCell> cells); string Complete(); }` — `Complete` cierra el archivo y devuelve la ruta del temporal, válida hasta `Dispose`, que lo borra.
  - `public sealed record ExportColumn(string Header, double Width)`
  - `public readonly record struct ExportCell(string? Text, decimal? Number)` con `static ExportCell OfText(string? value)` y `static ExportCell OfNumber(decimal value)`.
  - `internal sealed class OpenXmlExportWorkbookWriter : IExportWorkbookWriter` (singleton).

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Quotations/Modules.Quotations.UnitTests/OpenXmlExportWorkbookWriterTests.cs`:

```csharp
using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Excel;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// La forma de la hoja (D8): cabecera en negrita y congelada, texto como texto, importes como
/// número, anchos fijos. Se abre el archivo con el propio SDK de OpenXML: verificar sólo que "no
/// explota" dejaría pasar una hoja con las columnas corridas.
/// </summary>
public sealed class OpenXmlExportWorkbookWriterTests
{
    private static readonly IReadOnlyList<ExportColumn> Columns =
        [new("Numero", 16), new("Fecha", 34), new("Total", 16)];

    [Fact]
    public void TheHeaderIsTheFirstRowInBoldAndFrozen()
    {
        using var workbook = new OpenXmlExportWorkbookWriter().Create("Cotizaciones", Columns);

        var sheet = Read(workbook.Complete());

        Assert.Equal("Cotizaciones", sheet.Name);
        var header = Assert.Single(sheet.Rows);
        Assert.Equal(["Numero", "Fecha", "Total"], header.Select(cell => cell.Text));
        Assert.All(header, cell => Assert.Equal(1u, cell.StyleIndex));
        Assert.True(sheet.StyleOneIsBold);
        Assert.True(sheet.HeaderIsFrozen);
    }

    [Fact]
    public void RowsKeepTheirOrderWithTextAsTextAndAmountsAsNumbers()
    {
        using var workbook = new OpenXmlExportWorkbookWriter().Create("Cotizaciones", Columns);
        workbook.AppendRow([
            ExportCell.OfText("QUO-2026-0002"),
            ExportCell.OfText("2026-09-12T15:30:00.0000000+00:00"),
            ExportCell.OfNumber(452000.50m)]);
        workbook.AppendRow([
            ExportCell.OfText("QUO-2026-0001"),
            ExportCell.OfText(null),
            ExportCell.OfNumber(0m)]);

        var sheet = Read(workbook.Complete());

        Assert.Equal(3, sheet.Rows.Count);
        var first = sheet.Rows[1];
        Assert.Equal("QUO-2026-0002", first[0].Text);
        Assert.False(first[0].IsNumber);
        // Fecha como texto ISO: una celda de fecha se mostraría según la configuración regional
        // de quien abre el archivo.
        Assert.Equal("2026-09-12T15:30:00.0000000+00:00", first[1].Text);
        Assert.False(first[1].IsNumber);
        Assert.True(first[2].IsNumber);
        Assert.Equal(452000.50m, decimal.Parse(first[2].Text, CultureInfo.InvariantCulture));
        Assert.Null(first[0].StyleIndex);
        Assert.Equal("QUO-2026-0001", sheet.Rows[2][0].Text);
        Assert.Equal(string.Empty, sheet.Rows[2][1].Text);
    }

    // Anchos fijos (D8): medir el contenido obligaría a recorrerlo dos veces.
    [Fact]
    public void ColumnsHaveTheirFixedWidths()
    {
        using var workbook = new OpenXmlExportWorkbookWriter().Create("Cotizaciones", Columns);

        var sheet = Read(workbook.Complete());

        Assert.Equal([16d, 34d, 16d], sheet.Widths);
    }

    // El temporal no queda en el disco del pod: Dispose lo borra, se haya completado o no.
    [Fact]
    public void DisposeDeletesTheTemporaryFile()
    {
        string path;
        using (var workbook = new OpenXmlExportWorkbookWriter().Create("Cotizaciones", Columns))
        {
            path = workbook.Complete();
            Assert.True(File.Exists(path));
        }

        Assert.False(File.Exists(path));
    }

    // Una fila con más o menos celdas que columnas es un error del procesador, no un dato: correría
    // las columnas sin que nadie lo note.
    [Fact]
    public void ARowWithTheWrongNumberOfCellsIsRejected()
    {
        using var workbook = new OpenXmlExportWorkbookWriter().Create("Cotizaciones", Columns);

        Assert.Throws<ArgumentException>(() => workbook.AppendRow([ExportCell.OfText("solo una")]));
    }

    private sealed record CellSnapshot(string Text, bool IsNumber, uint? StyleIndex);

    private sealed record SheetSnapshot(
        string Name,
        IReadOnlyList<IReadOnlyList<CellSnapshot>> Rows,
        IReadOnlyList<double> Widths,
        bool HeaderIsFrozen,
        bool StyleOneIsBold);

    private static SheetSnapshot Read(string path)
    {
        using var document = SpreadsheetDocument.Open(path, isEditable: false);
        var workbookPart = document.WorkbookPart!;
        var sheet = workbookPart.Workbook.Sheets!.Elements<Sheet>().Single();
        var worksheet = ((WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!)).Worksheet;

        var rows = worksheet.GetFirstChild<SheetData>()!
            .Elements<Row>()
            .Select(row => (IReadOnlyList<CellSnapshot>)row.Elements<Cell>()
                .Select(cell => new CellSnapshot(
                    cell.InlineString?.Text?.Text ?? cell.CellValue?.Text ?? string.Empty,
                    cell.DataType?.Value == CellValues.Number,
                    cell.StyleIndex?.Value))
                .ToArray())
            .ToArray();
        var widths = worksheet.GetFirstChild<Columns>()!
            .Elements<Column>()
            .Select(column => column.Width!.Value)
            .ToArray();
        var pane = worksheet.GetFirstChild<SheetViews>()?.GetFirstChild<SheetView>()?.GetFirstChild<Pane>();
        var stylesheet = workbookPart.WorkbookStylesPart!.Stylesheet;
        var headerFontId = stylesheet.CellFormats!.Elements<CellFormat>().ElementAt(1).FontId!.Value;
        var headerFont = stylesheet.Fonts!.Elements<Font>().ElementAt((int)headerFontId);

        return new SheetSnapshot(
            sheet.Name!.Value!,
            rows,
            widths,
            pane?.State?.Value == PaneStateValues.Frozen && pane.TopLeftCell?.Value == "A2",
            headerFont.Bold is not null);
    }
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OpenXmlExportWorkbookWriterTests"
```

Esperado: no compila — `error CS0246: The type or namespace name 'OpenXmlExportWorkbookWriter' could not be found` (y `ExportColumn`, `ExportCell`). Hoy `DocumentFormat.OpenXml` ya resuelve transitivo por ClosedXML, así que el `using` del SDK compila. Pegar la salida.

- [ ] **Step 3: Implementar**

`Directory.Packages.props`, entre `coverlet.collector` y `FluentValidation`:

```xml
    <!-- Referencia directa de Quotations.Infrastructure para escribir el Excel en streaming
         (spec 2026-09-12, D8). Misma versión que ya resolvía transitiva por ClosedXML: con el
         pinning transitivo activado, declararla la fija también en Customers y Catalog. -->
    <PackageVersion Include="DocumentFormat.OpenXml" Version="3.1.1" />
```

`Modules.Quotations.Infrastructure.csproj`, en el `ItemGroup` de paquetes (ClosedXML se queda hasta Task 10):

```xml
    <PackageReference Include="DocumentFormat.OpenXml" />
```

`src/Modules/Quotations/Modules.Quotations.Application/IExportWorkbookWriter.cs`:

```csharp
namespace Modules.Quotations.Application;

/// <summary>
/// Escribe el Excel de una exportación fila por fila, sin tenerlo entero en memoria (D8). Puerto
/// y no la librería directa por el mismo motivo que el builder de Catalog: el SDK de OpenXML es
/// una decisión de infraestructura y Application no compila contra él.
/// </summary>
public interface IExportWorkbookWriter
{
    IExportWorkbook Create(string sheetName, IReadOnlyList<ExportColumn> columns);
}

/// <summary>Un archivo en construcción. La cabecera ya está escrita al crearlo.</summary>
public interface IExportWorkbook : IDisposable
{
    /// <summary>Una fila, con una celda por columna y en el orden de las columnas.</summary>
    void AppendRow(IReadOnlyList<ExportCell> cells);

    /// <summary>Cierra el archivo y devuelve la ruta del temporal. Vale hasta <c>Dispose</c>,
    /// que lo borra: quien lo sube lo tiene que hacer antes.</summary>
    string Complete();
}

/// <summary>Una columna con su encabezado (sin tildes, como Reporting) y su ancho fijo.</summary>
public sealed record ExportColumn(string Header, double Width);

/// <summary>
/// Una celda: texto o número, nunca los dos. Las fechas viajan como texto ISO a propósito; los
/// importes como número, porque quien abre el archivo los suma y filtra.
/// </summary>
public readonly record struct ExportCell(string? Text, decimal? Number)
{
    public static ExportCell OfText(string? value) => new(value ?? string.Empty, null);

    public static ExportCell OfNumber(decimal value) => new(null, value);
}
```

`src/Modules/Quotations/Modules.Quotations.Infrastructure/Excel/OpenXmlExportWorkbookWriter.cs`:

```csharp
using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Modules.Quotations.Application;

namespace Modules.Quotations.Infrastructure.Excel;

/// <summary>
/// El Excel de las exportaciones asíncronas, en streaming (D8). ClosedXML guarda cada celda como
/// objeto hasta el final; con un año de un tenant grande eso son cientos de MB en el pod de 1Gi
/// que comparte con la API. Acá cada fila se escribe al temporal apenas llega y la memoria queda
/// acotada al lote que el procesador tiene en la mano.
/// </summary>
internal sealed class OpenXmlExportWorkbookWriter : IExportWorkbookWriter
{
    public IExportWorkbook Create(string sheetName, IReadOnlyList<ExportColumn> columns) =>
        OpenXmlExportWorkbook.Start(sheetName, columns);
}

internal sealed class OpenXmlExportWorkbook : IExportWorkbook
{
    // Índice 1 de CellFormats: la fuente en negrita de BuildStylesheet.
    private const uint HeaderStyleIndex = 1;

    private readonly string _path;
    private readonly SpreadsheetDocument _document;
    private readonly OpenXmlWriter _writer;
    private readonly int _columnCount;
    private uint _nextRowIndex = 1;
    private bool _closed;

    private OpenXmlExportWorkbook(
        string path, SpreadsheetDocument document, OpenXmlWriter writer, int columnCount)
    {
        _path = path;
        _document = document;
        _writer = writer;
        _columnCount = columnCount;
    }

    public static OpenXmlExportWorkbook Start(string sheetName, IReadOnlyList<ExportColumn> columns)
    {
        var path = Path.Combine(Path.GetTempPath(), $"qep-export-{Guid.NewGuid():N}.xlsx");
        var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        try
        {
            var workbookPart = document.AddWorkbookPart();
            var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
            stylesPart.Stylesheet = BuildStylesheet();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            workbookPart.Workbook = new Workbook(new Sheets(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1U,
                Name = sheetName,
            }));

            // La hoja se escribe con OpenXmlWriter y nunca se toca worksheetPart.Worksheet: el
            // autoguardado del documento reescribiría la parte con un DOM vacío. Workbook y estilos
            // sí van por DOM —son chicos— y se guardan solos al cerrar el documento.
            var writer = OpenXmlWriter.Create(worksheetPart);
            writer.WriteStartElement(new Worksheet());
            writer.WriteElement(FrozenHeaderView());
            writer.WriteElement(new Columns(columns.Select((column, index) => new Column
            {
                Min = (uint)(index + 1),
                Max = (uint)(index + 1),
                Width = column.Width,
                CustomWidth = true,
            })));
            writer.WriteStartElement(new SheetData());

            var workbook = new OpenXmlExportWorkbook(path, document, writer, columns.Count);
            workbook.WriteRow(columns.Select(column => ExportCell.OfText(column.Header)).ToArray(), HeaderStyleIndex);
            return workbook;
        }
        catch
        {
            document.Dispose();
            File.Delete(path);
            throw;
        }
    }

    public void AppendRow(IReadOnlyList<ExportCell> cells)
    {
        if (cells.Count != _columnCount)
        {
            throw new ArgumentException(
                $"The row has {cells.Count} cells but the sheet has {_columnCount} columns.", nameof(cells));
        }

        WriteRow(cells, styleIndex: null);
    }

    public string Complete()
    {
        if (!_closed)
        {
            _writer.WriteEndElement(); // SheetData
            _writer.WriteEndElement(); // Worksheet
            _writer.Close();
            _document.Dispose();
            _closed = true;
        }

        return _path;
    }

    public void Dispose()
    {
        if (!_closed)
        {
            _writer.Dispose();
            _document.Dispose();
            _closed = true;
        }

        // File.Delete no falla si el archivo no está.
        File.Delete(_path);
    }

    private void WriteRow(IReadOnlyList<ExportCell> cells, uint? styleIndex)
    {
        var rowIndex = _nextRowIndex++;
        var rowNumber = rowIndex.ToString(CultureInfo.InvariantCulture);
        _writer.WriteStartElement(new Row { RowIndex = rowIndex });
        for (var index = 0; index < cells.Count; index++)
        {
            _writer.WriteElement(ToCell(cells[index], ColumnName(index) + rowNumber, styleIndex));
        }

        _writer.WriteEndElement();
    }

    // Texto inline y no la tabla de strings compartidos: la tabla se arma en memoria hasta el
    // final, que es justo lo que el streaming evita.
    private static Cell ToCell(ExportCell value, string reference, uint? styleIndex)
    {
        var cell = value.Number is { } number
            ? new Cell { DataType = CellValues.Number, CellValue = new CellValue(number) }
            : new Cell
            {
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text(value.Text ?? string.Empty)
                {
                    Space = SpaceProcessingModeValues.Preserve,
                }),
            };
        cell.CellReference = reference;
        if (styleIndex is { } style)
        {
            cell.StyleIndex = style;
        }

        return cell;
    }

    // 0 → A, 25 → Z, 26 → AA.
    private static string ColumnName(int index)
    {
        var name = string.Empty;
        for (var remaining = index + 1; remaining > 0; remaining = (remaining - 1) / 26)
        {
            name = (char)('A' + ((remaining - 1) % 26)) + name;
        }

        return name;
    }

    // La cabecera queda fija al hacer scroll, igual que en los Excel de Reporting.
    private static SheetViews FrozenHeaderView() =>
        new(new SheetView(
            new Pane
            {
                VerticalSplit = 1D,
                TopLeftCell = "A2",
                ActivePane = PaneValues.BottomLeft,
                State = PaneStateValues.Frozen,
            },
            new Selection { Pane = PaneValues.BottomLeft })
        {
            WorkbookViewId = 0U,
        });

    // Lo mínimo que Excel acepta sin quejarse: dos fuentes (normal y negrita), los dos rellenos
    // que la especificación exige, un borde vacío y dos formatos de celda.
    private static Stylesheet BuildStylesheet() =>
        new(
            new Fonts(new Font(), new Font(new Bold())) { Count = 2U },
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }))
            {
                Count = 2U,
            },
            new Borders(new Border(
                new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder()))
            {
                Count = 1U,
            },
            new CellFormats(
                new CellFormat { FontId = 0U, FillId = 0U, BorderId = 0U },
                new CellFormat { FontId = 1U, FillId = 0U, BorderId = 0U, ApplyFont = true })
            {
                Count = 2U,
            });
}
```

> Si el analizador marca `CA2213` sobre `_writer`/`_document` por la guarda de `Dispose`, sacar la guarda: `OpenXmlWriter` y `SpreadsheetDocument` toleran un segundo cierre.

En `QuotationsInfrastructureExtensions.cs`, junto a lo agregado en Task 4:

```csharp
        // Sin estado: una instancia por proceso alcanza. Cada export crea su propio temporal.
        services.AddSingleton<IExportWorkbookWriter, OpenXmlExportWorkbookWriter>();
```

- [ ] **Step 4: Correr y verificar que pasan**

```powershell
dotnet restore
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OpenXmlExportWorkbookWriterTests"
```

Esperado: `Passed! - Failed: 0, Passed: 5`. El `restore` normal actualiza los lock files en local; **no se stagean acá**: se regeneran con `--force-evaluate` en Task 10, cuando además sale ClosedXML. Pegar la salida.

- [ ] **Step 5: Stage** (Git Bash; el commit sale en Task 11)

```bash
test "$(git branch --show-current)" = "feature/export-asincrono" || { echo ABORT; exit 1; }
git add Directory.Packages.props \
  src/Modules/Quotations/Modules.Quotations.Infrastructure/Modules.Quotations.Infrastructure.csproj \
  src/Modules/Quotations/Modules.Quotations.Application/IExportWorkbookWriter.cs \
  src/Modules/Quotations/Modules.Quotations.Infrastructure/Excel/OpenXmlExportWorkbookWriter.cs \
  src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs \
  tests/Modules/Quotations/Modules.Quotations.UnitTests/OpenXmlExportWorkbookWriterTests.cs
```

---

### Task 9: `IExportFileStorage` con clave por `jobId`

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/IExportFileStorage.cs`
- Create: `src/Bootstrapper/ExportFileStorage.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:414` (después de `ICustomerExportStorage`)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportFileStorageTests.cs`

**Interfaces:**
- Consumes: `IObjectStorage.UploadAsync(string key, byte[] content, string contentType, CancellationToken)`, `IObjectStorage.CreatePresignedDownloadUrlAsync(string key, TimeSpan expiry, string? downloadFileName, CancellationToken)`, `StorageOptions.ExportUrlHours` (`StorageOptions.cs:15`; la misma opción que lee `CustomerExportStorage.cs:49`), `IClock` (existentes).
- Produces:
  - `public interface IExportFileStorage { Task<ExportFileUpload> UploadAsync(Guid tenantId, Guid jobId, string fileName, string filePath, CancellationToken cancellationToken); }`
  - `public sealed record ExportFileUpload(string DownloadUrl, DateTimeOffset ExpiresAt)`
  - Clave: `exports/tenants/{tenantId:N}/jobs/{jobId:N}.xlsx` (`internal static string ExportFileStorage.KeyFor(Guid tenantId, Guid jobId)`).

- [ ] **Step 1: Escribir la prueba que falla**

`tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportFileStorageTests.cs`:

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// D9: el archivo va bajo `exports/` —el prefijo de la regla de lifecycle que ya existe en el
/// bucket (D13)— con el id del job en la clave. Un reintento pisa el mismo objeto en vez de dejar
/// basura, y el enlace firmado vence a las `Storage:ExportUrlHours` horas, la misma opción que el
/// export de clientes.
/// </summary>
public sealed class ExportFileStorageTests
{
    [Fact]
    public async Task UploadsUnderTheExportsPrefixWithTheJobIdAndSignsForExportUrlHours()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        // 48 y no el default de 24: si el adaptador fijara la vigencia a mano en vez de leer
        // Storage:ExportUrlHours, esta prueba lo vería.
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.UseSetting("Storage:ExportUrlHours", "48"));
        var tenantId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        byte[] content = [0x50, 0x4B, 0x03, 0x04];
        var path = await WriteTempFileAsync(content);

        try
        {
            var upload = await UploadAsync(factory, tenantId, jobId, path);

            // Bajo `exports/`: fuera de ese prefijo la regla `expire-exports` no lo ve y el objeto
            // queda para siempre (README § Reportes exportados).
            Assert.StartsWith("https://r2.test/exports/", upload.DownloadUrl, StringComparison.Ordinal);
            var key = $"exports/tenants/{tenantId:N}/jobs/{jobId:N}.xlsx";
            Assert.Equal($"https://r2.test/{key}", upload.DownloadUrl);
            Assert.Equal(content, await baseFactory.ObjectStorage.DownloadAsync(key, TestContext.Current.CancellationToken));
            Assert.InRange(
                upload.ExpiresAt,
                DateTimeOffset.UtcNow.AddHours(47),
                DateTimeOffset.UtcNow.AddHours(49));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ARetryOfTheSameJobOverwritesTheSameObject()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var first = await WriteTempFileAsync([0x01]);
        byte[] retried = [0x02, 0x03];
        var second = await WriteTempFileAsync(retried);

        try
        {
            var firstUpload = await UploadAsync(factory, tenantId, jobId, first);
            var secondUpload = await UploadAsync(factory, tenantId, jobId, second);

            Assert.Equal(firstUpload.DownloadUrl, secondUpload.DownloadUrl);
            Assert.Equal(
                retried,
                await factory.ObjectStorage.DownloadAsync(
                    $"exports/tenants/{tenantId:N}/jobs/{jobId:N}.xlsx", TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    private static async Task<ExportFileUpload> UploadAsync(
        WebApplicationFactory<Program> factory, Guid tenantId, Guid jobId, string path)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IExportFileStorage>().UploadAsync(
            tenantId, jobId, "cotizaciones-2026-09-12-1530.xlsx", path, TestContext.Current.CancellationToken);
    }

    private static async Task<string> WriteTempFileAsync(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"qep-storage-test-{Guid.NewGuid():N}.xlsx");
        await File.WriteAllBytesAsync(path, content, TestContext.Current.CancellationToken);
        return path;
    }
}
```

- [ ] **Step 2: Correr y verificar que falla**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~ExportFileStorageTests"
```

Esperado: no compila — `error CS0246: The type or namespace name 'IExportFileStorage' could not be found` (y `ExportFileUpload`). Pegar la salida.

- [ ] **Step 3: Implementar**

`src/Modules/Quotations/Modules.Quotations.Application/IExportFileStorage.cs`:

```csharp
namespace Modules.Quotations.Application;

/// <summary>
/// Sube el Excel de una exportación y firma su enlace de descarga (D9). Puerto en Application y
/// adaptador en el composition root, igual que <c>ICustomerExportStorage</c>: Quotations no puede
/// referenciar Storage (QuotationsLayerTests lo impide).
///
/// Recibe la ruta del temporal y no los bytes: el procesador escribe a disco para no tener el
/// archivo en memoria, y si algún día Storage acepta un stream, el cambio queda en el adaptador.
/// </summary>
public interface IExportFileStorage
{
    Task<ExportFileUpload> UploadAsync(
        Guid tenantId,
        Guid jobId,
        string fileName,
        string filePath,
        CancellationToken cancellationToken);
}

/// <summary>El enlace y hasta cuándo sirve: el correo tiene que decir el vencimiento.</summary>
public sealed record ExportFileUpload(string DownloadUrl, DateTimeOffset ExpiresAt);
```

`src/Bootstrapper/ExportFileStorage.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.Extensions.Options;
using Modules.Quotations.Application;
using Modules.Storage.Application;
using Modules.Storage.Infrastructure;

namespace Bootstrapper;

/// <summary>
/// Adapta el almacenamiento de objetos de Storage al puerto de exportaciones de Quotations. Calcado
/// de <see cref="CustomerExportStorage"/>, con una diferencia a propósito: la clave lleva el id del
/// job y no un identificador aleatorio, así que un reintento pisa el mismo objeto (D9). Sigue sin
/// ser adivinable desde afuera: el id es un UUID v7 que sólo conocen la tabla y el correo.
///
/// El objeto va bajo `exports/` a propósito: es el prefijo de la regla de lifecycle que ya existe
/// en el bucket privado (`expire-exports`, README § Reportes exportados), y una clave fuera de él
/// no la borraría nadie. La vigencia es `Storage:ExportUrlHours`, la misma opción que lee
/// CustomerExportStorage: no hay una vigencia propia de estos exports.
/// </summary>
internal sealed class ExportFileStorage(
    IObjectStorage objectStorage,
    IOptions<StorageOptions> options,
    IClock clock)
    : IExportFileStorage
{
    private const string ExcelContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    internal static string KeyFor(Guid tenantId, Guid jobId) =>
        $"exports/tenants/{tenantId:N}/jobs/{jobId:N}.xlsx";

    public async Task<ExportFileUpload> UploadAsync(
        Guid tenantId,
        Guid jobId,
        string fileName,
        string filePath,
        CancellationToken cancellationToken)
    {
        var key = KeyFor(tenantId, jobId);

        // A bytes porque IObjectStorage sólo sube byte[]. Es el .xlsx comprimido —órdenes de
        // magnitud menos que el grafo de celdas que armaba ClosedXML—, no las filas.
        var content = await File.ReadAllBytesAsync(filePath, cancellationToken);
        await objectStorage.UploadAsync(key, content, ExcelContentType, cancellationToken);

        var expiry = TimeSpan.FromHours(options.Value.ExportUrlHours);
        var url = await objectStorage.CreatePresignedDownloadUrlAsync(
            key, expiry, fileName, cancellationToken);

        // `AbsoluteUri`, nunca `ToString()`: ToString desescapa el query string y rompe la firma
        // (ver CustomerExportStorage).
        return new ExportFileUpload(url.AbsoluteUri, clock.UtcNow.Add(expiry));
    }
}
```

En `QepServiceCollectionExtensions.cs`, después de `services.AddScoped<ICustomerExportStorage, CustomerExportStorage>();` (`:414`):

```csharp
        // Y entre `quotations` y `storage`, para el Excel de las exportaciones asíncronas.
        services.AddScoped<IExportFileStorage, ExportFileStorage>();
```

- [ ] **Step 4: Correr y verificar que pasan**

```powershell
dotnet build --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build --filter "FullyQualifiedName~ExportFileStorageTests"
```

Esperado: `Passed! - Failed: 0, Passed: 2`. Pegar la salida.

- [ ] **Step 5: Stage** (Git Bash)

```bash
test "$(git branch --show-current)" = "feature/export-asincrono" || { echo ABORT; exit 1; }
git add src/Modules/Quotations/Modules.Quotations.Application/IExportFileStorage.cs \
  src/Bootstrapper/ExportFileStorage.cs \
  src/Bootstrapper/QepServiceCollectionExtensions.cs \
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportFileStorageTests.cs
```

---

### Task 10: `POST /quotations/export` encola; sale el `GET` y ClosedXML

**Files:**
- Modify (reescritura): `src/Modules/Quotations/Modules.Quotations.Application/ExportQuotations.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Application/ExportJobSupport.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationRepository.cs` (después de `ListForExportAsync` `:35-49`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationRepository.cs` (después de `ListForExportAsync` `:65-80`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationsDtos.cs` (al final)
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/QuotationEndpoints.cs` (`:11-14` constante, `:28-37` mapeo, `:184-203` handler)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:298-300`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs:44-45`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Modules.Quotations.Infrastructure.csproj` (sale `ClosedXML`)
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj` (sale `ClosedXML`)
- Delete: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationExportWorkbookBuilder.cs`
- Delete: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Excel/ClosedXmlQuotationExportBuilder.cs`
- Modify: todos los `packages.lock.json` que cambie `dotnet restore --force-evaluate`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs` (`:128-197`, `:268-354`)
- Test (reescritura): `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportQuotationsHandlerTests.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportQuotationsValidatorTests.cs:60-61`
- Test (reescritura): `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationExportApiTests.cs`

**Interfaces:**
- Consumes: `IExportJobQueue`, `ExportJobLimits` (Task 3); `ExportJob.Enqueue` (Task 1); `QuotationListing.ParseStatus`, `QuotationListing.ResolveClientIdsByNitAsync` (existentes); harness `FindExportJobAsync` (Task 4); `InMemoryExportJobQueue`, `CountingQuotationsUnitOfWork` (Task 3).
- Produces:
  - `public sealed record ExportQuotationsCommand(Guid TenantId, Guid? ClientId, Guid? AdvisorId, string? Status, DateOnly? CreatedFrom, DateOnly? CreatedTo, string? ClientNit, string? QuotationNumber) : ICommand<ExportJobAccepted>`
  - `public sealed record ExportJobAccepted(Guid JobId, DateTimeOffset RequestedAt)` (lo reusa ventas)
  - `public sealed record QuotationsExportFilters(Guid? ClientId, Guid? AdvisorId, string? Status, DateOnly CreatedFrom, DateOnly CreatedTo, string? ClientNit, string? QuotationNumber)`
  - `ExportQuotationsValidator : AbstractValidator<ExportQuotationsCommand>` (mismas reglas que en `572200c`)
  - `ExportQuotationsHandler : ICommandHandler<ExportQuotationsCommand, ExportJobAccepted>`
  - `public static class ExportJobFilters { static string Serialize<TFilters>(TFilters filters); static TFilters Read<TFilters>(ExportJob job) where TFilters : class; }` — `Read` lanza `ExportJobDefinitiveException` si el JSON no se puede leer.
  - `Task<bool> IQuotationRepository.AnyForExportAsync(Guid tenantId, Guid? clientId, IReadOnlyCollection<Guid>? clientIds, MemberId? advisorId, QuotationStatus? status, DateOnly? createdFrom, DateOnly? createdTo, string? quotationNumber, CancellationToken cancellationToken)`
  - `public sealed record ExportJobAcceptedResponse(Guid JobId, DateTimeOffset RequestedAt)`
  - Dobles: `StubQuotationListRepository.AnyCalls` y `AnyForExportAsync` que registra en `LastExportSearch`.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `QuotationsTestDoubles.cs`:

- Borrar `RecordingQuotationExportWorkbookBuilder` entero (`:279-299`, con su comentario).
- En `StubQuotationRepository`, después de `ListForExportAsync` (`:166-176`):

```csharp
    public Task<bool> AnyForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        QuotationStatus? status,
        DateOnly? createdFrom,
        DateOnly? createdTo,
        string? quotationNumber,
        CancellationToken cancellationToken) =>
        Task.FromResult(true);
```

- En `StubQuotationListRepository`, después de `ListForExportAsync` (`:336-354`):

```csharp
    /// <summary>Cuántas veces se preguntó si había filas. Un pedido rechazado por permiso o
    /// rango no llega a preguntar, y el conteo es la aserción.</summary>
    public int AnyCalls { get; private set; }

    // Mismo contrato de `clientIds` que ListForExportAsync: un NIT sin cliente es "ninguna fila".
    public Task<bool> AnyForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        QuotationStatus? status,
        DateOnly? createdFrom,
        DateOnly? createdTo,
        string? quotationNumber,
        CancellationToken cancellationToken)
    {
        AnyCalls++;
        LastExportSearch = new RecordedExportSearch(
            clientId, clientIds, advisorId, status, createdFrom, createdTo, quotationNumber);
        return Task.FromResult(clientIds is null
            ? quotations.Length > 0
            : quotations.Any(quotation => clientIds.Contains(quotation.ClientId)));
    }
```

- Cambiar el comentario de `RecordedExportSearch` (`:268-269`) a: `/// <summary>Los filtros con que se preguntó por filas o se leyó para exportar.</summary>`.

En `ExportQuotationsValidatorTests.cs`, reemplazar el helper (`:60-61`):

```csharp
    private static ExportQuotationsCommand NewQuery(DateOnly? from, DateOnly? to) =>
        new(Guid.CreateVersion7(), null, null, null, from, to, null, null);
```

`tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportQuotationsHandlerTests.cs` (reemplaza el archivo entero):

```csharp
using System.Text.Json;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El pedido de exportación de cotizaciones (spec 2026-09-12, D4): valida en orden —permiso,
/// rango, que haya filas, límite de pendientes— y recién entonces encola. Nada pesado pasa en el
/// request: el Excel lo arma el worker.
/// </summary>
public sealed class ExportQuotationsHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 15, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly To = new(2026, 9, 12);

    [Fact]
    public async Task ExportForAnotherTenantIsForbiddenAndEnqueuesNothing()
    {
        var repository = new StubQuotationListRepository(NewQuotation());
        var queue = new InMemoryExportJobQueue();
        var handler = NewHandler(
            repository, queue, executionContext: new StubExecutionContext(SubjectId, Guid.CreateVersion7()));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Equal(0, repository.AnyCalls);
        Assert.Empty(queue.Jobs);
    }

    [Fact]
    public async Task ExportWithoutTheReadPermissionIsForbiddenAndEnqueuesNothing()
    {
        var repository = new StubQuotationListRepository(NewQuotation());
        var queue = new InMemoryExportJobQueue();
        var handler = NewHandler(
            repository, queue, executionContext: new PermissionlessExecutionContext(SubjectId, TenantId));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal(0, repository.AnyCalls);
        Assert.Empty(queue.Jobs);
    }

    // D4, paso 1 antes que el 2: a quien no puede exportar no se le contesta que su rango estaba mal.
    [Fact]
    public async Task ExportChecksThePermissionBeforeTheRange()
    {
        var handler = NewHandler(
            new StubQuotationListRepository(),
            executionContext: new PermissionlessExecutionContext(SubjectId, TenantId));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(NewCommand(createdFrom: null, createdTo: null), TestContext.Current.CancellationToken));
    }

    // D4, paso 2 antes que el 3: un rango inválido no llega a consultar la base.
    [Fact]
    public async Task ExportWithARangeLongerThanOneYearIsRejectedBeforeReading()
    {
        var repository = new StubQuotationListRepository(NewQuotation());
        var handler = NewHandler(repository);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            handler.HandleAsync(
                NewCommand(createdFrom: new DateOnly(2025, 1, 1), createdTo: new DateOnly(2026, 1, 2)),
                TestContext.Current.CancellationToken));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "CreatedTo");
        Assert.Equal(0, repository.AnyCalls);
    }

    [Fact]
    public async Task ExportWithAnInvalidStatusFailsLikeTheList()
    {
        var repository = new StubQuotationListRepository(NewQuotation());
        var handler = NewHandler(repository);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewCommand(From, To, status: "NotAStatus"), TestContext.Current.CancellationToken));

        Assert.Equal("quotation.quotation.status_invalid", error.Code);
        Assert.Equal(0, repository.AnyCalls);
    }

    // D4, paso 3: enterarse de que no había nada después de esperar un correo es peor.
    [Fact]
    public async Task ExportWithNoMatchingRowsIsRejectedAndEnqueuesNothing()
    {
        var queue = new InMemoryExportJobQueue();
        var unitOfWork = new CountingQuotationsUnitOfWork();
        var handler = NewHandler(new StubQuotationListRepository(), queue, unitOfWork);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal("quotation.export.empty", error.Code);
        Assert.Empty(queue.Jobs);
        Assert.Equal(0, unitOfWork.Saves);
    }

    // El NIT no vive en Quotation: si no resuelve a ningún cliente, la pregunta recibe una
    // colección vacía —"ninguna fila", no "sin filtro"— y el pedido sale como vacío.
    [Fact]
    public async Task ExportWithAClientNitThatMatchesNoCustomerIsRejectedAsEmpty()
    {
        var repository = new StubQuotationListRepository(NewQuotation());
        var handler = NewHandler(repository);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewCommand(From, To, clientNit: "no-existe-este-nit"), TestContext.Current.CancellationToken));

        Assert.Equal("quotation.export.empty", error.Code);
        Assert.NotNull(repository.LastExportSearch?.ClientIds);
        Assert.Empty(repository.LastExportSearch.ClientIds);
    }

    // D4, paso 3 antes que el 4: sin filas se dice eso, aunque además esté en el límite.
    [Fact]
    public async Task ExportChecksForRowsBeforeThePendingLimit()
    {
        var queue = QueueWithPendingJobsOf(SubjectId, 3);
        var handler = NewHandler(new StubQuotationListRepository(), queue);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal("quotation.export.empty", error.Code);
    }

    // D4, paso 4: tres pendientes de la misma persona, contando los dos tipos.
    [Fact]
    public async Task ExportBeyondThePendingLimitIsRejectedAndEnqueuesNothing()
    {
        var queue = QueueWithPendingJobsOf(SubjectId, 3);
        var unitOfWork = new CountingQuotationsUnitOfWork();
        var handler = NewHandler(new StubQuotationListRepository(NewQuotation()), queue, unitOfWork);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal("quotation.export.pending_limit", error.Code);
        Assert.Equal(3, queue.Jobs.Count);
        Assert.Equal(0, unitOfWork.Saves);
    }

    [Fact]
    public async Task PendingExportsOfSomeoneElseDoNotCount()
    {
        var queue = QueueWithPendingJobsOf(Guid.CreateVersion7(), 3);
        var handler = NewHandler(new StubQuotationListRepository(NewQuotation()), queue);

        await handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        Assert.Equal(4, queue.Jobs.Count);
    }

    [Fact]
    public async Task ExportEnqueuesAPendingJobWithTheValidatedFilters()
    {
        var queue = new InMemoryExportJobQueue();
        var unitOfWork = new CountingQuotationsUnitOfWork();
        var handler = NewHandler(new StubQuotationListRepository(NewQuotation()), queue, unitOfWork);

        var accepted = await handler.HandleAsync(
            new ExportQuotationsCommand(TenantId, ClientId, AdvisorId.Value, "sent", From, To, "900", "0001"),
            TestContext.Current.CancellationToken);

        var job = Assert.Single(queue.Jobs);
        Assert.Equal(new ExportJobAccepted(job.Id, Now), accepted);
        Assert.Equal(ExportJobKind.Quotations, job.Kind);
        Assert.Equal(ExportJobStatus.Pending, job.Status);
        Assert.Equal(TenantId, job.TenantId);
        Assert.Equal(SubjectId, job.RequestedBy);
        Assert.Equal(
            new QuotationsExportFilters(ClientId, AdvisorId.Value, "sent", From, To, "900", "0001"),
            JsonSerializer.Deserialize<QuotationsExportFilters>(job.Filters));
        Assert.Equal(1, unitOfWork.Saves);
    }

    // Un año exacto vale (D3).
    [Fact]
    public async Task ExportAcceptsARangeOfExactlyOneYear()
    {
        var queue = new InMemoryExportJobQueue();
        var handler = NewHandler(new StubQuotationListRepository(NewQuotation()), queue);

        await handler.HandleAsync(
            NewCommand(createdFrom: new DateOnly(2025, 1, 1), createdTo: new DateOnly(2026, 1, 1)),
            TestContext.Current.CancellationToken);

        Assert.Single(queue.Jobs);
    }

    [Fact]
    public async Task ExportAsksForRowsWithTheSameCriteriaAsTheList()
    {
        var repository = new StubQuotationListRepository(NewQuotation());
        var handler = NewHandler(repository);

        await handler.HandleAsync(
            new ExportQuotationsCommand(TenantId, ClientId, AdvisorId.Value, "sent", From, To, ClientNit: null, "0001"),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            new RecordedExportSearch(ClientId, ClientIds: null, AdvisorId, QuotationStatus.Sent, From, To, "0001"),
            repository.LastExportSearch);
    }

    private static ExportQuotationsCommand NewCommand() => NewCommand(From, To);

    // Las fechas sin default a propósito: un `null` explícito es justo lo que ejercen las pruebas
    // del rango obligatorio.
    private static ExportQuotationsCommand NewCommand(
        DateOnly? createdFrom,
        DateOnly? createdTo,
        string? status = null,
        string? clientNit = null) =>
        new(TenantId, ClientId: null, AdvisorId: null, status, createdFrom, createdTo, clientNit, QuotationNumber: null);

    private static InMemoryExportJobQueue QueueWithPendingJobsOf(Guid requestedBy, int count)
    {
        var queue = new InMemoryExportJobQueue();
        for (var index = 0; index < count; index++)
        {
            // Alternados a propósito: el límite cuenta cotizaciones y ventas juntas.
            var kind = index % 2 == 0 ? ExportJobKind.Quotations : ExportJobKind.Sales;
            queue.Add(ExportJob.Enqueue(Guid.CreateVersion7(), TenantId, requestedBy, kind, "{}", Now));
        }

        return queue;
    }

    private static StubQuotationCustomerLookup NewCustomerLookup() =>
        new(new QuotationCustomerRef(
            ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
            "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false));

    private static Quotation NewQuotation() =>
        Quotation.Create(
            QuotationId.New(),
            TenantId,
            "QUO-2026-0001",
            ClientId,
            AdvisorId,
            validUntil: null,
            paymentMethod: null,
            notes: null,
            QuotationParties.Empty,
            billingAccount: null,
            customerWithRetention: false,
            customerVatSurplus: false,
            AdvisorId,
            Now);

    private static ExportQuotationsHandler NewHandler(
        StubQuotationListRepository repository,
        InMemoryExportJobQueue? queue = null,
        CountingQuotationsUnitOfWork? unitOfWork = null,
        IExecutionContext? executionContext = null) =>
        new(repository,
            NewCustomerLookup(),
            queue ?? new InMemoryExportJobQueue(),
            unitOfWork ?? new CountingQuotationsUnitOfWork(),
            new ExportQuotationsValidator(),
            executionContext ?? new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now));
}
```

`tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationExportApiTests.cs` (reemplaza el archivo entero; el camino completo hasta el Excel lo agrega Task 11):

```csharp
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La exportación de cotizaciones por correo (spec 2026-09-12): el POST valida y encola en
/// milisegundos, y el worker arma el Excel después.
/// </summary>
public sealed class QuotationExportApiTests
{
    // También prueba que `/export` no lo captura `/{quotationId:guid}`.
    [Fact]
    public async Task ExportIsAcceptedAndLeavesAPendingJobForWhoAskedForIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        await CreateQuotationAsync(client, tenantId, customerId);

        var response = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<AcceptedDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(accepted);
        var job = await FindExportJobAsync(factory, accepted.JobId);
        Assert.Equal(ExportJobStatus.Pending, job.Status);
        Assert.Equal(ExportJobKind.Quotations, job.Kind);
        Assert.Equal(tenantId, job.TenantId);
        Assert.Equal(ownerUserId, job.RequestedBy);
        Assert.Equal(job.RequestedAt, accepted.RequestedAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task ExportWithoutDatesIsUnprocessableWithTheFieldErrors()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/export", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal("validation.failed", problem?.Code);
        Assert.NotNull(problem?.Errors);
        Assert.Contains("CreatedFrom", problem.Errors.Keys);
        Assert.Contains("CreatedTo", problem.Errors.Keys);
    }

    [Fact]
    public async Task ExportWithARangeLongerThanOneYearIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/export?createdFrom=2025-01-01&createdTo=2026-01-02",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal("validation.failed", problem?.Code);
        Assert.Contains("CreatedTo", problem!.Errors!.Keys);
    }

    [Fact]
    public async Task ExportWithNoMatchingRowsIsUnprocessableAndEnqueuesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal("quotation.export.empty", problem?.Code);
        Assert.Equal(0, await CountExportJobsAsync(factory));
    }

    [Fact]
    public async Task AFourthPendingExportIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        await CreateQuotationAsync(client, tenantId, customerId);
        var url = $"{QuotationsUrl(tenantId)}/export?{CurrentRange()}";

        for (var accepted = 0; accepted < 3; accepted++)
        {
            var ok = await client.PostAsync(url, content: null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        }

        var response = await client.PostAsync(url, content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal("quotation.export.pending_limit", problem?.Code);
        Assert.Equal(3, await CountExportJobsAsync(factory));
    }

    // `QuotationManage` sin `QuotationRead` no alcanza: exportar es leer el listado en otro formato.
    [Fact]
    public async Task ExportWithoutTheReadPermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, QuotationsPermissions.QuotationManage);
        using var _ = client;

        var response = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // El permiso para su tenant no le abre el de otro: eso lo frena el handler, no la política.
    [Fact]
    public async Task ExportForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = owner;
        var (_, _, otherOwner) = await RegisterTenantAsync(factory, QuotationsPermissions.QuotationRead);
        using var __ = otherOwner;

        var response = await otherOwner.PostAsync(
            $"{QuotationsUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // El GET síncrono de 572200c ya no existe: armaba el Excel dentro del request.
    [Fact]
    public async Task TheSynchronousGetExportIsGone()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.GetAsync(
            $"{QuotationsUrl(tenantId)}/export?{CurrentRange()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    private static string CurrentRange()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return $"createdFrom={Iso(today.AddDays(-7))}&createdTo={Iso(today.AddDays(1))}";
    }

    private static string Iso(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<int> CountExportJobsAsync(QepApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<QuotationsDbContext>()
            .ExportJobs.CountAsync(TestContext.Current.CancellationToken);
    }

    private sealed record AcceptedDto(Guid JobId, DateTimeOffset RequestedAt);

    private sealed record ProblemDto(string? Code, Dictionary<string, string[]>? Errors);
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~ExportQuotations"
```

Esperado: no compila — `error CS0246: The type or namespace name 'ExportQuotationsCommand' could not be found` (y `ExportJobAccepted`, `QuotationsExportFilters`). Los dobles sí compilan: `AnyForExportAsync` todavía no está en la interfaz, pero un método público de más no rompe nada. Pegar la salida.

- [ ] **Step 3: Implementar**

`src/Modules/Quotations/Modules.Quotations.Application/ExportJobSupport.cs`:

```csharp
using System.Text.Json;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Los filtros de un job, ida y vuelta a <c>export_jobs.filters</c>. Se guardan crudos —como los
/// mandó la pantalla y ya validados— y el procesador los vuelve a interpretar al generar.
/// </summary>
public static class ExportJobFilters
{
    public static string Serialize<TFilters>(TFilters filters) => JsonSerializer.Serialize(filters);

    /// <summary>D11: unos filtros que no se pueden leer no se arreglan reintentando.</summary>
    public static TFilters Read<TFilters>(ExportJob job)
        where TFilters : class
    {
        try
        {
            return JsonSerializer.Deserialize<TFilters>(job.Filters)
                ?? throw new ExportJobDefinitiveException("UnreadableFilters: the export filters are empty.");
        }
        catch (JsonException exception)
        {
            throw new ExportJobDefinitiveException($"UnreadableFilters: {exception.Message}", exception);
        }
    }
}
```

`src/Modules/Quotations/Modules.Quotations.Application/ExportQuotations.cs` (reemplaza el archivo entero):

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Pide el listado de cotizaciones en un <c>.xlsx</c> por correo (spec 2026-09-12). Comando y no
/// query: encola un job. El Excel lo arma <c>QuotationsExportProcessor</c> en el worker, nunca el
/// request.
///
/// **Los mismos filtros que <see cref="ListQuotationsQuery"/>, menos la paginación**: el archivo
/// tiene que ser lo que la tabla muestra. **Rango obligatorio de a lo sumo un año** en vez de un
/// tope de filas (D3): un tope castiga a quien más vende y no dice qué filtro tocar.
/// </summary>
public sealed record ExportQuotationsCommand(
    Guid TenantId,
    Guid? ClientId,
    Guid? AdvisorId,
    string? Status,
    DateOnly? CreatedFrom,
    DateOnly? CreatedTo,
    string? ClientNit,
    string? QuotationNumber) : ICommand<ExportJobAccepted>;

/// <summary>Lo que se acepta: el job y cuándo. Sin archivo ni filas —todavía no existen— ni
/// enlace: el canal de entrega es el correo (D5).</summary>
public sealed record ExportJobAccepted(Guid JobId, DateTimeOffset RequestedAt);

/// <summary>Los filtros tal como quedan en <c>export_jobs.filters</c>. Las fechas ya no son
/// opcionales: el validador las exigió antes de encolar.</summary>
public sealed record QuotationsExportFilters(
    Guid? ClientId,
    Guid? AdvisorId,
    string? Status,
    DateOnly CreatedFrom,
    DateOnly CreatedTo,
    string? ClientNit,
    string? QuotationNumber);

/// <summary>
/// El rango es la cota de volumen, así que es obligatorio y de a lo sumo un año. Validador y no
/// regla de dominio para que el 422 lleve el mapa <c>errors</c> y la pantalla marque la fecha.
/// "Un año" es <c>AddYears(1)</c> y no 365 días: vale igual en un bisiesto, y un año exacto pasa.
/// </summary>
public sealed class ExportQuotationsValidator : AbstractValidator<ExportQuotationsCommand>
{
    public ExportQuotationsValidator()
    {
        RuleFor(command => command.CreatedFrom)
            .NotNull()
            .WithMessage("createdFrom is required.");
        RuleFor(command => command.CreatedTo)
            .NotNull()
            .WithMessage("createdTo is required.");
        RuleFor(command => command.CreatedTo)
            .GreaterThanOrEqualTo(command => command.CreatedFrom!.Value)
            .When(command => command.CreatedFrom is not null && command.CreatedTo is not null)
            .WithMessage("createdTo must be on or after createdFrom.");
        RuleFor(command => command.CreatedTo)
            .LessThanOrEqualTo(command => command.CreatedFrom!.Value.AddYears(1))
            .When(command => command.CreatedFrom is not null && command.CreatedTo is not null)
            .WithMessage("The range from createdFrom to createdTo cannot exceed one year.");
    }
}

public sealed class ExportQuotationsHandler(
    IQuotationRepository repository,
    IQuotationCustomerLookup customerLookup,
    IExportJobQueue queue,
    IQuotationsUnitOfWork unitOfWork,
    IValidator<ExportQuotationsCommand> validator,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ExportQuotationsCommand, ExportJobAccepted>
{
    public async Task<ExportJobAccepted> HandleAsync(
        ExportQuotationsCommand command,
        CancellationToken cancellationToken)
    {
        // D4, en este orden y todo antes de encolar. 1: tenant y permiso.
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, QuotationsPermissions.QuotationRead);

        // 2: filtros y rango.
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var status = QuotationListing.ParseStatus(command.Status);
        var advisorId = command.AdvisorId is { } advisor ? new MemberId(advisor) : (MemberId?)null;
        var clientIds = await QuotationListing.ResolveClientIdsByNitAsync(
            customerLookup, command.TenantId, command.ClientNit, cancellationToken);

        // 3: al menos una fila. Un EXISTS es barato, y enterarse de que no había nada después de
        // esperar un correo es peor.
        var anyRow = await repository.AnyForExportAsync(
            command.TenantId,
            command.ClientId,
            clientIds,
            advisorId,
            status,
            command.CreatedFrom,
            command.CreatedTo,
            command.QuotationNumber,
            cancellationToken);
        if (!anyRow)
        {
            throw new QuotationsDomainException(
                "quotation.export.empty",
                "There are no quotations matching the export filters.");
        }

        // 4: el límite de pendientes, contando cotizaciones y ventas.
        var pending = await queue.CountPendingAsync(
            command.TenantId, executionContext.SubjectId, cancellationToken);
        if (pending >= ExportJobLimits.PendingPerRequester)
        {
            throw new QuotationsDomainException(
                "quotation.export.pending_limit",
                $"There are already {ExportJobLimits.PendingPerRequester} exports in progress for this user.");
        }

        var job = ExportJob.Enqueue(
            Guid.CreateVersion7(),
            command.TenantId,
            executionContext.SubjectId,
            ExportJobKind.Quotations,
            ExportJobFilters.Serialize(new QuotationsExportFilters(
                command.ClientId,
                command.AdvisorId,
                command.Status,
                command.CreatedFrom!.Value,
                command.CreatedTo!.Value,
                command.ClientNit,
                command.QuotationNumber)),
            clock.UtcNow);
        queue.Add(job);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new ExportJobAccepted(job.Id, job.RequestedAt);
    }
}
```

`IQuotationRepository.cs`, después de `ListForExportAsync` (`:49`):

```csharp
    /// <summary>Si hay al menos una cotización con esos filtros: el paso 3 de D4, antes de encolar
    /// una exportación. Mismos filtros y misma semántica de <c>clientIds</c> que
    /// <see cref="SearchAsync"/>.</summary>
    Task<bool> AnyForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        QuotationStatus? status,
        DateOnly? createdFrom,
        DateOnly? createdTo,
        string? quotationNumber,
        CancellationToken cancellationToken);
```

`QuotationRepository.cs`, después de `ListForExportAsync` (`:80`):

```csharp
    // Un EXISTS sobre el mismo FilteredQuery: el pedido de exportación pregunta si hay algo sin
    // traerse ninguna fila.
    public Task<bool> AnyForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        QuotationStatus? status,
        DateOnly? createdFrom,
        DateOnly? createdTo,
        string? quotationNumber,
        CancellationToken cancellationToken) =>
        FilteredQuery(
                tenantId, clientId, clientIds, advisorId, status, createdFrom, createdTo, quotationNumber)
            .AnyAsync(cancellationToken);
```

`QuotationsDtos.cs`, al final del archivo:

```csharp
/// <summary>
/// El 202 de las exportaciones por correo (spec 2026-09-12, D5), de cotizaciones y de ventas. No
/// lleva nombre de archivo ni cantidad de filas porque todavía no existen, ni enlace porque el
/// canal de entrega es el correo: con el enlace acá, la pantalla tomaría el atajo y el correo
/// quedaría sin ejercitar. El jobId es para soporte y para una futura "mis exportaciones" (D15).
/// </summary>
public sealed record ExportJobAcceptedResponse(Guid JobId, DateTimeOffset RequestedAt);
```

`QuotationEndpoints.cs`:

- Borrar la constante `ExcelContentType` y su comentario (`:11-14`): sólo la usaba el `GET`.
- Reemplazar el bloque del `GET /export` (`:28-37`) por:

```csharp
        // El listado en un .xlsx por correo (spec 2026-09-12). POST porque tiene efecto —encola un
        // job—; los filtros van por query string, los mismos que `GET /`, para que el archivo sea
        // lo que la tabla muestra. 202 porque lo aceptado es la solicitud: el archivo lo arma
        // ExportJobWorker y llega por correo. Mismo permiso que el listado: son los mismos datos.
        // `/export` no choca con `/{quotationId:guid}`: la restricción de guid no lo acepta.
        group.MapPost("/export", ExportQuotationsAsync)
            .RequireAuthorization(QuotationsPermissions.QuotationRead)
            .Produces<ExportJobAcceptedResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
```

- Reemplazar `ExportQuotationsAsync` (`:184-203`) por:

```csharp
    private static async Task<IResult> ExportQuotationsAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        Guid? clientId = null,
        Guid? advisorId = null,
        string? status = null,
        DateOnly? createdFrom = null,
        DateOnly? createdTo = null,
        string? clientNit = null,
        string? quotationNumber = null)
    {
        var accepted = await dispatcher.SendAsync(
            new ExportQuotationsCommand(
                tenantId, clientId, advisorId, status, createdFrom, createdTo, clientNit,
                quotationNumber),
            cancellationToken);

        return Results.Accepted(value: new ExportJobAcceptedResponse(accepted.JobId, accepted.RequestedAt));
    }
```

`QepServiceCollectionExtensions.cs:298-300`, reemplazar el registro de la query por:

```csharp
        // Comando y no query desde la exportación asíncrona: encola un job (spec 2026-09-12).
        services.AddScoped<
            ICommandHandler<ExportQuotationsCommand, ExportJobAccepted>,
            ExportQuotationsHandler>();
```

`QuotationsInfrastructureExtensions.cs:44-45`: borrar las dos líneas del builder de ClosedXML (comentario incluido).

Borrar los dos archivos del export síncrono:

```powershell
Remove-Item src/Modules/Quotations/Modules.Quotations.Application/IQuotationExportWorkbookBuilder.cs
Remove-Item src/Modules/Quotations/Modules.Quotations.Infrastructure/Excel/ClosedXmlQuotationExportBuilder.cs
```

`Modules.Quotations.Infrastructure.csproj`: borrar `<PackageReference Include="ClosedXML" />`.

`Modules.Quotations.IntegrationTests.csproj`: borrar `<PackageReference Include="ClosedXML" />` y su comentario (el workbook se lee con el SDK de OpenXML desde Task 11).

Lock files, en el mismo commit que el cambio de paquetes:

```powershell
dotnet restore --force-evaluate
dotnet restore --locked-mode
git diff --name-only -- '*packages.lock.json'
git diff -U0 -- '*packages.lock.json' | Select-String '"resolved"'
```

Esperado: el `--locked-mode` pasa; la lista incluye al menos `src/Modules/Quotations/Modules.Quotations.Infrastructure/packages.lock.json`, `src/Api/packages.lock.json`, `src/Bootstrapper/packages.lock.json` y los de los proyectos de prueba que referencian Api (hallazgo 9: pueden aparecer también Customers y Catalog por el pinning transitivo). Si el último comando muestra alguna línea `"resolved"`, comparar la versión vieja con la nueva: **tienen que ser iguales** (`DocumentFormat.OpenXml` en `3.1.1`, `ClosedXML` en `0.105.1` donde siga). Si alguna versión se movió, parar y preguntar.

- [ ] **Step 4: Correr y verificar que pasan**

```powershell
dotnet build --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-build
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build --filter "FullyQualifiedName~QuotationExportApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: todo en verde; `QuotationExportApiTests` con `Passed: 8`. Pegar las salidas.

- [ ] **Step 5: Stage** (Git Bash)

```bash
test "$(git branch --show-current)" = "feature/export-asincrono" || { echo ABORT; exit 1; }
git add src/Modules/Quotations/Modules.Quotations.Application/ExportQuotations.cs \
  src/Modules/Quotations/Modules.Quotations.Application/ExportJobSupport.cs \
  src/Modules/Quotations/Modules.Quotations.Application/IQuotationRepository.cs \
  src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationRepository.cs \
  src/Modules/Quotations/Modules.Quotations.Application/QuotationsDtos.cs \
  src/Modules/Quotations/Modules.Quotations.Api/QuotationEndpoints.cs \
  src/Bootstrapper/QepServiceCollectionExtensions.cs \
  src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs \
  src/Modules/Quotations/Modules.Quotations.Infrastructure/Modules.Quotations.Infrastructure.csproj \
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj \
  src/Modules/Quotations/Modules.Quotations.Application/IQuotationExportWorkbookBuilder.cs \
  src/Modules/Quotations/Modules.Quotations.Infrastructure/Excel/ClosedXmlQuotationExportBuilder.cs \
  tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs \
  tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportQuotationsHandlerTests.cs \
  tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportQuotationsValidatorTests.cs \
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationExportApiTests.cs
git add -- $(git diff --name-only -- '*packages.lock.json')
git diff --cached --name-only -- '*packages.lock.json'
```

`git add` de una ruta borrada stagea el borrado. La última línea lista los lock files stageados: tienen que ser los mismos que mostró el Step 3.

---

### Task 11: `QuotationsExportProcessor` y commit de cotizaciones

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/QuotationsExportProcessor.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ExportJobSupport.cs` (agrega `ExportFileNames`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationRepository.cs:35-49` (`ListForExportAsync` por keyset; `QuotationExportCursor` al final del archivo)
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationRepository.cs:65-80`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (junto a `ExportJobRunner`, Task 4)
- Modify: `README.md:840-844`
- Create: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportWorkbookReader.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs` (`ListForExportAsync` de los dos repositorios)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportTestDoubles.cs` (writer y storage de prueba)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsExportProcessorTests.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationExportApiTests.cs` (prueba de punta a punta)

**Interfaces:**
- Consumes: `ExportJobFilters.Read<QuotationsExportFilters>`, `QuotationsExportFilters` (Task 10); `IExportWorkbookWriter`, `ExportColumn`, `ExportCell` (Task 8); `IExportFileStorage` (Task 9); `ExportJobLimits.BatchSize`, `IExportJobProcessor`, `ExportJobResult`, `ExportJobDefinitiveException` (Task 3); `QuotationListing.ParseStatus/ResolveClientIdsByNitAsync/ToListItemsAsync` (existentes); harness `RunExportJobAsync`, `FindExportJobAsync`, `OutboxMessagesAsync` (Task 4).
- Produces:
  - `public sealed record QuotationExportCursor(DateTimeOffset CreatedAt, string QuotationNumber)` — la clave de la última fila leída.
  - `Task<IReadOnlyList<Quotation>> IQuotationRepository.ListForExportAsync(Guid tenantId, Guid? clientId, IReadOnlyCollection<Guid>? clientIds, MemberId? advisorId, QuotationStatus? status, DateOnly? createdFrom, DateOnly? createdTo, string? quotationNumber, QuotationExportCursor? after, int limit, CancellationToken cancellationToken)` — keyset sobre `(CreatedAt DESC, QuotationNumber DESC)`; `after` en `null` es el primer lote (hallazgo 11).
  - Doble: `StubQuotationListRepository.ExportCursors` (`List<QuotationExportCursor?>`, la clave con que se pidió cada lote; la reusa Task 13 como `StubSaleListRepository.ExportCursors`).
  - `public static class ExportFileNames { static string For(string prefix, DateTimeOffset generatedAt); }` → `cotizaciones-2026-09-12-1530.xlsx`.
  - `public sealed class QuotationsExportProcessor : IExportJobProcessor` con `const string SheetName = "Cotizaciones"`, `const string FilePrefix = "cotizaciones"`, `static readonly IReadOnlyList<ExportColumn> Columns`.
  - Dobles: `RecordingExportWorkbookWriter`, `RecordingExportFileStorage` (los reusa Task 13).
  - `internal static class ExportWorkbookReader { static ExportWorkbookSheet Read(byte[] content); }` y `internal sealed record ExportWorkbookSheet(string Name, IReadOnlyList<IReadOnlyList<string>> Rows, IReadOnlyList<IReadOnlyList<bool>> NumericCells)` (los reusa Task 13).

- [ ] **Step 1: Escribir las pruebas que fallan**

En `QuotationsTestDoubles.cs`, cambiar la firma de los dos `ListForExportAsync` agregando `QuotationExportCursor? after, int limit` antes del `CancellationToken`:

- En `StubQuotationRepository` (`:166-176`): el cuerpo pasa a `Task.FromResult<IReadOnlyList<Quotation>>(after is null ? [quotation] : [])`.
- En `StubQuotationListRepository` (`:325-354`): agregar la propiedad junto a `ExportCalls` y reemplazar el cuerpo de `ListForExportAsync` por el mismo keyset que la consulta real, en memoria:

```csharp
    /// <summary>La clave con que se pidió cada lote, en orden: el primero sin clave y cada uno de
    /// los siguientes con la de la última fila del anterior (keyset, D8).</summary>
    public List<QuotationExportCursor?> ExportCursors { get; } = [];

    // Honra el contrato de `clientIds` (vacio = ninguna fila) porque es lo que hace que un NIT
    // sin cliente termine en un Excel vacio, y el orden y la condición del keyset porque es lo que
    // hace que el procesador corte. El resto de los filtros son de la consulta SQL y los cubren
    // las pruebas de integracion.
    public Task<IReadOnlyList<Quotation>> ListForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        QuotationStatus? status,
        DateOnly? createdFrom,
        DateOnly? createdTo,
        string? quotationNumber,
        QuotationExportCursor? after,
        int limit,
        CancellationToken cancellationToken)
    {
        ExportCalls++;
        ExportCursors.Add(after);
        LastExportSearch = new RecordedExportSearch(
            clientId, clientIds, advisorId, status, createdFrom, createdTo, quotationNumber);
        IReadOnlyList<Quotation> rows = (clientIds is null
                ? quotations
                : quotations.Where(quotation => clientIds.Contains(quotation.ClientId)))
            .Where(quotation => after is null
                || quotation.CreatedAt < after.CreatedAt
                || (quotation.CreatedAt == after.CreatedAt
                    && string.CompareOrdinal(quotation.QuotationNumber, after.QuotationNumber) < 0))
            .OrderByDescending(quotation => quotation.CreatedAt)
            .ThenByDescending(quotation => quotation.QuotationNumber, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
        return Task.FromResult(rows);
    }
```

Al final de `ExportTestDoubles.cs`:

```csharp
/// <summary>Anota lo que se escribe en vez de armar un Excel: lo que verifican las pruebas de los
/// procesadores es qué filas y en qué orden; la forma de la hoja la cubre
/// OpenXmlExportWorkbookWriterTests.</summary>
internal sealed class RecordingExportWorkbookWriter : IExportWorkbookWriter
{
    public const string CompletedPath = "recorded-export.xlsx";

    public string? SheetName { get; private set; }

    public IReadOnlyList<ExportColumn> Columns { get; private set; } = [];

    public List<IReadOnlyList<ExportCell>> Rows { get; } = [];

    public bool Disposed { get; private set; }

    public IExportWorkbook Create(string sheetName, IReadOnlyList<ExportColumn> columns)
    {
        SheetName = sheetName;
        Columns = columns;
        return new RecordingWorkbook(this);
    }

    private sealed class RecordingWorkbook(RecordingExportWorkbookWriter owner) : IExportWorkbook
    {
        public void AppendRow(IReadOnlyList<ExportCell> cells) => owner.Rows.Add(cells);

        public string Complete() => CompletedPath;

        public void Dispose() => owner.Disposed = true;
    }
}

internal sealed record RecordedUpload(Guid TenantId, Guid JobId, string FileName, string FilePath);

internal sealed class RecordingExportFileStorage : IExportFileStorage
{
    public RecordedUpload? Upload { get; private set; }

    public Exception? Failure { get; set; }

    public Task<ExportFileUpload> UploadAsync(
        Guid tenantId, Guid jobId, string fileName, string filePath, CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            return Task.FromException<ExportFileUpload>(Failure);
        }

        Upload = new RecordedUpload(tenantId, jobId, fileName, filePath);
        return Task.FromResult(new ExportFileUpload(
            $"https://r2.test/exports/tenants/{tenantId:N}/jobs/{jobId:N}.xlsx",
            StubExportJobProcessor.LinkExpiresAt));
    }
}
```

`tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsExportProcessorTests.cs`:

```csharp
using System.Globalization;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El procesador de cotizaciones: lee por lotes con el mismo filtro que el listado, escribe las
/// columnas de la tabla en su orden y sube el archivo con el id del job. Y clasifica sus fallos
/// (D11): lo que no se arregla reintentando es definitivo.
/// </summary>
public sealed class QuotationsExportProcessorTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 15, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly To = new(2026, 9, 12);

    [Fact]
    public async Task WritesTheListColumnsInTheirOrder()
    {
        var writer = new RecordingExportWorkbookWriter();
        var processor = NewProcessor(new StubQuotationListRepository(NewQuotation("QUO-2026-0001")), writer);

        await processor.ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Cotizaciones", writer.SheetName);
        Assert.Equal(
            ["Numero", "Fecha", "Cliente", "Asesor", "Estado", "Moneda", "Total"],
            writer.Columns.Select(column => column.Header));
        var row = Assert.Single(writer.Rows);
        Assert.Equal("QUO-2026-0001", row[0].Text);
        Assert.Equal(Now.ToString("O", CultureInfo.InvariantCulture), row[1].Text);
        Assert.Equal("Ferretería El Tornillo", row[2].Text);
        // El correo y no el nombre, igual que la tabla (spec 2026-09-11, D1).
        Assert.Equal("asesora@qcode.co", row[3].Text);
        Assert.Equal("Draft", row[4].Text);
        Assert.Equal("COP", row[5].Text);
        Assert.Equal(0m, row[6].Number);
        Assert.Null(row[6].Text);
        Assert.True(writer.Disposed);
    }

    // D8: la memoria queda acotada al lote. Mil y una filas son dos consultas.
    [Fact]
    public async Task ReadsInBatchesOfAThousandUntilAShortBatch()
    {
        var quotations = Enumerable.Range(1, ExportJobLimits.BatchSize + 1)
            .Select(number => NewQuotation($"QUO-2026-{number:0000}"))
            .ToArray();
        var repository = new StubQuotationListRepository(quotations);
        var writer = new RecordingExportWorkbookWriter();

        var result = await NewProcessor(repository, writer).ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(2, repository.ExportCalls);
        Assert.Equal(ExportJobLimits.BatchSize + 1, result.RowCount);
        Assert.Equal(ExportJobLimits.BatchSize + 1, writer.Rows.Count);
    }

    // Keyset (D8): cada lote pide lo que viene después de la última fila del anterior, nunca un
    // offset. Todas del mismo instante: el número desempata, de mayor a menor.
    [Fact]
    public async Task EachBatchStartsAfterTheLastRowOfThePreviousOne()
    {
        var quotations = Enumerable.Range(1, ExportJobLimits.BatchSize + 1)
            .Select(number => NewQuotation($"QUO-2026-{number:0000}"))
            .ToArray();
        var repository = new StubQuotationListRepository(quotations);

        await NewProcessor(repository).ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(
            new QuotationExportCursor?[] { null, new QuotationExportCursor(Now, "QUO-2026-0002") },
            repository.ExportCursors);
    }

    [Fact]
    public async Task UploadsUnderTheJobAndReturnsWhatTheEmailNeeds()
    {
        var storage = new RecordingExportFileStorage();
        var job = NewJob();
        var processor = NewProcessor(
            new StubQuotationListRepository(NewQuotation("QUO-2026-0001")), storage: storage);

        var result = await processor.ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(
            new RecordedUpload(TenantId, job.Id, "cotizaciones-2026-09-12-1530.xlsx", RecordingExportWorkbookWriter.CompletedPath),
            storage.Upload);
        Assert.Equal(
            new ExportJobResult(
                "cotizaciones-2026-09-12-1530.xlsx",
                1,
                $"https://r2.test/exports/tenants/{TenantId:N}/jobs/{job.Id:N}.xlsx",
                StubExportJobProcessor.LinkExpiresAt),
            result);
    }

    [Fact]
    public async Task FiltersWithWhatTheRequestStored()
    {
        var repository = new StubQuotationListRepository(NewQuotation("QUO-2026-0001"));
        var job = NewJob(new QuotationsExportFilters(ClientId, AdvisorId.Value, "sent", From, To, null, "0001"));

        await NewProcessor(repository).ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(
            new RecordedExportSearch(ClientId, ClientIds: null, AdvisorId, QuotationStatus.Sent, From, To, "0001"),
            repository.LastExportSearch);
    }

    // Había filas al pedir y ya no al procesar: reintentar da lo mismo.
    [Fact]
    public async Task NoRowsWhenItRunsIsDefinitiveAndUploadsNothing()
    {
        var storage = new RecordingExportFileStorage();
        var processor = NewProcessor(new StubQuotationListRepository(), storage: storage);

        var error = await Assert.ThrowsAsync<ExportJobDefinitiveException>(() =>
            processor.ProcessAsync(NewJob(), TestContext.Current.CancellationToken));

        Assert.StartsWith("Empty:", error.Message, StringComparison.Ordinal);
        Assert.Null(storage.Upload);
    }

    [Fact]
    public async Task UnreadableFiltersAreDefinitive()
    {
        var job = ExportJob.Enqueue(
            Guid.CreateVersion7(), TenantId, Guid.CreateVersion7(), ExportJobKind.Quotations, "not json", Now);

        await Assert.ThrowsAsync<ExportJobDefinitiveException>(() =>
            NewProcessor(new StubQuotationListRepository(NewQuotation("QUO-2026-0001")))
                .ProcessAsync(job, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AStoredStatusThatNoLongerExistsIsDefinitive()
    {
        var job = NewJob(new QuotationsExportFilters(null, null, "Approved", From, To, null, null));

        await Assert.ThrowsAsync<ExportJobDefinitiveException>(() =>
            NewProcessor(new StubQuotationListRepository(NewQuotation("QUO-2026-0001")))
                .ProcessAsync(job, TestContext.Current.CancellationToken));
    }

    // R2 caído no es definitivo: la excepción sube tal cual y el runner reintenta. El temporal se
    // borra igual.
    [Fact]
    public async Task AStorageFailureIsTransientAndStillDisposesTheWorkbook()
    {
        var writer = new RecordingExportWorkbookWriter();
        var storage = new RecordingExportFileStorage { Failure = new IOException("r2 unavailable") };
        var processor = NewProcessor(
            new StubQuotationListRepository(NewQuotation("QUO-2026-0001")), writer, storage);

        await Assert.ThrowsAsync<IOException>(() =>
            processor.ProcessAsync(NewJob(), TestContext.Current.CancellationToken));

        Assert.True(writer.Disposed);
    }

    private static ExportJob NewJob(QuotationsExportFilters? filters = null) =>
        ExportJob.Enqueue(
            Guid.CreateVersion7(),
            TenantId,
            Guid.CreateVersion7(),
            ExportJobKind.Quotations,
            ExportJobFilters.Serialize(filters ?? new QuotationsExportFilters(null, null, null, From, To, null, null)),
            Now);

    private static Quotation NewQuotation(string number) =>
        Quotation.Create(
            QuotationId.New(),
            TenantId,
            number,
            ClientId,
            AdvisorId,
            validUntil: null,
            paymentMethod: null,
            notes: null,
            QuotationParties.Empty,
            billingAccount: null,
            customerWithRetention: false,
            customerVatSurplus: false,
            AdvisorId,
            Now);

    private static QuotationsExportProcessor NewProcessor(
        StubQuotationListRepository repository,
        RecordingExportWorkbookWriter? writer = null,
        RecordingExportFileStorage? storage = null) =>
        new(repository,
            new StubQuotationCustomerLookup(new QuotationCustomerRef(
                ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
                "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false)),
            new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            writer ?? new RecordingExportWorkbookWriter(),
            storage ?? new RecordingExportFileStorage(),
            new FixedClock(Now));
}
```

> `Quotation.Create` sin cuenta de cobro deja la moneda por defecto del agregado. Si no es `COP`, ajustar la aserción de `row[5]` a lo que devuelva `quotation.Currency.ToCode()`: lo que se prueba es que la columna es la moneda de la fila.

`tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportWorkbookReader.cs`:

```csharp
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Modules.Quotations.IntegrationTests;

internal sealed record ExportWorkbookSheet(
    string Name,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<IReadOnlyList<bool>> NumericCells);

/// <summary>
/// Abre el .xlsx que subió el worker con el SDK de OpenXML. Reabrir el archivo y leer celdas es la
/// convención del repo (CustomerExportApiTests): mirar sólo el status dejaría pasar un archivo con
/// las columnas corridas.
/// </summary>
internal static class ExportWorkbookReader
{
    public static ExportWorkbookSheet Read(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var document = SpreadsheetDocument.Open(stream, isEditable: false);
        var workbookPart = document.WorkbookPart!;
        var sheet = workbookPart.Workbook.Sheets!.Elements<Sheet>().Single();
        var worksheet = ((WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!)).Worksheet;
        var rows = worksheet.GetFirstChild<SheetData>()!.Elements<Row>().ToArray();

        return new ExportWorkbookSheet(
            sheet.Name!.Value!,
            rows.Select(row => (IReadOnlyList<string>)row.Elements<Cell>()
                .Select(cell => cell.InlineString?.Text?.Text ?? cell.CellValue?.Text ?? string.Empty)
                .ToArray()).ToArray(),
            rows.Select(row => (IReadOnlyList<bool>)row.Elements<Cell>()
                .Select(cell => cell.DataType?.Value == CellValues.Number)
                .ToArray()).ToArray());
    }
}
```

En `QuotationExportApiTests.cs`, agregar `using System.Text.Json;` y `using Npgsql;` arriba, y antes de `private static string CurrentRange()`:

```csharp
    // De punta a punta (D1): el POST encola, un tick del worker arma el Excel con las mismas filas
    // que la tabla, lo sube, deja el evento y Notifications manda el correo.
    [Fact]
    public async Task TheWorkerTurnsTheRequestIntoTheWorkbookTheEventAndTheEmail()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientA = await CreateActiveCustomerAsync(client, tenantId);
        var clientB = await CreateActiveCustomerAsync(client, tenantId);
        await CreateQuotationAsync(client, tenantId, clientA);
        await CreateQuotationAsync(client, tenantId, clientA);
        var otherClient = await CreateQuotationAsync(client, tenantId, clientB);
        var outOfRange = await CreateQuotationAsync(client, tenantId, clientA);
        await BackdateAsync(factory, outOfRange.Id, DateTimeOffset.UtcNow.AddYears(-2));
        var filters = $"clientId={clientA}&{CurrentRange()}";

        var response = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/export?{filters}", content: null, TestContext.Current.CancellationToken);
        var accepted = await response.Content.ReadFromJsonAsync<AcceptedDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(accepted);

        Assert.Equal(ExportJobRunOutcome.Completed, await RunExportJobAsync(factory));

        var job = await FindExportJobAsync(factory, accepted.JobId);
        Assert.Equal(ExportJobStatus.Completed, job.Status);
        Assert.Equal(2, job.RowCount);
        Assert.Matches(@"^cotizaciones-\d{4}-\d{2}-\d{2}-\d{4}\.xlsx$", job.FileName);

        var key = $"exports/tenants/{tenantId:N}/jobs/{accepted.JobId:N}.xlsx";
        var ready = Assert.Single(await OutboxMessagesAsync(factory, "quotations.export-ready.v1"));
        using (var payload = JsonDocument.Parse(ready.PayloadJson))
        {
            Assert.Equal($"https://r2.test/{key}", payload.RootElement.GetProperty("downloadUrl").GetString());
            Assert.Equal(job.FileName, payload.RootElement.GetProperty("fileName").GetString());
            Assert.Equal(2, payload.RootElement.GetProperty("rowCount").GetInt32());
        }

        var sheet = ExportWorkbookReader.Read(
            await factory.ObjectStorage.DownloadAsync(key, TestContext.Current.CancellationToken));
        var list = await client.GetFromJsonAsync<QuotationsPageResponse>(
            $"{QuotationsUrl(tenantId)}?{filters}", TestContext.Current.CancellationToken);
        var items = list!.Items.ToArray();
        Assert.Equal("Cotizaciones", sheet.Name);
        Assert.Equal(["Numero", "Fecha", "Cliente", "Asesor", "Estado", "Moneda", "Total"], sheet.Rows[0]);
        Assert.Equal(
            items.Select(item => item.QuotationNumber),
            sheet.Rows.Skip(1).Select(row => row[0]));
        Assert.DoesNotContain(sheet.Rows, row => row[0] == otherClient.QuotationNumber);
        Assert.DoesNotContain(sheet.Rows, row => row[0] == outOfRange.QuotationNumber);
        var first = sheet.Rows[1];
        Assert.Equal(items[0].CreatedAt, DateTimeOffset.Parse(first[1], CultureInfo.InvariantCulture));
        Assert.Equal("Verde Esencial S.A.S.", first[2]);
        Assert.Equal(items[0].AdvisorEmail ?? string.Empty, first[3]);
        Assert.Equal("Draft", first[4]);
        Assert.Equal(items[0].Currency, first[5]);
        Assert.True(sheet.NumericCells[1][6]);
        Assert.Equal(items[0].Total, decimal.Parse(first[6], CultureInfo.InvariantCulture));

        Assert.Equal("Sent", await WaitForEmailStatusAsync(
            database.GetConnectionString(), ownerUserId, "quotations.export-ready.v1"));
    }

    // Mueve la fecha de alta directo en la base: la API no deja crear una cotización en el pasado.
    private static async Task BackdateAsync(QepApiFactory factory, Guid quotationId, DateTimeOffset createdAt)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var id = new QuotationId(quotationId);
        var updated = await dbContext.Quotations
            .Where(quotation => quotation.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(quotation => quotation.CreatedAt, createdAt),
                TestContext.Current.CancellationToken);
        Assert.Equal(1, updated);
    }

    // Los workers de Notifications sí corren en el host de pruebas: el correo sale solo, y se
    // espera con plazo, como en InvitationNotificationTests.
    private static async Task<string?> WaitForEmailStatusAsync(
        string connectionString, Guid recipientId, string templateRef)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT status FROM notifications.notifications
                WHERE recipient_id = @recipientId AND template_ref = @templateRef
                """,
                connection);
            command.Parameters.AddWithValue("recipientId", recipientId);
            command.Parameters.AddWithValue("templateRef", templateRef);
            if (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) is string status)
            {
                return status;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }

        return null;
    }

    // Keyset y no offset (D8, hallazgo 11). Se lee de a una fila para tener un borde por
    // cotización, y entre lotes pasan los dos cambios que rompen el offset: una cotización nueva
    // (con offset, el lote siguiente repetiría la del borde) y una ya leída que sale del filtro
    // (con offset, el lote siguiente saltearía una). Lo esperado son las que existían al empezar,
    // en el orden del export, una vez cada una.
    [Fact]
    public async Task RowsCreatedOrLeavingTheFilterBetweenBatchesNeitherRepeatNorSkip()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        for (var created = 0; created < 3; created++)
        {
            await CreateQuotationAsync(client, tenantId, customerId);
        }

        var expected = (await ReadDraftBatchAsync(factory, tenantId, after: null, limit: 100))
            .Select(quotation => quotation.Id)
            .ToArray();
        Assert.Equal(3, expected.Length);

        var first = await ReadDraftBatchAsync(factory, tenantId, after: null, limit: 1);
        await CreateQuotationAsync(client, tenantId, customerId);
        var second = await ReadDraftBatchAsync(factory, tenantId, CursorOf(first), limit: 1);
        await SetQuotationStatusAsync(factory, second.Single().Id, QuotationStatus.Voided);
        var third = await ReadDraftBatchAsync(factory, tenantId, CursorOf(second), limit: 1);
        var fourth = await ReadDraftBatchAsync(factory, tenantId, CursorOf(third), limit: 1);

        Assert.Equal(expected, first.Concat(second).Concat(third).Select(quotation => quotation.Id));
        Assert.Empty(fourth);
    }

    // El repositorio real, contra Postgres: lo que se prueba es la consulta del keyset, no el
    // procesador. `Draft` es el filtro que la cotización anulada abandona.
    private static async Task<IReadOnlyList<Quotation>> ReadDraftBatchAsync(
        QepApiFactory factory, Guid tenantId, QuotationExportCursor? after, int limit)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQuotationRepository>().ListForExportAsync(
            tenantId,
            clientId: null,
            clientIds: null,
            advisorId: null,
            QuotationStatus.Draft,
            today.AddDays(-7),
            today.AddDays(1),
            quotationNumber: null,
            after,
            limit,
            TestContext.Current.CancellationToken);
    }

    private static QuotationExportCursor CursorOf(IReadOnlyList<Quotation> batch) =>
        new(batch[^1].CreatedAt, batch[^1].QuotationNumber);

    // Directo en la base, como BackdateAsync: lo que se prueba es la lectura, no la anulación.
    private static async Task SetQuotationStatusAsync(
        QepApiFactory factory, QuotationId quotationId, QuotationStatus status)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var updated = await dbContext.Quotations
            .Where(quotation => quotation.Id == quotationId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(quotation => quotation.Status, status),
                TestContext.Current.CancellationToken);
        Assert.Equal(1, updated);
    }
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationsExportProcessorTests"
```

Esperado: no compila — `error CS0246: The type or namespace name 'QuotationsExportProcessor' could not be found` (y `QuotationExportCursor`), y `error CS0535: 'StubQuotationRepository' does not implement interface member 'IQuotationRepository.ListForExportAsync(…, CancellationToken)'` (la interfaz todavía tiene la firma sin keyset). Pegar la salida. La prueba de keyset de integración compila recién con el Step 3 y corre en el Step 4.

- [ ] **Step 3: Implementar**

`IQuotationRepository.cs`, reemplazar `ListForExportAsync` y su comentario (`:35-49`):

```csharp
    /// <summary>Un lote de las cotizaciones que pasan los filtros del listado, en su mismo orden:
    /// lo que lee QuotationsExportProcessor para el Excel. Mismos filtros y misma semántica que
    /// <see cref="SearchAsync"/> —<c>clientIds</c> incluido— porque el archivo tiene que ser lo que
    /// la tabla muestra. Por lotes y no entero: la memoria del worker queda acotada al lote (D8).
    /// El volumen total lo acota el rango obligatorio de a lo sumo un año.
    ///
    /// Keyset y no offset: <paramref name="after"/> es la clave de la última fila del lote
    /// anterior (<c>null</c> en el primero), y el lote es lo que viene después en el orden
    /// <c>(CreatedAt DESC, QuotationNumber DESC)</c>. Una cotización creada o que sale del filtro
    /// durante el export no corre las filas.</summary>
    Task<IReadOnlyList<Quotation>> ListForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        QuotationStatus? status,
        DateOnly? createdFrom,
        DateOnly? createdTo,
        string? quotationNumber,
        QuotationExportCursor? after,
        int limit,
        CancellationToken cancellationToken);
```

y al final de `IQuotationRepository.cs`, fuera de la interfaz:

```csharp
/// <summary>
/// Dónde quedó el export (spec 2026-09-12, D8): la fecha de alta y el número de la última fila
/// leída. El número y no el id porque <see cref="QuotationId"/> no se compara, y el número es único
/// por tenant (<c>IX_quotations_tenant_number</c>), así que la clave nunca empata.
/// </summary>
public sealed record QuotationExportCursor(DateTimeOffset CreatedAt, string QuotationNumber);
```

`QuotationRepository.cs`, reemplazar `ListForExportAsync` y su comentario (`:65-80`):

```csharp
    // Keyset sobre (CreatedAt, QuotationNumber), los dos descendentes: el orden de la tabla con el
    // número —único por tenant— como desempate. El lote siguiente es "lo que viene después de la
    // última fila leída" y no un offset, así que una cotización creada o que sale del filtro
    // durante el export no repite ni saltea filas (spec 2026-09-12, D8). EF no compara tuplas: la
    // condición va en su forma OR. La sirve IX_quotations_tenant_created_at_number.
    public async Task<IReadOnlyList<Quotation>> ListForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        QuotationStatus? status,
        DateOnly? createdFrom,
        DateOnly? createdTo,
        string? quotationNumber,
        QuotationExportCursor? after,
        int limit,
        CancellationToken cancellationToken)
    {
        var query = FilteredQuery(
            tenantId, clientId, clientIds, advisorId, status, createdFrom, createdTo, quotationNumber);

        if (after is not null)
        {
            var createdAt = after.CreatedAt;
            var number = after.QuotationNumber;
            // `string.Compare` se traduce a `<` sobre la columna, con su collation: la misma que
            // usa el ORDER BY de abajo, así que corte y orden no se contradicen.
            query = query.Where(quotation =>
                quotation.CreatedAt < createdAt
                || (quotation.CreatedAt == createdAt
                    && string.Compare(quotation.QuotationNumber, number) < 0));
        }

        return await query
            .OrderByDescending(quotation => quotation.CreatedAt)
            .ThenByDescending(quotation => quotation.QuotationNumber)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }
```

> CA1310: `Directory.Build.props:7-8` tiene `TreatWarningsAsErrors` y `AnalysisLevel 10.0-recommended`. Si el build marca `CA1310` sobre ese `string.Compare`, envolver sólo la condición en `#pragma warning disable CA1310` / `#pragma warning restore CA1310` con el motivo en una línea: la expresión la traduce EF a SQL y compara con la collation de la columna; no corre ninguna cultura de .NET, y un `StringComparison` la volvería intraducible. No hay precedente de supresión escrita a mano en `src/` (sólo en migraciones generadas), así que mencionarlo en el handoff. Lo mismo vale para `SaleRepository` en Task 12.

Al final de `ExportJobSupport.cs` (y `using System.Globalization;` arriba):

```csharp
public static class ExportFileNames
{
    /// <summary>D8: <c>{prefijo}-yyyy-MM-dd-HHmm.xlsx</c> con la hora UTC en que se generó —la misma
    /// zona que el vencimiento que dice el correo—.</summary>
    public static string For(string prefix, DateTimeOffset generatedAt) =>
        $"{prefix}-{generatedAt.UtcDateTime.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture)}.xlsx";
}
```

`src/Modules/Quotations/Modules.Quotations.Application/QuotationsExportProcessor.cs`:

```csharp
using System.Globalization;
using BuildingBlocks.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Arma el Excel del listado de cotizaciones en el worker (D7, D8). Lee por lotes de mil con keyset
/// y el mismo filtro que la tabla —<c>FilteredQuery</c> y <see cref="QuotationListing"/>—, escribe
/// cada lote en streaming y sube el archivo con el id del job.
/// </summary>
public sealed class QuotationsExportProcessor(
    IQuotationRepository repository,
    IQuotationCustomerLookup customerLookup,
    IQuotationAdvisorLookup advisorLookup,
    IExportWorkbookWriter writer,
    IExportFileStorage storage,
    IClock clock)
    : IExportJobProcessor
{
    public const string SheetName = "Cotizaciones";

    public const string FilePrefix = "cotizaciones";

    /// <summary>Las de la tabla del listado, en su orden (D8), con la moneda aparte del total
    /// —la tabla la pinta junto al importe y en una planilla tiene que poder filtrarse—. Sin
    /// tildes, como Reporting. Anchos fijos: medir el contenido obligaría a recorrerlo dos veces;
    /// Fecha cabe entera en formato ISO.</summary>
    public static readonly IReadOnlyList<ExportColumn> Columns =
    [
        new("Numero", 16),
        new("Fecha", 34),
        new("Cliente", 40),
        new("Asesor", 32),
        new("Estado", 12),
        new("Moneda", 10),
        new("Total", 16),
    ];

    public ExportJobKind Kind => ExportJobKind.Quotations;

    public async Task<ExportJobResult> ProcessAsync(ExportJob job, CancellationToken cancellationToken)
    {
        var filters = ExportJobFilters.Read<QuotationsExportFilters>(job);
        var status = ParseStatus(filters.Status);
        var advisorId = filters.AdvisorId is { } advisor ? new MemberId(advisor) : (MemberId?)null;
        // El NIT se resuelve al generar, no al pedir: el archivo refleja los clientes de ahora.
        var clientIds = await QuotationListing.ResolveClientIdsByNitAsync(
            customerLookup, job.TenantId, filters.ClientNit, cancellationToken);
        var generatedAt = clock.UtcNow;

        using var workbook = writer.Create(SheetName, Columns);
        var rowCount = 0;
        QuotationExportCursor? after = null;
        while (true)
        {
            // Keyset (D8): el lote siguiente arranca después de la última fila leída, no en un
            // offset que una cotización nueva o anulada durante el export correría.
            var batch = await repository.ListForExportAsync(
                job.TenantId,
                filters.ClientId,
                clientIds,
                advisorId,
                status,
                filters.CreatedFrom,
                filters.CreatedTo,
                filters.QuotationNumber,
                after,
                ExportJobLimits.BatchSize,
                cancellationToken);

            if (batch.Count > 0)
            {
                // Nombres y correos por lote: dos idas por cada mil filas, no una por fila.
                var rows = await QuotationListing.ToListItemsAsync(
                    customerLookup, advisorLookup, job.TenantId, batch, cancellationToken);
                foreach (var row in rows)
                {
                    workbook.AppendRow(ToCells(row));
                }

                after = new QuotationExportCursor(batch[^1].CreatedAt, batch[^1].QuotationNumber);
            }

            rowCount += batch.Count;
            if (batch.Count < ExportJobLimits.BatchSize)
            {
                break;
            }
        }

        // Había filas cuando se pidió (el EXISTS del request) y ya no: reintentar da lo mismo.
        if (rowCount == 0)
        {
            throw new ExportJobDefinitiveException(
                "Empty: no quotations matched the export filters when the export ran.");
        }

        var fileName = ExportFileNames.For(FilePrefix, generatedAt);
        var upload = await storage.UploadAsync(
            job.TenantId, job.Id, fileName, workbook.Complete(), cancellationToken);
        return new ExportJobResult(fileName, rowCount, upload.DownloadUrl, upload.ExpiresAt);
    }

    // El request ya validó el estado; si igual no se puede leer —el enum cambió entre el pedido y
    // el proceso—, reintentar no lo arregla.
    private static QuotationStatus? ParseStatus(string? status)
    {
        try
        {
            return QuotationListing.ParseStatus(status);
        }
        catch (QuotationsDomainException exception)
        {
            throw new ExportJobDefinitiveException($"UnreadableFilters: {exception.Message}", exception);
        }
    }

    private static ExportCell[] ToCells(QuotationListItemDto row) =>
    [
        ExportCell.OfText(row.QuotationNumber),
        // Texto ISO y no celda de fecha: una fecha se muestra según la configuración regional de
        // quien abre el archivo, y ahí 03/04 deja de ser una fecha sola.
        ExportCell.OfText(row.CreatedAt.ToString("O", CultureInfo.InvariantCulture)),
        ExportCell.OfText(row.ClientName),
        ExportCell.OfText(row.AdvisorEmail),
        ExportCell.OfText(row.Status),
        ExportCell.OfText(row.Currency),
        ExportCell.OfNumber(row.Total),
    ];
}
```

En `QepServiceCollectionExtensions.cs`, debajo de `services.AddScoped<ExportJobRunner>();`:

```csharp
        services.AddScoped<IExportJobProcessor, QuotationsExportProcessor>();
```

`README.md:840-844`, reemplazar el primer párrafo de «Reportes exportados» por:

```markdown
La exportación del padrón de clientes (`POST /tenants/{tenantId}/customers/export`) y la del
listado de cotizaciones (`POST /tenants/{tenantId}/quotations/export`) no devuelven el archivo:
lo suben bajo el prefijo `exports/` del **bucket privado** y le mandan a quien la pidió un correo
con una URL prefirmada. Clientes arma el Excel dentro del request; cotizaciones contesta `202` y
lo encola en `quotations.export_jobs`, y `ExportJobWorker` lo arma en segundo plano con la clave
`exports/tenants/{tenantId}/jobs/{jobId}.xlsx` —un reintento pisa el mismo objeto—. La vigencia
del enlace es `Storage:ExportUrlHours` (24 h por defecto), propia y no
`Storage:PresignedUrlMinutes`: aquellas URLs las consume un navegador que ya está en pantalla, y
ésta espera en una bandeja de entrada.
```

- [ ] **Step 4: Correr y verificar que pasan, más la regresión del commit**

```powershell
dotnet build --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-build --filter "FullyQualifiedName~QuotationsExportProcessorTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build --filter "FullyQualifiedName~QuotationExportApiTests"
dotnet restore --locked-mode
dotnet format --verify-no-changes
```

Esperado: `Passed: 9` en el procesador, `Passed: 10` en `QuotationExportApiTests` (la de keyset incluida: prueba que `string.Compare` se traduce), el `--locked-mode` pasa y `dotnet format` sin cambios. Después la suite completa contra el baseline, igual que en Task 5 Step 4 (con `$after = Join-Path $env:TEMP "qep-export-asincrono-commit3"`): el `Compare-Object` tiene que salir vacío. Pegar las salidas.

- [ ] **Step 5: Commit** (Git Bash)

```bash
test "$(git branch --show-current)" = "feature/export-asincrono" || { echo ABORT; exit 1; }
git add src/Modules/Quotations/Modules.Quotations.Application/QuotationsExportProcessor.cs \
  src/Modules/Quotations/Modules.Quotations.Application/ExportJobSupport.cs \
  src/Modules/Quotations/Modules.Quotations.Application/IQuotationRepository.cs \
  src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationRepository.cs \
  src/Bootstrapper/QepServiceCollectionExtensions.cs \
  README.md \
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportWorkbookReader.cs \
  tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs \
  tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportTestDoubles.cs \
  tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsExportProcessorTests.cs \
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationExportApiTests.cs
git status --short
git commit -m "feat(quotations): exportar cotizaciones por correo" \
  -m "POST /quotations/export valida en el orden de la spec (permiso, rango de hasta un año, que haya filas, tres pendientes por persona), encola y responde 202 con jobId y requestedAt. QuotationsExportProcessor lee por lotes de mil con keyset —nunca offset— y el mismo filtro del listado, escribe el xlsx en streaming con OpenXmlWriter y lo sube bajo exports/ con el id del job en la clave." \
  -m "Sale el GET síncrono de 572200c, que armaba el Excel dentro del request, junto con ClosedXmlQuotationExportBuilder y la referencia a ClosedXML de Quotations; DocumentFormat.OpenXml pasa a directa en 3.1.1. Lock files regenerados sin mover versiones."
```

`git status --short` antes del commit: sólo archivos de Tasks 8–11 staged, incluidos los lock files y los dos borrados (`D `).

---

## Commit 4 — `feat(sales): exportar ventas por correo`

### Task 12: `POST /sales/export` encola

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/SaleListing.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ListSales.cs:82-94,110-131,136-165` (usa `SaleListing`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ISaleRepository.cs` (`SaleExportCursor` debajo de `SaleWithQuotation` `:10`; dos métodos después de `SearchAsync` `:34-55`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/SaleRepository.cs:62-158` (`SearchAsync`; los filtros a extraer son `:84-138`)
- Create: `src/Modules/Quotations/Modules.Quotations.Application/ExportSales.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/SaleEndpoints.cs` (mapeo después de `GET /{saleId:guid}` `:31-35`; handler después de `ListSalesAsync` `:70-96`). `GET /summary` ya no existe (`b840526`).
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:340-342` (después de `ListSalesHandler`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs` (`StubSaleListRepository` `:482-524`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportSalesValidatorTests.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportSalesHandlerTests.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/SaleExportApiTests.cs`

**Interfaces:**
- Consumes: `ExportJobAccepted`, `ExportJobAcceptedResponse`, `ExportJobFilters` (Task 10); `IExportJobQueue`, `ExportJobLimits` (Task 3); `InMemoryExportJobQueue`, `CountingQuotationsUnitOfWork` (Task 3); harness `FindExportJobAsync` (Task 4).
- Produces:
  - `internal static class SaleListing` con `SaleStatus? ParseStatus(string?)`, `SalePaymentStatus? ParsePaymentStatus(string?)`, `Task<IReadOnlyCollection<Guid>?> ResolveClientIdsByCucAsync(IQuotationCustomerLookup, Guid tenantId, string? clientCuc, CancellationToken)`, `Task<IReadOnlyList<SaleListItemDto>> ToListItemsAsync(IQuotationCustomerLookup, IQuotationAdvisorLookup, Guid tenantId, IReadOnlyList<SaleWithQuotation> rows, CancellationToken)` — mismos códigos `sale.sale.status_invalid` / `sale.sale.payment_status_invalid`.
  - `Task<bool> ISaleRepository.AnyForExportAsync(Guid tenantId, Guid? clientId, IReadOnlyCollection<Guid>? clientIds, MemberId? advisorId, SaleStatus? status, SalePaymentStatus? paymentStatus, DateOnly? convertedFrom, DateOnly? convertedTo, string? saleNumber, CancellationToken cancellationToken)`
  - `public sealed record SaleExportCursor(DateTimeOffset ConvertedAt, string SaleNumber)` — la clave de la última venta leída.
  - `Task<IReadOnlyList<SaleWithQuotation>> ISaleRepository.ListForExportAsync(<los mismos filtros>, SaleExportCursor? after, int limit, CancellationToken cancellationToken)` — keyset sobre `(ConvertedAt DESC, SaleNumber DESC)`, el orden exacto del listado (`SaleRepository.cs:150-151`); `after` en `null` es el primer lote.
  - `public sealed record ExportSalesCommand(Guid TenantId, Guid? ClientId, Guid? AdvisorId, string? Status, string? PaymentStatus, DateOnly? ConvertedFrom, DateOnly? ConvertedTo, string? ClientCuc, string? SaleNumber) : ICommand<ExportJobAccepted>`
  - `public sealed record SalesExportFilters(Guid? ClientId, Guid? AdvisorId, string? Status, string? PaymentStatus, DateOnly ConvertedFrom, DateOnly ConvertedTo, string? ClientCuc, string? SaleNumber)`
  - `ExportSalesValidator`, `ExportSalesHandler : ICommandHandler<ExportSalesCommand, ExportJobAccepted>`
  - Doble: `RecordedSaleExportSearch` y, en `StubSaleListRepository`, `AnyCalls`, `ExportCalls`, `ExportCursors`, `LastExportSearch`.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `QuotationsTestDoubles.cs`, antes de `StubSaleListRepository` (`:482`):

```csharp
/// <summary>Los filtros con que se preguntó por ventas o se leyó para exportarlas.</summary>
internal sealed record RecordedSaleExportSearch(
    Guid? ClientId,
    IReadOnlyCollection<Guid>? ClientIds,
    MemberId? AdvisorId,
    SaleStatus? Status,
    SalePaymentStatus? PaymentStatus,
    DateOnly? ConvertedFrom,
    DateOnly? ConvertedTo,
    string? SaleNumber);
```

y dentro de `StubSaleListRepository`, antes de `public void Add(Sale sale) { }`:

```csharp
    public int AnyCalls { get; private set; }

    public int ExportCalls { get; private set; }

    /// <summary>La clave con que se pidió cada lote, en orden (keyset, D8).</summary>
    public List<SaleExportCursor?> ExportCursors { get; } = [];

    public RecordedSaleExportSearch? LastExportSearch { get; private set; }

    // Honra el contrato de `clientIds` (vacío = ninguna fila): es lo que hace que un CUC sin
    // cliente termine en "no hay ventas". El resto de los filtros son SQL y los cubre integración.
    public Task<bool> AnyForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        SaleStatus? status,
        SalePaymentStatus? paymentStatus,
        DateOnly? convertedFrom,
        DateOnly? convertedTo,
        string? saleNumber,
        CancellationToken cancellationToken)
    {
        AnyCalls++;
        LastExportSearch = new RecordedSaleExportSearch(
            clientId, clientIds, advisorId, status, paymentStatus, convertedFrom, convertedTo, saleNumber);
        return Task.FromResult(Matching(clientIds).Any());
    }

    public Task<IReadOnlyList<SaleWithQuotation>> ListForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        SaleStatus? status,
        SalePaymentStatus? paymentStatus,
        DateOnly? convertedFrom,
        DateOnly? convertedTo,
        string? saleNumber,
        SaleExportCursor? after,
        int limit,
        CancellationToken cancellationToken)
    {
        ExportCalls++;
        ExportCursors.Add(after);
        LastExportSearch = new RecordedSaleExportSearch(
            clientId, clientIds, advisorId, status, paymentStatus, convertedFrom, convertedTo, saleNumber);
        // El mismo orden y la misma condición de keyset que SaleRepository, en memoria.
        IReadOnlyList<SaleWithQuotation> page = Matching(clientIds)
            .Where(row => after is null
                || row.Sale.ConvertedAt < after.ConvertedAt
                || (row.Sale.ConvertedAt == after.ConvertedAt
                    && string.CompareOrdinal(row.Sale.SaleNumber, after.SaleNumber) < 0))
            .OrderByDescending(row => row.Sale.ConvertedAt)
            .ThenByDescending(row => row.Sale.SaleNumber, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
        return Task.FromResult(page);
    }

    private IEnumerable<SaleWithQuotation> Matching(IReadOnlyCollection<Guid>? clientIds) =>
        clientIds is null ? rows : rows.Where(row => clientIds.Contains(row.Quotation.ClientId));
```

`tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportSalesValidatorTests.cs`:

```csharp
using System.Globalization;
using Modules.Quotations.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>El mismo rango que cotizaciones (D3), sobre la fecha de la venta:
/// <c>convertedFrom</c>/<c>convertedTo</c>.</summary>
public sealed class ExportSalesValidatorTests
{
    private readonly ExportSalesValidator _validator = new();

    [Theory]
    [InlineData("2025-01-01", "2026-01-01")]
    [InlineData("2024-02-29", "2025-02-28")]
    [InlineData("2026-09-12", "2026-09-12")]
    public void AcceptsARangeOfAtMostOneYear(string from, string to)
    {
        Assert.True(_validator.Validate(NewCommand(Date(from), Date(to))).IsValid);
    }

    [Theory]
    [InlineData("2025-01-01", "2026-01-02")]
    [InlineData("2024-02-29", "2025-03-01")]
    public void RejectsARangeLongerThanOneYear(string from, string to)
    {
        var failure = Assert.Single(_validator.Validate(NewCommand(Date(from), Date(to))).Errors);
        Assert.Equal("ConvertedTo", failure.PropertyName);
    }

    [Fact]
    public void RejectsConvertedToBeforeConvertedFrom()
    {
        var failure = Assert.Single(_validator.Validate(
            NewCommand(new DateOnly(2026, 9, 12), new DateOnly(2026, 9, 11))).Errors);
        Assert.Equal("ConvertedTo", failure.PropertyName);
    }

    [Fact]
    public void RequiresBothDates()
    {
        Assert.Equal(
            ["ConvertedFrom", "ConvertedTo"],
            _validator.Validate(NewCommand(null, null)).Errors
                .Select(failure => failure.PropertyName)
                .Order(StringComparer.Ordinal));
    }

    private static DateOnly Date(string value) =>
        DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static ExportSalesCommand NewCommand(DateOnly? from, DateOnly? to) =>
        new(Guid.CreateVersion7(), null, null, null, null, from, to, null, null);
}
```

`tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportSalesHandlerTests.cs`:

```csharp
using System.Text.Json;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>El pedido de exportación de ventas: mismo orden de D4 que cotizaciones, con el permiso
/// de ventas y los códigos `sale.export.*`. El límite de pendientes cuenta los dos tipos.</summary>
public sealed class ExportSalesHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 15, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 12);

    [Fact]
    public async Task ExportForAnotherTenantIsForbiddenAndReadsNothing()
    {
        var repository = new StubSaleListRepository(NewRow());
        var handler = NewHandler(
            repository, executionContext: new StubExecutionContext(SubjectId, Guid.CreateVersion7()));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal(0, repository.AnyCalls);
    }

    // Poder ver cotizaciones no es poder ver ventas: sin SaleRead no se exporta.
    [Fact]
    public async Task ExportWithoutTheSaleReadPermissionIsForbidden()
    {
        var handler = NewHandler(
            new StubSaleListRepository(NewRow()),
            executionContext: new StubExecutionContext(SubjectId, TenantId, SalesPermissions.SaleRead));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExportWithARangeLongerThanOneYearIsRejectedBeforeReading()
    {
        var repository = new StubSaleListRepository(NewRow());
        var handler = NewHandler(repository);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            handler.HandleAsync(
                NewCommand(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 2)),
                TestContext.Current.CancellationToken));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "ConvertedTo");
        Assert.Equal(0, repository.AnyCalls);
    }

    [Theory]
    [InlineData("NotAStatus", null, "sale.sale.status_invalid")]
    [InlineData(null, "NotAPaymentStatus", "sale.sale.payment_status_invalid")]
    public async Task ExportWithAnInvalidStatusFailsLikeTheList(
        string? status, string? paymentStatus, string expectedCode)
    {
        var handler = NewHandler(new StubSaleListRepository(NewRow()));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewCommand(From, To, status, paymentStatus), TestContext.Current.CancellationToken));

        Assert.Equal(expectedCode, error.Code);
    }

    [Fact]
    public async Task ExportWithNoMatchingSalesIsRejectedAndEnqueuesNothing()
    {
        var queue = new InMemoryExportJobQueue();
        var handler = NewHandler(new StubSaleListRepository(), queue);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal("sale.export.empty", error.Code);
        Assert.Empty(queue.Jobs);
    }

    // El CUC no vive en la venta: si no resuelve a ningún cliente, "ninguna fila".
    [Fact]
    public async Task ExportWithAClientCucThatMatchesNoCustomerIsRejectedAsEmpty()
    {
        var repository = new StubSaleListRepository(NewRow());
        var handler = NewHandler(repository);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewCommand(From, To, clientCuc: "CUC-NO-EXISTE"), TestContext.Current.CancellationToken));

        Assert.Equal("sale.export.empty", error.Code);
        Assert.Empty(repository.LastExportSearch!.ClientIds!);
    }

    // Tres exportaciones de cotizaciones pendientes también llenan el cupo de ventas (D4).
    [Fact]
    public async Task PendingQuotationExportsCountTowardsTheLimit()
    {
        var queue = new InMemoryExportJobQueue();
        for (var index = 0; index < 3; index++)
        {
            queue.Add(ExportJob.Enqueue(Guid.CreateVersion7(), TenantId, SubjectId, ExportJobKind.Quotations, "{}", Now));
        }

        var handler = NewHandler(new StubSaleListRepository(NewRow()), queue);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal("sale.export.pending_limit", error.Code);
        Assert.Equal(3, queue.Jobs.Count);
    }

    [Fact]
    public async Task ExportEnqueuesASalesJobWithTheValidatedFilters()
    {
        var queue = new InMemoryExportJobQueue();
        var unitOfWork = new CountingQuotationsUnitOfWork();
        var repository = new StubSaleListRepository(NewRow());
        var handler = NewHandler(repository, queue, unitOfWork);

        var accepted = await handler.HandleAsync(
            new ExportSalesCommand(
                TenantId, ClientId, AdvisorId.Value, "pending", "paymentpending", From, To, null, "VEN-2026"),
            TestContext.Current.CancellationToken);

        var job = Assert.Single(queue.Jobs);
        Assert.Equal(new ExportJobAccepted(job.Id, Now), accepted);
        Assert.Equal(ExportJobKind.Sales, job.Kind);
        Assert.Equal(SubjectId, job.RequestedBy);
        Assert.Equal(
            new SalesExportFilters(ClientId, AdvisorId.Value, "pending", "paymentpending", From, To, null, "VEN-2026"),
            JsonSerializer.Deserialize<SalesExportFilters>(job.Filters));
        Assert.Equal(
            new RecordedSaleExportSearch(
                ClientId, null, AdvisorId, SaleStatus.Pending, SalePaymentStatus.PaymentPending, From, To, "VEN-2026"),
            repository.LastExportSearch);
        Assert.Equal(1, unitOfWork.Saves);
    }

    private static ExportSalesCommand NewCommand() => NewCommand(From, To);

    private static ExportSalesCommand NewCommand(
        DateOnly? convertedFrom,
        DateOnly? convertedTo,
        string? status = null,
        string? paymentStatus = null,
        string? clientCuc = null) =>
        new(TenantId, null, null, status, paymentStatus, convertedFrom, convertedTo, clientCuc, null);

    private static SaleWithQuotation NewRow()
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, AdvisorId, new DateOnly(2026, 10, 30),
            paymentMethod: null, notes: null, QuotationParties.Empty, billingAccount: null,
            customerWithRetention: false, customerVatSurplus: false, AdvisorId, Now);
        var sale = Sale.Create(
            SaleId.New(), TenantId, "VEN-2026-0001", quotation.Id, SalePaymentStatus.PaymentPending,
            notes: null, AdvisorId, [], Now);
        return new SaleWithQuotation(sale, quotation);
    }

    private static ExportSalesHandler NewHandler(
        StubSaleListRepository repository,
        InMemoryExportJobQueue? queue = null,
        CountingQuotationsUnitOfWork? unitOfWork = null,
        IExecutionContext? executionContext = null) =>
        new(repository,
            new StubQuotationCustomerLookup(new QuotationCustomerRef(
                ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
                "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false)),
            queue ?? new InMemoryExportJobQueue(),
            unitOfWork ?? new CountingQuotationsUnitOfWork(),
            new ExportSalesValidator(),
            executionContext ?? new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now));
}
```

`tests/Modules/Quotations/Modules.Quotations.IntegrationTests/SaleExportApiTests.cs`:

```csharp
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>La exportación de ventas por correo: mismo contrato que cotizaciones, sobre el listado
/// de ventas (SALE-01) y con `SaleRead`.</summary>
public sealed class SaleExportApiTests
{
    private static string SalesUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/sales";

    [Fact]
    public async Task ExportIsAcceptedAndLeavesAPendingSalesJob()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        await CreateSaleAsync(client, factory, tenantId);

        var response = await client.PostAsync(
            $"{SalesUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<AcceptedDto>(TestContext.Current.CancellationToken);
        var job = await FindExportJobAsync(factory, accepted!.JobId);
        Assert.Equal(ExportJobKind.Sales, job.Kind);
        Assert.Equal(ExportJobStatus.Pending, job.Status);
        Assert.Equal(ownerUserId, job.RequestedBy);
    }

    [Fact]
    public async Task ExportWithoutDatesIsUnprocessableWithTheFieldErrors()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.PostAsync(
            $"{SalesUrl(tenantId)}/export", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal("validation.failed", problem?.Code);
        Assert.Contains("ConvertedFrom", problem!.Errors!.Keys);
        Assert.Contains("ConvertedTo", problem.Errors.Keys);
    }

    [Fact]
    public async Task ExportWithNoMatchingSalesIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.PostAsync(
            $"{SalesUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("sale.export.empty", problem?.Code);
    }

    // El cupo es por persona y cuenta los dos tipos: tres de cotizaciones frenan una de ventas.
    [Fact]
    public async Task PendingQuotationExportsFillTheSalesQuota()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        await CreateSaleAsync(client, factory, tenantId);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var quotationsExport =
            $"{QuotationsUrl(tenantId)}/export?createdFrom={Iso(today.AddDays(-7))}&createdTo={Iso(today.AddDays(1))}";
        for (var accepted = 0; accepted < 3; accepted++)
        {
            var ok = await client.PostAsync(quotationsExport, content: null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        }

        var response = await client.PostAsync(
            $"{SalesUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("sale.export.pending_limit", problem?.Code);
    }

    [Fact]
    public async Task ExportWithoutTheSaleReadPermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, QuotationsPermissions.QuotationRead);
        using var _ = client;

        var response = await client.PostAsync(
            $"{SalesUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ExportForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = owner;
        var (_, _, otherOwner) = await RegisterTenantAsync(factory, SalesPermissions.SaleRead);
        using var __ = otherOwner;

        var response = await otherOwner.PostAsync(
            $"{SalesUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static string CurrentRange()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return $"convertedFrom={Iso(today.AddDays(-7))}&convertedTo={Iso(today.AddDays(1))}";
    }

    private static string Iso(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Una venta convertida hoy, sin comprobantes (pago pendiente), mismo camino que
    /// SaleListApiTests.ConvertToSaleAsync.</summary>
    private static async Task<SaleResponse> CreateSaleAsync(HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, customerId, productId);
        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/sale",
            new ConvertQuotationToSaleRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var sale = await response.Content.ReadFromJsonAsync<SaleResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(sale);
        return sale;
    }

    private sealed record AcceptedDto(Guid JobId, DateTimeOffset RequestedAt);

    private sealed record ProblemDto(string? Code, Dictionary<string, string[]>? Errors);
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~ExportSales"
```

Esperado: no compila — `error CS0246: The type or namespace name 'ExportSalesCommand' could not be found` (y `ExportSalesValidator`, `ExportSalesHandler`, `SalesExportFilters`). Pegar la salida.

- [ ] **Step 3: Implementar**

`src/Modules/Quotations/Modules.Quotations.Application/SaleListing.cs`:

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Lo que el listado de ventas y su Excel comparten fuera de la consulta: cómo se leen los filtros
/// que llegan como texto y cómo se completa cada fila. Mismo papel que
/// <see cref="QuotationListing"/>: la tabla y el archivo no pueden empezar a contar cosas distintas.
/// </summary>
internal static class SaleListing
{
    // Texto libre por query string: un valor que no es del enum es un 422 con código de dominio,
    // no un filtro que en silencio no devuelve nada ni un 500 de un cast.
    public static SaleStatus? ParseStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        return Enum.TryParse<SaleStatus>(status, ignoreCase: true, out var parsed)
            ? parsed
            : throw new QuotationsDomainException(
                "sale.sale.status_invalid",
                $"'{status}' is not a valid sale status.");
    }

    public static SalePaymentStatus? ParsePaymentStatus(string? paymentStatus)
    {
        if (string.IsNullOrWhiteSpace(paymentStatus))
        {
            return null;
        }

        return Enum.TryParse<SalePaymentStatus>(paymentStatus, ignoreCase: true, out var parsed)
            ? parsed
            : throw new QuotationsDomainException(
                "sale.sale.payment_status_invalid",
                $"'{paymentStatus}' is not a valid sale payment status.");
    }

    // Sin término, null ("sin filtro"); con término que no resolvió a ningún cliente, vacío, y la
    // búsqueda ya sabe que no hay nada que traer.
    public static async Task<IReadOnlyCollection<Guid>?> ResolveClientIdsByCucAsync(
        IQuotationCustomerLookup customerLookup,
        Guid tenantId,
        string? clientCuc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientCuc))
        {
            return null;
        }

        var matchedIds = await customerLookup.SearchIdsByCucAsync(tenantId, clientCuc, cancellationToken);
        return matchedIds.ToArray();
    }

    public static async Task<IReadOnlyList<SaleListItemDto>> ToListItemsAsync(
        IQuotationCustomerLookup customerLookup,
        IQuotationAdvisorLookup advisorLookup,
        Guid tenantId,
        IReadOnlyList<SaleWithQuotation> rows,
        CancellationToken cancellationToken)
    {
        // Una ida para los nombres y otra para los correos, con los ids sin repetir.
        var clientNames = rows.Count == 0
            ? new Dictionary<Guid, string>()
            : await customerLookup.FindNamesAsync(
                tenantId,
                rows.Select(row => row.Quotation.ClientId).Distinct().ToArray(),
                cancellationToken);

        // El correo y no el nombre, mismo criterio que el listado de cotizaciones (spec 2026-09-11, D1).
        var advisors = rows.Count == 0
            ? new Dictionary<Guid, QuotationAdvisor>()
            : await advisorLookup.FindAsync(
                tenantId,
                rows.Select(row => row.Quotation.AdvisorId.Value).Distinct().ToArray(),
                cancellationToken);

        return rows
            .Select(row => row.ToListItemDto(
                clientNames.GetValueOrDefault(row.Quotation.ClientId),
                advisors.GetValueOrDefault(row.Quotation.AdvisorId.Value)?.Email))
            .ToArray();
    }
}
```

`ListSales.cs`: reemplazar las líneas de parseo (`:82-83`) por `SaleListing.ParseStatus(query.Status)` / `SaleListing.ParsePaymentStatus(query.PaymentStatus)`; el bloque del CUC (`:86-94`) por `var clientIds = await SaleListing.ResolveClientIdsByCucAsync(customerLookup, query.TenantId, query.ClientCuc, cancellationToken);`; el armado de filas (`:110-131`) por `var items = await SaleListing.ToListItemsAsync(customerLookup, advisorLookup, query.TenantId, rows, cancellationToken);`; y borrar los dos `Parse*` privados (`:136-165`). Comportamiento idéntico: lo cuidan `ListSalesHandlerTests` y `SaleListApiTests`.

`ISaleRepository.cs`, debajo de `SaleWithQuotation` (`:10`):

```csharp
/// <summary>
/// Dónde quedó el export de ventas (spec 2026-09-12, D8): la fecha de conversión y el número de la
/// última venta leída, el mismo orden que el listado. El número y no el id porque
/// <see cref="SaleId"/> no se compara, y el número es único por tenant
/// (<c>IX_sales_tenant_number</c>).
/// </summary>
public sealed record SaleExportCursor(DateTimeOffset ConvertedAt, string SaleNumber);
```

y después de `SearchAsync` (`:55`):

```csharp
    /// <summary>Si hay al menos una venta con los filtros del listado: el paso 3 de D4 antes de
    /// encolar una exportación.</summary>
    Task<bool> AnyForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        SaleStatus? status,
        SalePaymentStatus? paymentStatus,
        DateOnly? convertedFrom,
        DateOnly? convertedTo,
        string? saleNumber,
        CancellationToken cancellationToken);

    /// <summary>Un lote de las ventas del listado, en su mismo orden, para el Excel de
    /// SalesExportProcessor (D8). Mismos filtros y semántica que <see cref="SearchAsync"/>. Keyset
    /// y no offset: <paramref name="after"/> es la clave de la última venta del lote anterior
    /// (<c>null</c> en el primero).</summary>
    Task<IReadOnlyList<SaleWithQuotation>> ListForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        SaleStatus? status,
        SalePaymentStatus? paymentStatus,
        DateOnly? convertedFrom,
        DateOnly? convertedTo,
        string? saleNumber,
        SaleExportCursor? after,
        int limit,
        CancellationToken cancellationToken);
```

`SaleRepository.cs:62-158`: extraer los filtros de `SearchAsync` a un método privado y usarlo desde los tres:

```csharp
    public async Task<(IReadOnlyList<SaleWithQuotation> Items, int Total)> SearchAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        SaleStatus? status,
        SalePaymentStatus? paymentStatus,
        DateOnly? convertedFrom,
        DateOnly? convertedTo,
        string? saleNumber,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        // El join va acá adentro y no en el composition root --como sí lo hace el reporte de
        // ventas-- porque las dos tablas son de este módulo y viven en el mismo DbContext.
        var (sales, quotations) = Filtered(
            tenantId, clientId, clientIds, advisorId, status, paymentStatus, convertedFrom, convertedTo, saleNumber);
        var joined =
            from sale in sales
            join quotation in quotations on sale.QuotationId equals quotation.Id
            select new { sale, quotation };

        var total = await joined.CountAsync(cancellationToken);
        var items = await joined
            // Por fecha de conversión, y el número como desempate: dos ventas del mismo instante
            // --el mismo segundo en una siembra de prueba-- tienen que paginar en un orden
            // estable, o una fila puede repetirse o saltearse entre páginas.
            .OrderByDescending(row => row.sale.ConvertedAt)
            .ThenByDescending(row => row.sale.SaleNumber)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(row => new SaleWithQuotation(row.sale, row.quotation))
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public Task<bool> AnyForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        SaleStatus? status,
        SalePaymentStatus? paymentStatus,
        DateOnly? convertedFrom,
        DateOnly? convertedTo,
        string? saleNumber,
        CancellationToken cancellationToken)
    {
        var (sales, quotations) = Filtered(
            tenantId, clientId, clientIds, advisorId, status, paymentStatus, convertedFrom, convertedTo, saleNumber);
        return (from sale in sales
                join quotation in quotations on sale.QuotationId equals quotation.Id
                select sale.Id)
            .AnyAsync(cancellationToken);
    }

    // Keyset sobre el orden exacto del listado —(ConvertedAt, SaleNumber), los dos descendentes—:
    // el lote siguiente es "lo que viene después de la última venta leída" y no un offset, así que
    // una venta convertida o que sale del filtro durante el export no repite ni saltea filas
    // (spec 2026-09-12, D8). El corte va sobre `sales`, antes del join, igual que los filtros. EF
    // no compara tuplas: la condición va en su forma OR. La sirve IX_sales_tenant_converted_at_number.
    public async Task<IReadOnlyList<SaleWithQuotation>> ListForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        SaleStatus? status,
        SalePaymentStatus? paymentStatus,
        DateOnly? convertedFrom,
        DateOnly? convertedTo,
        string? saleNumber,
        SaleExportCursor? after,
        int limit,
        CancellationToken cancellationToken)
    {
        var (sales, quotations) = Filtered(
            tenantId, clientId, clientIds, advisorId, status, paymentStatus, convertedFrom, convertedTo, saleNumber);

        if (after is not null)
        {
            var convertedAt = after.ConvertedAt;
            var number = after.SaleNumber;
            // `string.Compare` se traduce a `<` sobre la columna, con la collation del ORDER BY.
            // Si el build marca CA1310, ver la nota de QuotationRepository en Task 11.
            sales = sales.Where(sale =>
                sale.ConvertedAt < convertedAt
                || (sale.ConvertedAt == convertedAt && string.Compare(sale.SaleNumber, number) < 0));
        }

        return await (
                from sale in sales
                join quotation in quotations on sale.QuotationId equals quotation.Id
                select new { sale, quotation })
            .OrderByDescending(row => row.sale.ConvertedAt)
            .ThenByDescending(row => row.sale.SaleNumber)
            .Take(limit)
            .Select(row => new SaleWithQuotation(row.sale, row.quotation))
            .ToListAsync(cancellationToken);
    }

    // Los filtros del listado y de su Excel salen de acá y de ningún otro lado. Se aplican sobre
    // cada tabla por separado y el join se arma recién al final, proyectando ahí mismo: filtrar
    // después de proyectar a un tipo propio es lo que hacía que el proveedor no tradujera la
    // consulta y el endpoint respondiera 500. Sin comprobantes ni líneas: la fila no los muestra, y
    // traerlos sería el mismo N+1 que QuotationRepository.SearchAsync evita.
    private (IQueryable<Sale> Sales, IQueryable<Quotation> Quotations) Filtered(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        SaleStatus? status,
        SalePaymentStatus? paymentStatus,
        DateOnly? convertedFrom,
        DateOnly? convertedTo,
        string? saleNumber)
    {
        var sales = dbContext.Sales
            .AsNoTracking()
            .Where(sale => sale.TenantId == tenantId);

        if (status is { } saleStatus)
        {
            sales = sales.Where(sale => sale.Status == saleStatus);
        }

        if (paymentStatus is { } salePaymentStatus)
        {
            sales = sales.Where(sale => sale.PaymentStatus == salePaymentStatus);
        }

        var saleNumberPattern = LikePattern(saleNumber);
        if (saleNumberPattern is not null)
        {
            sales = sales.Where(sale =>
                EF.Functions.ILike(sale.SaleNumber, saleNumberPattern, LikeEscapeCharacter));
        }

        if (convertedFrom is { } from)
        {
            var fromUtc = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            sales = sales.Where(sale => sale.ConvertedAt >= fromUtc);
        }

        if (convertedTo is { } to)
        {
            // Limite superior exclusivo al dia siguiente, igual que el listado de cotizaciones:
            // "hasta el 30" incluye todo el 30, no solo su instante 00:00:00.
            var toUtcExclusive = new DateTimeOffset(
                to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            sales = sales.Where(sale => sale.ConvertedAt < toUtcExclusive);
        }

        // Cliente y asesora viven en la cotizacion: sus filtros se aplican de ese lado.
        var quotations = dbContext.Quotations
            .AsNoTracking()
            .Where(quotation => quotation.TenantId == tenantId);

        if (clientId is { } client)
        {
            quotations = quotations.Where(quotation => quotation.ClientId == client);
        }

        if (clientIds is not null)
        {
            quotations = quotations.Where(quotation => clientIds.Contains(quotation.ClientId));
        }

        if (advisorId is { } advisor)
        {
            quotations = quotations.Where(quotation => quotation.AdvisorId == advisor);
        }

        return (sales, quotations);
    }
```

> Es el mismo código de filtros de `SaleRepository.cs:84-138`, movido sin cambiar una condición: es la consulta que ya pasa `SaleListApiTests`. Borrar esas líneas del `SearchAsync` viejo, junto con su comentario (`:79-83`), que pasa a `Filtered`.

`src/Modules/Quotations/Modules.Quotations.Application/ExportSales.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Pide el listado de ventas en un <c>.xlsx</c> por correo (spec 2026-09-12). Mismo modelo que
/// <see cref="ExportQuotationsCommand"/>: los filtros de <see cref="ListSalesQuery"/> sin
/// paginación, con el rango de conversión obligatorio y de a lo sumo un año.
/// </summary>
public sealed record ExportSalesCommand(
    Guid TenantId,
    Guid? ClientId,
    Guid? AdvisorId,
    string? Status,
    string? PaymentStatus,
    DateOnly? ConvertedFrom,
    DateOnly? ConvertedTo,
    string? ClientCuc,
    string? SaleNumber) : ICommand<ExportJobAccepted>;

/// <summary>Los filtros tal como quedan en <c>export_jobs.filters</c>.</summary>
public sealed record SalesExportFilters(
    Guid? ClientId,
    Guid? AdvisorId,
    string? Status,
    string? PaymentStatus,
    DateOnly ConvertedFrom,
    DateOnly ConvertedTo,
    string? ClientCuc,
    string? SaleNumber);

/// <summary>El mismo rango que <see cref="ExportQuotationsValidator"/>, sobre la fecha de la venta
/// (D3). Validador para que el 422 lleve el mapa <c>errors</c>.</summary>
public sealed class ExportSalesValidator : AbstractValidator<ExportSalesCommand>
{
    public ExportSalesValidator()
    {
        RuleFor(command => command.ConvertedFrom)
            .NotNull()
            .WithMessage("convertedFrom is required.");
        RuleFor(command => command.ConvertedTo)
            .NotNull()
            .WithMessage("convertedTo is required.");
        RuleFor(command => command.ConvertedTo)
            .GreaterThanOrEqualTo(command => command.ConvertedFrom!.Value)
            .When(command => command.ConvertedFrom is not null && command.ConvertedTo is not null)
            .WithMessage("convertedTo must be on or after convertedFrom.");
        RuleFor(command => command.ConvertedTo)
            .LessThanOrEqualTo(command => command.ConvertedFrom!.Value.AddYears(1))
            .When(command => command.ConvertedFrom is not null && command.ConvertedTo is not null)
            .WithMessage("The range from convertedFrom to convertedTo cannot exceed one year.");
    }
}

public sealed class ExportSalesHandler(
    ISaleRepository repository,
    IQuotationCustomerLookup customerLookup,
    IExportJobQueue queue,
    IQuotationsUnitOfWork unitOfWork,
    IValidator<ExportSalesCommand> validator,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ExportSalesCommand, ExportJobAccepted>
{
    public async Task<ExportJobAccepted> HandleAsync(
        ExportSalesCommand command,
        CancellationToken cancellationToken)
    {
        // D4, mismo orden que cotizaciones. 1: tenant y permiso de ventas.
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, SalesPermissions.SaleRead);

        // 2: filtros y rango.
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var status = SaleListing.ParseStatus(command.Status);
        var paymentStatus = SaleListing.ParsePaymentStatus(command.PaymentStatus);
        var advisorId = command.AdvisorId is { } advisor ? new MemberId(advisor) : (MemberId?)null;
        var clientIds = await SaleListing.ResolveClientIdsByCucAsync(
            customerLookup, command.TenantId, command.ClientCuc, cancellationToken);

        // 3: al menos una venta.
        var anyRow = await repository.AnyForExportAsync(
            command.TenantId,
            command.ClientId,
            clientIds,
            advisorId,
            status,
            paymentStatus,
            command.ConvertedFrom,
            command.ConvertedTo,
            command.SaleNumber,
            cancellationToken);
        if (!anyRow)
        {
            throw new QuotationsDomainException(
                "sale.export.empty",
                "There are no sales matching the export filters.");
        }

        // 4: el mismo cupo que cotizaciones, contando los dos tipos.
        var pending = await queue.CountPendingAsync(
            command.TenantId, executionContext.SubjectId, cancellationToken);
        if (pending >= ExportJobLimits.PendingPerRequester)
        {
            throw new QuotationsDomainException(
                "sale.export.pending_limit",
                $"There are already {ExportJobLimits.PendingPerRequester} exports in progress for this user.");
        }

        var job = ExportJob.Enqueue(
            Guid.CreateVersion7(),
            command.TenantId,
            executionContext.SubjectId,
            ExportJobKind.Sales,
            ExportJobFilters.Serialize(new SalesExportFilters(
                command.ClientId,
                command.AdvisorId,
                command.Status,
                command.PaymentStatus,
                command.ConvertedFrom!.Value,
                command.ConvertedTo!.Value,
                command.ClientCuc,
                command.SaleNumber)),
            clock.UtcNow);
        queue.Add(job);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new ExportJobAccepted(job.Id, job.RequestedAt);
    }
}
```

`SaleEndpoints.cs`, después del mapeo de `GET /{saleId:guid}` (`:31-35`):

```csharp
        // El listado de ventas en un .xlsx por correo (spec 2026-09-12): mismo contrato que
        // `POST /quotations/export` —202, filtros del listado por query string, sin paginación—.
        // "export" no choca con "/{saleId:guid}": no es un guid.
        collection.MapPost("/export", ExportSalesAsync)
            .RequireAuthorization(SalesPermissions.SaleRead)
            .Produces<ExportJobAcceptedResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
```

y después de `ListSalesAsync` (`:70-96`):

```csharp
    private static async Task<IResult> ExportSalesAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        Guid? clientId = null,
        Guid? advisorId = null,
        string? status = null,
        string? paymentStatus = null,
        DateOnly? convertedFrom = null,
        DateOnly? convertedTo = null,
        string? clientCuc = null,
        string? saleNumber = null)
    {
        var accepted = await dispatcher.SendAsync(
            new ExportSalesCommand(
                tenantId, clientId, advisorId, status, paymentStatus, convertedFrom, convertedTo,
                clientCuc, saleNumber),
            cancellationToken);

        return Results.Accepted(value: new ExportJobAcceptedResponse(accepted.JobId, accepted.RequestedAt));
    }
```

`QepServiceCollectionExtensions.cs`, después del registro de `ListSalesHandler` (`:340-342`):

```csharp
        services.AddScoped<
            ICommandHandler<ExportSalesCommand, ExportJobAccepted>,
            ExportSalesHandler>();
```

(El validador lo registra `AddValidatorsFromAssemblyContaining<CreateQuotationValidator>()`, que ya barre el ensamblado de Application.)

- [ ] **Step 4: Correr y verificar que pasan**

```powershell
dotnet build --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-build --filter "FullyQualifiedName~ExportSales|FullyQualifiedName~ListSalesHandlerTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build --filter "FullyQualifiedName~SaleExportApiTests|FullyQualifiedName~SaleListApiTests"
```

Esperado: todo en verde; `SaleExportApiTests` con `Passed: 6`, y `ListSalesHandlerTests`/`SaleListApiTests` igual que en el baseline (el refactor no cambia comportamiento). Pegar las salidas.

- [ ] **Step 5: Stage** (Git Bash)

```bash
test "$(git branch --show-current)" = "feature/export-asincrono" || { echo ABORT; exit 1; }
git add src/Modules/Quotations/Modules.Quotations.Application/SaleListing.cs \
  src/Modules/Quotations/Modules.Quotations.Application/ListSales.cs \
  src/Modules/Quotations/Modules.Quotations.Application/ISaleRepository.cs \
  src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/SaleRepository.cs \
  src/Modules/Quotations/Modules.Quotations.Application/ExportSales.cs \
  src/Modules/Quotations/Modules.Quotations.Api/SaleEndpoints.cs \
  src/Bootstrapper/QepServiceCollectionExtensions.cs \
  tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs \
  tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportSalesValidatorTests.cs \
  tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportSalesHandlerTests.cs \
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/SaleExportApiTests.cs
```

---

### Task 13: `SalesExportProcessor` y commit de ventas

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/SalesExportProcessor.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (junto al procesador de cotizaciones)
- Modify: `README.md` (el párrafo que reescribió Task 11)
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs` (recibe `WaitForEmailStatusAsync`)
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationExportApiTests.cs` (entrega `WaitForEmailStatusAsync` al harness)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/SalesExportProcessorTests.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/SaleExportApiTests.cs` (prueba de punta a punta)

**Interfaces:**
- Consumes: `SalesExportFilters`, `SaleListing`, `ISaleRepository.ListForExportAsync` (Task 12); `ExportJobFilters`, `ExportFileNames` (Tasks 10–11); `IExportWorkbookWriter`, `IExportFileStorage` (Tasks 8–9); dobles `RecordingExportWorkbookWriter`, `RecordingExportFileStorage`, `StubSaleListRepository` (Tasks 11–12); `ExportWorkbookReader` (Task 11).
- Produces: `public sealed class SalesExportProcessor : IExportJobProcessor` con `const string SheetName = "Ventas"`, `const string FilePrefix = "ventas"`, `static readonly IReadOnlyList<ExportColumn> Columns` = `Venta, Cliente, Asesor, Fecha, Pago, Estado, Moneda, Total` (hallazgo 2); `public static Task<string?> QuotationsApiHarness.WaitForEmailStatusAsync(string connectionString, Guid recipientId, string templateRef)`.

- [ ] **Step 1: Escribir las pruebas que fallan**

Mover `WaitForEmailStatusAsync` de `QuotationExportApiTests.cs` a `QuotationsApiHarness.cs` (con `using Npgsql;` en el harness), cambiando `private static` por `public static`; en `QuotationExportApiTests.cs` borrar el método y el `using Npgsql;`. La llamada queda igual porque el archivo ya importa el harness con `using static`.

`tests/Modules/Quotations/Modules.Quotations.UnitTests/SalesExportProcessorTests.cs`:

```csharp
using System.Globalization;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El procesador de ventas: las columnas de la tabla de ventas en su orden (hallazgo 2 del plan),
/// lectura por lotes con el filtro del listado, y los mismos fallos definitivos que cotizaciones.
/// </summary>
public sealed class SalesExportProcessorTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 15, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 12);

    [Fact]
    public async Task WritesTheSalesListColumnsInTheirOrder()
    {
        var writer = new RecordingExportWorkbookWriter();
        var processor = NewProcessor(new StubSaleListRepository(NewRow("VEN-2026-0001", paymentMethod: null)), writer);

        await processor.ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Ventas", writer.SheetName);
        Assert.Equal(
            ["Venta", "Cliente", "Asesor", "Fecha", "Pago", "Estado", "Moneda", "Total"],
            writer.Columns.Select(column => column.Header));
        var row = Assert.Single(writer.Rows);
        Assert.Equal("VEN-2026-0001", row[0].Text);
        Assert.Equal("Ferretería El Tornillo", row[1].Text);
        Assert.Equal("asesora@qcode.co", row[2].Text);
        Assert.Equal(Now.ToString("O", CultureInfo.InvariantCulture), row[3].Text);
        // Sin forma de pago, la columna cae al estado del pago, igual que la tabla.
        Assert.Equal("PaymentPending", row[4].Text);
        Assert.Equal("Pending", row[5].Text);
        Assert.Equal(0m, row[7].Number);
    }

    [Fact]
    public async Task PagoShowsThePaymentMethodWhenThereIsOne()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubSaleListRepository(NewRow("VEN-2026-0001", "Transferencia")), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Transferencia", Assert.Single(writer.Rows)[4].Text);
    }

    [Fact]
    public async Task ReadsInBatchesOfAThousandUntilAShortBatch()
    {
        var rows = Enumerable.Range(1, ExportJobLimits.BatchSize + 1)
            .Select(number => NewRow($"VEN-2026-{number:0000}", paymentMethod: null))
            .ToArray();
        var repository = new StubSaleListRepository(rows);

        var result = await NewProcessor(repository).ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(2, repository.ExportCalls);
        Assert.Equal(ExportJobLimits.BatchSize + 1, result.RowCount);
    }

    // Keyset (D8): el lote siguiente arranca después de la última venta del anterior. Todas del
    // mismo instante: el número desempata, de mayor a menor, como en el listado.
    [Fact]
    public async Task EachBatchStartsAfterTheLastRowOfThePreviousOne()
    {
        var rows = Enumerable.Range(1, ExportJobLimits.BatchSize + 1)
            .Select(number => NewRow($"VEN-2026-{number:0000}", paymentMethod: null))
            .ToArray();
        var repository = new StubSaleListRepository(rows);

        await NewProcessor(repository).ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(
            new SaleExportCursor?[] { null, new SaleExportCursor(Now, "VEN-2026-0002") },
            repository.ExportCursors);
    }

    [Fact]
    public async Task UploadsAsVentasUnderTheJob()
    {
        var storage = new RecordingExportFileStorage();
        var job = NewJob();

        var result = await NewProcessor(new StubSaleListRepository(NewRow("VEN-2026-0001", null)), storage: storage)
            .ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal("ventas-2026-09-12-1530.xlsx", result.FileName);
        Assert.Equal(job.Id, storage.Upload!.JobId);
    }

    [Fact]
    public async Task FiltersWithWhatTheRequestStored()
    {
        var repository = new StubSaleListRepository(NewRow("VEN-2026-0001", null));
        var job = NewJob(new SalesExportFilters(ClientId, AdvisorId.Value, "approved", "fullpaymentreceived", From, To, null, "VEN"));

        await NewProcessor(repository).ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(
            new RecordedSaleExportSearch(
                ClientId, null, AdvisorId, SaleStatus.Approved, SalePaymentStatus.FullPaymentReceived, From, To, "VEN"),
            repository.LastExportSearch);
    }

    [Fact]
    public async Task NoSalesWhenItRunsIsDefinitive()
    {
        await Assert.ThrowsAsync<ExportJobDefinitiveException>(() =>
            NewProcessor(new StubSaleListRepository()).ProcessAsync(NewJob(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AStoredPaymentStatusThatNoLongerExistsIsDefinitive()
    {
        var job = NewJob(new SalesExportFilters(null, null, null, "Refunded", From, To, null, null));

        await Assert.ThrowsAsync<ExportJobDefinitiveException>(() =>
            NewProcessor(new StubSaleListRepository(NewRow("VEN-2026-0001", null)))
                .ProcessAsync(job, TestContext.Current.CancellationToken));
    }

    private static ExportJob NewJob(SalesExportFilters? filters = null) =>
        ExportJob.Enqueue(
            Guid.CreateVersion7(),
            TenantId,
            Guid.CreateVersion7(),
            ExportJobKind.Sales,
            ExportJobFilters.Serialize(filters ?? new SalesExportFilters(null, null, null, null, From, To, null, null)),
            Now);

    private static SaleWithQuotation NewRow(string saleNumber, string? paymentMethod)
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, AdvisorId, new DateOnly(2026, 10, 30),
            paymentMethod, notes: null, QuotationParties.Empty, billingAccount: null,
            customerWithRetention: false, customerVatSurplus: false, AdvisorId, Now);
        var sale = Sale.Create(
            SaleId.New(), TenantId, saleNumber, quotation.Id, SalePaymentStatus.PaymentPending,
            notes: null, AdvisorId, [], Now);
        return new SaleWithQuotation(sale, quotation);
    }

    private static SalesExportProcessor NewProcessor(
        StubSaleListRepository repository,
        RecordingExportWorkbookWriter? writer = null,
        RecordingExportFileStorage? storage = null) =>
        new(repository,
            new StubQuotationCustomerLookup(new QuotationCustomerRef(
                ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
                "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false)),
            new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            writer ?? new RecordingExportWorkbookWriter(),
            storage ?? new RecordingExportFileStorage(),
            new FixedClock(Now));
}
```

En `SaleExportApiTests.cs`, agregar `using System.Text.Json;`, `using Microsoft.EntityFrameworkCore;`, `using Microsoft.Extensions.DependencyInjection;` y `using Modules.Quotations.Infrastructure.Persistence;` y, antes de `private static string CurrentRange()`:

```csharp
    // De punta a punta: el POST encola, un tick arma el Excel con las filas del listado de ventas
    // en el orden de su tabla, y el correo sale.
    [Fact]
    public async Task TheWorkerTurnsTheRequestIntoTheSalesWorkbookAndTheEmail()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        await CreateSaleAsync(client, factory, tenantId);
        await CreateSaleAsync(client, factory, tenantId);

        var response = await client.PostAsync(
            $"{SalesUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);
        var accepted = await response.Content.ReadFromJsonAsync<AcceptedDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(accepted);

        Assert.Equal(ExportJobRunOutcome.Completed, await RunExportJobAsync(factory));

        var job = await FindExportJobAsync(factory, accepted.JobId);
        Assert.Equal(2, job.RowCount);
        Assert.Matches(@"^ventas-\d{4}-\d{2}-\d{2}-\d{4}\.xlsx$", job.FileName);
        var ready = Assert.Single(await OutboxMessagesAsync(factory, "quotations.export-ready.v1"));
        using (var payload = JsonDocument.Parse(ready.PayloadJson))
        {
            Assert.Equal("Sales", payload.RootElement.GetProperty("kind").GetString());
        }

        var sheet = ExportWorkbookReader.Read(await factory.ObjectStorage.DownloadAsync(
            $"exports/tenants/{tenantId:N}/jobs/{accepted.JobId:N}.xlsx", TestContext.Current.CancellationToken));
        var list = await client.GetFromJsonAsync<SalesPageResponse>(
            $"{SalesUrl(tenantId)}?{CurrentRange()}", TestContext.Current.CancellationToken);
        var items = list!.Items.ToArray();
        Assert.Equal("Ventas", sheet.Name);
        Assert.Equal(["Venta", "Cliente", "Asesor", "Fecha", "Pago", "Estado", "Moneda", "Total"], sheet.Rows[0]);
        Assert.Equal(items.Select(item => item.SaleNumber), sheet.Rows.Skip(1).Select(row => row[0]));
        var first = sheet.Rows[1];
        Assert.Equal(items[0].ClientName, first[1]);
        Assert.Equal(items[0].AdvisorEmail ?? string.Empty, first[2]);
        Assert.Equal(items[0].ConvertedAt, DateTimeOffset.Parse(first[3], CultureInfo.InvariantCulture));
        Assert.Equal(items[0].PaymentMethod ?? items[0].PaymentStatus, first[4]);
        Assert.Equal(items[0].Status, first[5]);
        Assert.Equal(items[0].Currency, first[6]);
        Assert.True(sheet.NumericCells[1][7]);
        Assert.Equal(items[0].Total, decimal.Parse(first[7], CultureInfo.InvariantCulture));

        Assert.Equal("Sent", await WaitForEmailStatusAsync(
            database.GetConnectionString(), ownerUserId, "quotations.export-ready.v1"));
    }

    // Keyset y no offset (D8, hallazgo 11), sobre el orden del listado de ventas. Mismo diseño que
    // la de cotizaciones: de a una fila, una venta convertida entre el lote 1 y el 2 (con offset,
    // se repetiría la del borde) y una ya leída que sale del filtro entre el 2 y el 3 (con offset,
    // se saltearía una).
    [Fact]
    public async Task SalesCreatedOrLeavingTheFilterBetweenBatchesNeitherRepeatNorSkip()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        for (var converted = 0; converted < 3; converted++)
        {
            await CreateSaleAsync(client, factory, tenantId);
        }

        var expected = (await ReadPendingBatchAsync(factory, tenantId, after: null, limit: 100))
            .Select(row => row.Sale.Id)
            .ToArray();
        Assert.Equal(3, expected.Length);

        var first = await ReadPendingBatchAsync(factory, tenantId, after: null, limit: 1);
        await CreateSaleAsync(client, factory, tenantId);
        var second = await ReadPendingBatchAsync(factory, tenantId, CursorOf(first), limit: 1);
        await SetSaleStatusAsync(factory, second.Single().Sale.Id, SaleStatus.Approved);
        var third = await ReadPendingBatchAsync(factory, tenantId, CursorOf(second), limit: 1);
        var fourth = await ReadPendingBatchAsync(factory, tenantId, CursorOf(third), limit: 1);

        Assert.Equal(expected, first.Concat(second).Concat(third).Select(row => row.Sale.Id));
        Assert.Empty(fourth);
    }

    // El repositorio real, contra Postgres. `Pending` es el filtro que la venta aprobada abandona.
    private static async Task<IReadOnlyList<SaleWithQuotation>> ReadPendingBatchAsync(
        QepApiFactory factory, Guid tenantId, SaleExportCursor? after, int limit)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISaleRepository>().ListForExportAsync(
            tenantId,
            clientId: null,
            clientIds: null,
            advisorId: null,
            SaleStatus.Pending,
            paymentStatus: null,
            today.AddDays(-7),
            today.AddDays(1),
            saleNumber: null,
            after,
            limit,
            TestContext.Current.CancellationToken);
    }

    private static SaleExportCursor CursorOf(IReadOnlyList<SaleWithQuotation> batch) =>
        new(batch[^1].Sale.ConvertedAt, batch[^1].Sale.SaleNumber);

    // Directo en la base: lo que se prueba es la lectura, no la aprobación.
    private static async Task SetSaleStatusAsync(QepApiFactory factory, SaleId saleId, SaleStatus status)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var updated = await dbContext.Sales
            .Where(sale => sale.Id == saleId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(sale => sale.Status, status),
                TestContext.Current.CancellationToken);
        Assert.Equal(1, updated);
    }
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~SalesExportProcessorTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~SaleExportApiTests.TheWorkerTurnsTheRequestIntoTheSalesWorkbookAndTheEmail"
```

Esperado: la primera no compila (`error CS0246: … 'SalesExportProcessor' could not be found`). La segunda compila y falla en `Assert.Equal() Failure` con `Expected: Completed` / `Actual: Failed`: sin procesador de ventas, el runner cierra el job como `NoProcessor` (Task 3). Pegar las salidas.

- [ ] **Step 3: Implementar**

`src/Modules/Quotations/Modules.Quotations.Application/SalesExportProcessor.cs`:

```csharp
using System.Globalization;
using BuildingBlocks.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Arma el Excel del listado de ventas en el worker (D7, D8). Mismo esquema que
/// <see cref="QuotationsExportProcessor"/>: lotes de mil con keyset y el filtro del listado
/// (<see cref="SaleListing"/> y el <c>Filtered</c> de SaleRepository), streaming y subida con el id
/// del job.
/// </summary>
public sealed class SalesExportProcessor(
    ISaleRepository repository,
    IQuotationCustomerLookup customerLookup,
    IQuotationAdvisorLookup advisorLookup,
    IExportWorkbookWriter writer,
    IExportFileStorage storage,
    IClock clock)
    : IExportJobProcessor
{
    public const string SheetName = "Ventas";

    public const string FilePrefix = "ventas";

    /// <summary>
    /// Las de la tabla de ventas en su orden (sale-table.tsx: Venta, Cliente, Asesora, Fecha, Pago,
    /// Estado, Total), con la moneda aparte del total y "Asesor" como en el Excel de cotizaciones.
    /// "Pago" replica el respaldo de la tabla: la forma de pago, o el estado del pago mientras la
    /// forma llegue vacía.
    /// </summary>
    public static readonly IReadOnlyList<ExportColumn> Columns =
    [
        new("Venta", 18),
        new("Cliente", 40),
        new("Asesor", 32),
        new("Fecha", 34),
        new("Pago", 24),
        new("Estado", 12),
        new("Moneda", 10),
        new("Total", 16),
    ];

    public ExportJobKind Kind => ExportJobKind.Sales;

    public async Task<ExportJobResult> ProcessAsync(ExportJob job, CancellationToken cancellationToken)
    {
        var filters = ExportJobFilters.Read<SalesExportFilters>(job);
        var (status, paymentStatus) = ParseStatuses(filters);
        var advisorId = filters.AdvisorId is { } advisor ? new MemberId(advisor) : (MemberId?)null;
        var clientIds = await SaleListing.ResolveClientIdsByCucAsync(
            customerLookup, job.TenantId, filters.ClientCuc, cancellationToken);
        var generatedAt = clock.UtcNow;

        using var workbook = writer.Create(SheetName, Columns);
        var rowCount = 0;
        SaleExportCursor? after = null;
        while (true)
        {
            // Keyset (D8): el lote siguiente arranca después de la última venta leída.
            var batch = await repository.ListForExportAsync(
                job.TenantId,
                filters.ClientId,
                clientIds,
                advisorId,
                status,
                paymentStatus,
                filters.ConvertedFrom,
                filters.ConvertedTo,
                filters.SaleNumber,
                after,
                ExportJobLimits.BatchSize,
                cancellationToken);

            if (batch.Count > 0)
            {
                var rows = await SaleListing.ToListItemsAsync(
                    customerLookup, advisorLookup, job.TenantId, batch, cancellationToken);
                foreach (var row in rows)
                {
                    workbook.AppendRow(ToCells(row));
                }

                after = new SaleExportCursor(batch[^1].Sale.ConvertedAt, batch[^1].Sale.SaleNumber);
            }

            rowCount += batch.Count;
            if (batch.Count < ExportJobLimits.BatchSize)
            {
                break;
            }
        }

        if (rowCount == 0)
        {
            throw new ExportJobDefinitiveException(
                "Empty: no sales matched the export filters when the export ran.");
        }

        var fileName = ExportFileNames.For(FilePrefix, generatedAt);
        var upload = await storage.UploadAsync(
            job.TenantId, job.Id, fileName, workbook.Complete(), cancellationToken);
        return new ExportJobResult(fileName, rowCount, upload.DownloadUrl, upload.ExpiresAt);
    }

    private static (SaleStatus? Status, SalePaymentStatus? PaymentStatus) ParseStatuses(SalesExportFilters filters)
    {
        try
        {
            return (SaleListing.ParseStatus(filters.Status), SaleListing.ParsePaymentStatus(filters.PaymentStatus));
        }
        catch (QuotationsDomainException exception)
        {
            throw new ExportJobDefinitiveException($"UnreadableFilters: {exception.Message}", exception);
        }
    }

    private static ExportCell[] ToCells(SaleListItemDto row) =>
    [
        ExportCell.OfText(row.SaleNumber),
        ExportCell.OfText(row.ClientName),
        ExportCell.OfText(row.AdvisorEmail),
        ExportCell.OfText(row.ConvertedAt.ToString("O", CultureInfo.InvariantCulture)),
        ExportCell.OfText(row.PaymentMethod ?? row.PaymentStatus),
        ExportCell.OfText(row.Status),
        ExportCell.OfText(row.Currency),
        ExportCell.OfNumber(row.Total),
    ];
}
```

En `QepServiceCollectionExtensions.cs`, debajo del procesador de cotizaciones:

```csharp
        services.AddScoped<IExportJobProcessor, SalesExportProcessor>();
```

`README.md`: el párrafo que dejó Task 11 pasa a nombrar las tres exportaciones:

```markdown
La exportación del padrón de clientes (`POST /tenants/{tenantId}/customers/export`) y las de los
listados de cotizaciones y ventas (`POST /tenants/{tenantId}/quotations/export`,
`POST /tenants/{tenantId}/sales/export`) no devuelven el archivo: lo suben bajo el prefijo
`exports/` del **bucket privado** y le mandan a quien la pidió un correo con una URL prefirmada.
Clientes arma el Excel dentro del request; cotizaciones y ventas contestan `202` y lo encolan en
`quotations.export_jobs`, y `ExportJobWorker` lo arma en segundo plano con la clave
`exports/tenants/{tenantId}/jobs/{jobId}.xlsx` —un reintento pisa el mismo objeto—. La vigencia
del enlace es `Storage:ExportUrlHours` (24 h por defecto), propia y no
`Storage:PresignedUrlMinutes`: aquellas URLs las consume un navegador que ya está en pantalla, y
ésta espera en una bandeja de entrada.
```

- [ ] **Step 4: Correr y verificar que pasan, más la regresión del commit**

```powershell
dotnet build --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-build --filter "FullyQualifiedName~SalesExportProcessorTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build --filter "FullyQualifiedName~SaleExportApiTests|FullyQualifiedName~QuotationExportApiTests"
dotnet restore --locked-mode
dotnet format --verify-no-changes
```

Esperado: `Passed: 8` en el procesador, `Passed: 18` entre los dos archivos de integración (8 de ventas, 10 de cotizaciones), `--locked-mode` y `format` sin cambios. Después la suite completa contra el baseline como en Task 5 Step 4 (`$after = Join-Path $env:TEMP "qep-export-asincrono-commit4"`): `Compare-Object` vacío. Pegar las salidas.

- [ ] **Step 5: Commit** (Git Bash)

```bash
test "$(git branch --show-current)" = "feature/export-asincrono" || { echo ABORT; exit 1; }
git add src/Modules/Quotations/Modules.Quotations.Application/SalesExportProcessor.cs \
  src/Bootstrapper/QepServiceCollectionExtensions.cs \
  README.md \
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs \
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationExportApiTests.cs \
  tests/Modules/Quotations/Modules.Quotations.UnitTests/SalesExportProcessorTests.cs \
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/SaleExportApiTests.cs
git status --short
git commit -m "feat(sales): exportar ventas por correo" \
  -m "POST /sales/export con el mismo contrato que cotizaciones: filtros del listado de ventas, rango de conversión de hasta un año, sale.export.empty y sale.export.pending_limit, y el cupo de tres pendientes compartido con las exportaciones de cotizaciones. SalesExportProcessor lee por keyset sobre el orden del listado (fecha de conversión y número) y escribe las columnas de la tabla de ventas en su orden; el parseo de estados y la resolución del CUC pasan a SaleListing, y los filtros de SaleRepository a un método compartido con el listado."
```

`git status --short` antes del commit: sólo archivos de Tasks 12–13 staged.

---

## Después del último commit

- `git push -u origin feature/export-asincrono` y PR a `develop`, después de que el developer revise.
- **Despliegue: backend primero.** La migración `AddExportJobs` corre al arrancar (`QuotationsDatabaseInitializer`). Recién con el backend arriba se despliega el frontend de `feature/export-asincrono`.
- Verificar que la regla de lifecycle `expire-exports` sigue en el bucket privado: `npx wrangler r2 bucket lifecycle list <bucket-privado>` tiene que listarla con el prefijo `exports/`. Ya existe (hallazgo 8, README § Reportes exportados); no hay que crearla.

## Riesgos y pendientes

- **Memoria no medida** (spec): el streaming acota el riesgo, pero hay que medir un export de un año real en un pod de 1Gi antes de dar el tema por cerrado.
- **Sin snapshot transaccional** (spec, Riesgos): el keyset no repite ni saltea filas, pero cada lote lee el estado de ese momento; una fila que cambia antes de que le toque sale con el valor nuevo, o no sale si dejó el filtro. Leer en `REPEATABLE READ` lo cerraría a costa de sostener una transacción minutos.
- **CA1310 sobre `string.Compare`** (Tasks 11 y 12): si el analizador lo marca, la supresión local sería la primera escrita a mano en `src/`. Alternativa sin supresión: `EF.Functions.LessThan` de Npgsql con valores de fila, que la spec no eligió (pidió la forma OR).
- **Límite de pendientes no atómico:** dos pedidos simultáneos con dos pendientes pueden pasar los dos (quedan cuatro). Es un freno de doble clic y abuso, no un invariante; un `advisory lock` lo cerraría si hiciera falta.
- **Hora del nombre de archivo en UTC:** coherente con el vencimiento del correo ("UTC"), pero una asesora en Colombia ve la hora corrida cinco horas. Si molesta, se pasa a la zona del tenant.
- **Clientes y Catalog siguen sincrónicos** (D15): recomendado como trabajo aparte.

## Cobertura de la spec

| Punto | Dónde |
| --- | --- |
| D1 asíncrono de verdad | Tasks 3–5 (runner y worker), 10 y 12 (el request sólo encola), 11 y 13 (e2e) |
| D2 cola en Postgres, puerto `IExportJobQueue` | Tasks 1–4 |
| D3 rango de hasta un año | Tasks 10 y 12 (validadores y sus pruebas, bisiestos incluidos) |
| D4 orden de validaciones y códigos | Tasks 10 y 12 (pruebas de orden unitarias e integración) |
| D5 contrato `POST` + `202 { jobId, requestedAt }` | Tasks 10 y 12 |
| D6 `SKIP LOCKED` y lease | Task 4 (SQL, concurrencia, lease vencido) |
| D7 un worker, concurrencia 1, despacho por kind | Tasks 3 y 5 |
| D8 streaming, lotes de 1.000, columnas, anchos, nombres, OpenXML directo | Tasks 8, 10 (paquetes), 11, 13 |
| D8 keyset en vez de offset, con sus índices | Task 2 (índices), Tasks 11 y 13 (repositorios, procesadores y la prueba entre lotes) |
| D9 storage con clave por `jobId` bajo `exports/`, vigencia `ExportUrlHours` | Task 9 |
| D10 cierre en una transacción | Tasks 3 y 4 |
| D11 cuatro intentos, transitorio/definitivo/worker muerto | Tasks 1, 3, 4, 11, 13 |
| D12 correos | Tasks 6–7 (y e2e de 11 y 13) |
| D13 retención | Tasks 3–5; la regla de lifecycle ya existe y cubre `exports/` (hallazgo 8, Task 9) |
| D14 frontend | Plan de frontend |
| D15 fuera de alcance | Nada; ver «Riesgos» |
| Modelo de datos e índices | Task 2 |
| Qué pasa con `572200c` | Task 10 (se va el `GET`, el builder y ClosedXML; se reusan validador, `QuotationListing`, `FilteredQuery`, `ListForExportAsync`) |
| Entrega en cuatro commits | Tasks 5, 7, 11, 13 |


# Embudo de ventas — backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el asesor tenga un embudo de ventas: módulo nuevo `Pipeline` con etapas configurables, negocios enlazados al chat y a la cotización, historial de etapas desde el día 1, triage de conversaciones, señal de estancado, resumen para la ficha del cliente y los tres eventos de Quotations que mueven el negocio solo.

**Architecture:** Módulo nuevo `Pipeline` en cuatro capas (`Domain`, `Application`, `Infrastructure`, `Api`), schema `pipeline`, capacidad `pipeline` en `tenancy.tenant_modules` y `PipelineLayerTests`. El dominio es puro (embudo y etapas, negocio con su historial pendiente, foto de cotización, catálogos de motivos, reglas de triage y estancado). La unidad de trabajo vacía el historial pendiente de cada `Deal` rastreado en el mismo `SaveChanges`, así ningún handler puede olvidarlo. Quotations escribe tres eventos en su outbox, en la misma transacción que el cambio; un worker de Pipeline los consume con reclamo e inbox propio (copia de `ClaimedOutboxConsumer` de Identity) y delega en `QuotationEventApplier`. Messaging, Customers y las membresías entran por tres puertos de lectura con adaptadores en Bootstrapper; Pipeline no referencia el `Application` de ningún módulo salvo Tenancy.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql 10, FluentValidation 12, xUnit v3, Testcontainers (`postgres:18-alpine`).

**Spec:** `docs/superpowers/specs/2026-10-13-embudo-de-ventas-design.md` (commit `c74b955`, más D38 sin commitear: la carrera del triage).

Este plan cubre **sólo el backend**; el frontend es otro slice en `qep-frontend`. Rutas, formas JSON, nombres de eventos y códigos de error son **contrato**: no se cambian sin cambiar el spec. Quien ejecuta lee el spec, este plan y el `CLAUDE.md` del repo.

## Antes de empezar (leer una vez)

- **Rutas.** Se trabaja en el checkout principal. Abajo, `$B` es
  `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend`. El estado del shell no persiste: cada bloque vuelve a definir `$B`. Los comandos son **PowerShell** (Windows 5.1: sin `&&`, `curl.exe` y no `curl`).
- **Rama.** `feature/embudo-de-ventas` (el spec es `c74b955`). Se comprueba con `git branch --show-current` **en el momento** de commitear; si es otra, se aborta.
- **`Api.exe` o `dotnet … Api.dll` corriendo bloquea** `dotnet build`, `dotnet test` y `dotnet ef` (`MSB3021`). Antes del primer build de cada tarea:

  ```powershell
  Get-CimInstance Win32_Process -Filter "Name = 'Api.exe' OR Name = 'dotnet.exe'" |
    Where-Object { $_.CommandLine -match 'Api(\.dll|\.exe)' } |
    Select-Object ProcessId, Name, CommandLine
  ```

  Si sale algo, **no se mata**: puede ser la sesión de depuración del developer. Se detiene la tarea y se avisa.
- **Pruebas.** Siempre en **primer plano**, nunca con `| Select-Object`, `| tail` ni otro pipe (el pipe esconde el exit code). Por tarea **sólo las clases tocadas**, con un filtro por proyecto (`FullyQualifiedName~A|FullyQualifiedName~B`). Si un paso corre dos proyectos o más: primero `dotnet build "$B\Backend.slnx" --no-restore` con guard de `$LASTEXITCODE`, después cada `dotnet test … --no-build`. La suite completa corre **una sola vez**, en la Task 15, y se compara **por nombre** contra la línea base de la Task 0. Docker tiene que estar corriendo para las de integración.
- **TDD estricto:** RED antes que GREEN, con la salida **literal** de las dos corridas en el handoff de cada tarea. Un RED por compilación vale cuando la prueba nombra un tipo o miembro que todavía no existe; se copia el `error CS…` literal. Un RED de integración por ruta inexistente (404 donde se esperaba otra cosa) también vale; se copia el `Assert.Equal() Failure` literal.
- **`TreatWarningsAsErrors` + `AnalysisLevel 10.0-recommended`.** Lo que muerde:
  - xUnit1051: toda llamada que acepte `CancellationToken` en una prueba recibe `TestContext.Current.CancellationToken`.
  - xUnit2013: nada de `Assert.Equal(0, x.Count)`; `Assert.Empty` / `Assert.Single`.
  - CA1034: nada de tipos anidados públicos; por eso `SeedStage` es de primer nivel.
  - CA1873 / CA1848: los logs van con `[LoggerMessage]` y lo costoso en una variable local antes de la llamada.
  - CS9124: un parámetro del constructor primario no inicializa un campo y además se usa en un método.
  - Si el analizador marca algo que el plan no previó, se corrige **sin cambiar el comportamiento** y con el patrón del código vecino; se anota en el handoff.
- **Archivos.** Se escriben con las herramientas de edición, **nunca** con `Set-Content`/`Out-File` de PowerShell 5.1 (rompe las tildes: doble UTF-8 y BOM). Antes de cada commit, `git status --short` para ver que no quedó un `.cs` de 0 bytes ni un archivo ajeno (ya pasó con subagentes). Se agregan **sólo** las rutas tocadas, explícitas; nunca `git add -A` ni `git add .`.
- **`dotnet format` no es compuerta** (CRLF: ~90k `ENDOFLINE` en el baseline). La compuerta es el build con analizadores.
- **Locks.** Un proyecto nuevo o una referencia nueva exigen `dotnet restore --force-evaluate` y commitear los `packages.lock.json` que cambien **junto** con el `.csproj`: el `Dockerfile` corre `--locked-mode` y falla con `NU1004` si no coinciden. El `Dockerfile` copia `src/` entero (`COPY src/ src/`), así que **no** lista `.csproj`: no se toca.
- **Migraciones** con el factory de diseño, nunca con `--startup-project`:

  ```powershell
  dotnet ef migrations add <Nombre> --project src/Modules/Pipeline/Modules.Pipeline.Infrastructure --context PipelineDbContext -o Persistence/Migrations
  ```

- **Commits:** conventional commits en español, como la historia (`feat(pipeline): …`). **Sin atribución de IA y sin trailer `Co-Authored-By`**, aunque el harness lo pida. Después de cada commit, `git log -1 --format=%B` para comprobarlo.
- **Secretos:** ninguno nuevo. No se imprime ningún valor de `user-secrets` ni de configuración.
- **Comentarios y documentación** en español, tuteando, con el estilo del código vecino (`<summary>` que cita la sección o la decisión del spec). Identificadores, códigos y rutas en inglés, como el resto del repo.

## Global Constraints

- Grupo de rutas: `/api/v1/tenants/{tenantId:guid}/pipeline`. Rutas exactas (spec §6 y §6.4):
  - `GET /board?owner=me|all` — `pipeline.deal.read` — `200 PipelineBoardDto` (sin `owner` = `me`).
  - `GET /deals/{dealId:guid}` — `pipeline.deal.read` — `200 DealDetailDto`.
  - `POST /deals` — `pipeline.deal.manage` — `201 DealDetailDto` (sin `Location`).
  - `PATCH /deals/{dealId:guid}` — `pipeline.deal.manage` — `200 DealDetailDto`.
  - `POST /deals/{dealId:guid}/move|win|lose|reopen` — `pipeline.deal.manage` — `200 DealDetailDto`.
  - `POST /deals/{dealId:guid}/quotations/{quotationId:guid}/link|primary` — `pipeline.deal.manage` — `200 DealDetailDto`.
  - `GET /customers/{customerId:guid}/summary?conversationId=` — `pipeline.deal.read` — `200 CustomerDealsSummaryDto`.
  - `GET /triage` — `pipeline.deal.read` — `200 TriageListDto`.
  - `POST /triage/{conversationId:guid}` (`action`: `create` | `dismiss`) — `pipeline.deal.manage` — `200 TriageResultDto`.
  - `GET /settings/stages` — `pipeline.deal.read`; `PUT /settings/stages` — `pipeline.settings.manage` — `200 PipelineStagesDto`.
  - `GET|PUT /settings/loss-reasons` y `/settings/dismiss-reasons` — `GET` con `pipeline.deal.read`, `PUT` con `pipeline.settings.manage` — `200 ReasonListDto`.
- Permisos: `pipeline.deal.read`, `pipeline.deal.manage`, `pipeline.settings.manage` (`PipelinePermissions.DealRead|DealManage|SettingsManage`), cada uno con su `PermissionDefinition` (categoría `"Pipeline"`, `RequiredModules: []`, ver P1), su política en `AddAuthorization` y de fábrica: `admin` los tres; `advisor` `DealRead` y `DealManage`; `billing` y `cashier` ninguno (D32).
- Capacidad: `TenantModuleKeys.Pipeline = "pipeline"`, último de `All`, dependencia `[customers]`, fuera de `DefaultForNewTenants`; el `CHECK` de `tenancy.tenant_modules` la incluye (D3, D30).
- Todo handler: tenant de la ruta distinto del activo o permiso faltante → 403 `authorization.denied`; capacidad apagada → 403 `tenancy.module_not_enabled`. Un id de otro tenant bajo la ruta propia responde 404 igual que uno inexistente.
- Escrituras sobre un negocio: `version` obligatoria (sin ella, 422 `validation.failed` con `errors.version`); distinta → 412 `concurrency.conflict`; responden el negocio completo.
- Eventos de Quotations (spec §4.5), en la misma transacción que el cambio, con payload camelCase:
  - `quotations.quotation.sent.v1`: `tenantId`, `quotationId`, `quotationNumber`, `clientId`, `advisorMemberId`, `total`, `currency`, `occurredAt`.
  - `quotations.quotation.converted.v1`: los mismos + `orderId`, `orderNumber`.
  - `quotations.order.cancelled.v1`: `tenantId`, `orderId`, `quotationId`, `occurredAt`.
- Consumidor del worker: `pipeline.quotation-events`, inbox `pipeline.inbox_messages` con PK `(consumer, message_id)`, esperas `5 s → 30 s → 5 min`.
- Semillas (D31), creadas en el primer acceso y editables: Nuevo 10 %/3 d → Calificado 25 %/5 d → Cotizado 50 %/7 d (etapa de cotización) → Negociación 75 %/7 d → Ganado 100 % → Perdido 0 %. Pérdida: Precio, Competencia, Sin respuesta, Sin presupuesto, Otro. Descarte: Soporte, Recompra, Proveedor, Spam, Otro. Nombre del embudo: `Embudo de ventas`.
- Tablero (D24, D33): todas las columnas incluso en cero, `Count` y `Totals` de la columna **entera**, `Totals` agrupados por moneda en el orden de `Currencies.OrderOf`, máximo 200 tarjetas por columna, `Won`/`Lost` sólo con `closed_at` en los últimos 30 días. Negocio sin monto: cuenta en `Count`, fuera de las sumas (D34).
- Triage (D20, D29, D35, D37, D38): sólo `create` y `dismiss`; excluye conversaciones sin `CustomerId`; máximo 200 ítems con el `Count` real, el entrante más reciente primero (sin entrante, al final). Cada decisión guarda el `LastInboundAt` contra el que se tomó (`decided_against_inbound_at`, no nulo; sin entrante, `TriageDecision.NoInbound` = `1970-01-01T00:00:00Z`) y el índice único `IX_triage_decisions_conversation_inbound` sobre `(tenant_id, conversation_id, decided_against_inbound_at)` hace que la segunda decisión concurrente falle con `pipeline.triage.already_decided` (422), traducido por **nombre de índice** en `PipelineUnitOfWork`; el negocio creado en ese mismo `SaveChanges` se revierte con ella.
- Límites: título 1–200, nota de pérdida ≤ 500, nombre de etapa 1–60, nombre de motivo 1–80, 1–20 etapas, 1–30 motivos activos por catálogo, días de estancamiento 1–365, probabilidad 0–100, monto 0–999 999 999 999,99 con ≤ 2 decimales, número de cotización ≤ 40.
- Auditoría por `IPipelineAuditRecorder`, `source = "pipeline"`, en el mismo `SaveChanges`. Lo del worker con `AuditActorType.System` y actor `Guid.Empty`.
- Sin claves de configuración nuevas, sin paquetes NuGet nuevos. Migraciones: `AddPipelineModuleKey` (Tenancy) y `InitialPipeline` (Pipeline).

## Review Focus

1. **Eventos fuera de orden o repetidos:** un `converted` que llega sin su `sent` (convertida desde `Draft`, o el `sent` esperando reintento) tiene que crear el negocio y ganarlo **una vez**; el mismo mensaje procesado dos veces no duplica nada. → Task 10 (`ConvertedWithoutSentCreatesTheDealAndWinsIt`, `ARepeatedConvertedChangesNothing`), Task 11 (`AMessageIsAppliedOnceEvenIfDrainedTwice`).
2. **Ajustes que dejarían negocios huérfanos:** borrar una etapa con negocios es `pipeline.stage.has_deals` y **no cambia nada**; convertir una abierta en `Won` es `layout_invalid`; reabrir cuando la última etapa abierta ya no existe va a la primera abierta. → Task 4 (`RemovingAStageWithDealsIsRefusedAndNothingChanges`, `AStageCannotChangeItsKind`), Task 5 (`ReopenFallsBackToTheFirstOpenStageWhenTheLastOneIsGone`), Task 8 (`RemovingAStageWithDealsIs422AndTheLayoutStays`).
3. **Versión vieja después de que el worker tocó el negocio:** un `move` o `PATCH` con la versión leída antes de un evento de cotización es 412 y el negocio queda intacto. → Task 9 (`AStaleVersionIs412AndNothingChanges`).
4. **Ids de otro tenant bajo la ruta propia:** negocio, conversación o cliente de otro tenant responden 404 (no confirman que existen); la ruta de otro tenant es 403. → Task 9 (`AForeignDealIs404AndAnotherTenantsRouteIs403`), Task 13 (`AForeignConversationIs404`), Task 14 (`AForeignCustomerIs404AndAnotherTenantsRouteIs403`).
5. **Monedas mezcladas y negocios sin monto en una columna:** USD y COP en la misma columna salen como dos totales, el ponderado usa la probabilidad de la etapa, y un negocio sin monto suma en `Count` pero no en `Totals`. → Task 12 (`TotalsAreGroupedByCurrencyAndANoAmountDealOnlyCounts`).

## Decisiones de este plan (ambigüedades del spec o choques con el código)

| ID | Decisión | Motivo |
| --- | --- | --- |
| P1 | Los tres permisos son **de núcleo** (`RequiredModules: []`), no `[ModuleKeys.Pipeline]` como dice el spec §6.1. Cada handler revisa la capacidad con `TenantModuleGuard`. | Con `[pipeline]`, la máscara de entitlements quita el permiso y el 403 sale `authorization.denied`, no el `tenancy.module_not_enabled` que piden §6.2 y §6.4. Es el mismo choque que resolvió Messaging (`MessagingPermissions`, comentario de clase). |
| P2 | El consumidor copia `ClaimedOutboxConsumer` e `IdentityInboxClaims` de Identity, no el anti-join de Storage. | H10 del spec: el patrón con reclamo es el de Identity; ningún módulo referencia la infraestructura de otro, así que se copia. |
| P3 | Un negocio está cerrado si y sólo si `closed_at` no es nulo; `Win`/`Lose` lo fijan y `Reopen` lo limpia. Las consultas filtran por `closed_at`. | El dominio sigue validando contra el `kind` de la etapa; la columna evita un join a `stages` en cada consulta. |
| P4 | El historial lo escribe la unidad de trabajo: antes de `SaveChanges` recorre los `Deal` rastreados y agrega lo que `PullHistory()` devuelve. | D19: ningún camino (handler o worker) puede cambiar la etapa sin su fila. |
| P5 | `PATCH /deals/{id}` lleva **todos** los campos editables (título, dueño, monto, moneda, fecha esperada) y los reemplaza. | El formulario repinta el negocio entero; un `null` parcial sería ambiguo (¿borrar el monto o no tocarlo?). |
| P6 | Límites que el spec no fija: ver Global Constraints. | Topes contra errores de digitación, como `PosLimits`. |
| P7 | Códigos nuevos además de los del spec: `pipeline.deal.title_invalid`, `pipeline.deal.amount_invalid`, `pipeline.deal.loss_note_invalid`, `pipeline.stage.not_found`, `pipeline.reason.layout_invalid`, `pipeline.triage.dismiss_reason_not_found` (y `pipeline.triage.already_decided`, que viene del spec D38). | Reglas del dominio que necesitan su código (el validador da el campo, el dominio el código). |
| P8 | Los catálogos de motivos se reemplazan sin `version`; un motivo que deja de venir se **desactiva**. | El spec no pide concurrencia ahí; el historial nombra el motivo y no se puede borrar. |
| P9 | Orden de las tarjetas: abiertas por `stage_entered_at` ascendente (la que más lleva en la etapa, primero); `Won`/`Lost` por `closed_at` descendente. | El spec no lo fija; el informe de investigación dice que el kanban ordena por urgencia. |
| P10 | Sin `lossReasonId` el error es `pipeline.deal.loss_reason_required` (dominio), no `validation.failed`; lo mismo con el motivo de descarte. | El spec nombra esos códigos como contrato. |
| P11 | Enlazar a mano una cotización que ya está en otro negocio la **mueve**; si era la principal allá, ese negocio queda sin principal y con el monto manual. | Una cotización pertenece a lo sumo a un negocio (§4.4); D27 ya define qué pasa con el negocio que la pierde. |
| P12 | Un evento de un tenant sin la capacidad `pipeline` (o sin fila en `tenancy.tenants`) se marca procesado sin efecto. | §4.4: activar el módulo después no rellena el pasado. |
| P13 | En el triage, una conversación sin entrante (`LastInboundAt` nulo) se decide contra `TriageDecision.NoInbound`: sin decisión está pendiente; con una, decidida. Va al final del orden. | D20 sólo habla del entrante; sin él, la decisión es lo único que hay, y la columna de D38 no admite nulos. |
| P15 | Spec D38: la carrera de dos asesores que deciden la misma conversación a la vez la cierra la base, no el handler. Pendiente = no hay decisión cuyo `decided_against_inbound_at` sea el `LastInboundAt` actual (o uno posterior). | El chequeo de pendiente y el `SaveChanges` no son atómicos; el índice único sí. Un entrante nuevo es un instante nuevo y permite decidir otra vez (D20). |
| P14 | La línea base de la suite se mide en la Task 0, **antes** del primer cambio, con `git stash` para apartar lo local. | Después de los commits ya no hay forma de volver a la base con `git stash`. |

## File Structure

| Archivo | Qué |
| --- | --- |
| `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs` | `TenantModuleKeys.Pipeline` y su dependencia. |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs` | El `CHECK` con `'pipeline'`. |
| `src/Modules/Tenancy/Modules.Tenancy.Application/ChangeTenantModules.cs` | «at most nine changes». |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/<ts>_AddPipelineModuleKey*.cs` | Generada. |
| `src/Modules/Quotations/Modules.Quotations.Application/IQuotationLifecycleEventPublisher.cs` (nuevo) | Puerto de los tres eventos. |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationLifecycleEventPublisher.cs` (nuevo) | Escribe en el outbox de `QuotationsDbContext`. |
| `src/Modules/Quotations/Modules.Quotations.Application/SendQuotation.cs`, `ConvertQuotationToOrder.cs`, `CancelOrder.cs` | Publican el evento antes del `SaveChangesAsync`. |
| `src/Modules/Pipeline/Modules.Pipeline.Domain/*` (nuevo) | `PipelineErrorCodes`, `PipelineDomainException`, `PipelineEnums`, `DealActor`, `SalesPipeline` (+ `PipelineStage`, `StageInput`, `SeedStage`, `PipelineSeed`), `PipelineReason` (+ `ReasonInput`, `ReasonCatalog`), `PipelineRules` (`TriageRules`, `StallRules`), `TriageDecision`, `Deal`, `DealStageHistoryEntry`, `DealQuotation`. |
| `src/Modules/Pipeline/Modules.Pipeline.Application/*` (nuevo) | `PipelinePermissions`, `PipelineSupport` (autorización, `PipelineCaller`, 404), `PipelinePorts`, `PipelineDirectories`, `PipelineDtos`, `Settings`, `Deals`, `DealDetailBuilder`, `Board`, `Triage`, `CustomerSummary`, `QuotationEvents`. |
| `src/Modules/Pipeline/Modules.Pipeline.Infrastructure/*` (nuevo) | `PipelineDbContext` (+ factory), `PipelineUnitOfWork`, repositorios, `PipelineBoardQueries`, `PipelineAuditRecorder`, `PipelineProvisioner`, `PipelineInbox` (inbox, proyección del outbox y reclamo), `Messaging/ClaimedOutboxConsumer`, `Messaging/QuotationEventsProcessor`, `Messaging/QuotationEventsWorker`, `PipelineInfrastructureExtensions`, `PipelineDatabaseInitializer`, migración `InitialPipeline`. |
| `src/Modules/Pipeline/Modules.Pipeline.Api/PipelineEndpoints.cs` (nuevo) | Las rutas y los cuerpos de request. |
| `src/Bootstrapper/PipelineConversationDirectory.cs`, `PipelineCustomerDirectory.cs`, `PipelineMemberNames.cs` (nuevos) | Adaptadores de los puertos de lectura. |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` | Infraestructura, adaptadores, handlers, validadores, permisos, roles y políticas. |
| `src/Bootstrapper/Bootstrapper.csproj`, `src/Api/Api.csproj`, `src/Api/Program.cs`, `Backend.slnx` | Referencias, mapeo de rutas y migración al arrancar. |
| `tests/Modules/Pipeline/Modules.Pipeline.UnitTests/*` (nuevo) | Dominio y `QuotationEventApplier` con dobles. |
| `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/*` (nuevo) | Harness, persistencia, adaptadores, API y worker contra Postgres. |
| `tests/ArchitectureTests/ArchitectureTests/PipelineLayerTests.cs` (nuevo), `CompositionRootTests.cs`, `ArchitectureTests.csproj` | Capas, conteo de permisos y roles. |
| `tests/Modules/Tenancy/...` | Listas de claves con `pipeline`. |
| `tests/Modules/Quotations/...` | Doble del publicador y pruebas de los tres eventos. |
| `README.md`, `CLAUDE.md` | Módulo nuevo, contrato y gotchas (Task 15). |

---

### Task 0: Línea base de la suite (sin commit)

**Files:** ninguno del repo. Escribe `baseline-failures.txt` en `$env:TEMP\embudo-baseline` (fuera del repo).

**Interfaces:**
- Consumes: nada.
- Produces: la lista de pruebas que **ya** fallan en `c74b955`, por nombre completo, que la Task 15 usa para separar regresiones de fallas previas (conocidas: dos de `OrderExportApiTests` y algunas de integración de Messaging).

- [ ] **Step 1: Apartar lo local y comprobar la base**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
if ((git -C $B branch --show-current) -ne "feature/embudo-de-ventas") { throw "rama equivocada" }
git -C $B log -1 --format=%h
git -C $B status --short
git -C $B stash push --include-untracked -m "baseline-embudo"
git -C $B status --short
```

Esperado: `c74b955` y, después del `stash`, `git status` vacío. Si el `stash` dice `No local changes to save`, sigue igual (y en el Step 3 no se hace `pop`).

- [ ] **Step 2: Correr la suite completa una vez y guardar las fallas por nombre**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
$S = "$env:TEMP\embudo-baseline"
if (Test-Path $S) { Remove-Item -Recurse -Force $S }
New-Item -ItemType Directory -Force $S | Out-Null
dotnet build "$B\Backend.slnx"
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\Backend.slnx" --no-build --logger "trx" --results-directory $S
$failed = foreach ($file in Get-ChildItem -LiteralPath $S -Filter *.trx) {
  [xml]$trx = Get-Content -LiteralPath $file.FullName -Raw
  $trx.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq "Failed" } | ForEach-Object { $_.testName }
}
$failed | Sort-Object -Unique | Out-File -Encoding utf8 "$S\baseline-failures.txt"
(Get-Content -LiteralPath "$S\baseline-failures.txt").Count
```

`-LiteralPath` a propósito: los `.trx` con corchetes en el nombre (`[1].trx`) se saltan con `-Path`. El `Out-File` es para un `.txt` del scratchpad, no para código. Se copia el conteo y la lista al handoff.

- [ ] **Step 3: Devolver lo local**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
git -C $B stash list
git -C $B stash pop
git -C $B status --short
```

Esperado: vuelven `.atl/.skill-registry.cache.json` y `.atl/skill-registry.md` modificados (son del developer; **nunca** se commitean en este plan).

---

### Task 1: Capacidad `pipeline` en Tenancy

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs:45-62`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs:110`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/ChangeTenantModules.cs:28`
- Modify: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleKeysTests.cs`
- Modify: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/OperatorReadHandlerTests.cs:68`
- Modify: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesApiTests.cs:103-107`
- Create (generados): `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/<ts>_AddPipelineModuleKey.cs`, `.Designer.cs`; Modify (generado): `TenancyDbContextModelSnapshot.cs`

**Interfaces:**
- Consumes: `TenantModuleKey.Define`, `TenantModuleKeys.All`, `TenantModuleKeys.DependenciesOf`.
- Produces: `TenantModuleKeys.Pipeline` (`"pipeline"`), último de `All`, `DependenciesOf(Pipeline) == [Customers]`, fuera de `DefaultForNewTenants`. Lo usan todos los handlers de Pipeline (`TenantModuleGuard.EnsureEnabledAsync(…, TenantModuleKeys.Pipeline, …)`).

- [ ] **Step 1: Escribir las pruebas que fallan**

En `TenantModuleKeysTests.cs`, reemplazar `AllKeepsTheContractOrder` y `DefaultForNewTenantsIsEverythingButPosAndMessaging`, y agregar `PipelineDependsOnlyOnCustomers` después de `MessagingHasNoDependencies`:

```csharp
    [Fact]
    public void AllKeepsTheContractOrder()
    {
        Assert.Equal(
            ["catalog", "customers", "companies", "quotations", "orders", "reporting", "pos", "messaging", "pipeline"],
            TenantModuleKeys.All.Select(key => key.Value));
    }

    // Spec 2026-10-09 §6.2 y spec 2026-10-13 D3: messaging y pipeline son vendibles y se prenden por tenant, como pos.
    [Fact]
    public void DefaultForNewTenantsIsEverythingButPosMessagingAndPipeline()
    {
        Assert.Equal(
            ["catalog", "customers", "companies", "quotations", "orders", "reporting"],
            TenantModuleKeys.DefaultForNewTenants.Select(key => key.Value));
    }

    // Spec 2026-10-13 D30: un negocio siempre tiene cliente. Messaging y Quotations lo enriquecen, pero no lo condicionan.
    [Fact]
    public void PipelineDependsOnlyOnCustomers() =>
        Assert.Equal([TenantModuleKeys.Customers], TenantModuleKeys.DependenciesOf(TenantModuleKeys.Pipeline));
```

En `OperatorReadHandlerTests.cs:68`:

```csharp
        Assert.Equal(["catalog", "customers", "companies", "quotations", "orders", "reporting", "pos", "messaging", "pipeline"],
```

En `TenantModulesApiTests.cs:103-107`:

```csharp
        // Spec 2026-10-09 §6.2 y spec 2026-10-13 D3: messaging y pipeline van al final y, como pos, no vienen con el signup.
        Assert.Equal(
            ["catalog", "customers", "companies", "quotations", "orders", "reporting", "pos", "messaging", "pipeline"],
            modules.Modules.Select(module => module.Key));
        Assert.All(modules.Modules.Where(module => module.Key is not ("pos" or "messaging" or "pipeline")), module =>
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Tenancy\Modules.Tenancy.UnitTests\Modules.Tenancy.UnitTests.csproj" --filter "FullyQualifiedName~TenantModuleKeysTests"
```

Esperado: `error CS0117: 'TenantModuleKeys' does not contain a definition for 'Pipeline'`.

- [ ] **Step 3: Implementar la clave, el `CHECK` y el mensaje del lote**

`TenantModuleKey.cs`, después de `Messaging`, y `All` y `Dependencies`:

```csharp
    /// <summary>Spec 2026-10-09, decisión 4: la bandeja de WhatsApp. Último de la lista, sin
    /// dependencias y fuera de <see cref="DefaultForNewTenants"/>.</summary>
    public static readonly TenantModuleKey Messaging = TenantModuleKey.Define("messaging");

    /// <summary>Spec 2026-10-13, D3 y D30: el embudo de ventas. Último de la lista, depende sólo de
    /// <see cref="Customers"/> y queda fuera de <see cref="DefaultForNewTenants"/>: se prende por tenant.</summary>
    public static readonly TenantModuleKey Pipeline = TenantModuleKey.Define("pipeline");

    public static readonly IReadOnlyList<TenantModuleKey> All =
        [Catalog, Customers, Companies, Quotations, Orders, Reporting, Pos, Messaging, Pipeline];
```

```csharp
    private static readonly Dictionary<TenantModuleKey, TenantModuleKey[]> Dependencies = new()
    {
        [Quotations] = [Catalog, Customers, Companies],
        [Orders] = [Quotations],
        [Pos] = [Catalog, Companies],
        // Spec 2026-10-13 D30: sin messaging el triage queda vacío; sin quotations no llegan eventos.
        [Pipeline] = [Customers],
    };
```

Y el comentario de `DefaultForNewTenants`: `/// <summary>Los seis de hoy, sin <c>pos</c>, <c>messaging</c> ni <c>pipeline</c>: el backfill y el signup con el interruptor prendido.</summary>`.

`TenancyDbContext.cs:110`:

```csharp
                "module_key IN ('catalog','customers','companies','quotations','orders','reporting','pos','messaging','pipeline')");
```

`ChangeTenantModules.cs:28`: `.WithMessage("A batch has at most nine changes.");` y comprobar que ninguna prueba espera el texto viejo:

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
Get-ChildItem -Path "$B\src","$B\tests" -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Select-String -SimpleMatch "at most eight"
```

Si aparece en una prueba, cambiarla a `nine`.

- [ ] **Step 4: Generar la migración**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet ef migrations add AddPipelineModuleKey --project "$B\src\Modules\Tenancy\Modules.Tenancy.Infrastructure" --context TenancyDbContext -o Persistence/Migrations
if ($LASTEXITCODE -ne 0) { throw "ef falló" }
git -C $B status --short
```

El `Up` generado tiene que ser exactamente un `DropCheckConstraint` y un `AddCheckConstraint` de `CK_tenant_modules_module_key` con las nueve claves. Encima del `Up`, el comentario:

```csharp
        /// <summary>Spec 2026-10-13 D3: otro módulo = otra migración que cambia el CHECK, a propósito
        /// (<c>TenantModuleKey.cs</c>). <c>pipeline</c> no entra en <c>DefaultForNewTenants</c>: se prende por tenant.</summary>
```

Y el `Down`, como el de `AddMessagingModuleKey`, borra primero las filas de la clave que desaparece (si no, el `CHECK` de ocho claves muere con `23514`):

```csharp
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Volver atrás descarta las filas de pipeline: Postgres valida las filas existentes al agregar el CHECK de ocho claves.
            migrationBuilder.Sql("DELETE FROM tenancy.tenant_modules WHERE module_key = 'pipeline';");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tenant_modules_module_key",
                schema: "tenancy",
                table: "tenant_modules");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tenant_modules_module_key",
                schema: "tenancy",
                table: "tenant_modules",
                sql: "module_key IN ('catalog','customers','companies','quotations','orders','reporting','pos','messaging')");
        }
```

Si aparece cualquier otra operación (`AddColumn`, `AlterColumn`…), el modelo tenía un desvío previo: se borra la migración (`dotnet ef migrations remove --project … --context TenancyDbContext`), se detiene la tarea y se avisa.

- [ ] **Step 5: Correr y verificar que pasan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Modules\Tenancy\Modules.Tenancy.UnitTests\Modules.Tenancy.UnitTests.csproj" --no-build --filter "FullyQualifiedName~TenantModuleKeysTests|FullyQualifiedName~TenantModuleSetTests|FullyQualifiedName~OperatorReadHandlerTests|FullyQualifiedName~ChangeTenantModulesHandlerTests"
dotnet test "$B\tests\Modules\Tenancy\Modules.Tenancy.IntegrationTests\Modules.Tenancy.IntegrationTests.csproj" --no-build --filter "FullyQualifiedName~TenantModulesApiTests"
dotnet test "$B\tests\Modules\Authorization\Modules.Authorization.UnitTests\Modules.Authorization.UnitTests.csproj" --no-build --filter "FullyQualifiedName~ModuleEntitlementMaskTests"
```

Esperado: todo verde. Si otra clase de éstas compara la lista de claves literal, se actualiza con `pipeline` al final (es el contrato: «último de la lista»).

- [ ] **Step 6: Commit**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
if ((git -C $B branch --show-current) -ne "feature/embudo-de-ventas") { throw "rama equivocada" }
git -C $B status --short
git -C $B add src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs src/Modules/Tenancy/Modules.Tenancy.Application/ChangeTenantModules.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleKeysTests.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/OperatorReadHandlerTests.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesApiTests.cs
git -C $B commit -m "feat(tenancy): capacidad pipeline, última del catálogo y dependiente de customers"
git -C $B log -1 --format=%B
```

---

### Task 2: Quotations — los tres eventos del ciclo en el outbox

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationLifecycleEventPublisher.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationLifecycleEventPublisher.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs:45` (después del registro de `IOrderPaymentProofEventPublisher`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/SendQuotation.cs:28-40,137-144`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ConvertQuotationToOrder.cs:49-64,173-181`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/CancelOrder.cs:10-59`
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs` (al final)
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/SendQuotationHandlerTests.cs:334-414`
- Create: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationLifecycleEventsApiTests.cs`

**Interfaces:**
- Consumes: `Quotation` (`Id`, `TenantId`, `QuotationNumber`, `ClientId`, `AdvisorId`, `Total`, `Currency`), `Order` (`Id`, `TenantId`, `OrderNumber`, `QuotationId`), `QuotationsDbContext.Outbox`, `QuotationsOutboxMessage`.
- Produces:
  - `public interface IQuotationLifecycleEventPublisher { void PublishSent(Quotation quotation, DateTimeOffset occurredAt); void PublishConverted(Quotation quotation, Order order, DateTimeOffset occurredAt); void PublishOrderCancelled(Order order, DateTimeOffset occurredAt); }`
  - Filas en `platform.outbox_messages` con `event_name` `quotations.quotation.sent.v1`, `quotations.quotation.converted.v1` y `quotations.order.cancelled.v1` y el payload camelCase de Global Constraints. Las lee `QuotationEventsProcessor` (Task 11) **por nombre**.

- [ ] **Step 1: Escribir las pruebas que fallan**

Al final de `QuotationsTestDoubles.cs`:

```csharp
/// <summary>Spec 2026-10-13 §4.5: anota qué eventos del ciclo pidió el handler, sin outbox.</summary>
internal sealed class RecordingQuotationLifecycleEventPublisher : IQuotationLifecycleEventPublisher
{
    public List<(string Event, Guid QuotationId, decimal Total, string Currency)> Published { get; } = [];

    public void PublishSent(Quotation quotation, DateTimeOffset occurredAt) =>
        Published.Add(("sent", quotation.Id.Value, quotation.Total, quotation.Currency));

    public void PublishConverted(Quotation quotation, Order order, DateTimeOffset occurredAt) =>
        Published.Add(("converted", quotation.Id.Value, quotation.Total, quotation.Currency));

    public void PublishOrderCancelled(Order order, DateTimeOffset occurredAt) =>
        Published.Add(("cancelled", order.QuotationId.Value, 0m, string.Empty));
}
```

En `SendQuotationHandlerTests.cs`:

1. El record `Harness` suma `RecordingQuotationLifecycleEventPublisher Lifecycle` al final:

```csharp
    private sealed record Harness(
        SendQuotationHandler Handler,
        RecordingWhatsAppSender Sender,
        RecordingPdfStorage Storage,
        StubQuotationRepository Repository,
        RecordingQuotationAuditPublisher Audit,
        RecordingQuotationSendFailureLog FailureLog,
        RecordingQuotationLifecycleEventPublisher Lifecycle);
```

2. En `NewHandler`, antes de `var handler = …`: `var lifecycle = new RecordingQuotationLifecycleEventPublisher();`; en la construcción, el argumento nuevo va **después de `audit`** (`repository, new NoOpQuotationsUnitOfWork(), audit, lifecycle, new QuotationPdfProvider(…`); y el `return` pasa `lifecycle` al final.

3. Tres pruebas nuevas, después de `SendHandsTheSenderTheQuotationTotalAndValidity`:

```csharp
    // Spec 2026-10-13 §4.5: el envío deja quotations.quotation.sent.v1 en la misma unidad de trabajo, con el total y la moneda
    // con los que Pipeline mueve el monto del negocio.
    [Fact]
    public async Task SendPublishesTheSentEventWithTheTotalAndCurrency()
    {
        var harness = NewHandler();

        await harness.Handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        var published = Assert.Single(harness.Lifecycle.Published);
        Assert.Equal(("sent", CurrentQuotationId, "COP"), (published.Event, published.QuotationId, published.Currency));
        Assert.True(published.Total > 0m);
    }

    // H5 del spec 2026-10-13: reenviar es el mismo hecho para el agregado; el consumidor es idempotente.
    [Fact]
    public async Task AResendPublishesTheSentEventAgain()
    {
        var harness = NewHandler(alreadySent: true);

        await harness.Handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        Assert.Equal("sent", Assert.Single(harness.Lifecycle.Published).Event);
    }

    // Un envío que no llegó a guardarse no avisa nada: el evento sólo existe si la cotización quedó en Sent.
    [Fact]
    public async Task AFailedSendPublishesNothing()
    {
        var harness = NewHandler(whatsAppFailure: new TaskCanceledException("A task was canceled."));

        await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Empty(harness.Lifecycle.Published);
    }
```

`tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationLifecycleEventsApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>Spec 2026-10-13 §4.5: los tres eventos del ciclo de una cotización, con el payload que Pipeline lee por nombre, y
/// sólo cuando el cambio se guardó (mismo SaveChanges). Un rechazo no deja evento.</summary>
public sealed class QuotationLifecycleEventsApiTests
{
    private const string Sent = "quotations.quotation.sent.v1";
    private const string Converted = "quotations.quotation.converted.v1";
    private const string Cancelled = "quotations.order.cancelled.v1";

    private static readonly string[] CancellerPermissions = [.. ManagerPermissions, OrdersPermissions.OrderCancel];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<OrderResponse> ConvertAsync(HttpClient client, QepApiFactory factory, Guid tenantId, QuotationResponse quotation)
    {
        var proof = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest("FullPaymentReceived", null, [new OrderPaymentProofRequest(proof, quotation.Total)]),
            Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(Ct);
        Assert.NotNull(order);
        return order;
    }

    [Fact]
    public async Task SendingWritesTheSentEventWithTheContractPayload()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);

        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);

        var message = Assert.Single(await OutboxMessagesAsync(factory, Sent));
        using var payload = JsonDocument.Parse(message.PayloadJson);
        var root = payload.RootElement;
        Assert.Equal(tenantId, root.GetProperty("tenantId").GetGuid());
        Assert.Equal(quotation.Id, root.GetProperty("quotationId").GetGuid());
        Assert.Equal(quotation.QuotationNumber, root.GetProperty("quotationNumber").GetString());
        Assert.Equal(clientId, root.GetProperty("clientId").GetGuid());
        Assert.Equal(quotation.AdvisorId, root.GetProperty("advisorMemberId").GetGuid());
        Assert.Equal(quotation.Total, root.GetProperty("total").GetDecimal());
        Assert.Equal(quotation.Currency, root.GetProperty("currency").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("occurredAt").ValueKind);
    }

    // Sin cuenta de cobro ni productos el envío es 422: la cotización no cambió y el evento no existe.
    [Fact]
    public async Task ARejectedSendWritesNoEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        var pdf = await CreateAvailablePdfFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync($"{QuotationsUrl(tenantId)}/{quotation.Id}/send", new SendQuotationRequest(pdf), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(await OutboxMessagesAsync(factory, Sent));
    }

    // Spec D25: converted lleva todo lo de sent (para enlazar una convertida que nunca se envió) más el pedido. Una segunda
    // conversión se rechaza y no deja otro evento.
    [Fact]
    public async Task ConvertingWritesTheConvertedEventWithTheOrderOnce()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);

        var order = await ConvertAsync(client, factory, tenantId, quotation);
        var proof = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var again = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest("FullPaymentReceived", null, [new OrderPaymentProofRequest(proof, quotation.Total)]),
            Ct);

        Assert.NotEqual(HttpStatusCode.Created, again.StatusCode);
        var message = Assert.Single(await OutboxMessagesAsync(factory, Converted));
        using var payload = JsonDocument.Parse(message.PayloadJson);
        var root = payload.RootElement;
        Assert.Equal(quotation.Id, root.GetProperty("quotationId").GetGuid());
        Assert.Equal(clientId, root.GetProperty("clientId").GetGuid());
        Assert.Equal(quotation.AdvisorId, root.GetProperty("advisorMemberId").GetGuid());
        Assert.Equal(quotation.Total, root.GetProperty("total").GetDecimal());
        Assert.Equal(quotation.Currency, root.GetProperty("currency").GetString());
        Assert.Equal(order.Id, root.GetProperty("orderId").GetGuid());
        Assert.Equal(order.OrderNumber, root.GetProperty("orderNumber").GetString());
    }

    [Fact]
    public async Task CancellingWritesTheCancelledEventOnce()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, CancellerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var order = await ConvertAsync(client, factory, tenantId, quotation);
        var cancelUrl = $"/api/v1/tenants/{tenantId}/orders/{order.Id}/cancel";

        var first = await client.PostAsJsonAsync(cancelUrl, new CancelOrderRequest("El cliente desistió"), Ct);
        var second = await client.PostAsJsonAsync(cancelUrl, new CancelOrderRequest("Otra vez"), Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        var message = Assert.Single(await OutboxMessagesAsync(factory, Cancelled));
        using var payload = JsonDocument.Parse(message.PayloadJson);
        var root = payload.RootElement;
        Assert.Equal(tenantId, root.GetProperty("tenantId").GetGuid());
        Assert.Equal(order.Id, root.GetProperty("orderId").GetGuid());
        Assert.Equal(quotation.Id, root.GetProperty("quotationId").GetGuid());
    }
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Quotations\Modules.Quotations.UnitTests\Modules.Quotations.UnitTests.csproj" --filter "FullyQualifiedName~SendQuotationHandlerTests"
```

Esperado: `error CS0246: The type or namespace name 'IQuotationLifecycleEventPublisher' could not be found`.

- [ ] **Step 3: Implementar el puerto, el publicador, el registro y las tres llamadas**

`src/Modules/Quotations/Modules.Quotations.Application/IQuotationLifecycleEventPublisher.cs`:

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Spec 2026-10-13 §4.5: los tres hechos del ciclo de una cotización que otro módulo necesita —hoy Pipeline, que mueve el
/// negocio con ellos—. Se escriben en el outbox de la misma unidad de trabajo que el cambio: si guardar falla, el evento tampoco
/// existe. Mismo mecanismo que <see cref="IOrderPaymentProofEventPublisher"/>. Los nombres son hechos de Quotations, no órdenes
/// a Pipeline: mañana los puede consumir otro módulo.
/// </summary>
public interface IQuotationLifecycleEventPublisher
{
    /// <summary><c>quotations.quotation.sent.v1</c>. También en un reenvío: el consumidor es idempotente.</summary>
    void PublishSent(Quotation quotation, DateTimeOffset occurredAt);

    /// <summary><c>quotations.quotation.converted.v1</c>. Lleva todo lo de <c>sent</c> para que el consumidor enlace una
    /// cotización convertida que nunca se envió (spec 2026-10-13, D25).</summary>
    void PublishConverted(Quotation quotation, Order order, DateTimeOffset occurredAt);

    /// <summary><c>quotations.order.cancelled.v1</c>.</summary>
    void PublishOrderCancelled(Order order, DateTimeOffset occurredAt);
}
```

`src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationLifecycleEventPublisher.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Persistence;

// Spec 2026-10-13 §4.5: acumula el evento en la proyección de outbox de QuotationsDbContext, para que commitee con el cambio.
// Lo consume QuotationEventsProcessor, en Pipeline, que lee el payload por nombre: los campos son contrato.
internal sealed class QuotationLifecycleEventPublisher(QuotationsDbContext dbContext) : IQuotationLifecycleEventPublisher
{
    internal const string SentEventName = "quotations.quotation.sent.v1";

    internal const string ConvertedEventName = "quotations.quotation.converted.v1";

    internal const string OrderCancelledEventName = "quotations.order.cancelled.v1";

    public void PublishSent(Quotation quotation, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(quotation);
        Add(SentEventName, quotation.Id.Value, new SentPayload(
            quotation.TenantId, quotation.Id.Value, quotation.QuotationNumber, quotation.ClientId, quotation.AdvisorId.Value,
            quotation.Total, quotation.Currency, occurredAt), occurredAt);
    }

    public void PublishConverted(Quotation quotation, Order order, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(quotation);
        ArgumentNullException.ThrowIfNull(order);
        Add(ConvertedEventName, quotation.Id.Value, new ConvertedPayload(
            quotation.TenantId, quotation.Id.Value, quotation.QuotationNumber, quotation.ClientId, quotation.AdvisorId.Value,
            quotation.Total, quotation.Currency, occurredAt, order.Id.Value, order.OrderNumber), occurredAt);
    }

    public void PublishOrderCancelled(Order order, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(order);
        Add(OrderCancelledEventName, order.QuotationId.Value, new OrderCancelledPayload(
            order.TenantId, order.Id.Value, order.QuotationId.Value, occurredAt), occurredAt);
    }

    // La correlación es la cotización en los tres: soporte llega del log del worker de Pipeline a la fila sin adivinar.
    private void Add<TPayload>(string eventName, Guid quotationId, TPayload payload, DateTimeOffset occurredAt) =>
        dbContext.Outbox.Add(new QuotationsOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = eventName,
            PayloadJson = JsonSerializer.Serialize(payload),
            CorrelationId = quotationId.ToString("D", CultureInfo.InvariantCulture),
            OccurredAt = occurredAt,
        });

    // Nombres en minúscula como el resto de los payloads del outbox: el consumidor los lee por nombre con JsonDocument.
    private sealed record SentPayload(
        Guid tenantId, Guid quotationId, string quotationNumber, Guid clientId, Guid advisorMemberId,
        decimal total, string currency, DateTimeOffset occurredAt);

    private sealed record ConvertedPayload(
        Guid tenantId, Guid quotationId, string quotationNumber, Guid clientId, Guid advisorMemberId,
        decimal total, string currency, DateTimeOffset occurredAt, Guid orderId, string orderNumber);

    private sealed record OrderCancelledPayload(Guid tenantId, Guid orderId, Guid quotationId, DateTimeOffset occurredAt);
}
```

`QuotationsInfrastructureExtensions.cs`, después de `services.AddScoped<IOrderPaymentProofEventPublisher, OrderPaymentProofEventPublisher>();`:

```csharp
        // Spec 2026-10-13 §4.5: enviada, convertida y pedido anulado, para Pipeline. En la misma unidad de trabajo que el cambio.
        services.AddScoped<IQuotationLifecycleEventPublisher, QuotationLifecycleEventPublisher>();
```

`SendQuotation.cs`: parámetro nuevo `IQuotationLifecycleEventPublisher lifecycleEvents,` **después de** `IQuotationAuditPublisher auditPublisher,`; y entre el `auditPublisher.Publish(…)` y `await unitOfWork.SaveChangesAsync(cancellationToken);` (línea 144):

```csharp
            // Spec 2026-10-13 §4.5: en el mismo SaveChanges que el Sent. También en un reenvío (H5): Pipeline es idempotente.
            lifecycleEvents.PublishSent(quotation, now);
```

`ConvertQuotationToOrder.cs`: parámetro nuevo `IQuotationLifecycleEventPublisher lifecycleEvents,` **después de** `IOrderPaymentProofEventPublisher paymentProofEvents,`; y después del bloque `if (attached.Length > 0) { … }`, antes de `await unitOfWork.SaveChangesAsync(cancellationToken);`:

```csharp
            // Spec 2026-10-13 §4.5 y D25: dentro de la transacción del contador, con la cotización ya Converted y el pedido creado.
            lifecycleEvents.PublishConverted(quotation, order, now);
```

`CancelOrder.cs`: en el `<summary>` de la clase, reemplazar `Sin evento de outbox, igual que aprobar.` por `Escribe <c>quotations.order.cancelled.v1</c> (spec 2026-10-13 §4.5) en la misma unidad de trabajo: con él, Pipeline reabre el negocio ganado por esta cotización.`; parámetro nuevo `IQuotationLifecycleEventPublisher lifecycleEvents,` **después de** `IQuotationAuditPublisher auditPublisher,`; y después del `auditPublisher.Publish(…)`:

```csharp
        lifecycleEvents.PublishOrderCancelled(order, now);
```

- [ ] **Step 4: Correr y verificar que pasan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Modules\Quotations\Modules.Quotations.UnitTests\Modules.Quotations.UnitTests.csproj" --no-build --filter "FullyQualifiedName~SendQuotationHandlerTests"
dotnet test "$B\tests\Modules\Quotations\Modules.Quotations.IntegrationTests\Modules.Quotations.IntegrationTests.csproj" --no-build --filter "FullyQualifiedName~QuotationLifecycleEventsApiTests"
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --no-build --filter "FullyQualifiedName~QuotationsLayerTests|FullyQualifiedName~CompositionRootTests"
```

Esperado: `SendQuotationHandlerTests` verde (las tres nuevas incluidas), 4 verdes en `QuotationLifecycleEventsApiTests`, capas y composición verdes.

- [ ] **Step 5: Commit**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
if ((git -C $B branch --show-current) -ne "feature/embudo-de-ventas") { throw "rama equivocada" }
git -C $B status --short
git -C $B add src/Modules/Quotations/Modules.Quotations.Application/IQuotationLifecycleEventPublisher.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationLifecycleEventPublisher.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs src/Modules/Quotations/Modules.Quotations.Application/SendQuotation.cs src/Modules/Quotations/Modules.Quotations.Application/ConvertQuotationToOrder.cs src/Modules/Quotations/Modules.Quotations.Application/CancelOrder.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/SendQuotationHandlerTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationLifecycleEventsApiTests.cs
git -C $B commit -m "feat(quotations): eventos de cotización enviada, convertida y pedido anulado en el outbox"
git -C $B log -1 --format=%B
```

---

### Task 3: Proyectos del módulo, pruebas de capas y permisos

**Files:**
- Create: `src/Modules/Pipeline/Modules.Pipeline.Domain/Modules.Pipeline.Domain.csproj`, `PipelineErrorCodes.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Application/Modules.Pipeline.Application.csproj`, `PipelinePermissions.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Infrastructure/Modules.Pipeline.Infrastructure.csproj`, `PipelineInfrastructureExtensions.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Api/Modules.Pipeline.Api.csproj`, `PipelineEndpoints.cs`
- Create: `tests/Modules/Pipeline/Modules.Pipeline.UnitTests/Modules.Pipeline.UnitTests.csproj`
- Create: `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/Modules.Pipeline.IntegrationTests.csproj`
- Create: `tests/ArchitectureTests/ArchitectureTests/PipelineLayerTests.cs`
- Modify: `Backend.slnx` (carpetas `/src/Modules/Pipeline/` y `/tests/Modules/Pipeline/`)
- Modify: `src/Api/Api.csproj`, `src/Api/Program.cs:30,177`, `src/Bootstrapper/Bootstrapper.csproj`, `tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (`using`, `AddPipelineInfrastructure` después de `AddMessagingInfrastructure` `:653`, rol `admin` `:902`, rol `advisor` `:952`, `PermissionDefinition` después de `:1385`, políticas después de `:1684`)
- Modify: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs:168-179` y una prueba nueva
- Modify: todos los `packages.lock.json` que cambien

**Interfaces:**
- Consumes: `MessagingLayerTests` como molde; `RoleDefinition`/`PermissionDefinition` (`RoleCatalog.cs:12-33`).
- Produces:
  - Cuatro ensamblados `Modules.Pipeline.{Domain,Application,Infrastructure,Api}` y dos de prueba.
  - `PipelinePermissions.DealRead = "pipeline.deal.read"`, `DealManage = "pipeline.deal.manage"`, `SettingsManage = "pipeline.settings.manage"`.
  - `PipelineErrorCodes` completo (todas las tareas siguientes lo usan).
  - `PipelineInfrastructureExtensions.AddPipelineInfrastructure(this IServiceCollection, IConfiguration)` (vacío; lo llena la Task 6) y `PipelineEndpoints.MapPipelineEndpoints(this IEndpointRouteBuilder)` (vacío; lo llena la Task 8).

- [ ] **Step 1: Proyectos, archivos ancla, solución, referencias y locks**

`src/Modules/Pipeline/Modules.Pipeline.Domain/Modules.Pipeline.Domain.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <!-- Spec 2026-10-13 D2: el dominio del embudo no conoce ningún otro módulo (PipelineLayerTests). -->
    <ProjectReference Include="..\..\..\BuildingBlocks\BuildingBlocks.Domain\BuildingBlocks.Domain.csproj" />
  </ItemGroup>
</Project>
```

`src/Modules/Pipeline/Modules.Pipeline.Application/Modules.Pipeline.Application.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <InternalsVisibleTo Include="Modules.Pipeline.UnitTests" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Modules.Pipeline.Domain\Modules.Pipeline.Domain.csproj" />
    <!-- ITenantModules, TenantModuleGuard, IExecutionContext, IMembershipDirectory y Currencies: la única referencia a otro
         módulo que Application tiene permitida (PipelineLayerTests). -->
    <ProjectReference Include="..\..\Tenancy\Modules.Tenancy.Application\Modules.Tenancy.Application.csproj" />
    <ProjectReference Include="..\..\..\BuildingBlocks\BuildingBlocks.Application\BuildingBlocks.Application.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="FluentValidation" />
  </ItemGroup>
</Project>
```

`src/Modules/Pipeline/Modules.Pipeline.Infrastructure/Modules.Pipeline.Infrastructure.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <!-- BackgroundService y ILogger salen del shared framework, como en Messaging. -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Modules.Pipeline.Application\Modules.Pipeline.Application.csproj" />
    <ProjectReference Include="..\Modules.Pipeline.Domain\Modules.Pipeline.Domain.csproj" />
    <ProjectReference Include="..\..\Audit\Modules.Audit.Domain\Modules.Audit.Domain.csproj" />
    <ProjectReference Include="..\..\Audit\Modules.Audit.Infrastructure\Modules.Audit.Infrastructure.csproj" />
    <ProjectReference Include="..\..\Tenancy\Modules.Tenancy.Application\Modules.Tenancy.Application.csproj" />
    <ProjectReference Include="..\..\..\BuildingBlocks\BuildingBlocks.Application\BuildingBlocks.Application.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
  </ItemGroup>
  <ItemGroup>
    <!-- El procesador del worker y el reclamo son internal; las pruebas los buscan en el host. -->
    <InternalsVisibleTo Include="Modules.Pipeline.UnitTests" />
    <InternalsVisibleTo Include="Modules.Pipeline.IntegrationTests" />
  </ItemGroup>
</Project>
```

`src/Modules/Pipeline/Modules.Pipeline.Api/Modules.Pipeline.Api.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Modules.Pipeline.Application\Modules.Pipeline.Application.csproj" />
    <ProjectReference Include="..\Modules.Pipeline.Domain\Modules.Pipeline.Domain.csproj" />
    <ProjectReference Include="..\..\..\BuildingBlocks\BuildingBlocks.Application\BuildingBlocks.Application.csproj" />
  </ItemGroup>
</Project>
```

`tests/Modules/Pipeline/Modules.Pipeline.UnitTests/Modules.Pipeline.UnitTests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <PackageReference Include="coverlet.collector" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\..\src\Modules\Pipeline\Modules.Pipeline.Application\Modules.Pipeline.Application.csproj" />
    <ProjectReference Include="..\..\..\..\src\Modules\Pipeline\Modules.Pipeline.Domain\Modules.Pipeline.Domain.csproj" />
    <ProjectReference Include="..\..\..\..\src\Modules\Pipeline\Modules.Pipeline.Infrastructure\Modules.Pipeline.Infrastructure.csproj" />
  </ItemGroup>
</Project>
```

`tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/Modules.Pipeline.IntegrationTests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <PackageReference Include="coverlet.collector" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Testcontainers.PostgreSql" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\..\src\Api\Api.csproj" />
  </ItemGroup>
</Project>
```

`src/Modules/Pipeline/Modules.Pipeline.Domain/PipelineErrorCodes.cs`:

```csharp
namespace Modules.Pipeline.Domain;

/// <summary>
/// Spec 2026-10-13 §6.2 (y P7 del plan): los códigos del embudo, en un solo lugar. Los 422 salen por
/// <c>PipelineDomainException</c>; los 404, por <c>ResourceNotFoundException</c> en Application.
/// </summary>
public static class PipelineErrorCodes
{
    public const string DealNotFound = "pipeline.deal.not_found";
    public const string CustomerNotFound = "pipeline.customer.not_found";
    public const string DealCustomerNotFound = "pipeline.deal.customer_not_found";
    public const string DealOwnerNotMember = "pipeline.deal.owner_not_member";
    public const string DealTitleInvalid = "pipeline.deal.title_invalid";
    public const string DealAmountInvalid = "pipeline.deal.amount_invalid";
    public const string DealAmountFollowsQuotation = "pipeline.deal.amount_follows_quotation";
    public const string DealAlreadyClosed = "pipeline.deal.already_closed";
    public const string DealNotClosed = "pipeline.deal.not_closed";
    public const string DealStageNotOpen = "pipeline.deal.stage_not_open";
    public const string DealLossReasonRequired = "pipeline.deal.loss_reason_required";
    public const string DealLossReasonNotFound = "pipeline.deal.loss_reason_not_found";
    public const string DealLossNoteInvalid = "pipeline.deal.loss_note_invalid";
    public const string DealQuotationNotFound = "pipeline.deal.quotation_not_found";
    public const string DealQuotationNotLinked = "pipeline.deal.quotation_not_linked";
    public const string DealQuotationCustomerMismatch = "pipeline.deal.quotation_customer_mismatch";
    public const string StageNotFound = "pipeline.stage.not_found";
    public const string StageLayoutInvalid = "pipeline.stage.layout_invalid";
    public const string StageHasDeals = "pipeline.stage.has_deals";
    public const string ReasonLayoutInvalid = "pipeline.reason.layout_invalid";
    public const string TriageConversationNotFound = "pipeline.triage.conversation_not_found";
    public const string TriageNotPending = "pipeline.triage.not_pending";
    public const string TriageDismissReasonRequired = "pipeline.triage.dismiss_reason_required";
    public const string TriageDismissReasonNotFound = "pipeline.triage.dismiss_reason_not_found";

    /// <summary>Spec 2026-10-13 D38: otro asesor ya decidió esta conversación contra el mismo entrante
    /// (<c>IX_triage_decisions_conversation_inbound</c>).</summary>
    public const string TriageAlreadyDecided = "pipeline.triage.already_decided";
}
```

`src/Modules/Pipeline/Modules.Pipeline.Application/PipelinePermissions.cs`:

```csharp
namespace Modules.Pipeline.Application;

/// <summary>
/// Spec 2026-10-13 D23 y P1 del plan: de núcleo (<c>RequiredModules</c> vacío), como los de Messaging. Con
/// <c>[pipeline]</c>, la máscara de entitlements quitaría el permiso y el 403 saldría <c>authorization.denied</c>, sin el
/// <c>tenancy.module_not_enabled</c> que el contrato pide; por eso cada handler llama a <c>TenantModuleGuard</c>. Cada uno
/// necesita su política en <c>AddAuthorization</c>: sin ella <c>RequireAuthorization</c> no resuelve y el síntoma es 500.
/// </summary>
public static class PipelinePermissions
{
    /// <summary>Ver todos los negocios del tenant, el tablero, el triage y los ajustes. El filtro por dueño es foco, no
    /// seguridad (D23).</summary>
    public const string DealRead = "pipeline.deal.read";

    /// <summary>Crear, editar, mover, ganar, perder y reabrir negocios; enlazar cotizaciones; decidir el triage.</summary>
    public const string DealManage = "pipeline.deal.manage";

    /// <summary>Etapas y catálogos de motivos: cambian el trabajo de todos los asesores (D32).</summary>
    public const string SettingsManage = "pipeline.settings.manage";
}
```

`src/Modules/Pipeline/Modules.Pipeline.Infrastructure/PipelineInfrastructureExtensions.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Modules.Pipeline.Infrastructure;

public static class PipelineInfrastructureExtensions
{
    /// <summary>Se llena en la Task 6 (DbContext, repositorios, unidad de trabajo) y la Task 11 (worker).</summary>
    public static IServiceCollection AddPipelineInfrastructure(this IServiceCollection services, IConfiguration configuration) =>
        services;
}
```

`src/Modules/Pipeline/Modules.Pipeline.Api/PipelineEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Routing;

namespace Modules.Pipeline.Api;

public static class PipelineEndpoints
{
    /// <summary>Se llena desde la Task 8, bajo <c>/api/v1/tenants/{tenantId:guid}/pipeline</c>.</summary>
    public static IEndpointRouteBuilder MapPipelineEndpoints(this IEndpointRouteBuilder endpoints) => endpoints;
}
```

`Backend.slnx`, después de la carpeta `/src/Modules/Messaging/`:

```xml
  <Folder Name="/src/Modules/Pipeline/">
    <Project Path="src/Modules/Pipeline/Modules.Pipeline.Api/Modules.Pipeline.Api.csproj" />
    <Project Path="src/Modules/Pipeline/Modules.Pipeline.Application/Modules.Pipeline.Application.csproj" />
    <Project Path="src/Modules/Pipeline/Modules.Pipeline.Domain/Modules.Pipeline.Domain.csproj" />
    <Project Path="src/Modules/Pipeline/Modules.Pipeline.Infrastructure/Modules.Pipeline.Infrastructure.csproj" />
  </Folder>
```

Y después de `/tests/Modules/Notifications/`:

```xml
  <Folder Name="/tests/Modules/Pipeline/">
    <Project Path="tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/Modules.Pipeline.IntegrationTests.csproj" />
    <Project Path="tests/Modules/Pipeline/Modules.Pipeline.UnitTests/Modules.Pipeline.UnitTests.csproj" />
  </Folder>
```

`src/Api/Api.csproj`, después de las dos de Messaging:

```xml
    <ProjectReference Include="..\Modules\Pipeline\Modules.Pipeline.Api\Modules.Pipeline.Api.csproj" />
    <ProjectReference Include="..\Modules\Pipeline\Modules.Pipeline.Infrastructure\Modules.Pipeline.Infrastructure.csproj" />
```

`src/Bootstrapper/Bootstrapper.csproj`, después de las dos de Messaging:

```xml
    <ProjectReference Include="..\Modules\Pipeline\Modules.Pipeline.Application\Modules.Pipeline.Application.csproj" />
    <ProjectReference Include="..\Modules\Pipeline\Modules.Pipeline.Infrastructure\Modules.Pipeline.Infrastructure.csproj" />
```

`tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj`, después de las cuatro de Messaging (sin el `.Application` en la salida, `CompositionRootTests` no descubriría sus handlers):

```xml
    <ProjectReference Include="..\..\..\src\Modules\Pipeline\Modules.Pipeline.Api\Modules.Pipeline.Api.csproj" />
    <ProjectReference Include="..\..\..\src\Modules\Pipeline\Modules.Pipeline.Application\Modules.Pipeline.Application.csproj" />
    <ProjectReference Include="..\..\..\src\Modules\Pipeline\Modules.Pipeline.Domain\Modules.Pipeline.Domain.csproj" />
    <ProjectReference Include="..\..\..\src\Modules\Pipeline\Modules.Pipeline.Infrastructure\Modules.Pipeline.Infrastructure.csproj" />
```

`src/Api/Program.cs`: `using Modules.Pipeline.Api;` junto a los `using Modules.Messaging.*`, y después de `app.MapMessagingEndpoints();`:

```csharp
app.MapPipelineEndpoints();
```

`QepServiceCollectionExtensions.cs`: `using Modules.Pipeline.Application;` y `using Modules.Pipeline.Infrastructure;` junto a los de Messaging, y después de `services.AddMessagingInfrastructure(configuration);`:

```csharp
        // Pipeline (spec 2026-10-13): el embudo de ventas. Sólo ve Tenancy (y Audit en Infrastructure); conversaciones,
        // clientes y nombres de miembros entran por adaptadores que se registran más abajo.
        services.AddPipelineInfrastructure(configuration);
```

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet restore "$B\Backend.slnx" --force-evaluate
git -C $B status --short -- '*.lock.json'
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
```

Esperado: locks nuevos en los seis proyectos nuevos y modificados en `Api`, `Bootstrapper`, `ArchitectureTests` y los proyectos de prueba que referencian `Api.csproj`; build en 0 warnings.

- [ ] **Step 2: Escribir las pruebas que fallan**

`tests/ArchitectureTests/ArchitectureTests/PipelineLayerTests.cs`:

```csharp
using System.Reflection;
using Modules.Pipeline.Api;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;
using Modules.Pipeline.Infrastructure;

namespace ArchitectureTests;

/// <summary>
/// Capas del módulo Pipeline (spec 2026-10-13 D2 y D9), copia de <see cref="MessagingLayerTests"/>. Pipeline nunca
/// referencia Messaging, Customers ni Quotations: las conversaciones, los clientes y los nombres de miembros entran por
/// puertos propios con adaptadores en Bootstrapper, y las cotizaciones llegan por el outbox.
/// </summary>
public sealed class PipelineLayerTests
{
    private static readonly Assembly Domain = typeof(PipelineErrorCodes).Assembly;
    private static readonly Assembly Application = typeof(PipelinePermissions).Assembly;
    private static readonly Assembly Infrastructure = typeof(PipelineInfrastructureExtensions).Assembly;
    private static readonly Assembly Api = typeof(PipelineEndpoints).Assembly;

    [Fact]
    public void DomainDoesNotReferenceOuterLayers() =>
        AssertDoesNotReference(Domain, Application, Infrastructure, Api);

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi() =>
        AssertDoesNotReference(Application, Infrastructure, Api);

    [Fact]
    public void InfrastructureDoesNotReferenceApi() =>
        AssertDoesNotReference(Infrastructure, Api);

    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries() =>
        Assert.DoesNotContain(ReferenceNamesOf(Application), name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));

    // D9: Tenancy (ITenantModules, TenantModuleGuard, IExecutionContext, IMembershipDirectory, Currencies) y nada más.
    [Fact]
    public void ApplicationOnlyReferencesTenancyAmongTheModules() =>
        Assert.DoesNotContain(ReferenceNamesOf(Application), name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Pipeline", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Tenancy", StringComparison.Ordinal));

    [Fact]
    public void DomainReferencesNoOtherModule() =>
        Assert.DoesNotContain(ReferenceNamesOf(Domain), name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Pipeline", StringComparison.Ordinal));

    // D9: ni siquiera Infrastructure. Los adaptadores viven en Bootstrapper; las cotizaciones llegan por el outbox.
    [Theory]
    [InlineData("Modules.Messaging")]
    [InlineData("Modules.Customers")]
    [InlineData("Modules.Quotations")]
    [InlineData("Modules.Identity")]
    public void NoLayerReferencesMessagingCustomersQuotationsOrIdentity(string forbiddenPrefix)
    {
        foreach (var assembly in new[] { Domain, Application, Infrastructure, Api })
        {
            Assert.DoesNotContain(ReferenceNamesOf(assembly), name =>
                name is not null && name.StartsWith(forbiddenPrefix, StringComparison.Ordinal));
        }
    }

    private static string?[] ReferenceNamesOf(Assembly assembly) => assembly
        .GetReferencedAssemblies()
        .Select(name => name.Name)
        .ToArray();

    private static void AssertDoesNotReference(Assembly source, params Assembly[] forbiddenAssemblies)
    {
        var references = source
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var forbidden in forbiddenAssemblies)
        {
            Assert.DoesNotContain(forbidden.GetName().Name, references);
        }
    }
}
```

En `CompositionRootTests.cs`: `using Modules.Pipeline.Application;`; en el `<summary>` de `PermissionDiscoveryFindsTheFiftyTwoConstants` agregar «… y las tres <c>pipeline.*</c> del spec 2026-10-13»; y reemplazar la prueba:

```csharp
    [Fact]
    public void PermissionDiscoveryFindsTheFiftyFiveConstants()
    {
        // 46 + los dos de Messaging (spec 2026-10-09 §6.3) + los dos de Customers (spec 2026-10-11 §4.3) + el de campañas (§6.4)
        // + el de respuestas rápidas (spec 2026-10-12 §3.5) + los tres del embudo (spec 2026-10-13 D23).
        Assert.Equal(55, PermissionConstants().Length);
    }
```

Y después de `TheAdminAndAdvisorRolesCarryTheMessagingPermissionsAsCore`:

```csharp
    /// <summary>Spec 2026-10-13 D32 y P1 del plan: de núcleo (cada handler revisa la capacidad pipeline); <c>admin</c> con los tres,
    /// <c>advisor</c> sin los ajustes, <c>billing</c> y <c>cashier</c> sin ninguno.</summary>
    [Fact]
    public void ThePipelinePermissionsAreCoreAndGoToAdminAndAdvisor()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();
        string[] pipelinePermissions =
            [PipelinePermissions.DealRead, PipelinePermissions.DealManage, PipelinePermissions.SettingsManage];

        Assert.Equal(["pipeline.deal.read", "pipeline.deal.manage", "pipeline.settings.manage"], pipelinePermissions);
        Assert.All(pipelinePermissions, permission => Assert.Contains(permission, catalog.PermissionsFor("admin")));
        Assert.Contains(PipelinePermissions.DealRead, catalog.PermissionsFor("advisor"));
        Assert.Contains(PipelinePermissions.DealManage, catalog.PermissionsFor("advisor"));
        Assert.DoesNotContain(PipelinePermissions.SettingsManage, catalog.PermissionsFor("advisor"));
        foreach (var role in new[] { "billing", "cashier" })
        {
            Assert.All(pipelinePermissions, permission => Assert.DoesNotContain(permission, catalog.PermissionsFor(role)));
        }

        var definitions = catalog.ListPermissions()
            .Where(definition => pipelinePermissions.Contains(definition.Permission))
            .ToArray();
        Assert.Equal(3, definitions.Length);
        Assert.All(definitions, definition =>
        {
            Assert.Equal("Pipeline", definition.Category);
            Assert.Empty(definition.RequiredModules!);
        });
    }
```

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --no-build --filter "FullyQualifiedName~PipelineLayerTests|FullyQualifiedName~CompositionRootTests"
```

Esperado: `PipelineLayerTests` verde (los proyectos existen); en `CompositionRootTests` fallan `ThePipelinePermissionsAreCoreAndGoToAdminAndAdvisor` (`Assert.Contains() Failure`) y las que exigen definición por constante (las tres `pipeline.*` no tienen `PermissionDefinition`). Copiar la salida.

- [ ] **Step 3: Roles, definiciones y políticas**

En `QepServiceCollectionExtensions.cs`:

1. Rol `admin`, después de `MessagingPermissions.QuickReplyManage` (`:902`; poner coma a esa línea):

```csharp
                MessagingPermissions.QuickReplyManage,
                // Spec 2026-10-13 D32: el embudo de ventas completo. De núcleo; la capacidad pipeline la revisa cada handler.
                PipelinePermissions.DealRead,
                PipelinePermissions.DealManage,
                PipelinePermissions.SettingsManage
```

2. Rol `advisor`, después de `MessagingPermissions.ConversationManage` (`:952`; poner coma a esa línea):

```csharp
                MessagingPermissions.ConversationManage,
                // Spec 2026-10-13 D32: la asesora trabaja sus negocios; los ajustes del embudo los decide quien administra.
                PipelinePermissions.DealRead,
                PipelinePermissions.DealManage
```

3. Definiciones, después de la de `MessagingPermissions.QuickReplyManage` (`:1379-1385`):

```csharp
        // Spec 2026-10-13 D23 y P1 del plan: categoría "Pipeline" y núcleo (RequiredModules vacío), para que el enmascarado por
        // módulos no las toque y el 403 con la capacidad apagada salga con su código.
        services.AddSingleton(new PermissionDefinition(
            PipelinePermissions.DealRead,
            "Ver el embudo de ventas",
            "Permite ver el tablero, los negocios del tenant, el triage de conversaciones y los ajustes del embudo.",
            "Pipeline",
            "low",
            RequiredModules: []));
        services.AddSingleton(new PermissionDefinition(
            PipelinePermissions.DealManage,
            "Gestionar negocios",
            "Permite crear, editar, mover, ganar, perder y reabrir negocios, enlazar cotizaciones y decidir el triage.",
            "Pipeline",
            "medium",
            RequiredModules: []));
        services.AddSingleton(new PermissionDefinition(
            PipelinePermissions.SettingsManage,
            "Configurar el embudo",
            "Permite cambiar las etapas del embudo y los motivos de pérdida y de descarte.",
            "Pipeline",
            "medium",
            RequiredModules: []));
```

4. Políticas, después de la de `MessagingPermissions.QuickReplyManage` (`:1682-1684`; el `;` final pasa a la última):

```csharp
            .AddPolicy(
                MessagingPermissions.QuickReplyManage,
                policy => AddPermissionRequirement(policy, MessagingPermissions.QuickReplyManage))
            // Spec 2026-10-13: sin estas políticas, RequireAuthorization no resuelve y el síntoma es 500, no 403 (CLAUDE.md).
            .AddPolicy(
                PipelinePermissions.DealRead,
                policy => AddPermissionRequirement(policy, PipelinePermissions.DealRead))
            .AddPolicy(
                PipelinePermissions.DealManage,
                policy => AddPermissionRequirement(policy, PipelinePermissions.DealManage))
            .AddPolicy(
                PipelinePermissions.SettingsManage,
                policy => AddPermissionRequirement(policy, PipelinePermissions.SettingsManage));
```

- [ ] **Step 4: Correr y verificar que pasan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --no-build
```

Esperado: todo `ArchitectureTests` verde (es un proyecto chico y lo tocan los tres cambios: capas, composición y `ConfigurationExampleTests`, porque Pipeline no declara opciones).

- [ ] **Step 5: Commit**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
if ((git -C $B branch --show-current) -ne "feature/embudo-de-ventas") { throw "rama equivocada" }
git -C $B status --short
git -C $B add Backend.slnx src/Modules/Pipeline tests/Modules/Pipeline src/Api/Api.csproj src/Api/Program.cs src/Bootstrapper/Bootstrapper.csproj src/Bootstrapper/QepServiceCollectionExtensions.cs tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj tests/ArchitectureTests/ArchitectureTests/PipelineLayerTests.cs tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs
git -C $B status --short -- '*.lock.json'
```

Agregar uno por uno, con su ruta, los `packages.lock.json` modificados que listó el último comando (los de `src/Modules/Pipeline` y `tests/Modules/Pipeline` ya entraron con su carpeta). Antes, comprobar que `src/Modules/Pipeline` y `tests/Modules/Pipeline` no traen `bin/` ni `obj/` (los ignora `.gitignore`; si `git status` los muestra, se detiene y se avisa). Después:

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
git -C $B commit -m "feat(pipeline): proyectos del módulo, pruebas de capas y permisos del embudo"
git -C $B log -1 --format=%B
```

---

### Task 4: Dominio — embudo, etapas, motivos y reglas de triage y estancado

**Files:**
- Create: `src/Modules/Pipeline/Modules.Pipeline.Domain/PipelineDomainException.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Domain/PipelineEnums.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Domain/DealActor.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Domain/SalesPipeline.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Domain/PipelineReason.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Domain/PipelineRules.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Domain/TriageDecision.cs`
- Test: `tests/Modules/Pipeline/Modules.Pipeline.UnitTests/SalesPipelineTests.cs`, `ReasonCatalogTests.cs`, `PipelineRulesTests.cs`

**Interfaces:**
- Consumes: `PipelineErrorCodes` (Task 3), `BuildingBlocks.Domain.DomainException(string code, string message)`.
- Produces:
  - `public enum StageKind { Open, Won, Lost }`, `DealSource { Manual, Conversation, Quotation }`, `ActorKind { User, System }`, `TriageDecisionKind { Created, Dismissed }`, `ReasonKind { Loss, Dismiss }`, `DealQuotationStatus { Sent, Converted }`.
  - `public readonly record struct DealActor(ActorKind Kind, Guid? MemberId)` con `DealActor.ForSystem` y `DealActor.ForUser(Guid memberId)`.
  - `SalesPipeline`: `Id`, `TenantId`, `Name`, `Version`, `CreatedAt`, `UpdatedAt`, `IReadOnlyCollection<PipelineStage> Stages`, `IReadOnlyList<PipelineStage> OrderedStages`, `FirstOpenStage`, `WonStage`, `LostStage`, `QuotationStage`, `PipelineStage? FindStage(Guid)`, `static SalesPipeline CreateDefault(Guid tenantId, DateTimeOffset now)`, `void ReplaceStages(IReadOnlyList<StageInput> inputs, IReadOnlySet<Guid> stagesWithDeals, DateTimeOffset now)`; constantes `NameMaxLength = 80`, `StageNameMaxLength = 60`, `MaxStages = 20`, `MaxStallDays = 365`, `DefaultName = "Embudo de ventas"`.
  - `PipelineStage`: `Id`, `PipelineId`, `Name`, `Position` (desde 1), `Probability`, `Kind`, `StallDays`, `IsQuotationStage`.
  - `public sealed record StageInput(Guid? Id, string Name, StageKind Kind, int Probability, int? StallDays, bool IsQuotationStage)`.
  - `public sealed record SeedStage(string Name, StageKind Kind, int Probability, int? StallDays, bool IsQuotationStage)`; `PipelineSeed.Stages`, `PipelineSeed.LossReasons`, `PipelineSeed.DismissReasons`.
  - `PipelineReason`: `Id`, `TenantId`, `Kind`, `Name`, `Position`, `IsActive`, `CreatedAt`, `UpdatedAt`, `static Create(Guid tenantId, ReasonKind kind, string name, int position, DateTimeOffset now)`, `const int NameMaxLength = 80`.
  - `public sealed record ReasonInput(Guid? Id, string Name)`; `ReasonCatalog.Replace(Guid tenantId, ReasonKind kind, IReadOnlyCollection<PipelineReason> current, IReadOnlyList<ReasonInput> inputs, DateTimeOffset now) → IReadOnlyList<PipelineReason>` (los nuevos a agregar), `ReasonCatalog.MaxActive = 30`.
  - `TriageRules.IsPending(bool conversationIsOpen, Guid? customerId, bool customerHasOpenDeal, DateTimeOffset? lastInboundAt, DateTimeOffset? lastDecidedAgainstInboundAt)`.
  - `StallRules.LastActivity(DateTimeOffset dealChangedAt, DateTimeOffset? lastMessageAt)`, `StallRules.IsStalled(StageKind kind, int? stallDays, DateTimeOffset lastActivity, DateTimeOffset now)`, `StallRules.DaysIn(DateTimeOffset enteredAt, DateTimeOffset now)`.
  - `TriageDecision`: `Id`, `TenantId`, `ConversationId`, `CustomerId`, `Kind`, `DealId`, `DismissReasonId`, `DecidedAt`, `DecidedAgainstInboundAt` (D38, no nulo), `ActorKind`, `ActorMemberId`; `static readonly DateTimeOffset NoInbound` (= `DateTimeOffset.UnixEpoch`); `static Created(Guid tenantId, Guid conversationId, Guid customerId, Guid dealId, DateTimeOffset? lastInboundAt, DealActor actor, DateTimeOffset now)`, `static Dismissed(Guid tenantId, Guid conversationId, Guid customerId, Guid dismissReasonId, DateTimeOffset? lastInboundAt, DealActor actor, DateTimeOffset now)`.

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Pipeline/Modules.Pipeline.UnitTests/SalesPipelineTests.cs`:

```csharp
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.UnitTests;

/// <summary>Spec 2026-10-13 §4.1, D10–D12 y D31: el embudo por defecto y las reglas de su disposición. Un rechazo no cambia nada.</summary>
public sealed class SalesPipelineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-0000000000b1");
    private static readonly IReadOnlySet<Guid> NoDeals = new HashSet<Guid>();

    public static TheoryData<string> BrokenLayouts => new()
    {
        "no-open", "two-won", "won-before-open", "lost-before-won", "no-quotation-stage", "two-quotation-stages",
        "quotation-stage-closing", "open-without-stall", "stall-over-limit", "closing-with-stall", "probability-over-100",
        "negative-probability", "blank-name", "long-name", "duplicate-name", "unknown-id", "repeated-id", "empty", "too-many",
    };

    private static SalesPipeline Default() => SalesPipeline.CreateDefault(TenantId, Now);

    private static string CodeOf(Action action) => Assert.Throws<PipelineDomainException>(action).Code;

    private static StageInput Keep(PipelineStage stage) =>
        new(stage.Id, stage.Name, stage.Kind, stage.Probability, stage.StallDays, stage.IsQuotationStage);

    private static List<StageInput> CurrentLayout(SalesPipeline pipeline) => [.. pipeline.OrderedStages.Select(Keep)];

    [Fact]
    public void TheDefaultPipelineHasTheSeededStagesInOrder()
    {
        var pipeline = Default();
        var stages = pipeline.OrderedStages;

        Assert.Equal((TenantId, "Embudo de ventas", 1L), (pipeline.TenantId, pipeline.Name, pipeline.Version));
        Assert.Equal(["Nuevo", "Calificado", "Cotizado", "Negociación", "Ganado", "Perdido"], stages.Select(stage => stage.Name));
        Assert.Equal([1, 2, 3, 4, 5, 6], stages.Select(stage => stage.Position));
        Assert.Equal([10, 25, 50, 75, 100, 0], stages.Select(stage => stage.Probability));
        Assert.Equal([3, 5, 7, 7, (int?)null, null], stages.Select(stage => stage.StallDays));
        Assert.Equal(
            [StageKind.Open, StageKind.Open, StageKind.Open, StageKind.Open, StageKind.Won, StageKind.Lost],
            stages.Select(stage => stage.Kind));
        Assert.Equal(("Nuevo", "Cotizado", "Ganado", "Perdido"),
            (pipeline.FirstOpenStage.Name, pipeline.QuotationStage.Name, pipeline.WonStage.Name, pipeline.LostStage.Name));
        Assert.All(stages, stage => Assert.Equal(pipeline.Id, stage.PipelineId));
    }

    [Fact]
    public void TheSeededReasonsAreTheOnesOfTheSpec()
    {
        Assert.Equal(["Precio", "Competencia", "Sin respuesta", "Sin presupuesto", "Otro"], PipelineSeed.LossReasons);
        Assert.Equal(["Soporte", "Recompra", "Proveedor", "Spam", "Otro"], PipelineSeed.DismissReasons);
    }

    // D12: renombrar conserva el id; el historial apunta a ids.
    [Fact]
    public void RenamingKeepsTheIdAndBumpsTheVersion()
    {
        var pipeline = Default();
        var layout = CurrentLayout(pipeline);
        var firstId = layout[0].Id!.Value;
        layout[0] = layout[0] with { Name = "  Prospecto " };

        pipeline.ReplaceStages(layout, NoDeals, Now.AddMinutes(1));

        Assert.Equal("Prospecto", pipeline.FindStage(firstId)!.Name);
        Assert.Equal((2L, Now.AddMinutes(1)), (pipeline.Version, pipeline.UpdatedAt));
    }

    [Fact]
    public void ANewOpenStageIsAddedWhereItComes()
    {
        var pipeline = Default();
        var layout = CurrentLayout(pipeline);
        layout.Insert(3, new StageInput(null, "Demostración", StageKind.Open, 40, 4, false));

        pipeline.ReplaceStages(layout, NoDeals, Now);

        Assert.Equal(
            ["Nuevo", "Calificado", "Cotizado", "Demostración", "Negociación", "Ganado", "Perdido"],
            pipeline.OrderedStages.Select(stage => stage.Name));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], pipeline.OrderedStages.Select(stage => stage.Position));
        Assert.NotEqual(Guid.Empty, pipeline.OrderedStages[3].Id);
    }

    [Fact]
    public void RemovingAStageWithoutDealsDropsIt()
    {
        var pipeline = Default();
        var layout = CurrentLayout(pipeline);
        layout.RemoveAt(1);

        pipeline.ReplaceStages(layout, NoDeals, Now);

        Assert.Equal(["Nuevo", "Cotizado", "Negociación", "Ganado", "Perdido"], pipeline.OrderedStages.Select(stage => stage.Name));
    }

    // Review Focus 2: borrar una etapa con negocios no deja negocios huérfanos ni media disposición aplicada.
    [Fact]
    public void RemovingAStageWithDealsIsRefusedAndNothingChanges()
    {
        var pipeline = Default();
        var layout = CurrentLayout(pipeline);
        var qualified = layout[1].Id!.Value;
        layout.RemoveAt(1);
        layout[0] = layout[0] with { Name = "Otro nombre" };

        Assert.Equal(PipelineErrorCodes.StageHasDeals, CodeOf(() => pipeline.ReplaceStages(layout, new HashSet<Guid> { qualified }, Now)));

        Assert.Equal(6, pipeline.Stages.Count);
        Assert.Equal(("Nuevo", 1L), (pipeline.FirstOpenStage.Name, pipeline.Version));
    }

    // Review Focus 2: una abierta que pasa a Won cerraría en silencio sus negocios.
    [Fact]
    public void AStageCannotChangeItsKind()
    {
        var pipeline = Default();
        var layout = CurrentLayout(pipeline);
        List<StageInput> changed = [layout[0], layout[1], layout[2], layout[3] with { Kind = StageKind.Won, StallDays = null }, layout[5]];

        Assert.Equal(PipelineErrorCodes.StageLayoutInvalid, CodeOf(() => pipeline.ReplaceStages(changed, NoDeals, Now)));
        Assert.Equal(1L, pipeline.Version);
    }

    [Theory]
    [MemberData(nameof(BrokenLayouts))]
    public void ABrokenLayoutIsRefusedAndNothingChanges(string brokenCase)
    {
        var pipeline = Default();

        Assert.Equal(PipelineErrorCodes.StageLayoutInvalid, CodeOf(() => pipeline.ReplaceStages(Broken(pipeline, brokenCase), NoDeals, Now)));

        Assert.Equal(1L, pipeline.Version);
        Assert.Equal(6, pipeline.Stages.Count);
    }

    private static List<StageInput> Broken(SalesPipeline pipeline, string brokenCase)
    {
        var s = CurrentLayout(pipeline); // Nuevo, Calificado, Cotizado, Negociación, Ganado, Perdido
        switch (brokenCase)
        {
            case "no-open": return [s[4], s[5]];
            case "two-won": s.Insert(4, new StageInput(null, "Ganado 2", StageKind.Won, 100, null, false)); return s;
            case "won-before-open": return [s[0], s[4], s[1], s[2], s[3], s[5]];
            case "lost-before-won": return [s[0], s[1], s[2], s[3], s[5], s[4]];
            case "no-quotation-stage": s[2] = s[2] with { IsQuotationStage = false }; return s;
            case "two-quotation-stages": s[1] = s[1] with { IsQuotationStage = true }; return s;
            case "quotation-stage-closing": s[2] = s[2] with { IsQuotationStage = false }; s[4] = s[4] with { IsQuotationStage = true }; return s;
            case "open-without-stall": s[0] = s[0] with { StallDays = null }; return s;
            case "stall-over-limit": s[0] = s[0] with { StallDays = 366 }; return s;
            case "closing-with-stall": s[4] = s[4] with { StallDays = 3 }; return s;
            case "probability-over-100": s[0] = s[0] with { Probability = 101 }; return s;
            case "negative-probability": s[5] = s[5] with { Probability = -1 }; return s;
            case "blank-name": s[0] = s[0] with { Name = "   " }; return s;
            case "long-name": s[0] = s[0] with { Name = new string('a', 61) }; return s;
            case "duplicate-name": s[1] = s[1] with { Name = " nuevo " }; return s;
            case "unknown-id": s[0] = s[0] with { Id = Guid.CreateVersion7() }; return s;
            case "repeated-id": s[1] = s[1] with { Id = s[0].Id }; return s;
            case "empty": return [];
            case "too-many":
                s.InsertRange(0, Enumerable.Range(1, 15).Select(index => new StageInput(null, $"Extra {index}", StageKind.Open, 10, 3, false)));
                return s;
            default: throw new ArgumentOutOfRangeException(nameof(brokenCase));
        }
    }
}
```

`tests/Modules/Pipeline/Modules.Pipeline.UnitTests/ReasonCatalogTests.cs`:

```csharp
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.UnitTests;

/// <summary>Spec 2026-10-13 §6 y P8 del plan: el catálogo de motivos se reemplaza entero; lo que deja de venir se desactiva
/// (el historial lo nombra), y una disposición inválida no cambia nada.</summary>
public sealed class ReasonCatalogTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddMinutes(5);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-0000000000b2");

    public static TheoryData<string> BrokenCatalogs => new() { "empty", "blank", "long", "duplicate", "unknown-id", "other-kind-id", "repeated-id", "too-many" };

    private static List<PipelineReason> Three() =>
    [
        PipelineReason.Create(TenantId, ReasonKind.Loss, "Precio", 1, Now),
        PipelineReason.Create(TenantId, ReasonKind.Loss, "Competencia", 2, Now),
        PipelineReason.Create(TenantId, ReasonKind.Loss, "Otro", 3, Now),
    ];

    [Fact]
    public void ReplacingRenamesReordersAddsAndDeactivates()
    {
        var current = Three();
        var (price, competition, other) = (current[0], current[1], current[2]);

        var added = ReasonCatalog.Replace(TenantId, ReasonKind.Loss, current,
            [new ReasonInput(other.Id, " Otra razón "), new ReasonInput(null, "Sin presupuesto"), new ReasonInput(price.Id, "Precio")], Later);

        var created = Assert.Single(added);
        Assert.Equal((TenantId, ReasonKind.Loss, "Sin presupuesto", 2, true), (created.TenantId, created.Kind, created.Name, created.Position, created.IsActive));
        Assert.Equal(("Otra razón", 1, true), (other.Name, other.Position, other.IsActive));
        Assert.Equal((3, true), (price.Position, price.IsActive));
        Assert.Equal((false, Later), (competition.IsActive, competition.UpdatedAt));
    }

    [Fact]
    public void NamingAnInactiveReasonReactivatesIt()
    {
        var current = Three();
        ReasonCatalog.Replace(TenantId, ReasonKind.Loss, current, [new ReasonInput(current[0].Id, "Precio")], Now);

        ReasonCatalog.Replace(TenantId, ReasonKind.Loss, current, [new ReasonInput(current[0].Id, "Precio"), new ReasonInput(current[1].Id, "Competencia")], Later);

        Assert.True(current[1].IsActive);
        Assert.False(current[2].IsActive);
    }

    [Theory]
    [MemberData(nameof(BrokenCatalogs))]
    public void ABrokenCatalogIsRefusedAndNothingChanges(string brokenCase)
    {
        var current = Three();
        current.Add(PipelineReason.Create(TenantId, ReasonKind.Dismiss, "Spam", 1, Now));
        List<ReasonInput> inputs = brokenCase switch
        {
            "empty" => [],
            "blank" => [new ReasonInput(null, "  ")],
            "long" => [new ReasonInput(null, new string('a', 81))],
            "duplicate" => [new ReasonInput(current[0].Id, "Precio"), new ReasonInput(null, " precio ")],
            "unknown-id" => [new ReasonInput(Guid.CreateVersion7(), "Precio")],
            "other-kind-id" => [new ReasonInput(current[3].Id, "Spam")],
            "repeated-id" => [new ReasonInput(current[0].Id, "Precio"), new ReasonInput(current[0].Id, "Precio 2")],
            "too-many" => [.. Enumerable.Range(1, 31).Select(index => new ReasonInput(null, $"Motivo {index}"))],
            _ => throw new ArgumentOutOfRangeException(nameof(brokenCase)),
        };

        var error = Assert.Throws<PipelineDomainException>(() => ReasonCatalog.Replace(TenantId, ReasonKind.Loss, current, inputs, Later));

        Assert.Equal(PipelineErrorCodes.ReasonLayoutInvalid, error.Code);
        Assert.All(current, reason => Assert.Equal((true, Now), (reason.IsActive, reason.UpdatedAt)));
    }
}
```

`tests/Modules/Pipeline/Modules.Pipeline.UnitTests/PipelineRulesTests.cs`:

```csharp
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.UnitTests;

/// <summary>Spec 2026-10-13 §5.1, §5.2, D20, D22, D35 y P13 del plan: cuándo una conversación está pendiente de triage y cuándo
/// un negocio está estancado. Funciones puras: las usan el tablero, el triage y el resumen del cliente.</summary>
public sealed class PipelineRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid CustomerId = Guid.Parse("01900000-0000-7000-8000-0000000000c1");

    [Fact]
    public void AnOpenConversationWithoutDealNorDecisionIsPending() =>
        Assert.True(TriageRules.IsPending(true, CustomerId, false, Now.AddHours(-1), null));

    [Fact]
    public void ACustomerWithAnOpenDealIsNotPending() =>
        Assert.False(TriageRules.IsPending(true, CustomerId, true, Now.AddHours(-1), null));

    [Fact]
    public void AResolvedConversationIsNotPending() =>
        Assert.False(TriageRules.IsPending(false, CustomerId, false, Now.AddHours(-1), null));

    // D35: una fila vieja sin cliente queda fuera del triage.
    [Fact]
    public void AConversationWithoutCustomerIsNotPending() =>
        Assert.False(TriageRules.IsPending(true, null, false, Now.AddHours(-1), null));

    // D20 y D38: la decisión vale contra el entrante con el que se tomó; un entrante nuevo es un instante nuevo.
    [Fact]
    public void ADismissalHidesItUntilTheCustomerWritesAgain()
    {
        var inbound = Now.AddDays(-30);

        Assert.False(TriageRules.IsPending(true, CustomerId, false, inbound, inbound));
        Assert.True(TriageRules.IsPending(true, CustomerId, false, Now, inbound));
    }

    // P13 y D38: sin entrante, la decisión se toma contra NoInbound.
    [Fact]
    public void WithoutInboundItIsPendingUntilDecided()
    {
        Assert.True(TriageRules.IsPending(true, CustomerId, false, null, null));
        Assert.False(TriageRules.IsPending(true, CustomerId, false, null, TriageDecision.NoInbound));
    }

    [Fact]
    public void AnOpenDealPastItsStallDaysIsStalled()
    {
        Assert.False(StallRules.IsStalled(StageKind.Open, 3, Now.AddDays(-3), Now));
        Assert.True(StallRules.IsStalled(StageKind.Open, 3, Now.AddDays(-3).AddSeconds(-1), Now));
    }

    [Fact]
    public void AClosingStageIsNeverStalled()
    {
        Assert.False(StallRules.IsStalled(StageKind.Won, null, Now.AddDays(-400), Now));
        Assert.False(StallRules.IsStalled(StageKind.Lost, 3, Now.AddDays(-400), Now));
    }

    // §5.2: última actividad = lo más reciente entre el cambio del negocio y el último mensaje (entrante o saliente).
    [Fact]
    public void LastActivityIsTheLatestOfDealChangeAndMessage()
    {
        Assert.Equal(Now, StallRules.LastActivity(Now.AddDays(-5), Now));
        Assert.Equal(Now, StallRules.LastActivity(Now, Now.AddDays(-5)));
        Assert.Equal(Now, StallRules.LastActivity(Now, null));
    }

    [Fact]
    public void DaysInFloorsAndNeverGoesNegative()
    {
        Assert.Equal(2, StallRules.DaysIn(Now.AddDays(-2).AddHours(-23), Now));
        Assert.Equal(0, StallRules.DaysIn(Now.AddMinutes(5), Now));
    }

    [Fact]
    public void ADismissalKeepsTheReasonAndTheActor()
    {
        var tenantId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var reasonId = Guid.CreateVersion7();
        var memberId = Guid.CreateVersion7();

        var decision = TriageDecision.Dismissed(tenantId, conversationId, CustomerId, reasonId, Now.AddHours(-2), DealActor.ForUser(memberId), Now);

        Assert.Equal((TriageDecisionKind.Dismissed, reasonId, (Guid?)null), (decision.Kind, decision.DismissReasonId, decision.DealId));
        Assert.Equal((ActorKind.User, (Guid?)memberId, Now), (decision.ActorKind, decision.ActorMemberId, decision.DecidedAt));
        Assert.Equal((tenantId, conversationId, CustomerId), (decision.TenantId, decision.ConversationId, decision.CustomerId));
        Assert.Equal(Now.AddHours(-2), decision.DecidedAgainstInboundAt);
    }

    // D38: sin entrante, la decisión guarda NoInbound (la columna del índice único no admite nulos).
    [Fact]
    public void ADecisionWithoutInboundIsTakenAgainstNoInbound()
    {
        var decision = TriageDecision.Created(Guid.CreateVersion7(), Guid.CreateVersion7(), CustomerId, Guid.CreateVersion7(), null, DealActor.ForSystem, Now);

        Assert.Equal(DateTimeOffset.UnixEpoch, TriageDecision.NoInbound);
        Assert.Equal(TriageDecision.NoInbound, decision.DecidedAgainstInboundAt);
    }
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.UnitTests\Modules.Pipeline.UnitTests.csproj" --filter "FullyQualifiedName~SalesPipelineTests|FullyQualifiedName~ReasonCatalogTests|FullyQualifiedName~PipelineRulesTests"
```

Esperado: `error CS0246` sobre `SalesPipeline`, `PipelineDomainException`, `ReasonCatalog`, `TriageRules`.

- [ ] **Step 3: Implementar**

`src/Modules/Pipeline/Modules.Pipeline.Domain/PipelineDomainException.cs`:

```csharp
using BuildingBlocks.Domain;

namespace Modules.Pipeline.Domain;

/// <summary>Regla de negocio del embudo: <c>ApiExceptionHandler</c> la responde 422 con su código.</summary>
public sealed class PipelineDomainException : DomainException
{
    public PipelineDomainException(string code, string message)
        : base(code, message)
    {
    }

    /// <summary>Cuando el 422 envuelve una falla de la base (la FK de etapa en <c>PipelineUnitOfWork</c>): la causa queda en
    /// <c>platform.request_failures</c>.</summary>
    public PipelineDomainException(string code, string message, Exception innerException)
        : base(code, message, innerException)
    {
    }
}
```

`src/Modules/Pipeline/Modules.Pipeline.Domain/PipelineEnums.cs`:

```csharp
namespace Modules.Pipeline.Domain;

// Se guardan y viajan por nombre (texto): el diccionario de etiquetas lo tiene el frontend, y agregar un valor (Ai, D19) no
// necesita migración.

/// <summary>Spec 2026-10-13 D11: una etapa abierta, la de ganado o la de perdido.</summary>
public enum StageKind
{
    Open,
    Won,
    Lost,
}

/// <summary>Spec 2026-10-13 §4.2: de dónde nació el negocio.</summary>
public enum DealSource
{
    Manual,
    Conversation,
    Quotation,
}

/// <summary>Spec 2026-10-13 D19: quién movió la etapa. <c>Ai</c> llega con el slice de IA.</summary>
public enum ActorKind
{
    User,
    System,
}

/// <summary>Spec 2026-10-13 D29: las dos acciones del triage.</summary>
public enum TriageDecisionKind
{
    Created,
    Dismissed,
}

/// <summary>Los dos catálogos de motivos del tenant.</summary>
public enum ReasonKind
{
    Loss,
    Dismiss,
}

/// <summary>Spec 2026-10-13 §4.4: el estado que Pipeline conoce de una cotización (sólo por sus eventos).</summary>
public enum DealQuotationStatus
{
    Sent,
    Converted,
}
```

`src/Modules/Pipeline/Modules.Pipeline.Domain/DealActor.cs`:

```csharp
namespace Modules.Pipeline.Domain;

/// <summary>Spec 2026-10-13 D19: quién hizo el cambio. Una persona lleva su membresía; el sistema (el worker de cotizaciones),
/// ninguna.</summary>
public readonly record struct DealActor(ActorKind Kind, Guid? MemberId)
{
    public static DealActor ForSystem { get; } = new(ActorKind.System, null);

    public static DealActor ForUser(Guid memberId) => new(ActorKind.User, memberId);
}
```

`src/Modules/Pipeline/Modules.Pipeline.Domain/SalesPipeline.cs`:

```csharp
namespace Modules.Pipeline.Domain;

/// <summary>
/// Spec 2026-10-13 §4.1, D10–D12: el embudo del tenant. Uno por tenant en este slice, pero las etapas son hijas suyas (modelo
/// HubSpot), así que varios embudos después es quitar el índice único, no mover columnas. <see cref="ReplaceStages"/> recibe la
/// colección entera porque una etapa sola no sabe si es la única <c>Won</c>.
/// </summary>
public sealed class SalesPipeline
{
    public const int NameMaxLength = 80;
    public const int StageNameMaxLength = 60;
    public const int MaxStages = 20;
    public const int MaxStallDays = 365;
    public const string DefaultName = "Embudo de ventas";

    private readonly List<PipelineStage> _stages = [];

    private SalesPipeline() => Name = string.Empty;

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Sin orden: EF las carga como vengan. Para recorrerlas en orden, <see cref="OrderedStages"/>.</summary>
    public IReadOnlyCollection<PipelineStage> Stages => _stages;

    public IReadOnlyList<PipelineStage> OrderedStages => [.. _stages.OrderBy(stage => stage.Position)];

    public PipelineStage FirstOpenStage => OrderedStages.First(stage => stage.Kind == StageKind.Open);

    public PipelineStage WonStage => _stages.Single(stage => stage.Kind == StageKind.Won);

    public PipelineStage LostStage => _stages.Single(stage => stage.Kind == StageKind.Lost);

    public PipelineStage QuotationStage => _stages.Single(stage => stage.IsQuotationStage);

    public PipelineStage? FindStage(Guid stageId) => _stages.FirstOrDefault(stage => stage.Id == stageId);

    /// <summary>D31: el embudo con el que arranca todo tenant, editable después.</summary>
    public static SalesPipeline CreateDefault(Guid tenantId, DateTimeOffset now)
    {
        var pipeline = new SalesPipeline
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = DefaultName,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var position = 0;
        foreach (var seed in PipelineSeed.Stages)
        {
            position++;
            pipeline._stages.Add(PipelineStage.Create(
                pipeline.Id, seed.Name, position, seed.Probability, seed.Kind, seed.StallDays, seed.IsQuotationStage));
        }

        return pipeline;
    }

    /// <summary>
    /// D11 y D12: reemplaza la disposición entera. Valida todo antes de tocar nada; una etapa que estaba y ya no viene se borra,
    /// salvo que tenga negocios (<paramref name="stagesWithDeals"/>), y una etapa que existe no cambia de tipo (pasar una abierta
    /// a <c>Won</c> cerraría en silencio sus negocios).
    /// </summary>
    public void ReplaceStages(IReadOnlyList<StageInput> inputs, IReadOnlySet<Guid> stagesWithDeals, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(stagesWithDeals);
        var normalized = Validate(inputs);

        var kept = normalized.Where(input => input.Id is not null).Select(input => input.Id!.Value).ToHashSet();
        var removed = _stages.Where(stage => !kept.Contains(stage.Id)).ToArray();
        if (removed.Any(stage => stagesWithDeals.Contains(stage.Id)))
        {
            throw new PipelineDomainException(
                PipelineErrorCodes.StageHasDeals, "A stage with deals cannot be removed; move its deals first.");
        }

        foreach (var stage in removed)
        {
            _stages.Remove(stage);
        }

        for (var index = 0; index < normalized.Count; index++)
        {
            var input = normalized[index];
            var position = index + 1;
            if (input.Id is { } id)
            {
                _stages.Single(stage => stage.Id == id)
                    .Update(input.Name, position, input.Probability, input.StallDays, input.IsQuotationStage);
            }
            else
            {
                _stages.Add(PipelineStage.Create(
                    Id, input.Name, position, input.Probability, input.Kind, input.StallDays, input.IsQuotationStage));
            }
        }

        Version++;
        UpdatedAt = now;
    }

    private List<StageInput> Validate(IReadOnlyList<StageInput> inputs)
    {
        if (inputs.Count is 0 or > MaxStages)
        {
            throw Invalid($"A pipeline has 3 to {MaxStages} stages.");
        }

        var normalized = inputs.Select(input => input with { Name = (input.Name ?? string.Empty).Trim() }).ToList();
        if (normalized.Any(input => input.Name.Length is 0 or > StageNameMaxLength))
        {
            throw Invalid($"Every stage needs a name of 1 to {StageNameMaxLength} characters.");
        }

        if (normalized.Select(input => input.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Count)
        {
            throw Invalid("Two stages cannot share a name.");
        }

        var opens = normalized.TakeWhile(input => input.Kind == StageKind.Open).Count();
        if (opens == 0 || !normalized.Skip(opens).Select(input => input.Kind).SequenceEqual([StageKind.Won, StageKind.Lost]))
        {
            throw Invalid("Open stages go first, then exactly one Won stage and one Lost stage.");
        }

        if (normalized.Count(input => input.IsQuotationStage) != 1 ||
            normalized.Any(input => input.IsQuotationStage && input.Kind != StageKind.Open))
        {
            throw Invalid("Exactly one open stage is the quotation stage.");
        }

        foreach (var input in normalized)
        {
            if (input.Probability is < 0 or > 100)
            {
                throw Invalid("A probability goes from 0 to 100.");
            }

            var stallIsValid = input.Kind == StageKind.Open
                ? input.StallDays is >= 1 and <= MaxStallDays
                : input.StallDays is null;
            if (!stallIsValid)
            {
                throw Invalid($"Open stages need 1 to {MaxStallDays} stall days; closing stages have none.");
            }
        }

        var ids = normalized.Where(input => input.Id is not null).Select(input => input.Id!.Value).ToArray();
        if (ids.Distinct().Count() != ids.Length)
        {
            throw Invalid("A stage appears twice.");
        }

        foreach (var input in normalized.Where(input => input.Id is not null))
        {
            var current = FindStage(input.Id!.Value) ?? throw Invalid("A stage id does not belong to this pipeline.");
            if (current.Kind != input.Kind)
            {
                throw Invalid("A stage cannot change its kind.");
            }
        }

        return normalized;
    }

    private static PipelineDomainException Invalid(string message) => new(PipelineErrorCodes.StageLayoutInvalid, message);
}

/// <summary>Spec 2026-10-13 §4.1: una etapa del embudo. <see cref="Position"/> empieza en 1; <see cref="StallDays"/> sólo en las
/// abiertas.</summary>
public sealed class PipelineStage
{
    private PipelineStage() => Name = string.Empty;

    public Guid Id { get; private set; }

    public Guid PipelineId { get; private set; }

    public string Name { get; private set; }

    public int Position { get; private set; }

    public int Probability { get; private set; }

    public StageKind Kind { get; private set; }

    public int? StallDays { get; private set; }

    public bool IsQuotationStage { get; private set; }

    internal static PipelineStage Create(
        Guid pipelineId, string name, int position, int probability, StageKind kind, int? stallDays, bool isQuotationStage) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            PipelineId = pipelineId,
            Name = name,
            Position = position,
            Probability = probability,
            Kind = kind,
            StallDays = stallDays,
            IsQuotationStage = isQuotationStage,
        };

    internal void Update(string name, int position, int probability, int? stallDays, bool isQuotationStage)
    {
        Name = name;
        Position = position;
        Probability = probability;
        StallDays = stallDays;
        IsQuotationStage = isQuotationStage;
    }
}

/// <summary>Una etapa como la pide el <c>PUT /settings/stages</c>: <see cref="Id"/> nulo es una etapa nueva.</summary>
public sealed record StageInput(Guid? Id, string Name, StageKind Kind, int Probability, int? StallDays, bool IsQuotationStage);

/// <summary>Una etapa de la semilla (D31).</summary>
public sealed record SeedStage(string Name, StageKind Kind, int Probability, int? StallDays, bool IsQuotationStage);

/// <summary>Spec 2026-10-13 D31: con lo que arranca todo tenant. Editable después desde Ajustes.</summary>
public static class PipelineSeed
{
    public static IReadOnlyList<SeedStage> Stages { get; } =
    [
        new("Nuevo", StageKind.Open, 10, 3, false),
        new("Calificado", StageKind.Open, 25, 5, false),
        new("Cotizado", StageKind.Open, 50, 7, true),
        new("Negociación", StageKind.Open, 75, 7, false),
        new("Ganado", StageKind.Won, 100, null, false),
        new("Perdido", StageKind.Lost, 0, null, false),
    ];

    public static IReadOnlyList<string> LossReasons { get; } = ["Precio", "Competencia", "Sin respuesta", "Sin presupuesto", "Otro"];

    public static IReadOnlyList<string> DismissReasons { get; } = ["Soporte", "Recompra", "Proveedor", "Spam", "Otro"];
}
```

`src/Modules/Pipeline/Modules.Pipeline.Domain/PipelineReason.cs`:

```csharp
namespace Modules.Pipeline.Domain;

/// <summary>Un motivo de pérdida (D14) o de descarte (D29) del tenant. No se borra: el historial lo nombra (P8 del plan); se
/// desactiva.</summary>
public sealed class PipelineReason
{
    public const int NameMaxLength = 80;

    private PipelineReason() => Name = string.Empty;

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public ReasonKind Kind { get; private set; }

    public string Name { get; private set; }

    public int Position { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static PipelineReason Create(Guid tenantId, ReasonKind kind, string name, int position, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Kind = kind,
            Name = name,
            Position = position,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

    internal void Place(string name, int position, DateTimeOffset now)
    {
        Name = name;
        Position = position;
        IsActive = true;
        UpdatedAt = now;
    }

    internal void Deactivate(DateTimeOffset now)
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        UpdatedAt = now;
    }
}

/// <summary>Un motivo como lo pide el <c>PUT</c>: <see cref="Id"/> nulo es uno nuevo.</summary>
public sealed record ReasonInput(Guid? Id, string Name);

/// <summary>Reemplaza un catálogo entero (P8): renombra, reordena, agrega, reactiva lo que vuelve y desactiva lo que deja de
/// venir. Valida todo antes de tocar nada.</summary>
public static class ReasonCatalog
{
    public const int MaxActive = 30;

    /// <returns>Los motivos nuevos, para que el repositorio los agregue.</returns>
    public static IReadOnlyList<PipelineReason> Replace(
        Guid tenantId, ReasonKind kind, IReadOnlyCollection<PipelineReason> current, IReadOnlyList<ReasonInput> inputs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(inputs);
        var normalized = inputs.Select(input => input with { Name = (input.Name ?? string.Empty).Trim() }).ToArray();
        if (normalized.Length is 0 or > MaxActive)
        {
            throw Invalid($"A catalog has 1 to {MaxActive} reasons.");
        }

        if (normalized.Any(input => input.Name.Length is 0 or > PipelineReason.NameMaxLength))
        {
            throw Invalid($"Every reason needs a name of 1 to {PipelineReason.NameMaxLength} characters.");
        }

        if (normalized.Select(input => input.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
        {
            throw Invalid("Two reasons cannot share a name.");
        }

        var ids = normalized.Where(input => input.Id is not null).Select(input => input.Id!.Value).ToArray();
        if (ids.Distinct().Count() != ids.Length)
        {
            throw Invalid("A reason appears twice.");
        }

        var byId = current.Where(reason => reason.Kind == kind).ToDictionary(reason => reason.Id);
        if (ids.Any(id => !byId.ContainsKey(id)))
        {
            throw Invalid("A reason id does not belong to this catalog.");
        }

        var added = new List<PipelineReason>();
        for (var index = 0; index < normalized.Length; index++)
        {
            var input = normalized[index];
            var position = index + 1;
            if (input.Id is { } id)
            {
                byId[id].Place(input.Name, position, now);
            }
            else
            {
                added.Add(PipelineReason.Create(tenantId, kind, input.Name, position, now));
            }
        }

        var kept = ids.ToHashSet();
        foreach (var reason in byId.Values.Where(reason => !kept.Contains(reason.Id)))
        {
            reason.Deactivate(now);
        }

        return added;
    }

    private static PipelineDomainException Invalid(string message) => new(PipelineErrorCodes.ReasonLayoutInvalid, message);
}
```

`src/Modules/Pipeline/Modules.Pipeline.Domain/PipelineRules.cs`:

```csharp
namespace Modules.Pipeline.Domain;

/// <summary>Spec 2026-10-13 §5.1, D20, D35, D38 y P13 del plan: cuándo una conversación está pendiente de triage.</summary>
public static class TriageRules
{
    /// <summary>Abierta, con cliente, sin negocio abierto del cliente y sin una decisión tomada contra su último entrante
    /// (<paramref name="lastDecidedAgainstInboundAt"/> es el <c>decided_against_inbound_at</c> de la última decisión). Un entrante
    /// nuevo es un instante nuevo: la vuelve pendiente. Sin entrante se compara contra <see cref="TriageDecision.NoInbound"/>.</summary>
    public static bool IsPending(
        bool conversationIsOpen, Guid? customerId, bool customerHasOpenDeal, DateTimeOffset? lastInboundAt, DateTimeOffset? lastDecidedAgainstInboundAt) =>
        conversationIsOpen
        && customerId is not null
        && !customerHasOpenDeal
        && (lastDecidedAgainstInboundAt is not { } decidedAgainst || decidedAgainst < (lastInboundAt ?? TriageDecision.NoInbound));
}

/// <summary>Spec 2026-10-13 §5.2 y D22: el estancado se calcula al leer, sin worker.</summary>
public static class StallRules
{
    /// <summary>Lo más reciente entre el último cambio del negocio (etapa incluida) y el último mensaje de las conversaciones del
    /// cliente, entrante o saliente.</summary>
    public static DateTimeOffset LastActivity(DateTimeOffset dealChangedAt, DateTimeOffset? lastMessageAt) =>
        lastMessageAt is { } message && message > dealChangedAt ? message : dealChangedAt;

    /// <summary>Tiempo transcurrido en UTC, no días calendario del tenant. Una etapa de cierre nunca se estanca.</summary>
    public static bool IsStalled(StageKind kind, int? stallDays, DateTimeOffset lastActivity, DateTimeOffset now) =>
        kind == StageKind.Open && stallDays is { } days && now - lastActivity > TimeSpan.FromDays(days);

    public static int DaysIn(DateTimeOffset enteredAt, DateTimeOffset now) =>
        Math.Max(0, (int)Math.Floor((now - enteredAt).TotalDays));
}
```

`src/Modules/Pipeline/Modules.Pipeline.Domain/TriageDecision.cs`:

```csharp
namespace Modules.Pipeline.Domain;

/// <summary>
/// Spec 2026-10-13 §5.1, D29 y D38: lo que el asesor decidió sobre una conversación. Append-only; vale la última. Guarda el
/// <c>LastInboundAt</c> contra el que se decidió (<see cref="DecidedAgainstInboundAt"/>): el índice único
/// <c>IX_triage_decisions_conversation_inbound</c> sobre ese instante hace que dos asesores que deciden a la vez no dejen dos
/// negocios, y un entrante nuevo permite decidir otra vez.
/// </summary>
public sealed class TriageDecision
{
    /// <summary>D38 y P13: el instante contra el que se decide una conversación sin entrante. La columna no admite nulos porque
    /// Postgres no considera iguales dos NULL en un índice único.</summary>
    public static readonly DateTimeOffset NoInbound = DateTimeOffset.UnixEpoch;

    private TriageDecision()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConversationId { get; private set; }

    public Guid CustomerId { get; private set; }

    public TriageDecisionKind Kind { get; private set; }

    public Guid? DealId { get; private set; }

    public Guid? DismissReasonId { get; private set; }

    public DateTimeOffset DecidedAt { get; private set; }

    /// <summary>D38: el <c>LastInboundAt</c> de la conversación cuando se decidió, o <see cref="NoInbound"/>.</summary>
    public DateTimeOffset DecidedAgainstInboundAt { get; private set; }

    public ActorKind ActorKind { get; private set; }

    public Guid? ActorMemberId { get; private set; }

    public static TriageDecision Created(
        Guid tenantId, Guid conversationId, Guid customerId, Guid dealId, DateTimeOffset? lastInboundAt, DealActor actor, DateTimeOffset now) =>
        New(tenantId, conversationId, customerId, TriageDecisionKind.Created, dealId, null, lastInboundAt, actor, now);

    public static TriageDecision Dismissed(
        Guid tenantId, Guid conversationId, Guid customerId, Guid dismissReasonId, DateTimeOffset? lastInboundAt, DealActor actor, DateTimeOffset now) =>
        New(tenantId, conversationId, customerId, TriageDecisionKind.Dismissed, null, dismissReasonId, lastInboundAt, actor, now);

    private static TriageDecision New(
        Guid tenantId, Guid conversationId, Guid customerId, TriageDecisionKind kind, Guid? dealId, Guid? reasonId,
        DateTimeOffset? lastInboundAt, DealActor actor, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConversationId = conversationId,
            CustomerId = customerId,
            Kind = kind,
            DealId = dealId,
            DismissReasonId = reasonId,
            DecidedAt = now,
            DecidedAgainstInboundAt = lastInboundAt ?? NoInbound,
            ActorKind = actor.Kind,
            ActorMemberId = actor.MemberId,
        };
}
```

- [ ] **Step 4: Correr y verificar que pasan, más las capas**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.UnitTests\Modules.Pipeline.UnitTests.csproj" --no-build --filter "FullyQualifiedName~SalesPipelineTests|FullyQualifiedName~ReasonCatalogTests|FullyQualifiedName~PipelineRulesTests"
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --no-build --filter "FullyQualifiedName~PipelineLayerTests"
```

Esperado: todo verde (`SalesPipelineTests`: 7 hechos + 19 casos de la teoría; `ReasonCatalogTests`: 2 + 8; `PipelineRulesTests`: 12).

- [ ] **Step 5: Commit**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
if ((git -C $B branch --show-current) -ne "feature/embudo-de-ventas") { throw "rama equivocada" }
git -C $B status --short
git -C $B add src/Modules/Pipeline/Modules.Pipeline.Domain/PipelineDomainException.cs src/Modules/Pipeline/Modules.Pipeline.Domain/PipelineEnums.cs src/Modules/Pipeline/Modules.Pipeline.Domain/DealActor.cs src/Modules/Pipeline/Modules.Pipeline.Domain/SalesPipeline.cs src/Modules/Pipeline/Modules.Pipeline.Domain/PipelineReason.cs src/Modules/Pipeline/Modules.Pipeline.Domain/PipelineRules.cs src/Modules/Pipeline/Modules.Pipeline.Domain/TriageDecision.cs tests/Modules/Pipeline/Modules.Pipeline.UnitTests/SalesPipelineTests.cs tests/Modules/Pipeline/Modules.Pipeline.UnitTests/ReasonCatalogTests.cs tests/Modules/Pipeline/Modules.Pipeline.UnitTests/PipelineRulesTests.cs
git -C $B commit -m "feat(pipeline): embudo con etapas y semillas, catálogos de motivos y reglas de triage y estancado"
git -C $B log -1 --format=%B
```

---

### Task 5: Dominio — negocio, historial y foto de la cotización

**Files:**
- Create: `src/Modules/Pipeline/Modules.Pipeline.Domain/Deal.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Domain/DealStageHistoryEntry.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Domain/DealQuotation.cs`
- Test: `tests/Modules/Pipeline/Modules.Pipeline.UnitTests/DealTests.cs`, `DealQuotationTests.cs`

**Interfaces:**
- Consumes: `SalesPipeline`, `PipelineStage`, `StageKind`, `DealSource`, `DealActor`, `ActorKind`, `DealQuotationStatus`, `PipelineDomainException`, `PipelineErrorCodes` (Tasks 3 y 4).
- Produces:
  - `Deal` con `Id`, `TenantId`, `PipelineId`, `CustomerId`, `OwnerMemberId`, `Title`, `decimal? Amount`, `Currency`, `DateOnly? ExpectedCloseDate`, `StageId`, `StageEnteredAt`, `Guid? PrimaryQuotationId`, `Source`, `Guid? OriginConversationId`, `Guid? LossReasonId`, `string? LossNote`, `DateTimeOffset? ClosedAt`, `LastChangedAt`, `CreatedAt`, `UpdatedAt`, `Version`, `bool IsClosed`; constantes `TitleMaxLength = 200`, `LossNoteMaxLength = 500`, `MaxAmount = 999_999_999_999.99m`.
  - `static Deal Create(Guid tenantId, SalesPipeline pipeline, Guid customerId, Guid ownerMemberId, string? title, decimal? amount, string currency, DateOnly? expectedCloseDate, DealSource source, Guid? originConversationId, DealActor actor, DateTimeOffset now)`.
  - `void Update(string? title, Guid ownerMemberId, decimal? amount, string currency, DateOnly? expectedCloseDate, DateTimeOffset now)`.
  - `bool MoveTo(SalesPipeline pipeline, Guid stageId, DealActor actor, DateTimeOffset now)`, `bool AdvanceToQuotationStage(SalesPipeline pipeline, DealActor actor, DateTimeOffset now)`, `void Win(SalesPipeline pipeline, DealActor actor, DateTimeOffset now)`, `void Lose(SalesPipeline pipeline, Guid? lossReasonId, string? note, DealActor actor, DateTimeOffset now)`, `void Reopen(SalesPipeline pipeline, Guid? lastOpenStageId, DealActor actor, DateTimeOffset now)`.
  - `void SetPrimaryQuotation(Guid quotationId, decimal total, string currency, DateTimeOffset now)`, `void FollowPrimary(decimal total, string currency, DateTimeOffset now)`, `void ClearPrimary(DateTimeOffset now)`, `IReadOnlyList<DealStageHistoryEntry> PullHistory()`.
  - `DealStageHistoryEntry` con `Id`, `TenantId`, `DealId`, `PipelineId`, `Guid? FromStageId`, `ToStageId`, `OccurredAt`, `ActorKind`, `Guid? ActorMemberId`, `decimal? Amount`, `Currency`, `Probability`.
  - `DealQuotation` con `TenantId`, `QuotationId`, `Guid? DealId`, `CustomerId`, `QuotationNumber`, `Total`, `Currency`, `Status`, `Guid? OrderId`, `DateTimeOffset? OrderCancelledAt`, `UpdatedAt`; `const int NumberMaxLength = 40`; `static Create(Guid tenantId, Guid quotationId, string quotationNumber, Guid customerId, decimal total, string currency, DateTimeOffset now)`, `Refresh(string quotationNumber, Guid customerId, decimal total, string currency, bool converted, Guid? orderId, DateTimeOffset now)`, `LinkTo(Guid dealId, DateTimeOffset now)`, `Unlink(DateTimeOffset now)`, `MarkOrderCancelled(DateTimeOffset now)`.

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Pipeline/Modules.Pipeline.UnitTests/DealTests.cs`:

```csharp
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.UnitTests;

/// <summary>Spec 2026-10-13 §4.2, §4.3, D13–D19: transiciones del negocio, su historial y el monto que sigue a la cotización
/// principal. Cada cambio de etapa deja su fila pendiente, que la unidad de trabajo guarda en el mismo SaveChanges.</summary>
public sealed class DealTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-0000000000d1");
    private static readonly Guid CustomerId = Guid.Parse("01900000-0000-7000-8000-0000000000d2");
    private static readonly Guid OwnerId = Guid.Parse("01900000-0000-7000-8000-0000000000d3");
    private static readonly DealActor Advisor = DealActor.ForUser(OwnerId);

    private static SalesPipeline Pipeline() => SalesPipeline.CreateDefault(TenantId, Now);

    private static PipelineStage Stage(SalesPipeline pipeline, string name) => pipeline.OrderedStages.Single(stage => stage.Name == name);

    private static Deal NewDeal(SalesPipeline pipeline, decimal? amount = 1_000m, DealSource source = DealSource.Manual, Guid? conversationId = null) =>
        Deal.Create(TenantId, pipeline, CustomerId, OwnerId, "  Dotación sede norte ", amount, "COP", null, source, conversationId, Advisor, Now);

    private static string CodeOf(Action action) => Assert.Throws<PipelineDomainException>(action).Code;

    [Fact]
    public void CreateStartsInTheFirstOpenStageAndRecordsTheFirstHistoryRow()
    {
        var pipeline = Pipeline();

        var deal = NewDeal(pipeline);

        Assert.Equal(("Dotación sede norte", 1_000m, "COP"), (deal.Title, deal.Amount, deal.Currency));
        Assert.Equal((pipeline.FirstOpenStage.Id, Now, 1L, false), (deal.StageId, deal.StageEnteredAt, deal.Version, deal.IsClosed));
        Assert.Equal((Now, Now, Now), (deal.CreatedAt, deal.UpdatedAt, deal.LastChangedAt));
        var row = Assert.Single(deal.PullHistory());
        Assert.Equal(((Guid?)null, pipeline.FirstOpenStage.Id, ActorKind.User, (Guid?)OwnerId), (row.FromStageId, row.ToStageId, row.ActorKind, row.ActorMemberId));
        Assert.Equal(((decimal?)1_000m, "COP", 10, deal.Id, pipeline.Id), (row.Amount, row.Currency, row.Probability, row.DealId, row.PipelineId));
    }

    [Fact]
    public void TheOriginConversationIsKeptOnlyForAConversationDeal()
    {
        var pipeline = Pipeline();
        var conversationId = Guid.CreateVersion7();

        Assert.Equal(conversationId, NewDeal(pipeline, source: DealSource.Conversation, conversationId: conversationId).OriginConversationId);
        Assert.Null(NewDeal(pipeline, source: DealSource.Manual, conversationId: conversationId).OriginConversationId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankTitleIsRefused(string? title) =>
        Assert.Equal(PipelineErrorCodes.DealTitleInvalid, CodeOf(() =>
            Deal.Create(TenantId, Pipeline(), CustomerId, OwnerId, title, null, "COP", null, DealSource.Manual, null, Advisor, Now)));

    [Fact]
    public void ATitleOverTwoHundredCharactersIsRefused() =>
        Assert.Equal(PipelineErrorCodes.DealTitleInvalid, CodeOf(() =>
            Deal.Create(TenantId, Pipeline(), CustomerId, OwnerId, new string('a', 201), null, "COP", null, DealSource.Manual, null, Advisor, Now)));

    [Theory]
    [InlineData("-1")]
    [InlineData("1.001")]
    [InlineData("1000000000000")]
    public void AnInvalidAmountIsRefused(string amount) =>
        Assert.Equal(PipelineErrorCodes.DealAmountInvalid, CodeOf(() => NewDeal(Pipeline(), decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture))));

    // D34: un negocio puede nacer sin monto.
    [Fact]
    public void ADealCanBeBornWithoutAmount() => Assert.Null(NewDeal(Pipeline(), amount: null).Amount);

    // D13: libre entre abiertas, adelante, atrás o saltando.
    [Fact]
    public void MovingIsFreeBetweenOpenStages()
    {
        var pipeline = Pipeline();
        var deal = NewDeal(pipeline);
        deal.PullHistory();

        Assert.True(deal.MoveTo(pipeline, Stage(pipeline, "Negociación").Id, Advisor, Now.AddHours(1)));
        Assert.True(deal.MoveTo(pipeline, Stage(pipeline, "Calificado").Id, Advisor, Now.AddHours(2)));

        var rows = deal.PullHistory();
        Assert.Equal(
            [(Stage(pipeline, "Nuevo").Id, Stage(pipeline, "Negociación").Id), (Stage(pipeline, "Negociación").Id, Stage(pipeline, "Calificado").Id)],
            rows.Select(row => (row.FromStageId!.Value, row.ToStageId)));
        Assert.Equal([75, 25], rows.Select(row => row.Probability));
        Assert.Equal((Stage(pipeline, "Calificado").Id, Now.AddHours(2), 3L), (deal.StageId, deal.StageEnteredAt, deal.Version));
    }

    [Fact]
    public void MovingToTheSameStageChangesNothing()
    {
        var pipeline = Pipeline();
        var deal = NewDeal(pipeline);
        deal.PullHistory();

        Assert.False(deal.MoveTo(pipeline, deal.StageId, Advisor, Now.AddHours(1)));

        Assert.Empty(deal.PullHistory());
        Assert.Equal(1L, deal.Version);
    }

    [Fact]
    public void MovingToAClosingOrUnknownStageIsRefused()
    {
        var pipeline = Pipeline();
        var deal = NewDeal(pipeline);

        Assert.Equal(PipelineErrorCodes.DealStageNotOpen, CodeOf(() => deal.MoveTo(pipeline, pipeline.WonStage.Id, Advisor, Now)));
        Assert.Equal(PipelineErrorCodes.StageNotFound, CodeOf(() => deal.MoveTo(pipeline, Guid.CreateVersion7(), Advisor, Now)));
    }

    // D14: ganar a mano se permite (ventas por Pos o por teléfono).
    [Fact]
    public void WinningClosesTheDealInTheWonStage()
    {
        var pipeline = Pipeline();
        var deal = NewDeal(pipeline);
        deal.PullHistory();

        deal.Win(pipeline, Advisor, Now.AddDays(1));

        Assert.Equal((pipeline.WonStage.Id, (DateTimeOffset?)Now.AddDays(1), true), (deal.StageId, deal.ClosedAt, deal.IsClosed));
        Assert.Equal(100, Assert.Single(deal.PullHistory()).Probability);
    }

    [Fact]
    public void AClosedDealCannotMoveWinOrLose()
    {
        var pipeline = Pipeline();
        var deal = NewDeal(pipeline);
        deal.Win(pipeline, Advisor, Now);

        Assert.Equal(PipelineErrorCodes.DealAlreadyClosed, CodeOf(() => deal.MoveTo(pipeline, Stage(pipeline, "Calificado").Id, Advisor, Now)));
        Assert.Equal(PipelineErrorCodes.DealAlreadyClosed, CodeOf(() => deal.Win(pipeline, Advisor, Now)));
        Assert.Equal(PipelineErrorCodes.DealAlreadyClosed, CodeOf(() => deal.Lose(pipeline, Guid.CreateVersion7(), null, Advisor, Now)));
    }

    // D14 y P10: perder exige motivo; la nota es opcional y se recorta.
    [Fact]
    public void LosingNeedsAReasonAndKeepsTheNote()
    {
        var pipeline = Pipeline();
        var deal = NewDeal(pipeline);
        var reasonId = Guid.CreateVersion7();

        Assert.Equal(PipelineErrorCodes.DealLossReasonRequired, CodeOf(() => deal.Lose(pipeline, null, null, Advisor, Now)));
        Assert.Equal(PipelineErrorCodes.DealLossReasonRequired, CodeOf(() => deal.Lose(pipeline, Guid.Empty, null, Advisor, Now)));
        Assert.Equal(PipelineErrorCodes.DealLossNoteInvalid, CodeOf(() => deal.Lose(pipeline, reasonId, new string('a', 501), Advisor, Now)));
        Assert.False(deal.IsClosed);

        deal.Lose(pipeline, reasonId, "  Se fue con la competencia ", Advisor, Now);

        Assert.Equal((pipeline.LostStage.Id, (Guid?)reasonId, "Se fue con la competencia"), (deal.StageId, deal.LossReasonId, deal.LossNote));
        Assert.True(deal.IsClosed);
    }

    [Fact]
    public void ABlankLossNoteIsStoredAsNull()
    {
        var pipeline = Pipeline();
        var deal = NewDeal(pipeline);

        deal.Lose(pipeline, Guid.CreateVersion7(), "   ", Advisor, Now);

        Assert.Null(deal.LossNote);
    }

    // D15: reabrir vuelve a donde estaba.
    [Fact]
    public void ReopenReturnsToTheLastOpenStageAndClearsTheLoss()
    {
        var pipeline = Pipeline();
        var deal = NewDeal(pipeline);
        var negotiation = Stage(pipeline, "Negociación").Id;
        deal.MoveTo(pipeline, negotiation, Advisor, Now);
        deal.Lose(pipeline, Guid.CreateVersion7(), "nota", Advisor, Now);
        deal.PullHistory();

        deal.Reopen(pipeline, negotiation, DealActor.ForSystem, Now.AddDays(2));

        Assert.Equal((negotiation, (DateTimeOffset?)null, (Guid?)null, (string?)null), (deal.StageId, deal.ClosedAt, deal.LossReasonId, deal.LossNote));
        var row = Assert.Single(deal.PullHistory());
        Assert.Equal((pipeline.LostStage.Id, negotiation, ActorKind.System, (Guid?)null), (row.FromStageId!.Value, row.ToStageId, row.ActorKind, row.ActorMemberId));
    }

    // Review Focus 2: la última abierta pudo borrarse (ya no tenía negocios: éste estaba cerrado).
    [Fact]
    public void ReopenFallsBackToTheFirstOpenStageWhenTheLastOneIsGone()
    {
        var pipeline = Pipeline();
        var deal = NewDeal(pipeline);
        deal.Win(pipeline, Advisor, Now);

        deal.Reopen(pipeline, Guid.CreateVersion7(), Advisor, Now);

        Assert.Equal(pipeline.FirstOpenStage.Id, deal.StageId);
    }

    [Fact]
    public void ReopeningAnOpenDealIsRefused() =>
        Assert.Equal(PipelineErrorCodes.DealNotClosed, CodeOf(() => NewDeal(Pipeline()).Reopen(Pipeline(), null, Advisor, Now)));

    // D17: el avance automático es sólo hacia adelante y nunca toca un cerrado.
    [Fact]
    public void AdvanceToTheQuotationStageGoesOnlyForward()
    {
        var pipeline = Pipeline();
        var fresh = NewDeal(pipeline);
        var ahead = NewDeal(pipeline);
        ahead.MoveTo(pipeline, Stage(pipeline, "Negociación").Id, Advisor, Now);
        var closed = NewDeal(pipeline);
        closed.Win(pipeline, Advisor, Now);

        Assert.True(fresh.AdvanceToQuotationStage(pipeline, DealActor.ForSystem, Now));
        Assert.False(ahead.AdvanceToQuotationStage(pipeline, DealActor.ForSystem, Now));
        Assert.False(closed.AdvanceToQuotationStage(pipeline, DealActor.ForSystem, Now));

        Assert.Equal(pipeline.QuotationStage.Id, fresh.StageId);
        Assert.Equal(Stage(pipeline, "Negociación").Id, ahead.StageId);
    }

    // D18: con principal, monto y moneda son de sólo lectura; el resto se edita.
    [Fact]
    public void WithAPrimaryQuotationTheAmountIsReadOnly()
    {
        var pipeline = Pipeline();
        var deal = NewDeal(pipeline);
        var newOwner = Guid.CreateVersion7();
        deal.SetPrimaryQuotation(Guid.CreateVersion7(), 2_500m, "USD", Now);

        Assert.Equal(PipelineErrorCodes.DealAmountFollowsQuotation, CodeOf(() => deal.Update("Otro", OwnerId, 3_000m, "USD", null, Now)));
        Assert.Equal(PipelineErrorCodes.DealAmountFollowsQuotation, CodeOf(() => deal.Update("Otro", OwnerId, 2_500m, "COP", null, Now)));

        deal.Update(" Otro título ", newOwner, 2_500m, "USD", new DateOnly(2026, 11, 30), Now.AddHours(1));

        Assert.Equal(("Otro título", newOwner, (decimal?)2_500m, "USD"), (deal.Title, deal.OwnerMemberId, deal.Amount, deal.Currency));
        Assert.Equal((new DateOnly(2026, 11, 30), Now.AddHours(1)), (deal.ExpectedCloseDate!.Value, deal.LastChangedAt));
    }

    // D27: sin la principal, el monto queda manual con su último valor.
    [Fact]
    public void FollowingAndClearingThePrimaryKeepsTheLastAmount()
    {
        var pipeline = Pipeline();
        var deal = NewDeal(pipeline, amount: null);
        deal.SetPrimaryQuotation(Guid.CreateVersion7(), 2_500m, "USD", Now);
        var version = deal.Version;

        deal.FollowPrimary(2_500m, "USD", Now);
        Assert.Equal(version, deal.Version);

        deal.FollowPrimary(2_700m, "USD", Now);
        deal.ClearPrimary(Now);

        Assert.Equal(((Guid?)null, (decimal?)2_700m, "USD"), (deal.PrimaryQuotationId, deal.Amount, deal.Currency));
        deal.Update("Manual", OwnerId, 3_000m, "COP", null, Now);
        Assert.Equal((decimal?)3_000m, deal.Amount);
    }

    [Fact]
    public void PullHistoryEmptiesThePendingRows()
    {
        var deal = NewDeal(Pipeline());

        Assert.Single(deal.PullHistory());
        Assert.Empty(deal.PullHistory());
    }
}
```

`tests/Modules/Pipeline/Modules.Pipeline.UnitTests/DealQuotationTests.cs`:

```csharp
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.UnitTests;

/// <summary>Spec 2026-10-13 §4.4: la foto que Pipeline guarda de cada cotización que le llegó por evento.</summary>
public sealed class DealQuotationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 15, 0, 0, TimeSpan.Zero);

    private static DealQuotation Sent() =>
        DealQuotation.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "COT-0001", Guid.CreateVersion7(), 1_000m, "COP", Now);

    [Fact]
    public void ANewSnapshotIsSentAndUnlinked()
    {
        var quotation = Sent();

        Assert.Equal((DealQuotationStatus.Sent, (Guid?)null, "COT-0001", 1_000m), (quotation.Status, quotation.DealId, quotation.QuotationNumber, quotation.Total));
    }

    // Un sent que llega después del converted (reintento, D25) no la devuelve a Sent.
    [Fact]
    public void ConvertedIsStickyAndARefreshUpdatesTheFigures()
    {
        var quotation = Sent();
        var orderId = Guid.CreateVersion7();
        var customerId = Guid.CreateVersion7();

        quotation.Refresh("COT-0001", customerId, 1_200m, "USD", converted: true, orderId, Now.AddHours(1));
        quotation.Refresh("COT-0001", customerId, 1_300m, "USD", converted: false, null, Now.AddHours(2));

        Assert.Equal((DealQuotationStatus.Converted, (Guid?)orderId, customerId), (quotation.Status, quotation.OrderId, quotation.CustomerId));
        Assert.Equal((1_300m, "USD", Now.AddHours(2)), (quotation.Total, quotation.Currency, quotation.UpdatedAt));
    }

    [Fact]
    public void LinkUnlinkAndCancelTheOrder()
    {
        var quotation = Sent();
        var dealId = Guid.CreateVersion7();

        quotation.LinkTo(dealId, Now);
        Assert.Equal((Guid?)dealId, quotation.DealId);

        quotation.Unlink(Now);
        quotation.MarkOrderCancelled(Now.AddDays(1));

        Assert.Null(quotation.DealId);
        Assert.Equal((DateTimeOffset?)Now.AddDays(1), quotation.OrderCancelledAt);
    }
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.UnitTests\Modules.Pipeline.UnitTests.csproj" --filter "FullyQualifiedName~DealTests|FullyQualifiedName~DealQuotationTests"
```

Esperado: `error CS0246` sobre `Deal` y `DealQuotation`.

- [ ] **Step 3: Implementar**

`src/Modules/Pipeline/Modules.Pipeline.Domain/DealStageHistoryEntry.cs`:

```csharp
namespace Modules.Pipeline.Domain;

/// <summary>
/// Spec 2026-10-13 §4.3 y D19: una fila del historial de etapas, append-only. La foto del monto, la moneda y la probabilidad
/// es la del momento: el forecast del slice 2 la necesita así. Sin FK a <c>stages</c>: una etapa borrada no puede borrar historia.
/// </summary>
public sealed class DealStageHistoryEntry
{
    private DealStageHistoryEntry() => Currency = string.Empty;

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid DealId { get; private set; }

    public Guid PipelineId { get; private set; }

    public Guid? FromStageId { get; private set; }

    public Guid ToStageId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public ActorKind ActorKind { get; private set; }

    public Guid? ActorMemberId { get; private set; }

    public decimal? Amount { get; private set; }

    public string Currency { get; private set; }

    public int Probability { get; private set; }

    internal static DealStageHistoryEntry Create(
        Guid tenantId, Guid dealId, Guid pipelineId, Guid? fromStageId, Guid toStageId, DateTimeOffset occurredAt,
        DealActor actor, decimal? amount, string currency, int probability) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            DealId = dealId,
            PipelineId = pipelineId,
            FromStageId = fromStageId,
            ToStageId = toStageId,
            OccurredAt = occurredAt,
            ActorKind = actor.Kind,
            ActorMemberId = actor.MemberId,
            Amount = amount,
            Currency = currency,
            Probability = probability,
        };
}
```

`src/Modules/Pipeline/Modules.Pipeline.Domain/Deal.cs`:

```csharp
namespace Modules.Pipeline.Domain;

/// <summary>
/// Spec 2026-10-13 §4.2, D13–D19: un negocio. Cerrado si y sólo si <see cref="ClosedAt"/> no es nulo (P3 del plan); el tipo de
/// la etapa sigue siendo lo que el dominio valida. Cada cambio de etapa deja una fila en el historial pendiente
/// (<see cref="PullHistory"/>), y la unidad de trabajo la guarda en el mismo SaveChanges que el negocio (P4): ningún camino puede
/// mover la etapa sin su fila. <see cref="Version"/> es el token de concurrencia.
/// </summary>
public sealed class Deal
{
    public const int TitleMaxLength = 200;
    public const int LossNoteMaxLength = 500;

    /// <summary>El mayor importe que cabe en <c>numeric(14,2)</c>, igual que <c>PosLimits.MaxAmount</c>.</summary>
    public const decimal MaxAmount = 999_999_999_999.99m;

    private readonly List<DealStageHistoryEntry> _pendingHistory = [];

    private Deal()
    {
        Title = string.Empty;
        Currency = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid PipelineId { get; private set; }

    public Guid CustomerId { get; private set; }

    public Guid OwnerMemberId { get; private set; }

    public string Title { get; private set; }

    /// <summary>D34: nulo = sin monto todavía. Cuenta en el tablero, no suma.</summary>
    public decimal? Amount { get; private set; }

    public string Currency { get; private set; }

    public DateOnly? ExpectedCloseDate { get; private set; }

    public Guid StageId { get; private set; }

    public DateTimeOffset StageEnteredAt { get; private set; }

    public Guid? PrimaryQuotationId { get; private set; }

    public DealSource Source { get; private set; }

    public Guid? OriginConversationId { get; private set; }

    public Guid? LossReasonId { get; private set; }

    public string? LossNote { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    /// <summary>§5.2: el último cambio del negocio, etapa incluida. Entra en el cálculo de estancado.</summary>
    public DateTimeOffset LastChangedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public bool IsClosed => ClosedAt is not null;

    public static Deal Create(
        Guid tenantId, SalesPipeline pipeline, Guid customerId, Guid ownerMemberId, string? title, decimal? amount, string currency,
        DateOnly? expectedCloseDate, DealSource source, Guid? originConversationId, DealActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        var stage = pipeline.FirstOpenStage;
        var deal = new Deal
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            PipelineId = pipeline.Id,
            CustomerId = customerId,
            OwnerMemberId = ownerMemberId,
            Title = NormalizeTitle(title),
            Amount = ValidateAmount(amount),
            Currency = currency,
            ExpectedCloseDate = expectedCloseDate,
            StageId = stage.Id,
            StageEnteredAt = now,
            Source = source,
            OriginConversationId = source == DealSource.Conversation ? originConversationId : null,
            LastChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
        deal.Record(null, stage, actor, now);
        return deal;
    }

    /// <summary>P5 del plan: reemplaza los campos editables. Con principal (D18), monto y moneda no pueden cambiar.</summary>
    public void Update(string? title, Guid ownerMemberId, decimal? amount, string currency, DateOnly? expectedCloseDate, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        var normalizedTitle = NormalizeTitle(title);
        var validAmount = ValidateAmount(amount);
        if (PrimaryQuotationId is not null && (validAmount != Amount || !string.Equals(currency, Currency, StringComparison.Ordinal)))
        {
            throw new PipelineDomainException(
                PipelineErrorCodes.DealAmountFollowsQuotation, "The amount follows the primary quotation; change the quotation instead.");
        }

        Title = normalizedTitle;
        OwnerMemberId = ownerMemberId;
        Amount = validAmount;
        Currency = currency;
        ExpectedCloseDate = expectedCloseDate;
        Touch(now);
    }

    /// <summary>D13: libre entre abiertas. A una de cierre no: para eso están <see cref="Win"/> y <see cref="Lose"/>.</summary>
    public bool MoveTo(SalesPipeline pipeline, Guid stageId, DealActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        EnsureOpen();
        var target = pipeline.FindStage(stageId)
            ?? throw new PipelineDomainException(PipelineErrorCodes.StageNotFound, "The stage does not belong to this pipeline.");
        if (target.Kind != StageKind.Open)
        {
            throw new PipelineDomainException(PipelineErrorCodes.DealStageNotOpen, "Use win or lose to close a deal.");
        }

        if (target.Id == StageId)
        {
            return false;
        }

        ChangeStage(target, actor, now);
        return true;
    }

    /// <summary>D17: a la etapa de cotización sólo si está antes; nunca hacia atrás ni sobre un cerrado.</summary>
    public bool AdvanceToQuotationStage(SalesPipeline pipeline, DealActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        if (IsClosed)
        {
            return false;
        }

        var target = pipeline.QuotationStage;
        if (pipeline.FindStage(StageId) is { } current && current.Position >= target.Position)
        {
            return false;
        }

        ChangeStage(target, actor, now);
        return true;
    }

    public void Win(SalesPipeline pipeline, DealActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        EnsureOpen();
        ChangeStage(pipeline.WonStage, actor, now);
        ClosedAt = now;
    }

    /// <summary>D14 y P10: el motivo es obligatorio (que sea del catálogo activo lo revisa el handler); la nota, opcional.</summary>
    public void Lose(SalesPipeline pipeline, Guid? lossReasonId, string? note, DealActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        EnsureOpen();
        if (lossReasonId is not { } reasonId || reasonId == Guid.Empty)
        {
            throw new PipelineDomainException(PipelineErrorCodes.DealLossReasonRequired, "Losing a deal needs a reason from the catalog.");
        }

        var normalizedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (normalizedNote is { Length: > LossNoteMaxLength })
        {
            throw new PipelineDomainException(PipelineErrorCodes.DealLossNoteInvalid, $"The note takes at most {LossNoteMaxLength} characters.");
        }

        ChangeStage(pipeline.LostStage, actor, now);
        ClosedAt = now;
        LossReasonId = reasonId;
        LossNote = normalizedNote;
    }

    /// <summary>D15: a la última etapa abierta del historial (<paramref name="lastOpenStageId"/>, la que el repositorio lee de la
    /// fila de cierre); si ya no existe o no es abierta, a la primera abierta.</summary>
    public void Reopen(SalesPipeline pipeline, Guid? lastOpenStageId, DealActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        if (!IsClosed)
        {
            throw new PipelineDomainException(PipelineErrorCodes.DealNotClosed, "Only a won or lost deal can be reopened.");
        }

        var target = lastOpenStageId is { } id && pipeline.FindStage(id) is { Kind: StageKind.Open } last
            ? last
            : pipeline.FirstOpenStage;
        ChangeStage(target, actor, now);
        ClosedAt = null;
        LossReasonId = null;
        LossNote = null;
    }

    /// <summary>D17 y D18: la cotización pasa a ser la principal y el monto la sigue.</summary>
    public void SetPrimaryQuotation(Guid quotationId, decimal total, string currency, DateTimeOffset now)
    {
        PrimaryQuotationId = quotationId;
        Amount = total;
        Currency = currency;
        Touch(now);
    }

    /// <summary>D18 y D26: el monto se refresca con el reenvío y la conversión de la principal. Sin cambio, nada.</summary>
    public void FollowPrimary(decimal total, string currency, DateTimeOffset now)
    {
        if (Amount == total && string.Equals(Currency, currency, StringComparison.Ordinal))
        {
            return;
        }

        Amount = total;
        Currency = currency;
        Touch(now);
    }

    /// <summary>D27: sin principal, el monto queda manual con su último valor.</summary>
    public void ClearPrimary(DateTimeOffset now)
    {
        if (PrimaryQuotationId is null)
        {
            return;
        }

        PrimaryQuotationId = null;
        Touch(now);
    }

    /// <summary>P4 del plan: las filas que la unidad de trabajo guarda en el mismo SaveChanges.</summary>
    public IReadOnlyList<DealStageHistoryEntry> PullHistory()
    {
        var pending = _pendingHistory.ToArray();
        _pendingHistory.Clear();
        return pending;
    }

    private void ChangeStage(PipelineStage target, DealActor actor, DateTimeOffset now)
    {
        var from = StageId;
        StageId = target.Id;
        StageEnteredAt = now;
        Touch(now);
        Record(from, target, actor, now);
    }

    private void Record(Guid? fromStageId, PipelineStage to, DealActor actor, DateTimeOffset now) =>
        _pendingHistory.Add(DealStageHistoryEntry.Create(
            TenantId, Id, PipelineId, fromStageId, to.Id, now, actor, Amount, Currency, to.Probability));

    private void EnsureOpen()
    {
        if (IsClosed)
        {
            throw new PipelineDomainException(PipelineErrorCodes.DealAlreadyClosed, "The deal is already closed; reopen it first.");
        }
    }

    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
        LastChangedAt = now;
    }

    private static string NormalizeTitle(string? title)
    {
        var normalized = (title ?? string.Empty).Trim();
        return normalized.Length is > 0 and <= TitleMaxLength
            ? normalized
            : throw new PipelineDomainException(PipelineErrorCodes.DealTitleInvalid, $"The title takes 1 to {TitleMaxLength} characters.");
    }

    private static decimal? ValidateAmount(decimal? amount) =>
        amount is not { } value || (value >= 0 && value <= MaxAmount && value == Math.Round(value, 2))
            ? amount
            : throw new PipelineDomainException(
                PipelineErrorCodes.DealAmountInvalid, "The amount goes from 0 to 999999999999.99 with at most 2 decimals.");
}
```

`src/Modules/Pipeline/Modules.Pipeline.Domain/DealQuotation.cs`:

```csharp
namespace Modules.Pipeline.Domain;

/// <summary>
/// Spec 2026-10-13 §4.4: la foto que Pipeline guarda de cada cotización que le llegó por evento. Una cotización pertenece a lo
/// sumo a un negocio (PK <c>(tenant_id, quotation_id)</c>). Con ella el tablero muestra el número de la principal sin leer
/// Quotations. <see cref="DealQuotationStatus.Converted"/> no vuelve a <see cref="DealQuotationStatus.Sent"/>: un sent que llega
/// tarde (reintento) no deshace la conversión.
/// </summary>
public sealed class DealQuotation
{
    public const int NumberMaxLength = 40;

    private DealQuotation()
    {
        QuotationNumber = string.Empty;
        Currency = string.Empty;
    }

    public Guid TenantId { get; private set; }

    public Guid QuotationId { get; private set; }

    public Guid? DealId { get; private set; }

    public Guid CustomerId { get; private set; }

    public string QuotationNumber { get; private set; }

    public decimal Total { get; private set; }

    public string Currency { get; private set; }

    public DealQuotationStatus Status { get; private set; }

    public Guid? OrderId { get; private set; }

    public DateTimeOffset? OrderCancelledAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static DealQuotation Create(
        Guid tenantId, Guid quotationId, string quotationNumber, Guid customerId, decimal total, string currency, DateTimeOffset now) =>
        new()
        {
            TenantId = tenantId,
            QuotationId = quotationId,
            QuotationNumber = quotationNumber,
            CustomerId = customerId,
            Total = total,
            Currency = currency,
            Status = DealQuotationStatus.Sent,
            UpdatedAt = now,
        };

    public void Refresh(
        string quotationNumber, Guid customerId, decimal total, string currency, bool converted, Guid? orderId, DateTimeOffset now)
    {
        QuotationNumber = quotationNumber;
        CustomerId = customerId;
        Total = total;
        Currency = currency;
        if (converted)
        {
            Status = DealQuotationStatus.Converted;
            OrderId = orderId;
            OrderCancelledAt = null;
        }

        UpdatedAt = now;
    }

    public void LinkTo(Guid dealId, DateTimeOffset now)
    {
        DealId = dealId;
        UpdatedAt = now;
    }

    public void Unlink(DateTimeOffset now)
    {
        DealId = null;
        UpdatedAt = now;
    }

    public void MarkOrderCancelled(DateTimeOffset now)
    {
        OrderCancelledAt = now;
        UpdatedAt = now;
    }
}
```

- [ ] **Step 4: Correr y verificar que pasan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.UnitTests\Modules.Pipeline.UnitTests.csproj" --no-build --filter "FullyQualifiedName~DealTests|FullyQualifiedName~DealQuotationTests"
```

Esperado: todo verde.

- [ ] **Step 5: Commit**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
if ((git -C $B branch --show-current) -ne "feature/embudo-de-ventas") { throw "rama equivocada" }
git -C $B status --short
git -C $B add src/Modules/Pipeline/Modules.Pipeline.Domain/Deal.cs src/Modules/Pipeline/Modules.Pipeline.Domain/DealStageHistoryEntry.cs src/Modules/Pipeline/Modules.Pipeline.Domain/DealQuotation.cs tests/Modules/Pipeline/Modules.Pipeline.UnitTests/DealTests.cs tests/Modules/Pipeline/Modules.Pipeline.UnitTests/DealQuotationTests.cs
git -C $B commit -m "feat(pipeline): negocio con transiciones, historial de etapas y foto de la cotización"
git -C $B log -1 --format=%B
```

---

### Task 6: Persistencia — puertos, `PipelineDbContext`, migración `InitialPipeline`, unidad de trabajo, embudo en el primer acceso y harness

**Files:**
- Create: `src/Modules/Pipeline/Modules.Pipeline.Application/PipelinePorts.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Infrastructure/Persistence/PipelineDbContext.cs`, `PipelineDbContextFactory.cs`, `PipelineInbox.cs`, `PipelineUnitOfWork.cs`, `PipelineRepositories.cs`, `PipelineAuditRecorder.cs`, `PipelineProvisioner.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Infrastructure/PipelineDatabaseInitializer.cs`
- Modify: `src/Modules/Pipeline/Modules.Pipeline.Infrastructure/PipelineInfrastructureExtensions.cs`
- Modify: `src/Api/Program.cs` (`using Modules.Pipeline.Infrastructure;` y la migración después de la de Messaging, `:229`)
- Create (generados): `src/Modules/Pipeline/Modules.Pipeline.Infrastructure/Persistence/Migrations/<ts>_InitialPipeline.cs`, `.Designer.cs`, `PipelineDbContextModelSnapshot.cs`
- Create: `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/PipelineApiHarness.cs`
- Test: `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/PipelinePersistenceTests.cs`

**Interfaces:**
- Consumes: el dominio de las Tasks 4 y 5; `AuditEntry.Create`, `AuditActorType`, `AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false)`; `IClock`; `RequestConcurrencyException`.
- Produces (Application, `PipelinePorts.cs`):
  - `IPipelineProvisioner.EnsureAsync(Guid tenantId, CancellationToken) → Task<SalesPipeline>` (con etapas, rastreado; crea el embudo y los dos catálogos la primera vez).
  - `IDealRepository`: `FindAsync(Guid tenantId, Guid dealId, ct) → Task<Deal?>` (tracking), `ListOpenByCustomerAsync(Guid tenantId, Guid customerId, ct) → Task<IReadOnlyList<Deal>>` (tracking, `last_changed_at` desc), `FindLastClosedByCustomerAsync(Guid tenantId, Guid customerId, ct) → Task<Deal?>`, `CustomersWithOpenDealsAsync(Guid tenantId, IReadOnlyCollection<Guid> customerIds, ct) → Task<IReadOnlySet<Guid>>`, `StagesWithDealsAsync(Guid tenantId, Guid pipelineId, ct) → Task<IReadOnlySet<Guid>>`, `FindLastOpenStageIdAsync(Guid tenantId, Guid dealId, ct) → Task<Guid?>`, `ListHistoryAsync(Guid tenantId, Guid dealId, ct) → Task<IReadOnlyList<DealStageHistoryEntry>>`, `void Add(Deal)`.
  - `IDealQuotationRepository`: `FindAsync(Guid tenantId, Guid quotationId, ct) → Task<DealQuotation?>` (tracking), `ListByDealAsync(Guid tenantId, Guid dealId, ct)`, `ListUnlinkedByCustomerAsync(Guid tenantId, Guid customerId, ct)` (las dos → `Task<IReadOnlyList<DealQuotation>>`), `void Add(DealQuotation)`.
  - `IPipelineReasonRepository`: `ListAsync(Guid tenantId, ReasonKind kind, ct) → Task<IReadOnlyList<PipelineReason>>` (tracking, activos e inactivos), `FindActiveAsync(Guid tenantId, ReasonKind kind, Guid reasonId, ct) → Task<PipelineReason?>`, `FindNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> reasonIds, ct) → Task<IReadOnlyDictionary<Guid, string>>`, `void Add(PipelineReason)`.
  - `ITriageDecisionRepository`: `FindLatestAsync(Guid tenantId, IReadOnlyCollection<Guid> conversationIds, ct) → Task<IReadOnlyDictionary<Guid, TriageDecision>>`, `void Add(TriageDecision)`.
  - `IPipelineUnitOfWork.SaveChangesAsync(ct) → Task<int>` (vacía el historial pendiente; versión vieja → `RequestConcurrencyException("concurrency.conflict")`; FK de etapa → `pipeline.stage.has_deals`; índice de triage de D38 → `pipeline.triage.already_decided`).
  - `IPipelineAuditRecorder.Record(Guid tenantId, Guid actorId, bool bySystem, string action, string resourceType, Guid resourceId, DateTimeOffset occurredAt)` y `PipelineAuditActions` (constantes abajo).
- Produces (Infrastructure): `PipelineDbContext` (público, `Schema = "pipeline"`, `DbSet`s `Pipelines`, `Stages`, `Deals`, `History`, `DealQuotations`, `Reasons`, `TriageDecisions`; internos `Inbox`, `Outbox`, `AuditEntries`), `PipelineInboxMessage`, `PipelineOutboxRecord`, `PipelineInboxClaims.TryClaimAsync` (los usa la Task 11), `InitializePipelineDatabaseAsync`.
- Produces (pruebas): `PipelineApiHarness` con `StartDatabaseAsync`, `QepApiFactory`, `CreateClient`, `RegisterTenantAsync`, `OwnerMembershipIdAsync`, `SeedMemberAsync`, `EnablePipelineAsync`, `EnableMessagingAsync`, `DisableModuleAsync`, `SeedCustomerAsync`, `SeedConversationAsync`, `ProblemAsync`, `CountAsync`, `ScalarAsync`, `ExecuteAsync`, `PipelineUrl`, `ReadPermissions`, `ManagePermissions`, `AllPermissions`.

- [ ] **Step 1: Escribir el harness y las pruebas que fallan**

`tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/PipelineApiHarness.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using Modules.Pipeline.Application;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>
/// Lo que comparten las pruebas de Pipeline (spec 2026-10-13), copia de <c>MessagingApiHarness</c>: contenedor compartido,
/// plantilla migrada, una base por prueba y un host que fija sus claves con <c>UseSetting</c>; nunca las hereda de los
/// user-secrets de quien corre las pruebas (memoria «user-secrets rompen las pruebas de integración»). Clientes y
/// conversaciones se siembran por los servicios del host (Customers por <c>ICustomerWhatsAppDirectory</c>, Messaging por su
/// <c>DbContext</c>): Pipeline los lee por adaptadores, nunca por su API.
/// </summary>
internal static class PipelineApiHarness
{
    public static readonly string[] ReadPermissions = [PipelinePermissions.DealRead];

    public static readonly string[] ManagePermissions = [PipelinePermissions.DealRead, PipelinePermissions.DealManage];

    public static readonly string[] AllPermissions =
        [PipelinePermissions.DealRead, PipelinePermissions.DealManage, PipelinePermissions.SettingsManage];

    private const string TemplateDatabase = "qep_template_pipeline";

    private static readonly Lazy<Task<PostgreSqlContainer>> SharedServer = new(StartServerAsync);

    // Postgres rechaza dos CREATE DATABASE simultáneos desde la misma plantilla: los clones van de a uno.
    private static readonly SemaphoreSlim CloneGate = new(1, 1);

    public static string PipelineUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/pipeline";

    /// <summary>Una base limpia y migrada para esta prueba. Se borra en <c>DisposeAsync</c>.</summary>
    public static async Task<TestDatabase> StartDatabaseAsync()
    {
        var server = await SharedServer.Value;
        var name = $"t_{Guid.CreateVersion7():N}";
        await CloneGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            await ExecuteAdminAsync(server, $"CREATE DATABASE \"{name}\" TEMPLATE \"{TemplateDatabase}\"");
        }
        finally
        {
            CloneGate.Release();
        }

        return new TestDatabase(server, name);
    }

    private static async Task<PostgreSqlContainer> StartServerAsync()
    {
        var server = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .WithCommand("-c", "max_connections=400")
            .Build();
        await server.StartAsync(CancellationToken.None);
        await ExecuteAdminAsync(server, $"CREATE DATABASE \"{TemplateDatabase}\"");

        // El host migra al arrancar: arrancarlo una vez contra la plantilla la deja con todo.
        using (var factory = new QepApiFactory(ConnectionStringFor(server, TemplateDatabase)))
        using (factory.CreateClient())
        {
        }

        // Una conexión viva a la plantilla haría fallar todos los clones.
        NpgsqlConnection.ClearAllPools();
        await ExecuteAdminAsync(server, $"ALTER DATABASE \"{TemplateDatabase}\" WITH ALLOW_CONNECTIONS false");
        return server;
    }

    private static string ConnectionStringFor(PostgreSqlContainer server, string database) =>
        new NpgsqlConnectionStringBuilder(server.GetConnectionString()) { Database = database }.ConnectionString;

    private static async Task ExecuteAdminAsync(PostgreSqlContainer server, string sql)
    {
        await using var connection = new NpgsqlConnection(server.GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>La base de una prueba.</summary>
    public sealed class TestDatabase(PostgreSqlContainer server, string name) : IAsyncDisposable
    {
        public string GetConnectionString() => ConnectionStringFor(server, name);

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearPool(new NpgsqlConnection(GetConnectionString()));
            await ExecuteAdminAsync(server, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        }
    }

    public sealed class QepApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 40 }.ConnectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijados, nunca heredados (mismo criterio que MessagingApiHarness y QuotationsApiHarness).
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.UseSetting("Seed:ExportLoad:Quotations", "0");
            builder.UseSetting("Quotations:WhatsApp:ApiToken", string.Empty);
            builder.UseSetting("Quotations:WhatsApp:FromNumber", string.Empty);
            builder.UseSetting("Quotations:WhatsApp:TemplateId", string.Empty);
            builder.UseSetting("Entitlements:GrantDefaultModulesOnSignup", "true");
            // Los workers de Messaging no corren solos durante una prueba de Pipeline.
            builder.UseSetting("Messaging:Workers:DeliveryPollSeconds", "3600");
            builder.UseSetting("Messaging:Workers:MediaPollSeconds", "3600");
            builder.UseSetting("Messaging:Workers:CampaignPollSeconds", "3600");
            builder.UseSetting("Messaging:Workers:PurgeIntervalHours", "24");
        }
    }

    internal sealed record RegisteredTenant(Guid TenantId, Guid OwnerUserId);

    public static HttpClient CreateClient(WebApplicationFactory<Program> factory, Guid subjectId, Guid tenantId, params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId.ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId.ToString());
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        }

        return client;
    }

    /// <summary>Un tenant real, con el dueño y su membresía activa: los handlers resuelven la membresía de quien llama.</summary>
    public static async Task<RegisteredTenant> RegisterTenantAsync(WebApplicationFactory<Program> factory)
    {
        var email = $"owner-{Guid.CreateVersion7():N}@example.com";
        using var bootstrap = CreateClient(factory, Guid.CreateVersion7(), Guid.CreateVersion7());
        bootstrap.DefaultRequestHeaders.Add("X-Email", email);
        bootstrap.DefaultRequestHeaders.Add("X-Email-Verified", "true");
        var response = await bootstrap.PostAsJsonAsync(
            "/api/v1/auth/register-tenant",
            new
            {
                displayName = "Pipeline Test Org",
                slug = $"org-{Guid.NewGuid():N}"[..12],
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "yyyy-MM-dd",
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var registered = await response.Content.ReadFromJsonAsync<RegisterTenantResponseDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(registered);
        return new RegisteredTenant(registered.TenantId, registered.OwnerUserId);
    }

    public static Task<Guid> OwnerMembershipIdAsync(string connectionString, RegisteredTenant tenant) =>
        ScalarAsync<Guid>(
            connectionString,
            "SELECT id FROM tenancy.memberships WHERE tenant_id = @tenantId AND user_id = @userId",
            ("tenantId", tenant.TenantId),
            ("userId", tenant.OwnerUserId));

    /// <summary>Una membresía más del tenant, escrita directo (sin invitación), como <c>MessagingApiHarness.SeedMemberAsync</c>.</summary>
    public static async Task<(Guid MembershipId, Guid UserId)> SeedMemberAsync(string connectionString, Guid tenantId, string displayName, string role = "advisor")
    {
        var membershipId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        await ExecuteAsync(connectionString,
            """
            INSERT INTO tenancy.memberships (id, user_id, tenant_id, state, roles, origin, invited_at, accepted_at, expires_at, version, created_at, updated_at, display_name)
            VALUES (@id, @userId, @tenantId, 'Active', ARRAY[@role]::text[], 'invitation', now(), now(), now() + interval '3 days', 1, now(), now(), @name)
            """,
            ("id", membershipId), ("userId", userId), ("tenantId", tenantId), ("role", role), ("name", displayName));
        return (membershipId, userId);
    }

    /// <summary>D3: <c>pipeline</c> no viene con el signup; se prende con su fila, como <c>EnableMessagingAsync</c>.</summary>
    public static Task<int> EnablePipelineAsync(string connectionString, Guid tenantId) => EnableModuleAsync(connectionString, tenantId, "pipeline");

    public static Task<int> EnableMessagingAsync(string connectionString, Guid tenantId) => EnableModuleAsync(connectionString, tenantId, "messaging");

    public static Task<int> DisableModuleAsync(string connectionString, Guid tenantId, string moduleKey) =>
        ExecuteAsync(connectionString,
            "UPDATE tenancy.tenant_modules SET status = 'inactive', status_changed_at = now() WHERE tenant_id = @tenantId AND module_key = @key",
            ("tenantId", tenantId), ("key", moduleKey));

    private static Task<int> EnableModuleAsync(string connectionString, Guid tenantId, string moduleKey) =>
        ExecuteAsync(connectionString,
            """
            INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
            VALUES (@tenantId, @key, now(), 'manual')
            ON CONFLICT (tenant_id, module_key) DO UPDATE SET status = 'active', status_changed_at = now()
            """,
            ("tenantId", tenantId), ("key", moduleKey));

    /// <summary>Un cliente <c>Incomplete</c> como lo crea la ingesta de WhatsApp (H18), con el nombre que Customers le dio.</summary>
    public static async Task<(Guid Id, string Name)> SeedCustomerAsync(WebApplicationFactory<Program> factory, Guid tenantId, string profileName)
    {
        using var scope = factory.Services.CreateScope();
        var directory = scope.ServiceProvider.GetRequiredService<ICustomerWhatsAppDirectory>();
        var ensured = await directory.EnsureAsync(
            tenantId, new WhatsAppContact($"CO.{Guid.NewGuid():N}", null, profileName, null), TestContext.Current.CancellationToken);
        var refs = await directory.FindRefsAsync(tenantId, [ensured.CustomerId], TestContext.Current.CancellationToken);
        return (ensured.CustomerId, refs[ensured.CustomerId].Name);
    }

    /// <summary>Una conversación con BSUID, atada al cliente si se pasa, con sus marcas de tiempo como las dejaría la ingesta.
    /// La conexión es un id suelto: Messaging no tiene FK a Integrations.</summary>
    public static async Task<Guid> SeedConversationAsync(
        WebApplicationFactory<Program> factory, string connectionString, Guid tenantId, Guid? customerId,
        DateTimeOffset? lastInboundAt = null, DateTimeOffset? lastMessageAt = null, bool resolved = false)
    {
        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var conversation = Conversation.StartWithUserId(
                Guid.CreateVersion7(), tenantId, Guid.CreateVersion7(), $"CO.{Guid.NewGuid():N}", null, "Laura", DateTimeOffset.UtcNow);
            dbContext.Conversations.Add(conversation);
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            id = conversation.Id;
        }

        await ExecuteAsync(connectionString,
            """
            UPDATE messaging.conversations
            SET customer_id = @customerId, last_inbound_at = @inbound, last_message_at = @message, status = @status
            WHERE id = @id
            """,
            ("customerId", (object?)customerId ?? DBNull.Value),
            ("inbound", (object?)lastInboundAt?.ToUniversalTime() ?? DBNull.Value),
            ("message", (object?)lastMessageAt?.ToUniversalTime() ?? DBNull.Value),
            ("status", resolved ? "Resolved" : "Open"),
            ("id", id));
        return id;
    }

    /// <summary>El <c>code</c> y las claves de <c>errors</c> de un ProblemDetails.</summary>
    public static async Task<(string? Code, string[] ErrorKeys)> ProblemAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = document.RootElement;
        var code = root.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : null;
        var keys = root.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        return (code, keys);
    }

    public static Task<long> CountAsync(string connectionString, string sql, params (string Name, object Value)[] parameters) =>
        ScalarAsync<long>(connectionString, sql, parameters);

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public static async Task<int> ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private sealed record RegisterTenantResponseDto(Guid TenantId, Guid OwnerUserId);
}
```

`tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/PipelinePersistenceTests.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;
using static Modules.Pipeline.IntegrationTests.PipelineApiHarness;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>Spec 2026-10-13 §4, D4 y P4 del plan contra Postgres: el embudo se crea una sola vez aunque lleguen dos pedidos a la
/// vez; el historial se guarda con el negocio; la versión vieja es concurrency.conflict; nada cruza tenants.</summary>
public sealed class PipelinePersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Deal> SaveDealAsync(QepApiFactory factory, Guid tenantId, Guid customerId)
    {
        using var scope = factory.Services.CreateScope();
        var pipeline = await scope.ServiceProvider.GetRequiredService<IPipelineProvisioner>().EnsureAsync(tenantId, Ct);
        var deal = Deal.Create(tenantId, pipeline, customerId, Guid.CreateVersion7(), "Dotación", 1_000m, "COP", null,
            DealSource.Manual, null, DealActor.ForSystem, DateTimeOffset.UtcNow);
        scope.ServiceProvider.GetRequiredService<IDealRepository>().Add(deal);
        await scope.ServiceProvider.GetRequiredService<IPipelineUnitOfWork>().SaveChangesAsync(Ct);
        return deal;
    }

    [Fact]
    public async Task TheTenantIndexIsUniqueAndHasTheNameTheProvisionerReliesOn()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        _ = factory.Services;

        var definition = await ScalarAsync<string>(connectionString,
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'pipeline' AND indexname = 'IX_pipelines_tenant'");

        Assert.Contains("UNIQUE", definition, StringComparison.Ordinal);
        Assert.Contains("(tenant_id)", definition, StringComparison.Ordinal);
    }

    // D4: dos peticiones a la vez en el primer acceso no dejan dos embudos ni semillas duplicadas.
    [Fact]
    public async Task ProvisioningSeedsOnceEvenWithTwoConcurrentCalls()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();

        async Task<Guid> EnsureAsync()
        {
            using var scope = factory.Services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<IPipelineProvisioner>().EnsureAsync(tenantId, Ct)).Id;
        }

        var ids = await Task.WhenAll(EnsureAsync(), EnsureAsync(), EnsureAsync());

        Assert.Single(ids.Distinct());
        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.pipelines WHERE tenant_id = @t", ("t", tenantId)));
        Assert.Equal(6L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.stages WHERE pipeline_id = @p", ("p", ids[0])));
        Assert.Equal(5L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.reasons WHERE tenant_id = @t AND kind = 'Loss'", ("t", tenantId)));
        Assert.Equal(5L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.reasons WHERE tenant_id = @t AND kind = 'Dismiss'", ("t", tenantId)));
    }

    // P4: la fila de historial viaja en el mismo SaveChanges que el negocio, y la última abierta sale de la fila de cierre.
    [Fact]
    public async Task EveryStageChangeIsSavedWithItsHistoryRow()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var saved = await SaveDealAsync(factory, tenantId, Guid.CreateVersion7());

        Guid negotiation;
        using (var scope = factory.Services.CreateScope())
        {
            var pipeline = await scope.ServiceProvider.GetRequiredService<IPipelineProvisioner>().EnsureAsync(tenantId, Ct);
            negotiation = pipeline.OrderedStages.Single(stage => stage.Name == "Negociación").Id;
            var deal = await scope.ServiceProvider.GetRequiredService<IDealRepository>().FindAsync(tenantId, saved.Id, Ct);
            Assert.NotNull(deal);
            deal.MoveTo(pipeline, negotiation, DealActor.ForSystem, DateTimeOffset.UtcNow);
            deal.Lose(pipeline, Guid.CreateVersion7(), null, DealActor.ForSystem, DateTimeOffset.UtcNow);
            await scope.ServiceProvider.GetRequiredService<IPipelineUnitOfWork>().SaveChangesAsync(Ct);
        }

        using var reader = factory.Services.CreateScope();
        var deals = reader.ServiceProvider.GetRequiredService<IDealRepository>();
        var history = await deals.ListHistoryAsync(tenantId, saved.Id, Ct);
        Assert.Equal(3, history.Count);
        Assert.Null(history[0].FromStageId);
        Assert.Equal(negotiation, history[1].ToStageId);
        Assert.Equal(negotiation, await deals.FindLastOpenStageIdAsync(tenantId, saved.Id, Ct));
        Assert.Equal(3L, await ScalarAsync<long>(connectionString, "SELECT version FROM pipeline.deals WHERE id = @id", ("id", saved.Id)));
    }

    // Review Focus 3: otro proceso (el worker) cambió el negocio entre la lectura y el commit.
    [Fact]
    public async Task AStaleVersionIsAConcurrencyConflictAndWritesNoHistory()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var saved = await SaveDealAsync(factory, tenantId, Guid.CreateVersion7());

        using var scope = factory.Services.CreateScope();
        var pipeline = await scope.ServiceProvider.GetRequiredService<IPipelineProvisioner>().EnsureAsync(tenantId, Ct);
        var deal = await scope.ServiceProvider.GetRequiredService<IDealRepository>().FindAsync(tenantId, saved.Id, Ct);
        Assert.NotNull(deal);
        await ExecuteAsync(connectionString, "UPDATE pipeline.deals SET version = version + 1 WHERE id = @id", ("id", saved.Id));
        deal.Win(pipeline, DealActor.ForSystem, DateTimeOffset.UtcNow);

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            scope.ServiceProvider.GetRequiredService<IPipelineUnitOfWork>().SaveChangesAsync(Ct));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.deal_stage_history WHERE deal_id = @id", ("id", saved.Id)));
    }

    // Spec D38: dos asesores deciden la misma conversación contra el mismo entrante. El segundo SaveChanges choca con el índice
    // único, sale pipeline.triage.already_decided y su negocio no queda; un entrante nuevo es otro instante y se puede decidir.
    [Fact]
    public async Task ASecondDecisionAgainstTheSameInboundIsAlreadyDecidedAndLeavesOneDeal()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var customerId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        // Con microsegundos, como los guarda Postgres: el instante tiene que volver idéntico de la base.
        var inbound = new DateTimeOffset(2026, 10, 13, 15, 0, 0, TimeSpan.Zero).AddTicks(1_234_560);

        async Task DecideAsync(DateTimeOffset lastInboundAt)
        {
            using var scope = factory.Services.CreateScope();
            var pipeline = await scope.ServiceProvider.GetRequiredService<IPipelineProvisioner>().EnsureAsync(tenantId, Ct);
            var deal = Deal.Create(tenantId, pipeline, customerId, Guid.CreateVersion7(), "Laura", null, "COP", null,
                DealSource.Conversation, conversationId, DealActor.ForSystem, DateTimeOffset.UtcNow);
            scope.ServiceProvider.GetRequiredService<IDealRepository>().Add(deal);
            scope.ServiceProvider.GetRequiredService<ITriageDecisionRepository>().Add(
                TriageDecision.Created(tenantId, conversationId, customerId, deal.Id, lastInboundAt, DealActor.ForSystem, DateTimeOffset.UtcNow));
            await scope.ServiceProvider.GetRequiredService<IPipelineUnitOfWork>().SaveChangesAsync(Ct);
        }

        await DecideAsync(inbound);
        var error = await Assert.ThrowsAsync<PipelineDomainException>(() => DecideAsync(inbound));
        var dealsAfterRace = await CountAsync(connectionString, "SELECT count(*) FROM pipeline.deals WHERE tenant_id = @t", ("t", tenantId));
        await DecideAsync(inbound.AddMinutes(10));

        Assert.Equal(PipelineErrorCodes.TriageAlreadyDecided, error.Code);
        Assert.Equal(1L, dealsAfterRace);
        Assert.Equal(2L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.triage_decisions WHERE tenant_id = @t", ("t", tenantId)));
        Assert.Equal(2L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.deals WHERE tenant_id = @t", ("t", tenantId)));
    }

    [Fact]
    public async Task RepositoriesNeverCrossTenants()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantA = Guid.CreateVersion7();
        var tenantB = Guid.CreateVersion7();
        var customerId = Guid.CreateVersion7();
        var saved = await SaveDealAsync(factory, tenantA, customerId);

        using var scope = factory.Services.CreateScope();
        var deals = scope.ServiceProvider.GetRequiredService<IDealRepository>();

        Assert.Null(await deals.FindAsync(tenantB, saved.Id, Ct));
        Assert.Empty(await deals.ListOpenByCustomerAsync(tenantB, customerId, Ct));
        Assert.Empty(await deals.CustomersWithOpenDealsAsync(tenantB, [customerId], Ct));
        Assert.Equal([customerId], await deals.CustomersWithOpenDealsAsync(tenantA, [customerId, Guid.CreateVersion7()], Ct));
        Assert.Equal([saved.StageId], await deals.StagesWithDealsAsync(tenantA, saved.PipelineId, Ct));
        Assert.Empty(await deals.ListHistoryAsync(tenantB, saved.Id, Ct));
    }
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.IntegrationTests\Modules.Pipeline.IntegrationTests.csproj" --filter "FullyQualifiedName~PipelinePersistenceTests"
```

Esperado: `error CS0246` sobre `IPipelineProvisioner`, `IDealRepository`, `ITriageDecisionRepository`, `IPipelineUnitOfWork`.

- [ ] **Step 3: Implementar los puertos**

`src/Modules/Pipeline/Modules.Pipeline.Application/PipelinePorts.cs`:

```csharp
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.Application;

/// <summary>Spec 2026-10-13 D4: el embudo del tenant, creado con sus semillas (D31) en el primer acceso. Idempotente y seguro con
/// dos peticiones a la vez (índice único por tenant). Devuelve el agregado con sus etapas, rastreado.</summary>
public interface IPipelineProvisioner
{
    Task<SalesPipeline> EnsureAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Todo método recibe <c>tenantId</c>: un id de otro tenant responde igual que uno inexistente.</summary>
public interface IDealRepository
{
    /// <summary>Con tracking.</summary>
    Task<Deal?> FindAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken);

    /// <summary>Los abiertos del cliente, con tracking, el último cambiado primero.</summary>
    Task<IReadOnlyList<Deal>> ListOpenByCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    /// <summary>El cerrado más reciente del cliente (contexto de la ficha, D36). Sin tracking.</summary>
    Task<Deal?> FindLastClosedByCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    /// <summary>De los clientes pedidos, los que tienen al menos un negocio abierto.</summary>
    Task<IReadOnlySet<Guid>> CustomersWithOpenDealsAsync(Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    /// <summary>D12: las etapas con al menos un negocio, abierto o cerrado. Ésas no se borran.</summary>
    Task<IReadOnlySet<Guid>> StagesWithDealsAsync(Guid tenantId, Guid pipelineId, CancellationToken cancellationToken);

    /// <summary>D15: la etapa de origen de la fila más reciente del historial, que en un negocio cerrado es la de cierre.</summary>
    Task<Guid?> FindLastOpenStageIdAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken);

    /// <summary>El historial del negocio, en el orden en que se escribió.</summary>
    Task<IReadOnlyList<DealStageHistoryEntry>> ListHistoryAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken);

    void Add(Deal deal);
}

/// <summary>§4.4: la foto de las cotizaciones que llegaron por evento.</summary>
public interface IDealQuotationRepository
{
    /// <summary>Con tracking.</summary>
    Task<DealQuotation?> FindAsync(Guid tenantId, Guid quotationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DealQuotation>> ListByDealAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken);

    /// <summary>Las del cliente sin negocio, para el <c>link</c> del detalle.</summary>
    Task<IReadOnlyList<DealQuotation>> ListUnlinkedByCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    void Add(DealQuotation quotation);
}

public interface IPipelineReasonRepository
{
    /// <summary>Activos e inactivos, con tracking: el reemplazo del catálogo (P8) los necesita a todos.</summary>
    Task<IReadOnlyList<PipelineReason>> ListAsync(Guid tenantId, ReasonKind kind, CancellationToken cancellationToken);

    Task<PipelineReason?> FindActiveAsync(Guid tenantId, ReasonKind kind, Guid reasonId, CancellationToken cancellationToken);

    /// <summary>Los nombres, activos o no: el historial puede nombrar uno desactivado.</summary>
    Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> reasonIds, CancellationToken cancellationToken);

    void Add(PipelineReason reason);
}

public interface ITriageDecisionRepository
{
    /// <summary>La última decisión de cada conversación pedida (vale la última, §5.1): la del entrante más reciente (D38).</summary>
    Task<IReadOnlyDictionary<Guid, TriageDecision>> FindLatestAsync(
        Guid tenantId, IReadOnlyCollection<Guid> conversationIds, CancellationToken cancellationToken);

    void Add(TriageDecision decision);
}

/// <summary>P4 del plan: antes de guardar, agrega el historial pendiente de cada negocio rastreado. Una versión vieja es
/// <c>concurrency.conflict</c> (412).</summary>
public interface IPipelineUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>§6.3: auditoría atómica propia (proyección de <c>audit.entries</c> en <c>PipelineDbContext</c>), en la misma
/// transacción que el cambio. Lo del worker va con <paramref name="bySystem"/> y actor vacío.</summary>
public interface IPipelineAuditRecorder
{
    void Record(Guid tenantId, Guid actorId, bool bySystem, string action, string resourceType, Guid resourceId, DateTimeOffset occurredAt);
}

public static class PipelineAuditActions
{
    public const string DealResource = "deal";
    public const string TriageResource = "triage_decision";
    public const string SettingsResource = "pipeline";

    public const string DealCreated = "pipeline.deal.created";
    public const string DealUpdated = "pipeline.deal.updated";
    public const string DealMoved = "pipeline.deal.moved";
    public const string DealWon = "pipeline.deal.won";
    public const string DealLost = "pipeline.deal.lost";
    public const string DealReopened = "pipeline.deal.reopened";
    public const string QuotationLinked = "pipeline.deal.quotation_linked";
    public const string QuotationUnlinked = "pipeline.deal.quotation_unlinked";
    public const string PrimaryQuotationSet = "pipeline.deal.primary_quotation_set";
    public const string TriageCreated = "pipeline.triage.created";
    public const string TriageDismissed = "pipeline.triage.dismissed";
    public const string StagesReplaced = "pipeline.stages.replaced";
    public const string LossReasonsReplaced = "pipeline.loss_reasons.replaced";
    public const string DismissReasonsReplaced = "pipeline.dismiss_reasons.replaced";
}
```

- [ ] **Step 4: Implementar la infraestructura**

`src/Modules/Pipeline/Modules.Pipeline.Infrastructure/Persistence/PipelineInbox.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

namespace Modules.Pipeline.Infrastructure.Persistence;

// Guarda de idempotencia y reclamo por consumidor, copia de IdentityInboxMessage (P2 del plan): una fila con ProcessedAt en
// null es un mensaje reclamado y sin terminar; ClaimedUntil es hasta cuándo nadie lo vuelve a tomar (su espera antes del
// reintento) y Attempts cuenta los reclamos.
internal sealed class PipelineInboxMessage
{
    public string Consumer { get; init; } = string.Empty;

    public Guid MessageId { get; init; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public DateTimeOffset? ClaimedUntil { get; set; }

    public int Attempts { get; set; }
}

// Proyección de sólo lectura del outbox de plataforma, consumida independiente por este módulo.
internal sealed class PipelineOutboxRecord
{
    public Guid Id { get; init; }

    public string EventName { get; init; } = string.Empty;

    public string PayloadJson { get; init; } = "{}";

    public DateTimeOffset OccurredAt { get; init; }
}

/// <summary>
/// Copia de <c>IdentityInboxClaims</c> (P2 del plan; ningún módulo referencia la infraestructura de otro): una sola sentencia,
/// fuera de toda transacción explícita, así que se commitea sola y el intento queda contado aunque la unidad de trabajo del
/// mensaje falle. La PK (consumer, message_id) garantiza un solo ganador; el reclamo número n dura <c>leases[n - 1]</c>, y desde
/// el último en adelante, el último.
/// </summary>
internal static class PipelineInboxClaims
{
    /// <returns>Los intentos contando éste, o <c>null</c> si el mensaje no se pudo reclamar.</returns>
    public static async Task<int?> TryClaimAsync(
        PipelineDbContext dbContext, string consumer, Guid messageId, DateTimeOffset now, IReadOnlyList<TimeSpan> leases, CancellationToken cancellationToken)
    {
        var leaseArray = leases.ToArray();
        var attempts = await dbContext.Database
            .SqlQuery<int>($"""
                INSERT INTO pipeline.inbox_messages (consumer, message_id, claimed_until, attempts)
                VALUES ({consumer}, {messageId}, {now} + ({leaseArray})[1], 1)
                ON CONFLICT (consumer, message_id) DO UPDATE
                    SET claimed_until = {now} + ({leaseArray})[
                            LEAST(inbox_messages.attempts + 1, cardinality({leaseArray}))],
                        attempts = inbox_messages.attempts + 1
                    WHERE inbox_messages.processed_at IS NULL
                      AND inbox_messages.claimed_until < {now}
                RETURNING attempts AS "Value"
                """)
            // Sin FirstOrDefaultAsync: componer sobre el SQL lo envolvería en un SELECT, y Postgres lo rechaza para un
            // INSERT ... RETURNING. Mismo criterio que IdentityInboxClaims.
            .ToListAsync(cancellationToken);

        return attempts.Count == 1 ? attempts[0] : null;
    }
}
```

`src/Modules/Pipeline/Modules.Pipeline.Infrastructure/Persistence/PipelineDbContext.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.Infrastructure.Persistence;

/// <summary>
/// Esquema <c>pipeline</c> (spec 2026-10-13 §4): embudo y etapas, negocios con su historial append-only, la foto de las
/// cotizaciones, los catálogos de motivos, las decisiones de triage y el inbox del worker. Proyecta <c>audit.entries</c> y
/// <c>platform.outbox_messages</c> sin ser su dueño (ExcludeFromMigrations). Sin FKs a otros esquemas: clientes, conversaciones,
/// membresías y cotizaciones son referencias blandas.
/// </summary>
public sealed class PipelineDbContext(DbContextOptions<PipelineDbContext> options) : DbContext(options)
{
    public const string Schema = "pipeline";

    /// <summary>D4: el blanco del <c>ON CONFLICT</c> de <c>PipelineProvisioner</c>.</summary>
    public const string PipelineTenantIndex = "IX_pipelines_tenant";

    /// <summary>D38: <c>PipelineUnitOfWork</c> discrimina por este nombre para responder <c>pipeline.triage.already_decided</c>.</summary>
    public const string TriageDecisionInboundIndex = "IX_triage_decisions_conversation_inbound";

    /// <summary>D12: <c>PipelineUnitOfWork</c> discrimina por este nombre para responder <c>pipeline.stage.has_deals</c>.</summary>
    public const string DealStageForeignKey = "FK_deals_stages_stage_id";

    /// <summary>P4 del plan: columna identidad que ordena el historial sin depender de la resolución del reloj.</summary>
    internal const string HistorySequence = "Sequence";

    public DbSet<SalesPipeline> Pipelines => Set<SalesPipeline>();

    public DbSet<PipelineStage> Stages => Set<PipelineStage>();

    public DbSet<Deal> Deals => Set<Deal>();

    public DbSet<DealStageHistoryEntry> History => Set<DealStageHistoryEntry>();

    public DbSet<DealQuotation> DealQuotations => Set<DealQuotation>();

    public DbSet<PipelineReason> Reasons => Set<PipelineReason>();

    public DbSet<TriageDecision> TriageDecisions => Set<TriageDecision>();

    internal DbSet<PipelineInboxMessage> Inbox => Set<PipelineInboxMessage>();

    internal DbSet<PipelineOutboxRecord> Outbox => Set<PipelineOutboxRecord>();

    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigurePipeline(modelBuilder);
        ConfigureStage(modelBuilder);
        ConfigureDeal(modelBuilder);
        ConfigureHistory(modelBuilder);
        ConfigureDealQuotation(modelBuilder);
        ConfigureReason(modelBuilder);
        ConfigureTriageDecision(modelBuilder);
        ConfigureInbox(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
    }

    private static void ConfigurePipeline(ModelBuilder modelBuilder)
    {
        var pipeline = modelBuilder.Entity<SalesPipeline>();
        pipeline.ToTable("pipelines", Schema);
        pipeline.HasKey(value => value.Id);
        pipeline.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        pipeline.Property(value => value.TenantId).HasColumnName("tenant_id");
        pipeline.Property(value => value.Name).HasColumnName("name").HasMaxLength(SalesPipeline.NameMaxLength);
        pipeline.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        pipeline.Property(value => value.CreatedAt).HasColumnName("created_at");
        pipeline.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        // D10: uno por tenant en este slice. Varios embudos después = quitar este índice.
        pipeline.HasIndex(value => value.TenantId, PipelineTenantIndex).IsUnique();
        pipeline.HasMany(value => value.Stages).WithOne().HasForeignKey(stage => stage.PipelineId).OnDelete(DeleteBehavior.Cascade);
        pipeline.Navigation(value => value.Stages).UsePropertyAccessMode(PropertyAccessMode.Field);
        pipeline.Ignore(value => value.OrderedStages);
        pipeline.Ignore(value => value.FirstOpenStage);
        pipeline.Ignore(value => value.WonStage);
        pipeline.Ignore(value => value.LostStage);
        pipeline.Ignore(value => value.QuotationStage);
    }

    private static void ConfigureStage(ModelBuilder modelBuilder)
    {
        var stage = modelBuilder.Entity<PipelineStage>();
        stage.ToTable("stages", Schema, table =>
        {
            table.HasCheckConstraint("CK_stages_kind", "kind IN ('Open','Won','Lost')");
            table.HasCheckConstraint("CK_stages_probability", "probability BETWEEN 0 AND 100");
        });
        stage.HasKey(value => value.Id);
        stage.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        stage.Property(value => value.PipelineId).HasColumnName("pipeline_id");
        stage.Property(value => value.Name).HasColumnName("name").HasMaxLength(SalesPipeline.StageNameMaxLength);
        stage.Property(value => value.Position).HasColumnName("position");
        stage.Property(value => value.Probability).HasColumnName("probability");
        stage.Property(value => value.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(8);
        stage.Property(value => value.StallDays).HasColumnName("stall_days");
        stage.Property(value => value.IsQuotationStage).HasColumnName("is_quotation_stage");
        stage.HasIndex(value => new { value.PipelineId, value.Position }, "IX_stages_pipeline_position");
    }

    private static void ConfigureDeal(ModelBuilder modelBuilder)
    {
        var deal = modelBuilder.Entity<Deal>();
        deal.ToTable("deals", Schema, table =>
        {
            table.HasCheckConstraint("CK_deals_source", "source IN ('Manual','Conversation','Quotation')");
            table.HasCheckConstraint("CK_deals_amount", "amount IS NULL OR amount >= 0");
        });
        deal.HasKey(value => value.Id);
        deal.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        deal.Property(value => value.TenantId).HasColumnName("tenant_id");
        deal.Property(value => value.PipelineId).HasColumnName("pipeline_id");
        deal.Property(value => value.CustomerId).HasColumnName("customer_id");
        deal.Property(value => value.OwnerMemberId).HasColumnName("owner_member_id");
        deal.Property(value => value.Title).HasColumnName("title").HasMaxLength(Deal.TitleMaxLength);
        deal.Property(value => value.Amount).HasColumnName("amount").HasPrecision(14, 2);
        deal.Property(value => value.Currency).HasColumnName("currency").HasMaxLength(3);
        deal.Property(value => value.ExpectedCloseDate).HasColumnName("expected_close_date");
        deal.Property(value => value.StageId).HasColumnName("stage_id");
        deal.Property(value => value.StageEnteredAt).HasColumnName("stage_entered_at");
        deal.Property(value => value.PrimaryQuotationId).HasColumnName("primary_quotation_id");
        deal.Property(value => value.Source).HasColumnName("source").HasConversion<string>().HasMaxLength(16);
        deal.Property(value => value.OriginConversationId).HasColumnName("origin_conversation_id");
        deal.Property(value => value.LossReasonId).HasColumnName("loss_reason_id");
        deal.Property(value => value.LossNote).HasColumnName("loss_note").HasMaxLength(Deal.LossNoteMaxLength);
        deal.Property(value => value.ClosedAt).HasColumnName("closed_at");
        deal.Property(value => value.LastChangedAt).HasColumnName("last_changed_at");
        deal.Property(value => value.CreatedAt).HasColumnName("created_at");
        deal.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        deal.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        deal.Ignore(value => value.IsClosed);
        // D12: la base garantiza que no se borre una etapa con negocios, aunque un negocio llegue entre la lectura de
        // StagesWithDealsAsync y el commit. PipelineUnitOfWork traduce la violación a pipeline.stage.has_deals.
        deal.HasOne<PipelineStage>().WithMany().HasForeignKey(value => value.StageId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName(DealStageForeignKey);
        // §4.4 y §5.1: los abiertos de un cliente (enlace automático, triage, ficha).
        deal.HasIndex(value => new { value.TenantId, value.CustomerId }, "IX_deals_tenant_customer_open").HasFilter("closed_at IS NULL");
        // D33: el tablero por etapa, en orden de llegada a la etapa.
        deal.HasIndex(value => new { value.TenantId, value.StageId, value.StageEnteredAt }, "IX_deals_tenant_stage");
        // D33 y D36: la ventana de 30 días de Won/Lost y el último cerrado del cliente.
        deal.HasIndex(value => new { value.TenantId, value.CustomerId, value.ClosedAt }, "IX_deals_tenant_customer_closed").HasFilter("closed_at IS NOT NULL");
    }

    private static void ConfigureHistory(ModelBuilder modelBuilder)
    {
        var history = modelBuilder.Entity<DealStageHistoryEntry>();
        history.ToTable("deal_stage_history", Schema, table =>
            table.HasCheckConstraint("CK_deal_stage_history_actor_kind", "actor_kind IN ('User','System')"));
        history.HasKey(value => value.Id);
        history.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        history.Property<long>(HistorySequence).HasColumnName("sequence").UseIdentityAlwaysColumn();
        history.Property(value => value.TenantId).HasColumnName("tenant_id");
        history.Property(value => value.DealId).HasColumnName("deal_id");
        history.Property(value => value.PipelineId).HasColumnName("pipeline_id");
        history.Property(value => value.FromStageId).HasColumnName("from_stage_id");
        history.Property(value => value.ToStageId).HasColumnName("to_stage_id");
        history.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        history.Property(value => value.ActorKind).HasColumnName("actor_kind").HasConversion<string>().HasMaxLength(8);
        history.Property(value => value.ActorMemberId).HasColumnName("actor_member_id");
        history.Property(value => value.Amount).HasColumnName("amount").HasPrecision(14, 2);
        history.Property(value => value.Currency).HasColumnName("currency").HasMaxLength(3);
        history.Property(value => value.Probability).HasColumnName("probability");
        history.HasIndex(value => new { value.TenantId, value.DealId }, "IX_deal_stage_history_deal");
    }

    private static void ConfigureDealQuotation(ModelBuilder modelBuilder)
    {
        var quotation = modelBuilder.Entity<DealQuotation>();
        quotation.ToTable("deal_quotations", Schema, table =>
            table.HasCheckConstraint("CK_deal_quotations_status", "status IN ('Sent','Converted')"));
        // §4.4: una cotización pertenece a lo sumo a un negocio.
        quotation.HasKey(value => new { value.TenantId, value.QuotationId });
        quotation.Property(value => value.TenantId).HasColumnName("tenant_id");
        quotation.Property(value => value.QuotationId).HasColumnName("quotation_id");
        quotation.Property(value => value.DealId).HasColumnName("deal_id");
        quotation.Property(value => value.CustomerId).HasColumnName("customer_id");
        quotation.Property(value => value.QuotationNumber).HasColumnName("quotation_number").HasMaxLength(DealQuotation.NumberMaxLength);
        quotation.Property(value => value.Total).HasColumnName("total").HasPrecision(14, 2);
        quotation.Property(value => value.Currency).HasColumnName("currency").HasMaxLength(3);
        quotation.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        quotation.Property(value => value.OrderId).HasColumnName("order_id");
        quotation.Property(value => value.OrderCancelledAt).HasColumnName("order_cancelled_at");
        quotation.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        quotation.HasIndex(value => new { value.TenantId, value.DealId }, "IX_deal_quotations_deal");
        quotation.HasIndex(value => new { value.TenantId, value.CustomerId }, "IX_deal_quotations_customer");
    }

    private static void ConfigureReason(ModelBuilder modelBuilder)
    {
        var reason = modelBuilder.Entity<PipelineReason>();
        reason.ToTable("reasons", Schema, table => table.HasCheckConstraint("CK_reasons_kind", "kind IN ('Loss','Dismiss')"));
        reason.HasKey(value => value.Id);
        reason.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        reason.Property(value => value.TenantId).HasColumnName("tenant_id");
        reason.Property(value => value.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(8);
        reason.Property(value => value.Name).HasColumnName("name").HasMaxLength(PipelineReason.NameMaxLength);
        reason.Property(value => value.Position).HasColumnName("position");
        reason.Property(value => value.IsActive).HasColumnName("is_active");
        reason.Property(value => value.CreatedAt).HasColumnName("created_at");
        reason.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        reason.HasIndex(value => new { value.TenantId, value.Kind, value.Position }, "IX_reasons_tenant_kind");
    }

    private static void ConfigureTriageDecision(ModelBuilder modelBuilder)
    {
        var decision = modelBuilder.Entity<TriageDecision>();
        decision.ToTable("triage_decisions", Schema, table =>
        {
            table.HasCheckConstraint("CK_triage_decisions_kind", "kind IN ('Created','Dismissed')");
            table.HasCheckConstraint("CK_triage_decisions_actor_kind", "actor_kind IN ('User','System')");
        });
        decision.HasKey(value => value.Id);
        decision.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        decision.Property(value => value.TenantId).HasColumnName("tenant_id");
        decision.Property(value => value.ConversationId).HasColumnName("conversation_id");
        decision.Property(value => value.CustomerId).HasColumnName("customer_id");
        decision.Property(value => value.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
        decision.Property(value => value.DealId).HasColumnName("deal_id");
        decision.Property(value => value.DismissReasonId).HasColumnName("dismiss_reason_id");
        decision.Property(value => value.DecidedAt).HasColumnName("decided_at");
        decision.Property(value => value.DecidedAgainstInboundAt).HasColumnName("decided_against_inbound_at");
        decision.Property(value => value.ActorKind).HasColumnName("actor_kind").HasConversion<string>().HasMaxLength(8);
        decision.Property(value => value.ActorMemberId).HasColumnName("actor_member_id");
        // D38: una sola decisión por conversación y entrante. La segunda concurrente choca acá y PipelineUnitOfWork la traduce a
        // pipeline.triage.already_decided; el negocio creado en el mismo SaveChanges se revierte con ella. También sirve para
        // buscar las decisiones de una conversación.
        decision.HasIndex(value => new { value.TenantId, value.ConversationId, value.DecidedAgainstInboundAt }, TriageDecisionInboundIndex)
            .IsUnique();
    }

    private static void ConfigureInbox(ModelBuilder modelBuilder)
    {
        var inbox = modelBuilder.Entity<PipelineInboxMessage>();
        inbox.ToTable("inbox_messages", Schema);
        inbox.HasKey(value => new { value.Consumer, value.MessageId });
        inbox.Property(value => value.Consumer).HasColumnName("consumer").HasMaxLength(200);
        inbox.Property(value => value.MessageId).HasColumnName("message_id");
        inbox.Property(value => value.ProcessedAt).HasColumnName("processed_at");
        // Los nombres van a mano porque PipelineInboxClaims los escribe en SQL crudo.
        inbox.Property(value => value.ClaimedUntil).HasColumnName("claimed_until");
        inbox.Property(value => value.Attempts).HasColumnName("attempts").HasDefaultValue(1);
    }

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<PipelineOutboxRecord>();
        outbox.ToTable("outbox_messages", "platform", table => table.ExcludeFromMigrations());
        outbox.HasKey(value => value.Id);
        outbox.Property(value => value.Id).HasColumnName("id");
        outbox.Property(value => value.EventName).HasColumnName("event_name");
        outbox.Property(value => value.PayloadJson).HasColumnName("payload").HasColumnType("jsonb");
        outbox.Property(value => value.OccurredAt).HasColumnName("occurred_at");
    }
}
```

`src/Modules/Pipeline/Modules.Pipeline.Infrastructure/Persistence/PipelineDbContextFactory.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modules.Pipeline.Infrastructure.Persistence;

/// <summary>Para <c>dotnet ef</c>: <c>Api.csproj</c> no referencia EF Design (CLAUDE.md, gotchas).</summary>
public sealed class PipelineDbContextFactory : IDesignTimeDbContextFactory<PipelineDbContext>
{
    public PipelineDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__QepDatabase")
            ?? "Host=localhost;Port=5432;Database=qep;Username=qep;Password=qep_dev";
        var options = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", PipelineDbContext.Schema))
            .Options;
        return new PipelineDbContext(options);
    }
}
```

`src/Modules/Pipeline/Modules.Pipeline.Infrastructure/Persistence/PipelineUnitOfWork.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;
using Npgsql;

namespace Modules.Pipeline.Infrastructure.Persistence;

/// <summary>P4 del plan: el historial pendiente de cada negocio rastreado entra en el mismo SaveChanges (D19). La versión vieja es
/// <c>concurrency.conflict</c> (412). Borrar una etapa que ganó un negocio entre la lectura y el commit choca con
/// <c>FK_deals_stages_stage_id</c> y sale <c>pipeline.stage.has_deals</c> (422), discriminando por nombre de restricción y no sólo
/// por SqlState (CLAUDE.md). Igual con D38: la segunda decisión de triage contra el mismo entrante choca con
/// <c>IX_triage_decisions_conversation_inbound</c> y sale <c>pipeline.triage.already_decided</c> (422); como todo va en un solo
/// SaveChanges, el negocio que esa decisión creaba no queda. Traducido acá y no en Application, que no referencia Npgsql
/// (PipelineLayerTests).</summary>
internal sealed class PipelineUnitOfWork(PipelineDbContext dbContext) : IPipelineUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries<Deal>().ToArray())
        {
            dbContext.History.AddRange(entry.Entity.PullHistory());
        }

        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Antes que la rama de DbUpdateException: DbUpdateConcurrencyException hereda de ella.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict", "The deal or the pipeline changed while the operation was being committed.", exception);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.ForeignKeyViolation &&
                  string.Equals(postgres.ConstraintName, PipelineDbContext.DealStageForeignKey, StringComparison.Ordinal))
        {
            throw new PipelineDomainException(
                PipelineErrorCodes.StageHasDeals, "A stage with deals cannot be removed; move its deals first.", exception);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, PipelineDbContext.TriageDecisionInboundIndex, StringComparison.Ordinal))
        {
            throw new PipelineDomainException(
                PipelineErrorCodes.TriageAlreadyDecided, "Another advisor already decided this conversation for its last message.", exception);
        }
    }
}
```

`src/Modules/Pipeline/Modules.Pipeline.Infrastructure/Persistence/PipelineRepositories.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.Infrastructure.Persistence;

internal sealed class DealRepository(PipelineDbContext dbContext) : IDealRepository
{
    public Task<Deal?> FindAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken) =>
        dbContext.Deals.SingleOrDefaultAsync(deal => deal.TenantId == tenantId && deal.Id == dealId, cancellationToken);

    public async Task<IReadOnlyList<Deal>> ListOpenByCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        await dbContext.Deals
            .Where(deal => deal.TenantId == tenantId && deal.CustomerId == customerId && deal.ClosedAt == null)
            .OrderByDescending(deal => deal.LastChangedAt)
            .ToListAsync(cancellationToken);

    public Task<Deal?> FindLastClosedByCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        dbContext.Deals.AsNoTracking()
            .Where(deal => deal.TenantId == tenantId && deal.CustomerId == customerId && deal.ClosedAt != null)
            .OrderByDescending(deal => deal.ClosedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlySet<Guid>> CustomersWithOpenDealsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        if (customerIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = customerIds.ToArray();
        var found = await dbContext.Deals.AsNoTracking()
            .Where(deal => deal.TenantId == tenantId && deal.ClosedAt == null && ids.Contains(deal.CustomerId))
            .Select(deal => deal.CustomerId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return found.ToHashSet();
    }

    public async Task<IReadOnlySet<Guid>> StagesWithDealsAsync(Guid tenantId, Guid pipelineId, CancellationToken cancellationToken)
    {
        var found = await dbContext.Deals.AsNoTracking()
            .Where(deal => deal.TenantId == tenantId && deal.PipelineId == pipelineId)
            .Select(deal => deal.StageId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return found.ToHashSet();
    }

    public Task<Guid?> FindLastOpenStageIdAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken) =>
        dbContext.History.AsNoTracking()
            .Where(entry => entry.TenantId == tenantId && entry.DealId == dealId)
            .OrderByDescending(entry => EF.Property<long>(entry, PipelineDbContext.HistorySequence))
            .Select(entry => entry.FromStageId)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<DealStageHistoryEntry>> ListHistoryAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken) =>
        await dbContext.History.AsNoTracking()
            .Where(entry => entry.TenantId == tenantId && entry.DealId == dealId)
            .OrderBy(entry => EF.Property<long>(entry, PipelineDbContext.HistorySequence))
            .ToListAsync(cancellationToken);

    public void Add(Deal deal) => dbContext.Deals.Add(deal);
}

internal sealed class DealQuotationRepository(PipelineDbContext dbContext) : IDealQuotationRepository
{
    public Task<DealQuotation?> FindAsync(Guid tenantId, Guid quotationId, CancellationToken cancellationToken) =>
        dbContext.DealQuotations.SingleOrDefaultAsync(
            quotation => quotation.TenantId == tenantId && quotation.QuotationId == quotationId, cancellationToken);

    public async Task<IReadOnlyList<DealQuotation>> ListByDealAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken) =>
        await dbContext.DealQuotations.AsNoTracking()
            .Where(quotation => quotation.TenantId == tenantId && quotation.DealId == dealId)
            .OrderByDescending(quotation => quotation.UpdatedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DealQuotation>> ListUnlinkedByCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        await dbContext.DealQuotations.AsNoTracking()
            .Where(quotation => quotation.TenantId == tenantId && quotation.CustomerId == customerId && quotation.DealId == null)
            .OrderByDescending(quotation => quotation.UpdatedAt)
            .ToListAsync(cancellationToken);

    public void Add(DealQuotation quotation) => dbContext.DealQuotations.Add(quotation);
}

internal sealed class PipelineReasonRepository(PipelineDbContext dbContext) : IPipelineReasonRepository
{
    public async Task<IReadOnlyList<PipelineReason>> ListAsync(Guid tenantId, ReasonKind kind, CancellationToken cancellationToken) =>
        await dbContext.Reasons
            .Where(reason => reason.TenantId == tenantId && reason.Kind == kind)
            .OrderBy(reason => reason.Position)
            .ToListAsync(cancellationToken);

    public Task<PipelineReason?> FindActiveAsync(Guid tenantId, ReasonKind kind, Guid reasonId, CancellationToken cancellationToken) =>
        dbContext.Reasons.AsNoTracking().SingleOrDefaultAsync(
            reason => reason.TenantId == tenantId && reason.Kind == kind && reason.Id == reasonId && reason.IsActive, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> reasonIds, CancellationToken cancellationToken)
    {
        if (reasonIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = reasonIds.ToArray();
        return await dbContext.Reasons.AsNoTracking()
            .Where(reason => reason.TenantId == tenantId && ids.Contains(reason.Id))
            .ToDictionaryAsync(reason => reason.Id, reason => reason.Name, cancellationToken);
    }

    public void Add(PipelineReason reason) => dbContext.Reasons.Add(reason);
}

internal sealed class TriageDecisionRepository(PipelineDbContext dbContext) : ITriageDecisionRepository
{
    public async Task<IReadOnlyDictionary<Guid, TriageDecision>> FindLatestAsync(
        Guid tenantId, IReadOnlyCollection<Guid> conversationIds, CancellationToken cancellationToken)
    {
        if (conversationIds.Count == 0)
        {
            return new Dictionary<Guid, TriageDecision>();
        }

        var ids = conversationIds.ToArray();
        var rows = await dbContext.TriageDecisions.AsNoTracking()
            .Where(decision => decision.TenantId == tenantId && ids.Contains(decision.ConversationId))
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(decision => decision.ConversationId)
            .ToDictionary(group => group.Key, group => group.MaxBy(decision => decision.DecidedAgainstInboundAt)!);
    }

    public void Add(TriageDecision decision) => dbContext.TriageDecisions.Add(decision);
}
```

`src/Modules/Pipeline/Modules.Pipeline.Infrastructure/Persistence/PipelineAuditRecorder.cs`:

```csharp
using Modules.Audit.Domain;
using Modules.Pipeline.Application;

namespace Modules.Pipeline.Infrastructure.Persistence;

// Camino de auditoría atómica (ADR 0019), copia de MessagingAuditRecorder: la entrada se acumula en PipelineDbContext y
// commitea o revierte con el cambio. changed_fields va vacío, como en el resto de los módulos con recorder propio.
internal sealed class PipelineAuditRecorder(PipelineDbContext dbContext) : IPipelineAuditRecorder
{
    private const string Source = "pipeline";
    private const string Outcome = "success";
    private const string NoChangedFields = "[]";

    public void Record(Guid tenantId, Guid actorId, bool bySystem, string action, string resourceType, Guid resourceId, DateTimeOffset occurredAt) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            actorId,
            bySystem ? AuditActorType.System : AuditActorType.Human,
            action,
            resourceType,
            resourceId.ToString(),
            Outcome,
            NoChangedFields,
            Source,
            occurredAt));
}
```

`src/Modules/Pipeline/Modules.Pipeline.Infrastructure/Persistence/PipelineProvisioner.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.Infrastructure.Persistence;

/// <summary>
/// Spec 2026-10-13 D4 y D31: Tenancy no avisa cuando se activa un módulo (H1), así que el embudo se crea en el primer acceso. Una
/// transacción propia con <c>INSERT … ON CONFLICT (tenant_id) DO NOTHING RETURNING id</c>: dos peticiones a la vez esperan el
/// índice único, una gana y la otra no inserta nada; las etapas y los motivos sólo se siembran si esta llamada creó el embudo.
/// SQL crudo y no <c>SaveChanges</c> para no arrastrar cambios ajenos que el contexto ya tuviera rastreados.
/// </summary>
internal sealed class PipelineProvisioner(PipelineDbContext dbContext, IClock clock) : IPipelineProvisioner
{
    public async Task<SalesPipeline> EnsureAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (await LoadAsync(tenantId, cancellationToken) is { } existing)
        {
            return existing;
        }

        var now = clock.UtcNow;
        var seed = SalesPipeline.CreateDefault(tenantId, now);
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            var inserted = await dbContext.Database.SqlQuery<Guid>($"""
                INSERT INTO pipeline.pipelines (id, tenant_id, name, version, created_at, updated_at)
                VALUES ({seed.Id}, {tenantId}, {seed.Name}, {seed.Version}, {now}, {now})
                ON CONFLICT (tenant_id) DO NOTHING
                RETURNING id AS "Value"
                """).ToListAsync(cancellationToken);
            if (inserted.Count == 1)
            {
                foreach (var stage in seed.OrderedStages)
                {
                    await dbContext.Database.ExecuteSqlAsync($"""
                        INSERT INTO pipeline.stages (id, pipeline_id, name, position, probability, kind, stall_days, is_quotation_stage)
                        VALUES ({stage.Id}, {seed.Id}, {stage.Name}, {stage.Position}, {stage.Probability}, {stage.Kind.ToString()}, {stage.StallDays}, {stage.IsQuotationStage})
                        """, cancellationToken);
                }

                await SeedReasonsAsync(tenantId, ReasonKind.Loss, PipelineSeed.LossReasons, now, cancellationToken);
                await SeedReasonsAsync(tenantId, ReasonKind.Dismiss, PipelineSeed.DismissReasons, now, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return await LoadAsync(tenantId, cancellationToken)
            ?? throw new InvalidOperationException($"The pipeline of tenant '{tenantId}' was not provisioned.");
    }

    private Task<SalesPipeline?> LoadAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.Pipelines.Include(pipeline => pipeline.Stages).SingleOrDefaultAsync(pipeline => pipeline.TenantId == tenantId, cancellationToken);

    private async Task SeedReasonsAsync(
        Guid tenantId, ReasonKind kind, IReadOnlyList<string> names, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (var index = 0; index < names.Count; index++)
        {
            var reason = PipelineReason.Create(tenantId, kind, names[index], index + 1, now);
            await dbContext.Database.ExecuteSqlAsync($"""
                INSERT INTO pipeline.reasons (id, tenant_id, kind, name, position, is_active, created_at, updated_at)
                VALUES ({reason.Id}, {tenantId}, {kind.ToString()}, {reason.Name}, {reason.Position}, TRUE, {now}, {now})
                """, cancellationToken);
        }
    }
}
```

`src/Modules/Pipeline/Modules.Pipeline.Infrastructure/PipelineDatabaseInitializer.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pipeline.Infrastructure.Persistence;

namespace Modules.Pipeline.Infrastructure;

public static class PipelineDatabaseInitializer
{
    public static async Task InitializePipelineDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PipelineDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
```

`PipelineInfrastructureExtensions.cs`, completo:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pipeline.Application;
using Modules.Pipeline.Infrastructure.Persistence;

namespace Modules.Pipeline.Infrastructure;

public static class PipelineInfrastructureExtensions
{
    public static IServiceCollection AddPipelineInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException("Connection string 'QepDatabase' is required.");

        services.AddDbContext<PipelineDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", PipelineDbContext.Schema)));

        // Spec 2026-10-13 D4: el embudo y sus semillas en el primer acceso.
        services.AddScoped<IPipelineProvisioner, PipelineProvisioner>();
        services.AddScoped<IDealRepository, DealRepository>();
        services.AddScoped<IDealQuotationRepository, DealQuotationRepository>();
        services.AddScoped<IPipelineReasonRepository, PipelineReasonRepository>();
        services.AddScoped<ITriageDecisionRepository, TriageDecisionRepository>();
        // P4 del plan: la unidad de trabajo guarda el historial pendiente de cada negocio.
        services.AddScoped<IPipelineUnitOfWork, PipelineUnitOfWork>();
        // §6.3: auditoría atómica propia (nunca el IAuditRecorder de Tenancy).
        services.AddScoped<IPipelineAuditRecorder, PipelineAuditRecorder>();
        return services;
    }
}
```

`src/Api/Program.cs`: `using Modules.Pipeline.Infrastructure;` y, después de `await app.Services.InitializeMessagingDatabaseAsync(…);`:

```csharp
// Pipeline (spec 2026-10-13): después de Audit (escribe en audit.entries) y de Tenancy (lee platform.outbox_messages). Sin FKs
// a otros esquemas: clientes, conversaciones, membresías y cotizaciones son referencias blandas.
await app.Services.InitializePipelineDatabaseAsync(
    app.Lifetime.ApplicationStopping);
```

- [ ] **Step 5: Generar la migración**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet ef migrations add InitialPipeline --project "$B\src\Modules\Pipeline\Modules.Pipeline.Infrastructure" --context PipelineDbContext -o Persistence/Migrations
if ($LASTEXITCODE -ne 0) { throw "ef falló" }
Select-String -Path "$B\src\Modules\Pipeline\Modules.Pipeline.Infrastructure\Persistence\Migrations\*_InitialPipeline.cs" -Pattern "CreateTable\(|EnsureSchema|audit|outbox_messages"
```

Esperado en `Select-String`: `EnsureSchema` de `pipeline` y **ocho** `CreateTable(` (`pipelines`, `stages`, `deals`, `deal_stage_history`, `deal_quotations`, `reasons`, `triage_decisions`, `inbox_messages`) y **ninguna** línea con `audit` ni `outbox_messages` (son proyecciones excluidas). Si aparecen, falta un `ExcludeFromMigrations`/`ownsTable: false`: se borra la migración, se corrige el modelo y se vuelve a generar.

- [ ] **Step 6: Correr y verificar que pasan, más las capas**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.IntegrationTests\Modules.Pipeline.IntegrationTests.csproj" --no-build --filter "FullyQualifiedName~PipelinePersistenceTests"
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --no-build --filter "FullyQualifiedName~PipelineLayerTests|FullyQualifiedName~CompositionRootTests"
```

Esperado: 6 verdes en `PipelinePersistenceTests`; capas y composición verdes.

- [ ] **Step 7: Commit**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
if ((git -C $B branch --show-current) -ne "feature/embudo-de-ventas") { throw "rama equivocada" }
git -C $B status --short
git -C $B add src/Modules/Pipeline/Modules.Pipeline.Application/PipelinePorts.cs src/Modules/Pipeline/Modules.Pipeline.Infrastructure src/Api/Program.cs tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/PipelineApiHarness.cs tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/PipelinePersistenceTests.cs
git -C $B status --short
git -C $B commit -m "feat(pipeline): esquema, migración inicial, unidad de trabajo con historial y embudo en el primer acceso"
git -C $B log -1 --format=%B
```

El segundo `git status` tiene que mostrar staged sólo esos archivos (y ningún `bin/`/`obj/`).

---

### Task 7: Puertos de lectura y adaptadores — conversaciones, clientes y nombres de miembros

**Files:**
- Create: `src/Modules/Pipeline/Modules.Pipeline.Application/PipelineDirectories.cs`
- Create: `src/Bootstrapper/PipelineConversationDirectory.cs`, `src/Bootstrapper/PipelineCustomerDirectory.cs`, `src/Bootstrapper/PipelineMemberNames.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (después de `services.AddScoped<IMessagingUploadStaging, MessagingUploadStaging>();`, `:687`)
- Test: `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/PipelineDirectoriesTests.cs`

**Interfaces:**
- Consumes: `MessagingDbContext.Conversations` (`Conversation.Id`, `TenantId`, `CustomerId`, `Status`, `LastInboundAt`, `LastMessageAt`, `LastActivityAt`), `ICustomerWhatsAppDirectory.FindRefsAsync`, `IMembershipRepository.ListByIdsAsync`, `IUserDirectory.GetEmailAsync`.
- Produces (Application, `PipelineDirectories.cs`):
  - `public sealed record PipelineConversation(Guid Id, Guid? CustomerId, bool IsOpen, DateTimeOffset? LastInboundAt, DateTimeOffset? LastMessageAt)`.
  - `public sealed record PipelineCustomerActivity(Guid CustomerId, DateTimeOffset? LastMessageAt, IReadOnlyList<Guid> ConversationIds)`.
  - `public sealed record PipelineCustomerRef(Guid Id, string Name, bool IsComplete)`.
  - `IPipelineConversationDirectory`: `ListOpenAsync(Guid tenantId, ct) → Task<IReadOnlyList<PipelineConversation>>` (abiertas **con** cliente, entrante más reciente primero, sin entrante al final), `FindActivityAsync(Guid tenantId, IReadOnlyCollection<Guid> customerIds, ct) → Task<IReadOnlyDictionary<Guid, PipelineCustomerActivity>>` (todas las conversaciones del cliente, abiertas o no, la de actividad más reciente primero), `FindAsync(Guid tenantId, Guid conversationId, ct) → Task<PipelineConversation?>`.
  - `IPipelineCustomerDirectory.FindAsync(Guid tenantId, IReadOnlyCollection<Guid> customerIds, ct) → Task<IReadOnlyDictionary<Guid, PipelineCustomerRef>>`.
  - `IPipelineMemberNames.FindAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, ct) → Task<IReadOnlyDictionary<Guid, string>>` (sólo membresías del tenant; nombre visible o correo).

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/PipelineDirectoriesTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Modules.Pipeline.Application;
using static Modules.Pipeline.IntegrationTests.PipelineApiHarness;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>Spec 2026-10-13 D9 y §5.3: los tres adaptadores de Bootstrapper contra las tablas reales de Messaging, Customers y
/// Tenancy. Nada cruza tenants.</summary>
public sealed class PipelineDirectoriesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConversationsAreReadFromMessagingWithoutCrossingTenants()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var (customerId, _) = await SeedCustomerAsync(factory, tenantId, "Laura");
        var now = DateTimeOffset.UtcNow;
        var older = await SeedConversationAsync(factory, connectionString, tenantId, customerId, now.AddDays(-2), now.AddDays(-2));
        var newer = await SeedConversationAsync(factory, connectionString, tenantId, customerId, now.AddHours(-1), now.AddMinutes(-5));
        var silent = await SeedConversationAsync(factory, connectionString, tenantId, customerId);
        var resolved = await SeedConversationAsync(factory, connectionString, tenantId, customerId, now.AddDays(-9), now.AddMinutes(-1), resolved: true);
        await SeedConversationAsync(factory, connectionString, tenantId, customerId: null, now, now);
        var foreign = await SeedConversationAsync(factory, connectionString, Guid.CreateVersion7(), customerId, now, now);

        using var scope = factory.Services.CreateScope();
        var directory = scope.ServiceProvider.GetRequiredService<IPipelineConversationDirectory>();
        var open = await directory.ListOpenAsync(tenantId, Ct);
        var activity = await directory.FindActivityAsync(tenantId, [customerId], Ct);

        Assert.Equal([newer, older, silent], open.Select(conversation => conversation.Id));
        Assert.All(open, conversation => Assert.True(conversation.IsOpen));
        var customer = activity[customerId];
        Assert.Equal(4, customer.ConversationIds.Count);
        Assert.DoesNotContain(foreign, customer.ConversationIds);
        Assert.Equal(now.AddMinutes(-1).ToUnixTimeSeconds(), customer.LastMessageAt!.Value.ToUnixTimeSeconds());
        Assert.False((await directory.FindAsync(tenantId, resolved, Ct))!.IsOpen);
        Assert.Null(await directory.FindAsync(tenantId, foreign, Ct));
    }

    [Fact]
    public async Task CustomersAreReadFromCustomersWithTheirCompleteness()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var (customerId, name) = await SeedCustomerAsync(factory, tenantId, "Laura");

        using var scope = factory.Services.CreateScope();
        var directory = scope.ServiceProvider.GetRequiredService<IPipelineCustomerDirectory>();
        var found = await directory.FindAsync(tenantId, [customerId], Ct);

        Assert.Equal(new PipelineCustomerRef(customerId, name, false), found[customerId]);
        Assert.Empty(await directory.FindAsync(Guid.CreateVersion7(), [customerId], Ct));
    }

    [Fact]
    public async Task MemberNamesAreTheTenantsOnly()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var other = await RegisterTenantAsync(factory);
        var (memberId, _) = await SeedMemberAsync(connectionString, tenant.TenantId, "Laura Gómez");
        var foreignOwner = await OwnerMembershipIdAsync(connectionString, other);

        using var scope = factory.Services.CreateScope();
        var names = await scope.ServiceProvider.GetRequiredService<IPipelineMemberNames>()
            .FindAsync(tenant.TenantId, [memberId, foreignOwner, Guid.CreateVersion7()], Ct);

        Assert.Equal("Laura Gómez", Assert.Single(names).Value);
        Assert.Equal(memberId, names.Keys.Single());
    }
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.IntegrationTests\Modules.Pipeline.IntegrationTests.csproj" --filter "FullyQualifiedName~PipelineDirectoriesTests"
```

Esperado: `error CS0246` sobre `IPipelineConversationDirectory`, `IPipelineCustomerDirectory`, `PipelineCustomerRef`, `IPipelineMemberNames`.

- [ ] **Step 3: Implementar los puertos, los adaptadores y el registro**

`src/Modules/Pipeline/Modules.Pipeline.Application/PipelineDirectories.cs`:

```csharp
namespace Modules.Pipeline.Application;

/// <summary>Spec 2026-10-13 §5.3: una conversación de Messaging como la ve Pipeline.</summary>
public sealed record PipelineConversation(Guid Id, Guid? CustomerId, bool IsOpen, DateTimeOffset? LastInboundAt, DateTimeOffset? LastMessageAt);

/// <summary>§5.2 y D21: el último mensaje de las conversaciones del cliente (entrante o saliente) y cuáles son.</summary>
public sealed record PipelineCustomerActivity(Guid CustomerId, DateTimeOffset? LastMessageAt, IReadOnlyList<Guid> ConversationIds);

/// <summary>D36 y §6: nombre y completitud del cliente; un <c>Incomplete</c> se acepta (H13).</summary>
public sealed record PipelineCustomerRef(Guid Id, string Name, bool IsComplete);

/// <summary>
/// D9: lo que Pipeline necesita de Messaging, con adaptador en Bootstrapper sobre <c>MessagingDbContext</c> (como
/// <c>QuotationsReportSource</c> lee <c>QuotationsDbContext</c>). Pipeline no referencia Messaging.
/// </summary>
public interface IPipelineConversationDirectory
{
    /// <summary>§5.1 y D35: las abiertas <b>con</b> cliente, el entrante más reciente primero y las que no tienen entrante al
    /// final (P13).</summary>
    Task<IReadOnlyList<PipelineConversation>> ListOpenAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Por cliente, todas sus conversaciones (abiertas o no), la de actividad más reciente primero, y su último mensaje.</summary>
    Task<IReadOnlyDictionary<Guid, PipelineCustomerActivity>> FindActivityAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    Task<PipelineConversation?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);
}

/// <summary>D9: adaptador en Bootstrapper sobre <c>ICustomerWhatsAppDirectory.FindRefsAsync</c>. Un id de otro tenant no aparece.</summary>
public interface IPipelineCustomerDirectory
{
    Task<IReadOnlyDictionary<Guid, PipelineCustomerRef>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);
}

/// <summary>Mismo criterio que <c>IMessagingMemberNames</c>: nombre visible de la membresía o su correo. Sólo las del tenant; la que
/// no está no aparece, y quien llama decide qué mostrar.</summary>
public interface IPipelineMemberNames
{
    Task<IReadOnlyDictionary<Guid, string>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken);
}
```

`src/Bootstrapper/PipelineConversationDirectory.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using Modules.Pipeline.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-13 D9 y §5.3: las conversaciones que Pipeline necesita, leídas del <c>DbContext</c> de Messaging como
/// <see cref="QuotationsReportSource"/> lee el de Quotations. Sin tracking: Pipeline nunca escribe en Messaging. Usa
/// <c>IX_conversations_tenant_open</c> y <c>IX_conversations_tenant_customer_activity</c>.
/// </summary>
internal sealed class PipelineConversationDirectory(MessagingDbContext messaging) : IPipelineConversationDirectory
{
    public async Task<IReadOnlyList<PipelineConversation>> ListOpenAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await messaging.Conversations.AsNoTracking()
            .Where(conversation => conversation.TenantId == tenantId
                && conversation.Status == ConversationStatus.Open
                && conversation.CustomerId != null)
            // P13: sin entrante al final. Postgres pone los NULL primero en un DESC.
            .OrderByDescending(conversation => conversation.LastInboundAt != null)
            .ThenByDescending(conversation => conversation.LastInboundAt)
            .Select(conversation => new PipelineConversation(
                conversation.Id, conversation.CustomerId, true, conversation.LastInboundAt, conversation.LastMessageAt))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, PipelineCustomerActivity>> FindActivityAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        if (customerIds.Count == 0)
        {
            return new Dictionary<Guid, PipelineCustomerActivity>();
        }

        var ids = customerIds.ToArray();
        var rows = await messaging.Conversations.AsNoTracking()
            .Where(conversation => conversation.TenantId == tenantId
                && conversation.CustomerId != null
                && ids.Contains(conversation.CustomerId.Value))
            .OrderByDescending(conversation => conversation.LastActivityAt)
            .Select(conversation => new { CustomerId = conversation.CustomerId!.Value, conversation.Id, conversation.LastMessageAt })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.CustomerId)
            .ToDictionary(
                group => group.Key,
                group => new PipelineCustomerActivity(
                    group.Key, group.Max(row => row.LastMessageAt), group.Select(row => row.Id).ToArray()));
    }

    public Task<PipelineConversation?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        messaging.Conversations.AsNoTracking()
            .Where(conversation => conversation.TenantId == tenantId && conversation.Id == conversationId)
            .Select(conversation => new PipelineConversation(
                conversation.Id,
                conversation.CustomerId,
                conversation.Status == ConversationStatus.Open,
                conversation.LastInboundAt,
                conversation.LastMessageAt))
            .SingleOrDefaultAsync(cancellationToken);
}
```

`src/Bootstrapper/PipelineCustomerDirectory.cs`:

```csharp
using Modules.Customers.Application;
using Modules.Pipeline.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-13 D9: nombre y completitud por id, por el mismo puerto de Customers que usa Messaging
/// (<see cref="MessagingCustomerDirectory"/>).</summary>
internal sealed class PipelineCustomerDirectory(ICustomerWhatsAppDirectory whatsApp) : IPipelineCustomerDirectory
{
    public async Task<IReadOnlyDictionary<Guid, PipelineCustomerRef>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
        customerIds.Count == 0
            ? new Dictionary<Guid, PipelineCustomerRef>()
            : (await whatsApp.FindRefsAsync(tenantId, customerIds, cancellationToken))
                .ToDictionary(pair => pair.Key, pair => new PipelineCustomerRef(pair.Value.Id, pair.Value.Name, pair.Value.IsComplete));
}
```

`src/Bootstrapper/PipelineMemberNames.cs`:

```csharp
using Modules.Identity.Application;
using Modules.Pipeline.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>Spec 2026-10-13 §6: el dueño del negocio y el actor del historial por nombre. Copia de
/// <see cref="MessagingMemberNames"/>: <c>Membership.DisplayName ?? correo</c>, sólo membresías del tenant.</summary>
internal sealed class PipelineMemberNames(IMembershipRepository memberships, IUserDirectory users) : IPipelineMemberNames
{
    public async Task<IReadOnlyDictionary<Guid, string>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken)
    {
        var names = new Dictionary<Guid, string>();
        if (memberIds.Count == 0)
        {
            return names;
        }

        var scoped = await memberships.ListByIdsAsync(
            new TenantId(tenantId), memberIds.Select(id => new MembershipId(id)).ToArray(), cancellationToken);
        foreach (var membership in scoped)
        {
            var name = !string.IsNullOrWhiteSpace(membership.DisplayName)
                ? membership.DisplayName
                : await users.GetEmailAsync(membership.UserId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(name))
            {
                names[membership.Id.Value] = name;
            }
        }

        return names;
    }
}
```

`QepServiceCollectionExtensions.cs`, después de `services.AddScoped<IMessagingUploadStaging, MessagingUploadStaging>();`:

```csharp
        // Pipeline (spec 2026-10-13 D9): conversaciones por el DbContext de Messaging, clientes por Customers y nombres de
        // miembros por Tenancy e Identity. El único punto donde Pipeline toca esos módulos.
        services.AddScoped<IPipelineConversationDirectory, PipelineConversationDirectory>();
        services.AddScoped<IPipelineCustomerDirectory, PipelineCustomerDirectory>();
        services.AddScoped<IPipelineMemberNames, PipelineMemberNames>();
```

- [ ] **Step 4: Correr y verificar que pasan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.IntegrationTests\Modules.Pipeline.IntegrationTests.csproj" --no-build --filter "FullyQualifiedName~PipelineDirectoriesTests"
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --no-build --filter "FullyQualifiedName~PipelineLayerTests|FullyQualifiedName~CompositionRootTests"
```

Esperado: 3 verdes en `PipelineDirectoriesTests`; capas y composición verdes.

- [ ] **Step 5: Commit**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
if ((git -C $B branch --show-current) -ne "feature/embudo-de-ventas") { throw "rama equivocada" }
git -C $B status --short
git -C $B add src/Modules/Pipeline/Modules.Pipeline.Application/PipelineDirectories.cs src/Bootstrapper/PipelineConversationDirectory.cs src/Bootstrapper/PipelineCustomerDirectory.cs src/Bootstrapper/PipelineMemberNames.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/PipelineDirectoriesTests.cs
git -C $B commit -m "feat(pipeline): puertos de lectura de conversaciones, clientes y miembros con adaptadores"
git -C $B log -1 --format=%B
```

---

### Task 8: Ajustes — etapas y catálogos de motivos

**Files:**
- Create: `src/Modules/Pipeline/Modules.Pipeline.Application/PipelineSupport.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Application/PipelineDtos.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Application/Settings.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Api/SettingsEndpoints.cs`
- Modify: `src/Modules/Pipeline/Modules.Pipeline.Api/PipelineEndpoints.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (handlers y validadores, después de `services.AddScoped<IQueryHandler<ListAssigneesQuery, AssigneesDto>, ListAssigneesHandler>();`)
- Test: `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/SettingsApiTests.cs`

**Interfaces:**
- Consumes: `IPipelineProvisioner`, `IDealRepository.StagesWithDealsAsync`, `IPipelineReasonRepository`, `IPipelineUnitOfWork`, `IPipelineAuditRecorder`, `PipelineAuditActions` (Task 6); `SalesPipeline.ReplaceStages`, `ReasonCatalog.Replace` (Task 4); `IExecutionContext`, `ITenantModules`, `TenantModuleGuard`, `IMembershipDirectory`, `IClock`.
- Produces:
  - `PipelineSupport.cs`: `internal static class PipelineAuthorization { Task EnsureAsync(IExecutionContext, ITenantModules, Guid tenantId, string permission, CancellationToken); RequestForbiddenException Denied(); }`, `public sealed class PipelineCaller(IMembershipDirectory, IExecutionContext) { Task<Guid?> FindAsync(Guid tenantId, ct); Task<Guid> RequireAsync(Guid tenantId, ct); }`, `internal static class PipelineNotFound { Deal(Guid), Customer(Guid), Conversation(Guid) }`, `internal static class PipelineVersions { void Ensure(long current, long? received) }`.
  - DTOs (contrato): `StageDto(Guid Id, string Name, int Position, string Kind, int Probability, int? StallDays, bool IsQuotationStage)`, `PipelineStagesDto(Guid PipelineId, long Version, IReadOnlyList<StageDto> Stages)`, `ReasonDto(Guid Id, string Name, int Position)`, `ReasonListDto(string Kind, IReadOnlyList<ReasonDto> Items)`, `StageRequest(Guid? Id, string? Name, string? Kind, int? Probability, int? StallDays, bool IsQuotationStage)`, `ReasonRequest(Guid? Id, string? Name)`; `PipelineMapping.ToDto(SalesPipeline)`, `PipelineMapping.ToDto(ReasonKind, IEnumerable<PipelineReason>)`.
  - Mensajes: `GetPipelineStagesQuery(Guid TenantId)`, `ReplacePipelineStagesCommand(Guid TenantId, long? Version, IReadOnlyList<StageRequest>? Stages)`, `GetPipelineReasonsQuery(Guid TenantId, ReasonKind Kind)`, `ReplacePipelineReasonsCommand(Guid TenantId, ReasonKind Kind, IReadOnlyList<ReasonRequest>? Items)` con sus handlers y `ReplacePipelineStagesValidator`, `ReplacePipelineReasonsValidator`.
  - Api: `ReplaceStagesRequest(long? Version, IReadOnlyList<StageRequest>? Stages)`, `ReplaceReasonsRequest(IReadOnlyList<ReasonRequest>? Items)`; `PipelineEndpoints` crea el grupo `/api/v1/tenants/{tenantId:guid}/pipeline` y llama a `MapSettingsEndpoints`.

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/SettingsApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pipeline.Api;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;
using static Modules.Pipeline.IntegrationTests.PipelineApiHarness;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>Spec 2026-10-13 §6 (ajustes), D11, D12, D31 y P8 del plan: las etapas y los dos catálogos, sembrados en el primer
/// acceso, reemplazados enteros, con versión en las etapas y auditados.</summary>
public sealed class SettingsApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string StagesUrl(Guid tenantId) => $"{PipelineUrl(tenantId)}/settings/stages";

    private static async Task<(RegisteredTenant Tenant, HttpClient Client)> TenantAsync(QepApiFactory factory, string connectionString, params string[] permissions)
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        return (tenant, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, permissions));
    }

    private static List<StageRequest> Layout(PipelineStagesDto stages) =>
        [.. stages.Stages.Select(stage => new StageRequest(stage.Id, stage.Name, stage.Kind, stage.Probability, stage.StallDays, stage.IsQuotationStage))];

    [Fact]
    public async Task TheFirstReadSeedsTheDefaultLayout()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenant, client) = await TenantAsync(factory, connectionString, ReadPermissions);
        using var _ = client;

        var stages = await client.GetFromJsonAsync<PipelineStagesDto>(StagesUrl(tenant.TenantId), Ct);

        Assert.NotNull(stages);
        Assert.Equal(1L, stages.Version);
        Assert.Equal(["Nuevo", "Calificado", "Cotizado", "Negociación", "Ganado", "Perdido"], stages.Stages.Select(stage => stage.Name));
        Assert.Equal(["Open", "Open", "Open", "Open", "Won", "Lost"], stages.Stages.Select(stage => stage.Kind));
        Assert.Equal("Cotizado", Assert.Single(stages.Stages, stage => stage.IsQuotationStage).Name);
    }

    [Fact]
    public async Task ReplacingTheStagesRenamesAddsBumpsTheVersionAndAudits()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenant, client) = await TenantAsync(factory, connectionString, AllPermissions);
        using var _ = client;
        var stages = (await client.GetFromJsonAsync<PipelineStagesDto>(StagesUrl(tenant.TenantId), Ct))!;
        var layout = Layout(stages);
        layout[0] = layout[0] with { Name = "Prospecto" };
        layout.Insert(3, new StageRequest(null, "Demostración", "Open", 40, 4, false));

        var response = await client.PutAsJsonAsync(StagesUrl(tenant.TenantId), new ReplaceStagesRequest(stages.Version, layout), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var replaced = await response.Content.ReadFromJsonAsync<PipelineStagesDto>(Ct);
        Assert.NotNull(replaced);
        Assert.Equal(2L, replaced.Version);
        Assert.Equal(stages.Stages[0].Id, replaced.Stages[0].Id);
        Assert.Equal(
            ["Prospecto", "Calificado", "Cotizado", "Demostración", "Negociación", "Ganado", "Perdido"],
            replaced.Stages.Select(stage => stage.Name));
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE tenant_id = @t AND action = 'pipeline.stages.replaced' AND source = 'pipeline'",
            ("t", tenant.TenantId)));
    }

    [Fact]
    public async Task AStaleVersionIs412AndNothingChanges()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenant, client) = await TenantAsync(factory, connectionString, AllPermissions);
        using var _ = client;
        var stages = (await client.GetFromJsonAsync<PipelineStagesDto>(StagesUrl(tenant.TenantId), Ct))!;
        var layout = Layout(stages);
        layout[0] = layout[0] with { Name = "Prospecto" };
        (await client.PutAsJsonAsync(StagesUrl(tenant.TenantId), new ReplaceStagesRequest(stages.Version, layout), Ct)).EnsureSuccessStatusCode();
        layout[0] = layout[0] with { Name = "Otro" };

        var stale = await client.PutAsJsonAsync(StagesUrl(tenant.TenantId), new ReplaceStagesRequest(stages.Version, layout), Ct);

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", (await ProblemAsync(stale)).Code);
        Assert.Equal("Prospecto", (await client.GetFromJsonAsync<PipelineStagesDto>(StagesUrl(tenant.TenantId), Ct))!.Stages[0].Name);
    }

    // Review Focus 2: borrar la etapa de un negocio no deja negocios huérfanos ni media disposición aplicada.
    [Fact]
    public async Task RemovingAStageWithDealsIs422AndTheLayoutStays()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenant, client) = await TenantAsync(factory, connectionString, AllPermissions);
        using var _ = client;
        var stages = (await client.GetFromJsonAsync<PipelineStagesDto>(StagesUrl(tenant.TenantId), Ct))!;
        using (var scope = factory.Services.CreateScope())
        {
            var pipeline = await scope.ServiceProvider.GetRequiredService<IPipelineProvisioner>().EnsureAsync(tenant.TenantId, Ct);
            scope.ServiceProvider.GetRequiredService<IDealRepository>().Add(Deal.Create(
                tenant.TenantId, pipeline, Guid.CreateVersion7(), Guid.CreateVersion7(), "Dotación", null, "COP", null,
                DealSource.Manual, null, DealActor.ForSystem, DateTimeOffset.UtcNow));
            await scope.ServiceProvider.GetRequiredService<IPipelineUnitOfWork>().SaveChangesAsync(Ct);
        }

        var layout = Layout(stages);
        layout.RemoveAt(0);

        var response = await client.PutAsJsonAsync(StagesUrl(tenant.TenantId), new ReplaceStagesRequest(stages.Version, layout), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(PipelineErrorCodes.StageHasDeals, (await ProblemAsync(response)).Code);
        Assert.Equal(6, (await client.GetFromJsonAsync<PipelineStagesDto>(StagesUrl(tenant.TenantId), Ct))!.Stages.Count);
    }

    [Fact]
    public async Task ABrokenLayoutIs422LayoutInvalidAndABlankNameIsValidationFailed()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenant, client) = await TenantAsync(factory, connectionString, AllPermissions);
        using var _ = client;
        var stages = (await client.GetFromJsonAsync<PipelineStagesDto>(StagesUrl(tenant.TenantId), Ct))!;
        var twoQuotationStages = Layout(stages);
        twoQuotationStages[0] = twoQuotationStages[0] with { IsQuotationStage = true };
        var blankName = Layout(stages);
        blankName[1] = blankName[1] with { Name = "  " };

        var broken = await client.PutAsJsonAsync(StagesUrl(tenant.TenantId), new ReplaceStagesRequest(stages.Version, twoQuotationStages), Ct);
        var blank = await client.PutAsJsonAsync(StagesUrl(tenant.TenantId), new ReplaceStagesRequest(stages.Version, blankName), Ct);
        var noVersion = await client.PutAsJsonAsync(StagesUrl(tenant.TenantId), new ReplaceStagesRequest(null, Layout(stages)), Ct);

        Assert.Equal(PipelineErrorCodes.StageLayoutInvalid, (await ProblemAsync(broken)).Code);
        var (blankCode, blankKeys) = await ProblemAsync(blank);
        Assert.Equal("validation.failed", blankCode);
        Assert.Contains("stages[1].name", blankKeys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["version"], (await ProblemAsync(noVersion)).ErrorKeys, StringComparer.OrdinalIgnoreCase);
    }

    // P8: el catálogo se reemplaza entero; el que deja de venir se desactiva y no vuelve en el GET.
    [Fact]
    public async Task TheReasonCatalogsAreSeededAndReplacedIndependently()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenant, client) = await TenantAsync(factory, connectionString, AllPermissions);
        using var _ = client;
        var lossUrl = $"{PipelineUrl(tenant.TenantId)}/settings/loss-reasons";
        var dismissUrl = $"{PipelineUrl(tenant.TenantId)}/settings/dismiss-reasons";
        var loss = (await client.GetFromJsonAsync<ReasonListDto>(lossUrl, Ct))!;

        var response = await client.PutAsJsonAsync(lossUrl, new ReplaceReasonsRequest(
            [new ReasonRequest(loss.Items[1].Id, "Competencia directa"), new ReasonRequest(null, "Tiempo de entrega")]), Ct);

        Assert.Equal(("Loss", 5), (loss.Kind, loss.Items.Count));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var replaced = (await response.Content.ReadFromJsonAsync<ReasonListDto>(Ct))!;
        Assert.Equal(["Competencia directa", "Tiempo de entrega"], replaced.Items.Select(item => item.Name));
        Assert.Equal(loss.Items[1].Id, replaced.Items[0].Id);
        Assert.Equal(["Competencia directa", "Tiempo de entrega"], (await client.GetFromJsonAsync<ReasonListDto>(lossUrl, Ct))!.Items.Select(item => item.Name));
        Assert.Equal(["Soporte", "Recompra", "Proveedor", "Spam", "Otro"], (await client.GetFromJsonAsync<ReasonListDto>(dismissUrl, Ct))!.Items.Select(item => item.Name));
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE tenant_id = @t AND action = 'pipeline.loss_reasons.replaced'", ("t", tenant.TenantId)));
    }

    [Fact]
    public async Task WithoutTheCapabilityOrThePermissionOrFromAnotherTenantIs403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var other = await RegisterTenantAsync(factory);
        using var withoutCapability = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, AllPermissions);
        using var reader = CreateClient(factory, other.OwnerUserId, other.TenantId, ReadPermissions);
        await EnablePipelineAsync(connectionString, other.TenantId);

        var disabled = await withoutCapability.GetAsync(StagesUrl(tenant.TenantId), Ct);
        var noPermission = await reader.PutAsJsonAsync(StagesUrl(other.TenantId), new ReplaceStagesRequest(1, []), Ct);
        var foreignRoute = await reader.GetAsync(StagesUrl(tenant.TenantId), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(disabled)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, noPermission.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreignRoute.StatusCode);
        Assert.Equal("authorization.denied", (await ProblemAsync(foreignRoute)).Code);
    }
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.IntegrationTests\Modules.Pipeline.IntegrationTests.csproj" --filter "FullyQualifiedName~SettingsApiTests"
```

Esperado: `error CS0246` sobre `PipelineStagesDto`, `StageRequest`, `ReplaceStagesRequest`, `ReasonListDto`.

- [ ] **Step 3: Implementar el soporte, los DTO y los handlers**

`src/Modules/Pipeline/Modules.Pipeline.Application/PipelineSupport.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Pipeline.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Pipeline.Application;

/// <summary>Copia de <c>MessagingAuthorization</c>: tenant de la ruta distinto del activo, o permiso faltante → 403
/// <c>authorization.denied</c>, nunca 404. Después, la capacidad <c>pipeline</c> → 403 <c>tenancy.module_not_enabled</c> (P1).</summary>
internal static class PipelineAuthorization
{
    public static async Task EnsureAsync(
        IExecutionContext executionContext, ITenantModules tenantModules, Guid tenantId, string permission, CancellationToken cancellationToken)
    {
        if (executionContext.TenantId.Value != tenantId || !executionContext.HasPermission(permission))
        {
            throw Denied();
        }

        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, tenantId, TenantModuleKeys.Pipeline, cancellationToken);
    }

    public static RequestForbiddenException Denied() =>
        new("authorization.denied", "The subject cannot perform this pipeline operation for this tenant.");
}

/// <summary>La membresía activa de quien llama en el tenant, resuelta una vez por request (scoped), como <c>CallerMembership</c>
/// de Messaging. La usan el dueño por defecto, <c>owner=me</c> y el actor del historial.</summary>
public sealed class PipelineCaller(IMembershipDirectory membershipDirectory, IExecutionContext executionContext)
{
    private Guid? _tenantId;
    private Guid? _memberId;

    public async Task<Guid?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_tenantId != tenantId)
        {
            _memberId = await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, tenantId, cancellationToken);
            _tenantId = tenantId;
        }

        return _memberId;
    }

    /// <summary>Sin membresía activa no hay a quién atribuir el cambio: 403, como <c>QuotationAdvisorResolver</c>.</summary>
    public async Task<Guid> RequireAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await FindAsync(tenantId, cancellationToken) ?? throw PipelineAuthorization.Denied();
}

internal static class PipelineNotFound
{
    public static ResourceNotFoundException Deal(Guid id) => new(PipelineErrorCodes.DealNotFound, $"Deal '{id}' was not found.");

    public static ResourceNotFoundException Customer(Guid id) => new(PipelineErrorCodes.CustomerNotFound, $"Customer '{id}' was not found.");

    public static ResourceNotFoundException Conversation(Guid id) =>
        new(PipelineErrorCodes.TriageConversationNotFound, $"Conversation '{id}' was not found.");
}

internal static class PipelineVersions
{
    /// <summary>La versión que el cliente leyó tiene que ser la actual: si no, 412 sin tocar nada.</summary>
    public static void Ensure(long current, long? received)
    {
        if (received != current)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The resource changed after it was loaded.");
        }
    }
}
```

`src/Modules/Pipeline/Modules.Pipeline.Application/PipelineDtos.cs` (las tareas siguientes agregan los suyos al final):

```csharp
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.Application;

// Spec 2026-10-13 §6: contrato con qep-frontend. Los enums viajan con su nombre (Open, Won, Loss…): el diccionario de etiquetas
// lo tiene el frontend.

public sealed record StageDto(Guid Id, string Name, int Position, string Kind, int Probability, int? StallDays, bool IsQuotationStage);

/// <summary>BFF: la versión viaja con las etapas porque el <c>PUT</c> la exige; la pantalla de ajustes no la pide aparte.</summary>
public sealed record PipelineStagesDto(Guid PipelineId, long Version, IReadOnlyList<StageDto> Stages);

public sealed record ReasonDto(Guid Id, string Name, int Position);

/// <summary>Sólo los activos, en orden: lo que el modal de pérdida y el de descarte dibujan.</summary>
public sealed record ReasonListDto(string Kind, IReadOnlyList<ReasonDto> Items);

/// <summary>Una etapa del <c>PUT /settings/stages</c>. Todo opcional para que lo que falte salga 422 con su clave en
/// <c>errors</c>, no 400 del binder.</summary>
public sealed record StageRequest(Guid? Id, string? Name, string? Kind, int? Probability, int? StallDays, bool IsQuotationStage);

public sealed record ReasonRequest(Guid? Id, string? Name);

internal static class PipelineMapping
{
    public static PipelineStagesDto ToDto(SalesPipeline pipeline) =>
        new(pipeline.Id, pipeline.Version, [.. pipeline.OrderedStages.Select(ToDto)]);

    public static StageDto ToDto(PipelineStage stage) =>
        new(stage.Id, stage.Name, stage.Position, stage.Kind.ToString(), stage.Probability, stage.StallDays, stage.IsQuotationStage);

    public static ReasonListDto ToDto(ReasonKind kind, IEnumerable<PipelineReason> reasons) =>
        new(kind.ToString(), [.. reasons.Where(reason => reason.IsActive).OrderBy(reason => reason.Position)
            .Select(reason => new ReasonDto(reason.Id, reason.Name, reason.Position))]);
}
```

`src/Modules/Pipeline/Modules.Pipeline.Application/Settings.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pipeline.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pipeline.Application;

public sealed record GetPipelineStagesQuery(Guid TenantId) : IQuery<PipelineStagesDto>;

public sealed record ReplacePipelineStagesCommand(Guid TenantId, long? Version, IReadOnlyList<StageRequest>? Stages) : ICommand<PipelineStagesDto>;

public sealed record GetPipelineReasonsQuery(Guid TenantId, ReasonKind Kind) : IQuery<ReasonListDto>;

public sealed record ReplacePipelineReasonsCommand(Guid TenantId, ReasonKind Kind, IReadOnlyList<ReasonRequest>? Items) : ICommand<ReasonListDto>;

/// <summary>Texto libre ⇒ validador, aunque el dominio valide (CLAUDE.md): el validador da el campo, el dominio el código
/// (<c>pipeline.stage.layout_invalid</c>, <c>has_deals</c>).</summary>
public sealed class ReplacePipelineStagesValidator : AbstractValidator<ReplacePipelineStagesCommand>
{
    public ReplacePipelineStagesValidator()
    {
        RuleFor(command => command.Version).NotNull();
        RuleFor(command => command.Stages).NotEmpty();
        RuleForEach(command => command.Stages)
            .NotNull()
            .ChildRules(stage =>
            {
                stage.RuleFor(item => item.Name).NotEmpty().MaximumLength(SalesPipeline.StageNameMaxLength);
                stage.RuleFor(item => item.Kind)
                    .Must(kind => Enum.TryParse<StageKind>(kind, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed))
                    .WithMessage("Kind must be Open, Won or Lost.");
                stage.RuleFor(item => item.Probability).NotNull().InclusiveBetween(0, 100);
            });
    }
}

public sealed class ReplacePipelineReasonsValidator : AbstractValidator<ReplacePipelineReasonsCommand>
{
    public ReplacePipelineReasonsValidator()
    {
        RuleFor(command => command.Items).NotEmpty();
        RuleForEach(command => command.Items)
            .NotNull()
            .ChildRules(item => item.RuleFor(reason => reason.Name).NotEmpty().MaximumLength(PipelineReason.NameMaxLength));
    }
}

public sealed class GetPipelineStagesHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, IPipelineProvisioner provisioner)
    : IQueryHandler<GetPipelineStagesQuery, PipelineStagesDto>
{
    public async Task<PipelineStagesDto> HandleAsync(GetPipelineStagesQuery query, CancellationToken cancellationToken)
    {
        await PipelineAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, PipelinePermissions.DealRead, cancellationToken);
        return PipelineMapping.ToDto(await provisioner.EnsureAsync(query.TenantId, cancellationToken));
    }
}

/// <summary>D11 y D12: la colección entera, con la versión del embudo. Las etapas con negocios no se borran.</summary>
public sealed class ReplacePipelineStagesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IPipelineProvisioner provisioner,
    IDealRepository deals,
    IPipelineUnitOfWork unitOfWork,
    IPipelineAuditRecorder audit,
    IClock clock,
    IValidator<ReplacePipelineStagesCommand> validator)
    : ICommandHandler<ReplacePipelineStagesCommand, PipelineStagesDto>
{
    public async Task<PipelineStagesDto> HandleAsync(ReplacePipelineStagesCommand command, CancellationToken cancellationToken)
    {
        await PipelineAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, PipelinePermissions.SettingsManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var pipeline = await provisioner.EnsureAsync(command.TenantId, cancellationToken);
        PipelineVersions.Ensure(pipeline.Version, command.Version);
        // El validador ya garantizó nombre, tipo y probabilidad: estas conversiones no lanzan.
        var inputs = command.Stages!
            .Select(stage => new StageInput(stage.Id, stage.Name!, Enum.Parse<StageKind>(stage.Kind!), stage.Probability!.Value, stage.StallDays, stage.IsQuotationStage))
            .ToArray();
        var stagesWithDeals = await deals.StagesWithDealsAsync(command.TenantId, pipeline.Id, cancellationToken);
        var now = clock.UtcNow;

        pipeline.ReplaceStages(inputs, stagesWithDeals, now);
        audit.Record(command.TenantId, executionContext.SubjectId, false, PipelineAuditActions.StagesReplaced, PipelineAuditActions.SettingsResource, pipeline.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return PipelineMapping.ToDto(pipeline);
    }
}

public sealed class GetPipelineReasonsHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, IPipelineProvisioner provisioner, IPipelineReasonRepository reasons)
    : IQueryHandler<GetPipelineReasonsQuery, ReasonListDto>
{
    public async Task<ReasonListDto> HandleAsync(GetPipelineReasonsQuery query, CancellationToken cancellationToken)
    {
        await PipelineAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, PipelinePermissions.DealRead, cancellationToken);
        // D4: la semilla de los catálogos viaja con el embudo.
        await provisioner.EnsureAsync(query.TenantId, cancellationToken);
        return PipelineMapping.ToDto(query.Kind, await reasons.ListAsync(query.TenantId, query.Kind, cancellationToken));
    }
}

/// <summary>P8 del plan: reemplazo entero, sin versión; lo que deja de venir se desactiva (el historial lo nombra).</summary>
public sealed class ReplacePipelineReasonsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IPipelineProvisioner provisioner,
    IPipelineReasonRepository reasons,
    IPipelineUnitOfWork unitOfWork,
    IPipelineAuditRecorder audit,
    IClock clock,
    IValidator<ReplacePipelineReasonsCommand> validator)
    : ICommandHandler<ReplacePipelineReasonsCommand, ReasonListDto>
{
    public async Task<ReasonListDto> HandleAsync(ReplacePipelineReasonsCommand command, CancellationToken cancellationToken)
    {
        await PipelineAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, PipelinePermissions.SettingsManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var pipeline = await provisioner.EnsureAsync(command.TenantId, cancellationToken);
        var current = await reasons.ListAsync(command.TenantId, command.Kind, cancellationToken);
        var now = clock.UtcNow;
        var added = ReasonCatalog.Replace(
            command.TenantId, command.Kind, current, [.. command.Items!.Select(item => new ReasonInput(item.Id, item.Name!))], now);
        foreach (var reason in added)
        {
            reasons.Add(reason);
        }

        var action = command.Kind == ReasonKind.Loss ? PipelineAuditActions.LossReasonsReplaced : PipelineAuditActions.DismissReasonsReplaced;
        audit.Record(command.TenantId, executionContext.SubjectId, false, action, PipelineAuditActions.SettingsResource, pipeline.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return PipelineMapping.ToDto(command.Kind, current.Concat(added));
    }
}
```

`src/Modules/Pipeline/Modules.Pipeline.Api/SettingsEndpoints.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.Api;

/// <summary>Spec 2026-10-13 §6: los ajustes del embudo. Leer con <c>pipeline.deal.read</c> (el tablero necesita las etapas y el
/// modal de pérdida, los motivos); cambiar con <c>pipeline.settings.manage</c>.</summary>
internal static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this RouteGroupBuilder pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var settings = pipeline.MapGroup("/settings");

        settings.MapGet("/stages", GetStagesAsync)
            .RequireAuthorization(PipelinePermissions.DealRead)
            .Produces<PipelineStagesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        settings.MapPut("/stages", ReplaceStagesAsync)
            .RequireAuthorization(PipelinePermissions.SettingsManage)
            .Accepts<ReplaceStagesRequest>("application/json")
            .Produces<PipelineStagesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        settings.MapGet("/loss-reasons", (Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                GetReasonsAsync(tenantId, ReasonKind.Loss, dispatcher, cancellationToken))
            .RequireAuthorization(PipelinePermissions.DealRead)
            .Produces<ReasonListDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        settings.MapPut("/loss-reasons", (Guid tenantId, ReplaceReasonsRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                ReplaceReasonsAsync(tenantId, ReasonKind.Loss, request, dispatcher, cancellationToken))
            .RequireAuthorization(PipelinePermissions.SettingsManage)
            .Accepts<ReplaceReasonsRequest>("application/json")
            .Produces<ReasonListDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        settings.MapGet("/dismiss-reasons", (Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                GetReasonsAsync(tenantId, ReasonKind.Dismiss, dispatcher, cancellationToken))
            .RequireAuthorization(PipelinePermissions.DealRead)
            .Produces<ReasonListDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        settings.MapPut("/dismiss-reasons", (Guid tenantId, ReplaceReasonsRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                ReplaceReasonsAsync(tenantId, ReasonKind.Dismiss, request, dispatcher, cancellationToken))
            .RequireAuthorization(PipelinePermissions.SettingsManage)
            .Accepts<ReplaceReasonsRequest>("application/json")
            .Produces<ReasonListDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> GetStagesAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetPipelineStagesQuery(tenantId), cancellationToken));

    private static async Task<IResult> ReplaceStagesAsync(
        Guid tenantId, ReplaceStagesRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ReplacePipelineStagesCommand(tenantId, request.Version, request.Stages), cancellationToken));

    private static async Task<IResult> GetReasonsAsync(Guid tenantId, ReasonKind kind, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetPipelineReasonsQuery(tenantId, kind), cancellationToken));

    private static async Task<IResult> ReplaceReasonsAsync(
        Guid tenantId, ReasonKind kind, ReplaceReasonsRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ReplacePipelineReasonsCommand(tenantId, kind, request.Items), cancellationToken));
}

/// <summary>§6: la disposición entera y ordenada con la versión del embudo. Todo opcional: lo que falte sale 422 con su clave.</summary>
public sealed record ReplaceStagesRequest(long? Version, IReadOnlyList<StageRequest>? Stages);

/// <summary>P8 del plan: el catálogo entero, sin versión.</summary>
public sealed record ReplaceReasonsRequest(IReadOnlyList<ReasonRequest>? Items);
```

`src/Modules/Pipeline/Modules.Pipeline.Api/PipelineEndpoints.cs`, completo:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Modules.Pipeline.Api;

public static class PipelineEndpoints
{
    /// <summary>Spec 2026-10-13 §6: todo bajo <c>/api/v1/tenants/{tenantId:guid}/pipeline</c>. Cada ruta declara su
    /// <c>RequireAuthorization</c>; los handlers revalidan tenant, permiso y capacidad (403 con código, nunca 404 ante otro tenant).</summary>
    public static IEndpointRouteBuilder MapPipelineEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/tenants/{tenantId:guid}/pipeline").WithTags("Pipeline");
        group.MapSettingsEndpoints();
        return endpoints;
    }
}
```

`QepServiceCollectionExtensions.cs`, después de `services.AddScoped<IQueryHandler<ListAssigneesQuery, AssigneesDto>, ListAssigneesHandler>();`:

```csharp
        // Pipeline (spec 2026-10-13 §6): ajustes. Cada handler a mano: sin registro, 500 (CompositionRootTests).
        services.AddValidatorsFromAssemblyContaining<ReplacePipelineStagesValidator>();
        services.AddScoped<IQueryHandler<GetPipelineStagesQuery, PipelineStagesDto>, GetPipelineStagesHandler>();
        services.AddScoped<ICommandHandler<ReplacePipelineStagesCommand, PipelineStagesDto>, ReplacePipelineStagesHandler>();
        services.AddScoped<IQueryHandler<GetPipelineReasonsQuery, ReasonListDto>, GetPipelineReasonsHandler>();
        services.AddScoped<ICommandHandler<ReplacePipelineReasonsCommand, ReasonListDto>, ReplacePipelineReasonsHandler>();
```

- [ ] **Step 4: Correr y verificar que pasan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.IntegrationTests\Modules.Pipeline.IntegrationTests.csproj" --no-build --filter "FullyQualifiedName~SettingsApiTests"
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --no-build --filter "FullyQualifiedName~PipelineLayerTests|FullyQualifiedName~CompositionRootTests"
```

Esperado: 7 verdes en `SettingsApiTests`; capas y composición verdes (los cuatro mensajes nuevos tienen handler).

- [ ] **Step 5: Commit**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
if ((git -C $B branch --show-current) -ne "feature/embudo-de-ventas") { throw "rama equivocada" }
git -C $B status --short
git -C $B add src/Modules/Pipeline/Modules.Pipeline.Application/PipelineSupport.cs src/Modules/Pipeline/Modules.Pipeline.Application/PipelineDtos.cs src/Modules/Pipeline/Modules.Pipeline.Application/Settings.cs src/Modules/Pipeline/Modules.Pipeline.Api/SettingsEndpoints.cs src/Modules/Pipeline/Modules.Pipeline.Api/PipelineEndpoints.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/SettingsApiTests.cs
git -C $B commit -m "feat(pipeline): ajustes de etapas y catálogos de motivos"
git -C $B log -1 --format=%B
```

---

### Task 9: Negocios — crear, leer, editar, mover, ganar, perder y reabrir

**Files:**
- Create: `src/Modules/Pipeline/Modules.Pipeline.Application/DealDetailBuilder.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Application/Deals.cs`
- Modify: `src/Modules/Pipeline/Modules.Pipeline.Application/PipelineDtos.cs` (al final)
- Create: `src/Modules/Pipeline/Modules.Pipeline.Api/DealEndpoints.cs`
- Modify: `src/Modules/Pipeline/Modules.Pipeline.Api/PipelineEndpoints.cs` (`group.MapDealEndpoints();`)
- Modify: `src/Modules/Pipeline/Modules.Pipeline.Infrastructure/PipelineInfrastructureExtensions.cs` (`PipelineCaller`, `DealDetailBuilder`, `DealCommandSteps`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (handlers, después de los de ajustes)
- Test: `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/DealApiTests.cs`

**Interfaces:**
- Consumes: Tasks 4–8 (`Deal`, `SalesPipeline`, `StallRules`, puertos, directorios, `PipelineAuthorization`, `PipelineCaller`, `PipelineNotFound`, `PipelineVersions`), `ITenantDefaultCurrency`, `Currencies`.
- Produces:
  - DTOs (contrato): `DealQuotationDto(Guid QuotationId, string QuotationNumber, decimal Total, string Currency, string Status, bool IsPrimary, Guid? OrderId, bool OrderCancelled)`, `StageHistoryDto(Guid? FromStageId, string? FromStageName, Guid ToStageId, string? ToStageName, DateTimeOffset OccurredAt, string ActorKind, Guid? ActorMemberId, string? ActorName, decimal? Amount, string Currency, int Probability)`, `DealDetailDto(Guid Id, string Title, Guid CustomerId, string CustomerName, bool CustomerIsComplete, Guid OwnerMemberId, string OwnerName, decimal? Amount, string Currency, bool AmountIsEditable, DateOnly? ExpectedCloseDate, Guid StageId, string StageName, string StageKind, int DaysInStage, bool IsStalled, string Source, Guid? OriginConversationId, Guid? LossReasonId, string? LossReasonName, string? LossNote, DateTimeOffset? ClosedAt, Guid? PrimaryQuotationId, IReadOnlyList<DealQuotationDto> Quotations, IReadOnlyList<DealQuotationDto> UnlinkedCustomerQuotations, IReadOnlyList<Guid> ConversationIds, IReadOnlyList<StageHistoryDto> History, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long Version)`.
  - `public sealed class DealDetailBuilder { const string DeletedMemberName = "Miembro eliminado"; const string UnknownCustomerName = "Cliente no disponible"; Task<DealDetailDto> BuildAsync(SalesPipeline pipeline, Deal deal, CancellationToken) }` (lo reusan las Tasks 10, 13 y 14).
  - `public sealed class DealCommandSteps { Task AuthorizeAsync(Guid tenantId, CancellationToken); Task<DealCommandContext> LoadAsync(Guid tenantId, Guid dealId, long? version, CancellationToken); Task<DealDetailDto> CommitAsync(DealCommandContext context, string? auditAction, CancellationToken) }` y `public sealed record DealCommandContext(SalesPipeline Pipeline, Deal Deal, DealActor Actor, DateTimeOffset Now)` (los reusa la Task 10).
  - Mensajes: `GetDealQuery(Guid TenantId, Guid DealId)`, `CreateDealCommand(Guid TenantId, Guid? CustomerId, string? Title, decimal? Amount, string? Currency, DateOnly? ExpectedCloseDate, Guid? OwnerMemberId)`, `UpdateDealCommand(Guid TenantId, Guid DealId, string? Title, Guid? OwnerMemberId, decimal? Amount, string? Currency, DateOnly? ExpectedCloseDate, long? Version)`, `MoveDealCommand(Guid TenantId, Guid DealId, Guid? StageId, long? Version)`, `WinDealCommand(Guid TenantId, Guid DealId, long? Version)`, `LoseDealCommand(Guid TenantId, Guid DealId, Guid? LossReasonId, string? Note, long? Version)`, `ReopenDealCommand(Guid TenantId, Guid DealId, long? Version)`, todos `→ DealDetailDto`, con sus handlers y validadores.
  - Api: `CreateDealRequest(Guid? CustomerId, string? Title, decimal? Amount, string? Currency, DateOnly? ExpectedCloseDate, Guid? OwnerMemberId)`, `UpdateDealRequest(string? Title, Guid? OwnerMemberId, decimal? Amount, string? Currency, DateOnly? ExpectedCloseDate, long? Version)`, `MoveDealRequest(Guid? StageId, long? Version)`, `DealVersionRequest(long? Version)`, `LoseDealRequest(Guid? LossReasonId, string? Note, long? Version)`; `DealEndpoints.MapDealEndpoints(this RouteGroupBuilder)` (la Task 10 le agrega dos rutas).

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/DealApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Modules.Pipeline.Api;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;
using static Modules.Pipeline.IntegrationTests.PipelineApiHarness;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>Spec 2026-10-13 §4.2, §6, D13–D16, D19, D23 y D34: el ciclo de un negocio por la API, con el detalle completo en cada
/// respuesta (BFF), su historial, su auditoría y las fronteras de tenant, permiso, capacidad y versión.</summary>
public sealed class DealApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, HttpClient Client, Guid CustomerId, string CustomerName, Guid OwnerMemberId);

    private static string DealsUrl(Guid tenantId) => $"{PipelineUrl(tenantId)}/deals";

    private static async Task<Setup> SetupAsync(QepApiFactory factory, string connectionString, params string[] permissions)
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        var (customerId, customerName) = await SeedCustomerAsync(factory, tenant.TenantId, "Laura");
        var owner = await OwnerMembershipIdAsync(connectionString, tenant);
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, permissions.Length == 0 ? ManagePermissions : permissions);
        return new Setup(factory, connectionString, tenant, client, customerId, customerName, owner);
    }

    private static async Task<DealDetailDto> CreateAsync(Setup setup, decimal? amount = 1_500_000m)
    {
        var response = await setup.Client.PostAsJsonAsync(
            DealsUrl(setup.Tenant.TenantId), new CreateDealRequest(setup.CustomerId, "Dotación sede norte", amount, null, null, null), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;
    }

    private static async Task<DealDetailDto> PostAsync(Setup setup, string url, object body)
    {
        var response = await setup.Client.PostAsJsonAsync(url, body, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;
    }

    private static async Task<Guid> StageIdAsync(Setup setup, string name) =>
        (await setup.Client.GetFromJsonAsync<PipelineStagesDto>($"{PipelineUrl(setup.Tenant.TenantId)}/settings/stages", Ct))!
            .Stages.Single(stage => stage.Name == name).Id;

    [Fact]
    public async Task CreatingADealReturnsTheFullDetailInTheFirstStageAndAudits()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;

        var deal = await CreateAsync(setup);

        Assert.Equal(("Dotación sede norte", setup.CustomerId, setup.CustomerName, false), (deal.Title, deal.CustomerId, deal.CustomerName, deal.CustomerIsComplete));
        Assert.Equal((setup.OwnerMemberId, (decimal?)1_500_000m, "COP", true), (deal.OwnerMemberId, deal.Amount, deal.Currency, deal.AmountIsEditable));
        Assert.Equal(("Nuevo", "Open", "Manual", 0, false, 1L), (deal.StageName, deal.StageKind, deal.Source, deal.DaysInStage, deal.IsStalled, deal.Version));
        Assert.False(string.IsNullOrWhiteSpace(deal.OwnerName));
        var row = Assert.Single(deal.History);
        Assert.Equal(((string?)null, "Nuevo", "User", (Guid?)setup.OwnerMemberId), (row.FromStageName, row.ToStageName, row.ActorKind, row.ActorMemberId));
        Assert.Equal(1L, await CountAsync(setup.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE resource_id = @id AND action = 'pipeline.deal.created'", ("id", deal.Id.ToString())));
    }

    // D34: sin monto cuenta, no suma; la moneda es la del tenant.
    [Fact]
    public async Task ADealCanBeCreatedWithoutAmount()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;

        var deal = await CreateAsync(setup, amount: null);

        Assert.Equal(((decimal?)null, "COP"), (deal.Amount, deal.Currency));
    }

    [Fact]
    public async Task ABadCreateIs422WithItsCode()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (foreignCustomer, _) = await SeedCustomerAsync(factory, Guid.CreateVersion7(), "Otro");
        var url = DealsUrl(setup.Tenant.TenantId);

        var foreign = await setup.Client.PostAsJsonAsync(url, new CreateDealRequest(foreignCustomer, "Dotación", null, null, null, null), Ct);
        var stranger = await setup.Client.PostAsJsonAsync(url, new CreateDealRequest(setup.CustomerId, "Dotación", null, null, null, Guid.CreateVersion7()), Ct);
        var blank = await setup.Client.PostAsJsonAsync(url, new CreateDealRequest(setup.CustomerId, "  ", null, "XYZ", null, null), Ct);

        Assert.Equal(PipelineErrorCodes.DealCustomerNotFound, (await ProblemAsync(foreign)).Code);
        Assert.Equal(PipelineErrorCodes.DealOwnerNotMember, (await ProblemAsync(stranger)).Code);
        var (code, keys) = await ProblemAsync(blank);
        Assert.Equal("validation.failed", code);
        Assert.Equal(["currency", "title"], keys, StringComparer.OrdinalIgnoreCase);
    }

    // Review Focus 4: un negocio de otro tenant bajo la ruta propia es 404 (no confirma que existe); la ruta ajena es 403.
    [Fact]
    public async Task AForeignDealIs404AndAnotherTenantsRouteIs403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setupA = await SetupAsync(factory, connectionString);
        var setupB = await SetupAsync(factory, connectionString);
        using var a = setupA.Client;
        using var b = setupB.Client;
        var deal = await CreateAsync(setupA);

        var underOwnRoute = await b.GetAsync($"{DealsUrl(setupB.Tenant.TenantId)}/{deal.Id}", Ct);
        var underForeignRoute = await b.GetAsync($"{DealsUrl(setupA.Tenant.TenantId)}/{deal.Id}", Ct);
        var fetched = await a.GetFromJsonAsync<DealDetailDto>($"{DealsUrl(setupA.Tenant.TenantId)}/{deal.Id}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, underOwnRoute.StatusCode);
        Assert.Equal(PipelineErrorCodes.DealNotFound, (await ProblemAsync(underOwnRoute)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, underForeignRoute.StatusCode);
        Assert.Equal(deal.Id, fetched!.Id);
    }

    // D13–D15 y D19: libre entre abiertas, perder con motivo del catálogo, reabrir a donde estaba y ganar; cada paso con su fila.
    [Fact]
    public async Task MoveLoseReopenAndWinWalkTheDealAndKeepTheHistory()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var deal = await CreateAsync(setup);
        var url = $"{DealsUrl(setup.Tenant.TenantId)}/{deal.Id}";
        var negotiation = await StageIdAsync(setup, "Negociación");
        var price = (await setup.Client.GetFromJsonAsync<ReasonListDto>($"{PipelineUrl(setup.Tenant.TenantId)}/settings/loss-reasons", Ct))!
            .Items.Single(item => item.Name == "Precio").Id;

        var moved = await PostAsync(setup, $"{url}/move", new MoveDealRequest(negotiation, deal.Version));
        var withoutReason = await setup.Client.PostAsJsonAsync($"{url}/lose", new LoseDealRequest(null, null, moved.Version), Ct);
        var unknownReason = await setup.Client.PostAsJsonAsync($"{url}/lose", new LoseDealRequest(Guid.CreateVersion7(), null, moved.Version), Ct);
        var lost = await PostAsync(setup, $"{url}/lose", new LoseDealRequest(price, " Se fue con la competencia ", moved.Version));
        var reopened = await PostAsync(setup, $"{url}/reopen", new DealVersionRequest(lost.Version));
        var won = await PostAsync(setup, $"{url}/win", new DealVersionRequest(reopened.Version));

        Assert.Equal("Negociación", moved.StageName);
        Assert.Equal(PipelineErrorCodes.DealLossReasonRequired, (await ProblemAsync(withoutReason)).Code);
        Assert.Equal(PipelineErrorCodes.DealLossReasonNotFound, (await ProblemAsync(unknownReason)).Code);
        Assert.Equal(("Perdido", "Precio", "Se fue con la competencia"), (lost.StageName, lost.LossReasonName, lost.LossNote));
        Assert.NotNull(lost.ClosedAt);
        Assert.Equal(("Negociación", (DateTimeOffset?)null, (string?)null), (reopened.StageName, reopened.ClosedAt, reopened.LossReasonName));
        Assert.Equal(("Ganado", "Won"), (won.StageName, won.StageKind));
        Assert.Equal(["Nuevo", "Negociación", "Perdido", "Negociación", "Ganado"], won.History.Select(row => row.ToStageName));
        Assert.Equal(4L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE resource_id = @id AND action IN ('pipeline.deal.moved','pipeline.deal.lost','pipeline.deal.reopened','pipeline.deal.won')",
            ("id", deal.Id.ToString())));
    }

    [Fact]
    public async Task MovingToAClosingStageIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;
        var deal = await CreateAsync(setup);

        var response = await setup.Client.PostAsJsonAsync(
            $"{DealsUrl(setup.Tenant.TenantId)}/{deal.Id}/move", new MoveDealRequest(await StageIdAsync(setup, "Ganado"), deal.Version), Ct);

        Assert.Equal(PipelineErrorCodes.DealStageNotOpen, (await ProblemAsync(response)).Code);
    }

    // Review Focus 3: el worker (u otra pestaña) tocó el negocio después de que la pantalla lo cargó.
    [Fact]
    public async Task AStaleVersionIs412AndNothingChanges()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var deal = await CreateAsync(setup);
        await ExecuteAsync(connectionString, "UPDATE pipeline.deals SET version = version + 1 WHERE id = @id", ("id", deal.Id));
        var url = $"{DealsUrl(setup.Tenant.TenantId)}/{deal.Id}";

        var stale = await setup.Client.PostAsJsonAsync($"{url}/move", new MoveDealRequest(await StageIdAsync(setup, "Calificado"), deal.Version), Ct);
        var noVersion = await setup.Client.PostAsJsonAsync($"{url}/win", new DealVersionRequest(null), Ct);

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(["version"], (await ProblemAsync(noVersion)).ErrorKeys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Nuevo", (await setup.Client.GetFromJsonAsync<DealDetailDto>(url, Ct))!.StageName);
    }

    // P5 del plan: el PATCH lleva todos los editables y los reemplaza.
    [Fact]
    public async Task PatchReplacesTheEditableFields()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var deal = await CreateAsync(setup);
        var (member, _) = await SeedMemberAsync(connectionString, setup.Tenant.TenantId, "Laura Gómez");

        var response = await setup.Client.PatchAsJsonAsync(
            $"{DealsUrl(setup.Tenant.TenantId)}/{deal.Id}",
            new UpdateDealRequest(" Dotación 2027 ", member, 2_500m, "usd", new DateOnly(2026, 12, 15), deal.Version),
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var patched = (await response.Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;
        Assert.Equal(("Dotación 2027", member, "Laura Gómez"), (patched.Title, patched.OwnerMemberId, patched.OwnerName));
        Assert.Equal(((decimal?)2_500m, "USD", (DateOnly?)new DateOnly(2026, 12, 15), 2L), (patched.Amount, patched.Currency, patched.ExpectedCloseDate, patched.Version));
    }

    [Fact]
    public async Task WithoutManageTheWritesAre403AndWithoutTheCapabilityModuleNotEnabled()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        using var reader = CreateClient(factory, setup.Tenant.OwnerUserId, setup.Tenant.TenantId, ReadPermissions);

        var forbidden = await reader.PostAsJsonAsync(DealsUrl(setup.Tenant.TenantId), new CreateDealRequest(setup.CustomerId, "Dotación", null, null, null, null), Ct);
        await DisableModuleAsync(connectionString, setup.Tenant.TenantId, "pipeline");
        var disabled = await setup.Client.PostAsJsonAsync(DealsUrl(setup.Tenant.TenantId), new CreateDealRequest(setup.CustomerId, "Dotación", null, null, null, null), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(disabled)).Code);
    }
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.IntegrationTests\Modules.Pipeline.IntegrationTests.csproj" --filter "FullyQualifiedName~DealApiTests"
```

Esperado: `error CS0246` sobre `DealDetailDto`, `CreateDealRequest`, `MoveDealRequest`, `LoseDealRequest`, `DealVersionRequest`, `UpdateDealRequest`.

- [ ] **Step 3: Implementar los DTO, el armado del detalle, los pasos comunes y los handlers**

Al final de `PipelineDtos.cs`:

```csharp
public sealed record DealQuotationDto(
    Guid QuotationId, string QuotationNumber, decimal Total, string Currency, string Status, bool IsPrimary, Guid? OrderId, bool OrderCancelled);

/// <summary>D19: una fila del historial con los nombres ya resueltos (una etapa borrada o un miembro que ya no está salen sin
/// nombre o con «Miembro eliminado»).</summary>
public sealed record StageHistoryDto(
    Guid? FromStageId, string? FromStageName, Guid ToStageId, string? ToStageName, DateTimeOffset OccurredAt, string ActorKind,
    Guid? ActorMemberId, string? ActorName, decimal? Amount, string Currency, int Probability);

/// <summary>
/// BFF (§6): toda escritura sobre un negocio devuelve esto entero, y la pantalla lo repinta sin pedir nada más. Viajan resueltos
/// el nombre del cliente y su completitud, el del dueño, el de la etapa, los días en la etapa, el estancado, si el monto se puede
/// editar (D18: no con principal), las cotizaciones enlazadas y las del cliente sin enlazar (para el <c>link</c>), los chats del
/// cliente (D21) y el historial: la pantalla no ve membresías, conversaciones ni etapas por su cuenta.
/// </summary>
public sealed record DealDetailDto(
    Guid Id,
    string Title,
    Guid CustomerId,
    string CustomerName,
    bool CustomerIsComplete,
    Guid OwnerMemberId,
    string OwnerName,
    decimal? Amount,
    string Currency,
    bool AmountIsEditable,
    DateOnly? ExpectedCloseDate,
    Guid StageId,
    string StageName,
    string StageKind,
    int DaysInStage,
    bool IsStalled,
    string Source,
    Guid? OriginConversationId,
    Guid? LossReasonId,
    string? LossReasonName,
    string? LossNote,
    DateTimeOffset? ClosedAt,
    Guid? PrimaryQuotationId,
    IReadOnlyList<DealQuotationDto> Quotations,
    IReadOnlyList<DealQuotationDto> UnlinkedCustomerQuotations,
    IReadOnlyList<Guid> ConversationIds,
    IReadOnlyList<StageHistoryDto> History,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version);
```

`src/Modules/Pipeline/Modules.Pipeline.Application/DealDetailBuilder.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.Application;

/// <summary>§6: arma <see cref="DealDetailDto"/> con una llamada a cada directorio. Público sólo para que Infrastructure lo
/// registre; no es API para otros módulos.</summary>
public sealed class DealDetailBuilder(
    IPipelineCustomerDirectory customers,
    IPipelineMemberNames members,
    IPipelineConversationDirectory conversations,
    IDealRepository deals,
    IDealQuotationRepository quotations,
    IPipelineReasonRepository reasons,
    IClock clock)
{
    public const string DeletedMemberName = "Miembro eliminado";
    public const string UnknownCustomerName = "Cliente no disponible";

    public async Task<DealDetailDto> BuildAsync(SalesPipeline pipeline, Deal deal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(deal);
        var tenantId = deal.TenantId;
        var history = await deals.ListHistoryAsync(tenantId, deal.Id, cancellationToken);
        var linked = await quotations.ListByDealAsync(tenantId, deal.Id, cancellationToken);
        var unlinked = await quotations.ListUnlinkedByCustomerAsync(tenantId, deal.CustomerId, cancellationToken);
        var customer = (await customers.FindAsync(tenantId, [deal.CustomerId], cancellationToken)).GetValueOrDefault(deal.CustomerId);
        var memberIds = history
            .Where(row => row.ActorMemberId is not null)
            .Select(row => row.ActorMemberId!.Value)
            .Append(deal.OwnerMemberId)
            .Distinct()
            .ToArray();
        var names = await members.FindAsync(tenantId, memberIds, cancellationToken);
        var activity = (await conversations.FindActivityAsync(tenantId, [deal.CustomerId], cancellationToken)).GetValueOrDefault(deal.CustomerId);
        var reasonName = deal.LossReasonId is { } reasonId
            ? (await reasons.FindNamesAsync(tenantId, [reasonId], cancellationToken)).GetValueOrDefault(reasonId)
            : null;
        var stage = pipeline.FindStage(deal.StageId);
        var now = clock.UtcNow;
        var lastActivity = StallRules.LastActivity(deal.LastChangedAt, activity?.LastMessageAt);

        return new DealDetailDto(
            deal.Id,
            deal.Title,
            deal.CustomerId,
            customer?.Name ?? UnknownCustomerName,
            customer?.IsComplete ?? false,
            deal.OwnerMemberId,
            names.GetValueOrDefault(deal.OwnerMemberId) ?? DeletedMemberName,
            deal.Amount,
            deal.Currency,
            deal.PrimaryQuotationId is null,
            deal.ExpectedCloseDate,
            deal.StageId,
            stage?.Name ?? string.Empty,
            (stage?.Kind ?? StageKind.Open).ToString(),
            StallRules.DaysIn(deal.StageEnteredAt, now),
            stage is not null && StallRules.IsStalled(stage.Kind, stage.StallDays, lastActivity, now),
            deal.Source.ToString(),
            deal.OriginConversationId,
            deal.LossReasonId,
            reasonName,
            deal.LossNote,
            deal.ClosedAt,
            deal.PrimaryQuotationId,
            [.. linked.Select(quotation => ToDto(quotation, deal.PrimaryQuotationId))],
            [.. unlinked.Select(quotation => ToDto(quotation, null))],
            activity?.ConversationIds ?? [],
            [.. history.Select(row => ToDto(row, pipeline, names))],
            deal.CreatedAt,
            deal.UpdatedAt,
            deal.Version);
    }

    internal static DealQuotationDto ToDto(DealQuotation quotation, Guid? primaryQuotationId) =>
        new(quotation.QuotationId, quotation.QuotationNumber, quotation.Total, quotation.Currency, quotation.Status.ToString(),
            quotation.QuotationId == primaryQuotationId, quotation.OrderId, quotation.OrderCancelledAt is not null);

    private static StageHistoryDto ToDto(DealStageHistoryEntry row, SalesPipeline pipeline, IReadOnlyDictionary<Guid, string> names) =>
        new(row.FromStageId,
            row.FromStageId is { } from ? pipeline.FindStage(from)?.Name : null,
            row.ToStageId,
            pipeline.FindStage(row.ToStageId)?.Name,
            row.OccurredAt,
            row.ActorKind.ToString(),
            row.ActorMemberId,
            row.ActorMemberId is { } member ? names.GetValueOrDefault(member) ?? DeletedMemberName : null,
            row.Amount,
            row.Currency,
            row.Probability);
}
```

`src/Modules/Pipeline/Modules.Pipeline.Application/Deals.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pipeline.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pipeline.Application;

public sealed record GetDealQuery(Guid TenantId, Guid DealId) : IQuery<DealDetailDto>;

public sealed record CreateDealCommand(
    Guid TenantId, Guid? CustomerId, string? Title, decimal? Amount, string? Currency, DateOnly? ExpectedCloseDate, Guid? OwnerMemberId)
    : ICommand<DealDetailDto>;

public sealed record UpdateDealCommand(
    Guid TenantId, Guid DealId, string? Title, Guid? OwnerMemberId, decimal? Amount, string? Currency, DateOnly? ExpectedCloseDate, long? Version)
    : ICommand<DealDetailDto>;

public sealed record MoveDealCommand(Guid TenantId, Guid DealId, Guid? StageId, long? Version) : ICommand<DealDetailDto>;

public sealed record WinDealCommand(Guid TenantId, Guid DealId, long? Version) : ICommand<DealDetailDto>;

public sealed record LoseDealCommand(Guid TenantId, Guid DealId, Guid? LossReasonId, string? Note, long? Version) : ICommand<DealDetailDto>;

public sealed record ReopenDealCommand(Guid TenantId, Guid DealId, long? Version) : ICommand<DealDetailDto>;

public sealed record DealCommandContext(SalesPipeline Pipeline, Deal Deal, DealActor Actor, DateTimeOffset Now);

/// <summary>Texto libre ⇒ validador (CLAUDE.md): el validador da el campo; el dominio, el código.</summary>
public sealed class CreateDealValidator : AbstractValidator<CreateDealCommand>
{
    public CreateDealValidator()
    {
        RuleFor(command => command.CustomerId).NotEmpty();
        RuleFor(command => command.Title).NotEmpty().MaximumLength(Deal.TitleMaxLength);
        RuleFor(command => command.Amount).GreaterThanOrEqualTo(0m).When(command => command.Amount is not null);
        RuleFor(command => command.Currency).Must(Currencies.IsSupported).When(command => command.Currency is not null)
            .WithMessage("Unsupported currency.");
    }
}

public sealed class UpdateDealValidator : AbstractValidator<UpdateDealCommand>
{
    public UpdateDealValidator()
    {
        RuleFor(command => command.Title).NotEmpty().MaximumLength(Deal.TitleMaxLength);
        RuleFor(command => command.OwnerMemberId).NotEmpty();
        RuleFor(command => command.Amount).GreaterThanOrEqualTo(0m).When(command => command.Amount is not null);
        RuleFor(command => command.Currency).NotEmpty().Must(Currencies.IsSupported).WithMessage("Unsupported currency.");
        RuleFor(command => command.Version).NotNull();
    }
}

public sealed class MoveDealValidator : AbstractValidator<MoveDealCommand>
{
    public MoveDealValidator()
    {
        RuleFor(command => command.StageId).NotEmpty();
        RuleFor(command => command.Version).NotNull();
    }
}

public sealed class WinDealValidator : AbstractValidator<WinDealCommand>
{
    public WinDealValidator() => RuleFor(command => command.Version).NotNull();
}

/// <summary>P10 del plan: sin motivo NO es <c>validation.failed</c>; el dominio responde <c>pipeline.deal.loss_reason_required</c>.</summary>
public sealed class LoseDealValidator : AbstractValidator<LoseDealCommand>
{
    public LoseDealValidator()
    {
        RuleFor(command => command.Note).MaximumLength(Deal.LossNoteMaxLength);
        RuleFor(command => command.Version).NotNull();
    }
}

public sealed class ReopenDealValidator : AbstractValidator<ReopenDealCommand>
{
    public ReopenDealValidator() => RuleFor(command => command.Version).NotNull();
}

/// <summary>
/// El esqueleto común de las escrituras sobre un negocio existente: autorizar (antes de validar, como el resto del repo), cargar
/// con la versión que la pantalla leyó (412 si cambió), resolver el actor y, al final, auditar, guardar (con el historial, P4) y
/// devolver el detalle entero. Público sólo para que Infrastructure lo registre.
/// </summary>
public sealed class DealCommandSteps(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IPipelineProvisioner provisioner,
    IDealRepository deals,
    PipelineCaller caller,
    IPipelineUnitOfWork unitOfWork,
    IPipelineAuditRecorder audit,
    DealDetailBuilder detail,
    IClock clock)
{
    public Task AuthorizeAsync(Guid tenantId, CancellationToken cancellationToken) =>
        PipelineAuthorization.EnsureAsync(executionContext, tenantModules, tenantId, PipelinePermissions.DealManage, cancellationToken);

    public async Task<DealCommandContext> LoadAsync(Guid tenantId, Guid dealId, long? version, CancellationToken cancellationToken)
    {
        var pipeline = await provisioner.EnsureAsync(tenantId, cancellationToken);
        var deal = await deals.FindAsync(tenantId, dealId, cancellationToken) ?? throw PipelineNotFound.Deal(dealId);
        PipelineVersions.Ensure(deal.Version, version);
        var memberId = await caller.RequireAsync(tenantId, cancellationToken);
        return new DealCommandContext(pipeline, deal, DealActor.ForUser(memberId), clock.UtcNow);
    }

    /// <summary>Sin <paramref name="auditAction"/> no hubo cambio (mover a la misma etapa): no se audita ni se guarda.</summary>
    public async Task<DealDetailDto> CommitAsync(DealCommandContext context, string? auditAction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (auditAction is not null)
        {
            audit.Record(context.Deal.TenantId, executionContext.SubjectId, false, auditAction, PipelineAuditActions.DealResource, context.Deal.Id, context.Now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await detail.BuildAsync(context.Pipeline, context.Deal, cancellationToken);
    }
}

public sealed class GetDealHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, IPipelineProvisioner provisioner, IDealRepository deals, DealDetailBuilder detail)
    : IQueryHandler<GetDealQuery, DealDetailDto>
{
    public async Task<DealDetailDto> HandleAsync(GetDealQuery query, CancellationToken cancellationToken)
    {
        await PipelineAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, PipelinePermissions.DealRead, cancellationToken);
        var pipeline = await provisioner.EnsureAsync(query.TenantId, cancellationToken);
        var deal = await deals.FindAsync(query.TenantId, query.DealId, cancellationToken) ?? throw PipelineNotFound.Deal(query.DealId);
        return await detail.BuildAsync(pipeline, deal, cancellationToken);
    }
}

/// <summary>§4.2: un negocio manual (D34: puede nacer sin monto; la moneda por defecto es la del tenant). El dueño por defecto es
/// quien llama; otro dueño tiene que ser miembro del tenant.</summary>
public sealed class CreateDealHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IPipelineProvisioner provisioner,
    IDealRepository deals,
    IPipelineCustomerDirectory customers,
    IPipelineMemberNames members,
    ITenantDefaultCurrency defaultCurrency,
    PipelineCaller caller,
    IPipelineUnitOfWork unitOfWork,
    IPipelineAuditRecorder audit,
    DealDetailBuilder detail,
    IClock clock,
    IValidator<CreateDealCommand> validator)
    : ICommandHandler<CreateDealCommand, DealDetailDto>
{
    public async Task<DealDetailDto> HandleAsync(CreateDealCommand command, CancellationToken cancellationToken)
    {
        await PipelineAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, PipelinePermissions.DealManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var customerId = command.CustomerId!.Value;
        if (!(await customers.FindAsync(command.TenantId, [customerId], cancellationToken)).ContainsKey(customerId))
        {
            throw new PipelineDomainException(PipelineErrorCodes.DealCustomerNotFound, "The customer was not found in this tenant.");
        }

        var callerId = await caller.RequireAsync(command.TenantId, cancellationToken);
        var owner = command.OwnerMemberId ?? callerId;
        await DealOwners.EnsureMemberAsync(members, command.TenantId, owner, callerId, cancellationToken);
        var currency = command.Currency is null
            ? await defaultCurrency.GetAsync(command.TenantId, cancellationToken)
            : Currencies.Normalize(command.Currency);
        var pipeline = await provisioner.EnsureAsync(command.TenantId, cancellationToken);
        var now = clock.UtcNow;

        var deal = Deal.Create(command.TenantId, pipeline, customerId, owner, command.Title, command.Amount, currency,
            command.ExpectedCloseDate, DealSource.Manual, null, DealActor.ForUser(callerId), now);
        deals.Add(deal);
        audit.Record(command.TenantId, executionContext.SubjectId, false, PipelineAuditActions.DealCreated, PipelineAuditActions.DealResource, deal.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await detail.BuildAsync(pipeline, deal, cancellationToken);
    }
}

internal static class DealOwners
{
    /// <summary>El dueño tiene que ser una membresía del tenant. Quien llama ya lo es (lo resolvió <c>PipelineCaller</c>).</summary>
    public static async Task EnsureMemberAsync(
        IPipelineMemberNames members, Guid tenantId, Guid owner, Guid callerId, CancellationToken cancellationToken)
    {
        if (owner != callerId && !(await members.FindAsync(tenantId, [owner], cancellationToken)).ContainsKey(owner))
        {
            throw new PipelineDomainException(PipelineErrorCodes.DealOwnerNotMember, "The owner is not a member of this tenant.");
        }
    }
}

/// <summary>P5 del plan: reemplaza los editables. Con principal (D18), monto y moneda no cambian (lo dice el dominio).</summary>
public sealed class UpdateDealHandler(
    DealCommandSteps steps, IPipelineMemberNames members, PipelineCaller caller, IValidator<UpdateDealCommand> validator)
    : ICommandHandler<UpdateDealCommand, DealDetailDto>
{
    public async Task<DealDetailDto> HandleAsync(UpdateDealCommand command, CancellationToken cancellationToken)
    {
        await steps.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var context = await steps.LoadAsync(command.TenantId, command.DealId, command.Version, cancellationToken);
        var callerId = await caller.RequireAsync(command.TenantId, cancellationToken);
        await DealOwners.EnsureMemberAsync(members, command.TenantId, command.OwnerMemberId!.Value, callerId, cancellationToken);

        context.Deal.Update(command.Title, command.OwnerMemberId.Value, command.Amount, Currencies.Normalize(command.Currency),
            command.ExpectedCloseDate, context.Now);
        return await steps.CommitAsync(context, PipelineAuditActions.DealUpdated, cancellationToken);
    }
}

/// <summary>D13: libre entre abiertas. A la misma etapa no hay cambio, ni auditoría.</summary>
public sealed class MoveDealHandler(DealCommandSteps steps, IValidator<MoveDealCommand> validator)
    : ICommandHandler<MoveDealCommand, DealDetailDto>
{
    public async Task<DealDetailDto> HandleAsync(MoveDealCommand command, CancellationToken cancellationToken)
    {
        await steps.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var context = await steps.LoadAsync(command.TenantId, command.DealId, command.Version, cancellationToken);
        var moved = context.Deal.MoveTo(context.Pipeline, command.StageId!.Value, context.Actor, context.Now);
        return await steps.CommitAsync(context, moved ? PipelineAuditActions.DealMoved : null, cancellationToken);
    }
}

/// <summary>D14: ganar a mano se permite.</summary>
public sealed class WinDealHandler(DealCommandSteps steps, IValidator<WinDealCommand> validator)
    : ICommandHandler<WinDealCommand, DealDetailDto>
{
    public async Task<DealDetailDto> HandleAsync(WinDealCommand command, CancellationToken cancellationToken)
    {
        await steps.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var context = await steps.LoadAsync(command.TenantId, command.DealId, command.Version, cancellationToken);
        context.Deal.Win(context.Pipeline, context.Actor, context.Now);
        return await steps.CommitAsync(context, PipelineAuditActions.DealWon, cancellationToken);
    }
}

/// <summary>D14: el motivo tiene que ser un motivo de pérdida activo del tenant.</summary>
public sealed class LoseDealHandler(DealCommandSteps steps, IPipelineReasonRepository reasons, IValidator<LoseDealCommand> validator)
    : ICommandHandler<LoseDealCommand, DealDetailDto>
{
    public async Task<DealDetailDto> HandleAsync(LoseDealCommand command, CancellationToken cancellationToken)
    {
        await steps.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var context = await steps.LoadAsync(command.TenantId, command.DealId, command.Version, cancellationToken);
        if (command.LossReasonId is { } reasonId && reasonId != Guid.Empty &&
            await reasons.FindActiveAsync(command.TenantId, ReasonKind.Loss, reasonId, cancellationToken) is null)
        {
            throw new PipelineDomainException(PipelineErrorCodes.DealLossReasonNotFound, "The loss reason is not an active reason of this tenant.");
        }

        context.Deal.Lose(context.Pipeline, command.LossReasonId, command.Note, context.Actor, context.Now);
        return await steps.CommitAsync(context, PipelineAuditActions.DealLost, cancellationToken);
    }
}

/// <summary>D15: a la última etapa abierta del historial.</summary>
public sealed class ReopenDealHandler(DealCommandSteps steps, IDealRepository deals, IValidator<ReopenDealCommand> validator)
    : ICommandHandler<ReopenDealCommand, DealDetailDto>
{
    public async Task<DealDetailDto> HandleAsync(ReopenDealCommand command, CancellationToken cancellationToken)
    {
        await steps.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var context = await steps.LoadAsync(command.TenantId, command.DealId, command.Version, cancellationToken);
        var lastOpen = await deals.FindLastOpenStageIdAsync(command.TenantId, command.DealId, cancellationToken);
        context.Deal.Reopen(context.Pipeline, lastOpen, context.Actor, context.Now);
        return await steps.CommitAsync(context, PipelineAuditActions.DealReopened, cancellationToken);
    }
}
```

`src/Modules/Pipeline/Modules.Pipeline.Api/DealEndpoints.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Pipeline.Application;

namespace Modules.Pipeline.Api;

/// <summary>Spec 2026-10-13 §6: los negocios. Toda escritura recibe <c>version</c> y devuelve el negocio completo.</summary>
internal static class DealEndpoints
{
    public static void MapDealEndpoints(this RouteGroupBuilder pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var deals = pipeline.MapGroup("/deals");

        deals.MapPost("/", CreateAsync)
            .RequireAuthorization(PipelinePermissions.DealManage)
            .Accepts<CreateDealRequest>("application/json")
            .Produces<DealDetailDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        deals.MapGet("/{dealId:guid}", GetAsync)
            .RequireAuthorization(PipelinePermissions.DealRead)
            .Produces<DealDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        deals.MapPatch("/{dealId:guid}", UpdateAsync).WithWriteContract<UpdateDealRequest>();
        deals.MapPost("/{dealId:guid}/move", MoveAsync).WithWriteContract<MoveDealRequest>();
        deals.MapPost("/{dealId:guid}/win", WinAsync).WithWriteContract<DealVersionRequest>();
        deals.MapPost("/{dealId:guid}/lose", LoseAsync).WithWriteContract<LoseDealRequest>();
        deals.MapPost("/{dealId:guid}/reopen", ReopenAsync).WithWriteContract<DealVersionRequest>();
    }

    /// <summary>Lo común de toda escritura sobre un negocio existente.</summary>
    internal static RouteHandlerBuilder WithWriteContract<TRequest>(this RouteHandlerBuilder route) =>
        route.RequireAuthorization(PipelinePermissions.DealManage)
            .Accepts<TRequest>("application/json")
            .Produces<DealDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

    // Sin Location: la pantalla repinta con el negocio que vuelve.
    private static async Task<IResult> CreateAsync(Guid tenantId, CreateDealRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Created(
            (string?)null,
            await dispatcher.SendAsync(new CreateDealCommand(
                tenantId, request.CustomerId, request.Title, request.Amount, request.Currency, request.ExpectedCloseDate, request.OwnerMemberId), cancellationToken));

    private static async Task<IResult> GetAsync(Guid tenantId, Guid dealId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetDealQuery(tenantId, dealId), cancellationToken));

    private static async Task<IResult> UpdateAsync(
        Guid tenantId, Guid dealId, UpdateDealRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new UpdateDealCommand(
            tenantId, dealId, request.Title, request.OwnerMemberId, request.Amount, request.Currency, request.ExpectedCloseDate, request.Version), cancellationToken));

    private static async Task<IResult> MoveAsync(
        Guid tenantId, Guid dealId, MoveDealRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new MoveDealCommand(tenantId, dealId, request.StageId, request.Version), cancellationToken));

    private static async Task<IResult> WinAsync(
        Guid tenantId, Guid dealId, DealVersionRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new WinDealCommand(tenantId, dealId, request.Version), cancellationToken));

    private static async Task<IResult> LoseAsync(
        Guid tenantId, Guid dealId, LoseDealRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new LoseDealCommand(tenantId, dealId, request.LossReasonId, request.Note, request.Version), cancellationToken));

    private static async Task<IResult> ReopenAsync(
        Guid tenantId, Guid dealId, DealVersionRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ReopenDealCommand(tenantId, dealId, request.Version), cancellationToken));
}

/// <summary>Todo opcional: lo que falte sale 422 con su clave en <c>errors</c>, no 400 del binder.</summary>
public sealed record CreateDealRequest(Guid? CustomerId, string? Title, decimal? Amount, string? Currency, DateOnly? ExpectedCloseDate, Guid? OwnerMemberId);

/// <summary>P5 del plan: todos los editables, que reemplazan a los actuales.</summary>
public sealed record UpdateDealRequest(string? Title, Guid? OwnerMemberId, decimal? Amount, string? Currency, DateOnly? ExpectedCloseDate, long? Version);

public sealed record MoveDealRequest(Guid? StageId, long? Version);

public sealed record DealVersionRequest(long? Version);

public sealed record LoseDealRequest(Guid? LossReasonId, string? Note, long? Version);
```

`PipelineEndpoints.cs`: después de `group.MapSettingsEndpoints();`, `group.MapDealEndpoints();`.

`PipelineInfrastructureExtensions.cs`, antes del `return services;`:

```csharp
        // La membresía de quien llama, una vez por request; el detalle del negocio y los pasos comunes de sus escrituras.
        services.AddScoped<PipelineCaller>();
        services.AddScoped<DealDetailBuilder>();
        services.AddScoped<DealCommandSteps>();
```

`QepServiceCollectionExtensions.cs`, después de los handlers de ajustes (los validadores ya entran con `AddValidatorsFromAssemblyContaining<ReplacePipelineStagesValidator>()`):

```csharp
        // Pipeline (spec 2026-10-13 §6): negocios.
        services.AddScoped<IQueryHandler<GetDealQuery, DealDetailDto>, GetDealHandler>();
        services.AddScoped<ICommandHandler<CreateDealCommand, DealDetailDto>, CreateDealHandler>();
        services.AddScoped<ICommandHandler<UpdateDealCommand, DealDetailDto>, UpdateDealHandler>();
        services.AddScoped<ICommandHandler<MoveDealCommand, DealDetailDto>, MoveDealHandler>();
        services.AddScoped<ICommandHandler<WinDealCommand, DealDetailDto>, WinDealHandler>();
        services.AddScoped<ICommandHandler<LoseDealCommand, DealDetailDto>, LoseDealHandler>();
        services.AddScoped<ICommandHandler<ReopenDealCommand, DealDetailDto>, ReopenDealHandler>();
```

- [ ] **Step 4: Correr y verificar que pasan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.IntegrationTests\Modules.Pipeline.IntegrationTests.csproj" --no-build --filter "FullyQualifiedName~DealApiTests"
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --no-build --filter "FullyQualifiedName~PipelineLayerTests|FullyQualifiedName~CompositionRootTests"
```

Esperado: 9 verdes en `DealApiTests`; capas y composición verdes.

- [ ] **Step 5: Commit**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
if ((git -C $B branch --show-current) -ne "feature/embudo-de-ventas") { throw "rama equivocada" }
git -C $B status --short
git -C $B add src/Modules/Pipeline/Modules.Pipeline.Application/PipelineDtos.cs src/Modules/Pipeline/Modules.Pipeline.Application/DealDetailBuilder.cs src/Modules/Pipeline/Modules.Pipeline.Application/Deals.cs src/Modules/Pipeline/Modules.Pipeline.Api/DealEndpoints.cs src/Modules/Pipeline/Modules.Pipeline.Api/PipelineEndpoints.cs src/Modules/Pipeline/Modules.Pipeline.Infrastructure/PipelineInfrastructureExtensions.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/DealApiTests.cs
git -C $B commit -m "feat(pipeline): negocios con detalle completo, historial y auditoría"
git -C $B log -1 --format=%B
```

---

### Task 10: Reglas de las cotizaciones — `QuotationEventApplier`, enlazar y principal

**Files:**
- Create: `src/Modules/Pipeline/Modules.Pipeline.Application/QuotationEvents.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Application/DealQuotations.cs`
- Modify: `src/Modules/Pipeline/Modules.Pipeline.Api/DealEndpoints.cs` (dos rutas)
- Modify: `src/Modules/Pipeline/Modules.Pipeline.Infrastructure/PipelineInfrastructureExtensions.cs` (`QuotationEventApplier`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (dos handlers)
- Test: `tests/Modules/Pipeline/Modules.Pipeline.UnitTests/PipelineFakes.cs`, `QuotationEventApplierTests.cs`
- Test: `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/DealQuotationsApiTests.cs`

**Interfaces:**
- Consumes: `Deal`, `DealQuotation`, `SalesPipeline` (Tasks 4–5), puertos de la Task 6, `DealCommandSteps`/`DealCommandContext`/`DealVersionRequest`/`WithWriteContract` (Task 9), `ITenantModules`, `TenantModuleSet`, `TenantModuleKeys.Pipeline`.
- Produces:
  - `public sealed record QuotationFact(Guid TenantId, Guid QuotationId, string QuotationNumber, Guid ClientId, Guid AdvisorMemberId, decimal Total, string Currency, Guid? OrderId)`.
  - `public sealed record OrderCancelledFact(Guid TenantId, Guid OrderId, Guid QuotationId)`.
  - `public sealed class QuotationEventApplier { Task<bool> ApplySentAsync(QuotationFact, DateTimeOffset now, ct); Task<bool> ApplyConvertedAsync(QuotationFact, DateTimeOffset now, ct); Task<bool> ApplyOrderCancelledAsync(OrderCancelledFact, DateTimeOffset now, ct) }` — `false` = tenant sin la capacidad (P12). **No guarda**: quien llama (el worker, Task 11) guarda con la unidad de trabajo junto con el inbox.
  - `LinkDealQuotationCommand(Guid TenantId, Guid DealId, Guid QuotationId, long? Version)` y `SetPrimaryQuotationCommand(Guid TenantId, Guid DealId, Guid QuotationId, long? Version)`, ambos `→ DealDetailDto`.

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Pipeline/Modules.Pipeline.UnitTests/PipelineFakes.cs`:

```csharp
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Pipeline.UnitTests;

/// <summary>Los dobles del <see cref="QuotationEventApplier"/>. <see cref="InMemoryDeals.Flush"/> hace lo que la unidad de trabajo:
/// guarda el historial pendiente de cada negocio.</summary>
internal sealed class ApplierBed
{
    public static readonly DateTimeOffset Now = new(2026, 10, 13, 15, 0, 0, TimeSpan.Zero);

    public ApplierBed()
    {
        Provisioner = new FakeProvisioner(SalesPipeline.CreateDefault(TenantId, Now));
        Applier = new QuotationEventApplier(Modules, Provisioner, Deals, Quotations, Audit);
    }

    public Guid TenantId { get; } = Guid.CreateVersion7();

    public Guid AdvisorId { get; } = Guid.CreateVersion7();

    public FakeTenantModules Modules { get; } = new();

    public FakeProvisioner Provisioner { get; }

    public SalesPipeline Pipeline => Provisioner.Pipeline;

    public InMemoryDeals Deals { get; } = new();

    public InMemoryDealQuotations Quotations { get; } = new();

    public RecordingPipelineAudit Audit { get; } = new();

    public QuotationEventApplier Applier { get; }

    public QuotationFact Fact(Guid clientId, Guid quotationId, decimal total = 1_000m, string currency = "COP", Guid? orderId = null) =>
        new(TenantId, quotationId, "COT-0001", clientId, AdvisorId, total, currency, orderId);

    public Deal OpenDeal(Guid clientId, string title = "Manual")
    {
        var deal = Deal.Create(TenantId, Pipeline, clientId, AdvisorId, title, null, "COP", null, DealSource.Manual, null, DealActor.ForUser(AdvisorId), Now);
        Deals.Add(deal);
        Deals.Flush();
        return deal;
    }

    public PipelineStage Stage(string name) => Pipeline.OrderedStages.Single(stage => stage.Name == name);
}

internal sealed class FakeTenantModules : ITenantModules
{
    public bool PipelineEnabled { get; set; } = true;

    public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        TenantModuleKey[] keys = PipelineEnabled ? [TenantModuleKeys.Customers, TenantModuleKeys.Pipeline] : [TenantModuleKeys.Customers];
        return Task.FromResult<TenantModuleSet?>(TenantModuleSet.FromStored(keys));
    }
}

internal sealed class FakeProvisioner(SalesPipeline pipeline) : IPipelineProvisioner
{
    public SalesPipeline Pipeline { get; } = pipeline;

    public Task<SalesPipeline> EnsureAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(Pipeline);
}

internal sealed class InMemoryDeals : IDealRepository
{
    public List<Deal> Items { get; } = [];

    public List<DealStageHistoryEntry> History { get; } = [];

    public void Flush()
    {
        foreach (var deal in Items)
        {
            History.AddRange(deal.PullHistory());
        }
    }

    public Task<Deal?> FindAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.SingleOrDefault(deal => deal.TenantId == tenantId && deal.Id == dealId));

    public Task<IReadOnlyList<Deal>> ListOpenByCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Deal>>([.. Items.Where(deal => deal.TenantId == tenantId && deal.CustomerId == customerId && !deal.IsClosed)]);

    public Task<Deal?> FindLastClosedByCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Where(deal => deal.TenantId == tenantId && deal.CustomerId == customerId && deal.IsClosed).MaxBy(deal => deal.ClosedAt));

    public Task<IReadOnlySet<Guid>> CustomersWithOpenDealsAsync(Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<Guid>>(Items.Where(deal => !deal.IsClosed && customerIds.Contains(deal.CustomerId)).Select(deal => deal.CustomerId).ToHashSet());

    public Task<IReadOnlySet<Guid>> StagesWithDealsAsync(Guid tenantId, Guid pipelineId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<Guid>>(Items.Select(deal => deal.StageId).ToHashSet());

    public Task<Guid?> FindLastOpenStageIdAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken) =>
        Task.FromResult(History.LastOrDefault(row => row.DealId == dealId)?.FromStageId);

    public Task<IReadOnlyList<DealStageHistoryEntry>> ListHistoryAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DealStageHistoryEntry>>([.. History.Where(row => row.DealId == dealId)]);

    public void Add(Deal deal) => Items.Add(deal);
}

internal sealed class InMemoryDealQuotations : IDealQuotationRepository
{
    public List<DealQuotation> Items { get; } = [];

    public Task<DealQuotation?> FindAsync(Guid tenantId, Guid quotationId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.SingleOrDefault(quotation => quotation.TenantId == tenantId && quotation.QuotationId == quotationId));

    public Task<IReadOnlyList<DealQuotation>> ListByDealAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DealQuotation>>([.. Items.Where(quotation => quotation.DealId == dealId)]);

    public Task<IReadOnlyList<DealQuotation>> ListUnlinkedByCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DealQuotation>>([.. Items.Where(quotation => quotation.CustomerId == customerId && quotation.DealId is null)]);

    public void Add(DealQuotation quotation) => Items.Add(quotation);
}

internal sealed class RecordingPipelineAudit : IPipelineAuditRecorder
{
    public List<(string Action, Guid ResourceId, bool BySystem)> Entries { get; } = [];

    public void Record(Guid tenantId, Guid actorId, bool bySystem, string action, string resourceType, Guid resourceId, DateTimeOffset occurredAt) =>
        Entries.Add((action, resourceId, bySystem));
}
```

`tests/Modules/Pipeline/Modules.Pipeline.UnitTests/QuotationEventApplierTests.cs`:

```csharp
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.UnitTests;

/// <summary>Spec 2026-10-13 §4.4, D17, D18 y D25–D28: lo que cada evento de Quotations le hace al negocio. El resultado no
/// depende del orden de llegada (D25) y un evento repetido no duplica nada.</summary>
public sealed class QuotationEventApplierTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Later = ApplierBed.Now.AddHours(1);

    [Fact]
    public async Task ASentWithoutOpenDealsCreatesAQuotationDealInTheQuotationStage()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var quotationId = Guid.CreateVersion7();

        Assert.True(await bed.Applier.ApplySentAsync(bed.Fact(clientId, quotationId, 1_000m), Later, Ct));

        var deal = Assert.Single(bed.Deals.Items);
        Assert.Equal((DealSource.Quotation, bed.AdvisorId, "COT-0001", clientId), (deal.Source, deal.OwnerMemberId, deal.Title, deal.CustomerId));
        Assert.Equal((bed.Stage("Cotizado").Id, (Guid?)quotationId, (decimal?)1_000m, "COP"), (deal.StageId, deal.PrimaryQuotationId, deal.Amount, deal.Currency));
        Assert.Equal((Guid?)deal.Id, Assert.Single(bed.Quotations.Items).DealId);
        Assert.Equal(
            [PipelineAuditActions.DealCreated, PipelineAuditActions.QuotationLinked, PipelineAuditActions.PrimaryQuotationSet, PipelineAuditActions.DealMoved],
            bed.Audit.Entries.Select(entry => entry.Action));
        Assert.All(bed.Audit.Entries, entry => Assert.True(entry.BySystem));
        bed.Deals.Flush();
        Assert.All(bed.Deals.History, row => Assert.Equal(ActorKind.System, row.ActorKind));
    }

    [Fact]
    public async Task ASentWithExactlyOneOpenDealLinksAndAdvancesIt()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var deal = bed.OpenDeal(clientId);
        var quotationId = Guid.CreateVersion7();

        await bed.Applier.ApplySentAsync(bed.Fact(clientId, quotationId, 2_000m, "USD"), Later, Ct);

        Assert.Single(bed.Deals.Items);
        Assert.Equal((bed.Stage("Cotizado").Id, (Guid?)quotationId, (decimal?)2_000m, "USD"), (deal.StageId, deal.PrimaryQuotationId, deal.Amount, deal.Currency));
    }

    [Fact]
    public async Task ASentWithSeveralOpenDealsLeavesItUnlinked()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var first = bed.OpenDeal(clientId, "Uno");
        var second = bed.OpenDeal(clientId, "Dos");

        await bed.Applier.ApplySentAsync(bed.Fact(clientId, Guid.CreateVersion7()), Later, Ct);

        Assert.Null(Assert.Single(bed.Quotations.Items).DealId);
        Assert.Equal((1L, 1L), (first.Version, second.Version));
        Assert.Empty(bed.Audit.Entries);
    }

    [Fact]
    public async Task AnAlreadyLinkedQuotationKeepsItsDealEvenWithOtherOpenDeals()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var quotationId = Guid.CreateVersion7();
        await bed.Applier.ApplySentAsync(bed.Fact(clientId, quotationId, 1_000m), Later, Ct);
        var linked = Assert.Single(bed.Deals.Items);
        bed.OpenDeal(clientId, "Otro");

        await bed.Applier.ApplySentAsync(bed.Fact(clientId, quotationId, 1_200m), Later.AddHours(1), Ct);

        Assert.Equal((Guid?)linked.Id, Assert.Single(bed.Quotations.Items).DealId);
        Assert.Equal((decimal?)1_200m, linked.Amount);
    }

    // D17: la máquina nunca le deshace al asesor una decisión.
    [Fact]
    public async Task AnAdvanceNeverGoesBackwards()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var deal = bed.OpenDeal(clientId);
        deal.MoveTo(bed.Pipeline, bed.Stage("Negociación").Id, DealActor.ForUser(bed.AdvisorId), ApplierBed.Now);

        await bed.Applier.ApplySentAsync(bed.Fact(clientId, Guid.CreateVersion7()), Later, Ct);

        Assert.Equal(bed.Stage("Negociación").Id, deal.StageId);
    }

    // D18 y D26: el monto sigue a la principal en el reenvío; otra cotización del mismo negocio no lo mueve.
    [Fact]
    public async Task OnlyThePrimaryMovesTheAmount()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var primary = Guid.CreateVersion7();
        await bed.Applier.ApplySentAsync(bed.Fact(clientId, primary, 1_000m), Later, Ct);
        var deal = Assert.Single(bed.Deals.Items);

        await bed.Applier.ApplySentAsync(bed.Fact(clientId, Guid.CreateVersion7(), 9_999m), Later, Ct);
        await bed.Applier.ApplySentAsync(bed.Fact(clientId, primary, 1_300m), Later, Ct);

        Assert.Equal(((Guid?)primary, (decimal?)1_300m), (deal.PrimaryQuotationId, deal.Amount));
        Assert.Equal(2, bed.Quotations.Items.Count(quotation => quotation.DealId == deal.Id));
    }

    [Fact]
    public async Task AConvertedPrimaryWinsTheDeal()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var quotationId = Guid.CreateVersion7();
        await bed.Applier.ApplySentAsync(bed.Fact(clientId, quotationId, 1_000m), Later, Ct);

        await bed.Applier.ApplyConvertedAsync(bed.Fact(clientId, quotationId, 1_100m, orderId: Guid.CreateVersion7()), Later.AddDays(1), Ct);

        var deal = Assert.Single(bed.Deals.Items);
        Assert.Equal((bed.Pipeline.WonStage.Id, (decimal?)1_100m), (deal.StageId, deal.Amount));
        Assert.Equal(DealQuotationStatus.Converted, Assert.Single(bed.Quotations.Items).Status);
    }

    // Review Focus 1 y D25: convertida desde Draft, o el sent esperando su reintento.
    [Fact]
    public async Task ConvertedWithoutSentCreatesTheDealAndWinsIt()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var quotationId = Guid.CreateVersion7();

        await bed.Applier.ApplyConvertedAsync(bed.Fact(clientId, quotationId, 1_000m, orderId: Guid.CreateVersion7()), Later, Ct);

        var deal = Assert.Single(bed.Deals.Items);
        Assert.Equal((bed.Pipeline.WonStage.Id, (Guid?)quotationId, true), (deal.StageId, deal.PrimaryQuotationId, deal.IsClosed));
        bed.Deals.Flush();
        Assert.Equal([bed.Pipeline.FirstOpenStage.Id, bed.Pipeline.WonStage.Id], bed.Deals.History.Select(row => row.ToStageId));
    }

    [Fact]
    public async Task ARepeatedConvertedChangesNothing()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var quotationId = Guid.CreateVersion7();
        var fact = bed.Fact(clientId, quotationId, 1_000m, orderId: Guid.CreateVersion7());
        await bed.Applier.ApplyConvertedAsync(fact, Later, Ct);
        var deal = Assert.Single(bed.Deals.Items);
        var version = deal.Version;
        bed.Deals.Flush();

        await bed.Applier.ApplyConvertedAsync(fact, Later.AddMinutes(1), Ct);
        bed.Deals.Flush();

        Assert.Single(bed.Deals.Items);
        Assert.Equal(version, deal.Version);
        Assert.Equal(2, bed.Deals.History.Count);
    }

    // Un sent que llega después del converted (reintento) no saca al negocio de Ganado ni devuelve la foto a Sent.
    [Fact]
    public async Task ALateSentAfterTheConvertedChangesNothing()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var quotationId = Guid.CreateVersion7();
        await bed.Applier.ApplyConvertedAsync(bed.Fact(clientId, quotationId, 1_000m, orderId: Guid.CreateVersion7()), Later, Ct);

        await bed.Applier.ApplySentAsync(bed.Fact(clientId, quotationId, 1_000m), Later.AddMinutes(1), Ct);

        Assert.Equal(bed.Pipeline.WonStage.Id, Assert.Single(bed.Deals.Items).StageId);
        Assert.Equal(DealQuotationStatus.Converted, Assert.Single(bed.Quotations.Items).Status);
    }

    [Fact]
    public async Task AConvertedNonPrimaryDoesNotWin()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        await bed.Applier.ApplySentAsync(bed.Fact(clientId, Guid.CreateVersion7()), Later, Ct);
        var other = Guid.CreateVersion7();
        await bed.Applier.ApplySentAsync(bed.Fact(clientId, other), Later, Ct);

        await bed.Applier.ApplyConvertedAsync(bed.Fact(clientId, other, orderId: Guid.CreateVersion7()), Later.AddDays(1), Ct);

        Assert.False(Assert.Single(bed.Deals.Items).IsClosed);
    }

    // D27: la cotización cambió de cliente; el negocio viejo pierde la principal y conserva el monto.
    [Fact]
    public async Task AChangedClientUnlinksAndRelinksToTheNewCustomer()
    {
        var bed = new ApplierBed();
        var oldClient = Guid.CreateVersion7();
        var newClient = Guid.CreateVersion7();
        var quotationId = Guid.CreateVersion7();
        await bed.Applier.ApplySentAsync(bed.Fact(oldClient, quotationId, 1_000m), Later, Ct);
        var oldDeal = Assert.Single(bed.Deals.Items);

        await bed.Applier.ApplySentAsync(bed.Fact(newClient, quotationId, 1_500m), Later.AddDays(1), Ct);

        Assert.Equal(((Guid?)null, (decimal?)1_000m, false), (oldDeal.PrimaryQuotationId, oldDeal.Amount, oldDeal.IsClosed));
        var newDeal = Assert.Single(bed.Deals.Items, deal => deal.CustomerId == newClient);
        Assert.Equal(((Guid?)quotationId, (decimal?)1_500m), (newDeal.PrimaryQuotationId, newDeal.Amount));
        Assert.Equal((Guid?)newDeal.Id, Assert.Single(bed.Quotations.Items).DealId);
        Assert.Contains(bed.Audit.Entries, entry => entry.Action == PipelineAuditActions.QuotationUnlinked && entry.ResourceId == oldDeal.Id);
    }

    // §4.4: el pedido anulado reabre el negocio ganado por esa cotización, a la última etapa abierta (D15).
    [Fact]
    public async Task ACancelledOrderReopensTheDealWonByThatQuotation()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var quotationId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();
        await bed.Applier.ApplySentAsync(bed.Fact(clientId, quotationId), Later, Ct);
        await bed.Applier.ApplyConvertedAsync(bed.Fact(clientId, quotationId, orderId: orderId), Later.AddDays(1), Ct);
        bed.Deals.Flush();

        await bed.Applier.ApplyOrderCancelledAsync(new OrderCancelledFact(bed.TenantId, orderId, quotationId), Later.AddDays(2), Ct);

        var deal = Assert.Single(bed.Deals.Items);
        Assert.Equal((bed.Stage("Cotizado").Id, false), (deal.StageId, deal.IsClosed));
        Assert.NotNull(Assert.Single(bed.Quotations.Items).OrderCancelledAt);
    }

    [Fact]
    public async Task ACancelledOrderOfAQuotationThatDidNotWinReopensNothing()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var deal = bed.OpenDeal(clientId);
        deal.Win(bed.Pipeline, DealActor.ForUser(bed.AdvisorId), ApplierBed.Now);
        var quotationId = Guid.CreateVersion7();
        await bed.Applier.ApplySentAsync(bed.Fact(clientId, quotationId), Later, Ct);

        await bed.Applier.ApplyOrderCancelledAsync(new OrderCancelledFact(bed.TenantId, Guid.CreateVersion7(), quotationId), Later.AddDays(1), Ct);

        Assert.True(deal.IsClosed);
    }

    // P12: sin la capacidad, procesado sin efecto.
    [Fact]
    public async Task ATenantWithoutTheCapabilityIsIgnored()
    {
        var bed = new ApplierBed();
        bed.Modules.PipelineEnabled = false;

        Assert.False(await bed.Applier.ApplySentAsync(bed.Fact(Guid.CreateVersion7(), Guid.CreateVersion7()), Later, Ct));

        Assert.Empty(bed.Deals.Items);
        Assert.Empty(bed.Quotations.Items);
    }
}
```

`tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/DealQuotationsApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pipeline.Api;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;
using static Modules.Pipeline.IntegrationTests.PipelineApiHarness;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>Spec 2026-10-13 §4.4, D18 y P11 del plan: enlazar a mano una cotización que quedó suelta, elegir la principal y el
/// monto de sólo lectura. Las fotos se siembran con el mismo <see cref="QuotationEventApplier"/> que usa el worker.</summary>
public sealed class DealQuotationsApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task SentAsync(QepApiFactory factory, Guid tenantId, Guid clientId, Guid quotationId, decimal total)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<QuotationEventApplier>().ApplySentAsync(
            new QuotationFact(tenantId, quotationId, $"COT-{total}", clientId, Guid.CreateVersion7(), total, "COP", null), DateTimeOffset.UtcNow, Ct);
        await scope.ServiceProvider.GetRequiredService<IPipelineUnitOfWork>().SaveChangesAsync(Ct);
    }

    private static async Task<DealDetailDto> CreateDealAsync(HttpClient client, Guid tenantId, Guid customerId, string title)
    {
        var response = await client.PostAsJsonAsync($"{PipelineUrl(tenantId)}/deals", new CreateDealRequest(customerId, title, null, null, null, null), Ct);
        return (await response.Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid tenantId, Guid dealId, Guid quotationId, string action, long version) =>
        client.PostAsJsonAsync($"{PipelineUrl(tenantId)}/deals/{dealId}/quotations/{quotationId}/{action}", new DealVersionRequest(version), Ct);

    // Con dos negocios abiertos la cotización queda suelta (§4.4); el asesor la enlaza y el monto pasa a seguirla (D18).
    [Fact]
    public async Task LinkingALooseQuotationMakesItPrimaryAndTheAmountReadOnly()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        var (customerId, _) = await SeedCustomerAsync(factory, tenant.TenantId, "Laura");
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var first = await CreateDealAsync(client, tenant.TenantId, customerId, "Uno");
        await CreateDealAsync(client, tenant.TenantId, customerId, "Dos");
        var quotationId = Guid.CreateVersion7();
        await SentAsync(factory, tenant.TenantId, customerId, quotationId, 1_000m);

        var before = (await client.GetFromJsonAsync<DealDetailDto>($"{PipelineUrl(tenant.TenantId)}/deals/{first.Id}", Ct))!;
        var linked = await PostAsync(client, tenant.TenantId, first.Id, quotationId, "link", before.Version);
        var detail = (await linked.Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;
        var patch = await client.PatchAsJsonAsync($"{PipelineUrl(tenant.TenantId)}/deals/{first.Id}",
            new UpdateDealRequest("Uno", detail.OwnerMemberId, 5_000m, "COP", null, detail.Version), Ct);

        Assert.Equal(quotationId, Assert.Single(before.UnlinkedCustomerQuotations).QuotationId);
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        Assert.Equal(((Guid?)quotationId, (decimal?)1_000m, false), (detail.PrimaryQuotationId, detail.Amount, detail.AmountIsEditable));
        Assert.True(Assert.Single(detail.Quotations).IsPrimary);
        Assert.Empty(detail.UnlinkedCustomerQuotations);
        Assert.Equal(PipelineErrorCodes.DealAmountFollowsQuotation, (await ProblemAsync(patch)).Code);
    }

    [Fact]
    public async Task ForeignUnknownAndUnlinkedQuotationsAre422()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        var (customerId, _) = await SeedCustomerAsync(factory, tenant.TenantId, "Laura");
        var (otherCustomer, _) = await SeedCustomerAsync(factory, tenant.TenantId, "Pedro");
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var deal = await CreateDealAsync(client, tenant.TenantId, customerId, "Uno");
        await CreateDealAsync(client, tenant.TenantId, otherCustomer, "Otro A");
        await CreateDealAsync(client, tenant.TenantId, otherCustomer, "Otro B");
        var foreignQuotation = Guid.CreateVersion7();
        await SentAsync(factory, tenant.TenantId, otherCustomer, foreignQuotation, 700m);

        var mismatch = await PostAsync(client, tenant.TenantId, deal.Id, foreignQuotation, "link", deal.Version);
        var unknown = await PostAsync(client, tenant.TenantId, deal.Id, Guid.CreateVersion7(), "link", deal.Version);
        var notLinked = await PostAsync(client, tenant.TenantId, deal.Id, foreignQuotation, "primary", deal.Version);

        Assert.Equal(PipelineErrorCodes.DealQuotationCustomerMismatch, (await ProblemAsync(mismatch)).Code);
        Assert.Equal(PipelineErrorCodes.DealQuotationNotFound, (await ProblemAsync(unknown)).Code);
        Assert.Equal(PipelineErrorCodes.DealQuotationNotLinked, (await ProblemAsync(notLinked)).Code);
    }

    // P11: enlazar una cotización que estaba en otro negocio la mueve; ése pierde la principal y conserva el monto (D27).
    [Fact]
    public async Task LinkingMovesTheQuotationAndPrimaryChangesTheAmount()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        var (customerId, _) = await SeedCustomerAsync(factory, tenant.TenantId, "Laura");
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var first = await CreateDealAsync(client, tenant.TenantId, customerId, "Uno");
        var firstQuotation = Guid.CreateVersion7();
        await SentAsync(factory, tenant.TenantId, customerId, firstQuotation, 1_000m);
        var second = await CreateDealAsync(client, tenant.TenantId, customerId, "Dos");
        var secondQuotation = Guid.CreateVersion7();
        await SentAsync(factory, tenant.TenantId, customerId, secondQuotation, 2_000m);
        var dealsUrl = $"{PipelineUrl(tenant.TenantId)}/deals";
        second = (await client.GetFromJsonAsync<DealDetailDto>($"{dealsUrl}/{second.Id}", Ct))!;

        var moved = (await (await PostAsync(client, tenant.TenantId, second.Id, firstQuotation, "link", second.Version)).Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;
        var firstAfter = (await client.GetFromJsonAsync<DealDetailDto>($"{dealsUrl}/{first.Id}", Ct))!;
        var linkedSecond = await PostAsync(client, tenant.TenantId, second.Id, secondQuotation, "link", moved.Version);
        var afterLink = (await linkedSecond.Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;
        var primary = (await (await PostAsync(client, tenant.TenantId, second.Id, secondQuotation, "primary", afterLink.Version)).Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;

        Assert.Equal(((Guid?)firstQuotation, (decimal?)1_000m), (moved.PrimaryQuotationId, moved.Amount));
        Assert.Equal(((Guid?)null, (decimal?)1_000m, true), (firstAfter.PrimaryQuotationId, firstAfter.Amount, firstAfter.AmountIsEditable));
        Assert.Equal(((Guid?)secondQuotation, (decimal?)2_000m), (primary.PrimaryQuotationId, primary.Amount));
        Assert.Equal(2, primary.Quotations.Count);
    }
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.UnitTests\Modules.Pipeline.UnitTests.csproj" --filter "FullyQualifiedName~QuotationEventApplierTests"
```

Esperado: `error CS0246` sobre `QuotationEventApplier`, `QuotationFact`, `OrderCancelledFact`.

- [ ] **Step 3: Implementar el aplicador, los dos handlers y las rutas**

`src/Modules/Pipeline/Modules.Pipeline.Application/QuotationEvents.cs`:

```csharp
using Modules.Pipeline.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Pipeline.Application;

/// <summary>Spec 2026-10-13 §4.5: lo que traen <c>quotations.quotation.sent.v1</c> y <c>…converted.v1</c> (este último con el pedido).</summary>
public sealed record QuotationFact(
    Guid TenantId, Guid QuotationId, string QuotationNumber, Guid ClientId, Guid AdvisorMemberId, decimal Total, string Currency, Guid? OrderId);

/// <summary>§4.5: lo que trae <c>quotations.order.cancelled.v1</c>.</summary>
public sealed record OrderCancelledFact(Guid TenantId, Guid OrderId, Guid QuotationId);

/// <summary>
/// Spec 2026-10-13 §4.4, D17, D18 y D25–D28: lo que cada evento de Quotations le hace al negocio, con actor <c>System</c>. No
/// guarda: el worker guarda con la unidad de trabajo, en el mismo SaveChanges que el inbox. El resultado no depende del orden de
/// llegada (converted aplica primero las reglas de enlace de sent, D25) y un evento repetido no duplica nada. Una principal
/// anulada o vencida no dispara nada (D28): Quotations no emite evento por eso.
/// </summary>
public sealed class QuotationEventApplier(
    ITenantModules tenantModules,
    IPipelineProvisioner provisioner,
    IDealRepository deals,
    IDealQuotationRepository quotations,
    IPipelineAuditRecorder audit)
{
    /// <returns><c>false</c> si el tenant no tiene la capacidad (P12): el mensaje se marca procesado sin efecto.</returns>
    public Task<bool> ApplySentAsync(QuotationFact fact, DateTimeOffset now, CancellationToken cancellationToken) =>
        ApplyQuotationAsync(fact, converted: false, now, cancellationToken);

    /// <returns>Ver <see cref="ApplySentAsync"/>.</returns>
    public Task<bool> ApplyConvertedAsync(QuotationFact fact, DateTimeOffset now, CancellationToken cancellationToken) =>
        ApplyQuotationAsync(fact, converted: true, now, cancellationToken);

    /// <summary>§4.4: si el negocio está ganado y su principal es la cotización de ese pedido, se reabre (D15).</summary>
    public async Task<bool> ApplyOrderCancelledAsync(OrderCancelledFact fact, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (!await IsEnabledAsync(fact.TenantId, cancellationToken))
        {
            return false;
        }

        var snapshot = await quotations.FindAsync(fact.TenantId, fact.QuotationId, cancellationToken);
        if (snapshot is null)
        {
            return true;
        }

        snapshot.MarkOrderCancelled(now);
        if (snapshot.DealId is not { } dealId || await deals.FindAsync(fact.TenantId, dealId, cancellationToken) is not { } deal)
        {
            return true;
        }

        var pipeline = await provisioner.EnsureAsync(fact.TenantId, cancellationToken);
        if (deal.PrimaryQuotationId != fact.QuotationId || deal.StageId != pipeline.WonStage.Id)
        {
            return true;
        }

        var lastOpen = await deals.FindLastOpenStageIdAsync(fact.TenantId, deal.Id, cancellationToken);
        deal.Reopen(pipeline, lastOpen, DealActor.ForSystem, now);
        Record(fact.TenantId, PipelineAuditActions.DealReopened, deal.Id, now);
        return true;
    }

    private async Task<bool> ApplyQuotationAsync(QuotationFact fact, bool converted, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (!await IsEnabledAsync(fact.TenantId, cancellationToken))
        {
            return false;
        }

        var pipeline = await provisioner.EnsureAsync(fact.TenantId, cancellationToken);

        // Paso 1: la foto.
        var snapshot = await quotations.FindAsync(fact.TenantId, fact.QuotationId, cancellationToken);
        if (snapshot is null)
        {
            snapshot = DealQuotation.Create(fact.TenantId, fact.QuotationId, fact.QuotationNumber, fact.ClientId, fact.Total, fact.Currency, now);
            quotations.Add(snapshot);
        }

        snapshot.Refresh(fact.QuotationNumber, fact.ClientId, fact.Total, fact.Currency, converted, fact.OrderId, now);

        // Paso 2 (D27): enlazada a un negocio de otro cliente → se desenlaza y se sigue como si nunca lo hubiera estado.
        var deal = snapshot.DealId is { } linkedId ? await deals.FindAsync(fact.TenantId, linkedId, cancellationToken) : null;
        if (deal is not null && deal.CustomerId != fact.ClientId)
        {
            if (deal.PrimaryQuotationId == fact.QuotationId)
            {
                deal.ClearPrimary(now);
            }

            snapshot.Unlink(now);
            Record(fact.TenantId, PipelineAuditActions.QuotationUnlinked, deal.Id, now);
            deal = null;
        }

        // Paso 3: enlace. Con varios negocios abiertos del cliente queda suelta: el asesor elige desde el negocio.
        if (deal is null)
        {
            var open = await deals.ListOpenByCustomerAsync(fact.TenantId, fact.ClientId, cancellationToken);
            if (open.Count > 1)
            {
                return true;
            }

            if (open.Count == 1)
            {
                deal = open[0];
            }
            else
            {
                deal = Deal.Create(fact.TenantId, pipeline, fact.ClientId, fact.AdvisorMemberId, fact.QuotationNumber, fact.Total,
                    fact.Currency, null, DealSource.Quotation, null, DealActor.ForSystem, now);
                deals.Add(deal);
                Record(fact.TenantId, PipelineAuditActions.DealCreated, deal.Id, now);
            }

            snapshot.LinkTo(deal.Id, now);
            Record(fact.TenantId, PipelineAuditActions.QuotationLinked, deal.Id, now);
        }

        // Paso 4 (D17, D18, D26): la principal y el monto. El monto sólo se refresca acá.
        if (deal.PrimaryQuotationId is null)
        {
            deal.SetPrimaryQuotation(fact.QuotationId, fact.Total, fact.Currency, now);
            Record(fact.TenantId, PipelineAuditActions.PrimaryQuotationSet, deal.Id, now);
        }
        else if (deal.PrimaryQuotationId == fact.QuotationId)
        {
            deal.FollowPrimary(fact.Total, fact.Currency, now);
        }

        // Paso 5: sent avanza sólo hacia adelante (D17); converted de la principal de un abierto gana (D25).
        if (!converted)
        {
            if (deal.AdvanceToQuotationStage(pipeline, DealActor.ForSystem, now))
            {
                Record(fact.TenantId, PipelineAuditActions.DealMoved, deal.Id, now);
            }
        }
        else if (deal.PrimaryQuotationId == fact.QuotationId && !deal.IsClosed)
        {
            deal.Win(pipeline, DealActor.ForSystem, now);
            Record(fact.TenantId, PipelineAuditActions.DealWon, deal.Id, now);
        }

        return true;
    }

    private async Task<bool> IsEnabledAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await tenantModules.FindAsync(tenantId, cancellationToken) is { } modules && modules.IsEnabled(TenantModuleKeys.Pipeline);

    // §6.3: lo del worker no tiene una persona detrás: System y actor vacío (H15).
    private void Record(Guid tenantId, string action, Guid dealId, DateTimeOffset now) =>
        audit.Record(tenantId, Guid.Empty, true, action, PipelineAuditActions.DealResource, dealId, now);
}
```

`src/Modules/Pipeline/Modules.Pipeline.Application/DealQuotations.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.Application;

public sealed record LinkDealQuotationCommand(Guid TenantId, Guid DealId, Guid QuotationId, long? Version) : ICommand<DealDetailDto>;

public sealed record SetPrimaryQuotationCommand(Guid TenantId, Guid DealId, Guid QuotationId, long? Version) : ICommand<DealDetailDto>;

public sealed class LinkDealQuotationValidator : AbstractValidator<LinkDealQuotationCommand>
{
    public LinkDealQuotationValidator() => RuleFor(command => command.Version).NotNull();
}

public sealed class SetPrimaryQuotationValidator : AbstractValidator<SetPrimaryQuotationCommand>
{
    public SetPrimaryQuotationValidator() => RuleFor(command => command.Version).NotNull();
}

/// <summary>
/// §4.4 y P11 del plan: enlazar a mano una cotización que llegó por evento. Tiene que ser del mismo cliente; si estaba en otro
/// negocio se mueve, y ése pierde la principal (D27). Si el negocio no tenía principal, ésta pasa a serlo (D17).
/// </summary>
public sealed class LinkDealQuotationHandler(
    DealCommandSteps steps, IDealQuotationRepository quotations, IDealRepository deals, IValidator<LinkDealQuotationCommand> validator)
    : ICommandHandler<LinkDealQuotationCommand, DealDetailDto>
{
    public async Task<DealDetailDto> HandleAsync(LinkDealQuotationCommand command, CancellationToken cancellationToken)
    {
        await steps.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var context = await steps.LoadAsync(command.TenantId, command.DealId, command.Version, cancellationToken);
        var deal = context.Deal;
        var snapshot = await quotations.FindAsync(command.TenantId, command.QuotationId, cancellationToken)
            ?? throw new PipelineDomainException(PipelineErrorCodes.DealQuotationNotFound, "The quotation has not reached the pipeline.");
        if (snapshot.CustomerId != deal.CustomerId)
        {
            throw new PipelineDomainException(PipelineErrorCodes.DealQuotationCustomerMismatch, "The quotation belongs to another customer.");
        }

        if (snapshot.DealId == deal.Id)
        {
            return await steps.CommitAsync(context, null, cancellationToken);
        }

        if (snapshot.DealId is { } previousId &&
            await deals.FindAsync(command.TenantId, previousId, cancellationToken) is { } previous &&
            previous.PrimaryQuotationId == snapshot.QuotationId)
        {
            previous.ClearPrimary(context.Now);
        }

        snapshot.LinkTo(deal.Id, context.Now);
        if (deal.PrimaryQuotationId is null)
        {
            deal.SetPrimaryQuotation(snapshot.QuotationId, snapshot.Total, snapshot.Currency, context.Now);
        }

        return await steps.CommitAsync(context, PipelineAuditActions.QuotationLinked, cancellationToken);
    }
}

/// <summary>D18: la principal manda el monto y la moneda. Tiene que estar enlazada a este negocio.</summary>
public sealed class SetPrimaryQuotationHandler(
    DealCommandSteps steps, IDealQuotationRepository quotations, IValidator<SetPrimaryQuotationCommand> validator)
    : ICommandHandler<SetPrimaryQuotationCommand, DealDetailDto>
{
    public async Task<DealDetailDto> HandleAsync(SetPrimaryQuotationCommand command, CancellationToken cancellationToken)
    {
        await steps.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var context = await steps.LoadAsync(command.TenantId, command.DealId, command.Version, cancellationToken);
        var snapshot = await quotations.FindAsync(command.TenantId, command.QuotationId, cancellationToken)
            ?? throw new PipelineDomainException(PipelineErrorCodes.DealQuotationNotFound, "The quotation has not reached the pipeline.");
        if (snapshot.DealId != context.Deal.Id)
        {
            throw new PipelineDomainException(PipelineErrorCodes.DealQuotationNotLinked, "Link the quotation to this deal first.");
        }

        if (context.Deal.PrimaryQuotationId == snapshot.QuotationId)
        {
            return await steps.CommitAsync(context, null, cancellationToken);
        }

        context.Deal.SetPrimaryQuotation(snapshot.QuotationId, snapshot.Total, snapshot.Currency, context.Now);
        return await steps.CommitAsync(context, PipelineAuditActions.PrimaryQuotationSet, cancellationToken);
    }
}
```

`DealEndpoints.cs`: al final de `MapDealEndpoints`:

```csharp
        deals.MapPost("/{dealId:guid}/quotations/{quotationId:guid}/link", LinkQuotationAsync).WithWriteContract<DealVersionRequest>();
        deals.MapPost("/{dealId:guid}/quotations/{quotationId:guid}/primary", SetPrimaryQuotationAsync).WithWriteContract<DealVersionRequest>();
```

y los dos métodos, después de `ReopenAsync`:

```csharp
    private static async Task<IResult> LinkQuotationAsync(
        Guid tenantId, Guid dealId, Guid quotationId, DealVersionRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new LinkDealQuotationCommand(tenantId, dealId, quotationId, request.Version), cancellationToken));

    private static async Task<IResult> SetPrimaryQuotationAsync(
        Guid tenantId, Guid dealId, Guid quotationId, DealVersionRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new SetPrimaryQuotationCommand(tenantId, dealId, quotationId, request.Version), cancellationToken));
```

`PipelineInfrastructureExtensions.cs`, antes del `return services;`:

```csharp
        // Spec 2026-10-13 §4.4: las reglas de los eventos de Quotations; lo usa el worker (Task 11) en su scope por mensaje.
        services.AddScoped<QuotationEventApplier>();
```

`QepServiceCollectionExtensions.cs`, después de los handlers de negocios:

```csharp
        services.AddScoped<ICommandHandler<LinkDealQuotationCommand, DealDetailDto>, LinkDealQuotationHandler>();
        services.AddScoped<ICommandHandler<SetPrimaryQuotationCommand, DealDetailDto>, SetPrimaryQuotationHandler>();
```

- [ ] **Step 4: Correr y verificar que pasan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.UnitTests\Modules.Pipeline.UnitTests.csproj" --no-build --filter "FullyQualifiedName~QuotationEventApplierTests"
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.IntegrationTests\Modules.Pipeline.IntegrationTests.csproj" --no-build --filter "FullyQualifiedName~DealQuotationsApiTests"
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --no-build --filter "FullyQualifiedName~PipelineLayerTests|FullyQualifiedName~CompositionRootTests"
```

Esperado: 15 verdes en `QuotationEventApplierTests`, 3 en `DealQuotationsApiTests`, capas y composición verdes.

- [ ] **Step 5: Commit**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
if ((git -C $B branch --show-current) -ne "feature/embudo-de-ventas") { throw "rama equivocada" }
git -C $B status --short
git -C $B add src/Modules/Pipeline/Modules.Pipeline.Application/QuotationEvents.cs src/Modules/Pipeline/Modules.Pipeline.Application/DealQuotations.cs src/Modules/Pipeline/Modules.Pipeline.Api/DealEndpoints.cs src/Modules/Pipeline/Modules.Pipeline.Infrastructure/PipelineInfrastructureExtensions.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Pipeline/Modules.Pipeline.UnitTests/PipelineFakes.cs tests/Modules/Pipeline/Modules.Pipeline.UnitTests/QuotationEventApplierTests.cs tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/DealQuotationsApiTests.cs
git -C $B commit -m "feat(pipeline): reglas de las cotizaciones, enlace manual y cotización principal"
git -C $B log -1 --format=%B
```

---

### Task 11: Worker de eventos de Quotations — reclamo, inbox y aplicador

**Files:**
- Create: `src/Modules/Pipeline/Modules.Pipeline.Infrastructure/Messaging/ClaimedOutboxConsumer.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Infrastructure/Messaging/QuotationEventPayloads.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Infrastructure/Messaging/QuotationEventsProcessor.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Infrastructure/Messaging/QuotationEventsWorker.cs`
- Modify: `src/Modules/Pipeline/Modules.Pipeline.Infrastructure/PipelineInfrastructureExtensions.cs`
- Modify: `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/PipelineApiHarness.cs` (sacar el worker del host; `InsertOutboxAsync`, `DrainQuotationEventsAsync`)
- Test: `tests/Modules/Pipeline/Modules.Pipeline.UnitTests/QuotationEventPayloadsTests.cs`
- Test: `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/QuotationEventsWorkerTests.cs`

**Interfaces:**
- Consumes: `PipelineDbContext.Outbox`/`Inbox`, `PipelineOutboxRecord`, `PipelineInboxMessage`, `PipelineInboxClaims.TryClaimAsync` (Task 6); `QuotationEventApplier`, `QuotationFact`, `OrderCancelledFact` (Task 10); `IPipelineUnitOfWork`; `IClock`.
- Produces:
  - `internal sealed class ClaimedOutboxConsumer(IServiceScopeFactory, string consumer, IReadOnlyList<string> eventNames, IReadOnlyList<TimeSpan> leases)` con `ProcessBatchAsync(Func<ClaimedMessage, CancellationToken, Task>, Action<Exception, PipelineOutboxRecord, int?>, CancellationToken)` y `MarkProcessedAsync(ClaimedMessage, CancellationToken)`; `internal sealed record ClaimedMessage(IServiceProvider Services, PipelineDbContext DbContext, PipelineOutboxRecord Record, DateTimeOffset Now)`.
  - `internal static class QuotationEventPayloads { bool TryReadQuotation(string json, out QuotationFact? fact); bool TryReadOrderCancelled(string json, out OrderCancelledFact? fact) }`.
  - `internal sealed partial class QuotationEventsProcessor` (singleton) con `Consumer = "pipeline.quotation-events"`, `SentEvent`, `ConvertedEvent`, `OrderCancelledEvent`, `ErrorAfterAttempts = 3`, `Claims` y `Task ProcessBatchAsync(CancellationToken)`.
  - `internal sealed partial class QuotationEventsWorker : BackgroundService` (cada 3 s).
  - Harness: `InsertOutboxAsync(string connectionString, string eventName, object payload)` → `Task<Guid>`; `DrainQuotationEventsAsync(WebApplicationFactory<Program>)`.

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Pipeline/Modules.Pipeline.UnitTests/QuotationEventPayloadsTests.cs`:

```csharp
using Modules.Pipeline.Infrastructure.Messaging;

namespace Modules.Pipeline.UnitTests;

/// <summary>Spec 2026-10-13 §4.5: el payload es contrato y se lee por nombre. Uno roto no se reintenta para siempre: se lee como
/// inválido y el worker lo marca procesado.</summary>
public sealed class QuotationEventPayloadsTests
{
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-0000000000e1");
    private static readonly Guid QuotationId = Guid.Parse("01900000-0000-7000-8000-0000000000e2");
    private static readonly Guid ClientId = Guid.Parse("01900000-0000-7000-8000-0000000000e3");
    private static readonly Guid AdvisorId = Guid.Parse("01900000-0000-7000-8000-0000000000e4");
    private static readonly Guid OrderId = Guid.Parse("01900000-0000-7000-8000-0000000000e5");

    private static string Sent(string extra = "") =>
        $$"""{"tenantId":"{{TenantId}}","quotationId":"{{QuotationId}}","quotationNumber":"COT-0007","clientId":"{{ClientId}}","advisorMemberId":"{{AdvisorId}}","total":1234.50,"currency":"USD","occurredAt":"2026-10-13T15:00:00+00:00"{{extra}}}""";

    [Fact]
    public void ASentPayloadIsReadByName()
    {
        Assert.True(QuotationEventPayloads.TryReadQuotation(Sent(), out var fact));

        Assert.Equal((TenantId, QuotationId, "COT-0007", ClientId, AdvisorId), (fact!.TenantId, fact.QuotationId, fact.QuotationNumber, fact.ClientId, fact.AdvisorMemberId));
        Assert.Equal((1234.50m, "USD", (Guid?)null), (fact.Total, fact.Currency, fact.OrderId));
    }

    [Fact]
    public void AConvertedPayloadCarriesTheOrder()
    {
        Assert.True(QuotationEventPayloads.TryReadQuotation(Sent($$""","orderId":"{{OrderId}}","orderNumber":"PED-0003" """), out var fact));

        Assert.Equal((Guid?)OrderId, fact!.OrderId);
    }

    [Fact]
    public void ACancelledPayloadIsReadByName()
    {
        var json = $$"""{"tenantId":"{{TenantId}}","orderId":"{{OrderId}}","quotationId":"{{QuotationId}}","occurredAt":"2026-10-13T15:00:00+00:00"}""";

        Assert.True(QuotationEventPayloads.TryReadOrderCancelled(json, out var fact));

        Assert.Equal((TenantId, OrderId, QuotationId), (fact!.TenantId, fact.OrderId, fact.QuotationId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""{"tenantId":"x"}""")]
    public void ABrokenPayloadIsNotRead(string json)
    {
        Assert.False(QuotationEventPayloads.TryReadQuotation(json, out _));
        Assert.False(QuotationEventPayloads.TryReadOrderCancelled(json, out _));
    }

    [Fact]
    public void ABlankNumberOrCurrencyIsNotRead()
    {
        Assert.False(QuotationEventPayloads.TryReadQuotation(Sent().Replace("\"COT-0007\"", "\"\"", StringComparison.Ordinal), out _));
        Assert.False(QuotationEventPayloads.TryReadQuotation(Sent().Replace("\"USD\"", "null", StringComparison.Ordinal), out _));
    }
}
```

En `PipelineApiHarness.cs`:

1. `using Microsoft.Extensions.Hosting;` y `using Modules.Pipeline.Infrastructure.Messaging;`.
2. Al final de `QepApiFactory.ConfigureWebHost`:

```csharp
            builder.ConfigureServices(services =>
            {
                // El worker toma mensajes cada 3 s por su cuenta: en una prueba competiría con DrainQuotationEventsAsync y la
                // volvería no determinista. Se saca el hosted service; el procesador (singleton) sigue registrado.
                var workers = services
                    .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                        && descriptor.ImplementationType == typeof(QuotationEventsWorker))
                    .ToList();
                foreach (var descriptor in workers)
                {
                    services.Remove(descriptor);
                }
            });
```

3. Dos helpers, después de `SeedConversationAsync`:

```csharp
    /// <summary>Un evento de Quotations en el outbox de plataforma, como lo escribe <c>QuotationLifecycleEventPublisher</c>
    /// (payload camelCase, Global Constraints del plan).</summary>
    public static async Task<Guid> InsertOutboxAsync(string connectionString, string eventName, object payload)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(connectionString,
            """
            INSERT INTO platform.outbox_messages (id, event_name, payload, correlation_id, occurred_at, processed_at, attempts, last_error)
            VALUES (@id, @name, CAST(@payload AS jsonb), @correlation, now(), NULL, 0, NULL)
            """,
            ("id", id),
            ("name", eventName),
            ("payload", JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))),
            ("correlation", id.ToString()));
        return id;
    }

    /// <summary>Una pasada del worker de eventos de Quotations: reclama y procesa lo pendiente.</summary>
    public static Task DrainQuotationEventsAsync(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<QuotationEventsProcessor>().ProcessBatchAsync(TestContext.Current.CancellationToken);
```

`tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/QuotationEventsWorkerTests.cs`:

```csharp
using static Modules.Pipeline.IntegrationTests.PipelineApiHarness;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>Spec 2026-10-13 §4.4, §4.5, D6 y P12 del plan: el worker consume los tres eventos con reclamo e inbox propio, cada uno
/// una sola vez, y guarda el efecto, el historial y la auditoría en el mismo SaveChanges que el inbox.</summary>
public sealed class QuotationEventsWorkerTests
{
    private const string Sent = "quotations.quotation.sent.v1";
    private const string Converted = "quotations.quotation.converted.v1";
    private const string Cancelled = "quotations.order.cancelled.v1";

    private static object SentPayload(Guid tenantId, Guid quotationId, Guid clientId, decimal total = 1_000m) => new
    {
        tenantId,
        quotationId,
        quotationNumber = "COT-0001",
        clientId,
        advisorMemberId = Guid.CreateVersion7(),
        total,
        currency = "COP",
        occurredAt = DateTimeOffset.UtcNow,
    };

    private static object ConvertedPayload(Guid tenantId, Guid quotationId, Guid clientId, Guid orderId) => new
    {
        tenantId,
        quotationId,
        quotationNumber = "COT-0001",
        clientId,
        advisorMemberId = Guid.CreateVersion7(),
        total = 1_000m,
        currency = "COP",
        occurredAt = DateTimeOffset.UtcNow,
        orderId,
        orderNumber = "PED-0001",
    };

    private static Task<string> StageOfAsync(string connectionString, Guid tenantId, Guid customerId) =>
        ScalarAsync<string>(connectionString,
            "SELECT s.name FROM pipeline.deals d JOIN pipeline.stages s ON s.id = d.stage_id WHERE d.tenant_id = @t AND d.customer_id = @c",
            ("t", tenantId), ("c", customerId));

    [Fact]
    public async Task SentConvertedAndCancelledFlowThroughTheWorker()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        var customerId = Guid.CreateVersion7();
        var quotationId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();

        await InsertOutboxAsync(connectionString, Sent, SentPayload(tenant.TenantId, quotationId, customerId));
        await DrainQuotationEventsAsync(factory);
        var afterSent = await StageOfAsync(connectionString, tenant.TenantId, customerId);
        await InsertOutboxAsync(connectionString, Converted, ConvertedPayload(tenant.TenantId, quotationId, customerId, orderId));
        await DrainQuotationEventsAsync(factory);
        var afterConverted = await StageOfAsync(connectionString, tenant.TenantId, customerId);
        await InsertOutboxAsync(connectionString, Cancelled, new { tenantId = tenant.TenantId, orderId, quotationId, occurredAt = DateTimeOffset.UtcNow });
        await DrainQuotationEventsAsync(factory);

        Assert.Equal(("Cotizado", "Ganado"), (afterSent, afterConverted));
        Assert.Equal("Cotizado", await StageOfAsync(connectionString, tenant.TenantId, customerId));
        Assert.Equal(3L, await CountAsync(connectionString,
            "SELECT count(*) FROM pipeline.inbox_messages WHERE consumer = 'pipeline.quotation-events' AND processed_at IS NOT NULL"));
        Assert.Equal(4L, await CountAsync(connectionString,
            "SELECT count(*) FROM pipeline.deal_stage_history WHERE tenant_id = @t AND actor_kind = 'System'", ("t", tenant.TenantId)));
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE tenant_id = @t AND action = 'pipeline.deal.reopened' AND actor_type = 'System'", ("t", tenant.TenantId)));
    }

    // Review Focus 1: el mismo mensaje dos veces no duplica el negocio.
    [Fact]
    public async Task AMessageIsAppliedOnceEvenIfDrainedTwice()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        var customerId = Guid.CreateVersion7();
        await InsertOutboxAsync(connectionString, Sent, SentPayload(tenant.TenantId, Guid.CreateVersion7(), customerId));

        await DrainQuotationEventsAsync(factory);
        await DrainQuotationEventsAsync(factory);

        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.deals WHERE customer_id = @c", ("c", customerId)));
        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.inbox_messages WHERE processed_at IS NOT NULL"));
    }

    [Fact]
    public async Task AMalformedPayloadIsMarkedProcessedWithoutEffect()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var messageId = await InsertOutboxAsync(connectionString, Sent, new { tenantId = "nope" });

        await DrainQuotationEventsAsync(factory);

        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM pipeline.inbox_messages WHERE message_id = @id AND processed_at IS NOT NULL", ("id", messageId)));
        Assert.Equal(0L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.deals"));
    }

    // P12: sin la capacidad, procesado sin efecto; activarla después no rellena el pasado.
    [Fact]
    public async Task ATenantWithoutTheCapabilityIsProcessedWithoutEffect()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await InsertOutboxAsync(connectionString, Sent, SentPayload(tenant.TenantId, Guid.CreateVersion7(), Guid.CreateVersion7()));

        await DrainQuotationEventsAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        await DrainQuotationEventsAsync(factory);

        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.inbox_messages WHERE processed_at IS NOT NULL"));
        Assert.Equal(0L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.deals"));
        Assert.Equal(0L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.deal_quotations"));
    }
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.UnitTests\Modules.Pipeline.UnitTests.csproj" --filter "FullyQualifiedName~QuotationEventPayloadsTests"
```

Esperado: `error CS0234: The type or namespace name 'Messaging' does not exist in the namespace 'Modules.Pipeline.Infrastructure'`.

- [ ] **Step 3: Implementar el consumidor, la lectura del payload, el procesador y el worker**

`src/Modules/Pipeline/Modules.Pipeline.Infrastructure/Messaging/ClaimedOutboxConsumer.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pipeline.Infrastructure.Persistence;

namespace Modules.Pipeline.Infrastructure.Messaging;

/// <summary>
/// Copia de <c>ClaimedOutboxConsumer</c> de Identity (P2 del plan; ningún módulo referencia la infraestructura de otro): armar el
/// lote, reclamar cada mensaje, procesarlo en su propio scope y no dejar que una falla corte el lote.
/// </summary>
/// <remarks>
/// <para><b>Lote.</b> Los mensajes de sus eventos sin fila en el inbox, o reclamados y sin terminar con el reclamo vencido, en
/// orden de llegada. Uno que espera su reintento no entra, así que no le quita lugar a otro.</para>
/// <para><b>Reclamo.</b> <see cref="PipelineInboxClaims"/> antes de procesar: una sentencia que se commitea sola y suma el
/// intento. El reclamo número n dura <c>leases[n - 1]</c>, y desde el último en adelante, el último. Nunca se abandona un mensaje:
/// un evento perdido dejaría un negocio sin ganar o sin reabrir.</para>
/// <para><b>Un scope por mensaje.</b> Con su propio <see cref="PipelineDbContext"/>: lo que dejó rastreado un mensaje que falla
/// muere con su scope.</para>
/// </remarks>
internal sealed class ClaimedOutboxConsumer(
    IServiceScopeFactory scopeFactory,
    string consumer,
    IReadOnlyList<string> eventNames,
    IReadOnlyList<TimeSpan> leases)
{
    public const int BatchSize = 20;

    public IReadOnlyList<TimeSpan> Leases => leases;

    public async Task ProcessBatchAsync(
        Func<ClaimedMessage, CancellationToken, Task> process,
        Action<Exception, PipelineOutboxRecord, int?> onFailure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(onFailure);
        List<PipelineOutboxRecord> pending;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<PipelineDbContext>();
            var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
            pending = await dbContext.Outbox
                .AsNoTracking()
                .Where(record => eventNames.Contains(record.EventName))
                .Where(record => !dbContext.Inbox.Any(entry =>
                    entry.Consumer == consumer
                    && entry.MessageId == record.Id
                    && (entry.ProcessedAt != null || entry.ClaimedUntil >= now)))
                .OrderBy(record => record.OccurredAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
        }

        foreach (var record in pending)
        {
            await using var recordScope = scopeFactory.CreateAsyncScope();
            var services = recordScope.ServiceProvider;
            int? attempts = null;
            try
            {
                var dbContext = services.GetRequiredService<PipelineDbContext>();
                var now = services.GetRequiredService<IClock>().UtcNow;
                attempts = await PipelineInboxClaims.TryClaimAsync(dbContext, consumer, record.Id, now, leases, cancellationToken);
                if (attempts is null)
                {
                    // Otra réplica lo tiene, o ya se terminó desde que se armó el lote.
                    continue;
                }

                await process(new ClaimedMessage(services, dbContext, record, now), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // El intento ya quedó contado en el reclamo: el mensaje vuelve cuando ese reclamo venza, siempre.
                onFailure(exception, record, attempts);
            }
        }
    }

    /// <summary>Marca como terminada la fila que dejó el reclamo, sin guardar: quien llama la guarda en el mismo SaveChanges que su
    /// efecto, así que un mensaje procesado nunca queda sin marcar.</summary>
    public async Task MarkProcessedAsync(ClaimedMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var entry = await message.DbContext.Inbox.SingleAsync(
            candidate => candidate.Consumer == consumer && candidate.MessageId == message.Record.Id, cancellationToken);
        entry.ProcessedAt = message.Now;
    }
}

/// <summary>Un mensaje ya reclamado, con el scope en el que se procesa y el instante del reclamo.</summary>
internal sealed record ClaimedMessage(IServiceProvider Services, PipelineDbContext DbContext, PipelineOutboxRecord Record, DateTimeOffset Now);
```

`src/Modules/Pipeline/Modules.Pipeline.Infrastructure/Messaging/QuotationEventPayloads.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Modules.Pipeline.Application;

namespace Modules.Pipeline.Infrastructure.Messaging;

/// <summary>Spec 2026-10-13 §4.5: el payload es contrato de Quotations y se lee por nombre (camelCase), como
/// <c>PaymentProofMoveProcessor</c> lee el de los comprobantes. Un payload roto es determinista: reintentarlo no lo arregla, así
/// que se lee como inválido y el worker lo marca procesado.</summary>
internal static class QuotationEventPayloads
{
    public static bool TryReadQuotation(string json, [NotNullWhen(true)] out QuotationFact? fact)
    {
        fact = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var number = root.GetProperty("quotationNumber").GetString();
            var currency = root.GetProperty("currency").GetString();
            if (string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(currency))
            {
                return false;
            }

            Guid? orderId = root.TryGetProperty("orderId", out var order) && order.ValueKind == JsonValueKind.String ? order.GetGuid() : null;
            fact = new QuotationFact(
                root.GetProperty("tenantId").GetGuid(),
                root.GetProperty("quotationId").GetGuid(),
                number,
                root.GetProperty("clientId").GetGuid(),
                root.GetProperty("advisorMemberId").GetGuid(),
                root.GetProperty("total").GetDecimal(),
                currency,
                orderId);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    public static bool TryReadOrderCancelled(string json, [NotNullWhen(true)] out OrderCancelledFact? fact)
    {
        fact = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            fact = new OrderCancelledFact(
                root.GetProperty("tenantId").GetGuid(),
                root.GetProperty("orderId").GetGuid(),
                root.GetProperty("quotationId").GetGuid());
            return true;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }
}
```

`src/Modules/Pipeline/Modules.Pipeline.Infrastructure/Messaging/QuotationEventsProcessor.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Pipeline.Application;
using Modules.Pipeline.Infrastructure.Persistence;

namespace Modules.Pipeline.Infrastructure.Messaging;

/// <summary>
/// Spec 2026-10-13 D6: consume del outbox de plataforma los tres eventos del ciclo de una cotización y delega en
/// <see cref="QuotationEventApplier"/>. El efecto (negocio, foto, historial y auditoría) y el inbox se guardan en el mismo
/// SaveChanges: un mensaje procesado nunca queda sin marcar, ni uno marcado sin su efecto. Singleton: el estado es del proceso.
/// La curva es la de <c>SessionRevocationWorker</c> (5 s, 30 s, 5 minutos): la falla más probable es una carrera de versión con
/// una edición del asesor, que se resuelve en el reintento.
/// </summary>
internal sealed partial class QuotationEventsProcessor(IServiceScopeFactory scopeFactory, ILogger<QuotationEventsProcessor> logger)
{
    internal const string Consumer = "pipeline.quotation-events";
    internal const string SentEvent = "quotations.quotation.sent.v1";
    internal const string ConvertedEvent = "quotations.quotation.converted.v1";
    internal const string OrderCancelledEvent = "quotations.order.cancelled.v1";

    /// <summary>Desde qué intento una falla se loguea en Error: el primero que deja la espera de 5 minutos por delante.</summary>
    internal const int ErrorAfterAttempts = 3;

    private static readonly IReadOnlyList<TimeSpan> RetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)];

    internal ClaimedOutboxConsumer Claims { get; } =
        new(scopeFactory, Consumer, [SentEvent, ConvertedEvent, OrderCancelledEvent], RetryDelays);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({EventName}) has a malformed payload; it is marked as processed without effect.")]
    private static partial void LogMalformed(ILogger logger, Guid messageId, string eventName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipeline could not claim outbox message {MessageId}; it is retried on the next tick.")]
    private static partial void LogClaimFailed(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipeline failed to apply outbox message {MessageId} ({EventName}) on attempt {Attempt}; it is retried when its claim expires.")]
    private static partial void LogAttemptFailed(ILogger logger, Exception exception, Guid messageId, string eventName, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Pipeline keeps failing to apply outbox message {MessageId} ({EventName}) on attempt {Attempt}; it is retried when its claim expires.")]
    private static partial void LogAttemptFailedRepeatedly(ILogger logger, Exception exception, Guid messageId, string eventName, int attempt);

    public Task ProcessBatchAsync(CancellationToken cancellationToken) =>
        Claims.ProcessBatchAsync(ProcessAsync, LogFailure, cancellationToken);

    private async Task ProcessAsync(ClaimedMessage message, CancellationToken cancellationToken)
    {
        var applier = message.Services.GetRequiredService<QuotationEventApplier>();
        var record = message.Record;
        var readable = true;
        switch (record.EventName)
        {
            case OrderCancelledEvent when QuotationEventPayloads.TryReadOrderCancelled(record.PayloadJson, out var cancelled):
                await applier.ApplyOrderCancelledAsync(cancelled, message.Now, cancellationToken);
                break;
            case SentEvent when QuotationEventPayloads.TryReadQuotation(record.PayloadJson, out var sent):
                await applier.ApplySentAsync(sent, message.Now, cancellationToken);
                break;
            case ConvertedEvent when QuotationEventPayloads.TryReadQuotation(record.PayloadJson, out var converted):
                await applier.ApplyConvertedAsync(converted, message.Now, cancellationToken);
                break;
            default:
                readable = false;
                break;
        }

        if (!readable)
        {
            LogMalformed(logger, record.Id, record.EventName);
        }

        await Claims.MarkProcessedAsync(message, cancellationToken);
        await message.Services.GetRequiredService<IPipelineUnitOfWork>().SaveChangesAsync(cancellationToken);
    }

    // Sin intento (null) falló el reclamo mismo: no se contó nada y el mensaje vuelve en el tick siguiente.
    private void LogFailure(Exception exception, PipelineOutboxRecord record, int? attempts)
    {
        if (attempts is not { } attempt)
        {
            LogClaimFailed(logger, exception, record.Id);
            return;
        }

        if (attempt >= ErrorAfterAttempts)
        {
            LogAttemptFailedRepeatedly(logger, exception, record.Id, record.EventName, attempt);
        }
        else
        {
            LogAttemptFailed(logger, exception, record.Id, record.EventName, attempt);
        }
    }
}
```

`src/Modules/Pipeline/Modules.Pipeline.Infrastructure/Messaging/QuotationEventsWorker.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Pipeline.Infrastructure.Messaging;

// Spec 2026-10-13 D6: cada 3 s, como los demás consumidores del outbox. Sólo el apagado del host lo detiene; un tick fallido se
// registra y el siguiente reintenta.
internal sealed partial class QuotationEventsWorker(QuotationEventsProcessor processor, ILogger<QuotationEventsWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    [LoggerMessage(Level = LogLevel.Error, Message = "Pipeline quotation events tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await processor.ProcessBatchAsync(stoppingToken);
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
}
```

`PipelineInfrastructureExtensions.cs`: `using Modules.Pipeline.Infrastructure.Messaging;` y, antes del `return services;`:

```csharp
        // Spec 2026-10-13 D6: el consumidor de los eventos de Quotations, con reclamo e inbox propio.
        services.AddSingleton<QuotationEventsProcessor>();
        services.AddHostedService<QuotationEventsWorker>();
```

- [ ] **Step 4: Correr y verificar que pasan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.UnitTests\Modules.Pipeline.UnitTests.csproj" --no-build --filter "FullyQualifiedName~QuotationEventPayloadsTests"
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.IntegrationTests\Modules.Pipeline.IntegrationTests.csproj" --no-build --filter "FullyQualifiedName~QuotationEventsWorkerTests|FullyQualifiedName~PipelinePersistenceTests"
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --no-build --filter "FullyQualifiedName~PipelineLayerTests|FullyQualifiedName~CompositionRootTests"
```

Esperado: 8 verdes en `QuotationEventPayloadsTests` (contando la teoría), 4 en `QuotationEventsWorkerTests`, `PipelinePersistenceTests` igual que antes (el harness cambió), capas y composición verdes.

- [ ] **Step 5: Commit**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
if ((git -C $B branch --show-current) -ne "feature/embudo-de-ventas") { throw "rama equivocada" }
git -C $B status --short
git -C $B add src/Modules/Pipeline/Modules.Pipeline.Infrastructure/Messaging src/Modules/Pipeline/Modules.Pipeline.Infrastructure/PipelineInfrastructureExtensions.cs tests/Modules/Pipeline/Modules.Pipeline.UnitTests/QuotationEventPayloadsTests.cs tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/PipelineApiHarness.cs tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/QuotationEventsWorkerTests.cs
git -C $B commit -m "feat(pipeline): worker que consume los eventos de cotización con reclamo e inbox"
git -C $B log -1 --format=%B
```

---

### Task 12: Tablero — columnas en cero, sumas por moneda, ventana de 30 días, tope de 200 y estancado

**Files:**
- Create: `src/Modules/Pipeline/Modules.Pipeline.Application/Board.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Infrastructure/Persistence/PipelineBoardQueries.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Api/BoardEndpoints.cs`
- Modify: `src/Modules/Pipeline/Modules.Pipeline.Api/PipelineEndpoints.cs` (`group.MapBoardEndpoints();`)
- Modify: `src/Modules/Pipeline/Modules.Pipeline.Infrastructure/PipelineInfrastructureExtensions.cs` (`IPipelineBoardQueries`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (un handler)
- Test: `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/BoardApiTests.cs`

**Interfaces:**
- Consumes: `IPipelineProvisioner`, `PipelineCaller`, directorios de la Task 7, `StallRules` (Task 4), `Currencies.OrderOf`, `CreateDealRequest`/`DealDetailDto` (Task 9), `QuotationEventApplier` (Task 10, para sembrar una principal en la prueba).
- Produces:
  - `IPipelineBoardQueries { Task<IReadOnlyList<BoardAggregateRow>> AggregateAsync(Guid tenantId, Guid? ownerMemberId, DateTimeOffset closedSince, ct); Task<IReadOnlyList<BoardCardRow>> ListCardsAsync(Guid tenantId, Guid? ownerMemberId, DateTimeOffset closedSince, int perStage, ct) }`.
  - `public sealed record BoardAggregateRow(Guid StageId, string Currency, int Count, decimal? Amount)`, `public sealed record BoardCardRow(Guid Id, Guid StageId, string Title, Guid CustomerId, Guid OwnerMemberId, decimal? Amount, string Currency, DateTimeOffset StageEnteredAt, DateTimeOffset LastChangedAt, string? PrimaryQuotationNumber, long Version)`.
  - DTOs (contrato, spec §6): `PipelineBoardDto(IReadOnlyList<BoardColumnDto> Columns, string Owner)`, `BoardColumnDto(Guid StageId, string Name, string Kind, int Probability, bool IsQuotationStage, int Count, IReadOnlyList<CurrencyTotalDto> Totals, IReadOnlyList<DealCardDto> Deals)`, `CurrencyTotalDto(string Currency, decimal Amount, decimal Weighted)`, `DealCardDto(Guid Id, string Title, Guid CustomerId, string CustomerName, bool CustomerIsComplete, decimal? Amount, string Currency, Guid OwnerMemberId, string OwnerName, bool IsStalled, int DaysInStage, string? PrimaryQuotationNumber, long Version)`.
  - `GetBoardQuery(Guid TenantId, string? Owner) → PipelineBoardDto`, `GetBoardValidator`, `GetBoardHandler`; `BoardLimits.CardsPerColumn = 200`, `BoardLimits.ClosedWindow = 30 días`.

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/BoardApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pipeline.Api;
using Modules.Pipeline.Application;
using static Modules.Pipeline.IntegrationTests.PipelineApiHarness;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>Spec 2026-10-13 §6, D22, D24, D33, D34 y P9 del plan: el tablero que la pantalla dibuja sin lógica propia.</summary>
public sealed class BoardApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, HttpClient Client, Guid CustomerId, Guid OwnerMemberId);

    private static async Task<Setup> SetupAsync(QepApiFactory factory, string connectionString)
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        var (customerId, _) = await SeedCustomerAsync(factory, tenant.TenantId, "Laura");
        var owner = await OwnerMembershipIdAsync(connectionString, tenant);
        return new Setup(factory, connectionString, tenant, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions), customerId, owner);
    }

    private static async Task<DealDetailDto> CreateAsync(Setup setup, decimal? amount, string? currency = null, Guid? owner = null, Guid? customerId = null)
    {
        var response = await setup.Client.PostAsJsonAsync($"{PipelineUrl(setup.Tenant.TenantId)}/deals",
            new CreateDealRequest(customerId ?? setup.CustomerId, "Dotación", amount, currency, null, owner), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;
    }

    private static async Task<PipelineBoardDto> BoardAsync(Setup setup, string? owner = null) =>
        (await setup.Client.GetFromJsonAsync<PipelineBoardDto>(
            $"{PipelineUrl(setup.Tenant.TenantId)}/board{(owner is null ? string.Empty : $"?owner={owner}")}", Ct))!;

    private static BoardColumnDto Column(PipelineBoardDto board, string name) => board.Columns.Single(column => column.Name == name);

    // BFF: todas las columnas, incluso en cero; sin owner, "me".
    [Fact]
    public async Task EveryColumnComesEvenAtZero()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;

        var board = await BoardAsync(setup);

        Assert.Equal("me", board.Owner);
        Assert.Equal(["Nuevo", "Calificado", "Cotizado", "Negociación", "Ganado", "Perdido"], board.Columns.Select(column => column.Name));
        Assert.All(board.Columns, column =>
        {
            Assert.Equal(0, column.Count);
            Assert.Empty(column.Totals);
            Assert.Empty(column.Deals);
        });
        Assert.Equal((50, true), (Column(board, "Cotizado").Probability, Column(board, "Cotizado").IsQuotationStage));
    }

    // Review Focus 5 (D24, D34): COP y USD salen como dos totales, en el orden del catálogo; el ponderado usa la probabilidad
    // de la etapa; el negocio sin monto cuenta pero no suma.
    [Fact]
    public async Task TotalsAreGroupedByCurrencyAndANoAmountDealOnlyCounts()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;
        await CreateAsync(setup, 500m, "USD");
        await CreateAsync(setup, 1_000_000m);
        await CreateAsync(setup, 200_000m);
        await CreateAsync(setup, null);

        var column = Column(await BoardAsync(setup), "Nuevo");

        Assert.Equal(4, column.Count);
        Assert.Equal(4, column.Deals.Count);
        Assert.Equal(
            [new CurrencyTotalDto("COP", 1_200_000m, 120_000m), new CurrencyTotalDto("USD", 500m, 50m)],
            column.Totals);
    }

    // D23: el filtro por dueño es foco; "all" ve todo el tenant.
    [Fact]
    public async Task OwnerMeShowsMineAndAllShowsEveryone()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (member, _) = await SeedMemberAsync(connectionString, setup.Tenant.TenantId, "Laura Gómez");
        await CreateAsync(setup, 100m);
        await CreateAsync(setup, 200m, owner: member);

        var mine = Column(await BoardAsync(setup, "me"), "Nuevo");
        var all = Column(await BoardAsync(setup, "all"), "Nuevo");
        var invalid = await setup.Client.GetAsync($"{PipelineUrl(setup.Tenant.TenantId)}/board?owner=team", Ct);

        Assert.Equal((1, setup.OwnerMemberId), (mine.Count, Assert.Single(mine.Deals).OwnerMemberId));
        Assert.Equal(2, all.Count);
        Assert.Contains(all.Deals, card => card.OwnerName == "Laura Gómez");
        Assert.Equal(["owner"], (await ProblemAsync(invalid)).ErrorKeys, StringComparer.OrdinalIgnoreCase);
    }

    // D33: Won y Lost sólo con lo cerrado en los últimos 30 días.
    [Fact]
    public async Task ClosedColumnsOnlyShowTheLastThirtyDays()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var recent = await CreateAsync(setup, 100m);
        var old = await CreateAsync(setup, 200m);
        foreach (var deal in new[] { recent, old })
        {
            (await setup.Client.PostAsJsonAsync($"{PipelineUrl(setup.Tenant.TenantId)}/deals/{deal.Id}/win", new DealVersionRequest(deal.Version), Ct)).EnsureSuccessStatusCode();
        }

        await ExecuteAsync(connectionString, "UPDATE pipeline.deals SET closed_at = now() - interval '31 days' WHERE id = @id", ("id", old.Id));

        var won = Column(await BoardAsync(setup), "Ganado");

        Assert.Equal(1, won.Count);
        Assert.Equal(recent.Id, Assert.Single(won.Deals).Id);
        Assert.Equal([new CurrencyTotalDto("COP", 100m, 100m)], won.Totals);
    }

    // D33 y P9: máximo 200 tarjetas, con el Count y las sumas de toda la columna; la que más lleva en la etapa, primero.
    [Fact]
    public async Task AColumnBringsAtMostTwoHundredCardsWithTheRealCount()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var stages = (await setup.Client.GetFromJsonAsync<PipelineStagesDto>($"{PipelineUrl(setup.Tenant.TenantId)}/settings/stages", Ct))!;
        await ExecuteAsync(connectionString,
            """
            INSERT INTO pipeline.deals (id, tenant_id, pipeline_id, customer_id, owner_member_id, title, amount, currency, stage_id,
                stage_entered_at, source, last_changed_at, created_at, updated_at, version)
            SELECT gen_random_uuid(), @t, @p, @c, @o, 'Carga ' || n, 10, 'COP', @s, now() - make_interval(mins => n), 'Manual', now(), now(), now(), 1
            FROM generate_series(1, 205) AS n
            """,
            ("t", setup.Tenant.TenantId), ("p", stages.PipelineId), ("c", setup.CustomerId), ("o", setup.OwnerMemberId), ("s", stages.Stages[0].Id));

        var column = Column(await BoardAsync(setup), "Nuevo");

        Assert.Equal((205, 200), (column.Count, column.Deals.Count));
        Assert.Equal([new CurrencyTotalDto("COP", 2_050m, 205m)], column.Totals);
        Assert.Equal("Carga 205", column.Deals[0].Title);
    }

    // D22 y §5.2: estancado si pasó más que los días de la etapa desde la última actividad; un mensaje del cliente cuenta.
    [Fact]
    public async Task AStalledDealIsFlaggedUntilTheCustomerWrites()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var deal = await CreateAsync(setup, 100m);
        await ExecuteAsync(connectionString,
            "UPDATE pipeline.deals SET stage_entered_at = now() - interval '4 days', last_changed_at = now() - interval '4 days' WHERE id = @id",
            ("id", deal.Id));

        var stalled = Assert.Single(Column(await BoardAsync(setup), "Nuevo").Deals);
        await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, setup.CustomerId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var active = Assert.Single(Column(await BoardAsync(setup), "Nuevo").Deals);

        Assert.Equal((true, 4), (stalled.IsStalled, stalled.DaysInStage));
        Assert.False(active.IsStalled);
    }

    // BFF: la tarjeta trae resueltos el cliente (con su completitud), el dueño y el número de la principal.
    [Fact]
    public async Task TheCardCarriesTheNamesAndThePrimaryNumber()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<QuotationEventApplier>().ApplySentAsync(
                new QuotationFact(setup.Tenant.TenantId, Guid.CreateVersion7(), "COT-0042", setup.CustomerId, setup.OwnerMemberId, 750m, "COP", null),
                DateTimeOffset.UtcNow, Ct);
            await scope.ServiceProvider.GetRequiredService<IPipelineUnitOfWork>().SaveChangesAsync(Ct);
        }

        var card = Assert.Single(Column(await BoardAsync(setup), "Cotizado").Deals);

        Assert.Equal(("COT-0042", (decimal?)750m, false), (card.PrimaryQuotationNumber, card.Amount, card.CustomerIsComplete));
        Assert.False(string.IsNullOrWhiteSpace(card.CustomerName));
        Assert.False(string.IsNullOrWhiteSpace(card.OwnerName));
    }
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.IntegrationTests\Modules.Pipeline.IntegrationTests.csproj" --filter "FullyQualifiedName~BoardApiTests"
```

Esperado: `error CS0246` sobre `PipelineBoardDto`, `BoardColumnDto`, `CurrencyTotalDto`.

- [ ] **Step 3: Implementar la consulta, el handler y la ruta**

`src/Modules/Pipeline/Modules.Pipeline.Application/Board.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pipeline.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pipeline.Application;

/// <summary>D33: los topes del tablero.</summary>
public static class BoardLimits
{
    public const int CardsPerColumn = 200;

    public static readonly TimeSpan ClosedWindow = TimeSpan.FromDays(30);
}

/// <summary>Por etapa y moneda: cuántos negocios y la suma de sus montos (nula si ninguno tiene monto, D34).</summary>
public sealed record BoardAggregateRow(Guid StageId, string Currency, int Count, decimal? Amount);

public sealed record BoardCardRow(
    Guid Id, Guid StageId, string Title, Guid CustomerId, Guid OwnerMemberId, decimal? Amount, string Currency,
    DateTimeOffset StageEnteredAt, DateTimeOffset LastChangedAt, string? PrimaryQuotationNumber, long Version);

/// <summary>D33 y P9 del plan: los agregados son de la columna entera; las tarjetas, las primeras <c>perStage</c> por etapa
/// (abiertas por <c>stage_entered_at</c> ascendente, cerradas por <c>closed_at</c> descendente). Cerrados sólo desde
/// <c>closedSince</c>. <c>ownerMemberId</c> nulo = todos.</summary>
public interface IPipelineBoardQueries
{
    Task<IReadOnlyList<BoardAggregateRow>> AggregateAsync(Guid tenantId, Guid? ownerMemberId, DateTimeOffset closedSince, CancellationToken cancellationToken);

    Task<IReadOnlyList<BoardCardRow>> ListCardsAsync(
        Guid tenantId, Guid? ownerMemberId, DateTimeOffset closedSince, int perStage, CancellationToken cancellationToken);
}

/// <summary>
/// BFF (§6): TODAS las columnas, incluso en cero. Una columna que desaparece obliga a la pantalla a conocer las etapas para
/// dibujar la que falta (precedente: <c>ReportStatusSliceDto</c>). Las sumas van por moneda (D24): una cotización puede ir en USD
/// y sumar pesos con dólares no significa nada.
/// </summary>
public sealed record PipelineBoardDto(IReadOnlyList<BoardColumnDto> Columns, string Owner);

/// <summary>D33: <see cref="Deals"/> trae como máximo 200; <see cref="Count"/> y <see cref="Totals"/> son de TODA la columna, para
/// que la pantalla diga «200 de 340» sin otra llamada. Won y Lost sólo cuentan lo cerrado en los últimos 30 días.</summary>
public sealed record BoardColumnDto(
    Guid StageId, string Name, string Kind, int Probability, bool IsQuotationStage,
    int Count, IReadOnlyList<CurrencyTotalDto> Totals, IReadOnlyList<DealCardDto> Deals);

/// <summary>Weighted = Σ amount × probability / 100, calculado en el servidor: la pantalla no repite la regla. Un negocio sin monto
/// (D34) no entra en ninguna suma.</summary>
public sealed record CurrencyTotalDto(string Currency, decimal Amount, decimal Weighted);

/// <summary>BFF: nombre del dueño, completitud del cliente, días en la etapa e IsStalled ya resueltos; la pantalla no tiene con qué
/// calcularlos (no ve membresías ni conversaciones).</summary>
public sealed record DealCardDto(
    Guid Id, string Title, Guid CustomerId, string CustomerName, bool CustomerIsComplete, decimal? Amount, string Currency,
    Guid OwnerMemberId, string OwnerName, bool IsStalled, int DaysInStage, string? PrimaryQuotationNumber, long Version);

public sealed record GetBoardQuery(Guid TenantId, string? Owner) : IQuery<PipelineBoardDto>;

public sealed class GetBoardValidator : AbstractValidator<GetBoardQuery>
{
    public GetBoardValidator() =>
        RuleFor(query => query.Owner).Must(owner => owner is null or "me" or "all").WithMessage("Owner must be 'me' or 'all'.");
}

public sealed class GetBoardHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IPipelineProvisioner provisioner,
    IPipelineBoardQueries board,
    IPipelineCustomerDirectory customers,
    IPipelineMemberNames members,
    IPipelineConversationDirectory conversations,
    PipelineCaller caller,
    IClock clock,
    IValidator<GetBoardQuery> validator)
    : IQueryHandler<GetBoardQuery, PipelineBoardDto>
{
    public async Task<PipelineBoardDto> HandleAsync(GetBoardQuery query, CancellationToken cancellationToken)
    {
        await PipelineAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, PipelinePermissions.DealRead, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var owner = query.Owner ?? "me";
        Guid? ownerId = owner == "me" ? await caller.RequireAsync(query.TenantId, cancellationToken) : null;
        var pipeline = await provisioner.EnsureAsync(query.TenantId, cancellationToken);
        var now = clock.UtcNow;
        var closedSince = now - BoardLimits.ClosedWindow;
        var aggregates = await board.AggregateAsync(query.TenantId, ownerId, closedSince, cancellationToken);
        var cards = await board.ListCardsAsync(query.TenantId, ownerId, closedSince, BoardLimits.CardsPerColumn, cancellationToken);

        var customerIds = cards.Select(card => card.CustomerId).Distinct().ToArray();
        var customerRefs = await customers.FindAsync(query.TenantId, customerIds, cancellationToken);
        var activity = await conversations.FindActivityAsync(query.TenantId, customerIds, cancellationToken);
        var names = await members.FindAsync(query.TenantId, cards.Select(card => card.OwnerMemberId).Distinct().ToArray(), cancellationToken);

        var columns = pipeline.OrderedStages.Select(stage =>
        {
            var stageAggregates = aggregates.Where(row => row.StageId == stage.Id).ToArray();
            var totals = stageAggregates
                .Where(row => row.Amount is not null)
                .OrderBy(row => Currencies.OrderOf(row.Currency))
                .ThenBy(row => row.Currency, StringComparer.Ordinal)
                .Select(row => new CurrencyTotalDto(row.Currency, row.Amount!.Value, Math.Round(row.Amount.Value * stage.Probability / 100m, 2)))
                .ToArray();
            var stageCards = cards
                .Where(card => card.StageId == stage.Id)
                .Select(card =>
                {
                    var customer = customerRefs.GetValueOrDefault(card.CustomerId);
                    var lastActivity = StallRules.LastActivity(card.LastChangedAt, activity.GetValueOrDefault(card.CustomerId)?.LastMessageAt);
                    return new DealCardDto(
                        card.Id, card.Title, card.CustomerId, customer?.Name ?? DealDetailBuilder.UnknownCustomerName, customer?.IsComplete ?? false,
                        card.Amount, card.Currency, card.OwnerMemberId, names.GetValueOrDefault(card.OwnerMemberId) ?? DealDetailBuilder.DeletedMemberName,
                        StallRules.IsStalled(stage.Kind, stage.StallDays, lastActivity, now), StallRules.DaysIn(card.StageEnteredAt, now),
                        card.PrimaryQuotationNumber, card.Version);
                })
                .ToArray();
            return new BoardColumnDto(
                stage.Id, stage.Name, stage.Kind.ToString(), stage.Probability, stage.IsQuotationStage,
                stageAggregates.Sum(row => row.Count), totals, stageCards);
        }).ToArray();

        return new PipelineBoardDto(columns, owner);
    }
}
```

`src/Modules/Pipeline/Modules.Pipeline.Infrastructure/Persistence/PipelineBoardQueries.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Pipeline.Application;

namespace Modules.Pipeline.Infrastructure.Persistence;

/// <summary>D33 y P9 del plan: todo se agrega en la base. Las tarjetas salen con ROW_NUMBER por etapa para no traer la columna
/// entera; usa <c>IX_deals_tenant_stage</c>.</summary>
internal sealed class PipelineBoardQueries(PipelineDbContext dbContext) : IPipelineBoardQueries
{
    public async Task<IReadOnlyList<BoardAggregateRow>> AggregateAsync(
        Guid tenantId, Guid? ownerMemberId, DateTimeOffset closedSince, CancellationToken cancellationToken)
    {
        var deals = dbContext.Deals.AsNoTracking()
            .Where(deal => deal.TenantId == tenantId && (deal.ClosedAt == null || deal.ClosedAt >= closedSince));
        if (ownerMemberId is { } owner)
        {
            deals = deals.Where(deal => deal.OwnerMemberId == owner);
        }

        var rows = await deals
            .GroupBy(deal => new { deal.StageId, deal.Currency })
            .Select(group => new { group.Key.StageId, group.Key.Currency, Count = group.Count(), Amount = group.Sum(deal => deal.Amount) })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(row => new BoardAggregateRow(row.StageId, row.Currency, row.Count, row.Amount))];
    }

    public async Task<IReadOnlyList<BoardCardRow>> ListCardsAsync(
        Guid tenantId, Guid? ownerMemberId, DateTimeOffset closedSince, int perStage, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Database.SqlQuery<CardRow>($"""
            SELECT d.id AS "Id", d.stage_id AS "StageId", d.title AS "Title", d.customer_id AS "CustomerId",
                   d.owner_member_id AS "OwnerMemberId", d.amount AS "Amount", d.currency AS "Currency",
                   d.stage_entered_at AS "StageEnteredAt", d.last_changed_at AS "LastChangedAt",
                   q.quotation_number AS "PrimaryQuotationNumber", d.version AS "Version"
            FROM (
                SELECT deals.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY deals.stage_id
                           ORDER BY CASE WHEN deals.closed_at IS NULL THEN deals.stage_entered_at END ASC NULLS LAST,
                                    deals.closed_at DESC NULLS LAST,
                                    deals.id) AS position
                FROM pipeline.deals
                WHERE deals.tenant_id = {tenantId}
                  AND (deals.closed_at IS NULL OR deals.closed_at >= {closedSince})
                  AND (CAST({ownerMemberId} AS uuid) IS NULL OR deals.owner_member_id = {ownerMemberId})
            ) AS d
            LEFT JOIN pipeline.deal_quotations AS q ON q.tenant_id = d.tenant_id AND q.quotation_id = d.primary_quotation_id
            WHERE d.position <= {perStage}
            ORDER BY d.stage_id, d.position
            """).ToListAsync(cancellationToken);
        return [.. rows.Select(row => new BoardCardRow(
            row.Id, row.StageId, row.Title, row.CustomerId, row.OwnerMemberId, row.Amount, row.Currency,
            row.StageEnteredAt, row.LastChangedAt, row.PrimaryQuotationNumber, row.Version))];
    }

    // Clase con setters y no un record: EF no ve a través del constructor de un record en SqlQuery (QuotationsReportSource).
    private sealed class CardRow
    {
        public Guid Id { get; init; }

        public Guid StageId { get; init; }

        public string Title { get; init; } = string.Empty;

        public Guid CustomerId { get; init; }

        public Guid OwnerMemberId { get; init; }

        public decimal? Amount { get; init; }

        public string Currency { get; init; } = string.Empty;

        public DateTimeOffset StageEnteredAt { get; init; }

        public DateTimeOffset LastChangedAt { get; init; }

        public string? PrimaryQuotationNumber { get; init; }

        public long Version { get; init; }
    }
}
```

`src/Modules/Pipeline/Modules.Pipeline.Api/BoardEndpoints.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Pipeline.Application;

namespace Modules.Pipeline.Api;

/// <summary>Spec 2026-10-13 §6: el tablero. <c>owner=me</c> por defecto (D23: foco, no seguridad).</summary>
internal static class BoardEndpoints
{
    public static void MapBoardEndpoints(this RouteGroupBuilder pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        pipeline.MapGet("/board", GetBoardAsync)
            .RequireAuthorization(PipelinePermissions.DealRead)
            .Produces<PipelineBoardDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> GetBoardAsync(Guid tenantId, string? owner, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetBoardQuery(tenantId, owner), cancellationToken));
}
```

`PipelineEndpoints.cs`: `group.MapBoardEndpoints();` después de `group.MapDealEndpoints();`.

`PipelineInfrastructureExtensions.cs`, junto a los repositorios: `services.AddScoped<IPipelineBoardQueries, PipelineBoardQueries>();`.

`QepServiceCollectionExtensions.cs`, después de los handlers de cotizaciones del negocio:

```csharp
        // Pipeline (spec 2026-10-13 §6): el tablero.
        services.AddScoped<IQueryHandler<GetBoardQuery, PipelineBoardDto>, GetBoardHandler>();
```

- [ ] **Step 4: Correr y verificar que pasan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.IntegrationTests\Modules.Pipeline.IntegrationTests.csproj" --no-build --filter "FullyQualifiedName~BoardApiTests"
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --no-build --filter "FullyQualifiedName~PipelineLayerTests|FullyQualifiedName~CompositionRootTests"
```

Esperado: 7 verdes en `BoardApiTests`; capas y composición verdes.

- [ ] **Step 5: Commit**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
if ((git -C $B branch --show-current) -ne "feature/embudo-de-ventas") { throw "rama equivocada" }
git -C $B status --short
git -C $B add src/Modules/Pipeline/Modules.Pipeline.Application/Board.cs src/Modules/Pipeline/Modules.Pipeline.Infrastructure/Persistence/PipelineBoardQueries.cs src/Modules/Pipeline/Modules.Pipeline.Infrastructure/PipelineInfrastructureExtensions.cs src/Modules/Pipeline/Modules.Pipeline.Api/BoardEndpoints.cs src/Modules/Pipeline/Modules.Pipeline.Api/PipelineEndpoints.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/BoardApiTests.cs
git -C $B commit -m "feat(pipeline): tablero con todas las columnas, sumas por moneda, tope de 200 y estancado"
git -C $B log -1 --format=%B
```

---

### Task 13: Triage — cola de pendientes (tope de 200) y decisiones crear o descartar

**Files:**
- Create: `src/Modules/Pipeline/Modules.Pipeline.Application/Triage.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Api/TriageEndpoints.cs`
- Modify: `src/Modules/Pipeline/Modules.Pipeline.Api/PipelineEndpoints.cs` (`group.MapTriageEndpoints();`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (dos handlers)
- Test: `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/TriageApiTests.cs`

**Interfaces:**
- Consumes: `IPipelineConversationDirectory`, `IPipelineCustomerDirectory` (Task 7), `IDealRepository.CustomersWithOpenDealsAsync`, `ITriageDecisionRepository`, `IPipelineReasonRepository.FindActiveAsync` (Task 6), `TriageRules`, `TriageDecision` (Task 4), `Deal.Create` (Task 5), `DealDetailBuilder` (Task 9), `ITenantDefaultCurrency`, `TenantModuleKeys.Messaging`.
- Produces:
  - `public static class TriageLimits { public const int MaxItems = 200; }`.
  - DTOs (contrato): `TriageItemDto(Guid ConversationId, Guid CustomerId, string CustomerName, bool CustomerIsComplete, DateTimeOffset? LastInboundAt)`, `TriageListDto(bool Enabled, int Count, IReadOnlyList<TriageItemDto> Items)`, `TriageResultDto(Guid ConversationId, string Decision, DealDetailDto? Deal)`.
  - `ListTriageQuery(Guid TenantId) → TriageListDto`, `DecideTriageCommand(Guid TenantId, Guid ConversationId, string? Action, string? Title, Guid? DismissReasonId) → TriageResultDto`, `DecideTriageValidator`, y los dos handlers.
  - Api: `DecideTriageRequest(string? Action, string? Title, Guid? DismissReasonId)`.
  - `internal static class TriageState { Task<(bool Pending, TriageDecision? Last)> EvaluateAsync(...) }` (lo reusa la Task 14).

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/TriageApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Modules.Pipeline.Api;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;
using static Modules.Pipeline.IntegrationTests.PipelineApiHarness;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>Spec 2026-10-13 §5.1, D20, D29, D30, D35 y D37: la cola de conversaciones pendientes de triage y las dos decisiones.</summary>
public sealed class TriageApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, HttpClient Client, Guid OwnerMemberId);

    private static async Task<Setup> SetupAsync(QepApiFactory factory, string connectionString, bool messaging = true)
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        if (messaging)
        {
            await EnableMessagingAsync(connectionString, tenant.TenantId);
        }

        var owner = await OwnerMembershipIdAsync(connectionString, tenant);
        return new Setup(factory, connectionString, tenant, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions), owner);
    }

    private static string TriageUrl(Guid tenantId) => $"{PipelineUrl(tenantId)}/triage";

    private static async Task<TriageListDto> ListAsync(Setup setup) =>
        (await setup.Client.GetFromJsonAsync<TriageListDto>(TriageUrl(setup.Tenant.TenantId), Ct))!;

    private static Task<HttpResponseMessage> DecideAsync(Setup setup, Guid conversationId, DecideTriageRequest request) =>
        setup.Client.PostAsJsonAsync($"{TriageUrl(setup.Tenant.TenantId)}/{conversationId}", request, Ct);

    [Fact]
    public async Task OnlyPendingConversationsAreListedNewestInboundFirst()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var now = DateTimeOffset.UtcNow;
        var (older, olderName) = await SeedCustomerAsync(factory, setup.Tenant.TenantId, "Pedro");
        var (newer, _) = await SeedCustomerAsync(factory, setup.Tenant.TenantId, "Laura");
        var (withDeal, _) = await SeedCustomerAsync(factory, setup.Tenant.TenantId, "Ana");
        var olderConversation = await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, older, now.AddDays(-1), now.AddDays(-1));
        var newerConversation = await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, newer, now.AddHours(-1), now.AddHours(-1));
        await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, withDeal, now, now);
        await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, older, now, now, resolved: true);
        await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, customerId: null, now, now);
        (await setup.Client.PostAsJsonAsync($"{PipelineUrl(setup.Tenant.TenantId)}/deals",
            new CreateDealRequest(withDeal, "Dotación", null, null, null, null), Ct)).EnsureSuccessStatusCode();

        var list = await ListAsync(setup);

        Assert.Equal((true, 2), (list.Enabled, list.Count));
        Assert.Equal([newerConversation, olderConversation], list.Items.Select(item => item.ConversationId));
        Assert.Equal((older, olderName, false), (list.Items[1].CustomerId, list.Items[1].CustomerName, list.Items[1].CustomerIsComplete));
    }

    // D37: máximo 200 con el Count real.
    [Fact]
    public async Task TheQueueBringsAtMostTwoHundredWithTheRealCount()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (customerId, _) = await SeedCustomerAsync(factory, setup.Tenant.TenantId, "Laura");
        await ExecuteAsync(connectionString,
            """
            INSERT INTO messaging.conversations (id, tenant_id, connection_id, user_id, customer_id, status, unread_count,
                last_inbound_at, last_activity_at, created_at, updated_at, version)
            SELECT gen_random_uuid(), @t, gen_random_uuid(), 'CO.' || md5(n::text), @c, 'Open', 0,
                now() - make_interval(mins => n), now(), now(), now(), 1
            FROM generate_series(1, 201) AS n
            """,
            ("t", setup.Tenant.TenantId), ("c", customerId));

        var list = await ListAsync(setup);

        Assert.Equal((201, 200), (list.Count, list.Items.Count));
        Assert.True(list.Items[0].LastInboundAt > list.Items[199].LastInboundAt);
    }

    // D29: crear deja un negocio de la conversación y la saca de la cola; una segunda decisión llega tarde.
    [Fact]
    public async Task CreatingADealFromTheChatTakesItOutOfTheQueue()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (customerId, customerName) = await SeedCustomerAsync(factory, setup.Tenant.TenantId, "Laura");
        var conversationId = await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, customerId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var response = await DecideAsync(setup, conversationId, new DecideTriageRequest("create", null, null));
        var late = await DecideAsync(setup, conversationId, new DecideTriageRequest("create", null, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<TriageResultDto>(Ct))!;
        Assert.Equal((conversationId, "Created"), (result.ConversationId, result.Decision));
        Assert.NotNull(result.Deal);
        Assert.Equal(("Conversation", (Guid?)conversationId, setup.OwnerMemberId, customerName), (result.Deal.Source, result.Deal.OriginConversationId, result.Deal.OwnerMemberId, result.Deal.Title));
        Assert.Equal(("Nuevo", (decimal?)null), (result.Deal.StageName, result.Deal.Amount));
        Assert.Empty((await ListAsync(setup)).Items);
        Assert.Equal(PipelineErrorCodes.TriageNotPending, (await ProblemAsync(late)).Code);
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE tenant_id = @t AND action = 'pipeline.triage.created'", ("t", setup.Tenant.TenantId)));
    }

    // D38: dos asesores deciden la misma conversación a la vez. Gana uno; el otro sale not_pending (si llegó después del
    // commit) o already_decided (si chocó con el índice), y queda un solo negocio.
    [Fact]
    public async Task TwoConcurrentDecisionsLeaveOneDeal()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        using var second = CreateClient(factory, setup.Tenant.OwnerUserId, setup.Tenant.TenantId, ManagePermissions);
        var (customerId, _) = await SeedCustomerAsync(factory, setup.Tenant.TenantId, "Laura");
        var conversationId = await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, customerId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var url = $"{TriageUrl(setup.Tenant.TenantId)}/{conversationId}";

        var responses = await Task.WhenAll(
            setup.Client.PostAsJsonAsync(url, new DecideTriageRequest("create", null, null), Ct),
            second.PostAsJsonAsync(url, new DecideTriageRequest("create", null, null), Ct));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        var loser = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.UnprocessableEntity);
        Assert.Contains((await ProblemAsync(loser)).Code, new[] { PipelineErrorCodes.TriageNotPending, PipelineErrorCodes.TriageAlreadyDecided });
        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.deals WHERE customer_id = @c", ("c", customerId)));
    }

    // D20: el descarte no es para siempre; D29: sólo create y dismiss.
    [Fact]
    public async Task ADismissalHidesItUntilTheCustomerWritesAgain()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (customerId, _) = await SeedCustomerAsync(factory, setup.Tenant.TenantId, "Laura");
        var conversationId = await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, customerId, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-5));
        var spam = (await setup.Client.GetFromJsonAsync<ReasonListDto>($"{PipelineUrl(setup.Tenant.TenantId)}/settings/dismiss-reasons", Ct))!
            .Items.Single(item => item.Name == "Spam").Id;

        var link = await DecideAsync(setup, conversationId, new DecideTriageRequest("link", null, null));
        var withoutReason = await DecideAsync(setup, conversationId, new DecideTriageRequest("dismiss", null, null));
        var unknownReason = await DecideAsync(setup, conversationId, new DecideTriageRequest("dismiss", null, Guid.CreateVersion7()));
        var dismissed = await DecideAsync(setup, conversationId, new DecideTriageRequest("dismiss", null, spam));
        var hidden = await ListAsync(setup);
        await ExecuteAsync(connectionString, "UPDATE messaging.conversations SET last_inbound_at = now() + interval '1 minute' WHERE id = @id", ("id", conversationId));
        var back = await ListAsync(setup);

        Assert.Equal(["action"], (await ProblemAsync(link)).ErrorKeys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(PipelineErrorCodes.TriageDismissReasonRequired, (await ProblemAsync(withoutReason)).Code);
        Assert.Equal(PipelineErrorCodes.TriageDismissReasonNotFound, (await ProblemAsync(unknownReason)).Code);
        Assert.Equal("Dismissed", (await dismissed.Content.ReadFromJsonAsync<TriageResultDto>(Ct))!.Decision);
        Assert.Empty(hidden.Items);
        Assert.Equal(conversationId, Assert.Single(back.Items).ConversationId);
    }

    // D30: sin messaging, la cola está vacía y oculta, y decidir es 403 con el código del módulo.
    [Fact]
    public async Task WithoutMessagingTheQueueIsDisabledAndDecisionsAre403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString, messaging: false);
        using var _ = setup.Client;
        var (customerId, _) = await SeedCustomerAsync(factory, setup.Tenant.TenantId, "Laura");
        var conversationId = await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, customerId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var list = await ListAsync(setup);
        var decide = await DecideAsync(setup, conversationId, new DecideTriageRequest("create", null, null));

        Assert.Equal((false, 0), (list.Enabled, list.Count));
        Assert.Empty(list.Items);
        Assert.Equal(HttpStatusCode.Forbidden, decide.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(decide)).Code);
    }

    // Review Focus 4: la conversación de otro tenant bajo la ruta propia es 404, no 403 ni "no pendiente".
    [Fact]
    public async Task AForeignConversationIs404()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var otherTenant = Guid.CreateVersion7();
        var (foreignCustomer, _) = await SeedCustomerAsync(factory, otherTenant, "Laura");
        var foreignConversation = await SeedConversationAsync(factory, connectionString, otherTenant, foreignCustomer, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var response = await DecideAsync(setup, foreignConversation, new DecideTriageRequest("create", null, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(PipelineErrorCodes.TriageConversationNotFound, (await ProblemAsync(response)).Code);
    }
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.IntegrationTests\Modules.Pipeline.IntegrationTests.csproj" --filter "FullyQualifiedName~TriageApiTests"
```

Esperado: `error CS0246` sobre `TriageListDto`, `TriageResultDto`, `DecideTriageRequest`.

- [ ] **Step 3: Implementar los handlers y la ruta**

`src/Modules/Pipeline/Modules.Pipeline.Application/Triage.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pipeline.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Pipeline.Application;

/// <summary>D37: la cola trae como máximo 200, con el Count real.</summary>
public static class TriageLimits
{
    public const int MaxItems = 200;
}

public sealed record TriageItemDto(Guid ConversationId, Guid CustomerId, string CustomerName, bool CustomerIsComplete, DateTimeOffset? LastInboundAt);

/// <summary>BFF (D30, D37): <see cref="Enabled"/> en <c>false</c> sin la capacidad <c>messaging</c>, y la bandeja oculta el
/// contador sin conocer las capacidades del tenant. <see cref="Count"/> es el real aunque <see cref="Items"/> traiga 200: es el
/// contador de la bandeja.</summary>
public sealed record TriageListDto(bool Enabled, int Count, IReadOnlyList<TriageItemDto> Items);

/// <summary>Con <c>Created</c> viaja el negocio entero, para que el panel del chat lo dibuje sin otra llamada.</summary>
public sealed record TriageResultDto(Guid ConversationId, string Decision, DealDetailDto? Deal);

public sealed record ListTriageQuery(Guid TenantId) : IQuery<TriageListDto>;

public sealed record DecideTriageCommand(Guid TenantId, Guid ConversationId, string? Action, string? Title, Guid? DismissReasonId)
    : ICommand<TriageResultDto>;

/// <summary>D29: sólo <c>create</c> y <c>dismiss</c>. Sin motivo de descarte NO es <c>validation.failed</c>: el contrato pide
/// <c>pipeline.triage.dismiss_reason_required</c> (P10 del plan).</summary>
public sealed class DecideTriageValidator : AbstractValidator<DecideTriageCommand>
{
    public DecideTriageValidator()
    {
        RuleFor(command => command.Action).Must(action => action is "create" or "dismiss").WithMessage("Action must be 'create' or 'dismiss'.");
        RuleFor(command => command.Title).MaximumLength(Deal.TitleMaxLength);
    }
}

/// <summary>§5.1: si una conversación está pendiente y cuál fue su última decisión. Lo comparten el triage y la ficha (D36).</summary>
internal static class TriageState
{
    public static async Task<(bool Pending, TriageDecision? Last)> EvaluateAsync(
        IDealRepository deals, ITriageDecisionRepository decisions, Guid tenantId, PipelineConversation conversation, CancellationToken cancellationToken)
    {
        var last = (await decisions.FindLatestAsync(tenantId, [conversation.Id], cancellationToken)).GetValueOrDefault(conversation.Id);
        var hasOpenDeal = conversation.CustomerId is { } customerId &&
            (await deals.CustomersWithOpenDealsAsync(tenantId, [customerId], cancellationToken)).Contains(customerId);
        return (TriageRules.IsPending(conversation.IsOpen, conversation.CustomerId, hasOpenDeal, conversation.LastInboundAt, last?.DecidedAgainstInboundAt), last);
    }

    /// <summary>D30: sin <c>messaging</c> no hay triage. Con el tenant simulado por el stub (sin fila), como <c>TenantModuleGuard</c>, no bloquea.</summary>
    public static async Task<bool> MessagingEnabledAsync(ITenantModules tenantModules, Guid tenantId, CancellationToken cancellationToken) =>
        await tenantModules.FindAsync(tenantId, cancellationToken) is not { } modules || modules.IsEnabled(TenantModuleKeys.Messaging);
}

public sealed class ListTriageHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IPipelineConversationDirectory conversations,
    IPipelineCustomerDirectory customers,
    IDealRepository deals,
    ITriageDecisionRepository decisions)
    : IQueryHandler<ListTriageQuery, TriageListDto>
{
    public async Task<TriageListDto> HandleAsync(ListTriageQuery query, CancellationToken cancellationToken)
    {
        await PipelineAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, PipelinePermissions.DealRead, cancellationToken);
        if (!await TriageState.MessagingEnabledAsync(tenantModules, query.TenantId, cancellationToken))
        {
            return new TriageListDto(false, 0, []);
        }

        // Ya vienen abiertas, con cliente y en el orden del contrato (D35, D37, P13).
        var open = await conversations.ListOpenAsync(query.TenantId, cancellationToken);
        var withDeals = await deals.CustomersWithOpenDealsAsync(query.TenantId, [.. open.Select(item => item.CustomerId!.Value).Distinct()], cancellationToken);
        var latest = await decisions.FindLatestAsync(query.TenantId, [.. open.Select(item => item.Id)], cancellationToken);
        var pending = open
            .Where(item => TriageRules.IsPending(
                item.IsOpen, item.CustomerId, withDeals.Contains(item.CustomerId!.Value), item.LastInboundAt, latest.GetValueOrDefault(item.Id)?.DecidedAgainstInboundAt))
            .ToArray();
        var page = pending.Take(TriageLimits.MaxItems).ToArray();
        var refs = await customers.FindAsync(query.TenantId, [.. page.Select(item => item.CustomerId!.Value).Distinct()], cancellationToken);

        return new TriageListDto(true, pending.Length, [.. page.Select(item =>
        {
            var customer = refs.GetValueOrDefault(item.CustomerId!.Value);
            return new TriageItemDto(
                item.Id, item.CustomerId.Value, customer?.Name ?? DealDetailBuilder.UnknownCustomerName, customer?.IsComplete ?? false, item.LastInboundAt);
        })]);
    }
}

/// <summary>§5.1 y D29: crear (dueño = quien llama, origen = la conversación, sin monto) o descartar con motivo del catálogo.
/// Otro asesor que se adelantó deja la conversación sin pendiente: <c>pipeline.triage.not_pending</c>; si los dos deciden a la
/// vez, la base deja pasar uno y el otro sale <c>pipeline.triage.already_decided</c> (D38).</summary>
public sealed class DecideTriageHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IPipelineConversationDirectory conversations,
    IPipelineCustomerDirectory customers,
    IPipelineProvisioner provisioner,
    IDealRepository deals,
    ITriageDecisionRepository decisions,
    IPipelineReasonRepository reasons,
    ITenantDefaultCurrency defaultCurrency,
    PipelineCaller caller,
    IPipelineUnitOfWork unitOfWork,
    IPipelineAuditRecorder audit,
    DealDetailBuilder detail,
    IClock clock,
    IValidator<DecideTriageCommand> validator)
    : ICommandHandler<DecideTriageCommand, TriageResultDto>
{
    public async Task<TriageResultDto> HandleAsync(DecideTriageCommand command, CancellationToken cancellationToken)
    {
        await PipelineAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, PipelinePermissions.DealManage, cancellationToken);
        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, command.TenantId, TenantModuleKeys.Messaging, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var conversation = await conversations.FindAsync(command.TenantId, command.ConversationId, cancellationToken)
            ?? throw PipelineNotFound.Conversation(command.ConversationId);
        var (pending, _) = await TriageState.EvaluateAsync(deals, decisions, command.TenantId, conversation, cancellationToken);
        if (!pending)
        {
            throw new PipelineDomainException(PipelineErrorCodes.TriageNotPending, "The conversation is no longer pending triage.");
        }

        var customerId = conversation.CustomerId!.Value;
        var actor = DealActor.ForUser(await caller.RequireAsync(command.TenantId, cancellationToken));
        var now = clock.UtcNow;

        if (command.Action == "dismiss")
        {
            if (command.DismissReasonId is not { } reasonId || reasonId == Guid.Empty)
            {
                throw new PipelineDomainException(PipelineErrorCodes.TriageDismissReasonRequired, "Dismissing needs a reason from the catalog.");
            }

            if (await reasons.FindActiveAsync(command.TenantId, ReasonKind.Dismiss, reasonId, cancellationToken) is null)
            {
                throw new PipelineDomainException(PipelineErrorCodes.TriageDismissReasonNotFound, "The dismiss reason is not an active reason of this tenant.");
            }

            var dismissal = TriageDecision.Dismissed(command.TenantId, conversation.Id, customerId, reasonId, conversation.LastInboundAt, actor, now);
            decisions.Add(dismissal);
            audit.Record(command.TenantId, executionContext.SubjectId, false, PipelineAuditActions.TriageDismissed, PipelineAuditActions.TriageResource, dismissal.Id, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return new TriageResultDto(conversation.Id, TriageDecisionKind.Dismissed.ToString(), null);
        }

        var customerName = (await customers.FindAsync(command.TenantId, [customerId], cancellationToken)).GetValueOrDefault(customerId)?.Name;
        var title = !string.IsNullOrWhiteSpace(command.Title) ? command.Title : customerName ?? DealDetailBuilder.UnknownCustomerName;
        title = title.Trim();
        if (title.Length > Deal.TitleMaxLength)
        {
            title = title[..Deal.TitleMaxLength];
        }

        var pipeline = await provisioner.EnsureAsync(command.TenantId, cancellationToken);
        var currency = await defaultCurrency.GetAsync(command.TenantId, cancellationToken);
        var deal = Deal.Create(command.TenantId, pipeline, customerId, actor.MemberId!.Value, title, null, currency, null,
            DealSource.Conversation, conversation.Id, actor, now);
        deals.Add(deal);
        // D38: si otro asesor decidió contra el mismo entrante entre la comprobación de arriba y este SaveChanges, el índice único
        // lo rechaza (pipeline.triage.already_decided) y este negocio no queda.
        var creation = TriageDecision.Created(command.TenantId, conversation.Id, customerId, deal.Id, conversation.LastInboundAt, actor, now);
        decisions.Add(creation);
        audit.Record(command.TenantId, executionContext.SubjectId, false, PipelineAuditActions.DealCreated, PipelineAuditActions.DealResource, deal.Id, now);
        audit.Record(command.TenantId, executionContext.SubjectId, false, PipelineAuditActions.TriageCreated, PipelineAuditActions.TriageResource, creation.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new TriageResultDto(conversation.Id, TriageDecisionKind.Created.ToString(), await detail.BuildAsync(pipeline, deal, cancellationToken));
    }
}
```

`src/Modules/Pipeline/Modules.Pipeline.Api/TriageEndpoints.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Pipeline.Application;

namespace Modules.Pipeline.Api;

/// <summary>Spec 2026-10-13 §5.1 y §6: la cola de triage y sus dos decisiones (D29).</summary>
internal static class TriageEndpoints
{
    public static void MapTriageEndpoints(this RouteGroupBuilder pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var triage = pipeline.MapGroup("/triage");

        triage.MapGet("/", ListAsync)
            .RequireAuthorization(PipelinePermissions.DealRead)
            .Produces<TriageListDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        triage.MapPost("/{conversationId:guid}", DecideAsync)
            .RequireAuthorization(PipelinePermissions.DealManage)
            .Accepts<DecideTriageRequest>("application/json")
            .Produces<TriageResultDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> ListAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListTriageQuery(tenantId), cancellationToken));

    private static async Task<IResult> DecideAsync(
        Guid tenantId, Guid conversationId, DecideTriageRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new DecideTriageCommand(tenantId, conversationId, request.Action, request.Title, request.DismissReasonId), cancellationToken));
}

/// <summary>D29: <c>action</c> es <c>create</c> o <c>dismiss</c>. <c>title</c> opcional (sin él, el nombre del cliente).</summary>
public sealed record DecideTriageRequest(string? Action, string? Title, Guid? DismissReasonId);
```

`PipelineEndpoints.cs`: `group.MapTriageEndpoints();` después de `group.MapBoardEndpoints();`.

`QepServiceCollectionExtensions.cs`, después del handler del tablero:

```csharp
        // Pipeline (spec 2026-10-13 §5.1): triage.
        services.AddScoped<IQueryHandler<ListTriageQuery, TriageListDto>, ListTriageHandler>();
        services.AddScoped<ICommandHandler<DecideTriageCommand, TriageResultDto>, DecideTriageHandler>();
```

- [ ] **Step 4: Correr y verificar que pasan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.IntegrationTests\Modules.Pipeline.IntegrationTests.csproj" --no-build --filter "FullyQualifiedName~TriageApiTests"
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --no-build --filter "FullyQualifiedName~PipelineLayerTests|FullyQualifiedName~CompositionRootTests"
```

Esperado: 7 verdes en `TriageApiTests`; capas y composición verdes.

- [ ] **Step 5: Commit**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
if ((git -C $B branch --show-current) -ne "feature/embudo-de-ventas") { throw "rama equivocada" }
git -C $B status --short
git -C $B add src/Modules/Pipeline/Modules.Pipeline.Application/Triage.cs src/Modules/Pipeline/Modules.Pipeline.Api/TriageEndpoints.cs src/Modules/Pipeline/Modules.Pipeline.Api/PipelineEndpoints.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/TriageApiTests.cs
git -C $B commit -m "feat(pipeline): triage de conversaciones con tope de 200 y decisiones crear o descartar"
git -C $B log -1 --format=%B
```

---

### Task 14: Resumen de negocios para la ficha del cliente (D36, §6.4)

**Files:**
- Create: `src/Modules/Pipeline/Modules.Pipeline.Application/CustomerSummary.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Api/CustomerSummaryEndpoints.cs`
- Modify: `src/Modules/Pipeline/Modules.Pipeline.Api/PipelineEndpoints.cs` (`group.MapCustomerSummaryEndpoints();`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (un handler)
- Test: `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/CustomerSummaryApiTests.cs`

**Interfaces:**
- Consumes: `IDealRepository.ListOpenByCustomerAsync`/`FindLastClosedByCustomerAsync`, `IDealQuotationRepository.FindAsync`, `IPipelineReasonRepository.FindNamesAsync` (Task 6), directorios (Task 7), `TriageState` (Task 13), `StallRules`, `DealDetailBuilder.DeletedMemberName` (Task 9), `PipelineNotFound`.
- Produces:
  - DTOs (contrato, spec §6.4): `CustomerDealsSummaryDto(Guid CustomerId, IReadOnlyList<CustomerOpenDealDto> OpenDeals, CustomerClosedDealDto? LastClosedDeal, ConversationTriageStateDto? Triage)`, `CustomerOpenDealDto(Guid Id, string Title, Guid StageId, string StageName, decimal? Amount, string Currency, Guid OwnerMemberId, string OwnerName, bool IsStalled, int DaysInStage, string? PrimaryQuotationNumber, long Version)`, `CustomerClosedDealDto(Guid Id, string Title, string Outcome, DateTimeOffset ClosedAt, string? LossReasonName)`, `ConversationTriageStateDto(Guid ConversationId, string Status, IReadOnlyList<string> AvailableActions, TriageDecisionDto? LastDecision)`, `TriageDecisionDto(string Kind, DateTimeOffset DecidedAt, string? DismissReasonName)`.
  - `GetCustomerDealsSummaryQuery(Guid TenantId, Guid CustomerId, Guid? ConversationId) → CustomerDealsSummaryDto` y su handler.

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/CustomerSummaryApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Modules.Pipeline.Api;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;
using static Modules.Pipeline.IntegrationTests.PipelineApiHarness;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>Spec 2026-10-13 D36 y §6.4: la sección de negocios de la ficha del cliente que se abre desde la conversación, dibujada
/// sin lógica propia. Sin la capacidad, sin permiso o desde otro tenant es 403 y la ficha oculta la sección.</summary>
public sealed class CustomerSummaryApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, HttpClient Client, Guid CustomerId);

    private static async Task<Setup> SetupAsync(QepApiFactory factory, string connectionString, bool messaging = true)
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        if (messaging)
        {
            await EnableMessagingAsync(connectionString, tenant.TenantId);
        }

        var (customerId, _) = await SeedCustomerAsync(factory, tenant.TenantId, "Laura");
        return new Setup(factory, connectionString, tenant, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions), customerId);
    }

    private static string SummaryUrl(Guid tenantId, Guid customerId, Guid? conversationId = null) =>
        $"{PipelineUrl(tenantId)}/customers/{customerId}/summary{(conversationId is { } id ? $"?conversationId={id}" : string.Empty)}";

    private static async Task<CustomerDealsSummaryDto> SummaryAsync(HttpClient client, Guid tenantId, Guid customerId, Guid? conversationId = null) =>
        (await client.GetFromJsonAsync<CustomerDealsSummaryDto>(SummaryUrl(tenantId, customerId, conversationId), Ct))!;

    private static async Task<DealDetailDto> CreateAsync(Setup setup, string title)
    {
        var response = await setup.Client.PostAsJsonAsync($"{PipelineUrl(setup.Tenant.TenantId)}/deals",
            new CreateDealRequest(setup.CustomerId, title, 100m, null, null, null), Ct);
        return (await response.Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;
    }

    [Fact]
    public async Task TheSheetShowsTheOpenDealsAndTheLastClosedAsContext()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var lost = await CreateAsync(setup, "Perdido antes");
        var price = (await setup.Client.GetFromJsonAsync<ReasonListDto>($"{PipelineUrl(setup.Tenant.TenantId)}/settings/loss-reasons", Ct))!
            .Items.Single(item => item.Name == "Precio").Id;
        (await setup.Client.PostAsJsonAsync($"{PipelineUrl(setup.Tenant.TenantId)}/deals/{lost.Id}/lose",
            new LoseDealRequest(price, null, lost.Version), Ct)).EnsureSuccessStatusCode();
        var first = await CreateAsync(setup, "Primero");
        var second = await CreateAsync(setup, "Segundo");

        var summary = await SummaryAsync(setup.Client, setup.Tenant.TenantId, setup.CustomerId);

        Assert.Equal(setup.CustomerId, summary.CustomerId);
        Assert.Equal([second.Id, first.Id], summary.OpenDeals.Select(deal => deal.Id));
        Assert.All(summary.OpenDeals, deal =>
        {
            Assert.Equal("Nuevo", deal.StageName);
            Assert.False(string.IsNullOrWhiteSpace(deal.OwnerName));
            Assert.Equal(((decimal?)100m, "COP", false), (deal.Amount, deal.Currency, deal.IsStalled));
        });
        Assert.NotNull(summary.LastClosedDeal);
        Assert.Equal((lost.Id, "Lost", "Precio"), (summary.LastClosedDeal.Id, summary.LastClosedDeal.Outcome, summary.LastClosedDeal.LossReasonName));
        Assert.Null(summary.Triage);
    }

    // §6.4: Pending ofrece create y dismiss (sólo con manage); después de descartar, Decided; con un negocio abierto, NotApplicable.
    [Fact]
    public async Task TheTriageStateFollowsTheConversation()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        using var reader = CreateClient(factory, setup.Tenant.OwnerUserId, setup.Tenant.TenantId, ReadPermissions);
        var conversationId = await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, setup.CustomerId,
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-5));
        var spam = (await setup.Client.GetFromJsonAsync<ReasonListDto>($"{PipelineUrl(setup.Tenant.TenantId)}/settings/dismiss-reasons", Ct))!
            .Items.Single(item => item.Name == "Spam").Id;

        var pending = (await SummaryAsync(setup.Client, setup.Tenant.TenantId, setup.CustomerId, conversationId)).Triage!;
        var pendingForReader = (await SummaryAsync(reader, setup.Tenant.TenantId, setup.CustomerId, conversationId)).Triage!;
        (await setup.Client.PostAsJsonAsync($"{PipelineUrl(setup.Tenant.TenantId)}/triage/{conversationId}",
            new DecideTriageRequest("dismiss", null, spam), Ct)).EnsureSuccessStatusCode();
        var decided = (await SummaryAsync(setup.Client, setup.Tenant.TenantId, setup.CustomerId, conversationId)).Triage!;
        await CreateAsync(setup, "Abierto");
        var notApplicable = (await SummaryAsync(setup.Client, setup.Tenant.TenantId, setup.CustomerId, conversationId)).Triage!;

        Assert.Equal(("Pending", conversationId), (pending.Status, pending.ConversationId));
        Assert.Equal(["create", "dismiss"], pending.AvailableActions);
        Assert.Empty(pendingForReader.AvailableActions);
        Assert.Equal("Decided", decided.Status);
        Assert.Empty(decided.AvailableActions);
        Assert.Equal(("Dismissed", "Spam"), (decided.LastDecision!.Kind, decided.LastDecision.DismissReasonName));
        Assert.Equal("NotApplicable", notApplicable.Status);
    }

    // D30: sin messaging no hay triage que ofrecer.
    [Fact]
    public async Task WithoutMessagingTheTriageIsNotApplicable()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString, messaging: false);
        using var _ = setup.Client;
        var conversationId = await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, setup.CustomerId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var triage = (await SummaryAsync(setup.Client, setup.Tenant.TenantId, setup.CustomerId, conversationId)).Triage!;

        Assert.Equal("NotApplicable", triage.Status);
        Assert.Empty(triage.AvailableActions);
    }

    [Fact]
    public async Task AConversationOfAnotherCustomerIs404()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (otherCustomer, _) = await SeedCustomerAsync(factory, setup.Tenant.TenantId, "Pedro");
        var otherConversation = await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, otherCustomer, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var response = await setup.Client.GetAsync(SummaryUrl(setup.Tenant.TenantId, setup.CustomerId, otherConversation), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(PipelineErrorCodes.TriageConversationNotFound, (await ProblemAsync(response)).Code);
    }

    // Review Focus 4: el cliente de otro tenant bajo la ruta propia es 404; la ruta ajena es 403.
    [Fact]
    public async Task AForeignCustomerIs404AndAnotherTenantsRouteIs403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setupA = await SetupAsync(factory, connectionString);
        var setupB = await SetupAsync(factory, connectionString);
        using var a = setupA.Client;
        using var b = setupB.Client;

        var foreignCustomer = await b.GetAsync(SummaryUrl(setupB.Tenant.TenantId, setupA.CustomerId), Ct);
        var foreignRoute = await b.GetAsync(SummaryUrl(setupA.Tenant.TenantId, setupA.CustomerId), Ct);

        Assert.Equal(HttpStatusCode.NotFound, foreignCustomer.StatusCode);
        Assert.Equal(PipelineErrorCodes.CustomerNotFound, (await ProblemAsync(foreignCustomer)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, foreignRoute.StatusCode);
    }

    // D36: sin la capacidad o sin permiso, 403, y la ficha oculta la sección.
    [Fact]
    public async Task WithoutTheCapabilityOrThePermissionIs403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        using var noPermission = CreateClient(factory, setup.Tenant.OwnerUserId, setup.Tenant.TenantId, "customers.customer.read");

        var forbidden = await noPermission.GetAsync(SummaryUrl(setup.Tenant.TenantId, setup.CustomerId), Ct);
        await DisableModuleAsync(connectionString, setup.Tenant.TenantId, "pipeline");
        var disabled = await setup.Client.GetAsync(SummaryUrl(setup.Tenant.TenantId, setup.CustomerId), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(disabled)).Code);
    }
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.IntegrationTests\Modules.Pipeline.IntegrationTests.csproj" --filter "FullyQualifiedName~CustomerSummaryApiTests"
```

Esperado: `error CS0246` sobre `CustomerDealsSummaryDto`.

- [ ] **Step 3: Implementar el handler y la ruta**

`src/Modules/Pipeline/Modules.Pipeline.Application/CustomerSummary.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Pipeline.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pipeline.Application;

/// <summary>
/// BFF (D36): la ficha del cliente dentro de la conversación dibuja esta sección sin lógica propia. La pantalla no ve etapas,
/// membresías, conversaciones ni decisiones de triage, así que el servidor resuelve todo: nombre de la etapa y del dueño,
/// estancado, días en la etapa, el último cerrado como contexto y qué acciones de triage ofrecer. Viaja aparte de la ficha de
/// Customers porque Customers no puede referenciar Pipeline.
/// </summary>
public sealed record CustomerDealsSummaryDto(
    Guid CustomerId,
    IReadOnlyList<CustomerOpenDealDto> OpenDeals,
    CustomerClosedDealDto? LastClosedDeal,
    ConversationTriageStateDto? Triage);

public sealed record CustomerOpenDealDto(
    Guid Id, string Title, Guid StageId, string StageName, decimal? Amount, string Currency,
    Guid OwnerMemberId, string OwnerName, bool IsStalled, int DaysInStage, string? PrimaryQuotationNumber, long Version);

/// <summary>El cerrado más reciente, ganado o perdido, como contexto («perdido hace 2 meses por precio»). Uno solo: el histórico
/// completo es del slice 2.</summary>
public sealed record CustomerClosedDealDto(Guid Id, string Title, string Outcome, DateTimeOffset ClosedAt, string? LossReasonName);

/// <summary>Status: <c>Pending</c> | <c>Decided</c> | <c>NotApplicable</c>. <c>NotApplicable</c> = la conversación no está abierta,
/// el cliente ya tiene un negocio abierto, o el tenant no tiene <c>messaging</c>. <see cref="AvailableActions"/> viene ya filtrado
/// por estado y por permiso (sin <c>pipeline.deal.manage</c>, vacío): la pantalla dibuja los botones que vienen, nada más.</summary>
public sealed record ConversationTriageStateDto(
    Guid ConversationId, string Status, IReadOnlyList<string> AvailableActions, TriageDecisionDto? LastDecision);

public sealed record TriageDecisionDto(string Kind, DateTimeOffset DecidedAt, string? DismissReasonName);

public sealed record GetCustomerDealsSummaryQuery(Guid TenantId, Guid CustomerId, Guid? ConversationId) : IQuery<CustomerDealsSummaryDto>;

public sealed class GetCustomerDealsSummaryHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IPipelineProvisioner provisioner,
    IDealRepository deals,
    IDealQuotationRepository quotations,
    IPipelineReasonRepository reasons,
    ITriageDecisionRepository decisions,
    IPipelineCustomerDirectory customers,
    IPipelineMemberNames members,
    IPipelineConversationDirectory conversations,
    IClock clock)
    : IQueryHandler<GetCustomerDealsSummaryQuery, CustomerDealsSummaryDto>
{
    private static readonly string[] TriageActions = ["create", "dismiss"];

    public async Task<CustomerDealsSummaryDto> HandleAsync(GetCustomerDealsSummaryQuery query, CancellationToken cancellationToken)
    {
        await PipelineAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, PipelinePermissions.DealRead, cancellationToken);
        if (!(await customers.FindAsync(query.TenantId, [query.CustomerId], cancellationToken)).ContainsKey(query.CustomerId))
        {
            throw PipelineNotFound.Customer(query.CustomerId);
        }

        var pipeline = await provisioner.EnsureAsync(query.TenantId, cancellationToken);
        var now = clock.UtcNow;
        var open = await deals.ListOpenByCustomerAsync(query.TenantId, query.CustomerId, cancellationToken);
        var lastClosed = await deals.FindLastClosedByCustomerAsync(query.TenantId, query.CustomerId, cancellationToken);
        var activity = (await conversations.FindActivityAsync(query.TenantId, [query.CustomerId], cancellationToken)).GetValueOrDefault(query.CustomerId);
        var names = await members.FindAsync(query.TenantId, [.. open.Select(deal => deal.OwnerMemberId).Distinct()], cancellationToken);

        var openDeals = new List<CustomerOpenDealDto>();
        foreach (var deal in open)
        {
            var stage = pipeline.FindStage(deal.StageId);
            var number = deal.PrimaryQuotationId is { } primaryId
                ? (await quotations.FindAsync(query.TenantId, primaryId, cancellationToken))?.QuotationNumber
                : null;
            var lastActivity = StallRules.LastActivity(deal.LastChangedAt, activity?.LastMessageAt);
            openDeals.Add(new CustomerOpenDealDto(
                deal.Id, deal.Title, deal.StageId, stage?.Name ?? string.Empty, deal.Amount, deal.Currency, deal.OwnerMemberId,
                names.GetValueOrDefault(deal.OwnerMemberId) ?? DealDetailBuilder.DeletedMemberName,
                stage is not null && StallRules.IsStalled(stage.Kind, stage.StallDays, lastActivity, now),
                StallRules.DaysIn(deal.StageEnteredAt, now), number, deal.Version));
        }

        CustomerClosedDealDto? closedDto = null;
        if (lastClosed is { ClosedAt: { } closedAt })
        {
            var reasonName = lastClosed.LossReasonId is { } reasonId
                ? (await reasons.FindNamesAsync(query.TenantId, [reasonId], cancellationToken)).GetValueOrDefault(reasonId)
                : null;
            var outcome = pipeline.FindStage(lastClosed.StageId)?.Kind ?? StageKind.Lost;
            closedDto = new CustomerClosedDealDto(lastClosed.Id, lastClosed.Title, outcome.ToString(), closedAt, reasonName);
        }

        var triage = query.ConversationId is { } conversationId
            ? await TriageAsync(query.TenantId, query.CustomerId, conversationId, open.Count > 0, cancellationToken)
            : null;
        return new CustomerDealsSummaryDto(query.CustomerId, openDeals, closedDto, triage);
    }

    private async Task<ConversationTriageStateDto> TriageAsync(
        Guid tenantId, Guid customerId, Guid conversationId, bool customerHasOpenDeal, CancellationToken cancellationToken)
    {
        var conversation = await conversations.FindAsync(tenantId, conversationId, cancellationToken);
        if (conversation is null || conversation.CustomerId != customerId)
        {
            throw PipelineNotFound.Conversation(conversationId);
        }

        if (!await TriageState.MessagingEnabledAsync(tenantModules, tenantId, cancellationToken))
        {
            return new ConversationTriageStateDto(conversationId, "NotApplicable", [], null);
        }

        var (pending, last) = await TriageState.EvaluateAsync(deals, decisions, tenantId, conversation, cancellationToken);
        var status = pending ? "Pending" : !conversation.IsOpen || customerHasOpenDeal ? "NotApplicable" : "Decided";
        IReadOnlyList<string> actions = pending && executionContext.HasPermission(PipelinePermissions.DealManage) ? TriageActions : [];
        TriageDecisionDto? lastDto = null;
        if (last is not null)
        {
            var reasonName = last.DismissReasonId is { } reasonId
                ? (await reasons.FindNamesAsync(tenantId, [reasonId], cancellationToken)).GetValueOrDefault(reasonId)
                : null;
            lastDto = new TriageDecisionDto(last.Kind.ToString(), last.DecidedAt, reasonName);
        }

        return new ConversationTriageStateDto(conversationId, status, actions, lastDto);
    }
}
```

`src/Modules/Pipeline/Modules.Pipeline.Api/CustomerSummaryEndpoints.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Pipeline.Application;

namespace Modules.Pipeline.Api;

/// <summary>Spec 2026-10-13 D36 y §6.4: la segunda llamada de la ficha del cliente. Lo sirve Pipeline porque Customers no puede
/// referenciarlo; con 403 la ficha oculta la sección.</summary>
internal static class CustomerSummaryEndpoints
{
    public static void MapCustomerSummaryEndpoints(this RouteGroupBuilder pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        pipeline.MapGet("/customers/{customerId:guid}/summary", GetSummaryAsync)
            .RequireAuthorization(PipelinePermissions.DealRead)
            .Produces<CustomerDealsSummaryDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> GetSummaryAsync(
        Guid tenantId, Guid customerId, Guid? conversationId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetCustomerDealsSummaryQuery(tenantId, customerId, conversationId), cancellationToken));
}
```

`PipelineEndpoints.cs`: `group.MapCustomerSummaryEndpoints();` después de `group.MapTriageEndpoints();`.

`QepServiceCollectionExtensions.cs`, después de los handlers de triage:

```csharp
        // Pipeline (spec 2026-10-13 D36): el resumen de negocios de la ficha del cliente.
        services.AddScoped<IQueryHandler<GetCustomerDealsSummaryQuery, CustomerDealsSummaryDto>, GetCustomerDealsSummaryHandler>();
```

- [ ] **Step 4: Correr y verificar que pasan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.IntegrationTests\Modules.Pipeline.IntegrationTests.csproj" --no-build --filter "FullyQualifiedName~CustomerSummaryApiTests"
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --no-build --filter "FullyQualifiedName~PipelineLayerTests|FullyQualifiedName~CompositionRootTests"
```

Esperado: 6 verdes en `CustomerSummaryApiTests`; capas y composición verdes.

- [ ] **Step 5: Commit**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
if ((git -C $B branch --show-current) -ne "feature/embudo-de-ventas") { throw "rama equivocada" }
git -C $B status --short
git -C $B add src/Modules/Pipeline/Modules.Pipeline.Application/CustomerSummary.cs src/Modules/Pipeline/Modules.Pipeline.Api/CustomerSummaryEndpoints.cs src/Modules/Pipeline/Modules.Pipeline.Api/PipelineEndpoints.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/CustomerSummaryApiTests.cs
git -C $B commit -m "feat(pipeline): resumen de negocios para la ficha del cliente en la conversación"
git -C $B log -1 --format=%B
```

---

### Task 15: README, `CLAUDE.md` y la suite completa contra la línea base

**Files:**
- Modify: `README.md` (§ «Módulos por tenant», `:638-656`; sección nueva «### Embudo de ventas (Pipeline)» antes de `## Verificación`, `:2053`)
- Modify: `CLAUDE.md` (gotcha de los módulos construidos, `:258-263`, y un gotcha nuevo al final de la lista)

**Interfaces:**
- Consumes: todo lo anterior y `baseline-failures.txt` de la Task 0.
- Produces: la documentación del contrato y la evidencia de que no hay regresiones.

- [ ] **Step 1: README — módulos por tenant**

En `README.md`, § «Módulos por tenant»:

1. La lista de claves: «`catalog`, `customers`, `companies`, `quotations`, `orders`, `reporting`, `pos`, `messaging` y `pipeline`».
2. Después de la oración de `messaging`, agregar: «`pipeline` (el embudo de ventas; último de la lista) exige `customers`, no viene con el signup y se prende igual. Sin `messaging`, su triage queda vacío; sin `quotations`, no le llegan eventos.»
3. «que siempre devuelve los ocho» → «que siempre devuelve los nueve».

- [ ] **Step 2: README — sección del embudo**

Antes de `## Verificación`:

````markdown
### Embudo de ventas (Pipeline)

El embudo del asesor (spec 2026-10-13): negocios en un kanban por etapas, enlazados al chat y a la
cotización, con historial de etapas desde el día 1 y señal de estancado. Módulo `pipeline` (depende de
`customers`, se prende por tenant), permisos `pipeline.deal.read|manage` (`admin` y `advisor`) y
`pipeline.settings.manage` (sólo `admin`), todos de núcleo: cada handler revisa la capacidad y responde 403
`tenancy.module_not_enabled`. Esquema `pipeline` (`pipelines`, `stages`, `deals`, `deal_stage_history`,
`deal_quotations`, `reasons`, `triage_decisions`, `inbox_messages`).

**Primer acceso.** El embudo no existe hasta que alguien lo pide: el primer `GET` (o el primer evento de una
cotización) lo crea con sus semillas —Nuevo 10 %/3 d, Calificado 25 %/5 d, Cotizado 50 %/7 d (etapa de
cotización), Negociación 75 %/7 d, Ganado, Perdido— y los motivos de pérdida y de descarte. Tenancy no avisa
cuando se prende un módulo, así que no hay otro lugar donde engancharse.

**Rutas** bajo `/api/v1/tenants/{tenantId}/pipeline`:

| Método | Ruta | Permiso |
| --- | --- | --- |
| `GET` | `/board?owner=me\|all` | `pipeline.deal.read` |
| `GET` | `/deals/{dealId}` | `pipeline.deal.read` |
| `POST` | `/deals` | `pipeline.deal.manage` |
| `PATCH` | `/deals/{dealId}` | `pipeline.deal.manage` |
| `POST` | `/deals/{dealId}/move\|win\|lose\|reopen` | `pipeline.deal.manage` |
| `POST` | `/deals/{dealId}/quotations/{quotationId}/link\|primary` | `pipeline.deal.manage` |
| `GET` | `/customers/{customerId}/summary?conversationId=` | `pipeline.deal.read` |
| `GET` | `/triage` | `pipeline.deal.read` |
| `POST` | `/triage/{conversationId}` (`create` \| `dismiss`) | `pipeline.deal.manage` |
| `GET`/`PUT` | `/settings/stages`, `/settings/loss-reasons`, `/settings/dismiss-reasons` | `read` / `pipeline.settings.manage` |

Toda escritura sobre un negocio lleva `version` (412 si cambió) y devuelve el negocio completo. El tablero
trae todas las columnas, sumas por moneda, como máximo 200 tarjetas por columna con el conteo real, y
`Ganado`/`Perdido` sólo de los últimos 30 días. El triage trae como máximo 200 conversaciones con el conteo
real. Cada decisión de triage guarda el último entrante contra el que se tomó: con el índice único
`IX_triage_decisions_conversation_inbound`, si dos asesores deciden a la vez uno recibe
`pipeline.triage.already_decided` y queda un solo negocio. Un mensaje nuevo del cliente permite decidir otra vez.

**Eventos de Quotations.** Quotations escribe en `platform.outbox_messages`, en la misma transacción que el
cambio, `quotations.quotation.sent.v1` (también al reenviar), `quotations.quotation.converted.v1` y
`quotations.order.cancelled.v1`, con payload camelCase (`tenantId`, `quotationId`, `quotationNumber`,
`clientId`, `advisorMemberId`, `total`, `currency`, `occurredAt`; converted suma `orderId` y `orderNumber`;
cancelled lleva `tenantId`, `orderId`, `quotationId`, `occurredAt`). El payload es contrato: cambiarlo
rompe a Pipeline sin error de compilación. `QuotationEventsWorker` los consume cada 3 s con reclamo e
inbox propio (consumidor `pipeline.quotation-events`, reintentos a los 5 s, 30 s y cada 5 min). Un evento
de un tenant sin `pipeline` se marca procesado sin efecto: prender el módulo después no rellena el pasado.

**Despliegue.** Backend primero (migraciones `AddPipelineModuleKey` en Tenancy e `InitialPipeline`),
después el frontend del embudo —kanban, panel del chat con triage, ajustes y la sección de negocios en la
ficha del cliente de la conversación—. Los permisos nuevos exigen reiniciar la API y recargar la sesión.
Prender `pipeline` al tenant desde la consola de operador; para el triage también hace falta `messaging`.
````

- [ ] **Step 3: `CLAUDE.md`**

En el gotcha de `Conversations` (`:258-263`), «Los quince módulos construidos son … Storage y Tenancy» → «Los dieciséis módulos construidos son Audit, Authorization, Catalog, Companies, Customers, Geography, Identity, Integrations, Messaging, Notifications, Pipeline, Pos, Quotations, Reporting, Storage y Tenancy».

Al final de la lista de gotchas, antes de `## Verificación`:

```markdown
- **El payload de los eventos de Quotations es contrato con Pipeline** (spec 2026-10-13 §4.5).
  `QuotationLifecycleEventPublisher` escribe `quotations.quotation.sent.v1`, `…converted.v1` y
  `quotations.order.cancelled.v1` con campos camelCase, y `QuotationEventPayloads` los lee por nombre: renombrar
  un campo compila, pasa las pruebas de Quotations y deja los mensajes marcados como procesados sin efecto. Se
  cambia en los dos lados, y en `QuotationEventPayloadsTests`. Pipeline lee las conversaciones de Messaging por
  `MessagingDbContext` desde Bootstrapper (`PipelineConversationDirectory`), como Reporting lee Quotations: no
  por un puerto de Messaging.
- **El embudo de un tenant nace en su primer acceso, no al prender el módulo.** Tenancy no publica nada al
  activar una capacidad; `PipelineProvisioner` crea el embudo con `INSERT … ON CONFLICT (tenant_id) DO NOTHING`.
  Una consulta directa a `pipeline.stages` de un tenant que nunca abrió el tablero devuelve cero filas: no es
  un error.
```

- [ ] **Step 4: Suite completa, una sola vez, contra la línea base**

Comprobar antes que no corre `Api.exe` (ver «Antes de empezar»).

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
$S = "$env:TEMP\embudo-baseline"
$F = "$env:TEMP\embudo-final"
if (Test-Path $F) { Remove-Item -Recurse -Force $F }
New-Item -ItemType Directory -Force $F | Out-Null
dotnet restore "$B\Backend.slnx" --locked-mode
if ($LASTEXITCODE -ne 0) { throw "los locks no coinciden con el grafo" }
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\Backend.slnx" --no-build --logger "trx" --results-directory $F
$final = foreach ($file in Get-ChildItem -LiteralPath $F -Filter *.trx) {
  [xml]$trx = Get-Content -LiteralPath $file.FullName -Raw
  $trx.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq "Failed" } | ForEach-Object { $_.testName }
}
$final = @($final | Sort-Object -Unique)
$baseline = @(Get-Content -LiteralPath "$S\baseline-failures.txt")
$new = @($final | Where-Object { $baseline -notcontains $_ })
"Fallas: $($final.Count). Previas: $($final.Count - $new.Count). Nuevas: $($new.Count)"
$new
```

Esperado: `Nuevas: 0`. El `restore --locked-mode` es lo que corre el `Dockerfile`: si falla con `NU1004`, falta commitear un `packages.lock.json` (volver a la Task 3). Si aparece una falla nueva:

1. Se corre **sólo su clase** otra vez, en primer plano. Si pasa, es una inestable conocida: se anota en el handoff con su nombre y la salida de las dos corridas.
2. Si vuelve a fallar, es una regresión: se detiene, se busca la causa con la clase sola y se arregla en la tarea que la introdujo (commit propio, `fix(pipeline): …`), no con un parche acá.

Se copian al handoff el resumen (`Fallas/Previas/Nuevas`) y la lista de nuevas (vacía).

- [ ] **Step 5: Commit de la documentación**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
if ((git -C $B branch --show-current) -ne "feature/embudo-de-ventas") { throw "rama equivocada" }
git -C $B status --short
git -C $B add README.md CLAUDE.md
git -C $B commit -m "docs(pipeline): contrato del embudo, eventos de cotización y gotchas del módulo"
git -C $B log -1 --format=%B
git -C $B log --oneline c74b955..HEAD
```

Esperado en el último comando: quince commits de este plan (Tasks 1–15), ninguno con `Co-Authored-By`.

---

## Cobertura del spec

| Spec | Dónde |
| --- | --- |
| §1, D1–D3 (módulo `Pipeline`, capas, capacidad `pipeline`) | Tasks 1 y 3 |
| D4 (embudo en el primer acceso, concurrente) | Task 6 (`PipelineProvisioner`, `ProvisioningSeedsOnceEvenWithTwoConcurrentCalls`) |
| D5, §4.1, D10–D12 (embudo y etapas) | Task 4 (dominio), Task 6 (FK de etapa), Task 8 (API) |
| D6, D7 (outbox + consumidor con reclamo) | Task 2 (productor), Task 11 (consumidor) |
| D9, §5.3 (puertos de lectura) | Task 7 |
| §4.2, D13–D16, D18, D19, D34 (negocio, transiciones, monto, historial) | Tasks 5, 6 y 9 |
| §4.3 (historial append-only, actor) | Tasks 5 y 6 (P4) |
| §4.4, D17, D25–D28 (reglas de cotización) | Task 10 (aplicador y enlace), Task 11 (worker) |
| §4.5 (tres eventos y su payload) | Task 2 y Task 11 (`QuotationEventPayloads`) |
| §5.1, D20, D29, D30, D35, D37 (triage) | Tasks 4 (reglas) y 13 |
| D38 (carrera del triage) | Task 4 (`DecidedAgainstInboundAt` y la regla), Task 6 (índice único y traducción, `ASecondDecisionAgainstTheSameInboundIsAlreadyDecidedAndLeavesOneDeal`), Task 13 (`TwoConcurrentDecisionsLeaveOneDeal`) |
| §5.2, D22 (estancado) | Task 4 (reglas), Tasks 9, 12 y 14 (al leer) |
| §6, D23, D24, D33 (API, permisos, tablero) | Tasks 3, 8, 9, 10, 12 y 13 |
| §6.2 (errores) | `PipelineErrorCodes` (Task 3) y las pruebas de cada tarea |
| §6.3 (auditoría) | Task 6 (recorder) y cada handler; el worker con `System` (Tasks 10 y 11) |
| D31 (semillas) | Tasks 4 y 6 |
| D32 (roles) | Task 3 |
| §6.4, D36 (ficha del cliente) | Task 14 |
| §7 (pruebas) | Todas; `PipelineLayerTests` en la Task 3 |
| §8, §9 (fuera de alcance, despliegue) | Task 15 (README) |

## Lo que el spec dice y el plan ajusta

- **§6.1 `RequiredModules: [ModuleKeys.Pipeline]`** contradice §6.2/§6.4 (`tenancy.module_not_enabled`): con el permiso atado al módulo, la máscara lo quita y el 403 sale `authorization.denied`. El plan los deja de núcleo (P1), como Messaging. Para la ficha (D36) da igual —cualquier 403 oculta la sección—, pero el código del contrato cambia.
- **H10/D6 «reclamo + inbox como Storage»:** Storage no reclama (anti-join). El plan copia el consumidor de Identity (P2), que es el que reclama.
- **Códigos y límites que el spec no fija:** P6 y P7.
- **D12 «no se borra una etapa con negocios»:** además de la comprobación del handler, una FK `deals.stage_id → stages.id` con `RESTRICT` cierra la carrera entre la lectura y el commit; la unidad de trabajo la traduce a `pipeline.stage.has_deals` por nombre de restricción.

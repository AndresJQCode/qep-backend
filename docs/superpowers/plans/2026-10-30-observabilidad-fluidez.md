# Observabilidad y fluidez — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ver en un dashboard si QEP se siente rápido y si los webhooks van atrasados, y que Telegram avise antes que un cliente.

**Architecture:** El backend publica métricas nuevas por OpenTelemetry (colas por muestreo SQL, rezago del entrante, y lo que manda el navegador por un endpoint autenticado). El frontend mide navegación y Web Vitals y los envía por lotes. Dashboard y alertas viven en `migracion-k8s`.

**Tech Stack:** .NET 10, OpenTelemetry 1.16, `System.Diagnostics.Metrics`, xUnit, Testcontainers; React 19, TanStack Router y Query, `web-vitals`, Vitest; Grafana, Prometheus, Alertmanager.

**Spec:** `docs/superpowers/specs/2026-10-30-observabilidad-fluidez-design.md`

## Global Constraints

- Tres repos. Backend: worktree `qep-backend-worktrees/observabilidad`. Frontend: `qep-frontend-worktrees/observabilidad`. Ambos en `feature/observabilidad`. Plataforma: `~/migracion-k8s`, desde WSL.
- **Rutas absolutas y `Set-Location` al worktree en cada comando**: el cwd del shell vuelve al checkout principal. `git status` del checkout principal después de cada tarea: debe seguir igual.
- Nunca editar `.cs` con `Set-Content`: rompe tildes.
- Commits convencionales **sin** `Co-Authored-By` ni atribución de IA, aunque el harness lo pida. Revisar cada commit.
- `git branch --show-current` justo antes de cada commit, con guard que aborta.
- TDD: RED y GREEN con salida literal. Por tarea, sólo las clases tocadas. Suite completa una sola vez al final.
- `$env:Seed__Enabled = "false"` para pruebas de integración. Corridas en primer plano. Sin `Api.exe` corriendo.
- Sin etiqueta de tenant en ninguna métrica. Ningún dato personal en etiquetas.
- `docs/estado.md` no se toca en la rama: se actualiza en `develop` al mergear. Nada va a `main`.
- `kubectl apply` y push a remotos requieren OK del owner. `--context contabo-prod` siempre.
- Comentarios y textos dirigidos al lector, tuteando (español colombiano) en backend. Identificadores en inglés.

## Review Focus

1. **Reloj de Meta adelantado o timestamp en el futuro:** el rezago se registra en 0, nunca negativo (Task 2).
2. **Cola vacía:** `backlog` y `retrying` publican 0 y `oldest_age` publica 0. Si desaparecieran, dispararían `QepQueueMetricsMissing` en el estado más sano posible (Tasks 3 y 4).
3. **Redirección durante la navegación** (`SessionGate`, `ActiveTenantGate` mandan a otra ruta): una sola muestra, con el `routeId` final (Task 8).
4. **Consultas que nunca se quedan quietas** (polling, invalidaciones por SignalR): a los 15 s se descarta; jamás se reporta una navegación de 15 s (Task 8).
5. **Cierre de sesión con muestras en búfer:** sin tenant activo no se envía nada, el búfer no pasa de 50 y conserva las más nuevas (Task 7).

---

## Parte A. Backend (`qep-backend`)

### Task 1: Meters `Qep.*` y bucket de 2 s

**Files:**
- Modify: `src/BuildingBlocks/BuildingBlocks.Observability/QepObservability.cs`
- Test: `tests/ArchitectureTests/ArchitectureTests/ObservabilityConfigurationTests.cs`

**Interfaces:**
- Produces: `QepObservability.MeterName` sigue siendo `"Qep.Platform"`. `public const string MeterWildcard = "Qep.*"`. `public static readonly double[] HttpServerDurationBoundaries`.

- [ ] **Step 1: Escribir las pruebas que fallan**

Sin paquetes nuevos: un `BaseExporter<Metric>` de prueba con `BaseExportingMetricReader`, agregado por `ConfigureOpenTelemetryMeterProvider(b => b.AddReader(...))`.

`AMeterUnderTheQepPrefixIsExported`: crea `new Meter("Qep.Example")`, registra un contador, `ForceFlush`, y afirma que el exportador recibió `Qep.Example`.

`HttpServerDurationHasTheTwoSecondBucket`: crea `new Meter("Microsoft.AspNetCore.Hosting")`, histograma `http.server.request.duration`, registra `1.5`, y afirma que los límites exportados son exactamente `0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2, 2.5, 5, 7.5, 10`.

- [ ] **Step 2: Correr y confirmar RED** con `dotnet test tests/ArchitectureTests/ArchitectureTests --filter ObservabilityConfigurationTests`. Esperado: ambas fallan.

- [ ] **Step 3: Implementar.** `.AddMeter(MeterWildcard)` en lugar de `.AddMeter(MeterName)`; `.AddView("http.server.request.duration", new ExplicitBucketHistogramConfiguration { Boundaries = HttpServerDurationBoundaries })`. Comentario con el porqué: la meta de «Enviar mensaje» es 2 s y el defecto salta de 1 a 2,5.

- [ ] **Step 4: Correr y confirmar GREEN**, incluidas las pruebas previas de la clase.

- [ ] **Step 5: Commit** `feat(observability): export Qep meters and add a 2s http bucket`

### Task 2: Rezago del entrante

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/MessagingMetrics.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookDeliveryProcessor.cs:171-176`, `WebhookDeliveryProcessor.Instagram.cs:168`, el registro de DI de `MessagingInfrastructureExtensions.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/InboundLagMetricTests.cs`

**Interfaces:**
- Produces: `internal sealed class MessagingMetrics(IMeterFactory meterFactory)` con `Meter Meter` y `void RecordInboundLag(string channel, DateTimeOffset occurredAt, DateTimeOffset storedAt)`. Meter `"Qep.Messaging"`; histograma `qep.messaging.inbound.lag`, unidad `s`, etiqueta `channel` (`whatsapp` | `instagram`), buckets con `InstrumentAdvice<double>`: `1, 2, 5, 10, 30, 60, 300, 3600`.

- [ ] **Step 1: Escribir las pruebas que fallan.** `MeterListener` filtrado por la instancia de `Meter` resuelta de `factory.Services`, para que las pruebas paralelas no se crucen.

- `ANewInboundRecordsItsLag`: webhook con `timestamp` de hace 30 s; una medición ≥ 29 con `channel=whatsapp`.
- `AMetaRetryDoesNotRecordAgain`: mismo mensaje dos veces (`wamid` igual, cuerpo distinto para que no lo frene `body_sha256`); una sola medición.
- `AFutureTimestampRecordsZero`: `timestamp` a +60 s; medición `0`.
- `InstagramRecordsWithItsChannel`: por `PostInstagramWebhookAsync`; `channel=instagram`.

Usa `PostWebhookAsync` y el drenado del harness. Fixtures de Meta con `from_user_id`, o se descartan en silencio.

- [ ] **Step 2: Correr y confirmar RED** con `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter InboundLagMetricTests`.

- [ ] **Step 3: Implementar.** Singleton. En la rama `ingested is not null`, con `clock.UtcNow` tomado **ahí** (el `now` de la línea 161 es de antes del bucle). `Math.Max(0, …)`. Se registra antes de `NotifyAsync`, que puede fallar.

- [ ] **Step 4: Correr y confirmar GREEN.**

- [ ] **Step 5: Commit** `feat(messaging): measure inbound lag from Meta timestamp`

### Task 3: Lectura de colas

**Files:**
- Create: `src/Bootstrapper/Observability/QueueNames.cs`, `src/Bootstrapper/Observability/IQueueMetricsSource.cs`, `src/Bootstrapper/Observability/QueueMetricsSource.cs`
- Modify: `CLAUDE.md` (gotcha)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/QueueMetricsSourceTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record QueueReading(string Queue, long Backlog, long Retrying, double? OldestAgeSeconds);
  public interface IQueueMetricsSource { Task<QueueReading> ReadAsync(string queue, CancellationToken cancellationToken); }
  public static class QueueNames { Webhook = "webhook"; Automation = "automation"; Media = "media"; Campaign = "campaign"; Outbox = "outbox"; ShopifyWebhook = "shopify_webhook"; public static IReadOnlyList<string> All; }
  ```
  Scoped. SQL crudo sobre la conexión de `MessagingDbContext`, como `PipelineConversationDirectory`, con `CommandTimeout = 5`.

- [ ] **Step 1: Escribir las pruebas que fallan.** Con `QepApiFactory`, `ExecuteAsync` para sembrar y el puerto resuelto en un scope. Cada una contra el esquema migrado:

- `AnEmptyQueueReadsZero`: `Theory` por las seis; `Backlog == 0`, `Retrying == 0`, y `OldestAgeSeconds == 0` salvo `campaign`, que da `null`.
- `WebhookAgeIgnoresRowsAlreadyRetried`: fila de hace 1 h con `attempts = 3` y otra de hace 60 s con `attempts = 0`; backlog 2, retrying 1, edad ≈ 60.
- `AutomationAgeSkipsRowsHeldByAnOlderPendingRow`: en una conversación, fila con `attempts = 2` y otra posterior con `attempts = 0`; edad 0. En otra conversación, una con `attempts = 0` hace 30 s; edad ≈ 30.
- `GivenUpMediaIsNotBacklog`: `next_attempt_at = '9999-01-01'`; backlog 0.
- `ImportedMediaCountsAsBacklogButNotAge`: mensaje con `imported = true` de hace 2 h; backlog 1, edad 0.
- `CampaignCountsOnlySendingAndFrozen`: destinatario `Pending` en campaña sin `audience_frozen_at`; backlog 0. Con congelada y `Sending`; backlog 1.
- `OutboxAgeUsesOccurredAt` y `ShopifyWebhookAgeUsesReceivedAt`.

- [ ] **Step 2: Correr y confirmar RED.**

- [ ] **Step 3: Implementar.** Predicados de la tabla del spec. Forma común:
  ```sql
  SELECT count(*), count(*) FILTER (WHERE attempts > 0),
         COALESCE(EXTRACT(EPOCH FROM now() - min(<edad>) FILTER (WHERE attempts = 0 <extra>)), 0)
  FROM <tabla> WHERE <pendiente>
  ```
  `<extra>` de `automation` es el `NOT EXISTS` de una fila anterior sin procesar en la misma conversación, igual que `AutomationInbox.ClaimAsync`. `automation_inbox.attempts` es `short`. Un nombre fuera de `QueueNames.All` lanza `ArgumentOutOfRangeException`.

- [ ] **Step 4: Correr y confirmar GREEN.**

- [ ] **Step 5: Agregar a `CLAUDE.md` el gotcha:** `QueueMetricsSource` lee seis tablas por nombre; renombrar una columna compila y deja esa cola sin dato (dispara `QepQueueMetricsMissing` si es `webhook`); una cola nueva entra en `QueueNames`, en estas pruebas, en el dashboard y en las reglas.

- [ ] **Step 6: Commit** `feat(observability): read queue depth and oldest age`

### Task 4: Muestreador y gauges

**Files:**
- Create: `src/Bootstrapper/Observability/QueueGauges.cs`, `src/Bootstrapper/Observability/QueueSamplerWorker.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs`, `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs` (lista de hosted services)
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/QueueGaugesTests.cs`, `QueueSamplerWorkerTests.cs`

**Interfaces:**
- Consumes: `IQueueMetricsSource`, `QueueNames`, `QueueReading`.
- Produces: `public sealed class QueueGauges(IMeterFactory, IClock)` con `void Record(QueueReading reading)` y `Meter Meter`. Gauges `qep.queue.backlog`, `qep.queue.retrying`, `qep.queue.oldest_age` (unidad `s`), etiqueta `queue`, meter `QepObservability.MeterName`. `public sealed class QueueSamplerWorker : BackgroundService`, cada 15 s, con `internal Task SampleAsync(CancellationToken)`.

- [ ] **Step 1: Escribir las pruebas que fallan.**

- `PublishesTheLastReading`: `Record` y `RecordObservableInstruments` con `MeterListener`; tres valores con `queue=webhook`.
- `ACampaignPublishesNoAge`: `OldestAgeSeconds = null`; sin medición de `oldest_age`.
- `AReadingOlderThanSixtySecondsStopsPublishing`: reloj falso +61 s; ninguna medición de esa cola, las demás siguen.
- `OneFailingQueueDoesNotStopTheOthers`: doble que lanza en `media`; las otras cinco quedan registradas y `SampleAsync` no lanza.

- [ ] **Step 2: Correr y confirmar RED.**

- [ ] **Step 3: Implementar.** `QueueGauges` singleton, `ConcurrentDictionary` por cola. El worker abre un scope por vuelta. Fallo de una cola: `LoggerMessage` en `Warning` con la cola y el tipo de excepción, sin datos. `OperationCanceledException` del apagado no se loguea.

- [ ] **Step 4: Correr y confirmar GREEN**, incluido `CompositionRootTests`.

- [ ] **Step 5: Commit** `feat(observability): sample queues every 15 seconds`

### Task 5: Endpoint de telemetría del navegador

**Files:**
- Create: `src/Api/WebTelemetryEndpoints.cs`, `src/Api/WebTelemetryMetrics.cs`
- Modify: `src/Api/RateLimiterPolicies.cs`, `src/Api/Program.cs` (política antes de `Build`, `Map…` junto a los demás), `README.md`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/WebTelemetryApiTests.cs`, junto a `AuthorizationCatalogApiTests`

**Interfaces:**
- Produces:
  ```
  POST /api/v1/tenants/{tenantId:guid}/telemetry/web-vitals → 204
  { "samples": [ { "metric": "navigation"|"lcp"|"inp"|"ttfb"|"fcp"|"cls", "route": string?, "value": number } ] }
  ```
  `RateLimiterPolicies.Telemetry = "telemetry"`. Métricas, meter `"Qep.Web"`: `qep.web.navigation.duration` (s, etiqueta `route`, buckets `0.1, 0.25, 0.5, 0.75, 1, 1.5, 2, 3, 5, 10, 15`), `qep.web.vital` (s, etiqueta `name`, buckets `0.05, 0.1, 0.2, 0.3, 0.5, 1, 1.5, 2.5, 4, 6, 10`), `qep.web.cls` (buckets `0.01, 0.05, 0.1, 0.25, 0.5, 1`).

- [ ] **Step 1: Escribir las pruebas que fallan.**

- `ABatchIsAcceptedAndRecorded`: 204; el histograma de navegación recibe el valor con su `route`; `lcp` sale en `qep.web.vital` con `name=lcp` y sin `route`.
- `AnotherTenantIsForbidden`: 403.
- `WithoutSessionIsUnauthorized`: 401.
- `WithoutClientHeaderIsForbidden`: 403, con `WithRealAuthentication`; en stub el CSRF ni se registra.
- `AnInvalidBatchIs422WithErrors`: `Theory` con 51 muestras, `value = -1`, `value = 121`, `metric = "foo"`, `navigation` sin `route`, `route = "bad route"`; 422 `validation.failed` con `errors`.
- `TheRoute257IsOther`: 256 rutas distintas pasan tal cual; la 257 sale `other`; una ya vista sigue saliendo por su nombre.
- `TheTelemetryPolicyLimitsByIp`: 601 lotes seguidos desde una IP; el último responde 429.

- [ ] **Step 2: Correr y confirmar RED** con `--filter WebTelemetryApiTests`.

- [ ] **Step 3: Implementar.** Mismo patrón de `AuthorizationCatalogEndpoints.cs:31-57`: `.RequireAuthorization()` y comparación manual del claim `TenantId` con `RequestForbiddenException`. `.RequireRateLimiting(RateLimiterPolicies.Telemetry)`. Validador `IValidator<WebTelemetryRequest>` con FluentValidation; lanza `ValidationException`, que `ApiExceptionHandler` lleva a 422. Patrón de ruta `^/[A-Za-z0-9_$./-]{0,159}$`, `value` entre 0 y 120, hasta 50 muestras. Sin dispatcher ni base. Tope: `ConcurrentDictionary` de hasta 256 rutas, luego `other`. La política, ventana fija de 600 por minuto por `RemoteIpAddress`, con comentario: `UseRateLimiter` corre antes de `UseAuthentication` (`Program.cs:142`, `:151`), no hay usuario para particionar. El DTO lleva la nota BFF: por qué el navegador no manda la URL.

- [ ] **Step 4: Correr y confirmar GREEN.**

- [ ] **Step 5: Agregar a `README.md`** la sección de observabilidad: métricas nuevas, endpoint y dónde vive el dashboard.

- [ ] **Step 6: Commit** `feat(observability): accept web vitals from the SPA`

### Task 6: Cierre del backend

- [ ] **Step 1:** Suite completa una vez, comparando fallas por nombre contra `origin/develop` con script.
- [ ] **Step 2:** Revisar `git log origin/develop..HEAD --format='%h %s%n%b'`: sin `Co-Authored-By`.
- [ ] **Step 3:** Merge a `develop` y `docs/estado.md` en commit `docs:` inmediato. El push lo decide el owner.

---

## Parte B. Frontend (`qep-frontend`)

### Task 7: Búfer y envío

**Files:**
- Create: `src/features/telemetry/types/telemetry.types.ts`, `src/features/telemetry/services/telemetry.api.ts`, `src/features/telemetry/services/telemetry-buffer.ts`
- Test: `src/features/telemetry/services/telemetry-buffer.test.ts`

**Interfaces:**
- Produces:
  ```ts
  type TelemetrySample = { metric: 'navigation'|'lcp'|'inp'|'ttfb'|'fcp'|'cls'; route?: string; value: number }
  sendTelemetry(tenantId: string, samples: TelemetrySample[]): Promise<void>
  createTelemetryBuffer(deps: { send: (tenantId: string, samples: TelemetrySample[]) => Promise<void>; readTenant: () => string | null }): { add(sample: TelemetrySample): void; flush(): void; dispose(): void }
  ```

- [ ] **Step 1: Escribir las pruebas que fallan** (`vi.useFakeTimers`):

- `sends when it reaches 20 samples`
- `sends every 30 seconds when there is something to send`, y no llama a `send` con búfer vacío
- `flushes when the tab is hidden`
- `keeps samples while there is no tenant and sends them once there is one`
- `holds at most 50 samples and keeps the newest`
- `swallows a rejected send`: sin `unhandledrejection` y sin reintento

- [ ] **Step 2: Correr y confirmar RED** con `bun run test --run src/features/telemetry`.

- [ ] **Step 3: Implementar.** `sendTelemetry` usa `apiRequest` con `method: 'POST'`, `keepalive: true` y `/api/v1/tenants/${tenantId}/telemetry/web-vitals`. **No `sendBeacon`:** no lleva `X-Qep-Client`. Lotes de hasta 50.

- [ ] **Step 4: Correr y confirmar GREEN.**

- [ ] **Step 5: Commit** `feat(telemetry): buffer and send samples`

### Task 8: Tiempo de navegación

**Files:**
- Create: `src/features/telemetry/services/navigation-timer.ts`
- Test: `src/features/telemetry/services/navigation-timer.test.ts`

**Interfaces:**
- Consumes: `TelemetrySample`.
- Produces: `installNavigationTimer(deps: { router: AnyRouter; queryClient: QueryClient; onSample: (sample: TelemetrySample) => void }): () => void`.

- [ ] **Step 1: Escribir las pruebas que fallan**, con un router real de memoria (`createMemoryHistory`) y un `QueryClient`:

- `measures until the queries settle`: `value` incluye la consulta de la pantalla
- `reports the route template, not the url`: `/_authenticated/quotes/$quoteId`, sin el id
- `ignores a search-only change`
- `discards a navigation interrupted by another one`: una sola muestra, la última
- `reports one sample with the final route after a redirect`
- `discards when the tab is hidden`
- `discards after 15 seconds of never settling`: sin muestra
- `ignores the initial load`

- [ ] **Step 2: Correr y confirmar RED.**

- [ ] **Step 3: Implementar.** `router.subscribe('onBeforeNavigate', …)` guarda `performance.now()` si `pathChanged` y no es la primera carga. `router.subscribe('onResolved', …)` espera `queryClient.isFetching() === 0` dos `requestAnimationFrame` seguidos. La ruta sale de `router.state.matches.at(-1)?.routeId`. Confirma en la prueba los nombres de campo del evento con la versión instalada (`^1.170.32`).

- [ ] **Step 4: Correr y confirmar GREEN.**

- [ ] **Step 5: Commit** `feat(telemetry): time navigations until the screen settles`

### Task 9: Web Vitals e instalación

**Files:**
- Create: `src/features/telemetry/services/web-vitals.ts`, `src/features/telemetry/install-telemetry.ts`
- Modify: `src/main.tsx`, `package.json`, `bun.lock`
- Test: `src/features/telemetry/services/web-vitals.test.ts`

**Interfaces:**
- Consumes: `createTelemetryBuffer`, `installNavigationTimer`, `sendTelemetry`, `readActiveTenant`.
- Produces: `installTelemetry(router, queryClient): void`, `installWebVitals(onSample: (sample: TelemetrySample) => void): void`.

- [ ] **Step 1:** `bun add web-vitals`.
- [ ] **Step 2: Escribir la prueba que falla.** `vi.mock('web-vitals')`. `maps vitals to seconds`: `onLCP` con `1200` da `{ metric: 'lcp', value: 1.2 }`; `onCLS` con `0.1` da `{ metric: 'cls', value: 0.1 }`, sin dividir. Ninguna muestra lleva `route`.
- [ ] **Step 3: Correr y confirmar RED.**
- [ ] **Step 4: Implementar.** `web-vitals` da milisegundos; se convierte a segundos salvo CLS. En `main.tsx`, `installTelemetry(router, queryClient)` después de crear ambos, una vez, fuera de `StrictMode`. Sólo con `import.meta.env.PROD`: en local no hay a quién mandarle.
- [ ] **Step 5: Correr y confirmar GREEN**, más `bun run build` y `bun run lint`.
- [ ] **Step 6:** Suite una vez, comparando fallas por nombre contra `origin/develop`.
- [ ] **Step 7: Commit** `feat(telemetry): report web vitals in production`

---

## Parte C. Plataforma (`migracion-k8s`)

Repo local, sin remoto. Sus convenciones: comandos desde WSL, código en inglés, docs en español. Tiene una rama activa con `docs/28…` sin commitear: **no se toca**. Trabajar en un `git worktree` nuevo, `~/migracion-k8s-worktrees/qep-observabilidad`, desde la rama estable que muestre `git branch`, confirmándola con el owner si no es evidente.

### Task 10: Dashboard `QEP Backend`

**Files:**
- Create: `k8s/platform/dashboards/qep-backend-dashboard.yaml`
- Modify: `docs/07-monitoreo-dia-1.md`

- [ ] **Step 1:** Partir de `transportes-ci-backend-dashboard.yaml`, sección OTel: ConfigMap en `monitoring`, `grafana_dashboard: "1"`, nombre `grafana-qep-backend-dashboard`.
- [ ] **Step 2:** Las seis filas del spec. Filtro `exported_job="qep/qep-backend"`. Prometheus guarda `le` como `"0.5"`, `"1.0"`, `"2.0"`. Los gauges con `max by (queue)`. Enlace de datos del top 10 a Tempo por `span.http.route`. Los paneles de fases sin desplegar quedan con «Sin datos».
- [ ] **Step 3: Validar.** `kubectl --context contabo-prod apply --dry-run=server -f …` sin error, y cada consulta sin rutas de otros jobs, corrida contra Prometheus.
- [ ] **Step 4: Pedir OK del owner** y aplicar. Verificar en Grafana que carga. Es lo único que se publica antes del despliegue del backend.
- [ ] **Step 5: Commit** `feat(dashboards): add qep backend dashboard`

### Task 11: Alertas

Sólo después de desplegar el backend.

**Files:**
- Create: `k8s/platform/prometheusrules/qep-backend-prometheusrule.yaml`
- Modify: `k8s/platform/dashboards/qep-backend-dashboard.yaml` si los nombres reales difieren, `docs/07-monitoreo-dia-1.md`

- [ ] **Step 1:** Confirmar en Prometheus los nombres reales de `qep_queue_*`, `qep_messaging_inbound_lag_*` y `qep_web_*`, y que llegan con `exported_job="qep/qep-backend"`.
- [ ] **Step 2:** Las seis alertas del spec, con esos umbrales y severidades. Label `release: kube-prometheus-stack`.
- [ ] **Step 3: Validar.** `--dry-run=server`, y cada `expr` evaluada contra Prometheus.
- [ ] **Step 4: Pedir OK del owner y aplicar.** Verificar que `kubectl get prometheusrule` la lista y que aparece en `/api/v1/rules`. Prueba real de punta a punta: bajar a 1 s el umbral de una alerta `warning` en una copia temporal y ver que llega a Telegram; retirarla.
- [ ] **Step 5: Commit** `feat(alerts): add qep backend alerts`

# Monitoreo de webhooks de Meta — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Contar los webhooks de Meta que llegan y se descartan, y alertar por rechazo, silencio y descarte sospechoso.

**Architecture:** Un contador OTel `qep.webhook.discarded{provider,reason}` en `MessagingMetrics`, sumado en cada punto donde
`WebhookDeliveryProcessor` ya loguea un descarte. Las alertas y el panel viven en `migracion-k8s` y leen la métrica y las
métricas HTTP que ya existen.

**Tech Stack:** .NET 10, `System.Diagnostics.Metrics`, OTel → Prometheus; PrometheusRule y dashboard de Grafana en YAML.

**Spec:** `docs/superpowers/specs/2026-11-16-monitoreo-webhooks-design.md`

## Global Constraints

- Shopify fuera de alcance: no se toca `Modules.Shopify.*` ni la ruta `/api/webhooks/shopify`.
- Etiquetas sólo `provider` (`whatsapp` | `instagram`) y `reason`; nunca tenant, `phone_number_id`, cuenta ni datos de la persona.
- Los valores de `reason` son contrato con las alertas: `snake_case`, conjunto cerrado, fijado por prueba.
- El texto de los logs existentes no cambia.
- Commits conventional, sin atribución de IA. Trabajo en el worktree `qep-backend-worktrees/monitoreo-webhooks`
  (rama `feature/monitoreo-webhooks`); en `migracion-k8s`, worktree propio desde `main` (el checkout está en otra rama).
- Pruebas: las corre el owner al final. La evidencia de RED y GREEN es el `dotnet build` (RED = no compila por el símbolo
  que falta). No dejar `Api.exe` corriendo.
- `migracion-k8s`: código y comentarios en inglés, docs en español, git desde WSL.

## Review Focus

1. Un `change` de mensajes con ruta desconocida y `Messages.Count == 0` (sólo estados): no debe sumar 0 ni crear la serie
   en vano — se suma sólo con `count > 0`.
2. Un mismo delivery reintentado (`RetryLater`) vuelve a pasar por el descarte: se cuenta otra vez. Aceptado (el log ya hace
   lo mismo); la alerta compara con la semana anterior, no cuenta exacto.
3. `paused`/`module_off` nunca deben entrar en la alerta: la expresión filtra por `reason` explícito, no por exclusión.
4. Una ruta sin tráfico la semana anterior (Instagram sin conexiones) no alerta por silencio: lo garantiza el `offset 7d > 10`.
5. Pods múltiples: los contadores se suman con `sum by`, nunca `max by` (a diferencia de los gauges de cola).

---

### Task 1: Contador y motivos

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookDiscardReasons.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/MessagingMetrics.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/Webhook/WebhookDiscardReasonsTests.cs`

**Interfaces:**
- Produces:
  - `internal static class WebhookDiscardReasons` con `const string`: `UnknownRoute = "unknown_route"`,
    `WithoutUserId = "without_user_id"`, `Paused = "paused"`, `ModuleOff = "module_off"`,
    `ForeignStatus = "foreign_status"`, `UnhandledField = "unhandled_field"`, `ForeignEvent = "foreign_event"`,
    `Ignored = "ignored"`; y `IReadOnlyList<string> All` con los ocho.
  - `internal static class WebhookDiscardProviders` con `WhatsApp = "whatsapp"`, `Instagram = "instagram"`.
  - `MessagingMetrics.RecordDiscarded(string provider, string reason, int count)`: no hace nada con `count <= 0`.
    Instrumento `Counter<long>` `"qep.webhook.discarded"`, unidad `"{event}"`, descripción
    `"Webhook events from Meta that were received and deliberately not ingested."`, tags `provider` y `reason`.

- [ ] **Step 1: Write the failing test** `WebhookDiscardReasonsTests.TheReasonsAreTheContractTheAlertsRead`:
  `Assert.Equal(["unknown_route", "without_user_id", "paused", "module_off", "foreign_status", "unhandled_field", "foreign_event", "ignored"], WebhookDiscardReasons.All)`.
  Comentario de la prueba: los lee por nombre `QepWebhookDiscarding` en `migracion-k8s`.
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Messaging/Modules.Messaging.UnitTests` → `CS0103`/`CS0246` por
  `WebhookDiscardReasons`. Guardar la salida literal.
- [ ] **Step 3: Implement** las dos clases y `RecordDiscarded` (crear el contador en el constructor, como `_inboundLag`).
- [ ] **Step 4: GREEN** — mismo `dotnet build`, 0 errores.
- [ ] **Step 5: Commit** `feat(messaging): add webhook discard counter`.

### Task 2: Sumar en cada descarte

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookDeliveryProcessor.cs` (líneas 109, 144,
  150, 158, 229, 286, 339, 384, 474 al 2026-11-16)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookDeliveryProcessor.Instagram.cs` (37, 43, 53, 87, 103)
- Modify: `CLAUDE.md` (Gotchas) y `README.md` (sección de observabilidad, donde se describen las métricas `qep_*`)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/WebhookDiscardMetricTests.cs`

**Interfaces:**
- Consumes: `MessagingMetrics.RecordDiscarded`, `WebhookDiscardReasons`, `WebhookDiscardProviders` (Task 1).

Mapeo, con el mismo `count` que ya va al log:

| Llamada existente | `provider` | `reason` | `count` |
| --- | --- | --- | --- |
| `LogDiscarded(..., "unknown-route")` (×4: mensajes, historial, ecos, contactos) | whatsapp | `UnknownRoute` | el del log |
| `LogDiscarded(..., paused ? "paused" : "module-off")` | whatsapp | `Paused` / `ModuleOff` | el del log |
| `LogSkippedWithoutUserId` | whatsapp | `WithoutUserId` | `SkippedWithoutUserId` |
| `LogForeignStatus` | whatsapp | `ForeignStatus` | 1 |
| `LogIgnoredField` (×2) | whatsapp | `UnhandledField` | 1 |
| `LogInstagramDiscarded(..., "unknown-route")` | instagram | `UnknownRoute` | el del log |
| `LogInstagramDiscarded(..., paused/module-off)` | instagram | `Paused` / `ModuleOff` | el del log |
| `LogInstagramForeign` (×2) | instagram | `ForeignEvent` | 1 |
| `LogInstagramIgnored` | instagram | `Ignored` | `ignored.Length` |

Las pruebas siguen el patrón de `InboundLagMetricTests` (listener filtrado por `Meter.Scope` del host; recolecta
`(value, provider, reason)` de `qep.webhook.discarded`). Cada una levanta su host (escucha métricas del host).

- [ ] **Step 1: Write the failing tests** en `WebhookDiscardMetricTests`:
  - `AnInboundForAnUnknownNumberCountsAsUnknownRoute`: tenant con Messaging y conexión `"111"`; postear
    `MetaPayloads.Inbound` al `phone_number_id` `"999"`; drenar. Esperado: una medición `(1, "whatsapp", "unknown_route")`.
  - `AnInboundWithoutUserIdCountsAsWithoutUserId`: a `"111"`, mensaje sin `from_user_id` (ver cómo arma
    `MetaPayloads.Inbound` el campo; si hace falta, un parámetro opcional que lo omita). Esperado: contiene
    `(1, "whatsapp", "without_user_id")`.
  - `AStatusOfAnUnknownMessageCountsAsForeignStatus`: `MetaPayloads.Status` a `"111"` con un wamid que no existe y sin
    callback de QEP. Esperado: `(1, "whatsapp", "foreign_status")`.
  - `AnIngestedInboundCountsNothing`: inbound válido a `"111"`. Esperado: ninguna medición.
  - `AnInstagramEventForAnUnknownAccountCountsAsUnknownRoute`: sin conexión de Instagram, `InstagramPayloads.Text`.
    Esperado: `(1, "instagram", "unknown_route")`.
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Messaging/Modules.Messaging.IntegrationTests`. Si compila (las pruebas
  sólo usan literales), el RED lo da la corrida que hace el owner; anotar que compila y que el RED queda pendiente de él.
- [ ] **Step 3: Implement** — una llamada `metrics.RecordDiscarded(...)` junto a cada log de la tabla. `WebhookDeliveryProcessor`
  ya recibe `MessagingMetrics`; el parcial de Instagram usa el mismo campo.
- [ ] **Step 4: Docs** — en `CLAUDE.md` (Gotchas), una entrada: los `reason` de `qep_webhook_discarded_total` los leen por
  nombre las alertas de `migracion-k8s`; un descarte nuevo de Meta suma su motivo en `WebhookDiscardReasons` y en la prueba,
  y decide si alerta. En `README.md`, la métrica junto a las demás `qep_*`.
- [ ] **Step 5: GREEN** — `dotnet build` de la solución, 0 errores, 0 warnings (`TreatWarningsAsErrors`).
- [ ] **Step 6: Commit** `feat(messaging): count discarded meta webhooks`.

### Task 3: Alertas y panel (`migracion-k8s`)

**Files (en el worktree de `migracion-k8s`, rama `feat/qep-webhook-alerts` desde `main`):**
- Modify: `k8s/platform/prometheusrules/qep-backend-prometheusrule.yaml`
- Modify: `k8s/platform/dashboards/qep-backend-dashboard.yaml` (fila «4. Webhooks», cerca de la línea 1286)
- Create: `docs/<siguiente número>-qep-webhook-alerts.md` (español; apunta al spec de `qep-backend`)

**Interfaces:**
- Consumes: `qep_webhook_discarded_total{exported_job="qep/qep-backend",provider,reason}` (Task 1) y
  `http_server_request_duration_seconds_count{exported_job="qep/qep-backend",http_route,http_response_status_code}` (existe).

Reglas, en el grupo existente, con `severity: warning` y `annotations` en inglés como las vecinas:

```yaml
- alert: QepWebhookSignatureRejected
  expr: |
    sum by (http_route) (rate(http_server_request_duration_seconds_count{exported_job="qep/qep-backend",http_route=~"/api/webhooks/(whatsapp|instagram)",http_response_status_code="401"}[5m])) > 0
  for: 10m
# Without the `or 0 * ...offset 7d` a route with no sample this hour has no series and `== 0` never fires.
- alert: QepWebhookSilent
  expr: |
    (
      sum by (http_route) (increase(http_server_request_duration_seconds_count{exported_job="qep/qep-backend",http_route=~"/api/webhooks/(whatsapp|instagram)",http_response_status_code=~"2.."}[1h]))
      or
      0 * sum by (http_route) (increase(http_server_request_duration_seconds_count{exported_job="qep/qep-backend",http_route=~"/api/webhooks/(whatsapp|instagram)",http_response_status_code=~"2.."}[1h] offset 7d))
    ) == 0
    and on (http_route)
    sum by (http_route) (increase(http_server_request_duration_seconds_count{exported_job="qep/qep-backend",http_route=~"/api/webhooks/(whatsapp|instagram)",http_response_status_code=~"2.."}[1h] offset 7d)) > 10
  for: 5m
# Compared with the same hour a week earlier so chronic noise (a number without connection) does not fire forever.
- alert: QepWebhookDiscarding
  expr: |
    sum by (provider, reason) (increase(qep_webhook_discarded_total{exported_job="qep/qep-backend",reason=~"unknown_route|without_user_id"}[1h]))
    > on (provider, reason)
    (
      3 * sum by (provider, reason) (increase(qep_webhook_discarded_total{exported_job="qep/qep-backend",reason=~"unknown_route|without_user_id"}[1h] offset 7d))
      or
      0 * sum by (provider, reason) (increase(qep_webhook_discarded_total{exported_job="qep/qep-backend",reason=~"unknown_route|without_user_id"}[1h]))
    ) + 5
  for: 15m
```

Panel nuevo «Descartados por motivo»: `sum by (provider, reason) (rate(qep_webhook_discarded_total{exported_job="qep/qep-backend"}[$__rate_interval]))`,
timeseries, copiando la forma del panel «Estados» vecino (id nuevo, `gridPos` debajo).

- [ ] **Step 1:** crear el worktree desde `main` en WSL (`git worktree add ../migracion-k8s-worktrees/qep-webhook-alerts -b feat/qep-webhook-alerts main`).
- [ ] **Step 2:** agregar las tres reglas y el panel.
- [ ] **Step 3: Verificar** — `promtool check rules` sobre el `spec` extraído si `promtool` está; si no, cargar los dos YAML
  con `python3 -c "import yaml,sys; list(yaml.safe_load_all(open(sys.argv[1])))"` y validar el JSON del dashboard embebido
  con `python3 -m json.tool`. Probar cada `expr` en Grafana → Explore contra prod (sólo lectura) y anotar el resultado: la
  de silencio debe devolver vacío hoy; la de descartes, vacío hasta desplegar el backend.
- [ ] **Step 4:** doc corta en `docs/` (qué mide cada alerta, umbrales iniciales, que se ajustan con una semana de datos,
  y que Shopify queda pendiente).
- [ ] **Step 5: Commit** `feat(alerts): add qep meta webhook alerts`. No aplicar al clúster ni empujar sin el owner.

## Orden de despliegue

Backend (Tasks 1–2) a `develop` y luego a producción por el owner; después Task 3. Antes del despliegue del backend, las
reglas sobre `qep_webhook_discarded_total` dan vacío y no alertan, así que el orden inverso tampoco rompe nada.

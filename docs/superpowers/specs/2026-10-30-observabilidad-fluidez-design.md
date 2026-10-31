# Observabilidad: que QEP se sienta rápido y aguante los webhooks — diseño

Fecha: 2026-10-30. Estado: borrador para aprobación del owner. Datos de producción medidos ese mismo día contra
`contabo-prod`, salvo donde se dice lo contrario.

## Por qué

Van a empezar a entrar los webhooks de Meta en volumen, y el owner quiere dos cosas: que un pico no ponga lento todo, y
que **el asesor sienta la aplicación rápida y fluida al navegar**. Esa segunda es la promesa que manda; las métricas de
capacidad sirven para explicar por qué se rompió.

Lo que existe hoy:

- **La telemetría ya llega.** `qep-backend` manda trazas a Tempo y métricas a Prometheus
  (`exported_job="qep/qep-backend"`), con `http_route` y buckets de 5 ms a 10 s. Retención de Prometheus: **7 días**.
- **Nadie la mira.** Otros proyectos tienen dashboard de Grafana; QEP no. Ninguna alerta es de QEP.
- **El `Meter` no se usa.** `QepObservability.Meter` y `ActivitySource` están declarados y registrados, con cero usos.
  No hay `CreateHistogram`, `CreateCounter` ni `CreateObservable*` en todo `src/`.
- **El navegador no mide nada.** Sin `web-vitals`, Sentry ni analítica en `qep-frontend`.

### Línea base (7 días, `http_route=~"/api/v1/.+"`, ~20 mil requests)

| Umbral | Lecturas (GET) | Escrituras |
| --- | --- | --- |
| ≤ 250 ms | 55 % | 15 % |
| ≤ 500 ms | 82 % | — |
| ≤ 1 s | 95 % | 53 % |
| ≤ 2,5 s | — | 85 % |

Son unas 0,03 req/s, con CPU pico de 0,2 sobre 1 y 3 conexiones en uso de 100. **La lentitud no viene de la carga: ya es
lento casi sin tráfico.** Peores p95 en 24 h: `POST …/quotations/{id}/pdf` (6,5 s) y `POST …/quotations/preview` (6,4 s,
con 492 llamadas al día). El análisis de plataforma del 2026-10-29 (`migracion-k8s/docs/28-analisis-rendimiento-2026-10-29.md`,
sin commitear) además encontró 67 seq scans por minuto en `outbox_messages` por el polling.

### Por qué el top 10 HTTP no alcanza

`ReceiveWebhookHandler` valida la firma, calcula un SHA e inserta en `messaging.webhook_deliveries`
(`Persistence/WebhookDeliveries.cs:17-30`). Es decir, ese endpoint **seguirá rápido y en verde aunque los mensajes se
acumulen detrás**. El camino real es:

```
webhook → messaging.webhook_deliveries → WebhookDeliveryProcessor
        → messaging.messages + message_media + automation_inbox (una transacción)
```

Y el pod es uno solo, con límite de 1 CPU compartido entre la API y los workers: un atraso de los workers le pega a la
navegación del asesor. Por eso hacen falta las dos vistas.

## Metas, no objetivos

**Metas.** Saber en una sola pantalla: si la promesa se cumple, cuántos req/s hay, qué rutas concentran el tráfico, cuáles
10 son las más lentas y cuántas veces se llaman, si vamos atrasados con los webhooks y qué recurso se está saturando.
Y que Telegram avise antes de que lo haga un cliente.

**No objetivos.** Optimizar `quotations/preview` o cualquier ruta (sale de estos datos, es trabajo aparte). Pasar a dos
réplicas (`2026-10-29-replicas-multiples-design.md`). Un proveedor de APM. Propagar la traza del webhook al worker: hoy
no sobrevive (las tablas de cola no tienen columna de traza) y queda para después. Latencia punta a punta del bot: la
cubre la edad de `automation_inbox`. Rezago por consumidor del outbox: su pendiente es un anti-join sin índice, justo lo
que `docs/28` señala como caro. Pruebas de carga.

**Anti-objetivos.** Telemetría que empeore lo que mide: en el navegador jamás bloquea, jamás muestra error y jamás
cierra sesión; en la base no agrega carga visible. Alertas que suenen y nadie atienda. Datos personales o de tenant en
etiquetas de métricas.

## Metas de servicio (propuesta; las ajusta el owner)

Ventana de 7 días, la retención real de Prometheus. Una ventana de 28 días no se puede calcular.

| Promesa | Meta | Hoy |
| --- | --- | --- |
| Cambio de pantalla listo | 95 % ≤ 1 s | sin dato |
| Lectura `/api/v1` | 95 % ≤ 500 ms | 82 % |
| Escritura `/api/v1` | 95 % ≤ 1 s | 53 % |
| Enviar mensaje | 95 % ≤ 2 s | sin tráfico en 7 días |
| Mensaje entrante guardado | 95 % ≤ 5 s desde la hora de Meta | sin dato |

**Enviar mensaje** (owner, 2026-10-30) son los `POST …/conversations/{id}/messages*`. Esperan a Meta dentro del request, así
que en la meta de escrituras la dejarían en rojo por una latencia que QEP no controla; sacarlos sin más escondería la acción
que el asesor más repite. Por eso tienen meta y panel propios. `http.server.request.duration` salta de 1 a 2,5 s, así que la
fase 2 agrega el bucket de 2 s con una vista en `QepObservability`.

Las escrituras excluyen esas rutas, `…/pdf`, `…/send` y `…/files/{id}/complete`, que dependen de un tercero y tienen sus
propios paneles. `quotations/preview` **no** se excluye: es de lo que el asesor más sufre. Los umbrales coinciden con buckets
reales (`le="0.5"`, `le="1.0"`), para que el porcentaje sea exacto y no interpolado. Todas las metas y los tops excluyen
`/api/v1/hubs/*` (el GET de SignalR dura horas y se contaría como lectura lenta) y la ruta de telemetría de la fase 3.

## Qué se construye

### Fase 1. Dashboard `QEP Backend`, sin tocar código

Vive en `migracion-k8s/k8s/platform/dashboards/qep-backend-dashboard.yaml`: un ConfigMap en `monitoring` con
`grafana_dashboard: "1"`, como los hermanos. Es otro repo (se trabaja desde WSL, código en inglés, docs en español) y
cruza namespaces, por eso no va en `qep-backend`. Plantilla: `transportes-ci-backend-dashboard.yaml`, sección OTel.

Filtro base de todas las consultas: `exported_job="qep/qep-backend"`. Filas:

1. **Promesas.** Los cinco porcentajes de arriba con su meta, 7 días. El navegador, el entrante y el bucket de 2 s de
   «Enviar mensaje» aparecen vacíos hasta las fases 2 y 3.
2. **Tráfico.** Req/s totales y por ruta (top 10), estados HTTP, 5xx. Sin `/health/*`, sin `OPTIONS`, sin hubs y sin la ruta
   de telemetría (`http_route=~"/api/.+"`, `http_route!~".*/(hubs/.*|telemetry/.*)"`, `http_request_method!="OPTIONS"`).
3. **Lentitud.** Tabla top 10 por p95 con p50, p95, p99, req/s y tiempo total acumulado (volumen × promedio), que es lo
   que ordena qué conviene mejorar primero. Cada fila enlaza a Tempo con esa ruta (TraceQL sobre `span.http.route`), que
   ya desglosa consultas y llamadas externas.
4. **Webhooks.** Req/s, estados (incluye 401 de firma), p95 y `aspnetcore_rate_limiting_requests_total` de la política
   `webhook` por resultado. Fase 2: colas y rezago del entrante.
5. **Saturación.** CPU contra límite, throttling, memoria contra límite, cola del thread pool
   (`dotnet_thread_pool_queue_length_total`), pool de base (`db_client_connection_count` contra `_max`),
   `kestrel_queued_connections`, pausas de GC.
6. **Dependencias.** `http_client_request_duration_seconds` por `server_address`, top 10. Meta, R2, qcode-pdf y Zenvia
   tienen un host fijo; Shopify uno por tienda y el bloque HTTP uno por tenant, por eso el top y no una lista.

Da valor desde el primer día y no depende del despliegue del backend.

### Fase 2. Métricas de colas y del entrante (backend)

**Meters.** Cada módulo declara el suyo con `System.Diagnostics.Metrics`, sin referenciar `BuildingBlocks.Observability`,
que arrastra OpenTelemetry. `QepObservability` pasa de `AddMeter(MeterName)` a `AddMeter("Qep.*")` y deja una prueba que lo
amarra. Los histogramas declaran sus buckets con `InstrumentAdvice`, porque los de defecto están pensados para
milisegundos.

**Bucket de 2 s.** Vista sobre `http.server.request.duration` en `AddQepObservability`: los límites de hoy más `2`, amarrada
por una prueba que falla si la lista cambia. El collector y Prometheus aceptan el `le` nuevo sin migración; los datos viejos no
lo tienen, y el panel se llena desde el despliegue.

**Muestreador de colas.** Un `BackgroundService` en `src/Bootstrapper/Observability/`, porque cruza módulos, con SQL crudo
por nombre (precedente: `CustomerMergeReferences`). Cada 15 s lee y publica tres gauges con la etiqueta `queue`:

- `qep.queue.backlog`: filas pendientes.
- `qep.queue.retrying`: pendientes que ya fallaron al menos una vez.
- `qep.queue.oldest_age` (s): edad de la pendiente más vieja **que nadie ha intentado todavía** (`attempts = 0`).

Ese filtro de la edad es a propósito. Una fila sana en reintento largo (un status que llega antes que su `wamid` dura
unos 34 minutos sin procesarse) o un outbox envenenado (sin tope de intentos) dispararía la alerta sin que haya atraso.
El atraso real es que lo nuevo no se toca, y eso sí sube la edad aunque el worker se quede colgado.

En `automation` la edad además salta las filas frenadas por una anterior pendiente de su misma conversación
(`AutomationInbox.ClaimAsync` las bloquea con un `NOT EXISTS`). Si no, una sola conversación en reintento, con leases de hasta
10 minutos, dispararía un critical con el worker sano. Ese cliente trabado se ve en `retrying`, no en la alerta. El muestreador
repite ese anti-join sobre `IX_automation_inbox_conversation`.

| `queue` | Tabla | Pendiente | Edad desde |
| --- | --- | --- | --- |
| `webhook` | `messaging.webhook_deliveries` | `processed_at IS NULL` | `received_at` |
| `automation` | `messaging.automation_inbox` | `processed_at IS NULL` | `created_at` |
| `media` | `messaging.message_media` | `stored_at IS NULL AND next_attempt_at < '9999-01-01'` | `messaging.messages.created_at` por `message_id`, sólo con `imported = false` |
| `campaign` | `messaging.campaign_recipients` + `campaigns` | `status IN ('Pending','Sending')`, campaña `Sending` y `audience_frozen_at IS NOT NULL` | sin columna: no publica edad |
| `outbox` | `platform.outbox_messages` | `processed_at IS NULL` | `occurred_at` |
| `shopify_webhook` | `shopify.webhook_deliveries` | `processed_at IS NULL` | `received_at` |

Cubierto por índices parciales salvo el join de `campaign`, que corre sólo sobre destinatarios pendientes. Las filas de
`media` abandonadas quedan con `next_attempt_at = 9999-01-01`, de ahí el filtro. Cada consulta lleva tope de 5 s.

**Historial de coexistencia.** Sus medios se cuelgan de mensajes ya creados, y por eso nacerían viejos: `imported` los saca
de la edad de `media`, no del `backlog`. En `webhook` no hay excepción: `WebhookDeliveryWorker` procesa en secuencia, así que una
parte grande de historial sí retiene lo nuevo, y esa alerta es cierta. Antes de una importación planeada se avisa, o se silencia
`QepQueueLagging` en Alertmanager durante esa ventana.

Reglas del muestreador:

- Un fallo de una cola se loguea con `Warning` y **no** tumba las demás ni al worker.
- Si un valor tiene más de 60 s, el gauge deja de publicar esa cola: un número viejo y tranquilo es peor que ningún número.
- Sin etiqueta de tenant: es operación, no negocio.
- Con varias réplicas cada pod publica lo mismo, así que las consultas usan `max by (queue)`, nunca `sum`.

**Rezago del entrante.** Histograma `qep.messaging.inbound.lag` (s), etiqueta `channel`, con la hora de guardado menos
`message.OccurredAt`. Va en la rama `ingested is not null` de `WebhookDeliveryProcessor.cs:171-176` y en el camino de
Instagram, después del commit, así un reintento de Meta no cuenta doble. Negativos a 0 (Meta da segundos enteros). Buckets:
1, 2, 5, 10, 30, 60, 300, 3600. Un reintento de Meta horas después cae en la cola larga, y debe verse: el cliente esperó.

### Fase 3. Fluidez en el navegador (frontend + backend)

**Backend.** `POST /api/v1/tenants/{tenantId:guid}/telemetry/web-vitals`, en `src/Api/`, como `AuthorizationCatalogEndpoints`:
`.RequireAuthorization()` y la comparación manual del claim `TenantId` contra la ruta. Responde 204 sin tocar la base, sin
auditoría ni outbox: sólo registra métricas.

```json
{ "samples": [
  { "metric": "navigation", "route": "/_authenticated/quotes/$quoteId", "value": 0.84 },
  { "metric": "lcp", "value": 1.2 }
] }
```

- `metric` es enum cerrado: `navigation`, `lcp`, `inp`, `ttfb`, `fcp`, `cls`. Valores en segundos; `cls` sin unidad.
- Validador de FluentValidation: hasta 50 muestras, `value` entre 0 y 120, `route` obligatoria sólo en `navigation`, con
  patrón `^/[A-Za-z0-9_$./-]{0,159}$`.
- **Cardinalidad.** Cualquier usuario autenticado puede mandar rutas inventadas. Se admiten hasta 256 `route` distintas por
  proceso; las siguientes se publican como `other`. Hay 117 rutas reales.
- **Límite.** `UseRateLimiter` corre **antes** de `UseAuthentication` (`Program.cs:142` y `:151`), así que no hay usuario para
  particionar. Política nueva `Telemetry`, 600 por minuto por IP: un cliente manda unos 2 lotes por minuto y un NAT agrupa
  a varios asesores. La de `Public` (120) le quitaría cupo al resto.

Publica `qep.web.navigation.duration` (s, etiqueta `route`), `qep.web.vital` (s, etiqueta `name`: `lcp`, `inp`, `ttfb`,
`fcp`) y `qep.web.cls`. **Sólo la navegación lleva ruta:** `web-vitals` reporta LCP una vez y el INP de toda la vida de la
página, así que en una SPA pegarles la ruta sería mentir. Peor caso de series: 117 rutas × 11 buckets, más unas 60.

**Frontend.** `src/features/telemetry/`, consumido sólo desde el arranque en `src/main.tsx`, donde está el router.

- **Duración de navegación.** Empieza en `onBeforeNavigate` si cambió el `pathname` (cambiar filtros o página de una tabla
  no cuenta). Termina tras `onResolved`, cuando `queryClient.isFetching() === 0` dos frames seguidos. Se descarta si
  entra otra navegación, si la pestaña se oculta o a los 15 s. La ruta es el `routeId` de la hoja (`matches.at(-1).routeId`),
  **nunca** la URL, que lleva ids. La carga inicial no cuenta: la mide LCP.
- **Vitals.** `onLCP`, `onINP`, `onTTFB`, `onFCP`, `onCLS` de `web-vitals`.
- **Envío.** Búfer; se manda a las 20 muestras, cada 30 s y al ocultarse la pestaña, por `apiRequest` con `keepalive: true`.
  **No `sendBeacon`:** no puede poner `X-Qep-Client` y el CSRF lo rechazaría. Requiere tenant activo (`readActiveTenant()`);
  sin él el búfer espera y se vacía al loguearse, con tope de 50 muestras.
- **Silencio total.** Todo error se traga. Va por fuera de React Query, así que un 401 o un 429 no dispara el cierre de sesión
  (`query-client.ts:36-44`).
- Muestreo 100 %, con este volumen no hace falta reducir.

### Fase 4. Alertas

`migracion-k8s/k8s/platform/prometheusrules/qep-backend-prometheusrule.yaml`, con `release: kube-prometheus-stack`. Van al
receptor `telegram` existente, que agrupa por `alertname` y `namespace` y repite cada 4 h.

| Alerta | Condición | Severidad |
| --- | --- | --- |
| `QepQueueLagging` | `oldest_age` de `webhook` o `automation` > 60 s por 5 min | critical |
| `QepMediaQueueLagging` | `oldest_age` de `media` > 15 min por 10 min | warning |
| `QepQueueMetricsMissing` | sin `qep_queue_backlog{queue="webhook"}` por 5 min | warning |
| `QepHigh5xx` | 5xx > 2 % por 10 min, con al menos 0,2 req/s | critical |
| `QepCpuSaturated` | CPU > 85 % del límite por 10 min | warning |
| `QepDbPoolSaturated` | conexiones en uso > 80 % del máximo por 5 min | warning |

Las metas de navegación **no** alertan al principio: con este volumen un rato malo de una persona movería el porcentaje.
Se revisa en el dashboard hasta tener tráfico que lo sostenga.

## Orden y despliegue

1. Fase 1: dashboard. Requiere el OK del owner para `kubectl apply` en `contabo-prod`.
2. Fase 2: rama `feature/observabilidad` de `qep-backend`, mergeada a `develop`.
3. Fase 3: backend primero; el frontend tolera el endpoint ausente porque se traga el error.
4. Fase 4, cuando las métricas existan en prod. Tras el primer despliegue se verifican los nombres reales en Prometheus
   (el collector traduce `qep.queue.oldest_age` a algo como `qep_queue_oldest_age_seconds`) y se ajustan dashboard y reglas.

Dos slices, uno por repo. Lo de `migracion-k8s` va en rama propia; ese checkout está hoy en otra rama con cambios sin
commitear, y no se tocan.

## Pruebas

TDD, RED antes que GREEN.

- **Muestreador:** contra el esquema migrado, sembrando cada caso del filtro (fila en reintento, media abandonada, campaña sin
  congelar). Es SQL por nombre: renombrar una columna compila y deja la cola sin dato, así que esta prueba es la que lo
  amarra, y va como gotcha en `CLAUDE.md`. Valor vencido deja de publicar. Fallo de una cola no afecta a las otras.
- **Rezago:** con `MeterListener`. Un reintento de Meta no registra. Timestamp futuro da 0.
- **Endpoint:** tenant ajeno 403, sin sesión 401, sin `X-Qep-Client` 403 con el stub apagado, lote inválido 422 con `errors`,
  ruta 257 sale como `other`, y `CompositionRootTests` o similar amarra la política `Telemetry`.
- **Frontend (Vitest):** navegación medida, cortada por otra, pestaña oculta, cambio sólo de query, el `routeId` y no la URL, error de
  red sin efecto visible, y 401 sin cerrar sesión.

## Decisiones del asistente (el owner puede cambiarlas)

Metas y exclusiones de escrituras, intervalo de 15 s, la edad sólo con `attempts = 0` y sin las filas frenadas de `automation`,
sacar los medios importados de la edad, dejar que una importación grande alerte en `webhook`, las seis colas, el tope de 256 rutas,
el enfoque sin proveedor (Prometheus en vez de Vercel Analytics o Sentry), el histograma sin ruta para vitals, los umbrales de
alerta y dejar el bot punta a punta para después.

## Queda para quien construya

Forma exacta del `BackgroundService` y de su conexión, nombres de clases, disposición visual del dashboard y texto de las
alertas. Confirmar en el plan cuál mapeo de `MessagingDbContext` corresponde a `automation_inbox` para la columna `attempts`
(hay dos `inbox`, con default 0 y 1).

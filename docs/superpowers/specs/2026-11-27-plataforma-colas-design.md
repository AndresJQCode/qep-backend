# Plataforma: ver cuánto hay encolado

Fecha: 2026-11-27. Estado: **aprobado por el owner en conversación (2026-11-27), con el tope de 10 s y la prueba
nueva de arquitectura.** Tarjeta: <https://trello.com/c/CN1QTtcc>.
Frontend: <https://trello.com/c/tCGWdb4x>.

## Por qué

El owner quiere ver en la consola de plataforma lo que hay encolado, de la forma más sencilla posible: un conteo por
cola. Sin detalle por trabajo, sin acciones y sin tablas nuevas (decisión del 2026-11-26).

## Decisiones del owner (en la tarjeta)

- Sólo lectura, sin detalle ni acciones.
- Permiso propio `operator.queues.read`; no se reutiliza `operator.usage.read`.
- Puerto en Tenancy y adaptador en Bootstrapper; `null` donde no se mide, nunca cero.

## Lo verificado en el código (develop `7ec873493`)

- **`QueueMetricsSource`** (`src/Bootstrapper/Observability/QueueMetricsSource.cs`, scoped) ya mide las ocho colas de
  `QueueNames.All`: `webhook`, `automation`, `media`, `campaign`, `outbox`, `shopify_webhook`, `support_survey` y
  `ai_model`.
  - Lee con SQL crudo sobre la conexión de `MessagingDbContext`, una consulta por cola, con tope de 5 s cada una.
  - **Las ocho miden reintentos** (`count(*) FILTER (WHERE attempts > 0)`). Sólo `campaign` no mide edad
    (`NULL::float8`).
  - Por eso `QueueReading.Retrying` **no cambia de tipo**, y `QueueGauges` tampoco. El `null` de los reintentos aparece
    sólo cuando una cola no se pudo leer, y eso lo resuelve el adaptador.
- **La consola de operador** vive en `OperatorEndpoints` (Tenancy), bajo `/api/v1/tenants/{tenantId}/operator`.
  - La política exige el permiso y el handler revalida con `OperatorAuthorization.EnsureAuthorized` (403).
  - El precedente directo es el reporte de uso: `IPlatformUsageSource` en `Modules.Tenancy.Application` y
    `PlatformUsageSource` en Bootstrapper.
- **Un permiso de operador aparece en tres lugares de `QepServiceCollectionExtensions`:** el rol de fábrica `admin`
  (~3278), el catálogo `PermissionDefinition` (~4173) y la política de `AddAuthorization` (~5294). Si falta la política,
  el síntoma es un 500 y no un 403.
- **`CompositionRootTests.OnlyTheAdminRoleCarriesTheUsagePermission`** (en `tests/ArchitectureTests/`) amarra el permiso
  del reporte de uso. El de colas lleva su gemela.

## Diseño

### Contrato

`GET /api/v1/tenants/{tenantId}/operator/queues` → 200:

```json
{ "queues": [ { "queue": "webhook", "backlog": 3, "retrying": 1, "oldestAgeSeconds": 12.5 } ] }
```

- Viajan **las ocho colas, siempre y en el orden de `QueueNames.All`, también en cero.** Una cola que desaparece
  obligaría a la pantalla a conocer la lista del backend.
- `oldestAgeSeconds` es `null` en `campaign`, porque no se mide.
- **Si una cola no se puede leer** (error de base o tope de 5 s), sus tres números van `null` y las demás responden.
- 403 sin el permiso, o fuera del tenant operador.
- El nombre de la cola viaja tal cual (`shopify_webhook`). El diccionario para mostrarlo lo tiene el frontend.

### Permiso `operator.queues.read`

- Constante `OperatorPermissions.QueuesRead`.
- En el rol de fábrica `admin`, junto a `UsageRead`.
- `PermissionDefinition`:
  - título «Ver las colas de la plataforma»;
  - descripción «Permite ver cuántos trabajos hay encolados en cada cola de la plataforma.»;
  - categoría `Operator`, `"medium"`, `RequiredModules: []`;
  - `Shape = new("operator.queues", "Colas de la plataforma", "read")`.
- Política en `AddAuthorization`.

### Tenancy (Application y Api)

- `GetOperatorQueuesQuery(TenantId)` → `OperatorQueuesDto(IReadOnlyList<OperatorQueueDto> Queues)`, con
  `OperatorQueueDto(string Queue, long? Backlog, long? Retrying, double? OldestAgeSeconds)`. El DTO explica en su
  comentario por qué viajan todas las colas y por qué el `null` no es cero.
- Puerto: `IOperatorQueueSource.ListAsync(CancellationToken)`, que devuelve las filas ya en orden.
- El handler valida con `OperatorAuthorization.EnsureAuthorized(..., OperatorPermissions.QueuesRead, ...)` antes de
  leer, y después mapea.
- Endpoint en `OperatorEndpoints`: `.RequireAuthorization(OperatorPermissions.QueuesRead)`,
  `.Produces<OperatorQueuesDto>()` y `.ProducesProblem(403)`.

### Bootstrapper: adaptador `OperatorQueueSource`

Recibe `IQueueMetricsSource` y recorre `QueueNames.All` **una cola tras otra**: las consultas comparten la conexión de
`MessagingDbContext`, y leerlas en paralelo pediría un scope por cola.

- **Tope total de 10 s para toda la respuesta**, con un `CancellationTokenSource` enlazado al de la petición
  (`CancelAfter`).
  - Cuando el tope vence, la cola en curso y las que faltan van con `null`, sin consultarse.
  - Sin el tope, el peor caso sería 8 × 5 s = 40 s.
  - Los 10 s alcanzan para dos colas lentas y caben en el timeout de cualquier proxy.
- **Una cola que falla** (cualquier excepción que no sea la cancelación de la petición) va con sus tres números en
  `null`, y se anota un `Warning` con el nombre de la cola, sin datos de tenant. Las demás siguen.
- **Si se cancela la petición**, la cancelación se propaga como `OperationCanceledException`, nunca como números nulos.
  Se distingue del tope mirando el token de la petición.
- Se registra scoped, como `PlatformUsageSource`.

### Fuera de alcance

- El detalle por trabajo, `last_error`, reintentar y descartar (decisión del owner).
- Cambiar `QueueMetricsSource`, `QueueReading` o `QueueGauges`.
- El panel de métricas: sigue en Grafana con los gauges actuales.

## Cambio de arquitectura (autorizado por el owner el 2026-11-27)

- **Prueba nueva en `tests/ArchitectureTests/`:** `CompositionRootTests.OnlyTheAdminRoleCarriesTheQueuesPermission`,
  gemela de la del reporte de uso. `test-feature.ps1` la va a avisar como «Requiere autorización del owner».
- **Sin referencias nuevas entre módulos:** Bootstrapper ya referencia `Modules.Tenancy.Application` y
  `Bootstrapper.Observability`.

## Pruebas (TDD)

- **`Modules.Tenancy.UnitTests`:** el handler responde 403 sin el permiso o fuera del tenant operador, y mapea las filas
  en orden con los `null` tal cual.
- **`Bootstrapper.UnitTests`, el adaptador con un `IQueueMetricsSource` falso:**
  - ocho colas en orden;
  - una cola que lanza deja sus tres valores en `null` y las demás responden;
  - con el tope vencido, las que faltan van en `null` sin consultarse;
  - la cancelación de la petición se propaga.
- **`ArchitectureTests`:** la prueba del permiso de arriba.
- **Integración, junto a `PlatformUsageApiTests` (Messaging.IntegrationTests):** el GET devuelve 200 con las ocho colas
  para el operador, y 403 sin el permiso.

## Entrega

Comentar en la tarjeta del frontend (`tCGWdb4x`) la ruta, los campos y el permiso finales.

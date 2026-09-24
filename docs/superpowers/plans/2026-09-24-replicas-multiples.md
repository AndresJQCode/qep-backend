# Réplicas múltiples de la API — Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Subir `k8s/prod-deployment.yaml` de `replicas: 1` a `replicas: 2` sin que la app se comporte distinto a como lo hace con una sola instancia.

**Contexto:** Los recursos ya se ajustaron para esto (`820f5ec`, 2026-09-23): `requests.cpu: 500m`, `limits.cpu: "1"`. Con dos pods, el nodo tiene que reservar 1 CPU en estado estable y 1.5 durante el rollout (`maxSurge: 1`).

**Tech Stack:** .NET 10, EF Core + PostgreSQL, Kubernetes (clúster `contabo-prod`).

## Global Constraints

- **TDD obligatorio.** RED antes que GREEN, con evidencia literal de las dos corridas.
- **Comandos en PowerShell.** `A; if ($?) { B }`, nunca `&&`. `curl.exe`, nunca `curl`.
- **`kubectl` siempre con `--context contabo-prod`:** el contexto activo por defecto es un EKS ajeno.
- **`Seed__Enabled` se queda en `"true"`** en `k8s/prod-configMap.yaml`. Decisión del developer (2026-09-23): el arreglo del hallazgo 3 es un bloqueo, no apagar la semilla.
- **Commits: conventional commits, sin atribución de IA.**
- **Comentarios y textos nuevos en español colombiano, tuteando.**

## Qué ya funciona con varias réplicas

Verificado en el código; no requiere trabajo.

- **Sesión.** La cookie es un token opaco: el handler lo busca por hash en `identity.sessions`
  en cada request (`SessionCookieAuthenticationHandler.cs:38`, `SessionService.cs:55`). Una
  sesión creada en un pod vale en el otro, y el logout se ve en los dos al instante.
- **Sin Data Protection.** No hay `AddDataProtection`, `AddCookie`, `AddAntiforgery` ni
  `AddSession`, así que no hay llaves por pod que invaliden cookies al cambiar de instancia.
- **Login con Google.** El id token es un JWT sin estado, y sólo se usa en `POST /auth/session`.
- **CSRF.** Es el header `X-Qep-Client`, sin token guardado.
- **"Última actividad" de la sesión** (`SessionService.cs:64-68`). La tabla no tiene token de
  concurrencia: dos pods que la actualizan a la vez resuelven con "gana la última escritura",
  y EF sólo escribe `LastSeenAt`, así que no pisa una revocación concurrente.

Según la revisión automática del 2026-09-23 (hay que confirmarlo en la tarea 5):

- **Correos:** `OutboxDeliveryWorker` reserva cada mensaje con un lease antes de enviarlo.
- **Outbox de Tenancy:** `OutboxProcessor` toma las filas con `FOR UPDATE SKIP LOCKED`.
- **Exportaciones:** `ExportJobQueue` guarda la cola en la base, con `SKIP LOCKED` y lease.
- **WhatsApp:** se envía dentro del request, no desde un worker.
- **Archivos:** van a R2. No hay volúmenes.
- **Migraciones:** EF Core 10 las serializa con un lock en la base.

## Hallazgos

| # | Hallazgo | Evidencia | Efecto con 2 pods | Prioridad | Verificado |
|---|---|---|---|---|---|
| 1 | Readiness sin base de datos y sin PDB | `prod-deployment.yaml:54-57` apunta a `/health/live`, que es un `Results.Ok` fijo (`Program.cs:95`); no hay `AddHealthChecks` ni `PodDisruptionBudget` | Un pod sin conexión a la base sigue recibiendo tráfico; al drenar un nodo pueden caer los dos pods | Alta | Sí |
| 2 | Rate limiter en memoria y por IP | `Program.cs:45-58`, partición por `Connection.RemoteIpAddress`; sin `UseForwardedHeaders` | El límite real queda en 2 × 120/min; detrás de nginx, todos los clientes probablemente comparten la IP del proxy (eso ya pasa hoy con un pod). Sólo afecta endpoints públicos: `Program.cs:87,93` e `InvitationEndpoints.cs:25` | Media | Sí |
| 3 | La semilla corre en cada pod al arrancar, sin bloqueo | `Program.cs:157` (`RunQepSeedAsync`), `prod-configMap.yaml:84` | Choca sólo si dos pods arrancan a la vez cuando los datos aún no existen. En prod ya están sembrados, así que hoy el riesgo es bajo | Baja | Parcial |
| 4 | Workers que leen el outbox sin reservar filas | `SessionRevocationWorker.cs:62-73`, `AuditProjectionWorker.cs:56-67`, `OrphanUserCleanupWorker.cs:93-99`, `PaymentProofMoveProcessor.cs:74-81` | Los dos pods procesan el mismo mensaje; el segundo falla al guardar el registro del inbox y se deshace. Resultado: trabajo repetido y errores en el log. En Session y Audit, ese conflicto hace perder el resto del lote | Baja | No |
| 5 | Tareas periódicas en cada pod | `QuotationExpirationWorker`, `StagingCleanupProcessor`, `PaymentProofOrphanCleanupProcessor` | Se ejecutan dos veces. La expiración está protegida por `Quotation.Version`; las limpiezas pueden registrar la auditoría dos veces | Baja | No |

## Tareas

Orden: 1 y 2 bloquean el paso a `replicas: 2`. 3 a 5 pueden ir después.

### Tarea 1 — Readiness con base de datos y PodDisruptionBudget

**Files:**
- Modify: `src/Api/Program.cs` (registro y mapeo del health check)
- Modify: `k8s/prod-deployment.yaml` (el `readinessProbe` apunta al endpoint nuevo)
- Create: `k8s/prod-pdb.yaml`
- Test: prueba de integración del endpoint nuevo

- [ ] Prueba RED: `GET /health/ready` responde 200 con la base arriba y 503 sin conexión.
- [ ] Registrar `AddHealthChecks()` con un chequeo de conexión a PostgreSQL y mapear
      `/health/ready`. `/health/live` sigue sin tocar la base: si la liveness dependiera de ella,
      una caída de la base reiniciaría todos los pods en cadena.
- [ ] `readinessProbe` → `/health/ready`. `startupProbe` y `livenessProbe` se quedan en
      `/health/live`.
- [ ] `PodDisruptionBudget` con `minAvailable: 1` y el mismo selector del Deployment, en
      `k8s/prod-pdb.yaml`. **No** se agrega a `azure-pipelines.yml` todavía (ver tarea 6).
- [ ] GREEN con evidencia literal.

### Tarea 2 — IP real del cliente en el rate limiter

**Files:**
- Modify: `src/Api/Program.cs`
- Test: prueba de integración del limitador

- [ ] Averiguar qué header manda el ingress (`X-Forwarded-For` en ingress-nginx) y qué red
      tiene el proxy. Sin eso, confiar en el header deja que cualquier cliente falsifique su IP.
- [ ] Prueba RED: dos clientes con distinto `X-Forwarded-For`, que llegan desde el proxy
      conocido, tienen límites separados.
- [ ] `UseForwardedHeaders` con `KnownNetworks`/`KnownProxies` limitados al ingress, antes de
      `UseRateLimiter`.
- [ ] Decidir si el límite total de N × 120/min por IP es aceptable. Si no lo es, bajar
      `PermitLimit` en proporción a las réplicas o mover el límite al ingress
      (`nginx.ingress.kubernetes.io/limit-rpm`). **DECISIÓN-PENDIENTE.**
- [ ] GREEN con evidencia literal.

### Tarea 3 — Bloqueo en la semilla

**Files:**
- Modify: `src/Bootstrapper/Seeding/QepSeedRunner.cs`
- Test: prueba de integración con dos ejecuciones concurrentes

- [ ] Leer los seeders para confirmar que "saltan lo existente" consultando antes de insertar
      (la revisión automática sólo vio la descripción del runner).
- [ ] Prueba RED: dos `RunQepSeedAsync` concurrentes sobre una base vacía terminan sin
      excepción y sin filas duplicadas.
- [ ] Envolver la corrida en `pg_advisory_lock` con una clave fija, dentro de una conexión
      dedicada. El segundo pod espera al primero y después encuentra todo sembrado.
- [ ] GREEN con evidencia literal.

### Tarea 4 — Reservar filas en los workers de outbox

**Files:**
- Modify: los cuatro workers del hallazgo 4

- [ ] Confirmar cada hallazgo leyendo el worker. Descartar los que ya reservan filas.
- [ ] Para cada worker confirmado: prueba RED con dos instancias del worker sobre el mismo
      mensaje, donde sólo una lo procesa y ninguna registra error.
- [ ] Reservar con `FOR UPDATE SKIP LOCKED` dentro de la transacción, siguiendo el patrón de
      `OutboxProcessor` (Tenancy).
- [ ] GREEN con evidencia literal.

### Tarea 5 — Un solo pod por pasada en las tareas periódicas

**Files:**
- Modify: los tres procesadores del hallazgo 5

- [ ] Confirmar el hallazgo y verificar los casos seguros listados arriba.
- [ ] Prueba RED: dos pasadas concurrentes y una sola auditoría registrada.
- [ ] `pg_try_advisory_lock` al inicio de cada pasada: el pod que no lo obtiene se salta esa
      pasada. `OrphanUserCleanupWorker.cs:143` ya usa este patrón.
- [ ] GREEN con evidencia literal.

### Tarea 6 — Subir a dos réplicas

**Files:**
- Modify: `k8s/prod-deployment.yaml` (`replicas: 2`)
- Modify: `azure-pipelines.yml` (agregar `pdb` a la lista de manifests, después de `deployment`)

- [ ] Tareas 1 y 2 cerradas.
- [ ] Activar el PDB en el mismo commit que `replicas: 2`. `k8s/prod-pdb.yaml` quedó preparado
      en la tarea 1 pero fuera del pipeline: con una sola réplica bloquearía el drain del nodo.
- [ ] Confirmar que el nodo tiene al menos 1.5 CPU libres para reservar:
      `kubectl --context contabo-prod describe node <nodo>` → `Allocated resources`.
- [ ] Aplicar el manifiesto y verificar que los dos pods quedan `Ready`.
- [ ] Iniciar sesión, hacer 20 requests seguidos y comprobar en los logs de los dos pods que la
      sesión sirve en ambos.
- [ ] Revisar los logs de los workers durante 15 minutos para ver conflictos de inbox (sólo si
      la tarea 4 no se hizo).

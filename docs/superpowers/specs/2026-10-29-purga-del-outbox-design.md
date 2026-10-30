# Purga segura del outbox de plataforma — diseño

Fecha: 2026-10-29. Estado: aprobado por el owner (2026-10-29). Sigue al índice
`IX_outbox_messages_event_name_occurred_at` (merge `0c3a233d`, ver `docs/estado.md`, Registro 2026-10-29).

## Por qué

Una revisión de producción del 2026-10-29 encontró lentitud en el polling del outbox: `platform.outbox_messages` con
7.8 k filas, 672 M tuplas leídas por seq scan y más de 2.6 M llamadas. El índice por `(event_name, occurred_at)` quitó el
barrido de la tabla entera. Queda un costo que crece: cada consumidor recorre en orden **todas** las filas de su evento
hasta encontrar las que no tiene en su inbox, y como nada se borra —ni el outbox ni los siete inbox— ese recorrido se
alarga cada día. Esta purga hace que el tamaño de las tablas dependa del tráfico de las últimas horas, no de la edad del
sistema.

El objetivo es rendimiento, no historial: nadie consulta `outbox_messages` para investigar (decisión del owner,
2026-10-29). La evidencia que importa vive en `audit.entries`, `notifications.*` y los efectos de cada consumidor.

## Metas, no-metas y anti-metas

**Metas**

- Borrar una fila del outbox cuando **todos** los consumidores de su `event_name` la procesaron y pasaron 24 horas.
- Borrar las filas de los siete inbox cuando su mensaje ya no existe en el outbox.
- Que agregar un consumidor nuevo sin declararlo en la purga ponga una prueba en rojo.

**No-metas**

- Cambiar cómo consumen los workers (cursor por consumidor, otro orden, otro intervalo de polling).
- Conservar un historial de eventos en otro lado.
- Purgar `messaging.automation_inbox`: no la alimenta el outbox y ya tiene su purga (`InboxRetentionDays`).

**Anti-metas — si pasa esto, falló aunque "funcione"**

- Que un consumidor no reciba un mensaje porque la purga lo borró antes. El síntoma sería un correo, una auditoría o un
  negocio del embudo que no aparece nunca, sin error.
- Que un consumidor procese **dos veces** un mensaje porque se borró su fila de inbox mientras el mensaje seguía en el
  outbox (la guarda de idempotencia es el inbox).
- Decidir con `processed_at` del outbox solo. Ese campo lo marca `OutboxProcessor` de Tenancy en **todas** las filas,
  tengan o no handler (`OutboxProcessor.cs:49-51`), así que no dice nada de los consumidores de los otros módulos.

## Lo que hay hoy (verificado el 2026-10-29)

Consumidores de `platform.outbox_messages`. Todos filtran por `event_name`, ordenan por `occurred_at` y hacen anti-join
contra su inbox por `(consumer, message_id)`:

| Consumidor (`consumer` en el inbox) | `event_name` | Inbox |
| --- | --- | --- |
| `tenancy.tenant-settings-change-log` (vía `OutboxProcessor` + `IntegrationEventDispatcher`) | `tenancy.tenant-settings-updated.v1` | `platform.inbox_messages` |
| `audit.projection` | `platform.audit.recorded.v1` | `audit.inbox_messages` |
| `notifications.invitation-email` | `tenancy.membership-invited.v1` | `notifications.inbox_messages` |
| `notifications.email-verification-email` | `identity.email-verification-requested.v1` | `notifications.inbox_messages` |
| `notifications.password-reset-email` | `identity.password-reset-requested.v1` | `notifications.inbox_messages` |
| `notifications.existing-account-email` | `identity.existing-account-notice.v1` | `notifications.inbox_messages` |
| `notifications.customer-export-email` | `customers.export-ready.v1` | `notifications.inbox_messages` |
| `notifications.product-export-email` | `catalog.product-export-ready.v1` | `notifications.inbox_messages` |
| `notifications.supplier-export-email` | `suppliers.export-ready.v1` | `notifications.inbox_messages` |
| `notifications.quotations-export-ready-email` | `quotations.export-ready.v1` | `notifications.inbox_messages` |
| `notifications.quotations-export-failed-email` | `quotations.export-failed.v1` | `notifications.inbox_messages` |
| `identity.session-revocation` | `tenancy.membership-suspended.v1`, `tenancy.membership-removed.v1` | `identity.inbox_messages` |
| `identity.orphan-user-cleanup` | `tenancy.membership-removed.v1` | `identity.inbox_messages` |
| `pipeline.quotation-events` | `quotations.quotation.sent.v1`, `quotations.quotation.converted.v1`, `quotations.order.created.v1`, `quotations.order.cancelled.v1` | `pipeline.inbox_messages` |
| `shopify.city-synonym-events` | `geography.city_synonym.resolved.v1`, `geography.city_synonym.corrected.v1` | `shopify.inbox_messages` |
| `storage.payment-proof-move` | `quotations.order.payment-proofs-attached.v1` | `storage.inbox_messages` |
| `storage.payment-proof-detach` | `quotations.order.payment-proofs-detached.v1` | `storage.inbox_messages` |

Eventos que se producen y **ningún módulo consume** (sólo los marca `OutboxProcessor`):
`tenancy.membership-accepted.v1`, `tenancy.membership-reactivated.v1`, `tenancy.membership-roles-changed.v1`,
`tenancy.tenant-logo-updated.v1`, `integrations.connection-paused.v1`, `integrations.connection-deleted.v1`,
`integrations.connection-needs-attention.v1`.

Los siete inbox tienen PK `(consumer, message_id)`, sin índices secundarios ni FK, y ninguno se purga. Audit, Storage y
Platform insertan la fila ya con `processed_at`; Identity, Notifications, Pipeline y Shopify además reclaman con
`claimed_until`/`attempts`, así que puede existir una fila con `processed_at` nulo.

Cómo termina un mensaje que falla siempre:

- Notifications lo marca hecho (notificación `Failed`) al cuarto reclamo (`attempts > MaxAttempts`, con `MaxAttempts = 3`).
- Pipeline, Shopify y Storage marcan hecho un payload malformado; los demás errores se reintentan para siempre.
- Identity y `OutboxProcessor` reintentan para siempre.
- Audit se detiene entero ante una fila mala: no hay `catch` por mensaje, así que la misma fila encabeza cada lote.

**Para la purga, un mensaje atascado no se borra nunca, y eso es lo correcto.**

## Qué se construye

### 1. Registro de consumidores

Una lista en código —en Bootstrapper, que ve todos los módulos— con una entrada por consumidor: su `consumer`, los
`event_name` que lee y la tabla de inbox donde anota (`schema.inbox_messages`). Es la fuente de verdad de la purga: para
un `event_name`, sus consumidores son los de la lista y ningún otro.

**Una prueba amarra la lista a los workers reales.** Agregar un consumidor, cambiarle el nombre o hacerle leer otro
evento sin tocar la lista la pone en rojo. Mismo patrón que `ShopifyWebhookTopicsParityTests` y
`HandoffCauseKindParityTests`. Cómo obtiene la prueba los nombres de cada worker —constantes expuestas,
`InternalsVisibleTo`, reflexión— lo decide quien construya.

**Por qué no descubrir los consumidores desde los datos** («los que alguna vez escribieron un inbox para este evento»):
un consumidor que todavía no ha procesado ningún mensaje de su evento no aparecería, y la purga le borraría todo.

### 2. Regla de borrado del outbox

Una fila de `platform.outbox_messages` se borra cuando se cumple **todo** esto, con `corte = ahora − 24 h`:

1. `processed_at IS NOT NULL AND processed_at < corte`. Cubre al despachador de Tenancy: si está reintentando, la fila
   se queda.
2. Para **cada** consumidor registrado de su `event_name`, existe su fila en el inbox que le corresponde con
   `processed_at IS NOT NULL AND processed_at < corte`. Una fila reclamada sin terminar, o ausente, la frena.
3. Si el `event_name` no tiene consumidores registrados, basta la condición 1.

Las 24 horas son margen para mirar lo de ayer y para que un consumidor recién desplegado alcance eventos recientes; la
seguridad la da la condición 2, no el plazo (owner, 2026-10-29). Plazo configurable.

### 3. Regla de borrado de los inbox

Una fila de cualquiera de los siete inbox se borra cuando su `message_id` **ya no existe** en `platform.outbox_messages`.
En la misma corrida, después del outbox.

**Nunca se borra una fila de inbox cuyo mensaje sigue en el outbox.** Es la guarda de idempotencia: sin ella, el
consumidor vería el mensaje como pendiente y lo procesaría otra vez.

Los inbox se purgan por tabla, no por consumidor, porque varios consumidores comparten tabla (`notifications`,
`identity`, `storage`).

### 4. El worker

- Un `BackgroundService` en Bootstrapper. El SQL cruza siete esquemas (`platform`, que tiene el outbox y el inbox de
  Tenancy, más `audit`, `notifications`, `identity`, `pipeline`, `shopify` y `storage`), como `CustomerMergeReferences` y
  `OrdersReportSource`, que también viven ahí por la misma razón.
- Corre cada hora, por lotes de 1000 filas, en bucle mientras el lote salga lleno. Cada lote en su propio scope y
  transacción. Idioma a copiar: `AutomationInboxWorker` (barrido con `PeriodicTimer`, `DELETE … WHERE pk IN (SELECT …
  LIMIT n)`, `Math.Max(1, …)` sobre la retención) y `WebhookPurgeWorker` (`DrainAsync` interno para pruebas).
- Opciones con su sección propia: retención en horas (default 24), intervalo, tamaño de lote y un `Enabled`. Nombres y
  sección los decide quien construya.
- La primera corrida espera un intervalo completo después del arranque, como `AutomationInboxWorker`. Así un host de
  pruebas no purga lo que otra prueba sembró con fechas viejas. Además, las factorías de integración la fijan apagada
  salvo en las pruebas de la purga, que la ejercen con el `DrainAsync` interno (decisión de diseño, no del owner).
- Registra en el log cuántas filas borró de cada tabla, y nada del payload.
- Un fallo en una tabla no frena las demás en la misma corrida. Si falla el outbox, los inbox se purgan igual: su regla
  (mensaje ausente) no depende de que el outbox se haya purgado en esa corrida.

### 5. Varias réplicas

El spec `2026-10-29-replicas-multiples-design.md` lleva la API a dos pods. Dos purgas a la vez no se pisan: los dos
`DELETE` son idempotentes y la regla es la misma. Si quien construya prefiere que corra una sola, un
`pg_try_advisory_lock` y salir si está tomado es suficiente; no hace falta más coordinación.

## Pruebas que tiene que haber

Integración contra el esquema migrado (Testcontainers, como el resto). Cada caso siembra outbox e inbox por SQL:

- Todos los consumidores procesaron hace más de 24 h → se borra el mensaje y luego sus filas de inbox.
- Falta un consumidor de los dos (`tenancy.membership-removed.v1` tiene dos en `identity`) → no se borra nada.
- Un consumidor reclamó y no terminó (`processed_at` nulo, `claimed_until` futuro) → no se borra.
- Todos procesaron hace menos de 24 h → no se borra.
- `processed_at` del outbox nulo (el despachador de Tenancy reintentando) → no se borra.
- Evento sin consumidores registrado, procesado hace más de 24 h → se borra.
- Fila de inbox cuyo mensaje sigue en el outbox → no se borra, aunque sea vieja.
- Fila de inbox cuyo mensaje ya no existe → se borra.
- Más filas que el tamaño de lote → se borran todas en una corrida.
- Prueba de paridad del registro (punto 1).

## Lo que esto le cambia a otros trabajos

- **Borrar conversaciones al borrar conexión** (`2026-10-29-borrar-conversaciones-al-borrar-conexion-design.md`) planea un
  consumidor `messaging.connection-deleted` de `integrations.connection-deleted.v1`, con un inbox nuevo
  (`messaging.inbox_messages`). Las dos cosas son compatibles: ese spec no quiere efecto retroactivo (su D-12 marca como
  procesados los eventos que ya existen), así que no le importa que la purga haya borrado los viejos. **El que salga
  segundo** suma el consumidor y la tabla al registro, a la prueba de paridad y a las pruebas de la purga; con eso los
  esquemas pasan a ser ocho (decisión de diseño).
- **Despliegue con dos pods** (spec de réplicas): durante el rolling deploy, el pod viejo purga con el registro viejo.
  Si el release trae un consumidor nuevo, el pod viejo puede borrar eventos de más de 24 h que ese consumidor no vio. Es
  el mismo caso que la regla de abajo y se acepta igual (decisión de diseño).
- **Un consumidor nuevo, en general,** empieza a ver eventos desde lo que quede en la tabla, no desde el principio. Va
  como regla en `CLAUDE.md` junto con la del registro.
- **`shopify.city_correction_jobs.source_message_id`** guarda el id del mensaje del outbox, sin FK. Después de la purga
  apunta a una fila que ya no existe. Hoy sólo se usa para no sembrar dos veces el mismo evento (índice único parcial), y
  eso sigue valiendo. Quien construya lo confirma.
- **`OutboxIdempotencyTests`** pone `processed_at = NULL` a mano para forzar una reentrega. No choca: el mensaje es de hace
  segundos y la purga sólo toca lo que tiene más de 24 h.

## Lo que queda a criterio de quien construya

- Forma del registro (record, diccionario, atributo) y cómo la prueba de paridad lee a los workers.
- Si el `DELETE` del outbox es un solo SQL con `NOT EXISTS` por consumidor generado desde el registro, o una consulta por
  `event_name`.
- Nombres de la clase, de las opciones y de su sección de configuración.
- Si hace falta un índice en los inbox para el `NOT EXISTS` inverso (por `message_id` solo). La PK empieza por
  `consumer`; con los tamaños de hoy probablemente no, y se decide con `EXPLAIN`.

## Despliegue

- Sin migraciones, salvo que el `EXPLAIN` pida un índice.
- **`Enabled` viene prendido por defecto en `appsettings.json`** (owner, 2026-10-29): la purga arranca sola con el
  despliegue, sin paso manual en el ConfigMap.
- La primera corrida en producción borra casi toda la historia (~7.8 k filas y sus inbox): unos cuantos lotes. Los
  efectos de esos eventos (correos, `audit.entries`, negocios del embudo) viven en sus tablas y no se tocan.
- Rollback: apagar con la opción `Enabled`. Lo borrado no vuelve, y por la regla 2 no le hacía falta a nadie.

# Ajustes posteriores a la exportación asíncrona — diseño

**Fecha:** 2026-09-13
**Rama:** `feature/ajustes-post-export` (backend). La corrección de voseo del frontend va en su propio commit.
**Spec anterior:** [2026-09-12-export-asincrono-design.md](2026-09-12-export-asincrono-design.md). Este documento cierra los pendientes que dejó.

## Contexto

La exportación asíncrona de cotizaciones y ventas ya está en `develop`, en `037718a` para el backend y `08c6158` para el frontend. La revisión final dejó cinco pendientes fuera de su alcance:

1. **Correos duplicados.** Los cinco workers de correo de Notifications mandan el mismo correo dos veces si hay más de un proceso consumiendo.
2. **Workers muertos por timeout.** Esos mismos workers se detienen en silencio cuando el proveedor de correo tarda más de la cuenta.
3. **Enums en inglés.** El Excel de la exportación muestra los estados con su nombre en inglés (`Draft`, `PaymentPending`).
4. **Voseo en ventas.** `describeSalesFailure`, en el frontend, todavía usa voseo.
5. **Memoria sin medir.** No se midió una exportación real de un año; en el spec anterior figura como el riesgo «Memoria no medida».

## Decisiones

| # | Decisión | Por qué |
| --- | --- | --- |
| A1 | Un worker reclama cada mensaje en **su propio inbox**, con un lease, antes de enviar. | La PK `(consumer, message_id)` garantiza un solo ganador. Es una migración solo de Notifications, sigue el patrón de `quotations.export_jobs` y no toca la tabla de Tenancy. |
| A2 | Descartado: `FOR UPDATE SKIP LOCKED` sobre `platform.outbox_messages`. | El publicador de Tenancy ya hace `SKIP LOCKED` sobre esas filas, y cada uno se saltaría las del otro. Además, la tabla es de otro módulo. |
| A3 | Descartado: `pg_advisory_xact_lock` por mensaje. | Deja una transacción y una conexión abiertas durante toda la llamada HTTP a Infobip. |
| A4 | El worker sale del loop **solo** si el proceso se está apagando. | Un timeout del proveedor es una falla de envío, no un apagado. Mismo criterio que `ExportJobWorker` y `ExportJobRunner`. |
| A5 | El `HttpClient` de Infobip tiene un timeout explícito de 30 s. | Hoy usa los 100 s implícitos de `new HttpClient()`. El lease tiene que quedar por encima del timeout. |
| A6 | Los cinco workers comparten una base abstracta, `OutboxDeliveryWorker`. | El arreglo se hace una vez y no cinco. Cada worker conserva solo lo suyo: consumidor, evento, payload y correo. |
| A7 | El Excel usa las etiquetas en español **de las tablas de los listados**. | El Excel es «lo que estoy viendo, entero»: tiene que decir lo mismo que la pantalla de la que sale. |
| A8 | La API sigue mandando los enums con su nombre en inglés. | Es contrato, y el diccionario de la pantalla lo tiene el frontend. Las etiquetas son solo del archivo, que es lo que lee una persona. |
| A9 | Del voseo del frontend se corrige solo `describeSalesFailure`. | Las 303 apariciones en 68 archivos quedan para un barrido aparte. |
| A10 | La memoria se mide **en producción**, con un seed de carga en un tenant propio. | El proyecto está en fase de desarrollo. El pod real, con su CPU y su límite de 1Gi, es lo que importa medir. |

## Sección 1: los workers de correo de Notifications

### Qué pasa hoy

Los cinco workers son:

- `InvitationDeliveryWorker`
- `CustomerExportDeliveryWorker`
- `ProductExportDeliveryWorker`
- `QuotationsExportReadyDeliveryWorker`
- `QuotationsExportFailedDeliveryWorker`

Todos leen `platform.outbox_messages` con un anti-join contra `notifications.inbox_messages` que no toma ningún lock ni lease (`InvitationDeliveryWorker.cs:61-67`). Por cada mensaje, envían el correo y después guardan la notificación y la fila del inbox en un solo `SaveChanges`.

Esto trae tres problemas:

- **Correos duplicados.** Si dos procesos toman el mismo mensaje, los dos envían. El segundo recién choca contra `PK_inbox_messages` al guardar, y ese error solo aparece como una falla genérica del tick. Puede pasar con varias réplicas, o cuando el pod viejo y el nuevo se solapan durante un deploy.
- **Workers muertos sin log.** Un timeout de Infobip lanza `TaskCanceledException`. El `catch (OperationCanceledException) { break; }` de afuera lo toma como un apagado y el worker se detiene sin loguear, hasta que el proceso se reinicia (`InvitationDeliveryWorker.cs:32-51`).
- **Lotes bloqueados.** Una excepción fuera del `try` del envío (JSON inválido, un `GetEmailAsync` que falla, un guardado que falla) aborta el resto del lote. Como cada tick empieza de nuevo desde el mensaje más viejo, el mismo mensaje lo vuelve a abortar en cada tick.

### Diseño

**Base común.** `OutboxDeliveryWorker` (abstracta, en `Modules.Notifications.Infrastructure/Messaging/`) se queda con todo lo compartido:

- el loop con `PeriodicTimer` (3 s) y el lote de 20;
- la consulta de candidatos y el reclamo;
- el aislamiento por mensaje;
- el manejo de la cancelación.

Cada worker concreto declara `Consumer` y `EventName`, parsea su payload y arma su correo.

**Reclamo por mensaje:**

1. **Candidatos.** Son las filas del outbox de su evento que cumplen una de dos condiciones: no tienen fila en el inbox, o la tienen con `processed_at IS NULL` y `claimed_until < ahora` (un lease vencido).
2. **Reclamo.** Es una sola sentencia, en su propia transacción y comprometida antes de enviar:

   ```sql
   INSERT INTO notifications.inbox_messages (consumer, message_id, claimed_until, attempts)
   VALUES (@consumer, @id, @now + lease, 1)
   ON CONFLICT (consumer, message_id) DO UPDATE
     SET claimed_until = @now + lease, attempts = inbox_messages.attempts + 1
     WHERE inbox_messages.processed_at IS NULL AND inbox_messages.claimed_until < @now
   RETURNING attempts;
   ```

   Si no devuelve ninguna fila, otra réplica ya lo tiene o ya lo procesó, y este worker lo salta. `@now` sale de `IClock`.

3. **Envío.** Se manda el correo y después, en un solo `SaveChanges`, se agrega la notificación y se fija `processed_at = ahora`.
4. **Mensaje envenenado.** Si el reclamo devuelve `attempts > 3`, el mensaje ya se tomó tres veces sin terminar. Se registra la notificación como `Failed` y se fija `processed_at`, sin volver a enviarlo.

**Constantes.**

| Qué | Valor | Motivo |
| --- | --- | --- |
| Lease | 2 minutos | Por encima del timeout del `HttpClient` (30 s), con margen para el render y el guardado. |
| Máximo de intentos | 3 | Corta un mensaje envenenado. |
| Lote | 20 | Igual que hoy. |
| Tick | 3 s | Igual que hoy. |

**Qué garantiza.**

- Dos réplicas nunca envían el mismo mensaje al mismo tiempo.
- **Residual:** es el mismo de hoy. Si el proceso muere justo entre el envío y el guardado, el correo se reenvía cuando vence el lease. Infobip no recibe una clave de idempotencia, así que esto no se puede cerrar del todo. Queda documentado.

**Fallas de envío.** Mantienen la semántica actual: cualquier excepción de envío deja la notificación en `Failed`, fija `processed_at` y el mensaje no se reintenta. La diferencia es que el timeout del proveedor (`TaskCanceledException` sin apagado) ahora entra en ese mismo camino, en vez de matar el worker.

**Cancelación.** La salida del loop queda así:

```csharp
catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
{
    break;
}
```

Cualquier otra `OperationCanceledException` se loguea y el loop sigue. El `catch` interno del envío deja de excluir `OperationCanceledException`, salvo cuando el token del apagado está cancelado.

**Aislamiento por mensaje.** Cada mensaje corre en su propio `try`, con `ChangeTracker.Clear()` al terminar, que es el precedente de `OrphanUserCleanupWorker`. Un mensaje que falla fuera del envío se loguea y deja su reclamo vivo. Cuando el lease vence se reintenta, y al tercer intento se corta. Ya no bloquea al resto del lote.

**`HttpClient`.** `InfobipEmailChannel` recibe un `HttpClient` con `Timeout = TimeSpan.FromSeconds(30)`, en vez de `new HttpClient()` sin configurar (`NotificationsInfrastructureExtensions.cs:75-78`).

**Migración** (`Modules.Notifications.Infrastructure`, contexto `NotificationsDbContext`):

- `processed_at` pasa a nullable.
- Se agrega `claimed_until timestamptz null`.
- Se agrega `attempts integer not null default 1`.

Las filas existentes quedan con `processed_at` fijado, es decir, procesadas. No hace falta tocar las filas.

### Pruebas

**Cómo se ejercitan los workers:**

- `DrainAsync` pasa a ser `internal`, siguiendo el precedente de `ExportJobWorker`.
- `Modules.Notifications.Infrastructure.csproj` suma `InternalsVisibleTo` para `Modules.Notifications.IntegrationTests`.
- El harness de las pruebas de integración gana un switch para no levantar los workers hospedados, igual que `runExportWorker` en Quotations. Así una prueba que llama a `DrainAsync` no compite con el worker del host.

**Casos**, todos de integración contra Postgres, con un `IEmailChannel` de prueba que cuenta los envíos y puede fallar a pedido:

- **Duplicados:** dos `DrainAsync` concurrentes sobre el mismo mensaje producen un solo envío y una sola notificación.
- **Timeout del proveedor:** el canal lanza `TaskCanceledException` sin apagado; la notificación queda `Failed`, se fija `processed_at` y un segundo tick del mismo worker sigue procesando mensajes nuevos.
- **Lease vencido:** un reclamo con el lease vencido y sin `processed_at` se retoma, y `attempts` sube a 2.
- **Mensaje envenenado:** al tercer reintento se registra `Failed` sin enviar.
- **Aislamiento:** un mensaje con payload inválido no impide que los siguientes del mismo lote se envíen.
- **Regresión:** las pruebas actuales (`InvitationNotificationTests`, `QuotationsExportNotificationTests`) siguen en verde.

## Sección 2: etiquetas del Excel, voseo y medición

### Etiquetas del Excel

Un mapa nuevo en `Modules.Quotations.Application` traduce los estados a las etiquetas de las tablas de los listados:

| Enum | Valor | Etiqueta |
| --- | --- | --- |
| `QuotationStatus` | `Draft` | Borrador |
|  | `Sent` | Enviada |
|  | `Voided` | Anulada |
|  | `Expired` | Vencida |
| `SaleStatus` | `Pending` | Pendiente |
|  | `Approved` | Aprobada |
| `SalePaymentStatus` | `FullPaymentReceived` | Pago total |
|  | `PartialPaymentReceived` | Pago parcial |
|  | `PaymentPending` | Pago pendiente |

Las etiquetas vienen del frontend: `quote-status-badge.tsx` y `sale-list.ts` (`SALE_STATUS_LABELS`, `SALE_PAYMENT_STATUS_LABELS`).

**Qué cambia en cada procesador:**

- En `QuotationsExportProcessor`, la columna «Estado».
- En `SalesExportProcessor`, la columna «Estado» y la columna «Pago» cuando no hay forma de pago: hoy cae al nombre del enum y pasa a mostrar la etiqueta del estado de pago.

La forma de pago (`Efectivo`, `Transferencia`…) ya está en español y no cambia. La moneda sigue como código ISO.

**Pruebas:**

- Una prueba unitaria recorre `Enum.GetValues` de los tres enums y falla si algún valor no tiene etiqueta. Así un estado nuevo no llega al Excel en inglés sin que nadie se entere.
- Las pruebas de los dos procesadores verifican las celdas con sus etiquetas.

**Documentación:** este documento. El D8 del spec anterior se complementa con una línea que remite acá.

### Voseo (frontend)

`src/features/sales/services/sales.api.ts:117` pasa de «No tenés permiso para ver las ventas de este espacio de trabajo.» a «No tienes permiso para ver las ventas de este espacio de trabajo.». Si alguna prueba verifica ese texto, se actualiza también. Va en su propio commit en `qep-frontend`.

### Medición en producción: seed de carga

**Qué es.** Un seed nuevo, separado del de arranque (`QepSeedRunner`, `Seed:Enabled`, que en producción está en `true` y corre en cada arranque).

**Interruptor.** `Seed:ExportLoad:Quotations` es un número y `0` lo apaga, que es el valor por defecto. Es independiente de `Seed:Enabled`. El validador exige `Seed:OwnerEmail` cuando el número es mayor que 0.

**Tenant propio.**

- El slug es `carga-export`, con un GUID fijo.
- Tiene una membresía de admin para `Seed:OwnerEmail`.
- El developer entra con su cuenta, cambia a ese tenant y exporta desde la pantalla, como cualquier usuario.
- `origen-botanico` no se toca.

**Cuándo corre.** En un `BackgroundService` que arranca después de que la API está en pie, nunca dentro de `RunQepSeedAsync`. El `startupProbe` le da al pod como máximo 60 s (`prod-deployment.yaml`: 12 intentos cada 5 s). Sembrar decenas de miles de filas dentro del arranque haría que Kubernetes matara el pod, y con `maxUnavailable: 0` el deploy nunca quedaría listo.

**Cómo inserta.**

- Con SQL masivo (`INSERT … SELECT … FROM generate_series`) directo sobre las tablas de clientes, de cotizaciones con sus ítems y de ventas.
- No pasa por los handlers, así que no genera outbox, auditoría, correos ni WhatsApp.
- Las fechas se reparten en los últimos 12 meses.
- Cerca del 30 % de las cotizaciones termina convertida en venta.
- El plan fija las columnas exactas después de leer los mapeos de cada tabla.

**Idempotente.** Si el tenant `carga-export` ya tiene cotizaciones, no siembra de nuevo.

**Volumen.** Por defecto, 50 000 cotizaciones. Se sube cambiando el número en el ConfigMap.

**Medición.**

1. El developer prende el interruptor en el ConfigMap y despliega.
2. Espera a que el log del seed diga que terminó.
3. Exporta un año de cotizaciones y otro de ventas desde la pantalla.
4. Mientras tanto se observa, en solo lectura, `kubectl --context contabo-prod top pod` y los logs del job: duración, filas y tamaño del archivo.
5. Los números reemplazan el riesgo «Memoria no medida» en el spec anterior.

**Limpieza.**

- `ops/export-load-cleanup.sql` borra solo lo del tenant `carga-export` (ventas, ítems, cotizaciones, clientes, jobs de exportación, membresía y tenant), en el orden que exigen las relaciones.
- Después se apaga el interruptor.
- Los `.xlsx` generados los borra la regla de lifecycle de R2 sobre `exports/`, que ya existe.

**Qué queda en el repo.** El código del seed se commitea apagado por defecto, para reutilizarlo en futuras pruebas de carga.

## Fuera de alcance

- **El mismo patrón de cancelación en otros workers.** El `catch (OperationCanceledException) { break; }` sin filtro está también en Tenancy (`OutboxPublisherWorker.cs:29-32`), Identity (`OrphanUserCleanupWorker.cs:74-77`) y Audit (`AuditProjectionWorker.cs:38-41`). Queda como seguimiento aparte.
- **El barrido de voseo del frontend:** 303 apariciones en 68 archivos.
- **Unificar las etiquetas del frontend**, que hoy tienen tres redacciones distintas para el estado de pago.
- **Una clave de idempotencia ante Infobip.** El residual del «muere entre el envío y el guardado» queda aceptado.

## Riesgos

- **Datos sintéticos en producción.** Mientras estén cargados, conviven con los datos de desarrollo en la misma base. Quedan aislados por tenant y la limpieza se hace por tenant, no con `TRUNCATE`.
- **La medición no es un banco de pruebas.** Con una sola réplica, la exportación corre en el mismo pod que la API. Si alguien usa la API durante la medición, los números lo reflejan.
- **Reenvío residual de correos.** Descrito en la Sección 1.

## Entregables y commits

**Backend**, en `feature/ajustes-post-export`:

1. `fix(notifications): un solo envío por mensaje y timeout del proveedor sin matar el worker`. Incluye la base común, el reclamo, la migración, el `HttpClient` y las pruebas.
2. `feat(quotations): el Excel muestra los estados en español`
3. `feat(seed): carga sintética para medir la exportación`. Incluye el seed, las opciones, el validador y `ops/export-load-cleanup.sql`.
4. `docs(quotations): la medición de la exportación de un año`. Va después de medir en producción.

**Frontend**, en su propia rama:

5. `fix(sales): tuteo en el error de permiso de ventas`

## Verificación

- TDD con salida literal de RED y GREEN en cada prueba nueva.
- La suite completa del backend se compara por nombre contra el baseline. En `037718a` fallan 16 pruebas conocidas: 9 de Reporting y 7 del import de Customers.
- ArchitectureTests en verde.
- `dotnet restore --locked-mode` pasa.

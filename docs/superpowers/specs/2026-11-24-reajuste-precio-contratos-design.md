# Reajuste de precio de los contratos de servicio: diseño y contrato de backend

- **Estado:** propuesta, 2026-11-24. Sin código ni plan.
- **Origen:** conversación con el owner, 2026-11-24. Las decisiones del owner se marcan **(owner)**; las propuestas que se le
  mostraron y no objetó, **(propuesta mostrada al owner)**; las que toma este documento, **(decisión del diseño, revisable)**.
  Una decisión del diseño la puede revisar el builder si el código la contradice; lo que sólo el owner puede contestar está en
  §11 y no se inventa.
- **Documento hermano:** `qep-frontend/docs/superpowers/specs/2026-11-24-reajuste-precio-contratos-design.md` (pantallas y
  comportamiento del cliente). **Este documento es dueño de las reglas de dominio y del contrato HTTP**; el del frontend lo cita y
  no lo redefine.
- **Base:** `docs/superpowers/specs/2026-10-29-cobros-recurrentes.md` (en adelante, «spec de cobros»). Nombres del contrato
  desplegado sin cambios: esquema `billing`, clave de módulo `billing`, permisos `billing.service.*`, rutas
  `/api/v1/tenants/{tenantId}/billing`, códigos `billing.*`. En el código el módulo es `ServiceContracts`.
- **Despliegue:** el backend primero. Todo lo nuevo del contrato es aditivo: un frontend viejo sigue funcionando.

## 1. Intención

Los precios de los contratos suben cada año de servicio o porque cambió lo que el cliente contrató. Casi siempre suben por el
IPC de Colombia, a veces con puntos adicionales, y el porcentaje cambia cada año (owner).

Hoy el sistema no ayuda en nada de eso:

- Un contrato tiene un solo precio (`ServiceContract.Price`). Cada cobro lo copia al generarse (`Charge.Generate`), cinco días
  antes del vencimiento (`ChargeGenerator.WindowDays = 5`); el primero, al crear el contrato.
- Ninguna ruta cambia el monto de un cobro ya generado.
- Cambiar el precio es un `PUT /services/{id}` manual que exige `priceChangeReason` y deja una fila en
  `billing.service_price_changes`. Aplica **en el acto**: si el encargado lo hace un día tarde, el cobro ya salió con el precio
  viejo; si lo hace temprano, un cobro del año anterior todavía sin generar sale con el precio nuevo.
- Nadie avisa que se acerca el aniversario. El contrato no guarda una fecha de inicio (la importación de Excel sólo trae
  «Próximo vencimiento», que es `first_due_date`).

Lo que pide el owner: que los encargados reciban un aviso **10 días antes de que se genere el cobro** para que actualicen el
precio, y que, cuando el IPC nuevo todavía no se conoce, el cobro **espere** a que se conozca (así trabaja hoy) (owner).

**Éxito:** ningún cobro de un aniversario sale con el precio viejo por olvido, y ninguno sale antes de que el encargado decida.

## 2. Objetivos, no objetivos y anti-objetivos

**Objetivos**

1. Programar un cambio de precio que aplique desde un período concreto que todavía no se generó, sin tocar los anteriores.
2. Avisar al encargado del contrato 10 días antes de que se genere el cobro del período de revisión.
3. Retener el cobro de un contrato por IPC hasta que el encargado confirme el precio nuevo, sin mandarle nada al cliente.
4. Cargar el IPC del año una vez por tenant y confirmar en lote los contratos afectados.
5. Que un cobro retenido, al generarse tarde, le deje al cliente sus cinco días para pagar.

**No objetivos**

- Consultar el IPC a una fuente externa (DANE, Banco de la República). Lo escribe una persona.
- Editar el monto de un cobro ya generado (§11, P-1).
- Cobros de ajuste retroactivos: no existen (owner).
- Reajustes por otros índices (salario mínimo, UVT, IPC de otro país). El modo `Ipc` es el de Colombia.
- Cambiar el ciclo de un contrato.

**Anti-objetivos**

- **Aplicar porcentajes solos.** Ningún precio cambia sin que una persona lo confirme: el porcentaje varía y el alcance del
  contrato también (owner, enfoque C rechazado).
- **Retener cobros de contratos que hoy funcionan.** Desplegar esto no puede dejar ningún contrato existente o importado sin
  cobrar (§6.8).
- **Mover el calendario del contrato.** Retener un cobro no corre el período ni los cobros siguientes (owner).
- **Calcular en el navegador.** El precio calculado, los períodos, los estados y los conteos los entrega el backend (BFF).

## 3. Restricciones del código existente

- `ChargeGenerator.Generate` recorre las anclas desde `ServiceContract.NextDueDateToGenerate()` mientras
  `next <= today + 5`, y `MarkGenerated(next)` avanza `last_generated_due_date`. La misma regla corre al crear, al importar,
  en `GET /services/preview-charges` (en seco) y en la corrida diaria.
- **`charges.due_date` hace hoy dos trabajos:** es el período (el ancla del ciclo que el cobro paga) y el vencimiento que ve el
  cliente. El índice único `IX_charges_service_due (service_id, due_date)` es la deduplicación entre réplicas
  (`ServiceContractsReminderProcessor.GenerateServiceAsync`, `UniqueViolation.Is(exception, "IX_charges_service_due")`). La
  decisión 4 del owner obliga a separarlos (§6.2).
- La corrida diaria por tenant (`ServiceContractsReminderProcessor.ProcessTenantAsync`) va en orden: generar, mensaje del día a
  cada cliente, email diario a cada encargado (con el evento de campana `billing.charges.overdue-digest.v1` publicado al
  reclamar el digest), cierre de `Sending` vencidos. `billing.tenant_runs` y `billing.daily_digests` son las deduplicaciones.
- Encargado = `customer_profiles.assignee_member_id`; si ya no está activo o no puede gestionar, o el cliente no tiene perfil,
  lo suyo va a cada miembro con `billing.service.manage` (`IServiceContractsMemberDirectory.ListManagersAsync`).
- El redondeo de precios por moneda ya existe por tenant: `ITenantPriceRounding.GetAsync(tenantId, currency)` en
  `Modules.Tenancy.Application`, que `Modules.ServiceContracts.Application` ya puede referenciar.
- La campana: `InAppNotificationKind` viaja y se guarda por nombre (`kind` `varchar(64)`, `NotificationsDbContext.cs:70`); el
  consumidor `BillingInAppConsumer` deduplica por día con `dedupe_key`.
- Opcionales del DTO viajan como `null`, nunca ausentes (spec de cobros §4).

## 4. Enfoques

**Cómo se cambia el precio**

| Enfoque | Qué es | Por qué sí o no |
| --- | --- | --- |
| A. Sólo recordatorio, edición manual | Avisar antes del aniversario y que el encargado use el `PUT` de hoy | Barato. Pero el `PUT` aplica en el acto: un cambio a mitad de año depende de en qué día se haga respecto a la generación, y no hay forma de dejar decidido el precio de un período futuro. |
| **B. Recordatorio + cambio programado** | Avisar, y que el encargado programe el precio desde un período que no se generó | **Elegido (owner).** El cambio queda atado al período, no al día en que alguien lo escribió. |
| C. Regla automática de porcentaje | El contrato sube solo un % cada año | Rechazado (owner): el porcentaje varía cada año y el alcance del contrato cambia. |

**Qué pasa cuando el IPC nuevo todavía no se conoce**

| Opción | Por qué sí o no |
| --- | --- |
| **Retener el cobro** hasta confirmar el precio | **Elegida (owner):** así trabaja hoy el owner. El cliente no recibe nada mientras tanto. |
| Cobrar con el precio viejo y cobrar la diferencia después | Rechazada: exige cobros de ajuste, y el owner no quiere retroactivos. |
| Cobrar con el precio viejo y perder la diferencia | Rechazada: el tenant pierde plata por un calendario que no controla. |

## 5. Conceptos

- **Período** (`period_date`): el ancla del ciclo que paga un cobro, `firstDueDate + k × cycleMonths` con la regla de fin de
  mes de hoy (`BillingCycles`). Inmutable y único por contrato.
- **Vencimiento** (`due_date`): el día que ve el cliente. Igual al período, salvo en un cobro liberado de una retención (§6.5).
- **Fecha de generación** de un período: `period_date − 5`, el día en que la ventana de hoy lo alcanza.
- **Próxima revisión** (`next_review_date`): fecha por contrato desde la cual se revisa el precio **(propuesta mostrada al
  owner)**. El **período de revisión** es el primer período sin generar con `period_date >= next_review_date` (respetando el
  salto de la pausa que ya hace `NextDueDateToGenerate`).
- **Inicio del aviso:** `periodo_de_revision − 15` (= fecha de generación − 10) (owner).
- **Modo de reajuste** (`adjustment_mode`): `Ipc` o `Manual` por contrato **(propuesta mostrada al owner)**.
- **Puntos adicionales** (`ipc_extra_points`): sólo con `Ipc`; se suman al IPC (owner):
  `nuevo = precio × (1 + (ipc + puntos) / 100)`.
- **Cambio programado:** un precio nuevo que aplica desde el primer período generado con `period_date >= effective_from`. Uno
  pendiente por contrato **(propuesta mostrada al owner)**.
- **Cubrir la revisión:** un cambio programado cubre la revisión si el primer período con `period_date >= effective_from` es
  exactamente el período de revisión.
- **Invariante `Ipc` (decisión del diseño, revisable):** en un contrato `Ipc`, un cambio programado pendiente nunca aplica a un
  período posterior al período de revisión. Si existiera, la revisión retendría antes de llegar a él y el cambio quedaría
  colgado. Se rechaza con 422 `billing.price_schedule.after_review` todo lo que lo crearía: programar un cambio así, o un
  `PUT …/price-review` que deje uno pendiente así (pasar a `Ipc`, o adelantar `next_review_date`). En `Manual` se permite: la
  revisión genera «sin revisar» y el cambio aplica después.
- **Retención:** un contrato `Ipc` cuyo período de revisión entró en la ventana de generación sin estar cubierto. No se genera
  ese cobro ni los siguientes **(propuesta mostrada al owner)**.
- **Avanzar la revisión** (`AdvanceReview(resuelto)`): `next_review_date += 12 × k meses`, con el menor `k >= 1` que deja
  `next_review_date > resuelto` (estrictamente posterior). `AddMonths` sobre `next_review_date` conserva el día que eligió la
  persona (29 feb → 28 feb). Es **una sola regla de dominio** que usan todos los caminos que resuelven una revisión: aplicar un
  cambio programado (resuelto = el período donde aplicó), generar «sin revisar» (resuelto = ese período), «Mantener precio»
  (resuelto = el período de revisión vigente). Un `PUT …/price-review` no la usa: ahí la fecha la escribe la persona (y pasar a
  `Manual` un contrato retenido resuelve por «sin revisar», §6.4). Tras una pausa larga hay **una** revisión para lo que se genera después de reanudar, no una por cada año saltado, y nunca se componen varios IPC en una revisión: cada revisión usa el IPC de un solo año (§6.7). Si el contrato estaba retenido al pausar, los períodos que arrastra conservan su propia revisión (§6.4); al liberarlos, `AdvanceReview` puede dejar la siguiente revisión en el primer período posterior a la pausa, que se retiene de nuevo **(decisión del diseño, revisable)**.

## 6. Cómo funciona

### 6.1 Estados de revisión de un contrato (los calcula el backend)

Para un contrato `Active` (un `Paused` o `Cancelled` es `None`):

| `state` | Cuándo |
| --- | --- |
| `Held` | `held_since` no es nulo (§6.4). |
| `Scheduled` | Hay un cambio programado pendiente que cubre la revisión. |
| `Due` | `hoy >= periodo_de_revision − 15` y ninguno de los anteriores. |
| `Upcoming` | Lo demás. |

Las acciones que «resuelven» la revisión son dos: **programar** un cambio que la cubra, o **mantener el precio**. Editar el
reajuste (`PUT …/price-review`) puede además mover la revisión o, en un retenido, liberarlo (§6.4). Editar el
precio en el acto con el `PUT` de hoy **no** la resuelve: sigue disponible y sigue aplicando en el acto **(decisión del diseño,
revisable)**. Motivo: el `PUT` no sabe a qué período se refiere, y es justo la ambigüedad del enfoque A.

### 6.2 Período y vencimiento por separado (decisión 4 del owner)

**Decisión del diseño (revisable):**

- `billing.charges` gana `period_date date not null`. La migración lo rellena con `due_date` (hoy son iguales en toda fila).
- El índice único pasa a `IX_charges_service_period (service_id, period_date)`; `IX_charges_service_due` se borra (sin
  reemplazo: dos cobros de un contrato pueden vencer el mismo día, §6.5). La referencia en
  `GenerateServiceAsync` cambia de nombre.
- `ServiceContract.LastGeneratedDueDate` pasa a llamarse `LastGeneratedPeriodDate` en el dominio y sigue mapeado a la columna
  `last_generated_due_date` (no hay datos que migrar: siempre guardó el período). `MarkGenerated` recibe el período.
- `Charge.Generate(service, periodDate, dueDate, priceUnreviewed, now)`.

**Consumidores de `due_date` revisados** (todos siguen leyendo el vencimiento, que es lo que deben leer):

| Consumidor | Lee | Cambio |
| --- | --- | --- |
| `ReminderCalendar.StepDueOn` / `DateOf`, `CustomerMessagePlanner` | vencimiento | Ninguno. Un cobro liberado vence en `hoy + 5`, así que su `before_5` cae el día en que nace. `reminder_steps` es por `(charge_id, step)` y el vencimiento de un cobro nunca cambia después de generarse: no hay pasos huérfanos. |
| `Charge.IsOverdue`, `ChargeRepository` (vencidos, `due_date < today`), `daysOverdue` | vencimiento | Ninguno. Un cobro retenido no existe, así que no está vencido. |
| `ChargeRepository` (orden de listas), `ServiceDetailComposer` (`nextCharge`) | vencimiento | Ninguno. |
| Email diario (`horizon`, secciones) y mensajes al cliente (líneas, `NearestDueDate`) | vencimiento | Ninguno en lo que ya muestran; el digest suma secciones (§6.6). |
| `CopilotFinanceTools` (cita «servicio · fecha», `nextDueDate`) | vencimiento | Ninguno. |
| `ChargeGenerator`, `NextDueDateToGenerate`, `BillingCycles` | período | Pasan a hablar de período (renombre, misma aritmética). |
| `PreviewServiceCharges` | período (cuenta cobros) | §6.9. |
| Índice `(tenant_id, status, due_date)` | vencimiento | Se queda. |

`ChargeDto` gana `periodDate` y `priceUnreviewed` (§8).

### 6.3 Generación

`ChargeGenerator.Generate` recibe además el cambio programado pendiente (o null; su precio ya viene redondeado) y un **modo**: `Creation` (crear, importar, vista previa) o `Run` (corrida diaria y liberación
desde una acción del usuario). Para cada período `p` sin generar con `p <= hoy + 5` (o el primero, como hoy):

1. **Cambio programado.** Si hay uno pendiente y `p >= effective_from`: se aplica. El contrato toma `new_price` (con
   `ServicePriceChange.Record`, motivo y miembro del cambio programado, `origin = Scheduled` o `Ipc`), el cambio pasa a
   `Applied` con `applied_period_date = p` y `applied_at`. Si `p` es el período de revisión o posterior, la revisión se
   resuelve: `AdvanceReview(p)` (§5).
2. **Período de revisión** (`p >= next_review_date`) en modo `Run`, sin cambio que lo cubra:
   - `Ipc`: **retiene** y se corta el recorrido (los períodos siguientes esperan, **propuesta mostrada al owner**). Nada se
     genera de ahí en adelante, nada se le manda al cliente (owner).
   - `Manual`: genera con el precio vigente y `price_unreviewed = true` («sin revisar») y `AdvanceReview(p)` **(propuesta
     mostrada al owner)**.
3. **Vencimiento:** si el contrato estaba retenido al **empezar** la pasada (`held_since` no nulo), todo período generado en
   esta pasada vence en `max(p, hoy + 5)`; si no, en `p` (owner, decisión 4).
4. Genera el cobro, `MarkGenerated(p)`.

**`held_since` al terminar la pasada (decisión del diseño, revisable):**

- La pasada **no** terminó en retención ⇒ `held_since = null`.
- Terminó en retención y no generó nada ⇒ `held_since` se conserva, o vale `hoy` si era nulo (retención nueva).
- Terminó en retención después de generar algo (liberó períodos y llegó a otra revisión dentro de la ventana) ⇒ `held_since =
  hoy`: es una retención nueva. Los períodos liberados en esa misma pasada ya usaron `max(p, hoy + 5)` por el paso 3.

En modo `Creation` el paso 2 no aplica: crear o importar nunca retiene ni marca «sin revisar», porque el precio que se escribe
es el de hoy (§6.8).

### 6.4 Retención

- Sólo contratos `Active` en modo `Ipc`. La retiene la corrida diaria en el paso de generación; escribe `held_since` y sube la
  versión (`BumpVersionAfterGeneration`), como hoy al generar.
- **Se libera** con:
  - un cambio programado que la cubra (incluida la confirmación de IPC, §6.7);
  - «Mantener precio» (`AdvanceReview(periodo_de_revision)`);
  - `PUT …/price-review` que pasa el contrato a `Manual` (el período retenido se genera «sin revisar»);
  - `PUT …/price-review` que lo deja en `Ipc` pero corre `next_review_date` más allá del período retenido. Ese período deja de
    ser de revisión y se genera con el precio vigente, **sin** la marca «sin revisar», porque una persona decidió **(decisión
    del diseño, revisable)**.

  Cada acción que libera corre `ChargeGenerator` en modo `Run` **en la misma transacción** con el `hoy` del tenant, así que los
  cobros nacen al confirmar, no al día siguiente, y `held_since` queda según §6.3 **(decisión del diseño, revisable)**.
- **Pausar y reanudar un retenido (owner, 2026-11-25).** Reanudar conserva los períodos que ya estaban retenidos antes de la pausa: siguen retenidos hasta que se decida el precio y entonces se cobran como cobros liberados. Los períodos que caen durante la pausa nunca se cobran, como hoy.
  - **Qué estaba retenido (decisión del diseño, revisable):** si el contrato estaba retenido al pausar (`held_since` no nulo), todo período sin generar que el cursor alcanza con fecha de generación (`p − 5`) igual o anterior al día de la pausa (hoy del tenant): el período de revisión y los siguientes de la ventana. Cuenta también el período cuya ventana se abrió ese día aunque la corrida diaria todavía no haya pasado: es lo mismo que cobraría una liberación ese día, y no depende de la hora de la corrida. Un contrato que no estaba retenido no arrastra nada, aunque su período de revisión haya entrado en la ventana ese mismo día.
  - **Pausar** guarda esos períodos en `carried_held_periods` (sin repetir los que ya estaban) y borra `held_since`; no hay generación en pausa.
  - **Reanudar** escribe `reactivated_on` como hoy. Si hay períodos arrastrados, pone `held_since = hoy` (lo retenido sigue retenido) y corre `ChargeGenerator` en modo `Run` en la misma transacción: en `Ipc` sigue retenido; si mientras estaba pausado se pasó a `Manual` o se corrió `next_review_date` más allá de ellos, se liberan en el acto (§6.3, §6.5).
  - **Cursor:** el próximo período a generar es el primer período arrastrado; agotados, sigue la regla de hoy (la primera ancla igual o posterior a `reactivated_on`). `MarkGenerated(p)` saca `p` de la lista. Varias pausas seguidas con el contrato retenido acumulan sus huecos en la misma lista.
  - El período de revisión, `upcomingPeriodDates`, «cubrir la revisión» y la invariante `Ipc` (§5) recorren ese mismo cursor, así que nunca apuntan a un período de la pausa. En el DTO, `heldSince` es el día de la reanudación y `reviewPeriodDate` el período arrastrado más antiguo.
  - Un contrato que no estaba retenido al pausar sigue igual que hoy: reanudar no hace nada especial; si el período de revisión quedó atrás durante la pausa, la revisión aplica al primer período generado después de reanudar, y al resolverse `AdvanceReview` la deja después de ese período (§5) **(decisión del diseño, revisable)**.
- **Pausado:** programar un cambio y «Mantener precio» exigen un contrato `Active` (422 `billing.service.not_active`);
  `upcomingPeriodDates` es `[]`. Editar el reajuste (`PUT …/price-review`) sí se permite **(decisión del diseño, revisable)**.
- **Cancelar** borra `held_since` y `carried_held_periods` (lo retenido no se cobra) y cancela el cambio programado pendiente (`status = Cancelled`).
- `DELETE /services/{id}` (existe hoy) borra también sus cambios programados.

### 6.5 Vencimiento de un cobro liberado (owner, decisión 4)

Un cobro que estuvo retenido nace con `due_date = max(period_date, fecha de liberación + 5)`. El período y los cobros
siguientes no se mueven: el siguiente período sigue en su ancla. Si la retención duró más de un período, todos los liberados
juntos pueden vencer el mismo día: por eso el único deja de ser por `due_date`. No hay cobro de ajuste.

Ejemplo: contrato mensual, ancla día 10, revisión 2027-01-10, IPC confirmado el 2027-01-14. Nace el cobro de período
2027-01-10 con vencimiento 2027-01-19. El de período 2027-02-10 vence el 2027-02-10.

### 6.6 Avisos al encargado

**Propuesta mostrada al owner:** campana y email al encargado; se repiten mientras haya cobros retenidos; lo retenido se ve en
pantalla. Concreción **(decisión del diseño, revisable)**:

- **Qué entra:** contratos `Active` en estado `Due` o `Held` (§6.1), de cualquier modo. `Scheduled` no pide nada.
- **Email:** el email diario que ya existe (`SendDigestsAsync`) suma dos secciones: «Precios por revisar» (`Due`: cliente,
  contrato, precio actual, período de revisión, fecha de generación, y para `Ipc` el año de IPC y si falta cargarlo) y «Cobros
  retenidos» (`Held`: lo mismo más `heldSince`). El digest se manda si hay cobros de interés **o** revisiones; la fila de
  `daily_digests` sigue siendo la deduplicación. Mismo destinatario que los cobros: el encargado del perfil del cliente, con el
  respaldo a todos los gestores. Un contrato cuyo cliente no tiene perfil va al respaldo.
- **Campana:** al reclamar el digest, si el miembro tiene revisiones, se publica
  `billing.price-reviews.digest.v1` (nuevo) con `{ tenantId, memberId, runDate, dueCount, heldCount, occurredAt }`. Nuevo
  `InAppNotificationKind.ServicePriceReviewsDue`, `data = { dueCount, heldCount, runDate }`, `dedupe_key =
  "billing-price-review:" + runDate`, en `BillingInAppConsumer` (que suma el evento a `EventNames`). Una entrada por miembro y
  día mientras haya algo, como la de vencidos.
- **Frecuencia:** diaria desde el inicio del aviso hasta que se resuelva. El primer día es el día 10 antes de la generación.

`ServiceContractsDigestEmail` y `BillingDigestEmailTemplate` ganan las filas nuevas
(`ServiceContractsPriceReviewRow { customerName, serviceName, currentPrice, currency, reviewPeriodDate, generationDate, state,
ipcYear: int?, ipcMissing: bool, heldSince: date? }`).

### 6.7 IPC por tenant y confirmación en lote

**Propuesta mostrada al owner:** el IPC se escribe una vez por tenant y año; luego una lista de contratos afectados muestra el
precio actual y el calculado, con selección y confirmación en lote que genera los retenidos; lo no seleccionado sigue
retenido.

- **Qué año aplica (decisión del diseño, revisable):** `ipc_year = periodo_de_revision.Year − 1`. Es el IPC del año calendario
  anterior, el que se conoce desde comienzos de enero. Una revisión de enero o comienzos de febrero puede caer antes de que se
  publique: se retiene y espera, que es lo que pidió el owner. **Las cláusulas de los contratos pueden decir otra cosa** (por
  ejemplo, IPC de los últimos 12 meses al aniversario): §11, P-2.
- `billing.ipc_rates (tenant_id, year)`: el porcentaje con dos decimales (`5.20` es 5,20 %). Rango `-10.00` a `100.00`.
  Guardar una tasa avisa y nunca pisa en silencio la de otra persona: `expectedRate` y `409 billing.ipc.rate_changed`
  (§6.11, owner 2026-11-24).
- **En uso:** un año está en uso mientras algún cambio programado **`Pending`** lo use. Uno en uso no se edita (422
  `billing.ipc.in_use`). Los `Applied` no cuentan: ya copiaron `ipc_rate` y editar la tasa no los toca **(decisión del diseño,
  revisable)**. Un IPC mal escrito se corrige cancelando los cambios programados pendientes que lo usan, editando la tasa y
  volviendo a confirmar; lo ya aplicado y generado no se reescribe (§6.10). El chequeo de uso no compite con la confirmación:
  ambos pasan por un candado sobre la fila de la tasa (§6.11).
- **Confirmación:** por cada contrato elegido se crea un cambio programado `source = Ipc` con `effective_from =
  periodo_de_revision`, `ipc_year`, `ipc_rate`, `extra_points` y `new_price` (el calculado, o el que escribió la persona), y se
  corre la generación en modo `Run` si el período ya está en la ventana. Cada contrato va en **su propia transacción**, con su
  `version`; la respuesta dice el resultado de cada uno (§8) **(decisión del diseño, revisable)**: un contrato con la versión
  vieja no tumba a los demás.
- **Precio esperado (decisión del diseño, revisable):** cada ítem trae `expectedPrice`, el precio calculado que la persona vio.
  El backend vuelve a calcular; si le da otro (cambió la tasa, los puntos, el precio del contrato o la regla de redondeo), el
  ítem falla con `billing.price_review.price_changed` y no se toca nada de ese contrato. Vale también cuando la persona escribió
  otro precio: se compara `expectedPrice` con el calculado, no con el escrito.
- **Motivo automático:** `"IPC {año} ({rate} %)"` y, con puntos, `" + {puntos} puntos"`. Si la persona cambia el precio
  calculado, el motivo es obligatorio y es el suyo.
- **Candidatos:** contratos `Active`, modo `Ipc`, estado `Due` o `Held`, con ese `ipc_year`. Un contrato `Ipc` en `Upcoming`
  todavía no aparece.

### 6.8 Contratos existentes, importados y nuevos (decisión del diseño, revisable)

- **Existentes** (migración): `adjustment_mode = Manual`, `ipc_extra_points = 0`, `next_review_date` = el primer aniversario
  `first_due_date + 12k` (k ≥ 1) que sea `>= fecha de la migración + 15 días` (fecha UTC de la migración). Un contrato
  `Manual` nunca retiene: el peor caso es un aviso y un cobro «sin revisar». Ninguno queda sin cobrar.
- **Importados** (Excel): igual, sin columnas nuevas en la plantilla: modo `Manual`, puntos 0, `next_review_date` por la regla
  de creación de abajo. Se pasan a `Ipc` desde la pantalla.
- **Nuevos** (`POST /services`): `priceReview` opcional en el cuerpo. Ausente o `null` ⇒ `Manual`, puntos 0, revisión por
  defecto (así un frontend viejo no cambia de comportamiento). El frontend propone `Ipc` (ver su documento).
- **Revisión por defecto al crear o importar:** el primer aniversario `first_due_date + 12k` (k ≥ 1) posterior al último
  período generado en la creación. Con `first_due_date` futura es `first_due_date + 12 meses` (la propuesta mostrada al owner);
  con una fecha pasada (una migración desde Excel que genera varios períodos) es el aniversario siguiente al último generado.
- Una `nextReviewDate` explícita debe ser posterior al último período generado (en la creación, el último que esa creación
  genera) → si no, 422 `billing.service.review_date_invalid`.

### 6.9 Vista previa de cobros

`GET /services/preview-charges` sigue contando lo mismo: en modo `Creation` nunca hay retención. Gana
`defaultNextReviewDate` (la regla de §6.8 con lo que generaría), para que el formulario la proponga sin contar anclas.

### 6.10 IPC equivocado después de generar (decisión del diseño, revisable)

Lo ya generado no se corrige con esta feature: ninguna ruta cambia el monto de un cobro (restricción del código). Lo que sí se
puede: cancelar los cambios programados pendientes, editar el IPC (si ya no está en uso) y volver a confirmar; y, para un
contrato ya aplicado, un `PUT` con el precio correcto rige desde el siguiente cobro. Si el owner necesita corregir el cobro
mismo, es §11, P-1.

### 6.11 Concurrencia e idempotencia entre réplicas

- Generación: igual que hoy. El único `(service_id, period_date)` deduplica; una violación o un 412 del token de versión hacen
  releer y reintentar una vez (`GenerateServiceAsync`). Retener escribe `held_since` con la misma transacción y la misma subida
  de versión: dos réplicas que retienen a la vez chocan por versión y la segunda relee.
- Aplicar un cambio programado ocurre dentro de la generación, en la misma transacción que el cobro: si la transacción pierde,
  el cambio sigue `Pending` y el cobro no existe.
- Un cambio programado pendiente por contrato: índice único parcial `(service_id) WHERE status = 'Pending'`. Dos «programar» a
  la vez: uno gana; el otro, con la versión vieja, recibe 412.
- Toda acción sobre un contrato lleva `If-Match` con su versión (§8). La confirmación en lote, `version` por ítem.
- `PUT /ipc-rates/{year}` **nunca pisa en silencio el valor de otra persona (owner, 2026-11-24).** El cuerpo trae
  `expectedRate`, la tasa que mostraba la pantalla (`null` si no mostraba ninguna). Si lo guardado difiere de `expectedRate`
  (incluido que ya exista una tasa cuando se esperaba `null`, o que ya no exista cuando se esperaba una), responde
  `409 billing.ipc.rate_changed` y no escribe nada. Guardar el mismo valor que ya está guardado responde 200 sin cambios,
  aunque `expectedRate` no coincida. Dos primeras escrituras simultáneas del mismo año siguen respondiendo
  `409 billing.concurrent_first_write` a la que pierde el insert (existe, `ServiceContractsUnitOfWork.cs:62`). No hay «última
  escritura gana».
- **Guardar la tasa y confirmar con ella se excluyen** con un candado de fila sobre `billing.ipc_rates`, en una transacción
  explícita `READ COMMITTED` por cada ítem de la confirmación y por cada `PUT`; la tasa y los datos del contrato que deciden el
  resultado se leen después de tomarlo (el redondeo del tenant y «hoy» se leen una vez por lote). La confirmación lo toma compartido (`FOR SHARE`): los ítems de un mismo año no se esperan ni se tumban entre sí.
  El `PUT` lo toma exclusivo (`FOR UPDATE`): mientras un ítem tiene la tasa, espera y después ve el cambio programado nuevo
  (`billing.ipc.in_use`); mientras el `PUT` la tiene, el ítem espera y lee la tasa nueva
  (`billing.price_review.price_changed`). Una primera tasa no tiene fila que bloquear: ningún ítem puede confirmar con ella y
  dos primeras escrituras las separa la clave primaria. La confirmación en lote es el único camino que crea un cambio
  programado a partir de la tasa: «programar» es manual y «Mantener precio» y editar el reajuste no la leen.
- Avisos: el digest diario ya es idempotente por `(member, today)`; la campana por `dedupe_key` del día.

### 6.12 Interacciones con lo que existe

- **`PUT /services/{id}`** sigue igual. Cambiar la **moneda** con un cambio programado pendiente → 422
  `billing.price_schedule.currency_locked` (el programado está en la moneda vieja). Cambiar sólo el precio se permite; el
  programado, al aplicar, lo reemplaza.
- **Cambios programados sólo de precio**, en la moneda del contrato **(decisión del diseño, revisable)**.
- `billing.service_price_changes` gana `origin varchar(16)` (`Edit` | `Scheduled` | `Ipc`; la migración pone `Edit`) y
  `schedule_id uuid null`.

### 6.13 Permisos (decisión del diseño, revisable)

Sin permisos nuevos: leer revisiones, IPC y cambios programados es `billing.service.read`; escribir (programar, cancelar,
mantener, editar el reajuste, cargar IPC, confirmar) es `billing.service.manage`. Mismo criterio de la spec de cobros: un solo
permiso de escritura que incluye la configuración. Agregar uno nuevo obliga a tocar roles y editor de roles: §11, P-4.

### 6.14 Auditoría (`IAuditRecorder`, misma transacción)

| Acción | Recurso | `changedFields` |
| --- | --- | --- |
| `billing.service.price_review_updated` | `service` | los de `adjustmentMode`, `ipcExtraPoints`, `nextReviewDate` que cambiaron |
| `billing.service.price_scheduled` | `service` | `["newPrice","effectiveFrom","reason"]` (+ `"ipcYear","ipcRate","extraPoints"` si es IPC) |
| `billing.service.price_schedule_cancelled` | `service` | `["scheduleId"]` |
| `billing.service.price_kept` | `service` | `["nextReviewDate"]` |
| `billing.ipc.saved` | `ipc_rate` | `["year","rate"]` |

La aplicación del cambio programado en la corrida diaria no audita: queda en `service_price_changes` con su origen, motivo y
quien lo programó (la generación tampoco audita hoy).

## 7. Modelo de datos (migración en `ServiceContracts`)

### `billing.services` (columnas nuevas)

| Columna | Tipo | Notas |
| --- | --- | --- |
| `adjustment_mode` | varchar(16) not null | CHECK `IN ('Manual','Ipc')`. Relleno `Manual`. |
| `ipc_extra_points` | numeric(5,2) not null default 0 | CHECK `>= 0 AND <= 50`. Sólo tiene efecto con `Ipc`; con `Manual` se guarda 0. |
| `next_review_date` | date not null | Relleno §6.8. |
| `held_since` | date null | §6.4. |
| `carried_held_periods` | date[] not null default `'{}'` | Períodos retenidos antes de una pausa que siguen pendientes (§6.4). Siempre posteriores a `last_generated_due_date`, en orden. |

### `billing.charges` (columnas nuevas)

| Columna | Tipo | Notas |
| --- | --- | --- |
| `period_date` | date not null | Relleno `= due_date`. Único `IX_charges_service_period (service_id, period_date)`. |
| `price_unreviewed` | boolean not null default false | §6.3 paso 2, `Manual`. |

Se borra `IX_charges_service_due`.

### `billing.service_price_schedules` (nueva)

| Columna | Tipo | Notas |
| --- | --- | --- |
| `id` | uuid v7 | PK |
| `tenant_id`, `service_id` | uuid | Único parcial `(service_id) WHERE status = 'Pending'`. Índice `(tenant_id, source, ipc_year)`. |
| `effective_from` | date | Aplica al primer período generado `>=`. |
| `new_price` | numeric(18,2) | `> 0`, ya redondeado. |
| `currency` | varchar(3) | La del contrato al programar. |
| `source` | varchar(16) | CHECK `IN ('Manual','Ipc')`. |
| `ipc_year` | smallint null | Con `Ipc`. |
| `ipc_rate`, `extra_points` | numeric(6,2), numeric(5,2) null | Copiados al confirmar. |
| `reason` | varchar(500) | Trim, 1 a 500. |
| `created_by_member_id` | uuid null | Como `changed_by_member_id`. |
| `status` | varchar(16) | CHECK `IN ('Pending','Applied','Cancelled')`. |
| `applied_period_date` | date null | |
| `created_at`, `applied_at`, `cancelled_at` | timestamptz | los dos últimos null |

### `billing.ipc_rates` (nueva)

PK `(tenant_id, year)`; `year smallint` (2000 a año actual + 1), `rate numeric(6,2)`, `updated_by_member_id uuid null`,
`updated_at timestamptz`, `version bigint`.

### `billing.service_price_changes` (columnas nuevas)

`origin varchar(16) not null` (relleno `Edit`, CHECK `IN ('Edit','Scheduled','Ipc')`), `schedule_id uuid null`.

## 8. Contrato HTTP

Todas bajo `/api/v1/tenants/{tenantId}/billing`. Errores como la spec de cobros §1: `403 authorization.denied`, `404` con código
de recurso, `412 concurrency.conflict`, `428` sin `If-Match`, `422 validation.failed` con `errors` en PascalCase.

### Tipos nuevos y campos nuevos

```
AdjustmentMode   = "Manual" | "Ipc"
ReviewState      = "None" | "Upcoming" | "Due" | "Held" | "Scheduled"

PriceScheduleDto { id, effectiveFrom: date, appliesToPeriodDate: date, newPrice: decimal, currency: string,
                   source: "Manual"|"Ipc", ipcYear: int|null, ipcRate: decimal|null, extraPoints: decimal|null,
                   reason: string, createdBy: MemberRef|null, createdAt: iso }
PriceReviewInfoDto { adjustmentMode: AdjustmentMode, ipcExtraPoints: decimal, nextReviewDate: date,
                     reviewPeriodDate: date|null (null si no está Active), generationDate: date|null, noticeFrom: date|null,
                     state: ReviewState, heldSince: date|null, ipcYear: int|null (sólo Ipc),
                     scheduled: PriceScheduleDto|null }

ChargeDto         + periodDate: date, priceUnreviewed: bool
ServiceDto        + priceReview: PriceReviewInfoDto
ServiceDetailDto  + upcomingPeriodDates: date[]   (los próximos 12 períodos sin generar, para elegir desde cuándo programar;
                                                   [] si el contrato no está Active. En Ipc, sólo hasta el período de
                                                   revisión inclusive: lo posterior violaría la invariante de §5)
ServicePriceChangeDto + origin: "Edit"|"Scheduled"|"Ipc"
ChargePageDto.summary + priceReviews: { dueCount: int, heldCount: int }   (del tenant entero, como el resto del summary)
ChargesPreviewDto + defaultNextReviewDate: date

PriceReviewRowDto { service: { id, name, customer: CustomerRef, price: decimal, currency, cycle: Cycle, version: long },
                    assignee: MemberRef|null, review: PriceReviewInfoDto,
                    ipcRate: decimal|null (el del año, si está cargado), computedPrice: decimal|null (Ipc con IPC cargado) }
IpcRateDto        { year: int, rate: decimal, inUse: bool, updatedBy: MemberRef|null, updatedAt: iso, version: long }
```

`computedPrice` = `price × (1 + (ipcRate + ipcExtraPoints) / 100)` redondeado con `ITenantPriceRounding.GetAsync(tenant,
currency)` (**decisión del diseño, revisable**: la misma regla de precios que el tenant ya eligió por moneda; sin fila, los
decimales del catálogo al más cercano). Un resultado `<= 0` hace `computedPrice = null`.

### Rutas nuevas

| Método y ruta | Permiso | Respuesta | Errores |
| --- | --- | --- | --- |
| `GET /price-reviews?state=due\|held\|all&assignee=me&search=&page=1&pageSize=25` | read | `200 { items: PriceReviewRowDto[], total, page, pageSize, summary: { dueCount, heldCount }, ipcYears: [{ year: int, rate: decimal\|null, contractCount: int }] }`. Default `all` = `Due` + `Held`. Orden por `generationDate` asc. `ipcYears`: los años de IPC que necesitan los contratos `Ipc` en `Due` o `Held` del tenant entero (**sin** los filtros ni la página), con su tasa si está cargada y cuántos contratos la esperan. `summary` es del tenant entero. `assignee` y `search` como `GET /charges`. | 403; 422 `validation.failed` (`State`, `PageSize` > 100) |
| `GET /ipc-rates` | read | `200 { items: IpcRateDto[] }`, por `year` desc | 403 |
| `PUT /ipc-rates/{year}` body `{ rate: decimal, expectedRate: decimal\|null }` (`expectedRate` obligatorio, puede ser `null`) | manage | `200 IpcRateDto` (upsert). `expectedRate` es la tasa que mostraba la pantalla; si lo guardado difiere, 409 sin escribir; la misma tasa ya guardada responde 200 sin cambios (§6.11, owner 2026-11-24). Orden: `rate_changed` antes que `in_use`. | 403; 409 `billing.ipc.rate_changed` (lo guardado no es `expectedRate`), `billing.concurrent_first_write` (insert simultáneo del mismo año); 422 `billing.ipc.in_use` (cambia la tasa de un año que usa algún cambio programado `Pending`; la misma tasa no es cambio), `validation.failed` (`Year` fuera de 2000…año actual + 1, `Rate` fuera de −10…100 o con más de 2 decimales, `ExpectedRate` ausente del cuerpo) |
| `POST /price-reviews/confirm-ipc` body `{ year: int, items: [{ serviceId: guid, version: long, expectedPrice: decimal, newPrice: decimal\|null, reason: string\|null }] }` (1 a 200 ítems, sin repetir `serviceId`) | manage | `200 { results: [{ serviceId, outcome: "Confirmed"\|"Failed", code: string\|null, service: ServiceDto\|null }] }` en el orden pedido. `expectedPrice` es el `computedPrice` que vio la persona; si el backend calcula otro, el ítem falla con `billing.price_review.price_changed` y nada cambia para ese contrato (§6.7). `newPrice` null = el calculado; distinto del calculado exige `reason`. Cada ítem en su transacción. | 403; 422 `billing.ipc.not_set` (el año no tiene tasa), `validation.failed` (`Items` vacío, > 200 o repetido; `Items[i].ExpectedPrice` ausente; `Items[i].NewPrice` <= 0 o > 2 decimales; `Items[i].Reason` > 500). Por ítem (`code`): `billing.service.not_found`, `concurrency.conflict`, `billing.price_review.price_changed`, `billing.price_review.not_ipc`, `billing.price_review.year_mismatch`, `billing.price_review.not_pending` (no está `Due` ni `Held`, o ya está cubierta), `billing.price_schedule.already_pending` (está por revisar pero tiene un cambio pendiente que no cubre la revisión: primero se aplica o se cancela), `billing.service.price_reason_required` |
| `PUT /services/{id}/price-review` body `{ adjustmentMode, ipcExtraPoints: decimal, nextReviewDate: date }`, `If-Match` | manage | `200 ServiceDetailDto`. Se permite con el contrato `Active` o `Paused`. Pasar a `Manual` guarda puntos 0. Con el contrato retenido, libera en la misma transacción si pasa a `Manual` (genera «sin revisar») o si sigue en `Ipc` y corre `nextReviewDate` más allá del período retenido (genera con el precio vigente) (§6.4). | 403; 404; 412; 428; 422 `billing.service.cancelled`, `billing.service.review_date_invalid` (no posterior al último período generado), `billing.price_schedule.after_review` (dejaría en `Ipc` un cambio pendiente posterior al período de revisión, §5), `validation.failed` (`AdjustmentMode`, `IpcExtraPoints` fuera de 0…50 o > 2 decimales) |
| `POST /services/{id}/price-schedule` body `{ newPrice: decimal, effectiveFrom: date\|null, reason: string }`, `If-Match` | manage | `200 ServiceDetailDto`. `effectiveFrom` null = el período de revisión. `source = Manual`. Si el período al que aplica ya está en la ventana (o el contrato está retenido y lo cubre), genera en la misma transacción. Sólo con el contrato `Active`. | 403; 404; 412; 428; 422 `billing.service.cancelled`, `billing.service.not_active` (pausado), `billing.price_schedule.after_review` (en `Ipc`, aplicaría a un período posterior al de revisión, §5), `billing.price_schedule.already_pending`, `billing.price_schedule.period_generated` (`effectiveFrom` <= último período generado), `billing.price_schedule.same_price` (igual al precio actual: para eso está «Mantener precio»), `billing.service.price_reason_required`, `validation.failed` (`NewPrice`, `Reason` > 500) |
| `DELETE /services/{id}/price-schedule`, `If-Match` | manage | `200 ServiceDetailDto` (el cambio pasa a `Cancelled`) | 403; 404 `billing.service.not_found`, `billing.price_schedule.not_found`; 412; 428 |
| `POST /services/{id}/keep-price`, sin cuerpo, `If-Match` | manage | `200 ServiceDetailDto`. `AdvanceReview(periodo_de_revision)` (§5); si estaba retenido, libera y genera en la misma transacción. Sólo con el contrato `Active` y sin cambio programado pendiente. Sin motivo: no se guardaría en ningún lado (la auditoría registra la fecha nueva) **(decisión del diseño, revisable)**. | 403; 404; 412; 428; 422 `billing.service.cancelled`, `billing.service.not_active`, `billing.price_schedule.already_pending` (cancelar primero) |

### Rutas existentes que cambian

| Ruta | Cambio |
| --- | --- |
| `POST /services` | Cuerpo + `priceReview: { adjustmentMode, ipcExtraPoints, nextReviewDate: date\|null } \| null` (§6.8). Errores + `billing.service.review_date_invalid`, `validation.failed` (`PriceReview.*`). |
| `PUT /services/{id}` | Errores + `billing.price_schedule.currency_locked`. |
| `POST /services/{id}/pause` · `/resume` · `/cancel` | Pausar un retenido guarda sus períodos retenidos y borra `held_since`; reanudar los deja retenidos (`state: Held`, `heldSince` = hoy) o los libera si ya no son de revisión; cancelar los descarta y cancela el programado pendiente (§6.4). Sin campos nuevos. |
| `GET /services`, `GET /services/{id}`, `GET /charges`, `GET /charges/{id}`, `GET /services/preview-charges` | Campos nuevos de arriba. |
| `POST /import` | Contratos importados con los valores de §6.8. Plantilla y vista previa sin cambios. |

### Códigos nuevos (`ServiceContractsErrorCodes`)

`billing.service.review_date_invalid`, `billing.service.not_active`, `billing.price_schedule.already_pending`,
`billing.price_schedule.not_found`, `billing.price_schedule.period_generated`, `billing.price_schedule.same_price`,
`billing.price_schedule.currency_locked`, `billing.price_schedule.after_review`, `billing.ipc.not_set`, `billing.ipc.in_use`,
`billing.ipc.rate_changed` (409),
`billing.price_review.not_ipc`, `billing.price_review.year_mismatch`, `billing.price_review.not_pending`,
`billing.price_review.price_changed` (sólo por ítem de `confirm-ipc`). Mapeo en `ApiExceptionHandler` como los demás de
`billing.*` (422, 404 para `not_found`).

## 9. Corrida diaria (cambios)

1. **Generación** (paso 3): carga, por contrato, el cambio programado pendiente y corre §6.3 en modo `Run`.
2. **Mensaje al cliente** (paso 4): sin cambios. Un contrato retenido no tiene cobro, así que no hay mensaje.
3. **Digest** (paso 5): calcula las revisiones `Due`/`Held` del tenant (una consulta: contratos `Active` con
   `held_since is not null` o `periodo_de_revision − 15 <= today`), las reparte por encargado con la misma regla de respaldo, y
   un miembro entra al digest si tiene cobros de interés o revisiones. Publica `billing.price-reviews.digest.v1` al reclamar,
   junto al de vencidos.

El período de revisión de cada contrato lo calcula una función pura de dominio (`PriceReview.Of(service, schedule, today)` o
equivalente) que usan la corrida, los DTO y la confirmación: una sola regla.

## 10. Lo que se deja al builder

- Nombres de clases, handlers y repositorios; partir el trabajo en tareas.
- Si `PriceReview` vive como método de `ServiceContract` o como clase aparte del dominio.
- La forma exacta de la consulta de revisiones del digest y de `GET /price-reviews` (debe resolver el período de revisión sin
  N+1; si hace falta, materializar `review_period_date` en `billing.services` y mantenerlo al generar y al editar).
- Textos del email nuevo, siguiendo `BillingDigestEmailTemplate`.
- Verificar que `notifications.notifications.kind` no tenga un CHECK con la lista de valores; si lo tiene, migrarlo.
- Pruebas: unitarias de `ChargeGenerator` (retención, liberación con vencimiento corrido, varios períodos retenidos, liberación
  que llega a una retención nueva en la misma pasada, `Manual` sin revisar, programado antes de la revisión que no la resuelve,
  pausa durante retención,
  pausa y reanudación de un retenido (el período cuya generación cae el día de la pausa se conserva, el de un contrato no retenido no; varias pausas acumulan huecos; pasar a `Manual` en pausa libera al reanudar; pausa de años con períodos arrastrados da dos revisiones),
  una sola revisión tras una pausa de varios años), de `AdvanceReview`, y de `PUT /ipc-rates/{year}`
  (`expectedRate` que coincide, que no coincide, `null` con una tasa ya guardada, misma tasa ya guardada con `expectedRate`
  viejo, insert simultáneo) y de integración por nombre de
  prueba (regla del workspace).

## 11. Preguntas abiertas para el owner

1. **P-1. Corregir el monto de un cobro ya generado.** Hoy no se puede, y esta feature no lo agrega. Si un IPC mal escrito o un
   precio mal confirmado llega a un cobro, ¿basta con que el siguiente cobro salga bien, o hace falta poder editar (o anular y
   regenerar) un cobro pendiente? Cambia lo que se construye: sería una ruta y una regla nuevas.
2. **P-2. Qué IPC dice el contrato.** El diseño usa el IPC del año calendario anterior a la revisión (§6.7). ¿Algún contrato
   usa el IPC de los 12 meses anteriores al aniversario, u otro índice? Si es así, la tasa deja de ser una por año y tenant.
3. **P-3. Avisos al cliente sobre el aumento.** Hoy el cliente sólo ve el monto nuevo en el aviso del cobro. ¿Se le debe avisar
   antes del aumento (plantilla de WhatsApp nueva, aprobación de Meta)? Fuera de alcance hasta que lo diga.
4. **P-4. Permiso propio.** ¿Quién gestiona cobros debe poder también cargar el IPC y confirmar aumentos en lote, o eso es de un
   rol más alto (por ejemplo, sólo `admin`)? El diseño usa `billing.service.manage`.
5. **P-5. Puntos negativos.** ¿Existe el caso «IPC menos N puntos»? El diseño admite de 0 a 50.

# Cobros recurrentes: contrato de backend

- **Estado:** propuesta, 2026-10-29. Sin código.
- **Diseño de producto:** `qep-frontend/docs/superpowers/specs/2026-10-29-cobros-recurrentes-design.md` (decisiones del owner y del
  diseñador; su §10 no tiene preguntas abiertas al 2026-10-29). Este documento convierte ese diseño en lo que se construye: módulo, rutas,
  cuerpos, códigos, tablas, worker y calendario. Ante discrepancia, el diseño manda en el **qué** y este documento en el
  **cómo**; si el código ya contradice a alguno de los dos, gana el código y se corrige el documento.
- **Planes:** `docs/superpowers/plans/2026-10-29-cobros-recurrentes-backend.md` y
  `qep-frontend/docs/superpowers/plans/2026-10-29-cobros-recurrentes-frontend.md`. **El backend se despliega primero.**
- **Decisiones del owner, 2026-10-29 (segunda ronda), ya incorporadas:** cada período genera su cobro aunque el anterior siga
  sin pagar; un mensaje por cliente y día que agrupa los vencidos; la pausa no silencia los cobros existentes; plantillas de
  WhatsApp aprobadas (texto en §5); Ley 1581 no bloquea en fase 1; permisos en `admin` y en el rol de sistema `billing`. **El
  módulo es Facturación (`billing`) y los cobros recurrentes son su primera feature**: por eso los permisos se acotan por
  recurso (`billing.service.*`) y las rutas por recurso bajo `/billing`, para que lo que venga después quepa sin renombrar.
  Tercera ronda: importación desde Excel con alta de clientes (§3.2b, §4 «Importación») y clasificación opcional en la
  importación de Clientes (§5). No queda ninguna `DECISIÓN-PENDIENTE`.

## 1. Módulo

> **Renombre (owner, 2026-10-30).** El módulo se llama `ServiceContracts` en el código (`src/Modules/ServiceContracts/`,
> `Modules.ServiceContracts.*`, agregado `ServiceContract`, antes `BillingService`) y «Contratos de servicio» en pantalla. El
> contrato desplegado no cambió: esquema `billing`, clave de módulo `billing`, permisos `billing.service.*`, rutas `/billing`,
> auditoría y códigos `billing.*`, sección `Billing` de configuración. Lo que sigue describe ese contrato con los nombres de
> entonces; para la correspondencia de nombres ver `CLAUDE.md`.

| Qué | Valor | Precedente |
| --- | --- | --- |
| Carpeta | `src/Modules/Billing/Modules.Billing.{Domain,Application,Infrastructure,Api}` | `src/Modules/Suppliers` |
| Clave de módulo | `billing` (Facturación; coincide a propósito con el rol de sistema `billing`, `SystemRoleKeys.cs:18`: es el mismo equipo), al final de `TenantModuleKeys.All`, `Dependencies[Billing] = [Customers]`, fuera de `DefaultForNewTenants` | `TenantModuleKey.cs:61-83` |
| Migración del CHECK | `AddBillingModuleKey` en Tenancy, con el `Down` que borra las filas `billing` | `20261024172027_AddSuppliersModuleKey.cs` |
| Esquema | `billing` | `suppliers` |
| Permisos | `billing.service.read` (toda lectura), `billing.service.manage` (todo lo que escribe, incluido marcar pagado y la configuración) | `SuppliersPermissions.cs` |
| `PermissionDefinition` | categoría `Billing`, riesgo `low` / `medium`, `RequiredModules: [ModuleKeys.Billing]` | `QepServiceCollectionExtensions.cs:1944-1957` |
| Roles | los dos permisos en `admin` **y en el rol de sistema `billing`** («Facturación»), con comentario del por qué en su `RoleDefinition`; ningún otro rol de sistema (owner, 2026-10-29) | `QepServiceCollectionExtensions.cs:1343-1345` y `:1406-1435` |
| Autorización en el handler | tenant de la ruta = tenant activo y permiso presente → si no, 403 `authorization.denied`, nunca 404; además `TenantModuleGuard.EnsureEnabledAsync(..., TenantModuleKeys.Billing)` (defensa en profundidad). **Regla (owner, 2026-10-29): nunca se muestran opciones ni permisos que el tenant o el usuario no tiene.** Con el módulo apagado, `ModuleEntitlementMask` quita el permiso antes del handler: el editor de roles no ofrece los permisos de Billing y la API responde un 403 pelado, sin el código `tenancy.module_not_enabled` | `SuppliersAuthorization.cs`, `PipelineSupport.cs:10-25` |
| Capas | `Modules.Billing.Application` referencia sólo `BuildingBlocks` y `Modules.Tenancy.Application`; Customers, Messaging, Notifications, Identity y Authorization entran por puertos con adaptador en `Bootstrapper`. `BillingLayerTests` lo fija | `SuppliersLayerTests.cs` |

Todas las rutas cuelgan de `/api/v1/tenants/{tenantId:guid}/billing`, con `X-Tenant-Id` en el header como el resto. Errores por
`ApiExceptionHandler`: ProblemDetails con `code` y `traceId`; `422 validation.failed` con `errors` por campo (FluentValidation,
nombres en PascalCase: `Name`, `Profile.AssigneeMemberId`); `404` con código de recurso; `412 concurrency.conflict` /
`428` cuando la ruta pide `If-Match`. Dos primeras escrituras simultáneas del mismo perfil o de los ajustes (misma PK) responden `409 billing.concurrent_first_write`: reintentar.

## 2. Modelo de datos

Fechas de negocio (`due_date`, `first_due_date`, `paid_on`) son `date` (`DateOnly`): el «hoy» del tenant es `ITenantClock.GetAsync(tenantId).Today`.
Montos `numeric(18,2)`; moneda `varchar(3)` del catálogo `Currencies`. Todo lleva `tenant_id` y los índices empiezan por él.

### `billing.customer_profiles`

| Columna | Tipo | Regla |
| --- | --- | --- |
| `tenant_id`, `customer_id` | uuid | PK compuesta. `customer_id` referencia `customers.customers` por id lógico (sin FK entre esquemas, como `quotations`). |
| `channel` | varchar(16) | CHECK `IN ('WhatsApp','Email')`. |
| `assignee_member_id` | uuid | Membresía del tenant (`tenancy.memberships.id`), obligatoria. |
| `version` | bigint | Token de concurrencia. |
| `created_at`, `updated_at` | timestamptz | |

### `billing.services`

| Columna | Tipo | Regla |
| --- | --- | --- |
| `id` | uuid v7 | PK |
| `tenant_id`, `customer_id` | uuid | Índice `(tenant_id, customer_id)`. |
| `name` | varchar(150) | Obligatorio, trim, no vacío. |
| `price` | numeric(18,2) | `> 0`. |
| `currency` | varchar(3) | Catálogo `Currencies`. |
| `cycle_months` | smallint | CHECK `IN (1,6,12)`. Nombres por el cable: `Monthly`=1, `Semiannual`=6, `Annual`=12. |
| `first_due_date` | date | Ancla del ciclo. Inmutable. |
| `status` | varchar(16) | CHECK `IN ('Active','Paused','Cancelled')`. |
| `reactivated_on` | date null | La última reactivación: desde ahí se retoma el ancla (§3.2). |
| `last_generated_due_date` | date null | El vencimiento del último cobro generado; el worker genera a partir de él (§3.2). |
| `version` | bigint | Token de concurrencia (`If-Match`). |
| `created_at`, `updated_at`, `status_changed_at` | timestamptz | |

### `billing.service_price_changes` — el historial de precios (owner, 2026-10-30)

Una fila por cambio de precio o de moneda de un contrato, escrita en la misma transacción del `PUT`. Sólo se inserta: es la
justificación del cambio, nunca se edita ni se borra. Sin FK a `services`, como `charges`.

| Columna | Tipo | Regla |
| --- | --- | --- |
| `id` | uuid v7 | PK |
| `tenant_id`, `service_id` | uuid | Índice `(service_id, occurred_at)`. |
| `previous_price`, `new_price` | numeric(18,2) | El precio antes y después. |
| `previous_currency`, `new_currency` | varchar(3) | La moneda antes y después (cambiar sólo la moneda también es un cambio de precio). |
| `reason` | varchar(500) | Obligatorio, trim, no vacío. |
| `changed_by_member_id` | uuid null | La membresía activa de quien editó; nula si no tenía (un operador). |
| `occurred_at` | timestamptz | |

### `billing.charges`

| Columna | Tipo | Regla |
| --- | --- | --- |
| `id` | uuid v7 | PK |
| `tenant_id`, `service_id`, `customer_id` | uuid | `customer_id` copiado del servicio (la lista filtra por cliente sin join). Índices `(tenant_id, status, due_date)`, `(tenant_id, customer_id, status)` y `(service_id, due_date)` **único**: es la deduplicación de la generación. Un servicio tiene **varios** cobros `Pending` a la vez (owner, 2026-10-29). |
| `amount`, `currency` | | Copiados del servicio al generar. |
| `due_date` | date | |
| `status` | varchar(16) | CHECK `IN ('Pending','Paid','Cancelled')`. **`Overdue` no se guarda**: es `Pending` con `due_date < hoy del tenant`, calculado en la respuesta. |
| `paid_on` | date null | Fecha en que el cliente pagó. Obligatoria con `Paid`. |
| `payment_note` | varchar(1000) null | |
| `paid_by_member_id` | uuid null | Quién lo marcó. |
| `paid_at` | timestamptz null | Cuándo se registró. |
| `version` | bigint | |
| `created_at` | timestamptz | |

### `billing.customer_messages` — el mensaje del día a un cliente

Un cliente recibe **a lo sumo un mensaje por día** (owner, 2026-10-29); esta tabla es ese mensaje y su deduplicación.

| Columna | Tipo | Regla |
| --- | --- | --- |
| `id` | uuid v7 | PK |
| `tenant_id`, `customer_id` | uuid | |
| `run_date` | date | **Único `(tenant_id, customer_id, run_date)`**. |
| `kind` | varchar(16) | CHECK `IN ('Reminder','Overdue')` (§3.3). |
| `channel` | varchar(16) | `WhatsApp` / `Email`, el del perfil al momento de enviar. |
| `status` | varchar(16) | CHECK `IN ('Sending','Sent','Failed')`. |
| `failure_code` | varchar(64) null | §3.4. |
| `claimed_until` | timestamptz | Lease de 5 min: una fila `Sending` vencida la retoma otra réplica (se vuelve a intentar **una** vez; si vuelve a fallar queda `Failed`). |
| `attempted_at`, `sent_at` | timestamptz | |

### `billing.reminder_steps` — qué paso de qué cobro cubrió cada mensaje

| Columna | Tipo | Regla |
| --- | --- | --- |
| `charge_id`, `step` | uuid, varchar(16) | **PK compuesta**: un paso se cubre una sola vez. `step` ∈ `before_5`, `due`, `after_3`, `after_10`, … (`after_{3+7k}`). |
| `message_id` | uuid | FK a `customer_messages`. El resultado del paso es el del mensaje. |
| `tenant_id` | uuid | |

`lastReminder` de un cobro (§4) es el mensaje de su último `reminder_step`.

### `billing.daily_digests` — el registro del email diario al equipo

PK `(tenant_id, member_id, run_date)`; `status` `Sending|Sent|Failed`, `failure_code`, `claimed_until`, `sent_at`. Una fila por
encargado y día es la deduplicación del email interno.

### `billing.tenant_runs`

PK `(tenant_id, run_date)`; `claimed_until`, `finished_at`. Un tick que encuentra la fila con `finished_at` no hace nada; una
sin `finished_at` con lease vencido se retoma. `finished_at` es la hora en que la corrida terminó, y sólo se escribe si el día
no tiene ningún `customer_messages` ni `daily_digests` en `Sending` (§3.3, paso 6).

### `billing.settings`

PK `tenant_id`; `whatsapp_connection_id` uuid null (la conexión de Integrations elegida para cobros, design §5); `version`.

## 3. Reglas

### 3.1 Perfil

- `channel = WhatsApp` exige `Customer.PhoneE164` no nulo → si no, 422 `billing.profile.whatsapp_requires_phone`. `Email` exige
  `Customer.Email` → 422 `billing.profile.email_requires_address`. Se valida al guardar el perfil; si el cliente pierde el dato
  después, el aviso falla con `no_phone` / `no_email` (§3.4), no se cambia el canal.
- `assigneeMemberId` tiene que ser una membresía `Active` del tenant **cuyos roles concedan `billing.service.manage`** (owner,
  2026-10-29: el encargado tiene que poder actuar sobre lo que el email diario le pide) → si no, 422
  `billing.profile.assignee_invalid`. `GET /assignees` lista exactamente ese conjunto.
- Valor por defecto que el frontend propone (no el backend): `WhatsApp` si hay `PhoneE164`, si no `Email`. El backend no asume.

### 3.2 Servicio y generación de cobros

- **Cada período genera su propio cobro, aunque el anterior siga sin pagar (owner, 2026-10-29).** Marcar pagado **no genera
  nada**.
- **Fechas del ancla:** `firstDueDate + k × cycleMonths`, con el día del mes del ancla y, si el mes no lo tiene, su último día
  (31 ene → 28 feb → 31 mar).
- **Ventana de generación (diseño):** un cobro nace cuando `due_date <= hoy del tenant + 5` (el adelanto del primer aviso).
  Regla de generación de un servicio `Active`, que corre al crearlo y en cada corrida diaria del worker (§3.3, paso 3):
  `next = last_generated_due_date is null ? firstDueDate : primera ancla > last_generated_due_date`; si `reactivated_on` es
  posterior a `last_generated_due_date`, `next = primera ancla >= reactivated_on` (los períodos en pausa **no se cobran**,
  owner). Mientras `next <= hoy + 5`: inserta el cobro `Pending` (`amount`/`currency` del servicio en ese momento), avanza
  `last_generated_due_date = next` y `next` a la siguiente ancla. La unicidad `(service_id, due_date)` hace la generación
  idempotente entre réplicas: una violación es «otra réplica lo creó», no un error.
- Crear el servicio corre esa regla en la misma transacción: con `firstDueDate` futura nace **un** cobro (aunque falte más de 5
  días: la pantalla necesita mostrar el próximo vencimiento; diseño); con `firstDueDate` pasada (migración desde el Excel)
  nacen **todos** los períodos hasta hoy + 5. Si el worker estuvo caído, al volver genera los que faltan.
- `Pause`: sólo desde `Active`. No se generan cobros nuevos; **los `Pending` que ya existen siguen pendientes y siguen
  avisándose** (owner, 2026-10-29). `Resume`: sólo desde `Paused`; escribe `reactivated_on = hoy del tenant`. `Cancel`: desde
  `Active` o `Paused`; **todos** los cobros `Pending` del servicio pasan a `Cancelled`. Otra transición → 422
  `billing.service.invalid_transition`.
- Marcar pagado un cobro de un servicio `Paused` o `Cancelled`: se permite si el cobro está `Pending` (la deuda existe).
- `PUT` del servicio cambia `name`, `price`, `currency`. Un cobro ya generado conserva su monto; los que se generen después toman
  el nuevo.
- **Historial de precios (owner, 2026-10-30).** Si el `PUT` cambia `price` o `currency` (100 y 100.00 son el mismo precio), el
  cuerpo tiene que traer `priceChangeReason` (trim, 1 a 500) o responde 422 `billing.service.price_reason_required` sin tocar
  nada; con él, se inserta una fila en `service_price_changes` con el antes, el después, el motivo y la membresía activa de quien
  editó (nula si no tiene). Si el precio no cambia, `priceChangeReason` se ignora aunque venga. El historial viaja completo en
  `ServiceDetailDto.priceChanges`, del más reciente al más viejo.
- Marcar pagado: cobro `Pending` → si no, 422 `billing.charge.not_pending`. `paidOn <= hoy del tenant` → si no, 422
  `billing.payment.date_in_future`. `note` hasta 1000, trim, vacío = null.
- Corregir pago (`PUT /charges/{id}/payment`): cobro `Paid` → si no, 422 `billing.charge.not_paid`. Mismas reglas de fecha y nota.
  No cambia el cobro siguiente ya generado.

### 3.2b Importación desde Excel (owner, 2026-10-29)

Copia de `ImportCustomers.cs`: lector ClosedXML, reglas de fila con FluentValidation y código por regla, deduplicador puro,
plantilla descargable. `.xlsx`/`.xls`, 10 MB, **500 filas** (`billing.import.too_many_rows`, 422). Dos pasos sobre el **mismo
archivo** (no hay staging en el servidor): `preview` no escribe nada; `import` revalida todo y escribe las filas válidas. Como
en Clientes, **ninguna fila válida se pierde por otra con error**; una fila que falló entre la vista previa y la confirmación
vuelve en `errors`.

**Columnas** (encabezados exactos de la plantilla, `BillingImportColumns`): `Cliente`, `Tipo de documento`,
`Número de documento`, `Teléfono`, `Correo`, `País`, `Servicio`, `Precio`, `Moneda`, `Ciclo`, `Próximo vencimiento`,
`Canal de aviso`, `Encargado`. Desplegables en la plantilla para tipo de documento (`IdentificationTypeParser.SupportedWireValues`),
ciclo (`Mensual`, `Semestral`, `Anual`), canal (`WhatsApp`, `Email`) y moneda (catálogo `Currencies`).

**Reglas de fila** (código → cuándo):

| Código | Cuándo |
| --- | --- |
| `billing.import.row.customer_name_required` | `Cliente` vacío o > 150. |
| `billing.import.row.identification_incomplete` | Sólo uno de tipo/número. |
| `billing.import.row.identification_type_invalid` | Tipo fuera de `SupportedWireValues`. |
| `billing.import.row.phone_required` | Sin teléfono y la fila **crea** el cliente, o el canal resuelto es `WhatsApp`. |
| `billing.import.row.phone_invalid` | No normaliza a E.164 (`IPhoneNumberNormalizer`, país de la fila o `CO`). |
| `billing.import.row.email_invalid` / `email_required` | Correo mal formado / canal `Email` sin correo. |
| `billing.import.row.country_invalid` | No es ISO alfa-2 conocido. |
| `billing.import.row.service_name_required` | Vacío o > 150. |
| `billing.import.row.price_invalid` | No es número > 0. |
| `billing.import.row.currency_unknown` | Fuera del catálogo. |
| `billing.import.row.cycle_invalid` | Fuera de `Mensual`/`Semestral`/`Anual` (se compara sin tildes ni mayúsculas). |
| `billing.import.row.due_date_invalid` | No es fecha. |
| `billing.import.row.channel_invalid` | Fuera de `WhatsApp`/`Email`. |
| `billing.import.row.assignee_required` / `assignee_invalid` | Sin correo de encargado / ningún miembro **activo con `billing.service.manage`** tiene ese correo. |
| `billing.import.row.customer_ambiguous` | Más de un cliente coincide (regla de abajo). |
| `billing.import.row.customer_inactive` | El cliente emparejado está inactivo. |
| `billing.import.row.duplicate_service` | Otra fila anterior del archivo tiene el mismo cliente (misma clave de emparejamiento) y el mismo `Servicio` (sin mayúsculas ni espacios extra). |
| `billing.import.row.service_exists` | El cliente ya tiene un servicio `Active` o `Paused` con ese nombre. |
| `billing.import.row.failed` | El servicio no se pudo guardar después de crear el cliente (ver «Transacción»). |

**Emparejamiento** (en este orden; el primero que aplica decide; `>1` resultado → `customer_ambiguous`): documento (tipo +
número) → `PhoneE164` → `Email` (sin mayúsculas). Sin resultado → se crea.

**Alta del cliente**: por `IBillingCustomerProvisioning` (adaptador en Bootstrapper sobre `ICustomerProvisioning`): con documento,
`CreateFromRegisterAsync(tenantId, actorUserId, RegisterCustomerData(name, type, number, phone, email), source: "billing")`;
sin documento, `CreateIncompleteAsync(tenantId, name, phone, email, country)`. Nace **incompleto** (sin CUC). `RegisterCustomerData`
gana un `Email` opcional (hoy no lo tiene: `CustomerProvisioning.cs:29`). La clasificación no interviene. `identification_taken`
en la carrera → se relee y se empareja.

**Perfil**: si el cliente (nuevo o emparejado) no tiene perfil, se crea con `Canal de aviso` (vacío → WhatsApp si hay
`PhoneE164`, si no Email) y el encargado; si ya tiene, las dos celdas **se ignoran** (la vista previa lo dice:
`profileAction: "kept"`).

**Servicio y cobros**: `BillingService.Create` + `ChargeGenerator.Generate` (§3.2), igual que el `POST`. La vista previa trae por
fila `chargesToGenerate` y `overdueCharges` (anclas `< hoy`), calculados con el mismo `ChargeGenerator` en seco.

**Transacción**: una por fila (`Customers` y `Billing` son unidades de trabajo distintas): primero el cliente por el puerto, después
perfil + servicio + cobros en `BillingUnitOfWork`. Si el segundo paso falla, el cliente creado queda (incompleto, inofensivo) y la
fila vuelve como error `billing.import.row.failed` con el detalle; es la misma deuda que `SDD-CT-07` conoce en registro de tenants
y se documenta, no se compensa.

Auditoría: `billing.import.completed` con `changedFields` = `["rows","imported","customersCreated","errors"]`.

### 3.3 Calendario y worker

**`BillingReminderWorker`** (`Modules.Billing.Infrastructure`): `BackgroundService` + `PeriodicTimer` cada
`Billing:SweepMinutes` (default 15; precedente `QuotationExpirationWorker.cs`). Cada tick, en su propio scope:

1. Recorre los tenants con algún servicio de cobro (`billing.services`; `IActiveTenantsQuery` lista los de un usuario, no los de
   la plataforma) y se queda con los que están `Active` (`ITenantDirectory.GetStatusAsync`: uno suspendido o en otro estado no
   genera ni avisa) y tienen `billing` habilitado (`ITenantModules`).
2. Por tenant: `calendar = ITenantClock.GetAsync(tenantId)`. Si la hora local es `< Billing:SendHourLocal` (default 8), nada. Si
   `billing.tenant_runs(tenant_id, calendar.Today)` existe con `finished_at`, nada. Si no, reclama la fila (insert, o lease
   vencido) y corre los pasos 3 a 5 para `today = calendar.Today`.
3. **Generación.** Para cada servicio `Active` del tenant, la regla de ventana de §3.2 (`due <= today + 5`). Corre **antes** de
   avisar para que el cobro exista cuando toque su `before_5`.
4. **Mensaje del día a cada cliente.** Candidatos: cobros `Pending` de servicios `Active` **o `Paused`** (owner: la pausa no
   silencia) cuyo cliente tiene perfil. **Cliente sin perfil** (owner, 2026-10-29): no tiene canal, así que no recibe ningún
   aviso; sus cobros sí entran al email diario del paso 5 y, como no tienen encargado, van a **cada** miembro con
   `billing.service.manage`. Por cliente:
   - Pasos del calendario de cada cobro: `before_5 = due − 5`, `due = due`, `after_3 = due + 3`, `after_{3+7k} = due + 3 + 7k`.
     **Paso que toca hoy** de un cobro: el **último** con fecha `<= today` que no tenga fila en `billing.reminder_steps` (catch-up:
     un día perdido cubre sólo el paso más reciente).
   - Si ningún cobro del cliente tiene paso hoy, nada. Si ya hay fila en `billing.customer_messages` para
     `(tenant, customer, today)`, nada.
   - **Tipo de mensaje:** `Overdue` si algún cobro **con paso hoy** tiene `due_date < today`; si no, `Reminder`.
     `Overdue` lista **todos** los cobros `Pending` vencidos del cliente (no sólo los que dispararon) con el total **por moneda**;
     `Reminder` lista los cobros con paso hoy (todos por vencer), con el total por moneda y el vencimiento más próximo.
     Un total por moneda, nunca convertido (diseño): «$ 450.000 y US$ 20,00».
   - **Pasos que cubre** (owner, 2026-10-30): un `Reminder` cubre los pasos de hoy de los cobros que lista. Un `Overdue` cubre
     **sólo** los pasos de hoy de los cobros vencidos (`due_date < today`); el `before_5` o `due` de un cobro por vencer que
     también tocaba hoy queda sin fila, porque el mensaje no lo menciona, y sale como `Reminder` el día siguiente si ese día
     no dispara ningún paso vencido (sigue siendo el último paso con fecha `<= today`).
   - Inserta la fila `Sending` del mensaje (si choca con el único, otra réplica ganó: nada) y una fila en `reminder_steps` por
     cada paso que cubre, resuelve el canal (§3.4), envía, marca `Sent` o `Failed` con `failure_code`. Un fallo también cubre los
     pasos: no se reintenta mañana el mismo paso; el siguiente paso del calendario volverá a intentar, y el fallo queda visible.
   **Entrega al menos una vez:** si el proveedor acepta el mensaje y el proceso cae (o falla la escritura) antes de marcar
   `Sent`, la fila queda `Sending`; cuando vence su lease otra corrida la retoma y lo reenvía **una** vez, así que el cliente
   puede recibir ese aviso dos veces. Se prefiere a perderlo: no hay forma de saber si el proveedor lo entregó.
5. **Email diario al equipo.** Para cada encargado con cobros «de interés» —`Pending` con `due_date <= today + 5` (vencen
   pronto o vencidos) **o** cuyo último mensaje está `Failed`— arma un email con esas filas (y los totales por moneda) y lo
   manda; una fila en `billing.daily_digests` por `(member, today)` es la deduplicación. Si la membresía del encargado no está
   `Active`, sus roles ya no conceden `billing.service.manage` o el cliente no tiene perfil (paso 4), esos cobros van al email
   de **cada** miembro con ese permiso
   (`IBillingMemberDirectory.ListManagersAsync`). Sin cobros de interés, no se manda nada. Formato de fechas y montos con
   `EmailRegionalFormats.For(calendar.TimeZone, regionalSettings)` (precedente `SupplierExportDeliveryWorker.cs:38`). Cada
   monto, en los dos correos al cliente y en éste, se escribe como `WhatsAppTemplateValues.Total`: símbolo y decimales del
   catálogo `Currencies` de Tenancy y separadores del tenant («US$ 20,00»).
6. Cierra los `Sending` que nadie retomó: un mensaje o digest del tenant con el lease vencido (de hoy o de un día anterior)
   que los pasos 4 y 5 no volvieron a reclamar pasa a `Failed` con `send_failed` (o con el código que dejó su retoma). Después
   marca `finished_at` con la hora de cierre, **sólo si** al día no le queda ninguna fila `Sending`: una que sigue viva (otra
   réplica a mitad de envío, o un `MarkSent` que falló) deja el día abierto, y el tick siguiente la retoma cuando vence su
   lease. Un error en un servicio, un cliente o un email se registra y **no** frena a los demás (precedente
   `OutboxDeliveryWorker.DrainAsync`, un `try` por elemento).

**Puertos** (en `Modules.Billing.Application`, adaptadores en `Bootstrapper`):

| Puerto | Qué | Sobre qué |
| --- | --- | --- |
| `IBillingCustomerLookup` | `FindAsync(tenantId, customerId) → BillingCustomerRef?(Id, Name, PhoneE164, Email, IsActive)`, `FindManyAsync(tenantId, ids)`, `SearchIdsAsync(tenantId, term)` para el filtro por nombre | `CustomersDbContext`, como `QuotationCustomerLookup.cs` |
| `IBillingMemberDirectory` | `FindAsync(tenantId, memberIds) → {MemberId, DisplayName, Email?, IsActive}`; `ListManagersAsync(tenantId)` (membresías activas cuyos roles conceden `billing.service.manage`) | Tenancy + Identity + Authorization, como `MessagingAssignees` en Bootstrapper |
| `IBillingCustomerEmail` | `SendAsync(BillingCustomerMessage) → BillingSendResult` (`Kind` Reminder/Overdue, lista de cobros, totales por moneda) | `IEmailChannel` + `BillingReminderEmailTemplate` / `BillingOverdueEmailTemplate` (Notifications.Application) |
| `IBillingStaffEmail` | `SendDigestAsync(BillingDigestEmail) → BillingSendResult` | `IEmailChannel` + `BillingDigestEmailTemplate` |
| `IBillingWhatsAppOutbound` | `SendAsync(BillingCustomerMessage, connectionId) → BillingSendResult`; `ListConnectionsAsync(tenantId) → [{ConnectionId, Name, Phone, Ready, NotReadyReason}]` | Mensajería: `IBillingTemplateOutbound` (hermano de `IQuotationTemplateOutbound`, sin documento) y el directorio de conexiones `whatsapp-cloud` de Integrations (como `QuotationsWhatsAppChannelDirectory.cs`). Plantillas en §5. |
| `IBillingConsentGate` | `MayNotifyAsync(tenantId, customerId) → bool` | Fase 1: responde `true` siempre (owner, 2026-10-29: el aviso sale sin importar el consentimiento y esta fase no lo pide). Es el único lugar donde entraría Ley 1581 si cambia. |

`BillingSendResult` = `Sent | Failed(string Code)`.

### 3.4 Códigos de fallo de un aviso (`failure_code`)

| Código | Cuándo |
| --- | --- |
| `no_phone` | Canal WhatsApp y el cliente ya no tiene `PhoneE164`. |
| `no_email` | Canal Email y el cliente ya no tiene `Email`. |
| `channel_not_configured` | WhatsApp sin `billing.settings.whatsapp_connection_id`. |
| `template_not_approved` | La conexión no tiene la plantilla de cobro aprobada. |
| `connection_unavailable` | Mensajería no pudo usar la conexión (`ConnectionUnavailable`, con su razón detrás: `connection_unavailable:<reason>`). |
| `send_failed` | El proveedor rechazó el envío (`Failed(code)` de Mensajería → `send_failed:<code>`; una excepción de `IEmailChannel`). |
| `consent_blocked` | Reservado para si Ley 1581 pasa a bloquear. No se emite en fase 1. |

Nunca se cambia de canal ante un fallo. El fallo queda en el mensaje y viaja en `lastReminder` de cada cobro que cubrió.

## 4. Contrato HTTP

Convención de DTO: opcionales viajan como `null`, nunca ausentes. Fechas `date` como `yyyy-MM-dd`; instantes en ISO 8601 UTC.
`status` de cobro en el cable: `Pending | Overdue | Paid | Cancelled`.

### Tipos compartidos

```
ChargeStatus      = "Pending" | "Overdue" | "Paid" | "Cancelled"
ServiceStatus     = "Active" | "Paused" | "Cancelled"
BillingChannel    = "WhatsApp" | "Email"
Cycle             = "Monthly" | "Semiannual" | "Annual"

MemberRef         { memberId: guid, displayName: string, isActive: bool }
CustomerRef       { id: guid, name: string, phoneE164: string|null, email: string|null }
ProfileDto        { customerId: guid, channel: BillingChannel, assignee: MemberRef, version: long }
ReminderDto       { step: string, kind: "Reminder"|"Overdue", channel: BillingChannel, status: "Sending"|"Sent"|"Failed", failureCode: string|null, attemptedAt: iso, sentAt: iso|null }
CurrencyAmountDto { currency: string, amount: decimal }
PaymentDto        { paidOn: date, note: string|null, paidBy: MemberRef|null, paidAt: iso }
ChargeDto         { id, serviceId, serviceName, customer: CustomerRef, assignee: MemberRef|null, amount: decimal, currency: string,
                    dueDate: date, status: ChargeStatus, daysOverdue: int (0 si no vence), payment: PaymentDto|null,
                    lastReminder: ReminderDto|null, version: long }
ServiceDto        { id, customer: CustomerRef, name, price: decimal, currency, cycle: Cycle, firstDueDate: date, status: ServiceStatus,
                    nextCharge: ChargeDto|null (el Pending de vencimiento más próximo), openCount: int, openAmountByCurrency: CurrencyAmountDto[],
                    version: long, createdAt: iso, updatedAt: iso }
ServicePriceChangeDto { id, previousPrice: decimal, previousCurrency, newPrice: decimal, newCurrency, reason: string,
                        changedBy: MemberRef|null, occurredAt: iso }
ServiceDetailDto  { service: ServiceDto, profile: ProfileDto, charges: ChargeDto[] (todas, por dueDate desc),
                    priceChanges: ServicePriceChangeDto[] (del más reciente al más viejo; [] si nunca cambió) }
```

### Cobros

| Método y ruta | Permiso | Respuesta | Errores |
| --- | --- | --- | --- |
| `GET /charges?status=open\|paid\|cancelled&assignee=me&customerId=&search=&page=1&pageSize=25` | read | `200 { items: ChargeDto[], total, page, pageSize, summary: { openCount, overdueCount, failedReminderCount, openAmountByCurrency: CurrencyAmountDto[], overdueAmountByCurrency: CurrencyAmountDto[] } }`. `open` = `Pending`+`Overdue`, ordenados por `dueDate` asc; `paid` por `paidOn` desc. `assignee=me` resuelve la membresía del llamador (`IMembershipDirectory.FindActiveMembershipIdAsync`). `search` busca por nombre del cliente (`IBillingCustomerLookup.SearchIdsAsync`) y por nombre del servicio. `summary` es la **deuda acumulada del tenant entero** (`status=open`, sin los demás filtros), un total por moneda, sin convertir (owner, 2026-10-29). | 403; 422 `validation.failed` con `status` o `pageSize` (tope 100) inválidos |
| `GET /charges/{chargeId}` | read | `200 ChargeDto` | 403; 404 `billing.charge.not_found` |
| `POST /charges/{chargeId}/pay` body `{ paidOn: date, note: string\|null }` | manage | `200 ChargeDto` (el cobro pagado; **no genera nada**: la generación es del worker, §3.2). Sin `If-Match`: marcar pagado dos veces responde 422, no 412. | 403; 404; 422 `billing.charge.not_pending`, `billing.payment.date_in_future`, `validation.failed` (`Note` > 1000, `PaidOn` ausente) |
| `PUT /charges/{chargeId}/payment` body `{ paidOn, note }` | manage | `200 ChargeDto` | 403; 404; 422 `billing.charge.not_paid`, `billing.payment.date_in_future`, `validation.failed` |

Auditoría (`IAuditRecorder`, misma transacción): `billing.charge.paid` y `billing.charge.payment_updated`, recurso `charge`,
`changedFields` = `["paidOn","note"]` según corresponda. Precedente `UpdateTenantSettings.cs:109-124`.

### Servicios

| Método y ruta | Permiso | Respuesta | Errores |
| --- | --- | --- | --- |
| `GET /services?customerId=&status=active\|paused\|cancelled\|all&search=&page&pageSize` | read | `200 { items: ServiceDto[], total, page, pageSize }`, por `customer.name`, `name`. Default `status=active`. | 403; 422 |
| `GET /services/preview-charges?cycle=Monthly&firstDueDate=2025-12-10` | read | `200 { chargesToGenerate: int, overdueCharges: int }`: lo que `POST /services` crearía hoy con esos datos (`ChargeGenerator` en seco, §3.2). Lo usa el aviso del formulario «Se van a crear N cobros vencidos» (design §4.3); no escribe. | 403; 422 `validation.failed` (`Cycle`, `FirstDueDate`) |
| `GET /services/{serviceId}` | read | `200 ServiceDetailDto` | 403; 404 `billing.service.not_found` |
| `POST /services` body `{ customerId, name, description: string\|null, price, currency: string\|null, cycle: Cycle, firstDueDate: date, profile: { channel, assigneeMemberId }\|null }` | manage | `201 ServiceDetailDto`, `Location: /services/{id}`. `currency` null → moneda del tenant. `profile` se **ignora** si el cliente ya tiene perfil; si no tiene y viene null → 422 `billing.profile.missing`. `charges` trae el primer cobro o, con `firstDueDate` pasada, todos los períodos hasta hoy + 5 (§3.2). | 403; 422 `billing.customer.not_found` (no existe en el tenant), `billing.customer.inactive`, `billing.profile.missing`, `billing.profile.assignee_invalid`, `billing.profile.whatsapp_requires_phone`, `billing.profile.email_requires_address`, `billing.service.description_too_long` (> 1000 tras recortar), `billing.currency.unknown`, `validation.failed` (`Name`, `Price`, `Cycle`, `FirstDueDate`) |
| `PUT /services/{serviceId}` body `{ name, description: string\|null, price, currency, priceChangeReason: string\|null }`, `If-Match: <version>` | manage | `200 ServiceDetailDto`. `priceChangeReason` es obligatorio sólo si `price` o `currency` cambian (§3.2); si no, se ignora. | 403; 404; 412 `concurrency.conflict`; 428; 422 `billing.service.cancelled` (no se edita un cancelado), `billing.service.price_reason_required`, `billing.service.description_too_long` (> 1000 tras recortar), `billing.currency.unknown`, `validation.failed` (`PriceChangeReason` > 500) |
| `POST /services/{serviceId}/pause` · `/resume` · `/cancel`, `If-Match` | manage | `200 ServiceDetailDto` | 403; 404; 412; 428; 422 `billing.service.invalid_transition` |

Auditoría: `billing.service.created`, `billing.service.updated` (`changedFields` = campos cambiados), `billing.service.paused`,
`billing.service.resumed`, `billing.service.cancelled`.

### Importación (owner, 2026-10-29)

```
ImportRowErrorDto   { rowNumber: int, code: string, message: string, field: string|null }
ImportRowPreviewDto { rowNumber: int, customer: { name, matchedId: guid|null, action: "create"|"match" },
                      profileAction: "create"|"kept", service: { name, price, currency, cycle, firstDueDate },
                      chargesToGenerate: int, overdueCharges: int }
ImportPreviewDto    { fileName, totalRows, validRows: ImportRowPreviewDto[], errors: ImportRowErrorDto[],
                      summary: { customersToCreate, customersMatched, servicesToCreate, chargesToGenerate, overdueCharges } }
ImportResultDto     { fileName, receivedAt: iso, totalRows, importedCount, errorCount, customersCreated,
                      imported: [{ rowNumber, serviceId, customerId, customerCreated: bool, chargesGenerated, overdueCharges }],
                      errors: ImportRowErrorDto[] }
```

| Método y ruta | Permiso | Respuesta | Errores |
| --- | --- | --- | --- |
| `GET /import/template` | manage | `200` `.xlsx` (`Content-Disposition: attachment; filename="plantilla-cobros-recurrentes.xlsx"`), como `GET /customers/import/template` | 403 |
| `POST /import/preview` multipart `file` | manage | `200 ImportPreviewDto`. **No escribe.** `DisableAntiforgery()` como `/customers/import` (el CSRF por `X-Qep-Client` sigue). | 403; 422 `billing.import.file_required`, `billing.import.extension_invalid`, `billing.import.too_large`, `billing.import.unreadable`, `billing.import.too_many_rows`, `billing.import.header_mismatch` |
| `POST /import` multipart `file` | manage | `200 ImportResultDto` (§3.2b: filas válidas importadas, el resto en `errors`) | los mismos |

### Perfil de cobro

| Método y ruta | Permiso | Respuesta | Errores |
| --- | --- | --- | --- |
| `GET /customers/{customerId}/profile` | read | `200 ProfileDto` | 403; 404 `billing.profile.not_found` |
| `PUT /customers/{customerId}/profile` body `{ channel, assigneeMemberId }` | manage | `200 ProfileDto` (upsert; sin `If-Match`: última escritura gana, como `QuotationSettingsEndpoints`) | 403; 422 `billing.customer.not_found`, `billing.profile.assignee_invalid`, `billing.profile.whatsapp_requires_phone`, `billing.profile.email_requires_address`, `validation.failed` |

Auditoría: `billing.profile.updated`.

### Encargados y configuración

| Método y ruta | Permiso | Respuesta | Errores |
| --- | --- | --- | --- |
| `GET /assignees` | read | `200 { items: MemberRef[] }`: membresías `Active` del tenant **cuyos roles conceden `billing.service.manage`**, por `displayName` (owner, 2026-10-29: el encargado debe poder actuar sobre el email diario). | 403 |
| `GET /settings` | manage | `200 { whatsAppConnectionId: guid\|null, connections: [{ id, name, phone: string\|null, ready: bool, notReadyReason: "template_not_approved"\|"connection_unavailable"\|null }] }`. `connections` son las conexiones `whatsapp-cloud` activas del tenant; `ready` exige las **dos** plantillas de §5 `APPROVED`. | 403 |
| `PUT /settings` body `{ whatsAppConnectionId: guid\|null }` | manage | `200` mismo cuerpo | 403; 422 `billing.settings.connection_unknown` |

## 5. Cruces con otros módulos

- **Alta de clientes desde la importación:** `IBillingCustomerProvisioning` (Billing.Application) → adaptador en Bootstrapper sobre
  `ICustomerProvisioning` (`CustomerProvisioning.cs:37-70`). Cambio en Customers: `RegisterCustomerData` suma `string? Email = null`
  y `CreateFromRegisterAsync` lo pasa al agregado. `IBillingCustomerLookup` suma `FindByIdentificationAsync(tenantId, type, number)`,
  `FindByPhoneE164Async(tenantId, e164)`, `FindByEmailAsync(tenantId, email)` → listas (el emparejamiento necesita saber si hay
  más de uno).
- **Clasificación opcional en la importación de Clientes (owner, 2026-10-29; coherente con el 2026-10-19):** `ImportCustomers.cs:139-142`
  deja de exigirla (`classification_required` desaparece), `:342-353` sólo resuelve el nombre si viene (vacío → sin clasificación),
  `:607-611` y `:653` pasan `null`, `CucFormatter.Build(null, …)` ya lo admite (`CucFormatter.cs:9-10`). La plantilla
  (`ClosedXmlCustomerImportTemplateBuilder.cs:236-243`) conserva el desplegable. Frontend: `customers.api.ts:986-987` pierde la
  traducción del código; no hay otro «obligatorio» de clasificación en la UI de importación.

- **Fusión de clientes:** el adaptador de `ICustomerMergeReferences` en Bootstrapper mueve `billing.services.customer_id` y
  `billing.charges.customer_id` al que queda, en la misma transacción; el perfil del fusionado se borra si el que queda ya tiene
  uno, o se le reasigna si no. `CustomerMergeReferenceCounts` **no cambia** en fase 1 (cambiarlo cambia la vista previa de la
  fusión en el frontend): seguimiento.
- **Retención de usuarios:** `BillingUserReferenceProbe : IUserReferenceProbe` (`Source = "billing"`) responde `true` si alguna
  membresía del usuario (`IMembershipDirectory.ListMembershipIdsByUserAsync`) aparece en `customer_profiles.assignee_member_id`
  o `charges.paid_by_member_id`. Precedente: la sonda de Quotations.
- **Mensajería (owner, 2026-10-29, plantillas aprobadas):** `MessageTemplateKind.BillingReminder` y `BillingOverdue`, su
  aprovisionamiento por tenant como `QuotationTemplateProvisioning.cs` (QEP las crea en la cuenta del tenant; categoría
  `UTILITY`, idioma `es`, **parámetros posicionales**, sin encabezado de documento) y `IBillingTemplateOutbound`. Nombres
  `qep_cobro_recordatorio` y `qep_cobro_vencido` (versionados como `qep_cotizacion_v2` si cambian). Texto **literal** del owner:

  | Kind | Nombre | Cuerpo | Variables |
  | --- | --- | --- | --- |
  | `BillingReminder` | `qep_cobro_recordatorio` | `Hola {{1}}, te recordamos que tu servicio {{2}} por {{3}} vence el {{4}}. Si ya realizaste el pago, ignora este mensaje.` | `{{1}}` nombre del cliente; `{{2}}` servicio, o los nombres unidos con « y » si son varios; `{{3}}` monto, un total por moneda unidos con « y »; `{{4}}` vencimiento más próximo, formato de fecha del tenant |
  | `BillingOverdue` | `qep_cobro_vencido` | `Hola {{1}}, tienes {{2}} cobro(s) vencido(s) por un total de {{3}}. Escríbenos para ponerte al día. Si ya pagaste, ignora este mensaje.` | `{{1}}` nombre del cliente; `{{2}}` cantidad de cobros vencidos; `{{3}}` total por moneda unidos con « y » |

  Montos con el criterio de `Quotations.Application/WhatsAppTemplateValues.cs` (símbolo, decimales de la moneda y separadores
  del tenant). Meta rechaza parámetros con saltos de línea o más de 4 espacios seguidos (regla que ya aplica
  `QuotationTemplateOutbound.IsAcceptable`): los nombres se recortan a una línea.

## 6. Configuración

```json
"Billing": { "SweepMinutes": 15, "SendHourLocal": 8 }
```

Validadas al arrancar (`> 0`, hora `0..23`), como `QuotationsOptions`.

## 7. Orden de despliegue

1. Backend con el módulo, sin `billing` habilitado en ningún tenant. El worker no hace nada para un tenant sin el módulo.
2. Frontend.
3. El operador habilita `billing` al tenant del owner desde la consola; el tenant configura perfiles y servicios y, en
   Configuración, elige la conexión de WhatsApp una vez que Meta aprobó las dos plantillas aprovisionadas.

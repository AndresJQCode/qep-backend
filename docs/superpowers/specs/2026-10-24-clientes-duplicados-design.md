# Clientes duplicados: reglas de documento, número compartido y anomalías

**Fecha:** 2026-10-24
**Módulos:** Customers (reglas de alta y edición, consulta de coincidencias, anomalías), Messaging
(número compartido en el hilo, cambiar el cliente de una conversación, campañas sin duplicados por
contacto, conversación única por número al iniciar), Bootstrapper (adaptadores de los puertos de
Messaging). Authorization **sin** permisos nuevos.
**Repos:** `qep-backend` y `qep-frontend`.
**Estado:** borrador para revisión. Las reglas R1–R3, el enfoque de Messaging y las tres decisiones
de §10 son del owner (conversación del 2026-10-22 al 24); las que tomó el spec para cerrarlas están
en §9 (D-D1…), a ratificar.
**Rama:** `feature/clientes-duplicados` desde `develop` `c78792e3`.
**Fuera de este spec:** la fusión de clientes. Va en un spec aparte (§8).

## 1. Contexto

La pregunta que lo arrancó: «si dos clientes tienen el mismo número, ¿cuál muestra /messaging?».

Hoy:

- La conversación se amarra a un cliente **una sola vez**, en la ingesta (`WebhookDeliveryProcessor.PrepareAsync`
  → `CustomerWhatsAppDirectory.EnsureAsync`): por BSUID, si no por teléfono, si no se crea un incompleto.
  Por teléfono gana el más viejo y luego el id menor (D-M7 del spec 2026-10-09). Nadie se entera de
  que había otros candidatos, y **no existe ningún camino que cambie el `customer_id` después**.
- El documento (tipo + número) ya es único (`IX_customers_tenant_identification`), pero el 422
  `customers.customer.identification_taken` no dice de quién es, salvo en el alta rápida del POS.
- El teléfono no es único (`IX_customers_tenant_phone_e164`), y así se queda (R3).

En la base local (`dev_qep`, 2026-10-22) hay 3 números compartidos por 9 clientes. Uno es una
persona y su empresa (`LUIS ALBERTO MARULANDA GONZALEZ`, Cc, y `ZIBA TIENDA MULTIMARCA S.A.S.`, Nit),
que es el caso que justifica R3. Otro es el mismo número de documento con dos tipos (`92-1396892`,
Nit y Ce), que es el caso que justifica R2.

### Dos defectos que salieron en el reconocimiento

1. **Una campaña le manda la misma plantilla dos veces a la misma persona** si dos clientes de la
   audiencia comparten número. `campaign_recipients` es único por `(campaign_id, customer_id)` y no
   por contacto (`MessagingDbContext.cs:413`).
2. **Iniciar una conversación puede abrir un segundo chat con el mismo número.** Si el cliente B
   comparte el teléfono de A, B no tiene BSUID (es único por cliente) y la conversación de A ya
   tiene BSUID, `ConversationOpener.OpenAsync` inserta una fila nueva sólo con `wa_id`
   (`IX_conversations_connection_wa_legacy` lo permite porque filtra `user_id IS NULL`). Las
   respuestas de la persona llegan a la conversación de A y la de B nunca se adopta
   (`InboundIngestion.ResolveConversationAsync` encuentra primero la de A por BSUID).

## 2. Las reglas (decisión del owner)

| | Caso | ¿Se guarda? | Qué ve el asesor |
|---|---|---|---|
| R1 | Mismo tipo **y** mismo número de documento que otro cliente | **No** | El rechazo, **con el cliente que ya tiene esos datos** |
| R2 | Mismo número de documento, **otro tipo** | Sí | Un aviso con el cliente que ya tiene ese número, antes de guardar |
| R3 | Mismo celular o WhatsApp, documento distinto | Sí | Nada al guardar: el cliente se distingue por el documento, no por el número |

**Y en Messaging (recomendación aceptada por el owner):** un chat por persona, amarrado a un
**cliente principal**. El hilo avisa qué otros clientes comparten el número y el asesor puede
cambiar el cliente principal. Las campañas no le escriben dos veces al mismo contacto.

### Metas

- Que nadie cree un duplicado de documento sin saber quién es el dueño.
- Que el asesor vea, en el hilo, que el número es de varios clientes y pueda elegir el correcto.
- Que una persona reciba una sola plantilla por campaña.
- Que exista un lugar donde revisar los casos raros que ya están en la base.

### No metas

- Fusionar clientes (spec aparte, §8).
- Hacer único el teléfono: la ficha exige teléfono (`CustomerContactInfo`) y, con unicidad, la
  empresa que comparte el celular de su dueño terminaría con un número inventado; es lo que pasó
  con el correo, y por eso dejó de ser obligatorio (comentario de ese mismo archivo).
- Cambiar R1 en la importación de Excel, el POS o Shopify más allá de lo que dice §3.4.
- Normalizar el documento al guardarlo (`900.123.456-1` y `9001234561` siguen siendo dos valores
  distintos en base). §3.2 los compara normalizados **sólo para avisar**.

### Anti-metas

- Un aviso que bloquee. R2 es informativo: si el asesor sigue, se guarda.
- Un 404 donde corresponde 403 en cualquier consulta nueva (aislamiento de tenant, CLAUDE.md).

## 3. Customers

### 3.1 R1: el 422 dice quién es el dueño

`POST /customers` y `PUT /customers/{customerId}` (los dos con `CustomerManage`) siguen respondiendo
`422` con `code = customers.customer.identification_taken` (el contrato no cambia), y **además**
llevan la extensión `existing`. La forma sigue la del POS (`PosCustomerOptionResponse`, `PosDtos.cs:168`)
más `cuc`, que el POS no trae; el parser del frontend se generaliza para los dos códigos (§5.1):

```json
{
  "status": 422,
  "code": "customers.customer.identification_taken",
  "existing": {
    "id": "…", "name": "GYSELYS VIVAS PACHECO",
    "identificationType": "NIT", "identificationNumber": "92-1396892",
    "cuc": "MED00000105", "isComplete": true, "isActive": true
  }
}
```

- Un `CustomerIdentificationTakenException` nuevo en Customers.Application, `DomainException` +
  `IHasProblemExtensions`, como `PosCustomerIdentificationTakenException` (`PosCustomers.cs`). El
  record del `existing` es nuevo en `CustomersDtos.cs`; `cuc` es lo único que agrega sobre el del POS
  (puede ser `null` en un incompleto).
- **Sin pre-chequeo.** `CreateCustomer.cs` lo descarta a propósito («el único árbitro real son los
  índices»), y el código manda. El índice sigue decidiendo: `CustomersUnitOfWork` lanza el
  `CustomersDomainException` de hoy, y `CreateCustomer` y `UpdateCustomer` lo atrapan alrededor de
  sus `SaveChangesAsync`, releen al dueño con `FindExistingIdentificationsAsync` (incluye inactivos
  e incompletos: los del POS tienen documento) y lanzan el nuevo con `existing`, el mismo patrón
  que `CustomerProvisioning.cs:166-177`. Si el dueño no se puede releer (lo borraron en el medio),
  sale el 422 de hoy sin `existing`: el frontend lo tiene que tolerar.

### 3.2 R2: consulta de coincidencias de documento

```
GET /api/v1/tenants/{tenantId}/customers/identification-matches?number={n}&type={t}&excludeCustomerId={id}
```

- Permiso `CustomerRead`; el handler revalida tenant y permiso (`CustomersAuthorization`).
- `number` obligatorio (vacío → `422 validation.failed` con `errors.Number`); `type` y
  `excludeCustomerId` opcionales. `type` con el valor del cable (`NIT`, `CC`, `CE`, `PASAPORTE`).
- Devuelve **todos** los clientes del tenant cuyo número **normalizado** coincide con el normalizado
  de `number`, sin el `excludeCustomerId`. Normalizar es quitar todo lo que no sea letra o dígito y
  pasar a mayúsculas: `92-1396892` ≡ `921396892`.
- Respuesta (BFF: la pantalla decide el color por un campo, no comparando tipos):

```json
{
  "items": [
    {
      "id": "…", "name": "GISELY", "identificationType": "CE", "identificationNumber": "92-1396892",
      "cuc": "PEQ00000194", "isComplete": true, "isActive": true,
      "match": "OtherType"
    }
  ]
}
```

  `match` es `SameIdentification` (mismo tipo y mismo número exacto: R1, el guardado va a fallar),
  `OtherType` (R2) o `SameTypeOtherFormat` (mismo tipo, número igual al normalizar pero distinto
  tal cual: el índice no lo frena; D-D3). Con `type` ausente, todo lo que coincide es `OtherType`.
  Sin coincidencias, `items: []`.
- Con tope de 20 filas, ordenadas por `created_at, id`. Un número que comparten más de 20 clientes
  es una anomalía (§3.3), no un aviso.
- Índice nuevo para que sea una búsqueda y no un barrido: expresión
  `(tenant_id, upper(regexp_replace(identification_number, '[^0-9A-Za-z]', '', 'g')))` con filtro
  `identification_number IS NOT NULL`, nombre `IX_customers_tenant_identification_normalized`.
  La expresión vive en un solo lugar del código (una constante o función SQL), porque la usan esta
  consulta y la de §3.3.

### 3.3 Anomalías

```
GET /api/v1/tenants/{tenantId}/customers/anomalies?kind={kind}&page={p}&pageSize={s}
```

- Permiso `CustomerRead`.
- `kind`: `SharedPhone` (dos o más clientes con el mismo `phone_e164`) o
  `SharedIdentificationNumber` (dos o más clientes con el mismo número normalizado, cualquier
  tipo). Obligatorio; otro valor → `422 validation.failed`.
- Pagina **grupos**, no clientes: `pageSize` por defecto 20, máximo 50, con constantes propias (el
  listado usa 50 y 200, `ListCustomers.cs:53,65`; un grupo pesa hasta 50 clientes, así que no se
  reutilizan). Orden: grupos con más clientes primero, luego la clave del grupo.
- Respuesta:

```json
{
  "counts": [
    { "kind": "SharedPhone", "groups": 3 },
    { "kind": "SharedIdentificationNumber", "groups": 1 }
  ],
  "groups": [
    {
      "key": "+573043549660",
      "customers": [
        { "id": "…", "name": "LUIS ALBERTO MARULANDA GONZALEZ", "identificationType": "CC",
          "identificationNumber": "1151936762", "cuc": "SUP76000008", "phone": "+57 304 354 9660",
          "isComplete": true, "isActive": true, "createdAt": "2026-10-02T…Z" }
      ]
    }
  ],
  "total": 3, "page": 1, "pageSize": 20
}
```

  `counts` viaja **completo, incluso en cero** (las dos clases siempre), para que las pestañas
  internas dibujen sus contadores sin conocer el enum. `customers` de cada grupo va completo (sin
  paginar) y ordenado por `created_at, id`; un grupo con más de 50 clientes se corta en 50 y lleva
  `truncated: true` (el campo existe siempre).
- Incluye inactivos e incompletos: un incompleto de WhatsApp con el número de un completo es
  justamente el caso que más se repite.
- Sólo lectura en este spec: cada fila enlaza a la ficha del cliente. Fusionar llega con el spec
  de §8.
- Nada se guarda: los grupos se calculan en la consulta (`GROUP BY … HAVING count(*) > 1`) sobre
  los índices de teléfono y de §3.2.

### 3.4 Los otros caminos de alta

| Camino | R1 | R2 |
|---|---|---|
| Formulario (`POST`/`PUT /customers`) | 422 con `existing` (§3.1) | aviso antes de guardar (§5.1) |
| Importación de Excel | Como hoy: `customers.import.row.identification_taken` | No avisa (D-D5) |
| POS (`POST /pos/customers`) | Como hoy: ya devuelve `existing` | No avisa (D-D5) |
| Shopify, Messaging | Como hoy (reutilizan el cliente existente) | No aplica: son automáticos |

## 4. Messaging

### 4.1 El hilo muestra a los otros clientes del número

`ConversationSummary` (`MessagingDtos.cs`) suma `SharedPhoneCustomers`:
`IReadOnlyList<CustomerRefDto>`, **siempre presente**, vacía si no hay otros.

- Son los clientes del tenant con el mismo `phone_e164` que el `wa_id` de la conversación, **sin**
  el `customer_id` de la conversación, ordenados por `created_at, id`, con tope de 10.
- En **toda respuesta de una sola conversación**: el detalle `GET /messaging/conversations/{conversationId}`,
  el `PUT …/customer` de §4.2 y las transiciones que devuelven el resumen (tomar, transferir,
  liberar, resolver, reabrir, pausar y devolver el asistente). El frontend escribe esas respuestas
  en la caché del detalle (`patchConversationCaches`); si vinieran vacías, la línea del encabezado
  se apagaría hasta el siguiente GET. El listado de la bandeja la manda vacía: calcularla por página
  no la necesita ninguna fila (D-D6). Que el mismo DTO lleve una lista vacía en el listado se anota
  en el DTO, para que nadie la «arregle».
- Instagram y conversaciones sin `wa_id`: siempre vacía.
- Puerto nuevo: `IMessagingCustomerDirectory.ListByPhoneAsync(tenantId, waId)` →
  `ICustomerPhoneDirectory.ListByPhoneAsync(tenantId, e164)`; es la consulta de `MatchAsync` sin el
  `TryAdd` que se queda con el primero (`CustomerPhoneDirectory.cs`). Adaptador en Bootstrapper.

### 4.2 Cambiar el cliente de una conversación

```
PUT /api/v1/tenants/{tenantId}/messaging/conversations/{conversationId}/customer
If-Match: "<version>"
{ "customerId": "…" }
```

- Permiso `ConversationManage`, con `If-Match` como tomar o transferir (428 sin él, 412 con versión
  vieja; `ConversationConcurrency.EnsureVersion`).
- El cliente tiene que existir en el tenant (`FindRefsAsync`); si no, `422
  messaging.conversation.customer_not_found`. Cualquier cliente del tenant vale, no sólo los de
  `SharedPhoneCustomers` (un número reciclado puede necesitar uno que no comparte el número).
  Activo o inactivo, igual que la ingesta (D-D7).
- Responde `200` con el `ConversationSummary` del detalle (con la versión nueva y los
  `SharedPhoneCustomers` recalculados), como tomar y transferir (`ConversationAssignment.cs`). El
  mismo cliente que ya tiene → `200` con el resumen, sin evento ni versión nueva (idempotente, D-A2
  del spec 2026-10-10).
- Método nuevo del agregado `Conversation.ChangeCustomer(customerId, actor, now)`: cambia
  `CustomerId`, sube la versión y devuelve el evento `CustomerChanged` (nuevo en
  `ConversationEvent`), con el cliente anterior y el nuevo. Se graba en el hilo como los otros
  eventos de sistema (`ConversationEventRows`), con `occurred_at` distinto si coincide con otro
  (CLAUDE.md, «dos eventos del mismo cambio»).
- `MessageEventDto` del evento lleva el nombre del cliente anterior y del nuevo en
  `Previous`/`Target` para que el hilo diga «Andrés cambió el cliente de LUIS ALBERTO a ZIBA
  TIENDA» sin otra consulta. Si el `MessageEventDto` actual no sirve para nombres de cliente, el
  builder decide la forma y la anota en el DTO.
- Auditoría: acción nueva en `MessagingAuditActions` (`ForEvent` lanza con un tipo que no mapea).
- Aviso en tiempo real como los otros cambios (`notifier.ConversationChangedAsync`).
- **Lo que no se mueve** (D-D8): el BSUID se queda en el cliente que lo tenía; los negocios y las
  decisiones de triage del embudo siguen con el cliente anterior (son hechos de su momento); los
  mensajes ya enviados no cambian. Lo que sí sigue a la conversación porque lee `customer_id`: el
  opt-out por botón y por 131050, el asignado heredero, la búsqueda y el filtro por etiqueta de la
  bandeja y la triage pendiente del embudo.
- La ingesta no lo pisa: actualiza `customer_id` con `COALESCE(customer_id, …)`
  (`InboundIngestion.cs:144`). Una prueba lo amarra.

### 4.3 Iniciar conversación no abre un segundo chat del mismo número

`ConversationOpener.FindAsync` (`ConversationOpener.cs:79-119`) busca hoy por `customer_id`, luego por
BSUID, luego una fila vieja con ese `wa_id` y `user_id IS NULL`. Cambia a:

1. Por BSUID del cliente, si tiene.
2. **Nuevo:** una conversación de la misma conexión con ese `wa_id` y `user_id IS NOT NULL`, la de
   actividad más reciente. Es la que recibe las respuestas de la persona.
3. Por `customer_id`.
4. La fila vieja por `wa_id` con `user_id IS NULL`, como hoy.

El paso 2 va antes que el de `customer_id` para que una conversación huérfana creada por el defecto
no le gane a la que recibe las respuestas. Así B y A comparten el chat del número, que es la decisión
de §2. El `customer_id` de esa conversación no cambia por iniciar; cambiarlo es §4.2.

Las conversaciones huérfanas que el defecto ya creó se quedan como están (historial de lo enviado);
no hay migración que las fusione (D-D10).

El riesgo del número reciclado (el dueño nuevo del número cae en el chat del anterior) es el mismo
que el spec 2026-10-10 ya aceptó para la ingesta, y se arregla con §4.2.

### 4.4 Campañas: un envío por contacto

**El opt-out de marketing es del número, no de la ficha** (decisión del owner, 2026-10-24). Un
cliente cuenta como `MarketingOptOut` si él **o cualquier otro cliente del tenant con el mismo
`phone_e164`** tiene la bandera. Se calcula al leer, en `CustomerAudienceRepository` (que es donde
lo leen `FindContactAsync`, `ListAudienceBatchAsync` y `CountAudienceAsync`), así que vale igual
para el congelado de la campaña, el conteo previo y la revisión que el worker repite por destinatario
antes de enviar. El envío uno a uno que hace una persona (`SendTemplateMessage`, `StartConversation`)
no revisa el opt-out hoy (se apoya en el 131050 de Meta) y este spec no lo cambia. Marcar el
opt-out (`MarkMarketingOptOut*`) sigue marcando sólo la ficha de la conversación: la bandera
guardada es de la ficha y su rastro (`meta` o `button`) también; lo que cambia es cómo se lee. Una
ficha sin `phone_e164` sólo mira su propia bandera.

**Un envío por contacto.** Al congelar la audiencia (`CampaignDispatch.FreezeAsync` → `Seed`), de
los destinatarios no omitidos que comparten `wa_id` o `user_id` queda uno solo `Pending`; los
demás quedan `Skipped` con código nuevo `duplicate_contact`. Las filas omitidas por otro motivo
(`inactive`, `unreachable`, `opted_out`) no cuentan: no le quitan el envío a nadie. Con el opt-out
por número, si uno del número está fuera, todos lo están.

**Quién gana** (decisión del owner, 2026-10-24), porque su `customer_name` es el nombre que lleva la
plantilla:

1. El `customer_id` de la conversación de ese contacto en la conexión de la campaña (el cliente
   principal que eligió el asesor, §4.2), si está entre los no omitidos.
2. Si no, el más viejo: `created_at` y luego id, como D-M7.

**Cómo, sin perder la regla en un reinicio.** El congelado entra por lotes de 500 ordenados por id
(`CustomerAudienceRepository.cs:35-68`) y cada lote es un solo `INSERT … SELECT FROM unnest(…)`
(`CampaignDispatchStore.cs:49-73`), así que el ganador puede llegar en un lote posterior al
perdedor y un `NOT EXISTS` no ve las filas de su propia sentencia. Por eso la regla no se decide
fila por fila al insertar: se inserta todo como hoy y, al terminar el congelado y antes del primer
envío, un solo `UPDATE` sobre las filas de la campaña (ventana por contacto, unida a
`messaging.conversations` por conexión y `wa_id`/`user_id`, con el orden de arriba) pasa a los
perdedores a `Skipped duplicate_contact`. Es idempotente: repetirlo tras un reinicio no cambia
nada. Quien construye verifica en `CampaignDispatch` que ningún destinatario se admite antes de
que el congelado termine; si no es así, el `UPDATE` va en el mismo paso que cierra el congelado.

Dos clientes con el mismo número en el mismo lote, y en lotes distintos, son los dos casos que la
prueba cubre.
- `CampaignFailureReasons` suma el código y su texto: «Otro cliente de la campaña tiene el mismo
  WhatsApp.».
- El conteo de audiencia antes de lanzar (`CountAudienceAsync`) **no** descuenta duplicados en este
  spec (D-D9): el omitido aparece en el detalle de la campaña con su motivo.

## 5. Frontend (`qep-frontend`)

### 5.1 Formulario de cliente

`CustomerForm` (`features/customers/components/customer-form.tsx`) es el mismo componente en sus
cuatro usos (alta, ficha, modal de cotizaciones, ficha de contacto de Messaging), así que lo que
sigue los cubre a todos:

- **R1:** un parser `existingCustomerOf(error)` en `customers.api.ts` para
  `customers.customer.identification_taken`. El del POS (`pos.api.ts:172-185`) filtra por su propio
  código; se generaliza o se copia, a criterio de quien construye. Bajo el número:
  «Ese documento ya es de **{name}**», con enlace a su ficha. Sin `existing` (la carrera de §3.1),
  el texto fijo de hoy.
- **R2:** con tipo y número válidos, y con un retardo al escribir, el formulario consulta §3.2
  (excluyendo al cliente en edición) y muestra bajo el número, con los tokens `warning-*`:
  - `OtherType` o `SameTypeOtherFormat`: «Ya existe **{name}** con {tipo} {número}.» y enlace. No
    bloquea el guardado.
  - `SameIdentification`: el mismo texto de R1, en rojo, antes de guardar.
  - El campo `Field` sólo tiene `hint` (que se oculta con error); hace falta una variante de aviso.
- Un fallo de la consulta no bloquea ni muestra error: el aviso es una ayuda.

### 5.2 Pestaña «Anomalías» en Clientes

- `{ to: '/customers/anomalies', label: 'Anomalías' }` en `CUSTOMER_SECTIONS`
  (`customer-tabs.tsx`), ruta `routes/_authenticated/customers/anomalies.tsx` y página
  `features/customers/pages/anomalies-page.tsx` con `<CustomerTabs />`.
- Dos sub-pestañas con contador (de `counts`): «Mismo número de celular» y «Mismo número de
  documento». Cada grupo es una tarjeta con su clave y una tabla de sus clientes (nombre, tipo y
  número, CUC, teléfono, estado, creado), cada fila enlazada a la ficha.
- Texto de ayuda en «Mismo número de celular»: compartir número está permitido (una persona y su
  empresa); la lista es para revisar. El estado vacío lo dice en positivo.

### 5.3 Encabezado del hilo

- Con `sharedPhoneCustomers` no vacía, una línea bajo el nombre: «Este número también es de
  **{nombre}**» (o «… de {nombre} y {n} más»), cada nombre enlazado a su ficha.
- «Cambiar cliente…» en el menú «Más acciones» (sólo con `ConversationManage`): diálogo con los
  `sharedPhoneCustomers` primero y un buscador de clientes debajo. El buscador usa el listado de
  Customers, que exige `CustomerRead`; sin ese permiso el diálogo muestra sólo los compartidos, que
  vienen en la conversación (D-D11). El rol `advisor` tiene los dos. Manda §4.2 con la versión
  actual; un 412 relee y avisa, como tomar.
- El hilo dibuja el evento `CustomerChanged`.

### 5.4 Campañas

`duplicate_contact` en `RECIPIENT_REASONS` (`utils/campaign-draft.ts`) con el texto de §4.4.

## 6. Pruebas (TDD, RED antes de GREEN)

Por pieza, como mínimo:

- §3.1: alta y edición con documento ajeno → 422 con `existing` (también dueño inactivo e
  incompleto); edición sin cambiar documento no consulta; carrera simulada → 422 con `existing`.
- §3.2: cada `match`; `excludeCustomerId`; otro tenant → 403; número vacío → 422 con `errors`; tope.
- §3.3: grupos de las dos clases, `counts` en cero, paginado, `truncated`, otro tenant → 403.
- §4.1: detalle con y sin compartidos; Instagram vacío; listado vacío.
- §4.2: cambio, 412, 428, cliente de otro tenant → 422 `customer_not_found`, idempotente, evento en
  el hilo, auditoría, y un entrante posterior no lo pisa.
- §4.3: iniciar con B cuando A ya tiene la conversación del número → misma conversación.
- §4.4: dos clientes con el mismo número en la audiencia, en el mismo lote y en lotes distintos →
  un `Pending` y un `Skipped duplicate_contact`; gana el de la conversación y, sin ella, el más
  viejo; el `UPDATE` repetido no cambia nada; uno inactivo no le quita el envío al otro.
- Opt-out por número: con opt-out en A, B (mismo número) sale `opted_out` en una campaña
  `MARKETING`, en el conteo previo y en el envío uno a uno; en una `UTILITY` no.
- D-D8: tras cambiar a B el cliente de la conversación de A, un entrante del mismo BSUID por otra
  conexión resuelve a A. La prueba deja amarrado ese comportamiento elegido.
- Frontend: pruebas de los componentes de §5 con las respuestas de arriba.

Pruebas de integración de Messaging con el stub de auth y `$env:Seed__Enabled = "false"`;
acotadas a los módulos tocados, sin correr la suite completa.

## 7. Despliegue

- Una migración en Customers (índice de §3.2) y una en Messaging: `customer_created_at` en
  `campaign_recipients`, que es lo que permite desempatar por «el más viejo» (§4.4) sin que Messaging
  lea el esquema de Customers. `CustomerChanged` es un valor nuevo del enum que se guarda en
  `details` de `messages`; si un `CHECK` limita los tipos, va en la misma migración.
- Dos destinatarios con el mismo `wa_id` y distinto BSUID (número reciclado) también cuentan como el
  mismo contacto: es consecuencia de que el opt-out y el chat sean del número (§2). El BSUID es único
  por cliente, así que el agrupamiento es por `wa_id`.
- Backend y frontend juntos: un frontend viejo ignora `existing`, `sharedPhoneCustomers` y
  `duplicate_contact` sin romperse (son campos y códigos nuevos), pero un evento `CustomerChanged`
  sin dibujar sale como evento desconocido en el hilo.

## 8. Lo que sigue: fusión de clientes (spec aparte)

La pestaña de §5.2 es donde va a vivir la fusión. Lo que ese spec tiene que decidir, para no
olvidarlo: qué cliente sobrevive, qué pasa con el CUC y el documento del otro, y cómo se mueven
cotizaciones, pedidos, conversaciones, ventas del POS, negocios y decisiones del embudo, notas,
etiquetas, direcciones, espejo de Shopify, BSUID e IGSID, en varios `DbContext` sin una sola
transacción.

## 9. Decisiones que tomó el spec (a ratificar)

| Id | Decisión | Alternativa |
|---|---|---|
| D-D1 | R1 conserva el código `customers.customer.identification_taken` y agrega `existing` | Un código nuevo (rompería el diccionario del frontend) |
| D-D2 | R2 se avisa **antes** de guardar, con una consulta aparte | Un `warnings` en la respuesta del `POST` (avisa cuando ya está creado) |
| D-D3 | R2 compara el número normalizado (sin puntos, guiones ni espacios) y también avisa del mismo tipo con otro formato | Comparar tal cual (`92-1396892` y `921396892` no se verían) |
| D-D4 | Anomalías se calculan al consultar; no hay tabla ni estado «revisado» | Guardar los grupos y poder descartarlos |
| D-D5 | Importación y POS no avisan R2 | Avisar por fila en la importación |
| D-D6 | `SharedPhoneCustomers` sólo en el detalle | También en cada fila de la bandeja |
| D-D7 | Se puede elegir cualquier cliente del tenant, activo o no | Sólo los que comparten número |
| D-D8 | Cambiar el cliente no mueve el BSUID, ni los negocios ni el triage. **Ratificado por el owner el 2026-10-24 («así por ahora»):** el cliente principal vale para esa conversación; otra línea del tenant vuelve a resolver al dueño del BSUID | Mover el BSUID al nuevo cliente |
| D-D9 | El conteo previo de la campaña no descuenta duplicados | Descontarlos en `CountAudienceAsync` |
| D-D10 | Las conversaciones huérfanas que ya creó el defecto 2 no se fusionan | Una migración que las una |
| D-D11 | Sin `CustomerRead`, «Cambiar cliente…» sólo ofrece los que comparten número | Un buscador propio bajo Messaging |

## 10. Decisiones del owner del 2026-10-24

1. El opt-out de marketing vale para el número (§4.4).
2. La plantilla lleva el nombre del cliente principal de la conversación; sin conversación, el del
   más viejo (§4.4).
3. El cliente principal elegido vale para esa conversación, por ahora (D-D8).

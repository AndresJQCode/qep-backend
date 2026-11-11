# Borrar clientes desde la lista — diseño (backend)

Fecha: 2026-11-10 · Repo: `qep-backend` · Spec hermano: `qep-frontend/docs/superpowers/specs/2026-11-10-borrar-clientes-design.md`.
Este documento manda el contrato HTTP.

Estado: aprobado por el owner el 2026-11-10. Las decisiones de §3.1 son del owner. Las de §3.2 son **decisión del
asistente**: se tomaron al escribir y el owner las tiene que confirmar. §8 lista lo que queda abierto.

Reglas del workspace que aplican: TDD obligatorio (RED antes que GREEN, con evidencia literal); no se inventan
campos, estados, rutas, permisos ni códigos de error; ante discrepancia entre este documento y el código, gana el
código y se corrige el documento.

## 1. Intención y porqué

La lista de clientes se llena de basura por dos vías:

1. Clientes creados por error a mano.
2. Sobre todo, contactos que entraron en masa al conectar WhatsApp en **coexistencia**: `smb_app_state_sync` llama a
   `EnsureByPhoneAsync` por cada contacto del teléfono (`WebhookDeliveryProcessor.cs:276-289` →
   `CustomerWhatsAppDirectory.cs:78-111`), que crea un cliente `Incomplete` con auditoría `CreatedFromMessaging`.
   Muchos no son clientes: familiares, proveedores, números viejos.

Hoy no hay forma de sacarlos. `Deactivate` (`Customer.cs:971`) los oculta, pero siguen existiendo. El owner quiere un
**borrado real y definitivo**, en lote, desde `/customers`.

## 2. Objetivos, no objetivos, anti-objetivos

**Objetivos**

- Borrar uno o varios clientes (hasta 100 por pedido) con un solo request.
- Bloquear el borrado de los clientes con historia comercial o financiera que no se puede perder.
- Borrado **parcial**: se borra lo que se puede y la respuesta dice cuáles no y por qué.
- No dejar ids huérfanos en otros módulos, salvo los que este documento conserva a propósito (§4.3).

**No objetivos**

- Borrar «todo lo que coincide con el filtro». El borrado recibe siempre ids explícitos (§3.1, D-7).
- Impedir que un contacto borrado vuelva a crearse. Si escribe, se sincroniza o llega un eco, se crea de nuevo como
  `Incomplete`, como hoy (§3.1, D-4).
- Papelera, deshacer o borrado lógico.
- Atender solicitudes de eliminación de datos personales (habeas data). La auditoría y el consentimiento conservan
  datos personales a propósito (D-10).
- Borrar desde el detalle del cliente (el frontend usa la lista; el endpoint sirve igual para uno).

**Anti-objetivos** (si pasa esto, falló aunque funcione)

- Que desaparezca una cotización, un pedido, un préstamo, un contrato de servicio o un cobro.
- Que desaparezca un registro de consentimiento de datos.
- Que una venta del POS deje de mostrar a quién se vendió.
- Que una conversación quede en la bandeja apuntando a un cliente inexistente.
- Que un cliente se borre después de que, en paralelo, se le creó un pedido (ver §5, concurrencia).

## 3. Decisiones

### 3.1 Del owner

| ID | Decisión |
|---|---|
| D-1 | El borrado es real y definitivo. Se avisa antes, no hay vuelta atrás. |
| D-2 | **Bloquean** el borrado: cotizaciones, pedidos, préstamos, contratos de servicio. |
| D-3 | **No bloquean**: ventas del POS (guardan la foto del cliente), negocios del pipeline (se borran), consentimiento de datos (se conserva). |
| D-4 | Un contacto borrado que vuelve a escribir o a sincronizarse se crea de nuevo. Sin lista negra. |
| D-5 | Al borrar el cliente se borran sus conversaciones y mensajes. |
| D-6 | Selección mixta: se borran los que se pueden; se informa cuáles no y por qué. |
| D-7 | Ids explícitos, por página (máximo 100 por request). Nada de borrado por filtro. |
| D-8 | Permiso nuevo `customers.customer.delete`, de entrada sólo en el rol de sistema `admin`; el owner lo recibe por ahí, porque su membresía siempre conserva `admin` (no existe un rol `owner`, ver §8 D1). |
| D-10 | El borrado es **limpieza de la lista**, no un mecanismo de habeas data. Por eso la auditoría (A-8) y el consentimiento (A-5) conservan la foto con datos personales. Atender una solicitud de eliminación de datos es otro trabajo. |
| D-9 | Un cliente con alguna conversación **bloqueada** (`Conversation.BlockedAt` no nulo, `Conversation.cs:118-126`) no se borra. Para borrarlo, primero se desbloquea. Así el bloqueo nunca se pierde en silencio y no hace falta lista negra (D-4). |

### 3.2 Del asistente (a confirmar)

| ID | Decisión | Razón |
|---|---|---|
| A-1 | Endpoint `POST /api/v1/tenants/{tenantId}/customers/bulk-delete`, no `DELETE` con cuerpo. | `DELETE` con cuerpo lo descartan proxies y no hay precedente en el repo; el más cercano es el batch `POST /products/store-visibility` (`ProductEndpoints.cs:87-95`). |
| A-2 | Sin `If-Match`. | `CustomerListItemDto` no trae `version` y el borrado no depende del contenido del cliente. El riesgo de concurrencia que importa (alguien le crea un pedido) se cubre en §5. |
| A-3 | Un bloqueo por cliente no falla el request: el request responde 200 con `deleted` y `blocked`. | D-6. |
| A-4 | Las ventas del POS ponen `customer_id` en `null`; la foto (`CustomerName`, `CustomerIdentificationType`, `CustomerIdentificationNumber`, `PosSale.cs:40-48`) se queda. | D-3. La respuesta del POS ya sale de la foto (`PosSaleResponses.cs:40`). |
| A-5 | `data_consents`: se quita el `RESTRICT` (`AddCustomerDataConsents.cs:56-61`) y la fila queda con `customer_id` colgante. Se agregan columnas de foto `customer_name` y `customer_phone_e164`, que se llenan **al borrar** (y, si el builder lo ve barato, al registrar). | D-3. La fila ya tiene `WaId`/`ChannelUserId` y `ShownText`, pero una prueba de consentimiento tiene que decir de quién es sin depender de otra tabla. |
| A-6 | Los clientes fusionados (`MergedIntoId` = el borrado) se borran también. | Son lápidas sin referencias propias (la fusión ya las movió al sobreviviente) y quedarían apuntando a nada. |
| A-7 | Lo que no bloquea y no se borra pone el id en `null` (ver tabla §4.3). | No dejar ids muertos. |
| A-8 | El borrado se audita, uno por cliente, con la foto (nombre, teléfono, documento) y los conteos de lo que se llevó. | El cliente desaparece; la auditoría es lo único que queda. Precedente: auditoría en el handler. |
| A-9 | Los objetos de R2 de los adjuntos se borran en segundo plano, con reintento, después del commit. | Un fallo de R2 no puede dejar el borrado a medias ni frenarlo. |
| A-10 | Destinatarios de campañas se conservan con `customer_id` nulo (§4.3). | Las estadísticas de campañas pasadas no cambian. |
| A-11 | El `PreviousCustomerId` de eventos de conversaciones que sobreviven queda como historia. | Igual criterio que `data_consents`: historia, no referencia viva. |
| A-12 | La carrera con mensajes entrantes **también** se previene (§5). | Es un anti-objetivo de §2: una conversación apuntando a un cliente inexistente. |

## 4. Comportamiento

### 4.1 Contrato HTTP

`POST /api/v1/tenants/{tenantId}/customers/bulk-delete` (mismo grupo que el resto, `CustomerEndpoints.cs:30`) ·
permiso `customers.customer.delete`.

```json
{ "customerIds": ["<guid>", "..."] }
```

Validación (422 de FluentValidation, mapa `errors`): `customerIds` no vacío, máximo 100, sin `Guid.Empty`. Los
duplicados se ignoran.

Respuesta 200:

```json
{
  "deleted": ["<guid>"],
  "blocked": [
    { "customerId": "<guid>", "name": "Ana Pérez", "reasons": ["quotations", "orders"] }
  ],
  "notFound": ["<guid>"]
}
```

- `reasons` ⊆ `quotations | orders | loans | serviceContracts | blockedContact`, en ese orden.
- `notFound`: no existe en el tenant (incluye los ya borrados). No es un error: con dos pestañas abiertas pasa.
- 403 sin permiso. El nombre exacto de `serviceContracts` y de los códigos queda a criterio del builder si choca con
  una convención existente; el frontend los toma de aquí.

### 4.2 Qué bloquea (D-2)

Un cliente está bloqueado si tiene **al menos una** fila en:

| Motivo | Tabla |
|---|---|
| `quotations` | `quotations.quotations.client_id` |
| `orders` | `quotations.orders.client_id` |
| `loans` | `loans.loans.customer_id` |
| `serviceContracts` | `billing.services.customer_id` (y `billing.charges`, que cuelgan de un servicio) |
| `blockedContact` | `messaging.conversations` del cliente con `blocked_at` no nulo (D-9) |

Sin importar el estado (borrador, anulado, cancelado: todos bloquean). El conteo se arma extendiendo el patrón de
`ICustomerMergeReferences.CountAsync` (`Application/ICustomerMergeReferences.cs`), que ya conoce estas columnas
(`Bootstrapper/CustomerMergeReferences.cs:50-131`).

### 4.3 Qué pasa con cada referencia de un cliente borrado

| Referencia | Acción |
|---|---|
| `customers.customer_addresses`, `customer_notes`, `customer_tags` | Se borran (ya tienen `CASCADE`). |
| `customers.data_consents` | Se conserva. Foto llenada, FK sin `RESTRICT` (A-5). |
| `customers.customers` con `merged_into_id` = borrado | Se borran (A-6). |
| `messaging.conversations.customer_id` | La conversación **se borra**, con sus mensajes y todo lo que cuelga de ella: adjuntos (R2, A-9), corridas del asistente/chatbot, acuses, mensajes programados, notas, etc. El builder enumera las tablas desde `MessagingDbContext`. |
| `messaging.campaign_recipients.customer_id` | Se **conservan** las filas con `customer_id` en `null` (la columna pasa a nullable; la unicidad `(campaign_id, customer_id)` tiene que seguir funcionando con nulls). Las estadísticas de campaña se cuentan en vivo de esta tabla (`CampaignQueries.cs:96-103`); borrarlas bajaría en silencio los números de campañas terminadas. Un destinatario pendiente de una campaña en `Sending` se marca como fallido u omitido (el builder elige el estado que ya exista); el despachador tolera `customer_id` nulo (A-10). |
| `messaging.campaigns.audience_customer_ids` | Se saca el id del arreglo en campañas `Draft`/`Scheduled`. |
| `pipeline.deals`, `deal_quotations`, `deal_orders`, `triage_decisions` | Se borran (D-3). `deal_quotations`/`deal_orders` sólo pueden existir con cotización/pedido, que ya bloquea. |
| `pos.sales.customer_id` | `null` (A-4). |
| `billing.customer_profiles` | Se borra (un perfil sin servicios; con servicios ya bloquea). |
| `shopify.orders.qep_customer_id` | `null`. |
| `messaging.conversation_events` (`CustomerChanged.PreviousCustomerId`, `Conversation.cs:351-362`) de conversaciones que **sobreviven** | Se conserva como historia, con el id colgante (A-11). |
| `geography.unresolved_city_occurrences.customer_id` | Se borran las filas. |

Si el builder encuentra otra columna que guarde un id de cliente y no esté aquí, la agrega con el criterio de la tabla
y lo anota en §8; si el criterio no es obvio, es decisión del owner.

### 4.4 Re-creación (D-4)

No se toca `EnsureByPhoneAsync`. Un contacto borrado que vuelve a aparecer se crea con un `CustomerId` nuevo y una
conversación nueva. Es el comportamiento esperado; no hay que «arreglarlo».

## 5. Restricciones

- **Concurrencia con los bloqueos.** Entre la verificación y el borrado, otro request puede crear una cotización o un
  pedido para ese cliente. Las referencias son Guids sin FK entre módulos, así que la base no lo frena. El builder
  elige el mecanismo (una transacción de Postgres que abarque los esquemas con bloqueo de la fila del cliente, un
  `SELECT … FOR UPDATE` que los creadores de cotización/pedido también tomen, u otro), pero el anti-objetivo de §2 es
  obligatorio y lleva prueba de integración.
- **Concurrencia con Mensajería (A-12).** Un mensaje entrante, un eco o una sincronización de contactos que llega a
  mitad del borrado puede colgar un mensaje o una conversación nueva del cliente que se está borrando (por
  `EnsureByPhoneAsync` o por una conversación existente). El mecanismo de bloqueo cubre también esas rutas de
  escritura del procesador de webhooks, con su propia prueba de integración. El resultado aceptable es que el mensaje
  termine en una conversación nueva con un cliente nuevo (D-4), nunca colgado del borrado.
- **Atomicidad por cliente.** Un cliente se borra entero o no se borra. Lo que falle a mitad (salvo R2, A-9) revierte
  ese cliente. Si un cliente falla por algo inesperado, el builder decide entre cortar el lote o seguir; si sigue, la
  respuesta necesita un campo más y hay que avisar al frontend.
- **Módulos.** Cada módulo limpia lo suyo a través de un contrato en su `Application`, cableado en `Bootstrapper` (como
  `CustomerMergeReferences`). `ArchitectureTests` tiene que seguir en verde; Application no referencia EF ni Npgsql.
- **Rendimiento.** 100 clientes con cientos de mensajes cada uno es el caso normal. Nada de N+1 por mensaje.
- **Migraciones.** Quitar el `RESTRICT` de `data_consents` y agregar las columnas de foto. El orden de deploy es backend
  primero; el frontend sin el endpoint sólo no muestra la acción (ver spec del frontend).
- **Permiso.** `customers.customer.delete` se declara en `CustomersPermissions.cs`, se agrega al rol `admin` (el owner lo recibe por su membresía)
  (roles de código, `QepServiceCollectionExtensions.cs`) y aparece en el editor de roles sólo con el módulo de
  clientes activo, como los demás.

## 6. Pruebas mínimas

- Unitarias del handler: validación (vacío, >100, `Guid.Empty`, duplicados), cada motivo de bloqueo, mezcla
  borrados/bloqueados/no encontrados.
- Integración (Testcontainers, sólo el módulo tocado mientras se itera): un cliente con conversación, mensajes,
  adjunto, negocio de pipeline, venta POS, consentimiento, dirección y nota → tras borrar, cada fila de §4.3 quedó como
  dice la tabla. Un cliente con pedido → bloqueado y nada se tocó. Concurrencia de §5.
- Arquitectura: `CustomersLayerTests` y los de los módulos tocados en verde.
- Endpoint: 403 sin permiso, 200 con la forma de §4.1.

## 7. Lo que queda a criterio del builder

- Mecanismo de concurrencia (§5) y de borrado en Mensajería (SQL masivo vs. agregado).
- Nombre y forma del contrato entre módulos.
- Si la foto de `data_consents` se llena también al registrar el consentimiento.
- El texto exacto de la auditoría.

## 8. Abierto

Lo que apareció al construirlo (2026-11-11). Donde el plan y el código discreparon, ganó el código.

### Discrepancias plan/spec contra el código

- **D1.** No hay rol de sistema `owner` (`SystemRoleKeys`: admin, advisor, billing, cashier). El permiso va sólo a `admin`; el owner lo tiene por su membresía. D-8 y §5 corregidos arriba.
- **D2.** `messaging.conversation_events` no existe: los eventos son filas de `messaging.messages` con `details`. Los de conversaciones borradas se van por `CASCADE`; los `CustomerChanged` de conversaciones que sobreviven quedan como historia.
- **D3.** `audit.entries` no tiene columna de detalle: la foto del cliente borrado va en `changed_fields` como `clave=valor` (R5). Si el owner no lo acepta hace falta una columna en Audit.
- **D4.** Al borrar las conversaciones, los conteos `Delivered`, `Read` y `Replied` de campañas pasadas bajan (se calculan uniendo `messages` y `conversations`); `Total`, `Sent`, `Failed`, `Skipped` y `Pending` no. Aceptado por el owner (R2). Congelarlos exigiría un cambio de esquema en Messaging.
- **D5 / R4.** Una campaña `Draft`/`Scheduled` cuya única audiencia es el cliente conserva el id: `CK_campaigns_audience` exige audiencia no vacía y vaciarla daría 500. El congelado ignora ids que no existen.
- **D6.** Columnas no listadas en §4.3, ver abajo.
- **D7.** `data_consents.customer_id` es `NOT NULL` y su FK la creó una migración a mano: se quita la FK y la fila conserva el id, con `customer_name` y `customer_phone_e164` como foto tomada al borrar.
- **D8.** El comentario de `CustomerEndpoints` «No hay DELETE» queda reemplazado por la decisión del owner del 2026-11-10.

### Columnas encontradas más allá de §4.3

| Columna / tabla | Acción |
|---|---|
| `billing.customer_messages.customer_id` | Sin tocar: sólo existe con cobros, y los cobros ya bloquean. |
| `automations.conversation_runs`, `run_units`, `http_calls` | Se borran con la conversación. |
| `messaging.automation_inbox` | Se borra con la conversación. |
| `pipeline.deal_stage_history` | Se borra con el negocio. |
| `pipeline.deals.origin_conversation_id` de negocios de **otros** clientes | Pasa a `NULL` al borrarse la conversación de origen (R11, A-7: sin ids muertos). |
| `shopify.city_corrections`, `city_correction_jobs` | Quedan como están (R12): un trabajo pendiente ya termina en `CustomerNotFound` y la fusión tampoco los mueve. |
| `customers.data_consents.conversation_id` | Sin tocar: es historia, como la fila misma (A-5, A-11). Queda apuntando a una conversación borrada y no se «arregla»: es la prueba de por dónde respondió el titular. |

### Escritores que no toman el candado (P13 / R13)

El POS y los escritores del pipeline no toman `CustomerReferenceLock`. Una venta del POS creada mientras se borra puede conservar el id borrado (`pos.sales.customer_id`); la foto del cliente en la venta sigue mostrándose. Aceptado (R13).

Lo mismo con el pipeline: un negocio que el triage crea en la ventana de la carrera puede quedar con `pipeline.deals.customer_id` apuntando al cliente borrado, y el tablero lo muestra sin cliente («negocio fantasma»). Aceptado bajo R13.

### Escritores que sí toman el candado (revisión final)

- **R7 revocado.** El perfil de cobro también toma el candado: `PUT /customers/{id}/profile` crea un `CustomerBillingProfile` sin servicio, así que la premisa de R7 («el perfil nace con el servicio») era falsa. `ServiceContractsUnitOfWork` toma el compartido y comprueba que el cliente exista también para un perfil nuevo; si el borrado ganó, `CustomerNotFound`.
- **Fusión (I-1).** La fusión toma el candado **exclusivo** de todos sus clientes (el que queda, los fusionados y su cadena), en orden de id, al empezar su transacción: fusión y borrado se serializan sólo por candados consultivos y ninguno deja referencias a un cliente borrado.
- **Vínculo del chatbot (I-2).** `LinkCustomerAsync` toma el compartido antes de colgar la conversación; si el cliente ya no existe, la unidad del motor se reintenta. El borrado bloquea con `FOR UPDATE` las filas de los clientes borrables después de leer los bloqueos y antes de la foto de consentimientos: un consentimiento o una revocación que llegue en ese tramo espera y no encuentra al cliente.

### Auditoría (R10)

El port de consentimientos devuelve un conteo **por cliente**, y la auditoría de cada cliente escribe `dataConsents=<n>` en `changed_fields`: deja constancia de que el consentimiento se conservó (D-3).

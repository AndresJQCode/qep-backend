# Embudo de ventas: slice 1, el asesor

**Fecha:** 2026-10-13
**Módulos:** Pipeline (módulo nuevo: dominio, persistencia, worker, endpoints, permisos). Quotations (tres
eventos de outbox nuevos). Tenancy (clave de módulo `pipeline` y migración del `CHECK`). Authorization (tres
permisos y sus políticas). Bootstrapper (adaptadores de los puertos de lectura sobre Messaging, Customers y
membresías). **Frontend:** `qep-frontend`, slice aparte (§9).
**Estado:** diseño aprobado por el owner en conversación el 2026-10-13, incluidas las resoluciones D25–D36
de lo que había quedado pendiente, el resumen de negocios en la ficha del cliente (D36), el tope del triage
(D37) y su carrera entre asesores (D38).
**Base de investigación:** `../reports/Embudos de ventas en CRM.md` (carpeta `reports/` de la raíz del
workspace). Se cita como contexto; la autoridad sigue siendo el código.
**Ramas:** `feature/embudo-de-ventas` desde `develop` `7ad43ef` en `qep-backend`.

## 1. Contexto y alcance

Hoy el asesor no tiene dónde ver «qué negocios tengo abiertos y en qué punto está cada uno». La
conversación vive en la bandeja de WhatsApp (Messaging), la cotización en Quotations y el pedido en
Orders, pero nada los junta en una oportunidad con etapa, monto y fecha esperada de cierre.

Este slice construye el **embudo del asesor**: un kanban de negocios (*deals*), el negocio enlazado al
chat y a la cotización, y la señal de negocio estancado. Los reportes del gerente (conversión por etapa,
forecast) son el slice 2, pero **el historial de etapas se captura desde el día 1**: sin él, el slice 2 no
tendría con qué calcular (el informe de investigación, sección «Reportes y forecast exigen historial
append-only desde el primer día», llega a la misma conclusión).

**QEP va a ser «AI first».** No hay integración con un LLM hoy y este slice no la agrega, pero deja
preparados los puntos donde una IA va a intervenir: cada decisión del asesor es una acción explícita
(crear o descartar desde el chat; enlazar una cotización; mover de etapa; perder con motivo) y cada cambio
de etapa queda con el **tipo de actor** que lo hizo. Un slice futuro de IA propone esas mismas acciones y el
asesor las confirma; no hace falta rediseñar el modelo.

**Dentro:**

- Módulo `Pipeline` con un embudo por tenant, etapas configurables y catálogos de motivos.
- Negocios: crear, editar, mover entre etapas, ganar, perder con motivo, reabrir.
- Integración con Quotations por outbox: cotización enviada, cotización convertida, pedido anulado.
- Triage de conversaciones de WhatsApp: crear negocio o descartar desde el panel del chat.
- Resumen de negocios del cliente para la ficha que se abre desde la conversación (D36).
- Señal de estancado calculada al leer.
- Historial append-only de cambios de etapa.

**Fuera** (§8): reportes de conversión y forecast, reglas por etapa, varios embudos, sugerencias de IA,
frontend.

## 2. Hechos verificados en el código

Lo que el diseño asume, con el lugar donde se confirmó. Donde el nombre real difiere del borrador del
diseño, manda el real.

| # | Hecho | Dónde |
| --- | --- | --- |
| H1 | Tenancy **no publica ningún evento** al activar un módulo. `ChangeTenantModulesHandler` crea o cambia la fila de `tenant_modules`, escribe `TenantChange` y audita con `IAuditRecorder`; nada va al outbox. El signup tampoco: `TenantModuleDefaults` sólo devuelve la lista de claves. El único evento de dominio de `Tenant` es `TenantLogoUpdatedDomainEvent`. | `src/Modules/Tenancy/Modules.Tenancy.Application/ChangeTenantModules.cs:79-118`; `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenantModuleDefaults.cs:15`; `src/Modules/Tenancy/Modules.Tenancy.Domain/Tenant.cs:190` |
| H2 | Las claves de módulo son una lista cerrada (`TenantModuleKeys.All`) con dependencias declaradas, y la tabla tiene un `CHECK` con el texto de cada clave. Un módulo nuevo es «otra migración que cambia el `CHECK`, a propósito». | `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs:29-65`; `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs:110` |
| H3 | El enforcement por módulo es `TenantModuleGuard.EnsureEnabledAsync` sobre `ITenantModules`, y responde 403 `tenancy.module_not_enabled`. | `src/Modules/Tenancy/Modules.Tenancy.Application/TenantModuleGuard.cs:12-14`; `ITenantModules.cs:9` |
| H4 | `Quotation` tiene `ClientId` (`Guid`), `AdvisorId` (`MemberId`, id de membresía), `QuotationNumber`, `Total` (`Subtotal + TaxAmount`, lo facturado), `Currency` (ISO 4217 de toda la cotización), `SentAt` y `Version`. | `src/Modules/Quotations/Modules.Quotations.Domain/Quotation.cs:58,98,102,156,191,209,235` |
| H5 | `Sent` lo pone `SendQuotationHandler` (`quotation.Send`, luego `SaveChangesAsync`). Reenviar es el mismo `Send` desde `Sent`. | `src/Modules/Quotations/Modules.Quotations.Application/SendQuotation.cs:66,128,144`; `Quotation.cs:754` |
| H6 | `Converted` lo pone `ConvertQuotationToOrderHandler` (`quotation.ConvertToOrder`) en la misma transacción que crea el `Order`. Se puede convertir **desde `Draft`**, sin haber enviado. | `ConvertQuotationToOrder.cs:137,146,181`; `Quotation.cs:847,876-883` |
| H7 | Anular un pedido es `CancelOrderHandler` (`order.Cancel`, luego `SaveChangesAsync`). Se anula desde `Pending` o `Approved`, no desde `Invoiced`, y **la cotización sigue `Converted`**. Hoy anular no escribe outbox («Sin evento de outbox, igual que aprobar»). | `src/Modules/Quotations/Modules.Quotations.Application/CancelOrder.cs:19,50,59`; `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs:150-176` |
| H8 | Una cotización `Draft` o `Sent` **se edita** (`EnsureEditable`) y **puede cambiar de cliente** (`ChangeClient`), así que su `Total` y su `ClientId` cambian después de enviada sin pasar por `Send`. | `Quotation.cs:711-734,1051-1059` |
| H9 | Quotations escribe eventos con un publicador en Infrastructure que agrega un `QuotationsOutboxMessage` a `QuotationsDbContext.Outbox` (tabla compartida `platform.outbox_messages`), y se commitea con el `SaveChangesAsync` del cambio. Nombres con el patrón `quotations.<recurso>.<hecho>.v1`; el payload se lee por nombre y es contrato. | `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderPaymentProofEventPublisher.cs:8-16,46-56`; `IOrderPaymentProofEventPublisher.cs:5-28`; `QuotationsDbContext.cs:665` |
| H10 | Hay dos patrones de consumidor del outbox. Storage (`PaymentProofMoveProcessor`) hace anti-join contra su inbox (`storage.inbox_messages`, clave consumidor + id) y guarda efecto e inbox en el mismo `SaveChanges`, **sin reclamo**. El patrón **con reclamo** es el de Identity: `ClaimedOutboxConsumer` + `IdentityInboxClaims` (lease por intento, nunca abandona un mensaje, un scope por mensaje); Notifications tiene el mismo con `InboxClaims`. | `src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofMoveProcessor.cs:18-20,42-43,74-81`; `src/Modules/Identity/Modules.Identity.Infrastructure/Messaging/ClaimedOutboxConsumer.cs:8-52`; `IdentityInboxClaims.cs:21`; `src/Modules/Notifications/Modules.Notifications.Infrastructure/Persistence/InboxClaims.cs:15` |
| H11 | `Conversation` (Messaging) tiene `CustomerId` (`Guid?`, lo fija la ingesta), `Status` (`Open`/`Resolved`), `LastInboundAt`, `LastMessageAt` + `LastMessageDirection` (el último mensaje en cualquier dirección) y `LastActivityAt`. **No existe `LastOutboundAt`.** La clave es el BSUID por número: índice único `(connection_id, user_id)`; una conversación resuelta se reabre (`Reopen`), no se crea otra. Ya hay índice `(tenant_id, customer_id, last_activity_at)`. | `src/Modules/Messaging/Modules.Messaging.Domain/Conversation.cs:48,59,68,72,76,80,88,183`; `MessageEnums.cs:3-7`; `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingDbContext.cs:115,127` |
| H12 | Ningún módulo lee hoy de Messaging: todos los adaptadores de Bootstrapper con «Messaging» en el nombre son puertos **de** Messaging hacia otros. El precedente de leer otro módulo desde Bootstrapper es Reporting: `QuotationsReportSource` lee `QuotationsDbContext` y es el adaptador de un puerto de `Modules.Reporting.Application`. | `src/Bootstrapper/MessagingCustomerDirectory.cs:9`; `src/Bootstrapper/QuotationsReportSource.cs:17-21` |
| H13 | Customers tiene `CustomerCompleteness { Complete, Incomplete }` y `Customer.IsComplete`. El puerto que ya expone nombre y completitud por id es `ICustomerWhatsAppDirectory.FindRefsAsync`; Messaging lo consume como `CustomerRefDto(Id, Name, IsComplete)`. | `src/Modules/Customers/Modules.Customers.Domain/CustomerCompleteness.cs:9-13`; `Customer.cs:245-247`; `src/Modules/Customers/Modules.Customers.Application/ICustomerWhatsAppDirectory.cs:38`; `src/Modules/Messaging/Modules.Messaging.Application/MessagingDtos.cs:14` |
| H14 | Un permiso nuevo son cuatro piezas en Bootstrapper: la constante en el módulo, su `PermissionDefinition` (con `RequiredModules`), su lugar en el `RoleDefinition` de fábrica y su política en `AddAuthorization`. Los permisos de un módulo comercial declaran `RequiredModules: [ModuleKeys.<Modulo>]`. Los roles de fábrica son cuatro: `admin`, `advisor`, `billing` y `cashier`. | `src/Bootstrapper/QepServiceCollectionExtensions.cs:46,817-818,904-905,954-955,985-986,1277,1379-1385,1681-1684`; `src/Modules/Authorization/Modules.Authorization.Application/RoleCatalog.cs:20-33` |
| H15 | Un módulo con auditoría atómica declara su propio recorder sobre una proyección de `audit.entries` en su `DbContext`; lo que escribe un worker va con `bySystem` y actor vacío. | `src/Modules/Messaging/Modules.Messaging.Application/IMessagingAuditRecorder.cs:3-11`; `src/Modules/Integrations/Modules.Integrations.Application/ConnectionWriteSupport.cs:17` |
| H16 | Las rutas de módulo cuelgan de `/api/v1/tenants/{tenantId:guid}/<modulo>`. | `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs:24` |
| H17 | Cada módulo tiene su `<Modulo>LayerTests.cs`; el de Messaging sólo admite Tenancy y Audit entre los módulos y prohíbe referenciar Integrations, Customers y Storage. | `tests/ArchitectureTests/ArchitectureTests/MessagingLayerTests.cs:10-64` |
| H18 | La ingesta actual **siempre** deja la conversación con cliente: si la conversación no existe o no tiene `CustomerId`, llama a `EnsureAsync`, que busca por BSUID, luego por teléfono y si no encuentra **crea un cliente `Incomplete`**. Sólo una fila vieja sin BSUID que no volvió a recibir un entrante puede quedar con `CustomerId` nulo. | `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookDeliveryProcessor.cs:204-213`; `src/Modules/Customers/Modules.Customers.Application/ICustomerWhatsAppDirectory.cs:29-31`; `Conversation.cs:46-48` |
| H19 | Agregar la clave `pipeline` a `/modules` no rompe el frontend. En `develop` de `qep-frontend`, las claves que no conoce se descartan al leer la respuesta; `main` no consume `/modules`. | `qep-frontend` `src/features/auth/services/tenancy.api.ts:164` (`develop`) |

## 3. Decisiones

| ID | Decisión | Motivo |
| --- | --- | --- |
| D1 | Módulo nuevo **`Pipeline`**, no «Sales». | «Sale» ya es `PosSale` en Pos, y en el negocio «venta» es el pedido. Un tercer significado confundiría código y conversación. |
| D2 | Cuatro capas (`Domain`, `Application`, `Infrastructure`, `Api`), `PipelineLayerTests.cs`, `IPipelineAuditRecorder` propio, schema `pipeline`. | Convención del repo (H15, H17). El `IAuditRecorder` compartido está ligado a `TenancyDbContext`. |
| D3 | Capacidad **`pipeline`** en `tenancy.tenant_modules`: `TenantModuleKeys.Pipeline`, último de `All`, fuera de `DefaultForNewTenants`; migración que cambia el `CHECK`. Todo handler llama a `TenantModuleGuard.EnsureEnabledAsync`. | Mismo camino que `messaging` (H2, H3). Se prende por tenant desde la consola de operador. |
| D4 | El embudo por defecto y sus catálogos se crean **de forma idempotente en el primer acceso**, con índice único por tenant. | Tenancy no avisa cuando se activa un módulo (H1), así que no hay dónde engancharse. Crear al leer, con `ON CONFLICT DO NOTHING` sobre el índice único, aguanta dos peticiones simultáneas. |
| D5 | Pipeline es dueño de: embudo y etapas, `Deal`, historial `deal_stage_history` (append-only), decisiones de triage y catálogos de motivos de pérdida y de descarte por tenant. | Lo que el módulo decide vive en el módulo. |
| D6 | Quotations publica **tres eventos de outbox** (§4.5) en la misma transacción que el cambio, y un worker de Pipeline los consume con **reclamo + inbox propio**, replicando `ClaimedOutboxConsumer` de Identity. | El patrón con reclamo es el de Identity (H10); el de Storage es anti-join sin reclamo. Con reclamo, un mensaje que falla no frena el lote y nunca se abandona. |
| D7 | Rechazado: un puerto **síncrono** desde Quotations hacia Pipeline. | Serían dos unidades de trabajo sin compensación: el mismo defecto del registro de tenant que deja un usuario sin membresía (`CLAUDE.md`, «Defecto conocido sin cerrar»). |
| D8 | Rechazado: **calcular la etapa al leer** a partir del estado de las cotizaciones. | No deja historial ni eventos, y el slice 2 depende del historial. |
| D9 | Pipeline → Messaging por un **puerto de lectura** (`IPipelineConversationDirectory`); Pipeline → Customers por otro (`IPipelineCustomerDirectory`); nombres de dueños por `IPipelineMemberNames`. Adaptadores en Bootstrapper. Pipeline **nunca** referencia el `Application` de otro módulo salvo Tenancy y Audit, como Messaging. | Regla de capas (H17). El adaptador de conversaciones lee `MessagingDbContext` como `QuotationsReportSource` lee `QuotationsDbContext` (H12); el de clientes envuelve `ICustomerWhatsAppDirectory.FindRefsAsync` (H13). |
| D10 | Un embudo por tenant en este slice, pero las etapas son **hijas del embudo** (modelo HubSpot). | Varios embudos después no necesitan migración: es quitar una restricción, no mover columnas. |
| D11 | Etapas: exactamente una `Won`, una `Lost` y al menos una `Open`. Las abiertas van ordenadas; las de cierre, fijas al final. Cada etapa: nombre, orden, probabilidad (%), tipo `Open`/`Won`/`Lost`, días de estancamiento (sólo abiertas). **Exactamente una abierta** marcada como «etapa de cotización». | Lo que necesitan el kanban, el forecast del slice 2 y el avance automático por cotización (D17). |
| D12 | Renombrar conserva el id. No se borra una etapa con negocios. | El historial apunta a ids; renombrar no reescribe el pasado. |
| D13 | Movimiento **libre** entre etapas abiertas: adelante, atrás o saltando. | Las reglas por etapa quedan fuera (§8); el informe de investigación avisa que una regla sólo vale si la aplica la API, y este slice no la tiene. |
| D14 | Perder exige un motivo del catálogo (nota opcional). Ganar a mano se permite. | Ventas por Pos o por teléfono que nunca pasan por cotización. |
| D15 | Reabrir lleva el negocio a la **última etapa abierta del historial**. | Vuelve a donde estaba, no al principio. |
| D16 | Un cliente puede tener **varios negocios abiertos**. | Un mismo cliente compra cosas distintas en paralelo. |
| D17 | `quotation.sent` enlaza y avanza; `quotation.converted` enlaza si hace falta y gana; `order.cancelled` reabre (§4.4). El avance a la etapa de cotización es **sólo hacia adelante**. | Que la máquina nunca le deshaga al asesor una decisión que él tomó. |
| D18 | Con cotización principal, el monto y la moneda del negocio **siguen al `Total` y la `Currency`** de esa cotización y son de sólo lectura; sin principal, se escriben a mano. | Una sola fuente del monto. `Total` es lo facturado (H4). |
| D19 | Todo cambio de etapa, de cualquier actor, escribe su fila de historial **en la misma transacción**, con tipo de actor `User`/`System` guardado como texto. | `Ai` se agrega después sin migración. Es la base del slice 2 y del slice de IA. |
| D20 | Una conversación está **pendiente de triage** si está abierta, su cliente no tiene negocio abierto y no hay una decisión posterior a su último entrante. | Un descarte no es para siempre: si el cliente vuelve a escribir un mes después, la conversación vuelve a aparecer. |
| D21 | El enlace negocio ↔ chat es **por cliente**: los chats de un negocio son las conversaciones de su cliente. | La conversación ya tiene `CustomerId` (H11); no hace falta una tabla de enlace. |
| D22 | Estancado se calcula **al leer**, sin worker. | Es una comparación de fechas; un worker sólo agregaría un estado que se desactualiza. |
| D23 | Tres permisos: `pipeline.deal.read`, `pipeline.deal.manage`, `pipeline.settings.manage`. Leer ve **todos** los negocios del tenant; el tablero arranca en `owner=me` con interruptor. | El filtro por dueño es foco, no seguridad. |
| D24 | Sumas del tablero **agrupadas por moneda**. | Una cotización puede ir en dólares (H4); sumar pesos con dólares da un número que no significa nada. |
| D25 | `quotation.converted` aplica **primero las mismas reglas de enlace que `quotation.sent`** (§4.4, pasos 1–4) y después gana. El handler es idempotente. | Una cotización se convierte desde `Draft` sin haberse enviado (H6), y un reintento puede procesar el `converted` antes que su `sent`. Con las mismas reglas, el resultado no depende del orden de llegada. |
| D26 | Se acepta la **deriva del monto**: si una cotización `Sent` se edita, el monto del negocio no cambia hasta el siguiente reenvío (`sent`) o la conversión (`converted`). Sin cuarto evento. | Editar una enviada sin reenviarla deja al cliente con otro documento; el monto que importa es el que el cliente recibió. Un cuarto evento por cada edición sería ruido en el outbox. |
| D27 | **Cambio de cliente** de una cotización enlazada (H8): en el siguiente evento, si el cliente de la cotización ya no es el del negocio, se **desenlaza**; si era la principal, el negocio queda sin principal y el monto pasa a manual **conservando el último valor**. Después se vuelven a aplicar las reglas de enlace para el cliente nuevo. | El enlace es por cliente (D21): una cotización de otro cliente no puede seguir sosteniendo el monto de este negocio. Conservar el valor evita que el negocio quede en cero de un momento a otro. |
| D28 | Principal **anulada o vencida**: nada automático; el asesor decide (otra principal, perder o seguir). | Ningún CRM de la investigación lo hace: una cotización vencida suele reemplazarse por otra, no cerrar el negocio. |
| D29 | El triage tiene **sólo dos acciones: crear y descartar**. Se quita «enlazar». Enlazar cotizaciones vive en el negocio (`quotations/{id}/link`). | Contradicción con D20: una conversación sólo está pendiente si su cliente no tiene negocio abierto, así que no hay a qué enlazarla. |
| D30 | Dependencias del módulo: **`pipeline → [customers]`**. Sin la capacidad `messaging`, el triage queda vacío y oculto; sin `quotations`, simplemente no llegan eventos. | Un negocio siempre tiene cliente. La bandeja y las cotizaciones enriquecen el embudo pero no son condición para usarlo (ventas por teléfono o Pos, D14). |
| D31 | Semillas **editables**, creadas con el embudo (D4). Etapas: Nuevo 10 %, 3 días → Calificado 25 %, 5 días → Cotizado 50 %, 7 días (etapa de cotización) → Negociación 75 %, 7 días → Ganado 100 % → Perdido 0 %. Motivos de pérdida: Precio, Competencia, Sin respuesta, Sin presupuesto, Otro. Motivos de descarte: Soporte, Recompra, Proveedor, Spam, Otro. | Sin motivos no se puede perder (D14) ni descartar; el tenant arranca con un embudo usable y lo ajusta en Ajustes. |
| D32 | Roles de fábrica: `admin` con los tres permisos; `advisor` con `pipeline.deal.read` y `pipeline.deal.manage`. `billing` y `cashier`, ninguno. | Las claves reales son `admin`, `advisor`, `billing` y `cashier` (H14). La configuración del embudo cambia el trabajo de todos los asesores; la decide quien administra, como las respuestas rápidas. |
| D33 | Volumen del tablero: las columnas `Won` y `Lost` muestran los negocios cerrados en los **últimos 30 días**; cada columna trae **como máximo 200 tarjetas**, siempre con el `Count` real y las sumas sobre todos sus negocios, no sólo los 200. | El tablero es para trabajar lo vivo; el histórico es del slice 2. El conteo real deja que la pantalla diga «200 de 340» sin otra llamada. |
| D34 | Un negocio manual **puede nacer sin monto**: cuenta en `Count` y queda fuera de las sumas. La moneda por defecto es la del tenant (`ITenantDefaultCurrency`). | Al abrir una oportunidad desde el chat casi nunca se sabe el monto todavía; obligarlo produce montos inventados que ensucian el forecast. |
| D35 | Las conversaciones **sin `CustomerId`** (filas viejas sin BSUID) quedan **fuera del triage**. | La ingesta actual siempre crea o encuentra un cliente, `Incomplete` si hace falta (H18): el caso sin cliente sólo existe en filas viejas, y desaparece con su siguiente entrante. |
| D36 | **Resumen de negocios del cliente** para la ficha que se abre desde la conversación: `GET pipeline/customers/{customerId}/summary` (§6.4). Lo sirve **Pipeline**, no Customers; la pantalla hace una segunda llamada. | Customers no puede referenciar Pipeline (regla de capas). Sin la capacidad o sin permiso responde 403 y la ficha oculta la sección. |
| D37 | **Tope del triage:** `GET triage` y `ListOpenAsync` aplican el criterio de D33: como máximo 200 conversaciones pendientes, las de último entrante más reciente primero, y el `Count` real del total. | El contador de la bandeja tiene que decir la verdad aunque la lista se corte; sin tope, un tenant con miles de conversaciones abiertas vuelve pesada cada carga. |
| D38 | **Carrera del triage:** cada decisión guarda el `LastInboundAt` de la conversación contra el que se tomó (`decided_against_inbound_at`, no nulo; sin entrante, `1970-01-01T00:00:00Z`). Un índice único `IX_triage_decisions_conversation_inbound` sobre `(tenant_id, conversation_id, decided_against_inbound_at)` hace que la segunda decisión simultánea falle; la unidad de trabajo de Pipeline la traduce **por nombre de índice** a `pipeline.triage.already_decided` (422). El negocio que esa decisión creaba va en el mismo `SaveChanges` y se revierte con ella. | Dos asesores que deciden la misma conversación a la vez pasan los dos la comprobación de «pendiente»: sólo la base puede elegir uno. Un mensaje nuevo del cliente es un instante nuevo y permite decidir otra vez (D20). La columna no admite nulos porque Postgres no considera iguales dos `NULL` en un índice único. |

## 4. Dominio

### 4.1 Embudo y etapas

Agregado `SalesPipeline` (`pipeline.pipelines`): `id`, `tenant_id`, `name`, `version`, `created_at`,
`updated_at`. Índice único `IX_pipelines_tenant` sobre `tenant_id` (D4, D10).

Entidad hija `PipelineStage` (`pipeline.stages`): `id`, `pipeline_id`, `name`, `position`, `probability`
(0–100), `kind` (`Open`/`Won`/`Lost`, texto), `stall_days` (`int?`, sólo `Open`), `is_quotation_stage`.

Reglas de `SalesPipeline.ReplaceStages(...)` (D11, D12), todas `pipeline.stage.layout_invalid` salvo la
última:

- Exactamente una `Won` y una `Lost`; al menos una `Open`.
- Las `Open` primero, en el orden recibido; `Won` y `Lost` al final.
- Exactamente una `Open` con `is_quotation_stage`.
- `stall_days` > 0 en las abiertas y nulo en las de cierre.
- `probability` entre 0 y 100.
- Una etapa que estaba y ya no viene se borra; si tiene negocios → `pipeline.stage.has_deals`.

Lo hace el agregado porque necesita ver la colección entera: una etapa sola no sabe si es la única `Won`.

La creación en el primer acceso (D4) siembra las etapas y los dos catálogos de D31.

### 4.2 Negocio

Agregado `Deal` (`pipeline.deals`):

| Campo | Regla |
| --- | --- |
| `id`, `tenant_id`, `pipeline_id` | |
| `customer_id` | referencia blanda a Customers; un cliente `Incomplete` se acepta |
| `owner_member_id` | membresía del tenant |
| `title` | 1–200 después de `Trim` |
| `amount`, `currency` | `numeric(14,2)?` y ISO 4217; siguen a la principal (D18); sin principal, el monto puede ser nulo (D34) |
| `expected_close_date` | `date?` |
| `stage_id`, `stage_entered_at` | `stage_entered_at` alimenta «días en la etapa» sin leer el historial |
| `primary_quotation_id` | `Guid?` |
| `source` | `Manual` / `Conversation` / `Quotation`, texto |
| `origin_conversation_id` | `Guid?`, sólo con `source = Conversation` |
| `loss_reason_id`, `loss_note` | sólo cerrado en `Lost`; nota hasta 500 |
| `closed_at` | `null` mientras está abierto; alimenta la ventana de 30 días (D33) y el contexto de la ficha (§6.4) |
| `last_changed_at` | cualquier cambio del negocio, etapa incluida; entra en el cálculo de estancado |
| `created_at`, `updated_at`, `version` | concurrencia optimista |

`IsClosed` e `IsWon` se derivan del `kind` de la etapa; no se guardan.

Transiciones (cada una escribe su fila de historial, §4.3):

- `MoveTo(stage)`: sólo entre abiertas y con el negocio abierto. A una de cierre → `pipeline.deal.stage_not_open`
  (para eso están `Win` y `Lose`). Cerrado → `pipeline.deal.already_closed`. A la misma etapa → nada.
- `Win(actor)`: a la etapa `Won`. Cerrado → `pipeline.deal.already_closed`.
- `Lose(reason, note, actor)`: a la etapa `Lost`. Sin motivo → `pipeline.deal.loss_reason_required`; motivo
  inactivo o de otro tenant → `pipeline.deal.loss_reason_not_found`.
- `Reopen(actor)`: a la última etapa abierta del historial (D15); si esa etapa ya no existe, a la primera
  abierta. Abierto → `pipeline.deal.not_closed`. Limpia motivo, nota y `closed_at`.
- `SetAmount(...)` con principal → `pipeline.deal.amount_follows_quotation`.
- `UnlinkQuotation(quotationId)` (D27): si era la principal, la limpia y el monto queda manual con su último
  valor.

Los códigos son `DomainException` → 422 por el mapeo central.

### 4.3 Historial

`pipeline.deal_stage_history`, append-only, sin `UPDATE` ni `DELETE` desde el código:

| Columna | |
| --- | --- |
| `id`, `tenant_id`, `deal_id`, `pipeline_id` | |
| `from_stage_id` | `null` en la fila de creación |
| `to_stage_id` | |
| `occurred_at` | UTC |
| `actor_kind` | `User` / `System`, texto (D19; `Ai` en el slice de IA) |
| `actor_member_id` | `null` con `System` |
| `amount`, `currency`, `probability` | foto en ese momento |

Crear un negocio escribe la primera fila. Índice `(tenant_id, deal_id, occurred_at)`.

### 4.4 Cotizaciones del negocio

`pipeline.deal_quotations` es la foto que Pipeline guarda de cada cotización que le llegó por evento:
`tenant_id`, `quotation_id`, `deal_id` (`Guid?`, sin enlazar = `null`), `customer_id`, `quotation_number`,
`total`, `currency`, `status` (`Sent`/`Converted`), `order_id`, `order_cancelled_at`, `updated_at`. Índice
único `(tenant_id, quotation_id)`: una cotización pertenece a lo sumo a un negocio. Así el tablero muestra
el número de la principal sin leer Quotations.

Reglas al consumir (D17, D25, D27), en este orden:

**Enlace** (pasos comunes a `quotation.sent` y `quotation.converted`)

1. Se actualiza la foto (número, total, moneda, cliente).
2. **Cambio de cliente** (D27): si la cotización está enlazada a un negocio de **otro** cliente, se
   desenlaza (`UnlinkQuotation`) y se sigue como si nunca hubiera estado enlazada.
3. Enlace: si la cotización ya está enlazada, ese negocio. Si no y el cliente tiene **exactamente un**
   negocio abierto, se enlaza a ése. Si no tiene ninguno, se crea un negocio con `source = Quotation`,
   dueño = `AdvisorId` de la cotización, título = número de la cotización, primera etapa abierta. Si tiene
   **varios**, queda sin enlazar para que el asesor elija desde el negocio.
4. Si el negocio no tenía principal, ésta pasa a serlo. Si es la principal, monto y moneda pasan a su
   `Total` y `Currency` (D18). Este paso es el único momento en que el monto se refresca (D26).

**`quotation.sent`**: los pasos de enlace y, después, si el negocio está abierto y **antes** de la etapa de
cotización, avanza a ella con actor `System`. Si está en ella o después, o está cerrado, no se mueve. Un
reenvío (H5) repite el evento: todo es idempotente y sólo refresca la foto y el monto.

**`quotation.converted`** (D25): los pasos de enlace y, después, si la cotización es la principal de un
negocio abierto, el negocio pasa a `Won` con actor `System`. Si ya está ganado, nada (idempotente). Si quedó
sin enlazar (el cliente tiene varios negocios), no se gana nada: el asesor decide.

**`order.cancelled`**: si el negocio está ganado y su principal es la cotización de ese pedido, se reabre
(D15) con actor `System`. Si no, sólo se anota `order_cancelled_at` en la foto.

Una principal anulada o vencida no dispara nada (D28): Quotations no emite evento por eso y Pipeline no lo
necesita.

Un evento de un tenant **sin** el módulo `pipeline` activo se marca procesado sin efecto. Activar el módulo
después no rellena el pasado.

### 4.5 Eventos de Quotations

Un publicador nuevo en Quotations, calcado de `IOrderPaymentProofEventPublisher` (H9):
`IQuotationLifecycleEventPublisher` en `Modules.Quotations.Application` y `QuotationLifecycleEventPublisher`
en `Infrastructure/Persistence`, que agrega un `QuotationsOutboxMessage` antes del `SaveChangesAsync` del
handler.

| Evento | Lo escribe | Payload (contrato, se lee por nombre) |
| --- | --- | --- |
| `quotations.quotation.sent.v1` | `SendQuotationHandler`, antes de `SendQuotation.cs:144` | `tenantId`, `quotationId`, `quotationNumber`, `clientId`, `advisorMemberId`, `total`, `currency`, `occurredAt` |
| `quotations.quotation.converted.v1` | `ConvertQuotationToOrderHandler`, antes de `ConvertQuotationToOrder.cs:181` | los mismos + `orderId`, `orderNumber` |
| `quotations.order.cancelled.v1` | `CancelOrderHandler`, antes de `CancelOrder.cs:59` | `tenantId`, `orderId`, `quotationId`, `occurredAt` |

`converted` lleva el payload completo de `sent` a propósito: con él, Pipeline aplica las reglas de enlace sin
haber visto nunca el `sent` (D25). Los nombres son hechos de Quotations, no órdenes a Pipeline: mañana los
puede consumir otro módulo. Hay que actualizar el comentario «Sin evento de outbox» de `CancelOrder.cs:19`.
Sin migración en Quotations: la tabla es `platform.outbox_messages`.

## 5. Triage y estancado

### 5.1 Triage

Pendiente (D20, D35, D38): conversación `Open` en Messaging, **con `CustomerId`**, cuyo cliente **no** tiene
negocio abierto, y sin decisión en `pipeline.triage_decisions` tomada contra su `LastInboundAt` actual (es
decir, la última decisión tiene `decided_against_inbound_at` anterior a ese entrante, o no hay decisión). Sin
entrante, se compara contra `1970-01-01T00:00:00Z`. Sin la capacidad `messaging` no hay conversaciones y el
triage queda vacío (D30).

`pipeline.triage_decisions`: `id`, `tenant_id`, `conversation_id`, `customer_id`, `kind`
(`Created`/`Dismissed`, texto), `deal_id?`, `dismiss_reason_id?`, `decided_at`, `decided_against_inbound_at`
(D38), `actor_kind`, `actor_member_id`. Append-only; vale la última. Índice único
`IX_triage_decisions_conversation_inbound` sobre `(tenant_id, conversation_id, decided_against_inbound_at)`.

Acciones desde el panel del chat (`POST triage/{conversationId}`), sólo dos (D29):

- **Crear negocio** (`create`): cliente de la conversación, dueño = quien llama, `source = Conversation`,
  `origin_conversation_id`, primera etapa abierta, sin monto (D34). Título opcional; sin título, el nombre
  del cliente.
- **Descartar** (`dismiss`) con motivo del catálogo → `pipeline.triage.dismiss_reason_required` sin motivo.

Conversación que ya no está pendiente al crear o descartar → `pipeline.triage.not_pending` (otro asesor se
adelantó). Si los dos deciden en el mismo instante, el índice de D38 deja pasar uno y el otro recibe
`pipeline.triage.already_decided`; su negocio no queda. Conversación inexistente en el tenant → 404 `pipeline.triage.conversation_not_found`.

Enlazar una **cotización** a un negocio no es triage: vive en el negocio
(`deals/{dealId}/quotations/{quotationId}/link`).

El contador de pendientes de la bandeja sale de `GET triage` (`count`).

### 5.2 Estancado

Un negocio abierto está estancado si `now − última actividad > stall_days` de su etapa, con
**última actividad** = máximo entre `deal.last_changed_at` y el `LastMessageAt` más reciente de las
conversaciones de su cliente. `LastMessageAt` cubre entrantes y salientes (H11): no hace falta un
`LastOutboundAt`. Tiempo transcurrido en UTC, no días calendario del tenant.

### 5.3 Puertos

```csharp
// Modules.Pipeline.Application. Adaptador en Bootstrapper sobre MessagingDbContext (H12).
public interface IPipelineConversationDirectory
{
    /// Abiertas y con cliente (D35), para el triage.
    Task<IReadOnlyList<PipelineConversation>> ListOpenAsync(Guid tenantId, CancellationToken cancellationToken);

    /// Por cliente: último mensaje (estancado) y los chats de un negocio (D21). Usa IX_conversations_tenant_customer_activity.
    Task<IReadOnlyDictionary<Guid, PipelineCustomerActivity>> FindActivityAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    Task<PipelineConversation?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);
}

public sealed record PipelineConversation(
    Guid Id, Guid? CustomerId, bool IsOpen, DateTimeOffset? LastInboundAt, DateTimeOffset? LastMessageAt);
public sealed record PipelineCustomerActivity(Guid CustomerId, DateTimeOffset? LastMessageAt, IReadOnlyList<Guid> ConversationIds);

// Adaptador en Bootstrapper sobre ICustomerWhatsAppDirectory.FindRefsAsync (H13).
public interface IPipelineCustomerDirectory
{
    Task<IReadOnlyDictionary<Guid, PipelineCustomerRef>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);
}

public sealed record PipelineCustomerRef(Guid Id, string Name, bool IsComplete);

// Mismo criterio que IMessagingMemberNames.
public interface IPipelineMemberNames
{
    Task<IReadOnlyDictionary<Guid, string>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken);
}
```

Quien llama se resuelve a membresía con `IMembershipDirectory` (Tenancy), como `CallerMembership` en
Messaging. Si `messaging` no está activo para el tenant, el triage no llama al puerto de conversaciones.

## 6. API (BFF)

Grupo `/api/v1/tenants/{tenantId:guid}/pipeline` (H16). Cada handler revalida tenant y permiso antes del
repositorio y llama a `TenantModuleGuard.EnsureEnabledAsync`: otro tenant → **403, nunca 404**. Toda
escritura sobre un negocio recibe `version` y devuelve el **negocio completo**; versión vieja → 412.

| Método | Ruta | Permiso | Respuesta |
| --- | --- | --- | --- |
| `GET` | `/board?owner=me\|all` | `pipeline.deal.read` | `200 PipelineBoardDto` |
| `GET` | `/deals/{dealId:guid}` | `pipeline.deal.read` | `200 DealDetailDto` |
| `POST` | `/deals` | `pipeline.deal.manage` | `201 DealDetailDto` |
| `PATCH` | `/deals/{dealId:guid}` | `pipeline.deal.manage` | `200 DealDetailDto` |
| `POST` | `/deals/{dealId:guid}/move` | `pipeline.deal.manage` | `200 DealDetailDto` |
| `POST` | `/deals/{dealId:guid}/win` | `pipeline.deal.manage` | `200 DealDetailDto` |
| `POST` | `/deals/{dealId:guid}/lose` | `pipeline.deal.manage` | `200 DealDetailDto` |
| `POST` | `/deals/{dealId:guid}/reopen` | `pipeline.deal.manage` | `200 DealDetailDto` |
| `POST` | `/deals/{dealId:guid}/quotations/{quotationId:guid}/link` | `pipeline.deal.manage` | `200 DealDetailDto` |
| `POST` | `/deals/{dealId:guid}/quotations/{quotationId:guid}/primary` | `pipeline.deal.manage` | `200 DealDetailDto` |
| `GET` | `/customers/{customerId:guid}/summary?conversationId=` | `pipeline.deal.read` | `200 CustomerDealsSummaryDto` (§6.4) |
| `GET` | `/triage` | `pipeline.deal.read` | `200 TriageListDto` |
| `POST` | `/triage/{conversationId:guid}` (`action`: `create` \| `dismiss`) | `pipeline.deal.manage` | `200 TriageResultDto` |
| `GET` | `/settings/stages` | `pipeline.deal.read` | `200 PipelineStagesDto` |
| `PUT` | `/settings/stages` | `pipeline.settings.manage` | `200 PipelineStagesDto` |
| `GET` | `/settings/loss-reasons`, `/settings/dismiss-reasons` | `pipeline.deal.read` | `200 ReasonListDto` |
| `PUT` | `/settings/loss-reasons`, `/settings/dismiss-reasons` | `pipeline.settings.manage` | `200 ReasonListDto` |

`GET` de ajustes usa `deal.read`: el tablero necesita las etapas y el modal de pérdida, los motivos.

```csharp
/// BFF: TODAS las columnas, incluso en cero. Una columna que desaparece obliga a la pantalla a conocer las
/// etapas para dibujar la que falta (precedente: ReportStatusSliceDto). Las sumas van por moneda (D24): una
/// cotización puede ir en USD y sumar pesos con dólares no significa nada.
public sealed record PipelineBoardDto(IReadOnlyList<BoardColumnDto> Columns, string Owner);

/// D33: Deals trae como máximo 200; Count y Totals son de TODA la columna, para que la pantalla diga
/// «200 de 340» sin otra llamada. Won y Lost sólo cuentan los cerrados en los últimos 30 días.
public sealed record BoardColumnDto(
    Guid StageId, string Name, string Kind, int Probability, bool IsQuotationStage,
    int Count, IReadOnlyList<CurrencyTotalDto> Totals, IReadOnlyList<DealCardDto> Deals);

/// Weighted = Σ amount × probability / 100, calculado en el servidor: la pantalla no repite la regla. Un
/// negocio sin monto (D34) no entra en ninguna suma.
public sealed record CurrencyTotalDto(string Currency, decimal Amount, decimal Weighted);

/// BFF: nombre del dueño, completitud del cliente, días en la etapa e IsStalled ya resueltos; la pantalla no
/// tiene con qué calcularlos (no ve membresías ni conversaciones).
public sealed record DealCardDto(
    Guid Id, string Title, Guid CustomerId, string CustomerName, bool CustomerIsComplete,
    decimal? Amount, string Currency, Guid OwnerMemberId, string OwnerName, bool IsStalled,
    int DaysInStage, string? PrimaryQuotationNumber, long Version);
```

`DealDetailDto` lleva además: las cotizaciones enlazadas, las del mismo cliente **sin enlazar** (para el
`link`), los ids de las conversaciones del cliente (D21), el historial de etapas y si el monto es editable.
Los enums viajan con su nombre (`Open`, `Won`, `Conversation`).

`TriageListDto` lleva `Enabled` (`false` sin la capacidad `messaging`, D30), `Count` y los ítems. Sin
`messaging`, `POST triage` responde 403 `tenancy.module_not_enabled`.

`PUT settings/stages` recibe la colección entera y ordenada (`id` nulo = etapa nueva) con la `version` del
embudo. Los catálogos de motivos se reemplazan enteros igual; un motivo que deja de venir **se desactiva**,
no se borra, porque el historial lo nombra.

### 6.1 Permisos

`PipelinePermissions.DealRead = "pipeline.deal.read"`, `DealManage = "pipeline.deal.manage"`,
`SettingsManage = "pipeline.settings.manage"`. Cada uno con las cuatro piezas de H14:
`PermissionDefinition` con `RequiredModules: [ModuleKeys.Pipeline]`, su lugar en los roles de fábrica (D32:
`admin` los tres; `advisor` `DealRead` y `DealManage`) y **su política en `AddAuthorization`**; sin la
política el síntoma es 500, no 403.

### 6.2 Errores

| Código | Status |
| --- | --- |
| `pipeline.deal.not_found`, `pipeline.triage.conversation_not_found`, `pipeline.customer.not_found` | 404 (id inexistente en el tenant) |
| `pipeline.deal.loss_reason_required`, `pipeline.deal.loss_reason_not_found` | 422 |
| `pipeline.deal.already_closed`, `pipeline.deal.not_closed`, `pipeline.deal.stage_not_open` | 422 |
| `pipeline.deal.amount_follows_quotation` | 422 |
| `pipeline.deal.quotation_not_linked`, `pipeline.deal.quotation_not_found`, `pipeline.deal.quotation_customer_mismatch` | 422 |
| `pipeline.deal.customer_not_found`, `pipeline.deal.owner_not_member` | 422 |
| `pipeline.stage.layout_invalid`, `pipeline.stage.has_deals` | 422 |
| `pipeline.triage.not_pending`, `pipeline.triage.already_decided` (D38), `pipeline.triage.dismiss_reason_required` | 422 |
| `validation.failed` con el mapa `errors` | 422 |
| `concurrency.conflict` | 412 |
| `tenancy.module_not_enabled` | 403 |

FluentValidation en todo texto libre aunque el dominio valide: título, nota de pérdida, nombres de etapa y
de motivo. El dominio da el código; el validador, el campo. `action` del triage distinto de `create` o
`dismiss` → `validation.failed` con `errors.action`.

### 6.3 Auditoría

Por `IPipelineAuditRecorder`, en la misma transacción: negocio creado, movido, ganado, perdido, reabierto,
editado; cotización enlazada, desenlazada (D27) o marcada principal; decisiones de triage; cambios de etapas
y catálogos. Lo que escribe el worker va con `bySystem` y actor vacío (H15).

### 6.4 Resumen de negocios del cliente (D36)

La ficha del cliente que se abre al hacer clic en su nombre dentro de una conversación muestra sus negocios.
Lo sirve Pipeline, no Customers: Customers no puede referenciar Pipeline, así que la pantalla hace **una
segunda llamada** al abrir la ficha.

`GET /api/v1/tenants/{tenantId:guid}/pipeline/customers/{customerId:guid}/summary?conversationId={id}`

- Permiso `pipeline.deal.read`. Sin la capacidad `pipeline` → 403 `tenancy.module_not_enabled`; sin permiso
  → 403; otro tenant → 403, nunca 404. En los tres casos **la ficha oculta la sección**.
- Cliente inexistente en el tenant → 404 `pipeline.customer.not_found`.
- `conversationId` es opcional. Sin él, `Triage` es `null`. Con uno que no existe en el tenant o que es de
  otro cliente → 404 `pipeline.triage.conversation_not_found`.

```csharp
/// BFF (D36): la ficha del cliente dentro de la conversación dibuja esta sección sin lógica propia. La
/// pantalla no ve etapas, membresías, conversaciones ni decisiones de triage, así que el servidor resuelve
/// todo: nombre de la etapa y del dueño, estancado, días en la etapa, el último cerrado como contexto y qué
/// acciones de triage ofrecer. Viaja aparte de la ficha de Customers porque Customers no puede referenciar
/// Pipeline.
public sealed record CustomerDealsSummaryDto(
    Guid CustomerId,
    IReadOnlyList<CustomerOpenDealDto> OpenDeals,
    CustomerClosedDealDto? LastClosedDeal,
    ConversationTriageStateDto? Triage);

public sealed record CustomerOpenDealDto(
    Guid Id, string Title, Guid StageId, string StageName, decimal? Amount, string Currency,
    Guid OwnerMemberId, string OwnerName, bool IsStalled, int DaysInStage, string? PrimaryQuotationNumber,
    long Version);

/// El cerrado más reciente, ganado o perdido, como contexto («perdido hace 2 meses por precio»). Uno solo:
/// el histórico completo es del slice 2.
public sealed record CustomerClosedDealDto(
    Guid Id, string Title, string Outcome /* Won | Lost */, DateTimeOffset ClosedAt, string? LossReasonName);

/// Status: Pending | Decided | NotApplicable. NotApplicable = la conversación no está abierta o el cliente ya
/// tiene un negocio abierto. AvailableActions viene ya filtrado por estado y por permiso (sin
/// pipeline.deal.manage, vacío): la pantalla dibuja los botones que vienen, nada más. Sin la capacidad
/// messaging, Status es NotApplicable.
public sealed record ConversationTriageStateDto(
    Guid ConversationId, string Status, IReadOnlyList<string> AvailableActions /* create | dismiss */,
    TriageDecisionDto? LastDecision);

public sealed record TriageDecisionDto(string Kind, DateTimeOffset DecidedAt, string? DismissReasonName);
```

Los negocios abiertos van ordenados por `last_changed_at` descendente. Un cliente `Incomplete` se resume
igual.

## 7. Pruebas

TDD estricto: RED antes que GREEN, con la salida literal de ambos.

- **Unitarias de dominio:** reglas de `ReplaceStages` (una `Won`, una `Lost`, al menos una `Open`, una
  etapa de cotización, cierre al final, borrar con negocios); transiciones de `Deal` (mover entre abiertas,
  mover a cierre rechazado, perder sin motivo, ganar a mano, reabrir a la última abierta del historial y a la
  primera si ya no existe); monto de sólo lectura con principal; `UnlinkQuotation` de la principal deja el
  monto manual con su último valor; negocio sin monto; cálculo de estancado con y sin mensajes; pendiente de
  triage (descarte anterior y posterior al último entrante, conversación sin cliente excluida).
- **Unitarias del consumidor:** las cuatro ramas de enlace; avance sólo hacia adelante; reenvío
  idempotente; `converted` sin `sent` previo enlaza y gana (D25); `converted` repetido no duplica nada;
  `converted` de una no principal no gana; cambio de cliente desenlaza y reaplica las reglas (D27);
  `order.cancelled` reabre sólo el ganado por esa cotización; tenant sin módulo → procesado sin efecto.
- **Integración (harness de Pipeline):** cada endpoint; 403 desde otro tenant; 403 sin permiso; 403
  `tenancy.module_not_enabled`; 412 por versión; 422 `validation.failed` con `errors`; primer acceso siembra
  las etapas y los catálogos de D31 una sola vez aunque lleguen dos peticiones; tablero con todas las columnas
  en cero, sumas por moneda, tope de 200 con `Count` real y ventana de 30 días en `Won`/`Lost`; triage vacío
  y `Enabled: false` sin `messaging`; triage sólo acepta `create` y `dismiss`; el worker consumiendo los tres
  eventos; monto que sigue a la principal; reapertura por pedido anulado; auditoría escrita.
- **Integración del resumen del cliente (§6.4):** lista los negocios abiertos del cliente con etapa, dueño,
  estancado y número de la principal; trae el último cerrado como contexto (ganado y perdido con su motivo);
  estado de triage `Pending` con `create` y `dismiss`, `Decided` después de un descarte, `NotApplicable` con
  un negocio abierto; acciones vacías sin `pipeline.deal.manage`; 404 con una conversación de otro cliente;
  403 sin la capacidad `pipeline`, sin `pipeline.deal.read` y desde otro tenant.
- **Quotations:** una prueba por evento que verifique que se escribe en el outbox **en la misma
  transacción** que el cambio (si el `SaveChanges` falla, no hay evento).
- **`PipelineLayerTests.cs`**: Application sólo referencia Tenancy y Audit entre los módulos; ninguna capa
  referencia Messaging, Customers ni Quotations.

Gotchas del repo que aplican: el harness fija explícitamente las claves de proveedor (no las hereda de
user-secrets), pide los permisos por `X-Permissions` en modo stub (el stub sólo concede los de tenancy) y
activa el módulo como `PosApiHarness.EnablePosAsync`. Al agregar un campo requerido, barrer las pruebas de
integración por cuerpos crudos, no sólo los harness.

## 8. Fuera de alcance

- Reportes de conversión y forecast (slice 2). El historial ya queda capturado.
- Reglas por etapa (campos obligatorios, transiciones prohibidas).
- Varios embudos por tenant (D10 deja el modelo listo).
- Reacción automática a una principal anulada o vencida (D28).
- Sugerencias de IA (el slice de IA propone las mismas acciones de §4 y §5.1 con `actor_kind = Ai`).
- Frontend: va en su propio slice (§9).

## 9. Entregas y despliegue

1. **Backend** (`qep-backend`, `feature/embudo-de-ventas`): §4–§7, con las migraciones `AddPipeline`
   (schema `pipeline`) y la de Tenancy que agrega `pipeline` al `CHECK`.
2. **Frontend** (`qep-frontend`, slice aparte): kanban, panel del chat con triage (crear y descartar) y
   contador, ajustes de etapas y motivos, y **la sección de negocios en la ficha del cliente de la
   conversación**, alimentada por §6.4 y oculta cuando la llamada responde 403.

Backend primero, después frontend. Los permisos nuevos exigen reiniciar la API y recargar la sesión. El
módulo se activa por tenant desde la consola de operador; su dependencia `customers` tiene que estar activa
(D30).

## 10. Decisiones pendientes

Ninguna. La última (tope del triage) quedó resuelta en D37.

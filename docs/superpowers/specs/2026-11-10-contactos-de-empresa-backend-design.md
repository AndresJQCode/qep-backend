# Contactos de empresa: una persona vinculada a un cliente empresa — diseño (backend)

Fecha: 2026-11-10 · Repo: `qep-backend` · Spec hermano:
`qep-frontend/docs/superpowers/specs/2026-11-10-contactos-de-empresa-frontend-design.md`. Este documento manda el
contrato HTTP.

Estado: **borrador, pendiente de aprobación del owner**. Las decisiones de §3.1 son del owner. Las de §3.2 son
**decisión de diseño, no del owner**: se tomaron al escribir y el owner las tiene que confirmar. §9 lista lo abierto.

Reglas del workspace que aplican: TDD obligatorio (RED antes que GREEN, con evidencia literal); no se inventan
campos, estados, rutas, permisos ni códigos de error; ante discrepancia entre este documento y el código, gana el
código y se corrige el documento. Todo lo que este documento marca como **nuevo (propuesto)** no existe hoy.

## 1. Intención y porqué

Cada persona que escribe por WhatsApp o Instagram queda creada como cliente incompleto
(`IMessagingCustomerDirectory.EnsureAsync`, `Modules.Messaging.Application/IMessagingCustomerDirectory.cs:71`;
llamado desde `WebhookDeliveryProcessor.cs:375`). Cuando escriben varias personas de una misma empresa, quedan como
clientes sueltos. El documento es único por tenant (`IX_customers_tenant_identification`,
`CustomersDbContext.cs:247-255`), así que el NIT de la empresa sólo puede estar en una ficha; las demás personas quedan
incompletas y no se pueden cotizar (`QuotationCustomerEligibility.cs:22-48` exige ficha completa, CUC y activo).

Hoy el asesor compensa a mano, cada vez: al cotizar busca y elige a la empresa, y el sistema no recuerda que la
persona pertenece a ella. Además `Customer.Name` es la persona de contacto y no la empresa (`Customer.cs:136`), y la
búsqueda de clientes no mira la razón social (`CustomerRepository.cs:82-95`), así que buscar «ACME» no encuentra a ACME.

Este trabajo deja decir **una sola vez** que una persona es contacto de una empresa, y hace que Mensajería, los
negocios y el envío de cotizaciones y pedidos usen ese vínculo.

## 2. Objetivos, no objetivos, anti-objetivos

**Objetivos**

- Un cliente puede quedar vinculado a otro cliente del mismo tenant, que hace de su empresa.
- La lista de clientes no muestra los contactos por defecto; la ficha de la empresa trae sus contactos.
- La búsqueda de clientes encuentra por razón social, y los ítems de lista dicen si son empresa, persona o contacto.
- Mensajería sabe la empresa del cliente de una conversación.
- Un negocio creado desde la conversación de un contacto queda a nombre de la empresa.
- El resumen de negocios de la conversación de un contacto trae los de la empresa y los que la persona ya tenía.
- Una cotización o un pedido de la empresa se puede enviar por WhatsApp a uno de sus contactos, por el número de la
  conversación de origen cuando ese número puede enviar el documento.
- La importación y la exportación de Excel llevan la empresa.
- La fusión de clientes conserva los vínculos.

**No objetivos**

- Una entidad `Company` o `Contact` aparte. La persona sigue siendo una fila de `customers` (§3.1, O2).
- Cambios en reportes, POS, préstamos y contratos de servicio (§7 lista lo que queda como está).
- Cambiar a quién llegan las campañas: los contactos siguen en las audiencias igual que hoy.
- Mover al vincular lo que la persona ya tenía (negocios, conversaciones): nada cambia de dueño (O5).
- Guardar en la cotización o el pedido la conversación de origen. `Quotations` no tiene `ConversationId` y no lo gana.

**Anti-objetivos** (si pasa esto, falló aunque funcione)

- Vincular automáticamente por parecido de nombre o de razón social, o rellenar el vínculo desde el texto de
  `BusinessName` ya guardado.
- Que el `PUT` de cliente borre el vínculo porque el cuerpo no lo trae.
- Que un documento se envíe a una persona que no es contacto del cliente del documento.
- Que enviar por el número de la conversación cambie el canal elegido por el tenant.
- Que una fusión deje un cliente vinculado a sí mismo, a un fusionado o a dos niveles.

## 3. Decisiones

### 3.1 Del owner

La numeración es la misma en el spec hermano: un mismo número es la misma decisión en los dos documentos.

| ID | Decisión |
|---|---|
| O1 | El dolor es que cada persona que escribe se crea como cliente y el asesor vincula persona y empresa a mano cada vez. |
| O2 | Modelo al estilo Odoo: la persona sigue siendo un cliente, con un vínculo a un cliente empresa. Sin entidad nueva. |
| O3 | La lista de clientes oculta los contactos por defecto. Se ven en una sección de la ficha de la empresa y al buscarlos. |
| O4 | Mensajería muestra «María · ACME S.A.S.». Un negocio creado desde el hilo de la persona va a la empresa. |
| O5 | El hilo de un contacto muestra los negocios de la empresa **y** los que la persona ya tenía a su nombre, juntos. Al vincular no se mueve nada. |
| O6 | Entra la columna de empresa en la importación de Excel. |
| O7 | Entra «Crear cotización» y «Crear pedido» desde un hilo, como flujo ágil que termina en el envío. |
| O8 | Envío: destinatario nuevo, un contacto de la empresa. Desde un hilo, esa persona viene preseleccionada. Sale por el número del hilo cuando ese número puede enviar el documento. Después de enviar se vuelve al hilo. Misma plantilla de WhatsApp que hoy. |
| O9 | Persona sin empresa y con ficha completa: mismo flujo, el destinatario es ella. Sin empresa y con ficha incompleta: la acción no abre y explica que hay que completar la ficha o vincularla a una empresa. |
| O10 | Fuera por ahora: reportes, POS, préstamos, contratos de servicio. Las campañas siguen llegando a los contactos. |

### 3.2 De diseño, no del owner (a confirmar)

| ID | Decisión | Razón |
|---|---|---|
| A1 | `Customer` gana `CompanyId` (`Guid?`, columna `company_id`), autorreferencia con FK al mismo esquema. | Mismo patrón que `MergedIntoId` (`Customer.cs:311-315`). |
| A2 | **Un solo nivel.** Un contacto no puede tener contactos; un cliente con contactos no puede volverse contacto; nadie se vincula a sí mismo; mismo tenant; la empresa tiene que estar activa y sin fusionar al momento de vincular. | Evita cadenas y ciclos sin recursión. |
| A3 | La empresa **no** se restringe a `PersonType = Juridica`. | `PersonType` puede ser nulo en fichas existentes y `BusinessName` no está atado a `Juridica` en el dominio (`Customer.cs:1147-1160`, `:1284-1301`). |
| A4 | La etiqueta de una empresa es `BusinessName ?? Name`, calculada en el backend en todo DTO que la exponga. | `Name` es la persona de contacto (`Customer.cs:136`). |
| A5 | El vínculo se pone y se quita con una **acción propia** (§4.2), sin `If-Match`. Ni `POST /` ni `PUT /{id}` lo leen ni lo tocan. | El `PUT` manda el cuerpo entero; un frontend viejo lo borraría por omisión. Además tiene que servir en una ficha incompleta. |
| A6 | Permiso de la acción: `customers.customer.manage` (`CustomersPermissions.cs:13`). Sin permiso nuevo. | Es editar la ficha. |
| A7 | `GET /customers` excluye los contactos por defecto y los incluye cuando llega un término o filtro de búsqueda, o cuando se pide explícitamente (§4.3). | O3. |
| A8 | `search` también busca en `BusinessName`. | §1. |
| A9 | Los ítems de lista ganan `businessName`, `personType` y `company`. La ficha de la empresa trae `contacts`. | BFF: nada se arma en el navegador. |
| A10 | Triage resuelve la empresa en el servidor; `POST /deals` no cambia (el frontend manda el id de la empresa). | O4; `Triage.cs:78,113`, `Deals.cs:10-11,208`. |
| A11 | Destinatario nuevo `Contact` con `contactId`, validado en el servidor contra el cliente del documento. | Mismo criterio que hoy: opción cerrada, nunca un teléfono suelto (`SendQuotation.cs:14-16`). |
| A12 | El número del hilo viaja en un campo **aparte** de `connectionId` y nunca guarda ni cambia la elección del tenant (§4.6). Si ese número no puede enviar el documento, rige la resolución normal del canal, que puede responder `channel_required` o `no_channel` como hoy; no es «el canal elegido por el tenant» a secas. | Hoy un `connectionId` distinto al guardado es `channel_already_set` y uno elegido entre varios se guarda (`WhatsAppChannelSelection.cs:32-57`). |
| A13 | Fusión: los contactos de un perdedor pasan al sobreviviente; si el sobreviviente era contacto de un perdedor, o un perdedor era contacto del sobreviviente, ese vínculo se limpia (§4.7). | Anti-objetivo de §2. |
| A14 | Importación: columna nueva «Empresa» con el **número de documento** de la empresa. Puede existir ya o venir en el mismo archivo. | No volver al problema del nombre escrito distinto. |
| A15 | Sin migración de datos: `company_id` nace nulo en todas las filas. `BusinessName` sigue siendo texto libre. | Anti-objetivo de §2. |
| A16 | En pantalla el campo se llama «Empresa» y la sección «Contactos de la empresa». Los nombres de código usan `company` y `contacts`. | En Mensajería «contacto» ya significa el contacto de WhatsApp (spec `2026-10-11-contactos-plantillas-campanas-design.md`), y las acciones sobre una tarjeta de contacto compartida ya están mergeadas en `develop` (`bb408f7b`, «shared contact actions into develop»). |
| A17 | Desactivar una empresa no toca a sus contactos. **Condicional:** hoy no existe un `DELETE` de cliente (sólo el de direcciones, `CustomerEndpoints.cs:110`; el comentario de `:138` dice «No hay DELETE»). Si el spec `2026-11-10-borrar-clientes-design.md` se construye, borrar una empresa deja `company_id` en nulo en sus contactos y no bloquea. | El contacto es un cliente por derecho propio. |
| A18 | El resumen de negocios acepta como `customerId` a la **empresa** del cliente de la conversación y devuelve los negocios de la empresa más los de la persona (§4.5). | O5. Hoy responde 404 (`CustomerSummary.cs:101-104`). |
| A19 | `threadConnectionId` lo manda el frontend **sólo** cuando el destinatario elegido es la persona del hilo. El backend no puede comprobarlo (no recibe la conversación) y lo honra con el destinatario que llegue. | Con «cliente» o «facturación» el documento saldría por el número del hilo a otra persona. |
| A20 | Borde conocido, sin resolver: el documento va al teléfono de la **ficha** del contacto. Si no es el número del hilo, el mensaje cae en otra conversación. | El envío sigue yendo a un teléfono de ficha, nunca a un `waId` suelto (A11). |
| A21 | Vista previa de fusión: valor nuevo `Contacts` en `CustomerMergeMoveKind` y campo nuevo `CompanyConflict` en `CustomerMergePreviewResponse` (§4.7). | El frontend tiene que poder avisar antes de confirmar. |
| A22 | La fila de importación expone la columna como `CompanyIdentificationNumber` (`companyIdentificationNumber` en el JSON) en `CustomerImportRowData` (§4.8). | Es lo que el modal de errores muestra y reenvía. |

## 4. Comportamiento y contrato

Rutas existentes: grupo `/api/v1/tenants/{tenantId:guid}/customers` (`CustomerEndpoints.cs:29-31`).

### 4.1 Dominio

- `Customer.CompanyId` (A1). Métodos **nuevos (propuestos)** para vincular y desvincular, con las guardas de la
  casa (`EnsureNotMerged` siempre; el builder decide si un contacto inactivo se puede vincular, siguiendo lo que ya
  hacen notas y etiquetas).
- Reglas de A2, con códigos **nuevos (propuestos)**, todos 422 con el prefijo `customers.customer.`:
  `company_not_found` (no existe en el tenant o está fusionada), `company_inactive`, `company_is_self`,
  `company_is_contact` (la elegida ya es contacto de otra), `has_contacts` (el que se quiere vincular ya tiene
  contactos). El builder ajusta los nombres si chocan con una convención y avisa al frontend.
- «Tiene contactos» no se guarda: se consulta. Índice sobre `(tenant_id, company_id)` parcial `WHERE company_id IS NOT NULL`.
- Carrera: dos requests que vinculan A→B y B→A a la vez no pueden dejar dos niveles. El builder elige el mecanismo
  (bloqueo de las dos filas en orden de id, o verificación dentro de la misma transacción con nivel suficiente) y lo
  cubre con una prueba de integración.
- Auditoría en el handler, acción **nueva (propuesta)** `customers.customer.company_changed`
  (junto a las de `CustomerAuditActions.cs:8-31`).

### 4.2 Vincular y desvincular

**Nuevo (propuesto):** `PUT /{customerId:guid}/company` · permiso `customers.customer.manage`.

```json
{ "companyId": "<guid>" }
```

`companyId: null` desvincula. Responde la ficha entera (`CustomerResponse`), como el resto de las escrituras. 404 si
el cliente no existe; 422 con los códigos de §4.1. Funciona en una ficha `Incomplete` y no cambia su completitud.
Sin `If-Match`: `activate` y `deactivate` (`CustomerEndpoints.cs:126-145`) no lo usan; en clientes sólo la fusión
lo exige (`:66`, `RequireVersion` en `:277`).

`CreateCustomerRequest` y `UpdateCustomerRequest` **no** ganan el campo (A5). Prueba obligatoria: un `PUT /{id}` sobre
un contacto conserva `CompanyId`.

### 4.3 Lista, búsqueda y ficha

`GET /` (`CustomerEndpoints.cs:33`, parámetros en `:188-201`, filtro base en `CustomerRepository.cs:73`):

- Parámetro **nuevo (propuesto)** `contacts` = `include` | `exclude`. Ausente: se excluyen los contactos, **salvo**
  que llegue alguno de `search`, `name`, `identificationNumber`, `cuc` o `phone`, en cuyo caso se incluyen.
  `exclude` explícito sirve al selector de empresa (§4.2 no acepta contactos como empresa).
- `search` y `name` también buscan en `BusinessName` (A8), con el mismo criterio de relevancia por trigramas
  (`CustomerRepository.cs:232-273`). Hace falta el índice que corresponda.
- El total y la exportación (`POST /export`, que usa el mismo `FilteredQuery`, `CustomerRepository.cs:149`) siguen el
  mismo filtro: se exporta lo que la lista muestra.
- `CustomerListItemResponse` (`CustomersDtos.cs:145`, mapeo en `CustomerEndpoints.cs:592-606`) gana:
  `businessName` (`string?`), `personType` (`string?`), `company` (`{ id, name, isComplete, isActive } | null`, con
  `name` según A4) y `contactCount` (`int`). Una consulta por página, sin N+1.

`GET /{customerId}` — `CustomerResponse` (`CustomersDtos.cs:101`, mapeo en `:565-590`) gana `company` (misma forma) y
`contacts`: lista de `{ id, name, phone, email, isComplete, isActive }` (`phone` y `email` anulables), ordenada por
nombre, y `contactCount` (`int`) con el total. El builder pone un tope razonable a `contacts`; `contactCount` viaja
siempre, para que la pantalla diga cuántos faltan.

### 4.4 Mensajería

La referencia de cliente que Mensajería expone en conversaciones (`CustomerRefDto`, armada por
`Bootstrapper/MessagingCustomerDirectory.cs` a través de `MatchAsync` y `FindRefsAsync`,
`IMessagingCustomerDirectory.cs:43,60`) gana `company` (`{ id, name, isComplete, isActive } | null`). `Messaging` no
referencia `Customers`: el dato cruza por el adaptador, como hoy.

- La búsqueda por nombre de la bandeja (`FindIdsByNameAsync`, `:63`, tope 200) **no** cambia en este trabajo (§9).
- `EnsureAsync` (`:71`), `EnsureByPhoneAsync` (`:76`) y `ChangeConversationCustomer` no cambian: la conversación sigue siendo de la
  persona.

### 4.5 Negocios

- Triage (`Triage.cs:78,113`): si el cliente de la conversación tiene empresa, el negocio se crea a nombre de la
  empresa. `OriginConversationId` sigue siendo la conversación. Hace falta un método nuevo en el puerto de clientes
  de Pipeline (`Bootstrapper/PipelineCustomerDirectory.cs`) que resuelva la empresa, encadenado con
  `ResolveSurvivorAsync` (`:11`).
- `POST /deals` no cambia: recibe `CustomerId` del cuerpo y el frontend manda el de la empresa.
- **Resumen de negocios del hilo (A18, O5).** `GetCustomerDealsSummaryQuery(TenantId, CustomerId, ConversationId?)`
  (`CustomerSummary.cs:35`, endpoint en `CustomerSummaryEndpoints.cs:16`). Hoy, con `conversationId`, `TriageAsync`
  lanza `PipelineNotFound.Conversation` si `conversation.CustomerId != customerId` (`CustomerSummary.cs:101-104`), así
  que pedirlo con el id de la empresa da 404. Cambia así:
  - Con `conversationId`, `customerId` es válido si es el cliente de la conversación **o** la empresa de ese cliente.
    Cualquier otro sigue siendo 404.
  - Cuando `customerId` es la empresa del cliente de la conversación, los negocios abiertos y cerrados de la
    respuesta son los de la empresa **más** los que el cliente de la conversación tiene a su nombre, sin duplicados y
    con el orden de hoy. El estado de triage se calcula sobre ese conjunto.
  - Sin `conversationId`, o con el id de la persona, la respuesta es la de hoy: sólo los negocios de ese cliente.
  - La forma de `CustomerDealsSummaryDto` (`:13`) no cambia. El builder verifica si cada negocio del resumen ya dice
    de qué cliente es; si no, lo agrega como campo **nuevo (propuesto)** para que la tira los distinga (§9).
- El negocio automático al crear una cotización (`QuotationEvents.cs:229-243`) ya usa `ClientId`, que será la empresa.

### 4.6 Envío de cotización y de pedido

Hoy: `SendQuotationCommand(TenantId, QuotationId, Recipient, ConnectionId)` (`SendQuotation.cs:18-19`),
`QuotationRecipient { Customer, Billing }` (`:23-27`), `ResolveRecipient` (`:272-308`); el pedido lo calca
(`SendOrder.cs:17,209-243`, `SendOrderRequest` en `OrdersDtos.cs:134`).

Cambios, iguales en los dos documentos:

- `QuotationRecipient` gana `Contact`. El request gana `contactId` (`Guid?`), obligatorio con `Contact` e ignorado
  con los otros dos.
- Con `Contact`: el backend carga al contacto y exige `CompanyId == ClientId` del documento, sin fusionar. Teléfono y
  nombre salen de la ficha del contacto. Sin teléfono, el código existente `{quotation|order}.whatsapp.recipient_missing`
  (`WhatsAppSenderRouter.cs:19,37`). Si no pertenece al cliente: código **nuevo (propuesto)**
  `quotation.quotation.contact_not_of_client` / `order.order.contact_not_of_client`, 422. La elegibilidad
  (`QuotationCustomerEligibility`) se sigue comprobando sobre el **cliente** del documento, no sobre el contacto.
- El mensaje se asocia a la ficha del contacto: `WhatsAppQuotationMessage.CustomerId` (`SendQuotation.cs:159`) lleva el
  id del contacto cuando el destinatario es `Contact`, para que quede en su conversación.
- El historial distingue el destinatario (`QuotationChangeSummary.Sent()`, `OrderChangeSummary.Sent(recipient)` en
  `OrderChangeSummary.cs:74`): con `Contact` nombra a la persona.
- **Teléfono (A20).** Con `Contact` el número es el de la ficha del contacto, no el de la conversación. Si
  difieren, el mensaje cae en otra conversación; es un borde conocido y este trabajo no lo resuelve.
- **Número del hilo (A12, A19).** El request gana `threadConnectionId` (`Guid?`), **nuevo (propuesto)**. Regla: si esa
  conexión está en la lista de canales disponibles para el documento
  (`whatsAppChannels.ListAvailableAsync(tenant, WhatsAppDocument.X)`, `SendQuotation.cs:76-79`), el documento sale por
  ella, **sin** pasar por `channel_already_set`, **sin** `Remember` y sin tocar `channelChoices`. Si no está
  disponible, se ignora y rige `WhatsAppChannelSelection.Resolve` tal cual, con `connectionId` como hoy, incluidos
  sus errores `channel_required` y `no_channel`. El frontend sólo lo manda cuando el destinatario es la persona del
  hilo (A19); el backend no lo restringe por destinatario. El builder
  decide si esto es una rama antes de `Resolve` o un parámetro suyo; lo que no puede pasar está en los anti-objetivos.
- Para pintar la pregunta «¿a quién se lo envías?» sin llamadas extra, la respuesta que sirve a la pantalla de
  cotización y el `OrderDetail` del pedido ganan los contactos del cliente: `clientContacts`:
  `[{ id, name, phone }]` (`phone` puede ser nulo; la modal de hoy muestra el número de cada opción,
  `qep-frontend/src/features/quotes/utils/quote-send-recipients.ts:38-51`). En cotizaciones el contrato HTTP es `QuotationResponse`, no `QuotationDto`: el campo se
  cruza en el composer (el builder ubica el archivo; ver memoria del equipo «QuotationResponse es el contrato HTTP»).
  Los datos salen de `IQuotationCustomerLookup` (`Bootstrapper/QuotationCustomerLookup.cs:89-123`), que gana lo necesario.

### 4.7 Fusión

`CustomerMerge.cs:134-207`, `CustomerMergeStore.cs:39-51,82-102`. Reglas nuevas, en la misma transacción de la fusión:

- Los clientes con `company_id` = un perdedor pasan a `company_id` = sobreviviente.
- Si el sobreviviente tenía `company_id` = un perdedor, queda en nulo.
- Si un perdedor tenía `company_id` (sobreviviente u otro), la lápida queda en nulo.
- Después de lo anterior el sobreviviente no puede quedar con contactos **y** con empresa. Si quedaría así, el
  `POST …/merge` se rechaza con un código **nuevo (propuesto)** `customers.customer.merge_company_conflict`, 422. El
  builder puede proponer otra resolución; si cambia lo que ve la persona, es decisión del owner.
- **Vista previa (A21)** (`merge-preview`, `CustomerEndpoints.cs:59`; respuesta en `CustomersDtos.cs:462-467`):
  - `CustomerMergeMoveKind` (`Modules.Customers.Application/CustomerMerge.cs:15-25`) gana `Contacts`, **nuevo
    (propuesto)**: cuántos clientes pasan a tener al sobreviviente como empresa. Entra en `Moves` junto a los ocho de
    hoy (`:103-112`), también en cero.
  - `CustomerMergePreviewResponse` gana `CompanyConflict` (`bool`), **nuevo (propuesto)**: `true` cuando confirmar
    esta selección daría `merge_company_conflict`. La vista previa **no** responde 422 por eso: responde 200 con el
    campo, para que la pantalla lo explique y no deje confirmar.

### 4.8 Importación y exportación

Columnas actuales en `IExcelCustomerImporter.cs:19-74`; exportación en `ClosedXmlCustomerExportBuilder.cs:29-44`.

- Columna nueva **«Empresa»**, opcional, al final. Valor: número de documento de la empresa.
- Resolución: primero entre las filas del mismo archivo, luego entre los clientes del tenant sin fusionar. Si el
  número corresponde a más de un cliente (el índice es por tipo y número), la fila da error.
- Se aplican las reglas de A2. Lo que no se resuelve o las rompe es un error de fila, como los demás; nada queda a
  medias. Una fila con «Empresa» vacía **no** desvincula a un cliente ya vinculado (mismo criterio que `PersonType`,
  que la importación conserva, `ImportCustomers.cs:626,659`).
- **Fila (A22).** `CustomerImportRowData` (`CustomersDtos.cs:253-266`) gana `CompanyIdentificationNumber`
  (`string?`), **nuevo (propuesto)**, al final del record: el valor tal como vino en el Excel. Viaja en cada
  `ImportRowError` (`:281`) y vuelve en `ExportFailedCustomerRowsRequest` (`:323`). Los errores de la columna usan
  `field: "companyIdentificationNumber"` y códigos **nuevos (propuestos)** bajo `customers.import.row.` para: empresa
  no encontrada, número ambiguo y regla de A2 rota. El builder les pone nombre siguiendo los existentes y avisa al
  frontend.
- La plantilla (`ClosedXmlCustomerImportTemplateBuilder.cs`), el archivo de filas fallidas y la exportación llevan la
  columna. Un archivo viejo sin la columna sigue siendo válido; el builder verifica si el importador lee por
  encabezado o por posición y lo garantiza con una prueba.

## 5. Restricciones

- **Capas.** `Modules.Customers.Application` no referencia EF ni Npgsql; los demás módulos llegan a clientes por sus
  puertos con adaptador en `Bootstrapper`. `tests/ArchitectureTests/ArchitectureTests/CustomersLayerTests.cs` y los de
  los módulos tocados siguen en verde.
- **Migración.** Una, en `Modules.Customers.Infrastructure/Persistence/Migrations/`, con el nombre de la casa
  (`yyyyMMddHHmmss_PascalCase`). Aditiva: columna nula, FK, índices. Sin backfill (A15).
- **Rendimiento.** La lista y la bandeja no ganan consultas por fila.
- **Trabajo vecino ya mergeado.** Las acciones sobre una tarjeta de contacto compartida («cliente desde contacto»)
  ya están en `develop` (`bb408f7b`); la rama y sus worktrees ya no existen. Tocaron el camino de
  `EnsureAsync`/`StartConversation` y corrieron las líneas de `IMessagingCustomerDirectory.cs`: el builder parte del
  `develop` actual. Este trabajo no cambia ese camino.
- **Despliegue.** Backend primero. Todo es aditivo: un frontend viejo sigue funcionando (no manda `contacts`, no
  manda `contactId`, y su `PUT` no borra el vínculo).

## 6. Pruebas mínimas

TDD, RED antes que GREEN. Integración con Testcontainers por proyecto, filtrada al módulo mientras se itera, una vez
completa al final.

- Dominio: cada regla de A2 y su código; desvincular; vincular una ficha incompleta.
- Endpoint de §4.2: 403, 404, 422 por código, 200 con `company`; el `PUT /{id}` no borra el vínculo.
- Lista: contactos fuera por defecto; dentro con cada filtro de búsqueda; `contacts=exclude` con búsqueda; búsqueda
  por razón social; `company` y `contactCount` en los ítems; total y exportación coherentes.
- Ficha: `contacts` de una empresa; `company` de un contacto.
- Mensajería: la referencia de cliente de una conversación trae `company`.
- Pipeline: triage sobre la conversación de un contacto crea el negocio en la empresa; sobre una persona sin empresa,
  como hoy. Resumen de negocios: con el id de la empresa y la conversación de su contacto responde 200 con los
  negocios de la empresa y los de la persona, sin duplicados; con el id de un cliente que no es ni el de la
  conversación ni su empresa, 404; con el id de la persona, sólo los suyos; sin `conversationId`, sólo los del
  cliente pedido.
- Envío (cotización y pedido): `Contact` válido; contacto de otro cliente; contacto sin teléfono; `threadConnectionId`
  disponible con elección guardada distinta (sale por el hilo, la elección no cambia, sin auditoría de elección);
  `threadConnectionId` no disponible (cae a la regla de hoy, incluidos `channel_required` y `no_channel`).
- Fusión: cada regla de §4.7, incluida la carrera de §4.1. Vista previa: `Contacts` con su conteo y en cero;
  `CompanyConflict` en `true` responde 200, y el `POST …/merge` de esa misma selección responde 422.
- Importación: empresa existente, empresa en el mismo archivo, no resuelta, ambigua, que rompe A2, archivo sin la
  columna; el error trae `field` y `CompanyIdentificationNumber` en la fila; filas fallidas y exportación con la
  columna.

## 7. Lo que queda como está, a sabiendas

- **Anomalías** (`CustomerDuplicateQueries.cs:50-51`): un contacto que comparte teléfono con su empresa sigue formando grupo.
- **Reportes** (`CustomerReportSource.cs:273`): cuentan fichas completas; un contacto completo cuenta como cliente.
- **Préstamos y contratos de servicio**: su búsqueda por nombre (`LoansCustomerLookup.cs:62`,
  `ServiceContractsCustomerLookup.cs:63`) sigue devolviendo contactos.
- **POS** (`SearchSelectableAsync`, `CustomerRepository.cs:276-298`): igual que hoy.
- **Campañas** (`CustomerAudienceRepository.cs:126-140`): los contactos siguen en las audiencias (O10).

## 8. Lo que queda a criterio del builder

- Nombres finales de códigos, del parámetro `contacts`, de `threadConnectionId` y de `clientContacts`, avisando al frontend.
- Mecanismo de concurrencia de §4.1.
- Tope de `contacts` en la ficha.
- Forma del contrato entre módulos para la empresa (Pipeline, Mensajería, Cotizaciones).
- Texto de la auditoría y del historial de envío.

## 9. Abierto

- **Resumen de negocios**: falta verificar si cada negocio del resumen ya dice de qué cliente es (§4.5).
- **Importador**: falta verificar si lee por encabezado o por posición (§4.8); de eso depende que un archivo viejo
  sin la columna siga siendo válido.
- **Automatizaciones**: el relevamiento no revisó cómo el módulo de automatizaciones lee clientes.
- **Bandeja**: buscar «ACME» en la lista de conversaciones no trae los hilos de sus contactos. No se pidió; fuera.
- **Pedidos y conversación**: no se encontró ningún vínculo entre pedidos y conversaciones; el flujo desde el hilo
  es navegación del frontend.
- **Teléfono de la ficha distinto al número del hilo** (A20): borde conocido. Si molesta en el uso real, es una
  decisión nueva del owner.
- **Destinatario cuando el cliente del documento es la propia persona** (O9): es `Customer`, sin cambios.

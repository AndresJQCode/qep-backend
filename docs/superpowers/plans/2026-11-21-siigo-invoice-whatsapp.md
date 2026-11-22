# Enviar la factura de Siigo por WhatsApp — plan de implementación

> **Para quien ejecute:** SUB-SKILL requerida: `superpowers:subagent-driven-development` (recomendada) o `superpowers:executing-plans`.
> Los pasos usan casillas (`- [ ]`). Se ejecuta en **3 bloques** (A Messaging, B Quotations e Integrations, C Siigo); un implementador por
> bloque, un commit por tarea, una revisión por bloque y la fuerte al final (CLAUDE.md del workspace, «Presupuesto de pruebas»).

**Meta:** un botón en el detalle del pedido envía el PDF de **una** factura de Siigo al WhatsApp del cliente por el canal del tenant (Meta o Zenvia).

**Arquitectura:** Siigo declara un puerto `ISiigoInvoiceWhatsApp`; su adaptador (Bootstrapper) llama a una fachada pública de Quotations,
`IInvoiceWhatsAppDelivery`, que ya ve canales, cliente y los senders. Quotations es dueño del envío porque `WhatsAppChannelSelection` es
`internal` y los tres senders viven ahí. Messaging gana un tipo de plantilla `Invoice` (hermano de `Order`). El PDF viaja en memoria; sólo
Zenvia necesita una copia pública (clave aleatoria bajo `invoices/`).

**Stack:** .NET 10, EF Core + Postgres, xUnit, Meta Cloud (Messaging), Zenvia, R2.

**Spec:** [`../specs/2026-11-21-siigo-invoice-whatsapp-design.md`](../specs/2026-11-21-siigo-invoice-whatsapp-design.md).
**Corrección al spec (decidida al planear):** los códigos de error de envío son `invoice.whatsapp.*` (`no_channel`, `channel_required`,
`channel_unavailable`, `channel_already_set`, `no_phone`, `send_failed`), no `siigo.invoice.whatsapp_*`: salen del prefijo de
`WhatsAppDocuments.PrefixOf`, igual que `order.whatsapp.*`. Sigue siendo `siigo.invoice.not_found` para la factura inexistente. Task 9 corrige el spec.

## Restricciones globales

- TDD: RED antes que GREEN con la salida literal de ambos. Si el RED esperado es de compilación, la evidencia es la salida del build
  (sin contenedores). La integración corre en GREEN y **filtrada por nombre** (`--filter "FullyQualifiedName~Nombre"`); la clase completa
  una vez al final del bloque; la suite completa sólo antes de mergear y de los proyectos tocados.
- Antes de lanzar una corrida de integración: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay una, esperar. Nunca detener un proceso ajeno. Correr en primer plano.
- `Api.exe` corriendo bloquea `build` y `test`: avisar, no detener.
- Commits: conventional commits, **sin** atribución de IA ni trailer `Co-Authored-By`.
- Mensajes y comentarios dirigidos a personas: español colombiano tuteando. Identificadores y códigos en inglés.
- No inventar: campos, rutas y códigos salen de este plan o del código. Lo que falte se marca como decisión pendiente del owner.
- `siigo.invoices` no se toca (sin migración de Siigo). La única migración es la de Messaging (Task 2).
- Ningún log, DTO, fila de `siigo.call_failures` ni auditoría lleva el PDF, la URL pública ni el teléfono.
- Una plantilla de WhatsApp no se edita: el texto de `InvoiceTemplateDefinition.BaseBody` sólo es el inicial.

## Foco de revisión

Casos que el spec implica y ninguna tarea nombra aparte; cada uno tiene su prueba en la tarea indicada.

1. Factura de otro pedido o de otro tenant, o sin PDF (`Rejected`, `Pending`, resuelta a mano sin id) → 404 `siigo.invoice.not_found` sin llamar a Siigo ni al canal (Task 9).
2. Cliente del pedido sin teléfono → 422 `invoice.whatsapp.no_phone` **antes** de pedir el PDF a Siigo (Task 8).
3. Canal de Zenvia sin `invoiceTemplateId` no figura como disponible para facturas; uno de Meta sin fila `Invoice` aprobada tampoco (Tasks 4 y 6).
4. Bucket público sin configurar y canal Zenvia → el envío **falla** y no cae a URL firmada ni a ningún otro camino (Task 7).
5. Por Meta Quotations no publica ninguna copia: el PDF viaja en memoria y Mensajería deja una copia privada del adjunto en el hilo (por tenant, como en cotizaciones y pedidos); no hay copia pública (Task 8).
6. La clave pública de Zenvia no contiene el id del pedido, ni el número ni el id de la factura, ni el id del tenant (Task 7).
7. Doble clic: dos envíos son dos mensajes; no hay deduplicación (decisión: lo pide una persona, y cada envío deja su auditoría) (Task 9).

---

## Bloque A — Messaging: el tipo de plantilla `Invoice`

Referencia viva: el commit `028aa4906` (plantilla de pedido). Cada archivo nuevo es el hermano del de `Order`; se lee el de `Order` antes de escribir.

### Task 1: dominio y definición de la plantilla

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessageTemplate.cs` (constantes `OrderName`/`OrderPrefix` en ~106, el tipo y la lista de nombres reservados en ~135)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/SystemTemplateVariables.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/InvoiceTemplateDefinition.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/InvoiceTemplateDefinitionTests.cs` (hermano de `OrderTemplateDefinitionTests`) y casos en `MessageTemplateTests.cs`, `SystemTemplateVariablesTests.cs`

**Interfaces:**
- Produces: `MessageTemplateName.InvoiceName = "qep_factura"`, `MessageTemplateName.InvoicePrefix = "qep_factura_"` (reservados: ninguna plantilla libre puede llamarse así); un valor `Invoice` en el tipo de plantilla de sistema (el mismo que tiene `Order`); `SystemTemplateVariables.Invoice` con las claves `nombre_contacto`, `numero_factura`, `valor_total` (con etiqueta y ejemplo); `InvoiceTemplateDefinition` con `Name`, `Language = "es"`, `Category = "UTILITY"`, `BaseBody`, `NameFor(int version)`, `VersionOf(string? name)`, `Draft(string name, string body)`, `Payload(string handle, string name, string body)`, `IsAdoptable(string componentsJson)`.
- `BaseBody` = `"Hola {{nombre_contacto}}, te enviamos la factura {{numero_factura}} por un total de {{valor_total}}. Adjuntamos el documento. ¡Gracias por tu compra!"`.

- [ ] **Step 1: pruebas que fallan.** En `InvoiceTemplateDefinitionTests`: `NameForVersionOneIsTheBaseName` (`NameFor(1) == "qep_factura"`), `NameForLaterVersionsAreSuffixed` (`NameFor(2) == "qep_factura_v2"`), `VersionOfRoundTripsAndRejectsLookalikes` (`VersionOf("qep_factura_v02") is null`, `VersionOf("qep_factura_v1") is null`, `VersionOf("qep_pedido_v2") is null`), `TheBaseBodyUsesOnlyInvoiceVariables`, `IsAdoptableAcceptsTheDocumentHeaderShapeAndRejectsVigencia` (con `vigencia` → `false`), `TheOrderDefinitionRejectsAnInvoiceBody` (un cuerpo con `numero_factura` no es adoptable como pedido). En `MessageTemplateTests`: `AFreeTemplateCannotTakeTheInvoiceName` (la reserva).
- [ ] **Step 2: RED.** `dotnet build tests/Modules/Messaging/Modules.Messaging.UnitTests` → error de compilación esperado (símbolos inexistentes). Pegar la salida.
- [ ] **Step 3: implementar** lo de *Produces*, calcando `OrderTemplateDefinition` (mismo encabezado de documento, sin pie ni botones).
- [ ] **Step 4: GREEN.** `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~InvoiceTemplateDefinition|FullyQualifiedName~MessageTemplateTests|FullyQualifiedName~SystemTemplateVariablesTests"` → todo verde.
- [ ] **Step 5: commit** `feat(messaging): add the invoice system template definition`.

### Task 2: aprovisionamiento, readiness, endpoint y migración

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/InvoiceTemplateProvisioning.cs` (hermano de `OrderTemplateProvisioning`)
- Modify: `TemplateManagementPorts.cs` (`FindInvoiceAsync` en el repositorio, `IInvoiceTemplateReadiness`), `TemplateManagement.cs`, `TemplateManagementDtos.cs`, `SystemTemplateDrafts.cs`, `IMessagingAuditRecorder.cs`, `MessagingErrorCodes.cs` (Domain)
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/TemplateManagementEndpoints.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessageTemplateRepository.cs`, `QuotationTemplateReadiness.cs` (aloja las readiness de cotización y pedido), `MessagingDbContext.cs`, `MessagingSaveChanges.cs`, `MessagingInfrastructureExtensions.cs`
- Create: migración `AddInvoiceTemplateKind` (con su Designer) y actualizar `MessagingDbContextModelSnapshot.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (registro del handler)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/ProvisionInvoiceTemplateHandlerTests.cs`, `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/InvoiceTemplateProvisioningApiTests.cs`, caso en `MessageTemplatePersistenceTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces: `IInvoiceTemplateReadiness.FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken) : Task<ApprovedTemplate?>` (misma forma que `IOrderTemplateReadiness`); `IMessageTemplateRepository.FindInvoiceAsync(Guid tenantId, Guid connectionId, CancellationToken) : Task<MessageTemplate?>`; endpoint de aprovisionamiento con la misma ruta y permiso que el de pedido, cambiando el segmento (`…/templates/invoice`; confirmarlo leyendo la ruta de `order` en `TemplateManagementEndpoints`); acción de auditoría y códigos de error calcados de `Order`.

- [ ] **Step 1: pruebas que fallan.** Calcar `ProvisionOrderTemplateHandlerTests` con los mismos casos (crea y envía a Meta; reintento tras rechazo sube la versión; sin permiso 403; conexión de otro tenant 403; ya existe aprobada → conflicto) y `OrderTemplateProvisioningApiTests` (integración). Más: `AnInvoiceRowIsAdoptedFromAnExistingApprovedTemplate` si `Order` lo tiene.
- [ ] **Step 2: RED** por compilación (`dotnet build` del proyecto de pruebas). Pegar la salida.
- [ ] **Step 3: implementar** calcando el commit `028aa4906`. La migración se genera con el mismo comando que `AddOrderTemplateKind` (ver README § Migraciones) y su `Up` sólo ensancha el discriminador de tipo; revisar que el `Down` quede como el de `AddOrderTemplateKind`.
- [ ] **Step 4: GREEN unitario** filtrado `FullyQualifiedName~InvoiceTemplate` y luego la integración filtrada `FullyQualifiedName~InvoiceTemplateProvisioningApiTests` (revisar `testhost` antes).
- [ ] **Step 5: commit** `feat(messaging): provision the invoice template per connection`.

### Task 3: envío por Meta

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/InvoiceTemplateOutbound.cs` (hermano de `OrderTemplateOutbound`)
- Modify: `MessagingInfrastructureExtensions.cs` (registro)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/InvoiceTemplateOutboundTests.cs` (hermano del de pedido, si existe; si no, de `QuotationTemplateOutboundTests`)

**Interfaces:**
- Consumes: Task 2 (`ApprovedTemplate`), `DocumentTemplateOutbound`.
- Produces:
  `record InvoiceTemplateSend(Guid TenantId, Guid ConnectionId, Guid SentByMemberId, string WaId, Guid? CustomerId, string RecipientName, ApprovedTemplate Template, string FullName, string InvoiceNumber, string Total, ReadOnlyMemory<byte> Document, string DocumentFileName)`;
  `abstract record InvoiceTemplateSendResult` con `Sent(Guid MessageId, Guid ConversationId)`, `ConnectionUnavailable(string? Reason)`, `Failed(string Code)`;
  `interface IInvoiceTemplateOutbound { Task<InvoiceTemplateSendResult> SendAsync(InvoiceTemplateSend send, CancellationToken cancellationToken); }`.
  Valores de la plantilla: `nombre_contacto` = `FullName`, `numero_factura` = `InvoiceNumber`, `valor_total` = `Total`.

- [ ] **Step 1: prueba que falla** `TheValuesAreContactNameInvoiceNumberAndTotal` (assert sobre el `DocumentTemplateSend` que recibe el núcleo, con un doble) y `AFailedSendMapsToTheInvoiceResult`.
- [ ] **Step 2: RED** (compilación). **Step 3:** implementar calcando `OrderTemplateOutbound`. **Step 4: GREEN** filtrado `FullyQualifiedName~InvoiceTemplateOutbound`.
- [ ] **Step 5: commit** `feat(messaging): send the invoice template with the document header`.

**Cierre del bloque A:** `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests` completo (rápido, sin contenedores) y `dotnet build` de la solución. Revisión de bloque.

---

## Bloque B — Quotations, Zenvia e Integrations

### Task 4: campo `invoiceTemplateId` de la conexión de Zenvia

**Files:**
- Modify: `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationProviders.cs` (`ZenviaFieldKeys` ~10 y la definición de campos del proveedor ~154)
- Test: casos junto a los del `OrderTemplateId` en `tests/Modules/Integrations/Modules.Integrations.UnitTests` (buscar con `grep -rn OrderTemplateId tests`)

**Interfaces:**
- Produces: `ZenviaFieldKeys.InvoiceTemplateId = "invoiceTemplateId"`; campo **opcional**, mismo tipo y validación que `orderTemplateId`.

- [ ] **Step 1: prueba que falla** `TheZenviaProviderDeclaresAnOptionalInvoiceTemplateId` (calcada de la del pedido). **Step 2: RED** (compilación). **Step 3:** implementar. **Step 4: GREEN** (filtro por el nombre de la clase). **Step 5: commit** `feat(integrations): add the optional invoiceTemplateId to Zenvia connections`.

### Task 5: contratos de Quotations y envío por Zenvia

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IWhatsAppChannelDirectory.cs` (`ZenviaWhatsAppChannel`, enum `WhatsAppDocument`, `WhatsAppDocuments.PrefixOf`), `IWhatsAppSender.cs`, `WhatsAppSenderRouter.cs`, `WhatsAppTemplateValues.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/ZenviaWhatsAppSender.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/ZenviaWhatsAppSenderTests.cs`, `WhatsAppSenderRouterTests.cs`, `WhatsAppTestDoubles.cs` (los dobles implementan la interfaz nueva)

**Interfaces:**
- Produces:
  `WhatsAppDocument.Invoice`; `PrefixOf(Invoice) == "invoice"`;
  `ZenviaWhatsAppChannel` suma un último parámetro opcional `string? InvoiceTemplateId = null` (y su `ToString`);
  `record WhatsAppInvoiceMessage(string? ToPhone, string FullName, string InvoiceNumber, decimal Total, string Currency, string? DocumentUrl, string? NumberFormat = null, byte[]? Document = null, Guid? SentByMemberId = null, Guid? CustomerId = null, Guid? TenantId = null)`;
  `IWhatsAppSender`, `IZenviaWhatsAppSender` y `IMetaWhatsAppSender` ganan `SendInvoiceAsync` con la forma de su `SendOrderAsync` (el de Meta recibe además `string waId`); el de Meta toma `message.Document` (bytes), no una clave de storage;
  `WhatsAppTemplateValues.InvoiceDocumentFileName(string invoiceNumber) : string` → `"Factura-{numero}.pdf"` (con el número saneado como `OrderDocumentFileName`).
- Zenvia: la plantilla es `channel.InvoiceTemplateId`; sin ella lanza `QuotationsDomainException("invoice.whatsapp.send_failed", …)` como `SendOrderAsync` con `OrderTemplateId`. Los campos de la plantilla: `fullName`/`invoiceNumber`/`total` con el mismo criterio de nombres que `SendOrderAsync` usa para el pedido (copiar sus claves cambiando `orderNumber` por `invoiceNumber`) y `documentUrl`.

- [ ] **Step 1: pruebas que fallan.** `ZenviaWhatsAppSenderTests`: `AnInvoiceGoesWithItsOwnTemplateAndTheDocumentUrl` (assert sobre el cuerpo postado: `templateId == "tpl-invoice"` y `fields.documentUrl`), `AnInvoiceWithoutTemplateIdFailsWithInvoiceSendFailed`, `AnInvoiceWithoutDocumentUrlFails`. `WhatsAppSenderRouterTests`: `AnInvoiceGoesToZenviaOrMetaByChannel`. Unitaria de `InvoiceDocumentFileName`.
- [ ] **Step 2: RED** por compilación. **Step 3: implementar.** **Step 4: GREEN** filtrado (`ZenviaWhatsAppSender|WhatsAppSenderRouter|WhatsAppTemplateValues`).
- [ ] **Step 5: commit** `feat(quotations): invoice message contract and Zenvia sender`.

### Task 6: directorio de canales y envío por Meta

**Files:**
- Modify: `src/Bootstrapper/QuotationsWhatsAppChannelDirectory.cs`, `src/Bootstrapper/QuotationsMetaWhatsAppSender.cs`, `src/Bootstrapper/QepServiceCollectionExtensions.cs`
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/QuotationsWhatsAppChannelDirectoryTests.cs`, `QuotationsMetaWhatsAppSenderTests.cs`

**Interfaces:**
- Consumes: Tasks 2, 3, 4, 5.
- Produces: `ListAvailableAsync(tenantId, WhatsAppDocument.Invoice, ct)`: **Meta** `Active`, visible y con la fila `Invoice` aprobada (`IInvoiceTemplateReadiness`); **Zenvia** como para pedido pero exigiendo `invoiceTemplateId`. `QuotationsMetaWhatsAppSender.SendInvoiceAsync(MetaWhatsAppChannel, string waId, WhatsAppInvoiceMessage, ct)` busca la fila vigente (`IInvoiceTemplateReadiness`), llama `IInvoiceTemplateOutbound.SendAsync` con `message.Document` y `WhatsAppTemplateValues.Total(...)`, y mapea fallas a `QuotationsDomainException("invoice.whatsapp.send_failed", $"Meta did not accept the invoice ({reason}).")`. **No** llama a `IObjectStorage`.

- [ ] **Step 1: pruebas que fallan.** Directorio: `AMetaConnectionWithoutAnApprovedInvoiceTemplateIsNotAvailableForInvoices`, `AZenviaConnectionWithoutInvoiceTemplateIdIsNotAvailableForInvoices`, `AZenviaConnectionWithItIsAvailable`, y que cotización y pedido no cambian. Sender: `AnInvoiceIsSentFromTheBytesWithoutTouchingStorage` (el doble de `IObjectStorage` no recibe llamadas), `ANotApprovedTemplateFailsWithTemplateNotApproved`, `AFailedOutboundMapsToSendFailedWithTheReason`.
- [ ] **Step 2: RED** (compilación). **Step 3: implementar.** **Step 4: GREEN** filtrado por nombre de clase.
- [ ] **Step 5: commit** `feat(quotations): channels and Meta sender for invoices`.

### Task 7: copia pública con clave aleatoria para Zenvia

**Files:**
- Modify: `src/Modules/Storage/Modules.Storage.Application/IPublicObjectStorage.cs` (+ su implementación en `Modules.Storage.Infrastructure`, y los dobles que la implementan: `grep -rn ": IPublicObjectStorage" src tests`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application` — el puerto `IQuotationPdfStorage` (buscar su archivo) y `src/Bootstrapper/QuotationPdfStorage.cs`
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/QuotationPdfStorageTests.cs` (o el que exista) y el de `Storage` que cubra `IPublicObjectStorage`

**Interfaces:**
- Produces: `IPublicObjectStorage.UploadAsync(string publicKey, ReadOnlyMemory<byte> content, string contentType, CancellationToken)`; `IQuotationPdfStorage.PublishInvoiceAsync(ReadOnlyMemory<byte> content, CancellationToken) : Task<string>` que sube a `invoices/{Guid.CreateVersion7():N}.pdf` (content type `application/pdf`) y devuelve `publicObjectStorage.GetUrl(key)`. Sin `IsConfigured` lanza `InvalidOperationException` (mismo mensaje y criterio que `PublishAsync`: ruidoso, sin fallback).

- [ ] **Step 1: pruebas que fallan.** `PublishInvoiceUploadsUnderTheInvoicesPrefixWithARandomKey` (clave coincide con `^invoices/[0-9a-f]{32}\.pdf$`; dos llamadas dan claves distintas), `ThePublicKeyHasNoTenantOrderOrInvoiceData` (se pasa un `Guid` conocido por todos los caminos y no aparece en la clave ni en la URL), `PublishInvoiceFailsWhenThePublicBucketIsNotConfigured`, y que **no** toca el bucket privado.
- [ ] **Step 2: RED** (compilación). **Step 3: implementar** (R2: `PutObject` con el cliente que ya usa `CopyFromPrivateAsync`; sin el checksum por defecto que rompe R2, ver memoria `awssdk-v4-incompatible-con-r2` y cómo lo resuelve esa clase). **Step 4: GREEN** filtrado.
- [ ] **Step 5: commit** `feat(storage): public invoice copy with a random key for Zenvia`.

### Task 8: la fachada `IInvoiceWhatsAppDelivery`

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/InvoiceWhatsAppDelivery.cs`
- Modify: el registro de servicios de Quotations (`QuotationsApplicationExtensions` o equivalente, buscar dónde se registra `SendOrderHandler`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/InvoiceWhatsAppDeliveryTests.cs` (con los dobles de `SendOrderHandlerTests`)

**Interfaces:**
- Consumes: Tasks 5 y 7; `IOrderRepository.FindUntrackedByIdAsync`, `IQuotationCustomerLookup`, `IWhatsAppChannelDirectory`, `IWhatsAppChannelChoiceStore`, `WhatsAppChannelSelection.Resolve`, `ITenantDirectory`, `QuotationAdvisorResolver`, `IWhatsAppSender`, `IQuotationPdfStorage`.
- Produces:
  `record InvoiceWhatsAppRequest(Guid TenantId, Guid OrderId, string InvoiceNumber, decimal? InvoiceTotal, ReadOnlyMemory<byte> Pdf)`;
  `interface IInvoiceWhatsAppDelivery { Task SendAsync(InvoiceWhatsAppRequest request, CancellationToken ct); Task<bool> CanSendAsync(Guid tenantId, Guid orderId, CancellationToken ct); }`.
- `SendAsync`, en este orden (todo antes de gastar nada): pedido del tenant (si no, `OrderNotFound.ById`); `order.EnsureSendable()` no aplica (una factura existe aunque el pedido esté anulado: **decisión del builder si Siigo ya lo impide**, no agregar la regla); cliente (`customerLookup`, mismo tenant) sin teléfono → `QuotationsDomainException("invoice.whatsapp.no_phone", …)`; quien envía con `QuotationAdvisorResolver.ResolveAsync`; canal con `WhatsAppChannelSelection.Resolve(disponibles(Invoice), choiceStore, requested: null, WhatsAppDocument.Invoice)`, **sin** recordar la elección (`Remember` se ignora); si es Zenvia, `pdfStorage.PublishInvoiceAsync(pdf)`; total = `InvoiceTotal ?? order.Total`, moneda `order.Currency`; `whatsAppSender.SendInvoiceAsync(channel, new WhatsAppInvoiceMessage(…, Document: pdf bytes sólo para Meta, DocumentUrl sólo para Zenvia))`. Cualquier excepción de envío se relanza como viene (ya es `invoice.whatsapp.send_failed`).
- `CanSendAsync`: hay al menos un canal disponible para `Invoice` **y** el cliente del pedido tiene teléfono; nunca lanza por «no hay» (devuelve `false`).

- [ ] **Step 1: pruebas que fallan.** `ASuccessfulMetaSendCarriesTheBytesAndNoUrl`, `ASuccessfulZenviaSendCarriesTheUrlAndPublishesOnce`, `MetaNeverPublishesACopy`, `ACustomerWithoutPhoneFailsBeforeAnyChannelWork`, `NoChannelFailsWithInvoiceNoChannel`, `TwoChannelsWithoutAChoiceFailWithChannelRequired`, `TheSavedChoiceIsUsedAndNotRewritten`, `TheTotalFallsBackToTheOrderTotalWhenTheInvoiceHasNone`, `CanSendIsFalseWithoutChannelOrPhone`.
- [ ] **Step 2: RED** (compilación). **Step 3: implementar.** **Step 4: GREEN** filtrado `FullyQualifiedName~InvoiceWhatsAppDelivery`.
- [ ] **Step 5: commit** `feat(quotations): invoice WhatsApp delivery facade`.

**Cierre del bloque B:** `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests` completo y `tests/Bootstrapper/Bootstrapper.UnitTests`; `ArchitectureTests` (Quotations/Messaging/Integrations). Revisión de bloque.

---

## Bloque C — Siigo

### Task 9: caso de uso, endpoint, disponibilidad y documentación

**Files:**
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/SiigoPorts.cs` (puerto), `SiigoAuditActions.cs`, `Invoicing/GetSiigoInvoicePdf.cs` (extraer la obtención del PDF), `Invoicing/SiigoInvoiceDto.cs` y el handler que arma la lista (`GetSiigoInvoices…`)
- Create: `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/SendSiigoInvoiceWhatsApp.cs`
- Modify: `src/Modules/Siigo/Modules.Siigo.Api/SiigoEndpoints.cs` (ruta dentro del grupo `siigo-invoice`, ~líneas 118–160)
- Create: `src/Bootstrapper/SiigoInvoiceWhatsApp.cs` (adaptador) y registrarlo en `QepServiceCollectionExtensions.cs`; handler en el registro de Siigo
- Modify: `README.md` (§ Siigo: ruta, `canSendWhatsApp`, plantilla `Invoice`, `invoiceTemplateId`, regla de lifecycle de `invoices/`), `.claude/rules/siigo.md` (regla nueva), el spec (códigos `invoice.whatsapp.*`)
- Test: `tests/Modules/Siigo/Modules.Siigo.UnitTests/SendSiigoInvoiceWhatsAppHandlerTests.cs`, `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/SiigoInvoiceWhatsAppApiTests.cs`, ampliar `SiigoNoLeakTests.cs`

**Interfaces:**
- Consumes: Task 8.
- Produces:
  `interface ISiigoInvoiceWhatsApp { Task SendAsync(Guid tenantId, Guid orderId, string invoiceNumber, decimal? invoiceTotal, ReadOnlyMemory<byte> pdf, CancellationToken ct); Task<bool> CanSendAsync(Guid tenantId, Guid orderId, CancellationToken ct); }` (en `SiigoPorts.cs`; el adaptador sólo reenvía a `IInvoiceWhatsAppDelivery`);
  `record SendSiigoInvoiceWhatsAppCommand(Guid TenantId, Guid OrderId, Guid InvoiceId) : ICommand<SiigoInvoiceWhatsAppSent>` y `record SiigoInvoiceWhatsAppSent(bool Sent)` (el endpoint responde 204 y no lo mira, como `ClientClassificationDeletedResult`);
  `SiigoAuditActions.InvoiceWhatsAppSent = "siigo.invoice.whatsapp_sent"` (recurso `InvoiceResourceType`, sin teléfono ni contenido);
  ruta `POST /{invoiceId:guid}/send-whatsapp` en el grupo `…/orders/{orderId}/siigo-invoice`, política `siigo.invoice.issue`;
  `SiigoInvoiceDto` gana `bool CanSendWhatsApp = false` (con su nota de por qué, en el DTO), calculado una vez por petición en el handler de la lista: `hasPdf` de la fila **y** `ISiigoInvoiceWhatsApp.CanSendAsync` (si ninguna fila tiene PDF no se consulta).
- Handler: `SiigoInvoiceSupport.AuthorizeAsync` → fila de ese pedido y tenant con `HasPdf` (si no, `ResourceNotFoundException(SiigoErrorCodes.InvoiceNotFound)` sin llamar a Siigo) → PDF por el mismo camino que la descarga (extraer de `GetSiigoInvoicePdfHandler` un método compartido `SiigoInvoiceSupport.LoadPdfAsync(…)`; no llamar un handler desde otro) → `ISiigoInvoiceWhatsApp.SendAsync(…, row.Number ?? "", row.SiigoTotal, pdf, …)` → auditoría `InvoiceWhatsAppSent`.

- [ ] **Step 1: pruebas que fallan (unitarias del handler).** `ASuccessfulSendLoadsThePdfSendsItAndAudits`, `AnInvoiceOfAnotherOrderIsNotFoundAndNothingIsCalled`, `AnInvoiceWithoutPdfIsNotFoundAndNothingIsCalled`, `WithoutThePermissionItIs403NotNotFound`, `ASendFailureIsNotAuditedAsSent`, `TheAuditCarriesNeitherPhoneNorContent`; y `TheListMarksCanSendWhatsAppOnlyWhenThePdfExistsAndDeliveryCan`, `TheListDoesNotAskDeliveryWhenNoRowHasPdf`.
- [ ] **Step 2: RED** (compilación). **Step 3: implementar** lo de *Produces* (incluida la extracción de `LoadPdfAsync`; las pruebas existentes de `GetSiigoInvoicePdf` siguen verdes).
- [ ] **Step 4: pruebas de integración que fallan** (`SiigoInvoiceWhatsAppApiTests`, con `FakeSiigoApi` y los senders grabadores): `PostSendsTheInvoicePdfByMetaAndReturns204`, `PostByZenviaPublishesOneCopyUnderInvoices`, `ARoleWithoutInvoiceIssueGets403`, `AnotherTenantsInvoiceGets403Not404`, `NoChannelReturns422InvoiceWhatsappNoChannel`, `ACustomerWithoutPhoneReturns422NoPhone`, `TheInvoiceListCarriesCanSendWhatsApp`, y en `SiigoNoLeakTests`: tras un envío exitoso y uno fallido, ni el PDF, ni la URL pública, ni el teléfono aparecen en logs, `siigo.call_failures` ni la respuesta. Correr cada una filtrada por nombre y ver el RED real (404 de ruta o fallo de aserción).
- [ ] **Step 5: GREEN** filtrado (`SendSiigoInvoiceWhatsApp`, `SiigoInvoiceWhatsAppApiTests`, `SiigoNoLeakTests`), revisando `testhost` antes de cada corrida de integración. Después la clase completa `SiigoInvoiceApiTests` (regresión de la lista).
- [ ] **Step 6: documentación.** README y `.claude/rules/siigo.md` (regla: Siigo nunca guarda el PDF de factura; la copia pública de Zenvia vive en `invoices/` con lifecycle de 1 día **creado a mano** con `npx wrangler r2 bucket lifecycle add <bucket-publico> expire-invoices "invoices/" --expire-days 1`; el envío es por Quotations y por eso `WhatsAppChannelSelection` no se reimplementa), y corregir el spec (códigos `invoice.whatsapp.*`, fachada en Quotations).
- [ ] **Step 7: commit** `feat(siigo): send the invoice PDF to the customer by WhatsApp`.

**Cierre del bloque C y de la rama:**
- [ ] `dotnet build` de la solución y `dotnet test` de `Modules.Siigo.*`, `Modules.Quotations.*`, `Modules.Messaging.*`, `Modules.Integrations.*`, `Bootstrapper.UnitTests` y `ArchitectureTests`, **un proyecto a la vez**, con `-- xUnit.MaxParallelThreads=4` en los de integración. Transversal sólo en `Bootstrapper` (DI) → no hace falta la solución entera.
- [ ] Revisión fuerte de toda la rama (modelo más capaz) y arreglos.
- [ ] Merge a `develop` (nunca a `main`), y **en `develop`** actualizar `docs/estado.md` (fila de Siigo y Registro) en un commit `docs(estado):`.
- [ ] Despliegue (orden, para el owner): migración de Messaging; crear y aprobar la plantilla `Invoice` en Meta y en Zenvia, y poner `invoiceTemplateId` en cada conexión de Zenvia; regla de lifecycle de `invoices/`; backend y después frontend (spec aparte en `qep-frontend`).

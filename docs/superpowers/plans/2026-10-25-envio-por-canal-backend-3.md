# Envío por canal: Meta y elección del canal (backend, plan 3 de 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** La cotización también sale por la conexión de WhatsApp Business (Meta) del tenant y queda en el hilo de Mensajería. Con más de un canal disponible, el tenant elige uno y la elección se guarda.

**Architecture:** El canal de Quotations pasa a ser un tipo con dos variantes (`ZenviaWhatsAppChannel`, `MetaWhatsAppChannel`). `IWhatsAppSender` queda como fachada y la implementa `WhatsAppSenderRouter`, un Strategy por proveedor. Zenvia sigue en la Infrastructure de Quotations. Meta es un puerto (`IMetaWhatsAppSender`) con su adaptador en Bootstrapper, que lee el PDF del bucket privado y llama a un servicio público de Messaging, `IQuotationTemplateOutbound`. Ese servicio sigue el patrón de `IBotOutbound`: abre o encuentra la conversación por teléfono, guarda el PDF como media propia, sube el documento a Meta y manda la plantilla con encabezado de documento, sin pasar por las guardas de la bandeja. La elección del canal vive en una tabla propia de Quotations (`quotations.tenant_whatsapp_channels`), con `GET /quotations/whatsapp-channels` y `PUT /quotations/whatsapp-channel`.

**Tech Stack:** .NET 10, EF Core + PostgreSQL, FluentValidation, xUnit + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-10-21-envio-de-cotizaciones-por-canal-del-tenant-design.md`: D4–D10, D16, D16a, D16b, D16c, D17; §4.1–§4.4, §5.2, §7.4, §9 (criterios 1–4 y 7). Las decisiones de §3 están aprobadas y este plan no las reabre. Planes anteriores: `docs/superpowers/plans/2026-10-21-proveedores-habilitados-por-tenant.md` (plan 1) y `docs/superpowers/plans/2026-10-21-zenvia-por-tenant.md` (plan 2, recortado a Zenvia).

**Worktree:** `../qep-backend-worktrees/envio-por-canal-3`, rama `feature/envio-por-canal-3`, sacada de `develop`. Necesita su propio índice `.codegraph/` (no se copia el del checkout principal). Las rutas de este plan son relativas a ese worktree.

---

## Lo que ya está construido (no se planea)

| Parte del spec | Evidencia |
| --- | --- |
| D1, D18, §7.3: sin cuenta global, sin `LogWhatsAppSender` | `eeb65e49`. `WhatsAppOptions` (`QuotationsOptions.cs:29`) sólo tiene `DocumentUrlHours` (lo usa `QuotationFileLookup`) y `BaseUrl`. `rg "Quotations:WhatsApp:(ApiToken\|FromNumber\|TemplateId)"` sólo encuentra docs. |
| D11, §5.1: `templateId` en la conexión de Zenvia (y `orderTemplateId`, plan 2 R4) | `c920f649`, `ZenviaFieldKeys.TemplateId` |
| §4.3 Zenvia: envío con la conexión resuelta | `251e17c7` (`ZenviaWhatsAppSender` recibe `WhatsAppChannel`), `5d85d7ba` (`QuotationsWhatsAppChannelDirectory`) |
| §4.2 pasos 1–2 y «varias ⇒ `channel_required`» (sólo Zenvia) | `WhatsAppChannelSelection` (`251e17c7`), `WhatsAppChannelApiTests` (`29295837`) |
| D2, D3, D12–D15, §4.5, §5.1 (marca de deshabilitado), §5.3 | Plan 1: `6d98fc86`, `fd17e3bf` (backfill D14), `20a23dd8` (`integrations.provider.not_enabled`, `ProviderVisibility`), `IntegrationsDtos.cs:73` (`ProviderEnabled`) |
| §8: permiso del operador | Plan 1, Global Constraints: `operator.modules.manage` |
| §7.1–§7.2: Origen Botánico y el `templateId` de su conexión | Plan 2 R3 (a mano, sin migración). Procedimiento en README § Plantilla de WhatsApp (Zenvia), «En producción» |
| Total con los separadores del tenant | Spec 2026-10-23, merge `a7c559be`: `ZenviaWhatsAppSender.FormatTotal` con `RegionalNumberFormat.ToNumberFormatInfo` |
| Señal de plantilla de Meta aprobada | `IQuotationTemplateReadiness.FindApprovedAsync` (`TemplateManagementPorts.cs:49`), `QuotationTemplateReadiness` (Infrastructure) |

## Desvíos respecto del spec (gana el código)

1. **§4.3, encabezado «con la URL publicada del PDF».** Va el **`id` de media de Meta**, no un `link`. Hay tres motivos en el código. Meta no baja una URL prefirmada de R2 (comentario de `SendQuotationHandler`, líneas 102–105, y memoria del proyecto). La copia pública vive bajo `quotations/`, que limpia el lifecycle del bucket. Y todo documento saliente de WhatsApp ya se sube a Meta y se manda por `id` (`SendMediaMessageHandler.PrepareAsync`, `IWhatsAppCloudClient.UploadMediaAsync`). §8 deja esta elección al constructor. Ver la Decisión 4.
2. **D16b, «la conversación se busca por el teléfono de destino».** `ConversationOpener.FindAsync` busca hoy por BSUID, por teléfono con BSUID, por `customer_id` y por teléfono viejo. El paso por `customer_id` puede devolver la conversación de **otro número**, que es justo lo que D16b quiere evitar con `Billing`. El plan agrega una apertura sólo por teléfono (Decisión 5). Así cumple D16b también con `Customer`, y el `customer_id` se usa sólo al insertar.
3. **§4.2 paso 5.** Si el `connectionId` del request es **igual** a la elección vigente, se acepta sin guardar nada. Sólo uno distinto responde `channel_already_set`. La regla del spec (el diálogo no sirve para cambiar la configuración) se mantiene, y dos pestañas que eligieron lo mismo no reciben un error sin sentido.
4. **§10, «formatear monto y fecha con el formato regional queda fuera (hoy `es-CO` fijo)».** Eso ya no es así: el spec 2026-10-23 le puso al total los separadores del tenant. Meta usa el mismo helper que Zenvia (`WhatsAppTemplateValues`), así que el total sale con los separadores del tenant y la vigencia sigue en español largo. Esto cierra la DP-1 del spec 2026-10-23 en lo que toca al total.
5. **El spec habla sólo de la cotización.** El código también envía el pedido por WhatsApp (plan 2 R4, `SendOrderHandler`, `orderTemplateId`), y Meta no tiene plantilla de pedido: `MessageTemplateKind` es `Free | Quotation | External` y `QuotationTemplateDefinition` es sólo de cotización. El pedido sigue saliendo sólo por Zenvia (Decisión 12). Lo demás va como DP-1 y DP-2.

## Decisiones del plan

1. **El estado de la elección vive en una tabla propia:** `quotations.tenant_whatsapp_channels` (`tenant_id` PK, `connection_id`, `chosen_at`, `chosen_by_member_id`). No es una columna de `tenant_quotation_settings`, porque `QuotationSettingsStore` la lee con un `FromSql … FOR UPDATE` que debe listar todas las columnas (gotcha de CLAUDE.md). Tampoco lleva FK a `integrations`, que es otro esquema: una conexión borrada deja una fila vencida, y D10 la trata como «no elegida».
2. **El Strategy queda en dos capas.** `IWhatsAppSender` (la fachada que ya usan los handlers y que reemplazan los hosts de prueba) lo implementa `WhatsAppSenderRouter` (Application, scoped). El router despacha por tipo de canal a `IZenviaWhatsAppSender` (Infrastructure, singleton, el `ZenviaWhatsAppSender` de hoy) o a `IMetaWhatsAppSender` (puerto, adaptador `QuotationsMetaWhatsAppSender` en Bootstrapper, scoped). El teléfono se normaliza y se valida en el router, en un solo lugar para los dos (`WhatsAppPhone.Normalize`).
3. **Messaging expone `IQuotationTemplateOutbound`**, público en `Modules.Messaging.Application`, como `IBotOutbound` (`BotOutbound.cs:26`). Recibe valores ya formateados y un `ReadOnlyMemory<byte>` con el PDF. Los nombres de las variables (`fullname`, `order_number`, `total`, `valid_until`) siguen en un solo lugar, `QuotationTemplateDefinition.Variables`, y Quotations no los conoce.
4. **El PDF llega a Messaging como bytes del bucket privado**, por `IObjectStorage.DownloadAsync(storageKey)` en el adaptador de Bootstrapper. No se baja de la URL pública. Messaging lo escribe en `MessagingMediaKeys.For(tenant, messageId)` con `IMessagingMediaStore` (D16c: no lo borra el lifecycle de `quotations/`). Lo sube a Meta con `UploadMediaAsync(sender, stream, length, "application/pdf", "document.pdf")` y manda el encabezado con `{ "id", "filename" }`. El nombre que ve la persona es `Cotizacion-{número}.pdf`, el mismo de `ExportQuotationPdf.cs:39`. Por Meta no se publica la copia pública: `SendQuotationHandler` llama a `PublishAsync` sólo cuando el canal es de Zenvia.
5. **La conversación se abre por teléfono:** `IConversationOpener.OpenByPhoneAsync`. Primero busca la conversación con BSUID y ese `wa_id` (la de actividad más reciente), después la fila vieja sin BSUID con ese `wa_id`. Si no hay, inserta `Open`, sin asignar, con `user_id NULL`, `wa_id` = dígitos y `customer_id` = el del cliente con destinatario `Customer` o `NULL` con `Billing` (D16b). Sigue la misma regla de carrera que `OpenAsync` (`ON CONFLICT DO NOTHING` y volver a buscar). Cuando el cliente responda, la ingesta adopta la fila sin BSUID (paso 1a del spec 2026-10-10). No se escribe ningún evento de hilo (no hay `Taken`), así que el gotcha de `occurred_at` distinto no aplica.
6. **Sin guardas de la bandeja (D16a).** Ni `MessagingAuthorization`, ni `AdmitTemplateAsync`, ni autoasignación. El estado y la asignación no se tocan: `SettleAsync(…, seen: 0)` y `MarkRepliedAsync` no hace nada con `seen <= 0` (`ConversationRepository.cs:53`). El autor es el miembro que envía (`sent_by_member_id`).
7. **Una falla de Meta después del reclamo deja la burbuja `Failed` en el hilo**, como cualquier saliente (`OutboundSettlement`). La cotización sigue en borrador, con `SendFailed` en la etapa `WhatsApp`, y el 422 es `quotation.whatsapp.send_failed`, el mismo código de Zenvia. El `clientId` es nuevo en cada intento: no hay una clave estable por intento que no deduplique también un reenvío legítimo.
8. **`channel_required` viaja sin la lista de canales.** La pantalla vuelve a pedir `GET whatsapp-channels`, y §4.2 lo permite («o suficiente información»). Agregar la lista exigiría abrir `QuotationsDomainException` (`sealed`) a `IHasProblemExtensions`.
9. **La carrera de la primera elección la gana el primero.** El guardado desde el envío es un `INSERT … ON CONFLICT (tenant_id) DO UPDATE … WHERE connection_id = {anterior}`: inserta si no hay fila y reemplaza sólo la fila vencida que la resolución vio (D10). Si otro miembro eligió en el medio, no se pisa. Su envío ya salió por el canal que eligió y no falla después de que el WhatsApp llegó. Va en una transacción explícita con el `Sent`: si el guardado falla, tampoco queda la elección (§4.2 paso 4).
10. **Las claves de proveedor en Quotations:** `WhatsAppChannelProviders.Meta = "whatsapp-cloud"` y `.Zenvia = "zenvia"`, amarradas a `IntegrationProviders.*.Key` por una prueba de paridad en `Bootstrapper.UnitTests`, como `ShopifyWebhookTopicsParityTests`. Quotations no referencia Integrations.
11. **El orden y el teléfono de la lista.** Primero Meta y después Zenvia, cada grupo por nombre, igual que `MessagingConnectionDirectory.ListActiveAsync`. `phone` es el que guarda la conexión, sin formatear: `displayPhoneNumber` en Meta y `fromNumber` en Zenvia.
12. **El pedido sólo usa canales de Zenvia.** `WhatsAppChannelSelection.ForOrder` filtra `ZenviaWhatsAppChannel` y conserva las reglas del plan 2. Un tenant sólo con Meta responde `order.whatsapp.no_channel` (DP-1). La elección guardada no aplica al pedido (DP-2).
13. **El `PUT` acepta cualquier canal disponible, también si hay uno solo.** No hace daño, y la pantalla decide si muestra la sección. La auditoría es `quotations.whatsapp_channel.chosen`, por los dos caminos.
14. **No hay permisos nuevos.** `QuotationsPermissions.QuotationManage` y `TenancyPermissions.SettingsUpdate` ya tienen su política: las usan `QuotationEndpoints` y `QuotationSettingsEndpoints`.
15. **Sin siembra de la elección (owner, 2026-10-25).** La migración `AddTenantWhatsAppChannel` crea la tabla vacía: no
    deja elegida la conexión de Zenvia en los tenants que hoy envían por ella. El proyecto sigue en desarrollo, y al
    publicar en `main` el owner configura a mano el canal del único tenant que usa Zenvia. No agregues un backfill.

## Decisiones pendientes

- **DECISIÓN-PENDIENTE DP-1. El pedido por Meta.** Hoy no hay plantilla de pedido en Meta, así que un tenant que sólo tiene Meta no puede enviar pedidos (`order.whatsapp.no_channel`). Tienes dos opciones: (a) dejarlo así; (b) un spec nuevo para una plantilla de pedido aprovisionada por QEP (`MessageTemplateKind.Order`, definición, aprovisionamiento, `FindApprovedAsync` por tipo). Esa plantilla necesita aprobación de Meta en cada conexión. **No bloquea este plan.**
- **DECISIÓN-PENDIENTE DP-2. ¿El pedido respeta la elección del canal?** Con dos o más conexiones de Zenvia, el pedido sigue respondiendo `order.whatsapp.channel_required` y no hay forma de elegir. Opciones: (a) usar la elección de cotizaciones si es de Zenvia y tiene `orderTemplateId`; (b) una elección propia para pedidos; (c) dejarlo. **No bloquea.**
- **DECISIÓN-PENDIENTE DP-3. El ejemplo de `total` de la plantilla de Meta.** `QuotationTemplateDefinition.Examples["total"] = "2.450.000 COP"`, pero lo que se manda es `"$ 2.450.000"` (símbolo antes, separadores del tenant). Es sólo el ejemplo que revisa Meta. Cambiarlo crea una versión nueva de la plantilla (`qep_cotizacion_vN`), con aprobación nueva en cada conexión. Este plan no lo toca.

## Global Constraints

- TDD obligatorio: RED antes que GREEN, con la salida literal de los dos.
- Conventional commits en español que citan el spec, p. ej. `(spec 2026-10-21 D16)`. **Sin atribución de IA**: ni `Co-Authored-By` ni «Generated with». Revisa cada commit de un subagente.
- Comandos en PowerShell. Las pruebas de integración necesitan `$env:Seed__Enabled = "false"` en su propia línea, antes del `dotnet test`.
- `Api.exe` (o `dotnet Api.dll`) corriendo bloquea el build: deténlo primero.
- Por tarea se corren sólo las clases tocadas. La corrida combinada va una vez, al final (Task 7).
- Migraciones: `dotnet ef migrations add <Nombre> --project src/Modules/<M>/Modules.<M>.Infrastructure --context <M>DbContext -o Persistence/Migrations`.
- `QuotationsLayerTests`, `MessagingLayerTests` e `IntegrationsLayerTests` siguen verdes: Quotations no ve Messaging ni Integrations, y Messaging no ve Quotations, Integrations ni Storage.
- Códigos nuevos, como `QuotationsDomainException` (422 por `ApiExceptionHandler`): `quotation.whatsapp.channel_unavailable` y `quotation.whatsapp.channel_already_set`. No se arma ningún status a mano.
- La resolución del canal va **fuera** del `try` que registra fallas (como `EnsureSendable` y el plan 2): un `no_channel`, `channel_required`, `channel_unavailable` o `channel_already_set` no deja `SendFailed` ni cambia la cotización.
- Ni el token, ni el teléfono, ni la URL del PDF entran a un log. `ZenviaWhatsAppChannel.ToString()` no imprime `ApiToken`.
- Nombre del documento: `Cotizacion-{QuotationNumber}.pdf`. Mime: `application/pdf`.
- No se toca `docs/estado.md` en la rama: se actualiza en `develop`, al mergear.
- Comentarios y textos para personas, tuteando y sin voseo.

## Review Focus

1. **Una elección guardada que deja de estar disponible** (se borra, se pausa, su plantilla de Meta deja de estar `APPROVED`, el operador deshabilita Zenvia). Con varias disponibles responde `channel_required`, y un envío con `connectionId` reemplaza la fila vencida. Con una sola disponible usa esa y no reescribe nada (D10). Pruebas en la Task 5: `AStaleChoiceWithSeveralOthersAsksAgainAndTheNewChoiceReplacesIt` y `AStaleChoiceWithOneOtherUsesItWithoutSaving`.
2. **Dos miembros hacen la primera elección a la vez, con conexiones distintas.** Queda la primera y la segunda no la pisa. El segundo envío igual sale y la cotización queda `Sent`. Prueba en la Task 5: `RememberDoesNotOverwriteAChoiceMadeMeanwhile`.
3. **Destinatario `Billing`, o un cliente cuya conversación es de otro número.** La plantilla no puede caer en el hilo de otro número (D16b). Pruebas en la Task 2: `BillingIsLookedUpByPhoneOnlyAndCarriesNoCustomer` y `ACustomerConversationOnAnotherNumberIsNotReused`.
4. **Conversación resuelta o asignada a otra persona.** El envío entra en ese hilo y no cambia el estado, la asignación ni `unread_count` (D16a). Prueba en la Task 2: `AResolvedConversationAssignedToOtherKeepsItsStateAssignmentAndUnread`.
5. **Meta rechaza la plantilla** (p. ej. pausada, `132015`). La cotización sigue en borrador con `SendFailed` en la etapa `WhatsApp`, la API responde `quotation.whatsapp.send_failed`, la fila del hilo queda `Failed` con su PDF y no se guarda la elección. Pruebas en la Task 2 (`MetaRejectingTheTemplateLeavesAFailedMessageWithItsDocument`), la Task 4 (`AnythingButSentIsSendFailed`) y la Task 5 (`AFailedSendDoesNotRememberTheChoice`).

---

## Mapa de archivos

| Archivo | Qué cambia |
| --- | --- |
| `src/Modules/Messaging/Modules.Messaging.Application/MessageTemplates.cs` | `TemplateSend` gana `HeaderDocument` |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Meta/WhatsAppCloudClient.cs` | `SendTemplateAsync` manda el componente `header` |
| `src/Modules/Messaging/Modules.Messaging.Application/QuotationTemplateProvisioning.cs` | `QuotationTemplateDefinition.Render` y `ParametersFor` |
| `src/Modules/Messaging/Modules.Messaging.Application/IMessageTemplates.cs` + `Infrastructure/Persistence/ConversationOpener.cs` | `OpenByPhoneAsync` |
| `src/Modules/Messaging/Modules.Messaging.Application/QuotationTemplateOutbound.cs` (nuevo) | El servicio que consume Quotations |
| `src/Modules/Quotations/Modules.Quotations.Application/IWhatsAppChannelDirectory.cs` | Canal con dos variantes |
| `src/Modules/Quotations/Modules.Quotations.Application/IWhatsAppSender.cs` | Mensaje con `DocumentStorageKey`, autor y cliente; puertos por proveedor |
| `src/Modules/Quotations/Modules.Quotations.Application/WhatsAppSenderRouter.cs`, `WhatsAppTemplateValues.cs`, `WhatsAppPhone.cs` (nuevos) | Strategy y helpers compartidos |
| `src/Modules/Quotations/Modules.Quotations.Application/WhatsAppChannelSelection.cs` | Resolución con elección (D6–D10); `ForOrder` filtra Zenvia |
| `src/Modules/Quotations/Modules.Quotations.Application/WhatsAppChannels.cs` (nuevo) | Query, comando, DTOs, puerto de la elección |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/WhatsAppChannelChoiceStore.cs` (nuevo) + migración | La tabla |
| `src/Modules/Quotations/Modules.Quotations.Api/WhatsAppChannelEndpoints.cs` (nuevo) | `GET` y `PUT` |
| `src/Bootstrapper/QuotationsWhatsAppChannelDirectory.cs`, `QuotationsMetaWhatsAppSender.cs` (nuevo), `QepServiceCollectionExtensions.cs` | Adaptadores |

---

### Task 1: Plantilla con encabezado de documento en el cliente de Meta (Messaging)

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessageTemplates.cs:98`. A `TemplateSend` se le agrega el parámetro opcional final `TemplateHeaderDocument? HeaderDocument = null`, y en el mismo archivo el record `public sealed record TemplateHeaderDocument(string MediaId, string FileName);`.
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Meta/WhatsAppCloudClient.cs:167` (`SendTemplateAsync`). Con `HeaderDocument`, el **primer** componente es `{"type":"header","parameters":[{"type":"document","document":{"id":…,"filename":…}}]}`.
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/QuotationTemplateProvisioning.cs` (`QuotationTemplateDefinition`).
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/WhatsAppCloudClientTemplatesTests.cs`, `QuotationTemplateDefinitionTests.cs`.

**Interfaces:**
- Produces:
  - `TemplateHeaderDocument(string MediaId, string FileName)`.
  - `QuotationTemplateDefinition.ParametersFor(string fullName, string orderNumber, string total, string validUntil) : IReadOnlyList<TemplateParameter>`, en el orden de `Variables`.
  - `QuotationTemplateDefinition.Render(IReadOnlyList<TemplateParameter> parameters) : string`: el cuerpo con las variables sustituidas, una línea en blanco y `Footer`. Mismo formato que `TemplateCatalog.Render`.

- [ ] **Step 1: Escribir las pruebas que fallan**
  - `WhatsAppCloudClientTemplatesTests.ADocumentHeaderTravelsAsTheFirstComponent`: `TemplateSend("qep_cotizacion_v2", "es", Named: true, [fullname…], HeaderDocument: new("media.out", "Cotizacion-COT-1.pdf"))`. En el cuerpo enviado, `template.components[0]` es `{type:"header", parameters:[{type:"document", document:{id:"media.out", filename:"Cotizacion-COT-1.pdf"}}]}` y `components[1].type == "body"`, con los `parameter_name` en orden.
  - `WhatsAppCloudClientTemplatesTests.WithoutAHeaderDocumentTheFirstComponentIsTheBody`: regresión del camino de la bandeja y las campañas.
  - `QuotationTemplateDefinitionTests.ParametersFollowTheVariablesOrder`: las claves son `["fullname","order_number","total","valid_until"]`.
  - `QuotationTemplateDefinitionTests.RenderFillsTheFourVariablesAndAppendsTheFooter`: con `("Juan Perez","COT-000123","$ 2.450.000","30 de septiembre de 2026")` da `"¡Hola, Juan Perez! Adjuntamos la cotización *COT-000123* por *$ 2.450.000*, vigente hasta el *30 de septiembre de 2026*.\n\nEste mensaje fue generado automáticamente."`.
- [ ] **Step 2: RED.**
  ```powershell
  dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~WhatsAppCloudClientTemplatesTests|FullyQualifiedName~QuotationTemplateDefinitionTests"
  ```
  Falla por compilación: `TemplateHeaderDocument`, `ParametersFor` y `Render` no existen.
- [ ] **Step 3: Implementar** las tres piezas. `Render` reusa `TemplateCatalog.Placeholder()` (internal, mismo ensamblado).
- [ ] **Step 4: GREEN.** El mismo comando, más `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~Template"` (campañas y bandeja siguen verdes).
- [ ] **Step 5: Commit.** `feat(messaging): plantilla con encabezado de documento por id de media (spec 2026-10-21 D16c)`

### Task 2: Messaging envía la plantilla de cotización sin las guardas de la bandeja

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IMessageTemplates.cs:67` (`IConversationOpener`): se agrega `OpenByPhoneAsync`.
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ConversationOpener.cs`: la implementa en SQL, como `OpenAsync` (Decisión 5).
- Modify: `tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateTestDoubles.cs:139` (`FakeConversationOpener`): implementa el método nuevo con `throw new NotSupportedException()`, porque ninguna prueba unitaria lo usa.
- Create: `src/Modules/Messaging/Modules.Messaging.Application/QuotationTemplateOutbound.cs`.
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs:28`: `services.AddScoped<IQuotationTemplateOutbound, QuotationTemplateOutbound>();` junto a `IBotOutbound`.
- Create: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/QuotationTemplateOutboundTests.cs`, con el patrón de `BotOutboundTests` (servicio resuelto de un scope) y de `SendMediaMessageApiTests` (un `FakeBucket` como `IMessagingMediaStore`; si hace falta en dos clases, muévelo a `MessagingApiHarness`).

**Interfaces:**
- Consumes: `TemplateHeaderDocument`, `QuotationTemplateDefinition.ParametersFor` / `Render` (Task 1).
- Produces (namespace `Modules.Messaging.Application`, públicos):

```csharp
public sealed record QuotationTemplateSend(
    Guid TenantId, Guid ConnectionId, Guid SentByMemberId,
    string WaId,                 // sólo dígitos, con indicativo
    Guid? CustomerId,            // null con destinatario Billing (D16b)
    string RecipientName,        // profile_name de una conversación nueva
    string TemplateName, string TemplateLanguage,   // de FindApprovedAsync, nunca la constante
    string FullName, string OrderNumber, string Total, string ValidUntil,  // ya formateados
    ReadOnlyMemory<byte> Document, string DocumentFileName);

public abstract record QuotationTemplateSendResult
{
    public sealed record Sent(Guid MessageId, Guid ConversationId) : QuotationTemplateSendResult;
    public sealed record ConnectionUnavailable(string? Reason) : QuotationTemplateSendResult;
    public sealed record Failed(string Code) : QuotationTemplateSendResult;
}

public interface IQuotationTemplateOutbound
{
    Task<QuotationTemplateSendResult> SendAsync(QuotationTemplateSend send, CancellationToken cancellationToken);
}

// IConversationOpener, además de OpenAsync:
Task<OpenedConversation> OpenByPhoneAsync(
    Guid tenantId, Guid connectionId, string waId, Guid? customerId, string profileName, DateTimeOffset now,
    CancellationToken cancellationToken);
```

Orden de `QuotationTemplateOutbound.SendAsync` (las dependencias son las de `BotOutbound` más `IConversationOpener`, `IConversationQueries` e `IMessagingMediaStore`):
1. Cada valor pasa `TemplateParameterRules.IsValid`. Si alguno no, `Failed(MessagingErrorCodes.MessageRejected)` sin efectos.
2. `connections.ResolveSenderWithReasonAsync`. Sin sender, `ConnectionUnavailable(reason)`.
3. `meta.UploadMediaAsync(sender, stream, length, "application/pdf", "document.pdf")`, **antes** del reclamo (como `SendMediaMessageHandler`). Si falla, `Failed(MessagingErrorCodes.MediaUploadFailed)` sin fila.
4. `opener.OpenByPhoneAsync(…, assigned: nadie)` y después `conversations.FindAsync`.
5. Reclamo con `OutboundDraft`: `Kind = Template`, `Text = Render(...)`, `DetailsJson = TemplateDetailsJson.Serialize(name, language, QuotationTemplateDefinition.Category, values)`, `SentByMemberId`, `Media = new OutboundMedia("application/pdf", length, sha256, DocumentFileName)` y un `clientId` nuevo.
6. `store.UploadAsync(MessagingMediaKeys.For(tenant, claim.MessageId), …)`. Si falla, rollback del reclamo y `Failed(MediaUploadFailed)`.
7. `meta.SendTemplateAsync(sender, SendTarget.ForTemplate(row.UserId, row.WaId), new TemplateSend(name, language, Named: true, parameters, HeaderDocument: new(mediaId, fileName)), callback, CancellationToken.None)`.
8. `new OutboundSettlement(…).SettleAsync(…, metaMediaId: mediaId, afterRollback: borrar el objeto si la fila es nueva, seen: 0)`. Un `MessagingDomainException` `ConnectionUnavailable` se traduce a `ConnectionUnavailable(BotOutbound.CredentialsRejected)`; cualquier otro, a `Failed(exception.Code)`.
9. `Sent(claim.MessageId, conversationId)`.

- [ ] **Step 1: Escribir las pruebas que fallan** (integración; conexión sembrada con `SeedWhatsAppConnectionAsync(…, "111", "222")`; Meta responde `/111/media` → `{"id":"media.out"}` y `/111/messages` → `wamid.q`)
  - `SendsTheTemplateWithItsDocumentIntoANewOpenUnassignedConversation`. Sin conversación previa: `Sent`. Hay una conversación `Open` con `assigned_member_id IS NULL`, `wa_id = '573001234567'`, `user_id IS NULL` y `customer_id` = el enviado. El mensaje tiene `sent_by_member_id` = el miembro, `details->>'name' = 'qep_cotizacion_v2'` y `text` igual a `Render`. Su `message_media` tiene `mime_type = 'application/pdf'`, `file_name = 'Cotizacion-COT-1.pdf'`, `meta_media_id = 'media.out'`, `storage_key = 'messaging/{tenant}/{messageId}'` y `stored_at` no nulo, y el bucket guarda los bytes. A Meta le llega primero `/111/media` y después `/111/messages`, con el header de documento y `to = "573001234567"`.
  - `UsesTheExistingConversationOfThatPhone`. Con un entrante previo de `573001234567` (webhook): devuelve ese `ConversationId` y no hay una segunda conversación.
  - `AResolvedConversationAssignedToOtherKeepsItsStateAssignmentAndUnread` (Review Focus 4). Se siembra la conversación `Resolved`, asignada a otro miembro y con `unread_count = 2`. Después del envío siguen `Resolved`, el mismo asignado y `unread_count = 2`, y `version` no cambió.
  - `BillingIsLookedUpByPhoneOnlyAndCarriesNoCustomer` (Review Focus 3). El cliente C tiene conversación en `573001111111` con `customer_id = C`. Se envía a `573002222222` con `CustomerId = null`: la conversación es nueva, `customer_id IS NULL`, y la de C no tiene mensajes nuevos.
  - `ACustomerConversationOnAnotherNumberIsNotReused` (Review Focus 3). Lo mismo, pero con `CustomerId = C`: la conversación es nueva y tiene `customer_id = C`.
  - `AConnectionThatIsNotActiveReturnsConnectionUnavailableAndWritesNothing`. Con la conexión pausada: `ConnectionUnavailable`, cero filas en `messaging.messages` y ninguna llamada a Meta.
  - `MetaRejectingTheTemplateLeavesAFailedMessageWithItsDocument` (Review Focus 5). `/111/messages` responde 400 con `error.code = 132015`. El resultado es `Failed`, el mensaje queda con `status` Failed y su `message_media` sigue con el objeto en el bucket.
  - `AFailedUploadToMetaSendsNothingAndLeavesNoRow`. `/111/media` responde 500: `Failed("messaging.media.upload_failed")` (el valor de `MessagingErrorCodes.MediaUploadFailed`), sin filas y sin `/111/messages`.
  - `AnInvalidValueIsRejectedBeforeAnyCall`. `FullName = "Juan\nPerez"`: `Failed(MessagingErrorCodes.MessageRejected)`, sin conversación y sin llamadas a Meta.
- [ ] **Step 2: RED.**
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~QuotationTemplateOutboundTests"
  ```
  Falla por compilación: `IQuotationTemplateOutbound` no existe.
- [ ] **Step 3: Implementar** `OpenByPhoneAsync`, `QuotationTemplateOutbound` y el registro, siguiendo los nueve pasos de arriba. El `<summary>` de la clase cita D16a y explica por qué no usa `OutboundGuards`.
- [ ] **Step 4: GREEN.** El mismo comando, más:
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~StartConversationApiTests|FullyQualifiedName~BotOutboundTests|FullyQualifiedName~InboundBsuidTests"
  dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests
  dotnet test tests/ArchitectureTests --filter "FullyQualifiedName~MessagingLayerTests"
  ```
- [ ] **Step 5: Commit.** `feat(messaging): envío de la plantilla de cotización al hilo del número, sin guardas de la bandeja (spec 2026-10-21 D16, D16a, D16b, D16c)`

### Task 3: El canal de Quotations con dos proveedores y el Strategy de envío

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IWhatsAppChannelDirectory.cs`. `WhatsAppChannel` pasa a abstracto, con dos variantes (abajo). El `<summary>` de `ListAvailableAsync` dice: Meta (`Active`, visible, plantilla de cotización `APPROVED`) y Zenvia (como hoy).
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IWhatsAppSender.cs`. Cambian `WhatsAppQuotationMessage` y `WhatsAppOrderMessage` (abajo) y se agregan los puertos por proveedor.
- Create: `src/Modules/Quotations/Modules.Quotations.Application/WhatsAppSenderRouter.cs`, `WhatsAppTemplateValues.cs` y `WhatsAppPhone.cs`.
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/WhatsAppChannelSelection.cs`. `ForOrder` filtra `ZenviaWhatsAppChannel` antes de contar (Decisión 12). `ForQuotation` queda igual hasta la Task 5.
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/SendQuotation.cs`. `PublishAsync` sólo con canal de Zenvia. El mensaje lleva `DocumentStorageKey = pdf.StorageKey`, `SentByMemberId` y `CustomerId` (= `quotation.ClientId` con `Customer`, `null` con `Billing`). `ResolveRecipient` devuelve también el `QuotationRecipient`, como el de `SendOrder`.
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/SendOrder.cs`: llena los campos nuevos de `WhatsAppOrderMessage`.
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/ZenviaWhatsAppSender.cs`. Implementa `IZenviaWhatsAppSender`. `FormatTotal`, `Colombia` y `NormalizePhone` pasan a `WhatsAppTemplateValues` y `WhatsAppPhone`, y un `DocumentUrl` nulo es `InvalidOperationException`.
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs:105` (`AddWhatsAppSender`). `IZenviaWhatsAppSender` queda singleton (la misma fábrica de hoy) y se agrega `services.AddScoped<IWhatsAppSender, WhatsAppSenderRouter>();`.
- Barrido de construcciones de `WhatsAppChannel(…)`, que pasan a `ZenviaWhatsAppChannel`: `rg -n "WhatsAppChannel" tests src/Bootstrapper`. Aparecen en `QuotationsTestDoubles.cs` (`WhatsAppChannels.Any`), `WhatsAppChannelTests.cs`, `ZenviaWhatsAppSenderTests.cs`, `WhatsAppTestDoubles.cs` (integración), `ReportingApiHarness.cs:1088` (`SingleWhatsAppChannel`), `QuotationsWhatsAppChannelDirectory.cs` y su prueba.
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/WhatsAppSenderRouterTests.cs`, `WhatsAppTemplateValuesTests.cs` y `WhatsAppPhoneTests.cs` (nuevos); `SendQuotationHandlerTests.cs`, `SendOrderHandlerTests.cs` y `WhatsAppChannelTests.cs`.

**Interfaces:**
- Produces (namespace `Modules.Quotations.Application`):

```csharp
public static class WhatsAppChannelProviders { public const string Meta = "whatsapp-cloud"; public const string Zenvia = "zenvia"; }

public abstract record WhatsAppChannel(Guid ConnectionId, string Name, string? Phone) { public abstract string Provider { get; } }

public sealed record ZenviaWhatsAppChannel(
    Guid ConnectionId, string Name, string ApiToken, string FromNumber, string QuotationTemplateId, string? OrderTemplateId)
    : WhatsAppChannel(ConnectionId, Name, FromNumber)            // Provider => Zenvia; ToString() sin ApiToken
public sealed record MetaWhatsAppChannel(
    Guid ConnectionId, string Name, string? Phone, string TemplateName, string TemplateLanguage)
    : WhatsAppChannel(ConnectionId, Name, Phone)                 // Provider => Meta

public sealed record WhatsAppQuotationMessage(
    string? ToPhone, string FullName, string OrderNumber, decimal Total, string Currency, DateOnly ValidUntil,
    string? DocumentUrl,                 // sólo Zenvia: la copia pública
    string? NumberFormat = null,
    string? DocumentStorageKey = null,   // la clave privada del PDF (Meta la lee)
    Guid? SentByMemberId = null, Guid? CustomerId = null);
// WhatsAppOrderMessage: los mismos tres campos nuevos al final; DocumentUrl pasa a string?.

public interface IZenviaWhatsAppSender
{
    Task SendQuotationAsync(ZenviaWhatsAppChannel channel, WhatsAppQuotationMessage message, CancellationToken cancellationToken);
    Task SendOrderAsync(ZenviaWhatsAppChannel channel, WhatsAppOrderMessage message, CancellationToken cancellationToken);
}

/// Puerto hacia Mensajería; adaptador en Bootstrapper (Task 4). Recibe el teléfono ya normalizado (sólo dígitos).
public interface IMetaWhatsAppSender
{
    Task SendQuotationAsync(MetaWhatsAppChannel channel, string waId, WhatsAppQuotationMessage message, CancellationToken cancellationToken);
}

public sealed class WhatsAppSenderRouter(IZenviaWhatsAppSender zenvia, IMetaWhatsAppSender meta) : IWhatsAppSender;

public static class WhatsAppTemplateValues
{
    public static string Total(decimal total, string currency, string? numberFormat);  // "$ 2.450.000"
    public static string ValidUntil(DateOnly validUntil);                              // "30 de septiembre de 2026" (es-CO)
    public static string DocumentFileName(string quotationNumber);                     // "Cotizacion-{n}.pdf"
}

public static class WhatsAppPhone { public static string? Normalize(string? phone); }  // la regla de hoy: 10 dígitos ⇒ "57" + dígitos
```

- Consumes: nada de otras tareas. `IMetaWhatsAppSender` no tiene adaptador hasta la Task 4, así que en esta tarea **sólo se corren las unitarias** (el host de integración no resolvería el router).

- [ ] **Step 1: Escribir las pruebas que fallan** (dobles nuevos en `QuotationsTestDoubles.cs`: `RecordingZenviaSender` y `RecordingMetaSender`, que guardan canal, teléfono y mensaje)
  - `WhatsAppSenderRouterTests.AZenviaChannelGoesToZenvia` / `AMetaChannelGoesToMetaWithTheNormalizedPhone`: con `ToPhone = "300 123 4567"`, Meta recibe `waId = "573001234567"`.
  - `WhatsAppSenderRouterTests.AQuotationWithoutPhoneIsRecipientMissingOnEitherProvider` (theory Zenvia/Meta): `quotation.whatsapp.recipient_missing`, y ningún sender recibió nada.
  - `WhatsAppSenderRouterTests.AnOrderOnAMetaChannelIsAProgrammingError`: `InvalidOperationException`.
  - `WhatsAppTemplateValuesTests.TotalUsesTheCurrencyAndTheTenantSeparators`: `(2450000m, "COP", null)` da `"$ 2.450.000"` y `(2450.5m, "EUR", "1,234.56")` da `"€ 2,450.50"`. Copia las cifras exactas de `ZenviaWhatsAppSenderTests`, que siguen siendo la fuente.
  - `WhatsAppTemplateValuesTests.ValidUntilIsTheLongSpanishDate`: `2026-09-30` da `"30 de septiembre de 2026"`.
  - `WhatsAppPhoneTests.TenDigitsGetTheColombianPrefix`, `LongerNumbersKeepTheirDigits` y `BlankOrDigitlessIsNull`.
  - `SendQuotationHandlerTests.AMetaChannelSkipsThePublicCopyAndCarriesTheStorageKey`: con un `MetaWhatsAppChannel`, `pdfStorage.PublishAsync` no se llamó. El mensaje tiene `DocumentUrl == null`, `DocumentStorageKey == pdf.StorageKey`, `SentByMemberId` = el miembro y `CustomerId == quotation.ClientId`.
  - `SendQuotationHandlerTests.AZenviaChannelStillPublishesTheCopy`.
  - `SendQuotationHandlerTests.ABillingRecipientCarriesNoCustomerId`.
  - `SendOrderHandlerTests.OrderIgnoresMetaChannels`: con Meta + un Zenvia sale por Zenvia; con sólo Meta da `order.whatsapp.no_channel`.
  - `WhatsAppChannelTests.ChannelToStringHidesTheToken`: se adapta a `ZenviaWhatsAppChannel`.
- [ ] **Step 2: RED.**
  ```powershell
  dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~WhatsApp|FullyQualifiedName~SendQuotationHandlerTests|FullyQualifiedName~SendOrderHandlerTests|FullyQualifiedName~ZenviaWhatsAppSenderTests"
  ```
  Falla por compilación.
- [ ] **Step 3: Implementar** los tipos, el router, los helpers, el filtro de `ForOrder`, los cambios de los handlers y el registro, y hacer el barrido.
- [ ] **Step 4: GREEN.**
  ```powershell
  dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests
  dotnet build tests/Modules/Reporting/Modules.Reporting.IntegrationTests
  dotnet test tests/ArchitectureTests --filter "FullyQualifiedName~QuotationsLayerTests"
  ```
  `ZenviaWhatsAppSenderTests` sigue verde sin cambiar las cifras esperadas: el payload de Zenvia no cambia.
- [ ] **Step 5: Commit.** `feat(quotations): canal de Meta o Zenvia y envío por Strategy de proveedor (spec 2026-10-21 D17, §4.3)`

### Task 4: Adaptadores en Bootstrapper: canales de Meta y envío por Mensajería

**Files:**
- Modify: `src/Bootstrapper/QuotationsWhatsAppChannelDirectory.cs`. Recibe también `IQuotationTemplateReadiness`. Primero lista `ListActiveAsync(tenant, IntegrationProviders.WhatsAppCloud.Key)`: por cada una, `ResolveAsync`, después `FindApprovedAsync(tenant, id)`, y devuelve `MetaWhatsAppChannel(id, resolved.Name, Fields[DisplayPhoneNumber] o null, template.Name, template.Language)`. Después Zenvia como hoy, con `Name = resolved.Name`.
- Create: `src/Bootstrapper/QuotationsMetaWhatsAppSender.cs`: `internal sealed class QuotationsMetaWhatsAppSender(IQuotationTemplateOutbound outbound, IObjectStorage storage) : IMetaWhatsAppSender`.
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:1050`: `services.AddScoped<IMetaWhatsAppSender, QuotationsMetaWhatsAppSender>();`.
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/QuotationsWhatsAppChannelDirectoryTests.cs` (lleva un doble de `IQuotationTemplateReadiness`), `QuotationsMetaWhatsAppSenderTests.cs` (nuevo) y `WhatsAppChannelProvidersParityTests.cs` (nuevo).

**Interfaces:**
- Consumes: `IQuotationTemplateOutbound`, `QuotationTemplateSend`, `QuotationTemplateSendResult` (Task 2); `MetaWhatsAppChannel`, `IMetaWhatsAppSender`, `WhatsAppTemplateValues` y `WhatsAppChannelProviders` (Task 3); `IObjectStorage.DownloadAsync(string key, CancellationToken)` (Storage).
- Produces: el sender arma `QuotationTemplateSend` con `TemplateName`/`TemplateLanguage` del canal, `WaId` del router, `RecipientName = message.FullName`, `Total = WhatsAppTemplateValues.Total(…)`, `ValidUntil = WhatsAppTemplateValues.ValidUntil(…)`, `Document = await storage.DownloadAsync(message.DocumentStorageKey!)` y `DocumentFileName = WhatsAppTemplateValues.DocumentFileName(message.OrderNumber)`. Si el resultado no es `Sent`, lanza `QuotationsDomainException("quotation.whatsapp.send_failed", "Meta did not accept the quotation ({code o motivo}).")`.

- [ ] **Step 1: Escribir las pruebas que fallan**
  - `QuotationsWhatsAppChannelDirectoryTests.MapsAMetaConnectionWithAnApprovedQuotationTemplate`: con `qep_cotizacion_v2`/`es`, el canal lleva esos valores, el nombre de la conexión y `displayPhoneNumber`.
  - `SkipsAMetaConnectionWithoutAnApprovedTemplate` (`FindApprovedAsync` devuelve `null`) y `SkipsAMetaConnectionThatDoesNotResolve`.
  - `ListsMetaFirstThenZenvia`.
  - `AsksIntegrationsForBothProviders`: reemplaza a `AsksIntegrationsForZenviaOnly`. El doble registra `"whatsapp-cloud"` y `"zenvia"`.
  - `ZenviaChannelCarriesTheConnectionName`. Las pruebas de Zenvia que ya existen se adaptan al tipo nuevo.
  - `QuotationsMetaWhatsAppSenderTests.SendsThePrivatePdfBytesTheTemplateAndTheFormattedValues`: el doble de `IQuotationTemplateOutbound` registra el `QuotationTemplateSend` y se comparan uno por uno los campos de *Produces*. Los bytes son los del doble de `IObjectStorage` para esa clave.
  - `QuotationsMetaWhatsAppSenderTests.AnythingButSentIsSendFailed` (theory con `ConnectionUnavailable("credentials_rejected")` y `Failed("messaging.message.rejected")`): `QuotationsDomainException` con `Code == "quotation.whatsapp.send_failed"` (Review Focus 5).
  - `WhatsAppChannelProvidersParityTests.TheQuotationsKeysAreTheIntegrationsKeys`: `WhatsAppChannelProviders.Meta == IntegrationProviders.WhatsAppCloud.Key` y lo mismo con Zenvia.
- [ ] **Step 2: RED.**
  ```powershell
  dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~QuotationsWhatsAppChannelDirectoryTests|FullyQualifiedName~QuotationsMetaWhatsAppSenderTests|FullyQualifiedName~WhatsAppChannelProvidersParityTests"
  ```
- [ ] **Step 3: Implementar** el directorio, el sender y el registro.
- [ ] **Step 4: GREEN.**
  ```powershell
  dotnet test tests/Bootstrapper/Bootstrapper.UnitTests
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~WhatsAppChannelApiTests|FullyQualifiedName~QuotationSendVoidApiTests|FullyQualifiedName~OrderSendApiTests"
  dotnet test tests/ArchitectureTests --filter "FullyQualifiedName~QuotationsLayerTests|FullyQualifiedName~MessagingLayerTests|FullyQualifiedName~IntegrationsLayerTests"
  ```
  Las de integración de Quotations vuelven a estar verdes: el host resuelve el router.
- [ ] **Step 5: Commit.** `feat(bootstrapper): canales de Meta con plantilla aprobada y envío de la cotización por Mensajería (spec 2026-10-21 §4.1, D16)`

### Task 5: La elección del canal, guardada y usada en el envío

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/WhatsAppChannels.cs`, con el puerto de la elección (los DTOs, la query y el comando entran en la Task 6, en el mismo archivo).
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/WhatsAppChannelSelection.cs`. `ForQuotation` se reemplaza por `Resolve` (abajo); `ForOrder` no cambia.
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/SendQuotation.cs`. `SendQuotationCommand` gana `Guid? ConnectionId = null`. Antes del `try`, se lee la elección y se resuelve. En la etapa `Persistence`, con `Remember`, la escritura va dentro de `unitOfWork.BeginTransactionAsync`: `RememberAsync`, después `SaveChangesAsync` y `CommitAsync`. Si `RememberAsync` devolvió `true`, se publica la auditoría `quotations.whatsapp_channel.chosen`.
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationsDtos.cs:417`. `SendQuotationRequest` gana `Guid? ConnectionId = null`, y su `<summary>` cita §4.2.
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/QuotationEndpoints.cs:489`: pasa `request?.ConnectionId`.
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/WhatsAppChannelChoiceStore.cs`, y `TenantWhatsAppChannelRow` junto a `TenantQuotationSettingsRow`, con configuración en `QuotationsDbContext` (tabla `tenant_whatsapp_channels`, PK `tenant_id`).
- Modify: `QuotationsInfrastructureExtensions.cs`: `services.AddScoped<IWhatsAppChannelChoiceStore, WhatsAppChannelChoiceStore>();`.
- Migración: `dotnet ef migrations add AddTenantWhatsAppChannel --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations`.
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs` (`InMemoryWhatsAppChannelChoiceStore`) y la construcción de `SendQuotationHandler` en `SendQuotationHandlerTests.cs`.
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppTestDoubles.cs`. Se agrega `RecordingWhatsAppChannelDirectory.Meta`, un `MetaWhatsAppChannel` fijo. `RecordingWhatsAppSender` gana `Exception? Failure`: si viene, se lanza en el envío.
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/WhatsAppChannelSelectionTests.cs` (nuevo), `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppChannelApiTests.cs` y `WhatsAppChannelChoiceStoreTests.cs` (nuevo).

**Interfaces:**
- Consumes: `WhatsAppChannel` y sus variantes (Task 3).
- Produces:

```csharp
public interface IWhatsAppChannelChoiceStore
{
    Task<Guid?> FindAsync(Guid tenantId, CancellationToken cancellationToken);
    /// Inserta, o reemplaza sólo si la fila guardada sigue siendo `previous` (Decisión 9). true si escribió.
    Task<bool> RememberAsync(Guid tenantId, Guid connectionId, Guid? previous, Guid chosenByMemberId, DateTimeOffset now, CancellationToken cancellationToken);
    /// Upsert incondicional (PUT desde Configuración).
    Task ReplaceAsync(Guid tenantId, Guid connectionId, Guid chosenByMemberId, DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed record ResolvedWhatsAppChannel(WhatsAppChannel Channel, bool Remember, Guid? PreviousChoice);

// WhatsAppChannelSelection:
public static ResolvedWhatsAppChannel Resolve(IReadOnlyList<WhatsAppChannel> available, Guid? saved, Guid? requested);
```

Reglas de `Resolve`, en orden (prefijo `quotation.whatsapp.`):
1. Sin canales ⇒ `no_channel`.
2. Hay una elección `saved` entre los disponibles. Sin `requested`, o con el mismo id, se usa esa (`Remember = false`). Con otro id ⇒ `channel_already_set`.
3. Hay un solo disponible. Sin `requested`, o con su id, se usa ese (`Remember = false`). Con otro id ⇒ `channel_unavailable`.
4. Hay varios. Sin `requested` ⇒ `channel_required`. Con un id que no está ⇒ `channel_unavailable`. Con uno que está ⇒ ese, con `Remember = true` y `PreviousChoice = saved` (la fila vencida o `null`).

El SQL de `RememberAsync` (`ExecuteSqlAsync`, dentro de la transacción del llamador):
`INSERT INTO quotations.tenant_whatsapp_channels (tenant_id, connection_id, chosen_at, chosen_by_member_id) VALUES (…) ON CONFLICT (tenant_id) DO UPDATE SET connection_id = EXCLUDED.connection_id, chosen_at = EXCLUDED.chosen_at, chosen_by_member_id = EXCLUDED.chosen_by_member_id WHERE quotations.tenant_whatsapp_channels.connection_id = {previous}`. Con `previous = null`, una fila existente no se toca. Devuelve `true` si escribió una fila.

- [ ] **Step 1: Escribir las pruebas que fallan**
  - `WhatsAppChannelSelectionTests` (unitarias, una por regla): `NoChannelIsNoChannel`, `AValidChoiceIsUsed`, `AValidChoiceAcceptsItsOwnId`, `AValidChoiceRejectsAnotherId` (`channel_already_set`), `TheOnlyChannelIsUsedWithoutRemembering`, `TheOnlyChannelWithAnotherIdIsUnavailable`, `SeveralWithoutChoiceRequireAConnectionId`, `SeveralWithAnUnknownIdIsUnavailable`, `SeveralWithAnAvailableIdIsRemembered` (`Remember`, `PreviousChoice == null`) y `AStaleChoiceIsTreatedAsNone` (`saved` fuera de la lista y dos disponibles: sin id da `channel_required`; con id, `PreviousChoice == saved`).
  - `SendQuotationHandlerTests.ARejectedChoiceTouchesNothing` (theory con `channel_already_set` y `channel_unavailable`): el renderer no se llamó, el log de fallas está vacío y la cotización sigue `Draft`.
  - `WhatsAppChannelApiTests.TheFirstChoiceIsSavedWithTheSendAndTheNextSendDoesNotAsk` (criterio 2): con Meta + Zenvia, enviar sin `connectionId` da 422 `channel_required`. Enviar con `connectionId = Meta` da 200, y el sender registró el canal Meta. Otra cotización, enviada por **otro miembro** sin `connectionId`, da 200 por Meta.
  - `WhatsAppChannelApiTests.AConnectionIdWithAValidChoiceIs422AlreadySet`: se elige Meta y después se envía con `connectionId = Zenvia`. Da 422 `quotation.whatsapp.channel_already_set`, la cotización sigue `Draft` y no hay `SendFailed`.
  - `WhatsAppChannelApiTests.AnUnavailableConnectionIdIs422`: un id que no está da `quotation.whatsapp.channel_unavailable`.
  - `WhatsAppChannelApiTests.AStaleChoiceWithSeveralOthersAsksAgainAndTheNewChoiceReplacesIt` (Review Focus 1): se elige A, se saca A del doble y quedan B y C. Sin id da `channel_required`. Con `connectionId = B` da 200, y el siguiente envío sale por B.
  - `WhatsAppChannelApiTests.AStaleChoiceWithOneOtherUsesItWithoutSaving` (Review Focus 1): se elige A, el doble queda sólo con B y el envío sale por B. Se vuelve a agregar A y el siguiente envío sale por A: la fila no se reescribió.
  - `WhatsAppChannelApiTests.AFailedSendDoesNotRememberTheChoice` (Review Focus 5): con `factory.WhatsAppSender.Failure` puesto, enviar con `connectionId = Meta` da 422. Se quita la falla y enviar sin id da `channel_required`.
  - `WhatsAppChannelChoiceStoreTests.RememberDoesNotOverwriteAChoiceMadeMeanwhile` (Review Focus 2; integración, el store se resuelve de un scope del host): `Remember(A, previous: null)` da `true` y `Remember(B, previous: null)` da `false`. `Find` devuelve A. Después `Remember(B, previous: A)` da `true` y `Find` devuelve B.
- [ ] **Step 2: RED.**
  ```powershell
  dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~WhatsAppChannelSelectionTests|FullyQualifiedName~SendQuotationHandlerTests"
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~WhatsAppChannelApiTests|FullyQualifiedName~WhatsAppChannelChoiceStoreTests"
  ```
- [ ] **Step 3: Implementar** el store, la entidad, la migración, `Resolve`, el handler, el request y el endpoint. Revisa que la migración cree sólo `quotations.tenant_whatsapp_channels`.
- [ ] **Step 4: GREEN.** Los mismos comandos, más:
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~QuotationSendVoidApiTests|FullyQualifiedName~OrderSendApiTests|FullyQualifiedName~Migration"
  dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --filter "FullyQualifiedName~OrdersReport"
  ```
- [ ] **Step 5: Commit.** `feat(quotations): la elección del canal de WhatsApp se guarda al enviar y la usan los envíos siguientes (spec 2026-10-21 D5–D10, §4.2)`

### Task 6: `GET /quotations/whatsapp-channels` y `PUT /quotations/whatsapp-channel`

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/WhatsAppChannels.cs`: DTOs, query, comando, validador y handlers.
- Create: `src/Modules/Quotations/Modules.Quotations.Api/WhatsAppChannelEndpoints.cs`: grupo `/api/v1/tenants/{tenantId:guid}/quotations`, tag `"Quotations"`.
- Modify: `src/Api/Program.cs:185`: `app.MapWhatsAppChannelEndpoints();`.
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppChannelApiTests.cs`.

**Interfaces:**
- Consumes: `IWhatsAppChannelDirectory`, `IWhatsAppChannelChoiceStore`, `WhatsAppChannelProviders` (Tasks 3 y 5).
- Produces:

```csharp
/// BFF: needsChoice viaja calculado para que la pantalla no repita la regla de §4.2; el teléfono va como lo guarda la conexión.
public sealed record WhatsAppChannelOptionDto(Guid ConnectionId, string Provider, string Name, string? Phone);
public sealed record WhatsAppChannelsDto(IReadOnlyList<WhatsAppChannelOptionDto> Channels, Guid? SelectedConnectionId, bool NeedsChoice);

public sealed record GetWhatsAppChannelsQuery(Guid TenantId) : IQuery<WhatsAppChannelsDto>;
public sealed record ChooseWhatsAppChannelCommand(Guid TenantId, Guid? ConnectionId) : ICommand<WhatsAppChannelsDto>;
public sealed class ChooseWhatsAppChannelValidator : AbstractValidator<ChooseWhatsAppChannelCommand>; // ConnectionId NotNull/NotEqual(Empty), OverridePropertyName("connectionId")
public sealed record ChooseWhatsAppChannelRequest(Guid? ConnectionId);
```

Reglas: `SelectedConnectionId` va sólo si la elección está entre los disponibles, y `NeedsChoice = Channels.Count > 1 && SelectedConnectionId is null`. `GET`: `RequireAuthorization(QuotationsPermissions.QuotationManage)`, y el handler revalida con `QuotationsAuthorization.EnsureAuthorized` y `TenantModuleGuard` (`quotations`). `PUT`: `TenancyPermissions.SettingsUpdate`, en este orden: autorizar, módulo, validador, disponible (si no, `quotation.whatsapp.channel_unavailable`), `ReplaceAsync` con el miembro de `QuotationAdvisorResolver` y auditoría `quotations.whatsapp_channel.chosen`. Responde el mismo `WhatsAppChannelsDto`, que el formulario repinta.

- [ ] **Step 1: Escribir las pruebas que fallan**
  - `ChannelsListsTheAvailableOnesAndTheValidChoice`: Meta + Zenvia, con Meta elegido. Viajan los dos en orden, con `provider` `whatsapp-cloud`/`zenvia`, `name` y `phone`, más `selectedConnectionId = Meta` y `needsChoice = false`.
  - `NeedsChoiceWithSeveralAndNoChoice` (`true`), `OneChannelNeverNeedsChoice` (`false`, `selectedConnectionId = null`) y `NoChannelsIsAnEmptyList` (`channels = []`, `needsChoice = false`).
  - `AStaleChoiceIsNotReported`: `selectedConnectionId = null` y `needsChoice = true`.
  - `ChannelsRequireQuotationManage`: 403 con un cliente que sólo tiene `quotations.quotation.read`.
  - `PutSavesAnAvailableChannelAndAnswersTheChannels`: 200 con `selectedConnectionId` = el enviado, y el envío siguiente sin id sale por él.
  - `PutWithAnUnavailableConnectionIs422ChannelUnavailable`.
  - `PutWithoutConnectionIdIs422WithTheFieldError`: `validation.failed` con `errors.connectionId`.
  - `PutRequiresSettingsUpdate` (criterio 3): un asesor con `quotations.quotation.manage` y sin `tenancy.settings.update` recibe 403. Ese mismo asesor sí hace la primera elección desde el envío.
- [ ] **Step 2: RED.**
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~WhatsAppChannelApiTests"
  ```
  Falla con 404, porque las rutas no existen.
- [ ] **Step 3: Implementar** handlers, endpoints y mapeo.
- [ ] **Step 4: GREEN.** El mismo comando, más `dotnet test tests/ArchitectureTests --filter "FullyQualifiedName~Quotations"`.
- [ ] **Step 5: Commit.** `feat(quotations): canales de WhatsApp del tenant y su elección desde Configuración (spec 2026-10-21 §4.4, §5.2)`

### Task 7: Documentación y corrida combinada

**Files:**
- Modify: `README.md`.
  - § Plantilla de WhatsApp (Zenvia) (línea 2163) y la línea 206: el envío sale por Meta o Zenvia; las reglas de §4.2 y los cuatro códigos; los dos endpoints nuevos.
  - § Mensajería (WhatsApp Cloud): la cotización por Meta queda en el hilo del número, con el PDF como media propia, y no cambia estado ni asignación.
  - § Enviar el pedido por WhatsApp (línea 1247): el pedido sigue sólo por Zenvia (DP-1).
  - Despliegue: backend antes que frontend (§7.4). Un tenant con Meta aprobado más Zenvia pasa a `channel_required` hasta que alguien elija, desde el envío o con el `PUT` (un administrador puede elegir antes del despliegue del frontend). Es una migración nueva de Quotations, sin backfill.
- Modify: `CLAUDE.md`, con una entrada nueva en Gotchas: «La cotización por Meta no usa `OutboundGuards` ni `ConversationOpener.OpenAsync`». `QuotationTemplateOutbound` busca por teléfono (`OpenByPhoneAsync`) y no por `customer_id`, para no caer en el hilo de otro número (D16b). Pasa `seen: 0` para no tocar no leídos (D16a). Y el PDF va por `id` de media, no por URL. Un cambio en el reclamo o la liquidación (`IOutboundMessages`, `OutboundSettlement`) se prueba también con `QuotationTemplateOutboundTests`.
- Verificación transversal: `rg -n "ForQuotation\(" src tests` no devuelve nada, y `rg -n "new WhatsAppChannel\(" src tests` tampoco.

- [ ] **Step 1: Corrida combinada, una sola vez**
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet build
  dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests
  dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests
  dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests
  dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests
  dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests
  dotnet test tests/Bootstrapper/Bootstrapper.UnitTests
  dotnet test tests/ArchitectureTests
  ```
  Compara las fallas **por nombre** contra la base de `develop` (memoria del proyecto: hay fallas previas conocidas). No se acepta ninguna nueva.
- [ ] **Step 2: Escribir la documentación** de arriba.
- [ ] **Step 3: Commit.** `docs: envío de cotizaciones por Meta y elección del canal (spec 2026-10-21 §7)`

## Cierre

- Revisión de toda la rama, y merge a `develop`, nunca a `main`. En el mismo merge, o justo después, el commit `docs:` de `docs/estado.md` en `develop`: «Envío por canal» pasa a construido en el backend y quedan el frontend y DP-1 a DP-3.
- Prueba manual antes de `main` (D18: en local, con conexiones reales del tenant Origen Botánico): Meta con la plantilla aprobada. La plantilla llega con el PDF, aparece en el hilo y el documento baja desde QEP.

---

## Contrato para el plan del frontend

Todas las rutas van bajo `/api/v1/tenants/{tenantId}`. Los errores son ProblemDetails con `code` y `traceId`. Un 422 de validación trae además `errors`.

**`GET /quotations/whatsapp-channels`.** Permiso `quotations.quotation.manage` y módulo `quotations`.
```json
{
  "channels": [
    { "connectionId": "…", "provider": "whatsapp-cloud", "name": "WhatsApp sede norte", "phone": "+57 300 123 4567" },
    { "connectionId": "…", "provider": "zenvia", "name": "Zenvia ventas", "phone": "573001234567" }
  ],
  "selectedConnectionId": "…" ,
  "needsChoice": false
}
```
- Orden: primero Meta y después Zenvia, cada grupo por nombre. `phone` puede ser `null` y viaja sin formatear.
- `selectedConnectionId` va sólo si la elección sigue disponible; si no, `null`. `needsChoice` = más de un canal y ninguno elegido vigente.
- Lista vacía = sin canales: el botón «Enviar por WhatsApp» queda deshabilitado (D9).

**`PUT /quotations/whatsapp-channel`.** Permiso `tenancy.settings.update`. Cuerpo `{ "connectionId": "…" }`. Responde 200 con la misma forma del `GET`.
- 422 `validation.failed` con `errors.connectionId` si falta o es `Guid.Empty`.
- 422 `quotation.whatsapp.channel_unavailable` si no está entre los disponibles.
- 403 sin el permiso.

**`POST /quotations/{id}/send`.** Permiso `quotations.quotation.manage`. Cuerpo opcional `{ "recipient": "Customer" | "Billing", "connectionId": "…" }`. La respuesta 200 no cambia (el detalle de la cotización). Códigos de §4.2, todos 422, que dejan la cotización como estaba y sin `SendFailed`:
- `quotation.whatsapp.no_channel`: no hay canales.
- `quotation.whatsapp.channel_required`: varios y ninguno elegido. Viaja sin la lista: vuelve a pedir el `GET` y abre el diálogo.
- `quotation.whatsapp.channel_unavailable`: el `connectionId` no está entre los disponibles.
- `quotation.whatsapp.channel_already_set`: ya hay una elección vigente y el `connectionId` es otro. El mismo id se acepta.

Con varios canales y ninguno elegido, un envío con `connectionId` válido **guarda** la elección para todo el tenant, pero sólo si el envío salió.

Fallas del envío, también 422, con la cotización en borrador y `SendFailed` en el historial: `quotation.whatsapp.send_failed` (Zenvia o Meta no aceptaron), `quotation.whatsapp.recipient_missing`, `quotation.quotation.billing_phone_missing` y `quotation.send.failed`. No dicen por qué canal fue: es el `selectedConnectionId`, o el único canal, del `GET` (Decisión 8).

**`POST /orders/{id}/send` no cambia.** Sólo sale por Zenvia: `order.whatsapp.no_channel` (también con Meta solo), `order.whatsapp.channel_required` y `order.whatsapp.not_configured`. No recibe `connectionId` (DP-1, DP-2).

**Mensajería.** La cotización enviada por Meta aparece en el hilo de ese número como un mensaje `kind: "Template"`, con `text` (cuerpo y pie ya armados), `template` (nombre e idioma) y `media` con la forma de un documento saliente: `url` `/api/v1/tenants/{tenantId}/messaging/media/{messageId}`, `mimeType: "application/pdf"` y `fileName: "Cotizacion-{número}.pdf"`. Puede quedar `Failed`. `sentBy` es quien envió la cotización.

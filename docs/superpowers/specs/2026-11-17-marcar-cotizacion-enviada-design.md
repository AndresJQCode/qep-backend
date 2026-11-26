# Marcar una cotización como enviada por fuera de QEP — diseño (backend)

Estado: aprobado por el owner, 2026-11-17. Tamaño: cambio chico.

## Intención

Un tenant sin canal de WhatsApp —o que prefiere otra vía— baja el PDF de la cotización
(`POST …/quotations/{id}/pdf`) y lo manda por fuera de QEP. Hoy esa cotización se queda en
`Draft` para siempre, porque la única salida a `Sent` es `SendQuotationHandler`, que exige canal
de WhatsApp y manda el mensaje antes de llamar a `Quotation.Send`. En `Draft`:

- nunca vence (`Quotation.Expire` sólo acepta `Sent`; `QuotationExpirationProcessor` filtra por `Sent`);
- no congela código y nombre de los productos (`CaptureProductSnapshots` corre en `Send`);
- Pipeline no se entera (no se publica `quotations.quotation.sent.v1`).

Se agrega una acción explícita del asesor: «Marcar como enviada».

## Objetivos

- Pasar a `Sent` sin canal, con las mismas precondiciones y los mismos efectos de dominio que el envío por WhatsApp.
- Que el historial distinga «la mandó QEP» de «el asesor dice que la mandó».

## No objetivos

- Estado nuevo (`Open` o similar): `Sent` ya significa «el cliente tiene el documento».
- Cambiar el envío por WhatsApp.
- Guardar por qué vía se mandó (correo, otra app): no se pidió.

## Anti-objetivos

- Que marcar como enviada se salte una precondición que el envío exige (`EnsureSendable`,
  `BillingPersonTypeRequirement`): sería una puerta trasera para mandar cotizaciones sin vigencia o sin cuenta de cobro.

## Enfoques considerados

1. **Dejar en `Draft`** — sin código; no vence, no congela, Pipeline no la ve. Descartado.
2. **Estado nuevo «Abierta»** — toca el enum, todas las reglas `Draft or Sent`, el diccionario del
   frontend, reportes y eventos, para un significado igual al de `Sent`. Descartado.
3. **Acción «Marcar como enviada» → `Sent`** — reusa `Quotation.Send`. **Elegido por el owner.**

## Diseño

- **Ruta:** `POST /api/v1/tenants/{tenantId}/quotations/{quotationId}/mark-sent`, sin cuerpo,
  `RequireAuthorization(QuotationsPermissions.QuotationManage)`, responde `QuotationResponse`
  (mismo patrón que `/send` y `/void`, que tampoco usan `If-Match`).
- **Comando:** `MarkQuotationSentCommand(TenantId, QuotationId)` → `MarkQuotationSentHandler`.
- **Orden del handler** (mismo que `SendQuotationHandler`, sin canal ni destinatario):
  1. `QuotationsAuthorization.EnsureAuthorized(..., QuotationManage)`.
  2. Cargar; `QuotationNotFound` si no existe.
  3. `quotation.EnsureSendable()` y `BillingPersonTypeRequirement.EnsureForQuotationAsync`.
  4. Resolver asesor (`QuotationAdvisorResolver`).
  5. `QuotationItemProductLabel.ResolveMissingAsync` y `pdfProvider.EnsureCurrentAsync(quotation)`:
     el PDF que se entregó queda generado y guardado en `quotation_pdfs`.
  6. `quotation.Send(sentBy, now, products)`.
  7. Historial `QuotationHistoryEventType.MarkedSent` (valor nuevo, **al final** del enum), con
     un resumen en español («Marcada como enviada por fuera de QEP»).
  8. Auditoría `quotation.quotation.marked_sent`.
  9. `lifecycleEvents.PublishSent(quotation, now)` en el mismo `SaveChanges` (contrato con Pipeline intacto).
- **Desde `Draft` y desde `Sent`** (decisión del asistente, aprobada): igual que `Send`; marcar
  de nuevo tras editar deja `HasChangesSinceSent = false`.
- **Sin registro de fallas** (`IQuotationSendFailureLog`): no hay canal que falle. Un error
  del PDF sale como cualquier error del handler.
- **No toca** la elección de canal (`IWhatsAppChannelChoiceStore`).

## Restricciones

- Expand/contract: `MarkedSent` es valor nuevo de un enum persistido. Revisar cómo se guarda
  `QuotationHistoryEventType` (texto o entero) y que un pod viejo no reviente al leerlo durante el rollout.
- TDD: RED antes que GREEN.

## Pruebas

- Dominio/aplicación: marca desde `Draft` y desde `Sent`; rechaza `Voided`, `Expired`,
  `Converted` (`quotation.quotation.not_draft`); rechaza sin `ValidUntil` / sin cuenta de cobro.
- Integración: `POST …/mark-sent` → 200 con `status = Sent`, entrada de historial `MarkedSent`,
  evento `quotations.quotation.sent.v1` en el outbox, sin WhatsApp enviado; 403 sin `quotation.manage`.

## Frontend

Botón «Marcar como enviada» junto a «Enviar», con confirmación. Spec propio en `qep-frontend`.

## Para quien construye

- Nombre exacto del resumen de historial y su traducción en el frontend.
- Si la entrada `MarkedSent` necesita mapeo explícito en `QuotationHistoryEntry`/DTO.

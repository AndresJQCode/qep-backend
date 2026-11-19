# Orden de compra por correo al proveedor — plan de implementación (backend)

> **Para agentes:** usar `superpowers:executing-plans` (o `subagent-driven-development`) tarea por tarea. Pasos con `- [ ]`.

**Meta:** al enviar una orden de compra, el proveedor (externo o interno) recibe un correo con el pedido y el PDF adjunto; el
resultado queda guardado y se puede reenviar.

**Arquitectura:** puerto `IPurchaseOrderEmail` en Purchases.Application, adaptador en Bootstrapper sobre `IEmailChannel`, plantilla en
Notifications. El correo sale tras el `SaveChanges` del `send`; su fallo no deshace el envío.

**Stack:** .NET 10, EF Core (Postgres), xUnit, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-18-orden-de-compra-por-correo-design.md`

## Restricciones globales

- TDD: RED antes que GREEN, con evidencia literal de ambos. El RED de compilación no levanta contenedores.
- Integración filtrada por nombre (`--filter "FullyQualifiedName~..."`); la clase completa una vez por bloque; nunca dos corridas de
  integración a la vez (revisar `Get-Process testhost, dotnet` antes).
- Mensajes de dominio y códigos como en el spec: `purchases.order.email_not_allowed`, `purchases.order.supplier_email_missing`.
- Migración Expand/contract: columnas nulables, sin tocar las existentes.
- Nunca loguear la dirección ni el contenido del correo; sólo el tipo de la excepción.
- Rama de trabajo propia desde `develop`; merge a `develop`, nunca a `main`. `docs/estado.md` se actualiza en `develop` al mergear.

## Foco de revisión

1. Proveedor sin correo (vacío o sólo espacios): `NoRecipient`, sin llamada al canal y el envío sigue OK.
2. Canal que lanza excepción: orden en `Sent`, `Failed` guardado, 200.
3. Segundo `SaveChanges` que falla tras enviar el correo: la respuesta igual lleva el resultado.
4. Nombre de proveedor o notas con `<`/`&`: HTML del correo escapado.
5. Doble clic en reenviar: dos correos son aceptables, pero ninguno cambia la orden ni su `version`.

---

## Bloque 1 — Dominio, persistencia y lookup

### Tarea 1: correo en `PurchasesSupplierRef` y columnas del resultado

**Archivos:**
- Modificar: `src/Modules/Purchases/Modules.Purchases.Application/Ports.cs` (`PurchasesSupplierRef`), `src/Bootstrapper/PurchasesSupplierLookup.cs`
- Modificar: `src/Modules/Purchases/Modules.Purchases.Domain/PurchaseOrder.cs`; crear `PurchaseOrderEmailStatus.cs` (enum `Sent, Failed, NoRecipient`)
- Modificar: configuración EF de `PurchaseOrder` en Purchases.Infrastructure; crear la migración
- Pruebas: `tests/Modules/Purchases/Modules.Purchases.UnitTests/PurchaseOrderTests.cs`

**Interfaces:**
- Produce: `PurchasesSupplierRef.Email` (`string?`, último parámetro con default `null`); `PurchaseOrder.RecordEmail(PurchaseOrderEmailStatus status, string? to, DateTimeOffset at)`; propiedades `LastEmailStatus`, `LastEmailTo`, `LastEmailAt`. `RecordEmail` no sube `Version`.

- [ ] **Paso 1: RED.** `PurchaseOrderTests`: `RecordEmail_StoresResult_WithoutBumpingVersion` (estado `Sent`, destino y fecha guardados; `Version` igual) y `RecordEmail_OnDraft_Throws`. Correr `dotnet build` del proyecto de pruebas: falla por compilación.
- [ ] **Paso 2: GREEN.** Agregar enum, propiedades y `RecordEmail` (sólo estados distintos de `Draft`); `Email` en `PurchasesSupplierRef`; proyectar `supplier.Contact.Email` en `PurchasesSupplierLookup` (recortado; vacío ⇒ `null`).
- [ ] **Paso 3:** mapear columnas `last_email_status` (texto), `last_email_to` (254), `last_email_at`, todas nulables; generar la migración con `dotnet ef migrations add PurchaseOrderEmailResult` y revisar que sólo agregue columnas.
- [ ] **Paso 4:** correr `--filter "FullyQualifiedName~PurchaseOrderTests"`. Esperado: PASS.
- [ ] **Paso 5: commit** `feat(purchases): store the email result of a purchase order`.

### Tarea 2: DTO, `AllowedActions` y composición

**Archivos:**
- Modificar: `PurchasesDtos.cs`, `PurchaseOrderComposer.cs`, `PurchasesConflicts.cs`
- Pruebas: `PurchaseOrderComposerTests.cs`

**Interfaces:**
- Consume: Tarea 1.
- Produce: `PurchaseOrderEmailDto(string? Status, string? To, DateTimeOffset? At, string? Recipient)`; `PurchaseOrderDetailDto.Email` (al final del record); acción `"resend_email"` en `AllowedActions` para `Sent`, `Confirmed` y `Received` con permiso `manage`; `PurchasesConflicts.EmailNotAllowed(order)` → 409 `purchases.order.email_not_allowed`; `PurchasesConflicts.SupplierEmailMissing()` → 422 `purchases.order.supplier_email_missing`.

- [ ] **Paso 1: RED** en `PurchaseOrderComposerTests`: `Detail_ExposesRecipientAndLastEmail`, `Detail_WithoutSupplierEmail_HasNullRecipient`, `AllowedActions_IncludeResendEmail_OnlyFromSentOn` (no en `Draft` ni `Cancelled`, ni sin `manage`). Build falla.
- [ ] **Paso 2: GREEN** en el composer, usando `suppliers.FindManyAsync` que ya llama.
- [ ] **Paso 3:** correr `--filter "FullyQualifiedName~PurchaseOrderComposerTests"`. PASS.
- [ ] **Paso 4: commit** `feat(purchases): expose the email result and resend action in the order detail`.

**Revisión del bloque 1** (una sola, sobre la rama) antes de seguir.

---

## Bloque 2 — Correo: plantilla, puerto, adaptador y handlers

### Tarea 3: plantilla y adaptador

**Archivos:**
- Crear: `src/Modules/Notifications/Modules.Notifications.Application/PurchaseOrderEmailTemplate.cs`
- Crear: `src/Modules/Purchases/Modules.Purchases.Application/IPurchaseOrderEmail.cs`; `src/Bootstrapper/PurchaseOrderEmailSender.cs`; registro en `QepServiceCollectionExtensions.cs`
- Pruebas: unitarias de la plantilla (proyecto de Notifications) y del adaptador (Bootstrapper o Purchases, donde estén las de `SupportOpeningEmailSender`)

**Interfaces:**
- Produce: `PurchaseOrderEmailTemplate.Render(PurchaseOrderEmailInput input) : EmailMessage` con `PurchaseOrderEmailInput(string ToAddress, PurchaseOrderEmailContent Content, string FileName, ReadOnlyMemory<byte> Pdf)`, donde `PurchaseOrderEmailContent` espeja las líneas ya formateadas de `PurchaseOrderPdfDocument` (Notifications no referencia Purchases). `IPurchaseOrderEmail.SendAsync(PurchaseOrderEmailRequest, CancellationToken) : Task<PurchaseOrderEmailResult>` con `PurchaseOrderEmailRequest(string ToAddress, PurchaseOrderPdfDocument Document, byte[] Pdf, string FileName)` y `PurchaseOrderEmailResult` = `Sent | Failed`.

- [ ] **Paso 1: RED** de plantilla: `Render_SubjectIsOrderNumberAndTenant` («Orden de compra OC-0001 de {tenant}»), `Render_AttachesPdf_AsApplicationPdf`, `Render_EscapesHtml_InSupplierNameAndNotes`, `Render_TextBody_ListsEveryLine`. Build falla.
- [ ] **Paso 2: GREEN** de la plantilla, calcando `SupportOpeningEmailTemplate`.
- [ ] **Paso 3: RED** del adaptador: `SendAsync_ChannelThrows_ReturnsFailed_AndLogsOnlyExceptionType` (el log no contiene la dirección), `SendAsync_Ok_ReturnsSent`.
- [ ] **Paso 4: GREEN** del adaptador, calco de `SupportOpeningEmailSender` (`catch … when not OperationCanceledException`); registrarlo.
- [ ] **Paso 5:** correr las pruebas filtradas por nombre. PASS. **Commit** `feat(notifications): purchase order email template and sender`.

### Tarea 4: armado del documento compartido y envío en `send`

**Archivos:**
- Crear: `Modules.Purchases.Application/PurchaseOrderDocumentBuilder.cs`; modificar `GetPurchaseOrderPdf.cs` para usarlo
- Modificar: `TransitionPurchaseOrder.cs`; `PurchasesAuditActions` (`EmailSent`, `EmailFailed`)
- Pruebas: `LinkedPurchaseOrderHandlersTests.cs` y un `TransitionPurchaseOrderEmailTests.cs` nuevo; fakes en `PurchasesFakes.cs` (`FakePurchaseOrderEmail`)

**Interfaces:**
- Consume: Tareas 1 a 3.
- Produce: `PurchaseOrderDocumentBuilder.BuildAsync(PurchaseOrder order, PurchaseOrderDetailDto detail, PurchasesSupplierRef supplier, CancellationToken) : Task<(PurchaseOrderPdfDocument Document, byte[] Pdf)>`; `TransitionPurchaseOrderHandler` recibe `IPurchaseOrderEmail` y `PurchaseOrderDocumentBuilder`.

- [ ] **Paso 1: RED** (`TransitionPurchaseOrderEmailTests`): `Send_WithSupplierEmail_SendsOneEmailWithPdfAndStoresSent`, `Send_WithoutSupplierEmail_StoresNoRecipient_AndDoesNotCallChannel`, `Send_WhenEmailFails_OrderStaysSent_AndStoresFailed`, `Send_Linked_AlsoSendsEmail`, `Receive_And_Cancel_DoNotSendEmail`. Build falla (el handler aún no recibe el puerto).
- [ ] **Paso 2: GREEN.** Extraer el armado a `PurchaseOrderDocumentBuilder` (y que `GetPurchaseOrderPdfHandler` lo use, con sus pruebas existentes verdes); en el handler, tras `SaveChangesAsync` y sólo para `Send`: intentar el correo, `order.RecordEmail(...)`, auditar (`EmailSent`/`EmailFailed`, nada con `NoRecipient`), segundo `SaveChangesAsync` protegido con `try/catch` que no relanza; devolver `composer.DetailAsync(order)`.
- [ ] **Paso 3:** `--filter "FullyQualifiedName~TransitionPurchaseOrderEmailTests"` y `~PurchaseOrderPdf`. PASS.
- [ ] **Paso 4: commit** `feat(purchases): email the purchase order to the supplier on send`.

### Tarea 5: «Reenviar correo»

**Archivos:**
- Crear: `Modules.Purchases.Application/ResendPurchaseOrderEmail.cs`; modificar `PurchaseOrderEndpoints.cs`
- Pruebas: `ResendPurchaseOrderEmailTests.cs` (unitarias); `PurchaseOrderTransitionApiTests.cs` (integración, con un `IPurchaseOrderEmail` falso en `SharedPurchasesHost`)

**Interfaces:**
- Produce: `ResendPurchaseOrderEmailCommand(Guid TenantId, Guid OrderId) : ICommand<PurchaseOrderDetailDto>`; ruta `POST /{orderId:guid}/resend-email` con `RequireAuthorization(OrderManage)`.

- [ ] **Paso 1: RED** unitarias: `Resend_FromSent_SendsAndStoresResult`, `Resend_FromDraft_Throws409EmailNotAllowed`, `Resend_FromCancelled_Throws409`, `Resend_WithoutSupplierEmail_Throws422`, `Resend_ChannelFails_ReturnsFailedStatus`, `Resend_DoesNotBumpVersion`. Build falla.
- [ ] **Paso 2: GREEN** del handler (reusa builder y puerto, audita) y de la ruta (sin cuerpo ni `If-Match`).
- [ ] **Paso 3: RED+GREEN de integración**, una a la vez y filtradas: `Send_ReturnsEmailSent`, `ResendEmail_Returns200`, `ResendEmail_Draft_Returns409`, `ResendEmail_NoSupplierEmail_Returns422`, `ResendEmail_WithoutManage_Returns403`.
- [ ] **Paso 4:** correr la clase `PurchaseOrderTransitionApiTests` una vez, más `Modules.Purchases.UnitTests` completo y `ArchitectureTests`. PASS.
- [ ] **Paso 5: commit** `feat(purchases): resend the purchase order email`.

**Revisión del bloque 2** (una sola) y **revisión fuerte final** de toda la rama.

---

## Cierre

- Suite completa sólo de `Modules.Purchases.*`, `Modules.Notifications.*` y `ArchitectureTests` (Bootstrapper y Notifications se tocan, pero no es transversal), con `-- xUnit.MaxParallelThreads=4`.
- Actualizar `docs/estado.md` en `develop` al mergear. El frontend va después y su plan lo agradece: backend primero.

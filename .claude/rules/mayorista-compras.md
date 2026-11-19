---
paths:
  - "src/Modules/Wholesale/**"
  - "tests/Modules/Wholesale/**"
  - "src/Modules/Purchases/**"
  - "tests/Modules/Purchases/**"
  - "src/Modules/Suppliers/**"
  - "tests/Modules/Suppliers/**"
---

# Mayorista, Compras y Proveedores

Reglas de `qep-backend` que no se deducen leyendo el código. Se cargan solas al leer o editar un
archivo que coincide con `paths` (arriba). Las reglas generales están en [`CLAUDE.md`](../../CLAUDE.md).

- **La aceptación de una invitación de mayorista no es atómica entre módulos** (plan mayorista fase 2, D-3). Es idempotente por
  el tenant reservado (`wholesale.distributor_invitations.reserved_tenant_id`), el proveedor (`LinkedTenantId` o documento) y el
  cliente (documento); la invitación se consume en el último commit, el de `wholesale`. No la cambies a «consumir primero»: un
  fallo a mitad dejaría la invitación gastada y la relación a medias, sin link para reintentar. Si otra pestaña crea el tenant
  reservado mientras ésta valida el slug, el `slug.taken` es el propio y la aceptación termina sobre él
  (`DistributorInvitationService`, Review Focus 1).
- **`Supplier.LinkedTenantId` lo escribe sólo `ISupplierLinking`** (D-8). El formulario de proveedores no lo acepta (un
  `linkedTenantId` en el cuerpo se ignora) y un proveedor vinculado no cambia de documento: 422
  `suppliers.supplier.linked_identity_locked`.
- **El link de la invitación de mayorista sale de `Notifications:AppBaseUrl`** (`AccountLinks.WholesaleInvitation`, ruling W1),
  no de una variable propia: no hay `WholesaleInvitationUrl` que dar de alta en el pipeline. El token viaja sólo en el payload
  del outbox; `SensitivePaths.Mask` lo tapa en `platform.request_failures` y en su línea de log, también el de la invitación de
  miembros, y `SensitivePathTelemetry` (registrado en `Program.cs` después de `AddQepLogging`) con la misma regla en el
  `url.path` de los spans de OpenTelemetry y en el scope `RequestPath` de cada log del request. Una ruta nueva con un secreto en
  un segmento se suma ahí.
  miembros. Una ruta nueva con un secreto en un segmento se suma ahí.
- **El aviso de WhatsApp de la invitación es un evento aparte y sin token** (`wholesale.distributor-invitation-notice.v1`); su
  consumidor vive en Mayorista con inbox propio (`InvitationNoticeProcessor`, consumidor `wholesale.invitation-whatsapp-notice`)
  y manda por `WholesaleInvitationNoticeSender` de Bootstrapper. El resultado se aplica sólo a la `generation` vigente y no sube
  `version` (D-3, D-4); la `generation` es token de concurrencia, así que un resultado leído antes de un `PUT` o un «Reenviar» no
  se escribe.
- **El `clientId` del aviso es el id del mensaje del outbox**: no lo cambies por uno nuevo, o un reintento manda dos avisos
  (D-5). Por lo mismo, una fila `Failed` con `RetryDisabled` es terminal en `PlainTemplateOutbound` (`not_retriable`): repetir el
  `clientId` no la reenvía.
- **«Por aprobar» de un pedido `Wholesale` es `WholesaleApprovedAt`, no un estado** (mayorista fase 3, D-1). `OrderStatus` sigue
  `Open/Archived/Cancelled`; pendiente es `Open` sin `wholesale_approved_at`. Aprobado deja de ser editable (`IsEditable`, D-3):
  la aprobación ya le llevó las líneas al distribuidor y una edición posterior nunca le llegaría. «Lo anuló el comprador» tampoco
  se guarda: se deriva de `cancelled_by` nulo (`Order.CancelledByBuyer`).
- **La orden vinculada guarda costo sin IVA y congela la lista** (D-9). `UnitCost` es el precio de lista del mayorista sin IVA
  (`PurchaseOrderMath.NetOfVat`); `list_base_price`, `list_discount_percent` y `list_unit_price` (con IVA) quedan como foto para
  pintar «base y descuento». Los totales de la orden y del pedido pueden diferir en centavos. La moneda del pedido tiene que ser
  la de la orden: si el mayorista cambió la suya, el envío es 409 `wholesaler_rejected` y una aprobación en otra moneda se
  registra y no se aplica.
- **Los eventos de vuelta del mayorista los consume Purchases con inbox propio, y la creación de productos va en su
  transacción** (`WholesaleOrderEventsProcessor`, consumidor `purchases.wholesale-order-events`). `PurchasesCatalogWriter` enrola
  el `CatalogDbContext` en la conexión y la transacción de `PurchasesDbContext` (calca `ShopifyCatalogWriter`) y abre un punto de
  guardado por producto: en Postgres una sentencia fallida aborta la transacción entera, así que sin el punto de guardado un
  alta fallida dejaría la confirmación sin guardar y el evento reintentando para siempre (ruling X2). Un evento que encuentra la
  orden todavía en borrador (`RetryLater`) no se marca: vuelve cuando vence su reclamo.
- **Un producto creado por Compras no tiene precio de venta (`AllowsNoPrice`) ni origen externo** (D-16, D-17). Nace sin
  `ExternalSource` para que el distribuidor lo edite como cualquier otro, sin precio, y con costo sólo si es mayor que 0. No sale
  en tienda, POS ni cotizaciones hasta que alguien le ponga precio; guardar el producto desde el formulario exige precio.

---
paths:
  - "src/Modules/Buyers/**"
  - "tests/Modules/Buyers/**"
  - "src/Modules/Store/**"
  - "tests/Modules/Store/**"
---

# Compradores y tienda pública

Reglas de `qep-backend` que no se deducen leyendo el código. Se cargan solas al leer o editar un
archivo que coincide con `paths` (arriba). Las reglas generales están en [`CLAUDE.md`](../../CLAUDE.md).

- **Las reglas del perfil y de la contraseña del comprador duplican las de Store e Identity, y las amarra `BuyerParityTests`** (`tests/Bootstrapper/Bootstrapper.UnitTests`).
  Buyers no ve a Store ni a Identity, así que `BuyerPasswordRules` y `BuyerProfileValidator` son copias de `PublicOrderRequestBodyValidator`, `StoreRequestBilling`/`StoreRequestShipping`
  y de los límites de contraseña de Identity. Cambiar una sin la otra pone la prueba en rojo; el error es de paridad, no del código que acabas de tocar.
  La regla del tipo de documento ya no es una copia: Store y Buyers leen la misma `DocumentTypesByCountry` (`BuildingBlocks.Domain`), y los casos de paridad cubren la regla por país.
- **El payload de `store.buyer-signup-requested.v1` es un contrato entre Store y Buyers, leído por nombre.** Store lo escribe como `StoreBuyerSignupRequestedPayload` y Buyers
  lo lee como `BuyerSignupRequestedMessage` (record propio, camelCase), sin referencia entre los módulos; lo amarra `BuyerParityTests.TheSignupEventContractMatches`. Renombrar un
  campo en un lado compila y deja las altas procesadas sin efecto. El payload **nunca** lleva la contraseña en claro, sólo `passwordHash`; por eso el housekeeping de Buyers
  borra a las 24 h esas filas del outbox de plataforma.
- **`buyers.consents` es prueba legal: sin clave foránea a propósito, nunca se borra, y el `Down` de `InitialBuyers` no se corre** (borraría los consentimientos de todos los
  compradores). Ni borrar la cuenta ni `BuyerHousekeepingWorker` lo tocan; cualquier purga nueva de Buyers tiene que dejarlo fuera.
- **Todo `/api/v1/public/stores/{host}/buyer/*` responde `Cache-Control: no-store`, y lo pone `BuyerNoStoreMiddleware` antes de `UseRateLimiter`.** Un endpoint nuevo del
  comprador no necesita hacer nada, pero mover el middleware después del limitador deja los `429` cacheables. Las rutas de Buyers leen el cuerpo a mano con tope de 64 KB
  (`ReadBodyAsync`): no uses el binder de minimal API ahí.
- **El header de sesión del comprador está declarado dos veces.** `PublicBuyerEndpoints.BuyerSessionHeader` (Buyers) y `StorefrontClient.BuyerSessionHeader` (Store, que no
  ve a Buyers); `BuyerParityTests.TheBuyerSessionHeaderMatches` los ata. El BFF sólo borra la cookie con el código `store.buyer.session_invalid`: no devuelvas un 401 con
  sesión válida (por eso la contraseña actual mala es `422 store.buyer.password_incorrect`).
- **La anulación del comprador es más estricta que `Order.Cancel`, y su 409 también cubre la carrera.** Sólo anula con `Status == Open && PaymentStatus == PaymentPending && PaymentProofs.Count == 0 && !IsPrepared && !IsInvoiced` (D5 y spec 2026-11-10, `Order.CanBeCancelledByBuyer`: un pedido con cualquier comprobante, preparado o facturado ya no se anula desde la tienda); fuera de eso responde `409 order.order.not_cancellable_by_buyer`. La `RequestConcurrencyException` se traduce en `BuyerOrderGateway` al mismo 409 tras un reintento: la tienda nunca ve un 412, no tiene versión que mandar. El dominio no puede lanzar `ResourceConflictException` (Domain sólo ve `BuildingBlocks.Domain`): lanza con el código de `BuyerOrderErrors` y Application lo traduce. `BuyerStorefront` lleva `TenantId`: una prueba que lo construya a mano necesita los tres argumentos.
- **`Order.LinkBuyerAccount` no sube `Version`, pero `Version` es token de concurrencia.** El enlace del alta diferida es invisible para el `If-Match` de la consola, y aun así su `UPDATE` lleva `WHERE version = …`: si otra escritura ganó, `BuyerOrderGateway.LinkAsync` relee y reintenta una vez (sólo si sigue vacío). El checkout resuelve la sesión del comprador siempre que llega el header, no sólo con `updateProfile`; `ScalarTextAsync` de las pruebas de Store devuelve `""` para un `NULL`.
- **El enlace del pedido de la tienda con la cuenta del comprador lo deciden el correo de facturación y la marca `buyer_account_set_by_member`** (spec 2026-11-15). El checkout y el inicio de sesión enlazan por correo verificado, sólo en pedidos de origen `Store`. Cuando un asesor cambia o quita la cuenta (`PUT`/`DELETE …/orders/{id}/buyer-account`, `Order.SetBuyerAccountByMember`) la marca queda en `true`, también al quitar sobre un pedido sin cuenta, y con la marca en `true` ningún camino automático (`LinkBuyerAccount`, `LinkStoreOrdersByBillingEmailAsync`) toca la cuenta. Un camino automático nuevo respeta la marca, o deshace la decisión del asesor en el siguiente inicio de sesión del comprador. El correo del detalle lo resuelve `IQuotationBuyerAccountLookup` (adaptador en Bootstrapper): una cuenta borrada deja el id en el pedido y el detalle responde `buyerAccount: null`. La auditoría (`quotation.order.buyer_account_changed`) lleva sólo los dos ids en `changedFields`, nunca correos; el historial del pedido (`BuyerAccountChanged`, sólo la decisión del asesor) sí nombra los correos, a propósito (owner, 2026-11-15). No «unifiques» las dos reglas.
- **La nota del comprador es `Order.BuyerNotes`, no `Order.Notes`** (spec 2026-11-10, D9; Shopify desde el spec 2026-11-14). `Notes` es del asesor: la sobrescriben `UpdateNotes`, `AddPaymentProofs` y `AttachExternalShipping`, y en un pedido de Shopify lleva «Shopify #N» y la dirección cruda, nunca la nota del comprador, y no viaja a Shopify. `BuyerNotes` la escriben `CreateFromStore`, la importación de Shopify (`CreateExternal`) y la sincronización de Shopify, y el asesor **sólo** en pedidos de Shopify (`UpdateExternalBuyerNotes`, que responde `order.order.source_invalid` en cualquier otro origen; `EditExternalShippingParty` no la toca). En un pedido de la tienda pública sigue intocable: un método nuevo que la edite ahí le muestra al comprador un texto que no escribió. Los pedidos anteriores a `AddOrderBuyerNotes` la tienen en `NULL` a propósito (sin backfill), y los de Shopify importados antes del spec 2026-11-14 también: su nota sigue dentro de `Notes`.

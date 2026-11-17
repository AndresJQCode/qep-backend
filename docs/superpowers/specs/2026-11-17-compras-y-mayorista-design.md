# Compras y Mayorista — diseño

Fecha: 2026-11-17. Alcance: `qep-backend`. Hay un spec gemelo en `qep-frontend` con el mismo nombre.
Mockup: https://claude.ai/artifact/3Uad4hHNoajB6JzN2tdbQW (versión 9).

## Por qué

Un tenant mayorista quiere venderle a sus distribuidores por QEP: darlos de alta, asignarles una lista
de precios y recibir sus pedidos en la bandeja de Pedidos de siempre. Un distribuidor quiere hacer órdenes
de compra, a ese mayorista o a cualquier proveedor que no esté en QEP. Hoy QEP no tiene órdenes de compra
y no tiene relación entre tenants.

## Objetivos

1. **Módulo `purchases` (Compras)**, del lado del comprador: órdenes de compra contra un proveedor del
   directorio (`Supplier`). Sirve con un proveedor externo y con un mayorista vinculado en QEP.
2. **Módulo `wholesale` (Mayorista)**, del lado del vendedor: listas de precios, distribuidores e
   invitaciones.
3. **Invitación que crea un tenant o vincula uno existente.** El invitado nace con los módulos que
   necesita para comprar.
4. **El pedido del distribuidor llega al mayorista como un `Order` normal**, con `Source = Wholesale`,
   aprobado o editado con el flujo que ya existe.
5. **Al confirmarse una orden, los productos que el distribuidor no tiene se crean en su catálogo** con
   el costo de la compra y sin precio de venta.

## No objetivos

- Pago, factura, cartera o crédito en QEP (decisión del owner: «Nada en QEP»). La orden es un compromiso
  comercial; facturar y cobrar pasa por fuera.
- Stock, reservas o disponibilidad (decisión del owner: «de momento no va stock»).
- Recepción parcial, devoluciones o notas de crédito.
- Precio fijo por variante dentro de una lista (sólo porcentaje en este corte).
- Escalas por cantidad para distribuidores: las `PriceScale` del catálogo no aplican.
- Eventos DIAN (030/032/033), EDI, Peppol.
- Que un distribuidor compre a varios mayoristas en una misma orden.

## Anti-objetivos

- **Que el mayorista modifique el catálogo del distribuidor.** Nunca cambia precio, costo, nombre ni
  ningún otro dato de un producto existente (decisión del owner).
- **Que se actualice el costo de un producto que ya existe.** Sólo se crean los que faltan.
- Que un tenant lea datos de otro fuera de los puertos de este diseño.
- Que un distribuidor bloqueado o con la relación terminada pueda enviar una orden.
- Que el precio de una orden enviada cambie porque el mayorista editó su lista o su precio base. Sólo
  cambia si el mayorista edita ese pedido, y el distribuidor lo ve marcado.

## Enfoques

- **A. Tabla propia de pedidos del distribuidor en el lado del mayorista (descartado).** Una bandeja nueva
  «Pedidos de distribuidores». Duplica estados, aprobación, edición, PDF y reportes que `Order` ya tiene.
  El owner lo cuestionó: «¿pedidos no reflejaría el pedido realizado? ¿configurar nuevo source?».
- **A′. `Order` con `Source = Wholesale` en el mayorista y la orden de compra como vista del mismo
  documento (descartado).** Un solo documento compartido por dos tenants. Se cayó cuando el owner pidió
  órdenes a proveedores externos: ese caso no tiene `Order` del otro lado.
- **B. Dos documentos enlazados, uno por lado (elegido).** El comprador tiene su `PurchaseOrder`; el
  vendedor, su `Order` con `Source = Wholesale`. Se enlazan por id y se sincronizan sólo en enviar,
  confirmar y cancelar. Es el modelo de Odoo y Dynamics 365 (intercompany), y el de Peppol (Order +
  OrderResponse). Un proveedor externo es el mismo `PurchaseOrder` sin el lado vinculado.
- **C. Catálogo compartido: el distribuidor vende directo del catálogo del mayorista (descartado).** Rompe
  el anti-objetivo principal: el catálogo del distribuidor es suyo.

## Diseño

### Módulos y entitlements

| Clave | Depende de | En `DefaultForNewTenants` |
| --- | --- | --- |
| `purchases` | `suppliers`, `catalog` | No |
| `wholesale` | `orders` | No |

Las dos claves van al final de `TenantModuleKeys.All`, con su migración de Tenancy que recrea
`CK_tenant_modules_module_key`. Se prenden por tenant.

**Módulos del tenant creado por invitación:** `catalog`, `customers`, `companies`, `quotations`,
`orders`, `reporting`, `suppliers`, `purchases`. **Sin `shopify`** (decisión del owner). Es una lista
propia (`InvitedDistributorModules`), no `DefaultForNewTenants`.

### Proveedor vinculado

`Supplier` gana `LinkedTenantId: Guid?`. Lo escribe sólo la aceptación de una invitación; el formulario
de proveedor no lo expone ni lo acepta. Un proveedor vinculado se muestra como «Mayorista en QEP» y no se
puede borrar mientras tenga órdenes abiertas.

### Módulo `purchases`

**`PurchaseOrder`** (tenant del comprador):

- `Number` (secuencia por tenant, `OC-0001`), `SupplierId`, `Currency`, `Notes`, `CreatedBy`,
  `Version`.
- `Status`: `Draft → Sent → Confirmed → Received`, más `Cancelled`. `Confirmed` lleva
  `ConfirmedWithChanges: bool`.
- `LinkedOrderId: Guid?`: el `Order` del mayorista, sólo con proveedor vinculado.
- Líneas: `VariantId?`, `Code`, `Name`, `RequestedQuantity?` (nulo en una línea que agregó el
  mayorista), `ConfirmedQuantity?`, `UnitCost`, `ConfirmedUnitCost?`, `TaxRate`, `LineStatus`
  (`Unchanged`, `Changed`, `Rejected`, `Added`), `CreatedInCatalog: bool`. `Changed` cubre cantidad,
  precio o las dos; la respuesta dice cuál cambió comparando pedido y confirmado.
- Historial: cada transición con fecha y autor (miembro o «el mayorista»).

**Proveedor externo:**

- Las líneas son **sólo variantes del catálogo del comprador** (decisión del owner, 2026-11-17). No hay
  líneas de texto libre (flete, servicios). El costo se teclea; la moneda la elige el comprador.
- «Enviar» congela la orden y la deja lista para PDF o WhatsApp. Confirmar y recibir se marcan a mano.
  Confirmar a mano permite ajustar cantidades confirmadas.

**Proveedor vinculado:**

- Las líneas salen del catálogo del mayorista, filtrado por la visibilidad de la lista del distribuidor,
  con el precio calculado: `precio base × (1 − %)`. La moneda es la del mayorista.
- «Enviar» llama al puerto `IWholesaleOrderIntake` (declarado en `purchases.Application`, implementado
  por un adaptador en Bootstrapper contra `wholesale`). El puerto valida la relación, **recalcula el
  precio en el servidor** con la lista vigente y crea el `Order` del mayorista. El precio queda congelado
  en las dos puntas.
- Mientras el mayorista no apruebe, el comprador puede cancelar. Después, no.
- Aprobar, aprobar con cambios y anular viajan de vuelta por outbox:
  `quotations.order.wholesale-approved.v1` y `quotations.order.wholesale-cancelled.v1`. `purchases` los
  consume con `ClaimedOutboxConsumer` e inbox propio y pasa la orden a `Confirmed` o `Cancelled`.
- «Recibida» es siempre del comprador, a mano.

**Productos nuevos en el catálogo del comprador:**

- Ocurre al pasar a `Confirmed`, con proveedor externo o vinculado.
- Por cada línea confirmada con cantidad mayor que cero, se busca la variante por **`Code`** en el
  catálogo del comprador (`IX_product_variants_tenant_code`).
- Existe: no se toca nada. Ni costo, ni nombre, ni precio.
- No existe: se crea el producto con una variante, ese `Code`, el nombre de la línea, **costo = precio
  confirmado** en la moneda de la orden y **sin precio de venta**. La línea queda con
  `CreatedInCatalog = true`.
- Se usa `IExternalProductWriter`, que hoy no recibe costo: se amplía con un costo opcional. El permiso
  `catalog.product_cost.manage` no se exige aquí porque la escritura la hace el sistema, no el miembro.
- Con proveedor externo nunca hay que crear: las líneas ya son del catálogo propio.

### Módulo `wholesale`

**`PriceList`** (tenant del mayorista): `Name`, `DiscountPercent` (0 a 100, dos decimales),
`Visibility` (`AllActive` o `Categories` con lista de ids), `Version`. Cambiar la lista afecta sólo las
órdenes enviadas después.

**`Distributor`** (relación): `DistributorTenantId?`, `CustomerId` (cliente creado en el tenant del
mayorista; `Order.ClientId` es obligatorio), `PriceListId`, `AdvisorId` (asesor que recibe sus pedidos),
`Status` (`Invited`, `Active`, `Blocked`), `Version`. Un distribuidor `Blocked` no ve el catálogo ni
envía órdenes.

**`DistributorInvitation`:** `Email`, razón social, NIT, contacto, token con hash SHA-256, vigencia de 72
horas (igual que las invitaciones de miembros), reenviable. Evento
`wholesale.distributor-invited.v1`, entregado por un worker como `InvitationDeliveryWorker`.

**Aceptar la invitación:**

- **El que acepta no tiene tenant:** crea uno nuevo con `InvitedDistributorModules`, aunque
  `Registration:PublicTenantSignupEnabled` esté apagado. El token es la autorización.
- **El que acepta ya administra un tenant:** elige cuál vincula; se le prenden `suppliers` y `purchases`
  si no los tiene.
- En los dos casos: se crea el `Supplier` vinculado en el tenant del distribuidor, se crea el `Customer`
  en el tenant del mayorista y la relación pasa a `Active`.

**El pedido en el mayorista:** `Order` con `Source = Wholesale` (valor nuevo de `OrderSource`),
`BuyerTenantId` y `PurchaseOrderId`. Entra «Por aprobar». Aprobar tal cual es `/approve`. Aprobar con
cambios es el «Editar pedido como borrador» que ya existe, más `/approve`, **sin restricciones nuevas**
(decisión del owner, 2026-11-17: el distribuidor puede llamar a pedir más cosas o cambiar). El mayorista
agrega, quita y cambia líneas, cantidades y precios. Una línea quitada vuelve como `Rejected`; una
agregada, como `Added`. Las líneas agregadas se precian por defecto con la lista del distribuidor y el
mayorista puede cambiarlo, como en cualquier pedido. Anular es `/cancel`. Reportes ganan `Wholesale` en `OrderSourceFilter`.

### Permisos

| Permiso | Módulo | Roles por defecto |
| --- | --- | --- |
| `purchases.order.read` | `purchases` | owner, admin |
| `purchases.order.manage` | `purchases` | owner, admin |
| `wholesale.price_list.manage` | `wholesale` | owner, admin |
| `wholesale.distributor.read` | `wholesale` | owner, admin, asesor |
| `wholesale.distributor.manage` | `wholesale` | owner, admin |

Cada uno pasa por los cuatro pasos de `QepServiceCollectionExtensions.cs` y sube el conteo de la prueba de
descubrimiento de permisos. Los roles exactos son decisión pendiente del owner (P-3).

### Lectura entre tenants

Sólo dos puertos cruzan tenants: el catálogo del mayorista con precio de lista (lectura) y
`IWholesaleOrderIntake` (escritura). Los dos reciben el tenant del comprador y verifican en el servidor
que existe una relación `Active`. Se cubren con pruebas de integración que intentan leer sin relación,
con relación `Blocked` y con un producto fuera de la visibilidad de la lista.

### Errores

Códigos nuevos, mapeados en `ApiExceptionHandler.cs`:

- `purchases.order.not_draft`, `purchases.order.not_cancellable` (409)
- `purchases.order.empty`, `purchases.order.line_not_in_catalog` (422)
- `wholesale.relationship.inactive` (403)
- `wholesale.price_list.in_use` (409)
- `wholesale.invitation.expired`, `wholesale.invitation.already_accepted` (410 y 409)
- `wholesale.product.not_visible` (422)

## Pruebas

- Dominio: transiciones de `PurchaseOrder`, cálculo del precio de lista, reglas de cancelación.
- Integración: enviar a un mayorista crea el `Order` con el precio recalculado; aprobar con cambios
  vuelve como `Confirmed` con cambios; los productos faltantes se crean con costo y sin precio, y los
  existentes quedan iguales byte a byte; las lecturas entre tenants sin relación fallan.
- `ArchitectureTests`: `PurchasesLayerTests.cs` y `WholesaleLayerTests.cs`.

## Decisiones pendientes del owner

- **P-1.** ¿El invitado que ya tiene tenant puede vincular más de uno, o uno por invitación?
- **P-2.** ¿El mayorista ve el número de la orden del distribuidor y su nombre comercial, o sólo su
  razón social?
- **P-3.** Roles por defecto de los permisos nuevos (tabla de arriba es propuesta).
- **P-4.** ¿Se le avisa al mayorista de un pedido nuevo por correo, o sólo en la bandeja?
- **P-5.** ¿Un `Code` del mayorista que choca con un `Barcode` del distribuidor se trata como el mismo
  producto? Propuesta: no; sólo `Code`.
- **P-6 (resuelta 2026-11-17).** Al aprobar con cambios el mayorista puede editar el pedido entero:
  agregar, quitar y cambiar líneas, cantidades y precios.

## Respuestas al builder check (propuestas mías, salvo donde se indica)

- **Contrato HTTP:** lo fija el plan del backend, en una sección «Contrato HTTP» que el plan del frontend
  cita, como en préstamos. El PDF de la orden lo genera el backend. Un `Draft` se edita y se borra; desde
  `Sent` no.
- **Aprobar con cambios (P-6, resuelta por el owner):** el mayorista edita el pedido entero. Cada línea
  del `Order` guarda el id de la línea de la orden de compra que la originó (nulo si la agregó el
  mayorista). El evento de aprobación lleva **todas** las líneas finales del pedido con ese id, y
  `purchases` reconcilia: con id, actualiza la línea; sin id, la agrega como `Added`; una línea de la
  orden que no vuelve queda `Rejected`. Los productos faltantes se crean igual para las líneas agregadas.
- **Precio base e impuestos:** el precio base es el precio por defecto de la variante en la moneda del
  mayorista; sin precio en esa moneda, la variante no se ofrece. Precios con IVA incluido, como en
  cotizaciones. La empresa facturadora es la por defecto del mayorista. El producto creado en el comprador
  lleva costo y no lleva tasa de impuesto: la elige el distribuidor al ponerle precio.
- **Producto creado por el sistema:** sin categoría, tipo por defecto, una variante, activo. Match por
  `Code` exacto después de recortar espacios, contra variantes de cualquier estado. Idempotente: la línea
  marca `CreatedInCatalog` en la misma transacción, y un evento repetido no crea dos veces. No depende de
  permisos del miembro.
- **Aceptar la invitación:** el correo del login debe coincidir con el de la invitación. Token de un solo
  uso. Quien crea el tenant queda como owner. Para vincular un tenant existente hay que ser su owner.
- **Bloquear y desbloquear:** el mayorista lo hace desde Distribuidores. Bloquear no toca órdenes ya
  enviadas; sólo impide enviar nuevas. No hay «terminar» en este corte.
- **El mayorista anula un pedido ya aprobado:** la orden del comprador pasa a `Cancelled` si todavía no
  está `Received`; los productos ya creados se quedan. Una orden cancelada no se reenvía: se duplica.
- **`Distributor.CustomerId`:** nulo mientras `Invited`.
- **Visibilidad:** el asesor del mayorista ve el pedido en Pedidos con los permisos de siempre, y lee
  Distribuidores con `wholesale.distributor.read`. Compras del distribuidor no cambia.

## Decisiones de diseño tomadas por mí (no del owner)

- Un porcentaje por lista y visibilidad por categorías.
- La moneda de una orden vinculada es la del mayorista.
- Las escalas por cantidad no aplican a distribuidores.
- Confirmar a mano una orden externa permite ajustar cantidades.
- Los eventos de vuelta sólo en aprobar y anular; editar el borrador del pedido no se sincroniza hasta
  aprobar.

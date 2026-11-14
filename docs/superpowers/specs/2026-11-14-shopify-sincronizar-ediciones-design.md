# Shopify: sincronizar ediciones del pedido en los dos sentidos

Fecha: 2026-11-14. Estado: **diseño, pendiente de aprobación del owner**. Extiende
[2026-10-15-integracion-shopify-pedidos-design.md](2026-10-15-integracion-shopify-pedidos-design.md), que dejó fuera
(§6.3 y §14) las ediciones de líneas, envío, dirección y cliente después de importar.

## Por qué

Hoy un pedido de Shopify entra a QEP una sola vez. Después, `orders/updated` sólo reemplaza el espejo
(`shopify.orders.payload`) y el pedido de QEP reacciona al pago y a la cancelación (§6.3); cualquier otra edición en
Shopify se pierde sin aviso. Al revés, lo que el asesor corrige en QEP (la ciudad, que en Shopify es texto libre y en QEP
es una lista) no llega a Shopify, y la tienda queda con el dato malo.

El owner quiere (conversación del 2026-11-14):

- **Lo que se edita en Shopify se ve en QEP**, cualquier dato del pedido.
- **Lo que el asesor corrige a mano en QEP no se pisa**, y además **viaja a Shopify**: dirección, ciudad, nota y atributos.
- Un pedido **preparado o facturado no se actualiza**: el cambio queda en la bandeja para que el asesor lo revise a mano.

## Objetivos, no objetivos y anti-objetivos

**Objetivos**

- A. Recibir las ediciones de un pedido ya importado como **cambio**, que se aplica solo o espera aceptación según la
  configuración de la tienda.
- B. Enviar a Shopify las correcciones del asesor sobre dirección de envío, ciudad, nota y atributos.

**No objetivos**

- Reembolsos y devoluciones (siguen fuera, §14 del spec base).
- Enviar a Shopify cambios de líneas, cantidades, precios o flete: en Shopify eso es una edición de pedido que puede
  cobrar o devolver dinero al comprador.
- Cambiar la cancelación: sigue como está (§6.3; un preparado se anula y avisa del paquete, un facturado queda
  `invoiced_before_cancel`).
- Historial de pedidos anteriores al enlace (lo cubre el spec de días de historial).

**Anti-objetivos** (aunque funcione, sería un fracaso)

- Que una edición de Shopify pise algo que el asesor corrigió a mano.
- Que un pedido preparado o facturado cambie solo.
- Que el eco de una corrección enviada por QEP (`orders/updated` de vuelta) aparezca como un cambio por revisar.
- Que una caída de Shopify impida guardar en QEP.
- Que un pedido quede mitad con la versión vieja y mitad con la nueva de Shopify.

## Restricciones

- **Edición del pedido:** se mantiene `Order.IsEditable` (abierto, no preparado, no facturado). La sincronización no la
  abre.
- **Permisos:** escribir exige `write_orders`. El owner indica (2026-11-14) que los comercios existentes ya lo tienen;
  hay que sumarlo a `Shopify:App:Scopes` (hoy `read_orders,read_customers`) para que una instalación nueva lo pida. En la
  prueba manual se verifica con `currentAppInstallation.accessScopes`.
- **Arquitectura:** Shopify no ve módulos de negocio (`ShopifyLayerTests`); todo lo que toque el pedido entra por
  `IShopifyOrderWriter` → adaptador en `src/Bootstrapper/` → `IExternalOrderWriter` (Quotations).
- **Todo cambio de una fila del espejo pasa por `ShopifyOrderIngestion.ApplyAsync`** (`CLAUDE.md`). La detección de
  ediciones se cuelga de ahí, no de un camino aparte.
- **Contrato con el frontend:** BFF; los enums viajan por nombre; un antes/después se arma en el backend.

## Enfoques considerados

**A. Detectar qué cambió en Shopify**

1. **Comparar Shopify contra Shopify (elegido).** Se guarda la última versión de Shopify que QEP aplicó al pedido y, al
   llegar otra, se comparan las dos. Sólo cuenta como cambio lo que cambió en Shopify; lo que el asesor tocó en QEP y
   Shopify no, queda solo. Cuesta guardar una copia más por pedido.
2. **Comparar Shopify contra el pedido de QEP.** Descartado: QEP recalcula precios, IVA y flete, y todo eso aparecería
   como diferencia; además obliga a marcar cada dato que el asesor tocó.

**B. Enviar correcciones a Shopify**

1. **En segundo plano con reintentos (elegido).** Guardar en QEP deja un envío pendiente; un worker lo manda y el pedido
   muestra enviado, pendiente o fallido. Una caída de Shopify no bloquea al asesor.
2. **En el mismo request.** Descartado: cualquier falla de Shopify bloquea la edición en QEP.

## Diseño

### 1. Qué se lee de Shopify (parsers)

- **Cantidades vigentes.** Hoy los parsers leen `line_items[].quantity` (la original). Pasan a leer la vigente
  (`current_quantity` en REST, `currentQuantity` en GraphQL); una línea con vigente 0 es una línea quitada. Aplica también
  a la importación inicial: un pedido editado antes de importarse entra con lo vigente.
- **`billing_address`.** Se agrega al snapshot (REST y GraphQL) y `billing_address.company` pasa a ser una fuente más del
  documento (`ShopifyDocumentSources`, junto a `shipping_address.company`), con su opción en el select de la tienda. La
  parte de facturación del pedido sigue siendo la del cliente (D-S7/D-S15 no cambian).
- **`note_attributes` / `customAttributes`** quedan en el snapshot como lista de pares (ya se leen para el documento).
- Ver también la verificación V-11 (§9).

### 2. La versión aplicada

`shopify.orders` gana `applied_payload` (jsonb, nullable): el snapshot normalizado con el que QEP creó o actualizó el
pedido por última vez. Lo escriben la importación (al crear el pedido) y cada cambio aplicado. Una fila `Imported` de
antes de este cambio no lo tiene: la primera edición que llegue toma como base el `payload` guardado en ese momento
(última versión conocida), sin generar cambio por las diferencias viejas.

Junto con la versión aplicada se guarda la **proyección** (`applied_projection`, jsonb): los valores que QEP escribió en
el pedido a partir de esa versión (líneas con variante, cantidad, precio y descuento; flete y transportadora; parte de
envío con ciudad resuelta; nota del comprador; documento). Sirve para saber si el asesor cambió un dato: si el valor
actual del pedido difiere de la proyección, lo cambió QEP.

### 3. Un cambio

Al llegar un snapshot más nuevo sobre una fila `Imported` (después del espejo y antes de la reacción §6.3 de pago y
cancelación, que no cambia), se calcula el **diff por grupo** entre `applied_payload` y el snapshot nuevo:

| Grupo | Qué compara |
| --- | --- |
| Líneas | por línea de Shopify: variante emparejada (o línea personalizada), cantidad vigente, precio, descuento |
| Envío | líneas de envío: valor, transportadora, recogida en tienda, contraentrega |
| Dirección de envío | nombre, teléfono, dirección 1 y 2, ciudad y departamento |
| Comprador | documento (desde la fuente configurada), correo, teléfono |
| Nota | nota del comprador |
| Atributos | `note_attributes` |

Sin diferencias, no pasa nada (sólo el espejo, como hoy). Con diferencias se crea un **cambio** en `shopify.order_changes`:
fila del espejo, `base_updated_at` (de la versión aplicada), `updated_at` del snapshot nuevo, el diff por grupo (antes y
después, armado para pintar), el estado y su motivo.

**Conflictos con QEP.** Un grupo que cambió en Shopify **y** cuyo valor en el pedido difiere de la proyección (el asesor lo
tocó) **no se aplica**: gana QEP. El cambio lo marca como conflicto en ese grupo para que se vea en la bandeja; el resto
de los grupos sigue su curso.

**El eco de QEP.** Si el valor nuevo de Shopify es igual al valor actual del pedido (típicamente, la corrección que QEP
envió, §5), ese grupo no es cambio: se adopta como aplicado sin aviso.

**Un cambio por pedido a la vez.** Si llega otro snapshot mientras hay un cambio pendiente, el nuevo diff se calcula
igual contra la versión aplicada y **reemplaza** al pendiente (`Superseded`): Shopify siempre manda el pedido completo.

### 4. Aplicar, esperar o bloquear

Configuración nueva en la tienda (`PUT …/shopify/stores/{connectionId}`, junto al resto):

- `editSync`: `Manual` (por defecto) | `Automatic`.
- `editPaymentStatuses`: los estados de pago de QEP en los que se acepta aplicar un cambio (por defecto, todos).

Estado del cambio al crearse:

| Pedido en QEP | Pago permitido | `editSync` | Estado |
| --- | --- | --- | --- |
| No editable (preparado, facturado, anulado) | — | — | `Blocked` (motivo: `order_not_editable`) |
| Editable | no | — | `Blocked` (motivo: `payment_status_not_allowed`) |
| Editable | sí | `Automatic` | se aplica en el mismo procesamiento → `Applied` |
| Editable | sí | `Manual` | `Pending` |

- **Aplicar** = `IShopifyOrderWriter.ApplyEditAsync` → `IExternalOrderWriter.ApplyExternalEditAsync` → un método nuevo de
  dominio (`Order.ApplyExternalEdit`) que reemplaza, de los grupos que se aplican, líneas, detalle de envío, parte de envío,
  nota del comprador y deja historial (`order_history`, mismo evento que la edición equivalente) y auditoría. Respeta
  `IsEditable` y la versión (412 si el pedido cambió entre el cálculo y la aplicación: el cambio queda `Pending`).
  Precios: los de Shopify, como en la importación (`ShopifyPricing`), **sin** los descuentos de catálogo de QEP.
- **Ciudad y documento** se resuelven con las mismas reglas de la importación (lista, sinónimos, bandeja de ciudades;
  fuente del documento).
- **Comprador** (decisión mía): documento, correo y teléfono viven en el cliente de QEP, no en el pedido, y el cliente
  es maestro de QEP. El grupo es **informativo**: se muestra en el cambio y no toca el cliente. Si el documento nuevo
  resuelve a **otro cliente**, el grupo queda en conflicto con motivo `customer_changed` (el pedido no cambia de cliente).
- **Líneas sin emparejar** (decisión mía): si una línea nueva o editada no empareja con una variante (ni con un mapeo de
  línea personalizada), el cambio entero queda `Blocked` con motivo `product_unmatched`: nunca se aplica a medias.
- **Pedido sin parte de envío** (recogida, o ciudad sin resolver al importar): aplicar la dirección crea la parte si la
  ciudad resuelve (como `AttachExternalShipping`, quitando de `Notes` la línea de dirección cruda); si no resuelve, el
  grupo queda en conflicto con motivo `city_unresolved` y la ciudad va a la bandeja de ciudades como hoy.
- Aplicado el cambio, `applied_payload` y `applied_projection` pasan al snapshot nuevo; los grupos en conflicto
  conservan en la proyección el valor de QEP.
- **`Blocked` se puede aceptar cuando el pedido vuelva a ser editable**: aceptar reevalúa la tabla en ese momento. Es el
  flujo del owner para un pedido preparado que no ha salido: deshacer la preparación (`Order.UndoPreparation`, ya
  existe), anular la guía, aceptar el cambio y volver a preparar. Mientras siga sin poder aplicarse, aceptar responde 422
  con el motivo y la fila sólo se puede rechazar o marcar como revisada (`Reviewed`).
- **Rechazar** un `Pending` lo deja `Rejected` y adopta el snapshot como versión aplicada sin tocar el pedido: el
  siguiente diff no vuelve a proponer lo rechazado.

### 5. Enviar correcciones a Shopify (B1)

- **Qué se puede editar en QEP**, sólo en pedidos `Source = Shopify` y editables (`Order.IsEditable`; owner, 2026-11-14:
  un preparado que no ha salido se corrige deshaciendo la preparación, anulando la guía, corrigiendo y volviendo a
  preparar; un facturado no): la parte de envío (nombre, teléfono, dirección, ciudad de la lista; también crearla si el pedido no la
  tiene), la nota y los atributos de Shopify. Hoy el pedido no edita su parte de envío (`Order.cs:1617`); esto agrega ese
  camino sólo para pedidos de Shopify.
- **Nota y atributos de Shopify viven en el módulo Shopify, no en el pedido** (decisión mía). `Order` no tiene atributos,
  y la nota del comprador hoy va dentro de `Order.Notes`, que es lo que sale en «Observaciones» del Excel del ERP:
  moverla rompería esa hoja, y `BuyerNotes` es de la tienda pública (sólo la escribe `CreateFromStore`). Por eso se
  guardan en la fila del espejo como **valores de QEP** (`qep_note`, `qep_attributes`, nulos mientras nadie los corrija),
  se editan por un endpoint de Shopify y se comparan ahí para conflicto y eco. `Order.Notes` no cambia.
- **Las correcciones del sistema también viajan** (decisión mía). Los trabajos de sinónimos de ciudad
  (`ShopifyCityCorrectionProcessor`) y la bandeja de ciudades corrigen la ciudad del pedido: actualizan la proyección
  (no cuentan como «el asesor lo tocó») y encolan el envío a Shopify, porque el objetivo es que Shopify quede con la
  ciudad buena. Si el asesor ya editó la parte de envío, el trabajo de sinónimos la salta como hoy
  (`Order.SameExceptCity` → `shipping_party_changed`): gana lo del asesor.
- **Al guardar**, en la misma transacción, se encola un envío en `shopify.order_pushes` (por Bootstrapper, como el resto
  de los puertos): pedido, fila del espejo, qué grupos, `pending`. La proyección se actualiza con el valor de QEP, para
  que una edición posterior de Shopify sobre ese grupo cuente como conflicto (gana QEP).
- **`ShopifyOrderPushWorker`** manda `orderUpdate` (GraphQL) con `shippingAddress`, `note` y `customAttributes` del
  pedido; la ciudad va con el nombre de QEP y el departamento con `provinceCode` (`ColombiaProvinceCodes`). Reintenta con
  espera ante 429, 5xx y token vencido (`AccessToken = null`); un 4xx definitivo (por ejemplo `ACCESS_DENIED` por falta
  de `write_orders`) deja el envío `failed` con su código.
- **El pedido muestra el estado del envío** (enviado, pendiente, fallido con su código) en su detalle.
- El eco (`orders/updated` con el valor de QEP) se adopta sin aviso (§3).

### 6. Bandeja y API (BFF)

- La bandeja de Shopify suma la sección **«Cambios por revisar»**: `Pending` y `Blocked` sin revisar, con contador en
  `GET …/shopify/orders/summary`.
- `GET …/shopify/orders/{id}/changes`: los cambios del pedido, cada uno con sus grupos, antes y después, conflicto y motivo.
- `POST …/shopify/changes/{changeId}/accept` | `/reject` | `/review` (este último para `Blocked`).
- La pantalla de la tienda suma `editSync` y `editPaymentStatuses`.
- El detalle del pedido de Shopify suma la edición de envío, nota y atributos, y el estado del envío a Shopify.

### 7. Permisos

Aceptar, rechazar y revisar: `quotations.order.manage` (como «Sincronizar ahora» y el reintento). Configurar la tienda:
`integrations.connection.manage`. Editar envío, nota y atributos: el permiso de edición del pedido que ya existe.

### 8. Pruebas (TDD)

- Parsers: cantidad vigente, línea quitada, `billing_address.company`, atributos (REST y GraphQL, con fixtures con la
  forma real).
- Diff por grupo y proyección: sin cambio, cada grupo por separado, conflicto con QEP, eco, línea personalizada.
- Tabla de estados del §4 completa.
- Aplicar: idempotencia (dos entregas del mismo snapshot), 412, cliente distinto, precios de Shopify sin descuentos de
  catálogo, ciudad por sinónimo.
- Envío: encolado en la misma transacción, reintentos, `failed` definitivo, eco.
- Migraciones Shopify y Quotations (si `BuyerNotes` cambia de fuente).

### 9. Verificaciones pendientes contra Shopify

| # | Qué | Cómo se cierra |
| --- | --- | --- |
| V-11 | Que el webhook REST traiga `current_quantity` y la línea quitada; que `orders/updated` llegue al editar un pedido (orderEdit) | Editar un pedido en la tienda de desarrollo |
| V-12 | Que `orderUpdate` acepte `shippingAddress` con `provinceCode` de Colombia y `customAttributes` | Corregir una ciudad en QEP y verla en Shopify |
| V-13 | Que las instalaciones existentes tengan `write_orders` | `currentAppInstallation.accessScopes` |

## Lo que queda al implementador

Nombres de tablas y columnas finales, el formato del diff (siempre que el frontend no tenga que conocer enums del
backend para pintarlo), la frecuencia del worker de envíos y la política exacta de espera entre reintentos.

# Eliminar producto del catálogo — diseño (backend)

Fecha: 2026-11-08 · Repo: `qep-backend` · Rama: `feat/delete-product`
Spec hermano: `qep-frontend/docs/superpowers/specs/2026-11-08-delete-product-design.md`

## Intención

El owner creó productos de prueba y necesita quitarlos. Hoy el catálogo sólo permite desactivar un producto, y borrar
variantes sueltas con `deletedVariantIds` desactiva en vez de borrar la que ya se usó (`IVariantUsageChecker`).
No existe forma de que un producto desaparezca.

El owner pidió el **modelo de Shopify**. En Shopify, borrar un producto es permanente. Los pedidos completados
conservan los datos de la línea y pierden el vínculo con el producto. Los borradores conservan lo guardado y pierden
la referencia.

## Metas

- Borrar un producto **con todas sus variantes**, de forma permanente, aunque se haya usado en cotizaciones, pedidos,
  ventas del POS o solicitudes de la tienda.
- Que los documentos cerrados (cotizaciones enviadas, pedidos, ventas del POS) **se vean igual después**: código y
  nombre salen de su propia foto, no del catálogo.
- Que ningún flujo existente se rompa por una variante que ya no existe: recálculos de precio, cambio de moneda y
  toma de una solicitud de la tienda.
- Que las cotizaciones en borrador queden **con el precio correcto** después de perder la línea.
- **Sin imágenes huérfanas:** borrar un producto libera todas las fotos de su galería, y quitar una foto de la galería
  por el `PUT` del producto libera la que salió. Storage borra los objetos y marca el `FileResource` como `Purged`.
- Un solo producto a la vez, desde el producto.

## No metas

- Papelera, deshacer o borrado lógico.
- Borrado masivo desde la lista.
- Cambiar la regla de `deletedVariantIds` (la variante usada se desactiva). La inconsistencia queda escrita abajo.
- Limpiar el carrito del POS (vive en `sessionStorage` del navegador). Sigue el mensaje de `pos.sale.product_not_found`.
- Cambios en `qep-storefront`: ya trata una variante ausente como no disponible.
- Cambios en la importación de Shopify (ver «Comportamiento por área»).
- Barrer las imágenes que ya quedaron huérfanas antes de este trabajo.

## Anti-metas (sería un fracaso aunque funcione)

- Que un pedido, una cotización enviada o una venta del POS cambie de código, nombre o total después del borrado.
- Que el borrado quede a medias: producto borrado y líneas de borrador vivas apuntando a él, o al revés.
- Que un borrador quede con totales guardados que no corresponden a sus líneas.
- Que una cotización enviada con una línea borrada no se pueda volver a guardar por otra edición.
- Que Storage borre una foto que otra galería todavía usa.
- Que borrar un producto de un tenant toque datos de otro.

## Decisiones del owner

1. **Modelo Shopify.** El borrado es permanente e irreversible. Se borran el producto y todas sus variantes aunque
   estén en uso.
2. **Los documentos cerrados conservan su foto** de código y nombre, y se ven igual después.
3. **Cotizaciones en borrador: la línea del producto borrado desaparece en silencio.** El owner lo eligió sobre
   mostrar «producto eliminado». **Riesgo aceptado:** el total de un borrador puede cambiar sin que el asesor lo note.
   Con productos de prueba no importa. Con un producto real borrado por error, sí.
4. **Uno a la vez**, desde el producto. Sin borrado masivo.
5. **Permiso:** se reutiliza `catalog.product.manage` (`CatalogPermissions.ProductManage`). No hay permiso nuevo.
6. Se le explicó al owner que la regla de `deletedVariantIds` («la variante usada se desactiva, nunca se deja
   huérfana») contradice esto. **Eligió igual el modelo Shopify** para el borrado del producto entero.
7. **Los borradores se vuelven a cotizar** igual que al quitar una línea a mano: `RemoveQuotationItem.cs:81-85` corre
   `QuotationPricingRecalculation.ApplyAsync` después de quitarla, porque el descuento por escala agrupada
   (`QuotationScaleGroupPricing`) y el mínimo de compra de la cotización entera pueden cambiar.
8. **Solicitudes de la tienda en `New`:** se quitan las líneas de las variantes borradas, pero `Subtotal`,
   `DiscountAmount`, `TaxAmount` y `Total` **se conservan** como los vio el comprador (`StoreOrderRequest.cs:45-51`). Al
   convertir, `TotalDifference` (`StoreOrderRequest.cs:75-76`, asignada en la 203) le muestra al asesor la diferencia,
   y esa diferencia es la señal de que el comprador pidió algo que ya no existe. **Costo aceptado:** mientras la
   solicitud está pendiente, sus líneas no suman su total.
9. **Sin imágenes huérfanas**, tanto al borrar el producto como al quitar una foto de la galería por el `PUT`.

Cambios: 2026-11-08 — se agregan las decisiones 7, 8 y 9 (antes los totales del borrador y las imágenes eran
pendientes o fuera de alcance).

## Decisiones del diseñador (para revisión del owner)

- **D-1. Ruta y forma.** `DELETE /api/v1/tenants/{tenantId}/catalog/products/{productId}` (**nueva**), en el grupo de
  `ProductEndpoints.cs:16` junto a `GET` y `PUT` de `/products/{productId:guid}` (líneas 40 y 53). Copia el
  precedente de `DELETE /categories/{categoryId}` (`CategoryEndpoints.cs:54-61`, `RequireVersion` en 141-146,
  `DeleteCategory.cs`). El pedido mencionaba `/api/v1/products/{productId}`, pero esa ruta no sigue la convención del
  grupo; se usa la del grupo.
- **D-2. Concurrencia.** `If-Match` obligatorio, con la versión del producto (`Version`, token de concurrencia en
  `CatalogDbContext.cs:104-106`). Sin header: 428 `precondition.if_match_required`. Versión vieja: 412
  `concurrency.conflict`.
- **D-3. Auditoría.** Evento **nuevo** `catalog.product.deleted` por `ICatalogAuditPublisher`, igual que
  `catalog.category.deleted` (`DeleteCategory.cs:67-73`).
- **D-4. Sin evento de integración por el borrado en sí.** Ningún módulo consume un borrado de producto. El único
  evento nuevo es el de imágenes liberadas (D-11).
- **D-5. Nada bloquea el borrado.** No hay caso 422 «en uso».
- **D-6. Una sola transacción, orquestada en el composition root** (`Bootstrapper`), como
  `CatalogVariantUsageChecker` (`Bootstrapper/CatalogVariantUsageChecker.cs:48-63`), que ya consulta
  `quotations.quotation_items`, `quotations.order_items` y `pos.sale_lines` entre esquemas. La escritura entre
  módulos tiene precedente: `Bootstrapper/CustomerMergeReferences.cs` ya hace UPDATE en otros esquemas dentro de la
  transacción de Customers. Ver «Pasos de la transacción».
- **D-7. Solicitudes de la tienda en estado `New` pierden la línea.** Si no, `TakeOrderRequest` vuelve a cotizar y
  falla con `quotation.item.product_not_found`, que el flujo de tomar solicitud del frontend no traduce (`store.api.ts`;
  cotizaciones y pedidos sí, en `quotes.api.ts` y `orders.api.ts`). Los totales se conservan (decisión del
  owner 8).
- **D-8. Cotizaciones enviadas:** el recálculo de precio salta las líneas cuya variante ya no existe, como ya hace
  `OrderPricingRecalculation.cs:47-57`, en vez de dejarlas sin escalas y bajar su descuento a 0 %
  (`QuotationPricingRecalculation.cs:75-86`). El cambio de moneda de una cotización enviada con una línea borrada no
  debe tumbar el guardado entero (`QuotationEditsApplication.cs:56-67`).
- **D-9. Editar la cantidad de una línea borrada** (cotización enviada o pedido) sigue respondiendo el 422 existente
  `quotation.item.product_not_found`. Es aceptable: no hay precio contra el cual recalcular.
- **D-10. Se acepta perder el historial de cambios de precio** del producto (`product_price_changes` cae en cascada).
- **D-11. Imágenes por outbox, con el precedente de los comprobantes de pago.** Catalog escribe en el outbox de
  plataforma el evento **nuevo** `catalog.product.images-released.v1` (convención `<módulo>.<entidad>.<hecho>.v1`, como
  `quotations.order.payment-proofs-detached.v1` y `catalog.product-export-ready.v1`), con `tenantId` y los `fileIds`
  liberados, **en la misma transacción** que el borrado o el `PUT`. Un procesador **nuevo** de Storage lo consume. El
  borrado del producto no espera la limpieza.
- **D-12. Sonda nueva de Catalog.** Un `IFileReferenceProbe` de Catalog (**nuevo**) responde si alguna galería de
  producto todavía referencia el `fileId`. Protege archivos compartidos y la carrera de una foto que se vuelve a
  agregar antes de que corra el procesador.

## Restricciones del código existente

- `Modules.<Modulo>.Application` no referencia EF Core ni Npgsql; `tests/ArchitectureTests/` lo verifica. El SQL entre
  esquemas va en Infrastructure o en `Bootstrapper`, nunca en Application.
- `QuotationItem.ProductCode` es null mientras la cotización es borrador y **también en las líneas enviadas antes de
  que existiera la columna** (`QuotationItem.cs:58-64`): esas se leen en vivo del catálogo. Sin backfill, perderían
  código y nombre al borrar el producto.
- `OrderItem` tiene `ProductCode` y `ProductName` (`OrderItem.cs:53-55`), que también pueden ser null.
- Los nombres de la foto se recortan con `QuotationProductSnapshot.Fit` → `SnapshotText.Fit(name, NameMaxLength)`
  (`QuotationProductSnapshot.cs:35-43`). El backfill debe producir el mismo recorte.
- **Los totales de la cotización se guardan** (`quotations.total`, `net_total` y otros; `QuotationsDbContext.cs:117,147`).
  Por eso la limpieza de borradores **no puede ser SQL plano**: carga los agregados `Quotation` afectados y corre la
  pasada de precios de Quotations dentro de la transacción compartida.
- **La pasada de precios lee precios y escalas del catálogo** (`QuotationPricingRecalculation.cs:70-73`). Las líneas
  se quitan y la pasada corre **sin depender de que el producto ya esté borrado**: o antes de borrarlo, o de modo que
  la variante borrada no entre en la lectura. El orden exacto lo decide el constructor; esta restricción no.
- El historial de ediciones de cotización tiene el respaldo `"un producto"` cuando no encuentra el nombre
  (`QuotationItemEditHistory.cs:54-56`).
- Estados: `QuotationStatus.Draft` (`QuotationStatus.cs:21-27`) y `StoreOrderRequestStatus.New`
  (`StoreOrderRequestStatus.cs`). Las líneas de solicitud viven en `store.order_request_lines`.
- La galería se reemplaza en el lugar: `Product.ReplaceGallery` (`Product.cs:473-475`) borra las fotos que no vienen.
  Ahí se sabe qué `fileIds` salieron.
- `FileResource.SoftDelete` (`FileResource.cs:158-170`) sólo marca `Deleted` y no borra objetos. Los purgados de
  comprobantes tienen sus métodos propios (`PurgeDetachedPaymentProof`, `FileResource.cs:267-271`). Las variantes de
  imagen se guardan como `FileVariant` con su propia clave (`FileResource.AddVariant`, líneas 314-338; las genera
  `CompleteUpload.cs` con `StorageKey.VariantFor`).
- `IFileReferenceProbe` vive en `BuildingBlocks.Application` y hoy sólo la implementa Quotations
  (`OrderPaymentProofFileReferenceProbe`). `StagingCleanupProcessor` también consulta las sondas: la nueva de Catalog
  lo afecta (sólo para proteger, nunca para borrar).
- Las pruebas de integración usan Testcontainers. Correr por proyecto, uno a la vez.

## Contrato

```
DELETE /api/v1/tenants/{tenantId}/catalog/products/{productId}
If-Match: "7"            (también acepta W/"7", como el resto)
X-Qep-Client: web        (CSRF, como todo método mutante)
```

| Status | Cuándo | Código |
| --- | --- | --- |
| 204 | Borrado | — |
| 403 | Sin `catalog.product.manage` | existente |
| 404 | El producto no existe en ese tenant | el mismo que ya devuelve `GET /products/{productId}` |
| 412 | `If-Match` con versión vieja | `concurrency.conflict` (existente) |
| 428 | Sin `If-Match` válido | `precondition.if_match_required` (existente) |

Sin cuerpo de respuesta. Sin 422. El `PUT /products/{productId}` no cambia de contrato.

**Evento de outbox (nuevo):** `catalog.product.images-released.v1`, payload `{ tenantId, fileIds: [...] }`. Se escribe
sólo si la lista no está vacía.

## Pasos de la transacción

Todo en **una** transacción de base de datos. Si un paso falla, no queda nada escrito.

1. **Cargar y validar.** Autorizar `ProductManage`, cargar el producto del tenant (404 si no está), comparar versión
   (412).
2. **Backfill de fotos.** Toda fila de `quotations.quotation_items` y `quotations.order_items` del tenant, de las
   variantes del producto, con `ProductCode` null **y que no pertenezca a una cotización en borrador**, recibe
   `ProductCode` y `ProductName` desde el catálogo, con el recorte de `SnapshotText.Fit`. El constructor decide si
   aplica el recorte en SQL o en código; debe dar el mismo resultado que `QuotationProductSnapshot.Fit`.
3. **Borradores de cotización.** Se cargan los agregados `Quotation` en `Draft` del tenant con líneas de esas
   variantes. A cada uno se le quitan esas líneas, se corre `QuotationPricingRecalculation.ApplyAsync` como en
   `RemoveQuotationItem.cs:81-85` y se sube `version`. Respetar la restricción de orden con la lectura del catálogo.
   Entrada de historial por la línea quitada: decisión del constructor. La recomendación es no registrar ninguna, o
   una de sistema; en cualquier caso, el historial existente debe seguir pintándose.
4. **Solicitudes de la tienda en `New`.** Se borran las líneas de esas variantes. `Subtotal`, `DiscountAmount`,
   `TaxAmount` y `Total` no se tocan. Si una solicitud queda con cero líneas, el constructor decide y documenta qué
   pasa (recomendado: dejarla, y el asesor la descarta). **Pendiente.**
5. **Borrar el producto.** La cascada existente se lleva variantes, galería, `product_options`, precios, valores de
   opción, escalas y `product_price_changes`.
6. **Outbox de imágenes.** Si la galería tenía fotos, se escribe `catalog.product.images-released.v1` con sus `fileIds`.
7. **Auditar** `catalog.product.deleted` y guardar.

Las ventas del POS (`pos.sale_lines`) no se tocan: ya guardan su foto.

## Liberación de imágenes

**Al quitar fotos por el `PUT`:** el handler compara la galería antes y después de `ReplaceGallery` y escribe el evento
con los `fileIds` que salieron, en la misma transacción del guardado. Galería sin cambios, o sólo reordenada: no se
escribe nada.

**Procesador nuevo de Storage**, modelado en `PaymentProofDetachProcessor.cs:18-47`:

- Consume el evento del outbox de plataforma con su propio consumidor y anti-join contra su inbox.
- Por cada `fileId`, del tenant del evento: consulta las `IFileReferenceProbe`. Si alguna responde `true`, lo salta y
  lo registra en el log.
- Si no: borra **primero** los objetos (la clave del archivo, la pública si la tiene y la de cada `FileVariant`) y
  **después** marca el `FileResource` como `Purged` con un método de dominio **nuevo** (por ejemplo
  `PurgeReleasedProductImage`), igual que `PurgeDetachedPaymentProof`. El constructor confirma en qué bucket viven las
  fotos de producto y usa `IObjectStorage` o `IPublicObjectStorage` según corresponda.
- Guarda archivo por archivo; el último junto con el inbox.
- Errores deterministas (payload mal formado, estado que el dominio rechaza): se registran y no se reintentan.
  Transitorios (R2, base, timeout): se descarta lo pendiente del mensaje y vuelve en el tick siguiente.
- Un archivo que ya no está `Available` se salta (reintento idempotente).

## Comportamiento por área

| Área | Antes del borrado | Después |
| --- | --- | --- |
| Cotización enviada / convertida / anulada / vencida | Línea con foto, o leída en vivo si es vieja | Foto completa (paso 2). Se ve igual |
| Cotización en borrador | Línea viva | La línea desaparece, se recotiza el resto y sube de versión |
| Pedido | Línea con foto | Igual. El recálculo ya la salta (`OrderPricingRecalculation.cs:52-57`) |
| Venta del POS | Línea con foto | Igual |
| Carrito del POS | En el navegador | Al cobrar: `pos.sale.product_not_found` (`CreatePosSale.cs:249`). Sin cambio |
| Solicitud de la tienda `New` | Línea viva | La línea desaparece; los totales quedan como los vio el comprador |
| Solicitud `Converted` / `Discarded` | — | Sin cambio |
| Storefront | Variante visible | No disponible. Sin cambio |
| Shopify | SKU emparejado al importar | Un pedido nuevo con ese SKU entra a la bandeja como `ProductUnmatched`. Esperado, sin cambio |
| Reporte de cambios de precio | Historial del producto | Se pierde (D-10) |
| Imágenes | Galería | Storage borra objetos y variantes y marca `Purged`, salvo que otra galería las use |

### Cotización enviada con línea borrada

- **Recálculo:** las líneas sin variante en el catálogo quedan fuera de la pasada y conservan su descuento (D-8).
- **Cambio de moneda:** el constructor elige cómo resolver `QuotationProductPricingResolver.ResolveManyAsync`
  (líneas 70 y 118 lanzan `quotation.item.product_not_found`) para que una línea borrada no tumbe el guardado. La
  línea borrada conserva su precio; no se puede convertir a otra moneda sin catálogo. **Pendiente para el
  constructor:** si ese caso debe rechazarse con un código propio o pasar con la línea en la moneda vieja. No se
  admite una cotización con dos monedas (comentario en `QuotationEditsApplication.cs:54-55`), así que la opción
  segura es un 422 claro; documentar la elección.
- **Editar cantidad de esa línea:** 422 `quotation.item.product_not_found` (existente).

### Inconsistencia que queda escrita

`deletedVariantIds` en el `PUT` del producto sigue desactivando una variante usada (`IVariantUsageChecker`). Borrar el
producto entero, en cambio, borra variantes usadas. Es deliberado (decisión del owner 6). Unificar las dos reglas es
otro trabajo.

## Pruebas requeridas (TDD: RED antes que GREEN, con evidencia literal)

**Unitarias (Application):**

- El handler responde 404 con un producto de otro tenant o inexistente.
- 412 con versión vieja; no llama a la limpieza ni borra.
- Sin `ProductManage`: rechazado.
- Publica `catalog.product.deleted` con el id del producto.
- `QuotationPricingRecalculation`: una línea cuya variante no devuelve el lookup conserva su descuento (RED con el
  código actual, que la baja a 0 %).
- Cambio de moneda de una cotización enviada con una línea borrada: el comportamiento elegido por el constructor.
- `PUT` del producto: quitar una foto escribe `catalog.product.images-released.v1` con ese `fileId`; galería sin
  cambios o sólo reordenada no escribe nada.
- Dominio: el método nuevo de purga de `FileResource` exige `Available`.

**Endpoint:**

- 428 sin `If-Match`, 204 con versión correcta, 403 sin permiso.

**Integración (Testcontainers, un Postgres por clase):**

- Producto usado en: cotización enviada vieja sin foto, cotización enviada con foto, cotización en borrador, pedido sin
  foto, pedido con foto, venta del POS, solicitud de la tienda `New` y `Converted`. Después del borrado:
  - las líneas enviadas y de pedido tienen código y nombre (los viejos, recortados con `SnapshotText.Fit`);
  - el borrador no tiene la línea, su versión subió y sus totales guardados son los de una pasada de precios sobre las
    líneas restantes (caso con escala agrupada que cambia de tramo y caso con mínimo de compra que se cae);
  - la solicitud `New` no tiene la línea y sus cuatro totales no cambiaron; la `Converted` está intacta;
  - la venta del POS está intacta;
  - no existen el producto, sus variantes, precios, escalas, opciones ni galería;
  - el outbox tiene `catalog.product.images-released.v1` con los `fileIds` de la galería.
- Otro tenant con datos parecidos no cambia.
- Atomicidad: un fallo forzado después del backfill deja todo como estaba, outbox incluido.
- `TakeOrderRequest` de una solicitud que perdió una línea funciona con las líneas restantes y deja `TotalDifference`.
- Procesador de Storage, con los mismos casos que las pruebas de `PaymentProofDetachProcessor`: borra objeto y
  variantes y marca `Purged`; un `fileId` que la sonda reporta referenciado no se borra; fallo transitorio al borrar
  reintenta en el siguiente tick; payload mal formado se marca y no se reintenta; un mensaje repetido no hace nada.
- Sonda de Catalog: `true` si alguna galería tiene el `fileId`, `false` si no.
- `ArchitectureTests` en verde: ninguna referencia nueva de Application a EF Core o Npgsql.

## Orden de despliegue

**Backend primero.** No hay columnas nuevas: la ruta, los pasos y el evento usan tablas existentes (outbox e inbox de
plataforma); el plan agrega sólo un índice sobre el archivo de la galería. Un frontend nuevo contra un backend viejo
recibe 404 o 405: el 405 se muestra como error genérico y el 404 se trata como «ya eliminado» (spec del frontend,
F-6). No rompe nada.

## Queda para el constructor

- Orden exacto entre la pasada de precios de los borradores y el borrado del producto, respetando la restricción.
- Entrada de historial por la línea de borrador quitada (ninguna o de sistema), sin romper el historial existente.
- Qué pasa con una solicitud de la tienda que queda sin líneas (recomendado: dejarla).
- Cómo aplicar `SnapshotText.Fit` en el backfill (SQL o código).
- Cómo responder un cambio de moneda en una cotización enviada con una línea borrada.
- Dónde vive la orquestación entre módulos en `Bootstrapper` (un servicio con un puerto en Catalog.Application, como
  `IVariantUsageChecker`, es la forma conocida).
- En qué bucket viven las fotos de producto y qué claves borra el procesador.
- Actualizar `docs/estado.md` al mergear a `develop`, no en la rama.

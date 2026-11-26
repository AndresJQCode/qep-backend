# Mayorista: copiar la imagen al producto que crea Compras

## Problema

Cuando el mayorista aprueba una orden de compra, `WholesaleOrderEventApplier` crea en el catálogo del
distribuidor un producto por cada línea sin variante propia (`PurchasesCatalogWriter`). El producto nace sin
imagen: ni el evento `quotations.order.wholesale-approved.v1` ni `ExternalProductDraft` llevan una, y nada copia
la foto del producto del mayorista.

## Decisión

Se copia la imagen **después del commit** del evento, fuera de la transacción del producto (mismo patrón que
`ShopifyProductImageStore`). El evento no cambia: la línea de la orden ya guarda `SupplierVariantId` y la orden
guarda `SupplierTenantId`, que bastan para encontrar la foto en el catálogo del mayorista.

1. `PurchasesCatalogEntry` suma `ProductId` (el producto de la variante hallada o creada).
2. El applier anota, por cada producto **creado**, un `PendingProductImage` (comprador, mayorista, variante del
   mayorista, producto nuevo). Un producto que ya existía por código no se toca.
3. Después de `CommitAsync`, el procesador llama `applier.CopyPendingImagesAsync`, que delega en el puerto
   `IPurchasesProductImages` (adaptador en Bootstrapper). **Nunca lanza por la imagen**: lo ya confirmado no se
   deshace y el mensaje ya quedó procesado; una falla se registra.
4. El adaptador: busca la variante en el catálogo del **mayorista**, toma su foto (`ImageFileIdOf`: la de la
   variante o la portada), exige que el archivo sea del mayorista, `Available` y de dueño de producto; baja los
   bytes de `IObjectStorage`; los sube al tenant del distribuidor con `IStorageSystemUploads.StoreAsync` (dueño: el
   producto nuevo) y la pone en la galería con `IExternalProductWriter.ReplaceImageAsync(previousFileId: null)`.
   Si el archivo no entra a la galería, Catalog lo libera.

## Fuera de alcance

- Productos que el distribuidor ya tenía (hallados por código): no se les cambia la imagen.
- Reintento de una copia fallida: no hay; el distribuidor sube la foto a mano. Registrado, no silencioso.
- Cambios de foto posteriores del mayorista no se propagan.

## Pruebas

- Applier (unit): una línea nueva anota su copia; un producto existente no; una falla del copiador no cambia el
  resultado ni lanza.
- Adaptador (integración, Bootstrapper): el producto creado queda con la imagen del mayorista y el archivo es del
  tenant del distribuidor.

# Foto en las líneas del pedido del comprador

Fecha: 2026-11-15 · Repos: `qep-backend` (este) y luego `qep-storefront` (spec propio).

## Intención

El rediseño del detalle de pedido de la tienda (`/cuenta/pedidos/{id}`, aprobado por el owner el
2026-11-15) muestra una miniatura por línea. Hoy la línea del pedido del comprador no trae foto:
`BuyerOrderLine(Name, Quantity, UnitPrice, LineTotal)` y `BuyerOrderLineDto` con los mismos campos.

## Decisiones del owner

- **La foto es la del catálogo actual, no una copia guardada al comprar** (2026-11-15). No se
  guardan datos que no hacen falta; es el mismo criterio de Shopify (`LineItem.image` sale de la
  variante, mientras título y precio quedan fijados al crear el pedido). Consecuencia aceptada: si
  la tienda cambia la foto, el pedido muestra la nueva; si la borra, la línea queda sin foto.
- **Qué foto:** la de la variante o, si la variante no eligió ninguna, la principal del producto.
  `null` si no hay ninguna.

## Objetivos y no objetivos

- Objetivo: `imageUrl` (nullable) en cada línea del detalle del comprador, en el `GET` del detalle
  y en la respuesta de anular, que también devuelve el detalle.
- No objetivo: foto en la **lista** de pedidos (`BuyerOrderSummary`); sin migración ni columna nueva.
- Anti-objetivo: una consulta por línea. El costo por pedido tiene que ser fijo.

## Enfoques considerados

1. **Reusar `IQuotationProductLookup`** — elegido. Ya resuelve la foto con la regla de arriba, trae
   también variantes y productos inactivos y lo hace en tres consultas por lote (productos, opciones,
   archivos), sin importar cuántas líneas haya. Costo: la consulta de opciones (para el nombre
   visible) sobra acá.
2. Puerto nuevo sólo de fotos — descartado: ahorra una consulta pero duplica en `Bootstrapper` casi
   todo el adaptador existente, con sus pruebas.

## Diseño

- `BuyerOrderGateway` recibe `IQuotationProductLookup` por inyección.
- Al armar el detalle, junto con `ResolvePlacesAsync`, se llama **una vez**
  `FindManyAsync(tenantId, variantIds distintos del pedido)`; sin líneas no se llama.
- `BuyerOrderLine` y `BuyerOrderLineDto` ganan `string? ImageUrl` al final. La línea toma
  `refs[item.VariantId]?.ImageUrl`; una variante que el lookup no devuelve deja `null`.
- En `CancelAsync` las fotos se resuelven **antes** de anular, como los lugares: si el lookup falla,
  el pedido queda intacto. En el reintento por concurrencia se resuelven sobre el pedido releído.
- Una excepción del lookup sale tal cual (mismo trato que la geografía): el detalle falla, no se
  disfraza de foto nula.
- `BuyerOrdersAdapter` (Bootstrapper) pasa `ImageUrl` al DTO.

## Compatibilidad y despliegue

Campo nuevo y opcional en la respuesta. El storefront parsea con zod, que descarta claves
desconocidas: el backend se despliega primero sin romper la tienda actual.

## Queda al implementador

Nombres internos, la forma de pasar las fotos a `ToDetail` (junto a `Places` o aparte) y el
detalle de las pruebas: unitarias del gateway con un lookup falso (con foto, sin foto, variante no
devuelta, sin líneas, lookup que falla antes de anular) y una de integración del endpoint del detalle.

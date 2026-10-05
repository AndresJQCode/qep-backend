# Recoger en tienda en el pedido y en el Excel

Fecha: 2026-10-05. Cruza los dos repos: `qep-backend` (Excel) y `qep-frontend` (detalle del pedido).

## Problema

Al crear una cotización, en "Dirección de entrega" se puede elegir **Recoger en tienda**
(`Quotation.IsStorePickup`, columna `quotations.is_store_pickup`). Ese dato hoy se pierde en dos
lugares:

1. El **detalle del pedido** no muestra ninguna dirección, así que quien despacha no se entera de
   que el cliente pasa a recoger.
2. El **Excel de pedidos** pone siempre `Coordinadora` en "Transportadora (P2)": es una columna
   `Fixed` del layout, un literal que no puede variar por fila
   (`QuotationsSeeder.OrdersExportColumns`).

## Decisiones

- "Transportadora (P2)" vale `Recoger en tienda` si la cotización del pedido es de recogida, y
  `Coordinadora` en cualquier otro caso.
- Las demás columnas no cambian con la recogida: "Direccion (P8)", "Ciudad (P4)" y
  "Telefono (P10)" siguen cayendo a los datos del cliente cuando no hay parte de envío, y
  "Flete (P3)" sigue en `CONTRAENTREGA`. Decisión del owner, 2026-10-05.
- El layout ya guardado en prod **no se migra**: el owner borra la fila después de desplegar y la
  semilla la recrea (prod está en pruebas). La semilla ya trae la hoja `MIGRACION 1`, así que no se
  pierde ese ajuste.

## Backend — columna `carrier`

- Entrada nueva en `OrdersExportColumnCatalog.Columns`, al final: clave `carrier`, header por
  defecto `Transportadora`, `DefaultVisible: false` — mismo criterio que `coordinadora_city`: un
  layout ya guardado la recibe al final pero oculta, sin columna sorpresa.
- `OrdersExportProcessor.RowsFor` la llena por fila desde `quotation.IsStorePickup`. Los dos
  textos son contrato del ERP del tenant, no copy de UI: viven como constantes con nombre.
- `QuotationsSeeder.OrdersExportColumns`: `Fixed("Transportadora (P2)", "Coordinadora")` pasa a
  `Catalog("carrier", "Transportadora (P2)", visible: true)`, en la misma posición (índice 30).
- Sin migración de esquema: el layout es jsonb y la cotización ya tiene el campo.

## Frontend — tarjeta "Dirección de envío"

- Tarjeta nueva en `features/orders/pages/order-detail-page.tsx`, debajo de las tarjetas de
  resumen. No va dentro de `OrderSummaryCards`, que comparte la pantalla de editar.
- Recogida (`detail.quotation.isStorePickup`): aviso resaltado — fondo de acento, ícono de tienda
  y `Recoger en tienda` en negrita.
- Envío normal: la dirección de envío resuelta con `quotePartyView` / `resolveQuoteAddresses`, que
  ya cubren "enviar a la dirección del cliente"; se ve igual que en el detalle de la cotización.
- Sin cambio de contrato: `isStorePickup` y las partes ya vienen en `OrderDetailResponse.Quotation`.

## Pruebas

Backend:

- `OrdersExportProcessorTests`: pedido con recogida → `Recoger en tienda`; sin recogida →
  `Coordinadora`.
- `OrdersExportColumnCatalogTests` y `GetOrdersExportLayoutHandlerTests` (posiciones del catálogo).
- `QuotationsSeedTests`: la columna 30 del layout sembrado es la de catálogo `carrier`.
- `OrderExportApiTests.TheSeededLayoutProducesTheErpImportSheet`: el pedido actual sigue en
  `Coordinadora`; uno con recogida sale `Recoger en tienda`.

Frontend:

- `order-detail-page.test.tsx`: recogida muestra `Recoger en tienda` resaltado y ninguna
  dirección; envío normal muestra la dirección de envío.

## Despliegue

Los dos lados son independientes: el frontend no depende de nada nuevo del backend. En prod,
**después** de desplegar el backend, borrar la fila de `quotations.orders_export_layouts` del
tenant y reiniciar el pod. Si se borra antes, la semilla la recrea con el `Coordinadora` fijo.

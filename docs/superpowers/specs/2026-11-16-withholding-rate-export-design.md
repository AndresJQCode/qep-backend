# Tarifa de retención en el Excel de pedidos — diseño corto

Fecha: 2026-11-16. Alcance: sólo `qep-backend` (el frontend recibe el catálogo de columnas del backend y no
conoce las llaves).

## Intención

Un tenant importa el Excel de pedidos en su sistema de facturación (Siigo). El import usa la **tarifa** de la
retención para buscar el impuesto en Siigo, y Siigo calcula el monto. Hoy «Retefuente» y «ReteICA»
(`withholding_retefuente`, `withholding_reteica`) traen el monto en pesos, así que el import no encuentra el
impuesto.

## Lo que pide el sistema de facturación

- **Retefuente:** la tarifa en porcentaje, sin símbolo (`2.5`).
- **ReteICA:** la tarifa **por mil** (‰), el mismo valor que Siigo muestra como `percentage` del impuesto
  (Bogotá: `11.04`).
- Sólo el número, sin separador de miles ni `$`. Por pedido, repetido igual en cada fila del pedido.
- Sin esa retención en el pedido: vacío o `0`.

## Enfoques considerados

1. **Dos columnas nuevas en el catálogo** — elegido. Ocultas por defecto; el tenant de Siigo las prende en su
   layout con los encabezados que su import espera y oculta las de monto. La configuración por tenant ya existe
   (el layout), ningún ERP que lea el monto se rompe, y es el precedente de «IVA» / «Tasa IVA».
2. Interruptor de formato (monto | tarifa) por columna del layout: cambia el esquema del layout guardado y obliga
   a tocar el frontend, para el mismo resultado.
3. Cambiar el significado de las columnas de monto: no es configurable y rompe en silencio a quien las lee.

## Diseño

- Catálogo (`OrdersExportColumnCatalog`), al final, `DefaultVisible: false`:
  - `withholding_retefuente_rate`, encabezado «Tarifa Retefuente», ancho 16.
  - `withholding_reteica_rate`, encabezado «Tarifa ReteICA», ancho 16.
  Los encabezados por defecto no chocan con los de la hoja sembrada ni con «Retefuente»/«ReteICA», para que la
  regla de choque no las esconda (README § Columnas del Excel de pedidos).
- Valor (`OrdersExportProcessor`), sobre `order.AppliedWithholdings` del tipo (`TaxCountries.RetefuenteType`,
  `TaxCountries.ReteIcaType`):
  - Retefuente: `Percentage` tal cual (se guarda en %, `numeric(6,3)`).
  - ReteICA: `Percentage × 10` (se guarda en %, 1.104 → 11.04 ‰).
  - Es el `Percentage` efectivo (el que el asesor pudo editar), no `CatalogPercentage`: es el que dio el monto.
  - Sin retención aplicada de ese tipo: `0`, número (como las columnas de monto).
  - Celda numérica (`ExportCell.OfNumber`): el separador decimal lo pone Excel; la hoja del ERP no lee el formato
    regional del tenant (D-R8).

## Límite conocido (decisión del owner, 2026-11-16)

Si un pedido tiene **varias retenciones aplicadas del mismo tipo**, la columna exporta la tarifa de la **primera
por `Position`** (llamada del builder: es determinista y no inventa una tarifa que no exista en Siigo). El
owner confirma que el caso no se da hoy. A futuro la retención debe discriminarse por producto; eso es otro
spec.

## Fuera de alcance

- Cambiar `withholding_retefuente`, `withholding_reteica` o `retention_amount`.
- Retención por línea o por producto.
- Tocar el frontend.

## Pruebas

Unitarias del procesador/catálogo: Retefuente 2.5 → `2.5`; ReteICA 1.104 → `11.04`; sin retención → `0`; dos del
mismo tipo → la de menor `Position`; las columnas nuevas nacen ocultas y al final del catálogo.

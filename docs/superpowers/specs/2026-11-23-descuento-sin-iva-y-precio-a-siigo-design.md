# Descuento sin IVA por línea y forma del precio hacia Siigo

Fecha: 2026-11-23. Repo: `qep-backend`. Estado: pendiente de aprobación del owner.

## Intención

En el pedido POB888 (tenant `…0003`, local) QEP mostró «Descuentos aplicados −20.819.270» y Siigo imprimió
17.495.184,88. Es el mismo descuento en dos bases: 17.495.184,88 × 1,19 = 20.819.270,01. El subtotal que QEP muestra
al lado (31.674.731,09) es sin IVA y ya viene descontado, así que la pantalla mezcla bases. El owner confirmó que el
descuento se aplica sobre el producto sin IVA.

Dos cambios:

1. **El descuento de cada línea de cotización y pedido pasa a ser sin IVA**, igual que `Subtotal`.
2. **Por conexión de Siigo se elige qué se manda como precio:** el precio base con el descuento aparte (hoy) o el
   precio ya descontado sin descuento.

## Metas

- `DiscountAmount` de `QuotationItem` y `OrderItem` es una base sin IVA; el encabezado (`DocumentTotals`) lo suma tal
  cual y coincide con el «Descuentos» de Siigo.
- Ningún total cambia: `Subtotal`, `TaxAmount`, `Total` y el neto a cobrar salen idénticos.
- Una opción por conexión de Siigo, con el comportamiento actual como valor por defecto.

## No metas

- El POS no cambia: su descuento sigue con IVA incluido (el cliente paga con IVA y el ticket dice «Incluye X de descuento»).
- No se tocan `DiscountPercentage`, `UnitPrice` ni `DiscountedUnitPrice`.
- La pantalla de ajustes de Siigo en `qep-frontend` es un spec propio en ese repo; acá sólo se entrega el contrato.

## Anti-metas

- Que el total de una factura o de un pedido cambie un centavo.
- Que el PDF de cotización imprima un total de línea distinto al de hoy.
- Que Siigo reciba pagos distintos (`computedOrderTotal` debe seguir igual en ambos modos).

## Enfoques considerados

| | Qué | Por qué sí/no |
|---|---|---|
| A | Cambiar la fórmula compartida `VatIncludedLine` | Cambia el POS sin que nadie lo pidiera |
| **B (elegido)** | `VatIncludedLine` calcula además `DiscountAmountWithoutTax`; cotización y pedido guardan ese en `DiscountAmount`; el POS sigue con el suyo | POS intacto; una sola fórmula para los dos números |
| C | Guardar igual y exponer un campo derivado en el DTO | `DiscountAmount` queda ambiguo, que fue la confusión original |

Elegido por el owner: **B**.

## Diseño

### 1. Descuento sin IVA por línea

- `VatIncludedLineAmounts` suma `DiscountAmountWithoutTax`. `DiscountAmount` (con IVA) se conserva en el record para el POS.
- Fórmula (decisión mía, a confirmar en RED): bruto con IVA = `Round(cant × listaCobrada)`; IVA del bruto = `Round(bruto × t / (100 + t))`;
  bruto sin IVA = bruto − IVA del bruto; `DiscountAmountWithoutTax = bruto sin IVA − Subtotal`. Con tasa 0 coincide con el
  valor con IVA. Se deriva así, y no dividiendo el descuento, para que `bruto sin IVA − descuento = Subtotal` cierre al centavo.
- `QuotationItem` y `OrderItem` guardan `DiscountAmountWithoutTax` en `DiscountAmount`. `DocumentTotals` no cambia: suma la columna.
- **Lectores a corregir en el mismo cambio:**
  - `QuotationPdfDocumentMapper.LineFor` (línea 176) hace `Round(cant × UnitPrice) − DiscountAmount`: pasa a calcularse sin
    depender del descuento (el total de la línea ya existe como `Subtotal + TaxAmount`).
  - `OrdersExportProcessor` (línea 323) exporta `DiscountAmount` por línea al ERP: cambia de significado; revisar la
    columna y avisar al owner (puede afectar la importación).
  - `CopilotDocumentTools`, `StoreOrdersAdapter`, `IStoreOrderGateway`, `QuotePublicCart`: pasan el valor del encabezado; sólo se
    revisa que ninguno lo reste de un total con IVA.
  - Frontend (`order-items-card`, `created-order-summary`, `quote-totals-summary`): sólo lo dibujan; sin cambio de código, pero
    el rótulo «Descuentos aplicados» queda coherente con el «Subtotal» sin IVA.
- **Filas existentes:** decisión mía, a confirmar: **no se reescriben** al desplegar. Un pedido o cotización viejo conserva su
  valor con IVA hasta que se vuelva a calcular (editar líneas). Se anota en el spec de estado. Alternativa: una migración de
  datos que recalcule `discount_amount` de líneas y encabezados con la fórmula de arriba. Cuesta una migración en
  `quotations.quotation_items`, `quotations.order_items`, `quotations.quotations` y `quotations.orders`, y es irreversible en
  la práctica.

### 2. Forma del precio hacia Siigo

- `siigo.settings` suma `price_mode` (texto, `NOT NULL`, default `'BaseWithDiscount'`). Valores: `BaseWithDiscount` (hoy:
  `price = Subtotal / (cant × (1 − desc%))`, `discount = desc%`) y `DiscountedPrice` (`price = Subtotal / cant`, `discount = 0`).
- `SiigoSettings` (dominio), `SiigoDbContext`, `SiigoSettingsDto`, `UpdateSiigoDocument` y `SiigoSettingsSupport` lo llevan igual
  que `freight_separate_invoice`. Un valor desconocido responde 422 con el código de dominio.
- `SiigoInvoiceBuilder` ramifica en `(price, discount)` (líneas 390-396). El `subtotal` recalculado de la línea, `computedOrderTotal`
  y el reparto de pagos no cambian de fórmula: en `DiscountedPrice` el `price` ya trae el descuento.
- El regalo (`price: 0`, `tax_base`) no entra en la rama: sigue igual en los dos modos.
- La migración es aditiva (columna con default): no necesita expand/contract.
- `PUT` de ajustes: la regla de `docs`/`rules/siigo.md` suma una línea sobre `price_mode`.

## Pruebas (TDD, RED primero)

1. `VatIncludedLine`: con IVA 19 %, `bruto sin IVA − DiscountAmountWithoutTax = Subtotal`; con tasa 0 coincide con el viejo.
2. Pedido POB888: la suma de descuentos por línea cierra con el «Descuentos» de la factura (17.495.184,88 ± redondeo de línea).
3. `DocumentTotals`: `Subtotal`, `TaxAmount`, `Total`, retenciones y `NetTotal` iguales antes y después.
4. PDF de cotización: el total de cada línea no cambia.
5. POS: `DiscountAmount` con IVA, intacto.
6. `SiigoInvoiceBuilder`: en `DiscountedPrice`, `discount = 0`, `price × cant` suma el `Subtotal` de la línea y `computedOrderTotal` es igual al de `BaseWithDiscount`.
7. Ajustes: crear y actualizar con cada valor, default para filas existentes y 422 con un valor inválido.

Integración filtrada por nombre mientras se itera; suite de los módulos tocados (Quotations, Siigo, Pos) sólo antes de mergear.

## Pendiente del owner

- Filas existentes: dejarlas como están (propuesta) o migrar el dato.
- Si `OrdersExportProcessor` debe seguir exportando el descuento con IVA al ERP.
- Nombre de los valores de `price_mode` y su rótulo en pantalla (el spec del frontend lo cierra).

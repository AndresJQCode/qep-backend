# Regalos en el pedido y en la factura de Siigo — diseño

**Fecha:** 2026-11-21 · **Estado:** pendiente de aprobación · **Repo:** `qep-backend` (el botón vive en `qep-frontend`, con su propio spec).

## Intención

Que cualquier producto del catálogo se pueda **agregar a un pedido como regalo**: el cliente no lo paga, pero el pedido lo descuenta
del inventario, el costo cuenta en los reportes y la factura de Siigo lo lleva como obsequio. Ejemplo del owner: un producto de 1.000
con IVA, el cliente compra 10 y se le regala 1 → la factura que paga el cliente es de **10.000**.

## Metas, no metas

- **Meta:** botón «Agregar regalo» en el pedido editable; la línea de regalo viaja a Siigo como obsequio y el total no cambia.
- **Meta:** el regalo puede ser **la misma variante** que el cliente compra (el caso del ejemplo).
- **No meta:** regalos en cotizaciones, POS o Shopify (el regalo nace en el pedido; ver «Decisiones mías»).
- **No meta:** que el cliente pague el IVA del regalo. La empresa lo asume **siempre** (decisión del owner, 2026-11-21).
- **Antimeta:** que un descuento del 100 % puesto por otra razón salga como obsequio en Siigo.

## Lo que dice Siigo (investigado 2026-11-21)

Fuente: [API · Crear Factura, «Facturar productos de obsequio»](https://developers.siigo.com/docs/siigoapi/invoice/1-create-invoice/) y la
[guía de obsequios](https://ilimitada.portaldeclientes.siigo.com/realizar-factura-electronica-manejo-de-obsequios/).

- Un ítem con `price: 0` exige `tax_base` (> 0, precio base para el cálculo del IVA) y `taxpayer` (`"Customer"` o `"Company"`).
- Con `"Company"` el IVA no se suma al total del comprobante y se contabiliza en la cuenta contrapartida configurada en Siigo. Con
  `"Customer"` el cliente lo paga. **Ninguna página muestra un total con números**: «con `Company` el total no sube» es la lectura de
  la documentación, no un hecho probado.
- El IVA del regalo se reporta a la DIAN aunque nadie lo pague.
- Para POS Siigo dice que no se incluyan ítems en cero. Esto es factura de venta, no POS.
- Sin empresa de pruebas: la primera factura con regalo sale contra la cuenta real, con `send_to_dian` apagado (ver `.claude/rules/siigo.md`).

## Estado actual relevante

- `OrderItem` ya acepta descuento 0–100 y conserva `UnitPrice`; con 100 % da `Subtotal = 0` e `IVA = 0`.
- `SiigoInvoiceBuilder.Resolve` deriva el precio de `Subtotal / (cantidad × factor)`; con `factor = 0` devuelve `price = 0, discount = 0`, sin
  `tax_base` ni `taxpayer`. Por eso un regalo hoy lo rechazaría Siigo.
- **La línea se identifica por variante.** `Order.AddItemCore` rechaza la misma variante dos veces (`order.item.duplicate_product`),
  y `OrderItemEdits` concilia por `VariantId` (un `ToDictionary` que lanza con dos líneas de la misma variante). La base **no** tiene
  índice único por variante (`IX_order_items_variant` es normal): la unicidad es sólo del dominio. Un regalo de la misma variante que se
  vende choca con las dos reglas de código.
- `OrderPricingRecalculation` reescribe el descuento de **todas** las líneas (escalas, piso global) y cuenta todas las cantidades para el
  mínimo de compra; `Order.ReplaceExternalLines` (sincronización de Shopify) borra las líneas cuya variante no llega. Un regalo tiene que
  sobrevivir a las dos.

## Enfoques considerados

- **A. Marcador `IsGift` en la línea (elegido por el owner, 2026-11-21).** Una línea normal más, con el descuento fijado en 100 %.
- B. Sin marcador (descuento 100 % = regalo): frágil, no distingue el regalo en pantalla ni reportes. Descartado.
- C. Lista de regalos aparte del pedido: obliga a rehacer inventario, costos y factura sobre una entidad nueva. Descartado.

## Diseño (enfoque A)

**Dominio (`Quotations.Domain`).**
- `OrderItem.IsGift` (bool, por defecto `false`). Una línea de regalo se crea con descuento 100 % y no admite otro descuento ni precio
  distinto del de lista; `Subtotal` e `IVA` quedan en 0 y `UnitPrice` guarda el precio real (con IVA incluido, como el resto).
- La clave de la línea pasa de `VariantId` a **`(VariantId, IsGift)`**: puede haber una línea pagada y una de regalo de la misma
  variante, nunca dos de la misma clase. `AddItemCore` se ajusta a la clave nueva; `OrderItemEdits` (la lista deseada del `PUT`) trabaja
  **sólo con líneas pagadas**: los regalos tienen su propio alta y baja, y ni las escalas, ni el mínimo de compra, ni la sincronización
  externa los tocan.
- Un regalo sólo se agrega mientras el pedido sea editable (misma regla de `AddItem`); la cantidad se edita, el descuento no.

**Aplicación y API.**
- Casos de uso propios: `AddOrderGift` (variante + cantidad) y `RemoveOrderGift` (id de la línea). Cambiar la cantidad es quitar y volver
  a agregar (decisión mía, YAGNI). Mismo permiso que editar el pedido (`OrderManage`) y la misma doble capa de tenant.
- El detalle del pedido y la lista de líneas devuelven `isGift`. Motivo (BFF): la pantalla dibuja el regalo distinto y no puede
  deducirlo de un descuento del 100 %.

**Contrato de facturación externa y Siigo.**
- `ExternalInvoicingLine` y `SiigoInvoiceLineInput` ganan `IsGift` y `ListUnitPrice` (precio real con IVA incluido).
- `SiigoInvoiceBuilder`: una línea de regalo sale con `price: 0`, `discount: 0`, `tax_base` = precio real **sin IVA**
  (`ListUnitPrice × 100 / (100 + IVA)`), `taxpayer: "Company"` y el IVA del producto en `taxes`. Su aporte a `computedOrderTotal` es 0
  (subtotal 0, IVA 0), de modo que `qepOrderTotal` y los pagos siguen cuadrando sin tocar la tolerancia.
- Una variante sin vínculo a Siigo sigue siendo el faltante `variant_unlinked`; un regalo sin IVA configurado, el faltante de siempre.

**Pruebas (TDD, RED primero).** Dominio: regalo de la misma variante que una línea pagada, descuento fijo, editable. Builder: JSON del regalo
(`price`, `tax_base`, `taxpayer`, `taxes`), total del pedido de 10.000 en el ejemplo, regalo con IVA 0. Arquitectura: sin cambios de capas.

## Decisiones mías (corrígelas si no)

1. El regalo existe sólo en **pedidos**. Cotizaciones, POS y Shopify no lo traen; un pedido que nace de una cotización puede recibir regalos
   después, ya en el pedido.
2. `tax_base` va **sin IVA**. Es lectura de «precio base para el cálculo del IVA»; la primera factura real lo confirma.
3. El regalo descuenta inventario y congela costo como cualquier línea. Los reportes de venta lo muestran con ingreso 0 y costo real.
4. Una factura de flete aparte no lleva regalos.

## Pendiente de verificar, no de decidir

- Con una factura real (`send_to_dian` apagado): que `taxpayer: "Company"` deje el total en 10.000 y que `tax_base` sin IVA sea lo que Siigo espera.
- Que el contador confirme el tratamiento del IVA asumido por la empresa y la cuenta contrapartida en Siigo.
- La migración es sólo la columna `is_gift` (sin índices nuevos).

## Fuera del backend

El botón «Agregar regalo», cómo se dibuja la línea y los textos van en el spec de `qep-frontend`. El orden de despliegue: backend primero
(campo `isGift` opcional), luego frontend.

# Redondeo de precios por tenant y por moneda — diseño

Fecha: 2026-10-27. Estado: borrador para aprobación del owner.

## Por qué

Origen Botánico tiene un producto con precio base de USD 13,19 y una escala del 15 %. Su lista de
precios dice **11,22**; QEP muestra **11,21**. La diferencia tiene dos causas:

1. **Dónde se redondea.** En USD (y en toda moneda distinta de COP) `VatIncludedLine.Compute`
   redondea el **monto del descuento de la línea**, no el unitario descontado: con 10 unidades,
   131,90 × 15 % = 19,785 → 19,79 y la línea da 112,11. Si el documento muestra un unitario de 11,21,
   el cliente multiplica 11,21 × 10 = 112,10 y no le cuadra. Unitario × cantidad tiene que dar la
   línea.
2. **Hacia dónde se redondea.** 13,19 × 0,85 = 11,2115. Al más cercano da 11,21; el tenant redondea
   **hacia arriba** y obtiene 11,22.

Cada tenant decide cómo redondea su lista de precios, así que la regla tiene que ser configurable.

## Qué se construye

### 1. Una sola fórmula de línea: el unitario primero (decisión del owner)

Para **todas** las monedas y **todos** los tenants, configuren algo o no:

```
lista       = Redondear(precio de lista, regla de la moneda)
unitario    = Redondear(lista × (1 − descuento/100), regla de la moneda)
línea       = Round2(cantidad × unitario)          // lo que se cobra, IVA adentro
descuento   = Round2(cantidad × lista) − línea
IVA         = Round2(línea × tasa / (100 + tasa))  // sólo COP discrimina IVA, igual que hoy
base        = línea − IVA
```

Es la forma que COP ya usa hoy (`VatIncludedLine.Compute`, rama `WholePesoCurrency`), con los
decimales y el modo sacados de la configuración en vez de fijos. La rama «redondear el descuento de
la línea» desaparece.

Ejemplo de aceptación: USD, 2 decimales, hacia arriba, 13,19 con 15 % → unitario **11,22**; 10
unidades → línea **112,20**, descuento 19,70.

El owner aceptó que esto mueva centavos a tenants que no configuraron nada (hoy sólo Origen
Botánico está en uso, en desarrollo).

### 2. La regla de redondeo, por tenant y por moneda

Una regla es `(decimales, modo)`:

- **Modo**: `Nearest` (al más cercano, la mitad se aleja de cero: `MidpointRounding.AwayFromZero`,
  como hoy), `Up` (hacia arriba, techo) o `Down` (hacia abajo, piso). Los precios nunca son
  negativos; aun así se implementa como `ToPositiveInfinity` / `ToNegativeInfinity`.
- **Decimales**: entero entre 0 y los decimales de la moneda en el catálogo (`Currencies.cs`: COP 0,
  USD 2, EUR 2). *Mi decisión:* no se permiten más decimales que los que la moneda muestra, porque
  la pantalla, el PDF y el Excel muestran con los del catálogo y un tercer decimal cobrado no se
  vería en ningún lado.

**Valor por defecto** (tenant sin configuración para esa moneda): `(decimales del catálogo,
Nearest)`. Para COP es exactamente lo de hoy (0, al más cercano).

Configuración de Origen Botánico esperada: COP `(0, Nearest)`, USD `(2, Up)`.

**Quién la cambia:** el admin del tenant (decisión del owner), desde la pantalla de configuración
de cotizaciones (`tenant-settings/components/quotation-settings-section.tsx`), con el mismo permiso
que hoy edita esa configuración (`TenancyPermissions.SettingsUpdate`; lectura con `SettingsRead`).
Se muestra una fila por moneda del catálogo. El cambio se audita como el resto de la configuración
de cotizaciones.

**Dónde se guarda — mi decisión, a confirmar por el builder:** la usan Quotations **y** Pos, y Pos
no ve Quotations. Por eso la regla vive en **Tenancy** (como el formato numérico regional,
`tenants.number_format`), expuesta por un puerto de `Modules.Tenancy.Application` que consumen los
dos. La pantalla puede seguir siendo la de cotizaciones; el endpoint es de Tenancy. Si el builder
encuentra que Pos ya resuelve configuración de Quotations por un adaptador en Bootstrapper, puede
guardarla en `tenant_quotation_settings` siguiendo ese camino; lo que no se negocia es que
cotización, pedido y POS lean **la misma** regla.

### 3. Qué se recalcula cuando el admin cambia la regla (decisión del owner)

- Los documentos editables se actualizan con la regla nueva **la próxima vez que alguien los
  edita** (agregar o quitar una línea, cambiar cantidad, cambiar el piso global, cambiar moneda):
  cotizaciones en `Draft` o `Sent` y pedidos `Pending`. Abrirlos **no** los recalcula; ningún `GET`
  escribe.
- Lo que no se edita no cambia: una cotización enviada que nadie toca, una `Converted`, `Voided` o
  `Expired`, un pedido `Approved`, `Cancelled` o `Invoiced`, una venta del POS cerrada.
- No hay recálculo masivo al guardar la configuración.

### 4. Cada documento guarda con qué regla se calculó

`DiscountedUnitPrice`, `DiscountedUnitPriceWithoutTax` y `UnitPriceWithoutTax` no se persisten: se
derivan al leer (`QuotationItem.cs:122-152`, `OrderItem.cs:78-83`). Si se derivaran con la regla
**actual** del tenant, una cotización enviada mostraría otro unitario después del cambio, sin
cuadrar con el PDF que el cliente recibió.

Por eso la cotización, el pedido y la venta del POS guardan la regla con que se calcularon sus
líneas (decimales y modo de **su** moneda), y el unitario descontado derivado la usa.

*Mi decisión:* la regla se aplica **sólo al precio con IVA incluido** (lista y unitario
descontado). Los valores sin IVA (`DiscountedUnitPriceWithoutTax`, `UnitPriceWithoutTax`) siguen a 2
decimales al más cercano, como hoy: son los que lee el Excel del ERP ("Valor Unit") y Siigo
multiplica por `(1 + IVA)`; redondearlos con la regla movería la facturación aun con la regla por
defecto. Se actualiza cuando el
documento se recalcula (§3). Una fila existente sin regla guardada cuenta como el valor por defecto
de su moneda (mismo criterio que `QuotationPdf.LegacyNumberFormat`).

Al convertir una cotización en pedido, el pedido **hereda la regla guardada de la cotización**
(*mi decisión*, por lo que dijo el owner: lo que el cliente recibió no cambia). `Order.FromQuotation`
rehace las líneas con `OrderItem.Create`; con la regla de la cotización da los mismos centavos que el
PDF aprobado. Si después se edita el pedido pendiente, se recalcula con la regla vigente (§3).

**Pedidos externos (Shopify) no usan la regla del tenant** (decisión del owner). Un pedido creado
por `IExternalOrderWriter` (`Order.CreateExternal`) se calcula y se guarda con el valor por defecto
de su moneda `(decimales del catálogo, Nearest)`, siempre, aunque el admin configure otra cosa y
aunque después se edite: lo que Shopify cobró no lo decide la configuración de QEP. Una prueba
amarra que cambiar la regla del tenant no mueve un pedido de Shopify.

**POS** (*mi decisión*): la regla se lee en cada vista previa y en cada venta, no se congela por
sesión de caja; la venta guarda la que usó. Si el admin la cambia en medio de una sesión, el
`ExpectedUnitPrice` del cliente deja de coincidir y la venta responde con el mismo conflicto de
precio cambiado que hoy da un cambio de catálogo; el carrito se refresca con la vista previa.

### 5. Todos los lugares que hoy repiten la regla

Tienen que quedar usando la regla del documento, no un 2 o un 0 fijos:

| Dónde | Qué hace hoy |
| --- | --- |
| `BuildingBlocks.Domain/Pricing/VatIncludedLine.cs` | Fórmula; `"COP"` literal y `RoundToPeso`. Pasa a recibir la regla (un tipo de valor de BuildingBlocks), no la moneda |
| `QuotationItem.Apply` / `OrderItem.Apply` | Llaman a la fórmula; propiedades derivadas con `Round` de 2 |
| `FreightAmounts.cs:26` | El flete es `Compute(1, monto, 0, tasa, moneda)`: queda con la regla de la moneda |
| `PosSaleLine.cs`, `PreviewPosSale.cs`, `PosSale.cs` | Líneas del POS y su vista previa |
| `QuotationPdfDocumentMapper.cs:141-150` | Recalcula la línea con su propio `Round` de 2 |
| `OrdersExportProcessor.cs:284,300` | Excel del ERP: "Valor Unit" sale de las derivadas; sigue saliendo de ahí |
| Frontend `pos/utils/estimated-line-total.ts` | Copia la fórmula para el total estimado del carrito |
| Frontend `lib/format-currency.ts` `roundToCurrencyPrecision` | Usa `Math.round` (no aleja la mitad de cero) y los decimales del catálogo |
| Frontend `catalog/utils/product-pricing.ts` | Precio de cada escala en el catálogo, a 2 decimales |

Los sumatorios del encabezado (`DocumentTotals`, totales del POS) siguen sumando a 2 decimales: no
redondean precios, suman importes ya redondeados. Tampoco cambia `Order.RecalculatePaymentStatus`
(`Order.cs:853`, decimales del catálogo, al más cercano) ni `roundToCurrencyPrecision` del frontend
en su uso para pagos (*mi decisión*): comparan lo pagado contra un total, y un modo `Up` ahí haría
ver como parcial un pedido pagado al centavo.

## Lo que no se toca

- `ItemizesTaxIn`: sólo COP discrimina IVA. Esta feature no cambia eso.
- La hoja del ERP no lee el formato regional (D-R8 del spec 2026-10-23); sus valores cambian sólo
  porque cambian los importes de las líneas.
- El cálculo de escalas (`QuotationScaleGroupPricing`) decide **qué** porcentaje aplica; esta
  feature sólo cambia cómo se redondea el precio resultante.

## Riesgo conocido: Siigo en COP

La facturación masiva de Ritual manda a Siigo `round("Valor Unit" × (1 + IVA)) × cantidad`. Con COP
en `(0, Nearest)` —el valor por defecto— QEP y Siigo siguen cuadrando como hoy. Si un admin pone COP
en `Up` o `Down`, pueden diferir en un peso por unidad. El owner decidió que la regla aplica a todas
las monedas; *mi decisión*: la pantalla muestra un aviso en la fila de COP cuando el modo no es
`Nearest`, sin bloquearlo.

## Pruebas que la amarran

- Unitarias de la fórmula: el ejemplo de aceptación (13,19, 15 %, `(2, Up)` → 11,22 y 112,20 con 10
  unidades); `Down`; `Nearest` con mitad exacta; COP `(0, Nearest)` idéntico a hoy (las pruebas
  actuales de `VatIncludedLineTests` en COP no cambian de valor); el descuento nunca negativo.
- Paridad: cotización, pedido y POS dan el mismo unitario y la misma línea para la misma entrada.
- Documento: cambiar la regla no cambia una cotización enviada que nadie edita (ni su unitario
  derivado); editarla la recalcula y actualiza su regla guardada.
- Migración: filas existentes sin regla guardada leen el valor por defecto.
- Las pruebas de integración que hoy esperan importes USD con la fórmula vieja van a cambiar de
  valor: se actualizan a la fórmula nueva, una por una, no se borran.

## Lo que queda al builder

- Nombres de tipos, columnas, endpoint y forma del DTO (BFF: la respuesta trae **todas** las monedas
  del catálogo, cada una con su regla efectiva, incluso las que usan el valor por defecto).
- Cómo llega la regla a `VatIncludedLine` sin que BuildingBlocks conozca el catálogo de monedas.
- El reparto en slices: backend y frontend son repos distintos y van en slices propios.

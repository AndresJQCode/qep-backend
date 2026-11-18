# Servicios en cotizaciones y pedidos — diseño

Fecha: 2026-11-18. Repo: `qep-backend`. El frontend lleva su propio spec (pendiente, ver §9).

## 1. Intención

Hay tenants que venden servicios (instalación, visita técnica, hora de soporte, mantenimiento) y no sólo productos
físicos, y una misma cotización o pedido puede mezclar los dos. Hoy un servicio se puede cargar como producto y
cotizar, pero el sistema lo trata como mercancía: el PDF no dice en qué unidad se vende («2» en vez de «2 horas»), el
pedido pide envío y preparación aunque no haya nada que despachar, y la retención en la fuente se calcula con una sola
tasa sobre todo el subtotal, cuando en Colombia bienes y servicios tienen tarifa y base mínima distintas.

Decisiones del owner (conversación del 2026-11-18):

- No se crea un módulo nuevo: se ajusta Catalog. Cotizaciones, pedidos, POS, tienda y Shopify ya referencian
  `VariantId` de Catalog en cada línea.
- Un documento puede tener productos y servicios a la vez.
- Un pedido de sólo servicios se salta el envío y la preparación; «preparado» se reusa y en pantalla se llama
  «Prestado» (queda la constancia de quién y cuándo, `PreparedBy`/`PreparedAt`).
- Los tenants de servicios sí facturan documentos mixtos con retención: la retención por tipo de línea entra en esta
  entrega.
- Enfoque de retenciones elegido: **A** (§4). El owner delegó el resto de las decisiones abiertas «según softwares
  profesionales»; las que tomé yo van marcadas **[decisión del diseño]** con su referencia.

## 2. Objetivos, no objetivos, anti-objetivos

**Objetivos**

1. Un producto del catálogo es `Physical` o `Service`, y tiene una unidad de venta.
2. Cada línea de cotización y de pedido congela tipo y unidad al agregarse.
3. La pantalla sabe, sin deducirlo, si un documento necesita envío (`requiresShipping`).
4. Cada tasa de retención declara si aplica a bienes, servicios o a todo, y su base es el subtotal de las líneas de
   ese tipo.
5. El PDF de la cotización muestra la unidad de cada línea.

**No objetivos (fase 2 o de otro spec)**

- Checkout de la tienda pública, POS y Shopify: siguen como hoy. Las líneas de Shopify nacen `Physical`/`Unit`; las de
  la tienda toman el tipo del catálogo porque comparten el código del pedido manual (§3.4).
- Cobro recurrente de servicios: ya es `ServiceContracts` (`billing`).
- Columnas nuevas en el Excel de pedidos (ERP) o en el de cotizaciones.
- Inventario: el WMS (ramas `feature/wms-b`/`wms-d`, sin mergear) decide qué hace con una variante `Service` (§8).
- Agendar el servicio (fecha, técnico asignado), avance parcial o «cantidad prestada» por línea.

**Anti-objetivos (sería un fracaso aunque funcione)**

- Que un documento existente cambie de total, retención o PDF al desplegar. Todo lo nuevo nace con el valor que
  reproduce el comportamiento de hoy (`Physical`, `Unit`, `All`).
- Que el tipo de una línea cambie porque alguien editó el producto después: es foto, como el nombre y el precio.
- Que el backend exija a la pantalla conocer las reglas de envío para dibujar el pedido (BFF: el flag viaja hecho).

## 3. Catálogo

### 3.1 Tipo de producto

`Product.Kind`, enum `ProductKind { Physical, Service }`, viaja por nombre. Columna `catalog.products.kind text not
null default 'Physical'`. Vive en el producto, no en la variante **[decisión del diseño]**: Odoo (`type`), Siigo y
Alegra («Producto»/«Servicio») lo ponen en el ítem; una variante de talla o color de un servicio no cambia su
naturaleza. Shopify lo pone por variante (`requires_shipping`), pero eso responde a su modelo de despacho, que acá no
aplica.

- Se elige al crear y se puede cambiar al editar. El cambio afecta sólo líneas nuevas (§3.4). Cuando el WMS exista,
  la regla de «no cambiar a `Service` con saldo» es de ese spec.
- Un producto `Service` no lleva empaques: `PackagingUnits` no vacío con `Kind = Service` es `422
  catalog.product.packaging_not_allowed_for_service` (con `errors`, clave `PackagingUnits`) **[decisión del diseño]**:
  el empaque es de mercancía (Odoo tampoco ofrece empaques en un servicio). Pasar a `Service` un producto con
  empaques exige vaciarlos en el mismo `PUT`, y sus escalas con restricción de empaque responden igual que hoy cuando
  se vacían los empaques de un producto físico (no hay regla nueva ni arreglo de datos: hoy no existe ningún
  producto `Service`).
- Un producto con `ExternalSource = shopify` no puede ser `Service` desde la consola: lo administra la tienda
  (`catalog.product.managed_externally`, el mismo 422 de hoy, sumando `Kind` a lo que rechaza).

### 3.2 Unidad de venta

`Product.SalesUnit`, enum `SalesUnit`, viaja por nombre, columna `catalog.products.sales_unit text not null default
'Unit'`. Lista cerrada, no texto libre **[decisión del diseño]**: la factura electrónica de la DIAN exige el código de
unidad de UN/ECE Rec. 20, y Siigo, Alegra y Odoo ofrecen una lista fija por esa razón. Cada valor lleva su código, que
hoy no se usa en QEP pero evita una migración de datos el día que el ERP lo pida:

| Valor | Código Rec. 20 | Etiqueta (frontend) |
| --- | --- | --- |
| `Unit` | `94` | unidad |
| `Service` | `E48` | servicio |
| `Hour` | `HUR` | hora |
| `Day` | `DAY` | día |
| `Week` | `WEE` | semana |
| `Month` | `MON` | mes |
| `Year` | `ANN` | año |
| `Kilogram` | `KGM` | kg |
| `Meter` | `MTR` | m |
| `SquareMeter` | `MTK` | m² |
| `Liter` | `LTR` | l |

- No depende de `Kind`, a propósito y sin 422 cruzado: un producto físico puede venderse por metro o por hora
  (alquiler de equipo), y un servicio por kilo (lavandería) **[decisión del diseño]**; Odoo tampoco cruza tipo y
  unidad.
- El código vive en un solo lugar (`SalesUnits.CodeOf`, en `BuildingBlocks.Domain`, porque Catalog y Quotations no se
  ven).
- La etiqueta la pone el frontend (los enums viajan por nombre; el diccionario lo tiene la pantalla). El PDF, que se
  arma en el backend, necesita la suya: va en `quotation.typ` como diccionario cerrado en español (§6).

### 3.3 Contrato HTTP de Catalog

`kind` y `salesUnit` entran al cuerpo de alta y edición del producto (opcionales en el JSON: ausentes en el alta = `Physical` y
`Unit`; ausentes en la edición = se conserva lo guardado, como `visibleInStore`, para que un cliente viejo no
vuelva físico un servicio; viajan como texto y un valor desconocido es 422 `validation.failed`) y a la respuesta del detalle y del listado. Filtro `?kind=` en el listado
**[decisión del diseño]**: es lo primero que pide una pantalla de catálogo mixto.

### 3.4 Foto en la línea

`QuotationItem` y `OrderItem` ganan `Kind` y `SalesUnit`, columnas `not null` con default `'Physical'` y `'Unit'`
(migración aditiva; las filas viejas quedan con el valor que reproduce lo de hoy).

**Se fijan al crear la línea, no con la foto del nombre** (corrección del 2026-11-18 al leer el código): la foto del
nombre de una cotización no existe en borrador —se toma al enviar o convertir (`CaptureProductSnapshots`)—, y un
borrador sin tipo daría `requiresShipping` y bases de retención falsas. Todo `AddItem` ya lee la variante por
`IQuotationProductPricingLookup`; su `QuotationVariantPricingRef` trae `Kind` y `SalesUnit`, y también
`QuotationVariantRef` (`IQuotationProductLookup`, que usan el pedido manual y el externo). Una línea cuya variante no
cambia conserva los suyos en todo guardado (ningún camino de edición relee la variante de una línea existente); una
línea con variante nueva es una línea nueva.

Caminos: alta, batch, guardado de la lista completa y alta de cotización (`QuotationItemEdits`, `QuotationDraftBuilder`,
también el borrador del agente de IA); líneas y ediciones de pedido (`AddOrderItems`, `OrderItemEdits`); pedido manual
y de la tienda (`OrderDraftBuilder.PriceLinesAsync`, compartido: la tienda toma los valores del catálogo, que es lo
honesto, aunque su checkout no cambie); conversión (`Order.FromQuotation` copia los de la línea de la cotización).
Los pedidos de Shopify (`ExternalOrderService`) nacen `Physical`/`Unit`.

## 4. Retenciones por tipo de línea (enfoque A)

### 4.1 Enfoques considerados

- **A. La tasa declara a qué aplica (elegido).** `TaxRate.AppliesTo`: `All | Goods | Services`. La base de cada
  tasa es el subtotal de las líneas de su tipo. Es como lo hace Siigo (la retención se liquida por concepto: compras,
  servicios, honorarios, cada uno con su base) y lo que pide el formulario de la DIAN.
- **B. Retención por línea.** Cada línea con sus tasas, como Odoo con la localización colombiana (la retención es un
  impuesto más del producto). Más flexible, pero el asesor de QEP elige retenciones por documento o por cuenta de
  cobro, y habría que rehacer selector, pantallas y foto. Descartado.
- **C. Deducir el tipo del nombre de la tasa.** Frágil: se rompe con un renombre. Descartado.

### 4.2 Modelo

- `TaxRate.AppliesTo`, enum `WithholdingScope { All, Goods, Services }` por nombre, columna `catalog.tax_rates.applies_to
  text not null default 'All'`. Sólo vale distinto de `All` en tipos con `TaxBehavior.Withholds`; un IVA con otro
  valor es `422 catalog.tax_rate.applies_to_invalid`.
- **`AppliesTo` se fija al crear la tasa y no se edita**, igual que `Type` hoy (`TaxRate.Update` sólo recibe nombre,
  porcentaje y base mínima). Un `PUT` que traiga otro valor es `422 catalog.tax_rate.applies_to_immutable`; para
  cambiarlo se crea otra tasa y se desactiva la anterior, que es como Siigo y Alegra tratan un concepto de retención
  **[decisión del diseño]**. Esto cierra el caso de una selección guardada en una empresa o un cliente que quedaría
  repetida o reteniendo dos veces si el alcance cambiara después: como no cambia, Companies y Customers pueden leer el
  alcance por id de tasa al validar (sin foto propia) y el resultado es estable.
- `WithholdingRate` (la foto en el documento) gana `AppliesTo`, columna `not null default 'All'` en las tablas de
  retenciones de cotización y pedido. La foto no se mueve al editar la tasa en el catálogo, igual que hoy.
- `LineAmountsView` gana `Kind`. `DocumentTotals.Compute` calcula, por tasa, `base = Σ Subtotal` de las líneas cuyo
  tipo cae en `AppliesTo` (`All` = todas, `Goods` = `Physical`, `Services` = `Service`). El umbral
  (`ThresholdReason`) y el monto (`WithholdingAmount`) se evalúan contra esa base. El envío sigue fuera de toda base.
  `AppliedWithholding.Base` ya existe y pasa a ser la base propia de la tasa.
- Una tasa cuya base queda en 0 porque el documento no tiene líneas de su tipo no aplica, con motivo nuevo
  `WithholdingReason.NoLinesOfScope` en el veredicto (`Explain`), para que la pantalla diga «sin servicios en el
  documento» y no «bajo la base mínima» **[decisión del diseño]**.
  En el veredicto se evalúa después de los motivos que hoy descartan la tasa antes del cálculo (consumidor final,
  bloqueada, otro país o ciudad) y antes de los de umbral: `NoLinesOfScope` → `NoTaxUnitValue` → `BelowMinimumBase`
  / `Applied`. Así el motivo que se ve es el más útil: si no hay servicios, el umbral no importa.
- La base por alcance la usan **todos** los que hoy reciben `productsSubtotal`: `DocumentTotals.Compute`,
  `WithholdingCandidates` (candidatos y `Explain`) y `DocumentWithholding.Explain`. Una sola función calcula la base
  de una tasa a partir de las líneas, para que la vista previa, los candidatos y el total guardado nunca difieran.
- Con todas las tasas en `All`, el cálculo da exactamente lo de hoy (anti-objetivo de §2). Lo amarra una prueba.

### 4.3 «Una tasa por tipo» pasa a «una por tipo y alcance»

Hoy la regla está en cuatro lugares, y los cuatro cambian igual:

- `WithholdingSelectorRules` (`Quotations.Domain`, línea 37), código `{prefijo}.withholding_type_repeated`.
- `WithholdingSelection` (`Quotations.Application`, línea 138).
- `CompanyWithholdingRules` (`Companies.Application`, línea 44, agrupa por `(Type, CityId)`).
- `CustomerWriteRules` (`Customers.Application`, línea 233, agrupa por `(Country, Type)`).

Regla nueva: dos tasas del mismo tipo (y misma ciudad, donde hoy se compara) conviven sólo si una es `Goods` y la otra
`Services`.

**Los tipos municipales (ReteICA) quedan con una tasa por tipo y municipio, cualquiera sea su alcance** (corrección
del 2026-11-18 al implementar): la edición del porcentaje municipal (`SetWithholdingPercentage(type, …)`), la
re-resolución al cambiar de ciudad (`MunicipalOffer` por tipo) y `WithholdingSelection.ReResolved` identifican la fila
por tipo, y dos filas del mismo tipo las volverían ambiguas. La separación bienes/servicios vale para los nacionales
(Retefuente), que es el caso de los tenants de servicios. Una ReteICA puede tener alcance y su base se calcula por
alcance, pero la empresa y el documento llevan una sola por municipio. Separar la ReteICA por actividad es fase 2: pide
identificar la fila municipal por tasa y no por tipo. La regla vive en `WithholdingScopes.Repeated`. `All` con cualquier otra del mismo tipo sigue siendo repetida: sus bases se solaparían y se retendría dos
veces. El código de error no cambia.

Companies y Customers no ven el catálogo: si su foto o su lectura de tasas no trae `AppliesTo`, entra por el mismo
puerto por el que hoy llega `Type` (el builder lo ubica).

### 4.4 Lo que se ve

El desglose de retenciones del detalle y del PDF muestra la base de cada tasa (ya viaja `Base`). El DTO de la tasa
suma `appliesTo`.

## 5. Pedido: envío y «Prestado»

- **`requiresShipping`** en el DTO del detalle y del listado de pedidos y de cotizaciones: `true` si alguna línea es
  `Physical`. Un documento sin líneas da `true` (es el estado de hoy, y el formulario vacío muestra envío). El motivo
  va en el comentario del DTO: la pantalla oculta envío y cambia «Preparar» por «Marcar como prestado» sin conocer los
  tipos de línea. Se calcula en la consulta (`EXISTS` de una línea `Physical`), sin columna guardada: ningún reporte
  ni Excel lo necesita **[decisión del diseño]**.
- **Envío:** el backend no lo prohíbe en un documento sin líneas físicas **[decisión del diseño]**. La regla que hoy
  podría estorbar (ciudad de envío al marcar pagado o facturar) ya sólo aplica si el pedido se le envía al cliente
  (`BilledCustomerFieldsRequired`, `ShippingDetails != None`), así que un pedido con `ShippingDetails.None` pasa.
  Prohibir el envío obligaría a borrarlo en silencio al quitar la última línea física, y Odoo tampoco lo prohíbe.
- **No hay default nuevo que construir** (corrección del 2026-11-18 al leer el código): un pedido manual sin envío en
  el cuerpo ya nace con `ShippingDetails.None` (`OrderDraftBuilder.ResolveShippingAsync`), y la conversión copia el
  envío de la cotización (`Order.FromQuotation`), que en una de sólo servicios es el que dejó el asesor, `None` si la
  pantalla oculta la sección. El backend nunca pone ni borra envío por el tipo de las líneas, en ningún sentido: si un
  pedido de servicios gana una línea física, queda con `None` y `requiresShipping = true`, y el asesor elige el envío
  en la sección que reaparece; si uno mixto pierde su última línea física, conserva el envío y el flete que tenía.
  La cotización, igual con su flete **[decisión del diseño]**.
- **«Prestado»:** sin estado nuevo. `Prepare`, `UndoPreparation`, `IsEditable`, `ArchiveIfSettled` y la anulación del
  comprador no cambian: el pedido de servicios se marca «prestado» con el mismo endpoint y queda congelado igual, que
  es lo que hacen Odoo («entregado») y Alegra con un servicio prestado. Las entradas del historial y la auditoría
  siguen diciendo `Prepared`; el texto lo pone el frontend con `requiresShipping`.

## 6. PDF de la cotización

`quotation.typ` muestra la unidad junto a la cantidad («2 horas», «1 servicio», «3 unidades»). La unidad llega como
texto plano en los datos de la línea (nunca markup: regla de no `eval` de `quotation.typ`), con el plural resuelto en
el backend en un diccionario cerrado de `SalesUnit` en español. `Unit` en una cotización donde todas las líneas son
`Unit` se omite **[decisión del diseño]**, para que el PDF de un tenant que no usa unidades salga idéntico al de hoy
(anti-objetivo). Kind y unidad entran al fingerprint del PDF cacheado sólo a través de las líneas, que ya cuentan.

## 7. Restricciones

- **Expand/contract:** todo es aditivo, con default en la base. Un pod viejo que escribe una línea o una tasa omite
  las columnas y quedan `Physical`/`Unit`/`All`. Durante el rollout, un pod viejo calcula la retención de un
  documento con una tasa `Goods`/`Services` sobre todo el subtotal: nadie configura tasas por alcance hasta que no
  quede un pod viejo (mismo criterio que la prórroga de Soporte).
- **Arquitectura:** Quotations no referencia Catalog; tipo y unidad entran por `IQuotationProductLookup`. El código
  Rec. 20 vive en `BuildingBlocks.Domain`.
- **Cuerpos crudos en pruebas:** los campos nuevos son opcionales en el JSON, así que no hay barrido de cuerpos
  (gotcha de «endurecer una regla de dominio»).
- **`FromSql` crudos:** el builder busca los que leen `catalog.products`, `catalog.tax_rates`, `quotation_items`,
  `order_items` y las tablas de retenciones (`CatalogVariantUsageChecker`, `MarginReportSource`,
  `OrdersReportSource`…). Agregar columnas no los rompe salvo los `FromSql` de entidad completa
  (`QuotationSettingsStore` es el precedente).

## 8. Relación con el WMS

El WMS en curso tiene un perfil logístico por variante con `base_uom` y `display_uom`. Son unidades de **inventario**
(cómo se cuenta en bodega); `SalesUnit` es la unidad **comercial** (cómo se cotiza). Pueden coincidir y no se unifican
en esta entrega **[decisión del diseño]**. Pendiente para el spec del WMS: una variante de un producto `Service` no
lleva perfil logístico ni saldo, y su línea de pedido no entra al picking.

## 9. Contrato para el frontend (spec aparte en `qep-frontend`)

- Producto: selector de tipo y de unidad, empaques ocultos en `Service`, filtro por tipo.
- Tasa de retención: selector `appliesTo` en los tipos que retienen.
- Cotización y pedido: unidad en cada línea; con `requiresShipping = false`, la sección de envío oculta y el botón
  «Marcar como prestado» / estado «Prestado» en lugar de «Preparar» / «Preparado»; veredicto `NoLinesOfScope`.
- Despliegue: backend primero (todo opcional y con default); el frontend después.

## 10. Lo que queda al builder

- Ubicar todos los caminos de alta y edición de líneas (§3.4) y los adaptadores de tasas de Companies y Customers
  (§4.3).
- Nombres de migraciones y su orden por módulo (Catalog, Quotations, y Companies/Customers si guardan foto de tasas).
- Pruebas: unitarias de `DocumentTotals` (bases por alcance, umbral por base, `All` igual a hoy, `NoLinesOfScope`), de
  las cuatro reglas de repetición, de `Product` (empaques en servicio) y de la foto; integración de Catalog (alta,
  edición, filtro), de cotización y pedido (foto, `requiresShipping`, envío sin cambios, retención mixta) y del PDF.

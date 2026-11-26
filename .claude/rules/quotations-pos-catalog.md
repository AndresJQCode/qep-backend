---
paths:
  - "src/Modules/Quotations/**"
  - "tests/Modules/Quotations/**"
  - "src/Modules/Pos/**"
  - "tests/Modules/Pos/**"
  - "src/Modules/Catalog/**"
  - "tests/Modules/Catalog/**"
  - "src/Modules/Reporting/**"
  - "tests/Modules/Reporting/**"
  - "src/BuildingBlocks/**/SnapshotText.cs"
  - "src/BuildingBlocks/**/RichText/**"
  - "src/Modules/Notifications/**/*Format*"
  - "src/Modules/Tenancy/**/*Format*"
---

# Cotizaciones, pedidos, POS y catálogo

Reglas de `qep-backend` que no se deducen leyendo el código. Se cargan solas al leer o editar un
archivo que coincide con `paths` (arriba). Las reglas generales están en [`CLAUDE.md`](../../CLAUDE.md).

- **`Pos` necesita la capacidad `pos` y no viene con el signup.** `GrantDefaultModulesOnSignup`
  concede siete módulos (los seis base y `shopify`) sin `pos`, así que en un tenant nuevo todo `/pos/*`
  da 403 aunque el rol tenga los permisos: se prende con la fila de `tenancy.tenant_modules` (spec de entitlements,
  «Operación»). En pruebas, `PosApiHarness.EnablePosAsync`. Y `cashier` ya no es una clave reservada: es un rol
  sembrado en cada tenant, que el tenant puede editar o borrar (spec 2026-11-25, roles por tenant).
- **El payload de los eventos de Quotations es contrato con Pipeline** (spec 2026-10-13 §4.5).
  `QuotationLifecycleEventPublisher` escribe `quotations.quotation.sent.v1`, `…converted.v1` y
  `quotations.order.cancelled.v1` con campos camelCase, y `QuotationEventPayloads` los lee por nombre: renombrar
  un campo compila, pasa las pruebas de Quotations y deja los mensajes marcados como procesados sin efecto. Se
  cambia en los dos lados, y en `QuotationEventPayloadsTests`. Pipeline lee las conversaciones de Messaging por
  `MessagingDbContext` desde Bootstrapper (`PipelineConversationDirectory`), como Reporting lee Quotations: no
  por un puerto de Messaging.
- **Un pedido tiene datos propios y puede no tener cotización** (spec 2026-10-15). `Order` lleva cliente,
  asesor, cuenta de cobro, partes, líneas y totales; `QuotationId` es opcional y `Source` dice de dónde nació
  (`Quotation`, `Manual`, `Shopify`). Convertir **copia**: la cotización `Converted` queda congelada y editar un
  pedido editable edita `order_items`, nunca `quotation_items`. Un pedido sin sesión se crea sólo por
  `IExternalOrderWriter` (acepta cliente incompleto); el manual exige cliente completo con CUC. Los eventos
  `quotations.order.created.v1` y `order.cancelled.v1` llevan `source` y `quotationId` nullable: Pipeline los lee
  por nombre y guarda la foto en `pipeline.deal_orders` (un `order.created` **con** cotización no hace nada: ya
  actuó el `converted`). `Observaciones` del Excel sale de `Order.Notes`, que es un solo campo al
  convertir (el `notes` del cuerpo la reemplaza; `null` hereda las de la cotización; `""` la deja vacía; nunca se
  concatenan). Un `INSERT` a mano en `quotations.orders` (siembras, pruebas) tiene que llenar las columnas propias
  y copiar `order_items`/`order_parties`: son `NOT NULL` desde `AddOrderOwnData`.
- **La entrega de un pedido se edita; «el pedido no edita sus partes» (H5) quedó revertido sólo para la entrega** (spec
  2026-11-24 §10, owner). `PUT /orders/{id}` y su preview aceptan `delivery` (ausente conserva), en todo origen y mientras el
  pedido no esté facturado ni preparado (`deliveryEditable` = `IsEditable`). `Order.SetDelivery` va **antes** de la
  facturación, las retenciones, el envío y el recálculo del pago: el envío se resuelve contra la recogida nueva, y pasar a
  recoger limpia el envío y la contraentrega dentro del agregado, así que la fila y la auditoría `shipping_changed` se
  calculan contra el envío de antes de la entrega. Con `delivery` en el cuerpo, los handlers llaman
  `EnsureDeliveryEditable` antes de la guarda `not_editable`, o el motivo exacto no sale nunca.
- **Un `FromSql` crudo debe listar todas las columnas mapeadas de la entidad.** El `SELECT … FOR UPDATE` de
  `QuotationSettingsStore` no se puede componer (EF lo envolvería en una subconsulta), así que EF no completa
  las columnas que falten: al agregar `freight_tax_rate_id` a `tenant_quotation_settings`, guardar el mínimo de
  compra dio 500 hasta que se sumó al `SELECT`. Al extender una tabla, busca los `FromSql` que la leen.
- **Al agregar una columna al catálogo del Excel de pedidos, revisa los encabezados de las fijas de la semilla.**
  Una columna nueva cuyo encabezado choca con una visible del layout guardado nace oculta en silencio, sin
  error: la regla de choque está en
  [README § Columnas del Excel de pedidos](../../README.md#columnas-del-excel-de-pedidos-por-tenant-homologación).
- **Un `bool` con default `true` en la base lleva `ValueGeneratedNever`** (`OrdersExportLayout.IncludePhoneDialCode`, spec
  2026-11-25). Con sólo `HasDefaultValue(true)`, EF puede omitir un `false` al insertar y la base guarda `true`, sin error. Y una
  columna de teléfono nueva del Excel de pedidos pasa por `phoneText` en `OrdersExportProcessor.RowsFor`, o ignora el
  interruptor del indicativo.
- **La variante por defecto lleva el id del producto, y el relleno de `variant_id` depende de eso** (spec 2026-10-20 §8).
  `Product.Create` le da a la primera variante el id del producto —también al crear con opciones—, y `AddProductVariants`
  hizo lo mismo con cada producto que existía. Por eso `AddVariantToLines` (Quotations) y `AddVariantToSaleLines` (Pos)
  rellenan con `variant_id = product_id` sin consultar `catalog`, que es otro esquema y otro `DbContext`. No cambies
  `Create` para que estrene un id: las pruebas que mandan el id del producto como `variantId` dejan de valer en silencio.
  Al revés tampoco sirve: `variant_id = product_id` **no** dice que la variante sea la por defecto (un producto con
  opciones también tiene una con ese id); eso lo dice `OptionIds` vacío, y fuera de Catalog no se ve.
- **Un `INSERT` a mano en `quotation_items`, `order_items` o `sale_lines` necesita `variant_id`.** Es `NOT NULL` sin default
  desde `AddVariantToLines` y `AddVariantToSaleLines`, y va junto con el `product_id` **de esa variante**. Del lado de
  Catalog, `catalog.products` ya no tiene `code` y `catalog.product_prices` se une por `variant_id`: un SQL que lea el
  catálogo pasa por `catalog.product_variants` (precedente: `ExportLoadSeeder`). Las pruebas de migración que siembran
  **antes** de esas migraciones no lo llevan: la migración lo rellena.
- **Un cuerpo crudo con `productId` ya no sirve en las líneas.** Las de cotización, pedido y POS (`AddQuotationItemRequest`,
  `BatchQuotationItemAdditionRequest`, `QuotationEditItemRequest`, `OrderItemAdditionRequest`, `OrderEditItemRequest`,
  `PosSaleLineRequest`, `PosPreviewLineRequest`) llevan `variantId`, y la copia de escalas `sourceVariantId` /
  `targetVariantIds`. Un objeto anónimo con `productId` se deserializa con `VariantId = Guid.Empty` y responde `422
validation.failed` por el `VariantId` vacío, con la clave según la ruta: `VariantId` en `POST /quotations/{id}/items`,
  `ToAdd[i].VariantId` en el batch y en `POST /orders/{id}/items`, `Items[i].VariantId` en las listas completas
  (alta, guardado y preview de cotización y pedido) y `Lines[i].VariantId` en el POS. Una variante repetida en una lista
  completa sale en `errors.Items`, sin posición. El síntoma es una prueba que esperaba `Created`, no un error de nombre
  de campo. Los records tipados se arreglan solos; el barrido es por `new { productId` y por JSON literal.
- **Borrar una variante es explícito: `deletedVariantIds`. Omitirla del `PUT` responde `catalog.variant.missing_in_request`.**
  No lo «simplifiques» a «la que no viene se borra» (era lo que pedía el plan, y el spec §6 lo corrigió): la tabla de
  variantes llega a 2.048 filas y el formulario la pagina, así que una fila ausente puede ser una página que no se
  mandó. La listada se borra si ninguna línea la usa y se desactiva si alguna sí; borrarla se lleva su historial de
  precios por `CASCADE`.
- **La regla de recorte de la foto del nombre vive en `BuildingBlocks.Domain/SnapshotText.cs`.** El nombre visible de una
  variante pasa de los 200 de `product_name` (producto de 200 más hasta tres valores de 60), y
  `QuotationProductSnapshot` y `PosSaleLine` lo recortan con `SnapshotText.Fit` (200 con «…», sin partir un par
  sustituto). No copies la regla en un módulo —la copia de Pos ya se retiró una vez—: Quotations y Pos no se ven entre
  sí, y dos copias divergen sin que nada se ponga rojo. Ensanchar la columna es una migración en cada módulo más su
  constante (`QuotationProductSnapshot.NameMaxLength`, `PosSaleLine.ProductNameMaxLength`).
- **El respaldo colombiano del formato regional vive en tres copias que se cambian juntas** (spec 2026-10-23, D-R5), porque los
  módulos no se ven entre sí: `RegionalNumberFormat`/`RegionalDateFormat` (`Modules.Tenancy.Application`),
  `EmailRegionalFormat.Colombian` (`Modules.Notifications.Application`, que no ve Tenancy) y `QuotationPdf.LegacyNumberFormat`
  (dominio de Quotations: el formato con el que se supone generada una fila de `quotation_pdfs` con `number_format` nulo). Cambiar
  `TenantNumberFormats.CommaDecimal` sin la del dominio compila y regenera en silencio todos los PDF guardados de los tenants
  colombianos en el siguiente export. Las amarran `EmailRegionalFormatTests.ColombianMatchesTheTenancyFallback` y
  `QuotationPdfTests.ALegacyPdfWithoutFormatCountsAsCommaDecimal`. Y el historial de cambios es texto congelado: un tenant que
  cambia de formato ve las filas viejas con el anterior, y eso no es un bug.
- **`OrdersExportProcessor` no lee el formato del tenant, a propósito** (D-R8): la hoja del pedido la importa el ERP.
  «Unificarlo» con el Excel de cotizaciones compila, pasa sus pruebas unitarias y rompe la importación en silencio. La amarra
  `OrderExportApiTests.TheErpSheetIgnoresTheTenantRegionalFormat`.
- **La regla de redondeo de un documento vive en dos columnas nullable, y `null` es `PriceRounding.LegacyFor`** (spec 2026-10-27).
  `LegacyFor` tiene el literal `"COP"` porque BuildingBlocks no ve el catálogo; `TheLegacyRuleMatchesTheDefaultForEveryCatalogueCurrency`
  la amarra a `PriceRoundingDefaults.For`. Una moneda nueva con otros decimales pone roja esa prueba: decide ahí si los documentos viejos
  de esa moneda se leen con la regla vieja. La regla sólo baja al documento en `QuotationPricingRecalculation`/`OrderPricingRecalculation`
  (`UsePriceRounding`); un camino nuevo que rehaga líneas sin pasar por ahí calcula con la regla guardada, no con la vigente. Los pedidos
  externos nunca toman la del tenant.
- **`quotation.typ` nunca hace `eval` de datos del tenant** (spec 2026-11-16, contenido del PDF). Las secciones configurables llegan
  como árbol de bloques (`RichTextHtmlReader`) y la plantilla inserta cada texto como cadena; armar markup de Typst con texto del
  tenant, o pasarle el HTML, abre inyección en el PDF. Lo amarran `TheTemplateNeverEvaluatesData` y
  `TheTemplateCompilesWithTypstSyntaxInSectionText`. Y un dato nuevo que el PDF imprima desde la configuración del tenant entra
  en el fingerprint de `QuotationPdfSectionsBuilder.Fingerprint`, o el PDF cacheado de la cotización no se regenera al cambiarlo.
- **El saneador de HTML vive en `BuildingBlocks.Infrastructure/RichText` y lo comparten Soporte y Quotations.** La lista de
  etiquetas y esquemas existe una sola vez: ensancharla cambia lo que aceptan los documentos legales del portal **y** el PDF de
  cotizaciones, y el lector de bloques tiene que saber pintar toda etiqueta nueva o la plantilla la pierde en silencio.
- **Un precio de venta se lee por `IPriceListPricing`, nunca de `variant.Prices`** (spec 2026-11-22, listas de precios por canal).
  `variant.Prices` es el precio **base**; el que cobra cada canal sale de la lista asignada (`catalog.channel_price_lists`) con
  `PriceList.PriceFor`. `PosProductLookup`, `StoreCatalogReader` y `QuotationProductPricingLookup` arman un `PriceBook` por
  request. Un adaptador nuevo que lea `CatalogVariantReads.PricesOf` o `variant.PriceIn` para vender compila, pasa sus pruebas sin
  lista y cobra el base en un tenant con lista. El canal de Quotations sale del origen del pedido (`QuotationPriceChannels.For`):
  la tienda y la edición de un pedido `Store` usan `Store`; el resto, `Quotations`. Y un precio fijo sólo reemplaza (D-2): sin
  precio base en la moneda no hay precio, por eso los filtros SQL «tiene precio en la moneda» siguen valiendo.

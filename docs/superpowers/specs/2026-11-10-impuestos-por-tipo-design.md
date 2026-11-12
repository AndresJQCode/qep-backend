# Impuestos por tipo y país (IVA, Retefuente, ReteICA) — diseño backend

Fecha: 2026-11-10. Trabajo que cruza repos: este spec cubre `qep-backend` y **manda el contrato**; la mitad del frontend es
`qep-frontend/docs/superpowers/specs/2026-11-10-impuestos-por-tipo-design.md`. Salen juntos, backend primero (§12).

Estado: diseño, no construido. No existe código, migración ni prueba de nada de lo que sigue. Las rutas y líneas citadas son
de `develop` al 2026-11-10 (`1255abe3` en el backend, `ccfde16e` en el frontend).

**Enmienda 2026-11-12 (estados del pedido en ejes, `develop` `0e85094d`).** `OrderStatus.Pending` ya no existe. Donde este spec
dice «pedido `Pending`» se lee «pedido editable» (`Order.IsEditable`: abierto, sin preparar y sin facturar; spec 2026-11-10 de
estados del pedido §3.6), la misma regla que el resto de la edición del pedido. El rechazo es `order.order.not_editable`, no
`order.order.not_pending`. Lo amarran `OrderWithholdingsTests` (preparado, facturado, archivado y anulado) y
`OrderWithholdingsApiTests.APreparedOrderRejectsWithholdingAndAccountEdits`.

Marcas: **[owner]** decisión del owner (2026-11-10); **[asistente]** decisión de este spec, revisable por el owner; **NUEVO**
= no existe hoy en el código. Todo lo que no lleva NUEVO se verificó en el archivo citado.

## 1. Intención

Hoy la retención en la fuente es una constante: `DocumentTotals.RetentionRate = 0.025m` (`Modules.Quotations.Domain/
DocumentTotals.cs:28`), aplicada en `DocumentTotals.Compute` (`:49`) como `Round(subtotal * 0.025)` cuando `appliesRetention`.
Quién retiene lo dice un booleano del cliente (`Customer.WithRetention`, `Modules.Customers.Domain/Customer.cs:286`) o, con datos
propios de facturación, una pregunta por documento (`Quotation.PartyWithRetention`, `Quotation.cs:359`; `Order.PartyWithRetention`,
`Order.cs:202`). No hay ReteICA, no hay base mínima, y el 2,5 % no se puede cambiar sin desplegar.

El owner quiere que las retenciones sean tasas configurables, igual que el IVA de los productos: cada tasa de impuesto tiene un
**tipo** fijado en código por país (Colombia: `IVA`, `Retefuente`, `ReteICA`), una **base mínima** opcional expresada en la
unidad tributaria del país (Colombia: UVT) y el valor de esa unidad lo carga QCode por país y año en la consola del operador.

## 2. Objetivos

- `TaxRate` gana `Type`, `Country` y `MinimumBase` [owner, D1–D4]; las retenciones admiten 3 decimales [owner, D15].
- Una tabla **en código** por país: tipos, comportamiento de cada tipo (suma o retiene) y unidad tributaria [owner, D1–D2, D11].
- Valor de la unidad tributaria por país y año, administrado sólo por el operador (pestaña nueva «Unidad tributaria» de la
  consola) [owner, D5, D20].
- Clientes con tasas asignadas de tipo `Retefuente` y `ReteICA`; desaparecen `Customer.WithRetention` y
  `Quotation/Order.PartyWithRetention` [owner, D8–D9].
- Cotización con facturación propia: un selector por tipo de retención en el documento [owner, D16].
- Cotización y pedido **congelan** las tasas (y el excedente de IVA del cliente) al crearse, calculan cada retención con su tasa,
  su base mínima y la UVT del año del documento, y la muestran una por línea en el desglose y el PDF [owner D6, D11–D12, D17;
  asistente D24].
- La empresa facturadora es obligatoria para enviar y convertir **cotizaciones** [owner D18]. Un pedido `Pending` puede fijar o
  cambiar su empresa y sus retenciones en la edición [owner D21–D22].
- Migración sin cambio de totales: el 2,5 % de hoy se vuelve la tasa «Retefuente 2,5 %» de cada tenant que lo usa [owner, D10].

## 3. No objetivos

- Un producto vendido desde dos países con IVA distinto: sigue habiendo una sola tasa de IVA por producto [owner, D14].
- Recalcular documentos existentes cuando cambia una tasa, la UVT o la ficha del cliente [owner, D12, D17].
- Retenciones en POS (`Modules.Pos`), que hoy no tiene retención ni la pide (no hay `Retention` en `Modules.Pos.*`).
- Tipos de otros países: la tabla queda lista para crecer, pero en esta entrega sólo existe Colombia [owner, D1].
- Correo o notificación cuando falta la UVT del año siguiente: sólo un aviso en la consola [asistente, D6].
- Cambiar la pregunta del excedente de IVA de la facturación propia (`PartyVatSurplus`) [owner, D16].
- Exigir empresa facturadora a pedidos manuales o externos [owner, D22].

## 4. Anti objetivos

- **Nunca** un tipo, un país o una unidad tributaria como dato editable por el tenant: son código [owner].
- **Nunca** recalcular en el navegador: el contrato entrega cada retención ya calculada (§9). El frontend tiene hoy una copia del
  2,5 % (`qep-frontend/src/features/quotes/utils/quote-preview-totals.ts:129`) que este trabajo elimina (spec del frontend).
- **Nunca** referenciar Catalog desde Quotations o Customers: los puertos van con adaptador en `Bootstrapper` (§5).
- **Nunca** una retención sobre el envío: hoy se calcula sobre el subtotal de productos (`DocumentTotals.cs:14-15`, `:49`) y así
  sigue.
- **Nunca** releer las tasas ni el excedente de IVA del cliente para un documento ya creado (salvo cambio de cliente, D17).
- **Nunca** escribir en la ficha del cliente desde la edición de un pedido (D21).
- **Nunca** hacer fallar el webhook de Shopify por empresa facturadora o retenciones (D22).

## 5. Restricciones verificadas

- **Capas.** Application de cada módulo sólo referencia `Modules.Tenancy.*` entre los módulos de negocio
  (`tests/ArchitectureTests/`). Precedente de puerto hacia Catalog: `IQuotationTaxRateLookup`
  (`Modules.Quotations.Application/IQuotationTaxRateLookup.cs:6`), con adaptador en `Bootstrapper`. Precedente de lógica fiscal
  compartida entre módulos: `VatIncludedLine` en `BuildingBlocks/BuildingBlocks.Domain/Pricing/VatIncludedLine.cs`.
- **El porcentaje es entero.** `TaxRate.Percentage` es `int` de 0 a 100 (`Modules.Catalog.Domain/TaxRate.cs:15-18`, `:49`,
  P-008 «no admite retenciones con fracción», también en `CatalogDtos.cs:369-371`). `CreateTaxRateRequest(string Name, int
  Percentage)` (`CatalogDtos.cs:374`). 2,5 % y 0,966 % no caben: **el owner reabre P-008** (D15, §7.1).
- **El IVA es entero en toda la cadena.** `QuotationItem.TaxPercentage` (`QuotationItem.cs:103`), `OrderItem.TaxPercentage`
  (`OrderItem.cs:69`), `PosSaleLine.TaxPercentage` (`PosSaleLine.cs:50`), `ShopifyProductRef.TaxPercentage`
  (`ShopifyPorts.cs:182`) y `QuotationTaxRateRef.Percentage` (`IQuotationTaxRateLookup.cs:11`) son `int`.
- **La retención sólo existe en COP.** `Quotation.AppliesRetention` exige `QuotationItem.ItemizesTaxIn(Currency)`
  (`Quotation.cs:371-374`; `Order.cs:303-306`), que es `currency == "COP"` (`QuotationItem.cs:305-307`).
- **Consumidor final apaga la retención** (`Quotation.cs:346-351`, `:373`).
- **La empresa facturadora sí tiene país.** `Company.Country` es `string` ISO 3166-1 alfa-2 en mayúsculas, obligatorio
  (`Modules.Companies.Domain/Company.cs:23`, `:27` `ColombiaCountryCode = "CO"`, `:95-96`), validado en
  `CompanyWriteRules.cs:71-77`. Pero **no cruza a Quotations**: `QuotationCompanyRef` no trae país
  (`IQuotationCompanyLookup.cs:20-33`) y la foto `QuotationBillingAccount` guarda `CompanyId`, banco, cuenta y moneda
  (`QuotationBillingAccount.cs:20-29`), sin país.
- **Empresa facturadora en cotizaciones.** `EnsureConvertibleToOrder` lanza `quotation.billing.account_required` sin
  `BillingAccount` (`Quotation.cs:937-940`); `EnsureSendable` (`:847-866`) no la exige.
- **Empresa facturadora en pedidos.** El constructor desde cotización y el privado `Build` la aceptan nula
  (`Order.cs:64`, `:95`, `:539`), igual que `Order.Preview` (`:418`). Pero **las tres fábricas que guardan pedidos la exigen hoy**:
  `CreateManual` (`:389`, `ThrowIfNull` `:398`), `CreateExternal` (`:438`, `:465`) y `CreateFromStore` (`:491`, `:499`). Los
  comentarios de `:405-410` lo dicen («para que el pedido que se guarda no pueda nacer sin cuenta»). `Order` **no tiene** método
  para cambiar `BillingAccount` después de crearse (sólo se asigna en `:95`). Ver §7.7.
- **Tasas detrás del módulo `catalog`.** `catalog.tax_rate.read`/`.manage` llevan `RequiredModules: [ModuleKeys.Catalog]`
  (`Bootstrapper/QepServiceCollectionExtensions.cs:1939-1951`); aceptado por el owner (D19).
- **Orden de migraciones.** `Program.cs` migra Catalog (`:349`), luego Customers (`:356`), luego Quotations (`:366`), en secuencia.
- **Hoy el perfil tributario se relee mientras el documento sigue vivo.** `Quotation.RefreshCustomerTaxProfile`
  (`Quotation.cs:1009-1021`) se llama al leer (`GetQuotation.cs:36`) y en cada edición: `QuotationEditsApplication.cs:45`,
  `AddQuotationItem.cs:53`, `UpdateQuotationItem.cs:55`, `RemoveQuotationItem.cs:44`, `BatchUpdateQuotationItems.cs:80`.
  `Order.RefreshCustomerTaxProfile` (`Order.cs:849-864`) en `PreviewOrderEdits.cs:67` y `SaveOrderEdits.cs:86`. No tiene otros
  llamadores. D17 y D24 lo eliminan (§7.6).
- Errores de dominio → 422 con `code` (`src/Api/ApiExceptionHandler.cs`). TDD obligatorio; integración con Testcontainers, un
  proyecto a la vez.

## 6. Decisiones del owner (2026-11-10)

| # | Decisión |
| --- | --- |
| D1 | `TaxRate` gana `type`. Los tipos son **fijos en código por país**. Colombia → `IVA`, `Retefuente`, `ReteICA`. Otros países vendrán después: tabla de países en código. |
| D2 | Cada país en código declara si tiene unidad tributaria y su nombre. Colombia → `UVT`. |
| D3 | Las tasas se administran por tenant en `/settings/tax-rates`. Cada tasa guarda su `country`; al crear se elige entre los países de las empresas facturadoras del tenant, y el selector de tipo muestra sólo los de ese país. |
| D4 | `minimumBase` opcional: en la unidad tributaria del país si la tiene, en moneda si no; `null` = siempre aplica. Se compara contra el subtotal del **documento**, no por línea. |
| D5 | La UVT la administra QCode (operador) por país y año, en una pestaña nueva de la consola. Los tenants nunca la editan. |
| D6 | Se usa la UVT del año de la fecha del documento. Desde el 24 de diciembre, si un país con unidad tributaria no tiene el valor del año siguiente, la consola muestra un aviso (sin correo [asistente]). Qué valor se usa si falta el año: D20. |
| D7 | Los productos llevan tasas de tipo IVA, como hoy. El selector del producto filtra a IVA. |
| D8 | Los clientes llevan tasas de tipo ReteICA y Retefuente al crear/editar. La base admite varias del mismo tipo por cliente; la UI deja **una por tipo** (y por país). |
| D9 | Se eliminan `withRetention` (cliente) y `billingWithRetention` / `PartyWithRetention` (parte de facturación). El 2,5 % pasa a ser dinámico. |
| D10 | Migración: por tenant con clientes `withRetention = true`, crear la tasa «Retefuente 2,5 %» (tipo `Retefuente`, país Colombia, 2,5 %, sin base mínima) y asignarla a esos clientes. Las tasas existentes sin tipo pasan a `IVA`. Cotizaciones y pedidos ya emitidos no se recalculan. |
| D11 | Las retenciones se calculan sobre el subtotal **sin IVA**, no aplican si la cotización factura a consumidor final, restan de `total` hacia `netTotal`, y el desglose muestra una línea por retención. El IVA suma al total, como hoy. Cada tipo lleva en la tabla su comportamiento (suma o retiene). |
| D12 | País del cálculo = país de la empresa facturadora elegida en la cotización/pedido. Se aplican las tasas de retención de ese país. Cotización y pedido guardan foto de las tasas y montos aplicados; cambiar una tasa o la UVT nunca recalcula documentos. |
| D13 | ReteICA se suele expresar por mil (9,66 ‰); se guarda y se muestra como porcentaje (0,966 %) [asistente, por delegación del owner]. |
| D14 | Fuera de alcance: un producto vendido desde dos países con IVA distinto. |
| D15 | Se reabre P-008: los tipos que retienen (Retefuente, ReteICA) admiten hasta 3 decimales; el IVA sigue entero de 0 a 100. |
| D16 | **Cotización**: facturando con los datos del cliente («Facturar con los datos del cliente» prendido) se aplican las tasas de retención asignadas al cliente. Facturando a otros datos (apagado), la cotización tiene **un selector por tipo de retención** (Retefuente, ReteICA); cada uno elige una de las tasas del tenant de ese tipo (puede haber varias por tipo: «Retefuente compras 2,5 %», «Retefuente servicios 4 %»), arranca vacío y se guarda en el documento. Reemplaza el interruptor «Aplica retención en la fuente» (`billingWithRetention`). La pregunta «Tiene excedente de IVA» queda como está. **Excepción, pedidos de la tienda**: siempre llevan parte de facturación propia, pero toman las tasas **del cliente** al crearse (como hoy toman `customer.WithRetention`, `SubmitOrderRequest.cs:296-309`), no selectores vacíos. [asistente] Si el cliente tiene tasas de varios países, se copian sólo las del país de la empresa facturadora del pedido. |
| D17 | Las tasas se **congelan al crear** el documento (cotización y pedido), en todos los estados. **Es un cambio de comportamiento**: hoy la cotización en Draft/Sent y el pedido en `Pending` releen el perfil del cliente (§5). [asistente] Si se cambia el cliente de una cotización en borrador, las tasas se vuelven a copiar del cliente nuevo. La única otra excepción es D21. |
| D18 | La empresa facturadora es **obligatoria sólo en cotizaciones**. [asistente] Se exige al enviar y al convertir, no al guardar un borrador. |
| D19 | Las tasas siguen detrás del módulo `catalog`: aceptado (sin Catálogo no hay pedidos). |
| D20 | Decisiones del asistente que el owner registra: etiqueta de cada fila de retención = **nombre de la tasa**; la pestaña del operador se llama **«Unidad tributaria»**; UVT aplicada = la del año del documento o, si falta, la del **último año cargado anterior** a ése; el Excel de pedidos lleva **una columna por tipo de retención**; importación/exportación de clientes con **una columna por tipo de retención con el nombre de la tasa** ([asistente], quien construye puede afinarlo). |
| D21 | Mientras un pedido está `Pending`, el asesor puede cambiar sus retenciones en la edición del pedido con un selector por tipo, prellenado con las tasas congeladas. Cambia sólo ese pedido, nunca la ficha del cliente (para que valga en documentos futuros, el asesor la asigna en el cliente). [asistente] Vale para **todo** pedido `Pending`: manual, Shopify, tienda o convertido. Es la excepción explícita a «congelado al crear». |
| D22 | Pedidos manuales y de Shopify/externos pueden quedar sin empresa facturadora → sin país → sin retenciones. El asesor puede fijar la empresa al editar un pedido `Pending`, y desde ahí se ofrecen los selectores. El webhook de Shopify no puede empezar a fallar. |
| D23 | [asistente] Si la empresa facturadora se fija o cambia a otro país, la UI limpia los selectores cuya tasa sea de otro país; el backend responde 422 `*.billing.withholding_tax_rate_invalid` si llega una tasa de otro país al guardar. El filtro por país de los selectores queda confirmado. |
| D24 | [asistente] El excedente de IVA del cliente también se congela al crear, como las tasas: se elimina el refresco al leer y editar (`RefreshCustomerTaxProfile`) y el refetch del frontend atado a él. Verificado: no tiene más llamadores que los de §5. |

## 7. Diseño

### 7.1 La tabla de países (código) — NUEVO

`BuildingBlocks/BuildingBlocks.Domain/Taxes/TaxCountries.cs` (NUEVO) [asistente: `BuildingBlocks.Domain`, junto a
`Pricing/VatIncludedLine.cs`, porque la leen Catalog (validar), Quotations (calcular) y Customers (validar) y ninguno puede
referenciar a otro].

```csharp
public enum TaxBehavior { Adds, Withholds }

public sealed record TaxTypeDefinition(string Type, TaxBehavior Behavior, bool AllowsFraction, bool AllowsMinimumBase);

public sealed record TaxCountryDefinition(
    string Country,            // ISO 3166-1 alfa-2, el mismo formato que Company.Country
    string? TaxUnitName,       // "UVT" o null si el país no tiene unidad tributaria
    string TaxCurrency,        // la moneda en la que el país discrimina impuestos: "COP"
    IReadOnlyList<TaxTypeDefinition> Types);

public static class TaxCountries
{
    public static readonly TaxCountryDefinition Colombia = new("CO", "UVT", "COP",
    [
        new("IVA", TaxBehavior.Adds, AllowsFraction: false, AllowsMinimumBase: false),
        new("Retefuente", TaxBehavior.Withholds, AllowsFraction: true, AllowsMinimumBase: true),
        new("ReteICA", TaxBehavior.Withholds, AllowsFraction: true, AllowsMinimumBase: true),
    ]);

    public static TaxCountryDefinition? Find(string country);
    public static TaxTypeDefinition? FindType(string country, string type);
}
```

- `Type` viaja como texto (`"IVA"`, `"Retefuente"`, `"ReteICA"`), con la misma convención que `Status` en `QuotationDto`
  (`QuotationsDtos.cs:37-39`: ningún DTO expone un enum de dominio). El valor es único **dentro del país** [asistente].
- `TaxCurrency` [asistente] generaliza la regla de hoy (`ItemizesTaxIn`, `QuotationItem.cs:305-307`): una retención sólo aplica si
  la moneda del documento es la moneda tributaria del país de la empresa facturadora. Para Colombia da exactamente lo de hoy.
  También fija en qué moneda se lee `minimumBase` de un país sin unidad tributaria (D4).
- `AllowsFraction`: `false` para IVA, `true` con hasta **3 decimales** para Retefuente y ReteICA [owner D15]. El IVA sigue entero
  porque toda la cadena de líneas es `int` (§5).
- `AllowsMinimumBase: false` para IVA [asistente]: D4 compara contra el subtotal del documento, y el IVA es por línea.

### 7.2 Catalog — `TaxRate`

Cambios en `Modules.Catalog.Domain/TaxRate.cs`:

| Propiedad | Hoy | Después |
| --- | --- | --- |
| `Percentage` | `int`, 0–100 (`:49`) | `decimal`, 0–100; entero si el tipo no admite fracción, hasta 3 decimales si la admite [owner D15] |
| `Type` | — | NUEVO `string`, obligatorio, validado contra `TaxCountries.FindType(Country, Type)` |
| `Country` | — | NUEVO `string` (2), obligatorio |
| `MinimumBase` | — | NUEVO `decimal?`, `>= 0`, `null` = siempre aplica; sólo si el tipo lo admite |

- `Create(id, tenantId, name, percentage, type, country, minimumBase, occurredAt)`.
- `Update(name, percentage, minimumBase, occurredAt)`: **`Type` y `Country` no se editan** [asistente]. El `PUT` los ignora.
- Columnas (`CatalogDbContext.ConfigureTaxRate`, `:355-383`): `percentage` pasa de `integer` a `numeric(6,3)`; NUEVAS `type
  varchar(32) not null`, `country char(2) not null`, `minimum_base numeric(14,2) null`. `IX_tax_rates_tenant_name` (único,
  `:380-382`) no cambia.
- Validar que `Country` sea un país de las empresas del tenant (D3) es de Application: puerto NUEVO
  `ITaxRateCountrySource.ListCompanyCountriesAsync(tenantId)` con adaptador en `Bootstrapper` (empresas activas e inactivas
  [asistente]).
- Al **crear** una tasa con `MinimumBase` en un país con unidad tributaria que **no tiene ningún valor cargado**, 422
  `catalog.tax_rate.tax_unit_value_missing` [asistente].
- Desactivar y borrar: hoy responden `catalog.tax_rate.in_use` si un producto la usa (`DeactivateTaxRate.cs:44-51`,
  `DeleteTaxRate.cs:57`). Se amplía el chequeo a **clientes con la tasa asignada**, mismo código existente, por un puerto NUEVO
  `ITaxRateCustomerUsage` (adaptador en `Bootstrapper`). Que un documento la tenga congelada **no** la bloquea [asistente].
- Productos: `ProductTaxRateResolver` (`ProductTaxRateResolver.cs:42`) rechaza una tasa que no sea `Adds` con NUEVO
  `catalog.product.tax_rate_not_vat` (D7).
- Envío: `SetFreightTaxRate` (`SetFreightTaxRate.cs:39-44`) rechaza una tasa que no sea `Adds` con NUEVO
  `quotations.freight_tax_rate.not_vat` [asistente].
- `QuotationTaxRateRef` (`IQuotationTaxRateLookup.cs:11`) mantiene `int Percentage`: sólo resuelve tasas `Adds`.

### 7.3 Catalog — valor de la unidad tributaria (plataforma) — NUEVO

Entidad NUEVA `TaxUnitValue` en Catalog [asistente: Catalog es dueño de las tasas; Geography es el precedente de un dato de
plataforma administrado por el operador dentro de un módulo de negocio, `GeographyOperatorPermissions.cs:9-16`].

```
catalog.tax_unit_values (NUEVA, sin tenant_id: es de la plataforma)
  country     char(2)        not null
  year        integer        not null
  value       numeric(14,2)  not null   -- > 0, en la moneda tributaria del país (COP)
  updated_at  timestamptz    not null
  updated_by  uuid           not null   -- SubjectId del operador
  PK (country, year)
```

- Sólo países con `TaxUnitName != null` (422 NUEVO `catalog.tax_unit.country_without_unit`).
- `year` entre 2000 y año actual + 1 (422 NUEVO `catalog.tax_unit.year_out_of_range`) [asistente]; `value > 0` (422 NUEVO
  `catalog.tax_unit.value_invalid`).
- Sin borrado [asistente]: corregir es volver a escribir el año. Auditoría `catalog.tax_unit.value_set` [asistente].
- Resolución (D6, D20): `ValueFor(country, documentDate)` = el valor de `year(documentDate)`; si no existe, el del **mayor año
  cargado anterior** a ése. Si no hay ninguno anterior, la tasa se congela con `taxUnitValue = null` y **no aplica** [asistente].
- Fecha del documento: `Quotation.CreatedAt` (`Quotation.cs:136`) y `Order.CreatedAt` (`Order.cs:286`), en la zona horaria del
  tenant [asistente]. Al resolver de nuevo una tasa en la edición de un pedido (D21) se usa la misma fecha del pedido.

### 7.4 Customers — tasas del cliente

- Tabla NUEVA `customers.customer_tax_rates (tenant_id uuid, customer_id uuid, tax_rate_id uuid, PK (customer_id, tax_rate_id))`,
  sin FK hacia `catalog`. La base admite varias del mismo tipo (D8).
- `Customer` gana `WithholdingTaxRateIds` (NUEVO). Se elimina `CustomerCommercialInfo.WithRetention` (`CustomerCommercialInfo.cs:29`)
  y `Customer.WithRetention` (`Customer.cs:286`, `:967`); la columna `with_retention` (`CustomersDbContext.cs:200`) se borra en la
  migración de contrato (§11.5).
- Validación por puerto NUEVO `ICustomerTaxRateLookup` (adaptador en `Bootstrapper`): cada id existe en el tenant, está activo y su
  tipo es `Withholds` → si no, 422 NUEVO `customers.customer.withholding_tax_rate_invalid`. Dos tasas del mismo `(country, type)`
  → 422 NUEVO `customers.customer.withholding_type_repeated` [asistente].
- Fusión de clientes: el sobreviviente conserva sus tasas, como hoy conserva su `WithRetention` (`CustomerEndpoints.cs:296`)
  [asistente].
- Importación y exportación Excel [D20; asistente, quien construye puede afinarlo]: «Con Retencion» (`IExcelCustomerImporter.cs:52`,
  `ImportCustomers.cs:176`, `:413`, `ClosedXmlCustomerImporter.cs:68`, `ClosedXmlCustomerImportTemplateBuilder.cs:134`,
  `ClosedXmlCustomerExportBuilder.cs:122`) se reemplaza por una columna por tipo («Retefuente», «ReteICA») con el **nombre** de la
  tasa; vacía = sin tasa; un nombre inexistente o de otro tipo es error de fila. Un archivo viejo con «Con Retencion» se lee
  ignorando esa columna [asistente].
- Los puertos que hoy exponen `WithRetention` cambian a la lista de tasas: `IQuotationCustomerLookup` (`:77`, adaptador
  `Bootstrapper/QuotationCustomerLookup.cs:103`), `StoreCustomerRef` (`IStoreCustomerDirectory.cs:4`, adaptador
  `StoreCustomerDirectory.cs:183`).

### 7.5 Quotations — de dónde salen las tasas y cómo se calcula

**Fuentes de tasas**:

| Documento | Fuente al crear | Edición posterior |
| --- | --- | --- |
| Cotización, facturando con los datos del cliente | tasas del cliente, de todos sus países [owner D16, D17] | ninguna; se recopian sólo si cambia el cliente [asistente D17] |
| Cotización, facturando a otros datos | selectores vacíos, uno por tipo [owner D16] | los selectores, mientras sea editable |
| Pedido convertido | hereda la foto de la cotización tal cual [asistente] | selectores por tipo mientras `Pending` [owner D21] |
| Pedido manual | tasas del cliente del país de su empresa [asistente; el alta manual no tiene parte de facturación propia distinta en este diseño] | ídem |
| Pedido de la tienda | tasas **del cliente** del país de su empresa, aunque siempre tiene parte propia [owner D16 excepción; asistente: filtro por país] | ídem |
| Pedido de Shopify/externo | tasas del cliente del país de su empresa si tiene; sin empresa, ninguna [owner D22; asistente] | ídem; fijar la empresa habilita los selectores |

Selectores (cotización con facturación propia y edición de pedido): cada uno guarda el id de una tasa del tenant de ese tipo, o
nada. Sólo aceptan tasas activas, que retienen, del tipo del selector y **del país de la empresa facturadora** [asistente D23,
confirmado]. Sin empresa no hay selectores (no hay país). Errores NUEVOS `quotation.billing.withholding_tax_rate_invalid` /
`quotation.billing.withholding_type_repeated` y `order.billing.withholding_tax_rate_invalid` / `order.billing.withholding_type_repeated`
[asistente, mismo prefijo que `order.billing.tax_profile_required`]. Al cambiar la empresa a otro país, una tasa guardada de otro
país deja de aplicar (paso 4 abajo) y el siguiente guardado que la mande recibe `withholding_tax_rate_invalid`; la UI la limpia
antes (D23).

En la cotización, pasar de un modo de facturación al otro no borra la foto del otro modo [asistente].

**Qué aplica (D11, D12).** Para un documento con empresa facturadora `C` (país `P = Company.Country`):

1. Si factura a consumidor final → ninguna retención (como hoy, `Quotation.cs:373`).
2. Si la moneda del documento no es `TaxCountries.Find(P).TaxCurrency` → ninguna (§7.1).
3. Sin empresa facturadora (cotización en borrador, D18; pedido sin empresa, D22) → ninguna.
4. Si no, cada tasa de la fuente vigente con `Country == P`: base = `Subtotal` de productos sin IVA y sin envío
   (`DocumentTotals.cs:42`, `:49`); umbral = `MinimumBase × taxUnitValue` si el país tiene unidad, o `MinimumBase` si no; aplica si
   `MinimumBase == null` o `base >= umbral` [asistente]; monto = `Round(base × percentage / 100)` (`DocumentTotals.cs:53`).

Lo congelado son las **tasas** (nombre, tipo, país, porcentaje, base mínima, valor de UVT) y el **excedente de IVA del cliente**
(D24); los **montos** se recalculan con cada cambio de líneas, empresa o consumidor final, como hoy los totales.

**`DocumentTotals`.** `Compute(lines, freight, appliesVatSurplus, appliesRetention)` (`DocumentTotals.cs:38-51`) cambia el último
parámetro por `IReadOnlyList<WithholdingInput>` (NUEVO) y devuelve además `IReadOnlyList<AppliedWithholding>` (NUEVO).
`RetentionAmount` = suma; `NetTotal = Total − RetentionAmount`. `RetentionRate` (`:28`) se borra.

**Foto (D12, D17).** Tablas NUEVAS `quotations.quotation_withholdings` y `quotations.order_withholdings`:

```
(document_id uuid, source varchar(16),        -- 'Customer' | 'Billing' (cotización); 'Document' (pedido)
 position smallint, tax_rate_id uuid, name varchar(120), type varchar(32), country char(2),
 percentage numeric(6,3), minimum_base numeric(14,2) null, minimum_base_unit varchar(8) null,
 tax_unit_value numeric(14,2) null, base_amount numeric(14,2), amount numeric(14,2),
 PK (document_id, source, position))
```

El pedido tiene una sola fuente vigente (`Document`) [asistente]: al convertir hereda la fuente vigente de la cotización; al
editar (D21) los selectores la reemplazan. `amount = 0` en las que no aplican. Columnas `retention_amount` y `net_total`
(`QuotationsDbContext.cs:144-147`, `:561-562`) siguen y valen la suma.

Puertos NUEVOS (adaptadores en `Bootstrapper`): `IQuotationWithholdingLookup.ForCustomerAsync(tenantId, customerId, country?,
documentDate)` y `.ForRatesAsync(tenantId, taxRateIds, documentDate)`. `QuotationCompanyRef` gana `string Country` (NUEVO en
`IQuotationCompanyLookup.cs:20`).

### 7.6 Congelamiento — cambio de comportamiento (D17, D21, D24)

- Se **eliminan** `Quotation.RefreshCustomerTaxProfile` (`Quotation.cs:995-1021`) y `Order.RefreshCustomerTaxProfile`
  (`Order.cs:849-864`) y sus llamadas: `GetQuotation.cs:27-41`, `QuotationEditsApplication.cs:41-45`, `AddQuotationItem.cs:48-53`,
  `UpdateQuotationItem.cs:50-55`, `RemoveQuotationItem.cs:39-44`, `BatchUpdateQuotationItems.cs:74-80`, `PreviewOrderEdits.cs:67`,
  `SaveOrderEdits.cs:86`. Con eso `GetQuotation` vuelve a ser una lectura pura (hoy puede escribir, `:39`).
- `CustomerVatSurplus` (cotización `:177`, pedido) se copia una vez al crear, como las tasas [asistente D24]. `PartyVatSurplus` sigue
  siendo la respuesta por documento.
- Editar las tasas o el excedente del cliente, editar una tasa o cargar una UVT **no mueve** ningún documento existente.
- Excepciones: cambio de cliente de una cotización en borrador (`Quotation.ChangeClient`, `Quotation.cs:753-771`, que hoy reinicia
  `PartyWithRetention` y toma el perfil del nuevo cliente) recopia tasas y excedente del cliente nuevo y vacía los selectores
  [asistente D17]; y la edición de retenciones de un pedido `Pending` (D21), que nunca escribe en el cliente.

### 7.7 Empresa facturadora (D18, D22)

- **Cotizaciones.** `EnsureSendable` (`Quotation.cs:847-866`) exige `BillingAccount` con el código **existente**
  `quotation.billing.account_required` (hoy sólo en la conversión, `:937-940`). Guardar un borrador sin empresa sigue permitido.
  El comentario de `QuotationBillingAccount.cs:16-18` se corrige. Cotizaciones abiertas sin empresa no se migran: siguen
  editables y no se pueden enviar hasta elegirla.
- **Pedidos.** D22 pide que los manuales y externos puedan quedar sin empresa. **Hoy el código ya exige empresa en las tres
  fábricas que guardan pedidos** (§5: `CreateManual`, `CreateExternal`, `CreateFromStore`, `ThrowIfNull`); sólo `Order.Preview` y
  `Build` la aceptan nula. Este spec **no cambia** esas fábricas [asistente]: relajarlas no es parte de este trabajo y D22 se cumple
  igual (no se agrega ninguna exigencia nueva; el webhook de Shopify no cambia de comportamiento). El caso «pedido sin empresa →
  sin retenciones» queda cubierto para filas antiguas con `BillingAccount` nulo, si existen [pendiente de verificación, §15].
- **Fijar o cambiar la empresa de un pedido `Pending`** (D22): NUEVO `Order.SetBillingAccount(QuotationBillingAccount)` en la
  edición del pedido (`SaveOrderEdits`, `PreviewOrderEdits`), validado contra la empresa como en la cotización
  (`IQuotationCompanyLookup`). **La cuenta no puede cambiar la moneda del pedido**: una cuenta en otra moneda es 422
  `order.billing.currency_change_not_allowed` [owner, 2026-11-10, plan P10]. Cambiar la moneda sin repreciar dejaría los importes
  rotulados en otra moneda, y repreciar un pedido de Shopify cambiaría lo que la plataforma cobró. Esto reemplaza el criterio
  anterior («cambiar la moneda con la cuenta no reprecia», como la cotización en `Quotation.cs:263-275`), que sigue valiendo sólo para
  la cotización. Al cambiar de país, aplica D23.
- **Pendiente de verificación:** si algún tenant no tiene empresas facturadoras (no podría enviar cotizaciones después del
  despliegue).

### 7.8 Lo que se borra o cambia de forma

- `Quotation`: `CustomerWithRetention` (`:172`), `PartyWithRetention` (`:359`), `AppliesRetention` (`:371-374`),
  `RefreshCustomerTaxProfile` → reemplazados por la foto. `AppliesVatSurplus` sigue, sobre el excedente congelado.
- `Order`: `CustomerWithRetention` (`:206`), `PartyWithRetention` (`:202`), `AppliesRetention` (`:303-306`),
  `RefreshCustomerTaxProfile` (`:849-864`).
- `QuotationParties.BillingWithRetention` (`QuotationPartyDetails.cs:246`) → NUEVO `BillingWithholdingTaxRateIds`;
  `OrderInputs.CustomerWithRetention` (`OrderInputs.cs:11`), `StoreOrderDraft`/`IStoreOrders.BillingWithRetention`
  (`IStoreOrders.cs:62`, `SubmitOrderRequest.cs:305`, `StoreOrdersAdapter.cs:129`) → la lista de tasas del cliente (D16 excepción).
- `EnsureBillingRules` (`Order.cs:613-617`) y la conversión (`Quotation.cs:971-976`; `CanBeConvertedToOrder`, `:993`): siguen
  exigiendo **sólo** `PartyVatSurplus`; los selectores vacíos son válidos y no bloquean.
- `QuotationChangeSummary` (`:182`, `:318`, `:345`) compara los selectores en vez de `BillingWithRetention`.
- PDF: `QuotationPdfDocument` (`:56-62`) gana la lista; `quotation.typ:413-431` pasa a una fila por retención aplicada con el
  **nombre** de la tasa [D20].
- Excel de pedidos (`OrdersExportColumnCatalog.cs:103-109`, `OrdersExportProcessor.cs:335`): NUEVAS «Retefuente» y «ReteICA» con
  el monto aplicado de ese tipo (0 si no aplica), al final [D20]. «Retencion» sigue con la suma [asistente].

### 7.9 Consola del operador — permisos NUEVOS

Calcado de Ciudades (`GeographyOperatorPermissions.cs:12-15`, `CityOperatorEndpoints.cs:21`): clase NUEVA
`CatalogOperatorPermissions` en `Modules.Catalog.Application` con `operator.tax_units.read` y `operator.tax_units.manage`.
Núcleo (`RequiredModules: []`), categoría `Operator`, en el rol `admin` como los de ciudades (`QepServiceCollectionExtensions.cs:1696-1697`),
catálogo de permisos (`:2218-2225`) y políticas (`:2732-2736`). El prefijo `operator.` los deja vivos sólo en el tenant operador
(`OperatorPermissionFilter.cs:13`). Segunda capa: `OperatorAuthorization.EnsureAuthorized(..., IOperatorTenant)`, como
`CityOperatorQueries.cs:42`. Pestaña «Unidad tributaria» [D20].

**Aviso (D6).** `nextYearMissing` por país: `true` si hoy es 24 de diciembre o después **y** el país tiene unidad tributaria **y**
no hay valor para el año siguiente. «Hoy» en la zona horaria del tenant operador [asistente].

## 8. Modelo de datos (resumen)

| Esquema.tabla | Cambio |
| --- | --- |
| `catalog.tax_rates` | `percentage` integer → `numeric(6,3)`; NUEVAS `type`, `country`, `minimum_base` |
| `catalog.tax_unit_values` | NUEVA, de plataforma |
| `customers.customer_tax_rates` | NUEVA |
| `customers.customers.with_retention` | se deja de usar; se borra en la migración de contrato |
| `quotations.quotation_withholdings`, `quotations.order_withholdings` | NUEVAS (foto) |
| `quotations.quotation_billing_withholding_rates`, `quotations.order_withholding_rates` | NUEVAS: `(document_id, type, tax_rate_id)`, PK `(document_id, type)`, lo elegido en los selectores [asistente; puede resolverse con la columna `source` de la foto si basta] |
| `quotations.quotations.customer_with_retention`, `.party_with_retention`; `quotations.orders.customer_with_retention`, `.party_with_retention` (`QuotationsDbContext.cs:136-141`, `:552-554`) | se dejan de usar; se borran en la migración de contrato |

## 9. Contrato HTTP

Todos los campos marcados NUEVO son aditivos; ningún campo existente cambia de nombre en esta entrega.

### 9.1 Tasas de impuesto (`/api/v1/tenants/{tenantId}/catalog/tax-rates`, `TaxRateEndpoints.cs:20-71`)

Respuesta de cada tasa (`TaxRateResponse`, `CatalogDtos.cs:359-365`; la lista va en `{ "items": [...] }`, `:367`):

```json
{
  "id": "6f1c…",
  "name": "ReteICA Bogotá",
  "percentage": 0.966,
  "isActive": true,
  "type": "ReteICA",
  "country": "CO",
  "behavior": "Withholds",
  "minimumBase": 4,
  "minimumBaseUnit": "UVT",
  "createdAt": "2026-11-10T15:00:00Z",
  "updatedAt": "2026-11-10T15:00:00Z"
}
```

NUEVOS: `type`, `country`, `behavior` (`"Adds" | "Withholds"`), `minimumBase` (`number | null`), `minimumBaseUnit` (`"UVT"`, un
código de moneda, o `null`). `percentage` deja de ser siempre entero.

- `GET …/tax-rates?type=IVA` (NUEVO, repetible) filtra; sin `type`, todas como hoy.
- `POST`: `{ "name", "percentage", "type", "country", "minimumBase" }`. **Compatibilidad**: sin `type` → `IVA`; sin `country` →
  `CO` [asistente].
- `PUT`: `{ "name", "percentage", "minimumBase" }`; `type` y `country` se ignoran. Sin `minimumBase` → se conserva [asistente].

NUEVO `GET /api/v1/tenants/{tenantId}/catalog/tax-rate-options` (permiso `catalog.tax_rate.read`) [asistente, BFF]:

```json
{
  "countries": [
    {
      "country": "CO",
      "taxUnit": "UVT",
      "currency": "COP",
      "types": [
        { "type": "IVA", "behavior": "Adds", "allowsFraction": false, "allowsMinimumBase": false },
        { "type": "Retefuente", "behavior": "Withholds", "allowsFraction": true, "allowsMinimumBase": true },
        { "type": "ReteICA", "behavior": "Withholds", "allowsFraction": true, "allowsMinimumBase": true }
      ]
    }
  ]
}
```

Sólo los países que están en la tabla **y** en alguna empresa del tenant.

### 9.2 Clientes (`CustomerEndpoints.cs`)

- Cuerpo de crear/editar (hoy `request.WithRetention`, `:329`, `:361`): NUEVO `withholdingTaxRateIds: string[] | null`; `null` o
  ausente al editar = conservar; al crear = ninguna [asistente]. `withRetention` en el cuerpo se ignora.
- Respuesta (hoy `:578`): NUEVO `withholdingTaxRates: [{ "id", "name", "type", "country", "percentage", "isActive" }]`.
  `withRetention` sigue, derivado (alguna `Retefuente`) [asistente, compatibilidad].

### 9.3 Cotizaciones y pedidos

En `QuotationDto` (`QuotationsDtos.cs:32-75`) y el detalle del pedido (`OrdersDtos.cs:35-44`, `:229-238`):

```json
{
  "total": 1190000,
  "retentionAmount": 34660,
  "netTotal": 1155340,
  "withholdings": [
    { "taxRateId": "…", "name": "Retefuente 2,5 %", "type": "Retefuente", "country": "CO", "percentage": 2.5,
      "minimumBase": null, "minimumBaseUnit": null, "taxUnitValue": null, "base": 1000000, "amount": 25000 },
    { "taxRateId": "…", "name": "ReteICA Bogotá", "type": "ReteICA", "country": "CO", "percentage": 0.966,
      "minimumBase": 4, "minimumBaseUnit": "UVT", "taxUnitValue": 50000, "base": 1000000, "amount": 9660 }
  ],
  "customerWithholdingTaxRates": [
    { "id": "…", "name": "Retefuente 2,5 %", "type": "Retefuente", "country": "CO", "percentage": 2.5 }
  ],
  "billingWithholdingTaxRates": []
}
```

- NUEVO `withholdings`: sólo las **aplicadas**. `retentionAmount` = suma; `netTotal` igual que hoy. UVT ilustrativa.
- NUEVO `customerWithholdingTaxRates`: en la cotización, la foto del cliente (aplique o no) [asistente].
- NUEVO `billingWithholdingTaxRates`: en la cotización, los selectores de facturación propia; **en el pedido, las tasas vigentes
  del pedido** (lo que prellena los selectores de D21) [asistente].
- Cuerpos de guardado y vista previa: cotización (`QuotationsDtos.cs:256-263`) NUEVO `billingWithholdingTaxRateIds: string[] |
  null`, sólo con `Billing` propio; pedido (`OrdersDtos.cs:174`, edición `SaveOrderEdits`/`PreviewOrderEdits`) NUEVO
  `withholdingTaxRateIds: string[] | null` y NUEVO `billingAccount` (misma forma que en la cotización) para fijar o cambiar la
  empresa (D22). `null`/ausente = conservar [asistente]. `billingWithRetention` en el cuerpo se **ignora**.
- `billingWithRetention` (`QuotationsDtos.cs:72`; `OrdersDtos.cs:35`, `:229`) **sigue en la respuesta**, derivado: con parte propia,
  `billingWithholdingTaxRates.length > 0`; sin parte propia, `null`. **Nunca `null` con parte propia** [asistente]: el frontend
  viejo bloquea la conversión mientras sea `null` (`qep-frontend/src/features/quotes/utils/quote-actions.ts:104-106`).
- `client.withRetention` (`QuotationsDtos.cs:460`): derivado como en §9.2.
- Las vistas previas (`POST /quotations/preview`, `POST /quotations/{id}/preview`, `POST /orders/preview`,
  `POST /orders/{id}/preview`) devuelven los mismos campos.
- Enviar una cotización sin empresa: 422 `quotation.billing.account_required` (código existente, §7.7).

### 9.4 Operador (NUEVO)

`/api/v1/tenants/{tenantId}/operator/tax-units` [asistente, calcado de `/operator/cities`]:

- `GET /` (`operator.tax_units.read`):

  ```json
  {
    "countries": [
      { "country": "CO", "taxUnit": "UVT", "currency": "COP", "nextYearMissing": true,
        "values": [ { "year": 2026, "value": 50000, "updatedAt": "…" }, { "year": 2025, "value": 48000, "updatedAt": "…" } ] }
    ]
  }
  ```

  Montos ilustrativos, no oficiales.
- `PUT /{country}/{year}` (`operator.tax_units.manage`), cuerpo `{ "value": 50000 }` → la fila. Crea o reemplaza.

## 10. Códigos de error NUEVOS

| Código | Cuándo |
| --- | --- |
| `catalog.tax_rate.type_invalid` | `type` no existe en la tabla para ese `country` |
| `catalog.tax_rate.country_not_available` | `country` no está en la tabla o no es el país de ninguna empresa del tenant |
| `catalog.tax_rate.percentage_not_integer` | porcentaje con decimales en un tipo que no admite fracción (IVA) |
| `catalog.tax_rate.percentage_too_precise` | más de 3 decimales |
| `catalog.tax_rate.minimum_base_not_allowed` | `minimumBase` en un tipo que no la admite |
| `catalog.tax_rate.minimum_base_invalid` | `minimumBase < 0` |
| `catalog.tax_rate.tax_unit_value_missing` | base mínima en un país con unidad tributaria sin ningún valor cargado |
| `catalog.product.tax_rate_not_vat` | a un producto se le asigna una tasa que no es `Adds` |
| `quotations.freight_tax_rate.not_vat` | la tasa del envío no es `Adds` |
| `customers.customer.withholding_tax_rate_invalid` | id inexistente, inactivo o de tipo `Adds` |
| `customers.customer.withholding_type_repeated` | dos tasas del mismo tipo y país |
| `quotation.billing.withholding_tax_rate_invalid` / `order.billing.withholding_tax_rate_invalid` | selector con tasa inexistente, inactiva, que no retiene, de otro tipo, de **otro país que la empresa** o sin empresa (D23) |
| `quotation.billing.withholding_type_repeated` / `order.billing.withholding_type_repeated` | dos tasas del mismo tipo |
| `catalog.tax_unit.country_without_unit` | escribir un valor para un país sin unidad tributaria |
| `catalog.tax_unit.year_out_of_range` | año fuera de 2000…año actual + 1 |
| `catalog.tax_unit.value_invalid` | valor `<= 0` |
| `order.billing.currency_change_not_allowed` | la cuenta de cobro de la edición de un pedido `Pending` tiene otra moneda que el pedido (§7.7; owner, 2026-11-10, plan P10) |

Existentes que se reutilizan: `catalog.tax_rate.in_use` (ahora también por clientes), `catalog.tax_rate.not_found`,
`catalog.tax_rate.percentage_out_of_range`, `quotation.billing.account_required` (ahora también al enviar),
`quotation.billing.tax_profile_required`, `order.billing.tax_profile_required` (sólo excedente de IVA). Los errores de validar la
empresa en la edición del pedido reutilizan los `quotation.billing.*` de la cuenta (`QuotationBillingAccount.cs:39-96`) o sus
equivalentes del pedido si existen; quien construye lo verifica.

## 11. Migración

### 11.1 Catalog (expansión)

1. `percentage` → `numeric(6,3)`; `type`, `country` con default temporal; filas existentes `type = 'IVA'`, `country = 'CO'`
   [owner D10; asistente para el país]; se quita el default.
2. Crea `catalog.tax_unit_values` vacía. El operador carga la UVT antes de que alguien cree una tasa con base mínima.

### 11.2 Customers (datos, depende de 11.1 — `Program.cs:349` antes de `:356`)

Por cada `tenant_id` con al menos un cliente `with_retention = true` o un documento con `party_with_retention = true`:

1. Inserta en `catalog.tax_rates` la tasa «Retefuente 2,5 %» (`Retefuente`, `CO`, 2.5, sin base, activa). Si el nombre ya existe,
   «Retefuente 2,5 % (migrada)» [asistente]. Sin precedente de migración que escriba en otro esquema; alternativa: inicializador
   idempotente en `Bootstrapper` después de todas las migraciones. Decide quien construye; corre una vez y es idempotente.
2. Inserta en `customers.customer_tax_rates` esa tasa para cada cliente con `with_retention = true`.

### 11.3 Quotations (datos)

Para **toda** cotización y pedido (D17 congela en todos los estados):

- Cotización con `customer_with_retention = true` → fila `source = 'Customer'` con la tasa migrada.
- Cotización con parte propia y `party_with_retention = true` → selector `Retefuente` = tasa migrada y fila `source = 'Billing'`.
- Pedido con retención aplicable hoy (`party_with_retention` con parte propia, si no `customer_with_retention`) → fila
  `source = 'Document'` y selector `Retefuente` = tasa migrada [asistente].
- `customer_vat_surplus` ya está guardado: desde ahora no se refresca (D24); no requiere migración.
- `amount = retention_amount`, `base_amount = subtotal` donde aplicó. **No se recalcula ningún total** (D10).

### 11.4 Cotizaciones abiertas sin empresa

Sin migración (§7.7). Antes del release: verificar tenants sin empresa facturadora [pendiente de verificación].

### 11.5 Contrato (release N+1, rama aparte)

Borrar `customers.customers.with_retention`, `quotations.quotations.customer_with_retention`/`.party_with_retention`,
`quotations.orders.customer_with_retention`/`.party_with_retention` y los campos derivados (`withRetention`,
`billingWithRetention`, `client.withRetention`), una vez desplegado el frontend nuevo.

## 12. Despliegue

1. Backend (migraciones 11.1–11.3). El frontend viejo sigue funcionando: los campos nuevos son aditivos, los viejos siguen en las
   respuestas y se ignoran en los cuerpos. Diferencias que verá: su vista previa local del 2,5 % puede mostrar un provisional
   distinto; su interruptor «Aplica retención» con datos propios deja de tener efecto; enviar sin empresa da
   `quotation.billing.account_required`; editar el excedente o la retención de un cliente ya no mueve sus cotizaciones abiertas;
   los selectores de tasa de producto y de envío listan también las tasas que retienen (Retefuente, ReteICA), y elegir una
   responde 422 `catalog.product.tax_rate_not_vat` o `quotations.freight_tax_rate.not_vat`, códigos que el frontend viejo no
   mapea a un campo (muestra el error genérico).

   **Corte duro.** Una vez corridas las migraciones, el binario viejo del backend no puede seguir atendiendo escrituras: un
   documento que él cree o edite no recibe foto de retenciones (no conoce `quotation_withholdings` ni `order_withholdings`) y
   queda con totales que el binario nuevo no sabe reconstruir. El despliegue reemplaza todas las instancias a la vez, sin
   convivencia de versiones.
2. El operador carga la UVT de Colombia del año en curso.
3. Frontend.
4. Release N+1: migración de contrato (§11.5).

## 13. Pruebas (TDD: RED antes que GREEN)

Unitarias de dominio:

- `TaxCountries`: Colombia tiene los tres tipos con su comportamiento; un país desconocido da `null`.
- `TaxRate.Create`: IVA 19 pasa; IVA 2.5 → `percentage_not_integer`; Retefuente 2.5 y 0.966 pasan; 0.9665 →
  `percentage_too_precise`; tipo de otro país → `type_invalid`; IVA con base → `minimum_base_not_allowed`; base −1 →
  `minimum_base_invalid`. `Update` no cambia tipo ni país.
- `DocumentTotals.Compute`: dos retenciones sobre el subtotal sin IVA ni envío; `NetTotal = Total − suma`; base mínima en UVT
  (debajo no, igual sí, encima sí); `MinimumBase = null` siempre; redondeo `AwayFromZero`. Regresión: una Retefuente 2,5 % sin base
  da **exactamente** los números de hoy.
- `Quotation`: consumidor final → ninguna; USD con empresa colombiana → ninguna; sin `BillingAccount` → ninguna y aplica al
  elegirla; datos del cliente → tasas del cliente; datos propios → selectores (vacíos = ninguna); volver a datos del cliente
  recupera la foto; editar tasas o excedente del cliente **no** mueve una cotización Draft (D17, D24); `ChangeClient` recopia tasas
  y excedente y vacía selectores; `EnsureSendable` sin empresa → `quotation.billing.account_required`; selector de otro país →
  `withholding_tax_rate_invalid`.
- `Order`: convertir hereda la foto; tienda copia las tasas del cliente **del país de su empresa** aunque tenga parte propia;
  `Pending` acepta selectores (D21) y no toca al cliente; no `Pending` los rechaza; `SetBillingAccount` en `Pending` habilita
  retenciones; cambio a otro país deja de aplicar la tasa vieja.
- Resolución de UVT: año del documento; si falta, el mayor anterior; sólo posteriores → no aplica; 31-dic 20:00 Bogotá = ese año.
- `nextYearMissing`: 23-dic falso; 24-dic sin el año siguiente verdadero; con el año siguiente falso; país sin unidad nunca.

Integración (Testcontainers, un proyecto a la vez):

- Catalog: alta sin `type`/`country` crea IVA/CO; país sin empresa → `country_not_available`; `GET ?type=` filtra;
  `tax-rate-options` sólo países de empresas; desactivar/borrar tasa asignada a cliente → `catalog.tax_rate.in_use`; tasa sólo
  congelada en documentos se puede desactivar; producto con Retefuente → `catalog.product.tax_rate_not_vat`.
- Customers: dos Retefuente → `withholding_type_repeated`; tasa IVA → `withholding_tax_rate_invalid`; editar sin
  `withholdingTaxRateIds` conserva; `withRetention` derivado; import/export con columnas por tipo.
- Quotations: dos retenciones → dos líneas y suma; selector «Retefuente servicios 4 %» aplica 4 %; `GET` de una cotización ya no
  escribe; PDF con una fila por retención con el nombre; Excel de pedidos con «Retefuente» y «ReteICA»; `billingWithRetention`
  nunca `null` con parte propia; pedido de la tienda con las tasas del cliente; edición de pedido `Pending` con selectores y
  empresa; **webhook de Shopify sigue creando pedidos** como hoy (regresión, D22).
- Operador: sin `operator.tax_units.read` → 403; tenant que no es el operador → 403; `PUT` crea y reemplaza.
- Migración: «Retefuente 2,5 %» por tenant y asignada; foto en cada documento con retención; ningún total cambió; tasas IVA/CO.
- `ArchitectureTests`: Customers y Quotations siguen sin referenciar Catalog.

## 14. Decisiones cerradas el 2026-11-10

Primera ronda: P-1 → D16, P-2 → D17, P-3/P-4/P-7/F-1/F-2 → D20, P-5 → D18, P-6 → D19, P-8 → D15. Segunda ronda (revisión del
constructor): pedidos de la tienda → excepción de D16; edición de retenciones del pedido → D21; empresa sólo obligatoria en
cotizaciones → D18/D22; cambio de país de la empresa → D23; excedente de IVA congelado → D24.

## 15. Pendiente

- **Verificar** si algún tenant no tiene empresas facturadoras (no podría enviar cotizaciones tras el despliegue).
- **Verificar** si existen pedidos guardados con `BillingAccount` nulo (anteriores a que las fábricas lo exigieran). D22 asume que
  un pedido puede no tener empresa, pero el código actual no deja crear ninguno así (§7.7). Si el owner quiere de verdad pedidos
  manuales o de Shopify sin empresa, relajar `CreateManual`/`CreateExternal` es un cambio aparte.

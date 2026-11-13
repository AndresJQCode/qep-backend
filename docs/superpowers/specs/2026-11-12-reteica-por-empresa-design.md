# ReteICA por empresa facturadora y municipio — diseño backend

Fecha: 2026-11-12. Trabajo que cruza repos: este spec cubre `qep-backend` y **manda el contrato**; la mitad del frontend es
`qep-frontend/docs/superpowers/specs/2026-11-12-reteica-por-empresa-design.md`. Salen juntos, backend primero (§12).

Estado: **aprobado por el owner 2026-11-12, sin construir**. Rama `feature/reteica-por-empresa` desde `develop` (`bfe9d8d0`, que ya
contiene el merge `8052ee2f` de impuestos por tipo). Las rutas y líneas citadas son de ese commit.

Base: spec 2026-11-10 «Impuestos por tipo y país» (`2026-11-10-impuestos-por-tipo-design.md`). Su línea «Estado: diseño, no
construido» está desactualizada: ese trabajo está mergeado en `develop` (backend `8052ee2f`, frontend `8a1368c9`). Donde este spec
dice «baseline Dn» se refiere a sus decisiones.

**Hecho verificado por el owner (2026-11-12): la ReteICA nunca existió fuera de `develop` local.** El merge de impuestos por tipo del
backend (`8052ee2f`) no está en ninguna rama remota; el del frontend (`8a1368c9`) está en `origin/develop` pero no en `main`; producción
no tiene tipos de impuesto. La migración de datos de ese trabajo
(`MigrateRetentionToWithholdings.cs:22-86`) sólo crea «Retefuente 2,5 %», nunca una ReteICA. Al desplegar hay **cero** tasas
ReteICA, **cero** asignaciones ReteICA a clientes y **cero** filas ReteICA congeladas: este trabajo no migra datos (§11) ni
contempla documentos legados con ReteICA.

Marcas: **[owner]** decisión del owner (2026-11-12, R1–R16 abajo); **[agente]** propuesta de este spec, revisable por el owner;
**NUEVO** = no existe hoy en el código. Todo lo que no lleva NUEVO se verificó en el archivo citado.

## 1. Intención

Hoy la ReteICA se asigna **al cliente**, como la Retefuente: `customers.customer_tax_rates` (`CustomersDbContext.cs:250-264`),
`Customer.WithholdingTaxRateIds` (`Modules.Customers.Domain/Customer.cs:288`), `Customer.SetWithholdingTaxRates` (`:854`). Una
cotización congela las tasas del cliente al crearse (`QuotationDraftBuilder.cs:74-75`) y aplica las del país de la empresa
facturadora (`WithholdingCandidates.cs:17-37`).

Eso está mal para el ICA. El impuesto se causa en el municipio donde se ejerce la actividad (Ley 14 de 1983; Ley 1819 de 2016,
art. 343): los servicios, donde se prestan; el comercio con establecimiento, en el municipio del establecimiento; sin
establecimiento, donde se perfecciona la venta (este último literal **no está verificado del todo** contra el texto de la ley;
queda como supuesto, §14). La tarifa depende de **quien vende** (la empresa facturadora: su municipio y su actividad CIIU), y
cada municipio designa a sus agentes retenedores, así que un cliente puede retener ICA en Bogotá y no en Medellín. Al menos un
tenant factura con varias empresas en municipios distintos.

## 2. Objetivos

- Cada tipo de impuesto declara en código su **alcance territorial**: `National` o `Municipal` [owner R1].
- Una tasa de tipo municipal lleva su **municipio** (`cityId`) [owner R2].
- La **empresa facturadora** asigna sus tasas de retención municipales: a lo sumo una por (tipo, municipio) [owner R3].
- El **cliente** conserva la Retefuente y gana la lista de **municipios donde es agente retenedor**, por tipo municipal; ya no
  acepta tasas municipales [owner R4, R8].
- Cotización y pedido ganan el **municipio de la operación** (`operationCityId`), por defecto el de la empresa [owner R5].
- Una retención municipal aplica sólo si la empresa tiene tasa de ese tipo para el municipio de la operación **y** el cliente es
  agente retenedor ahí [owner R6].
- El asesor puede **editar el porcentaje** de una retención municipal en un documento editable [owner R7].

## 3. No objetivos

- España (IVA, IRPF, recargo de equivalencia, IGIC con alcance regional): spec aparte. El enum de alcance queda listo para sumar
  `Regional` sin tocar a los llamadores (§7.1) [owner R1].
- Editar el porcentaje de retenciones no municipales (Retefuente) [owner R7].
- Cambiar la Retefuente: sigue en el cliente, sin cambios [owner R8].
- Recalcular documentos ya creados por cambios de la ficha de la empresa o del cliente [baseline D17].
- Migrar datos de ReteICA: no hay (§11).
- Validar la actividad CIIU de la empresa o sugerir la tarifa del municipio: la tasa la escribe el tenant, como hoy.
- Municipios fuera de Colombia: Geography sólo tiene DIVIPOLA (§5).
- Retenciones en el POS (fuera de alcance desde el baseline).

## 4. Anti objetivos

- **Nunca** preguntar «¿es ReteICA?» en la lógica: se pregunta «¿el tipo es `Municipal`?» (`TaxCountries`) [owner R1].
- **Nunca** referenciar Catalog, Companies, Customers o Geography desde otro módulo de negocio: puertos en Application, adaptadores
  en `Bootstrapper` (§5).
- **Nunca** resolver la retención municipal en el navegador: el contrato dice qué ofrece la empresa y por qué aplicó o no
  (§9.4).
- **Nunca** mover filas ni totales de un documento al leerlo: la resolución corre sólo al crear y ante los cambios de §7.6. Leer
  la empresa y al cliente al responder sí se permite, pero sólo para **explicar** (`companyRate`, `status`, §9.4, A14), nunca para
  cambiar la foto.
- **Nunca** hacer fallar el webhook de Shopify ni el checkout de la tienda por la ReteICA (baseline D22).

## 5. Restricciones verificadas

- **Tabla de países.** `TaxTypeDefinition(string Type, TaxBehavior Behavior, bool AllowsFraction, bool AllowsMinimumBase)`
  (`BuildingBlocks/BuildingBlocks.Domain/Taxes/TaxCountries.cs:11`); Colombia con `IVA`, `Retefuente`, `ReteICA` (`:36-41`). La
  leen Catalog, Customers y Quotations.
- **La tasa no tiene municipio.** `TaxRate` (`Modules.Catalog.Domain/TaxRate.cs`) tiene `Percentage`, `Type`, `Country`,
  `MinimumBase` (`:70-82`); `Update(name, percentage, minimumBase, …)` no cambia tipo ni país (`:151`).
- **Geography es sólo Colombia.** `City` y `Department` son DIVIPOLA (`Modules.Geography.Domain/City.cs:4`, `Department.cs:4`); no
  hay columna de país. «La ciudad pertenece al país de la tasa» se reduce a «el país es `CO` y la ciudad existe».
- **Orden de migraciones** (`src/Api/Program.cs`): Catalog (`:352`), Geography (`:357`), Customers (`:359`), Companies (`:361`),
  Suppliers (`:364`), Quotations (`:372`). Catalog migra **antes** que Geography: `catalog.tax_rates` no puede declarar una FK a
  `geography.cities` en su propia migración en una base vacía. Customers y Companies sí pueden (Companies ya la tiene a mano,
  `Company.cs:101-111`).
- **No hay ReteICA que migrar.** `MigrateRetentionToWithholdings.cs:22-86` sólo crea la tasa `Retefuente` (filtro `Equivalent`,
  `r.type = 'Retefuente'`) y la asigna; ninguna línea crea o asigna una ReteICA. Con el hecho del owner de arriba, en producción no
  existe ningún dato ReteICA.
- **La empresa tiene municipio.** `Company.CityId` (`Company.cs:111`), obligatorio si `Country == "CO"` (`:366-378`, código
  `companies.company.city_required`), FK a `geography.cities` agregada a mano. `ICompanyGeographyLookup` (`ICompanyGeographyLookup.cs`)
  resuelve nombre y departamento.
- **La empresa no cruza con su municipio.** `QuotationCompanyRef` trae `Country` pero no `CityId`
  (`Modules.Quotations.Application/IQuotationCompanyLookup.cs:20-36`); la foto `QuotationBillingAccount` guarda `CompanyId`, banco,
  cuenta, moneda y `Country` (`QuotationBillingAccount.cs:22-39`), sin ciudad.
- **Escritura de la empresa reemplaza todo.** `UpdateCompanyRequest` (`CompaniesDtos.cs:122-132`): lo que no viene se limpia
  (comentario `:97-109`). Un campo nuevo tiene que ser «ausente = conservar» para el frontend viejo.
- **Permisos de la empresa**: `companies.company.read` / `.manage` (`CompaniesPermissions.cs`).
- **Cliente.** Reglas de retención en `CustomerWithholdingRules` (`CustomerWriteRules.cs:176-227`): activa, que retiene, una por
  (país, tipo). Puerto `ICustomerTaxRateLookup` (`ICustomerTaxRateLookup.cs`). Excel con columnas «Retefuente» y «ReteICA»
  (`IExcelCustomerImporter.cs:56-59`, `:101`; `ImportCustomers.cs:512-560`; export `ClosedXmlCustomerExportBuilder.cs:134-135`).
  `withRetention` se deriva de tener una Retefuente (`CustomerMapping.cs:74-76`).
- **«En uso» de una tasa.** `ITaxRateCustomerUsage` (`Modules.Catalog.Application/ITaxRateCustomerUsage.cs`), adaptador
  `Bootstrapper/TaxRateCustomerUsage.cs`, usado por `DeactivateTaxRate.cs` y `DeleteTaxRate.cs`.
- **La foto.** `WithholdingRate(TaxRateId, Name, Type, Country, Percentage, MinimumBase, MinimumBaseUnit, TaxUnitValue)`
  (`WithholdingRate.cs`). `DocumentWithholding` con `Source` `Customer | Billing | Document` y `Applied`
  (`DocumentWithholding.cs`); columnas en `QuotationsDbContext.ConfigureWithholding` (`:239-267`), tablas
  `quotations.quotation_withholdings` (`:207-208`) y `quotations.order_withholdings` (`:657-658`).
- **Candidatas.** `WithholdingCandidates.For(source, billsToFinalConsumer, billingAccount, currency)` (`WithholdingCandidates.cs:17`):
  consumidor final, moneda tributaria del país y país de la empresa. `DocumentTotals.Compute` calcula con `rate.Percentage`.
- **Fuentes de la cotización.** Cliente (`WithholdingSource.Customer`, copiadas en `Quotation.Create` y `ChangeClient`,
  `Quotation.cs:90`, `:798-817`) o selectores de facturación propia (`Billing`, `SetBillingWithholdings`, `:702-724`); vigente
  según haya parte propia (`:377-378`). La cotización se crea sin empresa: `ForCustomerAsync(…, country: null, …)`
  (`QuotationDraftBuilder.cs:74-75`). Editable en `Draft` y `Sent` (`EnsureEditable`, `:1105-1113`,
  `quotation.quotation.not_editable`).
- **Fuente del pedido.** Una sola (`Document`). Al convertir hereda la vigente de la cotización (`Order.cs:395`); manual, tienda
  e integración usan `OrderCustomerWithholdings.ResolveAsync` (`OrderDraftBuilder.cs:131`, `IStoreOrderGateway.cs:184`,
  `IExternalOrderWriter.cs:180`). La edición aplica cuenta y selectores en `OrderWithholdingEdits` (`OrderEdits.cs:121-174`).
  `Order.IsEditable` = abierto, sin preparar y sin facturar (`Order.cs:283`); si no, `order.order.not_editable`
  (`ExternalOrderErrors.cs:14`, `Order.cs:1623-1629`).
- **Selectores.** `WithholdingSelection.ResolveAsync` (Application) y `WithholdingSelectorRules.Ensure` (Domain) exigen tasa
  activa, que retiene, del país de la empresa, una por tipo; códigos `quotation.billing.withholding_*` y `order.billing.withholding_*`.
- **Historial.** `OrderHistoryEventType` (`OrderHistoryEventType.cs`) y `QuotationHistoryEventType`
  (`QuotationHistoryEventType.cs:14-31`) se guardan como texto: sumar un valor no requiere migración (comentarios en los dos
  archivos). La edición del pedido escribe `PartiesChanged` para cuenta y retenciones (`SaveOrderEdits.cs:273-301`) con textos
  de `OrderChangeSummary` (`WithholdingsChanged`, `:95-96`).
- **Permisos.** Guardar cotización exige `quotations.quotation.manage` (`QuotationEndpoints.cs:69-90`); editar pedido,
  `quotations.order.manage` (`OrderEndpoints.cs:55-72`). `CompositionRootTests` fija **80** permisos
  (`tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs:206`); este trabajo no agrega ninguno.
- **PDF.** Una fila por retención aplicada con el **nombre** de la tasa y el monto, sin porcentaje (`quotation.typ:371-424`,
  `QuotationPdfWithholding(string Name, decimal Amount)`).
- Errores de dominio → 422 con `code` (`src/Api/ApiExceptionHandler.cs`). TDD obligatorio; integración con Testcontainers, un
  proyecto a la vez.

## 6. Decisiones

### 6.1 Del owner (2026-11-12)

| # | Decisión |
| --- | --- |
| R1 | Alcance territorial genérico por tipo en `TaxCountries`: `National` o `Municipal`. Colombia: IVA y Retefuente `National`, ReteICA `Municipal`. La lógica pregunta por el alcance, nunca por el nombre. Se expone en `GET tax-rate-options`. Sólo Colombia en este trabajo; el enum admite `Regional` después (España, spec aparte). |
| R2 | Opción C: la ReteICA sigue en `catalog.tax_rates`. Una tasa de tipo municipal **exige** `cityId` (municipio de Geography, `Guid` plano por puerto), validado que exista y sea del país de la tasa. Un tipo nacional lo rechaza. |
| R3 | La empresa facturadora asigna sus tasas de retención municipales: a lo sumo una por (tipo, municipio); activa, que retiene, del país de la empresa. Campo nuevo en el contrato de escritura y lectura de la empresa, permiso `companies.company.manage`. El «en uso» de Catalog cuenta también las asignaciones de empresas. |
| R4 | El cliente conserva la Retefuente en `withholdingTaxRateIds` y deja de aceptar tasas municipales (código nuevo). Gana la lista de municipios donde es agente retenedor (forma: §7.4). La columna ReteICA del Excel se decide en §7.4. |
| R5 | Cotización y pedido ganan `operationCityId` (Guid blando, como `city_id` de las partes). Por defecto, el municipio de la empresa facturadora (la foto `QuotationBillingAccount` gana la ciudad y `QuotationCompanyRef` gana `CityId`). Sólo tiene sentido si el país de la empresa tiene algún tipo municipal. Editable mientras el documento lo sea. Pedidos de la tienda, de Shopify y manuales también arrancan con el de la empresa. |
| R6 | Una retención municipal aplica sólo si (a) pasan las reglas de hoy (`WithholdingCandidates.For`), (b) la empresa del documento tiene una tasa de ese tipo para el municipio de la operación y (c) el cliente es agente retenedor ahí. Se congela al crear, como hoy; convertir hereda. Cambiar la empresa o el municipio mientras sea editable vuelve a resolver la fila (se pierde un porcentaje editado y queda en el historial). |
| R7 | El asesor edita el porcentaje de una retención municipal del documento. Permisos existentes: `quotations.quotation.manage` y `quotations.order.manage`; ninguno nuevo. Cotización mientras sea editable; pedido sólo con `Order.IsEditable` (`order.order.not_editable`). Rango `0 < p ≤ 100`, hasta 3 decimales (`numeric(6,3)`). La foto guarda el porcentaje del catálogo y el efectivo. Historial con evento nuevo `WithholdingRateEdited` («de X % a Y %»). Totales recalculados. |
| R8 | La Retefuente se queda en el cliente, sin cambios. |
| R9 | ~~Migración de datos de ReteICA~~ **Sin efecto** (2026-11-12): el owner verificó que no existe ningún dato ReteICA fuera de `develop` local (§11). |
| R10 | Despliegue: backend primero, describiendo la compatibilidad del frontend viejo con el backend nuevo, como el baseline. |
| R11 | (2026-11-12, segunda ronda) En un pedido editable y en una cotización con facturación propia, el asesor puede aplicar **a mano** la tasa municipal de la empresa para el municipio de la operación aunque el cliente no esté registrado como agente retenedor ahí. Queda en el historial. Un cambio posterior de empresa o de municipio re-resuelve (§7.6): en el pedido, con la regla del cliente, que la quita y lo anota en el historial; en la cotización con facturación propia, la selección pasa a la tasa de la empresa para el municipio nuevo, o queda vacía si la empresa no tiene (owner, 2026-11-13). Resuelve los pendientes A4 y A5 de la primera versión. |
| R12 | (2026-11-12, confirmado) «Impuestos por tipo» y este trabajo se despliegan **juntos**. El frontend de impuestos por tipo (`8a1368c9`) **ya está** en `origin/develop` (no en `main`); el backend (`8052ee2f`) **no** está publicado. Hasta mergear este trabajo: no se lleva `develop` a `main` en ninguno de los dos repos y no se publica (push) `develop` del backend. Así nunca existe en producción una ReteICA con el modelo del baseline (§11.2, §12). |
| R13 | (2026-11-12) El PDF de cotización y de pedido muestra el **porcentaje** de cada retención, para que el cliente compruebe el monto (§7.9). [agente] En **todas** las filas de retención, no sólo en las editadas, y siempre el porcentaje **efectivo**: «ReteICA Bogotá 0,966 %». |
| R14 | (2026-11-12) Acepta A3: el Excel de clientes pierde la columna «ReteICA» y no gana una de municipios agente (§7.4). |
| R15 | (2026-11-12) Acepta A6: cambiar la empresa reinicia el municipio de la operación al de la empresa nueva, salvo que el mismo cuerpo traiga `operationCityId` (§7.5). |
| R16 | (2026-11-12) Acepta A10: el alta manual de pedido no recibe municipio de la operación ni porcentajes; se ajustan en la edición (§9.4). |

### 6.2 Propuestas del agente

| # | Propuesta |
| --- | --- |
| A1 | `cityId` es obligatorio al crear una tasa de tipo municipal e inmutable después, como `type` y `country` (R2 lo dejó al agente). No hay excepción: no existen tasas municipales sin municipio (§11). |
| A2 | La lista de agentes del cliente es **por (país, tipo municipal)**, no una lista plana de municipios (§7.4). |
| A3 | El Excel de clientes **pierde** la columna «ReteICA» en plantilla y exportación; un archivo viejo con esa columna se lee ignorándola (§7.4). **Aceptada por el owner 2026-11-12 (R14).** |
| A4 | ~~Propuesta~~ → owner R11. El selector de un tipo municipal (facturación propia de la cotización y edición del pedido) ofrece la tasa de la empresa para el municipio de la operación, o «Sin retención», **sin mirar** la lista de agentes del cliente (§7.5). |
| A5 | Regla única de re-resolución ante cambio de empresa, de municipio o de cliente (§7.6); confirmada por R11 para la tasa aplicada a mano. |
| A6 | Cambiar la empresa **reinicia** el municipio de la operación al de la empresa nueva, salvo que el mismo cuerpo traiga `operationCityId` (§7.5). **Aceptada por el owner 2026-11-12 (R15).** |
| A7 | Con empresa de un país sin tipos municipales, `operationCityId` se guarda `null` y lo que llegue se ignora. |
| A8 | Los documentos existentes **no** reciben `operation_city_id` (queda `null`). No tienen filas municipales (no existía la ReteICA), así que su `status` se calcula como cualquier otro (`NoOperationCity` hasta que se fije el municipio en uno editable). |
| A9 | `catalog.tax_rates.city_id` sin FK (Catalog migra antes que Geography); la existencia la garantiza el puerto. Las tablas nuevas de Customers y Companies sí llevan FK a `geography.cities`. |
| A10 | El pedido manual no recibe `operationCityId` ni porcentajes al crearse: nace con el municipio de su empresa y se ajusta en la edición, igual que los selectores del baseline. **Aceptada por el owner 2026-11-12 (R16).** |

### 6.3 Propuestas del agente (revisión de constructor, 2026-11-12)

| # | Propuesta |
| --- | --- |
| A11 | **Selecciones viejas en el mismo cuerpo.** Si un cuerpo cambia la empresa o el municipio de la operación, el backend **ignora** en ese cuerpo los selectores de tipos municipales (`billingWithholdingTaxRateIds` / `withholdingTaxRateIds`) y las entradas de `withholdingPercentages` de tipos municipales: gana la re-resolución, y un porcentaje editado que se pierde queda en el historial. Los selectores nacionales del mismo cuerpo se procesan como hoy (§7.6). El frontend además los quita del borrador y de cada vista previa (spec del frontend §6.5). |
| A12 | **Municipio de la empresa en el documento.** La respuesta expone `billingCompanyCity` desde `billing_company_city_id` guardado, para que «Usar el de la empresa» compare sin pedir Companies (§9.4). |
| A13 | ~~Documentos legados (`status = Legacy`)~~ **Retirada** (2026-11-12): no existen filas ReteICA congeladas sin municipio. |
| A14 | **`status` derivado al leer, sin columna nueva**, con los datos de hoy de la empresa y del cliente y con precedencia explícita (§9.4). En el pedido, sin fila: cliente no agente → `CustomerNotAgent`; cliente agente y empresa con tasa → `NotSelected`. |
| A15 | ~~Fusión por alcance~~ **Simplificada** (2026-11-12): `withholdingTaxRateIds` del cliente acepta sólo tasas de alcance nacional y una municipal es `withholding_tax_rate_municipal`. No hay asignaciones municipales guardadas que conservar (§7.4). |
| A16 | **Tasas inactivas en la empresa.** La escritura de la empresa tolera un id ya guardado que hoy esté inactivo (se conserva) y rechaza uno **nuevo** inactivo (§7.3). La parte de la migración se retira: no hay migración (§11). |
| A17 | ~~Despliegue conjunto~~ → owner R12. |

## 7. Diseño

### 7.1 `TaxCountries`: alcance territorial [owner R1]

`BuildingBlocks/BuildingBlocks.Domain/Taxes/TaxCountries.cs`:

```csharp
/// Dónde se causa un tipo de impuesto. Regional (comunidades autónomas, Canarias) entra con el spec de España.
public enum TaxScope { National, Municipal }

public sealed record TaxTypeDefinition(
    string Type, TaxBehavior Behavior, bool AllowsFraction, bool AllowsMinimumBase, TaxScope Scope);

public static readonly TaxCountryDefinition Colombia = new("CO", "UVT", "COP",
[
    new(IvaType, TaxBehavior.Adds, AllowsFraction: false, AllowsMinimumBase: false, TaxScope.National),
    new(RetefuenteType, TaxBehavior.Withholds, AllowsFraction: true, AllowsMinimumBase: true, TaxScope.National),
    new(ReteIcaType, TaxBehavior.Withholds, AllowsFraction: true, AllowsMinimumBase: true, TaxScope.Municipal),
]);

/// Los tipos municipales de un país (vacío si el país no está en la tabla).
public static IReadOnlyList<TaxTypeDefinition> MunicipalTypes(string? country);
```

- `Scope` viaja como texto (`"National" | "Municipal"`), como `Behavior`.
- Un tipo municipal sólo tiene sentido con `Behavior == Withholds` en este trabajo; la tabla no lo prohíbe [agente].
- Las constantes `ReteIcaType` y `RetefuenteType` (`:33-34`) se quedan para el Excel de pedidos y el de clientes; ninguna regla nueva
  las usa.

### 7.2 Catalog — municipio de la tasa [owner R2; agente A1, A9]

`TaxRate` gana `CityId` (`Guid?`), NUEVO.

- `Create(…, cityId, …)`: tipo municipal sin `cityId` → 422 NUEVO `catalog.tax_rate.city_required`; tipo nacional con `cityId` →
  422 NUEVO `catalog.tax_rate.city_not_allowed`. El dominio sólo exige «no vacío» (mismo criterio que `Company.EnsureValidLocation`,
  `Company.cs:361-365`).
- Que la ciudad exista y sea del país: en `CreateTaxRateHandler`, por puerto NUEVO `ITaxRateGeographyLookup` (adaptador en
  `Bootstrapper` sobre `ICityRepository`/`IDepartmentRepository`, como `CompanyGeographyLookup.cs`). Como Geography sólo es DIVIPOLA
  (§5), «del país» es `Country == "CO"`; si no existe o el país no es `CO` → 422 NUEVO `catalog.tax_rate.city_invalid`.
- `Update`: `cityId` no se edita; si el cuerpo lo trae, se ignora, como `type` y `country` (A1).
- Columna NUEVA `catalog.tax_rates.city_id uuid null`, sin FK (A9). Índice NUEVO `(tenant_id, type, city_id)` no único: puede haber
  varias tarifas por municipio (una por actividad CIIU), y es la empresa la que elige una.
- «En uso» (R3): puerto NUEVO `ITaxRateCompanyUsage.AnyCompanyWithTaxRateAsync(tenantId, taxRateId)`, adaptador en `Bootstrapper`
  sobre `ICompanyRepository`. `DeactivateTaxRate` y `DeleteTaxRate` lo suman al de clientes; el código sigue siendo
  `catalog.tax_rate.in_use` y su mensaje pasa a «product, customer or company» [agente: un puerto por módulo consultado, como
  `ITaxRateCustomerUsage`].
- Lectura: `TaxRateDto`/`TaxRateResponse` (`CatalogDtos.cs:359-384`) ganan `scope`, `cityId` y `city` (§9.1). El nombre se resuelve
  en lote con `ITaxRateGeographyLookup` en los handlers de lista y detalle.

### 7.3 Companies — tasas municipales de la empresa [owner R3]

- Tabla NUEVA `companies.company_withholding_tax_rates (tenant_id uuid, company_id uuid, tax_rate_id uuid, PK (company_id,
  tax_rate_id))`, dueña `Company` (como `company_bank_accounts`, `CompaniesDbContext.cs:114-117`), sin FK a `catalog`. Índice
  `(tenant_id, tax_rate_id)` para el «en uso».
- `Company` gana `WithholdingTaxRateIds` y `SetWithholdingTaxRates(ids, occurredAt)` (NUEVOS), calcados de `Customer.cs:288`,
  `:854-877`.
- Validación en Application por puerto NUEVO `ICompanyTaxRateLookup.FindManyAsync(tenantId, ids)` → `CompanyTaxRateRef(Id, Name,
  Type, Country, Percentage, IsActive, Withholds, CityId)` (adaptador en `Bootstrapper`, calcado de `CustomerTaxRateLookup.cs`):
  - cada id existe en el tenant, está activo, retiene, su tipo es `Municipal` (`TaxCountries.FindType(...).Scope`) y su país es el
    de la empresa → si no, 422 NUEVO `companies.company.withholding_tax_rate_invalid`;
  - dos con el mismo `(type, cityId)` → 422 NUEVO `companies.company.withholding_municipality_repeated`.
  - Tolerancia de lo ya guardado (A16): un id que la empresa **ya tiene** y hoy está inactivo no hace fallar el `PUT` y se conserva si
    viene en la lista; un id **nuevo** inactivo es `withholding_tax_rate_invalid`. Es raro: desactivar una tasa asignada a una
    empresa da `catalog.tax_rate.in_use` (§7.2), igual que con clientes (`DeactivateTaxRate.cs:44-51`), pero la carrera entre las
    dos escrituras no la frena ninguna FK.
- Cambiar el país de la empresa con tasas guardadas de otro país: la lista resultante (la del cuerpo, o la guardada si no viene) se
  valida contra el país nuevo; una de otro país es `withholding_tax_rate_invalid` [agente]. El municipio propio de la empresa
  **no** restringe sus tasas: puede tener tarifa en municipios donde presta servicios.
- Contrato (§9.2): `withholdingTaxRateIds` en `POST`/`PUT` (ausente o `null` = conservar, lista vacía = quitar; en el alta,
  ninguna), `withholdingTaxRates` en la respuesta. El listado (`CompanyListItemResponse`) no cambia.
- Auditoría: el `PUT` ya audita; sin evento nuevo [agente].

### 7.4 Customers — sin tasas municipales; agentes por municipio [owner R4, R8; agente A2, A3]

**Tasas (A15).** `CustomerWithholdingRules.Validate` (`CustomerWriteRules.cs:206-227`) rechaza una tasa de alcance `Municipal` con
422 NUEVO `customers.customer.withholding_tax_rate_municipal`, antes de la regla de repetidos. `Customer.SetWithholdingTaxRates`
(`Customer.cs:854-877`) sigue reemplazando la lista entera, que sólo puede tener tasas nacionales. La Retefuente sigue igual (R8).
No hay asignaciones municipales guardadas que conservar (§11).

**Agentes.** Tabla NUEVA:

```
customers.customer_withholding_agent_cities
  tenant_id    uuid        not null
  customer_id  uuid        not null
  country      char(2)     not null
  type         varchar(32) not null    -- un tipo Municipal de TaxCountries para ese país
  city_id      uuid        not null    -- FK geography.cities (Customers migra después de Geography)
  PK (customer_id, country, type, city_id)
```

Por qué **por (país, tipo)** y no una lista plana (A2): la designación de agente retenedor es del municipio **para un impuesto**;
mañana un segundo tipo municipal (o los regionales de España) tendría su propia lista sin migrar nada. El país va en la clave
porque el nombre del tipo sólo es único dentro del país (`TaxCountries.cs:19`, «único dentro de la lista»).

- `Customer` gana `WithholdingAgentCities` y `SetWithholdingAgentCities(entries, occurredAt)` (NUEVOS).
- Validación: tipo municipal del país → si no, 422 NUEVO `customers.customer.withholding_agent_type_invalid`; ciudad existente
  (`ICustomerGeographyLookup.FindCitiesAsync`, ya existe) y país `CO` → si no, 422 NUEVO
  `customers.customer.withholding_agent_city_invalid`. Repetidos en el cuerpo se colapsan [agente].
- Fusión: el sobreviviente conserva su lista (mismo criterio que sus tasas, `CustomerEndpoints.cs:342`) [agente].
- Puerto hacia Quotations: `IQuotationWithholdingLookup` gana la consulta del agente (§7.5).

**Excel (A3).** La columna «ReteICA» (`CustomerImportColumns.ReteIca`, `IExcelCustomerImporter.cs:59`) sale de la plantilla
(`ClosedXmlCustomerImportTemplateBuilder.cs:139`), de la lectura (`ClosedXmlCustomerImporter.cs:83`) y de la exportación (`ClosedXmlCustomerExportBuilder.cs:135`);
`CustomerImportColumns.WithholdingTypes` queda en `[Retefuente]`. Un archivo con la columna «ReteICA» (sólo pudo salir de un
`develop` local) se lee ignorándola, como el baseline hizo con «Con Retencion». Por qué no reemplazarla por «Municipios ReteICA»: un municipio se identifica por departamento y nombre (hay homónimos,
p. ej. Barbosa en Antioquia y en Santander) y en una celda con varios la fila se vuelve frágil; la lista se edita en la ficha. Si el
owner la quiere, queda en §14.

### 7.5 Quotations — municipio de la operación y resolución [owner R5, R6; agente A4, A6, A7, A10]

**Empresa con municipio.** `QuotationCompanyRef` gana `Guid? CityId` (adaptador `Bootstrapper/QuotationCompanyLookup.cs:29`).
`QuotationBillingAccount` gana `Guid? CityId`, copiado por `QuotationBillingAccountResolver` junto con `Country`
(`QuotationBillingAccountResolver.cs:47`). Columnas NUEVAS `billing_company_city_id uuid null` en `quotations.quotations` y
`quotations.orders`. Se expone como `billingCompanyCity` (A12, §9.4). En un documento anterior a este trabajo la columna arranca
`null` y se completa la próxima vez que el cuerpo trae la cuenta (el guardado de la cotización siempre la trae: `SaveQuotationRequest` reemplaza el
encabezado entero, `QuotationsDtos.cs:425-447`); reenviar la **misma** empresa completa la ciudad pero no es «cambiar la empresa»
(§7.6).

**Municipio de la operación.** `Quotation` y `Order` ganan `OperationCityId` (`Guid?`), columna NUEVA `operation_city_id uuid null`
en las dos tablas, sin FK (como `city_id` de las partes, `QuotationsDbContext.cs:414`, `:888`).

- Al fijar o cambiar la empresa (`Quotation.UpdateDetails` → `ApplyBillingAccount`, `Quotation.cs:687`; `Order.SetBillingAccount`,
  `Order.cs:798`): si el cuerpo no trae `operationCityId`, pasa a ser `BillingAccount.CityId` (A6).
- Si el país de la empresa no tiene tipos municipales (`TaxCountries.MunicipalTypes` vacío): `null`, y lo que llegue se ignora (A7).
- Cuerpo con `operationCityId`: ciudad existente (`IQuotationGeographyLookup.FindCityNamesAsync`, ya existe) → si no, 422 NUEVO
  `quotation.billing.operation_city_invalid` / `order.billing.operation_city_invalid`. `null` explícito = «el de la empresa».
- Cotización: editable con `EnsureEditable`. Pedido: con `Order.IsEditable` (`order.order.not_editable`).
- Pedidos que nacen sin cotización (manual, tienda, Shopify/externo): `OperationCityId = BillingAccount.CityId` (R5, A10).
- Convertir: el pedido hereda `OperationCityId` (junto a la foto, `Order.cs:395`).

**Foto.** `WithholdingRate` gana `Guid? CityId` y `decimal CatalogPercentage` (NUEVOS). `Percentage` pasa a ser el **efectivo** y
es el que usa `DocumentTotals` (sin cambio de aritmética); `Overridden => Percentage != CatalogPercentage`. Columnas NUEVAS en
`quotation_withholdings` y `order_withholdings`: `city_id uuid null`, `catalog_percentage numeric(6,3) not null` (relleno =
`percentage`).

**Resolución de un tipo municipal `T`** (función de dominio NUEVA `MunicipalWithholdings.Resolve`, compartida por cotización y pedido,
junto a `WithholdingCandidates`). Entradas: las tasas municipales que la empresa ofrece para `(T, OperationCityId)` (0 o 1, R3) y si
el cliente es agente en `(país, T, OperationCityId)`. Sale la tasa a congelar o ninguna:

| Fuente | Regla |
| --- | --- |
| Cotización, datos del cliente (`Customer`) | la tasa de la empresa si existe **y** el cliente es agente ahí [owner R6] |
| Cotización, facturación propia (`Billing`) | lo que diga el selector de `T`, que sólo acepta la tasa de la empresa para ese municipio; vacío = ninguna [owner R11] |
| Pedido (`Document`), al nacer sin cotización | regla del cliente (tienda incluida: excepción de baseline D16) [owner R5, R6] |
| Pedido, edición (selector de `T`) | igual que facturación propia: la tasa de la empresa para ese municipio, **sea o no agente el cliente** (aplicación a mano) [owner R11] |

- Las tasas **nacionales** siguen exactamente como hoy (cliente / selectores / herencia).
- `IQuotationWithholdingLookup.ForCustomerAsync` devuelve sólo tasas **nacionales** (el cliente ya no puede tener municipales). Métodos NUEVOS: `ForCompanyMunicipalAsync(tenantId, companyId, country, cityId, documentDate)` → las tasas
  municipales asignadas a la empresa para ese municipio, ya como foto; `IsWithholdingAgentAsync(tenantId, customerId, country,
  type, cityId)`. El adaptador (`Bootstrapper/QuotationWithholdingLookup.cs`) suma `ICompanyRepository`.
- **Selector municipal** (`WithholdingSelection.ResolveAsync`, `Application/WithholdingSelection.cs`): para un tipo municipal, el id
  tiene que ser una tasa asignada a la empresa del documento con `CityId == OperationCityId`; si no →
  `{prefijo}.billing.withholding_tax_rate_invalid` (código existente). `WithholdingSelectorRules.Ensure` agrega la misma
  comprobación de municipio sobre la foto.
- **Candidatas** (`WithholdingCandidates.For`): una tasa municipal aplica sólo si `CityId == OperationCityId` del documento (paso
  nuevo, defensivo: la re-resolución ya lo garantiza). Toda fila municipal tiene `CityId`: no hay fotos ReteICA anteriores (§11).

**Respuesta.** El documento expone `operationCity` y `municipalWithholdings` (§9.4) con lo que la empresa ofrece por tipo y por qué
quedó o no una fila. El cálculo de esa explicación es del backend (BFF).

### 7.6 Re-resolución [owner R6; agente A5]

Corre **sólo** en estos cambios, con el documento editable:

1. Se fija o cambia la empresa facturadora: `CompanyId` distinto del guardado (cotización: `UpdateDetails`; pedido:
   `SetBillingAccount`). Reenviar la misma empresa, aun con otra cuenta, no cuenta.
2. Cambia `OperationCityId` (explícito o por A6), incluido de `null` a un municipio en un documento anterior a este trabajo (A8).
3. Cambia el cliente de la cotización (`ChangeClient`, `Quotation.cs:798-817`; hoy ya recopia la fuente `Customer`).

**Selecciones viejas en el mismo cuerpo (A11).** Cuando el cuerpo dispara 1 o 2, en ese mismo cuerpo se **ignoran** los ids de tipos
municipales de `billingWithholdingTaxRateIds` (cotización) y `withholdingTaxRateIds` (pedido) y las entradas de
`withholdingPercentages` de tipos municipales: se eligieron contra la empresa o el municipio anteriores. Los ids nacionales del mismo
cuerpo se procesan como hoy. El asesor ajusta la ReteICA en el guardado siguiente, ya con lo que devuelve la vista previa.

Para cada tipo municipal `T` del país de la empresa:

- Fuente `Customer` y pedido (`Document`): se aplica la regla del cliente de §7.5 con los datos **de ese momento** (asignaciones de
  la empresa, lista de agentes del cliente).
- Fuente `Billing`: si el selector de `T` tenía tasa, pasa a la de la empresa para el municipio nuevo, o queda vacío si la empresa no
  tiene; si estaba vacío, sigue vacío.
- Se reemplaza la fila de `T` en su posición (`DocumentWithholding.ReplaceSource`) o se quita; las nacionales no se tocan.
- Si la fila anterior tenía porcentaje editado (`Overridden`), se pierde y se anota en el historial (§7.8).
- Fuera de estos cambios la fila no se mueve: editar la tasa en el catálogo, las asignaciones de la empresa o los agentes del
  cliente **no** cambia documentos creados (baseline D17).

Si la empresa nueva es de un país sin tipos municipales, las filas municipales se quitan (y la fila de facturación propia también,
como hoy con D23).

### 7.7 Porcentaje editado [owner R7]

- Dominio NUEVO: `Quotation.SetWithholdingPercentage(type, percentage?, updatedBy, occurredAt)` y su gemelo en `Order`. Actúa sobre
  la fila de `type` de la **fuente vigente**. `null` = volver al del catálogo (`CatalogPercentage`).
- Reglas: el tipo es `Municipal` y hay una fila de ese tipo en la fuente vigente → si no, 422 NUEVO
  `quotation.billing.withholding_percentage_not_editable` / `order.billing.withholding_percentage_not_editable`; `0 < p ≤ 100` y a
  lo sumo 3 decimales → si no, 422 NUEVO `quotation.billing.withholding_percentage_invalid` /
  `order.billing.withholding_percentage_invalid`. Cotización con `EnsureEditable`; pedido con `Order.IsEditable`.
- Totales: `Touch` → `RecalculateTotals` (`Quotation.cs:1205-1240`), y lo equivalente en `Order` (`Order.cs:1692-1716`).
- Orden dentro de un guardado: cuenta → municipio → re-resolución → selectores → porcentajes. Si el cuerpo cambió empresa o
  municipio, los selectores y porcentajes de tipos municipales de ese cuerpo se ignoran (A11, §7.6).
- Al convertir, el pedido hereda la fila con su porcentaje efectivo y el del catálogo.

### 7.8 Historial

- `OrderHistoryEventType` y `QuotationHistoryEventType` ganan `WithholdingRateEdited` (NUEVO, al final del enum). Detalle con texto
  de `OrderChangeSummary` / `QuotationChangeSummary` (NUEVOS métodos):
  - edición: «Cambió la tarifa de ReteICA Bogotá de 0,966 % a 0,8 %.»; volver al catálogo: «Restableció la tarifa de ReteICA Bogotá
    a 0,966 %.»;
  - pérdida por re-resolución: «Se volvió a calcular la ReteICA por cambio de municipio o de empresa; se descartó la tarifa editada
    (0,8 %).»;
  - aplicación a mano (R11): en el pedido, el `PartiesChanged` de los selectores (`OrderChangeSummary.WithholdingsChanged`,
    `OrderChangeSummary.cs:95-96`) suma «; el cliente no está registrado como agente retenedor de ReteICA en {municipio}» cuando
    la tasa municipal elegida es para un municipio donde el cliente no es agente; en la cotización con facturación propia, el
    resumen del `Edited` dice «Aplicó ReteICA {municipio} por facturación propia»;
  - quitada por re-resolución (R11): «Se quitó la ReteICA aplicada a mano al cambiar la empresa o el municipio: el cliente no es
    agente retenedor ahí.» (`WithholdingRateEdited`).
- Cambiar el municipio de la operación: pedido, `PartiesChanged` con «Cambió el municipio de la operación de Bogotá, D.C. a
  Medellín.»; cotización, el `Edited` del guardado con el mismo texto en su resumen [agente].
- Auditoría NUEVA: `quotation.quotation.withholding_rate_edited` y `quotation.order.withholding_rate_edited` (prefijo de
  `quotation.order.withholdings_changed`, `SaveOrderEdits.cs:297`).

### 7.9 PDF: porcentaje de cada retención [owner R13]

Hoy el PDF de cotización y el de pedido salen del mismo documento: `QuotationPdfProvider.cs:69` y `OrderPdfProvider.cs:48` llaman a
`QuotationPdfDocumentMapper.From` / `.FromOrder`, que arma las filas con `WithholdingsFor` (`QuotationPdfDocumentMapper.cs:130-133`,
nombre y monto de cada retención aplicada). `QuotationPdfWithholding(string Name, decimal Amount)` (`QuotationPdfDocument.cs:110`) llega
a la plantilla Typst, que imprime `retencion.name + ":"` y el monto (`Infrastructure/Pdf/quotation.typ:422-426`); la renderiza
`QCodePdfRenderer` (`Infrastructure/Pdf/QCodePdfRenderer.cs`).

- `QuotationPdfWithholding` gana `string PercentageLabel` (NUEVO): el porcentaje **efectivo** (`WithholdingResponse.Percentage`) con
  el separador decimal del formato numérico del tenant que el mapper ya recibe (`numberFormat`), sin ceros de sobra y hasta 3
  decimales, más « %»: `0,966 %`, `2,5 %`, `4 %` [agente: el mapper ya formatea para la plantilla y Typst no sabe del tenant].
- `quotation.typ`: la etiqueta de la fila pasa a `retencion.name + " " + retencion.percentageLabel + ":"` → «ReteICA Bogotá 0,966 %:».
- Vale para **toda** fila de retención, de cualquier tipo, editada o no [agente, R13]. El porcentaje del catálogo de una tasa editada
  no se imprime: el cliente comprueba el monto con el efectivo.
- Pruebas: `QuotationPdfDocumentMapperTests` y `OrderPdfDocumentMapperTests` (`tests/Modules/Quotations/Modules.Quotations.UnitTests/`):
  `PercentageLabel` con separador del tenant (`0,966 %` con coma, `0.966 %` con punto), `2,5 %` sin cero de más, porcentaje editado
  (0,8) en vez del catálogo; `QuotationTemplateTests`: la fila dice «ReteICA Bogotá 0,966 %:» y una Retefuente «Retefuente 2,5 %
  2,5 %:» (el nombre lo pone el tenant; no se deduplica [agente]).

### 7.10 Lo que no cambia

- Retefuente: asignación, selectores, foto y cálculo.
- Excel de pedidos: la columna «ReteICA» (`OrdersExportColumnCatalog.cs:178`) sigue con el monto aplicado.
- `retentionAmount`, `netTotal` y `withholdings` en las respuestas: misma forma, más campos (§9).

## 8. Modelo de datos (resumen)

| Esquema.tabla | Cambio | Módulo |
| --- | --- | --- |
| `catalog.tax_rates` | NUEVA `city_id uuid null` (sin FK), índice `(tenant_id, type, city_id)` | Catalog |
| `customers.customer_withholding_agent_cities` | NUEVA, FK `city_id` → `geography.cities` | Customers |
| `companies.company_withholding_tax_rates` | NUEVA | Companies |
| `quotations.quotations`, `quotations.orders` | NUEVAS `operation_city_id uuid null`, `billing_company_city_id uuid null` | Quotations |
| `quotations.quotation_withholdings`, `quotations.order_withholdings` | NUEVAS `city_id uuid null` (sólo filas nacionales la dejan en null), `catalog_percentage numeric(6,3) not null` (relleno = `percentage`) | Quotations |

## 9. Contrato HTTP

Todo lo NUEVO es aditivo; ningún campo existente cambia de nombre ni de significado, salvo `percentage` de las retenciones de un
documento, que ya era «el que se usó» y ahora puede diferir del catálogo.

### 9.1 Tasas de impuesto (`/api/v1/tenants/{tenantId}/catalog`, `TaxRateEndpoints.cs`)

Respuesta de cada tasa (`TaxRateResponse`):

```json
{
  "id": "6f1c…", "name": "ReteICA Bogotá", "percentage": 0.966, "isActive": true,
  "type": "ReteICA", "country": "CO", "behavior": "Withholds", "scope": "Municipal",
  "minimumBase": 4, "minimumBaseUnit": "UVT",
  "cityId": "a1b2…",
  "city": { "id": "a1b2…", "name": "Bogotá, D.C.", "departmentId": "c3d4…", "departmentName": "Bogotá, D.C." },
  "createdAt": "…", "updatedAt": "…"
}
```

NUEVOS: `scope` (`"National" | "Municipal"`), `cityId` (`string | null`; `null` sólo en tasas nacionales), `city` (`null` en una
tasa nacional o si la ciudad no se encontró). La forma `{ id, name, departmentId, departmentName }` es la **referencia de municipio** de todo este spec.

- `POST …/tax-rates`: NUEVO `cityId`. Obligatorio si el tipo es municipal, prohibido si es nacional.
- `PUT …/tax-rates/{id}`: `cityId` se ignora (A1).
- `GET …/tax-rate-options`: cada tipo gana `scope`:

  ```json
  { "type": "ReteICA", "behavior": "Withholds", "allowsFraction": true, "allowsMinimumBase": true, "scope": "Municipal" }
  ```

### 9.2 Empresas (`/api/v1/tenants/{tenantId}/companies`, `CompanyEndpoints.cs`)

- `POST /` y `PUT /{companyId}`: NUEVO `withholdingTaxRateIds: string[] | null`. Ausente o `null` = conservar (en el alta, ninguna);
  `[]` = quitar todas. Permiso existente `companies.company.manage`.
- `GET /{companyId}`, y la respuesta de `POST`/`PUT`/`activate`/`deactivate`: NUEVO

  ```json
  "withholdingTaxRates": [
    { "id": "…", "name": "ReteICA Bogotá", "type": "ReteICA", "country": "CO", "percentage": 0.966, "isActive": true,
      "city": { "id": "…", "name": "Bogotá, D.C.", "departmentId": "…", "departmentName": "Bogotá, D.C." } }
  ]
  ```

  Ordenadas por departamento, municipio y tipo [agente]. `GET /` (listado) no cambia.

### 9.3 Clientes (`CustomerEndpoints.cs`)

- `withholdingTaxRateIds` (crear/editar, `CustomersDtos.cs:277`, `:298`): sólo tasas nacionales; una municipal → 422
  `customers.customer.withholding_tax_rate_municipal` (A15).
- NUEVO en el cuerpo de crear/editar: `withholdingAgentCities: [{ "type": "ReteICA", "country": "CO", "cityId": "…" }] | null`.
  Ausente o `null` = conservar (en el alta, ninguna); `[]` = quitar.
- NUEVO en la respuesta de la ficha (`CustomerDto`, `CustomersDtos.cs:95-108`):

  ```json
  "withholdingAgentCities": [
    { "type": "ReteICA", "country": "CO",
      "city": { "id": "…", "name": "Medellín", "departmentId": "…", "departmentName": "Antioquia" } }
  ]
  ```

  `withholdingTaxRates` sigue (sólo nacionales). El listado no lo trae [agente].

### 9.4 Cotizaciones y pedidos

En `QuotationDto`/`QuotationResponse` (`QuotationsDtos.cs`) y el detalle del pedido (`OrdersDtos.cs`), y en todas sus vistas previas
(`POST /quotations/preview`, `POST /quotations/{id}/preview`, `POST /orders/preview`, `POST /orders/{id}/preview`):

```json
{
  "operationCity": { "id": "…", "name": "Bogotá, D.C.", "departmentId": "…", "departmentName": "Bogotá, D.C." },
  "municipalWithholdings": [
    {
      "type": "ReteICA",
      "country": "CO",
      "companyRate": { "taxRateId": "…", "name": "ReteICA Bogotá", "percentage": 0.966 },
      "status": "Resolved"
    }
  ],
  "withholdings": [
    { "taxRateId": "…", "name": "ReteICA Bogotá", "type": "ReteICA", "country": "CO",
      "percentage": 0.8, "catalogPercentage": 0.966, "percentageOverridden": true, "cityId": "…",
      "minimumBase": null, "minimumBaseUnit": null, "taxUnitValue": null, "base": 1000000, "amount": 8000 }
  ],
  "customerWithholdingTaxRates": [ … ],
  "billingWithholdingTaxRates": [ … ]
}
```

- NUEVO `operationCity`: `null` sin empresa, con empresa de un país sin tipos municipales, o en documentos anteriores (A8).
- NUEVO `billingCompanyCity` (A12): referencia de municipio desde `billing_company_city_id`; `null` sin empresa, fuera de CO o en un
  documento anterior que todavía no reenvió la cuenta. Es lo que «Usar el de la empresa» compara con `operationCity`.
- NUEVO `municipalWithholdings`: una entrada por tipo municipal del país de la empresa (vacío sin empresa o sin tipos municipales).
  `companyRate` = la tasa que la empresa tiene **hoy** para ese tipo en el municipio de la operación, o `null`. `status` se
  **deriva al leer** (A14, sin columna nueva), con las asignaciones de hoy de la empresa y la lista de agentes de hoy del cliente; no
  mueve la foto. Precedencia, la primera que se cumple:
  1. `Resolved`: hay una fila de ese tipo en la fuente vigente (aplique o no por base mínima). Incluye la tasa aplicada a mano en
     un pedido para un cliente que no es agente ahí (R11): `customerIsAgent` lo dice.
  2. `NoOperationCity`: no hay fila y falta el municipio de la operación.
  3. `NoCompanyRate`: no hay fila y la empresa no tiene tasa de ese tipo en ese municipio.
  4. Sin fila, con municipio y con tasa de la empresa:
     - cotización con facturación propia (`Billing`): `NotSelected` (el tercero no tiene lista de agentes);
     - cotización con datos del cliente y pedido: `CustomerNotAgent` si el cliente **no** es agente ahí; `NotSelected` si lo es (en
       el pedido, el asesor dejó el selector vacío; en la cotización, el cliente pasó a ser agente después de resolverse).
  Consumidor final, moneda y falta de empresa los sigue explicando lo de hoy (no son de este bloque).
- Cada entrada gana `customerIsAgent: boolean | null` (R11): si el cliente está hoy registrado como agente de ese tipo en el
  municipio de la operación; `null` en una cotización con facturación propia (el tercero no tiene lista) o sin municipio. Con
  `status = Resolved` y `customerIsAgent = false` el frontend explica que la tasa se aplicó a mano.
- `withholdings[]` gana `catalogPercentage`, `percentageOverridden`, `cityId`. `percentage` es el efectivo.
- `customerWithholdingTaxRates[]` y `billingWithholdingTaxRates[]` (`WithholdingTaxRateDto`, `QuotationsDtos.cs:148`) ganan
  `catalogPercentage`, `percentageOverridden`, `cityId`: el editor de porcentaje lee la fila aunque no haya aplicado.
- Cuerpos (cotización: `CreateQuotationRequest`, `SaveQuotationRequest` y vistas previas; pedido: `SaveOrderEditsRequest` y su
  vista previa) NUEVOS:
  - `operationCityId: string | null` — ausente = conservar (o el de la empresa si cambia, A6); `null` = el de la empresa.
  - `withholdingPercentages: [{ "type": "ReteICA", "percentage": 0.8 | null }]` — ausente = conservar todos; `null` en una
    entrada = volver al del catálogo.
  - Si el cuerpo cambia la empresa o el municipio, los ids municipales de los selectores y las entradas municipales de
    `withholdingPercentages` se ignoran (A11).
- `CreateOrderRequest` (pedido manual) no cambia (A10).
- `billingWithholdingTaxRateIds` (cotización) y `withholdingTaxRateIds` (pedido) no cambian de forma: para un tipo municipal sólo
  aceptan la tasa de `municipalWithholdings[].companyRate` (A4).
- Historial: `eventType` puede valer `"WithholdingRateEdited"` en las dos líneas de tiempo.

## 10. Códigos de error NUEVOS

| Código | Cuándo |
| --- | --- |
| `catalog.tax_rate.city_required` | tasa de tipo municipal sin `cityId` |
| `catalog.tax_rate.city_not_allowed` | tasa de tipo nacional con `cityId` |
| `catalog.tax_rate.city_invalid` | `cityId` que no existe en Geography, o país distinto de `CO` |
| `companies.company.withholding_tax_rate_invalid` | id inexistente, que no retiene, nacional o de otro país que la empresa; inactivo sólo si no estaba ya guardado (A16) |
| `companies.company.withholding_municipality_repeated` | dos tasas del mismo tipo y municipio |
| `customers.customer.withholding_tax_rate_municipal` | `withholdingTaxRateIds` trae una tasa de tipo municipal (A15) |
| `customers.customer.withholding_agent_type_invalid` | `type` que no es municipal en ese `country` |
| `customers.customer.withholding_agent_city_invalid` | `cityId` inexistente o país distinto de `CO` |
| `quotation.billing.operation_city_invalid` / `order.billing.operation_city_invalid` | `operationCityId` inexistente |
| `quotation.billing.withholding_percentage_invalid` / `order.billing.withholding_percentage_invalid` | `p <= 0`, `p > 100` o más de 3 decimales |
| `quotation.billing.withholding_percentage_not_editable` / `order.billing.withholding_percentage_not_editable` | tipo no municipal, o sin fila de ese tipo en la fuente vigente |

Existentes que se reutilizan: `catalog.tax_rate.in_use` (ahora también por empresas), `quotation.billing.withholding_tax_rate_invalid`
y `order.billing.withholding_tax_rate_invalid` (selector municipal con una tasa que no es la de la empresa para ese municipio),
`quotation.quotation.not_editable`, `order.order.not_editable`.

## 11. Migración

### 11.1 Esquema (expansión)

En el orden de `Program.cs`: Catalog (`tax_rates.city_id`), Customers (`customer_withholding_agent_cities`), Companies
(`company_withholding_tax_rates`), Quotations (`operation_city_id`, `billing_company_city_id`, `city_id`, `catalog_percentage` con
`UPDATE … SET catalog_percentage = percentage` antes del `NOT NULL`).

### 11.2 Sin migración de datos

No hay datos que migrar [owner R9 sin efecto]. Hechos (2026-11-12): el merge de impuestos por tipo del backend (`8052ee2f`) no está en
ninguna rama remota, el del frontend (`8a1368c9`) está en `origin/develop` pero no en `main`, y producción no tiene tipos de impuesto; la única migración de datos de ese trabajo,
`MigrateRetentionToWithholdings.cs:22-86`, crea y asigna sólo «Retefuente 2,5 %». Por eso al desplegar no hay tasas ReteICA, ni
asignaciones ReteICA a clientes, ni filas ReteICA congeladas, y este spec no tiene estado `Legacy`, ni tolerancia a asignaciones
municipales de clientes, ni tasas municipales sin municipio. Lo que sí cubre: los documentos existentes quedan con
`operation_city_id` y `billing_company_city_id` en `null` (A8, A12). Una base de `develop` local con ReteICA de prueba se limpia a mano;
no se escribe código para ella. Lo garantiza R12 (§12).

### 11.3 Contrato (release siguiente)

Nada que borrar en este trabajo: no hay campos derivados nuevos. La columna «ReteICA» del Excel de clientes ya no se lee (A3).

## 12. Despliegue

**Despliegue conjunto** [owner R12]: este trabajo sale **junto** con «impuestos por tipo», en el mismo despliegue a producción. El
frontend de impuestos por tipo (`8a1368c9`) ya está en `origin/develop`, no en `main`; el backend (`8052ee2f`) no está publicado. Hasta
mergear este trabajo no se lleva `develop` a `main` en ninguno de los dos repos ni se publica `develop` del backend: si saliera antes, un
tenant podría asignar ReteICA a sus clientes o congelarla en documentos con el modelo del baseline, y este spec no migra ni tolera
esos datos.

1. **Backend** (migraciones del baseline y de §11.1). El frontend «viejo» es el de **producción**, que no conoce tipos de impuesto,
   retenciones por tasa ni empresas con retenciones; la compatibilidad que importa es la del baseline (sus §9 y §12: campos
   aditivos, `withRetention`/`billingWithRetention` derivados e ignorados en los cuerpos). Lo de este trabajo es aditivo para ese
   frontend:
   - No crea tasas con tipo (el alta sin `type` es IVA, baseline §9.1), así que nunca crea una ReteICA ni ve `cityId`.
   - El `PUT` de empresa no manda `withholdingTaxRateIds` y conserva lo asignado; el de cliente no manda `withholdingTaxRateIds` ni
     `withholdingAgentCities` y los conserva.
   - Cotizaciones y pedidos: no manda `operationCityId` (rige el de la empresa, A6) ni `withholdingPercentages`; los campos nuevos de
     la respuesta se ignoran. Si hay retención municipal, `retentionAmount` y `netTotal` ya la incluyen, como en el baseline.
   - **Corte duro**, como el baseline: el binario viejo no conoce `catalog_percentage` ni la resolución municipal; se reemplazan
     todas las instancias a la vez.
2. **Frontend** (baseline y este trabajo juntos).

## 13. Pruebas (TDD: RED antes que GREEN)

Unitarias de dominio:

- `TaxCountries`: IVA y Retefuente `National`, ReteICA `Municipal`; `MunicipalTypes("CO")` = `[ReteICA]`; país desconocido → vacío.
- `TaxRate.Create`: ReteICA sin ciudad → `city_required`; Retefuente con ciudad → `city_not_allowed`; ReteICA con ciudad pasa.
  `Update` ignora `cityId`.
- `Company.SetWithholdingTaxRates`: igualdad no sube versión; reemplaza.
- `WithholdingRate`: `Overridden` falso con porcentajes iguales.
- `MunicipalWithholdings.Resolve`: empresa con tasa + cliente agente → fila; sin tasa → `NoCompanyRate`; cliente no agente →
  `CustomerNotAgent`; sin municipio → `NoOperationCity`; selector vacío → `NotSelected`.
- `WithholdingCandidates.For`: municipal con `CityId` distinto del municipio de la operación no aplica; nacional igual que hoy.
- `Quotation`: fijar la empresa pone el municipio de la operación en el de la empresa; cambiar la empresa lo reinicia salvo que venga
  explícito; cambiar el municipio re-resuelve y pierde el porcentaje editado; `ChangeClient` re-resuelve con los agentes del cliente
  nuevo; facturación propia con selector municipal se mueve a la tasa del municipio nuevo o se vacía; `SetWithholdingPercentage`:
  0 → `percentage_invalid`, 0,8125 (4 decimales) → `percentage_invalid`, 100 pasa, 100,001 → `percentage_invalid`, Retefuente →
  `percentage_not_editable`, sin fila → `percentage_not_editable`, `null` restaura; Sent editable, Converted → `not_editable`;
  `NetTotal` usa el porcentaje efectivo.
- `Order`: convertir hereda municipio, filas y porcentaje editado; pedido de la tienda y manual nacen con el municipio de la empresa
  y la regla del cliente; preparado o facturado rechaza municipio y porcentaje con `order.order.not_editable`.

Integración (Testcontainers, un proyecto a la vez):

- Catalog: `POST` ReteICA con ciudad → respuesta con `city`; sin ciudad → 422; ciudad inexistente → `city_invalid`;
  `tax-rate-options` con `scope`; desactivar o borrar una tasa asignada sólo a una empresa → `catalog.tax_rate.in_use`.
- Companies: asignar ReteICA Bogotá y ReteICA Medellín pasa; dos en Bogotá → `withholding_municipality_repeated`; Retefuente →
  `withholding_tax_rate_invalid`; `PUT` sin `withholdingTaxRateIds` conserva; `GET` con `withholdingTaxRates` y su `city`.
- Customers: ReteICA en `withholdingTaxRateIds` → `withholding_tax_rate_municipal` (A15); agentes con tipo `Retefuente` →
  `withholding_agent_type_invalid`; editar sin `withholdingAgentCities` conserva; Excel sin columna «ReteICA» y archivo viejo con
  ella importa ignorándola.
- Quotations: cotización con empresa en Bogotá y cliente agente en Bogotá → fila ReteICA aplicada; mismo cliente con municipio de
  operación Medellín y empresa sin tasa ahí → sin fila y `status = NoCompanyRate`; editar porcentaje → totales e historial
  `WithholdingRateEdited`; vista previa devuelve `operationCity` y `municipalWithholdings`; pedido de la tienda y webhook de Shopify
  siguen creando pedidos (regresión).
- Companies (A16): `PUT` que reenvía una tasa ya guardada e inactiva → 200 y se conserva; una inactiva nueva →
  `withholding_tax_rate_invalid`.
- Aplicación a mano (R11): en la edición de un pedido, elegir la ReteICA de la empresa para un municipio donde el cliente no es
  agente → 200, fila `Resolved` con `customerIsAgent = false` e historial; cambiar después el municipio la quita con la regla del
  cliente y lo anota. En la cotización con facturación propia (`customerIsAgent = null`) la aplicación es igual, pero cambiar
  después el municipio pasa la selección a la tasa de la empresa para el municipio nuevo (o la deja vacía si no hay), §7.6
  (owner, 2026-11-13).
- Selecciones viejas (A11): un `PUT` de cotización que cambia la empresa y trae un selector ReteICA y un porcentaje ReteICA → el
  selector y el porcentaje se ignoran, la fila sale de la re-resolución y el historial anota la tarifa editada perdida; la
  Retefuente del mismo cuerpo sí se aplica. Igual en la edición del pedido y en las dos vistas previas.
- `billingCompanyCity` (A12): presente con empresa CO; `null` en un documento anterior hasta reenviar la cuenta; reenviar la misma
  empresa lo completa sin re-resolver.
- Documento anterior (A8): sin `operation_city_id` da `NoOperationCity`; fijarlo en uno editable resuelve la ReteICA con la regla.
- `status` (A14): cada rama de la precedencia, en cotización (cliente y facturación propia) y en pedido; cambiar después los agentes
  del cliente cambia el `status` leído pero no la foto ni los totales.
- `ArchitectureTests`: Catalog, Customers, Companies y Quotations siguen sin referenciarse; `CompositionRootTests` sigue en 80.

## 14. Decisiones pendientes del owner

1. **Ley**: confirmar el literal «sin establecimiento, donde se perfecciona la venta» (Ley 14 de 1983 / Ley 1819 de 2016 art. 343).
   Sólo afecta la copia de ayuda del frontend: el municipio de la operación se elige a mano.

Resueltas el 2026-11-12: A4/A5 (R11), despliegue conjunto (R12), porcentaje en el PDF (R13), A3 (R14), A6 (R15), A10 (R16).
Retiradas por no haber datos ReteICA (§11.2): tenants ambiguos, asignaciones ReteICA de clientes no migradas, «fijar una vez» de A1.

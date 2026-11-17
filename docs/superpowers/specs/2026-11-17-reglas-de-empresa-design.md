# Reglas de empresa: cuenta de cobro opcional e IVA por empresa

Estado: borrador para aprobación del owner (2026-11-17). Decisiones del owner del 2026-11-16/17; lo marcado
**(criterio)** lo decidió quien escribe, con el owner fuera de línea, y se puede revertir en la revisión.

Es el trabajo 1 de 3 del onboarding de Shopify (orden acordado: 1 reglas de empresa → 2 productos desde Shopify →
3 onboarding de la tienda). Retoma §9 del spec `2026-10-21-onboarding-design.md`, que sigue siendo la referencia del
flujo; este spec cubre sólo el dominio que el onboarding necesita. Mockup: https://claude.ai/artifact/5XMmGydnUSrL9HGZoMxZZH.

## 1. Por qué

Un comercio que instala la app desde Shopify tiene que dejar su tienda funcionando sin ayuda. Hoy se traba en dos
reglas de dominio que no tienen que ver con Shopify:

- **La cuenta bancaria es obligatoria.** Una empresa necesita al menos una (`companies.company.bank_accounts_required`,
  `Company.cs:327-332`), un pedido no existe sin cuenta de cobro (`Order.cs:414-418`, `:498`, `:566`, `:602`;
  `QuotationsDbContext.cs:722`) y la tienda de Shopify tampoco se configura sin ella (`ShopifyStores.cs:53-57`). Una
  empresa nueva no tiene cuenta, y el owner prevé cobros por tarjeta o pasarela donde la cuenta no aplica.
- **No hay forma de decir «no cobro IVA».** Hay empresas (régimen simple, no responsables) que no cobran IVA. Hoy cada
  línea toma la tasa del producto y no existe interruptor por empresa ni por tenant. El único que existe, el excedente
  de IVA del cliente (`DocumentTotals.cs:53-55`), no sirve: deja el total en la base sin IVA, o sea cobra menos.

## 2. Objetivos, no objetivos, anti-objetivos

**Objetivos**
1. Una empresa puede existir sin cuentas bancarias.
2. Un pedido y una cotización se emiten con la empresa y **sin** cuenta. Sin cuenta se muestran «Pago: por definir».
3. Cada empresa dice si cobra IVA. Lo que emite una empresa que no cobra IVA lleva 0 % en las líneas y en el envío, y
   el cliente paga el mismo precio de lista.
4. Una empresa que sí cobra IVA y es de Colombia tiene listas las tasas IVA 19 %, IVA 5 % y Exento, y el IVA del envío,
   sin configurarlas a mano.

**No objetivos**
- La pantalla del onboarding y el prellenado desde Shopify (trabajo 3). Este spec sólo deja el dominio y la API listos.
- La importación de productos y S-4 (`taxesIncluded=false`) (trabajo 2).
- Cobro por tarjeta o pasarela. Sólo se deja de exigir la cuenta.
- Facturación electrónica o reglas fiscales más allá de cobrar o no IVA.

**Anti-objetivos** (lo que lo haría fallar aunque compile)
- Que una empresa sin IVA cobre menos que el precio de lista: el precio de lista es lo que paga el cliente.
- Que un documento ya emitido cambie de IVA porque la empresa cambió su configuración después.
- Que un pod viejo durante el rollout lea un pedido sin cuenta y responda 500 sin que el despliegue lo haya previsto.

## 3. Conceptos

- **Organización** = tenant. **Empresa** = `Company`, cada razón social (NIT) con la que la organización factura.
- **Cuenta de cobro** = `QuotationBillingAccount` en Quotations (`CompanyId`, banco, número, moneda, país, ciudad). Hoy
  mezcla dos cosas: **quién emite** (la empresa) y **dónde se paga** (la cuenta). Este spec las separa en el
  significado sin renombrar el contrato: la empresa sigue siendo obligatoria para emitir; banco y número pasan a ser
  opcionales. El estado intermedio que hoy existe sólo en previews (`IsPending`: empresa elegida, sin cuenta) pasa a ser
  un estado guardable.

## 4. Cuenta de cobro opcional

### 4.1 Companies
- `Company` acepta 0 cuentas: se quita el mínimo en `NormalizeBankAccounts` y el `NotEmpty` de `CompanyWriteRules`
  (`:721-723`). El máximo de 20, el catálogo de monedas y los duplicados siguen igual.
- El XML doc «Al menos una» se corrige.

### 4.2 Quotations
- **Cotización:** enviar y convertir exigen la **empresa** emisora, no la cuenta. Hoy `EnsureSendable`,
  `EnsureConvertibleToOrder`, `CanBeConvertedToOrder` y la bandera de listo de `QuotationMapping` miran
  `HasBillingAccount` («no pendiente»); pasan a mirar «tiene empresa». El código `quotation.billing.account_required`
  se conserva para «no tiene empresa» **(criterio:** no inventar un código nuevo para la misma pregunta que la pantalla ya
  sabe traducir).
- **Pedido:** `FromQuotation`, `CreateManual`, `CreateExternal` y `CreateFromStore` aceptan una cuenta con empresa y sin
  banco ni número. Siguen exigiendo empresa: `order.billing.account_required` cuando falta la empresa.
- **Persistencia:** en `quotations.orders`, `bank_name` y `account_number` pasan a nullable (en `quotations.quotations`
  ya lo son por ser `OwnsOne` opcional; verificar). `billing_company_id` y `account_currency` siguen `NOT NULL`. Los
  «pendientes» que hoy se rechazan al guardar (`QuotationsDbContext.cs:74-89`) se aceptan.
- **Moneda sin cuenta:** la cuenta ya no es la fuente de la moneda. Sin cuenta, la moneda del documento es:
  pedido manual o cotización → la que ya resuelve la regla de hoy sin cuenta (moneda por defecto del tenant,
  `Order.cs:104`, `Quotation.cs:90`); pedido externo → la del pedido de origen (Shopify: `snapshot.Currency`); pedido de
  la tienda → la moneda de la tienda en la que se compró. `account_currency` guarda esa moneda también sin cuenta, para
  no tocar la columna.
- **Cambiar la cuenta de un pedido** (`ApplyBillingContext`): se puede pasar de «sin cuenta» a una cuenta de la misma
  moneda y de una cuenta a «sin cuenta» **(criterio:** si se puede quitar en la empresa, se puede quitar en el pedido;
  la regla de moneda distinta, `order.billing.currency_change_not_allowed`, sigue igual).
- **Quién lee la cuenta y qué hace sin ella:**
  - PDF de cotización: sin cuenta no pinta el bloque de pago; el membrete de la empresa sigue.
  - Excel del ERP (`OrdersExportProcessor.cs:165-167`, `:294-302`): sin cuenta, las celdas `payment_method_N` del
    banco y número van vacías. El `!` de «un pedido guardado siempre tiene cuenta» se quita. El Excel ignora el formato
    regional a propósito (D-R8): no cambia.
  - DTOs (`OrderMapping`, `QuotationMapping`, composers): `bankName` y `accountNumber` pasan a `null`. La cuenta del DTO
    sigue presente con la empresa. **El texto «Pago: por definir» lo pinta el frontend**, que tiene el diccionario.
  - Retenciones y municipio (`BillingAccount.Country`/`CityId`) salen de la empresa, no de la cuenta: no cambian.
  - `ShopifyOrderWriter`, `StoreOrdersAdapter`, `ExportLoadSeeder` (SQL crudo): aceptan banco y número nulos.

### 4.3 Store (tienda pública)
- Cada moneda de la tienda sigue eligiendo su **empresa**; la cuenta pasa a opcional (`StoreBillingAccount` con banco y
  número nullable, el `CHECK` de moneda se conserva).
- El comprador ve el pedido sin instrucciones de pago cuando no hay cuenta **(criterio:** el texto de la tienda dice
  «Te contactaremos para coordinar el pago», y vive en el storefront; el backend sólo deja los campos en `null`).

### 4.4 Shopify
- La configuración de la tienda exige la **empresa** y deja banco y número opcionales. Con cuenta, la moneda de la
  cuenta tiene que ser la de la tienda (regla de hoy). Sin cuenta, la moneda sale del pedido.
- El importador: `billing_account_missing` sólo cuando la tienda tiene cuenta configurada y ya no existe en la empresa;
  con la tienda sin cuenta, el pedido entra sin cuenta. `currency_mismatch` sólo aplica con cuenta.
- `ShopifyStore.IsConfigured` = asesor y **empresa**, no cuenta.

## 5. IVA por empresa

### 5.1 El dato
- `Company.ChargesVat` (bool, `companies.companies.charges_vat`, `NOT NULL DEFAULT true`). Toda empresa existente
  queda en `true`: nada cambia para quien ya opera.
- Se escribe en crear y editar empresa (`chargesVat` en el cuerpo; ausente = `true` para que un frontend viejo no apague
  el IVA sin querer). Lo devuelven el detalle y la lista.

### 5.2 Cómo se aplica
- **Regla:** una línea emitida por una empresa que no cobra IVA calcula con 0 %: `VatIncludedLine.Compute` con tasa 0
  deja `TaxAmount = 0` y `Subtotal = total de la línea`, así que el cliente paga el precio de lista. El envío también va
  con 0 % y nunca da `freight_tax_rate_missing`.
- **La foto del documento:** el documento congela `charges_vat` de su empresa emisora junto con la cuenta
  (`quotations.quotations` y `quotations.orders`, `charges_vat NOT NULL DEFAULT true`). Cambiar después la empresa no
  cambia documentos emitidos (anti-objetivo).
- **La tasa del producto se conserva en la línea** (la foto de hoy). Lo efectivo es `charges_vat ? tasa del producto :
  0`. Así, cambiar la empresa emisora de una cotización en borrador recalcula sin volver a Catalog.
  **(criterio, a confirmar en el plan:** si las líneas guardan importes calculados, el agregado los recalcula al
  cambiar la empresa emisora, por el mismo camino que `QuotationPricingRecalculation`; si sólo guardan la tasa, basta
  con aplicar la efectiva al calcular.)
- **Dónde se conoce la empresa emisora:**
  - Cotización y pedido: `BillingAccount.CompanyId`. Una cotización sin empresa todavía calcula con IVA (es lo que hace
    hoy) y se recalcula al elegirla.
  - POS: `CashSession.CompanyId` y su `PosCompanySnapshot`, que gana `ChargesVat` congelado al abrir la caja. Las líneas
    de una venta de esa caja van con 0 %; la vista previa también, para que `ExpectedTaxPercentage` coincida y no salga
    `pos.sale.price_changed`.
  - Tienda pública: la empresa de la moneda de la tienda; `QuoteForStore` y `CreateFromStore` aplican la regla.
  - Shopify: la empresa de la tienda; las líneas y el envío del pedido externo aplican la regla.
- **Compatibilidad con el excedente de IVA del cliente:** si los dos aplican, gana «no cobra IVA» (líneas ya en 0 %, el
  excedente no tiene qué quitar). El excedente sigue como hoy para empresas que cobran IVA.

### 5.3 Tasas por defecto para Colombia
- Al crear una empresa con `chargesVat = true` y país `CO`, o al pasarla a `true`, si el tenant no tiene ninguna tasa
  de IVA activa de `CO`, se crean **IVA 19 %**, **IVA 5 %** y **Exento (0 %)**, de tipo IVA y país `CO`.
- Si el tenant no tiene tasa de IVA del envío (`freight_tax_rate_id`), se fija la de 19 % recién creada o, si ya
  existían tasas, la activa de `CO` con 19 % si la hay.
- Es idempotente y corre **después** de confirmar la empresa (Companies no ve Catalog ni Quotations: puerto en
  Companies.Application y adaptador en Bootstrapper, como la validación de ReteICA). Un fallo se loguea y no deshace la
  empresa **(criterio:** la empresa es lo que el usuario pidió; las tasas se pueden crear después a mano y el trabajo 3
  vuelve a pedirlas).
- Otros países: no se siembra nada.

## 6. Contrato (cambios)

- `POST/PUT …/companies`: `bankAccounts` acepta `[]`; nuevo `chargesVat` (bool, opcional, default `true`).
  `GET …/companies` y detalle devuelven `chargesVat`.
- Pedido y cotización (detalle, lista, PDF): `billingAccount.bankName` y `billingAccount.accountNumber` pueden ser
  `null`; nuevo `billingAccount.chargesVat`. Pedido manual: `billingAccount` acepta empresa sin banco ni número.
- Shopify `PUT …/shopify/stores/{id}`: `billingBankName` y `billingAccountNumber` opcionales (los dos o ninguno).
- Store: cada moneda acepta empresa sin cuenta (los dos campos o ninguno).
- Errores nuevos: ninguno. **Errores que dejan de salir:** `companies.company.bank_accounts_required` y, sin cuenta,
  `billing_account_missing`/`currency_mismatch` de Shopify y `freight_tax_rate_missing` para empresas sin IVA.

## 7. Despliegue

- **Migraciones:** `Companies` (columna `charges_vat`), `Quotations` (`charges_vat` en cotizaciones y pedidos; `bank_name`
  y `account_number` de pedidos a nullable), `Store` (banco y número nullable), `Shopify` (banco y número de la tienda
  nullable), `Pos` (`charges_vat` en la foto de la empresa de la caja). Las columnas nuevas son aditivas con default.
- **Relajar `NOT NULL` no es aditivo para el código viejo:** un pod viejo que lea un pedido sin cuenta lo materializa
  sin `BillingAccount` y el Excel del ERP cae en el `!`. Mientras no se cree ningún documento sin cuenta, los pods viejos
  no ven nulls. **(criterio:** desplegar con corte —`Recreate` o el backend a cero réplicas—, porque son cinco módulos
  y la ventana es corta; el handoff lo dice.)
- **Frontend:** necesita su propio spec (repo `qep-frontend`): empresa sin cuentas y el interruptor «Cobra IVA», «Pago:
  por definir» en pedidos y cotizaciones, tienda pública sin instrucciones de pago, configuración de Shopify con cuenta
  opcional. Hasta que llegue, el backend tiene que funcionar con el frontend de hoy: `chargesVat` ausente = `true`, y un
  frontend viejo sigue mandando cuentas.
- `docs/estado.md` se actualiza en `develop` al mergear.

## 8. Verificación

- **Dominio:** empresa sin cuentas; pedido manual, externo y de tienda sin cuenta; cotización enviada y convertida sin
  cuenta; cambiar de «sin cuenta» a cuenta y al revés; línea con empresa sin IVA da `TaxAmount = 0` y total = precio de
  lista; envío sin IVA; excedente más «sin IVA».
- **Integración:** Excel del ERP con un pedido sin cuenta; PDF sin bloque de pago; importación de Shopify sin cuenta y
  con empresa sin IVA (sin `freight_tax_rate_missing`); venta POS de una caja de empresa sin IVA; siembra de tasas
  idempotente (dos empresas CO seguidas crean tres tasas, no seis).
- **Barrido de pruebas por cuerpos crudos** que armen empresas, pedidos o tiendas (la regla del `CLAUDE.md`): relajar es
  menos riesgoso que endurecer, pero los harness que afirman `bank_accounts_required` o `billing_account_missing` dejan
  de valer.
- `ArchitectureTests`: el puerto de las tasas por defecto vive en Companies.Application; el adaptador en Bootstrapper.
- Las pruebas se escriben; las corre el owner al final (no correr pruebas en esta sesión; verificar con build).

## 9. Decisiones de la revisión (criterio, owner fuera de línea)

Una lectura de quien construye dejó siete preguntas. Todas se resolvieron con criterio y quedan para la revisión del owner.

- **R1. La tasa del producto en la línea.** Hoy la línea guarda una sola tasa (`QuotationItem.cs:103`, `OrderItem.cs:69`) y
  los importes calculados. Se agrega `product_tax_percentage` (int, nullable) a `quotation_items` y `order_items`, con la
  tasa del producto al tomar la foto. `tax_percentage` pasa a ser la **efectiva** (`charges_vat ? producto : 0`). Una
  fila vieja con `product_tax_percentage` nulo se lee como `tax_percentage` (todas eran de empresas con IVA). Las líneas
  del POS no la necesitan: una venta no se recalcula.
- **R2. Cuándo se recalculan las líneas.** Al cambiar la empresa emisora de un documento que todavía admite ese cambio
  (las mismas reglas de hoy de `ApplyBillingContext` y de la cotización), si `charges_vat` cambia: el agregado rehace
  `tax_percentage`, `tax_amount` y `subtotal` de cada línea desde `product_tax_percentage`, y el envío. El recálculo de
  precios (`QuotationPricingRecalculation`, `OrderPricingRecalculation`) usa siempre el `charges_vat` del documento.
  `FromQuotation` copia `charges_vat` y las líneas tal cual: no relee la empresa.
- **R3. «Sin cuenta» es banco y número nulos.** `IsPending` desaparece; la cuenta sin banco ni número es el estado
  «empresa sin cuenta», guardable, con `Currency` explícita. Sin cuenta, la moneda de una cotización o un pedido manual
  es la por defecto del tenant (la regla de hoy), también si la empresa es extranjera: elegir una cuenta es la forma de
  cambiarla. `RefusePendingBillingAccounts` se quita. `QuotationBillingAccountResolver` ya arma esa variante.
- **R4. POS.** `pos.cash_sessions` gana `company_charges_vat` (`NOT NULL DEFAULT true`), congelado al abrir la caja. El
  servidor manda: la vista previa devuelve 0 % y un cliente que mande la tasa del producto recibe `pos.sale.price_changed`,
  como cualquier diferencia con la vista previa. Una caja abierta antes del despliegue sigue con IVA hasta cerrarse.
- **R5. Despliegue con corte para todo.** No sólo los pedidos: un pod viejo recalcularía con IVA un documento sin IVA y
  rechazaría `bankAccounts: []`. **El despliegue entero es no aditivo** y va con `Recreate` (o el backend a cero
  réplicas). Una migración por módulo.
- **R6. Siembra de tasas.** Un solo adaptador en Bootstrapper toca Catalog (crea las tasas) y Quotations (fija
  `freight_tax_rate_id` en `tenant_quotation_settings` si está vacía), bajo un advisory lock de transacción por tenant
  para que dos empresas creadas a la vez no siembren seis. Exento es una tasa de IVA con 0 %, válida en `TaxRate`
  (porcentaje 0–100).
- **R7. Shopify sin cuenta.** La moneda sale de cada pedido y no hay `currency_mismatch` (Shopify cobra en la moneda de la
  tienda). Con empresa sin IVA, la tasa de la línea es 0 sin importar `taxesIncluded`; S-4 (trabajo 2) sólo aplica a
  empresas con IVA.

## 10. Lo que queda a quien construye

- El nombre del puerto de tasas por defecto y si escucha un evento de outbox o se llama tras el commit.
- Cómo se recalculan las líneas al cambiar la empresa emisora (§5.2, criterio).
- El orden de las migraciones y si la de Quotations va en una o dos.

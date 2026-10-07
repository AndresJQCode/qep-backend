# Módulo POS (punto de venta) — MVP

**Fecha:** 2026-10-07
**Módulos:** Pos (nuevo), BuildingBlocks.Domain, Quotations (sólo extracción de la fórmula de
línea), Catalog (una lectura por código exacto en `IProductRepository`), Authorization.Domain (clave
reservada `cashier`), Bootstrapper, Api (backend); `features/pos` nuevo, app-shell y
auth/landing (frontend)
**Estado:** borrador para revisión del owner
**Depende de:** el spec de entitlements de la misma noche (capacidad `pos`, enmascarado de
permisos, el puerto de módulos efectivos y `ModuleGate` en el frontend). POS no se despliega antes
que ese spec. Ahí `pos` depende de `catalog` y `companies`, y de nada más: **no** depende de
`customers`, ni siquiera como opcional (el MVP vende sólo a consumidor final).

## Problema

QEP vende por cotización: cotización → envío → pedido → facturación por fuera. Un tenant con
mostrador (droguería, tienda de insumos, la pizzería del `qcode-pos` viejo) no tiene cómo cobrar
una venta presencial en segundos. Hoy la única vía es crear un cliente, cotizar, enviar y
convertir, que son minutos por venta y exigen datos del cliente que en mostrador nadie da.

El `qcode-pos` de 2023 resolvió ese caso para un solo cliente, sin IVA, sin caja y sin
multi-tenant. No se reutiliza su código; sí sus lecciones (búsqueda rápida, carrito con snapshot,
ticket reimprimible de 80 mm, anulación con motivo).

## Objetivo

Un módulo `Pos` dentro del monolito que permita a un cajero:

1. abrir su caja con una base de efectivo,
2. vender productos del catálogo del tenant con lector de código de barras o búsqueda,
3. cobrar en efectivo, tarjeta o transferencia —o una mezcla— con cálculo de cambio,
4. imprimir un ticket de 80 mm desde el navegador,
5. anular una venta del turno con motivo, y
6. cerrar su caja contando el efectivo contra lo esperado.

### Criterios de éxito

- Una venta de 3 productos escaneados se cobra en efectivo en **menos de 15 segundos** y con
  **cero clics de mouse** (F2 → escanear ×3 → F9 → monto → Enter; F8 si quiere el ticket).
- Doble clic en "Confirmar venta", o un reintento después de un corte de red, **nunca** crea dos
  ventas ni gasta dos números, y **nunca** registra un carrito distinto del que se intentó cobrar.
- El total del ticket coincide al centavo con el que daría una cotización detal con los mismos
  productos y cantidades (misma fórmula de IVA incluido, misma regla de redondeo, misma escala de
  2 decimales en la cantidad).
- El cierre de caja reporta efectivo esperado, contado y diferencia, y los totales por medio de
  pago, sin que el cajero sume nada.
- Con la capacidad `pos` apagada, nadie ve el ítem del menú y todo endpoint `/pos/*` responde
  403 (por enmascarado, no por código nuevo).

## Alcance

**Entra (MVP):**

- Módulo backend `Pos` (Domain/Application/Infrastructure/Api), schema `pos`, `PosDbContext`.
- Agregados `CashSession` (caja/turno) y `PosSale` (venta con líneas y pagos).
- Numeración propia `POS-000001` por tenant.
- Cliente: siempre consumidor final. La venta guarda las columnas del snapshot de cliente (con
  `customer_id` null) para cuando se decida el cliente identificado (`DECISIÓN-PENDIENTE`).
- Escaneo por `Product.Code`: el lector teclea el código de producto del catálogo. Catalog no
  tiene un campo de código de barras/EAN aparte (ver `DECISIÓN-PENDIENTE`).
- Pagos `Cash`, `Card`, `Transfer`, divididos en hasta 5 líneas.
- Descuento por línea sólo con el permiso `pos.sale.discount`.
- Anulación con motivo mientras la caja de la venta siga abierta.
- Rol de sistema `cashier` ("Cajero"), reservado en `SystemRoleKeys`, y seis permisos `pos.*`.
- Frontend: pantalla de caja a pantalla completa, apertura y cierre de caja, listado de ventas,
  listado de cajas, ticket de 80 mm por `window.print`.

**No entra:** facturación electrónica DIAN, inventario y existencias, escalas de precio, USD,
retención en la fuente, excedente de IVA, domicilios, propinas, comandas de cocina, modo offline,
integración con hardware (cajón monedero, impresora ESC/POS, datáfono), devoluciones parciales,
descuento global por venta, PDF del ticket en backend. Ver «Fuera de alcance» y la lista de
`DECISIÓN-PENDIENTE`.

## Lo que se verificó en el código antes de diseñar

| Hecho | Dónde |
| --- | --- |
| Fórmula de línea con IVA incluido: `gross = q × p`; `DiscountAmount = Round(gross × d / 100)`; `lineTotal = Round(gross) − DiscountAmount`; `TaxAmount = Round(lineTotal × t / (100 + t))`; `Subtotal = lineTotal − TaxAmount` | `Modules.Quotations.Domain/QuotationItem.cs:242-258` |
| Redondeo: `Math.Round(value, 2, MidpointRounding.AwayFromZero)` | `QuotationItem.cs:261-262`, `Quotation.cs:1173-1174` |
| Encabezado: `Subtotal`, `DiscountAmount` y `TaxAmount` son la suma redondeada de las líneas; `Total = Subtotal + TaxAmount` | `Quotation.cs:1151-1164` |
| Detal = precio de lista, sin escalas | `QuotationProductPricingResolver.cs:113-116` |
| `Product.PriceBaseCop` es **nullable**: un producto puede tener sólo precio en USD | `Modules.Catalog.Domain/Product.cs:88-93` |
| La tasa se resuelve por `TaxRateId`; sin tasa, o tasa inexistente, cotiza con 0 % | `src/Bootstrapper/QuotationProductPricingLookup.cs:34-42` |
| `IProductRepository` no tiene búsqueda exacta por código: `SearchAsync(code:)` es `ILIKE '%x%'`, con conteo e `Include` de escalas | `IProductRepository.cs:16-24`, `ProductRepository.cs:118-123` |
| `IX_products_tenant_code` es único sobre `(tenant_id, code)` y **distingue mayúsculas** | `CatalogDbContext.cs:99-101` |
| Catalog no tiene campo de código de barras/EAN: lo único escaneable es `Product.Code` (≤ 60) | `Product.cs:13` |
| Cantidad de cotización: `numeric(10,2)` | `QuotationsDbContext.cs:198` |
| Contador atómico `INSERT ... ON CONFLICT DO NOTHING` + `UPDATE ... RETURNING next_value - 1`, dentro de la transacción del documento para no dejar huecos | `OrderNumberGenerator.cs:10-34`, `ConvertQuotationToOrder.cs:132-139` |
| `FinalConsumer.Name = "Consumidor final"`, `IdentificationNumber = "222222222222"`; el frontend ya lo duplica a propósito | `Modules.Quotations.Domain/FinalConsumer.cs:13-18`, `features/quotes/utils/final-consumer.ts` |
| La persona en un documento es un `MemberId` (membresía), no el usuario; se resuelve con `IMembershipDirectory.FindActiveMembershipIdAsync` | `QuotationAdvisorResolver.cs:17-30`, `MemberId.cs:3-13` |
| Toda columna con `MemberId` necesita su `IUserReferenceProbe` o el usuario se borra igual | `QuotationUserReferenceProbe.cs:15-22` |
| Empresa: `Name` (160), `TaxId` (32), `Address` (200), `Phone` (32), `IsActive` | `Company.cs:19-21`, `CompanyContactInfo.cs:27,31` |
| Una FK de otro schema contra `companies.companies(id)` convierte su borrado en `422 companies.company.in_use` sin código nuevo | `CompaniesUnitOfWork.cs:37-54` |
| Calendario del tenant: `ITenantClock.GetAsync` → `TenantCalendar` (`UtcNow`, `Today`, `ToLocal`, `StartOfDayUtc`, `EndOfDayExclusiveUtc`) | `ITenantClock.cs:12-15`, `TenantCalendar.cs:8-58` |
| Mapeo central: `ResourceNotFound`→404, `RequestForbidden`→403, `RequestConcurrency`→**412**, `PreconditionRequired`→428, `ValidationException`→422 `validation.failed` con `errors`, `DomainException`→422 con su código | `src/Api/ApiExceptionHandler.cs:129-151` |
| No encontrado dentro del tenant = 404 con código propio (`CompanyNotFound.For`); tenant de la ruta distinto del llamador = 403 (`CompaniesAuthorization.EnsureAuthorized`) | `CompanyNotFound.cs:5-12`, `CompaniesAuthorization.cs:9-23` |
| Concurrencia por versión: `If-Match` con la versión cargada; sin header → 428 `precondition.if_match_required`; versión distinta → `RequestConcurrencyException` (412) | `OrdersExportLayoutEndpoints.cs:60-65`, `UpdateOrdersExportLayout.cs:134-136` |
| Roles de sistema: `RoleDefinition(key, displayName, description, "Tenancy", risk, permisos)`; permisos: hoy `PermissionDefinition(key, label, description, grupo, risk)`, que el spec de entitlements extiende con un 6.º posicional obligatorio `RequiredModules` | `QepServiceCollectionExtensions.cs:582-710`, `711-...`; `RoleCatalog.cs:19-24` |
| Claves reservadas: `SystemRoleKeys.All` = `admin`, `advisor`, `billing`; `Role.Create` rechaza sólo esas (`authorization.role.key_reserved`); en `TenantRoleCatalog` un rol custom con la misma clave **pisa** al de sistema en silencio | `SystemRoleKeys.cs:15-21`, `Role.cs:145-150`, `TenantRoleCatalog.cs:88-106` |
| Auditoría por outbox: el contrato `platform.audit.recorded.v1` sólo tiene `changedFields` (lista de strings) como campo libre | `CompaniesAuditPublisher.cs:22-54` |
| Harness con tenant real: `QuotationsApiHarness.RegisterTenantAsync`; segundo miembro activo: `ReportingApiHarness.InviteActiveAdvisorAsync`; empresa y productos se siembran por API | `QuotationsApiHarness.cs:136,377,404,554`, `ReportingApiHarness.cs:178` |
| Política por permiso: `.AddPolicy(perm, policy => AddPermissionRequirement(policy, perm))`; sin ella el síntoma es 500 | `QepServiceCollectionExtensions.cs:1090-1093`, `1173-1179` |
| Handlers registrados a mano, vigilados por `CompositionRootTests.EveryCommandAndQueryHasItsHandlerRegistered` | `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs:12-46` |
| Patrón de módulo chico: `CompaniesDbContext` + proyección de outbox `ExcludeFromMigrations`, `CompaniesUnitOfWork`, `CompaniesAuditPublisher` (`platform.audit.recorded.v1`), `CompaniesAuthorization.EnsureAuthorized`, `CompaniesInfrastructureExtensions`, `InitializeCompaniesDatabaseAsync`, `CompaniesDbContextFactory` | `src/Modules/Companies/**` |
| Frontend: el shell no mete padding (`flex-1 overflow-auto`), cada página usa `PageContainer`; query keys con tenant, transaccionales con `refetchOnMount: 'always'` y `gcTime: 0` | `components/app-shell/app-shell.tsx:32-36`, `features/orders/hooks/use-order-list.ts:24-38` |
| `printWithTitle` existe en `features/quotes/utils/print-with-title.ts` | idem |
| Impresión: el mecanismo `data-print-region` / `data-print-hide` esconde todo lo demás y aplana los ancestros; lo usan el pedido y el resumen de conversión; hoy no hay ningún `@page` | `src/index.css:339-405`, `order-detail-page.tsx:161`, `created-order-summary.tsx:74` |
| El sidebar ya soporta `alternates` por permiso | `sidebar-nav-items.ts:59,227` |
| No hay `sheet`, `tabs`, `toggle-group` ni `scroll-area` en `components/ui` | `src/components/ui/` |
| `zustand` 5 ya es dependencia; sólo hay un store de ejemplo | `package.json`, `src/stores/use-counter-store.ts` |

## Dominio (`Modules.Pos.Domain`)

### Tipos

- `CashSessionId`, `PosSaleId`, `PosSaleLineId`, `PosPaymentId`: `readonly record struct (Guid Value)`
  con `New() => Guid.CreateVersion7()`, como `QuotationId`. `PosSaleId` además se acepta del
  cliente (es la clave de idempotencia); `Guid.Empty` lanza `pos.sale.id_required`.
- `MemberId`: tipo propio del módulo, mismo motivo que el de Quotations (el dominio no referencia a
  Tenancy).
- `CashSessionStatus { Open, Closed }`, `PosSaleStatus { Completed, Voided }`,
  `PosPaymentMethod { Cash, Card, Transfer }`. Se persisten y viajan **por nombre**.
- `PosDomainException : DomainException`.
- `PosFinalConsumer` con las dos constantes de `FinalConsumer` (ver «Decisiones»).
- `PosLimits`: `MaxCashAmount = 100 000 000` (tope de base de efectivo y de billete recibido),
  `MaxCountedCash = 1 000 000 000`, `MaxQuantity = 99 999`, `MoneyScale = 2`,
  `QuantityScale = 2`. Toda cifra de dinero, porcentaje de descuento y cantidad con más de 2
  decimales se **rechaza**, nunca se redondea: `numeric(14,2)` redondearía en silencio y
  rompería `Σ Amount = Total`. `PosLimits.HasValidScale(decimal)` compara
  `value == Math.Round(value, 2)`.

### Fórmula de línea compartida

La fórmula de `QuotationItem.Apply` se extrae, sin cambiarla, a
`BuildingBlocks.Domain/Pricing/VatIncludedLine.cs`:

```csharp
public readonly record struct VatIncludedLineAmounts(
    decimal DiscountAmount, decimal TaxAmount, decimal Subtotal)
{
    public decimal LineTotal => Subtotal + TaxAmount;
}

public static class VatIncludedLine
{
    public static VatIncludedLineAmounts Compute(
        decimal quantity, decimal unitPrice, decimal discountPercentage, int taxPercentage);
    public static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
```

`QuotationItem.Apply` conserva sus validaciones y sus códigos `quotation.item.*`, y reemplaza las
líneas 244-257 por una llamada a `Compute`. `PosSaleLine` valida con sus propios códigos `pos.*`
y llama a la misma función. Las pruebas existentes de Quotations son la red de la extracción.

### `CashSession` (caja o turno)

| Campo | Tipo | Regla |
| --- | --- | --- |
| `Id` | `CashSessionId` | v7, la genera el servidor |
| `TenantId` | `Guid` | |
| `CashierId` | `MemberId` | quien abrió; sólo él vende y cierra |
| `CashierName` | `string` (≤ 320) | snapshot de `DisplayName ?? Email` al abrir, para el ticket y los listados |
| `CompanyId` | `Guid` | empresa emisora; activa al abrir |
| `CompanyName`, `CompanyTaxId`, `CompanyAddress?`, `CompanyPhone?` | `string` (160/32/200/32) | snapshot al abrir: encabezado del ticket |
| `Status` | `CashSessionStatus` | `Open` → `Closed`, sin vuelta |
| `OpeningFloat` | `decimal(14,2)` | 0..`MaxCashAmount`, ≤ 2 decimales |
| `OpenedAt` | `DateTimeOffset` | |
| `SalesCount`, `VoidedCount` | `int` | acumulados vivos |
| `SalesTotal`, `CashTotal`, `CardTotal`, `TransferTotal` | `decimal(14,2)` | acumulados de ventas **completadas**; `CashTotal` es efectivo neto (recibido − cambio) |
| `ExpectedCash` | `decimal(14,2)?` | `OpeningFloat + CashTotal`, congelado al cerrar |
| `CountedCash` | `decimal(14,2)?` | lo que contó el cajero, 0..`MaxCountedCash`, ≤ 2 decimales |
| `CashDifference` | `decimal(14,2)?` | `CountedCash − ExpectedCash` (negativo = faltante) |
| `ClosingNote` | `string?` (≤ 500) | |
| `ClosedAt` | `DateTimeOffset?` | |
| `Version` | `long` | token de concurrencia; nace en 1 (como `Company.cs:51`) |
| `CreatedAt`, `UpdatedAt` | `DateTimeOffset` | |

Métodos:

- `Open(id, tenantId, cashier, cashierName, company snapshot, openingFloat, at)`: deja
  `Version = 1`. `openingFloat` negativa, mayor que `MaxCashAmount` o con más de 2 decimales →
  `pos.session.opening_float_invalid`.
- `RegisterSale(PosSale sale, at)`: exige `Open` (`pos.session.not_open`); suma `SalesCount`,
  `SalesTotal` y el aplicado de cada medio; `Version++`.
- `RegisterVoid(PosSale sale, at)`: exige `Open` (`pos.sale.void_session_closed`); resta lo mismo
  que sumó `RegisterSale`, suma `VoidedCount`; `Version++`.
- `Close(countedCash, note, at)`: exige `Open` (`pos.session.not_open`);
  `countedCash` negativo, mayor que `MaxCountedCash` o con más de 2 decimales →
  `pos.session.counted_cash_invalid`; congela `ExpectedCash`, `CountedCash`, `CashDifference`;
  `Status = Closed`; `Version++`. La versión esperada la compara el handler, no el dominio
  (mismo reparto que `UpdateOrdersExportLayout.cs:134-136`).

**Por qué acumulados en la caja y no una suma al cerrar.** Venta y caja quedan en la misma
transacción y la venta mueve la `Version` de la caja. Si un cierre y una venta se cruzan, el que
commitea segundo choca por concurrencia (412) en vez de dejar una venta fuera del arqueo. Y el
"turno en curso" se lee sin sumar ventas.

### `PosSale`

| Campo | Tipo | Regla |
| --- | --- | --- |
| `Id` | `PosSaleId` | lo manda el cliente |
| `RequestFingerprint` | `string` (64) | SHA-256 en hex del cuerpo canónico del request (ver idempotencia); sólo sirve para reconocer una repetición |
| `TenantId`, `CashSessionId`, `CashierId` | | `CashierId` = el de la caja |
| `SaleNumber` | `string` (≤ 20) | `POS-000001` |
| `CustomerId`, `CustomerName`, `CustomerIdentificationType`, `CustomerIdentificationNumber` | `Guid?`, `string` (160), `string?` (10), `string` (32) | snapshot del cliente. En el MVP siempre `PosFinalConsumer`: `null`, `Consumidor final`, `null`, `222222222222`. Las columnas existen para el cliente identificado (`DECISIÓN-PENDIENTE`) |
| `Lines` | `PosSaleLine[]` | 1..200 |
| `Payments` | `PosPayment[]` | 1..5 |
| `Subtotal`, `TaxAmount`, `DiscountAmount`, `Total`, `ChangeAmount` | `decimal(14,2)` | calculados |
| `Status` | `PosSaleStatus` | `Completed` → `Voided`, sin vuelta |
| `VoidReason` | `string?` (≤ 500) | obligatorio al anular |
| `VoidedAt`, `VoidedBy` | `DateTimeOffset?`, `MemberId?` | |
| `CreatedAt` | `DateTimeOffset` | sin `Version`: la anulación la serializa la `Version` de la caja |

`PosSaleLine`: `Id`, `Position`, `ProductId`, `ProductCode` (≤ 60), `ProductName` (≤ 200),
`Quantity` (`numeric(10,2)`, > 0, ≤ 99 999, a lo sumo 2 decimales, igual que Quotations),
`UnitPrice` (≥ 0), `DiscountPercentage` (0..100, a lo sumo 2 decimales), `TaxPercentage`
(`int`, 0..100), `DiscountAmount`, `TaxAmount`, `Subtotal`. `LineTotal` se deriva y no se
persiste, como el precio con descuento de Quotations.

`PosPayment`: `Id`, `Method`, `Amount` (lo aplicado al total), `Tendered` (sólo `Cash`: el
billete que entregó el cliente), `Reference` (≤ 60, sólo `Card`/`Transfer`, opcional). Quién pone
`Amount` depende del medio: en `Card`/`Transfer` lo manda el cliente; en `Cash` lo **calcula el
servidor** (`cashDue`) y el request sólo trae `Tendered` (ver «Reglas de pago»).

Creación: `PosSale.Create(id, fingerprint, tenantId, session, number, lines, payments, at)`; el
cliente es siempre `PosFinalConsumer`.
Orden: valida y calcula líneas → encabezado → pagos. Nada se asigna hasta que todo valida
(lección de `Company.Update`).

Totales del encabezado, idénticos a `Quotation.RecalculateTotals` sin excedente ni retención:

```
Subtotal       = Round(Σ line.Subtotal)
DiscountAmount = Round(Σ line.DiscountAmount)
TaxAmount      = Round(Σ line.TaxAmount)
Total          = Subtotal + TaxAmount
```

### Reglas de pago

Sea `nonCash = Σ Amount` de las líneas `Card`/`Transfer` y `cashDue = Total − nonCash`.

1. Al menos un pago (`pos.sale.payment_required`), a lo sumo 5 (`pos.sale.too_many_payments`).
2. A lo sumo una línea `Cash` (`pos.sale.duplicate_cash_payment`). Dos efectivos no dicen nada
   que uno no diga, y complican el cambio.
3. `Card`/`Transfer`: `Amount > 0` y con a lo sumo 2 decimales
   (`pos.sale.payment_amount_invalid`); `Tendered` debe venir null
   (`pos.sale.tendered_only_for_cash`). `Cash`: `Tendered` obligatorio, 0..`MaxCashAmount` y con
   a lo sumo 2 decimales (`pos.sale.tendered_invalid`); `Amount` debe venir null
   (`pos.sale.cash_amount_not_allowed`), porque lo calcula el servidor.
4. `nonCash ≤ Total` (`pos.sale.payment_exceeds_total`): una tarjeta no da cambio.
5. Si `cashDue > 0`, tiene que haber línea `Cash` con `Tendered ≥ cashDue`
   (`pos.sale.payment_insufficient`).
6. Si `cashDue = 0` y hay línea `Cash` → `pos.sale.cash_payment_unneeded`.
7. La línea `Cash` guarda `Amount = cashDue` y `Tendered` tal cual; `ChangeAmount = Tendered − cashDue`.
8. `Total = 0` se acepta con un único pago `Cash` de `Tendered = 0`, y es la única venta en la
   que la línea `Cash` guarda `Amount = 0`. Es la excepción a la regla 6. Con `Total = 0`, un
   `Cash` con `Tendered > 0` → `pos.sale.tendered_invalid`: una venta en cero no recibe billete
   ni da cambio, y aceptarlo dejaría un `ChangeAmount` igual al billete, que el arqueo no
   distingue de un vuelto real. (Una `Card`/`Transfer` con total 0 ya cae en la regla 4.) Que el
   llamador pueda llegar a un total en cero lo decide la aplicación, no el dominio: exige
   `pos.sale.discount` (ver «Crear venta»).

Se cumple siempre `Σ Amount = Total`.

### Ejemplo trabajado

Carrito (precios de lista COP, IVA incluido):

| # | Producto | Cant. | Precio | Desc. | IVA |
| --- | --- | --- | --- | --- | --- |
| 1 | Shampoo 400 ml | 2 | 11 900 | 10 % | 19 % |
| 2 | Avena granel (kg) | 1,5 | 5 000 | 0 % | 0 % |
| 3 | Jabón | 3 | 2 990 | 0 % | 5 % |

Línea 1: `gross = 23 800`; `DiscountAmount = Round(2 380) = 2 380,00`;
`lineTotal = 23 800 − 2 380 = 21 420`; `TaxAmount = Round(21 420 × 19 / 119) = 3 420,00`;
`Subtotal = 18 000,00`.

Línea 2: `gross = 7 500`; sin descuento; `TaxAmount = 0`; `Subtotal = 7 500,00`.

Línea 3: `gross = 8 970`; `TaxAmount = Round(8 970 × 5 / 105) = Round(427,142857…) = 427,14`;
`Subtotal = 8 542,86`.

Encabezado: `Subtotal = 34 042,86`; `TaxAmount = 3 847,14`; `DiscountAmount = 2 380,00`;
`Total = 37 890,00` (igual a `21 420 + 7 500 + 8 970`).

Desglose de IVA del ticket (por tasa, calculado en el servidor; sólo viaja en `PosSaleResponse`):

| Tasa | Base | IVA |
| --- | --- | --- |
| 0 % | 7 500,00 | 0,00 |
| 5 % | 8 542,86 | 427,14 |
| 19 % | 18 000,00 | 3 420,00 |

Pago dividido: tarjeta 20 000 (referencia `1234`) + efectivo, el cliente entrega 20 000.
`nonCash = 20 000`; `cashDue = 17 890`; `Tendered 20 000 ≥ 17 890` ✓; `ChangeAmount = 2 110`.
Se guardan `Card 20 000` y `Cash 17 890 (tendered 20 000)`. A la caja suman
`CardTotal += 20 000`, `CashTotal += 17 890`, `SalesTotal += 37 890`.

Cierre: base 100 000, una sola venta → `ExpectedCash = 117 890`. Si el cajero cuenta
`117 000`, `CashDifference = −890` (faltante).

### Redondeo y centavos

Los importes tienen 2 decimales, como en Quotations. Un descuento porcentual puede dejar centavos
(7 % de 2 990 = 209,30). El MVP **cobra el total exacto**, centavos incluidos; el redondeo del
efectivo al múltiplo de 50 queda como `DECISIÓN-PENDIENTE`. En pantalla, POS muestra centavos
sólo cuando no son cero (ver frontend), para que el cambio no mienta.

Lo que entra por el cable ya viene con su escala: dinero, descuento y cantidad con más de 2
decimales es 422 (validador con el campo, dominio con el código), nunca un redondeo silencioso.

## Persistencia (schema `pos`)

`PosDbContext`, historial de migraciones en `pos.__ef_migrations_history`, factory de diseño
`PosDbContextFactory` (mismo patrón que `CompaniesDbContextFactory`), proyección
`PosOutboxMessage` sobre `platform.outbox_messages` con `ExcludeFromMigrations`.

Migración inicial `CreatePosSchema` (DDL equivalente):

```sql
CREATE SCHEMA IF NOT EXISTS pos;

CREATE TABLE pos.cash_sessions (
  id                uuid PRIMARY KEY,
  tenant_id         uuid NOT NULL,
  cashier_id        uuid NOT NULL,              -- tenancy.memberships(id), sin FK (otro módulo)
  cashier_name      varchar(320) NOT NULL,
  company_id        uuid NOT NULL REFERENCES companies.companies(id) ON DELETE RESTRICT,
  company_name      varchar(160) NOT NULL,
  company_tax_id    varchar(32)  NOT NULL,
  company_address   varchar(200),
  company_phone     varchar(32),
  status            varchar(10)  NOT NULL CHECK (status IN ('Open','Closed')),
  opening_float     numeric(14,2) NOT NULL CHECK (opening_float >= 0),
  sales_count       integer NOT NULL DEFAULT 0,
  voided_count      integer NOT NULL DEFAULT 0,
  sales_total       numeric(14,2) NOT NULL DEFAULT 0,
  cash_total        numeric(14,2) NOT NULL DEFAULT 0,
  card_total        numeric(14,2) NOT NULL DEFAULT 0,
  transfer_total    numeric(14,2) NOT NULL DEFAULT 0,
  expected_cash     numeric(14,2),
  counted_cash      numeric(14,2),
  cash_difference   numeric(14,2),
  closing_note      varchar(500),
  opened_at         timestamptz NOT NULL,
  closed_at         timestamptz,
  version           bigint NOT NULL,
  created_at        timestamptz NOT NULL,
  updated_at        timestamptz NOT NULL
);
CREATE UNIQUE INDEX "IX_cash_sessions_one_open_per_cashier"
  ON pos.cash_sessions (tenant_id, cashier_id) WHERE status = 'Open';
CREATE INDEX "IX_cash_sessions_tenant_opened" ON pos.cash_sessions (tenant_id, opened_at DESC);
CREATE INDEX "IX_cash_sessions_company" ON pos.cash_sessions (company_id);

CREATE TABLE pos.sales (
  id                              uuid PRIMARY KEY,
  tenant_id                       uuid NOT NULL,
  cash_session_id                 uuid NOT NULL REFERENCES pos.cash_sessions(id),
  cashier_id                      uuid NOT NULL,
  request_fingerprint             char(64) NOT NULL,
  sale_number                     varchar(20) NOT NULL,
  customer_id                     uuid,                    -- siempre null en el MVP; sin FK
  customer_name                   varchar(160) NOT NULL,
  customer_identification_type    varchar(10),
  customer_identification_number  varchar(32) NOT NULL,
  subtotal                        numeric(14,2) NOT NULL,
  tax_amount                      numeric(14,2) NOT NULL,
  discount_amount                 numeric(14,2) NOT NULL,
  total                           numeric(14,2) NOT NULL,
  change_amount                   numeric(14,2) NOT NULL,
  status                          varchar(10) NOT NULL CHECK (status IN ('Completed','Voided')),
  void_reason                     varchar(500),
  voided_at                       timestamptz,
  voided_by                       uuid,
  created_at                      timestamptz NOT NULL
);
CREATE UNIQUE INDEX "IX_sales_tenant_number" ON pos.sales (tenant_id, sale_number);
CREATE INDEX "IX_sales_session" ON pos.sales (cash_session_id, created_at);
CREATE INDEX "IX_sales_tenant_created" ON pos.sales (tenant_id, created_at DESC);

CREATE TABLE pos.sale_lines (
  id                   uuid PRIMARY KEY,
  sale_id              uuid NOT NULL REFERENCES pos.sales(id) ON DELETE CASCADE,
  position             integer NOT NULL,
  product_id           uuid NOT NULL,                      -- catalog.products(id), sin FK
  product_code         varchar(60)  NOT NULL,
  product_name         varchar(200) NOT NULL,
  quantity             numeric(10,2) NOT NULL,
  unit_price           numeric(14,2) NOT NULL,
  discount_percentage  numeric(5,2)  NOT NULL,
  tax_percentage       integer NOT NULL,
  discount_amount      numeric(14,2) NOT NULL,
  tax_amount           numeric(14,2) NOT NULL,
  subtotal             numeric(14,2) NOT NULL,
  UNIQUE (sale_id, position)
);

CREATE TABLE pos.sale_payments (
  id         uuid PRIMARY KEY,
  sale_id    uuid NOT NULL REFERENCES pos.sales(id) ON DELETE CASCADE,
  position   integer NOT NULL,
  method     varchar(10) NOT NULL CHECK (method IN ('Cash','Card','Transfer')),
  amount     numeric(14,2) NOT NULL,
  tendered   numeric(14,2),
  reference  varchar(60),
  UNIQUE (sale_id, position)
);

CREATE TABLE pos.sale_number_counters (
  tenant_id   uuid PRIMARY KEY,
  next_value  bigint NOT NULL
);
```

Notas:

- **FK a `companies.companies` sí, a catálogo y clientes no.** La de empresa se agrega a mano con
  `migrationBuilder.AddForeignKey` (precedente: `CompaniesDbContext` hacia geografía) y hace que
  borrar una empresa con cajas salga como `422 companies.company.in_use` sin tocar Companies. A
  productos y clientes no: la venta guarda snapshot y un producto borrado no invalida un ticket.
- **El índice parcial** es lo que de verdad impone "una caja abierta por cajero". El handler lo
  revisa antes por legibilidad, pero dos aperturas simultáneas sólo las frena la base.
- **La migración corre después de la de Companies**: `InitializePosDatabaseAsync` va en
  `Program.cs` después de `InitializeQuotationsDatabaseAsync` (línea ~178).
- `pg_trgm` no hace falta: el módulo no busca por texto en sus propias tablas, salvo el número
  exacto de venta.

### Traducción de errores (Infrastructure, `PosUnitOfWork`)

| Excepción | Condición | Se traduce a |
| --- | --- | --- |
| `DbUpdateConcurrencyException` | cualquiera | `RequestConcurrencyException("concurrency.conflict")` → 412 |
| `DbUpdateException` 23505 | índice `IX_cash_sessions_one_open_per_cashier` | `PosDomainException("pos.session.already_open")` → 422 |
| `DbUpdateException` 23505 | constraint `PK_sales` | `PosDomainException("pos.sale.id_taken")`; el handler lo atrapa (ver idempotencia) |
| `DbUpdateException` 23505 | índice `IX_sales_tenant_number` | se relanza sin traducir (500): el contador es atómico, si pasa es un bug |

Se discrimina por nombre de índice, no sólo por `SqlState`, como pide `qep-backend/CLAUDE.md`.

**Dos POST idénticos en paralelo casi nunca llegan a `PK_sales`.** EF ordena el `UPDATE` de
`cash_sessions` (que lleva la `Version`) antes del `INSERT` de `sales`, y el `UPDATE ...
RETURNING` del contador serializa las dos transacciones: el segundo espera el commit del primero
y su `UPDATE` de caja afecta cero filas. Lo normal es que el duplicado salga como
`DbUpdateConcurrencyException` (412), no como `id_taken`. Por eso el handler trata los dos igual
(ver idempotencia).

`PosUnitOfWork` expone `BeginTransactionAsync`, igual que `IQuotationsUnitOfWork`, para el
contador, y `ResetAsync()` (rollback de la transacción abierta + `ChangeTracker.Clear()`) para
releer después de un choque sin arrastrar las entidades del intento fallido.
`PosSaleNumberGenerator` copia `OrderNumberGenerator` sin la columna `year`.

## Aplicación (`Modules.Pos.Application`)

Referencia sólo a `Modules.Tenancy.Application` entre los módulos de negocio (lo verifica
`PosLayerTests`). Todo lo demás entra por puertos con adaptador en `Bootstrapper`.

### Puertos

| Puerto | Adaptador (Bootstrapper) | Qué hace |
| --- | --- | --- |
| `IPosProductLookup.SearchAsync(tenantId, search, page, pageSize)` | `PosProductLookup` (`IProductRepository`, `ITaxRateRepository`, `IProductImageLookup`) | `SearchAsync(search, isActive: true)`; resuelve tasa e imagen como `QuotationProductPricingLookup` y `QuotationProductLookup` (dos consultas por página, no una por producto) |
| `IPosProductLookup.FindByCodeAsync(tenantId, code)` | idem | `IProductRepository.FindByCodeAsync`: igualdad exacta, con mayúsculas, servida por `IX_products_tenant_code`. Devuelve activos e inactivos: el handler marca `Inactive` |
| `IPosProductLookup.FindManyAsync(tenantId, ids)` | idem | `ListByIdsAsync` con tasas; incluye inactivos (el handler decide). Lo usan el preview y la venta |
| `IPosCompanyLookup.ListActiveAsync` / `FindAsync` | `PosCompanyLookup` (`ICompanyRepository`) | nombre, NIT, dirección, teléfono, activo |
| `IPosCashierLookup.FindAsync(tenantId, membershipId)` | `PosCashierLookup` | `DisplayName ?? Email`, mismas fuentes que `QuotationAdvisorLookup` |
| `IMembershipDirectory`, `ITenantClock`, `IExecutionContext` | ya existen en Tenancy.Application | |

`PosProductRef(Id, Code, Name, IsActive, PriceCop?, TaxPercentage, ImageUrl?)`. `TaxPercentage`
es `int` (0 sin tasa), como hace `QuotationProductPricingResolver` con `?? 0`.

**La lectura nueva de Catalog** (`IProductRepository` + `ProductRepository`, consumida sólo por el
adaptador de Bootstrapper; Pos no referencia Catalog): `FindByCodeAsync(tenantId, code)`,
`AsNoTracking`, `Code == code`, sin `Include` de escalas ni conteo. Un escaneo es una búsqueda por
índice único, no un `ILIKE '%x%'` con `COUNT`. No hay respaldo sin mayúsculas: el lector teclea el
código tal cual, y quien teclea a mano busca en la grilla.

### Autorización (doble capa)

`PosAuthorization.EnsureAuthorized(executionContext, tenantId, permission)`, copia de
`CompaniesAuthorization` con el mensaje "this pos operation". Todo handler la llama primero.
Después resuelve el `MemberId` del llamador con `PosCashierResolver` (copia de
`QuotationAdvisorResolver`).

**403 contra 404**, mismo reparto que Companies (`CompaniesAuthorization.cs`, `CompanyNotFound.cs`):

- Tenant de la ruta distinto del tenant activo del llamador, o permiso faltante → `403
  authorization.denied`.
- Id que no existe **dentro del tenant de la ruta** → `404 pos.sale.not_found` /
  `404 pos.session.not_found` (`ResourceNotFoundException`). Todo repositorio filtra por
  `tenantId`, así que el id de una venta de otro tenant responde exactamente igual que uno
  inexistente: no confirma nada.

Alcance por cajero: quien no tiene `pos.register.read` sólo ve **sus** ventas y **sus** cajas
(abiertas y cerradas, para reimprimir un cierre). Pedir una venta o una caja ajena del mismo
tenant es `403 authorization.denied`, mismo criterio que `ReportingPermissions.AllAdvisorsRead`.
`pos.register.read` amplía la lectura; no la reemplaza: los endpoints de lectura exigen
`pos.sale.read`.

**Descuento.** Una línea con `DiscountPercentage > 0` exige `pos.sale.discount` → si falta,
`403 pos.sale.discount_not_allowed` (`RequestForbiddenException`), igual en el preview y en la
venta. Una venta cuyo total quede en cero también lo exige, pero **sólo el POST responde 403**: el
preview devuelve `zeroTotalNotAllowed: true` y la pantalla bloquea "Cobrar" con el motivo. Un
carrito con productos a precio 0 se tiene que poder dibujar; lo que no se puede es cobrarlo.

### Casos de uso

| Caso | Tipo | Permiso | Validador |
| --- | --- | --- | --- |
| `GetRegisterContextQuery` | query | `pos.register.operate` | — |
| `OpenCashSessionCommand(CompanyId?, OpeningFloat)` | command | `pos.register.operate` | `OpeningFloat` 0..100 000 000, ≤ 2 decimales |
| `CloseCashSessionCommand(SessionId, ExpectedVersion, CountedCash, Note?)` | command | `pos.register.operate` | `ExpectedVersion` > 0; `CountedCash` 0..1 000 000 000, ≤ 2 decimales; `Note` ≤ 500 (texto libre) |
| `ListCashSessionsQuery(From?, To?, Status?, Page, PageSize)` | query | `pos.sale.read` (+ `pos.register.read` para ver todas) | `PageSize` ≤ 100 |
| `GetCashSessionQuery(SessionId)` | query | `pos.sale.read` (+ `pos.register.read` para una ajena) | — |
| `SearchPosProductsQuery(Search?, Page, PageSize)` | query | `pos.sale.create` | `PageSize` ≤ 60 |
| `FindPosProductByCodeQuery(Code)` | query | `pos.sale.create` | `Code` no vacío, ≤ 60 |
| `PreviewPosSaleCommand(Lines)` | command sin efectos | `pos.sale.create` (+ `pos.sale.discount` con descuento de línea) | líneas |
| `CreatePosSaleCommand(Id, CashSessionId, Lines, Payments)` | command | `pos.sale.create` (+ `pos.sale.discount` con descuento o total 0) | líneas, pagos, `Reference` ≤ 60 |
| `GetPosSaleQuery(SaleId)` | query | `pos.sale.read` | — |
| `ListPosSalesQuery(SessionId?, From?, To?, Status?, Number?, Page, PageSize)` | query | `pos.sale.read` | — |
| `VoidPosSaleCommand(SaleId, Reason)` | command | `pos.sale.void` | `Reason` 3..500 (texto libre) |

Los validadores de FluentValidation dan el campo (`422 validation.failed` con `errors`); el
dominio da el código. Los dos existen aunque se repitan, como pide la convención.

`Lines` de la venta: `[{ productId, quantity, discountPercentage, expectedUnitPrice, expectedTaxPercentage }]`
— `quantity` > 0, ≤ 99 999, ≤ 2 decimales; `discountPercentage` 0..100, ≤ 2 decimales; 1..200
líneas. `Payments`: `[{ method, amount?, tendered?, reference? }]` (Card/Transfer: `amount`
> 0 y ≤ 2 decimales, sin `tendered`; Cash: `tendered` 0..100 000 000 y ≤ 2 decimales, sin
`amount`); 1..5 pagos.

### Abrir caja

1. Autorizar; resolver `MemberId`.
2. Si el cajero ya tiene caja `Open` → `pos.session.already_open`.
3. Empresa: si `CompanyId` viene, `IPosCompanyLookup.FindAsync`; no existe o es de otro tenant →
   `pos.session.company_not_found`; inactiva → `pos.session.company_inactive`. Si no viene y el
   tenant tiene **exactamente una** empresa activa, se usa esa; con cero →
   `pos.session.no_active_company`; con más de una → `pos.session.company_required`.
4. `CashSession.Open`, auditoría `pos.session.opened`, un `SaveChanges`. El índice parcial
   cubre la carrera (traducción de arriba).

### Crear venta — idempotencia por id de cliente

**Huella del request.** `PosSaleFingerprint.Compute(command)` = SHA-256 en hex minúscula de un
**JSON canónico** serializado con `System.Text.Json`, sin espacios y con posiciones fijas:
`[cashSessionId, [[productId, quantity, discountPercentage, expectedUnitPrice,
expectedTaxPercentage], …], [[method, amount, tendered, reference], …]]`, líneas y pagos en el
orden del request. Los decimales van como texto en `InvariantCulture` y formato `0.##` (la escala
ya está validada, así que `2`, `2.0` y `2.00` dan lo mismo) y lo ausente va como `null` de JSON.
Con cadenas escapadas y un `null` explícito, una referencia que contenga `|` o `,` no puede imitar
otra partición de campos, y `null` no se confunde con `""`. Se guarda en la venta. Se eligió la huella y no "total + cantidad de líneas" porque dos carritos distintos pueden
dar el mismo total (dos productos del mismo precio intercambiados).

1. Autorizar, validar, resolver `MemberId`; calcular la huella. Lo que se responde aquí
   (`403 authorization.denied`, `422 validation.failed`) sale **antes** de buscar la repetición:
   no prueba que un envío anterior del mismo id no haya quedado, y el cliente no lo toma como
   definitivo cuando reintenta un cobro incierto (ver «Ciclo de vida del id»).
2. **Repetición**: `sales.FindAsync(tenantId, id)`. Si existe, su `CashierId` es el llamador **y**
   su huella es la del request → devolver esa venta con **200** (no 201), sin tocar nada. Si
   existe con otro cajero o con otra huella → `pos.sale.id_conflict` (422). Es la red de
   seguridad del servidor: el cliente no debería reusar un id con otro carrito (ver «Diálogo de
   cobro»), y si lo hace, la venta vieja no se presenta como si fuera la nueva.
3. Caja: la `Open` del cajero; si no hay → `pos.session.not_open`; si su id no es
   `CashSessionId` → `pos.sale.session_mismatch` (se cerró y reabrió en otra pestaña).
4. Productos con `FindManyAsync`: falta o es de otro tenant → `pos.sale.product_not_found`;
   inactivo → `pos.sale.product_inactive`; sin `PriceCop` → `pos.sale.product_price_unavailable`;
   `PriceCop ≠ expectedUnitPrice` **o** `TaxPercentage ≠ expectedTaxPercentage` →
   `pos.sale.price_changed`. Basta con que cambie uno de los dos: un cambio sólo de tasa no mueve
   el total (IVA incluido) pero sí el desglose del ticket, y por eso también se rechaza. El precio
   y la tasa que se cobran son **siempre** los del catálogo; los esperados sólo detectan que la
   pantalla quedó vieja, y la pantalla refresca **los dos** (ver «Precio o IVA cambiado»).
5. Descuento: alguna línea con descuento > 0 sin `pos.sale.discount` →
   `403 pos.sale.discount_not_allowed`.
6. `PosSale.Create` (valida líneas y pagos; cliente `PosFinalConsumer`). Si `Total = 0` y el
   llamador no tiene `pos.sale.discount` → `403 pos.sale.discount_not_allowed` (un total en cero
   sin descuento sólo sale de productos con precio 0, y regalar también es un descuento).
7. Transacción: número (`PosSaleNumberGenerator`), `sales.Add`, `session.RegisterSale`,
   auditoría `pos.sale.created`, `SaveChanges`, `Commit`. El número se pide **después** de
   todas las validaciones y adentro de la transacción: un 422 o un 412 no gastan número.
8. **Choque al guardar.** Si el guardado da `pos.sale.id_taken` **o**
   `RequestConcurrencyException`, `PosUnitOfWork.ResetAsync()` y se repite **una vez** el paso 2:
   - la venta aparece → lo que diga el paso 2 (200 con la venta del otro request, o
     `id_conflict`);
   - no aparece y el choque fue `id_taken` → `pos.sale.id_conflict`: el id es la PK de una venta
     de **otro tenant**. Es terminal; el cliente genera otro id;
   - no aparece y el choque fue de concurrencia → se relanza el 412: fue un cierre de caja u
     otra venta del mismo cajero (otra pestaña) que se cruzó, no un duplicado.

Respuesta: `201 Created` con `Location: .../pos/sales/{id}`, o `200` en la repetición. El cuerpo
es el mismo `PosSaleResponse`.

### Anular venta

1. Autorizar `pos.sale.void`. No hace falta ser el cajero: anula quien tenga el permiso (hoy
   sólo admin).
2. Venta del tenant de la ruta; si no está → `404 pos.sale.not_found`.
3. `Status = Voided` → `pos.sale.already_voided`.
4. Caja de la venta `Closed` → `pos.sale.void_session_closed`. Una caja cerrada es un arqueo que
   alguien ya firmó; tocarla lo dejaría mintiendo.
5. `sale.Void(reason, member, at)` + `session.RegisterVoid(sale, at)`, auditoría
   `pos.sale.voided`, un `SaveChanges`.

### Cerrar caja

1. Autorizar; caja del tenant (`404 pos.session.not_found` si no); sólo el cajero dueño (`403`
   si no).
2. `session.Version ≠ ExpectedVersion` → `RequestConcurrencyException("concurrency.conflict")`
   (412). La versión llega por `If-Match` y sale de `GET /pos/register`: el cajero cierra contra
   el arqueo que **vio**, y si entró una venta o una anulación después, el cierre no la absorbe
   sin que la pantalla la muestre.
3. `session.Close`, auditoría `pos.session.closed`, un `SaveChanges`. Devuelve el resumen de
   cierre.

No hay "ventas pendientes": una venta es atómica y sólo existe completada o anulada.

### Auditoría

`IPosAuditPublisher.Publish(tenantId, actorId, action, resourceType, resourceId, outcome,
changedFields, at)` sobre la proyección de outbox (`platform.audit.recorded.v1`,
`actorType: "Human"`, `source: "pos"`), en el mismo `SaveChanges` que el cambio.

**Descuentos en la auditoría.** `pos.sale.created` lleva en `changedFields` una entrada por línea
con descuento, `discount:{position}:{productCode}:{discountPercentage}` (p. ej.
`discount:1:SH-400:10`), y `total:0` si la venta quedó en cero. `changedFields` es el único campo
libre del contrato (`CompaniesAuditPublisher.cs:44-54`); usarlo evita cambiar un evento que
consumen todos los módulos. Sin descuentos va vacío, como en Companies.

| Acción | `resourceType` |
| --- | --- |
| `pos.session.opened` | `cash_session` |
| `pos.session.closed` | `cash_session` |
| `pos.sale.created` | `pos_sale` |
| `pos.sale.voided` | `pos_sale` |

`actorId` es `executionContext.SubjectId`, como en Companies.

### Retención de usuarios

`PosUserReferenceProbe` (`Source => "pos"`) en `Pos.Infrastructure`, copia de
`QuotationUserReferenceProbe`: traduce usuario → membresías y busca en
`cash_sessions.cashier_id`, `sales.cashier_id` y `sales.voided_by`. Una venta es historia: el
cajero no se borra mientras tenga una.

## API

Grupo `/api/v1/tenants/{tenantId:guid}/pos`, tag `Pos`, en `Modules.Pos.Api/PosEndpoints.cs`.
Cada endpoint declara su propio `RequireAuthorization`.

### Permisos y políticas

```csharp
public static class PosPermissions
{
    public const string SaleRead = "pos.sale.read";
    public const string SaleCreate = "pos.sale.create";
    public const string SaleVoid = "pos.sale.void";
    public const string SaleDiscount = "pos.sale.discount";
    public const string RegisterOperate = "pos.register.operate";
    public const string RegisterRead = "pos.register.read";
}
```

Las seis llevan su `.AddPolicy(...)` y su `PermissionDefinition` (grupo `"Pos"`) con la firma que
deja el spec de entitlements — 6.º parámetro posicional y obligatorio `RequiredModules`
(`RoleCatalog.cs:19-24` hoy tiene cinco):

```csharp
new PermissionDefinition(PosPermissions.SaleCreate, "Vender en caja", "…", "Pos", "medium",
    [TenantModuleKeys.Pos]),
```

| Permiso | Label | Riesgo |
| --- | --- | --- |
| `pos.sale.read` | Ver ventas y cierres de caja | low |
| `pos.sale.create` | Vender en caja | medium |
| `pos.sale.discount` | Dar descuentos en caja | high |
| `pos.sale.void` | Anular ventas de caja | high |
| `pos.register.operate` | Abrir y cerrar su caja | medium |
| `pos.register.read` | Ver todas las cajas y ventas | medium |

Los seis exigen `[pos]`. El spec de entitlements reserva cinco lugares; `pos.sale.discount` es el
sexto y su prueba de completitud del mapa obliga a declararlo igual.

### Roles

- `admin`: suma los seis.
- `cashier` nuevo (`DisplayName "Cajero"`, descripción "Vende en el punto de venta y abre y
  cierra su propia caja.", categoría `"Tenancy"`, riesgo `"medium"`): `pos.sale.read`,
  `pos.sale.create`, `pos.register.operate`. **Sin** `pos.sale.discount` ni `pos.sale.void`
  (ver «Decisiones»). Un tenant que quiera cajeros con descuento hace un rol custom.
- `advisor` y `billing`: sin cambios.

**`cashier` se reserva.** `SystemRoleKeys` (`SystemRoleKeys.cs:15-21`) suma
`Cashier = "cashier"` a `All`, así `Role.Create` (`Role.cs:145`) lo rechaza con
`authorization.role.key_reserved` desde el despliegue. Lo que no cubre es lo que ya existe: un rol
custom `cashier` creado antes **pisaría en silencio** al de sistema en ese tenant
(`TenantRoleCatalog.cs:88-106` indexa el custom después del de sistema). Por eso hay un chequeo
previo al despliegue (ver «Despliegue»).

### Endpoints

Montos como número JSON con 2 decimales; fechas UTC ISO-8601 más su versión local cuando la
pantalla la imprime.

**1. `GET /pos/register`** — `pos.register.operate` — todo lo que la pantalla de caja necesita
para decidir qué dibujar, en un viaje.

```json
{
  "cashier": { "memberId": "0192…", "name": "Laura Gómez" },
  "session": {
    "id": "0192…", "status": "Open", "openedAt": "2026-10-07T13:02:11Z",
    "openedAtLocal": "2026-10-07T08:02:11-05:00", "openedBeforeToday": false,
    "company": { "id": "0191…", "name": "Origen Botánico SAS", "taxId": "900123456-1" },
    "openingFloat": 100000.00, "salesCount": 1, "voidedCount": 0, "salesTotal": 37890.00,
    "expectedCash": 117890.00, "version": 2,
    "paymentTotals": [
      { "method": "Cash", "amount": 17890.00 },
      { "method": "Card", "amount": 20000.00 },
      { "method": "Transfer", "amount": 0.00 }
    ]
  },
  "companies": [ { "id": "0191…", "name": "Origen Botánico SAS", "taxId": "900123456-1" } ],
  "defaultCompanyId": "0191…"
}
```

Comentarios del DTO: `session` es `null` sin caja abierta (no 404: no tener caja es un estado, no
un error). `companies` viaja aquí porque el cajero no tiene `companies.company.read` y el selector
de apertura lo necesita. `defaultCompanyId` es null cuando hay que elegir. `paymentTotals` lleva
**los tres medios siempre**, en cero si no hubo, para que la pantalla no conozca el enum.
`expectedCash` vivo = base + efectivo neto. `openedBeforeToday` compara la fecha local de
`openedAt` con `TenantCalendar.Today`: la pantalla no sabe el huso del tenant, y con el campo
avisa de una caja que quedó abierta desde otro día. `version` es la que el cierre manda en
`If-Match` (1 al abrir, +1 por venta o anulación).

**2. `POST /pos/sessions`** — `pos.register.operate` — `{ "companyId": null, "openingFloat": 100000 }`
→ `201` con el mismo objeto `session` de arriba. Errores: `pos.session.already_open`,
`pos.session.company_required`, `pos.session.company_not_found`, `pos.session.company_inactive`,
`pos.session.no_active_company`, `pos.session.opening_float_invalid`, `validation.failed`.

**3. `POST /pos/sessions/{sessionId}/close`** — `pos.register.operate` — header
`If-Match: "2"` (la `version` de `GET /pos/register`) y
`{ "countedCash": 117000, "note": "Faltan 890, se revisa mañana" }` → `200`:

```json
{
  "id": "0192…", "status": "Closed", "cashierName": "Laura Gómez",
  "company": { "name": "Origen Botánico SAS", "taxId": "900123456-1" },
  "openedAtLocal": "2026-10-07T08:02:11-05:00", "closedAtLocal": "2026-10-07T18:31:40-05:00",
  "openingFloat": 100000.00, "salesCount": 1, "voidedCount": 0, "salesTotal": 37890.00,
  "paymentTotals": [
    { "method": "Cash", "amount": 17890.00 },
    { "method": "Card", "amount": 20000.00 },
    { "method": "Transfer", "amount": 0.00 }
  ],
  "expectedCash": 117890.00, "countedCash": 117000.00, "cashDifference": -890.00,
  "note": "Faltan 890, se revisa mañana"
}
```

`cashDifference` viaja con signo y calculado: la pantalla sólo elige el color. Errores:
`pos.session.not_open`, `pos.session.counted_cash_invalid`, `authorization.denied` (caja
ajena), `pos.session.not_found` (404), `precondition.if_match_required` (428, sin `If-Match`),
`concurrency.conflict` (412: entró una venta o una anulación después de cargar el arqueo, o el
mismo cierre ya había llegado). Ante 412 o `pos.session.not_open`, la pantalla consulta
`GET /pos/sessions/{sessionId}`: si está `Closed`, el cierre anterior sí quedó y muestra ese
resumen; si sigue `Open`, pinta los números nuevos antes de dejar cerrar.

**4. `GET /pos/sessions?from=2026-10-01&to=2026-10-07&status=Open&page=1&pageSize=20`**
— `pos.sale.read` — `{ "items": [<resumen de caja>], "page": 1, "pageSize": 20, "total": 3 }`.
`from`/`to` son fechas **locales del tenant**, cortadas con `TenantCalendar.StartOfDayUtc` /
`EndOfDayExclusiveUtc` sobre `opened_at`. `pageSize` máx. 100. Con `pos.register.read`, todas
las cajas; sin él, sólo las del llamador. No hay filtro por cajero.

**5. `GET /pos/sessions/{sessionId}`** — `pos.sale.read` — el resumen de cierre (o el vivo si
está abierta). La propia siempre; una ajena sólo con `pos.register.read` (`403` si no);
inexistente en el tenant → `404 pos.session.not_found`. Es lo que deja al cajero reimprimir un
cierre de otro día.

**6. `GET /pos/products?search=sham&page=1&pageSize=40`** — `pos.sale.create`:

```json
{
  "items": [
    { "id": "0190…", "code": "SH-400", "name": "Shampoo 400 ml", "unitPrice": 11900.00,
      "taxPercentage": 19, "imageUrl": "https://…/sh400.webp", "sellable": true,
      "unsellableReason": null }
  ],
  "page": 1, "pageSize": 40, "total": 1
}
```

Sólo productos activos. Uno sin precio en pesos viaja con `unitPrice: null`, `sellable: false`,
`unsellableReason: "PriceMissing"`: si se escondiera, el cajero escanearía un código existente y
vería "no existe". `unsellableReason` vale `Inactive`, `PriceMissing` o `NotFound` (este último
sólo en el preview). Orden por relevancia (el de `ProductRepository.SearchAsync`). No hay variante
`?ids=`: para refrescar un carrito alcanza el preview, que ya devuelve precio, tasa y
vendibilidad por línea.

**7. `GET /pos/products/by-code?code=SH-400`** — `pos.sale.create` — `200` con un ítem como los de
arriba, o `404 pos.product.not_found`. El código es `Product.Code` (el lector teclea ese código;
no hay campo EAN), con coincidencia **exacta**, mayúsculas incluidas. Un producto inactivo **sí**
vuelve, con `sellable: false` y `unsellableReason: "Inactive"`, por la misma razón que el sin
precio. 404 es correcto aquí: la búsqueda ya está acotada al tenant de la ruta, no confirma nada
de otro.

**8. `POST /pos/sales/preview`** — `pos.sale.create` — sin efectos.

```json
{ "lines": [ { "productId": "0190…", "quantity": 2, "discountPercentage": 10 } ] }
```

→

```json
{
  "lines": [
    { "productId": "0190…", "code": "SH-400", "name": "Shampoo 400 ml", "quantity": 2,
      "unitPrice": 11900.00, "taxPercentage": 19, "discountPercentage": 10,
      "discountAmount": 2380.00, "taxAmount": 3420.00, "subtotal": 18000.00,
      "lineTotal": 21420.00, "sellable": true, "unsellableReason": null }
  ],
  "subtotal": 18000.00, "taxAmount": 3420.00, "discountAmount": 2380.00, "total": 21420.00,
  "zeroTotalNotAllowed": false
}
```

Existe para que el frontend **nunca** calcule IVA: la pantalla pinta lo que el servidor calculó,
como el preview de cotizaciones. Una línea no vendible vuelve marcada y fuera de los totales, no
como 422: el carrito tiene que poder mostrar qué está mal (y la pantalla bloquea "Cobrar"
mientras haya una). Un producto que ya no existe en el tenant vuelve con `unsellableReason:
"NotFound"` y `code`/`name` en null; la pantalla los toma del snapshot del carrito.
`zeroTotalNotAllowed` es `true` cuando el total da 0 y el llamador no tiene `pos.sale.discount`
(ver «Autorización»). El desglose de IVA por tasa no viaja aquí: sólo lo usa el ticket. Un
descuento de línea sin `pos.sale.discount` es `403 pos.sale.discount_not_allowed`; cantidad,
descuento o escala fuera de rango, `422 validation.failed`.

**9. `POST /pos/sales`** — `pos.sale.create`:

```json
{
  "id": "6f1c2a52-8a3e-4c4e-9d55-3c2b1e0f7a11",
  "cashSessionId": "0192…",
  "lines": [
    { "productId": "0190…", "quantity": 2, "discountPercentage": 10,
      "expectedUnitPrice": 11900, "expectedTaxPercentage": 19 }
  ],
  "payments": [
    { "method": "Card", "amount": 20000, "reference": "1234" },
    { "method": "Cash", "tendered": 20000 }
  ]
}
```

→ `201` (o `200` en repetición con la misma huella) `PosSaleResponse`:

```json
{
  "id": "6f1c…", "saleNumber": "POS-000042", "status": "Completed",
  "createdAt": "2026-10-07T15:20:03Z", "createdAtLocal": "2026-10-07T10:20:03-05:00",
  "cashSessionId": "0192…", "cashierName": "Laura Gómez",
  "issuer": { "name": "Origen Botánico SAS", "taxId": "900123456-1",
              "address": "Cra 50 # 10-20, Rionegro", "phone": "6045551234" },
  "customer": { "name": "Consumidor final", "identificationType": null,
                "identificationNumber": "222222222222" },
  "lines": [ { "position": 1, "productId": "0190…", "code": "SH-400", "name": "Shampoo 400 ml",
               "quantity": 2, "unitPrice": 11900.00, "discountPercentage": 10,
               "taxPercentage": 19, "discountAmount": 2380.00, "taxAmount": 3420.00,
               "subtotal": 18000.00, "lineTotal": 21420.00 } ],
  "subtotal": 34042.86, "taxAmount": 3847.14, "discountAmount": 2380.00, "total": 37890.00,
  "taxBreakdown": [
    { "taxPercentage": 0, "base": 7500.00, "taxAmount": 0.00 },
    { "taxPercentage": 5, "base": 8542.86, "taxAmount": 427.14 },
    { "taxPercentage": 19, "base": 18000.00, "taxAmount": 3420.00 }
  ],
  "payments": [
    { "method": "Card", "amount": 20000.00, "tendered": null, "reference": "1234" },
    { "method": "Cash", "amount": 17890.00, "tendered": 20000.00, "reference": null }
  ],
  "changeAmount": 2110.00,
  "void": null,
  "voidable": true,
  "voidBlockedReason": null
}
```

Es a la vez la respuesta de la venta y **los datos del ticket**: emisor congelado, hora local ya
convertida al huso del tenant, desglose de IVA (`taxBreakdown`, sólo las tasas presentes,
ordenadas) y cambio. El frontend no arma nada (BFF). `void` es
`{ "reason", "voidedAtLocal", "voidedByName" }` en una anulada.

`voidable` describe **el estado de la venta**, no al que pregunta: `true` si está `Completed` y su
caja sigue `Open`. Si no, `voidBlockedReason` dice la primera razón: `AlreadyVoided` o
`SessionClosed`; con `voidable: true` viaja `null`. Existe porque la pantalla de ventas no tiene
cómo saber si la caja de cada fila sigue abierta, y sin el campo tendría que pedir una caja por
fila. El permiso no entra: sin `pos.sale.void` la pantalla no dibuja la acción.

**10. `GET /pos/sales/{saleId}`** — `pos.sale.read` — el mismo `PosSaleResponse`. Inexistente en
el tenant → `404 pos.sale.not_found`; ajena sin `pos.register.read` → `403`. Es también el
"Verificar" del cobro incierto.

**11. `GET /pos/sales?sessionId=&from=&to=&status=&number=&page=1&pageSize=20`** —
`pos.sale.read` — `{ "items": [ { "id", "saleNumber", "createdAtLocal", "cashierName",
"customerName", "total", "status", "paymentMethods": ["Card","Cash"], "voidable",
"voidBlockedReason" } ], "page", "pageSize", "total" }`. Sin `pos.register.read`, filtra al cajero
llamador aunque mande otro `sessionId`. `voidable` como en el punto 9, resuelto con un join a
`cash_sessions.status`, no una consulta por fila.

**12. `POST /pos/sales/{saleId}/void`** — `pos.sale.void` — `{ "reason": "Cliente se arrepintió" }`
→ `200 PosSaleResponse`. Errores: `pos.sale.not_found` (404), `pos.sale.already_voided`,
`pos.sale.void_session_closed`, `validation.failed`, `concurrency.conflict`.

### Códigos nuevos

Todos son `PosDomainException` → 422 por `ApiExceptionHandler`, salvo los marcados 404
(`ResourceNotFoundException`) y 403 (`RequestForbiddenException`).

| Código | Cuándo |
| --- | --- |
| `pos.session.already_open` | el cajero ya tiene caja abierta |
| `pos.session.not_open` | vender o cerrar sin caja abierta |
| `pos.session.not_found` (404) | id de caja que no existe en el tenant de la ruta |
| `pos.session.company_required` | más de una empresa activa y no se eligió |
| `pos.session.company_not_found` | empresa inexistente o de otro tenant |
| `pos.session.company_inactive` | empresa desactivada |
| `pos.session.no_active_company` | el tenant no tiene empresas activas |
| `pos.session.opening_float_invalid` | base negativa, > 100 000 000 o con más de 2 decimales |
| `pos.session.counted_cash_invalid` | contado negativo, > 1 000 000 000 o con más de 2 decimales |
| `pos.sale.id_required` | id vacío |
| `pos.sale.id_conflict` | el id ya es de una venta de otro cajero, de una venta con otra huella o de una venta de otro tenant. Terminal: el cliente genera otro id |
| `pos.sale.not_found` (404) | id de venta que no existe en el tenant de la ruta |
| `pos.sale.session_mismatch` | la caja enviada no es la abierta del cajero |
| `pos.sale.lines_required` / `pos.sale.too_many_lines` | 0 o > 200 líneas |
| `pos.sale.quantity_invalid` | ≤ 0, > 99 999 o con más de 2 decimales |
| `pos.sale.discount_out_of_range` | fuera de 0..100 o con más de 2 decimales |
| `pos.sale.discount_not_allowed` (403) | descuento en una línea, o total en cero, sin `pos.sale.discount` |
| `pos.sale.product_not_found` / `pos.sale.product_inactive` / `pos.sale.product_price_unavailable` | producto |
| `pos.sale.price_changed` | precio o tasa distintos de los esperados |
| `pos.sale.payment_required` / `pos.sale.too_many_payments` / `pos.sale.duplicate_cash_payment` | forma de los pagos |
| `pos.sale.payment_amount_invalid` / `pos.sale.tendered_only_for_cash` / `pos.sale.tendered_invalid` / `pos.sale.cash_amount_not_allowed` | línea de pago (monto ≤ 0 o con más de 2 decimales; recibido ausente, negativo, > 100 000 000, con más de 2 decimales, o > 0 en una venta de total 0; `amount` en una línea `Cash`) |
| `pos.sale.payment_exceeds_total` / `pos.sale.payment_insufficient` / `pos.sale.cash_payment_unneeded` | cuadre |
| `pos.sale.already_voided` / `pos.sale.void_session_closed` | anulación |
| `pos.product.not_found` (404) | código sin coincidencia exacta |

Códigos que POS usa y no define: `concurrency.conflict` (412); `precondition.if_match_required`
(428); `authorization.denied` (403); `validation.failed` (422).

`pos.sale.id_taken` es interno: el handler lo consume y nunca sale.

## Frontend (`qep-frontend`, `features/pos`)

### Rutas

| Ruta | Página | Gate |
| --- | --- | --- |
| `/_authenticated/pos.tsx` (layout) | `<ModuleGate modules={['pos']}><Outlet /></ModuleGate>` | capacidad `pos` (spec de entitlements) |
| `/_authenticated/pos/index.tsx` | `PosRegisterPage` (caja) | `pos.sale.create`, `pos.register.operate` **y** `pos.sale.read` (`GET /pos/register` exige el segundo; "Verificar" un cobro incierto, `GET /pos/sales/{id}`, exige el tercero) |
| `/_authenticated/pos/close.tsx` | `PosCloseSessionPage` | `pos.register.operate` |
| `/_authenticated/pos/sales/index.tsx` | `PosSalesPage` | `pos.sale.read` |
| `/_authenticated/pos/sessions/index.tsx` | `PosSessionsPage` | `pos.sale.read` (con `pos.register.read` ve todas; sin él, las suyas) |

Archivos de ruta finos, como `routes/_authenticated/orders/index.tsx`.

**Dos gates, cada uno con un solo trabajo.** La capacidad la resuelve `ModuleGate`
(`src/components/module-gate.tsx`, del spec de entitlements) en la ruta layout `pos.tsx`, el
mismo patrón que los layouts de `catalog`, `orders` y los demás módulos: con `pos` apagado, ninguna
página de `/pos/*` se monta y no sale ninguna consulta; sus mensajes («Este módulo no está
incluido en el plan de tu empresa.», dependencias faltantes, «Cargando módulos…») y su fail open
ante error son los de ese spec. POS **no** lee `useTenantModules` por su cuenta. Adentro, cada
página va envuelta en `PosGate`, que sólo mira permisos (patrón `QuotesReadGate`):
deny-by-default mientras cargan; sin los de la tabla, la tarjeta "No tienes permiso para usar el
punto de venta."

### Estructura

```
features/pos/
  components/  pos-gate.tsx, register-header.tsx, product-search-bar.tsx, product-grid.tsx,
               product-card.tsx, cart-panel.tsx, cart-line.tsx, quantity-stepper.tsx,
               line-discount-popover.tsx, cart-totals.tsx, pay-dialog.tsx,
               payment-method-buttons.tsx, cash-quick-amounts.tsx, payment-lines.tsx,
               sale-success-panel.tsx, pos-ticket.tsx, ticket-print-portal.tsx,
               open-session-form.tsx, close-session-form.tsx, closing-summary.tsx,
               sales-table.tsx, void-sale-dialog.tsx, sessions-table.tsx
  hooks/       use-register-context.ts, use-pos-product-search.ts, use-product-by-code.ts,
               use-sale-preview.ts, use-create-sale.ts, use-void-sale.ts, use-open-session.ts,
               use-close-session.ts, use-pos-sales.ts, use-pos-sessions.ts,
               use-pos-shortcuts.ts, use-scan-queue.ts, use-scanner-burst-guard.ts
  pages/       pos-register-page.tsx, pos-close-session-page.tsx, pos-sales-page.tsx,
               pos-sessions-page.tsx
  services/    pos.api.ts  (paths con /api/v1/tenants/{tenantId}/pos, mapa POS_CODE_MESSAGES)
  stores/      pos-cart-store.ts
  types/       pos.ts  (DTOs, PAYMENT_METHOD_LABELS, VOID_BLOCKED_REASON_LABELS, POS_PERMISSIONS)
  utils/       quick-cash-amounts.ts, format-pos-money.ts, money-cents.ts, build-payments.ts,
               sale-attempt-outcome.ts
  pos-print.css  (sólo la página con nombre del ticket; ver «Ticket»)
  index.ts
```

Query keys con tenant: `['pos', tenantId, 'register']`, `['pos', tenantId, 'products', search]`,
`['pos', tenantId, 'preview', linesKey]`, `['pos', tenantId, 'sales', filters]`, etc. Lecturas
transaccionales (`register`, `sales`, `sessions`) con `refetchOnMount: 'always'` y `gcTime: 0`.
La búsqueda de productos puede cachear 30 s: es catálogo, y el preview revalida el precio.

### Estado del carrito: `pos-cart-store.ts` (zustand + `persist` en `sessionStorage`)

```ts
interface PosCartState {
  sessionId: string | null          // la caja con la que se armó el carrito; campo, no clave
  lines: { productId: string; code: string; name: string; imageUrl: string | null;
           quantity: number; discountPercentage: number;
           expectedUnitPrice: number; expectedTaxPercentage: number }[]
  pendingSaleId: string | null      // crypto.randomUUID() al abrir el cobro
  payOpen: boolean                  // el diálogo de cobro está abierto
  // El cuerpo exacto que se mandó y en qué quedó. Mientras no sea null, carrito y cobro
  // están bloqueados: ningún setter cambia `lines` ni los pagos.
  // `wasUncertain`: ya hubo un envío de este id con resultado desconocido. Lo pone en true
  // `markUncertain()`, y lo conservan `retryAttempt()` (vuelve a `inFlight`) y la rehidratación.
  attempt: { saleId: string; body: CreatePosSaleRequest;
             status: 'inFlight' | 'uncertain'; wasUncertain: boolean } | null
  selectedLineId: string | null
  // Suma a la línea existente del mismo producto sin descuento. Nunca falla en silencio:
  // 'locked' (cobro abierto o intento en curso) y 'limit' (200 líneas) los muestra quien llama.
  addProduct(p, quantity = 1): 'added' | 'locked' | 'limit'
  setQuantity / setDiscount / removeLine / clear(): void
  // Pone en todas las líneas de ese producto el precio y la tasa que trajo el preview. Sólo con
  // payOpen = false y attempt = null; si no, 'locked' y no cambia nada.
  refreshExpected(productId, unitPrice, taxPercentage): 'updated' | 'locked'
  // La caja abierta ya no es `sessionId`: conserva las líneas, toma la caja nueva y pone
  // pendingSaleId en null. Si `payOpen` y no hay intento, también pone `payOpen = false`: el
  // cobro abierto se armó con la caja vieja. Nunca toca `attempt` (con intento no se llama).
  adoptSession(sessionId): void
  ensurePendingSaleId(): string
  beginAttempt(body) / retryAttempt() / markUncertain() / resolveAttempt(outcome): void
}
```

La clave de `persist` es **por tenant y por usuario** (`qep.pos.cart.{tenantId}.{userId}`), y la
caja viaja como el campo `sessionId` del estado. `userId` sale de `useSession()` (`/auth/me`,
`features/auth/services/auth.api.ts:24`); mientras la sesión o el tenant no estén resueltos, el
store no se hidrata y la caja no se dibuja (`persist` con `skipHydration` y `rehydrate()` cuando
la clave existe). Un F5 no pierde el carrito, ni `pendingSaleId`, ni `attempt`. Recargar con un
intento `inFlight` lo deja en `uncertain` con `wasUncertain = true`: no se sabe si llegó. `sessionStorage` y no
`localStorage`: el carrito es de esta pestaña, no de la máquina.

**Por qué el usuario sí va en la clave.** En un mostrador la pestaña se comparte: la cajera A
cierra sesión y el cajero B entra en la misma pestaña, y `sessionStorage` sobrevive al logout.
Con una clave sólo por tenant, B rehidrataría el intento incierto de A, y su "Reintentar" lo
mandaría con la identidad de B: el paso 2 vería una venta de otro cajero (`id_conflict`, un 4xx
definitivo que libera el carrito de A en la pantalla de B) o, si la de A nunca llegó, la crearía
a nombre de B. B termina cobrando otra vez lo que A pudo haber cobrado: venta duplicada. Con el
usuario en la clave, el intento de A sigue esperando bajo la clave de A hasta que A vuelva a
entrar en esa pestaña.

**Por qué la caja no va en la clave.** La caja cambia justo cuando el carrito tiene que
sobrevivir: una caja cerrada en otra pestaña (`session_mismatch` / `not_open` → apertura "con el
carrito intacto") o abierta de nuevo. Con la caja en la clave, abrir otra leería una clave vacía:
el carrito se perdería y, peor, un intento `uncertain` quedaría escondido bajo la clave vieja, con
un cobro que pudo haber quedado registrado y que nadie vuelve a mirar. Al montar `/pos`:

1. **Con `attempt` no nulo**, lo primero que se dibuja es el diálogo de cobro incierto
   (Reintentar / Verificar), **sin importar la caja**: que no haya caja abierta, que sea otra o
   que sea la misma. `OpenSessionForm` y la caja quedan detrás, sin aceptar nada, hasta resolverlo.
   El intento se resuelve con su propio cuerpo (`attempt.body.cashSessionId` incluido):
   Reintentar es idempotente por id, porque el servidor busca la repetición (paso 2) antes de
   mirar la caja (paso 3); si la venta no había llegado y la caja ya no está abierta, la respuesta
   es `session_mismatch` / `not_open`, un 4xx definitivo que libera el carrito.
2. **Sin intento y con `sessionId` distinto de la caja abierta**, `adoptSession(id)`: las líneas
   siguen, el preview revalida precio y tasa contra el catálogo de ahora. Esto no pasa sólo al
   montar: cada vez que `GET /pos/register` vuelve (refetch al enfocar la pestaña, por ejemplo)
   con otra caja y no hay intento, la pantalla llama `adoptSession`. Si el cobro estaba abierto,
   se cierra (el total congelado y el cuerpo armado eran de la caja vieja) y la pantalla dice "La
   caja cambió mientras cobrabas. Revisa el carrito y vuelve a cobrar."
3. **Sin caja abierta y sin intento**, el carrito persiste tal cual hasta que se abra una.

`clear()` sólo lo llaman una venta exitosa y "Cerrar caja" confirmado (ver «Cerrar caja con
carrito»). `/pos/close` con un `attempt` no nulo no deja cerrar: "Hay un cobro sin confirmar.
Resuélvelo en la caja antes de cerrar.", con enlace a `/pos`.

**Ciclo de vida del id** (lo que impide que un reintento registre otro carrito):

Qué es "definitivo" depende de si ya hubo un envío anterior de ese id con resultado desconocido.
En el **primer envío** (`attempt.status = 'inFlight'` y nunca incierto) ningún request previo
pudo guardar nada, así que cualquier 4xx de la tabla lo es. **Desde `uncertain`** (Reintentar),
el primer envío pudo haber quedado, y sólo prueba que no quedó una respuesta que el servidor da
**después** de buscar la repetición (paso 2 de «Crear venta»): si la venta existiera, el paso 2
habría respondido 200. `authorization.denied` y todo 403 del nivel de tenancy (membresía
suspendida, tenant distinto, capacidad apagada) salen del paso 1, antes de esa búsqueda, y no
dicen nada sobre el primer envío; lo mismo `validation.failed`, 400 y 404.

| Resultado del POST con `attempt.saleId` | Primer envío | Reintentar desde `uncertain` |
| --- | --- | --- |
| `201` / `200` | `clear()`: carrito vacío, `pendingSaleId` y `attempt` en null | igual |
| `403 pos.sale.discount_not_allowed` y `422` con código `pos.*` (pasos 2 a 6: `id_conflict`, `session_mismatch`, `session.not_open`, producto, `price_changed`, pagos) | definitivo: nada quedó guardado; `attempt = null` y `pendingSaleId = null`, el próximo cobro genera otro id y el carrito se desbloquea para corregir | igual: definitivo |
| `403 authorization.denied`, otro 403 del nivel de tenancy, `422 validation.failed`, 400, 404 | definitivo, como la fila anterior | **no** definitivo: sigue `uncertain`, bloqueado y con el mismo cuerpo |
| `401` | el intento queda bloqueado, en `uncertain`; la pantalla pasa por la reautenticación y al volver ofrece "Reintentar" con el mismo cuerpo | igual |
| `NetworkError`, timeout, 5xx, 412 y **cualquier otro status no listado** | `markUncertain()`: carrito y cobro siguen bloqueados con el **mismo** cuerpo | igual |

La clasificación vive en una función pura, `classifyCreateSaleOutcome(response, attempt.wasUncertain)`
(`utils/sale-attempt-outcome.ts`), que devuelve `'success' | 'definitive' | 'uncertain'`, y
`resolveAttempt` sólo aplica lo que ella decide.

`setQuantity`, `setDiscount`, `removeLine`, `addProduct` y `refreshExpected` no cambian nada con
`payOpen` o `attempt` no nulo; la pantalla además deshabilita los controles. Así un id nunca viaja con dos
cuerpos distintos, y el servidor lo comprueba igual con la huella (`pos.sale.id_conflict`).

Los totales **no** viven en el store: los pinta `useSalePreview(lines)` (debounce 200 ms,
`placeholderData` = la respuesta anterior para que los números no parpadeen). Su `linesKey` lleva
sólo `productId`, `quantity` y `discountPercentage`, lo mismo que el cuerpo del preview: un
`refreshExpected` no dispara otro preview.

### Pantalla de caja (`/pos`)

Ocupa el alto completo del área de contenido, sin `PageContainer`: `h-full` con grilla
`grid-cols-[1fr_400px]` desde `lg`; debajo de `lg` el carrito pasa a un panel inferior plegable
con el total y "Cobrar" siempre visibles. Usa los tokens del tema (`bg-background`, `bg-card`,
`text-primary`, `border-border`) y los componentes `Button`, `Input`, `Dialog`, `Badge`, `Card`,
`Popover`, `Tooltip` existentes. No se agregan primitivas de shadcn salvo `scroll-area` si la
grilla lo necesita.

```
┌──────────────────────────────────────────────────────────────────────────────────────────┐
│ Caja de Laura Gómez  desde las 08:02  (Origen Botánico SAS)    Ventas  Cajas  [Cerrar caja]│
├───────────────────────────────────────────────────────────┬──────────────────────────────┤
│ [🔍 Escanea o busca por nombre o código…          F2 ]    │ Carrito · 3 productos        │
│                                                           ├──────────────────────────────┤
│ ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐       │ Shampoo 400 ml       21.420  │
│ │  [img]   │ │  [img]   │ │  [img]   │ │  [img]   │       │ SH-400  [−] 2 [+]  10% ✕     │
│ │Shampoo   │ │Avena kg  │ │Jabón     │ │Crema     │       │──────────────────────────────│
│ │SH-400    │ │AV-01     │ │JB-03     │ │CR-77     │       │ Avena granel (kg)     7.500  │
│ │ $11.900  │ │ $5.000   │ │ $2.990   │ │Sin precio│       │ AV-01  [−] 1,5 [+]       ✕   │
│ └──────────┘ └──────────┘ └──────────┘ └──────────┘       │──────────────────────────────│
│ ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐       │ Jabón                 8.970  │
│ │   …      │ │   …      │ │   …      │ │   …      │       │ JB-03  [−] 3 [+]         ✕   │
│ └──────────┘ └──────────┘ └──────────┘ └──────────┘       ├──────────────────────────────┤
│                                                           │ Subtotal            34.042,86│
│                                                           │ IVA                  3.847,14│
│                                                           │ TOTAL                $37.890 │
│                                                           │   Incluye $2.380 de descuento│
│                                                           │ ┌──────────────────────────┐ │
│                                                           │ │      Cobrar   F9         │ │
│                                                           │ └──────────────────────────┘ │
└───────────────────────────────────────────────────────────┴──────────────────────────────┘
```

Detalles:

- **Caja de otro día**: si `session.openedBeforeToday`, banner `warning` arriba de todo:
  "Caja abierta desde el 06/10. Ciérrala si ese turno ya terminó." con enlace a cerrar.
- **Barra de búsqueda**: autofocus al montar y después de cada venta; `inputMode="search"`.
  Escribir filtra la grilla (debounce 250 ms, `pageSize` 40). La grilla vacía sin búsqueda
  muestra los primeros 40 activos.
- **Flujo de lector** (`use-scan-queue.ts`): el lector teclea `Product.Code` y manda Enter, y
  puede mandar el siguiente código antes de que vuelva la respuesta del primero. En Enter con
  texto:
  1. se captura el valor, se **limpia el input en el acto** y se cancela la búsqueda con
     debounce pendiente (si no, el siguiente código se concatenaría al anterior y la grilla
     buscaría el texto viejo);
  2. el código entra a una **cola FIFO** de consultas a `GET /pos/products/by-code`, que se
     resuelven en orden de llegada, para que el carrito quede en el orden en que se escaneó;
  3. `200` vendible → `addProduct`; con `'added'` resalta la línea 600 ms (`bg-accent` que se
     desvanece; sin animación con `prefers-reduced-motion`), con `'locked'` o `'limit'` muestra
     "No se agregó JB-09: …" con el motivo. `200` no vendible → mensaje inline bajo el input
     ("JB-09 no tiene precio en pesos", "JB-09 está inactivo"). `404` → mensaje "No hay un
     producto con el código «…»", y el texto vuelve al input **sólo si el input sigue vacío** (no
     pisa lo que ya se está escaneando).

  Ningún caso usa toast: el cajero mira el input. Ningún escaneo se pierde sin mensaje. Mientras la
  cola no esté vacía, "Cobrar" y F9 quedan deshabilitados: el cobro nunca sale con un producto
  escaneado todavía en camino.
- **Tarjeta de producto**: imagen 1:1 con `object-cover` (placeholder con la inicial si no hay),
  nombre en 2 líneas, código en `font-mono text-xs`, precio grande. Clic o Enter con foco suma 1.
  No vendible: opaca, sin clic, `Badge` "Sin precio" o "Inactivo".
- **Línea del carrito**: nombre, código, stepper `[−] n [+]` (input editable que acepta coma
  decimal hasta 2 decimales y redondea en blur; un `−` que la dejaría en 0 o menos pide
  confirmar el quitar; `+`/`−`
  operan en centésimas enteras, `Math.round(q * 100) ± 100`, con los mismos helpers de
  `money-cents.ts`, para que `1,1 + 1` dé `2,1` y no `2,1000000000000001`), botón de
  descuento —**sólo con `pos.sale.discount`**; sin él no se dibuja— que abre un `Popover` con
  "% descuento" (0–100, hasta 2 decimales) y el monto resultante del preview, total de línea a la
  derecha en `tabular-nums`. Con teclado, sólo con el foco en la lista del carrito: ↑/↓
  seleccionan línea, `+`/`−` cambian cantidad, `Supr` quita (ver «Atajos»).
- **Totales**: siempre los del preview. Mientras el preview está en vuelo (o en su debounce),
  los números quedan con la respuesta anterior y un indicador sutil; "Cobrar" se deshabilita
  hasta que el preview corresponda al carrito actual **y** cada línea tenga `expectedUnitPrice`
  y `expectedTaxPercentage` iguales al `unitPrice` y la `taxPercentage` del preview. Un F9 en ese lapso no se pierde en
  silencio: el botón muestra "Calculando el total…" y la región `aria-live` lo anuncia; el
  cajero vuelve a pulsar F9 cuando el total aparece.
- **Líneas no vendibles**: si el preview marca alguna línea con `sellable: false` (`Inactive`,
  `PriceMissing` o `NotFound`, con nombre y código del snapshot del carrito), esa línea se pinta
  con el motivo y **"Cobrar" queda deshabilitado** con el texto "Quita los productos que no se
  pueden vender" hasta que el cajero la quite. No se cobra un carrito parcial: el total que se
  mostró tiene que ser el que se cobra.
- **Total en cero sin permiso**: con `zeroTotalNotAllowed`, "Cobrar" queda deshabilitado con
  "Una venta en $0 necesita el permiso de descuentos."
- **Precio o IVA cambiado**: si el preview devuelve, para una línea vendible, un `unitPrice`
  distinto del `expectedUnitPrice` **o** una `taxPercentage` distinta del
  `expectedTaxPercentage`, la pantalla llama `refreshExpected(productId, unitPrice,
  taxPercentage)` y aparece un aviso `warning` arriba del carrito, uno por producto: "El precio de
  Shampoo 400 ml cambió de $11.900 a $12.500.", "El IVA de Shampoo 400 ml cambió del 19 % al
  5 %." o, si cambiaron los dos, ambos en la misma línea del aviso. Refrescar sólo el precio
  dejaría un cambio de tasa sin salida: el servidor rechaza cualquiera de los dos
  (`pos.sale.price_changed`), el preview no vería nada que corregir y cada cobro volvería a
  fallar con un id nuevo. El aviso se descarta al cobrar. Con el cobro abierto o un intento en
  curso el refresco devuelve `'locked'` y no se aplica; el servidor lo atrapa igual. Al cerrar el
  cobro por `price_changed` no basta con esperar "el siguiente preview": el `linesKey` no cambió,
  así que no saldría otro, y el preview en caché puede ser anterior al cambio que el servidor
  detectó. Por eso, (1) la respuesta `price_changed` **invalida explícitamente** el preview
  (`queryClient.invalidateQueries({ queryKey: ['pos', tenantId, 'preview'] })`), que vuelve a
  pedir precio y tasa al catálogo, y (2) el efecto que llama `refreshExpected` depende del
  preview **y** del bloqueo (`payOpen`, `attempt`), así que vuelve a correr cuando el carrito se
  desbloquea aunque el preview no haya cambiado. Sin las dos, "Cobrar" quedaría deshabilitado
  para siempre: el preview muestra un precio distinto del esperado y nadie lo aplica.
- **Cerrar caja con carrito**: si el carrito tiene líneas, "Cerrar caja" pide confirmar: "Hay 3
  productos sin cobrar. Si cierras la caja, se descartan."
- **Dinero**: `format-pos-money.ts` decide los decimales **por valor**: con
  `cents = Math.round(value * 100)`, usa 0 decimales si `cents % 100 === 0` y 2 si no
  (`minimumFractionDigits` = `maximumFractionDigits` = ese número), con
  `Intl.NumberFormat('es-CO', { style: 'currency', currency: 'COP' })`. Así `209.3` sale
  `$ 209,30` y nunca `$ 209,3`. `lib/format-currency.ts` no se toca: cotizaciones siguen sin
  centavos.
- **Aritmética de dinero en centavos enteros** (`money-cents.ts`): `toCents(x) =
  Math.round(x * 100)`, `fromCents(c) = c / 100`. Todo lo que el cliente suma, resta o compara
  (faltante, cambio, "alcanza", botones de efectivo) se hace en centavos enteros: en `number`,
  `0.1 + 0.2 !== 0.3`, y un "Falta por cubrir $0,00" que en realidad es `0.0000001` dejaría
  "Confirmar venta" deshabilitado para siempre.
- **Escaneo en un input de dinero** (`use-scanner-burst-guard.ts`, en recibido, monto de
  tarjeta o transferencia, base y efectivo contado): una ráfaga de 4 o más caracteres con menos
  de 30 ms entre uno y otro que termina en Enter es el lector, no una persona. Se descarta el
  valor **y** el Enter, y el input dice "Eso parece un escaneo; no se tomó como monto." Sin el
  guardia, escanear con el cobro abierto escribiría `7701234567890` en "recibido" y el Enter
  confirmaría la venta.

### Atajos (`use-pos-shortcuts.ts`)

| Tecla | Acción |
| --- | --- |
| F2 | foco en la búsqueda (y selecciona su texto) |
| F9 | abrir el cobro (si "Cobrar" está habilitado; si no, dice por qué) |
| F8 | imprimir el ticket de la última venta, mientras se ve el panel de éxito |
| Esc | cierra el diálogo abierto (salvo con un intento incierto, ver «Diálogo de cobro»); sin diálogo, limpia la búsqueda |
| ↑ / ↓, + / −, Supr | navegar y editar líneas — **sólo con el foco dentro de la lista del carrito** |
| Enter (en el cobro) | confirmar la venta cuando cuadra |

Reglas del manejador, para que los atajos no se coman un escaneo:

- `+`, `−` y `Supr` **nunca** actúan si el foco está en un `input`, `textarea` o
  `contenteditable`, ni fuera de la lista del carrito: los códigos de producto llevan `-` (como
  `SH-400`), y un `−` global quitaría una unidad a mitad de escaneo.
- El panel de éxito no toma el foco y ningún botón suyo se activa con Enter: el foco sigue en la
  búsqueda, así que el escaneo del primer producto de la venta siguiente llega entero a la
  búsqueda (y su Enter lo busca, no imprime).
- Las teclas de función se capturan con `preventDefault` sólo dentro de `/pos`. Los atajos se
  muestran como `kbd` en los botones y en un `Tooltip`.

### Diálogo de cobro

```
┌──────────────────────────── Cobrar ─────────────────────────────┐
│                    Total a cobrar   $37.890                     │
│                                                                 │
│  [ Efectivo ]  [ Tarjeta ]  [ Transferencia ]                   │
│                                                                 │
│  Pagos                                                          │
│   Tarjeta        $20.000   ref. 1234                       ✕    │
│   Efectivo  recibido [ 20.000      ]                       ✕    │
│             [Exacto $17.890] [$18.000] [$20.000] [$50.000]      │
│                                                                 │
│  Falta por cubrir                      $0                       │
│  Cambio                                $2.110                   │
│                                                                 │
│  [ + Otro medio de pago ]                                       │
│                                                                 │
│              [ Cancelar  Esc ]   [ Confirmar venta  Enter ]     │
└─────────────────────────────────────────────────────────────────┘
```

- Al abrir: genera (o reutiliza) `pendingSaleId`, marca `payOpen` (el carrito deja de aceptar
  cambios) y congela una copia de las líneas y del `total` del preview; el cuerpo se arma con esa
  copia. Arranca con **una** línea Efectivo, con el input de recibido enfocado y vacío. Escribir
  un número y Enter confirma si alcanza.
- **Líneas de pago** (`build-payments.ts`, puro y probado aparte):
  - mientras haya una sola línea, los botones Efectivo / Tarjeta / Transferencia la
    **reemplazan**: Tarjeta deja una línea Card por el total, sin efectivo;
  - "+ Otro medio de pago" agrega una línea (hasta 5, una sola de efectivo); con dos o más, cada
    una se edita en su sitio y se quita con ✕;
  - al armar el cuerpo, la línea Efectivo se **omite** si `cashDue = 0` (las otras ya cubren el
    total), salvo con total 0, que va como `{ method: "Cash", tendered: 0 }`. Así "sólo tarjeta"
    y "sólo transferencia" nunca mandan un efectivo vacío que el servidor rechazaría con
    `cash_payment_unneeded`;
  - la línea Efectivo manda `tendered` y nunca `amount`;
  - con total 0, el diálogo muestra una sola línea Efectivo con recibido fijo en $0 (input
    deshabilitado, sin botones rápidos ni "+ Otro medio de pago"), porque el servidor rechaza
    cualquier recibido mayor (`pos.sale.tendered_invalid`).
- `quick-cash-amounts.ts` (en centavos): "Exacto", y los siguientes múltiplos de 1.000, 10.000 y
  50.000 por encima del faltante, más 100.000 si el faltante es menor; sin repetidos, máximo 4
  botones.
- Tarjeta y Transferencia: monto (por defecto el faltante) y referencia opcional. El monto no
  puede pasar del faltante; el input lo limita y explica por qué. Los inputs de dinero aceptan
  hasta 2 decimales y no más de $100.000.000 en el recibido.
- "Falta por cubrir" y "Cambio" se calculan en vivo, **en centavos enteros**, con el `total` del
  preview — es aritmética de pagos, no de impuestos; el servidor vuelve a validar todo.
- "Confirmar venta" se deshabilita mientras falte algo **o** mientras el request está en vuelo
  (texto "Registrando…"). Al confirmar, `beginAttempt(body)` congela el cuerpo exacto que se
  manda. El doble clic no puede mandar dos requests, y si los mandara, id y cuerpo son los
  mismos.
- **Éxito**: el diálogo se cierra, el carrito queda vacío y el foco vuelve a la búsqueda. En el
  lugar del carrito aparece `SaleSuccessPanel`, **no modal**: check, "Venta POS-000042
  registrada", "Entrega de cambio $2.110" en grande si hay cambio, y los botones "Imprimir
  ticket F8" y "Nueva venta". Imprime sólo con F8 o con clic, nunca con Enter; después de un
  clic el foco vuelve a la búsqueda. El primer producto que entra al carrito (escaneo o clic) o
  "Nueva venta" quitan el panel: la venta siguiente empieza sola.
- **4xx definitivo** (ver «Ciclo de vida del id»: en el primer envío, 400, 403, 404 y 422; desde
  un intento incierto, sólo `pos.sale.discount_not_allowed` y los 422 `pos.*` del paso 2 en
  adelante): nada quedó guardado. Se descarta el intento y el id (el próximo cobro genera otro) y
  se muestra el mensaje de `POS_CODE_MESSAGES` en el diálogo.
  `pos.sale.price_changed` cierra el cobro, invalida el preview (que vuelve con precio y tasa
  nuevos), y el efecto de refresco, que ya no está bloqueado, aplica `refreshExpected`, dispara el
  aviso y deja corregir.
  `pos.sale.session_mismatch` / `pos.session.not_open` mandan a la pantalla de apertura con el
  carrito intacto: la clave de `persist` depende del tenant y del usuario, no de la caja, y al
  abrir la nueva
  `adoptSession` conserva las líneas.
- **401**: el intento queda incierto y bloqueado; la pantalla manda a reautenticar y, al volver,
  el diálogo incierto ofrece "Reintentar" con el mismo cuerpo.
- **Intento incierto** (`NetworkError`, timeout, 5xx, 412 o cualquier status no listado): no se
  sabe si la venta quedó. El diálogo **no se puede cerrar** (Esc y el clic afuera no hacen
  nada), el carrito queda bloqueado y la pantalla dice "No pudimos confirmar si la venta quedó
  registrada." con dos acciones:
  - **Reintentar**: `retryAttempt()` y reenvía `attempt.body` con el mismo id. Si la primera
    llegó, la respuesta es la venta ya creada (200); si no, se crea (201). Un `403
    authorization.denied` (o cualquier 403 del nivel de tenancy, o un `validation.failed`) **no**
    libera nada: sale del paso 1, antes de que el servidor busque la repetición, y no dice si la
    primera quedó. El diálogo sigue incierto y dice "No tienes permiso para registrar ventas en
    este momento. La venta sigue sin confirmar: cuando te devuelvan el acceso, reintenta o
    verifica." Sólo `pos.sale.discount_not_allowed` y los 422 `pos.*` (pasos 2 a 6) son
    definitivos aquí.
  - **Verificar**: `GET /pos/sales/{attempt.saleId}`. `200` → pasa al éxito con esa venta. `404
    pos.sale.not_found` → "Todavía no aparece. Reintentar la registra una sola vez", y **sigue
    bloqueado**: el primer request puede seguir en camino y llegar después, y sólo reintentar con
    el mismo id lo cubre. `403` (al cajero le quitaron `pos.sale.read` a mitad de turno, o se
    apagó la capacidad `pos`) **tampoco** resuelve nada: "No pudimos verificar la
    venta. Reintentar la registra una sola vez", y sigue incierta. Cualquier otro status de
    Verificar, igual: sólo un `200` saca del bloqueo por este camino.

  La única salida del bloqueo es una respuesta definitiva para ese id (2xx del POST, un 4xx que
  salga del paso 2 en adelante, o `200` de Verificar). Un 403 no lo es en ninguno de los dos
  botones. Un F5 en este estado vuelve al mismo diálogo incierto (el intento está en
  `sessionStorage`), aunque entre tanto la caja se haya cerrado o haya otra abierta (ver «Por qué
  la caja no va en la clave»). Si en esa pestaña entra otro usuario, no lo ve: el intento está
  bajo la clave de quien lo hizo (ver «Por qué el usuario sí va en la clave»).

### Ticket (`pos-ticket.tsx`)

Se imprime con `printWithTitle(saleNumber)`, que hoy vive en `features/quotes/utils/`: como
ahora lo usan dos features, se mueve a `src/lib/print-with-title.ts` (Screaming Architecture,
`SDD-ADR-07`).

**Reusa el mecanismo `data-print-region` de `src/index.css:339-405`** en vez de un segundo
sistema de impresión. `ticket-print-portal.tsx` monta el ticket en un portal sobre
`document.body` **sólo mientras imprime** (monta → `printWithTitle` → desmonta en `afterprint`),
con `<div data-print-region data-pos-ticket>`. `printWithTitle` se llama desde un
`useLayoutEffect` del portal, ya montado, y nunca en el mismo handler que hace el `setState` que
lo monta: ahí React todavía no escribió el ticket en el DOM y se imprimiría la página vacía. Las
reglas existentes ya esconden todo lo demás y aplanan los ancestros; la pantalla de caja no tiene
otra `data-print-region`, así que no compiten.

Lo único propio es la **página con nombre**, en `features/pos/pos-print.css`:

```css
@page pos-ticket { size: 80mm 297mm; margin: 0; }

@media print {
  [data-pos-ticket] { page: pos-ticket; width: 72mm; padding: 4mm; }
}
```

- `size: 80mm auto` **no es CSS válido** (`size` no acepta `auto` por eje): el navegador
  descartaría la regla entera. Se usa un alto fijo de rollo (297 mm) y el driver de la impresora
  térmica, configurado en papel continuo con corte automático, corta donde termina el contenido.
  Probarlo con la impresora del tenant es parte de la aceptación del slice.
- La página es **con nombre**: sólo la toma el elemento con `page: pos-ticket`. Un `@page` sin
  nombre sería global y cambiaría a 80 mm la impresión del pedido (`order-detail-page.tsx:161`)
  y del resumen de conversión, que hoy salen en el papel por defecto (A4/carta).
- `page:` lo soportan Chrome y Edge, que es lo que corre en un mostrador; en otro navegador el
  ticket sale en el papel por defecto del driver, sin romper nada.

Contenido, en `font-mono` 11 px, negro sobre blanco, sin colores del tema:

1. Nombre de la empresa (negrita, centrado), `NIT 900123456-1`, dirección, teléfono.
2. `Venta POS-000042` · fecha y hora local (`createdAtLocal`, formato `dd/MM/yyyy HH:mm`) ·
   `Cajero: Laura Gómez`.
3. Cliente: `Consumidor final` y `222222222222` (de `customer`, no fijo en la pantalla).
4. Líneas: nombre (puede partir), y debajo `2 x 11.900  -10%  21.420`.
5. Subtotal, desglose de IVA por tasa (`IVA 19% base 18.000 = 3.420`), **TOTAL** y, si hubo descuento,
   `Descuentos incluidos 2.380` **después** del TOTAL, fuera de la suma (decisión 53).
6. Pagos (`Tarjeta 20.000 ref 1234`, `Efectivo 20.000`) y `Cambio 2.110`.
7. Pie: "Documento interno de venta. No es factura electrónica." (ver DIAN fuera de alcance).
8. Si está anulada: "ANULADA" grande arriba y el motivo.

### Apertura y cierre

- **Sin caja abierta**, `/pos` muestra `OpenSessionForm` centrado (`max-w-md`): empresa (sólo si
  `defaultCompanyId` es null; `Select` con las de `companies`), "Base de efectivo" con
  `CurrencyInput` (componente compartido existente), botón "Abrir caja". Sin empresas activas:
  "Todavía no hay ninguna empresa emisora activa. Pídele a un administrador que active una en
  Empresas." sin formulario.
- **`/pos/close`**: resumen vivo (base, ventas, anuladas, totales por medio, efectivo esperado),
  input "Efectivo contado" (hasta 2 decimales), diferencia en vivo calculada en centavos (verde
  si 0, `warning` si sobra, `destructive` si falta), nota opcional, "Cerrar caja". El cierre
  manda `If-Match` con la `version` del `GET /pos/register` que pintó el resumen. Un `412` o un
  `pos.session.not_open` no se dan por error sin mirar: la pantalla pide
  `GET /pos/sessions/{id}`. Si la caja ya está `Closed` (el cierre anterior llegó y se perdió la
  respuesta), muestra ese resumen como éxito. Si sigue `Open`, recarga el resumen y dice
  "Entraron movimientos mientras contabas. Revisa el efectivo esperado antes de cerrar." sin
  borrar lo contado. Después del cierre, `ClosingSummary` con "Imprimir cierre" (mismo portal de
  80 mm) y "Abrir otra caja".

### Ventas y cajas

- `/pos/sales`: `DataTable` con número, hora local, cajero (sólo con `pos.register.read`),
  cliente, medios, total, estado (`Badge`), y acciones "Reimprimir" y "Anular". "Anular" sólo se
  dibuja con `pos.sale.void`; con el permiso, se habilita con `voidable` de cada fila, sin que
  la pantalla deduzca nada; si es `false`, queda deshabilitado con un `Tooltip` sacado de
  `VOID_BLOCKED_REASON_LABELS[voidBlockedReason]` ("Ya está anulada", "La caja de esta venta ya
  se cerró"). Filtros: hoy por defecto, rango, estado, número. `VoidSaleDialog` pide motivo
  (3–500) y confirma. Si la respuesta es `pos.sale.already_voided` (su request anterior llegó y
  se perdió la respuesta, o alguien más la anuló), recarga la venta y cierra como éxito.
- `/pos/sessions`: tabla de cajas (cajero sólo con `pos.register.read`, empresa,
  abierta/cerrada, horas, total, diferencia) y detalle en diálogo con el resumen de cierre e
  "Imprimir cierre". Un cajero ve sus propias cajas y puede reimprimir el cierre de cualquier día.

### Menú y aterrizaje

`sidebar-nav-items.ts`: ítem `{ to: '/pos', label: 'Punto de venta', icon: Store, permission:
'pos.sale.create', alternates: [{ permission: 'pos.sale.read', to: '/pos/sessions' }] }`,
después de Pedidos (`alternates` ya existe, `sidebar-nav-items.ts:59,227`). Quien lee sin vender
(un supervisor con `pos.sale.read` + `pos.register.read`) entra por las cajas. `Store` no choca
con los nueve íconos usados. `SidebarRoute` suma `'/pos'` y `'/pos/sessions'`. El ítem admite un
solo permiso (`sidebar-nav-items.ts:54`), así que un rol custom con `pos.sale.create` sin
`pos.register.operate` o sin `pos.sale.read` lo ve y cae en la tarjeta de permiso de `PosGate`;
los roles de sistema que venden (`admin`, `cashier`) llevan los tres.

`landing.ts`: `Landing` suma `'/pos'`; `landingFor` manda a `/pos` a quien tiene
`pos.sale.create`, `pos.register.operate` y `pos.sale.read` —los tres del gate de la caja— y no
tiene ni `quotations.quotation.read` ni `quotations.order.read`. Un cajero abre directo en la
caja. Coordinar con el cambio de aterrizaje del spec de entitlements: los dos
tocan la misma función.

### Estados vacío, error y carga

| Lugar | Carga | Vacío | Error |
| --- | --- | --- | --- |
| Caja | esqueleto de grilla y carrito | carrito: "Escanea un producto o búscalo para empezar." | tarjeta con "No pudimos cargar la caja" y Reintentar |
| Grilla | 8 tarjetas esqueleto | "Ningún producto coincide con «…»." | inline sobre la grilla, Reintentar |
| Preview | números anteriores + indicador | — | aviso inline "No pudimos actualizar los totales", "Cobrar" deshabilitado |
| Ventas / Cajas | filas esqueleto | "No hay ventas en este periodo." | patrón de listados existente |

### Accesibilidad

- Grilla como `<ul role="list">` con cada tarjeta dentro de su `<li>` (un `role="list"` cuyos
  hijos directos son botones no es una lista válida para los lectores de pantalla); el botón
  lleva `aria-label="Agregar Shampoo 400 ml, $11.900"`. El carrito, igual: `<ul>` de `<li>`.
- Total y cambio en una región `aria-live="polite"`; el resultado del escaneo, en otra.
- Diálogos de `components/ui/dialog` con foco atrapado y devuelto a la búsqueda al cerrar.
- Objetivos táctiles ≥ 44 px (pantallas táctiles de mostrador); "Cobrar" y "Confirmar venta"
  ≥ 56 px de alto.
- Contraste con los pares ya medidos del tema; diferencia de caja nunca sólo por color (signo y
  texto "Faltante" / "Sobrante").

## Errores y casos borde

| Caso | Qué pasa |
| --- | --- |
| Abrir con caja ya abierta (otra pestaña) | `pos.session.already_open`; la pantalla recarga `GET /pos/register` y muestra la abierta |
| Dos aperturas simultáneas | el índice parcial frena la segunda; misma respuesta |
| Cerrar con una venta en vuelo | quien commitea segundo choca en la `Version` de la caja (412); si perdió la venta, su reintento ve `pos.session.not_open` y la pantalla manda a abrir caja con el carrito intacto |
| Producto desactivado entre la búsqueda y el cobro | preview lo marca `Inactive`, lo saca del total y "Cobrar" queda bloqueado hasta quitarlo; la venta da `pos.sale.product_inactive` si igual llega |
| Producto borrado | el preview lo marca `NotFound` (nombre del snapshot del carrito) y bloquea "Cobrar"; la venta da `pos.sale.product_not_found` si igual llega |
| Precio o IVA cambiado | el preview trae el valor nuevo; `refreshExpected` actualiza precio **y** tasa esperados de la línea y aparece el aviso; la venta con cualquiera de los dos viejo da `pos.sale.price_changed` (4xx definitivo: id nuevo en el próximo cobro) |
| Cambió sólo la tasa de IVA (mismo precio) | el total no se mueve, pero la línea se refresca igual y el aviso dice "El IVA de … cambió"; sin eso, cada cobro volvería a dar `price_changed` sin que la pantalla mostrara nada que corregir |
| Cambio de precio o tasa con el cobro abierto | `refreshExpected` devuelve `'locked'`; el POST da `price_changed`, el cobro se cierra, el preview se invalida y vuelve con el valor nuevo, y el efecto de refresco —que depende también del bloqueo— lo aplica al desbloquearse el carrito. "Cobrar" se habilita sin que el cajero toque nada |
| `price_changed` con el preview en caché igual a lo esperado (el precio cambió después del último preview) | la invalidación explícita trae el precio nuevo; sin ella, el `linesKey` no cambia, no sale otro preview y cada cobro volvería a fallar |
| Doble clic en confirmar | botón deshabilitado en vuelo; mismo id y mismo cuerpo de todos modos |
| Dos requests idénticos en paralelo | el contador serializa; el segundo choca casi siempre en la `Version` de la caja (412) y a veces en `PK_sales`. En los dos casos el handler limpia el contexto, relee y devuelve 200 con la venta del primero |
| Corte de red después de enviar | carrito y cobro bloqueados; "Reintentar" con el mismo id y el mismo cuerpo → 200 si había llegado, 201 si no; "Verificar" consulta `GET /pos/sales/{id}` |
| Mismo id con otro carrito (cliente con un bug) | la huella no coincide → `pos.sale.id_conflict`; la venta vieja nunca se presenta como la nueva |
| Id de cliente que ya es PK de una venta de otro tenant | `PK_sales` → relee en el tenant, no está → `pos.sale.id_conflict` terminal; el cliente genera otro id |
| F5 en medio del cobro | carrito, `pendingSaleId` y el intento vuelven de `sessionStorage`; un intento en vuelo vuelve como incierto y el reintento es idempotente |
| Intento incierto y la caja ya no es la abierta (cerrada en otra pestaña, o hay otra abierta) | la clave de `persist` no lleva la caja (sólo tenant y usuario), así que el intento sigue ahí: `/pos` muestra primero el diálogo incierto, aunque no haya caja o sea otra. Reintentar → `200` si había llegado; si no, `session_mismatch` / `not_open` (4xx definitivo) libera el carrito, que sigue con sus líneas para la caja nueva |
| Caja reabierta sin intento pendiente | `adoptSession`: el carrito sigue, con la caja nueva en `sessionId`, y el preview revalida |
| La caja cambia con el cobro abierto y sin intento (cerrada y reabierta en otra pestaña; lo ve el refetch de `GET /pos/register`) | `adoptSession` cierra el cobro (`payOpen = false`), pone `pendingSaleId` en null y avisa "La caja cambió mientras cobrabas"; el cobro siguiente se arma con la caja nueva |
| Cajera A deja un intento incierto, cierra sesión y entra el cajero B en la misma pestaña | la clave es `qep.pos.cart.{tenantId}.{userId}`: B no rehidrata el intento de A ni su carrito, y no puede reintentarlo con su identidad. El intento de A reaparece cuando A vuelve a entrar en esa pestaña |
| Verificar responde 403 | sigue incierta y bloqueada, con "No pudimos verificar la venta"; sólo un `200` de Verificar o una respuesta definitiva del POST la resuelven |
| Reintentar responde `403 authorization.denied` (permiso quitado, capacidad apagada, membresía suspendida) | sigue incierta y bloqueada: ese 403 sale antes de que el servidor busque la repetición y no dice si la primera quedó. Sólo `pos.sale.discount_not_allowed` y los 422 `pos.*` del paso 2 en adelante son definitivos desde un intento incierto |
| Cerrar caja con un cobro incierto | `/pos/close` no deja cerrar hasta resolverlo en la caja |
| Escaneos seguidos más rápidos que la red | el input se limpia en cada Enter y la cola resuelve en orden; ningún código se concatena con el siguiente |
| Escanear un código con `-` (p. ej. `SH-400`) con una línea seleccionada | el foco está en la búsqueda: `−` se escribe, no quita unidades |
| Escanear con el panel de éxito a la vista | el foco está en la búsqueda: el código entra entero, su Enter lo busca, el producto empieza la venta siguiente y el panel se va. Nada imprime |
| Escanear con el cobro abierto | el guardia de ráfagas descarta el código y el Enter del input de dinero; la venta no se confirma |
| Escaneo pendiente al pulsar F9 | "Cobrar" y F9 deshabilitados hasta que la cola se vacíe |
| F9 con el preview en vuelo | no abre; "Cobrar" dice "Calculando el total…" |
| Pago sólo con tarjeta o sólo con transferencia | el cuerpo lleva sólo esa línea; el efectivo vacío no viaja |
| 401 con un intento en vuelo | sigue bloqueado; reautenticar y "Reintentar" con el mismo cuerpo |
| Respuesta del cierre perdida | el reintento da 412 o `not_open`; la pantalla consulta la caja y, si está `Closed`, muestra su resumen |
| Respuesta de la anulación perdida | el reintento da `pos.sale.already_voided`; se toma como éxito |
| Cerrar caja con productos en el carrito | confirmación antes de descartarlos |
| Caja abierta desde ayer | banner con la fecha de apertura (`openedBeforeToday`) |
| Cajero sin `pos.sale.discount` | no ve el botón de descuento; si el request llega igual, `403 pos.sale.discount_not_allowed` |
| Cierre con un arqueo viejo en pantalla | la `version` del `If-Match` no coincide → 412; la pantalla recarga el resumen antes de dejar cerrar |
| Cajero suspendido a mitad de turno | su próxima request es 403 (sin membresía activa); la caja queda abierta hasta que vuelva (cierre forzado: `DECISIÓN-PENDIENTE`) |
| Empresa desactivada con caja abierta | la caja sigue con su snapshot; abrir una nueva con esa empresa falla |
| Empresa borrada | imposible mientras tenga cajas: `422 companies.company.in_use` |
| Capacidad `pos` apagada a mitad de turno | permisos enmascarados → 403 en todo `/pos`; los datos quedan; al volver a prenderla la caja sigue abierta. Un intento incierto sigue incierto (ni el 403 de Reintentar ni el de Verificar lo liberan) |
| Anular venta de una caja cerrada | `pos.sale.void_session_closed`; la lista ya lo mostraba con `voidable: false` y `SessionClosed` |
| Id de venta o de caja que no existe en el tenant | `404 pos.sale.not_found` / `pos.session.not_found`; el de otro tenant responde igual |
| Total 0 | sólo con `pos.sale.discount`; un pago `Cash` con recibido 0. Sin el permiso, el preview responde 200 con `zeroTotalNotAllowed` y sólo el POST da 403 |
| Total 0 con recibido > 0 (API directa) | `pos.sale.tendered_invalid`: una venta en cero no recibe billete ni da cambio. La pantalla fija el recibido en $0 |
| Capacidad `pos` apagada al entrar a `/pos/*` | el layout `pos.tsx` con `ModuleGate` muestra su mensaje y no monta la página: ninguna consulta a `/pos/*` sale |
| Carrito > 200 líneas | el store no deja agregar la 201 y lo dice |
| Cantidad 0,005 | el input redondea a 2 decimales en blur; el servidor rechaza más de 2 |
| Monto con más de 2 decimales (API directa) | `422 validation.failed` con el campo; nunca se redondea en silencio |
| Rol custom `cashier` creado antes del despliegue | lo detecta el chequeo de «Despliegue» antes de subir; después, `Role.Create` lo rechaza |

## Despliegue

Orden: el spec de entitlements primero (POS depende de su tabla, su enmascarado y la firma nueva
de `PermissionDefinition`), después POS. Prender `pos` en un tenant es el SQL de «Operación» de ese
spec.

**Chequeo previo: ningún rol custom con la clave `cashier`.** Reservar la clave en
`SystemRoleKeys` sólo impide crearlo de aquí en adelante; uno que ya exista pisaría al de sistema
en silencio en su tenant (`TenantRoleCatalog.cs:88-106`). Antes de desplegar, contra la base de
cada ambiente. El SQL va a un archivo porque PowerShell 5.1 rompe las comillas dobles que
`"authorization"` necesita (es palabra reservada de PostgreSQL) al pasarlas a un `.exe`:

```powershell
Set-Content -Path check-cashier-role.sql -Encoding ascii -Value 'SELECT tenant_id, id, display_name FROM "authorization".roles WHERE key = ''cashier'';'
Get-Content check-cashier-role.sql | docker exec -i postgres18 psql -U postgres -d dev_lulo_crm_v2
```

`(0 rows)` → se despliega. Con alguna fila, el despliegue se detiene y se decide con el owner:
renombrar la clave de ese rol custom (y sus referencias en `tenancy.memberships.roles`) antes de
subir. En producción, la misma sentencia por el acceso a la base que QCode ya usa para
operaciones manuales.

## Pruebas (TDD: RED antes que GREEN, con evidencia literal)

**Unitarias de dominio** (`Modules.Pos.UnitTests`):

- `VatIncludedLine.Compute`: la tabla del ejemplo trabajado; tasa 0; descuento 100 %; punto
  medio (`x,xx5` sube, `AwayFromZero`); cantidad con 2 decimales.
- Paridad: los casos existentes de `QuotationItem` siguen verdes después de la extracción
  (Quotations.UnitTests sin cambios).
- `PosSale.Create`: totales del ejemplo al centavo; desglose por tasa; las 8 reglas de pago con
  un caso que pasa y uno que falla cada una; nada asignado si un pago falla; escala: cantidad,
  descuento, monto y recibido con 3 decimales lanzan su código (no redondean); recibido sobre
  100 000 000 o ausente lanza `tendered_invalid`; `Cash` con `amount` lanza
  `cash_amount_not_allowed`; total 0 guarda `Cash` con `Amount = 0`; total 0 con
  `Tendered = 5 000` lanza `tendered_invalid` y no asigna nada.
- `PosSale.Void`: motivo obligatorio; dos veces lanza `already_voided`.
- `CashSession`: `Open` deja `Version = 1`; `RegisterSale` suma por medio y mueve `Version`;
  `RegisterVoid` resta exacto; `Close` congela esperado y diferencia con signo; vender, anular o
  cerrar una cerrada lanza; base y contado fuera de rango o con 3 decimales lanzan `*_invalid`.
- `PosSaleFingerprint`: mismo cuerpo → misma huella; `2` y `2.00` → misma huella; cambiar una
  cantidad, un producto, el orden de las líneas o un pago → huella distinta; `reference: null` y
  `reference: ""` → huellas distintas; una referencia con `|`, `,` o `"` no reproduce la huella
  de otro reparto de campos.

**Unitarias de aplicación** (dobles de puertos):

- Crear venta: repetición del mismo cajero con la misma huella → misma venta, `SaveChanges` no se
  llama; id de otro cajero → `id_conflict`; mismo cajero con otra huella → `id_conflict`;
  producto inactivo, sin precio, precio cambiado, **sólo la tasa cambiada con el mismo precio**
  → `price_changed`; el contador no se pide si una validación falla.
- Choque al guardar, **los dos órdenes**: el doble de `PosUnitOfWork` lanza
  `RequestConcurrencyException` en el primer guardado y después el repositorio encuentra la venta
  → 200 con ella y `ResetAsync` llamado; lo mismo con `pos.sale.id_taken`; concurrencia sin venta
  → 412 relanzado; `id_taken` sin venta en el tenant → `id_conflict`.
- Descuento: línea con 10 % sin `pos.sale.discount` → 403 `pos.sale.discount_not_allowed` en
  preview y en venta, antes de pedir número; total 0 sin el permiso → preview 200 con
  `zeroTotalNotAllowed: true` y venta 403; con el permiso la auditoría lleva
  `discount:1:SH-400:10` en `changedFields`.
- Preview: producto que no existe en el tenant → línea `NotFound` fuera de los totales, no 422.
- La venta guarda siempre el snapshot de `PosFinalConsumer`.
- Autorización: tenant distinto → 403; sin permiso → 403; venta o caja ajena sin
  `pos.register.read` → 403; id inexistente en el tenant → 404 `pos.sale.not_found` /
  `pos.session.not_found`; el cajero lee su propia caja cerrada sin `pos.register.read`.
- Cerrar caja: `ExpectedVersion` distinta → 412 sin tocar la caja; caja ajena → 403.
- `voidable`: `Completed` + caja abierta → `true`, también para quien no tiene `pos.sale.void`;
  anulada → `AlreadyVoided` aunque la caja esté cerrada; completada con caja cerrada →
  `SessionClosed`.
- Abrir caja: empresa por defecto con una, `company_required` con dos, `no_active_company` con
  cero, empresa de otro tenant → `company_not_found`.
- `GET /pos/register`: `openedBeforeToday` con una caja abierta ayer en el huso del tenant (reloj
  fijo).
- `PosProductLookup` (Bootstrapper): coincidencia exacta; mayúsculas distintas → `null`; inactivo
  vuelve marcado.
- Validadores: nota, motivo, referencia, rangos concretos (100 000 000 / 1 000 000 000) y escala
  de 2 decimales en cada campo de dinero, descuento y cantidad.

**Integración** (`Modules.Pos.IntegrationTests`, Testcontainers, stub con `X-Permissions`):

*Harness.* Con el spec de entitlements, el stub **sí** enmascara cuando el tenant existe en
`tenancy.tenants`, y POS necesita que exista: el cajero se resuelve a `MemberId` por
`IMembershipDirectory.FindActiveMembershipIdAsync`, que lee membresías reales. Así que
`PosApiHarness`:

1. registra el tenant por `register-tenant`, como `QuotationsApiHarness.RegisterTenantAsync`
   (`:136`); el dueño queda con membresía activa;
2. inserta por SQL (o con un helper de prueba que haga ese insert) la fila `pos` en
   `tenancy.tenant_modules` (`source = 'manual'`). Ni el backfill ni el signup la prenden: tras la
   ronda 2 de entitlements ya no existe `Entitlements:DefaultModules`, sólo el booleano
   `GrantDefaultModulesOnSignup`, que concede seis módulos fijos sin `pos`. No hay configuración
   que la harness pueda sobrescribir, y sin la fila todo `/pos/*` da 403 aunque el header pida
   los permisos;
3. siembra empresa, tasas y productos por la API de Companies y Catalog (como
   `QuotationsApiHarness.cs:377,404,554`), y un segundo cajero con
   `ReportingApiHarness.InviteActiveAdvisorAsync` (`:178`) como modelo.

Casos:

- Abrir → vender (efectivo, tarjeta, dividido) → cerrar, verificando cuerpos completos, `paymentTotals`
  con los tres medios y la diferencia.
- Numeración: `POS-000001`, `POS-000002`; un 422 entre medio no deja hueco.
- Idempotencia: mismo POST dos veces → 201 y 200, una fila, un número; mismo id con otra cantidad
  → 422 `pos.sale.id_conflict` y la venta original intacta; dos POST idénticos en paralelo,
  repetido 20 veces → cada vez una fila, las dos respuestas 2xx y el mismo `saleNumber`.
- Índice parcial: segunda apertura → `pos.session.already_open`.
- Concurrencia cierre/venta, **determinista**: dos `PosDbContext` sobre la misma caja; A la carga,
  B registra una venta y commitea, A cierra y guarda → `DbUpdateConcurrencyException` traducida a
  412; el arqueo final cuadra con la venta de B. Más el caso HTTP: `If-Match` con la versión
  anterior a una venta → 412; sin `If-Match` → 428.
- Anulación: con caja abierta descuenta del arqueo; con caja cerrada da `void_session_closed`;
  la lista devuelve `voidable` y `voidBlockedReason` coherentes.
- Auditoría: cada operación deja su fila en `platform.outbox_messages` en la misma transacción
  (y ninguna si falla); la venta con descuento lleva sus entradas en `changedFields`.
- Aislamiento: ruta con un tenant que no es el del llamador → 403; id de una venta de otro tenant
  bajo la ruta propia → 404 `pos.sale.not_found`, en GET y en anular.
- Las seis políticas resuelven (403 y no 500 sin el permiso).
- `GET /pos/products/by-code`: `SH-400` exacto → 200; `sh-400` → 404.
- Pagos: sólo tarjeta y sólo transferencia → 201 sin línea `Cash`; `Cash` con `amount` → 422;
  total 0 con `tendered: 1000` → 422 `pos.sale.tendered_invalid`, sin fila ni número gastado.
- Precio igual y tasa del producto cambiada después del preview → 422 `pos.sale.price_changed`.
- Borrar una empresa con caja → `422 companies.company.in_use`. Vive aquí y no en
  Companies.IntegrationTests: la FK la crea la migración de Pos, y sólo este harness la tiene.
- `PosUserReferenceProbe`: un cajero con ventas no se borra al quitarlo.
- Con el spec de entitlements: se borra la fila `pos` → 403 en `/pos/register`.

**Integración, Catalog**: `FindByCodeAsync` distingue mayúsculas.

**Integración, Authorization**: crear un rol custom `cashier` → 422
`authorization.role.key_reserved`.

**Arquitectura**:

- `PosLayerTests.cs` (copia de `CompaniesLayerTests`): capas, Application sin EF/Npgsql, y
  Application sólo referencia Tenancy entre los módulos de negocio.
- `CompositionRootTests`: los handlers nuevos registrados (la prueba ya existe; sólo hay que sumar
  el ensamblado de Pos a los que descubre si los lista a mano).
- Prueba del mapa permiso → módulo del spec de entitlements: los seis `pos.*` mapean a `pos`.

**Frontend** (Vitest + Testing Library):

- `pos-cart-store`: sumar el mismo producto acumula; la línea 201 devuelve `'limit'`; con
  `payOpen` o `attempt` no nulo, `addProduct` devuelve `'locked'` y `setQuantity`,
  `setDiscount`, `removeLine` no cambian nada; `pendingSaleId` estable hasta un resultado
  definitivo; `resolveAttempt('definitive')` → `pendingSaleId` y `attempt` en null;
  `resolveAttempt('uncertain')` (401, status no listado, o 403 de Reintentar) → mismo id y mismo
  cuerpo, bloqueado; intento `inFlight` rehidratado como `uncertain` con `wasUncertain = true`. `refreshExpected` cambia precio y tasa esperados de todas las líneas del producto
  y devuelve `'updated'`; con `payOpen` o `attempt` no nulo devuelve `'locked'` y no cambia nada.
  Persistencia: la clave es `qep.pos.cart.{tenantId}.{userId}` y no lleva la caja; un estado
  guardado con `sessionId` A y un intento `uncertain` se rehidrata entero aunque la caja abierta
  sea B o no haya ninguna; `adoptSession(B)` sin intento conserva las líneas, pone
  `pendingSaleId` en null y no toca `attempt`; `adoptSession(B)` con `payOpen` y sin intento pone
  `payOpen` en false; dos tenants no comparten carrito; **el usuario B no rehidrata el intento del
  usuario A** (mismo tenant, misma pestaña: el estado de A guardado bajo su clave, el store
  hidratado con el `userId` de B arranca vacío y sin `attempt`, y la clave de A sigue intacta).
  `retryAttempt()` vuelve a `inFlight` y conserva `wasUncertain = true`.
- `sale-attempt-outcome`: con `wasUncertain = false`, 400, 403 (`authorization.denied` y
  `pos.sale.discount_not_allowed`), 404 y 422 → `'definitive'`; con `wasUncertain = true`,
  `403 authorization.denied`, `422 validation.failed`, 400 y 404 → `'uncertain'`, y
  `403 pos.sale.discount_not_allowed`, `422 pos.sale.session_mismatch`, `422 pos.sale.id_conflict`
  y `422 pos.sale.price_changed` → `'definitive'`; en los dos, 401, 412, 5xx, `NetworkError` y un
  409 → `'uncertain'`, y 200/201 → `'success'`.
- `money-cents`: `toCents(0.1) + toCents(0.2) === toCents(0.3)`; faltante de `37890 − 20000`
  exacto. Stepper: `1,1` con `+` da `2,1` exacto; `0,29` con `−` pide confirmar el quitar.
- `build-payments`: sólo tarjeta → `[Card total]`; sólo transferencia → `[Transfer total]`;
  tarjeta por el total más una línea Efectivo vacía → el efectivo no viaja; total 0 →
  `[{ Cash, tendered: 0 }]`; la línea Efectivo nunca lleva `amount`; un botón de medio con una
  sola línea la reemplaza.
- `use-scanner-burst-guard`: 13 caracteres a 5 ms y Enter en "recibido" → valor descartado,
  venta sin confirmar y mensaje visible; tecleo humano (> 30 ms) y Enter → confirma.
- `quick-cash-amounts`: faltante 17 890 → `[17890, 18000, 20000, 50000]`; sin duplicados.
- `format-pos-money`: `37890` → `'$ 37.890'`; `209.3` → `'$ 209,30'`; `0.5` →
  `'$ 0,50'` (el espacio de `Intl` es no separable; la prueba lo escribe así y no con un
  espacio común).
- `ProductSearchBar` / `use-scan-queue`: Enter con código exacto suma 1 sin esperar debounce y
  limpia el input en el acto; dos códigos tecleados seguidos con la primera respuesta pendiente
  → dos consultas en orden, sin concatenar, y el carrito en ese orden; 404 con el input vacío
  devuelve el texto, con el input ocupado no lo pisa; la búsqueda con debounce pendiente se
  cancela; con la cola no vacía "Cobrar" está deshabilitado; un escaneo con el carrito bloqueado
  muestra "No se agregó …".
- `PayDialog`: confirmar deshabilitado mientras falte (calculado en centavos); cambio en vivo;
  doble clic manda un request; `NetworkError` → Esc no cierra, el carrito no se edita,
  Reintentar manda el mismo id **y el mismo cuerpo**; Verificar con 200 pasa al éxito, con 404
  sigue bloqueado y **con 403 sigue bloqueado** con "No pudimos verificar la venta";
  **Reintentar respondido con `403 authorization.denied` sigue bloqueado** (Esc no cierra, el
  carrito no se edita, el id y el cuerpo no cambian) con el mensaje de permiso; Reintentar con
  `422 pos.sale.session_mismatch` sí libera; 422 en el primer envío → id nuevo en el próximo
  cobro; `price_changed` cierra, avisa e invalida el preview; 401 y un 409 (no listado) quedan
  inciertos. Con total 0, el recibido está fijo en $0 y el cuerpo lleva `tendered: 0`.
- Éxito: el foco queda en la búsqueda; **13 caracteres y Enter con el panel a la vista** → busca
  ese código, empieza la venta siguiente con ese producto y no imprime; F8 imprime; Enter nunca.
- Carrito con una línea `Inactive`, `PriceMissing` o `NotFound` (con el nombre del snapshot) →
  "Cobrar" deshabilitado con su texto; `zeroTotalNotAllowed` → "Cobrar" deshabilitado con el
  motivo.
- Sin `pos.sale.discount` no se dibuja el botón de descuento.
- `PosTicket`: renderiza emisor, desglose, pagos, cambio y "ANULADA"; el portal lleva
  `data-print-region` y `data-pos-ticket`, el ticket ya está en el DOM cuando se llama
  `window.print` (espía que lo comprueba) y se desmonta en `afterprint`.
- `SalesTable`: sin `pos.sale.void` no hay "Anular"; con él, sigue `voidable` y el tooltip sale
  de `voidBlockedReason`. `VoidSaleDialog`: `already_voided` → éxito.
- `PosCloseSessionPage`: manda `If-Match` con la versión; 412 con la caja `Open` recarga el
  resumen sin borrar lo contado; 412 o `not_open` con la caja `Closed` muestra su resumen.
- `PosRegisterPage`: banner con `openedBeforeToday`; "Cerrar caja" con carrito pide confirmar.
  **Sólo cambia la tasa**: carrito con `expectedUnitPrice` 11 900 y `expectedTaxPercentage` 19,
  el preview responde 11 900 y 5 → la línea queda con 5, aparece "El IVA de Shampoo 400 ml cambió
  del 19 % al 5 %.", "Cobrar" se habilita y el cuerpo del POST lleva `expectedTaxPercentage: 5`.
  **`price_changed` con el preview en caché al día con lo esperado**: el POST da
  `price_changed`, el preview se vuelve a pedir (un segundo request con el mismo cuerpo), trae
  12 500, la línea queda con 12 500, aparece el aviso y "Cobrar" se habilita. **Precio cambiado
  con el cobro abierto**: el preview trae 12 500 mientras `payOpen`; al cerrar el cobro sin
  cambiar el carrito, el refresco se aplica y "Cobrar" se habilita.
  **La caja cambia con el cobro abierto**: con el diálogo abierto y sin intento, el refetch de
  `GET /pos/register` trae la caja B → el diálogo se cierra, aparece "La caja cambió mientras
  cobrabas" y las líneas siguen.
  **Intento incierto con otra caja**: `sessionStorage` con un intento `uncertain` de la caja A y
  `GET /pos/register` con la caja B (y, en otro caso, con `session: null`) → se dibuja el diálogo
  incierto con Reintentar y Verificar, no la caja ni `OpenSessionForm`; Reintentar con
  `session_mismatch` libera el carrito con sus líneas.
- `PosCloseSessionPage` con un `attempt` no nulo: no deja cerrar y enlaza a la caja.
- `OpenSessionForm`: selector sólo con `defaultCompanyId` null; mensaje sin empresas.
- `PosGate` de `/pos`: con dos de `pos.sale.create` / `pos.register.operate` / `pos.sale.read`
  (cualquier par) muestra la tarjeta de permiso; con los tres, la caja.
- Layout `pos.tsx`: con `pos` sin contratar (`useTenantModules` mockeado como en las pruebas de
  `ModuleGate` del spec de entitlements) muestra el mensaje de `ModuleGate` y no sale ningún
  request a `/pos/register`. `PosGate` no importa `useTenantModules`.
- `visibleSidebarItems`: Punto de venta con `pos.sale.create`; alterno a `/pos/sessions` con
  `pos.sale.read`.
- `landingFor`: cajero → `/pos`; sin cualquiera de `pos.sale.create`, `pos.register.operate` o
  `pos.sale.read`, no.
- `use-pos-shortcuts`: F2, F8, F9, Esc; F9 con el preview pendiente no abre y da aviso; `−` con
  el foco en la búsqueda se escribe y no toca el carrito; `+`/`−`/`Supr` sólo con el foco en la
  lista.
- Accesibilidad: la grilla y el carrito son `ul` con `li` (`getAllByRole('listitem')`).

## Decisiones tomadas sin el owner

1. **Módulo propio `Pos`**, con agregado de venta propio: `Order` no tiene líneas y es 1:1 con una
   cotización (`Order.cs:65-68`), así que no sirve para una venta de mostrador.
2. **Capacidad `pos`** con dependencias `catalog` y `companies`, y sólo esas (igual que la tabla
   del spec de entitlements): ni `customers` ni ninguna opcional. Sin selección de cliente,
   `customers` no se toca. Se aplica por enmascarado de permisos (spec de
   entitlements); POS no usa `TenantModuleGuard` ni `ITenantModules`.
3. **Permisos** `pos.sale.read`, `pos.sale.create`, `pos.sale.discount`, `pos.sale.void`,
   `pos.register.operate`, `pos.register.read`; `admin` los tiene todos; anular es sólo admin.
4. **Rol `cashier` sin `catalog.product.read` ni `customers.customer.read`**, a diferencia del
   brief. POS tiene su propio `/pos/products`, que devuelve sólo lo que la caja dibuja, y no
   elige clientes; con esos dos permisos el cajero vería en el menú Productos y Clientes, y el
   padrón completo de clientes con sus datos de identificación, que no necesita para cobrar.
   Tampoco lleva `pos.sale.discount`. `cashier` entra a `SystemRoleKeys` con un chequeo SQL previo al
   despliegue.
5. **Una caja abierta por cajero y tenant**, impuesta por índice parcial; sólo el dueño cierra.
6. **Acumulados en la caja**, movidos en la misma transacción que la venta y protegidos por su
   `Version`, en vez de sumar ventas al cerrar.
7. **Snapshot de la empresa emisora en la caja** (no lectura en vivo como el PDF de
   cotizaciones): el ticket es el comprobante que se le entregó al cliente y su reimpresión tiene
   que decir lo mismo.
8. **FK real a `companies.companies`** y no a productos ni clientes (ver Persistencia).
9. **`MemberId` y no id de usuario**, con su `PosUserReferenceProbe`, mismo modelo que Quotations.
10. **Fórmula de línea extraída a `BuildingBlocks.Domain`** en vez de copiada: dos copias de la
    fórmula del IVA pueden divergir sin que ninguna prueba lo note, y el criterio de éxito es el
    centavo. Cuesta tocar `QuotationItem.Apply` sin cambiar su comportamiento.
11. **`FinalConsumer` duplicado en `Pos.Domain`** y no movido: son dos constantes, moverlas obliga
    a tocar siete llamadores de Quotations por nada, y el frontend ya las duplica con el mismo
    criterio.
12. **Precio = precio de lista COP con IVA incluido** (regla detal), sin escalas ni USD. Un
    producto sin precio en pesos se muestra como no vendible en vez de esconderse.
13. **Idempotencia por id generado en el cliente**: repetición del mismo cajero **con la misma
    huella** → 200 con la venta existente; otra huella → `pos.sale.id_conflict` (ver 24).
14. **Precio y tasa esperados por línea** para detectar pantalla vieja (`pos.sale.price_changed`
    si cambia cualquiera de los dos), y preview de servidor para que el frontend nunca calcule
    IVA. La pantalla refresca **los dos** con `refreshExpected` y avisa; sólo con el carrito
    desbloqueado, para que un id nunca viaje con dos cuerpos.
15. **Numeración `POS-000001`** con contador propio por tenant, sin año, sin formato configurable.
16. **Anulación sólo con la caja de la venta abierta**, con motivo; anular devuelve los montos al
    arqueo.
17. **Cantidad con hasta 2 decimales** (`numeric(10,2)`), igual que Quotations
    (`QuotationsDbContext.cs:198`); ver 27.
18. **Topes**: 200 líneas, 5 pagos, un solo efectivo; base y billete recibido hasta
    $100.000.000, efectivo contado hasta $1.000.000.000. Son topes contra errores de digitación,
    no reglas de negocio: un cobro en efectivo mayor no es caso de mostrador.
19. **Ticket por `window.print`** a 80 mm, reusando `data-print-region` con una página con nombre
    (`@page pos-ticket`); `printWithTitle` se mueve a `src/lib/`.
20. **Carrito en zustand persistido en `sessionStorage`** con clave por tenant y usuario
    (`qep.pos.cart.{tenantId}.{userId}`, ver 49) y la caja como campo, incluido el intento de
    cobro en curso. Un intento incierto se muestra con Reintentar / Verificar sea cual sea la caja
    abierta; una caja nueva sin intento adopta el carrito.
21. **Centavos visibles sólo cuando existen**, con un formateador propio de POS que decide los
    decimales por valor; aritmética de dinero del cliente en centavos enteros.
22. **`GET /pos/register`** como endpoint BFF de contexto: caja con su `version` y
    `openedBeforeToday`, empresas y empresa por defecto.
23. **Aterrizaje en `/pos`** para quien sólo vende; la página exige `pos.sale.create`,
    `pos.register.operate` y `pos.sale.read` [ORQ], porque sin el segundo `GET /pos/register` da
    403 y sin el tercero no se puede verificar un cobro incierto (ver 47).
24. **[ORQ] Los dos lados contra el reintento con otro carrito.** Cliente: mientras un intento es
    incierto, carrito y cobro quedan bloqueados y sólo se ofrece "Reintentar" (mismo id, mismo
    cuerpo) o "Verificar" (`GET /pos/sales/{id}`); el id se regenera sólo después de un 4xx
    definitivo. Servidor: la repetición compara la huella SHA-256 del request guardada en la
    venta; no coincide → `pos.sale.id_conflict`. Se eligió la huella y no "total + cantidad de
    líneas" porque dos carritos distintos pueden dar el mismo total. Y es 422 y no 409 porque el
    repo no mapea 409 y todo `DomainException` ya es 422.
25. **[ORQ] El MVP escanea `Product.Code`.** Catalog no tiene campo de código de barras/EAN; el
    lector teclea el código de producto. Un campo EAN aparte queda como `DECISIÓN-PENDIENTE`.
26. **[ORQ] Permiso nuevo `pos.sale.discount`** para cualquier descuento de línea > 0 y para una
    venta en total 0. `admin` lo tiene; `cashier` no. Los descuentos de cada venta quedan en la
    auditoría `pos.sale.created` (`changedFields`, el único campo libre del contrato de auditoría,
    para no cambiar un evento compartido).
27. **[ORQ] Escala de cantidad ≤ 2 decimales**, igual que Quotations, para que "coincide al
    centavo con una cotización detal" sea cierto para cualquier cantidad. La venta a granel en
    milésimas (gramos sobre kilo) no entra.
28. **Duplicados en paralelo**: el handler trata igual `pos.sale.id_taken` y el 412 de la
    `Version` de la caja (que es lo que de verdad sale, porque EF actualiza la caja antes de
    insertar la venta): limpia el contexto, relee y responde 200 si la venta es del llamador.
29. **404 dentro del tenant, 403 entre tenants**, como Companies: `pos.sale.not_found` y
    `pos.session.not_found`. El id de otro tenant responde igual que uno inexistente.
30. **Escaneo por índice único** con una lectura nueva en `IProductRepository`, exacta y con
    mayúsculas; sin respaldo sin mayúsculas [ORQ, YAGNI]: el lector teclea el código tal cual y
    la grilla cubre lo tecleado a mano. El escaneo devuelve también inactivos, marcados, para no
    decir "no existe" de un producto que existe.
31. **Cierre con `If-Match`** (versión de `GET /pos/register`), el mismo mecanismo que
    `/orders-export-layout`: el cajero cierra contra el arqueo que vio.
32. **El cajero lee sus propias cajas** (abiertas y cerradas) con `pos.sale.read`, para reimprimir
    un cierre; `pos.register.read` sólo amplía a las de otros.
33. **`voidable` + `voidBlockedReason`** calculados por el servidor sobre el estado de la venta
    (`AlreadyVoided`, `SessionClosed`), en la lista y en el detalle. Sin `NotAllowed` [ORQ,
    YAGNI]: sin `pos.sale.void` la pantalla esconde la acción.
34. **Líneas no vendibles bloquean "Cobrar"**: no se cobra un carrito parcial. Un producto borrado
    vuelve `NotFound` en el preview, no como 422.
35. **[ORQ, YAGNI] Cliente siempre consumidor final.** Sin `/pos/customers`, selector, F4 ni
    dependencia de `customers`. Las columnas de snapshot de cliente quedan en `pos.sales`, con
    `customer_id` null, para no migrar cuando se decida el cliente identificado.
36. **Sin `GET /pos/products?ids=`**: el preview ya refresca precio, tasa y vendibilidad del
    carrito.
37. **Atajos de línea sólo con el foco en el carrito**: ningún atajo puede comerse un carácter
    escaneado.
38. **[ORQ] Panel de éxito no modal.** El foco vuelve a la búsqueda, el ticket se imprime con F8 o
    clic y nunca con Enter, y el primer producto de la venta siguiente quita el panel. Con el
    éxito modal y "Imprimir" enfocado, el escaneo siguiente caía en el botón y su Enter imprimía.
39. **[ORQ] Guardia de ráfagas en los inputs de dinero**: ≥ 4 caracteres a < 30 ms que terminan
    en Enter se descartan. Escanear con el cobro abierto no puede confirmar una venta.
40. **[ORQ] Cola de escaneo, cobro y carrito.** "Cobrar" y F9 esperan a que la cola esté vacía y el
    preview al día; con el cobro abierto el carrito no acepta cambios; ningún `addProduct`
    rechazado pasa sin mensaje.
41. **[ORQ] Pagos armados en el cliente** (`build-payments.ts`): con una línea, el medio la
    reemplaza; el efectivo se omite si las otras cubren el total (salvo total 0). En el servidor,
    `Cash` trae sólo `tendered` y el `Amount` lo calcula él (`pos.sale.cash_amount_not_allowed`).
42. **[ORQ] Total 0 sin permiso: el preview avisa, el POST rechaza.** `zeroTotalNotAllowed` en el
    preview y 403 sólo en la venta, para que el carrito se pueda dibujar.
43. **[ORQ] Huella sobre JSON canónico** con `null` explícito, no sobre una cadena con `|`.
44. **[ORQ] Respuestas perdidas.** 401 deja el intento incierto y bloqueado hasta reautenticar;
    cualquier status no listado es incierto. El cierre que recibe 412 o `not_open` relee la caja
    y, si está `Closed`, muestra su resumen; la anulación que recibe `already_voided` es éxito.
45. **[ORQ, YAGNI] Recortes de la ronda 2**, además de 30, 33 y 35: sin `taxBreakdown` en el
    preview (sólo el ticket lo usa), sin `Version` en `PosSale` (la anulación ya la serializa la
    `Version` de la caja), sin filtro `cashierId` en `/pos/sessions` y sin `summary` en
    `/pos/sales`.
46. **[ORQ] Total 0 exige recibido 0.** Con `Total = 0`, un `Cash` con `Tendered > 0` es
    `pos.sale.tendered_invalid`: una venta en cero no recibe billete ni da cambio, y el arqueo no
    distinguiría ese "cambio" de uno real. La pantalla fija el recibido en $0.
47. **[ORQ] `/pos` exige también `pos.sale.read`, y un 403 de Verificar no resuelve nada.**
    "Verificar" es `GET /pos/sales/{id}`, que pide `pos.sale.read`; sin él, la caja podría quedar
    con un cobro incierto que no se puede verificar. Si igual responde 403 (permiso quitado a mitad
    de turno o capacidad apagada), el cobro sigue incierto y bloqueado: un 403 no dice si la venta
    quedó.
48. **[ORQ] La capacidad la resuelve `ModuleGate`** del spec de entitlements, en la ruta layout
    `routes/_authenticated/pos.tsx` (`<ModuleGate modules={['pos']}>`), como los demás módulos.
    `PosGate` sólo mira permisos y no lee `useTenantModules`: un solo lugar decide los mensajes y
    el fail open de módulos.
49. **[ORQ] El usuario va en la clave del carrito** (`qep.pos.cart.{tenantId}.{userId}`, con el
    `userId` de `useSession()`). Con una clave sólo por tenant, un cajero que entra en la pestaña
    que dejó otro rehidrataba el intento incierto ajeno y su "Reintentar" podía registrar la venta
    otra vez a su nombre. El intento se queda bajo la clave de quien lo hizo.
50. **[ORQ] Desde un intento incierto, un 403 no es definitivo.** `authorization.denied` y los 403
    del nivel de tenancy salen del paso 1 de «Crear venta», antes de buscar la repetición, así que
    no prueban que el primer envío no quedó; tampoco `validation.failed`, 400 ni 404. Sólo
    `pos.sale.discount_not_allowed` y los 422 `pos.*` del paso 2 en adelante liberan el carrito.
    En el primer envío, todo 4xx sigue siendo definitivo. La regla vive en una función pura
    (`classifyCreateSaleOutcome`), y Reintentar y Verificar quedan coherentes: ningún 403 libera.
51. **[ORQ] `price_changed` invalida el preview** de forma explícita, y el efecto que aplica
    `refreshExpected` depende también del bloqueo del carrito: sin eso, el `linesKey` no cambia,
    el refresco bloqueado nunca se reaplica y "Cobrar" queda deshabilitado.
52. **[ORQ] `adoptSession` cierra el cobro abierto sin intento** cuando la caja abierta cambia: el
    total congelado y el cuerpo eran de la caja vieja, y el cobro siguiente se arma con la nueva.
53. **[ORQ] Descuento informativo fuera de la suma.** `Subtotal` ya es la base sin IVA con el
    descuento restado (`Total = Subtotal + TaxAmount`), así que una fila "Descuentos −2.380" en la columna que se suma
    se lee como un segundo descuento (quien suma da 35.510, no 37.890). En la caja, la columna es
    Subtotal, IVA y TOTAL, y debajo del TOTAL va una nota apagada, sin signo y fuera de la columna:
    "Incluye $ 2.380 de descuento" (no se dibuja si es 0). El ticket imprime
    `Descuentos incluidos 2.380` después del TOTAL, no entre el subtotal y el IVA. Se descartó una
    línea de "subtotal antes de descuento": la API no la trae, y Total + Descuento mezcla un bruto
    con IVA con el `Subtotal` neto que muestran el ticket y el listado.
54. **[ORQ] Encabezado de la caja sin cadena con puntos medios.** En lugar de "Punto de venta ·
    Caja de … · empresa", el encabezado dibuja "Caja de {cajero}" con "desde las HH:MM" y la
    empresa en una segunda línea; "Punto de venta" queda como `<h1>` sólo para lectores de
    pantalla. Misma información, sin unir rótulos con "·".

## DECISIÓN-PENDIENTE

1. Descuento global sobre la venta (el `qcode-pos` viejo lo tenía).
2. Cliente identificado en POS: elegir un cliente existente (con qué permiso y si depende de la
   capacidad `customers`) y crearlo rápido desde la caja (hoy crear uno exige los campos de
   `CustomerWriteRules`). La venta ya guarda las columnas del snapshot.
3. Devoluciones parciales y notas crédito; hoy sólo existe anular entera con la caja abierta.
4. Formato de numeración configurable (tipo de documento `pos` en `DocumentNumberFormat`).
5. Ticket en PDF desde el backend (Typst 80 mm con `qcode-pdf`; alto automático sin probar, y
   `qcode-pdf` no soporta logos WebP).
6. Redondeo del cobro en efectivo al múltiplo de 50.
7. Cierre forzado de una caja ajena por un supervisor, y qué permiso lo concede.
8. Impresión automática del ticket al confirmar.
9. Ventas POS en Reporting (reporte propio, o sumarlas al de pedidos).
10. Arqueo por denominación de billetes y monedas.
11. Movimientos de caja: entradas, retiros parciales, gastos menores.
12. Anulación con tope de tiempo o por el mismo cajero.
13. Logo del tenant en el ticket.
14. Varias cajas abiertas por persona (dos terminales a la vez).
15. Campo de código de barras/EAN en Catalog (hoy se escanea `Product.Code`), y si un producto
    puede tener varios.
16. Salida de un cobro incierto cuando la red no vuelve: hoy el carrito queda bloqueado hasta una
    respuesta definitiva para ese id.
17. Venta a granel con cantidades de 3 decimales (exigiría cambiar también la escala de
    Quotations para no romper la paridad al centavo).
18. "Hoy" del tenant expuesto por la API. Los listados de ventas y cajas cortan `from`/`to` con
    el calendario del tenant (`TenantCalendar`), pero la pantalla arma el "hoy" por defecto con el
    día del navegador: ningún endpoint lo expone. Con navegador y tenant en el mismo huso coinciden;
    un supervisor en otro huso, o cerca de la medianoche, ve el día equivocado sin aviso. Opciones:
    un `todayLocal` en `GET /pos/register` o en la respuesta de los listados. Mientras tanto, la
    pantalla fija el "hoy" al abrir el listado (y lo relee al abrir el panel de filtros o al
    limpiarlos), así el chip "Hoy" no cambia bajo las mismas filas al pasar la medianoche.

## Fuera de alcance

- Facturación electrónica DIAN y su numeración autorizada. Por eso el ticket dice que no es
  factura electrónica.
- Inventario, existencias, kardex.
- Escalas de precio, precios en USD, retención en la fuente, excedente de IVA.
- Domicilios, propinas, comandas de cocina, mesas.
- Modo offline (el cobro necesita red; el reintento idempotente cubre los cortes cortos).
- Hardware: cajón monedero, impresoras ESC/POS, datáfonos integrados, básculas.
- Gating de workers por capacidad (no hay workers en POS).

## Historial de revisión

- Ronda 1 (2026-10-07): 21 hallazgos aplicados.
- Ronda 2 (2026-10-07): 17 hallazgos + 7 recortes YAGNI aplicados.
- Ronda 3 (2026-10-07): 3 MAJOR + 3 menores aplicados.
- Ronda 4 (2026-10-07): 2 MAJOR + 2 menores aplicados. Spec listo para revisión del owner.

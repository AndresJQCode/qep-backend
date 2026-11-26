# Política de campos obligatorios del cliente — diseño backend

Fecha: 2026-11-09. Trabajo que cruza repos: este spec cubre `qep-backend`; la mitad del frontend es
`qep-frontend/docs/superpowers/specs/2026-11-09-customer-field-policy-frontend-design.md`. Salen juntos, backend primero (§9).

Estado: diseño, no construido. No existe código, migración ni prueba de nada de lo que sigue. Las rutas y líneas citadas
son de `feature/customer-field-policy` en `364e543c` (mismo contenido que `develop`).

## 1. Intención

Hoy un cliente no se puede guardar sin tipo y número de documento ni sin ciudad (DIVIPOLA en Colombia, nombre libre afuera).
Lo exigen tres capas a la vez: el validador `CustomerWriteRules` (`Modules.Customers.Application/CustomerWriteRules.cs`:
`IdentificationType` y `IdentificationNumber` `NotEmpty`, `CityId` obligatorio con país `CO` o vacío, `CityName` con el resto),
el agregado (`Customer.EnsureValidLocation`, `Modules.Customers.Domain/Customer.cs:1215`, que lanza
`customers.customer.city_required` / `customers.customer.city_name_required`) y la base (`CK_customers_complete_fields`,
`CustomersDbContext.cs:40`: una ficha `Complete` tiene `cuc`, `identification_type`, `identification_number` y `country` no nulos).

El owner quiere que documento y ciudad sean **opcionales y configurables por tenant** (decisión del owner, 2026-11-09). No por
módulo: por campo, en dos momentos, «al guardar el cliente» y «al facturar». El valor por defecto es todo obligatorio en los dos
momentos, que es exactamente el comportamiento de hoy. Un tenant que no toque la configuración no nota el cambio.

Al mismo tiempo cae la condición de país del tipo de persona (decisión del owner): `PersonTypeRequirementPolicy.Effective`
(`Modules.Tenancy.Domain/PersonTypeRequirement.cs`) devuelve `Never` fuera de Colombia, y `CustomerPersonTypeRequirement` y
`BilledPersonTypeRequired` salen temprano con `IsColombian`. La política nueva no tiene país, y el tipo de persona tampoco.

## 2. Objetivos

- Una política por tenant con cuatro banderas: `document.onCustomerSave`, `document.onBilling`, `city.onCustomerSave`,
  `city.onBilling`. Default `true` las cuatro (columnas con default; sin migración de datos, §8).
- Un cliente completo sin documento es válido, recibe CUC y se puede cotizar. Un cliente completo sin ciudad, igual.
- Los momentos de facturación aplican `onBilling`: aprobar pedido (Quotations) y venta del POS con cliente (Pos). Tomar pedido
  de la tienda (Store) siempre trae el dato, así que la política no frena nada ahí (§6.5).
- `city.onBilling` significa **al despachar**: se exige sólo si el pedido no es recogida en tienda, tiene datos de envío
  (`Order.ShippingDetails != ShippingDetails.None`) y no tiene fila `Shipping` propia, es decir, sale a la dirección del cliente
  (§6.3). Un pedido sin flete (manual, Shopify, cotización sin envío) nunca se frena por ciudad.
- Tipo de persona disponible para todo tenant. La regla `Juridica ⇒ NIT` sigue siendo sólo colombiana (§6.6).
- La política viaja en `GET /auth/me` como `personTypeRequirement`, para que `cashier` la lea sin `tenancy.settings.read`.

## 3. No objetivos

- Nombre, teléfono y país siguen obligatorios. Políticas por módulo. Préstamos y contratos de servicio como momentos de
  facturación. Cambios en la tienda pública (el checkout sigue pidiendo documento al visitante). Compatibilidad con datos
  guardados de tenants existentes más allá del default de columna (decisión del owner: hay un solo tenant real y es colombiano).
- La identificación propia de una parte de facturación («otros datos») sigue siendo obligatoria:
  `Quotation.EnsureBillingIdentified` → `quotation.billing.identification_required` no cambia.
- Un catálogo de «documento societario por país». Fuera de Colombia no se filtra el documento por tipo de persona
  (decisión del owner, 2026-11-09).

## 4. Anti objetivos

- Rebajar el documento a «opcional siempre» sin configuración. Deducir la política del país. Guardar media identificación
  (tipo sin número o número sin tipo). Mover la traducción de errores de base a Application (`ArchitectureTests` lo prohíbe).

## 5. Restricciones

- Capas verificadas por `tests/ArchitectureTests/ArchitectureTests/<Modulo>LayerTests.cs`: Application de cada módulo sólo
  referencia `Modules.Tenancy.*` entre los módulos de negocio. Pos, Quotations y Customers leen la política por
  `ITenantDirectory.GetRegionalSettingsAsync` (`Modules.Tenancy.Application/ITenantDirectory.cs`), como hoy el tipo de persona.
  No hay proyectos `*.Contracts`.
- `IX_customers_tenant_identification` (único sobre `(tenant_id, identification_type, identification_number)`, filtro
  `merged_into_id IS NULL`, `CustomersDbContext.cs` ~L240) no cambia: en Postgres dos `NULL` no colisionan, así que N clientes sin
  documento conviven. `CustomersUnitOfWork` sigue traduciendo esa violación **por nombre de índice** a
  `customers.customer.identification_taken`.
- Errores de dominio → 422 con `code` (`ApiExceptionHandler.MapException`); `IHasFieldErrors` llena `errors` en PascalCase.
- TDD: RED antes que GREEN con evidencia literal (§10). Integración con Testcontainers, un proyecto a la vez.

## 6. Diseño

### 6.1 Tenancy — la política

No existe una entidad `TenantSettings`: los ajustes son propiedades de `Tenant` (`Modules.Tenancy.Domain/Tenant.cs`), mutadas
por `UpdateSettings(...)`. Se agrega:

```csharp
// Modules.Tenancy.Domain/CustomerFieldPolicy.cs (nuevo)
public sealed record FieldRequirement(bool OnCustomerSave, bool OnBilling)
{
    public static readonly FieldRequirement Always = new(true, true);
    /// <summary>Exigido al guardar implica exigido al facturar: una ficha completa ya lo trae. La combinación
    /// (true, false) no significa nada y se rechaza.</summary>
    public bool IsConsistent => !OnCustomerSave || OnBilling;
}

public sealed record CustomerFieldPolicy(FieldRequirement Document, FieldRequirement City)
{
    public static readonly CustomerFieldPolicy Default = new(FieldRequirement.Always, FieldRequirement.Always);
}
```

- `Tenant.CustomerFieldPolicy` (owned en EF, cuatro columnas `bool NOT NULL DEFAULT true`:
  `customer_document_required_on_save`, `customer_document_required_on_billing`, `customer_city_required_on_save`,
  `customer_city_required_on_billing`). Migración `AddTenantCustomerFieldPolicy` en
  `Modules.Tenancy.Infrastructure/Persistence/Migrations/` (la última hoy es `20261103231710_AddStoreModuleKey`).
- `UpdateSettings` gana `CustomerFieldPolicy? customerFieldPolicy` (null conserva lo guardado, como `PersonTypeRequirement`).
  Inconsistente → `TenantDomainException("tenancy.settings.customer_field_policy.invalid", ...)` (código nuevo, mismo patrón
  `tenancy.settings.<campo>.invalid`).
- `TenantRegionalSettings` (`ITenantDirectory.cs:42`) gana `CustomerFieldPolicy CustomerFieldPolicy = CustomerFieldPolicy.Default`.
  `TenantDirectory` lo rellena.
- **Cae la condición de país del tipo de persona:** `PersonTypeRequirementPolicy.Effective` devuelve `stored` siempre;
  `IsColombian` se quita de esa clase cuando nadie más lo use. `EffectivePersonTypeRequirement` se conserva como propiedad
  (el frontend y `SessionEstablisher` la leen) y pasa a ser igual a lo guardado.

### 6.2 Customers — guardar y completar

**Dominio.**
- `Customer.Create(...)`, `Complete(...)` y `Update(...)` reciben `CustomerIdentification? identification` (hoy no anulable).
  Con `null`, `IdentificationType` e `IdentificationNumber` quedan en `null`. `EnsureDocumentAllowedForCountry` y
  `EnsurePersonTypeMatches` sólo corren con identificación.
- `EnsureValidLocation` deja de lanzar `city_required` / `city_name_required`: sólo normaliza (limpia `CityName` en `CO` y
  `CityId` afuera) y devuelve la terna. La obligatoriedad se decide en Application (abajo), porque depende de la política del
  tenant y el agregado no la conoce. Los dos códigos **se conservan**: los lanza la regla nueva. Nota: hoy `CustomerWriteRules`
  también exige `CityId`/`CityName` por FluentValidation; esas dos reglas salen del validador.
- `CustomerIdentification.Normalized()` no cambia (`identification_number_required` sigue para número en blanco **con** tipo).
- `CreateFromRegister` (POS, `Customer.cs:388`) acepta `CustomerIdentification?` (§6.4).
- `CustomerMerge` (dominio): `FillIdentification` ya sólo copia al superviviente sin documento; sin cambio. `DocumentsDiffer`
  (`Modules.Customers.Application/CustomerMerge.cs`) ya ignora números vacíos; sin cambio.

**Persistencia.**
- `CK_customers_complete_fields` pasa a `completeness = 'Incomplete' OR (cuc IS NOT NULL AND country IS NOT NULL)`.
- Check nuevo `CK_customers_identification_pair`: `(identification_type IS NULL) = (identification_number IS NULL)`. Es la red
  de la regla «los dos o ninguno»; la primera línea es el validador.
- Migración `RelaxCustomerCompleteFields` en Customers (la última hoy es `20261103233115_AddForeignCustomerAddresses`). Los
  índices `IX_customers_tenant_identification` e `IX_customers_tenant_identification_normalized`
  (`WHERE identification_number IS NOT NULL`) no cambian. Se actualiza el comentario de `identification_type` en
  `CustomersDbContext.cs:138` («Obligatorio en una ficha completa» deja de ser cierto).

**Application.**
- `CustomerWriteRules`: `IdentificationType` e `IdentificationNumber` dejan de ser `NotEmpty`; se conservan los topes, el
  parse del tipo y `DocumentTypesByCountry.IsAllowed(type, Country)` cuando hay tipo. `CityId`/`CityName` dejan de ser
  obligatorios en el validador. El validador **no** conoce la regla «los dos o ninguno»: una violación de FluentValidation sale
  como `validation.failed` y el frontend mapea por código, así que esa regla va en la clase de abajo.
- Clase nueva `CustomerFieldPolicyRequirement.EnsureSatisfiedAsync(ITenantDirectory, tenantId, ICustomerWriteCommand, ct)`,
  hermana de `CustomerPersonTypeRequirement`, llamada en `CreateCustomerHandler` y `UpdateCustomerHandler` justo después de
  `EnsureSatisfiedAsync` del tipo de persona (así completar una ficha también la aplica, punto 7 del owner). Lanza siempre
  `CustomersDomainException` (422 con `code` propio y `errors`):
  - **Forma, sin política:** tipo sin número o número sin tipo → `customers.customer.identification_incomplete` (nuevo en
    `CustomersErrorCodes`, `IdentificationIncomplete`), con `errors` apuntando sólo al que falta (`{ IdentificationNumber: [code] }`
    o `{ IdentificationType: [code] }`).
  - `Document.OnCustomerSave` y sin identificación → `customers.customer.identification_required` (nuevo,
    `IdentificationRequired`), `errors = { IdentificationType: [code], IdentificationNumber: [code] }`.
  - `City.OnCustomerSave`, país `CO` y sin `CityId` → `customers.customer.city_required`; otro país y sin `CityName` →
    `customers.customer.city_name_required`. Mismos códigos de hoy, distinto lugar.
  - **Cuándo corre la obligatoriedad (decisión del owner, 2026-11-09):** siempre al crear (`CreateCustomerHandler`) y al
    completar una ficha `Incomplete` (`UpdateCustomerHandler` → `Customer.Complete`). En un `PUT` sobre una ficha `Complete`
    corre **sólo si el dato cambia**: para el documento, cuando el par (tipo, número) que llega difiere del guardado (incluido
    pasar a vacío); para la ciudad, cuando `CityId`/`CityName` o el país difieren de lo guardado. Un `PUT` que deja intacto un
    documento o una ciudad que ya faltaban pasa, aunque la política los exija: reactivar la política no bloquea la edición de los
    clientes guardados antes. La firma gana el estado actual: `EnsureSatisfiedAsync(directory, tenantId, command,
    Customer? current, ct)`; con `current` nulo o `Incomplete` se evalúa todo. La regla de forma «los dos o ninguno» corre siempre.
  - Sin fila regional (`GetRegionalSettingsAsync` → `null`) rige `CustomerFieldPolicy.Default`.
- `CustomerPersonTypeRequirement`: se quita el `IsColombian(customerCountry)` temprano; la regla aplica a todo país.
- `CustomerProvisioning` (`CreateCompleteAsync`, `CompleteFromStoreAsync`, `CompleteExternalSaleAsync`): no aplican la
  política (hoy tampoco aplican la de tipo de persona) porque el dato que traen viene de otro sistema y siempre completo.
- Importación Excel (`ImportCustomers.cs`, `ExcelCustomerRowRules`): `identification_type_required`,
  `identification_number_required`, `department_required` y `city_required` pasan a condicionales por la política de guardado
  (la fila es un «guardar cliente»); «los dos o ninguno» → `customers.import.row.identification_incomplete` (nuevo). El importador
  lee la política una vez por archivo. Exportación (`ClosedXmlCustomerExportBuilder`): ya escribe `""` para nulos; sin cambio.
- Consultas: `FindByNormalizedIdentificationAsync`, las anomalías `SharedIdentificationNumber` y `CustomerDuplicateQueries`
  ya filtran `identification_number IS NOT NULL`; sin cambio funcional. `GetCustomerCandidates` del Store casa por documento
  sólo cuando hay (§6.5).
- `CustomerDto` no cambia de forma: `identificationType`/`identificationNumber`/`cityId`/`cityName` ya son anulables desde
  spec 2026-10-10 §6.2. Lo que cambia es que ahora pueden ser `null` con `isComplete = true`.

### 6.3 Quotations — aprobar pedido

- Clase nueva `BilledCustomerFieldsRequired` junto a `BilledPersonTypeRequired` (`Modules.Quotations.Application/`), con
  `EnsureSatisfiedAsync(ITenantDirectory, IQuotationCustomerLookup, tenantId, Order, ct)`, llamada en `ApproveOrderHandler`
  (`ApproveOrder.cs:53`) en el mismo punto, sólo con `Status == Pending`. Fuente por `OrderBilledParty.SourceFor`:
  - `FinalConsumer` → nada. `BillingParty` → nada (la fila propia ya está identificada por `EnsureBillingIdentified`).
  - `Customer` → lee la ficha viva por `customerLookup.FindAsync` (`QuotationCustomerRef`,
    `IQuotationCustomerLookup.cs:69`; ya trae `IdentificationNumber` e `IdentificationType` anulables, y el adaptador
    `src/Bootstrapper/QuotationCustomerLookup.cs:125` ya rellena el tipo, así que no hay que tocar el ref). Con
    `Document.OnBilling` y `IdentificationNumber` o `IdentificationType` nulos → `order.order.billed_document_required` (nuevo,
    `DomainException` con extensión `missingOn = "Customer"`, mismo molde que `BilledPersonTypeRequiredException`).
- **Ciudad al despachar.** Con `City.OnBilling` se exige la ciudad del cliente sólo si se cumplen **las tres**: el pedido no es
  `IsStorePickup`, tiene datos de envío (`Order.ShippingDetails != ShippingDetails.None`, `ShippingDetails.cs:22`) y no tiene fila
  `Shipping` propia (va a la dirección del cliente: es el caso en que `OrdersExportProcessor.CoordinadoraCityFor` cae a
  `customer.CityId`). Si además el cliente no tiene `CityId` (CO) ni `CityName` (afuera) → `order.order.shipping_city_required`
  (nuevo, `missingOn = "Customer"`). Un pedido sin flete (manual, Shopify, cotización sin envío) nunca se frena por ciudad. Con
  fila `Shipping` propia manda la ciudad de esa fila, que hoy es anulable (`OrderParty.CityId`/`CityName`) y queda fuera de
  alcance (§11). Con recogida en tienda no se exige nada.
- `BilledPersonTypeRequired`: se quitan las dos comprobaciones de país (`IsColombian` sobre el tenant y sobre el cliente).
- `OrdersExportProcessor`: ya es tolerante (`?? string.Empty` en identificación, ciudad y departamento del cliente;
  `CoordinadoraCityFor` devuelve `""` sin ciudad). Un pedido sin ciudad que llegue al export produce una fila con la celda de
  ciudad vacía, no una excepción; el 422 de arriba es lo que lo evita cuando el tenant así lo quiere.
- PDF (`QuotationPdfDocumentMapper`, `quotation.typ`): el bloque de cliente nunca imprimió su documento; la parte de facturación
  imprime `NIT <taxId>` sólo si `taxId != ""`. Sin cambio.
- `quotation.quotation.client_incomplete` / `order.order.client_incomplete` (`QuotationCustomerEligibility`,
  `OrderCustomerEligibility`) siguen mirando `IsComplete`, no el documento.

### 6.4 Pos — venta con cliente y alta rápida

- `CreatePosSale.cs:168`: `pos.sale.customer_identification_missing` pasa a lanzarse sólo con `Document.OnBilling`. El código se
  conserva. La política se lee por `ITenantDirectory.GetRegionalSettingsAsync` (Pos.Application ya puede referenciar Tenancy).
- `PosSaleCustomer` (`PosSaleInputs.cs:32`) pasa a `(Guid Id, string Name, string? IdentificationType, string? IdentificationNumber)`.
  `PosSale.CustomerIdentificationNumber` (`PosSale.cs:47`, hoy `string` con default `PosFinalConsumer.IdentificationNumber`) pasa
  a `string?`; con cliente sin documento queda `null` (el consumidor final sigue con `222222222222`). Migración
  `AllowNullPosSaleCustomerIdentification` (columna `customer_identification_number` anulable). `PosSaleRepository.cs:65`
  (`ILike` sobre esa columna) tolera `null` sin cambio. `PosCustomerResponse` ya expone tipo anulable; el número pasa a anulable.
- Alta rápida (`CreatePosCustomerCommand`/`CreatePosCustomerValidator`, `PosCustomers.cs`): tipo y número dejan de ser
  `NotEmpty` y siguen la política de guardado (`Document.OnCustomerSave`) con el mismo par de códigos de Customers
  (`identification_required`, `identification_incomplete`); el adaptador `src/Bootstrapper/PosCustomerLookup.cs` pasa
  `CustomerIdentification?` a `CreateFromRegister`. Decisión del orquestador, revisable: el alta del POS es un «guardar
  cliente», así que obedece `onCustomerSave` y no `onBilling`. La ciudad no entra en el POS (no hay envío).
- `PosCustomerRef` (resultado de búsqueda) gana tipo y número anulables.

### 6.5 Store — tomar pedido

`StoreRequestBilling` (`Modules.Store.Domain/StoreRequestBilling.cs`) exige al visitante nombre, tipo, número, teléfono,
correo, dirección y lugar; el lugar siempre trae ciudad. Por eso `TakeOrderRequest` siempre crea o completa el cliente con
documento y ciudad: la política **no tiene nada que exigir** ahí. El único cambio es que `CompleteFromStoreAsync` no aplica la
política (§6.2) y que `FindCandidatesAsync(tenantId, phoneE164, identificationType?, identificationNumber?)` ya acepta nulos.
Discrepancia con el pedido del owner (punto 3): la tienda no es un momento donde la política pueda frenar; se anota en §11.

### 6.6 Tipo de persona para todo país (decisión del owner, 2026-11-09)

- Quitar la condición de país sólo hace que el tipo de persona sea **elegible y exigible** fuera de Colombia
  (`CustomerPersonTypeRequirement`, `BilledPersonTypeRequired`, `PersonTypeRequirementPolicy.Effective`, §6.1–§6.3).
- La regla `Juridica ⇒ NIT` sigue siendo **sólo colombiana**: `Customer.EnsurePersonTypeMatches` y `BillingPartyFiscalRules`
  (Quotations.Domain, con su propia copia de `"CO"`) no cambian. Fuera de Colombia cualquier documento permitido para el país
  (`DocumentTypesByCountry.AllowedFor`) vale para cualquier tipo de persona. No se crea ningún catálogo de documentos societarios.
- `BillingPersonTypeRequirement` (`quotation.billing.person_type_required`) no mira país hoy; sin cambio.

### 6.7 Contrato HTTP

**`GET/PUT /api/v1/tenants/{tenantId}/settings`** (`TenantSettingsEndpoints.cs`; `tenancy.settings.read` / `tenancy.settings.update`,
`If-Match` como hoy). `TenantSettingsResponse` y `UpdateTenantSettingsRequest` ganan:

```json
"customerFieldPolicy": {
  "document": { "onCustomerSave": true, "onBilling": true },
  "city":     { "onCustomerSave": true, "onBilling": true }
}
```

En el `PUT` el objeto es opcional (`null`/ausente conserva lo guardado, como `personTypeRequirement`); si viene, vienen las
cuatro banderas. `onCustomerSave: true` con `onBilling: false` → 422 `tenancy.settings.customer_field_policy.invalid`.

**`GET /api/v1/auth/me`** (`AuthSessionEndpoints.cs`, `ActiveTenantResponse`): campo nuevo `customerFieldPolicy` con la misma
forma, compuesto en `SessionEstablisher.ToActiveTenantResponsesAsync` desde `regional?.CustomerFieldPolicy`
(`SessionEstablisher.cs:125` es donde hoy sale `personTypeRequirement`). `null` cuando el tenant no tiene fila regional.
`personTypeRequirement` pasa a ser el valor guardado para todo país.

**Customers** `POST/PUT …/customers` y `…/customers/{id}`: `identificationType` e `identificationNumber` aceptan `null`/ausente;
`cityId`/`cityName` también. Respuestas sin cambio de forma.

**Pos** `POST …/pos/customers`: `identificationType`/`identificationNumber` anulables. `POST …/pos/sales`: sin cambio de
entrada; `customer.identificationNumber` en la respuesta puede ser `null`.

## 7. Códigos de error (resumen)

| Código | Estado | Dónde | Nuevo |
| --- | --- | --- | --- |
| `customers.customer.identification_required` | 422 | `CustomerFieldPolicyRequirement`, alta POS | sí |
| `customers.customer.identification_incomplete` | 422 | `CustomerFieldPolicyRequirement`, alta POS | sí |
| `customers.customer.city_required` / `city_name_required` | 422 | `CustomerFieldPolicyRequirement` (antes dominio) | no |
| `customers.import.row.identification_incomplete` | fila | `ExcelCustomerRowRules` | sí |
| `order.order.billed_document_required` (`missingOn`) | 422 | `BilledCustomerFieldsRequired` | sí |
| `order.order.shipping_city_required` (`missingOn`) | 422 | `BilledCustomerFieldsRequired` | sí |
| `pos.sale.customer_identification_missing` | 422 | `CreatePosSale`, ahora condicional | no |
| `tenancy.settings.customer_field_policy.invalid` | 422 | `UpdateTenantSettings` | sí |
| `order.order.billed_person_type_required` | 422 | sin condición de país | no |

## 8. Datos existentes

Las cuatro columnas nacen con `DEFAULT true`: todo tenant queda como hoy. No hay backfill. Los clientes existentes cumplen
`CK_customers_identification_pair` porque `CK_customers_complete_fields` les exigía los dos campos a los completos y los
incompletos nacen con ambos en `null` (`CreateIncomplete*`). Postgres valida las filas existentes al crear el check, así que la
migración falla ruidosamente si alguna fila lo contradice.

## 9. Despliegue

Backend primero. El frontend viejo manda siempre documento y ciudad y no manda `customerFieldPolicy`: todo compatible. El
frontend nuevo contra un backend viejo no ve `customerFieldPolicy` en `/auth/me` y asume todo obligatorio (spec frontend §6.1).
Orden dentro del backend: migraciones Tenancy → Customers → Pos; son aditivas o relajan restricciones, sin ventana.

## 10. Pruebas (TDD: RED antes que GREEN, evidencia literal de las dos)

### 10.1 Unitarias
- `FieldRequirement.IsConsistent`: `(true,false)` falso; las otras tres verdaderas. `Tenant.UpdateSettings` con política
  inconsistente lanza `tenancy.settings.customer_field_policy.invalid`; `null` conserva la guardada.
- `PersonTypeRequirementPolicy.Effective("ES", OnCustomerSave) == OnCustomerSave` (hoy devuelve `Never`: RED).
- `Customer.Create` con `identification: null` deja ambos campos nulos y `Completeness = Complete`; `Update`/`Complete` idem.
  `EnsureValidLocation` sin ciudad ya no lanza (RED contra el código de hoy). `EnsurePersonTypeMatches` con `ES`+`Juridica`+`NIE`
  sigue pasando (regresión: la regla NIT es sólo CO).
- `CustomerWriteRules`: ambos campos vacíos → sin error; `CityId` ausente en `CO` → sin error del validador.
- `CustomerFieldPolicyRequirement`: tipo sin número → `identification_incomplete` con `errors` sólo en `IdentificationNumber`;
  número sin tipo → sólo en `IdentificationType`; ambos vacíos con `Document.OnCustomerSave` → `identification_required` con
  `errors` en los dos; matriz 4 banderas × {con/sin dato} × {CO/ES}; sin fila regional rige `Default`. Momento de la regla:
  crear sin documento con `OnCustomerSave` → falla; completar `Incomplete` sin documento → falla; `PUT` sobre `Complete` sin
  documento que no toca el documento → pasa; `PUT` que borra un documento existente → falla; `PUT` que cambia número sin tipo →
  `identification_incomplete`. Igual para ciudad (`CityId` en CO, `CityName` afuera; cambiar de país cuenta como cambio).
- `CustomerPersonTypeRequirement` con país `ES` y `OnCustomerSave` sin tipo → `person_type_required` (hoy pasa: RED).
- `BilledCustomerFieldsRequired`: fuente `Customer` sin documento y `Document.OnBilling=false` → pasa; `true` → código con
  `missingOn=Customer`; ciudad con `City.OnBilling`: `IsStorePickup` sin ciudad → pasa; `ShippingDetails.None` sin ciudad → pasa;
  con fila `Shipping` propia → no mira al cliente; con flete, sin fila y sin ciudad → `shipping_city_required`; con flete, sin
  fila y con ciudad → pasa. `BilledPersonTypeRequired` con tenant `ES` → ya exige (RED).
- `CreatePosSale`: cliente sin documento con `Document.OnBilling=false` → venta creada con `CustomerIdentificationNumber=null`;
  con `true` → `customer_identification_missing`. `CreatePosCustomerValidator`: ambos vacíos pasan con `OnCustomerSave=false`.
- `ExcelCustomerRowRules`: fila sin tipo ni número con política relajada → sin error; sólo tipo → `identification_incomplete`.
- `SessionEstablisher`: `customerFieldPolicy` presente y con la forma de §6.7; `null` sin fila regional.

### 10.2 Integración (Testcontainers; un proyecto a la vez, `-- xUnit.MaxParallelThreads=4`)
- Tenancy: `PUT /settings` con la política, relectura, `ETag`; 422 por inconsistencia; `GET /auth/me` la publica.
- Customers: `POST` sin documento con `Document.OnCustomerSave=false` → 201 con CUC e `isComplete=true`; dos clientes sin
  documento en el mismo tenant conviven (`IX_customers_tenant_identification` con `NULL`); tipo sin número → 422
  `identification_incomplete`; `CK_customers_identification_pair` rechaza un `UPDATE` directo a media identificación;
  `PUT` que completa una ficha WhatsApp sin ciudad con `City.OnCustomerSave=false` → 200; con la política relajada se crea un
  cliente sin documento, se vuelve a `true` y un `PUT` que sólo cambia el nombre → 200, mientras que uno que borra la ciudad → 422.
- Quotations: aprobar pedido de cliente sin documento con `Document.OnBilling=false` → 200; con `true` → 422 y `missingOn`.
- Pos: venta con cliente sin documento según política; `POST /pos/customers` sin documento.
- Store: tomar pedido con cliente incompleto sin documento lo completa con el del visitante (sin regresión).

### 10.3 Arquitectura
`CustomersLayerTests`, `PosLayerTests`, `QuotationsLayerTests`, `TenancyLayerTests` siguen verdes: nada nuevo referencia
Infrastructure ni EF desde Application.

## 11. Discrepancias / decisiones pendientes

**Lo que el brief decía y el código no:**
1. No existe `TenantSettings` ni `TenantSettings.EffectivePersonTypeRequirement`: los ajustes viven en `Tenant`, y la
   propiedad efectiva en `TenantRegionalSettings` (`ITenantDirectory.cs`).
2. `city_required` lo lanza hoy tanto el dominio (`EnsureValidLocation`) como FluentValidation (`CustomerWriteRules`); el
   brief sólo nombraba al dominio.
3. `PosSale` no guarda «la identificación» tal cual: `CustomerIdentificationType` ya es anulable y `CustomerIdentificationNumber`
   es `string` no anulable con el default del consumidor final. Por eso hace falta la migración de Pos (§6.4).
4. Tomar pedido de la tienda no es un momento donde la política pueda exigir nada: el visitante siempre da documento y ciudad.
5. No hay snapshot de identificación del **cliente** en cotización ni pedido: sólo `ClientId` y lectura viva por
   `IQuotationCustomerLookup`. Las filas de parte (`OrderParty`) sí tienen identificación propia, toda anulable.
6. La importación de Excel también exige `Departamento` y `Ciudad`, no sólo documento.
7. Los specs recientes (2026-11-07, 2026-11-08) están en inglés; éste va en español porque así se pidió.

**Decisiones del orquestador, revisables:**
- Forma `customerFieldPolicy` anidada por campo y momento (§6.7) y la regla «guardar implica facturar».
- La ciudad «al facturar» se exige sólo con flete (`ShippingDetails != None`), sin fila `Shipping` propia y sin recogida en
  tienda; la fila `Shipping` con ciudad nula queda fuera de alcance.
- El alta rápida del POS obedece `onCustomerSave`.
- `CreateCompleteAsync`/`CompleteFromStoreAsync`/`CompleteExternalSaleAsync` no aplican la política.
- Los códigos `identification_required`/`identification_incomplete` se emiten como `CustomersDomainException` y no como
  `validation.failed`, para mapear por código en el frontend.

**Pendientes del owner:**
- Si `onBilling` para ciudad debe mirar también la fila `Shipping` propia sin ciudad.
- Si el storefront debe dejar de exigir el documento al visitante cuando la política lo relaja (fuera de alcance aquí).

**Resuelto por el owner (2026-11-09):** reactivar `document.onCustomerSave` o `city.onCustomerSave` no bloquea la edición de
clientes guardados antes sin ese dato; la regla corre al crear, al completar y cuando el dato cambia (§6.2).

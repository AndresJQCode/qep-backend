# Número de documento solo dígitos — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> **Regla del workspace (manda sobre el skill):** las tareas se agrupan en **3 bloques** con un implementador por
> bloque y una revisión por bloque; cada tarea conserva su commit. El RED de compilación no levanta contenedores.

**Goal:** Rechazar con 422 un número de documento con letras o símbolos cuando el tipo es colombiano numérico
(`NIT`, `CC`, `CE`; en Hr `CC`, `CE`, `PEP`, `PPT`), en los 17 caminos de escritura.

**Architecture:** Una regla pura en `BuildingBlocks.Domain.Identification` (junto a `DocumentTypesByCountry`). Cada
módulo la usa en dos capas: su validador de FluentValidation (422 `validation.failed` con el campo) y su dominio
(422 con código propio) en crear/editar, nunca en la materialización. Hr y las importaciones por Excel la usan en su
parser de filas.

**Tech Stack:** .NET 10, FluentValidation, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-27-numero-documento-solo-digitos-design.md`

## Global Constraints

- Caracteres admitidos en un tipo con regla: `0-9`, `.`, `-`, espacio; al menos un dígito. Hr: sin espacio.
- Tipos con regla: `NIT`, `CC`, `CE` (catálogo común); `HrDocumentType.CC`, `CE`, `PEP`, `PPT` (Hr).
- Sin regla: `PASAPORTE`, `NIF`, `NIE`, `TAX_ID`; Hr `PA`, `CD`, `SC`.
- El número se guarda como se escribió (recortado). `CustomerIdentificationNumbers.Normalize` y su SQL no se tocan.
- Bloquea siempre, también un número viejo que vuelve igual (decisión del owner, 2026-11-27).
- La regla corre sólo con tipo válido y número no vacío; va después de la regla de tipo/país de cada módulo.
- Mensaje (validador y fila de Excel), tuteo: `El número de documento solo puede tener números para NIT, CC y CE.`
  — Hr: `El número de documento solo puede tener números para CC, CE, PEP y PPT.`
- Código de dominio: sufijo `_not_numeric` sobre el prefijo que el módulo ya usa para `*_required`/`*_too_long`.
- Commits: conventional, terminan con
  `Session: Validación de número de documento de identidad` y `Co-Authored-By: Claude <modelo> <noreply@anthropic.com>`.
- No se toca `docs/estado.md` en la rama.

## Review Focus

1. **Número viejo con letras en una edición que no toca el documento** (pedido, cotización): debe dar 422 en el campo
   del número, no 500 ni un 422 sin campo. → Task 5, prueba `EditingAnOrderWithAStoredLetteredNitIsRejectedOnTheNumber`.
2. **Tipo en minúsculas o con espacios** (`" nit "`, Pos que compara exacto): la regla aplica igual. → Task 1
   (`RequiresDigits(" nit ")`) y Task 2 (Pos).
3. **Sólo separadores** (`"---"`, `" . "`): rechazado (no hay dígito). → Task 1.
4. **Leer una fila vieja** (`GET` de cliente con NIT `ACME SAS` sembrado por SQL): sigue respondiendo 200. → Task 7.
5. **Pasaporte/NIE/TAX_ID con letras en cada módulo**: pasa. → una prueba `*PassportWithLettersIsAccepted` por módulo
   en cada task.

---

## Bloque A — regla compartida y Clientes (Tasks 1–2)

### Task 1: Regla compartida `DocumentNumberFormat`

**Files:**
- Create: `src/BuildingBlocks/BuildingBlocks.Domain/Identification/DocumentNumberFormat.cs`
- Test: `tests/Modules/Companies/Modules.Companies.UnitTests/DocumentNumberFormatTests.cs` (mismo proyecto que
  `DocumentTypesByCountryTests`: BuildingBlocks no tiene proyecto de pruebas)

**Interfaces — Produces:**
```csharp
public static class DocumentNumberFormat
{
    public static IReadOnlyList<string> NumericTypes { get; }          // ["NIT", "CC", "CE"]
    public const string NumericTypesMessage = "El número de documento solo puede tener números para NIT, CC y CE.";
    public static bool RequiresDigits(string? type);                    // trim + OrdinalIgnoreCase contra NumericTypes
    public static bool IsNumeric(string? number, bool allowSpaces = true); // no vacío tras trim, sólo 0-9 . - [espacio], ≥1 dígito
    public static bool IsValidFor(string? type, string? number);        // !RequiresDigits(type) || IsNumeric(number)
}
```

- [ ] **Step 1: Pruebas que fallan**
  - `NumericTypesAreNitCcCe`: `Assert.Equal(["NIT","CC","CE"], NumericTypes)`.
  - `RequiresDigits` Theory: `"NIT"`,`" nit "`,`"cc"`,`"CE"` → true; `"PASAPORTE"`,`"NIF"`,`"NIE"`,`"TAX_ID"`,`""`,`null` → false.
  - `IsNumeric` Theory true: `"900123456"`, `"900.123.456-7"`, `"12 345 678"`, `" 123 "`.
  - `IsNumeric` Theory false: `"ACME SAS"`, `"900123456A"`, `"---"`, `" . "`, `""`, `null`, `"12/34"`.
  - `IsNumericWithoutSpacesRejectsSpaces`: `IsNumeric("12 345", allowSpaces: false)` false; `IsNumeric("12.345-6", false)` true.
  - `IsValidFor`: `("NIT","ACME SAS")` false; `("PASAPORTE","AB123456")` true; `("NIE","X1234567L")` true; `("CC","1.020.304")` true.
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Companies/Modules.Companies.UnitTests` → error CS0103 `DocumentNumberFormat`.
- [ ] **Step 3: Implementar** la clase con la firma de arriba.
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Companies/Modules.Companies.UnitTests --filter "FullyQualifiedName~DocumentNumberFormatTests"` → todo PASS.
- [ ] **Step 5: Commit** `feat(identification): add shared numeric document number rule`

### Task 2: Clientes (formulario, perfiles de facturación, Excel) y POS

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Domain/CustomerIdentification.cs` (`Normalized()`, tras el largo)
- Modify: `src/Modules/Customers/Modules.Customers.Domain/CustomerBillingProfile.cs` (junto a `identification_too_long`, ~`:224-230`)
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerWriteRules.cs` (regla nueva tras la de tipo/país, `:102-107`)
- Modify: `src/Modules/Customers/Modules.Customers.Application/ManageCustomerBillingProfiles.cs` (`CustomerBillingProfileWriteRules`, `:100-107`)
- Modify: `src/Modules/Customers/Modules.Customers.Application/ImportCustomers.cs` (`ExcelCustomerRowRules`, `:87-119`)
- Modify: `src/Modules/Pos/Modules.Pos.Application/PosCustomers.cs` (`CreatePosCustomerValidator`, `:57-63`)
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/` y `tests/Modules/Pos/Modules.Pos.UnitTests/` — en las
  clases de prueba que ya cubren cada validador/agregado (búscalas por el nombre del validador).

**Interfaces — Consumes:** `DocumentNumberFormat.IsValidFor`, `NumericTypesMessage` (Task 1).
El dominio pasa el enum a su código de wire (`IdentificationTypeParser`, `IdentificationType.cs:48`) antes de llamar.

**Códigos nuevos:** `customers.customer.identification_number_not_numeric`,
`customers.billing_profile.identification_number_not_numeric`, `customers.import.row.identification_number_not_numeric`
(fila con `Field` = el de las filas de número existentes). Las dos excepciones de dominio llevan `IHasFieldErrors` con
`["IdentificationNumber"]` (como `document_type_not_allowed` en `Customer.cs:1577`).

- [ ] **Step 1: Pruebas que fallan**
  - `CustomerWriteRules`: `ANitWithLettersIsRejectedOnTheNumber` → error con `PropertyName == "IdentificationNumber"` y
    mensaje `NumericTypesMessage`; `ANitWithDotsAndHyphenIsAccepted` (`900.123.456-7`); `APassportWithLettersIsAccepted`.
  - Dominio `Customer.Create` con NIT `ACME SAS` → `CustomersDomainException` con código
    `customers.customer.identification_number_not_numeric`; con `PASAPORTE` `AB123456` → crea.
  - Perfil de facturación: validador y dominio, mismos tres casos con el código de billing_profile.
  - Excel: fila CC `JUAN PEREZ` → `ImportRowError` con el código de fila nuevo; fila PASAPORTE `AB123` → sin error.
  - Pos: `CreatePosCustomerValidator` con `IdentificationType="NIT"`, número `ACME` → error en `IdentificationNumber`.
- [ ] **Step 2: RED** — build de los dos proyectos de pruebas compila (el código nuevo no existe → fallan las aserciones);
  correr filtrado por las clases tocadas → FAIL en las pruebas nuevas.
- [ ] **Step 3: Implementar** las reglas y los chequeos de dominio con los códigos de arriba.
- [ ] **Step 4: GREEN** — `dotnet test` de `Modules.Customers.UnitTests` y `Modules.Pos.UnitTests` completos → PASS.
- [ ] **Step 5: Commit** `feat(customers): reject letters in numeric document numbers`

**Revisión del bloque A** antes de seguir.

---

## Bloque B — Proveedores, Empresas, Distribuidores, Soporte, Tienda y Comprador (Tasks 3–4)

### Task 3: Proveedores, Empresas y Distribuidores

**Files:**
- Modify: `src/Modules/Suppliers/Modules.Suppliers.Domain/SupplierIdentification.cs` (`:15-31`) y
  `src/Modules/Suppliers/Modules.Suppliers.Application/SupplierWriteRules.cs` (`:62-73`)
- Modify: `src/Modules/Companies/Modules.Companies.Domain/Company.cs` (`:394-401`) y
  `src/Modules/Companies/Modules.Companies.Application/CompanyWriteRules.cs` (`:65-96`) — el número es `TaxId`
- Modify: `src/Modules/Wholesale/Modules.Wholesale.Application/DistributorWriteRules.cs` (`:66-74`) y
  `src/Modules/Wholesale/Modules.Wholesale.Domain/Distributor.cs` (`:170-171`, `:278-279`)
- Test: las clases existentes de cada validador/agregado en `Modules.Suppliers.UnitTests`, `Modules.Companies.UnitTests`,
  `Modules.Wholesale.UnitTests`.

**Códigos nuevos:** `suppliers.supplier.identification_number_not_numeric`, `companies.company.tax_id_not_numeric`,
`wholesale.distributor.identification_number_not_numeric` (si el módulo usa otro prefijo para sus errores de
distribuidor, se ajusta a ése y se anota en el commit). Campo del validador: `IdentificationNumber`, `TaxId`,
`IdentificationNumber`.

- [ ] **Step 1: Pruebas que fallan** — por módulo: `ANitWithLettersIsRejectedOnTheNumber` (validador, campo correcto),
  dominio con código nuevo, `APassportWithLettersIsAccepted`; Empresas además `ANieWithLettersIsAccepted` (`ES`, `X1234567L`).
- [ ] **Step 2: RED** — correr filtrado → FAIL en las nuevas.
- [ ] **Step 3: Implementar.**
- [ ] **Step 4: GREEN** — los tres proyectos de unitarias completos → PASS.
- [ ] **Step 5: Commit** `feat(suppliers,companies,wholesale): reject letters in numeric document numbers`

### Task 4: Soporte, checkout de la tienda y perfil del comprador

**Files:**
- Modify: `src/Modules/Support/Modules.Support.Application/Public/FilePublicTicket.cs` (`:132-145`),
  `src/Modules/Support/Modules.Support.Application/Tickets/CreateTicket.cs` (`:118-132`),
  `src/Modules/Support/Modules.Support.Domain/TicketRequester.cs`
- Modify: `src/Modules/Store/Modules.Store.Application/SubmitOrderRequest.cs` (`:123-137`),
  `src/Modules/Store/Modules.Store.Domain/StoreRequestBilling.cs` (`:41-54`)
- Modify: `src/Modules/Buyers/Modules.Buyers.Application/BuyerProfileValidator.cs` (`:55-67`),
  `src/Modules/Buyers/Modules.Buyers.Domain/BuyerProfile.cs` (`:63-70`)
- Test: clases existentes en `Modules.Support.UnitTests`, `Modules.Store.UnitTests`, `Modules.Buyers.UnitTests`.

**Códigos nuevos:** `support.ticket.document_number_not_numeric` (ajustar al prefijo de errores de `TicketRequester`),
`store.order_request.identification_number_not_numeric`, `store.buyer.identification_number_not_numeric`.
Campos: `Requester.DocumentNumber`, `Billing.IdentificationNumber`, el del número en `BuyerProfileValidator`.

- [ ] **Step 1: Pruebas que fallan** — por camino: NIT/CC con letras → error en el campo; pasaporte con letras → pasa;
  dominio con el código nuevo.
- [ ] **Step 2: RED** — filtrado → FAIL.
- [ ] **Step 3: Implementar.**
- [ ] **Step 4: GREEN** — los tres proyectos de unitarias completos → PASS.
- [ ] **Step 5: Commit** `feat(support,store,buyers): reject letters in numeric document numbers`

**Revisión del bloque B.**

---

## Bloque C — Cotizaciones/pedidos, Hr, contratos de servicio, integración y documentación (Tasks 5–7)

### Task 5: Partes de cotizaciones y pedidos

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/QuotationPartyDetails.cs` (`:193-199`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationEdits.cs` (`QuotationPartiesRequestValidator`, `:117-168`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrderEdits.cs` (`AddBillingRules`, `:644-681`)
- Test: clases existentes de esos validadores y de `QuotationPartyDetails` en `Modules.Quotations.UnitTests`.

**Código nuevo:** `quotations.party.identification_number_not_numeric`, con campo `{prefix}.IdentificationNumber`
(mismo mecanismo que `document_type_not_allowed` en `:141-153`). Validadores: `Parties.Billing.IdentificationNumber`
y `Billing.Party.IdentificationNumber`. Aplica también en preview.

- [ ] **Step 1: Pruebas que fallan**
  - `QuotationPartiesRequestValidator`: NIT `ACME SAS` → error en `Parties.Billing.IdentificationNumber`; pasaporte pasa.
  - `OrderEditsValidator`: `EditingAnOrderWithAStoredLetteredNitIsRejectedOnTheNumber` — un body que reenvía la parte
    de facturación sin cambios con NIT `ACME SAS` → error en `Billing.Party.IdentificationNumber` (Review Focus 1).
  - `QuotationPartyDetails` con NIT con letras → excepción con el código nuevo y el campo.
- [ ] **Step 2: RED** — filtrado → FAIL.
- [ ] **Step 3: Implementar.**
- [ ] **Step 4: GREEN** — `Modules.Quotations.UnitTests` completo → PASS.
- [ ] **Step 5: Commit** `feat(quotations): reject letters in numeric party document numbers`

### Task 6: Hr (empleados e importación) e importación de contratos de servicio

**Files:**
- Modify: `src/Modules/Hr/Modules.Hr.Application/HrRequestValidators.cs` (`:47-53`)
- Modify: `src/Modules/Hr/Modules.Hr.Application/EmployeeImportRowParser.cs` (`DocumentNumber()`, `:229-245`)
- Modify: `src/Modules/Hr/Modules.Hr.Application/HrErrorCodes.cs` (junto a `:113-114`)
- Create (o en el archivo de Hr que ya agrupe reglas de documento): `HrDocumentNumberRules` en `Modules.Hr.Application`
- Modify: `src/Modules/ServiceContracts/Modules.ServiceContracts.Application/ExcelServiceContractsRowRules.cs` (`:27-36`)
  y `ServiceContractsErrorCodes.cs` (`:100-102`)
- Test: clases existentes en `Modules.Hr.UnitTests` y `Modules.ServiceContracts.UnitTests`.

**Interfaces — Produces (Hr):**
```csharp
internal static class HrDocumentNumberRules
{
    public const string NumericTypesMessage = "El número de documento solo puede tener números para CC, CE, PEP y PPT.";
    public static bool RequiresDigits(HrDocumentType type);            // CC, CE, PEP, PPT
    public static bool IsValidFor(HrDocumentType type, string? number); // usa DocumentNumberFormat.IsNumeric(number, allowSpaces: false)
}
```

**Códigos nuevos:** `hr.import.row.document_number_not_numeric` (campo `documentNumber`),
`billing.import.row.identification_number_not_numeric`. Validador de empleados: campo `documentNumber`.

- [ ] **Step 1: Pruebas que fallan**
  - `EmployeeRequestValidator`: CC `PEREZ` → error en `documentNumber`; PEP `12.345-6` pasa; PA `AB123` pasa; CD `X-12` pasa.
  - Importación de empleados: fila CC `ABC` → issue con el código nuevo; fila PA `AB123` → sin issue.
  - Contratos de servicio: fila NIT `ACME SAS` → `ImportRowErrorDto` con el código nuevo; fila PASAPORTE `AB1` → sin error.
- [ ] **Step 2: RED** — build de pruebas → CS0117 (`HrErrorCodes`/`ServiceContractsErrorCodes` sin el código nuevo).
- [ ] **Step 3: Implementar.**
- [ ] **Step 4: GREEN** — `Modules.Hr.UnitTests` y `Modules.ServiceContracts.UnitTests` completos → PASS.
- [ ] **Step 5: Commit** `feat(hr,service-contracts): reject letters in numeric document numbers`

### Task 7: Integración mínima y documentación

**Files:**
- Test: la clase de integración que ya prueba `POST /customers` en `tests/Modules/Customers/Modules.Customers.IntegrationTests/`
- Modify: `.claude/rules/customers.md` (viñeta nueva junto a la de `DocumentTypesByCountry`)

- [ ] **Step 1: Pruebas de integración**
  - `CreatingACustomerWithALetteredNitIsRejectedOnTheNumber`: `POST /customers` con NIT `ACME SAS` → 422,
    `code == "validation.failed"`, `errors` contiene la clave `IdentificationNumber`.
  - `AStoredLetteredNitIsStillReadable`: sembrar por SQL un cliente con NIT `ACME SAS` → `GET` del cliente 200 (Review Focus 4).
- [ ] **Step 2: Correr** filtrado por nombre: `dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~LetteredNit"`
  (antes: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay corrida ajena, esperar) → PASS.
- [ ] **Step 3: Regla del módulo** — en `.claude/rules/customers.md`: el formato del número vive en
  `DocumentNumberFormat` (`NIT`/`CC`/`CE`), Hr tiene `HrDocumentNumberRules`; un camino nuevo que escriba un número
  usa las dos capas (validador + dominio) y la regla bloquea también números viejos.
- [ ] **Step 4: Verificación de merge** — `.\scripts\test-feature.ps1` en verde, leyendo sus avisos.
- [ ] **Step 5: Commit** `test(customers): cover lettered nit end to end and document the rule`

**Revisión del bloque C**, y al final la revisión fuerte de toda la rama (subagente de solo lectura, sin contexto).

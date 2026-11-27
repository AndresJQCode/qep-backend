# Número de documento: solo dígitos en los tipos colombianos numéricos

Fecha: 2026-11-27. Estado: aprobado por el owner en conversación (opción A y alcance por tipo).

## Por qué

Un asesor escribió el **nombre de una empresa** en el campo del número de documento y se guardó.
Hoy ningún camino de escritura valida el formato del número: sólo recorta, exige que no esté vacío
y limita el largo (los comentarios de `CustomerIdentification`, `SupplierIdentification` y
`QuotationPartyDetails` lo dicen a propósito, para conservar el NIT con puntos y guion).

## Qué se quiere

- En los tipos colombianos que siempre son numéricos, el número sólo admite **dígitos, puntos,
  guiones y espacios**, y al menos un dígito. Una letra (u otro símbolo) responde 422.
- Se guarda **lo que se escribió** (`900.123.456-7` sigue entrando igual). No cambia la
  normalización de duplicados (`CustomerIdentificationNumbers.Normalize` y su gemela SQL).
- Aplica en **todos los módulos** que escriben un número de documento desde una persona
  (formulario o Excel).

### Tipos con la regla

| Catálogo | Con la regla | Sin la regla (texto libre, como hoy) |
|---|---|---|
| `DocumentTypesByCountry` y los enums que lo replican (Customers, Suppliers, Companies, Quotations, Pos, Store, Buyers, Support, Wholesale, ServiceContracts) | `NIT`, `CC`, `CE` | `PASAPORTE`, `NIF`, `NIE`, `TAX_ID` |
| `HrDocumentType` (Hr) | `CC`, `CE`, `PEP`, `PPT` | `PA`, `CD`, `SC` |

**Por qué no "todo menos pasaporte":** un NIF español puede terminar en letra (`12345678Z`), un NIE
empieza y termina con letra (`X1234567L`) y muchos `TAX_ID` extranjeros llevan letras. La regla
literal dejaría sin poder guardar a los clientes y empresas de España.

**Hr conserva su regla de espacios:** hoy ya rechaza espacios en el número
(`HrRequestValidators`, `EmployeeImportRowParser`). En Hr, los tipos con la regla admiten dígitos,
puntos y guiones, sin espacios.

## Fuera de alcance (no-goals)

- Validar dígito de verificación del NIT, largos por tipo o rangos.
- Limpiar o reescribir el número guardado.
- Corregir datos existentes. Una fila guardada con letras en un NIT/CC/CE **se sigue leyendo**,
  pero no se puede volver a guardar igual: mismo criterio que ya rige para un tipo que no
  corresponde al país.

**Datos viejos: se bloquea siempre** (decisión del owner, 2026-11-27). La regla no distingue un
número nuevo de uno que vuelve sin cambios. Consecuencia aceptada: un pedido, cotización, perfil de
comprador o «Tomar» de la tienda cuyo documento guardado ya traiga letras no se puede guardar
—aunque lo editado sea otra cosa— hasta corregir el número. El 422 marca el campo del número para
que la pantalla diga qué corregir.
- Caminos que no son entrada de una persona: ingesta de Shopify (no trae documento), consumidor
  final (`222222222222`), copia del documento del cliente en la venta del POS, adaptadores del
  Bootstrapper que copian valores ya validados, y lectura (Siigo, Purchases, Reporting, Wms).
- Importaciones que sólo **buscan** un empleado por documento (exámenes médicos, novedades de
  nómina): no escriben el número.
- `qep-frontend`: se le entrega un prompt al terminar el backend.

## Anti-goals

- Que deje de entrar un NIT escrito con puntos o con el DV tras guion.
- Que un cliente de España o con `TAX_ID` no se pueda guardar.
- Que el índice normalizado de clientes cambie (obligaría a una migración nueva).

## Enfoques considerados

- **A. Rechazar letras y aceptar separadores** (elegido). Frena el caso real sin romper la forma
  habitual del NIT ni la normalización de duplicados.
- B. Sólo dígitos, literal. Rompe el NIT con DV y muchas filas de Excel con puntos.
- C. Limpiar en silencio y rechazar sólo si quedan letras. Guarda algo distinto de lo escrito y
  pega el DV al número.

## Diseño

### Regla compartida

En `BuildingBlocks.Domain/Identification/` (junto a `DocumentTypesByCountry`), una clase estática
con dos funciones puras:

- si un código de tipo exige número numérico (`NIT`, `CC`, `CE`; sin importar mayúsculas ni
  espacios alrededor);
- si un número cumple el formato numérico: no vacío tras recortar, sólo `0-9`, `.`, `-` y espacio,
  y al menos un dígito.

Hr tiene su propio catálogo, así que su lista (`CC`, `CE`, `PEP`, `PPT`) y su variante sin espacios
viven en Hr y reusan la misma verificación de caracteres.

### Dónde se aplica (dos capas)

Sigue la convención del repo: **el validador de FluentValidation da el campo** (422
`validation.failed` con el mapa `errors`) y **el dominio da el código** (422 con código propio),
para que los caminos que no pasan por el validador (POS → `CreateFromRegisterAsync`, importación de
contratos de servicio, tienda → cliente) también queden cubiertos.

| # | Camino | Validador (campo) | Dominio / fila (código) |
|---|---|---|---|
| 1 | Clientes `POST`/`PUT /customers` | `CustomerWriteRules` → `IdentificationNumber` | `CustomerIdentification` → `customers.customer.identification_number_not_numeric` |
| 2 | Perfiles de facturación | `CustomerBillingProfileWriteRules` → `IdentificationNumber` | `CustomerBillingProfile` → `customers.billing_profile.identification_number_not_numeric` |
| 3 | Importación de clientes | `ExcelCustomerRowRules` → fila `customers.import.row.identification_number_not_numeric` | — |
| 4 | POS `POST /pos/customers` | `CreatePosCustomerValidator` → `IdentificationNumber` | cubre el dominio de 1 |
| 5 | Tienda → cliente (Tomar / merge) | — | cubre el dominio de 1 |
| 6 | Proveedores | `SupplierWriteRules` → `IdentificationNumber` | `SupplierIdentification` → `suppliers.supplier.identification_number_not_numeric` |
| 7 | Empresas (el número se llama `TaxId`) | `CompanyWriteRules` → `TaxId` | `Company` → `companies.company.tax_id_not_numeric` |
| 8 | Distribuidores (invitar / editar) | `DistributorProfileValidator` → `IdentificationNumber` | `Distributor` → `wholesale.distributor.identification_number_not_numeric` |
| 9 | Hr empleados | `EmployeeRequestValidator` → `documentNumber` | — (Hr valida en Application) |
| 10 | Importación de empleados | fila `hr.import.row.document_number_not_numeric` | — |
| 11 | Importación de contratos de servicio | fila `billing.import.row.identification_number_not_numeric` | cubre el dominio de 1 |
| 12 | Soporte, formulario público | `FilePublicTicket` → `Requester.DocumentNumber` | `TicketRequester` → `support.ticket.document_number_not_numeric` |
| 13 | Soporte, crear ticket | `CreateTicket` → `Requester.DocumentNumber` | igual que 12 |
| 14 | Checkout de la tienda | `SubmitOrderRequest` → `Billing.IdentificationNumber` | `StoreRequestBilling` → `store.order_request.identification_number_not_numeric` |
| 15 | Perfil del comprador | `BuyerProfileValidator` → campo existente del número | `BuyerProfile` → `store.buyer.identification_number_not_numeric` |
| 16 | Cotizaciones y pedidos (crear, editar, preview) | `QuotationPartiesRequestValidator` → `Parties.Billing.IdentificationNumber` | `QuotationPartyDetails` → `quotations.party.identification_number_not_numeric` |
| 17 | Edición de pedido | `OrderEditsValidator` → `Billing.Party.IdentificationNumber` | igual que 16 |

Los nombres exactos de los códigos siguen el prefijo que ya usa cada módulo para su error de
número (`*_required`, `*_too_long`); el implementador los ajusta a ese prefijo si difiere del de la
tabla, y la tabla final del contrato va en el prompt del frontend. El dominio lanza la excepción
con `IHasFieldErrors` cuando el módulo ya lo hace para el tipo, para que el formulario marque el
input.

**Mensaje** (tuteo, para el validador y la fila del Excel): «El número de documento solo puede
tener números para NIT, CC y CE.» (en Hr: «… para CC, CE, PEP y PPT.»).

**Orden:** la regla corre sólo cuando el tipo es válido y el número no está vacío, para no
duplicar los errores que ya existen.

### Pruebas (TDD)

- Unitarias de la regla compartida (tipos con y sin regla, mayúsculas, `900.123.456-7`,
  `12 345 678`, `ACME SAS`, `---`, vacío) y de la variante de Hr.
- Una unitaria por validador y por dominio tocado: NIT con letras → error en el campo/código;
  `PASAPORTE` / `NIE` / `PA` con letras → pasa; NIT con puntos y guion → pasa.
- Integración mínima (filtrada por nombre): `POST /customers` con NIT `ACME SAS` → 422 con el campo
  `IdentificationNumber`, y una fila de importación de clientes con el código nuevo.
- Para mergear a `develop`: `.\scripts\test-feature.ps1` en verde, leyendo sus avisos.

### Decisiones del redactor (corregibles)

- **Hr compara por valor del enum** `HrDocumentType`, no por texto.
- **El chequeo del dominio vive en los métodos de crear/editar**, nunca en la materialización de
  EF ni en los `Restore`/rehidratación: una fila vieja se tiene que poder leer.
- **Tienda → cliente (Tomar) e importación de contratos de servicio** responden el 422 con el
  código del dominio, sin mapa `errors`: no hay formulario que marcar.
- **La regla va donde ya vive la de tipo/país de cada módulo** y corre después de ella.
- **Los códigos de fila del Excel son texto libre**, como los existentes; no hay catálogo de mensajes.
- **Documentación:** `docs/estado.md` se actualiza en `develop` al mergear (no en la rama), y la
  regla se anota en `.claude/rules/customers.md` junto a la de `DocumentTypesByCountry`.

## Lo que queda al implementador

- Nombre de la clase compartida y de sus funciones.
- Ajustar el código de error al prefijo real de cada módulo.
- Dónde exactamente engancha cada validador, respetando el orden de arriba.

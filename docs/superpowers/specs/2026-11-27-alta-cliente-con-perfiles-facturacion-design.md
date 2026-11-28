# Alta de cliente con sus perfiles de facturación

Fecha: 2026-11-27. Estado: **borrador, pendiente de aprobación del owner.** Tarjeta: <https://trello.com/c/gPRWKkXq>.
La pide el frontend (<https://trello.com/c/boyBajOt>, spec
`qep-frontend/docs/superpowers/specs/2026-11-27-ficha-cliente-card-facturacion-design.md`).

## Por qué

En el alta (`/customers/new`) el frontend arma los perfiles de facturación como borradores que viajan con el cliente.
Hoy `CreateCustomerRequest` no los recibe. Crearlos después, uno por uno, deja clientes a medias cuando falla un
request.

## Lo verificado en el código (develop `cda5979e9`)

- **El alta** (`CreateCustomerHandler`) valida, crea el agregado y guarda con un solo `SaveChangesAsync`. Con empresa
  abre una transacción explícita para bloquear la fila de la empresa.
- **El alta de un perfil** (`AddCustomerBillingProfileHandler`) corre, en este orden:
  - `CustomerBillingProfileWriteRules` (validador);
  - `CustomerRegionRules.ResolveRegionAsync` con `BillingProfileRegionInvalid`;
  - la ciudad DIVIPOLA, que se comprueba en `CustomerAddressWorkflow.LoadAsync`;
  - `CustomerWithholdingRules.ValidateAsync`;
  - `Customer.EnsureBillingProfile`.
- **`Customer.EnsureBillingProfile`:**
  - es idempotente por contenido;
  - el primer perfil queda principal;
  - el tope es 50 (`customers.billing_profile.limit_reached`);
  - exige un cliente completo (`customers.customer.incomplete`).
- **Errores de FluentValidation:** `ApiExceptionHandler` publica `PropertyName` tal cual en `errors`, así que una regla
  por elemento produce `BillingProfiles[0].IdentificationNumber`. El frontend lee esa forma sin distinguir mayúsculas.

## Diseño

### Contrato

- `CreateCustomerRequest` suma `billingProfiles?: CustomerBillingProfileRequest[]`, con la misma forma que el cuerpo de
  `POST /customers/{id}/billing-profiles`. Ausente o `null` significa ninguno.
- La respuesta es el `CustomerDto` de siempre, con los perfiles creados en el orden en que llegaron.
- **Con más de un `isPrincipal: true`, responde 422 `validation.failed`** con la clave `BillingProfiles`: «Sólo un
  perfil de facturación puede ser el principal.».
  - Es un error de forma del cuerpo, que el validador ya sabe marcar. Un código de dominio aparte no le daría a la
    pantalla nada que el mapa `errors` no le dé.
- **Con más de 50, responde 422 `customers.billing_profile.limit_reached`**, el mismo código del alta de uno.
  - Lo frena el validador antes de quemar un número del CUC, con la misma clave `BillingProfiles`.
- Si ninguno pide `isPrincipal`, el primero queda principal, como hoy.

### Validación, en el mismo orden que el alta de un perfil

1. **Validador:** `CreateCustomerValidator` incluye `RuleForEach(BillingProfiles).SetValidator(...)` con las mismas
   reglas de `CustomerBillingProfileWriteRules`. Para eso el comando de cada perfil implementa
   `ICustomerBillingProfileWriteCommand`. Las claves salen con índice.
2. **Antes de pedir el CUC,** para cada perfil: región (`BillingProfileRegionInvalid`), ciudad DIVIPOLA si es
   colombiano (`customers.customer.city_not_found`) y tasas de retención.
   - Así una referencia mala no quema un número del consecutivo, igual que con la clasificación y la ciudad del cliente.
3. **Después de `Customer.Create`:** `EnsureBillingProfile` para cada uno, en orden.
   - Dos perfiles iguales en el mismo cuerpo dan uno solo, por la idempotencia de hoy.

### Transacción

Los perfiles entran al agregado antes del único `SaveChangesAsync`, así que el cliente y sus perfiles se guardan juntos
o no se guarda nada. Con empresa se usa la transacción que ya existe.

### Auditoría

La del alta del cliente, como hoy, más una entrada por perfil, igual a la del alta de un perfil, si ese camino audita.
Se confirma al implementar y no se inventa una nueva.

## Fuera de alcance

- Editar o quitar perfiles en el alta.
- Cualquier cambio a `PUT /customers/{id}` o a la importación por Excel.

## Pruebas (TDD)

- **Unitarias del validador:**
  - clave con índice en un perfil inválido;
  - dos principales → `BillingProfiles`;
  - 51 perfiles → `BillingProfiles`.
- **Unitarias del handler:**
  - una región, una ciudad o una tasa mala en el segundo perfil no crea nada y no pide CUC;
  - el primero queda principal si ninguno lo pide.
- **Integración, filtrada por nombre:**
  - `POST /customers` con dos perfiles devuelve los dos y el principal;
  - un perfil inválido deja la base sin cliente.

## Entrega

Comentar el contrato final en la tarjeta del frontend (`boyBajOt`). El backend despliega primero.

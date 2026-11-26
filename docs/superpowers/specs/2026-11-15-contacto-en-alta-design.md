# Elegir «Contacto de» al crear un cliente — diseño (backend)

- **Fecha:** 2026-11-15
- **Módulo:** `Modules.Customers` (`CreateCustomer`, `CustomersDtos.CreateCustomerRequest`, `Customer`)
- **Estado:** aprobado por el owner; rama `feature/contacto-en-alta`
- **Verificado contra el código:** `develop` en `6e8c64f7`
- **Spec hermano (frontend):** `qep-frontend/docs/superpowers/specs/2026-11-15-contacto-en-alta-design.md`
- **Antecedente:** spec 2026-11-10 (contactos de empresa, `SetCustomerCompany`)

## 1. Por qué

Hoy un cliente que es contacto de una empresa se crea en dos pasos: el alta (`POST …/customers`) y después el vínculo desde
su detalle (`PUT …/customers/{id}/company`). El owner quiere elegir la empresa en el mismo formulario de alta. Hacerlo en el
frontend con dos requests encadenados deja un cliente creado sin empresa cuando el segundo falla, y va contra la regla BFF
del frontend: el contrato se arregla acá.

## 2. Qué cambia

- `CreateCustomerRequest` y `CreateCustomerCommand` ganan `Guid? CompanyId`, opcional y al final. Ausente o `null` crea el
  cliente exactamente como hoy.
- Con `CompanyId`, `CreateCustomerHandler`, en la misma unidad de trabajo del alta:
  1. bloquea la fila de la empresa como lo hace `SetCustomerCompanyHandler` (`LockForCompanyLinkAsync`), para que no se
     convierta en contacto de otra mientras tanto;
  2. la carga (`FindAsync`); si no existe, `CustomersErrorCodes.CompanyNotFound`;
  3. vincula con las mismas reglas del dominio que `LinkToCompany`: mismo tenant y no fusionada (`company_not_found`),
     activa (`company_inactive`), no es contacto de otra (`company_is_contact`).
- Los códigos y el status HTTP son los mismos que ya responde `PUT …/company`. El frontend los mapea al campo `companyId`.
- La respuesta del `POST` ya trae `company` (el DTO del cliente); con vínculo, llega resuelta.
- Auditoría (decisión mía, corregible): un solo evento, `customers.customer.created`, como hoy (`ICustomersAuditPublisher`
  no lleva payload). No se emite un `CompanyChanged` aparte: no hubo un cambio, el cliente nació así.
- La transacción explícita se abre **sólo** con `CompanyId` (el bloqueo la exige). Ante `identification_taken` se deshace
  antes de `CustomerIdentificationTaken.ResolveAsync`, que relee al dueño: en una transacción abortada de Postgres esa
  lectura fallaría.

## 3. Lo que no cambia

- `PUT …/customers/{id}` (editar) no toca `CompanyId`: el vínculo de un cliente existente sigue yendo por `PUT …/company`.
- `company_is_self` y `has_contacts` no aplican al alta: el cliente es nuevo, no puede ser su propia empresa ni tener contactos.
- Importación de Excel: sin cambios.

## 4. Corrección de documentación

El comentario de `Customer.CompanyId` (`Customer.cs`) dice que «crear y editar un cliente nunca lo tocan». Pasa a decir que
el alta puede fijarlo y que editar no lo toca.

## 5. Enfoques considerados

- **A (elegido):** `CompanyId` en el `POST`. Un request, atómico, reglas del dominio en un solo lugar.
- **B:** el frontend crea y luego llama a `PUT …/company`. Descartado: no es atómico y contradice la regla BFF.
- **C:** que la razón social cree una empresa al vuelo. Descartado: crea duplicados por un typo y un cliente exige documento,
  país y ciudad, que un texto suelto no tiene.

## 6. Pruebas

- Unitarias del dominio/handler: alta con empresa válida queda vinculada; con empresa inexistente, de otro tenant,
  fusionada, inactiva o que es contacto, falla con el código correspondiente y **no** crea el cliente.
- Integración (`CustomerEndpoints`): `POST` con `companyId` responde el cliente con `company` resuelto; con empresa inválida
  responde el error y el padrón no tiene el cliente.
- Alta sin `companyId`: las pruebas existentes siguen en verde.

## 7. Deploy

Backend primero. Un frontend nuevo contra un backend viejo manda `companyId` y el backend viejo lo ignora: el cliente se crea
sin empresa. Por eso el orden importa.

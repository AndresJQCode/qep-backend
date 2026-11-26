# La región del domicilio se guarda (backend) — diseño

Fecha: 2026-11-23. Repo: `qep-backend`. Rama: `feature/region-persist`. Spec hermano del frontend:
`qep-frontend/docs/superpowers/specs/2026-11-23-provincia-guardada-design.md`.

## Por qué

El selector de ubicación del frontend filtra la ciudad por región (provincia en España, departamento en Colombia). Hoy
la región **no se guarda** (README, § «Regiones y ciudades por país»): Colombia la deriva de `CityId`, pero un
domicilio de España sólo guarda el nombre del municipio, así que al abrir la ficha de un cliente de España la
provincia sale vacía aunque la ciudad esté. El owner decidió (2026-11-23) que la región se guarda, y que **no** hay
migración de datos: lo que ya existe queda sin región hasta que alguien lo edite y la elija.

Esto revierte la regla del 2026-11-22 («la región es sólo un filtro, NO se guarda») para los países con catálogo que
no son Colombia.

## Objetivos

- Cada domicilio de un país con catálogo de regiones distinto de CO puede guardar su región junto al `CityName`.
- Las lecturas devuelven la región ya resuelta (`id` y `name`), para que la ficha la preseleccione sin pedir nada más.
- Colombia no cambia: su región sigue saliendo de `CityId`.

## No objetivos

- **Sin migración de datos ni backfill.** Las filas existentes quedan con región `null`.
- Los flujos que crean domicilios sin pasar por la cascada (tienda pública, Shopify, Mayorista, Compradores,
  importación de clientes por Excel, Soporte) no escriben región: queda `null`.
- La ciudad sigue siendo texto: no se valida que `CityName` pertenezca a la región, ni se guarda el id de la ciudad
  del catálogo.
- Listados y exportaciones a Excel no cambian de forma.

## Anti-objetivos

- Que una región de otro país, o una región junto a una ciudad de Colombia, se guarde.
- Que un cliente existente cambie de comportamiento por no traer región.

## Alcance: seis entidades

Cada una guarda hoy la ciudad con el mismo patrón (referencia blanda a Geography, `CityId` en CO, `CityName` fuera de
CO). El carril lo respalda un `CHECK` en la base **sólo** en `customer_addresses` (`CK_customer_addresses_city_lane`) y
`companies` (`CK_companies_city_lane`); en las otras cuatro lo impone el dominio o el validador (en las partes,
`QuotationPartyDetails.EnsureLane`):

| Entidad | Dominio |
| --- | --- |
| Cliente | `Customer` |
| Dirección del cliente | `CustomerAddress` |
| Empresa facturadora | `Company` |
| Proveedor | `Supplier` |
| Parte de cotización | `QuotationParty` |
| Parte de pedido | `OrderParty` |

## Diseño

*Los nombres de columna, de campo y de código de error son propuestas que se alinean con las convenciones de cada
módulo; el implementador las confirma contra el código.*

### Persistencia

- Una columna `region_id uuid null` por entidad, **referencia blanda** a `geography.regions` (sin FK entre esquemas,
  igual que `CityId`). Propiedad `RegionId` (`Guid?`) en cada entidad, junto a `CityName`.
- Carril: `RegionId` sólo es válido con un país distinto de `CO`, y sólo con el carril `CityName`. En
  `customer_addresses` y `companies` se extiende el `CHECK` de carril existente (`region_id IS NULL` cuando
  `country = 'CO'`); en las otras cuatro no se agrega un `CHECK` nuevo, y la regla vive donde hoy vive su carril
  (validador de `Customer` y `Supplier`; `EnsureLane` en las partes, que lanza el `LaneInvalid` existente y llega como
  422 por el mapeo central).
- **Partes de cotización y de pedido: columna propia `region_id`.** No se reutiliza `department_id`: hoy `EnsureLane`
  rechaza `DepartmentId` y `CityId` fuera de CO, y darle un segundo significado a la columna mezclaría una referencia a
  `geography.departments` con otra a `geography.regions`. Como `QuotationPartyDetails` es un `record`, `RegionId` entra
  en su igualdad: `Order.SameExceptCity` y `CorrectExternalShippingCity` comparan detalles, así que cambiar la región
  cuenta como cambio de ubicación.
- **Cliente:** la región vive junto a `CityName` en `CustomerContactInfo`; el domicilio principal la toma de ahí y el
  DTO del cliente trae `Region`.
- Migración aditiva por módulo (Customers, Companies, Suppliers, Quotations). Sin backfill.

### Escritura

- Los comandos de crear y editar de las seis entidades aceptan `RegionId` opcional.
- Reglas, en el validador de cada comando, con el mismo estilo que las del carril de la ciudad:
  1. Con `CO` (o país ausente, que cuenta como CO): un `RegionId` presente se rechaza con 422.
  2. Con otro país: si viene, el país debe tener catálogo y la región debe pertenecer a ese país; si no, 422.
     Si el país no tiene catálogo (`regionKind` nulo), un `RegionId` presente se rechaza con 422.
  3. Ausente o `null` es «sin región».
- La semántica del `PUT` es de reemplazo: no mandar `RegionId` borra la región guardada, como pasa hoy con
  `CityName`. Cambiar el país a CO o a uno sin catálogo limpia la región.
- La validación y la lectura consultan a Geography extendiendo el puerto que cada módulo ya tiene
  (`ICustomerGeographyLookup`, `ICompanyGeographyLookup`, `ISupplierGeographyLookup`, `IQuotationGeographyLookup`) con
  una búsqueda de región (existencia, país al que pertenece y nombre), implementada en el Bootstrapper contra
  `IRegionRepository`; no se agregan referencias de proyecto entre módulos.
- Un error de carril lanzado desde el dominio (partes) es aceptable: el mapeo central ya lo responde como 422.
- **Rutas que escriben domicilios sin pasar por la cascada** (Shopify, Mayorista, Compradores, tienda pública):
  reconstruyen los detalles sin región, y eso es aceptable. La corrección de ciudad de Shopify cambia la ciudad, así que
  limpiar la región es lo correcto. Regla general: **toda ruta que conserve el `CityName` existente conserva también el
  `RegionId`** (copia cotización → pedido, ediciones con `with { … }`, la actualización por Excel cuando la fila no trae
  ciudad y deja el país y la ciudad como estaban).
- La edición de las partes del pedido (`OrderEdits`) toma `regionId` de la petición, igual que `CityName`.
- La importación por Excel sólo trata clientes colombianos: la región no interviene, salvo la regla de conservación de
  arriba.

### Lectura

- Los DTOs de detalle (cliente, dirección del cliente, empresa, proveedor, parte de cotización y de pedido) agregan
  `Region: { Id, Name } | null`, resuelto por el mismo puerto. `null` para CO (la región de Colombia sigue en
  `Department`) y para lo que no tiene región guardada. Los listados no la traen.
- La parte de cotización copia `RegionId` a la parte de pedido al convertir, como copia hoy `CityName`.
- Un campo nuevo en un DTO sólo llega al navegador si está también en el `Response` HTTP del módulo (gotcha de
  `QuotationResponse`): el implementador revisa cada uno.

### Contrato HTTP

Campos nuevos, todos opcionales para no romper a un cliente viejo:

- Request: `regionId` (GUID o ausente) en crear/editar cliente, dirección, empresa, proveedor y partes de cotización
  y de pedido.
- Response: `region` (`{ id, name }` o `null`) en los detalles de esas entidades.

Códigos de error: dos nuevos por módulo (región de otro país o sin catálogo; región con país colombiano), siguiendo la
convención `<modulo>.<entidad>.<motivo>` ya usada por `city_lane_invalid`.

### Auditoría y eventos

Donde la auditoría o un evento ya incluyen la ciudad, incluyen también la región. El implementador lo revisa por
entidad; no se crean eventos nuevos.

## Pruebas

- Unidad: las reglas de escritura por entidad (CO con región, país sin catálogo con región, región de otro país,
  ausente borra, cambio de país limpia).
- Integración (por módulo tocado, con filtro por nombre mientras se itera): crear y leer con región; editar sin
  región la borra; el `CHECK` de la base rechaza `CO` con región; conversión cotización → pedido copia la región.
- `ArchitectureTests` sin cambios de referencias.

## Despliegue y compatibilidad

Backend primero. Un frontend viejo no manda `regionId` y no lee `region`: funciona igual. Un frontend nuevo contra un
backend viejo manda un campo que el backend ignora y no recibe `region`: lo trata como «sin región».

## Decisiones pendientes del owner

Ninguna abierta. Decididas el 2026-11-23: la región se guarda en las seis entidades y no hay migración de datos.

## Decisiones del implementador (propias, a confirmar en el código)

- Referencia blanda por `Guid`, sin FK entre esquemas.
- Columna `region_id` propia en las partes (sin reutilizar `department_id`) y extensión de los puertos existentes en vez
  de puertos nuevos.
- 422 (no descarte silencioso) para una región enviada con CO o con un país sin catálogo.
- Región en los detalles, no en los listados ni en las exportaciones.
- Sin cambios en la importación de clientes por Excel.

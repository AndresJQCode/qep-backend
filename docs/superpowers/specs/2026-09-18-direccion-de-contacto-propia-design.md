# Dirección de contacto propia del cliente — diseño (backend)

**Fecha:** 2026-09-18
**Repo:** `qep-backend` (módulo `Customers`; adaptadores en `Bootstrapper`)
**Contraparte:** `qep-frontend/docs/superpowers/specs/2026-09-18-direccion-de-contacto-propia-design.md`
**Estado:** aprobado en conversación; pendiente de revisión escrita

## Problema

En la ficha del cliente, marcar otra dirección de la libreta como principal **cambia los datos de
contacto** (departamento, ciudad y dirección). No debe ocurrir: una cosa es la libreta de destinos de
envío, con su favorita; otra es el domicilio del cliente.

Hoy no son dos cosas. `CLI-DIR-01` (migración `20260906002234_AddCustomerAddresses`) **borró**
`customers.address` y `customers.city_id` y los movió a la primera fila de `customer_addresses`, marcada
`is_principal`. Desde entonces:

- `CustomerMapping.cs:25-27` y `:85-90` sacan `address`/`city`/`department` del cliente de
  `RequirePrincipalAddress()`.
- `UpdateCustomer.cs:78-89` e `ImportCustomers.cs:535-546` escriben el `address`/`cityId` del request
  **sobre** la principal.
- `ListCustomers.cs:155-158`, `ExportCustomers.cs:170-173`, `Bootstrapper/QuotationCustomerLookup.cs:38-61`
  y `Bootstrapper/CustomerReportSource.cs:190-339` leen la ciudad del cliente de la principal.

Consecuencia visible: cambiar la favorita a "Trabajo" mueve el domicilio del cliente a "Trabajo", y el
próximo `PUT` de la ficha escribe el domicilio viejo **encima** de "Trabajo". Se pierde una dirección
sin aviso.

Quedaron dos vestigios de antes de `CLI-DIR-01` que sirven para esto: `CustomerContactInfo.Address`
(`CustomerContactInfo.cs:22-47`, con `AddressMaxLength = 200` y `customers.customer.address_too_long`),
que `Customer.Assign` ya no asigna, y `Customer.EnsureValidCityId` (`Customer.cs:503-508`), sin caller.

## Decisiones tomadas

| #   | Decisión                                                                                                                                                                                                                         | Alternativa descartada                                                                                                                                                                                                                         |
| --- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1   | El cliente vuelve a tener **dirección y ciudad propias** (`customers.address`, `customers.city_id`). La libreta queda sólo como catálogo de destinos de envío; `is_principal` es "cuál se ofrece primero" y nada más.               | Sólo frontend (no copiar la principal en la ficha al marcarla): el GET seguiría devolviendo la principal como domicilio, la ficha la mostraría al recargar, y el `PUT` la pisaría. Pierde datos.                                              |
| 2   | Se reviven los vestigios: `CustomerContactInfo` gana `CityId` junto al `Address` que ya tiene, y `Customer.Assign(contact)` vuelve a asignarlos. No se crea un value object nuevo.                                                | `CustomerContactAddress` aparte. Más tipos para lo mismo; los vestigios ya llevan validación y código de error.                                                                                                                                 |
| 3   | Al crear, la dirección del request **sigue sembrando** la primera fila de la libreta (`Customer.Create`, `CreateCustomer.cs:83-89`), además de guardarse como contacto. Un cliente nuevo tiene a dónde enviar sin abrir la libreta. | Crear sin dirección de envío: la cotización necesita la libreta para el selector de envío y hoy cae al domicilio; la regla "la primera es la principal" tiene tests (`CreateWithoutAnAddressMarksTheAddressField`).                             |
| 4   | `UpdateCustomer` e `ImportCustomers` (modo actualización) escriben **sólo** el contacto. No tocan la libreta.                                                                                                                    | Seguir espejando el contacto en la principal: es exactamente el acoplamiento que causa el bug.                                                                                                                                                 |
| 5   | **Todo lo que significa "dónde está el cliente" lee el contacto**: DTO plano, listado, export, `QuotationCustomerLookup` (domicilio maestro de cotizaciones y pedidos) y el reporte de clientes por departamento.                 | Dejar el reporte agrupando por la principal: el listado diría una ciudad y el panel otra para el mismo cliente.                                                                                                                                |
| 6   | Migración con **backfill desde la principal**: ningún cliente cambia de domicilio visible al desplegar.                                                                                                                          | Columnas nulas hasta que alguien edite la ficha: rompe `RequirePrincipalAddress()`-equivalentes en GET/list y muestra clientes sin ciudad.                                                                                                     |
| 7   | La dirección de contacto sigue siendo **obligatoria** (calle y ciudad), como antes de `CLI-DIR-01` y como hoy exige el `422` al crear sin dirección.                                                                              | Hacerla opcional: el PDF y la cotización caen a ella como respaldo de facturación y envío; vacía, imprimen un cliente sin domicilio.                                                                                                             |
| 8   | Los endpoints de la libreta (`/customers/{id}/addresses*`) **no cambian** de contrato. Su respuesta `CustomerResponse` ahora trae el contacto en los campos planos, que ya no se mueven al marcar principal.                       | —                                                                                                                                                                                                                                              |

## Contrato (lo que ve el frontend)

Sin cambios de forma. Cambia el **significado** de tres campos de `CustomerResponse`:

- `address`, `city`, `department` describen el **domicilio del cliente** (contacto), no la principal.
- `addresses[]` sigue igual: todas las direcciones de envío, la principal primero.
- `POST /customers/{id}/addresses/{addressId}/principal` devuelve el cliente con los campos planos
  **iguales** a antes de la llamada.
- `PUT /customers/{id}` con `address`/`cityId` distintos deja `addresses[]` **igual** a antes.
- `POST /customers` sigue creando la primera fila de la libreta con `Name = name`, `Phone = phone`,
  `Address = address`, `CityId = cityId`, `isPrincipal = true`, y guarda lo mismo como contacto.
- Códigos de error del `PUT`/`POST`: `customers.customer.address_too_long` (existente,
  `CustomerContactInfo.cs:46`) vuelve a poder salir; `customers.customer.city_required` (existente,
  `Customer.cs:506`, emitido por `EnsureValidCityId`) sale en el `PUT` (y en `Update`) para `cityId`
  vacío — en el `POST` el mismo vacío sale antes, como `customers.address.city_required`, porque el
  agregado arma la primera fila de la libreta (`Customer.cs`, ctor, y `CustomerAddress.Create`) antes
  de correr `Assign(contact)`; y `customers.customer.address_required`, **nuevo**, para calle vacía.
  Los tres son defensa en profundidad: sobre HTTP los intercepta antes `CustomerWriteRules`
  (FluentValidation), que rechaza los valores vacíos como `422 validation.failed` con el mapa
  `errors`, y las reglas de fila de importación hacen lo mismo por fila — el frontend normalmente ve
  `validation.failed`, no estos códigos de dominio.

## Dominio (`Modules.Customers.Domain`)

- `CustomerContactInfo`: suma `Guid CityId` (required). `Normalized()` valida `Address` no vacío
  (`customers.customer.address_required`, código nuevo, mismo estilo que
  `customers.address.address_required`) y largo ≤ 200 (código existente).
- `Customer`: propiedades `Address` y `CityId`. El constructor y `Create` las toman de `contact`;
  siguen recibiendo `principalAddress` para sembrar la libreta (decisión 3). `Assign(contact)` asigna
  `Phone`, `Email`, `Address`, `CityId`; `EnsureValidCityId` vuelve a tener caller.
- `Update(...)` no cambia de firma: ya recibe `CustomerContactInfo`.
- La libreta (`AddAddress`, `UpdateAddress`, `MakeAddressPrincipal`, `RemoveAddress`, `ApplyPrincipal`)
  **no se toca**. El invariante "una sola principal, la principal no se borra" sigue igual.
- Se borra el comentario de `Customer.cs:330` ("La ciudad ya no viaja acá") y equivalentes que digan
  que el domicilio es la principal.

## Aplicación (`Modules.Customers.Application`)

- `CreateCustomer.cs`: arma `CustomerContactInfo { Phone, Email, Address, CityId }` y **además** el
  `CustomerAddressDetails` de la primera fila, como hoy.
- `UpdateCustomer.cs:75-89`: se **elimina** el bloque que edita la principal. El `CityId` sigue
  resolviéndose contra geografía antes (líneas 64-67) porque la respuesta lo devuelve resuelto.
- `ImportCustomers.cs:535-546`: mismo cambio que `UpdateCustomer`.
- `CustomerMapping.cs`: `ToDto`/`ToDtoAsync` leen `customer.Address` y `customer.CityId`. El comentario
  de `:25-27` y el de `CustomersDtos.cs:51` pasan a decir "domicilio del cliente; las direcciones de
  envío van en `addresses`".
- `ListCustomers.cs:155-158`, `ExportCustomers.cs:170-173`: leen `customer.CityId`. Los filtros por
  departamento/ciudad del listado filtran por el contacto.

## Infraestructura (`Modules.Customers.Infrastructure`)

- `CustomersDbContext.ConfigureCustomer`: columnas `address` (`varchar(200)`, not null) y `city_id`
  (`uuid`, not null); índice `IX_customers_city`. Igual que `customer_addresses`, la FK a
  `geography.cities` se escribe **a mano en la migración**, no en el modelo EF (mismo motivo que
  `CustomersDbContext.cs:162-167`).
- Migración `AddCustomerContactAddress`, en este orden y en una sola transacción:
  1. `ADD COLUMN address varchar(200) NOT NULL DEFAULT ''`, `ADD COLUMN city_id uuid NULL`.
  2. `UPDATE customers c SET address = a.address, city_id = a.city_id FROM customer_addresses a WHERE a.customer_id = c.id AND a.is_principal`.
  3. `ALTER COLUMN city_id SET NOT NULL`, `DROP DEFAULT` de `address`.
  4. `ADD CONSTRAINT FK_customers_cities_city_id ... ON DELETE RESTRICT`, `CREATE INDEX IX_customers_city`.
  - `Down`: quitar FK, índice y columnas. No hay que devolver nada a la libreta: nunca se sacó.
  - Precondición verificada por el backfill: todo cliente tiene exactamente una principal
    (`Customer.ApplyPrincipal`). Si el `UPDATE` deja `city_id` nulo, el paso 3 falla y la migración
    no aplica. Eso es lo deseado: es un dato roto que hay que mirar, no un default.
- Actualizar `CustomersDbContextModelSnapshot.cs` con `dotnet ef migrations add`, no a mano.

## Adaptadores (`Bootstrapper`)

- `QuotationCustomerLookup.cs:38-61`: `Address`, `CityId`, `CityName`, `DepartmentId`,
  `DepartmentName` salen de `customer.Address`/`customer.CityId`. `Addresses` sigue mapeando la
  libreta completa con `IsPrincipal`. Es el domicilio maestro que `QuotationResponseComposer.cs:96-120`
  entrega a la cotización y al pedido como respaldo de facturación y envío.
- `CustomerReportSource.cs:190-195`, `:301-303`, `:336-339`: agrupan y filtran por `customer.CityId`
  en vez de `Addresses.Any(a => a.IsPrincipal ...)`. Consulta más simple.
- `Seeding/ExportLoadSeeder.cs:190` y cualquier seeder que cree clientes: cargar contacto y primera fila.

## Bordes

- **Cliente sin principal en base** (no debería existir): la migración falla en el paso 3 y lo
  denuncia. Después de la migración `RequirePrincipalAddress()` deja de usarse en lectura, así que
  un cliente sin libreta ya no rompe GET/list; sí sigue sin poder cotizar envío desde la libreta.
- **`PUT` con la misma ciudad y calle que la principal**: guarda el contacto igual; la libreta no se
  toca. Ya no hay "sincronía" ni en un sentido ni en el otro.
- **Borrar la principal**: sigue prohibido (`customers.address.principal_not_removable`). Se mantiene
  porque la cotización preselecciona la principal para envío; sin ella el selector no tiene default.
- **Import en modo actualización con `address`/`cityId`**: actualiza el contacto y deja la libreta
  igual. El comentario de `CustomerStatusAndImportApiTests.cs:501` se corrige.
- **Concurrencia**: `Update` ya hace `Touch` (sube `Version`). Las operaciones de libreta también.
  Sin cambio.

## Pruebas

TDD: RED antes que GREEN, con evidencia literal. Suite de integración con Testcontainers: tarda
decenas de minutos y exige Docker; correr sólo los archivos tocados durante el ciclo y la suite
completa al final.

- **`CustomerTests.cs`** (unitarias)
  - `UpdateChangesTheContactAddressAndLeavesTheAddressBookUntouched`
  - `MakeAddressPrincipalDoesNotChangeTheContactAddress`
  - `CreateSeedsTheContactAddressAndTheFirstAddressBookRow`
  - `ContactInfoRejectsAnEmptyAddress` y `ContactInfoRejectsAnEmptyCityId` (códigos exactos)
  - `ContactInfoRejectsAnAddressLongerThanTheColumn` deja de probar un vestigio; se conserva.
- **`CustomerWriteApiTests.cs`** (integración)
  - `MakingAnotherAddressPrincipalKeepsTheCustomerAddressAndCity` — GET antes y después iguales en
    `address`/`city`/`department`; `addresses[]` con la nueva principal.
  - `UpdateChangesTheCustomerAddressWithoutTouchingTheAddressBook` — `PUT` con otra ciudad; la fila
    principal conserva su `address`/`cityId`.
  - `UpdateCanChangeTheCityAndTheClassification` (existente): sigue verde; ahora afirma sobre el
    contacto.
  - `CreateWithoutAnAddressMarksTheAddressField` (existente): sigue verde.
- **`CustomerApiTests.cs`**: `ListResolvesTheCityAndTheClassificationOfEachItem`,
  `GetResolvesTheCityDepartmentAndClassification` y los filtros por ciudad siguen verdes leyendo
  del contacto. Agregar un caso donde la principal esté en otra ciudad que el contacto y el listado
  muestre la del contacto.
- **`CustomerReportSummaryApiTests.cs`**: `SummaryGroupsByTheDepartmentOfThePrincipalAddress` pasa a
  `SummaryGroupsByTheDepartmentOfTheCustomerAddress`, con una principal en otro departamento para
  que la diferencia sea observable.
- **Cotizaciones**: un test existente que cree una cotización con envío "igual al cliente" sigue
  verde; agregar uno donde principal ≠ contacto y verificar que `QuotationResponse.customer.address`
  es el contacto y `addresses[0]` la principal.
- **Migración**: test de integración que inserta un cliente con dos direcciones (principal en
  ciudad B, otra en A) **antes** de migrar y verifica `customers.city_id = B` después. Si el harness
  no permite migrar a mitad de test, se documenta y se verifica a mano contra una base local con
  `dotnet ef database update` y un `SELECT`, con la evidencia en el ledger.
- **`ArchitectureTests`**: sin cambios esperados; correr igual.

## Orden de trabajo

1. Rama `feature/direccion-de-contacto-propia` desde `develop`. Baseline: `dotnet build`,
   `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests`, `ArchitectureTests`.
2. Dominio: `CustomerContactInfo.CityId`, `Customer.Address/CityId`, `Assign`. Tests unitarios RED → GREEN.
3. Infraestructura: configuración EF + migración + snapshot.
4. Aplicación: `CreateCustomer`, `UpdateCustomer`, `ImportCustomers`, `CustomerMapping`,
   `ListCustomers`, `ExportCustomers`. Integración RED → GREEN.
5. Bootstrapper: `QuotationCustomerLookup`, `CustomerReportSource`, seeders. Tests de cotización y
   reporte.
6. Comentarios que afirman "el domicilio es la principal": corregirlos en el mismo commit que el
   código que los invalida.
7. Suite completa + `ArchitectureTests`.

## Entrega

El frontend **no depende** de un cambio de forma del contrato: funciona contra el backend viejo y el
nuevo. Pero sólo con el backend nuevo deja de perderse la dirección al marcar principal y guardar.
Orden de deploy: backend primero (con migración), frontend después — es una compuerta de release,
no una preferencia. Si el frontend sale antes, deja de seguir a la principal en pantalla, y un
guardado contra este backend viejo todavía en pie espeja la calle/ciudad vieja del formulario sobre
la fila principal de la libreta: se pierde esa dirección, sin forma de recuperarla desde la UI. La
dirección inversa —backend nuevo con una pestaña del frontend viejo todavía abierta— es la benigna:
en el peor caso escribe una dirección de contacto vieja, recuperable editándola de nuevo.

## Fuera de alcance

- Cambiar qué dirección usa la cotización con el switch "igual al cliente" prendido: sigue siendo el
  domicilio del cliente (ahora, el contacto). La favorita sólo se preselecciona al elegir de la
  libreta.
- Permitir borrar la principal.
- Que la libreta y el contacto se puedan "vincular" (elegir una fila de la libreta como contacto).
- Tests unitarios de la libreta (`AddAddress`/`MakeAddressPrincipal`/`RemoveAddress`) que hoy no
  existen, salvo los que este cambio necesita.

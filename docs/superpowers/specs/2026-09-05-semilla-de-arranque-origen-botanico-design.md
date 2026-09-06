# Semilla de arranque para el ambiente desplegado (`api-qep.qcode.co`)

**Fecha:** 2026-09-05
**Estado:** propuesta, pendiente de aprobación

## Problema

`api-qep.qcode.co` es el ambiente que en algún momento se le entrega al cliente, pero durante
la fase de desarrollo se borra y se vuelve a crear la base. Cada vez que eso pasa hay que
dejarlo utilizable de nuevo: un tenant, una persona que pueda entrar, y el catálogo de
productos cargado.

Hacerlo con el script `ops/seed/Seed-CatalogProducts.ps1` no sirve ahí, por tres razones que
no aplican en local:

1. El script se autentica con los headers del stub de desarrollo, y el stub **no puede correr**
   fuera de `Development`: `AddAuthentication` aborta el arranque
   (`QepServiceCollectionExtensions.cs:745-750`). El ConfigMap fija
   `ASPNETCORE_ENVIRONMENT: "Production"`.
2. En el modo real los permisos **no se auto-declaran**: `ExternalClaimsTransformation` los
   resuelve desde los roles de la membresía activa (`ExternalClaimsTransformation.cs:112-124`).
   `X-Permissions` se ignora.
3. Con el stub apagado se registra la defensa CSRF (`Program.cs:64-66`), así que todo `POST` sin
   `X-Qep-Client: web` se va en 403.

## Hallazgos que fijan el diseño

Todo lo de esta sección está verificado contra el código, no supuesto.

- **En ese ambiente no hay tenant cuando la aplicación arranca.**
  `TenancyDatabaseInitializer` crea el tenant de demostración **sólo si**
  `environment.IsDevelopment()` (`TenancyDatabaseInitializer.cs:23-24`). Por eso una semilla
  que dependa de un tenant preexistente no tiene a quién sembrarle, y una que resuelva el tenant
  por slug o por id de configuración obliga a reiniciar el pod después de registrarlo a mano.
  **Que la semilla cree el tenant elimina el problema de orden por completo.**

- **Un usuario sembrado sólo con email sí se puede vincular a Google después.**
  `POST /auth/session` llama a `ProviderLinkingService.LinkAndActivateAsync`: si el `sub` de
  Google todavía no está vinculado y el email viene **verificado**, busca el usuario por email;
  si lo encuentra, vincula el proveedor y lo activa; si no, deniega con `invitation_required`.
  O sea que la semilla crea el usuario por email y el primer login lo vincula solo, sin
  invitación ni registro de tenant.

- **Ya existe el constructor de membresía que hace falta.**
  `Membership.CreateActive(id, userId, tenantId, roles, origin, createdAt)` crea la membresía
  directamente en `Active`. Es la excepción que el ADR 0017 abrió para el owner de un tenant
  auto-registrado, y la semilla es el mismo caso.

- **El rol `admin` ya trae los permisos de catálogo.** Incluye `catalog.product.manage` y
  `catalog.tax_rate.manage` (`QepServiceCollectionExtensions.cs:467-470`), así que no hay
  permiso nuevo que registrar ni política que agregar.

- **La membresía sembrada no dispara correos.** `CreateActive` emite
  `MembershipAcceptedDomainEvent`, que `OutboxWriter` publica como
  `tenancy.membership-accepted.v1` (`OutboxWriter.cs:18`). **Hoy no lo consume nadie.** Si
  mañana aparece un consumidor, la semilla pasaría a dispararlo en cada base nueva.

## Diseño

Cuatro semillas, **una por módulo**, orquestadas en `Program.cs` con la misma forma y en el
mismo lugar que la cadena de `Initialize*DatabaseAsync` que ya existe. Cada módulo siembra sólo
sus propias tablas, así que no se cruza ningún límite y `ArchitectureTests` no cambia.

El id del tenant es una **constante en código**, igual que
`TenancyDatabaseInitializer.DevelopmentTenantId`. Esa es la pieza que hace que Catalog nunca
tenga que preguntarle a Tenancy quién es el tenant, y que el id sobreviva a cualquier borrado.

| Orden | Módulo   | Qué siembra                                                   | Idempotente por      |
| ----- | -------- | ------------------------------------------------------------- | -------------------- |
| 1     | Tenancy  | Tenant `Origen botánico`, slug `origen-botanico`, id constante | id del tenant        |
| 2     | Identity | Usuario por email (`User.CreateInvited`), sin proveedor        | email                |
| 3     | Tenancy  | `Membership.CreateActive` con rol `admin`                      | par usuario + tenant |
| 4     | Catalog  | Tasa `IVA 19%` + los 19 productos                              | nombre / `code`      |

Los pasos 1 y 3 son del mismo módulo pero van separados: la membresía necesita el id de usuario
que produce el paso 2.

### Constantes

```csharp
// Modules.Tenancy.Infrastructure
public static readonly Guid SeedTenantId = Guid.Parse("01900000-0000-7000-8000-000000000003");
```

Se elige `...0003` para no chocar con `DevelopmentTenantId` (`...0001`) ni con el sujeto de
desarrollo que documenta el README (`...0002`).

### Configuración

| Clave                | Valor por defecto | Qué hace                                                  |
| -------------------- | ----------------- | --------------------------------------------------------- |
| `Seed__Enabled`      | `false`           | Interruptor único. Apagado, ninguna de las cuatro corre   |
| `Seed__OwnerEmail`   | sin valor         | Email que recibe la membresía `admin`                     |

`Seed__OwnerEmail` **no lleva valor por defecto y no se hardcodea**: dejar una persona fija en
un archivo versionado convierte al repositorio en la autoridad sobre quién administra un tenant,
que es una decisión del ambiente. Se declara en el ConfigMap, junto a las demás claves de
entorno. Para el ambiente actual: `andres.jaramillo@qcode.co`.

Si `Seed__Enabled` es `true` y `Seed__OwnerEmail` está vacío, **la aplicación no arranca**. Es el
mismo criterio que ya usan `StorageOptionsValidator` y compañía con `ValidateOnStart`, y el mismo
que la cadena de conexión: fallar con un mensaje que dice qué falta es mejor que sembrar un
tenant al que nadie puede entrar.

### Datos

El JSON de productos se muda de `ops/seed/catalog-products.json` a
`Modules.Catalog.Infrastructure/Seed/Data/catalog-products.json`, declarado como
`EmbeddedResource` en el `.csproj` — exactamente como los dos archivos de DIVIPOLA de
`Modules.Geography.Infrastructure`.

La semilla construye los agregados con `Product.Create` y `TaxRate.Create`, así que **todos los
invariantes del dominio siguen valiendo**. Lo único que se saltea respecto del script es la capa
HTTP: autorización, CSRF y validadores de FluentValidation. Los invariantes que importan —precio
en al menos una moneda, unicidad de código, anchos de campo— son del dominio y se siguen
aplicando.

## Seguridad

**Esto no es sólo cargar productos: crea un tenant y le da `admin` a un email.** Es un mecanismo
que otorga privilegios, y hay que tratarlo como tal.

El truco fail-closed del stub de auth —abortar el arranque si lo prenden fuera de
`Development`— acá **no se puede usar**: el ambiente objetivo es `Production`. La única defensa
es entonces:

1. `Seed__Enabled` por defecto en `false`. Sin la clave explícita en el ConfigMap, no pasa nada.
2. Un log de **advertencia** en cada arranque con la semilla activa, nombrando el tenant y el
   email que recibe `admin`. Que quede ruidoso es el punto: si el ambiente pasa al cliente con la
   clave prendida, tiene que verse en los logs del primer arranque y no seis meses después.
3. Idempotencia en las cuatro. Que quede prendida de más no duplica nada ni repite el otorgamiento.

**Al entregar el ambiente al cliente se borra la clave del ConfigMap.** Conviene que eso quede en
la lista de tareas de entrega, porque es un paso de configuración y no de código: nada en el
repositorio lo va a recordar.

## Qué NO hace

- **No borra ni actualiza nada.** Es idempotente por existencia: lo que ya está, se saltea. Cambiar
  el precio de un producto ya sembrado es un `PUT`, no una segunda corrida.
- **No toca el catálogo de otros tenants.** Sólo el de la constante.
- **No carga imágenes de producto.** `ImageFileId` queda en `null`; las imágenes son de Storage y
  se suben aparte.
- **No siembra pesos, unidad de empaque ni el id numérico de origen.** El dominio no tiene esos
  campos. Quedan en el JSON bajo `notSeeded`, como referencia.

## Pruebas

TDD, RED antes que GREEN, con evidencia de ambos.

| Nivel       | Qué verifica                                                                        |
| ----------- | ----------------------------------------------------------------------------------- |
| Unitaria    | El parser del JSON embebido: 19 productos, precios, tasa                            |
| Integración | Con `Seed__Enabled=false` no se crea nada                                           |
| Integración | Con la semilla activa: tenant, usuario, membresía `Active` con rol `admin`, 19 productos con la tasa ligada |
| Integración | **Correr la semilla dos veces deja exactamente el mismo estado** (la que importa)   |
| Integración | Con `Seed__Enabled=true` y `Seed__OwnerEmail` vacío, el arranque falla              |
| Arquitectura | `CatalogLayerTests` sigue en verde: el seeder vive en Infrastructure y no agrega referencias entre módulos |

Ojo con la trampa que ya está escrita en `CLAUDE.md`: las factories de integración tienen que
fijar `Notifications:EmailProvider`, o todas las pruebas del archivo mueren en el arranque antes
de su aserción.

## Qué pasa con el script de PowerShell

**Se elimina.** Decidido por el owner el 2026-09-05: la semilla de arranque lo reemplaza y no se
mantienen los dos.

El argumento para conservarlo era que cubría un caso que la de arranque no —cargar el catálogo
en un tenant arbitrario sin prender una clave ni reiniciar—, pero ese caso no se está usando, y
el costo de tenerlo es real: el JSON quedaría duplicado entre `ops/seed/` y el módulo, con dos
copias que se desincronizan en silencio. Para desarrollo local la semilla de arranque sirve
igual, prendiendo `Seed__Enabled`.

**El borrado va en el mismo cambio que implementa la semilla, no antes.** `ops/seed/` es hoy el
único mecanismo que existe y la única copia de los datos; eliminarlo mientras el reemplazo no
esté deja el repositorio sin ninguno de los dos. La secuencia es: mover
`ops/seed/catalog-products.json` a `Modules.Catalog.Infrastructure/Seed/Data/`, implementar los
cuatro seeders, verificar, y recién entonces borrar `ops/seed/Seed-CatalogProducts.ps1`.

## Decisiones pendientes

1. **El slug `origen-botanico`** — asumido por slugificación del nombre. Falta confirmarlo.
2. ~~La duplicación del JSON entre `ops/seed/` y el módulo.~~ **Resuelta el 2026-09-05:** el
   script se elimina y el JSON se muda al módulo. Ver "Qué pasa con el script de PowerShell".
3. **El precio USD y el IVA.** Decisión ya tomada el 2026-09-05: los 19 llevan `IVA 19%`, gana la
   lista COP. En este ambiente eso deja de ser dato de prueba: los 14 productos cuyo precio USD no
   contiene el 19% van a cotizar en USD con un impuesto extraído que no está en el precio. No
   bloquea la semilla, pero es la primera cosa que se va a ver rara en una cotización.

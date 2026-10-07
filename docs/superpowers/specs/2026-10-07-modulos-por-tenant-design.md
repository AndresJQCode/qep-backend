# Módulos por tenant (entitlements) con fuente local

**Fecha:** 2026-10-07
**Módulos:** Tenancy, Authorization, Storage, Quotations (orders-export-layout), Bootstrapper, Api
(backend); features `auth`, `roles`, `tenant-settings`, `components/`, `routes/` (frontend)
**Estado:** borrador para revisión del owner

## Problema

Hoy todo tenant tiene todo. No hay forma de venderle a una empresa sólo cotizaciones, ni de
dejarle el punto de venta a otra sin que vea pedidos. Lo que una persona puede hacer sale sólo de
los roles de su membresía: `ExternalClaimsTransformation` resuelve los permisos en cada request
(`src/Bootstrapper/Authentication/ExternalClaimsTransformation.cs:92-126`) llamando a
`AuthorizationService.ResolvePermissionsAsync`, que busca los roles de la membresía activa y los
traduce a permisos (`src/Modules/Authorization/Modules.Authorization.Application/AuthorizationService.cs:27-45`).
Nada en esa cadena sabe qué compró el tenant.

## Objetivo

Que QCode pueda prender y apagar por tenant cada módulo comercial (`catalog`, `customers`,
`companies`, `quotations`, `orders`, `reporting`, `pos`), con un núcleo que nunca se apaga, y que
apagar un módulo:

- corte el acceso en el request siguiente, en el backend, sin depender de la SPA;
- esconda el módulo en la SPA y explique por qué cuando alguien llega por URL;
- no borre datos ni interrumpa trabajos en vuelo.

La fuente es una tabla local de Tenancy, detrás de un puerto que el futuro control plane puede
reemplazar sin tocar el enforcement.

### Criterios de éxito

1. Con `quotations` apagado, `GET /api/v1/tenants/{id}/quotations` responde 403 para un admin, por
   cookie real y por el stub, y `/authorization/me` ya no lista `quotations.quotation.*`.
2. Todos los tenants que existen al correr la migración conservan los seis módulos de hoy. Los
   que un pod viejo cree durante el despliegue se detectan y se reparan con el SQL de
   «Operación».
3. Un tenant nuevo por signup nace con los seis módulos de hoy si
   `Entitlements:GrantDefaultModulesOnSignup` es `true` (el default), y sin ningún módulo si es
   `false`; el de la semilla, con los siete.
4. Una prueba falla si alguien agrega una constante de permiso sin decir a qué módulo pertenece, o
   un miembro de `FileOwnerType` sin decir a qué módulo pertenecen sus archivos.
5. La SPA no ofrece entradas de menú de un módulo apagado, quien llega por URL ve
   «Este módulo no está incluido en el plan de tu empresa.» (o el módulo que le falta), y ninguna
   pantalla de un módulo prendido dispara consultas a otro que está apagado.
6. Con `orders` apagado, `GET /files` no lista los comprobantes de pago y todo comando de
   Storage sobre uno por id —`download-url`, `complete`, `upload` (cancelar), `metadata`,
   `publication` (poner y quitar) y el borrado— responde 403 `tenancy.module_not_enabled`; lo
   mismo con `catalog` y las imágenes de producto.
7. Las suites de integración que usan tenants simulados por el stub (sin fila en
   `tenancy.tenants`) siguen verdes sin tocarlas. Las dos que insertan su propia fila de tenant y
   usan permisos de un módulo comercial (`CustomersApiHarness.SeedTenantAsync`,
   `ProductExportApiTests.SeedTenantAsync`) pasan a insertar también sus filas de módulos.

## Decisión

**El enforcement principal es enmascarar permisos.** Al resolver los permisos de una persona en
un tenant, se descarta cada permiso cuyo módulo no está efectivamente habilitado. Como cada
endpoint exige su propio permiso con `RequireClaim` (`QepServiceCollectionExtensions.cs:1173-1179`)
y cada handler lo revalida por claims (`HttpExecutionContext.cs:23`, p. ej.
`QuotationsAuthorization.cs:11-21`), un permiso enmascarado es un 403 en las dos capas. Y como el
sidebar y los gates de la SPA ya filtran por permisos (`sidebar-nav-items.ts:216-230`,
`quotes-read-gate.tsx:19-43`), la UI se esconde sola.

Dos lugares exigen un chequeo explícito además del enmascarado, porque su permiso es de núcleo
pero lo que exponen es de un módulo: la configuración del Excel de pedidos y los archivos de
Storage según su dueño (ver más abajo).

### Alternativas descartadas

| Alternativa | Por qué no |
| --- | --- |
| Filtro `RequireModule("orders")` por grupo de rutas (lo del handoff) | Ningún grupo tiene autorización a nivel de grupo hoy: cada endpoint declara su permiso (`Program.cs:122-141`, `OrdersExportLayoutEndpoints.cs:21-37`). Habría que tocar los nueve `Map*Endpoints` de negocio (`Program.cs:131-140`) y además los handlers, porque la revalidación vive ahí. El enmascarado es un solo punto de paso y da la UI gratis. **Costo aceptado:** el 403 es el genérico de permiso, sin un `code` propio — la SPA distingue «no está en el plan» leyendo el endpoint de módulos, no el error. |
| Chequear el módulo en los workers en segundo plano | Ningún worker arranca negocio por su cuenta: los jobs se autorizaron al encolarse (`ExportJobWorker`, `QuotationsInfrastructureExtensions.cs:46`) y `QuotationExpirationWorker` (`:69`) es mantenimiento de estado. Apagar un módulo no borra datos ni corta lo que ya está en vuelo. |
| `IMemoryCache` de módulos por tenant | Los permisos ya se resuelven contra la base en cada request (`ITenantRoleCatalog.cs:25-28`). Sumar una lectura de a lo sumo siete filas por PK no justifica invalidación de caché; el cache es asunto del adaptador del control plane. |
| Columna `enabled` en la fila en vez de borrarla | Dos formas de decir «apagado» (fila ausente y fila en falso). La presencia de la fila **es** el entitlement. |
| Mapa permiso→módulo como singletons aparte (`PermissionModuleRequirement`) | Se puede olvidar uno y sólo lo atrapa una prueba. Como parámetro posicional obligatorio de `PermissionDefinition`, el compilador obliga a declararlo en el mismo lugar donde nace el permiso. |
| Volver de núcleo a módulo los permisos `storage.file.*` | Los usan el logo del tenant (núcleo) y los comprobantes (pedidos) a la vez: el permiso no sabe de quién es el archivo. Lo sabe `FileOwnerType`, y ahí va el chequeo. |

## Catálogo de módulos

Lista cerrada en código: `TenantModuleKeys` (Tenancy.Domain), en este orden, que es el de la
respuesta del endpoint y el del `CHECK`. **El orden es topológico**: cada clave aparece después de
todas sus dependencias, y el algoritmo de abajo depende de eso.

| Clave | Depende de | Qué protege (rutas) |
| --- | --- | --- |
| `catalog` | — | `/tenants/{id}/catalog` (`ProductEndpoints.cs:14`, `TaxRateEndpoints.cs:17`); archivos `Product` |
| `customers` | — | `/tenants/{id}/customers` y clasificaciones (`CustomerEndpoints.cs:30`, `ClientClassificationEndpoints.cs:18`) |
| `companies` | — | `/tenants/{id}/companies` (`CompanyEndpoints.cs:17`) |
| `quotations` | `catalog`, `customers`, `companies` | `/tenants/{id}/quotations` (`QuotationEndpoints.cs:13-15`) |
| `orders` | `quotations` | `/tenants/{id}/orders`, `/quotations/{qid}/order` (`OrderEndpoints.cs:24-26,148-150`), `/orders-export-layout`; archivos `PaymentProof` |
| `reporting` | — | `/tenants/{id}/reports` (`ReportingEndpoints.cs:16-67`); cada reporte exige además su fuente (mapa abajo) |
| `pos` | `catalog`, `companies` | Lo define el spec de POS |

**Núcleo, nunca se apaga:** identidad y sesión (`/auth/*`), Tenancy (settings, membresías,
invitaciones), Authorization (roles, catálogo, `/authorization/me`), Storage (`/files`, salvo los
archivos cuyo dueño es de un módulo, ver «Archivos de Storage»), Platform (log de requests),
Geography (`/api/v1/departments`, `/api/v1/cities`), Audit y Notifications.

**Módulos efectivos (fail closed).** Un módulo guardado cuya dependencia no está efectivamente
habilitada cuenta como apagado, y eso se propaga: sin `customers`, `quotations` cae, y con él
`orders`. Algoritmo, en `TenantModuleSet.FromStored` (Tenancy.Domain), en una sola pasada y sin
modificar la colección que recorre. Recibe claves ya tipadas: la conversión de texto a clave la
hace el mapeo de EF con `TenantModuleKey.Parse` (ver «Tabla y migración»).

```text
stored    = set(claves)                                    // IEnumerable<TenantModuleKey>
effective = {}
for key in TenantModuleKeys.All:                          // orden topológico
    if key ∈ stored and DependenciesOf(key) ⊆ effective:
        effective.add(key)

Closure(key) = DependenciesOf(key) ∪ ⋃ Closure(d) para d ∈ DependenciesOf(key)

missingDependencies(key) =                                // causas raíz
    key ∈ stored and key ∉ effective
        ? [ d ∈ Closure(key) : d ∉ stored ]  en el orden de All
        : []
```

`missingDependencies` nombra la **causa raíz**, no la dependencia directa: con todo contratado
salvo `customers`, `orders` reporta `["customers"]` y no `["quotations"]`, porque contratar
`quotations` no lo arreglaría. Una clave contratada y no efectiva siempre tiene al menos una
causa raíz: si una dependencia no es efectiva, o no está guardada o tiene a su vez una
dependencia no efectiva, y la cadena termina en `All`.

## Mapa permiso → módulo

Cada permiso declara los módulos que exige (conjunción). `[]` es núcleo. Como los efectivos ya
están cerrados por dependencias, basta con el módulo directo: `quotations.order.read` exige
`orders`, y `orders` efectivo implica `quotations`, `catalog`, `customers` y `companies`.

Las 35 constantes que existen hoy (`grep "public const string" src/**/*Permissions.cs`), todas
registradas como `PermissionDefinition` en `QepServiceCollectionExtensions.cs:711-935` y todas
concedidas a `admin` (`:582-636`):

| Permiso | Constante | Exige |
| --- | --- | --- |
| `tenancy.settings.read` | `TenancyPermissions.SettingsRead` | `[]` |
| `tenancy.settings.update` | `TenancyPermissions.SettingsUpdate` | `[]` |
| `advisorship.invite` | `TenancyPermissions.AdvisorshipInvite` | `[]` |
| `advisorship.read` | `TenancyPermissions.AdvisorshipRead` | `[]` |
| `advisorship.manage` | `TenancyPermissions.AdvisorshipManage` | `[]` |
| `advisorship.roles.manage` | `TenancyPermissions.AdvisorshipRolesManage` | `[]` |
| `storage.file.upload` | `StoragePermissions.FileUpload` | `[]` |
| `storage.file.read` | `StoragePermissions.FileRead` | `[]` (y el dueño del archivo, ver «Archivos de Storage») |
| `storage.file.delete` | `StoragePermissions.FileDelete` | `[]` |
| `storage.file.publish` | `StoragePermissions.FilePublish` | `[]` |
| `platform.request_log.read` | `PlatformPermissions.RequestLogRead` | `[]` |
| `platform.request_log.purge` | `PlatformPermissions.RequestLogPurge` | `[]` |
| `catalog.product.read` | `CatalogPermissions.ProductRead` | `catalog` |
| `catalog.product.manage` | `CatalogPermissions.ProductManage` | `catalog` |
| `catalog.tax_rate.read` | `CatalogPermissions.TaxRateRead` | `catalog` |
| `catalog.tax_rate.manage` | `CatalogPermissions.TaxRateManage` | `catalog` |
| `companies.company.read` | `CompaniesPermissions.CompanyRead` | `companies` |
| `companies.company.manage` | `CompaniesPermissions.CompanyManage` | `companies` |
| `customers.customer.read` | `CustomersPermissions.CustomerRead` | `customers` |
| `customers.customer.manage` | `CustomersPermissions.CustomerManage` | `customers` |
| `customers.customer.import` | `CustomersPermissions.CustomerImport` | `customers` |
| `customers.classification.read` | `CustomersPermissions.ClassificationRead` | `customers` |
| `customers.classification.manage` | `CustomersPermissions.ClassificationManage` | `customers` |
| `quotations.quotation.read` | `QuotationsPermissions.QuotationRead` | `quotations` |
| `quotations.quotation.manage` | `QuotationsPermissions.QuotationManage` | `quotations` |
| `quotations.order.read` | `OrdersPermissions.OrderRead` | `orders` |
| `quotations.order.manage` | `OrdersPermissions.OrderManage` | `orders` |
| `quotations.order.approve` | `OrdersPermissions.OrderApprove` | `orders` |
| `quotations.order.cancel` | `OrdersPermissions.OrderCancel` | `orders` |
| `quotations.order.invoice` | `OrdersPermissions.OrderInvoice` | `orders` |
| `reporting.orders.read` | `ReportingPermissions.OrdersRead` | `reporting`, `orders` |
| `reporting.quotation.read` | `ReportingPermissions.QuotationRead` | `reporting`, `quotations` |
| `reporting.price_change.read` | `ReportingPermissions.PriceChangeRead` | `reporting`, `catalog` |
| `reporting.customer.read` | `ReportingPermissions.CustomerRead` | `reporting`, `customers` |
| `reporting.all_advisors.read` | `ReportingPermissions.AllAdvisorsRead` | `reporting` |

`reporting.all_advisors.read` exige sólo `reporting`: no abre ningún reporte por sí solo
(`QepServiceCollectionExtensions.cs:913-921`), amplía los de pedidos y cotizaciones, que ya están
enmascarados por su fuente.

**Lugares reservados para POS** (los declara el spec de POS, no este): `pos.sale.read`,
`pos.sale.create`, `pos.sale.void`, `pos.register.operate`, `pos.register.read` → `pos`. La prueba
de completitud de este spec es la que obliga a ese spec a mapearlos.

## Contratos — backend

### `PermissionDefinition` gana `RequiredModules`

`RoleCatalog.cs:19-24` pasa a:

```csharp
public sealed record PermissionDefinition(
    string Permission, string DisplayName, string Description, string Category, string RiskLevel,
    IReadOnlyCollection<TenantModuleKey>? RequiredModules);
```

- Posicional y sin default: cada `new PermissionDefinition(...)` del composition root
  (`:711-935`) y de las pruebas (4 usos fuera de él) tiene que decirlo.
- Tipado: `TenantModuleKey` sólo se obtiene de `TenantModuleKeys`, así que una clave inventada no
  compila. Authorization.Application ya referencia Tenancy.Application
  (`Modules.Authorization.Application.csproj:4`) y por ella Tenancy.Domain: no hay referencia
  nueva.
- `null` significa **sin mapear**, y sólo lo produce el fallback de `RoleCatalog.cs:74-80` (un
  permiso de rol sin metadata). Un permiso sin mapear se enmascara siempre.
- `ComputeCatalogVersion` (`RoleCatalog.cs:95-125`) **no** lo incluye: la SPA no usa
  `catalogVersion` (sólo lo tipa, `tenancy.api.ts:24`) y el hash sigue describiendo el catálogo
  del build, no el de un tenant.

### Dominio (Tenancy.Domain)

- `TenantModuleKey`: `sealed record` con `Value` (`string`) y constructor privado; sus únicas
  instancias son las de `TenantModuleKeys`. `Parse(string)` devuelve la instancia de `All` con
  ese `Value` y lanza `ArgumentException` con cualquier otro texto. Su único llamador es la
  conversión de EF: la configuración no lleva claves de módulo, así que la base es el único
  lugar donde una llega como texto. No hay `TryParse`: nadie necesita ignorar una clave
  desconocida.
- `TenantModuleKeys`: `Catalog` … `Pos` (de tipo `TenantModuleKey`); `All`
  (`IReadOnlyList<TenantModuleKey>`, orden topológico de la tabla de arriba);
  `DependenciesOf(TenantModuleKey)`; `DefaultForNewTenants` = los seis sin `pos`.
- `TenantModuleSet`: `FromStored(IEnumerable<TenantModuleKey>)`, `Stored`, `Effective`,
  `IsEnabled(TenantModuleKey)`, `IsContracted(TenantModuleKey)`,
  `MissingDependencies(TenantModuleKey)` (causas raíz), `Empty`.
- Entidad `TenantModule` (`TenantId` de tipo `TenantId`, `ModuleKey`, `EnabledAt`, `Source`,
  `Note`) con `Create(TenantId, TenantModuleKey, string source, DateTimeOffset, string? note)`.
  La clave no puede ser desconocida porque llega tipada. `Source` **no** se valida en el dominio:
  el código sólo escribe `signup` y `seed` desde constantes (`TenantModuleSources`), `backfill` y
  `manual` sólo los escribe SQL, y el `CHECK` de la tabla es la única validación.

### Puertos (Tenancy.Application)

```csharp
public interface ITenantModules            // el puerto que reemplaza el control plane
{
    /// null = el tenant no existe en tenancy.tenants (sólo pasa con el stub de desarrollo).
    Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken);
}

public interface ITenantModuleRepository { void Add(TenantModule module); }   // se commitea con ITenancyUnitOfWork

public interface ITenantModuleDefaults { IReadOnlyCollection<TenantModuleKey> ForNewTenants { get; } }
```

`TenantModuleGuard.EnsureEnabledAsync(ITenantModules modules, Guid tenantId, TenantModuleKey key,
CancellationToken cancellationToken)` lanza
`RequestForbiddenException("tenancy.module_not_enabled", ...)` si el módulo no es efectivo. La
clave es tipada: nadie puede pasar `"order"` por error. Con `FindAsync` en `null` no lanza
(tenant simulado por el stub).

Adaptadores en Tenancy.Infrastructure, registrados en `AddTenancyInfrastructure`
(`TenancyInfrastructureExtensions.cs:14-53`): `TenantModules` (una sola consulta: el tenant y sus
claves, `AsNoTracking`; las claves llegan tipadas por la conversión de EF y van directo a
`FromStored`, sin mapear texto en memoria), `TenantModuleRepository` y `TenantModuleDefaults`
(lee `IOptions<EntitlementsOptions>`). Ninguna capa Application del repo usa `IOptions` hoy, por eso
las opciones viven en Infrastructure.

### Tabla y migración

`tenancy.tenant_modules`:

```sql
CREATE TABLE tenancy.tenant_modules (
    tenant_id   uuid                     NOT NULL,
    module_key  character varying(32)    NOT NULL,
    enabled_at  timestamp with time zone NOT NULL,
    source      character varying(16)    NOT NULL,
    note        character varying(300)   NULL,
    CONSTRAINT "PK_tenant_modules" PRIMARY KEY (tenant_id, module_key),
    CONSTRAINT "FK_tenant_modules_tenants_tenant_id" FOREIGN KEY (tenant_id)
        REFERENCES tenancy.tenants (id) ON DELETE CASCADE,
    CONSTRAINT "CK_tenant_modules_module_key" CHECK (module_key IN
        ('catalog','customers','companies','quotations','orders','reporting','pos')),
    CONSTRAINT "CK_tenant_modules_source" CHECK (source IN ('backfill','signup','seed','manual'))
);
```

**Mapeo EF, y por qué la FK va en el modelo.** `TenancyDbContext` no declara hoy ninguna
relación (`TenancyDbContext.cs:37-145`; la de `owner_membership_id` se omite a propósito,
`:72-75`). Si la FK de `tenant_modules` existiera sólo en SQL, EF no sabría que la fila depende
del tenant, ordenaría los `INSERT` del mismo `SaveChangesAsync` sin esa restricción y
`tenant_modules` podría ir antes que `tenants`: `23503` en `TenantRegistrationService` y en
`TenancySeeder`. Por eso `ConfigureTenantModule`, junto a `ConfigureTenant`:

```csharp
var module = modelBuilder.Entity<TenantModule>();
module.ToTable("tenant_modules", "tenancy", table =>
{
    table.HasCheckConstraint("CK_tenant_modules_module_key",
        "module_key IN ('catalog','customers','companies','quotations','orders','reporting','pos')");
    table.HasCheckConstraint("CK_tenant_modules_source",
        "source IN ('backfill','signup','seed','manual')");
});
module.HasKey(value => new { value.TenantId, value.ModuleKey });
module.Property(value => value.TenantId)
    .HasColumnName("tenant_id")
    .HasConversion(id => id.Value, value => new TenantId(value));   // misma conversión que memberships (:100-102)
module.Property(value => value.ModuleKey)
    .HasColumnName("module_key").HasMaxLength(32)
    .HasConversion(key => key.Value, value => TenantModuleKey.Parse(value));
// enabled_at, source (16), note (300)
module.HasOne<Tenant>().WithMany()
    .HasForeignKey(value => value.TenantId)
    .OnDelete(DeleteBehavior.Cascade);
```

Sin navegación en `Tenant`: el agregado no carga sus módulos, y la relación existe sólo para que
EF ordene los `INSERT` y genere la FK. No cierra ningún ciclo: `tenants` no apunta a
`tenant_modules`. La prueba de integración del signup (ver «Pruebas») es la que demuestra el
orden.

Migración `AddTenantModules` en
`src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/` (la última hoy es
`20260924152521_AddMembershipAdvisorCode`), **generada** con el factory de diseño —no escrita a
mano—, para que el snapshot quede igual al modelo:

```powershell
dotnet ef migrations add AddTenantModules --project src/Modules/Tenancy/Modules.Tenancy.Infrastructure --context TenancyDbContext -o Persistence/Migrations
```

Al `Up` generado se le agrega a mano, después del `CreateTable`, el backfill con
`migrationBuilder.Sql(...)` en la **misma migración** (misma transacción), igual que
`AddTenantOwnerMembership` (`20260921161628_AddTenantOwnerMembership.cs:26-37`):

```sql
INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
SELECT t.id, m.key, now(), 'backfill'
FROM tenancy.tenants t
CROSS JOIN (VALUES ('catalog'),('customers'),('companies'),('quotations'),('orders'),('reporting')) AS m(key);
```

`InitializeTenancyDatabaseAsync` corre antes de `app.RunAsync()` (`Program.cs:143`, `:185`), así
que un pod nuevo no atiende sin la tabla llena. **Sí hay ventana durante el rolling update**: un
pod viejo sigue atendiendo `register-tenant` después de la migración y crea tenants sin filas,
que el pod nuevo trata como tenants sin módulos (sólo núcleo). Se detectan y se reparan con el
SQL de «Operación», que va en el checklist del despliegue. El `Down` hace `DROP TABLE`. Un
módulo nuevo en el futuro es otra migración que cambia el `CHECK`, a propósito.

### Alta por signup

`register-tenant` lo puede llamar cualquiera con un token de Google y correo verificado, sin
sesión previa (`RegistrationEndpoints.cs:33-34,81-87`), siempre que
`Registration:PublicTenantSignupEnabled` esté prendido (`:20,59-64`). Darle módulos comerciales a
ese tenant es una decisión de negocio, y por eso se configura aparte.

`TenantRegistrationService.RegisterOwnerTenantAsync` (`TenantRegistrationService.cs:19-70`) recibe
`ITenantModuleRepository` e `ITenantModuleDefaults` y, después de `tenantRepository.Add(tenant)`,
agrega una fila por clave de `ForNewTenants` con `source = 'signup'`. El `SaveChangesAsync` de la
línea 68 las commitea con el tenant y la membresía: un tenant nunca existe sin las filas que le
tocan.

`EntitlementsOptions` (`SectionName = "Entitlements"`) tiene una sola propiedad:

| Propiedad | Tipo | Default | Qué hace |
| --- | --- | --- | --- |
| `GrantDefaultModulesOnSignup` | `bool` | `true` | `true` ⇒ el signup agrega `TenantModuleKeys.DefaultForNewTenants` (los seis sin `pos`); `false` ⇒ no agrega ninguna fila (sólo núcleo). |

No hay lista configurable de módulos para el signup (YAGNI): hoy nadie necesita otro paquete que
«los seis» o «ninguno», y el paquete por tenant es asunto del control plane. Si algún día hace
falta, se agrega con su propio validador.

`TenantModuleDefaults.ForNewTenants`: `DefaultForNewTenants` con el interruptor en `true`, `[]`
con el interruptor en `false`.

Sin validador: un `bool` no tiene valores inválidos que validar. Se registra en
`AddTenancyInfrastructure` con `ValidateOnStart`, como Notifications
(`NotificationsInfrastructureExtensions.cs:29-33`), para que el binding corra al arrancar y un
valor que no es booleano (`"si"`) tumbe el arranque en vez del primer signup:

```csharp
services.AddOptions<EntitlementsOptions>()
    .Bind(configuration.GetSection(EntitlementsOptions.SectionName))
    .ValidateOnStart();
```

La clave va en `appsettings.example.json`; `ConfigurationExampleTests` lo exige
(`ConfigurationExampleTests.cs:31-48`). El default `true` conserva el comportamiento de hoy;
cuando exista el cobro, lo esperable es que QCode lo ponga en `false` y prenda los módulos al
pagar.

### Semilla

`TenancySeeder.SeedTenantWithOwnerAsync` (la sobrecarga general, `TenancySeeder.cs:57-99`) agrega
las siete claves con `source = 'seed'` en el mismo `SaveChangesAsync` que el tenant. Alcanza al
tenant de la semilla (`...0003`, `origen-botanico`) y al de la carga de exportación
(`ExportLoadSeeder.cs:64`). **Sólo al crear:** el seeder devuelve antes si el tenant ya existe
(`:70-75`), así que una base local que ya tenía `origen-botanico` queda con los seis del backfill
y se le prende `pos` con el SQL de «Operación». Mismo criterio que
`SeedDoesNotOverwriteAnOrderFormatSetByHand`: la semilla no pisa lo que alguien cambió a mano.

### Dónde se enchufa el enmascarado

`ModuleEntitlementMask` (Authorization.Application, puro, singleton). Indexa las
`PermissionDefinition` **registradas** (`IEnumerable<PermissionDefinition>` del contenedor,
`QepServiceCollectionExtensions.cs:711-935`), no `IRoleCatalog.ListPermissions()`: esa lista sólo
trae los permisos que concede algún rol de sistema (`RoleCatalog.cs:71-82`), y un permiso
registrado que hoy no está en ningún rol de sistema —y que puede llegar por un rol custom o por
`X-Permissions`— quedaría sin índice y se enmascararía siempre.

```text
ModuleEntitlementMask(registered: IEnumerable<PermissionDefinition>):
    index = registered indexado por Permission → RequiredModules      // una vez, al construir

Apply(permissions, modules: TenantModuleSet):
    return permissions
        .Where(p => index.TryGet(p, out req) && req is not null
                    && req.All(modules.IsEnabled))
        .Distinct(Ordinal)
```

Un permiso que no está registrado no está en el índice y se enmascara.

| Camino | Cambio |
| --- | --- |
| Real (cookie) | `AuthorizationService` (`AuthorizationService.cs:5-46`) recibe `ITenantModules` y `ModuleEntitlementMask`. Después de `PermissionsForAsync` (`:44`): `modules = await FindAsync(tenantId) ?? TenantModuleSet.Empty` y devuelve `Apply(...)`. `AuthorizeAsync` (`:10-25`) hereda el enmascarado. `null` no debería ocurrir (hay membresía activa, luego hay tenant); si ocurre, fail closed: sólo núcleo. |
| Stub de desarrollo | `DevelopmentAuthenticationHandler` (`DevelopmentAuthenticationHandler.cs:18-79`) arma los claims de `X-Permissions` o los cinco de tenancy por defecto, y `ExternalClaimsTransformation` no lo toca (`ExternalClaimsTransformation.cs:31-36`). Pasa a ser `async`, recibe `ITenantModules` y `ModuleEntitlementMask` (los handlers se resuelven por request, así que admiten scoped) y aplica `Apply` **si `FindAsync` no es `null`**. Con `null` deja los permisos como vienen. |
| `/authorization/me` | Sin cambio de código: lee los claims ya enmascarados (`AuthorizationCatalogEndpoints.cs:70-75`). |

**Cuándo el stub enmascara.** El criterio es si el tenant tiene fila en `tenancy.tenants`, no
cómo se creó:

- **Sin fila** (tenant simulado): no enmascara. Las suites de Catalog, Customers y Companies
  trabajan casi todas contra tenants que nunca existen en la base (`ProductApiTests.cs:12`,
  `CustomersApiHarness.cs:23`, `CompaniesApiHarness.cs:20`); fail closed ahí tumbaría sus
  pruebas por una razón que no prueban. El stub ya es identidad autodeclarada y no arranca fuera
  de `Development` (`QepServiceCollectionExtensions.cs:950-956`).
- **Con fila creada por `register-tenant`** (Quotations, Reporting, `CustomerWriteApiTests`,
  `CompanyWriteApiTests`, entre otras): enmascara, y el tenant nace con los seis del signup, así
  que esas suites no cambian. Ahí viven las pruebas de este spec.
- **Con fila insertada a mano por la prueba**: enmascara, y el tenant no tiene ninguna fila de
  módulos. Las pruebas que hacen esto están en «Pruebas → Pruebas que insertan tenants
  directamente»; las dos que usan permisos de un módulo comercial tienen que sembrar sus módulos.
- **Sin `X-Tenant-Id`** (p. ej. `/auth/session` o `/auth/me` llamados sin ese header): no hay
  consulta ni enmascarado. Verificado en el código: `HandleAuthenticateAsync` devuelve
  `AuthenticateResult.Fail` cuando `X-Subject-Id` o `X-Tenant-Id` no son un `Guid`
  (`DevelopmentAuthenticationHandler.cs:20-26`), antes de armar ningún claim; el request no
  autentica y el endpoint responde 401 como hoy. La llamada a `FindAsync` va **después** de ese
  guard y de `ResolvePermissions` (`:33-36`), con el `Guid` ya parseado, así que nunca corre sin
  tenant. Las pruebas de sesión del stub mandan un `X-Tenant-Id` de relleno
  (`AuthSessionApiTests.cs:479-482`): ese tenant no tiene fila y cae en el primer caso.

### Catálogo de roles filtrado

`GetCatalogAsync` (`AuthorizationCatalogEndpoints.cs:80-111`) hoy es síncrono y devuelve
`IResult`. Pasa a `async Task<IResult>`, recibe `ITenantModules`, `ModuleEntitlementMask` y
`CancellationToken`, y con `FindAsync` no nulo saca de `permissions` y de cada
`roles[].permissions` lo que `Apply` descartaría. Conserva el chequeo de tenant de `:85-90`
antes de consultar. Costo: una consulta, en un endpoint que sólo abre el editor de roles.

**No se filtran** `GET /authorization/roles` ni `EnsureKnownPermissions`
(`RoleCommands.cs:63-79`): son lo guardado. El editor inicializa lo marcado desde
`role.permissions` (`roles-page.tsx:294`) y sólo pinta checkboxes del catálogo (`:399-433`), así
que un permiso de un módulo apagado queda en el rol al guardar sin que nadie lo vea. Si el módulo
vuelve, el rol vuelve a conceder lo que concedía. La SPA tampoco lo cuenta (ver frontend).

### Endpoint de capacidades

`GET /api/v1/tenants/{tenantId:guid}/modules`, en `src/Api/TenantModulesEndpoints.cs`, mapeado en
`Program.cs` junto a `MapAuthorizationCatalogEndpoints` (`:125`). `.RequireAuthorization()` sin
permiso, igual que `/authorization/me` y por la misma razón (`AuthorizationCatalogEndpoints.cs:25-37`):
cualquier miembro activo lo necesita para dibujar su pantalla. Lee el claim de tenant directo —no
`IExecutionContext.TenantId`, que tira 500 sin claim (`HttpExecutionContext.cs:21`)— y responde
403 `authorization.denied` si no coincide con la ruta.

Ejemplo con todo contratado salvo `customers` y `pos`:

```json
{
  "tenantId": "01900000-0000-7000-8000-000000000003",
  "modules": [
    { "key": "catalog",    "enabled": true,  "contracted": true,  "missingDependencies": [] },
    { "key": "customers",  "enabled": false, "contracted": false, "missingDependencies": [] },
    { "key": "companies",  "enabled": true,  "contracted": true,  "missingDependencies": [] },
    { "key": "quotations", "enabled": false, "contracted": true,  "missingDependencies": ["customers"] },
    { "key": "orders",     "enabled": false, "contracted": true,  "missingDependencies": ["customers"] },
    { "key": "reporting",  "enabled": true,  "contracted": true,  "missingDependencies": [] },
    { "key": "pos",        "enabled": false, "contracted": false, "missingDependencies": [] }
  ]
}
```

Regla BFF, escrita en el DTO (`TenantModulesResponse`, `TenantModuleResponse`):

- **Siempre las siete, en el orden de `All`, aunque estén apagadas.** Si faltara una, la SPA
  tendría que conocer la lista del backend para dibujar la que no vino.
- `enabled` es el efectivo; `contracted`, que la fila existe. Sin los dos la pantalla no puede
  distinguir «no está en tu plan» de «está, pero le falta otro».
- `missingDependencies` son las **causas raíz** —las dependencias transitivas con
  `contracted: false`—, sólo con `contracted: true` y `enabled: false`. Así la pantalla nombra
  lo que hay que contratar, no un intermediario que ya está contratado. Viaja por clave: las
  etiquetas las tiene la SPA.
- Con `FindAsync` en `null`, la respuesta depende del esquema que autenticó el request:
  - **stub de desarrollo** (tenant simulado): los siete `enabled: true`, `contracted: true`, que
    es lo mismo que el stub hace con los permisos;
  - **cualquier otro esquema** (la cookie real): los siete `enabled: false`,
    `contracted: false`, `missingDependencies: []`. Es el mismo fail closed de
    `AuthorizationService` con `null`: la pantalla y los permisos dicen lo mismo.

  El esquema se lee del principal, igual que `ExternalClaimsTransformation.cs:31-33`
  (`identity.AuthenticationType == DevelopmentAuthenticationHandler.AuthenticationSchemeName`).
  Como `DevelopmentAuthenticationHandler` es `internal` de Bootstrapper y el endpoint vive en
  `src/Api`, `QepAuthenticationMode` (público, `QepAuthenticationMode.cs:9`) gana
  `IsDevelopmentStub(ClaimsPrincipal)` con esa comparación. La decisión vive en un método puro,
  `TenantModulesResponse.From(Guid tenantId, TenantModuleSet? modules, bool isDevelopmentStub)`,
  para probar los dos casos sin HTTP: por cookie real, `null` exige un tenant sin fila con una
  membresía activa, que no se puede armar por la API.

### Configuración de pedidos: chequeo explícito

`/orders-export-layout` exige `tenancy.settings.*`, que es núcleo, así que el enmascarado no lo
cubre. `GetOrdersExportLayoutHandler` (`GetOrdersExportLayout.cs:19-28`) y
`UpdateOrdersExportLayoutHandler` (`UpdateOrdersExportLayout.cs:122-129`) llaman a
`TenantModuleGuard.EnsureEnabledAsync(tenantModules, tenantId, TenantModuleKeys.Orders, ...)`
justo después de `QuotationsAuthorization.EnsureAuthorized`, antes del validador. 403
`tenancy.module_not_enabled` por `ApiExceptionHandler.cs:135`. Quotations.Application ya
referencia Tenancy.Application (`Modules.Quotations.Application.csproj:11`), así que no hay
referencia nueva. Es el patrón que va a usar la configuración de WhatsApp por tenant.

### Archivos de Storage: el dueño decide el módulo

`storage.file.*` es núcleo, pero `GET /files` lista todos los archivos del tenant sin mirar el
dueño (`ListFiles.cs:28-43`), `POST /files/{id}/download-url` firma cualquier archivo disponible
del tenant (`IssueDownloadUrl.cs:29-56`), y los comandos por id también leen: `complete`,
`metadata` y `publication` (poner y quitar) responden `FileResourceResponse`
(`StorageEndpoints.cs:29-34,43-67`), que trae `PublicUrl` y la de cada variante
(`StorageDtos.cs:56,64,70-80`). Sin un chequeo propio, apagar `orders` dejaría leer y modificar
los comprobantes de pago, y apagar `catalog`, las imágenes de producto.

El único productor de `FileResource` es `CreateUploadSession` (`CreateUploadSession.cs:35`, la
única llamada a `FileResource.CreatePendingUpload`), con el `ownerType` que manda el cliente
(`StorageEndpoints.cs:90-97`). Los PDF y Excel de cotizaciones, pedidos, clientes y productos no
son `FileResource`, así que no tienen tipo de dueño ni pasan por `/files`.

Mapa `FileOwnerModules` (Storage.Application), uno por miembro de `FileOwnerType`
(`FileResourceEnums.cs:19-38`):

| Miembro | Quién lo produce hoy | Módulo |
| --- | --- | --- |
| `User = 1` | Ninguna pantalla actual lo manda. Los comprobantes subidos antes de v2 siguieron siendo `User` (D13, `FileResourceEnums.cs:29-31`) | núcleo |
| `Entity = 2` | Nadie: ni la SPA ni el backend lo crean | núcleo |
| `System = 3` | Nadie: ni la SPA ni el backend lo crean | núcleo |
| `Product = 4` | Imágenes de producto (`product-image.api.ts:102`) | `catalog` |
| `PaymentProof = 5` | Comprobantes de pago (`quote-file-upload.ts:74`), que se adjuntan a un pedido (`AddOrderPaymentProofs`, conversión a pedido) | `orders` |
| `Tenant = 6` | Logo del tenant (`tenant-logo-upload.ts:67`, `TenantLogoStorage.cs:78`) | núcleo |

`IReadOnlyDictionary<FileOwnerType, TenantModuleKey?>`; una prueba exige que sus claves sean
exactamente `Enum.GetValues<FileOwnerType>()`, así que un miembro nuevo sin mapear no compila
verde. Storage.Application ya referencia Tenancy.Application
(`Modules.Storage.Application.csproj:5`): no hay referencia nueva.

- **`ListFilesHandler`**: después de `StorageAuthorization.EnsureAuthorized` (`ListFiles.cs:28-29`),
  con `FindAsync` no nulo calcula los tipos de dueño cuyo módulo no es efectivo y los pasa a
  `IFileResourceRepository.SearchAsync` como `excludedOwnerTypes`. El filtro va en SQL y no en
  memoria, para que `totalCount` y la paginación sigan siendo ciertos. Un filtro explícito
  `ownerType=Product` con `catalog` apagado devuelve una página vacía, igual que el listado sin
  filtro.
- **Todo handler que carga un `FileResource` por id** llama a
  `FileOwnerModuleGuard.EnsureOwnerModuleEnabledAsync(tenantModules, resource, cancellationToken)`
  (Storage.Application): busca el tipo de dueño en `FileOwnerModules` y, si tiene módulo, llama a
  `TenantModuleGuard.EnsureEnabledAsync(...)`: 403 `tenancy.module_not_enabled`. Va **después del
  404** de otro tenant —que va primero para no confirmar que el id existe ahí— y **antes de
  cualquier otra regla o efecto** (bucket, auditoría, `SaveChangesAsync`). Barrido de
  `repository.GetAsync` en Storage.Application, los siete:

  | Handler | Endpoint | Carga y 404 | El guard va antes de |
  | --- | --- | --- | --- |
  | `IssueDownloadUrlHandler` | `POST /{id}/download-url` | `IssueDownloadUrl.cs:32-38` | `EnsureDownloadable` (`:41`); cubre también la copia pública de un comprobante movido (`:44-46`) |
  | `CompleteUploadHandler` | `POST /{id}/complete` | `LoadAsync` (`CompleteUpload.cs:30`, definido en `:185-200`) | `objectStorage.StatAsync` (`:32`) |
  | `CancelUploadHandler` | `DELETE /{id}/upload` | `CancelUpload.cs:28-33` | `objectStorage.DeleteAsync` (`:37`) |
  | `UpdateFileMetadataHandler` | `PATCH /{id}/metadata` | `UpdateFileMetadata.cs:27-32` | `UpdateMetadata` |
  | `PublishFileHandler` | `PUT /{id}/publication` | `LoadAsync` (`SetFilePublication.cs:36`, definido en `:50-59`) | `PaymentProofGuard.EnsureNotPaymentProof`; el chequeo de bucket público sin configurar (`:29-34`) sigue primero, porque no carga nada |
  | `UnpublishFileHandler` | `DELETE /{id}/publication` | `PublishFileHandler.LoadAsync` (`SetFilePublication.cs:78-79`) | `PaymentProofGuard.EnsureNotReferencedAsync` |
  | `SoftDeleteFileHandler` | `DELETE /{id}` | `SoftDeleteFile.cs:27-32` | `PaymentProofGuard.EnsureNotReferencedAsync` |

- **No llevan el guard**, y por qué:
  - `CreateUploadSessionHandler` no carga nada por id. Crear la sesión de un `PaymentProof` con
    `orders` apagado deja una fila `PendingUpload` que no se puede completar (el guard de
    `complete` la para) y que purga `StagingCleanupProcessor.PurgeAbandonedUploadsAsync`
    (`StagingCleanupProcessor.cs:61-65`).
  - Los adaptadores del composition root que leen `IFileResourceRepository` —`ProductImageLookup`,
    `PublicPaymentProofPublisher`, `QuotationFileLookup`, `QuotationTenantLogoLookup`,
    `TenantLogoStorage`— no son endpoints de Storage: los llama un caso de uso de Catalog o de
    Quotations, cuyo permiso ya está enmascarado por su módulo, o el del logo, que es núcleo.
- Con `FindAsync` en `null` (stub, tenant simulado) no filtra ni bloquea.

**Residual aceptado** (ver DECISIÓN-PENDIENTE): lo que ya salió por un enlace público —las copias
de comprobantes en el bucket público que enlaza el Excel de pedidos, las imágenes de producto
publicadas y los PDF de cotización publicados para WhatsApp— sigue siendo alcanzable por quien
tenga la URL, y los comprobantes viejos guardados como `User` siguen siendo núcleo.

## Contratos — frontend (`qep-frontend`, rama `main` al escribir esto)

| Archivo | Cambio |
| --- | --- |
| `src/features/auth/services/tenancy.api.ts` | `TenantModules`, `TenantModuleItem`, `tenantModulesQueryKey(tenantId)` = `['tenancy','modules',tenantId]`, `fetchTenantModules` con `X-Tenant-Id` explícito, igual que `fetchEffectivePermissions` (`:53-63`). **Valida la forma** de la respuesta: `modules` tiene que ser un arreglo y cada ítem traer `key` (string), `enabled` y `contracted` (booleanos) y `missingDependencies` (arreglo); los ítems con una clave que la SPA no conoce se descartan. Si no cumple, lanza `TenantModulesPayloadError`. |
| `src/features/auth/types/tenant-modules.ts` (nuevo) | `TenantModuleKey` (unión de las siete) y `MODULE_LABELS`: Catálogo, Clientes, Empresas, Cotizaciones, Pedidos, Reportes, Punto de venta. |
| `src/features/auth/hooks/use-tenant-modules.ts` (nuevo) | `useTenantModules()` → `{ isEnabled(key), item(key), status }`, mismo `staleTime: 5 * 60 * 1000` que `usePermissions` (`use-permission.ts:37-46`) y su mismo `retry`, salvo que **no reintenta** un `TenantModulesPayloadError`: una respuesta malformada no se arregla reintentando, y el reintento esperaría el segundo de `retryDelay` por defecto de TanStack, que es también el tope por defecto de `findBy`. Una respuesta malformada da `status: 'error'`. `isEnabled(key)`: con `'ready'`, el `enabled` del ítem (`false` si no vino); con `'error'` o `'denied'`, `true` (mismo fail open que `ModuleGate`, criterio 2); con `'loading'`, `false`. |
| `src/test/tenant-modules.ts` (nuevo) | `ALL_TENANT_MODULES_ON`, la respuesta de `/modules` con los siete prendidos, y `tenantModulesResponse(overrides)` para apagar algunos. Mismo papel que `test/permissions.ts` (`ALL_SIDEBAR_PERMISSIONS`). Lo usan las pruebas nuevas; las existentes no lo necesitan (ver «Pruebas → Frontend»). |
| `src/features/quotes/hooks/use-quote-order.ts` | `GET .../quotations/{id}/order` es de `orders`, y lo llaman el detalle (`quotes/$quoteId/index.tsx:44`), el editor (`quotes/$quoteId/editar.tsx:116`) y la conversión (`quotes/$quoteId/order.tsx:57-58`), que viven bajo `quotations`. `enabled` (`:37`) suma `isEnabled('orders') && can(ORDER_READ_PERMISSION)` (`features/quotes/types/order.ts:52`). Con eso resuelto y cerrado devuelve `order: null` e `isLoading: false` —«sin pedido», como el 404 (`:33`)—, no `undefined`, que el contrato del hook reserva para «cargando» (`:13-15`); mientras módulos o permisos cargan, `isLoading: true`. Hoy, con `orders` apagado, la consulta da 403 y queda en `isError`. |
| `src/routes/_authenticated/quotes/$quoteId/order.tsx` | Queda bajo el layout de `quotes` (`['quotations']`), que no alcanza: convierte y lee el pedido. Se parte como `orders-export-columns`: el `component` de la ruta es `<ModuleGate modules={['orders']}>` y adentro el container con los hooks actuales (`useQuote`, `useConvertQuoteToOrder`, `useOrderDetail`, `useQuoteOrder`, `:40-58`). El botón «Convertir en pedido» del detalle (`quote-detail-page.tsx:198-208`) sigue llevando ahí, y con `orders` apagado esa ruta explica por qué. |
| `src/features/customers/components/customer-history-sections.tsx` | Hoy llama `useQuoteList` y `useOrderList` siempre (`:30-49`), desde la ficha del cliente (`customer-detail-page.tsx:135`), que es de `customers`, sin dependencias. Se parte en `QuotesHistoryCard` y `OrdersHistoryCard`, cada una dueña de su consulta; `CustomerHistorySections` lee `useTenantModules()` y monta cada tarjeta sólo con su módulo habilitado (`quotations`, `orders`). Sin ninguno, no dibuja nada. |
| `src/components/module-gate.tsx` (nuevo) | `<ModuleGate modules={['reporting','orders']}>`. Va en `components/` porque lo usan cinco features (SDD-ADR-07). |
| `src/routes/_authenticated/{catalog,customers,companies,quotes,orders}.tsx` (nuevos) | Rutas layout que renderizan `<ModuleGate modules={[...]}><Outlet /></ModuleGate>`. Mismo patrón que `quotes/$quoteId.tsx`, que ya es layout de sus hijos. En `quotes.tsx` el gate queda por fuera de los `QuotesReadGate` de las hojas. |
| `src/routes/_authenticated/reports/{orders,quotations,price-changes,customers}.tsx` | Envuelven la página en `ModuleGate` con `['reporting', <fuente>]`. `reports/log.tsx` no: es núcleo. |
| `src/routes/_authenticated/settings/orders-export-columns.tsx` | Se parte en dos: el `component` de la ruta es `<ModuleGate modules={['orders']}>` y adentro el container actual, que es el que llama a `useOrdersExportLayout` y `usePermissions` (`:21-24`). |
| `src/routes/_authenticated/settings/index.tsx` y `src/features/tenant-settings/pages/tenant-settings-page.tsx` | El container lee `useTenantModules()` y le pasa a la página `showOrdersExport`; la página oculta la sección «Excel de pedidos» con su enlace «Columnas del Excel de pedidos» (`tenant-settings-page.tsx:130-150`) cuando es `false`. Mientras los módulos cargan o fallan, se muestra (criterio 2 de `ModuleGate`). |
| `src/features/tenant-settings/services/orders-export-layout.api.ts` | Traduce `tenancy.module_not_enabled` al mensaje del plan. |
| `src/features/auth/services/landing.ts` | Ver «Landing». |
| `src/components/app-shell/sidebar-nav-items.ts` | Sólo exportar `SidebarRoute` (`:20`). El filtrado ya lo hace el enmascarado. |
| `src/features/roles/roles-page.tsx` | Con el catálogo cargado, `RoleDetail` (`:232-263`) omite los permisos que no están en el catálogo, y el contador de la lista de roles (`:183-184`, hoy `role.permissions.length`) cuenta sólo los que sí están. Sin catálogo cargado, los dos siguen como hoy. |

**Por qué el gate va a nivel de ruta.** En cotizaciones las consultas viven en la ruta y no en la
página (`quotes-read-gate.tsx:13-15`), y en 14 rutas de las carpetas de módulo, más
`settings/orders-export-columns.tsx`, el `component` de la ruta llama hooks directamente (p. ej.
`orders/$orderId/index.tsx`, `catalog/products/new.tsx`, `customers/$customerId.tsx`). Si `ModuleGate` envolviera sólo la página, esas consultas
saldrían igual y fallarían con 403. Como layout, o como el elemento más externo del `component`
con los hooks en un hijo, la rama de abajo no se monta y no se dispara ninguna consulta.

**Barrido de consultas entre módulos.** Además de las rutas de cada módulo, se revisaron todos
los imports de `hooks/` y `services/` de una feature de módulo desde otra feature o desde rutas
de otro módulo (`rg "from '@/features/<m>/(hooks|services)/"` para `catalog`, `customers`,
`companies`, `quotes`, `orders` y `reports`). Sólo tres cruzan hacia un módulo que puede estar
apagado con la pantalla prendida, y son las tres filas de arriba (`use-quote-order.ts`,
`quotes/$quoteId/order.tsx`, `customer-history-sections.tsx`). El resto queda cubierto por las
dependencias o es núcleo:

| Consumidor | Consulta de | Por qué no hace falta gate |
| --- | --- | --- |
| `features/quotes/hooks/use-quote-products.ts`, `use-product-name-lookup.ts` (`useProductList`) | `catalog` | `quotations` efectivo implica `catalog`. |
| `features/quotes/hooks/use-quote-customer.ts`, `use-quote-customers.ts`, `components/quote-customer-edit-modal.tsx`; `quotes/$quoteId/editar.tsx:21` (`useCustomerAddresses`) | `customers` | `quotations` implica `customers`. |
| `features/quotes/hooks/use-quote-billing-companies.ts` | `companies` | `quotations` implica `companies`. |
| `orders/$orderId/index.tsx` y `editar.tsx`, `features/orders/*` (hooks y servicios de `features/quotes`, `useQuoteProducts`) | `quotations`, `catalog` | `orders` implica `quotations` y, por él, `catalog`. |
| `features/reports/components/report-customer-combobox.tsx` (`useCustomerList`), en `orders-report-filters.tsx:173` y `quotations-report-filters.tsx:158` | `customers` | Esos reportes exigen `orders` o `quotations`, que implican `customers`. |
| `features/reports/components/report-product-combobox.tsx` (`useProductList`), en `price-change-report-filters.tsx:148` | `catalog` | El reporte exige `catalog`. |
| `features/reports/components/customers-report-filters.tsx:56` (`useClassificationList`) | `customers` | El reporte exige `customers`. |
| `useCitiesForDepartments` (`features/customers/hooks/`), desde rutas de cotizaciones y pedidos | Geography | Llama `fetchCities` de `components/geography.api` (`use-cities-for-departments.ts:3,23`): núcleo. |
| `useCustomerSync` (`routes/_authenticated.tsx:8`) | — | No consulta: escribe la caché con `setQueryData` e invalida listados de clientes (`use-customer-sync.ts:38-44`), que sólo vuelven a pedirse si una pantalla de clientes está montada. |
| `useDownloadPaymentProof` (`quotes/$quoteId/index.tsx:57`) | Storage, archivos `PaymentProof` | Es una mutación que sólo dispara el clic en un comprobante del pedido, y sin `orders` no hay pedido que mostrar. |

Ninguna feature de núcleo (`tenant-settings`, `memberships`, `account`, `roles`, `invitations`)
ni `components/` importa hooks o servicios de una feature de módulo.

**Mensajes de `ModuleGate`.** `modules[0]` es el módulo de la pantalla y los siguientes son sus
fuentes (`['reporting', 'customers']` en `reports/customers.tsx`). En este orden:

1. `status === 'loading'` → tarjeta propia «Cargando módulos…», con el mismo marco que
   `QuotesReadGate` (`PageContainer` + `Card`). No reusa «Cargando permisos...»: lo que carga es
   otra cosa, y los dos gates pueden aparecer uno detrás del otro.
2. `status === 'error'` o `'denied'` → renderiza los hijos. Mismo criterio que
   `visibleSidebarItems` con `error` (`sidebar-nav-items.ts:221`): el backend autoriza igual
   —enmascara los permisos aunque la SPA no sepa qué módulos hay—, y esconder todo por un fallo
   de transporte o una respuesta malformada deja la app sin salida.
3. `modules[0]` con `contracted: false` → «Este módulo no está incluido en el plan de tu
   empresa.»
4. Si no, se juntan, sin repetir y en el orden de la respuesta, las fuentes (`modules[1..]`) con
   `contracted: false` y las `missingDependencies` de cada módulo pedido que está contratado pero
   apagado. Si hay alguna, se nombran con concordancia de número:
   - una: «Este módulo necesita Clientes, que no está incluido en el plan de tu empresa.»
   - varias: «Este módulo necesita Catálogo y Clientes, que no están incluidos en el plan de tu
     empresa.»

   Así, en `reports/customers` con `reporting` contratado y `customers` no, la pantalla nombra
   Clientes en vez de decir que Reportes no está en el plan.
5. Si no, los hijos (y ahí el gate de permiso dice lo suyo).

**Landing.** `landingFor(can)` (`landing.ts:23-26`) sigue prefiriendo `/quotes` y después
`/orders`; si no puede ninguno, toma el `to` del primer ítem de
`visibleSidebarItems(can, 'ready')`; si no hay ninguno, `/quotes`, que con `quotations` apagado
muestra el mensaje del plan. `Landing` pasa a ser `SidebarRoute`. Un tenant sin cotizaciones ni
pedidos aterriza en `/customers`, `/companies` o `/catalog`, en ese orden, que es el del sidebar
(Clientes, Empresas, Productos: `sidebar-nav-items.ts:98-111`).

## Errores y casos borde

| Caso | Qué pasa |
| --- | --- |
| Tenant con cero módulos | Válido: así nace uno con `GrantDefaultModulesOnSignup = false`. Admin ve Miembros, Roles, Configuración (sin «Excel de pedidos») y Reportes→log (núcleo). Asesor: sin ítems de menú; aterriza en `/quotes` y ve el mensaje del plan. |
| Dependencia faltante | El dependiente cuenta como apagado (fail closed) y el endpoint de módulos nombra la causa raíz en `missingDependencies`, aunque esté varios niveles abajo. |
| Módulo apagado a mitad de sesión | Backend: el request siguiente ya es 403 (permisos y módulos se leen en cada request, sin caché). SPA: hasta **5 minutos** sigue mostrando menú y pantallas viejas (`staleTime` de permisos y de módulos; `refetchOnWindowFocus: false`, `query-client.ts:49`), y cada acción falla con su mensaje de 403 actual. Recargar lo corrige al instante. Aceptado. |
| 403 de política | No pasa por `ApiExceptionHandler` (`Program.cs:84-87`): sin `code`. La SPA no lo usa para distinguir; usa el endpoint de módulos. |
| Rol custom con permisos de un módulo apagado | Los conserva y deja de concederlos mientras el módulo esté apagado. La SPA no los muestra ni los cuenta. |
| Job de exportación en vuelo | Termina y manda su correo: los workers no miran módulos. |
| Datos | Apagar nunca borra. Volver a prender devuelve todo como estaba. |
| Comprobante o imagen de producto con su módulo apagado | No aparece en `GET /files`, y los siete comandos por id (`download-url`, `complete`, cancelar subida, `metadata`, poner y quitar publicación, borrado) dan 403 `tenancy.module_not_enabled`. Una URL pública que ya se compartió sigue abriendo. |
| Tenant creado por un pod viejo durante el rolling update | Queda sin filas: sólo núcleo hasta que se repare con el SQL de «Operación». |
| Clave desconocida en la tabla | Imposible por el `CHECK`. Si alguien quitara el `CHECK` y escribiera una, `TenantModuleKey.Parse` lanza al materializar la fila y el request da 500: ruidoso a propósito, porque ignorarla escondería una base corrupta. |
| Stub con tenant inexistente | Sin enmascarado, sin filtro de archivos y `/modules` con todo prendido (ver arriba). |
| `/modules` por cookie real con `FindAsync` en `null` | Los siete apagados, igual que los permisos (sólo núcleo). No debería ocurrir: hay membresía activa, luego hay tenant. |
| Respuesta de `/modules` malformada en la SPA | `status: 'error'` sin reintento; los gates dejan pasar y el backend sigue enmascarando. |
| Stub con tenant insertado a mano sin módulos | Enmascara: sólo núcleo. La prueba que lo haga tiene que sembrar sus módulos. |

## Operación: prender y apagar un módulo

No hay endpoint de administración hasta que exista el rol de plataforma. QCode lo hace por SQL; va
también en el README (§ Módulos por tenant). Local, PowerShell (base `dev_lulo_crm_v2` en el
contenedor `postgres18`, sin leer el connection string):

```powershell
# Ver los módulos de un tenant
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "SELECT module_key, source, enabled_at, note FROM tenancy.tenant_modules m JOIN tenancy.tenants t ON t.id = m.tenant_id WHERE t.slug = 'origen-botanico' ORDER BY module_key;"

# Prender pos
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source, note) SELECT id, 'pos', now(), 'manual', 'Activado por QCode' FROM tenancy.tenants WHERE slug = 'origen-botanico' ON CONFLICT (tenant_id, module_key) DO NOTHING;"

# Apagar orders
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "DELETE FROM tenancy.tenant_modules m USING tenancy.tenants t WHERE t.id = m.tenant_id AND t.slug = 'origen-botanico' AND m.module_key = 'orders';"
```

**Después de desplegar** (va en el checklist del despliegue): los tenants sin ninguna fila
creados en el último día. Un tenant sin filas también puede ser legítimo —uno al que QCode le
apagó todo, o uno del signup con `GrantDefaultModulesOnSignup = false`—, así que la consulta
sólo lista y la reparación se hace por slug, después de mirar cada uno:

```powershell
# Tenants sin módulos creados en el último día
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "SELECT t.id, t.slug, t.created_at FROM tenancy.tenants t WHERE NOT EXISTS (SELECT 1 FROM tenancy.tenant_modules m WHERE m.tenant_id = t.id) AND t.created_at >= now() - interval '1 day' ORDER BY t.created_at;"

# Reparar uno: los seis del signup
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source, note) SELECT t.id, m.key, now(), 'manual', 'Alta durante el despliegue de AddTenantModules' FROM tenancy.tenants t CROSS JOIN (VALUES ('catalog'),('customers'),('companies'),('quotations'),('orders'),('reporting')) AS m(key) WHERE t.slug = 'slug-del-tenant' ON CONFLICT (tenant_id, module_key) DO NOTHING;"
```

Sin `$` en las sentencias a propósito: PowerShell lo expandiría dentro de las comillas dobles.
Producción: las mismas sentencias, por el acceso a la base que QCode ya usa para operaciones
manuales; este spec no define uno nuevo. Efecto inmediato, sin reiniciar la API; la SPA de quien
ya está adentro se entera en hasta 5 minutos.

## Pruebas (TDD, RED antes que GREEN)

**Unitarias, Tenancy (`Modules.Tenancy.UnitTests`)**

El proyecto hoy referencia sólo Domain y Application (`Modules.Tenancy.UnitTests.csproj:13,16`);
suma Tenancy.Infrastructure para `TenantModuleDefaults`, igual que
`Modules.Notifications.UnitTests` referencia Notifications.Infrastructure
(`Modules.Notifications.UnitTests.csproj:15`).

- `TenantModuleKeys.All` está en orden topológico: para cada clave, todas sus dependencias
  aparecen antes en la lista; `DependenciesOf` sólo nombra claves de `All`.
- `TenantModuleSet.FromStored`: sin `customers`, `quotations` y `orders` quedan apagados;
  `MissingDependencies("quotations") == ["customers"]` y `MissingDependencies("orders") ==
  ["customers"]` (causa raíz, no `["quotations"]`); sin `catalog` ni `customers`, `orders`
  reporta `["catalog","customers"]` en el orden de `All`; una clave no contratada o efectiva
  reporta `[]`; `Empty` no tiene nada.
- `TenantModuleKey.Parse`: devuelve la misma instancia de `TenantModuleKeys` para cada `Value` de
  `All`; lanza `ArgumentException` con `"inventory"`, `""` y `"Catalog"` (no ignora mayúsculas,
  igual que el `CHECK`).
- `TenantModule.Create` guarda tenant, clave, origen, fecha y nota tal como llegan (sin
  validar el origen).
- `TenantRegistrationService`: agrega una fila `signup` por clave de los defaults y commitea una
  sola vez con tenant y membresía; con defaults vacíos no agrega ninguna.
- `TenantModuleGuard`: lanza `tenancy.module_not_enabled` con el módulo apagado; no lanza con
  `null`.
- `TenantModuleDefaults`: con el interruptor en `true` devuelve `DefaultForNewTenants`; en
  `false`, `[]`; sin la sección `Entitlements`, el default `true`.

**Unitarias, Authorization (`Modules.Authorization.UnitTests`)**
- `ModuleEntitlementMask.Apply`: núcleo pasa con `Empty`; `catalog.*` cae sin `catalog`;
  `reporting.orders.read` exige los dos; un permiso con `RequiredModules = null` cae siempre; un
  permiso no registrado cae; un permiso registrado que no concede ningún rol de sistema se
  resuelve por su definición (el índice no sale de `ListPermissions()`).
- `AuthorizationServiceTests`: `ResolvePermissionsAsync` enmascara; con `FindAsync` nulo deja sólo
  núcleo; `AuthorizeAsync` da `permission_denied` para un permiso enmascarado.
- `RoleCatalog`: el fallback de metadata produce `RequiredModules = null`.

**Unitarias, Storage (`Modules.Storage.UnitTests`)**
- `FileOwnerModules`: sus claves son exactamente `Enum.GetValues<FileOwnerType>()`; `Product` →
  `catalog`, `PaymentProof` → `orders`, el resto núcleo.
- `FileOwnerModuleGuard`: `Product` sin `catalog` y `PaymentProof` sin `orders` → 403
  `tenancy.module_not_enabled`; `Tenant` y `User` con cero módulos no lanzan; con `FindAsync`
  nulo no lanza.
- Un caso por handler de los siete que cargan por id (`IssueDownloadUrl`, `CompleteUpload`,
  `CancelUpload`, `UpdateFileMetadata`, `PublishFile`, `UnpublishFile`, `SoftDeleteFile`): un
  `PaymentProof` con `orders` apagado da 403 `tenancy.module_not_enabled` **sin efecto** —ni
  objeto borrado, copiado o consultado en el bucket, ni auditoría, ni `SaveChangesAsync`—, y un
  archivo de otro tenant sigue dando 404 antes del guard. En `PublishFile`, `UnpublishFile` y
  `SoftDeleteFile` el error tiene que ser el de módulo y no el de `PaymentProofGuard`, lo que
  prueba que el guard va antes de esa regla. `IssueDownloadUrlHandlerTests` suma
  además: `Product` sin `catalog` → 403; `Tenant` con cero módulos → URL; con `FindAsync` nulo
  → URL.
- `ListFiles` (junto a `PaymentProofPublicUrlTests`): sin `orders` el handler pasa
  `PaymentProof` en `excludedOwnerTypes`; con todo prendido o con `FindAsync` nulo no excluye
  nada.

**Unitarias, Quotations**: `GetOrdersExportLayoutHandler` y `UpdateOrdersExportLayoutHandler`
dan 403 `tenancy.module_not_enabled` sin `orders`, antes de leer el repositorio y del validador.

**Arquitectura (`tests/ArchitectureTests`)**
- `CompositionRootTests` — completitud del mapa: por reflexión, toda constante `public const
  string` de toda clase `*Permissions` de los ensamblados Application tiene una
  `PermissionDefinition` registrada con `RequiredModules` no nulo; todo permiso de
  `IRoleCatalog.ListPermissions()` tiene `RequiredModules` no nulo (ninguno salió del fallback).
  Ancla: la reflexión encuentra las 35.
- `ConfigurationExampleTests` en verde con `Entitlements:GrantDefaultModulesOnSignup` en el
  ejemplo.
- `TenancyLayerTests`: Tenancy.Application sigue sin referenciar EF Core.

**Integración**
- Tenancy (`TenantModulesApiTests`, nueva):
  - signup deja las seis filas `signup` y no `pos`, en el mismo `SaveChangesAsync` que el tenant:
    es la prueba de que EF ordena el `INSERT` de `tenants` antes que el de `tenant_modules`;
  - con `UseSetting("Entitlements:GrantDefaultModulesOnSignup", "false")` no deja ninguna y
    `/authorization/me` del owner sólo trae núcleo;
  - `GET /modules` trae las siete en orden; borrar `customers` muestra `quotations` y `orders` en
    `enabled: false`, los dos con `missingDependencies: ["customers"]`;
  - `GET /modules` de un tenant simulado por el stub (sin fila) trae las siete con
    `enabled: true` y `contracted: true`;
  - `TenantModulesResponse.From` sin HTTP, los dos casos de `null`: con `isDevelopmentStub`
    en `true`, las siete prendidas y contratadas; en `false`, las siete apagadas, sin contratar y
    sin `missingDependencies`;
  - otro tenant → 403; el `CHECK` rechaza `'inventory'` con `23514`;
  - el stub enmascara un tenant registrado: con `X-Permissions` de `catalog.product.read` y
    `catalog` borrado, `/authorization/me` no lo lista.
- Migración (`TenantModulesMigrationTests`, nueva, con `IMigrator` como `OrdersMigrationTests` y
  `AuthorizationPermissionsMigrationTests`): migrar Tenancy hasta
  `20260924152521_AddMembershipAdvisorCode`, insertar un tenant por SQL, migrar al final y ver sus
  seis filas `backfill`.
- Semilla (`SeedStartupTests`): el tenant de la semilla queda con las siete `seed`; correr dos
  veces no duplica ni falla.
- Quotations (con `RegisterTenantAsync`, `QuotationsApiHarness.cs:136`): `GET /quotations` 200;
  se borra `quotations` → 403; `/authorization/me` ya no lista `quotations.quotation.*`;
  `/authorization/catalog` no trae `quotations.*` ni en `permissions` ni en los roles;
  `/orders-export-layout` sin `orders` → 403 `tenancy.module_not_enabled`.
- Storage por dueño, en `OrderPaymentProofPublicationApiTests` (Quotations, ya registra el tenant
  y adjunta comprobantes): con un comprobante adjunto, borrar `orders` → `GET /files` no lo lista,
  su `download-url` y su `PATCH /metadata` dan 403 `tenancy.module_not_enabled` (el segundo
  es el que hoy devolvería su `PublicUrl`); el logo del tenant se sigue descargando.
- `RealAuthenticationApiTests`: un caso por cookie real — apagar `catalog` y `GET` de productos da
  403 —, que cubre `ExternalClaimsTransformation` → `AuthorizationService`.
- Regresión: las suites con tenants simulados (sin fila) en verde **sin tocarlas**; las que usan
  `register-tenant` en verde sin tocarlas (nacen con los seis).
- Regresión, pedido en el listado de cotizaciones (en las pruebas de Quotations con
  `RegisterTenantAsync`): con una cotización convertida en pedido y `orders` borrado,
  `GET /quotations` responde 200 y la fila trae `orderId` y `orderStatus` en `null`. No hace falta
  código nuevo: `ListQuotationsHandler` ya pide los pedidos sólo si el llamador tiene
  `quotations.order.read` (`ListQuotations.cs:170-177`), y ese permiso queda enmascarado. La
  prueba fija que el enmascarado alcanza al dato de otro módulo que viaja dentro de una respuesta
  de un módulo prendido.

**Pruebas unitarias existentes cuyo constructor cambia.** Verificado en `tests/`; se ajustan en
el mismo commit que el constructor, con un `ITenantModules` falso elegido para que sus casos
actuales no cambien de significado: en los handlers de Storage y de la configuración de pedidos,
uno que devuelve `null` (tenant simulado: ni filtra ni bloquea); en `AuthorizationService`, uno
con los siete módulos, porque ahí `null` es fail closed y dejaría sólo núcleo:

| Clase que cambia | Archivo de prueba | Dónde se construye |
| --- | --- | --- |
| `AuthorizationService` | `tests/Modules/Authorization/Modules.Authorization.UnitTests/AuthorizationServiceTests.cs` | `:47`, `:59`, `:73`, `:90` |
| `IssueDownloadUrlHandler` | `tests/Modules/Storage/Modules.Storage.UnitTests/IssueDownloadUrlHandlerTests.cs` | `HandlerFor` (`:72`) |
| `PublishFileHandler` | `tests/Modules/Storage/Modules.Storage.UnitTests/PaymentProofFileManagementTests.cs` | `:130`, `:154` |
| `SoftDeleteFileHandler` | `PaymentProofFileManagementTests.cs` | `DeleteHandler` (`:227`) |
| `UnpublishFileHandler` | `PaymentProofFileManagementTests.cs` | `UnpublishHandler` (`:241`) |
| `ListFilesHandler` | `tests/Modules/Storage/Modules.Storage.UnitTests/PaymentProofPublicUrlTests.cs` | `:39` |
| `GetOrdersExportLayoutHandler` | `tests/Modules/Quotations/Modules.Quotations.UnitTests/GetOrdersExportLayoutHandlerTests.cs` | `NewHandler` (`:195`) |
| `UpdateOrdersExportLayoutHandler` | `tests/Modules/Quotations/Modules.Quotations.UnitTests/UpdateOrdersExportLayoutHandlerTests.cs` | `NewHandler` (`:309`) |
| `PermissionDefinition` (no es constructor de handler, pero es posicional) | `tests/Modules/Authorization/Modules.Authorization.UnitTests/RoleCommandsTests.cs` | `:24`, `:26`, `:28` |

Sin prueba unitaria hoy, así que nada que ajustar y su caso nuevo va en un archivo nuevo:
`CompleteUploadHandler`, `CancelUploadHandler`, `UpdateFileMetadataHandler` y
`TenantRegistrationService` (sólo lo cubren las integraciones). `DevelopmentAuthenticationHandler`
es `internal` de Bootstrapper y no tiene prueba unitaria: lo cubren las integraciones del stub.

**Pruebas que insertan tenants directamente.** Barrido de `Tenants.Add(`, `Tenant.Create(` y
`INSERT INTO tenancy` en `tests/` (las de `Modules.Tenancy.UnitTests` usan `Tenant.Create` en
memoria y no tocan la base):

| Archivo | Línea | Permisos que usa | Cambio |
| --- | --- | --- | --- |
| `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomersApiHarness.cs` (`SeedTenantAsync`, usado por `CustomerExportApiTests.cs:34,88`) | `:49-71` | `customers.*` | **Inserta sólo las claves que faltan** de las siete, con `source = 'seed'`: `INSERT ... ON CONFLICT (tenant_id, module_key) DO NOTHING` por SQL, o el equivalente por EF (leer las claves que ya tiene el tenant y agregar con `TenantModule.Create(..., "seed", ...)` las que no). Corre también cuando el tenant ya existía —hoy el método devuelve antes (`:56-59`)—, así que se puede llamar dos veces sobre la misma base sin `23505`. Sin esto, 403. |
| `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/ProductExportApiTests.cs` (`SeedTenantAsync`, llamado en `:49,125`) | `:188-202` | `catalog.*` | **Agrega las siete filas**, igual. Sin esto, 403. |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantSettingsApiTests.cs` | `:185` | sólo núcleo | Ninguno. |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantLogoApiTests.cs` | `:402` | núcleo; archivos `Tenant` (`:427`) | Ninguno: `Tenant` es núcleo. |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantClockTests.cs` | `:64` | sólo núcleo | Ninguno. |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipApiTests.cs` | `:1313` | sólo núcleo | Ninguno. |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/InvitationApiTests.cs` | `:294` | sólo núcleo | Ninguno. |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthSessionApiTests.cs` | `:166`, `:175`, `:394` | sólo núcleo | Ninguno. |
| `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/InvitationNotificationTests.cs` | `:56` | sólo núcleo | Ninguno. |

**Por qué las siete y no sólo `customers`.** El `TenantId` que siembra `CustomersApiHarness`
(`...0001`, `CustomersApiHarness.cs:23`) es el mismo tenant de desarrollo que usan **todas** las
pruebas de Customers, no uno propio de la exportación. Una vez sembrado, ese tenant tiene fila en
`tenancy.tenants` y el stub lo enmascara para cualquier request contra esa base, sea de la prueba
que lo sembró o de otra que la comparta y pida por `X-Permissions` un permiso de otro módulo
comercial (`catalog`, `companies`, `quotations`…). Con sólo `customers`, esa otra prueba recibiría
un 403 que no tiene nada que ver con lo que prueba. Con las siete, el tenant sembrado se comporta
igual que el simulado. Mismo criterio en `ProductExportApiTests`, aunque su tenant (`...0081`) es
propio del archivo.

`OrphanUserCleanupTests.cs:468` inserta en `tenancy.memberships`, no en `tenancy.tenants`. Las
pruebas que usan el tenant de la semilla (`CatalogSeedTests`, `CompaniesSeedTests`,
`QuotationsSeedTests`, `ExportLoadSeedTests`) lo reciben con los siete por `TenancySeeder`.

**Frontend (Vitest + Testing Library, `bun run test --run`)**
- `tenancy.api.test.ts`: `fetchTenantModules` con `{}`, con `modules` que no es arreglo y con un
  ítem sin `enabled` lanza `TenantModulesPayloadError`; descarta un ítem con clave desconocida.
- `use-tenant-modules.test.tsx`: clave por tenant; estados `loading/ready/denied/error`; una
  respuesta malformada da `'error'` con **un solo** request (sin reintento); `isEnabled` en
  `'ready'`, en `'error'` (`true`) y en `'loading'` (`false`).
- `module-gate.test.tsx`: «Cargando módulos…»; `modules[0]` no contratado → mensaje del plan;
  contratado sin una dependencia → mensaje en singular con su etiqueta; sin dos → plural con las
  dos etiquetas en orden; `['reporting','customers']` con `customers` sin contratar → «Este
  módulo necesita Clientes…» y no «Este módulo no está incluido…»; error → hijos; habilitado →
  hijos.
- `use-quote-order.test.tsx`: con `orders` apagado o sin `quotations.order.read` no hay request
  a `.../order` y devuelve `order: null`, `isLoading: false`; con módulos cargando,
  `isLoading: true`.
- `customer-history-sections.test.tsx`: sin `orders` sólo se ve la tarjeta de cotizaciones y no
  sale `GET /orders`; sin `quotations` ni `orders`, nada y ningún request.
- Rutas: un caso por cada layout nuevo (`catalog`, `customers`, `companies`, `quotes`, `orders`),
  por cada reporte envuelto, por `settings/orders-export-columns` y por
  `quotes/$quoteId/order` (con `quotations` prendido y `orders` apagado): con el módulo apagado
  se ve el mensaje del plan y no se dispara la consulta del hijo; precedente,
  `reports/reports-routes.test.tsx`. `quotes/$quoteId/index.test.tsx`: con `orders` apagado el
  detalle se pinta entero y no sale `GET .../order`.

**Pruebas del frontend que montan una ruta con gate.** `renderRoute` monta el árbol real
(`src/test/render-route.tsx:20-36`), y no hay un stub compartido: cada archivo arma el suyo, y
todos terminan en un catch-all `json(200, {})`. Con el gate, esas pruebas piden `/modules` y
reciben `{}`. No se tocan: `fetchTenantModules` lo rechaza por forma, el hook queda en `'error'`
sin reintento y `ModuleGate` renderiza los hijos (criterio 2). Ninguno de esos stubs rechaza una
URL desconocida, y ninguno atiende hoy `/modules`. Son 17:

| Archivo | Qué monta |
| --- | --- |
| `routes/_authenticated/catalog/products/$productId.test.tsx`, `catalog/products/new.test.tsx`, `catalog/tax-rates/new.test.tsx` | layout `catalog` |
| `routes/_authenticated/companies/$companyId.test.tsx`, `companies/new.test.tsx` | layout `companies` |
| `routes/_authenticated/customers/new.test.tsx` | layout `customers` |
| `routes/_authenticated/orders/$orderId/index.test.tsx`, `orders/$orderId/editar.test.tsx` | layout `orders` |
| `routes/_authenticated/quotes/$quoteId/index.test.tsx`, `quotes/$quoteId/editar.test.tsx` | layout `quotes` y `useQuoteOrder` |
| `routes/_authenticated/quotes/$quoteId/order.test.tsx` | layout `quotes` y el gate de `orders` |
| `routes/_authenticated/reports/reports-routes.test.tsx` | reportes envueltos |
| `routes/_authenticated/settings/index.test.tsx`, `settings/orders-export-columns.test.tsx` | sección «Excel de pedidos» y su gate |
| `components/app-shell/app-shell.test.tsx` | `/customers` y `/companies` (`:195,285,349,406`) |
| `features/account/components/applied-theme.test.tsx` | `/quotes` (`:89`) |
| `routes/index.test.tsx` | `/`, que aterriza en `/quotes` o `/orders` (`:62,72,107`) |

Las demás pruebas con `renderRoute` montan `/profile`, `/login`, `/register`, invitaciones o la
raíz sin aterrizar en un módulo, y no pasan por ningún gate.

Dos pruebas de hook y componente **sí** cambian, porque la unidad gana dependencias que su
`fetch` no contesta bien: `features/quotes/hooks/use-quote-order.test.tsx` responde todo con el
pedido (`:42-49`), así que ni módulos ni permisos resuelven y la consulta no saldría; y
`features/customers/components/customer-history-sections.test.tsx` deja `fetch` colgado en su
caso de carga (`:79`), y con los módulos cargando no se monta ninguna tarjeta. La primera
mockea `useTenantModules` y `usePermissions`, y la segunda `useTenantModules`, con `vi.mock`,
como ya mockean `use-quotes-tenant` (`use-quote-order.test.tsx:10-12`) y `use-active-tenant`
(`customer-history-sections.test.tsx:19`).
- `settings/index.test.tsx`: sin `orders` no se ve «Columnas del Excel de pedidos»; con `orders`,
  o con módulos cargando o en error, sí.
- `landing.test.ts`: sin cotizaciones ni pedidos → primer ítem del sidebar (`/customers` antes que
  `/companies` y `/catalog`); sin nada → `/quotes`.
- `roles-page.test.tsx`: `RoleDetail` no pinta permisos fuera del catálogo cargado, y el contador
  de la lista de roles no los cuenta.
- `orders-export-layout.api.test.ts`: `tenancy.module_not_enabled` → mensaje del plan.

## Decisiones tomadas sin el owner

Todas para revisar. Las primeras once vienen del brief del orquestador; las marcadas
«(revisión)» las tomó el orquestador al aplicar la ronda 1 de revisión, y las marcadas
«(revisión 2)», la ronda 2; el resto las tomó este spec.

1. Siete claves cerradas (`catalog`, `customers`, `companies`, `quotations`, `orders`, `reporting`,
   `pos`) y núcleo fijo que no se apaga.
2. Dependencias: `quotations` → `catalog`, `customers`, `companies`; `orders` → `quotations`;
   `pos` → `catalog`, `companies`; `reporting` sin dependencias, pero cada reporte exige su fuente.
3. Fail closed: un módulo sin su dependencia cuenta como apagado.
4. Enmascarar permisos en vez de filtro por grupo de rutas; el 403 no trae código propio.
5. Enmascarado en los dos caminos de autenticación, real y stub.
6. Los workers no miran módulos.
7. Tabla de Tenancy detrás de un puerto reemplazable por el control plane.
8. Backfill de los seis (sin `pos`) en la misma migración que crea la tabla.
9. Signup con los seis de hoy (sin `pos`) mientras `Entitlements:GrantDefaultModulesOnSignup`
   esté en `true`, que es el default, y sin ninguno en `false` (ver 25 y 33); semilla con los
   siete.
10. Sin endpoint de administración: SQL documentado.
11. Sin `IMemoryCache`.
12. El mapa vive como parámetro obligatorio `RequiredModules` de `PermissionDefinition`, no como
    registro aparte; `null` = sin mapear = enmascarado.
13. El stub no enmascara un tenant que no existe en `tenancy.tenants` (y `/modules` responde todo
    prendido, sólo bajo el esquema del stub; ver 37), para no romper las suites de tenants
    simulados. Sí enmascara uno con fila, aunque la haya insertado una prueba.
14. La presencia de la fila es el entitlement; apagar es `DELETE`. Columnas `source` y `note`.
15. La semilla prende los módulos sólo al crear el tenant; las bases locales viejas prenden `pos`
    con SQL.
16. `/authorization/catalog` se filtra; `/authorization/roles` y `EnsureKnownPermissions` no.
17. `catalogVersion` no cambia con los módulos.
18. Endpoint `GET /tenants/{id}/modules`: siempre los siete, con `enabled`, `contracted` y
    `missingDependencies`.
19. `/orders-export-layout` se protege con un chequeo explícito y código nuevo
    `tenancy.module_not_enabled`.
20. Ni el signup ni el backfill escriben auditoría nueva: la fila guarda `source` y `enabled_at`.
21. `reporting.all_advisors.read` exige sólo `reporting`.
22. En la SPA, si el endpoint de módulos falla, los gates dejan pasar (el backend autoriza igual).
23. Landing: cotizaciones, pedidos, primer ítem del sidebar y, sin nada, `/quotes`.
24. (revisión) Los archivos de Storage heredan el módulo de su tipo de dueño: `Product` →
    `catalog`, `PaymentProof` → `orders`, `User`, `Entity`, `System` y `Tenant` núcleo.
    `GET /files` excluye los de módulos apagados y los comandos por id responden 403
    `tenancy.module_not_enabled` (ver 34). Los enlaces públicos ya compartidos quedan como
    residual aceptado.
25. (revisión) Interruptor explícito `Entitlements:GrantDefaultModulesOnSignup` (default `true`,
    el comportamiento de hoy); en `false` el signup no da ningún módulo. Cuando exista el cobro,
    lo probable es que QCode lo ponga en `false`.
26. (revisión) Se conservan la columna `source` y su `CHECK` en la base, y se quita la validación
    de `source` del dominio.
27. `missingDependencies` nombra las causas raíz (dependencias transitivas no contratadas), no la
    dependencia directa.
28. `ModuleEntitlementMask` indexa las `PermissionDefinition` registradas, no las que concede algún
    rol de sistema.
29. Claves de módulo tipadas (`TenantModuleKey`) en `PermissionDefinition`, `TenantModuleGuard`,
    `TenantModuleSet.FromStored` y el dominio; `string` sólo en la base y en el JSON. La única
    conversión de texto a clave es `TenantModuleKey.Parse`, en el mapeo de EF, y lanza con una
    clave desconocida.
30. `TenantModule.TenantId` es el value object `TenantId`, con relación EF a `Tenant` sin
    navegación, para que EF ordene los `INSERT`.
31. Un filtro explícito por un tipo de dueño de un módulo apagado devuelve una página vacía, no un
    403.
32. `ModuleGate` vive a nivel de ruta (layouts por carpeta de módulo) para que las consultas de
    las rutas hijas no se disparen.
33. (revisión 2) Se quita `Entitlements:DefaultModules` (YAGNI): queda sólo el interruptor
    `GrantDefaultModulesOnSignup` (`true` = los seis, `false` = ninguno), sin validador. El
    paquete por tenant lo decide el control plane.
34. (revisión 2) El chequeo por dueño va en **todos** los handlers de Storage que cargan un
    archivo por id —`IssueDownloadUrl`, `CompleteUpload`, `CancelUpload`, `UpdateFileMetadata`,
    `PublishFile`, `UnpublishFile`, `SoftDeleteFile`—, después del 404 de otro tenant y antes de
    cualquier efecto, con una prueba por handler. Ya no queda el residual de «los comandos por
    id sólo modifican»: `complete`, `metadata` y `publication` devuelven `PublicUrl`.
35. (revisión 2) Las consultas entre módulos de la SPA se cierran donde un módulo prendido
    consulta a uno que puede estar apagado: `useQuoteOrder` (módulo y permiso),
    `quotes/$quoteId/order` (gate de `orders`) y el historial de la ficha del cliente (cada
    tarjeta con su módulo). Las demás quedan cubiertas por las dependencias.
36. (revisión 2) `fetchTenantModules` valida la forma; una respuesta malformada es `'error'` sin
    reintento y los gates dejan pasar. Fail open en el cliente es aceptable porque el servidor
    enmascara. Con eso las 17 pruebas de ruta existentes, cuyo catch-all responde `{}`, siguen
    verdes sin tocarlas; las nuevas usan `src/test/tenant-modules.ts`.
37. (revisión 2) `/modules` con `FindAsync` en `null` responde todo prendido **sólo** bajo el
    esquema del stub de desarrollo; con cualquier otro, todo apagado, como el fail closed de los
    permisos.
38. (revisión 2) `ModuleGate` distingue el módulo de la pantalla (`modules[0]`) de sus fuentes:
    una fuente sin contratar se nombra («Este módulo necesita Clientes…»), igual que una
    dependencia faltante, en vez de decir que el módulo no está en el plan.

## DECISIÓN-PENDIENTE

- **Auditoría de los cambios manuales**: quién prendió o apagó qué y cuándo. Hoy un `DELETE` no
  deja rastro.
- **Copy y salida del mensaje del plan**: si lleva contacto o enlace comercial, y a dónde.
- **`quotations` sin `customers`**: hoy es dependencia dura; vender cotizaciones sin padrón de
  clientes exigiría otro diseño de la cotización.
- **Avisarle a la SPA de un cambio** sin esperar los 5 minutos (p. ej. invalidar permisos y
  módulos ante cualquier 403).
- **Nombres comerciales y planes** (paquetes de módulos): el control plane.
- **Enlaces públicos ya entregados**: siguen abriendo con el módulo apagado las copias de
  comprobantes en el bucket público (enlazadas desde el Excel de pedidos, que además llega por
  correo), las imágenes de producto publicadas y los PDF de cotización que se publicaron para
  mandarlos por WhatsApp (`QuotationPdfStorage.PublishAsync`, `QuotationPdfStorage.cs:21-73`:
  copia al bucket público con clave aleatoria y no son `FileResource`, así que no pasan por el
  chequeo por dueño). Retirarlos o rotarlos es otro trabajo.
- **Comprobantes viejos guardados como `User`** (D13): siguen siendo núcleo y se leen sin
  `orders`. Reclasificarlos exige saber cuáles son comprobantes, y hoy sólo lo sabe la referencia
  desde el pedido.

## Fuera de alcance

- El control plane, el cobro con Wompi y los planes.
- El rol de plataforma y un endpoint para administrar módulos.
- El módulo POS (sólo se reserva su clave y su lugar en el mapa) y la configuración de WhatsApp
  por tenant.
- Cuotas o límites por módulo (usuarios, cotizaciones por mes).
- Borrar o archivar los datos de un módulo apagado.

## Historial de revisión

- Ronda 1 (2026-10-07): 12 hallazgos aplicados
- Ronda 2 (2026-10-07): 8 hallazgos aplicados
- Ronda 3 (2026-10-07): sin hallazgos bloqueantes; 3 mejoras menores aplicadas. Spec listo para revisión del owner.

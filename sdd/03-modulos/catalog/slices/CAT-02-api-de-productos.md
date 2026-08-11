# `CAT-02` — API de productos

> **Estado:** In Progress
> **Módulo:** `catalog` — ficha y gate en `qep-frontend/sdd/03-modulos/catalog/`
> (`SDD-ADR-08`: los enlaces relativos no cruzan repos)
> **Depende de:** `CAT-00` (gate cerrado el 2026-08-10)
> **Bloqueado por:** ninguno — `DECISIÓN-PENDIENTE-CAT-04` cerrada el 2026-08-10 por el owner
> **Repos afectados:** `qep-backend`
> **Tamaño medido:** **1043 líneas autoradas** con un solo endpoint de cinco, sin contar
> lock files, migración generada ni `Backend.slnx`. El umbral de
> `qep-frontend/sdd/00-metodo/convenciones-de-id.md` es ~400, así que **el slice se partió**
> el 2026-08-10, como el propio spec anticipaba.

## Partición del slice

`CAT-02` deja de ser unidad ejecutable y queda como **fila padre**. No se renumera nada, y
los commits que ya citan `feat(CAT-02)` siguen siendo válidos: el ID padre no se toca
(`convenciones-de-id.md`, "Partir un slice").

| Parte | Alcance | Estado |
|---|---|---|
| `CAT-02a` | Andamiaje del módulo, dominio `Product`, persistencia con `InitialCatalog`, y `GET /products` | Código y pruebas listos (`968c4a8`). Falta runtime y revisión de riesgo |
| `CAT-02b` | Escrituras: `GET` por id, `POST`, `PUT`, `deactivate`, validadores, traducción del índice único y las pruebas de auditoría y outbox | In Progress |

Las dos partes ejecutan **en secuencia**, así que el repositorio sigue con un solo slice
activo: `CAT-02`. `a` y `b` son sus dos mitades entregables, no dos slices en paralelo.

### Reparto de criterios de aceptación

- **`CAT-02a`:** `CA-CAT-02-02` (completo), `CA-CAT-02-01` (mitad: la ruta responde para su
  tenant; el "sólo los suyos" necesita sembrar datos y por eso cierra en `b`),
  `CA-CAT-02-10` implementado pero **sin prueba** — la búsqueda no tiene con qué filtrar
  hasta que haya alta.
- **`CAT-02b`:** el resto — `-03` a `-09`, `-11`, `-12`, más las mitades pendientes de `-01`
  y `-10`.

## Objetivo

Un tenant puede listar, ver, crear, editar e inactivar los productos de su catálogo contra
la API real, con permisos propios y aislamiento entre tenants verificado por prueba.

## Fuera de alcance

- **Tasas de impuesto.** Van en `CAT-03`, slice propio. Comparten módulo y andamiaje, pero
  son otro recurso con otros permisos y otra migración.
- **Todo el frontend.** `catalog.api.ts` hoy apunta a `/api/v1/catalog/*` sin tenant;
  alinear sus 10 rutas es deuda declarada de `CAT-01`, no de este slice.
- Variantes, categorías y DIVIPOLA — sin requisito que las sustente (ficha, corrección del
  2026-08-08).
- Listas de precios y vigencias — son de `pricing` (`RF-021`, `RF-022`).
- **Eventos de integración hacia `pricing`/`quotes`.** Ver "Eventos de integración".
- Datos seed. Qué productos existen no es de este slice.

## Contrato existente que se consume

No consume ninguna ruta HTTP: consume **mecanismos de plataforma**, todos verificados en
código.

| Mecanismo | Dónde está | Qué aporta |
|---|---|---|
| `IExecutionContext` | `Modules.Tenancy.Application/IExecutionContext.cs` | `SubjectId`, `TenantId`, `HasPermission(string)` |
| Resolución de tenant y permisos | `Bootstrapper/Authentication/ExternalClaimsTransformation.cs:102-118` | lee `X-Tenant-Id` del request y deriva tenant y permisos (`SDD-CT-12`, cerrada) |
| Registro de permisos y roles | `Bootstrapper/QepServiceCollectionExtensions.cs:107-186` | `PermissionDefinition` + `RoleDefinition` en composición |
| Mapeo central de errores | `Api/ApiExceptionHandler.cs:68-90` | `DomainException` → **422** con el `code` del dominio; `ResourceNotFoundException` → **404**; `RequestForbiddenException` → **403**; `ValidationException` → **422** `validation.failed` **con** el mapa `errors` |
| Auditoría por outbox | `Modules.Storage.Application/IStorageAuditPublisher.cs` y su implementación `Persistence/StorageAuditPublisher.cs` | emite `platform.audit.recorded.v1` al outbox del propio `DbContext`, para que **commitee en la misma transacción** que el cambio |

**Nota de mecanismo, verificada:** hay **dos** caminos de auditoría. El atómico
(`IAuditRecorder`, atado al `DbContext` del productor) y el de outbox. Storage usa outbox
porque sus operaciones son operativas, no críticas de seguridad
(`IStorageAuditPublisher.cs`, comentario). **`catalog` es el mismo caso**: administrar un
producto es operativo. Se usa outbox.

## Contrato que se expone

### Endpoints nuevos o modificados

Sea `T` = `/api/v1/tenants/{tenantId:guid}/catalog`. Prefijo ratificado en
`DECISIÓN-PENDIENTE-CAT-03`; es la forma de las nueve rutas de tenant que ya existen.

| Método | Ruta | Permiso | Respuestas |
|---|---|---|---|
| `GET` | `T/products?search=` | `catalog.product.read` | 200 `ProductsResponse` · 403 |
| `GET` | `T/products/{productId:guid}` | `catalog.product.read` | 200 `ProductResponse` · 403 · 404 |
| `POST` | `T/products` | `catalog.product.manage` | 201 `ProductResponse` · 403 · 422 |
| `PUT` | `T/products/{productId:guid}` | `catalog.product.manage` | 200 `ProductResponse` · 403 · 404 · 422 |
| `POST` | `T/products/{productId:guid}/deactivate` | `catalog.product.manage` | 200 `ProductResponse` · 403 · 404 · 422 |

Cada una declarada con `.RequireAuthorization(<permiso>)`, `.Accepts<>` donde haya cuerpo,
`.Produces<>` y `.ProducesProblem(...)` por cada estado — mismo patrón que
`StorageEndpoints.cs:18-70`.

### DTOs

```csharp
public sealed record ProductResponse(
    Guid Id, string Name, string Code, bool IsActive,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ProductsResponse(IReadOnlyCollection<ProductResponse> Items);

public sealed record CreateProductRequest(string Name, string Code);

public sealed record UpdateProductRequest(string Name, string Code);
```

`IsActive` **no** viaja en los requests: se crea activo y sólo cambia por
`/deactivate`. Un booleano editable convertiría la inactivación en un `PUT` cualquiera y la
dejaría sin auditoría propia — el mismo criterio con el que `AUTH-06` separó suspender de
editar roles.

Sin `sku`, `category`, `departmentCode` ni `priceListCode`: los tres primeros no tienen
requisito, y el cuarto violaría la frontera con `pricing`.

### Permisos que se declaran

| Permiso | Qué habilita | Roles |
|---|---|---|
| `catalog.product.read` | Listar y ver productos | `tenancy.owner`, `tenancy.member` |
| `catalog.product.manage` | Crear, editar e inactivar productos | `tenancy.owner` |

Se declaran en `Modules.Catalog.Application/CatalogPermissions.cs` y se registran en
`QepServiceCollectionExtensions`, junto a los `RoleDefinition` existentes.

### Eventos de integración

| Evento | Cuándo se emite | Consumidores previstos |
|---|---|---|
| `platform.audit.recorded.v1` | en cada alta, edición e inactivación | módulo `Audit` (worker de proyección) |

**No se emiten eventos de dominio de `catalog`.** La ficha del módulo prevé a `pricing` y
`quotes` como consumidores, pero **ninguno de los dos existe en `qep-backend`** —verificado:
los módulos son Audit, Authorization, Identity, Notifications, Storage y Tenancy—. Publicar
`catalog.product-created.v1` hoy sería congelar un contrato sin un solo consumidor que lo
valide, que es exactamente lo que `AGENTS.md` §2 prohíbe. Se agregan en el slice que traiga
el primer consumidor real. **`CAT-01` los lista en su spec**; esa tabla queda corregida por
esta decisión.

### Migración

`InitialCatalog` — crea el esquema `catalog`, la tabla `catalog.products` y la tabla de
outbox del módulo, con el mismo patrón de `StorageDbContext.cs:11` (`DbSet<...> Outbox`
interno y proyección en `OnModelCreating`).

Columnas de `catalog.products`: `id` (uuid, PK, `ValueGeneratedNever`), `tenant_id` (uuid),
`name` (varchar 200), `code` (varchar 60), `is_active` (bool), `created_at`, `updated_at`.

Índices: uno por `tenant_id`, y **`IX_products_tenant_code` único sobre `(tenant_id, code)`**
— `DECISIÓN-PENDIENTE-CAT-04`, cerrada por el owner el 2026-08-10.

La violación de ese índice se traduce **en Infrastructure** —no en Application, que no
referencia Npgsql— a `CatalogDomainException("catalog.product.code_taken", …)`, que
`ApiExceptionHandler.cs:83-84` convierte en **422** con el código del dominio. Se discrimina
**por nombre de índice**, no sólo por `SqlState == "23505"`: es literalmente la lección de
`SDD-CT-06`, donde otro índice único del sistema devolvía el código equivocado.

**Reversible:** `Down()` borra el esquema `catalog` completo. Es una migración de creación
sin datos previos, así que revertirla no pierde nada que existiera antes.

## Reglas que la implementación debe respetar

| Regla | Origen |
|---|---|
| Administrar productos activos/inactivos | `RF-020`, `04-requisitos/requisitos-funcionales.md:27` |
| Cada registro pertenece a un tenant y se expone sólo dentro del tenant autenticado | ficha del módulo, "Datos y clasificación" |
| Capas: nada de lógica en el endpoint | `SDD-ADR-05`, verificado por `tests/ArchitectureTests/` |
| Traducir errores de base va en Infrastructure, no en Application | `SDD-CT-06`, cerrada. `Modules.Catalog.Application` **no** referencia EF Core ni Npgsql, y `CatalogLayerTests` lo verifica |
| Discriminar la violación de unicidad **por nombre de índice**, no sólo por `SqlState 23505` | `SDD-CT-06`: `memberships` tiene el suyo, y confundirlos devuelve el código de dominio equivocado |
| Autorización de doble capa: política en el endpoint **y** revalidación en el handler | `Modules.Storage.Application/StorageAuthorization.cs`, aplicada en `ListFiles.cs:29-30` |
| Auditoría y cambio en la misma transacción | `AGENTS.md` §7b; patrón en `StorageAuditPublisher.cs` |

## Diseño

**Backend** — capas según `SDD-ADR-05`.

```txt
src/Modules/Catalog/
  Modules.Catalog.Domain/
    Product.cs                    agregado: Create, Update, Deactivate + invariantes
    ProductId.cs                  value object
    CatalogDomainException.cs     códigos de dominio -> 422 vía ApiExceptionHandler
  Modules.Catalog.Application/
    CatalogPermissions.cs
    CatalogAuthorization.cs       EnsureAuthorized(ctx, tenantId, permiso)
    IProductRepository.cs
    ICatalogUnitOfWork.cs
    ICatalogAuditPublisher.cs
    CatalogDtos.cs
    ListProducts.cs / GetProduct.cs / CreateProduct.cs / UpdateProduct.cs / DeactivateProduct.cs
  Modules.Catalog.Infrastructure/
    Persistence/CatalogDbContext.cs, ProductRepository.cs, CatalogUnitOfWork.cs,
                CatalogAuditPublisher.cs, CatalogOutboxMessage.cs, Migrations/
    CatalogInfrastructureExtensions.cs, CatalogDatabaseInitializer.cs
  Modules.Catalog.Api/
    ProductEndpoints.cs
```

Más: alta de los cuatro `.csproj` en `Backend.slnx`, registro en `Bootstrapper`, permisos y
roles en `QepServiceCollectionExtensions`, y `tests/ArchitectureTests/ArchitectureTests/CatalogLayerTests.cs`
calcado de `TenancyLayerTests.cs`.

**Frontend** — `N/A`. Este slice no toca `qep-frontend`; la alineación de rutas es deuda de
`CAT-01`.

## Criterios de aceptación

| ID | Criterio |
|---|---|
| `CA-CAT-02-01` | Dado un usuario con `catalog.product.read` en su tenant, cuando pide `GET T/products`, entonces recibe 200 con los productos **de ese tenant únicamente** |
| `CA-CAT-02-02` | Dado un producto del tenant A, cuando un usuario del tenant B pide `GET T/products/{id}` con el `tenantId` de A en la ruta, entonces recibe **403**, no 404 — el handler revalida antes de tocar el repositorio |
| `CA-CAT-02-03` | Dado un usuario **sin** `catalog.product.manage`, cuando hace `POST T/products`, entonces recibe 403 y no se persiste nada |
| `CA-CAT-02-04` | Dado un `POST T/products` válido, entonces responde **201** con el producto creado, `isActive` en `true`, y deja **una** entrada de auditoría en el outbox, en la misma transacción |
| `CA-CAT-02-05` | Dado un `POST T/products` con `name` o `code` vacío, entonces responde **422** con el mapa `errors` por campo (validador de FluentValidation) |
| `CA-CAT-02-06` | Dado un `PUT T/products/{id}` válido, entonces responde 200 con los campos nuevos, `updatedAt` avanzado, y una entrada de auditoría |
| `CA-CAT-02-07` | Dado un `{id}` inexistente **dentro del tenant**, entonces `GET`, `PUT` y `deactivate` responden **404** |
| `CA-CAT-02-08` | Dado un producto activo, cuando se hace `POST .../deactivate`, entonces responde 200 con `isActive` en `false` y deja auditoría |
| `CA-CAT-02-09` | Dado un producto **ya inactivo**, cuando se vuelve a inactivar, entonces responde **422** con código de dominio, no 200 silencioso ni 500 |
| `CA-CAT-02-10` | Dado `GET T/products?search=`, entonces filtra por `name` y `code`, sin distinguir mayúsculas |
| `CA-CAT-02-11` | Dado el catálogo de permisos, entonces `catalog.product.read` y `catalog.product.manage` aparecen en `GET /tenants/{id}/authorization/catalog` y en `authorization/me` según el rol |

| `CA-CAT-02-12` | Dado un producto con `code` `X` en el tenant A, cuando se hace `POST T/products` con el mismo `code` en el tenant A, entonces responde **422** con `code = catalog.product.code_taken` — **no 500**. El mismo `code` en el tenant B se acepta: la unicidad es por tenant |

## Pruebas requeridas

| Nivel | Qué verifica |
|---|---|
| Unitaria | `Modules.Catalog.UnitTests` — invariantes de `Product`: no se crea con nombre o código vacío, no se inactiva dos veces, `Deactivate` avanza `UpdatedAt` |
| Arquitectura | `CatalogLayerTests` — Domain no referencia capas externas; Application no referencia Infrastructure ni Api ni EF Core; Infrastructure no referencia Api |
| Integración | `Modules.Catalog.IntegrationTests` contra **PostgreSQL real vía Testcontainers** (requiere Docker arriba, igual que `Modules.Tenancy.IntegrationTests`): los 11 CA, verificando **estado persistido y fila de auditoría en el outbox**, no sólo el status HTTP |
| Runtime | El owner corre alta, edición e inactivación contra la API local y confirma la entrada en `audit.entries` tras la proyección |

**La factory de integración debe fijar `Notifications:EmailProvider`.** No es opcional: es
`SDD-CT-17`, que ya tiró 6 pruebas al arrancar porque heredaban el `infobip` con
credenciales vacías del `appsettings.json`.

## Riesgos

| Riesgo | Cómo se detecta |
|---|---|
| Fuga entre tenants por olvidar el filtro en una consulta | `CA-CAT-02-02` y `-01`, con datos de dos tenants sembrados en la prueba de integración |
| Colisión de `code` devolviendo **500** en vez de 422 | Es la forma exacta de `SDD-CT-06`. Se evita traduciendo la violación **por nombre de índice** en Infrastructure, con prueba de integración de dos altas del mismo código |
| Auditoría que no commitea con el cambio | Prueba que cuenta las filas de outbox tras la operación, no el status |
| El slice crece más de 400 líneas | Se mide antes de commitear; si se pasa, se parte en `CAT-02a`/`CAT-02b` |
| Emitir eventos de integración sin consumidor | Declarado fuera de alcance arriba, con razón |

## Decisiones abiertas

| ID | Pregunta | Bloquea |
|---|---|---|
| ~~`DECISIÓN-PENDIENTE-CAT-04`~~ | **¿`code` es único por tenant?** `RF-020` no lo dice, y no se asumió. **Cerrada 2026-08-10, por el owner: sí, único por `(tenant_id, code)`.** Índice `IX_products_tenant_code`, y la violación se traduce en Infrastructure a `422 catalog.product.code_taken`. Precedente que la fundamenta: `SDD-CT-06`, donde un slug repetido devolvía `500` y era el error más probable de su pantalla. Agregarlo después habría sido migración sobre datos ya sucios | ~~La migración~~ — desbloqueado |

## Evidencia de cierre

Se completa al terminar. Ver `qep-frontend/sdd/00-metodo/definition-of-done.md`.

### Tramo 1 — andamiaje del módulo y prueba de arquitectura (2026-08-10)

Baseline antes de tocar nada, para que el RED no se confunda con un fallo preexistente:
`ArchitectureTests` en `Con error: 0, Superado: 12, Total: 12`.

- **RED:** `CatalogLayerTests.cs` escrito **antes** que los proyectos. Falla al compilar, por
  la razón correcta —el módulo no existe— y no por un import mal escrito:

  ```txt
  CatalogLayerTests.cs(2,15): error CS0234: El tipo o el nombre del espacio de nombres
  'Catalog' no existe en el espacio de nombres 'Modules' (¿falta alguna referencia de
  ensamblado?)
  ```

  El mismo error en las líneas 3, 4 y 5, uno por capa.

- **GREEN:** `Correctas! - Con error: 0, Superado: 16, Omitido: 0, Total: 16` — las 12
  preexistentes más las **4** de `CatalogLayerTests`. Los cuatro `.csproj` de
  `Modules.Catalog.*` creados y dados de alta en `Backend.slnx` y en
  `ArchitectureTests.csproj`.
- **Build:** `dotnet build Backend.slnx` → `Compilación correcta. 0 Advertencia(s), 0
  Errores` (5,30 s). Relevante porque `Directory.Build.props` fija
  `TreatWarningsAsErrors=true`.

La cuarta prueba, `ApplicationDoesNotReferencePersistenceLibraries`, no está en el molde de
`TenancyLayerTests`: se agregó acá para que la lección de `SDD-CT-06` quede verificada por
build y no por disciplina. Hoy pasa trivialmente porque Application todavía no tiene código
real; empieza a morder cuando llegue el repositorio.

Los tipos creados en este tramo son el **mínimo** para que la prueba de capas compile
—`Product`, `ListProductsQuery`, `CatalogInfrastructureExtensions`, `ProductEndpoints`, los
cuatro vacíos o casi—. La forma real la conduce el RED del tramo siguiente, no este.

**Commiteado en `968c4a8`.**

### Tramo 2 — dominio `Product` (2026-08-10)

- **RED:** `ProductTests.cs` (11 métodos, 13 casos con los dos `[Theory]`) escrito antes que
  el agregado. Literal, deduplicado:

  ```txt
  ProductTests.cs(14,38): error CS0103: El nombre 'ProductId' no existe en el contexto actual
  ProductTests.cs(42,35): error CS0246: El nombre del tipo o del espacio de nombres
  'CatalogDomainException' no se encontró (¿falta una directiva using o una referencia de
  ensamblado?)
  ```

  Apareció además `CS0619: 'Assert.Throws<T>(Func<Task>)' está obsoleto` en las líneas que
  ejercen `Update` y `Deactivate`. **No era un defecto de la prueba:** con `Product.Create`
  sin resolver, el compilador no podía inferir el tipo de la lambda y caía en la sobrecarga
  asíncrona. Desapareció solo al existir el agregado, sin tocar el test.

- **GREEN:** `Correctas! - Con error: 0, Superado: 13, Omitido: 0, Total: 13`.
- **Build:** `dotnet build Backend.slnx` → `Compilación correcta. 0 Advertencia(s), 0
  Errores`. `ArchitectureTests` sin regresión: `Superado: 16, Total: 16`.

Invariantes que quedaron en el agregado, todas con prueba:

| Invariante | Código de dominio |
|---|---|
| Nace activo, con `CreatedAt == UpdatedAt` | — |
| Nombre y código se recortan | — |
| Nombre en blanco | `catalog.product.name_required` |
| Código en blanco | `catalog.product.code_required` |
| Nombre de más de 200 caracteres | `catalog.product.name_too_long` |
| Código de más de 60 caracteres | `catalog.product.code_too_long` |
| Inactivar dos veces (`CA-CAT-02-09`) | `catalog.product.already_inactive` |
| Editar un producto inactivo | `catalog.product.inactive` |

Dos decisiones de diseño que el spec no dictaba y conviene dejar dichas:

1. **El recorte es invariante, no higiene del llamador.** `IX_products_tenant_code` trataría
   `" VS-001"` y `"VS-001"` como códigos distintos, y ninguna persona que lea el listado los
   leería así. Dejarlo al llamador es garantizar que el primero que olvide `Trim()` meta un
   duplicado que la base acepta.
2. **Los máximos de longitud viven en el dominio**, espejando `varchar(200)` y `varchar(60)`.
   Sin ese guardia, un valor largo llega a PostgreSQL y vuelve como `500 server.unexpected`:
   la forma exacta de `SDD-CT-06`.

**Divergencia declarada respecto de §Diseño:** el agregado expone `Update(name, code, ...)`,
no `Rename`/`Recode` por separado. El contrato `PUT` manda los dos campos juntos, así que dos
métodos obligarían al handler a decidir cuál llamar leyendo qué cambió — lógica en el lugar
equivocado. §Diseño queda corregido acá.

**Commiteado en `968c4a8`.**

### Tramo 3 — persistencia, migración y `GET` de productos (2026-08-10)

- **RED:** `ProductApiTests.cs` escrito antes del cableado. Literal:

  ```txt
  ProductApiTests.cs(5,23): error CS0234: El tipo o el nombre del espacio de nombres
  'Application' no existe en el espacio de nombres 'Modules.Catalog'
  ```

  El módulo existía pero no estaba enganchado al host: `Api.csproj` no lo referenciaba.

- **GREEN:** `Correctas! - Con error: 0, Superado: 3, Omitido: 0, Total: 3` contra
  PostgreSQL real (`postgres:18-alpine`, Testcontainers).
- **Build:** `dotnet build Backend.slnx` → `Compilación correcta. 0 Advertencia(s)`.
- **Sin regresión:** `ArchitectureTests` `16/16`; `Modules.Catalog.UnitTests` `13/13`;
  `Modules.Tenancy.IntegrationTests` `Con error: 5, Superado: 52, Total: 57` — **los 5 son
  los de `RealAuthenticationApiTests`**, o sea `SDD-CT-14`, preexistente. Verificado por
  nombre, no por conteo: `LogoutRevokesTheSessionCookie`,
  `MutatingRequestWithoutCsrfHeaderIsRejected`,
  `RoleDowngradeRemovesPermissionsOnTheNextRequest`,
  `SessionCookieAuthenticatesOrdinaryEndpointsWithoutTheBearerToken`,
  `SuspendingMembershipRevokesTheMembersActiveSession`. Importa porque este tramo tocó roles
  y permisos, y `AuthorizationCatalogApiTests` —que verifica ese catálogo— quedó en verde.

**Migración `20260811122715_InitialCatalog`:** crea el esquema `catalog`, la tabla
`catalog.products` con `varchar(200)`/`varchar(60)`, el índice `IX_products_tenant` y el
**único `IX_products_tenant_code`**. `Down()` borra la tabla: reversible, y sin datos previos
que perder. Generada con el factory de diseño (`CatalogDbContextFactory`), no con
`--startup-project src/Api`: `Api.csproj` no referencia `Microsoft.EntityFrameworkCore.Design`,
y así es como se generaron las de los demás módulos.

#### Dos defectos que encontró este tramo

**1. Faltaba registrar las políticas de autorización, y el síntoma era `500`, no `403`.**
`AddAuthorization` registra una política por permiso, a mano
(`QepServiceCollectionExtensions.cs:321`). Sin las cuatro de `catalog`,
`RequireAuthorization("catalog.product.read")` no resuelve y ASP.NET tira, así que las tres
pruebas daban `InternalServerError`. Un endpoint nuevo con permiso nuevo **siempre** necesita
las dos mitades: la constante y su política. Queda dicho acá porque el `500` no se parece en
nada a la causa.

**2. Una de las pruebas pasaba por la razón equivocada.** El stub de desarrollo concede sólo
los permisos de tenancy por defecto cuando no viene `X-Permissions`
(`DevelopmentAuthenticationHandler.ResolvePermissions`). `ListForAnotherTenantIsForbidden`
estaba en verde **sin tener el permiso de catálogo**: su `403` venía de la falta de permiso,
no del cruce de tenants, y habría seguido verde con el aislamiento roto. Corregido: el
llamador ahora **tiene** `catalog.product.read`, así que el `403` sólo puede venir de que el
tenant de la ruta no es el suyo. Es el mismo patrón de prueba de teatro que las revisiones de
`AUTH-05` y `AUTH-06` ya habían encontrado.

**Commiteado en `968c4a8`.**

### Tramo 4 — `CAT-02b`: escrituras (2026-08-10)

- **RED:** `ProductWriteApiTests.cs`, 12 pruebas escritas antes de los endpoints. **RED de
  runtime, no de compilación** —el archivo compilaba porque los DTOs ya existían—:

  ```txt
  Expected: Created
  Actual:   MethodNotAllowed
  ```

  `MethodNotAllowed` y no `NotFound` porque el grupo de rutas ya existía con el `GET`; el
  `POST` sobre la misma ruta cae en 405. Es el RED correcto: dice exactamente que falta el
  verbo, no la ruta.

- **GREEN:** `Correctas! - Con error: 0, Superado: 15, Omitido: 0, Total: 15` (las 3 de
  `CAT-02a` más las 12 nuevas), contra PostgreSQL real.
- **Build:** `Compilación correcta. 0 Advertencia(s)`.
- **Sin regresión:** `ArchitectureTests` 16/16, unitarias 13/13, y `Tenancy` con los mismos
  5 fallos de `SDD-CT-14`, verificados por nombre.

**Los 12 criterios de aceptación quedan cubiertos por prueba automatizada.** `CA-CAT-02-01`
y `-10`, que `CAT-02a` dejó a medias por no tener con qué sembrar, cierran acá.

#### Lo que trajo este tramo

`GET` por id, `POST`, `PUT` y `deactivate`, con validadores de FluentValidation para las dos
escrituras con texto libre. El validador **no** duplica al dominio por gusto: el agregado
lanza un código único y el validador es lo que produce el mapa `errors` por campo que
`ApiExceptionHandler` arma desde `ValidationException`, que es lo que un formulario necesita
para marcar el input culpable.

**La traducción del índice único, que era el hueco más serio de `CAT-02a`:** vive en
`CatalogUnitOfWork.SaveChangesAsync`, discrimina por **nombre de índice** además de
`SqlState`, y convierte la violación en `catalog.product.code_taken` → 422. Sin eso, un
código repetido devolvía `500`, que es la forma exacta de `SDD-CT-06`. `CatalogLayerTests`
sigue verificando que esa traducción no se filtre a Application.

#### Un defecto de prueba, corregido

`CatalogPermissionsArePublishedInTheAuthorizationCatalog` fallaba con `403`. No era el
producto: `/authorization/catalog` está guardado por `tenancy.membership.read`, cuya
definición dice "consultar membresías **y catálogo de roles/permisos**". Tener los permisos
de catálogo no alcanza para leer el catálogo que los publica. La prueba ahora pide también
ese permiso, con la razón escrita al lado.

#### Auditoría: por qué se asienta sobre el outbox

Las tres pruebas de auditoría consultan `platform.outbox_messages`, no `audit.entries`.
`catalog` usa el camino de outbox, así que `audit.entries` recién aparece cuando corre el
worker de proyección del módulo `Audit`: asertar ahí sería una carrera. El outbox commitea en
la misma transacción que el producto, que es justo la garantía que hay que probar.

**Commiteado en `CAT-02b`.**

### Tramos siguientes

- **RED:**
- **GREEN:**
- **Build:**
- **Runtime:**
- **Revisión:**
- **Commit:**

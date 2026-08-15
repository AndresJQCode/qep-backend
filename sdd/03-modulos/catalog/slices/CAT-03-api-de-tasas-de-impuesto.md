# `CAT-03` — API de tasas de impuesto

> **Estado:** **Complete** — 2026-08-13
> **Módulo:** `catalog` — ficha y gate en `qep-frontend/sdd/03-modulos/catalog/`
> (`SDD-ADR-08`: los enlaces relativos no cruzan repos)
> **Depende de:** `CAT-02` (`a` y `b` en `Complete`, 2026-08-11) · `CAT-00` (gate cerrado el
> 2026-08-10)
> **Bloqueado por:** ninguno. El contrato, los permisos y la precisión del porcentaje están
> ratificados en el gate cerrado
> **Repos afectados:** `qep-backend`
> **Tamaño estimado:** ~500-650 líneas autoradas. El umbral de `convenciones-de-id.md` es
> ~400, así que **el slice nace declarando su partición** — ver abajo. No se repite el error
> de `CAT-02`, que se midió en 1043 líneas recién al querer commitear

## Partición del slice

`CAT-02` se partió *después* de escribirlo. Acá se declara antes, porque la forma es la misma
—cinco endpoints sobre un recurso nuevo— y el andamiaje del módulo ya existe.

| Parte | Alcance | Umbral |
|---|---|---|
| `CAT-03a` | Dominio `TaxRate`, persistencia, migración `AddTaxRates`, permisos, y `GET T/tax-rates` + `GET T/tax-rates/{id}` | ~250 líneas |
| `CAT-03b` | Escrituras: `POST`, `PUT`, `deactivate`, validadores y pruebas de auditoría y outbox | ~300 líneas |

Ejecutan **en secuencia**: el repositorio sigue con un solo slice activo, que es `CAT-03`.

**Si al medir `CAT-03a` da menos de 400 líneas, no se parte.** La partición es una previsión,
no una obligación; el umbral se mide con `git diff --numstat` sobre `src/` y `tests/` antes de
commitear, sin lock files ni migración generada.

### Reparto de criterios de aceptación

- **`CAT-03a`:** `CA-CAT-03-01`, `-02`, `-07` (mitad de lectura), `-10`.
- **`CAT-03b`:** `-03` a `-06`, `-08`, `-09`, `-11`, y la mitad de escritura de `-07`.

## Objetivo

Un tenant puede listar, ver, crear, editar e inactivar las tasas de impuesto de su catálogo
contra la API real, con permisos propios y aislamiento entre tenants verificado por prueba.

## Fuera de alcance

- **La FK `Product.TaxRateId`.** Relacionar producto con tasa es `CAT-04`, junto con
  `descripción`, `imagen`, `precio` y `moneda`. Este slice entrega el recurso; el que lo
  consume es el siguiente. Meterlos juntos repite la medición de 1043 líneas de `CAT-02`.
- **Cálculo de totales y redondeo.** Es la mitad abierta de `P-008` y pertenece a `quotes`
  (`RN-013`). Acá sólo se guarda el porcentaje.
- **"Parámetros generales" de `RF-025`.** Fuera hasta que haya evidencia de qué son
  (`DECISIÓN-PENDIENTE-CAT-01`, abierta).
- **Todo el frontend.** Alinear `catalog.api.ts` es deuda declarada de `CAT-01`.
- **Búsqueda por texto.** `GET T/products` tiene `?search=`; `tax-rates` **no**. Una tasa de
  impuesto por tenant se cuenta con los dedos de una mano —el IVA colombiano es 19, 5 o 0—
  así que un filtro no resuelve un problema que exista. Se agrega el día que alguien lo pida
  con un caso.
- **Eventos de integración de `catalog`.** Misma razón que en `CAT-02`: `pricing` y `quotes`
  no existen en `qep-backend`, y publicar un contrato sin consumidor que lo valide es lo que
  `AGENTS.md` §2 prohíbe.
- Datos seed. Que un tenant nuevo arranque con "IVA 19%" cargado es decisión de producto, no
  de este slice.

## Contrato existente que se consume

Todo el andamiaje ya está construido por `CAT-02`. Este slice **no crea proyectos, ni
`DbContext`, ni unidad de trabajo, ni publicador de auditoría**: los reusa.

| Mecanismo | Dónde está | Qué aporta |
|---|---|---|
| `CatalogDbContext` | `Modules.Catalog.Infrastructure/Persistence/CatalogDbContext.cs` | esquema `catalog` y su tabla de outbox |
| `ICatalogUnitOfWork` | `Modules.Catalog.Application/ICatalogUnitOfWork.cs` | commit único; traduce violaciones de índice en Infrastructure |
| `ICatalogAuditPublisher` | `Modules.Catalog.Application/ICatalogAuditPublisher.cs` | emite `platform.audit.recorded.v1` al outbox del mismo `DbContext`, o sea **en la misma transacción** |
| `CatalogAuthorization.EnsureAuthorized` | `Modules.Catalog.Application/CatalogAuthorization.cs` | segunda capa: revalida tenant y permiso en el handler, **403 y nunca 404** |
| `CatalogDomainException` | `Modules.Catalog.Domain/CatalogDomainException.cs` | código de dominio → 422 vía `ApiExceptionHandler.cs:83-84` |
| `IExecutionContext` | `Modules.Tenancy.Application/IExecutionContext.cs` | `SubjectId`, `TenantId`, `HasPermission(string)` |
| Mapeo central de errores | `Api/ApiExceptionHandler.cs:68-90` | `DomainException` → 422 con `code`; `ResourceNotFoundException` → 404; `RequestForbiddenException` → 403; `ValidationException` → 422 `validation.failed` **con** el mapa `errors` |

**Ningún status HTTP se arma a mano en un handler.** El mapeo es central, y salirse de él es
el defecto que `ApiExceptionHandler` existe para evitar.

## Contrato que se expone

### Endpoints nuevos

Sea `T` = `/api/v1/tenants/{tenantId:guid}/catalog`. Las cinco rutas están **ratificadas en el
gate `CAT-00`**, cerrado el 2026-08-10; no se rediseñan acá.

| Método | Ruta | Permiso | Respuestas |
|---|---|---|---|
| `GET` | `T/tax-rates` | `catalog.tax_rate.read` | 200 `TaxRatesResponse` · 403 |
| `GET` | `T/tax-rates/{taxRateId:guid}` | `catalog.tax_rate.read` | 200 `TaxRateResponse` · 403 · 404 |
| `POST` | `T/tax-rates` | `catalog.tax_rate.manage` | 201 `TaxRateResponse` · 403 · 422 |
| `PUT` | `T/tax-rates/{taxRateId:guid}` | `catalog.tax_rate.manage` | 200 `TaxRateResponse` · 403 · 404 · 422 |
| `POST` | `T/tax-rates/{taxRateId:guid}/deactivate` | `catalog.tax_rate.manage` | 200 `TaxRateResponse` · 403 · 404 · 422 |

Cada una con `.RequireAuthorization(<permiso>)`, `.Accepts<>` donde haya cuerpo, `.Produces<>`
y `.ProducesProblem(...)` por estado — el patrón exacto de
`ProductEndpoints.cs:17-48`.

**Van en `TaxRateEndpoints.cs`, archivo propio**, sobre el mismo `MapGroup`. No se agregan a
`ProductEndpoints.cs`: son otro recurso, y un archivo que mapea dos recursos deja de decir su
nombre.

### DTOs

```csharp
public sealed record TaxRateResponse(
    Guid Id, string Name, int Percentage, bool IsActive,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record TaxRatesResponse(IReadOnlyCollection<TaxRateResponse> Items);

public sealed record CreateTaxRateRequest(string Name, int Percentage);

public sealed record UpdateTaxRateRequest(string Name, int Percentage);
```

**`Percentage` es `int`, no `decimal`.** `P-008`, decidido por el owner el 2026-08-10: el
porcentaje se guarda con **0 decimales**. Encaja con el IVA colombiano —19, 5 o 0— y el gate
lo declara como límite de alcance del módulo: no admite retenciones con fracción.

`IsActive` **no** viaja en los requests, igual que en `ProductResponse`: se crea activo y sólo
cambia por `/deactivate`. Un booleano editable convertiría la inactivación en un `PUT`
cualquiera y la dejaría sin auditoría propia.

Sin `code`: el modelo ratificado en el gate es **nombre, porcentaje e `isActive`**, y nada
más. `Product` tiene `code` porque `RF-020` lo sustenta; `RF-025` no dice nada equivalente
para tasas.

### Permisos que se declaran

| Permiso | Qué habilita | Roles |
|---|---|---|
| `catalog.tax_rate.read` | Listar y ver tasas | `tenancy.owner`, `tenancy.member` |
| `catalog.tax_rate.manage` | Crear, editar e inactivar tasas | `tenancy.owner` |

**Estos dos permisos ya existieron y se retiraron.** Estaban en `CatalogPermissions`,
registrados en roles y publicados en `/authorization/catalog`, **sin una sola línea que los
consumiera**; la revisión de 4 lentes de `CAT-02` los hizo quitar. El comentario de
`CatalogPermissions.cs:6-12` dice literal que "vuelven con `CAT-03`, junto a su
implementación". Este slice es ese momento.

**Un permiso nuevo necesita sus dos mitades, y se registran a mano una por una:**

1. La constante en `Modules.Catalog.Application/CatalogPermissions.cs`.
2. Su `PermissionDefinition` y su entrada de rol en
   `QepServiceCollectionExtensions.cs:142-155` y `:210-221`.
3. **Su `AddPolicy` en `AddAuthorization`** (`QepServiceCollectionExtensions.cs:359-364`).

Sin el punto 3, `RequireAuthorization` no resuelve la política y el síntoma es **500, no
403** — un error que no se parece en nada a su causa.

`tenancy.member` recibe sólo lectura. Cambiar una tasa mueve los totales de toda cotización:
es `high`, no operativo. Criterio ratificado en el gate.

### Eventos de integración

| Evento | Cuándo se emite | Consumidores previstos |
|---|---|---|
| `platform.audit.recorded.v1` | en cada alta, edición e inactivación | módulo `Audit` (worker de proyección) |

Mismo camino de outbox que `CAT-02`: administrar una tasa es operativo, no crítico de
seguridad, así que va por `ICatalogAuditPublisher` y no por `IAuditRecorder`.

### Migración

`AddTaxRates` — crea `catalog.tax_rates` dentro del esquema `catalog`, que ya existe desde
`InitialCatalog`. **No** toca `catalog.products`.

Columnas: `id` (uuid, PK, `ValueGeneratedNever`), `tenant_id` (uuid), `name` (varchar 120),
`percentage` (integer), `is_active` (bool), `version` (bigint, token de concurrencia),
`created_at`, `updated_at`.

Índices: uno por `tenant_id`, y **`IX_tax_rates_tenant_name`, único sobre `(tenant_id, name)`**
— ver `DECISIÓN-PENDIENTE-CAT-05` abajo.

La violación de ese índice se traduce **en Infrastructure** —`Modules.Catalog.Application` no
referencia EF Core ni Npgsql, y `CatalogLayerTests` lo verifica— a
`CatalogDomainException("catalog.tax_rate.name_taken", …)`, que sale como **422**.

**Se discrimina por nombre de índice, no sólo por `SqlState == "23505"`.** El esquema ya tiene
`IX_products_tenant_code`, y confundirlos manda a corregir el campo equivocado. Es literalmente
la lección de `SDD-CT-06`.

Generar la migración con el factory de diseño, no con `--startup-project`:

```powershell
dotnet ef migrations add AddTaxRates --project src/Modules/Catalog/Modules.Catalog.Infrastructure --context CatalogDbContext -o Persistence/Migrations
```

**Reversible:** `Down()` borra `catalog.tax_rates`. No hay datos previos que perder, y el
esquema `catalog` sobrevive porque lo creó `InitialCatalog`.

## Reglas que la implementación debe respetar

| Regla | Origen |
|---|---|
| Administrar tasas de impuesto | `RF-025`, `04-requisitos/requisitos-funcionales.md:32` (porción "tasas de impuesto"; "parámetros generales" queda fuera) |
| El porcentaje es entero de 0 decimales | `P-008`, cerrada por el owner el 2026-08-10; ratificada en el gate `CAT-00` |
| Cada registro pertenece a un tenant y se expone sólo dentro del tenant autenticado | ficha del módulo, "Datos y clasificación" |
| Autorización de doble capa: política en el endpoint **y** revalidación en el handler, con **403 y nunca 404** | `CatalogAuthorization`, aplicado en los cinco handlers de `CAT-02` |
| Todo método de repositorio recibe `tenantId` como primer parámetro | convención verificada del backend |
| Capas: nada de lógica en el endpoint | `SDD-ADR-05`, verificado por `CatalogLayerTests` |
| Traducir errores de base va en Infrastructure, no en Application | `SDD-CT-06`, cerrada |
| Auditoría y cambio en la misma transacción | `AGENTS.md` §7b; patrón en `CatalogAuditPublisher` |
| Un caso de uso con texto libre lleva validador de FluentValidation **aunque el dominio ya valide** | convención del backend: el dominio da el `code`, el validador da el **campo** |
| Token de concurrencia optimista desde el día uno | hallazgo `A` de la revisión de 4 lentes de `CAT-02`, al que llegaron dos lentes por separado |

## Diseño

**Backend** — capas según `SDD-ADR-05`. Todo lo que sigue son **archivos nuevos dentro de
proyectos que ya existen**; no se crea ningún `.csproj` ni se toca `Backend.slnx`.

```txt
src/Modules/Catalog/
  Modules.Catalog.Domain/
    TaxRate.cs                      agregado: Create, Update, Deactivate + invariantes
    TaxRateId.cs                    value object, calcado de ProductId
  Modules.Catalog.Application/
    CatalogPermissions.cs           (+) TaxRateRead, TaxRateManage
    ITaxRateRepository.cs
    CatalogDtos.cs                  (+) TaxRateDto
    ListTaxRates.cs / GetTaxRate.cs / CreateTaxRate.cs / UpdateTaxRate.cs / DeactivateTaxRate.cs
    CreateTaxRateValidator.cs / UpdateTaxRateValidator.cs
    TaxRateNotFound.cs
  Modules.Catalog.Infrastructure/
    Persistence/CatalogDbContext.cs (+) DbSet<TaxRate> y configuración
    Persistence/TaxRateRepository.cs
    Persistence/CatalogUnitOfWork.cs (+) rama de IX_tax_rates_tenant_name
    Persistence/Migrations/          AddTaxRates
    CatalogInfrastructureExtensions.cs (+) registro del repositorio
  Modules.Catalog.Api/
    TaxRateEndpoints.cs
src/Bootstrapper/
    QepServiceCollectionExtensions.cs (+) 2 PermissionDefinition, 2 entradas de rol, 2 AddPolicy
```

**El agregado `TaxRate`** se calca de `Product.cs`, con sus invariantes propias:

| Invariante | Código de dominio |
|---|---|
| Nombre requerido | `catalog.tax_rate.name_required` |
| Nombre de más de 120 caracteres | `catalog.tax_rate.name_too_long` |
| Porcentaje fuera de `0..100` | `catalog.tax_rate.percentage_out_of_range` |
| Editar una tasa inactiva | `catalog.tax_rate.inactive` |
| Inactivar una tasa ya inactiva | `catalog.tax_rate.already_inactive` |
| Nombre repetido en el tenant | `catalog.tax_rate.name_taken` (traducido en Infrastructure) |

Las constantes de ancho (`NameMaxLength = 120`) viven en el agregado y **espejan la columna**,
por la misma razón que en `Product.cs:9-13`: un valor demasiado largo falla como 422 con
código de dominio en vez de llegar a PostgreSQL y volver como 500.

**`Version` desde la primera migración.** `Product` lo tuvo que agregar en la corrección de la
revisión; acá nace con él, mapeado con `IsConcurrencyToken()`, y cada mutación lo incrementa.

**Frontend** — `N/A`. Este slice no toca `qep-frontend`.

## Criterios de aceptación

| ID | Criterio |
|---|---|
| `CA-CAT-03-01` | Dado un usuario con `catalog.tax_rate.read` en su tenant, cuando pide `GET T/tax-rates`, entonces recibe 200 con las tasas **de ese tenant únicamente** |
| `CA-CAT-03-02` | Dada una tasa del tenant A, cuando un usuario del tenant B pide `GET T/tax-rates/{id}` con el `tenantId` de A en la ruta, entonces recibe **403**, no 404 — el handler revalida antes de tocar el repositorio |
| `CA-CAT-03-03` | Dado un usuario **sin** `catalog.tax_rate.manage`, cuando hace `POST T/tax-rates`, entonces recibe 403 y no se persiste nada |
| `CA-CAT-03-04` | Dado un `POST T/tax-rates` válido, entonces responde **201** con la tasa creada, `isActive` en `true`, y deja **una** entrada de auditoría en el outbox, en la misma transacción |
| `CA-CAT-03-05` | Dado un `POST T/tax-rates` con `name` vacío, entonces responde **422** con el mapa `errors` por campo (validador de FluentValidation) |
| `CA-CAT-03-06` | Dado un `POST` o `PUT` con `percentage` fuera de `0..100` —probado con `-1` y con `101`—, entonces responde **422** con `code = catalog.tax_rate.percentage_out_of_range`, no 500 ni truncado silencioso |
| `CA-CAT-03-07` | Dado un `{id}` inexistente **dentro del tenant**, entonces `GET`, `PUT` y `deactivate` responden **404** |
| `CA-CAT-03-08` | Dada una tasa activa, cuando se hace `POST .../deactivate`, entonces responde 200 con `isActive` en `false` y deja auditoría |
| `CA-CAT-03-09` | Dada una tasa **ya inactiva**, cuando se vuelve a inactivar, entonces responde **422** con código de dominio, no 200 silencioso ni 500 |
| `CA-CAT-03-10` | Dado el catálogo de permisos, entonces `catalog.tax_rate.read` y `catalog.tax_rate.manage` aparecen en `GET /tenants/{id}/authorization/catalog` y en `authorization/me` según el rol — y **la política resuelve**, o sea que un llamador autorizado recibe 200 y no 500 |
| `CA-CAT-03-11` | Dada una tasa con `name` `X` en el tenant A, cuando se hace `POST T/tax-rates` con el mismo `name` en el tenant A, entonces responde **422** con `code = catalog.tax_rate.name_taken` — **no 500**. El mismo `name` en el tenant B se acepta: la unicidad es por tenant |

**`CA-CAT-03-10` tiene una mitad que parece de trámite y no lo es:** verificar que la política
resuelva. Es la prueba directa contra el defecto de "500 en vez de 403" por registrar la
constante y olvidar el `AddPolicy`.

## Pruebas requeridas

| Nivel | Qué verifica |
|---|---|
| Unitaria | `Modules.Catalog.UnitTests` — invariantes de `TaxRate`: nombre vacío, nombre largo, porcentaje `-1` y `101`, no se edita inactiva, no se inactiva dos veces, `Deactivate` avanza `UpdatedAt` e incrementa `Version` |
| Arquitectura | `CatalogLayerTests` — **sin archivo nuevo**: el módulo ya tiene el suyo y las reglas no cambian. Se corre para verificar que Application sigue sin referenciar EF Core tras tocar `CatalogUnitOfWork` |
| Integración | `Modules.Catalog.IntegrationTests` contra **PostgreSQL real vía Testcontainers** (requiere Docker arriba): los 11 CA, verificando **estado persistido y fila de auditoría en el outbox**, no sólo el status HTTP |
| Runtime | Los 11 criterios endpoint por endpoint contra la API local, con la auditoría confirmada en `audit.entries` tras la proyección — no por status HTTP |

**TDD obligatorio, RED antes que GREEN, con evidencia literal de ambos en este spec.**

**Y el RED tiene que fallar por el motivo correcto.** Es la lección más cara de `CAT-02`: la
primera prueba RED del hallazgo `D` **pasó en verde sin tocar el código**, porque describía un
llamador sin permiso a quien frena la política del endpoint antes de que el handler exista. Si
no se exige que el rojo sea el rojo esperado, la corrección se escribe contra un caso que ya
funcionaba y el defecto queda con una prueba en verde encima.

**Dos trampas de entorno, verificadas, que cuestan una vuelta cada una:**

- **`Api.exe` corriendo bloquea `dotnet build`, `dotnet test` y los comandos `ef`.** Detener el
  proceso antes. Se llama `Api.exe` porque `src/Api/Api.csproj` no declara `AssemblyName`.
- **La factory de integración debe fijar `Notifications:EmailProvider`** (`SDD-CT-17`). Sin
  eso hereda el `infobip` con credenciales vacías y **todas** las pruebas del archivo mueren al
  arrancar con `OptionsValidationException`, antes de su aserción.
- **El stub de desarrollo concede sólo los permisos de tenancy por defecto.** Una prueba que
  necesita `catalog.tax_rate.*` tiene que pedirlo por `X-Permissions`, o su 403 va a venir del
  permiso faltante y no de lo que cree estar probando.

**Regresión:** `SDD-CT-14` tiene 5 pruebas de `RealAuthenticationApiTests` en rojo, y son
preexistentes. Se verifican **por nombre, no por conteo**:
`RoleDowngradeRemovesPermissionsOnTheNextRequest`,
`MutatingRequestWithoutCsrfHeaderIsRejected`,
`SessionCookieAuthenticatesOrdinaryEndpointsWithoutTheBearerToken`,
`SuspendingMembershipRevokesTheMembersActiveSession`,
`LogoutRevokesTheSessionCookie`.

## Riesgos

| Riesgo | Cómo se detecta |
|---|---|
| Registrar la constante del permiso y olvidar su `AddPolicy` | `CA-CAT-03-10`. El síntoma sería **500**, que no se parece a su causa. Es el modo de falla documentado del backend |
| Fuga entre tenants por olvidar el filtro en una consulta | `CA-CAT-03-01` y `-02`, con datos de dos tenants sembrados |
| Colisión de `name` devolviendo **500** en vez de 422 | `CA-CAT-03-11`. Se evita traduciendo **por nombre de índice** en Infrastructure. Es la forma exacta de `SDD-CT-06` |
| Confundir `IX_tax_rates_tenant_name` con `IX_products_tenant_code` en la traducción | La rama nueva de `CatalogUnitOfWork` se prueba con las dos colisiones, no con una |
| Porcentaje negativo o mayor a 100 llegando a base | `CA-CAT-03-06`, con `-1` y `101` |
| Auditoría que no commitea con el cambio | Prueba que cuenta las filas de outbox tras la operación, no el status |
| El slice pasa las 400 líneas | Partición ya declarada arriba. Se mide con `git diff --numstat` antes de commitear |
| Publicar permisos sin implementación | Es exactamente lo que este slice viene a corregir. Los dos permisos entran **junto con** sus endpoints, en el mismo commit |

## Decisiones abiertas

| ID | Pregunta | Bloquea |
|---|---|---|
| ~~`DECISIÓN-PENDIENTE-CAT-05`~~ | **¿El `name` de una tasa es único por tenant?** `RF-025` no lo dice y no se asumió. **Cerrada el 2026-08-13 por el owner: sí, único por `(tenant_id, name)`.** Ratifica el default con el que se implementó: `IX_tax_rates_tenant_name` y traducción a `422 catalog.tax_rate.name_taken`. Fundamento: dos filas "IVA 19%" en el mismo tenant no son un dato válido con dos representaciones, son un error de carga — y quien arme una cotización elegiría una de las dos por orden incidental de base, justo lo que `RN-030`..`RN-037` prohíbe para precios. Precedente directo: `DECISIÓN-PENDIENTE-CAT-04`, lo mismo para `Product.code`. **Cero cambio de código** | ~~La migración `AddTaxRates`~~ — desbloqueado |
| ~~Hallazgo `B`~~ | **¿Desactivar una tasa libera su nombre?** **Cerrada el 2026-08-13 por el owner: no.** El índice único sigue **sin filtro parcial** por `is_active`, así que un nombre usado queda tomado aunque la tasa esté inactiva. Consecuencia aceptada, y conviene que quede escrita: recrear "IVA general" tras desactivarlo devuelve `422 catalog.tax_rate.name_taken` con la lista de activas sin mostrarlo. Es coherente con `Product.code`, que se comporta igual. **Cero cambio de código** | — |
| ~~Hallazgo `C`~~ | **¿`GET T/tax-rates` debe filtrar las inactivas?** **Cerrada el 2026-08-13 por el owner: no.** La lista devuelve activas e inactivas, con `isActive` en cada ítem para que el consumidor decida. Coherente con `GET T/products`. **Lo que esto traslada, y hay que tenerlo presente al construir cotizaciones:** filtrar una tasa desactivada es responsabilidad de quien arma el documento, no de este endpoint. **Cero cambio de código** | — |
| `DECISIÓN-PENDIENTE-CAT-06` | **Cuando exista `pricing` y una lista dé un valor distinto al de `Product.Price`, ¿cuál gana?** No bloquea `CAT-03` —acá no hay precio— pero sí `CAT-04`. **Default asumido, declarado para que no se decida por accidente: gana `pricing`; `Product.Price` es precio base y actúa de fallback cuando ninguna lista resuelve.** `RN-030`..`RN-037` exige que un precio ambiguo **falle cerrado**, así que la precedencia tiene que ser explícita antes de que exista el segundo origen | `CAT-04`, no este slice |

## Alcance que este slice corre en el gate `CAT-00`

El gate cerró declarando el modelo de `Product` con **"Ningún campo más"**. Las decisiones que
el owner tomó el 2026-08-12 lo modifican, y **eso tiene que quedar escrito en el gate**, no
sólo acá:

| Decisión | Efecto |
|---|---|
| `descripción`, `imagen`, `precio`, `moneda` entran a `Product` | Amplía el modelo del gate. Se implementa en `CAT-04` |
| `escala de precios` es de `pricing` | Confirma la frontera que el gate ya declaraba |
| **`stock` queda fuera del alcance del proyecto** | No tiene `RF` que lo sustente ni módulo en el mapa. No se implementa, y se registra para que no vuelva a proponerse sin requisito |

`CAT-03` no depende de ninguna de las tres: entrega el recurso `tax-rates`, cuyo contrato
estaba ratificado desde antes.

## Evidencia de cierre

Se completa al terminar. Ver `qep-frontend/sdd/00-metodo/definition-of-done.md`.

### Tramo 1 — dominio `TaxRate` (2026-08-13)

**RED**, literal, con `tests/.../TaxRateTests.cs` escrito y sin agregado:

```txt
TaxRateTests.cs(14,23): error CS0103: El nombre 'TaxRate' no existe en el contexto actual
TaxRateTests.cs(14,38): error CS0103: El nombre 'TaxRateId' no existe en el contexto actual
```

**GREEN**, tras `TaxRate.cs` y `TaxRateId.cs`:

```txt
Correctas! - Con error: 0, Superado: 31, Omitido: 0, Total: 31 - Modules.Catalog.UnitTests.dll
```

Eran **13** (los de `Product`), así que los **18 nuevos** pasan. Sin regresión de arquitectura:
`Correctas! - Con error: 0, Superado: 16, Total: 16 - ArchitectureTests.dll`.

Ocho invariantes con prueba, cada una con su código de dominio, más los **dos extremos válidos**
(`0` y `100`) que un guard escrito con `>`/`<` en vez de `>=`/`<=` dejaría afuera en silencio.

### Tramo 2 — persistencia, migración, permisos y los dos `GET` (`CAT-03a`, 2026-08-13)

`CatalogDbContext` suma `DbSet<TaxRate>` y su configuración, con `IX_tax_rates_tenant` e
`IX_tax_rates_tenant_name` **nombrados explícitamente** para que la traducción discrimine por
nombre de índice.

Migración generada con el factory de diseño:

```powershell
dotnet ef migrations add AddTaxRates --project src/Modules/Catalog/Modules.Catalog.Infrastructure --context CatalogDbContext -o Persistence/Migrations
```

`20260813135959_AddTaxRates.cs` crea `catalog.tax_rates` con las 8 columnas y los 2 índices;
`Down()` borra **sólo esa tabla** y el esquema `catalog` sobrevive, que es lo correcto porque lo
creó `InitialCatalog`.

**Los permisos volvieron con sus dos mitades**, que es el punto del slice: constantes en
`CatalogPermissions`, `PermissionDefinition` + entradas de rol, **y** sus dos `AddPolicy` en
`AddAuthorization`. Sin la tercera el síntoma habría sido 500, no 403.

### Tramo 3 — escrituras (`CAT-03b`, 2026-08-13)

`CreateTaxRate`, `UpdateTaxRate` y `DeactivateTaxRate` con sus validadores, calcados de los de
producto: **autorización antes que validación**, que es la corrección `D` de la revisión de
`CAT-02`. `CatalogUnitOfWork` suma la rama de `IX_tax_rates_tenant_name` → `422
catalog.tax_rate.name_taken`, **en rama propia y no en un `or`** con la de producto.

**Un cambio de comportamiento fuera del recurso, declarado:** el mensaje de
`concurrency.conflict` decía *"The product changed…"* y ahora dice *"The catalog record
changed…"*. El `catch` es compartido por los dos agregados, y decir "product" ante un conflicto
de tasa manda a mirar la entidad equivocada. El `code` —que es el contrato— no cambia.

```txt
dotnet build Backend.slnx
Compilación correcta.
    0 Advertencia(s)
    0 Errores
```

`dotnet format` sobre las rutas que tocó el slice: sin hallazgos.

### Una prueba de `CAT-02` que este slice borra, a propósito

`ProductWriteApiTests.TaxRatePermissionsAreNotPublishedBeforeTheirSliceExists` afirmaba que
`catalog.tax_rate` **no** aparece en `/authorization/catalog`. Era correcta mientras los permisos
estaban publicados sin implementación; su propio comentario decía *"Esta prueba se borra cuando
`CAT-03` los traiga con su implementación"*. Se borró, y su reemplazo es `CA-CAT-03-10`, que
afirma lo contrario **y además** verifica que la política resuelva.

### Tramo 4 — integración, y el defecto que sólo aparecía en runtime (2026-08-13)

Con Docker arriba, la primera corrida dio **18 de 18 en rojo**, todas con
`InternalServerError`, mientras las 18 de `Product` pasaban. El fallo llegaba disfrazado:

```txt
System.ArgumentNullException : Value cannot be null. (Parameter 'collection')
   at TaxRateApiTests.ListReturnsAnEmptyCatalogForANewTenant() line 32
```

**Ese mensaje no dice nada de la causa, y la culpa era del helper de la prueba.** `ListAsync`
deserializaba a `TaxRatesResponse` sin assertar el status: un `500` deserializa igual, con
`Items` en `null`, y el fallo sale como `ArgumentNullException` sobre una colección. Se corrigió
el helper para assertar `HttpStatusCode.OK` **antes** de leer el cuerpo, y recién ahí el fallo
dijo la verdad:

```txt
Assert.Equal() Failure: Values differ
Expected: OK
Actual:   InternalServerError
```

**La causa: los handlers se registran uno por uno a mano, no por escaneo de ensamblado.**
`QepServiceCollectionExtensions` los declara explícitamente
(`services.AddScoped<IQueryHandler<ListProductsQuery, …>, ListProductsHandler>()`), y los cinco
de tasas faltaban. El caso de uso compila, el endpoint mapea, la política resuelve — y el
`IRequestDispatcher` no encuentra a quién despachar, así que **500**.

**Es el mismo modo de falla que un permiso sin su `AddPolicy`, y hay que anotarlo como tal: en
este backend, un caso de uso nuevo tiene DOS mitades que se registran a mano, igual que los
permisos.** El síntoma no se parece a la causa en ninguno de los dos.

**GREEN, tras registrar los cinco handlers:**

```txt
Correctas! - Con error: 0, Superado: 36, Omitido: 0, Total: 36 - Modules.Catalog.IntegrationTests.dll
```

Eran 18 (los de `Product`); los 18 nuevos cubren los 11 criterios.

### Regresión de toda la solución (2026-08-13)

```txt
dotnet test Backend.slnx
Con error! - Con error: 5, Superado: 52, Total: 57 - Modules.Tenancy.IntegrationTests.dll
Correctas!  - Con error: 0, Superado: 36, Total: 36 - Modules.Catalog.IntegrationTests.dll
Correctas!  - Con error: 0, Superado: 31, Total: 31 - Modules.Catalog.UnitTests.dll
Correctas!  - Con error: 0, Superado: 16, Total: 16 - ArchitectureTests.dll
(resto de los ensamblados, todos en verde)
```

**233 en verde, 5 en rojo.** Los 5 son los de `SDD-CT-14`, verificados **por nombre y no por
conteo**: `LogoutRevokesTheSessionCookie`,
`RoleDowngradeRemovesPermissionsOnTheNextRequest`,
`SessionCookieAuthenticatesOrdinaryEndpointsWithoutTheBearerToken`,
`MutatingRequestWithoutCsrfHeaderIsRejected`,
`SuspendingMembershipRevokesTheMembersActiveSession`. **Cero regresión.**

`dotnet format` sobre las rutas del slice: sin hallazgos.

### Tramo 5 — revisión de 4 lentes y su corrección (2026-08-13)

**Limitación declarada, y no es menor: fue una autorrevisión.** El método pide cuatro lentes
**ciegos entre sí**; acá los cuatro los aplicó quien escribió el código, así que comparte sus
puntos ciegos. Vale menos que la revisión de `CAT-02`, donde dos lentes independientes
convergieron en el mismo hallazgo — y esa convergencia fue justamente lo que lo movió a
bloqueante. Queda como deuda de método del slice.

Cinco hallazgos. **Uno bloqueante, corregido.**

#### `A` — BLOQUEANTE, corregido. El token de concurrencia no tenía ninguna prueba que lo ejercitara

`Version` se implementó desde la primera migración y el spec lo vendía como mejora sobre
`Product`, que lo tuvo que agregar después. **Pero lo único que lo respaldaba era esto:**

```csharp
taxRate.Update("IVA reducido", 5, Now.AddMinutes(5));
Assert.Equal(2, taxRate.Version);
```

Eso demuestra que **un contador en memoria incrementa**. No demuestra que `IsConcurrencyToken()`
esté mapeado, que el `UPDATE` lleve la versión en su `WHERE`, ni que
`DbUpdateConcurrencyException` se traduzca a `RequestConcurrencyException`. `Product` sí tenía
esa prueba (`EditingAProductDeactivatedMidFlightIsRefusedInsteadOfOverwritingIt`); `CAT-03`
copió la columna y no la prueba.

**Es la misma familia que el peor hallazgo de `CAT-02`:** una afirmación con una prueba verde
encima que no prueba lo afirmado.

**Corrección:** `EditingATaxRateDeactivatedMidFlightIsRefusedInsteadOfOverwritingIt`, calcada
del escenario de `Product` — el competidor va por la API para pasar por el dominio, e
intercalado en vez de en paralelo porque una carrera de dos requests no falla de forma
reproducible.

**Y se verificó que la prueba prueba algo**, que es lo que `CAT-02` enseñó a exigir. Quitando
temporalmente `.IsConcurrencyToken()` del `CatalogDbContext`:

```txt
Assert.Throws() Failure: No exception was thrown
Expected: typeof(BuildingBlocks.Application.RequestConcurrencyException)
```

Sin el token, la edición perdida **entra igual**. Restaurado el mecanismo:

```txt
Correctas! - Con error: 0, Superado: 37, Total: 37 - Modules.Catalog.IntegrationTests.dll
```

#### Seguimiento — no corregido acá, y dos de los tres son decisión de producto

- **`B` — desactivar una tasa no libera su nombre.** `IX_tax_rates_tenant_name` es único sobre
  `(tenant_id, name)` **sin filtro parcial** por `is_active`. Desactivar "IVA general" y volver a
  crearlo devuelve `422 catalog.tax_rate.name_taken` mientras la lista de activas no muestra
  ninguno: el error dice "ya existe" y el usuario no ve nada. **No lo introdujo este slice** —
  `Product.code` tiene lo mismo— pero lo duplicó. Es decisión de producto: o índice parcial
  `WHERE is_active`, o el mensaje lo dice explícito.
- **`C` — `GET T/tax-rates` devuelve también las inactivas**, sin filtro ni parámetro. Consistente
  con `ListProducts`, pero acá pesa más: quien arme una cotización recibe una tasa desactivada en
  la lista y elegirla **mueve los totales**. El gate no lo especifica y ningún `CA` lo cubre.
- **`D` — el reparto por rol no tiene prueba.** `CA-CAT-03-10` verifica que los permisos aparezcan
  en el catálogo, no que `tenancy.member` sea **sólo lectura**. El gate ratificó ese reparto
  explícitamente porque cambiar una tasa es `high`. Hoy, mover `TaxRateManage` a `tenancy.member`
  pasa las 37 pruebas.
- **`E` — radio ampliado del mensaje veneno de `AuditProjectionWorker`.** Aborta el lote entero
  ante el primer mensaje que no parsea. `CAT-02` sumó un segundo productor a la cola compartida;
  `CAT-03` suma un tercero. **No es de este slice** y sigue mereciendo su `SDD-CT`, pero cada
  slice que pasa lo hace más caro.

### Tramo 6 — runtime contra la API local (2026-08-13)

Levantada con el stub, contra el PostgreSQL real del developer:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://localhost:5000"
$env:Authentication__UseDevelopmentStub = "true"
dotnet run --project src/Api --no-launch-profile
```

Tenants de prueba `…000c31` (A) y `…000c32` (B). **Los 11 criterios, endpoint por endpoint:**

| Criterio | Verificado en vivo | Resultado |
|---|---|---|
| `CA-CAT-03-01` | `GET T/tax-rates` en A y en B | A devuelve **3**, B devuelve **1**. Aislamiento real, con datos sembrados en los dos |
| `CA-CAT-03-02` | B pide `GET` de un id de A, **con** el permiso | **403**, no 404 |
| `CA-CAT-03-03` | `POST` con sólo `tax_rate.read` | **403**, y sin fila |
| `CA-CAT-03-04` | `POST` válido | **201**, `isActive: true`, `createdAt == updatedAt` |
| `CA-CAT-03-05` | `POST` con `name` en blanco | **422** `validation.failed` con `errors: {"Name": [...]}` |
| `CA-CAT-03-06` | `percentage` `-1` y `101` | **422** las dos, con `errors: {"Percentage": [...]}` |
| `CA-CAT-03-06` | `percentage` `0` y `100` | **201** las dos — los extremos son válidos |
| `CA-CAT-03-07` | id inexistente en `GET`, `PUT`, `deactivate` | **404**, **404**, **404** |
| `CA-CAT-03-08` | `POST .../deactivate` | **200** con `isActive: false` |
| `CA-CAT-03-09` | `deactivate` por segunda vez | **422** `catalog.tax_rate.already_inactive` |
| `CA-CAT-03-10` | `GET /authorization/catalog` | **200**, con `catalog.tax_rate.read` y `.manage`. Y las dos políticas resuelven: ningún 500 |
| `CA-CAT-03-11` | mismo `name` dos veces en A | **422 `catalog.tax_rate.name_taken`** — **no 500**. El mismo `name` en B: **201** |

Extra, no numerado: editar una tasa inactiva devuelve **422 `catalog.tax_rate.inactive`**.

`CA-CAT-03-11` es el que más valía verificar en vivo: es la forma exacta de `SDD-CT-06`, y en
runtime confirma que la discriminación **por nombre de índice** distingue
`IX_tax_rates_tenant_name` de `IX_products_tenant_code`.

#### La verificación que el status HTTP no da

```sql
SELECT payload->>'action', COUNT(*) FROM platform.outbox_messages
WHERE event_name = 'platform.audit.recorded.v1'
  AND payload->>'action' LIKE 'catalog.tax_rate%' GROUP BY 1;
```

```txt
 catalog.tax_rate.created     | 4
 catalog.tax_rate.deactivated | 1
 catalog.tax_rate.updated     | 1
```

**4 filas de alta = los 4 `201`, ni una más.** El `403` y los **seis** `422` —name en blanco,
`-1`, `101`, `name_taken`, `already_inactive`, `inactive`— **no dejaron rastro**, y los tres
`404` tampoco. La atomicidad queda probada por lo que **no** escribió, que es la única forma de
probarla: una prueba que sólo mira el status HTTP deja pasar el efecto que importa.

Estado persistido, coherente con lo anterior:

```txt
 01900000-…-000c31 | 3 tasas | 2 activas
 01900000-…-000c32 | 1 tasa  | 1 activa
```

#### Dos hallazgos del runtime, ninguno bloqueante

1. **El mapa `errors` sale en el idioma del sistema operativo del servidor.** Observado:
   `"'Name' no debería estar vacío."` y `"'Percentage' debe estar entre 0 y 100."`. **Es el
   mismo hallazgo 2 del runtime de `CAT-02`**, sin corregir: el `code` es contrato estable, el
   texto no. Que reaparezca en un recurso nuevo confirma que es sistémico y no del slice.
2. **Levantar el entorno local tiene una dependencia no documentada en el README:** la cadena de
   user-secrets apunta a `localhost:5433`, que es el contenedor `postgres18` del developer, no al
   `compose.yaml` de este repo —que crea `qep` en `5432`—. Con Docker abajo, la API muere al
   arrancar en `TenancyDatabaseInitializer` con `Failed to connect to 127.0.0.1:5433`. **El
   `compose.yaml` del repo no sirve para este entorno tal como está configurado.**

### Cierre — `CAT-03` cumple el DoD y está `Complete` (2026-08-13)

Las tres decisiones que faltaban las cerró el owner el 2026-08-13, y **las tres ratifican lo
implementado**: `name` único por tenant, desactivar **no** libera el nombre, y la lista **no**
filtra las inactivas. **Cero cambio de código.**

| Ítem del DoD | Estado |
|---|---|
| Spec con criterios de aceptación | ✅ 11, todos con prueba |
| TDD con RED y GREEN literales | ✅ tramo 1 |
| Unitarias | ✅ `31/31` |
| Arquitectura | ✅ `16/16`, sin regresión |
| Integración contra PostgreSQL real | ✅ `37/37` |
| Regresión de toda la solución | ✅ 233 en verde; los 5 rojos son `SDD-CT-14`, por nombre |
| `dotnet format` | ✅ sin hallazgos en las rutas del slice |
| Runtime contra la API local | ✅ 11 de 11, con auditoría verificada en base |
| Revisión de riesgo | ⚠️ hecha, **pero como autorrevisión** — ver abajo |
| Ítems de UI | `N/A` — es un slice de backend; su interfaz sería trabajo de `CAT-01`, en el otro repo |

**Deuda de método que el slice se lleva declarada, y no se disimula:** la revisión del tramo 5
la hizo quien escribió el código, no cuatro lentes ciegos entre sí. Encontró un bloqueante real
—`Version` sin prueba que lo ejercitara— pero no puede dar la garantía que dio la de `CAT-02`,
donde **dos lentes independientes convergieron** en el mismo hallazgo y esa convergencia fue lo
que lo movió a bloqueante. No sabemos cuántos hallazgos no vio.

#### Datos de prueba que quedaron

Cuatro tasas en `catalog.tax_rates` de `dev_lulo_crm_v2`, en los tenants `…000c31` y `…000c32`,
con sus 6 filas de outbox. No se borraron: no hay endpoint de baja y un `DELETE` por SQL sobre la
base del developer no es de esta sesión. Mismo criterio que dejó los cuatro productos de
`CAT-02`.

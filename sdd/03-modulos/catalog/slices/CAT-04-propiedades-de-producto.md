# `CAT-04` — Propiedades nuevas de producto

> **Estado:** In Progress — código y pruebas cerrados; **falta sólo el gate `CAT-00`**, que vive en `qep-frontend`
> **Módulo:** `catalog` — ficha y gate en `qep-frontend/sdd/03-modulos/catalog/`
> (`SDD-ADR-08`: los enlaces relativos no cruzan repos)
> **Depende de:** `CAT-03` (`Complete`, 2026-08-13) — el campo `impuesto` es una FK a `TaxRate`
> **Bloqueado por:** el gate `CAT-00` declara el modelo de `Product` con **"Ningún campo más"**.
> Ver «Alcance que este slice corre en el gate»
> **Repos afectados:** `qep-backend`
> **Tamaño estimado:** ~350-450 líneas autoradas. Un solo agregado, una migración, sin recurso
> nuevo ni permisos nuevos: **no se prevé partición**, pero se mide antes de commitear

## Objetivo

`Product` deja de tener sólo nombre, código y estado: suma **descripción, imagen, precio, moneda
y tasa de impuesto**, expuestas por los endpoints que `CAT-02` ya publicó.

## De dónde sale este slice

El owner pidió el 2026-08-12 que `Product` tuviera *nombre, descripción, código, imagen, estado,
precio, impuesto, moneda, stock y escala de precios*. Contrastada esa lista contra la
descomposición de módulos y contra el código, quedó repartida así:

| Campo pedido | Resolución | Dónde |
|---|---|---|
| `nombre`, `código`, `estado` | Ya existían | `CAT-02` |
| `impuesto` | Necesitaba que `TaxRate` existiera | `CAT-03`, cerrado |
| **`descripción`, `imagen`, `precio`, `moneda`** | **Este slice** | `CAT-04` |
| `escala de precios` | Es de `pricing` | **No se implementa**: `pricing` está `Definido`, con `PRI-00` abierto, y un módulo sin gate cerrado no recibe código |
| `stock` | **Fuera del alcance del proyecto**, decidido por el owner el 2026-08-12 | — |

**Por qué `stock` salió, porque conviene que quede escrito:** no tenía `RF` que lo sustentara ni
módulo en el mapa de seis, y como `int` en `Product` mutado desde `orders` reproducía el mismo
*lost update* que la revisión de 4 lentes encontró en `CAT-02`, pero sin historial para
auditarlo. Si vuelve, vuelve como movimiento de inventario, no como columna.

## Fuera de alcance

- **`stock` y `escala de precios`**, por lo de arriba.
- **Resolución de precio contra `pricing`.** Este slice guarda un **precio base**; elegir qué
  precio aplica a un cliente es de `pricing`. Ver `DECISIÓN-PENDIENTE-CAT-06`.
- **Subir el archivo de imagen.** Eso ya lo hace `Storage` con su propio flujo de sesión de
  carga. Acá sólo se guarda **cuál** de los archivos del producto es su imagen principal.
- **Cálculo de totales y redondeo.** Es la mitad abierta de `P-008` y pertenece a `quotes`
  (`RN-013`).
- **Todo el frontend.** Alinear `catalog.api.ts` es deuda declarada de `CAT-01`.
- **Migrar datos existentes.** Los campos nacen opcionales; ver «Migración».

## Contrato existente que se consume

| Mecanismo | Dónde está | Qué aporta |
|---|---|---|
| `TaxRate` y su repositorio | `Modules.Catalog.Domain/TaxRate.cs`, `ITaxRateRepository` | el agregado al que apunta `Product.TaxRateId`, **en el mismo módulo y el mismo esquema** |
| `Storage`, flujo de carga | `Modules.Storage.Api/StorageEndpoints.cs:22-34` | `POST /files` → sesión de carga, `POST /files/{id}/complete`. `CreateFileRequest` lleva **`OwnerId` + `OwnerType`** |
| `CatalogAuthorization`, `ICatalogUnitOfWork`, `ICatalogAuditPublisher` | `Modules.Catalog.Application` | sin cambios: este slice no agrega permisos ni recursos |

**Nada de esto se crea. `CAT-04` no agrega permisos, ni endpoints, ni proyectos**: enriquece los
cinco endpoints que `CAT-02` ya publicó, bajo `catalog.product.read` / `.manage`.

## Contrato que se expone

### Endpoints

**Ninguno nuevo.** Los cinco de `CAT-02` cambian su forma de cuerpo:

| Método | Ruta | Qué cambia |
|---|---|---|
| `GET` | `T/products` | `ProductResponse` suma 5 campos |
| `GET` | `T/products/{productId:guid}` | ídem |
| `POST` | `T/products` | `CreateProductRequest` acepta los 5, todos opcionales |
| `PUT` | `T/products/{productId:guid}` | `UpdateProductRequest` ídem |
| `POST` | `T/products/{id}/deactivate` | sin cambio de cuerpo de entrada |

**Es un cambio compatible hacia atrás en la entrada** —los campos nuevos son opcionales— y **no
compatible en la salida**, porque `ProductResponse` suma propiedades. Un consumidor que
deserialice estricto se rompe; `catalog.api.ts` del frontend hoy ni siquiera apunta a estas
rutas (deuda de `CAT-01`), así que no hay consumidor real que romper.

### DTOs

```csharp
public sealed record ProductResponse(
    Guid Id, string Name, string Code, bool IsActive,
    string? Description,
    Guid? ImageFileId,
    decimal? Price,
    string? Currency,
    Guid? TaxRateId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CreateProductRequest(
    string Name, string Code,
    string? Description, Guid? ImageFileId,
    decimal? Price, string? Currency, Guid? TaxRateId);

public sealed record UpdateProductRequest(
    string Name, string Code,
    string? Description, Guid? ImageFileId,
    decimal? Price, string? Currency, Guid? TaxRateId);
```

### Las cinco decisiones de diseño, con su razón

**1. `Description` — `string?`, máximo 2000.** Opcional. No hay `RF` que la exija obligatoria y
forzarla rompería los productos ya cargados.

**2. `ImageFileId` — `Guid?`, referencia blanda a `Storage`. Sin FK.**

Acá hay una trampa que conviene explicar, porque el diseño obvio es el equivocado.
`Storage.CreateFileRequest` ya lleva **`OwnerId` + `OwnerType`**: el archivo apunta al producto,
no al revés. La tentación es no poner nada en `Product` y preguntarle a `Storage` "dame los
archivos de este producto".

**No alcanza, porque son dos preguntas distintas.** `Storage` responde *qué archivos pertenecen
a este producto* —pueden ser varios—; `Product.ImageFileId` responde *cuál de ellos es la imagen
principal*. Un producto con cinco fotos sigue teniendo una sola portada, y esa elección es dato
del catálogo, no del almacenamiento.

**Y va sin FK de base a propósito:** `Modules.Catalog` no puede tener una foreign key contra
`storage.file_resources` sin violar *"ningún módulo lee las tablas de otro módulo"*
(`descomposicion-de-modulos.md`). Es un `Guid` sin constraint, como cualquier referencia entre
módulos de este monolito.

**3. `Price` — `decimal?`, columna `numeric(18,2)`.**

Dos decimales cubren COP —que en la práctica no usa centavos— y cualquier moneda que sí. **El
redondeo de totales no se decide acá**: es la mitad abierta de `P-008` y pertenece a `quotes`
(`RN-013`). Este slice sólo almacena.

**4. `Currency` — `string?`, exactamente 3 caracteres, ISO-4217, normalizado a mayúsculas.**

**Se implementa como campo de producto por decisión del owner, contra la recomendación de este
spec, y queda registrado.** La recomendación era que la moneda viviera en la configuración del
tenant, siguiendo el precedente que declara `descomposicion-de-modulos.md` —*"los prefijos,
DIVIPOLA, COP y plantillas son seed/configuración de tenant, nunca enums globales"*—. El riesgo
que se acepta: un catálogo puede quedar con precios en tres monedas distintas que después nadie
sabe sumar. El owner listó `moneda` entre los campos de producto tres veces, incluida una
posterior a la recomendación.

**Invariante que lo acota, y es la parte que salva el caso:** `Price` y `Currency` van juntos.
Un precio sin moneda es un número sin unidad, y una moneda sin precio no dice nada.

**5. `TaxRateId` — `Guid?`, FK real contra `catalog.tax_rates`.**

Acá **sí** hay FK, porque las dos tablas viven en el esquema `catalog` del mismo módulo.

**Pero la FK no alcanza, y esto es lo importante del slice:** una foreign key no sabe nada de
tenants. Sin una comprobación explícita, un producto del tenant A podría apuntar a una tasa del
tenant B — la FK la aceptaría, porque la fila existe. **El handler tiene que verificar que la
tasa pertenezca al tenant del producto antes de asignarla**, con `ITaxRateRepository.FindAsync`,
que ya recibe `tenantId` como primer parámetro justamente para esto.

Es una fuga entre tenants que ninguna prueba de status HTTP encuentra, y tiene su criterio de
aceptación propio (`CA-CAT-04-07`).

### Permisos

**Ninguno nuevo.** `catalog.product.read` y `catalog.product.manage`, ya registrados.

### Migración

`AddProductDetails` — cinco columnas nuevas en `catalog.products`, **todas nullable**:

| Columna | Tipo |
|---|---|
| `description` | `varchar(2000)` null |
| `image_file_id` | `uuid` null |
| `price` | `numeric(18,2)` null |
| `currency` | `char(3)` null |
| `tax_rate_id` | `uuid` null, **FK → `catalog.tax_rates(id)`**, `ON DELETE RESTRICT` |

**Nullable no es pereza: es lo único correcto acá.** Hay productos cargados —cuatro en la base
del developer, y en producción los que haya— y una columna `NOT NULL` sin default los rompe. Un
default inventado sería peor: un precio `0` es un dato falso que se ve igual que uno real.

**`ON DELETE RESTRICT` y no `CASCADE`:** borrar una tasa no debe borrar productos. Además no hay
endpoint de baja de tasas —se inactivan—, así que el caso no debería darse; el `RESTRICT` está
por si alguien borra por SQL.

**Reversible:** `Down()` quita las cinco columnas. **Pierde los datos que se hayan cargado en
ellas**, cosa que la migración de creación de `CAT-02`/`CAT-03` no arriesgaba. Se declara.

## Reglas que la implementación debe respetar

| Regla | Origen |
|---|---|
| Administrar productos activos/inactivos | `RF-020` |
| Un producto sólo puede apuntar a una tasa **de su propio tenant** | Aislamiento de doble capa, ficha del módulo. **No lo garantiza la FK** |
| `Price` y `Currency` van juntos o no van | Invariante de este spec |
| Las listas de precios y su vigencia son de `pricing` | `descomposicion-de-modulos.md`; comentario de `Product.cs:4-5` |
| Ningún módulo lee las tablas de otro | `descomposicion-de-modulos.md`. Por eso `image_file_id` no lleva FK |
| Traducir errores de base va en Infrastructure | `SDD-CT-06` |
| Autorización antes que validación | Corrección `D` de la revisión de `CAT-02` |
| Auditoría y cambio en la misma transacción | `AGENTS.md` §7b |
| Token de concurrencia ya existente, y **su prueba** | Hallazgo `A` de la revisión de `CAT-03`: la columna sin prueba de integración no está probada |

## Diseño

Todo son **archivos ya existentes**; no se crea ningún proyecto ni endpoint.

```txt
src/Modules/Catalog/
  Modules.Catalog.Domain/Product.cs            (+) 5 propiedades, invariantes y firma de Create/Update
  Modules.Catalog.Application/
    CatalogDtos.cs                             (+) 5 campos en los 3 records de producto
    ProductMapping.cs                          (+) mapeo
    CreateProduct.cs / UpdateProduct.cs        (+) validadores y la verificación de tenant de la tasa
  Modules.Catalog.Infrastructure/
    Persistence/CatalogDbContext.cs            (+) configuración de las 5 columnas y la FK
    Persistence/Migrations/                    AddProductDetails
  Modules.Catalog.Api/ProductEndpoints.cs      (+) los 5 campos en request y response
```

**Códigos de dominio nuevos:**

| Invariante | Código |
|---|---|
| Descripción de más de 2000 caracteres | `catalog.product.description_too_long` |
| Precio negativo | `catalog.product.price_negative` |
| Moneda que no son 3 letras | `catalog.product.currency_invalid` |
| Precio sin moneda, o moneda sin precio | `catalog.product.price_currency_mismatch` |
| Tasa inexistente o de otro tenant | `catalog.product.tax_rate_not_found` |

**Frontend** — `N/A`.

## Criterios de aceptación

| ID | Criterio |
|---|---|
| `CA-CAT-04-01` | Dado un `POST T/products` con los cinco campos, entonces responde **201** y `GET` los devuelve tal como se enviaron |
| `CA-CAT-04-02` | Dado un `POST T/products` **sin ninguno** de los cinco, entonces responde **201** con los cinco en `null` — siguen siendo opcionales |
| `CA-CAT-04-03` | Dado un `PUT` que manda `null` en un campo que tenía valor, entonces el campo queda en `null` — se puede limpiar, no sólo setear |
| `CA-CAT-04-04` | Dado un `price` negativo, entonces **422** con el mapa `errors` por campo |
| `CA-CAT-04-05` | Dado un `currency` de 2 o 4 caracteres, entonces **422**. `"cop"` en minúsculas se acepta y se persiste como `"COP"` |
| `CA-CAT-04-06` | Dado un `price` sin `currency` —y su inverso—, entonces **422** `catalog.product.price_currency_mismatch` |
| `CA-CAT-04-07` | **Dado un `taxRateId` de una tasa del tenant B, cuando un usuario del tenant A crea o edita un producto con él, entonces responde 422 `catalog.product.tax_rate_not_found` y no persiste** — la FK sola lo aceptaría |
| `CA-CAT-04-08` | Dado un `taxRateId` inexistente en cualquier tenant, entonces **422**, no 500 por violación de FK |
| `CA-CAT-04-09` | Dado un `taxRateId` de una tasa **inactiva** del propio tenant, entonces se acepta — inactivar una tasa no rompe los productos que ya la usaban |
| `CA-CAT-04-10` | Dado un producto con los cinco campos, cuando se edita, entonces deja **una** entrada de auditoría en el outbox, en la misma transacción |
| `CA-CAT-04-11` | Dados los productos ya existentes antes de la migración, entonces siguen legibles con los cinco campos en `null` |

`CA-CAT-04-07` es el criterio que justifica el slice tener revisión de riesgo: es una fuga entre
tenants que la base de datos no impide.

## Pruebas requeridas

| Nivel | Qué verifica |
|---|---|
| Unitaria | Invariantes de `Product`: descripción larga, precio negativo, moneda inválida, normalización a mayúsculas, precio-sin-moneda y moneda-sin-precio, y que `Update` siga incrementando `Version` |
| Arquitectura | `CatalogLayerTests` — sin archivo nuevo; se corre para verificar que Application sigue sin referenciar EF Core |
| Integración | Los 11 CA contra PostgreSQL real, **incluido `CA-CAT-04-11`**, que exige sembrar un producto, aplicar la migración y leerlo |
| Runtime | Los 11 criterios contra la API local, con la auditoría verificada **en base** |

**TDD obligatorio, RED antes que GREEN, y el RED tiene que fallar por el motivo correcto.**

**Y una exigencia que sale de la revisión de `CAT-03`:** la prueba de `CA-CAT-04-07` tiene que
**probarse contra el mecanismo ausente**. Si al quitar la verificación de tenant en el handler la
prueba sigue verde, la prueba no sirve — es exactamente lo que pasó con el token de concurrencia,
que tenía una prueba en verde que no lo ejercitaba.

**Trampas de entorno, verificadas:**

- **`Api.exe` corriendo bloquea `dotnet build`, `dotnet test` y los comandos `ef`.**
- **La factory de integración debe fijar `Notifications:EmailProvider`** (`SDD-CT-17`).
- **Para levantar la API local hay que arrancar el contenedor `postgres18`** (`5433`), **no**
  `docker compose up`: el `compose.yaml` del repo crea `qep` en `5432` y la cadena de
  user-secrets apunta a `5433`. Con Docker abajo la API muere en `TenancyDatabaseInitializer`.
  Hallazgo del runtime de `CAT-03`, no documentado en el README.
- **Regresión:** los 5 fallos de `SDD-CT-14` se verifican **por nombre, no por conteo**.

## Riesgos

| Riesgo | Cómo se detecta |
|---|---|
| **Producto apuntando a una tasa de otro tenant** | `CA-CAT-04-07`, y probando que la prueba falle sin la verificación |
| `taxRateId` inexistente saliendo como **500** por violación de FK | `CA-CAT-04-08`. Se traduce en Infrastructure, por nombre de constraint — `SDD-CT-06` otra vez |
| Romper los productos existentes con columnas `NOT NULL` | `CA-CAT-04-11`, con datos sembrados antes de migrar |
| Precio sin moneda entrando a base | `CA-CAT-04-06`, invariante de dominio **y** validador |
| `Down()` pierde datos | Declarado en «Migración». No es reversible sin pérdida, a diferencia de `CAT-02`/`CAT-03` |
| Cambio no compatible en `ProductResponse` | Declarado. Hoy no hay consumidor real: `catalog.api.ts` apunta a otras rutas (deuda de `CAT-01`) |

## Decisiones abiertas

| ID | Pregunta | Bloquea |
|---|---|---|
| `DECISIÓN-PENDIENTE-CAT-06` | **Cuando exista `pricing` y una lista dé un valor distinto al de `Product.Price`, ¿cuál gana?** **Default asumido y declarado: gana `pricing`; `Product.Price` es precio base y actúa de fallback cuando ninguna lista resuelve.** `RN-030`..`RN-037` exige que un precio ambiguo **falle cerrado**, así que la precedencia tiene que ser explícita antes de que exista el segundo origen. No bloquea el código de este slice —hoy hay un solo origen— pero sí su contrato | El contrato, no la implementación |
| `DECISIÓN-PENDIENTE-CAT-07` | **¿La moneda es por producto o por tenant?** Se implementa **por producto**, por decisión del owner y contra la recomendación de este spec. Se registra como decisión abierta y no como cerrada porque la consecuencia —un catálogo con precios en monedas mezcladas— recién se ve cuando haya datos reales, y revertirla después es migración con pérdida | Nada hoy |

## Alcance que este slice corre en el gate `CAT-00`

**Esto es un bloqueo formal y hay que resolverlo antes de dar por cerrado el slice.**

El gate `CAT-00`, cerrado el 2026-08-10, declara el modelo de `Product` como `id`, `name`,
`code`, `isActive` y agrega, literal: **"Ningún campo más"**. `CAT-04` lo contradice.

| Qué hay que escribir en el gate | Estado |
|---|---|
| `Product` suma `description`, `imageFileId`, `price`, `currency`, `taxRateId` | **pendiente** |
| `stock` queda **fuera del alcance del proyecto** | **pendiente** |
| `escala de precios` es de `pricing`, no de `catalog` | ya lo declaraba; se confirma |

**El gate vive en `qep-frontend/sdd/03-modulos/catalog/gate.md`**, que es autoridad del otro
repositorio y tiene su propio developer. **No se toca desde este lado sin acordarlo.** Mientras
no se escriba, el gate y el código dicen cosas distintas, y `SDD-ADR-01` manda que gane el
código y **se corrija el documento**.

## Evidencia de cierre

Se completa al terminar. Ver `qep-frontend/sdd/00-metodo/definition-of-done.md`.

### Tramo 1 — dominio: `ProductDetails` y las cinco propiedades (2026-08-13)

**RED**, literal, con las pruebas escritas y sin implementación:

```txt
ProductTests.cs(153,66): error CS0103: El nombre 'ProductDetails' no existe en el contexto actual
ProductTests.cs(155,29): error CS1061: "Product" no contiene una definición para "Description"
ProductTests.cs(157,29): error CS1061: "Product" no contiene una definición para "Price"
ProductTests.cs(158,29): error CS1061: "Product" no contiene una definición para "Currency"
ProductTests.cs(159,29): error CS1061: "Product" no contiene una definición para "TaxRateId"
```

**GREEN:**

```txt
Correctas! - Con error: 0, Superado: 44, Total: 44 - Modules.Catalog.UnitTests.dll
```

Eran **31**; los **13 nuevos** cubren los invariantes de `ProductDetails`.

**`ProductDetails` como value object y no cinco parámetros sueltos.** La razón menor es que la
firma de `Create` llegaría a diez argumentos. La que importa: la invariante «precio y moneda van
juntos» **cruza dos campos**, y suelta habría que repetirla en `Create` y en `Update`, o dejarla
sin dueño.

### Tramo 2 — persistencia y migración (2026-08-13)

`AddProductDetails` — cinco columnas **nullable** en `catalog.products`, más
`IX_products_tax_rate_id` y la FK `FK_products_tax_rates_tax_rate_id` con
`ReferentialAction.Restrict`.

**`currency` quedó `character(3)` con `IsFixedLength()`**, no `varchar`: un código ISO-4217
alfabético siempre tiene tres letras, y el tipo lo dice mejor que una validación sola.

`Down()` quita las cinco columnas, la FK y el índice. **Pierde los datos cargados en ellas** —a
diferencia de las migraciones de creación de `CAT-02` y `CAT-03`, que no arriesgaban nada—.
Declarado.

### Tramo 3 — integración (2026-08-13)

```txt
Correctas! - Con error: 0, Superado: 50, Total: 50 - Modules.Catalog.IntegrationTests.dll
```

Eran **37**; los **13 nuevos** cubren los 11 criterios.

#### La prueba de `CA-CAT-04-07`, verificada contra el mecanismo ausente

Es la exigencia que dejó la revisión de `CAT-03`: una prueba verde sobre un mecanismo presente
es indistinguible de una prueba verde sobre un mecanismo ausente, salvo que se compruebe.

Anulada la verificación de tenant en `ProductTaxRateResolver`:

```txt
Assert.Equal() Failure: Values differ
Expected: UnprocessableEntity
Actual:   Created
```

**`Created`.** Sin la comprobación, un producto del tenant A que apunta a una tasa del tenant B
**se crea con un 201 perfectamente normal**: la foreign key lo acepta porque la fila existe. La
fuga entre tenants queda demostrada en vivo, no argumentada. Restaurada la verificación, `50/50`.

**Esto es lo que la FK no puede darte, y por qué el criterio existe.**

#### Regresión de toda la solución

```txt
dotnet test Backend.slnx
Con error! - Con error: 5, Superado: 52, Total: 57 - Modules.Tenancy.IntegrationTests.dll
Correctas!  - Con error: 0, Superado: 50, Total: 50 - Modules.Catalog.IntegrationTests.dll
Correctas!  - Con error: 0, Superado: 44, Total: 44 - Modules.Catalog.UnitTests.dll
Correctas!  - Con error: 0, Superado: 16, Total: 16 - ArchitectureTests.dll
(resto de los ensamblados, todos en verde)
```

**260 en verde, 5 en rojo.** Los 5 son los de `SDD-CT-14`, verificados **por nombre**. Cero
regresión. `dotnet format` sin hallazgos.

### Tramo 4 — runtime contra la API local: 11 de 11 (2026-08-15)

Los 11 criterios verificados endpoint por endpoint contra `http://localhost:5099` con
PostgreSQL real, con el stub de desarrollo encendido.

**Cómo se levantó, que tiene una trampa nueva:** `launchSettings.json` **pisa** las variables
de entorno. Sus dos perfiles fijan `Authentication__UseDevelopmentStub=false` y
`applicationUrl=http://localhost:5000`, así que exportar `Authentication__UseDevelopmentStub=true`
antes de `dotnet run` **no alcanza**. Hay que pedir `--no-launch-profile`:

```powershell
$env:Authentication__UseDevelopmentStub = "true"
$env:ASPNETCORE_URLS = "http://localhost:5099"
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/Api --no-build --no-launch-profile
```

El contenedor `postgres18` (`5433`) ya estaba arriba y las migraciones al día —
`No migrations were applied. The database is already up to date.`

| Criterio | Resultado |
|---|---|
| `CA-CAT-04-01` | **201**, y el cuerpo devuelve los cinco campos tal como se enviaron |
| `CA-CAT-04-02` | **201** con los cinco en `null` |
| `CA-CAT-04-03` | **200**, los cinco quedan en `null`, verificado con un `GET` independiente |
| `CA-CAT-04-04` | **422** `validation.failed` con `errors.Price` |
| `CA-CAT-04-05` | **422** con `errors.Currency` para 2 y 4 caracteres; `"cop"` → **201** y persiste `"COP"` |
| `CA-CAT-04-06` | **422** `catalog.product.price_currency_mismatch` en los dos sentidos — **pero sin el mapa `errors`**, ver hallazgo `A` |
| `CA-CAT-04-07` | **422** `catalog.product.tax_rate_not_found` **y no persiste** |
| `CA-CAT-04-08` | **422**, no 500 por violación de FK |
| `CA-CAT-04-09` | **201** con una tasa **inactiva** del propio tenant |
| `CA-CAT-04-10` | **Una** entrada de auditoría por escritura, en la misma transacción |
| `CA-CAT-04-11` | Los 4 productos del 2026-08-11 siguen legibles con los cinco en `null` |

**`CA-CAT-04-11` salió con datos reales, no sembrados.** Los 4 productos que dejó el runtime
de `CAT-02` el 2026-08-11 son anteriores a la migración `AddProductDetails` del 2026-08-13, y
se leen con las cinco columnas en `NULL`. Es la prueba que las de integración simulan.

**`CA-CAT-04-10` es la evidencia más fuerte, y se probó por lo que NO escribió.** El outbox
—`platform.outbox_messages`, no una tabla de `catalog`— quedó con exactamente 5 entradas del
runtime: 4 `catalog.product.created` y **1** `catalog.product.updated`. Los **7 pedidos
rechazados no dejaron ninguna**. Y el `occurred_at` del `updated` —`10:43:32.567328`— es el
mismo instante que el `updatedAt` que devolvió el `PUT`: misma transacción, no dos escrituras.

```txt
platform.audit.recorded.v1 | catalog.product.created | 10:43:22.765231
platform.audit.recorded.v1 | catalog.product.created | 10:43:22.949561
platform.audit.recorded.v1 | catalog.product.updated | 10:43:32.567328
platform.audit.recorded.v1 | catalog.product.created | 10:43:42.594974
platform.audit.recorded.v1 | catalog.product.created | 10:44:01.670894
```

La no-persistencia de `CA-CAT-04-07` se verificó en base, no por el status: `select count(*)`
sobre los 7 códigos rechazados devuelve **0**.

### Tramo 5 — revisión con 4 lentes ciegos (2026-08-15)

Esta vez **no** fue autorrevisión: cuatro lentes independientes, sin verse entre sí, sobre el
commit `85b87c8`. Salda la deuda de método que dejó `CAT-03`.

| Lente | Hallazgos |
|---|---|
| **Riesgo** | **Ninguno.** Confirmó que `ProductTaxRateResolver` cubre los dos caminos de escritura y que `CA-CAT-04-07` está probado contra el mecanismo ausente |
| **Fiabilidad** | 1 — hallazgo `A` |
| **Resiliencia** | 2 — hallazgos `B` y `C` |
| **Legibilidad** | 3 — hallazgos `D`, `E` y `F` |

**Que el lente de riesgo saliera limpio es el resultado que importa**, porque la frontera de
aislamiento entre tenants era la razón declarada para exigir esta revisión.

**Hallazgo `A` — el `422` de `CA-CAT-04-06` no lleva el mapa `errors`.** Ni
`CreateProductValidator` ni `UpdateProductValidator` tienen una regla que empareje `Price` y
`Currency`, así que ese caso lo rechaza **sólo** el invariante de dominio: sale como
`DomainException` con `code`, sin el mapa por campo que produce `ValidationException`. Los
otros dos invariantes de `CAT-04` en el mismo validador —precio negativo, largo de moneda— sí
tienen regla y sí devuelven el mapa. **Contradice la tabla de «Riesgos» de este spec**, que
pide «invariante de dominio **y** validador». Lo encontraron dos lentes por separado y el
runtime lo confirmó en vivo. La prueba de integración no lo detectó porque afirma sobre el
status y el código, nunca sobre `errors`.

**Hallazgo `B` — la violación de FK no está traducida.** `CatalogUnitOfWork` traduce las dos
violaciones de índice único pero no `23503` sobre `FK_products_tax_rates_tax_rate_id`, que
esta migración estrena con `RESTRICT`. Hoy **no hay endpoint que borre una tasa** —sólo
`deactivate`—, así que no es alcanzable por HTTP; se activa si alguien borra por SQL, que es
justo el escenario para el que el `RESTRICT` se puso.

**Hallazgo `C` — `ApiExceptionHandler` devuelve `exception.Message` sin distinguir entorno.**
Preexistente, pero el camino lo estrena esta FK: un error no traducido filtraría nombres de
constraint y de tabla al llamador, también en producción. **No es de `catalog`**, así que no
se corrige acá.

**Hallazgos `D`, `E` y `F` — legibilidad.** (`D`) Los tres bloques de reglas nuevas están
duplicados textualmente entre `CreateProductValidator` y `UpdateProductValidator`, y cambiar
uno solo deja `POST` y `PUT` con validaciones distintas. (`E`) `ProductDetails` es un record
posicional con `Description` y `Currency` —los dos `string?`— en posiciones **no adyacentes**,
y todos los llamadores lo construyen posicionalmente: intercambiarlos compila. (`F`) La regla
de `Currency` sólo comprueba `Length(3)`, mientras el dominio además exige letras, así que
`"123"` atraviesa el validador y lo rechaza el dominio sin mapa por campo — la misma forma
que el hallazgo `A`.

### Tramo 6 — corrección de los hallazgos `A`, `B`, `D`, `E` y `F` (2026-08-15)

Una sola transacción de corrección, con RED antes que GREEN. **El RED falló por el motivo
correcto en los dos frentes**: las cuatro pruebas de validación con
`Assert.Equal() Failure: Strings differ / Expected: "validation.failed"` —o sea, llegaba el
código de dominio y no el de validación, que es exactamente lo que dice el hallazgo `A`— y la de
la foreign key con `Assert.Throws() Failure: Exception type was not an exact match ----
Microsoft.EntityFrameworkCore.DbUpdateException`, sin traducir. Literal:

```txt
Con error! - Con error: 5, Superado: 13, Omitido: 0, Total: 18 - Modules.Catalog.IntegrationTests.dll
```

| Hallazgo | Corrección | Archivo |
|---|---|---|
| `A` | Regla que empareja `Price` y `Currency`, **en los dos sentidos**, cada uno apuntando al campo que hay que corregir | `ProductWriteRules.cs` |
| `F` | `Matches("^[A-Za-z]{3}$")` además de `Length(3)`: el dominio exige letras y el validador comprobaba sólo el largo | `ProductWriteRules.cs` |
| `D` | Las reglas se escriben **una vez** en `ProductWriteRules` y los dos validadores las incluyen con `Include()`. `CreateProductCommand` y `UpdateProductCommand` implementan `IProductWriteCommand` | `ProductWriteRules.cs`, `CreateProduct.cs`, `UpdateProduct.cs` |
| `E` | `ProductDetails` deja de ser posicional: propiedades `init`, construcción sólo por nombre. Intercambiar `Description` y `Currency` ya no compila | `ProductDetails.cs` |
| `B` | **Se traduce.** Rama para `23503` sobre `FK_products_tax_rates_tax_rate_id`, discriminando por nombre de constraint como manda `SDD-CT-06` | `CatalogUnitOfWork.cs` |

**Decisión sobre `B`, que el tramo 5 dejó abierta: se traduce, no se declara deuda.** Cuesta una
rama de diez líneas, y el argumento para postergarla —«hoy no hay endpoint que borre una tasa»—
es justamente la condición que puede cambiar sin que nadie se acuerde de esta rama. Devuelve el
**mismo** código que `ProductTaxRateResolver` a propósito: para el llamador es el mismo problema
—la tasa que pidió no está— y darle dos códigos distintos según qué capa lo detectó lo obligaría
a manejar los dos.

**Cambio de contrato que la corrección de `A` produce, y hay que declararlo.** `CA-CAT-04-06`
pedía `422` con el código `catalog.product.price_currency_mismatch`. Con validador, ese caso
pasa a `422 validation.failed` **con el mapa `errors`**. No es una desviación: es lo que la
tabla de «Riesgos» de este spec pedía —«invariante de dominio **y** validador»— y lo que ya
hacían los otros dos invariantes de `CAT-04`, precio negativo y largo de moneda, que siempre
tuvieron regla. **El criterio y la tabla de «Riesgos» se contradecían entre sí**, que es lo que
el hallazgo `A` señaló; gana la tabla. El código de dominio sigue vivo como red de abajo para
quien llame al agregado sin pasar por el validador, y lo cubren las unitarias de `ProductTests`.

**GREEN, literal:**

```txt
Correctas! - Con error: 0, Superado: 44, Omitido: 0, Total: 44 - Modules.Catalog.UnitTests.dll
Correctas! - Con error: 0, Superado: 16, Omitido: 0, Total: 16 - ArchitectureTests.dll
Correctas! - Con error: 0, Superado: 55, Omitido: 0, Total: 55 - Modules.Catalog.IntegrationTests.dll
```

**Regresión de toda la solución: 265 en verde.** Los únicos 5 fallos son los de `SDD-CT-14`,
verificados **por nombre** y no por conteo:

```txt
Modules.Tenancy.IntegrationTests.RealAuthenticationApiTests.LogoutRevokesTheSessionCookie
Modules.Tenancy.IntegrationTests.RealAuthenticationApiTests.MutatingRequestWithoutCsrfHeaderIsRejected
Modules.Tenancy.IntegrationTests.RealAuthenticationApiTests.RoleDowngradeRemovesPermissionsOnTheNextRequest
Modules.Tenancy.IntegrationTests.RealAuthenticationApiTests.SessionCookieAuthenticatesOrdinaryEndpointsWithoutTheBearerToken
Modules.Tenancy.IntegrationTests.RealAuthenticationApiTests.SuspendingMembershipRevokesTheMembersActiveSession
```

`dotnet build Backend.slnx` → `Compilación correcta. 0 Advertencia(s)`. `dotnet format
--verify-no-changes` no reporta ninguno de los seis archivos tocados; los 22 archivos que el
repo tiene sucios son deuda preexistente y siguen abiertos.

**Trampa de entorno nueva, verificada hoy:** `dotnet restore` falla con
`error NU1903: ... "SSH.NET" 2025.1.0 tiene una vulnerabilidad de gravedad alta`, tratada como
error. Entra por `Testcontainers.PostgreSql` y **frena todos los proyectos de integración**. Se
esquiva en local con `-p:NuGetAudit=false`, que es lo que se usó acá; **no se tocó ninguna
configuración del repo**. Es preexistente, no lo introdujo este tramo, y va a frenar CI: queda
como seguimiento propio, fuera de este slice.

### Lo que falta para `Complete`

| Falta | Estado |
|---|---|
| ~~Resolver los hallazgos `A`, `D`, `E`, `F`~~ | **Hecho en el tramo 6.** |
| ~~Decidir sobre `B`~~ | **Decidido: se traduce.** Tramo 6 |
| **Escribir el alcance en el gate `CAT-00`** | **Abierto.** Vive en `qep-frontend/sdd/03-modulos/catalog/gate.md`, autoridad del otro repositorio y con su propio developer. **Sin acordar, no se toca** |

**Es el único bloqueo que queda, y no es técnico.** El gate declara el modelo de `Product` con
`id`, `name`, `code`, `isActive` y **"Ningún campo más"**; el código tiene cinco campos más.
`SDD-ADR-01` manda que gane el código y **se corrija el documento**. Lo que hay que escribir
está en «Alcance que este slice corre en el gate `CAT-00`», más arriba, y son tres filas.

`C` **no pertenece a este slice**: es de `src/Api/ApiExceptionHandler.cs` y afecta a todos los
módulos. Va como `DECISIÓN-PENDIENTE` propia.

**Seguimiento nuevo, tampoco de este slice:** `NU1903` sobre `SSH.NET`, que frena el restore de
los proyectos de integración y va a frenar CI.

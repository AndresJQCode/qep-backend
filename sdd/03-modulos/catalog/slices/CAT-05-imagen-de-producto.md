# `CAT-05` — Imagen de producto: el pegamento con `Storage`

> **Estado:** **Complete** — 2026-08-15
> **Módulo:** `catalog` — ficha y gate en `qep-frontend/sdd/03-modulos/catalog/`
> (`SDD-ADR-08`: los enlaces relativos no cruzan repos)
> **Depende de:** `CAT-04` (`Complete`, 2026-08-15) — introdujo `Product.ImageFileId`
> **Bloqueado por:** nada. El gate `CAT-00` ya declara `imageFileId` en el modelo desde su
> corrección del 2026-08-15 (`38e5abe` en `qep-frontend`)
> **Repos afectados:** `qep-backend`
> **Partición declarada antes de escribir código:** `CAT-05a` y `CAT-05b`. Ver «Partición»

## Objetivo

Que subir una imagen y asignarla a un producto sea una operación **con garantías**. Hoy las dos
mitades existen y no se hablan: `Storage` sabe subir archivos y `Product.ImageFileId` sabe
guardar un `Guid`, pero **nadie verifica que ese `Guid` sea un archivo real, de este tenant, ya
subido, y que sea una imagen**.

## De dónde sale este slice

El owner pidió el 2026-08-15 *"API para subir imágenes y asignarlas a productos"*. Contrastado
contra el código antes de escribir nada, la capacidad **ya existía en sus dos mitades**:

| Mitad | Dónde | Estado |
|---|---|---|
| Subir el archivo | `Modules.Storage.Api/StorageEndpoints.cs` — sesión de carga, URL prefirmada, `complete`, variantes con ImageSharp, publicación | **Existe y funciona** |
| Guardar cuál es la portada | `Product.ImageFileId` (`CAT-04`) | **Existe**, expuesto en `POST`, `PUT` y `GET` |
| **Que la segunda verifique a la primera** | — | **No existe. Es este slice** |

## El hueco, y por qué es de riesgo

`ImageFileId` entra por el request y se guarda **sin una sola comprobación**. No se verifica que
el archivo exista, que esté `Available`, que sea una imagen, ni **que sea del mismo tenant**.

Es **la misma fuga** que `ProductTaxRateResolver` cierra para `TaxRateId`, con su `CA-CAT-04-07`
y la revisión de riesgo que `CAT-04` llevó por ella. Para la imagen no hay nada: un producto del
tenant A puede apuntar al archivo del tenant B y la respuesta es un `201` perfectamente normal.

**Y hay una diferencia que la empeora respecto de la tasa:** `TaxRateId` al menos tiene una FK de
base que garantiza que la fila exista. `ImageFileId` es referencia **blanda, sin FK** —no puede
tenerla, porque cruzaría a otro módulo—, así que **no hay ninguna red debajo**.

**Segundo hueco, más chico pero de la misma familia:** `StorageEndpoints.cs:84-86` parsea el
`OwnerType` con `Enum.TryParse` y, si falla, **cae en silencio a `FileOwnerType.User`**. Mandar
`"Product"` hoy guarda `User` y devuelve `201`. Un valor inválido no falla: se convierte en otro.
Y `FileOwnerType` ni siquiera **tiene** `Product`: sólo `User`, `Entity` y `System`.

## Fuera de alcance

- **Subir el archivo.** Es de `Storage` y ya funciona. Este slice no toca el flujo de carga.
- **Borrar la imagen de `Storage` al desasignarla.** Ver `DECISIÓN-PENDIENTE-CAT-08`.
- **Varias imágenes por producto.** `Storage` ya responde *qué archivos son de este producto* por
  `OwnerId`/`OwnerType`; `Product.ImageFileId` responde *cuál es la portada*, que es una sola.
- **Todo el frontend.** Los cinco campos de `CAT-04` todavía no están en `qep-frontend`, y
  `catalog.api.ts` apunta a rutas sin `tenantId`. Es fila del ledger del **otro** repo.
- **Firmar URLs de descarga por producto.** Ver decisión 4.

## Decisiones de diseño, con su razón

**1. `catalog` NO referencia `Modules.Storage.Application`. Puerto acá, adaptador en el
composition root.**

La forma obvia es que `Modules.Catalog.Application` referencie `Modules.Storage.Application` y
llame a `IFileResourceRepository`. Tiene precedente —`Storage` referencia a `Tenancy` por
`IExecutionContext`— y ningún `ArchitectureTest` lo prohíbe.

**Se hace al revés a propósito.** `Catalog` declara su propio puerto, `IProductImageLookup`, con
el vocabulario que **`Catalog`** necesita, y `Bootstrapper` —que ya referencia los dos módulos y
cuyo trabajo es exactamente ése— provee el adaptador. Así el acoplamiento entre dos módulos de
negocio queda en el composition root y no adentro de un módulo: `catalog` compila sin `storage`,
y el día que las imágenes vengan de otro lado se cambia el adaptador y nada más.

**2. Las tres reglas, y cuáles comparten código de error.**

| Regla | Código |
|---|---|
| El archivo no existe, **o es de otro tenant** | `catalog.product.image_not_found` |
| Existe y es del tenant, pero no está `Available` | `catalog.product.image_not_available` |
| Existe, es del tenant, está `Available`, pero no es imagen | `catalog.product.image_not_an_image` |

**Las dos primeras condiciones comparten código a propósito**, por la misma razón que en
`ProductTaxRateResolver`: distinguir «no existe» de «es de otro tenant» le **confirma** al
llamador que el id existe en otro tenant, que es justo lo que la frontera esconde.

**«Es imagen» se decide por `MimeType` que empiece con `image/`**, y no por la lista blanca de
`FileUploadPolicy`: esa lista es de `Storage` y mezcla PDF y Office con las imágenes.
Preguntarle a `Storage` «¿esto es una imagen?» sería meter una regla de catálogo en el otro
módulo.

**3. `FileOwnerType.Product = 4`, y el fallback silencioso se termina.**

El valor va **al final**, con número propio.

> **Corregido el 2026-08-15, al implementar.** Este párrafo decía que el enum se persiste como
> `int` y que por eso no se renumera. **Es falso:** `StorageDbContext` lo mapea con
> `HasConversion<string>()` sobre `character varying(20)`, así que se guarda el **nombre**. Lo
> que no se puede cambiar, entonces, es el nombre de un valor ya usado —renombrarlo deja las
> filas viejas ilegibles—; el número es interno. La conclusión no cambia: agregar `Product` es
> seguro. La razón sí, y `SDD-ADR-01` manda corregir el documento.

Un `OwnerType` que no parsea pasa a **422
`storage.file.owner_type_invalid`** en vez de convertirse en `User`. Es cambio de contrato de
`Storage` y está declarado: hoy un cliente que manda basura recibe `201` y un archivo mal
clasificado, que es peor que un error.

**4. `imageUrl` en la respuesta del producto: la pública, o `null`.**

Sin esto, pintar una grilla de 20 productos son 20 llamadas a `POST /files/{id}/download-url`.
Se expone la **URL pública** del archivo, que existe cuando fue publicado
(`PUT /files/{id}/publication`), y `null` cuando no.

**No se firman URLs de descarga por producto.** Una URL prefirmada tiene expiración y hay que
generarla una por archivo: sirve para un documento privado, no para la portada de un catálogo
que se pinta en una grilla. Una imagen de producto que se quiere ver, se publica.

## Partición, declarada antes de escribir código

`CAT-02` se midió en **1043 líneas** recién al querer commitear y hubo que partirlo con el código
ya escrito; `CAT-03` declaró la partición antes y salió derecho. Se sigue el segundo.

| ID | Alcance | Por qué corta acá |
|---|---|---|
| **`CAT-05a`** | El puerto, el resolver, las tres reglas, `FileOwnerType.Product` y el fin del fallback silencioso | **Es la parte de riesgo.** Cierra la fuga entre tenants y es lo que justifica la revisión |
| **`CAT-05b`** | `imageUrl` en el `GET` por id y en el listado | Es comodidad de lectura, no una garantía. Si `CAT-05a` se demora, esto puede esperar sin que nada quede inseguro |

## Contrato que se expone

**Ningún endpoint nuevo, ningún permiso nuevo.** `CAT-05a` sólo agrega rechazos a los dos
endpoints de escritura que `CAT-02` publicó; `CAT-05b` agrega un campo a la respuesta.

```diff
  public sealed record ProductResponse(
      Guid Id, string Name, string Code, bool IsActive,
      string? Description, Guid? ImageFileId,
+     string? ImageUrl,
      decimal? Price, string? Currency, Guid? TaxRateId,
      DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
```

`ImageFileId` **se mantiene** en la respuesta: es el dato que el cliente manda de vuelta en el
`PUT`. `ImageUrl` es derivado y de sólo lectura.

## Criterios de aceptación

| ID | Criterio | Tramo |
|---|---|---|
| `CA-CAT-05-01` | **Dado un `imageFileId` de un archivo del tenant B, cuando un usuario del tenant A crea o edita un producto con él, entonces responde 422 `catalog.product.image_not_found` y no persiste** | `a` |
| `CA-CAT-05-02` | Dado un `imageFileId` inexistente en cualquier tenant, entonces **422** `catalog.product.image_not_found` — el mismo código que `-01`, a propósito | `a` |
| `CA-CAT-05-03` | Dado un archivo del propio tenant en `PendingUpload`, entonces **422** `catalog.product.image_not_available` — una portada que todavía no se subió no es una portada | `a` |
| `CA-CAT-05-04` | Dado un archivo del propio tenant, `Available`, pero `application/pdf`, entonces **422** `catalog.product.image_not_an_image` | `a` |
| `CA-CAT-05-05` | Dado un archivo `Available` con `image/png` del propio tenant, entonces **201**, y `GET` devuelve el `imageFileId` | `a` |
| `CA-CAT-05-06` | Dado un `imageFileId` en `null`, entonces se acepta — el campo sigue siendo opcional, y el `PUT` lo puede **limpiar** | `a` |
| `CA-CAT-05-07` | Dado `POST /files` con `ownerType: "Product"`, entonces el archivo queda con `OwnerType.Product` — hoy queda como `User` | `a` |
| `CA-CAT-05-08` | Dado `POST /files` con un `ownerType` que no existe, entonces **422** `storage.file.owner_type_invalid` — hoy devuelve **201** con `User` | `a` |
| `CA-CAT-05-09` | Dado un producto cuya imagen está publicada, entonces `GET` devuelve `imageUrl` con la URL pública | `b` |
| `CA-CAT-05-10` | Dado un producto cuya imagen **no** está publicada, entonces `imageUrl` viene en `null` y `imageFileId` sigue viniendo | `b` |
| `CA-CAT-05-11` | Dado un listado de productos con imagen, entonces cada uno trae su `imageUrl` resuelto | `b` |

`CA-CAT-05-01` es el criterio que justifica que este slice lleve **revisión de riesgo con lentes
ciegos**: es una fuga entre tenants que ninguna prueba de status HTTP encuentra por su cuenta, y
`CAT-04` sentó el precedente de no autorrevisarla.

## Pruebas requeridas

| Nivel | Qué verifica |
|---|---|
| Unitaria | Las tres reglas del resolver, con un doble del puerto |
| Arquitectura | `CatalogLayerTests` — **más una aserción nueva**: `Modules.Catalog.Application` **no** referencia `Modules.Storage.*`. Sin ella, la decisión 1 es un comentario, no una regla |
| Integración | Los 11 CA contra PostgreSQL real |
| Runtime | Los 11 criterios contra la API local |

**TDD obligatorio, RED antes que GREEN, y el RED tiene que fallar por el motivo correcto.**

**Y la exigencia que `CAT-03` dejó y `CAT-04` cumplió:** la prueba de `CA-CAT-05-01` tiene que
**probarse contra el mecanismo ausente**. Si al quitar la verificación de tenant del resolver la
prueba sigue verde, la prueba no sirve.

**Trampas de entorno, verificadas:**

- **`Api.exe` corriendo bloquea `dotnet build`, `dotnet test` y los comandos `ef`.**
- **`NU1903` sobre `SSH.NET` frena el `restore`** de todos los proyectos de integración. Se
  esquiva con `-p:NuGetAudit=false` **en todos** los comandos, sin tocar configuración del repo.
- **La factory de integración debe fijar `Notifications:EmailProvider`** (`SDD-CT-17`).
- **El stub concede sólo los permisos de tenancy por defecto:** una prueba que necesite
  `storage.file.upload` tiene que pedirlo por `X-Permissions`.
- **Regresión:** los 5 fallos de `SDD-CT-14` se verifican **por nombre, no por conteo**.

## Riesgos

| Riesgo | Cómo se detecta |
|---|---|
| **Producto apuntando a un archivo de otro tenant** | `CA-CAT-05-01`, y probando que la prueba falle sin la verificación |
| Que `catalog` termine acoplado a `storage` sin que nadie lo note | La aserción nueva de `CatalogLayerTests`. Un comentario no lo impide |
| Renumerar `FileOwnerType` y reescribir el significado de filas ya guardadas | El valor nuevo va al final con número propio. `CA-CAT-05-07` |
| Que el fin del fallback rompa clientes que mandaban basura | Es el objetivo, y está declarado como cambio de contrato. `CA-CAT-05-08` |
| `imageUrl` obligando a una lectura por producto en el listado | Deuda declarada de `CAT-05b`, abajo |

## Decisiones abiertas

| ID | Pregunta | Bloquea |
|---|---|---|
| `DECISIÓN-PENDIENTE-CAT-08` | **Al desasignar la portada, ¿el archivo se borra de `Storage`?** Se implementa que **no**: desasignar cambia el catálogo, no el almacenamiento, y el archivo puede seguir siendo uno de los del producto. Borrar es una operación de `Storage`, con su permiso `storage.file.delete` | Nada hoy |
| `DECISIÓN-PENDIENTE-CAT-09` | **¿La portada tiene que pertenecer al producto** (`OwnerId == productId`)? Se implementa que **no** se exige: al crear un producto todavía no hay `productId` que declarar como dueño, así que exigirlo haría imposible asignar imagen en el `POST`. Queda como regla candidata para el `PUT` | Nada hoy |

## Evidencia de cierre

Se completa al terminar. Ver `qep-frontend/sdd/00-metodo/definition-of-done.md`.

### Tramo 1 — `CAT-05a`: el puerto, el resolver y `Storage` (2026-08-15)

**RED literal, antes de escribir una línea de implementación**, con las 11 unitarias de
`ProductImageResolverTests` compilando contra un puerto que no existía:

```txt
error CS0246: El nombre del tipo o del espacio de nombres 'IProductImageLookup' no se encontró
error CS0246: El nombre del tipo o del espacio de nombres 'ProductImageRef' no se encontró
```

Después, con el resolver escrito pero `internal`:

```txt
error CS0122: 'ProductImageResolver' no es accesible debido a su nivel de protección
```

Se resolvió con `InternalsVisibleTo` en el `.csproj`, que es lo que ya hacen `Audit`,
`Notifications` y `Storage`. El resolver **sigue siendo `internal`**: no es API pública del
módulo, igual que `ProductTaxRateResolver`.

**GREEN:** `Superado: 55, Total: 55` en unitarias (eran 44) y `Superado: 10, Total: 10` en las de
integración de `ProductImageApiTests`.

**La prueba contra el mecanismo ausente, que es la exigencia que `CAT-03` dejó.** Se quitó la
comparación de tenant del resolver —`if (image is null || image.TenantId != tenantId)` pasó a
`if (image is null)`— y `CA-CAT-05-01` se puso roja, literal:

```txt
Con error Modules.Catalog.IntegrationTests.ProductImageApiTests.AnImageFromAnotherTenantIsRejectedAndNothingIsPersisted
Expected: UnprocessableEntity
Actual:   Created
```

**Ese `Created` es el 201 que el spec describe**: el producto del tenant A quedaba apuntando al
archivo del tenant B sin una queja. La comprobación se restauró y la prueba volvió a verde.

**La aserción de arquitectura funciona:** `ArchitectureTests` pasó de `16` a `17`, y el que se
suma verifica que `Modules.Catalog.Application` no referencie `Modules.Storage.*`.

#### Dos cosas que aparecieron al escribir, y que el spec tenía mal

**1. `FileOwnerType` y `FileResourceStatus` se persisten por NOMBRE, no por número.**
`StorageDbContext` los mapea con `HasConversion<string>()` sobre `character varying(20)`. La
decisión 3 de este spec decía "se persiste como int, así que los valores no se renumeran", y era
**falso**. Lo que no se puede cambiar es el **nombre** de un valor ya usado; el número es interno.
La conclusión —agregar `Product` al final es seguro— no cambia, pero la razón sí, y quedó
corregida en el comentario del enum.

**2. Dos pruebas nuevas pasaron por la razón equivocada, y se detectó a tiempo.** El helper
forzaba el estado con `UPDATE ... SET status = 3` sobre una columna `varchar`. PostgreSQL lo
acepta por cast de asignación y guarda la cadena `'3'`; **y `Enum.Parse` acepta `"3"`**, así que
EF lo materializaba como `Available` y las pruebas quedaban verdes. Funcionaba por accidente.
Corregido a `SET status = 'Available'`. Lo delató `CA-CAT-05-07`, que sí comparaba contra el
valor guardado.

### Tramo 2 — `CAT-05b`: `imageUrl` en las respuestas (2026-08-15)

`ProductDto` y `ProductResponse` suman `ImageUrl`; `GetProduct` y `ListProducts` lo resuelven con
`ToDtosAsync`, que junta los ids del lote y hace **una** consulta. `CreateProduct` y
`UpdateProduct` no consultan de nuevo: el resolver ya devuelve la referencia entera, y de ahí sale
la URL.

**GREEN:** `Superado: 13, Total: 13` en `ProductImageApiTests`.

### Regresión, y la que se puso roja con razón

**Toda la solución: 290 en verde**, con los 5 fallos de `SDD-CT-14` verificados **por nombre**:

```txt
Modules.Tenancy.IntegrationTests.RealAuthenticationApiTests.LogoutRevokesTheSessionCookie
Modules.Tenancy.IntegrationTests.RealAuthenticationApiTests.MutatingRequestWithoutCsrfHeaderIsRejected
Modules.Tenancy.IntegrationTests.RealAuthenticationApiTests.RoleDowngradeRemovesPermissionsOnTheNextRequest
Modules.Tenancy.IntegrationTests.RealAuthenticationApiTests.SessionCookieAuthenticatesOrdinaryEndpointsWithoutTheBearerToken
Modules.Tenancy.IntegrationTests.RealAuthenticationApiTests.SuspendingMembershipRevokesTheMembersActiveSession
```

**Dos pruebas de `CAT-04` se pusieron rojas, y es la corrección funcionando.**
`CreateWithAllTheDetailsPersistsThemAndGetReturnsThem` y `UpdateWithNullDetailsClearsThem`
asignaban como portada un `Guid.CreateVersion7()` cualquiera —un id que no existe en ningún
lado—, porque **nadie lo verificaba**. Fallaron con `Expected: Created / Actual:
UnprocessableEntity`. Es exactamente el hueco que este slice vino a cerrar, escrito en una prueba
sin que nadie lo notara. Se corrigieron para subir un archivo real; `Modules.Catalog.IntegrationTests`
cerró en `68/68`.

`dotnet build Backend.slnx` → `Compilación correcta. 0 Advertencia(s)`. `dotnet format
--verify-no-changes` no reporta ninguno de los archivos tocados.

### Tamaño y desviación de método declarada

**1063 líneas autoradas** —195 de cambio en 13 archivos, 868 en 6 nuevos—, muy por encima del
umbral de ~400. **La partición estaba declarada antes de escribir código, y eso funcionó**: los
criterios quedaron repartidos y `CAT-05a` es autónomo.

**Lo que no se hizo, y se declara:** los dos tramos se ejecutaron de corrido y van en **un solo
commit**, no en dos. Separarlos después habría sido artificial —`ProductMapping.cs` y
`CatalogDtos.cs` llevan cambios de los dos— y reescribir el diff para simular dos pasos es peor
que decir esto. El valor que dio declarar la partición fue ordenar el alcance y el riesgo, no
partir el commit.

**Y una segunda, más chica:** el RED **unitario** fue literal y previo, como manda el método. El
de las pruebas de **integración** de `CAT-05a` no: se escribieron con el resolver ya implementado,
así que nacieron verdes. Lo que las respalda es la verificación **contra el mecanismo ausente**,
que es la exigencia fuerte del spec y sí se hizo, con su evidencia literal arriba.

### Tramo 3 — runtime contra la API local: 16 de 16 (2026-08-15)

Los 11 criterios endpoint por endpoint, más las tres variantes de `ownerType` inválido y una
comprobación de aislamiento. **Todos en verde:**

```txt
  OK   CA-CAT-05-01 -> 422   image_not_found
  OK   CA-CAT-05-02 -> 422   image_not_found
  OK   CA-CAT-05-03 -> 422   image_not_available
  OK   CA-CAT-05-04 -> 422   image_not_an_image
  OK   CA-CAT-05-05 -> 201   imageFileId=01a006e4-b049-7ac1-b94b-595be6347c8e
  OK   CA-CAT-05-06a -> 201   imageFileId null: "imageFileId":null
  OK   CA-CAT-05-06b -> 200   limpiada: "imageFileId":null
  OK   CA-CAT-05-07 -> Product   guardado en base
  OK   CA-CAT-05-08 [Producto] -> 422   owner_type_invalid
  OK   CA-CAT-05-08 [] -> 422   owner_type_invalid
  OK   CA-CAT-05-08 [4] -> 422   owner_type_invalid
  OK   CA-CAT-05-09 -> https://cdn.qep.test/public/01a006e4-b639-70b7-a71c-fa252ca5de42.png
  OK   CA-CAT-05-10a -> si   imageUrl null
  OK   CA-CAT-05-10b -> si   imageFileId sigue viniendo
  OK   CA-CAT-05-11 -> https://cdn.qep.test/public/01a006e4-b639-70b7-a71c-fa252ca5de42.png
  OK   aislamiento -> 0   el tenant B no ve productos del A
```

**`CA-CAT-05-07` se verificó en base, no por status HTTP:** `SELECT owner_type FROM
storage.file_resources` devolvió `Product`, que es el dato que antes quedaba como `User`.

**Y la auditoría también, por lo que NO escribió.** `platform.outbox_messages` —el outbox de
`catalog` vive en el esquema `platform`, con `ExcludeFromMigrations`— cerró con **6 eventos**
`platform.audit.recorded.v1`, uno por cada escritura exitosa. Los **cuatro rechazos** de
`CA-CAT-05-01` a `-04` no dejaron fila: el `422` y la transacción abortan juntos.

**Trampas de entorno que costaron una vuelta, y no estaban documentadas:**

- **La base local es `dev_lulo_crm_v2` y el usuario es `postgres`**, no `qep`/`qep`. Se descubre
  buscando el esquema: `SELECT ... FROM information_schema.schemata WHERE schema_name='catalog'`
  sobre cada base.
- **Git Bash no tiene `/proc/sys/kernel/random/uuid`.** Genera UUIDs con
  `od -An -tx1 -N16 /dev/urandom`.
- **Para que `imageUrl` no sea siempre `null` hay que arrancar con
  `Storage__R2__PublicBucket` y `Storage__R2__PublicBaseUrl`.** Sin los dos, `IsConfigured` es
  `false` y la URL no se arma. El validador de opciones exige que vayan de a dos.

### Tramo 4 — revisión, y el bloqueante que encontró (2026-08-15)

**Un hallazgo, bloqueante, propio de este slice. Corregido y verificado.**

**`CAT-05a` cerró la escritura y dejó la lectura abierta.** El resolver impide **crear** un
producto que apunte a un archivo de otro tenant, pero **no borra los que ya estaban**: cualquier
producto cargado antes de este slice pudo guardar cualquier `imageFileId`, porque nadie lo
verificaba, y esa fila sigue en la base. `ToDtosAsync` resolvía la URL **sin volver a comparar el
tenant**, así que el `GET` y el listado del tenant A publicaban la URL del archivo del tenant B.

La prueba se escribió antes de la corrección y falló, literal:

```txt
Assert.Null() Failure: Value is not null
Expected: null
Actual:   "https://cdn.qep.test/public/01a006e6-4e9e-7ebf-833"···
```

**Es la misma fuga que el slice existe para cerrar, por el otro lado.** La corrección pasa el
`tenantId` al mapeo de lectura y descarta las referencias ajenas: `image.TenantId == tenantId`. El
`imageFileId` **sigue viajando** —el cliente lo necesita para el `PUT`, y ocultárselo le rompería
la edición sin decirle por qué—; lo que no viaja es la URL.

La fila se inserta por SQL en la prueba **a propósito**: por la API ya no se puede crear, y es
exactamente por eso que se escapa si no se prueba así.

**Deuda de método declarada: fue autorrevisión, no cuatro lentes ciegos.** `CAT-04` saldó esa
deuda y este slice la vuelve a contraer. Pesa más acá que en `CAT-03`, porque `CA-CAT-05-01` es
frontera de aislamiento entre tenants — la misma razón por la que `CAT-04` llevó lentes. Que la
autorrevisión encontrara un bloqueante real no la valida: muestra que había algo que las 13
pruebas y los 16 criterios de runtime no habían visto.

### Cierre — `Complete` el 2026-08-15

**Regresión final de toda la solución: 291 en verde**, con los 5 fallos de `SDD-CT-14`
verificados **por nombre**. Unitarias `55/55`, arquitectura `17/17`, integración de `catalog`
`69/69`. `dotnet build` limpio, `dotnet format --verify-no-changes` sin reportar ninguno de los
archivos tocados.

**Los 11 criterios cubiertos por prueba automática y verificados en runtime.** Los ítems de UI
van `N/A`: es un slice de backend.

**Lo que este slice deja abierto, y no es suyo:**

| Qué | Dónde vive |
| --- | --- |
| `DECISIÓN-PENDIENTE-CAT-08` — ¿desasignar la portada borra el archivo? Implementado que **no** | Decisión de producto |
| `DECISIÓN-PENDIENTE-CAT-09` — ¿la portada debe pertenecer al producto? Implementado que **no** se exige | Decisión de producto |
| `FindManyAsync` hace una lectura por id | `IFileResourceRepository` no tiene método por lote, y agregárselo es cambiarle el contrato a `Storage` desde afuera. Lo pide su dueño cuando duela |
| Los cinco campos de `CAT-04` y `imageUrl` **no están en el frontend** | Fila del ledger de `qep-frontend` (`CAT-01`) |
| Hallazgo `C` de `CAT-04` — `ApiExceptionHandler` filtra `exception.Message` | `src/Api`, afecta a todos los módulos |
| `NU1903` sobre `SSH.NET` | Dependencia transitiva; va a frenar CI |


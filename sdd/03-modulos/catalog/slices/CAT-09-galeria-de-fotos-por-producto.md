# `CAT-09` — Galería de fotos por producto

> **Estado:** **In Progress** — abierto el 2026-08-17
> **Módulo:** `catalog` — ficha y gate en `qep-frontend/sdd/03-modulos/catalog/`
> **Depende de:** `CAT-05` (`Complete`) — introdujo `FileOwnerType.Product` y el puerto
> `IProductImageLookup`
> **Repos afectados:** `qep-backend` únicamente
> **Corre el alcance del gate `CAT-00`:** sí. Ver «Alcance que este slice corre en el gate»

## Objetivo

Que se puedan **listar los archivos de un producto**. Hoy se pueden marcar como suyos y no se
pueden consultar.

## De dónde sale este slice

**De un error del spec de `CAT-05`, corregido el 2026-08-16.** Ese slice dejó la galería fuera de
alcance con esta justificación:

> «`Storage` ya responde *qué archivos son de este producto* por `OwnerId`/`OwnerType`;
> `Product.ImageFileId` responde *cuál es la portada*, que es una sola.»

**La primera mitad era falsa.** `IFileResourceRepository.SearchAsync` filtra por `tenantId`,
`search`, `status`, `kind`, `category`, `tag` y paginación — **`OwnerId` no está entre los
filtros**. Los dos campos se escriben al crear el archivo (`CreateFileRequest`) y se devuelven en
`FileResourceResponse`, pero **no hay forma de consultar por ellos**: el dato entra y no sale.

La exclusión de alcance de `CAT-05` sigue siendo correcta —ese slice era la portada— pero se
justificó con una capacidad que nadie verificó contra el código. Este slice la construye.

## Por qué es un slice de `catalog` y no de `storage`

`Storage` no tiene ficha, ni gate, ni prefijo reservado en `convenciones-de-id.md`: es
infraestructura transversal que el método nunca modeló como módulo de producto, igual que `Audit`,
`Notifications` y `Authorization`.

**El precedente es `CAT-05`**, que modificó `Storage` —agregó `FileOwnerType.Product` y terminó el
fallback silencioso del `OwnerType`— bajo un ID de `catalog`, porque el slice era de catálogo y
`Storage` era el medio. Acá pasa lo mismo: el disparador y el consumidor son de catálogo.

**Se declara la deuda que esto acumula:** es la **segunda** vez que `Storage` se modifica desde
afuera sin ficha propia. La tercera debería abrir el módulo, no repetir el atajo.

## Lo que hay que construir, y lo que no se ve leyendo el endpoint

**1. El filtro por owner en `SearchAsync`, y su query param.**

`ownerId` y `ownerType` entran juntos al listado. **Sueltos no sirven:** un `ownerId` sin tipo es
un `Guid` que podría ser un producto, un usuario o una entidad —los tres se guardan en la misma
columna— y devolver la unión de todos sería un resultado que nadie pidió.

**2. Un `ownerType` inválido responde 422, no se ignora.**

El listado hoy parsea el `status` con `Enum.TryParse` y, si falla, **cae en `null` en silencio**
(`StorageEndpoints.cs:126-128`): pedir `?status=Basura` devuelve la lista **sin filtrar**, como si
no se hubiera pedido nada. Es el mismo patrón que `CAT-05` encontró en el `POST` y corrigió —ahí un
`ownerType` inválido se convertía en `User` y devolvía `201`—.

**Este slice no repite ese error en el filtro que agrega:** un `ownerType` que no parsea responde
**422 `storage.file.owner_type_invalid`**, el código que `CAT-05` ya creó.

**No se corrige el `status`**, y eso se declara: es un cambio de contrato de un filtro preexistente
que ningún criterio de este slice necesita. Queda como deuda anotada al cierre.

**3. Hace falta un índice, y por lo tanto una migración.**

Los índices de `storage.file_resources` son `(TenantId, Status)`, `(TenantId, Status, CreatedAt,
Id)`, `(TenantId, Category)` y uno GIN sobre `Tags` (`StorageDbContext.cs:51-54`). **Ninguno sirve
para filtrar por owner.** Sin índice, la galería de cada producto hace scan del catálogo de
archivos del tenant.

Se agrega `IX_file_resources_tenant_owner` sobre `(TenantId, OwnerType, OwnerId)`, en ese orden:
el tenant primero porque está en todas las consultas, y el tipo antes del id porque es el de menor
cardinalidad.

**4. Lo que el filtro hereda y conviene saber.**

`SearchAsync` excluye siempre `Deleted` y `Purged`, y **además excluye `PendingUpload` cuando no se
pide un `status`**. O sea: la galería de un producto **no muestra las subidas a medio camino**, que
es el comportamiento correcto y no hay que reimplementarlo. Se hereda, y `CA-CAT-09-05` lo fija
para que nadie lo rompa sin enterarse.

## Contrato que se expone

**Ningún endpoint nuevo, ningún permiso nuevo.** Dos query params sobre el listado que ya existe:

| Método | Ruta | Permiso |
|---|---|---|
| `GET` | `/api/v1/tenants/{tenantId}/files?ownerId={guid}&ownerType={tipo}` | `storage.file.read` |

| Caso | Respuesta |
|---|---|
| `ownerId` y `ownerType` válidos | **200**, sólo los archivos de ese owner en ese tenant |
| `ownerType` que no existe en el enum | **422** `storage.file.owner_type_invalid` |
| `ownerId` sin `ownerType`, o al revés | **422** `storage.file.owner_filter_incomplete` |
| Ninguno de los dos | **200**, el listado completo de siempre — el filtro es opcional |
| Un `ownerId` de otro tenant | **200 con lista vacía**, nunca los archivos ajenos |

## Códigos de dominio nuevos

| Invariante | Código |
|---|---|
| Se mandó `ownerId` sin `ownerType`, o `ownerType` sin `ownerId` | `storage.file.owner_filter_incomplete` |

`storage.file.owner_type_invalid` **ya existe**: lo creó `CAT-05` para el `POST`. Se reutiliza, no
se duplica.

## Fuera de alcance

- **Corregir el fallback silencioso del filtro `status`.** Es un contrato preexistente y ningún
  criterio de este slice lo necesita. Queda declarado como deuda.
- **Ordenar la galería a mano.** Hereda el orden del listado —`CreatedAt` descendente— y no se
  agrega un campo de posición. Si hace falta ordenarlas, es otro slice y probablemente de producto.
- **`Product.ImageFileId`.** La portada sigue siendo la portada; este slice no la toca.
- **Que `catalog` consuma este listado.** El endpoint es de `Storage` y lo llama el frontend
  directamente, como ya hace con el resto de la biblioteca.
- **El frontend.** La grilla de fotos es fila del ledger de `qep-frontend`.

## Criterios de aceptación

| ID | Criterio |
|---|---|
| `CA-CAT-09-01` | Dados tres archivos del tenant, dos con `ownerId` del producto `P` y `ownerType: Product` y uno de otro owner, cuando se lista con `?ownerId=P&ownerType=Product`, entonces devuelve **sólo los dos**, y `totalCount` es **2** |
| `CA-CAT-09-02` | Dado un `ownerType` que no existe en el enum, entonces **422** `storage.file.owner_type_invalid` — **no** se ignora el filtro ni se devuelve la lista completa |
| `CA-CAT-09-03` | Dado `ownerId` sin `ownerType`, entonces **422** `storage.file.owner_filter_incomplete`; y lo mismo al revés |
| `CA-CAT-09-04` | Dado un listado **sin** `ownerId` ni `ownerType`, entonces se comporta exactamente como antes de este slice — el filtro es opcional y no cambia el contrato existente |
| `CA-CAT-09-05` | Dado un archivo del producto `P` en `PendingUpload`, entonces **no** aparece en la galería cuando no se pide `status` — se hereda la exclusión del listado |
| `CA-CAT-09-06` | **Dado un archivo del producto `P` en el tenant B, cuando el tenant A lista con `?ownerId=P&ownerType=Product`, entonces devuelve lista vacía y `totalCount` 0** |
| `CA-CAT-09-07` | Dado el filtro por owner combinado con `search` o `kind=image`, entonces los dos filtros se aplican juntos, no uno u otro |
| `CA-CAT-09-08` | Dada la migración aplicada, entonces existe `IX_file_resources_tenant_owner` sobre `(tenant_id, owner_type, owner_id)` |

`CA-CAT-09-06` es el criterio que justifica revisión de riesgo: es frontera de aislamiento entre
tenants, y es exactamente la clase de fuga que `CAT-05` tuvo **dos veces** —una en la escritura y
otra en la lectura, encontrada por su propia revisión—. Un filtro por owner que se aplique **antes**
o **en vez** del filtro de tenant publica la biblioteca ajena.

`CA-CAT-09-02` es el que impide repetir el error que `CAT-05` ya corrigió una vez en este mismo
enum.

## Pruebas requeridas

| Nivel | Qué verifica |
|---|---|
| Unitaria | El parseo del `ownerType` y la regla de los dos campos juntos, sin base de por medio |
| Arquitectura | `StorageLayerTests` sin cambios |
| Integración | Los 8 CA contra PostgreSQL real, incluido el índice por consulta al catálogo del sistema |
| Runtime | Los criterios contra la API local |

**TDD obligatorio, RED antes que GREEN, y el RED tiene que fallar por el motivo correcto.**

**Y la exigencia que arrastran `CAT-03` a `CAT-08`:** `CA-CAT-09-06` tiene que **probarse contra el
mecanismo ausente**. Si al quitar el filtro de tenant de `SearchAsync` la prueba sigue verde, la
prueba no sirve.

## Riesgos

| Riesgo | Cómo se detecta |
|---|---|
| **El filtro por owner pisa o precede al de tenant y publica archivos ajenos** | `CA-CAT-09-06`, y probando que la prueba falle sin el filtro de tenant |
| **Repetir el fallback silencioso** que `CAT-05` corrigió en el `POST` | `CA-CAT-09-02` |
| Romper el listado existente al agregar parámetros opcionales | `CA-CAT-09-04`, y la suite de `Storage` que ya existe |
| El índice se declara en el modelo pero la migración no se genera | `CA-CAT-09-08`, que lo verifica **en el catálogo de PostgreSQL**, no en el `DbContext` |
| Scan por falta de índice cuando la biblioteca del tenant crece | El índice, y `CA-CAT-09-08` |

## Alcance que este slice corre en el gate `CAT-00`

El gate declara las trece operaciones de `catalog`, **ninguna de `Storage`**: la biblioteca de
archivos nunca entró a ese contrato, aunque `CAT-05` ya la usaba. Este slice agrega **dos query
params a un endpoint de otro módulo**, y hay que decidir si eso se escribe en el gate de `catalog`
o si es la señal de que `storage` necesita el suyo.

**Se escribe en `CAT-00`, con la deuda declarada**, por el mismo criterio con el que este slice
lleva ID de `catalog`. Ver «Por qué es un slice de `catalog` y no de `storage`».

## Evidencia de cierre

Se completa al terminar. Ver `qep-frontend/sdd/00-metodo/definition-of-done.md`.

### Tramo 1 — el filtro, el índice y su migración (2026-08-18)

**RED unitario literal**, contra un tipo que no existía:

```txt
FileOwnerFilterTests.cs(22,21): error CS0103: El nombre 'FileOwnerFilter' no existe en el contexto actual
```

**GREEN unitario:** `Superado: 50, Total: 50` en `Modules.Storage.UnitTests` — eran 36.
**GREEN de integración:** `Superado: 9, Total: 9` con los 8 criterios contra PostgreSQL real.

#### Una decisión que el RED obligó a tomar, y que el spec no tenía

Dos pruebas fallaron en el primer GREEN, y **no era un defecto del código**: era una ambigüedad
que nadie había resuelto. `?ownerId=X&ownerType=` —el tipo presente pero **vacío**— ¿es un tipo
inválido o un tipo que no se mandó?

Gana **«no se mandó»**: `owner_filter_incomplete`. Decirle a alguien «el tipo no es válido» lo manda
a revisar un valor que nunca escribió, cuando lo que le falta es completar el filtro. Se corrigió
**la prueba**, no el código, y la razón quedó escrita en el test para el próximo que se haga la
misma pregunta.

#### La prueba contra el mecanismo ausente

`CA-CAT-09-06` es frontera de aislamiento, así que se verificó saboteándola. Se quitó
`resource.TenantId == tenantId` del `Where` de `SearchAsync` y la prueba se puso roja, literal:

```txt
Modules.Storage.IntegrationTests.FileOwnerFilterApiTests.TheOwnerFilterNeverReachesAcrossTenants
Assert.Equal() Failure: Values differ
Expected: 0
Actual:   1
```

**Ese `1` es el archivo del tenant B apareciendo en la lista del tenant A.** Es exactamente la fuga
que el criterio existe para prevenir: un `OwnerId` es único de por sí, así que filtrar sólo por él
parece funcionar hasta que dos tenants comparten un id. El sabotaje se restauró y `git diff` del
archivo cierra en `12 insertions, 0 deletions` — sólo el bloque nuevo, la línea del tenant intacta.

#### Regresión: 349 en verde, cero regresión

`Modules.Storage.IntegrationTests` pasó de `2` a **`11/11`**; unitarias de `Storage` de 36 a `50`;
`ArchitectureTests` `17/17` **sin cambios**. Los 5 fallos de `SDD-CT-14` verificados **por nombre**.

### Tramo 2 — runtime contra la API local: 9 de 9 (2026-08-18)

```txt
  OK   CA-CAT-09-01  -> 2     sólo los del producto A; el de otro producto no aparece
  OK   CA-CAT-09-05  -> no aparece   la subida a medio camino queda fuera
  OK   CA-CAT-09-02  -> 422   owner_type_invalid
  OK   CA-CAT-09-03a -> 422   sólo ownerId: owner_filter_incomplete
  OK   CA-CAT-09-03b -> 422   sólo ownerType: owner_filter_incomplete
  OK   CA-CAT-09-04  -> 3     sin filtro trae los 3 disponibles del tenant
  OK   CA-CAT-09-06  -> 2     el tenant B tiene uno con el MISMO ownerId -> sin fuga
  OK   CA-CAT-09-07  -> 1     owner + kind se aplican juntos; el PDF queda fuera
  OK   CA-CAT-09-08  -> 1     IX_file_resources_tenant_owner existe en la base
```

**`CA-CAT-09-06` en runtime se montó a propósito para que fuera capaz de fallar:** se creó en el
tenant B un archivo con **el mismo `ownerId`** que el producto del tenant A. Si el filtro por dueño
se aplicara en lugar del de tenant, ese archivo aparecería. No aparece.

**`CA-CAT-09-08` se verificó en el catálogo de PostgreSQL**, no en el `DbContext` — declarar el
índice en el modelo y olvidar la migración compila y pasa todo lo demás:

```txt
CREATE INDEX "IX_file_resources_tenant_owner" ON storage.file_resources
  USING btree (tenant_id, owner_type, owner_id)
```

**La migración se aplicó sola.** `dotnet ef database update` falló porque el design-time factory
apunta a `5432` y la base local vive en `5433`; no hizo falta la cadena de conexión —que es
secreto— porque cada módulo tiene su `<Modulo>DatabaseInitializer` con `MigrateAsync` y la API
migra al arrancar.

**Y el fallo de verificador que se repitió dos veces, no se repitió una tercera.** El `%{http_code}`
de `curl.exe` se captura en su **propia invocación** y no dentro de un array de argumentos de
PowerShell, que es lo que lo truncaba a `4` en los runtimes de `CAT-07` y `CAT-08`. Los 9 criterios
salieron a la primera.

### Tramos pendientes

| Tramo | Qué falta |
|---|---|
| Gate | Los dos query params en `CAT-00`, con la deuda de `Storage` declarada |
| Revisión | Lente ciego. `CA-CAT-09-06` es frontera de tenant |
| Ledger | La entrada de handoff en `sdd/02-plan/plan-maestro.md` |

# Imágenes de producto — guía de integración para el frontend

Cómo subir una imagen, publicarla y asignarla como portada de un producto. Está escrito para
quien consume la API desde `qep-frontend`.

Todo lo que sigue está verificado contra el código de este repositorio, no contra
documentación. Ante cualquier diferencia con la respuesta real de la API, **gana la API**
(`SDD-ADR-01`) y este documento se corrige.

| Capacidad | Estado |
| --- | --- |
| **Portada del producto** — subir, publicar y asignar la imagen principal | **Disponible de punta a punta** |
| **Galería** — listar las imágenes de un producto | **Disponible.** Ver [§ La galería](#el-resto-de-las-imágenes-la-galería) |

Slices que las construyeron: `CAT-04` (el campo `imageFileId`), `CAT-05a` (las validaciones),
`CAT-05b` (el `imageUrl` derivado) y `CAT-09` (el filtro por dueño en el listado de archivos). El inventario de rutas está en
[README § API implementada](../README.md#api-implementada); el comportamiento de `Storage` en
[README § Biblioteca de archivos](../README.md#biblioteca-de-archivos-cloudflare-r2).

## Antes de la primera llamada

Todas las rutas cuelgan del tenant: `/api/v1/tenants/{tenantId}/…`. No hay variante sin
`tenantId`.

- **Sesión por cookie.** Los `fetch` contra la API van con `credentials: "include"`.
- **Todo método que muta lleva `X-Qep-Client: web`.** Sin ese header el request muere con `403`
  antes de llegar al endpoint, en
  [`RequireCsrfHeaderMiddleware.cs`](../src/Bootstrapper/Csrf/RequireCsrfHeaderMiddleware.cs).
  Los `GET` no lo necesitan.
- **Cuatro permisos distintos:** `catalog.product.manage` para escribir el producto,
  `storage.file.upload` para subir, `storage.file.publish` para publicar y `storage.file.read`
  para leer o pedir URLs firmadas. Un usuario puede poder cargar un producto y no poder publicar
  su foto.

### Cómo leer un error

Todos los errores son `ProblemDetails` con un campo `code` en la raíz del JSON, armado en
[`ApiExceptionHandler.cs`](../src/Api/ApiExceptionHandler.cs). **Ese `code` es el contrato;
`title` y `detail` son texto para humanos y pueden cambiar.** Cuando el `code` es
`validation.failed` viene además un objeto `errors` con los mensajes por campo — es el único
caso que se mapea campo por campo.

```json
{
  "status": 422,
  "title": "Business rule failed",
  "detail": "The image has not finished uploading yet.",
  "code": "catalog.product.image_not_available",
  "traceId": "0HN7…"
}
```

## El flujo de la portada, en seis pasos

El orden importa: cada paso deja un estado que el siguiente necesita. Los pasos 2, 3 y 4 son una
sola operación partida en tres — si se corta en el medio, el archivo queda inservible y hay que
empezar de nuevo desde el 2.

### 1. Crear el producto, todavía sin imagen

La sesión de carga exige un `ownerId`, y para un producto nuevo ese id no existe hasta que el
producto existe. Primero se crea, después se sube.

```http
POST /api/v1/tenants/{tenantId}/catalog/products
X-Qep-Client: web

{ "name": "Termo 1L", "code": "TER-001", "description": null,
  "imageFileId": null, "price": 48000, "currency": "COP", "taxRateId": null }

→ 201 { "id": "…", "imageFileId": null, "imageUrl": null, … }
```

Si el producto ya existe, saltar directo al paso 2 con su `id`.

### 2. Abrir la sesión de carga

Se declara qué se va a subir; todavía no se mandan bytes. La respuesta trae una URL firmada
contra el almacenamiento de objetos.

```http
POST /api/v1/tenants/{tenantId}/files
X-Qep-Client: web

{ "ownerId": "<productId>", "ownerType": "Product",
  "name": "termo-1l.png", "mimeType": "image/png", "sizeBytes": 184320 }

→ 201 { "fileResourceId": "…", "uploadUrl": "https://…", "storageKey": "…" }
```

Lo que valida [`FileUploadPolicy`](../src/Modules/Storage/Modules.Storage.Application/FileUploadPolicy.cs)
antes de entregar la URL:

- **La extensión y el `mimeType` tienen que corresponderse.** Un `.png` declarado como
  `image/jpeg` es `422 storage.file.mime_mismatch`. Conviene usar el `type` del `File` del input,
  no una constante escrita a mano.
- **Imágenes admitidas:** `.jpg`, `.jpeg`, `.png`, `.webp`. Cualquier otra extensión es
  `422 storage.file.type_not_allowed`.
- **Máximo 25 MB**, y mayor que cero.
- **`name` es un nombre de archivo, no una ruta.** Sin barras ni `../`, y hasta 260 caracteres.
- **`ownerType` sólo acepta `"User"`, `"Entity"`, `"System"` o `"Product"`.** Cualquier otro
  valor es `422 storage.file.owner_type_invalid`. Para imágenes de producto va `"Product"`.

### 3. Subir los bytes a `uploadUrl` — este request no va a la API

Es un `PUT` directo al almacenamiento de objetos, con la URL firmada tal cual llegó. No lleva
cookies, no lleva `X-Qep-Client`, y **no debe pasar por el cliente HTTP de la app**: cualquier
interceptor que agregue un header rompe la firma.

```http
PUT {uploadUrl}
Content-Type: image/png      ← idéntico al mimeType declarado en el paso 2
If-None-Match: *             ← obligatorio, va firmado en la URL

body: los bytes crudos del File (no FormData, no multipart)
```

> **Los dos headers son parte de la firma.** La URL se firma con el `Content-Type` y con
> `If-None-Match: *` ([`R2ObjectStorage.cs:18-32`](../src/Modules/Storage/Modules.Storage.Infrastructure/ObjectStorage/R2ObjectStorage.cs#L18-L32)).
> Omitir cualquiera de los dos, o mandar un `Content-Type` distinto del declarado, hace que el
> almacenamiento rechace el `PUT` con un `403` de firma inválida que no explica nada. Es el error
> más caro de diagnosticar de todo el flujo.

- **Cada objeto se escribe una sola vez.** Eso es lo que hace `If-None-Match: *`. Reintentar el
  `PUT` sobre la misma URL falla: para reintentar hay que volver al paso 2 y abrir una sesión
  nueva.
- **La URL expira a los 5 minutos** (`Storage:PresignedUrlMinutes`). No conviene guardarla en
  estado que sobreviva a la pantalla, ni pedirla antes de que la persona elija el archivo.
- **CORS del bucket.** El preflight tiene que permitir `PUT` con `Content-Type` e `If-None-Match`.
  La configuración de referencia ya existe en
  [`ops/r2-cors.example.json`](../ops/r2-cors.example.json) y ya incluye los dos headers.
  **Lo que hay que verificar es `AllowedOrigins`:** hoy lista `http://localhost:3002` y
  `https://app.lulocrm.com`. Si el dev server del frontend corre en otro puerto, el preflight
  falla y hay que agregar ese origen y volver a aplicar el archivo al bucket.

### 4. Cerrar la carga

Hasta acá el archivo está `PendingUpload` y no sirve para nada. Este paso lo vuelve utilizable:
verifica el tamaño real, inspecciona el contenido, lo escanea y genera la miniatura.

```http
POST /api/v1/tenants/{tenantId}/files/{fileResourceId}/complete
X-Qep-Client: web

→ 200 {
  "id": "…", "status": "Available", "mimeType": "image/png",
  "isPublic": false, "publicUrl": null,
  "variants": [
    { "name": "thumbnail", "mimeType": "image/webp",
      "width": 320, "height": 240, "publicUrl": null }
  ], … }
```

Se genera **una sola variante**, llamada `thumbnail`: WebP calidad 80, con el lado mayor en
320 px y sin metadatos EXIF. No hay un set de tamaños; más medidas es trabajo de backend y hay
que pedirlo.

> **Un fallo acá es terminal para ese archivo.** Si el tamaño no coincide con lo declarado, si el
> contenido no se corresponde con el tipo, si el escaneo no da limpio o si la imagen no se puede
> procesar, el archivo queda en **cuarentena** y no se recupera. La UI tiene que descartar ese
> `fileResourceId` y reintentar desde el paso 2 — no reintentar `complete`.

Las cargas que se abandonan antes de este paso se purgan solas a las 24 horas.

### 5. Publicar el archivo

Sin este paso no hay URL para poner en un `src`. Publicar copia el archivo y su miniatura al
almacenamiento público y devuelve las URLs definitivas.

```http
PUT /api/v1/tenants/{tenantId}/files/{fileResourceId}/publication
X-Qep-Client: web

→ 200 { …, "isPublic": true,
        "publicUrl": "https://…/….png",
        "variants": [ { "name": "thumbnail", "publicUrl": "https://…/….webp" } ] }
```

Para un preview **antes** de publicar existe `POST /files/{fileId}/download-url?variant=thumbnail`,
que devuelve una URL firmada y con vencimiento. Sirve para el editor. **No sirve para pintar una
grilla de catálogo:** es una llamada por archivo y la URL caduca.

### 6. Asignar la portada al producto

```http
PUT /api/v1/tenants/{tenantId}/catalog/products/{productId}
X-Qep-Client: web

{ "name": "Termo 1L", "code": "TER-001", "description": null,
  "imageFileId": "<fileResourceId>", "price": 48000, "currency": "COP", "taxRateId": null }

→ 200 { …, "imageFileId": "…", "imageUrl": "https://…" }
```

> **El `PUT` reemplaza el producto entero.** No es un `PATCH`: todo campo opcional que no se mande
> queda en `null`. Mandar sólo `imageFileId` borra la descripción, el precio y la tasa. El body se
> arma desde el producto completo que ya está en pantalla.

- `imageFileId: null` quita la portada. Es la forma de desasignar.
- `imageUrl` es **derivado y de sólo lectura**: nunca se manda en el request. Viene resuelto en el
  `GET` por id y en el listado, así que una grilla de 20 productos se pinta con una sola llamada.
- `imageUrl` viene en `null` si el archivo existe pero no fue publicado, y en ese caso
  `imageFileId` sí viene. Ver uno sin el otro significa que falta el paso 5.
- `isActive` no viaja en el body. Se cambia con `POST /products/{id}/deactivate` y `/activate`.

## Errores que la UI tiene que distinguir

Los tres primeros aparecen al asignar la portada y los tres son `422`. Un mensaje genérico acá
deja a la persona sin saber qué corregir.

| `code` | HTTP | Qué pasó |
| --- | --- | --- |
| `catalog.product.image_not_found` | 422 | El archivo no existe **o pertenece a otro tenant**. Los dos casos comparten código a propósito: distinguirlos confirmaría que el id existe en otro tenant |
| `catalog.product.image_not_available` | 422 | Falta el paso 4, o el archivo quedó en cuarentena |
| `catalog.product.image_not_an_image` | 422 | El archivo existe pero su `mimeType` no empieza con `image/`. Un PDF subido correctamente cae acá |
| `storage.file.mime_mismatch` | 422 | La extensión y el `mimeType` declarados no se corresponden |
| `storage.file.type_not_allowed` | 422 | Extensión fuera de la lista |
| `storage.file.size_invalid` | 422 | Excede 25 MB, o el tamaño real no coincide con el declarado |
| `storage.file.content_mismatch` | 422 | Los bytes no son del tipo declarado — típicamente un archivo renombrado. Va a cuarentena |
| `storage.image.invalid` | 422 | La imagen no se pudo decodificar. Cuarentena |
| `storage.image.dimensions_too_large` | 422 | Más de 40 megapíxeles. Puede pesar poco y aun así rechazarse: es límite de dimensiones, no de bytes |
| `storage.object.missing` | 428 | Se llamó a `complete` sin que el `PUT` del paso 3 haya llegado. Se reintenta el paso 3, no éste |
| `storage.public.not_configured` | 422 | El almacenamiento público no está configurado en ese ambiente. No es error de la UI |
| `storage.file.owner_type_invalid` | 422 | `ownerType` fuera del conjunto admitido, al subir o al filtrar la galería |
| `storage.file.owner_filter_incomplete` | 422 | Se mandó `ownerId` sin `ownerType`, o al revés. Van juntos o no va ninguno |
| `validation.failed` | 422 | Errores de campo del producto. Trae el objeto `errors` con los mensajes por propiedad |

## El resto de las imágenes: la galería

Disponible desde `CAT-09`. Se listan los archivos de un producto filtrando por dueño:

```http
GET /api/v1/tenants/{tenantId}/files?ownerId={productId}&ownerType=Product&page=1&pageSize=20

→ 200 { "items": [ { "id": "…", "publicUrl": "…", "variants": [ … ], … } ],
        "totalCount": 3, "page": 1, "pageSize": 20 }
```

Requiere `storage.file.read`. Se combina con los demás filtros del listado —`search`, `status`,
`kind`, `category`, `tag`—, y la paginación admite hasta `pageSize=100`; fuera de rango cae al
default de 20.

Dos reglas de [`FileOwnerFilter`](../src/Modules/Storage/Modules.Storage.Application/FileOwnerFilter.cs)
que la UI tiene que respetar:

- **`ownerId` y `ownerType` van juntos o no va ninguno.** Mandar uno solo es
  `422 storage.file.owner_filter_incomplete`. Un filtro a medias no se ignora en silencio: si se
  ignorara, la pantalla mostraría los archivos de todo el tenant creyendo que son de un producto.
- **Un `ownerType` inválido falla**, con `422 storage.file.owner_type_invalid`. Tampoco se acepta
  el número crudo del enum (`"4"`): el contrato es el nombre.

Cada archivo del listado trae su `publicUrl` y sus `variants` sólo si fue publicado. **Un archivo
subido y no publicado aparece en la galería con `publicUrl: null`** — para mostrarlo hay que
publicarlo (paso 5) o pedir una URL firmada con `download-url`.

**Lo que la galería todavía no define:** el orden de las imágenes, y cómo se relaciona con la
portada. La portada es un campo del producto (`imageFileId`) y la galería es un listado de
`Storage`: son dos fuentes distintas, y hoy nada garantiza que la portada aparezca primera ni que
esté marcada dentro del listado. Si la pantalla necesita un orden estable, hay que pedirlo como
slice.

## Referencia rápida

El flujo completo, sin manejo de errores.

```ts
const base = `/api/v1/tenants/${tenantId}`;
const json = { "Content-Type": "application/json", "X-Qep-Client": "web" };

// 2 — sesión de carga
const session = await fetch(`${base}/files`, {
  method: "POST", credentials: "include", headers: json,
  body: JSON.stringify({
    ownerId: productId,
    ownerType: "Product",
    name: file.name,
    mimeType: file.type,
    sizeBytes: file.size,
  }),
}).then((r) => r.json());

// 3 — bytes al almacenamiento: sin cookies, sin el cliente HTTP de la app
await fetch(session.uploadUrl, {
  method: "PUT",
  headers: { "Content-Type": file.type, "If-None-Match": "*" },
  body: file,
});

// 4 — cerrar la carga
await fetch(`${base}/files/${session.fileResourceId}/complete`, {
  method: "POST", credentials: "include", headers: json,
});

// 5 — publicar para tener una URL directa
await fetch(`${base}/files/${session.fileResourceId}/publication`, {
  method: "PUT", credentials: "include", headers: json,
});

// 6 — asignar la portada (PUT completo, con todos los campos del producto)
await fetch(`${base}/catalog/products/${productId}`, {
  method: "PUT", credentials: "include", headers: json,
  body: JSON.stringify({ ...product, imageFileId: session.fileResourceId }),
});
```

---

Contrato leído de [`ProductEndpoints.cs`](../src/Modules/Catalog/Modules.Catalog.Api/ProductEndpoints.cs),
[`StorageEndpoints.cs`](../src/Modules/Storage/Modules.Storage.Api/StorageEndpoints.cs),
[`FileUploadPolicy.cs`](../src/Modules/Storage/Modules.Storage.Application/FileUploadPolicy.cs),
[`ProductImageResolver.cs`](../src/Modules/Catalog/Modules.Catalog.Application/ProductImageResolver.cs),
[`CompleteUpload.cs`](../src/Modules/Storage/Modules.Storage.Application/CompleteUpload.cs),
[`ApiExceptionHandler.cs`](../src/Api/ApiExceptionHandler.cs) y
[`RequireCsrfHeaderMiddleware.cs`](../src/Bootstrapper/Csrf/RequireCsrfHeaderMiddleware.cs).

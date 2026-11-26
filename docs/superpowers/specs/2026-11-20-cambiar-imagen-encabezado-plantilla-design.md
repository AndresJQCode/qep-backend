# Cambiar la imagen del encabezado de una plantilla — backend

Fecha: 2026-11-20. Spec par: `qep-frontend/docs/superpowers/specs/2026-11-20-cambiar-imagen-encabezado-plantilla-design.md`.

## Intención

Un tenant creó una plantilla con encabezado de imagen, Meta la aprobó, y la imagen quedó desactualizada. Hoy la única salida es
crear otra plantilla y esperar otra aprobación de Meta. No hace falta: la imagen que Meta guarda (`header_handle`) es sólo la muestra
de la revisión; la que recibe el cliente la pone QEP en cada envío, por enlace, desde `MessageTemplate.HeaderImageUrl`
(`MessageTemplates.StoredHeaderOf`). Cambiar la imagen es cambiar ese dato de QEP.

## Objetivos

- Reemplazar la imagen guardada de una plantilla con encabezado `IMAGE`, sin llamar a Meta ni tocar su estado de revisión.
- Asignar una imagen a una plantilla con encabezado `IMAGE` que no la tiene (sincronizada desde la consola de Meta): así pasa a ser
  enviable (`MessageTemplates.FromStored`).

## No objetivos

- Elegir la imagen en cada envío (alternativa B, descartada por ahora; se puede montar encima).
- Editar la plantilla en Meta (`POST /{template-id}`): la manda otra vez a revisión.
- Video o documento en el encabezado.

## Anti-objetivos

- Que el cambio deje una imagen pública sin fila, o le quite la imagen a una plantilla viva.
- Que el cambio dispare una revisión en Meta.

## Enfoques

- **A. Reemplazar la imagen guardada en QEP (elegido).** Un endpoint, sin Meta. Todo envío siguiente usa la nueva.
- B. Imagen por envío: selector en chat, «Nueva conversación», campañas y la cola. Mucho más grande; no resuelve mejor el caso.
- C. Editar la plantilla en Meta: vuelve a revisión, que es justo lo que se quiere evitar, y no cambia lo que recibe el cliente.

## Contrato

`PUT /api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management/templates/{templateId}/header-image` (mismo
grupo que `DELETE …/templates/{templateId}`), permiso `messaging.template.manage`, sin `If-Match`.

`ManagedTemplateDto` suma `HeaderMedia` (`"Image" | "Video" | "Document" | null`, de `TemplateCatalog.FromStored`), en el listado y en
toda respuesta que lo devuelve: sin él la pantalla no distingue una plantilla de imagen sin imagen guardada de una sin encabezado.

Cuerpo: `{ "headerImageFileId": "<guid>" }`, el archivo ya subido con `POST …/header-image/upload-session` (la misma sesión de la
creación).

Respuesta `200`: el `ManagedTemplateDto` de la plantilla, con `headerImageUrl` nuevo.

Errores (todos códigos que ya existen):

| Caso | Código |
|---|---|
| Plantilla no existe en esa conexión/tenant | el `404` que ya usa el borrado |
| El encabezado de la plantilla no es `IMAGE` | `422 TemplateHeaderMismatch` |
| `headerImageFileId` vacío | `422` de validación, campo `headerImageFileId` |
| Archivo ajeno, no subido, rechazado, MIME o tamaño fuera de `TemplateHeaderImagePolicy`, subido hace demasiado | los de `EnsureUsableImageAsync`, con `fieldErrors` en `headerImageFileId` |
| Archivo que ya es la imagen de alguna plantilla (incluida ésta) | `422 TemplateHeaderImageRejected` (misma regla que la creación) |

## Comportamiento

1. Autoriza, valida, carga la plantilla del tenant y la conexión.
2. Decide si el encabezado es `IMAGE` leyendo `ComponentsJson` con el parser existente (`HeaderMedia == Image`), no por
   `HeaderImageFileId`: una sincronizada no lo tiene y sí debe poder recibir imagen.
3. `IsHeaderImageInUseAsync` → `EnsureUsableImageAsync` → `images.PublishAsync`.
4. Método de dominio nuevo `MessageTemplate.ReplaceHeaderImage(fileId, publicUrl, now)` que devuelve el `fileId` anterior (o `null`).
   `AttachHeaderImage` sigue rechazando una segunda imagen: la creación no cambia.
5. En el mismo `SaveChangesAsync`: la fila, la auditoría (acción nueva `TemplateHeaderImageReplaced`) y, si había imagen anterior,
   una fila en la tabla nueva de imágenes retiradas (abajo). **La imagen anterior no se libera** (decisión del owner, 2026-11-20).
6. Si algo falla después de publicar y antes de guardar, la imagen **nueva** se libera con el mismo patrón que la creación
   (`ReleaseOrDeferAsync`, que no libera si una fila la usa). Conviene extraerlo a una pieza compartida en vez de copiarlo.

Estado de la plantilla: se permite con cualquier estado. No se toca `Status` ni `ComponentsJson`, y `SyncFrom`/`ApplyStatus` ya
preservan la imagen.

## La imagen anterior se conserva

Decisión del owner, 2026-11-20. El hilo de Mensajería pinta el encabezado de un mensaje enviado desde el enlace que guardó
(`TemplateDetailsJson.Header`, `template-header.tsx` del frontend). Liberar la imagen anterior rompería ese enlace en el historial
de QEP; el cliente la conserva igual en WhatsApp.

- **Tabla nueva** `messaging.message_template_retired_header_images` (`tenant_id`, `template_id`, `file_id`, `public_url`,
  `retired_at`), con FK a la plantilla. Una fila por cada imagen reemplazada.
- **La limpieza diaria no la toca:** `TemplateImageReferences.FindReferencedAsync` cuenta también los `file_id` de esa tabla. Sin
  esto, `TemplateHeaderImageSweep` la suelta a las 24 h por no tener fila de plantilla.
- **`IsHeaderImageInUseAsync` también la cuenta:** una imagen retirada no se puede volver a tomar por otra plantilla ni liberar
  por el camino de fallo de una creación.
- **Borrar la plantilla** (decisión mía, coherente con hoy): libera la imagen actual, como ya hace, y también las retiradas, con el
  mismo aviso `TemplateHeaderImageReleased` por archivo. El borrado ya rompe el historial de esa plantilla hoy; no se cambia acá.
- Sin tope de retiradas: cada cambio suma una imagen de hasta 5 MB. Si el volumen llega a importar, se decide aparte.

## Efectos a saber

- Los mensajes ya enviados siguen mostrando su imagen, en WhatsApp y en el hilo de QEP.
- Una campaña en curso usa la imagen nueva desde el cambio.

## Pruebas

Unitarias del dominio (`ReplaceHeaderImage`) y del handler (cada fila de la tabla de errores, fila de retirada sólo con imagen
anterior y sin aviso de liberación, liberación de la nueva al fallar el guardado). Integración filtrada por nombre: el endpoint, la
migración, que `TemplateImageReferences` y `IsHeaderImageInUseAsync` cuenten las retiradas, y que el borrado libere actual y
retiradas. Capas: `ArchitectureTests` sin cambios.

## Lo que queda al implementador

Nombre exacto del comando/handler, si la pieza de liberación se extrae a una clase o a un helper, y el texto de la auditoría.

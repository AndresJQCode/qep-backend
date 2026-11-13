# Encabezado multimedia en la API externa de plantillas — diseño

- **Fecha:** 2026-11-13
- **Módulos:** Messaging (Application, Infrastructure, Api). Sin migración. Frontend en su propio spec (§2, meta 5).
- **Estado:** en revisión del owner, sin construir
- **Verificado contra el código:** `origin/develop` en `bfc5b7bf`
- **Spec hermano:** `envios-venezuela-backend`, `docs/superpowers/specs/2026-11-13-notificaciones-por-qep-design.md` (el primer
  integrador que lo usa; QEP no depende de él)

## 1. Contexto y por qué

La API externa (spec 2026-10-30, `POST /api/v1/external/whatsapp/messages`) envía plantillas aprobadas por el número que el tenant
conectó a QEP. Hoy **rechaza toda plantilla con encabezado de imagen, video o documento**: `TemplateCatalog.Parse` le pone
`media_header` (`MessageTemplates.cs:326-336`), `ListExternalTemplates` la filtra por `Sendable` y el envío responde
`422 messaging.template.not_available`.

Meta permite esas plantillas y exige mandar el archivo del encabezado **en cada envío**: aprobarla con una imagen de ejemplo no
alcanza. Las plantillas transaccionales (aviso de despacho con el logo, factura en PDF) los usan mucho, así que cualquier
integrador se topa con el 422 apenas su plantilla tiene uno. Es un hueco del producto, no un pedido de un cliente: el primer
integrador que lo necesita es Envíos Venezuela (aviso de estado de la guía con el logo de encabezado), pero el contrato es
genérico y **QEP no conoce a Envíos Venezuela**.

Decisión del owner (2026-11-13): cuando el envío es externo, el integrador manda el **link directo** del archivo y QEP se lo pasa
a Meta (`{"type":"image","image":{"link":…}}`). QEP no descarga, no guarda ni sube el archivo.

## 2. Metas, no-metas y anti-metas

### Metas

1. Por la API externa se envía una plantilla aprobada con encabezado `IMAGE`, `VIDEO` o `DOCUMENT`, mandando el link del archivo.
2. `GET …/connections/{id}/templates` lista esas plantillas y dice qué tipo de encabezado piden, para que el integrador sepa qué
   mandar sin abrir WhatsApp Manager.
3. Un error de contrato (falta el encabezado, sobra, es de otro tipo, el link no es válido) sale en la respuesta del `POST` como
   422, nunca como un envío que Meta rechaza después.
4. El replay con el mismo `idempotencyKey` compara también el encabezado.
5. El asesor ve el encabezado en el hilo de la bandeja (owner, 2026-11-13): el backend lo expone en el mensaje (§4.6) y
   `qep-frontend` lo dibuja (spec hermano `qep-frontend/docs/superpowers/specs/2026-11-13-encabezado-de-plantilla-en-el-hilo-design.md`).

### No-metas

- **La bandeja, las campañas, el bot y las plantillas del sistema no cambian.** Ninguno tiene de dónde sacar un link: para ellos
  una plantilla con encabezado multimedia sigue `sendable = false` con `media_header`, igual que hoy. Abrirlas ahí es otro
  proyecto (necesita dónde guardar el archivo y quién lo elige).
- **Subir el archivo a Meta y enviar por `id`** (opción B) y **un encabezado predeterminado por plantilla configurado en QEP**
  (opción C). El contrato deja lugar para sumarlos (§4.1) sin romper a nadie.
- **Encabezado de texto con variables** (`{{1}}` en un `HEADER` `TEXT`): sigue `other` como hoy.
- **Ubicación (`LOCATION`) como encabezado.** Sigue no enviable.

### Anti-metas (fracaso aunque funcione)

- **QEP hace un request al link.** Ni para validarlo, ni para medir el tamaño, ni para adivinar el tipo. Descargar URLs ajenas
  abre SSRF y mete a QEP en el camino del archivo; la opción A existe justamente para no hacerlo.
- **Un cambio que vuelva enviables las plantillas con encabezado multimedia en la bandeja o en campañas.** Saldrían sin el
  encabezado y Meta las rechazaría (`131008`) después de que el asesor o la campaña creyeron que salieron.
- **Algo específico de un integrador** en QEP (un default de imagen, un nombre de plantilla, un país).

## 3. Cómo funciona hoy (lo que se toca)

| Pieza | Hoy | Archivo |
| --- | --- | --- |
| Parseo del catálogo | `HEADER` no-`TEXT` → `media_header`; guarda sólo el texto del encabezado | `MessageTemplates.cs:326-336` |
| Motivo | `Worst()` guarda **un** motivo por prioridad; `media_header` le gana a `dynamic_url_button` y `other` | `MessageTemplates.cs:304-309` |
| Espejo | `messaging.message_templates` guarda `components` de Meta; `FromStored` lo reparsa con `Parse` | `MessageTemplates.cs:221-245` |
| Exigir enviable | `RequireSendable` → `not_available` / `parameters_mismatch` | `MessageTemplates.cs:252-268` |
| Listado externo | filtra `Sendable`; `ExternalTemplateDto(Name, Language, Category, Parameters)` | `ListExternalTemplates.cs:10-34` |
| Envío externo | `RequireSendable` → `BuildSend` → `meta.SendTemplateAsync` | `SendExternalTemplate.cs:112-171` |
| Lo que viaja a Meta | `TemplateSend(…, HeaderDocument)`; el header va primero | `MessageTemplates.cs:141-147`, `WhatsAppCloudClient.cs:168-215` |
| Detalle guardado / replay | `TemplateDetailsJson.Serialize` y `ParseSend`; el replay compara contra eso | `MessageTemplates.cs:38-…`, `SendExternalTemplate.cs:213-232` |
| Cuerpo del request | `ExternalTemplateRequest(Name, Language, Parameters)`, leído a mano con `JsonSerializerOptions.Web` | `ExternalWhatsAppEndpoints.cs:88-107` |

No hace falta migración: el formato del encabezado ya está en `components` del espejo y se recupera al parsear.

## 4. Diseño

### 4.1 Contrato del `POST /api/v1/external/whatsapp/messages`

`template` suma un campo opcional `header`:

```json
{
  "idempotencyKey": "6f1c…",
  "connectionId": "0b9e…",
  "to": "573001234567",
  "template": {
    "name": "aviso_despacho",
    "language": "es",
    "header": { "type": "image", "link": "https://cdn.example.com/logo.png" },
    "parameters": { "tracking_number": "1014853", "status": "Pendiente", "button:0": "52be1d61-…" }
  }
}
```

```csharp
record ExternalTemplateRequest(string? Name, string? Language, ExternalTemplateHeaderRequest? Header,
    IReadOnlyDictionary<string, string?>? Parameters);
record ExternalTemplateHeaderRequest(string? Type, string? Link, string? Filename);
```

- `type`: `image` | `video` | `document`, en minúsculas como en Meta. Sin distinción de mayúsculas al leer.
- `link`: requerido. URL absoluta `https`, máximo 2048 caracteres, sin espacios ni caracteres de control. **No se resuelve ni se
  pide** (anti-meta). `http` se rechaza: Meta lo acepta, pero un link en claro a un archivo que va a un cliente final no es lo que
  QEP quiere promover.
- `filename`: opcional y **sólo** con `document` (es el nombre que ve el cliente en WhatsApp). Máximo 240, sin `/`, `\` ni
  caracteres de control. Con `image` o `video` es error.
- **Lugar para crecer:** un `id` de media (opción B) entraría como alternativa a `link` (exactamente uno de los dos). No se
  construye ahora.

Validación de forma (FluentValidation, `422 validation.failed` con el mapa `errors`), claves:
`template.header.type`, `template.header.link`, `template.header.filename`.

### 4.2 Contra la plantilla

Después de la validación de forma, contra la plantilla del catálogo (todo `422`, código de dominio):

| Caso | Código |
| --- | --- |
| La plantilla no existe, no está aprobada o no es enviable **por la API externa** (§4.3) | `messaging.template.not_available` (como hoy) |
| La plantilla pide encabezado multimedia y no vino `header` | `messaging.template.header_mismatch` (nuevo) |
| Vino `header` y la plantilla no tiene encabezado multimedia | `messaging.template.header_mismatch` |
| Vino `header` de otro tipo (`video` para una `IMAGE`) | `messaging.template.header_mismatch` |
| Claves de `parameters` distintas | `messaging.template.parameters_mismatch` (como hoy) |

`header_mismatch` es un solo código con el `detail` diciendo qué pide la plantilla (`"The template requires an image header."`):
el integrador lo corrige mirando el listado (§4.4), y tres códigos para el mismo arreglo no le dan nada. Se agrega a
`MessagingErrorCodes`, al README y a la tabla de errores externos.

Un link que Meta no puede bajar (404, tipo o tamaño que Meta no acepta) **no** se detecta acá: Meta acepta el envío y lo marca
fallido después por webhook. El integrador lo ve en `GET /messages/{id}` como `Failed` con `failureCode`/`failureReason`, que ya
existen. Es el costo aceptado de la opción A; el README lo dice.

### 4.3 Enviable por la API externa, sin tocar a los demás

El catálogo hoy colapsa todos los motivos en uno (`Worst`), así que «sólo le falta el encabezado» no se puede leer del motivo:
una plantilla con imagen **y** un botón no soportado muestra `media_header` y esconde el otro. Por eso:

- `Parse` calcula, además de lo de hoy, el **formato del encabezado multimedia** (`Image` | `Video` | `Document` | `null`; un
  `LOCATION` o un formato desconocido sigue siendo `other`) y si la plantilla sería enviable **ignorando sólo** el encabezado
  multimedia. Se expone en `MessageTemplateDto` como dos campos nuevos (nombres al constructor: p. ej. `HeaderMedia` y
  `SendableWithHeaderMedia`).
- `Sendable` y `UnsupportedReason` **no cambian de valor** para ninguna plantilla. La bandeja, las campañas, el bot, las plantillas
  del sistema y la pantalla de plantillas leen esos dos y siguen viendo lo mismo.
- La API externa usa su propia regla: enviable si `Sendable`, **o** si `SendableWithHeaderMedia` y la plantilla tiene
  `HeaderMedia`. Va en un `RequireSendable` externo (o un parámetro explícito de `RequireSendable`), nunca cambiando el
  compartido. La categoría `AUTHENTICATION`, el carrusel y el flujo siguen bloqueando.
- **Las plantillas del sistema de cotización y pedido quedan disponibles por la API externa** (owner, 2026-11-13). Tienen encabezado
  `DOCUMENT` y con esta regla pasan a listarse y enviarse con el PDF que mande el integrador. Son plantillas aprobadas en la WABA
  del tenant y el tenant dispone de ellas; QEP no las filtra. Una prueba lo deja explícito (§5).
- `QuotationTemplateProvisioning` y `OrderTemplateProvisioning` miran `media_header` hoy (`:89`, `:78`) para su encabezado
  `DOCUMENT`; siguen funcionando igual porque el motivo no cambia. Si al construir queda más limpio que lean `HeaderMedia`, es
  decisión del constructor, con sus pruebas verdes.

### 4.4 Listado externo

`ExternalTemplateDto` suma `Header`: `null` sin encabezado multimedia, o `{ "type": "image" | "video" | "document" }`.

```json
{ "name": "aviso_despacho", "language": "es", "category": "UTILITY",
  "header": { "type": "image" }, "parameters": ["tracking_number", "status", "button:0"] }
```

El listado incluye las plantillas enviables por la regla externa (§4.3). Es un campo nuevo en una respuesta existente: aditivo,
un integrador que no lo lee no se rompe. Las que el listado ya devolvía no cambian.

### 4.5 Lo que viaja a Meta y lo que se guarda

- `TemplateSend` lleva el encabezado en un solo campo general (tipo + `link` | `id` + `filename`), que reemplaza a
  `HeaderDocument`; la cotización pasa a usarlo con `id`. `WhatsAppCloudClient` arma el componente `header` primero, como hoy:
  `{"type":"image","image":{"link":…}}`, `{"type":"video","video":{"link":…}}`,
  `{"type":"document","document":{"link":…,"filename":…}}`. Si el constructor prefiere sumar un campo al lado de
  `HeaderDocument` en vez de reemplazarlo, vale; lo que importa es una sola forma de armar el componente.
- `TemplateDetailsJson` guarda el encabezado (`"header": {"type","link","filename"}`) junto a `parameters`. Así el replay lo
  compara y el hilo lo tiene para cuando el frontend quiera dibujarlo. Un detalle sin `header` (todo lo existente) se lee como
  «sin encabezado».
- **Replay:** mismo `idempotencyKey` con otro `header` (tipo, link o filename distintos, o uno con y otro sin) es
  `409 external.idempotency_key_reused`, como cualquier otro cambio del cuerpo. Eso vale para el replay que hoy pasa por
  `Repeat`/`SameTemplate`. **El reenvío de un mensaje que se puede reenviar (`CanResend`, p. ej. `Failed`) sigue sin comparar
  y sale con el cuerpo nuevo, como hoy** (decisión del diseñador): si Meta marcó `Failed` porque no pudo bajar el link, el
  integrador corrige el link y reintenta con la misma clave.
- **Logs y registro de llamadas:** el link es dato del integrador, puede llevar un token en la query. No se escribe en logs; el
  registro de llamadas externas (15 días) guarda lo que ya guarda hoy y no suma el cuerpo.

### 4.6 El encabezado en el hilo

- `MessageTemplateRefDto` (`MessagingDtos.cs:251`, hoy `Name, Language`) suma `Header`: `null`, o
  `{ "type": "image" | "video" | "document", "link": "https://…", "filename": "…" | null }`, leído del `header` de `DetailsJson`
  por `TemplateDetailsJson.ParseRef`. Sólo se expone un encabezado con `link`; uno con `id` (la cotización enviada desde QEP) no
  sale, y la burbuja sigue como hoy. Un `link` guardado que no sea `https` absoluto se omite (`Header = null`): el frontend
  también lo valida, pero el backend no entrega algo que el contrato no habría aceptado.
- El hilo ya devuelve `template` en cada mensaje (`MessageDto`), y el evento de tiempo real del mensaje usa el mismo DTO: es un
  campo nuevo en respuestas existentes, aditivo.
- El `Text` del mensaje es el de hoy: cuerpo con los valores, sin el encabezado. No se agrega «[imagen]» ni el link al texto: el
  texto lo leen el resumen de la conversación y las búsquedas.
- **Riesgos aceptados por el owner (2026-11-13):** el navegador del asesor pide el link al dueño del link (rastreo: IP, navegador,
  hora) y el contenido detrás del link puede cambiar después del envío. Cerrarlos exigiría que QEP copie el archivo, que es la
  anti-meta de §2. Sólo puede mandar un link quien tiene una API key del tenant.

## 5. Pruebas

TDD, RED antes que GREEN. Mínimo:

- **Unitarias de `TemplateCatalog`:** `IMAGE`, `VIDEO`, `DOCUMENT` → `HeaderMedia` y `SendableWithHeaderMedia = true`, con
  `Sendable = false` y `UnsupportedReason = media_header` **sin cambio**; imagen + botón no soportado → `SendableWithHeaderMedia =
  false`; `LOCATION` → no enviable; `FromStored` da lo mismo que `Normalize`.
- **Unitarias del armado a Meta:** JSON exacto del componente `header` para los tres tipos, primero en `components`; la
  cotización sigue saliendo igual (prueba existente verde).
- **Integración de la API externa** (contra el fake de Meta, como las de hoy): envío con imagen → 201 y el fake recibió el
  componente; sin `header` para una plantilla con imagen → `422 header_mismatch`; `header` para una sin imagen → 422; tipo
  equivocado → 422; link `http`, relativo o de 2049 → `422 validation.failed` con la clave; `filename` con `image` → 422; replay
  igual → 200; replay con otro link → 409; listado con `header`. **Ninguna prueba hace un request al link** (el fake de Meta no lo
  baja). Reenvío de un `Failed` con la misma clave y otro link → sale con el link nuevo. La plantilla del sistema de cotización
  aparece en el listado con `header.type = document` y se envía con un link.
- **Hilo:** `GET` de los mensajes de la conversación devuelve `template.header` con tipo, link y filename tras un envío externo con
  encabezado; `null` sin encabezado y para un mensaje viejo sin `header` en `DetailsJson`; un `DetailsJson` con un link `http`
  (sembrado a mano) sale con `header = null`.
- **No regresión:** la bandeja y las campañas siguen rechazando una plantilla con imagen (`not_available`) — una prueba por camino.

## 6. Entrega

- Rama `feature/api-externa-encabezado-multimedia` desde `develop`; merge a `develop`. `develop` → `main` lo lleva el owner.
- README § API externa: el campo `header`, el campo `header` del listado, el código `header_mismatch` y la nota de que un link
  que Meta no puede bajar se ve como `Failed` después.
- Sin migración ni cambio de configuración. El backend va primero: el frontend lee un campo que sin el backend llega ausente y no
  dibuja nada, así que el orden de despliegue no rompe.

## 7. Lo que queda al constructor

- Nombres exactos de los campos nuevos de `MessageTemplateDto` y de la regla externa.
- Si `TemplateSend` reemplaza `HeaderDocument` o suma al lado (§4.5).
- Dónde vive la validación del link (validador del comando, con un helper reusable).

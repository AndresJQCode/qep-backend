# Soporte (PQRSF) — Variables en los documentos legales del portal · diseño

> **Estado:** aprobado por el owner 2026-11-16 (decisiones D-F8, V-1 y V-2 abajo).
> **Construye sobre:** documentos legales del portal en `develop` (spec
> `2026-11-15-support-portal-legal-documents-design.md`, plan `2026-11-15-support-portal-legal-documents-backend.md`) y la empresa
> que responde del formulario (spec 2026-11-15 §9.3, D-6 y D-8).
> **Consola:** el editor de la consola se construye en paralelo contra el contrato de §3; ningún nombre cambia sin avisar.

## 1. Problema

El aviso de privacidad, el aviso anónimo y los términos nombran a quien trata los datos: la razón social, su NIT y el nombre del
negocio. Hoy el tenant los escribe a mano en cada documento, y cuando cambia la empresa que responde en Ajustes › Formulario público
los documentos quedan diciendo otra cosa. La pregunta de autorización tiene el mismo problema.

## 2. Decisiones del owner (2026-11-16)

| # | Decisión |
| --- | --- |
| D-F8 | Los tres documentos legales y la pregunta de autorización aceptan variables que el backend resuelve al servirlos. Lo guardado conserva el token: la versión publicada es la plantilla, no el texto resuelto. |
| V-1 | Cuatro variables, con nombre ASCII: `empresa` (la empresa que responde, `Organization.Name`), `tipo_documento` (`Organization.DocumentTypeCode` tal cual, p. ej. `NIT`), `documento_identificacion` (`Organization.DocumentNumber`) y `nombre_comercial` (el nombre del tenant). Una sola lista en el módulo, con etiqueta y ejemplo. |
| V-2 | La pregunta de autorización es texto plano: se resuelve con el valor tal cual, sin codificar. La pregunta por defecto no cambia. |

## 3. Contrato

**Sintaxis.** `{{nombre}}`, con espacios opcionales adentro (`{{ empresa }}`). El nombre es de letras minúsculas y guion bajo.

**Lista** (`SupportLegalVariables.All`, en `Modules.Support.Domain`):

| `name` | `label` | `example` |
| --- | --- | --- |
| `empresa` | Empresa que responde | Comercial Andina S.A.S. |
| `tipo_documento` | Tipo de documento de la empresa | NIT |
| `documento_identificacion` | Número de documento de la empresa | 900123456-7 |
| `nombre_comercial` | Nombre del negocio | Comercial Andina |

**Consola.** `GET /api/v1/tenants/{tenantId}/support/settings/legal-documents` suma `variables` como **último** campo:

```json
{
  "privacyNotice": { … },
  "anonymousNotice": { … },
  "terms": { … },
  "variables": [
    { "name": "empresa", "label": "Empresa que responde", "example": "Comercial Andina S.A.S." },
    { "name": "tipo_documento", "label": "Tipo de documento de la empresa", "example": "NIT" },
    { "name": "documento_identificacion", "label": "Número de documento de la empresa", "example": "900123456-7" },
    { "name": "nombre_comercial", "label": "Nombre del negocio", "example": "Comercial Andina" }
  ]
}
```

**Publicar** (`POST …/settings/legal-documents/{kind}`): todo `{{…}}` cuyo contenido, sin los espacios de los lados, no sea un nombre de
la lista —en el HTML ya saneado o en `authorizationQuestion`— es `422 support.legal_document.unknown_variable`, sin versión nueva. Eso
incluye `{{Empresa}}`, `{{}}` y `{{ciudad}}`: un token mal escrito no llega al portal como texto literal. Lo guardado conserva los
tokens; no se resuelve nada al publicar. La respuesta y las lecturas de la consola muestran los tokens.

**Servir** (`GET /api/v1/public/support/{slug}/form`): los tres documentos y la pregunta llegan resueltos.

- Los valores van **codificados como HTML** en los documentos (`&` → `&amp;`, `<` → `&lt;`, `>` → `&gt;`, `"` → `&quot;`,
  `'` → `&#39;`); una razón social con `&` o `<` no rompe ni inyecta marcado.
- En la pregunta, **sin codificar** (V-2): el portal (`qep-frontend-support`, `features/filing/step-privacy.tsx`) la pinta como texto
  de React (`{legal.privacyNotice.authorizationQuestion}`), que ya escapa; codificar mostraría `&amp;` al visitante.
- La empresa es la de la versión publicada del formulario (`SupportPublicOrganization.OfAsync`), la misma que `organization` del
  `GET /form`.
- Si un documento o la pregunta usa `empresa`, `tipo_documento` o `documento_identificacion` y el formulario no tiene empresa
  (publicado antes de D-8: `organization: null`), el portal responde el mismo `404 support.public.not_found` que con cualquier otra
  configuración incompleta. `nombre_comercial` siempre se resuelve.
- Un texto resuelto que ya no cabe (HTML de más de 60 000 caracteres o pregunta de más de 500) es el mismo 404: la foto de la
  aceptación no lo admitiría y es mejor no servir que radicar mal.

**Radicar** (`POST …/tickets`): la foto de cada aceptación guarda el HTML y la pregunta **resueltos**, lo que la persona vio y aceptó.
Las versiones del cuerpo siguen comparándose contra la versión publicada (una plantilla), no contra el texto resuelto.

## 4. Diseño

- Un solo lugar resuelve: `SupportServedLegal.ReadAsync`, que ya usan `GetPublicForm` y `FilePublicTicket`. Recibe la empresa
  (`SupportPublicOrganizationDto?`); `GetPublicForm` la lee una vez y la usa para la respuesta y para resolver, y `FilePublicTicket`
  la lee con `ISupportOrganizationDirectory`.
- La validación al publicar vive en el dominio, `SupportLegalDocumentVersion.Create`, junto a las otras reglas del contenido: así
  ningún llamador (consola, siembra de pruebas) guarda un token desconocido.
- Al servir, un token con un nombre desconocido (una fila escrita a mano) se deja literal: la validación es al publicar.
- Lo resuelto no se vuelve a sanear: el HTML de base ya salió del saneador y los valores van codificados.

## 5. Lo que no cambia

- Los ejemplos de QEP (`SupportLegalDocumentExamples`), la pregunta por defecto, el historial y la lectura de versiones viejas.
- `organization` del `GET /form` y del `GET /consult`.
- Las fotos ya guardadas: tienen el texto que se sirvió en su momento.

## 6. Deploy

Sin migración: no cambia ninguna columna. La consola puede salir antes o después; sin la consola nueva el campo `variables` se
ignora, y sin el backend nuevo un token publicado se mostraría literal (por eso el backend sale primero).

## 7. Pendiente

- Un token escrito con un espacio duro (`{{&nbsp;empresa}}`, lo que algunos editores insertan) se rechaza como desconocido: el saneador
  lo serializa como la entidad. Si el editor lo produce, se resuelve en la consola insertando el token desde la lista.

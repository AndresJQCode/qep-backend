# Soporte — documentos legales del portal público · diseño

> Estado: aprobado por el owner (2026-11-15), con D-1 a D-5 resueltas (§15). Complementa la fase 3 «Formulario público»
> (`2026-11-14-support-tickets-pqrs-design.md` §9 y plan `2026-11-15-support-phase-3-public-form-backend.md`), ya en
> `develop`, y **reemplaza su §9.5**. Corrige su §9.1 en el mismo commit: el portal vive en un repo nuevo,
> `qep-frontend-support`. Diseño aprobado del portal: https://claude.ai/artifact/CrSPJi5uVd9Co9v9XkSHuK (tablero
> «Portal de soporte público»: radicar en 4 pasos, radicado y PIN, consultar). Si el tablero y este spec discrepan,
> manda el spec.

Convención de marcas:

- **[owner]**: decisión tomada por el owner (2026-11-15). No se reabre.
- **[diseñador]**: propuesta de este documento.
- **D-n**: decisión del owner del 2026-11-15, resuelta en §15.

## 1. Intención

El portal público pide al solicitante, en cuatro pasos, aceptar documentos que escribe cada tenant: el aviso de
privacidad con la autorización de tratamiento de datos, un aviso para el trámite anónimo y los Términos y Condiciones del
Sistema de Atención al Cliente (SAC). Hoy el backend tiene un solo bloque de «textos legales» en texto plano con una
aceptación por ticket que sólo cubre el tratamiento de datos. Este spec define los tres documentos en texto enriquecido y
versionados, la prueba por ticket de lo aceptado (también de los términos) y el contrato que consumen la consola y el
portal.

## 2. Objetivos, no objetivos y anti-objetivos

**Objetivos**

- Tres documentos por tenant, editados con un editor de texto enriquecido y **versionados** [owner]:
  `PrivacyNotice` (aviso de privacidad con datos), `AnonymousNotice` (aviso anónimo) y `Terms` (Términos y Condiciones
  del SAC).
- La aceptación de los términos se guarda **por ticket, igual que el consentimiento de datos de hoy**: qué versión,
  cuándo y desde dónde [owner].
- El contenido enriquecido se **sanea en el servidor** con una lista blanca: el portal lo pinta como HTML y el riesgo
  principal es XSS.
- El texto de caracterización («Los datos solicitados en este formulario serán tratados para caracterizar…») es **fijo
  para todos los tenants** [owner] y sale del backend (§8).
- La consola ve, en el detalle del ticket, qué se aceptó y puede abrir la foto exacta.

**No objetivos**

- Borradores guardados en el servidor. El editor guarda su borrador en el navegador; publicar crea la versión (§5.3).
- Reaceptación: un ticket radicado conserva la versión que aceptó; publicar una versión nueva no pide nada a nadie.
- Imágenes, tablas, colores o estilos dentro de los documentos (§6.2). Sumar etiquetas después es aditivo.
- El editor de la consola y las pantallas del portal: son de `qep-frontend` y `qep-frontend-support`, con su propio spec.

**Anti-objetivos**

- Guardar HTML sin sanear, o confiar en que el portal o la consola lo sanean: el backend entrega HTML ya seguro.
- Editar o borrar una versión publicada o una aceptación: son prueba legal, sólo se insertan.
- Guardar IP, documento o datos personales de un trámite anónimo (spec §9.4, `CK_tickets_anonymous`).

## 3. Estado actual en el código (verificado en `develop`, `e28ecd0d`)

**Textos legales.** `SupportLegalTextVersion` (`Modules.Support.Domain/SupportLegalTextVersion.cs`), tabla
`support.legal_text_versions`: **un solo bloque versionado** por tenant con `authorization_text` (1–20 000, obligatorio),
`policy_text` (≤ 20 000) o `policy_url` (https ≤ 500), al menos uno de los dos (`CK_legal_text_versions_policy`,
código `support.legal_texts.policy_required`), y `terms_text` opcional (≤ 20 000). Texto plano. Versión `n+1` con
`If-Match: n` (`PublishLegalTexts.cs`), índice único `IX_legal_text_versions_tenant_version` (409
`support.legal_texts.version_taken`). Sin versión publicada, `GET` devuelve el ejemplo de QEP (`Example`, versión 0,
`IsExample`), que no se guarda. Insert-only y el `Down` de `AddSupportPublicForm` no borra la tabla.

**Consentimiento.** `SupportConsent` (`SupportConsent.cs`), tabla `support.consents`, una fila por ticket no anónimo
(`IX_consents_ticket`), sin FK, en la misma transacción que el ticket (`FilePublicTicket.cs:299-302`): `legal_text_version_id`,
`legal_text_version`, `accepted_at`, foto de los cuatro textos y `visitor_ip` (≤ 45). Un anónimo no deja fila.

**Contrato.** Consola: `GET` y `POST /settings/legal-texts` (`SupportEndpoints.cs:411-419`, `SettingsView` y
`SettingsManage`), `SupportLegalTextsDto`. Público: `GET /form` trae `SupportPublicLegalDto(Version, AuthorizationText,
PolicyText, PolicyUrl, TermsText)` (`GetPublicForm.cs:54`); `POST /tickets` recibe `AcceptsDataPolicy` y
`LegalTextVersion` (`SupportPublicDtos.cs:69-79`). Códigos: `support.public.consent_required`,
`support.public.legal_texts_outdated`, `support.public.consent_duplicate`. Publicar el formulario exige una versión de
textos (`support.form.legal_texts_required`, bloqueador `legal_texts_required` en `GetFormSettings.cs:38`). Auditoría
`support.legal_texts.published`. `legal_text_versions.created_by` entra en `SupportUserReferenceProbe`.

**Quién lo consume.** Nadie todavía: ni `qep-frontend` ni otro repo llaman `legal-texts` (búsqueda en el workspace,
2026-11-15), y el repo del portal no existe. La fase 3 está en `origin/develop` pero **no en `main`**: producción no
tiene ni versiones ni consentimientos. Sólo el entorno de desarrollo puede tener filas.

**Detalle del ticket.** `SupportTicketDetailDto` (`SupportDtos.cs:88-116`) no expone el consentimiento.

**Sanitizador.** El repo no tiene ninguna librería de saneamiento HTML (sin `HtmlSanitizer`, `AngleSharp` ni similar).

### Brecha

| Hoy | Lo que pide el portal |
| --- | --- |
| Un bloque con cuatro textos | Tres documentos que se versionan por separado |
| Texto plano | Texto enriquecido saneado |
| Sin aviso anónimo | Aviso anónimo en el paso 1 del trámite anónimo |
| Términos opcionales y aceptados de paso con los datos | Términos obligatorios, paso 3 propio, «No acepto» bloquea, también para el anónimo |
| Sólo el consentimiento de datos por ticket | Aceptación por ticket de cada documento que aplica |
| Pregunta de autorización implícita | Pregunta explícita «¿Autoriza usted a …?» con «Sí acepto / No acepto» |
| Sin texto de caracterización | Texto fijo en el paso 2 |
| Consentimiento invisible en la consola | Aceptaciones en el detalle del ticket |

## 4. El portal, paso por paso (lo que el backend tiene que servir)

Del tablero aprobado [owner]:

1. **Privacidad.** «¿Quieres hacer el trámite como persona anónima?». Con datos: `PrivacyNotice` y la pregunta de
   autorización con «Sí acepto / No acepto»; «No acepto» no deja seguir (el portal ofrece el anónimo). Anónimo:
   `AnonymousNotice`, sin pregunta, y salta el paso 2.
2. **Tus datos.** Campos según el tipo de documento (`SupportPublicFieldsDto`, sin cambios; con NIT, razón social). Al
   pie, el texto fijo de caracterización.
3. **Términos.** `Terms` en un recuadro con scroll y «Acepto / No acepto»; «No acepto» bloquea («Al no aceptar los
   términos y condiciones, no se continúa con su radicación»). Aplica a los dos caminos.
4. **Tu caso.** Tipo, asunto, descripción, adjuntos y Turnstile; radicar.

Más las pantallas «Radicado y PIN» y «Consultar», que no muestran documentos. El pie del portal enlaza «Política de
tratamiento de datos» (§6.4).

## 5. Modelo de datos

### 5.1 Versión de un documento

Entidad `SupportLegalDocumentVersion`, tabla `support.legal_document_versions`, **insert-only** [diseñador]:

| Columna | Tipo | Regla |
| --- | --- | --- |
| `id` | uuid PK | v7 |
| `tenant_id` | uuid | |
| `kind` | varchar(32) | `PrivacyNotice`, `AnonymousNotice`, `Terms` (`CK_legal_document_versions_kind`) |
| `version` | int | ≥ 1, consecutiva por `(tenant_id, kind)` (`CK_legal_document_versions_version`) |
| `content_html` | varchar(60 000) | HTML **ya saneado** (§6), con texto visible |
| `authorization_question` | varchar(500) null | Sólo `PrivacyNotice` (D-1); texto plano |
| `policy_url` | varchar(500) null | Sólo `PrivacyNotice` (D-2); https absoluta (`SupportLegalTextVersion.IsValidPolicyUrl`, se reusa) |
| `created_at` | timestamptz | reloj del tenant, como hoy |
| `created_by` | uuid | membresía que publicó; entra en `SupportUserReferenceProbe` |

- Índice único `IX_legal_document_versions_tenant_kind_version` sobre `(tenant_id, kind, version)`; índice
  `IX_legal_document_versions_created_by` para la sonda de miembros.
- `CK_legal_document_versions_privacy_only`: `authorization_question` y `policy_url` son `NULL` salvo con
  `kind = 'PrivacyNotice'`.
- Sin mutadores en el dominio; el `Down` de la migración no borra la tabla (precedente `customers.data_consents` y
  `legal_text_versions`).

**Versión vigente** = la de mayor `version` para `(tenant_id, kind)`. No hay columna `is_current` que mantener.

**Ejemplo.** Sin versión publicada de un tipo, la consola recibe el ejemplo de QEP de ese tipo (versión 0,
`isExample: true`), que no se guarda ni se puede aceptar, como `SupportLegalTextVersion.Example` hoy. El del aviso de
privacidad parte del ejemplo actual (autorización y política) convertido a párrafos. Los otros dos son nuevos, cortos y
marcados como ejemplo, y el abogado los revisa antes de salir a producción (D-4).

### 5.2 Aceptación por ticket

Entidad `SupportLegalAcceptance`, tabla `support.legal_acceptances`, **insert-only**, espejo de `support.consents`
[owner: «igual que el consentimiento de hoy»]:

| Columna | Tipo | Regla |
| --- | --- | --- |
| `id` | uuid PK | |
| `tenant_id` | uuid | |
| `ticket_id` | uuid | sin FK |
| `kind` | varchar(32) | el del documento |
| `document_version_id` | uuid | sin FK |
| `document_version` | int | ≥ 1 |
| `accepted_at` | timestamptz | la hora del radicado |
| `content_html` | varchar(60 000) | **foto** del documento aceptado |
| `authorization_question` | varchar(500) null | foto de la pregunta tal como se mostró (§6.4), sólo `PrivacyNotice` |
| `policy_url` | varchar(500) null | foto, sólo `PrivacyNotice` |
| `characterization_text` | varchar(1 000) null | foto del texto fijo de §8, sólo `PrivacyNotice` |
| `visitor_ip` | varchar(45) null | obligatoria en `PrivacyNotice`; `NULL` en `AnonymousNotice`; en `Terms`, la del radicado con datos y `NULL` en el anónimo |

- Índice único `IX_legal_acceptances_ticket_kind` sobre `(ticket_id, kind)`: una por tipo y ticket.
- `CK_legal_acceptances_ip`: `kind <> 'PrivacyNotice' OR visitor_ip IS NOT NULL` y
  `kind <> 'AnonymousNotice' OR visitor_ip IS NULL`. Que un `Terms` anónimo no tenga IP lo garantiza el dominio
  (`Record` recibe la IP sólo del camino con datos) y lo amarra una prueba (§13), porque el anonimato vive en `tickets`.
- Sin FK ni `Down` que la borre. Se escribe en la misma transacción que el ticket.

Qué filas deja cada radicado:

| Camino | `PrivacyNotice` | `AnonymousNotice` | `Terms` |
| --- | --- | --- | --- |
| Con datos | sí, con IP | no | sí, con IP |
| Anónimo | no | sí, sin IP («leído y continuó») | sí, sin IP |

La fila `AnonymousNotice` no es un consentimiento: prueba qué se le dijo a la persona (por ejemplo, que el PIN no se
recupera). `accepted_at` significa ahí «continuó después de verlo» [diseñador].

### 5.3 Borrador contra publicado

No hay borradores en el servidor [diseñador]. Cada «Publicar» del editor crea la versión `n+1` con `If-Match: n`, como
`PublishLegalTexts` hoy; el texto en edición vive en el navegador (precedente: borrador local de cotizaciones en
`qep-frontend`). Así toda versión que existe es una que pudo ver un solicitante, y no hay un estado intermedio que
probar. Publicar siempre crea versión, aunque el contenido no cambie (acción explícita, como hoy).

### 5.4 Lo que pasa con las tablas de la fase 3

- `support.legal_text_versions` y `support.consents` **quedan como están**, mapeadas en `SupportDbContext` pero sin
  escritor. Quitar sus entidades del modelo haría que la siguiente migración de EF las borrara: no se hace.
- Las rutas `GET`/`POST /settings/legal-texts`, sus handlers, `SupportLegalTextsDto` y la escritura de `SupportConsent`
  se eliminan (no tienen consumidor, §3).
- `legal_text_versions.created_by` sigue en `SupportUserReferenceProbe`; se suma `legal_document_versions.created_by`.

## 6. Texto enriquecido y saneamiento

### 6.1 Formato de almacenamiento

**HTML saneado y canónico** [diseñador]. El editor de la consola manda HTML; el backend lo sanea con una lista blanca y
guarda **la salida del saneador**, que es lo que devuelve la respuesta del `POST` (el editor se repinta con eso). Por qué
HTML y no Markdown ni el JSON de un editor: el portal (Preact) y la consola (React) lo pintan sin un renderizador
compartido, la foto de la aceptación es legible tal cual en SQL, y no ata el contrato a un editor concreto
(`qep-frontend` no tiene ninguno hoy).

### 6.2 Lista blanca

| Elemento | Atributos |
| --- | --- |
| `p`, `br`, `strong`, `em`, `u`, `s`, `h2`, `h3`, `h4`, `ul`, `ol`, `li`, `blockquote`, `hr` | ninguno |
| `a` | sólo `href`, con esquema `https:`, `mailto:` o `tel:`; el saneador agrega `rel="noopener noreferrer nofollow"` y `target="_blank"` |

- Sin `class`, `style`, `id`, `data-*` ni manejadores `on*`. Sin URL relativas, `http:`, `javascript:`, `data:` ni
  `vbscript:` (después de decodificar entidades y quitar espacios y controles).
- `script`, `style`, `template`, `noscript`, `iframe`, `object`, `embed`, `svg`, `math`, `form`, `input`, `img`,
  `video`, `audio` y comentarios se **quitan con su contenido**. Cualquier otro elemento fuera de la lista (`span`, `div`,
  `font`, `b`, `i`…) se **desenvuelve**: se conserva su texto. `b` e `i` se pueden normalizar a `strong` y `em` si la
  librería lo permite; si no, se desenvuelven.
- El resultado tiene que ser **idempotente**: sanear dos veces da los mismos bytes. Esa propiedad es la que deja
  re-sanear al servir sin cambiar la foto.

### 6.3 Dónde se sanea

- **Al publicar** (consola): puerto `ISupportHtmlSanitizer` en `Modules.Support.Application`, implementado en
  `Modules.Support.Infrastructure` sobre `HtmlSanitizer` (mganss, sobre AngleSharp), con versión fija y lock file
  regenerado (D-3). Application no referencia la librería (`SupportLayerTests`).
- **Al servir al portal** (`GET /form`): se vuelve a sanear lo leído. Con datos canónicos es un no-op, y protege contra
  una fila escrita por un camino que no pasó por el puerto (el relleno de §11, una corrección a mano en SQL o una lista
  blanca vieja). Tres documentos por request bajo el limitador `SupportPublicRead` (60/min/IP): costo despreciable.
- **La foto de la aceptación** guarda exactamente lo que se sirvió.
- El portal pinta el HTML recibido (`dangerouslySetInnerHTML` en la isla); su CSP es defensa adicional y la define su
  spec. La consola hace lo mismo con el HTML de la API.

### 6.4 Límites y la pregunta de autorización

- `content_html`: máximo 60 000 caracteres **después** de sanear, y al menos un carácter de texto visible. El cuerpo crudo
  se rechaza antes de sanear si pasa de 120 000 (tope contra abuso, no regla de negocio).
- `authorization_question` (D-1): texto plano de 1 a 500, opcional. Sin ella, el backend arma la pregunta por defecto con
  el nombre del tenant: «¿Autorizas a {TenantName} para el tratamiento de los datos personales suministrados en este
  formulario?». El tablero muestra una variante con «sus sociedades subsidiarias, matrices y/o económicamente
  vinculadas», que no aplica a todos los tenants: por eso es editable. Lo que se muestra (la escrita o la por defecto)
  es lo que guarda la foto.
- `policy_url` (D-2): https opcional. El portal la usa en el enlace del pie «Política de tratamiento de datos»; sin ella,
  el pie no lo muestra.

## 7. Contrato de la consola

Bajo `/api/v1/tenants/{tenantId:guid}/support`. Permisos existentes; ninguno nuevo.

| Método y ruta | Permiso | Respuesta |
| --- | --- | --- |
| `GET /settings/legal-documents` | `SettingsView` (política `support.settings.view`) | `SupportLegalDocumentsDto` |
| `GET /settings/legal-documents/{kind}/versions` | `SettingsView` | `SupportLegalDocumentHistoryDto` |
| `GET /settings/legal-documents/{kind}/versions/{version:int}` | `SettingsView` | `SupportLegalDocumentDto` |
| `POST /settings/legal-documents/{kind}` (`If-Match: <version>`, `0` si no hay) | `SettingsManage` | `201 SupportLegalDocumentDto` |
| `GET /tickets/{ticketId:guid}/legal-acceptances/{kind}` | `TicketView` (política `support.ticket.view`) | `SupportLegalAcceptanceSnapshotDto` |

`{kind}` en la ruta: `privacy-notice`, `anonymous-notice`, `terms`. En los cuerpos y las respuestas viaja como
`PrivacyNotice`, `AnonymousNotice`, `Terms`. Las rutas `GET`/`POST /settings/legal-texts` desaparecen.

- `SupportLegalDocumentsDto(SupportLegalDocumentDto PrivacyNotice, SupportLegalDocumentDto AnonymousNotice,
  SupportLegalDocumentDto Terms)`: la vigente de cada uno, o su ejemplo.
- `SupportLegalDocumentDto(string Kind, int Version, bool IsExample, string ContentHtml, string? AuthorizationQuestion,
  string? PolicyUrl, string? DefaultAuthorizationQuestion, DateTimeOffset? CreatedAt, SupportMemberDto? CreatedBy)`.
  `DefaultAuthorizationQuestion` sólo en `PrivacyNotice`: la pregunta que se mostraría si `AuthorizationQuestion` es
  `null`, para que el editor la ofrezca sin armarla en el navegador.
- `SupportLegalDocumentHistoryDto(string Kind, IReadOnlyList<SupportLegalDocumentVersionItemDto> Versions)`;
  `SupportLegalDocumentVersionItemDto(int Version, DateTimeOffset CreatedAt, SupportMemberDto CreatedBy)`, de la más nueva
  a la más vieja, sin contenido. Vacía si no hay publicadas.
- `PublishLegalDocumentRequest(string? ContentHtml, string? AuthorizationQuestion, string? PolicyUrl)`. Los dos últimos
  sólo se aceptan con `privacy-notice`; con otro tipo, `validation.failed` en su campo.
- `SupportLegalAcceptanceSnapshotDto(string Kind, int Version, DateTimeOffset AcceptedAt, string? VisitorIp,
  string ContentHtml, string? AuthorizationQuestion, string? PolicyUrl, string? CharacterizationText)`. El ticket tiene
  que ser del tenant; sin fila de ese tipo, 404.

**Detalle del ticket.** `SupportTicketDetailDto` suma `IReadOnlyList<SupportLegalAcceptanceDto> LegalAcceptances`
(lista vacía si no hay, nunca `null`), en el orden `PrivacyNotice`, `AnonymousNotice`, `Terms`:
`SupportLegalAcceptanceDto(string Kind, int Version, DateTimeOffset AcceptedAt, string? VisitorIp)`. Sin el contenido:
tres documentos de hasta 60 000 caracteres no viajan en cada lectura del detalle; la foto se abre con la ruta de arriba.

**Formulario.** `SupportFormSettingsDto.PublishBlockers` cambia `legal_texts_required` por `privacy_notice_required`,
`anonymous_notice_required` y `terms_required` (uno por documento sin versión publicada). Publicar con cualquiera de los
tres pendiente sigue respondiendo `422 support.form.legal_texts_required`: el código no cambia, la lista dice cuál falta.
Los tres son obligatorios porque el formulario siempre ofrece el trámite anónimo (spec §9.3) y los términos aplican a los
dos caminos [owner].

## 8. Texto de caracterización

**Recomendación adoptada: constante del backend, servida por `GET /form` y guardada en la foto** [diseñador].
`SupportLegalNotices.Characterization` en `Modules.Support.Domain`, con el texto exacto del tablero:

> Los datos solicitados en este formulario serán tratados para caracterizar a los usuarios de la Entidad y procurar una
> mejora continua en la atención.

Por qué en el backend y no en el portal: declara una **finalidad del tratamiento** (caracterizar usuarios) que el aviso
del tenant puede no mencionar. Si hay una disputa, la prueba de lo que se le dijo a la persona tiene que estar junto a
su aceptación, y sólo el backend escribe la foto (`characterization_text` en la fila `PrivacyNotice`). Escrito en el
portal, cambiarlo no dejaría rastro por ticket. No es editable por el tenant [owner]. No es una bifurcación real: si el
owner prefiriera el portal, se perdería la foto, que es la razón de ser del texto.

## 9. Contrato público

Bajo `/api/v1/public/support/{tenantSlug}`, sin cambios de grupo, CORS, CSRF, `no-store`, tope de 64 KB ni limitadores.

**`GET /form`** cambia `Legal`:

- `SupportPublicLegalDto(SupportPublicPrivacyNoticeDto PrivacyNotice, SupportPublicLegalDocumentDto AnonymousNotice,
  SupportPublicLegalDocumentDto Terms, string CharacterizationNotice)`.
- `SupportPublicPrivacyNoticeDto(int Version, string ContentHtml, string AuthorizationQuestion, string? PolicyUrl)`:
  `AuthorizationQuestion` ya resuelta (la escrita o la por defecto).
- `SupportPublicLegalDocumentDto(int Version, string ContentHtml)`.
- Si falta la versión publicada de cualquiera de los tres: el mismo `404 support.public.not_found` de hoy (fail closed,
  plan F3). Con el bloqueador de §7 eso sólo pasa con un formulario publicado antes de este cambio (§11).

**`POST /tickets`**: `SupportPublicFileRequest` reemplaza `LegalTextVersion` por:

```
SupportPublicFileRequest(
    Guid? TicketTypeId, string? Subject, string? Description, bool Anonymous,
    SupportPublicRequesterRequest? Requester,
    bool AcceptsDataPolicy, int? PrivacyNoticeVersion,
    int? AnonymousNoticeVersion,
    bool AcceptsTerms, int? TermsVersion,
    int? FormVersion, string? TurnstileToken,
    IReadOnlyList<SupportPublicAttachmentRef>? Attachments = null,
    Guid? ClientRequestId = null)      // §9.2
```

| Regla | Con datos | Anónimo | Si falla |
| --- | --- | --- | --- |
| `AcceptsDataPolicy` | `true` | se ignora, como hoy | `422 validation.failed`, `errors.AcceptsDataPolicy` (sin cambio) |
| `PrivacyNoticeVersion` | obligatoria | tiene que ser `null` | `validation.failed`, `errors.PrivacyNoticeVersion` |
| `AnonymousNoticeVersion` | tiene que ser `null` | obligatoria | `validation.failed`, `errors.AnonymousNoticeVersion` |
| `AcceptsTerms` | `true` | `true` | `422 validation.failed`, `errors.AcceptsTerms` (como `AcceptsDataPolicy`) |
| `TermsVersion` | obligatoria | obligatoria | `validation.failed`, `errors.TermsVersion` |
| Versión distinta de la vigente (cualquiera de las que aplican) | | | `422 support.public.legal_texts_outdated` (sin cambio; la página recarga `GET /form`) |

El handler lee las vigentes después de Turnstile y del validador, como hoy (`FilePublicTicket.cs:248`), y escribe las
filas de §5.2 en la transacción del ticket. La IP del visitante sólo llega a las filas del camino con datos. La auditoría
del alta suma `legalAcceptances` a sus `changedFields`. El reenvío del PIN y los adjuntos no cambian.

### 9.1 Datos que el portal no omite [owner, 2026-11-15, D-6]

El portal muestra la fecha de radicación y el término en días hábiles («no omitir datos»):

- `SupportPublicFiledDto` (respuesta de `POST /tickets`) suma `DateTimeOffset ReceivedAt` (`SupportTicket.ReceivedAt`, el
  instante del radicado), `int? TermBusinessDays` y `bool TermIsLegal` (la foto del tipo que guarda el ticket,
  `SupportTicket.TermBusinessDays`, `SupportTicket.cs:41`; `null` = el tipo no tiene término).
- `SupportPublicTicketDto` (consulta) ya trae `ReceivedAt`, `DueOn` y `TermIsLegal`; suma `int? TermBusinessDays`, de la
  misma foto.
- **El NIT del tenant sale de una de sus empresas** (§9.3, D-6 resuelta: opción a). `Tenant` no tiene razón social ni
  documento; el tenant elige en Ajustes › Formulario público cuál de sus empresas (`Modules.Companies`) es la que responde.

### 9.3 La empresa del portal [owner, 2026-11-15, D-6 y D-8]

**Dónde se guarda.** Columna `organization_company_id uuid NULL` en `support.form_versions` (cada versión del formulario
guarda la suya, como el resto de la configuración). Migración `AddSupportFormOrganization`, aditiva, sin relleno: las
versiones existentes quedan en `NULL`.

**Borrar y desactivar la empresa.** Hoy Companies no pregunta a nadie antes de borrar: deja que una clave foránea lo
frene y traduce cualquier violación al borrar una `Company` a `422 companies.company.in_use`
(`CompaniesUnitOfWork.cs:48-54`; precedente `pos.cash_sessions.company_id`, FK real entre esquemas agregada a mano en la
migración, `PosDbContext.cs:48-50`). Soporte hace lo mismo [diseñador]: FK `FK_form_versions_companies_organization_company_id`
a `companies.companies(id)` `ON DELETE RESTRICT`, escrita a mano en la migración (EF no modela relaciones entre contextos).
Companies migra antes que Support al arrancar (`Program.cs:450` y `:510`), así que la FK encuentra la tabla. Consecuencia:
una empresa que alguna versión del formulario eligió ya no se puede borrar, sólo desactivar; borrar es para la empresa
cargada por error que nadie usó (`DeleteCompany.cs:35-36`), así que es coherente. Desactivar no lo frena nadie:

- El portal **sigue mostrando** una empresa desactivada: sus datos siguen siendo ciertos y el owner pidió no omitirlos.
- La consola la marca con el bloqueador `organization_inactive` en `PublishBlockers`; como todo bloqueador, sólo impide
  pasar de sin publicar a publicado (`UpdateFormSettings.cs:141-147`). Un formulario ya publicado se sigue editando.
- Elegir una empresa **nueva** exige que sea del tenant y esté activa; dejar la que ya estaba elegida, aunque se haya
  desactivado, no falla (si no, desactivar una empresa bloquearía cualquier cambio del formulario).

**Obligatoria para publicar** (D-8): sin empresa elegida, el bloqueador `organization_required` impide publicar. Un
formulario publicado antes de este cambio (sólo en desarrollo) sigue sirviéndose con `organization: null`.

**Cómo la lee Soporte.** Soporte no referencia `Modules.Companies.*` (`SupportLayerTests`). Puerto nuevo
`ISupportOrganizationDirectory` en `Modules.Support.Application` con adaptador `SupportOrganizationDirectory` en
Bootstrapper sobre `ICompanyRepository`, calcado de `PosCompanyLookup` (`ListActiveAsync` y `FindAsync`, con
`CompanyDocumentType.ToWire()`).

**Contrato.**

- Público: `GET /form` y `GET /consult` suman `organization: { name, documentTypeCode, documentNumber } | null`
  (`Company.Name`, `Company.DocumentType.ToWire()` — `NIT`, `CC`, `CE`, `PASAPORTE`, `NIF`, `NIE`, `TAX_ID` —,
  `Company.TaxId`). `GET /form` usa la versión publicada; `GET /consult`, la vigente aunque no esté publicada (como
  `accentColor` hoy). `null` = sin empresa elegida. La respuesta de `POST /tickets/lookup` no la repite: la página de
  consulta ya la tiene de `GET /consult`, que carga primero.
- Consola: `SupportFormSettingsDto` suma la empresa elegida y la lista de empresas activas para el selector;
  `PUT /settings/public-form` suma `organizationCompanyId`. Las opciones viajan en la configuración del formulario y no se
  piden a `GET /api/v1/tenants/{tenantId}/companies?status=active`: esa ruta exige `companies.company.read`, que un rol con
  `support.settings.manage` puede no tener (precedente: `RegisterContextResponse.Companies` del POS, «el cajero no tiene
  companies.company.read»).

### 9.2 Radicado idempotente [owner, 2026-11-15, D-7]

`POST /tickets` acepta `Guid? ClientRequestId`, que el navegador genera una vez por intento de radicar (no por clic). Calca
el checkout de la tienda (`SubmitOrderRequest.cs:227-232`, `IStoreOrderGateway.cs:155-242`):

- **Columna** `support.tickets.client_request_id` (uuid, nulo) con índice único parcial
  `IX_tickets_tenant_client_request` sobre `(tenant_id, client_request_id) WHERE client_request_id IS NOT NULL`. Migración
  aditiva. Se guarda para siempre, como `external_key` del pedido de la tienda: no hay retención aparte.
- **Orden**: resolver el tenant y el formulario → si viene `ClientRequestId`, buscar el ticket con esa clave → si existe,
  responder con él **antes** de Turnstile, del validador y de las versiones. Así un reintento después de perder la
  respuesta no choca con el token de Turnstile ya gastado ni con un formulario que cambió entre medio. Sin clave, todo
  sigue como hoy.
- **Cuerpo distinto**: no se compara, igual que la tienda («devuelve el pedido que ya salió … aunque éste nombre otra
  [moneda]»). La clave identifica el intento; lo que el reintento traiga distinto se ignora.
- **Carrera**: dos requests con la misma clave a la vez. El índice rechaza al segundo al confirmar; `SupportUnitOfWork` lo
  reconoce **por nombre** y el handler relee el ganador y responde con él, como `KeyedOrderCommit`. La transacción
  perdedora se deshace entera, número de radicado incluido: no se consume un segundo radicado.
- **Respuesta**: la misma forma (`201 SupportPublicFiledDto`), con los datos del ticket guardado. No se reenvía nada que
  ya salió: el correo de apertura no se manda otra vez. El PIN no se puede repetir (sólo se guarda su hash): ver D-7 en
  §15 para qué trae `Pin` en un reintento.
- `ClientRequestId` vacío (`Guid.Empty`) es `422 validation.failed` con `errors.ClientRequestId`, como en la tienda.

El cuerpo del radicado no crece (sólo números de versión): el tope de 64 KB sigue sobrado. La respuesta de `GET /form`
sí crece (hasta tres documentos de 60 000 caracteres); no tiene tope y sigue `no-store`.

## 10. Errores

Patrones existentes: validador con `validation.failed` y el mapa `errors` por campo; reglas de negocio con
`SupportDomainException` (422); índices únicos traducidos por nombre en `SupportUnitOfWork` (409); `If-Match` con
`RequestConcurrencyException` (412). Ver `ApiExceptionHandler.MapException`.

| Código | Estado | Cuándo |
| --- | --- | --- |
| `validation.failed` | 422 | `contentHtml` vacío o crudo > 120 000; `authorizationQuestion` > 500; `policyUrl` no https; campos de privacidad con otro tipo; versiones del radicado según §9 |
| `support.legal_documents.content_empty` (nuevo) | 422 | después de sanear no queda texto visible |
| `support.legal_documents.content_too_long` (nuevo) | 422 | después de sanear pasa de 60 000 |
| `support.legal_documents.kind_not_found` (nuevo) | 404 | `{kind}` desconocido en la ruta |
| `support.legal_documents.version_not_found` (nuevo) | 404 | `GET …/versions/{version}` que no existe |
| `support.legal_documents.version_taken` (nuevo) | 409 | `IX_legal_document_versions_tenant_kind_version`, dos publicaciones a la vez |
| `concurrency.conflict` | 412 | `If-Match` distinto de la vigente, como `PublishLegalTexts` hoy |
| `support.legal_acceptance.not_found` (nuevo) | 404 | snapshot de un tipo que el ticket no tiene |
| `support.public.terms_required` (nuevo, interno) | — | `ErrorCode` de la regla del validador, como `consent_required`; por HTTP sale `422 validation.failed` con `errors.AcceptsTerms` (`ApiExceptionHandler` no publica el `ErrorCode`) |
| `support.public.legal_acceptance_duplicate` (nuevo) | 409 | `IX_legal_acceptances_ticket_kind`; inalcanzable hoy, como `consent_duplicate` |
| `support.public.legal_texts_outdated`, `support.form.legal_texts_required` | 422 | se conservan con el significado de §7 y §9. `support.public.consent_required` sigue siendo sólo el `ErrorCode` interno de su regla |

`support.legal_texts.policy_required` y `support.legal_texts.version_taken` quedan sin ruta que los produzca; se
retiran de `SupportErrorCodes` junto con las rutas viejas.

## 11. Migración y relleno

Migración `AddSupportLegalDocuments`, **aditiva** (regla expand/contract de `CLAUDE.md`): dos tablas nuevas y un
`INSERT … SELECT`; nada se altera ni se borra. El `Down` borra sólo los índices y deja las dos tablas (insert-only).

**Relleno** [diseñador], en la misma migración, por cada tenant con `legal_text_versions`, desde su versión vigente:

- `PrivacyNotice` versión 1: `authorization_text` y, si hay, `policy_text`, cada uno escapado (sólo `&`, `<`, `>`: es lo que escapa el serializador, y `&quot;` no sería canónico) y
  partido en `<p>` por líneas en blanco, con `<br>` en los saltos simples; `policy_url` se copia;
  `authorization_question` `NULL` (rige la por defecto); `created_at` y `created_by` de la versión de origen.
- `Terms` versión 1: sólo si `terms_text` no es `NULL`, con la misma conversión.
- `AnonymousNotice`: **no** se rellena. No hay texto del tenant del que salga, e inventarlo sería publicar un documento
  legal a su nombre.

La salida del relleno tiene que ser canónica para el saneador: una prueba fija que `sanear(relleno(x)) == relleno(x)`
para textos con `&`, `<`, comillas, saltos y líneas en blanco. Si el SQL no logra la forma canónica, el relleno se hace
en C# con el puerto, dentro de las inicializaciones de Support bajo el candado de arranque.

**Consentimientos viejos** (`support.consents`): no se copian. Producción no tiene ninguno (§3); en desarrollo quedan
legibles por SQL y el detalle del ticket no los muestra. Esto vale sólo si este cambio llega a `main` **junto con o antes
que** la fase 3 (§14). Durante el rollout, un pod viejo que publique `legal-texts` después de la migración no se copia:
no hay consola que lo llame.

**Formularios ya publicados en desarrollo** sin `AnonymousNotice` (o sin `Terms`) responden `404` en `GET /form` hasta
que el tenant publique lo que falta; la consola lo muestra en `PublishBlockers`.

**Radicado idempotente** (§9.2): migración aparte, `AddSupportTicketClientRequestId`, también aditiva: columna nula
`support.tickets.client_request_id` e índice único parcial `IX_tickets_tenant_client_request`. Sin relleno: los
tickets existentes quedan con `NULL` y el índice parcial no los mira. Un pod viejo durante el rollout ignora la columna.

## 12. CORS, limitadores, captcha y auditoría

- **CORS.** Sin cambio de mecanismo: política `support-public` (`CorsServiceCollectionExtensions.cs:57-63`), `GET` y
  `POST`, sólo `Content-Type`, **sin credenciales**, orígenes exactos de `Support:PublicForm:AllowedOrigins` validados
  por `SupportPublicFormOptionsValidator` (https exacto; `http://localhost[:puerto]` sólo en Development; obligatoria y
  sin tokens `#{…}#` en Production). Producción: `https://soporte.lulocrm.com` (`k8s/prod-configMap.yaml:55`), que
  sirve ahora `qep-frontend-support`. Desarrollo: `appsettings.Development.json` tiene `http://localhost:3002`, el
  puerto de la consola, puesto cuando el portal iba a ser el mismo SPA; se reemplaza por el puerto fijo del dev server de
  `qep-frontend-support` cuando ese repo lo fije. Previews de Vercel (D-5): producción sólo admite
  `https://soporte.lulocrm.com`; el backend de desarrollo admite además el alias estable de la rama `develop` del
  proyecto de Vercel del portal (un origen exacto, por configuración), que también entra en el CORS del bucket de
  desarrollo. Los previews por PR prueban contra un backend local.
- **El portal llama desde el navegador.** Si alguna página de Astro llamara al grupo en el render del servidor, la
  cubeta de los limitadores sería la IP de Vercel para todos los visitantes. No se hace (corrección de §9.1 del spec de
  fase 3); un render del servidor necesitaría el patrón de secreto del storefront, que queda fuera.
- **Limitadores.** Sin cambios: `SupportPublicRead` 60/min, `SupportPublicSubmit` 5 cada 10 min, `SupportPublicLookup`
  10/min, `SupportPublicUpload` 20 cada 10 min, por IP (`Program.cs:225-265`). Este cambio no agrega rutas públicas.
- **Turnstile.** Sin cambios: `Support:PublicForm:Hostname` = `soporte.lulocrm.com`, exigido sólo en Production.
- **Auditoría.** `support.legal_document.published`, recurso `legal_document`, id del recurso = id de la versión,
  `changedFields` sólo con nombres (`kind`, `contentHtml`, `authorizationQuestion`, `policyUrl`), nunca el contenido, en
  la misma transacción que la versión. `support.legal_texts.published` queda sin escritor. Leer una foto de aceptación no
  se audita (no es una descarga de archivo; el detalle ya muestra los datos del solicitante a quien la puede leer).

## 13. Pruebas (TDD)

RED antes que GREEN, con la salida literal de ambos; presupuesto del workspace (integración filtrada por nombre mientras
se itera, la clase completa una vez por bloque, nunca dos corridas de integración a la vez).

**Unitarias (`Modules.Support.UnitTests`)**

- `SupportHtmlSanitizerTests` (contra la implementación real del puerto): conserva cada elemento de la lista blanca;
  quita `script`/`style`/`iframe`/`svg`/`img` con su contenido; desenvuelve `span`/`div`; quita `on*`, `style`, `class`;
  rechaza `javascript:`, `data:`, `vbscript:`, `http:` y URL relativas en `href`, también ofuscadas
  (`java&#x09;script:`, mayúsculas, espacios y controles al inicio); agrega `rel` y `target` a los enlaces; mXSS
  (`<noscript><p title="</noscript><img src=x onerror=alert(1)>">`, `<svg><style><img src=x onerror=…>`); es idempotente
  sobre cada salida; y la salida del relleno de §11 es canónica.
- `SupportLegalDocumentVersionTests`: versión ≥ 1; contenido con texto visible y ≤ 60 000; pregunta y URL sólo en
  `PrivacyNotice`; URL https.
- `SupportLegalAcceptanceTests`: no acepta el ejemplo ni una versión de otro tenant u otro tipo; `PrivacyNotice` exige
  IP; `AnonymousNotice` la rechaza; guarda la foto que se le pasa.
- `PublishLegalDocumentValidatorTests` y `FilePublicTicketValidatorTests` (amplía la existente): cada fila de la tabla de
  §9, con el campo del mapa `errors` (PascalCase, el `PropertyName`); `TheTermsMustBeAccepted` con `errors.AcceptsTerms`.
- `SupportFormSettingsTests`: un bloqueador por documento faltante, todos con `support.form.legal_texts_required`.

**Integración (`Modules.Support.IntegrationTests`)**

- `PublishingCreatesTheNextVersionPerKind` (versionar `Terms` no mueve `PrivacyNotice`); `AStaleIfMatchIs412`;
  `TheStoredContentIsTheSanitizedOne` (publicar `<script>` y leer la columna); `WithoutVersionsTheConsoleGetsExamples`;
  `TheHistoryListsVersionsNewestFirst`.
- `TheFormServesTheThreeCurrentDocumentsAndTheCharacterizationNotice`; `AFormMissingADocumentIs404`.
- `ANamedTicketStoresPrivacyAndTermsWithTheVisitorIp`; `AnAnonymousTicketStoresTheNoticeAndTermsWithoutIp`
  (Review Focus: ninguna fila de un anónimo con IP); `EachOutdatedVersionIs422` (las tres);
  `TheAcceptanceKeepsTheTextTheRequesterSaw` (radicar, publicar una versión nueva, leer la foto).
- `TheTicketDetailListsTheAcceptances` y `TheSnapshotIsOnlyForTheTicketsTenant`.
- §9.1: `TheFilingResponseCarriesReceivedAtAndTheTerm` (con término y sin término); `TheConsultCarriesTheTermBusinessDays`.
- §9.2: `ARetryWithTheSameClientRequestIdReturnsTheSameTicket` (un solo ticket, el número no avanza, un solo correo de
  apertura); `ARetrySkipsTurnstileAndTheVersionChecks` (token gastado y formulario cambiado: igual `201`);
  `ARetryWithADifferentBodyStillReturnsTheFirstTicket`; `TwoConcurrentFilingsWithTheSameKeyCreateOneTicket` (dos
  requests en paralelo: los dos `201` con el mismo número, una fila); `TheSameKeyInAnotherTenantIsAnotherTicket`;
  `AnEmptyClientRequestIdIs422`; `WithoutAKeyEveryFilingIsNew`; y las de D-7 sobre el PIN del reintento.
- `TheBackfillCopiesTheCurrentLegacyTexts`: migrar hasta la migración anterior, sembrar `legal_text_versions` (con y sin
  `terms_text`), migrar a `AddSupportLegalDocuments` y leer las filas nuevas.

**Bootstrapper y arquitectura**

- `SupportUserReferenceProbe`: un miembro que publicó una versión no se puede borrar.
- `SupportLayerTests`: Application no referencia la librería del saneador.
- `SupportPublicSurfaceApiTests` no cambia (CORS); se corre una vez porque `GET /form` cambia de forma.

## 14. Orden de salida

1. **Backend** (esta rama → `develop`). La migración es aditiva; corre antes que cualquier frontend.
2. **Este cambio llega a `main` en el mismo paso que la fase 3, o antes de que la fase 3 llegue sola.** Si la fase 3
   saliera a producción antes, habría consentimientos de la forma vieja que el detalle no muestra, y habría que decidir
   cómo exponerlos.
3. **Consola (`qep-frontend`)**: editor de los tres documentos, bloqueadores del formulario y aceptaciones en el detalle.
4. **Portal (`qep-frontend-support`)**: los cuatro pasos contra el contrato de §9.
5. Configuración: origen de desarrollo del portal en `appsettings.Development.json` (§12) y el alias estable de
   `develop` del portal en el backend de desarrollo y en el CORS de su bucket (D-5).
6. Antes de producción: el abogado revisa los textos de ejemplo (D-4) y la suficiencia del aviso sin enlace (D-2).

`docs/estado.md` se actualiza al mergear a `develop`, no en esta rama.

## 15. Decisiones del owner (resueltas 2026-11-15)

El owner aprobó D-1 a D-5 tal como se recomendaron, y agregó D-6 y D-7. El cuerpo del spec ya las aplica. D-8 es nueva
(de este spec) y el plan la asume como se recomienda.

- **D-1 Pregunta de autorización: editable, con una por defecto.** Campo de texto plano opcional en el aviso de
  privacidad (`authorizationQuestion`, 1–500). Sin él, el backend arma la pregunta por defecto con el nombre del tenant
  (§6.4). La pregunta que se mostró, escrita o por defecto, va en la foto de la aceptación.
- **D-2 Enlace a la política de tratamiento: opcional.** `policyUrl` (https) es opcional en el aviso de privacidad y el
  portal muestra el enlace del pie sólo si existe. La política escrita va en el cuerpo del aviso. El abogado confirma
  antes de producción que el cuerpo del aviso cumple lo que pide el Decreto 1377 de 2013 sobre cómo conocer la política.
- **D-3 Librería de saneamiento: `HtmlSanitizer`** (mganss, sobre AngleSharp), versión fija con el lock file regenerado,
  referenciada sólo desde `Modules.Support.Infrastructure`. No se escribe un saneador propio.
- **D-4 Textos de ejemplo: los tres tipos tienen ejemplo.** Cortos, marcados como ejemplo y revisados por el abogado
  antes de salir a producción. Nunca llegan al portal: no se pueden aceptar y el formulario no se publica sin versiones
  propias.
- **D-5 Orígenes de previews: alias estable en desarrollo.** Producción sólo `https://soporte.lulocrm.com`. El backend
  de desarrollo admite además el alias estable de la rama `develop` del proyecto de Vercel del portal (un origen exacto,
  por configuración), que también entra en el CORS del bucket de desarrollo. Los previews por PR prueban contra un
  backend local.

**Agregadas el 2026-11-15 en la revisión del spec del portal:**

- **D-6 «No omitir datos»: el portal muestra el NIT del tenant, la fecha de radicación y el término en días hábiles**
  [owner]. Fecha y término: §9.1. **NIT: resuelto el 2026-11-15 con la opción (a)** [owner]: el tenant no tiene NIT
  propio (`Tenant` sólo tiene `DisplayName` y `Country`), así que elige en Ajustes › Formulario público una de sus
  empresas de `Modules.Companies`, y el portal muestra su nombre, tipo y número de documento (§9.3). Se descartó (b),
  agregar razón social y NIT al tenant en Tenancy, porque duplicaba un dato que ya existe.
- **D-7 Radicado idempotente por `clientRequestId`** [owner], calcado del checkout de la tienda (§9.2). **Qué trae `Pin`
  en un reintento** lo fija este spec dentro de lo que dijo el owner («sin reenviar el PIN si ya se entregó»), y conviene
  que lo confirme: sólo se guarda el hash del PIN, así que no se puede devolver el mismo. Regla: si el correo de apertura
  ya salió (`ticket_access.last_sent_channel = 'Email'` sin `last_failure_code`), el reintento trae `Pin: null` y
  `PinEmailed: true`: el solicitante lo tiene en el correo y puede pedir «Reenviar PIN». Si no salió por ningún canal
  (anónimo, o el correo falló), se **emite un PIN nuevo** que reemplaza al anterior y viaja sólo en la respuesta, sin
  correo: sin eso, un anónimo que perdió la primera respuesta perdería su caso. La clave sólo la conoce el navegador que
  radicó (UUID aleatorio que nunca se muestra), así que rotar el PIN con ella no abre nada a un tercero.

**Nueva, de este spec (2026-11-15), asumida por el plan hasta que el owner responda:**

- **D-8 ¿La empresa del portal es obligatoria para publicar?** **Recomendación: sí** (bloqueador `organization_required`,
  §9.3). El owner pidió no omitir el NIT; un formulario publicado sin empresa lo omitiría. Si el owner prefiere que sea
  opcional, el bloqueador se quita y `organization` llega `null` hasta que la elijan; nada más cambia.

## 16. Discrepancias encontradas (spec contra código)

1. Spec de fase 3 §9.1: «es el mismo SPA». Corregido en el mismo commit [owner].
2. Spec de fase 3 §9.5: «su política (texto o URL)» obligatoria y términos opcionales. Este spec la reemplaza: términos
   obligatorios y enlace a la política opcional (D-2).
3. `appsettings.Development.json` permite `http://localhost:3002` (la consola) en el grupo público: era consecuencia de
   §9.1 y deja de tener sentido (§12).

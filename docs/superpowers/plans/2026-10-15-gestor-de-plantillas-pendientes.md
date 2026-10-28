# Gestor de plantillas de WhatsApp (backend) — pendientes

**Fecha:** 2026-10-15
**Rama:** `feature/gestor-de-plantillas` (sin mergear a `develop`; el merge lo decide el owner).
**Contexto:** son los hallazgos menores de las revisiones por task y de la revisión final de la rama del
gestor de plantillas (spec `docs/superpowers/specs/2026-10-14-gestor-de-plantillas-whatsapp-design.md`,
plan `docs/superpowers/plans/2026-10-14-gestor-de-plantillas-backend.md`). Ninguno bloqueó el merge según el
revisor final. Las líneas citadas son las del commit de este archivo; verifícalas antes de tocar nada.

Reglas del repo que aplican: TDD con RED y GREEN literales, commits convencionales en español **sin atribución de
IA** (revisa el mensaje de cada commit, también los de subagentes), textos al usuario en español colombiano con
tuteo.

## 1. Comportamiento visible (recomendado antes del merge)

- [ ] **`buttons: [null]` da 500 en vez de 422.** `TemplateManagementEndpoints.cs:70` hace `button.Type` sobre un
  elemento nulo, y el validador de `CreateTemplateCommand` también lo desreferencia. Arreglo: el mapeo descarta o
  marca el nulo, y el validador da `validation.failed` en `buttons[i]`. Prueba de integración con `[null]`.
- [ ] **`limit_reached` es demasiado amplio.** `TemplateGraphErrors.cs:61` clasifica como tope de plantillas
  cualquier `error_user_title` que diga «limit». Un error de límite de caracteres o de ritmo manda al admin a
  borrar plantillas. Arreglo: exigir que hable de plantillas (p. ej. «template» + «limit», o el texto real del tope
  si se confirma con tráfico). Pruebas en `TemplateGraphErrorsTests` con un título de rate limit y uno de longitud.
- [ ] **`POST …/template-management/quotation` puede devolver `name_taken`.** Pasa tras agotar
  `MaxCreateAttempts` (`QuotationTemplateProvisioning.cs:199`, `:308`), y el frontend pinta `name_taken` en un
  campo `name` que esa acción no tiene. Arreglo: al agotarse, otro código (o `rejected_by_meta` con un mensaje que
  explique que los nombres `qep_cotizacion_*` están ocupados). Si es un código nuevo, va a `MessagingErrorCodes` y
  al spec §4.8.
- [ ] **La categoría que se fija al lanzar una campaña ya no está «viva».** `Campaigns.cs:415` llama
  `campaign.UseLiveTemplateCategory(template.Category)` con la categoría del espejo, que sólo se actualiza con el
  refresco (antes, con el caché de 5 minutos, estaba casi al día). Opciones: refrescar el espejo de esa conexión al
  lanzar (`refresh: true` en ese único camino), o aceptarlo y renombrar el método. Decide antes de tocar.
- [ ] **La adopción sólo reconoce el nombre base.** `QuotationTemplateProvisioning.cs:219` busca únicamente
  `qep_cotizacion`. Una `qep_cotizacion_vN` viva que perdió su fila (espejo atrasado, versión huérfana) no se
  readopta: se crea vN+1 y la otra queda huérfana en Meta. Arreglo: adoptar la mayor versión reservada en `es`,
  `External` y viva, que haga `QuotationTemplateDefinition.Matches`. Prueba de integración.
- [ ] **El `<summary>` de `TemplateGraphErrors` quedó sobre el enum.** `TemplateGraphErrors.cs:17-23`: el enum
  `TemplateGraphOperation` se metió entre el doc de la clase y `public static class TemplateGraphErrors`. Mueve el
  enum (con su propio `<summary>`) arriba del doc de la clase o debajo de la clase.

Conocido y documentado en el README, sin arreglo pendiente: las plantillas creadas en la consola de Meta no
entran por el webhook (sólo actualiza filas existentes); entran con *Actualizar* (`refresh=true`) o con
`POST …/sync`.

## 2. Calidad del código (no cambia el comportamiento)

- [ ] La URL de `Location` se arma dos veces: `TemplateManagementEndpoints.cs:66` y `:77`. Un helper privado.
- [ ] La conexión se resuelve dos veces por lectura de la pantalla (`TemplateManagementReader` más el handler).
- [ ] El `<summary>` de `TemplateCatalog` todavía dice que sólo se queda con las `APPROVED`
  (`MessageTemplates.cs:112`), y `FromStored` ya no filtra.
- [ ] `ManagedTemplateDto.Kind` no lista sus valores en el doc (`Free`, `Quotation`, `External`); al frontend le
  sirve.
- [ ] El regex de `MessageTemplateName` repite `{1,512}` en vez de usar `MaxLength` (`MessageTemplate.cs`).
- [ ] `MessagingUnitOfWork.cs:28`: la carrera del espejo sale con el mensaje «The conversation changed…».
- [ ] `WebhookDeliveryProcessor.cs:80`: un `message_template_status_update` sin id o sin `event` se registra como
  «field is not handled»; debería decir que faltaba un dato.
- [ ] `?? string.Empty` sobre `category`/`parameterFormat`, que no admiten nulos, en `MessageTemplate`.
- [ ] `TemplateStatusChange.Name` y `.Language` se parsean y nadie los usa (el spec los pide; decide si se quedan).
- [ ] `AdoptAsQuotation` reusa `quotation_conflict` para «no es `External`»; el handler nunca llega ahí con otro
  `kind`.

## 3. Pruebas que faltan

- [ ] El recorte al ancho de columna en la creación de `MessageTemplate` y en `SyncFrom` (`Name`, `Language`,
  `MetaTemplateId`, `Category`, `ParameterFormat`).
- [ ] El catch de `RequestConcurrencyException` en el llenado implícito del `GET` de la pantalla, y el
  `DiscardMirrorChanges()` de la rama de `DbUpdateConcurrencyException` (`MessagingUnitOfWork.cs:26`).
- [ ] Que el descarte del espejo no se lleve entidades que no son del espejo (p. ej. una campaña pendiente en el
  mismo scope sigue guardándose).
- [ ] Borrar: una falla de Meta (401) deja la fila; una conexión ajena da 422 `connection_unavailable`.
- [ ] Parser del webhook con un `event` desconocido (hoy sólo lo cubre la integración).
- [ ] La revalidación propia de `ProvisionQuotationTemplateHandler` (tenant ajeno o sin `TemplateManage`) en una
  unitaria; hoy sólo la política está probada.
- [ ] Crear: la integración no verifica `body_text_named_params` en lo que recibe Meta (sí la unitaria).
- [ ] Validador: nombre de más de 512, `buttons` nulo, un ejemplo de más de 1024.
- [ ] `AnImplicitFirstFillThatLosesTheRaceKeepsReadingTheMirror` promete más de lo que prueba (el fake conserva lo
  agregado en memoria); la carrera real sólo la prueba la integración en Postgres.

## 4. Riesgos aceptados (raros; anotados para no redescubrirlos)

- Una sincronización que corre mientras se borra una plantilla la puede reinsertar como `External` hasta el
  siguiente refresco (el listado de Meta es anterior al borrado).
- `AlreadyGone` al borrar depende de que Meta diga «does not exist» en inglés (`WhatsAppCloudClient.cs:491`).
- `DiscardMirrorChanges` suelta **todas** las `MessageTemplate` del contexto: un handler que tuviera una con tracking
  y disparara un llenado implícito la perdería. Ninguno lo hace hoy.
- Un listado incompleto marca igual la sincronización como hecha (no borra nada, pero no se reintenta solo).
- Sin single-flight en el primer llenado: con Meta caído, cada destinatario de una campaña en una conexión nunca
  sincronizada espera su propio timeout (una vez por conexión).

## 5. Decisión pendiente del owner

- [ ] **Qué errores de Meta pausan la conexión.** Hoy, por el spec §4.8, «permiso faltante» (401, 403, 190, 10 y
  200–299) al listar, crear o borrar llama `ReportRejectedAsync` y deja la conexión en `NeedsAttention`. El revisor
  final sugiere que sólo 190 y 401 la pausen: antes de esta rama una falla del listado nunca pausaba nada, y un 10 o
  200–299 al listar desde la bandeja ahora tumba bandeja y campañas. La subida del PDF ya no pausa nunca.

## 6. Fuera de este repo

- [ ] `qep-frontend`, `src/features/messaging/hooks/use-message-templates.ts` (rama `develop`): el comentario dice
  que el servidor guarda los mismos 5 minutos; ya no es así (el catálogo lee del espejo). Es del otro repo y de su
  developer.
- [ ] Con el primer tráfico real (handoff de Meta, «a confirmar en producción»): la subida del PDF con el token de
  negocio, el formato `?sig=` del id, el cuerpo exacto del error de un nombre en cuarentena y `rejected_reason` en
  `fields`.

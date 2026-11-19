---
paths:
  - "src/Modules/Support/**"
  - "tests/Modules/Support/**"
  - "src/Bootstrapper/**/Support*.cs"
---

# Soporte: formulario público, adjuntos y documentos legales

Reglas de `qep-backend` que no se deducen leyendo el código. Se cargan solas al leer o editar un
archivo que coincide con `paths` (arriba). Las reglas generales están en [`CLAUDE.md`](../../CLAUDE.md).

- **El formulario público de Soporte (`/api/v1/public/support/{tenantSlug}`) no se prueba con el stub de auth** (fase 3). En modo stub
  `RequireCsrfHeaderMiddleware` ni se registra y la cookie no se valida: una prueba con stub pasa aunque la exención por prefijo
  (`SupportPublicPaths`, duplicado de `PublicSupportEndpoints.GroupPattern` y atado por `SupportPublicPathsTests`) esté rota o un handler
  lea la sesión. Lo que toca CSRF, cookie o CORS va en `SupportPublicRealAuthHost` (precedente `ShopifyApiHarness.WithRealAuthentication`;
  lo amarra `ThePublicGroupIgnoresTheSessionCookieWithRealAuth`). Ningún handler del grupo lee `IExecutionContext` ni la cookie: el
  tenant sale del slug (`SupportPublicResolver`), nunca de la sesión de quien navega.
- **`support.legal_document_versions` y `support.legal_acceptances` son prueba legal: sólo se insertan** (spec 2026-11-15 §5). Ningún
  método de dominio las edita ni las borra, `legal_acceptances` no tiene FK (ni al ticket ni a la versión: guarda su foto del HTML
  servido, que es lo que prueba) y el `Down` de `AddSupportLegalDocuments` no las borra. **Ese `Down` no se corre en producción**: el
  rollback es revertir el código. Una purga futura de tickets tiene que dejar las aceptaciones fuera, y una migración nueva de Support que
  las toque tampoco puede borrarlas en su `Down`. Lo mismo vale para `support.consents` y `support.legal_text_versions` de la fase 3, que
  conservan sus filas.
- **Todo HTML de un documento legal pasa por `ISupportHtmlSanitizer`**: al publicar, al servir `GET /form` y en la foto. Un camino nuevo
  que escriba `content_html` sin el puerto abre XSS en el portal público. `legal_text_versions` y `consents` siguen mapeadas sin
  escritor: quitarlas del modelo hace que EF las borre en la siguiente migración.
- **El PIN de consulta nunca sale a logs, auditoría, eventos ni trazas: sólo se guarda su hash** (`ticket_access.pin_hash`, PBKDF2).
  Viaja dos veces y nada más: en la respuesta del radicado y en el envío al destino registrado del ticket. Un log nuevo en el formulario
  público lleva el id del ticket y el tipo de la excepción, nunca el PIN, el radicado ni el destinatario; lo amarra
  `ThePinNeverReachesLogsAuditOrEvents`, que corre el host en `Trace`. **El contador de fallos vive en `ticket_access`, no en el ticket**:
  si un intento fallido tocara el ticket, le subiría la versión y le daría un 412 al asesor que lo tiene abierto. Cinco fallos bloquean
  ese radicado 15 minutos, y mirar el bloqueo y anotar el intento van con la fila tomada (`LockByNumberAsync`, `SELECT … FOR UPDATE`):
  sin el candado, intentos a la vez leen el mismo contador, se pisan y el quinto fallo no bloquea (`ConcurrentWrongPinsStillLockTheTicket`). Un camino
  nuevo que lea o escriba la clave (otra forma de consulta, un reenvío por otro canal) toma la misma fila y nunca desbloquea.
- **El reintento del radicado público se reconoce por `clientRequestId` antes de Turnstile y bajo el candado del contador** (spec
  2026-11-15 §9.2). No muevas la búsqueda después de Turnstile: el token ya se gastó en el primer intento y el reintento fallaría con
  `captcha_invalid`. Un reintento no reenvía el correo; si el PIN no salió por ningún canal, emite uno nuevo y el viejo deja de servir.
  Con el correo de apertura todavía en vuelo (destino `Email` y nada anotado en `ticket_access`) no rota: invalidaría el PIN que va en el
  correo. Ese «en vuelo» vence a los 10 minutos de `pin_issued_at` (`OpeningDeliveryGrace`, plan L33): después rota, porque el proceso
  murió entre confirmar y anotar. La clave es UUID v4 (D-10): otra versión es `422`. La búsqueda va siempre filtrada por tenant
  (`FindByClientRequestAsync`), y la clave no se audita ni va a `ticket_events`.
- **`support.form_versions.organization_company_id` tiene FK real a `companies.companies`** (`ON DELETE RESTRICT`, a mano en la
  migración `AddSupportFormOrganization`, como `pos.cash_sessions`): una empresa que alguna versión del formulario público eligió no se
  borra, sólo se desactiva (`422 companies.company.in_use`). Soporte la lee por `ISupportOrganizationDirectory` (Bootstrapper), nunca
  referenciando Companies (`SupportDoesNotReferenceCompanies`). Es obligatoria para publicar (D-8): una siembra de prueba que publica un
  formulario por la consola tiene que elegir una empresa activa del tenant (`SupportPublicHarness.SeedCompanyAsync`).
- **`uploadKey` es el único vínculo entre la subida anónima de un adjunto y quien la abrió** (plan F32). Es un `Guid.NewGuid()` (v4) que
  Soporte genera, devuelve una vez y Storage guarda como `OwnerId` del archivo `SupportTicket`; completar y radicar exigen el par
  `(fileId, uploadKey)`. No lo cambies por un v7 (`Guid.CreateVersion7`, el de los ids de Storage): se adivina por la hora y cualquiera
  reclamaría la subida de otro. Tampoco lo pongas en una URL ni en un log. La subida sin miembro (`IStorageSystemUploads`) no pasa por
  `StorageAuthorization` y no tiene endpoint en Storage: la usa sólo `SupportAttachmentStorage` (Bootstrapper). El
  `CompleteUploadHandler` de la consola sigue exigiendo `storage.file.upload` y comparte con ella sólo el núcleo (`UploadCompletion`);
  un cambio en el núcleo vale para los dos caminos, y la autorización se queda en el handler
  (`TheMemberCompletionStillRequiresThePermission`).
- **Los archivos `SupportTicket` no se ven desde la consola de Storage** (fix round 1 del Bloque D). `storage.file.*` no es permiso
  de Soporte: sin la pared, quien tiene `storage.file.read` vería las PQRSF y su `uploadKey` (el `OwnerId`), con `publish` las sacaría al
  bucket público y con `delete` borraría un adjunto reclamado. La regla vive en un solo lugar, `SystemOwnedFiles` (al lado de
  `FileOwnerType`): `ListFilesHandler` los excluye, cada handler que carga por id llama `SystemOwnedFileGuard.EnsureVisibleToMembers`
  (404 `storage.file.not_found`, el mismo de otro tenant), `CreateUploadSessionHandler` responde `422 storage.file.owner_type_invalid`, y
  los adaptadores que leen el repositorio de archivos para otro módulo (`ProductImageLookup`, `QuotationFileLookup`) los tratan como
  inexistentes. **Un handler nuevo de Storage que cargue un archivo por id, o un adaptador nuevo sobre `IFileResourceRepository`, aplica
  la misma regla**; un dueño de sistema nuevo se suma al conjunto y no a cada handler. Lo amarran
  `ASystemOwnedFileIsNotFoundWithoutAnyEffect` y `SystemOwnedFilesApiTests`.
- **El `PUT` de un adjunto del formulario público sale del navegador al bucket privado de R2**, así que ese bucket necesita CORS para
  el origen de la página (`https://soporte.lulocrm.com`, y `http://localhost:3002` para probar en local) con `PUT` y los headers
  `Content-Type` e `If-None-Match`. Sin eso el navegador corta el `PUT`, completar responde `428 support.public.attachment_not_uploaded`
  y en el backend no hay ningún error: `Support:PublicForm:AllowedOrigins` es el CORS de la API, no el del bucket.
- **Un adjunto del formulario reclamado sigue `Available` para siempre y nada en Storage lo marca**: lo retiene la sonda
  `SupportAttachmentReferenceProbe` (Support.Infrastructure). Por eso el barrido de staging sólo pregunta por los adjuntos `SupportTicket`
  de la última semana pasada la retención (`StagingCleanupProcessor.SupportAttachmentLookback`): sin el tope, cada tick le preguntaría a
  las sondas por todos los adjuntos de la historia. Un camino nuevo que vincule un archivo `SupportTicket` a algo que no sea
  `support.ticket_attachments` necesita su propia sonda, o el barrido lo borra a las 24 h. **Esa purga guarda la fila antes de borrar
  los objetos**, al revés que la de comprobantes: un guardado fallido no deja una fila `Available` sin objeto que un radicado podría
  reclamar; un borrado fallido deja un objeto huérfano y un log (`AFailedObjectDeleteLeavesTheRowPurgedNotAvailable`). No lo «alinees»
  con el orden de los comprobantes.
- **El tope de 200 subidas anónimas por tenant es blando a propósito** (`OpenPublicAttachmentHandler`, fix round 1 del Bloque F): cuenta
  y después abre, así que entran unas pocas de más bajo ráfaga simultánea. No le pongas un candado por tenant: en un endpoint anónimo,
  quien espera el candado retiene una conexión del pool y quien lo tiene necesita otra para Storage, así que unas pocas IPs vaciarían el
  pool de toda la API. `lock_timeout` tampoco sirve (sigue reteniendo conexiones) y un try-lock rechazaría las 3 subidas en paralelo de una
  sola persona.
- **Un ticket anónimo no tiene cliente ni ninguna columna `requester_*`, y la base lo exige** (`CK_tickets_anonymous`). Una columna nueva
  de la foto del solicitante entra en `SupportDbContext.RequesterColumns` y en una migración que rehaga el CHECK, o el anónimo puede
  llevarla sin error.
- **El formulario público no acepta Word ni Excel mientras `Storage:ClamAv:Enabled` esté apagado en producción** (owner 2026-11-15): un
  adjunto anónimo con macros lo abre un asesor. Sumar un tipo de Office exige prender ClamAV primero. La lista vive en
  `SupportPublicAttachmentRules` (PDF, JPEG, PNG, WebP; `image/jpg` se toma como `image/jpeg`) y viaja a la página en
  `GET /form` → `attachments`; `SupportAttachmentParityTests` sólo exige que sea un subconjunto de la de Storage.
- **`allow_attachments` tiene default `true` en la base y en el modelo, con `HasSentinel(true)`** (owner 2026-11-15). Sin el sentinel, EF
  omite un `false` del `INSERT` (es el valor CLR por defecto) y la base lo guarda en `true`: el tenant apaga los adjuntos y siguen
  prendidos. Precedente: `stores.show_prices`. Lo amarra el guardado `true` → `false` de `SupportFormSettingsApiTests`.

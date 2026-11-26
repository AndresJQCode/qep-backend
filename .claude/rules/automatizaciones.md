---
paths:
  - "src/Modules/Automations/**"
  - "tests/Modules/Automations/**"
  - "src/Bootstrapper/**/Automation*.cs"
  - "src/Modules/Messaging/**/*Automation*"
---

# Automatizaciones (motor de chatbots)

Reglas de `qep-backend` que no se deducen leyendo el código. Se cargan solas al leer o editar un
archivo que coincide con `paths` (arriba). Las reglas generales están en [`CLAUDE.md`](../../CLAUDE.md).

- **El `clientId` de un mensaje del bot no depende del `attempts` de la fila.** Es `DeterministicClientId.For(runId,
baseStepNumber, effectIndex)` y el id de una corrida nueva sale del id del mensaje del inbox (`RunIdFor`), así un reintento de la
  fila —o un `Start` que se deshizo— reusa corrida y `clientId`s y Mensajería, idempotente por `clientId`, no duplica (P2-R5). El
  plan escribía `(runId, nodeId, índice, attempts)`: con `attempts` el reintento SÍ reenvía. El motor sube `StepNumber` en cada bloque
  que entra, también al reenviar uno que espera, para que dos unidades confirmadas nunca compartan base.
- **El cliente HTTP del bloque se registra en Bootstrapper, no en `AddAutomationsInfrastructure`**, porque su handler anti-SSRF
  (`ExternalApiUrlPolicy.CreateHandler`) es de `Integrations.Infrastructure`. Sin redirecciones, sin proxy, `RemoveAllLoggers()`: el
  log de `HttpClient` escribiría la URL con la query del cliente. Cambiar el handler sin pasar por ahí deja a cualquier admin con un
  proxy hacia la red interna; `CompositionRootTests` amarra que el handler sea el de la política.
- **Una llamada HTTP del bloque no se puede deshacer, y la unidad sí.** Si el proceso muere entre la llamada y el commit, el
  reintento vuelve a llamar (P2-R37; la API de Clasic no tiene llave de idempotencia). Si `http_calls` no se puede escribir, se loguea
  y se sigue: lanzar deshace la unidad y repite la llamada.
- **Nada de lo que responde una API externa, sus headers ni la clave sale a logs, auditoría ni `http_calls`** (D-H5, D-H6). La
  prueba `TheKeyNeverAppearsInLogsAuditOrHttpCalls` corre el host en `Trace`, porque el log de headers de `HttpClient` es `Trace`:
  a `Information` quitar `RemoveAllLoggers()` pasaría sin verse. `authHeader` no puede ser un header reservado (`Host`, `Content-*`,
  `Proxy-*`…, P2-R30); la lista vive en `ExternalApiUrlPolicy.IsReservedHeader` y copiada en el patrón del catálogo
  (`IntegrationProviders.ExternalApi`), amarradas por una prueba: se cambian juntas.
- **Un valor del cliente en la ruta del bloque HTTP se escapa y se vigila** (P2-R31/R32/R34): `Uri.EscapeDataString`, y un segmento
  `.`/`..` (también `%2E%2E`) o un valor vacío o de sólo `/` y `\` (P2-R46) rechaza la llamada (`path_dot_segment`,
  `path_value_empty`) sin hacerla, porque
  `System.Uri` resuelve `..` aun codificado y un `{id}` vacío llega a la colección con un `PUT`. El cuerpo se interpola
  JSON-aware (P2-R11): dentro de un string, el texto escapado; suelto, el literal JSON (`null` si falta la variable).
- **El mapeo de la respuesta no usa la gramática de las variables.** `mapping.from` acepta `$`, `$[0]`, `[0].id` y rutas con puntos
  (`JsonPath.IsValidResponsePath`, P2-R36): Clasic responde arreglos en la raíz (`[].id`, Anexo C). Un `from` que no cumple es
  `http.mapping_path_invalid` al validar (antes guardaba `null` en silencio). Y el validador detecta `{referencia}` con un patrón
  más laxo (`VariableToken`: `{_x}`, `{a..b}`) que `TemplateText`, que es lo que el motor reemplaza.
- **Esperas con tope, pero el lease de 30 s no acota la unidad.** El adjunto del bloque HTTP se espera 20 s (4 × 5 s, un presupuesto
  por bloque para la url y el archivo), no los 60 s de D-H8; el motor corta en `EngineReentries.Max = 25` reentradas por unidad (HTTP
  u horario que vuelven a entrar al motor) con `automations.run.loop_detected`, igual que el simulador (P2-R40); y un solo
  `ClaimAsync` reclama una fila, para que cada unidad arranque con el lease entero (P2-R24). Aun así el peor caso (25 × (20 + 10) s
  más 15 s por envío) pasa el lease: por eso el candado de la conversación es `pg_try_advisory_xact_lock` y, ocupado, la fila se
  reprograma a 5 s (P2-R45) en vez de bloquear al worker; cada reprogramación gasta un intento (README, «El lease de 30 s no acota la
  unidad»). No vuelvas al `pg_advisory_xact_lock` que espera.
- **`http_calls.outcome` mide 24, no 16** (spec §4.2 dice 16; `response_too_large` ya tiene 18, migración `WidenHttpCallOutcome`).
  Un outcome nuevo más largo es otra migración.
- **La causa de un traspaso del bot vive en el evento `AutomationPaused` (`details.handoff`), no en la corrida** (spec 2026-10-24).
  `conversation_runs.failure_code` sólo la tiene cuando falló; un bloque «Pasar a un asesor» alcanzado deja `HandedOff` sin código. Un
  `connection_unavailable` del bloque HTTP lleva el motivo en `ErrorCode` (`secret_unreadable`, `not_chosen`, …) y sigue sin fila en
  `http_calls`. `IIntegrationConnections.ResolveAsync` devuelve `ConnectionResolution`: un consumidor nuevo lee `.Connection` y decide qué
  hace con `.Unavailable`, y los códigos `snake_case` los arma sólo `ConnectionReasonCodes` (Bootstrapper). `HandoffCauseKind`
  (Automations) y `AutomationHandoffKind` (Messaging) se cambian juntos: los amarra `HandoffCauseKindParityTests`.
- **El simulador corre el borrador, no la versión publicada, y `Start` exige cuerpo** (`{ "liveHttp": false }`; sin cuerpo no es lo
  mismo). Con `liveHttp` un bucle de HTTP puede tardar `25 × timeoutSeconds` en un solo request. El bloque HTTP del simulador usa
  `JsonPath.SelectResponse`, el mismo mapeo del ejecutor.
- **Las plantillas del sistema de Automatizaciones se cargan al arrancar**: un JSON embebido roto tumba el host (a propósito).
- **`GET /customers` de Clasic responde `items`/`totalCount`, no `data`/`total`, y el cliente se busca con `?document=`, no con
  `?identificationNumber=`** (plan 4, 2026-10-23). `identificationNumber` es la búsqueda del admin y casa por subcadena: con «1» el
  bot leería el cliente, las direcciones y la orden de otra persona; `document` es igualdad exacta. `?phone=` recibe el `wa_id`
  completo (`57…`) y Clasic recorta a diez dígitos; con el valor vacío responde **400 a propósito** (QEP manda `telefono = ''` sin
  `wa_id`, y un filtro vacío ignorado le aceptaría la política a un cliente cualquiera): ese 400 va por `error` al asesor, no se
  «arregla» en Clasic. Y `/services` y `/departments/*` son públicas, así que la ruta de prueba de la conexión «API externa» tiene
  que ser una protegida (`/failures`, D-K10): con una pública, «Probar» da OK con cualquier clave.
- **En una Lista dinámica, `x.id` es el id de la fila (texto) y `x.item.id` es el valor del elemento.** Un cuerpo JSON hacia
  una API que espera un número lleva `{x.item.id}` sin comillas: `"{x.id}"` compila, pasa las pruebas con el fake y Clasic
  responde 500 (`invoiceItems[0].id` como string; verificado el 2026-10-23 en la revisión final del plan 4). En una query de
  URL sí va `{x.id}`, porque es texto.
- **`other` de Botones/Lista existe sólo con `fallback = otherOutput`** (spec 2026-10-25 D-3): `FlowNodeKinds.OutputsOf` la omite con
  `repeat` y `handoff`, el motor no la sigue aunque la versión publicada la tenga, y `FlowNormalizer` la quita al guardar. Al agotar los
  intentos sin salida conectada manda `Chatbot.ExhaustedAction` (`Restart` por defecto), no el flujo. El traspaso a un miembro pasa por
  `IMessagingAssignees.CanReplyAsync` en el adaptador: si no puede, cae sin asignar sin fallar.
- **`customers.data_consents` es prueba legal: sólo se inserta** (spec 2026-10-26 D-5). Nada la edita ni la borra salvo la fusión,
  que mueve `customer_id` (`CustomerMergeStore.MoveOwnedAsync`), y el borrado de clientes, que **no la borra**: la tabla ya no tiene
  FK a `customers` y cada fila guarda `customer_name` y `customer_phone_e164`, una foto tomada al borrar (spec 2026-11-10); el estado vigente es la última fila por `(occurred_at, sequence)`.
  Una respuesta es una fila: el reintento de una unidad del motor lo frena `IX_data_consents_reply_message`, que `CustomersUnitOfWork`
  traduce por nombre; no lo cambies por la consulta previa. Los CHECK de la tabla exigen identidad de canal en lo que escribe el bot y
  en `Revoked` + `ThisConversation`, y la prohíben en `Revoked` + `Other`: un `INSERT` a mano tiene que cumplirlos. El texto que se
  guarda es el que salió (`RunState.ConsentPrompt`, columna `conversation_runs.consent_prompt`), no la plantilla del bloque.
  **Dos relojes:** la fila del bot lleva la hora de Meta (truncada al segundo) y la revocación la del servidor; ordenar por
  `(occurred_at, sequence)` es lo buscado, nunca por `sequence` sola. **Un toque vale sólo para la pregunta que cita:** los ids
  `accepted`/`rejected` son fijos y WhatsApp deja tocar los botones viejos, así que un toque cuyo `reply_to_message_id` no es el
  `ConsentPrompt.PromptMessageId` vigente es una respuesta inválida (`FlowEngine.IsStaleConsentTap`); la fila guarda el citado en
  `replied_to_message_id`. **Deploy y rollback:** el kind `dataConsent` es sólo hacia adelante —un backend viejo no lee un flujo
  publicado que lo tenga— y el `Down` de `AddCustomerDataConsents` borra la prueba legal: nunca se corre. El rollback es revertir el
  código y dejar la tabla.
- **«Política de datos» tiene tres modos para «No acepto»** (spec 2026-10-26 repetir): `onReject` = `repeat` | `rejectedOutput` (default de todo bloque viejo) | `handoff`. `rejected` existe sólo con `rejectedOutput`: `OutputsOf` la omite y `FlowNormalizer` la quita. En `repeat`, el contador es `RunState.ConsentRejections` (columna `conversation_runs.consent_rejections`), no `Attempts` ni `ConsentPrompt`: vuelve a 0 en `Go`, `Leave` y `Restart`, nunca en `Resend`. En los dos modos sin `rejected`, todo camino que hoy saldría por ella (intentos agotados, cliente sin registrar) traspasa con `onRejectMemberId`, sin leer la regla de agotamiento del chatbot. Los modos son sólo hacia adelante: tras un rollback del backend, el motor viejo sigue la `rejected` ya quitada como `null` y termina con `EndRun(OutputUnconnected)` ante «No acepto» o el agotamiento, así que toda versión publicada en `repeat` o `handoff` hay que republicarla en `rejectedOutput`.
- **`run_units` se escribe en el mismo `SaveChanges` de la unidad y vive 3 días (`RunDetailRetentionDays`); armarla nunca lanza
  (`RunUnitWriter`).** Un camino nuevo que cierre una corrida fuera del motor llama `WriteClosed`, o la vista no explica por qué
  terminó.

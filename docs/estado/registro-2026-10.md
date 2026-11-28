# Registro de `qep-backend` — 2026-10

Entradas del registro de [`docs/estado.md`](../estado.md) de este mes, la más reciente arriba. Lo que vale hoy está en
`estado.md`; esto es historia y no se corrige.

- 2026-10-31 — **Imagen entrante que no se veía hasta recargar** (frontend `c0ad80ae`, en `origin/develop`; falta `main`): el hilo la
  pintaba antes de terminar la copia a R2, `GET /media` daba 404 y el `<img>` no volvía a pedirla. Imagen, sticker y video reintentan
  solos unos 70 s (`useMediaCopyRetry`) y luego ofrecen «Intentar de nuevo». Mensajería 1026 en verde; sin navegador.
- 2026-10-31 — Mergeado a `develop` de los dos repos (`--no-ff`; ya en `origin/main` de los dos repos) **«Iniciar asistente» desde el hilo** (spec
  2026-10-31; backend `5df69090`, frontend `1e0e0033`): `POST /conversations/{id}/automation/start` y `automationStart` en el resumen; también con
  pausa manual o traspaso, siempre sin asesor. Sin migración. Integración 29 + 84 + 24 + 13 en verde, que cubren además la entrada de abajo, y
  frontend de mensajería en verde; sin suite completa, navegador ni Meta real.
- 2026-10-31 — Mergeado a `develop` de los dos repos (`--no-ff`, sin push) **la parte en horario de «cuándo toma el bot la conversación»** (spec
  2026-10-26, D-1, D-1b, D-2, D-3b, D-12, D-12b; backend `07fcaafb`, frontend `589e0f4f`): `NewConversation` y `OutsideHours` arrancan con cualquier
  entrante, y una pausa automática sin asesor ya no frena al bot; `Manual`, `Handoff` y tener asesor sí. **Sin correr pruebas, por pedido del owner
  para desplegar rápido:** sólo se compiló y se revisó el SQL contra filas reales. Faltan las de `RunProcessor`, `TriggerEvaluator` y las de
  integración de Automations y Messaging; `AutomationInboxTests` puede tener un caso que espera lo contrario. La cobertura fuera de horario de
  conversaciones asignadas (D-5 a D-16) sigue sin construir.
- 2026-10-31 — Mergeado a `develop` de los dos repos (`--no-ff`, sin push) **borrar las plantillas del sistema desde sus tarjetas** (backend
  `eb68a999`, frontend `496039b2`): botón por plantilla en cualquier estado, nombre real visible y el texto en el diálogo para copiarlo. Cierra el
  pendiente de la entrada siguiente. El tope de creaciones sube de 5 a 20, porque borrar reinicia las versiones en 1, y sólo se adopta una `External`
  viva. Sin verificar contra Meta real: la prueba manual y que una borrada se liste como `PENDING_DELETION`.
- 2026-10-31 — Mergeado a `develop` de los dos repos (`--no-ff`, sin push) **las plantillas de cotización y cobro editables por tenant** (backend
  `2badbf31`, frontend `64ede2f9`): texto base editable antes de ir a Meta, lista cerrada de variables con claves en español, envío y bandeja desde la
  fila aprobada, sin pie en las nuevas y `{nombre}` renombrada a `{nombre_contacto}` en Automatizaciones. Cierra el rechazo de `qep_cotizacion` por el
  subcódigo 2388293. Sin verificar: la prueba manual y la aprobación del texto base en Meta. Pendiente del owner: no hay «Borrar» en pantalla para una
  plantilla del sistema, así que un tenant con la plantilla aprobada no cambia el texto desde QEP.
- 2026-10-31 — Mergeado a `develop` (`--no-ff`, sin push) **el texto legible de las fallas de validación** (merge `7f9c3c6c`): el `detail` del
  422 era el volcado de FluentValidation, con `Severity` y saltos de línea, y ahora es una frase fija en todas las rutas. Los títulos de 401,
  403 y 422 pasan a español. No cambian el status, `code` ni `errors`.
- 2026-10-31 — El owner corrió `ops/2026-10-30-repair-history-direction.sql` en producción, dentro del plazo de la purga de entregas. Lo
  reportó el owner; el resultado no se verificó contra la base.
- 2026-10-31 — **Alertas de observabilidad** (Task 11) en `main` de `AndresJQCode/migracion-k8s` (`1b5c64c`) y aplicadas por el owner:
  `QepQueueLagging`, `QepMediaQueueLagging`, `QepQueueMetricsMissing`, `QepHigh5xx`, `QepCpuSaturated` y `QepDbPoolSaturated`. `QepHigh5xx`
  pide al menos 5 errores en 10 minutos porque el tráfico medido es de unas 0,03 req/s y un piso de req/s nunca se alcanzaba. Observabilidad
  ya estaba en `main` (`eeea44bd`, imagen `4818`); la rama `feature/qep-observabilidad` de la entrada de abajo se borró al publicar en `main`.
- 2026-10-31 — Mergeado a `develop` de los dos repos (`--no-ff`, sin push) **las variables fijas de respuestas rápidas y campañas** (backend `df6d6ae5`,
  frontend `adff7601`): `{nombre_contacto}` y `{saludo}`, con `{nombre}` todavía aceptado, y la fuente `greeting` de campañas, resuelta por envío en la
  zona del tenant. Verificado con las unitarias de Messaging, `ArchitectureTests`, integración filtrada y la suite del frontend, no con la suite completa
  del backend. La migración de datos entró después por decisión del owner, sin esperar el día acordado (ver «Antes de llevar `develop` a `main`»).
- 2026-10-31 — Mergeado a `develop` (`--no-ff`, sin push) **el arreglo del falso tope de plantillas** (merge `d91471fe`): el subcódigo 2388293 de
  Meta (demasiadas variables para el texto) dice «limit» en el título y salía como `limit_reached` en cuentas sin plantillas. Ahora es
  `rejected_by_meta` con el texto de Meta. La plantilla de cotización sigue rechazada en producción hasta el spec 2026-10-31 de plantillas editables.
- 2026-10-31 — Mergeado a `develop` de los dos repos (`--no-ff`, sin push) **el indicador de escritura del asesor** (backend `2b5d66ac`,
  frontend `022806ac`). En WhatsApp marca leído para el contacto aunque en QEP siga sin leer; decisión del owner, en CLAUDE.md. Verificado
  con las pruebas de las clases tocadas, `ArchitectureTests`, `Bootstrapper.UnitTests` y las de mensajería del frontend, no con la suite
  completa ni contra Meta real.
- 2026-10-31 — Mergeado a `develop` de los dos repos, con push, **el renombre de `clientId` a `idempotencyKey` en la API externa** y el
  `connectionId` visible en Desarrolladores (backend `b43337ff`). Corrige un desajuste: `main` traía el backend con `clientId` y el
  frontend enseñando `idempotencyKey`, un 422 para quien siguiera el ejemplo. Hay que llevarlo a `main`. El código
  `external.client_id_reused` pasó a `external.idempotency_key_reused`. Verificado con las pruebas externas y `ArchitectureTests`, no con
  la suite completa.
- 2026-10-31 — Mergeado a `develop` (`--no-ff`, sin push) **el texto de los mensajes no soportados del historial** (merge `054918d7`): un
  `131051` en un mensaje importado ya no pide reenviarlo, dice que el historial no trae ese tipo. Los mensajes en vivo y el `131060` no
  cambian. Verificado con las unitarias de Messaging y `ThreadApiTests`, no con la suite completa.
- 2026-10-31 — Mergeado a `develop` de los dos repos (`--no-ff`, con push) **observabilidad y fluidez** (backend `6aa89693`, frontend
  `b50aa657`). La suite no dejó regresiones por nombre frente a `origin/develop`; las 17 fallas previas están en «Defectos conocidos».
  El dashboard vive en `migracion-k8s`, rama `feature/qep-observabilidad`.
- 2026-10-30 — Mergeado a `develop` (`--no-ff`, sin push) **el arreglo de la dirección del historial de coexistencia**: Meta nunca manda `to` en
  el historial, así que 795 mensajes de QCode entraron como recibidos; ahora la dirección sale de comparar `from` con el hilo. Script
  `ops/2026-10-30-repair-history-direction.sql` para lo ya importado: se corre a mano después de desplegar y antes de que se purguen las entregas
  (alrededor del 2026-11-06). Trae también lo que dejaba a `develop` sin compilar ni pasar `--locked-mode` tras el renombre a ServiceContracts.
- 2026-10-30 — Mergeado a `develop` del frontend (`--no-ff`, con push) **la pantalla del gestor de plantillas de WhatsApp**
  (`feature/gestor-de-plantillas`, merge `30e7f3d`): lista por conexión en `/integrations/whatsapp/$connectionId/templates`
  con estados, motivos de rechazo, filtros, sincronización con Meta, ficha y borrado, tarjetas de la plantilla de cotización
  y de cobro, y constructor con vista previa de teléfono en vivo; enlace «Plantillas» en la tarjeta del número, sólo con
  `messaging.template.manage`. Consume los contratos del spec 2026-10-14 §4.7 sin cambiar el backend. Verificado: build,
  46 pruebas nuevas y suite completa 8375 de 8377; las 2 fallas son de `variants-table.test.tsx` (catálogo) y fallan igual en
  `develop` sin esta rama. Sin probar contra Meta real. La rama remota `feature/gestor-de-plantillas` quedó sin borrar: el
  entorno del agente no permitió borrar ramas en GitHub.
- 2026-10-30 — Mergeados a `develop` de los dos repos (`--no-ff`, con push) **el renombre del módulo de cobros recurrentes a `ServiceContracts` («Contratos de servicio») y el historial de precios con motivo obligatorio**: backend `0165a99` y `cbaf506`, frontend `517bf22` y `893a795`. Decisión del owner: el renombre no toca nada desplegado (esquema, clave, permisos, rutas, códigos y configuración siguen en `billing`). **El backend se mergeó sin compilar**: el contenedor no tenía SDK de .NET; antes de llevarlo a `main` hay que correr el build y las pruebas.
- 2026-10-30 — Mergeado a `develop` (`--no-ff`, sin push) **el override temporal del webhook por WABA**: con
  `Meta:App:WebhookOverrideCallbackUrl` puesto, el Embedded Signup suscribe la WABA con `override_callback_uri`, para probar el número de QCode en
  QEP con la URL de la app todavía en lulo. Sin la variable `META_WEBHOOK_OVERRIDE_CALLBACK_URL` no cambia nada. Sin probar contra Meta real;
  los estados de plantillas de esa WABA siguen llegando a la URL de la app. Quitar el ajuste no limpia la WABA.
- 2026-10-30 — Mergeados a `develop` de los dos repos (`--no-ff`, sin push) **los ajustes del historial por coexistencia** (decisiones del owner):
  conversación de un eco a número desconocido nace resuelta (`started_by_echo`), el reporte de Mensajería excluye lo importado (`imported`,
  migración `AddImportedMarker`), `fromHistory` en la conversación y aviso propio en el compositor. Suite completa no recorrida tras los ajustes.
- 2026-10-30 — Mergeado a `develop` de los dos repos (`--no-ff`, sin push) **el historial por coexistencia de WhatsApp**: backend
  `ab353bdb`, frontend `18627637`. Incluye `a67ba40d`, que arregla el CS0229 de `CustomerWriteHandlersTests` que ya estaba en `develop`.
- 2026-10-29 — Mergeado a `develop` (`--no-ff`, sin push) **el índice del outbox para el polling de los consumidores**
  (`perf/outbox-event-name-index`, merge `0c3a233d`): `IX_outbox_messages_event_name_occurred_at` en
  `platform.outbox_messages`, migración `AddOutboxEventNameIndex` (Tenancy). Motivo: en producción la tabla tenía
  7.8 k filas con 672 M tuplas leídas por seq scan y más de 2.6 M llamadas; cada consumidor (Audit, Notifications,
  Pipeline, Shopify, Storage, Identity) consulta cada 10–15 s por `event_name` y no había índice que empezara por esa
  columna. **No es parcial por `processed_at`, y no se purga por `processed_at`:** los consumidores llevan lo pendiente en
  su inbox, no en `processed_at` (que sólo marca el despachador de Tenancy), así que borrar por ahí perdería mensajes sin
  consumir. La purga segura o un cursor por consumidor quedan para un spec aparte. Pruebas: `OutboxPollingIndexTests`
  (RED con `Seq Scan`, GREEN con el índice) y `OutboxIdempotencyTests`, 2/2; suite completa sin correr.
- 2026-10-26 — Mergeado a `develop` (`--no-ff`, sin push) el **resto del backend de sinónimos de ciudades**
  (`feature/sinonimos-de-ciudades-2`, merge `e2a8f9a7`, SC-12..SC-14 más arreglos a SC-11 y SC-13 de la revisión): lista,
  corrección, borrado y reintento de sinónimos con su impacto, recorrido `POST …/backfill`, prueba de punta a punta,
  README y CLAUDE.md. Sin migraciones nuevas. Build de `develop` mergeado: 0 errores, 0 advertencias (en un worktree
  aparte: la API corriendo en el checkout principal bloquea la copia de DLLs). Pruebas escritas y sin correr por pedido
  del owner. Rama conservada; worktree borrado.
- 2026-10-26 — Mergeado a `develop` (`--no-ff`, sin push) el **backend parcial de sinónimos de ciudades**
  (`feature/sinonimos-de-ciudades`, merge `32ec08b5`, SC-01..SC-11 de 14). Conflicto resuelto en `CompositionRootTests`:
  65 de `develop` (`customers.data_consent.revoke`) + los dos `operator.cities.*` = 67. Build sin errores de compilación
  (la API corriendo bloqueó la copia de DLLs); pruebas sin correr por pedido del owner.
- 2026-10-25 — Mergeado a `develop` del frontend (`--no-ff`, sin push) el **plan 3 del envío por canal, frontend**
  (`feature/envio-por-canal-3`, merge `a2d28c17`). Rama borrada; la carpeta del worktree quedó en disco porque un
  `codegraph serve` tiene abierto su `.codegraph/codegraph.db` (ya no está registrada en git).
- 2026-10-25 — Mergeado a `develop` (`--no-ff`, sin push) el **plan 3 del envío por canal, backend**
  (`feature/envio-por-canal-3`, merge `b2e8f509`): envío de la cotización por Meta al hilo de Mensajería y elección
  del canal guardada. Trae la migración aditiva `AddTenantWhatsAppChannel` (tabla vacía). Rama y worktree borrados.
  Decisiones del owner del mismo día: sin siembra de la elección; el `GET` de canales acepta los dos permisos (DP-F1).
- 2026-10-25 — Mergeado a `develop` (`--no-ff`, sin push) **Formato regional** (`feature/formato-regional`, merge
  `a7c559be`): PDF, WhatsApp de Zenvia, correos de exportación, historial nuevo y fechas de texto del Excel con el formato
  del tenant; la hoja del ERP queda fija. Trae la migración aditiva `AddQuotationPdfNumberFormat`. Rama y worktree
  borrados.
- 2026-10-25 — Corridas las pruebas de **variantes**, escritas y sin ejecutar desde la T4: build 0 warnings / 0 errores y
  3.647/3.647 en verde sobre `develop` `3472f01d` (Catalog 213 + 187, Quotations 1290 + 496, Pos 226 + 61, Reporting
  116 + 101, Shopify 361 + 81, Identity 22 + 36, `Bootstrapper.UnitTests` 325, `ArchitectureTests` 132), con
  `Seed__Enabled=false`. Las dos de `OrderExportApiTests` que fallaban por el formato del NIT (`6f8aa75`) ya pasan: una roja
  ahí es regresión, no falla previa. Sin cambios de código.
- 2026-10-24 — Mergeado a `develop` del frontend (ff, sin push) el frontend de **Proveedores**: `95e99419..f1cff1c3`
  (`feature/proveedores`, SUP-F01..F10, construido por otra sesión entre las 17:01 y las 18:13 con revisión por tarea,
  revisión final y una tanda de correcciones). Rama y worktree borrados; tres shells y un `python3` colgados desde las
  17:21 (un comando de la Tarea 4 que nunca terminó) bloqueaban la carpeta. Faltan la prueba manual y la fila «terceros»
  en `qep-docs`.
- 2026-10-24 — Borrado de `dev_qep` el tenant de pruebas `transportes-ci` (con su conexión `external-api`, su chatbot en
  borrador y el usuario `bot-transportes-ci@qcode.local`), a pedido del developer: las pruebas del chatbot de Transportes CI
  se hacen en Origen botánico, que tiene su propia copia. Sólo base local; no toca código.
- 2026-10-24 — Mergeado a `develop` de los dos repos **Causa del traspaso** (spec 2026-10-24): backend `--no-ff`, sin push
  (`feature/causa-del-traspaso`, que integró `develop` `edcc4fe0` en `9f47de8d`; cinco conflictos en Messaging resueltos
  conservando los dos lados: `ConversationEvent` lleva `PreviousCustomerId` y después `Handoff`, `MessageEventDto` lleva
  `TargetCustomer`, `PreviousCustomer` y `Handoff`, y el JSON del evento escribe `previousCustomerId` y `handoff`);
  frontend merge `95e99419` en `develop` del frontend, ya en `origin/develop`. El `AutomationPaused` de un traspaso lleva su
  causa en códigos (`handoff`) y `ConversationAutomationDetailsDto` suma `FailureCode`. Verificado sobre el merge de la
  rama: build 0/0; Architecture 132/132; unitarias Messaging 1323/1323, Automations 357/357, Bootstrapper 325/325 y el
  resto de los proyectos unitarios en verde; integración Messaging 563/563 y Automations 179/179. Rama y worktree se
  conservan.
- 2026-10-24 — Proveedores: **backend construido y mergeado a `develop`** (`feature/proveedores`, `d8d1f9e4..eaafba36`, merge `95e0fbc9`; rama y worktree borrados). La entrada siguiente («borradas las ramas… arranca en una sesión nueva») se escribió en paralelo sin ver ese merge y quedó superada: el backend ya no se vuelve a ejecutar; falta el frontend. Pendientes: fila «terceros» en `qep-docs` `open-decisions.md` y prueba manual.
- 2026-10-24 — Proveedores: borradas las ramas y worktrees `feature/proveedores` de los dos repos (todo ya en `develop`: spec `2a47bc6d`, plan frontend `9490220e`, plan backend `10e388bb`). El trabajo arranca en una sesión nueva siguiendo «Cómo seguir» de su fila en «Trabajo en curso». Corrige la nota anterior: la spec sí está, en `qep-frontend/docs/superpowers/specs/`.
- 2026-10-24 — Mergeado a `develop` del frontend (ff, sin push) **Ventas POS en el reporte de pedidos**: `5b07d246` (`feature/reportes-ventas-pos-front`, RVP-10..12 con revisión por tarea, revisión final y una tanda de correcciones; integró `develop` `bc0b4713` sin conflictos). Reportes 234/234; la suite completa deja 5 fallas previas de `product-form.test.tsx` en `develop` («Found multiple elements with the text: Tasa de impuesto», desde `faee9836`) y otras por tiempo bajo carga que pasan solas. Queda un «Intentá de nuevo» con voseo en `customers-report-page.tsx`.
- 2026-10-24 — Mergeado a `develop` de los dos repos (ff, sin push) **Fusión de clientes**: backend `ff8baabf` (`feature/fusion-clientes`, 10 tareas con revisión por tarea, revisión final y una tanda de correcciones; integró `develop` `6bd8ca43` con un conflicto en CLAUDE.md, resuelto conservando las dos entradas) y frontend `bc0b4713` (7 tareas, mismo proceso; integró `develop` `13ea86dc` sin conflictos). Decisiones del owner: uno queda y el fusionado queda marcado, inactivo y oculto (no se borra) con su CUC redirigiendo; todo lo del fusionado pasa al que queda; los datos del que queda mandan; irreversible con aviso; lo puede hacer el asesor; dos entradas (Anomalías y la ficha); todo en una sola transacción con SQL directo sobre los seis esquemas.
- 2026-10-24 — Mergeado a `develop` de los dos repos (ff, sin push) **Clientes duplicados**: backend `f0b8dbe3`
  (`feature/clientes-duplicados`, 13 tareas con revisión por tarea, revisión final y una tanda de correcciones) y frontend
  `68d3e6c1` (7 tareas, mismo proceso). Decisiones del owner: mismo tipo y número de documento se rechaza mostrando al dueño;
  mismo número con otro tipo avisa sin bloquear; el teléfono compartido se permite; un chat por persona con cliente principal
  que el asesor cambia; el opt-out de marketing vale para el número; la plantilla lleva el nombre del cliente principal y, sin
  conversación, el del más viejo; el cliente principal vale sólo para esa conversación «por ahora». `develop` local del
  backend divergió de `origin/develop` (20 contra 19 commits) y se reconcilió con un merge; sólo chocó `docs/estado.md`.
- 2026-10-24 — Mergeadas a `develop` (`--no-ff`, sin push) dos ramas del backend: `fix/catalog-restrict-violation`
  (`b5d42618`: Catalog, Pipeline y Customers traducen `23001 restrict_violation`, que es lo que Postgres responde con una
  FK `OnDelete(Restrict)`, además de `23503`; antes daban 500) y `feature/reportes-ventas-pos` (`206ed67f`, código de
  RVP-01..03). Sin conflictos. Verificado sobre `206ed67f`: build sin errores; Architecture 125/125; unitarias Catalog
  213/213, Customers 321/321, Pipeline 116/116, Reporting 116/116; integración Catalog 187/187, Customers 170/170, Pipeline
  78/78, Reporting 100/100.
- 2026-10-24 — Limpieza de ramas en los dos repos: borradas las ramas y worktrees ya mergeados a `develop` (backend: `automatizaciones-clasic`, `automatizaciones-constructor-backend`, `automatizaciones-motor-backend`, `chatbot-mover-conexion`, `pos-cliente-identificado`, `reportes-ventas-pos`; frontend: `pos-cliente-identificado`, `reportes-ventas-pos`, registro huérfano de `chatbot-mover-conexion`). En el frontend se integró `develop` en `feature/proveedores` y se hizo ff a `develop` (`aa517ab8`: spec y plan de proveedores). Quedan: `feature/proveedores` en ambos repos (ejecución pendiente), `feature/clientes-duplicados` en ambos (otra sesión, con cambios sin commitear) y los worktrees `hotfix/*` y `merge/hotfix-pago-parcial` del owner (ver «Hotfixes»).
- 2026-10-24 — Spec y planes de **Proveedores** (directorio) listos para ejecutar: spec aprobada por el owner el 2026-10-23 (`qep-frontend` `feature/proveedores`, `2a47bc6d`), plan backend `10e388bb` y plan frontend `9490220e` en las ramas `feature/proveedores` de cada repo, sin código. El pedido original («compras a proveedores y manejo de inventario») se recortó al directorio; se revisó «proveedores vs. terceros» y se eligió entidad propia con la identidad de `Customer` copiada campo por campo. Decisiones del owner: `suppliers` fuera del default (D-12), `advisor` sin permiso (D-14), renombrar la sección del operador (D-17).
- 2026-10-24 — Mergeados a `develop` (con push) tres ramas del backend: `feature/chatbot-mover-conexion` (`353e09bb`,
  código: traspaso de conexión entre chatbots con `takeOverConnectionIds`, con una corrección de la revisión final
  `4adce1dc`), `feature/reportes-ventas-pos` (`3244e9ff`, sólo spec y plan) y `feature/proveedores` (`64b38e86`, sólo
  plan). Los specs de mover conexión y de proveedores no están en ninguno de los dos repos. El mismo día, verificado contra
  git: los dos `develop` iguales a `origin/develop`, sin nada pendiente de push.
- 2026-10-24 — Mergeado a `develop` del frontend el frontend del embudo de ventas (`feature/embudo-de-ventas`, merge
  `--no-ff` `bdbb5963`, con push), con `origin/develop` `c9995f56` integrado antes en la rama. Prueba manual del developer
  en navegador ok (arrastre, Ganado/Perdido, 390 px, abrir desde la franja y la pestaña Negocios, otro dueño, Descartar).
  Suite: 7 fallas, todas de Catalog y ya presentes en `develop`. Contrato del backend que la UI no usa todavía: los pedidos
  del negocio y `POST /deals/{id}/orders/{orderId}/link`. Worktrees `embudo-de-ventas` y `develop-baseline` borrados.
- 2026-10-24 — Mergeados a `develop` dos pedidos del developer al probar el bot de Clasic: (1) frontend `07c20301`,
  «Publicar» sólo se habilita con cambios desde la última publicación (nunca publicado, guardado pendiente o posterior a
  `published.at`; sin cambios dice «Publicado vN» deshabilitado), porque en dev quedaron dos versiones idénticas publicadas
  con dos segundos de diferencia; (2) backend `32aaa465..58f868ec`, la palabra de reinicio («REINICIAR») termina la corrida viva
  `Completed` y arranca otra con la última versión publicada cuando difiere (mismo chatbot, `Active`); con la misma versión,
  chatbot pausado u otro chatbot en la conexión, reinicia la misma corrida como antes (excepción anotada en D-C1 del spec).
  Nota para el owner: «Hoy: atendidas» cuenta corridas, así que un reinicio a versión nueva suma dos por conversación. Hallazgo
  de la prueba manual: el túnel de Cloudflare de la máquina de dev estuvo sin red de 00:19 a 04:19 UTC del 24, así que los
  webhooks de Meta de esa noche no llegaron; Meta los reintenta tarde y la ingesta los descarta para el bot por llegar fuera de orden.
- 2026-10-23 — Escrito el spec de formato regional en PDF, correos y WhatsApp
  ([spec](superpowers/specs/2026-10-23-formato-regional-pdf-y-correos-design.md)), verificado contra `develop`
  `baf9aba2`, y dejado **pendiente** por pedido del owner: sin plan ni código. Deja abierta DP-1 (envío por Meta, plan 3
  del envío por canal).
- 2026-10-23 — Mergeado a `develop` importar y exportar chatbots desde el frontend (plan
  `superpowers/plans/2026-10-23-importar-exportar-chatbots.md`; backend `31f7704e..3ba14717`, frontend `d3add455..ee1a1d05`, ff,
  sin push): ruta `GET /automations/external-apis`; en el frontend «Importar» en la lista de chatbots (archivo `.json`, nombre
  editable, un selector por «API externa» del archivo, «Elegir después en el editor» deja `http.connection_missing`) y «Exportar»
  en el editor y en cada fila (`<nombre-en-kebab>.json`, con `autosave.flush()` antes). Ese mismo día, en `develop` (`cf4c36a5`),
  el fixture de Clasic pasó a ser el archivo de exportación completo, con layout por niveles (antes cuadrícula de 180 px), las
  salidas opcionales (`invalid`, `other`, `tooMany`) conectadas a «Pasar a un asesor» (sin conectar terminaban la corrida en
  silencio) y «Política de datos» como Botones «Acepto»/«No acepto» con el cierre «Política no aceptada» (15 pruebas e2e, una
  nueva). El borrador «Clasic» de `dev_qep` lleva los mismos cambios (versión 39). Más tarde ese día (developer, al probar con un número que
  Clasic no tiene): la política se pide a todo cliente sin `acceptPolicy = true` (D-K3 ya no la salta con teléfono desconocido);
  «Acepto» guarda con `PUT` sólo si hay cliente por teléfono, `new-customer` manda `acceptPolicy = true` y, si la ficha que
  aparece por cédula no tiene la política, se guarda ahí la aceptación de este chat (47 bloques, 16 pruebas e2e). Pendientes:
  (b) tarea 6 del plan, dibujar la salida «otra respuesta» de Botones abajo y no como cuarto chip; (c) menores de la revisión
  final (otros `export.*` del 422 muestran el texto en inglés; «Cargando conexiones…» también cuando `/external-apis` falla;
  preseleccionar la conexión cuando el id del archivo ya existe en el tenant).
- 2026-10-23 — Mergeado a `develop` el plan 4 (Clasic) del constructor de chatbots: QEP `f1223edd..94de87c0` (ff) y
  Clasic `feature/bot-qep` `3b24c6e..159e864` (ff a su `develop`, sin push; `main` lo mueve el owner). Siete tareas con
  revisión por tarea, dos revisiones finales (una por repo) y una tanda de correcciones en cada una. Lo que cambió respecto
  al spec al verificar la API de Clasic (D-K5..D-K13 del plan): `GET /customers` responde `items`/`totalCount`; el bot busca
  con `?document=` (exacto; `identificationNumber=` es el buscador por subcadena del admin, con el que «1» devolvía un cliente
  ajeno); no hay usuario bot (`createdBy = Whatsapp`, como hoy); la clave va al ConfigMap; sin Jest en Clasic; la ciudad del
  cliente nuevo va como texto con Antioquia/Medellín fijos; «Otra dirección» y una orden abierta terminan con un asesor; el
  id del equipo viaja como número (`{equipo.item.id}`: como texto Clasic responde 500). Pendiente del owner: la regla de
  orden abierta por dirección (el flujo la aplica por equipo). Queda un chatbot «Clasic» de prueba importado en `dev_qep`
  (tenant Origen botánico, `01a16606-18bc-70f0-8939-c3f6bc15863c`) para abrirlo en el editor.
- 2026-10-23 — Mergeado a `develop` de los dos repos el filtro de ventas del POS por documento del cliente (backend
  `a11da4f9`, frontend `4ea1a9e4`, ff). `GET /pos/sales` acepta `customerDocument` (≤ 32, contra la foto
  `customer_identification_number` de la venta) y `number` deja de ser exacto: los dos coinciden por un pedazo con
  `ILIKE` y comodines escapados, y se combinan con Y. En el popover de Ventas, «Documento del cliente» junto a «Número de
  venta». Pruebas: Pos unitarias 226/226, integración 61/61, `ArchitectureTests` 125/125; frontend `src/features/pos`
  341/341. Sin push.
- 2026-10-23 — Decisión del owner: buscar una venta del POS por número o documento recorre todas las fechas. Lo resuelve
  el frontend (`aac349fc`, ff a `develop`): aplicar una búsqueda sobre el «hoy» sin tocar quita el rango, y el chip de
  fechas desaparece con él; un rango elegido por el cajero se conserva. El backend no cambia. Frontend `src/features/pos`
  344/344. Sin push.
- 2026-10-23 — Mergeado a `develop` el arreglo del entrante fuera de orden (`35799550`, ff): el bot volvía a mandar el
  mensaje de fuera de horario sin que el cliente escribiera, porque Meta reintentó 19 min tarde un «hola» de las 21:42
  que llegó detrás de uno de las 21:56 ya contestado (`dev_qep`, 2026-10-22). `InboundIngestion.ShouldEnqueue` ya no
  encola un entrante anterior al `last_inbound_at` previo de la conversación; el mensaje sigue entrando al hilo. Regla en
  el spec 4 (§5.1) y en CLAUDE.md. Pruebas: Messaging integración 529/529, unitarias 1281/1281, Automations integración
  148/148. Sin push.
- 2026-10-22 — Mergeado a `develop` del frontend el frontend del constructor de chatbots (plan 3, `551c05d3..208e13f2`),
  construido con subagentes en 14 tareas (CB-01..14) con revisión por tarea, revisión final de la rama y una tanda de
  correcciones. Lo más delicado: una sola cola de escritura por editor (autoguardado de 2 s, validar, publicar, ajustes,
  borrar) con la `version` que devolvió la escritura anterior; un 412 recarga el editor. La revisión atrapó antes del
  merge: Suprimir con el foco fuera del lienzo borraba el bloque seleccionado; un `cta.url` `javascript:` quedaba
  clicable (ahora sólo `https`, `src/lib/https-url.ts`); un falso «No pudimos guardar»; una simulación muerta tras un
  reinicio fallido. **Otra sesión mergeó la rama a mitad (CB-01..12) y la empujó a `origin/develop` (`1d453314`, 21:25)**;
  el resto entró encima sin cambios de árbol. Importar/exportar quedó fuera por decisión del developer. Suite: sólo las
  5 fallas de `catalog/product-form` que ya tenía `develop`. Falta la prueba manual contra el backend real.
- 2026-10-22 — Mergeado a `develop` del frontend el frontend del spec 1 de Automatizaciones (`dd22f151..551c05d3`, con
  `develop` integrado en la rama y ff; sin push), construido con subagentes en 9 tareas (AH-01..09) con revisión por
  tarea, revisión final de la rama y una tanda de correcciones (franjas ordenadas al guardar, solape en excepciones,
  confirmar al eliminar). Decisiones: la hora y los días se formatean en `es-CO` fijo (idioma de la UI, no formato
  regional); los festivos como día calendario; «Hasta» 00:00 se guarda como 1440 (D-H12). Suite completa: 10 fallas
  en `landing`, `product-form` y la ruta `/`, idénticas en `develop` antes del merge. Falta la prueba manual contra el
  backend real. Al verificar se encontró que el bloqueante de `variantId` ya no aplica: el frontend de variantes
  (VAR-01..13, `22cde669`) está en `origin/develop` del frontend.
- 2026-10-22 — Mergeado a `develop` el backend del cliente identificado en POS (`d4d4eff3..bcc92424`, ff), construido con
  subagentes en 9 tareas (POSC-01..09) con revisión por tarea, revisión final de la rama y una tanda de correcciones; la
  compuerta de merge corrió `dotnet build -warnaserror` y los proyectos de prueba de Pos, Customers, Bootstrapper,
  ArchitectureTests y el catálogo de autorización de Tenancy, todos verdes. El gotcha de CLAUDE.md sobre clientes
  `Incomplete` se corrigió: `POST /pos/customers` crea incompletos con documento. Pendiente del owner: «Completar ficha»
  en Clientes podría precargar el documento de una ficha creada en la caja. Frontend en curso.
- 2026-10-22 — Mergeado a `develop` el backend 2 del constructor de chatbots (`6261ea26..a194e6ab`, ff), construido con
  subagentes en 15 tareas (0-14) con revisión por tarea, revisión final de la rama y una tanda de correcciones. Motor
  puro (`FlowEngine`) y `ConversationRun`; `RunProcessor`/`RunEffects` con lock por conversación no bloqueante
  (`pg_try_advisory_xact_lock`), `clientId` determinístico por `(runId, baseStepNumber, effectIndex)` (el spec §5.3
  tiene la errata) y tope de 25 reentradas por unidad; worker con abandono a las 24 h y purgas (inbox 7 días,
  `http_calls` 30 días, simulaciones 2 h); el motor respeta el módulo apagado. `GET /runs` y «Datos capturados» para el
  asesor. Integrations: proveedor `external-api` con política anti-SSRF (rangos prohibidos, sin proxy ni redirects,
  headers reservados) y probador. Bloque HTTP con interpolación JSON-aware, escape del path, mapeo con `$`/`[0]` (Clasic
  responde arrays en la raíz) y espera de 20 s por el adjunto. Simulador del borrador. Flag `Enqueue` prendido.
  **Límites conocidos** (README): una caída entre la llamada HTTP y el commit de la unidad repite la llamada; una
  unidad de más de ~45 s quema intentos del inbox de esa conversación y, pasados ~70 s, entrantes siguientes pueden
  cerrarse `failed` sin que el bot los lea (el arreglo es un `RetryLater` de Mensajería que no cuente intento).
- 2026-10-22 — Mergeado a `develop` el backend 1 del constructor de chatbots (spec 4 de Automatizaciones,
  `46455d73..ff7f6956`, ff), construido con subagentes en 20 tareas. Mensajería: interactivos salientes con los límites de
  Meta, autoría `sent_by_kind`/`sent_by_chatbot_id`, `MessageDto.sentByChatbot` e `interactive`, `IBotOutbound`, cola
  `messaging.automation_inbox` tras `Messaging:Automation:Enqueue` (apagado) con `IAutomationInbox` (lease, cerca por
  `attempts`, cierre de filas envenenadas). Tenancy: módulo `automations` (depende de `messaging`). Permisos
  `automations.chatbot.read`/`manage` sólo en `admin`. Módulo `Automations`: esquema de siete tablas, agregado `Chatbot`,
  modelo del flujo y validador, endpoints de §4.4 salvo simulación y `GET /runs`, plantillas del sistema embebidas,
  import/export. La revisión final (dos paquetes) no encontró Critical; la tanda de corrección cerró los Important que el
  backend 2 consume. Al verificar apareció que la suite de Mensajería moría a las ~512 pruebas en macOS (cada host de
  prueba dejaba vivo el watcher de los user-secrets): se corrigió en el harness (`ff7f6956`). **Pendiente para el backend
  2, antes de prender el flag:** purgar filas procesadas de `automation_inbox`; validar al publicar lo que el motor no puede
  ejecutar (0 botones, lista fija vacía, `timeoutSeconds` fuera de 1..10, `maxAttempts` fuera de 1..5, regex vacía, URL
  del botón); las versiones publicadas se cargan con sus blobs en cada `FindAsync` (table splitting + migración vacía);
  decisión del owner sobre si la respuesta del bot marca como leído para los asesores (`MarkRepliedAsync`).
- 2026-10-21 — Mergeado a `develop` el backend del spec 1 de Automatizaciones (`62cb0aaf`: asistente en la conversación y
  horarios de atención), construido con subagentes en 7 tareas. La revisión final encontró un Critical real —mover el
  predeterminado a un horario más antiguo daba 409 porque EF ordena los `UPDATE` por clave primaria— que se corrigió con
  `SwapDefaultAsync` en una transacción con orden fijo. Decisiones del developer en la revisión del spec: D-H9 pasa a
  festivos de Colombia por interruptor; el módulo se renombra «Automatizaciones». Frontend pendiente (plan escrito).
- 2026-10-21 — Mergeado a `develop` el plan 2 del envío por canal, recortado a Zenvia (`bba8ba1b..07217b94`, decisión
  del owner): la conexión de Zenvia lleva `templateId` y `orderTemplateId`, cotización y pedido salen por la conexión
  del tenant y se retira la cuenta global. Revisión independiente hecha; tres correcciones en la misma rama.
- 2026-10-21 — El owner pone en pausa el spec de onboarding para priorizar otras tareas. Lo pendiente quedó en la
  sección «Para retomar» del spec. Las urgencias de los tokens de Shopify (v1 y QEP, 2027-01-01) no esperan a
  retomarlo.
- 2026-10-21 — Tres cosas en el spec de onboarding:
  - **D-6 (decidida por el owner):** al registrar un tenant se crea una empresa que factura por defecto. NIT, ciudad y
    cuenta pasan a ser opcionales mientras la empresa tenga «datos incompletos».
  - **Cambio a QEP:** QEP pasa a `app.lulocrm.com` y `api.lulocrm.com` cuando tenga la paridad con Lulo CRM v1,
    guías incluidas. La categoría de la ficha no cambia.
  - **Webhooks (§9.11):** QEP atiende las rutas viejas de la v1 como alias y limpia las suscripciones al enlazar. Se
    encontró que la v1 responde 201 en los webhooks de cumplimiento con cualquier firma, y que no valida el HMAC de
    los de pedidos.
- 2026-10-21 — El owner decide que QEP reemplace como backend a la app de Shopify «lulo crm» que ya está publicada,
  conservando el nombre (spec de onboarding §9.10). Al investigarlo aparece que Shopify exige tokens que expiran a
  todas las apps públicas desde el 2027-01-01, y QEP no los soporta.
- 2026-10-21 — El owner decide S-1 a S-5 del onboarding de Shopify (§9.8):
  - **S-1:** la app es gratis en Shopify y QEP cobra su suscripción como hoy. Falta confirmarlo con Shopify Support.
  - **S-2:** en los productos importados manda Shopify.
  - **S-3:** los pedidos se emparejan por el id de la variante de Shopify y después por SKU.
  - **S-4:** a los precios sin impuestos se les suma el IVA al importar.
  - **S-5:** `read_products` se pide a las tiendas ya instaladas cuando eligen traer los productos.
  - **S-6:** los borradores entran inactivos y los archivados no se traen.
  - **S-7:** «Productos desde Shopify» se construye como parte de este onboarding.
- 2026-10-21 — Se suma al spec de onboarding la §9, el onboarding de la tienda de Shopify:
  - Shopify no exige un asistente, pero sí OAuth primero, una interfaz que funcione al aceptar los permisos, la
    configuración de la conexión, los webhooks de privacidad y el cobro por Shopify.
  - Hoy una empresa nueva que instala desde Shopify queda con todos sus pedidos en espera por `product_unmatched`.
  - El paso 4 trae los productos desde Shopify.
- 2026-10-21 — Se escribe el spec de onboarding («Primeros pasos» por metas según módulos y bienvenida a invitados),
  revisado en tres rondas contra el código. Sin commitear ni implementar; espera al owner.
- 2026-10-21 — Se verifica este archivo contra el código: los once specs «Por verificar» están construidos; variantes
  (backend), la fuente del documento de Shopify y el plan 1 del envío por canal ya están en `develop` con push; se
  confirma el `dotnet format` roto; se detecta que `develop` de backend y frontend no son compatibles por `variantId`.
- 2026-10-21 — Se mergea a `develop` el plan 1 del envío por canal (proveedores habilitados por tenant), en los dos repos.
- 2026-10-21 — Se mergea a `develop` la parte 1 de la fuente del documento de Shopify (`b7447106`); quedan el endpoint de
  opciones, el select del frontend y la documentación.
- 2026-10-21 — Se mergea a `develop` el backend de variantes, con la revisión final (`92f74496`).
- 2026-10-21 — Se escribe y commitea el spec de días de historial para «Sincronizar ahora» de Shopify (origen: un pedido
  anterior al enlace no entraba por D-S4). El frontend está sin verificar.
- 2026-10-20 — Se crea este archivo. Diseño y plan de variantes en `develop` local.

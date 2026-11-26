---
paths:
  - "src/Modules/Shopify/**"
  - "tests/Modules/Shopify/**"
  - "src/Modules/Integrations/**"
  - "tests/Modules/Integrations/**"
  - "src/Bootstrapper/**/Shopify*.cs"
---

# Shopify e Integraciones

Reglas de `qep-backend` que no se deducen leyendo el código. Se cargan solas al leer o editar un
archivo que coincide con `paths` (arriba). Las reglas generales están en [`CLAUDE.md`](../../CLAUDE.md).

- **Integrations no usa el `IAuditRecorder` ni el `IOutboxWriter` compartidos:** los dos están
  ligados a `TenancyDbContext`, y una segunda ligadura le robaría en silencio la auditoría o el outbox a
  Tenancy. Un módulo nuevo con auditoría atómica declara su propio puerto (`IIntegrationsAuditRecorder`,
  como `IIdentityAuditRecorder`). Y un consumidor de una conexión nunca referencia
  `Modules.Integrations.Application`: declara su puerto y el adaptador va en Bootstrapper
  (`IntegrationsLayerTests`).
- **El token de Shopify de una conexión lo refresca sólo `ShopifyTokenRefreshWorker`; el canje y el enlace adquieren y guardan
  uno nuevo, y los tres van bajo el candado `ShopifyTokenLock.Namespace`** (spec 2026-11-12). El canje
  (`CompleteShopifyInstall`) guarda el token en la instalación y, si la tienda ya tiene conexión, canjea con el candado de esa
  conexión; el enlace (`LinkShopifyStore`) refresca el de la instalación con el candado de la instalación y lo pasa a la
  conexión con el de la conexión. El refresh token rota: dos refrescos de la misma tienda, o un refresco y un canje o un
  reenlace a la vez, dejan guardado un par que Shopify ya retiró, y la tienda se cae una hora después sin error. El candado es
  un advisory lock de transacción de dos claves `(20261112, hashtext(id))` sobre el id de la conexión o de la instalación
  (`IIntegrationsUnitOfWork`): el worker usa la variante que no espera y salta la conexión; el canje y el reenlace, la que
  espera, **antes** de llamar a Shopify o de cargar la conexión. Ningún request tiene dos candados a la vez, y así no hay
  ciclo. El `Version` optimista no alcanza: el perdedor ya gastó el refresh token. Un camino nuevo que pida o refresque un
  token de Shopify —o que reemplace los secretos de una conexión que ya existe— toma el mismo candado; los consumidores sólo
  leen el guardado.
- **Rotar el client secret de la app de Shopify en el Partner Dashboard sin actualizar `Backend-prod` deja todas las tiendas en
  `token_expired` en el siguiente refresco** (spec 2026-11-12). El refresco se firma con ese secreto, Shopify responde un 4xx y
  el worker lo toma como definitivo; la única salida es que cada comercio vuelva a abrir la app. El secreto se rota en los dos
  lados en el mismo despliegue.
- **Una conexión puede publicar `integrations.connection-needs-attention.v1` dos veces seguidas, y el payload trae
  `failureCode`** (owner, 2026-11-12: el evento identifica el error). `ReportTokenExpiredAsync` publica también desde
  `NeedsAttention` con otro código (por ejemplo `webhooks_not_registered` → `token_expired`); desde `Paused` no publica, y ya en
  `token_expired` no hace nada. Hoy nadie consume ese evento: el primero que lo haga (un correo, un aviso) decide por
  `failureCode`, o le llegan dos avisos por la misma conexión.
- **Con el token de Shopify vencido, `ShopifyConnectionRef` sale `IsOperational = true` y `AccessToken = null`** (spec
  2026-11-12 §6.4, D7). No lo «arregles» a no operativa: `RedactShopAsync` daría la tienda por desinstalada y borraría su espejo,
  y la ingesta dejaría de importar, todo porque el worker iba tarde. Quien llame a la Admin API mira `AccessToken`, no
  `IsOperational` (`ReconcileStore` salta con 503 `sync_unavailable`; `ListOperationalAsync` la deja fuera). Lo amarran
  `ShopRedactWithAnExpiredTokenDeletesNothingOfTheCurrentConnection` y `OrdersTopicWithAnExpiredTokenStillImports`.
- **`token_expired` sale sólo de un 4xx salvo 429 en el refresco.** El gateway clasifica todo no-2xx que no sea 429 ni 5xx como
  `Rejected` (un 3xx incluido), y eso sirve al canje y al probador; el worker filtra por status (`IsDefinitiveRejection`) porque
  `token_expired` sólo se sale reautorizando. Un consumidor nuevo del refresco hace lo mismo.
- **El webhook de Shopify (`/api/webhooks/shopify`) se prueba con el stub de auth apagado**, igual que el de Meta: en
  modo stub `RequireCsrfHeaderMiddleware` ni se registra, así que una prueba con los headers `X-*` pasa aunque la
  exención esté rota (`ShopifyApiHarness.WithRealAuthentication`, `ShopifyWebhookApiTests.WebhookIsExemptFromCsrf`). El
  webhook se exime por el prefijo `/api/webhooks/` y el inicio B de la instalación por ruta exacta
  (`SignedAnonymousPaths`, duplicada de `IntegrationsEndpoints` y amarrada por `ShopifyInstallApiTests`); `install/complete`
  **no** está exento: lo llama el SPA con `X-Qep-Client` (2026-10-18, `0c6c06ae`). Una ruta anónima nueva que firme Shopify va a esa
  lista, con su prueba, o responde 403 sólo en producción.
- **Un pedido externo repetido lo frena `IX_orders_tenant_external_key`, no la búsqueda previa.** `ExternalOrderService`
  busca por la clave antes de crear, pero el webhook y la conciliación pueden llegar a la vez, y el worker puede caer entre
  crear el pedido y guardar la fila del espejo: lo único que deja un solo pedido es el índice único parcial
  `(tenant_id, source, external_key)`, que `QuotationsUnitOfWork` traduce **por nombre** a `order.order.external_key_taken`
  y el adaptador resuelve releyendo el existente (`AlreadyExisted`). No lo cambies por una comprobación en el handler. La
  clave es `"{shopId}:{shopifyOrderId}"` y no la conexión, para que borrar y volver a enlazar la tienda no duplique; un
  `INSERT` a mano en `quotations.orders` con `source = 'Shopify'` necesita una clave que no exista. Precedente (Review
  Focus 1 de `docs/superpowers/plans/2026-10-18-shopify-backend.md`): borrar la fila del espejo y recibir el pedido otra vez deja un pedido, y si llega cancelado lo anula —hasta
  `8b58fe9f` (2026-10-18) la fila nueva cancelada quedaba `Ignored` con el pedido vivo (`ReimportAfterMirrorRowDeleted*`).
- **Todo snapshot nuevo de una fila del espejo de Shopify pasa por `ShopifyOrderIngestion.ApplyAsync`.** El webhook, la
  conciliación, «Sincronizar ahora» y el reintento manual llaman al mismo método, que captura el estado anterior antes de
  `ApplySnapshot` y corre la reacción de pago y cancelación (§6.3) antes del guardado (2026-10-18, `82c4b2d9..f9b98fd1`, `WorkerFailedReactionIsSeenAgainOnRetry`). Llamar al importador o
  a `ApplySnapshot` directo compila y pasa las pruebas del importador, y pierde en silencio el pago o la cancelación.
  Desde el spec 2026-11-14 también detecta las ediciones: `IShopifyOrderEditDetector.DetectAsync` corre ahí, después del espejo
  y antes de la reacción, y sólo crea el cambio (no escribe en Quotations); `ApplyPendingAsync` aplica el pendiente de una tienda
  `Automatic` **después** del guardado y atrapa el `concurrency.conflict` de Quotations (el cambio queda `Pending`). Un cambio
  con algún grupo en conflicto no se aplica solo ni en `Automatic`: queda `Pending` para «Cambios por revisar» (spec §3). Un
  camino nuevo que dispare ediciones fuera de la ingesta rompe la regla del único camino y deja cambios sin espejo que los
  explique. Hay otros que escriben la fila **sin** traer un snapshot, y no son un bug: las decisiones sobre un cambio
  (`RecordApplied` al aceptar, `AdoptPayload` al rechazar o revisar), el trabajo de corrección de ciudad (`RecordProjection`) y
  la corrección del asesor (`SetQepAttributes`). Ninguno llama a `ApplySnapshot`.
- **La corrección del asesor no mueve `applied_projection`; la corrección de ciudad del sistema sí** (plan D3, spec
  2026-11-14 §5). La proyección es lo que QEP escribió desde Shopify: si la corrección del asesor la moviera, la siguiente edición
  de Shopify sobre ese grupo dejaría de ser conflicto y pisaría lo del asesor, también después del eco. Lo amarran
  `ACorrectionDoesNotMoveTheProjection`, `AfterAnEchoALaterShopifyEditOfThatGroupIsStillAConflict` y
  `TheEchoOfAPushedCorrectionCreatesNoChange`; el trabajo de sinónimos la mueve (`AnAppliedOrderCorrectionMovesTheProjectionAndEnqueuesAPush`).
  Una fila sin `applied_projection` (importada antes) toma la del pedido actual (D6, `AnOrderImportedBeforeTheSyncTakesAShopifyNoteEditWithoutConflict`).
  Y un eco que el cambio no aplicaba nunca mueve la proyección: sólo `SettleAsync`/`ApplyAsync` la mueven para los grupos que el
  propio cambio aplicaba.
- **`orderUpdate` reemplaza la dirección entera, y un envío sólo manda los grupos que se encolaron** (spec 2026-11-14, plan D12).
  `company` (donde muchas tiendas guardan el documento) y `countryCode` salen del **último** snapshot; sin ellos, corregir la
  ciudad borra la «Empresa» en Shopify. Mandar un grupo no encolado pisa la nota de Shopify con el `BuyerNotes` vacío de un pedido
  importado antes (sin backfill). Lo amarran `PushKeepsCompanyAndCountryFromTheLatestSnapshot` y
  `APushOnlySendsTheGroupsItWasEnqueuedFor`. Del `userError` de Shopify sólo se loguean los campos: el mensaje puede traer datos
  del comprador (`ThePushNeverLogsTheTokenOrTheUserErrorMessage`).
- **`current_quantity` ausente es `quantity`, y el descuento va sobre la cantidad original** (plan D2). Leer sólo la vigente deja
  en 0 toda línea de un payload que no la trae (las fixtures no la traen); calcular el porcentaje sobre la vigente cambia el precio
  neto de una línea a la que le quitaron unidades (`discount_allocations` reparte el descuento de la original).
- **El `PUT …/shopify/orders/{id}/corrections` enrola el `QuotationsDbContext` del request en la transacción de Shopify**
  (`ShopifyOrderCorrectionWriter`, como `CatalogProductDeletionCleanup`): el pedido, `qep_attributes` y el envío encolado
  quedan juntos o no quedan (`AShopifyFailureAfterTheOrderWriteRollsBackBoth`). Después del commit ese contexto apunta a una
  transacción terminada: nada en el scope vuelve a usarlo (el handler arma la respuesta con el estado que devolvió
  `CorrectAsync`, sin releer el pedido). Un camino nuevo que corrija un pedido de Shopify y encole su envío pasa por ahí, nunca por `IShopifyOrderWriter`.
- **Los envíos a Shopify tienen tres orígenes** (spec 2026-11-24 §11.1): la corrección del panel, la corrección de ciudad por
  sinónimo y la dirección cambiada en «Editar pedido». La tercera **no** pasa por `ShopifyOrderCorrectionWriter`, porque no
  escribe el pedido desde Shopify: Quotations publica `quotations.order.shipping-party-changed.v1` en su transacción y
  `ShopifyShippingPartyChangedApplier` sólo encola. Como la corrección del asesor, no mueve `applied_projection`
  (`TheProjectionDoesNotMove`). Y lo que el worker no puede mandar se rechaza en el guardado del pedido: una regla nueva de
  dirección va en `ShopifyShippingAddressRules`, que usan los dos lados (`ShopifyOrderPushProcessor` y
  `ExternalOrderDeliveryCheck`); en uno solo, el envío queda reintentando horas o el guardado deja pasar lo que no sale.
  La dirección que sale la arma `ShopifyPushAddress`, y con ella el planificador reconoce el eco de una parte de fuera de
  Colombia: un campo nuevo del envío que no entre en `IsForeignEchoAsync` deja un conflicto falso por cada envío.
  La dirección extranjera que **entra** la arma `ShopifyForeignAddress`, para el importador y el planificador; y los tres
  adaptadores de Bootstrapper que escriben la parte pasan por `ShopifyOrderWriter.ToExternalParty` o llevan sus mismos campos.
- **Un payload sin la clave `zip` no es lo mismo que `"zip": null`** (`ShopifyAddress.ZipUnrecorded`): es de antes de que el
  espejo lo leyera, y de ahí no sale ningún cambio. `ReadPayload` lo marca y `WritePayload` vuelve a omitir la clave; serializar
  un snapshot por otro camino lo convierte en «no tenía zip».
- **Un envío a Shopify `Held` nunca lo reclama el worker; sólo lo mueven los dos endpoints de sincronización** (owner,
  2026-11-14). Una corrección no se pierde por la conexión: guardada con la conexión no operativa (`connection_inactive`), con la
  conexión caída al mandar (`connection_missing`) o con `access_denied`, el envío queda retenido con el motivo en `failure_code`.
  `ListPendingAsync` y `TryClaimAsync` filtran `status = 'Pending'`, así que reactivar la conexión no lo manda: lo sueltan
  `POST …/orders/{id}/push` y `POST …/stores/{connectionId}/pushes/release`. `IX_order_pushes_mirror_pending` cubre `Pending` y
  `Held` (un solo envío abierto por pedido) y se fusiona igual en los dos; si cambias el filtro, cambia también `FindOpenAsync`. Un
  motivo nuevo de la conexión va a `HoldAsync` del procesador, no a `FailAsync`: un `Failed` no se puede volver a mandar.
- **Límite conocido de la detección de ediciones** (spec 2026-11-14): dos snapshots del mismo pedido que tocan el mismo grupo,
  intercalados por milisegundos (B reemplaza a A antes de que la escritura de A en Quotations confirme, y el segundo guardado de A
  falla), pueden dejar la proyección vieja y un `qep_changed` falso en la edición siguiente. No se cerró; la alternativa es un
  candado advisory por fila del espejo alrededor de la detección y la aplicación. No lo «arregles» moviendo la proyección con
  la corrección del asesor.
- **`ShopifyLayerTests` prohíbe que el módulo Shopify referencie módulos de negocio:** Application sólo ve Tenancy y Audit.
  La conexión (Integrations), el pedido (Quotations), el cliente (Customers), el producto (Catalog) y la ciudad
  (Geography) entran por los puertos de `ShopifyPorts.cs`, con adaptador en `src/Bootstrapper/Shopify*.cs`. El costo son
  dos copias que se cambian juntas, cada una con su prueba de paridad en `Bootstrapper.UnitTests`, que ve los dos módulos:
  `ShopifyWebhookTopics` vive en Integrations.Application (los que el enlace registra) y en Shopify.Application (los que el
  worker atiende), amarradas por `ShopifyWebhookTopicsParityTests` (2026-10-18, `7b83a958`), y
  `ShopifyCashOnDeliveryGateways.Normalize` copia `CarrierName.Normalize`, amarrada por `ShopifyNormalizationParityTests`
  (2026-10-18, `e157d9c2`).
- **`shopify.custom_line_mappings` es contrato con `lulo-crm-migrator`, que la siembra por SQL** (`INSERT … ON CONFLICT
(connection_id, title) DO NOTHING`, spec 2026-11-13). Renombrar una columna (`id`, `tenant_id`, `connection_id`, `title`,
  `variant_id`, `created_at`, `created_by`) o el índice `IX_custom_line_mappings_connection_title` compila, pasa las pruebas de
  este repo y rompe la siembra de la migración de Lulo v1; se cambia junto con el spec del migrador. `title` va recortado y
  `created_by` es id de **membresía** activa, no de usuario (como `stores.updated_by`): el migrador siembra la del dueño. Y
  `IsCustom` exige las llaves de ids presentes en `null` **y que la línea no tenga SKU**: un fixture sin `product_id`/`variant_id`
  no es una línea personalizada, y con «ausente = nulo» todo pedido de los fixtures quedaría en espera. Una variante borrada en
  Shopify deja `variant` y `product` en `null` pero conserva el SKU: esa línea es de producto y nunca casa por nombre (los dos
  parsers y `ShopifyOrderImporter.CustomTitleOf` aplican la misma regla). El 409 sale sólo del índice
  (`ShopifyUnitOfWork` lo traduce por nombre): no lo muevas a una consulta previa en el handler.
- **Todo camino que escriba en Catalog desde Shopify pasa por `ShopifyProductSync`** (spec 2026-11-17). Ahí van el candado de
  producto `pg_advisory_xact_lock(20261118, hashtext('{conexión}:{producto}'))` y el `CatalogDbContext` enrolado en la transacción
  de Shopify (`IShopifyCatalogWriter`): producto, opciones y vínculos se confirman juntos o no se confirman. Un rechazo de Catalog
  deja la transacción abortada en Postgres; no se sigue usando: se descarta y lo siguiente va en otra (la importación corre cada
  producto en su scope, `IShopifyProductSyncScope`). Escribir en Catalog desde otro lado deja dos productos cuando el webhook y la
  importación llegan a la vez.
- **Un producto con `external_source` rechaza en el `PUT` nombre, opciones, variantes, códigos y precios de lista** (422
  `catalog.product.managed_externally` con `errors`): los manda Shopify. Un camino nuevo de edición en Catalog aplica
  `ExternallyManagedProducts`, o la siguiente sincronización pisa lo que el asesor cambió. Desvincular
  (`DELETE …/shopify/products/{id}/link`) es la salida.
- **La imagen importada que no entra en la galería se libera explícitamente**, o queda un archivo `Available` de Storage para
  siempre (la galería es lo que lo retiene). La imagen va después del commit del producto, fuera de su transacción.
- **El perfil de la tienda (`shopProfile`) es un campo interno de la conexión con el correo y el teléfono del comerciante**
  (spec 2026-11-17 onboarding §2). Nada lo loguea ni lo audita: un log nuevo del asistente lleva el id de la conexión y el tipo de
  la excepción (`TheSetupNeverLogsTheShopEmailOrPhone`). Un campo nuevo de `ShopQuery` entra en `ShopifyShopProfile` de **los dos**
  módulos (Integrations y Shopify, que no se ven) y en el mapeo campo por campo de `ShopifyConnectionDirectory` (Bootstrapper).
- **`products_coverage`, `products_coverage_accepted_at` y `setup_history_*` no suben la `Version` de `shopify.stores`.** Son estado
  del asistente, no configuración: si un camino nuevo del asistente la sube, el formulario de la tienda abierto recibe 412 al
  guardar. Lo amarran `RecordingCoverageDoesNotBumpTheVersion` y `ThePutStillSucceedsAfterCoverageWasMeasured`.
- **`POST …/setup/company` crea la empresa en la transacción de Companies y la asigna en la de Shopify.** Si la asignación pierde
  por versión, el 412 trae `details.companyId` (`ShopifySetupCompanyConflictException`) y el reintento lo manda para sólo asignar.
  No lo «arregles» borrando la empresa: una creación en otro módulo no se deshace, y el reintento por `companyId` es lo idempotente.
  La versión se revisa **antes** de crear, para que un 412 común no deje empresas sueltas.

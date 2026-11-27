---
paths:
  - "src/Bootstrapper/**"
  - "**/Migrations/**"
  - "tests/Bootstrapper/**"
---

# SQL crudo entre módulos (columnas leídas por nombre)

Reglas de `qep-backend` que no se deducen leyendo el código. Se cargan solas al leer o editar un
archivo que coincide con `paths` (arriba). Las reglas generales están en [`CLAUDE.md`](../../CLAUDE.md).

- **`CustomerMergeReferences` también escribe en `loans.loans`** (`customer_id`, sin subir versión; las cuotas no guardan cliente). Es
  una sentencia más antes de la de Shopify, que sigue siendo la última. Renombrar esa columna compila y rompe la fusión en
  ejecución; la amarran `CustomerMergeReferencesTests` y `CustomerMergeAcrossModulesApiTests.TheRealAdapterRunsAgainstTheMigratedSchema`.
- **`CatalogVariantUsageChecker` lee tablas de Quotations, Pos y Wms por nombre.** Es SQL crudo en Bootstrapper sobre
  `quotations.quotation_items`/`order_items` (unidas a `quotations`/`orders` por el tenant), `pos.sale_lines` (unida a
  `pos.sales`) y, de Wms, `wms.stock_movements`, `wms.stock_balances`, `wms.adjustment_lines` (unida a `wms.adjustments`
  por el tenant, sólo los ajustes abiertos: `Proposed` y `Approved`, que todavía pueden contabilizarse), `wms.lots` y
  `wms.task_lines` (unida a `wms.tasks` por el tenant: líneas `line_status = 'Open'` de tareas `Planned`, `Ready`,
  `Assigned` o `InProgress`; un estado de tarea nuevo que todavía pueda confirmar entra en esa lista),
  `wms.incidents` (`tenant_id`, `variant_id` y `status IN ('Open', 'InReview')`: una novedad abierta puede escribir al ledger al
  resolverse, DA-51), y
  corre en la conexión del `PUT` de producto para quedar dentro de su transacción. Renombrar
  `variant_id` o una de esas tablas compila, y el `GET` y el `PUT` de producto responden 500 (`42703`). Sólo
  `ProductVariantDeletionApiTests` usa un doble del puerto; el resto de las pruebas de Catalog que leen o guardan un
  producto corre el checker real contra el esquema migrado y se cae con ese 500, que no se parece a la causa. Se cambia
  junto con el checker y sus pruebas: `CatalogVariantUsageCheckerTests` (que arma réplicas mínimas de las tablas, a
  mano: no siguen solas a la migración), `StockProbeTests.TheRealCheckerSeesAWmsMovementAgainstTheMigratedSchema` (y sus
  hermanas `…AnOpenAdjustmentLineAndALot…`, `…AnOpenTaskLine…`, `…AnOpenCountLine…` y `…AnOpenIncident…`) y las
  dos `TheCatalogUsageQueryFindsTheBackfilledVariant` (`VariantLinesMigrationTests`, `SaleLinesVariantMigrationTests`),
  que resuelven el checker real del contenedor y lo corren contra el esquema de las migraciones.
- **`LockNextCandidateAsync` (Wms, tareas) lee `wms.incidents.verification_task_id` y `reported_by` por nombre** para no ofrecerle a
  quien reportó una novedad el `Count` que la verifica (DA-45), y `FindOpenCountAsync` y el barrido leen `wms.task_lines` y
  `wms.tasks` por nombre de columna. Es SQL crudo dentro del mismo módulo: renombrar una de esas columnas compila y rompe el filtro
  o el barrido en ejecución. Las amarran `TaskCandidateQueryTests` y `TaskForwardCompatibilityTests`.
- **`CustomerMergeReferences` escribe en nueve esquemas por nombre.** La fusión de clientes conoce por nombre las tablas de
  quotations, pos, pipeline, messaging, billing, loans, support, ai y shopify: es SQL crudo en Bootstrapper sobre
  `quotations.quotations`/`orders.client_id`, `pos.sales.customer_id`,
  `pipeline.deals`/`deal_quotations`/`deal_orders`/`triage_decisions.customer_id`, `messaging.conversations.customer_id`,
  `messaging.campaigns.audience_customer_ids`, `billing.services`/`charges`/`customer_profiles.customer_id`,
  `loans.loans.customer_id`, `support.tickets.customer_id` (sin subir `version`, como el préstamo), `ai.tests.customer_id` (las pruebas
  del playground de agentes de IA; las sesiones siguen a su conversación), `scheduling.appointments.customer_id` (citas en
  cualquier estado, sin subir `version`) y `shopify.orders.qep_customer_id`, sobre la
  conexión del `CustomersDbContext` para quedar dentro de su transacción. Renombrar una de esas columnas compila y la fusión
  responde 500 (`42703`); como todo es una transacción, no fusiona nada. Lo amarran `CustomerMergeReferencesTests` (réplicas
  hechas a mano: no siguen solas a una migración) y `CustomerMergeAcrossModulesApiTests.TheRealAdapterRunsAgainstTheMigratedSchema`.
  Una tabla nueva que guarde un id de cliente vivo entra en `MoveAsync` y en esas dos pruebas, o la fusión la deja apuntando a un
  cliente inactivo. Y `merged_into_id` apunta siempre a un no fusionado: quien lea un fusionado sigue un solo salto.
- **`CatalogProductDeletionCleanup` escribe en `store.order_request_lines`/`order_requests` por nombre y enrola el
  `QuotationsDbContext` en la transacción de Catalog.** El borrado permanente de un producto (`DELETE .../catalog/products/{id}`)
  vive en Bootstrapper por la misma razón que `CustomerMergeReferences`: borra las líneas de las solicitudes de la tienda en
  `New` con SQL crudo (`request_id`, `variant_id`, `status`, `version`) y llama a `IQuotationVariantDeletion` de Quotations sobre
  la conexión del `CatalogDbContext` (`SetDbConnection` + `UseTransactionAsync`), todo envuelto en `CustomersWriteRaces.RunAsync`
  para que un deadlock o una falla de serialización sea 412 y no 500. Renombrar una de esas columnas compila y deja el `DELETE` del
  producto en 500 (`42703`/`42P01`); como es una transacción, no queda nada escrito. Lo amarran `ProductDeletionCleanupTests`
  (Quotations) y `ProductDeletionStoreCleanupTests` (Store), que resuelven el adaptador real y lo corren contra el esquema migrado.
  Tras confirmar, ese `QuotationsDbContext` queda apuntando a una transacción terminada: nada más en el scope debe usarlo.
- **`MarginReportSource` lee `quotations.order_items`/`orders` (`unit_cost`, `paid_at`), `pos.sale_lines`/`sales` y `catalog.products`/`product_variants` por nombre.** Renombrar una columna compila y deja `/reports/margin` en 500; lo amarra `MarginReportApiTests`. Una vía nueva a `FullPaymentReceived` pasa `OrderLineCosts` al dominio: si no, el pedido cuenta en el reporte con líneas «sin costo».
- **`OrdersReportSource` lee `quotations.orders` y `pos.sales` por nombre** (spec 2026-10-23). Es SQL crudo en
  Bootstrapper, un `UNION ALL` de las dos tablas (`ReportRowsSql`), porque son de `DbContext` distintos y EF no los une.
  Renombrar una columna de cualquiera de las dos compila y deja `/reports/orders` y `/summary` respondiendo 500: lo amarran
  `OrdersReportPosApiTests` y las demás pruebas `OrdersReport*`, que corren contra el esquema migrado. Un estado nuevo de
  `PosSaleStatus` u `OrderStatus` que no sea venta tiene que entrar en `is_void`, o empieza a sumar en el resumen sin
  error. Y un filtro opcional nuevo se omite del texto cuando no viene, no se manda como parámetro nulo: PostgreSQL no
  tipa un `NULL` suelto (`could not determine data type of parameter`).
- **`QueueMetricsSource` lee seis tablas por nombre** (observabilidad fase 2, 2026-10-30): `messaging.webhook_deliveries`,
  `automation_inbox`, `message_media` (unida a `messages`), `campaign_recipients` (unida a `campaigns`),
  `platform.outbox_messages` y `shopify.webhook_deliveries`. Es SQL crudo en `src/Bootstrapper/Observability/`. Renombrar una
  columna compila y deja esa cola sin dato; si es `webhook`, dispara `QepQueueMetricsMissing`. Una cola nueva entra en
  `QueueNames`, en `QueueMetricsSourceTests`, en el dashboard y en las reglas de alerta. La edad mira sólo `attempts = 0`, y la de
  `automation` repite el `NOT EXISTS` de `AutomationInbox.ClaimAsync`: si cambias el reclamo, cambia también la lectura. En una
  prueba, el registro del tenant deja eventos sin despachar en `platform.outbox_messages`: ciérralos antes de esperar una cola vacía.
- **`CityCorrectionInsights` lee `quotations`, `customers`, `shopify`, `tenancy` e `identity` por nombre.** Es SQL crudo en
  Bootstrapper que alimenta los impactos y los contadores de la consola de ciudades. Renombrar una columna de cualquiera de esas
  tablas compila y deja respondiendo 500 (`42703`) a los dos `impact`, a `GET synonyms` y `GET unresolved/{id}`, y a `resolve`,
  `PUT synonyms/{id}` y `retry` **después** de confirmar su escritura (la respuesta lee los contadores): el cambio queda hecho
  aunque el cliente vea el 500. El recorrido (`POST backfill`) no lee este adaptador. Lo amarra `CityCorrectionInsightsTests`
  (en el proyecto de Shopify, que es el que sabe armar pedidos, clientes y huellas reales): una tabla o columna nueva que lea el
  adaptador entra ahí.
- **`PlatformUsageSource` lee `tenancy.tenants`, `messaging.messages` y `messaging.conversations` por nombre** (spec 2026-11-01). Es SQL crudo en
  Bootstrapper sobre la conexión de `MessagingDbContext`: una conversación cuenta una vez por canal si tuvo un entrante en vivo (`direction = 1`,
  `NOT imported`) desde el corte, y el canal sale de `conversations.channel`, no de la conexión. Renombrar una de esas columnas compila y deja
  `GET …/operator/usage` en 500 (`42703`); lo amarra `PlatformUsageApiTests`, que corre contra el esquema migrado. Si agregas un tipo de mensaje
  que no deba contar como actividad del cliente, tiene que quedar fuera de `direction = 1` o llevar `imported`, o infla el reporte sin error.
- **`SupportThreadFeed` lee `messaging.messages` por nombre** (Soporte fase 2, plan 2026-11-15). Es SQL
  crudo en Bootstrapper sobre la conexión de `SupportDbContext`, igual que los demás lectores cruzados: renombrar `occurred_at`,
  `direction`, `imported` o `conversation_id` compila y deja el seguimiento del hilo sin vincular nada (el worker sólo loguea). Lo
  amarra `SupportBootstrapperAdaptersTests`, que corre el adaptador real contra el esquema migrado. Un entrante es `direction = 1` y
  `NOT imported`: un camino nuevo que escriba entrantes del cliente con otra marca los saca o los mete en Soporte sin error.
- **`SupportSurveyFeed` y `SupportAutoCloseFeed` leen `messaging.messages` por nombre** (`reply_to_message_id`, `kind`, `text`,
  `direction`, `imported`, `occurred_at`), y el segundo también `support.ticket_messages.state`. Renombrar una columna compila y deja la
  encuesta sin respuestas o el cierre automático cerrando antes de tiempo, sin error. Los amarra `SupportBootstrapperAdaptersTests`.
  `CustomerDeletionReferences` suelta los cuatro ids de mensaje de `ticket_surveys` (`message_id`, `rating_message_id`,
  `comment_message_id`, `prompt_message_id`): una columna nueva con un id de mensaje en `support.*` entra ahí.
- **Borrar clientes escribe en muchos esquemas por nombre** (spec 2026-11-10; `src/Bootstrapper/CustomerDeletionReferences.cs`, igual que
  `CustomerMergeReferences`). Toca `messaging` (conversaciones y todo lo que cuelga: mensajes, adjuntos, mensajes programados,
  `automation_inbox`, destinatarios de campañas con `customer_id` en `NULL`, `audience_customer_ids`), `automations`
  (`conversation_runs`, `run_units`, `http_calls`), `ai` (`sessions`, `turns`, `model_jobs` y `tool_calls` de sus conversaciones, y las
  `tests` del playground que fijaron al cliente con sus trabajos; `tool_calls` guarda sus pedidos y cotizaciones, y `usage_records` se
  queda porque es el consumo pagado), `pipeline` (`deals`, `deal_stage_history`, `deal_quotations`, `deal_orders`,
  `triage_decisions`; `origin_conversation_id` de otros negocios a `NULL`), `pos.sales`, `shopify.orders`, `billing.customer_profiles`
  y `geography.unresolved_city_occurrences`. Si una tabla nueva guarda un id de cliente, hay que agregarla ahí; lo fijan
  `CustomerDeletionAcrossModulesTests` (Customers.IntegrationTests) y los dobles de `CustomerWriteDoubles`. Bloquean el borrado
  cotizaciones, pedidos, préstamos, contratos de servicio (y sus cobros), tickets de soporte (`support.tickets.customer_id`, motivo
  `supportTickets`, en cualquier estado: también uno cerrado), citas de la Agenda (`scheduling.appointments.customer_id`, motivo
  `schedulingAppointments`, en cualquier estado: también una cancelada, PD-6; la salida es la fusión, que las mueve sin subir `version`)
  y una conversación bloqueada. Los amarran `CustomerMergeReferencesTests` (réplica de `scheduling.appointments`),
  `CustomerMergeAcrossModulesApiTests.TheRealAdapterRunsAgainstTheMigratedSchema` y `ADeleteIsBlockedByAnAppointmentInAnyState`.

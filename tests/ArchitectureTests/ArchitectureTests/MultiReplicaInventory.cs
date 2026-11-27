namespace ArchitectureTests;

/// <summary>
/// Cómo convive un hosted service con otro pod que corre el mismo código (spec 2026-10-29, D-6, y la decisión P3 del plan
/// 2026-11-13). <see cref="Unsafe"/> es listable para que la prueba no exija arreglarlo ya, pero bloquea <c>replicas &gt; 1</c>.
/// </summary>
public enum WorkerClassification
{
    /// <summary>Reclama cada unidad de trabajo con <c>FOR UPDATE SKIP LOCKED</c> o con un lease.</summary>
    Claimed,

    /// <summary>Correr dos veces da lo mismo: un <c>DELETE</c> por corte, un <c>UPSERT</c>, un <c>ON CONFLICT</c>.</summary>
    Idempotent,

    /// <summary>Toma un advisory lock antes de cada unidad de trabajo.</summary>
    AdvisoryLock,

    /// <summary>La segunda instancia choca con una clave única o con la concurrencia optimista: sólo log o un tick perdido.</summary>
    Noisy,

    /// <summary>Tiene que correr en cada pod y no reparte trabajo (un listener, una métrica del pod).</summary>
    PerPod,

    /// <summary>Apagado en producción por configuración; prendido, sólo vale con una réplica.</summary>
    OffInProduction,

    /// <summary>Con dos pods duplica un efecto externo o una fila. Bloquea <c>replicas &gt; 1</c>.</summary>
    Unsafe,
}

/// <summary>Cómo se comporta un estado en memoria con dos pods.</summary>
public enum StateClassification
{
    /// <summary>Cada pod tiene el suyo y eso está bien: el mismo comportamiento que con una réplica.</summary>
    PerPodByDesign,

    /// <summary>Se comparte entre pods (Valkey) y cae a la copia local si lo compartido falla.</summary>
    SharedWithLocalFallback,

    /// <summary>Con dos pods cada uno ve la mitad, o el límite se duplica. Bloquea <c>replicas &gt; 1</c>.</summary>
    Unsafe,
}

/// <param name="Evidence">«archivo:línea — mecanismo».</param>
public sealed record WorkerEntry(string TypeFullName, WorkerClassification Classification, string Evidence);

/// <param name="Evidence">«archivo:línea — mecanismo».</param>
public sealed record StateEntry(string TypeFullName, StateClassification Classification, string Evidence);

/// <param name="Evidence">«archivo:línea» del registro con la fábrica.</param>
public sealed record FactoryEntry(string ServiceTypeFullName, string ImplementationTypeFullName, string Evidence);

/// <summary>
/// El inventario D-6: todo hosted service y todo estado en memoria de nuestros ensamblados, con cómo convive con otro pod.
/// Lo exige <c>MultiReplicaInventoryTests</c>: quien agrega un worker o un singleton con estado lo clasifica acá, en el
/// mismo commit. Va por nombre completo y no por <c>typeof</c> porque casi todos los workers son <c>internal</c> de su
/// módulo; una entrada que ya no está registrada pone roja otra prueba, así que el texto no queda viejo en silencio.
/// </summary>
public static class MultiReplicaInventory
{
    // Verificado contra el código el 2026-11-13, rama feature/replicas-multiples (54 hosted services; 42 desde que los trece consumidores de correo
    // pasaron a un solo bucle, 2026-11-15). Las rutas son
    // relativas a src/.
    public static readonly IReadOnlyList<WorkerEntry> Workers =
    [
        // --- Claimed: FOR UPDATE SKIP LOCKED o lease. ---
        new("Modules.Support.Infrastructure.SupportSurveyWorker", WorkerClassification.Claimed,
            "Modules/Support/Modules.Support.Infrastructure/Persistence/SupportSurveyRepository.cs:16-38 — FOR UPDATE SKIP LOCKED + locked_until de 2 minutos confirmado antes de enviar y renovado por encuesta (RenewClaimAsync); el envío es idempotente por ClientId (el id de la encuesta). Las respuestas por WhatsApp (SupportSurveyAnswers) toman la fila con FOR UPDATE (LockAsync, línea 53) y el mensaje que pide el comentario es idempotente por un ClientId fijo por encuesta"),
        new("Modules.Tenancy.Infrastructure.Messaging.OutboxPublisherWorker", WorkerClassification.Claimed,
            "Modules/Tenancy/Modules.Tenancy.Infrastructure/Messaging/OutboxProcessor.cs:27 — FOR UPDATE SKIP LOCKED"),
        new("Modules.Quotations.Infrastructure.Exports.ExportJobWorker", WorkerClassification.Claimed,
            "Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/ExportJobQueue.cs:46 — FOR UPDATE SKIP LOCKED con lease"),
        new("Modules.Messaging.Infrastructure.Media.MediaCopyWorker", WorkerClassification.Claimed,
            "Modules/Messaging/Modules.Messaging.Infrastructure/Media/MediaCopyWorker.cs:76 — FOR UPDATE SKIP LOCKED y next_attempt_at como lease"),
        new("Modules.Messaging.Infrastructure.Campaigns.CampaignDispatchWorker", WorkerClassification.Claimed,
            "Modules/Messaging/Modules.Messaging.Infrastructure/Campaigns/CampaignDispatchStore.cs:171-173 — FOR UPDATE OF r SKIP LOCKED y locked_until"),
        new("Modules.Messaging.Infrastructure.Webhook.WebhookDeliveryWorker", WorkerClassification.Claimed,
            "Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookDeliveryWorker.cs:178-180 — reclamo por claimed_until"),
        new("Modules.Shopify.Infrastructure.Workers.ShopifyWebhookDeliveryWorker", WorkerClassification.Claimed,
            "Modules/Shopify/Modules.Shopify.Infrastructure/Persistence/WebhookDeliveries.cs:53 — reclamo por lease_until"),
        new("Modules.Automations.Infrastructure.Engine.AutomationInboxWorker", WorkerClassification.Claimed,
            "Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/AutomationInbox.cs:41 — FOR UPDATE SKIP LOCKED con lease"),
        new("Modules.Identity.Infrastructure.Messaging.SessionRevocationWorker", WorkerClassification.Claimed,
            "Modules/Identity/Modules.Identity.Infrastructure/Persistence/IdentityInboxClaims.cs:38-43 — ClaimedOutboxConsumer, reclamo en el inbox con claimed_until"),
        new("Modules.Identity.Infrastructure.Messaging.OrphanUserCleanupWorker", WorkerClassification.Claimed,
            "Modules/Identity/Modules.Identity.Infrastructure/Persistence/IdentityInboxClaims.cs:38-43 — ClaimedOutboxConsumer; el borrado además bajo pg_advisory_xact_lock (OrphanUserCleanupWorker.cs:191)"),
        new("Modules.Pipeline.Infrastructure.Messaging.QuotationEventsWorker", WorkerClassification.Claimed,
            "Modules/Pipeline/Modules.Pipeline.Infrastructure/Persistence/PipelineInbox.cs:50-55 — ClaimedOutboxConsumer, reclamo en el inbox con claimed_until"),
        new("Modules.Purchases.Infrastructure.Messaging.WholesaleOrderEventsWorker", WorkerClassification.Claimed,
            "Modules/Purchases/Modules.Purchases.Infrastructure/Persistence/PurchasesInbox.cs:49-54 — ClaimedOutboxConsumer, reclamo en el inbox con claimed_until"),
        new("Modules.Wholesale.Infrastructure.Messaging.InvitationNoticeWorker", WorkerClassification.Claimed,
            "Modules/Wholesale/Modules.Wholesale.Infrastructure/Persistence/WholesaleInbox.cs:37-42 — ClaimedOutboxConsumer, reclamo en el inbox con claimed_until"),
        new("Modules.Shopify.Infrastructure.Workers.CitySynonymEventsWorker", WorkerClassification.Claimed,
            "Modules/Shopify/Modules.Shopify.Infrastructure/Persistence/ShopifyInbox.cs:49-54 — ShopifyClaimedOutboxConsumer, reclamo en el inbox con claimed_until"),
        new("Modules.Shopify.Infrastructure.Workers.QuotationOrderEventsWorker", WorkerClassification.Claimed,
            "Modules/Shopify/Modules.Shopify.Infrastructure/Persistence/ShopifyInbox.cs:49-54 — ShopifyClaimedOutboxConsumer, reclamo en el inbox con claimed_until; dos réplicas encolando a la vez sobre la misma fila chocan con IX_order_pushes_mirror_pending y se reintenta"),
        new("Modules.Messaging.Infrastructure.CustomerDeletion.CustomerMediaPurgeWorker", WorkerClassification.Claimed,
            "Modules/Messaging/Modules.Messaging.Infrastructure/CustomerDeletion/CustomerMediaPurgeProcessor.cs:34 — ClaimedOutboxConsumer (MessagingInbox.cs:50)"),
        new("Modules.Storage.Infrastructure.HrDocuments.HrDocumentReleaseWorker", WorkerClassification.Claimed,
            "Modules/Storage/Modules.Storage.Infrastructure/HrDocuments/HrDocumentReleaseProcessor.cs:34 — ClaimedOutboxConsumer (StorageInboxClaims.cs:20-28); cada archivo bajo FOR UPDATE (HrDocumentPurgeStore.cs:43)"),
        new("Modules.Messaging.Infrastructure.Coexistence.CoexistenceEventsWorker", WorkerClassification.Claimed,
            "Modules/Messaging/Modules.Messaging.Infrastructure/HistorySync/CoexistenceEventsProcessor.cs:28 — ClaimedOutboxConsumer; el alta además con ON CONFLICT (connection_id) DO NOTHING (:61)"),
        new("Modules.Messaging.Infrastructure.Coexistence.HistorySyncRequestWorker", WorkerClassification.Claimed,
            "Modules/Messaging/Modules.Messaging.Infrastructure/HistorySync/HistorySyncRequestProcessor.cs:96-102 — FOR UPDATE SKIP LOCKED y claimed_until de 2 min"),
        new("Modules.Storage.Infrastructure.UnreferencedFiles.UnreferencedFileSweepWorker", WorkerClassification.Claimed,
            "Modules/Storage/Modules.Storage.Infrastructure/UnreferencedFiles/UnreferencedFileCandidates.cs:71-83 — cada archivo se reclama con FOR UPDATE SKIP LOCKED y se relee vivo bajo el candado; el segundo pod lo salta o lo ve Deleted y no audita dos veces"),
        new("Modules.Messaging.Infrastructure.ScheduledMessages.ScheduledMessageWorker", WorkerClassification.Claimed,
            "Modules/Messaging/Modules.Messaging.Infrastructure/ScheduledMessages/ScheduledMessageStore.cs:25-28 — FOR UPDATE SKIP LOCKED y locked_until"),
        new("Modules.ServiceContracts.Infrastructure.Reminders.ServiceContractsReminderWorker", WorkerClassification.Claimed,
            "Modules/ServiceContracts/Modules.ServiceContracts.Infrastructure/Reminders/TenantRunStore.cs:26 — reclamo por (tenant, día) con ON CONFLICT … DO UPDATE y claimed_until"),
        new("Modules.Hr.Infrastructure.Reminders.HrMedicalExamsReminderWorker", WorkerClassification.Claimed,
            "Modules/Hr/Modules.Hr.Infrastructure/Reminders/TenantRunStore.cs:19-30 — reclamo por (tenant, día) con ON CONFLICT … DO UPDATE y claimed_until; el digest de cada encargado se reclama aparte en DailyDigestStore.cs"),
        new("Modules.Scheduling.Infrastructure.Reminders.AppointmentReminderWorker", WorkerClassification.Claimed,
            "Modules/Scheduling/Modules.Scheduling.Infrastructure/Reminders/AppointmentReminderProcessor.cs (ClaimAsync) — UPDATE … RETURNING sobre scheduling.appointments con reminder_claimed_until por intento y reminder_attempts; sólo el ganador del reclamo envía, y el envío es idempotente por el clientId derivado de la cita (appointment-reminder:{id}), así que un reintento tras un envío que sí salió devuelve la misma fila de Mensajería"),
        new("Modules.Loans.Infrastructure.Generation.LoansGenerationWorker", WorkerClassification.Claimed,
            "Modules/Loans/Modules.Loans.Infrastructure/Generation/TenantRunStore.cs:24-27 — reclamo por (tenant, día) con claimed_until"),
        new("Modules.Buyers.Infrastructure.Signup.BuyerSignupRequestedWorker", WorkerClassification.Claimed,
            "Modules/Buyers/Modules.Buyers.Infrastructure/Signup/BuyersInboxClaims.cs:27-31 — reclamo en el inbox con claimed_until"),
        new("Modules.Shopify.Infrastructure.Workers.ShopifyCityCorrectionWorker", WorkerClassification.Claimed,
            "Modules/Shopify/Modules.Shopify.Infrastructure/Persistence/ShopifyCityCorrectionRepositories.cs:135 — FOR UPDATE SKIP LOCKED con lease por intento"),
        new("Modules.Shopify.Infrastructure.Workers.ShopifyReconcileWorker", WorkerClassification.Claimed,
            "Modules/Shopify/Modules.Shopify.Infrastructure/Persistence/ShopifyRepositories.cs:199-202 — lease por tienda (reconcile_lease_until)"),
        new("Modules.Shopify.Infrastructure.Workers.ShopifyOrderPushWorker", WorkerClassification.Claimed,
            "Modules/Shopify/Modules.Shopify.Infrastructure/Persistence/ShopifyOrderPushQueue.cs:36-39 — FOR UPDATE SKIP LOCKED con lease_until de 2 min por envío"),
        new("Modules.Siigo.Infrastructure.Batches.SiigoInvoiceBatchWorker", WorkerClassification.Claimed,
            "Modules/Siigo/Modules.Siigo.Infrastructure/Batches/SiigoInvoiceBatchWorker.cs (ClaimAsync) — UPDATE … WHERE id = (SELECT … FOR UPDATE SKIP LOCKED) con lease_until de 5 min por pedido; un pedido por réplica a la vez, y repetirlo no duplica (candado por pedido y clave de idempotencia del handler)"),
        new("Modules.Shopify.Infrastructure.Workers.ShopifyProductImportWorker", WorkerClassification.Claimed,
            "Modules/Shopify/Modules.Shopify.Infrastructure/Persistence/ShopifyProductImportQueue.cs:27-32 — FOR UPDATE SKIP LOCKED con lease_until de 2 min por importación"),
        new("Modules.Ai.Infrastructure.AiInboxWorker", WorkerClassification.Claimed,
            "Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/AutomationInbox.cs:45 — FOR UPDATE SKIP LOCKED con lease, filas bot_kind = 2 (adaptador Bootstrapper/AiConversations.cs:194); los barridos cierran cada sesión bajo pg_try_advisory_xact_lock(ConversationBotLock) y FOR UPDATE de la sesión (Modules/Ai/Modules.Ai.Application/AiSessionSweeper.cs:60-66), así que el segundo pod no encuentra nada; el de delegaciones huérfanas (spec 2026-11-17 §6.5) es una sola sentencia: UPDATE con FOR UPDATE SKIP LOCKED y status = 'Live', que en la misma encola la señal ReleaseRun de cada interrumpida (Bootstrapper/AiDelegationSweep.cs:26-55); la retención del copiloto (CopilotRetentionSweeper) borra cada hilo con DELETE … RETURNING y FOR UPDATE SKIP LOCKED y audita sólo lo que borró, y el registro de preguntas con un DELETE por corte (Modules/Ai/Modules.Ai.Infrastructure/Persistence/CopilotRepository.cs, DeleteThreadAsync y DeleteQuestionsBeforeAsync)"),
        new("Modules.Ai.Infrastructure.AiModelWorker", WorkerClassification.Claimed,
            "Modules/Ai/Modules.Ai.Infrastructure/Persistence/AiRunPersistence.cs:64-75 — FOR UPDATE SKIP LOCKED y lease_until (LeaseMinutes, mayor que el tope de 120 s del trabajo)"),
        new("Modules.Wms.Infrastructure.Workers.BalanceChangePublisher", WorkerClassification.Claimed,
            "Modules/Wms/Modules.Wms.Infrastructure/Persistence/BalanceChangeQueue.cs:39-56 — FOR UPDATE SKIP LOCKED y DELETE … RETURNING en la misma transacción que escribe el evento en el outbox"),
        new("Modules.Wms.Infrastructure.Workers.TaskLeaseSweeper", WorkerClassification.Claimed,
            "Modules/Wms/Modules.Wms.Infrastructure/Persistence/WarehouseTaskRepository.cs:201-216 (LockExpiredAsync) — toma las tareas vencidas con FOR UPDATE SKIP LOCKED en la transacción de la pasada; dos réplicas reparten filas distintas y el dominio (ExpireLease) no hace nada si el lease ya no está vencido"),
        // Un solo hosted drena en secuencia los trece consumidores de correo (spec 2026-11-13, D-7); cada uno reclama igual que antes.
        new("Modules.Notifications.Infrastructure.Messaging.NotificationsDeliveryWorker", WorkerClassification.Claimed, OutboxDelivery),
        new("Modules.Notifications.Infrastructure.TaskNotices.TaskNoticeEscalationWorker", WorkerClassification.Claimed,
            "Modules/Notifications/Modules.Notifications.Infrastructure/TaskNotices/TaskNoticeEscalationProcessor.cs:89-92 — FOR UPDATE SKIP LOCKED + claimed_until de 2 min; el WhatsApp es idempotente por ClientId (el id de la escalada)"),
        new("Modules.Notifications.Infrastructure.HrBirthdays.HrBirthdayDeliveryWorker", WorkerClassification.Claimed,
            "Modules/Notifications/Modules.Notifications.Infrastructure/HrBirthdays/HrBirthdayDeliveryProcessor.cs:88-92 — FOR UPDATE SKIP LOCKED + claimed_until de 2 min; el WhatsApp es idempotente por ClientId (el id de la entrega)"),

        // --- AdvisoryLock. ---
        new("Modules.Integrations.Infrastructure.Shopify.ShopifyTokenRefreshWorker", WorkerClassification.AdvisoryLock,
            "Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/IntegrationsUnitOfWork.cs:33 — pg_try_advisory_xact_lock(ShopifyTokenLock.Namespace, hashtext(id)) por conexión"),
        new("Modules.Integrations.Infrastructure.Meta.InstagramTokenRefreshWorker", WorkerClassification.AdvisoryLock,
            "Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/IntegrationsUnitOfWork.cs:43 — pg_try_advisory_xact_lock(InstagramTokenLock.Namespace, hashtext(id)) por conexión (owner, 2026-11-14)"),
        new("Modules.Wms.Infrastructure.Workers.BalanceReconciliationWorker", WorkerClassification.AdvisoryLock,
            "Modules/Wms/Modules.Wms.Infrastructure/Persistence/BalanceReconciliation.cs:73-79 — pg_advisory_xact_lock(WmsLockNamespace, hashtext('balance-check:{bodega}')) en su propia sentencia antes de cada tanda y en la misma transacción READ COMMITTED que el INSERT con NOT EXISTS (:128): el segundo pod espera, ve la fila ya confirmada y no la duplica (los dos recorren y miden; no corrigen saldos). Lo amarra BalanceReconciliationTests.TwoConcurrentReconciliationsRecordOneCheckPerDriftKey"),

        // --- Idempotent: el segundo pod no encuentra nada que hacer. ---
        new("Modules.Integrations.Infrastructure.SecretProtection.ConnectionSecretRekeyWorker", WorkerClassification.Idempotent,
            "Modules/Integrations/Modules.Integrations.Infrastructure/SecretProtection/ConnectionSecretRekeyWorker.cs:249,297 — recifrar dos veces da lo mismo; el lote que choca por versión se salta"),
        new("Modules.Customers.Infrastructure.Phones.CustomerPhoneBackfillWorker", WorkerClassification.Idempotent,
            "Modules/Customers/Modules.Customers.Infrastructure/Phones/CustomerPhoneBackfillWorker.cs:11-13 — recalcula phone_e164 donde es NULL; el resultado es determinista"),
        new("Modules.Storage.Infrastructure.ObjectStorage.StagingCleanupWorker", WorkerClassification.Idempotent,
            "Modules/Storage/Modules.Storage.Infrastructure/ObjectStorage/StagingCleanupProcessor.cs:60-76 — borrado por corte de edad; borrar en R2 lo que ya no está no falla"),
        new("Modules.Storage.Infrastructure.PaymentProofs.PaymentProofOrphanCleanupWorker", WorkerClassification.Idempotent,
            "Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofOrphanCleanupProcessor.cs:105-143 — borrado de huérfanos por corte de edad, cada 24 h"),
        new("Modules.Messaging.Infrastructure.Webhook.WebhookPurgeWorker", WorkerClassification.Idempotent,
            "Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookPurgeWorker.cs:62 — DELETE por corte"),
        new("Modules.Buyers.Infrastructure.BuyerHousekeepingWorker", WorkerClassification.Idempotent,
            "Modules/Buyers/Modules.Buyers.Infrastructure/BuyerHousekeepingWorker.cs:134-180 — DELETE por corte"),
        new("Modules.Integrations.Infrastructure.Shopify.ShopifyInstallSweeper", WorkerClassification.Idempotent,
            "Modules/Integrations/Modules.Integrations.Infrastructure/Shopify/ShopifyInstallSweeper.cs:86 — DELETE por corte"),
        new("Modules.Notifications.Infrastructure.InApp.InAppNotificationRetentionWorker", WorkerClassification.Idempotent,
            "Modules/Notifications/Modules.Notifications.Infrastructure/InApp/InAppNotificationRetentionWorker.cs:68-87 (PurgeAsync) — DELETE por corte en lotes y UPDATE que sólo toca las filas que aún guardan el enlace; repetirlos no encuentra nada"),
        new("Modules.ApiAccess.Infrastructure.ApiKeyRequestPurgeWorker", WorkerClassification.Idempotent,
            "Modules/ApiAccess/Modules.ApiAccess.Infrastructure/ApiKeyRequestPurgeWorker.cs:61 — DELETE por corte"),
        new("Modules.Support.Infrastructure.SupportUnassignedAlertWorker", WorkerClassification.Idempotent,
            "Modules/Support/Modules.Support.Infrastructure/Persistence/SupportUnassignedAlertStore.cs:75-94 (TryClaimAsync) y :26-36 (HealAsync) — el reclamo es un INSERT … ON CONFLICT (ticket_id, unassigned_since) DO NOTHING en la misma transacción que el evento del outbox: dos réplicas sobre el mismo ticket y el mismo tramo, la segunda inserta cero filas y no publica; la sanación de unassigned_since (HealAsync) son dos UPDATE idempotentes (sella las sin asignar con null, limpia las asignadas con marca) que no tocan version"),
        new("Modules.Support.Infrastructure.SupportThreadFollowWorker", WorkerClassification.Idempotent,
            "Modules/Support/Modules.Support.Infrastructure/SupportThreadFollowWorker.cs:9-13 — idempotente por índices (IX_ticket_messages_ticket_message, PK_unclassified_messages); el segundo pod choca y no duplica"),
        new("Bootstrapper.Valkey.LegalParameters.LegalParameterCacheStartupBump", WorkerClassification.Idempotent,
            "Bootstrapper/Valkey/LegalParameters/LegalParameterCacheStartupBump.cs — sube qep:legalparams:ver una vez al arrancar, sólo con Valkey:Areas:LegalParameters; cada pod que arranca invalida una vez más y el INCR no daña a otro pod"),

        new("Modules.Wms.Infrastructure.Workers.WmsHousekeepingWorker", WorkerClassification.Idempotent,
            "Modules/Wms/Modules.Wms.Infrastructure/Workers/WmsHousekeepingWorker.cs:74-89 — DELETE por corte de recibos y previews vencidos, y de saldos en cero"),

        // --- Noisy: el segundo choca con el inbox o con Version y sólo deja log o un tick perdido. ---
        new("Modules.Wms.Infrastructure.Workers.PartitionMaintenanceWorker", WorkerClassification.Noisy,
            "Modules/Wms/Modules.Wms.Infrastructure/Persistence/InventoryLedgerSql.cs:47-58 — ensure_movement_partition crea sólo si falta; el pod que pierde la carrera falla el CREATE TABLE, lo deja en log (Workers/PartitionMaintenanceWorker.cs) y ve la partición del otro"),
        new("Modules.Support.Infrastructure.SupportAutoCloseWorker", WorkerClassification.Noisy,
            "Modules/Support/Modules.Support.Application/AutoClose/SupportAutoCloser.cs — relee el ticket y cierra con su Version (token de concurrencia): dos réplicas en el mismo tick, la segunda pierde con RequestConcurrencyException y salta el ticket; TicketClosed va en la misma transacción"),
        new("Modules.Audit.Infrastructure.Messaging.AuditProjectionWorker", WorkerClassification.Noisy,
            "Modules/Audit/Modules.Audit.Infrastructure/Messaging/AuditProjectionWorker.cs:59 — anti-join contra el inbox sin reclamar; el que pierde choca con la clave del inbox"),
        new("Modules.Storage.Infrastructure.PaymentProofs.PaymentProofMoveWorker", WorkerClassification.Noisy,
            "Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofMoveProcessor.cs:19 — anti-join contra el inbox sin reclamar; el que pierde choca con la clave del inbox"),
        new("Modules.Storage.Infrastructure.ProductImages.ProductImageReleaseWorker", WorkerClassification.Noisy,
            "Modules/Storage/Modules.Storage.Infrastructure/ProductImages/ProductImageReleaseProcessor.cs:19 — anti-join contra el inbox sin reclamar; el que pierde choca con la clave del inbox"),
        new("Modules.Storage.Infrastructure.TemplateImages.TemplateHeaderImageReleaseWorker", WorkerClassification.Noisy,
            "Modules/Storage/Modules.Storage.Infrastructure/TemplateImages/TemplateHeaderImageReleaseProcessor.cs:68 — anti-join contra el inbox sin reclamar; el que pierde choca con la clave del inbox"),
        new("Modules.Storage.Infrastructure.ChatbotMedia.ChatbotMediaReleaseWorker", WorkerClassification.Noisy,
            "Modules/Storage/Modules.Storage.Infrastructure/ChatbotMedia/ChatbotMediaReleaseProcessor.cs:54 — anti-join contra el inbox sin reclamar; cada archivo se reclama con FOR UPDATE SKIP LOCKED (UnreferencedFileReleaser) y el que pierde choca con la clave del inbox"),
        new("Modules.Quotations.Infrastructure.Expiration.QuotationExpirationWorker", WorkerClassification.Noisy,
            "Modules/Quotations/Modules.Quotations.Infrastructure/Expiration/QuotationExpirationProcessor.cs:56-88 — barrido en los dos pods; Version es token de concurrencia y el segundo choca sin duplicar historial"),

        // --- PerPod: tiene que correr en cada pod y no reparte trabajo. ---
        new("Modules.Messaging.Infrastructure.Realtime.MessagingEventsListener", WorkerClassification.PerPod,
            "Modules/Messaging/Modules.Messaging.Infrastructure/Realtime/MessagingEventsListener.cs:83 — LISTEN por pod para los clientes conectados a ese pod"),
        new("Bootstrapper.Observability.QueueSamplerWorker", WorkerClassification.PerPod,
            "Bootstrapper/Observability/QueueSamplerWorker.cs:48-54 — sólo lee la base y publica el gauge del pod; con Valkey:Areas:Doorbells muestrea sólo el líder (Bootstrapper/Valkey/QueueSamplerLeadership.cs); el dashboard agrega con max/avg"),
        new("Bootstrapper.ApiKeys.ExternalApiCallLogWriter", WorkerClassification.PerPod,
            "Bootstrapper/ApiKeys/ExternalApiCallLogWriter.cs:26 — drena el canal en memoria de su propio pod"),
        new("Modules.Tenancy.Infrastructure.OperatorTenantStartupWarning", WorkerClassification.PerPod,
            "Modules/Tenancy/Modules.Tenancy.Infrastructure/OperatorTenantStartupWarning.cs:29-37 — sólo advierte en el log al arrancar"),
        new("Bootstrapper.Valkey.ValkeyConnection", WorkerClassification.PerPod,
            "Bootstrapper/Valkey/ValkeyConnection.cs:19-35 — el multiplexer de Valkey de cada pod"),
        new("Bootstrapper.Valkey.Presence.PresenceHeartbeatWorker", WorkerClassification.PerPod,
            "Bootstrapper/Valkey/Presence/PresenceHeartbeatWorker.cs:33-56 — late los conectados a su pod (ZADD idempotente); sólo con Valkey:Areas:Presence"),
        new("Bootstrapper.Valkey.Presence.AppPresenceHeartbeatWorker", WorkerClassification.PerPod,
            "Bootstrapper/Valkey/Presence/AppPresenceHeartbeatWorker.cs:33-62 — late los miembros conectados al AppHub de su pod (ZADD idempotente en qep:rt:online:{tenant}); sólo con Valkey:Areas:Presence"),
        new("Bootstrapper.Realtime.RealtimeEventsListener", WorkerClassification.PerPod,
            "Bootstrapper/Realtime/RealtimeEventsListener.cs:107 — LISTEN qep_realtime por pod para las conexiones del hub global de ese pod (plan 2026-11-15, N7)"),
        new("Bootstrapper.Realtime.RealtimePresenceSweeper", WorkerClassification.PerPod,
            "Bootstrapper/Realtime/RealtimePresenceSweeper.cs:23 — relee cada 5 s la presencia de los grupos que este pod entregó y la reentrega sólo a sus conexiones, sin NOTIFY (plan 2026-11-15, N10)"),

        // --- OffInProduction. ---
        new("Bootstrapper.Seeding.ExportLoadSeedWorker", WorkerClassification.OffInProduction,
            "Bootstrapper/Seeding/ExportLoadSeedWorker.cs:48-51 — no hace nada con Seed:ExportLoad:Quotations <= 0, y k8s/prod-configMap.yaml:158 lo fija en \"0\""),
    ];

    public static readonly IReadOnlyList<StateEntry> State =
    [
        // Valkey base (spec 2026-11-13, D-2 y D-3) ya está en esta rama: 9c0cdfa1 y e2a946a8. Compartido sólo con el área
        // prendida en producción, que es prerrequisito del Bloque C.
        new("Modules.Messaging.Application.MessagingPresence", StateClassification.SharedWithLocalFallback,
            "Bootstrapper/Valkey/ValkeyServiceCollectionExtensions.cs:60-74 — con Valkey:Areas:Presence el hub escribe en qep:presence:{tenantId} y esto queda de respaldo local"),
        new("Bootstrapper.ApiKeys.ApiKeyRateLimiter", StateClassification.SharedWithLocalFallback,
            "Bootstrapper/Valkey/RateLimiting/SharedRateLimitPartitions.cs:33-45 — con Valkey:Areas:RateLimiting la ventana se cuenta en Valkey y cae a memoria si falla"),
        new("Bootstrapper.Realtime.LocalRealtimePresence", StateClassification.SharedWithLocalFallback,
            "Bootstrapper/Valkey/ValkeyServiceCollectionExtensions.cs:147-165 — con Valkey:Areas:Presence el hub global escribe «está respondiendo» en qep:rt:presence:{tenantId}:{topic} y esto queda de respaldo local"),
        // Las cachés del pod que rigen con el área apagada (menos viajes a la base por request, 2026-11-17): con dos pods, un bump
        // o una revocación en uno no invalida al otro hasta que la entrada venza (60 s / 15 min). Con el área prendida se
        // registran los adaptadores de Valkey y éstas no se registran.
        new("Bootstrapper.Valkey.Permissions.MemoryPermissionCache", StateClassification.SharedWithLocalFallback,
            "Bootstrapper/Valkey/ValkeyServiceCollectionExtensions.cs:126-135 — con Valkey:Areas:Permissions la entrada y la versión de acceso viven en qep:perm:* y qep:tenantver:*; apagada, en este MemoryCache y su contador por tenant"),
        new("Bootstrapper.Valkey.Sessions.MemorySessionCache", StateClassification.SharedWithLocalFallback,
            "Bootstrapper/Valkey/ValkeyServiceCollectionExtensions.cs:107-114 — con Valkey:Areas:Sessions la entrada y la marca de revocación viven en qep:sess:*; apagada, en este MemoryCache"),

        new("Bootstrapper.Valkey.LegalParameters.MemoryLegalParameterCache", StateClassification.PerPodByDesign,
            "Bootstrapper/Valkey/LegalParameters/LegalParameterCacheRegistration.cs:37-41 — con Valkey:Areas:LegalParameters la tabla vive en qep:legalparams:{ver}; apagada, copia del pod con TTL de 60 s (decisión del spec 2026-11-22, «Caché»): el pod que escribe se invalida al instante y los demás sirven la tabla vieja a lo sumo 60 s, así que el área no es prerrequisito de más de una réplica"),
        new("Bootstrapper.ApiKeys.ExternalApiCallLogChannel", StateClassification.PerPodByDesign,
            "Bootstrapper/ApiKeys/ExternalApiCallLogChannel.cs:21 — Channel.CreateBounded por pod; lo no escrito se pierde con SIGKILL, igual que con un pod"),
        new("Api.WebTelemetryMetrics", StateClassification.PerPodByDesign,
            "Api/WebTelemetryMetrics.cs:23 — HashSet de rutas que acota la cardinalidad del histograma a 256 (MaxRoutes) por pod; cada pod publica sus métricas"),
        new("Bootstrapper.Observability.QueueGauges", StateClassification.PerPodByDesign,
            "Bootstrapper/Observability/QueueGauges.cs:20 — la última lectura del pod; el dashboard agrega con max/avg"),
        new("Bootstrapper.Observability.WebhookArrivalGauge", StateClassification.PerPodByDesign,
            "Bootstrapper/Observability/WebhookArrivalGauge.cs:16 — la última lectura del pod (hora del último webhook); el dashboard agrega con max"),
        new("Bootstrapper.Valkey.ValkeyConnection", StateClassification.PerPodByDesign,
            "Bootstrapper/Valkey/ValkeyConnection.cs:31 — el multiplexer de cada pod"),
        new("Bootstrapper.Valkey.ValkeyQueueDoorbell", StateClassification.PerPodByDesign,
            "Bootstrapper/Valkey/ValkeyQueueDoorbell.cs:21 — los oyentes de los workers de este pod; el aviso viaja entre pods por pub/sub (qep:wake:{cola})"),
        new("Bootstrapper.Realtime.AppHubConnections", StateClassification.PerPodByDesign,
            "Bootstrapper/Realtime/AppHubConnections.cs:12 — las conexiones del hub global de este pod con sus tópicos; cada pod entrega sólo a las suyas y el aviso cruza pods por NOTIFY qep_realtime"),
        new("Bootstrapper.Realtime.AppHubDispatcher", StateClassification.PerPodByDesign,
            "Bootstrapper/Realtime/AppHubDispatcher.cs:16 — la última foto de presencia que este pod entregó a sus conexiones, para que su barrido note un vencimiento"),
        new("Bootstrapper.Realtime.AppHubMessagingAlertSink", StateClassification.PerPodByDesign,
            "Bootstrapper/Realtime/AppHubMessagingAlertSink.cs:19,50-55 — IMemoryCache de 30 s (DecisionTtl) con la decisión «módulo prendido y puede leer conversaciones» por tenant+miembro; el aviso lleva sólo ids, así que un cambio de permiso tarda hasta 30 s en llegar a la campana y no expone nada; el retraso ya existe con un pod"),
        new("Modules.Messaging.Infrastructure.Webhook.WebhookRouting", StateClassification.PerPodByDesign,
            "Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookRouting.cs:12 — IMemoryCache de 60 s; el retraso de un minuto ya existe con un pod"),
        new("Modules.Automations.Infrastructure.Engine.AutomationsModuleStatus", StateClassification.PerPodByDesign,
            "Modules/Automations/Modules.Automations.Infrastructure/Engine/AutomationsModuleStatus.cs:14 — IMemoryCache de 60 s; el retraso de un minuto ya existe con un pod"),
        new("Modules.Ai.Infrastructure.AiModuleStatus", StateClassification.PerPodByDesign,
            "Modules/Ai/Modules.Ai.Infrastructure/AiInboxWorker.cs:15-29 — IMemoryCache de 60 s, como AutomationsModuleStatus; el retraso de un minuto ya existe con un pod"),
        new("Modules.Ai.Infrastructure.AiInboxWorker", StateClassification.PerPodByDesign,
            "Modules/Ai/Modules.Ai.Infrastructure/AiInboxWorker.cs:43-44 — la hora del próximo barrido de cada pod; dos pods barren dos veces y el segundo no cierra nada (AiSessionSweeper.cs:60-66)"),
        new("Modules.Authorization.Application.ModuleEntitlementMask", StateClassification.PerPodByDesign,
            "Modules/Authorization/Modules.Authorization.Application/ModuleEntitlementMask.cs:17 — tabla inmutable armada de los permisos registrados; igual en cada pod"),
        new("Modules.Authorization.Application.RoleCatalog", StateClassification.PerPodByDesign,
            "Modules/Authorization/Modules.Authorization.Application/RoleCatalog.cs:72 — tabla inmutable armada del código; igual en cada pod"),
        new("Modules.Automations.Infrastructure.Seed.SystemTemplateCatalog", StateClassification.PerPodByDesign,
            "Modules/Automations/Modules.Automations.Infrastructure/Seed/SystemTemplateCatalog.cs:45 — plantillas embebidas leídas al arrancar; igual en cada pod"),
        new("Modules.Ai.Infrastructure.Seed.SystemAgentTemplateCatalog", StateClassification.PerPodByDesign,
            "Modules/Ai/Modules.Ai.Infrastructure/Seed/SystemAgentTemplateCatalog.cs:29 — plantillas embebidas leídas al arrancar; igual en cada pod"),
        new("Modules.Siigo.Infrastructure.Api.SiigoTokenCache", StateClassification.PerPodByDesign,
            "Modules/Siigo/Modules.Siigo.Infrastructure/Api/SiigoTokenCache.cs:17 — caché de token por pod; un token de más por réplica no tiene efecto (Siigo acepta varios vivos y un 401 lo renueva)"),
        new("Modules.Siigo.Infrastructure.Api.SiigoApiClient", StateClassification.PerPodByDesign,
            "Modules/Siigo/Modules.Siigo.Infrastructure/Api/SiigoApiClient.cs:21-25 — IMemoryCache de 5 min con las listas de Siigo (comprobantes, usuarios, impuestos…) por conexión; un pod puede mostrar una lista hasta 5 min más vieja que otro, y la creación de facturas y los clientes no se cachean"),
        new("Modules.Siigo.Infrastructure.Api.SiigoMetrics", StateClassification.PerPodByDesign,
            "Modules/Siigo/Modules.Siigo.Infrastructure/Api/SiigoMetrics.cs:27 — contadores de fallas por pod; Prometheus los suma entre réplicas por etiqueta (operation, reason)"),
        new("Modules.Integrations.Infrastructure.Meta.InstagramAppSettings", StateClassification.PerPodByDesign,
            "Modules/Integrations/Modules.Integrations.Infrastructure/Meta/MetaInstagramOptions.cs:88-93 — opciones leídas al construir; igual en cada pod"),
        new("Modules.Integrations.Infrastructure.Shopify.ShopifyAppSettings", StateClassification.PerPodByDesign,
            "Modules/Integrations/Modules.Integrations.Infrastructure/Shopify/ShopifyAppOptionsValidator.cs:82-87 — opciones leídas al construir; igual en cada pod"),
        new("Modules.Integrations.Infrastructure.Verification.ConnectionTesterRegistry", StateClassification.PerPodByDesign,
            "Modules/Integrations/Modules.Integrations.Infrastructure/Verification/ConnectionTesterRegistry.cs:10 — diccionario inmutable de los testers registrados"),
        new("Modules.Messaging.Infrastructure.Webhook.MarketingOptOutButtons", StateClassification.PerPodByDesign,
            "Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/MarketingOptOutButtons.cs:13 — textos de la configuración, inmutables; igual en cada pod"),
        new("Modules.Support.Infrastructure.Legal.CachingSupportHtmlSanitizer", StateClassification.PerPodByDesign,
            "Modules/Support/Modules.Support.Infrastructure/Legal/CachingSupportHtmlSanitizer.cs:24 — MemoryCache acotado por caracteres, con clave SHA-256 de la entrada de una función pura; igual en cada pod"),
        new("Modules.Wms.Infrastructure.WmsMetrics", StateClassification.PerPodByDesign,
            "Modules/Wms/Modules.Wms.Infrastructure/WmsMetrics.cs:15-17 — la última lectura de cada gauge en el pod, sin etiqueta de tenant; el dashboard agrega con max"),
        new("Modules.Wms.Infrastructure.Workers.BalanceReconciliationWorker", StateClassification.PerPodByDesign,
            "Modules/Wms/Modules.Wms.Infrastructure/Workers/BalanceReconciliationWorker.cs:21-22 — la marca de «hoy ya corrió» es de cada pod; lo que dos pods harían doble lo cubre su entrada AdvisoryLock de Workers"),
    ];

    /// <summary>
    /// El interruptor del ConfigMap de producción que hace compartido cada estado <c>SharedWithLocalFallback</c>. Con más
    /// de una réplica, <c>MoreThanOneReplicaRequiresNoUnsafeEntry</c> exige cada uno, y <c>Valkey__Enabled</c>, en
    /// <c>"true"</c>: apagado, el estado es por pod.
    /// </summary>
    // Las claves son las de ValkeyOptions (src/Bootstrapper/Valkey/ValkeyOptions.cs) escritas como variable de entorno, igual
    // que Seed__ExportLoad__Quotations en k8s/prod-configMap.yaml.
    public static readonly IReadOnlyDictionary<string, string> SharedStateSwitches = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Modules.Messaging.Application.MessagingPresence"] = "Valkey__Areas__Presence",
        ["Bootstrapper.ApiKeys.ApiKeyRateLimiter"] = "Valkey__Areas__RateLimiting",
        ["Bootstrapper.Realtime.LocalRealtimePresence"] = "Valkey__Areas__Presence",
        ["Bootstrapper.Valkey.Permissions.MemoryPermissionCache"] = "Valkey__Areas__Permissions",
        ["Bootstrapper.Valkey.Sessions.MemorySessionCache"] = "Valkey__Areas__Sessions",
    };

    /// <summary>
    /// Singletons con fábrica tipada a una interfaz: el concreto que devuelve la fábrica, para que el escaneo de estado lo
    /// mire. Si el concreto tiene estado, además va en <see cref="State"/>.
    /// </summary>
    public static readonly IReadOnlyList<FactoryEntry> InterfaceFactories =
    [
        new("Modules.Messaging.Application.IMessagingPresence", "Modules.Messaging.Application.MessagingPresence",
            "Bootstrapper/Valkey/ValkeyServiceCollectionExtensions.cs:65 — reenvía al singleton MessagingPresence (presencia apagada)"),
        new("Bootstrapper.Realtime.IRealtimePresence", "Bootstrapper.Realtime.LocalRealtimePresence",
            "Bootstrapper/Valkey/ValkeyServiceCollectionExtensions.cs:157 — reenvía al singleton LocalRealtimePresence (presencia apagada)"),
        new("Bootstrapper.Realtime.IAppMemberPresence", "Bootstrapper.Realtime.LocalAppMemberPresence",
            "Bootstrapper/Valkey/ValkeyServiceCollectionExtensions.cs:160 — reenvía al singleton LocalAppMemberPresence (presencia apagada), sin estado propio: lee AppHubConnections"),
        new("Modules.Messaging.Infrastructure.CustomerDeletion.ICustomerMediaPurgeProcessor",
            "Modules.Messaging.Infrastructure.CustomerDeletion.CustomerMediaPurgeProcessor",
            "Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs:147 — reenvía al singleton concreto"),
        new("Modules.Storage.Infrastructure.HrDocuments.IHrDocumentReleaseProcessor",
            "Modules.Storage.Infrastructure.HrDocuments.HrDocumentReleaseProcessor",
            "Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs:93 — reenvía al singleton concreto"),
        new("Modules.Notifications.Application.IEmailChannel", "Modules.Notifications.Infrastructure.Channels.InfobipEmailChannel",
            "Modules/Notifications/Modules.Notifications.Infrastructure/NotificationsInfrastructureExtensions.cs:71 — sólo con Notifications:EmailProvider = infobip"),
        new("BuildingBlocks.Application.Realtime.IRealtimePublisher", "Bootstrapper.Realtime.PgRealtimePublisher",
            "Bootstrapper/Realtime/AppRealtimeExtensions.cs:23 — reenvía al singleton PgRealtimePublisher, sin estado (abre una conexión del pool por aviso)"),
        new("BuildingBlocks.Application.Realtime.IRealtimeMemberPublisher", "Bootstrapper.Realtime.PgRealtimePublisher",
            "Bootstrapper/Realtime/AppRealtimeExtensions.cs:24 — reenvía al mismo singleton PgRealtimePublisher, sin estado"),
        new("Modules.Quotations.Application.IQuotationPdfRenderer", "Modules.Quotations.Infrastructure.Pdf.QCodePdfRenderer",
            "Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs:95"),
        new("Modules.Quotations.Application.IZenviaWhatsAppSender", "Modules.Quotations.Infrastructure.Whatsapp.ZenviaWhatsAppSender",
            "Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs:113"),
        new("Modules.Support.Application.ISupportHtmlSanitizer", "Modules.Support.Infrastructure.Legal.CachingSupportHtmlSanitizer",
            "Modules/Support/Modules.Support.Infrastructure/SupportInfrastructureExtensions.cs:48 — envuelve a HtmlSupportSanitizer, sin estado"),
        new("Modules.Support.Application.ISupportFormalReplyRenderer", "Modules.Support.Infrastructure.Pdf.SupportPdfRenderer",
            "Modules/Support/Modules.Support.Infrastructure/SupportInfrastructureExtensions.cs:62 — HttpClient propio y opciones, sin estado"),
        new("Modules.Purchases.Application.IPurchaseOrderPdfRenderer", "Modules.Purchases.Infrastructure.Pdf.PurchaseOrderPdfRenderer",
            "Modules/Purchases/Modules.Purchases.Infrastructure/PurchasesInfrastructureExtensions.cs:38 — HttpClient propio y opciones, sin estado"),
        new("Modules.Storage.Application.IFileScanner", "Modules.Storage.Infrastructure.Scanning.ClamAvFileScanner",
            "Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs:55-60 — con Storage:ClamAv:Enabled"),
        new("Modules.Storage.Application.IFileScanner", "Modules.Storage.Infrastructure.Scanning.NoopFileScanner",
            "Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs:55-60 — sin ClamAV"),
    ];

    private const string OutboxDelivery =
        "Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/OutboxDeliveryWorker.cs:44 — reclamo en el inbox con lease de 2 min (Persistence/InboxClaims.cs:31-35)";
}

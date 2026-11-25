using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.ServiceContracts.Application;
using Modules.ServiceContracts.Domain;
using Modules.ServiceContracts.Infrastructure.Persistence;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.ServiceContracts.Infrastructure.Reminders;

/// <summary>
/// La corrida diaria de un tenant, spec §3.3 pasos 2 a 6 y en ese orden: generar (para que el cobro exista cuando toque su
/// <c>before_5</c>), el mensaje del día a cada cliente y el email diario a cada encargado, con sus cobros de interés y las
/// revisiones de precio abiertas (spec reajuste §6.6 y §9). Un error en un servicio, un cliente o
/// un email se registra y no frena a los demás (precedente <c>OutboxDeliveryWorker.DrainAsync</c>); el tracker se limpia después
/// de cada uno para que una entidad que no se pudo guardar no vuelva a intentarse en el <c>SaveChanges</c> del siguiente.
/// </summary>
internal sealed partial class ServiceContractsReminderProcessor(
    ServiceContractsDbContext dbContext,
    IServiceContractRepository services,
    IChargeRepository charges,
    IServicePriceScheduleRepository schedules,
    IServicePriceChangeRepository priceChanges,
    ICustomerBillingProfileRepository profiles,
    IServiceContractsSettingsRepository settingsRepository,
    IServiceContractsCustomerLookup customers,
    IServiceContractsMemberDirectory members,
    IServiceContractsCustomerEmail customerEmail,
    IServiceContractsStaffEmail staffEmail,
    IServiceContractsWhatsAppOutbound whatsApp,
    IServiceContractsConsentGate consentGate,
    ITenantClock tenantClock,
    ITenantDirectory tenantDirectory,
    ITenantModules tenantModules,
    TenantRunStore runs,
    CustomerMessageStore messages,
    DailyDigestStore digests,
    OverdueDigestEventPublisher digestEvents,
    PriceReviewReader reviewReader,
    IIpcRateRepository ipcRates,
    PriceReviewDigestEventPublisher reviewEvents,
    IOptions<ServiceContractsOptions> options,
    ILogger<ServiceContractsReminderProcessor> logger) : IServiceContractsReminderProcessor
{
    private const string FailedStatus = nameof(MessageDeliveryStatus.Failed);

    [LoggerMessage(Level = LogLevel.Error, Message = "Billing charge generation failed for service {ServiceId} of tenant {TenantId}.")]
    private static partial void LogGenerationFailed(ILogger logger, Exception exception, Guid serviceId, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Billing reminder failed for customer {CustomerId} of tenant {TenantId}.")]
    private static partial void LogCustomerFailed(ILogger logger, Exception exception, Guid customerId, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Billing reminder send threw for customer {CustomerId} of tenant {TenantId}.")]
    private static partial void LogSendThrew(ILogger logger, Exception exception, Guid customerId, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Billing daily digest failed for member {MemberId} of tenant {TenantId}.")]
    private static partial void LogDigestFailed(ILogger logger, Exception exception, Guid memberId, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Billing price reviews could not be loaded for the daily digest of tenant {TenantId}.")]
    private static partial void LogReviewsFailed(ILogger logger, Exception exception, Guid tenantId);

    public async Task ProcessTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        if (calendar.ToLocal(calendar.UtcNow).Hour < options.Value.SendHourLocal)
        {
            return;
        }

        // Un tenant suspendido (o en cualquier estado que no sea Active) no recibe servicio: tampoco se cobra en su nombre. null es
        // el tenant del stub, sin fila (ITenantDirectory.GetStatusAsync).
        if (await tenantDirectory.GetStatusAsync(new TenantId(tenantId), cancellationToken) != TenantStatus.Active)
        {
            return;
        }

        // El módulo puede apagarse con servicios ya creados: sin él no se genera ni se avisa nada. null es el tenant del stub.
        var modules = await tenantModules.FindAsync(tenantId, cancellationToken);
        if (modules is null || !modules.IsEnabled(TenantModuleKeys.ServiceContracts))
        {
            return;
        }

        var today = calendar.Today;
        var now = calendar.UtcNow;
        if (!await runs.TryClaimAsync(tenantId, today, now, cancellationToken))
        {
            return;
        }

        var run = await OpenRunAsync(tenantId, calendar, cancellationToken);
        await GenerateAsync(run, cancellationToken);
        await MessageCustomersAsync(run, cancellationToken);
        await SendDigestsAsync(run, cancellationToken);

        // Un Sending vencido que los pasos de arriba no retomaron ya no tiene quién lo retome (su cobro se pagó, su encargado ya
        // no recibe el digest, o es de un día que no terminó): se cierra como send_failed en vez de quedar colgado.
        await messages.FailStaleAsync(tenantId, today, now, cancellationToken);
        await digests.FailStaleAsync(tenantId, today, now, cancellationToken);

        // La hora de cierre, no la de inicio: la corrida puede llevar minutos enviando.
        var finishedAt = (await tenantClock.GetAsync(tenantId, cancellationToken)).UtcNow;
        await runs.TryFinishAsync(tenantId, today, finishedAt, cancellationToken);
    }

    private async Task<Run> OpenRunAsync(Guid tenantId, TenantCalendar calendar, CancellationToken cancellationToken)
    {
        var id = new TenantId(tenantId);
        var regional = await tenantDirectory.GetRegionalSettingsAsync(id, cancellationToken);
        // Sin fila regional, el huso del reloj y los formatos por defecto (RegionalDateFormat/RegionalNumberFormat resuelven "").
        var format = new ServiceContractsEmailFormat(
            regional?.TimeZone ?? calendar.TimeZone.Id, regional?.DateFormat ?? string.Empty, regional?.NumberFormat ?? string.Empty);
        var tenantName = await tenantDirectory.GetDisplayNameAsync(id, cancellationToken) ?? string.Empty;
        return new Run(tenantId, calendar.Today, calendar.UtcNow, tenantName, format);
    }

    // Paso 3. Los ids primero: cada servicio se relee con tracking dentro de su try, porque el finally limpia el tracker.
    private async Task GenerateAsync(Run run, CancellationToken cancellationToken)
    {
        var serviceIds = (await services.ListActiveAsync(run.TenantId, cancellationToken)).Select(service => service.Id).ToArray();
        dbContext.ChangeTracker.Clear();

        foreach (var serviceId in serviceIds)
        {
            try
            {
                await GenerateServiceAsync(run, serviceId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogGenerationFailed(logger, exception, serviceId.Value, run.TenantId);
            }
            finally
            {
                dbContext.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// La generación de un contrato en modo <c>Run</c> (spec reajuste §6.3): aplica el cambio de precio pendiente que alcance,
    /// retiene el cobro de una revisión <c>Ipc</c> sin precio decidido (o lo genera «sin revisar» en <c>Manual</c>) y guarda
    /// cobros, historial, programado y contrato en una sola transacción. <c>IX_charges_service_period</c> es la deduplicación:
    /// chocar es «otro lo creó», no un error. Se relee el servicio —su <c>last_generated_due_date</c> ya avanzó— y se intenta
    /// de nuevo, una vez; lo mismo si alguien editó el servicio en el medio (token de concurrencia), que es también como chocan
    /// dos réplicas que retienen a la vez.
    /// </summary>
    private async Task GenerateServiceAsync(Run run, ServiceContractId serviceId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var service = await services.FindAsync(run.TenantId, serviceId, cancellationToken);
            if (service is null)
            {
                return;
            }

            var pending = await schedules.FindPendingAsync(run.TenantId, serviceId, cancellationToken);
            var generated = ChargeGenerator.Generate(service, pending, GenerationMode.Run, run.Today, run.Now);
            if (!generated.ServiceChanged)
            {
                return;
            }

            // Una cancelación o edición que cargó la versión anterior pierde con 412 en vez de confirmar sobre cobros que no vio.
            service.BumpVersionAfterGeneration();
            charges.AddRange(generated.Charges);
            foreach (var change in generated.PriceChanges)
            {
                priceChanges.Add(change);
            }

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException exception) when (attempt == 0
                && (exception is DbUpdateConcurrencyException || UniqueViolation.Is(exception, "IX_charges_service_period")))
            {
                dbContext.ChangeTracker.Clear();
            }
        }
    }

    // Paso 4.
    private async Task MessageCustomersAsync(Run run, CancellationToken cancellationToken)
    {
        var pending = await LoadPendingAsync(run.TenantId, cancellationToken);
        if (pending.Charges.Count == 0)
        {
            return;
        }

        var profilesByCustomer = (await profiles.ListAsync(run.TenantId, cancellationToken)).ToDictionary(profile => profile.CustomerId);
        var chargeIds = pending.Charges.Select(charge => charge.Id).ToArray();
        var steps = await dbContext.ReminderSteps
            .AsNoTracking()
            .Where(step => chargeIds.Contains(step.ChargeId))
            .Select(step => new CoveredStep(step.ChargeId, step.Step, step.MessageId))
            .ToListAsync(cancellationToken);
        var todaysMessages = await dbContext.CustomerMessages
            .AsNoTracking()
            .Where(message => message.TenantId == run.TenantId && message.RunDate == run.Today)
            .ToDictionaryAsync(message => message.CustomerId, cancellationToken);
        var settings = await settingsRepository.GetOrDefaultAsync(run.TenantId, run.Now, cancellationToken);
        var connectionId = settings.WhatsAppConnectionId;
        dbContext.ChangeTracker.Clear();

        // Sin perfil no hay canal ni encargado: el cliente no recibe nada (spec §3.3, «cuyo cliente tiene perfil»).
        foreach (var group in pending.Charges.Where(charge => profilesByCustomer.ContainsKey(charge.CustomerId)).GroupBy(charge => charge.CustomerId))
        {
            try
            {
                await MessageCustomerAsync(
                    run,
                    new CustomerWork(
                        group.Key,
                        profilesByCustomer[group.Key].Channel,
                        profilesByCustomer[group.Key].AssigneeMemberId,
                        group.ToArray(),
                        pending.Customers.GetValueOrDefault(group.Key),
                        todaysMessages.GetValueOrDefault(group.Key)),
                    steps,
                    pending.ServicesById,
                    connectionId,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogCustomerFailed(logger, exception, group.Key, run.TenantId);
            }
            finally
            {
                dbContext.ChangeTracker.Clear();
            }
        }
    }

    private async Task MessageCustomerAsync(
        Run run,
        CustomerWork work,
        IReadOnlyList<CoveredStep> steps,
        IReadOnlyDictionary<ServiceContractId, ServiceContract> servicesById,
        Guid? connectionId,
        CancellationToken cancellationToken)
    {
        Guid messageId;
        CustomerMessagePlan? plan;
        var chargeIds = work.Charges.Select(charge => charge.Id).ToHashSet();

        if (work.TodaysMessage is { } existing)
        {
            // El cliente ya tiene su mensaje del día. Sólo se toca si quedó Sending con el lease vencido (la réplica se cayó).
            if (existing.Status != MessageDeliveryStatus.Sending || existing.ClaimedUntil >= run.Now)
            {
                return;
            }

            if (existing.FailureCode is not null)
            {
                await messages.FailAbandonedAsync(existing.Id, run.Now, cancellationToken);
                return;
            }

            if (!await messages.TryRetakeAsync(existing.Id, run.Now, cancellationToken))
            {
                return;
            }

            // Los pasos de este mensaje están cubiertos por él mismo: se planifica como si no lo estuvieran, para reenviar lo mismo.
            plan = CustomerMessagePlanner.Plan(work.Charges, CoveredBy(steps, chargeIds, except: existing.Id), run.Today);
            if (plan is null)
            {
                await messages.MarkFailedAsync(existing.Id, "send_failed", cancellationToken);
                return;
            }

            messageId = existing.Id;
        }
        else
        {
            plan = CustomerMessagePlanner.Plan(work.Charges, CoveredBy(steps, chargeIds, except: null), run.Today);
            if (plan is null)
            {
                return;
            }

            var claimed = await messages.TryClaimAsync(
                run.TenantId, work.CustomerId, run.Today, plan.Kind, work.Channel, plan.Steps, run.Now, cancellationToken);
            if (claimed is not { } id)
            {
                return;
            }

            messageId = id;
        }

        var result = await SendAsync(run, work, plan, servicesById, connectionId, cancellationToken);
        if (result is ServiceContractsSendResult.Failed failed)
        {
            // Un fallo también cubre los pasos (spec §3.3): no se reintenta mañana el mismo paso, y el fallo queda visible.
            await messages.MarkFailedAsync(messageId, failed.Code, cancellationToken);
        }
        else
        {
            await messages.MarkSentAsync(messageId, run.Now, cancellationToken);
        }
    }

    /// <summary>
    /// La resolución del canal de spec §3.4 (ruling F4). Nunca cambia de canal ante un fallo: un perfil WhatsApp sin teléfono o sin
    /// conexión falla con su código aunque el cliente tenga correo. Una excepción del puerto es <c>send_failed</c>.
    /// </summary>
    private async Task<ServiceContractsSendResult> SendAsync(
        Run run,
        CustomerWork work,
        CustomerMessagePlan plan,
        IReadOnlyDictionary<ServiceContractId, ServiceContract> servicesById,
        Guid? connectionId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await consentGate.MayNotifyAsync(run.TenantId, work.CustomerId, cancellationToken))
            {
                return new ServiceContractsSendResult.Failed("consent_blocked");
            }

            if (work.Channel == BillingChannel.WhatsApp)
            {
                if (work.Customer?.PhoneE164 is not { Length: > 0 } phone)
                {
                    return new ServiceContractsSendResult.Failed("no_phone");
                }

                if (connectionId is not { } connection)
                {
                    return new ServiceContractsSendResult.Failed("channel_not_configured");
                }

                return await whatsApp.SendAsync(
                    new ServiceContractsWhatsAppContext(run.TenantId, work.CustomerId, work.AssigneeMemberId),
                    BuildMessage(run, work, plan, servicesById, phone),
                    connection,
                    cancellationToken);
            }

            if (work.Customer?.Email is not { Length: > 0 } email)
            {
                return new ServiceContractsSendResult.Failed("no_email");
            }

            return await customerEmail.SendAsync(BuildMessage(run, work, plan, servicesById, email), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSendThrew(logger, exception, work.CustomerId, run.TenantId);
            return new ServiceContractsSendResult.Failed("send_failed");
        }
    }

    private static ServiceContractsCustomerMessage BuildMessage(
        Run run,
        CustomerWork work,
        CustomerMessagePlan plan,
        IReadOnlyDictionary<ServiceContractId, ServiceContract> servicesById,
        string toAddress)
    {
        var lines = plan.Charges
            .Select(charge => new ServiceContractsMessageLine(
                ServiceName(servicesById, charge),
                charge.Amount,
                charge.Currency,
                charge.DueDate,
                Math.Max(0, run.Today.DayNumber - charge.DueDate.DayNumber)))
            .ToArray();
        return new ServiceContractsCustomerMessage(
            plan.Kind,
            toAddress,
            work.Customer?.Name ?? string.Empty,
            run.TenantName,
            lines,
            TotalsByCurrency(plan.Charges),
            plan.NearestDueDate,
            run.Format);
    }

    // Paso 5. Se relee después de los mensajes: el «último aviso falló» de hoy ya tiene que estar en el email, y las revisiones se
    // calculan sobre lo que la generación de hoy dejó retenido.
    private async Task SendDigestsAsync(Run run, CancellationToken cancellationToken)
    {
        var pending = await LoadPendingAsync(run.TenantId, cancellationToken);
        var lastReminders = await charges.ListLastRemindersAsync(pending.Charges.Select(charge => charge.Id).ToArray(), cancellationToken);
        var horizon = run.Today.AddDays(ChargeGenerator.WindowDays);
        var ofInterest = pending.Charges
            .Where(charge => charge.DueDate <= horizon || IsFailed(lastReminders, charge))
            .ToArray();
        var reviews = await LoadReviewsOrNoneAsync(run, cancellationToken);
        if (ofInterest.Length == 0 && reviews.Count == 0)
        {
            return;
        }

        var customerIds = ofInterest.Select(charge => charge.CustomerId)
            .Concat(reviews.Select(review => review.CustomerId))
            .Distinct()
            .ToArray();
        var profilesByCustomer = (await profiles.ListByCustomersAsync(run.TenantId, customerIds, cancellationToken))
            .ToDictionary(profile => profile.CustomerId);
        dbContext.ChangeTracker.Clear();
        var assigneeIds = profilesByCustomer.Values.Select(profile => profile.AssigneeMemberId).Distinct().ToArray();
        var assignees = (await members.FindAsync(run.TenantId, assigneeIds, cancellationToken)).ToDictionary(member => member.MemberId);

        // El encargado del perfil, si sigue activo y puede gestionar. Sin él (o sin perfil), lo del cliente va a cada miembro que
        // sí puede (spec §3.3 y reajuste §6.6).
        ServiceContractsMemberRef? AssigneeOf(Guid customerId) =>
            profilesByCustomer.TryGetValue(customerId, out var profile)
            && assignees.TryGetValue(profile.AssigneeMemberId, out var assignee)
            && assignee is { IsActive: true, CanManage: true }
                ? assignee
                : null;

        var managers = customerIds.Any(customerId => AssigneeOf(customerId) is null)
            ? await members.ListManagersAsync(run.TenantId, cancellationToken)
            : [];
        IEnumerable<ServiceContractsMemberRef> RecipientsOf(Guid customerId) =>
            AssigneeOf(customerId) is { } assignee ? [assignee] : managers;

        var digestsByMember = new Dictionary<Guid, MemberDigest>();
        foreach (var charge in ofInterest)
        {
            foreach (var member in RecipientsOf(charge.CustomerId))
            {
                DigestOf(digestsByMember, member).Charges.Add(charge);
            }
        }

        foreach (var review in reviews)
        {
            foreach (var member in RecipientsOf(review.CustomerId))
            {
                DigestOf(digestsByMember, member).Reviews.Add(review.Row);
            }
        }

        foreach (var digest in digestsByMember.Values)
        {
            try
            {
                await SendDigestAsync(run, digest, pending, lastReminders, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogDigestFailed(logger, exception, digest.Member.MemberId, run.TenantId);
            }
            finally
            {
                dbContext.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// Las revisiones son un añadido al digest: si no se pueden leer, el email de los cobros sale igual, sin ellas, y la corrida
    /// termina. Ese día las revisiones no se avisan.
    /// </summary>
    private async Task<IReadOnlyList<CustomerReview>> LoadReviewsOrNoneAsync(Run run, CancellationToken cancellationToken)
    {
        try
        {
            return await LoadReviewsAsync(run, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogReviewsFailed(logger, exception, run.TenantId);
            return [];
        }
        finally
        {
            dbContext.ChangeTracker.Clear();
        }
    }

    /// <summary>Los contratos en <c>Due</c> o <c>Held</c> del tenant como filas del email, por fecha de generación.</summary>
    private async Task<IReadOnlyList<CustomerReview>> LoadReviewsAsync(Run run, CancellationToken cancellationToken)
    {
        var open = await reviewReader.ListOpenAsync(run.TenantId, run.Today, cancellationToken);
        dbContext.ChangeTracker.Clear();
        if (open.Count == 0)
        {
            return [];
        }

        var loadedYears = (await ipcRates.ListAsync(run.TenantId, cancellationToken)).Select(rate => rate.Year).ToHashSet();
        var customersById = (await customers.FindManyAsync(
                run.TenantId, open.Select(entry => entry.Service.CustomerId).Distinct().ToArray(), cancellationToken))
            .ToDictionary(customer => customer.Id);

        // Due y Held son de contratos activos: el período de revisión y su fecha de generación siempre vienen.
        return open
            .OrderBy(entry => entry.Review.GenerationDate)
            .ThenBy(entry => entry.Service.Id.Value)
            .Select(entry => new CustomerReview(
                entry.Service.CustomerId,
                new ServiceContractsPriceReviewRow(
                    customersById.GetValueOrDefault(entry.Service.CustomerId)?.Name ?? string.Empty,
                    entry.Service.Name,
                    entry.Service.Price,
                    entry.Service.Currency,
                    entry.Review.ReviewPeriodDate!.Value,
                    entry.Review.GenerationDate!.Value,
                    entry.Review.State.ToString(),
                    entry.Review.IpcYear,
                    entry.Review.IpcYear is { } year && !loadedYears.Contains(year),
                    entry.Service.HeldSince)))
            .ToArray();
    }

    private async Task SendDigestAsync(
        Run run,
        MemberDigest content,
        PendingSnapshot pending,
        IReadOnlyDictionary<Guid, ReminderDto> lastReminders,
        CancellationToken cancellationToken)
    {
        var member = content.Member;
        if (!await digests.TryClaimAsync(run.TenantId, member.MemberId, run.Today, run.Now, cancellationToken))
        {
            return;
        }

        var ordered = content.Charges.Distinct().OrderBy(charge => charge.DueDate).ThenBy(charge => charge.Id).ToArray();
        var overdue = ordered.Where(charge => charge.DueDate < run.Today).ToArray();

        // Spec 2026-11-22 D-17: the bell hears about the overdue charges as soon as the digest is claimed and before the email is looked at,
        // so a member without an email still sees it. A claim retaken after its lease expired publishes again; the consumer's daily
        // dedupe key absorbs the repeat.
        if (overdue.Length > 0)
        {
            await digestEvents.PublishAsync(run.TenantId, member.MemberId, run.Today, overdue.Length, run.Now, cancellationToken);
        }

        var due = content.Reviews.Where(row => row.State == nameof(ReviewState.Due)).ToArray();
        var held = content.Reviews.Where(row => row.State == nameof(ReviewState.Held)).ToArray();
        // Spec reajuste §6.6: la campana de las revisiones, con la misma regla: apenas se gana el claim y antes de mirar el correo.
        if (content.Reviews.Count > 0)
        {
            await reviewEvents.PublishAsync(
                run.TenantId, member.MemberId, run.Today, due.Length, held.Length, run.Now, cancellationToken);
        }

        if (member.Email is not { Length: > 0 } email)
        {
            await digests.MarkFailedAsync(run.TenantId, member.MemberId, run.Today, "no_email", cancellationToken);
            return;
        }

        var horizon = run.Today.AddDays(ChargeGenerator.WindowDays);
        var digest = new ServiceContractsDigestEmail(
            email,
            member.DisplayName,
            run.TenantName,
            ordered.Where(charge => charge.DueDate >= run.Today && charge.DueDate <= horizon)
                .Select(charge => DigestRow(charge, pending, lastReminders))
                .ToArray(),
            overdue.Select(charge => DigestRow(charge, pending, lastReminders)).ToArray(),
            ordered.Where(charge => IsFailed(lastReminders, charge)).Select(charge => DigestRow(charge, pending, lastReminders)).ToArray(),
            TotalsByCurrency(overdue),
            run.Format,
            due,
            held);

        ServiceContractsSendResult result;
        try
        {
            result = await staffEmail.SendDigestAsync(digest, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogDigestFailed(logger, exception, member.MemberId, run.TenantId);
            result = new ServiceContractsSendResult.Failed("send_failed");
        }

        if (result is ServiceContractsSendResult.Failed failed)
        {
            await digests.MarkFailedAsync(run.TenantId, member.MemberId, run.Today, failed.Code, cancellationToken);
        }
        else
        {
            await digests.MarkSentAsync(run.TenantId, member.MemberId, run.Today, run.Now, cancellationToken);
        }
    }

    /// <summary>
    /// Los <c>Pending</c> del tenant con sus servicios y clientes, sin tracking. Un cobro de un servicio <c>Cancelled</c> no debería
    /// existir (cancelar cancela sus pendientes, §3.2); si existiera, no se avisa. Los de un servicio <c>Paused</c> sí (owner).
    /// </summary>
    private async Task<PendingSnapshot> LoadPendingAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var all = await charges.ListPendingByTenantAsync(tenantId, cancellationToken);
        var serviceIds = all.Select(charge => charge.ServiceId).Distinct().ToArray();
        var servicesById = (await services.FindManyAsync(tenantId, serviceIds, cancellationToken)).ToDictionary(service => service.Id);
        dbContext.ChangeTracker.Clear();

        var live = all
            .Where(charge => servicesById.TryGetValue(charge.ServiceId, out var service) && service.Status != ServiceContractStatus.Cancelled)
            .ToArray();
        var customerRefs = live.Length == 0
            ? new Dictionary<Guid, ServiceContractsCustomerRef>()
            : (await customers.FindManyAsync(tenantId, live.Select(charge => charge.CustomerId).Distinct().ToArray(), cancellationToken))
                .ToDictionary(customer => customer.Id);
        return new PendingSnapshot(live, servicesById, customerRefs);
    }

    private static HashSet<(Guid ChargeId, string Step)> CoveredBy(
        IReadOnlyList<CoveredStep> steps, HashSet<Guid> chargeIds, Guid? except) =>
        steps
            .Where(step => chargeIds.Contains(step.ChargeId) && step.MessageId != except)
            .Select(step => (step.ChargeId, step.Step))
            .ToHashSet();

    private static bool IsFailed(IReadOnlyDictionary<Guid, ReminderDto> lastReminders, Charge charge) =>
        lastReminders.TryGetValue(charge.Id, out var reminder) && reminder.Status == FailedStatus;

    private static MemberDigest DigestOf(Dictionary<Guid, MemberDigest> digestsByMember, ServiceContractsMemberRef member)
    {
        if (!digestsByMember.TryGetValue(member.MemberId, out var digest))
        {
            digest = new MemberDigest(member);
            digestsByMember[member.MemberId] = digest;
        }

        return digest;
    }

    private static ServiceContractsDigestRow DigestRow(
        Charge charge, PendingSnapshot pending, IReadOnlyDictionary<Guid, ReminderDto> lastReminders) =>
        new(
            pending.Customers.GetValueOrDefault(charge.CustomerId)?.Name ?? string.Empty,
            ServiceName(pending.ServicesById, charge),
            charge.Amount,
            charge.Currency,
            charge.DueDate,
            IsFailed(lastReminders, charge) ? lastReminders[charge.Id].FailureCode : null);

    private static string ServiceName(IReadOnlyDictionary<ServiceContractId, ServiceContract> servicesById, Charge charge) =>
        servicesById.TryGetValue(charge.ServiceId, out var service) ? service.Name : string.Empty;

    /// <summary>Un total por moneda, nunca convertido (design): «$ 450.000 y US$ 20,00». En el orden en que aparece cada moneda.</summary>
    private static CurrencyAmountDto[] TotalsByCurrency(IEnumerable<Charge> listed) =>
        listed
            .GroupBy(charge => charge.Currency, StringComparer.Ordinal)
            .Select(group => new CurrencyAmountDto(group.Key, group.Sum(charge => charge.Amount)))
            .ToArray();

    private sealed record Run(Guid TenantId, DateOnly Today, DateTimeOffset Now, string TenantName, ServiceContractsEmailFormat Format);

    private sealed record CoveredStep(Guid ChargeId, string Step, Guid MessageId);

    private sealed record CustomerWork(
        Guid CustomerId,
        BillingChannel Channel,
        Guid AssigneeMemberId,
        IReadOnlyList<Charge> Charges,
        ServiceContractsCustomerRef? Customer,
        CustomerMessage? TodaysMessage);

    private sealed record CustomerReview(Guid CustomerId, ServiceContractsPriceReviewRow Row);

    /// <summary>Lo que le toca a un miembro en el digest del día.</summary>
    private sealed class MemberDigest(ServiceContractsMemberRef member)
    {
        public ServiceContractsMemberRef Member { get; } = member;

        public List<Charge> Charges { get; } = [];

        public List<ServiceContractsPriceReviewRow> Reviews { get; } = [];
    }

    private sealed record PendingSnapshot(
        IReadOnlyList<Charge> Charges,
        IReadOnlyDictionary<ServiceContractId, ServiceContract> ServicesById,
        IReadOnlyDictionary<Guid, ServiceContractsCustomerRef> Customers);
}

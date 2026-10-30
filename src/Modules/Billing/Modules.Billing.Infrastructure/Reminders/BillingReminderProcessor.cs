using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Billing.Application;
using Modules.Billing.Domain;
using Modules.Billing.Infrastructure.Persistence;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Billing.Infrastructure.Reminders;

/// <summary>
/// La corrida diaria de un tenant, spec §3.3 pasos 2 a 6 y en ese orden: generar (para que el cobro exista cuando toque su
/// <c>before_5</c>), el mensaje del día a cada cliente y el email diario a cada encargado. Un error en un servicio, un cliente o
/// un email se registra y no frena a los demás (precedente <c>OutboxDeliveryWorker.DrainAsync</c>); el tracker se limpia después
/// de cada uno para que una entidad que no se pudo guardar no vuelva a intentarse en el <c>SaveChanges</c> del siguiente.
/// </summary>
internal sealed partial class BillingReminderProcessor(
    BillingDbContext dbContext,
    IBillingServiceRepository services,
    IChargeRepository charges,
    ICustomerBillingProfileRepository profiles,
    IBillingSettingsRepository settingsRepository,
    IBillingCustomerLookup customers,
    IBillingMemberDirectory members,
    IBillingCustomerEmail customerEmail,
    IBillingStaffEmail staffEmail,
    IBillingWhatsAppOutbound whatsApp,
    IBillingConsentGate consentGate,
    ITenantClock tenantClock,
    ITenantDirectory tenantDirectory,
    ITenantModules tenantModules,
    TenantRunStore runs,
    CustomerMessageStore messages,
    DailyDigestStore digests,
    IOptions<BillingOptions> options,
    ILogger<BillingReminderProcessor> logger) : IBillingReminderProcessor
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

    public async Task ProcessTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        if (calendar.ToLocal(calendar.UtcNow).Hour < options.Value.SendHourLocal)
        {
            return;
        }

        // El módulo puede apagarse con servicios ya creados: sin él no se genera ni se avisa nada. null es el tenant del stub.
        var modules = await tenantModules.FindAsync(tenantId, cancellationToken);
        if (modules is null || !modules.IsEnabled(TenantModuleKeys.Billing))
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
        await runs.MarkFinishedAsync(tenantId, today, now, cancellationToken);
    }

    private async Task<Run> OpenRunAsync(Guid tenantId, TenantCalendar calendar, CancellationToken cancellationToken)
    {
        var id = new TenantId(tenantId);
        var regional = await tenantDirectory.GetRegionalSettingsAsync(id, cancellationToken);
        // Sin fila regional, el huso del reloj y los formatos por defecto (RegionalDateFormat/RegionalNumberFormat resuelven "").
        var format = new BillingEmailFormat(
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
    /// <c>IX_charges_service_due</c> es la deduplicación de la generación (spec §3.2): chocar es «otra réplica lo creó», no un
    /// error. Se relee el servicio —su <c>last_generated_due_date</c> ya avanzó— y se intenta de nuevo, una vez; lo mismo si alguien
    /// editó el servicio en el medio (token de concurrencia).
    /// </summary>
    private async Task GenerateServiceAsync(Run run, BillingServiceId serviceId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var service = await services.FindAsync(run.TenantId, serviceId, cancellationToken);
            if (service is null)
            {
                return;
            }

            var generated = ChargeGenerator.Generate(service, run.Today, run.Now);
            if (generated.Count == 0)
            {
                return;
            }

            charges.AddRange(generated);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException exception) when (attempt == 0
                && (exception is DbUpdateConcurrencyException || UniqueViolation.Is(exception, "IX_charges_service_due")))
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
        IReadOnlyDictionary<BillingServiceId, BillingService> servicesById,
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
        if (result is BillingSendResult.Failed failed)
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
    private async Task<BillingSendResult> SendAsync(
        Run run,
        CustomerWork work,
        CustomerMessagePlan plan,
        IReadOnlyDictionary<BillingServiceId, BillingService> servicesById,
        Guid? connectionId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await consentGate.MayNotifyAsync(run.TenantId, work.CustomerId, cancellationToken))
            {
                return new BillingSendResult.Failed("consent_blocked");
            }

            if (work.Channel == BillingChannel.WhatsApp)
            {
                if (work.Customer?.PhoneE164 is not { Length: > 0 } phone)
                {
                    return new BillingSendResult.Failed("no_phone");
                }

                if (connectionId is not { } connection)
                {
                    return new BillingSendResult.Failed("channel_not_configured");
                }

                return await whatsApp.SendAsync(BuildMessage(run, work, plan, servicesById, phone), connection, cancellationToken);
            }

            if (work.Customer?.Email is not { Length: > 0 } email)
            {
                return new BillingSendResult.Failed("no_email");
            }

            return await customerEmail.SendAsync(BuildMessage(run, work, plan, servicesById, email), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSendThrew(logger, exception, work.CustomerId, run.TenantId);
            return new BillingSendResult.Failed("send_failed");
        }
    }

    private static BillingCustomerMessage BuildMessage(
        Run run,
        CustomerWork work,
        CustomerMessagePlan plan,
        IReadOnlyDictionary<BillingServiceId, BillingService> servicesById,
        string toAddress)
    {
        var lines = plan.Charges
            .Select(charge => new BillingMessageLine(
                ServiceName(servicesById, charge),
                charge.Amount,
                charge.Currency,
                charge.DueDate,
                Math.Max(0, run.Today.DayNumber - charge.DueDate.DayNumber)))
            .ToArray();
        return new BillingCustomerMessage(
            plan.Kind,
            toAddress,
            work.Customer?.Name ?? string.Empty,
            run.TenantName,
            lines,
            TotalsByCurrency(plan.Charges),
            plan.NearestDueDate,
            run.Format);
    }

    // Paso 5. Se relee después de los mensajes: el «último aviso falló» de hoy ya tiene que estar en el email.
    private async Task SendDigestsAsync(Run run, CancellationToken cancellationToken)
    {
        var pending = await LoadPendingAsync(run.TenantId, cancellationToken);
        if (pending.Charges.Count == 0)
        {
            return;
        }

        var lastReminders = await charges.ListLastRemindersAsync(pending.Charges.Select(charge => charge.Id).ToArray(), cancellationToken);
        var horizon = run.Today.AddDays(ChargeGenerator.WindowDays);
        var ofInterest = pending.Charges
            .Where(charge => charge.DueDate <= horizon || IsFailed(lastReminders, charge))
            .ToArray();
        if (ofInterest.Length == 0)
        {
            return;
        }

        var profilesByCustomer = (await profiles.ListByCustomersAsync(
                run.TenantId, ofInterest.Select(charge => charge.CustomerId).Distinct().ToArray(), cancellationToken))
            .ToDictionary(profile => profile.CustomerId);
        dbContext.ChangeTracker.Clear();
        var assigneeIds = profilesByCustomer.Values.Select(profile => profile.AssigneeMemberId).Distinct().ToArray();
        var assignees = (await members.FindAsync(run.TenantId, assigneeIds, cancellationToken)).ToDictionary(member => member.MemberId);

        var recipients = new Dictionary<Guid, BillingMemberRef>();
        var rowsByMember = new Dictionary<Guid, List<Charge>>();
        var orphans = new List<Charge>();
        foreach (var charge in ofInterest)
        {
            if (profilesByCustomer.TryGetValue(charge.CustomerId, out var profile)
                && assignees.TryGetValue(profile.AssigneeMemberId, out var assignee)
                && assignee is { IsActive: true, CanManage: true })
            {
                recipients[assignee.MemberId] = assignee;
                RowsOf(rowsByMember, assignee.MemberId).Add(charge);
            }
            else
            {
                // Encargado que ya no está activo o no puede gestionar: sus cobros van a cada miembro que sí puede (spec §3.3).
                orphans.Add(charge);
            }
        }

        if (orphans.Count > 0)
        {
            foreach (var manager in await members.ListManagersAsync(run.TenantId, cancellationToken))
            {
                recipients[manager.MemberId] = manager;
                RowsOf(rowsByMember, manager.MemberId).AddRange(orphans);
            }
        }

        foreach (var (memberId, rows) in rowsByMember)
        {
            try
            {
                await SendDigestAsync(run, recipients[memberId], rows, pending, lastReminders, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogDigestFailed(logger, exception, memberId, run.TenantId);
            }
            finally
            {
                dbContext.ChangeTracker.Clear();
            }
        }
    }

    private async Task SendDigestAsync(
        Run run,
        BillingMemberRef member,
        IReadOnlyList<Charge> rows,
        PendingSnapshot pending,
        IReadOnlyDictionary<Guid, ReminderDto> lastReminders,
        CancellationToken cancellationToken)
    {
        if (!await digests.TryClaimAsync(run.TenantId, member.MemberId, run.Today, run.Now, cancellationToken))
        {
            return;
        }

        if (member.Email is not { Length: > 0 } email)
        {
            await digests.MarkFailedAsync(run.TenantId, member.MemberId, run.Today, "no_email", cancellationToken);
            return;
        }

        var ordered = rows.Distinct().OrderBy(charge => charge.DueDate).ThenBy(charge => charge.Id).ToArray();
        var overdue = ordered.Where(charge => charge.DueDate < run.Today).ToArray();
        var horizon = run.Today.AddDays(ChargeGenerator.WindowDays);
        var digest = new BillingDigestEmail(
            email,
            member.DisplayName,
            run.TenantName,
            ordered.Where(charge => charge.DueDate >= run.Today && charge.DueDate <= horizon)
                .Select(charge => DigestRow(charge, pending, lastReminders))
                .ToArray(),
            overdue.Select(charge => DigestRow(charge, pending, lastReminders)).ToArray(),
            ordered.Where(charge => IsFailed(lastReminders, charge)).Select(charge => DigestRow(charge, pending, lastReminders)).ToArray(),
            TotalsByCurrency(overdue),
            run.Format);

        BillingSendResult result;
        try
        {
            result = await staffEmail.SendDigestAsync(digest, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogDigestFailed(logger, exception, member.MemberId, run.TenantId);
            result = new BillingSendResult.Failed("send_failed");
        }

        if (result is BillingSendResult.Failed failed)
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
            .Where(charge => servicesById.TryGetValue(charge.ServiceId, out var service) && service.Status != BillingServiceStatus.Cancelled)
            .ToArray();
        var customerRefs = live.Length == 0
            ? new Dictionary<Guid, BillingCustomerRef>()
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

    private static List<Charge> RowsOf(Dictionary<Guid, List<Charge>> rowsByMember, Guid memberId)
    {
        if (!rowsByMember.TryGetValue(memberId, out var rows))
        {
            rows = [];
            rowsByMember[memberId] = rows;
        }

        return rows;
    }

    private static BillingDigestRow DigestRow(
        Charge charge, PendingSnapshot pending, IReadOnlyDictionary<Guid, ReminderDto> lastReminders) =>
        new(
            pending.Customers.GetValueOrDefault(charge.CustomerId)?.Name ?? string.Empty,
            ServiceName(pending.ServicesById, charge),
            charge.Amount,
            charge.Currency,
            charge.DueDate,
            IsFailed(lastReminders, charge) ? lastReminders[charge.Id].FailureCode : null);

    private static string ServiceName(IReadOnlyDictionary<BillingServiceId, BillingService> servicesById, Charge charge) =>
        servicesById.TryGetValue(charge.ServiceId, out var service) ? service.Name : string.Empty;

    /// <summary>Un total por moneda, nunca convertido (design): «$ 450.000 y US$ 20,00». En el orden en que aparece cada moneda.</summary>
    private static CurrencyAmountDto[] TotalsByCurrency(IEnumerable<Charge> listed) =>
        listed
            .GroupBy(charge => charge.Currency, StringComparer.Ordinal)
            .Select(group => new CurrencyAmountDto(group.Key, group.Sum(charge => charge.Amount)))
            .ToArray();

    private sealed record Run(Guid TenantId, DateOnly Today, DateTimeOffset Now, string TenantName, BillingEmailFormat Format);

    private sealed record CoveredStep(Guid ChargeId, string Step, Guid MessageId);

    private sealed record CustomerWork(
        Guid CustomerId,
        BillingChannel Channel,
        IReadOnlyList<Charge> Charges,
        BillingCustomerRef? Customer,
        CustomerMessage? TodaysMessage);

    private sealed record PendingSnapshot(
        IReadOnlyList<Charge> Charges,
        IReadOnlyDictionary<BillingServiceId, BillingService> ServicesById,
        IReadOnlyDictionary<Guid, BillingCustomerRef> Customers);
}

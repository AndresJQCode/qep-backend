using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.ServiceContracts.Application;
using Modules.ServiceContracts.Domain;
using Modules.ServiceContracts.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;
using static Modules.ServiceContracts.IntegrationTests.ServiceContractsApiHarness;

namespace Modules.ServiceContracts.IntegrationTests;

/// <summary>
/// El contrato HTTP del reajuste de precio (spec reajuste §8) de punta a punta: las revisiones en los DTO que ya existían, las
/// acciones por contrato, el IPC del tenant, la lista de revisiones y la confirmación en lote. Los contratos se siembran en la
/// base con fechas relativas a la «hoy» del tenant: los estados <c>Due</c> y <c>Held</c> dependen de ella.
/// </summary>
public sealed class ServiceContractsPriceReviewApiTests
{
    private const decimal Price = 100000m;

    private static readonly Guid Tenant = Guid.Parse(ServiceContractsApiHarness.TenantId);

    private static readonly string[] ScheduledFields = ["newPrice", "effectiveFrom", "reason"];

    private static readonly string[] ReviewFields = ["AdjustmentMode", "IpcExtraPoints", "NextReviewDate"];

    private static readonly string[] ConfirmedFields = ["newPrice", "effectiveFrom", "reason", "ipcYear", "ipcRate", "extraPoints"];

    /// <summary>Como se ven en <c>pg_stat_activity</c> las dos lecturas con candado de la tasa.</summary>
    private const string SharedRateLock = "%billing.ipc_rates%FOR SHARE%";

    private const string ExclusiveRateLock = "%billing.ipc_rates%FOR UPDATE%";

    // Por debajo del tiempo límite de un comando de Npgsql (30 s): una espera que no se suelta falla aquí y no como error de base.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Los tenants de las pruebas viven en America/Bogota (UTC-5, sin horario de verano): la «hoy» del tenant.
    private static DateOnly TenantToday => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));

    [Fact]
    public async Task TheDetailCarriesThePriceReviewAndTheUpcomingPeriods()
    {
        await using var world = await World.OpenAsync();
        var anchor = TenantToday.AddMonths(2);
        var manual = await world.SeedAsync(anchor, anchor, AdjustmentMode.Manual, anchor.AddMonths(12));
        var ipc = await world.SeedAsync(anchor, anchor, AdjustmentMode.Ipc, anchor.AddMonths(3), points: 2m, name: "Soporte");

        var detail = await world.DetailAsync(manual);
        var capped = await world.DetailAsync(ipc);

        var period = anchor.AddMonths(12);
        Assert.Equal(
            new PriceReviewInfoDto(
                "Manual", 0m, period, period, period.AddDays(-5), period.AddDays(-15), "Upcoming", null, null, null),
            detail.Service.PriceReview);
        Assert.Equal(Enumerable.Range(1, 12).Select(month => anchor.AddMonths(month)), detail.UpcomingPeriodDates);

        var review = anchor.AddMonths(3);
        Assert.Equal(
            new PriceReviewInfoDto(
                "Ipc", 2m, review, review, review.AddDays(-5), review.AddDays(-15), "Upcoming", null, review.Year - 1, null),
            capped.Service.PriceReview);
        Assert.Equal([anchor.AddMonths(1), anchor.AddMonths(2), review], capped.UpcomingPeriodDates);
    }

    [Fact]
    public async Task APausedContractHasStateNoneAndNoUpcomingPeriods()
    {
        await using var world = await World.OpenAsync();
        var anchor = TenantToday.AddMonths(2);
        var paused = await world.SeedAsync(
            anchor, anchor, AdjustmentMode.Ipc, anchor.AddMonths(3), points: 1.5m, paused: true);

        var detail = await world.DetailAsync(paused);

        Assert.Equal(
            new PriceReviewInfoDto("Ipc", 1.5m, anchor.AddMonths(3), null, null, null, "None", null, null, null),
            detail.Service.PriceReview);
        Assert.Empty(detail.UpcomingPeriodDates);
    }

    [Fact]
    public async Task TheScheduledChangeTravelsWithItsAuthorAndThePeriodItAppliesTo()
    {
        await using var world = await World.OpenAsync();
        var anchor = TenantToday.AddMonths(2);
        var active = await world.SeedAsync(anchor, anchor, AdjustmentMode.Manual, anchor.AddMonths(12));
        var paused = await world.SeedAsync(
            anchor, anchor, AdjustmentMode.Manual, anchor.AddMonths(12), paused: true, name: "Soporte");
        // «Desde» cae entre dos períodos: aplica en el primero que lo alcanza.
        var from = anchor.AddMonths(2).AddDays(-3);
        var scheduleId = await world.SeedScheduleAsync(active, 120000m, from, world.AdminId);
        await world.SeedScheduleAsync(paused, 120000m, from, memberId: null, whilePaused: true);

        var detail = await world.DetailAsync(active);
        var pausedDetail = await world.DetailAsync(paused);
        var list = await world.Manager.GetFromJsonAsync<ServicePageDto>($"{ServiceContractsUrl()}/services?status=all", Ct);

        var scheduled = detail.Service.PriceReview.Scheduled!;
        Assert.Equal(
            (scheduleId, from, anchor.AddMonths(2), 120000m, "COP", "Manual", (int?)null, "Más usuarios"),
            (scheduled.Id, scheduled.EffectiveFrom, scheduled.AppliesToPeriodDate, scheduled.NewPrice, scheduled.Currency,
                scheduled.Source, scheduled.IpcYear, scheduled.Reason));
        Assert.Equal((world.AdminId, "Ana Admin"), (scheduled.CreatedBy!.MemberId, scheduled.CreatedBy.DisplayName));
        Assert.Equal("None", pausedDetail.Service.PriceReview.State);
        Assert.Equal(anchor.AddMonths(2), pausedDetail.Service.PriceReview.Scheduled!.AppliesToPeriodDate);
        Assert.Null(pausedDetail.Service.PriceReview.Scheduled.CreatedBy);
        Assert.Equal("Ana Admin", list!.Items.Single(item => item.Id == active).PriceReview.Scheduled!.CreatedBy!.DisplayName);
    }

    [Fact]
    public async Task ChargesCarryPeriodAndUnreviewed()
    {
        await using var world = await World.OpenAsync();
        var anchor = TenantToday.AddMonths(-1);
        var due = TenantToday.AddDays(3);
        var service = await world.SeedAsync(
            anchor, anchor, AdjustmentMode.Manual, anchor.AddMonths(12),
            charges: contract => [Charge.Generate(contract, anchor, due, priceUnreviewed: true, DateTimeOffset.UtcNow)]);

        var detail = await world.DetailAsync(service);
        var page = await world.Manager.GetFromJsonAsync<ChargePageDto>($"{ServiceContractsUrl()}/charges", Ct);
        var charge = await world.Manager.GetFromJsonAsync<ChargeDto>(
            $"{ServiceContractsUrl()}/charges/{detail.Charges[0].Id}", Ct);

        foreach (var dto in new[] { Assert.Single(detail.Charges), Assert.Single(page!.Items), charge!, detail.Service.NextCharge! })
        {
            Assert.Equal((anchor, due, true), (dto.PeriodDate, dto.DueDate, dto.PriceUnreviewed));
        }
    }

    [Fact]
    public async Task PriceChangesCarryTheirOrigin()
    {
        await using var world = await World.OpenAsync();
        var anchor = TenantToday.AddMonths(2);
        var service = await world.SeedAsync(anchor, anchor, AdjustmentMode.Manual, anchor.AddMonths(12));

        var detail = await ReadDetailAsync(await world.Manager.SendAsync(
            WithVersion(
                HttpMethod.Put, ServiceUrl(service), await world.VersionAsync(service),
                new { name = "Hosting", price = 120000m, currency = "COP", priceChangeReason = "Más usuarios" }),
            Ct));

        Assert.Equal("Edit", Assert.Single(detail.PriceChanges).Origin);
    }

    [Fact]
    public async Task TheChargesSummaryCountsTheTenantPriceReviews()
    {
        await using var world = await World.OpenAsync();
        await world.SeedDueAsync(AdjustmentMode.Manual);
        await world.SeedHeldAsync();
        var anchor = TenantToday.AddMonths(2);
        await world.SeedAsync(anchor, anchor, AdjustmentMode.Ipc, anchor.AddMonths(12), name: "Lejano");

        // Ni el estado ni el cliente de la lista de cobros tocan el conteo: es el del tenant entero.
        var page = await world.Manager.GetFromJsonAsync<ChargePageDto>(
            $"{ServiceContractsUrl()}/charges?status=paid&customerId={Guid.CreateVersion7()}", Ct);

        Assert.Empty(page!.Items);
        Assert.Equal(new PriceReviewCountsDto(1, 1), page.Summary.PriceReviews);
    }

    [Fact]
    public async Task TheServiceListCarriesThePriceReview()
    {
        await using var world = await World.OpenAsync();
        var (due, duePeriod) = await world.SeedDueAsync(AdjustmentMode.Ipc);
        var (held, heldPeriod) = await world.SeedHeldAsync();

        var list = await world.Manager.GetFromJsonAsync<ServicePageDto>($"{ServiceContractsUrl()}/services", Ct);

        var dueReview = list!.Items.Single(item => item.Id == due).PriceReview;
        Assert.Equal(("Due", (DateOnly?)duePeriod, (DateOnly?)null), (dueReview.State, dueReview.ReviewPeriodDate, dueReview.HeldSince));
        var heldReview = list.Items.Single(item => item.Id == held).PriceReview;
        Assert.Equal(
            ("Held", (DateOnly?)heldPeriod, (DateOnly?)TenantToday), (heldReview.State, heldReview.ReviewPeriodDate, heldReview.HeldSince));
    }

    [Fact]
    public async Task SchedulingFromTheReviewMakesItScheduled()
    {
        await using var world = await World.OpenAsync();
        await SeedCallerMembershipAsync(world.Factory, "Caro Cobros");
        var (service, period) = await world.SeedDueAsync(AdjustmentMode.Manual);
        var version = await world.VersionAsync(service);

        var detail = await ReadDetailAsync(await world.ScheduleAsync(service, version, new { newPrice = 120000m, reason = " Más usuarios " }));

        var review = detail.Service.PriceReview;
        Assert.Equal(("Scheduled", Price, version + 1), (review.State, detail.Service.Price, detail.Service.Version));
        Assert.Equal(
            (period, period, 120000m, "Manual", "Más usuarios"),
            (review.Scheduled!.EffectiveFrom, review.Scheduled.AppliesToPeriodDate, review.Scheduled.NewPrice, review.Scheduled.Source,
                review.Scheduled.Reason));
        Assert.Equal("Caro Cobros", review.Scheduled.CreatedBy!.DisplayName);
        Assert.Empty(detail.Charges);
        Assert.Equal(1, await CountAuditEntriesAsync(world.Factory, ServiceContractsAuditActions.ServicePriceScheduled, service.ToString()));
        var fields = await ChangedFieldsOfAsync(world.Factory, ServiceContractsAuditActions.ServicePriceScheduled, service.ToString());
        Assert.All(ScheduledFields, field => Assert.Contains(field, fields));
    }

    [Fact]
    public async Task SchedulingAHeldContractReleasesAndGeneratesNow()
    {
        await using var world = await World.OpenAsync();
        var (service, period) = await world.SeedHeldAsync();
        var version = await world.VersionAsync(service);

        var detail = await ReadDetailAsync(await world.ScheduleAsync(service, version, new { newPrice = 120000m, reason = "Más usuarios" }));

        var charge = Assert.Single(detail.Charges);
        Assert.Equal(
            (period, Later(period, TenantToday.AddDays(5)), 120000m, false),
            (charge.PeriodDate, charge.DueDate, charge.Amount, charge.PriceUnreviewed));
        Assert.Equal((120000m, version + 1), (detail.Service.Price, detail.Service.Version));
        Assert.Null(detail.Service.PriceReview.HeldSince);
        Assert.Null(detail.Service.PriceReview.Scheduled);
        Assert.Equal(period.AddMonths(12), detail.Service.PriceReview.NextReviewDate);
        var change = Assert.Single(detail.PriceChanges);
        Assert.Equal(("Scheduled", Price, 120000m), (change.Origin, change.PreviousPrice, change.NewPrice));
    }

    [Fact]
    public async Task SchedulingErrorsFollowTheContract()
    {
        await using var world = await World.OpenAsync();
        var anchor = TenantToday.AddMonths(2);
        var review = anchor.AddMonths(3);
        var ipc = await world.SeedAsync(anchor, anchor, AdjustmentMode.Ipc, review);
        var paused = await world.SeedAsync(anchor, anchor, AdjustmentMode.Manual, review, paused: true, name: "Pausado");
        var cancelled = await world.SeedAsync(anchor, anchor, AdjustmentMode.Manual, review, name: "Cancelado");
        await ReadDetailAsync(await world.Manager.SendAsync(
            WithVersion(HttpMethod.Post, $"{ServiceUrl(cancelled)}/cancel", await world.VersionAsync(cancelled)), Ct));
        var pending = await world.SeedAsync(anchor, anchor, AdjustmentMode.Manual, review, name: "Con pendiente");
        await world.SeedScheduleAsync(pending, 120000m, null);

        async Task ExpectAsync(Guid service, object body, string code)
        {
            var version = await world.VersionAsync(service);
            var response = await world.ScheduleAsync(service, version, body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(code, await CodeOfAsync(response));
            Assert.Equal(version, await world.VersionAsync(service));
        }

        var valid = new { newPrice = 120000m, reason = "Más usuarios" };
        await ExpectAsync(paused, valid, "billing.service.not_active");
        await ExpectAsync(cancelled, valid, "billing.service.cancelled");
        await ExpectAsync(pending, valid, "billing.price_schedule.already_pending");
        await ExpectAsync(
            ipc, new { newPrice = 120000m, effectiveFrom = Iso(anchor), reason = "x" }, "billing.price_schedule.period_generated");
        await ExpectAsync(
            ipc, new { newPrice = 120000m, effectiveFrom = Iso(review.AddDays(1)), reason = "x" }, "billing.price_schedule.after_review");
        await ExpectAsync(ipc, new { newPrice = Price, reason = "x" }, "billing.price_schedule.same_price");
        await ExpectAsync(ipc, new { newPrice = 120000m, reason = "  " }, "billing.service.price_reason_required");
        Assert.Equal(1, await world.CountSchedulesAsync());
    }

    [Fact]
    public async Task AFarReviewOrEffectiveDateIsValidationFailed()
    {
        await using var world = await World.OpenAsync();
        var anchor = TenantToday.AddMonths(2);
        var service = await world.SeedAsync(anchor, anchor, AdjustmentMode.Manual, anchor.AddMonths(12));
        var version = await world.VersionAsync(service);

        var review = await world.ReviewAsync(
            service, version, new { adjustmentMode = "Manual", ipcExtraPoints = 0m, nextReviewDate = "9999-12-31" });
        var schedule = await world.ScheduleAsync(
            service, version, new { newPrice = 120000m, effectiveFrom = "9999-12-31", reason = "x" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, review.StatusCode);
        Assert.True((await ErrorsOfAsync(review)).TryGetProperty("NextReviewDate", out _));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, schedule.StatusCode);
        Assert.True((await ErrorsOfAsync(schedule)).TryGetProperty("EffectiveFrom", out _));
        Assert.Equal(version, await world.VersionAsync(service));
    }

    // «Mantener precio» repetido puede dejar la revisión guardada más allá del techo: editar los puntos o el modo con esa
    // misma fecha sigue permitido; cambiarla por otra igual de lejana, no.
    [Fact]
    public async Task AStoredReviewDatePastTheBoundDoesNotBlockTheOtherFields()
    {
        await using var world = await World.OpenAsync();
        var anchor = TenantToday.AddMonths(2);
        var stored = anchor.AddYears(11);
        var service = await world.SeedAsync(anchor, anchor, AdjustmentMode.Ipc, stored);

        var same = await ReadDetailAsync(await world.ReviewAsync(
            service,
            await world.VersionAsync(service),
            new { adjustmentMode = "Ipc", ipcExtraPoints = 2m, nextReviewDate = Iso(stored) }));
        var moved = await world.ReviewAsync(
            service,
            same.Service.Version,
            new { adjustmentMode = "Ipc", ipcExtraPoints = 2m, nextReviewDate = Iso(stored.AddDays(1)) });

        Assert.Equal((stored, 2m), (same.Service.PriceReview.NextReviewDate, same.Service.PriceReview.IpcExtraPoints));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, moved.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(moved));
        Assert.True((await ErrorsOfAsync(moved)).TryGetProperty("NextReviewDate", out _));
        Assert.Equal(same.Service.Version, await world.VersionAsync(service));
    }

    // Un período más allá del techo de las fechas respondería 422 como «desde cuándo»: no se ofrece.
    [Fact]
    public async Task TheUpcomingPeriodsStopAtTheTenYearBound()
    {
        await using var world = await World.OpenAsync();
        var anchor = TenantToday.AddMonths(1);
        var service = await world.SeedAsync(
            anchor, anchor, AdjustmentMode.Manual, anchor.AddYears(1), cycle: BillingCycle.Annual);

        var detail = await world.DetailAsync(service);

        Assert.Equal(Enumerable.Range(1, 9).Select(years => anchor.AddYears(years)), detail.UpcomingPeriodDates);
    }

    [Fact]
    public async Task SchedulingWithAStaleVersionIs412()
    {
        await using var world = await World.OpenAsync();
        var (service, _) = await world.SeedDueAsync(AdjustmentMode.Manual);

        foreach (var request in ContractActions(service, version: 99))
        {
            var response = await world.Manager.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
            Assert.Equal("concurrency.conflict", await CodeOfAsync(response));
        }
    }

    [Fact]
    public async Task SchedulingAndTheOtherContractActionsWithoutIfMatchAre428()
    {
        await using var world = await World.OpenAsync();
        var (service, _) = await world.SeedDueAsync(AdjustmentMode.Manual);

        foreach (var request in ContractActions(service, version: null))
        {
            var response = await world.Manager.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        }
    }

    [Fact]
    public async Task SchedulingAndTheOtherContractActionsNeedManageAndAKnownService()
    {
        await using var world = await World.OpenAsync();
        var (service, _) = await world.SeedDueAsync(AdjustmentMode.Manual);
        var version = await world.VersionAsync(service);
        using var reader = CreateReader(world.Factory);

        foreach (var request in ContractActions(service, version))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendAsync(request, Ct)).StatusCode);
        }

        foreach (var request in ContractActions(Guid.CreateVersion7(), version: 1))
        {
            var response = await world.Manager.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("billing.service.not_found", await CodeOfAsync(response));
        }

        Assert.Equal(version, await world.VersionAsync(service));
    }

    [Fact]
    public async Task CancellingThePendingScheduleBringsBackTheReview()
    {
        await using var world = await World.OpenAsync();
        var (service, _) = await world.SeedDueAsync(AdjustmentMode.Manual);
        var scheduled = await ReadDetailAsync(await world.ScheduleAsync(
            service, await world.VersionAsync(service), new { newPrice = 120000m, reason = "Más usuarios" }));

        var detail = await ReadDetailAsync(await world.Manager.SendAsync(
            WithVersion(HttpMethod.Delete, $"{ServiceUrl(service)}/price-schedule", scheduled.Service.Version), Ct));

        Assert.Equal(("Due", scheduled.Service.Version + 1), (detail.Service.PriceReview.State, detail.Service.Version));
        Assert.Null(detail.Service.PriceReview.Scheduled);
        var stored = await world.WithDbAsync(db => db.ServicePriceSchedules.AsNoTracking().SingleAsync(Ct));
        Assert.Equal(PriceScheduleStatus.Cancelled, stored.Status);
        Assert.Equal(
            1, await CountAuditEntriesAsync(world.Factory, ServiceContractsAuditActions.ServicePriceScheduleCancelled, service.ToString()));
        Assert.Contains(
            "scheduleId",
            await ChangedFieldsOfAsync(world.Factory, ServiceContractsAuditActions.ServicePriceScheduleCancelled, service.ToString()));
    }

    [Fact]
    public async Task CancellingWithoutPendingIs404()
    {
        await using var world = await World.OpenAsync();
        var (service, _) = await world.SeedDueAsync(AdjustmentMode.Manual);

        var response = await world.Manager.SendAsync(
            WithVersion(HttpMethod.Delete, $"{ServiceUrl(service)}/price-schedule", await world.VersionAsync(service)), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("billing.price_schedule.not_found", await CodeOfAsync(response));
    }

    [Fact]
    public async Task KeepingThePriceMovesTheReviewAYearAhead()
    {
        await using var world = await World.OpenAsync();
        var (service, period) = await world.SeedDueAsync(AdjustmentMode.Ipc);
        var version = await world.VersionAsync(service);

        var detail = await ReadDetailAsync(await world.KeepAsync(service, version));

        Assert.Equal(
            ("Upcoming", period.AddMonths(12), Price, version + 1),
            (detail.Service.PriceReview.State, detail.Service.PriceReview.NextReviewDate, detail.Service.Price, detail.Service.Version));
        Assert.Empty(detail.Charges);
        Assert.Empty(detail.PriceChanges);
        Assert.Equal(1, await CountAuditEntriesAsync(world.Factory, ServiceContractsAuditActions.ServicePriceKept, service.ToString()));
        Assert.Contains(
            "nextReviewDate",
            await ChangedFieldsOfAsync(world.Factory, ServiceContractsAuditActions.ServicePriceKept, service.ToString()));
    }

    [Fact]
    public async Task KeepingAHeldContractGeneratesAtTheCurrentPrice()
    {
        await using var world = await World.OpenAsync();
        var (service, period) = await world.SeedHeldAsync();

        var detail = await ReadDetailAsync(await world.KeepAsync(service, await world.VersionAsync(service)));

        var charge = Assert.Single(detail.Charges);
        Assert.Equal(
            (period, Later(period, TenantToday.AddDays(5)), Price, false),
            (charge.PeriodDate, charge.DueDate, charge.Amount, charge.PriceUnreviewed));
        Assert.Null(detail.Service.PriceReview.HeldSince);
        Assert.Equal(period.AddMonths(12), detail.Service.PriceReview.NextReviewDate);
    }

    [Fact]
    public async Task KeepingWithAPendingScheduleIsAlreadyPending()
    {
        await using var world = await World.OpenAsync();
        var (service, period) = await world.SeedDueAsync(AdjustmentMode.Manual);
        await world.SeedScheduleAsync(service, 120000m, null);
        var version = await world.VersionAsync(service);

        var response = await world.KeepAsync(service, version);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("billing.price_schedule.already_pending", await CodeOfAsync(response));
        var stored = await world.StoredAsync(service);
        Assert.Equal((period, (int)version), (stored.NextReviewDate, stored.Version));
    }

    [Fact]
    public async Task KeepingThePriceOfAPausedContractIsNotActive()
    {
        await using var world = await World.OpenAsync();
        var anchor = TenantToday.AddMonths(2);
        var paused = await world.SeedAsync(anchor, anchor, AdjustmentMode.Manual, anchor.AddMonths(12), paused: true);

        var response = await world.KeepAsync(paused, await world.VersionAsync(paused));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("billing.service.not_active", await CodeOfAsync(response));
    }

    [Fact]
    public async Task SwitchingAHeldContractToManualGeneratesUnreviewed()
    {
        await using var world = await World.OpenAsync();
        var (service, period) = await world.SeedHeldAsync();
        var version = await world.VersionAsync(service);

        var detail = await ReadDetailAsync(await world.ReviewAsync(
            service, version, new { adjustmentMode = "Manual", ipcExtraPoints = 5m, nextReviewDate = Iso(period) }));

        var charge = Assert.Single(detail.Charges);
        Assert.Equal(
            (period, Later(period, TenantToday.AddDays(5)), Price, true),
            (charge.PeriodDate, charge.DueDate, charge.Amount, charge.PriceUnreviewed));
        var review = detail.Service.PriceReview;
        Assert.Equal(("Manual", 0m, period.AddMonths(12)), (review.AdjustmentMode, review.IpcExtraPoints, review.NextReviewDate));
        Assert.Null(review.HeldSince);
        Assert.Equal(version + 1, detail.Service.Version);
        // La revisión la movió la generación, no la persona: la auditoría sólo nombra lo que ella cambió.
        var fields = await ChangedFieldsOfAsync(world.Factory, ServiceContractsAuditActions.ServicePriceReviewUpdated, service.ToString());
        Assert.Contains("adjustmentMode", fields);
        Assert.DoesNotContain("ipcExtraPoints", fields);
        Assert.DoesNotContain("nextReviewDate", fields);
    }

    [Fact]
    public async Task MovingTheReviewPastTheHeldPeriodGeneratesWithoutTheUnreviewedMark()
    {
        await using var world = await World.OpenAsync();
        var (service, period) = await world.SeedHeldAsync();

        var detail = await ReadDetailAsync(await world.ReviewAsync(
            service,
            await world.VersionAsync(service),
            new { adjustmentMode = "Ipc", ipcExtraPoints = 1.5m, nextReviewDate = Iso(period.AddMonths(6)) }));

        var charge = Assert.Single(detail.Charges);
        Assert.Equal(
            (period, Later(period, TenantToday.AddDays(5)), Price, false),
            (charge.PeriodDate, charge.DueDate, charge.Amount, charge.PriceUnreviewed));
        Assert.Null(detail.Service.PriceReview.HeldSince);
        Assert.Equal(period.AddMonths(6), detail.Service.PriceReview.NextReviewDate);
        var fields = await ChangedFieldsOfAsync(world.Factory, ServiceContractsAuditActions.ServicePriceReviewUpdated, service.ToString());
        Assert.Contains("ipcExtraPoints", fields);
        Assert.Contains("nextReviewDate", fields);
        Assert.DoesNotContain("adjustmentMode", fields);
    }

    [Theory]
    [InlineData(AdjustmentMode.Ipc)]
    [InlineData(AdjustmentMode.Manual)]
    public async Task MovingTheReviewBeforeAPendingChangeIsAfterReview(AdjustmentMode current)
    {
        await using var world = await World.OpenAsync();
        var anchor = TenantToday.AddMonths(2);
        // En Ipc se adelanta la revisión; en Manual el pendiente ya está después y se intenta pasar a Ipc.
        var review = current == AdjustmentMode.Ipc ? anchor.AddMonths(12) : anchor.AddMonths(3);
        var service = await world.SeedAsync(anchor, anchor, current, review);
        await world.SeedScheduleAsync(service, 120000m, anchor.AddMonths(6));
        var version = await world.VersionAsync(service);

        var response = await world.ReviewAsync(
            service, version, new { adjustmentMode = "Ipc", ipcExtraPoints = 0m, nextReviewDate = Iso(anchor.AddMonths(3)) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("billing.price_schedule.after_review", await CodeOfAsync(response));
        var stored = await world.StoredAsync(service);
        Assert.Equal((current, review, (int)version), (stored.AdjustmentMode, stored.NextReviewDate, stored.Version));
    }

    [Fact]
    public async Task APausedContractCanEditItsPriceReview()
    {
        await using var world = await World.OpenAsync();
        var anchor = TenantToday.AddMonths(2);
        var paused = await world.SeedAsync(anchor, anchor, AdjustmentMode.Manual, anchor.AddMonths(12), paused: true);

        var detail = await ReadDetailAsync(await world.ReviewAsync(
            paused,
            await world.VersionAsync(paused),
            new { adjustmentMode = "Ipc", ipcExtraPoints = 2m, nextReviewDate = Iso(anchor.AddMonths(4)) }));

        var review = detail.Service.PriceReview;
        Assert.Equal(
            ("Ipc", 2m, anchor.AddMonths(4), "None"),
            (review.AdjustmentMode, review.IpcExtraPoints, review.NextReviewDate, review.State));
        Assert.Empty(detail.Charges);
    }

    [Fact]
    public async Task ACancelledContractCannot()
    {
        await using var world = await World.OpenAsync();
        var anchor = TenantToday.AddMonths(2);
        var service = await world.SeedAsync(anchor, anchor, AdjustmentMode.Manual, anchor.AddMonths(12));
        var cancelled = await ReadDetailAsync(await world.Manager.SendAsync(
            WithVersion(HttpMethod.Post, $"{ServiceUrl(service)}/cancel", await world.VersionAsync(service)), Ct));

        var response = await world.ReviewAsync(
            service,
            cancelled.Service.Version,
            new { adjustmentMode = "Ipc", ipcExtraPoints = 0m, nextReviewDate = Iso(anchor.AddMonths(4)) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("billing.service.cancelled", await CodeOfAsync(response));
    }

    [Fact]
    public async Task PointsAboveFiftyAreValidationFailedInThePriceReview()
    {
        await using var world = await World.OpenAsync();
        var anchor = TenantToday.AddMonths(2);
        var service = await world.SeedAsync(anchor, anchor, AdjustmentMode.Manual, anchor.AddMonths(12));
        var version = await world.VersionAsync(service);

        var points = await world.ReviewAsync(
            service, version, new { adjustmentMode = "Ipc", ipcExtraPoints = 50.01m, nextReviewDate = Iso(anchor.AddMonths(4)) });
        var empty = await world.ReviewAsync(service, version, new { });
        var generated = await world.ReviewAsync(
            service, version, new { adjustmentMode = "Ipc", ipcExtraPoints = 0m, nextReviewDate = Iso(anchor) });

        Assert.Equal("validation.failed", await CodeOfAsync(points));
        Assert.True((await ErrorsOfAsync(points)).TryGetProperty("IpcExtraPoints", out _));
        var errors = await ErrorsOfAsync(empty);
        Assert.All(ReviewFields, field => Assert.True(errors.TryGetProperty(field, out _), field));
        Assert.Equal("billing.service.review_date_invalid", await CodeOfAsync(generated));
        Assert.Equal(version, await world.VersionAsync(service));
    }

    [Fact]
    public async Task TheListShowsDueAndHeldByGenerationDate()
    {
        await using var world = await World.OpenAsync();
        var (held, heldPeriod) = await world.SeedHeldAsync();
        var (ipc, ipcPeriod) = await world.SeedDueAsync(AdjustmentMode.Ipc, points: 2m);
        var (manual, manualPeriod) = await world.SeedDueAsync(AdjustmentMode.Manual, name: "Manual por revisar");
        var anchor = TenantToday.AddMonths(2);
        await world.SeedAsync(anchor, anchor, AdjustmentMode.Ipc, anchor.AddMonths(12), name: "Lejano");
        var (covered, _) = await world.SeedDueAsync(AdjustmentMode.Manual, name: "Ya programado");
        await world.SeedScheduleAsync(covered, 120000m, null);

        var before = await world.ReviewsAsync();

        // Orden por fecha de generación; el empate de los dos «por revisar» lo rompe el id.
        var due = new[] { ipc, manual }.Order().ToArray();
        Assert.Equal(new[] { held, due[0], due[1] }, before.Items.Select(row => row.Service.Id));
        Assert.Equal((3, 1, 25), (before.Total, before.Page, before.PageSize));
        Assert.Equal(new PriceReviewCountsDto(2, 1), before.Summary);
        Assert.All(before.Items, row => Assert.Null(row.ComputedPrice));
        Assert.All(before.Items, row => Assert.Null(row.IpcRate));
        var needed = new[] { heldPeriod, ipcPeriod }.GroupBy(period => period.Year - 1).OrderBy(group => group.Key).ToList();
        Assert.Equal(needed.Select(group => new IpcYearNeedDto(group.Key, null, group.Count())), before.IpcYears);

        foreach (var year in needed)
        {
            await world.SaveRateAsync(year.Key, 5.2m);
        }

        var after = await world.ReviewsAsync();
        var heldOnly = await world.ReviewsAsync("?state=held");
        var dueOnly = await world.ReviewsAsync("?state=DUE");
        var second = await world.ReviewsAsync("?page=2&pageSize=1");

        var heldRow = after.Items[0];
        Assert.Equal(
            ("Held", (decimal?)5.2m, (decimal?)105200m, Price, "COP", "Monthly", "Panadería La Esquina"),
            (heldRow.Review.State, heldRow.IpcRate, heldRow.ComputedPrice, heldRow.Service.Price, heldRow.Service.Currency,
                heldRow.Service.Cycle, heldRow.Service.Customer.Name));
        Assert.Equal((world.AdminId, "Ana Admin"), (heldRow.Assignee!.MemberId, heldRow.Assignee.DisplayName));
        Assert.Equal((await world.StoredAsync(held)).Version, heldRow.Service.Version);
        Assert.Equal(107200m, after.Items.Single(row => row.Service.Id == ipc).ComputedPrice);
        var manualRow = after.Items.Single(row => row.Service.Id == manual);
        Assert.Equal(("Due", (DateOnly?)manualPeriod, (decimal?)null, (decimal?)null),
            (manualRow.Review.State, manualRow.Review.ReviewPeriodDate, manualRow.IpcRate, manualRow.ComputedPrice));
        Assert.Equal([held], heldOnly.Items.Select(row => row.Service.Id));
        Assert.Equal((1, new PriceReviewCountsDto(2, 1)), (heldOnly.Total, heldOnly.Summary));
        Assert.Equal(needed.Select(group => new IpcYearNeedDto(group.Key, 5.2m, group.Count())), heldOnly.IpcYears);
        Assert.Equal(due, dueOnly.Items.Select(row => row.Service.Id));
        Assert.Equal((due[0], 3, 2, 1), (Assert.Single(second.Items).Service.Id, second.Total, second.Page, second.PageSize));
    }

    [Fact]
    public async Task AssigneeMeAndSearchFilterLikeTheCharges()
    {
        await using var world = await World.OpenAsync();
        var (hosting, _) = await world.SeedDueAsync(AdjustmentMode.Ipc, name: "Hosting");

        // Sin membresía activa no se es encargado de nadie: vacía, con el resumen del tenant.
        var stranger = await world.ReviewsAsync("?assignee=me");
        Assert.Empty(stranger.Items);
        Assert.Equal((0, new PriceReviewCountsDto(1, 0)), (stranger.Total, stranger.Summary));
        Assert.Single(stranger.IpcYears);

        var caller = await SeedCallerMembershipAsync(world.Factory, "Caro Cobros");
        var mine = await SeedCustomerAsync(world.Factory, Tenant, "Ferretería Central", email: "ferre@example.com");
        await world.SeedProfileAsync(mine, caller);
        var (domain, _) = await world.SeedDueAsync(AdjustmentMode.Manual, name: "Dominio", customerId: mine);

        var assigned = await world.ReviewsAsync("?assignee=me");
        var byCustomer = await world.ReviewsAsync("?search=ferreter");
        var byService = await world.ReviewsAsync("?search=host");
        var wildcard = await world.ReviewsAsync("?search=%25");
        var both = await world.ReviewsAsync("?assignee=me&search=host");

        Assert.Equal([domain], assigned.Items.Select(row => row.Service.Id));
        Assert.Equal(new PriceReviewCountsDto(2, 0), assigned.Summary);
        Assert.Equal([domain], byCustomer.Items.Select(row => row.Service.Id));
        Assert.Equal([hosting], byService.Items.Select(row => row.Service.Id));
        Assert.Empty(wildcard.Items);
        Assert.Empty(both.Items);
    }

    [Fact]
    public async Task TheListsAreForReadersAndTheRateAndTheBatchNeedManageAccess()
    {
        await using var world = await World.OpenAsync();
        using var reader = CreateReader(world.Factory);
        var year = TenantToday.Year - 1;

        var reviews = await reader.GetAsync($"{ServiceContractsUrl()}/price-reviews", Ct);
        var rates = await reader.GetAsync($"{ServiceContractsUrl()}/ipc-rates", Ct);
        var save = await reader.PutAsJsonAsync(RateUrl(year), new { rate = 5.2m, expectedRate = (decimal?)null }, Ct);
        var confirm = await reader.PostAsJsonAsync(
            ConfirmUrl(), new { year, items = new[] { new { serviceId = Guid.CreateVersion7(), version = 1, expectedPrice = 1m } } }, Ct);
        var badState = await reader.GetAsync($"{ServiceContractsUrl()}/price-reviews?state=scheduled&pageSize=101", Ct);

        Assert.Equal(HttpStatusCode.OK, reviews.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rates.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, save.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, confirm.StatusCode);
        var errors = await ErrorsOfAsync(badState);
        Assert.True(errors.TryGetProperty("State", out _));
        Assert.True(errors.TryGetProperty("PageSize", out _));
    }

    // Un esquema que JsonSchemaExporter no puede armar tumba el documento entero, y expectedRate es un JsonElement.
    [Fact]
    public async Task TheOpenApiDocumentStillBuildsWithTheRateAndReviewRoutes()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json", Ct);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        var document = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var paths = document.GetProperty("paths").EnumerateObject().Select(path => path.Name).ToArray();
        const string billing = "/api/v1/tenants/{tenantId}/billing";
        Assert.Contains($"{billing}/ipc-rates", paths);
        Assert.Contains($"{billing}/ipc-rates/{{year}}", paths);
        Assert.Contains($"{billing}/price-reviews", paths);
        Assert.Contains($"{billing}/price-reviews/confirm-ipc", paths);
    }

    [Fact]
    public async Task SavingTheFirstRateNeedsANullExpectedRate()
    {
        await using var world = await World.OpenAsync();
        var caller = await SeedCallerMembershipAsync(world.Factory, "Caro Cobros");
        var year = TenantToday.Year - 1;

        var stale = await world.PutRateAsync(year, 5.2m, 5.0m);
        var saved = await world.PutRateAsync(year, 5.2m, null);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("billing.ipc.rate_changed", await CodeOfAsync(stale));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var dto = (await saved.Content.ReadFromJsonAsync<IpcRateDto>(Ct))!;
        Assert.Equal(
            (year, 5.2m, false, caller, "Caro Cobros", 1L),
            (dto.Year, dto.Rate, dto.InUse, dto.UpdatedBy!.MemberId, dto.UpdatedBy.DisplayName, dto.Version));
        await world.SaveRateAsync(year - 1, 9.28m);
        var list = await world.Manager.GetFromJsonAsync<IpcRateListDto>($"{ServiceContractsUrl()}/ipc-rates", Ct);
        Assert.Equal([(year, 5.2m), (year - 1, 9.28m)], list!.Items.Select(item => (item.Year, item.Rate)));
        Assert.Equal("Caro Cobros", list.Items[0].UpdatedBy!.DisplayName);
        var id = year.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(1, await CountAuditEntriesAsync(world.Factory, ServiceContractsAuditActions.IpcSaved, id));
        var fields = await ChangedFieldsOfAsync(world.Factory, ServiceContractsAuditActions.IpcSaved, id);
        Assert.Contains("year", fields);
        Assert.Contains("rate", fields);
    }

    [Fact]
    public async Task AnExpectedRateThatIsNotStoredIs409()
    {
        await using var world = await World.OpenAsync();
        var year = TenantToday.Year - 1;
        await world.SaveRateAsync(year, 5.2m);

        var other = await world.PutRateAsync(year, 6m, 5.0m);
        var none = await world.PutRateAsync(year, 6m, null);
        var vanished = await world.PutRateAsync(year - 1, 6m, 5.2m);

        foreach (var response in new[] { other, none, vanished })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("billing.ipc.rate_changed", await CodeOfAsync(response));
        }

        var stored = Assert.Single(await world.RatesAsync());
        Assert.Equal((year, 5.2m, 1), (stored.Year, stored.Rate, stored.Version));

        var changed = (await (await world.PutRateAsync(year, 6m, 5.2m)).Content.ReadFromJsonAsync<IpcRateDto>(Ct))!;
        Assert.Equal((6m, 2L), (changed.Rate, changed.Version));
    }

    [Fact]
    public async Task TheSameStoredRateIs200EvenWithAStaleExpectedRate()
    {
        await using var world = await World.OpenAsync();
        var year = TenantToday.Year - 1;
        await world.SaveRateAsync(year, 5.2m);

        var response = await world.PutRateAsync(year, 5.20m, 3m);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<IpcRateDto>(Ct))!;
        Assert.Equal((5.2m, 1L), (dto.Rate, dto.Version));
        Assert.Equal(
            1,
            await CountAuditEntriesAsync(
                world.Factory, ServiceContractsAuditActions.IpcSaved, year.ToString(CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task ARateInUseCannotChange()
    {
        await using var world = await World.OpenAsync();
        var (service, period) = await world.SeedDueAsync(AdjustmentMode.Ipc);
        var year = period.Year - 1;
        await world.SaveRateAsync(year, 5.2m);
        var confirmed = await world.ConfirmAsync(year, new { serviceId = service, version = await world.VersionAsync(service), expectedPrice = 105200m });
        Assert.Equal("Confirmed", Assert.Single(confirmed).Outcome);

        // El conflicto de tasa se mira antes que el uso.
        var stale = await world.PutRateAsync(year, 6m, 4m);
        var inUse = await world.PutRateAsync(year, 6m, 5.2m);
        var same = await world.PutRateAsync(year, 5.2m, 5.2m);
        var list = await world.Manager.GetFromJsonAsync<IpcRateListDto>($"{ServiceContractsUrl()}/ipc-rates", Ct);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("billing.ipc.rate_changed", await CodeOfAsync(stale));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inUse.StatusCode);
        Assert.Equal("billing.ipc.in_use", await CodeOfAsync(inUse));
        Assert.True((await same.Content.ReadFromJsonAsync<IpcRateDto>(Ct))!.InUse);
        var item = Assert.Single(list!.Items);
        Assert.Equal((5.2m, true), (item.Rate, item.InUse));
    }

    [Fact]
    public async Task TwoUpdatesOfTheSameRateNeverOverwriteSilently()
    {
        await using var world = await World.OpenAsync();
        var year = TenantToday.Year - 1;
        await world.SaveRateAsync(year, 5m);

        var responses = await Task.WhenAll(world.PutRateAsync(year, 5.5m, 5m), world.PutRateAsync(year, 6m, 5m));

        var winner = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        var loser = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("billing.ipc.rate_changed", await CodeOfAsync(loser));
        var stored = Assert.Single(await world.RatesAsync());
        Assert.Equal(((await winner.Content.ReadFromJsonAsync<IpcRateDto>(Ct))!.Rate, 2), (stored.Rate, stored.Version));
    }

    // El orden de dos requests no se controla: aquí la escritura de la otra persona entra, sin falta, entre la lectura y el
    // guardado. Sin fila guardada no hay candado que la detenga y la frena la clave primaria; con fila, espera
    // (ASaveWaitsForAnotherWriterOfTheRateAndNeverOverwritesIt).
    [Fact]
    public async Task ARateFirstWrittenBetweenTheReadAndTheSaveIsNeverOverwritten()
    {
        var interleave = new RateInterleave();
        await using var world = await World.OpenAsync(services => Interleave(services, interleave));
        var year = TenantToday.Year - 1;

        interleave.AfterNextLock(() => world.WriteRateBehindTheBackAsync(year, 7m));
        var response = await world.PutRateAsync(year, 6m, null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("billing.concurrent_first_write", await CodeOfAsync(response));
        Assert.Equal(7m, Assert.Single(await world.RatesAsync()).Rate);
    }

    // La otra persona es una transacción aparte que ya cambió la fila y no ha confirmado.
    [Fact]
    public async Task ASaveWaitsForAnotherWriterOfTheRateAndNeverOverwritesIt()
    {
        await using var world = await World.OpenAsync();
        var year = TenantToday.Year - 1;
        await world.SaveRateAsync(year, 5m);
        await using var scope = world.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
        await using var writer = await db.Database.BeginTransactionAsync(Ct);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE billing.ipc_rates SET rate = 7, version = version + 1 WHERE tenant_id = {Tenant} AND year = {year}", Ct);

        var save = world.PutRateAsync(year, 6m, 5m);
        await world.WaitUntilBlockedAsync(ExclusiveRateLock, save);
        await writer.CommitAsync(Ct);
        var response = await save.WaitAsync(Patience, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("billing.ipc.rate_changed", await CodeOfAsync(response));
        var stored = Assert.Single(await world.RatesAsync());
        Assert.Equal((7m, 2), (stored.Rate, stored.Version));
    }

    // Una confirmación a medio camino, hecha a mano en otra transacción: tiene la tasa compartida y su cambio sin confirmar.
    [Fact]
    public async Task ASaveWaitsForTheHolderOfTheSharedRateAndThenSeesItsPendingChange()
    {
        await using var world = await World.OpenAsync();
        var (service, period) = await world.SeedDueAsync(AdjustmentMode.Ipc);
        var year = period.Year - 1;
        await world.SaveRateAsync(year, 5.2m);
        await using var scope = world.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
        await using var holder = await db.Database.BeginTransactionAsync(Ct);
        Assert.NotNull(await scope.ServiceProvider.GetRequiredService<IIpcRateRepository>().FindForShareAsync(Tenant, year, Ct));
        var contract = (await new ServiceContractRepository(db).FindAsync(Tenant, new ServiceContractId(service), Ct))!;
        db.ServicePriceSchedules.Add(ServicePriceSchedule.ConfirmIpc(
            contract, year, 5.2m, 105200m, $"IPC {year} (5,20 %)", memberId: null, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(Ct);

        var save = world.PutRateAsync(year, 6m, 5.2m);
        await world.WaitUntilBlockedAsync(ExclusiveRateLock, save);
        await holder.CommitAsync(Ct);
        var response = await save.WaitAsync(Patience, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("billing.ipc.in_use", await CodeOfAsync(response));
        Assert.Equal(5.2m, Assert.Single(await world.RatesAsync()).Rate);
    }

    [Fact]
    public async Task AConfirmWaitsForTheWriterOfTheRateAndThenReadsTheNewOne()
    {
        await using var world = await World.OpenAsync();
        var (service, period) = await world.SeedDueAsync(AdjustmentMode.Ipc);
        var year = period.Year - 1;
        await world.SaveRateAsync(year, 5.2m);
        var item = new { serviceId = service, version = await world.VersionAsync(service), expectedPrice = 105200m };
        await using var scope = world.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
        await using var writer = await db.Database.BeginTransactionAsync(Ct);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE billing.ipc_rates SET rate = 7, version = version + 1 WHERE tenant_id = {Tenant} AND year = {year}", Ct);

        var confirm = world.ConfirmAsync(year, item);
        await world.WaitUntilBlockedAsync(SharedRateLock, confirm);
        await writer.CommitAsync(Ct);
        var result = Assert.Single(await confirm.WaitAsync(Patience, Ct));

        Assert.Equal(("Failed", "billing.price_review.price_changed"), (result.Outcome, result.Code));
        Assert.Equal(0, await world.CountSchedulesAsync());
    }

    // Dos lotes del mismo año comparten la tasa: ninguno espera ni falla por el otro.
    [Fact]
    public async Task ConfirmingDoesNotWaitForAnotherHolderOfTheSharedRate()
    {
        await using var world = await World.OpenAsync();
        var (service, period) = await world.SeedDueAsync(AdjustmentMode.Ipc);
        var year = period.Year - 1;
        await world.SaveRateAsync(year, 5.2m);
        var item = new { serviceId = service, version = await world.VersionAsync(service), expectedPrice = 105200m };
        await using var scope = world.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
        await using var holder = await db.Database.BeginTransactionAsync(Ct);
        Assert.NotNull(await scope.ServiceProvider.GetRequiredService<IIpcRateRepository>().FindForShareAsync(Tenant, year, Ct));

        // La otra transacción sigue abierta mientras responde.
        var result = Assert.Single(await world.ConfirmAsync(year, item).WaitAsync(Patience, Ct));

        Assert.Equal(("Confirmed", (string?)null), (result.Outcome, result.Code));
        await holder.RollbackAsync(Ct);
    }

    // La confirmación real, detenida justo después de tomar la tasa: guardar otra no puede colarse antes de su cambio programado.
    [Fact]
    public async Task AConfirmHoldsTheRateUntilItsPendingChangeIsCommitted()
    {
        var interleave = new RateInterleave();
        await using var world = await World.OpenAsync(services => Interleave(services, interleave));
        var (service, period) = await world.SeedDueAsync(AdjustmentMode.Ipc);
        var year = period.Year - 1;
        await world.SaveRateAsync(year, 5.2m);
        var item = new { serviceId = service, version = await world.VersionAsync(service), expectedPrice = 105200m };
        var pause = new Pause();
        interleave.AfterNextLock(pause.WaitAsync);

        var confirm = world.ConfirmAsync(year, item);
        Task<HttpResponseMessage> save;
        try
        {
            await pause.Reached.WaitAsync(Patience, Ct);
            save = world.PutRateAsync(year, 6m, 5.2m);
            await world.WaitUntilBlockedAsync(ExclusiveRateLock, save);
        }
        finally
        {
            pause.Release();
        }

        var result = Assert.Single(await confirm.WaitAsync(Patience, Ct));
        var response = await save.WaitAsync(Patience, Ct);

        Assert.Equal(("Confirmed", (string?)null), (result.Outcome, result.Code));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("billing.ipc.in_use", await CodeOfAsync(response));
        Assert.Equal(5.2m, Assert.Single(await world.RatesAsync()).Rate);
    }

    // El guardado real, detenido justo después de tomar la tasa, antes de mirar el uso y de escribir.
    [Fact]
    public async Task ASaveHoldsTheRateUntilItIsCommitted()
    {
        var interleave = new RateInterleave();
        await using var world = await World.OpenAsync(services => Interleave(services, interleave));
        var (service, period) = await world.SeedDueAsync(AdjustmentMode.Ipc);
        var year = period.Year - 1;
        await world.SaveRateAsync(year, 5.2m);
        var item = new { serviceId = service, version = await world.VersionAsync(service), expectedPrice = 105200m };
        var pause = new Pause();
        interleave.AfterNextLock(pause.WaitAsync);

        var save = world.PutRateAsync(year, 7m, 5.2m);
        Task<IReadOnlyList<ConfirmIpcResultDto>> confirm;
        try
        {
            await pause.Reached.WaitAsync(Patience, Ct);
            confirm = world.ConfirmAsync(year, item);
            await world.WaitUntilBlockedAsync(SharedRateLock, confirm);
        }
        finally
        {
            pause.Release();
        }

        var response = await save.WaitAsync(Patience, Ct);
        var result = Assert.Single(await confirm.WaitAsync(Patience, Ct));

        Assert.True(response.IsSuccessStatusCode, await BodyAsync(response));
        Assert.Equal(("Failed", "billing.price_review.price_changed"), (result.Outcome, result.Code));
        Assert.Equal(0, await world.CountSchedulesAsync());
        Assert.Equal(7m, Assert.Single(await world.RatesAsync()).Rate);
    }

    [Fact]
    public async Task AnAbsentExpectedRateIsValidationFailed()
    {
        await using var world = await World.OpenAsync();
        var year = TenantToday.Year - 1;

        var absent = await world.Manager.PutAsJsonAsync(RateUrl(year), new { rate = 5.2m }, Ct);
        var outOfRange = await world.PutRateAsync(1999, 100.01m, null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, absent.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(absent));
        Assert.True((await ErrorsOfAsync(absent)).TryGetProperty("ExpectedRate", out _));
        var errors = await ErrorsOfAsync(outOfRange);
        Assert.True(errors.TryGetProperty("Year", out _));
        Assert.True(errors.TryGetProperty("Rate", out _));
        Assert.Empty(await world.RatesAsync());
    }

    [Fact]
    public async Task ConfirmingWithoutTheRateIsNotSet()
    {
        await using var world = await World.OpenAsync();
        var (service, period) = await world.SeedDueAsync(AdjustmentMode.Ipc);

        var response = await world.Manager.PostAsJsonAsync(
            ConfirmUrl(),
            new { year = period.Year - 1, items = new[] { new { serviceId = service, version = 1, expectedPrice = 105200m } } },
            Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("billing.ipc.not_set", await CodeOfAsync(response));
        Assert.Equal(0, await world.CountSchedulesAsync());
    }

    [Fact]
    public async Task ConfirmingReleasesHeldAndSchedulesDue()
    {
        await using var world = await World.OpenAsync();
        await SeedCallerMembershipAsync(world.Factory, "Caro Cobros");
        var (held, heldPeriod) = await world.SeedHeldAsync();
        var (due, duePeriod) = await world.SeedDueAsync(AdjustmentMode.Ipc, points: 2m);
        foreach (var year in new[] { heldPeriod.Year - 1, duePeriod.Year - 1 }.Distinct())
        {
            await world.SaveRateAsync(year, 5.2m);
        }

        // Un lote es de un solo año; a fin de diciembre los dos períodos caen en años distintos, así que van aparte.
        var heldResult = Assert.Single(await world.ConfirmAsync(
            heldPeriod.Year - 1, new { serviceId = held, version = await world.VersionAsync(held), expectedPrice = 105200m }));
        var dueResult = Assert.Single(await world.ConfirmAsync(
            duePeriod.Year - 1, new { serviceId = due, version = await world.VersionAsync(due), expectedPrice = 107200m }));

        Assert.Equal((held, "Confirmed", (string?)null), (heldResult.ServiceId, heldResult.Outcome, heldResult.Code));
        Assert.Equal(
            ("Upcoming", (DateOnly?)null, 105200m),
            (heldResult.Service!.PriceReview.State, heldResult.Service.PriceReview.HeldSince, heldResult.Service.Price));
        Assert.Equal(await world.VersionAsync(held), heldResult.Service.Version);
        var detail = await world.DetailAsync(held);
        var charge = Assert.Single(detail.Charges);
        Assert.Equal(
            (heldPeriod, Later(heldPeriod, TenantToday.AddDays(5)), 105200m, false),
            (charge.PeriodDate, charge.DueDate, charge.Amount, charge.PriceUnreviewed));
        var change = Assert.Single(detail.PriceChanges);
        Assert.Equal(
            ("Ipc", $"IPC {heldPeriod.Year - 1} (5,20 %)", "Caro Cobros"), (change.Origin, change.Reason, change.ChangedBy!.DisplayName));

        Assert.Equal("Confirmed", dueResult.Outcome);
        var review = dueResult.Service!.PriceReview;
        Assert.Equal(("Scheduled", Price), (review.State, dueResult.Service.Price));
        Assert.Equal(
            ("Ipc", (int?)(duePeriod.Year - 1), (decimal?)5.2m, (decimal?)2m, 107200m, duePeriod,
                $"IPC {duePeriod.Year - 1} (5,20 %) + 2,00 puntos"),
            (review.Scheduled!.Source, review.Scheduled.IpcYear, review.Scheduled.IpcRate, review.Scheduled.ExtraPoints,
                review.Scheduled.NewPrice, review.Scheduled.EffectiveFrom, review.Scheduled.Reason));
        Assert.Empty((await world.DetailAsync(due)).Charges);
        var fields = await ChangedFieldsOfAsync(world.Factory, ServiceContractsAuditActions.ServicePriceScheduled, due.ToString());
        Assert.All(ConfirmedFields, field => Assert.Contains(field, fields));
    }

    [Fact]
    public async Task EachItemFailsOnItsOwn()
    {
        await using var world = await World.OpenAsync();
        var (confirmed, period) = await world.SeedDueAsync(AdjustmentMode.Ipc);
        var year = period.Year - 1;
        await world.SaveRateAsync(year, 5.2m);
        var (stale, _) = await world.SeedDueAsync(AdjustmentMode.Ipc, name: "Versión vieja");
        var (manual, _) = await world.SeedDueAsync(AdjustmentMode.Manual, name: "Manual");
        var anchor = TenantToday.AddMonths(2);
        var upcoming = await world.SeedAsync(anchor, anchor, AdjustmentMode.Ipc, anchor.AddMonths(12), name: "Lejano");
        // Retenido desde hace dos años: su IPC es de otro año.
        var oldAnchor = TenantToday.AddYears(-2).AddMonths(-1);
        var otherYear = await world.SeedAsync(
            oldAnchor, oldAnchor, AdjustmentMode.Ipc, oldAnchor.AddMonths(1), name: "Otro año", held: true);
        var (wrongPrice, _) = await world.SeedDueAsync(AdjustmentMode.Ipc, name: "Precio distinto");
        var (noReason, _) = await world.SeedDueAsync(AdjustmentMode.Ipc, name: "Sin motivo");
        var (typed, _) = await world.SeedDueAsync(AdjustmentMode.Ipc, name: "Precio escrito");
        var (covered, _) = await world.SeedDueAsync(AdjustmentMode.Ipc, name: "Ya confirmado");
        await world.SeedScheduleAsync(covered, 120000m, null);
        // Por revisar con un cambio pendiente sobre un período anterior al de la revisión que la corrida aún no generó.
        var earlierAnchor = TenantToday.AddDays(10).AddMonths(-2);
        var earlier = await world.SeedAsync(
            earlierAnchor, earlierAnchor, AdjustmentMode.Ipc, earlierAnchor.AddMonths(2), name: "Con otro pendiente");
        await world.SeedScheduleAsync(earlier, 120000m, earlierAnchor.AddMonths(1));
        var unknown = Guid.CreateVersion7();

        async Task<object> ItemAsync(Guid id, decimal expected = 105200m, decimal? newPrice = null, string? reason = null) =>
            new { serviceId = id, version = await world.VersionAsync(id), expectedPrice = expected, newPrice, reason };

        var results = await world.ConfirmAsync(
            year,
            new { serviceId = stale, version = 99L, expectedPrice = 105200m },
            await ItemAsync(manual),
            await ItemAsync(confirmed),
            await ItemAsync(upcoming),
            await ItemAsync(otherYear),
            await ItemAsync(wrongPrice, expected: 105000m),
            await ItemAsync(noReason, newPrice: 106000m, reason: "  "),
            await ItemAsync(typed, newPrice: 106000m, reason: "Acordado con el cliente"),
            new { serviceId = unknown, version = 1L, expectedPrice = 105200m },
            await ItemAsync(covered),
            await ItemAsync(earlier));

        Assert.Equal(
            new (Guid, string, string?)[]
            {
                (stale, "Failed", "concurrency.conflict"),
                (manual, "Failed", "billing.price_review.not_ipc"),
                (confirmed, "Confirmed", null),
                (upcoming, "Failed", "billing.price_review.not_pending"),
                (otherYear, "Failed", "billing.price_review.year_mismatch"),
                (wrongPrice, "Failed", "billing.price_review.price_changed"),
                (noReason, "Failed", "billing.service.price_reason_required"),
                (typed, "Confirmed", null),
                (unknown, "Failed", "billing.service.not_found"),
                (covered, "Failed", "billing.price_review.not_pending"),
                (earlier, "Failed", "billing.price_schedule.already_pending"),
            },
            results.Select(result => (result.ServiceId, result.Outcome, result.Code)));
        Assert.All(results, result => Assert.Equal(result.Outcome == "Confirmed", result.Service is not null));
        var scheduled = results[7].Service!.PriceReview.Scheduled!;
        Assert.Equal((106000m, "Acordado con el cliente", (decimal?)5.2m), (scheduled.NewPrice, scheduled.Reason, scheduled.IpcRate));
        // Dos confirmados más los dos sembrados: ningún ítem fallido dejó nada.
        Assert.Equal(4, await world.CountSchedulesAsync());
        foreach (var failed in new[] { stale, manual, upcoming, wrongPrice, noReason })
        {
            Assert.Equal(1, (await world.StoredAsync(failed)).Version);
        }
    }

    // Spec reajuste §6.4 (owner, 2026-11-25): reanudar un retenido conserva lo retenido; no nace ningún cobro sin decidir.
    [Fact]
    public async Task ResumingAHeldContractKeepsItHeld()
    {
        await using var world = await World.OpenAsync();
        var (service, period) = await world.SeedHeldAsync();

        await ReadDetailAsync(await world.TransitionAsync(service, "pause"));
        var detail = await ReadDetailAsync(await world.TransitionAsync(service, "resume"));

        var review = detail.Service.PriceReview;
        Assert.Equal(("Held", (DateOnly?)TenantToday, (DateOnly?)period), (review.State, review.HeldSince, review.ReviewPeriodDate));
        Assert.DoesNotContain(detail.Charges, charge => charge.PeriodDate >= period);
    }

    [Fact]
    public async Task KeepingAResumedHeldContractBillsTheCarriedPeriods()
    {
        await using var world = await World.OpenAsync();
        var (service, period) = await world.SeedHeldAsync();
        await ReadDetailAsync(await world.TransitionAsync(service, "pause"));
        await ReadDetailAsync(await world.TransitionAsync(service, "resume"));

        var detail = await ReadDetailAsync(await world.KeepAsync(service, await world.VersionAsync(service)));

        var charge = Assert.Single(detail.Charges);
        Assert.Equal(
            (period, Later(period, TenantToday.AddDays(5)), Price, false),
            (charge.PeriodDate, charge.DueDate, charge.Amount, charge.PriceUnreviewed));
        Assert.Null(detail.Service.PriceReview.HeldSince);
    }

    [Fact]
    public async Task ResumingAPausedHeldContractSwitchedToManualBillsAtOnce()
    {
        await using var world = await World.OpenAsync();
        var (service, period) = await world.SeedHeldAsync();
        await ReadDetailAsync(await world.TransitionAsync(service, "pause"));
        var paused = await ReadDetailAsync(await world.ReviewAsync(
            service,
            await world.VersionAsync(service),
            new { adjustmentMode = "Manual", ipcExtraPoints = 0m, nextReviewDate = Iso(period) }));
        Assert.Empty(paused.Charges);

        var detail = await ReadDetailAsync(await world.TransitionAsync(service, "resume"));

        var charge = Assert.Single(detail.Charges);
        Assert.Equal(
            (period, Later(period, TenantToday.AddDays(5)), Price, true),
            (charge.PeriodDate, charge.DueDate, charge.Amount, charge.PriceUnreviewed));
        Assert.Null(detail.Service.PriceReview.HeldSince);
    }

    // La tasa se vuelve a leer por contrato: si otra persona la cambia durante el lote, quien vio la anterior recibe
    // price_changed y quien confirma la nueva queda con ella en el motivo y en el cambio programado.
    [Fact]
    public async Task ConfirmingReadsTheRateAgainForEachContract()
    {
        var interleave = new RateInterleave();
        await using var world = await World.OpenAsync(services => Interleave(services, interleave));
        var (sawTheOld, period) = await world.SeedDueAsync(AdjustmentMode.Ipc, name: "Vio la anterior");
        var (sawTheNew, _) = await world.SeedDueAsync(AdjustmentMode.Ipc, name: "Vio la nueva");
        var year = period.Year - 1;
        await world.SaveRateAsync(year, 5.2m);
        // La escritura entra justo después de la lectura con la que el lote comprueba que la tasa existe.
        interleave.AfterNextFind(() => world.WriteRateBehindTheBackAsync(year, 7m));

        var results = await world.ConfirmAsync(
            year,
            new { serviceId = sawTheOld, version = await world.VersionAsync(sawTheOld), expectedPrice = 105200m },
            new { serviceId = sawTheNew, version = await world.VersionAsync(sawTheNew), expectedPrice = 107000m });

        Assert.Equal(
            new (Guid, string, string?)[]
            {
                (sawTheOld, "Failed", "billing.price_review.price_changed"),
                (sawTheNew, "Confirmed", null),
            },
            results.Select(result => (result.ServiceId, result.Outcome, result.Code)));
        var scheduled = results[1].Service!.PriceReview.Scheduled!;
        Assert.Equal(
            ((decimal?)7m, 107000m, $"IPC {year} (7,00 %)"), (scheduled.IpcRate, scheduled.NewPrice, scheduled.Reason));
        Assert.Equal(1, await world.CountSchedulesAsync());
    }

    // Una falla de base en un ítem no puede responder 500 con los anteriores ya confirmados.
    [Fact]
    public async Task ADatabaseFailureSavingOneItemFailsOnlyThatItem()
    {
        await using var world = await World.OpenAsync();
        var (before, period) = await world.SeedDueAsync(AdjustmentMode.Ipc, name: "Antes");
        var (refused, _) = await world.SeedDueAsync(AdjustmentMode.Ipc, name: "Rechazado");
        var (after, _) = await world.SeedDueAsync(AdjustmentMode.Ipc, name: "Después");
        var year = period.Year - 1;
        await world.SaveRateAsync(year, 5.2m);
        // La base rechaza el guardado sólo del ítem que escribe ese precio.
        await world.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(
            """
            CREATE FUNCTION billing.test_refuse_schedule() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RAISE EXCEPTION ''schedule refused''; END';
            CREATE TRIGGER test_refuse_schedule BEFORE INSERT ON billing.service_price_schedules
                FOR EACH ROW WHEN (NEW.new_price = 106000) EXECUTE FUNCTION billing.test_refuse_schedule();
            """,
            Ct));

        var results = await world.ConfirmAsync(
            year,
            new { serviceId = before, version = await world.VersionAsync(before), expectedPrice = 105200m },
            new
            {
                serviceId = refused,
                version = await world.VersionAsync(refused),
                expectedPrice = 105200m,
                newPrice = 106000m,
                reason = "Acordado con el cliente",
            },
            new { serviceId = after, version = await world.VersionAsync(after), expectedPrice = 105200m });

        Assert.Equal(
            new (Guid, string, string?)[]
            {
                (before, "Confirmed", null),
                (refused, "Failed", "concurrency.conflict"),
                (after, "Confirmed", null),
            },
            results.Select(result => (result.ServiceId, result.Outcome, result.Code)));
        Assert.Equal(2, await world.CountSchedulesAsync());
        Assert.Equal(1, (await world.StoredAsync(refused)).Version);
    }

    // La de Npgsql tal cual, y envuelta por la estrategia de ejecución de EF cuando es transitoria.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADatabaseFailureReadingOneItemFailsOnlyThatItem(bool wrapped)
    {
        var fault = new ScheduleReadFault();
        await using var world = await World.OpenAsync(services => FailScheduleReads(services, fault));
        var (before, period) = await world.SeedDueAsync(AdjustmentMode.Ipc, name: "Antes");
        var (broken, _) = await world.SeedDueAsync(AdjustmentMode.Ipc, name: "Sin base");
        var (after, _) = await world.SeedDueAsync(AdjustmentMode.Ipc, name: "Después");
        var year = period.Year - 1;
        await world.SaveRateAsync(year, 5.2m);
        var items = new object[]
        {
            new { serviceId = before, version = await world.VersionAsync(before), expectedPrice = 105200m },
            new { serviceId = broken, version = await world.VersionAsync(broken), expectedPrice = 105200m },
            new { serviceId = after, version = await world.VersionAsync(after), expectedPrice = 105200m },
        };
        var lost = new NpgsqlException("connection lost");
        fault.Arm(broken, wrapped ? new InvalidOperationException("transient failure", lost) : lost);

        var results = await world.ConfirmAsync(year, items);

        Assert.Equal(
            new (Guid, string, string?)[]
            {
                (before, "Confirmed", null),
                (broken, "Failed", "concurrency.conflict"),
                (after, "Confirmed", null),
            },
            results.Select(result => (result.ServiceId, result.Outcome, result.Code)));
        Assert.Equal(2, await world.CountSchedulesAsync());
    }

    // Un error que no es de la base no se disfraza de conflicto.
    [Fact]
    public async Task AFailureThatIsNotTheDatabaseStillAnswers500()
    {
        var fault = new ScheduleReadFault();
        await using var world = await World.OpenAsync(services => FailScheduleReads(services, fault));
        var (broken, period) = await world.SeedDueAsync(AdjustmentMode.Ipc);
        var year = period.Year - 1;
        await world.SaveRateAsync(year, 5.2m);
        var version = await world.VersionAsync(broken);
        fault.Arm(broken, new InvalidOperationException("a bug"));

        var response = await world.Manager.PostAsJsonAsync(
            ConfirmUrl(), new { year, items = new[] { new { serviceId = broken, version, expectedPrice = 105200m } } }, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    private static string RateUrl(int year) => $"{ServiceContractsUrl()}/ipc-rates/{year}";

    private static string ConfirmUrl() => $"{ServiceContractsUrl()}/price-reviews/confirm-ipc";

    private static string ServiceUrl(Guid serviceId) => $"{ServiceContractsUrl()}/services/{serviceId}";

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly Later(DateOnly first, DateOnly second) => first > second ? first : second;

    /// <summary>Las cuatro acciones por contrato, cada una con un cuerpo válido.</summary>
    private static HttpRequestMessage[] ContractActions(Guid serviceId, long? version) =>
    [
        WithVersion(
            HttpMethod.Put, $"{ServiceUrl(serviceId)}/price-review", version,
            new { adjustmentMode = "Manual", ipcExtraPoints = 0m, nextReviewDate = Iso(TenantToday.AddYears(1)) }),
        WithVersion(
            HttpMethod.Post, $"{ServiceUrl(serviceId)}/price-schedule", version, new { newPrice = 120000m, reason = "Más usuarios" }),
        WithVersion(HttpMethod.Delete, $"{ServiceUrl(serviceId)}/price-schedule", version),
        WithVersion(HttpMethod.Post, $"{ServiceUrl(serviceId)}/keep-price", version),
    ];

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static async Task<JsonElement> ErrorsOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.GetProperty("errors").Clone();
    }

    private static async Task<string> ChangedFieldsOfAsync(QepApiFactory factory, string action, string resourceId)
    {
        await using var connection = new NpgsqlConnection(
            factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!);
        await connection.OpenAsync(Ct);
        await using var select = new NpgsqlCommand(
            "SELECT changed_fields::text FROM audit.entries WHERE action = @action AND resource_id = @id ORDER BY occurred_at DESC LIMIT 1",
            connection);
        select.Parameters.AddWithValue("action", action);
        select.Parameters.AddWithValue("id", resourceId);
        return (string)(await select.ExecuteScalarAsync(Ct))!;
    }

    private static HttpRequestMessage WithVersion(HttpMethod method, string url, long? version, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (version is { } value)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{value}\"");
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private static async Task<ServiceDetailDto> ReadDetailAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await BodyAsync(response));
        return (await response.Content.ReadFromJsonAsync<ServiceDetailDto>(Ct))!;
    }

    /// <summary>Corre una vez, justo después de la próxima lectura de una tasa: sin candado o con él.</summary>
    private sealed class RateInterleave
    {
        private Func<Task>? afterFind;

        private Func<Task>? afterLock;

        public void AfterNextFind(Func<Task> next) => afterFind = next;

        public void AfterNextLock(Func<Task> next) => afterLock = next;

        public Task RunAfterFindAsync() => Interlocked.Exchange(ref afterFind, null) is { } next ? next() : Task.CompletedTask;

        public Task RunAfterLockAsync() => Interlocked.Exchange(ref afterLock, null) is { } next ? next() : Task.CompletedTask;
    }

    /// <summary>Detiene a quien llegue a <see cref="WaitAsync"/> hasta <see cref="Release"/>.</summary>
    private sealed class Pause
    {
        private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => reached.Task;

        public Task WaitAsync()
        {
            reached.TrySetResult();
            return released.Task;
        }

        public void Release() => released.TrySetResult();
    }

    /// <summary>Envuelve el repositorio de tasas para meter una escritura justo después de la próxima lectura.</summary>
    private static void Interleave(IServiceCollection services, RateInterleave interleave)
    {
        var rates = services.Single(descriptor => descriptor.ServiceType == typeof(IIpcRateRepository));
        services.Remove(rates);
        services.AddScoped<IIpcRateRepository>(provider => new InterleavingIpcRateRepository(
            (IIpcRateRepository)ActivatorUtilities.CreateInstance(provider, rates.ImplementationType!), interleave));
    }

    private sealed class InterleavingIpcRateRepository(IIpcRateRepository inner, RateInterleave interleave) : IIpcRateRepository
    {
        public void Add(IpcRate rate) => inner.Add(rate);

        public async Task<IpcRate?> FindAsync(Guid tenantId, int year, CancellationToken cancellationToken)
        {
            var found = await inner.FindAsync(tenantId, year, cancellationToken);
            await interleave.RunAfterFindAsync();
            return found;
        }

        public async Task<IpcRate?> FindForShareAsync(Guid tenantId, int year, CancellationToken cancellationToken)
        {
            var found = await inner.FindForShareAsync(tenantId, year, cancellationToken);
            await interleave.RunAfterLockAsync();
            return found;
        }

        public async Task<IpcRate?> FindForUpdateAsync(Guid tenantId, int year, CancellationToken cancellationToken)
        {
            var found = await inner.FindForUpdateAsync(tenantId, year, cancellationToken);
            await interleave.RunAfterLockAsync();
            return found;
        }

        public Task<IReadOnlyList<IpcRate>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            inner.ListAsync(tenantId, cancellationToken);
    }

    /// <summary>Hace fallar la lectura del programado pendiente de un contrato.</summary>
    private sealed class ScheduleReadFault
    {
        private Guid serviceId;
        private Exception? failure;

        public void Arm(Guid id, Exception exception) => (serviceId, failure) = (id, exception);

        public void ThrowFor(ServiceContractId id)
        {
            if (failure is { } exception && id.Value == serviceId)
            {
                throw exception;
            }
        }
    }

    /// <summary>Envuelve el repositorio de programados para meter la falla armada en <paramref name="fault"/>.</summary>
    private static void FailScheduleReads(IServiceCollection services, ScheduleReadFault fault)
    {
        var schedules = services.Single(descriptor => descriptor.ServiceType == typeof(IServicePriceScheduleRepository));
        services.Remove(schedules);
        services.AddScoped<IServicePriceScheduleRepository>(provider => new FailingScheduleRepository(
            (IServicePriceScheduleRepository)ActivatorUtilities.CreateInstance(provider, schedules.ImplementationType!), fault));
    }

    private sealed class FailingScheduleRepository(IServicePriceScheduleRepository inner, ScheduleReadFault fault)
        : IServicePriceScheduleRepository
    {
        public void Add(ServicePriceSchedule schedule) => inner.Add(schedule);

        public Task<ServicePriceSchedule?> FindPendingAsync(
            Guid tenantId, ServiceContractId serviceId, CancellationToken cancellationToken)
        {
            fault.ThrowFor(serviceId);
            return inner.FindPendingAsync(tenantId, serviceId, cancellationToken);
        }

        public Task<IReadOnlyList<ServicePriceSchedule>> ListPendingByServicesAsync(
            Guid tenantId, IReadOnlyCollection<ServiceContractId> serviceIds, CancellationToken cancellationToken) =>
            inner.ListPendingByServicesAsync(tenantId, serviceIds, cancellationToken);

        public Task<IReadOnlyList<ServicePriceSchedule>> ListPendingByTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
            inner.ListPendingByTenantAsync(tenantId, cancellationToken);

        public Task<IReadOnlyList<int>> ListPendingIpcYearsAsync(Guid tenantId, CancellationToken cancellationToken) =>
            inner.ListPendingIpcYearsAsync(tenantId, cancellationToken);

        public Task RemoveByServiceAsync(Guid tenantId, ServiceContractId serviceId, CancellationToken cancellationToken) =>
            inner.RemoveByServiceAsync(tenantId, serviceId, cancellationToken);
    }

    /// <summary>Base, fábrica, un cliente con perfil y el encargado <c>Ana Admin</c>.</summary>
    private sealed class World : IAsyncDisposable
    {
        private World(PostgreSqlContainer database, QepApiFactory factory, Guid customerId, Guid adminId)
        {
            Database = database;
            Factory = factory;
            CustomerId = customerId;
            AdminId = adminId;
            Manager = CreateManager(factory);
        }

        public PostgreSqlContainer Database { get; }

        public QepApiFactory Factory { get; }

        public Guid CustomerId { get; }

        public Guid AdminId { get; }

        public HttpClient Manager { get; }

        public static async Task<World> OpenAsync(Action<IServiceCollection>? configureServices = null)
        {
            var database = await StartDatabaseAsync();
            var factory = new QepApiFactory(database.GetConnectionString(), configureServices);
            await SeedTenantAsync(factory);
            var admin = await SeedMemberAsync(factory, "ana@example.com", "Ana Admin", "admin");
            var customer = await SeedCustomerAsync(factory, Tenant, "Panadería La Esquina", "+573001234567", "pan@example.com");
            var world = new World(database, factory, customer, admin);
            await world.SeedProfileAsync(customer, admin);
            return world;
        }

        public async ValueTask DisposeAsync()
        {
            Manager.Dispose();
            await Factory.DisposeAsync();
            await Database.DisposeAsync();
        }

        public Task<bool> SeedProfileAsync(Guid customerId, Guid assignee) =>
            WithDbAsync(async db =>
            {
                db.Profiles.Add(CustomerBillingProfile.Create(Tenant, customerId, BillingChannel.Email, assignee, DateTimeOffset.UtcNow));
                await db.SaveChangesAsync(Ct);
                return true;
            });

        /// <summary>Un contrato, mensual si no se dice otro ciclo, que ya generó hasta <paramref name="lastGenerated"/>.</summary>
        public Task<Guid> SeedAsync(
            DateOnly firstDueDate,
            DateOnly lastGenerated,
            AdjustmentMode mode,
            DateOnly reviewDate,
            decimal points = 0m,
            decimal price = Price,
            string currency = "COP",
            string name = "Hosting",
            Guid? customerId = null,
            bool held = false,
            bool paused = false,
            Func<ServiceContract, Charge[]>? charges = null,
            BillingCycle cycle = BillingCycle.Monthly) =>
            WithDbAsync(async db =>
            {
                var now = DateTimeOffset.UtcNow;
                var service = ServiceContract.Create(
                    ServiceContractId.New(), Tenant, customerId ?? CustomerId, name, price, currency, cycle,
                    firstDueDate, now);
                service.MarkGenerated(lastGenerated);
                service.InitializePriceReview(mode, points, reviewDate);
                // La retención como la deja la corrida de hoy: no genera nada, porque la revisión es el siguiente período.
                if (held)
                {
                    Assert.Empty(ChargeGenerator.Generate(service, null, GenerationMode.Run, TenantToday, now).Charges);
                    Assert.Equal(TenantToday, service.HeldSince);
                }

                if (charges is not null)
                {
                    db.Charges.AddRange(charges(service));
                }

                if (paused)
                {
                    service.Pause(TenantToday, now);
                }

                db.Services.Add(service);
                await db.SaveChangesAsync(Ct);
                return service.Id.Value;
            });

        /// <summary>
        /// Por revisar: el período cae en 7 a 10 días (según el largo del mes), dentro del aviso y fuera de la ventana de
        /// generación.
        /// </summary>
        public async Task<(Guid Id, DateOnly Period)> SeedDueAsync(
            AdjustmentMode mode,
            decimal points = 0m,
            decimal price = Price,
            string currency = "COP",
            string name = "Por revisar",
            Guid? customerId = null)
        {
            var anchor = TenantToday.AddDays(10).AddMonths(-1);
            var period = anchor.AddMonths(1);
            var id = await SeedAsync(anchor, anchor, mode, period, points, price, currency, name, customerId);
            return (id, period);
        }

        /// <summary>Retenido hoy: el período cae a lo sumo en 2 días.</summary>
        public async Task<(Guid Id, DateOnly Period)> SeedHeldAsync(
            decimal points = 0m,
            decimal price = Price,
            string currency = "COP",
            string name = "Retenido",
            Guid? customerId = null)
        {
            var anchor = TenantToday.AddDays(2).AddMonths(-1);
            var period = anchor.AddMonths(1);
            var id = await SeedAsync(
                anchor, anchor, AdjustmentMode.Ipc, period, points, price, currency, name, customerId, held: true);
            return (id, period);
        }

        /// <summary>
        /// Un cambio pendiente programado a mano. Con <paramref name="whilePaused"/> se reanuda un instante para programarlo,
        /// porque el dominio no deja programar en pausa y un pausado con pendiente se llega pausándolo después.
        /// </summary>
        public Task<Guid> SeedScheduleAsync(
            Guid serviceId, decimal newPrice, DateOnly? effectiveFrom, Guid? memberId = null, bool whilePaused = false) =>
            WithDbAsync(async db =>
            {
                var now = DateTimeOffset.UtcNow;
                var service = (await new ServiceContractRepository(db).FindAsync(Tenant, new ServiceContractId(serviceId), Ct))!;
                if (whilePaused)
                {
                    service.Resume(service.FirstDueDate, now);
                }

                var schedule = ServicePriceSchedule.Schedule(service, null, newPrice, effectiveFrom, "Más usuarios", memberId, now);
                if (whilePaused)
                {
                    service.Pause(TenantToday, now);
                }

                db.ServicePriceSchedules.Add(schedule);
                await db.SaveChangesAsync(Ct);
                return schedule.Id;
            });

        public async Task<ServiceDetailDto> DetailAsync(Guid serviceId) =>
            await ReadDetailAsync(await Manager.GetAsync(ServiceUrl(serviceId), Ct));

        public Task<HttpResponseMessage> ScheduleAsync(Guid serviceId, long version, object body) =>
            Manager.SendAsync(WithVersion(HttpMethod.Post, $"{ServiceUrl(serviceId)}/price-schedule", version, body), Ct);

        public Task<HttpResponseMessage> ReviewAsync(Guid serviceId, long version, object body) =>
            Manager.SendAsync(WithVersion(HttpMethod.Put, $"{ServiceUrl(serviceId)}/price-review", version, body), Ct);

        public Task<HttpResponseMessage> KeepAsync(Guid serviceId, long version) =>
            Manager.SendAsync(WithVersion(HttpMethod.Post, $"{ServiceUrl(serviceId)}/keep-price", version), Ct);

        /// <summary><c>pause</c>, <c>resume</c> o <c>cancel</c> con la versión vigente.</summary>
        public async Task<HttpResponseMessage> TransitionAsync(Guid serviceId, string verb) =>
            await Manager.SendAsync(
                WithVersion(HttpMethod.Post, $"{ServiceUrl(serviceId)}/{verb}", await VersionAsync(serviceId)), Ct);

        public Task<int> CountSchedulesAsync() => WithDbAsync(db => db.ServicePriceSchedules.CountAsync(Ct));

        /// <summary>
        /// Espera a que Postgres muestre una sentencia que contiene <paramref name="statement"/> detenida por el candado de otra
        /// sesión. Falla si <paramref name="operation"/> termina sin haber esperado. Mira desde una conexión propia y sin
        /// transacción, que dentro de una <c>pg_stat_activity</c> no se refresca.
        /// </summary>
        public async Task WaitUntilBlockedAsync(string statement, Task operation)
        {
            await using var connection = new NpgsqlConnection(Database.GetConnectionString());
            await connection.OpenAsync(Ct);
            await using var blocked = new NpgsqlCommand(
                """
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity
                    WHERE cardinality(pg_blocking_pids(pid)) > 0 AND query LIKE @statement)
                """,
                connection);
            blocked.Parameters.AddWithValue("statement", statement);
            var deadline = DateTime.UtcNow + Patience;
            while (!(bool)(await blocked.ExecuteScalarAsync(Ct))!)
            {
                Assert.False(operation.IsCompleted, "The operation finished without waiting for the lock.");
                Assert.True(DateTime.UtcNow < deadline, "Postgres never showed the operation waiting for the lock.");
                await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
            }
        }

        public async Task<PriceReviewPageDto> ReviewsAsync(string query = "") =>
            (await Manager.GetFromJsonAsync<PriceReviewPageDto>($"{ServiceContractsUrl()}/price-reviews{query}", Ct))!;

        public Task<HttpResponseMessage> PutRateAsync(int year, decimal rate, decimal? expectedRate) =>
            Manager.PutAsJsonAsync(RateUrl(year), new { rate, expectedRate }, Ct);

        /// <summary>La primera tasa de un año.</summary>
        public async Task SaveRateAsync(int year, decimal rate)
        {
            var response = await PutRateAsync(year, rate, null);
            Assert.True(response.IsSuccessStatusCode, await BodyAsync(response));
        }

        public Task<List<IpcRate>> RatesAsync() => WithDbAsync(db => db.IpcRates.AsNoTracking().ToListAsync(Ct));

        /// <summary>La escritura de «otra persona», directa en la base: crea la tasa o la cambia subiendo su versión.</summary>
        public Task<bool> WriteRateBehindTheBackAsync(int year, decimal rate) =>
            WithDbAsync(async db =>
            {
                var stored = await db.IpcRates.SingleOrDefaultAsync(item => item.Year == year, Ct);
                if (stored is null)
                {
                    db.IpcRates.Add(IpcRate.Create(Tenant, year, rate, null, DateTimeOffset.UtcNow));
                }
                else
                {
                    stored.Change(rate, null, DateTimeOffset.UtcNow);
                }

                await db.SaveChangesAsync(Ct);
                return true;
            });

        public async Task<IReadOnlyList<ConfirmIpcResultDto>> ConfirmAsync(int year, params object[] items)
        {
            var response = await Manager.PostAsJsonAsync(ConfirmUrl(), new { year, items }, Ct);
            Assert.True(response.IsSuccessStatusCode, await BodyAsync(response));
            return (await response.Content.ReadFromJsonAsync<ConfirmIpcResponseDto>(Ct))!.Results;
        }

        public Task<ServiceContract> StoredAsync(Guid serviceId) =>
            WithDbAsync(async db =>
                (await new ServiceContractRepository(db).FindAsync(Tenant, new ServiceContractId(serviceId), Ct))!);

        public async Task<long> VersionAsync(Guid serviceId) => (await StoredAsync(serviceId)).Version;

        public async Task<T> WithDbAsync<T>(Func<ServiceContractsDbContext, Task<T>> action)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>());
        }
    }
}

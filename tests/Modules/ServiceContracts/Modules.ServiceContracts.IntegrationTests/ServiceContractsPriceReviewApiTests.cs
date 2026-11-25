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

        public static async Task<World> OpenAsync()
        {
            var (database, factory) = await ServiceContractsApiHarness.OpenAsync();
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

        /// <summary>Un contrato mensual que ya generó hasta <paramref name="lastGenerated"/>.</summary>
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
            Func<ServiceContract, Charge[]>? charges = null) =>
            WithDbAsync(async db =>
            {
                var now = DateTimeOffset.UtcNow;
                var service = ServiceContract.Create(
                    ServiceContractId.New(), Tenant, customerId ?? CustomerId, name, price, currency, BillingCycle.Monthly,
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
                    service.Pause(now);
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
                    service.Pause(now);
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

        public Task<int> CountSchedulesAsync() => WithDbAsync(db => db.ServicePriceSchedules.CountAsync(Ct));

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

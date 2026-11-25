using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

    private static string ServiceUrl(Guid serviceId) => $"{ServiceContractsUrl()}/services/{serviceId}";

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

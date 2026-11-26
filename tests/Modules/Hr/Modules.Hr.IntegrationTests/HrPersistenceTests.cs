using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Hr.Application;
using Modules.Hr.Domain;
using Npgsql;
using Testcontainers.PostgreSql;
using static Modules.Hr.IntegrationTests.HrApiHarness;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// El mapeo, los repositorios y la traducción de índices únicos de Hr contra Postgres real. Una sola base para la clase; cada
/// prueba trabaja en su propio tenant (los ids de tenant no tienen FK), así que no se estorban.
/// </summary>
public sealed class HrPersistenceTests : IClassFixture<HrPersistenceTests.World>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 20);
    private static readonly SalaryFloor Floor = new(Today, 1_750_905m, 22_761_765m);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly World _world;

    public HrPersistenceTests(World world) => _world = world;

    public sealed class World : IAsyncLifetime
    {
        private PostgreSqlContainer? _database;

        internal QepApiFactory Factory { get; private set; } = null!;

        public Guid CompanyId { get; private set; }

        public Guid OtherCompanyId { get; private set; }

        public async ValueTask InitializeAsync()
        {
            _database = await StartDatabaseAsync();
            Factory = new QepApiFactory(_database.GetConnectionString());
            await SeedTenantAsync(Factory);
            using var client = CreateClient(
                Factory, SubjectId, TenantId, "companies.company.read", "companies.company.manage");
            CompanyId = (await CreateCompanyAsync(client, Guid.Parse(TenantId), "Empresa uno")).Id;
            OtherCompanyId = (await CreateCompanyAsync(client, Guid.Parse(TenantId), "Empresa dos")).Id;
        }

        public async ValueTask DisposeAsync()
        {
            Factory?.Dispose();
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }
    }

    private sealed record Settings(Site Site, Workplace Workplace, Position Position, Position OtherPosition);

    // Corre una unidad de trabajo en su propio scope: lo que no se guardó no se ve, y cada lectura sale de la base.
    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _world.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private Task<object?> InScopeAsync(Func<IServiceProvider, Task> action) =>
        InScopeAsync<object?>(async provider =>
        {
            await action(provider);
            return null;
        });

    private static Task SaveAsync(IServiceProvider provider) =>
        provider.GetRequiredService<IHrUnitOfWork>().SaveChangesAsync(Ct);

    // Sede, centro y dos cargos del tenant: los registros de cargo y lugar tienen FK a ellos.
    private async Task<Settings> SeedSettingsAsync(Guid tenantId, Guid? companyId = null)
    {
        var site = Site.Create(SiteId.New(), tenantId, companyId ?? _world.CompanyId, "Principal", null, null, null, Now);
        var workplace = Workplace.Create(WorkplaceId.New(), tenantId, site, "Bodega", null, ArlRiskClass.III, false, Now);
        var position = Position.Create(PositionId.New(), tenantId, "Analista", Now);
        var other = Position.Create(PositionId.New(), tenantId, "Coordinador", Now);
        await InScopeAsync(async provider =>
        {
            var settings = provider.GetRequiredService<IHrSettingsRepository>();
            settings.Add(site);
            settings.Add(workplace);
            settings.Add(position);
            settings.Add(other);
            await SaveAsync(provider);
        });
        return new Settings(site, workplace, position, other);
    }

    private static EmployeeProfile Profile(
        string document, string first = "Ana", string last = "Pérez", Guid? memberId = null, string country = "CO") => new(
            first, last, HrDocumentType.CC, document, country, null, null, null, null, null, null, memberId,
            null, null, null, null, ContributorSubtype.Standard, false, [DayOfWeek.Saturday, DayOfWeek.Sunday]);

    private static NewContract Contract(
        Settings settings,
        Guid companyId,
        ContractDuration duration = ContractDuration.FixedTerm,
        DateOnly? end = null,
        ContractType type = ContractType.Employee,
        DateOnly? start = null) => new(
            EmploymentContractId.New(),
            new ContractTerms(
                companyId,
                type,
                duration,
                SalaryType.Monthly,
                start ?? new DateOnly(2026, 1, 15),
                duration == ContractDuration.Indefinite ? null : end ?? new DateOnly(2027, 1, 14)),
            null,
            SalaryRecordId.New(),
            2_500_000m,
            false,
            false,
            AssignmentRecordId.New(),
            settings.Position.Id,
            null,
            settings.Site.Id,
            settings.Workplace.Id);

    private Employee Hire(
        Guid tenantId,
        Settings settings,
        string document,
        string first = "Ana",
        string last = "Pérez",
        Guid? memberId = null,
        Guid? companyId = null,
        ContractDuration duration = ContractDuration.FixedTerm,
        DateOnly? end = null,
        DateOnly? start = null) => Employee.Hire(
            EmployeeId.New(),
            tenantId,
            Profile(document, first, last, memberId, "VE"),
            null,
            Contract(settings, companyId ?? _world.CompanyId, duration, end, start: start),
            Floor,
            Now);

    private async Task AddAsync(params Employee[] employees) =>
        await InScopeAsync(async provider =>
        {
            provider.GetRequiredService<IEmployeeRepository>().AddRange(employees);
            await SaveAsync(provider);
        });

    private Task<EmployeeListPage> ListAsync(
        Guid tenantId,
        string? search = null,
        EmployeeStatus? status = null,
        bool expiringSoon = false,
        Guid? companyId = null) =>
        InScopeAsync(provider => provider.GetRequiredService<IEmployeeRepository>()
            .ListAsync(tenantId, search, status, expiringSoon, companyId, Today, 1, 50, Ct));

    [Fact]
    public async Task ListForPayrollReturnsOnlyTheCompanyContractsThatCoverTheRange()
    {
        var tenant = Guid.NewGuid();
        var settings = await SeedSettingsAsync(tenant);
        var active = Hire(tenant, settings, "1001", "Ana", "Alvarez");
        var retired = Hire(tenant, settings, "1002", "Beto", "Benitez");
        retired.Deactivate(new DateOnly(2026, 10, 31), Now);
        var hiredLater = Hire(tenant, settings, "1003", "Carla", "Cardona", duration: ContractDuration.Indefinite, start: new DateOnly(2026, 11, 20));
        var otherCompany = Hire(tenant, settings, "1004", "Dario", "Duarte", companyId: _world.OtherCompanyId);
        await AddAsync(active, retired, hiredLater, otherCompany);

        var firstHalf = await InScopeAsync(provider => provider.GetRequiredService<IEmployeeRepository>()
            .ListForPayrollAsync(tenant, _world.CompanyId, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 15), Ct));
        var secondHalf = await InScopeAsync(provider => provider.GetRequiredService<IEmployeeRepository>()
            .ListForPayrollAsync(tenant, _world.CompanyId, new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 30), Ct));
        var elsewhere = await InScopeAsync(provider => provider.GetRequiredService<IEmployeeRepository>()
            .ListForPayrollAsync(Guid.NewGuid(), _world.CompanyId, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 15), Ct));

        Assert.Equal([active.Id], firstHalf.Select(employee => employee.Id));
        Assert.Equal([active.Id, hiredLater.Id], secondHalf.Select(employee => employee.Id));
        Assert.Empty(elsewhere);
        var contract = Assert.Single(firstHalf[0].Contracts);
        Assert.Single(contract.Salaries);
        Assert.Single(contract.Assignments);
    }

    [Fact]
    public async Task ASiteOfAnUnknownCompanyViolatesTheForeignKey()
    {
        var site = Site.Create(SiteId.New(), Guid.NewGuid(), Guid.NewGuid(), "Principal", null, null, null, Now);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => InScopeAsync(async provider =>
        {
            provider.GetRequiredService<IHrSettingsRepository>().Add(site);
            await SaveAsync(provider);
        }));

        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal("FK_sites_companies_company_id", postgres.ConstraintName);
    }

    [Fact]
    public async Task AnEmployeeWithItsRecordsRoundTrips()
    {
        var tenant = Guid.NewGuid();
        var settings = await SeedSettingsAsync(tenant);
        var employee = Hire(tenant, settings, "1020304050");
        var contractId = employee.ActiveContract!.Id;
        employee.AddSalary(
            contractId, SalaryRecordId.New(), new SalaryEntry(new DateOnly(2026, 12, 1), 2_800_000m, true, false), Floor, null, Now);
        employee.AddAssignment(
            contractId,
            AssignmentRecordId.New(),
            new AssignmentEntry(new DateOnly(2026, 12, 15), settings.OtherPosition.Id, null, settings.Site.Id, settings.Workplace.Id),
            new DocumentRef(Guid.NewGuid(), "cargo.pdf"),
            Now);
        employee.Renew(contractId, ContractRenewalId.New(), new DateOnly(2027, 7, 14), null, Now);
        await AddAsync(employee);

        var loaded = await InScopeAsync(provider =>
            provider.GetRequiredService<IEmployeeRepository>().FindAsync(tenant, employee.Id, Ct));

        Assert.NotNull(loaded);
        Assert.Equal(employee.Version, loaded.Version);
        Assert.Equal("VE", loaded.Country);
        Assert.Equal([DayOfWeek.Saturday, DayOfWeek.Sunday], loaded.RestDays);
        var contract = Assert.Single(loaded.Contracts);
        Assert.Equal(_world.CompanyId, contract.CompanyId);
        Assert.Equal(2, contract.Salaries.Count);
        Assert.Equal(2, contract.Assignments.Count);
        Assert.Contains(contract.Assignments, assignment => assignment.DocumentFileName == "cargo.pdf");
        var renewal = Assert.Single(contract.Renewals);
        Assert.Equal(new DateOnly(2027, 7, 14), renewal.NewEndDate);
        Assert.Equal(new DateOnly(2027, 7, 14), contract.EndDate);
        Assert.Equal(2_800_000m, contract.Salaries.Single(salary => salary.EffectiveDate == new DateOnly(2026, 12, 1)).Amount);
    }

    [Fact]
    public async Task AConversionSavesUnderTheActiveContractIndex()
    {
        var tenant = Guid.NewGuid();
        var settings = await SeedSettingsAsync(tenant);
        var employee = Hire(tenant, settings, "1020304050");
        await AddAsync(employee);
        var oldContractId = employee.ActiveContract!.Id;
        var next = new NewContract(
            EmploymentContractId.New(),
            new ContractTerms(
                _world.CompanyId, ContractType.Employee, ContractDuration.Indefinite, SalaryType.Monthly, new DateOnly(2026, 8, 1), null),
            null,
            SalaryRecordId.New(),
            3_000_000m,
            false,
            false,
            AssignmentRecordId.New(),
            settings.OtherPosition.Id,
            null,
            settings.Site.Id,
            settings.Workplace.Id);

        // One SaveChanges closes the Active contract and inserts the new Active one under the partial unique index.
        await InScopeAsync(async provider =>
        {
            var loaded = await provider.GetRequiredService<IEmployeeRepository>().FindAsync(tenant, employee.Id, Ct);
            loaded!.ChangeContractType(oldContractId, next, Floor, Now.AddMinutes(1));
            await SaveAsync(provider);
        });

        var reloaded = await InScopeAsync(provider =>
            provider.GetRequiredService<IEmployeeRepository>().FindAsync(tenant, employee.Id, Ct));
        Assert.NotNull(reloaded);
        Assert.Equal(2, reloaded.Contracts.Count);
        Assert.Equal(ContractStatus.Converted, reloaded.Contracts.Single(contract => contract.Id == oldContractId).Status);
        var active = reloaded.ActiveContract;
        Assert.NotNull(active);
        Assert.Equal(next.Id, active.Id);
        Assert.Equal(oldContractId, active.PreviousContractId);
        Assert.Equal(ContractStatus.Active, active.Status);
    }

    [Fact]
    public async Task TwoSitesOfTheSameCompanyCannotShareAName()
    {
        var tenant = Guid.NewGuid();
        await SeedSettingsAsync(tenant);

        var exception = await Assert.ThrowsAsync<ResourceConflictException>(() => InScopeAsync(async provider =>
        {
            provider.GetRequiredService<IHrSettingsRepository>()
                .Add(Site.Create(SiteId.New(), tenant, _world.CompanyId, " PRINCIPAL ", null, null, null, Now));
            await SaveAsync(provider);
        }));

        Assert.Equal("hr.settings.name_taken", exception.Code);
    }

    [Fact]
    public async Task TwoCompaniesMayEachHaveASiteWithTheSameName()
    {
        var tenant = Guid.NewGuid();
        await SeedSettingsAsync(tenant);

        await InScopeAsync(async provider =>
        {
            provider.GetRequiredService<IHrSettingsRepository>()
                .Add(Site.Create(SiteId.New(), tenant, _world.OtherCompanyId, "Principal", null, null, null, Now));
            await SaveAsync(provider);
        });

        var snapshot = await InScopeAsync(provider => provider.GetRequiredService<IHrSettingsRepository>().ListAsync(tenant, Ct));
        Assert.Equal(2, snapshot.Sites.Count);
    }

    [Fact]
    public async Task SetupNeedsAnActiveCompanyWithASiteAndAWorkplace()
    {
        var tenant = Guid.NewGuid();
        var withoutPosition = Guid.NewGuid();
        await SeedSettingsAsync(tenant);
        // Sede y centro, pero ningún cargo.
        var site = Site.Create(SiteId.New(), withoutPosition, _world.CompanyId, "Principal", null, null, null, Now);
        await InScopeAsync(async provider =>
        {
            var repository = provider.GetRequiredService<IHrSettingsRepository>();
            repository.Add(site);
            repository.Add(Workplace.Create(WorkplaceId.New(), withoutPosition, site, "Bodega", null, ArlRiskClass.I, false, Now));
            await SaveAsync(provider);
        });

        Task<bool> Complete(Guid forTenant, params Guid[] companies) => InScopeAsync(provider =>
            provider.GetRequiredService<IHrSettingsRepository>().IsSetupCompleteAsync(forTenant, companies, Ct));

        Assert.True(await Complete(tenant, _world.CompanyId));
        Assert.False(await Complete(tenant, _world.OtherCompanyId));
        Assert.False(await Complete(tenant));
        Assert.False(await Complete(withoutPosition, _world.CompanyId));
    }

    [Fact]
    public async Task TheListFiltersByTheCompanyOfTheActiveContract()
    {
        var tenant = Guid.NewGuid();
        var settings = await SeedSettingsAsync(tenant);
        var retired = Hire(tenant, settings, "3");
        retired.Terminate(retired.ActiveContract!.Id, TerminationReason.Resignation, new DateOnly(2026, 11, 1), null, Now);
        await AddAsync(
            Hire(tenant, settings, "1"),
            Hire(tenant, settings, "2", companyId: _world.OtherCompanyId),
            retired);

        var unfiltered = await ListAsync(tenant);
        var filtered = await ListAsync(tenant, companyId: _world.CompanyId);

        Assert.Equal(3, unfiltered.Total);
        Assert.Equal(1, filtered.Total);
        Assert.Equal("1", Assert.Single(filtered.Items).DocumentNumber);
        Assert.Equal((2, 1, 0), (filtered.Active, filtered.Retired, filtered.Deactivated));
        Assert.Equal((unfiltered.Active, unfiltered.Retired, unfiltered.Deactivated, unfiltered.ExpiringSoon),
            (filtered.Active, filtered.Retired, filtered.Deactivated, filtered.ExpiringSoon));
    }

    [Fact]
    public async Task TwoEmployeesCannotShareADocument()
    {
        var tenant = Guid.NewGuid();
        var settings = await SeedSettingsAsync(tenant);
        await AddAsync(Hire(tenant, settings, "1020304050"));

        var exception = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            AddAsync(Hire(tenant, settings, "1020304050", first: "Beto")));

        Assert.Equal("hr.employee.document_taken", exception.Code);
        var taken = await InScopeAsync(provider => provider.GetRequiredService<IEmployeeRepository>()
            .FindTakenDocumentsAsync(tenant, ["1020304050", "999"], Ct));
        Assert.Equal(["1020304050"], taken);
    }

    [Fact]
    public async Task TwoEmployeesCannotLinkTheSameMember()
    {
        var tenant = Guid.NewGuid();
        var settings = await SeedSettingsAsync(tenant);
        var member = Guid.NewGuid();
        await AddAsync(Hire(tenant, settings, "1", memberId: member));

        var exception = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            AddAsync(Hire(tenant, settings, "2", memberId: member)));

        Assert.Equal("hr.employee.member_taken", exception.Code);
        var linked = await InScopeAsync(provider =>
            provider.GetRequiredService<IEmployeeRepository>().ListLinkedMemberIdsAsync(tenant, Ct));
        Assert.Equal([member], linked);
    }

    [Fact]
    public async Task TwoPositionsCannotShareANormalizedName()
    {
        var tenant = Guid.NewGuid();
        await SeedSettingsAsync(tenant);

        var exception = await Assert.ThrowsAsync<ResourceConflictException>(() => InScopeAsync(async provider =>
        {
            provider.GetRequiredService<IHrSettingsRepository>().Add(Position.Create(PositionId.New(), tenant, " ANALISTA ", Now));
            await SaveAsync(provider);
        }));

        Assert.Equal("hr.settings.name_taken", exception.Code);
    }

    [Fact]
    public async Task TwoSitesMayHoldWorkplacesWithTheSameName()
    {
        var tenant = Guid.NewGuid();
        var settings = await SeedSettingsAsync(tenant);
        var otherSite = Site.Create(SiteId.New(), tenant, _world.CompanyId, "Norte", null, null, null, Now);

        await InScopeAsync(async provider =>
        {
            var repository = provider.GetRequiredService<IHrSettingsRepository>();
            repository.Add(otherSite);
            repository.Add(Workplace.Create(WorkplaceId.New(), tenant, otherSite, "Bodega", null, ArlRiskClass.I, false, Now));
            await SaveAsync(provider);
        });

        var snapshot = await InScopeAsync(provider => provider.GetRequiredService<IHrSettingsRepository>().ListAsync(tenant, Ct));
        Assert.Equal(2, snapshot.Workplaces.Count(workplace => workplace.Name == settings.Workplace.Name));

        var exception = await Assert.ThrowsAsync<ResourceConflictException>(() => InScopeAsync(async provider =>
        {
            provider.GetRequiredService<IHrSettingsRepository>()
                .Add(Workplace.Create(WorkplaceId.New(), tenant, otherSite, " BODEGA ", null, ArlRiskClass.I, false, Now));
            await SaveAsync(provider);
        }));
        Assert.Equal("hr.settings.name_taken", exception.Code);
    }

    [Fact]
    public async Task AStaleVersionIsAConcurrencyConflict()
    {
        var tenant = Guid.NewGuid();
        var settings = await SeedSettingsAsync(tenant);
        var employee = Hire(tenant, settings, "1");
        await AddAsync(employee);
        var contractId = employee.ActiveContract!.Id;

        await using var first = _world.Factory.Services.CreateAsyncScope();
        await using var second = _world.Factory.Services.CreateAsyncScope();
        var loadedFirst = await first.ServiceProvider.GetRequiredService<IEmployeeRepository>().FindAsync(tenant, employee.Id, Ct);
        var loadedSecond = await second.ServiceProvider.GetRequiredService<IEmployeeRepository>().FindAsync(tenant, employee.Id, Ct);
        loadedFirst!.AddSalary(
            contractId, SalaryRecordId.New(), new SalaryEntry(new DateOnly(2026, 12, 1), 2_800_000m, false, false), Floor, null, Now);
        loadedSecond!.AddSalary(
            contractId, SalaryRecordId.New(), new SalaryEntry(new DateOnly(2026, 12, 2), 2_900_000m, false, false), Floor, null, Now);

        await SaveAsync(first.ServiceProvider);

        await Assert.ThrowsAsync<RequestConcurrencyException>(() => SaveAsync(second.ServiceProvider));
    }

    // El token de concurrencia de la configuración (Version) corre en la base: dos pantallas que cargaron la misma versión de un área y
    // la guardan por su lado, la segunda pierde. El caso de uso sólo compara la versión con la que cargó; esto prueba el respaldo de EF.
    [Fact]
    public async Task TwoConcurrentUpdatesOfAnAreaEndInAConcurrencyConflict()
    {
        var tenant = Guid.NewGuid();
        var area = Area.Create(AreaId.New(), tenant, "Operaciones", Now);
        await InScopeAsync(async provider =>
        {
            provider.GetRequiredService<IHrSettingsRepository>().Add(area);
            await SaveAsync(provider);
        });

        await using var first = _world.Factory.Services.CreateAsyncScope();
        await using var second = _world.Factory.Services.CreateAsyncScope();
        var loadedFirst = await first.ServiceProvider.GetRequiredService<IHrSettingsRepository>().FindAreaAsync(tenant, area.Id, Ct);
        var loadedSecond = await second.ServiceProvider.GetRequiredService<IHrSettingsRepository>().FindAreaAsync(tenant, area.Id, Ct);
        Assert.Equal(loadedFirst!.Version, loadedSecond!.Version);
        loadedFirst.Rename("Operaciones A", Now);
        loadedSecond.Rename("Operaciones B", Now);

        await SaveAsync(first.ServiceProvider);

        await Assert.ThrowsAsync<RequestConcurrencyException>(() => SaveAsync(second.ServiceProvider));
    }

    // Eliminar es «revisar si se usa» y luego borrar: una contratación que llega en medio hace que el RESTRICT de la base rechace el
    // borrado. Eso es el mismo 409 hr.settings.in_use, no un 500 con una DbUpdateException cruda.
    [Fact]
    public async Task DeletingASettingThatAHireJustStartedUsingIsTheInUseConflict()
    {
        var tenant = Guid.NewGuid();
        var settings = await SeedSettingsAsync(tenant);

        await using var deleting = _world.Factory.Services.CreateAsyncScope();
        var repository = deleting.ServiceProvider.GetRequiredService<IHrSettingsRepository>();
        var workplace = await repository.FindWorkplaceAsync(tenant, settings.Workplace.Id, Ct);
        var position = await repository.FindPositionAsync(tenant, settings.Position.Id, Ct);
        repository.Remove(workplace!);
        repository.Remove(position!);

        // Entre la revisión de «en uso» y el borrado, otra petición contrata a alguien en ese centro y cargo.
        await AddAsync(Hire(tenant, settings, "9001"));

        var exception = await Assert.ThrowsAsync<ResourceConflictException>(() => SaveAsync(deleting.ServiceProvider));
        Assert.Equal("hr.settings.in_use", exception.Code);
    }

    // La traducción es de un borrado de configuración: una contratación que apunta a una configuración ya eliminada rompe la misma FK,
    // pero no es «está en uso», es un error.
    [Fact]
    public async Task AHireOfADeletedSettingIsNotTranslated()
    {
        var tenant = Guid.NewGuid();
        var settings = await SeedSettingsAsync(tenant);
        await InScopeAsync(async provider =>
        {
            var repository = provider.GetRequiredService<IHrSettingsRepository>();
            repository.Remove((await repository.FindWorkplaceAsync(tenant, settings.Workplace.Id, Ct))!);
            await SaveAsync(provider);
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => AddAsync(Hire(tenant, settings, "9002")));
    }

    [Fact]
    public async Task ListCountsRespectSearchButNotStatus()
    {
        var tenant = Guid.NewGuid();
        var settings = await SeedSettingsAsync(tenant);
        var retiredPerez = Hire(tenant, settings, "4", last: "Pérez");
        retiredPerez.Terminate(retiredPerez.ActiveContract!.Id, TerminationReason.Resignation, new DateOnly(2026, 11, 1), null, Now);
        var retiredGomez = Hire(tenant, settings, "5", last: "Gómez");
        retiredGomez.Terminate(retiredGomez.ActiveContract!.Id, TerminationReason.Resignation, new DateOnly(2026, 11, 1), null, Now);
        var deactivated = Hire(tenant, settings, "6", last: "Ruiz");
        deactivated.Deactivate(new DateOnly(2026, 11, 1), Now);
        await AddAsync(
            Hire(tenant, settings, "1", last: "Pérez"),
            Hire(tenant, settings, "2", last: "Gómez"),
            Hire(tenant, settings, "3", last: "Ruiz"),
            retiredPerez,
            retiredGomez,
            deactivated);

        var page = await ListAsync(tenant, search: "pérez", status: EmployeeStatus.Retired);

        Assert.Equal(1, page.Total);
        Assert.Equal("4", Assert.Single(page.Items).DocumentNumber);
        Assert.Equal((1, 1, 0), (page.Active, page.Retired, page.Deactivated));
        var all = await ListAsync(tenant);
        Assert.Equal((3, 2, 1), (all.Active, all.Retired, all.Deactivated));
        var byDocument = await ListAsync(tenant, search: "6");
        Assert.Equal("6", Assert.Single(byDocument.Items).DocumentNumber);
    }

    [Fact]
    public async Task ExpiringSoonFiltersFixedTermWithinThirtyDays()
    {
        var tenant = Guid.NewGuid();
        var settings = await SeedSettingsAsync(tenant);
        await AddAsync(
            Hire(tenant, settings, "1", end: new DateOnly(2026, 12, 20)),
            Hire(tenant, settings, "2", end: new DateOnly(2026, 12, 21)),
            Hire(tenant, settings, "3", duration: ContractDuration.Indefinite));

        var page = await ListAsync(tenant, expiringSoon: true);

        Assert.Equal("1", Assert.Single(page.Items).DocumentNumber);
        Assert.Equal(1, page.Total);
        Assert.Equal(1, page.ExpiringSoon);
        Assert.Equal(3, page.Active);
    }

    [Fact]
    public async Task UsageMarksSettingsInUse()
    {
        var tenant = Guid.NewGuid();
        var settings = await SeedSettingsAsync(tenant);
        await AddAsync(Hire(tenant, settings, "1"));

        var snapshot = await InScopeAsync(provider => provider.GetRequiredService<IHrSettingsRepository>().ListAsync(tenant, Ct));

        Assert.Contains(settings.Position.Id.Value, snapshot.InUse);
        Assert.DoesNotContain(settings.OtherPosition.Id.Value, snapshot.InUse);
        Assert.Contains(settings.Workplace.Id.Value, snapshot.InUse);
        Assert.Contains(settings.Site.Id.Value, snapshot.InUse);
        var repository = await InScopeAsync(async provider =>
        {
            var source = provider.GetRequiredService<IHrSettingsRepository>();
            return (
                await source.IsInUseAsync(tenant, settings.Position.Id.Value, Ct),
                await source.IsInUseAsync(tenant, settings.OtherPosition.Id.Value, Ct),
                await source.SiteHasActiveWorkplacesAsync(tenant, settings.Site.Id, Ct));
        });
        Assert.Equal((true, false, true), repository);
    }

    [Fact]
    public async Task ASiteWithAWorkplaceIsInUseEvenWithoutEmployees()
    {
        var tenant = Guid.NewGuid();
        var settings = await SeedSettingsAsync(tenant);

        var snapshot = await InScopeAsync(provider => provider.GetRequiredService<IHrSettingsRepository>().ListAsync(tenant, Ct));

        Assert.Contains(settings.Site.Id.Value, snapshot.InUse);
        Assert.DoesNotContain(settings.Workplace.Id.Value, snapshot.InUse);
    }

    [Fact]
    public async Task ActivityRoundTripsItsChangesAndFiltersByCategory()
    {
        var tenant = Guid.NewGuid();
        var settings = await SeedSettingsAsync(tenant);
        var employee = Hire(tenant, settings, "1");
        await AddAsync(employee);
        var actor = Guid.NewGuid();
        var salary = new EmployeeActivityEntry(
            Guid.NewGuid(), tenant, employee.Id, Now, actor, "Laura Gómez", HrActivityAction.SalaryCreated, HrActivityCategory.Salary, null,
            [new ActivityChange("amount", JsonSerializer.SerializeToElement(2_500_000m), JsonSerializer.SerializeToElement(2_800_000m), true)]);
        var personal = new EmployeeActivityEntry(
            Guid.NewGuid(), tenant, employee.Id, Now.AddMinutes(1), actor, "Laura Gómez", HrActivityAction.PersonalDataUpdated,
            HrActivityCategory.Personal, null, [new ActivityChange("firstName", JsonSerializer.SerializeToElement("Ana"), null, false)]);
        await InScopeAsync(async provider =>
        {
            var log = provider.GetRequiredService<IEmployeeActivityLog>();
            log.Append(salary);
            log.Append(personal);
            await SaveAsync(provider);
        });

        var all = await InScopeAsync(provider => provider.GetRequiredService<IEmployeeActivityLog>().ListAsync(
            tenant, employee.Id, new HashSet<HrActivityCategory>(Enum.GetValues<HrActivityCategory>()), 1, 25, Ct));
        var onlyPersonal = await InScopeAsync(provider => provider.GetRequiredService<IEmployeeActivityLog>().ListAsync(
            tenant, employee.Id, new HashSet<HrActivityCategory> { HrActivityCategory.Personal }, 1, 25, Ct));

        Assert.Equal(2, all.Total);
        Assert.Equal([personal.Id, salary.Id], all.Items.Select(item => item.Id));
        var change = Assert.Single(all.Items[1].Changes);
        Assert.Equal("amount", change.Field);
        Assert.Equal(2_500_000m, change.Before!.Value.GetDecimal());
        Assert.Equal(2_800_000m, change.After!.Value.GetDecimal());
        Assert.True(change.Sensitive);
        Assert.Equal("Laura Gómez", all.Items[1].ActorName);
        Assert.Equal(1, onlyPersonal.Total);
        Assert.Equal(personal.Id, Assert.Single(onlyPersonal.Items).Id);
        Assert.Null(onlyPersonal.Items[0].Changes[0].After);
    }
}

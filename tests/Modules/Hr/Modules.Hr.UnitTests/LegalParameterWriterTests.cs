using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Modules.Hr.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Hr.UnitTests;

public sealed class LegalParameterWriterTests
{
    private static readonly Guid OperatorTenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid Operator = Guid.Parse("01900000-0000-7000-8000-000000000002");
    private static readonly Guid CompanyA = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 12, 5, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Jan2026 = new(2026, 1, 1);
    private static readonly DateOnly Jan2027 = new(2027, 1, 1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class World
    {
        public readonly List<string> Log = [];
        public readonly List<LegalParameterValue> Values =
        [
            new(LegalParameterKey.Smmlv, new DateOnly(2025, 1, 1), 1_423_500m),
            new(LegalParameterKey.Smmlv, Jan2026, 1_750_905m),
            new(LegalParameterKey.HealthEmployeeRate, Jan2026, 0.04m),
        ];

        public readonly List<LegalParameterBracket> Brackets =
        [
            new(LegalBracketKey.SolidarityFund, Jan2026, 4m, 16m, 0.01m),
            new(LegalBracketKey.SolidarityFund, Jan2026, 16m, 25m, 0.02m),
        ];

        public readonly List<LegalParameterChange> Changes = [];
        public List<CalculatedPeriodStamp> Stamps = [];
        public string? ActorName = "Ana Operadora";
        public int StampReads;
        public bool ThrowOnSave;

        public LegalParameterWriter Writer() => new(
            new FakeRepository(this),
            new FakeUsage(this),
            new FakeUnitOfWork(this),
            new FakeCacheVersion(this),
            new FakeExecutionContext(),
            new FakeMembers(this),
            new FakeClock());
    }

    private sealed class FakeRepository(World world) : ILegalParameterRepository
    {
        public Task<IReadOnlyList<LegalParameterValue>> ListValuesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LegalParameterValue>>(world.Values.ToArray());

        public Task<IReadOnlyList<LegalParameterBracket>> ListBracketsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LegalParameterBracket>>(world.Brackets.ToArray());

        public void Add(LegalParameterValue value) => world.Values.Add(value);

        public void Remove(LegalParameterValue value) => world.Values.Remove(value);

        public void AddBrackets(IEnumerable<LegalParameterBracket> brackets) => world.Brackets.AddRange(brackets);

        public void RemoveBrackets(IEnumerable<LegalParameterBracket> brackets)
        {
            foreach (var bracket in brackets)
            {
                world.Brackets.Remove(bracket);
            }
        }

        public void AddChange(LegalParameterChange change) => world.Changes.Add(change);

        public Task<(IReadOnlyList<LegalParameterChange> Items, int Total)> ListChangesAsync(
            int page, int pageSize, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeUsage(World world) : ILegalParameterUsage
    {
        public Task<IReadOnlyList<CalculatedPeriodStamp>> ListCalculatedPeriodsOfAllTenantsAsync(CancellationToken cancellationToken)
        {
            world.StampReads++;
            return Task.FromResult<IReadOnlyList<CalculatedPeriodStamp>>(world.Stamps.ToArray());
        }
    }

    private sealed class FakeUnitOfWork(World world) : IHrUnitOfWork
    {
        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            world.Log.Add("Save");
            return world.ThrowOnSave ? throw new InvalidOperationException("boom") : Task.CompletedTask;
        }
    }

    private sealed class FakeCacheVersion(World world) : ILegalParameterCacheVersion
    {
        public Task BumpAsync(CancellationToken cancellationToken)
        {
            world.Log.Add("Bump");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeExecutionContext : IExecutionContext
    {
        public Guid SubjectId => Operator;

        public TenantId TenantId => new(OperatorTenant);

        public bool HasPermission(string permission) => true;
    }

    private sealed class FakeMembers(World world) : IHrMemberDirectory
    {
        public Task<IReadOnlyList<HrMemberRef>> FindAsync(
            Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<HrMemberRef>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<HrMemberRef>> ListWithPermissionAsync(Guid tenantId, string permission, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<HrMemberRef?> FindBySubjectAsync(Guid tenantId, Guid subjectId, CancellationToken cancellationToken) =>
            Task.FromResult(
                tenantId == OperatorTenant && subjectId == Operator && world.ActorName is not null
                    ? new HrMemberRef(Guid.NewGuid(), world.ActorName, true)
                    : null);
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private static LegalParameterEdit Edit(LegalParameterKey key, DateOnly date, decimal? value) => new(key, date, value);

    private static Task<LegalParameterWriteResult> ApplyAsync(
        World world, IReadOnlyList<LegalParameterEdit> parameters, IReadOnlyList<LegalBracketSetEdit>? brackets = null,
        LegalParameterChangeOrigin? origin = null) =>
        world.Writer().ApplyAsync(OperatorTenant, parameters, brackets ?? [], origin ?? LegalParameterChangeOrigin.Manual, Ct);

    [Fact]
    public async Task CreatingWritesTheRowAndOneManualChange()
    {
        var world = new World();

        var result = await ApplyAsync(world, [Edit(LegalParameterKey.Smmlv, Jan2027, 2_000_000m)]);

        Assert.Equal(new LegalParameterWriteResult(1, 0, 0, 0), result);
        var row = world.Values.Single(value => value.Key == LegalParameterKey.Smmlv && value.EffectiveFrom == Jan2027);
        Assert.Equal(2_000_000m, row.Value);
        Assert.Equal(Operator, row.UpdatedBy);
        Assert.Equal(Now, row.UpdatedAt);
        var change = Assert.Single(world.Changes);
        Assert.Equal("Smmlv", change.Key);
        Assert.Equal(Jan2027, change.EffectiveFrom);
        Assert.Equal(LegalParameterChangeAction.Created, change.Action);
        Assert.Null(change.OldValue);
        Assert.Equal("2000000", change.NewValue);
        Assert.Equal(Operator, change.ChangedBy);
        Assert.Equal("Ana Operadora", change.ChangedByName);
        Assert.Equal(Now, change.ChangedAt);
        Assert.Equal(LegalParameterChangeSource.Manual, change.Source);
        Assert.Null(change.BatchId);
        Assert.Null(change.FileName);
    }

    [Fact]
    public async Task UpdatingKeepsOldAndNewValues()
    {
        var world = new World();

        var result = await ApplyAsync(world, [Edit(LegalParameterKey.HealthEmployeeRate, Jan2026, 0.05m)]);

        Assert.Equal(new LegalParameterWriteResult(0, 1, 0, 0), result);
        var change = Assert.Single(world.Changes);
        Assert.Equal(LegalParameterChangeAction.Updated, change.Action);
        Assert.Equal("0.04", change.OldValue);
        Assert.Equal("0.05", change.NewValue);
        var row = world.Values.Single(value => value.Key == LegalParameterKey.HealthEmployeeRate);
        Assert.Equal(0.05m, row.Value);
        Assert.Equal(Operator, row.UpdatedBy);
    }

    [Fact]
    public async Task DeletingARowWritesAHistoryEntryWithTheOldValue()
    {
        var world = new World();

        var result = await ApplyAsync(world, [Edit(LegalParameterKey.Smmlv, new DateOnly(2025, 1, 1), null)]);

        Assert.Equal(new LegalParameterWriteResult(0, 0, 1, 0), result);
        Assert.DoesNotContain(world.Values, value => value.Key == LegalParameterKey.Smmlv && value.EffectiveFrom == new DateOnly(2025, 1, 1));
        var change = Assert.Single(world.Changes);
        Assert.Equal(LegalParameterChangeAction.Deleted, change.Action);
        Assert.Equal("1423500", change.OldValue);
        Assert.Null(change.NewValue);
    }

    [Fact]
    public async Task TheSameValueIsUnchangedWithoutHistoryNorBump()
    {
        var world = new World();
        world.Stamps = [new CalculatedPeriodStamp(new DateOnly(2026, 1, 16), CompanyA)];

        var result = await ApplyAsync(world, [Edit(LegalParameterKey.Smmlv, Jan2026, 1_750_905m)]);

        Assert.Equal(new LegalParameterWriteResult(0, 0, 0, 1), result);
        Assert.Empty(world.Changes);
        Assert.Empty(world.Log);
    }

    [Fact]
    public async Task DeletingTheLastRowIsLastRow()
    {
        var world = new World();

        var error = await Assert.ThrowsAsync<HrDomainException>(
            () => ApplyAsync(world, [Edit(LegalParameterKey.HealthEmployeeRate, Jan2026, null)]));

        Assert.Equal("hr.legal_parameter.last_row", error.Code);
        Assert.Empty(world.Changes);
    }

    [Fact]
    public async Task DeletingTheOnlyBracketSetIsLastRow()
    {
        var world = new World();
        world.Brackets.RemoveAll(bracket => bracket.EffectiveFrom == Jan2026);
        world.Brackets.Add(new LegalParameterBracket(LegalBracketKey.SolidarityFund, Jan2026, 4m, 16m, 0.01m));

        var error = await Assert.ThrowsAsync<HrDomainException>(
            () => ApplyAsync(world, [], [new LegalBracketSetEdit(LegalBracketKey.SolidarityFund, Jan2026, null)]));

        Assert.Equal("hr.legal_parameter.last_row", error.Code);
    }

    [Fact]
    public async Task DeletingARowThatDoesNotExistIsKeyUnknown()
    {
        var world = new World();

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => ApplyAsync(world, [Edit(LegalParameterKey.Smmlv, new DateOnly(2030, 1, 1), null)]));

        Assert.Equal("hr.legal_parameter.key_unknown", error.Code);
        Assert.Contains("2030-01-01", error.Message);
    }

    [Fact]
    public async Task ARowInUseIsRejectedWithCounts()
    {
        var world = new World();
        world.Stamps =
        [
            new CalculatedPeriodStamp(Jan2026, CompanyA),
            new CalculatedPeriodStamp(new DateOnly(2026, 1, 16), CompanyA),
        ];

        var error = await Assert.ThrowsAsync<LegalParameterInUseException>(
            () => ApplyAsync(world, [Edit(LegalParameterKey.Smmlv, Jan2026, 1_800_000m)]));

        Assert.Equal("hr.legal_parameter.in_use", error.Code);
        Assert.Equal(2, error.ProblemExtensions["periodsAffected"]);
        Assert.Equal(1, error.ProblemExtensions["companiesAffected"]);
        Assert.Equal("2026-01-01", error.ProblemExtensions["from"]);
        Assert.Equal("2026-01-16", error.ProblemExtensions["to"]);
        Assert.Equal(1_750_905m, world.Values.Single(v => v.Key == LegalParameterKey.Smmlv && v.EffectiveFrom == Jan2026).Value);
        Assert.Empty(world.Changes);
        Assert.Empty(world.Log);
    }

    [Fact]
    public async Task ACarryInAUsedRangeIsRejected()
    {
        var world = new World();
        world.Stamps = [new CalculatedPeriodStamp(new DateOnly(2026, 1, 16), CompanyA)];

        var error = await Assert.ThrowsAsync<LegalParameterInUseException>(
            () => ApplyAsync(world, [Edit(LegalParameterKey.Smmlv, new DateOnly(2026, 1, 10), 1_750_905m)]));

        Assert.Equal("hr.legal_parameter.in_use", error.Code);
    }

    [Fact]
    public async Task ARowAfterTheLastCalculatedPeriodIsAccepted()
    {
        var world = new World();
        world.Stamps = [new CalculatedPeriodStamp(new DateOnly(2026, 1, 16), CompanyA)];

        var result = await ApplyAsync(world, [Edit(LegalParameterKey.Smmlv, new DateOnly(2026, 6, 1), 1_800_000m)]);

        Assert.Equal(1, result.Created);
    }

    [Fact]
    public async Task ReplacingASetRemovesTheOldBracketsAndRecordsBothSets()
    {
        var world = new World();
        var replacement = new LegalBracketInput[] { new(4m, 18m, 0.015m), new(18m, null, 0.025m) };

        var result = await ApplyAsync(
            world, [], [new LegalBracketSetEdit(LegalBracketKey.SolidarityFund, Jan2027, replacement)]);

        Assert.Equal(1, result.Created);
        Assert.Equal(2, world.Brackets.Count(b => b.EffectiveFrom == Jan2026));

        var updated = await ApplyAsync(
            world, [], [new LegalBracketSetEdit(LegalBracketKey.SolidarityFund, Jan2026, replacement)]);

        Assert.Equal(1, updated.Updated);
        var rows = world.Brackets.Where(b => b.EffectiveFrom == Jan2026).OrderBy(b => b.LowerBound).ToList();
        Assert.Equal([4m, 18m], rows.Select(b => b.LowerBound));
        Assert.Equal([0.015m, 0.025m], rows.Select(b => b.Rate));
        Assert.All(rows, row => Assert.Equal(Operator, row.UpdatedBy));
        var change = world.Changes.Single(c => c.Action == LegalParameterChangeAction.Updated);
        Assert.Equal("SolidarityFund", change.Key);
        Assert.Equal(
            "[{\"lowerBound\":4,\"upperBound\":16,\"rate\":0.01},{\"lowerBound\":16,\"upperBound\":25,\"rate\":0.02}]",
            change.OldValue);
        Assert.Equal(
            "[{\"lowerBound\":4,\"upperBound\":18,\"rate\":0.015},{\"lowerBound\":18,\"upperBound\":null,\"rate\":0.025}]",
            change.NewValue);
    }

    [Fact]
    public async Task AnExcelBatchSharesBatchIdAndFileName()
    {
        var world = new World();
        var batch = Guid.NewGuid();
        var origin = new LegalParameterChangeOrigin(LegalParameterChangeSource.Excel, batch, "enero-2027.xlsx");

        var result = await ApplyAsync(
            world,
            [Edit(LegalParameterKey.Smmlv, Jan2027, 2_000_000m), Edit(LegalParameterKey.HealthEmployeeRate, Jan2027, 0.04m)],
            origin: origin);

        Assert.Equal(2, result.Created);
        Assert.Equal(2, world.Changes.Count);
        Assert.All(world.Changes, change =>
        {
            Assert.Equal(LegalParameterChangeSource.Excel, change.Source);
            Assert.Equal(batch, change.BatchId);
            Assert.Equal("enero-2027.xlsx", change.FileName);
        });
    }

    [Fact]
    public async Task TheVersionIsBumpedOnceAfterSaving()
    {
        var world = new World();

        await ApplyAsync(
            world,
            [Edit(LegalParameterKey.Smmlv, Jan2027, 2_000_000m), Edit(LegalParameterKey.HealthEmployeeRate, Jan2027, 0.04m)],
            [new LegalBracketSetEdit(LegalBracketKey.SolidarityFund, Jan2027, [new LegalBracketInput(4m, 25m, 0.01m)])]);

        Assert.Equal(["Save", "Bump"], world.Log);
    }

    [Fact]
    public async Task AnInvalidValueWritesNothing()
    {
        var world = new World();

        var error = await Assert.ThrowsAsync<HrDomainException>(
            () => ApplyAsync(
                world,
                [Edit(LegalParameterKey.Smmlv, Jan2027, 2_000_000m), Edit(LegalParameterKey.HealthEmployeeRate, Jan2027, 12m)]));

        Assert.Equal("hr.legal_parameter.value_invalid", error.Code);
        Assert.Equal(3, world.Values.Count);
        Assert.Empty(world.Changes);
        Assert.Empty(world.Log);
    }

    [Fact]
    public async Task ARepeatedKeyAndDateInOneBatchIsRejected()
    {
        var world = new World();

        var error = await Assert.ThrowsAsync<HrDomainException>(
            () => ApplyAsync(
                world,
                [Edit(LegalParameterKey.Smmlv, Jan2027, 2_000_000m), Edit(LegalParameterKey.Smmlv, Jan2027, 2_100_000m)]));

        Assert.Equal("hr.legal_parameter.date_invalid", error.Code);
        Assert.Empty(world.Log);
    }

    [Fact]
    public async Task AnActorWithoutANameIsWrittenAsSinNombre()
    {
        var world = new World();
        world.ActorName = null;

        await ApplyAsync(world, [Edit(LegalParameterKey.Smmlv, Jan2027, 2_000_000m)]);

        Assert.Equal("Sin nombre", world.Changes.Single().ChangedByName);
    }

    // Con las ediciones del lote cerrando el rango, la primera fila toca un solo período; con rangos por edición (sin mirar el lote) tocaría dos.
    [Fact]
    public async Task ACloserRowInTheSameBatchShrinksTheRangeOfTheEarlierOne()
    {
        var world = new World();
        world.Stamps =
        [
            new CalculatedPeriodStamp(new DateOnly(2026, 1, 20), CompanyA),
            new CalculatedPeriodStamp(new DateOnly(2026, 3, 16), CompanyA),
        ];

        var error = await Assert.ThrowsAsync<LegalParameterInUseException>(
            () => ApplyAsync(
                world,
                [
                    Edit(LegalParameterKey.Smmlv, new DateOnly(2026, 1, 10), 1_800_000m),
                    Edit(LegalParameterKey.Smmlv, new DateOnly(2026, 2, 1), 1_900_000m),
                ]));

        Assert.Equal(1, error.ProblemExtensions["periodsAffected"]);
        Assert.Equal("2026-01-20", error.ProblemExtensions["from"]);
        Assert.Equal("2026-01-20", error.ProblemExtensions["to"]);
    }

    // Borrar la fila siguiente en el mismo lote alarga el rango de la anterior: toca los dos períodos, no uno.
    [Fact]
    public async Task DeletingTheNextRowInTheSameBatchGrowsTheRangeOfTheEarlierOne()
    {
        var world = new World();
        world.Values.Add(new LegalParameterValue(LegalParameterKey.Smmlv, new DateOnly(2026, 6, 1), 1_800_000m));
        world.Stamps =
        [
            new CalculatedPeriodStamp(new DateOnly(2026, 3, 16), CompanyA),
            new CalculatedPeriodStamp(new DateOnly(2026, 7, 16), CompanyA),
        ];

        var error = await Assert.ThrowsAsync<LegalParameterInUseException>(
            () => ApplyAsync(
                world,
                [
                    Edit(LegalParameterKey.Smmlv, Jan2026, 1_760_000m),
                    Edit(LegalParameterKey.Smmlv, new DateOnly(2026, 6, 1), null),
                ]));

        Assert.Equal(2, error.ProblemExtensions["periodsAffected"]);
        Assert.Equal("2026-03-16", error.ProblemExtensions["from"]);
        Assert.Equal("2026-07-16", error.ProblemExtensions["to"]);
    }

    [Fact]
    public async Task NoBumpWhenSaveChangesThrows()
    {
        var world = new World { ThrowOnSave = true };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ApplyAsync(world, [Edit(LegalParameterKey.Smmlv, Jan2027, 2_000_000m)]));

        Assert.Equal(["Save"], world.Log);
    }

    [Fact]
    public async Task TheInUseMessageNamesTheRange()
    {
        var world = new World();
        world.Stamps = [new CalculatedPeriodStamp(new DateOnly(2026, 1, 16), CompanyA)];

        var error = await Assert.ThrowsAsync<LegalParameterInUseException>(
            () => ApplyAsync(world, [Edit(LegalParameterKey.Smmlv, Jan2026, 1_800_000m)]));

        Assert.Contains("2026-01-16", error.Message);
    }

    [Fact]
    public void ChangeJsonNormalizesNumbersAndRoundTripsBrackets()
    {
        Assert.Equal("1750905", LegalParameterChangeJson.Number(1_750_905.000000m));
        Assert.Equal("0.04", LegalParameterChangeJson.Number(0.040000m));
        Assert.Equal(0.04m, LegalParameterChangeJson.ReadNumber("0.04"));
        Assert.Null(LegalParameterChangeJson.ReadNumber(null));

        var set = new LegalBracketInput[] { new(4m, 16m, 0.01m), new(16m, null, 0.012m) };
        var json = LegalParameterChangeJson.Brackets(set);

        Assert.Equal("[{\"lowerBound\":4,\"upperBound\":16,\"rate\":0.01},{\"lowerBound\":16,\"upperBound\":null,\"rate\":0.012}]", json);
        Assert.Equal(set, LegalParameterChangeJson.ReadBrackets(json));
        // jsonb reordena las propiedades: la lectura no puede depender del orden.
        Assert.Equal(
            [new LegalBracketInput(4m, 16m, 0.01m)],
            LegalParameterChangeJson.ReadBrackets("[{\"rate\": 0.01, \"lowerBound\": 4, \"upperBound\": 16}]"));
        Assert.Null(LegalParameterChangeJson.ReadBrackets(null));
    }

    [Fact]
    public void TheOperatorPermissionsHaveTheirNames()
    {
        Assert.Equal("operator.legal_parameters.read", HrOperatorPermissions.LegalParametersRead);
        Assert.Equal("operator.legal_parameters.manage", HrOperatorPermissions.LegalParametersManage);
    }

    [Fact]
    public void TheInUseExceptionIsAConflictWithProblemExtensions()
    {
        var error = new LegalParameterInUseException(
            new LegalParameterUsageSummary(2, 1, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 16)));

        Assert.IsAssignableFrom<ResourceConflictException>(error);
        Assert.IsAssignableFrom<IHasProblemExtensions>(error);
    }
}

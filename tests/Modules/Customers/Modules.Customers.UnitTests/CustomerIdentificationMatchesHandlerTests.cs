using BuildingBlocks.Application;
using FluentValidation;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using static Modules.Customers.UnitTests.EngagementTestBed;

namespace Modules.Customers.UnitTests;

/// <summary>CD-03 (spec 2026-10-24 §3.2): el aviso de coincidencias de documento. Autoriza antes de validar, no consulta si el
/// número normalizado queda vacío y clasifica cada fila por tipo y por forma del número.</summary>
public sealed class CustomerIdentificationMatchesHandlerTests
{
    private const string StoredNumber = "92-1396892";
    private readonly FakeCustomerDuplicateQueries _queries = new();

    private FindCustomerIdentificationMatchesHandler CreateHandler(Guid? tenant = null, params string[] permissions) =>
        new(_queries, new FindCustomerIdentificationMatchesValidator(),
            new Context(tenant ?? TenantId, UserA, permissions.Length == 0 ? [CustomersPermissions.CustomerRead] : permissions));

    private static Customer Candidate() =>
        Customer.Create(
            CustomerId.New(), TenantId, "MED-0001", "GYSELYS VIVAS PACHECO", null, null,
            new CustomerIdentification { Type = IdentificationType.Nit, Number = StoredNumber },
            null,
            new CustomerContactInfo
            {
                Phone = "310 935 2187",
                Email = "compras@verde.co",
                Address = "Calle 10 # 45-12",
                Country = Customer.ColombiaCountryCode,
                CityId = Guid.NewGuid()
            },
            new CustomerCommercialInfo { ClassificationId = new ClientClassificationId(Guid.NewGuid()) },
            Now);

    [Theory]
    [InlineData("NIT", "92-1396892", "SameIdentification")]
    [InlineData("CE", "92-1396892", "OtherType")]
    [InlineData("NIT", "921396892", "SameTypeOtherFormat")]
    [InlineData(null, "92-1396892", "OtherType")]
    public async Task EachCandidateGetsItsMatch(string? type, string number, string expected)
    {
        var candidate = Candidate();
        _queries.Candidates.Add(candidate);

        var response = await CreateHandler().HandleAsync(
            new FindCustomerIdentificationMatchesQuery(TenantId, number, type, null), TestContext.Current.CancellationToken);

        var item = Assert.Single(response.Items);
        Assert.Equal(expected, item.Match);
        Assert.Equal(candidate.Id.Value, item.Id);
        Assert.Equal("NIT", item.IdentificationType);
        Assert.Equal(StoredNumber, item.IdentificationNumber);
        Assert.Equal(candidate.Cuc, item.Cuc);
        Assert.True(item.IsComplete);
        Assert.True(item.IsActive);
    }

    [Fact]
    public async Task AnInputThatNormalizesToNothingMatchesNoOne()
    {
        _queries.Candidates.Add(Candidate());

        var response = await CreateHandler().HandleAsync(
            new FindCustomerIdentificationMatchesQuery(TenantId, "---", "NIT", null), TestContext.Current.CancellationToken);

        Assert.Empty(response.Items);
        Assert.Empty(_queries.FindCalls);
    }

    [Fact]
    public async Task AnotherTenantIs403BeforeValidating()
    {
        await Assert.ThrowsAsync<RequestForbiddenException>(() => CreateHandler(OtherTenantId).HandleAsync(
            new FindCustomerIdentificationMatchesQuery(TenantId, "", "NO-EXISTE", null), TestContext.Current.CancellationToken));
        Assert.Empty(_queries.FindCalls);
    }

    [Fact]
    public async Task ItAsksForTwentyAndPassesTheExclusion()
    {
        var excluded = Guid.NewGuid();

        await CreateHandler().HandleAsync(
            new FindCustomerIdentificationMatchesQuery(TenantId, "92-1396892", "NIT", excluded), TestContext.Current.CancellationToken);

        var call = Assert.Single(_queries.FindCalls);
        Assert.Equal((TenantId, "921396892", (Guid?)excluded, CustomerIdentificationMatching.Take), call);
        Assert.Equal(20, call.Take);
    }

    [Theory]
    [InlineData("", "NIT", "Number")]
    [InlineData("   ", null, "Number")]
    [InlineData("123456789012345678901234567890123", "NIT", "Number")]
    [InlineData("123", "RUT", "Type")]
    public async Task InvalidInputIsAValidationErrorOnItsField(string number, string? type, string field)
    {
        var error = await Assert.ThrowsAsync<ValidationException>(() => CreateHandler().HandleAsync(
            new FindCustomerIdentificationMatchesQuery(TenantId, number, type, null), TestContext.Current.CancellationToken));

        Assert.Contains(error.Errors, failure => failure.PropertyName == field);
        Assert.Empty(_queries.FindCalls);
    }
}

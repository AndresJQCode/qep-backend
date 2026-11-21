using FluentValidation;
using Modules.Hr.Application;

namespace Modules.Hr.UnitTests;

public sealed class HrSettingsValidatorsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SiteRequest Site(Guid? companyId = null, string? name = "Principal") => new(
        companyId ?? Guid.NewGuid(), name, null, null, null);

    private static WorkplaceRequest Workplace(string? ciiu = null, string? riskClass = "III") => new(
        "Bodega", Guid.NewGuid(), ciiu, riskClass, null);

    // El contrato de errores usa camelCase: ValidateAndThrowAsync pasa por HrValidator, que reescribe la clave.
    private static async Task<string[]> FailedFieldsAsync<T>(IValidator<T> validator, T request)
    {
        var exception = await Assert.ThrowsAsync<ValidationException>(() => validator.ValidateAndThrowAsync(request, Ct));
        return exception.Errors.Select(error => error.PropertyName).Distinct().ToArray();
    }

    [Fact]
    public async Task ABlankNameFails()
    {
        Assert.Equal(["name"], await FailedFieldsAsync(new SiteRequestValidator(), Site(name: "  ")));
        Assert.Equal(["name"], await FailedFieldsAsync(new NamedSettingRequestValidator(), new NamedSettingRequest(null)));
        Assert.Equal(["name"], await FailedFieldsAsync(new WorkplaceRequestValidator(), Workplace() with { Name = "" }));
    }

    [Fact]
    public async Task ASiteWithoutCompanyFails()
    {
        Assert.Equal(["companyId"], await FailedFieldsAsync(new SiteRequestValidator(), Site() with { CompanyId = null }));
        Assert.Equal(["companyId"], await FailedFieldsAsync(new SiteRequestValidator(), Site() with { CompanyId = Guid.Empty }));
    }

    [Fact]
    public async Task AValidSitePasses() =>
        await new SiteRequestValidator().ValidateAndThrowAsync(Site(), Ct);

    [Fact]
    public async Task ACiiuCodeMustBeFourDigits()
    {
        Assert.Equal(["ciiuCode"], await FailedFieldsAsync(new WorkplaceRequestValidator(), Workplace(ciiu: "47A1")));
        await new WorkplaceRequestValidator().ValidateAndThrowAsync(Workplace(ciiu: "4711"), Ct);
        await new WorkplaceRequestValidator().ValidateAndThrowAsync(Workplace(ciiu: null), Ct);
    }

    [Fact]
    public async Task AnUnknownRiskClassFails() =>
        Assert.Equal(["riskClass"], await FailedFieldsAsync(new WorkplaceRequestValidator(), Workplace(riskClass: "VI")));

    [Fact]
    public async Task ARiskClassNumberIsNotAName()
    {
        Assert.Equal(["riskClass"], await FailedFieldsAsync(new WorkplaceRequestValidator(), Workplace(riskClass: "3")));
        Assert.Equal(["riskClass"], await FailedFieldsAsync(new WorkplaceRequestValidator(), Workplace(riskClass: null)));
    }

    [Fact]
    public void EnumsParseByExactNameOnly()
    {
        Assert.True(HrEnums.TryParse<Modules.Hr.Domain.ArlRiskClass>("III", out var value));
        Assert.Equal(Modules.Hr.Domain.ArlRiskClass.III, value);
        Assert.False(HrEnums.TryParse<Modules.Hr.Domain.ArlRiskClass>("iii", out _));
        Assert.False(HrEnums.TryParse<Modules.Hr.Domain.ArlRiskClass>("2", out _));
        Assert.False(HrEnums.TryParse<Modules.Hr.Domain.ArlRiskClass>(null, out _));
    }
}

using FluentValidation;
using Modules.Hr.Application;

namespace Modules.Hr.UnitTests;

public sealed class HrValidatorTests
{
    private sealed record ProbeContract(DateOnly? EndDate);

    private sealed record Probe(string FirstName, ProbeContract Contract);

    private sealed class ProbeValidator : HrValidator<Probe>
    {
        public ProbeValidator()
        {
            RuleFor(probe => probe.FirstName).NotEmpty();
            RuleFor(probe => probe.Contract.EndDate).NotEmpty();
        }
    }

    // El contrato de errores del frontend usa camelCase por segmento: `firstName`, `contract.endDate`.
    [Fact]
    public async Task NestedAndTopLevelFailuresUseCamelCaseKeys()
    {
        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            new ProbeValidator().ValidateAndThrowAsync(new Probe("", new ProbeContract(null)), TestContext.Current.CancellationToken));

        Assert.Equal(["firstName", "contract.endDate"], exception.Errors.Select(error => error.PropertyName));
    }
}

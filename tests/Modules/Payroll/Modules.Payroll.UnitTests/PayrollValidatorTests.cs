using FluentValidation;
using Modules.Payroll.Application;

namespace Modules.Payroll.UnitTests;

public sealed class PayrollValidatorTests
{
    private sealed record ProbeLine(decimal Hours);

    private sealed record Probe(IReadOnlyList<ProbeLine> Overtime);

    private sealed class ProbeValidator : PayrollValidator<Probe>
    {
        public ProbeValidator() =>
            RuleForEach(probe => probe.Overtime).ChildRules(line =>
                line.RuleFor(item => item.Hours).GreaterThan(0));
    }

    // El contrato de errores usa camelCase por segmento y conserva el índice: `overtime[0].hours`.
    [Fact]
    public async Task NestedListFailuresUseCamelCaseIndexedKeys()
    {
        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            new ProbeValidator().ValidateAndThrowAsync(new Probe([new(0)]), TestContext.Current.CancellationToken));

        Assert.Equal("overtime[0].hours", Assert.Single(exception.Errors).PropertyName);
    }
}

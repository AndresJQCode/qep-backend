using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

/// <summary>Fase 4a (plan G11): el validador ataja lo que la guarda del dominio rechazaría, como <c>validation.failed</c> con el campo
/// en <c>errors</c>. El tope real de los días (el término del tipo) lo juzga el dominio.</summary>
public sealed class ExtendTicketValidatorTests
{
    private const string Reason = "Falta respuesta del proveedor";

    private static List<string> Errors(int? businessDays, string? reason) =>
        new ExtendTicketValidator()
            .Validate(ExtendTicketCommand.From(
                Guid.CreateVersion7(), Guid.CreateVersion7(), new ExtendTicketRequest(businessDays, reason), expectedVersion: 1))
            .Errors.Select(error => error.PropertyName)
            .Distinct()
            .ToList();

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(SupportTicketType.MaxTermBusinessDays)]
    public void BusinessDaysAreOneTo365(int businessDays)
    {
        Assert.Empty(Errors(businessDays, Reason));
        Assert.Equal(["BusinessDays"], Errors(null, Reason));
        Assert.Equal(["BusinessDays"], Errors(0, Reason));
        Assert.Equal(["BusinessDays"], Errors(-1, Reason));
        Assert.Equal(["BusinessDays"], Errors(SupportTicketType.MaxTermBusinessDays + 1, Reason));
    }

    [Fact]
    public void TheReasonIsOneTo1000Trimmed()
    {
        Assert.Empty(Errors(5, "x"));
        Assert.Empty(Errors(5, "  " + new string('a', SupportTicket.ExtensionReasonMaxLength) + "  "));
        Assert.Equal(["Reason"], Errors(5, null));
        Assert.Equal(["Reason"], Errors(5, ""));
        Assert.Equal(["Reason"], Errors(5, "   "));
        Assert.Equal(["Reason"], Errors(5, new string('a', SupportTicket.ExtensionReasonMaxLength + 1)));
    }
}

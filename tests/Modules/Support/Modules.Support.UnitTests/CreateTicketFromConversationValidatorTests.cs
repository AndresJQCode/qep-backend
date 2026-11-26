using Modules.Support.Application;

namespace Modules.Support.UnitTests;

/// <summary>
/// El validador del alta desde una conversación cubre las guardas de <c>SupportTicket.CreateFromConversation</c> que vienen del cuerpo
/// (tipo, asunto, descripción, responsable): el solicitante y el canal no viajan, salen de la conversación (plan E10).
/// </summary>
public sealed class CreateTicketFromConversationValidatorTests
{
    private static List<string> Errors(CreateTicketFromConversationRequest request) =>
        new CreateTicketFromConversationValidator()
            .Validate(CreateTicketFromConversationCommand.From(Guid.CreateVersion7(), Guid.CreateVersion7(), request))
            .Errors.Select(error => error.PropertyName)
            .ToList();

    private static CreateTicketFromConversationRequest Valid() =>
        new(Guid.CreateVersion7(), "Pedido sin llegar", "El pedido de la semana pasada no ha llegado.", null);

    [Fact]
    public void AValidRequestPasses()
    {
        Assert.Empty(Errors(Valid()));
        Assert.Empty(Errors(Valid() with { AssigneeMemberId = Guid.CreateVersion7() }));
    }

    [Fact]
    public void EachMissingOrOversizedFieldIsNamed()
    {
        Assert.Equal(["TicketTypeId"], Errors(Valid() with { TicketTypeId = null }));
        Assert.Equal(["Subject"], Errors(Valid() with { Subject = "   " }));
        Assert.Equal(["Subject"], Errors(Valid() with { Subject = new string('a', 201) }));
        Assert.Equal(["Description"], Errors(Valid() with { Description = null }));
        Assert.Equal(["Description"], Errors(Valid() with { Description = new string('a', 5001) }));
        Assert.Equal(["AssigneeMemberId"], Errors(Valid() with { AssigneeMemberId = Guid.Empty }));
    }
}

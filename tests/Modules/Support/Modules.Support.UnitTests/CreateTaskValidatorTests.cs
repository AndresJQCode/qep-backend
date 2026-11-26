using Modules.Support.Application;

namespace Modules.Support.UnitTests;

/// <summary>
/// El validador del alta de una tarea (spec 2026-11-22 §5.2): asunto, descripción y responsable obligatorios; la prioridad es opcional
/// pero conocida; la fecha es <c>yyyy-MM-dd</c> o ausente. Que no sea anterior a hoy lo juzga el dominio, con la zona del tenant.
/// </summary>
public sealed class CreateTaskValidatorTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private static CreateTaskCommand Command(
        string? subject = "Cuadrar el inventario",
        string? description = "Revisar las diferencias de octubre.",
        Guid? assignee = null,
        string? priority = null,
        string? dueOn = null) =>
        CreateTaskCommand.From(Tenant, new CreateTaskRequest(subject, description, assignee ?? Guid.CreateVersion7(), priority, dueOn));

    private static List<string> Errors(CreateTaskCommand command) =>
        new CreateTaskValidator().Validate(command).Errors.Select(error => error.PropertyName).ToList();

    [Fact]
    public void AValidTaskPasses()
    {
        Assert.Empty(Errors(Command()));
        Assert.Empty(Errors(Command(priority: "Urgent", dueOn: "2026-11-30")));
    }

    [Fact]
    public void TheBodyNeedsSubjectDescriptionAndAssignee()
    {
        Assert.Equal(["Subject"], Errors(Command(subject: "  ")));
        Assert.Equal(["Subject"], Errors(Command(subject: null)));
        Assert.Equal(["Description"], Errors(Command(description: "")));
        Assert.Equal(["Description"], Errors(Command(description: null)));
        var withoutAssignee = CreateTaskCommand.From(Tenant, new CreateTaskRequest("Asunto", "Texto", null, null, null));
        Assert.Equal(["AssigneeMemberId"], Errors(withoutAssignee));
        Assert.Equal(["AssigneeMemberId"], Errors(Command(assignee: Guid.Empty)));
    }

    [Theory]
    [InlineData("2026-11-30", true)]
    [InlineData(null, true)]
    [InlineData("30/11/2026", false)]
    [InlineData("2026-13-01", false)]
    [InlineData("2026-11-30T10:00:00", false)]
    [InlineData("", false)]
    [InlineData("mañana", false)]
    public void ADueDateMustBeAnIsoDate(string? dueOn, bool valid) =>
        Assert.Equal(valid, Errors(Command(dueOn: dueOn)).Count == 0);

    [Fact]
    public void PriorityIsOptionalButMustBeKnown()
    {
        Assert.Empty(Errors(Command(priority: null)));
        Assert.Empty(Errors(Command(priority: "Low")));
        Assert.Equal(["Priority"], Errors(Command(priority: "Critical")));
        Assert.Equal(["Priority"], Errors(Command(priority: "high")));
    }
}

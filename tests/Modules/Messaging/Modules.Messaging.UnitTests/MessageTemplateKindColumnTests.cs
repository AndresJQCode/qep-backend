using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.UnitTests;

/// <summary>
/// <c>message_templates.kind</c> guarda el nombre del enum (<c>HasConversion&lt;string&gt;</c>), así que su ancho tiene que alcanzar
/// para el nombre más largo. <c>SupportFormalReply</c> (18) entró con la columna en 16 y todo aprovisionamiento o fila de ese tipo
/// moría con <c>22001 value too long</c>: el compilador no lo ve y el <c>CHECK</c> sí lo acepta.
/// </summary>
public sealed class MessageTemplateKindColumnTests
{
    [Fact]
    public void EveryKindNameFitsTheColumn()
    {
        using var context = new MessagingDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;
        var column = model.FindEntityType(typeof(MessageTemplate))!.FindProperty(nameof(MessageTemplate.Kind))!;

        var longest = Enum.GetNames<MessageTemplateKind>().MaxBy(name => name.Length)!;

        Assert.True(longest.Length <= column.GetMaxLength(), $"'{longest}' ({longest.Length}) does not fit kind ({column.GetMaxLength()}).");
    }
}

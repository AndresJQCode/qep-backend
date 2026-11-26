using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using Npgsql;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-17, fix round 2 del bloque C: una imagen de encabezado pertenece a una sola plantilla
/// (<c>UX_message_templates_header_image</c>, parcial sobre <c>header_image_file_id IS NOT NULL</c>). Dos creaciones con el mismo archivo
/// a la vez: la que pierde choca acá al guardar, y el guardado lo traduce, por nombre de índice y no sólo por 23505, a
/// <c>header_image_rejected</c> sobre <c>headerImageFileId</c>. Su compensación ve la fila de la ganadora y no libera la imagen.</summary>
public sealed class HeaderImageTakenTests
{
    private static DbUpdateException Violation(string sqlState, string? constraint) =>
        new("save failed", new PostgresException("dup", "ERROR", "ERROR", sqlState, constraintName: constraint));

    [Fact]
    public void TheHeaderImageIndexIsATakenImage() =>
        Assert.True(MessagingSaveChanges.IsHeaderImageTaken(Violation(PostgresErrorCodes.UniqueViolation, "UX_message_templates_header_image")));

    // Spec 2026-11-20: la carrera de dos cambios de imagen con el mismo archivo choca en el índice de las retiradas.
    [Fact]
    public void TheRetiredImageIndexIsATakenImageToo() =>
        Assert.True(MessagingSaveChanges.IsHeaderImageTaken(Violation(PostgresErrorCodes.UniqueViolation, MessagingDbContext.MessageTemplateRetiredHeaderImageFileIndex)));

    [Theory]
    [InlineData(PostgresErrorCodes.UniqueViolation, "UX_message_templates_name_language")]
    [InlineData(PostgresErrorCodes.UniqueViolation, null)]
    [InlineData(PostgresErrorCodes.CheckViolation, "UX_message_templates_header_image")]
    public void AnyOtherViolationIsNot(string sqlState, string? constraint) =>
        Assert.False(MessagingSaveChanges.IsHeaderImageTaken(Violation(sqlState, constraint)));

    [Fact]
    public void ATakenImageIsHeaderImageRejectedOnTheField()
    {
        var violation = Violation(PostgresErrorCodes.UniqueViolation, "UX_message_templates_header_image");

        var error = MessagingSaveChanges.HeaderImageTaken(violation);

        Assert.Equal(MessagingErrorCodes.TemplateHeaderImageRejected, error.Code);
        Assert.Equal([TemplateMessages.HeaderImage], error.FieldErrors["headerImageFileId"]);
        Assert.Same(violation, error.InnerException);
    }

    [Fact]
    public void TheIndexNameIsTheOneOfTheModel() =>
        Assert.Equal("UX_message_templates_header_image", MessagingDbContext.MessageTemplateHeaderImageIndex);
}

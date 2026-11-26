using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-20: cambiar la imagen del encabezado retira la anterior (no se libera: sigue contando como referenciada).</summary>
public sealed class MessageTemplateReplaceHeaderImageTests
{
    private static readonly DateTimeOffset Created = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Created.AddHours(2);

    private static MessageTemplate NewTemplate() =>
        MessageTemplate.Pending(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "555", "promo_imagen", "es", "MARKETING", "NAMED", "APPROVED", "[]",
            MessageTemplateKind.Free, Guid.CreateVersion7(), Created);

    [Fact]
    public void ReplacingWithoutAnImageAssignsAndRetiresNothing()
    {
        var template = NewTemplate();
        var fileId = Guid.CreateVersion7();

        var retired = template.ReplaceHeaderImage(fileId, "https://public.test/new.png", Now);

        Assert.Null(retired);
        Assert.Equal(fileId, template.HeaderImageFileId);
        Assert.Equal("https://public.test/new.png", template.HeaderImageUrl);
    }

    [Fact]
    public void ReplacingAnImageRetiresThePreviousOne()
    {
        var template = NewTemplate();
        var oldId = Guid.CreateVersion7();
        template.AttachHeaderImage(oldId, "https://public.test/old.png", Created);
        var newId = Guid.CreateVersion7();

        var retired = template.ReplaceHeaderImage(newId, "https://public.test/new.png", Now);

        Assert.NotNull(retired);
        Assert.Equal((template.TenantId, template.Id, oldId, "https://public.test/old.png", Now),
            (retired.TenantId, retired.TemplateId, retired.FileId, retired.PublicUrl, retired.RetiredAt));
        Assert.NotEqual(Guid.Empty, retired.Id);
        Assert.Equal(newId, template.HeaderImageFileId);
        Assert.Equal("https://public.test/new.png", template.HeaderImageUrl);
        Assert.Equal(Now, template.UpdatedAt);
    }

    [Fact]
    public void ReplacingWithTheSameFileThrows()
    {
        var template = NewTemplate();
        var fileId = Guid.CreateVersion7();
        template.AttachHeaderImage(fileId, "https://public.test/a.png", Created);

        Assert.Throws<ArgumentException>(() => template.ReplaceHeaderImage(fileId, "https://public.test/b.png", Now));
    }

    [Fact]
    public void ReplacingWithAnEmptyFileIdThrows()
    {
        var template = NewTemplate();

        Assert.Throws<ArgumentException>(() => template.ReplaceHeaderImage(Guid.Empty, "https://public.test/a.png", Now));
    }

    // Mismas guardas de la URL que AttachHeaderImage: null lanza ArgumentNullException (subtipo), vacía y de sólo espacios ArgumentException.
    [Fact]
    public void ReplacingWithANullUrlThrows()
    {
        var template = NewTemplate();

        Assert.ThrowsAny<ArgumentException>(() => template.ReplaceHeaderImage(Guid.CreateVersion7(), null!, Now));
        Assert.Null(template.HeaderImageFileId);
    }

    [Fact]
    public void ReplacingWithAWhitespaceUrlThrows()
    {
        var template = NewTemplate();

        Assert.Throws<ArgumentException>(() => template.ReplaceHeaderImage(Guid.CreateVersion7(), "   ", Now));
        Assert.Null(template.HeaderImageFileId);
    }

    [Fact]
    public void ReplacingWithAUrlOverTheMaxLengthThrows()
    {
        var template = NewTemplate();
        var tooLong = new string('a', MessageTemplate.HeaderImageUrlMaxLength + 1);

        Assert.Throws<ArgumentException>(() => template.ReplaceHeaderImage(Guid.CreateVersion7(), tooLong, Now));
        Assert.Null(template.HeaderImageFileId);
    }

    [Fact]
    public void ReplacingWithAUrlAtTheMaxLengthIsAccepted()
    {
        var template = NewTemplate();
        var atLimit = new string('a', MessageTemplate.HeaderImageUrlMaxLength);

        template.ReplaceHeaderImage(Guid.CreateVersion7(), atLimit, Now);

        Assert.Equal(atLimit, template.HeaderImageUrl);
    }
}

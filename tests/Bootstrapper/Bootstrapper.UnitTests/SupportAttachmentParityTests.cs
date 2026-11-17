using Modules.Storage.Application;
using Modules.Support.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Plan F34; owner, 2026-11-15: los tipos de los adjuntos del formulario público de Soporte son un subconjunto de los de Storage. Support
/// no ve a Storage (<c>SupportLayerTests</c>), así que <see cref="SupportPublicAttachmentRules"/> lleva su lista; esta prueba, que ve los
/// dos módulos, exige que cada extensión y cada tipo de Soporte los acepte <see cref="FileUploadPolicy"/>, y con eso los inspecciona
/// <c>FileContentInspector</c> (los magic bytes cubren toda extensión de esa política). Soporte puede aceptar menos, nunca más: un tipo que
/// Storage no acepta respondería 422 al abrir aunque la página lo ofrezca.
/// </summary>
public sealed class SupportAttachmentParityTests
{
    [Fact]
    public void EverySupportTypeIsAcceptedByStorage()
    {
        var storage = FileUploadPolicy.AllowedMimeTypesByExtension;

        foreach (var (extension, mimeTypes) in SupportPublicAttachmentRules.AllowedMimeTypesByExtension)
        {
            Assert.True(storage.ContainsKey(extension), $"Storage does not accept '{extension}'.");
            Assert.All(mimeTypes, mime => Assert.Contains(mime, storage[extension], StringComparer.OrdinalIgnoreCase));
        }

        Assert.All(
            SupportPublicAttachmentRules.MimeTypes,
            mime => Assert.Contains(mime, storage.Values.SelectMany(values => values), StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheSupportLimitsFitInsideStorage()
    {
        Assert.True(SupportPublicAttachmentRules.MaxSizeBytes <= FileUploadPolicy.MaxSizeBytes);
        // El tope del nombre es el de FileUploadPolicy y el de la columna name de storage.file_resources.
        Assert.Equal(260, SupportPublicAttachmentRules.NameMaxLength);
    }
}

using Modules.Storage.Application;
using Modules.Support.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Plan F34: los tipos de los adjuntos del formulario público de Soporte son los de Storage. Support no ve a Storage
/// (<c>SupportLayerTests</c>), así que <see cref="SupportPublicAttachmentRules"/> lleva su copia; esta prueba, que ve los dos módulos,
/// las ata. Si Storage suma o quita un tipo, ésta se pone roja y se decide si el formulario lo acepta.
/// </summary>
public sealed class SupportAttachmentParityTests
{
    [Fact]
    public void TheTypesMatchTheStoragePolicy()
    {
        var storage = FileUploadPolicy.AllowedMimeTypesByExtension;
        var support = SupportPublicAttachmentRules.AllowedMimeTypesByExtension;

        Assert.Equal(
            storage.Keys.Select(key => key.ToLowerInvariant()).Order(),
            support.Keys.Select(key => key.ToLowerInvariant()).Order());
        foreach (var (extension, mimeTypes) in storage)
        {
            Assert.Equal(
                mimeTypes.Select(mime => mime.ToLowerInvariant()).Order(),
                support[extension].Select(mime => mime.ToLowerInvariant()).Order());
        }
    }

    [Fact]
    public void TheSupportLimitsFitInsideStorage()
    {
        Assert.True(SupportPublicAttachmentRules.MaxSizeBytes <= FileUploadPolicy.MaxSizeBytes);
        // El tope del nombre es el de FileUploadPolicy y el de la columna name de storage.file_resources.
        Assert.Equal(260, SupportPublicAttachmentRules.NameMaxLength);
    }
}

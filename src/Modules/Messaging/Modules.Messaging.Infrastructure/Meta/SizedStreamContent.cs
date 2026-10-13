using System.Net;

namespace Modules.Messaging.Infrastructure.Meta;

/// <summary>
/// El cuerpo de la subida a Meta: un stream que se lee una sola vez y cuyo largo se conoce de antes. <c>StreamContent</c> sólo
/// mide streams que se pueden reposicionar; con uno de R2 devolvería «largo desconocido» y el multipart entero saldría en
/// <c>Transfer-Encoding: chunked</c>, que Graph no está verificado que acepte. Con el largo declarado sale con
/// <c>Content-Length</c>. No cierra el stream: es de quien lo abrió.
/// </summary>
internal sealed class SizedStreamContent(Stream source, long length) : HttpContent
{
    private const int BufferSize = 81920;

    protected override bool TryComputeLength(out long computed)
    {
        computed = length;
        return true;
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await source.CopyToAsync(stream, BufferSize, cancellationToken);
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);
}

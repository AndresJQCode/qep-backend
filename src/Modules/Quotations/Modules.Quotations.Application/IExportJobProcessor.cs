using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Arma y sube el archivo de un tipo de exportación (D7). Uno por <see cref="ExportJobKind"/>;
/// el runner despacha por <see cref="Kind"/>.
///
/// Contrato de fallos (D11): <see cref="ExportJobDefinitiveException"/> para lo que reintentar
/// no arregla; cualquier otra excepción es transitoria y vuelve a la cola con backoff.
/// </summary>
public interface IExportJobProcessor
{
    ExportJobKind Kind { get; }

    Task<ExportJobResult> ProcessAsync(ExportJob job, CancellationToken cancellationToken);
}

/// <summary>Lo que el correo necesita decir: archivo, filas, enlace y hasta cuándo sirve.</summary>
public sealed record ExportJobResult(
    string FileName,
    int RowCount,
    string DownloadUrl,
    DateTimeOffset ExpiresAt);

/// <summary>Un fallo que no se arregla reintentando: filtros ilegibles, cero filas al procesar.</summary>
public sealed class ExportJobDefinitiveException(string message, Exception? innerException = null)
    : Exception(message, innerException);

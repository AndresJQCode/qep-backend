using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;

namespace Modules.Quotations.Infrastructure.Seed;

/// <summary>
/// La mitad de Quotations de la semilla de arranque: el formato del numero de pedido del tenant
/// sembrado. El cliente trae su propia serie, asi que sus pedidos salen como <c>PW234235</c> y no
/// como <c>PED-2026-0001</c> — el caso canonico del README (§ Numeracion de documentos por tenant).
///
/// Solo el formato, no el consecutivo: <c>order_number_counters</c> no se toca porque no hay un
/// numero de arranque conocido, y la serie sigue desde donde este. Tampoco se configura
/// <c>quotation</c>: las cotizaciones siguen con el default.
///
/// Idempotente **por (tenant, tipo)**, que es la clave primaria de la tabla. Solo crea: si ya hay
/// fila de pedidos para el tenant, se deja tal cual, aunque no sea la de la semilla. La tabla se
/// configura a mano con el runbook del README, y un reinicio de pod no puede pisar lo que alguien
/// decidio con SQL.
/// </summary>
public static class QuotationsSeeder
{
    private const string OrderDocumentType = "order";

    /// <summary>
    /// Pasa por <c>DocumentNumberFormat.Create</c> aunque los valores sean constantes: si alguien
    /// los cambia por uno que el CHECK rechaza, el error sale con el codigo del dominio y en la
    /// primera linea del arranque, no como una <c>PostgresException</c> al guardar.
    /// </summary>
    private static readonly DocumentNumberFormat OrderFormat =
        DocumentNumberFormat.Create("PW", includeYear: false, yearSeparator: string.Empty, minDigits: 1);

    /// <summary>Crea el formato de pedidos del tenant si todavia no tiene uno.</summary>
    public static async Task SeedOrderNumberingAsync(
        this IServiceProvider services,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();

        var alreadyConfigured = await dbContext.DocumentNumberingFormats.AnyAsync(
            format => format.TenantId == tenantId && format.DocumentType == OrderDocumentType,
            cancellationToken);
        if (alreadyConfigured)
        {
            return;
        }

        dbContext.DocumentNumberingFormats.Add(new DocumentNumberingFormat
        {
            TenantId = tenantId,
            DocumentType = OrderDocumentType,
            Prefix = OrderFormat.Prefix,
            IncludeYear = OrderFormat.IncludeYear,
            YearSeparator = OrderFormat.YearSeparator,
            MinDigits = OrderFormat.MinDigits,
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

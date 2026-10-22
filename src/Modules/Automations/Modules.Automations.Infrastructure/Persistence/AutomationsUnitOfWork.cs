using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Automations.Application;
using Modules.Automations.Domain;
using Npgsql;

namespace Modules.Automations.Infrastructure.Persistence;

/// <summary>Copia de <c>PipelineUnitOfWork</c>: cada 23505 se traduce por el nombre del índice, nunca sólo por SqlState (CLAUDE.md).
/// Nombre de chatbot o de plantilla repetido y conexión ya ocupada → 409; la versión vieja y dos publicaciones que proponen el mismo
/// número (<c>IX_chatbot_versions_chatbot_number</c>) → <c>concurrency.conflict</c> (412). Traducido acá porque Application no
/// referencia Npgsql.</summary>
internal sealed class AutomationsUnitOfWork(AutomationsDbContext dbContext) : IAutomationsUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Antes que la rama de DbUpdateException: DbUpdateConcurrencyException hereda de ella.
        catch (DbUpdateConcurrencyException exception)
        {
            throw Concurrency(exception);
        }
        catch (DbUpdateException exception) when (IsUnique(exception, AutomationsDbContext.ChatbotNameIndex))
        {
            throw new ResourceConflictException(AutomationsErrorCodes.ChatbotNameTaken, "Ya existe un chatbot con ese nombre.", exception);
        }
        catch (DbUpdateException exception) when (IsUnique(exception, AutomationsDbContext.ActiveConnectionIndex))
        {
            throw new ResourceConflictException(
                AutomationsErrorCodes.ChatbotConnectionTaken, "Otro chatbot activo ya atiende esa conexión.", exception);
        }
        catch (DbUpdateException exception) when (IsUnique(exception, AutomationsDbContext.TemplateNameIndex))
        {
            throw new ResourceConflictException(AutomationsErrorCodes.TemplateNameTaken, "Ya existe una plantilla con ese nombre.", exception);
        }
        catch (DbUpdateException exception) when (IsUnique(exception, AutomationsDbContext.VersionNumberIndex))
        {
            throw Concurrency(exception);
        }
    }

    private static RequestConcurrencyException Concurrency(Exception inner) =>
        new("concurrency.conflict", "The chatbot changed while the operation was being committed.", inner);

    private static bool IsUnique(DbUpdateException exception, string indexName) =>
        exception.InnerException is PostgresException postgres &&
        postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
        string.Equals(postgres.ConstraintName, indexName, StringComparison.Ordinal);
}

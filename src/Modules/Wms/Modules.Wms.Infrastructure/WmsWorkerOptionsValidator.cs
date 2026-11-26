using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using Modules.Wms.Application;

namespace Modules.Wms.Infrastructure;

// Falla rápido al arrancar (ValidateOnStart). Los rangos viven como atributos en WmsWorkerOptions; Application no referencia el
// paquete de DataAnnotations de Options, así que se validan con Validator, que es del framework base.
internal sealed class WmsWorkerOptionsValidator : IValidateOptions<WmsWorkerOptions>
{
    public ValidateOptionsResult Validate(string? name, WmsWorkerOptions options)
    {
        var results = new List<ValidationResult>();
        return Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(results.Select(result => $"{WmsWorkerOptions.SectionName}: {result.ErrorMessage}"));
    }
}

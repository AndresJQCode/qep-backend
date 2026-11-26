namespace Modules.Hr.Domain;

public interface IEffectiveDated
{
    DateOnly EffectiveDate { get; }
}

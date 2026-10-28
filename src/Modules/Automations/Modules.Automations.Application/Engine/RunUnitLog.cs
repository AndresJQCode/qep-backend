using Modules.Automations.Domain.Engine;

namespace Modules.Automations.Application.Engine;

/// <summary>Un envío del bot dentro de un paso: <see cref="Kind"/> es <c>Text | Interactive</c>; el texto vive en Mensajería, aquí sólo ids.</summary>
public sealed record RunStepSend(string Kind, Guid? ClientId, int? TranscriptIndex);

/// <summary>Un <see cref="StepRecord"/> con lo que el procesador sabe y el motor no: el mensaje entrante que lo consumió y los envíos.</summary>
public sealed record RunUnitStep(StepRecord Record, Guid? InputMessageId, IReadOnlyList<RunStepSend> Sends);

/// <summary>
/// Spec 2026-10-25 D-E6 y plan D-P3: el registro en memoria de una unidad del procesador. Nunca lanza: registrar no puede tumbar al bot, así
/// que un índice de efecto que ningún registro reclama (p. ej. un <c>Fail</c> del motor que termina la corrida con efectos sin registro) se ignora.
/// </summary>
public sealed class RunUnitLog
{
    private readonly List<Entry> _steps = [];
    private readonly Dictionary<int, Entry> _owners = [];
    private IReadOnlyList<Effect> _effects = [];

    public IReadOnlyList<RunUnitStep> Steps =>
        _steps.Select(entry => new RunUnitStep(entry.Record, entry.InputMessageId, entry.Sends.ToArray())).ToArray();

    /// <summary>Anota los registros de un paso. El <c>MessageId</c> del entrante va al primer registro de ese paso (D-P3): el bloque que lo consumió.</summary>
    public void Record(StepResult step, EngineInput input)
    {
        ArgumentNullException.ThrowIfNull(step);
        _effects = step.Effects;
        _owners.Clear();
        var inbound = (input as EngineInput.CustomerMessage)?.MessageId;
        var first = true;
        foreach (var record in step.Steps)
        {
            var entry = new Entry(record, first ? inbound : null);
            first = false;
            _steps.Add(entry);
            foreach (var index in record.SendIndexes)
            {
                _owners[index] = entry;
            }
        }
    }

    /// <summary><paramref name="effectIndex"/> es una posición en <see cref="StepResult.Effects"/> del último <see cref="Record"/>.</summary>
    public void Sent(int effectIndex, Guid? clientId, int? transcriptIndex)
    {
        if (effectIndex < 0 || effectIndex >= _effects.Count || !_owners.TryGetValue(effectIndex, out var owner))
        {
            return;
        }

        var kind = _effects[effectIndex] switch
        {
            Effect.SendText => "Text",
            Effect.SendInteractive => "Interactive",
            _ => null,
        };
        if (kind is not null)
        {
            owner.Sends.Add(new RunStepSend(kind, clientId, transcriptIndex));
        }
    }

    private sealed class Entry(StepRecord record, Guid? inputMessageId)
    {
        public StepRecord Record { get; } = record;

        public Guid? InputMessageId { get; } = inputMessageId;

        public List<RunStepSend> Sends { get; } = [];
    }
}

using Modules.Hr.Domain;

namespace Modules.Hr.Application.MedicalExams;

/// <summary>
/// The labels of the exam types that the import accepts and the template lists (spec 2026-11-23 §8). The same rule as
/// <see cref="HrLabels"/>: compared without case, accents or extra spaces, and only the label, not the member name.
/// </summary>
public static class MedicalExamLabels
{
    public static IReadOnlyDictionary<MedicalExamType, string> Types { get; } = new Dictionary<MedicalExamType, string>
    {
        [MedicalExamType.PreEmployment] = "Preingreso",
        [MedicalExamType.Periodic] = "Periódico",
        [MedicalExamType.OccupationChange] = "Cambio de ocupación",
        [MedicalExamType.PostIncapacity] = "Post-incapacidad",
        [MedicalExamType.ReturnToWork] = "Retorno laboral",
        [MedicalExamType.FollowUp] = "Seguimiento",
        [MedicalExamType.Exit] = "Egreso",
    };

    public static bool TryParse(string? text, out MedicalExamType type)
    {
        type = default;
        if (HrText.Fold(text) is not { } folded)
        {
            return false;
        }

        foreach (var (member, label) in Types)
        {
            if (string.Equals(HrText.Fold(label), folded, StringComparison.Ordinal))
            {
                type = member;
                return true;
            }
        }

        return false;
    }
}

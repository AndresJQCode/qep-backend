namespace Modules.Hr.Domain;

/// <summary>The evaluation types of Resolution 1843 of 2025 (spec 2026-11-23 §5.1). Travels by name.</summary>
public enum MedicalExamType
{
    PreEmployment,
    Periodic,
    OccupationChange,
    PostIncapacity,
    ReturnToWork,
    FollowUp,
    Exit,
}

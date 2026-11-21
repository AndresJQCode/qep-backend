namespace Modules.Hr.Application;

/// <summary>
/// Acciones de <c>audit.entries</c> de Hr. Las escrituras de empleados y contratos las agregan las Tareas 7 a 10 junto a su caso
/// de uso. <c>audit.entries</c> guarda <b>nombres</b> de campos, nunca valores.
/// </summary>
public static class HrAuditActions
{
    public const string SettingsCreated = "hr.settings.created";
    public const string SettingsUpdated = "hr.settings.updated";
    public const string SettingsArchived = "hr.settings.archived";
    public const string SettingsUnarchived = "hr.settings.unarchived";
    public const string SettingsDeleted = "hr.settings.deleted";

    public static class Resources
    {
        public const string Site = "site";
        public const string Workplace = "workplace";
        public const string Area = "area";
        public const string Position = "position";
    }
}
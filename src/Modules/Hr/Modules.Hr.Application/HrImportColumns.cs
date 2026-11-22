namespace Modules.Hr.Application;

/// <summary>
/// Los 36 encabezados exactos de la plantilla de empleados, en su orden (spec 2026-11-20, «Importación»). El lector los ubica por
/// nombre, sin mayúsculas ni tildes; el resto del código los usa por estas constantes, nunca por posición.
/// </summary>
public static class HrImportColumns
{
    public const string DataSheetName = "Empleados";

    public const string ValuesSheetName = "Valores válidos";

    public const string TemplateFileName = "plantilla-empleados.xlsx";

    public const int MaxRows = 1000;

    public const long MaxSizeInBytes = 5 * 1024 * 1024;

    public const string FirstName = "NOMBRES";
    public const string LastName = "APELLIDOS";
    public const string DocumentType = "TIPO DE DOCUMENTO";
    public const string DocumentNumber = "NÚMERO DE DOCUMENTO";
    public const string Country = "PAÍS";
    public const string BirthDate = "FECHA DE NACIMIENTO";
    public const string Phone = "CELULAR";
    public const string PersonalEmail = "CORREO PERSONAL";
    public const string WorkEmail = "CORREO CORPORATIVO";
    public const string Address = "DIRECCIÓN";
    public const string City = "CIUDAD";
    public const string HealthProvider = "EPS";
    public const string PensionFund = "FONDO DE PENSIONES";
    public const string SeveranceFund = "FONDO DE CESANTÍAS";
    public const string OccupationalRiskProvider = "ARL";
    public const string ContributorSubtype = "SUBTIPO DE COTIZANTE";
    public const string ForeignPensionExempt = "EXENTO DE PENSIÓN";
    public const string RestDays = "DÍAS DE DESCANSO";
    public const string PaymentMethod = "MEDIO DE PAGO";
    public const string BankName = "BANCO";
    public const string BankAccountType = "TIPO DE CUENTA";
    public const string BankAccountNumber = "NÚMERO DE CUENTA";
    public const string Company = "EMPRESA";
    public const string ContractType = "TIPO DE CONTRATO";
    public const string Duration = "DURACIÓN";
    public const string SalaryType = "TIPO DE SALARIO";
    public const string StartDate = "FECHA DE INGRESO";
    public const string EndDate = "FECHA DE FIN";
    public const string Salary = "SALARIO";
    public const string Teleworker = "TELETRABAJADOR";
    public const string Position = "CARGO";
    public const string Area = "ÁREA";
    public const string Workplace = "CENTRO DE TRABAJO";
    public const string RetirementDate = "FECHA DE RETIRO";
    public const string RetirementReason = "MOTIVO DE RETIRO";

    public static readonly IReadOnlyList<string> Ordered =
    [
        FirstName, LastName, DocumentType, DocumentNumber, Country, BirthDate, Phone, PersonalEmail, WorkEmail, Address, City,
        HealthProvider, PensionFund, SeveranceFund, OccupationalRiskProvider, ContributorSubtype,
        ForeignPensionExempt, RestDays, PaymentMethod, BankName, BankAccountType, BankAccountNumber, Company, ContractType,
        Duration, SalaryType, StartDate, EndDate, Salary, Teleworker, Position, Area, Workplace, RetirementDate, RetirementReason,
    ];

    /// <summary>Los títulos de las columnas de «Valores válidos»: una por lista.</summary>
    public static class ValuesTitles
    {
        public const string DocumentTypes = "Tipos de documento";
        public const string Countries = "Países";
        public const string ContributorSubtypes = "Subtipos de cotizante";
        public const string YesNo = "Sí / No";
        public const string PaymentMethods = "Medios de pago";
        public const string BankAccountTypes = "Tipos de cuenta";
        public const string RestDays = "Días de descanso";
        public const string ContractTypes = "Tipos de contrato";
        public const string Durations = "Duraciones";
        public const string SalaryTypes = "Tipos de salario";
        public const string RetirementReasons = "Motivos de retiro";
        public const string Companies = "Empresas";
        public const string Positions = "Cargos";
        public const string Areas = "Áreas";
        public const string Workplaces = "Centros de trabajo";
    }
}

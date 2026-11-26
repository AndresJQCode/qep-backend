using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Hr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMedicalExamActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_employee_activity_action",
                schema: "hr",
                table: "employee_activity");

            migrationBuilder.DropCheckConstraint(
                name: "CK_employee_activity_category",
                schema: "hr",
                table: "employee_activity");

            migrationBuilder.AddCheckConstraint(
                name: "CK_employee_activity_action",
                schema: "hr",
                table: "employee_activity",
                sql: "action IN ('EmployeeCreated','EmployeeImported','PersonalDataUpdated','PaymentUpdated','SalaryCreated','SalaryRewritten','AssignmentCreated','ContractRenewed','ContractConvertedToIndefinite','ContractTypeChanged','ContractTerminated','EmployeeDeactivated','EmployeeReincorporated','DocumentAttached','MedicalExamRecorded','MedicalExamUpdated','MedicalExamDeleted','ExitExamNotAttended')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_employee_activity_category",
                schema: "hr",
                table: "employee_activity",
                sql: "category IN ('Personal','Lifecycle','Payment','Salary','Assignment','Contract','Medical')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_employee_activity_action",
                schema: "hr",
                table: "employee_activity");

            migrationBuilder.DropCheckConstraint(
                name: "CK_employee_activity_category",
                schema: "hr",
                table: "employee_activity");

            migrationBuilder.AddCheckConstraint(
                name: "CK_employee_activity_action",
                schema: "hr",
                table: "employee_activity",
                sql: "action IN ('EmployeeCreated','EmployeeImported','PersonalDataUpdated','PaymentUpdated','SalaryCreated','SalaryRewritten','AssignmentCreated','ContractRenewed','ContractConvertedToIndefinite','ContractTypeChanged','ContractTerminated','EmployeeDeactivated','EmployeeReincorporated','DocumentAttached')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_employee_activity_category",
                schema: "hr",
                table: "employee_activity",
                sql: "category IN ('Personal','Lifecycle','Payment','Salary','Assignment','Contract')");
        }
    }
}

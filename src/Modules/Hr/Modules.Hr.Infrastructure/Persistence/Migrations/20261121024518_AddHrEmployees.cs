using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Hr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHrEmployees : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "areas",
                schema: "hr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_areas", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employees",
                schema: "hr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    last_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    document_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    document_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    birth_date = table.Column<DateOnly>(type: "date", nullable: true),
                    phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    personal_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    work_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    city_id = table.Column<Guid>(type: "uuid", nullable: true),
                    member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    health_provider = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    pension_fund = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    severance_fund = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    occupational_risk_provider = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    compensation_fund = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    contributor_subtype = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    foreign_pension_exempt = table.Column<bool>(type: "boolean", nullable: false),
                    rest_days = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    payment_method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    bank_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    bank_account_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    bank_account_number = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employees", x => x.id);
                    table.CheckConstraint("CK_employees_bank_account_type", "bank_account_type IS NULL OR bank_account_type IN ('Savings','Checking')");
                    table.CheckConstraint("CK_employees_contributor_subtype", "contributor_subtype IN ('Standard','Pensioner','NotRequiredByAge','PensionRequirementsMet','SubstituteIndemnity')");
                    table.CheckConstraint("CK_employees_document_type", "document_type IN ('CC','CE','PEP','PPT','PA','CD','SC')");
                    table.CheckConstraint("CK_employees_payment_method", "payment_method IS NULL OR payment_method IN ('BankTransfer','Other')");
                    table.CheckConstraint("CK_employees_status", "status IN ('Active','Retired','Deactivated')");
                });

            migrationBuilder.CreateTable(
                name: "positions",
                schema: "hr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_positions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sites",
                schema: "hr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    city_id = table.Column<Guid>(type: "uuid", nullable: true),
                    compensation_fund = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sites", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_activity",
                schema: "hr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: true),
                    changes = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_activity", x => x.id);
                    table.CheckConstraint("CK_employee_activity_action", "action IN ('EmployeeCreated','EmployeeImported','PersonalDataUpdated','PaymentUpdated','SalaryCreated','SalaryRewritten','AssignmentCreated','ContractRenewed','ContractConvertedToIndefinite','ContractTypeChanged','ContractTerminated','EmployeeDeactivated','EmployeeReincorporated','DocumentAttached')");
                    table.CheckConstraint("CK_employee_activity_category", "category IN ('Personal','Lifecycle','Payment','Salary','Assignment','Contract')");
                    table.ForeignKey(
                        name: "FK_employee_activity_employees_employee_id",
                        column: x => x.employee_id,
                        principalSchema: "hr",
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "employment_contracts",
                schema: "hr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    duration = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    salary_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ended_on = table.Column<DateOnly>(type: "date", nullable: true),
                    converted_to_indefinite_on = table.Column<DateOnly>(type: "date", nullable: true),
                    previous_contract_id = table.Column<Guid>(type: "uuid", nullable: true),
                    document_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    document_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    termination_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    termination_notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employment_contracts", x => x.id);
                    table.CheckConstraint("CK_employment_contracts_contract_type", "contract_type IN ('Employee','Contractor','PartTime','SenaApprenticeTeaching','SenaApprenticeProductive','SenaApprenticeDual','UniversityIntern','StudentLaw789','StudentDecree055')");
                    table.CheckConstraint("CK_employment_contracts_duration", "duration IN ('FixedTerm','Indefinite','WorkOrLabor')");
                    table.CheckConstraint("CK_employment_contracts_salary_type", "salary_type IN ('Monthly','HalfTime','Integral')");
                    table.CheckConstraint("CK_employment_contracts_status", "status IN ('Active','Terminated','Converted','Closed')");
                    table.CheckConstraint("CK_employment_contracts_termination_reason", "termination_reason IS NULL OR termination_reason IN ('Resignation','DismissalWithJustCause','DismissalWithoutJustCause','TermExpiry','WorkCompletion','MutualAgreement','ProbationPeriod','CompanyClosure','Death')");
                    table.ForeignKey(
                        name: "FK_employment_contracts_employees_employee_id",
                        column: x => x.employee_id,
                        principalSchema: "hr",
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_employment_contracts_employment_contracts_previous_contract~",
                        column: x => x.previous_contract_id,
                        principalSchema: "hr",
                        principalTable: "employment_contracts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workplaces",
                schema: "hr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ciiu_code = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    risk_class = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    high_risk_pension = table.Column<bool>(type: "boolean", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workplaces", x => x.id);
                    table.CheckConstraint("CK_workplaces_risk_class", "risk_class IN ('I','II','III','IV','V')");
                    table.ForeignKey(
                        name: "FK_workplaces_sites_site_id",
                        column: x => x.site_id,
                        principalSchema: "hr",
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "contract_renewals",
                schema: "hr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    previous_end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    new_end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    is_short = table.Column<bool>(type: "boolean", nullable: false),
                    document_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    document_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract_renewals", x => x.id);
                    table.ForeignKey(
                        name: "FK_contract_renewals_employment_contracts_contract_id",
                        column: x => x.contract_id,
                        principalSchema: "hr",
                        principalTable: "employment_contracts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "salary_records",
                schema: "hr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    teleworker = table.Column<bool>(type: "boolean", nullable: false),
                    below_minimum_acknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    document_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    document_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_salary_records", x => x.id);
                    table.ForeignKey(
                        name: "FK_salary_records_employment_contracts_contract_id",
                        column: x => x.contract_id,
                        principalSchema: "hr",
                        principalTable: "employment_contracts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assignment_records",
                schema: "hr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    position_id = table.Column<Guid>(type: "uuid", nullable: false),
                    area_id = table.Column<Guid>(type: "uuid", nullable: true),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workplace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    document_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assignment_records", x => x.id);
                    table.ForeignKey(
                        name: "FK_assignment_records_areas_area_id",
                        column: x => x.area_id,
                        principalSchema: "hr",
                        principalTable: "areas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_assignment_records_employment_contracts_contract_id",
                        column: x => x.contract_id,
                        principalSchema: "hr",
                        principalTable: "employment_contracts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_assignment_records_positions_position_id",
                        column: x => x.position_id,
                        principalSchema: "hr",
                        principalTable: "positions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_assignment_records_sites_site_id",
                        column: x => x.site_id,
                        principalSchema: "hr",
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_assignment_records_workplaces_workplace_id",
                        column: x => x.workplace_id,
                        principalSchema: "hr",
                        principalTable: "workplaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_areas_tenant_normalized_name",
                schema: "hr",
                table: "areas",
                columns: new[] { "tenant_id", "normalized_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assignment_records_area_id",
                schema: "hr",
                table: "assignment_records",
                column: "area_id");

            migrationBuilder.CreateIndex(
                name: "IX_assignment_records_contract_id",
                schema: "hr",
                table: "assignment_records",
                column: "contract_id");

            migrationBuilder.CreateIndex(
                name: "IX_assignment_records_position_id",
                schema: "hr",
                table: "assignment_records",
                column: "position_id");

            migrationBuilder.CreateIndex(
                name: "IX_assignment_records_site_id",
                schema: "hr",
                table: "assignment_records",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "IX_assignment_records_workplace_id",
                schema: "hr",
                table: "assignment_records",
                column: "workplace_id");

            migrationBuilder.CreateIndex(
                name: "IX_contract_renewals_contract_id",
                schema: "hr",
                table: "contract_renewals",
                column: "contract_id");

            migrationBuilder.CreateIndex(
                name: "IX_employee_activity_employee_occurred",
                schema: "hr",
                table: "employee_activity",
                columns: new[] { "employee_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_employees_tenant_document_number",
                schema: "hr",
                table: "employees",
                columns: new[] { "tenant_id", "document_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_employees_tenant_member_id",
                schema: "hr",
                table: "employees",
                columns: new[] { "tenant_id", "member_id" },
                unique: true,
                filter: "member_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_employees_tenant_status_name",
                schema: "hr",
                table: "employees",
                columns: new[] { "tenant_id", "status", "last_name", "first_name" });

            migrationBuilder.CreateIndex(
                name: "IX_employment_contracts_company_id",
                schema: "hr",
                table: "employment_contracts",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "IX_employment_contracts_employee_active",
                schema: "hr",
                table: "employment_contracts",
                column: "employee_id",
                unique: true,
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_employment_contracts_previous_contract_id",
                schema: "hr",
                table: "employment_contracts",
                column: "previous_contract_id");

            migrationBuilder.CreateIndex(
                name: "IX_positions_tenant_normalized_name",
                schema: "hr",
                table: "positions",
                columns: new[] { "tenant_id", "normalized_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_salary_records_contract_id",
                schema: "hr",
                table: "salary_records",
                column: "contract_id");

            migrationBuilder.CreateIndex(
                name: "IX_sites_company_id",
                schema: "hr",
                table: "sites",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "IX_sites_tenant_company_normalized_name",
                schema: "hr",
                table: "sites",
                columns: new[] { "tenant_id", "company_id", "normalized_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workplaces_site_id",
                schema: "hr",
                table: "workplaces",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "IX_workplaces_tenant_site_normalized_name",
                schema: "hr",
                table: "workplaces",
                columns: new[] { "tenant_id", "site_id", "normalized_name" },
                unique: true);

            // FK real a la empresa (D-11): borrar una empresa con sedes o contratos sale como 422 companies.company.in_use:
            // CompaniesUnitOfWork traduce el SQLSTATE 23001 (restrict_violation, el de ON DELETE RESTRICT) igual que el 23503.
            // A mano porque EF no modela relaciones entre DbContext de módulos distintos (mismo precedente que pos → companies).
            migrationBuilder.AddForeignKey(
                name: "FK_sites_companies_company_id",
                schema: "hr",
                table: "sites",
                column: "company_id",
                principalSchema: "companies",
                principalTable: "companies",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_employment_contracts_companies_company_id",
                schema: "hr",
                table: "employment_contracts",
                column: "company_id",
                principalSchema: "companies",
                principalTable: "companies",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_employment_contracts_companies_company_id",
                schema: "hr",
                table: "employment_contracts");

            migrationBuilder.DropForeignKey(
                name: "FK_sites_companies_company_id",
                schema: "hr",
                table: "sites");

            migrationBuilder.DropTable(
                name: "assignment_records",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "contract_renewals",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "employee_activity",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "salary_records",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "areas",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "positions",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "workplaces",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "employment_contracts",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "sites",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "employees",
                schema: "hr");
        }
    }
}

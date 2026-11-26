namespace Modules.Hr.Infrastructure.Persistence;

/// <summary>
/// The backfill of the migration that adds the medical exams (spec 2026-11-23 §5.3, D-7): every contract already <c>Terminated</c>
/// whose exit exam deadline falls today or later gets its <c>Pending</c> requirement; older ones get nothing. A separate class so
/// that a test can run the very same statement against a reference day.
/// </summary>
/// <remarks>
/// The deadline is counted here on Monday to Friday only: the Colombian holidays live in Messaging and Hr cannot reference it, so
/// a contract terminated in the last days before the deploy may get a due date a day or two earlier than the exact one. The
/// requirement is born with the right shape and only the date of that handful of rows is approximate.
/// </remarks>
public static class MedicalExamBackfill
{
    /// <param name="todayExpression">A SQL date expression: <c>CURRENT_DATE</c> in the migration, a literal in a test.</param>
    public static string Sql(string todayExpression) => $"""
        INSERT INTO hr.exit_exam_requirements
            (id, tenant_id, employee_id, contract_id, terminated_on, due_date, status, exam_id, notified_on, notes, created_at)
        SELECT gen_random_uuid(), e.tenant_id, c.employee_id, c.id, c.ended_on, due.due_date, 'Pending', NULL, NULL, NULL, now()
        FROM hr.employment_contracts c
        JOIN hr.employees e ON e.id = c.employee_id
        CROSS JOIN LATERAL (
            SELECT d::date AS due_date
            FROM generate_series((c.ended_on + 1)::timestamp, (c.ended_on + 30)::timestamp, interval '1 day') AS d
            WHERE extract(isodow FROM d) < 6
            ORDER BY d
            OFFSET 4 LIMIT 1
        ) due
        WHERE c.status = 'Terminated'
          AND c.ended_on IS NOT NULL
          AND due.due_date >= {todayExpression}
          AND NOT EXISTS (SELECT 1 FROM hr.exit_exam_requirements r WHERE r.contract_id = c.id);
        """;
}

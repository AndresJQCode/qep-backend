-- Borra la carga sintética de la exportación (spec 2026-09-13): SÓLO el tenant carga-export, en el
-- orden que exigen las relaciones.
--
-- Córrelo DESPUÉS de dejar Seed__ExportLoad__Quotations en "0" y desplegar. Con el interruptor
-- prendido, cualquier reinicio del pod vuelve a sembrar el tenant que este script deja vacío.
--
-- No borra:
--   * el usuario dueño, que es la cuenta real de quien midió;
--   * las filas de audit.entries, porque el log es inmutable;
--   * los correos registrados en notifications.notifications ni los eventos del outbox;
--   * los .xlsx de R2, que los borra la regla de lifecycle de exports/.
--
--   psql -h <host> -p <puerto> -U <usuario> -d <base> -v ON_ERROR_STOP=1 -f ops/export-load-cleanup.sql
BEGIN;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM tenancy.tenants
        WHERE id = '01900000-0000-7000-8000-000000000004' AND slug = 'carga-export') THEN
        RAISE EXCEPTION 'El tenant carga-export no existe con el id esperado: no se borra nada.';
    END IF;
END
$$;

-- sales -> quotations es RESTRICT: primero las ventas. Sus comprobantes caen en cascada.
DELETE FROM quotations.sales WHERE tenant_id = '01900000-0000-7000-8000-000000000004';
-- Ítems, partes, historial y PDFs caen en cascada con la cotización.
DELETE FROM quotations.quotations WHERE tenant_id = '01900000-0000-7000-8000-000000000004';
DELETE FROM quotations.quotation_number_counters WHERE tenant_id = '01900000-0000-7000-8000-000000000004';
DELETE FROM quotations.sale_number_counters WHERE tenant_id = '01900000-0000-7000-8000-000000000004';
DELETE FROM quotations.export_jobs WHERE tenant_id = '01900000-0000-7000-8000-000000000004';

-- Las direcciones caen en cascada con el cliente. La clasificación es RESTRICT, así que va después.
DELETE FROM customers.customers WHERE tenant_id = '01900000-0000-7000-8000-000000000004';
DELETE FROM customers.client_classifications WHERE tenant_id = '01900000-0000-7000-8000-000000000004';
DELETE FROM customers.cuc_counters WHERE tenant_id = '01900000-0000-7000-8000-000000000004';

-- Escalas y cambios de precio caen en cascada con el producto. La tasa es RESTRICT, así que va después.
DELETE FROM catalog.products WHERE tenant_id = '01900000-0000-7000-8000-000000000004';
DELETE FROM catalog.tax_rates WHERE tenant_id = '01900000-0000-7000-8000-000000000004';

DELETE FROM tenancy.memberships WHERE tenant_id = '01900000-0000-7000-8000-000000000004';
DELETE FROM tenancy.tenants WHERE id = '01900000-0000-7000-8000-000000000004';

COMMIT;

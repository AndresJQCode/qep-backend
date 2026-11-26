# Etiquetas en la importación de clientes por Excel

Fecha: 2026-11-17. Rama: `fix/importar-clientes-sin-direccion`.

## Intención

Una lista de contactos (por ejemplo, los asistentes a un evento) se importa para enviarles una campaña de WhatsApp. La campaña
toma su audiencia por etiquetas o por ids de cliente (`CampaignAudienceRequest`), y hoy la etiqueta se asigna cliente por
cliente (`PUT …/customers/{id}/tags`). El Excel de importación es el lugar natural para decir «estos clientes son del evento X».

Antecedente en la misma rama: la dirección dejó de ser obligatoria en el Excel (`ca9ee15c`), para que una lista con sólo nombre y
celular entre.

## Objetivos y no objetivos

- Objetivo: una columna opcional en el Excel que etiqueta la fila al crear o actualizar el cliente.
- No objetivo: asignación masiva desde la lista de clientes (selección múltiple + endpoint por lotes). Es otra feature.
- Anti-objetivo: que un reimport borre etiquetas que puso un asesor.

## Enfoques considerados

1. **Columna «Etiquetas» en el Excel** (elegido). Resuelve el caso de origen con un cambio chico en el módulo, sin pantalla nueva.
2. **Endpoint de asignación masiva + selección múltiple en la lista.** Herramienta general; cuesta backend y frontend y, para una
   lista recién importada, obliga a identificar a mano los importados. Queda como feature aparte.
3. **Script contra la API.** Sirve una vez; no es producto. Descartado.

## Decisiones

- **Cabecera `Etiquetas`**, sin tilde como las demás, opcional y al final: `CustomerImportColumns.Optional = [Empresa, Etiquetas]`.
  Un archivo sin la columna se lee como «sin etiquetas» y sigue siendo válido.
- **Varios nombres separados por `;`.** Se recorta cada uno; los vacíos se ignoran; repetidos en la misma celda (sin distinguir
  mayúsculas) cuentan una vez.
- **Casa por nombre sin distinguir mayúsculas** contra las etiquetas del tenant (misma regla que `TagNameIndex`). La que ya
  existe conserva su nombre y color.
- **La que no existe se crea**, color `Gray`, con el nombre en **tipo oración**: primera letra en mayúscula y el resto en minúsculas
  (`evento MACEL` → `Evento macel`). La regla vive en un solo lugar (`TagNameFormat.SentenceCase`, dominio) y la usa sólo la
  importación; la pantalla de etiquetas sigue guardando lo que el usuario escribe.
- **Agrega, nunca quita.** En filas de actualización (con Cuc) y en creación, las etiquetas de la celda se suman a las que el
  cliente ya tiene. La exportación escribe las actuales, así un exportado-reimportado es estable.
- **Errores por fila**, con el código del importador y nunca una excepción a mitad del lote:
  - `customers.import.row.tag_name_too_long`: un nombre pasa `Tag.NameMaxLength` (40).
  - `customers.import.row.tag_limit_per_customer`: el cliente quedaría con más de `CustomerTagAssignment.MaxPerCustomer` (20).
  - `customers.import.row.tag_limit_reached`: crear la etiqueta pasaría `Tag.MaxPerTenant` (100).
  Una fila con error de etiquetas no se importa (igual que cualquier otro error de fila).
- **Permiso:** el de la importación (`CustomersPermissions.CustomerImport`). Crear etiquetas desde el Excel no exige
  `customers.tag.manage` aparte: es parte de importar.
- **Auditoría:** la de la importación ya cubre la fila; la creación de una etiqueta publica `customers.tag.created` como la pantalla.
- **Plantilla y exportación** escriben la columna después de «Empresa».

## Lo que queda al constructor

- Resolver las etiquetas una sola vez por archivo (un `ListAsync` del tenant) y crear las faltantes en la misma transacción de la
  importación, dentro del tope de 100 contando las que el propio archivo va creando.
- Una prueba de integración que importe dos filas con `Evento macel; VIP`, verifique que la etiqueta nueva se creó en tipo oración,
  que una existente (`VIP`) no se renombró, y que un segundo import con otra etiqueta suma sin quitar.

# Variantes de producto — estado del proyecto

Archivo vivo: se actualiza al cerrar cada tarea o decisión. Las tareas detalladas y sus casillas están en el
[plan](2026-10-20-variantes-de-producto-backend.md); el porqué, en el [diseño](../specs/2026-10-20-variantes-de-producto-design.md).

## Estado general

| Fase | Estado | Referencia |
| --- | --- | --- |
| Diseño | Aprobado por el owner (2026-10-20), en `develop` local, sin push | `ea383771` |
| Plan backend | Escrito, en `develop` local, sin push; **ejecución: subagentes, en sesión nueva** | `e0d7756f` |
| Implementación backend | En curso, rama `feature/variantes-producto` (worktree `../qep-backend-variantes`) | — |
| Plan y trabajo de frontend | No iniciado (sale en la misma ventana que el backend) | — |

## Tareas del plan (backend)

| # | Tarea | Estado |
| --- | --- | --- |
| 1 | Biblioteca de opciones (dominio) | Hecha (`e0506006`) |
| 2 | Variante, galería y reglas del agregado | Hecha (`31fb3098`, `04b26bfc`) |
| 3 | Persistencia y migración de datos de Catalog | Hecha (`ba326a59`) |
| 4 | Biblioteca de opciones: casos de uso y API | Hecha (`13d50f02`), pruebas escritas y sin ejecutar |
| 5 | Producto con variantes: crear, ver, listar, editar | Hecha (`852cd6c5`, `3f9497b9`), pruebas sin ejecutar |
| 6 | Borrar o desactivar variantes (puerto de uso) | Hecha (`5bd62b58`, `72e91421`), pruebas sin ejecutar |
| 7 | Historial de precios y copia de escalas por variante | Hecha (`f870b495`, `09072af1`), pruebas sin ejecutar |
| 8 | Excel de productos por variante | Pendiente |
| 9 | Adaptadores hacia Quotations, Pos y Shopify | Pendiente |
| 10 | Quotations: líneas por variante | Pendiente |
| 11 | Pedidos de Shopify por variante | Pendiente |
| 12 | Punto de venta por variante | Pendiente |
| 13 | Reporte de cambios de precio | Pendiente |
| 14 | PDF y Excel de pedidos | Pendiente |
| 15 | Siembras, documentación y traspaso al frontend | Pendiente |

Estados: Pendiente · En curso · Hecha (con SHA del commit) · Bloqueada (con motivo).

## Decisiones del owner

- 2026-10-20: decisiones 1, 2, 4 y 7 de §9 aprobadas tal cual.
- 2026-10-20: tope de **2.048** variantes por producto (el diseño decía 100).
- 2026-10-20: **galería** de hasta 250 fotos por producto; cada variante elige una (el diseño decía sin galería).
- 2026-10-20: una variante **se borra si no está en uso**, y si está en uso se desactiva (el diseño decía que nunca se borra).
- Reporte de ventas por producto/variante: fuera de este proyecto.

## Pendientes y riesgos abiertos

- Método elegido por el owner (2026-10-20): `superpowers:subagent-driven-development`, en sesión nueva. Falta crear la rama `feature/variantes-producto` desde `develop`, preferiblemente en un worktree (el árbol se comparte entre sesiones).
- Plan del frontend: no existe; parte del traspaso de la tarea 15.
- T9–T12 cambian contratos entre módulos y se commitean juntas.
- Antes de desplegar: backend y frontend juntos; probar el escáner del POS con códigos de barras reales.
- Proyectos siguientes (orden acordado): 2 productos desde Shopify → 3 campanita → 4 alertas de la conexión Shopify.

## Registro

- 2026-10-20 — Diseño aprobado y mergeado a `develop`; plan escrito.
- 2026-10-20 — El owner elige ejecutar por subagentes en una sesión nueva.

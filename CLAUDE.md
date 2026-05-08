# Spikit

**Tipo:** App nativa para Windows
**Cliente:** Proyecto propio
**Inicio:** 2026-04-28

## Estado del proyecto

> Tipo: **proyecto propio**. Flujo simplificado.

- [x] **1. Idea validada** — `/product-strategist` cerró go/no-go y completó `docs/product.md` (2026-04-28)
- [x] **2. Requerimientos definidos** — `/requirements-analyst` cerró user stories del MVP V1 BYOK-only en `docs/requirements.md` (2026-04-28)
- [x] **3. Diseño / arquitectura listos** — `/architect` cerró `docs/architecture.md` + ADR-0001/ADR-0002 (2026-04-28/30). `/ux-designer` cerró `docs/flows.md` (2026-04-29). `/ui-designer` cerró `docs/design-system.md` con tokens (dark+light), componentes base, componentes específicos (DictationPill, WaveformVisualizer, FloatingResultWindow, TrayIcon) e identidad visual de **Spikit** (2026-04-30).
- [x] **4. Ejecución iniciada** — `/pm` creó backlog en ClickUp el 2026-05-02: 3 spikes + 9 parents de épica (EP-0 a EP-8) + 16 sub-tasks. **Sprints cerrados:** EP-0 (setup técnico) + EP-1 (POC latencia, derivó ADR-0003) + spikes al 2026-05-05; EP-2 (Walking skeleton del dictado, 8 sub-tasks + tickets UX/UI/QA del estado "Iniciando…"); EP-3 (Onboarding inicial F1, 8 sub-tasks EP-3.1 a EP-3.8); EP-4 (Settings completos F3+F4+F5, 10 sub-tasks EP-4.1 a EP-4.10 + ticket de onboarding latencia `86aha9uzt`); EP-5 (Manejo de errores transversales F7, 3 sub-tasks EP-5.1 a EP-5.3); EP-6 (Pulido visual de pill y FloatingResultWindow post-D-9: LogoWave 3 barras + pill minimalista + Mica/Acrylic + FloatingResultWindow final V1-V6, 6 sub-tasks EP-6.1 a EP-6.6) cerrado al 2026-05-08. **Sprint actual (al 2026-05-08):** EP-7 (Sección Plan dormante F6 — UI placeholder con badge "BYOK" + descripción + botón disabled "Pasar a Pro" + foundation arquitectónica `IPlanService` lista para activar Free/Pro sin refactor RN-7) — desglosado en 3 sub-tasks: EP-7.1 `IPlanService` + `Plan` enum + `BYOK_OnlyPlanService`, EP-7.2 UserControl `PlanSection` (badge + descripción + botón disabled), EP-7.3 cableado runtime (DI + integración al sidebar). Abierto para `/frontend` (con `/architect` en 7.1 y consulta a `/ui-designer` en 7.2).

## Stack

Por definir — pendiente del Architect (app nativa para Windows con componentes de IA y deploy público).

## Cómo se trabaja en este repo

- **Convenciones globales del equipo:** [Equipo/CLAUDE.md](../Equipo/CLAUDE.md)
- **Convenciones de `docs/`:** [Equipo/docs-conventions.md](../Equipo/docs-conventions.md)
- **Convenciones de ClickUp:** [Equipo/clickup-conventions.md](../Equipo/clickup-conventions.md)

Cada agente que se active acá tiene que:
1. Leer la sección "Estado del proyecto" arriba para saber en qué etapa está el proyecto.
2. Leer los archivos de `docs/` que le competen (ver `docs-conventions.md`).
3. Consultar su queue en ClickUp ([Space `Spikit`](https://app.clickup.com/90133029066/v/o/s/901313785012)).

## Documentación viva

- [docs/product.md](docs/product.md) — qué es y para quién (Product Strategist)
- [docs/requirements.md](docs/requirements.md) — specs y user stories (Requirements Analyst)
- [docs/flows.md](docs/flows.md) — user flows y wireframes (UX Designer)
- [docs/design-system.md](docs/design-system.md) — tokens y componentes visuales (UI Designer)
- [docs/architecture.md](docs/architecture.md) — stack, estructura, contratos (Architect)
- [docs/adrs/](docs/adrs/) — decisiones arquitectónicas (Architect)
- [docs/ai-features.md](docs/ai-features.md) — features con IA (AI Engineer)
- [docs/infra.md](docs/infra.md) — hosting y deploy (DevOps)
- [docs/testing-strategy.md](docs/testing-strategy.md) — estrategia de testing (QA)

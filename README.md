# Spikit

> No lo escribas. Spikit.

App nativa para Windows que convierte voz a texto vía hotkey global y la pega en cualquier app activa (BYOK con Whisper API).

## Setup en máquina nueva

Requisitos: Windows 10 1809+ / Windows 11 con `winget` disponible.

```powershell
git clone <repo-url> Spikit
cd Spikit
.\scripts\bootstrap.ps1     # instala .NET 8 SDK + Build Tools + Inno Setup si faltan
# Si el script instaló el .NET SDK, reabrí la terminal antes del paso siguiente.
dotnet build
```

Detalle de los prerequisitos y alternativas (instalación manual, troubleshooting de PATH) en [`docs/infra.md`](docs/infra.md#setup-local-de-desarrollo).

## Documentación

Detalle del proyecto en [`docs/`](docs/):

- [`docs/product.md`](docs/product.md) — qué es y para quién
- [`docs/requirements.md`](docs/requirements.md) — specs y user stories
- [`docs/flows.md`](docs/flows.md) — user flows
- [`docs/design-system.md`](docs/design-system.md) — tokens y componentes
- [`docs/architecture.md`](docs/architecture.md) — stack y estructura
- [`docs/adrs/`](docs/adrs/) — decisiones arquitectónicas

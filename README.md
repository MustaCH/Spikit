# Spikit

> Don't write it. Spikit it.

Voice-first capture for Windows. Hold a global hotkey, talk, and your transcribed text gets pasted into whatever app had focus — Cursor, VS Code, your browser, anywhere. Built native (WPF + .NET 8), no Electron.

**Status:** Beta — actively developed toward V1.

## Why

The Windows market for voice dictation aimed at developers is broken. Electron-based apps (Wispr Flow) freeze IDEs like VS Code. The macOS-only options (Glaido, Superwhisper) leave Windows out. The native Windows alternatives have no brand or are barely maintained. Spikit ships a native, fast, privacy-conscious app focused on one thing: getting your voice into any text input on your machine, fast.

## Highlights

- **Native Windows.** WPF + .NET 8. No Electron, no resource-heavy runtime, no IDE freezing.
- **Privacy-strict.** The microphone only opens after a full hotkey press — no pre-warm, no contextual listening. Audio never touches disk; transcripts only persist if you explicitly opt in.
- **BYOK by default.** Bring your own Whisper-compatible endpoint (OpenAI, Groq, or any custom OpenAI-Whisper-compatible API). API keys are stored locally via Windows DPAPI.
- **Push-to-talk or toggle.** Configurable hotkey mode. Default push-to-talk for fine-grained control.
- **Designed for vibe coders.** Made for the workflow where the bottleneck is not writing code but communicating intent to AI agents at speed.

## Stack

| Layer | Choice |
|---|---|
| UI framework | WPF + .NET 8 (`net8.0-windows`) |
| Look & feel | [WPF-UI](https://wpfui.lepo.co/) (Fluent components) |
| Audio capture | [NAudio](https://github.com/naudio/NAudio) over WASAPI shared mode |
| Transcription | OpenAI Whisper API (or any compatible provider) |
| Local persistence | JSON in `%AppData%\Spikit\settings.json` |
| Secrets | Windows DPAPI |
| Logging | Serilog (rolling daily files in `%AppData%\Spikit\logs\`) |
| Tests | xUnit + Moq |
| Distribution | Inno Setup (`.exe` installer, work in progress) |

## Getting started (developer setup)

Requirements: Windows 10 1809+ or Windows 11 with `winget` available.

```powershell
git clone https://github.com/MustaCH/Spikit.git
cd Spikit
.\scripts\bootstrap.ps1     # detects and installs .NET 8 SDK, Build Tools, Inno Setup
```

After installing the .NET SDK for the first time you may need to **reopen the terminal** for `PATH` to refresh.

### Build and run

```powershell
dotnet build Spikit.sln
dotnet run --project src/Spikit/Spikit.csproj
```

### Tests

```powershell
dotnet test Spikit.sln
```

## Project layout

```
src/Spikit/              WPF app (Views, ViewModels, Services, Native, Models, Resources)
tests/Spikit.Tests/      xUnit tests for Services and ViewModels
installer/               Inno Setup script (in progress)
scripts/                 PowerShell helpers (bootstrap.ps1)
```

## Roadmap

**V1 (current):** BYOK MVP — onboarding, dictation core, Whisper integration, settings, error handling, polish, packaging.

**V2 (planned):** managed plans (Free / Pro) with backend, contextual modes per target app (Cursor, VS Code, Claude Code), additional transcription providers.

## License

To be defined.

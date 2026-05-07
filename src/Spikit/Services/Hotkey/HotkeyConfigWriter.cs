using Microsoft.Extensions.Logging;
using Spikit.Models;
using Spikit.Services.Orchestration;
using Spikit.Services.Settings;

namespace Spikit.Services.Hotkey;

// Implementación del contrato transaccional descrito en IHotkeyConfigWriter.
//
// Diferencia con ProviderConfigWriter (EP-3.4): acá la primitiva más probable de fallar
// es Register (CB-7), que ocurre primero. Por eso el orden es Unregister-prev → Register-nuevo,
// y solo si Register-nuevo OK pasamos a JsonSettings + reload del orchestrator. Si Register
// falla, restauramos la registration previa para que el usuario quede con un hotkey activo
// (no lo dejamos sin nada).
public sealed class HotkeyConfigWriter : IHotkeyConfigWriter
{
    private readonly IHotkeyService _hotkey;
    private readonly ISettingsService _settings;
    private readonly DictationOrchestrator _orchestrator;
    private readonly ILogger<HotkeyConfigWriter> _logger;

    public HotkeyConfigWriter(
        IHotkeyService hotkey,
        ISettingsService settings,
        DictationOrchestrator orchestrator,
        ILogger<HotkeyConfigWriter> logger)
    {
        _hotkey = hotkey;
        _settings = settings;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    public Task SaveAsync(HotkeyDefinition definition, HotkeyMode mode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ct.ThrowIfCancellationRequested();

        var previous = _hotkey.CurrentRegistration;

        // Paso 1 — Register de la nueva combinación. CB-7 cae acá: HotkeyRegistrationException
        // se propaga al caller (el VM la convierte en SaveError inline). Si la nueva falla,
        // restauramos la previa para no dejar al usuario sin hotkey activo.
        _hotkey.Unregister();
        try
        {
            _hotkey.Register(definition);
        }
        catch (HotkeyRegistrationException)
        {
            RestorePrevious(previous);
            throw;
        }

        // Paso 2 — JsonSettings. Si falla post-Register, rollback completo: Unregister la
        // nueva, re-Register la previa, devolver excepción específica de save.
        try
        {
            var current = _settings.Load();
            current.Hotkey = HotkeySettings.From(definition, mode);
            _settings.Save(current);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JsonSettings rechazó el guardado del bloque hotkey — rollback");
            _hotkey.Unregister();
            RestorePrevious(previous);

            throw new HotkeyConfigSaveException(
                "No se pudo guardar la configuración del hotkey en el archivo de settings. Probá de nuevo.",
                ex);
        }

        // Paso 3 — Reload runtime. El orchestrator lee Mode al recibir HotkeyPressed/Released
        // (no necesita re-registrar nada porque el hotkey global ya está activo arriba).
        _orchestrator.SetMode(mode);

        _logger.LogInformation(
            "Hotkey config persistida y aplicada en runtime ({Hotkey} / {Mode})",
            definition, mode);

        return Task.CompletedTask;
    }

    private void RestorePrevious(HotkeyDefinition? previous)
    {
        if (previous is null) return;
        try
        {
            _hotkey.Register(previous);
        }
        catch (Exception ex)
        {
            // Doble fallo: la previa tampoco se puede registrar (raro, pero posible si otra
            // app la tomó entre nuestro Unregister y el Register de rollback). Logueamos
            // para que el usuario vea la situación en logs y siga su camino — la excepción
            // original (HotkeyRegistrationException o HotkeyConfigSaveException) ya viaja.
            _logger.LogError(ex, "Fallo al restaurar el hotkey previo {Hotkey} durante rollback", previous);
        }
    }
}

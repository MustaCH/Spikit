namespace Spikit.Services.Hotkey;

// Excepción unificada que tira HotkeyConfigWriter cuando JsonSettings rechaza la persistencia
// (después del Register exitoso). Lleva el mensaje listo para mostrar inline al usuario.
//
// El conflict CB-7 (Register que falla porque otra app tiene la combinación) se propaga
// como HotkeyRegistrationException — distinto motivo, distinto mensaje en UI.
public sealed class HotkeyConfigSaveException : Exception
{
    public HotkeyConfigSaveException(string message) : base(message) { }
    public HotkeyConfigSaveException(string message, Exception inner) : base(message, inner) { }
}

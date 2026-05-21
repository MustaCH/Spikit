namespace Spikit.Services.Auth;

// EP-11.7 — orquesta el cleanup de los servicios runtime que dependen de identidad
// (orchestrator, hotkey, tray) y delega el cleanup final de tokens a IAuthService.
//
// Existe como mediator separado para mantener PlanSectionViewModel desacoplado del
// host lifecycle (App.xaml.cs) y del set de servicios a apagar — el VM solo conoce
// el método LogoutAsync. App.xaml.cs reacciona al StateChanged que dispara
// IAuthService.LogoutAsync internamente y se encarga del cleanup de UI (cerrar
// ventanas, abrir LoginWindow, resetear flags del shell).
public interface ISessionLifecycleService
{
    Task LogoutAsync(CancellationToken ct);
}

namespace Spikit.Services.Auth;

// Wrapper inyectable para abrir un URL en el browser default del sistema. La razón
// es testabilidad — el AuthService llama Open() al iniciar el login y los tests
// inyectan un fake que captura el URL sin lanzar procesos.
public interface IBrowserLauncher
{
    void Open(string url);
}

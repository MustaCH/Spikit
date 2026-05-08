namespace Spikit.Services.Autostart;

// Iniciar con Windows (US-5.1). Implementación V1 vía registry HKCU\Software\Microsoft\
// Windows\CurrentVersion\Run, que es el mecanismo "lightweight" que soporta Windows
// nativamente sin requerir Task Scheduler ni elevación. Cualquier app instalada por el
// usuario puede escribir HKCU sin elevation.
public interface IAutostartService
{
    bool IsEnabled();
    void Enable();
    void Disable();
}

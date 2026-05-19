using System.Diagnostics;

namespace Spikit.Services.Auth;

// Impl productiva del IBrowserLauncher: ProcessStartInfo con UseShellExecute=true
// delega en el browser default del usuario. No bloquea el UI thread — el Process
// arranca y el call site sigue.
public sealed class DefaultBrowserLauncher : IBrowserLauncher
{
    public void Open(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });
    }
}

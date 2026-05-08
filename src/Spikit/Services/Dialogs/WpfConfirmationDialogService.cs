using System.Windows;
using Spikit.ViewModels.Settings.Sections;
using Spikit.Views.Common;

namespace Spikit.Services.Dialogs;

// Implementación WPF del IConfirmationDialogService: instancia ConfirmDialog, lo asocia
// a la owner correcta (SettingsWindow si está abierta, sino MainWindow), y devuelve el
// resultado del ShowDialog().
//
// La elección de owner: privilegiar la window topmost que NO sea la dialog en sí. En la
// práctica, cuando el usuario aprieta "Borrar API key" desde Settings, la owner es la
// SettingsWindow (que tiene IsActive=true en ese momento). Si por alguna razón no hay
// activa, caemos a MainWindow.
//
// Singleton en DI: el servicio es stateless. Cada Confirm() crea su propia instancia de
// ConfirmDialog (transient por construcción).
public sealed class WpfConfirmationDialogService : IConfirmationDialogService
{
    public bool Confirm(ConfirmationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var dialog = new ConfirmDialog
        {
            Title = request.Title,
            MessageText = request.Message,
            ConfirmLabel = request.ConfirmLabel,
            CancelLabel = request.CancelLabel,
            IsDestructive = request.IsDestructive,
            Owner = ResolveOwner(),
        };

        return dialog.ShowDialog() == true;
    }

    private static Window? ResolveOwner()
    {
        if (Application.Current is null) return null;

        // Buscamos la window activa primero (la que el usuario tiene en foco — típicamente
        // SettingsWindow cuando se invoca el modal). Si ninguna está activa caemos a la
        // MainWindow del Application.
        foreach (Window w in Application.Current.Windows)
        {
            if (w.IsActive) return w;
        }
        return Application.Current.MainWindow;
    }
}

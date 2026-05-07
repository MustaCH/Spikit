namespace Spikit.ViewModels.Settings;

// Secciones del SettingsWindow (EP-4). Orden = orden visual en el sidebar.
// El separador visual va entre Plan y About (lo decide el XAML, no el enum).
//
// Spec: docs/flows.md FLOW 3, docs/design-system.md §9.12.
public enum SettingsSection
{
    General = 0,
    Provider = 1,
    Hotkey = 2,
    Audio = 3,
    Privacy = 4,
    History = 5,
    Plan = 6,
    About = 7,
}

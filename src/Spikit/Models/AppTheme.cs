namespace Spikit.Models;

// Tema visual de la app (US-5.2). System es el default V1: la app sigue el tema de Windows
// y se actualiza vivo cuando el usuario lo cambia desde el OS (suscripción a
// SystemEvents.UserPreferenceChanged en IThemeService).
public enum AppTheme
{
    System = 0,
    Dark = 1,
    Light = 2,
}

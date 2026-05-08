using Spikit.Models;

namespace Spikit.Services.Theme;

// Aplica el tema visual a la app en runtime (US-5.2). Resuelve "System" leyendo el setting
// `AppsUseLightTheme` del registry de Windows + se suscribe a SystemEvents.UserPreferenceChanged
// para reflejar cambios sin reiniciar la app.
//
// EffectiveTheme siempre es Dark o Light (nunca System) — los consumers que dibujan
// ventanas custom (DwmSetWindowAttribute UseImmersiveDarkMode, brushes específicos) lo
// usan para decidir el render.
public interface IThemeService
{
    AppTheme CurrentTheme { get; }
    AppTheme EffectiveTheme { get; }
    void Apply(AppTheme theme);
    event EventHandler<AppTheme>? EffectiveThemeChanged;
}

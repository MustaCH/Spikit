using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Spikit.Models;

namespace Spikit.Services.Theme;

// Implementación de IThemeService que swappea los MergedDictionaries de App.xaml entre
// Colors.{Dark,Light}.xaml + Shadows.{Dark,Light}.xaml. La identificación se hace por
// nombre de archivo en el Source URI (no por instancia) — más estable que mantener
// referencias y resilient a diferentes formas en que App.xaml puede cargar los recursos.
//
// Modo "System" se suscribe a SystemEvents.UserPreferenceChanged y resuelve el effective
// vía registry HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize →
// AppsUseLightTheme (DWORD, 0=dark / 1=light). Si la lectura falla, asume Dark (la app
// arranca en Dark por default en App.xaml).
public sealed class WpfThemeService : IThemeService, IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    private static readonly string[] DarkResources =
    {
        "Resources/Themes/Colors.Dark.xaml",
        "Resources/Themes/Shadows.Dark.xaml",
    };

    private static readonly string[] LightResources =
    {
        "Resources/Themes/Colors.Light.xaml",
        "Resources/Themes/Shadows.Light.xaml",
    };

    private readonly ILogger<WpfThemeService> _logger;
    private AppTheme _currentTheme = AppTheme.System;
    private AppTheme _effectiveTheme = AppTheme.Dark;
    private bool _disposed;

    public WpfThemeService(ILogger<WpfThemeService> logger)
    {
        _logger = logger;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public AppTheme CurrentTheme => _currentTheme;
    public AppTheme EffectiveTheme => _effectiveTheme;

    public event EventHandler<AppTheme>? EffectiveThemeChanged;

    public void Apply(AppTheme theme)
    {
        _currentTheme = theme;
        var effective = ResolveEffective(theme);
        ApplyEffective(effective, raiseEvent: true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Solo nos interesa cuando cambió la preferencia general (que incluye light/dark).
        // SystemEvents puede dispararse en background thread; siempre marshall a UI antes
        // de tocar Application.Resources.
        if (e.Category != UserPreferenceCategory.General) return;
        if (_currentTheme != AppTheme.System) return;

        var effective = ResolveEffective(AppTheme.System);
        if (effective == _effectiveTheme) return;

        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(() => ApplyEffective(effective, raiseEvent: true));
    }

    private void ApplyEffective(AppTheme effective, bool raiseEvent)
    {
        var app = Application.Current;
        if (app is null)
        {
            // Tests u otros contextos sin Application activa: solo actualizamos el state.
            _effectiveTheme = effective;
            if (raiseEvent) EffectiveThemeChanged?.Invoke(this, effective);
            return;
        }

        var dictionaries = app.Resources.MergedDictionaries;
        var (toRemove, toAdd) = effective == AppTheme.Light
            ? (DarkResources, LightResources)
            : (LightResources, DarkResources);

        // Remover las del tema saliente. Comparamos por suffix del Source URI: en design-time
        // el path puede venir como "pack://..." y en runtime como relative; el endsWith
        // captura ambos casos.
        for (var i = dictionaries.Count - 1; i >= 0; i--)
        {
            var src = dictionaries[i].Source?.ToString();
            if (src is null) continue;
            if (toRemove.Any(r => src.EndsWith(r, StringComparison.OrdinalIgnoreCase)))
            {
                dictionaries.RemoveAt(i);
            }
        }

        // Agregar las del tema entrante (skip si ya están — Apply puede llamarse dos veces).
        foreach (var resource in toAdd)
        {
            var alreadyLoaded = dictionaries.Any(d =>
                d.Source?.ToString().EndsWith(resource, StringComparison.OrdinalIgnoreCase) == true);
            if (alreadyLoaded) continue;

            try
            {
                var dict = new ResourceDictionary
                {
                    Source = new Uri(resource, UriKind.Relative),
                };
                dictionaries.Add(dict);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo cargar el ResourceDictionary {Resource}", resource);
            }
        }

        _effectiveTheme = effective;
        _logger.LogInformation("Tema aplicado: {Current} → effective {Effective}", _currentTheme, effective);
        if (raiseEvent) EffectiveThemeChanged?.Invoke(this, effective);
    }

    private AppTheme ResolveEffective(AppTheme theme)
    {
        if (theme == AppTheme.Dark) return AppTheme.Dark;
        if (theme == AppTheme.Light) return AppTheme.Light;

        // System: leemos el registry de Windows. AppsUseLightTheme=1 → Light, =0 → Dark.
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: false);
            var value = key?.GetValue(AppsUseLightThemeValue);
            if (value is int v) return v == 1 ? AppTheme.Light : AppTheme.Dark;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo leer AppsUseLightTheme; fallback a Dark");
        }
        return AppTheme.Dark;
    }
}

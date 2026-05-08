using System.Reflection;

namespace Spikit.ViewModels.Settings.Sections;

// VM mínimo de "Sobre la app" (EP-4.9). Lee la versión del assembly y la formatea
// como "X.Y.Z". El ticket pide "sin links, sin créditos, sin commit hash" — si el
// usuario quiere más, accede al repo público.
//
// Formato:
//   - "0.1.0" cuando la versión es 0.1.0(.0). El ".0" trailing del Build se omite
//     siempre — la convención SemVer del producto es Major.Minor.Patch, y la cuarta
//     posición del System.Version no tiene significado en V1.
//   - "0.0.0" si el assembly no expone versión (caso defensivo, no esperado en builds
//     reales del .csproj con <Version> definido).
public sealed class AboutSectionViewModel : ViewModelBase
{
    public AboutSectionViewModel()
        : this(Assembly.GetExecutingAssembly())
    {
    }

    // Constructor de tests: pasar un assembly específico para verificar el formateo
    // sin depender de la versión del .csproj.
    public AboutSectionViewModel(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        VersionLabel = FormatVersion(assembly.GetName().Version);
    }

    public string ProductName => "Spikit";
    public string VersionLabel { get; }
    public string FullVersionLabel => $"{ProductName} {VersionLabel}";

    private static string FormatVersion(Version? v)
    {
        if (v is null) return "0.0.0";
        return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
    }
}

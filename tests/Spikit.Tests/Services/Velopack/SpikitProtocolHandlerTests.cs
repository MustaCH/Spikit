using Microsoft.Win32;
using Spikit.Services.Velopack;

namespace Spikit.Tests.Services.Velopack;

// Tests del registro real en HKCU. Para no contaminar el HKCU\Software\Classes\spikit del
// dev (que puede tener una instalación legítima apuntando a Spikit.exe), los tests usan
// un scheme dedicado `spikit-test` — el handler escribe en
// HKCU\Software\Classes\spikit-test y los assertions leen de ahí. El comportamiento
// verificado es idéntico (mismas keys, mismos values, misma forma); solo el namespace
// cambia. EP-10.15.
//
// Estos tests SOLO corren en Windows (target net8.0-windows del proyecto de tests).
public class SpikitProtocolHandlerTests : IDisposable
{
    // Scheme dedicado para tests. Cualquier dev legítimamente instalado tiene
    // HKCU\Software\Classes\spikit; nosotros usamos `spikit-test` para no tocarlo.
    private const string TestScheme = "spikit-test";

    // Paths que el handler genera para el scheme de test. Si la clase cambia la estructura
    // de las keys, estos tests fallan con KeyDoesntExist y nos avisa.
    private const string RootKeyPath = @"Software\Classes\" + TestScheme;
    private const string CommandKeyPath = RootKeyPath + @"\shell\open\command";
    private const string IconKeyPath = RootKeyPath + @"\DefaultIcon";

    public SpikitProtocolHandlerTests()
    {
        // Cleanup defensivo previo: en caso de test anterior fallido, arrancamos limpios.
        // No toca el scheme productivo (`spikit`) — solo el de test (`spikit-test`).
        Cleanup();
    }

    public void Dispose() => Cleanup();

    private static void Cleanup()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(RootKeyPath, throwOnMissingSubKey: false);
        }
        catch
        {
            // Best-effort. Si la key está locked o no se puede borrar, los tests siguientes
            // que dependan de ausencia de keys van a fallar y nos vamos a enterar.
        }
    }

    [Fact]
    public void Register_creates_root_key_with_url_protocol_marker()
    {
        SpikitProtocolHandler.Register(scheme: TestScheme);

        using var root = Registry.CurrentUser.OpenSubKey(RootKeyPath, writable: false);
        Assert.NotNull(root);
        Assert.Equal("URL:Spikit Protocol", root!.GetValue(string.Empty));
        // "URL Protocol" debe existir como value (string vacío). Es el marker que Windows
        // exige para reconocer la key como URL handler vs file association.
        Assert.Equal(string.Empty, root.GetValue("URL Protocol"));
    }

    [Fact]
    public void Register_creates_default_icon_pointing_to_current_exe()
    {
        SpikitProtocolHandler.Register(scheme: TestScheme);

        using var icon = Registry.CurrentUser.OpenSubKey(IconKeyPath, writable: false);
        Assert.NotNull(icon);

        var value = (string?)icon!.GetValue(string.Empty);
        Assert.NotNull(value);
        // Formato esperado: "\"<exepath>\",0". No fijo el path exacto porque en tests es
        // el testhost; solo verifico forma.
        Assert.StartsWith("\"", value);
        Assert.EndsWith("\",0", value);
    }

    [Fact]
    public void Register_creates_command_with_quoted_exe_and_percent1()
    {
        SpikitProtocolHandler.Register(scheme: TestScheme);

        using var cmd = Registry.CurrentUser.OpenSubKey(CommandKeyPath, writable: false);
        Assert.NotNull(cmd);

        var value = (string?)cmd!.GetValue(string.Empty);
        Assert.NotNull(value);
        // Formato esperado: "\"<exepath>\" \"%1\"". Las comillas alrededor de %1 son
        // críticas: la URL puede tener `&` y otros caracteres que un shell parsearía.
        Assert.StartsWith("\"", value);
        Assert.EndsWith("\" \"%1\"", value);
    }

    [Fact]
    public void Unregister_removes_all_keys()
    {
        SpikitProtocolHandler.Register(scheme: TestScheme);
        // Sanity check: las keys quedaron escritas.
        Assert.NotNull(Registry.CurrentUser.OpenSubKey(RootKeyPath, writable: false));

        SpikitProtocolHandler.Unregister(scheme: TestScheme);

        Assert.Null(Registry.CurrentUser.OpenSubKey(RootKeyPath, writable: false));
        Assert.Null(Registry.CurrentUser.OpenSubKey(CommandKeyPath, writable: false));
        Assert.Null(Registry.CurrentUser.OpenSubKey(IconKeyPath, writable: false));
    }

    [Fact]
    public void Unregister_is_no_op_when_key_does_not_exist()
    {
        // Garantizamos que no existe antes.
        Cleanup();
        Assert.Null(Registry.CurrentUser.OpenSubKey(RootKeyPath, writable: false));

        // No debe tirar excepción aunque la key no existe (uninstall de máquina nueva, o
        // doble-uninstall por algún edge case de Velopack).
        SpikitProtocolHandler.Unregister(scheme: TestScheme);
    }

    [Fact]
    public void Register_is_idempotent_when_called_twice()
    {
        SpikitProtocolHandler.Register(scheme: TestScheme);
        SpikitProtocolHandler.Register(scheme: TestScheme);

        using var root = Registry.CurrentUser.OpenSubKey(RootKeyPath, writable: false);
        Assert.NotNull(root);
        Assert.Equal("URL:Spikit Protocol", root!.GetValue(string.Empty));
        // Solo deben existir las 3 subkeys (DefaultIcon, shell). No accumular basura.
        var subkeys = root.GetSubKeyNames();
        Assert.Equal(2, subkeys.Length);
        Assert.Contains("DefaultIcon", subkeys);
        Assert.Contains("shell", subkeys);
    }

    [Fact]
    public void Register_does_not_touch_production_scheme()
    {
        // Capturamos el estado previo del scheme productivo (puede o no existir según si
        // el dev tiene Spikit instalado). Después de un Register/Unregister con el scheme
        // de test, el estado del scheme productivo debe ser EXACTAMENTE el mismo.
        const string ProductionPath = @"Software\Classes\" + SpikitProtocolHandler.DefaultScheme;
        var prodExistedBefore = Registry.CurrentUser.OpenSubKey(ProductionPath, writable: false) is not null;

        SpikitProtocolHandler.Register(scheme: TestScheme);
        SpikitProtocolHandler.Unregister(scheme: TestScheme);

        var prodExistsAfter = Registry.CurrentUser.OpenSubKey(ProductionPath, writable: false) is not null;
        Assert.Equal(prodExistedBefore, prodExistsAfter);
    }

    [Fact]
    public void Register_throws_when_scheme_is_empty()
    {
        Assert.Throws<ArgumentException>(() => SpikitProtocolHandler.Register(scheme: string.Empty));
    }

    [Fact]
    public void Unregister_throws_when_scheme_is_empty()
    {
        Assert.Throws<ArgumentException>(() => SpikitProtocolHandler.Unregister(scheme: string.Empty));
    }
}

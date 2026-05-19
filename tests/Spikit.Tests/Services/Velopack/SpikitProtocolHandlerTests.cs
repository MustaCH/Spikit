using Microsoft.Win32;
using Spikit.Services.Velopack;

namespace Spikit.Tests.Services.Velopack;

// Tests del registro real en HKCU. Cada test borra la key antes y después para asegurar
// aislamiento — si el dev tiene Spikit instalado en su máquina, el test re-registra al
// finalizar nada, pero un re-install del instalador va a re-registrar correctamente.
//
// Estos tests SOLO corren en Windows (target net8.0-windows del proyecto de tests).
public class SpikitProtocolHandlerTests : IDisposable
{
    // Mismo path que la clase bajo test usa (chequeado con strings en el código). Si la
    // clase cambia de scheme, este test fallaría con KeyDoesntExist y nos avisa.
    private const string RootKeyPath = @"Software\Classes\spikit";
    private const string CommandKeyPath = RootKeyPath + @"\shell\open\command";
    private const string IconKeyPath = RootKeyPath + @"\DefaultIcon";

    public SpikitProtocolHandlerTests()
    {
        // Cleanup defensivo previo: en caso de test anterior fallido o de un Spikit
        // realmente instalado, arrancamos limpios.
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
        SpikitProtocolHandler.Register();

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
        SpikitProtocolHandler.Register();

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
        SpikitProtocolHandler.Register();

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
        SpikitProtocolHandler.Register();
        // Sanity check: las keys quedaron escritas.
        Assert.NotNull(Registry.CurrentUser.OpenSubKey(RootKeyPath, writable: false));

        SpikitProtocolHandler.Unregister();

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
        SpikitProtocolHandler.Unregister();
    }

    [Fact]
    public void Register_is_idempotent_when_called_twice()
    {
        SpikitProtocolHandler.Register();
        SpikitProtocolHandler.Register();

        using var root = Registry.CurrentUser.OpenSubKey(RootKeyPath, writable: false);
        Assert.NotNull(root);
        Assert.Equal("URL:Spikit Protocol", root!.GetValue(string.Empty));
        // Solo deben existir las 3 subkeys (DefaultIcon, shell). No accumular basura.
        var subkeys = root.GetSubKeyNames();
        Assert.Equal(2, subkeys.Length);
        Assert.Contains("DefaultIcon", subkeys);
        Assert.Contains("shell", subkeys);
    }
}

using System.Reflection;
using Spikit.ViewModels.Settings.Sections;

namespace Spikit.Tests.ViewModels.Settings.Sections;

public class AboutSectionViewModelTests
{
    [Fact]
    public void Formats_three_part_version_dropping_trailing_build()
    {
        // Construimos un assembly stub via dynamic assembly. Como no es práctico crear un
        // Assembly real con una versión arbitraria desde xUnit, validamos el helper con
        // un constructor que recibe Assembly y forzamos la version vía AssemblyName.
        var name = new AssemblyName("Stub") { Version = new Version(0, 1, 0, 0) };
        var asm = AssemblyBuilderShim.Build(name);

        var vm = new AboutSectionViewModel(asm);

        Assert.Equal("0.1.0", vm.VersionLabel);
        Assert.Equal("Spikit 0.1.0", vm.FullVersionLabel);
    }

    [Fact]
    public void Formats_version_with_zero_build()
    {
        var name = new AssemblyName("Stub") { Version = new Version(1, 2, 0) };
        var asm = AssemblyBuilderShim.Build(name);

        var vm = new AboutSectionViewModel(asm);

        Assert.Equal("1.2.0", vm.VersionLabel);
    }

    [Fact]
    public void Formats_two_part_version_filling_zero_build()
    {
        // System.Version permite Major.Minor sin Build (Build = -1). El VM clampa a 0.
        var name = new AssemblyName("Stub") { Version = new Version(2, 5) };
        var asm = AssemblyBuilderShim.Build(name);

        var vm = new AboutSectionViewModel(asm);

        Assert.Equal("2.5.0", vm.VersionLabel);
    }

    [Fact]
    public void Product_name_is_Spikit()
    {
        var vm = new AboutSectionViewModel();

        Assert.Equal("Spikit", vm.ProductName);
    }

    // Helper para crear un Assembly dinámico con un AssemblyName específico. Evita
    // depender de la versión real del assembly de Spikit.dll en el test runner.
    private static class AssemblyBuilderShim
    {
        public static Assembly Build(AssemblyName name)
            => System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
                name, System.Reflection.Emit.AssemblyBuilderAccess.Run);
    }
}

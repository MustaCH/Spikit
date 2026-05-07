using Microsoft.Extensions.Logging.Abstractions;
using Spikit.ViewModels.Onboarding;

namespace Spikit.Tests.ViewModels.Onboarding;

public class PruebaStepViewModelTests
{
    private static PruebaStepViewModel MakeVm() =>
        new(NullLogger<PruebaStepViewModel>.Instance);

    [Fact]
    public void Bootstrap_has_no_text_and_disabled_finalizar()
    {
        var vm = MakeVm();

        Assert.Equal(string.Empty, vm.Text);
        Assert.False(vm.HasText);
    }

    [Fact]
    public void Setting_text_with_content_enables_HasText()
    {
        var vm = MakeVm();

        vm.Text = "hola mundo";

        Assert.True(vm.HasText);
    }

    [Fact]
    public void Whitespace_only_does_not_count_as_HasText()
    {
        // CB-8: Whisper devuelve " " si no detecta audio. No queremos habilitar Finalizar
        // por eso — el usuario tiene que ver texto real para validar que el flow funcionó.
        var vm = MakeVm();

        vm.Text = "    \t\n";

        Assert.False(vm.HasText);
    }

    [Fact]
    public void TextStateChanged_fires_when_HasText_flips_true()
    {
        var vm = MakeVm();
        var fired = 0;
        vm.TextStateChanged += (_, _) => fired++;

        vm.Text = "x";

        Assert.Equal(1, fired);
    }

    [Fact]
    public void TextStateChanged_does_not_fire_when_HasText_unchanged()
    {
        var vm = MakeVm();
        vm.Text = "first";
        var fired = 0;
        vm.TextStateChanged += (_, _) => fired++;

        // Cambia el text pero HasText sigue true → no debería disparar.
        vm.Text = "second";

        Assert.Equal(0, fired);
    }

    [Fact]
    public void TextStateChanged_fires_when_HasText_flips_back_to_false()
    {
        var vm = MakeVm();
        vm.Text = "something";
        var fired = 0;
        vm.TextStateChanged += (_, _) => fired++;

        // Borrar todo el texto → HasText flip a false.
        vm.Text = string.Empty;

        Assert.Equal(1, fired);
        Assert.False(vm.HasText);
    }

    [Fact]
    public void Setting_same_text_does_not_fire_property_changed()
    {
        // SetProperty del ViewModelBase cortocircuita si el valor es igual — el evento
        // TextStateChanged tampoco debería dispararse.
        var vm = MakeVm();
        vm.Text = "ya estaba";
        var fired = 0;
        vm.TextStateChanged += (_, _) => fired++;

        vm.Text = "ya estaba";

        Assert.Equal(0, fired);
    }
}

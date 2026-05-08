using Spikit.Models;
using Spikit.Services.PlanInfo;
using Spikit.ViewModels.Settings.Sections;

namespace Spikit.Tests.ViewModels.Settings.Sections;

public class PlanSectionViewModelTests
{
    [Fact]
    public void Lifetime_only_plan_service_returns_Lifetime()
    {
        var service = new LifetimeOnlyPlanService();

        Assert.Equal(Plan.Lifetime, service.GetCurrent());
    }

    [Fact]
    public void Vm_renders_Lifetime_title_and_description()
    {
        var vm = new PlanSectionViewModel(new LifetimeOnlyPlanService());

        Assert.Equal("Lifetime access", vm.PlanLabel);
        Assert.Equal("Plan actual: Lifetime access", vm.PlanTitle);
        Assert.Contains("API key", vm.PlanDescription);
    }

    [Fact]
    public void Vm_disables_upgrade_in_v1()
    {
        var vm = new PlanSectionViewModel(new LifetimeOnlyPlanService());

        Assert.False(vm.CanUpgradeToPro);
    }
}

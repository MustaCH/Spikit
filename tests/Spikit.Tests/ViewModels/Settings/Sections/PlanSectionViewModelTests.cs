using Spikit.Models;
using Spikit.Services.PlanInfo;
using Spikit.ViewModels.Settings.Sections;

namespace Spikit.Tests.ViewModels.Settings.Sections;

public class PlanSectionViewModelTests
{
    [Fact]
    public void V1_plan_service_returns_BYOK()
    {
        var service = new V1PlanService();

        Assert.Equal(Plan.BYOK, service.GetCurrent());
    }

    [Fact]
    public void Vm_renders_BYOK_title_and_description()
    {
        var vm = new PlanSectionViewModel(new V1PlanService());

        Assert.Equal("BYOK", vm.PlanLabel);
        Assert.Equal("Plan actual: BYOK", vm.PlanTitle);
        Assert.Contains("API key", vm.PlanDescription);
    }

    [Fact]
    public void Vm_disables_upgrade_in_v1()
    {
        var vm = new PlanSectionViewModel(new V1PlanService());

        Assert.False(vm.CanUpgradeToPro);
    }

    [Fact]
    public void Vm_renders_Pro_when_service_returns_Pro()
    {
        var vm = new PlanSectionViewModel(new FakePlanService(Plan.Pro));

        Assert.Equal("Pro", vm.PlanLabel);
        Assert.Equal("Plan actual: Pro", vm.PlanTitle);
        Assert.Contains("Pro", vm.PlanDescription);
    }

    private sealed class FakePlanService : IPlanService
    {
        private readonly Plan _plan;
        public FakePlanService(Plan plan) => _plan = plan;
        public Plan GetCurrent() => _plan;
    }
}

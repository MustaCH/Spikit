using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Models;
using Spikit.Services.Toast;

namespace Spikit.Tests.Services.Toast;

public class ToastServiceTests
{
    private readonly FakeToastHost _host = new();

    private ToastService BuildService() => new(_host, NullLogger<ToastService>.Instance);

    [Fact]
    public void Show_invokes_host_show_with_notification()
    {
        var service = BuildService();

        service.Show(ToastSeverity.Info, "Hola");

        Assert.Single(_host.Shown);
        Assert.Equal("Hola", _host.Shown[0].Notification.Title);
        Assert.Equal(ToastSeverity.Info, _host.Shown[0].Notification.Severity);
    }

    [Fact]
    public void Show_with_no_autoDismiss_uses_default_5s()
    {
        var service = BuildService();

        service.Show(ToastSeverity.Warning, "Test");

        Assert.Equal(IToastService.DefaultAutoDismiss, _host.Shown[0].Notification.AutoDismiss);
    }

    [Fact]
    public void Show_propagates_action_label()
    {
        var service = BuildService();
        var action = new ToastAction("Abrir Settings", () => { });

        service.Show(ToastSeverity.Info, "Test", action: action);

        Assert.Equal("Abrir Settings", _host.Shown[0].Notification.Action?.Label);
    }

    [Fact]
    public void Show_dedupes_by_key_and_refreshes_existing_toast()
    {
        var service = BuildService();

        service.Show(ToastSeverity.Info, "Sin audio (1)", dedupeKey: "no-audio");
        service.Show(ToastSeverity.Info, "Sin audio (2)", dedupeKey: "no-audio");

        // Solo un Show; el segundo es Refresh con el título nuevo.
        Assert.Single(_host.Shown);
        Assert.Single(_host.Refreshed);
        Assert.Equal("Sin audio (2)", _host.Refreshed[0].Notification.Title);
        // El id del refresh es el mismo que el del show original.
        Assert.Equal(_host.Shown[0].Id, _host.Refreshed[0].Id);
    }

    [Fact]
    public void Show_without_dedupeKey_always_stacks_even_with_same_title()
    {
        var service = BuildService();

        service.Show(ToastSeverity.Info, "Mismo título");
        service.Show(ToastSeverity.Info, "Mismo título");

        Assert.Equal(2, _host.Shown.Count);
        Assert.Empty(_host.Refreshed);
    }

    [Fact]
    public void Show_evicts_oldest_when_max_visible_reached()
    {
        var service = BuildService();

        service.Show(ToastSeverity.Info, "uno");
        service.Show(ToastSeverity.Info, "dos");
        service.Show(ToastSeverity.Info, "tres");
        // Cuarto: el más antiguo ("uno") se desaloja.
        service.Show(ToastSeverity.Info, "cuatro");

        Assert.Equal(4, _host.Shown.Count);
        Assert.Single(_host.Dismissed);
        Assert.Equal(_host.Shown[0].Id, _host.Dismissed[0]);
    }

    [Fact]
    public void Show_evicts_in_FIFO_order_for_consecutive_overflow()
    {
        var service = BuildService();

        service.Show(ToastSeverity.Info, "uno");
        service.Show(ToastSeverity.Info, "dos");
        service.Show(ToastSeverity.Info, "tres");
        service.Show(ToastSeverity.Info, "cuatro"); // evict "uno"
        service.Show(ToastSeverity.Info, "cinco"); // evict "dos"

        Assert.Equal(2, _host.Dismissed.Count);
        Assert.Equal(_host.Shown[0].Id, _host.Dismissed[0]); // "uno"
        Assert.Equal(_host.Shown[1].Id, _host.Dismissed[1]); // "dos"
    }

    [Fact]
    public async Task Auto_dismiss_timer_invokes_host_Dismiss()
    {
        var service = BuildService();
        service.Show(ToastSeverity.Info, "Quick", autoDismiss: TimeSpan.FromMilliseconds(50));

        // Espera con margen al Timer.
        await Task.Delay(200);

        Assert.Single(_host.Dismissed);
        Assert.Equal(_host.Shown[0].Id, _host.Dismissed[0]);
    }

    [Fact]
    public async Task Dedupe_resets_auto_dismiss_timer()
    {
        var service = BuildService();
        service.Show(ToastSeverity.Info, "T1", autoDismiss: TimeSpan.FromMilliseconds(120), dedupeKey: "k");

        // A los 60ms, antes del expire original, refrescamos el toast.
        await Task.Delay(60);
        service.Show(ToastSeverity.Info, "T2", autoDismiss: TimeSpan.FromMilliseconds(120), dedupeKey: "k");

        // A los 100ms tras el segundo show, NO debería haberse disparado el dismiss
        // (el primer timer fue cancelado al refrescar).
        await Task.Delay(80);
        Assert.Empty(_host.Dismissed);

        // Esperando el expire del segundo timer (120ms desde el refresh), sí se dispara.
        await Task.Delay(150);
        Assert.Single(_host.Dismissed);
    }

    [Fact]
    public void Host_dismissed_event_cleans_internal_list_so_next_show_is_treated_as_fresh()
    {
        var service = BuildService();

        service.Show(ToastSeverity.Info, "uno");
        service.Show(ToastSeverity.Info, "dos");
        service.Show(ToastSeverity.Info, "tres");
        // Simulamos cierre manual (usuario clickeó la acción del primero).
        _host.SimulateUserClosed(_host.Shown[0].Id);
        // El servicio debería tener slot libre — un nuevo show NO desaloja.
        service.Show(ToastSeverity.Info, "cuatro");

        Assert.Empty(_host.Dismissed);
    }

    private sealed class FakeToastHost : IToastHost
    {
        public List<(Guid Id, ToastNotification Notification)> Shown { get; } = new();
        public List<(Guid Id, ToastNotification Notification)> Refreshed { get; } = new();
        public List<Guid> Dismissed { get; } = new();

        public event EventHandler<Guid>? DismissedEvent;

        event EventHandler<Guid>? IToastHost.Dismissed
        {
            add => DismissedEvent += value;
            remove => DismissedEvent -= value;
        }

        public void Show(Guid id, ToastNotification notification) => Shown.Add((id, notification));
        public void Refresh(Guid id, ToastNotification notification) => Refreshed.Add((id, notification));
        public void Dismiss(Guid id) => Dismissed.Add(id);

        // Helper de tests: simula que el host avisó "user closed this toast" (lo que
        // pasaría tras animación de salida de una window cerrada por click en acción).
        public void SimulateUserClosed(Guid id) => DismissedEvent?.Invoke(this, id);
    }
}

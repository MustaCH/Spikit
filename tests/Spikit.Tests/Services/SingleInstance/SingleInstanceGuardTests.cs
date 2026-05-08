using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Services.SingleInstance;

namespace Spikit.Tests.Services.SingleInstance;

// Tests de integración del guard real (mutex + named pipe). Los tests usan nombres
// aleatorizados con Guid para no chocar con la instancia real de la app si está corriendo
// y para no contaminarse entre sí. Sin prefijo `Global\` — la verificación funcional
// (mutex contention + pipe round-trip) no requiere multi-sesión y `Local\` (default)
// es suficiente y no requiere permisos.
public class SingleInstanceGuardTests
{
    private static SingleInstanceOptions UniqueOptions(int connectTimeoutMs = 1000)
    {
        var id = Guid.NewGuid().ToString("N");
        return new SingleInstanceOptions
        {
            MutexName = $"Spikit-Tests-{id}",
            PipeName = $"Spikit.Tests.{id}",
            ConnectTimeoutMilliseconds = connectTimeoutMs,
        };
    }

    private static SingleInstanceGuard NewGuard(SingleInstanceOptions options) =>
        new(options, NullLogger<SingleInstanceGuard>.Instance);

    // System.Threading.Mutex es reentrante en el mismo thread (un mismo thread que ya es
    // dueño del mutex puede WaitOne(0) de nuevo y retorna true). Para verificar el
    // bloqueo cross-instancia desde un sólo proceso, los guards "secondary" se crean
    // en un thread dedicado: TryAcquire y Dispose corren ahí, fuera del thread del test.
    // Esto refleja también el escenario real (cada instancia es un proceso/thread propio).
    private static T AcquireOnDedicatedThread<T>(Func<T> action)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("El thread auxiliar del test no terminó en 10s");
        }

        if (error is not null) throw error;
        return result;
    }

    [Fact]
    public void First_TryAcquire_returns_Primary()
    {
        var options = UniqueOptions();
        using var guard = NewGuard(options);

        var result = guard.TryAcquire();

        Assert.Equal(SingleInstanceAcquisition.Primary, result);
    }

    [Fact]
    public void TryAcquire_is_idempotent_on_same_guard()
    {
        var options = UniqueOptions();
        using var guard = NewGuard(options);

        var first = guard.TryAcquire();
        var second = guard.TryAcquire();

        Assert.Equal(SingleInstanceAcquisition.Primary, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Second_guard_with_same_options_returns_SecondaryNotified_and_primary_receives_event()
    {
        var options = UniqueOptions();
        using var primary = NewGuard(options);
        Assert.Equal(SingleInstanceAcquisition.Primary, primary.TryAcquire());

        using var openRequested = new ManualResetEventSlim(false);
        primary.OpenRequested += (_, _) => openRequested.Set();

        var secondaryResult = AcquireOnDedicatedThread(() =>
        {
            using var secondary = NewGuard(options);
            return secondary.TryAcquire();
        });
        Assert.Equal(SingleInstanceAcquisition.SecondaryNotified, secondaryResult);

        // El listener del primary corre en el threadpool. Damos hasta 3s para que procese
        // la conexión y dispare el evento — en hardware razonable lo hace en <100ms.
        Assert.True(openRequested.Wait(TimeSpan.FromSeconds(3)),
            "Primary debería haber recibido OpenRequested después del notify del secondary");
    }

    [Fact]
    public void Multiple_secondary_notifications_each_fire_OpenRequested()
    {
        var options = UniqueOptions();
        using var primary = NewGuard(options);
        Assert.Equal(SingleInstanceAcquisition.Primary, primary.TryAcquire());

        var count = 0;
        using var twoEvents = new ManualResetEventSlim(false);
        primary.OpenRequested += (_, _) =>
        {
            if (Interlocked.Increment(ref count) >= 2) twoEvents.Set();
        };

        for (var i = 0; i < 2; i++)
        {
            var result = AcquireOnDedicatedThread(() =>
            {
                using var secondary = NewGuard(options);
                return secondary.TryAcquire();
            });
            Assert.Equal(SingleInstanceAcquisition.SecondaryNotified, result);
        }

        Assert.True(twoEvents.Wait(TimeSpan.FromSeconds(3)),
            "El listener debería re-aceptar conexiones tras el primer mensaje");
    }

    [Fact]
    public void Disposing_primary_releases_mutex_so_next_guard_acquires_Primary()
    {
        var options = UniqueOptions();

        var first = NewGuard(options);
        Assert.Equal(SingleInstanceAcquisition.Primary, first.TryAcquire());
        first.Dispose();

        using var second = NewGuard(options);
        Assert.Equal(SingleInstanceAcquisition.Primary, second.TryAcquire());
    }

    [Fact]
    public void When_mutex_is_held_externally_but_pipe_is_dead_returns_SecondaryForwardFailed()
    {
        // Simula el caso "primera instancia zombie": un thread externo tiene el mutex y
        // mantiene su propiedad sin levantar pipe. El guard del test corre en su propio
        // thread y no puede ser dueño del mutex (cross-thread), así que TryAcquire ve
        // mutex tomado, intenta conectar al pipe inexistente y cae en SecondaryForwardFailed.
        var options = UniqueOptions(connectTimeoutMs: 250);

        using var releaseSignal = new ManualResetEventSlim(false);
        using var mutexAcquired = new ManualResetEventSlim(false);
        Exception? holderError = null;

        var holderThread = new Thread(() =>
        {
            try
            {
                using var orphanMutex = new Mutex(initiallyOwned: true, options.MutexName, out var createdNew);
                if (!createdNew) throw new InvalidOperationException("Mutex colisionó con otro test");
                mutexAcquired.Set();
                releaseSignal.Wait();
                orphanMutex.ReleaseMutex();
            }
            catch (Exception ex) { holderError = ex; }
        }) { IsBackground = true };
        holderThread.Start();

        Assert.True(mutexAcquired.Wait(TimeSpan.FromSeconds(2)), "Holder thread no adquirió el mutex");

        try
        {
            var result = AcquireOnDedicatedThread(() =>
            {
                using var guard = NewGuard(options);
                return guard.TryAcquire();
            });
            Assert.Equal(SingleInstanceAcquisition.SecondaryForwardFailed, result);
        }
        finally
        {
            releaseSignal.Set();
            holderThread.Join(TimeSpan.FromSeconds(2));
        }

        Assert.Null(holderError);
    }

    [Fact]
    public void Dispose_is_safe_to_call_multiple_times()
    {
        var options = UniqueOptions();
        var guard = NewGuard(options);
        guard.TryAcquire();

        guard.Dispose();
        guard.Dispose();
    }

    [Fact]
    public void Dispose_without_TryAcquire_does_not_throw()
    {
        var guard = NewGuard(UniqueOptions());
        guard.Dispose();
    }
}

using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace Spikit.Services.SingleInstance;

// Implementación canónica de RN-9 / CB-11 (single-instance + bring-to-front).
//
// Diseño:
// - Mutex global con nombre fijo (`Global\Spikit-SingleInstance`) detecta colisión
//   inter-proceso. La adquisición es no-bloqueante (`WaitOne(0)`) — si retorna false,
//   alguien más tiene el slot.
// - Named pipe server con ACL abierta (Everyone Read+Write) levantado en background
//   thread del threadpool. Acepta conexiones serializadas y dispara el evento OpenRequested
//   cada vez que recibe el literal `OPEN_SETTINGS\n`.
// - La segunda instancia abre un NamedPipeClientStream, escribe OPEN_SETTINGS y termina.
// - Si el mutex está tomado pero el pipe no responde dentro del timeout (2s default),
//   se asume que la "primera" es zombie y se degrada a Primary sin mutex (queda logueado).
internal sealed class SingleInstanceGuard : ISingleInstanceGuard
{
    private const string OpenSettingsMessage = "OPEN_SETTINGS";

    private readonly SingleInstanceOptions _options;
    private readonly ILogger<SingleInstanceGuard> _logger;

    private readonly object _gate = new();

    private Mutex? _mutex;
    private bool _mutexAcquired;
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;
    private SingleInstanceAcquisition? _result;
    private bool _disposed;

    public event EventHandler? OpenRequested;

    public SingleInstanceGuard(SingleInstanceOptions options, ILogger<SingleInstanceGuard> logger)
    {
        _options = options;
        _logger = logger;
    }

    public SingleInstanceAcquisition TryAcquire()
    {
        lock (_gate)
        {
            if (_result is { } cached) return cached;
            ObjectDisposedException.ThrowIf(_disposed, this);

            _mutex = new Mutex(initiallyOwned: false, _options.MutexName);
            try
            {
                _mutexAcquired = _mutex.WaitOne(millisecondsTimeout: 0);
            }
            catch (AbandonedMutexException)
            {
                // La instancia anterior murió sin liberar — hereda el mutex limpio.
                _logger.LogWarning(
                    "Mutex {Mutex} encontrado en estado abandonado — instancia previa terminó sin cleanup",
                    _options.MutexName);
                _mutexAcquired = true;
            }

            if (_mutexAcquired)
            {
                StartListener();
                _result = SingleInstanceAcquisition.Primary;
                _logger.LogInformation(
                    "Single-instance: PRIMARY (mutex {Mutex} adquirido, pipe {Pipe} escuchando)",
                    _options.MutexName, _options.PipeName);
                return _result.Value;
            }

            // Mutex tomado por otra instancia — soltar handle local antes de intentar IPC.
            _mutex.Dispose();
            _mutex = null;

            if (TryNotifyExisting())
            {
                _result = SingleInstanceAcquisition.SecondaryNotified;
                _logger.LogInformation(
                    "Single-instance: SECONDARY — OPEN_SETTINGS enviado a {Pipe}, terminando",
                    _options.PipeName);
                return _result.Value;
            }

            _logger.LogWarning(
                "Single-instance: mutex tomado pero pipe {Pipe} no respondió en {Timeout} ms — instancia primaria probablemente zombie, degradando a primary sin mutex",
                _options.PipeName, _options.ConnectTimeoutMilliseconds);
            _result = SingleInstanceAcquisition.SecondaryForwardFailed;
            return _result.Value;
        }
    }

    private bool TryNotifyExisting()
    {
        try
        {
            using var client = new NamedPipeClientStream(
                serverName: ".",
                pipeName: _options.PipeName,
                direction: PipeDirection.Out);

            client.Connect(_options.ConnectTimeoutMilliseconds);

            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(OpenSettingsMessage);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Error de IO escribiendo OPEN_SETTINGS al pipe primario");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Acceso denegado al pipe primario — ACL inesperada");
            return false;
        }
    }

    private void StartListener()
    {
        _listenerCts = new CancellationTokenSource();
        var ct = _listenerCts.Token;
        _listenerTask = Task.Run(() => ListenLoopAsync(ct), CancellationToken.None);
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        var security = BuildPipeSecurity();

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(
                    pipeName: _options.PipeName,
                    direction: PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    transmissionMode: PipeTransmissionMode.Byte,
                    options: PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 0,
                    pipeSecurity: security);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);

                if (string.Equals(line, OpenSettingsMessage, StringComparison.Ordinal))
                {
                    _logger.LogInformation("Pipe IPC: OPEN_SETTINGS recibido");
                    RaiseOpenRequested();
                }
                else
                {
                    _logger.LogWarning("Pipe IPC: mensaje desconocido recibido ({Line})", line ?? "<null>");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Listener IPC tiró excepción — reintentando en 500ms");
                try { await Task.Delay(500, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private void RaiseOpenRequested()
    {
        try
        {
            OpenRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // El subscriber falló — no debería derribar el listener loop.
            _logger.LogError(ex, "Subscriber de OpenRequested tiró excepción");
        }
    }

    // ACL abierta (Everyone Read+Write). Justifica multi-sesión / RDP: el mutex Global
    // bloquea inter-sesión, así que la segunda instancia debe poder mandar el mensaje
    // aunque corra como otro usuario logueado en la misma máquina.
    private static PipeSecurity BuildPipeSecurity()
    {
        var security = new PipeSecurity();
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        security.AddAccessRule(new PipeAccessRule(
            everyone,
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        return security;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try { _listenerCts?.Cancel(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error cancelando listener IPC"); }

        try { _listenerTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { /* tareas canceladas — esperado */ }
        catch (Exception ex) { _logger.LogWarning(ex, "Error esperando listener IPC"); }

        try { _listenerCts?.Dispose(); } catch { }

        if (_mutex is not null)
        {
            try
            {
                if (_mutexAcquired) _mutex.ReleaseMutex();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error liberando mutex {Mutex}", _options.MutexName);
            }

            _mutex.Dispose();
            _mutex = null;
        }
    }
}

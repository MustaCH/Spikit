using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Spikit.Services.Audio;

public sealed class AudioCaptureService : IAudioCaptureService
{
    private const int SampleRateHz = 16_000;
    private const int Channels = 1;
    private const int BitsPerSample = 16;
    private const int WasapiBufferMs = 10;

    // 30 ms a 16 kHz mono = 480 samples por ventana de RMS.
    private const int RmsWindowSamples = SampleRateHz * 30 / 1000;

    // Thresholds del SLA. Warm-cold se trata como default (cubre cold-from-launch).
    private const double WarmConsecGapSeconds = 20.0;
    private const double WarmConsecP99Ms = 200.0;
    private const double WarmColdP99Ms = 1500.0;

    private readonly ILogger<AudioCaptureService> _logger;
    private readonly AudioRuntimeOptions _runtimeOptions;

    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private WasapiCapture? _capture;

    // Buffer rolling para acumular samples hasta llegar a una ventana de RMS.
    private readonly short[] _rmsBuffer = new short[RmsWindowSamples];
    private int _rmsBufferIndex;

    private AudioCaptureState _state = AudioCaptureState.Idle;
    private long _startTicks;
    private DateTime? _lastStopUtc;
    private bool _firstSampleSeen;
    private bool _disposed;
    private TaskCompletionSource? _stopCompletion;

    public event EventHandler<AudioCaptureState>? StateChanged;
    public event EventHandler<float>? RmsLevelChanged;
    public event EventHandler<short[]>? SamplesAvailable;

    public AudioCaptureService(ILogger<AudioCaptureService> logger, AudioRuntimeOptions runtimeOptions)
    {
        _logger = logger;
        _runtimeOptions = runtimeOptions;
    }

    public Task StartAsync(CancellationToken ct)
    {
        EnsureNotDisposed();
        if (_state != AudioCaptureState.Idle)
        {
            throw new InvalidOperationException(
                $"AudioCaptureService no está en Idle (estado actual: {_state}).");
        }

        ct.ThrowIfCancellationRequested();

        TransitionTo(AudioCaptureState.Initializing);
        _firstSampleSeen = false;
        _rmsBufferIndex = 0;
        _startTicks = Stopwatch.GetTimestamp();

        // Lectura fresca del deviceId en cada StartAsync — no se cachea (EP-4.10 AC).
        // Esto permite hot-swap entre sesiones: el usuario cambia el device en Settings,
        // la próxima vez que dicta usa el nuevo sin reiniciar.
        var requestedDeviceId = _runtimeOptions.DeviceId ?? string.Empty;

        try
        {
            _enumerator = new MMDeviceEnumerator();
            _device = ResolveDevice(_enumerator, requestedDeviceId);
            _capture = new WasapiCapture(_device, useEventSync: false, audioBufferMillisecondsLength: WasapiBufferMs)
            {
                WaveFormat = new WaveFormat(SampleRateHz, BitsPerSample, Channels),
            };
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
        }
        catch (AudioDeviceUnavailableException)
        {
            DisposeCaptureChain();
            TransitionTo(AudioCaptureState.Idle);
            throw;
        }
        catch
        {
            DisposeCaptureChain();
            TransitionTo(AudioCaptureState.Idle);
            throw;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (_disposed || _state == AudioCaptureState.Idle)
        {
            return Task.CompletedTask;
        }
        if (_state == AudioCaptureState.Stopping)
        {
            return _stopCompletion?.Task ?? Task.CompletedTask;
        }

        _stopCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TransitionTo(AudioCaptureState.Stopping);

        try
        {
            _capture?.StopRecording();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error en WasapiCapture.StopRecording");
            _stopCompletion.TrySetResult();
        }

        // Cierre real + transición a Idle pasa en RecordingStopped (NAudio drena buffers).
        return _stopCompletion.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _capture?.StopRecording();
        }
        catch
        {
            // tragado: ya estamos en disposing
        }

        DisposeCaptureChain();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // Callback ejecutado siempre desde el mismo thread interno de NAudio (sin locks).
        if (_state == AudioCaptureState.Stopping || _state == AudioCaptureState.Idle) return;

        if (!_firstSampleSeen && PcmHelpers.HasNonZeroSample(e.Buffer, e.BytesRecorded))
        {
            _firstSampleSeen = true;
            var latencyMs = (Stopwatch.GetTimestamp() - _startTicks) * 1000.0 / Stopwatch.Frequency;
            LogStartLatency(latencyMs);
            TransitionTo(AudioCaptureState.Recording);
        }

        if (e.BytesRecorded < 2) return;

        var samples = PcmHelpers.BytesToShorts(e.Buffer, e.BytesRecorded);
        SamplesAvailable?.Invoke(this, samples);

        EmitRmsForChunk(samples);
    }

    private void EmitRmsForChunk(short[] samples)
    {
        var offset = 0;
        while (offset < samples.Length)
        {
            var spaceLeft = RmsWindowSamples - _rmsBufferIndex;
            var toCopy = Math.Min(spaceLeft, samples.Length - offset);
            Array.Copy(samples, offset, _rmsBuffer, _rmsBufferIndex, toCopy);
            _rmsBufferIndex += toCopy;
            offset += toCopy;

            if (_rmsBufferIndex == RmsWindowSamples)
            {
                var rms = PcmHelpers.Rms(_rmsBuffer);
                RmsLevelChanged?.Invoke(this, rms);
                _rmsBufferIndex = 0;
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            _logger.LogWarning(e.Exception, "WasapiCapture terminó con excepción");
        }

        DisposeCaptureChain();
        _lastStopUtc = DateTime.UtcNow;
        TransitionTo(AudioCaptureState.Idle);
        _stopCompletion?.TrySetResult();
        _stopCompletion = null;
    }

    private void DisposeCaptureChain()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }
        _enumerator?.Dispose();
        _enumerator = null;
        _device = null;
    }

    private void TransitionTo(AudioCaptureState next)
    {
        if (_state == next) return;
        _state = next;
        StateChanged?.Invoke(this, next);
    }

    private void LogStartLatency(double latencyMs)
    {
        var (scenario, threshold) = ClassifyStart();
        if (latencyMs > threshold)
        {
            _logger.LogWarning(
                "AudioCapture start excedió SLA ({Scenario}): {LatencyMs:F1} ms > {ThresholdMs:F0} ms",
                scenario, latencyMs, threshold);
        }
        else
        {
            _logger.LogInformation(
                "AudioCapture start OK ({Scenario}): {LatencyMs:F1} ms",
                scenario, latencyMs);
        }
    }

    private (string scenario, double thresholdMs) ClassifyStart()
    {
        if (_lastStopUtc is null) return ("cold-from-launch", WarmColdP99Ms);
        var gap = (DateTime.UtcNow - _lastStopUtc.Value).TotalSeconds;
        return gap < WarmConsecGapSeconds
            ? ("warm-consec", WarmConsecP99Ms)
            : ("warm-cold", WarmColdP99Ms);
    }

    private void EnsureNotDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AudioCaptureService));
    }

    // Resuelve el MMDevice a abrir según AudioRuntimeOptions:
    //   - DeviceId vacío → default del sistema (Role.Console).
    //   - DeviceId presente → buscar en la enumeración Active. Si NO está, throw
    //     AudioDeviceUnavailableException — el orchestrator lo cablea a un toast EP-5.3.
    //
    // Por qué no usamos GetDevice(id) directo: lanza COMException o devuelve un device
    // en un estado inválido si el id ya no existe. Enumerar Active y matchear es más
    // explícito y evita un edge case raro (device con id válido pero state=Disabled
    // que NAudio acepta y después falla al StartRecording).
    private MMDevice ResolveDevice(MMDeviceEnumerator enumerator, string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        }

        var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        foreach (var candidate in endpoints)
        {
            if (string.Equals(candidate.ID, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
            candidate.Dispose();
        }

        throw new AudioDeviceUnavailableException(
            deviceId,
            "El micrófono configurado ya no está disponible. Conectalo de nuevo o elegí otro en Settings → Audio.");
    }
}

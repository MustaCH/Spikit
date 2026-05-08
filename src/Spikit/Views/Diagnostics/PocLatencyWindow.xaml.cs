using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Spikit.Native;

namespace Spikit.Views.Diagnostics;

// POC EP-1 (sub-task 86ah83zw4): mide latencia desde press del hotkey hasta primer
// sample no-cero de WasapiCapture en modo shared. Autocontenida (no usa
// HotkeyService/AudioCaptureService). Solo se levanta con --diagnostics-poc; el
// flow normal de la app no la toca.
public partial class PocLatencyWindow : Window
{
    private const int HotkeyId = 0x9001;
    private const uint VK_M = 0x4D;
    private const int AudioBufferMs = 10;
    private const int ResampledRate = 16000;
    private const int TargetMeasurements = 50;

    private const string CsvHeaderV2 =
        "timestamp,setup_label,mode,latency_ms,sample_rate,buffer_size_ms";

    private readonly ILogger<PocLatencyWindow> _logger;

    private HwndSource? _hwndSource;
    private bool _hotkeyRegistered;
    private bool _measuring;
    private bool _resampleForCurrentMeasurement;
    private long _t0Ticks;
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private WasapiCapture? _capture;
    private int _measurementCount;

    private static string CsvPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Spikit",
        "poc-latencia.csv");

    public PocLatencyWindow(ILogger<PocLatencyWindow> logger)
    {
        _logger = logger;
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        EnsureCsvSchemaCompatible();

        var helper = new WindowInteropHelper(this);
        _hwndSource = HwndSource.FromHwnd(helper.Handle);
        _hwndSource?.AddHook(WndProc);

        var modifiers = (uint)(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.NoRepeat);
        _hotkeyRegistered = User32.RegisterHotKey(helper.Handle, HotkeyId, modifiers, VK_M);

        if (!_hotkeyRegistered)
        {
            var err = Marshal.GetLastWin32Error();
            StatusText.Text = $"Error registrando Ctrl+Alt+M (Win32 err {err}). ¿Otra app lo está usando?";
            _logger.LogError("RegisterHotKey failed with Win32 error {Error}", err);
        }
        else
        {
            _logger.LogInformation("POC latencia: hotkey Ctrl+Alt+M registrado, CSV en {Path}", CsvPath);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hotkeyRegistered && _hwndSource is not null)
        {
            User32.UnregisterHotKey(_hwndSource.Handle, HotkeyId);
            _hotkeyRegistered = false;
        }
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        DisposeCapture();
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WindowMessages.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            // t0 lo más cerca posible del despacho del WM_HOTKEY (= press real, post-OS).
            _t0Ticks = Stopwatch.GetTimestamp();
            handled = true;
            BeginMeasurement();
        }
        return IntPtr.Zero;
    }

    private void BeginMeasurement()
    {
        if (_measuring) return;
        _measuring = true;
        _resampleForCurrentMeasurement = ResamplingCheck.IsChecked == true;
        ResamplingCheck.IsEnabled = false;
        SetupCombo.IsEnabled = false;
        StatusText.Text = "Midiendo…";

        try
        {
            _enumerator = new MMDeviceEnumerator();
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            _capture = new WasapiCapture(_device, useEventSync: false, audioBufferMillisecondsLength: AudioBufferMs);
            if (_resampleForCurrentMeasurement)
            {
                _capture.WaveFormat = new WaveFormat(ResampledRate, 16, 1);
            }
            _capture.DataAvailable += OnDataAvailable;
            _capture.StartRecording();
        }
        catch (Exception ex)
        {
            _measuring = false;
            ResamplingCheck.IsEnabled = true;
            SetupCombo.IsEnabled = true;
            StatusText.Text = $"Error iniciando WasapiCapture: {ex.Message}";
            _logger.LogError(ex, "WasapiCapture failed to start");
            DisposeCapture();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_measuring || !HasNonZeroSample(e.Buffer, e.BytesRecorded))
        {
            return;
        }

        var t1 = Stopwatch.GetTimestamp();
        var latencyMs = (t1 - _t0Ticks) * 1000.0 / Stopwatch.Frequency;
        var fmt = _capture?.WaveFormat;
        var sampleRate = fmt?.SampleRate ?? 0;
        var channels = fmt?.Channels ?? 0;
        _measuring = false;

        Dispatcher.BeginInvoke(() => OnMeasurementCompleted(latencyMs, sampleRate, channels));
    }

    private static bool HasNonZeroSample(byte[] buffer, int bytesRecorded)
    {
        // 16-bit PCM: cada par de bytes es un short little-endian. Funciona para mono y estéreo
        // (en estéreo recorre L,R,L,R… intercaladas — basta con que cualquiera sea no-cero).
        for (int i = 0; i + 1 < bytesRecorded; i += 2)
        {
            if (buffer[i] != 0 || buffer[i + 1] != 0)
            {
                return true;
            }
        }
        return false;
    }

    private void OnMeasurementCompleted(double latencyMs, int sampleRate, int channels)
    {
        DisposeCapture();
        _measurementCount++;
        var setupLabel = (SetupCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "unknown";
        var mode = _resampleForCurrentMeasurement
            ? "resampled-16khz-mono"
            : $"native-{sampleRate}-{channels}ch";

        ResamplingCheck.IsEnabled = true;
        SetupCombo.IsEnabled = true;

        try
        {
            AppendCsvRow(setupLabel, mode, latencyMs, sampleRate);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Latencia OK ({latencyMs:F1} ms) pero falló escritura del CSV: {ex.Message}";
            _logger.LogError(ex, "Failed to append CSV row");
            UpdateCounter(setupLabel, latencyMs, mode, suffix: " (CSV NO grabado)");
            return;
        }
        UpdateCounter(setupLabel, latencyMs, mode, suffix: null);
    }

    private void UpdateCounter(string setupLabel, double latencyMs, string mode, string? suffix)
    {
        CounterText.Text = $"{_measurementCount} / {TargetMeasurements} mediciones";
        LastLatencyText.Text = $"Última: {latencyMs:F1} ms · {setupLabel} · {mode}";
        StatusText.Text = _measurementCount >= TargetMeasurements
            ? $"Set completo. Cambiá setup/modo y reseteá si vas por otro escenario.{suffix}"
            : $"Listo.{suffix}";
    }

    private static void AppendCsvRow(string setupLabel, string mode, double latencyMs, int sampleRate)
    {
        var path = CsvPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var newFile = !File.Exists(path);
        using var writer = new StreamWriter(path, append: true);
        if (newFile)
        {
            writer.WriteLine(CsvHeaderV2);
        }
        var ts = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var row = string.Format(
            CultureInfo.InvariantCulture,
            "{0},{1},{2},{3:F2},{4},{5}",
            ts, setupLabel, mode, latencyMs, sampleRate, AudioBufferMs);
        writer.WriteLine(row);
    }

    private void EnsureCsvSchemaCompatible()
    {
        var path = CsvPath;
        if (!File.Exists(path)) return;

        string firstLine;
        try
        {
            using var reader = new StreamReader(path);
            firstLine = reader.ReadLine() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No pude leer header del CSV existente, lo dejo intacto");
            return;
        }

        if (firstLine.Trim() == CsvHeaderV2) return;

        var dir = Path.GetDirectoryName(path)!;
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var rotated = Path.Combine(dir, $"poc-latencia.v1-{stamp}.csv");
        try
        {
            File.Move(path, rotated);
            _logger.LogInformation(
                "CSV viejo (schema v1, sin columna 'mode') rotado a {Rotated}; arranco con header v2",
                rotated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No pude rotar CSV viejo. Borralo manualmente o renombralo");
        }
    }

    private void DisposeCapture()
    {
        if (_capture is not null)
        {
            try
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.StopRecording();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping WasapiCapture");
            }
            _capture.Dispose();
            _capture = null;
        }
        _enumerator?.Dispose();
        _enumerator = null;
        _device = null;
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _measurementCount = 0;
        CounterText.Text = $"0 / {TargetMeasurements} mediciones";
        LastLatencyText.Text = "Última: —";
        StatusText.Text = "Contador reseteado.";
    }
}

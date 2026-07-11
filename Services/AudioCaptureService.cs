using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace WindowsLiveCaptionsReader.Services
{
    /// <summary>
    /// Captures the microphone with NAudio and transcribes it in chunks with Whisper.
    /// System.Speech (SAPI) is NOT used: modern Windows 11 builds ship 0 installed
    /// recognizers (legacy Windows Speech Recognition was removed in favor of Voice
    /// Access), so dictation via System.Speech silently captures nothing.
    /// Mirrors the chunking strategy of <see cref="SystemAudioCaptureService"/>.
    /// </summary>
    public class AudioCaptureService : IDisposable
    {
        public event EventHandler<string>? TextCaptured;
        public event EventHandler<string>? StatusChanged;
        public event EventHandler<float>? AudioLevelChanged;

        private readonly WhisperService _whisper;
        private WaveInEvent? _waveIn;
        private MemoryStream _buffer = new();
        private readonly object _lock = new();
        private Timer? _transcribeTimer;
        private readonly string _tempFile;
        private int _selectedDeviceIndex = -1;
        private bool _isRunning;
        private volatile bool _isTranscribing;

        // 16kHz mono 16-bit — exactly what Whisper wants, no resampling needed
        private static readonly WaveFormat CaptureFormat = new(16000, 16, 1);
        private const int ChunkIntervalMs  = 2000;
        private const int MaxBufferSeconds = 4;
        // Below this peak level the chunk is treated as silence and skipped —
        // Whisper hallucinates phrases ("Thank you.") on silent audio.
        private const float SilencePeakThreshold = 0.01f;
        private float _chunkPeak;

        public class AudioDevice
        {
            public int Index { get; set; }
            public string Name { get; set; } = "";
        }

        public AudioCaptureService(WhisperService whisper)
        {
            _whisper  = whisper;
            _tempFile = Path.Combine(Path.GetTempPath(), "ela_mic.wav");
            _whisper.DownloadProgress += (_, msg) => StatusChanged?.Invoke(this, msg);
        }

        public void StopListening() => Stop();

        public List<AudioDevice> GetMicrophones()
        {
            var list = new List<AudioDevice>
            {
                // WAVE_MAPPER: Windows routes to whatever the current default input is,
                // including headsets/BT mics plugged in after the app started.
                new() { Index = -1, Name = "🎙 Predeterminado de Windows" },
            };
            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                var cap = WaveIn.GetCapabilities(i);
                list.Add(new AudioDevice { Index = i, Name = cap.ProductName });
            }
            return list;
        }

        public void SetDevice(int deviceIndex)
        {
            _selectedDeviceIndex = deviceIndex;
            if (_isRunning)
            {
                Stop();
                Start();
            }
        }

        public void Start() => _ = StartAsync();

        private async Task StartAsync()
        {
            if (_isRunning) return;

            try
            {
                if (!_whisper.IsModelLoaded)
                {
                    StatusChanged?.Invoke(this, "Cargando modelo Whisper...");
                    await _whisper.InitializeAsync();
                }

                // -1 = WAVE_MAPPER: Windows default input device. Never force index 0 —
                // it's just the first enumerated device, often not the active mic,
                // which silently records nothing but silence.
                _waveIn = new WaveInEvent
                {
                    DeviceNumber       = _selectedDeviceIndex,
                    WaveFormat         = CaptureFormat,
                    BufferMilliseconds = 100,
                };

                _waveIn.RecordingStopped += (_, e) =>
                {
                    if (e.Exception != null)
                        StatusChanged?.Invoke(this, $"Mic Error: {e.Exception.Message}");
                };

                _waveIn.DataAvailable += (_, e) =>
                {
                    lock (_lock) _buffer.Write(e.Buffer, 0, e.BytesRecorded);

                    // Peak level of this callback's samples — drives the mic level bar
                    // and the silence gate for the next chunk.
                    float peak = 0;
                    for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
                    {
                        float sample = Math.Abs(BitConverter.ToInt16(e.Buffer, i) / 32768f);
                        if (sample > peak) peak = sample;
                    }
                    if (peak > _chunkPeak) _chunkPeak = peak;
                    AudioLevelChanged?.Invoke(this, peak * 100);
                };

                _waveIn.StartRecording();
                _transcribeTimer = new Timer(TranscribeChunk, null, ChunkIntervalMs, ChunkIntervalMs);
                _isRunning = true;
                StatusChanged?.Invoke(this, "Escuchando (Whisper)...");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"Mic Error: {ex.Message}");
            }
        }

        private async void TranscribeChunk(object? _)
        {
            if (_isTranscribing)
            {
                // Whisper fell behind — keep only the most recent audio
                lock (_lock)
                {
                    long maxBytes = (long)CaptureFormat.AverageBytesPerSecond * MaxBufferSeconds;
                    if (_buffer.Length > maxBytes)
                    {
                        byte[] all = _buffer.ToArray();
                        _buffer = new MemoryStream();
                        _buffer.Write(all, (int)(all.Length - maxBytes), (int)maxBytes);
                    }
                }
                return;
            }
            _isTranscribing = true;

            try
            {
                byte[] raw;
                float peak;

                lock (_lock)
                {
                    // Require at least 1 second of audio
                    if (_buffer.Length < CaptureFormat.AverageBytesPerSecond)
                    {
                        _isTranscribing = false;
                        return;
                    }
                    raw = _buffer.ToArray();
                    _buffer = new MemoryStream();
                    peak = _chunkPeak;
                    _chunkPeak = 0;
                }

                if (peak < SilencePeakThreshold) return; // silence — don't feed Whisper

                using (var writer = new WaveFileWriter(_tempFile, CaptureFormat))
                    writer.Write(raw, 0, raw.Length);

                string text = (await _whisper.TranscribeAsync(_tempFile)).Trim();

                if (text.Length > 0 && !IsWhisperNoise(text))
                    TextCaptured?.Invoke(this, text);
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"Error Whisper: {ex.Message[..Math.Min(60, ex.Message.Length)]}");
            }
            finally
            {
                _isTranscribing = false;
            }
        }

        // Whisper emits bracketed tags and stock hallucinations on near-silent audio
        private static bool IsWhisperNoise(string text)
        {
            string t = text.Trim();
            return (t.StartsWith('[') && t.EndsWith(']'))
                || (t.StartsWith('(') && t.EndsWith(')'))
                || (t.StartsWith('*') && t.EndsWith('*'));
        }

        public void Stop()
        {
            _isRunning = false;
            _transcribeTimer?.Dispose();
            _transcribeTimer = null;
            try { _waveIn?.StopRecording(); } catch { }
            _waveIn?.Dispose();
            _waveIn = null;
            lock (_lock) _buffer = new MemoryStream();
            StatusChanged?.Invoke(this, "Mic Stopped");
            AudioLevelChanged?.Invoke(this, 0);
        }

        public void Dispose()
        {
            Stop();
            _buffer.Dispose();
        }
    }
}

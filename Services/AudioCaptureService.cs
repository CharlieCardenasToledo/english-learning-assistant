using System;
using System.Collections.Generic;
using System.Speech.Recognition;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using System.Linq;

namespace WindowsLiveCaptionsReader.Services
{
    public class AudioCaptureService : IDisposable
    {
        public event EventHandler<string>? TextCaptured;
        public event EventHandler<string>? StatusChanged;
        public event EventHandler<float>? AudioLevelChanged; // New for validation

        private SpeechRecognitionEngine? _recognizer;
        private int _selectedDeviceIndex = -1;
        private bool _isListening;

        public class AudioDevice
        {
            public int Index { get; set; }
            public string Name { get; set; } = "";
        }

        public void StopListening() => Stop(); // Alias to fix build error
        
        public List<AudioDevice> GetMicrophones()
        {
            var list = new List<AudioDevice>();
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
            if (_isListening)
            {
                Stop();
                Start();
            }
        }

        public void Start()
        {
            if (_isListening) return;

            try
            {
                // Init Engine
                if (_recognizer == null)
                {
                    // Find logical recognizer
                    var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
                    RecognizerInfo? selectedInfo = null;
                    
                    // Try en-US
                    selectedInfo = recognizers.FirstOrDefault(r => r.Culture.Name.Equals("en-US", StringComparison.OrdinalIgnoreCase));
                    
                    // Fallback to any English
                    if (selectedInfo == null)
                         selectedInfo = recognizers.FirstOrDefault(r => r.Culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase));
                         
                    // Fallback to Default
                    if (selectedInfo == null)
                         selectedInfo = recognizers.FirstOrDefault();

                    if (selectedInfo == null)
                    {
                        StatusChanged?.Invoke(this, "Error: No Speech Recognizer found.");
                        return;
                    }

                    _recognizer = new SpeechRecognitionEngine(selectedInfo.Id);
                    StatusChanged?.Invoke(this, $"Engine: {selectedInfo.Culture.Name}");
                    
                    _recognizer.LoadGrammar(new DictationGrammar());
                    
                    // Hook events for debugging
                    _recognizer.SpeechRecognized += (s, e) => 
                    {
                        // STRICT MODE: Only accept high confidence execution to avoid noise
                        if (e.Result.Confidence > 0.8) 
                        {
                            TextCaptured?.Invoke(this, e.Result.Text);
                        }
                    };
                    
                    _recognizer.SpeechHypothesized += (s, e) =>
                    {
                         // Optional: Only log if needed, spammy
                    };
                    
                    _recognizer.SpeechRecognitionRejected += (s, e) =>
                    {
                         StatusChanged?.Invoke(this, "Speech Rejected (Noise?)");
                    };
                }

                if (_selectedDeviceIndex >= 0)
                {
                    // For now, let's try to trust the engine's default device handling to rule out stream issues.
                    // If this works, we know the issue was the custom SpeechStreamer pipe.
                    _recognizer.SetInputToDefaultAudioDevice();
                    StatusChanged?.Invoke(this, "Mic Active (Default System Device)");
                }
                else
                {
                    _recognizer.SetInputToDefaultAudioDevice();
                    StatusChanged?.Invoke(this, "Mic Active (Default)");
                }

                _recognizer.RecognizeAsync(RecognizeMode.Multiple);
                _isListening = true;

                /* NAUDIO DISABLED TEMPORARILY TO AVOID CONFLICTS
                _waveIn = new WaveInEvent();
                // ... logic removed ...
                _waveIn.StartRecording();
                */
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"Mic Error: {ex.Message}");
            }
        }

        public void Stop()
        {
            try
            {
                if (_recognizer != null) _recognizer.RecognizeAsyncStop();
                // if (_waveIn != null) _waveIn.StopRecording();
                _isListening = false;
                StatusChanged?.Invoke(this, "Mic Stopped");
                AudioLevelChanged?.Invoke(this, 0);
            }
            catch { }
        }

        public void Dispose()
        {
            Stop();
            _recognizer?.Dispose();
        }
    }
    
    // Custom Stream Pipe
    public class SpeechStreamer : System.IO.Stream
    {
        private System.Collections.Concurrent.ConcurrentQueue<byte> _buffer;
        private AutoResetEvent _dataAvailable;
        private long _written;

        public SpeechStreamer(int bufferSize)
        {
            _buffer = new System.Collections.Concurrent.ConcurrentQueue<byte>();
            _dataAvailable = new AutoResetEvent(false);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++) _buffer.Enqueue(buffer[offset + i]);
            _dataAvailable.Set();
            _written += count;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = 0;
            while (read < count)
            {
                if (_buffer.TryDequeue(out byte b))
                {
                    buffer[offset + read] = b;
                    read++;
                }
                else
                {
                    if (read > 0) return read;
                    _dataAvailable.WaitOne(100); // Wait for audio
                }
            }
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotImplementedException(); }
        public override void Flush() { }
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotImplementedException();
        public override void SetLength(long value) => throw new NotImplementedException();
    }
}

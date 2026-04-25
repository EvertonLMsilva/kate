using System.Diagnostics;
using System.Media;
using System.Threading;
using System.Threading.Tasks;

namespace model_kate.Voice
{
    public class PiperTextToSpeechService : ITextToSpeechService
    {
        private const string DefaultNoiseScale = "0.82";
        private const string DefaultLengthScale = "0.93";
        private const string DefaultNoiseW = "0.9";

        private readonly string _piperExecutablePath;
        private readonly string _modelPath;
        private readonly string? _configPath;
        private readonly SemaphoreSlim _speakGate = new(1, 1);
        private Process? _currentProcess;
        private SoundPlayer? _currentPlayer;

        public PiperTextToSpeechService(string piperExecutablePath, string modelPath, string? configPath = null)
        {
            _piperExecutablePath = piperExecutablePath;
            _modelPath = modelPath;
            _configPath = configPath;
        }

        public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            await _speakGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var tempWaveFilePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"kate-piper-{Guid.NewGuid():N}.wav");
            CancellationTokenRegistration registration = default;
            try
            {
                registration = cancellationToken.Register(() =>
                {
                    try { _currentProcess?.Kill(true); } catch { }
                    try { _currentPlayer?.Stop(); } catch { }
                });

                var arguments = BuildArguments(tempWaveFilePath);
                var startInfo = new ProcessStartInfo
                {
                    FileName = _piperExecutablePath,
                    Arguments = arguments,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(_piperExecutablePath) ?? AppContext.BaseDirectory
                };

                using var process = Process.Start(startInfo);
                _currentProcess = process;
                if (process is null)
                {
                    return;
                }

                await process.StandardInput.WriteAsync(text).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
                process.StandardInput.Close();

                var errorOutputTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                var errorOutput = await errorOutputTask.ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Piper retornou código {process.ExitCode}. {errorOutput}".Trim());
                }

                if (!System.IO.File.Exists(tempWaveFilePath))
                {
                    throw new InvalidOperationException("Piper não gerou o arquivo de áudio esperado.");
                }

                using var player = new SoundPlayer(tempWaveFilePath);
                player.Load();
                _currentPlayer = player;
                await Task.Run(player.PlaySync, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                registration.Dispose();
                _currentProcess = null;
                _currentPlayer = null;
                try { System.IO.File.Delete(tempWaveFilePath); } catch { }
                _speakGate.Release();
            }
        }

        private string BuildArguments(string tempWaveFilePath)
        {
            var parts = new List<string>
            {
                "--model",
                Quote(_modelPath),
                "--output_file",
                Quote(tempWaveFilePath),
                "--noise_scale",
                DefaultNoiseScale,
                "--length_scale",
                DefaultLengthScale,
                "--noise_w",
                DefaultNoiseW,
                "--sentence_silence",
                "0.05"
            };

            if (!string.IsNullOrWhiteSpace(_configPath))
            {
                parts.Add("--config");
                parts.Add(Quote(_configPath));
            }

            return string.Join(" ", parts);
        }

        private static string Quote(string value)
        {
            return $"\"{value.Replace("\"", "") }\"";
        }
    }
}
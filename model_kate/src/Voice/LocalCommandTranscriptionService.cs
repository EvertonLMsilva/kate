using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using NAudio.Wave;

namespace model_kate.Voice
{
    internal sealed class LocalCommandTranscriptionService : IDisposable
    {
        private readonly string _executablePath;
        private readonly string _modelPath;
        private readonly SemaphoreSlim _transcriptionGate = new(1, 1);
        private Process? _currentProcess;

        private LocalCommandTranscriptionService(string executablePath, string modelPath)
        {
            _executablePath = executablePath;
            _modelPath = modelPath;
        }

        public static LocalCommandTranscriptionService? Create(string baseDirectory)
        {
            var executablePath = ResolveExecutablePath(baseDirectory);
            var modelPath = ResolveModelPath(baseDirectory);
            if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(modelPath))
            {
                return null;
            }

            return new LocalCommandTranscriptionService(executablePath, modelPath);
        }

        public string Description => $"Whisper local - {Path.GetFileName(_modelPath)}";

        public async Task<string?> TranscribeAsync(byte[] pcmAudio, CancellationToken cancellationToken = default)
        {
            if (pcmAudio.Length == 0)
            {
                return null;
            }

            await _transcriptionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var tempWaveFilePath = Path.Combine(Path.GetTempPath(), $"kate-whisper-{Guid.NewGuid():N}.wav");
            var outputBasePath = Path.Combine(Path.GetTempPath(), $"kate-whisper-{Guid.NewGuid():N}");
            CancellationTokenRegistration registration = default;
            try
            {
                WriteWaveFile(tempWaveFilePath, pcmAudio);
                registration = cancellationToken.Register(() =>
                {
                    try { _currentProcess?.Kill(true); } catch { }
                });

                foreach (var arguments in BuildArgumentCandidates(tempWaveFilePath, outputBasePath))
                {
                    var transcription = await TryRunWhisperAsync(arguments, tempWaveFilePath, outputBasePath, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(transcription))
                    {
                        return transcription;
                    }
                }

                return null;
            }
            finally
            {
                registration.Dispose();
                _currentProcess = null;
                try { File.Delete(tempWaveFilePath); } catch { }
                TryDeleteOutputArtifacts(tempWaveFilePath, outputBasePath);
                _transcriptionGate.Release();
            }
        }

        public void Dispose()
        {
            try { _currentProcess?.Kill(true); } catch { }
            _currentProcess = null;
            _transcriptionGate.Dispose();
        }

        private async Task<string?> TryRunWhisperAsync(string arguments, string tempWaveFilePath, string outputBasePath, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _executablePath,
                Arguments = arguments,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_executablePath) ?? AppContext.BaseDirectory,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = Process.Start(startInfo);
            _currentProcess = process;
            if (process is null)
            {
                return null;
            }

            var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorOutputTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var standardOutput = await standardOutputTask.ConfigureAwait(false);
            var errorOutput = await errorOutputTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                VoskWakeWordRecognitionService.AppendVoiceLog($"[Voice] Whisper local retornou codigo {process.ExitCode}. {errorOutput}".Trim());
                return null;
            }

            var textFromFile = TryReadTranscriptionFile(tempWaveFilePath, outputBasePath);
            if (!string.IsNullOrWhiteSpace(textFromFile))
            {
                return NormalizeWhisperText(textFromFile);
            }

            if (!string.IsNullOrWhiteSpace(standardOutput))
            {
                return NormalizeWhisperText(standardOutput);
            }

            if (!string.IsNullOrWhiteSpace(errorOutput))
            {
                return NormalizeWhisperText(errorOutput);
            }

            return null;
        }

        private IEnumerable<string> BuildArgumentCandidates(string waveFilePath, string outputBasePath)
        {
            yield return string.Join(" ", new[]
            {
                "-m", Quote(_modelPath),
                "-f", Quote(waveFilePath),
                "-l", "auto",
                "-nt",
                "-np",
                "-of", Quote(outputBasePath),
                "-otxt"
            });

            yield return string.Join(" ", new[]
            {
                "--model", Quote(_modelPath),
                "--file", Quote(waveFilePath),
                "--language", "auto",
                "--no-timestamps",
                "--output-txt",
                "--output-file", Quote(outputBasePath)
            });
        }

        private static string Quote(string value)
        {
            return $"\"{value.Replace("\"", string.Empty)}\"";
        }

        private static void WriteWaveFile(string filePath, byte[] pcmAudio)
        {
            using var writer = new WaveFileWriter(filePath, new WaveFormat(16000, 16, 1));
            writer.Write(pcmAudio, 0, pcmAudio.Length);
        }

        private static void TryDeleteOutputArtifacts(string tempWaveFilePath, string outputBasePath)
        {
            var candidates = new[]
            {
                outputBasePath + ".txt",
                tempWaveFilePath + ".txt",
                outputBasePath + ".json",
                tempWaveFilePath + ".json",
                outputBasePath + ".srt",
                tempWaveFilePath + ".srt",
                outputBasePath + ".vtt",
                tempWaveFilePath + ".vtt"
            };

            foreach (var candidate in candidates)
            {
                try { File.Delete(candidate); } catch { }
            }
        }

        private static string? TryReadTranscriptionFile(string tempWaveFilePath, string outputBasePath)
        {
            var candidates = new[]
            {
                outputBasePath + ".txt",
                tempWaveFilePath + ".txt"
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    if (!File.Exists(candidate))
                    {
                        continue;
                    }

                    var text = File.ReadAllText(candidate, Encoding.UTF8);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static string NormalizeWhisperText(string text)
        {
            var cleaned = text.Replace("\r", " ").Replace("\n", " ");
            cleaned = Regex.Replace(cleaned, @"\[[^\]]+\]", " ");
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
            return cleaned;
        }

        private static string? ResolveExecutablePath(string baseDirectory)
        {
            var configuredPath = Environment.GetEnvironmentVariable("KATE_WHISPER_EXE");
            if (IsExistingFile(configuredPath))
            {
                return configuredPath;
            }

            var candidates = new[]
            {
                Path.Combine(baseDirectory, "ia_local", "whisper", "whisper-cli.exe"),
                Path.Combine(baseDirectory, "ia_local", "whisper", "main.exe"),
                Path.Combine(baseDirectory, "ia_local", "whisper", "whisper.exe"),
                Path.Combine(baseDirectory, "whisper", "whisper-cli.exe"),
                Path.Combine(baseDirectory, "whisper", "main.exe"),
                Path.Combine(baseDirectory, "whisper", "whisper.exe")
            };

            return candidates.FirstOrDefault(IsExistingFile);
        }

        private static string? ResolveModelPath(string baseDirectory)
        {
            var configuredPath = Environment.GetEnvironmentVariable("KATE_WHISPER_MODEL");
            if (IsExistingFile(configuredPath))
            {
                return configuredPath;
            }

            var candidateDirectories = new[]
            {
                Path.Combine(baseDirectory, "ia_local", "whisper", "models"),
                Path.Combine(baseDirectory, "ia_local", "whisper"),
                Path.Combine(baseDirectory, "whisper", "models"),
                Path.Combine(baseDirectory, "whisper")
            };

            var allModels = candidateDirectories
                .Where(Directory.Exists)
                .SelectMany(directory => Directory.EnumerateFiles(directory, "*.bin", SearchOption.TopDirectoryOnly))
                .ToArray();

            return allModels
                .OrderByDescending(ScoreModelPath)
                .FirstOrDefault();
        }

        private static int ScoreModelPath(string modelPath)
        {
            var fileName = Path.GetFileName(modelPath).ToLowerInvariant();
            var score = 0;
            if (fileName.Contains("small"))
            {
                score += 30;
            }
            else if (fileName.Contains("base"))
            {
                score += 20;
            }
            else if (fileName.Contains("medium"))
            {
                score += 10;
            }

            if (fileName.Contains("pt") || fileName.Contains("portuguese"))
            {
                score += 15;
            }

            if (fileName.Contains("q5") || fileName.Contains("q8"))
            {
                score += 5;
            }

            return score;
        }

        private static bool IsExistingFile(string? path)
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
    }
}
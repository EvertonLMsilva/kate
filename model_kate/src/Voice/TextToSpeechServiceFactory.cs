using System.Linq;
using System.Threading.Tasks;

namespace model_kate.Voice
{
    public sealed class TextToSpeechServiceSelection
    {
        public TextToSpeechServiceSelection(ITextToSpeechService service, string description)
        {
            Service = service;
            Description = description;
        }

        public ITextToSpeechService Service { get; }
        public string Description { get; }
    }

    public static class TextToSpeechServiceFactory
    {
        private static readonly TimeSpan LocalTtsInitializationTimeout = TimeSpan.FromSeconds(2);

        public static TextToSpeechServiceSelection Create(string appBaseDirectory)
        {
            var piperSelection = TryCreatePiper(appBaseDirectory);
            if (piperSelection is not null)
            {
                return piperSelection;
            }

            var localTtsService = TryCreateLocalTts();
            if (localTtsService is not null && localTtsService.HasPortugueseVoice)
            {
                var description = $"System.Speech - {localTtsService.SelectedVoiceDescription}";
                return new TextToSpeechServiceSelection(localTtsService, description);
            }

            var bundledPiperHint = DescribeBundledPiperSetup(appBaseDirectory);
            var fallbackDescription = localTtsService is null
                ? $"Narracao desativada: System.Speech nao respondeu a tempo e nenhum narrador pt-BR local foi encontrado. {bundledPiperHint}"
                : $"Narracao desativada: nenhuma voz pt-BR encontrada. Voz atual do Windows: {localTtsService.SelectedVoiceDescription}. {bundledPiperHint}";

            return new TextToSpeechServiceSelection(new NullTextToSpeechService(), fallbackDescription);
        }

        private static LocalTtsService? TryCreateLocalTts()
        {
            try
            {
                var task = Task.Run(() => new LocalTtsService());
                return task.Wait(LocalTtsInitializationTimeout)
                    ? task.Result
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static TextToSpeechServiceSelection? TryCreatePiper(string appBaseDirectory)
        {
            var executablePath = ResolvePiperExecutablePath(appBaseDirectory);
            var modelPath = ResolvePiperModelPath(appBaseDirectory);
            if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(modelPath))
            {
                return null;
            }

            var configPath = System.IO.File.Exists(modelPath + ".json")
                ? modelPath + ".json"
                : null;

            var service = new PiperTextToSpeechService(executablePath, modelPath, configPath);
            var description = $"Piper - {System.IO.Path.GetFileName(modelPath)}";
            return new TextToSpeechServiceSelection(service, description);
        }

        private static string? ResolvePiperExecutablePath(string appBaseDirectory)
        {
            foreach (var root in GetSearchRoots(appBaseDirectory))
            {
                var candidates = new[]
                {
                    System.IO.Path.Combine(root, "voz", "piper", "piper.exe"),
                    System.IO.Path.Combine(root, "piper", "piper.exe"),
                    System.IO.Path.Combine(root, "piper.exe")
                };

                var found = candidates.FirstOrDefault(System.IO.File.Exists);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }

            var envPath = Environment.GetEnvironmentVariable("KATE_PIPER_EXE");
            if (!string.IsNullOrWhiteSpace(envPath) && System.IO.File.Exists(envPath))
            {
                return envPath;
            }

            return null;
        }

        private static string? ResolvePiperModelPath(string appBaseDirectory)
        {
            foreach (var root in GetSearchRoots(appBaseDirectory))
            {
                var voiceDirectoryCandidates = GetPiperModelDirectories(appBaseDirectory, root);

                foreach (var voiceDirectory in voiceDirectoryCandidates.Where(System.IO.Directory.Exists))
                {
                    var models = System.IO.Directory.GetFiles(voiceDirectory, "*.onnx", SearchOption.AllDirectories)
                        .OrderBy(path => ScoreModelPath(path))
                        .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    var chosen = models.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(chosen))
                    {
                        return chosen;
                    }
                }
            }

            var envPath = Environment.GetEnvironmentVariable("KATE_PIPER_MODEL");
            if (!string.IsNullOrWhiteSpace(envPath) && System.IO.File.Exists(envPath))
            {
                return envPath;
            }

            return null;
        }

        private static int ScoreModelPath(string path)
        {
            var normalized = path.ToLowerInvariant();
            var score = 100;

            if (normalized.Contains("pt_br") || normalized.Contains("pt-br") || normalized.Contains("portuguese") || normalized.Contains("brazil"))
            {
                score -= 50;
            }

            if (normalized.Contains("female")
                || normalized.Contains("feminina")
                || normalized.Contains("woman")
                || normalized.Contains("mulher")
                || normalized.Contains("girl")
                || normalized.Contains("menina")
                || normalized.Contains("young")
                || normalized.Contains("jovem"))
            {
                score -= 30;
            }

            if (normalized.Contains("medium"))
            {
                score -= 5;
            }

            return score;
        }

        private static string[] GetSearchRoots(string appBaseDirectory)
        {
            var roots = new List<string>();
            var current = new System.IO.DirectoryInfo(appBaseDirectory);
            while (current is not null)
            {
                roots.Add(current.FullName);
                current = current.Parent;
            }

            return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static string[] GetPiperModelDirectories(string appBaseDirectory, string root)
        {
            var candidates = new List<string>
            {
                System.IO.Path.Combine(root, "voz", "piper"),
                System.IO.Path.Combine(root, "piper")
            };

            if (string.Equals(root, appBaseDirectory, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(root);
            }

            return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static string DescribeBundledPiperSetup(string appBaseDirectory)
        {
            var projectPiperDirectory = GetBundledPiperDirectory(appBaseDirectory);
            return $"Para narracao propria do projeto, coloque piper.exe, DLLs do Piper, um modelo pt-BR .onnx e o .onnx.json em '{projectPiperDirectory}'.";
        }

        private static string GetBundledPiperDirectory(string appBaseDirectory)
        {
            foreach (var root in GetSearchRoots(appBaseDirectory))
            {
                var candidate = System.IO.Path.Combine(root, "voz", "piper");
                if (System.IO.Directory.Exists(System.IO.Path.Combine(root, "voz")))
                {
                    return candidate;
                }
            }

            return System.IO.Path.Combine(appBaseDirectory, "voz", "piper");
        }
    }
}
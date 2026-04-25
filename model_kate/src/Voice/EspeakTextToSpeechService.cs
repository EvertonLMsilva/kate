using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace model_kate.Voice
{
    public class EspeakTextToSpeechService : ITextToSpeechService
    {
        private readonly string _voice;
        private readonly string _espeakPath;

        public EspeakTextToSpeechService(string espeakPath = "espeak", string voice = "pt+f3")
        {
            _espeakPath = espeakPath;
            _voice = voice;
        }

        public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = _espeakPath,
                Arguments = $"-v {_voice} \"{text.Replace("\"", "'")}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
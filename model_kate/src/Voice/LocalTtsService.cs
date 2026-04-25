using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace model_kate.Voice
{
    public class LocalTtsService : ITextToSpeechService
    {
        private readonly SpeechSynthesizer _synth;
        private readonly SemaphoreSlim _speakGate = new(1, 1);

        public string SelectedVoiceDescription { get; }
        public bool HasPortugueseVoice { get; }

        public LocalTtsService()
        {
            _synth = new SpeechSynthesizer();
            _synth.SetOutputToDefaultAudioDevice();
            SelectedVoiceDescription = SelectPreferredVoice();
            HasPortugueseVoice = SelectedVoiceDescription.Contains("pt", StringComparison.OrdinalIgnoreCase)
                || SelectedVoiceDescription.Contains("portugu", StringComparison.OrdinalIgnoreCase)
                || SelectedVoiceDescription.Contains("brazil", StringComparison.OrdinalIgnoreCase);
        }

        public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            await _speakGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<SpeakCompletedEventArgs>? handler = null;
                CancellationTokenRegistration registration = default;

                handler = (_, _) =>
                {
                    _synth.SpeakCompleted -= handler;
                    registration.Dispose();
                    completion.TrySetResult();
                };

                if (cancellationToken.CanBeCanceled)
                {
                    registration = cancellationToken.Register(() =>
                    {
                        _synth.SpeakAsyncCancelAll();
                        completion.TrySetCanceled(cancellationToken);
                    });
                }

                _synth.SpeakCompleted += handler;
                _synth.SpeakAsync(text);
                await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                _speakGate.Release();
            }
        }

        private string SelectPreferredVoice()
        {
            var voices = _synth.GetInstalledVoices()
                .Where(v => v.Enabled)
                .Select(v => v.VoiceInfo)
                .ToList();

            var preferredVoice = voices.FirstOrDefault(v => string.Equals(v.Culture?.Name, "pt-BR", StringComparison.OrdinalIgnoreCase))
                ?? voices.FirstOrDefault(v => string.Equals(v.Culture?.TwoLetterISOLanguageName, "pt", StringComparison.OrdinalIgnoreCase))
                ?? _synth.Voice;

            if (preferredVoice is not null)
            {
                _synth.SelectVoice(preferredVoice.Name);
                return $"{preferredVoice.Name} ({preferredVoice.Culture?.Name ?? "desconhecida"})";
            }

            return "nenhuma voz instalada";
        }
    }
}

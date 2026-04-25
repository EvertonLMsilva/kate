using System.Threading;
using System.Threading.Tasks;

namespace model_kate.Voice
{
    public sealed class NullTextToSpeechService : ITextToSpeechService
    {
        public Task SpeakAsync(string text, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
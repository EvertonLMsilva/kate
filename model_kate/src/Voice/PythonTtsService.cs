using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace model_kate.Voice
{
    public class PythonTtsService : ITextToSpeechService
    {
        private readonly string _endpoint;
        private static readonly HttpClient _httpClient = new HttpClient();

        public PythonTtsService(string endpoint = "http://127.0.0.1:5005/speak")
        {
            _endpoint = endpoint;
        }

        public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var payload = new { text = text };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            try
            {
                await _httpClient.PostAsync(_endpoint, content, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }
}

using model_kate.Domain;
using System;
using System.Threading.Tasks;

namespace model_kate.Application
{
    public class GenerateResponseUseCase
    {
        private readonly IGenerativeAIService _aiService;

        public GenerateResponseUseCase(IGenerativeAIService aiService)
        {
            _aiService = aiService;
        }

        public IGenerativeAIService AiService => _aiService;

        public string Execute(string prompt)
        {
            return _aiService.GenerateResponse(prompt);
        }

        public Task<string> ExecuteAsync(string prompt, Action<string>? onToken = null, CancellationToken cancellationToken = default)
        {
            return _aiService.GenerateResponseAsync(prompt, onToken, cancellationToken);
        }
    }
}

using model_kate.Domain;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace model_kate.Infrastructure
{
    public class MockGenerativeAIService : IGenerativeAIService
    {
        private string _model = "mock";
        public string CurrentModel => _model;

        public string GenerateResponse(string prompt)
        {
            return $"[Mock] Resposta para: {prompt}";
        }

        public Task<string> GenerateResponseAsync(string prompt, Action<string>? onToken = null, CancellationToken cancellationToken = default)
        {
            var response = GenerateResponse(prompt);
            onToken?.Invoke(response);
            return Task.FromResult(response);
        }

        public Task<IReadOnlyList<string>> ListModelsAsync()
            => Task.FromResult<IReadOnlyList<string>>(new List<string> { "mock" });

        public Task<string> SwitchModelAsync(string modelName, Action<string>? onProgress = null, CancellationToken cancellationToken = default)
        {
            _model = modelName;
            return Task.FromResult($"[Mock] Modelo trocado para {modelName}.");
        }
    }
}

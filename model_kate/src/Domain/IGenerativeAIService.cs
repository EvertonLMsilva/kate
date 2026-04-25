using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace model_kate.Domain
{
    public interface IGenerativeAIService
    {
        string GenerateResponse(string prompt);

        /// <summary>
        /// Gera resposta com streaming. onToken e chamado a cada fragmento recebido.
        /// Retorna a resposta completa ao final. Cancela se cancellationToken for acionado.
        /// </summary>
        Task<string> GenerateResponseAsync(string prompt, Action<string>? onToken = null, CancellationToken cancellationToken = default);

        /// <summary>Nome do modelo LLM atualmente em uso.</summary>
        string CurrentModel { get; }

        /// <summary>Lista os modelos disponíveis localmente no Ollama.</summary>
        Task<IReadOnlyList<string>> ListModelsAsync();

        /// <summary>
        /// Troca o modelo LLM em uso. Baixa automaticamente se não estiver disponível.
        /// Retorna mensagem de confirmação ou erro para falar ao usuário.
        /// </summary>
        Task<string> SwitchModelAsync(string modelName, Action<string>? onProgress = null, CancellationToken cancellationToken = default);
    }
}

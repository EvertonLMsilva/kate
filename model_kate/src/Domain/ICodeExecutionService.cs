namespace model_kate.Domain
{
    public sealed record CodeExecutionResult(
        bool Success,
        string Output,
        string? Error,
        string Language,
        TimeSpan Duration);

    public interface ICodeExecutionService
    {
        /// <summary>Executa um bloco de código e retorna o resultado.</summary>
        Task<CodeExecutionResult> ExecuteAsync(string code, string language);

        /// <summary>Tenta detectar a linguagem de um bloco de código.</summary>
        string DetectLanguage(string codeOrResponse);

        /// <summary>Extrai o primeiro bloco de código de uma resposta do LLM (remove markdown fences).</summary>
        string? ExtractCodeBlock(string response, out string language);
    }
}

using model_kate.Domain;
using model_kate.Application;
using model_kate.Infrastructure;

Console.WriteLine("=== IA MVP - model_kate ===");
Console.WriteLine("Digite 'sair' para encerrar.\n");

IGenerativeAIService aiService = new OllamaGenerativeAIService("http://localhost:11434", "llama2");
var useCase = new GenerateResponseUseCase(aiService);

while (true)
{
	Console.Write("Prompt: ");
	var prompt = Console.ReadLine();
	if (string.IsNullOrWhiteSpace(prompt) || prompt.Trim().ToLower() == "sair")
		break;
	var resposta = useCase.Execute(prompt);
	Console.WriteLine($"IA: {resposta}\n");
}

Console.WriteLine("Encerrado.");

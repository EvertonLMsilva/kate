using Xunit;
using model_kate.Application;
using model_kate.Domain;
using model_kate.Infrastructure;

namespace model_kate.Tests
{
    public class GenerateResponseUseCaseTests
    {
        [Fact]
        public void Execute_ReturnsMockResponse()
        {
            // Arrange
            IGenerativeAIService aiService = new MockGenerativeAIService();
            var useCase = new GenerateResponseUseCase(aiService);
            var prompt = "Teste";

            // Act
            var resposta = useCase.Execute(prompt);

            // Assert
            Assert.Contains("[Mock] Resposta para: Teste", resposta);
        }
    }
}

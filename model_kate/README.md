# model_kate - MVP IA Local

## Descrição
MVP de arquitetura Clean Architecture para IA local, com fluxo completo e mock de IA.

## Estrutura
- **Domain**: Interface e contratos da IA
- **Application**: Casos de uso (ex: GenerateResponseUseCase)
- **Infrastructure**: Implementação mock da IA
- **Presentation**: Console interativo para testar o fluxo

## Como rodar
1. Instale o .NET 8 ou superior
2. No terminal, navegue até a pasta `model_kate`
3. Execute:
   ```
   dotnet run --project src/Presentation/model_kate.Presentation.csproj
   ```
4. Digite um prompt e veja a resposta mockada
5. Digite `sair` para encerrar

## Trocar o mock por IA real
Implemente `IGenerativeAIService` em `Infrastructure` e troque a instância no `Program.cs`.

## Exemplo de uso
```
=== IA MVP - model_kate ===
Digite 'sair' para encerrar.

Prompt: Olá!
IA: [Mock] Resposta para: Olá!

Prompt: sair
Encerrado.
```

---

Para dúvidas ou evolução, consulte os READMEs de cada camada.

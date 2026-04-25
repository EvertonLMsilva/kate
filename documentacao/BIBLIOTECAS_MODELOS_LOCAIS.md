# Sugestão de Bibliotecas/Modelos Locais - MVP Kate

## 1. Reconhecimento de Voz Local
### Opção 1: Vosk
- Site: https://alphacephei.com/vosk/
- Prós: Leve, fácil de integrar, suporta português, roda offline
- Integração: Biblioteca C# via Vosk API (NuGet: Vosk)

### Opção 2: Whisper (OpenAI)
- Site: https://github.com/openai/whisper
- Prós: Alta precisão, suporta vários idiomas
- Integração: Executável Python (chamado via processo externo) ou wrappers .NET
- Observação: Mais pesado que Vosk

### Opção 3: Microsoft Speech SDK (Offline)
- Site: https://learn.microsoft.com/azure/ai-services/speech-service/offline
- Prós: Integração nativa Windows, suporte offline limitado
- Integração: Biblioteca oficial Microsoft.CognitiveServices.Speech

## 2. IA Generativa Local
### Opção 1: Ollama
- Site: https://ollama.com/
- Prós: Instalação simples, roda modelos Llama, Mistral, etc., API local HTTP
- Integração: Requisições HTTP do C# para http://localhost:11434

### Opção 2: GPT4All
- Site: https://gpt4all.io/
- Prós: Interface desktop, modelos variados, API local
- Integração: Requisições HTTP ou chamada de executável

### Opção 3: LM Studio
- Site: https://lmstudio.ai/
- Prós: Interface amigável, suporta vários modelos, API local
- Integração: HTTP API

## 3. Criação de Arquivos
- Implementação: System.IO.File (C# nativo)

## 4. Roteiro de Instalação/Teste
1. Instalar Vosk ou Whisper (conforme escolha)
2. Instalar Ollama, GPT4All ou LM Studio para IA local
3. Baixar modelos de idioma/voz e LLM desejados
4. Testar reconhecimento de voz localmente
5. Testar geração de texto localmente
6. Integrar ambos no protótipo C#
7. Testar criação de arquivos

---

Essas opções são recomendadas para garantir funcionamento local, privacidade e sem custos recorrentes.

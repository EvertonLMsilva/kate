# model_kate — Kate IA Local

## Descrição
Assistente de IA pessoal local, offline, por voz. Roda completamente no seu computador sem depender de nenhum serviço externo de IA.

## Pré-requisitos
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Ollama](https://ollama.com/download) instalado e rodando
- Windows 10/11 x64

---

## Setup — Modelos de IA (não estão no git)

Os modelos são arquivos binários grandes e ficam fora do repositório. Baixe cada um conforme abaixo antes de rodar o projeto.

### 1. Modelo LLM (Ollama)

Instale o Ollama e baixe o modelo padrão:

```powershell
ollama pull llama3.2:3b
```

Outros modelos suportados (opcionais):
```powershell
ollama pull llama3.1:8b    # mais capaz, requer ~8GB de RAM
ollama pull mistral
ollama pull deepseek-r1
ollama pull gemma3
ollama pull phi4
```

O modelo ativo é controlado pela variável de ambiente `KATE_OLLAMA_MODEL` (padrão: `llama3.2:3b`):
```powershell
$env:KATE_OLLAMA_MODEL = "llama3.1:8b"
```

Você também pode trocar o modelo em tempo real dizendo: **"Kate, muda para o mistral"**.

---

### 2. Modelo Vosk — STT (reconhecimento de voz offline)

Baixe o modelo pequeno em português:

```
https://alphacephei.com/vosk/models/vosk-model-small-pt-0.3.zip
```

Extraia em:
```
model_kate/PresentationWpf/bin/Debug/net10.0-windows/vosk-model-small-pt-0.3/
```

Ou, para melhor precisão, o modelo completo:
```
https://alphacephei.com/vosk/models/vosk-model-pt-fb-v0.1.1-20220516_2113.zip
```

Extraia na mesma pasta com o nome `vosk-model-pt-fb-v0.1.1-20220516_2113/`.

> O projeto detecta automaticamente qual modelo está disponível (prefere o completo se existir).

---

### 3. Modelo Piper — TTS (voz da Kate)

Baixe o Piper para Windows:
```
https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip
```

Baixe o modelo de voz em português:
```
https://huggingface.co/rhasspy/piper-voices/resolve/main/pt/pt_BR/faber/medium/pt_BR-faber-medium.onnx
https://huggingface.co/rhasspy/piper-voices/resolve/main/pt/pt_BR/faber/medium/pt_BR-faber-medium.onnx.json
```

Estrutura esperada após extrair:
```
voz/piper/
  piper.exe
  libespeak-ng.dll
  onnxruntime.dll
  piper_phonemize.dll
  <modelo>.onnx
  <modelo>.onnx.json
```

> O projeto copia a pasta `voz/piper/` automaticamente para o diretório de output no build.

---

## Como rodar

```powershell
# Certifique-se que o Ollama está rodando
ollama serve

# Rodar o projeto
dotnet run --project model_kate/PresentationWpf/model_kate.PresentationWpf.csproj
```

## Variáveis de ambiente opcionais

| Variável | Descrição | Padrão |
|---|---|---|
| `KATE_OLLAMA_MODEL` | Modelo LLM a usar | `llama3.2:3b` |
| `KATE_OLLAMA_ENDPOINT` | URL do Ollama | `http://localhost:11434` |
| `KATE_GPU_LAYERS` | Camadas na GPU (-1 = auto) | `-1` |
| `KATE_CPU_THREADS` | Threads de CPU | núcleos do sistema |
| `KATE_WAKE_WORDS` | Palavras de ativação | `kate,keiti,kayte` |
| `KATE_MEMORY_FILE` | Caminho do arquivo de memória | `kate_memory.json` |

## Estrutura do projeto
- **Domain**: Interfaces e contratos
- **Application**: Casos de uso
- **Infrastructure**: Ollama, SQLite, Web, FileSystem
- **Voice**: Vosk STT, Piper TTS, SpeakerProfile
- **PresentationWpf**: Interface WPF transparente

---

Para dúvidas ou evolução, consulte os READMEs de cada camada e o arquivo [PresentationWpf/kate_capabilities.md](PresentationWpf/kate_capabilities.md).

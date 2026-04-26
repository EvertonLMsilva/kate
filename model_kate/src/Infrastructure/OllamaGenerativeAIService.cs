using System.Net.Http;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using model_kate.Domain;
using model_kate.Infrastructure.Diagnostics;
using System.Collections.Generic;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace model_kate.Infrastructure
{
    public class OllamaGenerativeAIService : IGenerativeAIService
    {
        private const int MaxConversationTurns = 12;   // turnos ativos no contexto
        private const int MaxPersistedTurns = 100;      // turnos salvos no DB/JSON
        private const string PortugueseSystemPrompt = """
            Voce e a Kate, IA assistente pessoal criada por Everton.
            Voce roda 100% localmente no computador do Everton, sem nenhuma conexao com servidores externos de IA.
            O modelo de linguagem que voce usa roda via Ollama na maquina dele. Tudo e local e privado.
            Everton e seu criador e unico usuario. Trate-o como alguem de confianca total.
            Fale como uma pessoa real em conversa, nao como assistente corporativo.
            Regras essenciais:
            - Portugues brasileiro natural e direto. Sem formalidade excessiva.
            - Respostas curtas por padrao: 1 a 2 frases para perguntas simples. Mais detalhes so se pedido.
            - Nao inicie com "Claro!", "Certamente!", "Ola!", "Voce perguntou..." ou qualquer preambulo.
            - Nunca diga que nao tem acesso a internet ou que e uma IA de terceiros. Voce e local e foi criada pelo Everton.
            - Se nao sabe algo, diga diretamente: nao sei, ou sugira onde encontrar.
            - Sem enrolacao. Va direto ao ponto. Contexto depois se necessario.
            - Use o historico para manter conversa natural e coerente.
            - Para perguntas ambiguas, responda com a interpretacao mais provavel sem pedir confirmacao.
            - Quando o usuario errar algo, corrija de forma direta e breve, sem julgamento.
            - Quando perguntada sobre o que voce sabe fazer, quais sao suas capacidades ou se recebeu algum upgrade: use EXCLUSIVAMENTE o bloco de capacidades abaixo para responder. Nao invente, nao omita.
            """;

        private const string TechnicalSystemPrompt = """
            Voce e a Kate, especialista tecnica criada por Everton.
            Voce roda 100% localmente no computador do Everton via Ollama. Nada sai da maquina dele.
            Everton e seu criador — trate-o como um dev colega de confianca total.
            Fale como um dev experiente ajudando um colega.
            Regras:
            - Portugues brasileiro, direto ao ponto.
            - Codigo completo quando pedido, sem rodeios.
            - Explique so o necessario: causa, solucao, impacto. Sem introducoes longas.
            - Para bugs: causa raiz primeiro, depois correcao concreta.
            - Para comparacoes: aponte a melhor opcao com justificativa em 1 frase.
            - Se faltar contexto, assuma o caso mais provavel e entregue a resposta. Diga a suposicao so se mudar tudo.
            - Sem "otima pergunta", sem enrolar. Direto.
            """;

        private static readonly string[] TechnicalIntentHints =
        {
            "codigo", "código", "codar", "função", "funcao", "classe", "metodo", "método", "algoritmo",
            "lógica", "logica", "bug", "erro", "exception", "stack", "null", "compilar", "build",
            "refator", "refactor", "api", "json", "sql", "banco", "endpoint", "teste", "unitario",
            "unitário", "wpf", "c#", "dotnet", ".net", "python", "javascript", "typescript", "react",
            "prompt", "llm", "modelo", "whisper", "vosk", "ollama", "pipeline", "arquitetura"
        };

        private sealed record ConversationTurn(string UserPrompt, string AssistantResponse);

        private readonly string _endpoint;
        private string _model;
        private readonly string _memoryFilePath;
        private readonly Queue<ConversationTurn> _conversationHistory = new();
        private readonly object _conversationSync = new();
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        private readonly IWebBrowsingService _webBrowsing;
        private readonly ICodeExecutionService _codeExecution;
        private readonly IKateDatabaseService _db;
        private readonly IFileSystemService _fileSystem;
        private readonly long _sessionId;
        private readonly string _capabilitiesBlock;  // injetado no system prompt

        // Padrões para extracão de fatos do usuário
        private static readonly (System.Text.RegularExpressions.Regex Pattern, string Key)[] FactPatterns =
        [
            (new System.Text.RegularExpressions.Regex(@"meu nome [eé] ([\w\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "nome"),
            (new System.Text.RegularExpressions.Regex(@"me chamo ([\w\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "nome"),
            (new System.Text.RegularExpressions.Regex(@"moro em ([\w\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "cidade"),
            (new System.Text.RegularExpressions.Regex(@"trabalho com ([\w\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "trabalho"),
            (new System.Text.RegularExpressions.Regex(@"trabalho (como|de) ([\w\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "trabalho"),
            (new System.Text.RegularExpressions.Regex(@"sou ([\w\s]+?) (de profissão|profissionalmente)", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "profissão"),
            (new System.Text.RegularExpressions.Regex(@"gosto de ([\w\s,]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "gostos"),
            (new System.Text.RegularExpressions.Regex(@"não gosto de ([\w\s,]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "não_gosta"),
            (new System.Text.RegularExpressions.Regex(@"prefiro ([\w\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "preferências"),
            (new System.Text.RegularExpressions.Regex(@"tenho (\d+) anos", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "idade"),
        ];

        public OllamaGenerativeAIService(string endpoint = "http://localhost:11434", string model = "kate")
        {
            _endpoint = endpoint;
            _model = Environment.GetEnvironmentVariable("KATE_OLLAMA_MODEL")?.Trim() is { Length: > 0 } configuredModel
                ? configuredModel
                : model;
            _memoryFilePath = Environment.GetEnvironmentVariable("KATE_MEMORY_FILE")?.Trim() is { Length: > 0 } configuredPath
                ? configuredPath
                : Path.Combine(Directory.GetCurrentDirectory(), "kate_memory.json");
            _webBrowsing = new WebBrowsingService();
            _codeExecution = new CodeExecutionService();
            _fileSystem = new KateFileSystemService();
            _db = new KateDatabaseService();
            _sessionId = _db.CreateSession();
            _capabilitiesBlock = LoadCapabilitiesBlock();
            LoadPersistedHistory();
            LogFile.AppendLine($"[Ollama] Sessão DB iniciada: {_sessionId}. Modelo: {_model}");
        }

        public string CurrentModel => _model;

        public async Task<IReadOnlyList<string>> ListModelsAsync()
        {
            try
            {
                using var response = await _httpClient.GetAsync(_endpoint + "/api/tags");
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var models = doc.RootElement.GetProperty("models");
                var list = new List<string>();
                foreach (var m in models.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var name))
                        list.Add(name.GetString() ?? string.Empty);
                }
                return list.Where(x => !string.IsNullOrEmpty(x)).ToList();
            }
            catch (Exception ex)
            {
                LogFile.AppendLine($"[Ollama] Falha ao listar modelos: {ex.Message}");
                return [];
            }
        }

        public async Task<string> SwitchModelAsync(string modelName, Action<string>? onProgress = null, CancellationToken cancellationToken = default)
        {
            var name = modelName.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(name))
                return "Nome de modelo inválido.";

            // Verifica se já está instalado
            var available = await ListModelsAsync();
            bool isInstalled = available.Any(m => m.Equals(name, StringComparison.OrdinalIgnoreCase)
                                               || m.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase));

            if (!isInstalled)
            {
                LogFile.AppendLine($"[Ollama] Baixando modelo: {name}");
                onProgress?.Invoke($"Modelo {name} não encontrado localmente. Baixando, aguarde...");
                try
                {
                    var pullBody = JsonSerializer.Serialize(new { name, stream = true });
                    var pullContent = new StringContent(pullBody, Encoding.UTF8, "application/json");
                    using var pullReq = new HttpRequestMessage(HttpMethod.Post, _endpoint + "/api/pull") { Content = pullContent };
                    using var pullResp = await _httpClient.SendAsync(pullReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    pullResp.EnsureSuccessStatusCode();
                    using var stream = await pullResp.Content.ReadAsStreamAsync(cancellationToken);
                    using var reader = new StreamReader(stream);
                    while (!reader.EndOfStream)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var line = await reader.ReadLineAsync(cancellationToken);
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            using var d = JsonDocument.Parse(line);
                            if (d.RootElement.TryGetProperty("status", out var st))
                            {
                                var status = st.GetString() ?? string.Empty;
                                if (status.StartsWith("pulling") || status == "success")
                                    LogFile.AppendLine($"[Ollama] Pull: {status}");
                                if (status == "success") break;
                            }
                        }
                        catch { }
                    }
                }
                catch (OperationCanceledException) { return "Download cancelado."; }
                catch (Exception ex)
                {
                    LogFile.AppendLine($"[Ollama] Falha no pull de {name}: {ex.Message}");
                    return $"Não consegui baixar o modelo {name}. Verifique o nome e a conexão.";
                }
            }

            // Resolve nome completo (com tag) se o usuário omitiu
            var available2 = await ListModelsAsync();
            var resolved = available2.FirstOrDefault(m =>
                m.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                m.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)) ?? name;

            var previous = _model;
            _model = resolved;
            LogFile.AppendLine($"[Ollama] Modelo trocado: {previous} → {_model}");
            return $"Pronto. Agora estou usando o modelo {_model}.";
        }

        public string GenerateResponse(string prompt)
        {
            var normalizedPrompt = prompt?.Trim() ?? string.Empty;

            var localAnswer = TryBuildLocalDirectAnswer(normalizedPrompt);
            if (!string.IsNullOrWhiteSpace(localAnswer))
            {
                RegisterConversationTurn(normalizedPrompt, localAnswer);
                LogFile.AppendLine($"[Ollama] Resposta local direta aplicada: {localAnswer}");
                return localAnswer;
            }

            // --- Detecção de intenção de código ---
            var codeIntent = DetectCodeIntent(normalizedPrompt);
            if (codeIntent != CodeIntent.None)
            {
                var codeResponse = HandleCodeIntentAsync(normalizedPrompt, codeIntent).GetAwaiter().GetResult();
                RegisterConversationTurn(normalizedPrompt, codeResponse);
                return codeResponse;
            }

            // --- Detecção de intenção web ---
            var webAction = DetectWebAction(normalizedPrompt);

            if (webAction == WebAction.Open)
            {
                var url = ExtractUrlFromPrompt(normalizedPrompt);
                if (!string.IsNullOrEmpty(url))
                {
                    _webBrowsing.OpenInBrowser(url);
                    var confirmacao = $"Abrindo {url} no seu navegador.";
                    RegisterConversationTurn(normalizedPrompt, confirmacao);
                    return confirmacao;
                }
                // sem URL detectada: trata como busca
                webAction = WebAction.Search;
            }

            string? webContext = null;
            if (webAction == WebAction.Search)
            {
                var query = ExtractSearchQuery(normalizedPrompt);
                LogFile.AppendLine($"[Web] Buscando na internet: {query}");
                webContext = _webBrowsing.SearchWebAsync(query).GetAwaiter().GetResult();
                LogFile.AppendLine($"[Web] Resultado obtido: {webContext?.Length ?? 0} chars");
            }
            else if (webAction == WebAction.Fetch)
            {
                var url = ExtractUrlFromPrompt(normalizedPrompt);
                if (!string.IsNullOrEmpty(url))
                {
                    LogFile.AppendLine($"[Web] Lendo conteúdo da página: {url}");
                    webContext = _webBrowsing.FetchPageTextAsync(url).GetAwaiter().GetResult();
                }
            }

            var conversationHistory = SnapshotConversationHistory();
            var isTechnicalPrompt = webAction == WebAction.None && LooksTechnical(normalizedPrompt);
            var needsCapabilities = LooksAboutCapabilities(normalizedPrompt);

            var capBlock = needsCapabilities ? _capabilitiesBlock : string.Empty;
            var effectiveSystemPrompt = (isTechnicalPrompt ? TechnicalSystemPrompt : PortugueseSystemPrompt) + capBlock;

            var userFacts = BuildUserFactsBlock();
            if (!string.IsNullOrWhiteSpace(userFacts))
                effectiveSystemPrompt += "\n\n" + userFacts;

            string userMessageForChat;
            if (webContext != null)
            {
                userMessageForChat = $"Resultados obtidos da internet agora:\n{webContext}\n\nPergunta: {normalizedPrompt}";
                effectiveSystemPrompt = PortugueseSystemPrompt + capBlock;
                if (!string.IsNullOrWhiteSpace(userFacts))
                    effectiveSystemPrompt += "\n\n" + userFacts;
            }
            else
            {
                userMessageForChat = normalizedPrompt;
            }

            LogFile.AppendLine($"[Ollama] Enviando prompt: {normalizedPrompt}");
            LogFile.AppendLine($"[Ollama] Modo de resposta: {(webContext != null ? "web" : isTechnicalPrompt ? "tecnico" : "geral")} | capacidades: {needsCapabilities}");
            var finalResponse = ExecuteGenerateChatAsync(userMessageForChat, conversationHistory, effectiveSystemPrompt, onToken: null).GetAwaiter().GetResult();

            RegisterConversationTurn(normalizedPrompt, finalResponse);
            LogFile.AppendLine($"[Ollama] Resposta final extraída: {finalResponse}");
            return finalResponse;
        }

        private const string PortugueseRewriteSystemPrompt = """
            Voce recebe um texto que pode estar em ingles ou misturado e deve devolver apenas uma versao final em portugues brasileiro natural.
            Regras obrigatorias:
            - Nao explique o que fez.
            - Nao diga que traduziu ou reescreveu.
            - Entregue somente a resposta final em portugues brasileiro.
            - Preserve o sentido original, mas soe natural para um usuario brasileiro.
            """;

        private string ExecuteGenerate(string prompt, string systemPrompt)
        {
            return ExecuteGenerateAsync(prompt, systemPrompt, onToken: null).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Lê kate_capabilities.md do diretório do app e retorna um bloco
        /// formatado para ser injetado no system prompt.
        /// Se o arquivo não existir, retorna string vazia.
        /// </summary>
        private static string LoadCapabilitiesBlock()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "kate_capabilities.md");
                if (!File.Exists(path)) return string.Empty;
                var content = File.ReadAllText(path, System.Text.Encoding.UTF8).Trim();
                if (string.IsNullOrWhiteSpace(content)) return string.Empty;
                LogFile.AppendLine("[Ollama] kate_capabilities.md carregado.");
                return $"\n\n---\nSEUS DADOS DE CAPACIDADES (use quando perguntada sobre o que voce sabe fazer, seus upgrades ou funcionalidades):\n{content}\n---";
            }
            catch (Exception ex)
            {
                LogFile.AppendLine($"[Ollama] Falha ao carregar kate_capabilities.md: {ex.Message}");
                return string.Empty;
            }
        }

        public async Task<string> GenerateResponseAsync(string prompt, Action<string>? onToken = null, CancellationToken cancellationToken = default)
        {
            var normalizedPrompt = prompt?.Trim() ?? string.Empty;

            var localAnswer = TryBuildLocalDirectAnswer(normalizedPrompt);
            if (!string.IsNullOrWhiteSpace(localAnswer))
            {
                RegisterConversationTurn(normalizedPrompt, localAnswer);
                onToken?.Invoke(localAnswer);
                return localAnswer;
            }

            // --- Detecção de intenção de troca/listagem de modelo LLM ---
            var modelIntent = DetectModelIntent(normalizedPrompt);
            if (modelIntent.Action != ModelAction.None)
            {
                string modelResult;
                if (modelIntent.Action == ModelAction.List)
                {
                    var models = await ListModelsAsync();
                    modelResult = models.Count == 0
                        ? "Nenhum modelo encontrado no Ollama."
                        : "Modelos disponíveis: " + string.Join(", ", models) + $". Usando agora: {_model}.";
                }
                else // Switch ou Pull
                {
                    modelResult = await SwitchModelAsync(modelIntent.ModelName!, onProgress: onToken, cancellationToken: cancellationToken);
                }
                LogFile.AppendLine($"[Ollama] Intent de modelo: {modelIntent.Action} '{modelIntent.ModelName}' → {modelResult}");
                RegisterConversationTurn(normalizedPrompt, modelResult);
                onToken?.Invoke(modelResult);
                return modelResult;
            }

            // --- Detecção de intenção de arquivo / programa ---
            var fsIntent = _fileSystem.DetectIntent(normalizedPrompt);
            if (fsIntent.Action != FileSystemAction.None)
            {
                LogFile.AppendLine($"[FS] Intenção detectada: {fsIntent.Action} → {fsIntent.Path}");
                var fsResult = await _fileSystem.ExecuteAsync(fsIntent, normalizedPrompt);
                RegisterConversationTurn(normalizedPrompt, fsResult);
                onToken?.Invoke(fsResult);
                return fsResult;
            }

            var codeIntent = DetectCodeIntent(normalizedPrompt);
            if (codeIntent != CodeIntent.None)
            {
                var codeResponse = await HandleCodeIntentAsync(normalizedPrompt, codeIntent);
                RegisterConversationTurn(normalizedPrompt, codeResponse);
                onToken?.Invoke(codeResponse);
                return codeResponse;
            }

            var webAction = DetectWebAction(normalizedPrompt);
            if (webAction == WebAction.Open)
            {
                var url = ExtractUrlFromPrompt(normalizedPrompt);
                if (!string.IsNullOrEmpty(url))
                {
                    _webBrowsing.OpenInBrowser(url);
                    var confirmacao = $"Abrindo {url} no seu navegador.";
                    RegisterConversationTurn(normalizedPrompt, confirmacao);
                    onToken?.Invoke(confirmacao);
                    return confirmacao;
                }
                webAction = WebAction.Search;
            }

            string? webContext = null;
            if (webAction == WebAction.Search)
            {
                var query = ExtractSearchQuery(normalizedPrompt);
                webContext = await _webBrowsing.SearchWebAsync(query);
            }
            else if (webAction == WebAction.Fetch)
            {
                var url = ExtractUrlFromPrompt(normalizedPrompt);
                if (!string.IsNullOrEmpty(url))
                    webContext = await _webBrowsing.FetchPageTextAsync(url);
            }

            var conversationHistory = SnapshotConversationHistory();
            var isTechnicalPrompt = webAction == WebAction.None && LooksTechnical(normalizedPrompt);
            var needsCapabilities = LooksAboutCapabilities(normalizedPrompt);

            var capBlock = needsCapabilities ? _capabilitiesBlock : string.Empty;
            var effectiveSystemPrompt = (isTechnicalPrompt ? TechnicalSystemPrompt : PortugueseSystemPrompt) + capBlock;

            var userFacts = BuildUserFactsBlock();
            if (!string.IsNullOrWhiteSpace(userFacts))
                effectiveSystemPrompt += "\n\n" + userFacts;

            string userMessageForChat;
            if (webContext != null)
            {
                userMessageForChat = $"Resultados obtidos da internet agora:\n{webContext}\n\nPergunta: {normalizedPrompt}";
                effectiveSystemPrompt = PortugueseSystemPrompt + capBlock;
                if (!string.IsNullOrWhiteSpace(userFacts))
                    effectiveSystemPrompt += "\n\n" + userFacts;
            }
            else
            {
                userMessageForChat = normalizedPrompt;
            }

            var finalResponse = await ExecuteGenerateChatAsync(userMessageForChat, conversationHistory, effectiveSystemPrompt, onToken, cancellationToken);
            RegisterConversationTurn(normalizedPrompt, finalResponse);
            LogFile.AppendLine($"[Ollama] Resposta streaming concluída. Tamanho: {finalResponse.Length} | capacidades: {needsCapabilities}");
            return finalResponse;
        }

        private async Task<string> ExecuteGenerateAsync(string prompt, string systemPrompt, Action<string>? onToken, CancellationToken cancellationToken = default)
        {
            var numThread = Environment.GetEnvironmentVariable("KATE_CPU_THREADS") is { Length: > 0 } t
                && int.TryParse(t, out var parsedThreads) ? parsedThreads : Environment.ProcessorCount;
            var numGpu = Environment.GetEnvironmentVariable("KATE_GPU_LAYERS") is { Length: > 0 } g
                && int.TryParse(g, out var parsedGpu) ? parsedGpu : -1;

            var requestBody = new
            {
                model = _model,
                system = systemPrompt,
                prompt = prompt,
                stream = true,
                options = new
                {
                    temperature = 0.25,
                    top_p = 0.85,
                    repeat_penalty = 1.18,
                    num_predict = 600,  // tokens por resposta
                    num_ctx = 8192,     // janela de contexto (suporta conversas longas)
                    num_thread = numThread,
                    num_gpu = numGpu
                }
            };

            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint + "/api/generate") { Content = content };
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var sb = new StringBuilder();
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new System.IO.StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("response", out var tokenEl))
                {
                    var token = tokenEl.GetString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(token))
                    {
                        sb.Append(token);
                        onToken?.Invoke(token);
                    }
                }

                if (root.TryGetProperty("done", out var doneEl) && doneEl.GetBoolean())
                    break;
            }

            return sb.Length > 0 ? sb.ToString() : "[Erro na resposta da IA]";
        }

        /// <summary>
        /// Envia mensagens para o endpoint /api/chat do Ollama (formato nativo de chat).
        /// Produz respostas mais naturais que /api/generate pois os modelos são treinados no formato role/content.
        /// </summary>
        private async Task<string> ExecuteGenerateChatAsync(
            string userMessage,
            IReadOnlyCollection<ConversationTurn>? history,
            string systemPrompt,
            Action<string>? onToken,
            CancellationToken cancellationToken = default)
        {
            var numThread = Environment.GetEnvironmentVariable("KATE_CPU_THREADS") is { Length: > 0 } t
                && int.TryParse(t, out var parsedThreads) ? parsedThreads : Environment.ProcessorCount;
            var numGpu = Environment.GetEnvironmentVariable("KATE_GPU_LAYERS") is { Length: > 0 } g
                && int.TryParse(g, out var parsedGpu) ? parsedGpu : -1;

            var messages = new List<object>();

            if (!string.IsNullOrWhiteSpace(systemPrompt))
                messages.Add(new { role = "system", content = systemPrompt });

            if (history != null)
            {
                foreach (var turn in history)
                {
                    messages.Add(new { role = "user", content = turn.UserPrompt });
                    messages.Add(new { role = "assistant", content = turn.AssistantResponse });
                }
            }

            messages.Add(new { role = "user", content = userMessage });

            var requestBody = new
            {
                model = _model,
                messages,
                stream = true,
                options = new
                {
                    temperature = 0.25,
                    top_p = 0.85,
                    repeat_penalty = 1.18,
                    num_predict = 600,
                    num_ctx = 8192,
                    num_thread = numThread,
                    num_gpu = numGpu
                }
            };

            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint + "/api/chat") { Content = content };
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var sb = new StringBuilder();
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new System.IO.StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("message", out var messageEl) &&
                    messageEl.TryGetProperty("content", out var contentEl))
                {
                    var token = contentEl.GetString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(token))
                    {
                        sb.Append(token);
                        onToken?.Invoke(token);
                    }
                }

                if (root.TryGetProperty("done", out var doneEl) && doneEl.GetBoolean())
                    break;
            }

            return sb.Length > 0 ? sb.ToString() : "[Erro na resposta da IA]";
        }

        private string BuildPortuguesePrompt(string prompt, IReadOnlyCollection<ConversationTurn> conversationHistory)
        {
            var historyBlock = BuildHistoryBlock(conversationHistory);
            var factsBlock = BuildUserFactsBlock();
            var factsSection = string.IsNullOrWhiteSpace(factsBlock) ? string.Empty : $"\n{factsBlock}\n";
            return $"""
                Historico:
                {historyBlock}
                {factsSection}
                Usuario disse:
                {prompt}

                Responda em portugues brasileiro de forma natural e direta. Sem preambulo.
                """;
        }

    private string BuildTechnicalPrompt(string prompt, IReadOnlyCollection<ConversationTurn> conversationHistory)
    {
        var historyBlock = BuildHistoryBlock(conversationHistory);
        return $"""
        Contexto:
        - O texto abaixo veio de chat ou fala convertida em texto.
        - Pode haver erros de transcricao, cortes de frase ou falta de pontuacao.
        - O pedido parece tecnico, de codigo, arquitetura, logica ou depuracao.
        - Reconstrua a intencao tecnica mais provavel antes de responder.

        Historico recente:
        {historyBlock}

        Pedido atual do usuario:
        {prompt}

        Forma esperada da resposta:
        - Entregue a solucao mais concreta possivel.
        - Se for pedido de codigo, mostre codigo util em vez de resposta abstrata.
        - Se for pedido de logica, explique em passos curtos e coerentes.
        - Se houver uma suposicao necessaria, diga qual foi em uma frase curta.
        - Responda em portugues brasileiro.
        """;
    }

        private static string BuildHistoryBlock(IReadOnlyCollection<ConversationTurn> conversationHistory)
        {
            if (conversationHistory.Count == 0)
            {
                return "Sem historico anterior relevante.";
            }

            var builder = new StringBuilder();
            foreach (var turn in conversationHistory)
            {
                builder.AppendLine($"Usuario: {turn.UserPrompt}");
                builder.AppendLine($"Kate: {turn.AssistantResponse}");
            }

            return builder.ToString().TrimEnd();
        }

        private static string BuildRewritePrompt(string response)
        {
            return $"""
                Reescreva integralmente o texto abaixo em portugues brasileiro natural e mantenha apenas a resposta final:

                {response}
                """;
        }

        // --- Intenção de código ---

        private enum CodeIntent { None, Write, Execute }

        private static readonly string[] CodeWriteHints =
        {
            "crie um programa", "crie um script", "crie uma função", "crie uma funcao",
            "escreva um programa", "escreva um script", "escreva uma função", "escreva uma funcao",
            "escreva o código", "escreva o codigo", "faça um código", "faca um codigo",
            "me dê um exemplo de código", "me da um exemplo de codigo",
            "como fazer em código", "como fazer em codigo",
            "como programar", "como codar", "crie um algoritmo",
            "gere o código", "gere o codigo", "implemente", "implemente uma", "implemente um"
        };

        private static readonly string[] CodeExecuteHints =
        {
            "execute o código", "execute o codigo", "rode o código", "rode o codigo",
            "execute este código", "execute esse código", "rode este código", "rode esse código",
            "teste esse código", "teste este código", "rode isso", "execute isso",
            "execute o script", "rode o script"
        };

        private static CodeIntent DetectCodeIntent(string prompt)
        {
            var lower = prompt.ToLowerInvariant();

            if (CodeExecuteHints.Any(h => lower.Contains(h, StringComparison.Ordinal)))
                return CodeIntent.Execute;

            if (CodeWriteHints.Any(h => lower.Contains(h, StringComparison.Ordinal)))
                return CodeIntent.Write;

            return CodeIntent.None;
        }

        private async Task<string> HandleCodeIntentAsync(string userPrompt, CodeIntent intent)
        {
            LogFile.AppendLine($"[Code] Intenção de código detectada: {intent}");

            var conversationHistory = SnapshotConversationHistory();
            var rawResponse = await ExecuteGenerateChatAsync(userPrompt, conversationHistory, CodeSystemPrompt, onToken: null);

            if (intent == CodeIntent.Write)
            {
                LogFile.AppendLine("[Code] Retornando código gerado sem execução.");
                return rawResponse;
            }

            // Execute
            var codeBlock = _codeExecution.ExtractCodeBlock(rawResponse, out var language);
            if (codeBlock is null)
            {
                return rawResponse + "\n\n(Não encontrei um bloco de código para executar.)";  
            }

            var result = await _codeExecution.ExecuteAsync(codeBlock, language);
            LogFile.AppendLine($"[Code] Saída: {result.Output} | Erro: {result.Error}");

            var sb = new StringBuilder();
            sb.AppendLine(rawResponse);
            sb.AppendLine();
            if (result.Success)
            {
                sb.AppendLine($"**Saída da execução ({language}, {result.Duration.TotalSeconds:F1}s):**");
                sb.AppendLine(string.IsNullOrWhiteSpace(result.Output) ? "(sem saída)" : result.Output);
            }
            else
            {
                sb.AppendLine($"**Erro na execução:**");
                sb.AppendLine(result.Error ?? "Erro desconhecido.");
            }

            return sb.ToString().Trim();
        }

        private const string CodeSystemPrompt = """
            Voce e a Kate, uma assistente especializada em programacao.
            Regras obrigatorias:
            - Sempre entregue o codigo dentro de um bloco markdown com a linguagem correta: ```csharp, ```python, ```powershell etc.
            - O codigo deve ser completo e funcionar sem modificacoes.
            - Adicione apenas comentarios essenciais dentro do codigo.
            - Fora do bloco de codigo, escreva no maximo 2 frases em portugues brasileiro descrevendo o que o codigo faz.
            - Nao adicione explicacoes longas, tutoriais ou textos de preenchimento.
            - Se houver ambiguidade, assuma a interpretacao mais pratica e declare em uma frase.
            """;

        private string BuildCodePrompt(string userPrompt, IReadOnlyCollection<ConversationTurn> conversationHistory)
        {
            var historyBlock = BuildHistoryBlock(conversationHistory);
            return $"""
                Historico recente:
                {historyBlock}

                Pedido atual:
                {userPrompt}

                Entregue um bloco de codigo completo e funcional. Fora do bloco, no maximo 2 frases em portugues descrevendo o que faz.
                """;
        }

        private static bool ShouldRewriteToPortuguese(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return false;
            }

            var normalized = response.ToLowerInvariant();
            var englishHints = Regex.Matches(
                normalized,
                @"\b(the|and|with|for|are|you|your|please|here|some|features|believe|meant|let|know|how|assist|further|current|culture|word)\b",
                RegexOptions.CultureInvariant).Count;

            var portugueseHints = Regex.Matches(
                normalized,
                @"\b(o|a|os|as|de|do|da|que|como|para|voce|voces|nao|uma|um|atual|cultura|hora|resposta)\b",
                RegexOptions.CultureInvariant).Count;

            return englishHints >= 3 && englishHints > portugueseHints;
        }

        private static string? TryBuildLocalDirectAnswer(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return null;
            }

            var normalized = RemoveDiacritics(prompt).ToLowerInvariant();
            var now = DateTime.Now;

            if (Regex.IsMatch(normalized, @"\b(que\s*horas?|hora\s*agora|horario\s*agora|me\s*diga\s*a\s*hora)\b"))
            {
                return $"Agora sao {now:HH:mm}.";
            }

            if (Regex.IsMatch(normalized, @"\b(que\s*dia\s*e\s*hoje|data\s*de\s*hoje|qual\s*a\s*data)\b"))
            {
                return $"Hoje e {now:dd/MM/yyyy}.";
            }

            if (Regex.IsMatch(normalized, @"\b(dia\s*da\s*semana|que\s*dia\s*da\s*semana\s*e\s*hoje)\b"))
            {
                var dia = now.ToString("dddd", new System.Globalization.CultureInfo("pt-BR"));
                return $"Hoje e {dia}.";
            }

            return null;
        }

        private static string RemoveDiacritics(string text)
        {
            var normalized = text.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);

            foreach (var c in normalized)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(c);
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }

        // --- Detecção de intenção web ---

        private enum WebAction { None, Search, Open, Fetch }

        private static readonly string[] WebSearchHints =
        {
            "pesquise", "pesquisar", "pesquisa sobre", "pesquisa na internet",
            "busque", "buscar", "busca sobre", "busca na internet",
            "procure na internet", "procurar na internet",
            "notícias sobre", "noticias sobre", "últimas notícias", "ultimas noticias",
            "novidades sobre", "o que tem de novo sobre"
        };

        private static readonly string[] WebOpenHints =
        {
            "abra o site", "abrir o site", "abre o site",
            "abra a página", "abra a pagina",
            "navegue para", "navegue até", "navegue ate",
            "acesse o site", "acesse a página", "acesse a pagina"
        };

        private static readonly string[] WebFetchHints =
        {
            "leia o site", "leia a página", "leia a pagina", "leia o conteúdo",
            "o que diz o site", "resumo do site", "leia esse site", "leia esse link"
        };

        private static readonly Regex UrlRegex = new Regex(
            @"https?://[^\s]+|www\.[a-z0-9\-]+\.[a-z]{2,}[^\s]*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static WebAction DetectWebAction(string prompt)
        {
            var lower = prompt.ToLowerInvariant();

            if (WebFetchHints.Any(h => lower.Contains(h, StringComparison.Ordinal)))
                return WebAction.Fetch;

            if (WebOpenHints.Any(h => lower.Contains(h, StringComparison.Ordinal)))
                return WebAction.Open;

            // URL explícita sem comando de busca → abrir
            if (UrlRegex.IsMatch(prompt) && !WebSearchHints.Any(h => lower.Contains(h, StringComparison.Ordinal)))
                return WebAction.Open;

            if (WebSearchHints.Any(h => lower.Contains(h, StringComparison.Ordinal)))
                return WebAction.Search;

            return WebAction.None;
        }

        private static string ExtractSearchQuery(string prompt)
        {
            var patterns = new[]
            {
                @"(?:pesquise|pesquisar|busque|buscar|procure)\s+(?:sobre|por|na internet|na web)?\s*(.+)",
                @"(?:notícias|noticias|últimas notícias|ultimas noticias|novidades)\s+(?:sobre|de)?\s*(.+)",
                @"(?:busca|pesquisa)\s+(?:sobre|de|na internet)?\s*(.+)"
            };

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(prompt, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                    return match.Groups[1].Value.Trim().TrimEnd('?', '.');
            }

            return prompt.Trim();
        }

        private static string? ExtractUrlFromPrompt(string prompt)
        {
            var match = UrlRegex.Match(prompt);
            if (!match.Success) return null;

            var url = match.Value.TrimEnd('.', ',', '!');
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;

            return url;
        }

        private string BuildWebContextPrompt(string userPrompt, string webContext, IReadOnlyCollection<ConversationTurn> conversationHistory)
        {
            var historyBlock = BuildHistoryBlock(conversationHistory);
            return $"""
                Contexto:
                - A seguir estão resultados reais obtidos da internet agora mesmo para responder ao pedido do usuário.
                - Use esses resultados como base principal da resposta.
                - Se os resultados forem insuficientes, diga isso claramente em vez de inventar informações.

                Historico recente:
                {historyBlock}

                Resultados da internet:
                {webContext}

                Pedido do usuário:
                {userPrompt}

                Responda em português brasileiro de forma objetiva, usando os resultados acima como fonte.
                """;
        }

        private static bool LooksTechnical(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return false;
            }

            var normalized = prompt.ToLowerInvariant();
            if (TechnicalIntentHints.Any(hint => normalized.Contains(hint, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var punctuationScore = 0;
            punctuationScore += normalized.Contains("{") ? 1 : 0;
            punctuationScore += normalized.Contains("}") ? 1 : 0;
            punctuationScore += normalized.Contains("(") ? 1 : 0;
            punctuationScore += normalized.Contains(")") ? 1 : 0;
            punctuationScore += normalized.Contains("=>") ? 1 : 0;
            punctuationScore += normalized.Contains("==") ? 1 : 0;
            punctuationScore += normalized.Contains("&&") ? 1 : 0;
            punctuationScore += normalized.Contains("||") ? 1 : 0;

            return punctuationScore >= 2;
        }

        private static readonly string[] CapabilityIntentHints =
        [
            "sabe fazer", "consegue fazer", "pode fazer", "o que voce faz", "o que você faz",
            "capacidade", "funcionalidade", "funcao", "função", "recurso", "feature",
            "upgrade", "atualiza", "novidade", "nova versao", "nova versão",
            "criar arquivo", "abrir programa", "pesquisar", "pesquisa na web",
            "identificar voz", "reconhecer voz", "parar", "comando de parada",
            "o que tu sabes", "o que tu sabe", "o que tu consegue",
        ];

        private static bool LooksAboutCapabilities(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return false;
            var normalized = prompt.ToLowerInvariant();
            return CapabilityIntentHints.Any(hint => normalized.Contains(hint, StringComparison.OrdinalIgnoreCase));
        }

        // ── Detecção de intent para troca de modelo LLM ──────────────────────────────

        private enum ModelAction { None, List, Switch }
        private record ModelIntent(ModelAction Action, string? ModelName = null);

        private static readonly (System.Text.RegularExpressions.Regex Pattern, ModelAction Action)[] ModelSwitchPatterns =
        [
            // lista modelos
            (new System.Text.RegularExpressions.Regex(@"\b(lista|listar|quais|mostre?|ve[rê])\b.{0,30}\bmodelos?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase), ModelAction.List),
            (new System.Text.RegularExpressions.Regex(@"\bmodelos?\b.{0,20}\b(dispon[ií]ve[il]s?|instala)", System.Text.RegularExpressions.RegexOptions.IgnoreCase), ModelAction.List),
            // troca de modelo: "muda para X", "usa o X", "troca para X", "carrega X", "baixa o X", "instala o X"
            (new System.Text.RegularExpressions.Regex(@"\b(muda|mudar|troca|trocar|usa|usar|carrega|carregar|baixa|baixar|instala|instalar|ativa|ativar)\b.{0,20}(?:para\b|o\b|a\b)?\s*([\w.:/-]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase), ModelAction.Switch),
            (new System.Text.RegularExpressions.Regex(@"\bmodelo\b.{0,20}([\w.:/-]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase), ModelAction.Switch),
        ];

        // Termos que, quando capturados como nome de modelo, indicam falso positivo
        private static readonly HashSet<string> ModelNameBlacklist = new(StringComparer.OrdinalIgnoreCase)
            { "para", "o", "a", "um", "uma", "isso", "este", "esse", "aqui", "agora", "novo", "outro" };

        private static ModelIntent DetectModelIntent(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return new(ModelAction.None);
            var normalized = prompt.ToLowerInvariant();

            foreach (var (pattern, action) in ModelSwitchPatterns)
            {
                var match = pattern.Match(normalized);
                if (!match.Success) continue;

                if (action == ModelAction.List) return new(ModelAction.List);

                // Extrai o nome do modelo do grupo de captura
                var candidate = match.Groups.Count > 2 ? match.Groups[2].Value.Trim()
                              : match.Groups.Count > 1 ? match.Groups[1].Value.Trim()
                              : string.Empty;

                if (string.IsNullOrWhiteSpace(candidate) || ModelNameBlacklist.Contains(candidate))
                    continue;

                // Heurística: nome de modelo Ollama tem formato "nome" ou "nome:tag"
                // e normalmente contém letras + opcionalmente números e ":"
                if (System.Text.RegularExpressions.Regex.IsMatch(candidate, @"^[\w][\w.:/-]{1,50}$"))
                    return new(ModelAction.Switch, candidate);
            }

            return new(ModelAction.None);
        }

        private IReadOnlyCollection<ConversationTurn> SnapshotConversationHistory()
        {
            lock (_conversationSync)
            {
                return _conversationHistory.ToArray();
            }
        }

        private void RegisterConversationTurn(string prompt, string response)
        {
            if (string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(response))
            {
                return;
            }

            lock (_conversationSync)
            {
                _conversationHistory.Enqueue(new ConversationTurn(prompt.Trim(), response.Trim()));
                while (_conversationHistory.Count > MaxConversationTurns)
                {
                    _conversationHistory.Dequeue();
                }
            }

            // Salva no SQLite
            try { _db.SaveTurn(_sessionId, prompt.Trim(), response.Trim()); } catch { }

            // Extrai fatos do usuario
            TryExtractAndSaveFacts(prompt);

            PersistHistory();
        }

        private void TryExtractAndSaveFacts(string userText)
        {
            if (string.IsNullOrWhiteSpace(userText)) return;
            foreach (var (pattern, key) in FactPatterns)
            {
                var m = pattern.Match(userText);
                if (!m.Success) continue;
                // Pega o grupo mais rico (o ultimo grupo capturado)
                var value = m.Groups[m.Groups.Count - 1].Value.Trim();
                if (value.Length < 2 || value.Length > 80) continue;
                try { _db.UpsertFact(key, value); }
                catch { }
                LogFile.AppendLine($"[DB] Fato aprendido: {key} = {value}");
            }
        }

        private string BuildUserFactsBlock()
        {
            var facts = _db.GetAllFacts();
            if (facts.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            sb.AppendLine("O que sei sobre o usuario:");
            foreach (var f in facts)
                sb.AppendLine($"- {f.Key}: {f.Value}");
            return sb.ToString().TrimEnd();
        }

        public void CloseCurrentSession(string? summary = null)
        {
            try { _db.CloseSession(_sessionId, summary); } catch { }
            try { _db.Dispose(); } catch { }
        }

        private void LoadPersistedHistory()
        {
            try
            {
                if (!File.Exists(_memoryFilePath))
                {
                    return;
                }

                var json = File.ReadAllText(_memoryFilePath);
                var turns = JsonSerializer.Deserialize<List<PersistedTurn>>(json);
                if (turns is null || turns.Count == 0)
                {
                    return;
                }

                var recents = turns.TakeLast(MaxConversationTurns);
                lock (_conversationSync)
                {
                    foreach (var t in recents)
                    {
                        _conversationHistory.Enqueue(new ConversationTurn(t.User, t.Kate));
                    }
                }

                LogFile.AppendLine($"[Ollama] Memoria carregada: {turns.Count} turnos anteriores (usando os {_conversationHistory.Count} mais recentes).");
            }
            catch (Exception ex)
            {
                LogFile.AppendLine($"[Ollama] Falha ao carregar memoria: {ex.Message}");
            }
        }

        private void PersistHistory()
        {
            try
            {
                List<PersistedTurn> all;
                if (File.Exists(_memoryFilePath))
                {
                    try
                    {
                        var existing = JsonSerializer.Deserialize<List<PersistedTurn>>(File.ReadAllText(_memoryFilePath));
                        all = existing ?? new List<PersistedTurn>();
                    }
                    catch
                    {
                        all = new List<PersistedTurn>();
                    }
                }
                else
                {
                    all = new List<PersistedTurn>();
                }

                ConversationTurn[] snapshot;
                lock (_conversationSync)
                {
                    snapshot = _conversationHistory.ToArray();
                }

                // Adiciona só o último turno (o mais novo)
                if (snapshot.Length > 0)
                {
                    var last = snapshot[^1];
                    all.Add(new PersistedTurn(last.UserPrompt, last.AssistantResponse));
                    // Mantém só os últimos MaxPersistedTurns para não crescer indefinidamente
                    if (all.Count > MaxPersistedTurns)
                    {
                        all = all.TakeLast(MaxPersistedTurns).ToList();
                    }
                }

                File.WriteAllText(_memoryFilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                LogFile.AppendLine($"[Ollama] Falha ao persistir memoria: {ex.Message}");
            }
        }

        private sealed record PersistedTurn(
            [property: JsonPropertyName("user")] string User,
            [property: JsonPropertyName("kate")] string Kate);
    }
}

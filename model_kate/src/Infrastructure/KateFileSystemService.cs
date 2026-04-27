using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using model_kate.Domain;
using model_kate.Infrastructure.Diagnostics;

namespace model_kate.Infrastructure
{
    /// <summary>
    /// Serviço que permite à Kate criar arquivos, ler, apagar, listar diretórios e abrir programas.
    /// Operações de arquivo são restritas às pastas permitidas (Desktop, Documents, Downloads e pasta do app).
    /// </summary>
    public sealed class KateFileSystemService : IFileSystemService
    {
        // ── Pastas permitidas para escrita ──────────────────────────────────
        private static readonly string[] AllowedWriteRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Directory.GetCurrentDirectory(),
        ];

        // ── Mapeamento de nomes de programa ─────────────────────────────────
        // Cada chave pode ser uma palavra ou frase que o usuário pode falar naturalmente.
        // A busca é fuzzy: basta qualquer token do que o usuário falou estar contido numa chave.
        private static readonly Dictionary<string, string> ProgramAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            // Editores de texto
            ["notepad"]              = "notepad.exe",
            ["bloco de notas"]       = "notepad.exe",
            ["bloco"]                = "notepad.exe",
            ["editor de texto"]      = "notepad.exe",
            ["caderninho"]           = "notepad.exe",
            // Calculadora
            ["calculadora"]          = "calc.exe",
            ["calc"]                 = "calc.exe",
            // Gerenciador de tarefas
            ["gerenciador de tarefas"] = "taskmgr.exe",
            ["gerenciador"]          = "taskmgr.exe",
            ["task manager"]         = "taskmgr.exe",
            // Explorador de arquivos
            ["explorador"]           = "explorer.exe",
            ["explorador de arquivos"] = "explorer.exe",
            ["gerenciador de arquivos"] = "explorer.exe",
            ["explorer"]             = "explorer.exe",
            ["meu computador"]       = "explorer.exe",
            ["pasta"]                = "explorer.exe",
            // Navegadores
            ["navegador"]            = "msedge",
            ["internet"]             = "msedge",
            ["chrome"]               = "chrome",
            ["google"]               = "chrome",
            ["google chrome"]        = "chrome",
            ["firefox"]              = "firefox",
            ["mozilla"]              = "firefox",
            ["edge"]                 = "msedge",
            ["microsoft edge"]       = "msedge",
            // VS Code
            ["vscode"]               = "code",
            ["vs code"]              = "code",
            ["visual studio code"]   = "code",
            ["code"]                 = "code",
            ["editor"]               = "code",
            // Visual Studio
            ["visual studio"]        = "devenv",
            ["devenv"]               = "devenv",
            // Office
            ["word"]                 = "winword",
            ["microsoft word"]       = "winword",
            ["editor de documentos"] = "winword",
            ["excel"]                = "excel",
            ["microsoft excel"]      = "excel",
            ["planilha"]             = "excel",
            ["powerpoint"]           = "powerpnt",
            ["power point"]          = "powerpnt",
            ["microsoft powerpoint"] = "powerpnt",
            ["apresentacao"]         = "powerpnt",
            ["apresentação"]         = "powerpnt",
            ["slides"]               = "powerpnt",
            // Paint
            ["paint"]                = "mspaint",
            ["paintbrush"]           = "mspaint",
            ["desenho"]              = "mspaint",
            // Terminais
            ["terminal"]             = "wt",
            ["windows terminal"]     = "wt",
            ["cmd"]                  = "cmd",
            ["prompt"]               = "cmd",
            ["prompt de comando"]    = "cmd",
            ["powershell"]           = "pwsh",
            ["power shell"]          = "pwsh",
            ["shell"]                = "pwsh",
            // Sistema
            ["configuracoes"]        = "ms-settings:",
            ["configurações"]        = "ms-settings:",
            ["configuracao"]         = "ms-settings:",
            ["configuração"]         = "ms-settings:",
            ["settings"]             = "ms-settings:",
            ["painel de controle"]   = "control.exe",
            ["painel"]               = "control.exe",
            // Entretenimento / comunicação
            ["spotify"]              = "spotify",
            ["spotfy"]               = "spotify",
            ["spotifi"]              = "spotify",
            ["espotify"]             = "spotify",
            ["musica"]               = "spotify",
            ["música"]               = "spotify",
            ["discord"]              = "discord",
            ["telegram"]             = "telegram",
            ["whatsapp"]             = "whatsapp",
            ["zap"]                  = "whatsapp",
            ["steam"]                = "steam",
            ["jogos"]                = "steam",
            ["obs"]                  = "obs64",
            ["obs studio"]           = "obs64",
            ["gravacao de tela"]     = "obs64",
            ["gravação de tela"]     = "obs64",
            ["teams"]                = "ms-teams",
            ["microsoft teams"]      = "ms-teams",
            ["reuniao"]              = "ms-teams",
            ["reunião"]              = "ms-teams",
            ["vlc"]                  = "vlc",
            ["player"]               = "vlc",
            ["reprodutor"]           = "vlc",
            ["video"]                = "vlc",
            ["vídeo"]                = "vlc",
        };

        // ── Padrões de detecção de intenção ─────────────────────────────────

        private static readonly Regex CreateFileRegex = new(
            @"(?:cri[ea]|escrev[ae]|salv[ae]|grav[ae]|create|write|save)(?:\s+um)?(?:\s+novo)?\s+(?:arquivo|ficheiro|texto|doc|file|text)\s+(?:chamado\s+|com\s+(?:o\s+)?nome\s+|named\s+)?[""']?(?<name>[\w\s.\-]+?)[""']?(?:\s+(?:com|contendo|de|no caminho|with)(?:\s+o\s+conteudo)?(?:\s+o\s+texto)?\s+[""']?(?<content>.+?)[""']?)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SaveContentRegex = new(
            @"(?:salv[ae]|grav[ae]|escrev[ae]|save|write)\s+(?:isso|o\s+seguinte|este\s+texto|o\s+texto|o\s+conte[uú]do|tudo\s+isso|this|this\s+text|this\s+content)\s+(?:no|em\s+um|in)\s+(?:arquivo|file)\s+(?:chamado\s+|com\s+(?:o\s+)?nome\s+|named\s+)?[""']?(?<name>[\w\s.\-]+?)[""']?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AppendRegex = new(
            @"(?:adicione?|acrescente?|inclua?|insira?|append|add)\s+(?:no\s+arquivo|ao\s+arquivo|no\s+final\s+do\s+arquivo|to\s+(?:file|the\s+file)|at\s+the\s+end\s+of\s+the\s+file)\s+[""']?(?<name>[\w\s.\-]+?)[""']?\s*[:|,]?\s*(?<content>.+)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex OverwriteRegex = new(
            @"(?:sobrescrev[ae]|substitu[ae]\s+todo\s+o\s+conte[uú]do\s+do\s+arquivo|reescrev[ae]|atualiz[ae]\s+o\s+arquivo)\s+[""']?(?<name>[\w\s.\-/\\:]+?)[""']?\s+(?:com|para)\s+[""']?(?<content>.+?)[""']?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReplaceInFileRegex = new(
            @"(?:substitu[ae]|troqu[ae]|alter[ae])\s+(?:no\s+arquivo\s+)?[""']?(?<name>[\w\s.\-/\\:]+?)[""']?\s+(?:de|o\s+trecho)\s+[""'](?<old>.+?)[""']\s+(?:para|por)\s+[""'](?<new>.+?)[""']\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReadFileRegex = new(
            @"(?:l[êe]ia?|mostr[ae]|exib[ae]|abra?\s+e\s+l[êe]ia?|read|show|display|open\s+and\s+read)\s+(?:o\s+arquivo|a\s+arquivo|o\s+conte[uú]do\s+do|the\s+file|file\s+content\s+of)\s+[""']?(?<name>[\w\s.\-/\\:]+?)[""']?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex OpenFileRegex = new(
            @"(?:abr[ae]|abre|inicie?|execut[ae]|open|start|launch)\s+(?:o\s+|a\s+|the\s+)?(?:arquivo|ficheiro|pasta|diretorio|file|folder|directory)\s+[""']?(?<name>[\w\s.\-/\\:~]+?)[""']?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ListDirRegex = new(
            @"(?:list[ae]|mostr[ae]|exib[ae]|list|show|display)\s+(?:os\s+arquivos|o\s+conte[uú]do|a\s+pasta|files|contents|folder)\s+(?:de\s+|do?\s+|da\s+|of\s+)?[""']?(?<path>[\w\s.\-/\\:~]*)[""']?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex OpenProgramRegex = new(
            @"(?:abr[ae]|inicie?|execut[ae]|ligue?|rode?|coloc[ae]|start|ativa|open|launch|run)\s+(?:o\s+|a\s+|um\s+|uma\s+|the\s+|o\s+programa\s+|o\s+aplicativo\s+|o\s+app\s+|o\s+software\s+|program\s+|application\s+|app\s+)?[""']?(?<name>[\w\s.\-]+?)[""']?(?:\s+(?:pra\s+mim|para\s+mim|agora|ai|aí|logo|now|please))?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TerminalCommandRegex = new(
            @"(?:roda|rodar|execute?|executar|run|manda|verifica|checa|confere|run|execute|check|verify)\s+(?:no\s+|in\s+)?(?:terminal|powershell|cmd|shell)(?:\s+o\s+comando|\s+the\s+command)?\s*[:\-]?\s*(?<cmd>[\s\S]+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TerminalCommandAltRegex = new(
            @"(?:comando|command|cmd)\s*[:\-]\s*(?<cmd>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] BlockedCommandHints =
        {
            "format ", "shutdown", "restart-computer", "stop-computer", "remove-item", "rm -rf",
            "rd /s", "del /f", "reg delete", "diskpart", "bcdedit", "cipher /w", "takeown"
        };

        private static readonly TimeSpan TerminalCommandTimeout = TimeSpan.FromSeconds(20);

        private static readonly Regex DeleteFileRegex = new(
            @"(?:apague?|delete?|remov[ae]|exclu[ia]|delete|remove)\s+(?:o\s+|the\s+)?(?:arquivo\s+|file\s+)?[""']?(?<name>[\w\s.\-/\\:]+?)[""']?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ── IFileSystemService ───────────────────────────────────────────────

        public FileSystemIntent DetectIntent(string prompt)
        {
            var lower = prompt.Trim();

            // Criar arquivo
            var m = CreateFileRegex.Match(lower);
            if (m.Success)
            {
                var name = m.Groups["name"].Value.Trim();
                var content = m.Groups["content"].Value.Trim();
                return new FileSystemIntent(FileSystemAction.CreateFile, ResolvePath(name), string.IsNullOrWhiteSpace(content) ? null : content);
            }

            m = SaveContentRegex.Match(lower);
            if (m.Success)
                return new FileSystemIntent(FileSystemAction.CreateFile, ResolvePath(m.Groups["name"].Value.Trim()), null);

            // Sobrescrever arquivo
            m = OverwriteRegex.Match(lower);
            if (m.Success)
            {
                var path = ResolvePath(m.Groups["name"].Value.Trim());
                var content = m.Groups["content"].Value.Trim();
                return new FileSystemIntent(FileSystemAction.OverwriteFile, path, content);
            }

            // Substituir trecho dentro de arquivo
            m = ReplaceInFileRegex.Match(lower);
            if (m.Success)
            {
                var path = ResolvePath(m.Groups["name"].Value.Trim());
                var oldValue = m.Groups["old"].Value;
                var newValue = m.Groups["new"].Value;
                return new FileSystemIntent(FileSystemAction.ReplaceInFile, path, oldValue + "\n---\n" + newValue);
            }

            // Adicionar a arquivo
            m = AppendRegex.Match(lower);
            if (m.Success)
                return new FileSystemIntent(FileSystemAction.AppendFile, ResolvePath(m.Groups["name"].Value.Trim()), m.Groups["content"].Value.Trim());

            // Ler arquivo
            m = ReadFileRegex.Match(lower);
            if (m.Success)
                return new FileSystemIntent(FileSystemAction.ReadFile, ResolvePath(m.Groups["name"].Value.Trim()), null);

            // Listar pasta
            m = ListDirRegex.Match(lower);
            if (m.Success)
            {
                var rawPath = m.Groups["path"].Value.Trim();
                var dir = string.IsNullOrWhiteSpace(rawPath)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                    : ResolvePath(rawPath);
                return new FileSystemIntent(FileSystemAction.ListDir, dir, null);
            }

            // Abrir arquivo ou pasta
            m = OpenFileRegex.Match(lower);
            if (m.Success)
            {
                var rawPath = m.Groups["name"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(rawPath))
                    return new FileSystemIntent(FileSystemAction.OpenFile, ResolveOpenPath(rawPath), null);
            }

            // Executar comando no terminal
            m = TerminalCommandRegex.Match(lower);
            if (!m.Success)
                m = TerminalCommandAltRegex.Match(lower);
            if (m.Success)
            {
                var command = m.Groups["cmd"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(command))
                    return new FileSystemIntent(FileSystemAction.RunTerminalCommand, null, command);
            }

            // Abrir programa — busca fuzzy por tokens
            m = OpenProgramRegex.Match(lower);
            if (m.Success)
            {
                var name = NormalizeProgramName(m.Groups["name"].Value);
                var resolved = ResolveProgramAlias(name);
                if (resolved != null)
                    return new FileSystemIntent(FileSystemAction.OpenProgram, resolved, null);

                LogFile.AppendLine($"[FS] Comando de abrir programa nao reconhecido: '{name}'");
            }

            // Apagar arquivo
            m = DeleteFileRegex.Match(lower);
            if (m.Success)
                return new FileSystemIntent(FileSystemAction.DeleteFile, ResolvePath(m.Groups["name"].Value.Trim()), null);

            return new FileSystemIntent(FileSystemAction.None, null, null);
        }

        /// <summary>
        /// Executa uma ação estruturada retornada pelo LLM via bloco &lt;KATE_ACTION&gt;.
        /// type: open_program | open_file | terminal | create_file | append_file | read_file | list_dir | delete_file
        /// target: nome do programa/caminho do arquivo/pasta
        /// content: conteúdo (para create/append/terminal)
        /// </summary>
        public Task<string> ExecuteFromLlmActionAsync(string type, string? target, string? content)
        {
            LogFile.AppendLine($"[FS-LLM] Ação recebida: type={type} target={target} content={content?.Length} chars");
            try
            {
                return type.ToLowerInvariant() switch
                {
                    "open_program" => OpenProgramInternalAsync(
                        ResolveProgramAlias(NormalizeProgramName(target ?? string.Empty)) ?? target ?? string.Empty),
                    "open_file"    => OpenFileInternalAsync(ResolveOpenPath(target ?? string.Empty)),
                    "terminal"     => RunTerminalCommandInternalAsync(content ?? target ?? string.Empty),
                    "create_file"  => CreateFileInternalAsync(ResolvePath(target ?? string.Empty), content),
                    "append_file"  => AppendFileInternalAsync(ResolvePath(target ?? string.Empty), content ?? string.Empty),
                    "read_file"    => ReadFileInternalAsync(ResolvePath(target ?? string.Empty)),
                    "list_dir"     => ListDirInternalAsync(
                        string.IsNullOrWhiteSpace(target)
                            ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                            : ResolvePath(target)),
                    "delete_file"  => DeleteFileInternalAsync(ResolvePath(target ?? string.Empty)),
                    _ => Task.FromResult($"Tipo de ação LLM desconhecido: {type}")
                };
            }
            catch (Exception ex)
            {
                LogFile.AppendLine($"[FS-LLM] Erro ao executar ação {type}: {ex.Message}");
                return Task.FromResult($"Não consegui realizar a operação: {ex.Message}");
            }
        }

        public async Task<string> ExecuteAsync(FileSystemIntent intent, string originalPrompt)
        {
            try
            {
                return intent.Action switch
                {
                    FileSystemAction.CreateFile  => await CreateFileInternalAsync(intent.Path!, intent.Content),
                    FileSystemAction.AppendFile  => await AppendFileInternalAsync(intent.Path!, intent.Content ?? string.Empty),
                    FileSystemAction.OverwriteFile => await OverwriteFileInternalAsync(intent.Path!, intent.Content ?? string.Empty),
                    FileSystemAction.ReplaceInFile => await ReplaceInFileInternalAsync(intent.Path!, intent.Content ?? string.Empty),
                    FileSystemAction.ReadFile    => await ReadFileInternalAsync(intent.Path!),
                    FileSystemAction.ListDir     => await ListDirInternalAsync(intent.Path!),
                    FileSystemAction.DeleteFile  => await DeleteFileInternalAsync(intent.Path!),
                    FileSystemAction.OpenProgram => await OpenProgramInternalAsync(intent.Path!),
                    FileSystemAction.OpenFile    => await OpenFileInternalAsync(intent.Path!),
                    FileSystemAction.RunTerminalCommand => await RunTerminalCommandInternalAsync(intent.Content ?? originalPrompt),
                    _                            => "Não entendi a intenção de arquivo/programa."
                };
            }
            catch (Exception ex)
            {
                LogFile.AppendLine($"[FS] Erro ao executar ação {intent.Action}: {ex.Message}");
                return $"Não consegui realizar a operação: {ex.Message}";
            }
        }

        // ── Implementações internas ──────────────────────────────────────────

        private static async Task<string> CreateFileInternalAsync(string path, string? content)
        {
            EnsureAllowedPath(path);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var text = content ?? string.Empty;
            await File.WriteAllTextAsync(path, text, System.Text.Encoding.UTF8);
            LogFile.AppendLine($"[FS] Arquivo criado: {path} ({text.Length} chars)");
            return $"Arquivo criado: {path}";
        }

        private static async Task<string> AppendFileInternalAsync(string path, string content)
        {
            EnsureAllowedPath(path);
            await File.AppendAllTextAsync(path, content + Environment.NewLine, System.Text.Encoding.UTF8);
            LogFile.AppendLine($"[FS] Conteúdo adicionado ao arquivo: {path}");
            return $"Conteúdo adicionado ao arquivo {Path.GetFileName(path)}.";
        }

        private static async Task<string> OverwriteFileInternalAsync(string path, string content)
        {
            EnsureAllowedPath(path);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(path, content, System.Text.Encoding.UTF8);
            LogFile.AppendLine($"[FS] Arquivo sobrescrito: {path}");
            return $"Arquivo {Path.GetFileName(path)} atualizado com novo conteúdo.";
        }

        private static async Task<string> ReplaceInFileInternalAsync(string path, string payload)
        {
            EnsureAllowedPath(path);
            if (!File.Exists(path))
                return $"Arquivo não encontrado: {path}";

            var split = payload.Split("\n---\n", 2, StringSplitOptions.None);
            if (split.Length != 2)
                return "Formato inválido para substituição. Use: substituir no arquivo X de \"A\" para \"B\".";

            var oldValue = split[0];
            var newValue = split[1];
            var text = await File.ReadAllTextAsync(path, System.Text.Encoding.UTF8);

            if (!text.Contains(oldValue, StringComparison.Ordinal))
                return "Não encontrei o trecho informado para substituir.";

            var updated = text.Replace(oldValue, newValue, StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, updated, System.Text.Encoding.UTF8);
            LogFile.AppendLine($"[FS] Trecho substituído no arquivo: {path}");
            return $"Trecho substituído em {Path.GetFileName(path)}.";
        }

        private static async Task<string> ReadFileInternalAsync(string path)
        {
            EnsureAllowedPath(path);
            if (!File.Exists(path))
                return $"Arquivo não encontrado: {path}";

            var text = await File.ReadAllTextAsync(path, System.Text.Encoding.UTF8);
            if (text.Length > 4000) text = text[..4000] + "\n... (texto truncado)";
            LogFile.AppendLine($"[FS] Arquivo lido: {path} ({text.Length} chars)");
            return $"Conteúdo de {Path.GetFileName(path)}:\n{text}";
        }

        private static Task<string> ListDirInternalAsync(string path)
        {
            EnsureAllowedPath(path);
            if (!Directory.Exists(path))
                return Task.FromResult($"Pasta não encontrada: {path}");

            var dirs  = Directory.GetDirectories(path);
            var files = Directory.GetFiles(path);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Conteúdo de {path}:");
            foreach (var d in dirs)  sb.AppendLine($"  [pasta] {Path.GetFileName(d)}");
            foreach (var f in files) sb.AppendLine($"  [arquivo] {Path.GetFileName(f)}");
            return Task.FromResult(sb.ToString().TrimEnd());
        }

        private static Task<string> DeleteFileInternalAsync(string path)
        {
            EnsureAllowedPath(path);
            if (!File.Exists(path))
                return Task.FromResult($"Arquivo não encontrado: {path}");
            File.Delete(path);
            LogFile.AppendLine($"[FS] Arquivo apagado: {path}");
            return Task.FromResult($"Arquivo {Path.GetFileName(path)} apagado.");
        }

        private static async Task<string> OpenProgramInternalAsync(string nameOrPath)
        {
            if (string.IsNullOrWhiteSpace(nameOrPath))
                return "Não consegui abrir o programa porque o nome está vazio.";

            var expectedProcessName = ResolveExpectedProcessName(nameOrPath);
            var beforeCount = string.IsNullOrWhiteSpace(expectedProcessName)
                ? 0
                : SafeGetProcessCount(expectedProcessName!);

            var psi = new ProcessStartInfo
            {
                FileName        = nameOrPath,
                UseShellExecute = true,
            };

            var started = Process.Start(psi);

            // Em cenários com shell (UseShellExecute=true), Process pode vir null.
            // Então confirmamos por incremento do processo-alvo quando possível.
            var confirmed = started is not null;
            if (!confirmed && !string.IsNullOrWhiteSpace(expectedProcessName))
            {
                confirmed = await WaitForProcessIncreaseAsync(expectedProcessName!, beforeCount, timeoutMs: 4000);
            }

            if (!confirmed)
            {
                LogFile.AppendLine($"[FS] Falha ao confirmar abertura do programa: {nameOrPath}");
                return $"Não consegui confirmar a abertura de {nameOrPath}.";
            }

            LogFile.AppendLine($"[FS] Programa aberto e confirmado: {nameOrPath}");
            return $"Programa aberto com sucesso: {nameOrPath}.";
        }

        private static Task<string> OpenFileInternalAsync(string path)
        {
            EnsureAllowedPath(path);
            var psi = new ProcessStartInfo { FileName = path, UseShellExecute = true };
            Process.Start(psi);
            LogFile.AppendLine($"[FS] Arquivo aberto: {path}");
            return Task.FromResult($"Abrindo {Path.GetFileName(path)}.");
        }

        private static async Task<string> RunTerminalCommandInternalAsync(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return "Nao entendi qual comando voce quer executar.";

            if (IsBlockedTerminalCommand(command))
                return "Esse comando foi bloqueado por seguranca. Posso executar comandos de diagnostico e consulta.";

            var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
                WorkingDirectory = Directory.GetCurrentDirectory(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi };
            var cts = new CancellationTokenSource(TerminalCommandTimeout);

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                LogFile.AppendLine($"[FS-Terminal] Timeout: {command}");
                return "O comando demorou demais e foi interrompido (timeout de 20s).";
            }

            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();

            LogFile.AppendLine($"[FS-Terminal] Comando executado: {command} | ExitCode={process.ExitCode}");

            if (process.ExitCode != 0)
            {
                var errorText = string.IsNullOrWhiteSpace(stderr) ? "Comando retornou erro." : stderr;
                return $"Comando executado com erro (codigo {process.ExitCode}).\n{TruncateOutput(errorText)}";
            }

            if (string.IsNullOrWhiteSpace(stdout) && string.IsNullOrWhiteSpace(stderr))
                return "Comando executado sem saida.";

            var merged = string.IsNullOrWhiteSpace(stderr) ? stdout : stdout + "\n" + stderr;
            return "Resultado do comando:\n" + TruncateOutput(merged);
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static string ResolvePath(string name)
        {
            // Se já é um caminho absoluto, usa direto
            if (Path.IsPathRooted(name)) return name;

            // Adiciona extensão .txt se não tiver
            if (!Path.HasExtension(name)) name = name + ".txt";

            // Padrões de pasta especial
            if (name.StartsWith("desktop", StringComparison.OrdinalIgnoreCase) || name.StartsWith("area de trabalho", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), Path.GetFileName(name));

            if (name.StartsWith("documents", StringComparison.OrdinalIgnoreCase) || name.StartsWith("documentos", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Path.GetFileName(name));

            // Por padrão, Desktop
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), name);
        }

        private static string ResolveOpenPath(string name)
        {
            if (Path.IsPathRooted(name)) return name;

            if (name.StartsWith("desktop", StringComparison.OrdinalIgnoreCase) || name.StartsWith("area de trabalho", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), Path.GetFileName(name));

            if (name.StartsWith("documents", StringComparison.OrdinalIgnoreCase) || name.StartsWith("documentos", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Path.GetFileName(name));

            if (name.StartsWith("downloads", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), Path.GetFileName(name));

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), name);
        }

        private static string NormalizeProgramName(string name)
        {
            var normalized = name.Trim();
            // Remove artigos iniciais
            normalized = Regex.Replace(normalized, @"^(?:o|a|os|as|um|uma)\s+", string.Empty, RegexOptions.IgnoreCase);
            // Remove sufixos coloquiais
            normalized = Regex.Replace(normalized, @"\s+(?:pra\s+mim|para\s+mim|agora|ai|aí|logo|por\s+favor|please)$", string.Empty, RegexOptions.IgnoreCase);
            // Remove acentos para normalização
            return RemoveDiacritics(normalized.Trim());
        }

        /// <summary>
        /// Busca o alias do programa de forma fuzzy:
        /// 1. Lookup exato (após normalização);
        /// 2. Lookup token-a-token: verifica se algum token do pedido está contido numa chave de alias;
        /// 3. Lookup reverso: verifica se alguma chave está contida no pedido.
        /// </summary>
        private static string? ResolveProgramAlias(string normalizedName)
        {
            if (IsKnownExtension(normalizedName))
                return normalizedName;

            // 1. Exato
            if (ProgramAliases.TryGetValue(normalizedName, out var exact))
                return exact;

            var inputTokens = normalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // 2. Algum token do input é igual a uma chave exata (busca precisa para evitar falsos positivos)
            // Ex: "chrome" casa com a chave "chrome", mas "comando" NAO casa com "prompt de comando"
            foreach (var (key, exe) in ProgramAliases)
            {
                var normalizedKey = RemoveDiacritics(key);
                foreach (var token in inputTokens)
                {
                    if (token.Length >= 3 && token.Equals(normalizedKey, StringComparison.OrdinalIgnoreCase))
                        return exe;
                }
            }

            // 3. Alguma chave está contida no input
            foreach (var (key, exe) in ProgramAliases)
            {
                var normalizedKey = RemoveDiacritics(key);
                if (normalizedKey.Length >= 3 && normalizedName.Contains(normalizedKey, StringComparison.OrdinalIgnoreCase))
                    return exe;
            }

            return null;
        }

        private static string RemoveDiacritics(string text)
        {
            var normalized = text.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(normalized.Length);
            foreach (var c in normalized)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
        }

        private static bool IsBlockedTerminalCommand(string command)
        {
            var lower = command.ToLowerInvariant();
            foreach (var hint in BlockedCommandHints)
            {
                if (lower.Contains(hint, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static int SafeGetProcessCount(string processName)
        {
            try
            {
                return Process.GetProcessesByName(processName).Length;
            }
            catch
            {
                return 0;
            }
        }

        private static async Task<bool> WaitForProcessIncreaseAsync(string processName, int beforeCount, int timeoutMs)
        {
            var startedAt = DateTime.UtcNow;
            while ((DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
            {
                var nowCount = SafeGetProcessCount(processName);
                if (nowCount > beforeCount)
                    return true;

                await Task.Delay(200);
            }

            return false;
        }

        private static string? ResolveExpectedProcessName(string nameOrPath)
        {
            var value = nameOrPath.Trim();
            if (string.IsNullOrWhiteSpace(value))
                return null;

            // Esquemas de URI (ms-settings:, etc.) não têm nome de processo determinístico.
            if (value.Contains(':') && !value.Contains("\\") && !value.Contains("/"))
                return null;

            var file = Path.GetFileNameWithoutExtension(value);
            if (!string.IsNullOrWhiteSpace(file))
                return file;

            return null;
        }

        private static string TruncateOutput(string text, int maxChars = 3500)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
                return text;

            return text[..maxChars] + "\n... (saida truncada)";
        }

        private static void EnsureAllowedPath(string path)
        {
            var fullPath = Path.GetFullPath(path);
            foreach (var root in AllowedWriteRoots)
            {
                if (!string.IsNullOrWhiteSpace(root) && fullPath.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                    return;
            }
            throw new UnauthorizedAccessException($"Caminho não permitido: {path}. Use Desktop, Documentos ou Downloads.");
        }

        private static bool IsKnownExtension(string name) =>
            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
    }
}

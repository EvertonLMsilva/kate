using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
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
        private static readonly Dictionary<string, string> ProgramAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["notepad"]         = "notepad.exe",
            ["bloco de notas"]  = "notepad.exe",
            ["calculadora"]     = "calc.exe",
            ["calc"]            = "calc.exe",
            ["explorador"]      = "explorer.exe",
            ["explorer"]        = "explorer.exe",
            ["chrome"]          = "chrome",
            ["google chrome"]   = "chrome",
            ["firefox"]         = "firefox",
            ["mozilla"]         = "firefox",
            ["edge"]            = "msedge",
            ["microsoft edge"]  = "msedge",
            ["vscode"]          = "code",
            ["visual studio code"] = "code",
            ["word"]            = "winword",
            ["excel"]           = "excel",
            ["powerpoint"]      = "powerpnt",
            ["paint"]           = "mspaint",
            ["terminal"]        = "wt",
            ["cmd"]             = "cmd",
            ["powershell"]      = "pwsh",
            ["spotify"]         = "spotify",
            ["discord"]         = "discord",
            ["telegram"]        = "telegram",
            ["whatsapp"]        = "whatsapp",
            ["vlc"]             = "vlc",
        };

        // ── Padrões de detecção de intenção ─────────────────────────────────

        private static readonly Regex CreateFileRegex = new(
            @"(?:cri[ea]|escrev[ae]|salv[ae]|grav[ae])(?:\s+um)?(?:\s+novo)?\s+(?:arquivo|ficheiro|texto|doc)\s+(?:chamado\s+|com\s+(?:o\s+)?nome\s+)?[""']?(?<name>[\w\s.\-]+?)[""']?(?:\s+(?:com|contendo|de|no caminho)(?:\s+o\s+conteudo)?(?:\s+o\s+texto)?\s+[""']?(?<content>.+?)[""']?)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SaveContentRegex = new(
            @"(?:salv[ae]|grav[ae]|escrev[ae])\s+(?:isso|o\s+seguinte|este\s+texto|o\s+texto|o\s+conte[uú]do|tudo\s+isso)\s+(?:no|em\s+um)\s+arquivo\s+(?:chamado\s+|com\s+(?:o\s+)?nome\s+)?[""']?(?<name>[\w\s.\-]+?)[""']?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AppendRegex = new(
            @"(?:adicione?|acrescente?|inclua?|insira?)\s+(?:no\s+arquivo|ao\s+arquivo|no\s+final\s+do\s+arquivo)\s+[""']?(?<name>[\w\s.\-]+?)[""']?\s*[:|,]?\s*(?<content>.+)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex OverwriteRegex = new(
            @"(?:sobrescrev[ae]|substitu[ae]\s+todo\s+o\s+conte[uú]do\s+do\s+arquivo|reescrev[ae]|atualiz[ae]\s+o\s+arquivo)\s+[""']?(?<name>[\w\s.\-/\\:]+?)[""']?\s+(?:com|para)\s+[""']?(?<content>.+?)[""']?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReplaceInFileRegex = new(
            @"(?:substitu[ae]|troqu[ae]|alter[ae])\s+(?:no\s+arquivo\s+)?[""']?(?<name>[\w\s.\-/\\:]+?)[""']?\s+(?:de|o\s+trecho)\s+[""'](?<old>.+?)[""']\s+(?:para|por)\s+[""'](?<new>.+?)[""']\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReadFileRegex = new(
            @"(?:l[êe]ia?|mostr[ae]|exib[ae]|abra?\s+e\s+l[êe]ia?)\s+(?:o\s+arquivo|a\s+arquivo|o\s+conte[uú]do\s+do)\s+[""']?(?<name>[\w\s.\-/\\:]+?)[""']?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ListDirRegex = new(
            @"(?:list[ae]|mostr[ae]|exib[ae])\s+(?:os\s+arquivos|o\s+conte[uú]do|a\s+pasta)\s+(?:de\s+|do?\s+|da\s+)?[""']?(?<path>[\w\s.\-/\\:~]*)[""']?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex OpenProgramRegex = new(
            @"(?:abr[ae]|inicie?|execut[ae]|ligue?)\s+(?:o\s+|a\s+|o\s+programa\s+|o\s+aplicativo\s+)?[""']?(?<name>[\w\s.\-]+?)[""']?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DeleteFileRegex = new(
            @"(?:apague?|delete?|remov[ae]|exclu[ia])\s+(?:o\s+)?(?:arquivo\s+)?[""']?(?<name>[\w\s.\-/\\:]+?)[""']?\s*$",
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

            // Abrir programa
            m = OpenProgramRegex.Match(lower);
            if (m.Success)
            {
                var name = m.Groups["name"].Value.Trim();
                if (ProgramAliases.TryGetValue(name, out var exe) || IsKnownExtension(name))
                    return new FileSystemIntent(FileSystemAction.OpenProgram, ProgramAliases.TryGetValue(name, out var e2) ? e2 : name, null);
            }

            // Apagar arquivo
            m = DeleteFileRegex.Match(lower);
            if (m.Success)
                return new FileSystemIntent(FileSystemAction.DeleteFile, ResolvePath(m.Groups["name"].Value.Trim()), null);

            return new FileSystemIntent(FileSystemAction.None, null, null);
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

        private static Task<string> OpenProgramInternalAsync(string nameOrPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName        = nameOrPath,
                UseShellExecute = true,
            };
            Process.Start(psi);
            LogFile.AppendLine($"[FS] Programa aberto: {nameOrPath}");
            return Task.FromResult($"Abrindo {nameOrPath}.");
        }

        private static Task<string> OpenFileInternalAsync(string path)
        {
            var psi = new ProcessStartInfo { FileName = path, UseShellExecute = true };
            Process.Start(psi);
            LogFile.AppendLine($"[FS] Arquivo aberto: {path}");
            return Task.FromResult($"Abrindo {Path.GetFileName(path)}.");
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

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using model_kate.Domain;
using model_kate.Infrastructure.Diagnostics;

namespace model_kate.Infrastructure
{
    public sealed class CodeExecutionService : ICodeExecutionService
    {
        private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(15);

        // Regex para extrair bloco de código markdown (```lang\n...\n```)
        private static readonly Regex CodeBlockRegex = new Regex(
            @"```(?<lang>[a-zA-Z0-9#+-]*)\r?\n(?<code>[\s\S]*?)```",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public async Task<CodeExecutionResult> ExecuteAsync(string code, string language)
        {
            var normalizedLang = NormalizeLanguage(language);
            LogFile.AppendLine($"[Code] Executando {normalizedLang}. Tamanho: {code.Length} chars.");

            var start = Stopwatch.StartNew();
            try
            {
                var result = normalizedLang switch
                {
                    "csharp" => await ExecuteCSharpAsync(code),
                    "python" => await ExecuteScriptAsync(code, ".py", DetectPythonExecutable(), ""),
                    "powershell" => await ExecuteScriptAsync(code, ".ps1", "powershell", "-NoProfile -ExecutionPolicy Bypass -File"),
                    _ => new CodeExecutionResult(false, string.Empty, $"Linguagem '{language}' não suportada para execução.", normalizedLang, TimeSpan.Zero)
                };

                start.Stop();
                LogFile.AppendLine($"[Code] Execução concluída em {start.Elapsed.TotalSeconds:F1}s. Sucesso: {result.Success}");
                return result with { Duration = start.Elapsed };
            }
            catch (Exception ex)
            {
                start.Stop();
                LogFile.AppendLine($"[Code] Erro inesperado: {ex.Message}");
                return new CodeExecutionResult(false, string.Empty, ex.Message, normalizedLang, start.Elapsed);
            }
        }

        public string DetectLanguage(string codeOrResponse)
        {
            var match = CodeBlockRegex.Match(codeOrResponse);
            if (match.Success && !string.IsNullOrWhiteSpace(match.Groups["lang"].Value))
            {
                return NormalizeLanguage(match.Groups["lang"].Value);
            }

            // Heurística por conteúdo
            var lower = codeOrResponse.ToLowerInvariant();
            if (lower.Contains("console.writeline") || lower.Contains("using system") || lower.Contains("namespace "))
                return "csharp";
            if (lower.Contains("def ") || lower.Contains("import ") || lower.Contains("print("))
                return "python";
            if (lower.Contains("write-host") || lower.Contains("get-") || lower.Contains("$env:"))
                return "powershell";

            return "csharp"; // padrão
        }

        public string? ExtractCodeBlock(string response, out string language)
        {
            var match = CodeBlockRegex.Match(response);
            if (match.Success)
            {
                language = NormalizeLanguage(match.Groups["lang"].Value);
                return match.Groups["code"].Value.Trim();
            }

            language = "csharp";
            return null;
        }

        // ---- Execução C# via projeto temporário ----

        private static async Task<CodeExecutionResult> ExecuteCSharpAsync(string code)
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), "kate_csharp_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tmpDir);

            try
            {
                // Projeto mínimo
                var csproj = """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <TargetFramework>net8.0</TargetFramework>
                        <Nullable>enable</Nullable>
                        <ImplicitUsings>enable</ImplicitUsings>
                      </PropertyGroup>
                    </Project>
                    """;

                await File.WriteAllTextAsync(Path.Combine(tmpDir, "Kate_Temp.csproj"), csproj);
                await File.WriteAllTextAsync(Path.Combine(tmpDir, "Program.cs"), code, Encoding.UTF8);

                var (buildOut, buildErr, buildExitCode) = await RunProcessAsync("dotnet", $"build \"{tmpDir}\" -c Release --nologo -v q", tmpDir);

                if (buildExitCode != 0)
                {
                    return new CodeExecutionResult(false, string.Empty,
                        $"Erro de compilação:\n{buildErr}\n{buildOut}", "csharp", TimeSpan.Zero);
                }

                var (runOut, runErr, runCode) = await RunProcessAsync("dotnet", $"run --project \"{tmpDir}\" -c Release --no-build", tmpDir);
                var output = runOut.Trim();
                var error = runErr.Trim();

                return new CodeExecutionResult(runCode == 0, output, string.IsNullOrWhiteSpace(error) ? null : error, "csharp", TimeSpan.Zero);
            }
            finally
            {
                try { Directory.Delete(tmpDir, recursive: true); } catch { }
            }
        }

        // ---- Execução de script (Python / PowerShell) ----

        private static async Task<CodeExecutionResult> ExecuteScriptAsync(string code, string extension, string executable, string args)
        {
            var tmpFile = Path.Combine(Path.GetTempPath(), "kate_script_" + Guid.NewGuid().ToString("N")[..8] + extension);
            try
            {
                await File.WriteAllTextAsync(tmpFile, code, Encoding.UTF8);
                var fullArgs = string.IsNullOrWhiteSpace(args) ? $"\"{tmpFile}\"" : $"{args} \"{tmpFile}\"";
                var (output, error, exitCode) = await RunProcessAsync(executable, fullArgs, Path.GetTempPath());
                return new CodeExecutionResult(exitCode == 0, output.Trim(), string.IsNullOrWhiteSpace(error) ? null : error.Trim(), extension.TrimStart('.'), TimeSpan.Zero);
            }
            finally
            {
                try { File.Delete(tmpFile); } catch { }
            }
        }

        // ---- Utilitários ----

        private static async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(string executable, string arguments, string workingDir)
        {
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi };
            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var exited = await Task.Run(() => process.WaitForExit((int)ExecutionTimeout.TotalMilliseconds));
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (stdoutBuilder.ToString(), "Tempo limite de execução excedido.", -1);
            }

            return (stdoutBuilder.ToString(), stderrBuilder.ToString(), process.ExitCode);
        }

        private static string NormalizeLanguage(string lang)
        {
            return lang.ToLowerInvariant() switch
            {
                "cs" or "c#" or "csharp" => "csharp",
                "py" or "python" or "python3" => "python",
                "ps" or "ps1" or "powershell" or "pwsh" => "powershell",
                _ => lang.ToLowerInvariant()
            };
        }

        private static string DetectPythonExecutable()
        {
            foreach (var candidate in new[] { "python", "python3", "py" })
            {
                try
                {
                    using var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = candidate,
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    p?.WaitForExit(2000);
                    if (p?.ExitCode == 0) return candidate;
                }
                catch { }
            }
            return "python";
        }
    }
}

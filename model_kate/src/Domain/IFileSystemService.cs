using System.Threading.Tasks;

namespace model_kate.Domain
{
    public enum FileSystemAction
    {
        None,
        CreateFile,
        AppendFile,
        OverwriteFile,
        ReplaceInFile,
        ReadFile,
        DeleteFile,
        ListDir,
        OpenProgram,
        OpenFile
    }

    public record FileSystemIntent(FileSystemAction Action, string? Path, string? Content);

    public interface IFileSystemService
    {
        /// <summary>Tenta detectar uma intenção de arquivo/programa na fala do usuário.</summary>
        FileSystemIntent DetectIntent(string prompt);

        /// <summary>Executa a intenção detectada e retorna uma resposta em linguagem natural.</summary>
        Task<string> ExecuteAsync(FileSystemIntent intent, string originalPrompt);
    }
}

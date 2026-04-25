namespace model_kate.Domain
{
    public interface IWebBrowsingService
    {
        /// <summary>Pesquisa na internet e retorna texto com os resultados.</summary>
        Task<string> SearchWebAsync(string query);

        /// <summary>Faz download do conteúdo de uma página e retorna o texto sem HTML.</summary>
        Task<string> FetchPageTextAsync(string url);

        /// <summary>Abre uma URL no navegador padrão do sistema.</summary>
        void OpenInBrowser(string url);
    }
}

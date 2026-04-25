using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using model_kate.Domain;
using model_kate.Infrastructure.Diagnostics;

namespace model_kate.Infrastructure
{
    public sealed class WebBrowsingService : IWebBrowsingService
    {
        private static readonly HttpClient _httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        })
        {
            Timeout = TimeSpan.FromSeconds(10),
            DefaultRequestHeaders =
            {
                { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36" },
                { "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8" },
                { "Accept-Language", "pt-BR,pt;q=0.9,en;q=0.5" }
            }
        };

        private const int MaxPageChars = 2500;
        private const int MaxSearchResults = 5;

        public async Task<string> SearchWebAsync(string query)
        {
            try
            {
                var result = await SearchInstantAnswerAsync(query);
                if (!string.IsNullOrWhiteSpace(result) && result.Length > 60)
                {
                    return result;
                }

                return await SearchHtmlAsync(query);
            }
            catch (Exception ex)
            {
                LogFile.AppendLine($"[Web] Erro na busca: {ex.GetType().Name} - {ex.Message}");
                return string.Empty;
            }
        }

        public async Task<string> FetchPageTextAsync(string url)
        {
            try
            {
                var html = await _httpClient.GetStringAsync(url);
                return StripHtml(html, MaxPageChars);
            }
            catch (Exception ex)
            {
                LogFile.AppendLine($"[Web] Erro ao buscar página: {ex.GetType().Name} - {ex.Message}");
                return string.Empty;
            }
        }

        public void OpenInBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                LogFile.AppendLine($"[Web] Abrindo no navegador: {url}");
            }
            catch (Exception ex)
            {
                LogFile.AppendLine($"[Web] Erro ao abrir navegador: {ex.Message}");
            }
        }

        // --- DuckDuckGo Instant Answer API ---

        private static async Task<string> SearchInstantAnswerAsync(string query)
        {
            var url = $"https://api.duckduckgo.com/?q={Uri.EscapeDataString(query)}&format=json&no_html=1&skip_disambig=1";
            var json = await _httpClient.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var sb = new StringBuilder();

            // Quick answer (calculator, conversions, etc.)
            if (root.TryGetProperty("Answer", out var answerEl))
            {
                var answer = answerEl.GetString();
                if (!string.IsNullOrWhiteSpace(answer))
                {
                    sb.AppendLine($"Resposta direta: {answer}");
                }
            }

            // Abstract (Wikipedia-style)
            if (root.TryGetProperty("AbstractText", out var abstractEl))
            {
                var text = abstractEl.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var source = root.TryGetProperty("AbstractSource", out var srcEl) ? srcEl.GetString() : "";
                    if (!string.IsNullOrWhiteSpace(source))
                        sb.AppendLine($"Fonte: {source}");
                    sb.AppendLine(text);
                }
            }

            // Related topics
            if (root.TryGetProperty("RelatedTopics", out var topicsEl) && topicsEl.ValueKind == JsonValueKind.Array)
            {
                var count = 0;
                foreach (var topic in topicsEl.EnumerateArray())
                {
                    if (count >= MaxSearchResults) break;

                    if (topic.TryGetProperty("Text", out var textEl))
                    {
                        var text = textEl.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            sb.AppendLine($"- {text}");
                            count++;
                        }
                    }
                    // Grouped topics
                    else if (topic.TryGetProperty("Topics", out var subTopics) && subTopics.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var sub in subTopics.EnumerateArray())
                        {
                            if (count >= MaxSearchResults) break;
                            if (sub.TryGetProperty("Text", out var subText))
                            {
                                var text = subText.GetString();
                                if (!string.IsNullOrWhiteSpace(text))
                                {
                                    sb.AppendLine($"- {text}");
                                    count++;
                                }
                            }
                        }
                    }
                }
            }

            return sb.ToString().Trim();
        }

        // --- DuckDuckGo HTML search (fallback) ---

        private static async Task<string> SearchHtmlAsync(string query)
        {
            var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}&kl=br-pt";
            var html = await _httpClient.GetStringAsync(url);

            var titles = Regex.Matches(html,
                @"class=""result__a""[^>]*>(.+?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            var snippets = Regex.Matches(html,
                @"class=""result__snippet""[^>]*>(.+?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            var sb = new StringBuilder();
            var count = Math.Min(MaxSearchResults, Math.Min(titles.Count, snippets.Count));

            for (var i = 0; i < count; i++)
            {
                var title = WebUtility.HtmlDecode(StripInlineTags(titles[i].Groups[1].Value)).Trim();
                var snippet = WebUtility.HtmlDecode(StripInlineTags(snippets[i].Groups[1].Value)).Trim();

                if (!string.IsNullOrWhiteSpace(title))
                    sb.AppendLine($"**{title}**");
                if (!string.IsNullOrWhiteSpace(snippet))
                    sb.AppendLine(snippet);
                sb.AppendLine();
            }

            return sb.ToString().Trim();
        }

        // --- HTML stripping ---

        private static string StripHtml(string html, int maxChars)
        {
            // Remove <script>, <style>, <head> blocks
            var text = Regex.Replace(html, @"<(script|style|head|noscript)[^>]*>.*?</\1>",
                string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // Remove remaining tags
            text = StripInlineTags(text);

            // Decode HTML entities
            text = WebUtility.HtmlDecode(text);

            // Collapse whitespace
            text = Regex.Replace(text, @"[ \t]{2,}", " ");
            text = Regex.Replace(text, @"(\r?\n){3,}", "\n\n");
            text = text.Trim();

            if (text.Length > maxChars)
                text = text[..maxChars] + "...";

            return text;
        }

        private static string StripInlineTags(string html)
        {
            return Regex.Replace(html, @"<[^>]+>", " ");
        }
    }
}

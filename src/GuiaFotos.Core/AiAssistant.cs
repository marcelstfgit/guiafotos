using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GuiaFotos.Core;

public sealed class AiAssistant(ISecrets secrets, OpenAiLogin login, HttpMessageHandler? handler = null)
{
    public static void Validate(AiPlan plan, int count)
    {
        var supported = new HashSet<string> { "enhance", "restore", "rotate", "crop", "strip_exif", "metadata", "rate", "tag", "album", "rename_by_date", "find_duplicates", "backup" };
        if (plan.Actions.Count is 0 or > 30) throw new InvalidDataException("Plano deve ter entre 1 e 30 ações.");
        foreach (var action in plan.Actions)
        {
            if (!supported.Contains(action.Operation) || action.Items.Length == 0 || action.Items.Any(i => i < 0 || i >= count)) throw new InvalidDataException("A IA propôs uma ação ou seleção inválida.");
            if (action.Arguments?.Any(x => x.Key.Length > 100 || x.Value.Length > 16000) == true) throw new InvalidDataException("Argumento inválido no plano.");
            var args = action.Arguments ?? new();
            var allowed = action.Operation switch { "rotate" => new[] { "degrees" }, "crop" => new[] { "geometry" }, "strip_exif" => new[] { "allPrivate" }, "rate" => new[] { "rating" }, "tag" => new[] { "tags" }, "album" => new[] { "name" }, "metadata" => args.Keys.ToArray(), _ => Array.Empty<string>() };
            if (args.Keys.Any(k => !allowed.Contains(k))) throw new InvalidDataException("Argumento inesperado para " + action.Operation);
            if (action.Operation == "rotate" && args.GetValueOrDefault("degrees") is not ("90" or "180" or "270")) throw new InvalidDataException("Rotação inválida.");
            if (action.Operation == "crop" && !System.Text.RegularExpressions.Regex.IsMatch(args.GetValueOrDefault("geometry") ?? "", "^[1-9][0-9]{0,5}x[1-9][0-9]{0,5}\\+[0-9]{1,6}\\+[0-9]{1,6}$")) throw new InvalidDataException("Recorte inválido.");
            if (action.Operation == "rate" && (!int.TryParse(args.GetValueOrDefault("rating"), out var rating) || rating is < 0 or > 5)) throw new InvalidDataException("Avaliação inválida.");
            if (action.Operation == "strip_exif" && args.TryGetValue("allPrivate", out var privacy) && privacy is not ("true" or "false")) throw new InvalidDataException("Opção allPrivate inválida.");
            if (action.Operation == "metadata") PhotoEditor.ValidateMetadata(args);
        }
    }
    private static string Prompt(string instruction, IReadOnlyList<MediaItem> items) => "Você é o assistente do GuiaFotos. Responda SOMENTE JSON, sem Markdown. Nunca invente resultados; apenas planeje ações executáveis. " +
        "Schema: {\"explanation\":\"texto\",\"actions\":[{\"operation\":\"enhance\",\"items\":[0],\"arguments\":{}}]}. " +
        "Operações: enhance, restore (restauração visual local, não recria arquivos apagados), rotate (degrees 90/180/270), crop (geometry WxH+X+Y), strip_exif (allPrivate true/false), metadata (tag ExifTool: valor), rate (rating 0..5), tag (tags separadas por vírgula), album (name), rename_by_date, find_duplicates, backup. " +
        "Nunca execute shell, código, URLs ou exclua arquivos. As edições geram cópias. Índices: " + Json.Write(items.Select((x, i) => new { Index = i, x.Name, x.Kind, x.Taken, x.Tags, x.Rating })) + "\nInstrução: " + instruction;
    public async Task<AiPlan> Plan(string provider, string model, string instruction, IReadOnlyList<MediaItem> items, CancellationToken token = default)
    {
        if (provider is not ("OpenAI" or "Claude" or "ChatGPT")) throw new ArgumentException("Provedor desconhecido.");
        if (items.Count is 0 or > 100) throw new ArgumentException("Selecione de 1 a 100 itens por instrução.");
        if (model.Length == 0 || instruction.Length == 0) throw new ArgumentException("Informe modelo e instrução.");
        using var client = new HttpClient(handler ?? new HttpClientHandler(), handler == null) { Timeout = TimeSpan.FromMinutes(3) };
        var prompt = Prompt(instruction, items); HttpRequestMessage request; bool oauth = provider == "ChatGPT";
        if (provider == "Claude")
        {
            var key = secrets.Get("claude-key") ?? throw new UnauthorizedAccessException("Configure a chave Claude.");
            request = new(HttpMethod.Post, "https://api.anthropic.com/v1/messages"); request.Headers.Add("x-api-key", key); request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = new StringContent(Json.Write(new { model, max_tokens = 2500, messages = new[] { new { role = "user", content = prompt } } }), Encoding.UTF8, "application/json");
        }
        else
        {
            var key = oauth ? await login.Token(token) : secrets.Get("openai-key") ?? throw new UnauthorizedAccessException("Configure a chave OpenAI ou entre com ChatGPT.");
            request = new(HttpMethod.Post, "https://api.openai.com/v1/responses"); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var schema = new { type = "object", additionalProperties = false, required = new[] { "explanation", "actions" }, properties = new {
                explanation = new { type = "string" }, actions = new { type = "array", items = new { type = "object", additionalProperties = false, required = new[] { "operation", "items", "arguments" }, properties = new {
                    operation = new { type = "string" }, items = new { type = "array", items = new { type = "integer" } }, arguments = new { type = "object", additionalProperties = new { type = "string" } }
                } } } } };
            request.Content = new StringContent(Json.Write(new { model, input = prompt, max_output_tokens = 2500, store = false, stream = oauth,
                text = new { format = new { type = "json_schema", name = "guiafotos_plan", strict = false, schema } } }), Encoding.UTF8, "application/json");
        }
        using (request) using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token))
        {
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"A API recusou a solicitação ({(int)response.StatusCode}). Confira credenciais, modelo e saldo.");
            string text;
            if (oauth)
            {
                text = await ReadResponsesStream(await response.Content.ReadAsStreamAsync(token), token);
            }
            else
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)); var root = document.RootElement;
                if (provider == "Claude")
                { if (root.TryGetProperty("stop_reason", out var reason) && reason.GetString() == "max_tokens") throw new IOException("Resposta da IA truncada.");
                    text = string.Concat(root.GetProperty("content").EnumerateArray().Where(x => x.GetProperty("type").GetString() == "text").Select(x => x.GetProperty("text").GetString())); }
                else
                { if (root.TryGetProperty("status", out var status) && status.GetString() != "completed") throw new IOException("Resposta da IA não concluída.");
                    text = string.Concat(root.GetProperty("output").EnumerateArray().Where(x => x.GetProperty("type").GetString() == "message").SelectMany(x => x.GetProperty("content").EnumerateArray()).Where(x => x.GetProperty("type").GetString() == "output_text").Select(x => x.GetProperty("text").GetString())); }
            }
            var plan = Json.Read<AiPlan>(text); Validate(plan, items.Count); return plan;
        }
    }
    public static async Task<string> ReadResponsesStream(Stream stream, CancellationToken token = default)
    {
        using var reader = new StreamReader(stream); var output = new StringBuilder(); var data = new StringBuilder(); bool completed = false;
        void Event()
        {
            var payload = data.ToString().Trim(); data.Clear(); if (payload.Length == 0 || payload == "[DONE]") return;
            using var document = JsonDocument.Parse(payload); var evt = document.RootElement; var type = evt.GetProperty("type").GetString();
            if (type == "response.output_text.delta") output.Append(evt.GetProperty("delta").GetString());
            if (type == "response.completed") completed = true;
            if (type is "response.failed" or "response.incomplete" or "error") throw new IOException("A IA não concluiu a resposta. Verifique limites, permissão e saldo da conta.");
        }
        while (await reader.ReadLineAsync(token) is { } line)
        { if (line.Length == 0) Event(); else if (line.StartsWith("data:")) data.AppendLine(line[5..].TrimStart(' ')); }
        Event(); if (!completed) throw new IOException("Resposta da IA interrompida antes da conclusão."); return output.ToString();
    }
    public async Task<List<string>> Models(string provider, CancellationToken token = default)
    {
        using var http = new HttpClient();
        if (provider == "Claude") { http.DefaultRequestHeaders.Add("x-api-key", secrets.Get("claude-key") ?? throw new UnauthorizedAccessException("Configure chave Claude.")); http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01"); }
        else http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", provider == "ChatGPT" ? await login.Token(token) : secrets.Get("openai-key") ?? throw new UnauthorizedAccessException("Configure chave OpenAI."));
        using var response = await http.GetAsync(provider == "Claude" ? "https://api.anthropic.com/v1/models" : "https://api.openai.com/v1/models", token); response.EnsureSuccessStatusCode();
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (data.RootElement.TryGetProperty("models", out var models)) return models.EnumerateArray().Where(x => x.GetProperty("visibility").GetString() == "list").Select(x => x.GetProperty("slug").GetString()!).ToList();
        return data.RootElement.GetProperty("data").EnumerateArray().Select(x => x.GetProperty("id").GetString()!).Order().ToList();
    }
}

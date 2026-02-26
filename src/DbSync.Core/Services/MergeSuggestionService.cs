using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DbSync.Core.Models;

namespace DbSync.Core.Services;

/// <summary>
/// Llama a la API de Claude para sugerir un merge entre dos versiones de un objeto SQL Server.
/// </summary>
public class MergeSuggestionService
{
    private readonly HttpClient _httpClient;
    private readonly ClaudeSettings _settings;
    private readonly ILogger<MergeSuggestionService> _logger;

    public MergeSuggestionService(
        HttpClient httpClient,
        IOptions<ClaudeSettings> settings,
        ILogger<MergeSuggestionService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public bool IsEnabled => _settings.Enabled && !string.IsNullOrWhiteSpace(_settings.ApiKey);

    /// <summary>
    /// Envía ambas definiciones a Claude y retorna una sugerencia de merge.
    /// </summary>
    public async Task<MergeSuggestionResult> SuggestMergeAsync(
        string objectFullName,
        string sourceDefinition,
        string targetDefinition,
        string origenLabel,
        string destinoLabel,
        CancellationToken ct = default)
    {
        if (!IsEnabled)
            return MergeSuggestionResult.Fail(
                "La funcionalidad de Sugerir Merge no está habilitada. Configure la API key de Claude en appsettings.json.");

        try
        {
            var systemPrompt = BuildSystemPrompt();
            var userPrompt = BuildUserPrompt(objectFullName, sourceDefinition, targetDefinition, origenLabel, destinoLabel);

            var requestBody = new
            {
                model = _settings.Model,
                max_tokens = _settings.MaxTokens,
                system = systemPrompt,
                messages = new[]
                {
                    new { role = "user", content = userPrompt }
                }
            };

            var json = JsonSerializer.Serialize(requestBody, JsonOpts);

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_settings.BaseUrl}/v1/messages");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            request.Headers.Add("x-api-key", _settings.ApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");

            var response = await _httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Claude API error {StatusCode}: {Body}",
                    response.StatusCode, responseBody);

                // Intentar extraer el mensaje de error real de la respuesta
                var errorDetail = "";
                try
                {
                    using var doc = JsonDocument.Parse(responseBody);
                    if (doc.RootElement.TryGetProperty("error", out var errorObj) &&
                        errorObj.TryGetProperty("message", out var msgProp))
                    {
                        errorDetail = msgProp.GetString() ?? "";
                    }
                }
                catch { /* Si no se puede parsear, usar el body crudo */ }

                var displayMessage = !string.IsNullOrWhiteSpace(errorDetail)
                    ? $"Error de la API de Claude ({response.StatusCode}): {errorDetail}"
                    : $"Error de la API de Claude ({response.StatusCode}): {responseBody}";

                return MergeSuggestionResult.Fail(displayMessage);
            }

            var claudeResponse = JsonSerializer.Deserialize<ClaudeApiResponse>(responseBody, JsonOpts);

            var textContent = claudeResponse?.Content?
                .FirstOrDefault(c => c.Type == "text")?.Text;

            if (string.IsNullOrWhiteSpace(textContent))
                return MergeSuggestionResult.Fail("La respuesta de Claude no contiene texto.");

            return new MergeSuggestionResult
            {
                Success = true,
                MergedDefinition = ExtractSqlFromResponse(textContent),
                Explanation = ExtractExplanationFromResponse(textContent),
                Model = claudeResponse?.Model ?? _settings.Model,
                InputTokens = claudeResponse?.Usage?.InputTokens ?? 0,
                OutputTokens = claudeResponse?.Usage?.OutputTokens ?? 0
            };
        }
        catch (TaskCanceledException)
        {
            return MergeSuggestionResult.Fail("La solicitud fue cancelada (timeout).");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error de conexión con Claude API");
            return MergeSuggestionResult.Fail($"Error de conexión: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inesperado en SuggestMergeAsync");
            return MergeSuggestionResult.Fail($"Error inesperado: {ex.Message}");
        }
    }

    private string BuildSystemPrompt()
    {
        if (!string.IsNullOrWhiteSpace(_settings.SystemPrompt))
        {
            if (_settings.SystemPrompt.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                var filePath = _settings.SystemPrompt[5..].Trim();
                if (File.Exists(filePath))
                    return File.ReadAllText(filePath);
            }
            else
            {
                return _settings.SystemPrompt;
            }
        }

        return DefaultSystemPrompt;
    }

    private static string BuildUserPrompt(
        string objectFullName,
        string sourceDefinition,
        string targetDefinition,
        string origenLabel,
        string destinoLabel)
    {
        return $"""
            Necesito un merge de las dos versiones del siguiente objeto SQL Server:

            **Objeto:** `{objectFullName}`

            **Ambiente Origen ({origenLabel}):**
            ```sql
            {sourceDefinition}
            ```

            **Ambiente Destino ({destinoLabel}):**
            ```sql
            {targetDefinition}
            ```

            Por favor, genera una version mergeada que combine los cambios de ambas versiones.
            """;
    }

    private static string ExtractSqlFromResponse(string response)
    {
        // Buscar bloque ```sql
        var sqlBlockStart = response.IndexOf("```sql", StringComparison.OrdinalIgnoreCase);
        if (sqlBlockStart >= 0)
        {
            var codeStart = response.IndexOf('\n', sqlBlockStart) + 1;
            var codeEnd = response.IndexOf("```", codeStart);
            if (codeEnd > codeStart)
                return response[codeStart..codeEnd].Trim();
        }

        // Buscar bloque ``` genérico
        var blockStart = response.IndexOf("```");
        if (blockStart >= 0)
        {
            var codeStart = response.IndexOf('\n', blockStart) + 1;
            var codeEnd = response.IndexOf("```", codeStart);
            if (codeEnd > codeStart)
                return response[codeStart..codeEnd].Trim();
        }

        // Sin bloque de código — retornar todo
        return response.Trim();
    }

    private static string ExtractExplanationFromResponse(string response)
    {
        var parts = new List<string>();

        var firstBlock = response.IndexOf("```");
        if (firstBlock > 0)
            parts.Add(response[..firstBlock].Trim());

        var lastBlockEnd = response.LastIndexOf("```");
        if (lastBlockEnd >= 0)
        {
            var afterBlock = lastBlockEnd + 3;
            if (afterBlock < response.Length)
                parts.Add(response[afterBlock..].Trim());
        }

        var result = string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return string.IsNullOrWhiteSpace(result) && !response.Contains("```")
            ? "" // Si no hay code blocks y todo es SQL, no hay explicación
            : result;
    }

    // --- JSON options ---
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // --- System prompt por defecto ---
    private const string DefaultSystemPrompt = """
        Sos un experto en SQL Server, especializado en stored procedures, views y functions
        para sistemas de salud (clinicas y centros medicos).

        Tu tarea es generar un MERGE inteligente de dos versiones de un objeto SQL Server.

        ## Reglas de Merge

        1. **Preservar funcionalidad de ambas versiones**: Si una version agrega logica nueva
           que no existe en la otra, incluirla en el merge.
        2. **Resolver conflictos de forma conservadora**: Si ambas versiones modifican la misma
           seccion de forma diferente, incluir la version mas completa o robusta. Agregar un
           comentario indicando el conflicto.
        3. **Mantener el estilo del codigo original**: Respetar indentacion, convenciones de
           nombres, idioma de los comentarios (generalmente espanol).
        4. **Usar CREATE OR ALTER**: El resultado siempre debe comenzar con CREATE OR ALTER.
        5. **No perder parametros**: Si una version agrega un parametro nuevo, incluirlo
           (con valor default si la otra version no lo tiene).

        ## Anotaciones Especiales en el Codigo

        Busca y respeta estos comentarios especiales que el desarrollador puede dejar en el SP:

        - `-- @AI-CONTEXT: <texto>` — Contexto de negocio que debes considerar al mergear.
          Ejemplo: `-- @AI-CONTEXT: Este bloque calcula el copago segun obra social, no modificar la formula`

        - `-- @MERGE-RULE: <regla>` — Instruccion explicita para el merge.
          Ejemplo: `-- @MERGE-RULE: Siempre mantener la version de produccion de este bloque`

        - `-- @NO-MERGE: ORIGEN|DESTINO` — No mergear este bloque; mantener la version del
          ambiente indicado.
          Ejemplo: `-- @NO-MERGE: DESTINO`

        - `-- @DEPRECATED` — Este bloque esta deprecado y puede eliminarse en el merge.

        ## Formato de Respuesta

        1. Primero, una explicacion breve (2-5 oraciones) de que hiciste y por que.
        2. Luego, el SQL mergeado completo dentro de un bloque ```sql ... ```.
        3. Si encontraste conflictos que no pudiste resolver automaticamente, listalos al final
           con recomendaciones.

        IMPORTANTE: Responder siempre en espanol.
        """;

    // --- DTOs para respuesta de Claude API ---
    private class ClaudeApiResponse
    {
        public string? Model { get; set; }
        public List<ContentBlock>? Content { get; set; }
        public UsageInfo? Usage { get; set; }
    }

    private class ContentBlock
    {
        public string Type { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }

    private class UsageInfo
    {
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
    }
}

/// <summary>
/// Resultado de una sugerencia de merge de Claude.
/// </summary>
public class MergeSuggestionResult
{
    public bool Success { get; set; }
    public string MergedDefinition { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public string Model { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }

    public static MergeSuggestionResult Fail(string message) => new()
    {
        Success = false,
        ErrorMessage = message
    };
}

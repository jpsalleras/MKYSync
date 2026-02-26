namespace DbSync.Core.Models;

/// <summary>
/// Configuración para integración con Claude API (sugerencias de merge).
/// Se mapea desde la sección "Claude" de appsettings.json.
/// </summary>
public class ClaudeSettings
{
    public const string SectionName = "Claude";

    /// <summary>Si la funcionalidad de Sugerir Merge está habilitada.</summary>
    public bool Enabled { get; set; }

    /// <summary>API key de Claude (sk-ant-...).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Modelo a usar. Default: claude-sonnet-4-5-20250929 (costo-eficiente).</summary>
    public string Model { get; set; } = "claude-sonnet-4-5-20250929";

    /// <summary>Max tokens para la respuesta.</summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>URL base de la API.</summary>
    public string BaseUrl { get; set; } = "https://api.anthropic.com";

    /// <summary>
    /// System prompt personalizado. Si vacío, usa el default embebido.
    /// Puede referenciar un archivo con prefijo "file:" (ej: "file:merge-rules.md").
    /// </summary>
    public string? SystemPrompt { get; set; }
}

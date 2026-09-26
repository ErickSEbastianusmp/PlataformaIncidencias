using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PlataformaIncidencias.Services;

/// <summary>
/// Servicio de búsqueda con Algolia (REST via HttpClient).
/// Usa AppId / ApiKey / IndexName desde configuración "Algolia".
/// </summary>
public class AlgoliaSearchService
{
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly ILogger<AlgoliaSearchService> _logger;

    public AlgoliaSearchService(
        IConfiguration configuration,
        HttpClient httpClient,
        ILogger<AlgoliaSearchService> logger)
    {
        _configuration = configuration;
        _httpClient = httpClient;
        _logger = logger;
    }

    public bool IsConfigured()
    {
        var appId = _configuration["Algolia:AppId"];
        var apiKey = _configuration["Algolia:ApiKey"];
        return !string.IsNullOrWhiteSpace(appId)
            && !appId.Contains("REPLACE")
            && !string.IsNullOrWhiteSpace(apiKey)
            && !apiKey.Contains("REPLACE");
    }

    public async Task<string> SearchAsync(string query)
    {
        _logger.LogInformation("[ALGOLIA SEARCH] Consulta ejecutada: {Query}", query);
        Console.WriteLine("[ALGOLIA SEARCH] Consulta ejecutada");

        if (!IsConfigured())
        {
            return "[]";
        }

        try
        {
            var appId = _configuration["Algolia:AppId"]!;
            var apiKey = _configuration["Algolia:ApiKey"]!;
            var indexName = _configuration["Algolia:IndexName"] ?? "incidencias";

            var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://{appId}-dsn.algolia.net/1/indexes/{indexName}/query");

            request.Headers.Add("X-Algolia-API-Key", apiKey);
            request.Headers.Add("X-Algolia-Application-Id", appId);

            var payload = JsonSerializer.Serialize(new { query });
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ALGOLIA SEARCH] Consulta ejecutada con error, fallback local.");
            return "[]";
        }
    }

    /// <summary>
    /// Consulta Algolia por estación o descripción y devuelve los IDs (objectID).
    /// Si no hay configuración real, devuelve lista vacía para que el controlador
    /// aplique fallback local contra SQLite.
    /// </summary>
    public async Task<List<int>> SearchIdsAsync(string query)
    {
        _logger.LogInformation("[ALGOLIA SEARCH] Consulta ejecutada: {Query}", query);
        Console.WriteLine("[ALGOLIA SEARCH] Consulta ejecutada");

        if (string.IsNullOrWhiteSpace(query))
        {
            return new List<int>();
        }

        if (!IsConfigured())
        {
            return new List<int>();
        }

        try
        {
            var raw = await SearchAsync(query);
            using var doc = JsonDocument.Parse(raw);
            var ids = new List<int>();

            if (doc.RootElement.TryGetProperty("hits", out var hits))
            {
                foreach (var hit in hits.EnumerateArray())
                {
                    if (hit.TryGetProperty("objectID", out var objectIdProp))
                    {
                        if (objectIdProp.ValueKind == JsonValueKind.Number && objectIdProp.TryGetInt32(out var numId))
                        {
                            ids.Add(numId);
                        }
                        else if (objectIdProp.ValueKind == JsonValueKind.String &&
                                 int.TryParse(objectIdProp.GetString(), out var strId))
                        {
                            ids.Add(strId);
                        }
                    }
                }
            }

            return ids;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ALGOLIA SEARCH] Consulta ejecutada con error al parsear IDs.");
            return new List<int>();
        }
    }

    public async Task IndexIncidenciaAsync(int id, string estacion, string descripcion, string prioridad, string estado)
    {
        if (!IsConfigured())
        {
            return;
        }

        try
        {
            var appId = _configuration["Algolia:AppId"]!;
            var adminKey = _configuration["Algolia:AdminKey"] ?? _configuration["Algolia:ApiKey"]!;
            var indexName = _configuration["Algolia:IndexName"] ?? "incidencias";

            // PUT idempotente: guarda/actualiza el objeto con objectID = id
            var request = new HttpRequestMessage(
                HttpMethod.Put,
                $"https://{appId}.algolia.net/1/indexes/{indexName}/{id}");

            request.Headers.Add("X-Algolia-API-Key", adminKey);
            request.Headers.Add("X-Algolia-Application-Id", appId);

            var payload = JsonSerializer.Serialize(new
            {
                objectID = id.ToString(),
                estacion,
                descripcion,
                prioridad,
                estado
            });
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo indexar incidencia en Algolia.");
        }
    }
}

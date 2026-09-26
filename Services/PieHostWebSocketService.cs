using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PlataformaIncidencias.Services;

/// <summary>
/// Servicio de eventos en tiempo real con PieHost (HTTP publish + WebSocket client).
/// Usa ClusterId / ApiKey / Channel desde configuración "PieHost".
/// </summary>
public class PieHostWebSocketService
{
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly ILogger<PieHostWebSocketService> _logger;

    public PieHostWebSocketService(
        IConfiguration configuration,
        HttpClient httpClient,
        ILogger<PieHostWebSocketService> logger)
    {
        _configuration = configuration;
        _httpClient = httpClient;
        _logger = logger;
    }

    public bool IsConfigured()
    {
        var apiKey = _configuration["PieHost:ApiKey"];
        return !string.IsNullOrWhiteSpace(apiKey) && !apiKey.Contains("REPLACE");
    }

    public string GetWebSocketUrl()
    {
        var clusterId = _configuration["PieHost:ClusterId"] ?? "free3";
        return $"wss://{clusterId}.piehost.com/app";
    }

    public async Task PublishAsync(string eventName, object data, CancellationToken cancellationToken = default)
    {
        var channel = _configuration["PieHost:Channel"] ?? "incidencias-canal";
        _logger.LogInformation("[PIEHOST WEBSOCKET] Evento publicado: {Event} en canal {Channel}", eventName, channel);

        if (!IsConfigured())
        {
            return;
        }

        try
        {
            var clusterId = _configuration["PieHost:ClusterId"] ?? "free3";
            var apiKey = _configuration["PieHost:ApiKey"]!;

            // 1) Intento vía WebSocket (cliente nativo)
            try
            {
                using var ws = new ClientWebSocket();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(5));

                await ws.ConnectAsync(new Uri(GetWebSocketUrl()), cts.Token);

                var message = JsonSerializer.Serialize(new
                {
                    channel,
                    @event = eventName,
                    data
                });
                var bytes = Encoding.UTF8.GetBytes(message);
                await ws.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken: cts.Token);

                await ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "done",
                    CancellationToken.None);
                return;
            }
            catch
            {
                // Fallback a HTTP publish si el WebSocket falla
            }

            // 2) Fallback vía HTTP publish
            var payload = JsonSerializer.Serialize(new
            {
                channel,
                @event = eventName,
                data
            });
            var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://{clusterId}.piehost.com/publish");
            request.Headers.Add("X-Api-Key", apiKey);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PIEHOST WEBSOCKET] Evento publicado con error, se omite.");
        }
    }
}

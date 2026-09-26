using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using PlataformaIncidencias.Data;
using PlataformaIncidencias.Models;
using PlataformaIncidencias.Services;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PlataformaIncidencias.Controllers;

[Route("Operaciones")]
public class OperacionesController : Controller
{
    private const string CacheKeyAbiertas = "incidencias_abiertas";

    private readonly ApplicationDbContext _context;
    private readonly ILogger<OperacionesController> _logger;
    private readonly IDistributedCache _cache;
    private readonly AlgoliaSearchService _algolia;
    private readonly PieHostWebSocketService _pieHost;
    private readonly IConfiguration _configuration;

    public OperacionesController(
        ApplicationDbContext context,
        ILogger<OperacionesController> logger,
        IDistributedCache cache,
        AlgoliaSearchService algolia,
        PieHostWebSocketService pieHost,
        IConfiguration configuration)
    {
        _context = context;
        _logger = logger;
        _cache = cache;
        _algolia = algolia;
        _pieHost = pieHost;
        _configuration = configuration;
    }

    // GET: /Operaciones/Incidencias?query=texto (alias q para compatibilidad)
    [HttpGet("Incidencias")]
    public async Task<IActionResult> Incidencias(string? query, string? q)
    {
        var texto = query ?? q;
        ViewData["Query"] = texto ?? string.Empty;
        ViewData["PieHostCluster"] = _configuration["PieHost:ClusterId"] ?? "free3";
        ViewData["PieHostChannel"] = _configuration["PieHost:Channel"] ?? "incidencias-canal";
        ViewData["PieHostKey"] = _configuration["PieHost:ApiKey"] ?? "REPLACE_PIEHOST_KEY";

        // 1) Búsqueda con texto: saltarse la caché, consultar Algolia y cruzar con SQLite
        if (!string.IsNullOrWhiteSpace(texto))
        {
            return await BuscarPorAlgolia(texto.Trim());
        }

        // 2) Lista estándar: cacheada en Redis 60s bajo "incidencias_abiertas"
        var cachedJson = await _cache.GetStringAsync(CacheKeyAbiertas);
        if (!string.IsNullOrEmpty(cachedJson))
        {
            _logger.LogInformation("[REDIS HIT] Lectura desde caché");
            Console.WriteLine("[REDIS HIT] Lectura desde caché");
            var cached = JsonSerializer.Deserialize<List<Incidencia>>(cachedJson);
            if (cached != null)
            {
                return View(cached);
            }
        }

        _logger.LogInformation("[REDIS MISS] Consulta a Base de Datos SQLite");
        Console.WriteLine("[REDIS MISS] Consulta a Base de Datos SQLite");

        var abiertas = await _context.Incidencias
            .Where(i => i.Estado == "Abierta")
            .OrderBy(i => i.Id)
            .ToListAsync();

        await _cache.SetStringAsync(
            CacheKeyAbiertas,
            JsonSerializer.Serialize(abiertas),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60)
            });

        return View(abiertas);
    }

    /// <summary>
    /// Consulta Algolia por estación o descripción cuando 'query' contiene datos,
    /// cruza los IDs devueltos con la BD y muestra ÚNICAMENTE incidencias abiertas existentes.
    /// Si la búsqueda está vacía, devuelve la lista estándar (no debería llegar aquí).
    /// </summary>
    private async Task<IActionResult> BuscarPorAlgolia(string texto)
    {
        _logger.LogInformation("[ALGOLIA SEARCH] Consulta ejecutada: {Query}", texto);
        Console.WriteLine("[ALGOLIA SEARCH] Consulta ejecutada");

        // Consulta directa (sin caché) para no mezclar resultados de búsqueda con el listado general
        _logger.LogInformation("[REDIS MISS] Consulta a Base de Datos SQLite");
        Console.WriteLine("[REDIS MISS] Consulta a Base de Datos SQLite");

        List<int> ids = await _algolia.SearchIdsAsync(texto);

        List<Incidencia> resultado;
        if (ids.Count > 0)
        {
            // Cruza IDs de Algolia con SQLite: solo abiertas existentes
            resultado = await _context.Incidencias
                .Where(i => i.Estado == "Abierta" && ids.Contains(i.Id))
                .OrderBy(i => i.Id)
                .ToListAsync();
        }
        else if (_algolia.IsConfigured())
        {
            // Algolia configurado pero sin coincidencias -> lista vacía
            resultado = new List<Incidencia>();
        }
        else
        {
            // Fallback local (sin credenciales reales): filtra en memoria por
            // estación o descripción, insensible a mayúsculas y tildes
            // (SQLite lower()/LIKE no maneja bien los acentos).
            var abiertas = await _context.Incidencias
                .Where(i => i.Estado == "Abierta")
                .OrderBy(i => i.Id)
                .ToListAsync();

            var normalizado = Normalizar(texto);
            resultado = abiertas
                .Where(i => Normalizar(i.Estacion).Contains(normalizado) ||
                            Normalizar(i.Descripcion).Contains(normalizado))
                .ToList();
        }

        return View("Incidencias", resultado);
    }

    private static string Normalizar(string texto)
    {
        var descompuesto = texto.Normalize(NormalizationForm.FormD);
        var sinTildes = new string(descompuesto
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray());
        return sinTildes.ToLowerInvariant();
    }

    // POST: /Operaciones/Crear (form modal + AJAX)
    // Campos: Estación, Descripción, Prioridad [Alta, Media, Baja], Estado por defecto "Abierta"
    [HttpPost("Crear")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Crear(string estacion, string descripcion, string prioridad)
    {
        var prioridadesValidas = new[] { "Alta", "Media", "Baja" };

        if (string.IsNullOrWhiteSpace(estacion) ||
            string.IsNullOrWhiteSpace(descripcion) ||
            !prioridadesValidas.Contains(prioridad))
        {
            if (IsAjax())
            {
                return BadRequest(new { success = false, message = "Estación, Descripción y Prioridad (Alta, Media, Baja) son obligatorios." });
            }
            TempData["ErrorCrear"] = "Estación, Descripción y Prioridad (Alta, Media, Baja) son obligatorios.";
            return RedirectToAction(nameof(Incidencias));
        }

        var nueva = new Incidencia
        {
            Estacion = estacion.Trim(),
            Descripcion = descripcion.Trim(),
            Prioridad = prioridad,
            Estado = "Abierta"
        };

        _context.Incidencias.Add(nueva);
        await _context.SaveChangesAsync();

        // Sincroniza SQLite y actualiza/invalida la caché de Redis
        await _cache.RemoveAsync(CacheKeyAbiertas);
        _logger.LogInformation("[REDIS MISS] Caché invalidada tras crear incidencia {Id}", nueva.Id);
        Console.WriteLine("[REDIS MISS] Consulta a Base de Datos SQLite");

        // Indexa en Algolia (si hay credenciales reales) y emite evento WebSocket
        await _algolia.IndexIncidenciaAsync(nueva.Id, nueva.Estacion, nueva.Descripcion, nueva.Prioridad, nueva.Estado);
        _logger.LogInformation("[PIEHOST WEBSOCKET] Evento publicado: IncidenciaActualizada {Id} -> {Estado}", nueva.Id, nueva.Estado);
        Console.WriteLine("[PIEHOST WEBSOCKET] Evento publicado");
        await _pieHost.PublishAsync("IncidenciaActualizada", new
        {
            id = nueva.Id,
            estado = nueva.Estado
        });

        if (IsAjax())
        {
            return Json(new
            {
                success = true,
                id = nueva.Id,
                estacion = nueva.Estacion,
                descripcion = nueva.Descripcion,
                prioridad = nueva.Prioridad,
                estado = nueva.Estado
            });
        }

        return RedirectToAction(nameof(Incidencias));
    }

    // POST: /Operaciones/Cerrar/5 (form clásico + AJAX)
    [HttpPost("Cerrar/{id}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cerrar(int id)
    {
        var incidencia = await _context.Incidencias.FindAsync(id);
        if (incidencia == null)
        {
            if (IsAjax())
            {
                return NotFound(new { success = false, id });
            }
            return RedirectToAction(nameof(Incidencias));
        }

        incidencia.Estado = "Cerrada";
        await _context.SaveChangesAsync();

        // Invalidar inmediatamente la clave "incidencias_abiertas" en Redis
        await _cache.RemoveAsync(CacheKeyAbiertas);

        // Enviar mensaje WebSocket vía PieHost con evento "IncidenciaActualizada" e { id, estado }
        _logger.LogInformation("[PIEHOST WEBSOCKET] Evento publicado: IncidenciaActualizada {Id} -> {Estado}", id, incidencia.Estado);
        Console.WriteLine("[PIEHOST WEBSOCKET] Evento publicado");
        await _pieHost.PublishAsync("IncidenciaActualizada", new
        {
            id = incidencia.Id,
            estado = incidencia.Estado
        });

        if (IsAjax())
        {
            return Json(new { success = true, id = incidencia.Id, estado = incidencia.Estado });
        }

        return RedirectToAction(nameof(Incidencias));
    }

    private bool IsAjax()
    {
        return Request.Headers.XRequestedWith == "XMLHttpRequest" ||
               (Request.Headers.Accept.ToString().Contains("application/json") &&
                Request.Method == "POST");
    }
}

using System.ComponentModel.DataAnnotations;

namespace PlataformaIncidencias.Models;

public class Incidencia
{
    public int Id { get; set; }

    [Required]
    [StringLength(100)]
    public string Estacion { get; set; } = string.Empty;

    [Required]
    [StringLength(500)]
    public string Descripcion { get; set; } = string.Empty;

    [Required]
    public string Prioridad { get; set; } = "Media"; // Alta, Media, Baja

    [Required]
    public string Estado { get; set; } = "Abierta"; // Abierta o Cerrada
}

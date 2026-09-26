using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PlataformaIncidencias.Models;

namespace PlataformaIncidencias.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var services = scope.ServiceProvider;

        var context = services.GetRequiredService<ApplicationDbContext>();
        // Aplica migraciones automáticas
        await context.Database.MigrateAsync();

        var userManager = services.GetRequiredService<UserManager<IdentityUser>>();

        const string supervisorEmail = "supervisor@empresa.com";
        const string supervisorPassword = "Supervisor123!";

        var supervisor = await userManager.FindByEmailAsync(supervisorEmail);
        if (supervisor == null)
        {
            supervisor = new IdentityUser
            {
                UserName = supervisorEmail,
                Email = supervisorEmail,
                EmailConfirmed = true
            };
            var result = await userManager.CreateAsync(supervisor, supervisorPassword);
            if (!result.Succeeded)
            {
                throw new Exception("No se pudo crear el usuario supervisor: " +
                    string.Join(", ", result.Errors.Select(e => e.Description)));
            }
        }

        // Incidencias de prueba iniciales
        if (!await context.Incidencias.AnyAsync())
        {
            var incidencias = new List<Incidencia>
            {
                new Incidencia { Estacion = "Estación Norte", Descripcion = "Falla en cinta transportadora principal", Prioridad = "Alta", Estado = "Abierta" },
                new Incidencia { Estacion = "Estación Sur", Descripcion = "Sensor de temperatura descalibrado", Prioridad = "Media", Estado = "Abierta" },
                new Incidencia { Estacion = "Estación Centro", Descripcion = "Mantenimiento preventivo de bomba de agua", Prioridad = "Baja", Estado = "Abierta" },
                new Incidencia { Estacion = "Estación Este", Descripcion = "Revisión de tablero eléctrico completada", Prioridad = "Media", Estado = "Cerrada" },
            };
            context.Incidencias.AddRange(incidencias);
            await context.SaveChangesAsync();
        }
    }
}

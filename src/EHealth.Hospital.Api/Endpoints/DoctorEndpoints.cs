using EHealth.Hospital.Data;
using EHealth.Hospital.Models;
using Microsoft.EntityFrameworkCore;

namespace EHealth.Hospital.Endpoints;

public static class DoctorEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/doctors").WithTags("Doctors");

        group.MapGet("/", async (AppDbContext db) =>
            await db.Doctors.ToListAsync());

        group.MapGet("/{id:guid}", async (Guid id, AppDbContext db) =>
            await db.Doctors.FindAsync(id) is { } d
                ? Results.Ok(d)
                : Results.NotFound());

        group.MapDelete("/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var doctor = await db.Doctors.FindAsync(id);
            if (doctor is null) return Results.NotFound();
            db.Doctors.Remove(doctor);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}

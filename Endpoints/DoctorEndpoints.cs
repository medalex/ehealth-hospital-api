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

        // Register doctor credential on DKG and store the returned UAL
        group.MapPost("/{id:guid}/register-credential", async (
            Guid id, AppDbContext db,
            IHttpClientFactory http, IConfiguration config) =>
        {
            var doctor = await db.Doctors.FindAsync(id);
            if (doctor is null) return Results.NotFound();

            var ual = await PublishCredentialToDkg(doctor, http, config);
            if (ual is null)
                return Results.Problem("Failed to publish credential to DKG");

            doctor.CredentialUal = ual;
            await db.SaveChangesAsync();

            return Results.Ok(new { doctor.Id, doctor.CredentialUal });
        });
    }

    private static async Task<string?> PublishCredentialToDkg(
        Doctor doctor, IHttpClientFactory http, IConfiguration config)
    {
        try
        {
            var mfssiaUrl = config["MfssiaUrl"] ?? "http://mfssia-ehealth:4000/api";
            var client = http.CreateClient();

            var turtle = $"""
                @prefix rx: <https://mfssia.io/ontology/prescription#> .
                @prefix mfssia: <https://mfssia.org/ontology#> .

                <urn:hospital:doctor:{doctor.Id}> a mfssia:Credential ;
                    rx:doctorId "{doctor.Id}" ;
                    rx:firstName "{doctor.FirstName}" ;
                    rx:lastName "{doctor.LastName}" ;
                    rx:specialty "{doctor.Specialty}" .
                """;

            var response = await client.PostAsync(
                $"{mfssiaUrl}/rdf",
                new StringContent(turtle, System.Text.Encoding.UTF8, "text/turtle"));

            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadFromJsonAsync<DkgResponse>();
            return json?.UAL;
        }
        catch { return null; }
    }

    private record DkgResponse(string UAL);
}

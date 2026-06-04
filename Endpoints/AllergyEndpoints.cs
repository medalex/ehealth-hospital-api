using EHealth.Hospital.Data;
using EHealth.Hospital.Models;
using Microsoft.EntityFrameworkCore;

namespace EHealth.Hospital.Endpoints;

public static class AllergyEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/allergies").WithTags("Allergies");

        group.MapGet("/patient/{patientId:guid}", async (Guid patientId, AppDbContext db) =>
            await db.AllergyRecords
                .Where(a => a.PatientId == patientId)
                .ToListAsync());

        group.MapPost("/", async (AllergyRecord allergy, AppDbContext db,
            IHttpClientFactory http, IConfiguration config) =>
        {
            allergy.Id = Guid.NewGuid();
            allergy.RecordedAt = DateTime.UtcNow;

            db.AllergyRecords.Add(allergy);
            await db.SaveChangesAsync();

            // Publish Turtle RDF commitment to DKG
            var ual = await PublishToDkg(allergy, http, config);
            if (ual is not null)
            {
                allergy.DkgUal = ual;
                await db.SaveChangesAsync();
            }

            return Results.Created($"/api/allergies/{allergy.Id}", allergy);
        });

        group.MapDelete("/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var allergy = await db.AllergyRecords.FindAsync(id);
            if (allergy is null) return Results.NotFound();
            db.AllergyRecords.Remove(allergy);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static async Task<string?> PublishToDkg(
        AllergyRecord allergy, IHttpClientFactory http, IConfiguration config)
    {
        try
        {
            var mfssiaUrl = config["MfssiaUrl"] ?? "http://mfssia-ehealth:4000/api";
            var client = http.CreateClient();

            var turtle = $"""
                @prefix rx: <https://mfssia.io/ontology/prescription#> .
                @prefix xsd: <http://www.w3.org/2001/XMLSchema#> .

                <urn:hospital:allergy:{allergy.Id}> a rx:Allergy ;
                    rx:patientId "{allergy.PatientId}" ;
                    rx:substance "{allergy.Substance}" ;
                    rx:snomedCode "{allergy.SnomedCode}" ;
                    rx:source "{allergy.Source}" ;
                    rx:recordedAt "{allergy.RecordedAt:O}"^^xsd:dateTime .
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

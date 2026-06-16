using EHealth.Hospital.Data;
using EHealth.Hospital.Models;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

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

        // Register doctor credential on DKG and store the returned UAL
        group.MapPost("/{id:guid}/register-credential", async (
            Guid id, AppDbContext db,
            IHttpClientFactory http, IConfiguration config) =>
        {
            var doctor = await db.Doctors.FindAsync(id);
            if (doctor is null) return Results.NotFound();

            var ual = await PublishCredentialToDkg(doctor, http, config);
            if (ual is null)
                return Results.Json(
                    new { error = "Failed to publish credential to DKG" },
                    statusCode: 502);

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

            var hospitalId    = config["HospitalId"] ?? "hospital-1";
            var issuedAt      = DateTime.UtcNow.ToString("O");
            var credentialHash = ComputeCredentialHash(doctor);

            // Per paper eq:public-inputs: only H(cred) and DID go to DKG.
            // Actual credential attributes (license, specialty) stay off-chain as private ZKP witness.
            var turtle = $"""
                @prefix rx:     <https://mfssia.io/ontology/prescription#> .
                @prefix mfssia: <http://www.semanticweb.org/mubashar/ontologies/2024/4/MFSSIA-ontology#> .
                @prefix xsd:    <http://www.w3.org/2001/XMLSchema#> .
                @prefix prov:   <http://www.w3.org/ns/prov#> .
                @prefix owl:    <http://www.w3.org/2002/07/owl#> .

                <did:mfssia:physician:{doctor.Id}>
                    a rx:Physician, owl:NamedIndividual ;
                    rx:did                "did:mfssia:physician:{doctor.Id}" ;
                    rx:holdsCredential    <did:mfssia:credential:{doctor.Id}> ;
                    mfssia:isRegisteredBy <urn:hospital:{hospitalId}> ;
                    prov:generatedAtTime  "{issuedAt}"^^xsd:dateTime .

                <did:mfssia:credential:{doctor.Id}>
                    a mfssia:Authentication, owl:NamedIndividual ;
                    rx:credentialHash "{credentialHash}" .
                """;

            var response = await client.PostAsync(
                $"{mfssiaUrl}/rdf",
                new StringContent(turtle, System.Text.Encoding.UTF8, "text/turtle"));

            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadFromJsonAsync<DkgResponse>();
            return json?.Data?.UAL ?? json?.UAL;
        }
        catch { return null; }
    }

    // H(cred) — collision-resistant commitment to credential attributes (private witness).
    // In the ZKP circuit this corresponds to doctorCredentialHash (eq:public-inputs).
    private static string ComputeCredentialHash(Doctor doctor)
    {
        var raw = $"{doctor.Id}:{doctor.LicenseNumber}:{doctor.Specialty}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private record DkgData(string? UAL);
    private record DkgResponse(string? UAL, DkgData? Data);
}

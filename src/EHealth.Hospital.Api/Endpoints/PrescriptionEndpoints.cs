using EHealth.Hospital.Data;
using EHealth.Hospital.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace EHealth.Hospital.Endpoints;

public static class PrescriptionEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/prescriptions").WithTags("Prescriptions");

        group.MapGet("/", async (AppDbContext db) =>
            await db.Prescriptions.OrderByDescending(p => p.CreatedAt).ToListAsync());

        group.MapGet("/{id:guid}", async (Guid id, AppDbContext db) =>
            await db.Prescriptions.FindAsync(id) is { } p
                ? Results.Ok(p)
                : Results.NotFound());

        group.MapDelete("/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var prescription = await db.Prescriptions.FindAsync(id);
            if (prescription is null) return Results.NotFound();
            db.Prescriptions.Remove(prescription);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Main endpoint: create prescription → triggers ZKP proof generation
        group.MapPost("/", async (CreatePrescriptionRequest req, AppDbContext db,
            IHttpClientFactory http, IConfiguration config) =>
        {
            var doctor = await db.Doctors.FindAsync(req.DoctorId);
            if (doctor is null)
                return Results.BadRequest(new { error = "Doctor not found" });
            if (doctor.CredentialUal is null)
                return Results.BadRequest(new { error = "Doctor credential not registered on DKG" });

            // Проверяем consent пациента на доступ госпиталя к его данным
            var orgId = config["HospitalId"] ?? "hospital-1";
            if (!await CheckConsent(req.PatientId, orgId, http, config))
                return Results.Json(
                    new { error = $"Patient {req.PatientId} has not granted consent to {orgId}" },
                    statusCode: 403);

            // Fetch patient allergies from local DB
            var allergies = await db.AllergyRecords
                .Where(a => a.PatientId == req.PatientId)
                .ToListAsync();

            // Fetch lab results from lab service
            var labResults = await FetchLabResults(req.PatientId, http, config);

            // Fetch ZKP public params (clinical policies) from mfssia-ehealth
            var policies = await FetchPolicies(http, config);

            // Build ZKP proof request
            var proofRequest = new ZkpProveRequest(
                DoctorCredentialUal: doctor.CredentialUal,
                PatientId: req.PatientId,
                DrugIds: [req.DrugId],
                Dosages: [req.Dosage],
                PatientAge: req.PatientAge,
                WorkflowId: req.WorkflowId,
                Allergies: allergies.Select(a => a.Substance).ToArray(),
                LabResults: labResults,
                Policies: policies
            );

            // Call ZKP prover service
            var zkpResult = await CallZkpProver(proofRequest, http, config);

            var prescription = new Prescription
            {
                Id = Guid.NewGuid(),
                DoctorId = req.DoctorId,
                PatientId = req.PatientId,
                DrugId = req.DrugId,
                Dosage = req.Dosage,
                Outcome = zkpResult?.Outcome,
                StmtHash = zkpResult?.StmtHash,
                ProofJson = zkpResult is not null ? JsonSerializer.Serialize(zkpResult.Proof) : null,
                PublicSignalsJson = zkpResult?.PublicSignals is not null ? JsonSerializer.Serialize(zkpResult.PublicSignals) : null,
                CreatedAt = DateTime.UtcNow
            };

            db.Prescriptions.Add(prescription);
            await db.SaveChangesAsync();

            return Results.Created($"/api/prescriptions/{prescription.Id}", prescription);
        });
    }

    private static async Task<bool> CheckConsent(
        Guid patientId, string organizationId, IHttpClientFactory http, IConfiguration config)
    {
        try
        {
            var patientApiUrl = config["PatientApiUrl"] ?? "http://patient-api:3001";
            var client = http.CreateClient();
            var resp = await client.GetAsync(
                $"{patientApiUrl}/api/consents/check?patientId={patientId}&organizationId={organizationId}");
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static async Task<LabResultDto[]> FetchLabResults(
        Guid patientId, IHttpClientFactory http, IConfiguration config)
    {
        try
        {
            var labUrl = config["LabServiceUrl"] ?? "http://lab:3002";
            var client = http.CreateClient();
            var results = await client.GetFromJsonAsync<LabResultDto[]>(
                $"{labUrl}/api/results/patient/{patientId}");
            return results ?? [];
        }
        catch { return []; }
    }

    private static async Task<PolicyDto[]> FetchPolicies(
        IHttpClientFactory http, IConfiguration config)
    {
        try
        {
            var mfssiaUrl = config["MfssiaUrl"] ?? "http://mfssia-ehealth:4000/api";
            var client = http.CreateClient();

            // Реальная структура ответа: { success, data: { data: [...] } }
            var root = await client.GetFromJsonAsync<JsonElement>(
                $"{mfssiaUrl}/rx-governance/policies");

            if (!root.TryGetProperty("data", out var outer) ||
                !outer.TryGetProperty("data", out var inner) ||
                inner.ValueKind != JsonValueKind.Array)
                return [];

            return inner.EnumerateArray()
                .Select(p =>
                {
                    // medication нет отдельным полем — извлекаем из id: "urn:rx:policy:pol:metformin-egfr"
                    var id = GetStr(p, "id").ToLowerInvariant();
                    var med = id.Contains("metformin") ? "metformin"
                            : id.Contains("penicillin") ? "penicillin"
                            : "";

                    return new PolicyDto(
                        MedicationCode: med,
                        ClinicalCondition: CleanRdf(GetStr(p, "clinicalCondition")),
                        ComparisonOperator: CleanRdf(GetStr(p, "comparisonOperator")),
                        Threshold: decimal.TryParse(
                            CleanRdf(GetStr(p, "threshold")),
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var t) ? t : 0m
                    );
                })
                .Where(p => !string.IsNullOrEmpty(p.MedicationCode))
                .ToArray();
        }
        catch { return []; }
    }

    // Возвращает строковое значение свойства JsonElement или ""
    private static string GetStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) ? v.GetString() ?? "" : "";

    // Очищает RDF-значения: "\"30\"^^xsd:decimal" → "30", "\"eGFR\"" → "eGFR"
    private static string CleanRdf(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var idx = s.IndexOf("^^", StringComparison.Ordinal);
        if (idx >= 0) s = s[..idx];
        return s.Trim('"');
    }

    private static async Task<ZkpResult?> CallZkpProver(
        ZkpProveRequest req, IHttpClientFactory http, IConfiguration config)
    {
        try
        {
            var zkpUrl = config["ZkpProverUrl"] ?? "http://zkp-prover:3005";
            var client = http.CreateClient();
            var res = await client.PostAsJsonAsync($"{zkpUrl}/prove", req);
            return res.IsSuccessStatusCode
                ? await res.Content.ReadFromJsonAsync<ZkpResult>()
                : null;
        }
        catch { return null; }
    }

    // DTOs for inter-service communication
    private record CreatePrescriptionRequest(
        Guid DoctorId, Guid PatientId,
        int DrugId, string Dosage,
        int PatientAge, int WorkflowId);

    private record LabResultDto(
        string LoincCode, string Metric,
        string Formula, decimal Value, string Unit);

    private record PolicyDto(
        string MedicationCode, string ClinicalCondition,
        string ComparisonOperator, decimal Threshold);

    private record ZkpProveRequest(
        string DoctorCredentialUal, Guid PatientId,
        int[] DrugIds, string[] Dosages, int PatientAge, int WorkflowId,
        string[] Allergies, LabResultDto[] LabResults, PolicyDto[] Policies);

    private record ZkpResult(bool Outcome, string StmtHash, object Proof, string[]? PublicSignals);
}

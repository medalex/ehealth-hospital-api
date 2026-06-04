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

        // Main endpoint: create prescription → triggers ZKP proof generation
        group.MapPost("/", async (CreatePrescriptionRequest req, AppDbContext db,
            IHttpClientFactory http, IConfiguration config) =>
        {
            var doctor = await db.Doctors.FindAsync(req.DoctorId);
            if (doctor is null) return Results.BadRequest("Doctor not found");
            if (doctor.CredentialUal is null)
                return Results.BadRequest("Doctor credential not registered on DKG");

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
                DrugIds: req.DrugIds,
                Dosages: req.Dosages,
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
                DrugIds = req.DrugIds,
                Dosages = req.Dosages,
                Outcome = zkpResult?.Outcome,
                StmtHash = zkpResult?.StmtHash,
                ProofJson = zkpResult is not null ? JsonSerializer.Serialize(zkpResult.Proof) : null,
                CreatedAt = DateTime.UtcNow
            };

            db.Prescriptions.Add(prescription);
            await db.SaveChangesAsync();

            return Results.Created($"/api/prescriptions/{prescription.Id}", prescription);
        });
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
            var resp = await client.GetFromJsonAsync<MfssiaResponse<PolicyDto[]>>(
                $"{mfssiaUrl}/rx-governance/policies");
            return resp?.Data ?? [];
        }
        catch { return []; }
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
        int[] DrugIds, int[] Dosages,
        int PatientAge, int WorkflowId);

    private record LabResultDto(
        string LoincCode, string Metric,
        string Formula, decimal Value, string Unit);

    private record PolicyDto(
        string MedicationCode, string ClinicalCondition,
        string ComparisonOperator, decimal Threshold);

    private record MfssiaResponse<T>(bool Success, T? Data);

    private record ZkpProveRequest(
        string DoctorCredentialUal, Guid PatientId,
        int[] DrugIds, int[] Dosages, int PatientAge, int WorkflowId,
        string[] Allergies, LabResultDto[] LabResults, PolicyDto[] Policies);

    private record ZkpResult(bool Outcome, string StmtHash, object Proof);
}

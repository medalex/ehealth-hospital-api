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

            // MFSSIA physician-access gate (ConsentAccessSet, ALL_MANDATORY):
            //   C-DOC-AUTH  — physician authenticated (in registry)
            //   C-DOC-AUTHZ — patient consent in DKG covers physician's organization
            var access = await CheckPhysicianAccess(req.DoctorId, req.PatientId, http, config);
            if (!access.Access)
                return Results.Json(new { error = access.Reason }, statusCode: 403);

            // Fetch patient allergies from local DB
            var allergies = await db.AllergyRecords
                .Where(a => a.PatientId == req.PatientId)
                .ToListAsync();

            // Fetch lab record (root + membership proofs) from the MFSSIA lab-record registry.
            var lab = await FetchLabResults(req.PatientId, http, config);
            if (lab is null)
                return Results.Json(
                    new { error = "MFSSIA lab-record registry unavailable" },
                    statusCode: 503);

            // Fetch ZKP public params (clinical policies) from mfssia-ehealth
            var policies = await FetchPolicies(http, config);

            // Fetch physician Merkle proof from MFSSIA registry (credential hash + siblings + pathBits + root)
            var credProof = await FetchCredentialProofFromMfssia(req.DoctorId, http, config);
            if (credProof is null)
                return Results.Json(
                    new { error = $"Doctor {req.DoctorId} not found in MFSSIA physician registry" },
                    statusCode: 403);

            // Fetch the patient-record Merkle proof from MFSSIA (allergy tree built from DKG,
            // leaf bound to patientId). MFSSIA is authoritative — required, no local fallback.
            var recordProof = await FetchPatientRecordProof(req.PatientId, http, config);
            if (recordProof is null)
                return Results.Json(
                    new { error = "MFSSIA patient-record registry unavailable — cannot anchor patient allergies" },
                    statusCode: 503);

            // Fetch the committed contraindication closure proof from MFSSIA (root +
            // per-allergy membership for the prescribed drug). Required — the circuit binds
            // P2 to this root, so the prover cannot fabricate the contraindication.
            var contra = await FetchContraindicationProofs(recordProof.SubstanceIds, req.DrugId, http, config);
            if (contra is null)
                return Results.Json(
                    new { error = "MFSSIA contraindication registry unavailable" },
                    statusCode: 503);

            // Build ZKP proof request
            var proofRequest = new ZkpProveRequest(
                DoctorCredentialUal: doctor.CredentialUal ?? string.Empty,
                DoctorCredentialHash: credProof.CredentialHash,
                ValidCredentialRoot: credProof.ValidCredentialRoot,
                CredentialSiblings: credProof.Siblings,
                CredentialPathBits: credProof.PathBits,
                PatientId: req.PatientId,
                DrugIds: [req.DrugId],
                Dosages: [req.Dosage],
                PatientAge: req.PatientAge,
                WorkflowId: req.WorkflowId,
                PrescriptionIssuedAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Allergies: allergies.Select(a => a.Substance).ToArray(),
                Substances: recordProof.Substances,
                PatientRecordRoot: recordProof.PatientRecordRoot,
                RefLeaf: recordProof.RefLeaf,
                RefSiblings: recordProof.RefSiblings,
                RefPathBits: recordProof.RefPathBits,
                RefIsActive: recordProof.RefIsActive,
                ContraindicationRoot: contra.ContraindicationRoot,
                ContraProofs: contra.ContraProofs,
                LabRecordRoot: lab.LabRecordRoot,
                LabResults: lab.Results,
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
                CreatedAt = DateTime.UtcNow,
                Reasons = zkpResult?.Reasons
            };

            db.Prescriptions.Add(prescription);
            await db.SaveChangesAsync();

            return Results.Created($"/api/prescriptions/{prescription.Id}", prescription);
        });
    }

    // Runs the MFSSIA ConsentAccessSet gate for the physician↔patient pair.
    // Returns access=false (with a reason) when MFSSIA is unreachable or either challenge fails.
    private static async Task<AccessDecision> CheckPhysicianAccess(
        Guid doctorId, Guid patientId, IHttpClientFactory http, IConfiguration config)
    {
        try
        {
            var mfssiaUrl = config["MfssiaUrl"] ?? "http://mfssia-ehealth:4000/api";
            var client = http.CreateClient();
            var resp = await client.PostAsJsonAsync(
                $"{mfssiaUrl}/physician-access/check",
                new { doctorId = doctorId.ToString(), patientId = patientId.ToString() });

            if (!resp.IsSuccessStatusCode)
                return new AccessDecision(false, $"MFSSIA access check unavailable (HTTP {(int)resp.StatusCode})");

            // MFSSIA wraps the decision: { success, message, data: { access, authz, reason, ... } }
            var root = await resp.Content.ReadFromJsonAsync<JsonElement>();
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return new AccessDecision(false, "MFSSIA returned no access decision");

            var access = data.TryGetProperty("access", out var a) && a.ValueKind == JsonValueKind.True;
            var reason = data.TryGetProperty("reason", out var r) ? r.GetString() : null;
            return new AccessDecision(access, reason ?? (access ? "" : "access denied by MFSSIA gate"));
        }
        catch (Exception e)
        {
            return new AccessDecision(false, $"MFSSIA access gate unreachable: {e.Message}");
        }
    }

    private record AccessDecision(bool Access, string Reason);

    // Lab measurements + Merkle proofs are read from the DKG-backed MFSSIA lab-record
    // registry, so the value the ZKP uses is bound to the committed lab record.
    // Null → MFSSIA unavailable. Returns a valid (possibly empty) record otherwise.
    private static async Task<LabRecordBundle?> FetchLabResults(
        Guid patientId, IHttpClientFactory http, IConfiguration config)
    {
        try
        {
            var mfssiaUrl = config["MfssiaUrl"] ?? "http://mfssia-ehealth:4000/api";
            var client = http.CreateClient();
            var root = await client.GetFromJsonAsync<JsonElement>($"{mfssiaUrl}/lab-record/{patientId}");

            // MFSSIA wraps payloads: { success, data: { labRecordRoot, measurements: [...] } }
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return null;
            if (!data.TryGetProperty("labRecordRoot", out var rootEl)) return null;

            var measurements = data.TryGetProperty("measurements", out var ms) && ms.ValueKind == JsonValueKind.Array
                ? ms.EnumerateArray().Select(m => new LabResultDto(
                    Metric: m.TryGetProperty("metric", out var me) ? me.GetString() ?? "" : "",
                    MetricId: m.TryGetProperty("metricId", out var mi) ? mi.GetString() ?? "0" : "0",
                    Value: m.TryGetProperty("value", out var va) && va.TryGetDecimal(out var dec) ? dec : 0m,
                    MeasuredAt: m.TryGetProperty("measuredAt", out var ma)
                        && ma.ValueKind == JsonValueKind.String
                        && DateTime.TryParse(ma.GetString(), out var dt) ? dt : default,
                    Siblings: m.TryGetProperty("siblings", out var sb) && sb.ValueKind == JsonValueKind.Array
                        ? sb.EnumerateArray().Select(x => x.GetString()!).ToArray() : Array.Empty<string>(),
                    PathBits: m.TryGetProperty("pathBits", out var pb) && pb.ValueKind == JsonValueKind.Array
                        ? pb.EnumerateArray().Select(x => x.GetInt32()).ToArray() : Array.Empty<int>()
                )).ToArray()
                : Array.Empty<LabResultDto>();

            return new LabRecordBundle(rootEl.GetString() ?? "", measurements);
        }
        catch { return null; }
    }

    private static async Task<PolicyDto[]> FetchPolicies(
        IHttpClientFactory http, IConfiguration config)
    {
        try
        {
            var mfssiaUrl = config["MfssiaUrl"] ?? "http://mfssia-ehealth:4000/api";
            var client = http.CreateClient();

            // Actual response structure: { success, data: { data: [...] } }
            var root = await client.GetFromJsonAsync<JsonElement>(
                $"{mfssiaUrl}/rx-governance/policies");

            if (!root.TryGetProperty("data", out var outer) ||
                !outer.TryGetProperty("data", out var inner) ||
                inner.ValueKind != JsonValueKind.Array)
                return [];

            return inner.EnumerateArray()
                .Select(p =>
                {
                    // medication has no dedicated field — extracted from id: "urn:rx:policy:pol:metformin-egfr"
                    var id = GetStr(p, "id").ToLowerInvariant();
                    var med = id.Contains("metformin") ? "metformin"
                            : id.Contains("amoxicillin") ? "amoxicillin"
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
                            out var t) ? t : 0m,
                        DeltaMax: decimal.TryParse(
                            CleanRdf(GetStr(p, "deltaMax")),
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var dm) ? dm : 0m
                    );
                })
                .Where(p => !string.IsNullOrEmpty(p.MedicationCode))
                .ToArray();
        }
        catch { return []; }
    }

    // Returns string value of a JsonElement property, or ""
    private static string GetStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) ? v.GetString() ?? "" : "";

    // Strips RDF-typed literals: "\"30\"^^xsd:decimal" → "30", "\"eGFR\"" → "eGFR"
    private static string CleanRdf(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var idx = s.IndexOf("^^", StringComparison.Ordinal);
        if (idx >= 0) s = s[..idx];
        return s.Trim('"');
    }

    // Fetches physician Merkle proof from MFSSIA registry:
    // credential hash + siblings + pathBits for the ZKP circuit + tree root
    private static async Task<CredentialProof?> FetchCredentialProofFromMfssia(
        Guid doctorId, IHttpClientFactory http, IConfiguration config)
    {
        try
        {
            var mfssiaUrl = config["MfssiaUrl"] ?? "http://mfssia-ehealth:4000/api";
            var client = http.CreateClient();

            var proofResp = await client.GetFromJsonAsync<JsonElement>(
                $"{mfssiaUrl}/physician-registry/{doctorId}/merkle-proof");
            var rootResp = await client.GetFromJsonAsync<JsonElement>(
                $"{mfssiaUrl}/physician-registry/merkle-root");

            // MFSSIA wraps payloads: { success, message, data: {...} }
            if (!proofResp.TryGetProperty("data", out var proof) || proof.ValueKind != JsonValueKind.Object) return null;
            if (!rootResp.TryGetProperty("data", out var rootData) || rootData.ValueKind != JsonValueKind.Object) return null;

            if (!proof.TryGetProperty("credentialHash", out var hash)) return null;
            if (!proof.TryGetProperty("siblings", out var sibs)) return null;
            if (!proof.TryGetProperty("pathBits", out var bits)) return null;
            if (!rootData.TryGetProperty("root", out var root)) return null;

            return new CredentialProof(
                CredentialHash: hash.GetString()!,
                ValidCredentialRoot: root.GetString()!,
                Siblings: sibs.EnumerateArray().Select(s => s.GetString()!).ToArray(),
                PathBits: bits.EnumerateArray().Select(b => b.GetInt32()).ToArray()
            );
        }
        catch { return null; }
    }

    // Fetches the patient allergy Merkle proof from MFSSIA (built from DKG allergies,
    // leaf bound to patientId). Null → prover falls back to the local allergy list.
    private static async Task<PatientRecordProof?> FetchPatientRecordProof(
        Guid patientId, IHttpClientFactory http, IConfiguration config)
    {
        try
        {
            var mfssiaUrl = config["MfssiaUrl"] ?? "http://mfssia-ehealth:4000/api";
            var client = http.CreateClient();
            var env = await client.GetFromJsonAsync<PatientRecordEnvelope>(
                $"{mfssiaUrl}/patient-record/{patientId}/proof");
            return env?.Data;
        }
        catch { return null; }
    }

    // Fetches the contraindication-closure root plus a membership proof for each allergy
    // substance against the prescribed drug, from the MFSSIA contraindication registry.
    private static async Task<ContraindicationBundle?> FetchContraindicationProofs(
        int[] substanceIds, int drugId, IHttpClientFactory http, IConfiguration config)
    {
        try
        {
            var mfssiaUrl = config["MfssiaUrl"] ?? "http://mfssia-ehealth:4000/api";
            var client = http.CreateClient();

            var rootResp = await client.GetFromJsonAsync<JsonElement>($"{mfssiaUrl}/contraindication/root");
            if (!rootResp.TryGetProperty("data", out var rootData) ||
                !rootData.TryGetProperty("contraindicationRoot", out var rootEl))
                return null;
            var root = rootEl.GetString()!;

            var proofs = new List<ContraProof>();
            foreach (var s in substanceIds)
            {
                var resp = await client.GetFromJsonAsync<JsonElement>(
                    $"{mfssiaUrl}/contraindication/proof?substance={s}&drug={drugId}");
                if (!resp.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object) return null;
                proofs.Add(new ContraProof(
                    Value: d.GetProperty("value").GetInt32(),
                    Siblings: d.GetProperty("siblings").EnumerateArray().Select(x => x.GetString()!).ToArray(),
                    PathBits: d.GetProperty("pathBits").EnumerateArray().Select(x => x.GetInt32()).ToArray()
                ));
            }
            return new ContraindicationBundle(root, proofs.ToArray());
        }
        catch { return null; }
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

    // Lab measurement + lab-record membership proof from the MFSSIA lab-record registry.
    public record LabResultDto(
        string Metric, string MetricId, decimal Value, DateTime MeasuredAt,
        string[] Siblings, int[] PathBits);

    private record LabRecordBundle(string LabRecordRoot, LabResultDto[] Results);

    private record PolicyDto(
        string MedicationCode, string ClinicalCondition,
        string ComparisonOperator, decimal Threshold, decimal DeltaMax);

    private record CredentialProof(
        string CredentialHash, string ValidCredentialRoot,
        string[] Siblings, int[] PathBits);

    private record ZkpProveRequest(
        string DoctorCredentialUal, string? DoctorCredentialHash,
        string? ValidCredentialRoot, string[]? CredentialSiblings, int[]? CredentialPathBits,
        Guid PatientId, int[] DrugIds, string[] Dosages, int PatientAge, int WorkflowId,
        long PrescriptionIssuedAt,
        string[] Allergies,
        string[]? Substances, string? PatientRecordRoot,
        string[]? RefLeaf, string[][]? RefSiblings, int[][]? RefPathBits, int[]? RefIsActive,
        string? ContraindicationRoot, ContraProof[]? ContraProofs,
        string? LabRecordRoot, LabResultDto[] LabResults, PolicyDto[] Policies);

    private record PatientRecordProof(
        string[] Substances, int[] SubstanceIds, string PatientRecordRoot,
        string[] RefLeaf, string[][] RefSiblings, int[][] RefPathBits, int[] RefIsActive);

    private record PatientRecordEnvelope(PatientRecordProof? Data);

    // Per-allergy contraindication membership proof (aligned with patient-record substances).
    private record ContraProof(int Value, string[] Siblings, int[] PathBits);

    private record ContraindicationBundle(string ContraindicationRoot, ContraProof[] ContraProofs);

    private record ZkpResult(bool Outcome, string StmtHash, object Proof, string[]? PublicSignals, string[]? Reasons);
}

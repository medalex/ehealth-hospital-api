using System.Text.Json;
using EHealth.Hospital.Endpoints;

namespace EHealth.Hospital.Api.Tests;

// Guards the hospital-api ↔ lab-api JSON contract. The prescription flow reads
// lab results via GetFromJsonAsync<LabResultDto[]>; a type mismatch there throws
// and the catch silently returns an empty list, so lab policies (P6) never fire.
public class LabResultContractTests
{
    // Same behavior as ASP.NET Core's GetFromJsonAsync (camelCase, case-insensitive).
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // The exact shape lab-api emits: `formula` is a NUMERIC enum, plus extra fields
    // the consumer does not model. Two eGFR results + one unrelated metric.
    private const string LabApiJson = """
    [
      {
        "id": "04fded0d-a530-4267-bdc4-eae530214002",
        "patientId": "00000000-0000-0000-0000-000000000001",
        "loincCode": "33914-3",
        "metric": "eGFR",
        "formula": 0,
        "value": 20,
        "unit": "mL/min/1.73m²",
        "measuredBy": "City Lab",
        "measuredAt": "2026-06-25T14:35:38.8967262",
        "leafHash": "e8753c3a537c4575f9a5014ae39fab464cbdc40ae31b322cd4c549abb09ad1e6",
        "dkgUal": "did:dkg:hardhat1:31337/0xabc/5"
      },
      {
        "id": "11111111-0000-0000-0000-000000000000",
        "patientId": "00000000-0000-0000-0000-000000000001",
        "loincCode": "2164-2",
        "metric": "Creatinine Clearance",
        "formula": 1,
        "value": 52,
        "unit": "mL/min",
        "measuredBy": "City Lab",
        "measuredAt": "2026-06-20T09:00:00.0000000"
      },
      {
        "id": "22222222-0000-0000-0000-000000000000",
        "patientId": "00000000-0000-0000-0000-000000000001",
        "loincCode": "33914-3",
        "metric": "eGFR",
        "formula": 2,
        "value": 45,
        "unit": "mL/min/1.73m²",
        "measuredBy": "City Lab",
        "measuredAt": "2026-06-18T08:00:00.0000000"
      }
    ]
    """;

    [Fact]
    public void LabApiResponse_DeserializesAllResults_WithoutThrowing()
    {
        // Regression: a numeric `formula` (the lab-api enum) must not break the read.
        // The old DTO had `string Formula`, which threw here → empty lab list → P6 never blocked.
        var ex = Record.Exception(() =>
            JsonSerializer.Deserialize<PrescriptionEndpoints.LabResultDto[]>(LabApiJson, Web));
        Assert.Null(ex);
    }

    [Fact]
    public void LabApiResponse_PreservesMetricValueAndMeasuredAt()
    {
        var results = JsonSerializer.Deserialize<PrescriptionEndpoints.LabResultDto[]>(LabApiJson, Web);

        Assert.NotNull(results);
        Assert.Equal(3, results!.Length);

        var egfr = results[0];
        Assert.Equal("eGFR", egfr.Metric);
        Assert.Equal(20m, egfr.Value);
        Assert.Equal(2026, egfr.MeasuredAt.Year);
        Assert.Equal(6, egfr.MeasuredAt.Month);
        Assert.Equal(25, egfr.MeasuredAt.Day);
        Assert.NotEqual(default, egfr.MeasuredAt);
    }

    [Fact]
    public void MeasuredAt_DistinguishesResults_SoFreshestCanBeSelected()
    {
        var results = JsonSerializer.Deserialize<PrescriptionEndpoints.LabResultDto[]>(LabApiJson, Web)!;
        var egfrs = results.Where(r => r.Metric == "eGFR")
                           .OrderByDescending(r => r.MeasuredAt)
                           .ToArray();

        // The freshest eGFR (the contraindicating 20) must sort ahead of the older 45.
        Assert.Equal(20m, egfrs[0].Value);
        Assert.Equal(45m, egfrs[1].Value);
    }
}

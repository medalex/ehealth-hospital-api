namespace EHealth.Hospital.Models;

public class AllergyRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PatientId { get; set; }
    public string Substance { get; set; } = default!;  // e.g. "Penicillin"
    public string SnomedCode { get; set; } = default!; // e.g. "372687004"
    public string Source { get; set; } = default!;     // hospital / lab name
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
    public string? DkgUal { get; set; }
}

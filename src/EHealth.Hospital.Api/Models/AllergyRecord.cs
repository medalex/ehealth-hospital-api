namespace EHealth.Hospital.Models;

public class AllergyRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PatientId { get; set; }
    public string Substance { get; set; } = default!;  // e.g. "Penicillin"
    public string SnomedCode { get; set; } = default!; // code in CodeSystem, e.g. "372687004" or local "PCN-001"
    // Coding system the allergy is expressed in. "SNOMED-CT" is the governed/canonical
    // system (used as-is). Any other value (a lab's local allergen vocabulary) must be
    // reconciled to a governed rx concept via an rx:alignsTo axiom, or the prescription
    // flow raises a terminological conflict and escalates to the DAO.
    public string CodeSystem { get; set; } = "SNOMED-CT";
    public string Source { get; set; } = default!;     // hospital / lab name
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
    public string? DkgUal { get; set; }
}

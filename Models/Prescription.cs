namespace EHealth.Hospital.Models;

public class Prescription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DoctorId { get; set; }
    public Guid PatientId { get; set; }
    public int[] DrugIds { get; set; } = [];    // circuit: prescribedDrugIds
    public int[] Dosages { get; set; } = [];    // circuit: prescribedDosages
    public bool? Outcome { get; set; }           // ZKP result: true=accept, false=reject
    public string? StmtHash { get; set; }        // Poseidon commitment
    public string? ProofJson { get; set; }       // serialised Groth16 proof
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

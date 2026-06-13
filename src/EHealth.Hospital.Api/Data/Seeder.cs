using EHealth.Hospital.Models;

namespace EHealth.Hospital.Data;

public static class Seeder
{
    private static readonly Guid Pat1 = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static void Seed(AppDbContext db)
    {
        if (db.Doctors.Any()) return;

        db.Doctors.Add(new Doctor
        {
            Id = Guid.Parse("00000000-0000-0000-0002-000000000001"),
            FirstName = "James",
            LastName = "Wilson",
            Specialty = "General Practitioner",
            LicenseNumber = "MED-LIC-2024-001",
            // UAL предустановлен — в реальном демо перезаписывается через DKG регистрацию
            CredentialUal = Environment.GetEnvironmentVariable("SEED_DOCTOR_UAL")
                            ?? "did:dkg:hardhat2:31337/0x0000000000000000000000000000000000000001/1"
        });

        db.AllergyRecords.Add(new AllergyRecord
        {
            Id = Guid.Parse("00000000-0000-0000-0003-000000000001"),
            PatientId = Pat1,
            Substance = "Penicillin",
            SnomedCode = "372687004",
            Source = "St. Mary's Hospital",
            RecordedAt = DateTime.UtcNow.AddMonths(-3)
        });

        db.SaveChanges();
    }
}

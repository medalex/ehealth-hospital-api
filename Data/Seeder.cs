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
            FirstName = "Ivan",
            LastName = "Petrenko",
            Specialty = "General Practitioner",
            CredentialUal = null  // set after DKG registration
        });

        db.AllergyRecords.Add(new AllergyRecord
        {
            Id = Guid.Parse("00000000-0000-0000-0003-000000000001"),
            PatientId = Pat1,
            Substance = "Penicillin",
            SnomedCode = "372687004",
            Source = "City Hospital",
            RecordedAt = DateTime.UtcNow.AddMonths(-3)
        });

        db.SaveChanges();
    }
}

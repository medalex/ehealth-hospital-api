using EHealth.Hospital.Models;

namespace EHealth.Hospital.Data;

public static class Seeder
{
    private static readonly Guid Pat1 = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static void Seed(AppDbContext db)
    {
        if (db.Doctors.Any()) return;

        db.Doctors.AddRange(
            new Doctor
            {
                Id = Guid.Parse("00000000-0000-0000-0002-000000000001"),
                FirstName = "James",
                LastName = "Wilson",
                Specialty = "General Practitioner",
                LicenseNumber = "MED-LIC-2024-001",
                CredentialUal = Environment.GetEnvironmentVariable("SEED_DOCTOR_UAL")
            },
            new Doctor
            {
                Id = Guid.Parse("00000000-0000-0000-0002-000000000002"),
                FirstName = "Sarah",
                LastName = "Chen",
                Specialty = "Endocrinologist",
                LicenseNumber = "MED-LIC-2024-002",
                CredentialUal = null
            },
            new Doctor
            {
                Id = Guid.Parse("00000000-0000-0000-0002-000000000003"),
                FirstName = "Michael",
                LastName = "Roberts",
                Specialty = "Pulmonologist",
                LicenseNumber = "MED-LIC-2024-003",
                CredentialUal = null
            },
            // Doctor exists in hospital DB but NOT in MFSSIA registry — for blocking demo
            new Doctor
            {
                Id = Guid.Parse("00000000-0000-0000-0002-000000000099"),
                FirstName = "Alex",
                LastName = "Turner",
                Specialty = "Intern",
                LicenseNumber = "MED-LIC-2024-999",
                CredentialUal = null
            }
        );

        // Only doctors are seeded. Allergies are recorded live during the demo.

        db.SaveChanges();
    }
}

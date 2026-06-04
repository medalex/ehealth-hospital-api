using EHealth.Hospital.Models;
using Microsoft.EntityFrameworkCore;

namespace EHealth.Hospital.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Doctor> Doctors => Set<Doctor>();
    public DbSet<AllergyRecord> AllergyRecords => Set<AllergyRecord>();
    public DbSet<Prescription> Prescriptions => Set<Prescription>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Prescription>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.DrugIds).HasConversion(
                v => string.Join(',', v),
                v => v.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray());
            e.Property(p => p.Dosages).HasConversion(
                v => string.Join(',', v),
                v => v.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray());
        });
    }
}

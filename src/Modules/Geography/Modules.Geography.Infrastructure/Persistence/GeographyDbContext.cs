using Microsoft.EntityFrameworkCore;
using Modules.Geography.Domain;

namespace Modules.Geography.Infrastructure.Persistence;

public sealed class GeographyDbContext(DbContextOptions<GeographyDbContext> options)
    : DbContext(options)
{
    public DbSet<Department> Departments => Set<Department>();

    public DbSet<City> Cities => Set<City>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureDepartment(modelBuilder);
        ConfigureCity(modelBuilder);
    }

    private static void ConfigureDepartment(ModelBuilder modelBuilder)
    {
        var department = modelBuilder.Entity<Department>();
        department.ToTable("departments", "geography");
        department.HasKey(value => value.Id);
        department.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new DepartmentId(value))
            .ValueGeneratedNever();
        department.Property(value => value.DivipolaCode)
            .HasColumnName("divipola_code")
            .HasMaxLength(2)
            .IsRequired();
        department.Property(value => value.Name)
            .HasColumnName("name")
            .HasMaxLength(120)
            .IsRequired();
        department.HasIndex(value => value.DivipolaCode).IsUnique();
    }

    private static void ConfigureCity(ModelBuilder modelBuilder)
    {
        var city = modelBuilder.Entity<City>();
        city.ToTable("cities", "geography");
        city.HasKey(value => value.Id);
        city.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new CityId(value))
            .ValueGeneratedNever();
        city.Property(value => value.DivipolaCode)
            .HasColumnName("divipola_code")
            .HasMaxLength(8)
            .IsRequired();
        city.Property(value => value.Name)
            .HasColumnName("name")
            .HasMaxLength(120)
            .IsRequired();
        city.Property(value => value.DepartmentId)
            .HasColumnName("department_id")
            .HasConversion(id => id.Value, value => new DepartmentId(value));
        city.HasIndex(value => value.DivipolaCode).IsUnique();
        city.HasIndex(value => value.DepartmentId);
        city.HasOne<Department>()
            .WithMany()
            .HasForeignKey(value => value.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using DbSync.Core.Models;

namespace DbSync.Core.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    public DbSet<Cliente> Clientes => Set<Cliente>();
    public DbSet<ClienteAmbiente> ClienteAmbientes => Set<ClienteAmbiente>();
    public DbSet<ObjetoCustom> ObjetosCustom => Set<ObjetoCustom>();
    public DbSet<ObjetoBase> ObjetosBase => Set<ObjetoBase>();
    public DbSet<SyncHistory> SyncHistory => Set<SyncHistory>();
    public DbSet<UsuarioCliente> UsuarioClientes => Set<UsuarioCliente>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Cliente>(entity =>
        {
            entity.ToTable("Clientes");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Nombre).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Codigo).IsRequired().HasMaxLength(50);
            entity.HasIndex(e => e.Codigo).IsUnique();
        });

        modelBuilder.Entity<ClienteAmbiente>(entity =>
        {
            entity.ToTable("ClienteAmbientes");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Ambiente).HasConversion<string>().HasMaxLength(10);
            entity.Property(e => e.Server).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Database).IsRequired().HasMaxLength(200);
            entity.Property(e => e.UserId).HasMaxLength(100);
            entity.Property(e => e.PasswordEncrypted).HasMaxLength(500);
            entity.HasIndex(e => new { e.ClienteId, e.Ambiente }).IsUnique();
            entity.HasOne(e => e.Cliente)
                  .WithMany(c => c.Ambientes)
                  .HasForeignKey(e => e.ClienteId);
        });

        modelBuilder.Entity<ObjetoCustom>(entity =>
        {
            entity.ToTable("ObjetosCustom");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.NombreObjeto).IsRequired().HasMaxLength(300);
            entity.Property(e => e.TipoObjeto).IsRequired().HasMaxLength(10);
            entity.Property(e => e.Notas).HasMaxLength(500);
            entity.HasIndex(e => new { e.ClienteId, e.NombreObjeto, e.TipoObjeto }).IsUnique();
            entity.HasOne(e => e.Cliente)
                  .WithMany(c => c.ObjetosCustom)
                  .HasForeignKey(e => e.ClienteId);
        });

        modelBuilder.Entity<ObjetoBase>(entity =>
        {
            entity.ToTable("ObjetosBase");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.NombreObjeto).IsRequired().HasMaxLength(300);
            entity.Property(e => e.TipoObjeto).IsRequired().HasMaxLength(10);
            entity.Property(e => e.Notas).HasMaxLength(500);
            entity.HasIndex(e => new { e.ClienteId, e.NombreObjeto, e.TipoObjeto }).IsUnique()
                  .HasFilter(null);  // SQL Server: incluir NULLs en el índice
            entity.HasOne(e => e.Cliente)
                  .WithMany(c => c.ObjetosBase)
                  .HasForeignKey(e => e.ClienteId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SyncHistory>(entity =>
        {
            entity.ToTable("SyncHistory");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.AmbienteOrigen).HasConversion<string>().HasMaxLength(10);
            entity.Property(e => e.AmbienteDestino).HasConversion<string>().HasMaxLength(10);
            entity.Property(e => e.NombreObjeto).IsRequired().HasMaxLength(300);
            entity.Property(e => e.TipoObjeto).IsRequired().HasMaxLength(10);
            entity.Property(e => e.AccionRealizada).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Usuario).HasMaxLength(100);
            entity.HasIndex(e => e.FechaEjecucion);
            entity.HasIndex(e => e.ClienteId);
            entity.HasOne(e => e.Cliente)
                  .WithMany()
                  .HasForeignKey(e => e.ClienteId);
        });

        modelBuilder.Entity<UsuarioCliente>(entity =>
        {
            entity.ToTable("UsuarioClientes");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.UserId, e.ClienteId }).IsUnique();
            entity.HasOne(e => e.Usuario)
                  .WithMany(u => u.ClientesAsignados)
                  .HasForeignKey(e => e.UserId);
            entity.HasOne(e => e.Cliente)
                  .WithMany()
                  .HasForeignKey(e => e.ClienteId);
        });
    }
}

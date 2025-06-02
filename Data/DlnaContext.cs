using Microsoft.EntityFrameworkCore;
using FinDLNA.Models;
using System.Text.Json;

namespace FinDLNA.Data;

// MARK: DlnaContext
public class DlnaContext : DbContext
{
    public DlnaContext(DbContextOptions<DlnaContext> options) : base(options)
    {
    }

    public DbSet<DeviceProfile> DeviceProfiles { get; set; }
    public DbSet<DirectPlayProfile> DirectPlayProfiles { get; set; }
    public DbSet<TranscodingProfile> TranscodingProfiles { get; set; }
    public DbSet<ContainerProfile> ContainerProfiles { get; set; }
    public DbSet<CodecProfile> CodecProfiles { get; set; }

    // MARK: OnModelCreating
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<DeviceProfile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.UserAgent).HasMaxLength(500);
            entity.Property(e => e.Manufacturer).HasMaxLength(200);
            entity.Property(e => e.ModelName).HasMaxLength(200);
            entity.Property(e => e.ModelNumber).HasMaxLength(100);
            entity.Property(e => e.SerialNumber).HasMaxLength(100);
            entity.Property(e => e.FriendlyName).HasMaxLength(200);
            entity.Property(e => e.MaxAlbumArtWidth).HasMaxLength(20);
            entity.Property(e => e.MaxAlbumArtHeight).HasMaxLength(20);
            entity.Property(e => e.MaxIconWidth).HasMaxLength(20);
            entity.Property(e => e.MaxIconHeight).HasMaxLength(20);
            entity.Property(e => e.TimelineOffsetSeconds).HasMaxLength(20);
            
            entity.HasIndex(e => e.Name);
            entity.HasIndex(e => e.UserAgent);
            entity.HasIndex(e => e.IsActive);
        });

        modelBuilder.Entity<DirectPlayProfile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Container).HasMaxLength(100);
            entity.Property(e => e.AudioCodec).HasMaxLength(200);
            entity.Property(e => e.VideoCodec).HasMaxLength(200);
            entity.Property(e => e.Type).HasMaxLength(50);
            
            entity.HasOne(e => e.DeviceProfile)
                .WithMany(d => d.DirectPlayProfiles)
                .HasForeignKey(e => e.DeviceProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TranscodingProfile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Container).HasMaxLength(100);
            entity.Property(e => e.Type).HasMaxLength(50);
            entity.Property(e => e.VideoCodec).HasMaxLength(200);
            entity.Property(e => e.AudioCodec).HasMaxLength(200);
            entity.Property(e => e.Protocol).HasMaxLength(50);
            entity.Property(e => e.TranscodeSeekInfo).HasMaxLength(50);
            entity.Property(e => e.Context).HasMaxLength(50);
            
            entity.HasOne(e => e.DeviceProfile)
                .WithMany(d => d.TranscodingProfiles)
                .HasForeignKey(e => e.DeviceProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ContainerProfile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Type).HasMaxLength(50);
            entity.Property(e => e.Container).HasMaxLength(100);
            
            entity.HasOne(e => e.DeviceProfile)
                .WithMany(d => d.ContainerProfiles)
                .HasForeignKey(e => e.DeviceProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CodecProfile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Type).HasMaxLength(50);
            entity.Property(e => e.Codec).HasMaxLength(100);
            entity.Property(e => e.Container).HasMaxLength(100);
            
            entity.HasOne(e => e.DeviceProfile)
                .WithMany(d => d.CodecProfiles)
                .HasForeignKey(e => e.DeviceProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
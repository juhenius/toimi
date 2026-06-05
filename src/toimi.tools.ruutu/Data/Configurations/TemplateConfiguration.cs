using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using toimi.tools.ruutu.Data.Entities;

namespace toimi.tools.ruutu.Data.Configurations;

public class TemplateConfiguration : IEntityTypeConfiguration<Template>
{
  public void Configure(EntityTypeBuilder<Template> builder)
  {
    builder.HasKey(t => t.Id);

    builder.HasIndex(t => t.Name).IsUnique();

    builder.Property(t => t.Name).IsRequired();
    builder.Property(t => t.Description).IsRequired();
    builder.Property(t => t.SchemaJson).HasColumnType("jsonb").IsRequired();

    builder.Property(t => t.CreatedAt).HasDefaultValueSql("now()");
    builder.Property(t => t.UpdatedAt).HasDefaultValueSql("now()");
  }
}

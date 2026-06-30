using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace toimi.tools.tietue.Data;

public class TypeDefinitionConfiguration : IEntityTypeConfiguration<TypeDefinition>
{
  public void Configure(EntityTypeBuilder<TypeDefinition> builder)
  {
    builder.ToTable("type_definitions");
    builder.HasKey(t => t.Name);

    builder.Property(t => t.Name)
      .IsRequired();

    builder.Property(t => t.JsonSchema)
      .HasColumnType("jsonb")
      .HasConversion(
        v => v.RootElement.GetRawText(),
        v => JsonDocument.Parse(v))
      .IsRequired();

    builder.Property(t => t.Behaviors)
      .HasColumnType("jsonb");

    builder.Property(t => t.DefaultTriggers)
      .HasColumnType("jsonb");

    builder.Property(t => t.CreatedAt)
      .HasDefaultValueSql("now()");

    builder.Property(t => t.UpdatedAt)
      .HasDefaultValueSql("now()");
  }
}

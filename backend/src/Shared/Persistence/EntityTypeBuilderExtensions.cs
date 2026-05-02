using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hook.Shared.Persistence;

public static class EntityTypeBuilderExtensions
{
    public static PropertyBuilder PropertyAsStringEnum<TEntity, TEnum>(
        this EntityTypeBuilder<TEntity> builder,
        Expression<Func<TEntity, TEnum>> propertyExpression,
        int maxLength)
        where TEntity : class
        where TEnum : struct, Enum
    {
        return builder.Property(propertyExpression)
            .HasConversion<string>()
            .HasMaxLength(maxLength);
    }

    public static PropertyBuilder<TProp> HasJsonbArray<TEntity, TProp>(
        this EntityTypeBuilder<TEntity> builder,
        Expression<Func<TEntity, TProp>> propertyExpression)
        where TEntity : class
    {
        return builder.Property(propertyExpression)
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'[]'::jsonb");
    }
}

using System.Reflection;
using System.Runtime.CompilerServices;
using Hook.Shared.Domain;
using Shouldly;

namespace Hook.UnitTests.Architecture;

public class AggregateRulesTests
{
    // Walk every public IAggregateRoot type in the production assembly and assert
    // structural invariants. Catches regressions at PR time so a new entity with
    // public setters or no factory cannot land silently.
    private static IReadOnlyList<Type> AggregateTypes() =>
        typeof(Hook.Program).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && typeof(IAggregateRoot).IsAssignableFrom(t))
            .ToList();

    [Fact]
    public void AggregateTypes_NotEmpty()
    {
        // Hard floor — every other test below silently passes on an empty set.
        // Catches a future reflection refactor that breaks the discovery query.
        AggregateTypes().Count.ShouldBeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public void EveryAggregateRoot_HasPublicStaticFactoryReturningSelf()
    {
        var offenders = new List<string>();
        foreach (var t in AggregateTypes())
        {
            var taskOfT = typeof(Task<>).MakeGenericType(t);
            var hasFactory = t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                // Skip helpers like Combine(T, T) → T whose first arg is T itself —
                // those are aggregate operations, not factories.
                .Where(m => m.GetParameters() is var ps
                            && (ps.Length == 0 || ps[0].ParameterType != t))
                .Any(m => m.ReturnType == t || m.ReturnType == taskOfT);
            if (!hasFactory)
                offenders.Add($"{t.FullName} — add a `public static {t.Name} <Action>(...)` factory.");
        }
        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void EveryAggregateRoot_HasNoPublicInstanceSetters()
    {
        var offenders = new List<string>();
        foreach (var t in AggregateTypes())
        {
            // No DeclaredOnly — inherited public setters from a base aggregate must
            // also fail the rule, not slip through the inheritance gap.
            foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var setter = prop.SetMethod;
                if (setter is null) continue;
                if (!setter.IsPublic) continue;
                if (IsInitOnly(setter)) continue;
                offenders.Add($"{t.FullName}.{prop.Name} — make setter `private set` or `init`.");
            }
        }
        offenders.ShouldBeEmpty();
    }

    private static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter
            .GetRequiredCustomModifiers()
            .Any(m => m == typeof(IsExternalInit));
}

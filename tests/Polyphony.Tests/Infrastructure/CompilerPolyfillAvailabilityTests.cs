using System.Runtime.CompilerServices;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure;

// ── Trivial union defined at file scope to verify compiler polyfill availability ──
// If these types compile, UnionAttribute and IUnion are transitively available
// from the Twig.Domain project reference — no local CompilerPolyfill.cs needed.

public sealed record PolyfillCaseA;
public sealed record PolyfillCaseB(string Value);

public union PolyfillTestUnion(PolyfillCaseA, PolyfillCaseB);

/// <summary>
/// Verifies that the C# 15 <c>union</c> keyword compiles and works correctly
/// when the compiler polyfill types (<see cref="UnionAttribute"/>,
/// <see cref="IUnion"/>) are provided transitively from the Twig.Domain reference.
/// </summary>
public sealed class CompilerPolyfillAvailabilityTests
{
    [Fact]
    public void Union_type_is_decorated_with_UnionAttribute()
    {
        var attr = Attribute.GetCustomAttribute(
            typeof(PolyfillTestUnion), typeof(UnionAttribute));

        attr.ShouldNotBeNull();
        attr.ShouldBeOfType<UnionAttribute>();
    }

    [Fact]
    public void Union_type_implements_IUnion()
    {
        typeof(IUnion).IsAssignableFrom(typeof(PolyfillTestUnion)).ShouldBeTrue();
    }

    [Fact]
    public void Union_can_be_constructed_from_case_A()
    {
        PolyfillTestUnion union = new PolyfillCaseA();

        IUnion asInterface = union;
        asInterface.Value.ShouldBeOfType<PolyfillCaseA>();
    }

    [Fact]
    public void Union_can_be_constructed_from_case_B()
    {
        PolyfillTestUnion union = new PolyfillCaseB("hello");

        IUnion asInterface = union;
        var inner = asInterface.Value.ShouldBeOfType<PolyfillCaseB>();
        inner.Value.ShouldBe("hello");
    }

    [Fact]
    public void Union_supports_exhaustive_switch_pattern_matching()
    {
        PolyfillTestUnion union = new PolyfillCaseB("matched");

        IUnion asUnion = union;
        var result = asUnion.Value switch
        {
            PolyfillCaseA => "a",
            PolyfillCaseB b => b.Value,
            _ => throw new InvalidOperationException("Unexpected case"),
        };

        result.ShouldBe("matched");
    }

    [Fact]
    public void Union_Value_property_returns_inner_case()
    {
        var inner = new PolyfillCaseB("inner");
        PolyfillTestUnion union = inner;

        IUnion asInterface = union;
        asInterface.Value.ShouldBe(inner);
    }
}

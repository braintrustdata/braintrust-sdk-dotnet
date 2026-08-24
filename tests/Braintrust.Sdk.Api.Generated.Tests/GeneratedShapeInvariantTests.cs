using System.Reflection;
using System.Text.RegularExpressions;
using Braintrust.Sdk.Api.Generated;
using Xunit;

namespace Braintrust.Sdk.Api.Generated.Tests;

/// <summary>
/// Invariants over the whole generated surface, asserted by reflection rather than by
/// listing schemas - so they keep holding as the spec grows. Each one corresponds to a
/// rule in build/SpecNormalizer.cs; if a spec bump defeats a rule, the failure lands
/// here instead of on a caller at runtime.
/// </summary>
public class GeneratedShapeInvariantTests
{
    static readonly Type[] GeneratedTypes = typeof(BraintrustGeneratedApiClient).Assembly
        .GetExportedTypes()
        .Where(t => t.IsClass && t.Namespace == typeof(BraintrustGeneratedApiClient).Namespace)
        .ToArray();

    static bool IsPolymorphicBase(Type t) =>
        CustomAttributeData.GetCustomAttributes(t)
            .Any(a => a.AttributeType.Name.StartsWith("JsonInheritance", StringComparison.Ordinal));

    static IEnumerable<PropertyInfo> DataMembers(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.Name != "AdditionalProperties");

    [Fact]
    public void No_type_is_an_untyped_pass_through_bag()
    {
        // NSwag renders a union it cannot represent as a class with nothing but
        // [JsonExtensionData], which can hold neither a scalar nor an array - so every
        // form of the value fails to deserialize. Rules 6, 12 and 9 exist to ensure no
        // such class is emitted. A polymorphic base legitimately has no members of its
        // own, as does a variant whose only property was the discriminator.
        var bags = GeneratedTypes
            .Where(t => !t.IsAbstract && !DataMembers(t).Any())
            .Where(t => !IsPolymorphicBase(t))
            .Where(t => t.BaseType == typeof(object))
            .Where(t => !t.Name.EndsWith("Exception", StringComparison.Ordinal))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Empty(bags);
    }

    [Fact]
    public void No_type_name_is_a_generator_fallback()
    {
        // Unnamed inline schemas come out as Anonymous, or as Name2/Name3 alongside an
        // existing Name. Rules 10 and 11 give them real names. Matching the pair (Name
        // and Name2 both present) rather than "ends in a digit" keeps a legitimately
        // numbered name such as Sha256 from tripping this.
        var names = GeneratedTypes.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var fallbacks = names
            .Where(n => n.StartsWith("Anonymous", StringComparison.Ordinal)
                        || (Regex.Match(n, @"^(.*?)([2-9]|\d{2,})$") is { Success: true } m
                            && names.Contains(m.Groups[1].Value)))
            .OrderBy(n => n)
            .ToArray();

        Assert.Empty(fallbacks);
    }

    [Fact]
    public void No_type_is_named_after_a_bare_json_kind()
    {
        // A union arm titled "object" produced a class called Object, shadowing
        // System.Object for anything written in the generated namespace. These names say
        // nothing about the type either way, so no rule may derive one.
        //
        // Names the *spec* declares are left alone even when they shadow something
        // (Action, Environment are real Braintrust concepts, and renaming them would
        // make the client disagree with the API); only names our rules invent are checked.
        var kindWords = new[]
        {
            "Object", "String", "Array", "Number", "Integer", "Boolean",
            "Null", "Value", "Type", "Enum", "Tuple",
        };

        var named = GeneratedTypes.Select(t => t.Name).Intersect(kindWords, StringComparer.Ordinal)
            .OrderBy(n => n).ToArray();

        Assert.Empty(named);
    }

    [Fact]
    public void Every_union_wrapper_has_a_converter_and_at_least_two_arms()
    {
        // The wrapper's accessors are found by reflection at runtime, so a wrapper
        // without its converter would silently serialize as {"asString": ...}.
        var wrappers = GeneratedTypes
            .Where(t => DataMembers(t).Any() && DataMembers(t).All(p => p.Name.StartsWith("As", StringComparison.Ordinal)))
            .ToArray();

        Assert.NotEmpty(wrappers);

        foreach (var wrapper in wrappers)
        {
            var converter = CustomAttributeData.GetCustomAttributes(wrapper)
                .FirstOrDefault(a => a.AttributeType == typeof(System.Text.Json.Serialization.JsonConverterAttribute));

            Assert.NotNull(converter);
            Assert.Equal(typeof(JsonKindUnionConverter<>), ((Type)converter!.ConstructorArguments[0].Value!).GetGenericTypeDefinition());
            Assert.True(DataMembers(wrapper).Count() >= 2, $"{wrapper.Name} has fewer than two arms");
        }
    }

    [Fact]
    public void Every_polymorphic_base_has_variants_and_declares_no_members()
    {
        // The patched converter materializes an unknown variant as the base and fills
        // only its extension data, which is lossless exactly while bases stay memberless.
        var bases = GeneratedTypes.Where(IsPolymorphicBase).ToArray();

        Assert.NotEmpty(bases);

        foreach (var baseType in bases)
        {
            Assert.Empty(DataMembers(baseType));
            Assert.NotEmpty(GeneratedTypes.Where(t => t.BaseType == baseType));
        }
    }
}

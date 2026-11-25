using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Quasar.Components.Shared;

/// <summary>
/// Recognizes the compiled-in Quasar icon constants. Custom SVG supplied by a
/// plugin remains untrusted unless the caller explicitly opts in.
/// </summary>
internal static class QuasarIconTrust
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicFields, typeof(QuasarIcons))]
    private static HashSet<string> BuildTrusted() =>
        typeof(QuasarIcons)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string))
            .Select(field => (string?)field.GetValue(null))
            .Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal)!;

    private static readonly HashSet<string> Trusted = BuildTrusted();

    public static bool IsTrusted(string? value) => value is not null && Trusted.Contains(value);
}

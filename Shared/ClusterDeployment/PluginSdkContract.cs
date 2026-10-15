using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Quasar.ClusterDeployment;

/// <summary>
/// Magnetar's PluginSdk contract version, <c>major.minor</c>, declared by
/// <c>[assembly: AssemblyMetadata("PluginSdkContract", "1.0")]</c> in PluginSdk.dll.
/// The minor goes up when public API is added, the major when API is removed or changed.
/// </summary>
internal readonly record struct PluginSdkContract(int Major, int Minor)
{
    internal const string AttributeKey = "PluginSdkContract";

    public override string ToString() => $"{Major}.{Minor}";

    /// <summary>A plugin built against this contract runs on <paramref name="actual"/>.</summary>
    internal bool IsSatisfiedBy(PluginSdkContract actual) =>
        actual.Major == Major && actual.Minor >= Minor;

    internal static PluginSdkContract Parse(string? text)
    {
        string[] parts = text?.Split('.') ?? [];
        if (
            parts.Length == 2
            && int.TryParse(
                parts[0],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int major
            )
            && int.TryParse(
                parts[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int minor
            )
        )
            return new(major, minor);
        throw new InvalidDataException($"Invalid PluginSdk contract version '{text}'.");
    }

    /// <summary>
    /// Reads the contract from the assembly metadata without loading the DLL, which references
    /// game assemblies Quasar doesn't have. Null when the DLL declares no contract (Magnetar
    /// releases before the contract was introduced).
    /// </summary>
    internal static PluginSdkContract? Read(string dllPath)
    {
        using var stream = File.OpenRead(dllPath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
                continue;
            var parent = metadata
                .GetMemberReference((MemberReferenceHandle)attribute.Constructor)
                .Parent;
            if (parent.Kind != HandleKind.TypeReference)
                continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)parent);
            if (
                !metadata.StringComparer.Equals(type.Name, "AssemblyMetadataAttribute")
                || !metadata.StringComparer.Equals(type.Namespace, "System.Reflection")
            )
                continue;
            // Blob: prolog 0x0001, then the (key, value) constructor arguments as SerStrings.
            var blob = metadata.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1 || blob.ReadSerializedString() != AttributeKey)
                continue;
            return Parse(blob.ReadSerializedString());
        }
        return null;
    }
}

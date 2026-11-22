using System.Text.Json;
using System.Text.Json.Serialization;

namespace ViewerFixture;

[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
internal partial class FixtureJson : JsonSerializerContext
{
    // Installed UI components need these overloads even when the host never calls them.
    internal static T RoundTrip<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, typeof(T), Default);
        return (T)JsonSerializer.Deserialize(json, typeof(T), Default)!;
    }
}

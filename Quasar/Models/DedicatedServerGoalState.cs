using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quasar.Models;

[JsonConverter(typeof(DedicatedServerGoalStateConverter))]
public enum DedicatedServerGoalState
{
    Off = 0,
    On = 1,
}

// server.json stores the goal as a number, which older Quasar releases can still read after a
// downgrade. Names are accepted on read because pre-release cluster builds wrote them. The
// cluster API opts into names per property with JsonStringEnumConverter.
public sealed class DedicatedServerGoalStateConverter : JsonConverter<DedicatedServerGoalState>
{
    public override DedicatedServerGoalState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int number) && Enum.IsDefined((DedicatedServerGoalState)number))
            return (DedicatedServerGoalState)number;
        if (reader.TokenType == JsonTokenType.String
            && Enum.TryParse(reader.GetString(), ignoreCase: true, out DedicatedServerGoalState named) && Enum.IsDefined(named))
            return named;
        throw new JsonException("Goal state must be Off (0) or On (1).");
    }

    public override void Write(Utf8JsonWriter writer, DedicatedServerGoalState value, JsonSerializerOptions options) =>
        writer.WriteNumberValue((int)value);
}

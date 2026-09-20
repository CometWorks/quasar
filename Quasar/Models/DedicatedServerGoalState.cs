using System.Text.Json.Serialization;

namespace Quasar.Models;

[JsonConverter(typeof(JsonStringEnumConverter<DedicatedServerGoalState>))]
public enum DedicatedServerGoalState
{
    Off = 0,
    On = 1,
}

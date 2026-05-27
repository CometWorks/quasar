namespace Quasar.Models;

public sealed class QuasarWorldProfile
{
    public string WorldProfileId { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

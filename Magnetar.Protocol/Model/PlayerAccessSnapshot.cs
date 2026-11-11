using System;
using System.Collections.Generic;

namespace Magnetar.Protocol.Model;

/// <summary>Expiring access decisions, delivered only over the authenticated control connection.</summary>
public class PlayerAccessSnapshot
{
    public const int ProtocolVersion = 1;
    public int Version { get; set; } = ProtocolVersion;
    public string Target { get; set; } = string.Empty;
    public long Revision { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Enabled { get; set; }
    public bool Whitelist { get; set; }
    public List<PlayerAccessGrant> Players { get; set; } = new List<PlayerAccessGrant>();
}

public class PlayerAccessGrant
{
    public ulong SteamId { get; set; }
    public bool Allowed { get; set; }
    public bool Confirmed { get; set; }
    public bool Priority { get; set; }
    /// <summary>None, Scripter, Moderator, SpaceMaster, Admin. Owner is never assignable.</summary>
    public int Promotion { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

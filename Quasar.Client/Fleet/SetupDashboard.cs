using Quasar.Services;

namespace Quasar.Client.Fleet;

public sealed record SetupMachine(HostRegistration Host, bool Connected);
public sealed record SetupProfile(string Id, string Name, string[] PendingDefaults);
public sealed record SetupLookup(string? Id = null, Guid? Attempt = null);
public sealed record SetupSubmission(ClusterSetupRequest Request, Guid Attempt);
public sealed record SetupRegistration(string Id, string Name, string Url, string CredentialReference);
public sealed record SetupSnapshot(SetupMachine[] Machines, ConversionDestination[] Worlds, SetupProfile[] Profiles,
    string? HostError, bool Enroll, ClusterSetupStatus? Status, ClusterSetupRequest? ActiveRequest,
    bool Registered, string? ProgressMessage, int? Percent, string? AttemptError);

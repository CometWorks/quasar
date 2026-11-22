namespace Quasar.Services;

public enum ExistingServerKind
{
    Vanilla,
    Torch,
}

public enum ExistingServerTransferMode
{
    Copy,
    Move,
}

[Flags]
public enum ExistingServerImportSections
{
    None = 0,
    Identity = 1 << 0,
    Network = 1 << 1,
    ServerSettings = 1 << 2,
    AccessLists = 1 << 3,
    SessionSettings = 1 << 4,
    Mods = 1 << 5,
    TorchLifecycle = 1 << 6,
}

namespace Quasar.Plugin.Abstractions.Navigation;

public sealed record QuasarNavItem(
    string Text,
    string Href,
    string Icon,
    string Zone,
    int Order,
    string? Policy);

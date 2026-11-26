using Microsoft.AspNetCore.Components;

namespace Quasar.Ui.Components;

/// <summary>One tab rendered by <see cref="QuasarTabs"/>.</summary>
/// <param name="Id">Stable identifier used for the active tab and ARIA wiring.</param>
/// <param name="Title">Visible tab label.</param>
/// <param name="Content">Panel content.</param>
public sealed record QuasarTabItem(string Id, string Title, RenderFragment Content);

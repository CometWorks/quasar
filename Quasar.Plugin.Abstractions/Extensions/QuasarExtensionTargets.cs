namespace Quasar.Plugin.Abstractions.Extensions;

public static class QuasarExtensionTargets
{
    public const string DashboardPanels = "quasar.dashboard.panels";
    public const string EntityActions = "quasar.component.entity-actions";
    public const string EntityViewerColumnHeader = "quasar.entities.viewer-column-header";
    public const string EntityViewerColumnCell = "quasar.entities.viewer-column-cell";
    public const string EntityDetailsTabs = "quasar.component.entity-details-tabs";
    public const string ServerDetailActions = "quasar.component.server-detail-actions";
    public const string EntitiesPage = "quasar.page.entities";
    public const string PluginsPage = "quasar.page.plugins";
    public const string AnalyticsPage = "quasar.page.analytics";

    /// <summary>Rendered once on every page, for a drawer such as the assistant chat. The component must declare both
    /// parameters: <c>PageUri</c> (string) and <c>ServerName</c> (string?, the server or cluster unique name on its pages).</summary>
    public const string LayoutDrawer = "quasar.layout.drawer";
}

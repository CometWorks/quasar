using System.Globalization;
using Magnetar.Protocol.Model;
using MudBlazor;
using Quasar.Components.Pages;

namespace Quasar.Services;

public sealed class EntityViewerDialogService
{
    private readonly IDialogService _dialogService;

    public EntityViewerDialogService(IDialogService dialogService)
    {
        _dialogService = dialogService;
    }

    public async Task ShowAsync(AgentRuntimeState agent, string serverName, EntitySummary entity)
    {
        var request = new EntityViewerDialogRequest(
            agent.AgentId,
            entity.EntityId,
            entity.TypeTag,
            entity.DisplayName,
            serverName);

        await ShowAsync(request);
    }

    public async Task ShowAsync(EntityViewerDialogRequest request)
    {
        var title = string.IsNullOrWhiteSpace(request.EntityDisplayName)
            ? $"Entity {request.EntityId}"
            : request.EntityDisplayName;
        var subtitle = $"{request.ServerName} - {request.EntityType} #{request.EntityId}";
        var parameters = new DialogParameters
        {
            [nameof(EntityViewerDialog.ViewerUrl)] = BuildViewerUrl(request.AgentId, request.EntityId, request.EntityType),
            [nameof(EntityViewerDialog.Title)] = title,
            [nameof(EntityViewerDialog.Subtitle)] = subtitle,
        };
        var options = new DialogOptions
        {
            CloseOnEscapeKey = true,
            FullScreen = true,
        };

        await _dialogService.ShowAsync<EntityViewerDialog>(title, parameters, options);
    }

    public static string BuildViewerUrl(string agentId, long entityId, string entityType)
    {
        var parts = new List<string>
        {
            $"agentId={Uri.EscapeDataString(agentId)}",
            $"entityId={entityId.ToString(CultureInfo.InvariantCulture)}",
            "voxels=1",
        };
        if (string.Equals(entityType, "Grid", StringComparison.OrdinalIgnoreCase))
            parts.Add("context=1");

        return "/viewer/entity?" + string.Join("&", parts);
    }
}

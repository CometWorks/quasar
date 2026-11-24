using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Quasar.Components.Shared;

namespace Quasar.Tests;

/// <summary>
/// Emulates the browser half of the native dialog host in bUnit.
/// </summary>
/// <remarks>
/// <para>
/// The production host asks <c>quasar-ui.js</c> to show or close a
/// <c>&lt;dialog&gt;</c>. The browser later raises the native <c>close</c>
/// event, which calls <see cref="QuasarDialogInstanceHost.OnNativeClosedAsync"/>.
/// bUnit has no browser event loop, so a close request would otherwise leave the
/// dialog service waiting forever.
/// </para>
/// <para>
/// This runtime records the .NET callback supplied by each show call and invokes
/// it after a matching close call. It still forwards the original invocation to
/// the wrapped runtime, so tests can assert the expected JS calls.
/// </para>
/// </remarks>
internal sealed class QuasarDialogJsRuntime(IJSRuntime inner) : IJSRuntime
{
    private readonly ConcurrentDictionary<string, DotNetObjectReference<QuasarDialogInstanceHost>> _hosts = new();

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, default, args);

    public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        var result = await inner.InvokeAsync<TValue>(identifier, cancellationToken, args);

        if (identifier == "quasarUi.dialogs.show" &&
            args is [ElementReference element, _, DotNetObjectReference<QuasarDialogInstanceHost> host])
        {
            _hosts[element.Id] = host;
        }
        else if (identifier == "quasarUi.dialogs.close" && args is [ElementReference closing])
        {
            if (_hosts.TryRemove(closing.Id, out var closingHost))
                await closingHost.Value.OnNativeClosedAsync("close");
        }
        else if (identifier == "quasarUi.dialogs.dispose" && args is [ElementReference disposed])
        {
            _hosts.TryRemove(disposed.Id, out _);
        }

        return result;
    }
}

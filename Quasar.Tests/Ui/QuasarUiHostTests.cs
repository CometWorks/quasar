using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.JSInterop;
using Quasar.Client.Ui;
using Quasar.Components.Shared;
using Quasar.Ui;
using Xunit;

namespace Quasar.Tests.Ui;

public sealed class QuasarUiHostTests
{
    [Fact]
    public async Task DialogHostRendersNativeDialogAndCallsShow()
    {
        using var context = CreateContext();
        var cut = context.Render<QuasarDialogHost>();
        var dialogs = context.Services.GetRequiredService<IQuasarDialogService>();

        var reference = await dialogs.ShowAsync<HostTestDialog>("Host dialog");
        cut.WaitForAssertion(() =>
        {
            var dialog = cut.Find("dialog");
            Assert.Equal("Host dialog", dialog.GetAttribute("aria-label"));
            Assert.Contains("quasar-dialog", dialog.ClassList);
            Assert.Contains("<p>Host dialog content</p>", cut.Markup);
        });
        Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "quasarUi.dialogs.show");

        reference.Close(QuasarDialogResult.Ok("done"));
        cut.WaitForAssertion(() =>
            Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "quasarUi.dialogs.close"));

        // The JS runtime bridge raises the native close event from the close
        // invocation, so the host removes the dialog without a manual callback.
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("dialog")));
        var result = await reference.Result;
        Assert.False(result.Canceled);
        Assert.Equal("done", result.Data);
    }

    [Fact]
    public void DialogHostAppliesOptions()
    {
        using var context = CreateContext();
        var cut = context.Render<QuasarDialogHost>();
        var dialogs = context.Services.GetRequiredService<IQuasarDialogService>();

        _ = cut.InvokeAsync(() => dialogs.ShowAsync<HostTestDialog>("Custom",
            options: new QuasarDialogOptions
            {
                Class = "custom-dialog",
                Style = "--quasar-dialog-radius: 12px;",
                MaxWidth = QuasarDialogWidth.Large,
                FullWidth = true,
            }));

        cut.WaitForAssertion(() =>
        {
            var dialog = cut.Find("dialog");
            Assert.Contains("custom-dialog", dialog.ClassList);
            Assert.Contains("quasar-dialog-width-lg", dialog.ClassList);
            Assert.Contains("quasar-dialog-full-width", dialog.ClassList);
            Assert.Contains("--quasar-dialog-radius", dialog.GetAttribute("style"));
        });
    }

    [Fact]
    public void ToastHostRendersSeverityRolesAndDismisses()
    {
        using var context = CreateContext();
        var cut = context.Render<QuasarToastHost>();
        var toasts = context.Services.GetRequiredService<IQuasarToastService>();

        toasts.Add("A success", QuasarSeverity.Success, options => options.Duration = TimeSpan.FromMinutes(1));
        toasts.Add("A failure", QuasarSeverity.Error, options => options.Duration = TimeSpan.FromMinutes(1));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, cut.FindAll(".quasar-toast").Count);
            Assert.Single(cut.FindAll("[role='status']"));
            Assert.Single(cut.FindAll("[role='alert']"));
            var text = string.Join(" | ", cut.FindAll(".quasar-toast").Select(element => element.TextContent));
            Assert.Contains("A success", text);
            Assert.Contains("A failure", text);
        }, TimeSpan.FromSeconds(5));

        var items = cut.FindComponents<QuasarToastItem>();
        Assert.Equal(0, items[0].Instance.Order);
        Assert.Equal(-1, items[1].Instance.Order);

        // DOM order is oldest first; the failure toast is the second one.
        cut.FindAll("button[aria-label='Dismiss notification']")[1].Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".quasar-toast"));
            Assert.Contains("A success", cut.Markup);
        });
    }

    [Fact]
    public async Task NativeDismissWithoutCloseRequestCancelsResult()
    {
        using var context = CreateContext();
        var cut = context.Render<QuasarDialogHost>();
        var dialogs = context.Services.GetRequiredService<IQuasarDialogService>();

        var reference = await dialogs.ShowAsync<HostTestDialog>("Dismissed");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("dialog")));

        await cut.InvokeAsync(() =>
            cut.FindComponent<QuasarDialogInstanceHost>().Instance.OnNativeClosedAsync("dismissed"));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("dialog")));
        Assert.True((await reference.Result).Canceled);
    }

    [Fact]
    public async Task FailedShowCompletesDialogAsCanceled()
    {
        using var context = CreateContext();
        var cut = context.Render<QuasarDialogHost>();
        var dialogs = context.Services.GetRequiredService<IQuasarDialogService>();
        context.JSInterop.SetupVoid("quasarUi.dialogs.show", _ => true).SetException(new JSException("boom"));

        var reference = await dialogs.ShowAsync<HostTestDialog>("Broken");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("dialog")));
        Assert.True((await reference.Result).Canceled);
    }

    [Fact]
    public async Task DialogHostForwardsOptionsToJavaScript()
    {
        using var context = CreateContext();
        var cut = context.Render<QuasarDialogHost>();
        var dialogs = context.Services.GetRequiredService<IQuasarDialogService>();

        _ = await cut.InvokeAsync(() => dialogs.ShowAsync<HostTestDialog>("Options",
            options: new QuasarDialogOptions
            {
                Modal = false,
                Draggable = false,
                Resizable = false,
                CloseOnEscapeKey = false,
                BackdropClick = false,
                LockScroll = false,
                DefaultFocus = QuasarDialogFocus.Dialog,
            }));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("dialog")));
        var show = context.JSInterop.Invocations.Last(invocation => invocation.Identifier == "quasarUi.dialogs.show");
        var payload = show.Arguments[1]!;
        var type = payload.GetType();

        Assert.Equal(false, type.GetProperty("modal")!.GetValue(payload));
        Assert.Equal(false, type.GetProperty("draggable")!.GetValue(payload));
        Assert.Equal(false, type.GetProperty("resizable")!.GetValue(payload));
        Assert.Equal(false, type.GetProperty("closeOnEscapeKey")!.GetValue(payload));
        Assert.Equal(false, type.GetProperty("backdropClick")!.GetValue(payload));
        Assert.Equal(false, type.GetProperty("lockScroll")!.GetValue(payload));
        Assert.Equal(true, type.GetProperty("focusDialog")!.GetValue(payload));
    }

    [Fact]
    public async Task DialogClosedDuringInitializationIsRemoved()
    {
        using var context = CreateContext();
        var cut = context.Render<QuasarDialogHost>();
        var dialogs = context.Services.GetRequiredService<IQuasarDialogService>();

        var reference = await dialogs.ShowAsync<SelfClosingDialog>("Self-closing");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("dialog")));
        Assert.True((await reference.Result).Canceled);
    }

    [Fact]
    public void ToastHostRendersAtMostMaxVisibleToasts()
    {
        using var context = new QuasarBunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.Replace(ServiceDescriptor.Singleton(new QuasarToastHostOptions
        {
            MaxVisibleToasts = 1,
            NewestOnTop = false,
        }));

        var cut = context.Render<QuasarToastHost>();
        var toasts = context.Services.GetRequiredService<IQuasarToastService>();
        toasts.Add("One", QuasarSeverity.Normal, options => options.Duration = TimeSpan.FromMinutes(1));
        toasts.Add("Two", QuasarSeverity.Normal, options => options.Duration = TimeSpan.FromMinutes(1));

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".quasar-toast"));
            Assert.Contains("Two", cut.Markup);
        });
    }

    [Fact]
    public void ToastIconOverrideIsNotTrustedMarkup()
    {
        using var context = CreateContext();
        var cut = context.Render<QuasarToastHost>();
        var toasts = context.Services.GetRequiredService<IQuasarToastService>();

        toasts.Add("Icon", QuasarSeverity.Normal, options =>
        {
            options.Duration = TimeSpan.FromMinutes(1);
            options.Icon = "</svg><img src=x onerror=alert(1)>";
        });

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".quasar-toast")));
        Assert.DoesNotContain("<img", cut.Markup);
    }

    [Fact]
    public void ToastActionLabelWithoutCallbackDoesNotRender()
    {
        using var context = CreateContext();
        var cut = context.Render<QuasarToastHost>();
        var toasts = context.Services.GetRequiredService<IQuasarToastService>();

        toasts.Add("No callback", QuasarSeverity.Normal, options =>
        {
            options.Duration = TimeSpan.FromMinutes(1);
            options.ActionText = "Do something";
        });

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".quasar-toast")));
        Assert.Empty(cut.FindAll(".quasar-toast-action"));
    }

    [Fact]
    public async Task DialogHostRendersModelessDragHandle()
    {
        using var context = CreateContext();
        var cut = context.Render<QuasarDialogHost>();
        var dialogs = context.Services.GetRequiredService<IQuasarDialogService>();

        await cut.InvokeAsync(() => dialogs.ShowAsync<HostTestDialog>("Modeless",
            options: new QuasarDialogOptions { Modal = false }));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("quasar-dialog-modeless", cut.Find("dialog").ClassList);
            Assert.NotNull(cut.Find(".quasar-dialog-drag-handle"));
        });
    }

    [Fact]
    public async Task OpeningAnotherDialogDoesNotReapplyParametersToOpenDialogs()
    {
        await using var context = CreateContext();
        using var cut = context.Render<QuasarDialogHost>();
        var dialogs = context.Services.GetRequiredService<IQuasarDialogService>();

        var first = await dialogs.ShowAsync<ParameterCountingDialog>("First",
            new QuasarDialogParameters<ParameterCountingDialog>
            {
                { dialog => dialog.Label, "first" },
            });
        await cut.WaitForAssertionAsync(() => Assert.Single(cut.FindAll("dialog")));
        var firstContent = cut.FindComponent<ParameterCountingDialog>();
        Assert.Equal(1, firstContent.Instance.ParameterSetCount);
        Assert.Equal("first", firstContent.Instance.Label);

        var second = await dialogs.ShowAsync<ParameterCountingDialog>("Second",
            new QuasarDialogParameters<ParameterCountingDialog>
            {
                { dialog => dialog.Label, "second" },
            });
        await cut.WaitForAssertionAsync(() => Assert.Equal(2, cut.FindAll("dialog").Count));

        // The first dialog must not receive its parameter dictionary again just
        // because a sibling dialog joined the stack.
        Assert.Equal(1, firstContent.Instance.ParameterSetCount);
        Assert.Equal("first", firstContent.Instance.Label);

        // Its own state changes still render.
        await cut.InvokeAsync(() => firstContent.Instance.Increment());
        await cut.WaitForAssertionAsync(() =>
            Assert.Contains("first: 1", cut.Markup, StringComparison.Ordinal));
        Assert.Equal(1, firstContent.Instance.ParameterSetCount);

        second.Cancel();
        await cut.WaitForAssertionAsync(() => Assert.Single(cut.FindAll("dialog")));
        Assert.Equal(1, firstContent.Instance.ParameterSetCount);

        first.Cancel();
        await cut.WaitForAssertionAsync(() => Assert.Empty(cut.FindAll("dialog")));
    }

    [Theory]
    [InlineData("Delete", true)]
    [InlineData("Keep", false)]
    [InlineData("Cancel", null)]
    public async Task MessageBoxRendersChoicesAndMapsResult(string buttonText, bool? expected)
    {
        await using var context = CreateContext();
        using var cut = context.Render<QuasarDialogHost>();
        var dialogs = context.Services.GetRequiredService<IQuasarDialogService>();

        var task = dialogs.ShowMessageBoxAsync("Delete backup?", "Permanently delete it?", "Delete", "Keep", "Cancel");
        await cut.WaitForAssertionAsync(() =>
        {
            Assert.Contains("Permanently delete it?", cut.Markup, StringComparison.Ordinal);
            Assert.Equal("Delete backup?", cut.Find("dialog").GetAttribute("aria-label"));
            Assert.Equal("Delete backup?", cut.Find("h2.quasar-dialog-title").TextContent);
        });

        await cut.FindAll("button").Single(button => button.TextContent.Trim() == buttonText).ClickAsync();
        await cut.WaitForAssertionAsync(() =>
            Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "quasarUi.dialogs.close"));
        Assert.Equal(expected, await task);
    }

    [Fact]
    public async Task MessageBoxEncodesTitleMessageAndLabels()
    {
        await using var context = CreateContext();
        using var cut = context.Render<QuasarDialogHost>();
        var dialogs = context.Services.GetRequiredService<IQuasarDialogService>();

        _ = dialogs.ShowMessageBoxAsync(
            "<img src=x onerror=alert(1)>",
            "<script>alert(1)</script>",
            "<b>Yes</b>",
            "<i>No</i>",
            "<u>Cancel</u>");

        await cut.WaitForAssertionAsync(() =>
        {
            Assert.Empty(cut.FindAll("img"));
            Assert.Empty(cut.FindAll("script"));
            Assert.Empty(cut.FindAll("b"));
            Assert.Equal("<img src=x onerror=alert(1)>", cut.Find("dialog").GetAttribute("aria-label"));
            Assert.Equal("<img src=x onerror=alert(1)>", cut.Find("h2.quasar-dialog-title").TextContent);
            var description = cut.Find("p[data-quasar-dialog-description]");
            Assert.Equal("<script>alert(1)</script>", description.TextContent);
            Assert.Contains("quasar-dialog-message", description.ClassList, StringComparer.Ordinal);
            Assert.Contains(cut.FindAll("button"), button => button.TextContent.Trim() == "<b>Yes</b>");
        });
    }

    [Fact]
    public async Task MessageBoxHidesOptionalNoButton()
    {
        await using var context = CreateContext();
        using var cut = context.Render<QuasarDialogHost>();
        var dialogs = context.Services.GetRequiredService<IQuasarDialogService>();

        var task = dialogs.ShowMessageBoxAsync("Question", "Choose", "OK", cancelText: "Cancel");
        await cut.WaitForAssertionAsync(() =>
        {
            var labels = cut.FindAll("button").Select(button => button.TextContent.Trim()).ToArray();
            Assert.Equal(["Cancel", "OK"], labels);
        });

        await cut.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel").ClickAsync();
        await cut.WaitForAssertionAsync(() =>
            Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "quasarUi.dialogs.close"));
        Assert.Null(await task);
    }

    private static QuasarBunitContext CreateContext()
    {
        var context = new QuasarBunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLogging();
        context.Services.AddQuasarUi();
        return context;
    }

    public sealed class HostTestDialog : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "p");
            builder.AddContent(1, "Host dialog content");
            builder.CloseElement();
        }
    }

    public sealed class SelfClosingDialog : ComponentBase
    {
        [CascadingParameter] private IQuasarDialogInstance Dialog { get; set; } = default!;

        protected override void OnInitialized() => Dialog.Cancel();
    }

    public sealed class ParameterCountingDialog : ComponentBase
    {
        [Parameter] public string Label { get; set; } = string.Empty;

        [CascadingParameter] private IQuasarDialogInstance Dialog { get; set; } = default!;

        public int ParameterSetCount { get; private set; }

        private int _count;

        protected override void OnParametersSet() => ParameterSetCount++;

        public void Increment()
        {
            _count++;
            StateHasChanged();
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "p");
            builder.AddContent(1, FormattableString.Invariant($"{Label}: {_count} ({Dialog.Id})"));
            builder.CloseElement();
        }
    }
}

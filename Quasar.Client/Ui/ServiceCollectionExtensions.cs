using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Quasar.Client.Ui.Dialogs;
using Quasar.Client.Ui.Toasts;
using Quasar.Ui;

namespace Quasar.Client.Ui;

/// <summary>
/// Registers the native Quasar dialog and toast services.
/// </summary>
public static class QuasarUiServiceCollectionExtensions
{
    /// <summary>
    /// Adds the native dialog and toast services to the dashboard container.
    /// </summary>
    /// <param name="services">The application service collection.</param>
    /// <param name="configureToasts">
    /// Optional callback that changes global toast placement, stack size, and
    /// navigation behavior. The values are copied into a singleton options
    /// object; later changes to the callback's local object have no effect.
    /// A later configured call replaces the previous options; an unconfigured
    /// call keeps the existing options.
    /// </param>
    /// <remarks>
    /// <para>
    /// The concrete services are scoped and shared through their public
    /// interfaces, so a page and a dialog in the same browser application see
    /// the same dialog stack and toast list. <see cref="TimeProvider.System"/>
    /// is registered with <c>TryAdd</c>, so tests can substitute a fake time
    /// provider before calling this method.
    /// </para>
    /// <para>
    /// The application must render <c>QuasarDialogHost</c> and
    /// <c>QuasarToastHost</c> once, normally in the shell layout. Without the
    /// hosts, the services still accept calls but nothing is displayed. A
    /// shown dialog's result stays pending until a host renders or the
    /// application is disposed; a toast is tracked but never seen.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddQuasarUi(options =&gt;
    /// {
    ///     options.Position = QuasarToastPosition.BottomRight;
    ///     options.MaxVisibleToasts = 3;
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddQuasarUi(
        this IServiceCollection services,
        Action<QuasarToastHostOptions>? configureToasts = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var toastOptions = new QuasarToastHostOptions();
        configureToasts?.Invoke(toastOptions);
        if (toastOptions.MaxVisibleToasts < 1)
            throw new InvalidOperationException("MaxVisibleToasts must be at least 1.");

        var registeredOptions = new QuasarToastHostOptions
        {
            Position = toastOptions.Position,
            MaxVisibleToasts = toastOptions.MaxVisibleToasts,
            NewestOnTop = toastOptions.NewestOnTop,
            ClearAfterNavigation = toastOptions.ClearAfterNavigation,
        };

        services.TryAddSingleton(TimeProvider.System);
        if (configureToasts is not null)
            services.Replace(ServiceDescriptor.Singleton(registeredOptions));
        else
            services.TryAddSingleton(registeredOptions);
        services.TryAddScoped<QuasarDialogService>();
        services.TryAddScoped<IQuasarDialogService>(provider => provider.GetRequiredService<QuasarDialogService>());
        services.TryAddScoped<QuasarToastService>();
        services.TryAddScoped<IQuasarToastService>(provider => provider.GetRequiredService<QuasarToastService>());
        return services;
    }
}

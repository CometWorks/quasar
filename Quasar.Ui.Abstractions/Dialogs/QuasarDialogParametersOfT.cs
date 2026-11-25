using System.Linq.Expressions;

namespace Quasar.Ui;

/// <summary>
/// Strongly typed dialog parameters. The expression only identifies the
/// component parameter; no reflection is used to resolve it.
/// </summary>
/// <typeparam name="TComponent">The dialog component type.</typeparam>
/// <remarks>
/// <para>
/// The selector is compiled only to read the member name. The host still
/// binds parameters by name, so renaming a component parameter breaks the
/// call site at compile time instead of silently passing an unknown value.
/// </para>
/// <para>
/// The initializer syntax and the fluent <see cref="Set{TValue}"/> method are
/// equivalent. Use whichever is more readable at the call site.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp">
/// var parameters = new QuasarDialogParameters&lt;ConfirmDialog&gt;
/// {
///     { dialog =&gt; dialog.Message, "Delete this host?" },
///     { dialog =&gt; dialog.HostId, hostId },
/// };
/// </code>
/// </example>
public sealed class QuasarDialogParameters<TComponent> : QuasarDialogParameters
{
    /// <summary>Creates an empty parameter set.</summary>
    public QuasarDialogParameters()
    {
    }

    /// <summary>Sets a component parameter by expression.</summary>
    /// <remarks>
    /// Only a simple property or field on the component lambda parameter is
    /// accepted. Nested members, method calls and computed expressions are
    /// rejected because they have no stable component parameter name.
    /// </remarks>
    public object? this[Expression<Func<TComponent, object?>> selector]
    {
        set => Set(selector, value);
    }

    /// <summary>
    /// Adds a component parameter by expression, supporting collection
    /// initializers such as <c language="csharp">{ dialog =&gt; dialog.Message, "..." }</c>.
    /// </summary>
    public void Add(Expression<Func<TComponent, object?>> selector, object? value) => Set(selector, value);

    /// <summary>Sets a component parameter by expression and returns this instance.</summary>
    public QuasarDialogParameters<TComponent> Set<TValue>(
        Expression<Func<TComponent, TValue>> selector,
        TValue value)
    {
        base.Set(GetParameterName(selector), value);
        return this;
    }

    private static string GetParameterName(LambdaExpression selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var body = selector.Body;
        if (body is UnaryExpression { NodeType: ExpressionType.Convert } unary)
            body = unary.Operand;

        if (body is MemberExpression { Member.Name: { Length: > 0 } name, Expression: { } target }
            && IsParameterTarget(target, selector.Parameters[0]))
            return name;

        throw new ArgumentException(
            "Dialog parameter selectors must target a property or field on the component parameter, for example p => p.Name.",
            nameof(selector));
    }

    private static bool IsParameterTarget(Expression target, ParameterExpression parameter)
    {
        while (target is UnaryExpression { NodeType: ExpressionType.Convert } unary)
            target = unary.Operand;

        return target == parameter;
    }
}

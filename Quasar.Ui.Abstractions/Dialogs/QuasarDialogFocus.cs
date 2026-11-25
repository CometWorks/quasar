namespace Quasar.Ui;

/// <summary>
/// Initial focus target for a dialog.
/// </summary>
/// <remarks>
/// Native modal dialogs focus their first focusable descendant by default,
/// which is usually the right behavior. Choose <see cref="Dialog"/> for a
/// dialog whose content has no focusable control until the user interacts, so
/// the dialog container itself receives focus and Escape still works.
/// </remarks>
public enum QuasarDialogFocus
{
    /// <summary>Let the browser focus the first focusable descendant.</summary>
    FirstFocusable,

    /// <summary>Focus the dialog container itself.</summary>
    Dialog,
}

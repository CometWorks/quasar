using System.Xml;
using System.Xml.Linq;

namespace Quasar.Services.Plugins;

/// <summary>
/// Validates SVG fragments contributed by UI plugins before the navigation
/// renderer treats them as trusted markup.
/// </summary>
/// <remarks>
/// <para>
/// <c>QuasarIcon</c> is safe by default: plugin icons are rendered as text
/// unless the caller opts in. Navigation icons are trusted plugin metadata, so
/// <c>PluginNavItems</c> opts in after this sanitizer has run once at catalog
/// load.
/// </para>
/// <para>
/// The accepted shape is a fragment of standard SVG drawing elements, matching
/// the built-in Material icon constants. Script elements, event attributes,
/// external references, and unlisted CSS-bearing attributes are rejected. A
/// legacy font icon name (any value without markup) passes through unchanged and
/// is rendered as text by <c>QuasarIcon</c>.
/// </para>
/// </remarks>
internal static class QuasarNavIconSanitizer
{
    /// <summary>
    /// Material "extension" icon used when a plugin supplies an invalid SVG
    /// fragment. The path matches <c>QuasarIcons.Extension</c>; it is repeated
    /// here so the worker does not depend on the browser component assembly.
    /// </summary>
    internal const string FallbackIcon =
        "<path d=\"M0 0h24v24H0z\" fill=\"none\"/><path d=\"M20.5 11H19V7c0-1.1-.9-2-2-2h-4V3.5C13 2.12 11.88 1 10.5 1S8 2.12 8 3.5V5H4c-1.1 0-1.99.9-1.99 2v3.8H3.5c1.49 0 2.7 1.21 2.7 2.7s-1.21 2.7-2.7 2.7H2V20c0 1.1.9 2 2 2h3.8v-1.5c0-1.49 1.21-2.7 2.7-2.7 1.49 0 2.7 1.21 2.7 2.7V22H17c1.1 0 2-.9 2-2v-4h1.5c1.38 0 2.5-1.12 2.5-2.5S21.88 11 20.5 11z\"/>";

    private const int MaxIconLength = 16 * 1024;

    private static readonly HashSet<string> AllowedElements = new(StringComparer.Ordinal)
    {
        "circle",
        "clipPath",
        "defs",
        "ellipse",
        "g",
        "line",
        "linearGradient",
        "path",
        "polygon",
        "polyline",
        "radialGradient",
        "rect",
        "stop",
        "title",
    };

    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.Ordinal)
    {
        "class",
        "clip-path",
        "clip-rule",
        "cx",
        "cy",
        "d",
        "fill",
        "fill-opacity",
        "fill-rule",
        "gradientTransform",
        "gradientUnits",
        "height",
        "id",
        "offset",
        "opacity",
        "points",
        "preserveAspectRatio",
        "r",
        "rx",
        "ry",
        "stop-color",
        "stop-opacity",
        "stroke",
        "stroke-dasharray",
        "stroke-dashoffset",
        "stroke-linecap",
        "stroke-linejoin",
        "stroke-miterlimit",
        "stroke-opacity",
        "stroke-width",
        "transform",
        "viewBox",
        "width",
        "x",
        "x1",
        "x2",
        "y",
        "y1",
        "y2",
    };

    /// <summary>
    /// Returns a render-safe icon. When the supplied SVG fragment is invalid or
    /// unsafe, the built-in fallback is returned and <paramref name="replaced"/>
    /// is set so the caller can record a load warning.
    /// </summary>
    internal static string Sanitize(string? icon, out bool replaced)
    {
        replaced = false;
        if (string.IsNullOrWhiteSpace(icon))
            return icon ?? string.Empty;

        // Legacy UI plugins may contribute an icon-font name. It contains no
        // markup and is rendered as text, so there is nothing to validate.
        if (!icon.Contains('<', StringComparison.Ordinal))
            return icon;

        if (IsSafeSvgFragment(icon))
            return icon;

        replaced = true;
        return FallbackIcon;
    }

    /// <summary>
    /// Returns <c>true</c> when the value is an SVG fragment made only of the
    /// allowed drawing elements and attributes.
    /// </summary>
    internal static bool IsSafeSvgFragment(string icon)
    {
        if (icon.Length > MaxIconLength)
            return false;

        XElement root;
        try
        {
            // A fragment may have several roots, so wrap it in a private svg
            // element. DTD processing is prohibited by XElement.Parse.
            root = XElement.Parse("<svg>" + icon + "</svg>", LoadOptions.None);
        }
        catch (XmlException)
        {
            return false;
        }

        // Layout whitespace between elements is harmless. Comments, processing
        // instructions, and other node types are not part of an icon fragment.
        foreach (var node in root.Nodes())
        {
            if (node is XElement)
                continue;
            if (node is XText text && string.IsNullOrWhiteSpace(text.Value))
                continue;
            return false;
        }

        foreach (var element in root.Descendants())
        {
            if (element.Name.Namespace != XNamespace.None ||
                !AllowedElements.Contains(element.Name.LocalName))
                return false;

            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration ||
                    attribute.Name.Namespace != XNamespace.None ||
                    !AllowedAttributes.Contains(attribute.Name.LocalName) ||
                    IsUnsafeValue(attribute.Value))
                    return false;
            }
        }

        return true;
    }

    private static bool IsUnsafeValue(string value)
    {
        if (value.Contains("javascript:", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("vbscript:", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("data:", StringComparison.OrdinalIgnoreCase))
            return true;

        // Only same-document paint references are allowed. External url(...)
        // values could load a remote resource or leak the viewer's address.
        var search = 0;
        while (true)
        {
            var index = value.IndexOf("url(", search, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;

            var cursor = index + 4;
            while (cursor < value.Length && char.IsWhiteSpace(value[cursor]))
                cursor++;
            if (cursor < value.Length && value[cursor] is '\'' or '"')
                cursor++;
            if (cursor >= value.Length || value[cursor] != '#')
                return true;

            search = index + 4;
        }
    }
}

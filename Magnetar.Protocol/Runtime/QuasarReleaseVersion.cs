using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Magnetar.Protocol.Runtime;

public static class QuasarReleaseVersion
{
    private static readonly Regex VersionPattern = new(
        @"(?<core>\d+(?:\.\d+){1,3})(?:-(?<pre>[0-9A-Za-z][0-9A-Za-z.-]*))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string GetEntryAssemblyVersion()
    {
        var assembly = Assembly.GetEntryAssembly();
        var informational = assembly?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        var normalized = Normalize(informational ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(normalized))
            return normalized;

        return assembly?.GetName().Version?.ToString() ?? "0.0.0";
    }

    public static bool IsNewer(string candidate, string current)
    {
        candidate = Normalize(candidate);
        current = Normalize(current);

        if (string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase))
            return false;

        if (TryParse(candidate, out var candidateVersion) &&
            TryParse(current, out var currentVersion))
        {
            return candidateVersion.CompareTo(currentVersion) > 0;
        }

        return string.IsNullOrWhiteSpace(current) && !string.IsNullOrWhiteSpace(candidate);
    }

    public static string Normalize(string value)
    {
        value = (value ?? string.Empty).Trim();
        var match = VersionPattern.Match(value);
        if (!match.Success)
            return value.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? value.Substring(1) : value;

        var core = NormalizeCore(match.Groups["core"].Value);
        var prerelease = match.Groups["pre"].Success ? match.Groups["pre"].Value : string.Empty;
        if (int.TryParse(prerelease, out _))
            return $"{core}.{prerelease}";

        return string.IsNullOrWhiteSpace(prerelease) ? core : $"{core}-{prerelease}";
    }

    private static string NormalizeCore(string core)
    {
        var parts = core.Split('.');
        if (parts.Length == 4 && parts[3] == "0")
            parts = parts.Take(3).ToArray();

        return string.Join(".", parts);
    }

    private static bool TryParse(string value, out ReleaseVersion version)
    {
        version = default!;
        value = Normalize(value);
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var prereleaseStart = value.IndexOf("-", StringComparison.Ordinal);
        var core = prereleaseStart < 0 ? value : value.Substring(0, prereleaseStart);
        var prerelease = prereleaseStart < 0 ? string.Empty : value.Substring(prereleaseStart + 1);
        var parts = core.Split('.');
        if (parts.Length < 2 || parts.Length > 4)
            return false;

        var numbers = new int[4];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], out numbers[index]) || numbers[index] < 0)
                return false;
        }

        version = new ReleaseVersion(numbers, prerelease);
        return true;
    }

    private sealed class ReleaseVersion : IComparable<ReleaseVersion>
    {
        private readonly int[] _numbers;
        private readonly string _prerelease;

        public ReleaseVersion(int[] numbers, string prerelease)
        {
            _numbers = numbers;
            _prerelease = prerelease;
        }

        public int CompareTo(ReleaseVersion? other)
        {
            if (other is null)
                return 1;

            for (var index = 0; index < _numbers.Length; index++)
            {
                var comparison = _numbers[index].CompareTo(other._numbers[index]);
                if (comparison != 0)
                    return comparison;
            }

            return ComparePrerelease(_prerelease, other._prerelease);
        }

        private static int ComparePrerelease(string left, string right)
        {
            var leftEmpty = string.IsNullOrWhiteSpace(left);
            var rightEmpty = string.IsNullOrWhiteSpace(right);
            if (leftEmpty && rightEmpty)
                return 0;

            if (leftEmpty)
                return 1;

            if (rightEmpty)
                return -1;

            var leftParts = left.Split('.');
            var rightParts = right.Split('.');
            var count = Math.Min(leftParts.Length, rightParts.Length);
            for (var index = 0; index < count; index++)
            {
                var leftIsNumber = int.TryParse(leftParts[index], out var leftNumber);
                var rightIsNumber = int.TryParse(rightParts[index], out var rightNumber);
                int comparison;
                if (leftIsNumber && rightIsNumber)
                    comparison = leftNumber.CompareTo(rightNumber);
                else if (leftIsNumber)
                    comparison = -1;
                else if (rightIsNumber)
                    comparison = 1;
                else
                    comparison = string.Compare(leftParts[index], rightParts[index], StringComparison.OrdinalIgnoreCase);

                if (comparison != 0)
                    return comparison;
            }

            return leftParts.Length.CompareTo(rightParts.Length);
        }
    }
}

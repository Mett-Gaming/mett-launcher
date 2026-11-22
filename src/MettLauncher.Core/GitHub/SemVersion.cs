using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace MettLauncher.Core.GitHub;

/// <summary>SemVer 2.0 (Major.Minor.Patch[-Prerelease][+Build]) mit Vergleich nach SemVer-Präzedenz.</summary>
public sealed partial class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    public SemVersion(int major, int minor, int patch, string? prerelease = null, string? build = null)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = string.IsNullOrEmpty(prerelease) ? null : prerelease;
        Build = string.IsNullOrEmpty(build) ? null : build;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? Prerelease { get; }
    public string? Build { get; }
    public bool IsPrerelease => Prerelease is not null;

    /// <summary>Akzeptiert <c>1.2.3</c>, <c>v1.2.3</c>, <c>V1.2.3-beta.1+abc</c>.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out SemVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = SemVerRegex().Match(text.Trim());
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups["major"].Value, out var major) ||
            !int.TryParse(m.Groups["minor"].Value, out var minor) ||
            !int.TryParse(m.Groups["patch"].Value, out var patch)) return false;
        version = new SemVersion(major, minor, patch, m.Groups["pre"].Value, m.Groups["build"].Value);
        return true;
    }

    public static SemVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"'{text}' ist keine gültige Version (erwartet vX.Y.Z).");

    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;

        var a = Prerelease.Split('.');
        var b = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNum = long.TryParse(a[i], out var an);
            var bNum = long.TryParse(b[i], out var bn);
            c = (aNum, bNum) switch
            {
                (true, true) => an.CompareTo(bn),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    public bool Equals(SemVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemVersion v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    public static bool operator <(SemVersion? a, SemVersion? b) => Compare(a, b) < 0;
    public static bool operator >(SemVersion? a, SemVersion? b) => Compare(a, b) > 0;
    public static bool operator <=(SemVersion? a, SemVersion? b) => Compare(a, b) <= 0;
    public static bool operator >=(SemVersion? a, SemVersion? b) => Compare(a, b) >= 0;
    public static bool operator ==(SemVersion? a, SemVersion? b) => Compare(a, b) == 0;
    public static bool operator !=(SemVersion? a, SemVersion? b) => Compare(a, b) != 0;

    private static int Compare(SemVersion? a, SemVersion? b) =>
        a is null ? (b is null ? 0 : -1) : a.CompareTo(b);

    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}{(Prerelease is null ? "" : "-" + Prerelease)}{(Build is null ? "" : "+" + Build)}";

    [GeneratedRegex(@"^[vV]?(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+(?<build>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$")]
    private static partial Regex SemVerRegex();
}

using RouteDeck.Core.Models;

namespace RouteDeck.Core.Diagnostics;

public enum FindingLevel
{
    Info,
    Notice,
    Problem,
}

/// <param name="Id">Stable across refreshes so the UI can remember dismissals.</param>
public sealed record DiagnosticFinding(string Id, string RuleId, FindingLevel Level, string Title, string Detail, IReadOnlyList<RouteKey> Routes)
{
    /// <summary>A safe one-click fix, only offered when it touches RouteDeck-owned or inert objects.</summary>
    public ChangeSet? Fix { get; init; }

    public string? FixLabel { get; init; }

    /// <summary>Set for tunnel takeover findings: the UI can offer "let a destination bypass this tunnel".</summary>
    public ulong? TunnelLuid { get; init; }

    internal static string MakeId(string ruleId, IEnumerable<RouteKey> routes) =>
        ruleId + ":" + string.Join("|", routes.Select(r => r.ToString()).Order(StringComparer.Ordinal));
}

public interface IDiagnosticRule
{
    string Id { get; }

    IEnumerable<DiagnosticFinding> Evaluate(NetworkSnapshot snapshot);
}

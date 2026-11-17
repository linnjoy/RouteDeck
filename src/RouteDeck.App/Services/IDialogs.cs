using System.Net;
using RouteDeck.Core.Changes;
using RouteDeck.Core.Models;

namespace RouteDeck.App.Services;

public sealed record BypassRequest(IpPrefix Destination, ulong ExitLuid, IPAddress? Gateway, bool Persistent);

/// <summary>Modal dialogs, abstracted so view models stay testable.</summary>
public interface IDialogs
{
    /// <param name="warnings">Shown highlighted above the list (foreign routes, lockout risk).</param>
    bool ConfirmChange(string title, IReadOnlyList<string> operations, IReadOnlyList<string> warnings, string confirmText);

    RouteSpec? EditRoute(NetworkSnapshot snapshot, Route? existing);

    BypassRequest? Bypass(NetworkSnapshot snapshot, string? presetTarget, ulong? tunnelLuid);

    bool ConfirmUndo(string title, UndoPlan plan);

    void ShowError(string title, string message);
}

using System;
using Dalamud.Interface.ImGuiNotification;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Relay;

namespace Oathbound.Plugin.UI;

/// Owner side: a Sub's safeword, from their tell or the relay. Both carry the Sub's own `at`, so the second copy of
/// one safeword is dropped. Framework thread.
public sealed class OwnerSafewordAlerts
{
    /// A tell can carry any number; one far ahead would hide every later safeword.
    private const long FutureToleranceSeconds = 300;

    private readonly PluginConfig config;
    private readonly OwnerStatusEstimateTracker estimates;

    public OwnerSafewordAlerts(PluginConfig config, OwnerStatusEstimateTracker estimates)
    {
        this.config = config;
        this.estimates = estimates;
    }

    /// Callers have already verified the sender or the pair; this only checks the pairing's direction.
    public void Accept(PairingState pairing, long at, SafewordMood mood)
    {
        if (pairing is not { IsPaired: true, Direction: PairingDirection.OwnerSide })
            return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (at > now + FutureToleranceSeconds)
            at = now;
        if (at <= pairing.LastSafewordAt)
            return;

        pairing.LastSafewordAt = at;
        pairing.LastSafewordMood = mood;
        config.Save();
        estimates.Clear(pairing.Id);

        var who = pairing.PeerName ?? "Your Sub";
        var text = $"{who} used their safeword {Ago(at)}. {MoodText(mood)}";
        Plugin.NotificationManager.AddNotification(new Notification
        {
            Title = "Safeword used",
            Content = text,
            Type = NotificationType.Error,
            InitialDuration = TimeSpan.FromSeconds(30),
        });
        OathboundChat.PrintError(text);
    }

    public void OnPairStatus(PairingState pairing, PairEnvelope pair)
    {
        if (pair.SafewordAt is { } at && ChatComposer.TryParseSafewordMood(pair.SafewordMood, out var mood))
            Accept(pairing, at, mood);
    }

    public static bool IsShowing(PairingState pairing) => pairing.LastSafewordAt > pairing.SafewordDismissedAt;

    public void Dismiss(PairingState pairing)
    {
        pairing.SafewordDismissedAt = pairing.LastSafewordAt;
        config.Save();
    }

    public static string MoodText(SafewordMood mood) => mood == SafewordMood.CheckIn
        ? "They asked you to check on them."
        : "They're okay and want it to stop.";

    public static string Ago(long at)
    {
        var elapsed = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(at);
        if (elapsed < TimeSpan.FromMinutes(1)) return "just now";
        if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes} min ago";
        if (elapsed < TimeSpan.FromDays(1)) return $"{(int)elapsed.TotalHours} h ago";
        return $"{(int)elapsed.TotalDays} d ago";
    }
}

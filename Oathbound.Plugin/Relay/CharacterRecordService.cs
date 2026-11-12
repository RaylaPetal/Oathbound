using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// Publishes this install's linked characters (and which one it's on) to each pairing, and applies the other person's
/// record from the pair status check. Every change to MyCharacters or to a Sub's confirmations goes through here so it
/// gets published.
public sealed class CharacterRecordService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);
    /// An older relay without the route: back off for a long time instead of retrying every few minutes.
    private static readonly TimeSpan UnsupportedRetryDelay = TimeSpan.FromHours(1);

    private readonly PluginConfig config;
    private readonly RelayClient relay;
    private readonly DeviceIdentityService identity;

    private bool wasLoggedIn;
    private bool loginPublishPending;
    private volatile bool dirty;
    private DateTime retryNotBeforeUtc;
    private int busy;
    private readonly HashSet<string> notNowThisSession = new(StringComparer.OrdinalIgnoreCase);

    /// The logged-in character, once the object table has it; null at the title screen.
    public CharacterRef? Current { get; private set; }

    public CharacterRecordService(PluginConfig config, RelayClient relay, DeviceIdentityService identity)
    {
        this.config = config;
        this.relay = relay;
        this.identity = identity;
    }

    // ---- Own characters ----

    public bool NeedsLinkPrompt =>
        Current is { } current && config.ActivePairings.Any() && config.FindMyCharacter(current.Name, current.World) is null &&
        !notNowThisSession.Contains(current.ToString());

    public void LinkCurrent() { if (Current is { } c) SetMine(c.Name, c.World, MyCharacterState.Linked); }

    public void NotNowForCurrent() { if (Current is { } c) notNowThisSession.Add(c.ToString()); }

    public void NeverForCurrent() { if (Current is { } c) SetMine(c.Name, c.World, MyCharacterState.Never); }

    /// Settings: link, unlink or mark Never. Unlinking the last linked character is refused while paired.
    public bool SetMine(string name, string world, MyCharacterState state)
    {
        if (state != MyCharacterState.Linked && config.FindMyCharacter(name, world) is { State: MyCharacterState.Linked } &&
            config.LinkedCharacters.Count() == 1 && config.ActivePairings.Any())
            return false;
        if (!config.SetMyCharacterState(name, world, state)) return true;
        config.SaveNow();
        if (state == MyCharacterState.Linked && Current is { } c && c.Is(name, world))
            loginPublishPending = true;
        dirty = true;
        return true;
    }

    public bool CanUnlink(MyCharacter character) =>
        character.State != MyCharacterState.Linked || config.LinkedCharacters.Count() > 1 || !config.ActivePairings.Any();

    // ---- The other person's characters ----

    /// Sub side: let a Pending Owner character send commands.
    public void Confirm(PairingState pairing, PeerCharacter character)
    {
        if (pairing.Direction != PairingDirection.SubSide || character.State == PeerCharacterState.Confirmed) return;
        character.State = PeerCharacterState.Confirmed;
        character.ConfirmedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        pairing.ResolveTarget();
        config.SaveNow();
        dirty = true;
        OathboundChat.Print($"Commands from {character} now count as {OwnerLabel(pairing)}.");
    }

    public void Reject(PairingState pairing, PeerCharacter character)
    {
        if (pairing.Direction != PairingDirection.SubSide) return;
        var wasConfirmed = character.State == PeerCharacterState.Confirmed;
        if (wasConfirmed && pairing.ConfirmedCharacters.Count() <= 1) return;
        character.State = PeerCharacterState.Rejected;
        pairing.ResolveTarget();
        config.SaveNow();
        dirty = true;
    }

    /// Sub side: stop accepting a confirmed Owner character. The last one can't go; that's what Unpair is for.
    public bool CanRemove(PairingState pairing, PeerCharacter character) =>
        pairing.Direction == PairingDirection.SubSide && character.State == PeerCharacterState.Confirmed && pairing.ConfirmedCharacters.Count() > 1;

    public void Remove(PairingState pairing, PeerCharacter character)
    {
        if (!CanRemove(pairing, character)) return;
        Reject(pairing, character);
    }

    public void Pin(PairingState pairing, CharacterRef? character)
    {
        pairing.PinnedTargetName = character?.Name;
        pairing.PinnedTargetWorld = character?.World;
        pairing.ResolveTarget();
        config.SaveNow();
    }

    public static IEnumerable<(PairingState Pairing, PeerCharacter Character)> PendingOwnerCharacters(PluginConfig config) =>
        config.ActivePairings.Where(p => p.Direction == PairingDirection.SubSide)
            .SelectMany(p => p.PeerCharacters.Where(c => c.State == PeerCharacterState.Pending).Select(c => (p, c)));

    private static string OwnerLabel(PairingState pairing) =>
        pairing.ConfirmedCharacters.FirstOrDefault()?.ToString() ?? "your Owner";

    // ---- Publishing ----

    public void OnFrameworkUpdate(CancellationToken ct)
    {
        if (!Plugin.ClientState.IsLoggedIn)
        {
            wasLoggedIn = false;
            Current = null;
            return;
        }
        if (!wasLoggedIn || Current is null)
        {
            if (CodePairingService.CurrentCharacter(null) is not { } character) return; // Not loaded in yet.
            Current = new CharacterRef { Name = character.Name, World = character.World };
            wasLoggedIn = true;
            loginPublishPending = true;
        }

        if (config.LinkCurrentCharacterRequested)
        {
            config.LinkCurrentCharacterRequested = false;
            config.SetMyCharacterState(Current.Name, Current.World, MyCharacterState.Linked);
            foreach (var pairing in config.ActivePairings.Where(p => p.RecordPairedFrom))
            {
                pairing.PairedFromName = Current.Name;
                pairing.PairedFromWorld = Current.World;
                pairing.RecordPairedFrom = false;
            }
            config.SaveNow();
            loginPublishPending = true;
        }

        if (loginPublishPending)
        {
            loginPublishPending = false;
            // Presence is only ever published from a linked character, so an unlinked one stays private.
            if (config.FindMyCharacter(Current.Name, Current.World) is { State: MyCharacterState.Linked })
                dirty = true;
        }

        if (!dirty || DateTime.UtcNow < retryNotBeforeUtc || Interlocked.Exchange(ref busy, 1) == 1)
            return;
        var batch = BuildBatch();
        dirty = false;
        if (batch.Count == 0)
        {
            Interlocked.Exchange(ref busy, 0);
            return;
        }
        Plugin.FireAndForget(SendAsync(batch, ct));
    }

    private sealed record Outgoing(string PairIdHash, int PairEpoch, long Seq, EncryptedCharacterRecord Record);

    /// Built on the main thread, seq bumped and saved before anything is sent, so a seq is never reused.
    private List<Outgoing> BuildBatch()
    {
        var linked = config.LinkedCharacters.Select(CharacterRecordEntry.From).ToList();
        var unlinked = config.MyCharacters.Where(c => c.State == MyCharacterState.Unlinked).Select(CharacterRecordEntry.From).ToList();
        if (linked.Count == 0 && unlinked.Count == 0) return new();

        if (Current is { } current && config.FindMyCharacter(current.Name, current.World) is { State: MyCharacterState.Linked })
        {
            config.LastPublishedCurrentName = current.Name;
            config.LastPublishedCurrentWorld = current.World;
        }
        var currentIndex = linked.FindIndex(c =>
            string.Equals(c.Name, config.LastPublishedCurrentName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.World, config.LastPublishedCurrentWorld, StringComparison.OrdinalIgnoreCase));

        RelayEcKeyPair ownKey;
        try { ownKey = identity.GetSigningKey(); }
        catch (Exception ex) when (ex is DeviceIdentityUnavailableException or InvalidOperationException)
        {
            Plugin.Log.Information($"Characters not published: {ex.Message}");
            return new();
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var batch = new List<Outgoing>();
        foreach (var pairing in config.ActivePairings.Where(p => p.PairIdHash is not null && p.PeerPublicKeyX is not null && p.PeerPublicKeyY is not null))
        {
            var record = new CharacterRecord
            {
                Seq = pairing.NextOutgoingCharactersSeq(),
                At = now,
                Linked = linked,
                Unlinked = unlinked,
                Current = currentIndex >= 0 ? currentIndex : null,
                Accepted = pairing.Direction == PairingDirection.SubSide
                    ? pairing.ConfirmedCharacters.Select(CharacterRecordEntry.From).ToList()
                    : null,
            };
            var key = CharacterRecordCrypto.DeriveKey(ownKey, PeerKey(pairing), pairing.PairIdHash!, pairing.PairEpoch);
            try
            {
                var side = CharacterRecordCrypto.SideOf(pairing.Direction, own: true);
                batch.Add(new Outgoing(pairing.PairIdHash!, pairing.PairEpoch, record.Seq, CharacterRecordCrypto.Seal(record, key, pairing.PairIdHash!, pairing.PairEpoch, side)));
            }
            catch (InvalidOperationException ex)
            {
                Plugin.Log.Warning($"Characters not published: {ex.Message}");
            }
            finally
            {
                Array.Clear(key);
            }
        }
        config.SaveNow();
        return batch;
    }

    private async Task SendAsync(List<Outgoing> batch, CancellationToken ct)
    {
        try
        {
            foreach (var item in batch)
                await relay.PutPairCharactersAsync(item.PairIdHash, item.PairEpoch, item.Seq, item.Record, ct).ConfigureAwait(false);
            Plugin.Log.Information($"Characters published to {batch.Count} pairing(s).");
        }
        catch (RelayException ex)
        {
            dirty = true;
            retryNotBeforeUtc = DateTime.UtcNow + (ex.Code == "not_found" ? UnsupportedRetryDelay : RetryDelay);
            Plugin.Log.Information($"Characters publish failed ({ex.Code}); retrying later.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            dirty = true;
            retryNotBeforeUtc = DateTime.UtcNow + RetryDelay;
            Plugin.Log.Warning(ex, "Characters publish failed; retrying later.");
        }
        finally
        {
            Interlocked.Exchange(ref busy, 0);
        }
    }

    // ---- Reading ----

    /// Main thread, from RevocationService.PairStatusFetched.
    public void OnPairStatus(PairingState pairing, PairEnvelope pair)
    {
        if (pair.PeerCharacters is not { } summary || summary.Seq <= pairing.LastAppliedPeerRecordSeq || !pairing.IsPaired ||
            pairing.PairIdHash is null || pairing.PeerPublicKeyX is null || pairing.PeerPublicKeyY is null)
            return;

        CharacterRecord? record;
        byte[] key;
        try { key = CharacterRecordCrypto.DeriveKey(identity.GetSigningKey(), PeerKey(pairing), pairing.PairIdHash, pairing.PairEpoch); }
        catch (Exception ex) when (ex is DeviceIdentityUnavailableException or InvalidOperationException) { return; }
        try
        {
            record = CharacterRecordCrypto.Open(summary.Record, key, pairing.PairIdHash, pairing.PairEpoch,
                CharacterRecordCrypto.SideOf(pairing.Direction, own: false), summary.Seq);
        }
        finally
        {
            Array.Clear(key);
        }
        if (record is null)
        {
            Plugin.Log.Warning("Ignored a characters record that didn't decrypt for this pairing.");
            return;
        }
        Apply(pairing, record);
    }

    private void Apply(PairingState pairing, CharacterRecord record)
    {
        pairing.EnsureCharactersMigrated();
        var peerIsOwner = pairing.Direction == PairingDirection.SubSide;
        var label = OwnerLabel(pairing);
        var sendAccepted = false;

        foreach (var gone in record.Unlinked.Where(Valid))
        {
            if (pairing.FindPeerCharacter(gone.Name, gone.World) is not { } existing) continue;
            if (existing.State == PeerCharacterState.Confirmed && pairing.ConfirmedCharacters.Count() <= 1) continue;
            pairing.PeerCharacters.Remove(existing);
            sendAccepted |= peerIsOwner && existing.State == PeerCharacterState.Confirmed;
        }

        foreach (var added in record.Linked.Where(Valid))
        {
            if (pairing.FindPeerCharacter(added.Name, added.World) is not null) continue;
            if (pairing.PeerCharacters.Count >= PluginConfig.PeerCharactersMax) break;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (peerIsOwner)
            {
                pairing.PeerCharacters.Add(new PeerCharacter { Name = added.Name, World = added.World, State = PeerCharacterState.Pending });
                OathboundChat.Print($"{label} says {added.Name}@{added.World} is also them. Confirm it in Oathbound before it can send you commands.");
            }
            else
            {
                pairing.PeerCharacters.Add(new PeerCharacter { Name = added.Name, World = added.World, State = PeerCharacterState.Confirmed, ConfirmedAt = now });
                OathboundChat.Print($"{label} is now also {added.Name}@{added.World}.");
            }
        }

        if (record.Current is { } index && index >= 0 && index < record.Linked.Count && record.At >= pairing.PresenceAt && Valid(record.Linked[index]))
        {
            pairing.PresenceName = record.Linked[index].Name;
            pairing.PresenceWorld = record.Linked[index].World;
            pairing.PresenceAt = record.At;
        }

        if (!peerIsOwner)
            pairing.PeerAcceptedMine = (record.Accepted ?? new()).Where(Valid).Take(PluginConfig.PeerCharactersMax)
                .Select(c => new CharacterRef { Name = c.Name, World = c.World }).ToList();

        pairing.PeerPublishesCharacters = true;
        pairing.LastAppliedPeerRecordSeq = record.Seq;
        pairing.ResolveTarget();
        config.SaveNow();
        if (sendAccepted) dirty = true;
    }

    /// Owner side: whether the Sub accepts commands from one of my characters. Before the Sub ever publishes, only
    /// the character this pairing was made from is known to them.
    public static bool IsAcceptedBySub(PairingState pairing, CharacterRef mine) =>
        pairing.PeerAcceptedMine.Any(c => c.Is(mine.Name, mine.World)) ||
        (!pairing.PeerPublishesCharacters && mine.Is(pairing.PairedFromName, pairing.PairedFromWorld));

    private static bool Valid(CharacterRecordEntry c) =>
        c.Name.Length is > 0 and <= 32 && c.World.Length is > 0 and <= 32;

    private static EcPublicKeyJwk PeerKey(PairingState pairing) =>
        new() { Kty = "EC", Crv = "P-256", X = pairing.PeerPublicKeyX!, Y = pairing.PeerPublicKeyY! };
}

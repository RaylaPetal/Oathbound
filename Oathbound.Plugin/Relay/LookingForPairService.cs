using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.ImGuiNotification;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// collar/looking-for-pair: this install's profile, browsing, likes, matches and blocks on the relay. Nothing a match
/// sends is ever applied; a match only carries chat. Main thread only; relay calls run off it and report back.
public sealed class LookingForPairService : IDisposable
{
    public const int MaxSkipped = 2000;
    /// protocol/constants.json lookingForPair.pictureMaxSidePixels.
    public const int PictureMaxSide = 512;
    private static readonly TimeSpan MatchPoll = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromDays(1);

    public static readonly string[] Races = ["hyur", "elezen", "lalafell", "miqote", "roegadyn", "aura", "hrothgar", "viera"];
    public static readonly string[] RaceNames = ["Hyur", "Elezen", "Lalafell", "Miqo'te", "Roegadyn", "Au Ra", "Hrothgar", "Viera"];
    public static readonly string[] Genders = ["woman", "man", "nonbinary", "other"];
    public static readonly string[] GenderNames = ["Woman", "Man", "Non-binary", "Other"];
    public static readonly string[] Roles = ["owner", "sub", "switch"];
    public static readonly string[] RoleNames = ["Owner", "Sub", "Switch"];
    public static readonly string[] Seeking = ["owner", "sub", "either"];
    public static readonly string[] SeekingNames = ["an Owner", "a Sub", "either"];
    public static readonly string[] DataCenters = ["aether", "crystal", "dynamis", "primal", "chaos", "light", "shadow", "elemental", "gaia", "mana", "meteor", "materia"];
    public static readonly string[] DataCenterNames = ["Aether", "Crystal", "Dynamis", "Primal", "Chaos", "Light", "Shadow", "Elemental", "Gaia", "Mana", "Meteor", "Materia"];

    public static string NameOf(string[] ids, string[] names, string? id) =>
        id is null ? "" : Array.IndexOf(ids, id) is var i and >= 0 ? names[i] : id;

    private readonly PluginConfig config;
    private readonly RelayClient relay;
    private readonly MatchKeyService matchKey;
    private readonly PeerMessenger messenger;
    private readonly CancellationTokenSource shutdown = new();
    private readonly HashSet<string> pictureDownloads = new();
    private readonly HashSet<string> missingPictures = new();
    private bool introduced;
    private bool introducing;
    private DateTime nextIntroduceUtc = DateTime.MinValue;
    private DateTime nextMatchPollUtc = DateTime.MinValue;
    private DateTime? hintedPollUtc;
    private DateTime nextRefreshCheckUtc = DateTime.MinValue;
    private bool matchPollRunning;

    public LookingForPairService(PluginConfig config, RelayClient relay, MatchKeyService matchKey, PeerMessenger messenger)
    {
        this.config = config;
        this.relay = relay;
        this.matchKey = matchKey;
        this.messenger = messenger;
        messenger.LfpHintReceived += OnHint;
    }

    public void Dispose()
    {
        messenger.LfpHintReceived -= OnHint;
        shutdown.Cancel();
    }

    private LookingForPairState State => config.LookingForPair;

    public bool Attested => State.AdultAttestedAt is not null;
    public bool Busy { get; private set; }
    public string? LastError { get; private set; }
    public string? LastNotice { get; private set; }

    /// The last browse page(s), newest first; NextCursor is null once there are no more.
    public List<LfpProfileWire> Browse { get; } = new();
    public string? NextCursor { get; private set; }
    public bool BrowseLoaded { get; private set; }
    public string? FilterRole { get; set; }
    public string? FilterSeeking { get; set; }
    public string? FilterDataCenter { get; set; }
    public string? FilterRace { get; set; }
    public string? FilterGender { get; set; }

    public LfpProfileWire? CodeResult { get; private set; }
    public List<LfpProfileWire> Likes { get; private set; } = new();
    public List<LfpBlockWire> Blocks { get; private set; } = new();

    /// A new match: the chat window can open on it.
    public event Action<LfpMatchState>? MatchMade;
    /// A match ended (by either person or a block).
    public event Action<LfpMatchState>? MatchEnded;
    /// Clicking a match toast asks for its chat.
    public event Action<Guid>? OpenChatRequested;

    /// Whether the live relay connection is needed for Looking for pair (spec: relay-messaging).
    public bool WantsConnection => State.Published || State.HasActiveMatch;

    public int UnseenMatches => State.Matches.Count(m => m.IsActive && !m.Seen);

    public void MarkMatchesSeen()
    {
        if (UnseenMatches == 0) return;
        foreach (var m in State.Matches) m.Seen = true;
        config.Save();
    }

    public void DismissMessages()
    {
        LastError = null;
        LastNotice = null;
    }

    // ---- gate ------------------------------------------------------------------------------------------------------

    public void Attest()
    {
        State.AdultAttestedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        config.Save();
        EnsureIntroduced();
    }

    /// Withdrawing the confirmation deletes the profile; matches and their chats stay.
    public void WithdrawAttestation()
    {
        Run(async ct =>
        {
            if (State.Published) await relay.LfpDeleteProfileAsync(ct).ConfigureAwait(false);
        }, () =>
        {
            ForgetProfileLocally();
            State.AdultAttestedAt = null;
            Browse.Clear();
            BrowseLoaded = false;
            CodeResult = null;
            config.Save();
        });
    }

    // ---- profile ---------------------------------------------------------------------------------------------------

    /// Null when the fields are fine; otherwise what to fix, named for the user.
    public static string? Validate(LfpProfileFields f)
    {
        var name = f.DisplayName.Trim();
        if (name.EnumerateRunes().Count() is < 2 or > 32) return "Display name must be 2 to 32 characters.";
        if ((f.Pronouns?.Trim().EnumerateRunes().Count() ?? 0) > 24) return "Pronouns can be at most 24 characters.";
        if (f.Bio.Trim().EnumerateRunes().Count() > 1000) return "Bio can be at most 1000 characters.";
        if (name.Any(char.IsControl) || (f.Pronouns ?? "").Any(char.IsControl)) return "Display name and pronouns can't contain control characters.";
        if (f.Bio.Any(c => char.IsControl(c) && c is not '\n' and not '\t' and not '\r')) return "Bio can't contain control characters.";
        if (!Races.Contains(f.Race) || !Genders.Contains(f.Gender) || !Roles.Contains(f.Role) || !Seeking.Contains(f.Seeking)) return "Pick a race, gender, role and what you're seeking.";
        if (f.DataCenter is not null && !DataCenters.Contains(f.DataCenter)) return "Pick a data center or none.";
        return null;
    }

    public void SaveProfile(LfpProfileFields fields, string visibility, bool hidden)
    {
        if (Validate(fields) is { } problem)
        {
            LastError = problem;
            return;
        }
        var copy = fields.Clone();
        Run(async ct =>
        {
            await IntroduceAsync(ct).ConfigureAwait(false);
            var profile = await relay.LfpPutProfileAsync(copy, visibility, hidden, matchKey.EnsurePublicKey(), ct).ConfigureAwait(false);
            if (profile.Picture is null && PicturePath(State.PictureFile) is { } local && File.Exists(local))
                profile = await UploadPictureAsync(local, ct).ConfigureAwait(false);
            return profile;
        }, profile =>
        {
            State.Fields = copy;
            State.Visibility = visibility;
            State.Hidden = hidden;
            Adopt(profile);
            LastNotice = "Profile saved.";
        });
    }

    public void NewCode() => Run(relay.LfpNewCodeAsync, profile =>
    {
        Adopt(profile);
        LastNotice = "New profile code made - the old one no longer works.";
    });

    public void DeleteProfile() => Run(relay.LfpDeleteProfileAsync, () =>
    {
        ForgetProfileLocally();
        LastNotice = "Your profile is deleted. Your matches and their chats stay.";
    });

    /// Shrinks the picture to the profile size and keeps it locally (for re-uploads after expiry). With a profile it's
    /// uploaded now; without one it goes up when the profile is created.
    public void SetPicture(string sourcePath)
    {
        var published = State.Published;
        Run(async ct =>
        {
            var prepared = await ChatImageService.PrepareAsync(sourcePath, PictureMaxSide, RelayClient.LfpPictureMaxBytes, "use").ConfigureAwait(false);
            Directory.CreateDirectory(PictureFolder);
            var fileName = "mine" + prepared.Extension;
            foreach (var old in Directory.EnumerateFiles(PictureFolder, "mine.*")) File.Delete(old);
            await File.WriteAllBytesAsync(Path.Combine(PictureFolder, fileName), prepared.Bytes, ct).ConfigureAwait(false);
            LfpProfileWire? profile = published
                ? await relay.LfpPutPictureAsync(prepared.Bytes, prepared.Extension == ".png" ? "image/png" : "image/jpeg", ct).ConfigureAwait(false)
                : null;
            return (profile, fileName);
        }, result =>
        {
            State.PictureFile = result.fileName;
            config.Save();
            if (result.profile is { } own) Adopt(own);
            LastNotice = result.profile is null ? "Picture ready - it's uploaded when you create your profile." : "Picture updated.";
        });
    }

    public void RemovePicture()
    {
        if (!State.Published)
        {
            DeleteLocalPicture();
            config.Save();
            return;
        }
        Run(relay.LfpRemovePictureAsync, profile =>
        {
            DeleteLocalPicture();
            Adopt(profile);
        });
    }

    private async Task<LfpProfileWire> UploadPictureAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return await relay.LfpPutPictureAsync(bytes, path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg", ct).ConfigureAwait(false);
    }

    private void Adopt(LfpProfileWire own)
    {
        State.Published = true;
        State.ProfileId = own.ProfileId;
        State.ProfileCode = own.Code;
        if (own.Visibility is { } v) State.Visibility = v;
        if (own.Hidden is { } h) State.Hidden = h;
        OwnProfile = own;
        config.Save();
    }

    /// The relay's copy of this install's profile, once read this session.
    public LfpProfileWire? OwnProfile { get; private set; }

    private void ForgetProfileLocally()
    {
        State.Published = false;
        State.ProfileId = null;
        State.ProfileCode = null;
        OwnProfile = null;
        Likes.Clear();
        DeleteLocalPicture();
        config.Save();
        matchKey.DropIfUnused();
    }

    private void DeleteLocalPicture()
    {
        if (PicturePath(State.PictureFile) is { } path)
        {
            try { File.Delete(path); } catch (Exception ex) { Plugin.Log.Debug($"Profile picture not deleted: {ex.Message}"); }
        }
        State.PictureFile = null;
    }

    /// Introduces the device and syncs the profile once a session: a profile the relay dropped after a long break is
    /// published again from the local copy.
    public void EnsureIntroduced()
    {
        if (introduced || introducing || !Attested || DateTime.UtcNow < nextIntroduceUtc) return;
        introducing = true;
        nextIntroduceUtc = DateTime.UtcNow.AddMinutes(1);
        _ = Task.Run(async () =>
        {
            try
            {
                var own = await relay.LfpMeAsync(shutdown.Token).ConfigureAwait(false);
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    introduced = true;
                    if (own is not null) Adopt(own);
                    else if (State.Published && Validate(State.Fields) is null) SaveProfile(State.Fields, State.Visibility, State.Hidden);
                    else if (State.Published) ForgetProfileLocally();
                    nextMatchPollUtc = DateTime.MinValue;
                }).ConfigureAwait(false);
            }
            catch (RelayException ex)
            {
                Plugin.Log.Information($"Looking for pair: couldn't reach the relay ({ex.Code}).");
            }
            finally
            {
                introducing = false;
            }
        }, shutdown.Token);
    }

    private async Task IntroduceAsync(CancellationToken ct)
    {
        if (introduced) return;
        await relay.LfpMeAsync(ct).ConfigureAwait(false);
        introduced = true;
    }

    // ---- browse ----------------------------------------------------------------------------------------------------

    public void LoadBrowse(bool more = false)
    {
        if (more && NextCursor is null) return;
        var query = BuildQuery(more ? NextCursor : null);
        Run(async ct =>
        {
            await IntroduceAsync(ct).ConfigureAwait(false);
            return await relay.LfpBrowseAsync(query, ct).ConfigureAwait(false);
        }, page =>
        {
            if (!more) Browse.Clear();
            Browse.AddRange(page.Profiles.Where(p => !State.SkippedProfileIds.Contains(p.ProfileId) && Browse.All(b => b.ProfileId != p.ProfileId)));
            NextCursor = page.NextCursor;
            BrowseLoaded = true;
        });
    }

    private string BuildQuery(string? cursor)
    {
        var parts = new List<string>();
        void Add(string key, string? value) { if (!string.IsNullOrEmpty(value)) parts.Add($"{key}={Uri.EscapeDataString(value)}"); }
        Add("role", FilterRole);
        Add("seeking", FilterSeeking);
        Add("dataCenter", FilterDataCenter);
        Add("race", FilterRace);
        Add("gender", FilterGender);
        Add("cursor", cursor);
        return string.Join("&", parts);
    }

    public void Skip(LfpProfileWire profile)
    {
        if (!State.SkippedProfileIds.Contains(profile.ProfileId)) State.SkippedProfileIds.Add(profile.ProfileId);
        if (State.SkippedProfileIds.Count > MaxSkipped) State.SkippedProfileIds.RemoveRange(0, State.SkippedProfileIds.Count - MaxSkipped);
        Browse.RemoveAll(p => p.ProfileId == profile.ProfileId);
        config.Save();
    }

    public void LookupCode(string typed)
    {
        CodeResult = null;
        Run(async ct =>
        {
            await IntroduceAsync(ct).ConfigureAwait(false);
            return await relay.LfpLookupCodeAsync(typed.Trim(), ct).ConfigureAwait(false);
        }, profile => CodeResult = profile);
    }

    public void ClearCodeResult() => CodeResult = null;

    // ---- likes, matches, blocks --------------------------------------------------------------------------------------

    public void Like(LfpProfileWire profile)
    {
        if (!State.Published)
        {
            LastError = "Create your profile first - a match shows both of you to each other.";
            return;
        }
        Run(ct => relay.LfpLikeAsync(profile.ProfileId, ct), result =>
        {
            Browse.RemoveAll(p => p.ProfileId == profile.ProfileId);
            if (CodeResult?.ProfileId == profile.ProfileId) CodeResult = null;
            if (result.Matched && result.Match is { } match)
            {
                Merge([match]);
            }
            else
            {
                if (Likes.All(l => l.ProfileId != profile.ProfileId)) Likes.Insert(0, profile);
                LastNotice = $"You liked {profile.Fields.DisplayName}. If they like you back, you'll both be told.";
            }
        });
    }

    public void Unlike(LfpProfileWire profile) => Run(ct => relay.LfpUnlikeAsync(profile.ProfileId, ct), () => Likes.RemoveAll(l => l.ProfileId == profile.ProfileId));

    public void LoadLikes() => Run(relay.LfpLikesAsync, likes => Likes = likes);

    public void LoadBlocks() => Run(relay.LfpBlocksAsync, blocks => Blocks = blocks);

    public void Unmatch(LfpMatchState match) => Run(ct => relay.LfpUnmatchAsync(match.MatchId, ct), () => End(match, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    public void BlockProfile(LfpProfileWire profile) => Run(ct => relay.LfpBlockAsync(profile.ProfileId, null, ct), () =>
    {
        Browse.RemoveAll(p => p.ProfileId == profile.ProfileId);
        Likes.RemoveAll(p => p.ProfileId == profile.ProfileId);
        if (CodeResult?.ProfileId == profile.ProfileId) CodeResult = null;
        foreach (var m in State.Matches.Where(m => m.IsActive && m.PeerProfileId == profile.ProfileId).ToList())
            End(m, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        LastNotice = $"{profile.Fields.DisplayName} is blocked. Neither of you will see the other.";
    });

    public void BlockMatch(LfpMatchState match) => Run(ct => relay.LfpBlockAsync(null, match.MatchId, ct), () =>
    {
        End(match, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        LastNotice = $"{match.Peer.DisplayName} is blocked. Neither of you will see the other.";
    });

    public void Unblock(LfpBlockWire block) => Run(ct => relay.LfpUnblockAsync(block.BlockId, ct), () => Blocks.RemoveAll(b => b.BlockId == block.BlockId));

    private void OnHint() => hintedPollUtc = DateTime.UtcNow.AddSeconds(2);

    private void PollMatches()
    {
        if (matchPollRunning) return;
        matchPollRunning = true;
        nextMatchPollUtc = DateTime.UtcNow + MatchPoll;
        _ = Task.Run(async () =>
        {
            try
            {
                var matches = await relay.LfpMatchesAsync(shutdown.Token).ConfigureAwait(false);
                await Plugin.Framework.RunOnFrameworkThread(() => Merge(matches)).ConfigureAwait(false);
            }
            catch (RelayException ex)
            {
                Plugin.Log.Information($"Looking for pair: match check failed ({ex.Code}).");
            }
            finally
            {
                matchPollRunning = false;
            }
        }, shutdown.Token);
    }

    /// Folds the relay's view of matches into the local list: new ones are announced, ended ones become read-only.
    private void Merge(IEnumerable<LfpMatchWire> fromRelay)
    {
        foreach (var wire in fromRelay)
        {
            var local = State.FindMatch(wire.MatchId);
            if (local is null)
            {
                if (wire.EndedAt is not null) continue;
                local = new LfpMatchState
                {
                    MatchId = wire.MatchId,
                    Side = wire.Side,
                    CreatedAt = wire.CreatedAt,
                    PeerMatchKeyX = wire.PeerMatchPublicKey.X,
                    PeerMatchKeyY = wire.PeerMatchPublicKey.Y,
                };
                State.Matches.Add(local);
            }
            local.PeerProfileId = wire.Peer.ProfileId;
            local.Peer = wire.Peer.Fields.ToFields();
            local.PeerPicture = wire.Peer.Picture;
            if (wire.EndedAt is { } ended && local.IsActive) End(local, ended);
        }
        foreach (var match in State.Matches.Where(m => m.IsActive && !m.Announced).ToList())
            Announce(match);
        config.Save();
    }

    private void Announce(LfpMatchState match)
    {
        match.Announced = true;
        var notification = Plugin.NotificationManager.AddNotification(new Notification
        {
            Title = "It's a match!",
            Content = $"You and {match.Peer.DisplayName} liked each other. Say hi in Chat.",
            Type = NotificationType.Success,
            Minimized = false,
        });
        var id = match.LocalId;
        notification.Click += _ => OpenChatRequested?.Invoke(id);
        MatchMade?.Invoke(match);
    }

    private void End(LfpMatchState match, long at)
    {
        if (!match.IsActive) return;
        match.EndedAt = at;
        messenger.ForgetMatch(match.MatchId);
        config.Save();
        MatchEnded?.Invoke(match);
        matchKey.DropIfUnused();
    }

    /// Removes an ended match and its local record (its chat history is cleared separately).
    public void Forget(LfpMatchState match)
    {
        if (match.IsActive) return;
        State.Matches.Remove(match);
        config.Save();
    }

    // ---- pictures ----------------------------------------------------------------------------------------------------

    private static string PictureFolder => Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "lfp");

    private static string? PicturePath(string? fileName) =>
        fileName is { Length: > 0 } && fileName == Path.GetFileName(fileName) ? Path.Combine(PictureFolder, fileName) : null;

    /// This install's own picture file, if any.
    public string? OwnPicturePath => PicturePath(State.PictureFile) is { } p && File.Exists(p) ? p : null;

    /// A local copy of a profile picture by its relay path, fetched in the background the first time it's asked for.
    /// Null while it loads, when pictures are turned off, or when it's gone.
    public string? PictureFor(string? relayPath)
    {
        if (relayPath is null || !State.ShowPictures) return null;
        var key = relayPath[(relayPath.LastIndexOf('/') + 1)..];
        if (key.Length != 43 || key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return null;
        foreach (var ext in new[] { ".png", ".jpg" })
        {
            var path = Path.Combine(PictureFolder, "cache", key + ext);
            if (File.Exists(path)) return path;
        }
        if (missingPictures.Contains(key) || !pictureDownloads.Add(key)) return null;
        _ = Task.Run(async () =>
        {
            try
            {
                var (bytes, type) = await relay.LfpFetchPictureAsync(relayPath, shutdown.Token).ConfigureAwait(false);
                var folder = Path.Combine(PictureFolder, "cache");
                Directory.CreateDirectory(folder);
                await File.WriteAllBytesAsync(Path.Combine(folder, key + (type == "image/jpeg" ? ".jpg" : ".png")), bytes, shutdown.Token).ConfigureAwait(false);
            }
            catch (RelayException ex) when (ex.Code == "not_found")
            {
                await Plugin.Framework.RunOnFrameworkThread(() => missingPictures.Add(key)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RelayException or IOException or UnauthorizedAccessException)
            {
                Plugin.Log.Debug($"Profile picture not fetched: {ex.Message}");
            }
            finally
            {
                await Plugin.Framework.RunOnFrameworkThread(() => pictureDownloads.Remove(key)).ConfigureAwait(false);
            }
        }, shutdown.Token);
        return null;
    }

    // ---- ticking -----------------------------------------------------------------------------------------------------

    /// Main thread, about once a second, while logged in.
    public void Tick()
    {
        if (!Attested && !State.HasActiveMatch) return;
        if (Attested) EnsureIntroduced();
        var now = DateTime.UtcNow;
        if ((State.Published || State.Matches.Count > 0) && (now >= nextMatchPollUtc || hintedPollUtc is { } h && now >= h))
        {
            hintedPollUtc = null;
            PollMatches();
        }
        if (State.Published && introduced && now >= nextRefreshCheckUtc)
        {
            nextRefreshCheckUtc = now.AddMinutes(10);
            var last = DateTimeOffset.FromUnixTimeSeconds(State.LastRefreshAt).UtcDateTime;
            if (now - last >= RefreshEvery) Refresh();
        }
    }

    private void Refresh()
    {
        State.LastRefreshAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        config.Save();
        _ = Task.Run(async () =>
        {
            try
            {
                var profile = await relay.LfpRefreshAsync(shutdown.Token).ConfigureAwait(false);
                if (profile.Picture is null && OwnPicturePathOffThread() is { } local)
                    profile = await UploadPictureAsync(local, shutdown.Token).ConfigureAwait(false);
                await Plugin.Framework.RunOnFrameworkThread(() => Adopt(profile)).ConfigureAwait(false);
            }
            catch (RelayException ex) when (ex.Code == "not_found")
            {
                // Gone after a year away: publish it again from the local copy.
                await Plugin.Framework.RunOnFrameworkThread(() => SaveProfile(State.Fields, State.Visibility, State.Hidden)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RelayException or IOException)
            {
                Plugin.Log.Information($"Looking for pair: profile refresh failed ({ex.Message}).");
            }
        }, shutdown.Token);
    }

    private string? OwnPicturePathOffThread()
    {
        var file = State.PictureFile;
        return PicturePath(file) is { } p && File.Exists(p) ? p : null;
    }

    // ---- plumbing ----------------------------------------------------------------------------------------------------

    private void Run<T>(Func<CancellationToken, Task<T>> call, Action<T> onSuccess)
    {
        if (Busy) return;
        Busy = true;
        LastError = null;
        LastNotice = null;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await call(shutdown.Token).ConfigureAwait(false);
                await Plugin.Framework.RunOnFrameworkThread(() => onSuccess(result)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is RelayException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                var message = Describe(ex);
                await Plugin.Framework.RunOnFrameworkThread(() => LastError = message).ConfigureAwait(false);
            }
            finally
            {
                await Plugin.Framework.RunOnFrameworkThread(() => Busy = false).ConfigureAwait(false);
            }
        }, shutdown.Token);
    }

    private void Run(Func<CancellationToken, Task> call, Action onSuccess) =>
        Run(async ct => { await call(ct).ConfigureAwait(false); return true; }, _ => onSuccess());

    private static string Describe(Exception ex) => ex switch
    {
        RelayException { Code: "not_found" } => "Not found - it may have been hidden, deleted, given a new code, or it's someone you've blocked.",
        RelayException { Code: "rate_limited", RetryAfterSeconds: { } s } => $"You've hit today's limit for that. Try again in {TimeSpan.FromSeconds(s):h\\h\\ m\\m}.",
        RelayException { Code: "rate_limited" or "service_unavailable" } => "The relay is busy - try again later.",
        RelayException { Code: "payload_too_large" } => "That picture is too large.",
        RelayException { Code: "network" } => "The relay couldn't be reached.",
        RelayException { Code: "invalid_request" } => "The relay didn't accept that. Check the fields and try again.",
        RelayException r => $"The relay refused that ({r.Code}).",
        InvalidOperationException => ex.Message,
        _ => "Something went wrong saving files - see /xllog.",
    };
}

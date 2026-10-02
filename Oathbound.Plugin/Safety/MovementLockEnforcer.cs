using Oathbound.Plugin.Commands;

namespace Oathbound.Plugin.Safety;

/// Each instance claims its own MovementLockService token, so releasing one rule kind never lifts another's claim.
public sealed class MovementLockEnforcer : IRestrictionEnforcer
{
    private readonly MovementLockService movementLock;
    private readonly string ownerToken;

    public MovementLockEnforcer(MovementLockService movementLock, string ownerToken)
    {
        this.movementLock = movementLock;
        this.ownerToken = ownerToken;
    }

    public bool IsAvailable => movementLock.IsImmobilizeAvailable;
    public void Engage() => movementLock.EngageImmobilize(ownerToken);
    public void Release() => movementLock.ReleaseImmobilize(ownerToken);
}

using UnityEngine;

namespace TankIO
{
    // things a shell can meet and a tank can attack: tanks and HQs.
    // one uniform list keeps the shell sweep and the whole attack path (chase, turret, range checks)
    // NetworkObjectId is satisfied by NetworkBehaviour for free.
    public interface IShellTarget
    {
        ulong NetworkObjectId { get; }

        // who commands this entity: a human's client id, or a bot's assigned id.
        // bots' entities are all network-owned by the server, so OwnerClientId cannot tell two bots apart.
        ulong CommanderId { get; }

        // gameplay position from replicated state, never the drawn transform (which carries cosmetic offsets)
        Vector3 PositionAtTime(double time);

        // the locally drawn position, for cosmetic aiming only (turret swings, shell visuals)
        Vector3 DrawnPosition { get; }

        // contact distance for the shell sweep, and the extra range attackers get against big targets
        float HitRadius { get; }

        // an HQ mid-move is a non-entity: shells pass through it and attack orders drop it
        bool Attackable { get; }

        // attackerObjectId names the shooting tank, so an idle victim can fire back at it
        void TakeShellHit(int shellId, float hitFraction, int damage, ulong attackerCommanderId, ulong attackerObjectId);
    }
}

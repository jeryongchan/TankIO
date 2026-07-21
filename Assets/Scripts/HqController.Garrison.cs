using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace TankIO
{
    // garrison fire, and the aggression memory that aims it
    public partial class HqController
    {
        // garrison fire: undodgeable, single-victim, troop-scaled. only tanks currently attacking this
        // owner's HQ or tanks are valid victims. driving past costs nothing, attacking is answered.
        private const float GarrisonRange = 10f; // more range than tank so can outrange tank.
        private const float GarrisonDamagePerTroopPerSecond = 0.02f; // full 1000-troop HQ = 20 dps, about four full tanks
        private const double GarrisonCheckInterval = 0.3;

        // who the garrison is shooting, 0 = nobody. replicated only so every machine can draw the tracer;
        // the damage itself is server arithmetic on the existing health path.
        private readonly NetworkVariable<ulong> garrisonVictimId = new NetworkVariable<ulong>();

        private double garrisonCheckTimer; // server only
        private float garrisonDamageRemainder; // server only: fractional damage carried between checks
        private LineRenderer garrisonTracer; // debug local cosmetic, reads garrisonVictimId

        // who has attacked this owner recently: time their tank shell last hit us.
        // only tank shells mark aggression, garrison fire never does, so an HQ defending itself is not considered aggressor. server only.
        private const double AggressionMemorySeconds = 60.0;
        private readonly Dictionary<ulong, double> aggressionByClient = new Dictionary<ulong, double>();

        // garrison fire, undodgeable by design, concentrated on one victim at a time
        void UpdateGarrison(double now)
        {
            if (now < garrisonCheckTimer)
                return;
            garrisonCheckTimer = now + GarrisonCheckInterval;

            if (!Attackable) // mid-move: cannot defend either
            {
                garrisonVictimId.Value = 0;
                return;
            }

            Vector3 center = TileGrid.Instance.TileToWorldCenter(HomeTile);
            TankController victim = TankController.TankFromObjectId(garrisonVictimId.Value);
            if (!IsValidGarrisonVictim(victim, center, now))
            {
                victim = NearestGarrisonVictim(center, now);
                garrisonVictimId.Value = victim != null ? victim.NetworkObjectId : 0;
            }
            if (victim == null)
            {
                garrisonDamageRemainder = 0f;
                return;
            }

            // whole damage per check, fractional part carried, so low garrisons chip slowly instead of
            // rounding to a free pass (or to a full point they didn't earn)
            float damage =
                (float)HomeTroops(now) * GarrisonDamagePerTroopPerSecond * (float)GarrisonCheckInterval
                + garrisonDamageRemainder;
            int wholeDamage = (int)damage;
            garrisonDamageRemainder = damage - wholeDamage;
            if (wholeDamage > 0)
                victim.TakeGarrisonDamage(wholeDamage);
        }

        // a shell from attackerClientId's tank hit this owner's HQ or a tank: the garrison remembers.
        // garrison damage never routes here, so defending cannot mark anyone. server only.
        public void MarkAggressor(ulong attackerClientId)
        {
            if (attackerClientId == OwnerClientId)
                return;
            aggressionByClient[attackerClientId] = NetworkManager.ServerTime.Time;
        }

        bool IsAggressor(ulong clientId, double now)
        {
            return aggressionByClient.TryGetValue(clientId, out double lastHitTime)
                && now - lastHitTime <= AggressionMemorySeconds;
        }

        // valid: alive, in range, and owned by a client whose tanks hit this owner within the memory window.
        bool IsValidGarrisonVictim(TankController tank, Vector3 center, double now)
        {
            if (tank == null || tank.OwnerClientId == OwnerClientId)
                return false;
            if (!IsAggressor(tank.OwnerClientId, now))
                return false;
            Vector3 toTank = tank.PositionAtTime(now) - center;
            toTank.y = 0f;
            return toTank.sqrMagnitude <= GarrisonRange * GarrisonRange;
        }

        TankController NearestGarrisonVictim(Vector3 center, double now)
        {
            TankController nearest = null;
            float nearestDistanceSquared = float.MaxValue;
            foreach (TankController tank in TankController.SpawnedTanks)
            {
                if (!IsValidGarrisonVictim(tank, center, now))
                    continue;
                Vector3 toTank = tank.PositionAtTime(now) - center;
                toTank.y = 0f;
                if (toTank.sqrMagnitude < nearestDistanceSquared)
                {
                    nearest = tank;
                    nearestDistanceSquared = toTank.sqrMagnitude;
                }
            }
            return nearest;
        }

        // debug placeholder for the real bullet tracer volley.
        void RenderGarrisonTracer()
        {
            IShellTarget victim = ShellSystem.TargetFromObjectId(garrisonVictimId.Value);
            if (victim == null)
            {
                if (garrisonTracer != null)
                    garrisonTracer.enabled = false;
                return;
            }
            if (garrisonTracer == null)
            {
                GameObject tracerObject = new GameObject("GarrisonTracer");
                tracerObject.transform.SetParent(transform, false);
                garrisonTracer = tracerObject.AddComponent<LineRenderer>();
                garrisonTracer.material = new Material(Shader.Find("Sprites/Default"));
                garrisonTracer.startColor = garrisonTracer.endColor = new Color(1f, 0.85f, 0.2f);
                garrisonTracer.startWidth = garrisonTracer.endWidth = 0.08f;
                garrisonTracer.positionCount = 2;
            }
            garrisonTracer.enabled = true;
            garrisonTracer.SetPosition(0, transform.position + Vector3.up * 1.2f);
            garrisonTracer.SetPosition(1, victim.DrawnPosition + Vector3.up * 0.3f);
        }
    }
}

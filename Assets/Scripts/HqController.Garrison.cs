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
        private const float GarrisonRange = 8.5f; // more range than tank so can outrange tank.
        private const float GarrisonDamagePerTroopPerSecond = 0.02f; // full 1000-troop HQ = 20 dps, about four full tanks
        private const double GarrisonCheckInterval = 0.3;

        private double garrisonCheckTimer; // server only
        private float garrisonDamageRemainder; // server only: fractional damage carried between checks
        private LineRenderer garrisonTracer; // debug local cosmetic, reads garrisonVictimId

        [SerializeField]
        private Material garrisonTracerMaterial; // the line is built in code, so this is all the look it has

        [SerializeField]
        private Transform garrisonMuzzle; // the roof turret

        [SerializeField]
        private GameObject garrisonImpactPrefab; // looping sparks: there is no per-hit event to burst from

        [SerializeField]
        private float garrisonImpactOffset = 0.4f; // from the victim's centre toward the firing HQ, onto the face the rounds strike

        private ParticleSystem garrisonImpact; // one instance, moved onto the current victim

        // who has attacked this commander recently: time their tank shell last hit us.
        // only tank shells mark aggression, garrison fire never does, so an HQ defending itself is not considered aggressor. server only.
        private const double AggressionMemorySeconds = 60.0;
        private readonly Dictionary<ulong, double> aggressionByCommander = new Dictionary<ulong, double>();

        // garrison fire, undodgeable by design, concentrated on one victim at a time
        void UpdateGarrison(double now)
        {
            if (now < garrisonCheckTimer)
                return;
            garrisonCheckTimer = now + GarrisonCheckInterval;

            if (!Attackable) // mid-move: cannot defend either
            {
                Detail.GarrisonVictimId = 0;
                return;
            }

            Vector3 center = TileGrid.Instance.TileToWorldCenter(HomeTile);
            TankController victim = TankController.TankFromObjectId(Detail.GarrisonVictimId);
            if (!IsValidGarrisonVictim(victim, center, now))
            {
                victim = NearestGarrisonVictim(center, now);
                Detail.GarrisonVictimId = victim != null ? victim.NetworkObjectId : 0;
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

        // a shell from attackerCommanderId's tank hit this commander's HQ or a tank: the garrison remembers.
        // garrison damage never routes here, so defending cannot mark anyone. server only.
        public void MarkAggressor(ulong attackerCommanderId)
        {
            if (attackerCommanderId == CommanderId)
                return;
            aggressionByCommander[attackerCommanderId] = NetworkManager.ServerTime.Time;
        }

        bool IsAggressor(ulong commanderId, double now)
        {
            return aggressionByCommander.TryGetValue(commanderId, out double lastHitTime)
                && now - lastHitTime <= AggressionMemorySeconds;
        }

        // valid: alive, in range, and commanded by someone whose tanks hit this commander within the memory window.
        bool IsValidGarrisonVictim(TankController tank, Vector3 center, double now)
        {
            if (tank == null || tank.CommanderId == CommanderId)
                return false;
            if (!IsAggressor(tank.CommanderId, now))
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

        void RenderGarrisonTracer()
        {
            IShellTarget victim = null;
            // no detail means this client is not watching the area, so there is no tracer to draw.
            // also gated to Near: past it HQs are icons and victims are not drawn
            if (Detail != null && CameraController.Lod == LodTier.Near)
                victim = ShellSystem.TargetFromObjectId(Detail.GarrisonVictimId);
            if (victim == null)
            {
                if (garrisonTracer != null)
                    garrisonTracer.enabled = false;
                if (garrisonImpact != null)
                    garrisonImpact.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                return;
            }
            if (garrisonTracer == null)
            {
                GameObject tracerObject = new GameObject("GarrisonTracer");
                tracerObject.transform.SetParent(transform, false);
                garrisonTracer = tracerObject.AddComponent<LineRenderer>();
                garrisonTracer.sharedMaterial = garrisonTracerMaterial; // the material carries the colour; an unlit shader ignores LineRenderer's vertex colours
                garrisonTracer.startWidth = garrisonTracer.endWidth = 0.25f; // the shader carves this width into strands
                garrisonTracer.positionCount = 2;
                garrisonTracer.textureMode = LineTextureMode.Tile; // uv.x counts world units: dashes keep one size at any range
                // vertex alpha carries 0-1 along the line; Tile mode leaves uv.x in world units
                garrisonTracer.startColor = new Color(1f, 1f, 1f, 0f);
                garrisonTracer.endColor = Color.white;
            }
            Vector3 muzzlePoint = garrisonMuzzle != null
                ? garrisonMuzzle.position
                : transform.position + Vector3.up * 1.2f;
            Vector3 victimPoint = victim.DrawnPosition + Vector3.up * 0.3f;

            // flat, or the line's downward slope tips the spray out of view
            Vector3 towardMuzzle = muzzlePoint - victimPoint;
            towardMuzzle.y = 0f;
            towardMuzzle = towardMuzzle.sqrMagnitude > 0.0001f ? towardMuzzle.normalized : Vector3.forward;
            Vector3 impactPoint = victimPoint + towardMuzzle * garrisonImpactOffset;

            garrisonTracer.enabled = true;
            garrisonTracer.SetPosition(0, muzzlePoint);
            garrisonTracer.SetPosition(1, impactPoint); // ends at the sparks, not inside the hull

            if (garrisonImpactPrefab == null)
                return;
            // parented so it dies with the HQ; placed in world space, since the victim changes
            if (garrisonImpact == null)
                garrisonImpact = Instantiate(garrisonImpactPrefab, transform).GetComponent<ParticleSystem>();
            // sparks come off the armour toward the shooter
            garrisonImpact.transform.SetPositionAndRotation(impactPoint, Quaternion.LookRotation(towardMuzzle));
            if (!garrisonImpact.isEmitting)
                garrisonImpact.Play(true);
        }
    }
}

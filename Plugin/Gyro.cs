using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;

namespace CrapMechanics
{
    /// <summary>0.19.14 (Andre: "is there a way to make a gyroscope with the mod? so that something always stays 'upright'?").
    /// Gyro Bearing: a bearing whose servo aims by itself. The target is the angle that keeps the upper side at the SAME tilt (around
    /// the bearing's axis) it had when the simulation started — as it was built in the garage, upright. One levels one axis; two
    /// stacked at 90 degrees level both (gimbal). It applies no torque to the ship, only to what is on top.
    /// "Down" = PhysicsStep.Gravity (the game adds up the planets' gravity at the center of the universe = the player, every step); with the
    /// axis nearly vertical (nothing to level) or without gravity (space) the target freezes. The data signal on an input becomes an
    /// offset (-1..1 x the knob's degrees) on top of the gravity target.</summary>
    internal static partial class JointSystem
    {
        static float3 gravity; static float tGrav = 9f; static bool gravOk;
        static unsafe bool Gravity(EntityManager em, float dt, out float3 g)
        {
            tGrav += dt;
            if (tGrav >= 0.1f)
            {
                tGrav = 0f; gravOk = false;
                try
                {
                    if (!stepOk) { tiStep = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<PhysicsStep>()); var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiStep); qStep = em.CreateEntityQuery(a); stepOk = true; }
                    foreach (var e in Ecs.QueryList(qStep))
                    {
                        var ps = (Ecs.HasC(em, e, tiStep) ? (PhysicsStep*)em.GetComponentDataRawRO(e, tiStep) : null); if (ps == null) continue;
                        gravity = ps->Gravity; gravOk = true; break;
                    }
                }
                catch (Exception) { gravOk = false; }
            }
            g = gravity; return gravOk && Ecs.Len(g) > 0.05f;
        }

        /// <summary>Servo target (in ServoAngle's convention: angle from qA to qB around eA, continuous) that keeps the upper
        /// side at the reference tilt; `displacement` = offset requested by the signal (rad). Returns the last target if it can't.</summary>
        static float GyroTarget(EntityManager em, Joint j, float displacement, float dt)
        {
            try
            {
                if (!Gravity(em, dt, out var g)) { if (j.spinLog++ % 600 == 0 && DetailedLog) ModLog.Info(j.name + " " + j.block.Index + ": gyro — no gravity here, holding"); return Servo(j.spinTarget); }
                Fits(em, j, out _, out var eA, out var qA, out _, out _, out _);
                var top = Ecs.Norm(Ecs.Mul(g, -1f));
                var u = Ecs.Sub(top, Ecs.Mul(eA, Ecs.Dot(top, eA)));   // "up" projected onto the gyro's plane
                if (Ecs.Len(u) < 0.15f) return Servo(j.spinTarget);       // axis nearly vertical: nothing to level
                u = Ecs.Norm(u);
                float thU = (float)System.Math.Atan2(Ecs.Dot(Ecs.Cross(qA, u), eA), Ecs.Dot(qA, u));   // where "up" is, measured the way ServoAngle measures qB
                if (!j.spinRefOk)
                {
                    float th = ServoAngle(em, j);
                    j.spinRef = thU - th; j.spinRefOk = true; j.spinTarget = 0f;
                    ModLog.Info(j.name + " " + j.block.Index + ": gyro — reference taken (top side " + (th * 57.2958f).ToString("F0") + " degrees from the base, 'up' at " + (thU * 57.2958f).ToString("F0") + " degrees): it will keep this tilt");
                }
                // wanted target for the measured angle: th = thU - spinRef (+ offset). Continuous: takes the shortest path from the previous target.
                float wanted = thU - j.spinRef + displacement;
                float d = wanted - j.spinTarget; const float PI2 = 2f * (float)System.Math.PI;
                while (d > (float)System.Math.PI) d -= PI2; while (d < -(float)System.Math.PI) d += PI2;
                j.spinTarget += d;
                return Servo(j.spinTarget);
            }
            catch (Exception ex) { if (j.spinLog++ < 3) ModLog.Warn(j.name + " " + j.block.Index + ": gyro: " + ex.Message); return Servo(j.spinTarget); }
        }
        /// <summary>ServoStep takes the measured angle to -target (or to +target with Bearing.servo_invert).</summary>
        static float Servo(float thWanted) => ServoInvert ? thWanted : -thWanted;
    }
}

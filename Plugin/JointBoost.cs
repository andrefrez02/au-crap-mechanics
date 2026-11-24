using System;
using System.Collections.Generic;
using Unity.Entities;

namespace CrapMechanics
{
    /// <summary>0.31.4 (Andre, 31/10: a nave de 97 t se despedacou no meio do voo com os thrusters a x2,96): a caixa-preta mostrou as juntas
    /// do jogo (break force 1600) a 104-192% do limite aos 185 m/s e a 200-334% aos 334 m/s — o jogo quebrando a nave porque ela acelerava
    /// e corria muito mais do que as juntas foram feitas para aguentar (com a forca normal, ~65%). [Thrusters] reinforce_joints: enquanto
    /// algum thruster no mundo estiver com multiplicador maior que 1, o _breakForce de cada junta do jogo vale o original x o maior
    /// multiplicador (x2,96 de forca = x2,96 de resistencia). Ao voltar pra 1 (ou desligar a opcao) cada junta volta ao valor do jogo.
    /// Nao acumula: guarda o original de cada junta (pelos dois blocos) e so reescreve o que ela mesma escreveu.</summary>
    internal static class JointBoost
    {
        public static bool Enabled = true;   // [Thrusters] reinforce_joints
        const float Cap = 1e7f;              // mesmo teto do reforco da base dos pistoes
        // 0.31.26 (item 11): orig stays fixed for the whole simulation and every value this class wrote is remembered. The time rewind memcpy's
        // _joints back exactly as saved [recon/TimeRewindSystem__05_rewind_02.cs:486-490], so after a rewind a joint can read the original or an
        // older reinforced value: both are ours (keep orig). Until 0.31.25 any value it had not JUST written became the new original, so each
        // rewind multiplied the reinforcement again (k>1) or left it reinforced until the recall (k==1).
        class Rec { public float orig, written; public readonly List<float> mine = new List<float>(2); public bool Ours(float bf) => bf == orig || bf == written || mine.Contains(bf); }
        static readonly Dictionary<(long, long), Rec> recs = new Dictionary<(long, long), Rec>();
        static float tStep = 9f, applied = 1f; static int lastCount = -1;

        public static void Forget() { recs.Clear(); applied = 1f; lastCount = -1; tStep = 9f; }

        /// <summary>Chamado pelo Thrusters.Step com o maior multiplicador em uso no mundo.</summary>
        public static unsafe void Step(EntityManager em, float maxMultiplier, float dt)
        {
            tStep += dt; if (tStep < 0.5f) return; tStep = 0f;
            float k = Enabled && maxMultiplier > 1.005f ? maxMultiplier : 1f;
            if (k == 1f && recs.Count == 0) { applied = 1f; return; }   // with recs it does not stop early: a rewind may bring reinforced values back
            var joints = Ecs.Joints(em, out int n); if (joints == null) return;
            int boosted = 0, restored = 0;
            for (int i = 0; i < n; i++)
            {
                var sj = &joints[i];
                float bf = sj->_breakForce;
                if (bf <= 0f) continue;   // no limit: left alone
                var key = (Ecs.K(sj->_a), Ecs.K(sj->_b));
                recs.TryGetValue(key, out var r);
                // 0.31.26 (item 26): no IsPart filter any more — in the garage there is no SpaceshipSingleton (Ecs.Joints = null) and every launch
                // recomputes _breakForce from the materials [recon_managed/SpaceshipSystem.cs:429], so nothing can pile up between launches
                if (r != null && !r.Ours(bf)) { r = null; recs.Remove(key); }   // someone else wrote it (game, Reinforce, PistonMerge): new original
                if (k == 1f)
                {
                    // back to the original, but the record is kept until the recall: a rewind can bring a reinforced value back
                    if (r != null && bf != r.orig) { sj->_breakForce = r.orig; restored++; }
                    if (r != null) r.written = r.orig;
                    continue;
                }
                if (r == null) { r = new Rec { orig = bf }; recs[key] = r; }
                float want = Math.Min(r.orig * k, Math.Max(r.orig, Cap));
                if (sj->_breakForce != want) sj->_breakForce = want;
                r.written = want; if (want != r.orig && !r.mine.Contains(want)) { if (r.mine.Count >= 16) r.mine.RemoveAt(0); r.mine.Add(want); } boosted++;
            }
            if (k == 1f) { if (restored > 0 || applied != 1f) ModLog.Info("thrusters: game joints back to their own strength (" + restored + " joint(s))"); applied = 1f; lastCount = -1; return; }
            if (Math.Abs(k - applied) > 0.001f || Math.Abs(boosted - lastCount) > Math.Max(20, lastCount / 10))
            {
                ModLog.Info("thrusters: game joints reinforced x" + k.ToString("0.##") + " (" + boosted + " joint(s)) — the strongest thruster multiplier in use; [Thrusters] reinforce_joints turns this off");
                applied = k; lastCount = boosted;
            }
        }
    }
}

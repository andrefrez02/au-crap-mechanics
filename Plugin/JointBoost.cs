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
        class Rec { public float orig, written; }
        static readonly Dictionary<(long, long), Rec> recs = new Dictionary<(long, long), Rec>();
        static float tStep = 9f, applied = 1f; static int lastCount = -1;

        public static void Forget() { recs.Clear(); applied = 1f; lastCount = -1; tStep = 9f; }

        /// <summary>Chamado pelo Thrusters.Step com o maior multiplicador em uso no mundo.</summary>
        public static unsafe void Step(EntityManager em, float maxMultiplier, float dt)
        {
            tStep += dt; if (tStep < 0.5f) return; tStep = 0f;
            float k = Enabled && maxMultiplier > 1.005f ? maxMultiplier : 1f;
            if (k == 1f && recs.Count == 0) { applied = 1f; return; }
            var joints = Ecs.Joints(em, out int n); if (joints == null) return;
            int boosted = 0, restored = 0;
            for (int i = 0; i < n; i++)
            {
                var sj = &joints[i];
                float bf = sj->_breakForce;
                if (bf <= 0f) continue;   // quebrada pelo mod (-1) ou sem limite: nao mexe
                var key = (Ecs.K(sj->_a), Ecs.K(sj->_b));
                recs.TryGetValue(key, out var r);
                // so nave LANCADA (o bloco esta numa part): o jogo copia a garagem pra simulacao com o que estiver escrito nela, e um
                // reforco na garagem viraria o "original" da proxima simulacao e acumularia a cada lancamento
                if (r == null && k != 1f && !Ecs.IsPart(em, Ecs.Parent(em, sj->_a))) continue;
                if (r != null && bf != r.written) { r = null; recs.Remove(key); }   // outro sistema (jogo ou mod) escreveu: esse e' o novo original
                if (k == 1f)
                {
                    if (r != null) { sj->_breakForce = r.orig; recs.Remove(key); restored++; }
                    continue;
                }
                if (r == null) { r = new Rec { orig = bf }; recs[key] = r; }
                float want = Math.Min(r.orig * k, Math.Max(r.orig, Cap));
                if (sj->_breakForce != want) sj->_breakForce = want;
                r.written = want; boosted++;
            }
            if (k == 1f) { if (restored > 0 || applied != 1f) ModLog.Info("thrusters: game joints back to their own strength (" + restored + " joint(s))"); recs.Clear(); applied = 1f; lastCount = -1; return; }
            if (Math.Abs(k - applied) > 0.001f || Math.Abs(boosted - lastCount) > Math.Max(20, lastCount / 10))
            {
                ModLog.Info("thrusters: game joints reinforced x" + k.ToString("0.##") + " (" + boosted + " joint(s)) — the strongest thruster multiplier in use; [Thrusters] reinforce_joints turns this off");
                applied = k; lastCount = boosted;
            }
        }
    }
}

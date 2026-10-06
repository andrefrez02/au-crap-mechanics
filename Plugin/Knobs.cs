using System;
using Il2CppInterop.Runtime;
using Unity.Entities;

namespace Mecanica
{
    /// <summary>0.19.2 (Andre: "configurar a rotacao maxima do bearing por um knob no proprio bearing"; "se adicionassemos knob nos pistoes
    /// poderiamos limitar o nivel sem logica"): knob do jogo (EPC_Actionable_Knob, continuo, ActionableData._value.x 0..1) no painel +Z:
    /// bearing: angulo do servo = 10 + 350 x valor graus (padrao 0,1 = 45); pistao: curso = (10 + 90 x valor) % do curso (padrao 1 = 100 %).
    /// A dica do ponteiro (ActionableTargetDrawCustomUIValue, FixedString64Bytes) mostra o valor, igual o knob do monitor.</summary>
    internal static class Knobs
    {
        static TypeIndex tiData, tiDica; static bool ok, falhou;
        static bool Preparar()
        {
            if (ok) return true; if (falhou) return false;
            try
            {
                tiData = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableData>());
                tiDica = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableTargetDrawCustomUIValue>());
                ok = true; return true;
            }
            catch (Exception ex) { falhou = true; Registro.Aviso("knobs: " + ex.Message); return false; }
        }

        /// <summary>Filho do bloco com ActionableData (o knob). Entity.Null se o bloco nao tem (prefab antigo).</summary>
        public static Entity Achar(EntityManager em, Entity bloco)
        {
            if (!Preparar()) return Entity.Null;
            foreach (var f in Ecs.Filhos(em, bloco)) if (Ecs.TemC(em, f, tiData)) return f;
            return BeaconIcone.AcharKnobDe(em, bloco);   // 0.19.3: na garagem o knob nao esta no Child do bloco (so no BelongingSC)
        }
        /// <summary>Valor 0..1 do knob (NaN se nao da pra ler).</summary>
        public static unsafe float Valor(EntityManager em, Entity knob)
        {
            if (!ok || !Ecs.Vivo(em, knob) || !Ecs.TemC(em, knob, tiData)) return float.NaN;
            var p = (float*)em.GetComponentDataRawRO(knob, tiData); if (p == null) return float.NaN;
            float v = p[0]; return float.IsNaN(v) ? float.NaN : Math.Max(0f, Math.Min(1f, v));
        }
        /// <summary>Texto da dica do ponteiro (ate 60 bytes UTF-8).</summary>
        public static unsafe void Dica(EntityManager em, Entity knob, string txt)
        {
            if (!ok || !Ecs.Vivo(em, knob) || !Ecs.TemC(em, knob, tiDica)) return;
            var bytes = System.Text.Encoding.UTF8.GetBytes(txt); int n = Math.Min(bytes.Length, 60);
            var p = (byte*)em.GetComponentDataRawRW(knob, tiDica); if (p == null) return;
            *(ushort*)p = (ushort)n; for (int i = 0; i < n; i++) p[2 + i] = bytes[i]; p[2 + n] = 0;
        }

        public static float GrausServo(float v) => (float)Math.Round(10f + 350f * v);
        public static float FracaoCurso(float v) => (float)Math.Round(10f + 90f * v) / 100f;

        /// <summary>Por junta, todo frame (barato): acha o knob a cada 3 s se nao tiver; le o valor; atualiza a dica quando muda.</summary>
        public static void Passo(EntityManager em, Junta j, float dt)
        {
            if (!Preparar()) return;
            j.tKnob += dt;
            // 0.19.3 (Andre: "so aparece o tooltip depois que eu inicio a simulacao"): igual o beacon — procura o knob de novo a cada 3 s
            // (na garagem ele pode trocar de entity sem o antigo morrer) e reescreve a dica quando a entity muda; log 1x quando acha
            if (!Ecs.Vivo(em, j.knob) || j.tKnob >= 3f)
            {
                if (j.tKnob < 3f && !Ecs.Vivo(em, j.knob)) { if (j.tKnob < 0.5f) return; }
                j.tKnob = 0f; var k = Achar(em, j.bloco);
                if (Ecs.Nulo(k)) { j.knob = k; return; }
                if (k.Index != j.knob.Index || k.Version != j.knob.Version)
                {
                    j.knob = k; j.knobDica = int.MinValue;
                    if (LogDetalheKnob) Registro.Info(j.nome + " " + j.bloco.Index + ": knob = entity " + k.Index + (Ecs.TemC(em, k, tiDica) ? " (tooltip ok)" : " (NO tooltip component — rebuild the bundle in the Modkit)"));
                }
            }
            if (Ecs.Nulo(j.knob)) return;
            float v = Valor(em, j.knob); if (float.IsNaN(v)) return;
            if (j.tipo == Tipo.Bearing)
            {
                int g = (int)GrausServo(v); j.grausServo = g;
                if (g != j.knobDica) { j.knobDica = g; Dica(em, j.knob, Texto("SC_Bearing_KnobServo", "Servo: {0}°").Replace("{0}", g.ToString())); if (LogDetalheKnob) Registro.Info(j.nome + " " + j.bloco.Index + ": servo knob " + g + " degrees"); }
            }
            else
            {
                int pct = (int)Math.Round(FracaoCurso(v) * 100f); j.fracaoCurso = pct / 100f;
                if (pct != j.knobDica) { j.knobDica = pct; Dica(em, j.knob, Texto("SC_Pistao_KnobCurso", "Stroke: {0}%").Replace("{0}", pct.ToString())); if (LogDetalheKnob) Registro.Info(j.nome + " " + j.bloco.Index + ": stroke knob " + pct + " %"); }
            }
        }
        public static bool LogDetalheKnob = true;
        static string Texto(string chave, string padrao)
        {
            try { var loc = Localization._cachedInstance; if (loc != null && loc._map != null && loc._map.ContainsKey(chave)) return loc._map[chave]; } catch (Exception) { }
            return padrao;
        }
    }
}

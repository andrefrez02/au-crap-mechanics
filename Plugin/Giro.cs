using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;

namespace Mecanica
{
    /// <summary>0.19.14 (Andre: "tem algum jeito de fazer um giroscopio com o mod? de forma que uma coisa fique sempre 'de pe'?").
    /// Gyro Bearing: um bearing cujo servo mira sozinho. O alvo e' o angulo que deixa o lado de cima com a MESMA inclinacao (em torno
    /// do eixo do bearing) que ele tinha quando a simulacao comecou — como foi montado na garagem, de pe. Um nivela um eixo; dois
    /// empilhados a 90 graus nivelam os dois (gimbal). Nao aplica torque na nave, so no que esta em cima.
    /// "Pra baixo" = PhysicsStep.Gravity (o jogo soma a gravidade dos planetas no centro do universo = jogador, a cada passo); com o
    /// eixo quase vertical (nada pra nivelar) ou sem gravidade (espaco) o alvo congela. O sinal de dados numa entrada vira um
    /// deslocamento (-1..1 x graus do knob) sobre o alvo da gravidade.</summary>
    internal static partial class Juntas
    {
        static float3 gravidade; static float tGrav = 9f; static bool gravOk;
        static unsafe bool Gravidade(EntityManager em, float dt, out float3 g)
        {
            tGrav += dt;
            if (tGrav >= 0.1f)
            {
                tGrav = 0f; gravOk = false;
                try
                {
                    if (!stepOk) { tiStep = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<PhysicsStep>()); var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiStep); qStep = em.CreateEntityQuery(a); stepOk = true; }
                    foreach (var e in Ecs.Lista(qStep))
                    {
                        var ps = (Ecs.TemC(em, e, tiStep) ? (PhysicsStep*)em.GetComponentDataRawRO(e, tiStep) : null); if (ps == null) continue;
                        gravidade = ps->Gravity; gravOk = true; break;
                    }
                }
                catch (Exception) { gravOk = false; }
            }
            g = gravidade; return gravOk && Ecs.Comp(g) > 0.05f;
        }

        /// <summary>Alvo do servo (na convencao do AnguloServo: angulo de qA ate qB em torno de eA, continuo) pra manter o lado de
        /// cima na inclinacao de referencia; `desloc` = deslocamento pedido pelo sinal (rad). Devolve o ultimo alvo se nao der.</summary>
        static float GiroAlvo(EntityManager em, Junta j, float desloc, float dt)
        {
            try
            {
                if (!Gravidade(em, dt, out var g)) { if (j.giroLog++ % 600 == 0 && LogDetalhe) Registro.Info(j.nome + " " + j.bloco.Index + ": gyro — no gravity here, holding"); return Servo(j.giroAlvo); }
                Encaixes(em, j, out _, out var eA, out var qA, out _, out _, out _);
                var cima = Ecs.Norm(Ecs.Mul(g, -1f));
                var u = Ecs.Sub(cima, Ecs.Mul(eA, Ecs.Dot(cima, eA)));   // "pra cima" projetado no plano do giro
                if (Ecs.Comp(u) < 0.15f) return Servo(j.giroAlvo);       // eixo quase vertical: nada a nivelar
                u = Ecs.Norm(u);
                float thU = (float)System.Math.Atan2(Ecs.Dot(Ecs.Cross(qA, u), eA), Ecs.Dot(qA, u));   // onde o "cima" esta, medido como o AnguloServo mede qB
                if (!j.giroRefOk)
                {
                    float th = AnguloServo(em, j);
                    j.giroRef = thU - th; j.giroRefOk = true; j.giroAlvo = 0f;
                    Registro.Info(j.nome + " " + j.bloco.Index + ": gyro — reference taken (top side " + (th * 57.2958f).ToString("F0") + " degrees from the base, 'up' at " + (thU * 57.2958f).ToString("F0") + " degrees): it will keep this tilt");
                }
                // alvo desejado pro angulo medido: th = thU - giroRef (+ deslocamento). Continuo: anda pelo caminho mais curto a partir do alvo anterior.
                float querido = thU - j.giroRef + desloc;
                float d = querido - j.giroAlvo; const float PI2 = 2f * (float)System.Math.PI;
                while (d > (float)System.Math.PI) d -= PI2; while (d < -(float)System.Math.PI) d += PI2;
                j.giroAlvo += d;
                return Servo(j.giroAlvo);
            }
            catch (Exception ex) { if (j.giroLog++ < 3) Registro.Aviso(j.nome + " " + j.bloco.Index + ": gyro: " + ex.Message); return Servo(j.giroAlvo); }
        }
        /// <summary>ServoPasso leva o angulo medido pra -alvo (ou pra +alvo com Bearing.servo_invert).</summary>
        static float Servo(float thQuerido) => ServoInverter ? thQuerido : -thQuerido;
    }
}

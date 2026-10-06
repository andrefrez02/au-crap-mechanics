using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Unity.Entities;

namespace Mecanica
{
    /// <summary>0.11.1/0.11.2: blocos do mod com alavancas do jogo dentro — SC_Pedal (2 pedais) e SC_Volante (1 aro).
    /// O jogo so liga UMA alavanca a porta (SCTypeJoystick tem um _actionableLeverEntity) e a raiz do joystick escreve o valor
    /// dela em TODAS as portas do bloco. Por isso esses blocos sao genericos (raiz EPC_SpaceshipComponent, sem SCTypeJoystick):
    /// o jogo monta as alavancas (clique, arrastar, binds da cadeira, som) e este arquivo copia o valor de cada uma
    /// (ActionableData._value.x) pra sua porta de saida, na ordem da posicao x (esquerda -> porta 0, direita -> porta 1).
    /// Pedais (0.11.2): alavanca comum do jogo de um sentido (EPC_Actionable_DoubleLever, 0..1 = pisar); o plugin poe nela o
    /// ActionableJoystickReturnToMid pra ela voltar sozinha pro 0 quando solta (e o ControleAnalogico a trata como joystick).</summary>
    internal class BlocoAlavancas
    {
        public Entity bloco; public string nome; public Entity[] alavancas = new Entity[0]; public Entity[] portas = new Entity[0];
        public float[] ultimo = new float[4]; public bool filhosLogados, semAlavancaAvisado, voltaMeioPosto; public int avisos;
        public float tAchar = 9f; public string[] valoresLogados = new string[4]; public bool viaBelong;
        public bool combina;    // 0.12.1: pedais / turret seat — UMA saida = direita - esquerda (-1..1)
        public bool soDouble;   // 0.12.0: turret seat — so as alavancas comuns (gatilhos); a alavanca de altura do seat (EPC_Actionable_Lever) fica de fora
    }

    internal static class Alavancas
    {
        static readonly Dictionary<long, BlocoAlavancas> blocos = new Dictionary<long, BlocoAlavancas>();
        static TypeIndex tiData, tiLever, tiDouble, tiBelong, tiVoltaMeio, tiBotao; static EntityQuery qLevers; static bool ok, falhou, voltaMeioOk;

        public static bool Tem(long k) => blocos.ContainsKey(k);
        public static void Esquecer() { blocos.Clear(); }
        public static void Novo(Entity e, string nome, bool soDouble = false) { blocos[Ecs.K(e)] = new BlocoAlavancas { bloco = e, nome = nome, soDouble = soDouble, combina = soDouble || nome == "SC_Pedal" }; if (!soDouble) Registro.Info(nome + " found: block " + e.Index + ":" + e.Version); }

        public static string Estado(EntityManager em)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var p in blocos.Values)
            {
                sb.Append("  ").Append(p.nome).Append(' ').Append(p.bloco.Index).Append(": ").Append(p.alavancas.Length).Append(" lever(s), ").Append(p.portas.Length).Append(" port(s)");
                for (int i = 0; i < p.alavancas.Length && i < 4; i++) sb.Append(" [").Append(i).Append("]=").Append(p.ultimo[i].ToString("F2"));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        static bool Preparar(EntityManager em)
        {
            if (ok) return true; if (falhou) return false;
            try
            {
                tiData = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableData>());
                tiLever = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableLever>());
                tiDouble = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableDoubleLever>());
                tiBelong = TypeManager.GetTypeIndex(Il2CppType.Of<BelongingSC>());
                tiBotao = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableButton>());   // 0.12.2: pedais = botoes
                var a = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ComponentType>(2);
                a[0] = ComponentType.ReadOnly(tiBelong); a[1] = ComponentType.ReadOnly(tiData);
                qLevers = em.CreateEntityQuery(a);
                try { tiVoltaMeio = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableJoystickReturnToMid>()); voltaMeioOk = true; }
                catch (Exception ex) { Registro.Aviso("levers: ActionableJoystickReturnToMid type: " + ex.Message + " (the pedals won't return on their own)"); }
                ok = true; return true;
            }
            catch (Exception ex) { falhou = true; Registro.Erro("levers: ActionableData/ActionableLever types: " + ex.Message); return false; }
        }

        static bool EhAlavanca(EntityManager em, Entity f) =>
            em.HasComponent(f, ComponentType.ReadOnly(tiData)) && (em.HasComponent(f, ComponentType.ReadOnly(tiLever)) || em.HasComponent(f, ComponentType.ReadOnly(tiDouble)) || em.HasComponent(f, ComponentType.ReadOnly(tiBotao)));

        public static void Passo(EntityManager em, float dt)
        {
            if (blocos.Count == 0) return;
            if (!Preparar(em)) return;
            var mortos = new List<long>();
            foreach (var kv in blocos)
            {
                var p = kv.Value;
                try
                {
                    if (!Ecs.Vivo(em, p.bloco)) { mortos.Add(kv.Key); continue; }
                    PassoBloco(em, p, dt);
                }
                catch (Exception ex) { if (p.avisos++ < 5) Registro.Aviso(p.nome + " " + p.bloco.Index + ": " + ex.Message); }
            }
            foreach (var k in mortos) blocos.Remove(k);
        }

        static unsafe void PassoBloco(EntityManager em, BlocoAlavancas p, float dt)
        {
            // acha as alavancas (filhos do bloco com ActionableData + Lever/DoubleLever) e as portas, de novo a cada 5 s (o jogo pode recriar)
            p.tAchar += dt;
            if (p.tAchar >= 5f || p.alavancas.Length == 0)
            {
                p.tAchar = 0f;
                var achadas = new List<(Entity e, float x)>();
                foreach (var f in Ecs.Filhos(em, p.bloco)) if (EhAlavanca(em, f) && (!p.soDouble || em.HasComponent(f, ComponentType.ReadOnly(tiDouble)))) achadas.Add((f, Ecs.LT(em, f).Position.x));
                if (achadas.Count == 0)   // as alavancas podem nao ter Parent: acha pelo BelongingSC._sc (igual as etiquetas)
                {
                    foreach (var f in Ecs.Lista(qLevers))
                    {
                        var b = (Ecs.TemC(em, f, tiBelong) ? (BelongingSC*)em.GetComponentDataRawRO(f, tiBelong) : null);
                        if (b == null || b->_sc.Index != p.bloco.Index || b->_sc.Version != p.bloco.Version || !EhAlavanca(em, f)) continue;
                        if (p.soDouble && !em.HasComponent(f, ComponentType.ReadOnly(tiDouble))) continue;
                        float x = 0f; try { if (em.HasComponent(f, ComponentType.ReadOnly(Ecs.tiLT))) x = Ecs.LT(em, f).Position.x; } catch (Exception) { }
                        achadas.Add((f, x));
                    }
                    if (achadas.Count > 0 && !p.viaBelong) { p.viaBelong = true; Registro.Info(p.nome + " " + p.bloco.Index + ": levers found via BelongingSC (not children of the block)"); }
                }
                achadas.Sort((a, b) => System.Math.Abs(a.x - b.x) > 0.01f ? a.x.CompareTo(b.x) : a.e.Index.CompareTo(b.e.Index));   // 0.12.3: ordem estavel (mesmo x = trocava esquerdo/direito)
                var lista = new Entity[achadas.Count]; for (int i = 0; i < achadas.Count; i++) lista[i] = achadas[i].e;
                if (lista.Length != p.alavancas.Length) { Registro.Info(p.nome + " " + p.bloco.Index + ": " + lista.Length + " game lever(s) found"); p.voltaMeioPosto = false; }
                p.alavancas = lista;
                var ps = Ecs.Portas(em, p.bloco); Array.Sort(ps, (a, b) => Ecs.PortaIndice(em, a).CompareTo(Ecs.PortaIndice(em, b)));
                p.portas = ps;
                if (!p.filhosLogados && lista.Length > 0)
                {
                    p.filhosLogados = true;
                    var sb = new System.Text.StringBuilder(p.nome + " " + p.bloco.Index + ": block children:");
                    foreach (var f in Ecs.Filhos(em, p.bloco)) { var lt = Ecs.LT(em, f); sb.Append("\n    ").Append(f.Index).Append(" at (").Append(Ecs.F3(lt.Position)).Append("): ").Append(Ecs.Componentes(em, f)); }
                    sb.Append("\n  ports: ").Append(ps.Length);
                    for (int i = 0; i < ps.Length; i++) { Ecs.Porta(em, ps[i], out var t, out _, out _); sb.Append(" [").Append(i).Append(": ").Append(ps[i].Index).Append(" t").Append(t).Append(" i").Append(Ecs.PortaIndice(em, ps[i])).Append("]"); }
                    Registro.Info(sb.ToString());
                }
                if (lista.Length < p.portas.Length && !p.semAlavancaAvisado && Ecs.EhPart(em, Ecs.Pai(em, p.bloco))) { p.semAlavancaAvisado = true; Registro.Aviso(p.nome + " " + p.bloco.Index + ": expected " + p.portas.Length + " lever(s), found " + lista.Length + " — the game didn't build the prefab's lever(s)?"); }
                // 0.11.2: alavanca comum (DoubleLever, 0..1) ganha o "volta pro meio" do joystick: o jogo devolve ela pro 0 quando solta
                if (voltaMeioOk && !p.voltaMeioPosto)
                {
                    p.voltaMeioPosto = true; int n = 0;
                    foreach (var a in lista)
                    {
                        try
                        {
                            if (!em.HasComponent(a, ComponentType.ReadOnly(tiDouble)) || em.HasComponent(a, ComponentType.ReadOnly(tiVoltaMeio))) continue;
                            em.AddComponent(a, ComponentType.ReadWrite(tiVoltaMeio)); n++;
                        }
                        catch (Exception ex) { Registro.Aviso(p.nome + " " + p.bloco.Index + ": could not add ReturnToMid to lever " + a.Index + ": " + ex.Message); }
                    }
                    if (n > 0) Registro.Info(p.nome + " " + p.bloco.Index + ": ReturnToMid added to " + n + " lever(s) (they return to 0 on their own)");
                }
            }
            // valor de cada alavanca -> porta
            for (int i = 0; i < p.alavancas.Length && i < 4; i++)
            {
                var a = p.alavancas[i]; if (!Ecs.Vivo(em, a)) continue;
                var v = (Ecs.TemC(em, a, tiData) ? (float*)em.GetComponentDataRawRO(a, tiData) : null); if (v == null) continue;
                float x = v[0];
                if (float.IsNaN(x) || float.IsInfinity(x)) x = 0f;
                if (Math.Abs(x - p.ultimo[i]) > 0.02f || (x == 0f && p.ultimo[i] != 0f))
                {
                    // diagnostico: loga os 4 componentes na primeira vez que a alavanca mexe (o valor do jogo fica em _value.x — confirmado no log 0.11.1)
                    string todos = v[0].ToString("F2") + "," + v[1].ToString("F2") + "," + v[2].ToString("F2") + "," + v[3].ToString("F2");
                    if (p.valoresLogados[i] == null) { p.valoresLogados[i] = todos; Registro.Info(p.nome + " " + p.bloco.Index + " lever " + i + ": _value = (" + todos + ")"); }
                    p.ultimo[i] = x;
                }
                if (!p.combina && i < p.portas.Length) Ecs.EscreverPorta(em, p.portas[i], x);
            }
            // 0.19.2: volante com 2 saidas (uma em cada lateral): as duas levam o valor da unica alavanca
            // (0.19.3: o volante voltou a ter uma saida so)
            // 0.12.1: uma saida so — esquerda (menor x) puxa pra -1, direita pra +1
            if (p.combina && p.portas.Length > 0 && p.alavancas.Length >= 2)
            {
                float v = System.Math.Max(-1f, System.Math.Min(1f, p.ultimo[0] - p.ultimo[1]));   // 0.12.4 (Andre): estava invertido — esquerdo -1, direito +1
                Ecs.EscreverPorta(em, p.portas[0], v);
            }
        }
    }
}

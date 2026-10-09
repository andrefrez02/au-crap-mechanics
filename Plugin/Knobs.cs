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
        static TypeIndex tiData, tiDica, tiKnobC, tiToggle; static bool ok, falhou;
        static bool Preparar()
        {
            if (ok) return true; if (falhou) return false;
            try
            {
                tiData = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableData>());
                tiDica = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableTargetDrawCustomUIValue>());
                // 0.23.0: o bearing passou a ter DOIS actionables (knob + switch da saida de angulo) — os dois tem ActionableData,
                // entao o knob agora e' achado pelo ActionableKnob e o switch pelo ActionableToggle.
                try { tiKnobC = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableKnob>()); } catch (Exception ex) { Registro.Aviso("knobs: ActionableKnob: " + ex.Message); }
                try { tiToggle = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableToggle>()); } catch (Exception ex) { Registro.Aviso("knobs: ActionableToggle: " + ex.Message); }
                BeaconIcone.PularToggle = tiToggle;
                ok = true; return true;
            }
            catch (Exception ex) { falhou = true; Registro.Aviso("knobs: " + ex.Message); return false; }
        }

        /// <summary>Filho do bloco com ActionableData (o knob). Entity.Null se o bloco nao tem (prefab antigo).</summary>
        public static Entity Achar(EntityManager em, Entity bloco)
        {
            if (!Preparar()) return Entity.Null;
            if (tiKnobC.Value != 0) foreach (var f in Ecs.Filhos(em, bloco)) if (Ecs.TemC(em, f, tiKnobC)) return f;
            foreach (var f in Ecs.Filhos(em, bloco)) if (Ecs.TemC(em, f, tiData) && !(tiToggle.Value != 0 && Ecs.TemC(em, f, tiToggle))) return f;
            return BeaconIcone.AcharKnobDe(em, bloco);   // 0.19.3: na garagem o knob nao esta no Child do bloco (so no BelongingSC)
        }
        /// <summary>0.23.0: o switch da saida de angulo (filho do bloco com ActionableToggle). Entity.Null se o bloco nao tem.</summary>
        public static Entity AcharSwitch(EntityManager em, Entity bloco)
        {
            if (!Preparar() || tiToggle.Value == 0) return Entity.Null;
            foreach (var f in Ecs.Filhos(em, bloco)) if (Ecs.TemC(em, f, tiToggle)) return f;
            foreach (var f in BeaconIcone.ActionablesDe(em, bloco)) if (Ecs.TemC(em, f, tiToggle)) return f;
            return Entity.Null;
        }
        /// <summary>0.23.0: true = switch ligado (graus); false = desligado (-1..1). Sem switch (prefab antigo) devolve o padrao.</summary>
        public static unsafe bool SwitchLigado(EntityManager em, Entity sw, bool padrao = true)
        {
            if (!ok || !Ecs.Vivo(em, sw) || !Ecs.TemC(em, sw, tiData)) return padrao;
            var p = (float*)em.GetComponentDataRawRO(sw, tiData); if (p == null) return padrao;
            return !(p[0] < 0.5f);
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
        /// <summary>0.20.0: " (1.50 m)" = pct do curso ate o Piston End; vazio sem ponta.</summary>
        static string Metros(int pct, float cursoM) => cursoM > 0.0005f ? " (" + (cursoM * pct / 100f).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " m)" : "";
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
            v = Persistir(em, j.knob, j.bloco, v);   // 0.19.15: valor guardado por SCGuid (o jogo nao salva o knob de bloco generico no BP)
            if (j.tipo == Tipo.Bearing)
            {
                int g = (int)GrausServo(v); j.grausServo = g;
                if (g != j.knobDica) { j.knobDica = g; Dica(em, j.knob, Texto(j.giro ? "SC_GyroBearing_KnobServo" : "SC_Bearing_KnobServo", j.giro ? "Offset range: {0}°" : "Servo: {0}°").Replace("{0}", g.ToString())); if (LogDetalheKnob) Registro.Info(j.nome + " " + j.bloco.Index + ": servo knob " + g + " degrees"); }
            }
            else
            {
                int pct = (int)Math.Round(FracaoCurso(v) * 100f); j.fracaoCurso = pct / 100f;
                // 0.20.0: com Piston End o curso e' a distancia ate ele: a dica mostra os metros (chave = pct x 1000 + metros em cm, pra reescrever quando mudar)
                float cursoM = j.comPonta ? j.curso : Garagem.ExtDe(j.bloco); if (cursoM <= 0.0005f) cursoM = j.cursoMax;   // 0.22.5 (Andre: "mostrar em % e em metros mesmo sem o piston end")
                int chave = pct * 100000 + (int)Math.Round(cursoM * 100f);
                if (chave != j.knobDica) { j.knobDica = chave; Dica(em, j.knob, Texto("SC_Pistao_KnobCurso", "Stroke: {0}%").Replace("{0}", pct.ToString()) + Metros(pct, cursoM)); if (LogDetalheKnob) Registro.Info(j.nome + " " + j.bloco.Index + ": stroke knob " + pct + " %" + Metros(pct, cursoM)); }
            }
        }
        public static bool LogDetalheKnob = true;

        // ---------------------------------------------------------------- 0.19.15 (Andre: "os knobs so atualizam o tooltip durante a
        // simulacao" / "o valor do knob nao ta guardando no bp"). (1) Na garagem os blocos nao viram Junta (nao estao numa part), entao
        // o knob nunca era lido: PassoGaragem varre todos os actionables com BelongingSC e trata os que pertencem a bloco nosso com knob.
        // (2) O jogo so salva no blueprint o BlueprintData do EPC generico (guid, pose, cor): o knob volta pro padrao a cada
        // garagem/lancamento. Guardamos o valor por SCGuid em BepInEx/config/Mecanica/knobs.txt e escrevemos de volta no
        // ActionableData quando o knob (re)aparece — vale na garagem, no lancamento e no time rewind.
        static readonly System.Collections.Generic.Dictionary<string, float> salvos = new System.Collections.Generic.Dictionary<string, float>();
        static readonly System.Collections.Generic.HashSet<long> aplicados = new System.Collections.Generic.HashSet<long>();   // knob entity ja conferido
        static readonly System.Collections.Generic.Dictionary<long, int> dicaGaragem = new System.Collections.Generic.Dictionary<long, int>();
        static bool carregado, sujo; static float tGravar; static string arquivo;
        static string Arquivo()
        {
            if (arquivo == null) { var pasta = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "Mecanica"); System.IO.Directory.CreateDirectory(pasta); arquivo = System.IO.Path.Combine(pasta, "knobs.txt"); }
            return arquivo;
        }
        static void Carregar()
        {
            if (carregado) return; carregado = true;
            try
            {
                if (!System.IO.File.Exists(Arquivo())) return;
                foreach (var l in System.IO.File.ReadAllLines(Arquivo()))
                {
                    var p = l.Trim().Split(' '); if (p.Length != 2) continue;
                    if (float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) salvos[p[0]] = v;
                }
                Registro.Info("knobs: " + salvos.Count + " saved knob value(s) loaded from " + Arquivo());
            }
            catch (Exception ex) { Registro.Aviso("knobs: could not read " + Arquivo() + ": " + ex.Message); }
        }
        /// <summary>Escreve o arquivo no maximo 1x por 2 s quando algo mudou.</summary>
        public static void Gravar(float dt)
        {
            if (!sujo) return; tGravar += dt; if (tGravar < 2f) return; tGravar = 0f; sujo = false;
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var kv in salvos) sb.Append(kv.Key).Append(' ').Append(kv.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                System.IO.File.WriteAllText(Arquivo(), sb.ToString());
            }
            catch (Exception ex) { Registro.Aviso("knobs: could not write " + Arquivo() + ": " + ex.Message); }
        }
        /// <summary>Na 1a vez que um knob (entity) e' visto: se ha valor guardado pro SCGuid do bloco, escreve no ActionableData e
        /// devolve esse valor; depois disso, toda mudanca do jogador vai pro arquivo. Devolve o valor que vale.</summary>
        public static unsafe float Persistir(EntityManager em, Entity knob, Entity bloco, float v, string sufixo = null)
        {
            Carregar();
            string guid; try { guid = Ecs.Guid(em, bloco); } catch (Exception) { guid = ""; }
            if (string.IsNullOrEmpty(guid)) return v;
            if (sufixo != null) guid += "|" + sufixo;   // 0.23.1: o switch da saida de angulo tem chave propria
            long k = Ecs.K(knob);
            if (!aplicados.Contains(k))
            {
                aplicados.Add(k);
                if (salvos.TryGetValue(guid, out var sv) && Math.Abs(sv - v) > 0.0005f)
                {
                    var p = (float*)em.GetComponentDataRawRW(knob, tiData);
                    if (p != null) { p[0] = sv; if (LogDetalheKnob) Registro.Info("knobs: block " + bloco.Index + " (" + guid.Substring(0, 8) + "…): knob restored to " + sv.ToString("F3") + " (was " + v.ToString("F3") + ")"); return sv; }
                }
                if (!salvos.ContainsKey(guid)) { salvos[guid] = v; sujo = true; }
                return v;
            }
            if (!salvos.TryGetValue(guid, out var atual) || Math.Abs(atual - v) > 0.0005f) { salvos[guid] = v; sujo = true; }
            return v;
        }
        /// <summary>A cada 0,5 s: knobs dos blocos nossos que NAO estao numa Junta (garagem): tooltip + persistencia.</summary>
        // 0.20.1: a varredura de TODOS os actionables (BlocoDoActionable + SCPrefab de cada um) so a cada 1 s; a cada 0,1 s le so os knobs
        // que ja se sabe que sao de bloco nosso (o passo de 0,1 s numa nave grande varria centenas de botoes do jogo)
        static readonly System.Collections.Generic.Dictionary<long, (Entity bloco, bool bearing, bool giro, float curso)> knobsMod = new System.Collections.Generic.Dictionary<long, (Entity, bool, bool, float)>();
        static readonly System.Collections.Generic.Dictionary<long, Entity> switchesMod = new System.Collections.Generic.Dictionary<long, Entity>();   // 0.23.1: switch da saida de angulo
        static float tMapa = 9f, tUltimo = -1f;
        public static unsafe void PassoGaragem(EntityManager em)
        {
            if (!Preparar()) return;
            float agora = UnityEngine.Time.unscaledTime; tMapa += tUltimo < 0f ? 9f : agora - tUltimo; tUltimo = agora;
            if (tMapa >= 1f)
            {
                tMapa = 0f; knobsMod.Clear(); switchesMod.Clear();
                Entity[] knobs; try { knobs = BeaconIcone.TodosActionables(em); } catch (Exception) { return; }
                foreach (var kn in knobs)
                {
                    var bloco = BeaconIcone.BlocoDoActionable(em, kn); if (Ecs.Nulo(bloco) || !Ecs.Vivo(em, bloco)) continue;
                    ulong h; try { h = Ecs.Prefab(em, bloco); } catch (Exception) { continue; }
                    if (!Juntas.BlocoComKnob(h, out _, out var bearing, out var giro, out var cursoP)) continue;
                    // 0.23.1 (Andre: "o switch do bearing ta com texto errado"): o bearing tem DOIS actionables — o switch da saida
                    // de angulo caia aqui e levava a dica e o valor guardado do knob. Agora vai pra lista dele.
                    if (tiToggle.Value != 0 && Ecs.TemC(em, kn, tiToggle)) { switchesMod[Ecs.K(kn)] = bloco; continue; }
                    knobsMod[Ecs.K(kn)] = (bloco, bearing, giro, cursoP);
                }
            }
            foreach (var kv in knobsMod)
            {
                var kn = Ecs.DeK(kv.Key); var bloco = kv.Value.bloco; bool bearing = kv.Value.bearing, giro = kv.Value.giro; float cursoMaxP = kv.Value.curso;
                if (!Ecs.Vivo(em, kn) || !Ecs.Vivo(em, bloco)) continue;
                if (Juntas.TemJunta(bloco)) continue;   // na simulacao a Junta cuida
                float v = Valor(em, kn); if (float.IsNaN(v)) continue;
                v = Persistir(em, kn, bloco, v);
                long k = Ecs.K(kn); dicaGaragem.TryGetValue(k, out var antes);
                if (bearing)
                {
                    int g = (int)GrausServo(v);
                    if (g != antes) { dicaGaragem[k] = g; Dica(em, kn, Texto(giro ? "SC_GyroBearing_KnobServo" : "SC_Bearing_KnobServo", giro ? "Offset range: {0}°" : "Servo: {0}°").Replace("{0}", g.ToString())); }
                }
                else
                {
                    int pct = (int)Math.Round(FracaoCurso(v) * 100f); float cursoM = Garagem.ExtDe(bloco); if (cursoM <= 0.0005f) cursoM = cursoMaxP;   // 0.22.5: sem Piston End = curso maximo do pistao
                    int chave = pct * 100000 + (int)Math.Round(cursoM * 100f);
                    if (chave != antes) { dicaGaragem[k] = chave; Dica(em, kn, Texto("SC_Pistao_KnobCurso", "Stroke: {0}%").Replace("{0}", pct.ToString()) + Metros(pct, cursoM)); }
                }
            }
            // 0.23.1: switch da saida de angulo — dica propria e valor guardado por bloco (o jogo nao salva actionable de bloco generico)
            foreach (var kv in switchesMod)
            {
                var sw = Ecs.DeK(kv.Key); var bloco = kv.Value;
                if (!Ecs.Vivo(em, sw) || !Ecs.Vivo(em, bloco)) continue;
                float v = Valor(em, sw); if (float.IsNaN(v)) continue;
                v = Persistir(em, sw, bloco, v, "sw");
                bool graus = !(v < 0.5f); long k = Ecs.K(sw); int quer = graus ? 1 : 2;
                if (!dicaGaragem.TryGetValue(k, out var antes) || antes != quer)
                { dicaGaragem[k] = quer; Dica(em, sw, Texto(graus ? "SC_Bearing_SwitchGraus" : "SC_Bearing_SwitchNorm", graus ? "Angle out: degrees" : "Angle out: -1..1")); }
            }
            if (dicaGaragem.Count > 4096) dicaGaragem.Clear();
        }
        public static void Esquecer() { aplicados.Clear(); dicaGaragem.Clear(); knobsMod.Clear(); switchesMod.Clear(); tMapa = 9f; }
        static string Texto(string chave, string padrao)
        {
            try { var loc = Localization._cachedInstance; if (loc != null && loc._map != null && loc._map.ContainsKey(chave)) return loc._map[chave]; } catch (Exception) { }
            return padrao;
        }
    }
}

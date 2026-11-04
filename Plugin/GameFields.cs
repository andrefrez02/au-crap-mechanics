using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CrapMechanics
{
    /// <summary>0.31.3 (crash do G na 1.0.300/301): onde fica cada campo das structs do jogo que o mod le direto da memoria.
    /// Ate a 0.31.2 eram numeros fixos no codigo; a 1.0.300 acrescentou 20 bytes no GarageGrabberSingleton e o +216 que era o
    /// _remainingComponentsMapPtr virou parte de _selectionPoints (floats da caixa de selecao) — ao apertar G o mod seguia esse
    /// "ponteiro" e o jogo fechava. Agora o offset vem do interop que o BepInEx regenera a cada atualizacao do jogo (o
    /// [FieldOffset] de cada campo, pelo NOME): se o jogo mover um campo, o mod acompanha; se renomear ou tirar, a funcao
    /// que depende dele se desliga e avisa no log em vez de ler lixo.</summary>
    internal static class GameFields
    {
        static readonly Dictionary<string, int> cache = new Dictionary<string, int>();

        /// <summary>Offset (bytes) do campo na struct do jogo, ou -1 se nao existe. <paramref name="usedToBe"/> e' o numero que o mod
        /// usava fixo: se mudou, fica registrado no log (uma vez).</summary>
        public static int Of(Type t, string field, int usedToBe)
        {
            string key = t.FullName + "." + field;
            if (cache.TryGetValue(key, out var v)) return v;
            v = -1;
            try
            {
                var fi = t.GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var fo = fi?.GetCustomAttribute<FieldOffsetAttribute>();
                if (fo != null) v = fo.Value;
            }
            catch (Exception) { v = -1; }
            cache[key] = v;
            if (v < 0) ModLog.Warn("game fields: " + t.Name + "." + field + " not found in this version of the game — what depends on it is off");
            else if (v != usedToBe) ModLog.Info("game fields: " + t.Name + "." + field + " is at +" + v + " in this version of the game (it was +" + usedToBe + ")");
            return v;
        }

        /// <summary>Ponteiro plausivel para uma struct nativa (alinhado e dentro do espaco de usuario do Windows x64). Nao garante que
        /// a memoria exista, mas barra o que obviamente nao e' ponteiro (floats, -1, enderecos nao canonicos).</summary>
        public static unsafe bool Plausible(void* p)
        {
            ulong a = (ulong)p;
            return a >= 0x10000UL && a < 0x00007FFFFFFF0000UL && (a & 7UL) == 0;
        }
    }
}

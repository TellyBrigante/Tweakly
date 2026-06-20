using System;
using System.IO;
using System.Media;
using System.Text;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Sons de NOTIFICATION discrets, synthétisés à la volée (aucun fichier, aucune dépendance).
    /// Notes sinus avec enveloppe attaque + extinction complète à zéro (pas de coupure nette).
    /// Succès = montant, avertissement = descendant. Volume bas. Coupable via Settings.
    /// (Le survol/clic a été retiré : difficile à rendre agréable sans assets.)
    /// </summary>
    public static class UiSound
    {
        public static bool Enabled = true;

        private static SoundPlayer? _success, _warn;
        private static bool _init;

        public static void Init()
        {
            if (_init) return;
            _init = true;
            try
            {
                // (fréquence Hz, durée ms, amplitude 0..1)
                _success = ToPlayer(GenTone(new[] { (523.0, 110, 0.11), (784.0, 150, 0.11) }));  // C5 → G5 (montant)
                _warn    = ToPlayer(GenTone(new[] { (523.0, 120, 0.11), (392.0, 180, 0.11) }));  // C5 → G4 (descendant)
            }
            catch { _success = _warn = null; }
        }

        public static void Success() => Play(_success);
        public static void Warn()    => Play(_warn);

        private static void Play(SoundPlayer? p)
        {
            if (!Enabled || p == null) return;
            try { p.Play(); } catch { }
        }

        private const int SR = 44100;

        // Enveloppe : 0 au début, montée douce, extinction COMPLÈTE à 0 à la fin.
        private static double Env(int i, int len, int attack, int release, double decayK)
        {
            double atk   = Math.Min(1.0, (double)i / Math.Max(1, attack));
            double rel   = Math.Min(1.0, (double)(len - 1 - i) / Math.Max(1, release));
            double decay = Math.Exp(-decayK * (double)i / len);
            return atk * rel * decay;
        }

        private static short[] GenTone((double freq, int ms, double amp)[] notes)
        {
            int total = 0;
            foreach (var n in notes) total += SR * n.ms / 1000;
            var buf = new short[total];
            int k = 0;
            foreach (var n in notes)
            {
                int len     = SR * n.ms / 1000;
                int attack  = Math.Min(len / 2, SR * 9 / 1000);
                int release = Math.Max(attack, len * 40 / 100);
                for (int i = 0; i < len; i++)
                {
                    double s = Math.Sin(2 * Math.PI * n.freq * i / SR) * n.amp * Env(i, len, attack, release, 1.8);
                    buf[k++] = (short)(s * 32767);
                }
            }
            return buf;
        }

        private static SoundPlayer ToPlayer(short[] samples)
        {
            int dataLen = samples.Length * 2;
            // Construire le WAV dans un MemoryStream temporaire, le passer à SoundPlayer qui en
            // copie le contenu via Load(), puis tout disposer. Sinon ms + bw restaient en vie à
            // jamais (référencés par le SoundPlayer statique).
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
            {
                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(36 + dataLen);
                bw.Write(Encoding.ASCII.GetBytes("WAVE"));
                bw.Write(Encoding.ASCII.GetBytes("fmt "));
                bw.Write(16);
                bw.Write((short)1);          // PCM
                bw.Write((short)1);          // mono
                bw.Write(SR);
                bw.Write(SR * 2);            // byteRate
                bw.Write((short)2);          // blockAlign
                bw.Write((short)16);         // bits/sample
                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write(dataLen);
                foreach (var s in samples) bw.Write(s);
                bw.Flush();
            }
            ms.Position = 0;
            var sp = new SoundPlayer(ms);
            sp.Load();   // SoundPlayer copie les octets en interne ; on peut disposer ms après.
            return sp;
        }
    }
}

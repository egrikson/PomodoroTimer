// Будильник. Мелодия синтезируется в PCM-поток и играется через SoundPlayer:
// системные звуки Windows тихие и зависят от звуковой схемы, а здесь сигнал
// нормализован почти к полной шкале, так что его слышно и из соседней комнаты.

using System;
using System.IO;
using System.Media;
using System.Text;

namespace Pomodoro
{
    public static class Alarm
    {
        const int Rate = 44100;
        const double Peak = 32000;      // из 32767 — примерно −0,2 дБ полной шкалы

        // Конец фокуса: нисходящее трезвучие «можно отдохнуть»
        static readonly double[] RestHz = { 1318.51, 1108.73, 880.00, 0 };
        static readonly int[] RestMs = { 150, 150, 260, 520 };

        // Конец перерыва: настойчивый будильник «подъём»
        static readonly double[] WorkHz = { 1567.98, 0, 1567.98, 0, 2093.00, 0 };
        static readonly int[] WorkMs = { 95, 65, 95, 65, 150, 430 };

        static SoundPlayer player;
        static byte[] restWav, workWav;

        public static bool Ringing { get { return player != null; } }

        /// Готовый WAV — тем же кодом, что и звучит в приложении.
        public static byte[] Render(bool toWork)
        {
            return toWork ? BuildWav(WorkHz, WorkMs) : BuildWav(RestHz, RestMs);
        }

        public static void Start(bool toWork)
        {
            Stop();
            byte[] wav;
            if (toWork) wav = workWav ?? (workWav = BuildWav(WorkHz, WorkMs));
            else wav = restWav ?? (restWav = BuildWav(RestHz, RestMs));
            try
            {
                var p = new SoundPlayer(new MemoryStream(wav));
                p.Load();
                p.PlayLooping();
                player = p;
            }
            catch { player = null; }
        }

        public static void Stop()
        {
            if (player == null) return;
            try { player.Stop(); player.Dispose(); } catch { }
            player = null;
        }

        static byte[] BuildWav(double[] hz, int[] ms)
        {
            int total = 0;
            for (int i = 0; i < hz.Length; i++) total += ms[i] * Rate / 1000;

            var buf = new double[total];
            int pos = 0;
            for (int i = 0; i < hz.Length; i++)
            {
                int n = ms[i] * Rate / 1000;
                double f = hz[i];
                if (f > 0)
                {
                    const double attack = 0.006 * Rate;
                    const double release = 0.030 * Rate;
                    for (int k = 0; k < n; k++)
                    {
                        double w = 2 * Math.PI * f * k / Rate;
                        // гармоники дают звонкий тембр: чистая синусоида на слух глуше
                        double v = 0.62 * Math.Sin(w) + 0.26 * Math.Sin(2 * w) + 0.12 * Math.Sin(3 * w);
                        // огибающая со скруглёнными краями, иначе на стыках щелчки
                        double env = Math.Min(1.0, Math.Min(k / attack, (n - k) / release));
                        buf[pos + k] = v * env;
                    }
                }
                pos += n;
            }

            // Гармоники почти никогда не складываются в фазе, поэтому без
            // нормализации сигнал теряет несколько децибел громкости.
            double max = 0;
            for (int i = 0; i < total; i++)
            {
                double a = Math.Abs(buf[i]);
                if (a > max) max = a;
            }
            double gain = max > 0 ? Peak / max : 0;

            var pcm = new short[total];
            for (int i = 0; i < total; i++) pcm[i] = (short)(buf[i] * gain);

            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream))
            {
                int dataLen = pcm.Length * 2;
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + dataLen);
                w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16);
                w.Write((short)1);        // PCM без сжатия
                w.Write((short)1);        // моно
                w.Write(Rate);
                w.Write(Rate * 2);        // байт в секунду
                w.Write((short)2);        // выравнивание блока
                w.Write((short)16);       // бит на отсчёт
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(dataLen);
                for (int i = 0; i < pcm.Length; i++) w.Write(pcm[i]);
                w.Flush();
                return stream.ToArray();
            }
        }
    }
}

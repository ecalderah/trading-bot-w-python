using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using cAlgo.Robots;

namespace SessionEngineTests
{
    // =====================================================================
    // Vectores cruzados Python <-> C#
    //
    // El arnes de validacion vive en Python y el bot en C#: son dos
    // implementaciones del mismo modelo y pueden divergir en silencio. Es el
    // riesgo que se identifico al principio del proyecto.
    //
    // Estos vectores lo cierran: shared_cbot/tests/vectores/signals.json lo
    // genera research/ sobre datos reales, y aqui se comprueba que el C#
    // devuelve exactamente lo mismo. Si alguien toca cualquiera de los dos
    // lados, esto falla.
    //
    // Para regenerar los vectores, ver scripts/gen_vectores.py
    // =====================================================================

    internal static class SignalsTests
    {
        public static void Ejecutar(Action<string, bool, string> check)
        {
            Console.WriteLine("[señales: vectores cruzados con Python]");

            string ruta = Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "vectores", "signals.json");
            ruta = Path.GetFullPath(ruta);

            if (!File.Exists(ruta))
            {
                check("vectores encontrados", false, $"no existe {ruta}");
                Console.WriteLine();
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(ruta));
            var raiz = doc.RootElement;

            double[] Leer(string prop) =>
                raiz.GetProperty(prop).EnumerateArray().Select(e => e.GetDouble()).ToArray();

            var high = Leer("high");
            var low = Leer("low");
            var close = Leer("close");
            int periodo = raiz.GetProperty("atr_period").GetInt32();
            int lookback = raiz.GetProperty("lookback").GetInt32();

            var atr = Signals.Atr(high, low, close, periodo);

            foreach (var e in raiz.GetProperty("esperado").EnumerateArray())
            {
                int i = e.GetProperty("i").GetInt32();
                double atrEsperado = e.GetProperty("atr").GetDouble();
                int tsmomEsperado = e.GetProperty("tsmom").GetInt32();

                double atrObtenido = atr[i];
                bool okAtr = Math.Abs(atrObtenido - atrEsperado) < 1e-4;
                check($"ATR[{i}] coincide con Python", okAtr,
                    $"esperado {atrEsperado:F6}, obtenido {atrObtenido:F6}");

                int tsObtenido = Signals.Tsmom(close, i, lookback);
                check($"TSMOM[{i}] coincide con Python", tsObtenido == tsmomEsperado,
                    $"esperado {tsmomEsperado}, obtenido {tsObtenido}");
            }
            Console.WriteLine();
        }

        public static void CasosLimite(Action<string, bool, string> check)
        {
            Console.WriteLine("[señales: casos limite]");

            var c = new double[] { 10, 11, 12, 13, 14, 15 };

            check("sin historia suficiente -> 0",
                Signals.Tsmom(c, 2, 5) == 0, "");
            check("subida -> +1",
                Signals.Tsmom(c, 5, 3) == 1, "");

            var bajada = new double[] { 15, 14, 13, 12, 11, 10 };
            check("bajada -> -1",
                Signals.Tsmom(bajada, 5, 3) == -1, "");

            var plano = new double[] { 10, 11, 12, 10, 10, 10 };
            check("mismo precio -> 0",
                Signals.Tsmom(plano, 3, 3) == 0, "");

            check("indice fuera de rango -> 0",
                Signals.Tsmom(c, 99, 3) == 0, "");
            check("array nulo -> 0",
                Signals.Tsmom(null, 3, 3) == 0, "");

            // precio previo <= 0: los futuros pueden cotizar negativo (el WTI
            // lo hizo el 20/04/2020) y no se puede dividir ni comparar a ciegas
            var conCero = new double[] { 0, 1, 2, 3 };
            check("precio previo 0 -> 0 (no divide)",
                Signals.Tsmom(conCero, 3, 3) == 0, "");

            Console.WriteLine();
        }

        public static void Trailing(Action<string, bool, string> check)
        {
            Console.WriteLine("[trailing: nunca retrocede]");

            // largo: el stop sube con el maximo
            double stop = 90.0;
            double s1 = TrailingExit.NuevoStop(true, 100, 98, 2.0, 3.0, stop);
            check("largo: sube a high - 3xATR", Math.Abs(s1 - 94.0) < 1e-9,
                $"obtenido {s1}");

            double s2 = TrailingExit.NuevoStop(true, 95, 93, 2.0, 3.0, s1);
            check("largo: NO retrocede si el precio baja", Math.Abs(s2 - 94.0) < 1e-9,
                $"obtenido {s2} (deberia seguir en 94)");

            // corto: el stop baja con el minimo
            double sc = 110.0;
            double c1 = TrailingExit.NuevoStop(false, 102, 100, 2.0, 3.0, sc);
            check("corto: baja a low + 3xATR", Math.Abs(c1 - 106.0) < 1e-9,
                $"obtenido {c1}");

            double c2 = TrailingExit.NuevoStop(false, 108, 106, 2.0, 3.0, c1);
            check("corto: NO retrocede si el precio sube", Math.Abs(c2 - 106.0) < 1e-9,
                $"obtenido {c2}");

            check("ATR invalido -> deja el stop como esta",
                Math.Abs(TrailingExit.NuevoStop(true, 100, 98, double.NaN, 3.0, 94.0) - 94.0) < 1e-9, "");
            check("ATR cero -> deja el stop como esta",
                Math.Abs(TrailingExit.NuevoStop(true, 100, 98, 0, 3.0, 94.0) - 94.0) < 1e-9, "");

            Console.WriteLine();
        }
    }
}

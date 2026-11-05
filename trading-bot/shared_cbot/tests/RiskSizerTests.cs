using System;
using cAlgo.Robots;

namespace SessionEngineTests
{
    // =====================================================================
    // RiskSizer: la pieza que justifica usar cTrader.
    //
    // Lo que se fija aqui es el comportamiento cuando el lotaje fraccionado
    // NO alcanza -- el caso que el FIX 23/24 del bot anterior documenta y que
    // paso desapercibido durante 415 operaciones.
    // =====================================================================

    internal static class RiskSizerTests
    {
        // Normalizador de juguete: redondea a la baja al paso indicado y sube
        // al minimo si queda por debajo. Es lo que hace cTrader.
        private static Func<double, double> Normalizador(double paso, double minimo)
            => v =>
            {
                double r = Math.Floor(v / paso) * paso;
                return r < minimo ? minimo : r;
            };

        public static void Ejecutar(Action<string, bool, string> check)
        {
            Console.WriteLine("[sizing: lotaje fraccionado]");

            var sizer = new RiskSizer();

            // Caso normal: capital 10.000, riesgo 0.5% = 50 USD.
            // Stop de 20 en precio, valor del punto 1 USD/unidad.
            // -> 50 / 20 = 2.5 unidades.
            var r = sizer.Calcular(10_000, 0.5, 20, 1.0, 0.01, 1e9,
                                   Normalizador(0.01, 0.01));
            check("volumen = riesgo / (stop x valor punto)",
                Math.Abs(r.Volumen - 2.5) < 1e-9, $"obtenido {r.Volumen}");
            check("estado Ok", r.Estado == SizingResultado.Ok, r.Estado.ToString());
            check("riesgo efectivo = pedido", Math.Abs(r.RiesgoEfectivo - 50) < 1e-9,
                $"{r.RiesgoEfectivo}");
            check("fidelidad 100%", Math.Abs(r.Fidelidad - 1.0) < 1e-9, $"{r.Fidelidad}");

            Console.WriteLine();
            Console.WriteLine("[sizing: cuando el minimo del broker obliga a arriesgar de mas]");

            var s2 = new RiskSizer();
            // Capital 1.000, riesgo 0.5% = 5 USD. Stop 20, valor punto 1.
            // -> pide 0.25 unidades, pero el minimo es 1.0.
            var r2 = s2.Calcular(1_000, 0.5, 20, 1.0, 1.0, 1e9,
                                 Normalizador(1.0, 1.0));
            check("detecta que el minimo obliga a pasarse",
                r2.Estado == SizingResultado.RiesgoPorDebajoDelMinimo,
                r2.Estado.ToString());
            check("volumen sube al minimo del broker",
                Math.Abs(r2.Volumen - 1.0) < 1e-9, $"{r2.Volumen}");
            check("riesgo efectivo (20) es 4x el pedido (5)",
                Math.Abs(r2.RiesgoEfectivo - 20) < 1e-9, $"{r2.RiesgoEfectivo}");
            check("fidelidad 400% -- se arriesga de mas, no de menos",
                Math.Abs(r2.Fidelidad - 4.0) < 1e-9, $"{r2.Fidelidad}");

            // FIX 24: la comprobacion tiene que ir sobre el volumen CRUDO.
            // Si se comparara el normalizado contra volMin, este caso no
            // saltaria nunca porque el normalizador ya lo subio al minimo.
            check("el aviso salta aunque el normalizador ya subiera al minimo",
                s2.AlMinimo == 1, $"AlMinimo={s2.AlMinimo}");

            Console.WriteLine();
            Console.WriteLine("[sizing: el caso que mata cuentas pequeñas]");

            var s3 = new RiskSizer();
            for (int i = 0; i < 10; i++)
                s3.Calcular(1_000, 0.5, 20, 1.0, 1.0, 1e9, Normalizador(1.0, 1.0));
            check("10 de 10 al minimo -> fraccion 100%",
                Math.Abs(s3.FraccionAlMinimo - 1.0) < 1e-9, $"{s3.FraccionAlMinimo}");
            check("el resumen avisa de que el riesgo no gobierna",
                s3.Resumen().Contains("ATENCION"), s3.Resumen());

            Console.WriteLine();
            Console.WriteLine("[sizing: entradas invalidas]");

            var s4 = new RiskSizer();
            check("equity 0 -> invalido",
                s4.Calcular(0, 0.5, 20, 1, 0.01, 1e9, Normalizador(0.01, 0.01)).Estado
                == SizingResultado.DatosInvalidos, "");
            check("stop 0 -> invalido",
                s4.Calcular(10_000, 0.5, 0, 1, 0.01, 1e9, Normalizador(0.01, 0.01)).Estado
                == SizingResultado.DatosInvalidos, "");
            check("stop NaN -> invalido",
                s4.Calcular(10_000, 0.5, double.NaN, 1, 0.01, 1e9, Normalizador(0.01, 0.01)).Estado
                == SizingResultado.DatosInvalidos, "");
            check("valor de punto 0 -> invalido",
                s4.Calcular(10_000, 0.5, 20, 0, 0.01, 1e9, Normalizador(0.01, 0.01)).Estado
                == SizingResultado.DatosInvalidos, "");

            // volumen maximo
            var s5 = new RiskSizer();
            var r5 = s5.Calcular(10_000_000, 5.0, 1, 1.0, 0.01, 100,
                                 Normalizador(0.01, 0.01));
            check("respeta el volumen maximo del broker",
                Math.Abs(r5.Volumen - 100) < 1e-9, $"{r5.Volumen}");

            Console.WriteLine();
        }

        public static void Costes(Action<string, bool, string> check)
        {
            Console.WriteLine("[costes: el vigilante del swap]");

            var c = new CostTracker { PresupuestoCoste = 0.35, DiasMinimosParaJuzgar = 10 };

            check("spread relativo aceptable (5 pb con cap de 10 pb)",
                c.SpreadAceptable(0.05, 100.0), "");
            check("spread relativo excesivo (50 pb con cap de 10 pb)",
                !c.SpreadAceptable(0.50, 100.0), "");
            check("el veto queda contado", c.VetadasPorSpread == 1,
                $"{c.VetadasPorSpread}");

            // Un instrumento caro y otro barato con el MISMO spread relativo
            // deben tratarse igual: es el error del umbral absoluto en pips.
            var c2 = new CostTracker();
            check("mismo spread relativo, mismo veredicto en oro (2000) y EURUSD (1.1)",
                c2.SpreadAceptable(2000 * 0.0005, 2000) == c2.SpreadAceptable(1.1 * 0.0005, 1.1), "");

            for (int i = 0; i < 20; i++) c.RegistrarDiaConPosicion();
            c.RegistrarCierre("XAUUSD", 100, 2, 5);
            c.RegistrarCierre("XAUUSD", -30, 2, 8);
            c.RegistrarCierre("EURUSD", 50, 1, 3);

            check("bruto ganado suma solo las ganadoras",
                Math.Abs(c.BrutoGanado - 150) < 1e-9, $"{c.BrutoGanado}");
            check("swap acumulado", Math.Abs(c.Swaps - 16) < 1e-9, $"{c.Swaps}");
            check("arrastre = coste / bruto ganado",
                Math.Abs(c.ArrastreDeCoste - 21.0 / 150.0) < 1e-9, $"{c.ArrastreDeCoste}");
            check("dentro de presupuesto (14% < 35%)", !c.FueraDePresupuesto, "");

            var caro = new CostTracker { PresupuestoCoste = 0.35, DiasMinimosParaJuzgar = 10 };
            for (int i = 0; i < 20; i++) caro.RegistrarDiaConPosicion();
            caro.RegistrarCierre("XNGUSD", 100, 5, 200);
            check("swap desbocado dispara el aviso", caro.FueraDePresupuesto, "");
            check("el veredicto es CRITICO cuando el coste supera al bruto",
                caro.Veredicto().Contains("CRITICO"), caro.Veredicto());

            var pronto = new CostTracker { DiasMinimosParaJuzgar = 60 };
            pronto.RegistrarCierre("XAUUSD", 100, 1, 90);
            check("no juzga sin dias suficientes", !pronto.FueraDePresupuesto, "");
            check("y lo dice", pronto.Veredicto().Contains("no hay datos suficientes"),
                pronto.Veredicto());

            Console.WriteLine();
        }
    }
}

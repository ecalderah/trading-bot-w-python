using System;
using System.Collections.Generic;

namespace cAlgo.Robots
{
    // =====================================================================
    // CostTracker  -  vigila lo unico que puede matar esta estrategia en CFD
    //
    // El arnes valido la señal sobre FUTUROS, donde el coste de financiacion
    // va embebido en el precio del contrato. En CFD se cobra aparte, cada
    // noche, y este sistema mantiene posiciones SEMANAS O MESES: el swap deja
    // de ser un detalle y pasa a ser el factor decisivo.
    //
    // La estimacion previa (research/capital.py) decia que con un nocional
    // bruto del 570% del capital y un 3% de financiacion, el coste anual
    // superaria al CAGR. Pero esa estimacion usa una TASA SUPUESTA. Las tasas
    // reales dependen del broker, del instrumento y del lado: en indices el
    // swap del corto suele ser positivo, y en varios brokers las materias
    // primas son CFD sobre futuro con rolo en vez de swap diario.
    //
    // Asi que en vez de discutir la tasa, se MIDE. Este componente acumula el
    // swap real que cobra el broker y lo compara con un presupuesto. Si lo
    // supera, dispara alarma. Los numeros reales deciden, no la suposicion.
    //
    // ARREGLADO respecto a la version aparcada (parked/CostGate.cs):
    //   - Nada en pips absolutos. Un umbral en pips depende del instrumento
    //     y es el error que el FIX 27 del bot anterior ya documentaba. Todo
    //     va en moneda de la cuenta o en fraccion del precio.
    //   - El spread se mide como fraccion del precio, no en pips.
    // =====================================================================

    public sealed class CostTracker
    {
        /// <summary>Fraccion del beneficio BRUTO que el coste puede consumir.</summary>
        public double PresupuestoCoste { get; set; } = 0.35;

        /// <summary>Spread maximo admisible, como fraccion del precio (5 pb = 0.0005).</summary>
        public double SpreadMaximoFraccion { get; set; } = 0.0010;

        /// <summary>Dias minimos con posicion antes de fiarse del ratio de swap.</summary>
        public int DiasMinimosParaJuzgar { get; set; } = 60;

        // --- acumuladores ---
        public double BrutoGanado { get; private set; }
        public double BrutoPerdido { get; private set; }
        public double Comisiones { get; private set; }
        public double Swaps { get; private set; }
        public int Operaciones { get; private set; }
        public int VetadasPorSpread { get; private set; }
        public int DiasConPosicion { get; private set; }

        private readonly Dictionary<string, double> _swapPorSimbolo = new();

        public double CosteTotal => Comisiones + Swaps;

        /// <summary>Coste como fraccion del bruto ganado. Es la cifra que decide.</summary>
        public double ArrastreDeCoste => BrutoGanado > 0 ? CosteTotal / BrutoGanado : 0.0;

        public bool FueraDePresupuesto =>
            DiasConPosicion >= DiasMinimosParaJuzgar &&
            BrutoGanado > 0 &&
            ArrastreDeCoste > PresupuestoCoste;

        /// <summary>
        /// Puerta de spread. Relativa al precio, nunca en pips absolutos.
        /// </summary>
        public bool SpreadAceptable(double spreadEnPrecio, double precio)
        {
            if (precio <= 0) return true;
            double frac = spreadEnPrecio / precio;
            if (frac > SpreadMaximoFraccion)
            {
                VetadasPorSpread++;
                return false;
            }
            return true;
        }

        public void RegistrarDiaConPosicion() => DiasConPosicion++;

        /// <summary>
        /// Registrar una operacion cerrada. `swap` y `comisiones` se toman de
        /// Position.Swap y Position.Commissions, que ya vienen en moneda de la
        /// cuenta y con el signo del broker.
        /// </summary>
        public void RegistrarCierre(string simbolo, double netoBruto,
                                    double comisiones, double swap)
        {
            Operaciones++;
            if (netoBruto > 0) BrutoGanado += netoBruto;
            else BrutoPerdido += -netoBruto;

            Comisiones += Math.Abs(comisiones);
            Swaps += Math.Abs(swap);

            if (!_swapPorSimbolo.ContainsKey(simbolo)) _swapPorSimbolo[simbolo] = 0;
            _swapPorSimbolo[simbolo] += Math.Abs(swap);
        }

        /// <summary>
        /// Los tres simbolos que mas swap se han comido. Sirve para decidir si
        /// vale la pena sacar un mercado concreto de la cesta.
        /// </summary>
        public IEnumerable<KeyValuePair<string, double>> PeoresPorSwap(int cuantos = 3)
        {
            var lista = new List<KeyValuePair<string, double>>(_swapPorSimbolo);
            lista.Sort((a, b) => b.Value.CompareTo(a.Value));
            return lista.GetRange(0, Math.Min(cuantos, lista.Count));
        }

        public string Resumen()
        {
            if (Operaciones == 0) return "costes: sin operaciones cerradas";

            string linea =
                $"costes: comisiones {Comisiones:F2} + swap {Swaps:F2} = {CosteTotal:F2} " +
                $"| bruto ganado {BrutoGanado:F2} | arrastre {ArrastreDeCoste:P1} " +
                $"(presupuesto {PresupuestoCoste:P0})";

            if (Swaps > 0 && CosteTotal > 0)
                linea += $" | el swap es el {Swaps / CosteTotal:P0} del coste";

            return linea;
        }

        /// <summary>
        /// El veredicto sobre la viabilidad en CFD. Es la pregunta que el
        /// backtest sobre futuros no podia responder.
        /// </summary>
        public string Veredicto()
        {
            if (DiasConPosicion < DiasMinimosParaJuzgar)
                return $"aun no hay datos suficientes ({DiasConPosicion} dias con posicion, " +
                       $"hacen falta {DiasMinimosParaJuzgar})";

            if (BrutoGanado <= 0)
                return "sin beneficio bruto todavia: no se puede juzgar el arrastre";

            if (ArrastreDeCoste > 1.0)
                return $"CRITICO: el coste ({CosteTotal:F2}) supera al beneficio bruto " +
                       $"({BrutoGanado:F2}). En CFD esta estrategia NO es viable con estos swaps.";

            if (FueraDePresupuesto)
                return $"AVISO: el coste se lleva el {ArrastreDeCoste:P0} del bruto, por encima " +
                       $"del {PresupuestoCoste:P0} presupuestado. Revisar swaps por simbolo.";

            return $"coste dentro de presupuesto: {ArrastreDeCoste:P0} del bruto ganado";
        }
    }
}

using System;

namespace cAlgo.Robots
{
    // =====================================================================
    // RiskSizer  -  dimensionamiento por riesgo con lotaje fraccionado
    //
    // Esta es LA razon por la que cTrader sirve para esto y una cuenta de
    // futuros pequeña no. En futuros el tamaño minimo es un contrato entero:
    // el micro maiz exige ~24.000 USD para que su riesgo quepa en el 0.77%
    // del capital, y con menos capital ese mercado simplemente no se puede
    // operar. En cTrader el volumen se fracciona hasta VolumeInUnitsStep, asi
    // que el tamaño se ajusta al riesgo en vez de al reves.
    //
    // El sizing por ATR YA ES vol targeting: cuando un mercado se vuelve
    // volatil el ATR sube, la distancia del stop sube y el tamaño baja solo.
    // El arnes probo que añadir un GARCH encima no aporta (prueba 9), asi que
    // no hay ninguno.
    //
    // Lo que este componente NO puede arreglar, y hay que vigilar:
    //   - VolumeInUnitsMin sigue existiendo. Si el riesgo pedido queda por
    //     debajo del minimo, la operacion arriesga MAS de lo permitido. Es
    //     exactamente el fallo que el FIX 23/24 del bot anterior documenta:
    //     400 de 415 operaciones al volumen minimo con el contador de "volumen
    //     insuficiente" marcando 0. Aqui se detecta y se reporta.
    //   - El redondeo a la baja recorta el riesgo real. Tambien se mide.
    // =====================================================================

    public enum SizingResultado
    {
        Ok,
        RiesgoPorDebajoDelMinimo,   // el minimo del broker obliga a arriesgar de mas
        VolumenCero,                // ni siquiera llega al minimo tras redondear
        DatosInvalidos
    }

    public struct Sizing
    {
        public double Volumen;          // en unidades, ya normalizado
        public double RiesgoPedido;     // USD que se queria arriesgar
        public double RiesgoEfectivo;   // USD que de verdad se arriesga
        public SizingResultado Estado;
        public string Detalle;

        /// <summary>Fraccion del riesgo pedido que se acaba asumiendo. 1.0 = exacto.</summary>
        public double Fidelidad => RiesgoPedido > 0 ? RiesgoEfectivo / RiesgoPedido : 0;
    }

    public sealed class RiskSizer
    {
        // Acumuladores para el informe final y las alarmas.
        public int Operaciones { get; private set; }
        public int AlMinimo { get; private set; }
        public int Rechazadas { get; private set; }
        private double _sumaFidelidad;

        public double FidelidadMedia => Operaciones > 0 ? _sumaFidelidad / Operaciones : 0;

        /// <summary>
        /// Fraccion de operaciones que salieron al volumen minimo del broker.
        /// Si pasa de ~0.3, el dimensionamiento por riesgo NO esta gobernando
        /// la estrategia y los resultados no escalan con el capital.
        /// </summary>
        public double FraccionAlMinimo => Operaciones > 0 ? (double)AlMinimo / Operaciones : 0;

        /// <param name="equity">Capital de referencia (usar Account.Equity).</param>
        /// <param name="riesgoPct">% del capital a arriesgar, p.ej. 0.5.</param>
        /// <param name="distanciaStop">Distancia del stop en PRECIO, no en pips.</param>
        /// <param name="valorPorUnidadYPrecio">
        ///   Cuanto vale, en moneda de la cuenta, un movimiento de 1 de precio
        ///   por 1 unidad de volumen. En cTrader: PipValue / PipSize.
        /// </param>
        /// <param name="volMin">Symbol.VolumeInUnitsMin</param>
        /// <param name="volMax">Symbol.VolumeInUnitsMax</param>
        /// <param name="normalizar">Symbol.NormalizeVolumeInUnits, inyectado para poder probar.</param>
        public Sizing Calcular(double equity, double riesgoPct, double distanciaStop,
                               double valorPorUnidadYPrecio, double volMin, double volMax,
                               Func<double, double> normalizar)
        {
            var r = new Sizing();

            if (equity <= 0 || riesgoPct <= 0 || distanciaStop <= 0 ||
                valorPorUnidadYPrecio <= 0 || double.IsNaN(distanciaStop))
            {
                r.Estado = SizingResultado.DatosInvalidos;
                r.Detalle = "entrada invalida";
                return r;
            }

            r.RiesgoPedido = equity * riesgoPct / 100.0;

            // riesgo de UNA unidad = distancia del stop * valor del punto
            double riesgoUnidad = distanciaStop * valorPorUnidadYPrecio;
            double crudo = r.RiesgoPedido / riesgoUnidad;

            double vol = normalizar != null ? normalizar(crudo) : crudo;
            if (vol > volMax) vol = volMax;

            if (vol <= 0)
            {
                r.Estado = SizingResultado.VolumenCero;
                r.Detalle = $"volumen 0 tras normalizar (crudo {crudo:F4})";
                Rechazadas++;
                return r;
            }

            r.Volumen = vol;
            r.RiesgoEfectivo = vol * riesgoUnidad;

            // FIX 24 del bot anterior: hay que comprobar el volumen CRUDO, no
            // el normalizado. NormalizeVolumeInUnits sube al minimo por su
            // cuenta, asi que comparar el normalizado contra volMin nunca se
            // cumple y el aviso no salta jamas.
            if (crudo < volMin)
            {
                r.Estado = SizingResultado.RiesgoPorDebajoDelMinimo;
                r.Detalle = $"el minimo del broker ({volMin}) obliga a arriesgar " +
                            $"{r.RiesgoEfectivo:F2} en vez de {r.RiesgoPedido:F2}";
                AlMinimo++;
            }
            else
            {
                r.Estado = SizingResultado.Ok;
                r.Detalle = "ok";
            }

            Operaciones++;
            _sumaFidelidad += r.Fidelidad;
            return r;
        }

        public string Resumen()
        {
            if (Operaciones == 0) return "sizing: sin operaciones";
            string aviso = FraccionAlMinimo > 0.30
                ? "  ATENCION: mas del 30% al volumen minimo. El riesgo NO gobierna la estrategia."
                : "";
            return $"sizing: {Operaciones} operaciones | fidelidad media {FidelidadMedia:P0} " +
                   $"| al minimo {AlMinimo} ({FraccionAlMinimo:P0}) | rechazadas {Rechazadas}{aviso}";
        }
    }
}

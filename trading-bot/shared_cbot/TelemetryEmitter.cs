using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace cAlgo.Robots
{
    // =====================================================================
    // TelemetryEmitter  -  version corregida
    //
    // Vuelca la vida del bot a un .jsonl (una linea = un evento). Requiere
    // AccessRights.FullAccess en el atributo [Robot(...)].
    //
    // TRES BUGS ARREGLADOS respecto a parked/TelemetryEmitter.cs:
    //
    //   1. GRAVE. Sellaba cada evento con DateTime.UtcNow. En backtest eso es
    //      el reloj de pared, no el tiempo simulado: dos años de backtest
    //      quedaban registrados como los 40 segundos que tardo en correr, y
    //      la curva de equity resultante era basura. Ahora el tiempo del
    //      servidor ENTRA POR PARAMETRO en cada evento y es obligatorio.
    //
    //   2. AutoFlush=true con un snapshot por barra = un flush a disco por
    //      barra. Ahora se escribe con buffer y se vacia cada N eventos o al
    //      cerrar; en backtest el buffer es grande.
    //
    //   3. El POST era fire-and-forget sin guarda de modo: con la URL puesta
    //      en un backtest disparaba miles de peticiones con Task no
    //      observadas. Eliminado: el archivo es la unica via. Si algun dia
    //      hace falta tiempo real, lo lee un proceso aparte haciendo tail.
    // =====================================================================

    public sealed class TelemetryEmitter : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly JsonSerializerOptions _json = new() { WriteIndented = false };
        private readonly object _lock = new();
        private readonly int _flushCada;
        private int _desdeUltimoFlush;

        public string RunId { get; }
        public string Ruta { get; }
        public bool Activo { get; }

        /// <param name="dir">Carpeta raiz de runs.</param>
        /// <param name="bot">Nombre del bot; se usa como subcarpeta.</param>
        /// <param name="modo">"backtest" o "live".</param>
        /// <param name="flushCada">Eventos entre volcados. En vivo, 1.</param>
        public TelemetryEmitter(string dir, string bot, string modo,
                                bool activo = true, int flushCada = 200)
        {
            Activo = activo;
            if (!activo) return;

            try
            {
                string safe = Sanear(bot);
                string sello = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                RunId = $"{safe}_{modo}_{sello}";

                string carpeta = Path.Combine(dir, safe);
                Directory.CreateDirectory(carpeta);
                Ruta = Path.Combine(carpeta, $"{sello}-{modo}.jsonl");

                _writer = new StreamWriter(Ruta, append: false, Encoding.UTF8, 1 << 16)
                {
                    AutoFlush = false
                };
                _flushCada = Math.Max(1, flushCada);
            }
            catch (Exception)
            {
                // Nunca tumbar el bot por la telemetria.
                Activo = false;
            }
        }

        // --- eventos. `ahora` es SIEMPRE Server.Time, nunca DateTime.UtcNow ---

        public void Run(DateTime ahora, string bot, string version, string modo,
                        double balanceInicial, string huella,
                        IReadOnlyDictionary<string, object> parametros)
            => Emitir("run", ahora, new Dictionary<string, object>
            {
                ["bot"] = bot,
                ["version"] = version,
                ["modo"] = modo,
                ["balance_inicial"] = balanceInicial,
                ["huella"] = huella,
                ["parametros"] = parametros,
            });

        public void Snapshot(DateTime ahora, double equity, double balance,
                             int posicionesAbiertas, double drawdownPct,
                             double swapAcumulado, double comisionesAcumuladas)
            => Emitir("snapshot", ahora, new Dictionary<string, object>
            {
                ["equity"] = equity,
                ["balance"] = balance,
                ["posiciones"] = posicionesAbiertas,
                ["drawdown_pct"] = drawdownPct,
                ["swap_acum"] = swapAcumulado,
                ["comisiones_acum"] = comisionesAcumuladas,
            });

        public void Trade(DateTime ahora, string simbolo, string direccion,
                          double entrada, double salida, double neto, double r,
                          double comisiones, double swap, int diasAbierta,
                          string motivo)
            => Emitir("trade", ahora, new Dictionary<string, object>
            {
                ["simbolo"] = simbolo,
                ["direccion"] = direccion,
                ["entrada"] = entrada,
                ["salida"] = salida,
                ["neto"] = neto,
                ["r"] = r,
                ["comisiones"] = comisiones,
                ["swap"] = swap,
                ["dias_abierta"] = diasAbierta,
                ["motivo"] = motivo,
            });

        /// <param name="severidad">"info", "aviso", "critico".</param>
        public void Alarma(DateTime ahora, string severidad, string codigo,
                           string mensaje, IReadOnlyDictionary<string, object> contexto = null)
            => Emitir("alarma", ahora, new Dictionary<string, object>
            {
                ["severidad"] = severidad,
                ["codigo"] = codigo,
                ["mensaje"] = mensaje,
                ["contexto"] = contexto ?? new Dictionary<string, object>(),
            });

        public void RunEnd(DateTime ahora, double balanceFinal, double retornoPct,
                           double drawdownMaxPct, int operaciones,
                           string resumenCostes, string resumenSizing)
            => Emitir("run_end", ahora, new Dictionary<string, object>
            {
                ["balance_final"] = balanceFinal,
                ["retorno_pct"] = retornoPct,
                ["drawdown_max_pct"] = drawdownMaxPct,
                ["operaciones"] = operaciones,
                ["costes"] = resumenCostes,
                ["sizing"] = resumenSizing,
            });

        // --- fontaneria ---

        private void Emitir(string tipo, DateTime ahora, Dictionary<string, object> carga)
        {
            if (!Activo || _writer == null) return;

            carga["tipo"] = tipo;
            carga["run_id"] = RunId;
            carga["ts"] = ahora.ToString("O", CultureInfo.InvariantCulture);

            string linea;
            try { linea = JsonSerializer.Serialize(carga, _json); }
            catch (Exception ex)
            {
                linea = "{\"tipo\":\"" + tipo + "\",\"error_serializacion\":\"" +
                        ex.Message.Replace("\"", "'") + "\"}";
            }

            lock (_lock)
            {
                try
                {
                    _writer.WriteLine(linea);
                    if (++_desdeUltimoFlush >= _flushCada)
                    {
                        _writer.Flush();
                        _desdeUltimoFlush = 0;
                    }
                }
                catch { /* la telemetria nunca tumba al bot */ }
            }
        }

        private static string Sanear(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "bot")
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.Length == 0 ? "bot" : sb.ToString();
        }

        public void Dispose()
        {
            lock (_lock)
            {
                try { _writer?.Flush(); _writer?.Dispose(); } catch { }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace cAlgo.Robots
{
    // =====================================================================
    // TelemetryEmitter  -  componente compartido
    //
    // Vuelca la vida del bot a un archivo .jsonl (una linea = un evento) que
    // el dashboard ingiere. No depende de la logica del bot: recibe valores
    // que el bot ya calcula.
    //
    // Eventos:
    //   run       metadata + huella de configuracion (una vez, al arrancar)
    //   snapshot  equity, balance, drawdown, P/L de sesion, rachas (periodico)
    //   trade     operacion cerrada: direccion, pips, R, motivo, costes
    //   alarm     severidad, codigo, mensaje, contexto
    //   run_end   estadisticas finales
    //
    // Requiere AccessRights.FullAccess en el [Robot(...)] para escribir fuera
    // de la carpeta de datos del bot y (opcional) para el POST en vivo.
    //
    // En backtest el archivo es la unica via fiable. En vivo, ademas del
    // archivo, hace POST a un endpoint local para baja latencia; si el POST
    // falla el archivo sigue siendo la fuente de verdad.
    // =====================================================================

    public sealed class TelemetryEmitter : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly string _path;
        private readonly string _postUrl;
        private readonly HttpClient _http;
        private readonly object _lock = new();
        private readonly JsonSerializerOptions _json = new() { WriteIndented = false };

        public string RunId { get; }
        public string FilePath => _path;
        public bool Enabled { get; }

        /// <param name="dir">Carpeta raiz de runs (p.ej. .../trading-bot/data/runs).</param>
        /// <param name="botName">Nombre del bot, se usa como subcarpeta.</param>
        /// <param name="mode">"backtest" o "live".</param>
        /// <param name="postUrl">Endpoint HTTP para vivo; null o vacio lo desactiva.</param>
        public TelemetryEmitter(string dir, string botName, string mode, string postUrl = null, bool enabled = true)
        {
            Enabled = enabled;
            if (!enabled) return;

            string safeBot = Sanitize(botName);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            RunId = $"{safeBot}_{mode}_{stamp}";

            string folder = Path.Combine(dir, safeBot);
            Directory.CreateDirectory(folder);
            _path = Path.Combine(folder, $"{stamp}-{mode}.jsonl");
            _writer = new StreamWriter(_path, append: false, Encoding.UTF8) { AutoFlush = true };

            _postUrl = string.IsNullOrWhiteSpace(postUrl) ? null : postUrl;
            if (_postUrl != null)
                _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
        }

        // --- eventos -----------------------------------------------------------

        public void Run(string bot, string version, string symbol, string timeframe, string mode,
                        double startingBalance, string fingerprint, IReadOnlyDictionary<string, object> parameters)
            => Emit("run", new Dictionary<string, object>
            {
                ["bot"] = bot,
                ["version"] = version,
                ["symbol"] = symbol,
                ["timeframe"] = timeframe,
                ["mode"] = mode,
                ["starting_balance"] = startingBalance,
                ["fingerprint"] = fingerprint,
                ["parameters"] = parameters,
            });

        public void Snapshot(double equity, double balance, int openPositions, double drawdownPct,
                             string activeSession, double sessionPnl, int sessionTrades, int consecutiveLosses,
                             double spreadPips, bool blackout)
            => Emit("snapshot", new Dictionary<string, object>
            {
                ["equity"] = equity,
                ["balance"] = balance,
                ["open_positions"] = openPositions,
                ["drawdown_pct"] = drawdownPct,
                ["active_session"] = activeSession ?? "",
                ["session_pnl"] = sessionPnl,
                ["session_trades"] = sessionTrades,
                ["consecutive_losses"] = consecutiveLosses,
                ["spread_pips"] = spreadPips,
                ["blackout"] = blackout,
            });

        public void Trade(string direction, double entryPrice, double exitPrice, double pips, double netPnl,
                          double rMultiple, string session, string closeReason,
                          double commission, double swap, DateTime entryTimeUtc, DateTime exitTimeUtc)
            => Emit("trade", new Dictionary<string, object>
            {
                ["direction"] = direction,
                ["entry_price"] = entryPrice,
                ["exit_price"] = exitPrice,
                ["pips"] = pips,
                ["net_pnl"] = netPnl,
                ["r_multiple"] = rMultiple,
                ["session"] = session ?? "",
                ["close_reason"] = closeReason ?? "",
                ["commission"] = commission,
                ["swap"] = swap,
                ["entry_time"] = entryTimeUtc.ToString("O"),
                ["exit_time"] = exitTimeUtc.ToString("O"),
            });

        /// <param name="severity">"info", "warning", "critical".</param>
        public void Alarm(string severity, string code, string message, IReadOnlyDictionary<string, object> context = null)
            => Emit("alarm", new Dictionary<string, object>
            {
                ["severity"] = severity,
                ["code"] = code,
                ["message"] = message,
                ["context"] = context ?? new Dictionary<string, object>(),
            });

        public void RunEnd(double finalBalance, double netReturnPct, double maxDrawdownPct,
                           int trades, double profitFactor, double expectancyR, string costSummary,
                           IReadOnlyDictionary<string, object> extra = null)
        {
            var d = new Dictionary<string, object>
            {
                ["final_balance"] = finalBalance,
                ["net_return_pct"] = netReturnPct,
                ["max_drawdown_pct"] = maxDrawdownPct,
                ["trades"] = trades,
                ["profit_factor"] = profitFactor,
                ["expectancy_r"] = expectancyR,
                ["cost_summary"] = costSummary,
            };
            if (extra != null) foreach (var kv in extra) d[kv.Key] = kv.Value;
            Emit("run_end", d);
        }

        // --- fontaneria ------------------------------------------------------

        private void Emit(string type, Dictionary<string, object> payload)
        {
            if (!Enabled) return;

            payload["type"] = type;
            payload["run_id"] = RunId;
            payload["ts"] = DateTime.UtcNow.ToString("O");

            string line;
            try { line = JsonSerializer.Serialize(payload, _json); }
            catch (Exception ex)
            {
                line = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["type"] = type, ["run_id"] = RunId, ["ts"] = DateTime.UtcNow.ToString("O"),
                    ["serialize_error"] = ex.Message,
                });
            }

            lock (_lock)
            {
                try { _writer.WriteLine(line); } catch { /* nunca tumbar el bot por telemetria */ }
            }

            if (_postUrl != null)
                TryPost(line);
        }

        private void TryPost(string jsonLine)
        {
            try
            {
                var content = new StringContent(jsonLine, Encoding.UTF8, "application/json");
                // fire-and-forget: en vivo no bloqueamos el hilo del bot
                _http.PostAsync(_postUrl, content);
            }
            catch { /* el archivo sigue siendo la fuente de verdad */ }
        }

        private static string Sanitize(string s)
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
            _http?.Dispose();
        }
    }
}

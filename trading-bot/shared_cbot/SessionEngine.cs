using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace cAlgo.Robots
{
    // =====================================================================
    // SessionEngine  -  componente compartido
    //
    // Decide, para un instante dado, si el bot puede ABRIR posiciones, si
    // debe CERRAR todo, y por que. No abre ni cierra nada: solo responde.
    //
    // Cubre lo que el motor de sesiones de un scalper necesita y que la
    // "Puerta 5" original no tenia:
    //
    //   - Tres ventanas independientes y conmutables (Asia / Europa / NY),
    //     cada una en su zona horaria. El horario de verano se aplica solo
    //     porque se evalua con TimeZoneInfo, no con offsets fijos.
    //   - Solape Londres-NY: se toma la UNION de las ventanas activas, sin
    //     logica doble.
    //   - Calendario de festivos y medias sesiones (archivo JSON).
    //   - Bloqueo por noticias de alto impacto (archivo JSON). El cBot no
    //     puede descargar un calendario, asi que lo lee de disco; el sidecar
    //     puede reescribir ese archivo en caliente.
    //   - Colas de sesion: sin entradas los ultimos N minutos, cerrar todo
    //     M minutos antes del cierre.
    //   - Viernes: cierre anticipado configurable.
    //
    // Los filtros de senal del bot SIGUEN actualizandose fuera de sesion.
    // Esto solo gobierna la APERTURA y el CIERRE forzado.
    // =====================================================================

    public sealed class SessionWindow
    {
        public string Name = "sesion";
        public bool Enabled;
        public string TimeZoneId = "UTC";
        public string Open = "08:00";   // HH:mm en la zona de la sesion
        public string Close = "12:00";
        public int NoNewTradesLastMinutes = 20;
        public int FlattenBeforeCloseMinutes = 5;
        public int MaxTrades = 5;
        public double MaxLossPct = 1.5;
        public double MaxSpreadPips = 25;

        internal TimeZoneInfo Tz;
        internal TimeSpan OpenTs;
        internal TimeSpan CloseTs;
        internal bool Valid;
    }

    public enum SessionAction
    {
        Idle,        // fuera de toda sesion activa, sin obligacion de cerrar
        CanTrade,    // alguna ventana activa con margen: se puede abrir
        NoEntry,     // no abrir, pero gestionar normal (cola de sesion, noticia en curso)
        Flatten      // cerrar todo ya (fin de sesion, festivo, antes de noticia)
    }

    public sealed class SessionDecision
    {
        public SessionAction Action;
        public string ActiveSession = "";
        public string Reason = "";
        public double MinutesToFlatten = double.MaxValue;
        public bool BlackoutActive;
        public bool NearFlatten => Action == SessionAction.Flatten;
        public bool CanOpen => Action == SessionAction.CanTrade;
    }

    public sealed class SessionEngine
    {
        private readonly List<SessionWindow> _windows;
        private readonly bool _flattenAtEndOfLastSession;
        private readonly bool _tradeOnFriday;
        private readonly TimeSpan? _fridayCloseUtc;

        private readonly Dictionary<DateTime, HolidayRule> _holidays = new();
        private readonly List<(DateTime From, DateTime To, string Name)> _blackouts = new();

        /// <summary>
        /// Minutos ANTES de una noticia de alto impacto en los que se cierra
        /// todo. Deliberadamente separado de la ventana de bloqueo: cerrar a
        /// mercado en plena noticia es el peor momento posible (spread
        /// disparado). Se sale antes, con spread normal, y durante la noticia
        /// solo se prohibe abrir.
        /// </summary>
        public int NewsFlattenLeadMinutes { get; set; } = 30;

        public SessionEngine(
            IEnumerable<SessionWindow> windows,
            bool flattenAtEndOfLastSession = true,
            bool tradeOnFriday = true,
            string fridayCloseUtc = null)
        {
            _windows = windows.ToList();
            _flattenAtEndOfLastSession = flattenAtEndOfLastSession;
            _tradeOnFriday = tradeOnFriday;

            if (TimeSpan.TryParse(fridayCloseUtc, CultureInfo.InvariantCulture, out var fc))
                _fridayCloseUtc = fc;

            foreach (var w in _windows)
                ResolveWindow(w);
        }

        public IReadOnlyList<SessionWindow> Windows => _windows;

        /// <summary>Errores de configuracion para volcar al log en OnStart.</summary>
        public IEnumerable<string> ConfigWarnings()
        {
            foreach (var w in _windows.Where(x => x.Enabled && !x.Valid))
                yield return $"Sesion '{w.Name}': zona horaria u horas invalidas ('{w.TimeZoneId}' {w.Open}-{w.Close}). Queda DESACTIVADA.";
            if (_windows.All(x => !(x.Enabled && x.Valid)))
                yield return "Ninguna sesion activa y valida: el bot no abrira ninguna posicion.";
        }

        private static void ResolveWindow(SessionWindow w)
        {
            w.Valid = false;
            if (!w.Enabled) return;
            try { w.Tz = TimeZoneInfo.FindSystemTimeZoneById(w.TimeZoneId); }
            catch { return; }
            if (!TimeSpan.TryParse(w.Open, CultureInfo.InvariantCulture, out w.OpenTs)) return;
            if (!TimeSpan.TryParse(w.Close, CultureInfo.InvariantCulture, out w.CloseTs)) return;
            if (w.OpenTs == w.CloseTs) return;
            w.Valid = true;
        }

        // --- carga de calendarios --------------------------------------------------

        private sealed class HolidayRule
        {
            public bool Closed;
            public TimeSpan? EarlyCloseUtc;
            public string Note = "";
        }

        public void LoadHolidays(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("holidays", out var arr)) return;

            foreach (var h in arr.EnumerateArray())
            {
                if (!h.TryGetProperty("date", out var d)) continue;
                if (!DateTime.TryParse(d.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day))
                    continue;

                var rule = new HolidayRule();
                if (h.TryGetProperty("closed", out var c) && c.ValueKind == JsonValueKind.True)
                    rule.Closed = true;
                if (h.TryGetProperty("early_close_utc", out var ec) &&
                    TimeSpan.TryParse(ec.GetString(), CultureInfo.InvariantCulture, out var ecTs))
                    rule.EarlyCloseUtc = ecTs;
                if (h.TryGetProperty("note", out var n))
                    rule.Note = n.GetString() ?? "";

                _holidays[day.Date] = rule;
            }
        }

        /// <summary>
        /// Solo lee ventanas explicitas ("one_off" con from/to). Los eventos
        /// recurrentes del ejemplo necesitan que un feed los expanda a fechas
        /// concretas; el sidecar del dashboard hace ese trabajo y reescribe el
        /// archivo con "one_off" ya materializados.
        /// </summary>
        public void LoadNewsBlackout(string path)
        {
            _blackouts.Clear();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));

            int bufBefore = GetInt(doc.RootElement, "default_buffer_min_before", 5);
            int bufAfter = GetInt(doc.RootElement, "default_buffer_min_after", 15);

            if (!doc.RootElement.TryGetProperty("one_off", out var arr)) return;
            foreach (var e in arr.EnumerateArray())
            {
                if (!e.TryGetProperty("from", out var f) || !e.TryGetProperty("to", out var t)) continue;
                if (!TryUtc(f.GetString(), out var from) || !TryUtc(t.GetString(), out var to)) continue;
                string name = e.TryGetProperty("name", out var nn) ? (nn.GetString() ?? "noticia") : "noticia";
                _blackouts.Add((from.AddMinutes(-bufBefore), to.AddMinutes(bufAfter), name));
            }
        }

        public void ReloadNewsBlackout(string path) => LoadNewsBlackout(path);

        // --- decision ------------------------------------------------------------

        public SessionDecision Evaluate(DateTime utcNow)
        {
            var dec = new SessionDecision();

            // 1. festivo de dia completo
            if (_holidays.TryGetValue(utcNow.Date, out var hr) && hr.Closed)
            {
                dec.Action = SessionAction.Flatten;
                dec.Reason = $"festivo: {hr.Note}";
                return dec;
            }

            // 2. cierre anticipado (festivo o viernes)
            TimeSpan? earlyClose = hr?.EarlyCloseUtc;
            if (utcNow.DayOfWeek == DayOfWeek.Friday)
            {
                if (!_tradeOnFriday)
                {
                    dec.Action = SessionAction.Flatten;
                    dec.Reason = "viernes deshabilitado";
                    return dec;
                }
                if (_fridayCloseUtc.HasValue &&
                    (!earlyClose.HasValue || _fridayCloseUtc.Value < earlyClose.Value))
                    earlyClose = _fridayCloseUtc;
            }
            if (earlyClose.HasValue && utcNow.TimeOfDay >= earlyClose.Value)
            {
                dec.Action = SessionAction.Flatten;
                dec.Reason = $"cierre anticipado {earlyClose.Value:hh\\:mm} UTC";
                return dec;
            }

            // 3. noticias de alto impacto. DOS umbrales distintos a proposito:
            //    - en la antesala (lead) se CIERRA, con el spread todavia normal.
            //    - durante la noticia solo se prohibe ABRIR. Cerrar a mercado
            //      en pleno spike es el peor momento posible.
            foreach (var b in _blackouts)
            {
                var lead = b.From.AddMinutes(-NewsFlattenLeadMinutes);
                if (utcNow >= lead && utcNow < b.From)
                {
                    dec.BlackoutActive = true;
                    dec.Action = SessionAction.Flatten;
                    dec.Reason = $"antesala de '{b.Name}' en {(b.From - utcNow).TotalMinutes:F0} min";
                    return dec;
                }
                if (utcNow >= b.From && utcNow < b.To)
                {
                    dec.BlackoutActive = true;
                    dec.Action = SessionAction.NoEntry;
                    dec.Reason = $"noticia en curso: {b.Name}";
                    return dec;
                }
            }

            // 4. ventanas de sesion, con semantica de UNION.
            //
            //    Se puede abrir si ALGUNA ventana activa lo permite; solo se
            //    cierra si TODAS las abiertas estan ya en zona de cierre. Sin
            //    esto, en el solape Londres-NY el cierre de Londres cerraria
            //    las posiciones aunque NY siguiera plenamente abierta.
            SessionWindow governing = null;   // la que MAS margen deja
            double bestUsable = double.NegativeInfinity;
            int live = 0, inFlattenZone = 0;
            string flattenReason = "";
            string anyLiveName = "";

            foreach (var w in _windows.Where(x => x.Enabled && x.Valid))
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, w.Tz).TimeOfDay;
                if (!InWindow(local, w.OpenTs, w.CloseTs)) continue;

                live++;
                anyLiveName = w.Name;
                double toClose = MinutesToClose(local, w.OpenTs, w.CloseTs);

                if (w.FlattenBeforeCloseMinutes > 0 && toClose <= w.FlattenBeforeCloseMinutes)
                {
                    inFlattenZone++;
                    if (flattenReason.Length == 0)
                        flattenReason = $"cierre de '{w.Name}' en {toClose:F0} min";
                    continue;
                }

                double usable = toClose - Math.Max(0, w.NoNewTradesLastMinutes);
                if (usable > bestUsable) { bestUsable = usable; governing = w; }
            }

            if (live == 0)
            {
                // Fuera de toda sesion. Si se pidio no dejar nada abierto al
                // final del dia, esto es una ORDEN DE CIERRE, no un "no hago
                // nada": de lo contrario una posicion heredada se queda colgada.
                dec.Action = _flattenAtEndOfLastSession ? SessionAction.Flatten : SessionAction.Idle;
                dec.Reason = _flattenAtEndOfLastSession
                    ? "fuera de sesion (cerrar todo al final del dia)"
                    : "fuera de sesion";
                return dec;
            }

            if (inFlattenZone == live)
            {
                dec.Action = SessionAction.Flatten;
                dec.Reason = flattenReason;
                return dec;
            }

            if (governing == null || bestUsable <= 0)
            {
                dec.Action = SessionAction.NoEntry;
                dec.ActiveSession = governing?.Name ?? anyLiveName;
                dec.Reason = $"cola de '{dec.ActiveSession}'";
                return dec;
            }

            dec.ActiveSession = governing.Name;
            dec.MinutesToFlatten = Math.Max(0, bestUsable);
            dec.Action = SessionAction.CanTrade;
            dec.Reason = $"sesion '{governing.Name}'";
            return dec;
        }

        /// <summary>
        /// Minutos transcurridos desde la apertura de la ventana. Lo usa el
        /// originador ORB para saber cuando el rango de apertura esta cerrado.
        /// </summary>
        public double MinutesSinceOpen(DateTime utcNow, SessionWindow w)
        {
            if (w == null || !w.Valid) return double.MaxValue;
            var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, w.Tz).TimeOfDay;
            if (w.OpenTs <= w.CloseTs)
                return (local - w.OpenTs).TotalMinutes;
            return local >= w.OpenTs
                ? (local - w.OpenTs).TotalMinutes
                : (TimeSpan.FromHours(24) - w.OpenTs + local).TotalMinutes;
        }

        /// <summary>Fecha local de la sesion: identifica "el dia" de esa ventana.</summary>
        public DateTime SessionDate(DateTime utcNow, SessionWindow w)
            => w == null || !w.Valid ? utcNow.Date : TimeZoneInfo.ConvertTimeFromUtc(utcNow, w.Tz).Date;

        /// <summary>Ventana activa en este instante, o null. Para el gate de spread por sesion.</summary>
        public SessionWindow ActiveWindow(DateTime utcNow)
        {
            foreach (var w in _windows.Where(x => x.Enabled && x.Valid))
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, w.Tz).TimeOfDay;
                if (InWindow(local, w.OpenTs, w.CloseTs)) return w;
            }
            return null;
        }

        // --- helpers de ventana (contemplan cruce de medianoche) ----------------

        private static bool InWindow(TimeSpan t, TimeSpan open, TimeSpan close)
            => open <= close ? t >= open && t < close : t >= open || t < close;

        private static double MinutesToClose(TimeSpan t, TimeSpan open, TimeSpan close)
        {
            if (open <= close) return (close - t).TotalMinutes;
            return t >= open ? (TimeSpan.FromHours(24) - t + close).TotalMinutes : (close - t).TotalMinutes;
        }

        private static int GetInt(JsonElement e, string prop, int dflt)
            => e.TryGetProperty(prop, out var v) && v.TryGetInt32(out var i) ? i : dflt;

        private static bool TryUtc(string s, out DateTime utc)
            => DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);
    }
}

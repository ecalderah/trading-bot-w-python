using System;
using System.Collections.Generic;
using System.IO;
using cAlgo.Robots;

namespace SessionEngineTests
{
    // =====================================================================
    // Vectores de prueba del SessionEngine.
    //
    // La logica de cruce de medianoche, horario de verano, solape de sesiones
    // y festivos es justo la que falla en silencio. Estos casos fijan el
    // comportamiento esperado; si algun dia se toca el motor, tienen que
    // seguir pasando.
    //
    //   dotnet run --project shared_cbot/tests
    // =====================================================================

    internal static class Program
    {
        private static int _pass, _fail;

        internal static void Check(string name, bool ok, string detail = "")
        {
            if (ok) { _pass++; Console.WriteLine($"  OK    {name}"); }
            else { _fail++; Console.WriteLine($"  FALLA {name}   {detail}"); }
        }

        private static void Eq(string name, SessionAction got, SessionAction want, string reason = "")
            => Check(name, got == want, $"esperado {want}, obtenido {got}  ({reason})");

        private static SessionWindow W(string name, string tz, string open, string close,
                                       int noNew = 20, int flatten = 5, bool enabled = true)
            => new SessionWindow
            {
                Name = name, TimeZoneId = tz, Open = open, Close = close,
                NoNewTradesLastMinutes = noNew, FlattenBeforeCloseMinutes = flatten,
                Enabled = enabled,
            };

        private static DateTime Utc(int y, int m, int d, int hh, int mm)
            => new DateTime(y, m, d, hh, mm, 0, DateTimeKind.Utc);

        private static void Main()
        {
            Console.WriteLine("== SessionEngine: vectores de prueba ==\n");

            SolapeLondresNy();
            CruceDeMedianoche();
            HorarioDeVerano();
            FueraDeSesion();
            Festivos();
            ViernesCierreAnticipado();
            Noticias();
            ConfiguracionInvalida();

            SignalsTests.Ejecutar(Check);
            SignalsTests.CasosLimite(Check);
            SignalsTests.Trailing(Check);
            RiskSizerTests.Ejecutar(Check);
            RiskSizerTests.Costes(Check);

            Console.WriteLine($"\n{_pass} OK, {_fail} fallos");
            Environment.Exit(_fail == 0 ? 0 : 1);
        }

        // -----------------------------------------------------------------
        // El bug que motivo el rediseño: el cierre de Londres NO puede cerrar
        // posiciones mientras NY sigue abierta.
        // -----------------------------------------------------------------
        private static void SolapeLondresNy()
        {
            Console.WriteLine("[solape Londres-NY]");
            var e = new SessionEngine(new[]
            {
                W("europa", "GMT Standard Time",     "08:00", "16:30"),
                W("ny",     "Eastern Standard Time", "08:00", "17:00"),
            });

            // 15 de enero (invierno): Londres 08:00-16:30 UTC, NY 13:00-22:00 UTC.
            // A las 16:27 UTC Londres esta a 3 min de cerrar; NY tiene 5h33.
            var d = e.Evaluate(Utc(2026, 1, 15, 16, 27));
            Eq("16:27 UTC -> se puede operar (manda NY)", d.Action, SessionAction.CanTrade, d.Reason);
            Check("   la sesion que gobierna es NY", d.ActiveSession == "ny", $"fue '{d.ActiveSession}'");

            // Solo Londres activa: ahi si debe cerrar.
            var solo = new SessionEngine(new[] { W("europa", "GMT Standard Time", "08:00", "16:30") });
            Eq("16:27 UTC con solo Londres -> cerrar", solo.Evaluate(Utc(2026, 1, 15, 16, 27)).Action,
               SessionAction.Flatten);

            // Pleno solape.
            Eq("14:00 UTC -> operar", e.Evaluate(Utc(2026, 1, 15, 14, 0)).Action, SessionAction.CanTrade);
            // Hueco entre el cierre de Londres y... nada: NY sigue.
            Eq("21:00 UTC -> operar (solo NY)", e.Evaluate(Utc(2026, 1, 15, 21, 0)).Action, SessionAction.CanTrade);
            // Cola de NY (ultimos 20 min antes de las 22:00 UTC).
            Eq("21:45 UTC -> cola de NY, sin entradas", e.Evaluate(Utc(2026, 1, 15, 21, 45)).Action,
               SessionAction.NoEntry);
            // Zona de cierre de NY.
            Eq("21:57 UTC -> cerrar", e.Evaluate(Utc(2026, 1, 15, 21, 57)).Action, SessionAction.Flatten);
            Console.WriteLine();
        }

        // -----------------------------------------------------------------
        private static void CruceDeMedianoche()
        {
            Console.WriteLine("[ventana que cruza medianoche: Asia]");
            // Tokio 23:00-06:00 local = ventana que cruza la medianoche.
            var e = new SessionEngine(new[] { W("asia", "Tokyo Standard Time", "23:00", "06:00") },
                                      flattenAtEndOfLastSession: false);
            // JST = UTC+9 todo el año (Japon no tiene horario de verano).
            // 23:30 JST = 14:30 UTC del mismo dia.
            Eq("14:30 UTC (23:30 JST) -> dentro", e.Evaluate(Utc(2026, 3, 10, 14, 30)).Action,
               SessionAction.CanTrade);
            // 02:00 JST = 17:00 UTC del dia anterior.
            Eq("17:00 UTC (02:00 JST) -> dentro", e.Evaluate(Utc(2026, 3, 10, 17, 0)).Action,
               SessionAction.CanTrade);
            // 10:00 JST = 01:00 UTC -> fuera.
            Eq("01:00 UTC (10:00 JST) -> fuera", e.Evaluate(Utc(2026, 3, 10, 1, 0)).Action,
               SessionAction.Idle);
            // 05:45 JST = 20:45 UTC -> cola (faltan 15 min para las 06:00).
            Eq("20:45 UTC (05:45 JST) -> cola", e.Evaluate(Utc(2026, 3, 10, 20, 45)).Action,
               SessionAction.NoEntry);
            Console.WriteLine();
        }

        // -----------------------------------------------------------------
        // Lo que justifica usar TimeZoneInfo y no offsets fijos.
        // -----------------------------------------------------------------
        private static void HorarioDeVerano()
        {
            Console.WriteLine("[horario de verano]");
            var e = new SessionEngine(new[] { W("ny", "Eastern Standard Time", "09:30", "16:00") },
                                      flattenAtEndOfLastSession: false);

            // Invierno: 09:30 ET = 14:30 UTC.
            Eq("15 ene 14:30 UTC -> dentro", e.Evaluate(Utc(2026, 1, 15, 14, 30)).Action, SessionAction.CanTrade);
            Eq("15 ene 13:30 UTC -> fuera",  e.Evaluate(Utc(2026, 1, 15, 13, 30)).Action, SessionAction.Idle);

            // Verano: 09:30 ET = 13:30 UTC. La ventana se mueve SOLA.
            Eq("15 jul 13:30 UTC -> dentro", e.Evaluate(Utc(2026, 7, 15, 13, 30)).Action, SessionAction.CanTrade);
            Eq("15 jul 20:05 UTC -> fuera",  e.Evaluate(Utc(2026, 7, 15, 20, 5)).Action,  SessionAction.Idle);

            // EE.UU. y la UE cambian la hora en fechas distintas: entre el
            // 8 y el 29 de marzo de 2026 el solape esta desalineado una hora.
            var dos = new SessionEngine(new[]
            {
                W("europa", "GMT Standard Time",     "08:00", "16:30"),
                W("ny",     "Eastern Standard Time", "08:00", "17:00"),
            });
            // 16 marzo: NY ya en verano (EDT, UTC-4), Londres aun en GMT.
            // 12:30 UTC = 08:30 EDT -> NY abierta.
            Eq("16 mar 12:30 UTC -> NY ya abierta (desalineo)",
               dos.Evaluate(Utc(2026, 3, 16, 12, 30)).Action, SessionAction.CanTrade);
            Console.WriteLine();
        }

        // -----------------------------------------------------------------
        private static void FueraDeSesion()
        {
            Console.WriteLine("[fuera de sesion / cierre al final del dia]");
            var win = new[] { W("ny", "Eastern Standard Time", "09:30", "16:00") };

            var conCierre = new SessionEngine(win, flattenAtEndOfLastSession: true);
            Eq("con 'cerrar al final del dia' -> Flatten",
               conCierre.Evaluate(Utc(2026, 1, 15, 3, 0)).Action, SessionAction.Flatten);

            var sinCierre = new SessionEngine(win, flattenAtEndOfLastSession: false);
            Eq("sin 'cerrar al final del dia' -> Idle",
               sinCierre.Evaluate(Utc(2026, 1, 15, 3, 0)).Action, SessionAction.Idle);
            Console.WriteLine();
        }

        // -----------------------------------------------------------------
        private static void Festivos()
        {
            Console.WriteLine("[festivos y medias sesiones]");
            string path = Path.Combine(Path.GetTempPath(), "holidays_test.json");
            File.WriteAllText(path, @"{
              ""holidays"": [
                { ""date"": ""2026-12-25"", ""closed"": true, ""note"": ""Navidad"" },
                { ""date"": ""2026-12-24"", ""early_close_utc"": ""18:00"", ""note"": ""Nochebuena"" }
              ]
            }");

            var e = new SessionEngine(new[] { W("ny", "Eastern Standard Time", "08:00", "17:00") },
                                      flattenAtEndOfLastSession: false);
            e.LoadHolidays(path);

            Eq("25 dic (cerrado) -> Flatten", e.Evaluate(Utc(2026, 12, 25, 15, 0)).Action, SessionAction.Flatten);
            Eq("24 dic 15:00 UTC (antes del cierre) -> operar",
               e.Evaluate(Utc(2026, 12, 24, 15, 0)).Action, SessionAction.CanTrade);
            Eq("24 dic 18:30 UTC (tras cierre anticipado) -> Flatten",
               e.Evaluate(Utc(2026, 12, 24, 18, 30)).Action, SessionAction.Flatten);
            File.Delete(path);
            Console.WriteLine();
        }

        // -----------------------------------------------------------------
        private static void ViernesCierreAnticipado()
        {
            Console.WriteLine("[viernes]");
            var win = new[] { W("ny", "Eastern Standard Time", "08:00", "17:00") };

            var e = new SessionEngine(win, flattenAtEndOfLastSession: false,
                                      tradeOnFriday: true, fridayCloseUtc: "20:00");
            // 2026-01-16 es viernes.
            Eq("viernes 19:00 UTC -> operar", e.Evaluate(Utc(2026, 1, 16, 19, 0)).Action, SessionAction.CanTrade);
            Eq("viernes 20:30 UTC -> Flatten", e.Evaluate(Utc(2026, 1, 16, 20, 30)).Action, SessionAction.Flatten);
            // 2026-01-15 es jueves: el cierre de viernes no aplica.
            Eq("jueves 20:30 UTC -> operar",  e.Evaluate(Utc(2026, 1, 15, 20, 30)).Action, SessionAction.CanTrade);

            var sinViernes = new SessionEngine(win, flattenAtEndOfLastSession: false, tradeOnFriday: false);
            Eq("viernes con trade_on_friday=false -> Flatten",
               sinViernes.Evaluate(Utc(2026, 1, 16, 15, 0)).Action, SessionAction.Flatten);
            Console.WriteLine();
        }

        // -----------------------------------------------------------------
        // Dos umbrales: cerrar ANTES, no abrir DURANTE.
        // -----------------------------------------------------------------
        private static void Noticias()
        {
            Console.WriteLine("[noticias de alto impacto]");
            string path = Path.Combine(Path.GetTempPath(), "news_test.json");
            File.WriteAllText(path, @"{
              ""default_buffer_min_before"": 0,
              ""default_buffer_min_after"": 0,
              ""one_off"": [
                { ""name"": ""NFP"", ""from"": ""2026-01-15T15:00:00Z"", ""to"": ""2026-01-15T15:30:00Z"" }
              ]
            }");

            var e = new SessionEngine(new[] { W("ny", "Eastern Standard Time", "08:00", "17:00") },
                                      flattenAtEndOfLastSession: false);
            e.NewsFlattenLeadMinutes = 30;
            e.LoadNewsBlackout(path);

            // NY 08:00-17:00 ET; en invierno = 13:00-22:00 UTC.
            Eq("12:30 UTC -> NY aun no ha abierto",
               e.Evaluate(Utc(2026, 1, 15, 12, 30)).Action, SessionAction.Idle);
            Eq("13:30 UTC (abierta, 1h30 antes) -> operar normal",
               e.Evaluate(Utc(2026, 1, 15, 13, 30)).Action, SessionAction.CanTrade);
            Eq("14:40 UTC (antesala) -> CERRAR antes de la noticia",
               e.Evaluate(Utc(2026, 1, 15, 14, 40)).Action, SessionAction.Flatten);
            var dur = e.Evaluate(Utc(2026, 1, 15, 15, 15));
            Eq("15:15 UTC (noticia en curso) -> NO abrir, pero NO cerrar a mercado",
               dur.Action, SessionAction.NoEntry);
            Check("   marca blackout activo", dur.BlackoutActive);
            Eq("15:45 UTC (pasada) -> operar normal",
               e.Evaluate(Utc(2026, 1, 15, 15, 45)).Action, SessionAction.CanTrade);
            File.Delete(path);
            Console.WriteLine();
        }

        // -----------------------------------------------------------------
        private static void ConfiguracionInvalida()
        {
            Console.WriteLine("[configuracion invalida]");
            var e = new SessionEngine(new[]
            {
                W("mala_tz",   "No Existe Standard Time", "08:00", "12:00"),
                W("mala_hora", "GMT Standard Time",       "no",    "12:00"),
                W("iguales",   "GMT Standard Time",       "08:00", "08:00"),
            });

            int avisos = 0;
            foreach (var _ in e.ConfigWarnings()) avisos++;
            // 3 ventanas invalidas + el aviso de "ninguna sesion activa"
            Check("emite un aviso por cada ventana invalida y uno global", avisos == 4, $"fueron {avisos}");
            Eq("sin ninguna ventana valida -> nunca opera",
               e.Evaluate(Utc(2026, 1, 15, 10, 0)).Action, SessionAction.Flatten);
            Console.WriteLine();
        }
    }
}

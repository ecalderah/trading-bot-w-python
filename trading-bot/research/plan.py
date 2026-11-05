"""
Plan operativo segun el capital disponible, en USD o MXN.

Uso:
    python -m research.plan 50000 MXN
    python -m research.plan 500000 USD
    python -m research.plan                 (muestra la tabla completa)

Devuelve, para ese capital concreto: que vehiculo es viable, que mercados
entran, que Sharpe cabe esperar, con cuanto riesgo por operacion, y que CAGR
y drawdown salen de ahi. O que no es viable, que es la respuesta correcta
por debajo de cierto umbral.

CONFIGURACION BASE (la mejor medida, prueba 12):
    señal ........ TSMOM con lookback FIJO de 252 dias (12 meses)
    salida ....... trailing 3xATR, sin objetivo
    pesos ........ iguales entre mercados
    riesgo ....... medio Kelly

Nota sobre el lookback: 252 es el valor del paper original de Moskowitz, Ooi
& Pedersen, no uno elegido mirando estos datos. Y hay meseta ancha -- 252,
378 y 504 dan Sharpe entre 0.73 y 0.76. Reoptimizarlo cada año lo EMPEORA
(0.591): el arnes lo midio y es coherente con todo lo demas del proyecto.
"""

from __future__ import annotations

import sys
import numpy as np
import pandas as pd

from . import data, strategies
from .backtest import BacktestConfig, run
from .capital import CONTRATOS, RIESGO_OP, tabla_capital
from .diversified import coste_relativo
from .metrics import equity_metrics, sharpe

LOOKBACK = 252
FX_POR_DEFECTO = 16.88          # USD/MXN; se actualiza si hay red


def tipo_cambio() -> float:
    try:
        import warnings
        warnings.filterwarnings("ignore")
        import yfinance as yf
        d = yf.download("MXN=X", period="5d", progress=False,
                        auto_adjust=False, multi_level_index=False)
        v = float(d["Close"].dropna().iloc[-1])
        return v if 5 < v < 60 else FX_POR_DEFECTO
    except Exception:
        return FX_POR_DEFECTO


# ---------------------------------------------------------------------------
# Curva Sharpe vs numero de mercados, FUERA DE MUESTRA
# ---------------------------------------------------------------------------

def series_oos_por_mercado(mercados: dict, first_year: int = 2010
                           ) -> dict[str, pd.Series]:
    """Retornos fuera de muestra de cada mercado por separado.

    Como el lookback es fijo no hay nada que optimizar; lo unico que hace el
    corte por años es limitar cada tramo a la ventana de contexto, igual que
    en las pruebas anteriores, para que sea comparable.
    """
    out: dict[str, pd.Series] = {}
    for nombre, df in mercados.items():
        piezas = []
        for y in sorted({d.year for d in df.index}):
            if y < first_year:
                continue
            ctx = df[df.index.year <= y].tail(900)
            if len(ctx) < 500:
                continue
            sig = strategies.tsmom(ctx, LOOKBACK)
            cfg = BacktestConfig(cost_usd_per_oz=coste_relativo(ctx), risk_pct=0.5,
                                 atr_stop_mult=2.0, exit_mode="trail",
                                 trail_atr_mult=3.0, allow_fractional=True,
                                 min_units=1e-9, cooldown_bars=2)
            r = run(ctx, sig, cfg)
            sel = r.equity.pct_change().fillna(0.0)
            sel = sel[sel.index.year == y]
            if len(sel) >= 20:
                piezas.append(sel)
        if piezas:
            out[nombre] = pd.concat(piezas)
    return out


def curva_por_n(series: dict[str, pd.Series], orden: list[str]) -> dict[int, dict]:
    """Sharpe, CAGR y drawdown REALES de cada subconjunto, a medio Kelly.

    El CAGR y el drawdown se obtienen apalancando la serie de verdad y
    componiendola, no con una formula. La aproximacion SR^2/2*0.75 sirve para
    razonar sobre el techo, pero da drawdowns inventados: hay que medirlos.
    """
    activos = [m for m in orden if m in series]
    df = pd.DataFrame({m: series[m] for m in activos}).fillna(0.0)
    out = {}
    for n in range(1, len(activos) + 1):
        port = df[activos[:n]].mean(axis=1).to_numpy()
        sh = sharpe(port)
        sd = port.std(ddof=1) * np.sqrt(252)
        mu = port.mean() * 252
        f_kelly = mu / sd ** 2 if sd > 0 else 0.0
        f = max(0.0, f_kelly / 2.0)                 # medio Kelly

        rl = port * f
        if f <= 0 or np.any(rl <= -1.0):
            cagr, dd, vol = 0.0, 1.0, 0.0
        else:
            eq = np.cumprod(1 + rl)
            años = len(rl) / 252
            cagr = float(eq[-1] ** (1 / años) - 1)
            dd = equity_metrics(eq)["max_dd_pct"] / 100
            vol = float(rl.std(ddof=1) * np.sqrt(252))

        out[n] = {"sharpe": sh, "techo": sh ** 2 / 2, "cagr": cagr,
                  "dd": dd, "vol": vol, "apalanc": f,
                  "riesgo_op": 0.005 * f / max(n, 1),
                  "ultimo": activos[n - 1]}
    return out


# ---------------------------------------------------------------------------
# El plan
# ---------------------------------------------------------------------------

def plan(capital_usd: float, tablas: dict, curvas: dict) -> dict:
    """Elige el mejor vehiculo viable para ese capital."""
    mejor = None
    for veh in ("micro", "completo"):
        t, c = tablas[veh], curvas[veh]
        n = int((t["capital_min"] <= capital_usd).sum())
        if n == 0:
            continue
        info = c.get(n)
        if info is None:
            continue
        margen = float(t.nsmallest(n, "capital_min")["margen"].sum())
        cand = {"vehiculo": veh, "n": n, "margen": margen,
                "mercados": t.nsmallest(n, "capital_min")["mercado"].tolist(),
                **info}
        # se prefiere el que mas Sharpe da, y que el margen quepa
        cand["margen_ok"] = margen <= capital_usd * 0.5
        if mejor is None or cand["sharpe"] > mejor["sharpe"]:
            mejor = cand
    return mejor


def informe(capital: float, moneda: str, fx: float, tablas, curvas) -> None:
    usd = capital / fx if moneda.upper() == "MXN" else capital
    mxn = usd * fx

    print("=" * 78)
    print(f"PLAN PARA {capital:,.0f} {moneda.upper()}")
    print("=" * 78)
    print(f"  = {usd:,.0f} USD  =  {mxn:,.0f} MXN     (tipo {fx:.2f})")
    print()

    p = plan(usd, tablas, curvas)

    if p is None:
        print("  VEREDICTO: NO VIABLE")
        print()
        print(f"  Con {usd:,.0f} USD no entra ni un solo mercado. El contrato mas")
        print(f"  barato -micro maiz (XC)- exige {tablas['micro']['capital_min'].min():,.0f} USD")
        print(f"  para que su riesgo no pase del {RIESGO_OP:.2%} del capital.")
        print()
        print("  Las tres salidas, y por que ninguna sirve:")
        print("   1. Subir el riesgo por operacion. Con un solo mercado el Sharpe")
        print("      cae a ~0.2: el techo baja al 2% anual pase lo que pase.")
        print("   2. CFDs en cTrader. Quitan el minimo de contrato, pero la")
        print("      financiacion nocturna sobre 570% de nocional bruto cuesta mas")
        print("      que el CAGR (prueba 11). El sistema queda NEGATIVO.")
        print("   3. Operar un solo mercado. Es una apuesta direccional, no esta")
        print("      estrategia: lo que la hace funcionar es la diversificacion.")
        print()
        print(f"  Umbral real de entrada: {tablas['micro'].nsmallest(9,'capital_min')['capital_min'].max():,.0f} USD "
              f"({tablas['micro'].nsmallest(9,'capital_min')['capital_min'].max()*fx:,.0f} MXN)")
        print("  para 9 mercados micro, que es donde empieza a tener sentido.")
        return

    veh = "MICRO FUTUROS (CME)" if p["vehiculo"] == "micro" else "FUTUROS COMPLETOS"
    print(f"  VEHICULO ............ {veh}")
    print(f"  MERCADOS ............ {p['n']}")
    print(f"  Riesgo por operacion  {p['riesgo_op']:.2%} del capital = "
          f"{usd*p['riesgo_op']:,.0f} USD ({usd*p['riesgo_op']*fx:,.0f} MXN)")
    print(f"  Volatilidad objetivo  {p['vol']:.0%} anual  "
          f"(medio Kelly = {p['apalanc']:.0f}x sobre la cartera equiponderada)")
    print(f"  Margen necesario .... {p['margen']:,.0f} USD"
          f"{'' if p['margen_ok'] else '   <-- MAS DEL 50% DEL CAPITAL: no cabe'}")
    print()
    print(f"  Sharpe esperado ..... {p['sharpe']:.3f}")
    print(f"  Techo (SR^2/2) ...... {p['techo']:.1%} anual  (a Kelly completo)")
    print(f"  CAGR a medio Kelly .. {p['cagr']:.1%} anual "
          f"= {usd*p['cagr']:,.0f} USD ({usd*p['cagr']*fx:,.0f} MXN)")
    print(f"  Drawdown MEDIDO ..... {p['dd']:.0%}  "
          f"(= {usd*p['dd']:,.0f} USD de caida desde maximos)")
    print()
    print(f"  Mercados: {', '.join(p['mercados'])}")
    print()
    print("  CONFIGURACION")
    print(f"    señal ....... TSMOM lookback {LOOKBACK} dias, FIJO (no reoptimizar)")
    print( "    salida ...... trailing 3xATR, sin objetivo fijo")
    print( "    stop ........ 2xATR en la entrada")
    print( "    pesos ....... iguales entre mercados")
    if p["sharpe"] < 0.4:
        print()
        print("  AVISO: con este Sharpe el techo esta por debajo del 8% anual.")
        print("  Es poco para el trabajo y el drawdown que cuesta.")


def main() -> None:
    fx = tipo_cambio()
    mercados = data.load_basket(start="2004-01-01")
    series = series_oos_por_mercado(mercados)

    tablas, curvas = {}, {}
    for veh, micro in (("completo", False), ("micro", True)):
        t = tabla_capital(mercados, micro=micro)
        tablas[veh] = t
        curvas[veh] = curva_por_n(series, t["mercado"].tolist())

    args = sys.argv[1:]
    if len(args) >= 1:
        try:
            cap = float(args[0].replace(",", "").replace("_", ""))
        except ValueError:
            print("Uso: python -m research.plan <capital> [USD|MXN]")
            return
        mon = args[1] if len(args) > 1 else "USD"
        informe(cap, mon, fx, tablas, curvas)
        return

    # sin argumentos: la tabla completa
    print("=" * 88)
    print("PLAN SEGUN CAPITAL  -  TSMOM(252) fijo + trailing 3xATR, medio Kelly")
    print("=" * 88)
    print(f"Tipo de cambio USD/MXN: {fx:.2f}\n")
    print(f"  {'USD':>10} {'MXN':>14} {'vehiculo':>10} {'mkts':>5} "
          f"{'Sharpe':>8} {'riesgo/op':>10} {'CAGR':>7} {'drawdown':>9}")
    print("  " + "-" * 78)
    for usd in (3_000, 10_000, 25_000, 50_000, 100_000, 250_000,
                500_000, 1_000_000):
        p = plan(usd, tablas, curvas)
        if p is None:
            print(f"  {usd:9,}$ {usd*fx:13,.0f} {'—':>10} {0:5d} "
                  f"{'—':>8} {'—':>10} {'—':>7}  no viable")
            continue
        veh = "micro" if p["vehiculo"] == "micro" else "completo"
        print(f"  {usd:9,}$ {usd*fx:13,.0f} {veh:>10} {p['n']:5d} "
              f"{p['sharpe']:8.3f} {p['riesgo_op']:9.2%} {p['cagr']:6.1%} "
              f"{p['dd']:8.0%}")
    print()
    print("  Detalle de un caso concreto:")
    print("    python -m research.plan 50000 MXN")
    print("    python -m research.plan 500000 USD")


if __name__ == "__main__":
    main()

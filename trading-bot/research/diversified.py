"""
LA ESTRATEGIA NUEVA: ruptura lenta diversificada.

Es lo que señalaban las pruebas anteriores, no una idea nueva:

  - La ruptura tendencial tiene meseta ancha (prueba 5, ataque 4): funciona
    en todo el rango de lookback 20-200, no en un pico.
  - Lo decisivo era la SALIDA, no la entrada (research/exits.py). Con
    trailing en vez de objetivo fijo, la misma señal casi triplica.
  - Pero sobre UN mercado la ventaja es pequeña y las rachas de perdidas son
    largas. Sobre veinte mercados poco correlacionados, las rachas no
    coinciden y la volatilidad de la cartera cae sin que haya que acertar mas.

Eso ultimo es la unica "comida gratis" real que hay aqui, y es la tesis
entera del CTA clasico (Managed Futures). Este modulo la prueba.

DIMENSIONAMIENTO. Cada mercado arriesga el mismo % del capital por operacion
y el tamaño sale de dividir ese riesgo entre la distancia del stop en ATR.
Eso YA es vol targeting: cuando un mercado se vuelve volatil, el ATR sube y
la posicion baja sola. No hace falta un GARCH encima.

LIMITACIONES QUE NO SE OCULTAN:
  - Son futuros continuos de yfinance, sin ajuste de rolo. En mercados con
    contango fuerte (gas natural) eso distorsiona los retornos largos.
  - El coste se aplica en unidades de precio por unidad, calibrado para el
    oro. Para el resto es una aproximacion.
  - No hay margen ni limites de exposicion: es una prueba de la SEÑAL y de
    la diversificacion, no una simulacion de cuenta real.
"""

from __future__ import annotations

import numpy as np
import pandas as pd

from . import data, strategies
from .backtest import BacktestConfig, run
from .costs import BROKER_SCENARIOS
from .exits import bootstrap_p
from .metrics import deflated_sharpe, equity_metrics, sharpe


def coste_relativo(df: pd.DataFrame, fraccion: float = 0.0004) -> float:
    """Coste ida y vuelta como fraccion del precio medio, no absoluto.

    Un umbral absoluto en pips depende del instrumento -es el error que el
    FIX 27 del bot documenta y que ya se cometio una vez en este proyecto-.
    4 puntos basicos del precio equivale a ~$0.40/oz con el oro a $1000, que
    es el escenario 'ecn_tipico', y escala solo al resto de mercados.
    """
    return float(df["close"].mean() * fraccion)


def correr_mercado(df: pd.DataFrame, lookback: int, trail: float,
                   risk_pct: float, atr_stop: float) -> tuple[pd.Series, list]:
    sig = strategies.donchian(df, lookback)
    cfg = BacktestConfig(
        cost_usd_per_oz=coste_relativo(df),
        risk_pct=risk_pct, atr_stop_mult=atr_stop,
        exit_mode="trail", trail_atr_mult=trail,
        allow_fractional=True, min_units=1e-9, cooldown_bars=2,
    )
    res = run(df, sig, cfg)
    return res.equity, res.trades


def cartera(mercados: dict[str, pd.DataFrame], lookback: int = 120,
            trail: float = 3.0, risk_pct: float = 0.5,
            atr_stop: float = 2.0) -> dict:
    """Cada mercado con su propio presupuesto de riesgo; se suman los P&L.

    Se suman RETORNOS, no balances: asi cada mercado aporta segun su riesgo
    y no segun el tamaño nominal de su contrato, que no es comparable entre
    el gas natural y el bono a 10 años.
    """
    curvas: dict[str, pd.Series] = {}
    todos_trades: dict[str, list] = {}

    for nombre, df in mercados.items():
        eq, tr = correr_mercado(df, lookback, trail, risk_pct, atr_stop)
        if len(tr) < 5:
            continue
        curvas[nombre] = eq.pct_change().fillna(0.0)
        todos_trades[nombre] = tr

    if not curvas:
        return {}

    rets = pd.DataFrame(curvas).fillna(0.0)
    port = rets.mean(axis=1)                    # equiponderado por riesgo
    equity = (1 + port).cumprod()

    return {"rets_por_mercado": rets, "port_rets": port, "equity": equity,
            "trades": todos_trades, "n_mercados": len(curvas)}


def resumen(nombre: str, port: dict, n_trials: int = 1) -> dict:
    eq = port["equity"].to_numpy()
    m = equity_metrics(eq)
    r = port["port_rets"].to_numpy()
    d = deflated_sharpe(r, n_trials=n_trials)
    todas_r = [t.r for lst in port["trades"].values() for t in lst]
    return {
        "nombre": nombre,
        "mercados": port["n_mercados"],
        "trades": len(todas_r),
        "ret": m["net_return_pct"],
        "maxDD": m["max_dd_pct"],
        "ret_dd": m["ret_over_dd"],
        "sharpe": sharpe(r),
        "espR": float(np.mean(todas_r)) if todas_r else 0.0,
        "p_boot": bootstrap_p(todas_r) if todas_r else 1.0,
        "dsr": d["dsr"],
    }


def fila(x: dict) -> str:
    mark = " *" if x["p_boot"] < 0.05 else "  "
    return (f"  {x['nombre']:26} {x['mercados']:4d} {x['trades']:7d} "
            f"{x['ret']:+9.1f}% {x['maxDD']:7.1f}% {x['ret_dd']:7.2f} "
            f"{x['sharpe']:7.2f} {x['espR']:+7.3f} {x['p_boot']:8.4f}{mark}{x['dsr']:6.2f}")


CAB = (f"  {'cartera':26} {'mkts':>4} {'trades':>7} {'retorno':>10} "
       f"{'maxDD':>8} {'ret/DD':>7} {'Sharpe':>7} {'espR':>7} {'p_boot':>8}  {'DSR':>5}")


def main() -> None:
    print("=" * 108)
    print("RUPTURA LENTA DIVERSIFICADA  -  21 futuros, diario 2004-2026")
    print("=" * 108)
    print("Donchian(120) + trailing 3xATR. Riesgo 0.5% por operacion y mercado.")
    print("El tamaño sale del ATR: eso ya es vol targeting, sin GARCH encima.")
    print("Coste = 4 pb del precio de cada mercado (relativo, no absoluto).\n")

    mercados = data.load_basket(start="2004-01-01")
    print(f"Mercados cargados: {len(mercados)}")
    print("  " + ", ".join(sorted(mercados)) + "\n")

    # --- 1. un mercado contra la cartera ---
    print("--- 1. Un mercado contra la cartera completa ---")
    print(CAB)
    print("  " + "-" * 104)

    solo_oro = cartera({"oro": mercados["oro"]})
    print(fila(resumen("solo oro", solo_oro)))

    solo_metales = cartera({k: v for k, v in mercados.items()
                            if data.SECTORES.get(k) == "metales"})
    print(fila(resumen("solo metales (4)", solo_metales)))

    port = cartera(mercados)
    print(fila(resumen("cartera completa", port, n_trials=20)))

    # --- 2. por sector ---
    print("\n--- 2. Por sector ---")
    print(CAB)
    print("  " + "-" * 104)
    for sec in ("metales", "energia", "agricola", "indices", "tipos", "divisas"):
        sub = {k: v for k, v in mercados.items() if data.SECTORES.get(k) == sec}
        if len(sub) < 2:
            continue
        print(fila(resumen(sec, cartera(sub))))

    # --- 3. el efecto de diversificar, aislado ---
    print("\n--- 3. El efecto de diversificar, aislado ---")
    print("  Misma señal y mismo riesgo; solo cambia CUANTOS mercados hay.")
    print(f"\n  {'n mercados':>11} {'Sharpe':>8} {'maxDD':>8} {'ret/DD':>8}")
    print("  " + "-" * 39)
    orden = ["oro", "bono10", "sp500", "crudo", "eurusd", "maiz", "cobre",
             "gas_nat", "nasdaq", "yen", "trigo", "azucar", "plata", "soja",
             "bono30", "dxy", "cafe", "algodon", "platino", "nikkei", "gasolina"]
    orden = [m for m in orden if m in mercados]
    for n in (1, 2, 3, 5, 8, 12, 16, len(orden)):
        sub = {k: mercados[k] for k in orden[:n]}
        p = cartera(sub)
        if not p:
            continue
        s = resumen(f"{n}", p)
        print(f"  {n:11d} {s['sharpe']:8.2f} {s['maxDD']:7.1f}% {s['ret_dd']:8.2f}")

    # --- 4. sensibilidad al lookback ---
    print("\n--- 4. ¿Sigue habiendo meseta en la cartera? ---")
    print(f"\n  {'lookback':>9} {'trades':>7} {'retorno':>10} {'maxDD':>8} "
          f"{'Sharpe':>8} {'espR':>8} {'p_boot':>9}")
    print("  " + "-" * 62)
    for lb in (20, 40, 80, 120, 160, 200):
        p = cartera(mercados, lookback=lb)
        s = resumen(str(lb), p)
        mark = " *" if s["p_boot"] < 0.05 else ""
        print(f"  {lb:9d} {s['trades']:7d} {s['ret']:+9.1f}% {s['maxDD']:7.1f}% "
              f"{s['sharpe']:8.2f} {s['espR']:+8.3f} {s['p_boot']:9.4f}{mark}")

    # --- 5b. escalado a volatilidad objetivo ---
    print("\n--- 5. Escalado a volatilidad objetivo ---")
    print("  Los retornos de arriba salen pequeños porque el riesgo por operacion")
    print("  es 0.5% y ademas se reparte entre 21 mercados: apalancamiento ridiculo.")
    print("  Lo INVARIANTE es el Sharpe; retorno y drawdown escalan juntos. Asi es")
    print("  como se compara de verdad con un CTA, que apunta a 10-15% de vol anual.")
    print(f"\n  {'vol objetivo':>13} {'ret anual':>11} {'maxDD estimado':>16} {'apalancamiento':>15}")
    print("  " + "-" * 58)

    r = port["port_rets"].to_numpy()
    vol_actual = r.std(ddof=1) * np.sqrt(252)
    eq = port["equity"].to_numpy()
    años = len(r) / 252
    cagr = (eq[-1] / eq[0]) ** (1 / años) - 1
    dd_actual = equity_metrics(eq)["max_dd_pct"] / 100

    for objetivo in (0.05, 0.10, 0.15, 0.20):
        k = objetivo / vol_actual if vol_actual > 0 else 0
        # el drawdown escala aproximadamente lineal con el apalancamiento
        print(f"  {objetivo:12.0%} {cagr * k:10.1%} {dd_actual * k:15.1%} "
              f"{k:14.1f}x")
    print(f"\n  Volatilidad actual de la cartera: {vol_actual:.2%} anual")
    print(f"  Un CTA real opera a 10-15%. A esa escala esta cartera daria")
    print(f"  ~{cagr * (0.12 / vol_actual):.1%} anual con ~{dd_actual * (0.12 / vol_actual):.0%} de drawdown maximo.")
    print("  OJO: escalar tambien multiplica el riesgo de ruina y asume que se")
    print("  puede mantener el apalancamiento en el peor momento. No es gratis.")

    # --- 6. correlaciones ---
    print("\n--- 5. ¿Por que funciona? Correlacion entre los P&L ---")
    c = port["rets_por_mercado"].corr()
    tri = c.to_numpy()[np.triu_indices(len(c), k=1)]
    print(f"  Correlacion media entre mercados: {np.nanmean(tri):.3f}")
    print(f"  Maxima: {np.nanmax(tri):.3f} | minima: {np.nanmin(tri):.3f}")
    print(f"  Pares con correlacion > 0.3: {(tri > 0.3).sum()} de {len(tri)}")
    print("\n  Es baja porque las tendencias de mercados distintos no coinciden")
    print("  en el tiempo. Ahi esta la reduccion de riesgo, no en acertar mas.")


if __name__ == "__main__":
    main()

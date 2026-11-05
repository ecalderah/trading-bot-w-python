"""
MAXIMA RENTABILIDAD: donde esta el techo y como se llega.

Pedir "maxima rentabilidad" sin restriccion de riesgo no esta bien definido:
subiendo apalancamiento se sube el retorno ESPERADO sin limite. Pero el
retorno COMPUESTO -el que determina cuanto dinero acabas teniendo- no crece
sin limite. Tiene un maximo, y pasado ese punto MAS APALANCAMIENTO DA MENOS
DINERO.

    g(f) = f*mu - (f^2 * sigma^2) / 2

El optimo es f* = mu/sigma^2 (Kelly) y el crecimiento en ese punto vale

    g_max = SR^2 / 2

Eso es un TECHO DURO. Con Sharpe 0.55 el maximo teorico es 15% anual, y
ningun apalancamiento lo supera. La unica forma de subir el techo es subir
el Sharpe.

Por eso este modulo hace dos cosas, en este orden:
  1. Busca la señal con mejor Sharpe FUERA DE MUESTRA (sube el techo).
  2. Calcula el apalancamiento que maximiza el crecimiento (llega al techo).

Y avisa de lo que casi nadie contabiliza: el Sharpe se ESTIMA con error.
Apalancar a Kelly sobre un Sharpe sobreestimado es exactamente como se
revientan las cuentas.
"""

from __future__ import annotations

import numpy as np
import pandas as pd

from . import data, strategies
from .backtest import BacktestConfig, run
from .diversified import coste_relativo
from .exits import bootstrap_p
from .metrics import equity_metrics, sharpe

DIAS = 252


# ---------------------------------------------------------------------------
# 1. Subir el techo: que señal tiene mas Sharpe fuera de muestra
# ---------------------------------------------------------------------------

SEÑALES = {
    "Donchian 120":   lambda d, p: strategies.donchian(d, p),
    "TSMOM":          lambda d, p: strategies.tsmom(d, p),
}
REJILLA = {"Donchian 120": [60, 120, 200], "TSMOM": [126, 189, 252, 378]}


def _cartera(mercados, fn, param, risk=0.5, trail=3.0):
    curvas, rs = {}, []
    for nombre, df in mercados.items():
        sig = fn(df, param)
        cfg = BacktestConfig(cost_usd_per_oz=coste_relativo(df), risk_pct=risk,
                             atr_stop_mult=2.0, exit_mode="trail",
                             trail_atr_mult=trail, allow_fractional=True,
                             min_units=1e-9, cooldown_bars=2)
        r = run(df, sig, cfg)
        if len(r.trades) < 5:
            continue
        curvas[nombre] = r.equity.pct_change().fillna(0.0)
        rs.extend(t.r for t in r.trades)
    if not curvas:
        return None, None
    port = pd.DataFrame(curvas).fillna(0.0).mean(axis=1)
    return port, rs


def walk_forward_señal(mercados, nombre, first_year=2010):
    """Elige el parametro con datos pasados; opera el año siguiente."""
    fn = SEÑALES[nombre]
    rejilla = REJILLA[nombre]
    años = sorted({d.year for df in mercados.values() for d in df.index})
    piezas, todos_r = [], []

    for y in años:
        if y < first_year:
            continue
        train = {k: v[v.index.year < y] for k, v in mercados.items()}
        train = {k: v for k, v in train.items() if len(v) > 900}
        if len(train) < 5:
            continue

        mejor, mejor_sh = None, -np.inf
        for p in rejilla:
            port, _ = _cartera(train, fn, p)
            if port is None or port.std() == 0:
                continue
            sh = sharpe(port.to_numpy())
            if sh > mejor_sh:
                mejor_sh, mejor = sh, p
        if mejor is None:
            continue

        ctx = {k: v[v.index.year <= y].tail(900) for k, v in mercados.items()}
        ctx = {k: v for k, v in ctx.items() if len(v) > 500}
        port, rs = _cartera(ctx, fn, mejor)
        if port is None:
            continue
        sel = port[port.index.year == y]
        if len(sel) < 20:
            continue
        piezas.append(sel)
        todos_r.extend(rs)

    if not piezas:
        return None
    return pd.concat(piezas)


# ---------------------------------------------------------------------------
# 2. Llegar al techo: apalancamiento optimo
# ---------------------------------------------------------------------------

def curva_crecimiento(r: np.ndarray, apalancamientos: np.ndarray) -> pd.DataFrame:
    """Crecimiento compuesto real segun el apalancamiento.

    No se usa la formula: se RECALCULA la serie apalancada y se compone. Asi
    la respuesta incluye el arrastre de volatilidad y la ruina de verdad, no
    la aproximacion de segundo orden.
    """
    filas = []
    for f in apalancamientos:
        rl = r * f
        if np.any(rl <= -1.0):          # cuenta a cero: ruina
            filas.append({"f": f, "cagr": -1.0, "maxDD": 1.0, "ruina": True,
                          "vol": float(rl.std(ddof=1) * np.sqrt(DIAS))})
            continue
        eq = np.cumprod(1 + rl)
        años = len(rl) / DIAS
        cagr = eq[-1] ** (1 / años) - 1
        m = equity_metrics(eq)
        filas.append({"f": f, "cagr": float(cagr), "maxDD": m["max_dd_pct"] / 100,
                      "ruina": False, "vol": float(rl.std(ddof=1) * np.sqrt(DIAS))})
    return pd.DataFrame(filas)


def kelly(r: np.ndarray) -> dict:
    mu = r.mean() * DIAS
    sig = r.std(ddof=1) * np.sqrt(DIAS)
    sr = mu / sig if sig > 0 else 0.0
    f_kelly = mu / sig ** 2 if sig > 0 else 0.0
    # error estandar del Sharpe (Lo 2002)
    T = len(r) / DIAS
    se_sr = np.sqrt((1 + sr ** 2 / 2) / T) if T > 0 else np.nan
    return {"mu": mu, "sigma": sig, "sharpe": sr, "f_kelly": f_kelly,
            "g_max": sr ** 2 / 2, "se_sharpe": se_sr, "años": T}


def main() -> None:
    print("=" * 92)
    print("MAXIMA RENTABILIDAD  -  el techo, y como llegar a el")
    print("=" * 92)

    mercados = data.load_basket(start="2004-01-01")

    # ---- 1. subir el techo ----
    print("\n--- 1. SUBIR EL TECHO: que señal tiene mas Sharpe fuera de muestra ---")
    print("El techo de crecimiento compuesto es SR^2/2. Es lo unico que importa")
    print("para maximizar rentabilidad: el apalancamiento te lleva al techo, no")
    print("lo sube.\n")
    print(f"  {'señal':16} {'Sharpe OOS':>11} {'techo SR^2/2':>14} {'maxDD (1x)':>12}")
    print("  " + "-" * 56)

    resultados = {}
    for nombre in SEÑALES:
        port = walk_forward_señal(mercados, nombre)
        if port is None:
            continue
        r = port.to_numpy()
        k = kelly(r)
        eq = np.cumprod(1 + r)
        m = equity_metrics(eq)
        resultados[nombre] = r
        print(f"  {nombre:16} {k['sharpe']:11.3f} {k['g_max']:13.1%} "
              f"{m['max_dd_pct']:11.1f}%")

    if not resultados:
        print("  sin resultados")
        return

    ganador = max(resultados, key=lambda n: kelly(resultados[n])["sharpe"])
    r = resultados[ganador]
    k = kelly(r)

    print(f"\n  Ganador: {ganador}")
    print(f"  Sharpe fuera de muestra {k['sharpe']:.3f} +/- {k['se_sharpe']:.3f} "
          f"(error estandar, {k['años']:.0f} años)")
    print(f"  TECHO de crecimiento compuesto: {k['g_max']:.1%} anual")
    print(f"  Apalancamiento que lo alcanza (Kelly): {k['f_kelly']:.1f}x")

    # ---- 2. la curva ----
    print("\n--- 2. LLEGAR AL TECHO: crecimiento real segun apalancamiento ---")
    print("Recalculado sobre la serie, no con la formula: incluye el arrastre")
    print("de volatilidad y la ruina de verdad.\n")
    fs = np.array([0.25, 0.5, 1, 2, 3, 5, 8, 12, 16, 20, 25, 30, 40, 50]) * k["f_kelly"] / 8
    fs = np.unique(np.round(fs, 1))
    fs = fs[fs > 0]
    curva = curva_crecimiento(r, fs)

    print(f"  {'apalanc.':>9} {'vol anual':>10} {'CAGR':>9} {'maxDD':>8} {'':>4}")
    print("  " + "-" * 44)
    mejor_i = curva["cagr"].idxmax()
    for i, row in curva.iterrows():
        marca = ""
        if row["ruina"]:
            marca = "  RUINA"
        elif i == mejor_i:
            marca = "  <-- maximo"
        elif abs(row["f"] - k["f_kelly"] / 2) < k["f_kelly"] * 0.08:
            marca = "  medio Kelly"
        print(f"  {row['f']:8.1f}x {row['vol']:9.1%} {row['cagr']:8.1%} "
              f"{row['maxDD']:7.1%}{marca}")

    # ---- 3. el aviso ----
    print("\n--- 3. EL ERROR DE ESTIMACION, QUE ES LO QUE REVIENTA CUENTAS ---")
    sr_lo = k["sharpe"] - 1.96 * k["se_sharpe"]
    sr_hi = k["sharpe"] + 1.96 * k["se_sharpe"]
    print(f"  El Sharpe real esta, con 95% de confianza, entre "
          f"{sr_lo:.2f} y {sr_hi:.2f}.")
    print(f"  Eso es un techo entre {max(sr_lo,0)**2/2:.1%} y {sr_hi**2/2:.1%} anual.")
    print()
    if sr_lo > 0:
        f_lo = (sr_lo * k["sigma"]) / k["sigma"] ** 2
        print(f"  Kelly calculado con el Sharpe optimista: {k['f_kelly']:.1f}x")
        print(f"  Kelly calculado con el pesimista:        {f_lo:.1f}x")
        print(f"  Apalancar a {k['f_kelly']:.1f}x cuando el verdadero era {f_lo:.1f}x")
        print(f"  significa operar por encima de Kelly, donde MAS RIESGO DA MENOS")
        print(f"  DINERO y el drawdown se dispara.")
    print()
    print("  Por eso el estandar de la industria es MEDIO KELLY: conserva ~75%")
    print("  del crecimiento con la mitad de volatilidad, y sobrevive a que el")
    print("  Sharpe estimado se quede corto.")
    fh = k["f_kelly"] / 2
    fila = curva_crecimiento(r, np.array([fh])).iloc[0]
    print(f"\n  MEDIO KELLY ({fh:.1f}x): vol {fila['vol']:.0%} anual, "
          f"CAGR {fila['cagr']:.1%}, drawdown maximo {fila['maxDD']:.0%}")
    print("\n  Ese drawdown es historico y sobre datos diarios limpios. En vivo,")
    print("  con huecos de fin de semana y noticias, sera peor.")


if __name__ == "__main__":
    main()

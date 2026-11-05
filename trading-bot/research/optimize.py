"""
Optimizar para maxima rentabilidad = subir el Sharpe. No hay otra palanca.

El techo del crecimiento compuesto es SR^2/2 (prueba 10). El apalancamiento
te lleva al techo pero no lo sube. Asi que "optimizar el rendimiento" solo
puede significar una cosa: subir el Sharpe fuera de muestra.

Se prueban tres mejoras. Las tres se eligieron porque son DIVERSIFICACION, no
ajuste de parametros: no añaden grados de libertad que se puedan sobreajustar,
que es la trampa en la que este proyecto ya cayo una vez.

  1. ENSEMBLE DE VELOCIDADES. En vez de elegir un lookback de TSMOM, usar
     varios a la vez y votar. Elimina el riesgo de elegir mal el parametro y
     es practica estandar de CTA. Nota: NO multiplica las posiciones -- los
     votos se agregan en una sola señal por mercado-, asi que no sube el
     capital minimo.

  2. PESOS POR VOLATILIDAD INVERSA. Ahora mismo la cartera promedia los 21
     mercados por igual. Pero sus P&L no tienen la misma volatilidad, asi que
     los mas ruidosos dominan el riesgo sin aportar mas informacion.

  3. PESOS POR SECTOR. Metales son 4 mercados correlacionados entre si y
     pesan 4/21; divisas otros 3. Equiponderar SECTORES y luego dentro de
     cada sector reparte mejor la informacion independiente.

Todo se valida con walk-forward. Los pesos se estiman SOLO con datos pasados.
"""

from __future__ import annotations

import numpy as np
import pandas as pd

from . import data, strategies
from .backtest import BacktestConfig, run
from .diversified import coste_relativo
from .metrics import equity_metrics, sharpe

VELOCIDADES = [63, 126, 252]        # 3, 6 y 12 meses


# ---------------------------------------------------------------------------
# 1. Ensemble de velocidades
# ---------------------------------------------------------------------------

def tsmom_ensemble(df: pd.DataFrame, lookbacks=VELOCIDADES) -> np.ndarray:
    """Voto de varios TSMOM. Una sola señal por mercado, no varias posiciones.

    Se exige mayoria estricta: con 3 velocidades hacen falta 2 de acuerdo.
    Si estan repartidas, no se opera. Es mas exigente que promediar y evita
    entrar cuando las escalas se contradicen.
    """
    votos = np.zeros(len(df), dtype=int)
    for lb in lookbacks:
        votos += strategies.tsmom(df, lb)
    umbral = len(lookbacks) / 2.0
    return np.where(votos > umbral, 1, np.where(votos < -umbral, -1, 0))


# ---------------------------------------------------------------------------
# 2 y 3. Ponderacion de la cartera
# ---------------------------------------------------------------------------

def pesos_iguales(rets: pd.DataFrame, hasta: int) -> pd.Series:
    return pd.Series(1.0 / rets.shape[1], index=rets.columns)


def pesos_vol_inversa(rets: pd.DataFrame, hasta: int) -> pd.Series:
    """1/sigma, estimado SOLO con las barras anteriores a `hasta`."""
    hist = rets.iloc[:hasta]
    if len(hist) < 60:
        return pesos_iguales(rets, hasta)
    sd = hist.std()
    sd = sd.replace(0, np.nan)
    w = 1.0 / sd
    w = w.fillna(w.median())
    return w / w.sum()


def pesos_sector(rets: pd.DataFrame, hasta: int) -> pd.Series:
    """Equiponderar sectores, y dentro de cada sector equiponderar mercados."""
    secs: dict[str, list[str]] = {}
    for m in rets.columns:
        secs.setdefault(data.SECTORES.get(m, "otro"), []).append(m)
    w = pd.Series(0.0, index=rets.columns)
    for miembros in secs.values():
        for m in miembros:
            w[m] = 1.0 / (len(secs) * len(miembros))
    return w


PONDERACIONES = {
    "iguales": pesos_iguales,
    "vol inversa": pesos_vol_inversa,
    "por sector": pesos_sector,
}


# ---------------------------------------------------------------------------
# Motor
# ---------------------------------------------------------------------------

def retornos_por_mercado(mercados, señal_fn) -> pd.DataFrame:
    curvas = {}
    for nombre, df in mercados.items():
        sig = señal_fn(df)
        cfg = BacktestConfig(cost_usd_per_oz=coste_relativo(df), risk_pct=0.5,
                             atr_stop_mult=2.0, exit_mode="trail",
                             trail_atr_mult=3.0, allow_fractional=True,
                             min_units=1e-9, cooldown_bars=2)
        r = run(df, sig, cfg)
        if len(r.trades) >= 5:
            curvas[nombre] = r.equity.pct_change().fillna(0.0)
    return pd.DataFrame(curvas).fillna(0.0)


def cartera_ponderada(rets: pd.DataFrame, peso_fn, rebalanceo: int = 252) -> pd.Series:
    """Aplica los pesos, reestimandolos periodicamente con datos pasados."""
    port = pd.Series(0.0, index=rets.index)
    for inicio in range(0, len(rets), rebalanceo):
        fin = min(inicio + rebalanceo, len(rets))
        w = peso_fn(rets, inicio)          # solo mira hasta `inicio`
        tramo = rets.iloc[inicio:fin]
        port.iloc[inicio:fin] = (tramo * w).sum(axis=1)
    return port


def walk_forward(mercados, señal_fn, peso_fn, first_year=2010) -> pd.Series | None:
    """Igual que maxret: entrena con el pasado, opera el año siguiente."""
    años = sorted({d.year for df in mercados.values() for d in df.index})
    piezas = []
    for y in años:
        if y < first_year:
            continue
        ctx = {k: v[v.index.year <= y].tail(900) for k, v in mercados.items()}
        ctx = {k: v for k, v in ctx.items() if len(v) > 500}
        if len(ctx) < 5:
            continue
        rets = retornos_por_mercado(ctx, señal_fn)
        if rets.empty:
            continue
        corte = int((rets.index.year < y).sum())
        if corte < 60:
            continue
        w = peso_fn(rets, corte)
        port = (rets * w).sum(axis=1)
        sel = port[port.index.year == y]
        if len(sel) >= 20:
            piezas.append(sel)
    return pd.concat(piezas) if piezas else None


def evalua(port: pd.Series) -> dict:
    r = port.to_numpy()
    sh = sharpe(r)
    eq = np.cumprod(1 + r)
    return {"sharpe": sh, "techo": sh ** 2 / 2, "cagr_medio_kelly": sh ** 2 / 2 * 0.75,
            "maxDD_1x": equity_metrics(eq)["max_dd_pct"]}


def main() -> None:
    print("=" * 92)
    print("OPTIMIZAR PARA MAXIMA RENTABILIDAD = SUBIR EL SHARPE")
    print("=" * 92)
    print("El techo es SR^2/2. El apalancamiento lleva al techo, no lo sube.")
    print("Las tres mejoras son DIVERSIFICACION, no ajuste de parametros.\n")

    mercados = data.load_basket(start="2004-01-01")

    señales = {
        "TSMOM 252 (base)": lambda d: strategies.tsmom(d, 252),
        "ensemble 63/126/252": tsmom_ensemble,
    }

    print(f"  {'señal':22} {'pesos':14} {'Sharpe':>8} {'techo':>8} "
          f"{'CAGR 1/2K':>10} {'maxDD 1x':>9}")
    print("  " + "-" * 78)

    mejor = (None, None, -np.inf)
    for nom_s, fn_s in señales.items():
        for nom_p, fn_p in PONDERACIONES.items():
            port = walk_forward(mercados, fn_s, fn_p)
            if port is None:
                continue
            e = evalua(port)
            marca = ""
            if e["sharpe"] > mejor[2]:
                mejor = (nom_s, nom_p, e["sharpe"])
            print(f"  {nom_s:22} {nom_p:14} {e['sharpe']:8.3f} {e['techo']:7.1%} "
                  f"{e['cagr_medio_kelly']:9.1%} {e['maxDD_1x']:8.1f}%")

    print(f"\n  Mejor combinacion: {mejor[0]} + pesos {mejor[1]}  "
          f"(Sharpe {mejor[2]:.3f})")

    base = walk_forward(mercados, señales["TSMOM 252 (base)"], pesos_iguales)
    if base is not None:
        eb = evalua(base)
        print(f"  Punto de partida (prueba 10): Sharpe {eb['sharpe']:.3f}, "
              f"techo {eb['techo']:.1%}, CAGR {eb['cagr_medio_kelly']:.1%}")
        mejora_sh = mejor[2] - eb["sharpe"]
        techo_nuevo = mejor[2] ** 2 / 2
        print(f"  Mejora: {mejora_sh:+.3f} de Sharpe  ->  techo de "
              f"{eb['techo']:.1%} a {techo_nuevo:.1%}")

    print("\n" + "=" * 92)
    print("""
POR QUE ESTAS TRES Y NO OTRAS

  Ninguna de las tres añade un parametro que se pueda sobreajustar:

    - El ensemble USA todas las velocidades en vez de elegir una. Quita un
      grado de libertad, no lo añade.
    - Los pesos por volatilidad inversa se calculan, no se ajustan.
    - Los pesos por sector salen de la clasificacion del mercado, no de su
      resultado.

  Eso importa porque este proyecto ya demostro que anadir capas ajustables no
  crea ventaja (pruebas 2, 3 y 9). Diversificar si: es lo unico que ha
  funcionado de forma consistente en las once pruebas.

  AVISO: estas son ~6 pruebas mas sobre la misma muestra. La mejora hay que
  juzgarla por su TAMAÑO y por que sea coherente entre celdas de la tabla, no
  por que una celda concreta destaque.
""")


if __name__ == "__main__":
    main()

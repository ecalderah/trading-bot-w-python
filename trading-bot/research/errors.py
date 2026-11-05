"""
Analisis de errores: DONDE pierde cada estrategia, no solo cuanto.

Las pruebas 1-5 responden "¿hay ventaja?". Esta responde "¿que esta roto?",
que es lo que dice como arreglarlo. Cinco preguntas:

  1. CONCENTRACION. ¿Que fraccion del beneficio viene de las N mejores
     operaciones? Un seguidor de tendencia DEBE estar concentrado: pocas
     ganadoras enormes pagan muchas perdidas pequeñas. Si no lo esta, no
     esta siguiendo tendencias. Si lo esta demasiado (1 operacion = todo),
     es suerte.

  2. MOTIVO DE SALIDA. ¿Que salida destruye valor? Si el "giro de señal"
     cierra en verde lo que el objetivo habria cobrado mejor, sobra.

  3. DIRECCION. Largos contra cortos. El bot actual ya sabia que sus cortos
     perdian (PF 0.79 vs 1.38); la pregunta es si eso es del oro o del motor.

  4. MAE DE LAS GANADORAS. ¿Cuanto llegaron a ir en contra antes de ganar?
     Si las ganadoras rozan el stop (-0.9R), el stop esta demasiado ajustado
     y te esta sacando de operaciones buenas.

  5. MFE DE LAS PERDEDORAS. ¿Cuanto llegaron a favor antes de morir? Es la
     metrica mas accionable que existe: si una perdedora tipica llego a +1.5R
     antes de volverse -1R, no falta señal, falta gestion de la salida.
"""

from __future__ import annotations

import numpy as np
import pandas as pd

from . import data, strategies
from .backtest import BacktestConfig, Trade, run
from .costs import BROKER_SCENARIOS


def concentracion(trades: list[Trade]) -> str:
    nets = np.array(sorted((t.net for t in trades), reverse=True))
    if len(nets) == 0:
        return "  sin operaciones"
    total = nets.sum()
    ganadoras = nets[nets > 0]
    out = []
    for k in (1, 3, 5, 10):
        if k <= len(nets):
            frac = nets[:k].sum() / total if total != 0 else np.nan
            out.append(f"    top {k:2d} operaciones: {frac:6.0%} del neto total")
    out.append(f"    ganadoras: {len(ganadoras)}/{len(nets)} "
               f"({len(ganadoras)/len(nets):.0%})")
    # sin la mejor
    if len(nets) > 1 and total != 0:
        sin = total - nets[0]
        out.append(f"    neto sin la mejor operacion: {sin:+,.0f} "
                   f"(de {total:+,.0f})")
    return "\n".join(out)


def por_motivo(trades: list[Trade]) -> str:
    df = pd.DataFrame([{"motivo": t.reason, "net": t.net, "r": t.r} for t in trades])
    if df.empty:
        return "  sin operaciones"
    g = df.groupby("motivo").agg(n=("net", "size"), neto=("net", "sum"),
                                 media_r=("r", "mean")).sort_values("neto")
    lines = [f"    {'motivo':18} {'n':>5} {'neto':>12} {'R medio':>9}"]
    for m, row in g.iterrows():
        flag = "  <-- destruye valor" if row["neto"] < 0 else ""
        lines.append(f"    {m:18} {int(row['n']):5d} {row['neto']:12,.0f} "
                     f"{row['media_r']:9.3f}{flag}")
    return "\n".join(lines)


def por_direccion(trades: list[Trade]) -> str:
    lines = [f"    {'':8} {'n':>5} {'acierto':>8} {'PF':>6} {'neto':>12} {'R medio':>9}"]
    for d, name in ((1, "LARGOS"), (-1, "CORTOS")):
        sel = [t for t in trades if t.direction == d]
        if not sel:
            continue
        nets = np.array([t.net for t in sel])
        w, l = nets[nets > 0].sum(), -nets[nets <= 0].sum()
        pf = w / l if l > 0 else np.inf
        lines.append(f"    {name:8} {len(sel):5d} "
                     f"{(nets > 0).mean():7.1%} {pf:6.2f} {nets.sum():12,.0f} "
                     f"{np.mean([t.r for t in sel]):9.3f}")
    return "\n".join(lines)


def excursiones(trades: list[Trade]) -> str:
    gan = [t for t in trades if t.net > 0]
    per = [t for t in trades if t.net <= 0]
    lines = []

    if gan:
        mae = np.array([t.mae_r for t in gan])
        lines.append(f"    GANADORAS ({len(gan)}): MAE mediana {np.median(mae):+.2f} R, "
                     f"p90 {np.percentile(mae, 10):+.2f} R")
        rozaron = (mae <= -0.8).mean()
        lines.append(f"      {rozaron:.0%} llegaron a -0.8R o peor antes de ganar")
        if rozaron > 0.25:
            lines.append("      -> el stop esta cerca de sacarte de operaciones buenas")

    if per:
        mfe = np.array([t.mfe_r for t in per])
        lines.append(f"    PERDEDORAS ({len(per)}): MFE mediana {np.median(mfe):+.2f} R, "
                     f"p90 {np.percentile(mfe, 90):+.2f} R")
        for umbral in (0.5, 1.0, 1.5):
            frac = (mfe >= umbral).mean()
            lines.append(f"      {frac:5.0%} llegaron a +{umbral}R antes de acabar en perdida")
        if (mfe >= 1.0).mean() > 0.20:
            lines.append("      -> falta gestion de salida (break-even / trailing / parcial),")
            lines.append("         no falta señal: la operacion SI se movio a favor")

    return "\n".join(lines) if lines else "  sin operaciones"


def duracion(trades: list[Trade]) -> str:
    gan = [t.bars_held for t in trades if t.net > 0]
    per = [t.bars_held for t in trades if t.net <= 0]
    if not gan or not per:
        return "  sin datos"
    mg, mp = np.median(gan), np.median(per)
    lines = [f"    ganadoras: {mg:.0f} barras (mediana) | perdedoras: {mp:.0f} barras"]
    if mg > mp * 1.3:
        lines.append("    -> correcto: las ganadoras se dejan correr, las perdedoras se cortan")
    elif mp > mg * 1.1:
        lines.append("    -> INVERTIDO: aguantas mas las perdedoras que las ganadoras.")
        lines.append("       Es el patron clasico de cortar ganancias y dejar correr perdidas.")
    else:
        lines.append("    -> simetrico: el sistema no distingue ganadoras de perdedoras")
    return "\n".join(lines)


def analizar(nombre: str, trades: list[Trade]) -> None:
    print(f"\n{'=' * 78}")
    print(f"  {nombre}   ({len(trades)} operaciones)")
    print("=" * 78)
    print("\n  1. CONCENTRACION DEL BENEFICIO")
    print(concentracion(trades))
    print("\n  2. POR MOTIVO DE SALIDA")
    print(por_motivo(trades))
    print("\n  3. POR DIRECCION")
    print(por_direccion(trades))
    print("\n  4-5. EXCURSIONES (MAE / MFE)")
    print(excursiones(trades))
    print("\n  6. DURACION")
    print(duracion(trades))


def main() -> None:
    cost = BROKER_SCENARIOS["ecn_tipico"].round_trip
    oro, plata = data.load_pair(start="2004-01-01")
    cfg = BacktestConfig(cost_usd_per_oz=cost, risk_pct=0.5, atr_stop_mult=2.0,
                         reward_risk=1.5, allow_fractional=True, min_units=0.01)

    print("=" * 78)
    print("ANALISIS DE ERRORES  -  oro diario 2004-2026")
    print("=" * 78)
    print("Donde pierde cada estrategia, no solo cuanto.")

    for nombre, sig in (
        ("KALMAN (el motor actual)", strategies.kalman_trend(oro)),
        ("DONCHIAN 20", strategies.donchian(oro, 20)),
        ("DONCHIAN 120 (la meseta)", strategies.donchian(oro, 120)),
    ):
        res = run(oro, sig, cfg)
        analizar(nombre, res.trades)

    print(f"\n{'=' * 78}")
    print("""
QUE BUSCAR

  - Un seguidor de tendencia sano esta CONCENTRADO: pocas ganadoras enormes.
    Si el top 10 no llega al 50% del neto, no esta capturando tendencias.
  - Si un motivo de salida tiene neto negativo, esa salida sobra o esta mal
    calibrada.
  - Si muchas perdedoras llegaron a +1R, el problema es de GESTION, no de
    señal. Eso se arregla y es barato de probar.
  - Si las perdedoras duran mas que las ganadoras, la logica de salida esta
    invertida respecto a lo que exige seguir tendencias.
""")


if __name__ == "__main__":
    main()

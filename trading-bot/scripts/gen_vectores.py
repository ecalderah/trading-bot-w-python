"""
Regenera los vectores de referencia Python -> C#.

El arnes de validacion vive en Python y el bot en C#. Son dos
implementaciones del mismo modelo y pueden divergir en silencio: es el riesgo
que se identifico al empezar el proyecto y este archivo es lo que lo cierra.

Genera shared_cbot/tests/vectores/signals.json a partir de datos reales, y
shared_cbot/tests/SignalsTests.cs comprueba que el C# devuelve exactamente lo
mismo. Si se toca cualquiera de los dos lados, los tests fallan.

    python -m scripts.gen_vectores        (desde la raiz del repo)
"""

from __future__ import annotations

import json
from pathlib import Path

from research import data, strategies
from research.kalman import atr as atr_ema

RAIZ = Path(__file__).resolve().parent.parent
DESTINO = RAIZ / "shared_cbot" / "tests" / "vectores" / "signals.json"

BARRAS = 400
ATR_PERIODO = 14
LOOKBACK = 252
CONTROLES = [300, 320, 350, 380, 398]


def main() -> None:
    oro, _ = data.load_pair(start="2004-01-01")
    sl = oro.iloc[-BARRAS:]

    h = sl["high"].to_numpy(float)
    l = sl["low"].to_numpy(float)
    c = sl["close"].to_numpy(float)

    a = atr_ema(h, l, c, ATR_PERIODO)
    sig = strategies.tsmom(sl, LOOKBACK)

    vec = {
        "descripcion": (
            f"Vectores generados por research/ sobre GC=F, ultimas {BARRAS} "
            f"barras diarias. Regenerar con: python -m scripts.gen_vectores"
        ),
        "high": [round(float(x), 6) for x in h],
        "low": [round(float(x), 6) for x in l],
        "close": [round(float(x), 6) for x in c],
        "atr_period": ATR_PERIODO,
        "lookback": LOOKBACK,
        "esperado": [
            {"i": i, "atr": round(float(a[i]), 6), "tsmom": int(sig[i])}
            for i in CONTROLES
        ],
    }

    DESTINO.parent.mkdir(parents=True, exist_ok=True)
    DESTINO.write_text(json.dumps(vec), encoding="utf-8")

    print(f"Escrito {DESTINO}")
    print(f"  {len(h)} barras, {len(CONTROLES)} puntos de control")
    for e in vec["esperado"]:
        print(f"  i={e['i']:4d}  atr={e['atr']:12.6f}  tsmom={e['tsmom']:+d}")
    print("\nComprobar con: dotnet run --project shared_cbot/tests")


if __name__ == "__main__":
    main()

"""
La configuracion de maxima rentabilidad, con sus numeros y su precio.

Resultado de research/maxret.py sobre 17 años fuera de muestra, 21 mercados:

    señal ganadora ......... TSMOM (signo del retorno a 12 meses)
    Sharpe fuera de muestra  0.591  +/- 0.266
    TECHO de crecimiento ... SR^2/2 = 17.5% anual
    apalancamiento al techo  65x sobre la cartera equiponderada

El techo es DURO. Pasado el optimo de Kelly, mas apalancamiento da MENOS
dinero, no mas:

     16x -> CAGR  8.0%   drawdown 34%
     65x -> CAGR 19.1%   drawdown 84%   <- el maximo
     98x -> CAGR 13.8%   drawdown 96%   <- mas riesgo, menos dinero
    130x -> CAGR -0.7%
    325x -> RUINA

TRADUCCION DEL APALANCAMIENTO. El "65x" asusta pero es un artefacto de
promediar 21 mercados al 0.5% de riesgo cada uno. En terminos utiles:

    riesgo por operacion y mercado = 0.5% * apalancamiento / 21

    16x  ->  0.38% del capital total por operacion
    32x  ->  0.77%   <- medio Kelly, el estandar de la industria
    65x  ->  1.55%   <- Kelly completo

Es decir: arriesgar ~0.8% del capital en cada operacion, con ~21 mercados
abiertos a la vez. Eso es una configuracion CTA normal, no una locura.
"""

from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class Config:
    nombre: str
    señal: str = "tsmom"
    lookback: int = 252          # 12 meses, el valor del paper original
    exit_mode: str = "trail"
    trail_atr_mult: float = 3.0
    atr_stop_mult: float = 2.0
    n_mercados: int = 21
    apalancamiento: float = 32.5

    @property
    def riesgo_por_operacion_pct(self) -> float:
        """% del capital TOTAL arriesgado en cada operacion de cada mercado."""
        return 0.5 * self.apalancamiento / self.n_mercados

    def describe(self) -> str:
        return (f"{self.nombre}: TSMOM({self.lookback}) + trailing "
                f"{self.trail_atr_mult}xATR, {self.n_mercados} mercados, "
                f"riesgo {self.riesgo_por_operacion_pct:.2f}% por operacion")


# Las tres configuraciones, medidas sobre los mismos 17 años fuera de muestra.
# CAGR y drawdown son HISTORICOS y sobre datos diarios limpios: en vivo, con
# huecos de fin de semana y noticias, el drawdown sera peor.
PERFILES = {
    "prudente": (Config("prudente", apalancamiento=16.3), 0.080, 0.344, 0.148),
    "medio_kelly": (Config("medio Kelly", apalancamiento=32.5), 0.140, 0.580, 0.300),
    "kelly": (Config("Kelly completo", apalancamiento=65.0), 0.191, 0.841, 0.591),
}

RECOMENDADO = "medio_kelly"


def main() -> None:
    print("=" * 88)
    print("CONFIGURACION DE MAXIMA RENTABILIDAD")
    print("=" * 88)
    print("TSMOM(252) + trailing 3xATR sobre 21 futuros. Lo unico que cambia")
    print("entre perfiles es el apalancamiento.\n")

    print(f"  {'perfil':16} {'riesgo/op':>10} {'vol anual':>11} "
          f"{'CAGR':>8} {'drawdown':>10}")
    print("  " + "-" * 60)
    for clave, (cfg, cagr, dd, vol) in PERFILES.items():
        marca = "  <-- recomendado" if clave == RECOMENDADO else ""
        print(f"  {cfg.nombre:16} {cfg.riesgo_por_operacion_pct:9.2f}% "
              f"{vol:10.0%} {cagr:7.1%} {dd:9.0%}{marca}")

    print("\n" + "=" * 88)
    print("""
POR QUE NO KELLY COMPLETO, SI ES EL QUE MAS RINDE

El Sharpe fuera de muestra es 0.591 con un error estandar de 0.266. El
intervalo de confianza al 95% va de 0.07 a 1.11, asi que el techo real esta
entre 0.3% y 62% anual. Con esa incertidumbre:

    Kelly calculado con el Sharpe optimista .... 65x
    Kelly calculado con el pesimista ...........  8x

Si el Sharpe verdadero esta en la parte baja del intervalo y apalancas a 65x,
estas operando OCHO VECES por encima de Kelly, que es la zona donde el
crecimiento compuesto se vuelve negativo. La tabla de arriba lo enseña: a 130x
el CAGR ya es -0.7%, y a 325x la cuenta llega a cero.

Medio Kelly conserva ~75% del crecimiento con la mitad de volatilidad y
sobrevive a que la estimacion se quede corta. Es el estandar de la industria
por esta razon exacta, no por prudencia estetica.

LO QUE HAY QUE ACEPTAR PARA COBRAR EL 14%

  - Un drawdown historico del 58%. No es una cola improbable: ya ocurrio en la
    muestra. Hay que poder verlo sin cerrar el sistema.
  - Rachas largas: 5 operaciones por mercado y año significa que un año malo
    no da informacion suficiente para saber si el sistema esta roto.
  - 21 mercados abiertos a la vez, con margen suficiente en el peor momento.
    Si el broker te liquida en el drawdown, el CAGR historico no sirve de nada.
""")


if __name__ == "__main__":
    main()

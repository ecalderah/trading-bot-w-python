"""
Teoria clasica de portafolios de Markowitz (media-varianza).

Descarga precios reales desde Yahoo Finance (yfinance), estima los
rendimientos esperados y la matriz de covarianzas, y calcula:

  - la frontera eficiente (forma cerrada, permite venta en corto)
  - el portafolio de minima varianza global (GMV)
  - el portafolio tangente / maximo Sharpe
  - la Linea del Mercado de Capitales (CML)

Uso:
    python markowitz.py
    python markowitz.py --tickers AAPL MSFT NVDA --start 2020-01-01 --rf 0.04
"""

from __future__ import annotations

import argparse

import numpy as np
import pandas as pd
import yfinance as yf
import matplotlib.pyplot as plt


def descargar_precios(tickers: list[str], start: str, end: str | None) -> pd.DataFrame:
    """Descarga precios de cierre ajustados desde Yahoo Finance."""
    data = yf.download(tickers, start=start, end=end, auto_adjust=True, progress=False)
    precios = data["Close"]
    if isinstance(precios, pd.Series):  # un solo ticker
        precios = precios.to_frame(tickers[0])
    return precios.dropna(how="all").dropna(axis=1, how="any")


def estimar_parametros(precios: pd.DataFrame, periodos_ano: int = 252):
    """Devuelve (mu, Sigma) anualizados a partir de rendimientos diarios."""
    rets = precios.pct_change().dropna()
    mu = rets.mean().values * periodos_ano
    sigma = rets.cov().values * periodos_ano
    return mu, sigma


class Markowitz:
    """Solucion analitica del problema media-varianza sin restriccion de signo."""

    def __init__(self, mu: np.ndarray, sigma: np.ndarray):
        self.mu = mu
        self.sigma = sigma
        self.inv = np.linalg.inv(sigma)
        self.ones = np.ones(len(mu))

        self.A = self.ones @ self.inv @ self.ones
        self.B = self.ones @ self.inv @ self.mu
        self.C = self.mu @ self.inv @ self.mu
        self.D = self.A * self.C - self.B**2

    def pesos_para_retorno(self, r_objetivo: float) -> np.ndarray:
        """Portafolio de minima varianza para un retorno esperado dado."""
        lam = (self.C - self.B * r_objetivo) / self.D
        gam = (self.A * r_objetivo - self.B) / self.D
        return lam * (self.inv @ self.ones) + gam * (self.inv @ self.mu)

    def volatilidad(self, w: np.ndarray) -> float:
        return float(np.sqrt(w @ self.sigma @ w))

    def retorno(self, w: np.ndarray) -> float:
        return float(w @ self.mu)

    def gmv(self) -> np.ndarray:
        """Portafolio de minima varianza global."""
        return (self.inv @ self.ones) / self.A

    def tangente(self, rf: float) -> np.ndarray:
        """Portafolio tangente (maximo ratio de Sharpe) dada la tasa libre de riesgo."""
        return (self.inv @ (self.mu - rf * self.ones)) / (self.B - self.A * rf)

    def frontera(self, r_min: float, r_max: float, n: int = 100):
        objetivos = np.linspace(r_min, r_max, n)
        vols = np.array([self.volatilidad(self.pesos_para_retorno(r)) for r in objetivos])
        return vols, objetivos


def graficar(mk: Markowitz, tickers: list[str], rf: float) -> None:
    w_gmv = mk.gmv()
    w_tan = mk.tangente(rf)
    ret_gmv, vol_gmv = mk.retorno(w_gmv), mk.volatilidad(w_gmv)
    ret_tan, vol_tan = mk.retorno(w_tan), mk.volatilidad(w_tan)

    vols, objetivos = mk.frontera(ret_gmv, mk.mu.max())

    fig, ax = plt.subplots(figsize=(9, 6))
    ax.plot(vols, objetivos, label="Frontera eficiente", lw=2)
    ax.scatter(np.sqrt(np.diag(mk.sigma)), mk.mu, c="gray", label="Activos individuales")
    for t, x, y in zip(tickers, np.sqrt(np.diag(mk.sigma)), mk.mu):
        ax.annotate(t, (x, y), textcoords="offset points", xytext=(5, 5), fontsize=8)
    ax.scatter(vol_gmv, ret_gmv, c="tab:blue", s=70, zorder=5, label="Minima varianza global")
    ax.scatter(vol_tan, ret_tan, c="tab:red", s=70, zorder=5, label="Tangente (max Sharpe)")

    x = np.linspace(0, vols.max(), 50)
    ax.plot(x, rf + (ret_tan - rf) / vol_tan * x, "r--", label="Linea del Mercado de Capitales")

    ax.set_xlabel("Riesgo (desviacion estandar anual)")
    ax.set_ylabel("Retorno esperado anual")
    ax.set_title("Teoria de portafolios de Markowitz")
    ax.legend()
    ax.grid(alpha=0.3)
    fig.tight_layout()
    fig.savefig("frontera_eficiente.png", dpi=120)
    print("Grafico guardado en frontera_eficiente.png")
    plt.show()


def main() -> None:
    p = argparse.ArgumentParser(description="Markowitz mean-variance con datos reales")
    p.add_argument("--tickers", nargs="+",
                   default=["AAPL", "MSFT", "GOOG", "AMZN", "KO"])
    p.add_argument("--start", default="2019-01-01")
    p.add_argument("--end", default=None)
    p.add_argument("--rf", type=float, default=0.03, help="tasa libre de riesgo anual")
    args = p.parse_args()

    precios = descargar_precios(args.tickers, args.start, args.end)
    tickers = list(precios.columns)
    print(f"Datos: {precios.index[0].date()} a {precios.index[-1].date()}  "
          f"({len(precios)} observaciones, {len(tickers)} activos)\n")

    mu, sigma = estimar_parametros(precios)
    mk = Markowitz(mu, sigma)

    w_gmv = mk.gmv()
    w_tan = mk.tangente(args.rf)

    print(pd.DataFrame({
        "GMV": pd.Series(w_gmv, index=tickers),
        "Tangente": pd.Series(w_tan, index=tickers),
    }).round(4))

    print(f"\nGMV:      ret={mk.retorno(w_gmv):.2%}  vol={mk.volatilidad(w_gmv):.2%}")
    sharpe = (mk.retorno(w_tan) - args.rf) / mk.volatilidad(w_tan)
    print(f"Tangente: ret={mk.retorno(w_tan):.2%}  vol={mk.volatilidad(w_tan):.2%}  "
          f"Sharpe={sharpe:.2f}")

    graficar(mk, tickers, args.rf)


if __name__ == "__main__":
    main()

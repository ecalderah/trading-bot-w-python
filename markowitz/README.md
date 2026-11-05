# Markowitz — teoría clásica de portafolios (media-varianza)

Descarga precios reales desde Yahoo Finance (`yfinance`), estima μ y Σ, y calcula
la frontera eficiente, el portafolio de mínima varianza global (GMV), el
portafolio tangente (máximo Sharpe) y la Línea del Mercado de Capitales.

## Entorno (VS Code, Windows)

El entorno virtual ya está creado en `.venv/`. VS Code lo detecta por
`.vscode/settings.json`. Si necesitas recrearlo:

```powershell
python -m venv .venv
.venv\Scripts\Activate.ps1
pip install -r requirements.txt
```

En VS Code: `Ctrl+Shift+P` → **Python: Select Interpreter** → `.venv`.

## Uso

```powershell
python markowitz.py
python markowitz.py --tickers AAPL MSFT NVDA JPM --start 2020-01-01 --rf 0.04
```

Parámetros:

| flag        | por defecto                       | descripción                     |
|-------------|-----------------------------------|---------------------------------|
| `--tickers` | AAPL MSFT GOOG AMZN KO             | símbolos a incluir              |
| `--start`   | 2019-01-01                        | fecha inicial del histórico     |
| `--end`     | (hoy)                             | fecha final                     |
| `--rf`      | 0.03                              | tasa libre de riesgo anual      |

Genera `frontera_eficiente.png` y muestra los pesos por consola.

## Modelo

Minimizar `wᵀΣw` sujeto a `wᵀμ = R` y `wᵀ1 = 1`. Solución cerrada vía
multiplicadores de Lagrange (permite venta en corto, `w` puede ser negativo).
Para restringir `w ≥ 0` haría falta un optimizador (`scipy.optimize` o `cvxpy`).

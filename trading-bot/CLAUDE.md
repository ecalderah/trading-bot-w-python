# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Qué es este repo

Dos mitades, en este orden deliberado:

1. **`research/`** — el arnés de validación. Doce pruebas que decidieron qué
   estrategia merece existir. Es lo que se corre antes de tocar el bot.
2. **`cbot/` + `shared_cbot/`** — el bot que implementa lo que sobrevivió.

La plataforma es **cTrader**, y la razón concreta es el **lotaje fraccionado**:
en futuros el tamaño mínimo es un contrato entero y eso expulsa a los mercados
caros de una cuenta pequeña; en cTrader el volumen se fracciona hasta
`VolumeInUnitsStep`, así que el tamaño se ajusta al riesgo en vez de al revés.

cTrader compila los robots desde `Documentos/cAlgo/Sources/Robots/<Bot>/<Bot>/`
y no admite referencias fuera de esa carpeta. Por eso `scripts/deploy_cbot.ps1`
**copia** `shared_cbot/*.cs` en lugar de enlazarlos. **El repo es la fuente de
verdad: nunca editar los archivos copiados.**

Orden deliberado del proyecto: **primero se demuestra que hay ventaja, después
se construye la infraestructura para explotarla.** Un intento anterior empezó
por la fontanería (motor de sesiones, telemetría, dashboard) antes de validar
nada; ese código está en `parked/` con sus defectos documentados.

Los cBots viven fuera de este repo, en
`C:\Users\eical\OneDrive\Documentos\cAlgo\Sources\Robots\` (cTrader los
necesita ahí). Aquí solo está el código compartido en `shared_cbot/`.

## Comandos

```powershell
# entorno (Python 3.14, venv en el propio proyecto)
.venv\Scripts\Activate.ps1
pip install -r requirements.txt

# 1. aritmetica de costes  -- la prueba mas barata y la primera
python -m research.costs

# 2. supervivencia por regimen  -- 22 años de oro, 5 estrategias
python -m research.survival

# 3. barrido + Deflated Sharpe  -- le da al motor su mejor oportunidad
#    y despues le cobra el precio de haber buscado
python -m research.sweep

# 4. walk-forward anclado  -- reoptimiza cada año, juzga solo fuera de muestra
python -m research.walkforward

# 5. estres del resultado positivo  -- concentracion, coste, busqueda, meseta
python -m research.robustness

# 6-8. analisis de errores, modo de salida, cartera diversificada
python -m research.errors
python -m research.exits
python -m research.diversified

# 9. R:B adaptativo, en el orden correcto:
#    ¿se predice la volatilidad? -> ¿que estado predice el recorrido? -> ¿la regla funciona?
python -m research.volatility
python -m research.conditional
python -m research.adaptive

# 10. techo de rentabilidad y apalancamiento optimo (Kelly)
python -m research.maxret
python -m research.config_final     # la configuracion final y su precio

# --- el bot ---
.\scripts\deploy_cbot.ps1              # copia al arbol de cTrader
.\scripts\deploy_cbot.ps1 -Verificar   # compara sin copiar

# tests C# (puros, no necesitan cTrader): 84 vectores
dotnet run --project shared_cbot/tests

# regenerar los vectores Python -> C# tras tocar research/
python -m scripts.gen_vectores
```

## Arquitectura

### `research/` — la capa que decide

| módulo | qué hace |
|---|---|
| `costs.py` | Aritmética de costes en **USD/oz, nunca en pips**. Un umbral absoluto en pips depende del instrumento — es el error que el FIX 27 del bot actual documenta. |
| `data.py` | Oro y plata diarios (yfinance, `GC=F`/`SI=F`) con caché en parquet. **Solo diario**: yfinance no da intradía histórico, así que el scalping no se puede validar aquí. |
| `kalman.py` | Port en Python del motor del `XauusdKalmanModularBot`: filtro local lineal, hedge dinámico, `RollingStats`, `normalize_z` (el FIX 1). |
| `strategies.py` | Generadores de señal. El Kalman **y las varas de medir**: Donchian (1 parámetro), cruce de EMAs, y "siempre largo con stop". |
| `backtest.py` | Motor barra a barra, deliberadamente pesimista. |
| `metrics.py` | PF, esperanza en R, **t de la esperanza**, y **Deflated Sharpe Ratio**. |
| `survival.py` | Corre todo por régimen de mercado. |
| `sweep.py` | Barrido de parámetros + corrección por número de pruebas. |
| `walkforward.py` | Walk-forward anclado, reoptimizando cada año. La rejilla se parte en parámetros de **señal** y de **ejecución**: la señal se calcula una vez y se reutiliza (27 filtros en vez de 243). |
| `robustness.py` | Ataca el único resultado positivo: concentración anual, coste, corrección honesta por búsqueda, y meseta de parámetros. |
| `errors.py` | Dónde pierde cada estrategia: concentración del P&L, motivo de salida, dirección, MAE/MFE, duración. Fue lo que destapó el defecto del objetivo fijo. |
| `exits.py` | Compara modos de salida sobre las mismas señales. Incluye `bootstrap_p`, que es el test correcto cuando la asimetría es alta y el `t` deja de valer. |
| `diversified.py` | La cartera multimercado: 21 futuros, riesgo equiponderado, coste **relativo** al precio de cada mercado. |
| `volatility.py` | EWMA, GARCH(1,1) por MLE, ratio de Kaufman, y la evaluación honesta del pronóstico (QLIKE, Mincer-Zarnowitz). |
| `conditional.py` | Mide si el estado del día predice el recorrido, **antes** de construir ninguna regla. |
| `adaptive.py` | La regla adaptativa y su validación fuera de muestra. |
| `maxret.py` | El techo de rentabilidad (`SR²/2`) y la curva de crecimiento contra apalancamiento, recalculada sobre la serie real. Incluye el error estándar del Sharpe (Lo 2002), que es lo que decide cuánto apalancamiento es prudente. |
| `config_final.py` | La configuración recomendada con sus tres perfiles de riesgo y lo que cuesta cada uno. |

Los filtros de `kalman.py` están escritos con **aritmética escalar, no numpy**:
el 2×2 desenrollado es matemáticamente idéntico pero no asigna arrays por
barra. Con numpy el walk-forward agotaba la memoria de la máquina; con
escalares la suite completa baja de minutos a 13 segundos. Si se toca, los
números de `survival.py` tienen que salir idénticos.

### Supuestos del backtest (no cambiar sin justificarlo)

- **El modo de salida es una decisión de diseño, no un parámetro.** `exit_mode="tp"`
  (objetivo fijo) amputa la cola derecha y convierte un seguidor de tendencia en
  otra cosa: la asimetría de R cae a 0,2 y `maxR` queda clavado en el R:R
  configurado. Para tendencia va `exit_mode="trail"`. Este defecto invalidó parte
  de las pruebas 2–5 antes de detectarse.
- Señal en el **cierre** de la barra `i`, entrada en la **apertura** de `i+1`. Sin lookahead.
- Si stop y objetivo caen en la misma barra, **se asume el stop**. Con OHLC no
  se sabe el orden; asumir lo bueno es cómo se inflan los backtests.
- El coste ida y vuelta se descuenta entero en cada operación.
- Equity marcado a mercado en cada barra, no solo al cerrar.

### `cbot/PortfolioTrendBot/` — el bot

Una sola instancia gestiona toda la cesta vía `Symbols.GetSymbol()` y
`MarketData.GetBars(TimeFrame.Daily, simbolo)`. Procesa una vez al día.
Configuración: TSMOM(252) fijo + trailing 3×ATR sin objetivo + sizing por ATR.
Ver `cbot/PortfolioTrendBot/README.md`.

**Lo primero que hay que mirar en cualquier backtest es el veredicto del swap**,
no el resultado. El arnés validó la señal sobre futuros, donde la financiación
va en el precio; en CFD se cobra aparte y este sistema aguanta posiciones de
meses. El bot no discute la tasa — la mide y publica el veredicto en `OnStop`.

### `shared_cbot/` — componentes compartidos

| archivo | qué hace |
|---|---|
| `Signals.cs` | TSMOM + ATR. Fijado contra Python con vectores cruzados. |
| `RiskSizer.cs` | Sizing fraccionado. Detecta cuándo el mínimo del bróker obliga a arriesgar de más (el `FIX 23/24` del bot anterior). |
| `CostTracker.cs` | Vigila el swap y da el veredicto de viabilidad en CFD. Todo relativo al precio, **nunca en pips absolutos**. |
| `TelemetryEmitter.cs` | JSONL. Sella con `Server.Time`, **nunca `DateTime.UtcNow`** — en backtest eso es el reloj de pared. |
| `SessionEngine.cs` | Ventanas de sesión. **No lo usa el bot actual** (es diario, no intradía); se conserva probado por si vuelve el intradía. |

`research/` (Python) y `shared_cbot/` (C#) son dos implementaciones del mismo
modelo y pueden divergir en silencio. Los vectores de
`shared_cbot/tests/vectores/signals.json` lo cierran: si se toca cualquiera de
los dos lados, los tests fallan. Regenerar con `python -m scripts.gen_vectores`.

### Notas del SessionEngine

`SessionEngine.cs` decide, para un instante, si se puede abrir, si hay que
cerrar todo, y por qué. Es **.NET puro, sin dependencia de `cAlgo.API`**, y
por eso se compila y se prueba fuera de cTrader (`shared_cbot/tests`, 33
vectores). Cubre: tres ventanas conmutables con su propia zona horaria,
horario de verano automático, **semántica de unión en el solape**, festivos y
medias sesiones, y noticias con dos umbrales (cerrar antes, no abrir durante).

Reglas al tocarlo:
- Los vectores de `shared_cbot/tests/Program.cs` tienen que seguir en verde.
- En el solape, `CanTrade` si **alguna** ventana lo permite; `Flatten` solo si
  **todas** están en zona de cierre. Lo contrario cerraba posiciones al cerrar
  Londres con NY plenamente abierta.

## Convenciones

- Todo en español. Prosa y documentos **con** acentos; código C# y Python
  (comentarios, docstrings, identificadores) **sin** acentos, para mantener el
  fuente ASCII. Es la convención del `XauusdKalmanModularBot`.
- Razonar en **USD por onza**, no en pips. La conversión a pips es solo para
  presentar (`pipSize = 0.1` en el broker actual: 1 pip = $0.10/oz).
- Cada afirmación sobre rendimiento va con su `t` y su DSR. Un PF sin número
  de operaciones y sin `t` no dice nada.
- Con asimetría alta (seguimiento de tendencia) el estadístico `t` asume
  normalidad y **deja de ser fiable**: usar `exits.bootstrap_p`.
- Antes de construir una regla condicional, **medir primero que la relación
  existe** sobre las operaciones que ya se hacen (`conditional.py`). Programar la
  regla y backtestearla es empezar por el final.
- Nunca `np.log()` sobre precios de futuros sin guarda: el crudo cotizó a −37 USD
  el 20 de abril de 2020. Usar `volatility.retornos()`.
- **El modo de salida no es un parámetro, es una decisión de diseño.** Sin
  objetivo fijo, siempre trailing. Un take-profit amputa la cola derecha.
- **El lookback no se reoptimiza.** Fijo en 252 dio Sharpe 0,756 fuera de
  muestra; reoptimizarlo cada año lo bajó a 0,591.
- **«Más rentabilidad» no es subir el apalancamiento.** El techo del crecimiento
  compuesto es `SR²/2` y se alcanza en Kelly; pasado ese punto, más apalancamiento
  da **menos** dinero. Lo único que sube el techo es subir el Sharpe. Cualquier
  propuesta de mejora se juzga por su efecto sobre el Sharpe fuera de muestra.

## Contexto del bot existente

`XauusdKalmanModularBot` (en `cAlgo/Sources/Robots/`) implementa "**un
originador, varias puertas que solo vetan**" con 27 FIX documentados en la
cabecera. Su perfil congelado `Mayor Riesgo` reportó +111% en 2025 con 36.6%
de drawdown, PF 1.11, 42.5% de acierto. Ese resultado es el que este repo
puso a prueba — ver `HALLAZGOS.md`.

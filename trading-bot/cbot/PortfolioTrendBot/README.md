# PortfolioTrendBot

Seguidor de tendencia diversificado sobre una cesta de símbolos, con **una sola
instancia** del bot. Implementa la configuración que sobrevivió a las doce
pruebas del arnés (`HALLAZGOS.md`).

```
señal ..... TSMOM, signo del retorno a 252 barras diarias, FIJO
entrada ... barra siguiente a la señal
stop ...... 2×ATR(14)
salida .... trailing de 3×ATR, SIN objetivo fijo
tamaño .... riesgo % del equity / distancia del stop
pesos ..... iguales entre símbolos
```

## Instalar

```powershell
.\scripts\deploy_cbot.ps1              # copia al árbol de cTrader
.\scripts\deploy_cbot.ps1 -Verificar   # compara sin copiar
```

Después, en cTrader:

1. **Automate** → aparece `PortfolioTrendBot`
2. Compilar (`Ctrl+B`)
3. Abrirlo sobre un gráfico **diario** de cualquiera de los símbolos
4. Ajustar el parámetro **Símbolos** con los nombres **exactos** de tu bróker

El repo es la fuente de verdad. **Nunca editar los archivos copiados** en
`Documentos\cAlgo\Sources\Robots\` — los sobrescribe el siguiente despliegue.

## Primer backtest: qué mirar, en este orden

### 1. El swap. Antes que el resultado.

El arnés validó la señal sobre **futuros**, donde la financiación va embebida en
el precio del contrato. En CFD el swap se cobra aparte cada noche, y este sistema
mantiene posiciones **semanas o meses**. Una estimación previa sugería que con
swaps del 3 % anual el coste superaría al beneficio — pero esa cifra depende del
bróker, del instrumento y del lado.

Por eso el bot no discute la tasa: **la mide**. Al final del run imprime:

```
VEREDICTO SOBRE EL COSTE EN CFD:
  <dentro de presupuesto | AVISO | CRITICO>
  swap acumulado XNGUSD: ...
```

Si sale `CRITICO`, el coste se comió el beneficio bruto y la respuesta no es
ajustar parámetros: es quitar de la cesta los símbolos con peor swap, o
reconsiderar el vehículo.

### 2. La fidelidad del sizing

```
sizing: N operaciones | fidelidad media X% | al minimo M (Y%)
```

Si más del 30 % sale al volumen mínimo del bróker, **el riesgo no está
gobernando la estrategia** y los resultados no escalan con el capital. Es el
fallo que el `FIX 23/24` del bot anterior documenta: 400 de 415 operaciones al
mínimo con el contador marcando 0.

### 3. Solo entonces, el resultado

Con ~5 operaciones por símbolo y año, hace falta un backtest **largo** —
mínimo 5 años— para que las cifras signifiquen algo.

## Parámetros que no hay que tocar sin volver a medir

| parámetro | por qué |
|---|---|
| `Lookback = 252` | Fijo dio Sharpe 0,756 fuera de muestra; **reoptimizarlo cada año lo bajó a 0,591**. Meseta ancha en 252–504, así que el valor exacto no es crítico — lo crítico es no ir cambiándolo. |
| sin objetivo fijo | Un take-profit amputa la cola derecha. Con objetivo fijo la asimetría de R cae a 0,2 y ninguna ganadora supera el R:R; con trailing, una operación del backtest llegó a **+96R**. |
| sin GARCH ni régimen | El sizing por ATR ya es vol targeting. Ninguna capa añadida creó ventaja en ninguna prueba. |

## Símbolos

El `DefaultValue` usa nombres genéricos (`XAUUSD`, `US500`, `XTIUSD`…) que
**varían entre brókers**. Comprobar los tuyos en cTrader antes de arrancar: los
que no existan se omiten con un aviso en el log, no rompen el bot.

Los sectores con ventaja demostrada (prueba 8) son **metales, agrícolas e
índices**. Divisas, tipos y energía no mostraron nada significativo. Muchos
brókers de cTrader no ofrecen agrícolas — si faltan, se pierde uno de los dos
sectores buenos.

## Requisitos

- `AccessRights.FullAccess` — necesario para escribir la telemetría. Si no la
  quieres, pon **Escribir telemetría = false**; el bot funciona igual, pero
  pierdes la medición del swap, que es lo que decide si esto es viable.
- Gráfico **diario**. El bot procesa una vez al día.
- Historial suficiente: TSMOM(252) necesita 252 barras diarias antes de la
  primera señal. El bot precarga con `LoadMoreHistory` y avisa si no alcanza.

# trading-bot

Arnés de validación para estrategias sobre cTrader/cAlgo.

**Primero se demuestra que hay ventaja; después se construye el bot.**

```powershell
.venv\Scripts\Activate.ps1
pip install -r requirements.txt

python -m research.costs         # aritmetica de costes
python -m research.survival      # 22 años de oro, por regimen
python -m research.sweep         # barrido + Deflated Sharpe
python -m research.walkforward   # walk-forward anclado (lento)

# --- el bot ---
.\scripts\deploy_cbot.ps1              # copia al arbol de cTrader
dotnet run --project shared_cbot/tests  # 84 vectores C#
```

- **`HALLAZGOS.md`** — los resultados y el veredicto. Empieza por ahí.
- `research/` — el arnés de validación (12 pruebas).
- `cbot/PortfolioTrendBot/` — **el bot**: TSMOM(252) + trailing 3×ATR sobre una cesta.
- `shared_cbot/` — componentes C# compartidos, con 84 vectores de prueba.
- `scripts/` — despliegue a cTrader y generación de vectores.
- `parked/` — infraestructura escrita antes de tiempo, con sus defectos documentados.
- `CLAUDE.md` — guía para trabajar en el repo.

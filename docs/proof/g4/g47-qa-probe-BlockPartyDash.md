# G4.7 prediction probe: BlockPartyDash (3 probe clients (BotBrain on the predicted state) + 9 server bots, bump off, 150 s per leg)

## localhost (2026-11-20T17:41Z)
```text
g47-BlockPartyDash-local-1: 140s corrections=0 (0.00/5s) push=0 meanCorr=0.0cm maxCorr=0.0cm bitIdentical=1818/1849 silent=28 hazardMismatch=0 hazardExplained=0 predictedHits=0 respawns=0 replayMax=1 lateP95max=13ms extrap=0.0% frozenMax=0.0% remoteStutterP95max=2.16m/s rtt p50/p95=('16.6', '17.5') lead/target=('1', '1') laps=0 falls=0 fpsMin=58.0 | racing windows=5 (50s): corrections=0 (0.00/5s) push=0 (0.00/5s) hazardCorr=0 raceRounds=2
g47-BlockPartyDash-local-2: 140s corrections=0 (0.00/5s) push=0 meanCorr=0.0cm maxCorr=0.0cm bitIdentical=1775/1914 silent=136 hazardMismatch=0 hazardExplained=0 predictedHits=0 respawns=0 replayMax=3 lateP95max=16ms extrap=0.0% frozenMax=0.0% remoteStutterP95max=2.17m/s rtt p50/p95=('16.6', '18.5') lead/target=('1', '1') laps=0 falls=0 fpsMin=57.0 | racing windows=5 (50s): corrections=0 (0.00/5s) push=0 (0.00/5s) hazardCorr=0 raceRounds=2
g47-BlockPartyDash-local-3: 140s corrections=0 (0.00/5s) push=0 meanCorr=0.0cm maxCorr=0.0cm bitIdentical=1814/1824 silent=7 hazardMismatch=0 hazardExplained=0 predictedHits=0 respawns=0 replayMax=3 lateP95max=18ms extrap=0.0% frozenMax=0.0% remoteStutterP95max=1.82m/s rtt p50/p95=('16.6', '17.7') lead/target=('1', '1') laps=0 falls=0 fpsMin=57.0 | racing windows=5 (50s): corrections=0 (0.00/5s) push=0 (0.00/5s) hazardCorr=0 raceRounds=2
```

## 150 ms ± 15 + 2 % loss (2026-11-20T17:52Z, local Docker netem relay ws://127.0.0.1:7778)
```text
g47-BlockPartyDash-150-1: 140s corrections=17 (0.61/5s) push=8 meanCorr=28.2cm maxCorr=54.4cm bitIdentical=1720/1859 silent=109 hazardMismatch=0 hazardExplained=0 predictedHits=0 respawns=0 replayMax=16 lateP95max=213ms extrap=2.2% frozenMax=1.9% remoteStutterP95max=1.06m/s rtt p50/p95=('188.2', '300.6') lead/target=('7', '6') laps=0 falls=0 fpsMin=57.0 | racing windows=6 (60s): corrections=17 (1.42/5s) push=6 (0.50/5s) hazardCorr=0 raceRounds=3
g47-BlockPartyDash-150-2: 140s corrections=3 (0.11/5s) push=0 meanCorr=30.8cm maxCorr=38.5cm bitIdentical=1485/1512 silent=21 hazardMismatch=0 hazardExplained=0 predictedHits=0 respawns=0 replayMax=19 lateP95max=105ms extrap=2.3% frozenMax=0.0% remoteStutterP95max=1.16m/s rtt p50/p95=('183.9', '273.0') lead/target=('6', '6') laps=0 falls=0 fpsMin=58.0 | racing windows=6 (60s): corrections=3 (0.25/5s) push=0 (0.00/5s) hazardCorr=0 raceRounds=3
g47-BlockPartyDash-150-3: 140s corrections=1 (0.04/5s) push=0 meanCorr=18.6cm maxCorr=18.6cm bitIdentical=1822/1861 silent=35 hazardMismatch=0 hazardExplained=0 predictedHits=0 respawns=0 replayMax=14 lateP95max=153ms extrap=2.6% frozenMax=0.2% remoteStutterP95max=0.97m/s rtt p50/p95=('183.5', '307.3') lead/target=('5', '5') laps=0 falls=0 fpsMin=58.0 | racing windows=5 (50s): corrections=1 (0.10/5s) push=0 (0.00/5s) hazardCorr=0 raceRounds=3
```

# G4.7 prediction probe: GooTower (3 probe clients (BotBrain on the predicted state) + 9 server bots, bump off, 150 s per leg)

## localhost (2026-11-20T17:44Z)
```text
g47-GooTower-local-1: 140s corrections=0 (0.00/5s) push=5 meanCorr=0.0cm maxCorr=0.0cm bitIdentical=1536/1828 silent=284 hazardMismatch=0 hazardExplained=0 predictedHits=0 respawns=0 replayMax=2 lateP95max=19ms extrap=0.0% frozenMax=0.0% remoteStutterP95max=1.18m/s rtt p50/p95=('16.9', '18.6') lead/target=('1', '1') laps=0 falls=0 fpsMin=55.0 | racing windows=5 (50s): corrections=0 (0.00/5s) push=5 (0.50/5s) hazardCorr=0 raceRounds=2
g47-GooTower-local-2: 140s corrections=0 (0.00/5s) push=3 meanCorr=0.0cm maxCorr=0.0cm bitIdentical=1690/1746 silent=49 hazardMismatch=0 hazardExplained=0 predictedHits=0 respawns=0 replayMax=2 lateP95max=18ms extrap=0.0% frozenMax=0.0% remoteStutterP95max=2.33m/s rtt p50/p95=('17.1', '19.1') lead/target=('1', '1') laps=0 falls=0 fpsMin=56.0 | racing windows=4 (40s): corrections=0 (0.00/5s) push=3 (0.38/5s) hazardCorr=0 raceRounds=4
g47-GooTower-local-3: 140s corrections=0 (0.00/5s) push=16 meanCorr=0.0cm maxCorr=0.0cm bitIdentical=1514/1839 silent=305 hazardMismatch=0 hazardExplained=0 predictedHits=0 respawns=0 replayMax=2 lateP95max=19ms extrap=0.0% frozenMax=0.0% remoteStutterP95max=2.28m/s rtt p50/p95=('17.1', '33.3') lead/target=('1', '1') laps=0 falls=0 fpsMin=56.0 | racing windows=4 (40s): corrections=0 (0.00/5s) push=16 (2.00/5s) hazardCorr=0 raceRounds=4
```

## 150 ms ± 15 + 2 % loss (2026-11-20T17:55Z, local Docker netem relay ws://127.0.0.1:7778)
```text
g47-GooTower-150-1: 140s corrections=1 (0.04/5s) push=1 meanCorr=7.5cm maxCorr=7.5cm bitIdentical=1912/1945 silent=28 hazardMismatch=0 hazardExplained=0 predictedHits=0 respawns=0 replayMax=22 lateP95max=112ms extrap=1.4% frozenMax=0.0% remoteStutterP95max=1.46m/s rtt p50/p95=('169.4', '233.3') lead/target=('7', '6') laps=0 falls=0 fpsMin=58.0 | racing windows=5 (50s): corrections=1 (0.10/5s) push=1 (0.10/5s) hazardCorr=0 raceRounds=3
g47-GooTower-150-2: 140s corrections=2 (0.07/5s) push=10 meanCorr=19.6cm maxCorr=31.8cm bitIdentical=1397/1717 silent=304 hazardMismatch=0 hazardExplained=0 predictedHits=0 respawns=0 replayMax=27 lateP95max=403ms extrap=2.6% frozenMax=4.0% remoteStutterP95max=1.02m/s rtt p50/p95=('183.7', '266.6') lead/target=('7', '6') laps=0 falls=0 fpsMin=57.0 | racing windows=4 (40s): corrections=2 (0.25/5s) push=10 (1.25/5s) hazardCorr=0 raceRounds=5
g47-GooTower-150-3: 140s corrections=1 (0.04/5s) push=9 meanCorr=17.1cm maxCorr=17.1cm bitIdentical=1470/1789 silent=305 hazardMismatch=0 hazardExplained=0 predictedHits=0 respawns=0 replayMax=15 lateP95max=151ms extrap=3.0% frozenMax=0.0% remoteStutterP95max=0.92m/s rtt p50/p95=('183.3', '266.6') lead/target=('6', '6') laps=0 falls=0 fpsMin=58.0 | racing windows=4 (40s): corrections=1 (0.12/5s) push=9 (1.12/5s) hazardCorr=0 raceRounds=5
```

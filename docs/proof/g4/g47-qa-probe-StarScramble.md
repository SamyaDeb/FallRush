# G4.7 prediction probe: StarScramble (3 probe clients (BotBrain on the predicted state) + 9 server bots, bump off, 150 s per leg)

## localhost (2026-11-20T17:46Z)
```text
g47-StarScramble-local-1: 140s corrections=0 (0.00/5s) push=10 meanCorr=0.0cm maxCorr=0.0cm bitIdentical=1178/1768 silent=576 hazardMismatch=0 hazardExplained=0 predictedHits=0 respawns=1 replayMax=1 lateP95max=13ms extrap=0.0% frozenMax=0.0% remoteStutterP95max=1.67m/s rtt p50/p95=('16.6', '17.0') lead/target=('1', '1') laps=0 falls=0 fpsMin=57.0 | racing windows=6 (60s): corrections=0 (0.00/5s) push=10 (0.83/5s) hazardCorr=0 raceRounds=2
g47-StarScramble-local-2: 140s corrections=1 (0.04/5s) push=6 meanCorr=9.7cm maxCorr=9.7cm bitIdentical=858/1828 silent=958 hazardMismatch=0 hazardExplained=0 predictedHits=0 respawns=2 replayMax=2 lateP95max=12ms extrap=0.0% frozenMax=0.0% remoteStutterP95max=1.53m/s rtt p50/p95=('16.6', '17.6') lead/target=('1', '1') laps=0 falls=0 fpsMin=60.0 | racing windows=6 (60s): corrections=0 (0.00/5s) push=6 (0.50/5s) hazardCorr=0 raceRounds=2
g47-StarScramble-local-3: 140s corrections=0 (0.00/5s) push=7 meanCorr=0.0cm maxCorr=0.0cm bitIdentical=1415/1889 silent=464 hazardMismatch=0 hazardExplained=0 predictedHits=0 respawns=0 replayMax=2 lateP95max=16ms extrap=0.0% frozenMax=0.0% remoteStutterP95max=1.54m/s rtt p50/p95=('16.8', '33.3') lead/target=('1', '1') laps=0 falls=0 fpsMin=58.0 | racing windows=6 (60s): corrections=0 (0.00/5s) push=7 (0.58/5s) hazardCorr=0 raceRounds=2
```

## 150 ms ± 15 + 2 % loss (2026-11-20T17:58Z, local Docker netem relay ws://127.0.0.1:7778)
```text
g47-StarScramble-150-1: 140s corrections=6 (0.21/5s) push=16 meanCorr=21.6cm maxCorr=33.9cm bitIdentical=1308/1887 silent=554 hazardMismatch=0 hazardExplained=0 predictedHits=0 respawns=0 replayMax=23 lateP95max=138ms extrap=2.9% frozenMax=0.0% remoteStutterP95max=2.01m/s rtt p50/p95=('183.6', '246.8') lead/target=('5', '6') laps=0 falls=0 fpsMin=56.0 | racing windows=6 (60s): corrections=4 (0.33/5s) push=13 (1.08/5s) hazardCorr=0 raceRounds=3
g47-StarScramble-150-2: 140s corrections=8 (0.29/5s) push=14 meanCorr=42.2cm maxCorr=77.5cm bitIdentical=1243/1798 silent=530 hazardMismatch=0 hazardExplained=0 predictedHits=0 respawns=0 replayMax=19 lateP95max=119ms extrap=2.0% frozenMax=0.9% remoteStutterP95max=1.99m/s rtt p50/p95=('186.9', '275.2') lead/target=('7', '6') laps=0 falls=0 fpsMin=55.0 | racing windows=6 (60s): corrections=3 (0.25/5s) push=14 (1.17/5s) hazardCorr=0 raceRounds=3
g47-StarScramble-150-3: 140s corrections=428 (15.29/5s) push=15 meanCorr=7.4cm maxCorr=152.0cm bitIdentical=842/1857 silent=569 hazardMismatch=0 hazardExplained=0 predictedHits=0 respawns=0 replayMax=22 lateP95max=148ms extrap=1.9% frozenMax=0.5% remoteStutterP95max=1.77m/s rtt p50/p95=('185.0', '254.1') lead/target=('7', '6') laps=0 falls=0 fpsMin=56.0 | racing windows=6 (60s): corrections=2 (0.17/5s) push=13 (1.08/5s) hazardCorr=0 raceRounds=3
```

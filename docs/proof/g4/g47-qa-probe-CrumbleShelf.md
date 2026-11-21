# G4.7 prediction probe: CrumbleShelf (3 probe clients (BotBrain on the predicted state) + 9 server bots, bump off, 150 s per leg)

## localhost (2026-11-20T17:49Z)
```text
g47-CrumbleShelf-local-1: 140s corrections=0 (0.00/5s) push=1 meanCorr=0.0cm maxCorr=0.0cm bitIdentical=1686/1899 silent=210 hazardMismatch=0 hazardExplained=0 predictedHits=1 respawns=0 replayMax=2 lateP95max=24ms extrap=0.0% frozenMax=0.0% remoteStutterP95max=1.96m/s rtt p50/p95=('17.2', '33.9') lead/target=('1', '1') laps=0 falls=0 fpsMin=60.0 | racing windows=11 (110s): corrections=0 (0.00/5s) push=1 (0.05/5s) hazardCorr=0 raceRounds=1
g47-CrumbleShelf-local-2: 140s corrections=1 (0.04/5s) push=4 meanCorr=11.3cm maxCorr=11.3cm bitIdentical=1531/1929 silent=391 hazardMismatch=0 hazardExplained=0 predictedHits=0 respawns=0 replayMax=2 lateP95max=23ms extrap=0.0% frozenMax=0.0% remoteStutterP95max=1.77m/s rtt p50/p95=('16.6', '33.8') lead/target=('1', '1') laps=0 falls=0 fpsMin=58.0 | racing windows=11 (110s): corrections=1 (0.05/5s) push=4 (0.18/5s) hazardCorr=0 raceRounds=1
g47-CrumbleShelf-local-3: 140s corrections=1 (0.04/5s) push=3 meanCorr=10.7cm maxCorr=10.7cm bitIdentical=1585/1960 silent=369 hazardMismatch=0 hazardExplained=0 predictedHits=0 respawns=0 replayMax=2 lateP95max=20ms extrap=0.0% frozenMax=0.0% remoteStutterP95max=1.82m/s rtt p50/p95=('16.6', '33.6') lead/target=('1', '1') laps=0 falls=0 fpsMin=58.0 | racing windows=11 (110s): corrections=1 (0.05/5s) push=3 (0.14/5s) hazardCorr=0 raceRounds=1
```

## 150 ms ± 15 + 2 % loss (2026-11-20T18:00Z, local Docker netem relay ws://127.0.0.1:7778)
```text
g47-CrumbleShelf-150-1: 140s corrections=1 (0.04/5s) push=2 meanCorr=8.3cm maxCorr=8.3cm bitIdentical=1807/1896 silent=83 hazardMismatch=0 hazardExplained=0 predictedHits=0 respawns=0 replayMax=16 lateP95max=116ms extrap=2.6% frozenMax=0.0% remoteStutterP95max=1.58m/s rtt p50/p95=('183.3', '250.0') lead/target=('5', '6') laps=0 falls=0 fpsMin=57.0 | racing windows=11 (110s): corrections=1 (0.05/5s) push=2 (0.09/5s) hazardCorr=0 raceRounds=2
g47-CrumbleShelf-150-2: 140s corrections=2 (0.07/5s) push=6 meanCorr=18.7cm maxCorr=29.1cm bitIdentical=1383/1928 silent=535 hazardMismatch=0 hazardExplained=0 predictedHits=1 respawns=0 replayMax=20 lateP95max=165ms extrap=2.3% frozenMax=0.0% remoteStutterP95max=1.50m/s rtt p50/p95=('184.7', '277.2') lead/target=('6', '6') laps=0 falls=0 fpsMin=57.0 | racing windows=11 (110s): corrections=2 (0.09/5s) push=6 (0.27/5s) hazardCorr=0 raceRounds=2
g47-CrumbleShelf-150-3: 140s corrections=2 (0.07/5s) push=3 meanCorr=17.0cm maxCorr=18.4cm bitIdentical=1504/1961 silent=449 hazardMismatch=0 hazardExplained=0 predictedHits=0 respawns=0 replayMax=17 lateP95max=141ms extrap=2.2% frozenMax=0.0% remoteStutterP95max=1.65m/s rtt p50/p95=('199.5', '259.2') lead/target=('6', '6') laps=0 falls=0 fpsMin=56.0 | racing windows=11 (110s): corrections=2 (0.09/5s) push=3 (0.14/5s) hazardCorr=0 raceRounds=2
```

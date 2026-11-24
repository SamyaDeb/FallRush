# Ramp landing check (real KinematicMotor, every tilted walkable box)

- fail = landing later than flat ground's hop + 8 ticks, or no landing within 45 ticks (1.5 s); glide = airborne ticks with the feet < 0.2 m above a surface

## Capsule Run

- flat hop: 19 ticks

| ramp | pitch | length | hop-up (jump after 3…15 ticks) | jump-on (0…1.2 m before the low edge) | glide ticks | verdict |
|---|---|---|---|---|---|---|
| Conveyor_bpd_belt/MotorCollider | 11.3° | 30.59 m | ✗✗✗✗✓ | ✗✗✗✗✗ | 388 | **9 fail** |
| Geometry/ArchTop_0 | 23.1° | 1.40 m | ✗✗✗✗✗ | ····· | 0 | **5 fail** |
| Geometry/ArchTop_1 | 35.6° | 1.90 m | ✓✗✗✓✓ | ····· | 53 | **2 fail** |
| Geometry/ArchTop_2 | 19.1° | 1.90 m | ✓✓✓✓✓ | ····· | 13 | ok |
| Geometry/ArchTop_3 | 8.5° | 1.90 m | ✓✓✓✓✓ | ····· | 0 | ok |
| Geometry/ArchTop_5 | 8.5° | 1.90 m | ✓✓✓✓✓ | ····· | 0 | ok |
| Geometry/ArchTop_6 | 19.1° | 1.90 m | ✓✓✓✓✓ | ····· | 13 | ok |
| Geometry/ArchTop_7 | 35.6° | 1.90 m | ✓✗✗✓✓ | ····· | 53 | **2 fail** |
| Geometry/ArchTop_8 | 23.1° | 1.40 m | ✗✗✗✗✗ | ····· | 0 | **5 fail** |
| Geometry/BeltRailL | 11.3° | 30.59 m | ✗✓✗✓✓ | ····· | 31 | **2 fail** |
| Geometry/BeltRailR | 11.3° | 30.59 m | ✗✗✗✗✗ | ····· | 154 | **5 fail** |
| Geometry/S1RailL | 7.9° | 36.35 m | ✗✗✗✗✗ | ····· | 150 | **5 fail** |
| Geometry/S1RailR | 7.9° | 36.35 m | ✗✗✗✗✗ | ····· | 150 | **5 fail** |
| Geometry/S1Slope | 7.9° | 36.35 m | ✗✗✗✗✗ | ✗✗✗✗✗ | 415 | **10 fail** |

## Goo Tower

- flat hop: 19 ticks

| ramp | pitch | length | hop-up (jump after 3…15 ticks) | jump-on (0…1.2 m before the low edge) | glide ticks | verdict |
|---|---|---|---|---|---|---|
| Conveyor_gt_k2_belt_0/MotorCollider | 11.1° | 14.27 m | ✗✓✓✓✓ | ✓✗✗✗✗ | 247 | **5 fail** |
| Conveyor_gt_k6_belt_0/MotorCollider | 11.1° | 14.27 m | ✗✗✗✗✗ | ✗✗✗✗✗ | 417 | **10 fail** |
| Geometry/S2_RailShelfRamp | 28.8° | 1.14 m | ✓✓✓✓✓ | ✓✓✓✓✗ | 0 | **1 fail** |
| Geometry/S2_ShelfRamp | 28.8° | 1.14 m | ✓✓✓✓✓ | ✓✓✓✓✓ | 0 | ok |
| Geometry/S3_RailRamp | 11.1° | 14.27 m | ✗✓✓✓✓ | ✓✗✗✗✗ | 239 | **5 fail** |
| Geometry/S3_Ramp | 11.1° | 14.27 m | ✗✓✓✓✓ | ✓✗✗✗✗ | 117 | **5 fail** |
| Geometry/S5_Ramp | 27.0° | 6.06 m | ✗✗✗✓✓ | ✗✗✗✓✗ | 295 | **7 fail** |
| Geometry/S5_RampBody | 27.0° | 6.06 m | ✗✗✗✗✓ | ····· | 115 | **4 fail** |
| Geometry/S6_BeltBody | 11.1° | 14.27 m | ✗✗✗✗✗ | ····· | 150 | **5 fail** |
| Geometry/S7_Ramp | 21.4° | 7.52 m | ✓✗✗✗✗ | ✗✓✗✗✗ | 302 | **8 fail** |
| Geometry/S7_RampBody | 21.4° | 7.52 m | ✓✗✗✗✗ | ····· | 109 | **4 fail** |

## Star Fountain

- flat hop: -1 ticks

- no tilted walkable boxes

## Crumble Shelf

- flat hop: 19 ticks

- no tilted walkable boxes


**FAIL**: 104 failing case(s)

//! Chaos Capsule modifiers (`packages/shared/config/modifiers_v1.json` via `ModifiersData.g.cs`): motor and hazard
//! scope effect values by id. Visual-scope values (FOV, fog) are client-only and not here.

use crate::config::MotorModifier;
use crate::hazards::HazardModifier;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Modifier {
    pub id: u8,
    pub motor: MotorModifier,
    pub hazard: HazardModifier,
}

const fn m(id: u8, g: i32, apex: i32, decel: i32, speed: i32, tele: i32, kb: i32) -> Modifier {
    Modifier {
        id,
        motor: MotorModifier { gravity_permille: g, jump_apex_permille: apex, ground_decel_permille: decel },
        hazard: HazardModifier { hazard_speed_permille: speed, telegraph_permille: tele, knockback_permille: kb },
    }
}

/// None, MoonBounce, ButterFloors, TurboToys, BigBonk, TinyTilt.
pub const ALL: [Modifier; 6] = [
    m(0, 1000, 1000, 1000, 1000, 1000, 1000),
    m(1, 700, 1250, 1000, 1000, 1000, 1000),
    m(2, 1000, 1000, 400, 1000, 1000, 1000),
    m(3, 1000, 1000, 1000, 1200, 1000, 1000),
    m(4, 1000, 1000, 1000, 1000, 1000, 1400),
    m(5, 1000, 1000, 1000, 1000, 1000, 1000),
];

/// `ModifierEffects.Get`: unknown ids → None.
pub fn get(id: u8) -> Modifier {
    if (id as usize) < ALL.len() {
        ALL[id as usize]
    } else {
        ALL[0]
    }
}

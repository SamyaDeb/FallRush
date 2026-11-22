//! `RoundTargets` (port of `Shared/RoundTargets.cs`, = `packages/shared/src/targets.ts` and the program's
//! `round_targets`): qualify counts per round from the player count P (bots included), integer ceil.

pub const MIN_PLAYERS: i32 = 4;
pub const MAX_PLAYERS: i32 = 20;

/// `RoundTargets.For(p)`; `None` outside 4…20.
pub fn round_targets(p: i32) -> Option<[u8; 4]> {
    if !(MIN_PLAYERS..=MAX_PLAYERS).contains(&p) {
        return None;
    }
    let r1 = (p * 66 + 99) / 100;
    let r2 = (r1 * 62 + 99) / 100;
    let r3 = (r2 - 1).min(3);
    Some([r1 as u8, r2 as u8, r3 as u8, 1])
}

/// `RoundParams.RaceQualifyCount`: ceil(P × 0.66).
pub fn race_qualify_count(p: i32) -> i32 {
    if p <= 0 {
        0
    } else {
        (p * 66 + 99) / 100
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn twelve_players() {
        assert_eq!(round_targets(12), Some([8, 5, 3, 1]));
        assert_eq!(round_targets(3), None);
    }
}

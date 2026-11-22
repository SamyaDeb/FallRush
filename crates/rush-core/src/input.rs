//! Player input (port of `Shared/InputCmd.cs` and the deterministic parts of `Shared/InputSanitizer.cs`).
//!
//! On-chain, `submit_input` writes a slot's `InputBuf` and `step()` consumes one command per tick. The network-rate
//! parts of the C# sanitiser (token bucket, seq freshness, lead window) belong to the program (P2); rush-core ports
//! the value clamping ([`sanitize`]) and the missing-input rule ([`InputTrack::consume`]): repeat the last command
//! with its press edges cleared for up to 3 ticks, then neutral.

use crate::fx::isqrt_i64;

pub const JUMP_HELD: u8 = 1 << 0;
pub const JUMP_PRESSED: u8 = 1 << 1;
pub const DIVE_HELD: u8 = 1 << 2;
pub const DIVE_PRESSED: u8 = 1 << 3;
pub const KNOWN_BUTTONS: u8 = JUMP_HELD | JUMP_PRESSED | DIVE_HELD | DIVE_PRESSED;
pub const EDGE_BUTTONS: u8 = JUMP_PRESSED | DIVE_PRESSED;
/// `InputSanitizer.RepeatTicks`.
pub const REPEAT_TICKS: u8 = 3;

/// One tick of input: camera-relative stick already rotated to world X/Z (±127, length ≤ 127), buttons, camera yaw.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct InputCmd {
    pub move_x: i8,
    pub move_y: i8,
    pub buttons: u8,
    pub cam_yaw: u16,
}

impl InputCmd {
    pub const NEUTRAL: InputCmd = InputCmd { move_x: 0, move_y: 0, buttons: 0, cam_yaw: 0 };

    #[inline]
    pub const fn has(&self, b: u8) -> bool {
        self.buttons & b != 0
    }

    /// 5 bytes: move x, move y, buttons, cam yaw (LE).
    pub fn to_bytes(&self) -> [u8; 5] {
        let y = self.cam_yaw.to_le_bytes();
        [self.move_x as u8, self.move_y as u8, self.buttons, y[0], y[1]]
    }

    pub fn from_bytes(b: &[u8]) -> InputCmd {
        InputCmd { move_x: b[0] as i8, move_y: b[1] as i8, buttons: b[2], cam_yaw: u16::from_le_bytes([b[3], b[4]]) }
    }
}

/// `InputSanitizer.Sanitize`: axes to [−127, 127], the move vector to length ≤ 127 (C#: `(int)(x·127/√l2)` then shrink
/// until inside, computed exactly in integers), unknown buttons masked. Returns true if anything changed.
pub fn sanitize(cmd: &mut InputCmd) -> bool {
    let mut changed = false;
    let mut x = cmd.move_x as i64;
    let mut y = cmd.move_y as i64;
    if x < -127 {
        x = -127;
        changed = true;
    }
    if y < -127 {
        y = -127;
        changed = true;
    }
    let l2 = x * x + y * y;
    if l2 > 127 * 127 {
        x = trunc_scale(x, l2);
        y = trunc_scale(y, l2);
        let mut guard = 0;
        while x * x + y * y > 127 * 127 && guard < 8 {
            x -= x.signum();
            y -= y.signum();
            guard += 1;
        }
        changed = true;
    }
    let b = cmd.buttons & KNOWN_BUTTONS;
    if b != cmd.buttons {
        changed = true;
    }
    cmd.move_x = x as i8;
    cmd.move_y = y as i8;
    cmd.buttons = b;
    changed
}

/// trunc(v · 127 / √l2) exactly: the largest |q| with q² · l2 ≤ (127 v)².
fn trunc_scale(v: i64, l2: i64) -> i64 {
    let a = v.abs() * 127;
    let s = isqrt_i64(l2).max(1);
    let mut q = a / s;
    let mut guard = 0;
    while q > 0 && q * q * l2 > a * a && guard < 4 {
        q -= 1;
        guard += 1;
    }
    while (q + 1) * (q + 1) * l2 <= a * a && guard < 8 {
        q += 1;
        guard += 1;
    }
    if v < 0 {
        -q
    } else {
        q
    }
}

/// Per-slot missing-input state kept in `World` (`InputSanitizer._lastApplied` / `_missing`).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct InputTrack {
    pub last: InputCmd,
    pub missing: u8,
}

impl InputTrack {
    /// `InputSanitizer.Consume`: the command to simulate this tick from `offered` (`None` = none arrived).
    pub fn consume(&mut self, offered: Option<InputCmd>) -> InputCmd {
        match offered {
            Some(mut c) => {
                sanitize(&mut c);
                self.last = c;
                self.missing = 0;
                c
            }
            None => {
                self.missing = self.missing.saturating_add(1);
                if self.missing <= REPEAT_TICKS {
                    let mut c = self.last;
                    c.buttons &= !EDGE_BUTTONS;
                    c
                } else {
                    InputCmd::NEUTRAL
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn repeat_then_neutral() {
        let mut t = InputTrack::default();
        let c = InputCmd { move_x: 50, move_y: 0, buttons: JUMP_PRESSED | JUMP_HELD, cam_yaw: 0 };
        assert_eq!(t.consume(Some(c)), c);
        for _ in 0..3 {
            let r = t.consume(None);
            assert_eq!((r.move_x, r.buttons), (50, JUMP_HELD));
        }
        assert_eq!(t.consume(None), InputCmd::NEUTRAL);
    }
}

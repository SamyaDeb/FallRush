//! Bots (port of `Shared/BotBrain.cs` + `Shared/WaypointBot.cs`). One function, [`bot_input`], serves both the
//! on-chain tick (inside `step()`) and off-chain runners. Bots read only what a player can see.

use crate::codec::{Codec, Dec, Enc};
use crate::collide::Scene;
use crate::input::InputCmd;
use crate::mapblob::MapView;
use crate::world::World;

/// Per-bot memory kept in `World`.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct BotState {
    pub tier: u8,
    pub rng: u32,
    pub a: u16,
    pub b: u16,
    pub c: i32,
    pub d: i32,
}

impl Codec for BotState {
    fn enc(&self, e: &mut Enc) {
        e.u8(self.tier);
        e.u32(self.rng);
        e.u16(self.a);
        e.u16(self.b);
        e.i32(self.c);
        e.i32(self.d);
    }
    fn dec(d: &mut Dec) -> Self {
        BotState { tier: d.u8(), rng: d.u32(), a: d.u16(), b: d.u16(), c: d.i32(), d: d.i32() }
    }
}

/// The input bot `slot` sends this tick (placeholder: stands still).
pub fn bot_input(_slot: usize, _w: &mut World, _map: &MapView, _sc: &Scene) -> InputCmd {
    InputCmd::NEUTRAL
}

//! Match director (port of `Shared/MatchDirector.cs`): placeholder until P1-G.

use crate::codec::{Codec, Dec, Enc};
use crate::mapblob::MapView;
use crate::world::{Hasher, StepEvents, World};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct DirectorState {
    pub phase: u8,
}

impl Codec for DirectorState {
    fn enc(&self, e: &mut Enc) {
        e.u8(self.phase);
    }
    fn dec(d: &mut Dec) -> Self {
        DirectorState { phase: d.u8() }
    }
}

pub fn before_tick<H: Hasher>(_w: &mut World, _map: &MapView, _tick: u32, _h: &mut H, _ev: &mut StepEvents) {}
pub fn after_tick<H: Hasher>(_w: &mut World, _map: &MapView, _tick: u32, _h: &mut H, _ev: &mut StepEvents) {}

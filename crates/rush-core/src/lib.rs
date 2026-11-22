//! # rush-core
//!
//! The whole Fall Rush game simulation (motor, collisions, hazards, round rules, deal, bots) in integer maths, for
//! sBPF (the `fall_rush` program, the authority) and WASM (browser prediction). Same source + integers only = the
//! client and the chain agree bit for bit (doc 19 §0, §3).
//!
//! Rules (P1 prompt §1.2): `no_std`, no `unsafe`, no floats (lint + a source scan test), zero runtime dependencies,
//! one rounding helper (`fx::div_round`), fixed iteration order, every loop bounded, no allocation in `step()`.
#![no_std]
#![forbid(unsafe_code)]
#![deny(clippy::float_arithmetic)]
// Kept portable to older toolchains (the sBPF platform tools lag stable): no `is_none_or` / `is_multiple_of`.
#![allow(clippy::should_implement_trait, clippy::unnecessary_map_or, clippy::manual_is_multiple_of, clippy::len_without_is_empty)]

#[cfg(test)]
#[macro_use]
extern crate std;

pub mod bots;
pub mod codec;
pub mod collide;
pub mod config;
pub mod config_data;
pub mod fx;
pub mod hazards;
pub mod input;
pub mod mapblob;
pub mod modifiers;
pub mod motor;
#[cfg(any(test, feature = "builder"))]
pub mod motor_table;
pub mod rules;
pub mod vec;
pub mod vectors;
pub mod world;

/// rush-core version (goes into the ruleset with the movement_v4 hash, doc 19 §3).
pub const VERSION: u32 = 1;

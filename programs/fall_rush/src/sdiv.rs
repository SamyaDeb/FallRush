//! sBPF v0 has no signed divide: LLVM lowers `i64 /` and `%` to compiler-builtins routines costing ≈ 240–350 CU
//! each (P2 0.2, measured in LiteSVM). These strong definitions replace the weak builtins with the magnitude form
//! over the native unsigned divide (≈ 11 CU). Same results as Rust's `/` and `%` (Rust rejects a zero divisor and
//! `MIN / -1` before calling them). Same code as `crates/rush-core-sbf-check`.

#[no_mangle]
pub extern "C" fn __divdi3(a: i64, b: i64) -> i64 {
    let q = (a.unsigned_abs() / b.unsigned_abs()) as i64;
    if (a < 0) != (b < 0) { q.wrapping_neg() } else { q }
}
#[no_mangle]
pub extern "C" fn __moddi3(a: i64, b: i64) -> i64 {
    let r = (a.unsigned_abs() % b.unsigned_abs()) as i64;
    if a < 0 { r.wrapping_neg() } else { r }
}
#[no_mangle]
pub extern "C" fn __divsi3(a: i32, b: i32) -> i32 {
    let q = (a.unsigned_abs() / b.unsigned_abs()) as i32;
    if (a < 0) != (b < 0) { q.wrapping_neg() } else { q }
}
#[no_mangle]
pub extern "C" fn __modsi3(a: i32, b: i32) -> i32 {
    let r = (a.unsigned_abs() % b.unsigned_abs()) as i32;
    if a < 0 { r.wrapping_neg() } else { r }
}

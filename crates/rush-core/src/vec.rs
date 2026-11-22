//! Integer vectors (doc 19 §3): positions in mm (`IVec3`, i32), velocities in mm/s (`Vel3` = `IVec3`), unit vectors
//! in Q15. Dot and cross products use `i64`.
//!
//! Port of `Shared/Vec3.cs` (float) → integer. `Yaw256` lives in [`crate::fx`] (`yaw_*`, [`crate::fx::atan2_yaw256`]).

use crate::fx::{div_round, isqrt_round, normalize_q15, ONE_Q15};

/// A 3-vector of `i32` (mm, mm/s or Q15 depending on use).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub struct IVec3 {
    pub x: i32,
    pub y: i32,
    pub z: i32,
}

/// Velocity in mm/s.
pub type Vel3 = IVec3;

impl IVec3 {
    pub const ZERO: IVec3 = IVec3 { x: 0, y: 0, z: 0 };
    /// +Y in Q15.
    pub const UP: IVec3 = IVec3 { x: 0, y: ONE_Q15, z: 0 };
    /// −Y in Q15.
    pub const DOWN: IVec3 = IVec3 { x: 0, y: -ONE_Q15, z: 0 };

    #[inline]
    pub const fn new(x: i32, y: i32, z: i32) -> Self {
        IVec3 { x, y, z }
    }

    #[inline]
    pub const fn add(self, o: IVec3) -> IVec3 {
        IVec3::new(self.x.wrapping_add(o.x), self.y.wrapping_add(o.y), self.z.wrapping_add(o.z))
    }

    #[inline]
    pub const fn sub(self, o: IVec3) -> IVec3 {
        IVec3::new(self.x.wrapping_sub(o.x), self.y.wrapping_sub(o.y), self.z.wrapping_sub(o.z))
    }

    #[inline]
    pub const fn neg(self) -> IVec3 {
        IVec3::new(-self.x, -self.y, -self.z)
    }

    #[inline]
    pub const fn dot(self, o: IVec3) -> i64 {
        self.x as i64 * o.x as i64 + self.y as i64 * o.y as i64 + self.z as i64 * o.z as i64
    }

    #[inline]
    pub const fn cross(self, o: IVec3) -> [i64; 3] {
        [
            self.y as i64 * o.z as i64 - self.z as i64 * o.y as i64,
            self.z as i64 * o.x as i64 - self.x as i64 * o.z as i64,
            self.x as i64 * o.y as i64 - self.y as i64 * o.x as i64,
        ]
    }

    #[inline]
    pub const fn len2(self) -> i64 {
        self.dot(self)
    }

    /// Rounded length.
    #[inline]
    pub const fn len(self) -> i64 {
        isqrt_round(self.len2())
    }

    #[inline]
    pub const fn horizontal(self) -> IVec3 {
        IVec3::new(self.x, 0, self.z)
    }

    #[inline]
    pub const fn is_zero(self) -> bool {
        self.x == 0 && self.y == 0 && self.z == 0
    }

    /// Q15 unit vector along `self` (zero stays zero).
    #[inline]
    pub fn unit_q15(self) -> IVec3 {
        let n = normalize_q15(self.x as i64, self.y as i64, self.z as i64);
        IVec3::new(n[0], n[1], n[2])
    }

    /// `self` (a Q15 unit vector) × `dist` (mm) → mm, rounded per axis.
    #[inline]
    pub const fn scale_q15(self, dist: i64) -> IVec3 {
        IVec3::new(
            div_round(self.x as i64 * dist, ONE_Q15 as i64) as i32,
            div_round(self.y as i64 * dist, ONE_Q15 as i64) as i32,
            div_round(self.z as i64 * dist, ONE_Q15 as i64) as i32,
        )
    }

    /// Dot with a Q15 unit vector → the component along it in `self`'s units (rounded).
    #[inline]
    pub const fn dot_q15(self, n: IVec3) -> i64 {
        div_round(self.dot(n), ONE_Q15 as i64)
    }

    /// `self − n · (self·n)` for a Q15 unit `n` (projection onto the plane), rounded per axis.
    #[inline]
    pub const fn reject_q15(self, n: IVec3) -> IVec3 {
        let d = self.dot(n); // units · Q15
        // n · d / 2^30
        let k = (ONE_Q15 as i64) * (ONE_Q15 as i64);
        IVec3::new(
            self.x - div_round(n.x as i64 * d, k) as i32,
            self.y - div_round(n.y as i64 * d, k) as i32,
            self.z - div_round(n.z as i64 * d, k) as i32,
        )
    }

    /// Component-wise `i64` view.
    #[inline]
    pub const fn to_i64(self) -> [i64; 3] {
        [self.x as i64, self.y as i64, self.z as i64]
    }

    #[inline]
    pub const fn from_i64(v: [i64; 3]) -> IVec3 {
        IVec3::new(crate::fx::sat_i32(v[0]), crate::fx::sat_i32(v[1]), crate::fx::sat_i32(v[2]))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reject_removes_component() {
        let v = IVec3::new(100, -50, 30);
        let r = v.reject_q15(IVec3::UP);
        assert_eq!(r, IVec3::new(100, 0, 30));
        let n = IVec3::new(23170, 23170, 0);
        let r = IVec3::new(1000, 0, 0).reject_q15(n);
        assert!((r.x - 500).abs() <= 1 && (r.y + 500).abs() <= 1, "{r:?}");
    }

    #[test]
    fn dot_cross() {
        let a = IVec3::new(1, 2, 3);
        let b = IVec3::new(4, 5, 6);
        assert_eq!(a.dot(b), 32);
        assert_eq!(a.cross(b), [-3, 6, -3]);
    }
}

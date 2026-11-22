//! Canonical little-endian encoding of `World` (P1 prompt F.1): fixed field order, fixed sizes, a version byte. Used
//! for the parity hash (`rc_hash`), the FFI / WASM state transfer and the size assert (≤ 2 KB).

/// Byte writer over a fixed buffer (writes past the end are dropped and flagged).
pub struct Enc<'a> {
    pub b: &'a mut [u8],
    pub n: usize,
    pub overflow: bool,
}

impl<'a> Enc<'a> {
    pub fn new(b: &'a mut [u8]) -> Self {
        Enc { b, n: 0, overflow: false }
    }
    #[inline]
    pub fn put(&mut self, s: &[u8]) {
        if self.n + s.len() > self.b.len() {
            self.overflow = true;
            self.n += s.len();
            return;
        }
        self.b[self.n..self.n + s.len()].copy_from_slice(s);
        self.n += s.len();
    }
    pub fn u8(&mut self, v: u8) {
        self.put(&[v]);
    }
    pub fn bool(&mut self, v: bool) {
        self.u8(v as u8);
    }
    pub fn u16(&mut self, v: u16) {
        self.put(&v.to_le_bytes());
    }
    pub fn i16(&mut self, v: i16) {
        self.put(&v.to_le_bytes());
    }
    pub fn u32(&mut self, v: u32) {
        self.put(&v.to_le_bytes());
    }
    pub fn i32(&mut self, v: i32) {
        self.put(&v.to_le_bytes());
    }
    pub fn u64(&mut self, v: u64) {
        self.put(&v.to_le_bytes());
    }
    pub fn i8(&mut self, v: i8) {
        self.u8(v as u8);
    }
}

/// Byte reader (reads past the end return zeros and flag an error).
pub struct Dec<'a> {
    pub b: &'a [u8],
    pub n: usize,
    pub err: bool,
}

impl<'a> Dec<'a> {
    pub fn new(b: &'a [u8]) -> Self {
        Dec { b, n: 0, err: false }
    }
    #[inline]
    pub fn take<const N: usize>(&mut self) -> [u8; N] {
        let mut o = [0u8; N];
        if self.n + N > self.b.len() {
            self.err = true;
            self.n += N;
            return o;
        }
        o.copy_from_slice(&self.b[self.n..self.n + N]);
        self.n += N;
        o
    }
    pub fn u8(&mut self) -> u8 {
        self.take::<1>()[0]
    }
    pub fn bool(&mut self) -> bool {
        self.u8() != 0
    }
    pub fn u16(&mut self) -> u16 {
        u16::from_le_bytes(self.take())
    }
    pub fn i16(&mut self) -> i16 {
        i16::from_le_bytes(self.take())
    }
    pub fn u32(&mut self) -> u32 {
        u32::from_le_bytes(self.take())
    }
    pub fn i32(&mut self) -> i32 {
        i32::from_le_bytes(self.take())
    }
    pub fn u64(&mut self) -> u64 {
        u64::from_le_bytes(self.take())
    }
    pub fn i8(&mut self) -> i8 {
        self.u8() as i8
    }
}

/// A type with a canonical encoding.
pub trait Codec: Sized {
    fn enc(&self, e: &mut Enc);
    fn dec(d: &mut Dec) -> Self;
}

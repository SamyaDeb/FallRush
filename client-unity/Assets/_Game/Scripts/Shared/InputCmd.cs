namespace FallRush.Shared
{
    /// <summary>
    /// One tick of player input (doc 04 §4.1). 13 bytes on the wire.
    /// <para><c>MoveX</c>/<c>MoveY</c> are the camera-relative stick after the client rotated it by <c>CamYaw</c>,
    /// i.e. world X/Z, scaled to ±127 with length ≤ 127. The server never does trigonometry on input.</para>
    /// </summary>
    public struct InputCmd
    {
        public const int Size = 4 + 4 + 1 + 1 + 1 + 2;

        public const byte JumpHeld = 1 << 0;
        public const byte JumpPressed = 1 << 1;
        public const byte DiveHeld = 1 << 2;
        public const byte DivePressed = 1 << 3;
        public const byte KnownButtons = JumpHeld | JumpPressed | DiveHeld | DivePressed;
        public const byte EdgeButtons = JumpPressed | DivePressed;

        public uint Seq;
        public uint Tick;
        public sbyte MoveX;
        public sbyte MoveY;
        public byte Buttons;
        public ushort CamYaw;

        public bool Has(byte bit) => (Buttons & bit) != 0;

        public override string ToString() => $"cmd(seq={Seq} tick={Tick} move=({MoveX},{MoveY}) btn=0x{Buttons:X2} yaw={CamYaw})";
    }
}

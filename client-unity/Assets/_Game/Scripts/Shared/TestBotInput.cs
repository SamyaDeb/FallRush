namespace FallRush.Shared
{
    /// <summary>
    /// TEST-ONLY input source for server-side bots (M2 load / feel testing; not the M4 BotBrain).
    /// Wanders inside a rectangle on the XZ plane, changes heading every 1–3 s, hops now and then, dives rarely.
    /// Deterministic per slot (xorshift32), so runs are repeatable. Bots go through the same sanitiser and motor as humans.
    /// </summary>
    public sealed class TestBotInput
    {
        private uint _rng;
        private uint _seq;
        private int _turnIn;
        private int _hopIn;
        private int _diveIn;
        private byte _heading;

        public float AreaCenterX, AreaCenterZ, AreaHalfX = 10f, AreaHalfZ = 10f;

        public TestBotInput(int slot)
        {
            _rng = 0x9E3779B9u ^ (uint)(slot * 0x85EBCA6B + 1);
            _heading = (byte)Next(256);
            _turnIn = 30 + Next(60);
            _hopIn = 20 + Next(60);
            _diveIn = 150 + Next(300);
        }

        private int Next(int n)
        {
            _rng ^= _rng << 13;
            _rng ^= _rng >> 17;
            _rng ^= _rng << 5;
            return (int)(_rng % (uint)n);
        }

        public InputCmd Next(uint tick, in MotorState s)
        {
            var px = s.PosX * 0.001f - AreaCenterX;
            var pz = s.PosZ * 0.001f - AreaCenterZ;
            if (px > AreaHalfX || px < -AreaHalfX || pz > AreaHalfZ || pz < -AreaHalfZ)
            {
                // Head back toward the middle of the area.
                _heading = Yaw256.FromVector((int)(-px * 100f), (int)(-pz * 100f), _heading);
                _turnIn = 30 + Next(30);
            }
            else if (--_turnIn <= 0)
            {
                _heading = (byte)(_heading + Next(96) - 48);
                _turnIn = 30 + Next(60);
            }

            byte buttons = 0;
            if (--_hopIn <= 0)
            {
                buttons |= InputCmd.JumpPressed | InputCmd.JumpHeld;
                _hopIn = 25 + Next(70);
            }

            if (--_diveIn <= 0)
            {
                buttons |= InputCmd.DivePressed | InputCmd.DiveHeld;
                _diveIn = 150 + Next(300);
            }

            return new InputCmd
            {
                Seq = ++_seq,
                Tick = tick,
                MoveX = (sbyte)(Yaw256.SinQ(_heading) * 127 / Yaw256.Scale),
                MoveY = (sbyte)(Yaw256.CosQ(_heading) * 127 / Yaw256.Scale),
                Buttons = buttons,
                CamYaw = (ushort)(_heading << 8),
            };
        }
    }
}

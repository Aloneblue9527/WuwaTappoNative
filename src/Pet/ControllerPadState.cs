using System;
using System.IO;

namespace WuwatappoNative.Pet;

internal readonly record struct ControllerPadState(
    ushort Buttons, byte LeftTrigger, byte RightTrigger,
    short LeftX = 0, short LeftY = 0, short RightX = 0, short RightY = 0)
{
    public ControllerPadState Merge(ControllerPadState other)
    {
        // 每个摇杆使用偏转更大的那一组 X/Y，避免两个设备的轴拼成一个方向。
        var left = StrongerStick(LeftX, LeftY, other.LeftX, other.LeftY);
        var right = StrongerStick(RightX, RightY, other.RightX, other.RightY);
        return new ControllerPadState(
            (ushort)(Buttons | other.Buttons),
            Math.Max(LeftTrigger, other.LeftTrigger), Math.Max(RightTrigger, other.RightTrigger),
            left.X, left.Y, right.X, right.Y);
    }

    static (short X, short Y) StrongerStick(short x, short y, short otherX, short otherY)
    {
        long magnitude = (long)x * x + (long)y * y;
        long otherMagnitude = (long)otherX * otherX + (long)otherY * otherY;
        return otherMagnitude > magnitude ? (otherX, otherY) : (x, y);
    }

    public void WriteTo(BinaryWriter writer)
    {
        writer.Write(Buttons);
        writer.Write(LeftTrigger);
        writer.Write(RightTrigger);
        writer.Write(LeftX);
        writer.Write(LeftY);
        writer.Write(RightX);
        writer.Write(RightY);
    }

    public static ControllerPadState ReadFrom(BinaryReader reader) => new(
        reader.ReadUInt16(), reader.ReadByte(), reader.ReadByte(),
        reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16());
}

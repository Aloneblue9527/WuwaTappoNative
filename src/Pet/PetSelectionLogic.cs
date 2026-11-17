using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace WuwatappoNative.Pet;

/// <summary>
/// 多选桌宠的纯逻辑：Ctrl+A 判定、起拖死区、整组贴边偏移。
/// 不依赖窗口实例，便于回归测试直接覆盖边界条件。
/// </summary>
internal static class PetSelectionLogic
{
    public const int VK_A = 0x41;

    /// <summary>
    /// 只有已经选中至少一个角色时才响应 Ctrl+A；Alt/Shift 参与时让给其他快捷键或系统行为。
    /// </summary>
    public static bool ShouldSelectAllOnCtrlA(int vk, bool ctrl, bool alt, bool shift, int selectedCount)
        => vk == VK_A && ctrl && !alt && !shift && selectedCount > 0;

    /// <summary>
    /// Ctrl+A 的结果：加入全部可见联机成员，但不主动改变主角。
    /// 主角原本被选中说明是用户特意选的，继续保留；原本没选也不趁机加进来。
    /// </summary>
    public static (bool SelfSelected, long[] CatIds) BuildCtrlASelection(bool selfSelectedBefore, IEnumerable<long> remoteIds)
        => (selfSelectedBefore, remoteIds?.Distinct().ToArray() ?? Array.Empty<long>());

    /// <summary>
    /// 按系统 DragSize 的宽、高分别判断是否越过起拖死区。
    /// 系统设置异常（0 或负值）时至少按 1px 处理，调用方再保证最小 4px。
    /// </summary>
    public static bool ExceedsDragThreshold(Point start, Point current, Size dragSize)
    {
        int thresholdX = Math.Max(1, dragSize.Width);
        int thresholdY = Math.Max(1, dragSize.Height);
        return Math.Abs(current.X - start.X) >= thresholdX
            || Math.Abs(current.Y - start.Y) >= thresholdY;
    }

    /// <summary>
    /// 计算整组贴边需要的统一偏移。左右/上下同轴两侧都命中时取距离更近的一侧；
    /// 组比工作区还宽/高时该轴保持不动，避免拆散队形。
    /// </summary>
    public static Point ComputeGroupSnapDelta(Rectangle groupBounds, Rectangle workingArea, int threshold)
    {
        int dx = 0, dy = 0;
        if (groupBounds.Width <= workingArea.Width)
        {
            int left = groupBounds.Left - workingArea.Left;
            int right = groupBounds.Right - workingArea.Right;
            int leftDistance = Math.Abs(left);
            int rightDistance = Math.Abs(right);
            bool leftHit = leftDistance <= threshold;
            bool rightHit = rightDistance <= threshold;
            if (leftHit && rightHit) dx = leftDistance <= rightDistance ? -left : -right;
            else if (leftHit) dx = -left;
            else if (rightHit) dx = -right;
        }
        if (groupBounds.Height <= workingArea.Height)
        {
            int top = groupBounds.Top - workingArea.Top;
            int bottom = groupBounds.Bottom - workingArea.Bottom;
            int topDistance = Math.Abs(top);
            int bottomDistance = Math.Abs(bottom);
            bool topHit = topDistance <= threshold;
            bool bottomHit = bottomDistance <= threshold;
            if (topHit && bottomHit) dy = topDistance <= bottomDistance ? -top : -bottom;
            else if (topHit) dy = -top;
            else if (bottomHit) dy = -bottom;
        }
        return new Point(dx, dy);
    }
}

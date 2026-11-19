using System;
using System.Collections.Generic;

namespace WuwatappoNative.Pet;

internal enum PetZOrderProblem
{
    None,
    MissingWindow,
    NotTopMost,
    MainNotAboveRemotes,
    NotContiguous,
    /// <summary>
    /// WS_EX_TOPMOST 样式位还在，但桌宠组上方出现了普通（非 topmost）窗口：
    /// 典型的“假置顶 / broken topmost”，必须重新摘掉并恢复 TOPMOST band。
    /// </summary>
    PetsBelowNonTopMost,
}

/// <summary>
/// 纯逻辑的桌宠层级检查，方便离线回归测试。
/// pets 的顺序约定为：主桌宠在前，联机桌宠按期望的从上到下顺序排列。
/// </summary>
internal static class PetZOrderGuard
{
    /// <summary>
    /// 检查桌宠组的 z-order 健康度。
    /// </summary>
    /// <param name="pets">主桌宠（可选）+ 联机桌宠，按期望的从上到下顺序排列。</param>
    /// <param name="visibleTopLevelOrder">当前可见顶层窗口，按 z-order 从顶到底排列。</param>
    /// <param name="isTopMost">返回窗口是否带 WS_EX_TOPMOST；为 null 时跳过 topmost 校验。</param>
    /// <param name="requireContiguous">是否要求桌宠组内部连续（置顶模式）。</param>
    /// <param name="isExemptWindow">
    /// 返回 true 表示该外部窗口不参与假置顶判定：DWM cloaked、shell/系统窗口、空矩形等。
    /// </param>
    /// <param name="hasMainPet">
    /// 主桌宠是否参与本次检查。OBS 停止渲染主角时主窗口会被隐藏，此时 pets 里没有主桌宠，
    /// 必须跳过“主桌宠在联机桌宠上方”的判断，否则会把第一个联机角色误当主桌宠。
    /// </param>
    /// <param name="isOwnWindow">返回 true 表示窗口属于本进程（键盘 UI、面板等），不算外部窗口。</param>
    public static PetZOrderProblem Check(
        IReadOnlyList<IntPtr> pets,
        IReadOnlyList<IntPtr> visibleTopLevelOrder,
        Func<IntPtr, bool> isTopMost,
        bool requireContiguous,
        Func<IntPtr, bool> isExemptWindow = null,
        bool hasMainPet = true,
        Func<IntPtr, bool> isOwnWindow = null)
    {
        if (pets == null || pets.Count == 0) return PetZOrderProblem.None;
        if (visibleTopLevelOrder == null || visibleTopLevelOrder.Count == 0)
            return PetZOrderProblem.MissingWindow;

        var positions = new Dictionary<IntPtr, int>(visibleTopLevelOrder.Count);
        for (int i = 0; i < visibleTopLevelOrder.Count; i++)
            positions[visibleTopLevelOrder[i]] = i;

        int min = int.MaxValue, max = int.MinValue;
        var petPositions = new int[pets.Count];
        for (int i = 0; i < pets.Count; i++)
        {
            if (!positions.TryGetValue(pets[i], out int pos))
                return PetZOrderProblem.MissingWindow;
            petPositions[i] = pos;
            if (isTopMost != null && !isTopMost(pets[i]))
                return PetZOrderProblem.NotTopMost;
            if (pos < min) min = pos;
            if (pos > max) max = pos;
        }

        if (hasMainPet)
        {
            int mainPos = petPositions[0];
            for (int i = 1; i < petPositions.Length; i++)
            {
                if (petPositions[i] < mainPos)
                    return PetZOrderProblem.MainNotAboveRemotes;
            }
        }

        if (requireContiguous && max - min + 1 != pets.Count)
            return PetZOrderProblem.NotContiguous;

        // 置顶模式下的“假置顶”检查：topmost band 内不应夹着普通窗口。
        // 外部 topmost 窗口允许在桌宠上方（例如任务栏、输入法状态条），这里只挑
        // WS_EX_TOPMOST == false 的普通窗口。
        if (requireContiguous && isTopMost != null)
        {
            for (int i = 0; i < min; i++)
            {
                IntPtr hwnd = visibleTopLevelOrder[i];
                if (hwnd == IntPtr.Zero) continue;
                if (isOwnWindow != null && isOwnWindow(hwnd)) continue;
                if (isExemptWindow != null && isExemptWindow(hwnd)) continue;
                if (isTopMost(hwnd)) continue;
                return PetZOrderProblem.PetsBelowNonTopMost;
            }
        }

        return PetZOrderProblem.None;
    }
}

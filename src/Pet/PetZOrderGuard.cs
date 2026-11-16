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
}

/// <summary>
/// 纯逻辑的桌宠层级检查，方便离线回归测试。
/// pets 的顺序约定为：主桌宠在前，联机桌宠按期望的从上到下顺序排列。
/// </summary>
internal static class PetZOrderGuard
{
    public static PetZOrderProblem Check(
        IReadOnlyList<IntPtr> pets,
        IReadOnlyList<IntPtr> visibleTopLevelOrder,
        Func<IntPtr, bool> isTopMost,
        bool requireContiguous)
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

        int mainPos = petPositions[0];
        for (int i = 1; i < petPositions.Length; i++)
        {
            if (petPositions[i] < mainPos)
                return PetZOrderProblem.MainNotAboveRemotes;
        }

        if (requireContiguous && max - min + 1 != pets.Count)
            return PetZOrderProblem.NotContiguous;

        return PetZOrderProblem.None;
    }
}

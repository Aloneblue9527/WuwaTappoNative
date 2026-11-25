using System;
using System.Collections.Generic;
using System.Drawing;

namespace WuwatappoNative.Pet;

/// <summary>
/// 桌宠网格对齐的纯几何逻辑。网格从工作区左上角开始，按桌宠可见尺寸 + 间距分格；
/// 主角色不参与计算。
/// </summary>
internal static class PetGridLogic
{
    const double OccupiedTolerance = 0.30;

    /// <summary>网格单元比桌宠可见区域略大，避免相邻格子的桌宠互相压住。</summary>
    public static Size CellSize(Size visibleSize, int gap)
    {
        int extra = Math.Max(0, gap);
        return new Size(
            Math.Max(1, visibleSize.Width + extra),
            Math.Max(1, visibleSize.Height + extra));
    }

    /// <summary>
    /// 把桌宠可见矩形吸附到离它最近的空闲网格单元；传入 obstacles 时会跳过被
    /// 其他桌宠/主角明显占用的格子。返回窗口左上角坐标（需要减去 headroom）。
    /// </summary>
    public static Point SnapVisibleRect(
        Rectangle visible,
        Rectangle workingArea,
        Size cellSize,
        int headroom,
        IReadOnlyList<Rectangle> obstacles = null,
        bool alignRight = false)
    {
        int stepX = Math.Max(1, cellSize.Width);
        int stepY = Math.Max(1, cellSize.Height);
        int maxCol = Math.Max(0, (workingArea.Width - visible.Width) / stepX);
        int maxRow = Math.Max(0, (workingArea.Height - visible.Height) / stepY);
        int preferredCol = alignRight
            ? Math.Clamp(
                (int)Math.Round((workingArea.Right - visible.Right) / (double)stepX),
                0, maxCol)
            : Math.Clamp(
                (int)Math.Round((visible.Left - workingArea.Left) / (double)stepX),
                0, maxCol);
        int preferredRow = Math.Clamp(
            (int)Math.Round((visible.Top - workingArea.Top) / (double)stepY),
            0, maxRow);

        int CellLeft(int col) => alignRight
            ? workingArea.Right - visible.Width - col * stepX
            : workingArea.Left + col * stepX;

        Point WindowPoint(int col, int row) => new(
            CellLeft(col),
            workingArea.Top + row * stepY - Math.Max(0, headroom));

        if (obstacles == null || obstacles.Count == 0)
            return WindowPoint(preferredCol, preferredRow);

        var fallback = WindowPoint(preferredCol, preferredRow);
        long bestDistance = long.MaxValue;
        for (int row = 0; row <= maxRow; row++)
        {
            for (int col = 0; col <= maxCol; col++)
            {
                long dx = col - preferredCol;
                long dy = row - preferredRow;
                long distance = dx * dx + dy * dy;
                if (distance >= bestDistance) continue;

                var candidate = new Rectangle(
                    CellLeft(col),
                    workingArea.Top + row * stepY,
                    visible.Width,
                    visible.Height);
                if (HasSignificantOverlap(candidate, obstacles)) continue;
                bestDistance = distance;
                fallback = WindowPoint(col, row);
            }
        }
        return fallback;
    }

    static bool HasSignificantOverlap(Rectangle candidate, IReadOnlyList<Rectangle> obstacles)
    {
        foreach (var obstacle in obstacles)
        {
            var inter = Rectangle.Intersect(candidate, obstacle);
            if (inter.IsEmpty) continue;
            double ratio = (double)inter.Width * inter.Height / ((double)candidate.Width * candidate.Height);
            if (ratio > OccupiedTolerance) return true;
        }
        return false;
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;

namespace WuwatappoNative.Pet;

/// <summary>
/// 桌宠缩放时的位置保持。按整组桌宠到屏幕边缘/中心的距离选择锚点，
/// 再把每个窗口相对锚点的偏移按缩放比例换算：
/// - 右下角的桌宠缩小后仍贴着右下角，不会因为窗口以左上角为原点而离角落越来越远；
/// - 多人/伙伴整组缩放时，队形间距按同一比例缩放，不会各缩各的拆散组合。
/// </summary>
internal static class PetScalePlacement
{
    internal enum AxisAnchor
    {
        Start,
        Center,
        End,
    }

    /// <summary>
    /// 计算缩放后的窗口左上角。<paramref name="oldBounds"/> 与 <paramref name="newSizes"/> 一一对应；
    /// 整组共用一个屏幕工作区和锚点。
    /// </summary>
    internal static Point[] Reposition(
        IReadOnlyList<Rectangle> oldBounds,
        IReadOnlyList<Size> newSizes,
        Rectangle workingArea,
        int centerTolerance)
    {
        if (oldBounds == null || newSizes == null || oldBounds.Count == 0 || oldBounds.Count != newSizes.Count)
            return Array.Empty<Point>();

        var group = oldBounds[0];
        for (int i = 1; i < oldBounds.Count; i++)
            group = Rectangle.Union(group, oldBounds[i]);

        var (anchorX, anchorY) = ResolveAnchor(group, workingArea, centerTolerance);
        var rects = new Rectangle[oldBounds.Count];
        double groupCenterX = group.Left + group.Width / 2.0;
        double groupCenterY = group.Top + group.Height / 2.0;

        for (int i = 0; i < oldBounds.Count; i++)
        {
            var old = oldBounds[i];
            var size = newSizes[i];
            double scaleX = old.Width > 0 ? size.Width / (double)old.Width : 1.0;
            double scaleY = old.Height > 0 ? size.Height / (double)old.Height : 1.0;

            double left = anchorX switch
            {
                AxisAnchor.Start => group.Left + (old.Left - group.Left) * scaleX,
                AxisAnchor.End => group.Right - (group.Right - old.Right) * scaleX - size.Width,
                _ => groupCenterX + (old.Left + old.Width / 2.0 - groupCenterX) * scaleX - size.Width / 2.0,
            };
            double top = anchorY switch
            {
                AxisAnchor.Start => group.Top + (old.Top - group.Top) * scaleY,
                AxisAnchor.End => group.Bottom - (group.Bottom - old.Bottom) * scaleY - size.Height,
                _ => groupCenterY + (old.Top + old.Height / 2.0 - groupCenterY) * scaleY - size.Height / 2.0,
            };
            rects[i] = new Rectangle((int)Math.Round(left), (int)Math.Round(top), size.Width, size.Height);
        }

        // 整组放大到越界时先整体平移回工作区，避免逐只 clamp 把队形压到同一条边上。
        var scaledGroup = rects[0];
        for (int i = 1; i < rects.Length; i++)
            scaledGroup = Rectangle.Union(scaledGroup, rects[i]);
        int dx = 0, dy = 0;
        if (scaledGroup.Width <= workingArea.Width)
        {
            if (scaledGroup.Right > workingArea.Right) dx = workingArea.Right - scaledGroup.Right;
            if (scaledGroup.Left + dx < workingArea.Left) dx = workingArea.Left - scaledGroup.Left;
        }
        if (scaledGroup.Height <= workingArea.Height)
        {
            if (scaledGroup.Bottom > workingArea.Bottom) dy = workingArea.Bottom - scaledGroup.Bottom;
            if (scaledGroup.Top + dy < workingArea.Top) dy = workingArea.Top - scaledGroup.Top;
        }

        var result = new Point[rects.Length];
        for (int i = 0; i < rects.Length; i++)
        {
            var rect = rects[i];
            rect.Offset(dx, dy);
            result[i] = ClampToWorkingArea(rect, workingArea).Location;
        }
        return result;
    }

    /// <summary>按整组到左右/上下边缘的距离选择锚点；接近居中时围绕中心缩放。</summary>
    internal static (AxisAnchor X, AxisAnchor Y) ResolveAnchor(Rectangle group, Rectangle workingArea, int centerTolerance)
    {
        int tolerance = Math.Max(0, centerTolerance);
        int leftGap = Math.Max(0, group.Left - workingArea.Left);
        int rightGap = Math.Max(0, workingArea.Right - group.Right);
        int topGap = Math.Max(0, group.Top - workingArea.Top);
        int bottomGap = Math.Max(0, workingArea.Bottom - group.Bottom);

        AxisAnchor x = Math.Abs(leftGap - rightGap) <= tolerance
            ? AxisAnchor.Center
            : rightGap < leftGap ? AxisAnchor.End : AxisAnchor.Start;
        AxisAnchor y = Math.Abs(topGap - bottomGap) <= tolerance
            ? AxisAnchor.Center
            : bottomGap < topGap ? AxisAnchor.End : AxisAnchor.Start;
        return (x, y);
    }

    static Rectangle ClampToWorkingArea(Rectangle rect, Rectangle workingArea)
    {
        int x = rect.Width >= workingArea.Width
            ? workingArea.Left
            : Math.Clamp(rect.Left, workingArea.Left, workingArea.Right - rect.Width);
        int y = rect.Height >= workingArea.Height
            ? workingArea.Top
            : Math.Clamp(rect.Top, workingArea.Top, workingArea.Bottom - rect.Height);
        return new Rectangle(x, y, rect.Width, rect.Height);
    }
}

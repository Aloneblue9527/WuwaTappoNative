using System;
using System.Drawing;
using System.Windows.Forms;

namespace WuwatappoNative.Pet;

/// <summary>
/// 桌宠右键/托盘菜单的统一入口。
/// 桌宠窗口是 ShowWithoutActivation + WS_EX_NOACTIVATE 的非前台窗口，菜单以它为所有者时
/// 系统自带的“点击外部自动关闭”会失效。这里登记当前显示的菜单，并用全局 Raw Input 的
/// “任意位置鼠标按下”做兜底关闭；不调用 SetForegroundWindow，避免抢走全屏游戏的前台焦点。
/// </summary>
internal static class PetContextMenu
{
    static readonly object Gate = new();
    static ContextMenuStrip _open;
    static Control _openDispatcher;
    static int _generation;
    static int _openGeneration;
    // 触发本次弹出的鼠标按下事件可能仍在队列中，这段时间内的按下不参与关闭判断。
    static long _ignorePointerDownUntilMs;

    /// <summary>
    /// 显示菜单。会先收起上一个由本类打开的菜单；
    /// <paramref name="dispatcher"/> 用于把跨线程的关闭请求切回菜单所属的 UI 线程。
    /// </summary>
    internal static void Show(ContextMenuStrip menu, Control owner, Point screenPoint, Control dispatcher)
    {
        if (menu == null || menu.IsDisposed) return;

        // 同一实例可能正被托盘/上一次右键显示，必须同步收起，避免异步关闭误伤刚弹出的新菜单。
        CloseNow();
        try { if (menu.Visible) menu.Close(); } catch { }

        int generation;
        lock (Gate)
        {
            generation = ++_generation;
            _open = menu;
            _openGeneration = generation;
            _openDispatcher = dispatcher;
            _ignorePointerDownUntilMs = Environment.TickCount64 + 250;
        }

        try
        {
            if (owner != null && owner.IsHandleCreated && !owner.IsDisposed)
                menu.Show(owner, owner.PointToClient(screenPoint));
            else
                menu.Show(screenPoint);
        }
        catch
        {
            lock (Gate)
            {
                if (_openGeneration == generation)
                {
                    _open = null;
                    _openDispatcher = null;
                }
            }
        }
    }

    /// <summary>
    /// 任意位置的鼠标按下：只关闭“点在菜单外”的情况。
    /// 全局 Raw Input 包含本程序自己的窗口（设置面板、键盘、其他桌宠），
    /// 因此点任何非菜单区域都能收起菜单。
    /// </summary>
    internal static void NotifyPointerDown(int vk)
    {
        if (vk != 0x01 && vk != 0x02) return;

        ContextMenuStrip menu;
        Control dispatcher;
        int generation;
        lock (Gate)
        {
            menu = _open;
            dispatcher = _openDispatcher;
            generation = _openGeneration;
            if (menu == null) return;
            if (Environment.TickCount64 < _ignorePointerDownUntilMs) return;
        }

        Point point;
        try { point = Cursor.Position; } catch { return; }

        void CheckAndClose()
        {
            try
            {
                if (menu.IsDisposed || !menu.Visible) return;
                // 子菜单是独立的 ToolStripDropDown 窗口，不在父菜单 Bounds 内；
                // 点“桌宠大小/透明度”等子项时不能把整个菜单关掉。
                if (ContainsPoint(menu, point)) return;
            }
            catch { return; }
            CloseIfCurrent(menu, generation);
        }

        Dispatch(dispatcher, CheckAndClose);
    }

    static bool ContainsPoint(ToolStripDropDown dropDown, Point point)
    {
        try
        {
            if (dropDown.Visible && dropDown.Bounds.Contains(point)) return true;
        }
        catch { }
        foreach (ToolStripItem item in dropDown.Items)
        {
            if (item is not ToolStripMenuItem menuItem || !menuItem.HasDropDownItems) continue;
            var sub = menuItem.DropDown;
            if (sub != null && ContainsPoint(sub, point)) return true;
        }
        return false;
    }

    /// <summary>关闭当前由 <see cref="Show"/> 打开的菜单；没有菜单时无副作用。</summary>
    internal static void CloseOpen()
    {
        ContextMenuStrip menu;
        Control dispatcher;
        int generation;
        lock (Gate)
        {
            menu = _open;
            dispatcher = _openDispatcher;
            generation = _openGeneration;
        }
        if (menu == null) return;
        Dispatch(dispatcher, () => CloseIfCurrent(menu, generation));
    }

    static void Dispatch(Control dispatcher, Action action)
    {
        try
        {
            if (dispatcher != null && !dispatcher.IsDisposed && dispatcher.IsHandleCreated && dispatcher.InvokeRequired)
            {
                dispatcher.BeginInvoke(action);
                return;
            }
        }
        catch { }
        action();
    }

    /// <summary>只关闭“仍是当前这一次显示”的菜单，避免延迟关闭误伤同实例的新菜单。</summary>
    static void CloseIfCurrent(ContextMenuStrip menu, int generation)
    {
        lock (Gate)
        {
            if (!ReferenceEquals(_open, menu) || _openGeneration != generation) return;
            _open = null;
            _openDispatcher = null;
        }
        CloseMenu(menu);
    }

    /// <summary>UI 线程同步收起当前菜单，用于 Show 前清理同实例的旧显示。</summary>
    static void CloseNow()
    {
        ContextMenuStrip menu;
        lock (Gate)
        {
            menu = _open;
            _open = null;
            _openDispatcher = null;
        }
        if (menu == null) return;
        CloseMenu(menu);
    }

    static void CloseMenu(ContextMenuStrip menu)
    {
        try
        {
            if (!menu.IsDisposed && menu.Visible) menu.Close();
        }
        catch { }
    }
}

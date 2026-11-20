using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DragonButler;

/// <summary>
/// 托管配置的独立窗口：一个 3×2 的桌面。
///
/// 左键单击 → 启动设置（ToolLaunchWindow，和主窗共用同一个）
/// 左键双击 → 直接把它跑一次
/// 右键     → 菜单（今晚跑不跑 / 改配置 / 移除）
/// 长按拖动 → 换顺序，别的一格让位（插入不是交换）
/// 悬停     → 底部那行换成这一格的说明
///
/// 和主窗那张卡共用同一份 <see cref="AppState.Settings.ToolPages"/> 与队列顺序，
/// 所以这边怎么摆，那边就是怎么跑——不存在「两个地方各记一份」。
/// </summary>
public partial class TrayConfigWindow : Window
{
    /// <summary>这一桌摆几列。</summary>
    private const int Columns = 3;

    /// <summary>这一桌摆几行。</summary>
    private const int Rows = 2;

    /// <summary>一屏几格。和 <see cref="ToolPages.PerPage"/> 一致（都是 6），只是排法不同。</summary>
    private const int PerView = Columns * Rows;

    private const string DefaultHint = "左键单击改设置 · 双击直接启动 · 右键改「今晚跑不跑」/移除 · 按住拖到别处换顺序";

    private int _page;
    private readonly Dictionary<int, Grid> _views = new();
    private readonly Dictionary<string, Border> _tiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Point> _positions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Border> _dots = new();

    // ---- 拖动 ----
    private Border? _dragTile;
    private string _dragId = "";
    private DispatcherTimer? _hold;
    private Point _origin;
    private Point _grab;
    private bool _dragging;

    /// <summary>这边改过东西没有。主窗据此决定要不要重画。</summary>
    public bool Changed { get; private set; }

    public TrayConfigWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Render(animate: false);
    }

    // =========================================================
    //  数据
    // =========================================================

    private static List<string> KnownIds()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ProfileRegistry.All)
            if (!string.IsNullOrWhiteSpace(p.ToolId)) set.Add(p.ToolId);
        foreach (var e in AppState.Settings.Queue ?? new List<QueueEntry>())
            if (!string.IsNullOrWhiteSpace(e.ToolId)) set.Add(e.ToolId);
        return set.ToList();
    }

    private static List<List<string>> Pages()
        => ToolPages.Normalize(AppState.Settings.ToolPages, KnownIds());

    private void Store(List<List<string>> pages)
    {
        var packed = ToolPages.Repack(pages);
        AppState.Settings.ToolPages = packed;
        AppState.Settings.ToolOrder = ToolPages.Flatten(packed).ToArray();
        AppState.SaveSettings();
        Changed = true;
    }

    private static QueueEntry? EntryOf(string toolId)
        => (AppState.Settings.Queue ?? new List<QueueEntry>())
            .FirstOrDefault(e => string.Equals(e.ToolId, toolId, StringComparison.OrdinalIgnoreCase));

    // =========================================================
    //  渲染
    // =========================================================

    private void Render(bool animate)
    {
        var pages = Pages();
        _page = Math.Clamp(_page, 0, Math.Max(0, pages.Count - 1));

        var before = new Dictionary<string, Point>(_positions, StringComparer.OrdinalIgnoreCase);
        _positions.Clear();
        _tiles.Clear();
        _views.Clear();
        TileHost.Children.Clear();

        int total = ToolPages.Flatten(pages).Count;
        TbCount.Text = total == 0 ? "桌面是空的" : $"共 {total} 个";

        for (int p = 0; p < pages.Count; p++)
        {
            var view = new Grid { RenderTransform = new TranslateTransform() };
            for (int c = 0; c < Columns; c++)
                view.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int r = 0; r < Rows; r++)
                view.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var list = pages[p];
            for (int i = 0; i < list.Count && i < PerView; i++)
            {
                var tile = BuildTile(ProfileRegistry.Get(list[i]));
                Place(tile, i);
                view.Children.Add(tile);
            }

            // 只有当前页画「＋」：别的页上画一个添加位，用户点不到还得先翻过去
            if (p == _page && list.Count < PerView)
            {
                var add = BuildAddTile();
                Place(add, list.Count);
                view.Children.Add(add);
            }

            for (int i = list.Count + (p == _page ? 1 : 0); i < PerView; i++)
            {
                var empty = BuildEmptyTile();
                Place(empty, i);
                view.Children.Add(empty);
            }

            view.IsHitTestVisible = p == _page;
            TileHost.Children.Add(view);
            _views[p] = view;
        }

        PlacePages(animate: false);
        RenderDots();

        int overflow = Math.Max(0, total - ToolPages.MaxPages * ToolPages.PerPage);
        TbOverflow.Visibility = overflow > 0 ? Visibility.Visible : Visibility.Collapsed;
        TbOverflow.Text = $"还有 {overflow} 个没摆上来——最多 {ToolPages.MaxPages} 页。";

        int shown = pages.Count <= 1 ? 0 : (_page + 1);
        TbFoot.Text = shown == 0
            ? "这一页就是全部。放满 6 个会自动开下一页。"
            : $"第 {shown} 页 / 共 {pages.Count} 页。左右滑或点下面的点翻页。";

        if (animate) AnimateFrom(before);
    }

    private void Place(UIElement element, int slot)
    {
        int col = slot % Columns;
        int row = slot / Columns;
        Grid.SetColumn(element, col);
        Grid.SetRow(element, row);
    }

    private double ViewWidth
    {
        get
        {
            double w = TileClip?.ActualWidth ?? 0;
            if (w <= 0) w = TileHost?.ActualWidth ?? 0;
            if (w <= 0) w = Width - 40;
            return w > 0 ? w : 640;
        }
    }

    private void PlacePages(bool animate)
    {
        double w = ViewWidth;
        foreach (var kv in _views)
        {
            var tt = (TranslateTransform)kv.Value.RenderTransform;
            double target = (kv.Key - _page) * w;
            if (!animate)
            {
                kv.Value.BeginAnimation(TranslateTransform.XProperty, null);
                tt.X = target;
                continue;
            }
            var anim = new DoubleAnimation(tt.X, target, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd,
            };
            kv.Value.BeginAnimation(TranslateTransform.XProperty, anim);
        }
    }

    private void GoPage(int index)
    {
        var pages = Pages();
        int target = Math.Clamp(index, 0, Math.Max(0, pages.Count - 1));
        if (target == _page) return;
        _page = target;
        PlacePages(animate: true);
        UpdateDots();
        int shown = pages.Count <= 1 ? 0 : _page + 1;
        TbFoot.Text = shown == 0
            ? "这一页就是全部。放满 6 个会自动开下一页。"
            : $"第 {shown} 页 / 共 {pages.Count} 页。左右滑或点下面的点翻页。";
    }

    /// <summary>
    /// 让每一格从「它刚才在哪」滑到「它现在该在哪」。
    /// 这就是手机桌面上拖完一格之后别的格子「让开」的那个效果——
    /// 不这么做的话，落点一换，别的格子是瞬移的，眼睛跟不上谁去了哪。
    /// </summary>
    private void AnimateFrom(Dictionary<string, Point> before)
    {
        if (before.Count == 0) return;
        foreach (var kv in _tiles)
        {
            if (!before.TryGetValue(kv.Key, out var old)) continue;
            var tile = kv.Value;
            if (tile.RenderTransform is not TranslateTransform tt) continue;

            // 目标位置：先把布局跑一遍拿到最终点
            tile.UpdateLayout();
            var to = tile.TranslatePoint(new Point(0, 0), TileHost);
            var delta = new Vector(old.X - to.X, old.Y - to.Y);
            if (Math.Abs(delta.X) < 0.5 && Math.Abs(delta.Y) < 0.5) continue;

            tt.X = delta.X;
            tt.Y = delta.Y;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var ax = new DoubleAnimation(delta.X, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease };
            var ay = new DoubleAnimation(delta.Y, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease };
            tt.BeginAnimation(TranslateTransform.XProperty, ax);
            tt.BeginAnimation(TranslateTransform.YProperty, ay);
        }
    }

    // =========================================================
    //  格子
    // =========================================================

    private Brush? Res(string key) => TryFindResource(key) as Brush;

    private Border BuildTile(ToolProfile profile)
    {
        var entry = EntryOf(profile.ToolId);
        bool onDuty = entry == null || entry.Enabled;
        bool runningThis = QueueService.IsRunning &&
                           string.Equals(QueueService.CurrentToolId, profile.ToolId, StringComparison.OrdinalIgnoreCase);
        bool ready = !string.IsNullOrWhiteSpace(profile.ExecutablePath) && System.IO.File.Exists(profile.ExecutablePath);

        var tile = new Border
        {
            Margin = new Thickness(5),
            Padding = new Thickness(8, 12, 8, 10),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = Res("BorderBrush") ?? Brushes.Gray,
            Background = Res("CardBg2Brush") ?? Brushes.Transparent,
            Cursor = Cursors.Hand,
            Tag = profile.ToolId,
            Opacity = onDuty ? 1.0 : 0.5,
            RenderTransform = new TranslateTransform(),
        };

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var icon = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(11),
            Background = Res("InsetBrush") ?? Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Center,
            ClipToBounds = true,
        };
        var bmp = ToolIconService.GetBitmap(profile);
        if (bmp != null)
            icon.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
        else
            icon.Child = new TextBlock
            {
                Text = string.IsNullOrEmpty(profile.DisplayName) ? "?" : profile.DisplayName[..1].ToUpperInvariant(),
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Res("TextSecondaryBrush") ?? Brushes.Gray,
            };
        stack.Children.Add(icon);

        var name = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.ToolId : profile.DisplayName,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 7, 0, 0),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Res("TextPrimaryBrush") ?? Brushes.White,
        };
        stack.Children.Add(name);

        var state = new TextBlock
        {
            Text = !onDuty ? "今晚不跑" : runningThis ? "运行中" : ready ? "已就绪" : "未配置",
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = !onDuty ? (Res("TextTertiaryBrush") ?? Brushes.Gray)
                        : runningThis ? (Res("BrandBrush") ?? Brushes.Green)
                        : ready ? (Res("SuccessBrush") ?? Brushes.Green)
                        : (Res("TextTertiaryBrush") ?? Brushes.Gray),
        };
        stack.Children.Add(state);

        tile.Child = stack;
        tile.ToolTip = BuildTip(profile, ready, onDuty);

        tile.MouseEnter += (_, _) =>
        {
            tile.BorderBrush = Res("BorderStrongBrush") ?? Brushes.Gray;
            string duty = onDuty ? "让它今晚不跑" : "换成今晚跑";
            TbHint.Text = $"{profile.DisplayName}：左键单击改设置 · 双击现在就跑 · 右键{duty} / 移除";
        };
        tile.MouseLeave += (_, _) =>
        {
            tile.BorderBrush = runningThis ? (Res("BrandBrush") ?? Brushes.Gray) : (Res("BorderBrush") ?? Brushes.Gray);
            TbHint.Text = DefaultHint;
        };

        tile.MouseLeftButtonDown += Tile_PressDown;
        tile.MouseMove += Tile_DragMove;
        tile.MouseLeftButtonUp += Tile_PressUp;
        tile.MouseRightButtonUp += Tile_RightClick;

        _tiles[profile.ToolId] = tile;
        _positions[profile.ToolId] = new Point(0, 0);
        return tile;
    }

    private string BuildTip(ToolProfile profile, bool ready, bool onDuty)
    {
        var lines = new List<string>();
        lines.Add(profile.DisplayName);
        lines.Add(ready ? profile.ExecutablePath : "还没找到主程序——左键单击配一次。");
        if (!onDuty) lines.Add("今晚不跑（右键可以改回来）");
        lines.Add("左键改设置 · 双击直接启动 · 右键菜单 · 按住拖换顺序");
        return string.Join("\n", lines);
    }

    private Border BuildAddTile()
    {
        var tile = new Border
        {
            Margin = new Thickness(5),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = Res("BorderBrush") ?? Brushes.Gray,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = "＋",
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Res("TextSecondaryBrush") ?? Brushes.Gray,
        });
        stack.Children.Add(new TextBlock
        {
            Text = "添加",
            FontSize = 12,
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Res("TextSecondaryBrush") ?? Brushes.Gray,
        });
        tile.Child = stack;
        tile.MouseEnter += (_, _) => tile.BorderBrush = Res("BorderStrongBrush") ?? Brushes.Gray;
        tile.MouseLeave += (_, _) => tile.BorderBrush = Res("BorderBrush") ?? Brushes.Gray;
        tile.MouseLeftButtonUp += (_, _) => AddTool();
        return tile;
    }

    private Border BuildEmptyTile()
    {
        var tile = new Border
        {
            Margin = new Thickness(5),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(0),
        };
        var grid = new Grid();
        grid.Children.Add(new Rectangle
        {
            Stroke = Res("BorderBrush") ?? Brushes.Gray,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 3 },
            RadiusX = 12,
            RadiusY = 12,
            IsHitTestVisible = false,
        });
        grid.Children.Add(new TextBlock
        {
            Text = "空",
            FontSize = 11,
            Opacity = 0.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Res("TextTertiaryBrush") ?? Brushes.Gray,
        });
        tile.Child = grid;
        return tile;
    }

    // =========================================================
    //  点 / 双击 / 右键
    // =========================================================

    private void Tile_PressDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border tile || tile.Tag is not string toolId) return;
        _dragTile = tile;
        _dragId = toolId;
        _origin = e.GetPosition(TileHost);
        _dragging = false;

        _hold?.Stop();
        _hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _hold.Tick += (_, _) =>
        {
            _hold?.Stop();
            if (_dragTile == null) return;
            _dragging = true;
            BeginDrag();
        };
        _hold.Start();
    }

    private void BeginDrag()
    {
        if (_dragTile == null) return;
        var tile = _dragTile;

        var pos = tile.TranslatePoint(new Point(0, 0), TileHost);
        var size = new Size(tile.ActualWidth, tile.ActualHeight);
        var p = Mouse.GetPosition(TileHost);
        _grab = new Point(p.X - pos.X, p.Y - pos.Y);

        if (tile.Parent is Panel parent) parent.Children.Remove(tile);
        tile.Width = size.Width;
        tile.Height = size.Height;
        tile.HorizontalAlignment = HorizontalAlignment.Left;
        tile.VerticalAlignment = VerticalAlignment.Top;
        tile.Margin = new Thickness(0);
        tile.IsHitTestVisible = false;
        tile.Opacity = 0.92;
        tile.BorderBrush = Res("BrandBrush") ?? Brushes.Green;
        DragLayer.Height = Math.Max(DragLayer.ActualHeight, size.Height * 3);
        Canvas.SetLeft(tile, pos.X);
        Canvas.SetTop(tile, pos.Y);
        DragLayer.Children.Add(tile);
        Panel.SetZIndex(tile, 50);
        TbHint.Text = "松手放到想放的位置。";
    }

    private void Tile_DragMove(object sender, MouseEventArgs e)
    {
        if (_dragTile == null) return;
        var p = e.GetPosition(TileHost);

        if (!_dragging)
        {
            // 手抖不算拖：移动超过 6px 就认为「他是想点一下」
            if (Math.Abs(p.X - _origin.X) > 6 || Math.Abs(p.Y - _origin.Y) > 6) _hold?.Stop();
            return;
        }

        Canvas.SetLeft(_dragTile, p.X - _grab.X);
        Canvas.SetTop(_dragTile, p.Y - _grab.Y);
        e.Handled = true;
    }

    private void Tile_PressUp(object sender, MouseButtonEventArgs e)
    {
        _hold?.Stop();

        if (_dragTile == null)
        {
            // 走到这里说明 MouseLeftButtonDown 没落在格子上（比如点在图标里的 Image 上）
            if (sender is Border b && b.Tag is string id) OpenConfig(id);
            return;
        }

        var tile = _dragTile;
        string toolId = _dragId;
        bool wasDragging = _dragging;
        _dragTile = null;
        _dragId = "";
        _dragging = false;

        if (!wasDragging)
        {
            tile.BorderBrush = Res("BorderBrush") ?? Brushes.Gray;
            OpenConfig(toolId);
            return;
        }

        // ---- 落点 ----
        var drop = e.GetPosition(TileHost);
        var (page, slot) = SlotAt(drop);

        tile.IsHitTestVisible = true;
        if (tile.Parent is Panel p2) p2.Children.Remove(tile);

        if (page >= 0)
        {
            var pages = Pages();
            var moved = ToolPages.Insert(pages, toolId, page, slot);
            if (moved != null)
            {
                Store(moved);
                _page = Math.Clamp(page, 0, Math.Max(0, ToolPages.Repack(moved).Count - 1));
                LogService.LogEvent($"托管配置：{toolId} → 第 {page + 1} 页第 {slot + 1} 格");
            }
        }

        TbHint.Text = DefaultHint;
        Render(animate: true);
    }

    private (int Page, int Slot) SlotAt(Point p)
    {
        var pages = Pages();
        if (pages.Count == 0) return (-1, -1);
        double w = ViewWidth;
        double h = TileClip?.ActualHeight ?? 0;
        if (w <= 0 || h <= 0) return (-1, -1);

        int page = _page + (int)Math.Floor(p.X / w);
        page = Math.Clamp(page, 0, pages.Count - 1);
        double x = p.X - (page - _page) * w;
        double cellW = w / Columns;
        double cellH = h / Rows;
        if (cellW <= 0 || cellH <= 0) return (-1, -1);
        int col = Math.Clamp((int)(x / cellW), 0, Columns - 1);
        int row = Math.Clamp((int)(p.Y / cellH), 0, Rows - 1);
        return (page, row * Columns + col);
    }

    private void Tile_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border tile || tile.Tag is not string toolId) return;
        e.Handled = true;

        var profile = ProfileRegistry.Get(toolId);
        var entry = EntryOf(toolId);
        bool onDuty = entry == null || entry.Enabled;

        var menu = new ContextMenu();

        var head = new MenuItem { Header = profile.DisplayName, IsEnabled = false };
        menu.Items.Add(head);
        menu.Items.Add(new Separator());

        menu.Items.Add(Item("改启动设置…", () => OpenConfig(toolId)));
        menu.Items.Add(Item("现在就跑一次", () => LaunchNow(toolId)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(onDuty ? "今晚不跑它" : "今晚跑它", () => ToggleDuty(toolId)));
        menu.Items.Add(Item("移到最后", () => MoveToEnd(toolId)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("从桌面上移除…", () => RemoveTool(toolId)));

        menu.PlacementTarget = tile;
        menu.IsOpen = true;
    }

    private static MenuItem Item(string text, Action act)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => act();
        return item;
    }

    private void OpenConfig(string toolId)
    {
        try
        {
            var w = new ToolLaunchWindow(ProfileRegistry.Get(toolId)) { Owner = this };
            w.ShowDialog();
            if (w.Deleted)
            {
                Store(ToolPages.Remove(Pages(), toolId));
            }
            if (w.Changed || w.Deleted) Render(animate: false);
        }
        catch (Exception ex)
        {
            AppDialog.Show("打不开这个工具的设置，详情见日志。\n" + ex.Message,
                kind: AppDialog.Kind.Warning, title: "托管配置", primary: "知道了", owner: this);
        }
    }

    private void LaunchNow(string toolId)
    {
        var profile = ProfileRegistry.Get(toolId);
        if (string.IsNullOrWhiteSpace(profile.ExecutablePath) || !System.IO.File.Exists(profile.ExecutablePath))
        {
            var go = AppDialog.Show($"「{profile.DisplayName}」还没找到主程序。现在去配一次？",
                kind: AppDialog.Kind.Question, title: "现在就跑", primary: "去配", secondary: "算了", owner: this);
            if (go) OpenConfig(toolId);
            return;
        }

        try
        {
            string args = ToolRunner.BuildArguments(profile, null);
            var p = ToolRunner.StartProcess(profile, args);
            if (p == null)
            {
                AppDialog.Show("没能启动，可能是路径不对，或者程序需要管理员权限。",
                    kind: AppDialog.Kind.Warning, title: "现在就跑", primary: "知道了", owner: this);
                return;
            }
            LogService.LogEvent($"托管配置里直接启动 {toolId}：{profile.ExecutablePath} {args}");
            TbHint.Text = $"已经拉起「{profile.DisplayName}」了。";
        }
        catch (Exception ex)
        {
            AppDialog.Show("启动失败：" + ex.Message,
                kind: AppDialog.Kind.Warning, title: "现在就跑", primary: "知道了", owner: this);
        }
    }

    private void ToggleDuty(string toolId)
    {
        var entry = EntryOf(toolId);
        if (entry == null)
        {
            // 有档案但没上桌：加进队列并让它跑
            var list = AppState.Settings.Queue ?? new List<QueueEntry>();
            list.Add(new QueueEntry { ToolId = toolId, Enabled = true });
            AppState.Settings.Queue = list;
        }
        else entry.Enabled = !entry.Enabled;

        AppState.SaveSettings();
        Changed = true;
        LogService.LogEvent($"托管配置：{toolId} 今晚{(entry == null || entry.Enabled ? "跑" : "不跑")}");
        Render(animate: false);
    }

    private void MoveToEnd(string toolId)
    {
        var pages = Pages();
        var flat = ToolPages.Flatten(pages);
        flat.RemoveAll(x => string.Equals(x, toolId, StringComparison.OrdinalIgnoreCase));
        flat.Add(toolId);
        Store(ToolPages.Repack(new List<List<string>> { flat }));
        Render(animate: true);
    }

    private void RemoveTool(string toolId)
    {
        if (ProfileRegistry.IsBuiltIn(toolId))
        {
            AppDialog.Show("内置工具不能移除。想让它今晚不跑的话，右键选「今晚不跑它」。",
                kind: AppDialog.Kind.Info, title: "移除工具", primary: "知道了", owner: this);
            return;
        }

        var profile = ProfileRegistry.Get(toolId);
        bool ok = AppDialog.Show($"从桌面上移除「{profile.DisplayName}」？",
            "这个工具在管家里的档案、队列里的那一项、以及它的图标缓存都会一起清掉。\n它自己的程序和配置不受影响，装在哪还在哪。",
            AppDialog.Kind.Question, "移除工具", "移除", "算了", owner: this);
        if (!ok) return;

        if (!ProfileRegistry.Remove(toolId))
        {
            AppDialog.Show("没能移除它——内置工具不允许移除。",
                kind: AppDialog.Kind.Warning, title: "移除工具", primary: "知道了", owner: this);
            return;
        }

        var pages = Pages();
        var flat = ToolPages.Flatten(pages);
        flat.RemoveAll(x => string.Equals(x, toolId, StringComparison.OrdinalIgnoreCase));
        Store(ToolPages.Repack(new List<List<string>> { flat }));
        AppState.PublishLog($"✓ 已移除工具：{profile.DisplayName}");
        Render(animate: true);
    }

    private void AddTool()
    {
        try
        {
            var dlg = new CustomToolWindow { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Result == null) return;

            string id = dlg.Result.ToolId;
            var pages = Pages();
            var flat = ToolPages.Flatten(pages);
            if (!flat.Any(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase)))
                flat.Add(id);
            var packed = ToolPages.Repack(new List<List<string>> { flat });
            Store(packed);
            _page = Math.Max(0, ToolPages.Locate(packed, id).Page);
            Changed = true;
            Render(animate: true);
        }
        catch (Exception ex)
        {
            AppDialog.Show("添加失败：" + ex.Message,
                kind: AppDialog.Kind.Warning, title: "添加工具", primary: "知道了", owner: this);
        }
    }

    // =========================================================
    //  页码点 / 翻页
    // =========================================================

    private void RenderDots()
    {
        int count = _views.Count;
        PageDotsHost.Children.Clear();
        _dots.Clear();
        PageDotsHost.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (count <= 1) return;

        for (int i = 0; i < count; i++)
        {
            var dot = new Border
            {
                Width = i == _page ? 16 : 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(3, 0, 3, 0),
                Background = i == _page ? (Res("BrandBrush") ?? Brushes.Green) : (Res("BorderStrongBrush") ?? Brushes.Gray),
                Cursor = Cursors.Hand,
                Tag = i,
                ToolTip = $"第 {i + 1} 页",
            };
            dot.MouseLeftButtonUp += (_, _) =>
            {
                if (dot.Tag is int idx) GoPage(idx);
            };
            PageDotsHost.Children.Add(dot);
            _dots.Add(dot);
        }
    }

    private void UpdateDots()
    {
        for (int i = 0; i < _dots.Count; i++)
        {
            _dots[i].Background = i == _page ? (Res("BrandBrush") ?? Brushes.Green) : (Res("BorderStrongBrush") ?? Brushes.Gray);
            double target = i == _page ? 16 : 6;
            _dots[i].BeginAnimation(FrameworkElement.WidthProperty,
                new DoubleAnimation(_dots[i].Width, target, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.HoldEnd,
                });
        }
    }

    private void TileClip_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_views.Count == 0) return;
        PlacePages(animate: false);
    }

    private void TileClip_ManipulationStarting(object sender, ManipulationStartingEventArgs e)
    {
        e.ManipulationContainer = TileClip;
        e.Mode = ManipulationModes.TranslateX;
    }

    private void TileClip_ManipulationDelta(object sender, ManipulationDeltaEventArgs e) => e.Handled = true;

    private void TileClip_ManipulationCompleted(object sender, ManipulationCompletedEventArgs e)
    {
        double dx = e.TotalManipulation.Translation.X;
        double threshold = Math.Max(50, ViewWidth * 0.16);
        if (dx < -threshold) GoPage(_page + 1);
        else if (dx > threshold) GoPage(_page - 1);
    }

    // =========================================================
    //  窗口按钮
    // =========================================================

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnAdd_Click(object sender, RoutedEventArgs e) => AddTool();

    private void BtnQueue_Click(object sender, RoutedEventArgs e)
    {
        // 队列那套（按日计划、依赖、跑完做什么）留在主窗，不在这张桌子上重复一遍
        if (Owner is MainWindow mw) mw.RevealQueueFromOutside();
        Close();
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ArenaDuel.Data;
using ArenaDuel.Gameplay;
using ArenaDuel.Render;

namespace ArenaDuel;

public partial class MainWindow : Window
{
    private readonly GameController _game;
    private readonly DispatcherTimer _timer;
    private readonly CombatVfx _vfx = new();
    private readonly HashSet<Key> _keys = [];
    private bool _prevQ, _prevE, _prevC;
    private Point _mouseCanvas = new(480, 320);
    private double _viewScale = 1;
    private double _viewOx;
    private double _viewOy;
    private double _animT;
    private double _ghostHpA = -1;
    private double _ghostHpB = -1;
    private string? _lastPhase;
    private float _aimWorldX;
    private float _aimWorldY;
    private bool _hasAim;
    private int _aimPreviewSkill = -1; // 1=Q 2=E 3=C while held for preview

    public MainWindow()
    {
        InitializeComponent();
        _game = new GameController(Dispatcher);
        _game.Changed += OnGameChanged;
        _timer = new DispatcherTimer { Interval = GameController.TickInterval };
        _timer.Tick += (_, _) =>
        {
            _animT += GameController.TickInterval.TotalSeconds;
            PushInput();
            _game.TickFrame();
            if (_game.Phase == UiPhase.Battle && _game.Snapshot != null)
                RenderBattle(_game.Snapshot, (float)GameController.TickInterval.TotalSeconds);
        };
        Loaded += async (_, _) =>
        {
            await _game.StartAsync();
            _timer.Start();
        };
        Closed += async (_, _) =>
        {
            _timer.Stop();
            await _game.DisposeAsync();
        };
    }

    private void OnGameChanged()
    {
        StatusText.Text = _game.StatusText;
        LobbyPanel.Visibility = _game.Phase == UiPhase.Lobby ? Visibility.Visible : Visibility.Collapsed;
        PickPanel.Visibility = _game.Phase == UiPhase.Pick ? Visibility.Visible : Visibility.Collapsed;
        BattlePanel.Visibility = _game.Phase == UiPhase.Battle ? Visibility.Visible : Visibility.Collapsed;
        ResultPanel.Visibility = _game.Phase == UiPhase.Result ? Visibility.Visible : Visibility.Collapsed;

        LobbyHint.Text = _game.IsLocalMode
            ? "本地试炼已就绪 — 点上方按钮进入立绘选人。"
            : _game.IsPausedForReconnect
                ? "重连中，请保持 LanHub 与本程序开启。"
                : (_game.IsHost
                    ? "主机就位。双方都要从游戏库打开本程序后才会进入盲选（仅进房不够）。"
                    : "等待双方游戏进程 Hello 配对…");

        if (_game.Phase == UiPhase.Pick)
            RebuildPickStrip();

        if (_game.Phase == UiPhase.Battle && _lastPhase != "battle")
        {
            _vfx.Reset();
            _ghostHpA = _ghostHpB = -1;
        }

        if (_game.Phase == UiPhase.Result && _game.Result != null)
        {
            var r = _game.Result;
            var me = _game.LocalPlayerId;
            if (r.WinnerPlayerId == null)
            {
                ResultTitle.Text = "平局";
                ResultDetail.Text = $"双雄未分胜负\nHP  {r.HpA:0}  vs  {r.HpB:0}";
            }
            else if (r.WinnerPlayerId == me)
            {
                ResultTitle.Text = "胜利";
                ResultDetail.Text = $"击败对手 · {r.Reason}\nHP  {r.HpA:0}  vs  {r.HpB:0}";
            }
            else
            {
                ResultTitle.Text = "落败";
                ResultDetail.Text = $"{r.Reason}\nHP  {r.HpA:0}  vs  {r.HpB:0}";
            }
        }

        BtnReady.Content = _game.IAmReady ? "取消准备" : "准备就绪";
        _lastPhase = _game.Phase.ToString().ToLowerInvariant();
    }

    private void RebuildPickStrip()
    {
        CharStrip.Children.Clear();
        foreach (var c in _game.Characters)
        {
            var id = c.Id;
            CharStrip.Children.Add(FighterArt.BuildSelectCard(c, id == _game.SelectedCharacterId, () =>
            {
                if (!_game.IAmReady) _game.SelectCharacter(id);
            }));
        }
    }

    private void RenderBattle(MatchSnapshot snap, float dt)
    {
        var arena = GameData.GetArena(snap.ArenaId);
        ArenaLabel.Text = "◆ " + arena.Name;
        var mins = (int)snap.TimeLeft / 60;
        var secs = (int)snap.TimeLeft % 60;
        TimerText.Text = $"{mins}:{secs:00}";

        var meId = _game.LocalPlayerId;
        var left = snap.A;
        var right = snap.B;
        // Always show slot A on left, B on right (arcade convention)
        UpdateFighterHud(left, PortraitA, HudNameA, HudHpTextA, HpFillA, HpGhostA, MpFillA, ref _ghostHpA, mirrorBar: false);
        UpdateFighterHud(right, PortraitB, HudNameB, HudHpTextB, HpFillB, HpGhostB, MpFillB, ref _ghostHpB, mirrorBar: true);

        var me = left.PlayerId == meId ? left : right;
        var chMe = GameData.GetCharacter(me.CharacterId);
        TxtQ.Text = me.CdSkill1 > 0 ? $"{me.CdSkill1:0.0}s" : chMe.Skill1.Name;
        TxtE.Text = me.CdSkill2 > 0 ? $"{me.CdSkill2:0.0}s" : chMe.Skill2.Name;
        TxtC.Text = me.CdUltimate > 0 ? $"{me.CdUltimate:0.0}s" : chMe.Ultimate.Name;
        CdQ.Opacity = me.CdSkill1 > 0 ? 0.55 : 1;
        CdE.Opacity = me.CdSkill2 > 0 ? 0.55 : 1;
        CdC.Opacity = me.CdUltimate > 0 ? 0.55 : 1;

        _vfx.Observe(snap, dt);

        BattleCanvas.Children.Clear();
        LayoutArena(arena.Width, arena.Height);
        var shakeX = _vfx.ShakeX * _viewScale;
        var shakeY = _vfx.ShakeY * _viewScale;

        Point ToScreen(double x, double y) =>
            new(_viewOx + x * _viewScale + shakeX, _viewOy + y * _viewScale + shakeY);

        DrawArenaBackdrop(arena, shakeX, shakeY);

        if (arena.Bridge != null)
        {
            // void under bridge
            var voidRect = new Rectangle
            {
                Width = arena.Width * _viewScale,
                Height = arena.Height * _viewScale,
                Fill = new SolidColorBrush(Color.FromArgb(180, 4, 8, 18))
            };
            Canvas.SetLeft(voidRect, _viewOx + shakeX);
            Canvas.SetTop(voidRect, _viewOy + shakeY);
            BattleCanvas.Children.Add(voidRect);
            BattleCanvas.Children.Add(MakeDecorBlock(arena.Bridge.X, arena.Bridge.Y, arena.Bridge.W, arena.Bridge.H,
                arena.Floor, shakeX, shakeY, 1.0));
            // bridge edge glow
            BattleCanvas.Children.Add(MakeDecorBlock(arena.Bridge.X, arena.Bridge.Y, arena.Bridge.W, 6,
                arena.Accent, shakeX, shakeY, 0.55));
            BattleCanvas.Children.Add(MakeDecorBlock(arena.Bridge.X, arena.Bridge.Y + arena.Bridge.H - 6, arena.Bridge.W, 6,
                arena.Accent, shakeX, shakeY, 0.55));
        }

        foreach (var o in arena.Obstacles)
            MakeSolidObstacle(o.X, o.Y, o.W, o.H, arena.Accent, shakeX, shakeY);

        foreach (var prop in arena.Props)
            DrawProp(prop, arena, shakeX, shakeY);
        // projectiles / aoe with trails
        foreach (var p in snap.Projectiles)
        {
            var pt = ToScreen(p.X, p.Y);
            if (p.IsAoePulse)
            {
                var ring = new Ellipse
                {
                    Width = Math.Max(8, p.Radius * 2 * _viewScale),
                    Height = Math.Max(8, p.Radius * 2 * _viewScale),
                    Stroke = new SolidColorBrush(Color.FromArgb(180, 255, 200, 60)),
                    StrokeThickness = 3 * _viewScale,
                    Fill = new SolidColorBrush(Color.FromArgb(50, 255, 180, 40))
                };
                Canvas.SetLeft(ring, pt.X - ring.Width / 2);
                Canvas.SetTop(ring, pt.Y - ring.Height / 2);
                BattleCanvas.Children.Add(ring);
            }
            else
            {
                var core = new Ellipse
                {
                    Width = Math.Max(6, p.Radius * 2.4 * _viewScale),
                    Height = Math.Max(6, p.Radius * 2.4 * _viewScale),
                    Fill = new RadialGradientBrush(Colors.White, Color.FromRgb(0xFF, 0xC0, 0x40))
                };
                Canvas.SetLeft(core, pt.X - core.Width / 2);
                Canvas.SetTop(core, pt.Y - core.Height / 2);
                BattleCanvas.Children.Add(core);
                var trail = new Ellipse
                {
                    Width = core.Width * 1.6,
                    Height = core.Height * 0.7,
                    Fill = new SolidColorBrush(Color.FromArgb(80, 255, 220, 120))
                };
                Canvas.SetLeft(trail, pt.X - trail.Width / 2 - p.Vx * 0.02 * _viewScale);
                Canvas.SetTop(trail, pt.Y - trail.Height / 2);
                BattleCanvas.Children.Add(trail);
            }
        }

        DrawFighterOnField(snap.A, snap.B, ToScreen);
        DrawFighterOnField(snap.B, snap.A, ToScreen);
        DrawAimAssist(snap, me, chMe, ToScreen);
        _vfx.Draw(BattleCanvas, ToScreen, _viewScale);
    }

    private void DrawAimAssist(MatchSnapshot snap, FighterState me, CharacterDef chMe, Func<double, double, Point> toScreen)
    {
        if (!_hasAim) return;
        var from = toScreen(me.X, me.Y);
        var cursor = toScreen(_aimWorldX, _aimWorldY);

        // cursor crosshair
        var cross = 8 * _viewScale;
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 220, 80)), 1.5 * _viewScale);
        BattleCanvas.Children.Add(MakeLine(cursor.X - cross, cursor.Y, cursor.X + cross, cursor.Y, pen));
        BattleCanvas.Children.Add(MakeLine(cursor.X, cursor.Y - cross, cursor.X, cursor.Y + cross, pen));

        var dx = _aimWorldX - me.X;
        var dy = _aimWorldY - me.Y;
        var dist = MathF.Sqrt(dx * dx + dy * dy);

        SkillDef? preview = _aimPreviewSkill switch
        {
            1 => chMe.Skill1,
            2 => chMe.Skill2,
            3 => chMe.Ultimate,
            _ => null
        };

        if (preview == null)
        {
            // idle aim line
            var tip = toScreen(me.X + (dist > 1e-3f ? dx / dist : me.AimX) * 80,
                me.Y + (dist > 1e-3f ? dy / dist : me.AimY) * 80);
            BattleCanvas.Children.Add(MakeLine(from.X, from.Y, tip.X, tip.Y,
                new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1.2 * _viewScale)));
            return;
        }

        var range = preview.Range > 0 ? preview.Range : 0;
        var type = preview.Type.ToLowerInvariant();

        // max range ring
        if (range > 0)
        {
            var ring = new Ellipse
            {
                Width = range * 2 * _viewScale,
                Height = range * 2 * _viewScale,
                Stroke = new SolidColorBrush(Color.FromArgb(70, 255, 210, 80)),
                StrokeThickness = 1.5 * _viewScale,
                Fill = Brushes.Transparent,
                StrokeDashArray = new DoubleCollection { 4, 3 }
            };
            var c = toScreen(me.X, me.Y);
            Canvas.SetLeft(ring, c.X - ring.Width / 2);
            Canvas.SetTop(ring, c.Y - ring.Height / 2);
            BattleCanvas.Children.Add(ring);
        }

        float tx = _aimWorldX, ty = _aimWorldY;
        if (range > 0 && dist > range)
        {
            tx = me.X + dx / dist * range;
            ty = me.Y + dy / dist * range;
        }

        if (type is "aoe" && range > 0)
        {
            var land = toScreen(tx, ty);
            var aoe = new Ellipse
            {
                Width = Math.Max(12, preview.Radius * 2 * _viewScale),
                Height = Math.Max(12, preview.Radius * 2 * _viewScale),
                Stroke = new SolidColorBrush(Color.FromArgb(200, 255, 180, 60)),
                StrokeThickness = 2 * _viewScale,
                Fill = new SolidColorBrush(Color.FromArgb(40, 255, 180, 40))
            };
            Canvas.SetLeft(aoe, land.X - aoe.Width / 2);
            Canvas.SetTop(aoe, land.Y - aoe.Height / 2);
            BattleCanvas.Children.Add(aoe);
            BattleCanvas.Children.Add(MakeLine(from.X, from.Y, land.X, land.Y,
                new Pen(new SolidColorBrush(Color.FromArgb(140, 255, 200, 80)), 2 * _viewScale)));
        }
        else if (type is "projectile" or "dash")
        {
            var tip = toScreen(tx, ty);
            BattleCanvas.Children.Add(MakeLine(from.X, from.Y, tip.X, tip.Y,
                new Pen(new SolidColorBrush(Color.FromArgb(180, 120, 220, 255)), 2.5 * _viewScale)));
            var arrow = new Ellipse
            {
                Width = 10 * _viewScale,
                Height = 10 * _viewScale,
                Fill = new SolidColorBrush(Color.FromArgb(220, 120, 220, 255))
            };
            Canvas.SetLeft(arrow, tip.X - arrow.Width / 2);
            Canvas.SetTop(arrow, tip.Y - arrow.Height / 2);
            BattleCanvas.Children.Add(arrow);
        }
        else
        {
            // buff / self
            var self = new Ellipse
            {
                Width = Math.Max(20, preview.Radius * 2 * _viewScale),
                Height = Math.Max(20, preview.Radius * 2 * _viewScale),
                Stroke = new SolidColorBrush(Color.FromArgb(160, 120, 255, 160)),
                StrokeThickness = 2 * _viewScale,
                Fill = new SolidColorBrush(Color.FromArgb(35, 120, 255, 120))
            };
            Canvas.SetLeft(self, from.X - self.Width / 2);
            Canvas.SetTop(self, from.Y - self.Height / 2);
            BattleCanvas.Children.Add(self);
        }
    }

    private static System.Windows.Shapes.Line MakeLine(double x1, double y1, double x2, double y2, Pen pen) => new()
    {
        X1 = x1,
        Y1 = y1,
        X2 = x2,
        Y2 = y2,
        Stroke = pen.Brush,
        StrokeThickness = pen.Thickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round
    };

    private void UpdateFighterHud(FighterState f, Image portrait, TextBlock name, TextBlock hpText,
        Rectangle hpFill, Rectangle hpGhost, Rectangle mpFill, ref double ghostHp, bool mirrorBar)
    {
        var ch = GameData.GetCharacter(f.CharacterId);
        portrait.Source = FighterArt.GetPortrait(ch, 128);
        name.Text = ch.Name;
        hpText.Text = $"{f.Hp:0}/{f.MaxHp:0}";

        var parent = hpFill.Parent as FrameworkElement;
        var barW = parent?.ActualWidth > 1 ? parent.ActualWidth : 220;
        var ratio = f.MaxHp <= 0 ? 0 : Math.Clamp(f.Hp / f.MaxHp, 0, 1);
        hpFill.Width = barW * ratio;

        if (ghostHp < 0) ghostHp = f.Hp;
        if (f.Hp < ghostHp) ghostHp = Math.Max(f.Hp, ghostHp - f.MaxHp * 0.012);
        else ghostHp = f.Hp;
        var ghostRatio = f.MaxHp <= 0 ? 0 : Math.Clamp(ghostHp / f.MaxHp, 0, 1);
        hpGhost.Width = barW * ghostRatio;

        var mpParent = mpFill.Parent as FrameworkElement;
        var mpW = mpParent?.ActualWidth > 1 ? mpParent.ActualWidth : 220;
        var mpRatio = f.MaxMp <= 0 ? 0 : Math.Clamp(f.Mp / f.MaxMp, 0, 1);
        mpFill.Width = mpW * mpRatio;
    }

    private void DrawFighterOnField(FighterState self, FighterState foe, Func<double, double, Point> toScreen)
    {
        var ch = GameData.GetCharacter(self.CharacterId);
        var pt = toScreen(self.X, self.Y);
        bool faceRight;
        if (self.PlayerId == _game.LocalPlayerId && _hasAim)
            faceRight = _aimWorldX >= self.X;
        else
            faceRight = foe.X >= self.X;
        var casting = self.CastingSlot >= 0 || self.CastLeft > 0;
        var hurt = self.DotLeft > 0;
        var bob = Math.Sin(_animT * 6 + (int)self.Slot) * 2.2 * _viewScale;
        FighterArt.DrawFighter(BattleCanvas, ch, pt.X, pt.Y + bob, _viewScale * 1.15, faceRight, casting, hurt, bob * 0.2);
    }

    private void DrawArenaBackdrop(ArenaDef arena, double shakeX, double shakeY)
    {
        var bgColor = (Color)ColorConverter.ConvertFromString(arena.Background)!;
        var accent = (Color)ColorConverter.ConvertFromString(arena.Accent)!;
        var floorCol = (Color)ColorConverter.ConvertFromString(
            string.IsNullOrEmpty(arena.Floor) ? arena.Background : arena.Floor)!;

        var bg = new Rectangle
        {
            Width = arena.Width * _viewScale,
            Height = arena.Height * _viewScale,
            Fill = new LinearGradientBrush(Darken(bgColor, -0.05), Darken(bgColor, 0.3), 90)
        };
        Canvas.SetLeft(bg, _viewOx + shakeX);
        Canvas.SetTop(bg, _viewOy + shakeY);
        BattleCanvas.Children.Add(bg);

        if (arena.Bridge == null)
        {
            var floorRect = new Rectangle
            {
                Width = arena.Width * _viewScale,
                Height = arena.Height * _viewScale,
                Fill = new SolidColorBrush(Color.FromArgb(55, floorCol.R, floorCol.G, floorCol.B))
            };
            Canvas.SetLeft(floorRect, _viewOx + shakeX);
            Canvas.SetTop(floorRect, _viewOy + shakeY);
            BattleCanvas.Children.Add(floorRect);

            for (var gy = 0; gy < arena.Height; gy += 64)
            {
                var line = new Rectangle
                {
                    Width = arena.Width * _viewScale,
                    Height = 1,
                    Fill = new SolidColorBrush(Color.FromArgb(22, accent.R, accent.G, accent.B))
                };
                Canvas.SetLeft(line, _viewOx + shakeX);
                Canvas.SetTop(line, _viewOy + gy * _viewScale + shakeY);
                BattleCanvas.Children.Add(line);
            }
        }

        var moon = new Ellipse
        {
            Width = 90 * _viewScale,
            Height = 90 * _viewScale,
            Fill = new RadialGradientBrush(
                Color.FromArgb(70, accent.R, accent.G, accent.B),
                Color.FromArgb(0, accent.R, accent.G, accent.B))
        };
        Canvas.SetLeft(moon, _viewOx + arena.Width * 0.75 * _viewScale + shakeX);
        Canvas.SetTop(moon, _viewOy + 28 * _viewScale + shakeY);
        BattleCanvas.Children.Add(moon);

        var count = arena.Theme switch { "alley" => 8, "ruins" => 5, "gorge" => 4, _ => 6 };
        for (var i = 0; i < count; i++)
        {
            var h = (50 + (i % 4) * 22) * _viewScale;
            var w = (28 + (i % 3) * 18) * _viewScale;
            var sil = new Rectangle
            {
                Width = w,
                Height = h,
                Fill = new SolidColorBrush(Color.FromArgb(55, 0, 0, 0))
            };
            Canvas.SetLeft(sil, _viewOx + (40 + i * (arena.Width / (count + 1f))) * _viewScale + shakeX);
            Canvas.SetTop(sil, _viewOy + arena.Height * _viewScale - h - 4 + shakeY);
            BattleCanvas.Children.Add(sil);
        }

        var frame = new Rectangle
        {
            Width = arena.Width * _viewScale,
            Height = arena.Height * _viewScale,
            Stroke = new SolidColorBrush(Color.FromArgb(140, accent.R, accent.G, accent.B)),
            StrokeThickness = 3 * _viewScale,
            Fill = Brushes.Transparent
        };
        Canvas.SetLeft(frame, _viewOx + shakeX);
        Canvas.SetTop(frame, _viewOy + shakeY);
        BattleCanvas.Children.Add(frame);
    }

    private void DrawProp(PropDef prop, ArenaDef arena, double sx, double sy)
    {
        var accent = (Color)ColorConverter.ConvertFromString(arena.Accent)!;
        switch (prop.Kind)
        {
            case "lantern":
                BattleCanvas.Children.Add(MakeDecorBlock(prop.X, prop.Y, prop.W, prop.H, "#FFB040", sx, sy, 0.95));
                var glow = new Ellipse
                {
                    Width = prop.W * 2.5 * _viewScale,
                    Height = prop.H * 2.5 * _viewScale,
                    Fill = new SolidColorBrush(Color.FromArgb(50, 255, 180, 60))
                };
                Canvas.SetLeft(glow, _viewOx + (prop.X - prop.W * 0.75) * _viewScale + sx);
                Canvas.SetTop(glow, _viewOy + (prop.Y - prop.H * 0.75) * _viewScale + sy);
                BattleCanvas.Children.Add(glow);
                break;
            case "banner":
                BattleCanvas.Children.Add(MakeDecorBlock(prop.X, prop.Y, prop.W * 0.25f, prop.H, "#5A4030", sx, sy));
                BattleCanvas.Children.Add(MakeDecorBlock(prop.X + prop.W * 0.2f, prop.Y + 8, prop.W * 0.7f, prop.H * 0.55f,
                    arena.Accent, sx, sy, 0.85));
                break;
            case "waterfall":
                BattleCanvas.Children.Add(MakeDecorBlock(prop.X, prop.Y, prop.W, prop.H, "#80E0FF", sx, sy, 0.35));
                break;
            case "crystal":
            case "pillar":
                BattleCanvas.Children.Add(MakeDecorBlock(prop.X, prop.Y, prop.W, prop.H, arena.Accent, sx, sy, 0.5));
                break;
            default:
                BattleCanvas.Children.Add(MakeDecorBlock(prop.X, prop.Y, prop.W, prop.H,
                    $"#{Darken(accent, 0.4).R:X2}{Darken(accent, 0.4).G:X2}{Darken(accent, 0.4).B:X2}", sx, sy, 0.4));
                break;
        }
    }

    private void MakeSolidObstacle(float x, float y, float w, float h, string color, double sx, double sy)
    {
        var c = (Color)ColorConverter.ConvertFromString(color)!;
        var top = Darken(c, -0.15);
        var bot = Darken(c, 0.45);
        var r = new Rectangle
        {
            Width = w * _viewScale,
            Height = h * _viewScale,
            Fill = new LinearGradientBrush(top, bot, 90),
            Stroke = new SolidColorBrush(Color.FromArgb(200, 0x20, 0x10, 0x08)),
            StrokeThickness = 2
        };
        Canvas.SetLeft(r, _viewOx + x * _viewScale + sx);
        Canvas.SetTop(r, _viewOy + y * _viewScale + sy);
        BattleCanvas.Children.Add(r);
        var rim = new Rectangle
        {
            Width = w * _viewScale,
            Height = 4 * _viewScale,
            Fill = new SolidColorBrush(Color.FromArgb(120, 255, 220, 160))
        };
        Canvas.SetLeft(rim, _viewOx + x * _viewScale + sx);
        Canvas.SetTop(rim, _viewOy + y * _viewScale + sy);
        BattleCanvas.Children.Add(rim);
    }

    private Rectangle MakeDecorBlock(float x, float y, float w, float h, string color, double sx, double sy, double opacity = 0.9)
    {
        var c = (Color)ColorConverter.ConvertFromString(color)!;
        c.A = (byte)(opacity * 255);
        var edge = Darken(c, 0.35);
        var r = new Rectangle
        {
            Width = w * _viewScale,
            Height = h * _viewScale,
            Fill = new LinearGradientBrush(c, edge, 90),
            Stroke = new SolidColorBrush(Color.FromArgb(100, 0xE0, 0xB0, 0x40)),
            StrokeThickness = 1
        };
        Canvas.SetLeft(r, _viewOx + x * _viewScale + sx);
        Canvas.SetTop(r, _viewOy + y * _viewScale + sy);
        return r;
    }

    private static Color Darken(Color c, double amount)
    {
        double f = 1 - amount;
        if (amount < 0) f = 1 - amount; // lighten when negative... wait
        if (amount < 0)
        {
            var t = -amount;
            return Color.FromRgb(
                (byte)Math.Min(255, c.R + (255 - c.R) * t),
                (byte)Math.Min(255, c.G + (255 - c.G) * t),
                (byte)Math.Min(255, c.B + (255 - c.B) * t));
        }
        return Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));
    }

    private void LayoutArena(float aw, float ah)
    {
        var cw = BattleCanvas.ActualWidth;
        var ch = BattleCanvas.ActualHeight;
        if (cw < 1 || ch < 1) return;
        _viewScale = Math.Min(cw / aw, ch / ah);
        _viewOx = (cw - aw * _viewScale) / 2;
        _viewOy = (ch - ah * _viewScale) / 2;
    }

    private void PushInput()
    {
        if (_game.Phase != UiPhase.Battle || _game.Snapshot == null) return;
        float mx = 0, my = 0;
        if (_keys.Contains(Key.A) || _keys.Contains(Key.Left)) mx -= 1;
        if (_keys.Contains(Key.D) || _keys.Contains(Key.Right)) mx += 1;
        if (_keys.Contains(Key.W) || _keys.Contains(Key.Up)) my -= 1;
        if (_keys.Contains(Key.S) || _keys.Contains(Key.Down)) my += 1;

        // Live cursor → world aim (prefer canvas-relative even if slightly outside)
        _mouseCanvas = Mouse.GetPosition(BattleCanvas);

        var me = _game.Snapshot.A.PlayerId == _game.LocalPlayerId ? _game.Snapshot.A : _game.Snapshot.B;
        var world = CanvasToWorld(_mouseCanvas);
        _aimWorldX = (float)world.X;
        _aimWorldY = (float)world.Y;
        _hasAim = true;

        var q = _keys.Contains(Key.Q);
        var e = _keys.Contains(Key.E);
        var c = _keys.Contains(Key.C);
        var skill1Edge = q && !_prevQ;
        var skill2Edge = e && !_prevE;
        var ultEdge = c && !_prevC;
        _prevQ = q;
        _prevE = e;
        _prevC = c;

        _aimPreviewSkill = q ? 1 : e ? 2 : c ? 3 : -1;

        var aimX = _aimWorldX - me.X;
        var aimY = _aimWorldY - me.Y;

        _game.SetLocalInput(new InputState
        {
            MoveX = mx,
            MoveY = my,
            Attack = _keys.Contains(Key.J) || Mouse.LeftButton == MouseButtonState.Pressed,
            Skill1 = skill1Edge,
            Skill2 = skill2Edge,
            Ultimate = ultEdge,
            AimX = aimX,
            AimY = aimY,
            WorldAimX = _aimWorldX,
            WorldAimY = _aimWorldY,
            HasWorldAim = true
        });
    }

    private Point CanvasToWorld(Point canvasPt)
    {
        if (_viewScale < 1e-6) return canvasPt;
        return new Point((canvasPt.X - _viewOx) / _viewScale, (canvasPt.Y - _viewOy) / _viewScale);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e) => _keys.Add(e.Key);
    private void Window_KeyUp(object sender, KeyEventArgs e) => _keys.Remove(e.Key);
    private void BattleCanvas_MouseMove(object sender, MouseEventArgs e) => _mouseCanvas = e.GetPosition(BattleCanvas);
    private void BattleCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _mouseCanvas = e.GetPosition(BattleCanvas);
    private void BtnLocal_Click(object sender, RoutedEventArgs e) => _game.StartLocalMatchSetup();
    private async void BtnReady_Click(object sender, RoutedEventArgs e) => await _game.ToggleReadyAsync();
    private void BtnRematch_Click(object sender, RoutedEventArgs e) => _game.ReturnToPick();
}

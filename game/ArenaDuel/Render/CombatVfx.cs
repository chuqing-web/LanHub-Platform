using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ArenaDuel.Gameplay;

namespace ArenaDuel.Render;

public sealed class VfxParticle
{
    public double X;
    public double Y;
    public double Vx;
    public double Vy;
    public double Life;
    public double MaxLife;
    public double Size;
    public Color Color;
    public bool Ring;
}

public sealed class FloatText
{
    public double X;
    public double Y;
    public double Vy;
    public double Life;
    public string Text = "";
    public Color Color;
}

/// <summary>Arcade-style hit sparks, rings, and floating damage.</summary>
public sealed class CombatVfx
{
    private readonly List<VfxParticle> _parts = [];
    private readonly List<FloatText> _texts = [];
    private float _prevHpA = -1;
    private float _prevHpB = -1;
    private int _prevProjCount;
    public double ShakeX { get; private set; }
    public double ShakeY { get; private set; }
    private double _shake;

    public void Reset()
    {
        _parts.Clear();
        _texts.Clear();
        _prevHpA = _prevHpB = -1;
        _shake = 0;
    }

    public void Observe(MatchSnapshot snap, float dt)
    {
        if (_prevHpA < 0)
        {
            _prevHpA = snap.A.Hp;
            _prevHpB = snap.B.Hp;
            _prevProjCount = snap.Projectiles.Count;
            return;
        }

        if (snap.A.Hp < _prevHpA - 0.5f)
            OnHit(snap.A.X, snap.A.Y, _prevHpA - snap.A.Hp, fromRight: true);
        if (snap.B.Hp < _prevHpB - 0.5f)
            OnHit(snap.B.X, snap.B.Y, _prevHpB - snap.B.Hp, fromRight: false);

        if (snap.Projectiles.Count > _prevProjCount)
        {
            foreach (var p in snap.Projectiles.TakeLast(snap.Projectiles.Count - _prevProjCount))
                Burst(p.X, p.Y, p.IsAoePulse ? 18 : 8, p.IsAoePulse
                    ? Color.FromRgb(0xFF, 0xC0, 0x40)
                    : Color.FromRgb(0xFF, 0xF0, 0xA0), p.IsAoePulse);
        }

        _prevHpA = snap.A.Hp;
        _prevHpB = snap.B.Hp;
        _prevProjCount = snap.Projectiles.Count;

        _shake = Math.Max(0, _shake - dt * 18);
        if (_shake > 0)
        {
            ShakeX = (Random.Shared.NextDouble() - 0.5) * _shake;
            ShakeY = (Random.Shared.NextDouble() - 0.5) * _shake;
        }
        else ShakeX = ShakeY = 0;

        for (var i = _parts.Count - 1; i >= 0; i--)
        {
            var p = _parts[i];
            p.Life -= dt;
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            p.Vy += 80 * dt;
            if (p.Life <= 0) _parts.RemoveAt(i);
        }
        for (var i = _texts.Count - 1; i >= 0; i--)
        {
            var t = _texts[i];
            t.Life -= dt;
            t.Y += t.Vy * dt;
            if (t.Life <= 0) _texts.RemoveAt(i);
        }
    }

    public void Draw(Canvas canvas, Func<double, double, Point> toScreen, double scale)
    {
        foreach (var p in _parts)
        {
            var pt = toScreen(p.X, p.Y);
            var alpha = (byte)(255 * Math.Clamp(p.Life / p.MaxLife, 0, 1));
            var c = Color.FromArgb(alpha, p.Color.R, p.Color.G, p.Color.B);
            if (p.Ring)
            {
                var el = new Ellipse
                {
                    Width = p.Size * scale * (2 - p.Life / p.MaxLife),
                    Height = p.Size * scale * (2 - p.Life / p.MaxLife),
                    Stroke = new SolidColorBrush(c),
                    StrokeThickness = 2.5 * scale,
                    Fill = Brushes.Transparent
                };
                Canvas.SetLeft(el, pt.X - el.Width / 2);
                Canvas.SetTop(el, pt.Y - el.Height / 2);
                canvas.Children.Add(el);
            }
            else
            {
                var el = new Ellipse
                {
                    Width = p.Size * scale,
                    Height = p.Size * scale,
                    Fill = new SolidColorBrush(c)
                };
                Canvas.SetLeft(el, pt.X - el.Width / 2);
                Canvas.SetTop(el, pt.Y - el.Height / 2);
                canvas.Children.Add(el);
            }
        }

        foreach (var t in _texts)
        {
            var pt = toScreen(t.X, t.Y);
            var alpha = (byte)(255 * Math.Clamp(t.Life / 0.8, 0, 1));
            var tb = new TextBlock
            {
                Text = t.Text,
                FontSize = 18 * scale,
                FontWeight = FontWeights.Black,
                FontFamily = new FontFamily("Impact, Microsoft YaHei UI"),
                Foreground = new SolidColorBrush(Color.FromArgb(alpha, t.Color.R, t.Color.G, t.Color.B)),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black, BlurRadius = 2, ShadowDepth = 1, Opacity = 0.9
                }
            };
            Canvas.SetLeft(tb, pt.X);
            Canvas.SetTop(tb, pt.Y);
            canvas.Children.Add(tb);
        }
    }

    private void OnHit(float x, float y, float dmg, bool fromRight)
    {
        _texts.Add(new FloatText
        {
            X = x,
            Y = y - 40,
            Vy = -50,
            Life = 0.85,
            Text = ((int)Math.Round(dmg)).ToString(),
            Color = dmg > 80 ? Color.FromRgb(0xFF, 0x40, 0x40) : Color.FromRgb(0xFF, 0xE0, 0x60)
        });
        Burst(x, y, 14, Color.FromRgb(0xFF, 0x60, 0x30), ring: true);
        _shake = Math.Min(14, 4 + dmg * 0.04);
    }

    private void Burst(float x, float y, int n, Color color, bool ring)
    {
        if (ring)
        {
            _parts.Add(new VfxParticle
            {
                X = x, Y = y, Life = 0.35, MaxLife = 0.35, Size = 28, Color = color, Ring = true
            });
        }
        for (var i = 0; i < n; i++)
        {
            var a = Random.Shared.NextDouble() * Math.PI * 2;
            var sp = 60 + Random.Shared.NextDouble() * 160;
            _parts.Add(new VfxParticle
            {
                X = x,
                Y = y,
                Vx = Math.Cos(a) * sp,
                Vy = Math.Sin(a) * sp - 40,
                Life = 0.25 + Random.Shared.NextDouble() * 0.35,
                MaxLife = 0.5,
                Size = 3 + Random.Shared.NextDouble() * 5,
                Color = color
            });
        }
    }
}

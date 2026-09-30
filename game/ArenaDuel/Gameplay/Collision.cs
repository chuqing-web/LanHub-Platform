using ArenaDuel.Data;

namespace ArenaDuel.Gameplay;

/// <summary>Shared 2D collision helpers for movement and skill hits.</summary>
public static class Collision
{
    public static bool CircleAabb(float cx, float cy, float cr, float x, float y, float w, float h)
    {
        var nx = Math.Clamp(cx, x, x + w);
        var ny = Math.Clamp(cy, y, y + h);
        var dx = cx - nx;
        var dy = cy - ny;
        return dx * dx + dy * dy < cr * cr;
    }

    public static bool CircleCircle(float ax, float ay, float ar, float bx, float by, float br)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var r = ar + br;
        return dx * dx + dy * dy <= r * r;
    }

    /// <summary>Closest distance from point to segment AB.</summary>
    public static float DistPointSegment(float px, float py, float ax, float ay, float bx, float by)
    {
        var abx = bx - ax;
        var aby = by - ay;
        var apx = px - ax;
        var apy = py - ay;
        var ab2 = abx * abx + aby * aby;
        if (ab2 < 1e-8f) return MathF.Sqrt(apx * apx + apy * apy);
        var t = Math.Clamp((apx * abx + apy * aby) / ab2, 0f, 1f);
        var qx = ax + abx * t;
        var qy = ay + aby * t;
        var dx = px - qx;
        var dy = py - qy;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Swept circle (from→to with radius) vs static circle.</summary>
    public static bool SweepCircleCircle(
        float x0, float y0, float x1, float y1, float pr,
        float tx, float ty, float tr)
    {
        return DistPointSegment(tx, ty, x0, y0, x1, y1) <= pr + tr;
    }

    /// <summary>Does swept circle hit any AABB obstacle?</summary>
    public static bool SweepHitsObstacles(
        float x0, float y0, float x1, float y1, float radius,
        IReadOnlyList<RectDef> obstacles, int steps = 8)
    {
        steps = Math.Clamp(steps, 2, 24);
        for (var i = 0; i <= steps; i++)
        {
            var t = i / (float)steps;
            var x = x0 + (x1 - x0) * t;
            var y = y0 + (y1 - y0) * t;
            foreach (var o in obstacles)
            {
                if (CircleAabb(x, y, radius, o.X, o.Y, o.W, o.H))
                    return true;
            }
        }
        return false;
    }

    /// <summary>First blocked fraction along move [0,1]. 1 = clear.</summary>
    public static float SweepBlockFraction(
        float x0, float y0, float dx, float dy, float radius,
        IReadOnlyList<RectDef> obstacles, int steps = 12)
    {
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-5f) return 1f;
        steps = Math.Clamp(steps, 2, 32);
        for (var i = 1; i <= steps; i++)
        {
            var t = i / (float)steps;
            var x = x0 + dx * t;
            var y = y0 + dy * t;
            foreach (var o in obstacles)
            {
                if (CircleAabb(x, y, radius, o.X, o.Y, o.W, o.H))
                    return (i - 1) / (float)steps;
            }
        }
        return 1f;
    }

    public static bool PointInRect(float x, float y, RectDef r) =>
        x >= r.X && y >= r.Y && x <= r.X + r.W && y <= r.Y + r.H;

    public static void Normalize(ref float x, ref float y)
    {
        var len = MathF.Sqrt(x * x + y * y);
        if (len < 1e-5f) { x = 1; y = 0; return; }
        x /= len;
        y /= len;
    }

    public static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);
}

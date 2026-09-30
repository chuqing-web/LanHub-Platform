using ArenaDuel.Data;

namespace ArenaDuel.Gameplay;

public sealed class CombatSim
{
    public const float MatchDuration = 120f;
    private const float MoveAccel = 14f;
    private const float CastMoveFactor = 0.28f;
    private const float AttackRecovery = 0.08f;

    private readonly ArenaDef _arena;
    private readonly CharacterDef _charA;
    private readonly CharacterDef _charB;
    private readonly FighterState _a = new();
    private readonly FighterState _b = new();
    private readonly List<ProjectileState> _projectiles = [];
    private readonly Dictionary<FighterSlot, InputState> _inputs = new()
    {
        [FighterSlot.A] = new InputState(),
        [FighterSlot.B] = new InputState()
    };

    private int _nextProjId = 1;
    private float _timeLeft = MatchDuration;
    private bool _ended;
    private string? _winnerId;
    private MatchEndReason _endReason = MatchEndReason.None;
    private PendingCast? _pendingA;
    private PendingCast? _pendingB;

    private sealed class PendingCast
    {
        public int Slot { get; init; }
        public SkillDef Skill { get; init; } = new();
        public float AimX { get; init; }
        public float AimY { get; init; }
        public float TargetX { get; init; }
        public float TargetY { get; init; }
        public bool HasTarget { get; init; }
        public float Delay { get; set; }
    }

    public CombatSim(ArenaDef arena, CharacterDef charA, CharacterDef charB, string playerIdA, string playerIdB)
    {
        _arena = arena;
        _charA = charA;
        _charB = charB;
        InitFighter(_a, FighterSlot.A, charA, playerIdA, arena.SpawnA.X, arena.SpawnA.Y);
        InitFighter(_b, FighterSlot.B, charB, playerIdB, arena.SpawnB.X, arena.SpawnB.Y);
    }

    public string ArenaId => _arena.Id;
    public bool Ended => _ended;
    public float TimeLeft => _timeLeft;

    public void SetInput(FighterSlot slot, InputState input) => _inputs[slot] = input.Clone();

    public MatchSnapshot Capture() => new()
    {
        TimeLeft = _timeLeft,
        ArenaId = _arena.Id,
        Phase = _ended ? "ended" : "battle",
        WinnerPlayerId = _winnerId,
        EndReason = _endReason.ToString(),
        A = CloneFighter(_a),
        B = CloneFighter(_b),
        Projectiles = _projectiles.Select(CloneProj).ToList()
    };

    public void Tick(float dt)
    {
        if (_ended || dt <= 0) return;
        dt = Math.Clamp(dt, 0.001f, 0.05f);
        _timeLeft -= dt;
        TickFighter(_a, _charA, FighterSlot.A, dt, ref _pendingA);
        TickFighter(_b, _charB, FighterSlot.B, dt, ref _pendingB);
        SeparateFighters();
        TickProjectiles(dt);
        ApplyBridgeDamage(dt);
        if (_a.Hp <= 0 || _b.Hp <= 0)
        {
            EndByKill();
            return;
        }
        if (_timeLeft <= 0)
        {
            _timeLeft = 0;
            EndByTime();
        }
    }

    public void ForceEndDisconnect(string winnerPlayerId)
    {
        if (_ended) return;
        _ended = true;
        _winnerId = winnerPlayerId;
        _endReason = MatchEndReason.Disconnect;
    }

    private static void InitFighter(FighterState f, FighterSlot slot, CharacterDef def, string playerId, float x, float y)
    {
        f.PlayerId = playerId;
        f.CharacterId = def.Id;
        f.Slot = slot;
        f.X = x;
        f.Y = y;
        f.MaxHp = def.MaxHp;
        f.Hp = def.MaxHp;
        f.MaxMp = def.MaxMp;
        f.Mp = def.MaxMp;
        f.Radius = def.Radius;
        f.MoveSpeed = def.MoveSpeed;
        f.AimX = slot == FighterSlot.A ? 1 : -1;
    }

    private void TickFighter(FighterState f, CharacterDef def, FighterSlot slot, float dt, ref PendingCast? pending)
    {
        if (!f.Alive) return;
        var now = MatchDuration - _timeLeft;
        var input = _inputs[slot];

        f.CdAttack = Math.Max(0, f.CdAttack - dt);
        f.CdSkill1 = Math.Max(0, f.CdSkill1 - dt);
        f.CdSkill2 = Math.Max(0, f.CdSkill2 - dt);
        f.CdUltimate = Math.Max(0, f.CdUltimate - dt);
        f.AttackLock = Math.Max(0, f.AttackLock - dt);
        f.IFrames = Math.Max(0, f.IFrames - dt);
        f.Mp = Math.Min(f.MaxMp, f.Mp + def.MpRegen * dt);

        if (f.DotLeft > 0)
        {
            var dmg = f.DotDps * dt;
            f.DotLeft -= dt;
            ApplyDamage(f, dmg, lifeSteal: 0, attacker: Opponent(slot));
        }

        if (pending != null)
        {
            pending.Delay -= dt;
            f.CastLeft = Math.Max(0, pending.Delay);
            if (pending.Delay <= 0)
            {
                FireSkill(f, slot, pending.Skill, pending.AimX, pending.AimY,
                    pending.TargetX, pending.TargetY, pending.HasTarget);
                pending = null;
                f.CastingSlot = -1;
                f.CastLeft = 0;
                f.AttackLock = AttackRecovery;
            }
            else
            {
                f.AimX = pending.AimX;
                f.AimY = pending.AimY;
            }
        }
        else
        {
            f.CastLeft = 0;
            f.CastingSlot = -1;
        }

        var rooted = now < f.RootUntil;
        var silenced = now < f.SilenceUntil;
        var slow = now < f.SlowUntil ? Math.Clamp(f.SlowFactor, 0.15f, 1f) : 1f;
        var casting = pending != null;
        var moveMul = casting ? CastMoveFactor : 1f;
        if (f.AttackLock > 0 && !casting) moveMul *= 0.55f;

        // Smooth velocity toward wish dir
        float wishX = 0, wishY = 0;
        if (!rooted)
        {
            wishX = Math.Clamp(input.MoveX, -1, 1);
            wishY = Math.Clamp(input.MoveY, -1, 1);
            var len = MathF.Sqrt(wishX * wishX + wishY * wishY);
            if (len > 1e-3f) { wishX /= len; wishY /= len; }
            else { wishX = 0; wishY = 0; }
        }

        var targetSpeed = f.MoveSpeed * slow * moveMul;
        var desiredVx = wishX * targetSpeed;
        var desiredVy = wishY * targetSpeed;
        var blend = 1f - MathF.Exp(-MoveAccel * dt);
        f.VelX = Collision.Lerp(f.VelX, desiredVx, blend);
        f.VelY = Collision.Lerp(f.VelY, desiredVy, blend);
        if (MathF.Abs(f.VelX) + MathF.Abs(f.VelY) > 1e-3f)
            TryMoveSmooth(f, f.VelX * dt, f.VelY * dt);

        if (pending != null) return;
        if (f.AttackLock > 0.02f) return;

        var foe = Opponent(slot);
        var toFoeX = foe.X - f.X;
        var toFoeY = foe.Y - f.Y;
        var foeDist = MathF.Sqrt(toFoeX * toFoeX + toFoeY * toFoeY);
        Collision.Normalize(ref toFoeX, ref toFoeY);

        ResolveAim(f, input, toFoeX, toFoeY, out var aimX, out var aimY, out var targetX, out var targetY, out var hasTarget);
        f.AimX = aimX;
        f.AimY = aimY;

        // Skill priority over basic attack
        if (!silenced && input.Skill1 && f.CdSkill1 <= 0 && f.Mp >= def.Skill1.ManaCost)
            BeginCast(f, ref pending, 1, def.Skill1, aimX, aimY, targetX, targetY, hasTarget, def.Skill1.ManaCost);
        else if (!silenced && input.Skill2 && f.CdSkill2 <= 0 && f.Mp >= def.Skill2.ManaCost)
            BeginCast(f, ref pending, 2, def.Skill2, aimX, aimY, targetX, targetY, hasTarget, def.Skill2.ManaCost);
        else if (!silenced && input.Ultimate && f.CdUltimate <= 0 && f.Mp >= def.Ultimate.ManaCost)
            BeginCast(f, ref pending, 3, def.Ultimate, aimX, aimY, targetX, targetY, hasTarget, def.Ultimate.ManaCost);
        else if (input.Attack && f.CdAttack <= 0)
        {
            // In range: auto-aim foe. Out of range: still swing toward cursor (ranged AA / client latency).
            if (InAttackRange(def.Attack, foeDist))
                BeginCast(f, ref pending, 0, def.Attack, toFoeX, toFoeY, foe.X, foe.Y, hasTarget: true, spendMp: 0);
            else
                BeginCast(f, ref pending, 0, def.Attack, aimX, aimY, targetX, targetY, hasTarget, spendMp: 0);
        }
    }

    private static bool InAttackRange(SkillDef attack, float foeDist)
    {
        var range = attack.Range > 0 ? attack.Range : 100f;
        // Small grace so moving targets are still hittable
        return foeDist <= range + 12f;
    }

    private static void ResolveAim(
        FighterState f,
        InputState input,
        float toFoeX,
        float toFoeY,
        out float aimX,
        out float aimY,
        out float targetX,
        out float targetY,
        out bool hasTarget)
    {
        aimX = f.AimX;
        aimY = f.AimY;
        targetX = f.X + aimX * 120;
        targetY = f.Y + aimY * 120;
        hasTarget = false;

        if (input.HasWorldAim)
        {
            targetX = input.WorldAimX;
            targetY = input.WorldAimY;
            hasTarget = true;
            var dx = targetX - f.X;
            var dy = targetY - f.Y;
            var len = MathF.Sqrt(dx * dx + dy * dy);
            if (len > 6f)
            {
                aimX = dx / len;
                aimY = dy / len;
                return;
            }
        }

        var aimLen = MathF.Sqrt(input.AimX * input.AimX + input.AimY * input.AimY);
        if (aimLen > 1e-3f)
        {
            aimX = input.AimX / aimLen;
            aimY = input.AimY / aimLen;
            targetX = f.X + aimX * 120;
            targetY = f.Y + aimY * 120;
            hasTarget = true;
            return;
        }

        if (MathF.Abs(toFoeX) + MathF.Abs(toFoeY) > 1e-3f)
        {
            aimX = toFoeX;
            aimY = toFoeY;
        }
    }

    private void BeginCast(
        FighterState f,
        ref PendingCast? pending,
        int slot,
        SkillDef skill,
        float ax,
        float ay,
        float targetX,
        float targetY,
        bool hasTarget,
        float spendMp = 0)
    {
        Collision.Normalize(ref ax, ref ay);
        if (hasTarget && skill.Range > 0)
            ClampPointToRange(f.X, f.Y, ref targetX, ref targetY, skill.Range);

        f.Mp -= spendMp;
        switch (slot)
        {
            case 0: f.CdAttack = skill.Cooldown; break;
            case 1: f.CdSkill1 = skill.Cooldown; break;
            case 2: f.CdSkill2 = skill.Cooldown; break;
            case 3: f.CdUltimate = skill.Cooldown; break;
        }
        f.CastingSlot = slot;
        f.AimX = ax;
        f.AimY = ay;
        f.VelX *= 0.35f;
        f.VelY *= 0.35f;

        if (skill.CastTime <= 0)
        {
            f.CastLeft = 0;
            FireSkill(f, f.Slot, skill, ax, ay, targetX, targetY, hasTarget);
            f.CastingSlot = -1;
            f.AttackLock = AttackRecovery;
            return;
        }

        f.CastLeft = skill.CastTime;
        pending = new PendingCast
        {
            Slot = slot,
            Skill = skill,
            AimX = ax,
            AimY = ay,
            TargetX = targetX,
            TargetY = targetY,
            HasTarget = hasTarget,
            Delay = skill.CastTime
        };
    }

    private void FireSkill(
        FighterState f,
        FighterSlot owner,
        SkillDef skill,
        float ax,
        float ay,
        float targetX = 0,
        float targetY = 0,
        bool hasTarget = false)
    {
        Collision.Normalize(ref ax, ref ay);
        var type = skill.Type.ToLowerInvariant();

        if (type == "buff")
        {
            f.Shield += skill.Shield;
            if (skill.SlowDuration > 0)
            {
                // self haste encoded as SlowFactor > 1 temporarily via reverse: use Speed buff as negative slow
            }
            return;
        }

        if (type == "dash")
        {
            ExecuteDash(f, owner, skill, ax, ay, targetX, targetY, hasTarget);
            return;
        }

        if (type == "aoe")
        {
            float tx = f.X, ty = f.Y;
            if (skill.Range <= 0)
            {
                tx = f.X;
                ty = f.Y;
            }
            else if (hasTarget)
            {
                tx = targetX;
                ty = targetY;
                ClampPointToRange(f.X, f.Y, ref tx, ref ty, skill.Range);
            }
            else
            {
                tx = f.X + ax * skill.Range;
                ty = f.Y + ay * skill.Range;
            }
            ClampToArena(ref tx, ref ty);

            // Blocked by solid cover if LOS crosses thick obstacle center (soft: only full block if target inside wall)
            if (!HitsObstacle(tx, ty, 2))
                SpawnAoeAt(tx, ty, owner, skill);
            else
            {
                // snap to nearest clear point toward caster
                for (var i = 8; i >= 1; i--)
                {
                    var t = i / 8f;
                    var sx = f.X + (tx - f.X) * t;
                    var sy = f.Y + (ty - f.Y) * t;
                    if (!HitsObstacle(sx, sy, skill.Radius * 0.3f))
                    {
                        SpawnAoeAt(sx, sy, owner, skill);
                        break;
                    }
                }
            }
            return;
        }

        // projectile (+ multi)
        var count = Math.Max(1, skill.Count);
        var spread = skill.Spread;
        for (var i = 0; i < count; i++)
        {
            var angle = count == 1 ? 0 : (i - (count - 1) / 2f) * spread;
            var cos = MathF.Cos(angle);
            var sin = MathF.Sin(angle);
            var dx = ax * cos - ay * sin;
            var dy = ax * sin + ay * cos;
            Collision.Normalize(ref dx, ref dy);
            var speed = skill.Speed <= 0 ? 400 : skill.Speed;
            var ox = f.X + dx * (f.Radius + 6);
            var oy = f.Y + dy * (f.Radius + 6);
            _projectiles.Add(new ProjectileState
            {
                Id = _nextProjId++,
                Owner = owner,
                Type = "projectile",
                X = ox,
                Y = oy,
                PrevX = ox,
                PrevY = oy,
                Vx = dx * speed,
                Vy = dy * speed,
                Radius = Math.Max(4, skill.Radius),
                Damage = skill.Damage,
                Life = skill.Lifetime <= 0 ? 0.6f : skill.Lifetime,
                LifeSteal = skill.LifeSteal,
                SlowFactor = skill.SlowFactor,
                SlowDuration = skill.SlowDuration,
                SilenceDuration = skill.SilenceDuration,
                RootDuration = skill.RootDuration,
                AntiHealDuration = skill.AntiHealDuration,
                DotDamage = skill.DotDamage,
                DotDuration = skill.DotDuration,
                MarkDuration = skill.MarkDuration,
                MarkBonus = skill.MarkBonus,
                ChainBonus = skill.ChainBonus,
                Knockback = skill.Knockback,
                Pierce = skill.Count > 1 || skill.ChainBonus > 0
            });
        }
    }

    private void ExecuteDash(
        FighterState f,
        FighterSlot owner,
        SkillDef skill,
        float ax,
        float ay,
        float targetX,
        float targetY,
        bool hasTarget)
    {
        var maxDist = skill.Range > 0
            ? skill.Range
            : skill.Speed * Math.Max(skill.Lifetime, 0.15f);
        float dx, dy;
        if (hasTarget)
        {
            dx = targetX - f.X;
            dy = targetY - f.Y;
        }
        else
        {
            dx = ax * maxDist;
            dy = ay * maxDist;
        }

        var dist = MathF.Sqrt(dx * dx + dy * dy);
        if (dist < 1e-3f) return;
        if (dist > maxDist)
        {
            dx = dx / dist * maxDist;
            dy = dy / dist * maxDist;
            dist = maxDist;
        }

        var foe = Opponent(owner);
        var hitFoe = false;
        const int steps = 10;
        f.IFrames = Math.Max(f.IFrames, 0.12f);

        for (var i = 1; i <= steps; i++)
        {
            var stepDx = dx / steps;
            var stepDy = dy / steps;
            var beforeX = f.X;
            var beforeY = f.Y;
            TryMoveSmooth(f, stepDx, stepDy);

            if (!hitFoe && foe.Alive && foe.IFrames <= 0)
            {
                if (Collision.SweepCircleCircle(beforeX, beforeY, f.X, f.Y, f.Radius * 0.85f,
                        foe.X, foe.Y, foe.Radius))
                {
                    hitFoe = true;
                    ApplyHitEffects(owner, foe, skill.Damage, skill);
                }
            }
        }

        // End burst even if dash missed path
        if (!hitFoe)
            SpawnAoeAt(f.X, f.Y, owner, skill);
        else
            SpawnAoeAt(f.X, f.Y, owner, WithDamage(skill, 0)); // VFX only residual
    }

    private static SkillDef WithDamage(SkillDef src, float dmg) => new()
    {
        Name = src.Name,
        Damage = dmg,
        Radius = src.Radius,
        Lifetime = Math.Min(0.15f, src.Lifetime),
        LifeSteal = 0,
        SlowFactor = src.SlowFactor,
        SlowDuration = src.SlowDuration,
        SilenceDuration = src.SilenceDuration,
        RootDuration = src.RootDuration,
        AntiHealDuration = src.AntiHealDuration,
        DotDamage = src.DotDamage,
        DotDuration = src.DotDuration,
        MarkDuration = src.MarkDuration,
        MarkBonus = src.MarkBonus,
        Knockback = src.Knockback
    };

    private void SpawnAoeAt(float x, float y, FighterSlot owner, SkillDef skill)
    {
        _projectiles.Add(new ProjectileState
        {
            Id = _nextProjId++,
            Owner = owner,
            Type = "aoe",
            X = x,
            Y = y,
            PrevX = x,
            PrevY = y,
            Radius = Math.Max(8, skill.Radius),
            Damage = skill.Damage,
            Life = Math.Max(0.06f, skill.Lifetime),
            LifeSteal = skill.LifeSteal,
            SlowFactor = skill.SlowFactor,
            SlowDuration = skill.SlowDuration,
            SilenceDuration = skill.SilenceDuration,
            RootDuration = skill.RootDuration,
            AntiHealDuration = skill.AntiHealDuration,
            DotDamage = skill.DotDamage,
            DotDuration = skill.DotDuration,
            MarkDuration = skill.MarkDuration,
            MarkBonus = skill.MarkBonus,
            Knockback = skill.Knockback,
            IsAoePulse = true
        });
    }

    private void TickProjectiles(float dt)
    {
        for (var i = _projectiles.Count - 1; i >= 0; i--)
        {
            var p = _projectiles[i];
            p.Life -= dt;
            if (p.IsAoePulse)
            {
                if (!p.Hit)
                {
                    TryHitTargetSweep(p, p.X, p.Y, p.X, p.Y);
                    p.Hit = true;
                }
                if (p.Life <= 0) _projectiles.RemoveAt(i);
                continue;
            }

            p.PrevX = p.X;
            p.PrevY = p.Y;
            var nx = p.X + p.Vx * dt;
            var ny = p.Y + p.Vy * dt;

            // Obstacle sweep — destroy on wall
            if (Collision.SweepHitsObstacles(p.X, p.Y, nx, ny, p.Radius, _arena.Obstacles))
            {
                _projectiles.RemoveAt(i);
                continue;
            }

            if (OutOfPlayable(nx, ny))
            {
                _projectiles.RemoveAt(i);
                continue;
            }

            var hit = TryHitTargetSweep(p, p.X, p.Y, nx, ny);
            p.X = nx;
            p.Y = ny;

            if (hit && !p.Pierce)
            {
                _projectiles.RemoveAt(i);
                continue;
            }
            if (p.Life <= 0) _projectiles.RemoveAt(i);
        }
    }

    private bool TryHitTargetSweep(ProjectileState p, float x0, float y0, float x1, float y1)
    {
        var target = p.Owner == FighterSlot.A ? _b : _a;
        if (!target.Alive || target.IFrames > 0) return false;
        var slotKey = (int)target.Slot;
        if (p.HitSlots.Contains(slotKey)) return false;

        if (!Collision.SweepCircleCircle(x0, y0, x1, y1, p.Radius, target.X, target.Y, target.Radius))
            return false;

        p.HitSlots.Add(slotKey);
        ApplyProjectileHit(p, target);
        return true;
    }

    private void ApplyProjectileHit(ProjectileState p, FighterState target)
    {
        var attacker = p.Owner == FighterSlot.A ? _a : _b;
        var skillLike = new SkillDef
        {
            Damage = p.Damage,
            LifeSteal = p.LifeSteal,
            SlowFactor = p.SlowFactor,
            SlowDuration = p.SlowDuration,
            SilenceDuration = p.SilenceDuration,
            RootDuration = p.RootDuration,
            AntiHealDuration = p.AntiHealDuration,
            DotDamage = p.DotDamage,
            DotDuration = p.DotDuration,
            MarkDuration = p.MarkDuration,
            MarkBonus = p.MarkBonus,
            Knockback = p.Knockback,
            ChainBonus = p.ChainBonus
        };
        var dmg = p.Damage;
        var now = MatchDuration - _timeLeft;
        if (now < target.MarkUntil) dmg += target.MarkBonus;
        if (p.ChainBonus > 0) dmg += p.ChainBonus;
        skillLike.Damage = dmg;
        ApplyHitEffects(p.Owner, target, dmg, skillLike);
    }

    private void ApplyHitEffects(FighterSlot owner, FighterState target, float damage, SkillDef fx)
    {
        var attacker = owner == FighterSlot.A ? _a : _b;
        var now = MatchDuration - _timeLeft;
        ApplyDamage(target, damage, fx.LifeSteal, attacker);

        var dx = target.X - attacker.X;
        var dy = target.Y - attacker.Y;
        if (fx.Knockback > 0)
        {
            Collision.Normalize(ref dx, ref dy);
            TryMoveSmooth(target, dx * fx.Knockback, dy * fx.Knockback);
        }
        if (fx.SlowDuration > 0)
        {
            target.SlowUntil = now + fx.SlowDuration;
            target.SlowFactor = fx.SlowFactor <= 0 ? 0.5f : fx.SlowFactor;
        }
        if (fx.SilenceDuration > 0) target.SilenceUntil = now + fx.SilenceDuration;
        if (fx.RootDuration > 0) target.RootUntil = now + fx.RootDuration;
        if (fx.AntiHealDuration > 0) target.AntiHealUntil = now + fx.AntiHealDuration;
        if (fx.MarkDuration > 0)
        {
            target.MarkUntil = now + fx.MarkDuration;
            target.MarkBonus = fx.MarkBonus;
        }
        if (fx.DotDuration > 0)
        {
            target.DotLeft = fx.DotDuration;
            target.DotDps = fx.DotDamage;
        }
    }

    private void ApplyDamage(FighterState target, float damage, float lifeSteal, FighterState attacker)
    {
        if (damage <= 0 || target.IFrames > 0) return;
        var remain = damage;
        if (target.Shield > 0)
        {
            var absorb = Math.Min(target.Shield, remain);
            target.Shield -= absorb;
            remain -= absorb;
        }
        target.Hp = Math.Max(0, target.Hp - remain);
        if (lifeSteal > 0 && remain > 0)
        {
            var now = MatchDuration - _timeLeft;
            if (now >= attacker.AntiHealUntil)
                attacker.Hp = Math.Min(attacker.MaxHp, attacker.Hp + remain * lifeSteal);
        }
    }

    private void ApplyBridgeDamage(float dt)
    {
        if (!string.Equals(_arena.OutOfBounds, "damage", StringComparison.OrdinalIgnoreCase)) return;
        if (_arena.Bridge is null) return;
        var dps = _arena.OutDamagePerSecond <= 0 ? 40 : _arena.OutDamagePerSecond;
        foreach (var f in new[] { _a, _b })
        {
            if (!f.Alive || f.IFrames > 0) continue;
            if (!Collision.PointInRect(f.X, f.Y, _arena.Bridge))
                f.Hp = Math.Max(0, f.Hp - dps * dt);
        }
    }

    private void SeparateFighters()
    {
        if (!_a.Alive || !_b.Alive) return;
        var dx = _b.X - _a.X;
        var dy = _b.Y - _a.Y;
        var dist = MathF.Sqrt(dx * dx + dy * dy);
        var min = _a.Radius + _b.Radius - 2f;
        if (dist >= min || dist < 1e-4f) return;
        Collision.Normalize(ref dx, ref dy);
        var push = (min - dist) * 0.5f;
        TryMoveSmooth(_a, -dx * push, -dy * push);
        TryMoveSmooth(_b, dx * push, dy * push);
    }

    private FighterState Opponent(FighterSlot slot) => slot == FighterSlot.A ? _b : _a;

    private void TryMoveSmooth(FighterState f, float dx, float dy)
    {
        if (MathF.Abs(dx) < 1e-6f && MathF.Abs(dy) < 1e-6f) return;

        // Axis-separated slide with sweep fraction
        var fx = Collision.SweepBlockFraction(f.X, f.Y, dx, 0, f.Radius, _arena.Obstacles);
        var nx = f.X + dx * fx;
        if (InPlayable(nx, f.Y, f.Radius)) f.X = nx;

        var fy = Collision.SweepBlockFraction(f.X, f.Y, 0, dy, f.Radius, _arena.Obstacles);
        var ny = f.Y + dy * fy;
        if (InPlayable(f.X, ny, f.Radius)) f.Y = ny;

        // Bridge OOB: still allow but damage handles it; clamp soft to arena bounds always
        ClampFighter(f);

        // If on damage arena and outside bridge, gently pull toward bridge center
        if (string.Equals(_arena.OutOfBounds, "damage", StringComparison.OrdinalIgnoreCase)
            && _arena.Bridge != null
            && !Collision.PointInRect(f.X, f.Y, _arena.Bridge))
        {
            // no hard block — falling feels intentional
        }
    }

    private void ClampFighter(FighterState f)
    {
        f.X = Math.Clamp(f.X, f.Radius, _arena.Width - f.Radius);
        f.Y = Math.Clamp(f.Y, f.Radius, _arena.Height - f.Radius);
    }

    private void ClampToArena(ref float x, ref float y)
    {
        x = Math.Clamp(x, 0, _arena.Width);
        y = Math.Clamp(y, 0, _arena.Height);
    }

    private static void ClampPointToRange(float ox, float oy, ref float tx, ref float ty, float range)
    {
        if (range <= 0) return;
        var dx = tx - ox;
        var dy = ty - oy;
        var d = MathF.Sqrt(dx * dx + dy * dy);
        if (d <= range || d < 1e-5f) return;
        tx = ox + dx / d * range;
        ty = oy + dy / d * range;
    }

    private bool HitsObstacle(float x, float y, float radius)
    {
        foreach (var o in _arena.Obstacles)
        {
            if (Collision.CircleAabb(x, y, radius, o.X, o.Y, o.W, o.H)) return true;
        }
        return false;
    }

    private bool InPlayable(float x, float y, float radius)
    {
        if (string.Equals(_arena.OutOfBounds, "damage", StringComparison.OrdinalIgnoreCase))
            return x >= radius && y >= radius && x <= _arena.Width - radius && y <= _arena.Height - radius;
        return x >= radius && y >= radius && x <= _arena.Width - radius && y <= _arena.Height - radius
               && !HitsObstacle(x, y, radius);
    }

    private bool OutOfPlayable(float x, float y) =>
        x < -20 || y < -20 || x > _arena.Width + 20 || y > _arena.Height + 20;

    private void EndByKill()
    {
        _ended = true;
        _endReason = MatchEndReason.Kill;
        if (_a.Hp <= 0 && _b.Hp <= 0) { _winnerId = null; _endReason = MatchEndReason.Draw; }
        else if (_a.Hp <= 0) _winnerId = _b.PlayerId;
        else _winnerId = _a.PlayerId;
    }

    private void EndByTime()
    {
        _ended = true;
        if (Math.Abs(_a.Hp - _b.Hp) < 0.5f)
        {
            _winnerId = null;
            _endReason = MatchEndReason.Draw;
        }
        else if (_a.Hp > _b.Hp)
        {
            _winnerId = _a.PlayerId;
            _endReason = MatchEndReason.Time;
        }
        else
        {
            _winnerId = _b.PlayerId;
            _endReason = MatchEndReason.Time;
        }
    }

    private static FighterState CloneFighter(FighterState f) => new()
    {
        PlayerId = f.PlayerId,
        CharacterId = f.CharacterId,
        Slot = f.Slot,
        X = f.X,
        Y = f.Y,
        Hp = f.Hp,
        MaxHp = f.MaxHp,
        Mp = f.Mp,
        MaxMp = f.MaxMp,
        Radius = f.Radius,
        MoveSpeed = f.MoveSpeed,
        Shield = f.Shield,
        CdAttack = f.CdAttack,
        CdSkill1 = f.CdSkill1,
        CdSkill2 = f.CdSkill2,
        CdUltimate = f.CdUltimate,
        CastLeft = f.CastLeft,
        CastingSlot = f.CastingSlot,
        AimX = f.AimX,
        AimY = f.AimY,
        VelX = f.VelX,
        VelY = f.VelY,
        AttackLock = f.AttackLock,
        IFrames = f.IFrames,
        SlowUntil = f.SlowUntil,
        SlowFactor = f.SlowFactor,
        RootUntil = f.RootUntil,
        SilenceUntil = f.SilenceUntil,
        AntiHealUntil = f.AntiHealUntil,
        MarkUntil = f.MarkUntil,
        MarkBonus = f.MarkBonus,
        DotLeft = f.DotLeft,
        DotDps = f.DotDps
    };

    private static ProjectileState CloneProj(ProjectileState p) => new()
    {
        Id = p.Id,
        Owner = p.Owner,
        Type = p.Type,
        X = p.X,
        Y = p.Y,
        PrevX = p.PrevX,
        PrevY = p.PrevY,
        Vx = p.Vx,
        Vy = p.Vy,
        Radius = p.Radius,
        Damage = p.Damage,
        Life = p.Life,
        IsAoePulse = p.IsAoePulse,
        Pierce = p.Pierce
    };
}

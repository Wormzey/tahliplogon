using System;
using System.Collections.Generic;
using System.Numerics;

namespace StrayApocalypse;

/// <summary>
/// The fixed layout of Stray Apocalypse, from the ACT logs. All positions are X/Z offsets from the arena centre.
/// Every logged pull used all six starting pairs once, alternating NW and NE, in a different order each time.
/// </summary>
internal static class Pattern
{
    public const int Sets = 6;
    public const int Steps = 6;
    public const float SetGap = 2.5f;
    public const float StarterDelay = 3.98f;
    public const float FirstStepDelay = 4.60f;
    public const float StepInterval = 0.514f;

    /// <summary>[side][pair] = the pair's two starters. Side 0 = NW (heading SE), side 1 = NE (heading SW).</summary>
    public static readonly (Vector2 A, Vector2 B)[][] Pairs =
    [
        [
            (new(-20f, -15f), new(-5f, -30f)),   // (80,85) (95,70)
            (new(-10f, -25f), new(-25f, -10f)),  // (90,75) (75,90)
            (new(-15f, -20f), new(-30f, -5f)),   // (85,80) (70,95)
        ],
        [
            (new(15f, -20f), new(30f, -5f)),     // (115,80) (130,95)
            (new(20f, -15f), new(5f, -30f)),     // (120,85) (105,70)
            (new(25f, -10f), new(10f, -25f)),    // (125,90) (110,75)
        ],
    ];

    public static Vector2 Step(int side) => side == 0 ? new Vector2(5f, 5f) : new Vector2(-5f, 5f);

    /// <summary>Seconds after the starter's cast begins that step k goes off (k = 0 is the starter itself).</summary>
    public static float FireTime(int k) => k == 0 ? StarterDelay : FirstStepDelay + StepInterval * (k - 1);
}

/// <summary>An announced explosion that hasn't gone off yet, going off in <c>In</c> seconds.</summary>
internal readonly record struct Blast(Vector2 Pos, float In);

/// <summary>
/// Chooses where to stand, given the explosions announced so far.
///
/// You're modelled as noticing a new target after <see cref="Reaction"/> seconds and then running to it in a
/// straight line at <see cref="Speed"/> (normal run, no sprint). Every explosion is checked against where you'd be
/// at the moment it goes off, so a route that crosses an explosion counts as unsafe, not just the destination.
/// People react at different speeds, so each route is checked across a spread of reaction times.
///
/// Spots are ranked first by whether they clear every announced explosion, on the way and once there (if none do,
/// whichever is hit latest). Then by a cost: how far you'd run, plus a little weight towards the centre, minus
/// <see cref="MarginWeight"/> yalms for each yalm of margin you'd keep from the nearest explosion's edge as it goes off
/// (counted up to <see cref="Buffer"/>). So it takes a slightly longer run for a wider berth, but not a much longer
/// one. The current target is kept while it stays clear, unless another is clearly cheaper.
///
/// Why not a hard buffer: with 7 y explosions the lines cover the arena so densely that a spot keeping a few yalms
/// from everything is often impossible. Making the buffer a must, or switching spots for margin alone, doubled the
/// moves (to 9-16 per mechanic) for little extra clearance; this weighting cost ~0.3 extra moves.
///
/// Simulated over all 36 set orders with a player who follows the circle (0.5-1.0 s reaction, 6 y/s), it was not
/// hit at a 7 y radius, and moved about 5 times per Stray Apocalypse, ~5.6 s before the old spot would be hit.
/// Looking ahead at sets not yet announced was tried and only added moves, so it isn't used.
/// </summary>
internal sealed class SafeSpotPlanner
{
    public float Reaction = 0.7f;
    public float Speed = 6f;
    public float HitboxMargin = 0.5f;
    public float Buffer = 2f;
    public float ArenaRadius = 20f;
    public float EdgeMargin = 1f;
    public float GridStep = 1f;
    public float CentreWeight = 0.25f;
    public float SwitchSeconds = 0.3f;
    public float SwitchCost = 3f;
    public float MarginWeight = 2f;

    private static readonly float[] ReactionSpread = [0.4f, 0.7f, 1f, 1.4f, 2f];

    public Vector2? Target { get; private set; }

    public void Reset() => Target = null;

    public Vector2? Update(Vector2 centre, Vector2 player, float blastRadius, IReadOnlyList<Blast> blasts)
    {
        if (blasts.Count == 0)
        {
            Target = null;
            return null;
        }

        var reach = blastRadius + HitboxMargin;
        if (Target is { } stale && Vector2.Distance(stale, centre) > ArenaRadius)
            Target = null;

        Score? best = null;
        Vector2 bestSpot = default;
        var limit = ArenaRadius - EdgeMargin;
        for (var dx = -limit; dx <= limit; dx += GridStep)
        {
            for (var dz = -limit; dz <= limit; dz += GridStep)
            {
                var offset = new Vector2(dx, dz);
                if (offset.Length() > limit)
                    continue;

                var spot = centre + offset;
                var score = Worst(spot, player, centre, reach, blasts);
                if (best is null || score.BetterThan(best.Value, 0.05f, 0f))
                {
                    best = score;
                    bestSpot = spot;
                }
            }
        }

        if (Target is { } current)
        {
            // already heading there, so no reaction delay
            var kept = Evaluate(current, player, centre, reach, blasts, 0f);
            if (!best!.Value.BetterThan(kept, SwitchSeconds, SwitchCost))
                return Target;
        }

        Target = bestSpot;
        return Target;
    }

    private Score Worst(Vector2 spot, Vector2 player, Vector2 centre, float reach, IReadOnlyList<Blast> blasts)
    {
        Score? worst = null;
        foreach (var factor in ReactionSpread)
        {
            var s = Evaluate(spot, player, centre, reach, blasts, Reaction * factor);
            worst = worst is not { } w ? s : w with
            {
                Danger = MathF.Min(w.Danger, s.Danger),
                Margin = MathF.Min(w.Margin, s.Margin),
            };
        }

        return worst!.Value;
    }

    private Score Evaluate(Vector2 spot, Vector2 player, Vector2 centre, float reach, IReadOnlyList<Blast> blasts,
                           float reaction)
    {
        var path = spot - player;
        var distance = path.Length();
        var direction = distance > 0.01f ? path / distance : Vector2.Zero;
        var danger = float.PositiveInfinity;
        var margin = Buffer;

        foreach (var b in blasts)
        {
            if (b.In < 0f)
                continue;

            var travelled = MathF.Min(distance, MathF.Max(0f, b.In - reaction) * Speed);
            var gap = Vector2.Distance(player + direction * travelled, b.Pos) - reach;
            if (gap < margin)
                margin = gap;
            if (gap <= 0f && b.In < danger)
                danger = b.In;
        }

        // each yalm of margin (up to the buffer) is worth MarginWeight yalms of running
        var cost = distance + CentreWeight * Vector2.Distance(spot, centre) - MarginWeight * MathF.Max(0f, margin);
        return new Score(danger, MathF.Max(0f, margin), cost);
    }

    /// <summary>
    /// <c>Danger</c>: seconds until something announced could hit you (infinity if nothing can).
    /// <c>Margin</c>: the closest you'd come to an explosion's edge as it goes off, capped at the buffer.
    /// </summary>
    private readonly record struct Score(float Danger, float Margin, float Cost)
    {
        public bool BetterThan(Score other, float dangerMargin, float costMargin)
        {
            var clear = float.IsPositiveInfinity(Danger);
            var otherClear = float.IsPositiveInfinity(other.Danger);
            if (clear != otherClear)
                return clear;

            if (!clear)
            {
                if (Danger > other.Danger + dangerMargin) return true;
                if (Danger < other.Danger - dangerMargin) return false;
            }

            return Cost < other.Cost - costMargin;
        }
    }
}

// FateApproach の ChooseSmoothed・Validate・IsDescentAcceptable（本物を抜き出したもの）を試す。
// 実行：python -X utf8 tools/regression/approach_smooth.py
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoCollector.Automation;
using ECommons.GameHelpers;
using SmoothNav.Core;

static class Program
{
    static int count;
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
    static RoutePlan Plan(bool smoothed, params Vector3[] points) => new(points.ToList(), smoothed, false, false, "試験", null, .1);

    public static void Main()
    {
        var probe = new FateApproachProbe();
        // 水平に飛ぶ接近（降下ではない）。公式の経路は角が 1 つ。
        Player.Position = new(0, 50, 0);
        List<Vector3> official = [new(0, 50, 0), new(40, 50, 0), new(40, 50, 40)];
        var curve = Plan(true, new(30, 50, 0), new(38, 50, 4), new(40, 50, 12), new(40, 50, 40));
        var used = probe.Choose(official, curve, official[^1], "試験");
        Check(used.Count == 5 && used[0] == official[0] && used.Skip(1).SequenceEqual(curve.Waypoints), "確かめを通った整えた経路を使う（出発点を付けて渡す）");
        Check(probe.Trace.Decisions.Any(d => d.Contains("曲線に整えた")), "整えたことを記録に残す");
        Check(ReferenceEquals(probe.Choose(official, Plan(false, official[1], official[2]), official[^1], "試験"), official), "整えていなければ公式の経路");
        Check(ReferenceEquals(probe.Choose(official, Plan(true, new(30, 50, 0), new(float.NaN, 50, 0), official[2]), official[^1], "試験"), official),
            "読めない座標が入った整えた経路は使わない");
        Check(ReferenceEquals(probe.Choose(official, Plan(true, new(30, 50, 0), new(10, 50, 10)), official[^1], "試験"), official),
            "終点が目的地から離れる整えた経路は使わない");

        // 降下：公式の経路は斜めに降りる。整えた経路が「着地点の真上まで水平に行ってから落ちる」形なら使わない。
        probe.Phase = ApproachPhase.Descending; probe.Landing = new(40, 0, 40);
        List<Vector3> diagonal = [new(0, 50, 0), new(20, 25, 20), new(40, 0, 40)];
        var drop = Plan(true, new(20, 50, 20), new(39.5f, 50, 39.5f), new(40, 0, 40));
        Check(ReferenceEquals(probe.Choose(diagonal, drop, diagonal[^1], "試験"), diagonal), "降下で垂直落下になる整えた経路は使わない");
        Check(probe.Trace.Troubles.Any(t => t.Contains("垂直落下")), "使わない理由を記録に残す");
        var slope = Plan(true, new(10, 40, 10), new(20, 27, 20), new(30, 13, 30), new(40, 0, 40));
        Check(probe.Choose(diagonal, slope, diagonal[^1], "試験").Count == 5, "斜めに降りる整えた経路は使う");
        probe.DescentStages = [new(10, 30, 10)];
        Check(probe.Choose(diagonal, drop, diagonal[^1], "試験").Count == 4, "段に分けて降りている間は形を問わない（今までの確かめと同じ）");

        // 先に求める飛行の経路（2026-10-10）：離陸の合図の点から外周上空、そこから着地点までつないだ経路。
        // 確かめは「いまの位置」ではなく、経路を求めた出発点（離陸の合図の点・外周上空の点）で測る。
        var pre = new FateApproachProbe { Phase = ApproachPhase.Mounting, PreOrigin = new(0, 5, 0), Entry = new(60, 40, 0), Landing = new(100, 0, 0) };
        Player.Position = new(500, 0, 500); // まだ乗っている途中で、離れた所にいる扱い（確かめに使わないことを見る）
        List<Vector3> joined = [new(0, 5, 0), new(30, 30, 0), new(60, 40, 0), new(80, 20, 0), new(100, 0, 0)];
        var curved = Plan(true, new(25, 28, 2), new(55, 39, 1), new(70, 30, 0), new(85, 15, 0), new(100, 0, 0));
        var chosen = pre.ChoosePre(joined, curved, true);
        Check(chosen.Count == 6 && chosen[0] == joined[0] && chosen.Skip(1).SequenceEqual(curved.Waypoints), "つないだ経路の、確かめを通った整えた形を使う");
        Check(pre.Trace.Decisions.Any(d => d.Contains("先に求めた経路を曲線に整えた")), "先に求めた経路を整えたことを記録に残す");
        var fall = Plan(true, new(30, 40, 0), new(99.5f, 40, 0), new(100, 0, 0));
        Check(ReferenceEquals(pre.ChoosePre(joined, fall, true), joined), "つないだ経路を整えた形が垂直落下になるなら、つないだ公式の経路を使う");
        Check(pre.Trace.Troubles.Any(t => t.Contains("垂直落下")), "使わない理由を記録に残す");
        Check(ReferenceEquals(pre.ChoosePre(joined, Plan(false, joined.Skip(1).ToArray()), true), joined), "整わなければ、つないだ公式の経路");
        List<Vector3> toEntry = [new(0, 5, 0), new(30, 30, 0), new(60, 40, 0)];
        Check(ReferenceEquals(pre.ChoosePre(toEntry, Plan(true, new(30, 35, 5), new(10, 40, 40)), false), toEntry),
            "外周上空までの経路は、終点が外周上空から離れる整えた形を使わない");
        var toEntryCurve = Plan(true, new(20, 25, 3), new(40, 35, 2), new(60, 40, 0));
        Check(pre.ChoosePre(toEntry, toEntryCurve, false).Skip(1).SequenceEqual(toEntryCurve.Waypoints),
            "外周上空までの経路の整えた形は、外周上空で終わっていれば使う（着地点では確かめない）");
        Console.WriteLine($"回帰試験（FATE の接近・曲線）{count} 件 合格");
    }
}

namespace AutoCollector.Automation
{
    /*ENUM*/

    public sealed class FakeTrace
    {
        public readonly List<string> Decisions = [], Troubles = [], States = [];
        public void Decision(string what, string why) => this.Decisions.Add(what + " " + why);
        public void Trouble(string what, string detail) => this.Troubles.Add(what + " " + detail);
        public void State(string step, string detail) => this.States.Add(step + " " + detail);
    }

    public static class SmoothMoveService
    {
        public static string Describe(RoutePlan plan) => plan.Reason;
    }

    // 本物の FateApproach から抜き出したメソッドと定数を、同じ名前の欄を持つ入れ物に入れる。
    public sealed class FateApproachProbe
    {
        public readonly FakeTrace Trace = new();
        public ApproachPhase Phase { get; set; } = ApproachPhase.Idle;
        public Vector3 Landing { get => this.landing; set => this.landing = value; }
        public Vector3 Entry { get => this.entry; set => this.entry = value; }
        public Vector3 PreOrigin { get => this.preOrigin; set => this.preOrigin = value; }
        public List<Vector3> DescentStages { get => this.descentStages; set => this.descentStages = value; }
        private FakeTrace trace => this.Trace;
        private Vector3 landing;
        private Vector3 entry;
        private Vector3 preOrigin;
        private List<Vector3> descentStages = [];

        /*MEMBERS*/

        public List<Vector3> Choose(List<Vector3> official, RoutePlan plan, Vector3 destination, string describe) => this.ChooseSmoothed(official, plan, destination, describe);
        public List<Vector3> ChoosePre(List<Vector3> raw, RoutePlan plan, bool joined) => this.ChoosePrePlan(raw, plan, joined);
    }
}

namespace ECommons.GameHelpers
{
    public static class Player { public static Vector3 Position; }
}

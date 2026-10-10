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
        public List<Vector3> DescentStages { get => this.descentStages; set => this.descentStages = value; }
        private FakeTrace trace => this.Trace;
        private Vector3 landing;
        private List<Vector3> descentStages = [];

        /*MEMBERS*/

        public List<Vector3> Choose(List<Vector3> official, RoutePlan plan, Vector3 destination, string describe) => this.ChooseSmoothed(official, plan, destination, describe);
    }
}

namespace ECommons.GameHelpers
{
    public static class Player { public static Vector3 Position; }
}

using System;
using System.Linq;

internal static class Program {
    static int total, failed;

    internal static void Check(string name, bool ok) {
        total++;
        if (ok) return;
        failed++;
        Console.WriteLine("FAIL " + name);
    }

    internal static bool Near(float a, float b) => Math.Abs(a - b) < 1e-4f;

    static int Main(string[] args) {
        string? file = args.Length >= 2 ? args[1] : null;
        if (args.Length >= 1 && args[0] == "--overlaps") return PoseTests.PrintOverlaps(file);          // the poses (default, or a poses.txt) that one pair of hands can strike together
        if (args.Length >= 1 && args[0] == "--paths") return PoseTests.PrintPaths();                     // task 17a's hand paths that fire what they should not (KNOWN or NEW)
        if (args.Length >= 1 && args[0] == "--replay") return PoseTests.ReplayTrace(file, args.Skip(2).ToArray()); // a pose-trace CSV (and a poses.txt, Key=value numbers) through the machine
        if (args.Length >= 1 && args[0] == "--check-poses") return PoseTests.CheckPoses(file);          // a poses.txt as PoseLibrary.Parse reads it: POSES OK (16)
        if (args.Length >= 1 && args[0] == "--capture-sample") return PoseTests.WriteCaptureSample(file); // a made-up capture CSV through PoseCapture, for fit_poses.py
        StoneTests.Run();
        PoseTests.Run();
        MotionTests.Run();

        Console.WriteLine(failed == 0 ? $"ALL PASS ({total})" : $"FAILED {failed} of {total}");
        return failed;
    }
}

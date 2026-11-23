using System;

internal static class Program {
    static int total, failed;

    internal static void Check(string name, bool ok) {
        total++;
        if (ok) return;
        failed++;
        Console.WriteLine("FAIL " + name);
    }

    internal static bool Near(float a, float b) => Math.Abs(a - b) < 1e-4f;

    static int Main() {
        StoneTests.Run();

        Console.WriteLine(failed == 0 ? $"ALL PASS ({total})" : $"FAILED {failed} of {total}");
        return failed;
    }
}

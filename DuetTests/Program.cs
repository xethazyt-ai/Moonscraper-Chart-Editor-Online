// See LICENSE in project root for license information.

using System;

public static class Program
{
    public static int Main()
    {
        Console.WriteLine("== Recording and replaying edits");
        int failures = ReplayTests.Run();

        Console.WriteLine("== Sessions over the network");
        failures += SessionTests.Run();

        Console.WriteLine("== Online room links");
        failures += LinkTests.Run();

        Console.WriteLine("== Cursors and song details");
        failures += PresenceTests.Run();

        Console.WriteLine("== Song files");
        failures += FileTests.Run();

        Console.WriteLine(failures == 0 ? "All checks passed." : failures + " check(s) failed.");
        return failures == 0 ? 0 : 1;
    }
}

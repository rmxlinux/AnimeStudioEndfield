using System;

namespace AnimeStudio.Tests
{
    internal static class Program
    {
        private static int Main()
        {
            Console.WriteLine("AnimeStudio Endfield VFS crypto tests");
            Console.WriteLine();

            var runner = new TestRunner();
            DescriptorTests.Register(runner);
            StreamTests.Register(runner);
            KeyStoreTests.Register(runner);
            PartialStreamTests.Register(runner);
            VfsBundleTests.Register(runner);

            return runner.Complete();
        }
    }
}

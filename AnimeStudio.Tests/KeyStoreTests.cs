using System;
using System.Collections.Generic;

namespace AnimeStudio.Tests
{
    internal static class KeyStoreTests
    {
        public static void Register(TestRunner runner)
        {
            runner.Add("key store: only well formed 16 byte keys are stored", () =>
            {
                EndfieldBundleKeyStore.ReplaceKeys(new[]
                {
                    new KeyValuePair<uint, byte[]>(1, new byte[16]),
                    new KeyValuePair<uint, byte[]>(2, new byte[15]),
                    new KeyValuePair<uint, byte[]>(3, new byte[17]),
                    new KeyValuePair<uint, byte[]>(4, null),
                });

                Assert.True(EndfieldBundleKeyStore.TryGetKey(1, out var key), "16 byte key should be stored");
                Assert.NotNull(key, "stored key");
                Assert.False(EndfieldBundleKeyStore.TryGetKey(2, out _), "15 byte key must be ignored");
                Assert.False(EndfieldBundleKeyStore.TryGetKey(3, out _), "17 byte key must be ignored");
                Assert.False(EndfieldBundleKeyStore.TryGetKey(4, out _), "null key must be ignored");
                Assert.False(EndfieldBundleKeyStore.TryGetKey(5, out _), "unknown id must miss");
            });

            runner.Add("key store: replace clears stale entries and updates existing ones", () =>
            {
                EndfieldBundleKeyStore.ReplaceKeys(new[]
                {
                    new KeyValuePair<uint, byte[]>(10, Fill(0xAA)),
                });
                Assert.True(EndfieldBundleKeyStore.TryGetKey(10, out _), "initial key should be present");

                EndfieldBundleKeyStore.ReplaceKeys(new[]
                {
                    new KeyValuePair<uint, byte[]>(10, Fill(0xBB)),
                    new KeyValuePair<uint, byte[]>(11, Fill(0xCC)),
                });

                Assert.True(EndfieldBundleKeyStore.TryGetKey(10, out var updated), "key 10 should survive the replace");
                Assert.Equal((byte)0xBB, updated[0], "key 10 should be updated");
                Assert.True(EndfieldBundleKeyStore.TryGetKey(11, out _), "key 11 should be added");
                Assert.False(EndfieldBundleKeyStore.TryGetKey(9, out _), "unknown key stays missing");

                var snapshot = EndfieldBundleKeyStore.Snapshot();
                Assert.Equal(2, snapshot.Count, "snapshot count");
                snapshot[10][0] = 0x00;
                Assert.True(EndfieldBundleKeyStore.TryGetKey(10, out var afterMutation), "key 10 should exist");
                Assert.Equal((byte)0xBB, afterMutation[0], "snapshot must not expose the stored array");
            });

            runner.Add("key store: added keys are copied from the caller", () =>
            {
                var source = Fill(0x42);
                EndfieldBundleKeyStore.ReplaceKeys(new[] { new KeyValuePair<uint, byte[]>(20, source) });
                source[0] = 0x99;

                Assert.True(EndfieldBundleKeyStore.TryGetKey(20, out var stored), "key 20 should be stored");
                Assert.Equal((byte)0x42, stored[0], "the store must keep its own copy");
            });
        }

        private static byte[] Fill(byte value)
        {
            var data = new byte[16];
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = value;
            }

            return data;
        }
    }
}

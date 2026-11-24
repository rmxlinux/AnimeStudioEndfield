using System;
using System.IO;
using System.Runtime.InteropServices;
using AnimeStudio.PInvoke;

namespace ACLLibs
{
    public static class EndfieldACL
    {
        private const string DllName = "endfieldacl";

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeClip
        {
            public IntPtr Values;
            public int ValuesCount;
            public IntPtr Times;
            public int TimesCount;
            public int Error;
        }

        static EndfieldACL()
        {
            DllLoader.PreloadDll(DllName);
        }

        public static (float[] Values, float[] Times, int TrackCount) DecodeTransforms(byte[] data) => Decode(data, true);

        public static (float[] Values, float[] Times, int TrackCount) DecodeScalars(byte[] data) => Decode(data, false);

        private static (float[] Values, float[] Times, int TrackCount) Decode(byte[] data, bool transform)
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("ACL buffer is empty.", nameof(data));

            var clip = new NativeClip();
            try
            {
                var status = transform
                    ? EndfieldACLDecompressTransform(data, data.Length, ref clip)
                    : EndfieldACLDecompressScalar(data, data.Length, ref clip);
                if (status != 0 || clip.TimesCount <= 0 || clip.ValuesCount <= 0)
                    throw new InvalidDataException($"Endfield ACL decompression failed (error {status}).");

                var width = transform ? 10 : 1;
                if (clip.ValuesCount % (clip.TimesCount * width) != 0)
                    throw new InvalidDataException("Endfield ACL returned inconsistent track dimensions.");

                var values = new float[clip.ValuesCount];
                var times = new float[clip.TimesCount];
                Marshal.Copy(clip.Values, values, 0, values.Length);
                Marshal.Copy(clip.Times, times, 0, times.Length);
                return (values, times, values.Length / (times.Length * width));
            }
            finally
            {
                EndfieldACLDispose(ref clip);
            }
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int EndfieldACLDecompressTransform(byte[] data, int length, ref NativeClip clip);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int EndfieldACLDecompressScalar(byte[] data, int length, ref NativeClip clip);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void EndfieldACLDispose(ref NativeClip clip);
    }
}

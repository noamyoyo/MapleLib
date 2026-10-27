using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MapleLib.Helpers
{
    /// <summary>
    /// A read-only view of 32-bit straight-alpha BGRA pixels that the pixel encoders in <see cref="PngUtility"/>
    /// can lock, whether the pixels live in a <see cref="Bitmap"/> (GDI+, Windows) or in a plain byte array
    /// (no GDI+ needed, used on Linux). The encoders only read <see cref="BitmapData.Scan0"/> and
    /// <see cref="BitmapData.Stride"/>; <see cref="BitmapData"/> is just a data holder here.
    /// </summary>
    internal sealed class BgraSurface
    {
        private readonly Bitmap _bitmap;
        private readonly byte[] _pixels;
        private GCHandle _handle;

        public int Width { get; }
        public int Height { get; }

        public BgraSurface(Bitmap bitmap)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            _bitmap = bitmap;
            Width = bitmap.Width;
            Height = bitmap.Height;
        }

        /// <param name="bgra">Pixels as B, G, R, A bytes, rows tightly packed (stride = width * 4).</param>
        public BgraSurface(byte[] bgra, int width, int height)
        {
            ArgumentNullException.ThrowIfNull(bgra);
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
            if ((long)width * height * 4L != bgra.Length)
                throw new ArgumentException("The pixel buffer length does not match width * height * 4.", nameof(bgra));
            _pixels = bgra;
            Width = width;
            Height = height;
        }

        public BitmapData LockBits()
        {
            if (_bitmap != null)
                return _bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            _handle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
            return new BitmapData
            {
                Width = Width,
                Height = Height,
                Stride = Width * 4,
                PixelFormat = PixelFormat.Format32bppArgb,
                Scan0 = _handle.AddrOfPinnedObject()
            };
        }

        public void UnlockBits(BitmapData data)
        {
            if (_bitmap != null)
            {
                _bitmap.UnlockBits(data);
                return;
            }

            if (_handle.IsAllocated)
                _handle.Free();
        }
    }
}

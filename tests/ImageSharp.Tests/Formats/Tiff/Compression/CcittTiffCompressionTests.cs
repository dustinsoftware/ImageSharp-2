// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.

using System.IO;
using SixLabors.ImageSharp.Formats.Tiff.Compression.Compressors;
using SixLabors.ImageSharp.Formats.Tiff.Constants;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.Tests.Memory;
using Xunit;

namespace SixLabors.ImageSharp.Tests.Formats.Tiff.Compression
{
    [Trait("Format", "Tiff")]
    public class CcittTiffCompressionTests
    {
        private const int BitsPerPixel = 1;

        [Theory]
        [InlineData(TiffCompression.CcittGroup3Fax)]
        [InlineData(TiffCompression.CcittGroup4Fax)]
        [InlineData(TiffCompression.Ccitt1D)]
        public void CompressStrip_WithMoreRowsThanInitialized_ThrowsInvalidMemoryOperationException(TiffCompression compression)
        {
            const int Width = 1;
            const int InitializedRows = 1;
            const int StripRows = 64;
            const byte White = byte.MaxValue;
            const byte Black = 0;
            byte[] pixels = new byte[Width * StripRows];
            for (int y = 0; y < StripRows; y++)
            {
                pixels[y] = (y & 1) == 0 ? White : Black;
            }

            using var output = new MemoryStream();
            using TiffCcittCompressor compressor = CreateCompressor(compression, output, MemoryAllocator.Create(), Width);
            compressor.Initialize(InitializedRows);

            Assert.Throws<InvalidMemoryOperationException>(() => compressor.CompressStrip(pixels, StripRows));
        }

        [Theory]
        [InlineData(TiffCompression.CcittGroup3Fax)]
        [InlineData(TiffCompression.CcittGroup4Fax)]
        [InlineData(TiffCompression.Ccitt1D)]
        public void Initialize_WithEncodedBoundOverInt32_ThrowsWithoutAllocating(TiffCompression compression)
        {
            const int Width = 1 << 20;
            const int RowsPerStrip = 1 << 11;
            var allocator = new TestMemoryAllocator();
            allocator.EnableNonThreadSafeLogging();

            using var output = new MemoryStream();
            using TiffCcittCompressor compressor = CreateCompressor(compression, output, allocator, Width);

            InvalidMemoryOperationException exception = Assert.Throws<InvalidMemoryOperationException>(() => compressor.Initialize(RowsPerStrip));

            Assert.StartsWith("Attempted to allocate a buffer of length=", exception.Message);
            Assert.EndsWith($"that exceeded the limit {int.MaxValue}.", exception.Message);
            Assert.Empty(allocator.AllocationLog);
        }

        private static TiffCcittCompressor CreateCompressor(TiffCompression compression, Stream output, MemoryAllocator allocator, int width)
        {
            switch (compression)
            {
                case TiffCompression.CcittGroup4Fax:
                    return new T6BitCompressor(output, allocator, width, BitsPerPixel);
                case TiffCompression.Ccitt1D:
                    return new T4BitCompressor(output, allocator, width, BitsPerPixel, useModifiedHuffman: true);
                default:
                    return new T4BitCompressor(output, allocator, width, BitsPerPixel);
            }
        }
    }
}

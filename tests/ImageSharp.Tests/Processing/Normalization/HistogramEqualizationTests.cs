// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.

using System.IO;
using System.Numerics;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Normalization;
using SixLabors.ImageSharp.Tests.TestUtilities.ImageComparison;
using Xunit;

namespace SixLabors.ImageSharp.Tests.Processing.Normalization
{
    // ReSharper disable InconsistentNaming
    [Trait("Category", "Processors")]
    public class HistogramEqualizationTests
    {
        private static readonly ImageComparer ValidatorComparer = ImageComparer.TolerantPercentage(0.0456F);

        private const float LowerInRangeSample = 0.625F;

        private const float UpperInRangeSample = 0.75F;

        private const float OneThird = 1F / 3F;

        private static readonly float[] ExpectedWithOutOfRangeInTopBin = { 0F, 0F, OneThird, OneThird, 1F, 1F, 1F, 1F };

        private static readonly float[] ExpectedWithOutOfRangeInBottomBin = { 0.5F, 0.5F, 1F, 1F, 0F, 0F, 0F, 0F };

        [Theory]
        [InlineData(2F, true)]
        [InlineData(-1F, false)]
        [InlineData(float.PositiveInfinity, true)]
        [InlineData(float.NegativeInfinity, false)]
        [InlineData(float.NaN, false)]
        public void GlobalHistogramEqualization_ClampsDecodedFloatingPointSamples(float sample, bool isAboveRange)
        {
            byte[] data = BuildFloatTiff(LowerInRangeSample, LowerInRangeSample, UpperInRangeSample, UpperInRangeSample, sample, sample, sample, sample);

            using Image<HalfVector4> image = Image.Load<HalfVector4>(data);
            image.Mutate(x => x.HistogramEqualization());

            AssertEqualizedLuminance(image, isAboveRange ? ExpectedWithOutOfRangeInTopBin : ExpectedWithOutOfRangeInBottomBin);
        }

        [Theory]
        [InlineData(float.PositiveInfinity, UpperInRangeSample, true)]
        [InlineData(float.NaN, UpperInRangeSample, false)]
        [InlineData(float.PositiveInfinity, float.NegativeInfinity, false)]
        public void GlobalHistogramEqualization_ClampsDecodedRgbSamplesWithNonFiniteChannels(float red, float green, bool isAboveRange)
        {
            float[] samples =
            {
                LowerInRangeSample, LowerInRangeSample, LowerInRangeSample,
                LowerInRangeSample, LowerInRangeSample, LowerInRangeSample,
                UpperInRangeSample, UpperInRangeSample, UpperInRangeSample,
                UpperInRangeSample, UpperInRangeSample, UpperInRangeSample,
                red, green, UpperInRangeSample,
                red, green, UpperInRangeSample,
                red, green, UpperInRangeSample,
                red, green, UpperInRangeSample,
            };
            byte[] data = BuildRgbFloatTiff(samples);

            using Image<HalfVector4> image = Image.Load<HalfVector4>(data);
            image.Mutate(x => x.HistogramEqualization());

            AssertEqualizedLuminance(image, isAboveRange ? ExpectedWithOutOfRangeInTopBin : ExpectedWithOutOfRangeInBottomBin);
        }

        [Theory]
        [InlineData(256)]
        [InlineData(65536)]
        public void GlobalHistogramEqualization_WithDifferentLuminanceLevels(int luminanceLevels)
        {
            // Arrange
            byte[] pixels =
            {
                52,  55,  61,  59,  70,  61,  76,  61,
                62,  59,  55, 104,  94,  85,  59,  71,
                63,  65,  66, 113, 144, 104,  63,  72,
                64,  70,  70, 126, 154, 109,  71,  69,
                67,  73,  68, 106, 122,  88,  68,  68,
                68,  79,  60,  79,  77,  66,  58,  75,
                69,  85,  64,  58,  55,  61,  65,  83,
                70,  87,  69,  68,  65,  73,  78,  90
            };

            using (var image = new Image<Rgba32>(8, 8))
            {
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        byte luminance = pixels[(y * 8) + x];
                        image[x, y] = new Rgba32(luminance, luminance, luminance);
                    }
                }

                byte[] expected =
                {
                    0,    12,   53,   32,  146,   53,  174,   53,
                    57,   32,   12,  227,  219,  202,   32,  154,
                    65,   85,   93,  239,  251,  227,   65,  158,
                    73,  146,  146,  247,  255,  235,  154,  130,
                    97,  166,  117,  231,  243,  210,  117,  117,
                    117, 190,   36,  190,  178,   93,   20,  170,
                    130, 202,   73,   20,   12,   53,   85,  194,
                    146, 206,  130,  117,   85,  166,  182,  215
                };

                // Act
                image.Mutate(x => x.HistogramEqualization(new HistogramEqualizationOptions
                {
                    LuminanceLevels = luminanceLevels,
                    Method = HistogramEqualizationMethod.Global
                }));

                // Assert
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        Rgba32 actual = image[x, y];
                        Assert.Equal(expected[(y * 8) + x], actual.R);
                        Assert.Equal(expected[(y * 8) + x], actual.G);
                        Assert.Equal(expected[(y * 8) + x], actual.B);
                    }
                }
            }
        }

        [Theory]
        [WithFile(TestImages.Jpeg.Baseline.HistogramEqImage, PixelTypes.Rgba32)]
        public void GlobalHistogramEqualization_CompareToReferenceOutput<TPixel>(TestImageProvider<TPixel> provider)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            using (Image<TPixel> image = provider.GetImage())
            {
                var options = new HistogramEqualizationOptions
                {
                    Method = HistogramEqualizationMethod.Global,
                    LuminanceLevels = 256,
                };
                image.Mutate(x => x.HistogramEqualization(options));
                image.DebugSave(provider);
                image.CompareToReferenceOutput(ValidatorComparer, provider, extension: "png");
            }
        }

        [Theory]
        [WithFile(TestImages.Jpeg.Baseline.LowContrast, PixelTypes.Rgba32)]
        public void Adaptive_SlidingWindow_15Tiles_WithClipping<TPixel>(TestImageProvider<TPixel> provider)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            using (Image<TPixel> image = provider.GetImage())
            {
                var options = new HistogramEqualizationOptions
                {
                    Method = HistogramEqualizationMethod.AdaptiveSlidingWindow,
                    LuminanceLevels = 256,
                    ClipHistogram = true,
                    NumberOfTiles = 15
                };
                image.Mutate(x => x.HistogramEqualization(options));
                image.DebugSave(provider);
                image.CompareToReferenceOutput(ValidatorComparer, provider);
            }
        }

        [Theory]
        [WithFile(TestImages.Jpeg.Baseline.LowContrast, PixelTypes.Rgba32)]
        public void Adaptive_TileInterpolation_10Tiles_WithClipping<TPixel>(TestImageProvider<TPixel> provider)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            using (Image<TPixel> image = provider.GetImage())
            {
                var options = new HistogramEqualizationOptions
                {
                    Method = HistogramEqualizationMethod.AdaptiveTileInterpolation,
                    LuminanceLevels = 256,
                    ClipHistogram = true,
                    NumberOfTiles = 10
                };
                image.Mutate(x => x.HistogramEqualization(options));
                image.DebugSave(provider);
                image.CompareToReferenceOutput(ValidatorComparer, provider);
            }
        }

        /// <summary>
        /// This is regression test for a bug with the calculation of the y-start positions,
        /// where it could happen that one too much start position was calculated in some cases.
        /// See: https://github.com/SixLabors/ImageSharp/pull/984
        /// </summary>
        /// <typeparam name="TPixel">The pixel type of the image.</typeparam>
        /// <param name="provider">The test image provider.</param>
        [Theory]
        [WithTestPatternImages(110, 110, PixelTypes.Rgb24)]
        [WithTestPatternImages(170, 170, PixelTypes.Rgb24)]
        public void Issue984<TPixel>(TestImageProvider<TPixel> provider)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            using (Image<TPixel> image = provider.GetImage())
            {
                var options = new HistogramEqualizationOptions()
                {
                    Method = HistogramEqualizationMethod.AdaptiveTileInterpolation,
                    LuminanceLevels = 256,
                    ClipHistogram = true,
                    ClipLimit = 5,
                    NumberOfTiles = 10
                };
                image.Mutate(x => x.HistogramEqualization(options));
                image.DebugSave(provider);
                image.CompareToReferenceOutput(ValidatorComparer, provider);
            }
        }

        [Theory]
        [WithTestPatternImages(5120, 9234, PixelTypes.L16)]
        public unsafe void Issue1640<TPixel>(TestImageProvider<TPixel> provider)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            if (!TestEnvironment.Is64BitProcess)
            {
                return;
            }

            // https://github.com/SixLabors/ImageSharp/discussions/1640
            // Test using isolated memory to ensure clean buffers for reference
            provider.Configuration = Configuration.CreateDefaultInstance();
            var options = new HistogramEqualizationOptions
            {
                Method = HistogramEqualizationMethod.AdaptiveTileInterpolation,
                LuminanceLevels = 4096,
                ClipHistogram = false,
                ClipLimit = 350,
                NumberOfTiles = 8
            };

            using Image<TPixel> image = provider.GetImage();
            using Image<TPixel> referenceResult = image.Clone(ctx =>
            {
                ctx.HistogramEqualization(options);
                ctx.Resize(image.Width / 4, image.Height / 4, KnownResamplers.Bicubic);
            });

            using Image<TPixel> processed = image.Clone(ctx =>
            {
                ctx.HistogramEqualization(options);
                ctx.Resize(image.Width / 4, image.Height / 4, KnownResamplers.Bicubic);
            });

            ValidatorComparer.VerifySimilarity(referenceResult, processed);
        }

        private static void AssertEqualizedLuminance(Image<HalfVector4> image, float[] expected)
        {
            const int Precision = 3;

            Assert.Equal(expected.Length, image.Width);
            Assert.Equal(1, image.Height);
            for (int x = 0; x < expected.Length; x++)
            {
                Vector4 pixel = image[x, 0].ToVector4();
                Assert.Equal(expected[x], pixel.X, Precision);
                Assert.Equal(expected[x], pixel.Y, Precision);
                Assert.Equal(expected[x], pixel.Z, Precision);
            }
        }

        private static byte[] BuildFloatTiff(params float[] samples)
        {
            const int headerLength = 8;
            const int entryCount = 10;
            const int ifdLength = 2 + (entryCount * 12) + 4;
            uint pixelOffset = headerLength + ifdLength;
            int width = samples.Length;

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);

            writer.Write((byte)0x49);
            writer.Write((byte)0x49);
            writer.Write((ushort)42);
            writer.Write((uint)headerLength);
            writer.Write((ushort)entryCount);
            WriteEntry(writer, 256, 4, 1, (uint)width);
            WriteEntry(writer, 257, 4, 1, 1);
            WriteEntry(writer, 258, 3, 1, 32);
            WriteEntry(writer, 259, 3, 1, 1);
            WriteEntry(writer, 262, 3, 1, 1);
            WriteEntry(writer, 273, 4, 1, pixelOffset);
            WriteEntry(writer, 277, 3, 1, 1);
            WriteEntry(writer, 278, 4, 1, 1);
            WriteEntry(writer, 279, 4, 1, (uint)(width * sizeof(float)));
            WriteEntry(writer, 339, 3, 1, 3);
            writer.Write(0U);

            for (int i = 0; i < width; i++)
            {
                writer.Write(samples[i]);
            }

            return stream.ToArray();

            static void WriteEntry(BinaryWriter writer, ushort tag, ushort type, uint count, uint value)
            {
                writer.Write(tag);
                writer.Write(type);
                writer.Write(count);
                writer.Write(value);
            }
        }

        private static byte[] BuildRgbFloatTiff(float[] samples)
        {
            const int HeaderLength = 8;
            const int EntryCount = 10;
            const int IfdLength = 2 + (EntryCount * 12) + 4;
            const int ChannelCount = 3;
            const int PerChannelArrayLength = ChannelCount * sizeof(ushort);
            const uint BitsPerSampleOffset = HeaderLength + IfdLength;
            const uint SampleFormatOffset = BitsPerSampleOffset + PerChannelArrayLength;
            const uint PixelOffset = SampleFormatOffset + PerChannelArrayLength;
            const ushort BitsPerChannel = 32;
            const ushort FloatSampleFormat = 3;
            const ushort RgbPhotometricInterpretation = 2;
            int width = samples.Length / ChannelCount;

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);

            writer.Write((byte)0x49);
            writer.Write((byte)0x49);
            writer.Write((ushort)42);
            writer.Write((uint)HeaderLength);
            writer.Write((ushort)EntryCount);
            WriteEntry(writer, 256, 4, 1, (uint)width);
            WriteEntry(writer, 257, 4, 1, 1);
            WriteEntry(writer, 258, 3, ChannelCount, BitsPerSampleOffset);
            WriteEntry(writer, 259, 3, 1, 1);
            WriteEntry(writer, 262, 3, 1, RgbPhotometricInterpretation);
            WriteEntry(writer, 273, 4, 1, PixelOffset);
            WriteEntry(writer, 277, 3, 1, ChannelCount);
            WriteEntry(writer, 278, 4, 1, 1);
            WriteEntry(writer, 279, 4, 1, (uint)(samples.Length * sizeof(float)));
            WriteEntry(writer, 339, 3, ChannelCount, SampleFormatOffset);
            writer.Write(0U);

            for (int i = 0; i < ChannelCount; i++)
            {
                writer.Write(BitsPerChannel);
            }

            for (int i = 0; i < ChannelCount; i++)
            {
                writer.Write(FloatSampleFormat);
            }

            foreach (float sample in samples)
            {
                writer.Write(sample);
            }

            return stream.ToArray();

            static void WriteEntry(BinaryWriter writer, ushort tag, ushort type, uint count, uint value)
            {
                writer.Write(tag);
                writer.Write(type);
                writer.Write(count);
                writer.Write(value);
            }
        }
    }
}
